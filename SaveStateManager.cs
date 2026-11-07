using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.AI;
using UnityRandom = UnityEngine.Random;

namespace UltrakillTAS
{
    public struct SaveResult
    {
        public bool Success;
        public string Message;
        public int Restored;
        public int Missing;

        public static SaveResult Fail(string message)
        {
            return new SaveResult { Success = false, Message = message };
        }

        public static SaveResult Ok(string message, int restored, int missing)
        {
            return new SaveResult { Success = true, Message = message, Restored = restored, Missing = missing };
        }
    }

    /// <summary>
    /// One field of a game object, with the FieldInfo resolved at capture time so
    /// a restore does not pay for a reflection lookup again.
    /// </summary>
    public enum FieldKind
    {
        /// <summary>Primitive, enum, string or Unity math struct: stored and written back as-is.</summary>
        Plain,
        /// <summary>ULTRAKILL's TimeSince struct: an absolute Time.time stamp that must be rebased on load.</summary>
        TimeSince,
        /// <summary>One-dimensional array of primitives: cloned on capture, copied element-wise on restore.</summary>
        Array,
        /// <summary>A reference or Nullable field that was null at capture: written back as null.</summary>
        Null,
        /// <summary>
        /// Collection, Nullable, AnimationCurve, array of objects or plain class/struct: Value is a
        /// captured value tree (see SaveStateManager.CaptureDeep), restored in place.
        /// </summary>
        Deep,
        /// <summary>float/double absolute time stamp: shifted by the delta of its clock on restore.</summary>
        Rebased
    }

    /// <summary>Clock an absolute time stamp was taken from; decides the delta a restore adds.</summary>
    public enum TimeClock : byte
    {
        /// <summary>Time.time / Time.timeSinceLevelLoad (TimeSince).</summary>
        Scaled,
        /// <summary>Time.unscaledTime (UnscaledTimeSince).</summary>
        Unscaled,
        /// <summary>InputState.currentTime / InputAction.CallbackContext.time (NewMovement.*Timestamp).</summary>
        Input
    }

    public sealed class FieldState
    {
        public FieldInfo Field;
        public string Name;
        public object Value;
        public FieldKind Kind;
        /// <summary>Clock of a Rebased field.</summary>
        public TimeClock Clock;
        /// <summary>
        /// Object the Deep value was read from. Applying the state to another object (a rebuilt
        /// breakable, a projectile respawned from its template) fills that object's own
        /// collections instead of handing it the captured instances.
        /// </summary>
        public object Owner;
    }

    /// <summary>
    /// Everything we keep about one MonoBehaviour: enabled flag, primitive/TimeSince/array
    /// fields and live object references (targets, weapons).
    /// </summary>
    public sealed class ComponentSnapshot
    {
        public Component Component;
        public bool HasEnabled;
        public bool Enabled;
        public List<FieldState> Fields;
        public List<ObjectFieldState> References;
        /// <summary>Every delayed Invoke this script can schedule, and those pending at capture.</summary>
        public InvokeInfo[] KnownInvokes;
        /// <summary>Unity-object list fields (a Magnet's sawblades, stuck enemies, connected magnets).</summary>
        public List<ListRefState> ListRefs;
        public List<InvokeInfo> PendingInvokes;
        /// <summary>Effect fields (slide dust, charge glow) that were empty at capture.</summary>
        public List<FieldInfo> EmptyEffects;
    }

    public sealed class ActiveFlag
    {
        public GameObject Object;
        public bool Active;
    }

    public sealed class ColliderFlag
    {
        public Collider Collider;
        public bool Enabled;
    }

    public sealed class ParticleSnapshot
    {
        public ParticleSystem System;
        public bool Playing;
        public bool Paused;
        public float Time;
    }

    public sealed class AudioSnapshot
    {
        public AudioSource Source;
        public bool Playing;
        public float Time;
        public float Pitch;
        public float Volume;
    }

    /// <summary>A map element (trigger, platform, elevator, breakable): active flag, pose, fields.</summary>
    public sealed class LevelElementState
    {
        public ComponentSnapshot Component;
        public GameObject Root;
        /// <summary>Instance id of Root at capture; finds the rebuilt copy of a destroyed breakable.</summary>
        public int RootId;
        public bool Active;
        public Transform Parent;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public BodyState Body;
        public List<AnimatorState> Animators;
        /// <summary>Trigger volumes switch their collider instead of their object, so it is saved too.</summary>
        public List<ColliderFlag> Colliders;
    }

    /// <summary>
    /// A UnityEngine.Object reference (Animator, NavMesh target, weapon, ...).
    /// Stored separately from primitive fields because it is restored only while
    /// the referenced object is still alive.
    /// </summary>
    public sealed class ObjectFieldState
    {
        public FieldInfo Field;
        public UnityEngine.Object Value;
        /// <summary>A plain C# reference (an enemy's EnemyTarget); restored while non-null.</summary>
        public object Plain;
        /// <summary>
        /// The field was empty at capture (a hook that had caught nothing, no held item, no
        /// target) and is cleared again on restore. Only recorded for components that were
        /// active and enabled, so a cache that Awake/Start fills later is never cleared.
        /// </summary>
        public bool WasNull;
    }

    public sealed class AnimatorParameterState
    {
        public int NameHash;
        public AnimatorControllerParameterType Type;
        public float FloatValue;
        public int IntValue;
        public bool BoolValue;
    }

    /// <summary>Layer state plus parameters of a single Animator.</summary>
    public sealed class AnimatorState
    {
        public Animator Animator;
        public bool Enabled = true;
        public float Speed = 1f;
        public int LayerCount;
        public int[] StateHashes;
        public float[] NormalizedTimes;
        public float[] LayerWeights;
        public List<AnimatorParameterState> Parameters;
    }

    /// <summary>Only ever created for agents that are active and on the NavMesh.</summary>
    public sealed class AgentState
    {
        public NavMeshAgent Agent;
        public Vector3 Position;
        public Vector3 Destination;
        public Vector3 Velocity;
        public bool IsStopped;
        public bool HasPath;
        public float Speed;
        public float Acceleration;
        public float AngularSpeed;
        public float StoppingDistance;
        public bool AutoBraking;
        public bool UpdatePosition;
        public bool UpdateRotation;
    }

    public sealed class BodyState
    {
        public Rigidbody Body;
        public bool IsKinematic;
        public bool UseGravity;
        public bool DetectCollisions;
        public RigidbodyConstraints Constraints;
        public float Drag;
        public float AngularDrag;
        public bool Sleeping;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    public sealed class PlayerState
    {
        public bool HasPlayer;
        /// <summary>The player was dead when captured (a load then keeps the death screen).</summary>
        public bool Dead;
        /// <summary>Enabled flag, references, pending Invokes and late effects of NewMovement itself.</summary>
        public ComponentSnapshot Extra;
        public GameObject Root;
        public Transform Transform;
        public Component Movement;
        public Vector3 Position;
        public Quaternion Rotation;
        public BodyState Body;
        public int GunSlot = -1;
        public int GunVariation = -1;
        public float CameraPitch;
        public float CameraYaw;
        public float Health;
        public float HardDamage;
        public float Stamina;
        public List<FieldState> Fields;
        public List<AnimatorState> Animators;
        public List<AgentState> Agents;
    }

    public sealed class EnemyState
    {
        public Component Identifier;
        public GameObject Root;
        public string IdentifierType;
        public Vector3 Position;
        public Quaternion Rotation;
        public BodyState Body;
        public bool HasHealth;
        public float Health;
        public bool Dead;
        public float Bleed;
        public List<FieldState> Fields;
        public List<ObjectFieldState> References;
        public AnimatorState Animator;
        public AgentState Agent;
        public List<ComponentSnapshot> Behaviours;
        public List<ListRefState> ListRefs;
        /// <summary>Animators beyond the primary one (many enemies animate several rigs).</summary>
        public List<AnimatorState> ExtraAnimators;
        public List<ColliderFlag> Colliders;
        public List<ParticleSnapshot> Particles;
        public bool ActiveSelf = true;
        public bool HasAgentComponent;
        public bool AgentEnabled;
        /// <summary>Dead or not yet spawned: only identity, pose, active flag and identifier fields are kept.</summary>
        public bool Lightweight;
    }

    /// <summary>
    /// Fixed-capacity circular buffer of per-tick snapshots (BizHawk style).
    /// Pushing at a tick discards any newer entries, so the buffer always describes
    /// one linear timeline even after a branch.
    /// </summary>
    public sealed class SnapshotRing
    {
        readonly Savestate[] _states;
        readonly int[] _ticks;
        int _newest = -1;
        int _count;

        public SnapshotRing(int capacity)
        {
            if (capacity < 1)
                capacity = 1;
            _states = new Savestate[capacity];
            _ticks = new int[capacity];
        }

        public int Capacity { get { return _states.Length; } }
        public int Count { get { return _count; } }

        public void Clear()
        {
            Array.Clear(_states, 0, _states.Length);
            _newest = -1;
            _count = 0;
        }

        public void Push(int tick, Savestate state)
        {
            if (state == null)
                return;
            DiscardFrom(tick);
            _newest = (_newest + 1) % _states.Length;
            _states[_newest] = state;
            _ticks[_newest] = tick;
            if (_count < _states.Length)
                _count++;
        }

        /// <summary>Removes every entry at or after <paramref name="tick"/>.</summary>
        public void DiscardFrom(int tick)
        {
            while (_count > 0 && _ticks[_newest] >= tick)
                PopNewest();
        }

        /// <summary>Pops and returns the newest snapshot taken strictly before a tick.</summary>
        public Savestate PopBefore(int tick, out int snapshotTick)
        {
            DiscardFrom(tick);
            snapshotTick = -1;
            if (_count == 0)
                return null;
            snapshotTick = _ticks[_newest];
            Savestate state = _states[_newest];
            PopNewest();
            return state;
        }

        void PopNewest()
        {
            _states[_newest] = null;
            _newest = (_newest - 1 + _states.Length) % _states.Length;
            _count--;
            if (_count == 0)
                _newest = -1;
        }
    }

    public sealed class DoorState
    {
        public Component Door;
        public GameObject Root;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Open;
        public bool Locked;
        public bool FullyOpened;
        public bool FullyClosed;
        public bool InPos;
        public int Requests;
        public int OpenLocks;
        public Vector3 TargetPos;
        public bool HasRigidbody;
        public bool RigidbodyKinematic;
        public Vector3 RigidbodyPosition;
        public Quaternion RigidbodyRotation;
        public bool HasNoPass;
        public bool NoPassActive;
        public bool HasObstacle;
        public bool ObstacleEnabled;
        public bool HasLight;
        public bool LightEnabled;
        public List<AnimatorState> Handles = new List<AnimatorState>();
        /// <summary>All primitive/TimeSince fields of the Door itself (speed, ease, lock bookkeeping).</summary>
        public ComponentSnapshot Generic;
        /// <summary>BigDoor / SubDoor leaves the Door animates: local pose plus their own fields.</summary>
        public List<DoorPart> Parts = new List<DoorPart>();
        /// <summary>Animators, colliders, Behaviour enabled flags and light colour of the door (see SaveStateManager.Doors.cs).</summary>
        public List<AnimatorState> RootAnimators;
        public List<ColliderFlag> RootColliders;
        public List<BehaviourFlag> RootBehaviours;
        public bool HasLightColor;
        public Color LightColor;
    }

    public sealed class DoorPart
    {
        public Component Component;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public ComponentSnapshot Snapshot;
    }

    public sealed class ProjectileState
    {
        public Component Component;
        public GameObject Root;
        public string TypeName;
        public Type ComponentType;
        public string PrefabName;
        /// <summary>Deactivated clone kept alive so a destroyed projectile can be rebuilt.</summary>
        public GameObject Template;
        public List<ObjectFieldState> References;
        public List<ListRefState> ListRefs;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LocalScale;
        public BodyState Body;
        public int Ricochets;
        public int HitTimes;
        public float Lifetime;
        public List<FieldState> Fields;
        /// <summary>Coin-only state (null for every other projectile type).</summary>
        public CoinState Coin;
    }

    /// <summary>
    /// Explicit Coin state. The generic projectile path already copies the Coin's
    /// fields, but these are the ones that decide punch/split/railcoin behaviour and
    /// the lifetime, so they are read and written by name, applied after the game's
    /// Start() has run, and paired with the SphereCollider that beams hit.
    /// Field names are probed in order because they differ between game versions;
    /// the binary contains "splitcoinable" for the split window, not "splittable".
    /// </summary>
    public sealed class CoinState
    {
        public static readonly string[] TimerNames = { "timer", "coinTimer", "lifetime", "life" };
        public static readonly string[] HitNames = { "hits", "hitTimes", "hitAmount", "ricochets" };
        public static readonly string[] ShotNames = { "shot", "wasShot", "hasBeenShot" };
        public static readonly string[] SplitNames = { "splitcoinable", "splittable", "splitable", "canSplit" };
        public static readonly string[] AltNames = { "alt", "isAlt", "altCoin", "alternate" };
        public static readonly string[] TargetNames = { "target", "currentTarget", "targetEnemy", "lockedTarget" };

        public float Timer;
        public int Hits;
        public bool Shot;
        public bool Splittable;
        public bool Alt;
        /// <summary>Target reference (Transform or EnemyIdentifier) when the coin was tracking one.</summary>
        public UnityEngine.Object Target;
        public bool HasCollider;
        public bool ColliderEnabled;
        public float ColliderRadius;
        public Vector3 ColliderCenter;
    }

    /// <summary>Small non-player systems (GunControl, WeaponHUD, StatsManager).</summary>
    public sealed class AuxiliaryState
    {
        public Component Component;
        public string TypeName;
        public List<FieldState> Fields;
        /// <summary>Object references (StatsManager.currentCheckPoint, ...), including empty ones.</summary>
        public List<ObjectFieldState> References;
    }

    /// <summary>One captured moment. References are resolved by live reference or instance id.</summary>
    public sealed class Savestate
    {
        public string Label = string.Empty;
        public string SceneName = string.Empty;
        public DateTime CreatedUtc;
        public int Tick;
        public double ScriptTime;
        public double CaptureMilliseconds;

        /// <summary>Time.time at capture; TimeSince stamps are shifted by (now - this) on restore.</summary>
        public float GameTime;
        /// <summary>Time.unscaledTime at capture; UnscaledTimeSince stamps are shifted by its delta.</summary>
        public float UnscaledTime;
        /// <summary>InputState.currentTime at capture; NewMovement's slide/jump input stamps use this clock.</summary>
        public double InputTime;

        /// <summary>
        /// Per-area state of the savestate modules (SaveStateManager.&lt;Area&gt;.cs), keyed by the
        /// area name ("Timing", "Enemies2", ...). See SaveStateManager.Modules.cs for the contract.
        /// </summary>
        public Dictionary<string, object> Ext = new Dictionary<string, object>();

        /// <summary>Opt-in static fields (SceneHelper scene names, Explosion.globalSizeMulti, ...).</summary>
        public List<FieldState> StaticFields = new List<FieldState>();

        /// <summary>
        /// Loose gameplay scripts no other category owns (fire zones, burning voxels): fields,
        /// references and pending Invokes only. They are never destroyed or rebuilt by a load.
        /// </summary>
        public List<ComponentSnapshot> ExtraComponents = new List<ComponentSnapshot>();

        /// <summary>Captured in the rolling history's light mode: collections and plain objects were skipped.</summary>
        public bool Light;

        /// <summary>Every component of the player rig (weapons, fists, camera, ground checks, hook).</summary>
        public List<ComponentSnapshot> PlayerParts = new List<ComponentSnapshot>();
        public List<ActiveFlag> PlayerActive = new List<ActiveFlag>();
        public List<ParticleSnapshot> PlayerParticles = new List<ParticleSnapshot>();
        public List<AudioSnapshot> PlayerAudio = new List<AudioSnapshot>();
        public List<ColliderFlag> PlayerColliders = new List<ColliderFlag>();

        /// <summary>Names of the scenes loaded at capture (the level plus any additive scenes).</summary>
        public List<string> LoadedScenes = new List<string>();

        /// <summary>Triggers, arenas, platforms, elevators, breakables: map state beyond doors.</summary>
        public List<LevelElementState> LevelElements = new List<LevelElementState>();

        /// <summary>
        /// Animators on the objects those triggers switch on: the prop that starts moving, the
        /// actor that walks out of the glass. Cutscene motion lives there, not on the trigger.
        /// </summary>
        public List<AnimatorState> LevelAnimators = new List<AnimatorState>();

        /// <summary>
        /// Every assigned entity ID and the object it names, so look_at survives a load. Shared
        /// with the registry while its numbering is unchanged, which keeps per-tick captures free.
        /// </summary>
        public EntityRegistry.EntityIdSnapshot EntityIds;

