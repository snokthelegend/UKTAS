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
            public List<ParticleSnapshot> Particles = new List<ParticleSnapshot>();
            public List<GasolineStainState> Gasoline = new List<GasolineStainState>();
            /// <summary>Fire propagation queues owned by StainVoxelManager, not by a stain.</summary>
            public List<Vector3Int> PendingIgnitions;
            public List<Vector3Int> ExplodedVoxels;
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

            // World emitters are captured here; player and enemy emitters are already owned by
            // their own captures and are skipped so a slot does not store the same buffers twice.
            // Light history skips this pass entirely: the per-tick walk over every emitter is
            // the expensive part, and a light state never takes live particle buffers.
            if (!_lightCapture)
            {
                UnityEngine.Object[] particles;
                try { particles = UnityEngine.Object.FindObjectsOfType(typeof(ParticleSystem)); }
                catch { particles = new UnityEngine.Object[0]; }
                Transform playerTransform = _bridge.PlayerTransform;
                for (int i = 0; i < particles.Length && world.Particles.Count < MaxParticleSeeds; i++)
                {
                    ParticleSystem system = particles[i] as ParticleSystem;
                    if (system == null || IsTemplateClone(system.gameObject))
                        continue;
                    if (playerTransform != null && system.transform.IsChildOf(playerTransform))
                        continue;
                    if (IsUnderEnemy(system.transform))
                        continue;
                    try
                    {
                        ParticleSnapshot snapshot = CaptureParticle(system, true);
                        if (snapshot != null)
                            world.Particles.Add(snapshot);
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
            try { CaptureStainVoxelQueue(world); }
            catch (Exception ex) { state.Warnings.Add("stain voxel queue: " + ex.Message); }

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

            if (world.Particles.Count > 0)
            {
                RestoreParticles(world.Particles);
                restored += world.Particles.Count;
            }

            try { RestoreGasolineStains(world, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Gasoline stain restore skipped: " + ex.Message); }
            try { RestoreStainVoxelQueue(world); }
            catch (Exception ex) { _plugin.LogWarning("Stain voxel queue restore skipped: " + ex.Message); }
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

        /// <summary>
        /// The voxel manager derives its stain map from the restored stains, but two plain
        /// sets drive the next propagation tick: pending ignitions and the explosion guard.
        /// They are copied by value; the voxel map itself is rebuilt by AttachTo.
        /// </summary>
        void CaptureStainVoxelQueue(World2ModuleState world)
        {
            Type managerType = _bridge.FindType("StainVoxelManager");
            if (managerType == null)
                return;
            UnityEngine.Object manager = UnityEngine.Object.FindObjectOfType(managerType);
            if (manager == null)
                return;
            world.PendingIgnitions = CaptureVectorSet(manager, managerType, "pendingIgnitions");
            world.ExplodedVoxels = CaptureVectorSet(manager, managerType, "explodedVoxels");
        }

        static List<Vector3Int> CaptureVectorSet(object owner, Type ownerType, string fieldName)
        {
            FieldInfo field = NamedField(ownerType, fieldName);
            if (field == null)
                return null;
            HashSet<Vector3Int> values = field.GetValue(owner) as HashSet<Vector3Int>;
            return values != null ? new List<Vector3Int>(values) : null;
        }

        void RestoreStainVoxelQueue(World2ModuleState world)
        {
            if ((world.PendingIgnitions == null || world.PendingIgnitions.Count == 0) &&
                (world.ExplodedVoxels == null || world.ExplodedVoxels.Count == 0))
                return;
            Type managerType = _bridge.FindType("StainVoxelManager");
            if (managerType == null)
                return;
            UnityEngine.Object manager = UnityEngine.Object.FindObjectOfType(managerType);
            if (manager == null)
                return;
            RestoreVectorSet(manager, managerType, "pendingIgnitions", world.PendingIgnitions);
            RestoreVectorSet(manager, managerType, "explodedVoxels", world.ExplodedVoxels);
        }

        static void RestoreVectorSet(object owner, Type ownerType, string fieldName, List<Vector3Int> values)
        {
            FieldInfo field = NamedField(ownerType, fieldName);
            if (field == null)
                return;
            HashSet<Vector3Int> set = field.GetValue(owner) as HashSet<Vector3Int>;
            if (set == null)
                return;
            set.Clear();
            if (values == null)
                return;
            for (int i = 0; i < values.Count; i++)
                set.Add(values[i]);
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
        /// Re-run GasolineStain.AttachTo on a rebuilt stain. Current ULTRAKILL exposes
        /// AttachTo(Collider); older builds had AttachTo(Collider, bool). `parent` is the
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
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Collider))
                    {
                        method.Invoke(stain, new object[] { surface });
                        return true;
                    }
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(Collider) &&
                        parameters[1].ParameterType == typeof(bool))
                    {
                        method.Invoke(stain, new object[] { surface, false });
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
