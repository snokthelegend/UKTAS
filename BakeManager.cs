using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;

namespace UltrakillTAS
{
    /// <summary>What the baking system does on the current tick.</summary>
    public enum BakeBlockMode
    {
        /// <summary>Outside any bake block (or no script): pure live input.</summary>
        None,
        /// <summary>Inside a block with no valid .ukbake: run live and record every tick.</summary>
        Record,
        /// <summary>Inside a baked block, baking switched off: pure live input.</summary>
        Live,
        /// <summary>Inside a baked block with "Enable Baked Playback": live input, then the baked state is applied.</summary>
        Enforced,
        /// <summary>Inside a baked block with only the drift detector on: live input, compared to the bake.</summary>
        Verify
    }

    /// <summary>One projectile or coin in a <see cref="TickBakeFrame"/>.</summary>
    public struct BakeProjectile
    {
        public const byte FlagShot = 1;
        public const byte FlagSplittable = 2;

        /// <summary>(prefab type &lt;&lt; 20) | order of first appearance of that type inside the block.</summary>
        public int Key;
        /// <summary>Index into SaveStateManager.BakeProjectileTypeNames.</summary>
        public int Type;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        /// <summary>Coin timer / projectile lifetime or fuse.</summary>
        public float Timer;
        /// <summary>Coin hits / projectile ricochets.</summary>
        public int Hits;
        public byte Flags;
    }

    /// <summary>One level enemy in a <see cref="TickBakeFrame"/>.</summary>
    public struct BakeEnemy
    {
        public const byte FlagDead = 1;
        public const byte FlagAgent = 2;
        public const byte FlagAnimator = 4;

        /// <summary>EntityRegistry level ID (stable across runs of the same scene).</summary>
        public int Key;
        /// <summary>EnemyIdentifier.enemyType as an integer.</summary>
        public int Type;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Health;
        public byte Flags;
        public Vector3 NavDestination;
        public Vector3 NavVelocity;
        /// <summary>Animator layer 0 fullPathHash and normalizedTime.</summary>
        public int StateHash;
        public float NormalizedTime;
    }

    /// <summary>
    /// The baked state at the end of one simulation tick: the scalar values, plus ranges into the
    /// block's flat decoded arrays (<see cref="BakeBlockData.Projectiles"/>, ...). Every frame of a
    /// block is decoded once (<see cref="BakeBlockData.Frames"/>), so playback indexes by tick and
    /// never parses bytes per tick.
    /// </summary>
    public struct TickBakeFrame
    {
        public const byte MoveGrounded = 1;
        public const byte MoveSliding = 2;
        public const byte MoveJumping = 4;
        public const byte MoveDashing = 8;

        public int Tick;
        /// <summary>The engine's virtual elapsed time after the tick.</summary>
        public double Time;
        /// <summary>UnityEngine.Random.state, the raw 16 bytes as four ints.</summary>
        public int Rng0, Rng1, Rng2, Rng3;
        /// <summary>
        /// RngController.SystemSeedStream after the tick (format 2 and later). False for a format 1
        /// file, which only carries UnityEngine.Random.
        /// </summary>
        public bool HasSystemSeedStream;
        public int SystemSeedStream;

        public bool HasPlayer;
        public Vector3 PlayerPosition;
        public Vector3 PlayerVelocity;
        /// <summary>Script convention: positive pitch = looking down.</summary>
        public float Pitch;
        public float Yaw;
        public float Health;
        public float HardDamage;
        /// <summary>NewMovement.boostCharge (the dash meter).</summary>
        public float Stamina;
        public byte MoveFlags;
        public int WeaponSlot;
        public int WeaponVariation;
        /// <summary>Range in <see cref="BakeBlockData.Charges"/>.</summary>
        public int ChargeStart, ChargeCount;

        /// <summary>Range in <see cref="BakeBlockData.Projectiles"/>.</summary>
        public int ProjectileStart, ProjectileCount;
        /// <summary>
        /// One bit per projectile type (bit 31 = type 31 and above) whose set of keys differs from
        /// the previous frame: a spawn or destroy happened in the recording, so the live objects of
        /// that type are scanned again on this tick.
        /// </summary>
        public int ProjectileChangeMask;
        /// <summary>Range in <see cref="BakeBlockData.Enemies"/>.</summary>
        public int EnemyStart, EnemyCount;
        /// <summary>Range in <see cref="BakeBlockData.DoorFlags"/> (bit 0 open, bit 1 locked).</summary>
        public int DoorStart, DoorCount;
        /// <summary>Byte offset in <see cref="BakeBlockData.RoomBits"/> (activeSelf of every streamed room, 8 per byte).</summary>
        public int RoomStart, RoomCount;
    }

    /// <summary>
    /// One .ukbake file. Loaded (decompressed) once; then <see cref="BakeManager"/> decodes every
    /// frame once into the typed arrays below and drops the raw bytes.
    /// </summary>
    public sealed class BakeBlockData
    {
        public string Path = string.Empty;
        public string Name = string.Empty;
        public string Hash = string.Empty;
        public int StartTick;
        public int TickCount;
        /// <summary>Format version the frames were written with (1 = no System.Random data).</summary>
        public int Version = BakeManager.FormatVersion;
        /// <summary>
        /// The live System.Random states after each tick (RngController.CaptureSystemRandoms, the
        /// same snapshots a savestate takes). Only a bake recorded in this session has them: they
        /// hold weak references to the live instances, so they are not written to the file. Null
        /// for a bake loaded from disk.
        /// </summary>
        public List<SystemRandomSnapshot>[] SystemRandoms;
        /// <summary>Byte offset of each tick's frame inside <see cref="Data"/>.</summary>
        public int[] Offsets = new int[0];
        public byte[] Data = new byte[0];
        public int DataLength;

        /// <summary>True once <see cref="Frames"/> and the flat arrays hold every frame.</summary>
        public bool Decoded;
        public TickBakeFrame[] Frames = new TickBakeFrame[0];
        public float[] Charges = new float[0];
        public BakeProjectile[] Projectiles = new BakeProjectile[0];
        public BakeEnemy[] Enemies = new BakeEnemy[0];
        public byte[] DoorFlags = new byte[0];
        public byte[] RoomBits = new byte[0];
    }

    /// <summary>
    /// Optional TAS baking (bake_start [name] ... bake_end in the frames section).
    ///
    /// Record: the first run of a block (or a run after any edit above or inside it changes its
    /// checksum) plays the real inputs and writes the world state at the end of every tick into a
    /// pooled memory buffer; at the block's last tick the buffer goes to
    /// "&lt;script&gt;-&lt;block&gt;.ukbake" next to the script (GZip, one file per block).
    ///
    /// Enforced playback ("Enable Baked Playback"): the real inputs still run every tick, so
    /// animations, sounds and hitstops happen naturally, and at the end of the tick the baked
    /// state is written over the world (player, projectiles, enemies, RNG, doors and rooms).
    ///
    /// Hot path (enforced and verify): a block's frames are decoded once into typed arrays; live
    /// projectiles are kept in a cached set keyed by the recorded key and rescanned (one
    /// FindObjectsOfType per type) only on ticks where the recording spawned or destroyed one of
    /// that type, or where the bake names a key this run has not seen yet; game fields go through
    /// compiled accessors (no FieldInfo.GetValue boxing); every value is compared before it is
    /// written, and NavMesh agents are only warped / re-pathed when their destination changed or
    /// a direct move cannot put them back. Every write counts as a correction (per tick and per
    /// block, shown in the GUI and logged at the block's end).
    ///
    /// What enforced playback guarantees: at every tick boundary inside the block the player and
    /// every tracked entity (RNG, enemies, projectiles, doors, rooms) hold the recorded values
    /// (within the advanced correction threshold, 0 by default = float-noise epsilon). PhysX
    /// contacts, solver caches and other engine-internal state are not in the file, so this is
    /// not a bit-exact physics replay.
    ///
    /// Verify ("Verify Raw Playback Against Bake"): pure live playback, with the player position
    /// and every coin position compared to the bake each tick; the first divergence per block is
    /// reported once (toast + log) with the tick and the distance.
    ///
    /// Skip ("Skip Baked Blocks to End"): when the next tick is the start of a validly baked block,
    /// the last baked frame is applied and the cursor jumps to the first tick after bake_end.
    ///
    /// Sampling point: the end of the rendered frame that ran the tick (TimeController.EndFrame,
    /// called from the frame-end hook), the same point the trajectory recorder samples. Record and
    /// apply use that one point, so the next frame's physics step starts from the baked state.
    ///
    /// A script without bake markers never gets past the first check in OnBeginFrame/OnEndFrame.
    /// </summary>
    public sealed class BakeManager
    {
        public const uint Magic = 0x4B424B55; // "UKBK"
        /// <summary>
        /// 2: every frame also carries RngController.SystemSeedStream after the Unity RNG state.
        /// 1 is still read (Unity RNG only; a warning is logged when such a bake is loaded).
        /// </summary>
        public const int FormatVersion = 2;
        const int MinFormatVersion = 1;
        /// <summary>First format version whose frames carry the System.Random seed stream.</summary>
        const int SystemRngFormatVersion = 2;
        public const string Extension = ".ukbake";

        public const string StatusLive = "[LIVE INPUT]";
        public const string StatusSynced = "[BAKED - SYNCED - NOT LEGIT]";
        public const string StatusRecording = "[RECORDING BAKE]";
        public const string StatusVerify = "[VERIFY - LIVE INPUT]";
        public const string StatusDrift = "[VERIFY - DRIFT DETECTED]";
        public const string StatusSkipped = "[BAKED - SKIPPED TO END - NOT LEGIT]";

        /// <summary>Distance (world units) above which a live value counts as drifted from the bake.</summary>
        const float VerifyTolerance = 0.0001f;
        /// <summary>Squared distance under which an enforced write is skipped (already equal).</summary>
        const float ApplyEpsilonSq = 1e-10f;
        const float ApplyEpsilon = 1e-6f;
        /// <summary>Squared distance between the live and the baked NavMesh destination that forces a re-path.</summary>
        const float NavDestinationEpsilonSq = 1e-4f;
        /// <summary>Squared drift above which an agent is warped and re-pathed instead of moved directly.</summary>
        const float NavDirectMoveMaxSq = 1f;

        static readonly string[] EnemyTypeNames = { "enemyType" };
        static readonly string[] EnemyHealthNames = { "health", "hp", "currentHealth" };
        static readonly string[] EnemyDeadNames = { "dead", "isDead" };
        static readonly string[] ProjectileTimerNames = { "timeToDelete", "life", "lifetime", "lifeTime", "timer", "fuse" };
        static readonly string[] ProjectileHitNames = { "ricochets", "ricochet", "bounceCount", "bounces", "hitTimes", "hits", "hitAmount" };

        readonly Plugin _plugin;
        readonly GameBridge _bridge;

        public ConfigEntry<bool> EnableBakedPlayback;
        public ConfigEntry<bool> VerifyRawPlayback;
        public ConfigEntry<bool> SkipBakedBlocks;
        /// <summary>Advanced: drift (world units) tolerated before a position/velocity is corrected. 0 = always snap.</summary>
        public ConfigEntry<float> CorrectionThreshold;

        // Script the per-block state below belongs to.
        TasScript _script;
        string _scriptPath = string.Empty;
        // Valid bakes in memory, by block file name; each carries its hash.
        readonly Dictionary<string, BakeBlockData> _loaded = new Dictionary<string, BakeBlockData>(StringComparer.OrdinalIgnoreCase);
        // Hashes already looked up on disk without a valid file (no File.Exists per frame).
        readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);

        // Frame tracking: the tick before the frame ran, to detect exactly one executed tick.
        bool _engaged;
        bool _tickBeforeValid;
        int _tickBefore;
        int _lastTick = int.MinValue;
        EngineMode _lastEngineMode = EngineMode.Idle;

        // The block the last executed tick was in.
        TasBakeBlock _active;
        BakeBlockData _activeData;
        BakeBlockMode _mode = BakeBlockMode.None;
        bool _recording;
        string _recordPath = string.Empty;
        // One bit per projectile type whose keys were followed from the block's first tick (the
        // keys of one type do not depend on any other type).
        int _projValidMask;
        bool _driftReported;

        // Recording: one pooled buffer, reused for every block.
        readonly MemoryStream _recBuffer = new MemoryStream(1 << 20);
        readonly BinaryWriter _recWriter;
        readonly List<int> _recOffsets = new List<int>(4096);
        // System.Random snapshots per recorded tick, parallel to _recOffsets (unchanged ticks share one list).
        readonly List<List<SystemRandomSnapshot>> _recSystemRandoms = new List<List<SystemRandomSnapshot>>(4096);

        // Enforced playback: the per-tick correction threshold (read once per applied tick).
        float _thresholdSq = ApplyEpsilonSq;

        // Correction counters: every value written because the live game differed from the bake.
        // Per tick (by category), folded into the block totals by EndCorrectionTick.
        int _tickRng, _tickPlayer, _tickEnemies, _tickNavRepaths, _tickProjectiles, _tickDoors;
        bool _corrLogged;
        int _corrLastTick;
        long _corrBlock;
        int _corrTicksApplied;
        int _corrTicksCorrected;
        int _corrMaxTick;
        int _corrMaxAt = -1;
        int _corrRng, _corrPlayer, _corrEnemies, _corrNavRepaths, _corrProjectiles, _corrDoors;
        int _projRescans;
        string _corrBlockName = string.Empty;
        int _corrVersion;
        int _corrTextVersion = -1;
        string _corrText = string.Empty;
        readonly StringBuilder _corrBuilder = new StringBuilder(160);

