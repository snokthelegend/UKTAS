using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SysRandom = System.Random;
using UnityRandom = UnityEngine.Random;

namespace UltrakillTAS
{
    /// <summary>Snapshot of one live System.Random instance (weak reference plus its private state).</summary>
    public sealed class SystemRandomSnapshot
    {
        public WeakReference Instance;
        public object[] Values;
        public string[] FieldNames;
        public bool Valid;
    }

    /// <summary>
    /// Deterministic RNG control.
    ///
    /// UnityEngine.Random is backed by a native PRNG whose internal state is
    /// exposed through UnityEngine.Random.state. Harmony cannot detour the
    /// native Random.Range/value icalls, so this class controls the exact same
    /// generator through InitState()/state instead. Seeding at playback start and
    /// restoring Random.State in every savestate gives identical bullet spreads,
    /// coin bounces and AI rolls without touching game code.
    ///
    /// System.Random lives in mscorlib, is fully managed, and is patched:
    ///   - every 'new Random(seed)' can be redirected to a reproducible seed stream
    ///   - every instance is registered so its internal state can be saved/restored
    /// This covers game code that creates its own Random instances.
    /// </summary>
    public sealed class RngController
    {
        readonly Plugin _plugin;
        readonly GameBridge _bridge;
        Harmony _harmony;

        readonly List<WeakReference> _randoms = new List<WeakReference>();
        readonly List<SystemRandomSnapshot> _pendingRestore = new List<SystemRandomSnapshot>();
        int _seedStream;
        bool _forceSystemSeeds;
        bool _patchedSystemRandom;

        public int CurrentSeed { get; private set; }
        public bool SystemRandomPatched { get { return _patchedSystemRandom; } }
        public int TrackedSystemRandoms { get { return _randoms.Count; } }

        /// <summary>
        /// Position of the deterministic seed stream handed to new System.Random instances.
        /// Savestates capture it so randoms constructed after a load get the same seeds as
        /// they did after the original save.
        /// </summary>
        public int SystemSeedStream
        {
            get { return _seedStream; }
            set { _seedStream = value; }
        }

        public RngController(Plugin plugin, GameBridge bridge)
        {
            _plugin = plugin;
            _bridge = bridge;
        }

        public void Initialize(bool forceSystemSeeds)
        {
            _forceSystemSeeds = forceSystemSeeds;
            _seedStream = 0;
            CurrentSeed = 0;
            TryPatchSystemRandom();
        }

