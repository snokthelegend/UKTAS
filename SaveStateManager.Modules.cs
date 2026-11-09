using System;

namespace UltrakillTAS
{
    /// <summary>
    /// Savestate module hooks.
    ///
    /// Contract for a module area X (one of Timing, Invokes, Coroutines, Enemies2, Bosses,
    /// PlayerExtra, Weapons, Style, World2, Physics, Audio, Misc):
    ///
    ///  - All code of the area lives in its own partial file SaveStateManager.X.cs and is
    ///    self-contained: it implements <c>partial void CaptureX(Savestate state)</c> and/or
    ///    <c>partial void RestoreX(Savestate state, ref int restored, ref int missing)</c>, plus any
    ///    private helpers/types it needs (named so they cannot clash with another area's).
    ///    An area that is not implemented compiles away.
    ///  - Its state is stored in <c>state.Ext["X"]</c> (use SetExt / GetExt below), never in new
    ///    Savestate fields, so areas never touch the shared files. A collection stored there is
    ///    counted element-wise in the slot summary (Savestate.ExtItemCount).
    ///  - CaptureX runs at the end of Capture, after every core area and before the RNG streams
    ///    are captured. RestoreX runs after every core restore (player, enemies, doors,
    ///    projectiles, systems, loaded scenes, camera, player effects) and before the hitstop
    ///    reset and the RNG restore, so it may draw on nothing random.
    ///  - PreRestore runs first in Restore (after the scene check and the time deltas are set),
    ///    before anything is written: stop coroutines and pending Invokes there.
    ///    PostRestore runs last, after the RNG streams: fix-ups such as Animator.Update(0) or
    ///    re-arming a coroutine against the restored values. Both get the state; neither counts.
    ///  - Every hook call is wrapped in its own try/catch here; an exception is logged (capture:
    ///    Savestate.Warnings + log, restore: log) and never aborts the save or the load. Inside a
    ///    hook, guard each object individually as well, so one broken object cannot lose the rest.
    ///  - A restore must tolerate state from a capture that predates the module (no entry in
    ///    Ext: do nothing) and objects destroyed since the capture (skip them, optionally
    ///    missing++).
    ///  - Values are written through the core helpers where possible (CaptureFields/ApplyFields,
    ///    CaptureComponent/RestoreComponent, CaptureReferences/ApplyReferences), which already
    ///    rebase TimeSince and deep-copy collections/plain objects.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        partial void CaptureTiming(Savestate state);
        partial void RestoreTiming(Savestate state, ref int restored, ref int missing);
        partial void CaptureInvokes(Savestate state);
        partial void RestoreInvokes(Savestate state, ref int restored, ref int missing);
        partial void CaptureCoroutines(Savestate state);
        partial void RestoreCoroutines(Savestate state, ref int restored, ref int missing);
        partial void CaptureEnemies2(Savestate state);
        partial void RestoreEnemies2(Savestate state, ref int restored, ref int missing);
        partial void CaptureBosses(Savestate state);
        partial void RestoreBosses(Savestate state, ref int restored, ref int missing);
        partial void CapturePlayerExtra(Savestate state);
        partial void RestorePlayerExtra(Savestate state, ref int restored, ref int missing);
        partial void CaptureWeapons(Savestate state);
        partial void RestoreWeapons(Savestate state, ref int restored, ref int missing);
        partial void CaptureStyle(Savestate state);
        partial void RestoreStyle(Savestate state, ref int restored, ref int missing);
        partial void CaptureWorld2(Savestate state);
        partial void RestoreWorld2(Savestate state, ref int restored, ref int missing);
        partial void CapturePhysics(Savestate state);
        partial void RestorePhysics(Savestate state, ref int restored, ref int missing);
        partial void CaptureAudio(Savestate state);
        partial void RestoreAudio(Savestate state, ref int restored, ref int missing);
        partial void CaptureMisc(Savestate state);
        partial void RestoreMisc(Savestate state, ref int restored, ref int missing);
        partial void CaptureCompleteness(Savestate state);
        partial void RestoreCompleteness(Savestate state, ref int restored, ref int missing);

