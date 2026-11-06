using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.UI;

namespace UltrakillTAS
{
    /// <summary>A delayed MonoBehaviour.Invoke("Name", delay) call found in a script's IL.</summary>
    public sealed class InvokeInfo
    {
        public string Name;
        public float Delay;
    }

    public sealed class GraphicState
    {
        public Graphic Graphic;
        public Color Color;
        public bool Enabled;
    }

    public sealed class PoseSnapshot
    {
        public Transform Transform;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;
        public Camera Camera;
        public float FieldOfView;
    }

    /// <summary>
    /// Finds every <c>Invoke("Method", delay)</c> a script can schedule by reading its IL.
    ///
    /// ULTRAKILL clears many of its states with delayed calls: Jump() sets jumping and
    /// jumpCooldown and schedules NotJumping/JumpReady to clear them. Those pending calls
    /// are not fields, so a field-only savestate restores "jumping = true" with nothing
    /// left to ever clear it: no jump, no ground friction, a jump sound retried every
    /// frame. Knowing which methods can be pending lets a state record and re-arm them.
    /// </summary>
    internal static class InvokeScanner
    {
        static readonly OpCode[] OneByte = new OpCode[256];
        static readonly OpCode[] TwoByte = new OpCode[256];
        static readonly bool[] HasOne = new bool[256];
        static readonly bool[] HasTwo = new bool[256];
        static readonly Dictionary<Type, InvokeInfo[]> Cache = new Dictionary<Type, InvokeInfo[]>();
        static readonly InvokeInfo[] None = new InvokeInfo[0];