        /// <summary>Called once at boot and whenever the game creates a new System.Random.</summary>
        public void TryPatchSystemRandom()
        {
            if (_patchedSystemRandom)
                return;
            try
            {
                _harmony = new Harmony("com.tas.ultrakill.rng");
                MethodBase constructor = AccessTools.Constructor(typeof(SysRandom), new[] { typeof(int) });
                if (constructor == null)
                {
                    _plugin.LogWarning("System.Random(int) constructor was not found; System.Random determinism is unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(RngController).GetMethod("RandomSeedPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(RngController).GetMethod("RandomCtorPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null || postfix == null)
                    return;

                _harmony.Patch(constructor, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                _patchedSystemRandom = true;
                Instance = this;
                _plugin.LogInfo("System.Random constructor patched for deterministic seeding and savestates.");
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Could not patch System.Random: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static RngController Instance;

        // Set while CreateUntracked constructs the tool's own System.Random.
        [ThreadStatic] static bool s_untracked;

        /// <summary>
        /// A System.Random for the tool itself (the optimizer's mutation RNG). It keeps the given seed,
        /// does not advance <see cref="SystemSeedStream"/> and is not tracked, so savestates, optimizer
        /// masters and bakes never capture or rewind it, and creating it leaves the game's RNG untouched.
        /// </summary>
        public static SysRandom CreateUntracked(int seed)
        {
            s_untracked = true;
            try
            {
                return new SysRandom(seed);
            }
            finally
            {
                s_untracked = false;
            }
        }

        // Harmony prefix: runs before System.Random(int) stores its seed.
        static void RandomSeedPrefix(ref int __0)
        {
            RngController self = Instance;
            if (self == null || !self._forceSystemSeeds || s_untracked)
                return;
            __0 = self.NextSystemSeed();
        }

        // Harmony postfix: remember the instance so savestates can capture its state.
        static void RandomCtorPostfix(SysRandom __instance)
        {
            RngController self = Instance;
            if (self == null || __instance == null || s_untracked)
                return;
            self.Register(__instance);
        }

        void Register(SysRandom random)
        {
            for (int i = _randoms.Count - 1; i >= 0; i--)
            {
                object target = _randoms[i].Target;
                if (target == null)
                    _randoms.RemoveAt(i);
                else if (ReferenceEquals(target, random))
                    return;
            }
            if (_randoms.Count < 512)
                _randoms.Add(new WeakReference(random));
        }

        int NextSystemSeed()
        {
            // Deterministic LCG so every constructed Random gets a stable seed.
            _seedStream = unchecked(_seedStream * 1103515245 + 12345);
            return _seedStream;
        }

        public void Seed(int seed)
        {
            CurrentSeed = seed;
            _seedStream = seed;
            UnityRandom.InitState(seed);
            _plugin.LogInfo("RNG seeded with " + seed + " (UnityEngine.Random.InitState).");
        }

        /// <summary>Re-apply the current seed. Call at playback start for fresh runs.</summary>
        public void Apply()
        {
            UnityRandom.InitState(CurrentSeed);
            _seedStream = CurrentSeed;
        }

        public UnityRandom.State CaptureUnityState()
        {
            return UnityRandom.state;
        }

        public void RestoreUnityState(UnityRandom.State state)
        {
            UnityRandom.state = state;
        }

        /// <summary>Snapshot all live System.Random instances.</summary>
        public List<SystemRandomSnapshot> CaptureSystemRandoms()
        {
            return CaptureSystemRandoms(null);
        }

        /// <summary>
        /// Snapshot all live System.Random instances, reusing the snapshot at the same position in
        /// <paramref name="previous"/> when it is the same instance with an unchanged state (baking
        /// captures every tick and most instances are idle on most ticks). When nothing changed,
        /// <paramref name="previous"/> itself is returned. Snapshots are never modified after capture,
        /// so sharing them is safe.
        /// </summary>
        public List<SystemRandomSnapshot> CaptureSystemRandoms(List<SystemRandomSnapshot> previous)
        {
            List<SystemRandomSnapshot> snapshots = new List<SystemRandomSnapshot>(previous != null ? previous.Count : 4);
            bool allReused = previous != null;
            for (int i = _randoms.Count - 1; i >= 0; i--)
            {
                SysRandom random = _randoms[i].Target as SysRandom;
                if (random == null)
                {
                    _randoms.RemoveAt(i);
                    continue;
                }

                int at = snapshots.Count;
                SystemRandomSnapshot old = previous != null && at < previous.Count ? previous[at] : null;
                SystemRandomSnapshot snapshot;
                if (old != null && Unchanged(old, random))
                {
                    snapshot = old;
                }
                else
                {
                    snapshot = CaptureOne(random);
                    allReused = false;
                }
                if (snapshot != null)
                    snapshots.Add(snapshot);
            }
            if (allReused && snapshots.Count == previous.Count)
                return previous;
            return snapshots;
        }

        /// <summary>True when <paramref name="snapshot"/> was taken of <paramref name="random"/> and its state has not moved since.</summary>
        static bool Unchanged(SystemRandomSnapshot snapshot, SysRandom random)
        {
            if (snapshot == null || !snapshot.Valid || snapshot.Instance == null ||
                !ReferenceEquals(snapshot.Instance.Target, random))
                return false;
            ResolveStateFields();
            if (_stateFields == null || snapshot.Values == null || snapshot.Values.Length != _stateFields.Length)
                return false;
            for (int i = 0; i < _stateFields.Length; i++)
            {
                object live = _stateFields[i].GetValue(random);
                object saved = snapshot.Values[i];
                int[] liveArray = live as int[];
                int[] savedArray = saved as int[];
                if (liveArray != null || savedArray != null)
                {
                    if (liveArray == null || savedArray == null || liveArray.Length != savedArray.Length)
                        return false;
                    for (int k = 0; k < liveArray.Length; k++)
                    {
                        if (liveArray[k] != savedArray[k])
                            return false;
                    }
                }
                else if (!Equals(live, saved))
                {
                    return false;
                }
            }
            return true;
        }

        // Resolved once: the field layout of System.Random never changes at runtime,
        // and a capture may touch hundreds of instances on every step.
        static string[] _stateNames;
        static FieldInfo[] _stateFields;
        static bool _stateResolved;

        static void ResolveStateFields()
        {
            if (_stateResolved)
                return;
            _stateResolved = true;
            Type type = typeof(SysRandom);
            string[] candidateSets = { "_seedArray|_inext|_inextp", "SeedArray|inext|inextp" };
            for (int s = 0; s < candidateSets.Length; s++)
            {
                string[] names = candidateSets[s].Split('|');
                FieldInfo[] fields = new FieldInfo[names.Length];
                bool ok = true;
                for (int i = 0; i < names.Length && ok; i++)
                {
                    fields[i] = type.GetField(names[i], BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    ok = fields[i] != null;
                }
                if (ok)
                {
                    _stateNames = names;
                    _stateFields = fields;
                    return;
                }
            }
        }

        static SystemRandomSnapshot CaptureOne(SysRandom random)
        {
            ResolveStateFields();
            if (_stateFields == null)
                return null;
            object[] values = new object[_stateFields.Length];
            for (int i = 0; i < _stateFields.Length; i++)
            {
                object value = _stateFields[i].GetValue(random);
                int[] array = value as int[];
                values[i] = array != null ? (object)array.Clone() : value;
            }
            return new SystemRandomSnapshot
            {
                Instance = new WeakReference(random),
                FieldNames = _stateNames,
                Values = values,
                Valid = true
            };
        }

        /// <summary>Restore previously captured System.Random instances.</summary>
        public int RestoreSystemRandoms(List<SystemRandomSnapshot> snapshots)
        {
            if (snapshots == null)
                return 0;
            int restored = 0;
            ResolveStateFields();
            if (_stateFields == null)
                return 0;
            for (int s = 0; s < snapshots.Count; s++)
            {
                SystemRandomSnapshot snapshot = snapshots[s];
                if (snapshot == null || !snapshot.Valid || snapshot.Instance == null)
                    continue;
                SysRandom random = snapshot.Instance.Target as SysRandom;
                if (random == null)
                    continue;
                try
                {
                    // Already in the saved state (baked playback restores every tick): nothing to write.
                    if (Unchanged(snapshot, random))
                    {
                        restored++;
                        continue;
                    }
                    for (int i = 0; i < _stateFields.Length && i < snapshot.Values.Length; i++)
                    {
                        object value = snapshot.Values[i];
                        int[] array = value as int[];
                        _stateFields[i].SetValue(random, array != null ? array.Clone() : value);
                    }
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Failed to restore a System.Random instance: " + ex.Message);
                }
            }
            return restored;
        }

        /// <summary>Diagnostic summary for the GUI.</summary>
        public string Describe()
        {
            return "Unity RNG: seeded " + CurrentSeed + ", state captured per savestate\n" +
                   "System.Random: " + (_patchedSystemRandom ? "patched" : "not patched") +
                   ", tracked instances: " + _randoms.Count;
        }
    }
}
