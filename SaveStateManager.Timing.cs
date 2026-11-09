using System;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Timing module.
    ///
    /// Two pieces of state live outside the game's own components and were lost on every
    /// load, which desynchronised undo and branch switches:
    ///
    ///  - The injected input mask. A load releases every button (TasEngine calls
    ///    InputPatcher.ReleaseAll), so a jump or punch that was held when the state was
    ///    taken produced a fresh press edge on the next frame and repeated its one-shot
    ///    action. The mask is stored together with the edge suppression, so a held button
    ///    reads as held instead of pressed.
    ///  - The clock's sub-step phase: the fraction of a frame not yet spent on a native
    ///    0.008 s step. Unity's fixed accumulator decides which tick a physics step lands
    ///    on, so a state loaded in the middle of a framebulk used to run its first steps a
    ///    tick early or late.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        sealed class TimingState
        {
            public bool HasInput;
            public TasButtons HeldButtons;
            public bool HasAccumulator;
            public double PhysicsAccumulator;
            public int LastTickSteps;
            // The game's exact clock at the save. Restored after the hitstop decision so a
            // state taken during slow motion, an assist-speed change or a stop comes back
            // with the same scale instead of the scale of the timeline that was discarded.
            public float TimeScale;
            // Game hitstop: currentStop, the virtual stop timers, pitch and parry flash.
            public TimeControllerHitstopPatch.Snapshot Hitstop;
            public bool HitstopTickPending;
            public double HitstopTickSeconds;
        }

        partial void CaptureTiming(Savestate state)
        {
            TimingState timing = new TimingState();
            InputPatcher injector = _plugin.Injector;
            if (injector != null)
            {
                timing.HasInput = true;
                timing.HeldButtons = injector.HeldButtons;
            }

            TimeController clock = _plugin.Clock;
            if (clock != null)
            {
                timing.HasAccumulator = true;
                timing.PhysicsAccumulator = clock.PhysicsAccumulatorSeconds;
                timing.LastTickSteps = clock.LastTickSteps;
                timing.HitstopTickPending = clock.HitstopTickPending;
                timing.HitstopTickSeconds = clock.HitstopTickSeconds;
            }
            timing.TimeScale = Time.timeScale;
            try { timing.Hitstop = TimeControllerHitstopPatch.Capture(); }
            catch (Exception ex) { _plugin.LogWarning("Hitstop capture skipped: " + ex.Message); }

            SetExt(state, "Timing", timing);
        }

        /// <summary>
        /// Put the saved game hitstop back; runs after the module restores (which write the
        /// game TimeController's fields) and before the hitstop reset. True when the state
        /// was taken inside a hitstop: currentStop and its timers are back, and the caller
        /// must skip ResetHitstop so the stop runs its remaining ticks and then ends through
        /// the game's ContinueTime. Outside a stop the saved Time.timeScale is restored here.
        /// </summary>
        bool RestoreHitstop(Savestate state)
        {
            TimingState timing = GetExt<TimingState>(state, "Timing");
            bool midStop = false;
            try { midStop = TimeControllerHitstopPatch.Restore(timing != null ? timing.Hitstop : null); }
            catch (Exception ex) { _plugin.LogWarning("Hitstop restore skipped: " + ex.Message); }

            // Outside a stop, put the saved game scale back exactly. This is deliberately
            // after the module restores (which write TimeController.timeScale and
            // timeScaleModifier) and after the hitstop patch; a saved hitstop owns scale 0
            // until the game's ContinueTime runs, so it is left alone in that case.
            if (!midStop && timing != null)
            {
                try
                {
                    Time.timeScale = Mathf.Max(0f, timing.TimeScale);
                }
                catch (Exception ex)
                {
                    _plugin.LogWarning("Game time scale restore skipped: " + ex.Message);
                }
            }

            TimeController clock = _plugin.Clock;
            if (clock != null)
            {
                if (midStop && timing != null)
                    clock.RestoreHitstopTick(timing.HitstopTickPending, timing.HitstopTickSeconds);
                else
                    clock.RestoreHitstopTick(false, 0.0);
                // ResetHitstop is skipped on a mid-stop load; the carry still needs the
                // resync it would have done (PostRestore then puts the saved phase back).
                if (midStop)
                    clock.ResyncPhysicsCarry();
            }
            return midStop;
        }

        partial void RestoreTiming(Savestate state, ref int restored, ref int missing)
        {
            TimingState timing = GetExt<TimingState>(state, "Timing");
            if (timing == null)
                return;

            // The phase itself is restored in PostRestore: ResetHitstop runs after the
            // module restores and resyncs the carry from Unity's live remainder, so writing
            // it here would be overwritten one call later (see ApplySavedPhysicsPhase).

            if (timing.HasInput)
            {
                InputPatcher injector = _plugin.Injector;
                if (injector != null)
                {
                    // Held buttons are restored, with their press edge suppressed, so the
                    // one-shot action they started at the save does not run a second time.
                    try { injector.RestoreHeldState(timing.HeldButtons); }
                    catch (Exception ex) { _plugin.LogWarning("Held input restore skipped: " + ex.Message); }
                }
            }

            restored++;
        }

        /// <summary>
        /// The fixed-step phase a state was captured on, read before a restore so the clock can
        /// be aligned to it (see TimeController.QueueFixedStepAlign). False when the state has no
        /// timing module or was taken before the accumulator was captured.
        /// </summary>
        public bool TryGetSavedPhysicsPhase(Savestate state, out double phase)
        {
            phase = 0.0;
            TimingState timing = GetExt<TimingState>(state, "Timing");
            if (timing == null || !timing.HasAccumulator)
                return false;
            phase = timing.PhysicsAccumulator;
            return true;
        }

        /// <summary>
        /// Last step of a load: put the saved sub-step phase back after ResetHitstop has
        /// resynced the clock from the discarded timeline's live remainder.
        /// </summary>
        void ApplySavedPhysicsPhase(Savestate state)
        {
            TimingState timing = GetExt<TimingState>(state, "Timing");
            if (timing == null || !timing.HasAccumulator)
                return;
            TimeController clock = _plugin.Clock;
            if (clock == null)
                return;
            try { clock.RestorePhysicsAccumulator(timing.PhysicsAccumulator); }
            catch (Exception ex) { _plugin.LogWarning("Fixed-step phase restore skipped: " + ex.Message); }
        }
    }
}
