using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>One user-visible message shown by the GUI toast and written to the log.</summary>
    public sealed class TasMessage
    {
        public string Text = string.Empty;
        public bool IsError;
        public float Time;
    }

    /// <summary>
    /// BepInEx entry point.
    ///
    /// Execution order -30000 makes Update run before ULTRAKILL's own scripts, so
    /// a tick's input is in place before NewMovement/CameraController read it.
    /// A separate FrameEndHook at +32000 arms the next frame's clock after every
    /// game LateUpdate, which is what makes pause/step exact instead of one frame
    /// late (Unity samples Time settings before MonoBehaviour callbacks).
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    [BepInProcess("ULTRAKILL.exe")]
    [DefaultExecutionOrder(-30000)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.tas.ultrakill";
        public const string PluginName = "ULTRAKILL TAS Tool";
        public const string PluginVersion = "1.1.0";

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// State holder used by <see cref="TasRuntime"/>. It survives the BepInEx
        /// host component being destroyed mid-session, because every subsystem
        /// (engine, GUI, clock, savestates) hangs off this object and all of them
        /// are plain managed code.
        /// </summary>
        public static Plugin State { get; private set; }

        public static bool Quitting { get { return _quitting; } }

        /// <summary>The clock, so a savestate can carry its fixed-step phase.</summary>
        public TimeController Clock { get { return _time; } }

        /// <summary>The input injector, so a savestate can carry the held-button edge state.</summary>
        public InputPatcher Injector { get { return _input; } }

        static bool _quitting;
        int _tickFrame = -1;

        GameBridge _bridge;
        RngController _rng;
        TimeController _time;
        InputPatcher _input;
        SaveStateManager _states;
        TasEngine _engine;
        GuiManager _gui;
        FrameEndHook _frameEnd;
        GameObject _frameEndObject;
        bool _fastForwardKeyHeld;
        bool _loggedUpdate;
        bool _loggedOnGui;
        bool _loggedInsert;
        bool _guiWasVisible;
        bool _freecamCursorWasFree;
        bool _sceneHooked;

        public readonly List<TasMessage> Messages = new List<TasMessage>(64);
        public string ScriptDirectory { get; private set; }

        // ------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------

        public ConfigEntry<string> Autoload;
        public ConfigEntry<bool> Autoplay;
        public ConfigEntry<float> DefaultHostFrametime;
        public ConfigEntry<bool> FrameLockedPlayback;
        public ConfigEntry<int> DefaultSeed;
        public ConfigEntry<int> PhysicsSubsteps;
        public ConfigEntry<string> RecordingFile;
        public ConfigEntry<string> LastScriptFile;
        public ConfigEntry<bool> CaptureStartState;
        public ConfigEntry<bool> PauseOnSceneChange;
        public ConfigEntry<bool> ForceSystemRandomSeeds;
        public ConfigEntry<bool> DeterministicPhysics;
        public ConfigEntry<bool> AlignFixedStepPhase;
        public ConfigEntry<bool> TraceDeterminism;
        public ConfigEntry<bool> SavestateCleanupSpawned;
        public ConfigEntry<bool> RespawnDynamicObjects;
        public ConfigEntry<bool> BlockGameInputWhileGuiOpen;
        public ConfigEntry<bool> InjectDeviceState;
        public ConfigEntry<bool> EnforceCameraAngles;
        public ConfigEntry<int> PerformedFrameOffset;
        public ConfigEntry<int> ActiveStateSlot;
        public ConfigEntry<string> PlayStartMode;
        public ConfigEntry<bool> RestartLevelOnPlay;
        public ConfigEntry<string> PlayStartLevel;
        public ConfigEntry<int> PlayStartStateSlot;
        public ConfigEntry<bool> GuiVisibleOnStart;
        public ConfigEntry<bool> UnlockAllWeapons;
        public ConfigEntry<bool> WaitForPlayerControl;
        public ConfigEntry<int> SceneSettleFrames;
        public ConfigEntry<float> FreeLookSensitivity;
        public ConfigEntry<float> FreecamSpeed;
        public ConfigEntry<bool> FreecamShowV1Body;
        public ConfigEntry<bool> TrajectoryEnabled;
        public ConfigEntry<int> TrajectoryVisibilityMode;
        public ConfigEntry<bool> TrajectoryGradient;
        public ConfigEntry<string> TrajectoryColor;
        public ConfigEntry<float> TrajectoryThickness;
        public ConfigEntry<float> TrajectoryOpacity;
        public ConfigEntry<int> TrajectoryMarkerEvery;
        public ConfigEntry<int> TrajectoryMaxNodes;
        public ConfigEntry<bool> TrajectoryHoverHitbox;
        public ConfigEntry<bool> CoinPathEnabled;
        public ConfigEntry<float> CoinPathOpacity;
        public ConfigEntry<float> CoinPathThickness;
        public ConfigEntry<float> ThirdPersonDistance;
        public ConfigEntry<float> ThirdPersonHeight;
        public ConfigEntry<string> FfmpegPath;
        public ConfigEntry<string> RecordingDirectory;
        public ConfigEntry<bool> RecordCleanRender;
        public ConfigEntry<bool> RecordAudio;        public ConfigEntry<float> KillPlaneY;
        public ConfigEntry<bool> StepRecord;
        public ConfigEntry<int> RollingHistoryTicks;
        public ConfigEntry<bool> HistoryDuringPlayback;
        public ConfigEntry<bool> AutoSaveToDisk;
        public ConfigEntry<bool> AutoReloadOnPlay;
        public ConfigEntry<bool> AutoShowLeaderboard;
        public ConfigEntry<bool> AutoEnableCheats;
        public ConfigEntry<bool> PauseOnDeathKillPlane;
        public ConfigEntry<bool> UseNorefresh;
        public ConfigEntry<bool> NoRefreshFastForward;
        public ConfigEntry<bool> PauseAtNoRefreshBreak;
        public ConfigEntry<bool> ShowHud;
        public ConfigEntry<bool> HudShowSpeed;
        public ConfigEntry<bool> HudShowVelocity;
        public ConfigEntry<bool> HudShowAngles;
        public ConfigEntry<bool> HudShowVitals;
        public ConfigEntry<bool> HudShowRng;
        public ConfigEntry<bool> HudShowInputs;
        public ConfigEntry<bool> HudShowProgress;
        public ConfigEntry<bool> HudShowPosition;
        public ConfigEntry<bool> HudShowState;
        public ConfigEntry<bool> HudShowWeapon;
        public ConfigEntry<bool> HudShowInputDisplay;
        public ConfigEntry<bool> HudShowPointer;
        public ConfigEntry<bool> HudEntityInspector;
        public ConfigEntry<bool> HudShowHit;
        public ConfigEntry<bool> HudShowLevelStats;
        public ConfigEntry<bool> ShowTriggers;
        public ConfigEntry<float> ShowTriggersDistance;
        public ConfigEntry<bool> ShowTriggersSolid;
        public ConfigEntry<float> ShowTriggersOpacity;
        public ConfigEntry<bool> HideViewmodels;
        public ConfigEntry<bool> ShowHitboxes;
        public ConfigEntry<bool> ShowPlayerHitbox;
        public ConfigEntry<string> HudCorner;
        public ConfigEntry<int> HudOffsetX;
        public ConfigEntry<int> HudOffsetY;
        public ConfigEntry<float> HudScale;
        public ConfigEntry<float> HudOpacity;
        public ConfigEntry<bool> HudBackground;
        public ConfigEntry<string> HudColor;
        public ConfigEntry<bool> HudGameFont;

        public ConfigEntry<bool> SpeedoEnabled;
        public ConfigEntry<string> SpeedoMode;
        public ConfigEntry<int> SpeedoDecimals;
        public ConfigEntry<int> SpeedoSize;
        public ConfigEntry<int> SpeedoOffsetX;
        public ConfigEntry<int> SpeedoOffsetY;
        public ConfigEntry<string> SpeedoColor;
        public ConfigEntry<bool> SpeedoColorByChange;
        public ConfigEntry<bool> SpeedoShadow;
        public ConfigEntry<bool> SpeedoUnit;
        public ConfigEntry<bool> SpeedoShowVertical;
        public ConfigEntry<bool> SpeedoShowPeak;
        public ConfigEntry<float> SpeedoOpacity;

        public ConfigEntry<bool> PausedLabelEnabled;
        public ConfigEntry<int> PausedLabelSize;
        public ConfigEntry<int> PausedLabelOffsetY;

        public ConfigEntry<KeyboardShortcut> KeyToggleGui;
        public ConfigEntry<KeyboardShortcut> KeyPlayPause;
        public ConfigEntry<KeyboardShortcut> KeyStep;
        public ConfigEntry<KeyboardShortcut> KeyFastForward;
        public ConfigEntry<KeyboardShortcut> KeySaveState;
        public ConfigEntry<KeyboardShortcut> KeyLoadState;
        public ConfigEntry<KeyboardShortcut> KeyRecord;
        public ConfigEntry<KeyboardShortcut> KeyStop;
        public ConfigEntry<KeyboardShortcut> KeyRewind;
        public ConfigEntry<KeyboardShortcut> KeyToggleHud;
        public ConfigEntry<KeyboardShortcut> KeyToggleFreecam;
        public ConfigEntry<KeyboardShortcut> KeyCameraMode;
        public ConfigEntry<KeyboardShortcut> KeyCameraReset;
        public ConfigEntry<KeyboardShortcut> KeyFreecamCursor;
        public ConfigEntry<KeyboardShortcut> KeyFreecamFaster;
        public ConfigEntry<KeyboardShortcut> KeyFreecamSlower;
        public ConfigEntry<KeyboardShortcut> KeyUndo;
        public ConfigEntry<KeyboardShortcut> KeyReloadPlay;

        public ConfigEntry<KeyCode> MenuUpKey;
        public ConfigEntry<KeyCode> MenuDownKey;
        public ConfigEntry<KeyCode> MenuLeftKey;
        public ConfigEntry<KeyCode> MenuRightKey;
        public ConfigEntry<KeyCode> MenuConfirmKey;
        public ConfigEntry<KeyCode> MenuCancelKey;

        public ConfigFile ConfigFile { get { return Config; } }
        public GuiManager Gui { get { return _gui; } }
        /// <summary>SAR-style player path: recording, world drawing, hover and click.</summary>
        public TrajectoryManager Trajectory { get; private set; }
        /// <summary>Coin flight paths. 'clearcoinpaths' (field 8) wipes them.</summary>
        public CoinPathManager CoinPaths { get; private set; }
        /// <summary>BXT-style offline renderer with sub-frame exposure blending.</summary>
        public VideoRecorder Video { get; private set; }
        /// <summary>First person, third person and freecam views.</summary>
        public TasCameraController Cameras { get; private set; }
        /// <summary>True while a recording with Clean render on is running: TAS overlays and messages are hidden.</summary>
        public bool CleanRecordingActive
        {
            get { return Video != null && Video.IsRecording && RecordCleanRender != null && RecordCleanRender.Value; }
        }

        /// <summary>The open .uktasproj workspace, or null.</summary>
        public BranchProject Project { get; set; }
        public TasEngine Engine { get { return _engine; } }
        public GameBridge Bridge { get { return _bridge; } }
        /// <summary>The RNG controller; baking records and applies the same RNG set as a savestate through it.</summary>
        public RngController Rng { get { return _rng; } }
        public bool FreecamActive { get { return _input != null && _input.FreecamActive; } }

        /// <summary>The freecam view pose; false when freecam is off or its camera is gone.</summary>
        public bool TryGetFreecamPose(out Vector3 position, out Quaternion rotation)
        {
            if (_input != null)
                return _input.TryGetFreecamPose(out position, out rotation);
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>Game camera at the freecam view pose for the scope; see TasCameraController.ApplyView.</summary>
        public TasCameraController.ViewScope ViewPose()
        {
            return Cameras != null ? Cameras.ApplyView() : new TasCameraController.ViewScope();
        }

        public void ToggleFreecam()
        {
            SetFreecam(!FreecamActive);
        }

        public void SetFreecam(bool enabled)
        {
            if (_input == null)
                return;
            if (!enabled)
            {
                if (_input.FreecamActive)
                {
                    _input.EndFreecam();
                    Toast("Freecam off.", false);
                }
                return;
            }
            if (_input.FreecamActive)
                return;
            if (!_input.BeginFreecam())
            {
                Toast("Freecam: no game camera is loaded.", true);
                return;
            }
            Toast("Freecam on: WASD, Space up, Q down; Shift fast, Ctrl slow.", false);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        void Awake()
        {
            Instance = this;
            State = this;
            ReadConfiguration();

            ScriptDirectory = Path.Combine(Paths.ConfigPath, "ULTRAKILL-TAS");
            try
            {
                Directory.CreateDirectory(ScriptDirectory);
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not create script directory: " + ex.Message);
            }

            _bridge = new GameBridge(this);
            WeaponUnlock.UnlockAll = UnlockAllWeapons.Value;
            FistHudDump.InstallTrace(); // TEMP diagnostic: traces who hides the fist HUD
            _rng = new RngController(this, _bridge);
            _rng.Initialize(ForceSystemRandomSeeds.Value);

            _time = new TimeController(this, _bridge);
            _time.SaveOriginals();
            _time.PhysicsSubsteps = Mathf.Clamp(PhysicsSubsteps.Value, 1, 8);
            _time.SetHostFrametime(DefaultHostFrametime.Value);
            // Game hitstops count down per TAS tick instead of on the wall clock.
            TimeControllerHitstopPatch.Install(this, _time);
            // Spawn door savestate hooks and the GUI's game helpers (leaderboard, cheats, death restart guard).
            SpawnDoorTracker.Install(this);
            GameToggles.Install(this);

            _input = new InputPatcher(this, _bridge);
            _input.Initialize();

            _states = new SaveStateManager(this, _bridge, _rng);
            _states.ResetHitstop = _time.ResetHitstop;
            _engine = new TasEngine(this, _bridge, _time, _input, _states, _rng);
            _gui = new GuiManager(this, _engine, _time, _states, _bridge, _input, _rng);
            Trajectory = new TrajectoryManager(this);
            CoinPaths = new CoinPathManager(this);
            Video = new VideoRecorder(this);
            Cameras = new TasCameraController(this);

            CreateFrameEndObject();

            HookSceneEvents();

            LogInfo(PluginName + " " + PluginVersion + " loaded.");
            LogInfo("Scripts: " + ScriptDirectory);
            LogInfo("Unity " + Application.unityVersion + ", game path " + Paths.GameRootPath);

            // On-screen proof that BepInEx and this plugin are alive: the window
            // itself stays hidden until Insert, so without this a working install
            // looks exactly like a game that never loaded BepInEx at all.
            Toast(PluginName + " " + PluginVersion + " loaded. Insert opens the TAS window.", false);

            ApplyStartupScript();

            // Own the per-frame driver instead of relying on BepInEx's host
            // object staying alive; see TasRuntime.
            TasRuntime.Install(this);
        }

        void CreateFrameEndObject()
        {
            _frameEndObject = new GameObject("ULTRAKILL_TAS_FrameEnd");
            UnityEngine.Object.DontDestroyOnLoad(_frameEndObject);
            _frameEnd = _frameEndObject.AddComponent<FrameEndHook>();
            _frameEnd.OnEndFrame = OnEndFrame;
        }

        /// <summary>
        /// Make sure scene changes reach the engine.
        ///
        /// This is idempotent and static-event based on purpose: if the BepInEx
        /// host component is destroyed the subscription must survive, otherwise
        /// the engine waits for a sceneLoaded that never arrives and every Play
        /// with load_level/restart times out.
        /// </summary>
        public void HookSceneEvents()
        {
            if (_sceneHooked)
                return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHooked = true;
        }

        /// <summary>Recreate the frame-end hook if something destroyed it.</summary>
        public void EnsureFrameEnd()
        {
            if (_frameEndObject == null)
            {
                CreateFrameEndObject();
                LogWarning("Frame-end hook was missing; recreated it.");
            }
        }

        void ReadConfiguration()
        {
            string general = "1. General";
            string timing = "2. Timing";
            string keys = "3. Keybinds";
            string menu = "4. Menu keys";
            string overlay = "5. Overlays";
            string advanced = "6. Advanced";

            Autoload = Config.Bind(general, "Autoload script", string.Empty,
                "Path to a .uktas or .hltas file loaded at boot. Relative paths resolve to the script directory. Empty = disabled.");
            Autoplay = Config.Bind(general, "Autoplay on boot", false,
                "Start playing the autoload script as soon as BepInEx creates the plugin (splash screen / main menu).");
            GuiVisibleOnStart = Config.Bind(general, "Show GUI on start", false,
                "Show the TAS window immediately. Insert toggles it at any time.");
            UnlockAllWeapons = Config.Bind(general, "Unlock all weapons", false,
                "Report every weapon and arm as owned, in memory only. Save files are never written. A script's weapons header takes priority while it plays.");
            WaitForPlayerControl = Config.Bind(general, "Wait for player control", true,
                "Hold tick 1 while NewMovement.activated is false (elevator drops and intro cutscenes).");
            SceneSettleFrames = Config.Bind(general, "Scene settle frames", 2,
                "Rendered frames to wait after control is granted for room triggers and occlusion culling.");
            FreeLookSensitivity = Config.Bind(general, "Free-look sensitivity", 2.5f,
                "Degrees of camera rotation per unit of raw mouse movement while paused.");
            FreecamSpeed = Config.Bind(general, "Freecam speed", 12f,
                "Freecam movement in units per second. Shift is 4x faster, Ctrl is 4x slower.");
            FreecamShowV1Body = Config.Bind(general, "Show V1 playermodel in freecam", false,
                "While freecam is on, enable V1's hidden body renderers so the full model is visible.");
            TrajectoryEnabled = Config.Bind("Trajectory", "Record and draw path", true,
                "Sample the player once per simulation tick and draw the path in the world.");
            TrajectoryVisibilityMode = Config.Bind("Trajectory", "Visibility (0 always, 1 freecam only, 2 hidden)", 0,
                "When the path is drawn.");
            TrajectoryGradient = Config.Bind("Trajectory", "Speed gradient", true,
                "Colour by speed (green slow, yellow medium, magenta fast). Off uses the single colour.");
            TrajectoryColor = Config.Bind("Trajectory", "Single colour", "#40FF60",
                "Hex colour used when the speed gradient is off.");
            TrajectoryThickness = Config.Bind("Trajectory", "Line thickness", 2f,
                "Parallel GL lines drawn per segment (1 to 6).");
            TrajectoryOpacity = Config.Bind("Trajectory", "Opacity", 0.85f,
                "Line alpha, 0 to 1.");
            TrajectoryMarkerEvery = Config.Bind("Trajectory", "Marker every N ticks", 0,
                "Draw a small cross every N ticks. 0 turns markers off.");
            TrajectoryMaxNodes = Config.Bind("Trajectory", "Maximum nodes", 200000,
                "Oldest nodes are dropped past this count.");
            TrajectoryHoverHitbox = Config.Bind("Trajectory", "Show player hitbox on hover", true,
                "Draw the same local player bounding box as Show player hitbox, at the tick under the path cursor.");
            CoinPathEnabled = Config.Bind("Coin paths", "Show coin paths", true,
                "Record every coin once per simulation tick and draw its flight path. Yellow: in flight. Green: punched coin. Cyan: shot coin. Red: shot back by an enemy. Magenta: tracking an enemy. Clear with the 'clearcoinpaths' script command.");
            CoinPathOpacity = Config.Bind("Coin paths", "Opacity", 0.9f,
                "Coin path alpha, 0 to 1.");
            CoinPathThickness = Config.Bind("Coin paths", "Line thickness", 2f,
                "Parallel GL lines drawn per path segment (1 to 6).");
            ThirdPersonDistance = Config.Bind("Camera", "Third person distance", 4f,
                "Distance behind V1's head in units.");
            ThirdPersonHeight = Config.Bind("Camera", "Third person height", 0.5f,
                "Height added above V1's head in third person.");
            FfmpegPath = Config.Bind("Recording", "ffmpeg path", string.Empty,
                "Full path to ffmpeg.exe. Empty searches the game folder, then PATH.");
            RecordingDirectory = Config.Bind("Recording", "Output directory", string.Empty,
                "Folder for recordings. Empty uses ScriptDirectory/Recordings.");
            RecordAudio = Config.Bind("Recording", "Record audio", true,
                "Capture game audio with Unity's offline mixer (silent on speakers while recording) and mux it into the video.");
            RecordCleanRender = Config.Bind("Recording", "Clean render while recording", true,
                "While recording, hide the TAS HUD, hitboxes, triggers and trajectory path and show viewmodels. Restored when the recording ends.");
            KillPlaneY = Config.Bind(general, "Kill plane Y", -100f,
                "Player positions below this Y are treated as out of bounds; savestates are refused and diagnostics warn.");
            StepRecord = Config.Bind(general, "Step-record staged input", false,
                "When enabled, F6 writes the staged input as the next one-tick frame before advancing.");
            RollingHistoryTicks = Config.Bind(general, "Rolling history ticks", SaveStateManager.DefaultHistoryTicks,
                "How many per-tick snapshots the in-memory ring buffer keeps for Undo Step (Ctrl+Z) and scrubbing.");
            HistoryDuringPlayback = Config.Bind(general, "Rolling history while playing", false,
                "Also snapshot every tick of continuous playback (not only F6 steps). Costs one capture per tick.");
            UseNorefresh = Config.Bind(general, "Use norefresh checkpoints", true,
                "When a script has norefresh lines and a cached state exists for one of them in the level that is loaded, Play restores that state and skips everything before it, including load_level. Otherwise the script plays normally.");
            NoRefreshFastForward = Config.Bind(general, "Enable FFF NoRefresh Fast-Forward", true,
                "Run a script's FFF segments headless (GoldSrc r_norefresh): every tick before an FFF marker simulates with all cameras' culling masks cleared, so no 3D geometry, shadows or lighting reach the GPU, at uncapped CPU speed. Rendering comes back on the framebulk immediately after the marker. F5 or F11 aborts at the current tick.");
            PauseAtNoRefreshBreak = Config.Bind(general, "Pause at the FFF breakpoint", false,
                "When a NoRefresh run reaches its FFF breakpoint, pause playback at that exact tick instead of continuing with normal rendering.");
            AutoReloadOnPlay = Config.Bind(general, "Reload script on Play", true,
                "When the script file changed on disk since it was loaded or saved, Play (F5) reloads it first. Edit the .uktas in any editor and press Play; no need to click Load.");
            AutoShowLeaderboard = Config.Bind(general, "Auto-show leaderboard", false,
                "Keep the in-game level stats / leaderboard panel (the one Tab shows) open automatically, also on a level that has no rank yet.");
            AutoEnableCheats = Config.Bind(general, "Auto-enable cheats", false,
                "Switch the game's cheats on as soon as a level loads, as if the cheat code had been typed and the confirmation answered yes. Marks the level as cheated, like the game does.");
            PauseOnDeathKillPlane = Config.Bind(general, "Pause on death (kill plane)", false,
                "On: playback pauses when the player falls below the kill plane, and a death may restart the level as in the game. Off: a death is only recorded and shown in the HUD; the level is never reloaded by itself (load a savestate, rewind or reload the script).");
            AutoSaveToDisk = Config.Bind(general, "Auto-save to disk", false,
                "Write the active script to disk after step-record edits and when recording stops.");
            ActiveStateSlot = Config.Bind(general, "Active savestate slot", 0,
                "Slot used by the quick save/load hotkeys (0-9). Scripts use load_state/save_state with explicit slots.");
            PlayStartMode = Config.Bind(general, "On Play", "script",
                "Startup override: script (use header), current, restart, level, state, or menu (title screen). The GUI Startup tab edits this.");
            RestartLevelOnPlay = Config.Bind(general, "Restart Level on Play", false,
                "When true, every Play restarts the current mission before frame 1 (overrides the other startup choices).");
            PlayStartLevel = Config.Bind(general, "Startup level scene", string.Empty,
                "Scene name used when On Play = level. Empty falls back to the script header's load_level.");
            PlayStartStateSlot = Config.Bind(general, "Startup savestate slot", 0,
                "Slot restored before frame 1 when On Play = state (0-9).");
            RecordingFile = Config.Bind(general, "Recording file", "recording.uktas",
                "File name (or absolute path) a recording is saved to when no script file is loaded. Relative names go to the script directory.");
            LastScriptFile = Config.Bind(general, "Last script file", string.Empty,
                "Script Editor path remembered across launches. Updated whenever a script is loaded, saved or created.");

            DefaultHostFrametime = Config.Bind(timing, "host_frametime", (float)TasProperties.DefaultHostFrametime,
                "Virtual seconds per TAS tick. 0.008 = 125 ticks/s and one native physics step (default). 0.016 = two native steps per tick. Time.fixedDeltaTime is never changed.");
            FrameLockedPlayback = Config.Bind(timing, "Frame-locked playback", true,
                "GoldSrc host_framerate behaviour: every rendered frame advances the script by exactly one tick of host_frametime, so a slow render means slow game time. Playback speed caps the frame rate to speed / host_frametime (1.0 = real time). Above 1.0 the cap is lifted.");
            PhysicsSubsteps = Config.Bind(timing, "Physics substeps", 1,
                "Deprecated and ignored: the clock always keeps ULTRAKILL's native 0.008 s physics step. Kept so old config files still load.");

            DefaultSeed = Config.Bind(advanced, "Default seed", 0,
                "Seed written into new scripts and used before a script is loaded.");
            CaptureStartState = Config.Bind(advanced, "Capture start state on play", true,
                "Snapshot the game when Play starts so Rewind can restore it. Menu runs have no player and therefore no start state.");
            PauseOnSceneChange = Config.Bind(advanced, "Pause on scene change", false,
                "Pause playback when a new scene loads instead of continuing the script across the transition.");
            ForceSystemRandomSeeds = Config.Bind(advanced, "Force deterministic System.Random seeds", true,
                "Patch System.Random(int) so game-created Random instances get a reproducible seed stream.");
            DeterministicPhysics = Config.Bind(advanced, "Deterministic physics", true,
                "While a TAS is active: bodies never fall asleep (Physics.sleepThreshold = 0) and transform changes reach the physics scene immediately (Physics.autoSyncTransforms). Removes run-to-run differences from sleeping bodies and delayed syncs. Restored when the TAS stops.");
            AlignFixedStepPhase = Config.Bind(advanced, "Align physics step phase", true,
                "Before Play reloads the level, move Unity's leftover fixed-step time to a fixed value so every run starts at the same point in the 0.008 s physics step. Without it the leftover from the previous run shifts which tick a step lands on, and 0.001 s ticks give a different result each run.");
            TraceDeterminism = Config.Bind(advanced, "Trace determinism", false,
                "Write determinism_trace_<time>.csv next to the plugin DLL on every run: player and moving rigidbody state per tick. Run a script twice and compare the files; the first differing row is where the physics diverged.");
            SavestateCleanupSpawned = Config.Bind(advanced, "Savestate cleanup spawned objects", true,
                "On loadstate, destroy coins/projectiles created after the save so a reload does not keep stray rockets and coins. Turn off if the game pools objects.");
            RespawnDynamicObjects = Config.Bind(advanced, "Respawn destroyed projectiles on load", true,
                "Rebuild grenades, rockets, coins and other projectiles that were destroyed after the save: one deactivated template per projectile type is kept while the scene is loaded.");
            BlockGameInputWhileGuiOpen = Config.Bind(advanced, "Block gameplay input while GUI is open", true,
                "Withhold the player's real keyboard and mouse input from the game whenever the TAS window is open, so typing, clicking in it or just moving the mouse cannot move the player, fire weapons or turn the view. Scripted input is not affected.");
            InjectDeviceState = Config.Bind(advanced, "Inject Input System device state", true,
                "Queue keyboard/mouse states for the game's currently bound keys. This is what suppresses physical input.");
            EnforceCameraAngles = Config.Bind(advanced, "Enforce camera angles", true,
                "Patch CameraController.Update/LateUpdate to write the scripted pitch/yaw immediately before the game reads them.");
            PerformedFrameOffset = Config.Bind(advanced, "PerformedFrame offset", 0,
                "Value added to Time.frameCount for InputSource.PerformedFrame. Try +1 if one-shot actions do not trigger.");

            KeyToggleGui = BindKey(keys, "Toggle GUI", KeyCode.Insert);
            KeyPlayPause = BindKey(keys, "Play / Pause", KeyCode.F5);
            KeyStep = BindKey(keys, "Step one frame", KeyCode.F6);
            KeyFastForward = BindKey(keys, "Fast forward (hold)", KeyCode.F7);
            KeySaveState = BindKey(keys, "Quick savestate", KeyCode.F8);
            KeyLoadState = BindKey(keys, "Quick loadstate", KeyCode.F9);
            KeyRecord = BindKey(keys, "Start / stop recording", KeyCode.F10);
            KeyStop = BindKey(keys, "Stop and release clock", KeyCode.F11);
            KeyRewind = BindKey(keys, "Rewind to tick 0", KeyCode.F4);
            KeyReloadPlay = BindKey(keys, "Reload script from disk and play", KeyCode.F3);
            KeyToggleHud = BindKey(keys, "Toggle HUD", KeyCode.F12);
            KeyToggleFreecam = BindKey(keys, "Toggle freecam", KeyCode.F2);
            KeyCameraMode = BindKey(keys, "Cycle camera mode", KeyCode.F1);
            KeyCameraReset = BindKey(keys, "Reset camera", KeyCode.Home);
            KeyFreecamCursor = BindKey(keys, "Freecam cursor toggle", KeyCode.LeftAlt);
            KeyFreecamFaster = BindKey(keys, "Freecam speed up", KeyCode.Equals);
            KeyFreecamSlower = BindKey(keys, "Freecam speed down", KeyCode.Minus);
            KeyUndo = Config.Bind(keys, "Undo last step",
                new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl),
                "Restore the previous tick state and remove the last step-recorded frame.");

            MenuUpKey = Config.Bind(menu, "Menu up", KeyCode.UpArrow, "Key injected for the U token (field 3).");
            MenuDownKey = Config.Bind(menu, "Menu down", KeyCode.DownArrow, "Key injected for the D token (field 3).");
            MenuLeftKey = Config.Bind(menu, "Menu left", KeyCode.LeftArrow, "Key injected for the L token (field 3).");
            MenuRightKey = Config.Bind(menu, "Menu right", KeyCode.RightArrow, "Key injected for the R token (field 3).");
            MenuConfirmKey = Config.Bind(menu, "Menu confirm", KeyCode.Return, "Key injected for the C token (field 3).");
            MenuCancelKey = Config.Bind(menu, "Menu cancel", KeyCode.Escape, "Key injected for the B token (field 3).");

            ShowHud = Config.Bind(overlay, "Show HUD", true, "Draw the TAS overlay.");
            HudShowSpeed = Config.Bind(overlay, "HUD: speed", true, "Player speed in Unity units per second (horizontal, vertical and total).");
            HudShowVelocity = Config.Bind(overlay, "HUD: velocity", true, "Rigidbody velocity vector.");
            HudShowAngles = Config.Bind(overlay, "HUD: angles", true, "Camera pitch and yaw.");
            HudShowVitals = Config.Bind(overlay, "HUD: vitals", true, "Health, hard damage and dash charge when available.");
            HudShowRng = Config.Bind(overlay, "HUD: RNG", true, "Current seed and RNG determinism state.");
            HudShowInputs = Config.Bind(overlay, "HUD: inputs", true, "Buttons applied by the current tick.");
            HudShowProgress = Config.Bind(overlay, "HUD: progress", true, "Tick, time and frametime.");
            HudShowPosition = Config.Bind(overlay, "HUD: position", true, "Player world position (X, Y, Z).");
            HudShowState = Config.Bind(overlay, "HUD: movement state", true, "Grounded, sliding, dashing and jump flags.");
            HudShowWeapon = Config.Bind(overlay, "HUD: weapon", true, "Equipped weapon, slot and variation.");
            HudShowInputDisplay = Config.Bind(overlay, "HUD: input display", true, "The 'now' / 'F6' key rows showing every action held.");
            HudShowPointer = Config.Bind(overlay, "HUD: pointer row", true, "Mouse position (pixels and fractions, top-left origin) and buttons.");
            HudEntityInspector = Config.Bind(overlay, "HUD: entity inspector", false, "The enemy, projectile or object under the crosshair: its look_at ID, type, position, distance, HP/state and velocity.");
            HudShowHit = Config.Bind(overlay, "HUD: surface hit coordinate", false, "Show the world coordinate of the surface under the crosshair as \"hit x y z\" (\"hit none\" when the ray hits nothing).");
            HudShowLevelStats = Config.Bind(overlay, "HUD: level stats", false, "Kills, score and time with their ranks, the total rank and the challenge, read from the game's stats screen data, with the value each needs for an S (all three S is a P rank). Major assists are not shown.");
            ShowTriggers = Config.Bind(overlay, "Show triggers", false, "Draw the level's trigger and event volumes near the camera as boxes with their names. Read-only.");
            ShowTriggersDistance = Config.Bind(overlay, "Show triggers distance", 60f, "Only triggers within this many units of the camera are drawn (5 - 500).");
            ShowTriggersSolid = Config.Bind(overlay, "Show triggers solid", true, "Fill the trigger volumes with translucent boxes under their outlines.");
            ShowTriggersOpacity = Config.Bind(overlay, "Show triggers opacity", 0.18f, "Opacity of the solid trigger boxes (0.02 - 1). The outline stays as it is.");
            HideViewmodels = Config.Bind(overlay, "Hide viewmodels", false, "Do not draw the first-person weapon and arm models. Only their renderers are hidden; weapon logic, input and physics are untouched.");
            ShowHitboxes = Config.Bind(overlay, "Show hitboxes", false, "Draw enemy collider bounds (hitboxes/hurtboxes) as read-only outlines near the camera.");
            ShowPlayerHitbox = Config.Bind(overlay, "Show player hitbox", false, "Draw the local player's collision bounds. Non-trigger colliders are preferred; these are the shapes that collide with walls and that trigger volumes detect.");
            HudCorner = Config.Bind(overlay, "HUD corner", "topleft", "Where the HUD panel is anchored: topleft, topright, bottomleft or bottomright.");
            HudOffsetX = Config.Bind(overlay, "HUD offset X", 10, "Distance from the anchored screen edge, in pixels.");
            HudOffsetY = Config.Bind(overlay, "HUD offset Y", 10, "Distance from the anchored screen edge, in pixels.");
            HudScale = Config.Bind(overlay, "HUD scale", 1f, "Size multiplier of the whole HUD panel (0.5 - 3).");
            HudOpacity = Config.Bind(overlay, "HUD opacity", 1f, "Opacity of the HUD panel (0.1 - 1).");
            HudBackground = Config.Bind(overlay, "HUD background", true, "Draw a dark box behind the HUD text.");
            HudColor = Config.Bind(overlay, "HUD text color", "#FFFFFF", "HUD text color as #RRGGBB or #RRGGBBAA.");
            HudGameFont = Config.Bind(overlay, "HUD use game font", true, "Use ULTRAKILL's own pixel font (VCR OSD Mono) when it can be found.");

            SpeedoEnabled = Config.Bind(overlay, "Speedometer enabled", true, "BunnyMod XT style speed readout drawn in ULTRAKILL's font.");
            SpeedoMode = Config.Bind(overlay, "Speedometer mode", "horizontal", "horizontal (XZ speed), total (full 3D speed) or vertical (Y speed).");
            SpeedoDecimals = Config.Bind(overlay, "Speedometer decimals", 0, "Decimal places shown (0 - 3).");
            SpeedoSize = Config.Bind(overlay, "Speedometer size", 44, "Font size in pixels (12 - 160).");
            SpeedoOffsetX = Config.Bind(overlay, "Speedometer offset X", 0, "Pixels right of the screen centre (negative = left).");
            SpeedoOffsetY = Config.Bind(overlay, "Speedometer offset Y", 60, "Pixels below the screen centre (negative = above). 60 sits just under the crosshair.");
            SpeedoColor = Config.Bind(overlay, "Speedometer color", "#FFFFFF", "Base color as #RRGGBB or #RRGGBBAA.");
            SpeedoColorByChange = Config.Bind(overlay, "Speedometer color by change", true, "Green while speed is rising, red while it is falling.");
            SpeedoShadow = Config.Bind(overlay, "Speedometer shadow", true, "Draw a dark outline so the number stays readable.");
            SpeedoUnit = Config.Bind(overlay, "Speedometer show unit", false, "Append the unit (u/s, Unity units per second).");
            SpeedoShowVertical = Config.Bind(overlay, "Speedometer show vertical", false, "Second smaller line with vertical speed.");
            SpeedoShowPeak = Config.Bind(overlay, "Speedometer show peak", false, "Second smaller line with the highest speed reached this run.");
            SpeedoOpacity = Config.Bind(overlay, "Speedometer opacity", 1f, "Opacity of the speedometer (0.1 - 1).");

            PausedLabelEnabled = Config.Bind(overlay, "Paused label enabled", true,
                "Show a small PAUSED text above the crosshair while the TAS clock is paused (drawn in ULTRAKILL's font when it can be found).");
            PausedLabelSize = Config.Bind(overlay, "Paused label size", 22,
                "Height of the PAUSED text in pixels (12 - 96).");
            PausedLabelOffsetY = Config.Bind(overlay, "Paused label height", 56,
                "Pixels above the screen centre (the crosshair) where the PAUSED text sits.");
        }

        /// <summary>
        /// Where a typed script path is written: absolute paths as typed, relative names inside
        /// the script folder (the same folder Load looks in), with .uktas added when the name has
        /// no extension. Empty stays empty.
        /// </summary>
        public string ResolveScriptSavePath(string path)
        {
            string text = (path ?? string.Empty).Trim().Trim('"');
            if (text.Length == 0)
                return string.Empty;
            try
            {
                if (!Path.HasExtension(text))
                    text += ".uktas";
                if (!Path.IsPathRooted(text))
                    text = Path.Combine(ScriptDirectory, text);
            }
            catch (ArgumentException)
            {
            }
            return text;
        }

        ConfigEntry<KeyboardShortcut> BindKey(string section, string name, KeyCode key)
        {
            return Config.Bind(section, name, new KeyboardShortcut(key),
                "Hotkey. Click Rebind in the Keybinds tab to change it.");
        }

        void ApplyStartupScript()
        {
            string scriptPath = string.Empty;
            string[] args = Environment.GetCommandLineArgs();
            bool autoplayArg = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if ((arg == "--uktas" || arg == "-uktas" || arg == "--tas-script") && i + 1 < args.Length)
                {
                    scriptPath = args[i + 1];
                    i++;
                }
                else if (arg == "--tas-autoplay")
                {
                    autoplayArg = true;
                }
            }

            if (string.IsNullOrEmpty(scriptPath) && !string.IsNullOrEmpty(Autoload.Value))
                scriptPath = Autoload.Value;

            if (string.IsNullOrEmpty(scriptPath))
                return;

            if (!Path.IsPathRooted(scriptPath))
                scriptPath = Path.Combine(ScriptDirectory, scriptPath);

            if (!File.Exists(scriptPath))
            {
                Toast("Autoload script not found: " + scriptPath, true);
                return;
            }

            if (_engine.LoadScript(scriptPath, true))
            {
                bool play = autoplayArg || Autoplay.Value;
                if (play)
                    _engine.Play();
            }
        }

        /// <summary>
        /// Per-frame work. Driven by TasRuntime so it keeps running even if the
        /// BepInEx host component is torn down; the frame guard makes it safe to
        /// call from more than one driver in the same frame.
        /// </summary>
        public void Tick()
        {
            // SimulationFreeze.FrameCount, not Time.frameCount: a fully frozen frame runs no
            // time update, so Time.frameCount can stand still while the GUI keeps ticking.
            int frame = SimulationFreeze.FrameCount;
            if (_tickFrame == frame)
                return;
            _tickFrame = frame;
            LastGuiMilliseconds = _guiMilliseconds;
            _guiMilliseconds = 0.0;

            // Assign the level's entity IDs on its first visible frame (see EntityRegistry.Poll):
            // doing it later let spawned/destroyed objects change the numbering between runs.
            if (_bridge != null)
                _bridge.Entities.Poll();

            if (!_loggedUpdate)
            {
                _loggedUpdate = true;
                // Startup probe: tells "Update never ran" apart from "the keypress
                // never reached the game" when the TAS window does not open.
                KeyUtil.Held(KeyCode.Insert);
                LogInfo("Update loop running. Legacy input: " +
                        (KeyUtil.LegacyInputWorks ? "available" : "disabled") +
                        ", new keyboard: " + (KeyUtil.NewKeyboardAvailable ? "available" : "missing") + ".");
            }

            // The one "GUI captures input" decision of the frame (the TAS window is open).
            // Everything that feeds the game reads KeyUtil's copy of it.
            bool wasCapturing = KeyUtil.GuiCapturesInput;
            KeyUtil.SetGuiCapture(_gui != null && _gui.CapturesInput, _gui != null && _gui.OwnsEscape);
            if (!wasCapturing && KeyUtil.GuiCapturesInput && _input != null)
                _input.OnGuiCaptureBegan();
            else if (wasCapturing && !KeyUtil.GuiCapturesInput && _input != null)
                _input.OnGuiCaptureEnded();

            // Config can change at runtime from the GUI; keep the clock in sync.
            _time.PhysicsSubsteps = Mathf.Clamp(PhysicsSubsteps.Value, 1, 8);
            if (!_time.Active)
                _time.SetHostFrametime(DefaultHostFrametime.Value);

            // HUD input lights: everything held on the devices this frame.
            if (_input != null)
                _input.UpdateLive();

            if (!_loggedInsert && KeyUtil.Down(KeyCode.Insert))
            {
                _loggedInsert = true;
                LogInfo("Insert keypress observed.");
            }

            HandleHotkeys();

            if (_engine != null)
                _engine.BeginFrame();
            GameToggles.Update();
            if (_gui != null)
            {
                // Speed is read every frame (the speedometer must not lag); the rest of the
                // HUD text is rebuilt on a short timer in UpdateCache.
                _gui.UpdateSpeed();
                _gui.UpdateCache();
            }
            UpdateCursorState();
        }

        /// <summary>
        /// Cursor handling for the TAS window.
        ///
        /// While the window is open the cursor must be free to click it. Once it
        /// closes, gameplay needs the cursor captured again - but the grip is
        /// taken back only on that transition and released immediately, because
        /// ULTRAKILL's pause/options screen shows the mouse itself. Enforcing
        /// lock+hidden every frame hid the pointer in the pause menu.
        /// </summary>
        void UpdateCursorState()
        {
            if (_gui == null || !Application.isFocused)
                return;

            bool guiVisible = _gui.Visible;
            bool changed = guiVisible != _guiWasVisible;
            _guiWasVisible = guiVisible;

            // The game's own pause/options menu owns the cursor while it is up.
            if (_bridge != null && _bridge.IsGamePaused())
                return;

            // Freecam cursor: free only while freecam is on AND the author toggled it free.
            // When that ends (toggle back, or freecam turned off), the cursor is locked once
            // and then left to ULTRAKILL's normal mouse look.
            // Attached, not just active: between a level unload and the new camera there is nothing to
            // fly, so the cursor is not held locked through loading screens or menus.
            bool freecamOn = _input != null && _input.FreecamAttached;
            bool cursorFree = freecamOn && Cameras != null && Cameras.CursorFree;
            bool freeEnded = _freecamCursorWasFree && !cursorFree;
            _freecamCursorWasFree = cursorFree;

            if (guiVisible || cursorFree)
            {
                if (Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible)
                    Cursor.visible = true;
                return;
            }

            // Paused free-look and captured freecam need a locked cursor every frame, not
            // only on the transition: the camera patch only reads the mouse while the
            // cursor is locked, and a menu may have freed it.
            bool freeLook = _engine != null && _engine.FreeLookActive;
            if (!changed && !freeLook && !freecamOn && !freeEnded)
                return;

            if (Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;
            if (Cursor.visible)
                Cursor.visible = false;
        }

        /// <summary>
        /// Called by SimulationFreeze in place of script LateUpdate on a fully frozen frame,
        /// where neither CameraController.LateUpdate nor FrameEndHook runs: the paused
        /// free-look/freecam camera, then the usual end of frame.
        /// </summary>
        internal void RunFrozenLateUpdate()
        {
            if (_input != null)
                _input.UpdateFrozenCamera();
            OnEndFrame();
        }

        void OnEndFrame()
        {
            long profileStart = FastForwardProfiler.Now();
            try
            {
                OnEndFrameCore();
            }
            finally
            {
                FastForwardProfiler.AddEnd(FastForwardProfiler.Ms(profileStart));
            }
        }

        void OnEndFrameCore()
        {
            if (_engine != null)
                _engine.EndFrame();

            // NoRefresh: the 3D screen is off and the tick counter is racing. The path
            // recorder and freecam only feed what is drawn, so their per-frame work is
            // skipped (the path resumes at the breakpoint).
            bool noRefresh = _engine != null && _engine.RenderSuppressed;
            if (!noRefresh)
            {
                if (_engine != null && Trajectory != null && _engine.IsActive)
                    Trajectory.Sample(_engine.CurrentTick, _engine.CurrentTime);
                if (_engine != null && CoinPaths != null && _engine.IsActive)
                    CoinPaths.Sample(_engine.CurrentTick);
                if (Cameras != null)
                {
                    Cameras.Tick();
                    if (Trajectory != null)
                    {
                        using (ViewPose())
                            Trajectory.Update(Cameras.CursorFree);
                    }
                }
            }
            if (Video != null)
                Video.OnFrameEnd();
        }

        void HandleHotkeys()
        {
            if (_gui != null && _gui.IsRebinding)
                return;

            // These are deliberately read through legacy Input.GetKeyDown even
            // while the GUI is open or the simulation is paused. InputPatcher
            // only owns gameplay actions, so management hotkeys cannot be
            // swallowed by the GUI's input block.
            if (ShortcutDown(KeyToggleGui))
            {
                _gui.Visible = !_gui.Visible;
                // Logged so "the window never appeared" can be told apart from
                // "the keypress never reached the game".
                Toast("TAS window " + (_gui.Visible ? "opened" : "closed") + ".", false);
            }
            // Home, '=' and '-' are also editing keys: leave them to the text field / script editor
            // while one has the keyboard.
            bool typingInGui = _gui != null && _gui.Visible && _gui.IsTextInputFocused;
            if (ShortcutDown(KeyToggleFreecam))
                ToggleFreecam();
            if (Cameras != null)
            {
                if (ShortcutDown(KeyCameraMode))
                    Cameras.CycleMode();
                if (!typingInGui && ShortcutDown(KeyCameraReset))
                    Cameras.ResetCamera();
                if (ShortcutDown(KeyFreecamCursor))
                    Cameras.ToggleCursor();
                if (!typingInGui && ShortcutDown(KeyFreecamFaster))
                    Cameras.AdjustFreecamSpeed(1.25f);
                if (!typingInGui && ShortcutDown(KeyFreecamSlower))
                    Cameras.AdjustFreecamSpeed(0.8f);
            }
            if (ShortcutDown(KeyToggleHud))
            {
                ShowHud.Value = !ShowHud.Value;
                // F12 is also a common screenshot key; without this the overlay
                // could be switched off with no visible feedback at all.
                Toast("TAS HUD " + (ShowHud.Value ? "on" : "off") + ".", false);
            }
            if (ShortcutDown(KeyPlayPause))
                _engine.TogglePause();
            if (ShortcutDown(KeyStep))
                _engine.Step();
            if (ShortcutDown(KeySaveState))
                _states.Save(Mathf.Clamp(ActiveStateSlot.Value, 0, SaveStateManager.SlotCount - 1));
            if (ShortcutDown(KeyLoadState))
                _engine.LoadStateSlot(Mathf.Clamp(ActiveStateSlot.Value, 0, SaveStateManager.SlotCount - 1));
            if (ShortcutDown(KeyRecord))
                _engine.ToggleRecording();
            if (ShortcutDown(KeyStop))
                _engine.Stop();
            // Ctrl+Z is also the text-field undo; leave it to the field while typing.
            bool typing = typingInGui;
            if (!typing && ShortcutDown(KeyUndo))
                _engine.UndoStep();
            if (ShortcutDown(KeyRewind))
                _engine.Rewind();
            if (ShortcutDown(KeyReloadPlay))
                _engine.ReloadAndPlay();

            bool fastForwardHeld = ShortcutPressed(KeyFastForward);
            if (fastForwardHeld && !_fastForwardKeyHeld)
            {
                _fastForwardKeyHeld = true;
                _engine.SetFastForward(true);
            }
            else if (!fastForwardHeld && _fastForwardKeyHeld)
            {
                _fastForwardKeyHeld = false;
                _engine.SetFastForward(false);
            }
        }

        bool ShortcutDown(ConfigEntry<KeyboardShortcut> entry)
        {
            if (entry == null)
                return false;
            KeyboardShortcut shortcut = entry.Value;
            if (shortcut.MainKey == KeyCode.None)
                return false;
            if (!KeyUtil.Down(shortcut.MainKey))
                return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (modifier != KeyCode.None && !KeyUtil.Held(modifier))
                    return false;
            }
            return true;
        }

        bool ShortcutPressed(ConfigEntry<KeyboardShortcut> entry)
        {
            if (entry == null)
                return false;
            KeyboardShortcut shortcut = entry.Value;
            if (shortcut.MainKey == KeyCode.None)
                return false;
            if (!KeyUtil.Held(shortcut.MainKey))
                return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (modifier != KeyCode.None && !KeyUtil.Held(modifier))
                    return false;
            }
            return true;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_input != null)
            {
                // Freecam is deliberately left on: InputPatcher.ApplyFreecam re-attaches it to the
                // new level's camera and keeps its position, so a TAS (re)load never resets it.
                _input.OnSceneChanged();
            }
            if (_bridge != null)
                _bridge.InvalidateScene();
            // Installs the weapon patches if they were wanted before the game assembly was ready.
            WeaponUnlock.Refresh();
            // Entity IDs are assigned here for a single load (after Awake, before Start);
            // addressed loads that never raise sceneLoaded are caught by Poll instead.
            if (_bridge != null)
                _bridge.Entities.OnSceneLoaded(scene, mode);
            if (_gui != null)
                _gui.OnSceneChanged();
            // Trails from the previous level would hang in the new one.
            if (CoinPaths != null)
                CoinPaths.Clear();
            if (_engine != null)
                _engine.OnSceneLoaded(scene.name);
        }

        /// <summary>Milliseconds the TAS spent drawing in OnGUI during the last finished frame.</summary>
        public double LastGuiMilliseconds { get; private set; }
        double _guiMilliseconds;

        public void DrawGui()
        {
            if (!_loggedOnGui)
            {
                _loggedOnGui = true;
                LogInfo("OnGUI running.");
            }
            // IMGUI runs after Update, so the cost is summed here and reported with the next
            // frame's NoRefresh rate line: it tells a slow TAS window apart from a slow game.
            float start = Time.realtimeSinceStartup;
            if (_gui != null)
                _gui.OnGUI();
            // NoRefresh draws nothing but the HUD panel and its banner.
            if (Trajectory != null && (_engine == null || !_engine.RenderSuppressed))
                Trajectory.DrawOverlay();
            double guiMs = (Time.realtimeSinceStartup - start) * 1000.0;
            _guiMilliseconds += guiMs;
            FastForwardProfiler.AddGui(guiMs);
        }

        void OnApplicationQuit()
        {
            _quitting = true;
            if (_engine != null)
                _engine.CloseTrace();
            if (_time != null)
                _time.Shutdown();
        }

        void OnDestroy()
        {
            // Anything other than a real shutdown destroys the host while the
            // game keeps running; that used to stop the tool for the session
            // (no Update, so no hotkeys and no window). TasRuntime keeps ticking
            // this same state, so leave the clock and patches alone.
            LogWarning("Plugin component destroyed at frame " + Time.frameCount +
                       " (scene '" + SceneManager.GetActiveScene().name +
                       "', quitting: " + _quitting + ").");

            if (!_quitting && Application.isPlaying)
            {
                LogWarning("Continuing on the TAS runtime driver.");
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            _sceneHooked = false;

            if (Video != null && Video.IsRecording)
                Video.Stop("shutdown");
            if (Trajectory != null)
                Trajectory.Dispose();
            if (CoinPaths != null)
                CoinPaths.Dispose();
            if (Cameras != null)
                Cameras.Dispose();
            if (_engine != null)
                _engine.Stop();
            if (_input != null)
                _input.Shutdown();
            if (_time != null)
                _time.Shutdown();
            if (_frameEndObject != null)
                UnityEngine.Object.Destroy(_frameEndObject);
            if (ReferenceEquals(Instance, this))
                Instance = null;
            State = null;
        }

        // ------------------------------------------------------------------
        // Logging
        // ------------------------------------------------------------------

        public void LogInfo(string message)
        {
            Logger.LogInfo(message);
        }

        public void LogWarning(string message)
        {
            Logger.LogWarning(message);
        }

        public void LogError(string message)
        {
            Logger.LogError(message);
        }

        public void Toast(string message, bool error)
        {
            if (string.IsNullOrEmpty(message))
                return;
            TasMessage entry = new TasMessage
            {
                Text = message,
                IsError = error,
                Time = TimeController.RealTime
            };
            Messages.Add(entry);
            if (Messages.Count > 100)
                Messages.RemoveAt(0);

            if (error)
                Logger.LogWarning(message);
            else
                Logger.LogInfo(message);

            if (_gui != null)
                _gui.ShowToast(message, error);
        }
    }

    /// <summary>Last callback in the frame: arms the clock for the next frame.</summary>
    [DefaultExecutionOrder(32000)]
    public sealed class FrameEndHook : MonoBehaviour
    {
        public Action OnEndFrame;

        void LateUpdate()
        {
            Action callback = OnEndFrame;
            if (callback != null)
                callback();
        }
    }
}