        public BakeManager(Plugin plugin, GameBridge bridge)
        {
            _plugin = plugin;
            _bridge = bridge;
            _recWriter = new BinaryWriter(_recBuffer);
            ConfigFile config = plugin.ConfigFile;
            const string general = "1. General";
            EnableBakedPlayback = config.Bind(general, "Enable Baked Playback (.ukbake)", false,
                "Inside a bake block with a valid .ukbake, write the baked world state over the game after every tick (inputs still run).");
            VerifyRawPlayback = config.Bind(general, "Verify Raw Playback Against Bake", false,
                "Inside a baked block played live, compare the player and coin positions to the bake every tick and report the first drift.");
            SkipBakedBlocks = config.Bind(general, "Skip Baked Blocks to End", false,
                "When a bake block has a valid .ukbake, jump straight to its last baked frame and continue live after bake_end.");
            CorrectionThreshold = config.Bind("6. Advanced", "Baked Playback Correction Threshold", 0f,
                "ADVANCED. Enforced baked playback only: live-vs-baked difference (world units for positions, units/s for " +
                "velocities) that is left uncorrected. 0 (default) snaps every difference above float noise back to the " +
                "bake on every tick. Any value above 0 lets the game drift from the .ukbake by up to that amount.");
            StatusText = StatusLive;
            DetailText = string.Empty;
            s_current = this;
        }

        /// <summary>"[LIVE INPUT]", "[BAKED - SYNCED - NOT LEGIT]", "[RECORDING BAKE]", ... for the current tick.</summary>
        public string StatusText { get; private set; }
        /// <summary>The active block and what it is doing (rebuilt only when a block starts or ends).</summary>
        public string DetailText { get; private set; }
        public BakeBlockMode Mode { get { return _mode; } }

        /// <summary>Values written by the last enforced tick because the live game differed from the bake.</summary>
        public int CorrectionsLastTick { get { return _corrLastTick; } }
        /// <summary>Values written in the current (or last) enforced block.</summary>
        public long CorrectionsThisBlock { get { return _corrBlock; } }

        /// <summary>
        /// The desync-correction counters as one line for the GUI ("" before any enforced tick).
        /// Rebuilt only when read after a change, so ticks that run while the menu is closed allocate nothing.
        /// </summary>
        public string CorrectionText
        {
            get
            {
                if (_corrTextVersion == _corrVersion)
                    return _corrText;
                _corrTextVersion = _corrVersion;
                if (_corrTicksApplied == 0)
                {
                    _corrText = string.Empty;
                    return _corrText;
                }
                StringBuilder sb = _corrBuilder;
                sb.Length = 0;
                sb.Append("Desync corrections ('").Append(_corrBlockName).Append("'): last tick ").Append(_corrLastTick)
                  .Append(", block ").Append(_corrBlock).Append(" on ").Append(_corrTicksCorrected).Append('/')
                  .Append(_corrTicksApplied).Append(" ticks");
                if (_corrMaxAt >= 0)
                    sb.Append(", max ").Append(_corrMaxTick).Append(" at tick ").Append(_corrMaxAt);
                _corrText = sb.ToString();
                return _corrText;
            }
        }

        // ------------------------------------------------------------------
        // Submission guard (run legitimacy)
        // ------------------------------------------------------------------
        //
        // Baked playback is NOT legitimate: it replays stored state instead of pure inputs.
        // A run is submittable only when it used real inputs and no baked information at all.
        // The tool has no submission / leaderboard code. Any future submit, export-as-valid or
        // "mark run valid" path MUST call CheckRunSubmittable first and refuse when it returns false.
        // Verify-only playback (baked playback off, drift detector on) applies no state and stays allowed.

        public const string NotLegitWarning =
            "WARNING: BAKED PLAYBACK IS NOT LEGITIMATE. It replays stored state instead of pure inputs. " +
            "Runs using it are not valid TAS runs.";

        static BakeManager s_current;
        // Per-run latches, cleared only when a fresh run starts from tick 0 (BeginRun).
        static bool s_runAppliedBakedState;
        static bool s_runRecordedBake;
        static bool s_runBakedPlaybackWasOn;

        /// <summary>True while "Enable Baked Playback" is switched on.</summary>
        public static bool BakedPlaybackEnabled
        {
            get { return s_current != null && s_current.EnableBakedPlayback != null && s_current.EnableBakedPlayback.Value; }
        }

        /// <summary>Baked values were written into the game during this run (enforced playback or skip-to-end).</summary>
        public static bool RunAppliedBakedState { get { return s_runAppliedBakedState; } }

        /// <summary>
        /// False when baked playback is on, was on while this run played a bake block, any baked state
        /// was applied in this run, or this run recorded a bake. Only a pure real-input run is submittable.
        /// </summary>
        public static bool RunIsSubmittable
        {
            get { return !BakedPlaybackEnabled && !s_runAppliedBakedState && !s_runRecordedBake && !s_runBakedPlaybackWasOn; }
        }

        /// <summary>Why the run is not submittable ("" when it is).</summary>
        public static string NotSubmittableReason
        {
            get
            {
                if (s_runAppliedBakedState)
                    return "baked state was applied during this run";
                if (BakedPlaybackEnabled)
                    return "Enable Baked Playback is ON";
                if (s_runBakedPlaybackWasOn)
                    return "Enable Baked Playback was ON during this run";
                if (s_runRecordedBake)
                    return "this run recorded a bake (baking was active)";
                return string.Empty;
            }
        }

        /// <summary>
        /// The check every submit / export-as-valid / mark-valid path must call. Returns false (and logs
        /// and toasts the reason) when the run is not submittable. <paramref name="action"/> names the
        /// attempted action for the message, e.g. "Submit run".
        /// </summary>
        public static bool CheckRunSubmittable(string action, out string message)
        {
            if (RunIsSubmittable)
            {
                message = string.Empty;
                return true;
            }
            message = (string.IsNullOrEmpty(action) ? "Run" : action) + " blocked: RUN IS NOT SUBMITTABLE (" +
                      NotSubmittableReason + "). " + NotLegitWarning;
            if (s_current != null)
            {
                s_current._plugin.LogWarning(message);
                s_current._plugin.Toast(message, true);
            }
            return false;
        }

        /// <summary>A fresh run starts from tick 0: clear the per-run latches.</summary>
        public static void BeginRun()
        {
            s_runAppliedBakedState = false;
            s_runRecordedBake = false;
            s_runBakedPlaybackWasOn = false;
        }

        // ------------------------------------------------------------------
        // Frame hooks (TimeController)
        // ------------------------------------------------------------------

        /// <summary>Start of a clock frame, before the engine may run a tick.</summary>
        public void OnBeginFrame()
        {
            TasEngine engine = _plugin.Engine;
            TasScript script = engine != null ? engine.Script : null;
            _engaged = script != null && script.HasBakeBlocks;
            if (!_engaged)
                return;
            _tickBefore = engine.CurrentTick;
            _tickBeforeValid = true;
        }

