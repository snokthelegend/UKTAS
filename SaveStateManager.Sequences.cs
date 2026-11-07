using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Cutscene and sequence teardown on load.
    ///
    /// A cutscene is driven by components that start coroutines and delayed calls, and it often
    /// instantiates clones while it runs (the "ObjectActivator (Clone)" a sequence spawns).
    /// Restoring the fields of a state taken before the cutscene started is not enough: the
    /// coroutine keeps running and the clones stay alive, so the map ends up midway through the
    /// sequence and the trigger never fires again. This partial deletes the clones that did not
    /// exist at capture, stops the drivers that were idle at capture, and puts the clips of the
    /// objects the sequence switched on back to their first frame, so the sequence starts over
    /// from its first tick the next time it is triggered.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        /// <summary>The scripts that start and step a sequence, deduplicated against _levelTypes.</summary>
        static readonly string[] SequenceTypeNames =
        {
            "ObjectActivator", "ObjectActivatorStay", "ObjectActivateInSequence",
            "EventsSequence", "ObjectSpawner"
        };

        readonly List<Type> _sequenceTypes = new List<Type>(8);

        void ResolveSequenceTypes()
        {
            _sequenceTypes.Clear();
            for (int i = 0; i < SequenceTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(SequenceTypeNames[i]);
                if (type != null && !_sequenceTypes.Contains(type))
                    _sequenceTypes.Add(type);
            }
            // Any other script the game calls a sequence (a director added by an update) is
            // reset the same way instead of being left running.
            for (int i = 0; i < _levelTypes.Count && _sequenceTypes.Count < 16; i++)
            {
                Type type = _levelTypes[i];
                if (type != null && type.Name.IndexOf("Sequence", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !_sequenceTypes.Contains(type))
                    _sequenceTypes.Add(type);
            }
        }

        /// <summary>
        /// Remembers the sequence drivers alive now, so a load can tell which of them the
        /// cutscene spawned afterwards. A fresh typed scan is used, not the cached level list,
        /// because the clones created during a sequence are exactly what the cache misses.
        /// </summary>
        void CaptureSequenceObjects(Savestate state)
        {
            if (_sequenceTypes.Count == 0)
                return;
            state.CapturedSequences = true;
            for (int t = 0; t < _sequenceTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_sequenceTypes[t], true); }
                catch { continue; }
                for (int i = 0; i < found.Length && state.SequenceObjects.Count < MaxLevelElements; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || !component.gameObject.scene.IsValid() ||
                        IsToolOrPlayerObject(component.gameObject))
                        continue;
                    state.SequenceObjects.Add(component.gameObject);
                }
            }
        }

        /// <summary>
        /// Removes the objects a sequence spawned after the save and rewinds the drivers that
        /// were idle at the save. Runs before any saved value is written, so nothing the old
        /// cutscene still had scheduled can overwrite the restored state.
        /// </summary>
        void ResetSequences(Savestate state)
        {
            // An empty set is only safe to sweep on when the capture actually ran; a state
            // made before this tracking existed would otherwise match every driver in the map.
            if (_sequenceTypes.Count == 0 || !state.CapturedSequences)
                return;

            Dictionary<int, LevelElementState> saved =
                new Dictionary<int, LevelElementState>(state.LevelElements.Count);
            for (int i = 0; i < state.LevelElements.Count; i++)
            {
                ComponentSnapshot snapshot = state.LevelElements[i].Component;
                if (snapshot != null && snapshot.Component != null)
                    saved[snapshot.Component.GetInstanceID()] = state.LevelElements[i];
            }

            int removed = 0;
            int rewound = 0;
            HashSet<int> destroyed = new HashSet<int>();
            for (int t = 0; t < _sequenceTypes.Count; t++)
            {
                UnityEngine.Object[] found;
                try { found = UnityEngine.Object.FindObjectsOfType(_sequenceTypes[t], true); }
                catch { continue; }
                for (int i = 0; i < found.Length; i++)
                {
                    Component component = found[i] as Component;
                    if (component == null || !component.gameObject.scene.IsValid() ||
                        IsToolOrPlayerObject(component.gameObject))
                        continue;
                    GameObject go = component.gameObject;

                    if (state.SequenceObjects.Contains(go))
                    {
                        LevelElementState element;
                        if (!saved.TryGetValue(component.GetInstanceID(), out element))
                            continue;   // existed at the save, no state kept: leave it alone
                        if (SequenceWasRunning(element))
                            continue;   // the save was taken mid-sequence: let it carry on
                        if (StopSequence(component))
                            rewound++;
                        continue;
                    }

                    // Not there when the state was taken: the cutscene spawned it. Destroy is
                    // deferred, so one clone seen by several driver types is removed once.
                    if (!destroyed.Add(go.GetInstanceID()))
                        continue;
                    UnityEngine.Object.Destroy(go);
                    removed++;
                }
            }

            if (removed > 0 || rewound > 0)
                _plugin.LogInfo("Savestate load removed " + removed +
                                " cutscene object(s) spawned after the save and rewound " + rewound +
                                " sequence(s).");
        }

        /// <summary>
        /// True when the captured fields say the sequence had already been started, so the
        /// save was taken while it ran and the load must not stop it.
        /// </summary>
        static bool SequenceWasRunning(LevelElementState element)
        {
            ComponentSnapshot snapshot = element != null ? element.Component : null;
            if (snapshot == null || snapshot.Fields == null)
                return false;
            for (int i = 0; i < snapshot.Fields.Count; i++)
            {
                FieldState field = snapshot.Fields[i];
                if (field == null || field.Value == null)
                    continue;
                if (field.Value is bool)
                {
                    if (!(bool)field.Value)
                        continue;
                    if (field.Name == "activated" || field.Name == "inProgress" ||
                        field.Name == "running" || field.Name == "playing" ||
                        field.Name == "active")
                        return true;
                }
                else if (field.Value is int && (int)field.Value > 0 &&
                         (field.Name == "currentEvent" || field.Name == "index" || field.Name == "step"))
                {
                    return true;
                }
            }
            return false;
        }

        static bool StopSequence(Component component)
        {
            MonoBehaviour behaviour = component as MonoBehaviour;
            if (behaviour == null)
                return false;
            try
            {
                // The sequence steps in a coroutine and schedules delayed calls; both have to
                // go, or the next step re-applies the cutscene over the restored state.
                behaviour.StopAllCoroutines();
                behaviour.CancelInvoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        bool IsToolOrPlayerObject(GameObject go)
        {
            if (go == null)
                return true;
            if (go.name.StartsWith("ULTRAKILL_TAS", StringComparison.Ordinal))
                return true;
            Transform player = _bridge.PlayerTransform;
            return player != null && go.transform.IsChildOf(player);
        }

        /// <summary>Animator snapshots of the level, keyed by instance id, for the pre-disable rewind.</summary>
        static Dictionary<int, AnimatorState> CollectAnimatorStates(Savestate state)
        {
            Dictionary<int, AnimatorState> map =
                new Dictionary<int, AnimatorState>(state.LevelAnimators.Count + 16);
            AddAnimatorStates(state.LevelAnimators, map);
            for (int i = 0; i < state.LevelElements.Count; i++)
                AddAnimatorStates(state.LevelElements[i].Animators, map);
            return map;
        }

        static void AddAnimatorStates(List<AnimatorState> snapshots, Dictionary<int, AnimatorState> map)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
            {
                AnimatorState snapshot = snapshots[i];
                if (snapshot != null && snapshot.Animator != null)
                    map[snapshot.Animator.GetInstanceID()] = snapshot;
            }
        }
    }
}