        /// <summary>First step of Restore: stop coroutines/Invokes before any value is written.</summary>
        partial void PreRestore(Savestate state);

        /// <summary>Last step of Restore, after the RNG streams: animator updates and other fix-ups.</summary>
        partial void PostRestore(Savestate state);

        /// <summary>The state an area stored at capture, or null (absent or of another type).</summary>
        static T GetExt<T>(Savestate state, string area) where T : class
        {
            object value;
            if (state == null || state.Ext == null || !state.Ext.TryGetValue(area, out value))
                return null;
            return value as T;
        }

        static void SetExt(Savestate state, string area, object value)
        {
            if (state == null)
                return;
            if (state.Ext == null)
                state.Ext = new System.Collections.Generic.Dictionary<string, object>();
            state.Ext[area] = value;
        }

        void CaptureModules(Savestate state)
        {
            try { CaptureTiming(state); }
            catch (Exception ex) { AddWarning(state, "timing", ex); }
            try { CaptureInvokes(state); }
            catch (Exception ex) { AddWarning(state, "invokes", ex); }
            try { CaptureCoroutines(state); }
            catch (Exception ex) { AddWarning(state, "coroutines", ex); }
            try { CaptureEnemies2(state); }
            catch (Exception ex) { AddWarning(state, "enemies (extra)", ex); }
            try { CaptureBosses(state); }
            catch (Exception ex) { AddWarning(state, "bosses", ex); }
            try { CapturePlayerExtra(state); }
            catch (Exception ex) { AddWarning(state, "player (extra)", ex); }
            try { CaptureWeapons(state); }
            catch (Exception ex) { AddWarning(state, "weapons", ex); }
            try { CaptureStyle(state); }
            catch (Exception ex) { AddWarning(state, "style", ex); }
            try { CaptureWorld2(state); }
            catch (Exception ex) { AddWarning(state, "world (extra)", ex); }
            try { CapturePhysics(state); }
            catch (Exception ex) { AddWarning(state, "physics", ex); }
            try { CaptureAudio(state); }
            catch (Exception ex) { AddWarning(state, "audio", ex); }
            try { CaptureMisc(state); }
            catch (Exception ex) { AddWarning(state, "misc", ex); }
            try { CaptureCompleteness(state); }
            catch (Exception ex) { AddWarning(state, "completeness", ex); }
        }

        void RestoreModules(Savestate state, ref int restored, ref int missing)
        {
            try { RestoreTiming(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate timing restore skipped: " + ex.Message); }
            try { RestoreInvokes(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate invoke restore skipped: " + ex.Message); }
            try { RestoreCoroutines(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate coroutine restore skipped: " + ex.Message); }
            try { RestoreEnemies2(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate enemy (extra) restore skipped: " + ex.Message); }
            try { RestoreBosses(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate boss restore skipped: " + ex.Message); }
            try { RestorePlayerExtra(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate player (extra) restore skipped: " + ex.Message); }
            try { RestoreWeapons(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate weapon restore skipped: " + ex.Message); }
            try { RestoreStyle(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate style restore skipped: " + ex.Message); }
            try { RestoreWorld2(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate world (extra) restore skipped: " + ex.Message); }
            try { RestorePhysics(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate physics restore skipped: " + ex.Message); }
            try { RestoreAudio(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate audio restore skipped: " + ex.Message); }
            try { RestoreMisc(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate misc restore skipped: " + ex.Message); }
            try { RestoreCompleteness(state, ref restored, ref missing); }
            catch (Exception ex) { _plugin.LogWarning("Savestate completeness restore skipped: " + ex.Message); }
        }

        void RunPreRestore(Savestate state)
        {
            try { PreRestore(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate pre-restore step skipped: " + ex.Message); }
        }

        void RunPostRestore(Savestate state)
        {
            try { PostRestore(state); }
            catch (Exception ex) { _plugin.LogWarning("Savestate post-restore step skipped: " + ex.Message); }
        }
    }
}
