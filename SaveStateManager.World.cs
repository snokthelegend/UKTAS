using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace UltrakillTAS
{
    /// <summary>
    /// Everything beyond the four core categories: the whole player rig (weapons, fists,
    /// camera, ground checks, whiplash hook), particles and audio, map elements and the
    /// objects their triggers switch, door leaves, and enemy orphan cleanup.
    ///
    /// Field names were taken from the game's own assembly rather than guessed: the
    /// per-weapon state (Revolver charge, Shotgun heat, Nailgun heat/ammo, Railcannon
    /// charge, RocketLauncher timer) lives in MonoBehaviours under GunControl, and the
    /// dash / slide / slam state in NewMovement. All of it is read generically, so a game
    /// update that adds a field is captured without touching this code.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        static readonly string[] LevelTypeNames =
        {
            "ObjectActivator", "ActivateArena", "ActivateNextWave", "ActivateNextWaveHP",
            "ArenaStatus", "Arena", "CheckPoint", "Elevator", "FinalDoor", "FinalDoorOpener",
            "BigDoorOpener", "DoorOpener", "DoorController", "DoorLock", "DoorUnlocker",
            "Breakable", "Glass", "Torch", "MovingPlatform", "LinkPlatform", "Piston",
            "Rotator", "RotateOverTime", "LimboSwitch", "LimboSwitchLock", "ItemTrigger",
            "StatueActivator", "PlayerActivator", "ScriptActivator", "ObjectSpawner",
            "BossHealthBar", "HookPoint", "FinalPit", "SecretMissionPit", "ItemIdentifier",
            "RoomManager", "RoomArea", "FakeRoomActivator", "NextRoomInfo",
            // One-time and progress flags confirmed in Assembly-CSharp: activated / oneTime /
            // acceptedItemPlaced / currentEvent / m_RefCount / currentStatus / timer.
            "ObjectActivatorStay", "ItemPlaceZone", "CoinActivated", "EventsSequence",
            "ObjectActivateInSequence",
            "CheckpointsDisabler", "EventOnRandomTimer", "WeaponPickUp", "ClashModePickup",
            "LevelNameActivator", "DirectionalDotActivator", "RefCountedEvent", "BreakBreakables",
            "TimeBomb"
        };

        // Safety limits only, far above any stock level: reaching one is logged once (NoteCap)
        // instead of silently leaving the rest out. Capture time is watched by Capture() and
        // reported once above 60 ms; the rolling history drops to light capture when slow.
        const int MaxLevelElements = 100000;
        const int MaxLevelTypes = 4096;
        const int MaxElementAnimators = 1024;
        /// <summary>Animators on trigger targets.</summary>
        const int MaxLevelAnimators = 100000;
        const int MaxAnimatorsPerTarget = 1024;
        /// <summary>Targets whose subtrees are searched; a target can be a whole room root.</summary>
        const int MaxAnimatorTargetScan = 100000;
        const int MaxLevelTargets = 100000;
        const int MaxRigScripts = 8192;
        const int MaxRigActive = 100000;
        const int MaxRigParticles = 8192;
        const int MaxRigAudio = 4096;
        const int MaxElementColliders = 4096;
        const int MaxPlayerColliders = 4096;
        /// <summary>Re-simulating a particle system costs time proportional to its age.</summary>
        const float MaxParticleResimulate = 6f;

        Assembly _gameAssembly;
        readonly List<Type> _levelTypes = new List<Type>(48);
        readonly List<Type> _transientTypes = new List<Type>(12);

        /// <summary>
        /// Level elements found by the last typed scan, keyed by the scene's handle. These
        /// scripts are placed in the level, not spawned, so the 39 typed scans run once per
        /// scene load instead of on every save.
        /// </summary>
        readonly List<Component> _levelCache = new List<Component>(256);
        int _levelCacheScene = -1;
        Type _fistControlType;

        static readonly Dictionary<Type, FieldInfo[]> TargetFieldCache = new Dictionary<Type, FieldInfo[]>();

        void ResolveWorldTypes()
        {
            _gameAssembly = _identifierType != null ? _identifierType.Assembly : null;
            _fistControlType = _bridge.FindType("FistControl");
            _levelTypes.Clear();
            for (int i = 0; i < LevelTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(LevelTypeNames[i]);
                // Only scene scripts: a name can resolve to something else (LinkPlatform is an
                // enum in Assembly-CSharp). FindObjectsOfType returns null for such a type, and
                // that null used to abort the whole map capture with a NullReferenceException,
                // so no FinalDoor, opener, activator or checkpoint was ever saved.
                if (type != null && typeof(Component).IsAssignableFrom(type) && !_levelTypes.Contains(type))
                    _levelTypes.Add(type);
            }
            _transientTypes.Clear();
            for (int i = 0; i < TransientTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(TransientTypeNames[i]);
                if (type != null && typeof(Component).IsAssignableFrom(type) && !_transientTypes.Contains(type))
                    _transientTypes.Add(type);
            }
            AddDiscoveredLevelTypes();
            ResolveRebuildTypes();
            ResolveSequenceTypes();
        }

        /// <summary>
        /// Every other game component whose type name contains Trigger, Event or Cutscene (the
        /// rule the trigger overlay uses, plus cutscenes). The curated list above is verified
        /// member by member; this catches the level scripts it does not name, which is where
        /// one-off map events and cutscene direction live. Bounded, deduplicated, and skipped
        /// entirely while the game assembly is still loading.
        /// </summary>
        void AddDiscoveredLevelTypes()
        {
            Type anchor = _identifierType ?? _bridge.FindType("ObjectActivator");
            if (anchor == null)
                return;
            Type[] types;
            try
            {
                types = anchor.Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Savestate map types could not be listed: " + ex.Message);
                return;
            }
            for (int i = 0; i < types.Length && _levelTypes.Count < MaxLevelTypes; i++)
            {
                if (_levelTypes.Count + 1 >= MaxLevelTypes)
                    NoteCap("map script types", MaxLevelTypes);
                Type type = types[i];
                if (type == null || type.IsAbstract || type.ContainsGenericParameters ||
                    !typeof(Component).IsAssignableFrom(type))
                    continue;
                if (type.Name.IndexOf("Trigger", StringComparison.Ordinal) < 0 &&
                    type.Name.IndexOf("Event", StringComparison.Ordinal) < 0 &&
                    type.Name.IndexOf("Cutscene", StringComparison.Ordinal) < 0)
                    continue;
                if (!_levelTypes.Contains(type))
                    _levelTypes.Add(type);
            }
        }

        /// <summary>
        /// Hazards an enemy spawns that can still hurt the player after a load, but whose
        /// state is not worth restoring: they last a second or two. A load destroys every
        /// instance that did not exist at the save. Nothing else spawned after the save
        /// (gore, debris, particles) is tracked; it is cosmetic.
        /// CrateCoin is the pickup a broken crate drops: one dropped after the save would
        /// otherwise stay next to the rebuilt crate and be collected twice.
        /// </summary>
        static readonly string[] TransientTypeNames =
        {
            "PhysicalShockwave", "VirtueInsignia", "ContinuousBeam", "LightningStrikeExplosive",
            "ProjectileSpread", "Landmine", "ThrownSword", "BlackHoleProjectile", "Chainsaw",
            "CrateCoin"
        };

        /// <summary>True for ULTRAKILL's own scripts; Unity/UI/TMP components are skipped.</summary>
        bool IsGameScript(Type type)
        {
            return _gameAssembly == null || type.Assembly == _gameAssembly;
        }

        // ------------------------------------------------------------------
        // Component snapshots
        // ------------------------------------------------------------------

        static ComponentSnapshot CaptureComponent(Component component, int referenceMax)
        {
            if (component == null)
                return null;
            ComponentSnapshot snapshot = new ComponentSnapshot
            {
                Component = component,
                TypeName = component.GetType().Name
            };
            Behaviour behaviour = component as Behaviour;
            if (behaviour != null)
            {
                snapshot.HasEnabled = true;
                snapshot.Enabled = behaviour.enabled;
            }
            snapshot.Fields = CaptureFields(component, FieldCap);
            snapshot.References = referenceMax > 0 ? CaptureReferences(component, referenceMax) : null;
            CaptureInvokes(component, snapshot);
            CaptureEmptyEffects(component, snapshot);
            snapshot.ListRefs = CaptureListRefs(component);
            return snapshot;
        }

        static void RestoreComponent(ComponentSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            Component component = Resolve(snapshot.Component);
            if (component == null)
                return;

            // Enabled flag first (OnEnable may reset fields), then the saved values win.
            Behaviour behaviour = component as Behaviour;
            if (behaviour != null && snapshot.HasEnabled && behaviour.enabled != snapshot.Enabled)
            {
                try { behaviour.enabled = snapshot.Enabled; }
                catch { }
            }
            ApplyFields(component, snapshot.Fields);
            ApplyReferences(component, snapshot.References);
            RestoreListRefs(component, snapshot.ListRefs);
            // Delayed calls last: they must be re-armed against the restored fields.
            RestoreInvokes(component, snapshot);
            ClearLateEffects(component, snapshot);
        }

        static void RestoreActiveFlags(List<ActiveFlag> flags, Dictionary<int, AnimatorState> animators = null)
        {
            if (flags == null)
                return;
            for (int i = 0; i < flags.Count; i++)
            {
                GameObject target = flags[i].Object;
                if (target == null || target.activeSelf == flags[i].Active)
                    continue;
                // A cutscene switched the object on and started its clips. Put the saved clip
                // back to its saved time while the object is still active: an Animator that is
                // switched off cannot be posed, and enabling it again would resume the cutscene
                // frame it was left on instead of starting the sequence over.
                if (!flags[i].Active && animators != null)
                    RestartAnimatorsBeforeDisable(target, animators);
                try { target.SetActive(flags[i].Active); }
                catch { }
            }
        }

        static void RestartAnimatorsBeforeDisable(GameObject root, Dictionary<int, AnimatorState> animators)
        {
            if (root == null || animators == null || animators.Count == 0)
                return;
            Animator[] found = root.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < found.Length; i++)
            {
                Animator animator = found[i];
                if (animator == null || !animator.isActiveAndEnabled)
                    continue;
                AnimatorState snapshot;
                if (animators.TryGetValue(animator.GetInstanceID(), out snapshot))
                    RestoreAnimator(snapshot);
            }
        }

        // ------------------------------------------------------------------
        // Colliders, animators, particles, audio
        // ------------------------------------------------------------------

        static List<ColliderFlag> CaptureColliders(GameObject root, int max)
        {
            if (root == null)
                return new List<ColliderFlag>(0);
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            List<ColliderFlag> result = new List<ColliderFlag>(Mathf.Min(max, colliders.Length));
            for (int i = 0; i < colliders.Length && result.Count < max; i++)
            {
                if (colliders[i] != null)
                    result.Add(new ColliderFlag { Collider = colliders[i], Enabled = colliders[i].enabled });
            }
            if (result.Count >= max && colliders.Length > max)
                NoteCap("colliders per object", max);
            return result;
        }

        static void RestoreColliders(List<ColliderFlag> flags)
        {
            if (flags == null)
                return;
            for (int i = 0; i < flags.Count; i++)
            {
                Collider collider = flags[i].Collider;
                if (collider == null || collider.enabled == flags[i].Enabled)
                    continue;
                try { collider.enabled = flags[i].Enabled; }
                catch { }
            }
        }

        static List<AnimatorState> CaptureAnimatorsExcept(GameObject root, int max, Animator skip)
        {
            if (root == null)
                return new List<AnimatorState>(0);
            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            List<AnimatorState> result = new List<AnimatorState>(Mathf.Min(max, animators.Length));
            for (int i = 0; i < animators.Length && result.Count < max; i++)
            {
                if (animators[i] == null || ReferenceEquals(animators[i], skip))
                    continue;
                AnimatorState snapshot = CaptureAnimator(animators[i], true);
                if (snapshot != null)
                    result.Add(snapshot);
            }
            if (result.Count >= max && animators.Length > max)
                NoteCap("animators per enemy", max);
            return result;
        }

        void CaptureParticles(GameObject root, int max, List<ParticleSnapshot> into, bool full)
        {
            if (root == null || into == null)
                return;
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            if (into.Count + systems.Length > max)
                NoteCap("particle systems", max);
            for (int i = 0; i < systems.Length && into.Count < max; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null)
                    continue;
                ParticleSnapshot snapshot = CaptureParticle(system, full);
                if (snapshot != null)
                    into.Add(snapshot);
            }
        }

        /// <summary>
        /// A system's live particles plus the modules that decide what they do next. The
        /// buffer is exact for a slot save; a light rolling-history capture keeps only the
        /// seed/time path because copying tens of thousands of vertices every tick is not
        /// worth it. The buffer budget is per whole state, so many emitters cannot exhaust
        /// memory; once it is spent the older seed/resimulate path is used instead.
        /// </summary>
        ParticleSnapshot CaptureParticle(ParticleSystem system, bool full)
        {
            if (system == null)
                return null;
            ParticleSnapshot snapshot = new ParticleSnapshot
            {
                System = system,
                Playing = system.isPlaying,
                Paused = system.isPaused,
                Time = system.time,
                RandomSeed = system.randomSeed,
                UseAutoRandomSeed = system.useAutoRandomSeed,
                Emitting = system.isEmitting,
                EmissionEnabled = system.emission.enabled,
                SimulationSpeed = system.main.simulationSpeed
            };
            try
            {
                snapshot.Main = system.main;
                snapshot.Emission = system.emission;
                snapshot.Shape = system.shape;
                snapshot.VelocityOverLifetime = system.velocityOverLifetime;
                snapshot.Noise = system.noise;
                snapshot.Collision = system.collision;
                snapshot.Trails = system.trails;
                snapshot.SubEmitters = system.subEmitters;
                snapshot.TextureSheetAnimation = system.textureSheetAnimation;
                snapshot.ColorOverLifetime = system.colorOverLifetime;
                snapshot.SizeOverLifetime = system.sizeOverLifetime;
                snapshot.RotationOverLifetime = system.rotationOverLifetime;
                snapshot.ForceOverLifetime = system.forceOverLifetime;
                snapshot.LimitVelocity = system.limitVelocityOverLifetime;
                snapshot.InheritVelocity = system.inheritVelocity;
                snapshot.HasModules = true;
            }
            catch
            {
            }
            if (!full || _particleBufferBudget <= 0)
                return snapshot;

            int wanted = system.particleCount;
            if (wanted <= 0)
            {
                snapshot.FullState = true;
                return snapshot;
            }
            int count = Math.Min(wanted, MaxParticleBufferPerSystem);
            count = Math.Min(count, _particleBufferBudget);
            if (count <= 0)
                return snapshot;
            ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[count];
            int got = system.GetParticles(buffer);
            if (got < buffer.Length)
                Array.Resize(ref buffer, got);
            snapshot.FullState = true;
            snapshot.Buffer = buffer;
            snapshot.BufferCount = got;
            _particleBufferBudget -= got;
            if (wanted > got)
                NoteCap("live particles per system", MaxParticleBufferPerSystem);
            return snapshot;
        }

        static void RestoreParticles(List<ParticleSnapshot> snapshots)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
            {
                ParticleSnapshot snapshot = snapshots[i];
                ParticleSystem system = snapshot.System;
                if (system == null)
                    continue;
                try
                {
                    // Module configuration first: a stopped system still needs its seed,
                    // bursts, shape and emission setup for the next time the game plays it.
                    RestoreParticleModules(system, snapshot);
                    if (!snapshot.Playing)
                    {
                        if (system.isPlaying || system.particleCount > 0)
                            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        system.useAutoRandomSeed = snapshot.UseAutoRandomSeed;
                        if (!snapshot.UseAutoRandomSeed)
                            system.randomSeed = snapshot.RandomSeed;
                        continue;
                    }
                    // Exact path: put the saved modules and every live particle back. This
                    // preserves age, velocity, colour/size curves, sub-emitter spawn state and
                    // the RNG sequence instead of approximating by re-simulating from a seed.
                    if (snapshot.FullState)
                    {
                        ParticleSystem.MainModule main = system.main;
                        main.simulationSpeed = snapshot.SimulationSpeed;
                        ParticleSystem.EmissionModule emission = system.emission;
                        emission.enabled = snapshot.EmissionEnabled;
                        system.useAutoRandomSeed = snapshot.UseAutoRandomSeed;
                        if (!snapshot.UseAutoRandomSeed)
                            system.randomSeed = snapshot.RandomSeed;

                        system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        system.Play(false);
                        if (snapshot.Buffer != null && snapshot.BufferCount > 0)
                            system.SetParticles(snapshot.Buffer, snapshot.BufferCount);
                        system.time = snapshot.Time;
                        // StopEmitting leaves existing particles alive; it is how a burst that
                        // had finished emitting but still had live particles is represented.
                        if (!snapshot.Emitting)
                            system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                        if (snapshot.Paused)
                            system.Pause(false);
                        continue;
                    }
                    // Particles that were mid-flight are rebuilt by simulating to the saved
                    // age; very old systems are left running instead of paying for a long
                    // re-simulation.
                    if (snapshot.Time > MaxParticleResimulate)
                        continue;
                    system.Simulate(snapshot.Time, false, true);
                    if (!snapshot.Paused)
                        system.Play(false);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Particle module structs are read-only properties, but their individual properties
        /// write through to the native system. Copy every writable public property from the
        /// saved module box into the live module box; unsupported/indexed/native properties
        /// are skipped individually so one bad module cannot lose the whole emitter.
        /// </summary>
        static void RestoreParticleModules(ParticleSystem system, ParticleSnapshot snapshot)
        {
            if (system == null || snapshot == null || !snapshot.HasModules ||
                !ReferenceEquals(system, snapshot.System))
                return;
            try { CopyParticleModule(snapshot.Main, system.main); } catch { }
            try { CopyParticleModule(snapshot.Emission, system.emission); } catch { }
            try { CopyParticleModule(snapshot.Shape, system.shape); } catch { }
            try { CopyParticleModule(snapshot.VelocityOverLifetime, system.velocityOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.Noise, system.noise); } catch { }
            try { CopyParticleModule(snapshot.Collision, system.collision); } catch { }
            try { CopyParticleModule(snapshot.Trails, system.trails); } catch { }
            try { CopyParticleModule(snapshot.SubEmitters, system.subEmitters); } catch { }
            try { CopyParticleModule(snapshot.TextureSheetAnimation, system.textureSheetAnimation); } catch { }
            try { CopyParticleModule(snapshot.ColorOverLifetime, system.colorOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.SizeOverLifetime, system.sizeOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.RotationOverLifetime, system.rotationOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.ForceOverLifetime, system.forceOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.LimitVelocity, system.limitVelocityOverLifetime); } catch { }
            try { CopyParticleModule(snapshot.InheritVelocity, system.inheritVelocity); } catch { }
        }

        static void CopyParticleModule(object saved, object live)
        {
            if (saved == null || live == null)
                return;
            PropertyInfo[] properties = saved.GetType().GetProperties(
                BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    object value = property.GetValue(saved, null);
                    if (value is UnityEngine.Object unity && !unity)
                        continue;
                    property.SetValue(live, value, null);
                }
                catch
                {
                }
            }
        }

        static AudioSnapshot CaptureAudioSource(AudioSource source)
        {
            if (source == null)
                return null;
            try
            {
                return new AudioSnapshot
                {
                    Source = source,
                    Playing = source.isPlaying,
                    Time = source.clip != null ? source.time : 0f,
                    TimeSamples = source.clip != null ? source.timeSamples : 0,
                    Pitch = source.pitch,
                    Volume = source.volume,
                    Mute = source.mute,
                    Loop = source.loop,
                    PlayOnAwake = source.playOnAwake,
                    Priority = source.priority,
                    PanStereo = source.panStereo,
                    SpatialBlend = source.spatialBlend,
                    ReverbZoneMix = source.reverbZoneMix,
                    DopplerLevel = source.dopplerLevel,
                    Spread = source.spread,
                    RolloffMode = source.rolloffMode,
                    MinDistance = source.minDistance,
                    MaxDistance = source.maxDistance,
                    IgnoreListenerPause = source.ignoreListenerPause,
                    IgnoreListenerVolume = source.ignoreListenerVolume,
                    VelocityUpdateMode = source.velocityUpdateMode,
                    OutputAudioMixerGroup = source.outputAudioMixerGroup,
                    HasOutputAudioMixerGroup = source.outputAudioMixerGroup != null
                };
            }
            catch
            {
                return null;
            }
        }

        static void CaptureAudio(GameObject root, int max, List<AudioSnapshot> into)
        {
            if (root == null || into == null)
                return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            if (into.Count + sources.Length > max)
                NoteCap("audio sources", max);
            for (int i = 0; i < sources.Length && into.Count < max; i++)
            {
                AudioSource source = sources[i];
                if (source == null)
                    continue;
                into.Add(new AudioSnapshot
                {
                    Source = source,
                    Playing = source.isPlaying,
                    Time = source.clip != null ? source.time : 0f,
                    TimeSamples = source.clip != null ? source.timeSamples : 0,
                    Pitch = source.pitch,
                    Volume = source.volume,
                    Mute = source.mute,
                    Loop = source.loop,
                    PlayOnAwake = source.playOnAwake,
                    Priority = source.priority,
                    PanStereo = source.panStereo,
                    SpatialBlend = source.spatialBlend,
                    ReverbZoneMix = source.reverbZoneMix,
                    DopplerLevel = source.dopplerLevel,
                    Spread = source.spread,
                    RolloffMode = source.rolloffMode,
                    MinDistance = source.minDistance,
                    MaxDistance = source.maxDistance,
                    IgnoreListenerPause = source.ignoreListenerPause,
                    IgnoreListenerVolume = source.ignoreListenerVolume,
                    VelocityUpdateMode = source.velocityUpdateMode,
                    OutputAudioMixerGroup = source.outputAudioMixerGroup,
                    HasOutputAudioMixerGroup = source.outputAudioMixerGroup != null
                });
            }
        }

        static void RestoreAudio(List<AudioSnapshot> snapshots)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
            {
                AudioSnapshot snapshot = snapshots[i];
                AudioSource source = snapshot.Source;
                if (source == null || !source.gameObject.activeInHierarchy)
                    continue;
                try
                {
                    source.pitch = snapshot.Pitch;
                    source.volume = snapshot.Volume;
                    source.mute = snapshot.Mute;
                    source.loop = snapshot.Loop;
                    source.playOnAwake = snapshot.PlayOnAwake;
                    source.priority = snapshot.Priority;
                    source.panStereo = snapshot.PanStereo;
                    source.spatialBlend = snapshot.SpatialBlend;
                    source.reverbZoneMix = snapshot.ReverbZoneMix;
                    source.dopplerLevel = snapshot.DopplerLevel;
                    source.spread = snapshot.Spread;
                    source.rolloffMode = snapshot.RolloffMode;
                    source.minDistance = snapshot.MinDistance;
                    source.maxDistance = snapshot.MaxDistance;
                    source.ignoreListenerPause = snapshot.IgnoreListenerPause;
                    source.ignoreListenerVolume = snapshot.IgnoreListenerVolume;
                    source.velocityUpdateMode = snapshot.VelocityUpdateMode;
                    if (snapshot.HasOutputAudioMixerGroup && snapshot.OutputAudioMixerGroup != null)
                        source.outputAudioMixerGroup = snapshot.OutputAudioMixerGroup;
                    if (snapshot.Playing)
                    {
                        if (source.clip == null)
                            continue;
                        if (!source.isPlaying)
                            source.Play();
                        source.timeSamples = Mathf.Clamp(snapshot.TimeSamples, 0, Mathf.Max(0, source.clip.samples - 1));
                    }
                    else if (source.isPlaying)
                    {
                        source.Stop();
                    }
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // The player rig
        // ------------------------------------------------------------------

        static void AddRoot(List<GameObject> roots, Component component)
        {
            if (component == null)
                return;
            GameObject root = component.gameObject;
            if (root != null && !roots.Contains(root))
                roots.Add(root);
        }

        /// <summary>
        /// Weapons (Revolver, Shotgun, Nailgun, Railcannon, RocketLauncher), fists (Punch,
        /// FistControl), WeaponCharges, camera, ground/wall checks and the whiplash arm all
        /// hang off the player hierarchy. Every ULTRAKILL script found there is captured,
        /// together with the active flag of every weapon object so the equipped weapon,
        /// muzzle flashes and charge effects come back exactly as they were.
        /// </summary>
        void CapturePlayerRig(Savestate state)
        {
            Component movement = _bridge.NewMovement as Component;
            if (movement == null)
                return;

            HashSet<int> seenScripts = new HashSet<int>();
            seenScripts.Add(movement.GetInstanceID());

            List<GameObject> roots = new List<GameObject>(4);
            AddRoot(roots, movement);
            AddRoot(roots, _bridge.CameraController as Component);
            Component gun = _bridge.GunControl as Component;
            AddRoot(roots, gun);
            Component fists = null;
            if (_fistControlType != null)
            {
                try { fists = UnityEngine.Object.FindObjectOfType(_fistControlType) as Component; }
                catch { }
            }
            AddRoot(roots, fists);

            HashSet<int> seenObjects = new HashSet<int>();
            for (int r = 0; r < roots.Count; r++)
            {
                GameObject root = roots[r];
                MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < scripts.Length && state.PlayerParts.Count < MaxRigScripts; i++)
                {
                    MonoBehaviour script = scripts[i];
                    if (script == null || !IsGameScript(script.GetType()) || !seenScripts.Add(script.GetInstanceID()))
                        continue;
                    ComponentSnapshot snapshot = CaptureComponent(script, RefCap);
                    if (snapshot != null)
                        state.PlayerParts.Add(snapshot);
                }
            }
            if (state.PlayerParts.Count >= MaxRigScripts)
                NoteCap("player rig scripts", MaxRigScripts);

            // Active flags: weapon and fist models switch whole objects on and off.
            List<GameObject> activeRoots = new List<GameObject>(2);
            AddRoot(activeRoots, gun);
            AddRoot(activeRoots, fists);
            for (int r = 0; r < activeRoots.Count; r++)
            {
                Transform[] transforms = activeRoots[r].GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length && state.PlayerActive.Count < MaxRigActive; i++)
                {
                    GameObject go = transforms[i].gameObject;
                    if (seenObjects.Add(go.GetInstanceID()))
                        state.PlayerActive.Add(new ActiveFlag { Object = go, Active = go.activeSelf });
                }
            }
            if (state.PlayerActive.Count >= MaxRigActive)
                NoteCap("player rig objects", MaxRigActive);

            CaptureRigPoses(state, _bridge.CameraController as Component, gun, fists);
            state.PlayerColliders = CaptureColliders(movement.gameObject, MaxPlayerColliders);
            for (int r = 0; r < roots.Count; r++)
            {
                CaptureParticles(roots[r], MaxRigParticles, state.PlayerParticles, !_lightCapture);
                CaptureAudio(roots[r], MaxRigAudio, state.PlayerAudio);
            }
        }

        /// <summary>Active flags first (OnEnable runs), then fields, then animation/particles/audio.</summary>
        void RestorePlayerRig(Savestate state)
        {
            // The fall/slam trail is a clone the game instantiates while the player falls fast
            // and keeps in NewMovement.currentFallParticle. The field restore below writes the
            // saved reference (usually null), which would orphan a live clone: it then stays on
            // screen. Note the live clone now and destroy it afterwards if it is not the saved one.
            GameObject liveFall = FallParticleObject();
            RestoreActiveFlags(state.PlayerActive);
            RestoreRigPoses(state);
            for (int i = 0; i < state.PlayerParts.Count; i++)
                RestoreComponent(state.PlayerParts[i]);
            RestoreColliders(state.PlayerColliders);
            GameObject keptFall = FallParticleObject();
            if (liveFall != null && liveFall != keptFall)
            {
                try { UnityEngine.Object.Destroy(liveFall); }
                catch { }
            }
        }

        /// <summary>NewMovement.currentFallParticle (the instantiated slam/fall trail), or null.</summary>
        GameObject FallParticleObject()
        {
            object movement = _bridge.NewMovement;
            if (movement == null)
                return null;
            object value = _bridge.GetMember(movement, new[] { "currentFallParticle" });
            GameObject go = value as GameObject;
            if (go != null)
                return go;
            Component component = value as Component;
            return component != null ? component.gameObject : null;
        }

        /// <summary>
        /// Particle systems and trail renderers under the player that did not exist at the
        /// save (the slam/fall trail among them) are stopped and cleared, so a load never
        /// leaves streaks from the discarded timeline on screen.
        /// </summary>
        void ClearStrayPlayerEffects(Savestate state)
        {
            Component movement = _bridge.NewMovement as Component;
            if (movement == null)
                return;
            HashSet<int> known = new HashSet<int>();
            for (int i = 0; i < state.PlayerParticles.Count; i++)
            {
                ParticleSystem saved = state.PlayerParticles[i].System;
                if (saved != null)
                    known.Add(saved.GetInstanceID());
            }
            ParticleSystem[] systems = movement.gameObject.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem system = systems[i];
                if (system == null || known.Contains(system.GetInstanceID()))
                    continue;
                try
                {
                    if (system.isPlaying || system.particleCount > 0)
                        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                catch { }
            }
            TrailRenderer[] trails = movement.gameObject.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] == null)
                    continue;
                try { trails[i].Clear(); }
                catch { }
            }
        }

        void RestorePlayerEffects(Savestate state)
        {
            RestoreParticles(state.PlayerParticles);
            RestoreAudio(state.PlayerAudio);
            ClearStrayPlayerEffects(state);
        }

        // ------------------------------------------------------------------
        // Map elements
        // ------------------------------------------------------------------

        static FieldInfo[] GetTargetFields(Type type)
        {
            FieldInfo[] cached;
            if (TargetFieldCache.TryGetValue(type, out cached))
                return cached;

            List<FieldInfo> fields = new List<FieldInfo>(8);
            Type current = type;
            while (current != null && current != typeof(MonoBehaviour) && current != typeof(object))
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    Type fieldType = declared[i].FieldType;
                    if (fieldType == typeof(GameObject) || fieldType == typeof(GameObject[]) ||
                        typeof(UnityEventBase).IsAssignableFrom(fieldType) || fieldType.Name == "UltrakillEvent")
                        fields.Add(declared[i]);
                }
                current = current.BaseType;
            }
            cached = fields.ToArray();
            TargetFieldCache[type] = cached;
            return cached;
        }

        static void AddTarget(GameObject go, HashSet<GameObject> into)
        {
            // Prefab assets are not part of the scene and never change active state.
            if (go == null || !go.scene.IsValid())
                return;
            if (into.Count < MaxLevelTargets)
                into.Add(go);
            else
                NoteCap("objects switched by triggers", MaxLevelTargets);
        }

        /// <summary>
        /// The objects a trigger switches on or off: direct GameObject fields, GameObject
        /// arrays, UnityEvent persistent targets and the arrays inside UltrakillEvent. Their
        /// active flags are stored so a load undoes "the arena walls came up".
        /// </summary>
        static void CollectTargetObjects(object owner, HashSet<GameObject> into, int depth)
        {
            if (owner == null || depth > 2 || into.Count >= MaxLevelTargets)
                return;
            FieldInfo[] fields = GetTargetFields(owner.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try { value = fields[i].GetValue(owner); }
                catch { continue; }
                if (value == null)
                    continue;

                GameObject single = value as GameObject;
                if (single != null)
                {
                    AddTarget(single, into);
                    continue;
                }

                GameObject[] array = value as GameObject[];
                if (array != null)
                {
                    for (int a = 0; a < array.Length; a++)
                        AddTarget(array[a], into);
                    continue;
                }

                UnityEventBase unityEvent = value as UnityEventBase;
                if (unityEvent != null)
                {
                    int count = unityEvent.GetPersistentEventCount();
                    for (int e = 0; e < count; e++)
                    {
                        UnityEngine.Object target = unityEvent.GetPersistentTarget(e);
                        GameObject go = target as GameObject;
                        if (go == null)
                        {
                            Component component = target as Component;
                            go = component != null ? component.gameObject : null;
                        }
                        AddTarget(go, into);
                    }
                    continue;
                }

                if (fields[i].FieldType.Name == "UltrakillEvent")
                    CollectTargetObjects(value, into, depth + 1);
            }
        }

        void CaptureLevelElements(Savestate state)
        {
            HashSet<GameObject> targets = new HashSet<GameObject>();
            HashSet<int> seen = new HashSet<int>();

            EnsureRebuildScene();
            List<Component> found = LevelElementComponents();
            HashSet<int> animatorIds = new HashSet<int>();
            for (int i = 0; i < found.Count && state.LevelElements.Count < MaxLevelElements; i++)
            {
                Component component = found[i];
                if (component == null || !seen.Add(component.GetInstanceID()))
                    continue;
                try
                {
                    GameObject root = component.gameObject;
                    Transform transform = root.transform;
                    LevelElementState element = new LevelElementState
                    {
                        Component = CaptureComponent(component, RefCap),
                        Root = root,
                        RootId = root.GetInstanceID(),
                        Active = root.activeSelf,
                        Parent = transform.parent,
                        Position = transform.localPosition,
                        Rotation = transform.localRotation,
                        Scale = transform.localScale,
                        Animators = CaptureAnimators(root, MaxElementAnimators, true),
                        Colliders = CaptureColliders(root, MaxElementColliders)
                    };
                    for (int a = 0; a < element.Animators.Count; a++)
                    {
                        Animator animator = element.Animators[a].Animator;
                        if (animator != null)
                            animatorIds.Add(animator.GetInstanceID());
                    }
                    Rigidbody body = root.GetComponent<Rigidbody>();
                    if (body != null)
                        element.Body = CaptureBody(body);
                    CollectTargetObjects(component, targets, 0);
                    state.LevelElements.Add(element);
                    // Breakable / Glass: keep an intact copy so a load can undo the break.
                    TrackRebuildable(component);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("level element " + component.GetType().Name + ": " + ex.Message);
                }
            }

            try { CaptureRebuilds(state, targets); }
            catch (Exception ex) { state.Warnings.Add("breakables: " + ex.Message); }

            // Rooms a door streams on or off are part of the loaded layout too.
            CollectDoorRooms(targets);

            foreach (GameObject target in targets)
                state.LevelActive.Add(new ActiveFlag { Object = target, Active = target.activeSelf });

            // Cutscene motion lives on the objects a trigger switches, not on the trigger.
            CaptureTargetAnimators(state, targets, animatorIds);

            // The drivers themselves, so a load can remove the clones a sequence spawned
            // after this moment and rewind the ones that had not started yet.
            try { CaptureSequenceObjects(state); }
            catch (Exception ex) { state.Warnings.Add("cutscene objects: " + ex.Message); }

            if (state.LevelElements.Count >= MaxLevelElements)
            {
                string message = "level elements: the cap of " + MaxLevelElements + " was reached; the rest of this level's map scripts were not saved.";
                state.Warnings.Add(message);
                _plugin.LogWarning("Savestate " + message);
            }
        }

        /// <summary>
        /// Animators of the objects triggers and UnityEvents switch on (the prop that starts
        /// moving, the actor that walks out of the glass). Level elements carry their own
        /// animators already; this adds the targets they activate. Bounded per object and per
        /// save so a room full of animated props cannot make a savestate expensive.
        /// </summary>
        void CaptureTargetAnimators(Savestate state, HashSet<GameObject> targets, HashSet<int> already)
        {
            if (targets == null || already == null)
                return;
            int scanned = 0;
            foreach (GameObject target in targets)
            {
                if (target == null)
                    continue;
                if (state.LevelAnimators.Count >= MaxLevelAnimators)
                {
                    NoteCap("map animators", MaxLevelAnimators);
                    break;
                }
                if (++scanned > MaxAnimatorTargetScan)
                {
                    NoteCap("trigger targets searched for animators", MaxAnimatorTargetScan);
                    break;
                }
                Animator[] animators = target.GetComponentsInChildren<Animator>(true);
                int added = 0;
                for (int i = 0; i < animators.Length && added < MaxAnimatorsPerTarget &&
                                state.LevelAnimators.Count < MaxLevelAnimators; i++)
                {
                    Animator animator = animators[i];
                    if (animator == null || !already.Add(animator.GetInstanceID()))
                        continue;
                    AnimatorState snapshot = CaptureAnimator(animator, true);
                    if (snapshot == null)
                        continue;
                    state.LevelAnimators.Add(snapshot);
                    added++;
                }
            }
        }

        /// <summary>
        /// The arena, activator, checkpoint and door-opener scripts of the loaded level.
        /// A typed scan (never a Transform scan) is needed once: these scripts carry the
        /// level's progress flags (arena started, wave index, checkpoint taken) and nothing
        /// references all of them. The result is reused until the scene is reloaded.
        /// </summary>
        List<Component> LevelElementComponents()
        {
            // Keyed on the loaded scene count too: levels stream parts in additively, and a
            // newly loaded part brings its own arenas and activators.
            int scene = unchecked(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle * 31
                + UnityEngine.SceneManagement.SceneManager.sceneCount);
            if (scene == _levelCacheScene && _levelCache.Count > 0)
            {
                _levelCache.RemoveAll(c => c == null);
                return _levelCache;
            }

            _levelCache.Clear();
            _levelCacheScene = scene;
            Transform playerTransform = _bridge.PlayerTransform;
            for (int t = 0; t < _levelTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_levelTypes[t], true); }
                catch { continue; }
                if (found == null)
                    continue;
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    // includeInactive also returns prefab assets; keep scene objects only.
                    // The inactive intact copies of breakables are not level elements either.
                    if (component == null || !component.gameObject.scene.IsValid() || IsRebuildBackup(component))
                        continue;
                    // The wider discovery can match a script on the player or an enemy; those
                    // objects have their own, fuller capture and must not be posed twice.
                    if (playerTransform != null && component.transform.IsChildOf(playerTransform))
                        continue;
                    if (_identifierType != null && component.GetComponentInParent(_identifierType) != null)
                        continue;
                    _levelCache.Add(component);
                }
            }
            return _levelCache;
        }

        void RestoreLevelElements(Savestate state, ref int restored, ref int missing, bool resetArenas)
        {
            // Re-arming a destroyed arena trigger is only safe on a user load: a rewind runs
            // many times per tick and must not create components or switch enemies off.
            BeginArenaRestore(resetArenas);

            // A cutscene that is running now but was not in the state has to be torn down
            // before anything else: its coroutines would otherwise keep moving the map over
            // the values this load writes.
            try { ResetSequences(state); }
            catch (Exception ex) { _plugin.LogWarning("Cutscene reset skipped: " + ex.Message); }

            // Broken glass and breakables first: a rebuilt copy must exist before the
            // active flags and saved values below are written to it.
            try { RestoreRebuilds(state, ref restored); }
            catch (Exception ex) { _plugin.LogWarning("Breakable restore skipped: " + ex.Message); }
            RestoreActiveFlags(state.LevelActive, CollectAnimatorStates(state));
            for (int i = 0; i < state.LevelElements.Count; i++)
            {
                LevelElementState element = state.LevelElements[i];
                GameObject root = ResolveRebuiltElement(element);
                if (root == null)
                {
                    // A tracked breakable removed on purpose (broken at the save) is not missing.
                    if (!_rebuildById.ContainsKey(element.RootId))
                        missing++;
                    continue;
                }
                try
                {
                    // Rewind the clips while the object is still active; it is switched off
                    // below and an Animator cannot be posed once it is inactive.
                    if (!element.Active && root.activeSelf)
                        RestoreAnimators(element.Animators);
                    if (root.activeSelf != element.Active)
                        root.SetActive(element.Active);
                    // The game destroys ActivateArena/ActivateNextWave as a fight progresses; add
                    // the saved component back before RestoreComponent writes its fields.
                    RebuildArenaScript(element, root);
                    RestoreComponent(element.Component);

                    Transform transform = root.transform;
                    // A held skull or a carried item changed parent since the save.
                    if (transform.parent != element.Parent && (element.Parent != null || transform.parent != null))
                        transform.SetParent(element.Parent, false);
                    transform.localPosition = element.Position;
                    transform.localRotation = element.Rotation;
                    transform.localScale = element.Scale;

                    RestoreBody(element.Body);
                    RestoreColliders(element.Colliders);
                    RestoreAnimators(element.Animators);
                    FinalizeRebuilt(root, element.Component);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Level element restore skipped: " + ex.Message);
                }
            }
            // Last: the objects above may have just been activated, and an animator only
            // takes a pose while it is active.
            RestoreAnimators(state.LevelAnimators);

            // Re-arm the arenas whose trigger script this load had to rebuild: AutoSetupWaves
            // rewires waves/doors, then the counters reset and the wave enemies go inactive.
            // This is after the animator pass so the enemies are posed while they are still
            // active; RestoreEnemies later writes their saved (inactive) state.
            try { FinishArenaRestore(); }
            catch (Exception ex) { _plugin.LogWarning("Arena trigger reset skipped: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Doors: the leaves a Door animates (BigDoor / SubDoor) and its full field set
        // ------------------------------------------------------------------

        static void CaptureDoorParts(DoorState snapshot, Component door, string fieldName)
        {
            Component[] parts = GetReferenceArray<Component>(door, fieldName);
            for (int i = 0; i < parts.Length; i++)
            {
                Component part = parts[i];
                if (part == null)
                    continue;
                snapshot.Parts.Add(new DoorPart
                {
                    Component = part,
                    LocalPosition = part.transform.localPosition,
                    LocalRotation = part.transform.localRotation,
                    Snapshot = CaptureComponent(part, 4)
                });
            }
        }

        static void CaptureDoorExtras(DoorState snapshot, Component door)
        {
            snapshot.Generic = CaptureComponent(door, 12);
            CaptureDoorParts(snapshot, door, "bdoors");
            CaptureDoorParts(snapshot, door, "subdoors");
        }

        static void RestoreDoorExtras(DoorState snapshot)
        {
            RestoreComponent(snapshot.Generic);
            for (int i = 0; i < snapshot.Parts.Count; i++)
            {
                DoorPart part = snapshot.Parts[i];
                Component component = Resolve(part.Component);
                if (component == null)
                    continue;
                component.transform.localPosition = part.LocalPosition;
                component.transform.localRotation = part.LocalRotation;
                RestoreComponent(part.Snapshot);
            }
        }

        // ------------------------------------------------------------------
        // Orphans
        // ------------------------------------------------------------------

        /// <summary>
        /// Records the roots of the transient hazards present at capture so a load can tell
        /// which were spawned afterwards. This replaced a scan of every Transform in the scene:
        /// projectiles and enemies have their own typed orphan cleanup, and nothing else that
        /// spawns after a save affects the run.
        /// </summary>
        void CaptureSceneObjects(Savestate state)
        {
            for (int t = 0; t < _transientTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_transientTypes[t]); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component != null)
                        state.SceneObjects.Add(component.gameObject);
                }
            }
        }

        /// <summary>
        /// Destroys transient hazards (shockwaves, beams, thrown swords) spawned after the
        /// save. The player and this tool's own objects are never touched.
        /// </summary>
        void SweepSpawnedObjects(Savestate state)
        {
            Component player = _bridge.PlayerComponent;
            Transform playerTransform = player != null ? player.transform : null;

            int removed = 0;
            for (int t = 0; t < _transientTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_transientTypes[t]); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null)
                        continue;
                    GameObject go = component.gameObject;
                    if (state.SceneObjects.Contains(go) || state.ProjectileRoots.Contains(go) ||
                        _checkpointFresh.Contains(go))
                        continue;
                    if (go.name.StartsWith("ULTRAKILL_TAS", StringComparison.Ordinal))
                        continue;
                    if (playerTransform != null && go.transform.IsChildOf(playerTransform))
                        continue;
                    UnityEngine.Object.Destroy(go);
                    removed++;
                }
            }

            if (removed > 0)
                _plugin.LogInfo("Savestate load removed " + removed + " hazard(s) spawned after the save.");
        }

        /// <summary>
        /// Enemies that did not exist at capture time (spawned by an arena wave, a spawner or
        /// a cheat) are removed so a load does not keep reinforcements the saved timeline
        /// never had.
        /// </summary>
        void CleanupEnemyOrphans(Savestate state)
        {
            // A state captured before any enemy existed must still sweep the wave the player
            // spawned afterwards, so the guard is "the enemy pass ran", not "it found some".
            if (_identifierType == null || !state.HasEnemySnapshot)
                return;
            Component player = _bridge.PlayerComponent;
            Transform playerTransform = player != null ? player.transform : null;

            int removed = 0;
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(_identifierType, true);
            for (int i = 0; i < found.Length; i++)
            {
                Component identifier = found[i] as Component;
                if (identifier == null)
                    continue;
                GameObject root = identifier.gameObject;
                // The saved set is by instance id, plus the live reference so a recycled id
                // cannot make a different enemy survive the sweep. Anything else spawned after
                // the capture is removed by destroying its root: the game's own OnDestroy runs
                // for every component it carried, including any boss bar it owned.
                if (state.EnemyIds.Contains(root.GetInstanceID()) || state.EnemyRoots.Contains(root) ||
                    IsTemplateClone(root) || _checkpointFresh.Contains(root))
                    continue;
                if (playerTransform != null && root.transform.IsChildOf(playerTransform))
                    continue;
                UnityEngine.Object.Destroy(root);
                removed++;
            }

            if (removed > 0)
                _plugin.LogInfo("Savestate load removed " + removed + " enemy object(s) spawned after the save.");
        }
    }
}
