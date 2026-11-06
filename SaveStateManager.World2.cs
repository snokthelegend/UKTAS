using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// World module for movable physics state that the entity captures do not own.
    ///
    /// Joints (CharacterJoint, HingeJoint, ConfigurableJoint, SpringJoint) drive ragdoll
    /// limbs, chains, gates and breakable props. Their limits, anchors and drives are fields
    /// that a load must put back or the prop flops in a different direction. Dynamic props
    /// and gibs that are not enemies, projectiles or level elements also need their pose and
    /// velocity restored; they are the objects a TAS run nudges with an explosion.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        const int MaxJoints = 4096;
        const int MaxProps = 1024;
        const int MaxParticleSeeds = 2048;
        const int MaxGasolineStains = 4096;
        /// <summary>Below this a prop is at rest; its body is deterministic anyway.</summary>
        const float PropMotionEpsilon = 0.0001f;

        sealed class JointModuleState
        {
            public Joint Joint;
            public string TypeName;
            public List<FieldState> Fields;
            public Rigidbody ConnectedBody;
            public bool HasConnectedBody;
        }

        sealed class PropBodyState
        {
            public Rigidbody Body;
            public Vector3 Position;
            public Quaternion Rotation;
            public BodyState State;
        }

        /// <summary>
        /// Random seed and run state of a playing particle system. Systems older than the
        /// core re-simulation window keep their seed and play state instead of being
        /// simulated back to their saved age, which is what keeps old smoke and blood from
        /// re-rolling its randomness after a load.
        /// </summary>
        sealed class ParticleSeedState
        {
            public ParticleSystem System;
            public uint RandomSeed;
            public bool Playing;
            public bool Paused;
            public float Time;
        }

        /// <summary>
        /// One ground gasoline stain from the Firestarter Rocket Launcher's alternate fire.
        /// The stain persists until it is ignited, so it is gameplay state, not decoration:
        /// it makes the floor slippery and is the fuel a later explosion lights. The runtime
        /// `index` field is deliberately not stored; StainVoxelManager owns that numbering.
        /// </summary>
        sealed class GasolineStainState
        {
            public Component Stain;
            public GameObject Root;
            public string RootName;
            public Transform Parent;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public bool ActiveSelf;
            public bool RendererEnabled;
            public List<FieldState> Fields;
            public GameObject Template;
            public bool HasTemplate;
        }

        sealed class World2ModuleState
        {
            public List<JointModuleState> Joints = new List<JointModuleState>();
            public List<PropBodyState> Props = new List<PropBodyState>();
            public List<ParticleSeedState> Particles = new List<ParticleSeedState>();
            public List<GasolineStainState> Gasoline = new List<GasolineStainState>();
        }

        partial void CaptureWorld2(Savestate state)
        {
            World2ModuleState world = new World2ModuleState();
            UnityEngine.Object[] joints;
            try { joints = UnityEngine.Object.FindObjectsOfType(typeof(Joint), true); }
            catch { joints = new UnityEngine.Object[0]; }
            for (int i = 0; i < joints.Length && world.Joints.Count < MaxJoints; i++)
            {
                Joint joint = joints[i] as Joint;
                if (joint == null || !joint.gameObject.scene.IsValid() || IsTemplateClone(joint.gameObject))
                    continue;
                try
                {
                    JointModuleState snapshot = new JointModuleState
                    {
                        Joint = joint,
                        TypeName = joint.GetType().Name,
                        Fields = CaptureFields(joint, 64)
                    };
                    snapshot.ConnectedBody = joint.connectedBody;
                    snapshot.HasConnectedBody = joint.connectedBody != null;
                    world.Joints.Add(snapshot);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("joint " + joint.GetType().Name + ": " + ex.Message);
                }
            }

            UnityEngine.Object[] bodies;
            try { bodies = UnityEngine.Object.FindObjectsOfType(typeof(Rigidbody)); }
            catch { bodies = new UnityEngine.Object[0]; }
            Rigidbody playerBody = _bridge.PlayerRigidbody;
            Transform player = _bridge.PlayerTransform;
            for (int i = 0; i < bodies.Length && world.Props.Count < MaxProps; i++)
            {
                Rigidbody body = bodies[i] as Rigidbody;
                if (body == null || body.isKinematic || !body.gameObject.scene.IsValid())
                    continue;
                if (ReferenceEquals(body, playerBody) || IsTemplateClone(body.gameObject))
                    continue;
                Transform transform = body.transform;
                if (player != null && transform.IsChildOf(player))
                    continue;
                if (IsUnderEnemy(transform))
                    continue;
                // Resting static geometry is deterministic; only moving props are worth a slot.
                if (body.velocity.sqrMagnitude < PropMotionEpsilon &&
                    body.angularVelocity.sqrMagnitude < PropMotionEpsilon &&
                    body.IsSleeping())
                    continue;
                try
                {
                    world.Props.Add(new PropBodyState
                    {
                        Body = body,
                        Position = transform.position,
                        Rotation = transform.rotation,
                        State = CaptureBody(body)
                    });
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("physics prop " + body.name + ": " + ex.Message);
                }
            }
            if (world.Props.Count >= MaxProps)
                NoteCap("dynamic physics props", MaxProps);

            // Seeds are skipped in a light rolling-history snapshot (the per-tick walk over
            // every emitter is the expensive part); slot saves always carry them.
            if (!_lightCapture)
            {
                UnityEngine.Object[] particles;
                try { particles = UnityEngine.Object.FindObjectsOfType(typeof(ParticleSystem)); }
                catch { particles = new UnityEngine.Object[0]; }
                for (int i = 0; i < particles.Length && world.Particles.Count < MaxParticleSeeds; i++)
                {
                    ParticleSystem system = particles[i] as ParticleSystem;
                    if (system == null || !system.isPlaying || IsTemplateClone(system.gameObject))
                        continue;
                    try
                    {
                        world.Particles.Add(new ParticleSeedState
                        {
                            System = system,
                            RandomSeed = system.randomSeed,
                            Playing = true,
                            Paused = system.isPaused,
                            Time = system.time
                        });
                    }
                    catch
                    {
                    }
                }
                if (world.Particles.Count >= MaxParticleSeeds)
                    NoteCap("playing particle systems", MaxParticleSeeds);
            }

            // Firestarter gasoline on surfaces. These strains are separate objects from the
            // in-flight GasolineProjectile and from the FireZone that appears once lit.
            try { CaptureGasolineStains(state, world); }
            catch (Exception ex) { state.Warnings.Add("gasoline: " + ex.Message); }

            SetExt(state, "World2", world);
        }

        partial void RestoreWorld2(Savestate state, ref int restored, ref int missing)
        {
            World2ModuleState world = GetExt<World2ModuleState>(state, "World2");
            if (world == null)
                return;

            for (int i = 0; i < world.Joints.Count; i++)
            {
                JointModuleState snapshot = world.Joints[i];
                if (snapshot == null)
                    continue;
                Joint joint = snapshot.Joint;
                if (joint == null || !GameBridge.IsAlive(joint))
                {
                    missing++;
                    continue;
                }
                try
                {
                    if (snapshot.HasConnectedBody && snapshot.ConnectedBody != null &&
                        GameBridge.IsAlive(snapshot.ConnectedBody))
                        joint.connectedBody = snapshot.ConnectedBody;
                    ApplyFields(joint, snapshot.Fields);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Joint restore skipped: " + ex.Message);
                }
            }

            for (int i = 0; i < world.Props.Count; i++)
            {
                PropBodyState prop = world.Props[i];
                if (prop == null)
                    continue;
                Rigidbody body = prop.Body;
                if (body == null || !GameBridge.IsAlive(body))
                {
                    // A prop destroyed after the save (a gib, an exploded canister) is not
                    // rebuilt: it is cosmetic and has no stable template.
                    continue;
                }
                try
                {
                    body.transform.position = prop.Position;
                    body.transform.rotation = prop.Rotation;
                    RestoreBody(prop.State);
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Physics prop restore skipped: " + ex.Message);
                }
            }

            for (int i = 0; i < world.Particles.Count; i++)
            {
                ParticleSeedState particle = world.Particles[i];
                if (particle == null)
                    continue;
                ParticleSystem system = particle.System;
                if (system == null || !GameBridge.IsAlive(system) || !system.gameObject.activeInHierarchy)
                    continue;
                try
                {
                    system.useAutoRandomSeed = false;
                    system.randomSeed = particle.RandomSeed;
                    if (particle.Time > MaxParticleResimulate)
                    {
                        if (particle.Playing)
                        {
                            if (!system.isPlaying)
                                system.Play(false);
                            if (particle.Paused && !system.isPaused)
                                system.Pause(false);
                            else if (!particle.Paused && system.isPaused)
                                system.Play(false);
                        }
                        else if (system.isPlaying)
                        {
                            system.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                        }
                    }
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Particle seed restore skipped: " + ex.Message);
                }
            }

            try { RestoreGasolineStains(world, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Gasoline stain restore skipped: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Firestarter gasoline on the ground
        // ------------------------------------------------------------------

        void CaptureGasolineStains(Savestate state, World2ModuleState world)
        {
            Type stainType = _bridge.FindType("GasolineStain");
            if (stainType == null)
                return;
            Type projectileType = _bridge.FindType("GasolineProjectile");
            Transform player = _bridge.PlayerTransform;

            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(stainType); }
            catch { return; }

            for (int i = 0; i < found.Length && world.Gasoline.Count < MaxGasolineStains; i++)
            {
                Component stain = found[i] as Component;
                if (stain == null || IsTemplateClone(stain.gameObject) || !stain.gameObject.scene.IsValid())
                    continue;
                Transform transform = stain.transform;
                if (player != null && transform.IsChildOf(player))
                    continue;
                // A stain still hanging off an in-flight droplet, or coating an enemy, is
                // owned by that projectile/enemy capture and is not ground oil.
                if (IsUnderType(transform, projectileType) || IsUnderEnemy(transform))
                    continue;

                try
                {
                    Renderer renderer = stain.GetComponent<Renderer>();
                    GasolineStainState snapshot = new GasolineStainState
                    {
                        Stain = stain,
                        Root = stain.gameObject,
                        RootName = CleanObjectName(stain.gameObject.name),
                        Parent = transform.parent,
                        Position = transform.position,
                        Rotation = transform.rotation,
                        Scale = transform.localScale,
                        ActiveSelf = stain.gameObject.activeSelf,
                        RendererEnabled = renderer == null || renderer.enabled
                    };
                    // A light history snapshot keeps the pose/size but skips the reflection
                    // fields and the template; slot and branch captures carry both.
                    if (!_lightCapture)
                    {
                        snapshot.Fields = CaptureGasolineFields(stain);
                        snapshot.Template = GetOrCreateTemplate(stain, stain.gameObject);
                        snapshot.HasTemplate = snapshot.Template != null;
                    }
                    world.Gasoline.Add(snapshot);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("gasoline stain: " + ex.Message);
                }
            }
            if (world.Gasoline.Count >= MaxGasolineStains)
                NoteCap("gasoline stains", MaxGasolineStains);
        }

        static List<FieldState> CaptureGasolineFields(Component stain)
        {
            List<FieldState> fields = CaptureFields(stain, 32);
            if (fields == null)
                return null;
            // `index` is assigned by StainVoxelManager when a stain registers; writing the
            // saved number onto a newly registered stain would point at another stain's slot.
            for (int i = fields.Count - 1; i >= 0; i--)
            {
                if (fields[i].Name == "index")
                    fields.RemoveAt(i);
            }
            return fields;
        }

        void RestoreGasolineStains(World2ModuleState world, ref int restored, ref int missing)
        {
            if (world.Gasoline == null || world.Gasoline.Count == 0)
                return;
            Type stainType = _bridge.FindType("GasolineStain");
            if (stainType == null)
                return;

            HashSet<GameObject> saved = new HashSet<GameObject>();
            for (int i = 0; i < world.Gasoline.Count; i++)
            {
                GasolineStainState snapshot = world.Gasoline[i];
                if (snapshot == null)
                    continue;
                Component stain = Resolve(snapshot.Stain);
                if (stain == null)
                {
                    // The stain was removed after the save (a surface changed, a burn
                    // consumed it): rebuild it from the stored stain template.
                    stain = RebuildGasolineStain(snapshot, stainType);
                    if (stain == null)
                    {
                        missing++;
                        continue;
                    }
                }
                // Keep a rebuilt stain out of the post-save sweep even if a later line throws.
                saved.Add(stain.gameObject);

                try
                {
                    Transform transform = stain.transform;
                    Transform parent = snapshot.Parent != null && GameBridge.IsAlive(snapshot.Parent)
                        ? snapshot.Parent
                        : null;
                    if (!ReferenceEquals(transform.parent, parent))
                        transform.SetParent(parent, true);
                    transform.position = snapshot.Position;
                    transform.rotation = snapshot.Rotation;
                    transform.localScale = snapshot.Scale;
                    if (transform.gameObject.activeSelf != snapshot.ActiveSelf)
                        transform.gameObject.SetActive(snapshot.ActiveSelf);
                    // Awake/Start run when an inactive stain is re-enabled; both touch
                    // initialSize/localScale, so put the captured values back afterwards.
                    ApplyFields(stain, snapshot.Fields);
                    transform.localScale = snapshot.Scale;
                    Renderer renderer = stain.GetComponent<Renderer>();
                    if (renderer != null && renderer.enabled != snapshot.RendererEnabled)
                        renderer.enabled = snapshot.RendererEnabled;
                    restored++;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Gasoline stain restore skipped: " + ex.Message);
                }
            }

            int removed = SweepGasolineStains(stainType, saved);
            if (removed > 0)
                _plugin.LogInfo("Savestate removed " + removed + " gasoline stain(s) sprayed after the save.");

            // Voxel/proxy bookkeeping is derived from the stain transforms. The manager's
            // cleanup drops entries for the stains the sweep above just destroyed.
            Type managerType = _bridge.FindType("StainVoxelManager");
            if (managerType != null)
            {
                try
                {
                    UnityEngine.Object manager = UnityEngine.Object.FindObjectOfType(managerType);
                    if (manager != null)
                        TryInvoke(manager, "CleanupStaleTransforms");
                }
                catch
                {
                }
            }
        }

        Component RebuildGasolineStain(GasolineStainState snapshot, Type stainType)
        {
            if (snapshot == null || !snapshot.HasTemplate || snapshot.Template == null)
                return null;
            GameObject instance;
            try
            {
                instance = UnityEngine.Object.Instantiate(snapshot.Template);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Gasoline stain respawn failed: " + ex.Message);
                return null;
            }

            instance.name = string.IsNullOrEmpty(snapshot.RootName) ? "Gasoline Stain" : snapshot.RootName;
            instance.SetActive(false);
            Transform transform = instance.transform;
            if (snapshot.Parent != null && GameBridge.IsAlive(snapshot.Parent))
                transform.SetParent(snapshot.Parent, false);
            transform.position = snapshot.Position;
            transform.rotation = snapshot.Rotation;
            transform.localScale = snapshot.Scale;

            Component stain = instance.GetComponent(stainType);
            if (stain == null)
            {
                UnityEngine.Object.Destroy(instance);
                return null;
            }

            // A plain Instantiate only produces the renderer. AttachTo is what parents the
            // stain and registers its voxel/proxy with StainVoxelManager, i.e. what makes
            // the restored fuel slippery and ignitable rather than a decal.
            ApplyFields(stain, snapshot.Fields);
            TryAttachGasolineStain(stain, transform.parent);
            instance.SetActive(true); // Awake/Start
            ApplyFields(stain, snapshot.Fields);
            transform.localScale = snapshot.Scale;
            Renderer renderer = stain.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = snapshot.RendererEnabled;

            snapshot.Stain = stain;
            snapshot.Root = instance;
            return stain;
        }

        /// <summary>
        /// Destroy ground stains that did not exist at the save. Stains under the player, an
        /// enemy or an in-flight GasolineProjectile belong to those objects' own captures.
        /// </summary>
        int SweepGasolineStains(Type stainType, HashSet<GameObject> saved)
        {
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(stainType); }
            catch { return 0; }
            Type projectileType = _bridge.FindType("GasolineProjectile");
            Transform player = _bridge.PlayerTransform;

            int removed = 0;
            for (int i = 0; i < found.Length; i++)
            {
                Component stain = found[i] as Component;
                if (stain == null)
                    continue;
                GameObject go = stain.gameObject;
                if (saved.Contains(go) || IsTemplateClone(go))
                    continue;
                Transform transform = stain.transform;
                if (player != null && transform.IsChildOf(player))
                    continue;
                if (IsUnderType(transform, projectileType) || IsUnderEnemy(transform))
                    continue;
                UnityEngine.Object.Destroy(go);
                removed++;
            }
            return removed;
        }

        static bool IsUnderType(Transform transform, Type type)
        {
            if (transform == null || type == null)
                return false;
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.GetComponent(type) != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Re-run GasolineStain.AttachTo(Collider, bool) on a rebuilt stain. `parent` is the
        /// surface transform saved from the original attachment, and the hit collider lives
        /// on exactly that transform, so its own Collider is the one to pass.
        /// </summary>
        static bool TryAttachGasolineStain(Component stain, Transform parent)
        {
            if (stain == null || parent == null)
                return false;
            Collider surface = parent.GetComponent<Collider>();
            if (surface == null)
                return false;
            try
            {
                MethodInfo[] methods = stain.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "AttachTo")
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Collider) ||
                        parameters[1].ParameterType != typeof(bool))
                        continue;
                    method.Invoke(stain, new object[] { surface, false });
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