        /// <summary>Intact / broken / destroyed state of every Breakable and Glass (see SaveStateManager.Breakables.cs).</summary>
        public List<RebuildState> Rebuilds = new List<RebuildState>();

        /// <summary>Hurt screen, HP flashes and the screen-blood overlays (the red damage effect).</summary>
        public List<GraphicState> Graphics = new List<GraphicState>();
        public List<ComponentSnapshot> HudParts = new List<ComponentSnapshot>();
        public string BloodOverlayName = string.Empty;
        public HashSet<int> BloodOverlayIds = new HashSet<int>();
        /// <summary>Camera, weapon holder and fist holder local poses plus camera field of view.</summary>
        public List<PoseSnapshot> RigPoses = new List<PoseSnapshot>();

        /// <summary>Objects that triggers and UnityEvents switch on/off (arena walls, wave spawns).</summary>
        public List<ActiveFlag> LevelActive = new List<ActiveFlag>();

        /// <summary>Every enemy root that existed at capture (active or not), for orphan cleanup.</summary>
        public HashSet<GameObject> EnemyRoots = new HashSet<GameObject>();

        /// <summary>
        /// Roots of the tracked transient gameplay objects (shockwaves, beams, landmines, thrown
        /// swords, ...; see SaveStateManager.TransientTypeNames) alive at capture. A load removes
        /// any such object missing from this set. Projectiles use ProjectileRoots instead.
        /// </summary>
        public HashSet<GameObject> SceneObjects = new HashSet<GameObject>();

        /// <summary>
        /// Roots of the cutscene and sequence drivers (ObjectActivator, ObjectActivateInSequence,
        /// EventsSequence, ObjectSpawner) present at capture. A load destroys any such object that
        /// is not in this set - the clones a running sequence spawned - and rewinds the drivers
        /// that were idle, so a cutscene starts again from its first frame.
        /// </summary>
        public HashSet<GameObject> SequenceObjects = new HashSet<GameObject>();
        /// <summary>False for states captured before sequence tracking existed: never sweep on those.</summary>
        public bool CapturedSequences;

        public bool HasUnityRandomState;
        public UnityRandom.State UnityRandomState;
        public List<SystemRandomSnapshot> SystemRandoms = new List<SystemRandomSnapshot>();
        public int SystemSeedStream;

        public PlayerState Player = new PlayerState();
        public List<EnemyState> Enemies = new List<EnemyState>();
        public List<DoorState> Doors = new List<DoorState>();
        public List<ProjectileState> Projectiles = new List<ProjectileState>();
        public List<AuxiliaryState> Systems = new List<AuxiliaryState>();
        public HashSet<int> ProjectileIds = new HashSet<int>();
        public HashSet<GameObject> ProjectileRoots = new HashSet<GameObject>();
        public List<string> Warnings = new List<string>();

        public int ObjectCount
        {
            get
            {
                return (Player.HasPlayer ? 1 : 0) + Enemies.Count + Doors.Count +
                       Projectiles.Count + Systems.Count + LevelElements.Count + PlayerParts.Count +
                       ExtraComponents.Count;
            }
        }

