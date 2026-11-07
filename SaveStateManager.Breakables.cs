using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// One map object the game destroys when it breaks, and an intact copy to rebuild it from.
    /// Breakable.Break(float) ends with Object.Destroy(gameObject) and destroys every
    /// destroyOnBreak entry; Glass.Shatter() destroys the glass panes under the Glass, disables
    /// its colliders, removes its BloodstainParent and moves it to layer 17. A destroyed object
    /// cannot be brought back from references, so the first capture that sees one intact keeps
    /// an inactive clone of it.
    /// </summary>
    public sealed class RebuildRecord
    {
        /// <summary>Inactive intact clone under the backup holder (Awake never ran on it).</summary>
        public GameObject Backup;
        /// <summary>The live instance: the scene original, or the last copy rebuilt from Backup.</summary>
        public GameObject Current;
        /// <summary>Breakable or Glass; null for a plain destroyOnBreak target.</summary>
        public Type ComponentType;
        public Transform Parent;
        public bool HadParent;
        public int SiblingIndex;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;
        public string Name;
        public Scene Scene;
        /// <summary>Objects the element switches when it breaks (activateOnBreak, destroyEvent, onShatter).</summary>
        public List<GameObject> Targets = new List<GameObject>();
        /// <summary>Child rigidbodies of Current; Breakable.Break detaches them as debris.</summary>
        public List<GameObject> Debris = new List<GameObject>();
    }

    /// <summary>What one rebuildable map object looked like at capture.</summary>
    public sealed class RebuildState
    {
        public RebuildRecord Record;
        /// <summary>The object existed (was not destroyed) at capture.</summary>
        public bool Alive;
        /// <summary>The Breakable/Glass "broken" flag at capture (true when not alive).</summary>
        public bool Broken;
    }

    public sealed partial class SaveStateManager
    {
        /// <summary>Upper bound on intact clones kept per level (glass panes, crates, their targets).</summary>
        /// <summary>Safety limit only (each record keeps an inactive intact copy); reaching it is logged.</summary>
        const int MaxRebuildRecords = 100000;

        Type _breakableType;
        Type _glassType;
        readonly List<RebuildRecord> _rebuilds = new List<RebuildRecord>(64);
        /// <summary>Instance id of every object that ever stood for a record (original and rebuilt copies).</summary>
        readonly Dictionary<int, RebuildRecord> _rebuildById = new Dictionary<int, RebuildRecord>();
        /// <summary>Objects replaced or removed by the current restore; their saved values are not applied.</summary>
        readonly HashSet<GameObject> _retired = new HashSet<GameObject>();
        /// <summary>Copies rebuilt by the current restore; their fields are re-applied after Start.</summary>
        readonly HashSet<GameObject> _rebuiltNow = new HashSet<GameObject>();
        GameObject _rebuildHolder;
        int _rebuildScene = -1;
        bool _warnedRebuildCap;
        bool _warnedBrokenField;

        void ResolveRebuildTypes()
        {
            _breakableType = _bridge.FindType("Breakable");
            _glassType = _bridge.FindType("Glass");
            if (_breakableType == null)
                _plugin.LogWarning("Type Breakable not found; broken breakables will not come back on a savestate load.");
            if (_glassType == null)
                _plugin.LogWarning("Type Glass not found; shattered glass will not come back on a savestate load.");
        }

        bool IsRebuildType(Type type)
        {
            return type != null && (type == _breakableType || type == _glassType);
        }

        /// <summary>
        /// The live replacement of a tracked object, by the instance id it had when the state
        /// was taken. Lets an entity ID stay on a crate or glass that was rebuilt after a load
        /// instead of naming the instance the restore destroyed.
        /// </summary>
        Component LookupRebuiltComponent(int objectId, Type type)
        {
            if (type == null || objectId == 0)
                return null;
            RebuildRecord record;
            if (!_rebuildById.TryGetValue(objectId, out record))
                return null;
            GameObject current = record.Current;
            return current != null ? current.GetComponent(type) : null;
        }

        /// <summary>True for the inactive clones this class keeps; level scans must skip them.</summary>
        bool IsRebuildBackup(Component component)
        {
            return _rebuildHolder != null && component != null && component.transform.root == _rebuildHolder.transform;
        }

        /// <summary>Records belong to one level instance; a new active scene drops them (their holder went with the old scene).</summary>
        void EnsureRebuildScene()
        {
            int handle = SceneManager.GetActiveScene().handle;
            if (handle == _rebuildScene)
                return;
            _rebuildScene = handle;
            for (int i = 0; i < _rebuilds.Count; i++)
            {
                if (_rebuilds[i].Backup != null)
                    UnityEngine.Object.Destroy(_rebuilds[i].Backup);
            }
            _rebuilds.Clear();
            _rebuildById.Clear();
            if (_rebuildHolder != null)
                UnityEngine.Object.Destroy(_rebuildHolder);
            _rebuildHolder = null;
            _warnedRebuildCap = false;
        }

        GameObject RebuildHolder()
        {
            if (_rebuildHolder == null)
            {
                // Created in the active scene and inactive: clones under it never Awake, never
                // render, and are unloaded with the level.
                _rebuildHolder = new GameObject("[TAS rebuild backups]");
                _rebuildHolder.SetActive(false);
            }
            return _rebuildHolder;
        }

        bool ReadBroken(Component component)
        {
            if (component == null)
                return true;
            System.Reflection.FieldInfo field = NamedField(component.GetType(), "broken");
            if (field == null || field.FieldType != typeof(bool))
            {
                if (!_warnedBrokenField)
                {
                    _warnedBrokenField = true;
                    _plugin.LogWarning(component.GetType().Name + ".broken not found; it is treated as intact for savestates.");
                }
                return false;
            }
            try { return (bool)field.GetValue(component); }
            catch { return false; }
        }

        bool IsBrokenNow(RebuildRecord record)
        {
            GameObject current = record.Current;
            if (current == null)
                return true;
            if (record.ComponentType == null)
                return false;
            Component component = current.GetComponent(record.ComponentType);
            return component == null || ReadBroken(component);
        }

        /// <summary>Called for each Breakable/Glass the level scan finds: keeps an intact clone the first time.</summary>
        void TrackRebuildable(Component component)
        {
            if (component == null || !IsRebuildType(component.GetType()))
                return;
            GameObject root = component.gameObject;
            if (_rebuildById.ContainsKey(root.GetInstanceID()) || ReadBroken(component))
                return;
            RebuildRecord record = CreateRebuildRecord(root, component.GetType());
            if (record == null)
                return;

            HashSet<GameObject> targets = new HashSet<GameObject>();
            CollectTargetObjects(component, targets, 0);
            record.Targets.AddRange(targets);

            // destroyOnBreak entries outside the breakable are destroyed by Break too.
            GameObject[] destroyed = GetReferenceArray<GameObject>(component, "destroyOnBreak");
            for (int i = 0; i < destroyed.Length; i++)
            {
                GameObject go = destroyed[i];
                if (go == null || !go.scene.IsValid() || go.transform.IsChildOf(root.transform) ||
                    _rebuildById.ContainsKey(go.GetInstanceID()))
                    continue;
                // A target that is itself a Breakable or Glass keeps its own broken flag.
                Type type = null;
                if (_breakableType != null && go.GetComponent(_breakableType) != null)
                    type = _breakableType;
                else if (_glassType != null && go.GetComponent(_glassType) != null)
                    type = _glassType;
                CreateRebuildRecord(go, type);
            }
        }

        RebuildRecord CreateRebuildRecord(GameObject go, Type componentType)
        {
            if (_rebuilds.Count >= MaxRebuildRecords)
            {
                if (!_warnedRebuildCap)
                {
                    _warnedRebuildCap = true;
                    _plugin.LogWarning("More than " + MaxRebuildRecords + " breakable objects in this level; the rest will not come back on a savestate load.");
                }
                return null;
            }
            // An enemy inside the copy would be seen by the enemy scans; such objects are left out.
            if (_identifierType != null && go.GetComponentInChildren(_identifierType, true) != null)
                return null;
            try
            {
                Transform transform = go.transform;
                GameObject backup = UnityEngine.Object.Instantiate(go, RebuildHolder().transform, false);
                backup.name = go.name;
                RebuildRecord record = new RebuildRecord
                {
                    Backup = backup,
                    Current = go,
                    ComponentType = componentType,
                    Parent = transform.parent,
                    HadParent = transform.parent != null,
                    SiblingIndex = transform.GetSiblingIndex(),
                    LocalPosition = transform.localPosition,
                    LocalRotation = transform.localRotation,
                    LocalScale = transform.localScale,
                    Name = go.name,
                    Scene = go.scene
                };
                CollectDebris(record, go);
                _rebuilds.Add(record);
                _rebuildById[go.GetInstanceID()] = record;
                return record;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Could not keep an intact copy of " + go.name + ": " + ex.Message);
                return null;
            }
        }

        static void CollectDebris(RebuildRecord record, GameObject root)
        {
            record.Debris.Clear();
            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null && bodies[i].gameObject != root)
                    record.Debris.Add(bodies[i].gameObject);
            }
        }

        /// <summary>Stores the broken/intact/destroyed state of every tracked object and keeps its targets' flags.</summary>
        void CaptureRebuilds(Savestate state, HashSet<GameObject> targets)
        {
            for (int i = 0; i < _rebuilds.Count; i++)
            {
                RebuildRecord record = _rebuilds[i];
                bool alive = record.Current != null;
                state.Rebuilds.Add(new RebuildState
                {
                    Record = record,
                    Alive = alive,
                    Broken = IsBrokenNow(record)
                });
                // The breakable is gone after it breaks, so its targets are collected from the record.
                for (int t = 0; t < record.Targets.Count; t++)
                    AddTarget(record.Targets[t], targets);
            }
        }

        /// <summary>
        /// Puts every tracked object back to its captured condition before any other level value is
        /// written: rebuilt from the intact copy when it was intact at capture and is broken or gone
        /// now, removed when it was gone at capture, and shattered through Glass.Shatter when a glass
        /// was broken at capture but is intact now.
        /// </summary>
        void RestoreRebuilds(Savestate state, ref int restored)
        {
            _retired.Clear();
            _rebuiltNow.Clear();
            if (state.Rebuilds.Count == 0)
                return;
            EnsureRebuildScene();
            for (int i = 0; i < state.Rebuilds.Count; i++)
            {
                RebuildState saved = state.Rebuilds[i];
                RebuildRecord record = saved.Record;
                if (record == null)
                    continue;
                try
                {
                    bool brokenNow = IsBrokenNow(record);
                    if (saved.Alive && !saved.Broken)
                    {
                        if (!brokenNow)
                            continue;
                        Retire(record.Current);
                        if (Rebuild(record))
                            restored++;
                    }
                    else if (!saved.Alive || record.ComponentType != _glassType)
                    {
                        // Gone at capture, or a Breakable broken on the capture frame (its
                        // Destroy was already queued): it must not exist after the load.
                        if (record.Current != null)
                            Retire(record.Current);
                    }
                    else if (!brokenNow)
                    {
                        // A glass stays in the scene when it shatters; the game's own Shatter
                        // makes it broken again. The saved fields and flags are applied after.
                        Component glass = record.Current.GetComponent(record.ComponentType);
                        if (!TryInvoke(glass, "Shatter"))
                            _plugin.LogWarning("Glass.Shatter not found; a glass broken at the save stays intact.");
                    }
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Breakable restore skipped for " + record.Name + ": " + ex.Message);
                }
            }
        }

        void Retire(GameObject go)
        {
            if (go == null)
                return;
            _retired.Add(go);
            try { go.SetActive(false); }
            catch { }
            UnityEngine.Object.Destroy(go);
        }

        bool Rebuild(RebuildRecord record)
        {
            if (record.Backup == null)
            {
                _plugin.LogWarning("No intact copy of " + record.Name + " is left; it cannot be rebuilt.");
                return false;
            }
            GameObject instance = UnityEngine.Object.Instantiate(record.Backup, RebuildHolder().transform, false);
            instance.name = record.Name;
            Transform transform = instance.transform;
            Transform parent = record.Parent;
            if (record.HadParent && parent == null)
                _plugin.LogWarning("The parent of " + record.Name + " is gone; it is rebuilt at the scene root.");
            // Leaving the inactive holder is what wakes the copy (Awake/OnEnable run here).
            transform.SetParent(parent, false);
            if (parent == null && record.Scene.IsValid() && record.Scene.isLoaded && instance.scene != record.Scene)
                SceneManager.MoveGameObjectToScene(instance, record.Scene);
            transform.localPosition = record.LocalPosition;
            transform.localRotation = record.LocalRotation;
            transform.localScale = record.LocalScale;
            try { transform.SetSiblingIndex(record.SiblingIndex); }
            catch { }

            // Debris the old copy dropped belongs to the discarded timeline.
            for (int i = 0; i < record.Debris.Count; i++)
            {
                if (record.Debris[i] != null)
                    UnityEngine.Object.Destroy(record.Debris[i]);
            }
            CollectDebris(record, instance);

            record.Current = instance;
            _rebuildById[instance.GetInstanceID()] = record;
            _rebuiltNow.Add(instance);
            if (record.ComponentType != null)
            {
                Component component = instance.GetComponent(record.ComponentType);
                if (component != null && !_levelCache.Contains(component))
                    _levelCache.Add(component);
            }
            return true;
        }

        /// <summary>
        /// The live object a saved level element now stands for. A rebuilt copy replaces the
        /// object the state was captured with; the element's references are moved to it.
        /// Returns null when the element was removed on purpose by this restore.
        /// </summary>
        GameObject ResolveRebuiltElement(LevelElementState element)
        {
            GameObject root = element.Root;
            if (root != null && _retired.Contains(root))
                root = null;
            RebuildRecord record;
            if (root != null || !_rebuildById.TryGetValue(element.RootId, out record))
                return root;
            GameObject current = record.Current;
            if (current == null || _retired.Contains(current))
                return null;

            element.Root = current;
            ComponentSnapshot snapshot = element.Component;
            if (snapshot != null && (object)snapshot.Component != null)
            {
                Component replacement = current.GetComponent(snapshot.Component.GetType());
                if (replacement != null)
                    snapshot.Component = replacement;
            }
            if (element.Body != null)
                element.Body.Body = current.GetComponent<Rigidbody>();
            return current;
        }

        /// <summary>
        /// Start() of a rebuilt Breakable runs on the next frame and re-reads defaultHeight and
        /// originalBounceHealth; the saved fields are applied once more after it.
        /// </summary>
        void FinalizeRebuilt(GameObject root, ComponentSnapshot snapshot)
        {
            if (root == null || snapshot == null || !_rebuiltNow.Contains(root))
                return;
            try
            {
                RespawnFinalizer finalizer = root.AddComponent<RespawnFinalizer>();
                Component target = snapshot.Component;
                List<FieldState> fields = snapshot.Fields;
                finalizer.Reapply = () =>
                {
                    if (target != null)
                        ApplyFields(target, fields);
                };
            }
            catch
            {
            }
        }
    }
}
