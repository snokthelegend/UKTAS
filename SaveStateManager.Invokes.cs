using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Delayed-call module.
    ///
    /// Every script's pending <c>MonoBehaviour.Invoke(name, delay)</c> calls are already
    /// recorded in its ComponentSnapshot (SaveStateManager.Vitals.cs), but only the full
    /// declared delay was known: a "jump ready" Invoke(0.1) that had 0.03 s left was
    /// re-armed for the whole 0.1 s, and an Invoke whose delay was computed was re-armed
    /// with the 0.25 s default. That shifts every queued action after a load.
    ///
    /// The true remainder is not observable from outside Unity, so this module observes it:
    /// the first capture that sees a given (component, method) pending stamps the time, and
    /// later captures subtract it from the declared delay. Per-tick rolling history makes
    /// the observation accurate to one tick, and a slot save made without history still
    /// degrades to the full delay instead of failing.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        /// <summary>One pending delayed call as it was recorded in the state.</summary>
        sealed class InvokePendingState
        {
            public Component Owner;
            public string Name;
            public float Delay;
            public float Remaining;
            public bool Observed;
        }

        /// <summary>Time.time when a (component instance, method) pair was first seen pending.</summary>
        readonly Dictionary<string, float> _invokeFirstSeen = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>Declared delay of that pair, remembered across captures.</summary>
        readonly Dictionary<string, float> _invokeDeclaredDelay = new Dictionary<string, float>(StringComparer.Ordinal);
        const int MaxInvokeTraceEntries = 4096;

        partial void CaptureInvokes(Savestate state)
        {
            List<ComponentSnapshot> snapshots = CollectInvokeSnapshots(state);
            List<InvokePendingState> saved = new List<InvokePendingState>();
            HashSet<string> live = new HashSet<string>(StringComparer.Ordinal);
            float now = Time.time;

            for (int s = 0; s < snapshots.Count; s++)
            {
                ComponentSnapshot snapshot = snapshots[s];
                if (snapshot == null || snapshot.PendingInvokes == null || snapshot.PendingInvokes.Count == 0)
                    continue;
                Component owner = snapshot.Component;
                if (owner == null)
                    continue;

                int id = owner.GetInstanceID();
                List<InvokeInfo> adjusted = new List<InvokeInfo>(snapshot.PendingInvokes.Count);
                for (int p = 0; p < snapshot.PendingInvokes.Count; p++)
                {
                    InvokeInfo pending = snapshot.PendingInvokes[p];
                    if (pending == null || string.IsNullOrEmpty(pending.Name))
                        continue;
                    string key = id + "|" + pending.Name;
                    live.Add(key);

                    float declared = pending.Delay;
                    if (declared <= 0f)
                        _invokeDeclaredDelay.TryGetValue(key, out declared);
                    else
                        _invokeDeclaredDelay[key] = declared;

                    float firstSeen;
                    bool observed = _invokeFirstSeen.TryGetValue(key, out firstSeen);
                    if (!observed)
                    {
                        firstSeen = now;
                        _invokeFirstSeen[key] = firstSeen;
                    }

                    // Remaining = declared delay - time already spent waiting. A call whose
                    // delay is generated at runtime stays unknown (0) and keeps the caller's
                    // fallback in RestoreInvokes.
                    float remaining = 0f;
                    if (declared > 0f)
                    {
                        float elapsed = Mathf.Max(0f, now - firstSeen);
                        remaining = Mathf.Max(0f, declared - elapsed);
                    }

                    // A call that is due now is re-armed for one frame, not for its whole
                    // original delay; a call with an unknown delay keeps 0 so the caller's
                    // fallback applies.
                    adjusted.Add(new InvokeInfo
                    {
                        Name = pending.Name,
                        Delay = declared > 0f ? Mathf.Max(remaining, 0.008f) : 0f
                    });
                    saved.Add(new InvokePendingState
                    {
                        Owner = owner,
                        Name = pending.Name,
                        Delay = declared,
                        Remaining = remaining,
                        Observed = observed
                    });
                }
                // The shared InvokeInfo instances from InvokeScanner are never mutated; only
                // these per-capture copies carry the shortened delay.
                snapshot.PendingInvokes = adjusted;
            }

            PruneInvokeTrace(live);
            SetExt(state, "Invokes", saved);
        }

        partial void RestoreInvokes(Savestate state, ref int restored, ref int missing)
        {
            List<InvokePendingState> saved = GetExt<List<InvokePendingState>>(state, "Invokes");
            if (saved == null)
                return;
            // Re-arming already happened in RestoreComponent for every snapshot (it uses the
            // per-capture copies above); this only reports what came back.
            for (int i = 0; i < saved.Count; i++)
            {
                InvokePendingState pending = saved[i];
                if (pending == null)
                    continue;
                if (Resolve(pending.Owner) == null)
                {
                    missing++;
                    continue;
                }
                restored++;
            }
        }

        /// <summary>Drop bookkeeping for calls that are no longer pending anywhere.</summary>
        void PruneInvokeTrace(HashSet<string> live)
        {
            if (_invokeFirstSeen.Count > MaxInvokeTraceEntries)
            {
                _invokeFirstSeen.Clear();
                _invokeDeclaredDelay.Clear();
                return;
            }
            if (live.Count == 0 && _invokeFirstSeen.Count == 0)
                return;

            List<string> stale = null;
            foreach (string key in _invokeFirstSeen.Keys)
            {
                if (live.Contains(key))
                    continue;
                if (stale == null)
                    stale = new List<string>();
                stale.Add(key);
            }
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
            {
                _invokeFirstSeen.Remove(stale[i]);
                _invokeDeclaredDelay.Remove(stale[i]);
            }
        }

        /// <summary>
        /// Every component snapshot a state holds. Used by the invoke and coroutine modules
        /// to reach the scripts whose delayed calls and coroutines need re-arming.
        /// </summary>
        static List<ComponentSnapshot> CollectInvokeSnapshots(Savestate state)
        {
            List<ComponentSnapshot> result = new List<ComponentSnapshot>(64);
            if (state == null)
                return result;

            if (state.Player != null)
                AddInvokeSnapshot(result, state.Player.Extra);
            AddInvokeSnapshots(result, state.PlayerParts);
            AddInvokeSnapshots(result, state.ExtraComponents);
            AddInvokeSnapshots(result, state.HudParts);

            if (state.Enemies != null)
            {
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    EnemyState enemy = state.Enemies[i];
                    if (enemy == null || enemy.Lightweight)
                        continue;
                    AddInvokeSnapshots(result, enemy.Behaviours);
                }
            }

            if (state.LevelElements != null)
            {
                for (int i = 0; i < state.LevelElements.Count; i++)
                {
                    LevelElementState element = state.LevelElements[i];
                    if (element != null)
                        AddInvokeSnapshot(result, element.Component);
                }
            }

            if (state.Doors != null)
            {
                for (int i = 0; i < state.Doors.Count; i++)
                {
                    DoorState door = state.Doors[i];
                    if (door == null)
                        continue;
                    AddInvokeSnapshot(result, door.Generic);
                    if (door.Parts == null)
                        continue;
                    for (int p = 0; p < door.Parts.Count; p++)
                    {
                        if (door.Parts[p] != null)
                            AddInvokeSnapshot(result, door.Parts[p].Snapshot);
                    }
                }
            }
            return result;
        }

        static void AddInvokeSnapshots(List<ComponentSnapshot> into, List<ComponentSnapshot> snapshots)
        {
            if (snapshots == null)
                return;
            for (int i = 0; i < snapshots.Count; i++)
                AddInvokeSnapshot(into, snapshots[i]);
        }

        static void AddInvokeSnapshot(List<ComponentSnapshot> into, ComponentSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Component == null)
                return;
            if (!GameBridge.IsAlive(snapshot.Component))
                return;
            into.Add(snapshot);
        }
    }
}