        /// <summary>End of a rendered frame, after the game ran the tick (if any).</summary>
        public void OnEndFrame()
        {
            // Not only the flag from OnBeginFrame: the engine skips the clock's BeginFrame on the
            // frames it waits on the game, and a skip at tick 0 is decided on such a frame.
            TasEngine engine = _plugin.Engine;
            TasScript script = engine != null ? engine.Script : null;
            if (script == null || !script.HasBakeBlocks)
            {
                if (_engaged || _active != null)
                {
                    _engaged = false;
                    ResetRun();
                }
                _tickBeforeValid = false;
                return;
            }
            _engaged = true;
            if (!ReferenceEquals(script, _script) || !string.Equals(engine.ScriptPath, _scriptPath, StringComparison.Ordinal))
                SetScript(script, engine.ScriptPath);

            EngineMode mode = engine.Mode;
            if (mode != _lastEngineMode)
            {
                // A new Play may follow a bake copied in by hand: look on disk again.
                if (_lastEngineMode == EngineMode.Idle || mode == EngineMode.Idle)
                    _missing.Clear();
                _lastEngineMode = mode;
            }
            bool scriptMode = mode == EngineMode.Playing || mode == EngineMode.Paused;
            if (!scriptMode)
            {
                ResetRun();
                _tickBeforeValid = false;
                return;
            }
            // Submission guard: baked playback switched on at any point of a run taints the run.
            if (EnableBakedPlayback.Value)
                s_runBakedPlaybackWasOn = true;

            int now = engine.CurrentTick;
            if (_tickBeforeValid && now == _tickBefore + 1)
                OnTickExecuted(_tickBefore, engine, script);
            else if (_tickBeforeValid && now != _tickBefore)
                BreakContinuity();
            _tickBeforeValid = false;

            if (SkipBakedBlocks.Value && !engine.Finished && !engine.StartupPending && !engine.WaitingForControl)
                TrySkip(engine, script);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        void SetScript(TasScript script, string path)
        {
            ResetRun();
            _script = script;
            _scriptPath = path ?? string.Empty;
            _loaded.Clear();
            _missing.Clear();
            _lastEngineMode = EngineMode.Idle;
        }

        /// <summary>Forget the active block (an unfinished recording is dropped: a partial bake is never written).</summary>
        void ResetRun()
        {
            if (_recording)
                _plugin.LogInfo("Bake recording of block '" + (_active != null ? _active.FileName : "?") +
                                "' dropped: playback left the block before bake_end.");
            if (_active != null)
                LogCorrections(_active, false);
            _recording = false;
            _recSystemRandoms.Clear();
            _active = null;
            _activeData = null;
            _mode = BakeBlockMode.None;
            _lastTick = int.MinValue;
            if (!ReferenceEquals(StatusText, StatusLive))
            {
                StatusText = StatusLive;
                DetailText = string.Empty;
            }
        }

        /// <summary>The cursor moved by something other than one tick (rewind, load, norefresh, skip).</summary>
        void BreakContinuity()
        {
            ResetRun();
        }

        void OnTickExecuted(int tick, TasEngine engine, TasScript script)
        {
            if (tick != _lastTick + 1 && _active != null)
                ResetRun();
            _lastTick = tick;

            if (_active == null || tick < _active.StartTick || tick >= _active.EndTick)
            {
                if (_active != null)
                    ResetRun();
                _lastTick = tick;
                if (!EnterBlockAt(tick, script))
                {
                    if (!ReferenceEquals(StatusText, StatusLive))
                    {
                        StatusText = StatusLive;
                        DetailText = string.Empty;
                    }
                    return;
                }
            }

            TasBakeBlock block = _active;
            int index = tick - block.StartTick;
            try
            {
                // In the record / enforced / verify branches the player is resolved first: a changed
                // player (scene load) invalidates the entity caches before anything reads them.
                if (_recording)
                {
                    EnsurePlayer();
                    RecordTick(tick, engine.CurrentTime);
                }
                else if (_activeData != null)
                {
                    BakeBlockMode mode = EnableBakedPlayback.Value
                        ? BakeBlockMode.Enforced
                        : VerifyRawPlayback.Value ? BakeBlockMode.Verify : BakeBlockMode.Live;
                    SetMode(mode);
                    if (mode != BakeBlockMode.Live)
                        EnsurePlayer();
                    // The index is always in range for a validated bake (its tick count equals the block's).
                    if (index >= _activeData.TickCount)
                        _projValidMask = 0;
                    else if (mode == BakeBlockMode.Enforced)
                    {
                        TrackProjectiles(_activeData, index, 0);
                        BeginCorrectionTick();
                        ApplyFrame(_activeData, index, true);
                        EndCorrectionTick(tick);
                    }
                    else if (mode == BakeBlockMode.Verify)
                    {
                        // The drift detector also reports a coin that exists live but not in the
                        // bake, so the coin type is rescanned every tick until the first drift
                        // (the same list the full per-tick scan produced). Other types follow the bake.
                        TrackProjectiles(_activeData, index, _driftReported ? 0 : CoinMask());
                        VerifyFrame(_activeData, index, block, tick);
                    }
                    else
                    {
                        // Live ticks do not follow projectile keys, so a later switch to enforced
                        // playback inside this block leaves projectiles alone.
                        _projValidMask = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Bake: tick " + tick + " of block '" + block.FileName + "' failed: " + ex.Message);
            }

            if (tick == block.EndTick - 1)
                FinishBlock();
        }

        /// <summary>Start of a block at <paramref name="tick"/> (its first tick, or mid-block after a jump).</summary>
        bool EnterBlockAt(int tick, TasScript script)
        {
            IList<TasBakeBlock> blocks = script.BakeBlocks;
            TasBakeBlock found = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (tick >= blocks[i].StartTick && tick < blocks[i].EndTick)
                {
                    found = blocks[i];
                    break;
                }
            }
            if (found == null)
                return false;

            _active = found;
            _driftReported = false;
            ResetEntityCaches();
            ResetCorrections(found);
            // Projectile keys count first appearances from the block's first tick.
            _projValidMask = tick == found.StartTick ? -1 : 0;
            _activeData = GetValidBake(found);
            if (_activeData != null)
            {
                // The mode is decided per tick: baked playback may be switched at any time.
                DetailText = "block '" + found.FileName + "' (ticks " + found.StartTick + "-" + (found.EndTick - 1) +
                             "): valid bake loaded";
                // A loaded bake was silent in the log, so "the bake is not used" could not be told
                // apart from "baked playback is off". Logged once per block entry, not per tick.
                _plugin.LogInfo("Bake: block '" + found.FileName + "' entered at tick " + tick + " with a valid bake (" +
                                Path.GetFileName(_activeData.Path) + ", " + _activeData.TickCount + " ticks); " +
                                (EnableBakedPlayback.Value
                                    ? "Enable Baked Playback is ON: baked state is applied every tick."
                                    : "Enable Baked Playback is OFF: the block plays live input only.") +
                                (tick != found.StartTick ? " Entered mid-block: projectiles are not followed." : string.Empty));
                return true;
            }

            if (tick != found.StartTick)
            {
                SetMode(BakeBlockMode.Live);
                DetailText = "block '" + found.FileName + "': entered mid-block with no valid bake; not recorded";
                return true;
            }
            _recordPath = SidecarPath(_scriptPath, found);
            if (_recordPath.Length == 0)
            {
                SetMode(BakeBlockMode.Live);
                DetailText = "block '" + found.FileName + "': save the script first; an unsaved script cannot be baked";
                _plugin.LogWarning("Bake: block '" + found.FileName + "' is not recorded because the script has no file path.");
                return true;
            }
            _recBuffer.SetLength(0);
            _recOffsets.Clear();
            _recSystemRandoms.Clear();
            _recording = true;
            // Submission guard: the .ukbake may be saved, but a run that is baking is not submitted as legit.
            s_runRecordedBake = true;
            SetMode(BakeBlockMode.Record);
            DetailText = "block '" + found.FileName + "': recording " + found.TickCount + " ticks";
            _plugin.LogInfo("Bake: recording block '" + found.FileName + "' (ticks " + found.StartTick + "-" +
                            (found.EndTick - 1) + ").");
            return true;
        }

        void FinishBlock()
        {
            TasBakeBlock block = _active;
            if (_recording)
            {
                _recording = false;
                if (_recOffsets.Count == block.TickCount)
                {
                    BakeBlockData data = new BakeBlockData
                    {
                        Path = _recordPath,
                        Name = block.FileName,
                        Hash = block.Hash,
                        StartTick = block.StartTick,
                        TickCount = block.TickCount,
                        Offsets = _recOffsets.ToArray(),
                        DataLength = (int)_recBuffer.Length,
                        Version = FormatVersion,
                        SystemRandoms = _recSystemRandoms.Count == block.TickCount ? _recSystemRandoms.ToArray() : null
                    };
                    data.Data = new byte[data.DataLength];
                    Buffer.BlockCopy(_recBuffer.GetBuffer(), 0, data.Data, 0, data.DataLength);
                    string error;
                    if (Save(data, out error))
                    {
                        _loaded[block.FileName] = data;
                        _missing.Remove(block.Hash);
                        _plugin.Toast("Baked block '" + block.FileName + "' (" + block.TickCount + " ticks) to " +
                                      Path.GetFileName(_recordPath) + ".", false);
                    }
                    else
                    {
                        _plugin.Toast("Bake write failed for block '" + block.FileName + "': " + error, true);
                    }
                }
                else
                {
                    _plugin.LogWarning("Bake: block '" + block.FileName + "' recorded " + _recOffsets.Count + " of " +
                                       block.TickCount + " ticks; nothing written.");
                }
                // Do not keep a block-sized buffer around between blocks. Empty it first:
                // MemoryStream.Capacity below the current Length throws, which escaped the
                // frame-end hook and skipped the rest of TimeController.EndFrame for that frame.
                _recBuffer.SetLength(0);
                _recSystemRandoms.Clear();
                if (_recBuffer.Capacity > (8 << 20))
                    _recBuffer.Capacity = 1 << 20;
            }
            LogCorrections(block, true);
            _active = null;
            _activeData = null;
            SetMode(BakeBlockMode.None);
            DetailText = string.Empty;
        }

        void SetMode(BakeBlockMode mode)
        {
            if (mode == _mode)
                return;
            _mode = mode;
            switch (mode)
            {
                case BakeBlockMode.Record: StatusText = StatusRecording; break;
                case BakeBlockMode.Enforced: StatusText = StatusSynced; break;
                case BakeBlockMode.Verify: StatusText = _driftReported ? StatusDrift : StatusVerify; break;
                default: StatusText = StatusLive; break;
            }
        }

        /// <summary>The valid in-memory bake of a block, loading (and decompressing) its file at most once.</summary>
        BakeBlockData GetValidBake(TasBakeBlock block)
        {
            BakeBlockData data;
            string reason;
            if (_loaded.TryGetValue(block.FileName, out data))
            {
                if (string.Equals(data.Hash, block.Hash, StringComparison.Ordinal) && data.TickCount == block.TickCount)
                {
                    // A bake recorded in this session is decoded on its first playback.
                    if (Decode(data, out reason))
                        return data;
                    _plugin.LogWarning("Bake: block '" + block.FileName + "' could not be decoded (" + reason +
                                       "); the block will be re-recorded.");
                    _loaded.Remove(block.FileName);
                    _missing.Add(block.Hash);
                    return null;
                }
                _loaded.Remove(block.FileName);
            }
            if (_missing.Contains(block.Hash))
                return null;
            string path = SidecarPath(_scriptPath, block);
            if (path.Length == 0)
            {
                _missing.Add(block.Hash);
                return null;
            }
            data = Load(path, block.Hash, out reason);
            if (data == null || data.TickCount != block.TickCount || !Decode(data, out reason))
            {
                if (data != null && data.TickCount != block.TickCount)
                    reason = "tick count " + data.TickCount + " does not match the block's " + block.TickCount;
                if (!string.IsNullOrEmpty(reason))
                    _plugin.LogInfo("Bake: " + Path.GetFileName(path) + " is not used (" + reason + "); the block will be re-recorded.");
                _missing.Add(block.Hash);
                return null;
            }
            data.StartTick = block.StartTick;
            if (data.Version < SystemRngFormatVersion)
                _plugin.LogWarning("Bake: " + Path.GetFileName(path) + " is format " + data.Version +
                                   " and only carries UnityEngine.Random: the System.Random seed stream is not applied " +
                                   "inside or after this block. Force Re-Bake to record it.");
            // A bake from disk never has the per-instance System.Random states (they are live object
            // references, kept only for a bake recorded in this session).
            _loaded[block.FileName] = data;
            return data;
        }

        // ------------------------------------------------------------------
        // Skip baked blocks to end
        // ------------------------------------------------------------------

        void TrySkip(TasEngine engine, TasScript script)
        {
            IList<TasBakeBlock> blocks = script.BakeBlocks;
            // A chain of baked blocks back to back is skipped in one frame.
            for (int guard = 0; guard < blocks.Count; guard++)
            {
                int next = engine.CurrentTick;
                TasBakeBlock block = null;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i].StartTick == next)
                    {
                        block = blocks[i];
                        break;
                    }
                }
                if (block == null)
                    return;
                BakeBlockData data = GetValidBake(block);
                if (data == null || data.TickCount <= 0)
                    return;
                int last = data.TickCount - 1;
                ResetEntityCaches();
                bool reseeded = false;
                try
                {
                    // A block at tick 0 is skipped before tick 0 runs: seed the run exactly as tick 0
                    // would (TasEngine.ExecuteTick), then the baked RNG below replaces the seeded state.
                    reseeded = engine.ApplyPendingReseed();
                    // Projectile keys cannot be followed through ticks that never ran.
                    ApplyFrame(data, last, false);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Bake: skip of block '" + block.FileName + "' failed: " + ex.Message);
                    // Tick 0 runs live after all: it reseeds at its start, as it does without a bake.
                    if (reseeded)
                        engine.RearmFirstTickReseed();
                    return;
                }
                finally
                {
                    // A skip is not an enforced tick: its writes are not desync corrections.
                    BeginCorrectionTick();
                }
                TickBakeFrame final = data.Frames[last];
                if (!engine.BakeJumpTo(block.EndTick, final.Time, final.Pitch, final.Yaw))
                {
                    if (reseeded)
                        engine.RearmFirstTickReseed();
                    return;
                }
                ResetRun();
                _lastTick = block.EndTick - 1;
                StatusText = StatusSkipped;
                DetailText = "skipped block '" + block.FileName + "' to tick " + block.EndTick;
                _plugin.LogInfo("Bake: skipped block '" + block.FileName + "' to its last baked frame; live from tick " +
                                block.EndTick + ".");
                if (engine.Finished)
                    return;
            }
        }

        // ------------------------------------------------------------------
        // Force re-bake
        // ------------------------------------------------------------------

        /// <summary>Deletes every block's .ukbake for this script; the next run records them fresh.</summary>
        public int ForceRebake(TasScript script, string scriptPath)
        {
            ResetRun();
            _loaded.Clear();
            _missing.Clear();
            if (script == null || string.IsNullOrEmpty(scriptPath))
                return 0;
            int deleted = 0;
            IList<TasBakeBlock> blocks = script.BakeBlocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                string path = SidecarPath(scriptPath, blocks[i]);
                if (path.Length > 0 && Delete(path))
                    deleted++;
            }
            return deleted;
        }

        /// <summary>How many of the script's blocks have a valid bake in memory (no disk access).</summary>
        public int CachedBlockCount(TasScript script)
        {
            if (script == null || !ReferenceEquals(script, _script))
                return 0;
            int count = 0;
            IList<TasBakeBlock> blocks = script.BakeBlocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                BakeBlockData data;
                if (_loaded.TryGetValue(blocks[i].FileName, out data) &&
                    string.Equals(data.Hash, blocks[i].Hash, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        // ------------------------------------------------------------------
        // Sidecar files
        // ------------------------------------------------------------------

        /// <summary>"&lt;dir&gt;/&lt;script name&gt;-&lt;block name&gt;.ukbake", or empty when the script has no path.</summary>
        public static string SidecarPath(string scriptPath, TasBakeBlock block)
        {
            if (string.IsNullOrEmpty(scriptPath) || block == null)
                return string.Empty;
            try
            {
                string full = Path.GetFullPath(scriptPath);
                string directory = Path.GetDirectoryName(full) ?? string.Empty;
                string name = SanitizeFileName(Path.GetFileNameWithoutExtension(full)) + "-" +
                              SanitizeFileName(block.FileName) + Extension;
                return Path.Combine(directory, name);
            }
            catch
            {
                return string.Empty;
            }
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "block";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reads a .ukbake: checks the magic, the format version and that its hash equals
        /// <paramref name="expectedHash"/>, then decompresses it once into memory. Null (with a
        /// reason, or none when the file simply does not exist) when missing, damaged or dirty.
        /// </summary>
        public static BakeBlockData Load(string path, string expectedHash, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            try
            {
                byte[] raw;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (MemoryStream memory = new MemoryStream())
                {
                    gzip.CopyTo(memory);
                    raw = memory.ToArray();
                }
                using (BinaryReader reader = new BinaryReader(new MemoryStream(raw, false)))
                {
                    if (reader.ReadUInt32() != Magic)
                    {
                        reason = "not a .ukbake file";
                        return null;
                    }
                    int version = reader.ReadInt32();
                    if (version < MinFormatVersion || version > FormatVersion)
                    {
                        reason = "format version " + version + ", expected " + MinFormatVersion + "-" + FormatVersion;
                        return null;
                    }
                    BakeBlockData data = new BakeBlockData { Path = path, Version = version };
                    data.Name = reader.ReadString();
                    data.Hash = reader.ReadString();
                    if (expectedHash != null && !string.Equals(data.Hash, expectedHash, StringComparison.Ordinal))
                    {
                        reason = "the script changed since it was baked (hash mismatch)";
                        return null;
                    }
                    data.StartTick = reader.ReadInt32();
                    data.TickCount = reader.ReadInt32();
                    if (data.TickCount < 0 || data.TickCount > TasScript.MaxTicksPerLine * 16)
                    {
                        reason = "bad tick count";
                        return null;
                    }
                    data.Offsets = new int[data.TickCount];
                    for (int i = 0; i < data.TickCount; i++)
                        data.Offsets[i] = reader.ReadInt32();
                    data.DataLength = reader.ReadInt32();
                    if (data.DataLength < 0)
                    {
                        reason = "bad data length";
                        return null;
                    }
                    data.Data = reader.ReadBytes(data.DataLength);
                    if (data.Data.Length != data.DataLength)
                    {
                        reason = "file is truncated";
                        return null;
                    }
                    for (int i = 0; i < data.TickCount; i++)
                    {
                        if (data.Offsets[i] < 0 || data.Offsets[i] >= data.DataLength)
                        {
                            reason = "bad frame offset";
                            return null;
                        }
                    }
                    return data;
                }
            }
            catch (Exception ex)
            {
                reason = "could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>Writes a block's bake atomically (temp file, then swap).</summary>
        public static bool Save(BakeBlockData data, out string error)
        {
            error = null;
            string temp = data.Path + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(data.Path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (GZipStream gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (BinaryWriter writer = new BinaryWriter(gzip))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(data.Name ?? string.Empty);
                    writer.Write(data.Hash ?? string.Empty);
                    writer.Write(data.StartTick);
                    writer.Write(data.TickCount);
                    for (int i = 0; i < data.TickCount; i++)
                        writer.Write(data.Offsets[i]);
                    writer.Write(data.DataLength);
                    writer.Write(data.Data, 0, data.DataLength);
                }
                if (File.Exists(data.Path))
                    File.Replace(temp, data.Path, null);
                else
                    File.Move(temp, data.Path);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Deletes one .ukbake (Force Re-Bake). True when a file was removed.</summary>
        public static bool Delete(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Frame encoding
        // ------------------------------------------------------------------

        void RecordTick(int tick, double time)
        {
            BinaryWriter w = _recWriter;
            _recOffsets.Add((int)_recBuffer.Position);
            w.Write(tick);
            w.Write(time);
            WriteRng(w);
            RecordSystemRng(w);
            WritePlayer(w);
            CollectProjectiles();
            WriteProjectiles(w);
            WriteEnemies(w);
            WriteDoors(w);
        }

        /// <summary>Bit of a projectile type in a type mask (types 31 and above share bit 31).</summary>
        static int TypeBit(int type)
        {
            return type < 31 ? 1 << type : 1 << 31;
        }

        /// <summary>
        /// Decodes every frame of a block once into its typed arrays (frames indexed by tick, the
        /// entries of all frames in flat arrays), computes each frame's projectile change mask, and
        /// releases the raw bytes. Playback never parses bytes per tick. False (with a reason) when
        /// the data is damaged; the block is then re-recorded.
        /// </summary>
        static bool Decode(BakeBlockData data, out string reason)
        {
            reason = null;
            if (data.Decoded)
                return true;
            try
            {
                int ticks = data.TickCount;
                TickBakeFrame[] frames = new TickBakeFrame[ticks];
                List<float> charges = new List<float>(ticks * 4);
                List<BakeProjectile> projectiles = new List<BakeProjectile>(ticks * 4);
                List<BakeEnemy> enemies = new List<BakeEnemy>(ticks * 4);
                List<byte> doors = new List<byte>(ticks * 4);
                List<byte> rooms = new List<byte>(ticks * 2);
                // Projectile key sets of the previous and the current frame, for the change mask.
                HashSet<int> previousKeys = new HashSet<int>();
                HashSet<int> currentKeys = new HashSet<int>();
                int[] previousCount = new int[32];
                int[] currentCount = new int[32];

                using (BinaryReader r = new BinaryReader(new MemoryStream(data.Data, 0, data.DataLength, false)))
                {
                    Stream stream = r.BaseStream;
                    for (int index = 0; index < ticks; index++)
                    {
                        stream.Position = data.Offsets[index];
                        TickBakeFrame f = default(TickBakeFrame);
                        f.Tick = r.ReadInt32();
                        f.Time = r.ReadDouble();
                        f.Rng0 = r.ReadInt32();
                        f.Rng1 = r.ReadInt32();
                        f.Rng2 = r.ReadInt32();
                        f.Rng3 = r.ReadInt32();
                        if (data.Version >= SystemRngFormatVersion)
                        {
                            f.HasSystemSeedStream = true;
                            f.SystemSeedStream = r.ReadInt32();
                        }

                        f.HasPlayer = r.ReadByte() != 0;
                        f.ChargeStart = charges.Count;
                        if (f.HasPlayer)
                        {
                            f.PlayerPosition = ReadV3(r);
                            f.PlayerVelocity = ReadV3(r);
                            f.Pitch = r.ReadSingle();
                            f.Yaw = r.ReadSingle();
                            f.Health = r.ReadSingle();
                            f.HardDamage = r.ReadSingle();
                            f.Stamina = r.ReadSingle();
                            f.MoveFlags = r.ReadByte();
                            f.WeaponSlot = r.ReadInt32();
                            f.WeaponVariation = r.ReadInt32();
                            f.ChargeCount = r.ReadByte();
                            for (int i = 0; i < f.ChargeCount; i++)
                                charges.Add(r.ReadSingle());
                        }

                        f.ProjectileStart = projectiles.Count;
                        f.ProjectileCount = ReadCount(r);
                        for (int t = 0; t < currentCount.Length; t++)
                            currentCount[t] = 0;
                        currentKeys.Clear();
                        int changed = 0;
                        for (int i = 0; i < f.ProjectileCount; i++)
                        {
                            BakeProjectile p;
                            p.Key = r.ReadInt32();
                            p.Type = r.ReadInt32();
                            p.Position = ReadV3(r);
                            p.Rotation = ReadQ(r);
                            p.Velocity = ReadV3(r);
                            p.AngularVelocity = ReadV3(r);
                            p.Timer = r.ReadSingle();
                            p.Hits = r.ReadInt32();
                            p.Flags = r.ReadByte();
                            projectiles.Add(p);
                            int slot = p.Type >= 0 && p.Type < 31 ? p.Type : 31;
                            currentCount[slot]++;
                            currentKeys.Add(p.Key);
                            if (!previousKeys.Contains(p.Key))
                                changed |= TypeBit(slot);
                        }
                        // A type with the same count and no new key lost nothing either.
                        for (int t = 0; t < currentCount.Length; t++)
                        {
                            if (currentCount[t] != previousCount[t])
                                changed |= TypeBit(t);
                        }
                        f.ProjectileChangeMask = changed;
                        HashSet<int> swapKeys = previousKeys;
                        previousKeys = currentKeys;
                        currentKeys = swapKeys;
                        int[] swapCount = previousCount;
                        previousCount = currentCount;
                        currentCount = swapCount;

                        f.EnemyStart = enemies.Count;
                        f.EnemyCount = ReadCount(r);
                        for (int i = 0; i < f.EnemyCount; i++)
                        {
                            BakeEnemy e;
                            e.Key = r.ReadInt32();
                            e.Type = r.ReadInt32();
                            e.Position = ReadV3(r);
                            e.Rotation = ReadQ(r);
                            e.Velocity = ReadV3(r);
                            e.Health = r.ReadSingle();
                            e.Flags = r.ReadByte();
                            e.NavDestination = ReadV3(r);
                            e.NavVelocity = ReadV3(r);
                            // AI attack cooldowns: the count is written for format room; none are recorded yet.
                            int cooldowns = r.ReadByte();
                            for (int c = 0; c < cooldowns; c++)
                                r.ReadSingle();
                            e.StateHash = r.ReadInt32();
                            e.NormalizedTime = r.ReadSingle();
                            enemies.Add(e);
                        }

                        f.DoorStart = doors.Count;
                        f.DoorCount = ReadCount(r);
                        for (int i = 0; i < f.DoorCount; i++)
                            doors.Add(r.ReadByte());
                        f.RoomStart = rooms.Count;
                        f.RoomCount = ReadCount(r);
                        int roomBytes = (f.RoomCount + 7) >> 3;
                        for (int i = 0; i < roomBytes; i++)
                            rooms.Add(r.ReadByte());
                        frames[index] = f;
                    }
                }

                data.Frames = frames;
                data.Charges = charges.ToArray();
                data.Projectiles = projectiles.ToArray();
                data.Enemies = enemies.ToArray();
                data.DoorFlags = doors.ToArray();
                data.RoomBits = rooms.ToArray();
                data.Decoded = true;
                // Everything playback reads is in the typed arrays now.
                data.Data = new byte[0];
                return true;
            }
            catch (Exception ex)
            {
                reason = "frame decode failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>An entry count from the file; a negative or absurd value means the data is damaged.</summary>
        static int ReadCount(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > (1 << 24))
                throw new InvalidDataException("bad entry count " + count);
            return count;
        }

        static void WriteV3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        static void WriteQ(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        static Vector3 ReadV3(BinaryReader r)
        {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            return new Vector3(x, y, z);
        }

        static Quaternion ReadQ(BinaryReader r)
        {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            float w = r.ReadSingle();
            return new Quaternion(x, y, z, w);
        }

        /// <summary>Writes a placeholder count; <see cref="PatchCount"/> fills it once the entries are written.</summary>
        long BeginCount(BinaryWriter w)
        {
            long at = _recBuffer.Position;
            w.Write(0);
            return at;
        }

        void PatchCount(BinaryWriter w, long at, int count)
        {
            long end = _recBuffer.Position;
            _recBuffer.Position = at;
            w.Write(count);
            _recBuffer.Position = end;
        }

        // ------------------------------------------------------------------
        // A. RNG
        // ------------------------------------------------------------------

        /// <summary>
        /// UnityEngine.Random.State is a 16-byte struct of four ints (s0..s3). This overlay reads and
        /// writes those bytes without reflection or boxing.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        struct RandomStateBits
        {
            [FieldOffset(0)] public UnityEngine.Random.State State;
            [FieldOffset(0)] public int S0;
            [FieldOffset(4)] public int S1;
            [FieldOffset(8)] public int S2;
            [FieldOffset(12)] public int S3;
        }

        bool _rngBroken;

        void WriteRng(BinaryWriter w)
        {
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            if (!_rngBroken)
            {
                try
                {
                    ReadRngBits(out s0, out s1, out s2, out s3);
                }
                catch (Exception ex)
                {
                    // Stub: the overlay could not be used on this runtime; zeros are written.
                    _rngBroken = true;
                    _plugin.LogWarning("Bake: Random.state cannot be read (" + ex.Message + "); RNG is not baked.");
                }
            }
            w.Write(s0);
            w.Write(s1);
            w.Write(s2);
            w.Write(s3);
        }

        static void ReadRngBits(out int s0, out int s1, out int s2, out int s3)
        {
            RandomStateBits bits = default(RandomStateBits);
            bits.State = UnityEngine.Random.state;
            s0 = bits.S0;
            s1 = bits.S1;
            s2 = bits.S2;
            s3 = bits.S3;
        }

        static void WriteRngBits(int s0, int s1, int s2, int s3)
        {
            RandomStateBits bits = default(RandomStateBits);
            bits.S0 = s0;
            bits.S1 = s1;
            bits.S2 = s2;
            bits.S3 = s3;
            UnityEngine.Random.state = bits.State;
        }

        bool _systemRngWarned;

        /// <summary>
        /// The rest of the savestate RNG set (SaveStateManager capture): RngController.SystemSeedStream
        /// goes into the frame; the live System.Random snapshots (RngController.CaptureSystemRandoms)
        /// are kept in memory per tick, since they reference live instances.
        /// </summary>
        void RecordSystemRng(BinaryWriter w)
        {
            RngController rng = _plugin.Rng;
            w.Write(rng != null ? rng.SystemSeedStream : 0);
            List<SystemRandomSnapshot> snapshots = null;
            if (rng != null)
            {
                List<SystemRandomSnapshot> previous = _recSystemRandoms.Count > 0
                    ? _recSystemRandoms[_recSystemRandoms.Count - 1]
                    : null;
                try
                {
                    snapshots = rng.CaptureSystemRandoms(previous);
                }
                catch (Exception ex)
                {
                    if (!_systemRngWarned)
                    {
                        _systemRngWarned = true;
                        _plugin.LogWarning("Bake: System.Random states cannot be captured (" + ex.Message + ").");
                    }
                }
            }
            _recSystemRandoms.Add(snapshots);
        }

        /// <summary>
        /// Writes the baked System.Random seed stream and (for a bake recorded in this session) the
        /// System.Random instance states, after the Unity state: the same order as a savestate load.
        /// </summary>
        void ApplySystemRng(BakeBlockData data, int index, ref TickBakeFrame f)
        {
            RngController rng = _plugin.Rng;
            if (rng == null)
                return;
            if (f.HasSystemSeedStream && rng.SystemSeedStream != f.SystemSeedStream)
            {
                rng.SystemSeedStream = f.SystemSeedStream;
                _tickRng++;
            }
            List<SystemRandomSnapshot>[] randoms = data.SystemRandoms;
            if (randoms == null || index < 0 || index >= randoms.Length || randoms[index] == null)
                return;
            try
            {
                rng.RestoreSystemRandoms(randoms[index]);
            }
            catch (Exception ex)
            {
                if (!_systemRngWarned)
                {
                    _systemRngWarned = true;
                    _plugin.LogWarning("Bake: System.Random states cannot be restored (" + ex.Message + ").");
                }
            }
        }

        // ------------------------------------------------------------------
        // B. Player
        // ------------------------------------------------------------------

        Component _player;
        Transform _playerTransform;
        Rigidbody _playerBody;
        FieldAccess _fHp, _fAntiHp, _fBoostCharge, _fSliding, _fJumping, _fDashing, _fGroundCheck, _fOnGround;
        Type _groundCheckType;
        object _camera;
        FieldAccess _fRotX, _fRotY;
        object _gun;
        FieldAccess _fSlot, _fVariation;
        Component _charges;
        FieldAccess[] _chargeFields = new FieldAccess[0];
        Type _chargesType;
        bool _chargesSearched;

        /// <summary>Resolves the player rig's members once per player object (a scene load replaces it).</summary>
        bool EnsurePlayer()
        {
            Component player = _bridge.PlayerComponent;
            if (player == null)
            {
                _player = null;
                return false;
            }
            if (!ReferenceEquals(player, _player))
            {
                if (!ReferenceEquals(_player, null))
                {
                    // The scene changed: every cached enemy/door/projectile key belongs to the old one.
                    _enemiesCached = false;
                    _doorsCached = false;
                    if (_projKeys.Count > 0)
                        _projValidMask = 0;
                    _projKeys.Clear();
                    for (int i = 0; i < _projNextOrdinal.Length; i++)
                        _projNextOrdinal[i] = 0;
                    ClearLiveProjectiles();
                }
                _player = player;
                _playerTransform = player.transform;
                _playerBody = player.GetComponent<Rigidbody>();
                Type type = player.GetType();
                _fHp = NamedField(type, "hp", "health", "currentHealth");
                _fAntiHp = NamedField(type, "antiHp", "antiHP", "hardDamage");
                _fBoostCharge = NamedField(type, "boostCharge", "stamina");
                _fSliding = NamedField(type, "sliding");
                _fJumping = NamedField(type, "jumping");
                _fDashing = NamedField(type, "boost");
                _fGroundCheck = NamedField(type, "gc");
                _fOnGround = null;
                _groundCheckType = null;
                _chargesSearched = false;
            }
            object camera = _bridge.CameraController;
            if (!ReferenceEquals(camera, _camera))
            {
                _camera = camera;
                _fRotX = camera != null ? NamedField(camera.GetType(), "rotationX") : null;
                _fRotY = camera != null ? NamedField(camera.GetType(), "rotationY") : null;
            }
            object gun = _bridge.GunControl;
            if (!ReferenceEquals(gun, _gun))
            {
                _gun = gun;
                _fSlot = gun != null ? NamedField(gun.GetType(), "currentSlotIndex") : null;
                _fVariation = gun != null ? NamedField(gun.GetType(), "currentVariationIndex") : null;
            }
            if (!_chargesSearched || (_charges == null && !ReferenceEquals(_charges, null)))
            {
                _chargesSearched = true;
                _charges = null;
                _chargeFields = new FieldAccess[0];
                if (_chargesType == null)
                    _chargesType = _bridge.FindType("WeaponCharges");
                if (_chargesType != null)
                {
                    _charges = UnityEngine.Object.FindObjectOfType(_chargesType) as Component;
                    if (_charges != null)
                        _chargeFields = FloatFields(_chargesType);
                }
            }
            return true;
        }

        void WritePlayer(BinaryWriter w)
        {
            if (!EnsurePlayer())
            {
                w.Write((byte)0);
                return;
            }
            w.Write((byte)1);
            Component player = _player;
            WriteV3(w, player.transform.position);
            WriteV3(w, _playerBody != null && !_playerBody.isKinematic ? _playerBody.velocity : Vector3.zero);
            // Script convention: positive pitch is down, the game's rotationX is positive up.
            w.Write(_camera != null ? -GetFloat(_fRotX, _camera) : 0f);
            w.Write(_camera != null ? GetFloat(_fRotY, _camera) : 0f);
            w.Write(GetFloat(_fHp, player));
            w.Write(GetFloat(_fAntiHp, player));
            w.Write(GetFloat(_fBoostCharge, player));
            w.Write(ReadMoveFlags(player));
            w.Write(_gun != null ? GetInt(_fSlot, _gun) : -1);
            w.Write(_gun != null ? GetInt(_fVariation, _gun) : -1);
            int charges = _charges != null ? Math.Min(255, _chargeFields.Length) : 0;
            w.Write((byte)charges);
            for (int i = 0; i < charges; i++)
                w.Write(GetFloat(_chargeFields[i], _charges));
        }

        byte ReadMoveFlags(Component player)
        {
            byte flags = 0;
            if (_fGroundCheck != null)
            {
                object gc = _fGroundCheck.GetValue(player);
                if (gc != null)
                {
                    Type gcType = gc.GetType();
                    if (!ReferenceEquals(gcType, _groundCheckType))
                    {
                        _groundCheckType = gcType;
                        _fOnGround = NamedField(gcType, "onGround");
                    }
                    if (GetBool(_fOnGround, gc))
                        flags |= TickBakeFrame.MoveGrounded;
                }
            }
            if (GetBool(_fSliding, player))
                flags |= TickBakeFrame.MoveSliding;
            if (GetBool(_fJumping, player))
                flags |= TickBakeFrame.MoveJumping;
            if (GetBool(_fDashing, player))
                flags |= TickBakeFrame.MoveDashing;
            return flags;
        }

        /// <summary>
        /// Puts the player on the baked values. Every value is compared first (compiled accessors,
        /// no boxing) and only written, and counted as a correction, when it differs.
        /// </summary>
        void ApplyPlayer(BakeBlockData data, ref TickBakeFrame f)
        {
            if (!f.HasPlayer || !EnsurePlayer())
                return;
            Component player = _player;
            Transform transform = _playerTransform;
            if (transform == null)
                transform = _playerTransform = player.transform;
            if ((transform.position - f.PlayerPosition).sqrMagnitude > _thresholdSq)
            {
                transform.position = f.PlayerPosition;
                if (_playerBody != null)
                    _playerBody.position = f.PlayerPosition;
                _tickPlayer++;
            }
            if (_playerBody != null && !_playerBody.isKinematic &&
                (_playerBody.velocity - f.PlayerVelocity).sqrMagnitude > _thresholdSq)
            {
                _playerBody.velocity = f.PlayerVelocity;
                _tickPlayer++;
            }
            if (_camera != null && _fRotX != null && _fRotY != null)
            {
                if (Math.Abs(-GetFloat(_fRotX, _camera) - f.Pitch) > ApplyEpsilon)
                {
                    SetFloat(_fRotX, _camera, -f.Pitch);
                    _tickPlayer++;
                }
                if (Math.Abs(GetFloat(_fRotY, _camera) - f.Yaw) > ApplyEpsilon)
                {
                    SetFloat(_fRotY, _camera, f.Yaw);
                    _tickPlayer++;
                }
            }
            if (_fHp != null && Math.Abs(GetFloat(_fHp, player) - f.Health) > ApplyEpsilon)
            {
                SetFloat(_fHp, player, f.Health);
                _tickPlayer++;
            }
            if (_fAntiHp != null && Math.Abs(GetFloat(_fAntiHp, player) - f.HardDamage) > ApplyEpsilon)
            {
                SetFloat(_fAntiHp, player, f.HardDamage);
                _tickPlayer++;
            }
            if (_fBoostCharge != null && Math.Abs(GetFloat(_fBoostCharge, player) - f.Stamina) > ApplyEpsilon)
            {
                SetFloat(_fBoostCharge, player, f.Stamina);
                _tickPlayer++;
            }
            // Movement flags and the weapon slot are recorded but not written back: flipping
            // NewMovement.sliding/jumping/boost or GunControl's slot without the game's own
            // transition code would leave colliders, particles and viewmodels inconsistent.
            if (_charges != null)
            {
                int count = Math.Min(f.ChargeCount, _chargeFields.Length);
                float[] charges = data.Charges;
                for (int i = 0; i < count; i++)
                {
                    float baked = charges[f.ChargeStart + i];
                    if (Math.Abs(GetFloat(_chargeFields[i], _charges) - baked) > ApplyEpsilon)
                    {
                        SetFloat(_chargeFields[i], _charges, baked);
                        _tickPlayer++;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // C. Projectiles and coins
        // ------------------------------------------------------------------

        struct PendingProjectile
        {
            public Component Component;
            public int Type;
            public int Id;
            public Vector3 Position;
        }

        Type[] _projTypes;
        int _coinType = -1;
        FieldAccess[] _projTimer, _projHits, _projShot, _projSplit;
        int[] _projNextOrdinal = new int[0];
        // Root instance id -> key, for every projectile seen since the block's first tick.
        readonly Dictionary<int, int> _projKeys = new Dictionary<int, int>(256);
        readonly HashSet<int> _projRoots = new HashSet<int>();
        readonly List<PendingProjectile> _pending = new List<PendingProjectile>(16);
        // The live projectile set: one slot per object, with its parts resolved once when it is
        // first seen (no GetComponent / .transform per tick).
        Component[] _liveProj = new Component[64];
        GameObject[] _liveProjObject = new GameObject[64];
        Transform[] _liveProjTransform = new Transform[64];
        Rigidbody[] _liveProjBody = new Rigidbody[64];
        int[] _liveProjKey = new int[64];
        int[] _liveProjType = new int[64];
        int _liveProjCount;
        // Playback: recorded key -> live slot (rebuilt only on a tick that rescanned a type).
        readonly Dictionary<int, int> _slotByKey = new Dictionary<int, int>(128);
        // The next playback tick rescans every type (block start, cache reset).
        bool _projFullScanPending = true;
        // Root instance ids seen by one per-type rescan.
        readonly HashSet<int> _scanRoots = new HashSet<int>();
        // Verify: baked coin key -> entry index in the current frame (no allocation once warmed up).
        readonly Dictionary<int, int> _frameProjIndex = new Dictionary<int, int>(64);
        static readonly Comparison<PendingProjectile> PendingOrder = ComparePending;
        static readonly IComparer<PendingProjectile> PendingComparer = new PendingProjectileComparer();

        sealed class PendingProjectileComparer : IComparer<PendingProjectile>
        {
            public int Compare(PendingProjectile a, PendingProjectile b)
            {
                return ComparePending(a, b);
            }
        }

        static int ComparePending(PendingProjectile a, PendingProjectile b)
        {
            if (a.Type != b.Type)
                return a.Type.CompareTo(b.Type);
            int c = a.Position.x.CompareTo(b.Position.x);
            if (c != 0)
                return c;
            c = a.Position.y.CompareTo(b.Position.y);
            if (c != 0)
                return c;
            return a.Position.z.CompareTo(b.Position.z);
        }

        void EnsureProjectileTypes()
        {
            if (_projTypes != null)
                return;
            string[] names = SaveStateManager.BakeProjectileTypeNames;
            _projTypes = new Type[names.Length];
            _projTimer = new FieldAccess[names.Length];
            _projHits = new FieldAccess[names.Length];
            _projShot = new FieldAccess[names.Length];
            _projSplit = new FieldAccess[names.Length];
            _projNextOrdinal = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Type type = _bridge.FindType(names[i]);
                _projTypes[i] = type;
                if (type == null)
                    continue;
                bool coin = names[i] == "Coin";
                if (coin)
                    _coinType = i;
                _projTimer[i] = NamedField(type, coin ? CoinState.TimerNames : ProjectileTimerNames);
                _projHits[i] = NamedField(type, coin ? CoinState.HitNames : ProjectileHitNames);
                _projShot[i] = NamedField(type, CoinState.ShotNames);
                _projSplit[i] = coin ? NamedField(type, CoinState.SplitNames) : null;
            }
            // A type that was not loaded yet is looked up again at the next block.
            for (int i = 0; i < _projTypes.Length; i++)
            {
                if (_projTypes[i] == null)
                {
                    _retryProjectileTypes = true;
                    break;
                }
            }
        }

        bool _retryProjectileTypes;

        /// <summary>
        /// Every active projectile/coin (same types and filters as the savestate capture), each with a
        /// key that is stable between runs: (type &lt;&lt; 20) | order of first appearance of that
        /// type in the block, objects that appear on the same tick ordered by position.
        /// </summary>
        void CollectProjectiles()
        {
            EnsureProjectileTypes();
            _liveProjCount = 0;
            _projRoots.Clear();
            _pending.Clear();
            for (int t = 0; t < _projTypes.Length; t++)
            {
                Type type = _projTypes[t];
                if (type == null)
                    continue;
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(type); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    GameObject root = component.gameObject;
                    if (!root.activeInHierarchy || SaveStateManager.IsTemplateClone(root))
                        continue;
                    int id = root.GetInstanceID();
                    if (!_projRoots.Add(id))
                        continue;
                    int key;
                    if (_projKeys.TryGetValue(id, out key))
                        AddLiveProjectile(component, key, t);
                    else
                        _pending.Add(new PendingProjectile { Component = component, Type = t, Id = id, Position = root.transform.position });
                }
            }
            if (_pending.Count > 1)
                _pending.Sort(PendingOrder);
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingProjectile p = _pending[i];
                int key = (p.Type << 20) | (_projNextOrdinal[p.Type]++ & 0xFFFFF);
                _projKeys[p.Id] = key;
                AddLiveProjectile(p.Component, key, p.Type);
            }
            _pending.Clear();
        }

        void AddLiveProjectile(Component component, int key, int type)
        {
            if (_liveProjCount == _liveProj.Length)
            {
                int size = _liveProj.Length * 2;
                Array.Resize(ref _liveProj, size);
                Array.Resize(ref _liveProjObject, size);
                Array.Resize(ref _liveProjTransform, size);
                Array.Resize(ref _liveProjBody, size);
                Array.Resize(ref _liveProjKey, size);
                Array.Resize(ref _liveProjType, size);
            }
            _liveProj[_liveProjCount] = component;
            _liveProjObject[_liveProjCount] = component.gameObject;
            _liveProjTransform[_liveProjCount] = component.transform;
            _liveProjBody[_liveProjCount] = component.GetComponent<Rigidbody>();
            _liveProjKey[_liveProjCount] = key;
            _liveProjType[_liveProjCount] = type;
            _liveProjCount++;
        }

        void ClearLiveProjectiles()
        {
            _liveProjCount = 0;
            Array.Clear(_liveProj, 0, _liveProj.Length);
            Array.Clear(_liveProjObject, 0, _liveProjObject.Length);
            Array.Clear(_liveProjTransform, 0, _liveProjTransform.Length);
            Array.Clear(_liveProjBody, 0, _liveProjBody.Length);
            _slotByKey.Clear();
            _projFullScanPending = true;
        }

        int CoinMask()
        {
            EnsureProjectileTypes();
            return _coinType >= 0 ? TypeBit(_coinType) : 0;
        }

        /// <summary>
        /// Playback: keeps the cached live projectile set in step with the bake without a scene scan
        /// per tick. A type is rescanned (one FindObjectsOfType) only when
        ///  - this is the first tracked tick of the block (every type),
        ///  - the recording spawned or destroyed an object of that type on this tick (the frame's
        ///    precomputed change mask),
        ///  - the bake names a key of that type this run has not assigned yet (the live object
        ///    appears later than in the recording), or
        ///  - <paramref name="forceMask"/> asks for it (the drift detector's coins).
        /// Objects that are destroyed or deactivated live are skipped per tick through their cached
        /// slot. Keys are assigned exactly as the per-tick scan assigns them while recording.
        /// </summary>
        void TrackProjectiles(BakeBlockData data, int index, int forceMask)
        {
            if (_projValidMask == 0)
                return;
            EnsureProjectileTypes();
            ref TickBakeFrame f = ref data.Frames[index];
            int need = forceMask | f.ProjectileChangeMask;
            if (_projFullScanPending)
            {
                need = -1;
                _projFullScanPending = false;
            }
            BakeProjectile[] baked = data.Projectiles;
            int end = f.ProjectileStart + f.ProjectileCount;
            for (int i = f.ProjectileStart; i < end; i++)
            {
                int t = baked[i].Type;
                if (t >= 0 && t < _projNextOrdinal.Length && (baked[i].Key & 0xFFFFF) >= _projNextOrdinal[t])
                    need |= TypeBit(t);
            }
            need &= _projValidMask;
            if (need == 0)
                return;
            bool rescanned = false;
            for (int t = 0; t < _projTypes.Length; t++)
            {
                if ((need & TypeBit(t)) != 0 && _projTypes[t] != null)
                {
                    RescanProjectileType(t);
                    rescanned = true;
                }
            }
            if (!rescanned)
                return;
            _slotByKey.Clear();
            for (int i = 0; i < _liveProjCount; i++)
                _slotByKey[_liveProjKey[i]] = i;
        }

        /// <summary>
        /// Rebuilds the live slots of one projectile type from a scan of that type only, with the
        /// recording's rules: active, not a template clone, one entry per GameObject (a GameObject
        /// that also carries a lower-indexed type belongs to that type, as in the all-type scan),
        /// known objects keep their key, new ones get the next ordinals in position order.
        /// </summary>
        void RescanProjectileType(int t)
        {
            _projRescans++;
            // Drop this type's slots, keeping the order of the others.
            int write = 0;
            for (int i = 0; i < _liveProjCount; i++)
            {
                if (_liveProjType[i] == t)
                    continue;
                if (write != i)
                {
                    _liveProj[write] = _liveProj[i];
                    _liveProjObject[write] = _liveProjObject[i];
                    _liveProjTransform[write] = _liveProjTransform[i];
                    _liveProjBody[write] = _liveProjBody[i];
                    _liveProjKey[write] = _liveProjKey[i];
                    _liveProjType[write] = _liveProjType[i];
                }
                write++;
            }
            for (int i = write; i < _liveProjCount; i++)
            {
                _liveProj[i] = null;
                _liveProjObject[i] = null;
                _liveProjTransform[i] = null;
                _liveProjBody[i] = null;
            }
            _liveProjCount = write;

            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(_projTypes[t]); }
            catch { return; }
            _scanRoots.Clear();
            _pending.Clear();
            for (int i = 0; i < found.Length; i++)
            {
                Component component = found[i] as Component;
                if (component == null)
                    continue;
                GameObject root = component.gameObject;
                if (!root.activeInHierarchy)
                    continue;
                int id = root.GetInstanceID();
                if (!_scanRoots.Add(id))
                    continue;
                int key;
                if (_projKeys.TryGetValue(id, out key))
                {
                    // Known object: it belongs to the type that keyed it.
                    if ((key >> 20) == t)
                        AddLiveProjectile(component, key, t);
                    continue;
                }
                if (SaveStateManager.IsTemplateClone(root) || ClaimedByLowerType(root, t))
                    continue;
                _pending.Add(new PendingProjectile { Component = component, Type = t, Id = id, Position = root.transform.position });
            }
            if (_pending.Count > 1)
                _pending.Sort(PendingComparer);
            for (int i = 0; i < _pending.Count; i++)
            {
                PendingProjectile p = _pending[i];
                int key = (t << 20) | (_projNextOrdinal[t]++ & 0xFFFFF);
                _projKeys[p.Id] = key;
                AddLiveProjectile(p.Component, key, t);
            }
            _pending.Clear();
        }

        /// <summary>The all-type scan gives a GameObject to the first (lowest) type found on it.</summary>
        bool ClaimedByLowerType(GameObject root, int t)
        {
            for (int a = 0; a < t; a++)
            {
                if (_projTypes[a] != null && root.GetComponent(_projTypes[a]) != null)
                    return true;
            }
            return false;
        }

        void WriteProjectiles(BinaryWriter w)
        {
            w.Write(_liveProjCount);
            for (int i = 0; i < _liveProjCount; i++)
            {
                Component component = _liveProj[i];
                int t = _liveProjType[i];
                Transform transform = _liveProjTransform[i];
                Rigidbody body = _liveProjBody[i];
                bool dynamic = body != null && !body.isKinematic;
                w.Write(_liveProjKey[i]);
                w.Write(t);
                WriteV3(w, transform.position);
                WriteQ(w, transform.rotation);
                WriteV3(w, dynamic ? body.velocity : Vector3.zero);
                WriteV3(w, dynamic ? body.angularVelocity : Vector3.zero);
                w.Write(GetFloat(_projTimer[t], component));
                w.Write(GetInt(_projHits[t], component));
                byte flags = 0;
                if (GetBool(_projShot[t], component))
                    flags |= BakeProjectile.FlagShot;
                if (GetBool(_projSplit[t], component))
                    flags |= BakeProjectile.FlagSplittable;
                w.Write(flags);
            }
        }

        /// <summary>
        /// Puts every tracked projectile on its baked entry: the frame's entries are matched to live
        /// slots by recorded key (no per-tick index rebuild), types whose keys were not followed are
        /// skipped, and every value is compared before it is written.
        /// </summary>
        void ApplyProjectiles(BakeBlockData data, ref TickBakeFrame f)
        {
            if (_projValidMask == 0 || _liveProjCount == 0)
                return;
            BakeProjectile[] baked = data.Projectiles;
            int end = f.ProjectileStart + f.ProjectileCount;
            for (int e = f.ProjectileStart; e < end; e++)
            {
                int t = baked[e].Type;
                if (t < 0 || t >= _projTypes.Length || (_projValidMask & TypeBit(t)) == 0)
                    continue;
                int i;
                if (!_slotByKey.TryGetValue(baked[e].Key, out i))
                    continue;
                Component component = _liveProj[i];
                // Destroyed, or deactivated since its type was last scanned: not a live projectile now.
                if (component == null || !_liveProjObject[i].activeInHierarchy)
                    continue;
                Transform transform = _liveProjTransform[i];
                if ((transform.position - baked[e].Position).sqrMagnitude > _thresholdSq)
                {
                    transform.position = baked[e].Position;
                    _tickProjectiles++;
                }
                if (Quaternion.Dot(transform.rotation, baked[e].Rotation) < 0.9999999f)
                {
                    transform.rotation = baked[e].Rotation;
                    _tickProjectiles++;
                }
                Rigidbody body = _liveProjBody[i];
                if (body != null && !body.isKinematic)
                {
                    if ((body.position - baked[e].Position).sqrMagnitude > _thresholdSq)
                    {
                        body.position = baked[e].Position;
                        _tickProjectiles++;
                    }
                    if ((body.velocity - baked[e].Velocity).sqrMagnitude > _thresholdSq)
                    {
                        body.velocity = baked[e].Velocity;
                        _tickProjectiles++;
                    }
                    if ((body.angularVelocity - baked[e].AngularVelocity).sqrMagnitude > _thresholdSq)
                    {
                        body.angularVelocity = baked[e].AngularVelocity;
                        _tickProjectiles++;
                    }
                }
                FieldAccess timer = _projTimer[t];
                if (timer != null && Math.Abs(timer.GetFloat(component) - baked[e].Timer) > ApplyEpsilon)
                {
                    timer.SetFloat(component, baked[e].Timer);
                    _tickProjectiles++;
                }
                FieldAccess hits = _projHits[t];
                if (hits != null && hits.GetInt(component) != baked[e].Hits)
                {
                    hits.SetFloat(component, baked[e].Hits);
                    _tickProjectiles++;
                }
                bool shot = (baked[e].Flags & BakeProjectile.FlagShot) != 0;
                FieldAccess shotField = _projShot[t];
                if (shotField != null && shotField.GetBool(component) != shot)
                {
                    shotField.SetBool(component, shot);
                    _tickProjectiles++;
                }
                bool split = (baked[e].Flags & BakeProjectile.FlagSplittable) != 0;
                FieldAccess splitField = _projSplit[t];
                if (splitField != null && splitField.GetBool(component) != split)
                {
                    splitField.SetBool(component, split);
                    _tickProjectiles++;
                }
            }
        }

        // ------------------------------------------------------------------
        // D. Enemies (EntityRegistry level IDs)
        // ------------------------------------------------------------------

        bool _enemiesCached;
        int _enemyCount;
        int[] _enemyIds = new int[0];
        Component[] _enemyComps = new Component[0];
        Rigidbody[] _enemyBodies = new Rigidbody[0];
        NavMeshAgent[] _enemyAgents = new NavMeshAgent[0];
        Animator[] _enemyAnimators = new Animator[0];
        int[] _enemyTypes = new int[0];
        FieldAccess[] _enemyHealth = new FieldAccess[0];
        FieldAccess[] _enemyDead = new FieldAccess[0];
        GameObject[] _enemyObjects = new GameObject[0];
        Transform[] _enemyTransforms = new Transform[0];
        // Level ID (1..LevelCount) -> cache slot, -1 when not a tracked enemy. An array, not a dictionary.
        int[] _enemySlotById = new int[0];

        /// <summary>
        /// The level's enemies in EntityRegistry order (IDs 1..N, stable across runs), with their
        /// components resolved once per block instead of every tick.
        /// </summary>
        void EnsureEnemies()
        {
            if (_enemiesCached)
                return;
            _enemiesCached = true;
            EntityRegistry registry = _bridge.Entities;
            int levelCount = registry.LevelCount;
            _enemySlotById = new int[levelCount + 1];
            for (int id = 0; id < _enemySlotById.Length; id++)
                _enemySlotById[id] = -1;
            List<EntityRegistry.Entity> enemies = new List<EntityRegistry.Entity>(64);
            for (int id = 1; id <= levelCount; id++)
            {
                EntityRegistry.Entity entity = registry.Find(id);
                if (entity != null && entity.Category == "enemy" && GameBridge.IsAlive(entity.Component) &&
                    !SaveStateManager.IsTemplateClone(entity.Component.gameObject))
                    enemies.Add(entity);
            }
            _enemyCount = enemies.Count;
            _enemyIds = new int[_enemyCount];
            _enemyComps = new Component[_enemyCount];
            _enemyBodies = new Rigidbody[_enemyCount];
            _enemyAgents = new NavMeshAgent[_enemyCount];
            _enemyAnimators = new Animator[_enemyCount];
            _enemyTypes = new int[_enemyCount];
            _enemyHealth = new FieldAccess[_enemyCount];
            _enemyDead = new FieldAccess[_enemyCount];
            _enemyObjects = new GameObject[_enemyCount];
            _enemyTransforms = new Transform[_enemyCount];
            for (int i = 0; i < _enemyCount; i++)
            {
                Component component = enemies[i].Component;
                GameObject root = component.gameObject;
                _enemyIds[i] = enemies[i].Id;
                _enemyComps[i] = component;
                _enemyObjects[i] = root;
                _enemyTransforms[i] = component.transform;
                _enemyBodies[i] = root.GetComponent<Rigidbody>();
                _enemyAgents[i] = root.GetComponent<NavMeshAgent>();
                Animator animator = root.GetComponent<Animator>();
                _enemyAnimators[i] = animator != null ? animator : root.GetComponentInChildren<Animator>(true);
                _enemyTypes[i] = GameBridge.ToInt(_bridge.GetMember(component, EnemyTypeNames), -1);
                Type type = component.GetType();
                _enemyHealth[i] = NamedField(type, EnemyHealthNames);
                _enemyDead[i] = NamedField(type, EnemyDeadNames);
                int slotId = enemies[i].Id;
                if (slotId >= 0 && slotId < _enemySlotById.Length && _enemySlotById[slotId] < 0)
                    _enemySlotById[slotId] = i;
            }
        }

        void WriteEnemies(BinaryWriter w)
        {
            EnsureEnemies();
            long countAt = BeginCount(w);
            int written = 0;
            for (int i = 0; i < _enemyCount; i++)
            {
                Component component = _enemyComps[i];
                if (!GameBridge.IsAlive(component) || !component.gameObject.activeInHierarchy)
                    continue;
                Transform transform = component.transform;
                Rigidbody body = _enemyBodies[i];
                NavMeshAgent agent = _enemyAgents[i];
                bool onMesh = agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
                Vector3 velocity = body != null && !body.isKinematic
                    ? body.velocity
                    : onMesh ? agent.velocity : Vector3.zero;
                byte flags = 0;
                if (GetBool(_enemyDead[i], component))
                    flags |= BakeEnemy.FlagDead;
                Vector3 destination = Vector3.zero;
                Vector3 navVelocity = Vector3.zero;
                if (onMesh)
                {
                    flags |= BakeEnemy.FlagAgent;
                    destination = agent.destination;
                    navVelocity = agent.velocity;
                }
                int stateHash = 0;
                float normalizedTime = 0f;
                Animator animator = _enemyAnimators[i];
                if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null &&
                    animator.layerCount > 0)
                {
                    flags |= BakeEnemy.FlagAnimator;
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                    stateHash = info.fullPathHash;
                    normalizedTime = info.normalizedTime;
                }

                w.Write(_enemyIds[i]);
                w.Write(_enemyTypes[i]);
                WriteV3(w, transform.position);
                WriteQ(w, transform.rotation);
                WriteV3(w, velocity);
                w.Write(GetFloat(_enemyHealth[i], component));
                w.Write(flags);
                WriteV3(w, destination);
                WriteV3(w, navVelocity);
                // AI attack cooldowns: stub. Every enemy type keeps them in different, unnamed
                // fields of its own AI script, so none are recorded (count 0).
                w.Write((byte)0);
                w.Write(stateHash);
                w.Write(normalizedTime);
                written++;
            }
            PatchCount(w, countAt, written);
        }

        /// <summary>
        /// Puts every level enemy in the frame on its baked entry. Only enemies the recording saw
        /// active are in the frame, so dormant ones cost nothing. Lookups go through an array by
        /// level ID and cached components; values are compared before they are written; a NavMesh
        /// agent is warped and re-pathed only when its destination changed or a direct move along
        /// its current path cannot put it back.
        /// </summary>
        void ApplyEnemies(BakeBlockData data, ref TickBakeFrame f)
        {
            EnsureEnemies();
            BakeEnemy[] enemies = data.Enemies;
            int end = f.EnemyStart + f.EnemyCount;
            for (int e = f.EnemyStart; e < end; e++)
            {
                int id = enemies[e].Key;
                if (id < 0 || id >= _enemySlotById.Length)
                    continue;
                int i = _enemySlotById[id];
                if (i < 0)
                    continue;
                Component component = _enemyComps[i];
                if (component == null || !_enemyObjects[i].activeInHierarchy)
                    continue;
                Transform transform = _enemyTransforms[i];
                Vector3 position = enemies[e].Position;
                float driftSq = (transform.position - position).sqrMagnitude;
                bool moved = driftSq > _thresholdSq;
                NavMeshAgent agent = (enemies[e].Flags & BakeEnemy.FlagAgent) != 0 ? _enemyAgents[i] : null;
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    if ((agent.destination - enemies[e].NavDestination).sqrMagnitude > NavDestinationEpsilonSq ||
                        (moved && !MoveAgentDirect(agent, transform, position, driftSq)))
                    {
                        // Diverged: same steps as the savestate loader (warp, synchronous path,
                        // velocity), with a pooled path.
                        SaveStateManager.BakeRestoreAgent(agent, position, enemies[e].NavDestination, enemies[e].NavVelocity);
                        _tickNavRepaths++;
                        _tickEnemies++;
                    }
                    else
                    {
                        if (moved)
                            _tickEnemies++;
                        if ((agent.velocity - enemies[e].NavVelocity).sqrMagnitude > _thresholdSq)
                        {
                            agent.velocity = enemies[e].NavVelocity;
                            _tickEnemies++;
                        }
                    }
                }
                else if (moved)
                {
                    transform.position = position;
                    _tickEnemies++;
                }
                if (Quaternion.Dot(transform.rotation, enemies[e].Rotation) < 0.9999999f)
                {
                    transform.rotation = enemies[e].Rotation;
                    _tickEnemies++;
                }
                Rigidbody body = _enemyBodies[i];
                if (body != null && !body.isKinematic)
                {
                    if (moved)
                        body.position = position;
                    if ((body.velocity - enemies[e].Velocity).sqrMagnitude > _thresholdSq)
                    {
                        body.velocity = enemies[e].Velocity;
                        _tickEnemies++;
                    }
                }
                // Health is written back; death is not forced (killing needs the game's own death
                // path), so a dead flag mismatch is left to the live simulation.
                FieldAccess health = _enemyHealth[i];
                if (health != null && Math.Abs(health.GetFloat(component) - enemies[e].Health) > ApplyEpsilon)
                {
                    health.SetFloat(component, enemies[e].Health);
                    _tickEnemies++;
                }
                if ((enemies[e].Flags & BakeEnemy.FlagAnimator) != 0 && enemies[e].StateHash != 0)
                {
                    Animator animator = _enemyAnimators[i];
                    if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                    {
                        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                        if (info.fullPathHash != enemies[e].StateHash ||
                            Math.Abs(info.normalizedTime - enemies[e].NormalizedTime) > 0.0001f)
                        {
                            animator.Play(enemies[e].StateHash, 0, enemies[e].NormalizedTime);
                            _tickEnemies++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Small drift with the right destination: moves the agent along the navmesh to the baked
        /// spot (NavMeshAgent.nextPosition) without Warp, which would drop its path and force a
        /// synchronous re-path. False when the drift is large or the agent did not land on the
        /// baked spot (off-mesh point, base offset); the caller then warps and re-paths.
        /// </summary>
        static bool MoveAgentDirect(NavMeshAgent agent, Transform transform, Vector3 position, float driftSq)
        {
            if (driftSq > NavDirectMoveMaxSq)
                return false;
            try
            {
                agent.nextPosition = position;
                if ((agent.nextPosition - position).sqrMagnitude > ApplyEpsilonSq)
                    return false;
                if ((transform.position - position).sqrMagnitude > ApplyEpsilonSq)
                    transform.position = position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // E. Doors and rooms
        // ------------------------------------------------------------------

        bool _doorsCached;
        Component[] _doors = new Component[0];
        FieldAccess[] _doorOpen = new FieldAccess[0];
        FieldAccess[] _doorLocked = new FieldAccess[0];
        GameObject[] _rooms = new GameObject[0];

        /// <summary>Every Door (in hierarchy order, so the order is the same each run) and the rooms they stream.</summary>
        void EnsureDoors()
        {
            if (_doorsCached)
                return;
            _doorsCached = true;
            _doors = new Component[0];
            _rooms = new GameObject[0];
            Type doorType = _bridge.FindType("Door");
            if (doorType == null)
                return;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(doorType, true); }
            catch { return; }
            List<KeyValuePair<string, Component>> keyed = new List<KeyValuePair<string, Component>>(found.Length);
            for (int i = 0; i < found.Length; i++)
            {
                Component door = found[i] as Component;
                if (door == null || !door.gameObject.scene.IsValid() || SaveStateManager.IsTemplateClone(door.gameObject))
                    continue;
                keyed.Add(new KeyValuePair<string, Component>(HierarchyKey(door), door));
            }
            keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            _doors = new Component[keyed.Count];
            _doorOpen = new FieldAccess[keyed.Count];
            _doorLocked = new FieldAccess[keyed.Count];
            List<GameObject> rooms = new List<GameObject>(32);
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < keyed.Count; i++)
            {
                Component door = keyed[i].Value;
                _doors[i] = door;
                Type type = door.GetType();
                _doorOpen[i] = NamedField(type, "open");
                _doorLocked[i] = NamedField(type, "locked");
                AddRooms(_bridge.GetMember(door, new[] { "activatedRooms" }) as GameObject[], rooms, seen);
                AddRooms(_bridge.GetMember(door, new[] { "deactivatedRooms" }) as GameObject[], rooms, seen);
            }
            _rooms = rooms.ToArray();
        }

        static void AddRooms(GameObject[] list, List<GameObject> into, HashSet<int> seen)
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null && seen.Add(list[i].GetInstanceID()))
                    into.Add(list[i]);
            }
        }

        /// <summary>Scene handle, then the sibling index of every transform from the root down.</summary>
        static string HierarchyKey(Component component)
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append(component.gameObject.scene.name).Append('|');
            List<int> path = new List<int>(16);
            for (Transform t = component.transform; t != null; t = t.parent)
                path.Add(t.GetSiblingIndex());
            for (int i = path.Count - 1; i >= 0; i--)
                sb.Append(path[i].ToString("D6", CultureInfo.InvariantCulture)).Append('/');
            return sb.ToString();
        }

        void WriteDoors(BinaryWriter w)
        {
            EnsureDoors();
            w.Write(_doors.Length);
            for (int i = 0; i < _doors.Length; i++)
            {
                Component door = _doors[i];
                byte flags = 0;
                if (GameBridge.IsAlive(door))
                {
                    if (GetBool(_doorOpen[i], door))
                        flags |= 1;
                    if (GetBool(_doorLocked[i], door))
                        flags |= 2;
                }
                w.Write(flags);
            }
            w.Write(_rooms.Length);
            int bytes = (_rooms.Length + 7) >> 3;
            for (int b = 0; b < bytes; b++)
            {
                byte packed = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int index = (b << 3) + bit;
                    if (index < _rooms.Length && _rooms[index] != null && _rooms[index].activeSelf)
                        packed |= (byte)(1 << bit);
                }
                w.Write(packed);
            }
        }

        void ApplyDoors(BakeBlockData data, ref TickBakeFrame f)
        {
            EnsureDoors();
            byte[] doorFlags = data.DoorFlags;
            int doors = Math.Min(f.DoorCount, _doors.Length);
            for (int i = 0; i < doors; i++)
            {
                Component door = _doors[i];
                if (door == null)
                    continue;
                byte flags = doorFlags[f.DoorStart + i];
                bool open = (flags & 1) != 0;
                bool locked = (flags & 2) != 0;
                FieldAccess openField = _doorOpen[i];
                if (openField != null && openField.GetBool(door) != open)
                {
                    openField.SetBool(door, open);
                    _tickDoors++;
                }
                FieldAccess lockedField = _doorLocked[i];
                if (lockedField != null && lockedField.GetBool(door) != locked)
                {
                    lockedField.SetBool(door, locked);
                    _tickDoors++;
                }
            }
            byte[] roomBits = data.RoomBits;
            int rooms = Math.Min(f.RoomCount, _rooms.Length);
            for (int i = 0; i < rooms; i++)
            {
                GameObject room = _rooms[i];
                if (room == null)
                    continue;
                bool active = (roomBits[f.RoomStart + (i >> 3)] & (1 << (i & 7))) != 0;
                if (room.activeSelf != active)
                {
                    room.SetActive(active);
                    _tickDoors++;
                }
            }
        }

        void ResetEntityCaches()
        {
            _enemiesCached = false;
            _doorsCached = false;
            _projKeys.Clear();
            if (_retryProjectileTypes)
            {
                _retryProjectileTypes = false;
                _projTypes = null;
            }
            for (int i = 0; i < _projNextOrdinal.Length; i++)
                _projNextOrdinal[i] = 0;
            ClearLiveProjectiles();
        }

        // ------------------------------------------------------------------
        // Enforced playback and the drift detector
        // ------------------------------------------------------------------

        /// <summary>
        /// Writes frame <paramref name="index"/> of a decoded block over the game: RNG, player,
        /// enemies, projectiles (when their keys were followed) and doors/rooms. Allocation-free.
        /// Every value that had to be written is counted in the per-tick correction counters.
        /// <paramref name="enforcedTick"/>: an enforced playback tick (projectiles applied, the
        /// advanced correction threshold used); false for skip-to-end, which always snaps.
        /// </summary>
        void ApplyFrame(BakeBlockData data, int index, bool enforcedTick)
        {
            // Submission guard: from here on this run carries baked state (enforced or skip-to-end).
            s_runAppliedBakedState = true;
            float threshold = enforcedTick && CorrectionThreshold != null ? CorrectionThreshold.Value : 0f;
            // 0 (the default) = snap: only float noise below ApplyEpsilonSq is left alone.
            _thresholdSq = threshold > 0f && !float.IsInfinity(threshold) && threshold * threshold > ApplyEpsilonSq
                ? threshold * threshold
                : ApplyEpsilonSq;
            ref TickBakeFrame f = ref data.Frames[index];
            if (!_rngBroken)
            {
                try
                {
                    int s0, s1, s2, s3;
                    ReadRngBits(out s0, out s1, out s2, out s3);
                    if (s0 != f.Rng0 || s1 != f.Rng1 || s2 != f.Rng2 || s3 != f.Rng3)
                    {
                        WriteRngBits(f.Rng0, f.Rng1, f.Rng2, f.Rng3);
                        _tickRng++;
                    }
                }
                catch { _rngBroken = true; }
            }
            ApplySystemRng(data, index, ref f);
            ApplyPlayer(data, ref f);
            // A game hitstop (or any scale 0 from the game) holds time: enemies, projectiles, doors
            // and rooms do not move, animate or change on that tick, so the baked values for them
            // wait for the first tick that runs at the game's scale. Compared, not forced, each tick.
            if (enforcedTick && IsGameTimeStopped())
                return;
            ApplyEnemies(data, ref f);
            // Skip-to-end: projectile keys cannot be followed through ticks that never ran.
            if (enforcedTick)
                ApplyProjectiles(data, ref f);
            ApplyDoors(data, ref f);
        }

        /// <summary>
        /// True while the game holds its own clock: a game hitstop (TimeController stop) or
        /// Time.timeScale at 0. Read on the frame the tick ran, so it matches the scale the tick
        /// was simulated with.
        /// </summary>
        static bool IsGameTimeStopped()
        {
            return TimeControllerHitstopPatch.Holding || Time.timeScale <= 0.0001f;
        }

        // ------------------------------------------------------------------
        // Desync correction counters
        // ------------------------------------------------------------------

        void ResetCorrections(TasBakeBlock block)
        {
            BeginCorrectionTick();
            _corrLastTick = 0;
            _corrBlock = 0;
            _corrTicksApplied = 0;
            _corrTicksCorrected = 0;
            _corrMaxTick = 0;
            _corrMaxAt = -1;
            _corrRng = _corrPlayer = _corrEnemies = _corrNavRepaths = _corrProjectiles = _corrDoors = 0;
            _projRescans = 0;
            _corrLogged = false;
            _corrBlockName = block != null ? block.FileName : string.Empty;
            _corrVersion++;
        }

        void BeginCorrectionTick()
        {
            _tickRng = _tickPlayer = _tickEnemies = _tickNavRepaths = _tickProjectiles = _tickDoors = 0;
        }

        /// <summary>Folds one enforced tick's corrections into the block totals (no allocation).</summary>
        void EndCorrectionTick(int tick)
        {
            int count = _tickRng + _tickPlayer + _tickEnemies + _tickProjectiles + _tickDoors;
            _corrRng += _tickRng;
            _corrPlayer += _tickPlayer;
            _corrEnemies += _tickEnemies;
            _corrNavRepaths += _tickNavRepaths;
            _corrProjectiles += _tickProjectiles;
            _corrDoors += _tickDoors;
            _corrTicksApplied++;
            if (count > 0)
                _corrTicksCorrected++;
            if (count > _corrMaxTick)
            {
                _corrMaxTick = count;
                _corrMaxAt = tick;
            }
            _corrBlock += count;
            _corrLastTick = count;
            // The GUI text is rebuilt lazily, only when it is drawn after this.
            _corrVersion++;
            BeginCorrectionTick();
        }

        /// <summary>Logs the enforced block's correction totals once, when the block ends or is left.</summary>
        void LogCorrections(TasBakeBlock block, bool completed)
        {
            if (_corrLogged || _corrTicksApplied == 0)
                return;
            _corrLogged = true;
            _plugin.LogInfo("Bake: block '" + (block != null ? block.FileName : _corrBlockName) + "' enforced playback " +
                            (completed ? "finished" : "left early") + ": " + _corrTicksApplied + " tick(s) applied, " +
                            _corrBlock + " correction(s) on " + _corrTicksCorrected + " tick(s)" +
                            (_corrMaxAt >= 0 ? " (max " + _corrMaxTick + " at tick " + _corrMaxAt + ")" : string.Empty) +
                            "; RNG " + _corrRng + ", player " + _corrPlayer + ", enemies " + _corrEnemies +
                            " (NavMesh re-paths " + _corrNavRepaths + "), projectiles " + _corrProjectiles +
                            ", doors/rooms " + _corrDoors + "; projectile type rescans " + _projRescans + ".");
        }

        /// <summary>
        /// The drift detector: the live player and coins against the decoded frame. Allocates only
        /// when it reports (once per block). Coins were rescanned this tick (TrackProjectiles), so the
        /// live coin list and its order are those of a full scan.
        /// </summary>
        void VerifyFrame(BakeBlockData data, int index, TasBakeBlock block, int tick)
        {
            if (_driftReported)
                return;
            ref TickBakeFrame f = ref data.Frames[index];
            if (f.HasPlayer && EnsurePlayer())
            {
                Vector3 live = _playerTransform != null ? _playerTransform.position : _player.transform.position;
                float delta = Vector3.Distance(live, f.PlayerPosition);
                if (delta > VerifyTolerance)
                {
                    ReportDrift(block, tick, "player position is " + delta.ToString("0.######", CultureInfo.InvariantCulture) +
                                             " units off (live " + live.ToString("F4") + ", baked " +
                                             f.PlayerPosition.ToString("F4") + ")");
                    return;
                }
            }
            if (_coinType < 0 || (_projValidMask & TypeBit(_coinType)) == 0)
                return;
            // Baked coins by key (a coin key only ever matches a coin entry).
            BakeProjectile[] baked = data.Projectiles;
            int end = f.ProjectileStart + f.ProjectileCount;
            _frameProjIndex.Clear();
            int bakedCoins = 0;
            for (int e = f.ProjectileStart; e < end; e++)
            {
                if (baked[e].Type != _coinType)
                    continue;
                _frameProjIndex[baked[e].Key] = e;
                bakedCoins++;
            }
            int liveCoins = 0;
            for (int i = 0; i < _liveProjCount; i++)
            {
                if (_liveProjType[i] != _coinType)
                    continue;
                liveCoins++;
                int entry;
                if (!_frameProjIndex.TryGetValue(_liveProjKey[i], out entry))
                {
                    ReportDrift(block, tick, "a coin exists live that the bake does not have (key " + _liveProjKey[i] + ")");
                    return;
                }
                Vector3 live = _liveProjTransform[i].position;
                float delta = Vector3.Distance(live, baked[entry].Position);
                if (delta > VerifyTolerance)
                {
                    ReportDrift(block, tick, "coin " + (_liveProjKey[i] & 0xFFFFF) + " is " +
                                             delta.ToString("0.######", CultureInfo.InvariantCulture) +
                                             " units off (live " + live.ToString("F4") + ", baked " +
                                             baked[entry].Position.ToString("F4") + ")");
                    return;
                }
            }
            if (bakedCoins != liveCoins)
                ReportDrift(block, tick, "coin count " + liveCoins + " live vs " + bakedCoins + " baked");
        }

        void ReportDrift(TasBakeBlock block, int tick, string detail)
        {
            _driftReported = true;
            StatusText = StatusDrift;
            DetailText = "block '" + block.FileName + "': drift at tick " + tick;
            string message = "Bake drift in block '" + block.FileName + "' at tick " + tick +
                             " (block tick " + (tick - block.StartTick) + "): " + detail + ".";
            _plugin.LogWarning(message);
            _plugin.Toast(message, true);
        }

        // ------------------------------------------------------------------
        // Cached member access: resolved once per type, then compiled accessors per tick
        // ------------------------------------------------------------------

        FieldAccess NamedField(Type type, params string[] names)
        {
            if (type == null || names == null)
                return null;
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo field = _bridge.FindMember(type, names[i]) as FieldInfo;
                if (field != null)
                    return FieldAccess.For(field);
            }
            return null;
        }

        /// <summary>Every instance float field of a type, in name order (fixed between runs).</summary>
        static FieldAccess[] FloatFields(Type type)
        {
            List<FieldInfo> result = new List<FieldInfo>(16);
            for (Type current = type; current != null && current != typeof(MonoBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType == typeof(float) && !fields[i].IsInitOnly)
                        result.Add(fields[i]);
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.DeclaringType.Name + "." + a.Name, b.DeclaringType.Name + "." + b.Name));
            FieldAccess[] access = new FieldAccess[result.Count];
            for (int i = 0; i < access.Length; i++)
                access[i] = FieldAccess.For(result[i]);
            return access;
        }

        static float GetFloat(FieldAccess field, object target)
        {
            return field == null || target == null ? 0f : field.GetFloat(target);
        }

        static int GetInt(FieldAccess field, object target)
        {
            return field == null || target == null ? 0 : field.GetInt(target);
        }

        static bool GetBool(FieldAccess field, object target)
        {
            return field != null && target != null && field.GetBool(target);
        }

        static void SetFloat(FieldAccess field, object target, float value)
        {
            if (field != null && target != null)
                field.SetFloat(target, value);
        }

        /// <summary>
        /// One game field, read and written through delegates compiled once (DynamicMethod IL:
        /// castclass + ldfld/stfld) instead of FieldInfo.GetValue/SetValue, which box every value
        /// and cost a reflection call each. Conversions are exactly those of the reflection
        /// helpers they replace (float/int/double as float, int/float/enum as int, bool). If an
        /// accessor cannot be compiled, or its first call fails, it falls back to reflection for
        /// good, so a runtime without DynamicMethod support still works (just slower).
        /// </summary>
        sealed class FieldAccess
        {
            static readonly Dictionary<FieldInfo, FieldAccess> s_cache = new Dictionary<FieldInfo, FieldAccess>();

            readonly FieldInfo _field;
            readonly Type _type;
            bool _compiled;
            Func<object, float> _getFloat;
            Func<object, int> _getInt;
            Func<object, bool> _getBool;
            Action<object, float> _setFloat;
            Action<object, bool> _setBool;

            FieldAccess(FieldInfo field)
            {
                _field = field;
                _type = field.FieldType;
                try
                {
                    _compiled = Compile();
                }
                catch
                {
                    _compiled = false;
                }
            }

            /// <summary>The shared accessor of a field (compiled at most once per field).</summary>
            public static FieldAccess For(FieldInfo field)
            {
                if (field == null)
                    return null;
                FieldAccess access;
                if (!s_cache.TryGetValue(field, out access))
                {
                    access = new FieldAccess(field);
                    s_cache[field] = access;
                }
                return access;
            }

            /// <summary>The raw value through reflection (reference-type fields, recording only).</summary>
            public object GetValue(object target)
            {
                return _field.GetValue(target);
            }

            public float GetFloat(object target)
            {
                if (_compiled)
                {
                    try { return _getFloat(target); }
                    catch { _compiled = false; }
                }
                object value = _field.GetValue(target);
                if (value is float)
                    return (float)value;
                if (value is int)
                    return (int)value;
                if (value is double)
                    return (float)(double)value;
                return 0f;
            }

            public int GetInt(object target)
            {
                if (_compiled)
                {
                    try { return _getInt(target); }
                    catch { _compiled = false; }
                }
                object value = _field.GetValue(target);
                if (value is int)
                    return (int)value;
                if (value is float)
                    return (int)(float)value;
                if (value is Enum)
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return 0;
            }

            public bool GetBool(object target)
            {
                if (_compiled)
                {
                    try { return _getBool(target); }
                    catch { _compiled = false; }
                }
                object value = _field.GetValue(target);
                return value is bool && (bool)value;
            }

            public void SetFloat(object target, float value)
            {
                if (_compiled)
                {
                    try
                    {
                        _setFloat(target, value);
                        return;
                    }
                    catch { _compiled = false; }
                }
                if (_type == typeof(float))
                    _field.SetValue(target, value);
                else if (_type == typeof(int))
                    _field.SetValue(target, (int)Math.Round(value));
                else if (_type == typeof(double))
                    _field.SetValue(target, (double)value);
            }

            public void SetBool(object target, bool value)
            {
                if (_type != typeof(bool))
                    return;
                if (_compiled)
                {
                    try
                    {
                        _setBool(target, value);
                        return;
                    }
                    catch { _compiled = false; }
                }
                _field.SetValue(target, value);
            }

            bool Compile()
            {
                FieldInfo field = _field;
                Type owner = field.DeclaringType;
                if (field.IsStatic || field.IsLiteral || field.IsInitOnly || owner == null || owner.IsValueType ||
                    owner.ContainsGenericParameters)
                    return false;
                Type type = _type;
                Type underlying = type.IsEnum ? Enum.GetUnderlyingType(type) : null;
                Module module = typeof(BakeManager).Module;
                Type[] getArgs = { typeof(object) };

                // float GetFloat(object): float / int / double fields, else 0.
                DynamicMethod method = new DynamicMethod("BakeGetFloat_" + field.Name, typeof(float), getArgs, module, true);
                ILGenerator il = method.GetILGenerator();
                if (type == typeof(float) || type == typeof(int) || type == typeof(double))
                {
                    EmitLoadField(il, owner, field);
                    if (type != typeof(float))
                        il.Emit(OpCodes.Conv_R4);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_R4, 0f);
                }
                il.Emit(OpCodes.Ret);
                _getFloat = (Func<object, float>)method.CreateDelegate(typeof(Func<object, float>));

                // int GetInt(object): int, float (truncated), enum (checked like Convert.ToInt32), else 0.
                method = new DynamicMethod("BakeGetInt_" + field.Name, typeof(int), getArgs, module, true);
                il = method.GetILGenerator();
                if (type == typeof(int))
                {
                    EmitLoadField(il, owner, field);
                }
                else if (type == typeof(float))
                {
                    EmitLoadField(il, owner, field);
                    il.Emit(OpCodes.Conv_I4);
                }
                else if (underlying != null)
                {
                    EmitLoadField(il, owner, field);
                    bool unsigned = underlying == typeof(byte) || underlying == typeof(ushort) ||
                                    underlying == typeof(uint) || underlying == typeof(ulong);
                    il.Emit(unsigned ? OpCodes.Conv_Ovf_I4_Un : OpCodes.Conv_Ovf_I4);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                }
                il.Emit(OpCodes.Ret);
                _getInt = (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));

                // bool GetBool(object): bool fields, else false.
                method = new DynamicMethod("BakeGetBool_" + field.Name, typeof(bool), getArgs, module, true);
                il = method.GetILGenerator();
                if (type == typeof(bool))
                    EmitLoadField(il, owner, field);
                else
                    il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                _getBool = (Func<object, bool>)method.CreateDelegate(typeof(Func<object, bool>));

                // void SetFloat(object, float): float, int ((int)Math.Round), double; other types untouched.
                method = new DynamicMethod("BakeSetFloat_" + field.Name, typeof(void),
                                           new[] { typeof(object), typeof(float) }, module, true);
                il = method.GetILGenerator();
                if (type == typeof(float) || type == typeof(int) || type == typeof(double))
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, owner);
                    il.Emit(OpCodes.Ldarg_1);
                    if (type == typeof(int))
                    {
                        il.Emit(OpCodes.Conv_R8);
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Round", new[] { typeof(double) }));
                        il.Emit(OpCodes.Conv_I4);
                    }
                    else if (type == typeof(double))
                    {
                        il.Emit(OpCodes.Conv_R8);
                    }
                    il.Emit(OpCodes.Stfld, field);
                }
                il.Emit(OpCodes.Ret);
                _setFloat = (Action<object, float>)method.CreateDelegate(typeof(Action<object, float>));

                // void SetBool(object, bool): bool fields only.
                method = new DynamicMethod("BakeSetBool_" + field.Name, typeof(void),
                                           new[] { typeof(object), typeof(bool) }, module, true);
                il = method.GetILGenerator();
                if (type == typeof(bool))
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, owner);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, field);
                }
                il.Emit(OpCodes.Ret);
                _setBool = (Action<object, bool>)method.CreateDelegate(typeof(Action<object, bool>));
                return true;
            }

            static void EmitLoadField(ILGenerator il, Type owner, FieldInfo field)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, owner);
                il.Emit(OpCodes.Ldfld, field);
            }
        }
    }
}
