using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>The enabled flag of one Behaviour (Light, OcclusionPortal, door sub-scripts).</summary>
    public sealed class BehaviourFlag
    {
        public Behaviour Behaviour;
        public bool Enabled;
    }

    /// <summary>
    /// The visible and physical side of a door. Door.Open and Door.Close do more than set fields:
    /// they set the door's own Animator, open or close its OcclusionPortal, toggle colliders and
    /// enable components, and colour the lights through ChangeColor. Restoring only the fields
    /// left the light lit and the door's animation and collision at the wrong state.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        static void CaptureDoorVisuals(DoorState snapshot, Component door)
        {
            GameObject root = door.gameObject;
            snapshot.RootAnimators = CaptureAnimators(root, 8, true);
            snapshot.RootColliders = CaptureColliders(root, 32);

            snapshot.RootBehaviours = new List<BehaviourFlag>(8);
            Behaviour[] behaviours = root.GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < behaviours.Length && snapshot.RootBehaviours.Count < 64; i++)
            {
                Behaviour behaviour = behaviours[i];
                // Animators are captured with their layer state above.
                if (behaviour == null || behaviour is Animator)
                    continue;
                snapshot.RootBehaviours.Add(new BehaviourFlag { Behaviour = behaviour, Enabled = behaviour.enabled });
            }

            object color;
            if (TryReadField(door, "currentLightsColor", out color) && color is Color)
            {
                snapshot.HasLightColor = true;
                snapshot.LightColor = (Color)color;
            }
        }

        static void RestoreDoorVisuals(DoorState snapshot, Component door)
        {
            RestoreAnimators(snapshot.RootAnimators);
            RestoreColliders(snapshot.RootColliders);

            if (snapshot.RootBehaviours != null)
            {
                for (int i = 0; i < snapshot.RootBehaviours.Count; i++)
                {
                    BehaviourFlag flag = snapshot.RootBehaviours[i];
                    if (flag.Behaviour == null || flag.Behaviour.enabled == flag.Enabled)
                        continue;
                    try { flag.Behaviour.enabled = flag.Enabled; }
                    catch { }
                }
            }

            // The light colour is also painted through a MaterialPropertyBlock that is not
            // captured, so the game's own ChangeColor repaints it from the saved colour.
            if (snapshot.HasLightColor)
                TryInvokeWithColor(door, "ChangeColor", snapshot.LightColor);
        }

        static bool TryReadField(object target, string name, out object value)
        {
            value = null;
            if (target == null)
                return false;
            FieldInfo field = NamedField(target.GetType(), name);
            if (field == null)
                return false;
            try
            {
                value = field.GetValue(target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void TryInvokeWithColor(object target, string methodName, Color value)
        {
            if (target == null)
                return;
            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Color) }, null);
                if (method != null)
                    method.Invoke(target, new object[] { value });
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Spawn and exit doors: FinalDoor, FinalDoorOpener and OnLevelStart
        // ------------------------------------------------------------------
        //
        // The first room's door is not opened by a DoorController. A FinalDoorOpener does it from
        // Awake: FinalDoor.Open() (aboutToOpen, light on, Invoke("OpenDoors", 1)) and
        // Invoke("GoTime", 1), which sets opened and calls OnLevelStart.StartLevel (timer, music,
        // level name). FinalDoor.OpenDoors then calls Door.Open on the leaves, once: it returns
        // when opened is already true.
        //
        // A load (F4 to the start state) put the Door leaves back to closed, but FinalDoor.opened,
        // FinalDoorOpener.opened and OnLevelStart.activated kept their later values, so nothing
        // ever opened the spawn door again. These flags, the two pending Invokes and the door
        // light are saved here, independently of the generic map capture. An opener that a load
        // puts back to "not fired yet" also has its Awake run again the next time it is enabled
        // (SpawnDoorTracker): Unity runs Awake only once per object, and OnEnable alone does not
        // open the door.

        sealed class FinalDoorCapture
        {
            public Component Door;
            public bool Opened;
            public bool AboutToOpen;
            public bool OpenPending;
            public float OpenRemaining;
            public bool HasLight;
            public bool LightActive;
            public bool HasBlocker;
            public bool BlockerActive;
            public List<KeyValuePair<Renderer, Material>> Materials;
        }

        sealed class FinalDoorOpenerCapture
        {
            public Component Opener;
            public bool Active;
            public bool Opened;
            public bool Opening;
            public bool Closed;
            public bool GoPending;
            public float GoRemaining;
        }

        sealed class SpawnDoorsState
        {
            public List<FinalDoorCapture> Doors = new List<FinalDoorCapture>();
            public List<FinalDoorOpenerCapture> Openers = new List<FinalDoorOpenerCapture>();
            public Component LevelStart;
            public bool LevelStartActivated;
            public bool LevelStartFogHidden;
            public bool Fog;
        }

        const int MaxFinalDoorRenderers = 64;

        static List<Component> SceneComponentsOf(Type type)
        {
            List<Component> result = new List<Component>();
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return result;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
            catch { return result; }
            if (found == null)
                return result;
            for (int i = 0; i < found.Length; i++)
            {
                Component component = found[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                    result.Add(component);
            }
            return result;
        }

        /// <summary>Called at the end of CaptureDoors.</summary>
        void CaptureSpawnDoors(Savestate state)
        {
            Type doorType = _bridge.FindType("FinalDoor");
            Type openerType = _bridge.FindType("FinalDoorOpener");
            Type levelStartType = _bridge.FindType("OnLevelStart");
            if (doorType == null && openerType == null && levelStartType == null)
                return;

            SpawnDoorsState saved = new SpawnDoorsState();
            List<Component> doors = SceneComponentsOf(doorType);
            for (int i = 0; i < doors.Count; i++)
            {
                Component door = doors[i];
                try
                {
                    MonoBehaviour behaviour = door as MonoBehaviour;
                    FinalDoorCapture snapshot = new FinalDoorCapture
                    {
                        Door = door,
                        Opened = GetBool(door, "opened"),
                        AboutToOpen = GetBool(door, "aboutToOpen"),
                        OpenPending = behaviour != null && behaviour.IsInvoking(SpawnDoorTracker.OpenDoorsMethod)
                    };
                    if (snapshot.OpenPending)
                        snapshot.OpenRemaining = SpawnDoorTracker.Remaining(door, SpawnDoorTracker.OpenDoorsMethod);

                    GameObject light = GetReference<GameObject>(door, "doorLight");
                    if (light != null)
                    {
                        snapshot.HasLight = true;
                        snapshot.LightActive = light.activeSelf;
                    }
                    GameObject blocker = GetReference<GameObject>(door, "closingBlocker");
                    if (blocker != null)
                    {
                        snapshot.HasBlocker = true;
                        snapshot.BlockerActive = blocker.activeSelf;
                    }
                    snapshot.Materials = CaptureFinalDoorMaterials(door);
                    saved.Doors.Add(snapshot);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("final door " + door.name + ": " + ex.Message);
                }
            }

            List<Component> openers = SceneComponentsOf(openerType);
            for (int i = 0; i < openers.Count; i++)
            {
                Component opener = openers[i];
                try
                {
                    MonoBehaviour behaviour = opener as MonoBehaviour;
                    FinalDoorOpenerCapture snapshot = new FinalDoorOpenerCapture
                    {
                        Opener = opener,
                        Active = opener.gameObject.activeSelf,
                        Opened = GetBool(opener, "opened"),
                        Opening = GetBool(opener, "opening"),
                        Closed = GetBool(opener, "closed"),
                        GoPending = behaviour != null && behaviour.IsInvoking(SpawnDoorTracker.GoTimeMethod)
                    };
                    if (snapshot.GoPending)
                        snapshot.GoRemaining = SpawnDoorTracker.Remaining(opener, SpawnDoorTracker.GoTimeMethod);
                    saved.Openers.Add(snapshot);
                }
                catch (Exception ex)
                {
                    state.Warnings.Add("final door opener " + opener.name + ": " + ex.Message);
                }
            }

            List<Component> levelStarts = SceneComponentsOf(levelStartType);
            if (levelStarts.Count > 0)
            {
                Component levelStart = levelStarts[0];
                saved.LevelStart = levelStart;
                saved.LevelStartActivated = GetBool(levelStart, "activated");
                saved.LevelStartFogHidden = GetBool(levelStart, "fogHidden");
                saved.Fog = RenderSettings.fog;
            }

            SetExt(state, "SpawnDoors", saved);
        }

        /// <summary>The door's light panels: renderers showing one of its on/off materials.</summary>
        static List<KeyValuePair<Renderer, Material>> CaptureFinalDoorMaterials(Component door)
        {
            List<KeyValuePair<Renderer, Material>> result = new List<KeyValuePair<Renderer, Material>>();
            Material[] on = GetReferenceArray<Material>(door, "onMaterials");
            Material[] off = GetReferenceArray<Material>(door, "offMaterials");
            if (on.Length == 0 && off.Length == 0)
                return result;
            HashSet<Material> known = new HashSet<Material>();
            for (int i = 0; i < on.Length; i++)
                if (on[i] != null)
                    known.Add(on[i]);
            for (int i = 0; i < off.Length; i++)
                if (off[i] != null)
                    known.Add(off[i]);
            MeshRenderer[] renderers = door.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length && result.Count < MaxFinalDoorRenderers; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                Material material = renderer.sharedMaterial;
                if (material != null && known.Contains(material))
                    result.Add(new KeyValuePair<Renderer, Material>(renderer, material));
            }
            return result;
        }

        /// <summary>Called at the end of RestoreDoors, after the Door leaves are back.</summary>
        void RestoreSpawnDoors(Savestate state)
        {
            SpawnDoorsState saved = GetExt<SpawnDoorsState>(state, "SpawnDoors");
            if (saved == null)
                return;

            // Openers first: switching one on can run its Awake, which calls FinalDoor.Open; the
            // door values written below must win over that.
            for (int i = 0; i < saved.Openers.Count; i++)
            {
                FinalDoorOpenerCapture snapshot = saved.Openers[i];
                Component opener = Resolve(snapshot.Opener);
                if (opener == null)
                    continue;
                try
                {
                    GameObject go = opener.gameObject;
                    if (go.activeSelf != snapshot.Active)
                        go.SetActive(snapshot.Active);
                    SetBool(opener, "opened", snapshot.Opened);
                    SetBool(opener, "opening", snapshot.Opening);
                    SetBool(opener, "closed", snapshot.Closed);

                    MonoBehaviour behaviour = opener as MonoBehaviour;
                    if (behaviour != null)
                    {
                        behaviour.CancelInvoke(SpawnDoorTracker.GoTimeMethod);
                        if (snapshot.GoPending && go.activeInHierarchy)
                        {
                            behaviour.Invoke(SpawnDoorTracker.GoTimeMethod, snapshot.GoRemaining);
                            SpawnDoorTracker.MarkArmed(opener, SpawnDoorTracker.GoTimeMethod, snapshot.GoRemaining);
                        }
                    }

                    // Not fired yet at the save: the next enable must start the door again.
                    if (!snapshot.Opened && !snapshot.Opening && !snapshot.GoPending)
                        SpawnDoorTracker.MarkRearm(opener);
                    else
                        SpawnDoorTracker.ClearRearm(opener);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Final door opener restore skipped: " + ex.Message);
                }
            }

            for (int i = 0; i < saved.Doors.Count; i++)
            {
                FinalDoorCapture snapshot = saved.Doors[i];
                Component door = Resolve(snapshot.Door);
                if (door == null)
                    continue;
                try
                {
                    SetBool(door, "opened", snapshot.Opened);
                    SetBool(door, "aboutToOpen", snapshot.AboutToOpen);
                    if (snapshot.HasLight)
                    {
                        GameObject light = GetReference<GameObject>(door, "doorLight");
                        if (light != null && light.activeSelf != snapshot.LightActive)
                            light.SetActive(snapshot.LightActive);
                    }
                    if (snapshot.HasBlocker)
                    {
                        GameObject blocker = GetReference<GameObject>(door, "closingBlocker");
                        if (blocker != null && blocker.activeSelf != snapshot.BlockerActive)
                            blocker.SetActive(snapshot.BlockerActive);
                    }
                    if (snapshot.Materials != null)
                    {
                        for (int m = 0; m < snapshot.Materials.Count; m++)
                        {
                            Renderer renderer = snapshot.Materials[m].Key;
                            Material material = snapshot.Materials[m].Value;
                            if (renderer != null && material != null && renderer.sharedMaterial != material)
                                renderer.sharedMaterial = material;
                        }
                    }

                    MonoBehaviour behaviour = door as MonoBehaviour;
                    if (behaviour != null)
                    {
                        behaviour.CancelInvoke(SpawnDoorTracker.OpenDoorsMethod);
                        if (snapshot.OpenPending && door.gameObject.activeInHierarchy)
                        {
                            behaviour.Invoke(SpawnDoorTracker.OpenDoorsMethod, snapshot.OpenRemaining);
                            SpawnDoorTracker.MarkArmed(door, SpawnDoorTracker.OpenDoorsMethod, snapshot.OpenRemaining);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Final door restore skipped: " + ex.Message);
                }
            }

            // OnLevelStart.StartLevel runs once (activated). Left set, the run after a load never
            // got its level start again: no timer start, no music, no level name.
            Component levelStart = Resolve(saved.LevelStart);
            if (levelStart != null)
            {
                try
                {
                    SetBool(levelStart, "activated", saved.LevelStartActivated);
                    SetBool(levelStart, "fogHidden", saved.LevelStartFogHidden);
                    if (RenderSettings.fog != saved.Fog)
                        RenderSettings.fog = saved.Fog;
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Level start restore skipped: " + ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Harmony hooks behind the FinalDoor savestate (see SaveStateManager.Doors.cs).
    ///
    /// FinalDoor.Open and FinalDoorOpener.Awake both arm a 1 s Invoke. The time it was armed is
    /// recorded so a savestate re-arms the remainder instead of the full second. An opener that a
    /// load reset to "not fired yet" gets its Awake run again on its next OnEnable: OnEnable on
    /// its own only reopens a door that was closed with FinalDoorOpener.Close.
    /// </summary>
    internal static class SpawnDoorTracker
    {
        public const string OpenDoorsMethod = "OpenDoors";
        public const string GoTimeMethod = "GoTime";
        /// <summary>Delay of both Invokes in the game's IL (FinalDoor.Open, FinalDoorOpener.Awake/OnEnable).</summary>
        public const float ArmDelay = 1f;
        const int MaxTracked = 512;

        static Plugin _plugin;
        static Harmony _harmony;
        static MethodInfo _openerAwake;
        static FieldInfo _openerOpened;
        static FieldInfo _openerOpening;
        static FieldInfo _openerClosed;

        /// <summary>Time.time at which "instanceId|method" was armed.</summary>
        static readonly Dictionary<string, float> ArmedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>Openers a load reset to "not fired yet".</summary>
        static readonly HashSet<int> Rearm = new HashSet<int>();

        public static bool Installed { get; private set; }

        public static void Install(Plugin plugin)
        {
            _plugin = plugin;
            if (Installed)
                return;
            try
            {
                Type doorType = AccessTools.TypeByName("FinalDoor");
                Type openerType = AccessTools.TypeByName("FinalDoorOpener");
                if (doorType == null || openerType == null)
                {
                    plugin.LogWarning("Spawn door hooks skipped: FinalDoor/FinalDoorOpener not found.");
                    return;
                }
                MethodInfo doorOpen = AccessTools.Method(doorType, "Open", Type.EmptyTypes);
                _openerAwake = AccessTools.Method(openerType, "Awake", Type.EmptyTypes);
                MethodInfo openerEnable = AccessTools.Method(openerType, "OnEnable", Type.EmptyTypes);
                _openerOpened = AccessTools.Field(openerType, "opened");
                _openerOpening = AccessTools.Field(openerType, "opening");
                _openerClosed = AccessTools.Field(openerType, "closed");
                if (doorOpen == null || _openerAwake == null || openerEnable == null ||
                    _openerOpened == null || _openerOpening == null || _openerClosed == null)
                {
                    plugin.LogWarning("Spawn door hooks skipped: FinalDoor/FinalDoorOpener members changed.");
                    return;
                }

                _harmony = new Harmony("com.tas.ultrakill.spawndoor");
                BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                _harmony.Patch(doorOpen, postfix: new HarmonyMethod(typeof(SpawnDoorTracker).GetMethod("DoorOpenPostfix", flags)));
                _harmony.Patch(_openerAwake, postfix: new HarmonyMethod(typeof(SpawnDoorTracker).GetMethod("OpenerAwakePostfix", flags)));
                _harmony.Patch(openerEnable, postfix: new HarmonyMethod(typeof(SpawnDoorTracker).GetMethod("OpenerEnablePostfix", flags)));
                Installed = true;
            }
            catch (Exception ex)
            {
                plugin.LogWarning("Spawn door hooks failed: " + ex.Message);
            }
        }

        static string Key(Component component, string method)
        {
            return component.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + method;
        }

        /// <summary>Seconds left on a pending 1 s Invoke; the full delay when its start was not seen.</summary>
        public static float Remaining(Component component, string method)
        {
            float armed;
            if (component == null || !ArmedAt.TryGetValue(Key(component, method), out armed))
                return ArmDelay;
            return Mathf.Clamp(ArmDelay - (Time.time - armed), 0.008f, ArmDelay);
        }

        /// <summary>A load re-armed the Invoke with <paramref name="remaining"/> seconds left.</summary>
        public static void MarkArmed(Component component, string method, float remaining)
        {
            if (component == null)
                return;
            if (ArmedAt.Count > MaxTracked)
                ArmedAt.Clear();
            ArmedAt[Key(component, method)] = Time.time - (ArmDelay - remaining);
        }

        public static void MarkRearm(Component opener)
        {
            if (opener == null)
                return;
            if (Rearm.Count > MaxTracked)
                Rearm.Clear();
            Rearm.Add(opener.GetInstanceID());
        }

        public static void ClearRearm(Component opener)
        {
            if (opener != null)
                Rearm.Remove(opener.GetInstanceID());
        }

        /// <summary>Start of a load: marks from an earlier load must not fire while this one switches objects on.</summary>
        public static void ClearAllRearm()
        {
            Rearm.Clear();
        }

        static void DoorOpenPostfix(Component __instance)
        {
            if (__instance == null)
                return;
            if (ArmedAt.Count > MaxTracked)
                ArmedAt.Clear();
            ArmedAt[Key(__instance, OpenDoorsMethod)] = Time.time;
        }

        static void OpenerAwakePostfix(MonoBehaviour __instance)
        {
            if (__instance == null || !__instance.IsInvoking(GoTimeMethod))
                return;
            if (ArmedAt.Count > MaxTracked)
                ArmedAt.Clear();
            ArmedAt[Key(__instance, GoTimeMethod)] = Time.time;
        }

        static void OpenerEnablePostfix(MonoBehaviour __instance)
        {
            if (__instance == null)
                return;
            try
            {
                // First activation: Awake already ran just before this and set opening/opened,
                // so this does nothing. After a load reset the opener, Awake will not run
                // again by itself; run it now so the door opens as it did the first time.
                if (Rearm.Remove(__instance.GetInstanceID()) &&
                    !(bool)_openerOpened.GetValue(__instance) &&
                    !(bool)_openerOpening.GetValue(__instance) &&
                    !__instance.IsInvoking(GoTimeMethod))
                {
                    _openerAwake.Invoke(__instance, null);
                    return;
                }
                // OnEnable re-arms GoTime itself after FinalDoorOpener.Close.
                if ((bool)_openerClosed.GetValue(__instance) && __instance.IsInvoking(GoTimeMethod))
                {
                    if (ArmedAt.Count > MaxTracked)
                        ArmedAt.Clear();
                    ArmedAt[Key(__instance, GoTimeMethod)] = Time.time;
                }
            }
            catch (Exception ex)
            {
                if (_plugin != null)
                    _plugin.LogWarning("Spawn door re-arm failed: " + ex.Message);
            }
        }
    }
}
