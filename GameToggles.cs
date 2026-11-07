using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Game-side switches from the TAS window (Startup tab, "Game helpers"):
    ///
    ///  - Auto-show leaderboard: keeps the level stats panel open, the one the game shows while
    ///    Tab is held (LevelStatsEnabler.levelStats). LevelStatsEnabler.keepOpen is the game's
    ///    own "double tap Tab" flag; it is set in memory only, the LevStaOpe pref is not written.
    ///  - Auto-enable cheats: calls CheatsController.ActivateCheats, the method the consent
    ///    popup's "yes" runs (and CheatsEnabler.Start, the sandbox's own auto-enable). It sets
    ///    AssistController.cheatsEnabled (what marks the level as cheated) and
    ///    CheatsController.cheatsEnabled (what the cheat menu reads) and hides the popup. Doing
    ///    that directly works without the up-up-down-down-left-right-B-A input, without clicking
    ///    UI while the TAS owns the mouse, and is skipped when cheats are already on. A popup
    ///    that is open anyway is answered the same way and the pause it opened is closed.
    ///  - Death restart guard: StatsManager.Update calls Restart (checkpoint, or the whole level
    ///    without one) when the player is dead and Fire1 or R is pressed. A script that keeps
    ///    firing reloaded the level the frame after a death. While a TAS is active and "Pause on
    ///    death (kill plane)" is off, that call is skipped. Restart from the pause menu
    ///    (OptionsManager.RestartCheckpoint) is not affected.
    /// </summary>
    public static class GameToggles
    {
        const float SearchInterval = 0.5f;

        static Plugin _plugin;
        static Harmony _harmony;
        static bool _inStatsUpdate;
        static bool _restartBlockLogged;

        // Leaderboard (LevelStatsEnabler)
        static Type _enablerType;
        static FieldInfo _levelStatsField;
        static FieldInfo _keepOpenField;
        static Component _enabler;
        static float _enablerSearchAt = -1f;
        static Component _forcedEnabler;
        static bool _origKeepOpen;
        static bool _origStatsActive;
        static bool _origEnablerActive;

        // Cheats (CheatsController)
        static Type _cheatsType;
        static FieldInfo _cheatsEnabledField;
        static FieldInfo _consentField;
        static MethodInfo _activateCheats;
        static Component _cheats;
        static float _cheatsSearchAt = -1f;
        static bool _cheatsLogged;

        public static void Install(Plugin plugin)
        {
            _plugin = plugin;
            if (_harmony != null)
                return;

            try
            {
                _enablerType = AccessTools.TypeByName("LevelStatsEnabler");
                if (_enablerType != null)
                {
                    _levelStatsField = AccessTools.Field(_enablerType, "levelStats");
                    _keepOpenField = AccessTools.Field(_enablerType, "keepOpen");
                }
                _cheatsType = AccessTools.TypeByName("CheatsController");
                if (_cheatsType != null)
                {
                    _cheatsEnabledField = AccessTools.Field(_cheatsType, "cheatsEnabled");
                    _consentField = AccessTools.Field(_cheatsType, "consentScreen");
                    _activateCheats = AccessTools.Method(_cheatsType, "ActivateCheats", Type.EmptyTypes);
                }
            }
            catch (Exception ex)
            {
                plugin.LogWarning("Game helper lookups failed: " + ex.Message);
            }

            try
            {
                Type stats = AccessTools.TypeByName("StatsManager");
                MethodInfo update = stats != null ? AccessTools.Method(stats, "Update", Type.EmptyTypes) : null;
                MethodInfo restart = stats != null ? AccessTools.Method(stats, "Restart", Type.EmptyTypes) : null;
                if (update == null || restart == null)
                {
                    plugin.LogWarning("Death restart guard skipped: StatsManager.Update/Restart not found.");
                    return;
                }
                _harmony = new Harmony("com.tas.ultrakill.gametoggles");
                BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                _harmony.Patch(update,
                               prefix: new HarmonyMethod(typeof(GameToggles).GetMethod("StatsUpdatePrefix", flags)),
                               finalizer: new HarmonyMethod(typeof(GameToggles).GetMethod("StatsUpdateFinalizer", flags)));
                _harmony.Patch(restart, prefix: new HarmonyMethod(typeof(GameToggles).GetMethod("RestartPrefix", flags)));
            }
            catch (Exception ex)
            {
                plugin.LogWarning("Death restart guard failed: " + ex.Message);
            }
        }

        /// <summary>Once per frame from Plugin.Tick.</summary>
        public static void Update()
        {
            if (_plugin == null)
                return;
            try { UpdateLeaderboard(); }
            catch (Exception ex) { LogOnce(ref _leaderboardErrorLogged, "Auto-show leaderboard failed: " + ex.Message); }
            try { UpdateCheats(); }
            catch (Exception ex) { LogOnce(ref _cheatsErrorLogged, "Auto-enable cheats failed: " + ex.Message); }
        }

        static bool _leaderboardErrorLogged;
        static bool _cheatsErrorLogged;

        static void LogOnce(ref bool logged, string message)
        {
            if (logged)
                return;
            logged = true;
            _plugin.LogWarning(message);
        }

        /// <summary>A live scene component of the type, searched at most twice a second.</summary>
        static Component FindCached(Type type, ref Component cache, ref float searchAt)
        {
            if (cache != null)
                return cache;
            cache = null;
            float now = Time.realtimeSinceStartup;
            if (type == null || now < searchAt)
                return null;
            searchAt = now + SearchInterval;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
            catch { return null; }
            if (found == null)
                return null;
            for (int i = 0; i < found.Length; i++)
            {
                Component component = found[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    cache = component;
                    return cache;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Auto-show leaderboard
        // ------------------------------------------------------------------

        static void UpdateLeaderboard()
        {
            bool want = _plugin.AutoShowLeaderboard != null && _plugin.AutoShowLeaderboard.Value;
            if (!want || _levelStatsField == null || _keepOpenField == null)
            {
                ReleaseLeaderboard();
                return;
            }

            Component enabler = FindCached(_enablerType, ref _enabler, ref _enablerSearchAt);
            if (enabler == null)
                return;
            // Start assigns levelStats (its first child); before that there is nothing to show.
            GameObject stats = _levelStatsField.GetValue(enabler) as GameObject;
            if (stats == null)
                return;

            if (_forcedEnabler != enabler)
            {
                ReleaseLeaderboard();
                _forcedEnabler = enabler;
                _origKeepOpen = (bool)_keepOpenField.GetValue(enabler);
                _origStatsActive = stats.activeSelf;
                _origEnablerActive = enabler.gameObject.activeSelf;
            }

            // The enabler switches itself off in Start on a level that has no rank yet (the game
            // only offers the panel when replaying); the setting shows it there too.
            if (!enabler.gameObject.activeSelf)
                enabler.gameObject.SetActive(true);
            // keepOpen: the game's Update then only closes the panel on a Tab press, and the
            // next frame opens it again.
            if (!(bool)_keepOpenField.GetValue(enabler))
                _keepOpenField.SetValue(enabler, true);
            if (!stats.activeSelf)
                stats.SetActive(true);
        }

        /// <summary>Hand the panel back in the state it was in before the setting took it over.</summary>
        static void ReleaseLeaderboard()
        {
            Component enabler = _forcedEnabler;
            _forcedEnabler = null;
            if (enabler == null || _levelStatsField == null || _keepOpenField == null)
                return;
            try
            {
                _keepOpenField.SetValue(enabler, _origKeepOpen);
                GameObject stats = _levelStatsField.GetValue(enabler) as GameObject;
                if (stats != null && stats.activeSelf != _origStatsActive)
                    stats.SetActive(_origStatsActive);
                if (enabler.gameObject.activeSelf != _origEnablerActive)
                    enabler.gameObject.SetActive(_origEnablerActive);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Auto-enable cheats
        // ------------------------------------------------------------------

        static void UpdateCheats()
        {
            if (_plugin.AutoEnableCheats == null || !_plugin.AutoEnableCheats.Value)
                return;
            if (_cheatsEnabledField == null || _activateCheats == null)
                return;
            Component cheats = FindCached(_cheatsType, ref _cheats, ref _cheatsSearchAt);
            if (cheats == null)
                return;

            GameObject consent = _consentField != null ? _consentField.GetValue(cheats) as GameObject : null;
            bool consentOpen = consent != null && consent.activeSelf;
            bool enabled = (bool)_cheatsEnabledField.GetValue(cheats);
            if (enabled && !consentOpen)
                return;

            // The consent popup's "yes": idempotent, so a state that already has cheats on is
            // left as it is apart from closing a popup that is somehow open.
            _activateCheats.Invoke(cheats, null);

            // The cheat code paused the game when it opened the popup (OptionsManager.Pause).
            if (consentOpen)
                UnpauseGame();

            if (!_cheatsLogged)
            {
                _cheatsLogged = true;
                _plugin.LogInfo("Auto-enable cheats: cheats switched on (CheatsController.ActivateCheats).");
            }
        }

        static void UnpauseGame()
        {
            object options = _plugin.Bridge != null ? _plugin.Bridge.OptionsManager : null;
            if (options == null)
                return;
            FieldInfo paused = AccessTools.Field(options.GetType(), "paused");
            MethodInfo unpause = AccessTools.Method(options.GetType(), "UnPause", Type.EmptyTypes);
            if (paused == null || unpause == null || !(bool)paused.GetValue(options))
                return;
            unpause.Invoke(options, null);
        }

        // ------------------------------------------------------------------
        // Death restart guard
        // ------------------------------------------------------------------

        static void StatsUpdatePrefix()
        {
            _inStatsUpdate = true;
        }

        static Exception StatsUpdateFinalizer(Exception __exception)
        {
            _inStatsUpdate = false;
            return __exception;
        }

        /// <summary>True when a death must not reload the level by itself.</summary>
        public static bool BlockDeathRestart
        {
            get
            {
                Plugin plugin = _plugin;
                if (plugin == null || plugin.PauseOnDeathKillPlane == null || plugin.PauseOnDeathKillPlane.Value)
                    return false;
                TasEngine engine = plugin.Engine;
                return engine != null && engine.IsActive;
            }
        }

        static bool RestartPrefix()
        {
            // Only the restart StatsManager.Update starts for a dead player; every other
            // caller (pause menu, console) is a deliberate choice and runs.
            if (!_inStatsUpdate || !BlockDeathRestart)
            {
                _restartBlockLogged = false;
                return true;
            }
            if (!_restartBlockLogged)
            {
                _restartBlockLogged = true;
                _plugin.LogInfo("Death restart skipped: the TAS keeps the dead state. Load a savestate, rewind (F4), " +
                                "reload the script or use Restart Checkpoint in the pause menu.");
            }
            return false;
        }
    }
}
