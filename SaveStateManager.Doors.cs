using System;
using System.Collections.Generic;
using System.Reflection;
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
    }
}