        /// <summary>Entries the modules stored: a collection counts its elements, anything else counts once.</summary>
        public int ExtItemCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<string, object> pair in Ext)
                {
                    System.Collections.ICollection collection = pair.Value as System.Collections.ICollection;
                    count += collection != null ? collection.Count : (pair.Value != null ? 1 : 0);
                }
                return count;
            }
        }
    }

    /// <summary>
    /// In-memory savestates scoped to what actually changes during a TAS run.
    ///
    /// The previous implementation walked the scene hierarchy and every tracked
    /// component type, which meant 800-1200 objects per state, thousands of
    /// reflection reads and repeated full-scene instance-id scans. It also wrote
    /// velocities into kinematic bodies and drove NavMeshAgents that were not on
    /// a NavMesh, which is where the console spam came from.
    ///
    /// This version captures the gameplay entities and nothing else: the player and
    /// its whole rig (weapons, fists, camera, checks), every enemy (full state for
    /// living ones, a light record for dead or dormant ones), doors and their leaves,
    /// map elements and the objects their triggers switch, and live projectiles.
    /// Values are read generically (primitives, strings, primitive arrays and
    /// TimeSince stamps, which are rebased on load) through cached FieldInfo handles,
    /// so a game update that adds a field is picked up without code changes.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        public const int SlotCount = 10;

        static readonly string[] DeadNames = { "dead", "isDead", "enemyDead", "killed" };
        static readonly string[] HealthNames = { "health", "hp", "currentHealth", "hitPoints" };
        static readonly string[] BleedNames = { "bleed", "bleedAmount", "bleedCounter", "bleedHealth" };
        static readonly string[] ProjectileTypeNames =
        {
            "Coin", "Projectile", "Nail", "Harpoon", "Grenade", "RevolverBeam",
            "Cannonball", "Magnet", "Explosion", "GasolineProjectile"
        };
        // GunControl, FistControl and the camera are captured as part of the player rig.
        static readonly string[] AuxiliaryTypeNames =
        {
            "WeaponCharges", "WeaponHUD", "StatsManager", "StyleHUD", "StyleCalculator", "HookArm",
            "TimeController", "CheckPointsController", "CrateCounter"
        };

        static readonly Dictionary<Type, FieldPlan[]> FieldCache = new Dictionary<Type, FieldPlan[]>();
        static readonly Dictionary<Type, Dictionary<string, FieldInfo>> NamedFieldCache =
            new Dictionary<Type, Dictionary<string, FieldInfo>>();

        readonly Plugin _plugin;
        readonly GameBridge _bridge;
        readonly RngController _rng;
        readonly Savestate[] _slots = new Savestate[SlotCount];

        readonly List<Type> _projectileTypes = new List<Type>(8);
        readonly List<Type> _auxiliaryTypes = new List<Type>(4);
        readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        string _templateScene = string.Empty;
        bool _typesResolved;
        Type _identifierType;

        public Savestate StartState { get; private set; }
        public bool HasStartState { get { return StartState != null; } }

        /// <summary>Called after a restore so the clock resets the game time scale and physics carry.</summary>
        public Action ResetHitstop;

        public SaveStateManager(Plugin plugin, GameBridge bridge, RngController rng)
        {
            _plugin = plugin;
            _bridge = bridge;
            _rng = rng;
            // The field-capture helpers are static; their once-only notices go to this log.
            _staticWarn = message => _plugin.LogWarning(message);
            // Entity IDs follow a breakable that a load rebuilt from its intact copy.
            _bridge.Entities.RebuiltLookup = LookupRebuiltComponent;
        }

        // ------------------------------------------------------------------
        // Slots
        // ------------------------------------------------------------------

        public bool HasSlot(int slot)
        {
            return slot >= 0 && slot < SlotCount && _slots[slot] != null;
        }

        public Savestate GetSlot(int slot)
        {
            return slot >= 0 && slot < SlotCount ? _slots[slot] : null;
        }

        public string SlotSummary(int slot)
        {
            Savestate state = GetSlot(slot);
            if (state == null)
                return "empty";
            return state.Label + " @ " + state.ScriptTime.ToString("0.00") + "s (" +
                   state.ObjectCount + " objects, " + state.Enemies.Count + " enemies, " +
                   state.Doors.Count + " doors, " + state.Projectiles.Count + " projectiles" +
                   (state.StaticFields.Count > 0 ? ", " + state.StaticFields.Count + " statics" : string.Empty) +
                   (state.Ext.Count > 0
                       ? ", " + state.Ext.Count + " module areas / " + state.ExtItemCount + " items"
                       : string.Empty) + ")";
        }

        public void ClearSlot(int slot)
        {
            if (slot >= 0 && slot < SlotCount)
                _slots[slot] = null;
        }

        public void ClearAll()
        {
            for (int i = 0; i < SlotCount; i++)
                _slots[i] = null;
            StartState = null;
            _history.Clear();
        }

        // ------------------------------------------------------------------
        // Rolling history (undo / scrubbing)
        // ------------------------------------------------------------------

        public const int DefaultHistoryTicks = 120;
        bool _warnedSlowCapture;
        /// <summary>Set after a slow history snapshot: later ones skip Deep fields (see _lightCapture).</summary>
        bool _historyLight;
        int _slowHistoryRun;
        bool _warnedSlowFullCapture;

        SnapshotRing _history = new SnapshotRing(DefaultHistoryTicks);

        public int HistoryCount { get { return _history.Count; } }
        public int HistoryCapacity { get { return _history.Capacity; } }

        public void ClearHistory()
        {
            _history.Clear();
        }

        /// <summary>Resize the ring (drops the stored snapshots) when the config changed.</summary>
        public void SetHistoryCapacity(int ticks)
        {
            ticks = Mathf.Clamp(ticks, 1, 1000);
            if (ticks != _history.Capacity)
                _history = new SnapshotRing(ticks);
        }

        /// <summary>
        /// Store the state that exists right before <paramref name="tick"/> executes.
        /// Cheap enough to run once per step; the ring overwrites its oldest entry.
        /// </summary>
        public bool RecordHistory(int tick)
        {
            if (_plugin.RollingHistoryTicks != null)
                SetHistoryCapacity(_plugin.RollingHistoryTicks.Value);
            Savestate state;
            _lightCapture = _historyLight;
            try { state = CaptureTransient("tick " + tick); }
            finally { _lightCapture = false; }
            if (state == null)
                return false;
            state.Tick = tick;
            state.Light = _historyLight;
            // The first captures of a level also build the per-type field plans, so only a run
            // of slow snapshots counts.
            _slowHistoryRun = state.CaptureMilliseconds > 30.0 ? _slowHistoryRun + 1 : 0;
            if (!_historyLight && _slowHistoryRun >= 5)
            {
                // Per-tick snapshots first drop the deep part (collections, plain objects) before
                // the user is told to shrink the history; slot saves always capture everything.
                _historyLight = true;
                _slowHistoryRun = 0;
                _plugin.LogWarning("A history snapshot took " + state.CaptureMilliseconds.ToString("0.0") +
                                   " ms; rolling history now skips collection and plain-object fields " +
                                   "(slot saves still capture them).");
            }
            else if (_historyLight && !_warnedSlowCapture && _slowHistoryRun >= 5)
            {
                _warnedSlowCapture = true;
                _plugin.LogWarning("A history snapshot took " + state.CaptureMilliseconds.ToString("0.0") +
                                   " ms (" + state.ObjectCount + " objects). Stepping may feel slow in this level; " +
                                   "lower 'Rolling history ticks' or leave 'Rolling history while playing' off.");
            }
            _history.Push(tick, state);
            return true;
        }

        /// <summary>
        /// Restore the newest snapshot taken before <paramref name="tick"/> and drop it
        /// together with everything newer. <paramref name="restoredTick"/> is the tick
        /// that snapshot represents.
        /// </summary>
        public SaveResult RestoreHistoryBefore(int tick, out int restoredTick)
        {
            Savestate state = _history.PopBefore(tick, out restoredTick);
            if (state == null)
                return SaveResult.Fail("The rolling history holds no earlier snapshot.");
            return Restore(state);
        }

        public void OnSceneChanged()
        {
            // Templates hold meshes/materials from the old scene; drop them before
            // the scene unload actually happens.
            ClearTemplates();
            ClearAll();
            // A new level gets a new chance at full per-tick snapshots.
            _historyLight = false;
            _slowHistoryRun = 0;
            // Norefresh checkpoints are keyed to the active level instance, so they are not
            // cleared here: additive scene loads fire this too and must not discard them.
        }

        // ------------------------------------------------------------------
        // Save / load entry points
        // ------------------------------------------------------------------

        public SaveResult Save(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
                return SaveResult.Fail("Slot must be 0-" + (SlotCount - 1) + ".");
            if (_bridge.PlayerBelowKillPlane)
                return SaveResult.Fail("Refusing savestate: player is below the kill plane (Y " +
                                       _bridge.PlayerY.ToString("0.##") + " < " +
                                       _plugin.KillPlaneY.Value.ToString("0.##") + ").");

            Savestate state = Capture("slot " + slot);
            _slots[slot] = state;
            string message = "Saved slot " + slot + " (" + state.ObjectCount + " objects: " +
                             state.Enemies.Count + " enemies, " + state.Doors.Count + " doors, " +
                             state.Projectiles.Count + " projectiles) in " +
                             state.CaptureMilliseconds.ToString("0.0") + " ms" +
                             (state.Warnings.Count > 0 ? ", " + state.Warnings.Count + " warnings" : string.Empty) + ".";
            _plugin.Toast(message, false);
            return SaveResult.Ok(message, state.ObjectCount, 0);
        }

        public SaveResult Load(int slot)
        {
            Savestate state = GetSlot(slot);
            if (state == null)
                return SaveResult.Fail("Slot " + slot + " is empty.");
            SaveResult result = Restore(state);
            _plugin.Toast(result.Message, !result.Success);
            return result;
        }

        public void CaptureStartState(int tick, double time)
        {
            if (_bridge.PlayerComponent == null || _bridge.PlayerBelowKillPlane)
            {
                StartState = null;
                return;
            }
            StartState = Capture("start");
            if (StartState != null)
            {
                StartState.Tick = tick;
                StartState.ScriptTime = time;
                _plugin.LogInfo("Captured start state (" + StartState.ObjectCount + " objects, " +
                                StartState.Enemies.Count + " enemies, " + StartState.Doors.Count + " doors).");
            }
        }

        /// <summary>Capture for undo/branch without occupying one of the ten slots.</summary>
        public Savestate CaptureTransient(string label)
        {
            if (_bridge.PlayerComponent == null || _bridge.PlayerBelowKillPlane)
                return null;
            return Capture(label);
        }

        public SaveResult RestoreTransient(Savestate state)
        {
            if (state == null)
                return SaveResult.Fail("No transient state was captured.");
            return Restore(state);
        }

        /// <summary>Master savestate for the brute-force optimizer: taken once at the segment start tick.</summary>
        public Savestate CaptureOptimizerMaster(int startTick)
        {
            return CaptureTransient("optim_master@" + startTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Restores the optimizer master. Transient states never touch the user's slots or history.</summary>
        public SaveResult RestoreOptimizerMaster(Savestate master)
        {
            return RestoreTransient(master);
        }

        public Savestate Capture(string label)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Savestate state = new Savestate
            {
                Label = label,
                SceneName = _bridge.SceneName,
                CreatedUtc = DateTime.UtcNow
            };
            ResolveTypes();
            state.GameTime = Time.time;
            state.UnscaledTime = Time.unscaledTime;
            state.InputTime = CurrentInputTime();

            // Each category is isolated so one unusual object cannot lose the rest.
            try { CapturePlayer(state); }
            catch (Exception ex) { AddWarning(state, "player", ex); }
            try { CapturePlayerRig(state); }
            catch (Exception ex) { AddWarning(state, "player rig", ex); }
            try { CaptureVitals(state); }
            catch (Exception ex) { AddWarning(state, "vitals", ex); }
            try { CaptureLevelElements(state); }
            catch (Exception ex) { AddWarning(state, "level elements", ex); }
            try { CaptureLoadedParts(state); }
            catch (Exception ex) { AddWarning(state, "loaded scenes", ex); }
            try { CaptureEnemies(state); }
            catch (Exception ex) { AddWarning(state, "enemies", ex); }
            try { CaptureDoors(state); }
            catch (Exception ex) { AddWarning(state, "doors", ex); }
            try { CaptureProjectiles(state); }
            catch (Exception ex) { AddWarning(state, "projectiles", ex); }
            try { CaptureAuxiliary(state); }
            catch (Exception ex) { AddWarning(state, "player systems", ex); }
            try { CaptureSceneObjects(state); }
            catch (Exception ex) { AddWarning(state, "scene objects", ex); }
            try { CaptureExtraComponents(state); }
            catch (Exception ex) { AddWarning(state, "loose scripts", ex); }
            try { CaptureStaticFields(state); }
            catch (Exception ex) { AddWarning(state, "static fields", ex); }
            try { state.EntityIds = _bridge.Entities.CaptureIds(); }
            catch (Exception ex) { AddWarning(state, "entity ids", ex); }

            // Module areas (SaveStateManager.Modules.cs), each isolated like the areas above.
            CaptureModules(state);

            try
            {
                state.HasUnityRandomState = true;
                state.UnityRandomState = _rng.CaptureUnityState();
                state.SystemRandoms = _rng.CaptureSystemRandoms();
                state.SystemSeedStream = _rng.SystemSeedStream;
            }
            catch (Exception ex)
            {
                AddWarning(state, "RNG", ex);
            }

            state.CaptureMilliseconds = watch.Elapsed.TotalMilliseconds;
            // Nothing is truncated to keep a save fast any more; a slow level is reported once.
            if (!_warnedSlowFullCapture && !_lightCapture && state.CaptureMilliseconds > SlowCaptureMilliseconds)
            {
                _warnedSlowFullCapture = true;
                _plugin.LogWarning("Savestate capture took " + state.CaptureMilliseconds.ToString("0.0") +
                                   " ms (" + state.ObjectCount + " objects, " + state.Enemies.Count + " enemies, " +
                                   state.LevelElements.Count + " map scripts, " + state.LevelAnimators.Count +
                                   " map animators). Nothing was left out; this is only a performance notice.");
            }
            return state;
        }

        /// <summary>A capture slower than this is reported once (see Capture).</summary>
        const double SlowCaptureMilliseconds = 60.0;

        static double CurrentInputTime()
        {
            try { return UnityEngine.InputSystem.LowLevel.InputState.currentTime; }
            catch { return 0.0; }
        }

        void AddWarning(Savestate state, string area, Exception ex)
        {
            string message = area + ": " + ex.Message;
            state.Warnings.Add(message);
            _plugin.LogWarning("Savestate capture warning: " + message);
        }

        void ResolveTypes()
        {
            if (_typesResolved)
                return;
            _typesResolved = true;

            _identifierType = _bridge.FindType("EnemyIdentifier");
            ResolveWorldTypes();
            _projectileTypes.Clear();
            for (int i = 0; i < ProjectileTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(ProjectileTypeNames[i]);
                if (type != null && !_projectileTypes.Contains(type))
                    _projectileTypes.Add(type);
            }
            _auxiliaryTypes.Clear();
            for (int i = 0; i < AuxiliaryTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(AuxiliaryTypeNames[i]);
                if (type != null)
                    _auxiliaryTypes.Add(type);
            }
            _extraTypes.Clear();
            for (int i = 0; i < ExtraComponentTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(ExtraComponentTypeNames[i]);
                if (type != null && !_extraTypes.Contains(type))
                    _extraTypes.Add(type);
            }
            ResolveStaticFields();
        }

        // ------------------------------------------------------------------
        // Player
        // ------------------------------------------------------------------

        void CapturePlayer(Savestate state)
        {
            object player = _bridge.NewMovement;
            Component component = player as Component;
            if (component == null)
                return;

            PlayerState snapshot = new PlayerState
            {
                HasPlayer = true,
                Root = component.gameObject,
                Transform = component.transform,
                Movement = component,
                Position = component.transform.position,
                Rotation = component.transform.rotation,
                GunSlot = _bridge.GunSlot,
                GunVariation = _bridge.GunVariation
            };

            Rigidbody body = _bridge.PlayerRigidbody;
            if (body != null)
                snapshot.Body = CaptureBody(body);

            snapshot.Health = ReadFloat(player, HealthNames, 0f);
            snapshot.HardDamage = ReadFloat(player, BleedNames, 0f);
            snapshot.Stamina = ReadFloat(player, new[] { "stamina", "staminaMeter", "staminaPoints", "stam" }, 0f);
            snapshot.Fields = CaptureFields(player, 64);
            snapshot.Dead = ReadBool(player, DeadNames, false);
            // NewMovement's own enabled flag, references, pending Jump/Dodge Invokes and
            // slide/fall effect clones (its fields are in Fields above).
            snapshot.Extra = CaptureComponent(component, RefCap);
            if (snapshot.Extra != null)
                snapshot.Extra.Fields = null;
            // Arms, weapons and the camera rig carry their own animators.
            snapshot.Animators = CaptureAnimators(component.gameObject, MaxPlayerAnimators, true);
            snapshot.Agents = CaptureAgents(component.gameObject, 1);

            float pitch;
            float yaw;
            if (_bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                snapshot.CameraPitch = pitch;
                snapshot.CameraYaw = yaw;
            }

            state.Player = snapshot;
        }

        void RestorePlayer(Savestate state, ref int restored, ref int missing)
        {
            PlayerState snapshot = state.Player;
            if (snapshot == null || !snapshot.HasPlayer)
                return;

            Component component = Resolve(snapshot.Movement);
            if (component == null)
            {
                missing++;
                return;
            }

            component.transform.position = snapshot.Position;
            component.transform.rotation = snapshot.Rotation;
            RestoreBody(snapshot.Body);
            ApplyFields(component, snapshot.Fields);
            RestoreComponent(snapshot.Extra);
            RestoreAnimators(snapshot.Animators);
            RestoreAgents(snapshot.Agents);
            restored++;
        }

        // ------------------------------------------------------------------
        // Enemies
        // ------------------------------------------------------------------

        // Safety limits only: they are far above any real enemy, and reaching one is logged
        // (NoteCap) instead of silently dropping the rest. Speed is watched by Capture instead.
        const int MaxBehavioursPerEnemy = 4096;
        const int MaxAnimatorsPerEnemy = 1024;
        const int MaxCollidersPerEnemy = 4096;
        const int MaxParticlesPerEnemy = 2048;
        /// <summary>Animators on the player hierarchy (arms, weapons, camera rig).</summary>
        const int MaxPlayerAnimators = 1024;

        void CaptureEnemies(Savestate state)
        {
            if (_identifierType == null)
                return;

            // includeInactive: arena enemies waiting to spawn are recorded too, so a
            // load puts them back to sleep and they are not mistaken for orphans.
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(_identifierType, true);
            for (int i = 0; i < found.Length; i++)
            {
                Component identifier = found[i] as Component;
                if (identifier == null)
                    continue;
                GameObject root = identifier.gameObject;
                // Deactivated clones kept for respawning are not enemies of the scene.
                if (IsTemplateClone(root))
                    continue;
                state.EnemyRoots.Add(root);
                try
                {
                    bool dead = ReadBool(identifier, DeadNames, false);
                    EnemyState enemy = new EnemyState
                    {
                        Identifier = identifier,
                        Root = root,
                        IdentifierType = identifier.GetType().Name,
                        Position = root.transform.position,
                        Rotation = root.transform.rotation,
                        ActiveSelf = root.activeSelf,
                        Dead = dead,
                        Lightweight = !root.activeInHierarchy || dead,
                        Fields = CaptureFields(identifier, FieldCap)
                    };

                    object health = ReadMember(identifier, HealthNames);
                    if (health != null)
                    {
                        enemy.HasHealth = true;
                        enemy.Health = GameBridge.ToFloat(health, 0f);
                    }
                    enemy.Bleed = ReadFloat(identifier, BleedNames, 0f);

                    if (!enemy.Lightweight)
                    {
                        enemy.References = CaptureReferences(identifier, RefCap);
                        enemy.ListRefs = CaptureListRefs(identifier);
                        enemy.Behaviours = CaptureBehaviours(root, identifier);

                        Animator primary = FindAnimator(root);
                        enemy.Animator = CaptureAnimator(primary, true);
                        enemy.ExtraAnimators = CaptureAnimatorsExcept(root, MaxAnimatorsPerEnemy, primary);
                        enemy.Colliders = CaptureColliders(root, MaxCollidersPerEnemy);
                        enemy.Particles = new List<ParticleSnapshot>();
                        CaptureParticles(root, MaxParticlesPerEnemy, enemy.Particles);

                        NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
                        enemy.HasAgentComponent = agent != null;
                        enemy.AgentEnabled = agent != null && agent.enabled;
                        enemy.Agent = CaptureAgent(agent);

                        Rigidbody body = root.GetComponent<Rigidbody>();
                        if (body != null)
                            enemy.Body = CaptureBody(body);
                    }
                    state.Enemies.Add(enemy);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("enemy " + identifier.name + ": " + ex.Message);
                }
            }
        }

        void RestoreEnemies(Savestate state, ref int restored, ref int missing)
        {
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                EnemyState enemy = state.Enemies[i];
                Component identifier = Resolve(enemy.Identifier);
                if (identifier == null)
                {
                    missing++;
                    continue;
                }

                try
                {
                    GameObject root = identifier.gameObject;
                    // Active flag first: an enemy that woke up after the save is put back
                    // to sleep, and OnEnable runs before the fields below are applied.
                    if (root.activeSelf != enemy.ActiveSelf)
                        root.SetActive(enemy.ActiveSelf);

                    root.transform.position = enemy.Position;
                    root.transform.rotation = enemy.Rotation;
                    ApplyFields(identifier, enemy.Fields);
                    if (enemy.HasHealth)
                        SetMember(identifier, HealthNames, enemy.Health);
                    SetMember(identifier, DeadNames, enemy.Dead);
                    SetMember(identifier, BleedNames, enemy.Bleed);

                    if (enemy.Lightweight)
                    {
                        restored++;
                        continue;
                    }

                    RestoreBody(enemy.Body);
                    ApplyReferences(identifier, enemy.References);
                    RestoreListRefs(identifier, enemy.ListRefs);
                    RestoreBehaviours(enemy.Behaviours);
                    RestoreAnimator(enemy.Animator);
                    RestoreAnimators(enemy.ExtraAnimators);
                    RestoreColliders(enemy.Colliders);
                    RestoreParticles(enemy.Particles);

                    if (enemy.HasAgentComponent)
                    {
                        NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
                        if (agent != null && agent.enabled != enemy.AgentEnabled)
                        {
                            try { agent.enabled = enemy.AgentEnabled; }
                            catch { }
                        }
                    }
                    RestoreAgent(enemy.Agent);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Enemy restore skipped: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Every game script on the enemy (Zombie, Machine, Drone, Statue...): attack
        /// cooldowns, state enums, TimeSince stamps and target references all live there,
        /// not on EnemyIdentifier. Limb markers and Unity/UI components are skipped.
        /// </summary>
        List<ComponentSnapshot> CaptureBehaviours(GameObject root, Component identifier)
        {
            List<ComponentSnapshot> result = new List<ComponentSnapshot>(8);
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length && result.Count < MaxBehavioursPerEnemy; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || ReferenceEquals(behaviour, identifier))
                    continue;
                Type type = behaviour.GetType();
                if (!IsGameScript(type) || type.Name == "EnemyIdentifierIdentifier")
                    continue;
                ComponentSnapshot snapshot = CaptureComponent(behaviour, RefCap);
                if (snapshot != null)
                    result.Add(snapshot);
            }
            if (result.Count >= MaxBehavioursPerEnemy)
                NoteCap("scripts per enemy", MaxBehavioursPerEnemy);
            return result;
        }

        static void RestoreBehaviours(List<ComponentSnapshot> behaviours)
        {
            if (behaviours == null)
                return;
            for (int i = 0; i < behaviours.Count; i++)
                RestoreComponent(behaviours[i]);
        }

        // ------------------------------------------------------------------
        // Doors
        // ------------------------------------------------------------------

        void CaptureDoors(Savestate state)
        {
            Type doorType = _bridge.FindType("Door");
            if (doorType == null)
                return;

            // Include doors in currently inactive rooms: a room can be disabled at save time and
            // re-enabled later, and its doors must still return to their saved open/locked state.
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(doorType, true);
            for (int i = 0; i < found.Length; i++)
            {
                Component door = found[i] as Component;
                if (door == null || !door.gameObject.scene.IsValid())
                    continue;
                try
                {
                    DoorState snapshot = new DoorState
                    {
                        Door = door,
                        Root = door.gameObject,
                        Position = door.transform.position,
                        Rotation = door.transform.rotation,
                        Open = GetBool(door, "open"),
                        Locked = GetBool(door, "locked"),
                        FullyOpened = GetBool(door, "isFullyOpened"),
                        FullyClosed = GetBool(door, "isFullyClosed"),
                        InPos = GetBool(door, "inPos"),
                        Requests = GetInt(door, "requests"),
                        OpenLocks = GetInt(door, "openLocks"),
                        TargetPos = GetVector3(door, "targetPos")
                    };

                    Rigidbody body = GetReference<Rigidbody>(door, "rb");
                    if (body == null)
                        body = door.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        snapshot.HasRigidbody = true;
                        snapshot.RigidbodyKinematic = body.isKinematic;
                        snapshot.RigidbodyPosition = body.position;
                        snapshot.RigidbodyRotation = body.rotation;
                    }

                    GameObject noPass = GetReference<GameObject>(door, "noPass");
                    if (noPass != null)
                    {
                        snapshot.HasNoPass = true;
                        snapshot.NoPassActive = noPass.activeSelf;
                    }

                    NavMeshObstacle obstacle = GetReference<NavMeshObstacle>(door, "nmo");
                    if (obstacle != null)
                    {
                        snapshot.HasObstacle = true;
                        snapshot.ObstacleEnabled = obstacle.enabled;
                    }

                    Light openLight = GetReference<Light>(door, "openLight");
                    if (openLight != null)
                    {
                        snapshot.HasLight = true;
                        snapshot.LightEnabled = openLight.enabled;
                    }

                    Animator[] handles = GetReferenceArray<Animator>(door, "doorHandles");
                    for (int h = 0; h < handles.Length; h++)
                    {
                        AnimatorState handle = CaptureAnimator(handles[h], false);
                        if (handle != null)
                            snapshot.Handles.Add(handle);
                    }

                    CaptureDoorExtras(snapshot, door);
                    CaptureDoorVisuals(snapshot, door);
                    state.Doors.Add(snapshot);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("door " + door.name + ": " + ex.Message);
                }
            }

            // FinalDoor / FinalDoorOpener / OnLevelStart (the spawn door): SaveStateManager.Doors.cs.
            try { CaptureSpawnDoors(state); }
            catch (Exception ex) { state.Warnings.Add("spawn doors: " + ex.Message); }
        }

        void RestoreDoors(Savestate state, ref int restored, ref int missing)
        {
            for (int i = 0; i < state.Doors.Count; i++)
            {
                DoorState door = state.Doors[i];
                Component component = Resolve(door.Door);
                if (component == null)
                {
                    missing++;
                    continue;
                }

                try
                {
                    component.transform.position = door.Position;
                    component.transform.rotation = door.Rotation;
                    RestoreDoorExtras(door);

                    Rigidbody body = GetReference<Rigidbody>(component, "rb");
                    if (body == null)
                        body = component.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.isKinematic = door.RigidbodyKinematic;
                        body.position = door.RigidbodyPosition;
                        body.rotation = door.RigidbodyRotation;
                        if (!body.isKinematic)
                        {
                            body.velocity = Vector3.zero;
                            body.angularVelocity = Vector3.zero;
                            body.WakeUp();
                        }
                    }

                    SetBool(component, "open", door.Open);
                    SetBool(component, "locked", door.Locked);
                    SetBool(component, "isFullyOpened", door.FullyOpened);
                    SetBool(component, "isFullyClosed", door.FullyClosed);
                    SetBool(component, "inPos", door.InPos);
                    SetInt(component, "requests", door.Requests);
                    SetInt(component, "openLocks", door.OpenLocks);
                    SetVector3(component, "targetPos", door.TargetPos);

                    if (door.HasNoPass)
                    {
                        GameObject noPass = GetReference<GameObject>(component, "noPass");
                        if (noPass != null)
                            noPass.SetActive(door.NoPassActive);
                    }
                    if (door.HasObstacle)
                    {
                        NavMeshObstacle obstacle = GetReference<NavMeshObstacle>(component, "nmo");
                        if (obstacle != null)
                            obstacle.enabled = door.ObstacleEnabled;
                    }
                    if (door.HasLight)
                    {
                        Light openLight = GetReference<Light>(component, "openLight");
                        if (openLight != null)
                            openLight.enabled = door.LightEnabled;
                    }

                    for (int h = 0; h < door.Handles.Count; h++)
                        RestoreAnimator(door.Handles[h]);
                    RestoreDoorVisuals(door, component);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Door restore skipped: " + ex.Message);
                }
            }

            // After the leaves: the opener flags decide whether they open again.
            try { RestoreSpawnDoors(state); }
            catch (Exception ex) { _plugin.LogWarning("Spawn door restore skipped: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Projectiles and coins
        // ------------------------------------------------------------------

        void CaptureProjectiles(Savestate state)
        {
            HashSet<int> roots = new HashSet<int>();
            for (int t = 0; t < _projectileTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_projectileTypes[t]); }
                catch { continue; }

                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    GameObject root = component.gameObject;
                    if (!root.activeInHierarchy || !roots.Add(root.GetInstanceID()))
                        continue;

                    try
                    {
                        ProjectileState projectile = new ProjectileState
                        {
                            Component = component,
                            Root = root,
                            TypeName = component.GetType().Name,
                            Position = root.transform.position,
                            Rotation = root.transform.rotation,
                            LocalScale = root.transform.localScale,
                            Ricochets = ReadInt(component, new[] { "ricochets", "ricochet", "bounceCount", "bounces" }, 0),
                            HitTimes = ReadInt(component, new[] { "hitTimes", "hits", "hitAmount" }, 0),
                            Lifetime = ReadFloat(component, new[] { "timeToDelete", "life", "lifetime", "lifeTime", "timer" }, 0f),
                            Fields = CaptureFields(component, 48)
                        };

                        Rigidbody body = root.GetComponent<Rigidbody>();
                        if (body != null)
                            projectile.Body = CaptureBody(body);

                        projectile.ComponentType = component.GetType();
                        projectile.PrefabName = CleanObjectName(root.name);
                        projectile.References = CaptureReferences(component, RefCap);
                        projectile.Template = GetOrCreateTemplate(component, root);
                        projectile.ListRefs = CaptureListRefs(component);
                        if (projectile.TypeName == "Coin")
                            projectile.Coin = CaptureCoin(component, root);

                        state.Projectiles.Add(projectile);
                        state.ProjectileIds.Add(root.GetInstanceID());
                        state.ProjectileRoots.Add(root);
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add("projectile " + component.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        CoinState CaptureCoin(Component coin, GameObject root)
        {
            CoinState state = new CoinState
            {
                Timer = ReadFloat(coin, CoinState.TimerNames, 0f),
                Hits = ReadInt(coin, CoinState.HitNames, 0),
                Shot = ReadBool(coin, CoinState.ShotNames, false),
                Splittable = ReadBool(coin, CoinState.SplitNames, false),
                Alt = ReadBool(coin, CoinState.AltNames, false),
                Target = ReadMember(coin, CoinState.TargetNames) as UnityEngine.Object
            };

            SphereCollider sphere = root.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                state.HasCollider = true;
                state.ColliderEnabled = sphere.enabled;
                state.ColliderRadius = sphere.radius;
                state.ColliderCenter = sphere.center;
            }
            return state;
        }

        /// <summary>
        /// Writes the coin's lifetime, ricochet/punch counter, shot and split flags, alt
        /// behaviour, target lock and hit collider back onto a live or respawned coin.
        /// Velocity and angular velocity are restored by RestoreBody.
        /// </summary>
        void ApplyCoin(Component coin, CoinState state)
        {
            if (coin == null || state == null)
                return;
            try
            {
                SetMember(coin, CoinState.TimerNames, state.Timer);
                SetMember(coin, CoinState.HitNames, state.Hits);
                SetMember(coin, CoinState.ShotNames, state.Shot);
                SetMember(coin, CoinState.SplitNames, state.Splittable);
                SetMember(coin, CoinState.AltNames, state.Alt);
                if (state.Target != null)
                    SetMember(coin, CoinState.TargetNames, state.Target);

                SphereCollider sphere = coin.GetComponent<SphereCollider>();
                if (sphere != null && state.HasCollider)
                {
                    sphere.radius = state.ColliderRadius;
                    sphere.center = state.ColliderCenter;
                    sphere.enabled = state.ColliderEnabled;
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Coin restore skipped: " + ex.Message);
            }
        }

        /// <summary>Remove projectiles/coins that did not exist when the state was captured.</summary>
        void CleanupOrphans(Savestate state)
        {
            HashSet<int> destroyed = new HashSet<int>();
            Component player = _bridge.PlayerComponent;
            Transform playerTransform = player != null ? player.transform : null;

            // Reference identity is checked first: a recycled instance id must not
            // let a projectile spawned after the save survive the reload.
            HashSet<GameObject> savedRoots = new HashSet<GameObject>();
            for (int i = 0; i < state.Projectiles.Count; i++)
            {
                if (state.Projectiles[i].Root != null)
                    savedRoots.Add(state.Projectiles[i].Root);
            }

            for (int t = 0; t < _projectileTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_projectileTypes[t]); }
                catch { continue; }

                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    GameObject root = component.gameObject;
                    int id = root.GetInstanceID();
                    if (savedRoots.Contains(root) || state.ProjectileIds.Contains(id) || !destroyed.Add(id))
                        continue;
                    if (playerTransform != null && root.transform.IsChildOf(playerTransform))
                        continue;
                    UnityEngine.Object.Destroy(root);
                }
            }

            SweepSpawnedObjects(state);
        }

        void RestoreProjectiles(Savestate state, ref int restored, ref int missing, ref int respawned)
        {
            // Second pass: list membership (a Magnet's sawblades, connected magnets) can point at
            // other projectiles, including respawned ones, so it is applied once all exist.
            Component[] resolved = new Component[state.Projectiles.Count];
            for (int i = 0; i < state.Projectiles.Count; i++)
            {
                ProjectileState projectile = state.Projectiles[i];
                Component component = Resolve(projectile.Component);
                if (component == null)
                {
                    // The grenade exploded / coin was consumed after the save:
                    // rebuild it from the deactivated template.
                    component = RespawnProjectile(state, projectile);
                    if (component == null)
                    {
                        missing++;
                        continue;
                    }
                    resolved[i] = component;
                    respawned++;
                    restored++;
                    continue;
                }

                resolved[i] = component;
                try
                {
                    component.transform.position = projectile.Position;
                    component.transform.rotation = projectile.Rotation;
                    component.transform.localScale = projectile.LocalScale;
                    ApplyFields(component, projectile.Fields);
                    ApplyReferences(component, projectile.References);
                    RestoreBody(projectile.Body);
                    ApplyCoin(component, projectile.Coin);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Projectile restore skipped: " + ex.Message);
                }
            }

            for (int i = 0; i < state.Projectiles.Count; i++)
            {
                if (resolved[i] != null)
                    RestoreListRefs(resolved[i], state.Projectiles[i].ListRefs);
            }
        }

        /// <summary>
        /// Instantiate a saved template while it is still inactive, apply the saved
        /// fields, then activate it so Awake/OnEnable see the restored state.
        /// </summary>
        Component RespawnProjectile(Savestate state, ProjectileState projectile)
        {
            if (projectile.Template == null || projectile.ComponentType == null)
                return null;
            GameObject instance;
            try
            {
                instance = UnityEngine.Object.Instantiate(projectile.Template);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Projectile respawn failed: " + ex.Message);
                return null;
            }

            instance.name = string.IsNullOrEmpty(projectile.PrefabName) ? projectile.TypeName : projectile.PrefabName;
            instance.transform.position = projectile.Position;
            instance.transform.rotation = projectile.Rotation;
            instance.transform.localScale = projectile.LocalScale;

            Component component = instance.GetComponent(projectile.ComponentType);
            if (component == null)
            {
                UnityEngine.Object.Destroy(instance);
                return null;
            }

            ApplyFields(component, projectile.Fields);
            ApplyReferences(component, projectile.References);
            instance.SetActive(true);
            // Awake/OnEnable ran inside SetActive and may have reset saved values (e.g.
            // Projectile.OnEnable, Nail.Awake); apply them again now. Start() runs on the
            // next frame and resets more (Coin.doubled, Projectile.speed/maxSpeed/target,
            // Harpoon.damageLeft, Grenade.rocketCount), so a finalizer re-applies once more
            // after Start and before the projectile's first simulation step.
            ApplyFields(component, projectile.Fields);
            ApplyReferences(component, projectile.References);
            ApplyCoin(component, projectile.Coin);
            try
            {
                RespawnFinalizer finalizer = instance.AddComponent<RespawnFinalizer>();
                Component target = component;
                List<FieldState> fields = projectile.Fields;
                List<ObjectFieldState> references = projectile.References;
                CoinState coin = projectile.Coin;
                finalizer.Reapply = () =>
                {
                    if (target == null)
                        return;
                    ApplyFields(target, fields);
                    ApplyReferences(target, references);
                    ApplyCoin(target, coin);
                };
            }
            catch
            {
            }

            // Later loads of the same state restore this instance in place instead of
            // respawning another copy (CleanupOrphans keeps roots listed here).
            projectile.Component = component;
            projectile.Root = instance;
            state.ProjectileRoots.Add(instance);
            state.ProjectileIds.Add(instance.GetInstanceID());

            Rigidbody body = instance.GetComponent<Rigidbody>();
            if (body != null && projectile.Body != null)
            {
                try
                {
                    body.isKinematic = projectile.Body.IsKinematic;
                    if (!body.isKinematic)
                    {
                        body.velocity = projectile.Body.Velocity;
                        body.angularVelocity = projectile.Body.AngularVelocity;
                        body.WakeUp();
                    }
                }
                catch
                {
                }
            }
            return component;
        }

        GameObject GetOrCreateTemplate(Component component, GameObject root)
        {
            if (_plugin.RespawnDynamicObjects == null || !_plugin.RespawnDynamicObjects.Value)
                return null;
            if (!string.Equals(_templateScene, _bridge.SceneName, StringComparison.Ordinal))
            {
                ClearTemplates();
                _templateScene = _bridge.SceneName;
            }

            string key = component.GetType().Name + "|" + CleanObjectName(root.name);
            GameObject template;
            if (_templates.TryGetValue(key, out template) && template != null)
                return template;

            try
            {
                // Clone under an inactive holder: Instantiate on an active object
                // runs Awake/OnEnable immediately (sounds, coin list registration,
                // projectile spawn effects), which must not happen at save time.
                template = UnityEngine.Object.Instantiate(root, TemplateHolder().transform, true);
                template.name = "[TAS template] " + key;
                template.SetActive(false);
                template.transform.SetParent(null, true);
                UnityEngine.Object.DontDestroyOnLoad(template);
                _templates[key] = template;
                return template;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Could not store template for " + key + ": " + ex.Message);
                return null;
            }
        }

        GameObject _templateHolder;

        GameObject TemplateHolder()
        {
            if (_templateHolder == null)
            {
                _templateHolder = new GameObject("[TAS template holder]");
                _templateHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_templateHolder);
            }
            return _templateHolder;
        }

        void ClearTemplates()
        {
            foreach (KeyValuePair<string, GameObject> pair in _templates)
            {
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            }
            _templates.Clear();
            _templateScene = string.Empty;
        }

        static string CleanObjectName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "object";
            const string clone = "(Clone)";
            int index = name.IndexOf(clone, StringComparison.Ordinal);
            return index >= 0 ? name.Substring(0, index).Trim() : name;
        }

        // ------------------------------------------------------------------
        // Player-side systems
        // ------------------------------------------------------------------

        void CaptureAuxiliary(Savestate state)
        {
            for (int i = 0; i < _auxiliaryTypes.Count; i++)
            {
                UnityEngine.Object found;
                try { found = UnityEngine.Object.FindObjectOfType(_auxiliaryTypes[i]); }
                catch { continue; }
                Component component = found as Component;
                if (component == null)
                    continue;
                try
                {
                    state.Systems.Add(new AuxiliaryState
                    {
                        Component = component,
                        TypeName = component.GetType().Name,
                        Fields = CaptureFields(component, 32),
                        // StatsManager.currentCheckPoint, StyleHUD/WeaponHUD references.
                        References = CaptureReferences(component, RefCap)
                    });
                }
                catch (Exception ex)
                {
                    state.Warnings.Add(component.GetType().Name + ": " + ex.Message);
                }
            }
        }

        void RestoreSystems(Savestate state, ref int restored, ref int missing)
        {
            for (int i = 0; i < state.Systems.Count; i++)
            {
                AuxiliaryState system = state.Systems[i];
                Component component = Resolve(system.Component);
                if (component == null)
                {
                    missing++;
                    continue;
                }
                ApplyFields(component, system.Fields);
                ApplyReferences(component, system.References);
                // The crate HUD text is only redrawn by UpdateDisplay; with the saved
                // success flag it does not fire onAllCratesGet again.
                if (system.TypeName == "CrateCounter")
                    TryInvoke(component, "UpdateDisplay");
                restored++;
            }
        }

        // ------------------------------------------------------------------
        // Restore
        // ------------------------------------------------------------------

        public SaveResult Restore(Savestate state)
        {
            if (state == null)
                return SaveResult.Fail("Savestate is null.");
            if (!string.IsNullOrEmpty(state.SceneName) && state.SceneName != _bridge.SceneName)
                return SaveResult.Fail("Savestate was made in scene '" + state.SceneName +
                                       "' but the current scene is '" + _bridge.SceneName + "'. Load it in the same scene.");

            Stopwatch watch = Stopwatch.StartNew();
            int restored = 0;
            int missing = 0;
            int respawned = 0;

            // TimeSince stamps are absolute Time.time values; shift them by the virtual time
            // that has passed since the capture so every cooldown keeps its remaining time.
            _restoreTimeDelta = Time.time - state.GameTime;
            _restoreUnscaledDelta = state.UnscaledTime != 0f ? Time.unscaledTime - state.UnscaledTime : 0f;
            _restoreInputDelta = state.InputTime != 0.0 ? CurrentInputTime() - state.InputTime : 0.0;

            bool cleanup = _plugin.SavestateCleanupSpawned == null || _plugin.SavestateCleanupSpawned.Value;

            // Modules stop what must not keep running into the restored state (coroutines,
            // pending Invokes) before any value below is written.
            RunPreRestore(state);
            SpawnDoorTracker.ClearAllRearm();

            // Order matters: things that wake or sleep objects come first (their OnEnable
            // then runs before any saved value is written), values next, effects last,
            // and the RNG streams after everything that might have drawn from them.
            try { RestoreLevelElements(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate level restore skipped: " + ex.Message); }
            // Undo a death first: it re-enables the camera, weapons and fists that the
            // rig and player restores below then put back to their saved values.
            try { RevivePlayerIfDead(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate revive skipped: " + ex.Message); }
            try { RestorePlayerRig(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate player rig restore skipped: " + ex.Message); }
            try { RestorePlayer(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate player restore skipped: " + ex.Message); }
            try { RestoreVitals(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate vitals restore skipped: " + ex.Message); }
            if (cleanup)
            {
                try { CleanupOrphans(state); }
                catch (Exception ex) { _plugin.LogWarning("Savestate orphan cleanup skipped: " + ex.Message); }
                try { CleanupEnemyOrphans(state); }
                catch (Exception ex) { _plugin.LogWarning("Savestate enemy cleanup skipped: " + ex.Message); }
            }
            try { RestoreEnemies(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate enemy restore skipped: " + ex.Message); }
            // The numbering goes back after the orphan cleanups, so IDs of objects this load
            // removed stay dead instead of being handed to the next object seen.
            try { _bridge.Entities.RestoreIds(state.EntityIds); }
            catch (Exception ex) { _plugin.LogWarning("Savestate entity ID restore skipped: " + ex.Message); }
            try { RestoreDoors(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate door restore skipped: " + ex.Message); }
            try { RestoreProjectiles(state, ref restored, ref missing, ref respawned); }
            catch (Exception ex) { _plugin.LogWarning("Savestate projectile restore skipped: " + ex.Message); }
            try { RestoreSystems(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate system restore skipped: " + ex.Message); }
            try { RestoreExtraComponents(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate loose script restore skipped: " + ex.Message); }
            try { RestoreStaticFields(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate static field restore skipped: " + ex.Message); }

            Physics.SyncTransforms();

            try { RestoreLoadedParts(state, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate loaded-parts check skipped: " + ex.Message); }

            _bridge.SetCameraAngles(state.Player.CameraPitch, state.Player.CameraYaw);

            // Animators and particles are re-posed after the physics sync so a pose that
            // depends on world position sees the restored transforms.
            try { RestorePlayerEffects(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate effects restore skipped: " + ex.Message); }

            // Module areas (SaveStateManager.Modules.cs): after every core restore, before the
            // hitstop reset and the RNG streams.
            RestoreModules(state, ref restored, ref missing);

            // A state taken inside a game hitstop gets that hitstop back exactly (currentStop,
            // its tick timers) and resumes it; see SaveStateManager.Timing.cs. Time.timeScale
            // is never written by a load.
            bool midHitstop = RestoreHitstop(state);

            // The clock's physics carry belongs to the timeline that was just discarded;
            // resync it. Not on a mid-hitstop load (RestoreHitstop resyncs it there).
            if (!midHitstop && ResetHitstop != null)
                ResetHitstop();

            if (state.HasUnityRandomState)
            {
                _rng.RestoreUnityState(state.UnityRandomState);
                _rng.SystemSeedStream = state.SystemSeedStream;
            }
            int restoredRandoms = _rng.RestoreSystemRandoms(state.SystemRandoms);

            // Last fix-ups (animator Update, re-armed coroutines) that need everything above.
            RunPostRestore(state);

            double milliseconds = watch.Elapsed.TotalMilliseconds;
            string message = "Loaded " + state.Label + " from " + state.ScriptTime.ToString("0.00") +
                             "s: " + restored + " objects restored, " + respawned + " respawned, " +
                             missing + " missing, " +
                             restoredRandoms + " System.Random restored, " +
                             milliseconds.ToString("0.0") + " ms.";
            _plugin.LogInfo(message);
            return SaveResult.Ok(message, restored, missing);
        }

        // ------------------------------------------------------------------
        // Rigidbody / Animator / NavMesh primitives
        // ------------------------------------------------------------------

        static BodyState CaptureBody(Rigidbody body)
        {
            if (body == null)
                return null;
            BodyState snapshot = new BodyState
            {
                Body = body,
                IsKinematic = body.isKinematic,
                UseGravity = body.useGravity,
                DetectCollisions = body.detectCollisions,
                Constraints = body.constraints,
                Drag = body.drag,
                AngularDrag = body.angularDrag
            };
            // Kinematic bodies reject velocity writes, so only read them when the
            // body is dynamic; this is the source of the old warning flood.
            if (!body.isKinematic)
            {
                snapshot.Velocity = body.velocity;
                snapshot.AngularVelocity = body.angularVelocity;
                snapshot.Sleeping = body.IsSleeping();
            }
            return snapshot;
        }

        static void RestoreBody(BodyState snapshot)
        {
            if (snapshot == null || snapshot.Body == null)
                return;
            Rigidbody body = snapshot.Body;
            try
            {
                body.constraints = snapshot.Constraints;
                body.useGravity = snapshot.UseGravity;
                body.detectCollisions = snapshot.DetectCollisions;
                body.drag = snapshot.Drag;
                body.angularDrag = snapshot.AngularDrag;
                body.isKinematic = snapshot.IsKinematic;
                if (!body.isKinematic)
                {
                    body.velocity = snapshot.Velocity;
                    body.angularVelocity = snapshot.AngularVelocity;
                    if (snapshot.Sleeping)
                        body.Sleep();
                    else
                        body.WakeUp();
                }
            }
            catch
            {
            }
        }

        static AgentState CaptureAgent(NavMeshAgent agent)
        {
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
                return null;
            return new AgentState
            {
                Agent = agent,
                Position = agent.transform.position,
                Destination = agent.destination,
                Velocity = agent.velocity,
                IsStopped = agent.isStopped,
                HasPath = agent.hasPath,
                Speed = agent.speed,
                Acceleration = agent.acceleration,
                AngularSpeed = agent.angularSpeed,
                StoppingDistance = agent.stoppingDistance,
                AutoBraking = agent.autoBraking,
                UpdatePosition = agent.updatePosition,
                UpdateRotation = agent.updateRotation
            };
        }

        static void RestoreAgent(AgentState snapshot)
        {
            if (snapshot == null || snapshot.Agent == null)
                return;
            NavMeshAgent agent = snapshot.Agent;
            if (!agent.isActiveAndEnabled)
                return;
            try
            {
                // Movement parameters first: the game retunes these per attack and a
                // stale speed would change how the restored path is followed.
                agent.speed = snapshot.Speed;
                agent.acceleration = snapshot.Acceleration;
                agent.angularSpeed = snapshot.AngularSpeed;
                agent.stoppingDistance = snapshot.StoppingDistance;
                agent.autoBraking = snapshot.AutoBraking;
                agent.updatePosition = snapshot.UpdatePosition;
                agent.updateRotation = snapshot.UpdateRotation;

                // An agent knocked off the mesh since the save is put back on it before
                // any NavMesh call, otherwise Warp/SetPath throw.
                if (!agent.isOnNavMesh)
                {
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(snapshot.Position, out hit, 3f, NavMesh.AllAreas))
                        return;
                    agent.Warp(hit.position);
                    if (!agent.isOnNavMesh)
                        return;
                }

                agent.Warp(snapshot.Position);
                // Warp can leave the agent off the mesh when the saved spot is no longer
                // on it (moving platform, carved obstacle); every call below would throw.
                if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
                    return;

                if (snapshot.HasPath)
                {
                    // CalculatePath + SetPath is synchronous. SetDestination only queues a
                    // path request, so the agent would walk a frame with no path and the
                    // replay could diverge from the original.
                    NavMeshPath path = new NavMeshPath();
                    if (agent.CalculatePath(snapshot.Destination, path))
                        agent.SetPath(path);
                    else
                        agent.destination = snapshot.Destination;
                }
                else
                {
                    agent.ResetPath();
                }

                agent.velocity = snapshot.Velocity;
                agent.isStopped = snapshot.IsStopped;
            }
            catch
            {
            }
        }

        static AnimatorState CaptureAnimator(Animator animator, bool includeParameters)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return null;

            // A disabled animator still matters: death and spawn logic switch it off, and
            // restoring an earlier state must switch it back on. Its clips are recorded too,
            // so a cutscene prop that was switched off at the save goes back to the start of
            // its clip instead of carrying on from wherever the cutscene left it.
            bool active = animator.isActiveAndEnabled;

            int layerCount = animator.layerCount;
            if (layerCount <= 0)
                return null;

            AnimatorState snapshot = new AnimatorState
            {
                Animator = animator,
                Enabled = animator.enabled,
                Speed = animator.speed,
                LayerCount = layerCount,
                StateHashes = new int[layerCount],
                NormalizedTimes = new float[layerCount],
                LayerWeights = new float[layerCount]
            };
            for (int layer = 0; layer < layerCount; layer++)
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(layer);
                snapshot.StateHashes[layer] = info.fullPathHash;
                // An object that was switched off keeps no meaningful playback time: start its
                // clip at the first frame when the sequence enables it again.
                snapshot.NormalizedTimes[layer] = active ? info.normalizedTime : 0f;
                snapshot.LayerWeights[layer] = animator.GetLayerWeight(layer);
            }

            if (includeParameters)
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                snapshot.Parameters = new List<AnimatorParameterState>(parameters.Length);
                for (int p = 0; p < parameters.Length; p++)
                {
                    AnimatorControllerParameter parameter = parameters[p];
                    AnimatorParameterState state = new AnimatorParameterState
                    {
                        NameHash = parameter.nameHash,
                        Type = parameter.type
                    };
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            state.FloatValue = animator.GetFloat(parameter.nameHash);
                            break;
                        case AnimatorControllerParameterType.Int:
                            state.IntValue = animator.GetInteger(parameter.nameHash);
                            break;
                        case AnimatorControllerParameterType.Bool:
                        case AnimatorControllerParameterType.Trigger:
                            state.BoolValue = animator.GetBool(parameter.nameHash);
                            break;
                    }
                    snapshot.Parameters.Add(state);
                }
            }
            return snapshot;
        }

        static void RestoreAnimator(AnimatorState snapshot)
        {
            if (snapshot == null || snapshot.Animator == null)
                return;
            Animator animator = snapshot.Animator;
            if (animator.runtimeAnimatorController == null)
                return;

            try
            {
                if (animator.enabled != snapshot.Enabled)
                    animator.enabled = snapshot.Enabled;
                animator.speed = snapshot.Speed;
            }
            catch
            {
            }

            if (!animator.isActiveAndEnabled || snapshot.LayerCount <= 0)
                return;

            try
            {
                int layers = Mathf.Min(snapshot.LayerCount, animator.layerCount);
                for (int layer = 0; layer < layers; layer++)
                {
                    if (snapshot.StateHashes[layer] != 0)
                        animator.Play(snapshot.StateHashes[layer], layer, snapshot.NormalizedTimes[layer]);
                    animator.SetLayerWeight(layer, snapshot.LayerWeights[layer]);
                }
                if (snapshot.Parameters != null)
                {
                    for (int p = 0; p < snapshot.Parameters.Count; p++)
                    {
                        AnimatorParameterState parameter = snapshot.Parameters[p];
                        switch (parameter.Type)
                        {
                            case AnimatorControllerParameterType.Float:
                                animator.SetFloat(parameter.NameHash, parameter.FloatValue);
                                break;
                            case AnimatorControllerParameterType.Int:
                                animator.SetInteger(parameter.NameHash, parameter.IntValue);
                                break;
                            case AnimatorControllerParameterType.Bool:
                                animator.SetBool(parameter.NameHash, parameter.BoolValue);
                                break;
                            case AnimatorControllerParameterType.Trigger:
                                animator.ResetTrigger(parameter.NameHash);
                                if (parameter.BoolValue)
                                    animator.SetTrigger(parameter.NameHash);
                                break;
                        }
                    }
                }
                animator.Update(0f);
            }
            catch
            {
            }
        }

        static List<AnimatorState> CaptureAnimators(GameObject root, int max, bool includeParameters)
        {
            if (root == null || max <= 0)
                return new List<AnimatorState>(0);
            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            List<AnimatorState> result = new List<AnimatorState>(Mathf.Min(max, animators.Length));
            for (int i = 0; i < animators.Length && result.Count < max; i++)
            {
                AnimatorState snapshot = CaptureAnimator(animators[i], includeParameters);
                if (snapshot != null)
                    result.Add(snapshot);
            }
            if (result.Count >= max && animators.Length > max)
                NoteCap("animators per object", max);
            return result;
        }

        static void RestoreAnimators(List<AnimatorState> snapshots)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
                RestoreAnimator(snapshots[i]);
        }

        static List<AgentState> CaptureAgents(GameObject root, int max)
        {
            if (root == null || max <= 0)
                return new List<AgentState>(0);
            NavMeshAgent[] agents = root.GetComponentsInChildren<NavMeshAgent>(true);
            List<AgentState> result = new List<AgentState>(Mathf.Min(max, agents.Length));
            for (int i = 0; i < agents.Length && result.Count < max; i++)
            {
                AgentState snapshot = CaptureAgent(agents[i]);
                if (snapshot != null)
                    result.Add(snapshot);
            }
            return result;
        }

        static void RestoreAgents(List<AgentState> snapshots)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
                RestoreAgent(snapshots[i]);
        }

        // ------------------------------------------------------------------
        // Field capture (cached per-type plans)
        // ------------------------------------------------------------------
        //
        // Every field of a captured object is classified once per Type (FieldPlan, cached in
        // FieldCache), so a capture costs one FieldInfo.GetValue per field plus the copy:
        //   Plain     primitives, enums, strings, Unity value structs, and any struct made only of
        //             such values and object references (PhysicsCastResult, PortalTraversalV2,
        //             Matrix4x4, LayerMask, ...): stored boxed and written back as-is.
        //   TimeSince ULTRAKILL's TimeSince stamp, rebased by the virtual time passed since capture.
        //   Array     one-dimensional array of primitives: cloned, copied back element-wise.
        //   Rebased   float/double absolute stamps listed in RebaseFields, shifted by their clock.
        //   Deep      everything else that holds state: List/HashSet/Queue/Stack/Dictionary (nested
        //             too), Nullable<T> (Nullable<TimeSince> rebased), arrays of structs/objects,
        //             AnimationCurve keys, UnscaledTimeSince, and the game's own plain classes and
        //             structs (EnemyTarget, HurtCooldownCollection, CaughtEnemy, EnemyScanner...)
        //             captured recursively (MaxDeepDepth, shared/cyclic instances kept shared).
        //             A restore refills the very instances that existed at capture and puts them
        //             back into the field, so every other holder of a shared object rewinds too;
        //             readonly collection fields are refilled in place.
        //   Null      a reference/Nullable field that was empty at capture: written back as null.
        // UnityEngine.Object fields of the captured script itself stay with CaptureReferences;
        // inside Deep values a reference is restored only while the object is alive (dead list
        // and set members are dropped, like RestoreListRefs).

        /// <summary>Generous: NewMovement alone has ~150 captured-worthy fields and the old cap of 64 truncated it.</summary>
        const int FieldCap = 1024;
        /// <summary>Object references per script; empty ones count too since they are restored.</summary>
        const int RefCap = 1024;
        /// <summary>Nesting depth of plain objects and collections; deeper values keep their identity only.</summary>
        const int MaxDeepDepth = 6;
        /// <summary>Values inside one script's collections before the rest are kept by identity only (noted once).</summary>
        const int MaxDeepNodes = 200000;

        /// <summary>Time.time shift (now minus capture) applied to TimeSince stamps during a restore.</summary>
        static float _restoreTimeDelta;
        /// <summary>Time.unscaledTime shift for UnscaledTimeSince stamps.</summary>
        static float _restoreUnscaledDelta;
        /// <summary>Input System clock shift for NewMovement's slide/jump stamps.</summary>
        static double _restoreInputDelta;

        /// <summary>
        /// Light capture (the rolling history after a slow snapshot): Deep fields are skipped, so
        /// such a snapshot restores collections and plain objects as they are now.
        /// </summary>
        static bool _lightCapture;

        /// <summary>Log sink for the static helpers (set by the constructor).</summary>
        static Action<string> _staticWarn;
        static readonly HashSet<string> CapNotices = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A safety limit was reached: say so once per session instead of truncating silently.</summary>
        static void NoteCap(string what, int cap)
        {
            if (!CapNotices.Add(what))
                return;
            Action<string> warn = _staticWarn;
            if (warn != null)
                warn("Savestate safety limit reached: " + what + " (" + cap + "). Anything beyond it was not saved.");
        }

        /// <summary>
        /// Absolute time stamps kept in plain float/double fields, by "Type.field", and the clock
        /// each comes from. The game keeps its timers in TimeSince (handled on its own) or as
        /// durations: an IL scan of Assembly-CSharp (every ldfld/stfld of a float or double within
        /// four instructions of a UnityEngine.Time getter) found no float field holding Time.time.
        /// The fields once suspected - Turret.nextBeepTime (aimTime-relative), StyleCalculator.
        /// lastAirTime (copy of airTime), PortalIdentifier.lastUpdateTimer (deltaTime accumulator)
        /// and HudMessage.timerTime (Invoke delay) - are durations and must NOT be shifted, so they
        /// stay plain values. NewMovement's slide/jump stamps hold InputAction.CallbackContext.time
        /// and are compared with InputState.currentTime.
        /// </summary>
        static readonly Dictionary<string, TimeClock> RebaseFields = new Dictionary<string, TimeClock>(StringComparer.Ordinal)
        {
            { "NewMovement.slideTimestamp", TimeClock.Input },
            { "NewMovement.jumpTimestamp", TimeClock.Input }
        };

        /// <summary>
        /// Game classes whose float/double fields named like a stamp (time/timer/stamp/next/last)
        /// are rebased on Time.time when their value at capture lies within RebaseWindow of it.
        /// Empty on purpose: the scan above found no class storing Time.time in a float, and the
        /// shift would corrupt a countdown that happens to fall inside the window. Add a class only
        /// after its IL shows the fields are absolute stamps.
        /// </summary>
        static readonly HashSet<string> RebaseHeuristicClasses = new HashSet<string>(StringComparer.Ordinal);
        static readonly System.Text.RegularExpressions.Regex RebaseNamePattern =
            new System.Text.RegularExpressions.Regex("(time|timer|stamp|next|last)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        const double RebaseWindow = 1e5;

        /// <summary>How a value of one Type is captured. Container and object kinds come last (see CaptureDeep).</summary>
        enum PlanKind : byte
        {
            Unsupported,
            Null,
            Scalar,
            TimeSince,
            UnscaledTimeSince,
            Rebased,
            UnityRef,
            Nullable,
            Curve,
            Array,
            List,
            HashSet,
            Queue,
            Stack,
            Dictionary,
            Object
        }

        sealed class TypePlan
        {
            public PlanKind Kind;
            public Type Type;
            /// <summary>Element type of an array/collection, key type of a dictionary.</summary>
            public Type ElementType;
            /// <summary>Plan of the elements, the dictionary keys, or the value of a Nullable.</summary>
            public TypePlan Element;
            /// <summary>Plan of the dictionary values.</summary>
            public TypePlan Value;
            /// <summary>Fields of a plain class/struct.</summary>
            public FieldPlan[] Fields;
            public MethodInfo Clear;
            /// <summary>HashSet.Add / Queue.Enqueue / Stack.Push.</summary>
            public MethodInfo Add;
            /// <summary>List.AddRange (fast refill from a typed array).</summary>
            public MethodInfo AddRange;
        }

        sealed class FieldPlan
        {
            public FieldInfo Field;
            public TypePlan Plan;
            public FieldKind Kind;
            public bool ReadOnly;
            public TimeClock Clock;
            public bool HeuristicRebase;
        }

        /// <summary>One captured value inside a Deep field.</summary>
        sealed class DeepValue
        {
            public PlanKind Kind;
            public TypePlan Plan;
            /// <summary>Scalar / reference / stamp value, or the instance a container/object was read from.</summary>
            public object Value;
            /// <summary>Typed copy of a container whose elements are plain values.</summary>
            public Array Fast;
            /// <summary>Elements, dictionary keys, or field values of a plain object.</summary>
            public DeepValue[] Items;
            /// <summary>Dictionary values.</summary>
            public DeepValue[] Values;
            public int Length;
            public TimeClock Clock;
            public Keyframe[] Keys;
            public WrapMode PreWrap;
            public WrapMode PostWrap;
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }

        sealed class DeepContext
        {
            /// <summary>Reference-type instances already captured in this call: shared and cyclic objects stay one node.</summary>
            public readonly Dictionary<object, DeepValue> Seen = new Dictionary<object, DeepValue>(ReferenceComparer.Instance);
            public int Nodes;
        }

        static readonly DeepValue NullValue = new DeepValue { Kind = PlanKind.Null };
        /// <summary>Returned by RestoreDeep when the current value must be left alone.</summary>
        static readonly object SkipValue = new object();
        static readonly DeepContext CaptureContext = new DeepContext();
        /// <summary>Instance each container/object node was restored into, per ApplyFields call.</summary>
        static readonly Dictionary<DeepValue, object> RestoredNodes = new Dictionary<DeepValue, object>();

        static readonly Dictionary<Type, TypePlan> TypePlans = new Dictionary<Type, TypePlan>();
        static readonly Dictionary<Type, bool> ShallowStructs = new Dictionary<Type, bool>();
        static readonly Dictionary<Type, FieldInfo> StampFields = new Dictionary<Type, FieldInfo>();
        static Assembly _gameAssemblyStatic;

        static Assembly GameAssembly
        {
            get
            {
                if (_gameAssemblyStatic != null)
                    return _gameAssemblyStatic;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == "Assembly-CSharp")
                    {
                        _gameAssemblyStatic = assemblies[i];
                        break;
                    }
                }
                return _gameAssemblyStatic;
            }
        }

        static bool IsGameType(Type type)
        {
            Assembly game = GameAssembly;
            return game != null && type.Assembly == game;
        }

        /// <summary>The float "time" field of TimeSince / UnscaledTimeSince.</summary>
        static FieldInfo StampTimeField(Type stampType)
        {
            FieldInfo field;
            if (StampFields.TryGetValue(stampType, out field))
                return field;
            field = stampType.GetField("time", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType != typeof(float))
                field = null;
            StampFields[stampType] = field;
            return field;
        }

        static bool IsTimeSince(Type type)
        {
            return type.IsValueType && type.Name == "TimeSince";
        }

        static bool IsUnscaledTimeSince(Type type)
        {
            return type.IsValueType && type.Name == "UnscaledTimeSince";
        }

        /// <summary>Handles to native memory: writing an old value back could free or corrupt it.</summary>
        static bool IsNativeType(Type type)
        {
            if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr) ||
                type == typeof(System.Runtime.InteropServices.GCHandle))
                return true;
            string ns = type.Namespace;
            if (ns != null && (ns.StartsWith("Unity.Collections", StringComparison.Ordinal) ||
                               ns.StartsWith("Unity.Jobs", StringComparison.Ordinal) ||
                               ns == "UnityEngine.Jobs"))
                return true;
            string name = type.Name;
            return name == "ComputeBuffer" || name == "GraphicsBuffer" || name == "CommandBuffer" ||
                   name == "MaterialPropertyBlock";
        }

        /// <summary>
        /// A struct that is copied by value as a whole: only primitives, enums, strings, object
        /// references, arrays (shared like any reference) and further such structs. A TimeSince
        /// inside, a collection, a plain class or a native handle makes it a Deep struct instead.
        /// </summary>
        static bool IsShallowStruct(Type type)
        {
            bool cached;
            if (ShallowStructs.TryGetValue(type, out cached))
                return cached;
            ShallowStructs[type] = false;
            bool result = ComputeShallowStruct(type);
            ShallowStructs[type] = result;
            return result;
        }

        static bool ComputeShallowStruct(Type type)
        {
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum || IsTimeSince(type) ||
                IsUnscaledTimeSince(type) || IsNativeType(type))
                return false;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return false;
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                Type fieldType = fields[i].FieldType;
                if (fieldType.IsPointer)
                    return false;
                if (fieldType.IsPrimitive)
                {
                    if (fieldType == typeof(IntPtr) || fieldType == typeof(UIntPtr))
                        return false;
                    continue;
                }
                if (fieldType.IsEnum || fieldType == typeof(string) || fieldType.IsArray ||
                    typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
                    continue;
                if (fieldType.IsValueType && IsShallowStruct(fieldType))
                    continue;
                return false;
            }
            return true;
        }

        /// <summary>Plain classes/structs whose fields are captured recursively.</summary>
        static bool IsDeepCandidate(Type type)
        {
            if (type.IsInterface || type == typeof(object) || type == typeof(ValueType) || type.IsPointer || type.IsByRef)
                return false;
            if (typeof(Delegate).IsAssignableFrom(type))
                return false;
            // Compiler-generated iterators (coroutines) and closures belong to the coroutine module.
            if (type.Name.IndexOf('<') >= 0 || type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                return false;
            if (typeof(IEnumerator).IsAssignableFrom(type) || typeof(YieldInstruction).IsAssignableFrom(type) ||
                typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(type))
                return false;
            // Trigger wiring (objects to switch, UnityEvents): configuration, not state.
            if (type.Name == "UltrakillEvent")
                return false;
            if (type.IsValueType)
                return IsGameType(type) ||
                       (type.Namespace == "System" && type.Name.StartsWith("ValueTuple", StringComparison.Ordinal));
            if (!IsGameType(type))
                return false;
            string ns = type.Namespace;
            return ns == null || !(ns.StartsWith("plog", StringComparison.Ordinal) ||
                                   ns.StartsWith("GameConsole", StringComparison.Ordinal) ||
                                   ns.StartsWith("SettingsMenu", StringComparison.Ordinal) ||
                                   ns.StartsWith("Steamworks", StringComparison.Ordinal) ||
                                   ns.StartsWith("Discord", StringComparison.Ordinal));
        }

        static TypePlan PlanFor(Type type)
        {
            TypePlan plan;
            if (TypePlans.TryGetValue(type, out plan))
                return plan;
            // Cached before it is built, so a class that refers to itself terminates.
            plan = new TypePlan { Type = type };
            TypePlans[type] = plan;
            try { BuildPlan(plan); }
            catch { plan.Kind = PlanKind.Unsupported; }
            return plan;
        }

        static void BuildPlan(TypePlan plan)
        {
            Type type = plan.Type;
            if (IsTimeSince(type))
            {
                plan.Kind = PlanKind.TimeSince;
                return;
            }
            if (IsUnscaledTimeSince(type))
            {
                plan.Kind = PlanKind.UnscaledTimeSince;
                return;
            }
            if (IsSupportedScalar(type) || IsShallowStruct(type))
            {
                plan.Kind = PlanKind.Scalar;
                return;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                plan.Kind = PlanKind.UnityRef;
                return;
            }
            if (type == typeof(AnimationCurve))
            {
                plan.Kind = PlanKind.Curve;
                return;
            }
            if (type.IsPointer || type.IsByRef || type.ContainsGenericParameters || IsNativeType(type))
                return;

            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                    return;
                TypePlan element = PlanFor(type.GetElementType());
                if (element.Kind == PlanKind.Unsupported)
                    return;
                plan.ElementType = type.GetElementType();
                plan.Element = element;
                plan.Kind = PlanKind.Array;
                return;
            }

            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                Type[] args = type.GetGenericArguments();
                if (definition == typeof(Nullable<>))
                {
                    TypePlan inner = PlanFor(args[0]);
                    if (inner.Kind == PlanKind.Unsupported)
                        return;
                    plan.Element = inner;
                    plan.ElementType = args[0];
                    plan.Kind = PlanKind.Nullable;
                    return;
                }

                PlanKind collection =
                    definition == typeof(List<>) ? PlanKind.List :
                    definition == typeof(HashSet<>) ? PlanKind.HashSet :
                    definition == typeof(Queue<>) ? PlanKind.Queue :
                    definition == typeof(Stack<>) ? PlanKind.Stack :
                    definition == typeof(Dictionary<,>) ? PlanKind.Dictionary : PlanKind.Unsupported;
                if (collection != PlanKind.Unsupported)
                {
                    TypePlan element = PlanFor(args[0]);
                    if (element.Kind == PlanKind.Unsupported)
                        return;
                    if (collection == PlanKind.Dictionary)
                    {
                        TypePlan value = PlanFor(args[1]);
                        if (value.Kind == PlanKind.Unsupported)
                            return;
                        plan.Value = value;
                    }
                    plan.Element = element;
                    plan.ElementType = args[0];
                    plan.Clear = type.GetMethod("Clear", Type.EmptyTypes);
                    switch (collection)
                    {
                        case PlanKind.List:
                            plan.AddRange = type.GetMethod("AddRange");
                            break;
                        case PlanKind.HashSet:
                            plan.Add = type.GetMethod("Add", new[] { args[0] });
                            break;
                        case PlanKind.Queue:
                            plan.Add = type.GetMethod("Enqueue", new[] { args[0] });
                            break;
                        case PlanKind.Stack:
                            plan.Add = type.GetMethod("Push", new[] { args[0] });
                            break;
                    }
                    if ((collection == PlanKind.HashSet || collection == PlanKind.Queue || collection == PlanKind.Stack) &&
                        (plan.Clear == null || plan.Add == null))
                        return;
                    plan.Kind = collection;
                    return;
                }
            }

            if (!IsDeepCandidate(type))
                return;
            // Provisional, so a field of the class's own type resolves to this plan.
            plan.Kind = PlanKind.Object;
            FieldPlan[] fields = BuildFieldPlans(type, false);
            if (fields == null || fields.Length == 0)
            {
                plan.Kind = PlanKind.Unsupported;
                return;
            }
            plan.Fields = fields;
        }

        /// <summary>
        /// Fields of <paramref name="type"/> worth capturing. Top level (a captured script):
        /// object references are left to CaptureReferences and every base class up to
        /// MonoBehaviour is walked. Nested (a plain class/struct): references are included, only
        /// the game's own base classes are walked, and a class that mirrors native memory is
        /// not captured at all (null).
        /// </summary>
        static FieldPlan[] BuildFieldPlans(Type type, bool topLevel)
        {
            List<FieldPlan> result = new List<FieldPlan>(16);
            for (Type current = type;
                 current != null && current != typeof(object) && current != typeof(ValueType) &&
                 current != typeof(MonoBehaviour) && current != typeof(Behaviour) && current != typeof(Component) &&
                 current != typeof(UnityEngine.Object) && current != typeof(ScriptableObject);
                 current = current.BaseType)
            {
                if (!topLevel && current != type && !IsGameType(current))
                    break;
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    FieldInfo field = declared[i];
                    // One field whose type cannot be loaded must not cost the others.
                    try
                    {
                        if (field.IsLiteral)
                            continue;
                        if (!topLevel && !type.IsValueType && IsNativeType(field.FieldType))
                            return null;
                        FieldPlan plan = ClassifyField(field, !topLevel);
                        if (plan != null)
                            result.Add(plan);
                    }
                    catch
                    {
                    }
                }
            }
            return result.ToArray();
        }

        static bool IsFilledInPlace(TypePlan plan)
        {
            switch (plan.Kind)
            {
                case PlanKind.Array:
                case PlanKind.List:
                case PlanKind.HashSet:
                case PlanKind.Queue:
                case PlanKind.Stack:
                case PlanKind.Dictionary:
                case PlanKind.Curve:
                    return true;
                case PlanKind.Object:
                    return !plan.Type.IsValueType;
                default:
                    return false;
            }
        }

        /// <summary>How one field is captured, or null to leave it alone.</summary>
        static FieldPlan ClassifyField(FieldInfo field, bool allowReferences)
        {
            Type fieldType = field.FieldType;
            TypePlan plan = PlanFor(fieldType);
            if (plan.Kind == PlanKind.Unsupported)
                return null;
            if (plan.Kind == PlanKind.UnityRef && !allowReferences)
                return null;
            bool readOnly = field.IsInitOnly;
            // A readonly collection or object is refilled in place; a readonly value never changes.
            if (readOnly && !IsFilledInPlace(plan))
                return null;

            FieldPlan result = new FieldPlan { Field = field, Plan = plan, ReadOnly = readOnly };
            switch (plan.Kind)
            {
                case PlanKind.Scalar:
                    result.Kind = FieldKind.Plain;
                    break;
                case PlanKind.TimeSince:
                    result.Kind = FieldKind.TimeSince;
                    break;
                case PlanKind.Array:
                    // Cooldown / charge tables such as WeaponCharges.shoaltcooldowns keep the old path.
                    result.Kind = plan.Element.Kind == PlanKind.Scalar && IsSupportedScalar(plan.ElementType) &&
                                  plan.ElementType != typeof(string)
                        ? FieldKind.Array
                        : FieldKind.Deep;
                    break;
                default:
                    result.Kind = FieldKind.Deep;
                    break;
            }

            if (fieldType == typeof(float) || fieldType == typeof(double))
            {
                Type owner = field.DeclaringType;
                TimeClock clock;
                if (owner != null && RebaseFields.TryGetValue(owner.Name + "." + field.Name, out clock))
                {
                    result.Kind = FieldKind.Rebased;
                    result.Clock = clock;
                }
                else if (fieldType == typeof(double) && owner != null && IsGameType(owner) &&
                         field.Name.EndsWith("Timestamp", StringComparison.Ordinal))
                {
                    // Input System event stamps (slideTimestamp / jumpTimestamp and any added later).
                    result.Kind = FieldKind.Rebased;
                    result.Clock = TimeClock.Input;
                }
                else if (owner != null && RebaseHeuristicClasses.Contains(owner.Name) &&
                         RebaseNamePattern.IsMatch(field.Name))
                {
                    result.HeuristicRebase = true;
                }
            }
            return result;
        }

        static FieldPlan[] GetCaptureFields(Type type)
        {
            FieldPlan[] cached;
            if (FieldCache.TryGetValue(type, out cached))
                return cached;
            try { cached = BuildFieldPlans(type, true) ?? new FieldPlan[0]; }
            catch { cached = new FieldPlan[0]; }
            FieldCache[type] = cached;
            return cached;
        }

        static bool IsSupportedScalar(Type type)
        {
            if (type.IsEnum)
                return true;
            if (type.IsPrimitive)
                return type != typeof(IntPtr) && type != typeof(UIntPtr);
            return type == typeof(string) || type == typeof(Vector2) || type == typeof(Vector3) ||
                   type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color) ||
                   type == typeof(Color32) || type == typeof(Vector2Int) || type == typeof(Vector3Int) ||
                   type == typeof(Rect) || type == typeof(Bounds) || type == typeof(LayerMask) ||
                   type == typeof(Matrix4x4) || type == typeof(Plane) || type == typeof(Ray);
        }

        /// <summary>True when a field of this type is captured by CaptureFields (or, for object references, by CaptureReferences).</summary>
        static bool IsSupportedType(Type type)
        {
            return PlanFor(type).Kind != PlanKind.Unsupported;
        }

        /// <summary>
        /// Active and enabled: Awake and Start have run, so a field that is empty now is empty on
        /// purpose. A disabled or sleeping script may still fill its caches later, and clearing
        /// them again on a load would leave it broken.
        /// </summary>
        static bool IsLiveTarget(object target)
        {
            try
            {
                Behaviour behaviour = target as Behaviour;
                if (behaviour != null)
                    return behaviour.isActiveAndEnabled;
                Component component = target as Component;
                if (component != null)
                    return component.gameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
            return true;
        }

        static List<FieldState> CaptureFields(object target, int max)
        {
            if (target == null)
                return null;
            if (max < FieldCap)
                max = FieldCap;
            FieldPlan[] fields = GetCaptureFields(target.GetType());
            List<FieldState> values = new List<FieldState>(Mathf.Min(max, fields.Length));
            DeepContext context = null;
            int live = -1;
            for (int i = 0; i < fields.Length && values.Count < max; i++)
            {
                FieldPlan plan = fields[i];
                if (_lightCapture && plan.Kind == FieldKind.Deep)
                    continue;
                object value;
                try { value = plan.Field.GetValue(target); }
                catch { continue; }

                if (value == null && plan.Kind == FieldKind.Deep && !plan.Field.FieldType.IsValueType)
                {
                    if (live < 0)
                    {
                        try { live = IsLiveTarget(target) ? 1 : 0; }
                        catch { live = 0; }
                    }
                    if (live == 0)
                        continue;
                }

                FieldState state;
                try { state = CaptureField(plan, target, value, ref context); }
                catch { continue; }
                if (state != null)
                    values.Add(state);
            }
            if (values.Count >= max && fields.Length > max)
                NoteCap("fields per script", max);
            return values;
        }

        static FieldState CaptureField(FieldPlan plan, object owner, object value, ref DeepContext context)
        {
            FieldState state = new FieldState { Field = plan.Field, Name = plan.Field.Name, Value = value };
            switch (plan.Kind)
            {
                case FieldKind.TimeSince:
                {
                    FieldInfo timeField = StampTimeField(plan.Field.FieldType);
                    if (timeField == null || value == null)
                        return null;
                    state.Kind = FieldKind.TimeSince;
                    state.Value = timeField.GetValue(value);
                    break;
                }
                case FieldKind.Array:
                {
                    Array array = value as Array;
                    state.Kind = FieldKind.Array;
                    state.Value = array != null ? array.Clone() : null;
                    break;
                }
                case FieldKind.Rebased:
                    state.Kind = FieldKind.Rebased;
                    state.Clock = plan.Clock;
                    break;
                case FieldKind.Deep:
                {
                    if (value == null)
                    {
                        state.Kind = FieldKind.Null;
                        break;
                    }
                    if (context == null)
                    {
                        context = CaptureContext;
                        context.Seen.Clear();
                        context.Nodes = 0;
                    }
                    DeepValue node = CaptureDeep(plan.Plan, value, 0, context);
                    if (node == null)
                        return null;
                    state.Kind = FieldKind.Deep;
                    state.Value = node;
                    state.Owner = owner;
                    break;
                }
                default:
                    state.Kind = FieldKind.Plain;
                    if (plan.HeuristicRebase && IsNearNow(value))
                    {
                        state.Kind = FieldKind.Rebased;
                        state.Clock = TimeClock.Scaled;
                    }
                    break;
            }
            return state;
        }

        static bool IsNearNow(object value)
        {
            double stamp;
            if (value is float)
                stamp = (float)value;
            else if (value is double)
                stamp = (double)value;
            else
                return false;
            double now = Time.time;
            return stamp >= now - RebaseWindow && stamp <= now + RebaseWindow;
        }

        /// <summary>
        /// Elements copied as one typed array: plain values, and object references (which are
        /// checked for being alive on restore instead of getting a node each; buffers such as
        /// a Collider[256] would otherwise cost an allocation per slot on every capture).
        /// </summary>
        static bool IsFlatElement(TypePlan element)
        {
            return element.Kind == PlanKind.Scalar || element.Kind == PlanKind.UnityRef;
        }

        /// <summary>Keeps only the identity of a value (past the depth or value budget).</summary>
        static DeepValue IdentityValue(object value)
        {
            return new DeepValue { Kind = PlanKind.Scalar, Value = value };
        }

        static DeepValue CaptureDeep(TypePlan plan, object value, int depth, DeepContext context)
        {
            if (value == null)
                return NullValue;
            if (++context.Nodes > MaxDeepNodes)
            {
                NoteCap("values inside one script's collections", MaxDeepNodes);
                return IdentityValue(value);
            }

            switch (plan.Kind)
            {
                case PlanKind.Scalar:
                    return new DeepValue { Kind = PlanKind.Scalar, Value = value };
                case PlanKind.TimeSince:
                case PlanKind.UnscaledTimeSince:
                {
                    FieldInfo timeField = StampTimeField(plan.Type);
                    if (timeField == null)
                        return IdentityValue(value);
                    return new DeepValue { Kind = plan.Kind, Plan = plan, Value = timeField.GetValue(value) };
                }
                case PlanKind.UnityRef:
                    // A destroyed object reads as null to the game; it is restored as null.
                    return GameBridge.IsAlive(value) ? new DeepValue { Kind = PlanKind.UnityRef, Value = value } : NullValue;
                case PlanKind.Nullable:
                    // A Nullable boxes as its value (or null, handled above).
                    return CaptureDeep(plan.Element, value, depth, context);
                case PlanKind.Curve:
                    return CaptureCurve(plan, value as AnimationCurve);
                case PlanKind.Unsupported:
                case PlanKind.Null:
                case PlanKind.Rebased:
                    return IdentityValue(value);
            }

            // Containers and plain objects.
            Type runtime = value.GetType();
            if (runtime != plan.Type && !plan.Type.IsValueType)
            {
                // A subclass (or the concrete class behind an abstract field type).
                plan = PlanFor(runtime);
                if (plan.Kind < PlanKind.Array)
                    return IdentityValue(value);
            }
            if (depth >= MaxDeepDepth)
            {
                NoteCap("nesting depth of plain objects/collections", MaxDeepDepth);
                return IdentityValue(value);
            }

            bool reference = !runtime.IsValueType;
            DeepValue seen;
            if (reference && context.Seen.TryGetValue(value, out seen))
                return seen;
            DeepValue node = new DeepValue { Kind = plan.Kind, Plan = plan, Value = value };
            if (reference)
                context.Seen[value] = node;

            switch (plan.Kind)
            {
                case PlanKind.Array:
                {
                    Array array = (Array)value;
                    node.Length = array.Length;
                    if (IsFlatElement(plan.Element))
                    {
                        node.Fast = (Array)array.Clone();
                        break;
                    }
                    node.Items = new DeepValue[array.Length];
                    for (int i = 0; i < array.Length; i++)
                        node.Items[i] = CaptureDeep(plan.Element, array.GetValue(i), depth + 1, context);
                    break;
                }
                case PlanKind.List:
                {
                    IList list = (IList)value;
                    int count = list.Count;
                    node.Length = count;
                    if (IsFlatElement(plan.Element))
                    {
                        Array fast = Array.CreateInstance(plan.ElementType, count);
                        ((ICollection)list).CopyTo(fast, 0);
                        node.Fast = fast;
                        break;
                    }
                    node.Items = new DeepValue[count];
                    for (int i = 0; i < count; i++)
                        node.Items[i] = CaptureDeep(plan.Element, list[i], depth + 1, context);
                    break;
                }
                case PlanKind.HashSet:
                case PlanKind.Queue:
                case PlanKind.Stack:
                {
                    // Enumeration order: set order, queue front first, stack top first.
                    List<object> items = new List<object>();
                    foreach (object item in (IEnumerable)value)
                        items.Add(item);
                    node.Length = items.Count;
                    if (IsFlatElement(plan.Element))
                    {
                        Array fast = Array.CreateInstance(plan.ElementType, items.Count);
                        for (int i = 0; i < items.Count; i++)
                            fast.SetValue(items[i], i);
                        node.Fast = fast;
                        break;
                    }
                    node.Items = new DeepValue[items.Count];
                    for (int i = 0; i < items.Count; i++)
                        node.Items[i] = CaptureDeep(plan.Element, items[i], depth + 1, context);
                    break;
                }
                case PlanKind.Dictionary:
                {
                    IDictionary dictionary = (IDictionary)value;
                    int count = dictionary.Count;
                    node.Items = new DeepValue[count];
                    node.Values = new DeepValue[count];
                    int n = 0;
                    IDictionaryEnumerator entries = dictionary.GetEnumerator();
                    while (n < count && entries.MoveNext())
                    {
                        node.Items[n] = CaptureDeep(plan.Element, entries.Key, depth + 1, context);
                        node.Values[n] = CaptureDeep(plan.Value, entries.Value, depth + 1, context);
                        n++;
                    }
                    node.Length = n;
                    break;
                }
                case PlanKind.Object:
                {
                    FieldPlan[] fields = plan.Fields;
                    node.Items = new DeepValue[fields.Length];
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldPlan field = fields[i];
                        object fieldValue;
                        try { fieldValue = field.Field.GetValue(value); }
                        catch { continue; }
                        if (field.Kind == FieldKind.Rebased || (field.HeuristicRebase && IsNearNow(fieldValue)))
                        {
                            node.Items[i] = new DeepValue
                            {
                                Kind = PlanKind.Rebased,
                                Value = fieldValue,
                                Clock = field.Kind == FieldKind.Rebased ? field.Clock : TimeClock.Scaled
                            };
                            continue;
                        }
                        node.Items[i] = CaptureDeep(field.Plan, fieldValue, depth + 1, context);
                    }
                    break;
                }
                default:
                    return IdentityValue(value);
            }
            return node;
        }

        // Unity native calls are kept in their own methods, out of the hot generic paths.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static DeepValue CaptureCurve(TypePlan plan, AnimationCurve curve)
        {
            if (curve == null)
                return NullValue;
            return new DeepValue
            {
                Kind = PlanKind.Curve,
                Plan = plan,
                Value = curve,
                Keys = curve.keys,
                PreWrap = curve.preWrapMode,
                PostWrap = curve.postWrapMode
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static object RestoreCurve(DeepValue node, object current, bool foreign)
        {
            AnimationCurve curve = (foreign ? current : node.Value) as AnimationCurve;
            if (curve == null)
                curve = new AnimationCurve();
            curve.keys = node.Keys;
            curve.preWrapMode = node.PreWrap;
            curve.postWrapMode = node.PostWrap;
            return curve;
        }

        static object RebaseValue(object value, TimeClock clock)
        {
            double delta = clock == TimeClock.Input ? _restoreInputDelta :
                           clock == TimeClock.Unscaled ? _restoreUnscaledDelta : _restoreTimeDelta;
            if (value is float)
                return (float)((float)value + delta);
            if (value is double)
                return (double)value + delta;
            return value;
        }

        static void ApplyFields(object target, List<FieldState> values)
        {
            if (target == null || values == null)
                return;
            RestoredNodes.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                try { ApplyField(target, values[i]); }
                catch { }
            }
            RestoredNodes.Clear();
        }

        /// <summary>Writes one captured field; <paramref name="target"/> is null for a static field.</summary>
        static void ApplyField(object target, FieldState state)
        {
            switch (state.Kind)
            {
                case FieldKind.TimeSince:
                {
                    FieldInfo timeField = StampTimeField(state.Field.FieldType);
                    if (timeField == null)
                        break;
                    object boxed = state.Field.GetValue(target);
                    timeField.SetValue(boxed, (float)state.Value + _restoreTimeDelta);
                    state.Field.SetValue(target, boxed);
                    break;
                }
                case FieldKind.Array:
                {
                    Array saved = state.Value as Array;
                    if (saved == null)
                        break;
                    Array current = state.Field.GetValue(target) as Array;
                    if (current != null && current.Length == saved.Length)
                        Array.Copy(saved, current, saved.Length);
                    else if (!state.Field.IsInitOnly)
                        state.Field.SetValue(target, saved.Clone());
                    break;
                }
                case FieldKind.Null:
                    if (!state.Field.IsInitOnly)
                        state.Field.SetValue(target, null);
                    break;
                case FieldKind.Rebased:
                    state.Field.SetValue(target, RebaseValue(state.Value, state.Clock));
                    break;
                case FieldKind.Deep:
                {
                    DeepValue node = state.Value as DeepValue;
                    if (node == null)
                        break;
                    object current = state.Field.GetValue(target);
                    bool foreign = !ReferenceEquals(target, state.Owner);
                    object result = RestoreDeep(node, current, foreign);
                    if (result == SkipValue)
                        break;
                    if (node.Kind == PlanKind.UnityRef && !GameBridge.IsAlive(result))
                        break;
                    // A readonly collection/object was refilled in place above.
                    if (state.Field.IsInitOnly)
                        break;
                    if (state.Field.FieldType.IsValueType || !ReferenceEquals(result, current))
                        state.Field.SetValue(target, result);
                    break;
                }
                default:
                    state.Field.SetValue(target, state.Value);
                    break;
            }
        }

        /// <summary>
        /// The value to store for a captured node, or SkipValue to leave the current one.
        /// Containers and plain classes are refilled in the instance they were read from (or, when
        /// <paramref name="foreign"/>, in <paramref name="current"/> / a new instance) and that
        /// instance is returned; structs are rebuilt on a copy of the current value.
        /// </summary>
        static object RestoreDeep(DeepValue node, object current, bool foreign)
        {
            if (node == null)
                return SkipValue;
            switch (node.Kind)
            {
                case PlanKind.Null:
                    return null;
                case PlanKind.Scalar:
                case PlanKind.UnityRef:
                    return node.Value;
                case PlanKind.Rebased:
                    return RebaseValue(node.Value, node.Clock);
                case PlanKind.TimeSince:
                case PlanKind.UnscaledTimeSince:
                {
                    FieldInfo timeField = StampTimeField(node.Plan.Type);
                    if (timeField == null)
                        return SkipValue;
                    object boxed = Activator.CreateInstance(node.Plan.Type);
                    float delta = node.Kind == PlanKind.TimeSince ? _restoreTimeDelta : _restoreUnscaledDelta;
                    timeField.SetValue(boxed, (float)node.Value + delta);
                    return boxed;
                }
                case PlanKind.Curve:
                    return RestoreCurve(node, current, foreign);
            }

            object done;
            if (RestoredNodes.TryGetValue(node, out done))
                return done;
            object instance = ResolveInstance(node, current, foreign);
            if (instance == null)
                return SkipValue;
            bool reference = !node.Plan.Type.IsValueType;
            if (reference)
                RestoredNodes[node] = instance;

            switch (node.Kind)
            {
                case PlanKind.Array:
                    RestoreArray(node, (Array)instance, foreign);
                    break;
                case PlanKind.List:
                    RestoreList(node, (IList)instance, foreign);
                    break;
                case PlanKind.HashSet:
                case PlanKind.Queue:
                case PlanKind.Stack:
                    RestoreSequence(node, instance, foreign);
                    break;
                case PlanKind.Dictionary:
                    RestoreDictionary(node, (IDictionary)instance, foreign);
                    break;
                case PlanKind.Object:
                    RestoreObject(node, instance, foreign);
                    break;
                default:
                    return SkipValue;
            }
            return instance;
        }

        static object ResolveInstance(DeepValue node, object current, bool foreign)
        {
            Type type = node.Plan.Type;
            if (type.IsValueType)
            {
                // Built on the live value so fields that are not captured (native handles) stay
                // valid; a fresh copy of the saved value when there is none.
                if (current != null && current.GetType() == type)
                    return current;
                return node.Value != null ? RuntimeHelpers.GetObjectValue(node.Value) : null;
            }

            object instance = foreign
                ? (current != null && current.GetType() == type ? current : null)
                : node.Value;
            if (node.Kind == PlanKind.Array)
            {
                Array array = instance as Array;
                if (array == null || array.Length != node.Length)
                    return Array.CreateInstance(node.Plan.ElementType, node.Length);
                return array;
            }
            if (instance != null)
                return instance;
            try { return Activator.CreateInstance(type, true); }
            catch
            {
                try { return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type); }
                catch { return null; }
            }
        }

        /// <summary>Elements in order; dead object references are dropped (membership semantics).</summary>
        static List<object> RestoreItems(DeepValue node, bool foreign)
        {
            List<object> values = new List<object>(node.Length);
            if (node.Fast != null)
            {
                bool references = node.Plan.Element.Kind == PlanKind.UnityRef;
                for (int i = 0; i < node.Fast.Length; i++)
                {
                    object value = node.Fast.GetValue(i);
                    if (references && !GameBridge.IsAlive(value))
                        continue;
                    values.Add(value);
                }
                return values;
            }
            if (node.Items == null)
                return values;
            for (int i = 0; i < node.Items.Length; i++)
            {
                DeepValue item = node.Items[i];
                if (item == null)
                    continue;
                object value = RestoreDeep(item, null, foreign);
                if (value == SkipValue)
                    continue;
                if (item.Kind == PlanKind.UnityRef && !GameBridge.IsAlive(value))
                    continue;
                values.Add(value);
            }
            return values;
        }

        static void RestoreArray(DeepValue node, Array array, bool foreign)
        {
            if (node.Fast != null)
            {
                Array.Copy(node.Fast, array, Math.Min(node.Fast.Length, array.Length));
                return;
            }
            if (node.Items == null)
                return;
            int count = Math.Min(node.Items.Length, array.Length);
            for (int i = 0; i < count; i++)
            {
                DeepValue item = node.Items[i];
                if (item == null)
                    continue;
                try
                {
                    object current = item.Kind >= PlanKind.Curve ? array.GetValue(i) : null;
                    object value = RestoreDeep(item, current, foreign);
                    // Positions matter in an array: a destroyed member is written back (as the
                    // game would now see it) rather than shifting the others.
                    if (value != SkipValue)
                        array.SetValue(value, i);
                }
                catch
                {
                }
            }
        }

        static void RestoreList(DeepValue node, IList list, bool foreign)
        {
            if (node.Fast != null && node.Plan.AddRange != null && node.Plan.Element.Kind == PlanKind.Scalar)
            {
                list.Clear();
                node.Plan.AddRange.Invoke(list, new object[] { node.Fast });
                return;
            }
            List<object> values = RestoreItems(node, foreign);
            list.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                try { list.Add(values[i]); }
                catch { }
            }
        }

        static void RestoreSequence(DeepValue node, object collection, bool foreign)
        {
            List<object> values = RestoreItems(node, foreign);
            node.Plan.Clear.Invoke(collection, null);
            object[] argument = new object[1];
            bool stack = node.Kind == PlanKind.Stack;
            for (int i = 0; i < values.Count; i++)
            {
                // A stack enumerates top first, so it is rebuilt from the bottom.
                argument[0] = values[stack ? values.Count - 1 - i : i];
                try { node.Plan.Add.Invoke(collection, argument); }
                catch { }
            }
        }

        static void RestoreDictionary(DeepValue node, IDictionary dictionary, bool foreign)
        {
            int count = node.Items != null ? node.Items.Length : 0;
            object[] keys = new object[count];
            object[] values = new object[count];
            bool[] keep = new bool[count];
            for (int i = 0; i < count; i++)
            {
                DeepValue keyNode = node.Items[i];
                DeepValue valueNode = node.Values[i];
                if (keyNode == null || valueNode == null)
                    continue;
                object key = RestoreDeep(keyNode, null, foreign);
                // An entry keyed by an object destroyed since the save (a dead enemy) is dropped.
                if (key == null || key == SkipValue || (keyNode.Kind == PlanKind.UnityRef && !GameBridge.IsAlive(key)))
                    continue;
                object value = RestoreDeep(valueNode, null, foreign);
                if (value == SkipValue)
                    continue;
                keys[i] = key;
                values[i] = value;
                keep[i] = true;
            }
            dictionary.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!keep[i])
                    continue;
                try { dictionary[keys[i]] = values[i]; }
                catch { }
            }
        }

        static void RestoreObject(DeepValue node, object instance, bool foreign)
        {
            FieldPlan[] fields = node.Plan.Fields;
            if (fields == null || node.Items == null)
                return;
            int count = Math.Min(fields.Length, node.Items.Length);
            for (int i = 0; i < count; i++)
            {
                DeepValue item = node.Items[i];
                if (item == null)
                    continue;
                FieldPlan field = fields[i];
                try
                {
                    object current = item.Kind >= PlanKind.Curve ? field.Field.GetValue(instance) : null;
                    object value = RestoreDeep(item, current, foreign);
                    if (value == SkipValue)
                        continue;
                    // Like ApplyReferences: a reference whose object died is left alone.
                    if (item.Kind == PlanKind.UnityRef && !GameBridge.IsAlive(value))
                        continue;
                    if (field.ReadOnly)
                        continue;
                    field.Field.SetValue(instance, value);
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // Static fields (opt-in allowlist)
        // ------------------------------------------------------------------

        /// <summary>
        /// Mutable statics that carry gameplay state, as (type, field). Found by scanning
        /// Assembly-CSharp for non-readonly static fields written outside a static constructor;
        /// UI state, preference mirrors, cheat toggles, singleton instances, Burst function
        /// pointers and scratch buffers (PortalPhysicsV2.traversalsList) were left out.
        /// </summary>
        static readonly string[][] StaticFieldNames =
        {
            // Scene names the level-exit and restart logic read.
            new[] { "SceneHelper", "<CurrentScene>k__BackingField" },
            new[] { "SceneHelper", "<LastScene>k__BackingField" },
            new[] { "SceneHelper", "<PendingScene>k__BackingField" },
            // Multiplies every explosion's speed and size in Explosion.Start.
            new[] { "Explosion", "globalSizeMulti" },
            // Where the last PlayerActivator fired (respawn/teleport logic).
            new[] { "PlayerActivator", "lastActivatedPosition" },
            // Set by SetFishZone while the player stands in a fishing zone.
            new[] { "FishingRodWeapon", "suggestedDistanceMulti" },
            new[] { "FishingRodWeapon", "minDistanceMulti" }
        };

        readonly List<FieldPlan> _staticPlans = new List<FieldPlan>(8);

        void ResolveStaticFields()
        {
            _staticPlans.Clear();
            for (int i = 0; i < StaticFieldNames.Length; i++)
            {
                try
                {
                    Type type = _bridge.FindType(StaticFieldNames[i][0]);
                    if (type == null)
                        continue;
                    FieldInfo field = type.GetField(StaticFieldNames[i][1],
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null || field.IsLiteral || field.IsInitOnly)
                        continue;
                    FieldPlan plan = ClassifyField(field, true);
                    if (plan != null)
                        _staticPlans.Add(plan);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Savestate static field " + StaticFieldNames[i][0] + "." +
                                       StaticFieldNames[i][1] + " unavailable: " + ex.Message);
                }
            }
        }

        void CaptureStaticFields(Savestate state)
        {
            DeepContext context = null;
            for (int i = 0; i < _staticPlans.Count; i++)
            {
                FieldPlan plan = _staticPlans[i];
                try
                {
                    FieldState field = CaptureField(plan, null, plan.Field.GetValue(null), ref context);
                    if (field != null)
                        state.StaticFields.Add(field);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("static " + plan.Field.DeclaringType.Name + "." + plan.Field.Name + ": " + ex.Message);
                }
            }
        }

        void RestoreStaticFields(Savestate state)
        {
            if (state.StaticFields == null)
                return;
            RestoredNodes.Clear();
            for (int i = 0; i < state.StaticFields.Count; i++)
            {
                FieldState field = state.StaticFields[i];
                try { ApplyField(null, field); }
                catch (Exception ex) { _plugin.LogWarning("Static field " + field.Name + " restore skipped: " + ex.Message); }
            }
            RestoredNodes.Clear();
        }

        // ------------------------------------------------------------------
        // Loose gameplay scripts (fire zones, burning voxels)
        // ------------------------------------------------------------------

        /// <summary>
        /// Scripts that live outside every other category but hold damage timing: a FireZone's
        /// and a BurningVoxel's HurtCooldownCollection decides when the fire hurts next.
        /// </summary>
        static readonly string[] ExtraComponentTypeNames = { "FireZone", "BurningVoxel" };
        readonly List<Type> _extraTypes = new List<Type>(2);

        void CaptureExtraComponents(Savestate state)
        {
            Transform playerTransform = _bridge.PlayerTransform;
            HashSet<int> seen = new HashSet<int>();
            for (int t = 0; t < _extraTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_extraTypes[t], true); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || !component.gameObject.scene.IsValid() || !seen.Add(component.GetInstanceID()))
                        continue;
                    // On the player rig or an enemy it is captured with them already.
                    if (playerTransform != null && component.transform.IsChildOf(playerTransform))
                        continue;
                    if (IsUnderEnemy(component.transform))
                        continue;
                    try
                    {
                        ComponentSnapshot snapshot = CaptureComponent(component, RefCap);
                        if (snapshot != null)
                            state.ExtraComponents.Add(snapshot);
                    }
                    catch (Exception ex)
                    {
                        state.Warnings.Add(component.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        bool IsUnderEnemy(Transform transform)
        {
            if (_identifierType == null)
                return false;
            // Walked by hand: GetComponentInParent skips inactive objects.
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.GetComponent(_identifierType) != null)
                    return true;
            }
            return false;
        }

        void RestoreExtraComponents(Savestate state, ref int restored, ref int missing)
        {
            if (state.ExtraComponents == null)
                return;
            for (int i = 0; i < state.ExtraComponents.Count; i++)
            {
                ComponentSnapshot snapshot = state.ExtraComponents[i];
                // Fires burn out on their own; one that is gone is not reported as missing.
                if (snapshot == null || Resolve(snapshot.Component) == null)
                    continue;
                try
                {
                    RestoreComponent(snapshot);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Loose script restore skipped: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // Named field helpers used by door handling
        // ------------------------------------------------------------------

        static FieldInfo NamedField(Type type, string name)
        {
            Dictionary<string, FieldInfo> map;
            if (!NamedFieldCache.TryGetValue(type, out map))
            {
                map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                NamedFieldCache[type] = map;
            }
            FieldInfo field;
            if (map.TryGetValue(name, out field))
                return field;

            Type current = type;
            while (current != null && current != typeof(object))
            {
                field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    break;
                current = current.BaseType;
            }
            map[name] = field;
            return field;
        }

        static bool GetBool(Component component, string name)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(bool))
                return false;
            try { return (bool)field.GetValue(component); }
            catch { return false; }
        }

        static int GetInt(Component component, string name)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(int))
                return 0;
            try { return (int)field.GetValue(component); }
            catch { return 0; }
        }

        static Vector3 GetVector3(Component component, string name)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(Vector3))
                return Vector3.zero;
            try { return (Vector3)field.GetValue(component); }
            catch { return Vector3.zero; }
        }

        static T GetReference<T>(Component component, string name) where T : UnityEngine.Object
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || !typeof(T).IsAssignableFrom(field.FieldType))
                return null;
            try { return field.GetValue(component) as T; }
            catch { return null; }
        }

        static T[] GetReferenceArray<T>(Component component, string name) where T : UnityEngine.Object
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || !typeof(T[]).IsAssignableFrom(field.FieldType))
                return new T[0];
            try { return field.GetValue(component) as T[] ?? new T[0]; }
            catch { return new T[0]; }
        }

        static void SetBool(Component component, string name, bool value)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(bool))
                return;
            try { field.SetValue(component, value); }
            catch { }
        }

        static void SetInt(Component component, string name, int value)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(int))
                return;
            try { field.SetValue(component, value); }
            catch { }
        }

        static void SetVector3(Component component, string name, Vector3 value)
        {
            FieldInfo field = NamedField(component.GetType(), name);
            if (field == null || field.FieldType != typeof(Vector3))
                return;
            try { field.SetValue(component, value); }
            catch { }
        }

        /// <summary>First animator on the enemy root or any child (some enemies animate a rig).</summary>
        static Animator FindAnimator(GameObject root)
        {
            if (root == null)
                return null;
            Animator animator = root.GetComponent<Animator>();
            return animator != null ? animator : root.GetComponentInChildren<Animator>(true);
        }

        /// <summary>
        /// References (target transform, homing target, source weapon) are kept apart
        /// from primitives so a restore never writes a destroyed object into a field.
        /// </summary>
        static List<ObjectFieldState> CaptureReferences(object target, int max)
        {
            if (target == null || max <= 0)
                return null;
            if (max < RefCap)
                max = RefCap;
            // Empty references are only recorded for a running script (see ObjectFieldState.WasNull).
            bool live;
            try { live = IsLiveTarget(target); }
            catch { live = false; }
            List<ObjectFieldState> result = new List<ObjectFieldState>(8);
            Type current = target.GetType();
            while (current != null && current != typeof(MonoBehaviour) && current != typeof(Behaviour) &&
                   current != typeof(Component) && current != typeof(object))
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length && result.Count < max; i++)
                {
                    FieldInfo field = declared[i];
                    // Who an enemy is currently targeting lives in an EnemyTarget (a plain
                    // class, so it never matched the UnityEngine.Object test below).
                    if (field.FieldType.Name == "EnemyTarget")
                    {
                        object plain;
                        try { plain = field.GetValue(target); }
                        catch { continue; }
                        if (plain != null)
                            result.Add(new ObjectFieldState { Field = field, Plain = plain });
                        continue;
                    }
                    if (field.FieldType.IsArray || !typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                        continue;
                    object raw;
                    try { raw = field.GetValue(target); }
                    catch { continue; }
                    // Truly empty (not a destroyed object, which is still left alone as before):
                    // a hook that had caught nothing, no held item, no target.
                    if (ReferenceEquals(raw, null))
                    {
                        if (live && !field.IsInitOnly)
                            result.Add(new ObjectFieldState { Field = field, WasNull = true });
                        continue;
                    }
                    UnityEngine.Object value = raw as UnityEngine.Object;
                    if (value == null)
                        continue;
                    result.Add(new ObjectFieldState { Field = field, Value = value });
                }
                current = current.BaseType;
            }
            if (result.Count >= max)
                NoteCap("object references per script", max);
            return result;
        }

        static void ApplyReferences(object target, List<ObjectFieldState> references)
        {
            if (target == null || references == null)
                return;
            for (int i = 0; i < references.Count; i++)
            {
                try
                {
                    if (references[i].WasNull)
                    {
                        if (!ReferenceEquals(references[i].Field.GetValue(target), null))
                            references[i].Field.SetValue(target, null);
                        continue;
                    }
                    if (references[i].Plain != null)
                    {
                        references[i].Field.SetValue(target, references[i].Plain);
                        continue;
                    }
                    // Skip references whose target died since the save; leaving the
                    // field alone is safer than assigning a fake-null object.
                    if (references[i].Value != null)
                        references[i].Field.SetValue(target, references[i].Value);
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // Generic helpers
        // ------------------------------------------------------------------

        /// <summary>A captured component is only usable while its GameObject is alive.</summary>
        static Component Resolve(Component reference)
        {
            return reference != null && GameBridge.IsAlive(reference) ? reference : null;
        }

        object ReadMember(object target, string[] names)
        {
            if (target == null)
                return null;
            for (int i = 0; i < names.Length; i++)
            {
                MemberInfo member = _bridge.FindMember(target.GetType(), names[i]);
                if (member == null)
                    continue;
                object value = _bridge.GetMemberValue(target, member);
                if (value != null)
                    return value;
            }
            return null;
        }

        float ReadFloat(object target, string[] names, float fallback)
        {
            object value = ReadMember(target, names);
            return value == null ? fallback : GameBridge.ToFloat(value, fallback);
        }

        int ReadInt(object target, string[] names, int fallback)
        {
            object value = ReadMember(target, names);
            return value == null ? fallback : GameBridge.ToInt(value, fallback);
        }

        bool ReadBool(object target, string[] names, bool fallback)
        {
            object value = ReadMember(target, names);
            return value == null ? fallback : GameBridge.ToBool(value, fallback);
        }

        bool SetMember(object target, string[] names, object value)
        {
            if (target == null)
                return false;
            for (int i = 0; i < names.Length; i++)
            {
                MemberInfo member = _bridge.FindMember(target.GetType(), names[i]);
                if (member != null)
                    return _bridge.SetMemberValue(target, member, value);
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Baking (BakeManager): the savestate's own type lists and apply helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// The projectile/coin component types a savestate captures, in a fixed order. A .ukbake
        /// stores the index into this list as an entry's prefab type, so the order must not change.
        /// </summary>
        internal static string[] BakeProjectileTypeNames { get { return ProjectileTypeNames; } }

        /// <summary>
        /// Puts a NavMeshAgent on a baked position / destination / velocity with the same steps the
        /// savestate loader uses (warp back onto the mesh, synchronous path, then velocity). The
        /// movement tuning (speed, acceleration, ...) is taken from the live agent, so only the
        /// baked values change. Allocation-free: the path object is pooled, and the tuning values
        /// RestoreAgent would write back are the agent's own, so they are left alone. Callers only
        /// use it when the agent diverged (destination changed, or it cannot be moved back directly).
        /// </summary>
        internal static void BakeRestoreAgent(NavMeshAgent agent, Vector3 position, Vector3 destination,
                                              Vector3 velocity)
        {
            if (agent == null || !agent.isActiveAndEnabled)
                return;
            try
            {
                bool isStopped = agent.isOnNavMesh && agent.isStopped;
                // Same order as RestoreAgent: back onto the mesh, warp, synchronous path, velocity.
                if (!agent.isOnNavMesh)
                {
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(position, out hit, 3f, NavMesh.AllAreas))
                        return;
                    agent.Warp(hit.position);
                    if (!agent.isOnNavMesh)
                        return;
                }
                agent.Warp(position);
                if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
                    return;
                NavMeshPath path = s_bakePath ?? (s_bakePath = new NavMeshPath());
                if (agent.CalculatePath(destination, path))
                    agent.SetPath(path);
                else
                    agent.destination = destination;
                agent.velocity = velocity;
                agent.isStopped = isStopped;
            }
            catch
            {
            }
        }

        // One path reused by every baked re-path (SetPath copies the corners into the agent).
        static NavMeshPath s_bakePath;
    }
    /// <summary>
    /// Attached to a projectile rebuilt from a template. Unity runs Start() on the frame after
    /// instantiation, and the game's Start() methods reset saved values; this re-applies the
    /// saved state once Start has run, before the first simulation step, then removes itself.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class RespawnFinalizer : MonoBehaviour
    {
        internal Action Reapply;
        bool _done;

        void Start()
        {
            // Start order among components is not guaranteed, so this pass may run before the
            // game's Start; the first FixedUpdate/Update pass below covers that case.
            Run();
        }

        void FixedUpdate()
        {
            Finish();
        }

        void Update()
        {
            Finish();
        }

        void Finish()
        {
            if (_done)
                return;
            _done = true;
            Run();
            Destroy(this);
        }

        void Run()
        {
            if (Reapply == null)
                return;
            try { Reapply(); }
            catch { }
        }
    }
}