        static InvokeScanner()
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(OpCode))
                    continue;
                OpCode op = (OpCode)fields[i].GetValue(null);
                int low = (ushort)op.Value & 0xFF;
                if (op.Size == 1)
                {
                    OneByte[low] = op;
                    HasOne[low] = true;
                }
                else
                {
                    TwoByte[low] = op;
                    HasTwo[low] = true;
                }
            }
        }

        public static InvokeInfo[] For(Type type)
        {
            InvokeInfo[] cached;
            if (Cache.TryGetValue(type, out cached))
                return cached;

            List<InvokeInfo> found = new List<InvokeInfo>(8);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (Type current = type;
                 current != null && current != typeof(MonoBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                MethodInfo[] methods;
                try
                {
                    methods = current.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }
                for (int i = 0; i < methods.Length; i++)
                {
                    try { ScanMethod(methods[i], found, seen); }
                    catch { }
                }
            }
            cached = found.Count == 0 ? None : found.ToArray();
            Cache[type] = cached;
            return cached;
        }

        static void ScanMethod(MethodInfo method, List<InvokeInfo> found, HashSet<string> seen)
        {
            MethodBody body = method.GetMethodBody();
            byte[] il = body != null ? body.GetILAsByteArray() : null;
            if (il == null || il.Length == 0)
                return;
            Module module = method.Module;

            // The pattern is ldstr "Name"; ldc.r4 delay; call MonoBehaviour::Invoke.
            OpCode previous1 = OpCodes.Nop;
            OpCode previous2 = OpCodes.Nop;
            string lastString = null;
            float lastFloat = 0f;
            int pos = 0;

            while (pos < il.Length)
            {
                OpCode op;
                int first = il[pos++];
                if (first == 0xFE)
                {
                    if (pos >= il.Length || !HasTwo[il[pos]])
                        return;
                    op = TwoByte[il[pos++]];
                }
                else
                {
                    if (!HasOne[first])
                        return;
                    op = OneByte[first];
                }

                int token = 0;
                float floatOperand = 0f;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        pos += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        pos += 8;
                        break;
                    case OperandType.ShortInlineR:
                        floatOperand = BitConverter.ToSingle(il, pos);
                        pos += 4;
                        break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, pos);
                        pos += 4 + 4 * count;
                        break;
                    default:
                        token = BitConverter.ToInt32(il, pos);
                        pos += 4;
                        break;
                }

                if (op == OpCodes.Ldstr)
                {
                    lastString = module.ResolveString(token);
                }
                else if (op == OpCodes.Ldc_R4)
                {
                    lastFloat = floatOperand;
                }
                else if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    MethodBase target = null;
                    try { target = module.ResolveMethod(token); }
                    catch { }
                    if (target != null && target.Name == "Invoke" && target.DeclaringType == typeof(MonoBehaviour))
                    {
                        // The method name is the last string loaded. A literal delay is kept; a
                        // computed one (weapons: Shoot, Pump, DelayedShoot) is recorded as
                        // unknown (0) so a pending call is still re-armed rather than lost.
                        bool constantDelay = previous1 == OpCodes.Ldc_R4 && previous2 == OpCodes.Ldstr;
                        if (!string.IsNullOrEmpty(lastString) && seen.Add(lastString))
                            found.Add(new InvokeInfo { Name = lastString, Delay = constantDelay ? lastFloat : 0f });
                    }
                }

                previous2 = previous1;
                previous1 = op;
            }
        }
    }

    public sealed partial class SaveStateManager
    {
        // ------------------------------------------------------------------
        // Pending Invoke calls
        // ------------------------------------------------------------------

        static void CaptureInvokes(Component component, ComponentSnapshot snapshot)
        {
            MonoBehaviour behaviour = component as MonoBehaviour;
            if (behaviour == null || snapshot == null)
                return;
            InvokeInfo[] known = InvokeScanner.For(behaviour.GetType());
            if (known.Length == 0)
                return;
            snapshot.KnownInvokes = known;
            for (int i = 0; i < known.Length; i++)
            {
                if (!behaviour.IsInvoking(known[i].Name))
                    continue;
                if (snapshot.PendingInvokes == null)
                    snapshot.PendingInvokes = new List<InvokeInfo>(2);
                snapshot.PendingInvokes.Add(known[i]);
            }
        }

        /// <summary>
        /// Clear every delayed call the script could have pending (some belong to the
        /// timeline being discarded) and re-arm the ones that were pending at capture. The
        /// full delay is used: the true remainder is not observable, but a slightly late
        /// "jump ready" is correct, while a missing one leaves the player stuck forever.
        /// </summary>
        static void RestoreInvokes(Component component, ComponentSnapshot snapshot)
        {
            MonoBehaviour behaviour = component as MonoBehaviour;
            if (behaviour == null || snapshot == null || snapshot.KnownInvokes == null)
                return;
            if (!behaviour.gameObject.activeInHierarchy)
                return;
            try
            {
                for (int i = 0; i < snapshot.KnownInvokes.Length; i++)
                    behaviour.CancelInvoke(snapshot.KnownInvokes[i].Name);
                if (snapshot.PendingInvokes == null)
                    return;
                for (int i = 0; i < snapshot.PendingInvokes.Count; i++)
                {
                    InvokeInfo invoke = snapshot.PendingInvokes[i];
                    behaviour.Invoke(invoke.Name, invoke.Delay > 0f ? invoke.Delay : 0.25f);
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Effect objects held in "current..." fields (slide dust, charge glow, drips)
        // ------------------------------------------------------------------

        static readonly Dictionary<Type, FieldInfo[]> EffectFieldCache = new Dictionary<Type, FieldInfo[]>();

        static bool IsEffectFieldName(string name)
        {
            if (!name.StartsWith("current", StringComparison.Ordinal))
                return false;
            // current weapon / current slot style fields are NOT effects.
            return name.IndexOf("Particle", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("Drip", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("Effect", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("Scrape", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("Charge", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("Dust", StringComparison.Ordinal) >= 0;
        }

        static FieldInfo[] GetEffectFields(Type type)
        {
            FieldInfo[] cached;
            if (EffectFieldCache.TryGetValue(type, out cached))
                return cached;
            List<FieldInfo> fields = new List<FieldInfo>(4);
            for (Type current = type;
                 current != null && current != typeof(MonoBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (declared[i].FieldType == typeof(GameObject) && IsEffectFieldName(declared[i].Name))
                        fields.Add(declared[i]);
                }
            }
            cached = fields.ToArray();
            EffectFieldCache[type] = cached;
            return cached;
        }

        /// <summary>Remember which effect fields were empty, so effects started later can be removed.</summary>
        static void CaptureEmptyEffects(Component component, ComponentSnapshot snapshot)
        {
            FieldInfo[] fields = GetEffectFields(component.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                GameObject value;
                try { value = fields[i].GetValue(component) as GameObject; }
                catch { continue; }
                if (value != null)
                    continue;
                if (snapshot.EmptyEffects == null)
                    snapshot.EmptyEffects = new List<FieldInfo>(2);
                snapshot.EmptyEffects.Add(fields[i]);
            }
        }

        static void ClearLateEffects(Component component, ComponentSnapshot snapshot)
        {
            if (snapshot == null || snapshot.EmptyEffects == null)
                return;
            for (int i = 0; i < snapshot.EmptyEffects.Count; i++)
            {
                try
                {
                    GameObject effect = snapshot.EmptyEffects[i].GetValue(component) as GameObject;
                    // Scene clones only; a prefab asset reference is never destroyed.
                    if (effect != null && effect.scene.IsValid())
                    {
                        UnityEngine.Object.Destroy(effect);
                        snapshot.EmptyEffects[i].SetValue(component, null);
                    }
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // Death and the damage screen
        // ------------------------------------------------------------------

        static bool TryInvoke(object target, string methodName)
        {
            if (target == null)
                return false;
            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (method == null)
                    return false;
                method.Invoke(target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Loading a state taken while alive must undo a death: the game switches on a
        /// DeathSequence (the "[YOU ARE DEAD]" screen, slow-down, music pitch and a
        /// post-processing effect), disables the camera, takes the weapons and fists away and
        /// frees the rigidbody. None of that is a plain field, so it is reversed explicitly
        /// with the game's own methods.
        /// </summary>
        void RevivePlayerIfDead(Savestate state)
        {
            if (state.Player == null || state.Player.Dead)
                return;
            Component player = _bridge.NewMovement as Component;
            if (player == null)
                return;

            bool dead = ReadBool(player, DeadNames, false);
            Component death = GetReference<Component>(player, "deathSequence");
            bool deathScreenUp = death != null && death.gameObject.activeSelf;
            if (!dead && !deathScreenUp)
                return;

            if (death != null)
            {
                TryInvoke(death, "EndSequence");
                GameObject screen = GetReference<GameObject>(death, "deathScreen");
                if (screen != null)
                    screen.SetActive(false);
                // OnDisable resets the global shader fade, music pitch and the death effect.
                death.gameObject.SetActive(false);
            }

            SetPostProcessDeathEffect(false);
            SetBool(player, "dead", false);
            TryInvoke(player, "ActivatePlayer");

            // Camera control comes back with ActivatePlayer on most builds; make sure.
            Behaviour cameraController = GetReference<Behaviour>(player, "cc");
            if (cameraController != null && !cameraController.enabled)
                cameraController.enabled = true;

            _plugin.LogInfo("Savestate load: revived the player from the death screen.");
        }

        void SetPostProcessDeathEffect(bool on)
        {
            try
            {
                Type type = _bridge.FindType("PostProcessV2_Handler");
                if (type == null)
                    return;
                UnityEngine.Object handler = UnityEngine.Object.FindObjectOfType(type);
                if (handler == null)
                    return;
                MethodInfo method = type.GetMethod("DeathEffect",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(bool) }, null);
                if (method != null)
                    method.Invoke(handler, new object[] { on });
            }
            catch
            {
            }
        }

        static void AddGraphic(Savestate state, Graphic graphic)
        {
            if (graphic == null)
                return;
            state.Graphics.Add(new GraphicState { Graphic = graphic, Color = graphic.color, Enabled = graphic.enabled });
        }

        static void AddHudFlag(Savestate state, GameObject go)
        {
            if (go == null)
                return;
            state.PlayerActive.Add(new ActiveFlag { Object = go, Active = go.activeSelf });
        }

        static Transform HudRoot(Component player)
        {
            Component canvas = GetReference<Component>(player, "fullHud");
            if (canvas != null)
                return canvas.transform;
            GameObject hud = GetReference<GameObject>(player, "screenHud");
            return hud != null ? hud.transform : null;
        }

        /// <summary>The red hurt screen, the HP / hard-damage flashes and screen-blood overlays.</summary>
        void CaptureVitals(Savestate state)
        {
            Component player = _bridge.NewMovement as Component;
            if (player == null)
                return;

            // Death switches the game's HUD objects off (health, weapon and style panels). Their
            // active state is part of the savestate, so loading a living state brings them back.
            AddHudFlag(state, GetReference<GameObject>(player, "screenHud"));
            AddHudFlag(state, GetReference<GameObject>(player, "hudCam"));
            Transform hudRoot = HudRoot(player);
            if (hudRoot != null)
            {
                Transform[] hudObjects = hudRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < hudObjects.Length && i < 256; i++)
                    AddHudFlag(state, hudObjects[i].gameObject);
            }

            AddGraphic(state, GetReference<Graphic>(player, "hurtScreen"));
            string[] flashNames = { "hpFlash", "antiHpFlash" };
            for (int i = 0; i < flashNames.Length; i++)
            {
                Component flash = GetReference<Component>(player, flashNames[i]);
                if (flash == null)
                    continue;
                ComponentSnapshot snapshot = CaptureComponent(flash, 0);
                if (snapshot != null)
                    state.HudParts.Add(snapshot);
                AddGraphic(state, GetReference<Graphic>(flash, "img"));
            }

            // Screen-blood overlays are clones spawned under the HUD each time you are hit.
            GameObject prefab = GetReference<GameObject>(player, "scrnBlood");
            Transform hud = HudRoot(player);
            if (prefab == null || hud == null)
                return;
            state.BloodOverlayName = CleanObjectName(prefab.name);
            Transform[] children = hud.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].name.StartsWith(state.BloodOverlayName, StringComparison.Ordinal))
                    state.BloodOverlayIds.Add(children[i].gameObject.GetInstanceID());
            }
        }

        void RestoreVitals(Savestate state)
        {
            for (int i = 0; i < state.HudParts.Count; i++)
                RestoreComponent(state.HudParts[i]);
            for (int i = 0; i < state.Graphics.Count; i++)
            {
                GraphicState graphic = state.Graphics[i];
                if (graphic.Graphic == null)
                    continue;
                try
                {
                    graphic.Graphic.color = graphic.Color;
                    graphic.Graphic.enabled = graphic.Enabled;
                }
                catch
                {
                }
            }

            if (string.IsNullOrEmpty(state.BloodOverlayName))
                return;
            Component player = _bridge.NewMovement as Component;
            Transform hud = player != null ? HudRoot(player) : null;
            if (hud == null)
                return;
            Transform[] children = hud.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || !child.name.StartsWith(state.BloodOverlayName, StringComparison.Ordinal))
                    continue;
                if (state.BloodOverlayIds.Contains(child.gameObject.GetInstanceID()))
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        // ------------------------------------------------------------------
        // Camera and weapon-holder poses
        // ------------------------------------------------------------------

        static void AddPose(List<PoseSnapshot> poses, Component component)
        {
            if (component == null)
                return;
            Transform transform = component.transform;
            PoseSnapshot pose = new PoseSnapshot
            {
                Transform = transform,
                LocalPosition = transform.localPosition,
                LocalRotation = transform.localRotation,
                LocalScale = transform.localScale,
                Camera = transform.GetComponent<Camera>()
            };
            if (pose.Camera != null)
                pose.FieldOfView = pose.Camera.fieldOfView;
            poses.Add(pose);
        }

        void CaptureRigPoses(Savestate state, Component camera, Component gun, Component fists)
        {
            AddPose(state.RigPoses, camera);
            AddPose(state.RigPoses, gun);
            AddPose(state.RigPoses, fists);
            Component player = _bridge.NewMovement as Component;
            GameObject hudCamera = player != null ? GetReference<GameObject>(player, "hudCam") : null;
            if (hudCamera != null)
                AddPose(state.RigPoses, hudCamera.transform);
        }

        static void RestoreRigPoses(Savestate state)
        {
            for (int i = 0; i < state.RigPoses.Count; i++)
            {
                PoseSnapshot pose = state.RigPoses[i];
                if (pose.Transform == null)
                    continue;
                try
                {
                    pose.Transform.localPosition = pose.LocalPosition;
                    pose.Transform.localRotation = pose.LocalRotation;
                    pose.Transform.localScale = pose.LocalScale;
                    if (pose.Camera != null)
                        pose.Camera.fieldOfView = pose.FieldOfView;
                }
                catch
                {
                }
            }
        }
    }
}
