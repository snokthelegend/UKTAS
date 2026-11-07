using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// IMGUI front end: transport, live script inspector/editor, keybinds,
    /// overlays and diagnostics. Rendered through OnGUI so it works while the
    /// game's Time.timeScale is 0 or the TAS clock is paused / frame stepping.
    /// </summary>
    public sealed partial class GuiManager
    {
        readonly Plugin _plugin;
        readonly TasEngine _engine;
        readonly TimeController _time;
        readonly SaveStateManager _states;
        readonly GameBridge _bridge;
        readonly InputPatcher _input;
        readonly RngController _rng;

        const float WindowWidth = 900f;
        const float WindowHeight = 680f;
        Rect _windowRect = new Rect(40, 40, WindowWidth, WindowHeight);
        int _tab;
        bool _visible = true;
        bool _loggedDraw;
        bool _loggedHudState;
        bool _loggedHudDraw;

        string _pathText;
        string _statusText = string.Empty;
        bool _statusIsError;
        string _loadoutText = string.Empty;

        string _frametimeText;
        string _seedText;
        string _authorText;
        string _descriptionText;

        string _autofuncsText = TasInputColumns.NeutralAutofuncs;
        string _movementText = TasInputColumns.NeutralMovement;
        string _actionsText = TasInputColumns.NeutralActions;
        string _ftOverrideText = string.Empty;
        string _pitchText = "0.0";
        string _yawText = "90.0";
        string _framesText = "1";
        bool _liveEdit;
        string _commandsText = string.Empty;
        string _pauseAtTickText = "0";
        string _noRefreshTickText = "0";
        bool _autoSync = true;
        int _lastSyncedTick = -1;

        ConfigEntry<KeyboardShortcut> _rebindEntry;
        ConfigEntry<KeyCode> _rebindKeyEntry;
        string _rebindLabel = string.Empty;

        string[] _files = new string[0];
        /// <summary>Scripts in the script folder's examples subfolder (listed as their own group).</summary>
        string[] _exampleFiles = new string[0];
        int _filesFrame = -1000;
        Vector2 _fileScroll;
        Vector2 _lineScroll;
        Vector2 _diagnosticsScroll;
        string _diagnostics = "Press Refresh to build a report.";
        bool _diagnosticsRequested;
        bool _diagnosticsLogRequested;
        int _cacheFrame = -1000;
        int _cachedEnemyCount;
        int _enemyCountFrame = -1000;
        string _cachedModeTag = "[IDLE]";
        string _cachedPointerText = string.Empty;
        string _cachedWindowStatus = "status initializing...";
        string _cachedProgress = string.Empty;
        string _cachedMode = string.Empty;
        string _cachedStatusDetail = string.Empty;
        string _cachedClock = string.Empty;
        string _cachedInputDescription = string.Empty;
        string _cachedRngDescription = string.Empty;
        readonly List<string> _cachedHudLines = new List<string>(16);
        string _cachedHudError = string.Empty;

        static readonly string[] TabLabels =
        {
            "General", "Script Editor", "Frame Editor", "Startup", "Keybinds", "Overlays", "Branches", "Recording", "Diagnostics", "Optimizer"
        };

        // Branches and Recording tab state.
        string _projectPathText = string.Empty;
        string _projectAuthorText = string.Empty;
        string _projectLevelText = string.Empty;
        string _forkNameText = "Alt route";
        string _branchStatus = string.Empty;
        Vector2 _branchScroll;
        string _recordDirText;
        string _recordFileText = "run.mp4";
        string _ffmpegText;
        int _recordFpsIndex = 1;
        int _recordSampleIndex = 2;
        float _recordShutter = 0.5f;

        /// <summary>The eight columns, in canonical order, for the editor header.</summary>
        const string ColumnHeader =
            "autofuncs  |movem |action|frametime |yaw   |pitch |frames|commands";

        string _toastText = string.Empty;
        bool _toastError;
        float _toastUntil;

        GUIStyle _hudStyle;
        GUIStyle _hudTitleStyle;
        GUIStyle _noRefreshStyle;
        GUIStyle _errorStyle;
        GUIStyle _smallStyle;
        GUIStyle _wrapStyle;
        GUIStyle _matrixStyle;
        GUISkin _styleSkin;

        public bool Visible
        {
            get { return _visible; }
            set { _visible = value; }
        }

        public bool IsRebinding { get { return _rebindEntry != null || _rebindKeyEntry != null; } }
        public bool IsTextInputFocused { get { return GUIUtility.keyboardControl != 0; } }

        /// <summary>
        /// True while something in the window can use an Escape press (shortcuts popup, Find / Go to
        /// bar, a rebind, the script editor or a text field): that Escape must not also pause the game.
        /// </summary>
        public bool OwnsEscape
        {
            get { return _visible && (_shortcutsOpen || _prompt != PromptNone || IsRebinding || IsTextInputFocused); }
        }

        /// <summary>
        /// The single "GUI captures input" decision: the window is up and the switch is on. While the
        /// menu is open the pointer belongs to it, so no real keyboard or mouse input reaches the game
        /// and moving the mouse cannot turn the view. False when the window is hidden or not drawn.
        /// </summary>
        public bool CapturesInput
        {
            get
            {
                if (!_visible || _plugin.CleanRecordingActive || !_plugin.BlockGameInputWhileGuiOpen.Value)
                    return false;
                // NoRefresh races to its breakpoint with the 3D screen off (the open window is exempt).
                if (_engine != null && _engine.RenderSuppressed)
                    return false;
                return true;
            }
        }

        public GuiManager(Plugin plugin, TasEngine engine, TimeController time,
                          SaveStateManager states, GameBridge bridge, InputPatcher input, RngController rng)
        {
            _plugin = plugin;
            _engine = engine;
            _time = time;
            _states = states;
            _bridge = bridge;
            _input = input;
            _rng = rng;
            _visible = plugin.GuiVisibleOnStart.Value;
            _pathText = string.IsNullOrEmpty(plugin.LastScriptFile.Value)
                ? Path.Combine(plugin.ScriptDirectory, "run.uktas")
                : plugin.LastScriptFile.Value;
            _frametimeText = plugin.DefaultHostFrametime.Value.ToString("0.######", CultureInfo.InvariantCulture);
            _seedText = plugin.DefaultSeed.Value.ToString(CultureInfo.InvariantCulture);
            SyncEditor(true);
            _engine.ConfirmRecordingOverwrite = AskRecordingOverwrite;
        }

        /// <summary>Remember the editor path so the field is where it was left.</summary>
        void RememberScriptPath()
        {
            if (string.IsNullOrEmpty(_pathText))
                return;
            if (_plugin.LastScriptFile.Value == _pathText)
                return;
            _plugin.LastScriptFile.Value = _pathText;
            _plugin.ConfigFile.Save();
        }

        public void ShowToast(string text, bool error)
        {
            _toastText = text;
            _toastError = error;
            _toastUntil = TimeController.RealTime + 4.5f;
        }

        /// <summary>
        /// Refresh expensive labels, scene counts and the HUD outside OnGUI.
        /// IMGUI can run several layout/repaint passes per frame; doing scene
        /// searches there causes visible stalls.
        /// </summary>
        public void UpdateCache()
        {
            if (ReferenceEquals(_plugin, null) || _time == null || _engine == null)
                return;
            // SimulationFreeze.FrameCount keeps moving on fully frozen (paused) frames,
            // where Time.frameCount can stand still.
            int frame = SimulationFreeze.FrameCount;
            if (frame - _cacheFrame < 10)
                return;
            _cacheFrame = frame;

            // NoRefresh races to its breakpoint: the file list and the scene-wide enemy count
            // are display-only, so they are left on their last values until rendering returns.
            bool noRefresh = _engine.RenderSuppressed;
            if (!noRefresh)
                RefreshFileList();

            // EnemyCount is a scene-wide FindObjectsOfType; it moves slowly and is only
            // shown on screen, so refresh it every ~second and only while it is visible.
            if (!noRefresh && frame - _enemyCountFrame >= 60 && (_visible || _plugin.ShowHud.Value))
            {
                _enemyCountFrame = frame;
                try
                {
                    _cachedEnemyCount = _bridge != null ? _bridge.EnemyCount : 0;
                }
                catch
                {
                    _cachedEnemyCount = 0;
                }
            }

            _cachedModeTag = BuildModeTag();
            Vector2 pointer = _input != null ? _input.LiveMousePosition : Vector2.zero;
            // Top-left origin, the same convention the mouse / mousef script commands use.
            int px = (int)pointer.x;
            int py = (int)(Screen.height - pointer.y);
            _cachedPointerText = px.ToString() + "," + py.ToString() + "  (" +
                                 (px / (float)Mathf.Max(1, Screen.width)).ToString("0.000", CultureInfo.InvariantCulture) + " " +
                                 (py / (float)Mathf.Max(1, Screen.height)).ToString("0.000", CultureInfo.InvariantCulture) + ")" +
                                 (_input != null && _input.Pointer.Active ? " [TAS]" : string.Empty);

            _cachedWindowStatus = _time.StatusLine() + "   |   " + _engine.Mode +
                                  "   |   " + (_time.Paused ? "PAUSED" : "RUNNING") +
                                  "   |   tick " + _engine.CurrentTick + "/" + _engine.TotalTicks +
                                  "   |   enemies " + _cachedEnemyCount +
                                  "   |   seed " + _rng.CurrentSeed;

            _cachedProgress = "tick " + _engine.CurrentTick + " / " + _engine.TotalTicks +
                              "   time " + _engine.CurrentTime.ToString("0.000") + "s / " +
                              _engine.TotalTime.ToString("0.000") + "s" +
                              "   tpf " + (_engine.LastFrametime > 0 ? _engine.LastFrametime.ToString("0.######") : "-") +
                              "   render " + _time.MeasuredFps.ToString("0") + " fps";
            _cachedMode = "mode " + _engine.Mode + "   finished " + _engine.Finished +
                          "   steps queued " + _time.PendingSteps +
                          "   timeScale " + Time.timeScale.ToString("0.###") + " (game-owned, read only)";
            _cachedStatusDetail = "status " + (_time.Paused ? "PAUSED" : "RUNNING") +
                                  "   timer " + _engine.TimerText +
                                  "   enemies " + _cachedEnemyCount +
                                  (_engine.StartupPending ? "   STARTUP WAIT" : string.Empty);
            _cachedClock = "native physics step: " + _time.NativeFixedStep.ToString("0.########") + "s" +
                           "   steps/tick: " + _time.StepsPerTick.ToString("0.###") +
                           "   (fixedDeltaTime stays " + Time.fixedDeltaTime.ToString("0.########") + ")";

            _cachedInputDescription = _input != null ? _input.Describe() : string.Empty;
            _cachedRngDescription = _rng != null ? _rng.Describe() : string.Empty;
            BuildHudCache();
            using (_plugin.ViewPose())
            {
                RefreshTriggerOverlay();
                RefreshHitboxOverlay();
            }

            if (_diagnosticsRequested)
                BuildDiagnostics();
        }

        string BuildModeTag()
        {
            switch (_engine.Mode)
            {
                case EngineMode.Recording:
                    return "[RECORDING]";
                case EngineMode.Playing:
                    return _engine.WaitingForControl ? "[PLAYBACK - WAITING FOR CONTROL]" : "[PLAYBACK]";
                case EngineMode.Starting:
                    return "[STARTING]";
                case EngineMode.Standalone:
                    return "[STANDALONE]";
                case EngineMode.Paused:
                {
                    string tag = _engine.FreeLookActive ? "[PAUSED - FREE LOOK]" : "[PAUSED]";
                    return _plugin.StepRecord.Value ? tag + " [STEP RECORD]" : tag;
                }
                default:
                    return "[IDLE]";
            }
        }

        void RequestDiagnostics(bool log)
        {
            _diagnosticsRequested = true;
            _diagnosticsLogRequested = log;
            _statusText = "Collecting diagnostics outside OnGUI...";
            _statusIsError = false;
        }

        void BuildDiagnostics()
        {
            _diagnostics = _bridge.BuildReport() +
                           "\n\n" + _time.StatusLine() +
                           "\n" + _cachedInputDescription +
                           "\n" + _cachedRngDescription +
                           "\nScript: " + (_engine.Script == null ? "none" : _engine.TotalTicks + " ticks, " + _engine.Script.Lines.Count + " lines") +
                           "\nFrametime: " + _time.HostFrametime.ToString("0.########") +
                           "\nEnemies: " + _cachedEnemyCount +
                           "\nKill plane Y: " + _plugin.KillPlaneY.Value.ToString("0.##") +
                           "   player Y: " + _bridge.PlayerY.ToString("0.##") +
                           (_bridge.PlayerBelowKillPlane ? "  OUT OF BOUNDS" : string.Empty) +
                           "\nPlayer control: " + _bridge.PlayerControlActivated +
                           "\nStartup mode: " + _plugin.PlayStartMode.Value +
                           "\nStartup pending: " + _engine.StartupPending +
                           "\nUnity captureDeltaTime: " + Time.captureDeltaTime.ToString("0.########") +
                           "\nFixed deltaTime: " + Time.fixedDeltaTime.ToString("0.########") +
                           "\nBepInEx config: " + BepInEx.Paths.ConfigPath;
            if (_diagnosticsLogRequested)
            {
                _plugin.LogInfo(_diagnostics);
                _diagnosticsLogRequested = false;
            }
            _diagnosticsRequested = false;
            _statusText = "Diagnostics refreshed.";
            _statusIsError = false;
        }

        // ------------------------------------------------------------------
        // Root drawing
        // ------------------------------------------------------------------

        public void OnGUI()
        {
            PushOptimizerOverlay();
            // While NoRefresh races to its breakpoint nothing is drawn, so the viewmodel scan
            // and every world-space overlay are skipped: they would only cost frame time.
            bool noRefresh = _engine != null && _engine.RenderSuppressed;

            // Apply late in the frame, after weapon/arm animation has updated, and on
            // every event so a renderer cannot flash for a frame between hides.
            if (!noRefresh)
                ApplyViewmodelVisibility();
            EnsureStyles();
            HandleRebind();

            // A clean recording captures the screen, so the window stays hidden until it ends.
            // NoRefresh is bxt_norefresh: nothing is drawn, the TAS window included (its
            // layout pass alone costs milliseconds per frame). It comes back at the breakpoint.
            if (_visible && !_plugin.CleanRecordingActive && !noRefresh)
            {
                if (!_loggedDraw)
                {
                    _loggedDraw = true;
                    _plugin.LogInfo("TAS window drawing at " + _windowRect.x + "," + _windowRect.y + ".");
                }
                EnsureTheme();
                float windowHeight = Mathf.Min(WindowHeight, Mathf.Max(420f, Screen.height - 24f));
                float windowWidth = Mathf.Min(WindowWidth, Mathf.Max(640f, Screen.width - 24f));
                GUISkin previousSkin = GUI.skin;
                GUI.skin = _skin;
                try
                {
                    _windowRect = GUILayout.Window(0x554B5441, _windowRect, DrawWindow,
                        "ULTRAKILL TAS Tool  v" + Plugin.PluginVersion + "   [Insert: hide]",
                        GUILayout.Width(windowWidth), GUILayout.Height(windowHeight));
                }
                finally
                {
                    GUI.skin = previousSkin;
                }

                // A click outside the window gives up the keyboard focus, so a field that was typed
                // in earlier no longer keeps the game's input captured once the player goes back to it.
                Event outside = Event.current;
                if (outside.type == EventType.MouseDown && !_windowRect.Contains(outside.mousePosition))
                    GUIUtility.keyboardControl = 0;
            }

            if (!_loggedHudState)
            {
                _loggedHudState = true;
                _plugin.LogInfo("HUD visible at startup: " + _plugin.ShowHud.Value + " (F12 toggles).");
            }

            // World-space overlay first so the HUD panel draws over it. None of it is drawn
            // during NoRefresh: hitboxes, triggers, speedometer and toasts are all skipped,
            // leaving only the HUD panel and the NoRefresh banner.
            if (!noRefresh)
            {
                using (_plugin.ViewPose())
                {
                    DrawTriggerOverlay();
                    DrawHitboxOverlay();
                }
            }

            // The HUD panel is not drawn during NoRefresh either; the banner below is the only
            // feedback while the screen is frozen.
            if (_plugin.ShowHud.Value && !noRefresh)
                DrawHud();

            // Above everything else: while the 3D screen is dark this is the only feedback
            // that the simulation is still moving (and that it can be aborted).
            DrawNoRefreshBanner();

            if (!noRefresh)
            {
                DrawSpeedometer();
                DrawPausedLabel();
                DrawToast();
            }
        }

        void EnsureStyles()
        {
            if (GUI.skin == _styleSkin && _hudStyle != null)
                return;
            _styleSkin = GUI.skin;

            _hudStyle = new GUIStyle(GUI.skin.label);
            _hudStyle.fontSize = 12;
            _hudStyle.wordWrap = false;
            _hudStyle.richText = true;

            _hudTitleStyle = new GUIStyle(_hudStyle);
            _hudTitleStyle.fontStyle = FontStyle.Bold;
            _hudTitleStyle.fontSize = 13;

            _errorStyle = new GUIStyle(GUI.skin.label);
            _errorStyle.normal.textColor = new Color(1f, 0.45f, 0.45f);
            _errorStyle.wordWrap = true;

            _smallStyle = new GUIStyle(GUI.skin.label);
            _smallStyle.fontSize = 10;
            _smallStyle.wordWrap = true;

            _wrapStyle = new GUIStyle(GUI.skin.label);
            _wrapStyle.wordWrap = true;

            _matrixStyle = new GUIStyle(GUI.skin.box);
            _matrixStyle.fontSize = 9;
            _matrixStyle.alignment = TextAnchor.MiddleCenter;
            _matrixStyle.padding = new RectOffset(0, 0, 0, 0);
            _matrixStyle.normal.textColor = Color.white;
        }

        const int TabGeneral = 0;
        const int TabScriptEditor = 1;
        const int TabFrameEditor = 2;
        const int TabStartup = 3;
        const int TabKeybinds = 4;
        const int TabOverlays = 5;
        const int TabBranches = 6;
        const int TabRecording = 7;
        const int TabDiagnostics = 8;
        const int TabOptimizer = 9;

        static readonly string[] TabTips =
        {
            "Transport, clock, startup behaviour and the script file: everything needed to play a run.",
            "Write and edit .uktas scripts as plain text with colours and Notepad++ style shortcuts.",
            "Edit the framebulk lines around the current tick, one input column at a time.",
            "What happens when you press Play: which level to load, weapons, autoload.",
            "Rebind the hotkeys used by the tool.",
            "HUD panel, speedometer, hitboxes, freecam and the PAUSED label.",
            "Keep several routes in one .uktasproj file and switch between them.",
            "Render the run to a video with motion blur (needs ffmpeg).",
            "Engine report for bug reports: what the tool sees in the game right now.",
            "Brute-force search for a better framebulk segment between two ticks."
        };

        readonly Vector2[] _tabScroll = new Vector2[10];

        void DrawWindow(int id)
        {
            // Window callbacks can run after OnGUI has put the previous skin back, so the themed
            // skin is set again here and restored on the way out.
            GUISkin outerSkin = GUI.skin;
            if (_skin != null)
                GUI.skin = _skin;
            try
            {
                DrawWindowContents();
            }
            finally
            {
                GUI.skin = outerSkin;
            }
        }

        void DrawWindowContents()
        {
            Event current = Event.current;
            _tipWindowMouse = current.mousePosition;
            if (current.type == EventType.Repaint)
                _tipText = null;

            // While the shortcuts pop-up is open everything under it is switched off, so no click,
            // key or scroll reaches the tabs or the text editor. (A disabled GUI hides input events.)
            bool enabledBefore = GUI.enabled;
            if (_shortcutsOpen)
                GUI.enabled = false;

            DrawToolbar();
            if (current.type == EventType.Repaint)
            {
                Rect bar = GUILayoutUtility.GetLastRect();
                _tipClip = new Rect(0f, bar.yMax, _windowRect.width, Mathf.Max(0f, _windowRect.height - bar.yMax - 30f));
            }

            if (_confirmAction != null)
            {
                DrawConfirm();
            }
            else
            {
                if (_tab != TabScriptEditor)
                    _editorTabWasActive = false;
                // Every tab scrolls, except the text editor, which fills the window and scrolls itself.
                bool scrolls = _tab != TabScriptEditor;
                if (scrolls)
                {
                    _tabScroll[_tab] = GUILayout.BeginScrollView(_tabScroll[_tab], false, false,
                        GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
                    _tipClipActive = true;
                }

                switch (_tab)
                {
                    case TabGeneral: DrawGeneral(); break;
                    case TabScriptEditor: DrawTextEditorTab(); break;
                    case TabFrameEditor: DrawFrameEditorTab(); break;
                    case TabStartup: DrawStartup(); break;
                    case TabKeybinds: DrawKeybinds(); break;
                    case TabOverlays:
                        DrawOverlays();
                        DrawTrajectoryOptions();
                        DrawCoinPathOptions();
                        break;
                    case TabBranches: DrawBranches(); break;
                    case TabRecording: DrawRecording(); break;
                    case TabOptimizer: DrawOptimizerTab(); break;
                    default: DrawDiagnostics(); break;
                }

                if (scrolls)
                {
                    GUILayout.EndScrollView();
                    _tipClipActive = false;
                }
            }

            GUILayout.Label(_cachedWindowStatus, _statusBarStyle);

            GUI.enabled = enabledBefore;
            if (_shortcutsOpen)
            {
                _tipText = null;
                // Drawn last, so it sits over the whole window, and before DragWindow, so a click on
                // the veil is used up here and cannot start dragging the window.
                DrawShortcutsPopup();
            }
            DrawTooltip();
            GUI.DragWindow(new Rect(0, 0, 10000, 34));
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabLabels.Length; i++)
            {
                string tabLabel = i == TabScriptEditor && _editor.Buffer.Modified ? TabLabels[i] + " *" : TabLabels[i];
                if (GUILayout.Button(tabLabel, i == _tab ? _tabActiveStyle : _tabStyle))
                    _tab = i;
                Tip(TabTips[i]);
            }
            GUILayout.FlexibleSpace();
            if (Btn("Save config", "Write every plugin setting (keybinds, overlays, startup) to the BepInEx config file now.", GUILayout.Width(96)))
                _plugin.ConfigFile.Save();
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }

        /// <summary>A row of buttons where exactly one is lit. Returns the picked index.</summary>
        int ChoiceRow(int selected, string[] labels, string[] tips, float width)
        {
            int picked = selected;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < labels.Length; i++)
            {
                if (BtnChoice(i == selected, labels[i], tips != null && i < tips.Length ? tips[i] : null,
                        width > 0f ? GUILayout.Width(width) : GUILayout.ExpandWidth(true)))
                    picked = i;
            }
            GUILayout.EndHorizontal();
            return picked;
        }

        // ------------------------------------------------------------------
        // General
        // ------------------------------------------------------------------

        static readonly double[] PlaybackSpeeds = { 0.1, 0.25, 0.5, 1.0, 2.0, 4.0 };
        static readonly string[] StartLabels = { "Script header", "Current state", "Restart level", "Load level", "Load savestate", "Main menu" };
        static readonly string[] StartValues = { "script", "current", "restart", "level", "state", "menu" };
        static readonly string[] StartTips =
        {
            "Use the start_type written in the script's header.",
            "Start from wherever the game is right now.",
            "Restart the current mission before frame 1.",
            "Load the level named below before frame 1.",
            "Restore a savestate slot before frame 1.",
            "Go to the title screen first (for scripts that click through the menus)."
        };

        void DrawGeneral()
        {
            DrawTransportCard();
            DrawScriptFileCard();
            DrawClockCard();
            DrawStartupOnPlayCard();
            DrawNoRefreshCard();
            DrawBakingCard();
            DrawSavestateCard();
        }

        /// <summary>The Play button must never start the standalone clock (which pauses the game) when there is no script.</summary>
        void PressPlayPause()
        {
            if (_engine.Mode == EngineMode.Idle && _engine.Script == null)
            {
                ShowNoScriptMessage();
                return;
            }
            _engine.TogglePause();
        }

        void ShowNoScriptMessage()
        {
            _statusText = "No script is loaded, so there is nothing to play. Load one (Script file box below), write one in the Script Editor tab, or press Record. The game was not paused.";
            _statusIsError = true;
            _plugin.Toast("No script loaded: nothing to play. The game was not paused.", true);
        }

        void DrawTransportCard()
        {
            BeginCard("Transport");
            GUILayout.BeginHorizontal();
            string transportLabel = _engine.Mode == EngineMode.Playing
                ? "Pause"
                : (_engine.Mode == EngineMode.Standalone ? (_time.Paused ? "Resume" : "Pause") : "Play");
            if (BtnPrimary(transportLabel, "Play starts the script from the start, or resumes if paused (F5). While it plays this button is Pause and stops at the current tick; after the script has finished, Play starts it again from the start. While recording, F5 runs / pauses the recording. With no script loaded this only shows a message.", GUILayout.Width(100)))
                PressPlayPause();
            GUI.enabled = _engine.IsActive;
            if (Btn("Step frame", "Advance exactly one tick while paused (F6).", GUILayout.Width(100)))
                _engine.Step();
            if (Btn("Rewind", "Go back to tick 0 and restore the state captured when Play started (F4).", GUILayout.Width(80)))
                _engine.Rewind();
            if (Btn("Stop", "Stop the run and give the game its own clock back (F11).", GUILayout.Width(70)))
                _engine.Stop();
            GUI.enabled = true;
            bool recording = _engine.Mode == EngineMode.Recording;
            if (recording
                    ? BtnDanger("Stop recording", "Stop recording and save the script (F10). Asks first if that would overwrite an existing file.", GUILayout.Width(120))
                    : Btn("Record", "Start recording what you do (F10; starts paused, F5 runs). With a script loaded it records into that script, otherwise into a new one.", GUILayout.Width(120)))
                _engine.ToggleRecording();
            if (BtnChoice(_engine.FastForward, _engine.FastForward ? "FF: on" : "FF: off",
                    "Fast-forward: run the script as fast as the PC allows (hold F7 for a quick burst).", GUILayout.Width(80)))
                _engine.ToggleFastForward();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            Lbl("Playback speed", "Wall-clock speed only. The tick length and the physics stay identical, so the run result never changes.", GUILayout.Width(100));
            for (int i = 0; i < PlaybackSpeeds.Length; i++)
            {
                bool selected = Math.Abs(_engine.PlaybackSpeed - PlaybackSpeeds[i]) < 0.001;
                if (BtnChoice(selected, PlaybackSpeeds[i].ToString("0.##") + "x",
                        "Play at " + PlaybackSpeeds[i].ToString("0.##") + "x real time.", GUILayout.Width(56)))
                    _engine.SetPlaybackSpeed(PlaybackSpeeds[i]);
            }
            GUILayout.EndHorizontal();
            EndCard();

            DrawStudioCard();
        }

        void DrawScriptFileCard()
        {
            BeginCard("Script file");
            GUILayout.BeginHorizontal();
            _pathText = Field(_pathText, "Path of the .uktas / .hltas file. Relative names are looked up in the script folder.");
            if (Btn("Load", "Read this file into the tool. If the file does not exist, nothing happens and the game is not paused.", GUILayout.Width(65)))
                LoadScriptFromPath(_pathText);
            if (Btn("Save", "Write the script in memory to this file (relative names go to the script folder). Asks before overwriting an existing file.", GUILayout.Width(65)))
                SaveScriptToPathText();
            if (Btn("New", "Create a fresh empty script in memory (nothing is written until you save). Relative names go to the script folder.", GUILayout.Width(60)))
            {
                _engine.NewScript(_pathText);
                if (!string.IsNullOrEmpty(_engine.ScriptPath))
                    _pathText = _engine.ScriptPath;
                RememberScriptPath();
            }
            GUILayout.EndHorizontal();

            Lbl("Scripts in " + _plugin.ScriptDirectory, "Every .uktas and .hltas file in the script folder and its examples subfolder. Click a name to load it.");
            _fileScroll = GUILayout.BeginScrollView(_fileScroll, GUILayout.Height(92));
            DrawFileButtons(_files);
            if (_files.Length == 0)
                Hint("(no .uktas or .hltas files yet)");
            if (_exampleFiles.Length > 0)
            {
                Hint("examples");
                DrawFileButtons(_exampleFiles);
            }
            GUILayout.EndScrollView();
            StatusLine();
            EndCard();
        }

        void DrawFileButtons(string[] files)
        {
            for (int i = 0; i < files.Length; i++)
            {
                GUILayout.BeginHorizontal();
                if (Btn(Path.GetFileName(files[i]), "Load " + files[i], GUILayout.Width(250)))
                {
                    _pathText = files[i];
                    LoadScriptFromPath(files[i]);
                }
                if (Btn("Edit", "Open this file in the Script Editor tab.", GUILayout.Width(50)))
                    OpenInTextEditor(files[i]);
                GUILayout.Label(files[i], _hintStyle);
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>Saves the in-memory script to the path box (resolved like Load), asking before an overwrite.</summary>
        void SaveScriptToPathText()
        {
            string target = _plugin.ResolveScriptSavePath(_pathText);
            RequestOverwriteSave(target, delegate
            {
                if (_engine.SaveScript(target))
                {
                    _pathText = target;
                    RememberScriptPath();
                }
            });
        }

        /// <summary>
        /// Resolves a typed path to an existing file. Absolute paths as typed; relative names inside
        /// the script folder (with .uktas added when no extension was given), then relative to the
        /// game folder. Null when there is no such file.
        /// </summary>
        string ResolveScriptPath(string path)
        {
            try
            {
                string text = (path ?? string.Empty).Trim().Trim('"');
                if (text.Length == 0)
                    return null;
                if (Path.IsPathRooted(text))
                    return File.Exists(text) ? text : null;
                // Relative names: the script folder first (where Save writes them), then the
                // game's working folder as a fallback.
                string inFolder = Path.Combine(_plugin.ScriptDirectory, text);
                if (File.Exists(inFolder))
                    return inFolder;
                if (!Path.HasExtension(text) && File.Exists(inFolder + ".uktas"))
                    return inFolder + ".uktas";
                if (File.Exists(text))
                    return Path.GetFullPath(text);
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>
        /// Loads a script into the engine. A missing file only produces a message: the engine, the
        /// game clock and the loaded script are left exactly as they were.
        /// </summary>
        bool LoadScriptFromPath(string path)
        {
            string resolved = ResolveScriptPath(path);
            if (resolved == null)
            {
                string shown = string.IsNullOrEmpty(path) ? "(no path typed)" : path;
                _statusText = "No script file found at: " + shown + "  Nothing was loaded and the game was not paused.";
                _statusIsError = true;
                _plugin.Toast("No script file found: " + shown + ". Nothing was loaded; the game was not paused.", true);
                return false;
            }
            _pathText = resolved;
            if (!_engine.LoadScript(resolved, true))
                return false;
            _lastSyncedTick = -1;
            RememberScriptPath();
            return true;
        }

        void DrawClockCard()
        {
            BeginCard("Clock");
            GUILayout.BeginHorizontal();
            Lbl("host_frametime", "Virtual seconds per tick. 0.008 is 125 ticks per second, ULTRAKILL's native physics step.", GUILayout.Width(100));
            _frametimeText = Field(_frametimeText, "Seconds per tick, for example 0.008.", GUILayout.Width(100));
            if (Btn("Apply", "Use the typed value as the tick length.", GUILayout.Width(60)))
                ApplyFrametime(_frametimeText);
            if (Btn("125 fps (native)", "0.008 s per tick: one native physics step. The default.", GUILayout.Width(115)))
                ApplyFrametime("0.008");
            if (Btn("250 fps", "0.004 s per tick.", GUILayout.Width(70)))
                ApplyFrametime("0.004");
            if (Btn("500 fps", "0.002 s per tick.", GUILayout.Width(70)))
                ApplyFrametime("0.002");
            if (Btn("1000 fps", "0.001 s per tick.", GUILayout.Width(75)))
                ApplyFrametime("0.001");
            GUILayout.EndHorizontal();

            // Quantised to 0.00001 s so 0.008 and its neighbours stay reachable and
            // the handle cannot snap onto a 60 fps value.
            float slider = Slide((float)_time.HostFrametime, 0.001f, 0.05f, "Drag to change the tick length (0.001 to 0.05 seconds).");
            slider = Mathf.Round(slider * 100000f) / 100000f;
            if (Math.Abs(slider - (float)_time.HostFrametime) > 0.000001f)
            {
                _time.SetHostFrametime(slider);
                _plugin.DefaultHostFrametime.Value = (float)slider;
                _frametimeText = slider.ToString("0.######", CultureInfo.InvariantCulture);
                if (_engine.Script != null)
                {
                    _engine.Script.Properties.HostFrametime = _time.HostFrametime;
                    _engine.Script.InvalidateTimeline();
                }
            }

            Hint(_cachedClock);
            GUILayout.Space(2);
            GUILayout.Label(_cachedProgress);
            Hint(_cachedMode);
            Hint(_cachedStatusDetail);
            EndCard();
        }

        void DrawStartupOnPlayCard()
        {
            BeginCard("Startup on Play");
            int selected = 0;
            string configured = (_plugin.PlayStartMode.Value ?? "script").Trim().ToLowerInvariant();
            for (int i = 0; i < StartValues.Length; i++)
                if (StartValues[i] == configured)
                    selected = i;
            int picked = ChoiceRow(selected, StartLabels, StartTips, 0f);
            if (picked != selected)
                _plugin.PlayStartMode.Value = StartValues[picked];
            _plugin.RestartLevelOnPlay.Value = Tog(_plugin.RestartLevelOnPlay.Value,
                "Restart Level on Play (overrides the choice above)",
                "Every Play reloads the current mission first, whatever the buttons above say.");
            GUILayout.BeginHorizontal();
            Lbl("level", "Scene name used by 'Load level', for example 1-1. Empty uses the script header.", GUILayout.Width(40));
            _plugin.PlayStartLevel.Value = Field(_plugin.PlayStartLevel.Value, "Scene to load before frame 1 when 'Load level' is chosen.", GUILayout.Width(220));
            Lbl("state slot", "Savestate slot restored before frame 1 when 'Load savestate' is chosen.", GUILayout.Width(65));
            _plugin.PlayStartStateSlot.Value = Mathf.Clamp(IntField(_plugin.PlayStartStateSlot.Value, 40, "Savestate slot restored before frame 1 when Load savestate is chosen."), 0, SaveStateManager.SlotCount - 1);
            GUILayout.EndHorizontal();
            EndCard();
        }

        void DrawNoRefreshCard()
        {
            BeginCard("Fast-forward (NoRefresh)");
            _plugin.UseNorefresh.Value = Tog(_plugin.UseNorefresh.Value,
                "Use norefresh checkpoints (Play resumes from the last valid norefresh line)",
                "When a cached checkpoint exists for this level, Play skips straight to it instead of replaying everything before.");
            GUILayout.BeginHorizontal();
            if (Btn("Clear norefresh cache", "Forget every cached checkpoint.", GUILayout.Width(170)))
                _engine.ClearNorefreshCache();
            GUILayout.Label("cached checkpoints: " + _states.NorefreshCount, _hintStyle);
            GUILayout.EndHorizontal();
            Separator();
            _plugin.NoRefreshFastForward.Value = Tog(_plugin.NoRefreshFastForward.Value,
                "Enable FFF NoRefresh Fast-Forward (run FFF segments with no 3D rendering)",
                "Segments before an FFF marker run with all cameras off at uncapped speed. Rendering returns right after the marker.");
            _plugin.PauseAtNoRefreshBreak.Value = Tog(_plugin.PauseAtNoRefreshBreak.Value,
                "Pause at the FFF breakpoint instead of continuing with rendering",
                "Stop on the exact tick where the fast-forward ends.");
            GUILayout.BeginHorizontal();
            Lbl("NoRefresh to tick", "Fast-forward headless to this tick, then show the game again.", GUILayout.Width(110));
            _noRefreshTickText = Field(_noRefreshTickText, "Target tick number.", GUILayout.Width(80));
            if (Btn("Run", "Fast-forward to the typed tick.", GUILayout.Width(60)))
            {
                int targetTick;
                if (int.TryParse(_noRefreshTickText, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetTick))
                    _engine.RunNoRefreshToTick(targetTick);
                else
                {
                    _statusText = "NoRefresh tick must be an integer.";
                    _statusIsError = true;
                }
            }
            GUI.enabled = _engine.NoRefreshActive;
            if (Btn("Abort", "Stop the fast-forward at the current tick (F5 or F11 also work).", GUILayout.Width(70)))
                _engine.AbortNoRefresh();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            int noRefreshBreaks = _engine.Script != null ? _engine.Script.NoRefreshBreakTicks.Count : 0;
            int nextBreak = noRefreshBreaks > 0 ? _engine.Script.NextNoRefreshBreakTick(_engine.CurrentTick) : -1;
            Hint(_engine.NoRefreshActive
                ? "NOREFRESH " + _engine.CurrentTick + " -> " + _engine.NoRefreshTargetTick +
                  "   " + _engine.NoRefreshTicksPerSecond.ToString("0", CultureInfo.InvariantCulture) + " ticks/s" +
                  "  (" + _engine.SuspendedCameraCount + " cameras off)"
                : (noRefreshBreaks == 0
                    ? "no FFF markers in this script"
                    : nextBreak > 0
                        ? noRefreshBreaks + " FFF breakpoint(s); next at tick " + nextBreak
                        : noRefreshBreaks + " FFF breakpoint(s); none ahead of this tick"));
            EndCard();
        }

        void DrawBakingCard()
        {
            BakeManager bake = _time.Bake;
            if (bake == null)
                return;
            BeginCard("Baking");
            // Baked playback is not legitimate: always visible, in the error colour.
            GUILayout.Label(BakeManager.NotLegitWarning, _errorStyle);
            GUILayout.BeginHorizontal();
            bake.EnableBakedPlayback.Value = Tog(bake.EnableBakedPlayback.Value,
                "Enable Baked Playback (.ukbake)",
                "NOT LEGITIMATE. Inside a bake_start/bake_end block with a valid .ukbake, the inputs still run and the baked world state is written over the game after every tick. Can be switched at any time. A run that uses it cannot be submitted.",
                GUILayout.Width(250));
            GUILayout.Label(BakeManager.NotLegitWarning, _errorStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            bake.VerifyRawPlayback.Value = Tog(bake.VerifyRawPlayback.Value,
                "Verify Raw Playback Against Bake (Drift Detector)",
                "With baked playback off, compare the live player and coin positions to the bake every tick and report the first divergence in each block.");
            bake.SkipBakedBlocks.Value = Tog(bake.SkipBakedBlocks.Value,
                "Skip Baked Blocks to End",
                "When a block has a valid bake, jump straight to its last baked frame and continue live on the first tick after bake_end.");
            GUILayout.BeginHorizontal();
            TasScript script = _engine.Script;
            GUI.enabled = script != null && script.HasBakeBlocks && !string.IsNullOrEmpty(_engine.ScriptPath);
            if (Btn("Force Re-Bake All Blocks", "Delete this script's .ukbake files; the next run records every block fresh.", GUILayout.Width(190)))
            {
                int deleted = bake.ForceRebake(script, _engine.ScriptPath);
                _plugin.Toast("Force re-bake: " + deleted + " .ukbake file(s) deleted; the next run records every block.", false);
            }
            GUI.enabled = true;
            bool nonLegit = bake.EnableBakedPlayback.Value || bake.Mode == BakeBlockMode.Enforced ||
                            ReferenceEquals(bake.StatusText, BakeManager.StatusSkipped);
            GUILayout.Label(bake.StatusText, nonLegit ? _errorStyle : _hintStyle);
            GUILayout.EndHorizontal();
            if (bake.EnableBakedPlayback.Value)
                GUILayout.Label("STATUS: NON-LEGIT - baked playback is ON. This run replays stored state and is not a valid TAS run.", _errorStyle);
            // Submission guard: the run's state is always shown here.
            if (BakeManager.RunIsSubmittable)
                Hint("Run status: real inputs only, no baked state applied in this run (submittable).");
            else
                GUILayout.Label("Run submission: BLOCKED - NOT SUBMITTABLE (" + BakeManager.NotSubmittableReason +
                                "). Start a fresh run from tick 0 with baked playback off.", _errorStyle);
            int blocks = script != null ? script.BakeBlocks.Count : 0;
            Hint(blocks == 0
                ? "no bake_start / bake_end blocks in this script"
                : blocks + " bake block(s), " + bake.CachedBlockCount(script) + " with a valid bake in memory" +
                  (bake.DetailText.Length > 0 ? "   " + bake.DetailText : string.Empty));
            // Desync corrections of enforced playback (values written because the game drifted from the bake).
            string corrections = bake.CorrectionText;
            if (corrections.Length > 0)
                Hint(corrections);
            EndCard();
        }

        void DrawSavestateCard()
        {
            BeginCard("Savestates");
            GUILayout.BeginHorizontal();
            Lbl("Active slot", "Slot used by the quick save / load hotkeys (F8 / F9). 0 to " + (SaveStateManager.SlotCount - 1) + ".", GUILayout.Width(80));
            int slot = _plugin.ActiveStateSlot.Value;
            int parsedSlot;
            string slotText = Field(slot.ToString(), "Slot number.", GUILayout.Width(40));
            if (int.TryParse(slotText, out parsedSlot))
                _plugin.ActiveStateSlot.Value = Mathf.Clamp(parsedSlot, 0, SaveStateManager.SlotCount - 1);
            if (Btn("Save slot", "Store the whole game state in this slot (F8).", GUILayout.Width(80)))
                _states.Save(_plugin.ActiveStateSlot.Value);
            if (Btn("Load slot", "Restore the game state saved in this slot (F9).", GUILayout.Width(80)))
                _engine.LoadStateSlot(_plugin.ActiveStateSlot.Value);
            GUILayout.Label(_states.SlotSummary(_plugin.ActiveStateSlot.Value), _hintStyle);
            GUILayout.EndHorizontal();
            EndCard();
        }

        void DrawStudioCard()
        {
            BeginCard("Step studio");
            DrawStudioControls();
            EndCard();
        }

        void DrawStudioControls()
        {
            GUILayout.BeginHorizontal();
            Lbl("Pause at tick", "Play until this tick, then pause there.", GUILayout.Width(88));
            _pauseAtTickText = Field(_pauseAtTickText, "Tick number to stop at.", GUILayout.Width(80));
            if (Btn("Play to Tick", "Play from here and pause on the typed tick.", GUILayout.Width(100)))
            {
                int target;
                if (int.TryParse(_pauseAtTickText, NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
                    _engine.PlayToTick(target);
                else
                {
                    _statusText = "Pause-at-tick must be an integer.";
                    _statusIsError = true;
                }
            }
            if (Btn("Overwrite from Here", "Throw away every frame after the current tick so you can record new input from here.", GUILayout.Width(140)))
                _engine.BranchFromCurrentTick();
            if (Btn("Undo Step", "Go back one tick and remove the last step-recorded frame (Ctrl+Z).", GUILayout.Width(85)))
                _engine.UndoStep();
            if (Btn("Restore Branch", "Bring back the frames removed by the last 'Overwrite from Here'.", GUILayout.Width(110)))
                _engine.RestoreBranch();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _plugin.StepRecord.Value = Tog(_plugin.StepRecord.Value,
                "Step-record staged input (F6 writes one tick)",
                "While paused, each F6 writes the keys you are holding as one new frame.");
            _plugin.AutoSaveToDisk.Value = Tog(_plugin.AutoSaveToDisk.Value,
                "Auto-save to disk",
                "Write the script file after every edit and when recording stops.");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Undo history (ticks)", "How many ticks Undo Step (Ctrl+Z) can go back: one snapshot per F6 step is kept in memory (1 - 1000, default " + SaveStateManager.DefaultHistoryTicks + "). Changing it clears the stored history.", GUILayout.Width(130));
            _plugin.RollingHistoryTicks.Value = Mathf.Clamp(IntField(_plugin.RollingHistoryTicks.Value, 60, "Rolling history length in ticks (1 - 1000)."), 1, 1000);
            _plugin.HistoryDuringPlayback.Value = Tog(_plugin.HistoryDuringPlayback.Value,
                "Also keep history while playing",
                "Snapshot every tick of continuous playback too, not only F6 steps. Costs one capture per tick.");
            GUILayout.EndHorizontal();

            string staged = TasInputColumns.FormatCompact(_engine.StagedButtons);
            Hint("free-look " + (_engine.FreeLookActive ? "ON" : "off") +
                 "   staged " + staged +
                 "   pitch/yaw " + _engine.StagedPitch.ToString("0.00") + "/" + NumYaw(_engine.StagedYaw));
            if (_engine.PauseAtTick >= 0)
                Hint("seeking to tick " + _engine.PauseAtTick);
            if (_engine.BranchTick >= 0)
                Hint("branch point: tick " + _engine.BranchTick);
        }

        void ApplyFrametime(string text)
        {
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                value < TasScript.MinFrametime || value > TasScript.MaxFrametime)
            {
                _statusText = "Frametime must be " + TasScript.MinFrametime + " - " + TasScript.MaxFrametime + " seconds.";
                _statusIsError = true;
                return;
            }
            _time.SetHostFrametime(value);
            _plugin.DefaultHostFrametime.Value = (float)value;
            _frametimeText = value.ToString("0.########", CultureInfo.InvariantCulture);
            if (_engine.Script != null)
            {
                _engine.Script.Properties.HostFrametime = value;
                _engine.Script.InvalidateTimeline();
            }
            _statusText = "host_frametime = " + _frametimeText + " (" + (1.0 / value).ToString("0.##") +
                          " ticks/s, " + _time.StepsPerTick.ToString("0.###") + " native steps per tick)";
            _statusIsError = false;
            _engine.AutoSaveIfEnabled();
        }

        void RefreshFileList()
        {
            int frame = SimulationFreeze.FrameCount;
            if (frame - _filesFrame < 120 && (_files.Length > 0 || _exampleFiles.Length > 0))
                return;
            _filesFrame = frame;
            _files = ScriptFilesIn(_plugin.ScriptDirectory);
            _exampleFiles = ScriptFilesIn(Path.Combine(_plugin.ScriptDirectory, "examples"));
        }

        /// <summary>The .uktas and .hltas files directly in <paramref name="folder"/>, sorted; empty when none.</summary>
        static string[] ScriptFilesIn(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return new string[0];
                string[] uktas = Directory.GetFiles(folder, "*.uktas");
                string[] hltas = Directory.GetFiles(folder, "*.hltas");
                string[] files = new string[uktas.Length + hltas.Length];
                Array.Copy(uktas, 0, files, 0, uktas.Length);
                Array.Copy(hltas, 0, files, uktas.Length, hltas.Length);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files;
            }
            catch
            {
                return new string[0];
            }
        }

        // ------------------------------------------------------------------
        // Frame editor: the framebulk lines around the current tick
        // ------------------------------------------------------------------

        void DrawFrameEditorTab()
        {
            if (_engine.Script == null)
            {
                BeginCard("No script loaded");
                GUILayout.Label("There is no script in memory yet.", _bodyStyle);
                Hint("Load one from the General tab, write one in the Script Editor tab, or record a run.");
                GUILayout.BeginHorizontal();
                if (BtnPrimary("Create new script", "Start an empty script in memory.", GUILayout.Width(180)))
                    _engine.NewScript(_pathText);
                GUILayout.EndHorizontal();
                EndCard();
                return;
            }

            TasScript script = _engine.Script;

            BeginCard("Script");
            GUILayout.Label(string.IsNullOrEmpty(_engine.ScriptPath) ? "(unsaved)" : _engine.ScriptPath);
            Hint("lines " + script.Lines.Count + "   ticks " + script.TotalTicks +
                 "   duration " + script.TotalTime.ToString("0.###") + "s   seed " + script.Properties.Seed);
            EndCard();

            DrawStudioCard();

            BeginCard("Around the current tick");
            Hint(ColumnHeader);
            int lineIndex = _engine.CurrentLineIndex;
            if (lineIndex < 0)
                lineIndex = 0;
            int from = Math.Max(0, lineIndex - 5);
            int to = Math.Min(script.Lines.Count - 1, lineIndex + 5);
            _lineScroll = GUILayout.BeginScrollView(_lineScroll, GUILayout.Height(160));
            for (int i = from; i <= to; i++)
            {
                TasFrameLine line = script.Lines[i];
                bool current = i == lineIndex;
                if (current)
                    GUI.color = new Color(1f, 0.74f, 0.45f);
                GUILayout.Label((current ? "> " : "  ") + i.ToString().PadLeft(5) + "  " +
                                line.ToDisplayLine(), _hudStyle);
                GUI.color = Color.white;
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            _autoSync = Tog(_autoSync, "Follow playback", "Keep the fields below on the frame that is playing.", GUILayout.Width(130));
            if (Btn("Sync to current frame", "Reload the fields below from the frame at the current tick.", GUILayout.Width(150)))
                SyncEditor(true);
            if (Btn("Split Bulk", "Cut the current framebulk into two halves (commands stay on the first).", GUILayout.Width(90)))
            {
                if (script.SplitBulkInHalf(_engine.CurrentLineIndex))
                {
                    _lastSyncedTick = -1;
                    _statusText = "Split the framebulk in half; commands stayed on the first half.";
                    _statusIsError = false;
                    _engine.AutoSaveIfEnabled();
                }
                else
                {
                    _statusText = "That line has no bulk range to split (frames must be 2 or more).";
                    _statusIsError = true;
                }
            }
            if (Btn("Duplicate", "Copy the current framebulk right below itself.", GUILayout.Width(85)))
            {
                script.Duplicate(_engine.CurrentLineIndex);
                _lastSyncedTick = -1;
                _engine.AutoSaveIfEnabled();
            }
            if (Btn("Merge identical", "Join neighbouring lines that hold the same input into one bulk.", GUILayout.Width(110)))
            {
                script.MergeAdjacent();
                _lastSyncedTick = -1;
                _engine.AutoSaveIfEnabled();
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (Btn("Insert Framebulk", "Add an empty framebulk after the current one.", GUILayout.Width(120)))
            {
                script.InsertAfter(_engine.CurrentLineIndex);
                _lastSyncedTick = -1;
                _engine.AutoSaveIfEnabled();
            }
            if (BtnDanger("Delete Framebulk", "Remove the current framebulk from the script.", GUILayout.Width(125)))
            {
                script.RemoveAt(_engine.CurrentLineIndex);
                _lastSyncedTick = -1;
                _engine.AutoSaveIfEnabled();
            }
            GUILayout.FlexibleSpace();
            if (Btn("Save to Disk", "Write the script to the path in the General tab. Asks before overwriting an existing file.", GUILayout.Width(110)))
                SaveScriptToPathText();
            GUILayout.EndHorizontal();
            EndCard();

            if (_autoSync && _engine.CurrentTick != _lastSyncedTick)
                SyncEditor(false);

            BeginCard("Current framebulk");
            Hint("Edits are live during playback. " + ColumnHeader);
            GUILayout.BeginHorizontal();
            Lbl("autofuncs", "Automatic helpers on this line: j autojump, s autoslide, d autodash, c coin assist, g ground slam, w whiplash assist, t coin throw (once per bulk), e deadcoin (when a coin is airborne: aim one frame, fire the next, then the aim is yours again).", GUILayout.Width(70));
            _autofuncsText = Field(_autofuncsText, "Ten characters, '-' for off.", GUILayout.Width(104));
            Lbl("movement", "Movement keys held: f forward, l left, r right, b back, d dash, s slide.", GUILayout.Width(66));
            _movementText = Field(_movementText, "Six characters, '-' for off.", GUILayout.Width(64));
            Lbl("actions", "Actions: j jump, 1 fire, 2 alt fire, p punch, v whiplash, wN weapon slot, x change fist, and more (see the guide).", GUILayout.Width(52));
            _actionsText = Field(_actionsText, "Action tokens, '-' for none.", GUILayout.Width(80));
            Lbl("frames", "How many ticks this line lasts.", GUILayout.Width(48));
            _framesText = Field(_framesText, "A whole number, 1 or more.", GUILayout.Width(56));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Lbl("frametime", "Tick length for just this line. Leave empty to use the script's host_frametime.", GUILayout.Width(70));
            _ftOverrideText = Field(_ftOverrideText, "Seconds, or empty.", GUILayout.Width(104));
            Lbl("yaw", "Horizontal look angle. Absolute (90.0), relative (d+2.0) or '-' to keep.", GUILayout.Width(66));
            _yawText = Field(_yawText, "Yaw value.", GUILayout.Width(64));
            Lbl("pitch", "Vertical look angle. Absolute (0.0), relative (d-1.0) or '-' to keep.", GUILayout.Width(52));
            _pitchText = Field(_pitchText, "Pitch value.", GUILayout.Width(80));
            Hint("(- keeps / inherits)");
            GUILayout.EndHorizontal();
            Hint("autofuncs: a j s d c g w t e   movement: f l r b d s   actions: j 1 2 p v wN x F K N P T < > yN S (see spec)");
            GUILayout.BeginHorizontal();
            Lbl("commands", "Console-style commands for this line, separated by ';'. Prefix one with once: to run it on the first tick only.", GUILayout.Width(70));
            _commandsText = Field(_commandsText, "Example: once:echo hello; click");
            GUILayout.EndHorizontal();
            Hint("Commands run on every tick of the bulk; prefix one with once: to run it on the first tick only.");
            _liveEdit = Tog(_liveEdit, "Live apply during playback", "Apply every change to the running script immediately.", GUILayout.Width(220));
            if (_liveEdit && GUI.changed)
                ApplyLineEdits();

            GUILayout.BeginHorizontal();
            if (BtnPrimary("Apply to this line", "Write the fields above into the current framebulk.", GUILayout.Width(140)))
                ApplyLineEdits();
            if (Btn("Append framebulk", "Add an empty framebulk at the very end of the script.", GUILayout.Width(130)))
            {
                script.InsertAfter(script.Lines.Count - 1);
                _lastSyncedTick = -1;
                _statusText = "Appended a new framebulk at the end of the script.";
                _statusIsError = false;
                _engine.AutoSaveIfEnabled();
            }
            if (Btn("Rewind to start", "Go back to tick 0.", GUILayout.Width(120)))
                _engine.Rewind();
            GUILayout.EndHorizontal();
            StatusLine();
            EndCard();

            BeginCard("Properties");
            GUILayout.BeginHorizontal();
            Lbl("seed", "Random seed applied when the script starts. Same seed, same run.", GUILayout.Width(38));
            _seedText = Field(_seedText, "A 32-bit whole number.", GUILayout.Width(90));
            Lbl("host_frametime", "Default tick length for the whole script.", GUILayout.Width(100));
            _frametimeText = Field(_frametimeText, "Seconds per tick.", GUILayout.Width(90));
            Lbl("author", "Who made the script (saved in the header).", GUILayout.Width(48));
            _authorText = Field(_authorText, "Your name.", GUILayout.Width(150));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Lbl("description", "A note about the run (saved in the header).", GUILayout.Width(75));
            _descriptionText = Field(_descriptionText, "Free text.");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (BtnPrimary("Apply properties", "Write seed, frametime, author and description into the script header.", GUILayout.Width(140)))
                ApplyProperties();

            // Writes the level the player is standing in into the script header,
            // so Play loads that level before frame 1.
            string currentLevel = _bridge.CurrentLevelId ?? string.Empty;
            GUI.enabled = !string.IsNullOrEmpty(currentLevel);
            if (Btn(string.IsNullOrEmpty(currentLevel)
                    ? "Load current level"
                    : "Load current level (" + currentLevel + ")",
                    "Make Play load the level you are standing in before frame 1 (writes start_type load_level).",
                    GUILayout.Width(230)))
            {
                script.Properties.StartType = TasStartType.LoadLevel;
                script.Properties.LoadLevelName = currentLevel;
                _statusText = "Play now loads level " + currentLevel +
                              " first (header: start_type load_level " + currentLevel + ").";
                _statusIsError = false;
                _engine.AutoSaveIfEnabled();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(currentLevel))
                Hint("Load current level needs a loaded campaign scene.");

            if (script.Warnings.Count > 0)
            {
                GUILayout.Space(4);
                Hint("Parser warnings (" + script.Warnings.Count + "):");
                GUILayout.Label(script.Warnings[script.Warnings.Count - 1], _errorStyle);
            }
            EndCard();
        }

        void SyncEditor(bool force)
        {
            TasFrameLine line = _engine.CurrentLine;
            if (line == null && _engine.Script != null && _engine.Script.Lines.Count > 0)
            {
                int index = Mathf.Clamp(_engine.CurrentLineIndex, 0, _engine.Script.Lines.Count - 1);
                line = _engine.Script.Lines[index];
            }
            if (line == null)
                return;

            _autofuncsText = TasInputColumns.FormatAutofuncs(line.Autofuncs);
            _movementText = TasInputColumns.FormatMovement(line.Buttons);
            _actionsText = TasInputColumns.FormatActions(line.Buttons);
            _framesText = line.Count.ToString(CultureInfo.InvariantCulture);
            _yawText = line.Yaw.Raw;
            _pitchText = line.Pitch.Raw;
            _ftOverrideText = line.HasFrametimeOverride
                ? TasScript.FormatFrametime(line.FrametimeOverride)
                : string.Empty;
            _commandsText = string.Join("; ", line.Commands.ToArray());
            if (_engine.Script != null)
            {
                _seedText = _engine.Script.Properties.Seed.ToString(CultureInfo.InvariantCulture);
                _frametimeText = _engine.Script.Properties.HostFrametime.ToString("0.######", CultureInfo.InvariantCulture);
                _authorText = _engine.Script.Properties.Author;
                _descriptionText = _engine.Script.Properties.Description;
            }
            _lastSyncedTick = _engine.CurrentTick;
            if (force)
                _statusText = "Editor synced to frame " + _engine.CurrentTick + ".";
        }

        void ApplyLineEdits()
        {
            if (_engine.Script == null)
                return;
            int index = _engine.CurrentLineIndex;
            if (index < 0 || index >= _engine.Script.Lines.Count)
            {
                _statusText = "No current line to edit.";
                _statusIsError = true;
                return;
            }
            TasFrameLine line = _engine.Script.Lines[index];
            int count;
            if (!int.TryParse(_framesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1)
            {
                _statusText = "The frames column (field 7) must be a positive integer.";
                _statusIsError = true;
                return;
            }
            try
            {
                TasAutofuncs autofuncs = TasInputColumns.ParseAutofuncs(_autofuncsText, 0, _autofuncsText);
                TasButtons buttons = TasInputColumns.ParseMovement(_movementText, 0, _movementText) |
                                      TasInputColumns.ParseActions(_actionsText, 0, _actionsText);
                AngleToken pitch = AngleToken.Parse(_pitchText, "pitch", 0, _pitchText);
                AngleToken yaw = AngleToken.Parse(_yawText, "yaw", 0, _yawText);
                bool hasFt = !string.IsNullOrEmpty(_ftOverrideText.Trim());
                double ft = 0.0;
                if (hasFt &&
                    (!double.TryParse(_ftOverrideText, NumberStyles.Float, CultureInfo.InvariantCulture, out ft) ||
                     ft < TasScript.MinFrametime || ft > TasScript.MaxFrametime))
                {
                    _statusText = "Frame frametime must be " + TasScript.MinFrametime + " - " + TasScript.MaxFrametime + ".";
                    _statusIsError = true;
                    return;
                }

                // A special frame's angle column is its interpolation target, so it
                // must stay an absolute value.
                if (line.Kind == TasFrameKind.ChangeYaw && (yaw.Unchanged || yaw.IsDelta))
                {
                    _statusText = "change yaw needs an absolute yaw value.";
                    _statusIsError = true;
                    return;
                }
                if (line.Kind == TasFrameKind.ChangePitch && (pitch.Unchanged || pitch.IsDelta))
                {
                    _statusText = "change pitch needs an absolute pitch value.";
                    _statusIsError = true;
                    return;
                }
                if (line.Kind == TasFrameKind.TargetYaw && (yaw.Unchanged || yaw.IsDelta))
                {
                    _statusText = "target_yaw needs an absolute yaw value.";
                    _statusIsError = true;
                    return;
                }

                line.Count = Math.Min(count, TasScript.MaxTicksPerLine);
                line.Autofuncs = autofuncs;
                line.Buttons = buttons;
                line.Pitch = pitch;
                line.Yaw = yaw;
                if (line.Kind == TasFrameKind.TargetYaw)
                    line.TargetYaw = yaw;
                line.HasFrametimeOverride = hasFt;
                line.FrametimeOverride = hasFt ? ft : 0.0;
                line.Commands.Clear();
                string[] commands = _commandsText.Split(';');
                for (int i = 0; i < commands.Length; i++)
                {
                    string command = commands[i].Trim();
                    if (command.Length > 0)
                        line.Commands.Add(command);
                }
                _engine.Script.InvalidateTimeline();
                _statusText = "Applied line " + index + " (" + line.Count + " frames).";
                _statusIsError = false;
                _lastSyncedTick = -1;
                _engine.AutoSaveIfEnabled();
            }
            catch (Exception ex)
            {
                _statusText = ex.Message;
                _statusIsError = true;
            }
        }

        void ApplyProperties()
        {
            if (_engine.Script == null)
                return;
            int seed;
            double ft;
            if (!int.TryParse(_seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
            {
                _statusText = "Seed must be a 32-bit integer.";
                _statusIsError = true;
                return;
            }
            if (!double.TryParse(_frametimeText, NumberStyles.Float, CultureInfo.InvariantCulture, out ft) ||
                ft < TasScript.MinFrametime || ft > TasScript.MaxFrametime)
            {
                _statusText = "host_frametime must be " + TasScript.MinFrametime + " - " + TasScript.MaxFrametime + ".";
                _statusIsError = true;
                return;
            }

            _engine.Script.Properties.Seed = seed;
            _engine.Script.Properties.HostFrametime = ft;
            _engine.Script.Properties.Author = _authorText;
            _engine.Script.Properties.Description = _descriptionText;
            _engine.Script.InvalidateTimeline();
            _time.SetHostFrametime(ft);
            _plugin.DefaultHostFrametime.Value = (float)ft;
            _statusText = "Properties applied. Seed takes effect on the next play/rewind.";
            _statusIsError = false;
            _engine.AutoSaveIfEnabled();
        }

        // ------------------------------------------------------------------
        // Startup
        // ------------------------------------------------------------------

        void DrawStartup()
        {
            BeginCard("Script startup behavior");
            Hint("The header start_type is used only when On Play is Script header. Restart Level on Play overrides every choice.");
            _plugin.WaitForPlayerControl.Value = Tog(_plugin.WaitForPlayerControl.Value,
                "Wait for Player Control (NewMovement.activated) before tick 1",
                "Hold tick 1 until V1 can actually move (elevator rides and intros).");
            GUILayout.BeginHorizontal();
            Lbl("Scene settle frames", "Rendered frames to wait after control is granted, so triggers and occlusion culling settle.", GUILayout.Width(130));
            _plugin.SceneSettleFrames.Value = Mathf.Clamp(IntField(_plugin.SceneSettleFrames.Value, 40, "Rendered frames to wait after control is granted (0 to 30)."), 0, 30);
            GUILayout.EndHorizontal();

            string[] labels = { "Script header", "Current state", "Restart level", "Load level", "Load savestate", "Main menu (title screen)" };
            int selected = 0;
            string configured = (_plugin.PlayStartMode.Value ?? "script").Trim().ToLowerInvariant();
            for (int i = 0; i < StartValues.Length; i++)
                if (StartValues[i] == configured)
                    selected = i;
            int picked = ChoiceRow(selected, labels, StartTips, 0f);
            if (picked != selected)
                _plugin.PlayStartMode.Value = StartValues[picked];

            _plugin.RestartLevelOnPlay.Value = Tog(_plugin.RestartLevelOnPlay.Value,
                "Restart Level on Play (overrides the selection above)",
                "Every Play reloads the current mission first, whatever is selected above.");
            if (_plugin.RestartLevelOnPlay.Value)
                Hint("Restart is active. The current mission scene will reload before frame 1.");

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            Lbl("Load level scene", "Scene name used by 'Load level', for example 1-1.", GUILayout.Width(120));
            _plugin.PlayStartLevel.Value = Field(_plugin.PlayStartLevel.Value, "Scene to load before frame 1.", GUILayout.Width(260));
            GUILayout.EndHorizontal();
            Hint("Empty uses the script header's load_level scene.");

            GUILayout.BeginHorizontal();
            Lbl("Startup savestate slot", "Slot restored before frame 1 when 'Load savestate' is chosen.", GUILayout.Width(150));
            _plugin.PlayStartStateSlot.Value = Mathf.Clamp(IntField(_plugin.PlayStartStateSlot.Value, 45, "Savestate slot restored before frame 1 when Load savestate is chosen."), 0, SaveStateManager.SlotCount - 1);
            Hint("(0-" + (SaveStateManager.SlotCount - 1) + ")");
            GUILayout.EndHorizontal();

            if (_engine.Script != null)
            {
                TasProperties p = _engine.Script.Properties;
                string header = p.StartType == TasStartType.LoadLevel
                    ? "load_level " + p.LoadLevelName
                    : (p.StartType == TasStartType.LoadState
                        ? "load_state " + p.LoadStateSlot
                        : p.StartType.ToString().ToLowerInvariant());
                Hint("Loaded script header: start_type " + header);
            }
            else
            {
                Hint("No script loaded; header start_type is unavailable.");
            }
            EndCard();

            BeginCard("Workflow");
            GUILayout.BeginHorizontal();
            Lbl("autoload", "A script loaded automatically when the game starts. Absolute, or relative to the script folder.", GUILayout.Width(70));
            _plugin.Autoload.Value = Field(_plugin.Autoload.Value, "Path of the script to load at boot. Empty = none.");
            GUILayout.EndHorizontal();
            _plugin.Autoplay.Value = Tog(_plugin.Autoplay.Value, "Autoplay loaded script on boot",
                "Start playing the autoload script as soon as the game boots.");
            Hint("Path may be absolute or relative to " + _plugin.ScriptDirectory);

            GUILayout.BeginHorizontal();
            Lbl("default seed", "Seed written into new scripts.", GUILayout.Width(85));
            _plugin.DefaultSeed.Value = IntField(_plugin.DefaultSeed.Value, 90, "Seed written into new scripts.");
            Hint("native step " + _time.NativeFixedStep.ToString("0.####") + "s (fixed)");
            GUILayout.EndHorizontal();
            _plugin.GuiVisibleOnStart.Value = Tog(_plugin.GuiVisibleOnStart.Value, "Show this window on game start",
                "Open the TAS window as soon as the game starts. Insert toggles it any time.");
            EndCard();

            BeginCard("Game helpers");
            _plugin.AutoShowLeaderboard.Value = Tog(_plugin.AutoShowLeaderboard.Value,
                "Auto-show leaderboard (Tab stats panel)",
                "Keep the level stats / leaderboard panel the game shows while Tab is held open all the time.");
            _plugin.AutoEnableCheats.Value = Tog(_plugin.AutoEnableCheats.Value,
                "Auto-enable cheats",
                "Switch cheats on in every level, as if the cheat code was typed and the confirmation answered yes. The level counts as cheated, as in the game.");
            _plugin.PauseOnDeathKillPlane.Value = Tog(_plugin.PauseOnDeathKillPlane.Value,
                "Pause on death (kill plane)",
                "On: playback pauses when V1 falls below the kill plane. Off: a death is only recorded (HUD, log) and the level is never reloaded by itself.");
            if (!_plugin.PauseOnDeathKillPlane.Value)
                Hint("A death does not reload the level: load a savestate, rewind (F4), reload the script or use Restart Checkpoint.");
            EndCard();

            DrawLoadout();

            GUILayout.BeginHorizontal();
            if (Btn("Save config", "Write every plugin setting to the BepInEx config file now.", GUILayout.Width(120)))
                _plugin.ConfigFile.Save();
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------
        // Weapons and loadout (Startup tab)
        // ------------------------------------------------------------------

        void DrawLoadout()
        {
            BeginCard("Weapons and loadout");

            bool unlock = Tog(_plugin.UnlockAllWeapons.Value,
                "Unlock all weapons and arms (in-memory, save files untouched)",
                "Report every weapon and arm as owned. Your save file is never changed.");
            if (unlock != _plugin.UnlockAllWeapons.Value)
                _plugin.UnlockAllWeapons.Value = unlock;
            if (WeaponUnlock.UnlockAll != unlock)
                WeaponUnlock.UnlockAll = unlock;
            if (WeaponUnlock.OwnedOverride != null)
                Hint("A script's weapons header is active and takes priority over the unlock.");

            GUILayout.BeginHorizontal();
            if (Btn("Give all weapons", "Unlock and equip every weapon and arm right now (in memory only).", GUILayout.Width(140)))
                GiveAllWeapons();
            if (Btn("DUMP fist HUD (temp)", "Temporary diagnostic: write the fist / arm HUD state to a file.", GUILayout.Width(160)))
                DumpFistHud();
            if (Btn("Copy loadout", "Copy the weapons you own and your loadout settings as two script header lines.", GUILayout.Width(110)))
                CopyLoadout();
            GUI.enabled = _engine.Script != null;
            if (Btn("Write to script header", "Store the current weapons and loadout in the loaded script's header.", GUILayout.Width(160)))
                WriteLoadoutToScript();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            Hint("Copy loadout captures the weapons you own and your loadout settings as two script header lines.");

            if (_loadoutText.Length > 0)
            {
                // A text area so the lines can be selected; edits are discarded on purpose.
                GUILayout.TextArea(_loadoutText, GUILayout.Height(48));
                Tip("The captured header lines. Select and copy them into your script.");
            }

            StatusLine();
            EndCard();
        }

        void GiveAllWeapons()
        {
            if (_bridge.PlayerComponent == null || _bridge.GunSetter == null)
            {
                _statusText = "No player loaded.";
                _statusIsError = true;
                return;
            }
            _plugin.UnlockAllWeapons.Value = true;
            WeaponUnlock.UnlockAll = true;
            if (!_bridge.ResetWeaponSlots())
            {
                _statusText = "Unlock is on, but the weapon slots were not refreshed: " + _bridge.LastError;
                _statusIsError = true;
                return;
            }
            _statusText = WeaponUnlock.OwnedOverride != null
                ? "Unlock is on, but a script's weapons header is active and takes priority."
                : "All weapons and arms given (in memory only). Untick the unlock to go back to your real weapons.";
            _statusIsError = WeaponUnlock.OwnedOverride != null;
        }

        /// <summary>Captures the live weapons and loadout as the two header lines. False with the reason in <paramref name="text"/>.</summary>
        bool TryBuildLoadoutText(out string text)
        {
            List<string> weapons;
            Dictionary<string, string> loadout;
            if (!_bridge.TryCaptureLoadout(out weapons, out loadout))
            {
                text = string.IsNullOrEmpty(_bridge.LastError) ? "Loadout capture failed." : _bridge.LastError;
                return false;
            }
            text = "weapons " + TasScript.FormatWeapons(weapons) + "\n" + "loadout " + TasScript.FormatLoadout(loadout);
            return true;
        }

        // TEMPORARY diagnostic (see FistHudDump.cs): writes the fist/arm HUD state to a file.
        void DumpFistHud()
        {
            string path = FistHudDump.Write(_bridge);
            _statusText = "Fist HUD dump written: " + path;
            _statusIsError = false;
        }

        void CopyLoadout()
        {
            string text;
            if (!TryBuildLoadoutText(out text))
            {
                _statusText = text;
                _statusIsError = true;
                return;
            }
            _loadoutText = text;
            GUIUtility.systemCopyBuffer = text;
            _statusText = "Loadout copied: paste into the script header.";
            _statusIsError = false;
        }

        void WriteLoadoutToScript()
        {
            string error;
            if (!_engine.CaptureLoadoutIntoScript(out error))
            {
                _statusText = error;
                _statusIsError = true;
                return;
            }
            TasProperties properties = _engine.Script.Properties;
            _loadoutText = "weapons " + TasScript.FormatWeapons(properties.Weapons) + "\n" +
                           "loadout " + TasScript.FormatLoadout(properties.Loadout);
            _statusText = "Loadout written to the script header.";
            _statusIsError = false;
            _engine.AutoSaveIfEnabled();
        }

        // ------------------------------------------------------------------
        // Keybinds
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Trajectory options (Overlays tab)
        // ------------------------------------------------------------------

        void DrawTrajectoryOptions()
        {
            TrajectoryManager trajectory = _plugin.Trajectory;
            BeginCard("Player trajectory (SAR style)");
            Hint("Sampled once per simulation tick. Hold freecam (F2), release the cursor with Left Alt, hover a node for its tick, click to scrub.");
            _plugin.TrajectoryEnabled.Value = Tog(_plugin.TrajectoryEnabled.Value, "Record and draw the path",
                "Draw the line V1 has travelled in the world.");
            _plugin.TrajectoryHoverHitbox.Value = Tog(_plugin.TrajectoryHoverHitbox.Value, "Show player hitbox on hover",
                "Draw the same box as Show player hitbox, at the tick under the path cursor.");
            _plugin.TrajectoryGradient.Value = Tog(_plugin.TrajectoryGradient.Value, "Colour by speed (green slow, yellow medium, magenta fast)",
                "Colour the line by how fast you were going. Off uses the single colour below.");

            GUILayout.Label("Visibility");
            int visibility = ChoiceRow(Mathf.Clamp(_plugin.TrajectoryVisibilityMode.Value, 0, 2),
                new[] { "Always", "Freecam only", "Hidden" },
                new[] { "Always draw the path.", "Draw it only while the freecam is on.", "Never draw it." }, 0f);
            _plugin.TrajectoryVisibilityMode.Value = visibility;

            GUILayout.BeginHorizontal();
            Lbl("Line thickness", "How many parallel lines make the path (1 to 6).", GUILayout.Width(150));
            _plugin.TrajectoryThickness.Value = Mathf.Round(Slide(_plugin.TrajectoryThickness.Value, 1f, 6f, "Line thickness.", GUILayout.Width(180)));
            GUILayout.Label(_plugin.TrajectoryThickness.Value.ToString("0"), GUILayout.Width(30));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Opacity", "How see-through the path is.", GUILayout.Width(150));
            _plugin.TrajectoryOpacity.Value = Slide(_plugin.TrajectoryOpacity.Value, 0.1f, 1f, "Path opacity.", GUILayout.Width(180));
            GUILayout.Label(Mathf.RoundToInt(_plugin.TrajectoryOpacity.Value * 100f) + "%", GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Marker every N ticks", "Draw a small cross on the path every N ticks. 0 turns markers off.", GUILayout.Width(150));
            _plugin.TrajectoryMarkerEvery.Value = Mathf.RoundToInt(Slide(_plugin.TrajectoryMarkerEvery.Value, 0f, 60f, "Marker spacing in ticks.", GUILayout.Width(180)));
            GUILayout.Label(_plugin.TrajectoryMarkerEvery.Value == 0 ? "off" : _plugin.TrajectoryMarkerEvery.Value.ToString(), GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Single colour (hex)", "Path colour when the speed gradient is off, as #RRGGBB.", GUILayout.Width(150));
            _plugin.TrajectoryColor.Value = Field(_plugin.TrajectoryColor.Value, "Hex colour such as #40FF60.", GUILayout.Width(90));
            GUILayout.EndHorizontal();

            if (trajectory != null)
                Hint("Nodes recorded: " + trajectory.Count);
            EndCard();
        }

        void DrawCoinPathOptions()
        {
            CoinPathManager paths = _plugin.CoinPaths;
            BeginCard("Coin paths");
            Hint("Every coin is recorded once per simulation tick. Yellow: in flight. Green: punched. Cyan: shot. Red: shot back by an enemy. Magenta: tracking an enemy. Write 'clearcoinpaths' in field 8 to wipe the paths.");
            _plugin.CoinPathEnabled.Value = Tog(_plugin.CoinPathEnabled.Value, "Record and draw coin paths",
                "Draw where every coin has flown. Off stops recording and drawing.");

            GUILayout.BeginHorizontal();
            Lbl("Opacity", "How see-through the coin paths are.", GUILayout.Width(150));
            _plugin.CoinPathOpacity.Value = Slide(_plugin.CoinPathOpacity.Value, 0.1f, 1f, "Coin path opacity.", GUILayout.Width(180));
            GUILayout.Label(Mathf.RoundToInt(_plugin.CoinPathOpacity.Value * 100f) + "%", GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Line thickness", "How many parallel lines make a coin path (1 to 6).", GUILayout.Width(150));
            _plugin.CoinPathThickness.Value = Mathf.Round(Slide(_plugin.CoinPathThickness.Value, 1f, 6f, "Coin path line thickness.", GUILayout.Width(180)));
            GUILayout.Label(_plugin.CoinPathThickness.Value.ToString("0"), GUILayout.Width(30));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (Btn("Clear coin paths", "Forget every recorded coin trail now.", GUILayout.Width(160)) && paths != null)
                paths.Clear();
            GUILayout.EndHorizontal();

            if (paths != null)
                Hint("Trails recorded: " + paths.Count);
            EndCard();
        }

        // ------------------------------------------------------------------
        // Branches tab: .uktasproj workspace and bridged script switching
        // ------------------------------------------------------------------

        void DrawBranches()
        {
            BranchProject project = _plugin.Project;
            if (_projectPathText == string.Empty)
                _projectPathText = _plugin.ScriptDirectory != null ? System.IO.Path.Combine(_plugin.ScriptDirectory, "route" + BranchProject.ProjectExtension) : "route" + BranchProject.ProjectExtension;

            BeginCard("Route projects (.uktasproj)");
            Hint("One project file holds many routes. The active route is compiled to <name>-bridged.uktas, which the TAS player loads.");

            GUILayout.BeginHorizontal();
            Lbl("Path", "Where the .uktasproj project file lives.", GUILayout.Width(60));
            _projectPathText = Field(_projectPathText, "Project file path.", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Author", "Who made the project.", GUILayout.Width(60));
            _projectAuthorText = Field(_projectAuthorText, "Your name.", GUILayout.Width(160));
            Lbl("Target level", "The level this project is for.", GUILayout.Width(90));
            _projectLevelText = Field(_projectLevelText, "Level name.", GUILayout.Width(160));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (Btn("New project from current script", "Create a project file whose first route is the script in memory.", GUILayout.Width(220)))
                CreateProject();
            if (Btn("Open project", "Open the project at the path above.", GUILayout.Width(110)))
                OpenProject();
            if (Btn("Save project", "Write the project file and refresh the bridged script. Asks before overwriting an existing file.", GUILayout.Width(110)) && project != null)
            {
                BranchProject toSave = project;
                RequestOverwriteSave(toSave.ProjectPath, delegate
                {
                    toSave.SyncActive(_engine.Script);
                    toSave.Save();
                    toSave.WriteBridged();
                    _branchStatus = "Saved " + toSave.ProjectPath + ".";
                });
            }
            GUILayout.EndHorizontal();

            if (project == null)
            {
                Hint("No project open.");
                if (_branchStatus.Length > 0)
                    Hint(_branchStatus);
                EndCard();
                return;
            }

            UktasBranch active = project.Active;
            Hint("Project " + project.Data.Name + "  |  active route: " + (active != null ? active.Name : "none") +
                 "  |  bridged: " + System.IO.Path.GetFileName(project.BridgedPath));
            EndCard();

            BeginCard("Routes");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Route", _sectionStyle, GUILayout.Width(200));
            GUILayout.Label("Ticks", _sectionStyle, GUILayout.Width(80));
            GUILayout.Label("Time (s)", _sectionStyle, GUILayout.Width(110));
            GUILayout.Label("vs active", _sectionStyle, GUILayout.Width(170));
            GUILayout.EndHorizontal();

            _branchScroll = GUILayout.BeginScrollView(_branchScroll, GUILayout.Height(170));
            IList<UktasBranch> branches = project.Branches;
            for (int i = 0; i < branches.Count; i++)
            {
                UktasBranch branch = branches[i];
                bool isActive = active != null && branch.Id == active.Id;
                GUILayout.BeginHorizontal();
                GUILayout.Label((isActive ? "> " : "  ") + branch.Name, GUILayout.Width(200));
                GUILayout.Label(branch.TotalTicks.ToString(), GUILayout.Width(80));
                GUILayout.Label(branch.TotalSeconds.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), GUILayout.Width(110));
                GUILayout.Label(isActive ? "active" : BranchProject.FormatDelta(project.DeltaAgainstActive(branch)), GUILayout.Width(170));
                if (!isActive && Btn("Switch to", "Make this the active route and reload the bridged script.", GUILayout.Width(90)))
                    SwitchBranch(branch.Id);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            EndCard();

            BeginCard("Fork");
            GUILayout.BeginHorizontal();
            Lbl("Fork name", "Name of the new route.", GUILayout.Width(80));
            _forkNameText = Field(_forkNameText, "Route name.", GUILayout.Width(180));
            if (Btn("Fork at current time", "Start a new route that keeps every frame before this tick and continues from the live game state.", GUILayout.Width(170)))
                ForkAtCurrentTime();
            GUILayout.EndHorizontal();

            Hint("Current tick " + _engine.CurrentTick + " (t = " +
                 _engine.CurrentTime.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) +
                 " s). Forking keeps frames before this tick and continues from the live state.");
            if (_branchStatus.Length > 0)
                Hint(_branchStatus);
            EndCard();
        }

        void CreateProject()
        {
            TasScript script = _engine.Script;
            if (script == null)
            {
                _branchStatus = "Load or record a script before creating a project.";
                return;
            }
            string path = ResolveProjectPath(_projectPathText);
            try
            {
                int seed = script.Properties.Seed;
                _plugin.Project = BranchProject.Create(path, System.IO.Path.GetFileNameWithoutExtension(path),
                    _projectAuthorText, seed, _projectLevelText, script);
                _engine.SetScript(TasScript.Parse(_plugin.Project.Active.Script, "bridged"), _plugin.Project.BridgedPath);
                _branchStatus = "Created " + _plugin.Project.ProjectPath + ".";
            }
            catch (Exception ex)
            {
                _branchStatus = "Create failed: " + ex.Message;
            }
        }

        void OpenProject()
        {
            string path = ResolveProjectPath(_projectPathText);
            try
            {
                _plugin.Project = BranchProject.Open(path);
                _engine.SetScript(TasScript.Load(_plugin.Project.BridgedPath), _plugin.Project.BridgedPath);
                _branchStatus = "Opened " + path + ".";
            }
            catch (Exception ex)
            {
                _branchStatus = "Open failed: " + ex.Message;
            }
        }

        void SwitchBranch(string branchId)
        {
            BranchProject project = _plugin.Project;
            if (project == null)
                return;
            try
            {
                TasScript script = project.SwitchTo(branchId, _engine.Script);
                _engine.SetScript(script, project.BridgedPath);
                _branchStatus = "Active route: " + project.Active.Name + ". Bridged script reloaded.";
            }
            catch (Exception ex)
            {
                _branchStatus = "Switch failed: " + ex.Message;
            }
        }

        void ForkAtCurrentTime()
        {
            BranchProject project = _plugin.Project;
            if (project == null)
            {
                _branchStatus = "Create or open a project first.";
                return;
            }
            try
            {
                TasScript forked;
                int tick = _engine.CurrentTick;
                UktasBranch branch = project.Fork(_forkNameText, _engine.Script, tick, out forked);
                _engine.SetScriptKeepPosition(forked, project.BridgedPath);
                _branchStatus = "Forked '" + branch.Name + "' at tick " + tick + ". Continue recording or stepping from here.";
            }
            catch (Exception ex)
            {
                _branchStatus = "Fork failed: " + ex.Message;
            }
        }

        string ResolveProjectPath(string text)
        {
            string path = (text ?? string.Empty).Trim();
            if (path.Length == 0)
                path = "route" + BranchProject.ProjectExtension;
            if (!path.EndsWith(BranchProject.ProjectExtension, StringComparison.OrdinalIgnoreCase))
                path += BranchProject.ProjectExtension;
            if (!System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(_plugin.ScriptDirectory, path);
            return path;
        }

        // ------------------------------------------------------------------
        // Recording tab: offline renderer controls
        // ------------------------------------------------------------------

        static readonly int[] RecordSampleOptions = { 1, 4, 8, 16, 32 };
        static readonly int[] RecordFpsOptions = { 30, 60, 120 };

        void DrawRecording()
        {
            VideoRecorder video = _plugin.Video;
            if (_recordDirText == null)
                _recordDirText = _plugin.RecordingDirectory.Value;
            if (_ffmpegText == null)
                _ffmpegText = _plugin.FfmpegPath.Value;

            BeginCard("Offline video renderer");
            Hint("Each frame averages sub-frame renders across the shutter. Physics is not advanced, so jumps and timings match the live run. The HUD is not in the video.");

            GUILayout.BeginHorizontal();
            Lbl("Output folder", "Folder the video is written to. Empty uses the script folder's Recordings directory.", GUILayout.Width(110));
            _recordDirText = Field(_recordDirText, "Folder path.", GUILayout.ExpandWidth(true));
            if (_recordDirText != _plugin.RecordingDirectory.Value)
                _plugin.RecordingDirectory.Value = _recordDirText;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("File name", "Name of the video file, for example run.mp4.", GUILayout.Width(110));
            _recordFileText = Field(_recordFileText, "File name with extension.", GUILayout.Width(260));
            GUILayout.EndHorizontal();

            GUILayout.Label("Target video FPS");
            _recordFpsIndex = ChoiceRow(_recordFpsIndex, new[] { "30", "60", "120" },
                new[] { "30 frames per second.", "60 frames per second.", "120 frames per second." }, 0f);

            GUILayout.Label("Motion blur samples");
            _recordSampleIndex = ChoiceRow(_recordSampleIndex, new[] { "1x (no blur)", "4x", "8x", "16x", "32x" },
                new[]
                {
                    "No motion blur: one render per video frame.",
                    "4 sub-frames per tick. Light blur, fast.",
                    "8 sub-frames per tick (1000 fps virtual). A good default.",
                    "16 sub-frames per tick. Smooth blur, slower.",
                    "32 sub-frames per tick. Smoothest, slowest."
                }, 0f);

            GUILayout.BeginHorizontal();
            Lbl("Shutter angle", "How much of each frame the virtual shutter stays open. 180 degrees is the film look.", GUILayout.Width(110));
            _recordShutter = Slide(_recordShutter, 0.1f, 1f, "Shutter angle in degrees (36 to 360).", GUILayout.Width(200));
            GUILayout.Label(Mathf.RoundToInt(_recordShutter * 360f) + " deg", GUILayout.Width(70));
            GUILayout.EndHorizontal();

            Hint("Motion blur samples = rendered sub-frames per 0.008 s tick, with the camera interpolated between ticks (8x = 1000 fps virtual). Frames inside the shutter are averaged. 1x = no blur.");

            GUILayout.BeginHorizontal();
            Lbl("ffmpeg path", "Where ffmpeg.exe is. Empty searches the game folder, then PATH.", GUILayout.Width(110));
            _ffmpegText = Field(_ffmpegText, "Full path to ffmpeg.exe.", GUILayout.ExpandWidth(true));
            if (_ffmpegText != _plugin.FfmpegPath.Value)
                _plugin.FfmpegPath.Value = _ffmpegText;
            GUILayout.EndHorizontal();
            string ffmpeg = VideoRecorder.LocateFfmpeg(_plugin.FfmpegPath.Value, BepInEx.Paths.GameRootPath);
            Hint("ffmpeg: " + (ffmpeg != null ? "Found (" + ffmpeg + ")" : "Missing. Install ffmpeg or set the path above."));

            _plugin.RecordAudio.Value = Tog(_plugin.RecordAudio.Value,
                "Record audio (game sound follows game time; speakers are silent while recording)",
                "Capture the game's sound into the video. The speakers stay silent while rendering.");
            _plugin.RecordCleanRender.Value = Tog(_plugin.RecordCleanRender.Value,
                "Clean render while recording (hide TAS HUD, hitboxes, triggers, trajectory; show viewmodels)",
                "Hide every TAS overlay while the video renders, so only the game is filmed.");
            EndCard();

            BeginCard("Render");
            if (video != null && video.IsRecording)
            {
                GUILayout.Label("Frames encoded: " + video.FramesEncoded + "  (" + video.Fps + " fps, " + video.Samples + " samples)", _bodyStyle);
                if (BtnDanger("Stop Recording", "Stop rendering and finish the video file.", GUILayout.Width(160)))
                    video.Stop("stopped from the GUI");
            }
            else if (BtnPrimary("Start Recording", "Start rendering the run to a video with the settings above.", GUILayout.Width(160)) && video != null)
            {
                string directory = _plugin.RecordingDirectory.Value;
                if (string.IsNullOrEmpty(directory))
                    directory = System.IO.Path.Combine(_plugin.ScriptDirectory, "Recordings");
                string file = string.IsNullOrEmpty(_recordFileText) ? "run.mp4" : _recordFileText;
                if (!video.Start(System.IO.Path.Combine(directory, file),
                        RecordFpsOptions[_recordFpsIndex], RecordSampleOptions[_recordSampleIndex], _recordShutter))
                {
                    _plugin.Toast(video.LastError, true);
                }
            }

            if (video != null && video.Status != null && video.Status.Length > 0)
                Hint(video.Status);

            Hint("From a script: header lines record_video <file>, record_fps, blend_samples, shutter_angle (recording ends with the script). Commands: bxt_rec start [file], bxt_rec stop.");
            EndCard();
        }

        void DrawKeybinds()
        {
            BeginCard("Hotkeys");
            Hint("Click Rebind, then press a key; Escape cancels; right-click a row or its Rebind button to unbind it. These keys are read through legacy Input and never appear in the TAS.");

            KeybindRow("Toggle GUI", _plugin.KeyToggleGui, new KeyboardShortcut(KeyCode.Insert), "Show or hide this window.");
            KeybindRow("Play / Pause", _plugin.KeyPlayPause, new KeyboardShortcut(KeyCode.F5), "Play starts the script from the start, or resumes if paused; while it plays, pauses at the current tick.");
            KeybindRow("Step one frame", _plugin.KeyStep, new KeyboardShortcut(KeyCode.F6), "Advance exactly one tick while paused.");
            KeybindRow("Fast forward (hold)", _plugin.KeyFastForward, new KeyboardShortcut(KeyCode.F7), "Hold to run the script at full speed.");
            KeybindRow("Quick savestate", _plugin.KeySaveState, new KeyboardShortcut(KeyCode.F8), "Save the game state into the active slot.");
            KeybindRow("Quick loadstate", _plugin.KeyLoadState, new KeyboardShortcut(KeyCode.F9), "Restore the game state from the active slot.");
            KeybindRow("Start / stop recording", _plugin.KeyRecord, new KeyboardShortcut(KeyCode.F10), "Start or stop recording your input.");
            KeybindRow("Stop and release clock", _plugin.KeyStop, new KeyboardShortcut(KeyCode.F11), "Stop the run and hand the clock back to the game.");
            KeybindRow("Rewind to tick 0", _plugin.KeyRewind, new KeyboardShortcut(KeyCode.F4), "Go back to the start of the run.");
            KeybindRow("Reload script from disk and play", _plugin.KeyReloadPlay, new KeyboardShortcut(KeyCode.F3), "Read the script file again, then play it from the start. Shows a message if the file is missing.");
            KeybindRow("Toggle HUD", _plugin.KeyToggleHud, new KeyboardShortcut(KeyCode.F12), "Show or hide the on-screen info panel.");
            KeybindRow("Toggle freecam", _plugin.KeyToggleFreecam, new KeyboardShortcut(KeyCode.F2), "Fly a free camera around the level.");
            KeybindRow("Cycle camera mode", _plugin.KeyCameraMode, new KeyboardShortcut(KeyCode.F1), "Switch between first person, third person and freecam.");
            KeybindRow("Reset camera", _plugin.KeyCameraReset, new KeyboardShortcut(KeyCode.Home), "Put the camera back behind V1.");
            KeybindRow("Freecam cursor toggle", _plugin.KeyFreecamCursor, new KeyboardShortcut(KeyCode.LeftAlt), "Free the mouse cursor while the freecam is on.");
            KeybindRow("Freecam speed up", _plugin.KeyFreecamFaster, new KeyboardShortcut(KeyCode.Equals), "Fly faster.");
            KeybindRow("Freecam speed down", _plugin.KeyFreecamSlower, new KeyboardShortcut(KeyCode.Minus), "Fly slower.");
            KeybindRow("Undo last step", _plugin.KeyUndo, new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl), "Go back one tick and remove the last step-recorded frame.");
            EndCard();

            BeginCard("Menu injection keys");
            Hint("Used by the uppercase field 3 tokens U / D / L / R / C / B (up, down, left, right, confirm, cancel) to drive ULTRAKILL's menus from a script.");
            KeyCodeRow("Menu up", _plugin.MenuUpKey, KeyCode.UpArrow, "Key pressed for the U token.");
            KeyCodeRow("Menu down", _plugin.MenuDownKey, KeyCode.DownArrow, "Key pressed for the D token.");
            KeyCodeRow("Menu left", _plugin.MenuLeftKey, KeyCode.LeftArrow, "Key pressed for the L token.");
            KeyCodeRow("Menu right", _plugin.MenuRightKey, KeyCode.RightArrow, "Key pressed for the R token.");
            KeyCodeRow("Confirm", _plugin.MenuConfirmKey, KeyCode.Return, "Key pressed for the C token.");
            KeyCodeRow("Cancel", _plugin.MenuCancelKey, KeyCode.Escape, "Key pressed for the B token.");
            EndCard();

            BeginCard("Input injection");
            _plugin.InjectDeviceState.Value = Tog(_plugin.InjectDeviceState.Value,
                "Inject bound keys into the Input System (recommended; blocks physical input)",
                "Feed the script's keys to the game as if pressed on a keyboard. Your real keys are ignored during a run.");
            _plugin.EnforceCameraAngles.Value = Tog(_plugin.EnforceCameraAngles.Value,
                "Enforce camera pitch/yaw before CameraController runs",
                "Write the scripted look angles right before the game reads them, so aim is exact.");
            _plugin.BlockGameInputWhileGuiOpen.Value = Tog(_plugin.BlockGameInputWhileGuiOpen.Value,
                "Block gameplay input while this window is open",
                "Typing in this window cannot move V1 or turn the view.");
            GUILayout.BeginHorizontal();
            Lbl("PerformedFrame offset", "Added to the frame counter for one-shot actions. Raise to +1 if jumps or fire clicks do not register.", GUILayout.Width(170));
            int offset = _plugin.PerformedFrameOffset.Value;
            int parsedOffset;
            string offsetText = Field(offset.ToString(), "Whole number from -2 to 4.", GUILayout.Width(50));
            if (int.TryParse(offsetText, out parsedOffset))
                _plugin.PerformedFrameOffset.Value = Mathf.Clamp(parsedOffset, -2, 4);
            Hint("(raise to +1 if one-shot actions do not fire)");
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_rebindLabel))
                GUILayout.Label("Waiting for a key for: " + _rebindLabel + " ...", _sectionStyle);
            EndCard();
        }

        const string UnbindHint = "Right-click to unbind.";
        const string RebindTip = "Click, then press the new key. Escape cancels. Right-click to unbind.";

        /// <summary>
        /// Draws the Rebind button for a hotkey row. Left click reports true so the row waits
        /// for a key; a right click clears that binding and cancels a pending rebind on it.
        /// </summary>
        bool RebindBtn(ConfigEntry<KeyboardShortcut> entry)
        {
            Rect rect = GUILayoutUtility.GetRect(new GUIContent("Rebind"), GUI.skin.button, GUILayout.Width(80));
            if (RightClickedRebind(rect))
            {
                entry.Value = KeyboardShortcut.Empty;
                _plugin.ConfigFile.Save();
                if (_rebindEntry == entry)
                {
                    _rebindEntry = null;
                    _rebindLabel = string.Empty;
                }
                return false;
            }
            bool clicked = GUI.Button(rect, "Rebind");
            TipAt(rect, RebindTip);
            return clicked;
        }

        /// <summary>Same as the hotkey Rebind button, for the menu injection key rows.</summary>
        bool RebindBtn(ConfigEntry<KeyCode> entry)
        {
            Rect rect = GUILayoutUtility.GetRect(new GUIContent("Rebind"), GUI.skin.button, GUILayout.Width(80));
            if (RightClickedRebind(rect))
            {
                entry.Value = KeyCode.None;
                _plugin.ConfigFile.Save();
                if (_rebindKeyEntry == entry)
                {
                    _rebindKeyEntry = null;
                    _rebindLabel = string.Empty;
                }
                return false;
            }
            bool clicked = GUI.Button(rect, "Rebind");
            TipAt(rect, RebindTip);
            return clicked;
        }

        /// <summary>Right-click on a Rebind button. The press is consumed so no rebind starts.</summary>
        static bool RightClickedRebind(Rect rect)
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 1 || !rect.Contains(e.mousePosition))
                return false;
            e.Use();
            return true;
        }

        void TipAt(Rect rect, string text)
        {
            if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint || _shortcutsOpen)
                return;
            if (_tipClipActive && !_tipClip.Contains(_tipWindowMouse))
                return;
            if (rect.Contains(Event.current.mousePosition))
                _tipText = text;
        }

        /// <summary>True when a right mouse press lands on the row; the press is consumed so it never starts a rebind.</summary>
        bool RightClickedRow(Rect row)
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 1)
                return false;
            if (!row.Contains(e.mousePosition) || !_tipClip.Contains(_tipWindowMouse))
                return false;
            e.Use();
            return true;
        }

        void KeybindRow(string label, ConfigEntry<KeyboardShortcut> entry, KeyboardShortcut fallback, string tip)
        {
            GUILayout.BeginHorizontal();
            string fullTip = tip + " " + UnbindHint;
            Lbl(label, fullTip, GUILayout.Width(220));
            bool unbound = entry.Value.MainKey == KeyCode.None;
            GUILayout.Label(_rebindEntry == entry ? "[PRESS KEYBIND]" : (unbound ? "Unbound" : entry.Value.ToString()), GUILayout.Width(170));
            Tip(fullTip);
            if (RebindBtn(entry))
            {
                _rebindEntry = entry;
                _rebindKeyEntry = null;
                _rebindLabel = label;
            }
            if (Btn("Reset", "Go back to the default key.", GUILayout.Width(70)))
            {
                entry.Value = fallback;
                _plugin.ConfigFile.Save();
            }
            GUILayout.EndHorizontal();
            if (RightClickedRow(GUILayoutUtility.GetLastRect()))
            {
                entry.Value = KeyboardShortcut.Empty;
                _plugin.ConfigFile.Save();
                if (_rebindEntry == entry)
                {
                    _rebindEntry = null;
                    _rebindLabel = string.Empty;
                }
            }
        }

        void KeyCodeRow(string label, ConfigEntry<KeyCode> entry, KeyCode fallback, string tip)
        {
            GUILayout.BeginHorizontal();
            string fullTip = tip + " " + UnbindHint;
            Lbl(label, fullTip, GUILayout.Width(220));
            bool unbound = entry.Value == KeyCode.None;
            GUILayout.Label(_rebindKeyEntry == entry ? "[PRESS KEYBIND]" : (unbound ? "Unbound" : entry.Value.ToString()), GUILayout.Width(170));
            Tip(fullTip);
            if (RebindBtn(entry))
            {
                _rebindKeyEntry = entry;
                _rebindEntry = null;
                _rebindLabel = label;
            }
            if (Btn("Reset", "Go back to the default key.", GUILayout.Width(70)))
            {
                entry.Value = fallback;
                _plugin.ConfigFile.Save();
            }
            GUILayout.EndHorizontal();
            if (RightClickedRow(GUILayoutUtility.GetLastRect()))
            {
                entry.Value = KeyCode.None;
                _plugin.ConfigFile.Save();
                if (_rebindKeyEntry == entry)
                {
                    _rebindKeyEntry = null;
                    _rebindLabel = string.Empty;
                }
            }
        }

        void HandleRebind()
        {
            if (_rebindEntry == null && _rebindKeyEntry == null)
                return;
            Event current = Event.current;
            if (current == null)
                return;

            // Middle and side buttons: button 2 is "mouse3" (Unity Mouse2, middle), button 3 is
            // "mouse4" (Mouse3, back), button 4 is "mouse5" (Mouse4, forward).
            KeyCode key;
            if (current.type == EventType.MouseDown && current.button >= 2 && current.button <= 4)
                key = (KeyCode)((int)KeyCode.Mouse2 + current.button - 2);
            else if (current.type == EventType.KeyDown)
                key = current.keyCode;
            else
                return;
            if (key == KeyCode.None)
                return;
            if (key == KeyCode.Escape)
            {
                _rebindEntry = null;
                _rebindKeyEntry = null;
                _rebindLabel = string.Empty;
                current.Use();
                return;
            }
            if (key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                key == KeyCode.LeftAlt || key == KeyCode.RightAlt)
                return;

            if (_rebindEntry != null)
            {
                List<KeyCode> modifiers = new List<KeyCode>(3);
                if (current.control)
                    modifiers.Add(KeyCode.LeftControl);
                if (current.shift)
                    modifiers.Add(KeyCode.LeftShift);
                if (current.alt)
                    modifiers.Add(KeyCode.LeftAlt);
                _rebindEntry.Value = new KeyboardShortcut(key, modifiers.ToArray());
                _plugin.ConfigFile.Save();
            }
            else if (_rebindKeyEntry != null)
            {
                _rebindKeyEntry.Value = key;
                _plugin.ConfigFile.Save();
            }

            _rebindEntry = null;
            _rebindKeyEntry = null;
            _rebindLabel = string.Empty;
            current.Use();
        }

        // ------------------------------------------------------------------
        // Overlays / settings
        // ------------------------------------------------------------------

        int IntField(int value, int width, string tip = null)
        {
            string text = GUILayout.TextField(value.ToString(CultureInfo.InvariantCulture), GUILayout.Width(width));
            Tip(tip);
            int parsed;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : value;
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        void DrawDiagnostics()
        {
            BeginCard("Diagnostics");
            GUILayout.BeginHorizontal();
            if (Btn("Refresh report", "Rebuild the report below from the live game.", GUILayout.Width(120)))
                RequestDiagnostics(false);
            if (Btn("Write report to log", "Also copy the report into the BepInEx log file.", GUILayout.Width(150)))
                RequestDiagnostics(true);
            if (Btn("Save report", "Write the report to diagnostics.txt in the script folder.", GUILayout.Width(110)))
            {
                try
                {
                    string path = Path.Combine(_plugin.ScriptDirectory, "diagnostics.txt");
                    File.WriteAllText(path, _diagnostics);
                    _statusText = "Report saved to " + path;
                    _statusIsError = false;
                }
                catch (Exception ex)
                {
                    _statusText = ex.Message;
                    _statusIsError = true;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _diagnosticsScroll = GUILayout.BeginScrollView(_diagnosticsScroll, GUILayout.Height(360));
            GUILayout.Label(_diagnostics, _wrapStyle);
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            Hint(_cachedInputDescription);
            Hint(_cachedRngDescription);
            if (!string.IsNullOrEmpty(_engine.LastError))
                GUILayout.Label("Last engine error: " + _engine.LastError, _errorStyle);
            StatusLine();
            EndCard();
        }

        // ------------------------------------------------------------------
        // HUD + toast
        // ------------------------------------------------------------------

        // Every ULTRAKILL action, two rows. A gap is drawn before cells marked GapBefore.
        struct MatrixCell
        {
            public string Label;
            public TasButtons Mask;
            public float Width;
            public bool GapBefore;

            public MatrixCell(string label, TasButtons mask, float width, bool gap)
            {
                Label = label;
                Mask = mask;
                Width = width;
                GapBefore = gap;
            }
        }

        const TasButtons AnyFist = TasButtons.FistChange | TasButtons.FistFeedbacker | TasButtons.FistKnuckleblaster;

        // Movement and combat: W A S D | JUMP SLIDE DASH | F1 F2 PUNCH WHIP
        static readonly MatrixCell[] RowMove =
        {
            new MatrixCell("W", TasButtons.Forward, 18f, false),
            new MatrixCell("A", TasButtons.Left, 18f, false),
            new MatrixCell("S", TasButtons.Back, 18f, false),
            new MatrixCell("D", TasButtons.Right, 18f, false),
            new MatrixCell("JUMP", TasButtons.Jump, 34f, true),
            new MatrixCell("SLIDE", TasButtons.Slide, 38f, false),
            new MatrixCell("DASH", TasButtons.Dash, 34f, false),
            new MatrixCell("F1", TasButtons.Fire1, 22f, true),
            new MatrixCell("F2", TasButtons.Fire2, 22f, false),
            new MatrixCell("PUNCH", TasButtons.Punch, 40f, false),
            new MatrixCell("WHIP", TasButtons.Whiplash, 34f, false),
            new MatrixCell("STATS", TasButtons.Stats, 38f, true)
        };

        // Weapons and arms: 1-6 | FIST FEED KNUK | WHL+ WHL- LAST | VAR< VAR> V1 V2 V3
        static readonly MatrixCell[] RowWeapons =
        {
            new MatrixCell("1", TasButtons.Slot1, 18f, false),
            new MatrixCell("2", TasButtons.Slot2, 18f, false),
            new MatrixCell("3", TasButtons.Slot3, 18f, false),
            new MatrixCell("4", TasButtons.Slot4, 18f, false),
            new MatrixCell("5", TasButtons.Slot5, 18f, false),
            new MatrixCell("6", TasButtons.Slot6, 18f, false),
            new MatrixCell("FIST", TasButtons.FistChange, 30f, true),
            new MatrixCell("FEED", TasButtons.FistFeedbacker, 30f, false),
            new MatrixCell("KNUK", TasButtons.FistKnuckleblaster, 30f, false),
            new MatrixCell("WHL+", TasButtons.WeaponNext, 32f, true),
            new MatrixCell("WHL-", TasButtons.WeaponPrev, 32f, false),
            new MatrixCell("LAST", TasButtons.WeaponLast, 32f, false),
            new MatrixCell("VAR<", TasButtons.VariationPrev, 32f, true),
            new MatrixCell("VAR>", TasButtons.VariationNext, 32f, false),
            new MatrixCell("V1", TasButtons.Variant1, 20f, false),
            new MatrixCell("V2", TasButtons.Variant2, 20f, false),
            new MatrixCell("V3", TasButtons.Variant3, 20f, false)
        };

        /// <summary>One row of cells. No allocation: labels and widths are static.</summary>
        void DrawMatrixRow(float x, float y, string caption, MatrixCell[] cells, TasButtons held, Color lit)
        {
            Color previous = GUI.color;
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 4f, y, 44f, 18f), caption, _panelSmallStyle);
            float cursor = x + 46f;
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i].GapBefore)
                    cursor += 8f;
                GUI.color = (held & cells[i].Mask) != 0 ? new Color(lit.r, lit.g, lit.b, _hudAlpha) : new Color(1f, 1f, 1f, 0.35f * _hudAlpha);
                GUI.Box(new Rect(cursor, y, cells[i].Width, 18f), cells[i].Label, _matrixStyle);
                cursor += cells[i].Width + 2f;
            }
            GUI.color = previous;
        }

        /// <summary>Pointer row: live position and the three mouse buttons.</summary>
        void DrawPointerRow(float x, float y, Color lit)
        {
            Color previous = GUI.color;
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 4f, y, 44f, 18f), "mouse", _panelSmallStyle);
            float cursor = x + 46f;
            GUI.Label(new Rect(cursor, y, 210f, 18f), _cachedPointerText, _panelSmallStyle);
            cursor += 212f;
            GUI.color = _input.LiveMouseLeft ? new Color(lit.r, lit.g, lit.b, _hudAlpha) : new Color(1f, 1f, 1f, 0.35f * _hudAlpha);
            GUI.Box(new Rect(cursor, y, 30f, 18f), "LMB", _matrixStyle);
            cursor += 32f;
            GUI.color = _input.LiveMouseRight ? new Color(lit.r, lit.g, lit.b, _hudAlpha) : new Color(1f, 1f, 1f, 0.35f * _hudAlpha);
            GUI.Box(new Rect(cursor, y, 30f, 18f), "RMB", _matrixStyle);
            cursor += 32f;
            GUI.color = _input.LiveMouseMiddle ? new Color(lit.r, lit.g, lit.b, _hudAlpha) : new Color(1f, 1f, 1f, 0.35f * _hudAlpha);
            GUI.Box(new Rect(cursor, y, 30f, 18f), "MMB", _matrixStyle);
            GUI.color = previous;
        }
    }
}
