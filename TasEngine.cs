using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UltrakillTAS
{
    public enum EngineMode
    {
        Idle,
        Starting,
        Playing,
        Paused,
        Recording,
        Standalone
    }

    /// <summary>
    /// Playback, recording, standalone stepping and startup state machine.
    ///
    /// Exactly one script tick is applied per rendered simulation frame. The
    /// time controller decides whether a frame simulates, is frozen, or is a
    /// single requested step. Commands on a pipe line run once on that line's
    /// first tick, before the line's input.
    /// </summary>
    public sealed class TasEngine
    {
        readonly Plugin _plugin;
        readonly GameBridge _bridge;
        readonly TimeController _time;
        readonly InputPatcher _input;
        readonly SaveStateManager _states;
        readonly RngController _rng;

        TasScript _script;
        string _scriptPath = string.Empty;
        // Disk timestamp of the script as it was last loaded or saved (for auto-reload on Play).
        DateTime _loadedWriteTime;
        TasFrameLine _currentLine;
        int _currentLineIndex = -1;

        bool _anglesInitialized;
        bool _skipFirstFrame;
        // A state was just loaded: the next StartPlayback must not spend the neutral warm-up
        // frame, which would release the saved slide/dash keys before the first tick.
        bool _loadedStateAwaitingPlay;
        // A load requested mid-frame is applied at the end of that frame, after the game's
        // Update has run: the next update the game evaluates is then the first scripted tick's,
        // not a leftover frame of the world the load is replacing.
        int _pendingLoadSlot = -1;
        // A user load that reloads the map checkpoint first: the game's own respawn runs when the
        // load is requested and the savestate restore waits in EndFrame until the room clones the
        // respawn destroys with Object.Destroy have actually been flushed.
        int _checkpointReloadSlot = -1;
        int _checkpointReloadWait;
        // A load must not lose the landing edge autoslide reacts to; the first resumed tick
        // engages the slide once, from the restored onGround flag.
        bool _forceAutoslideEngage;
        // Capital-S autoslide: true on the next eligible tick that should press Slide.
        bool _spamAutoslideOn = true;
        // Reseed when tick 0 runs, after any scene settling and control wait. Frames that
        // ran during loading draw random numbers too, so the seed set at Play is not enough.
        bool _reseedAtFirstTick;
        readonly DeterminismTrace _trace = new DeterminismTrace();
        bool _resumeAfterFirstTick;
        // Tick to report the first post-resume physics step for, or -1. A resume that loses a
        // tick shows up here first: the tick runs but the clock spent no step on it.
        int _resumeTickCheck = -1;
        // Tick whose post-resume probe line is logged next, or -1. Two lines per resume (the
        // restored tick and the one after it) make a drift readable in the log instead of by eye.
        int _norefreshProbeTick = -1;
        // The script cursor the loaded state was captured at. A load resumes there instead of
        // replaying the script from tick 0 onto a mid-run world (the input desync).
        int _resumeTick = -1;
        double _resumeTime;
        string _resumeScript;
        bool _suppressRecordFrame;
        TasButtons _recordButtons;
        string _recordingPath = string.Empty;
        double _lastFrametime;
        TasButtons _lastButtons;

        bool _startupPending;
        bool _startupSceneLoaded;
        /// <summary>A held scene load has finished loading; its settle frames stay frozen.</summary>
        bool _holdSceneArrived;
        /// <summary>A "difficulty" header failed at Play because PrefsManager was not available; retried once at the next scene load.</summary>
        bool _difficultyRetryPending;
        int _startupWaitFrames;
        // Frames to wait before asking for the startup scene, so the clock alignment runs first.
        int _startupLoadDelay;
        // Work held back until the queued fixed-step align frame has run (see DeferForAlign).
        Action _afterAlign;
        int _afterAlignDelay;
        // Snapshot taken before an in-place align frame, put back once it has run.
        Savestate _alignGuard;
        int _startupSceneHandle = -1;
        TasStartType _startupType;
        string _startupLevel = string.Empty;
        int _startupSlot;
        bool _waitForControl;
        int _sceneSettleFrames;
        bool _captureStartAfterWait;
        int _pauseAtTick = -1;
        int _branchTick = -1;
        // Script length right after the last step-recorded frame. Undo may only
        // delete script frames that step-record itself appended; stepping through a
        // loaded script must scrub the cursor without destroying authored frames.
        int _recordedTailTick = -1;
        Savestate _branchState;
        bool _stepRecordedThisFrame;
        bool _branchMode;

        // NoRefresh (FFF) fast-forward. The engine decides when the screen goes dark; the
        // clock does the work (see TimeController.EnterNoRefresh).
        bool _noRefreshActive;
        /// <summary>Tick the headless run stops at: an FFF breakpoint or a manual target.</summary>
        int _noRefreshTargetTick = -1;
        int _noRefreshStartTick;
        /// <summary>A manual "NoRefresh to tick" request waiting for the startup scene to finish.</summary>
        int _pendingNoRefreshTick = -1;

        bool _timerRunning;
        double _timerStart;
        double _timerElapsed;

        public EngineMode Mode { get; private set; }
        public TasScript Script { get { return _script; } }
        public string ScriptPath { get { return _scriptPath; } }
        public int CurrentTick { get; private set; }
        public double CurrentTime { get; private set; }
        public bool Finished { get; private set; }
        public double PlaybackSpeed { get; private set; }
        public bool FastForward { get; private set; }

        /// <summary>Game-time frames the wait-for-control drop ran this playback (before tick 0).</summary>
        public int WaitFrames { get; private set; }
        /// <summary>Game seconds the wait-for-control drop ran this playback.</summary>
        public double WaitTime { get; private set; }

        /// <summary>
        /// Video timeline: every frame that advanced game time this playback, the elevator
        /// drop first, then the ticks. Only grows during one playback.
        /// </summary>
        public int VideoFrameIndex { get { return WaitFrames + CurrentTick; } }
        /// <summary>Game seconds on the video timeline (drop + script time).</summary>
        public double VideoTime { get { return WaitTime + CurrentTime; } }

        /// <summary>True while playing a tick above the script's ff line (uncapped pacing).</summary>
        public bool ScriptFastForwardActive
        {
            get
            {
                return Mode == EngineMode.Playing && _script != null && !VideoRecording &&
                       CurrentTick < _script.FastForwardTick;
            }
        }

        /// <summary>
        /// True while a video is being recorded. Every fast-forward (F7, ff lines, FFF) is
        /// off then: the recorder needs every tick drawn at normal pacing.
        /// </summary>
        public bool VideoRecording
        {
            get { return _plugin.Video != null && _plugin.Video.IsRecording; }
        }

        /// <summary>
        /// Called when a video recording starts: drops any fast-forward that is running, so
        /// the first captured frame is already a normal, fully rendered one.
        /// </summary>
        /// <summary>Flushes and closes the determinism trace file (game quit).</summary>
        public void CloseTrace()
        {
            _trace.Close();
        }

        public void CancelFastForwardForRecording()
        {
            FastForward = false;
            EndNoRefresh(false);
            _time.Turbo = PlaybackSpeed > 1.001;
        }
        /// <summary>True while the screen is not being refreshed and the script fast-forwards headless.</summary>
        public bool NoRefreshActive { get { return _time.NoRefreshActive; } }
        /// <summary>NoRefreshActive and the screen really is dark: false while the TAS window is open (see TimeController.RenderSuppressed).</summary>
        public bool RenderSuppressed { get { return _time.RenderSuppressed; } }
        /// <summary>Tick the headless run stops at (an FFF breakpoint or a manual target), or -1.</summary>
        public int NoRefreshTargetTick { get { return _noRefreshTargetTick; } }
        /// <summary>Tick the headless run started from.</summary>
        public int NoRefreshStartTick { get { return _noRefreshStartTick; } }
        /// <summary>Ticks simulated since NoRefresh was entered.</summary>
        public long NoRefreshTicks { get { return _time.NoRefreshTicks; } }
        public double NoRefreshTicksPerSecond { get { return _time.NoRefreshTicksPerSecond; } }
        public double NoRefreshSeconds { get { return _time.NoRefreshSeconds; } }
        /// <summary>Cameras whose culling mask is currently 0.</summary>
        public int SuspendedCameraCount { get { return _time.SuspendedCameraCount; } }

        public float VirtualPitch { get; private set; }
        public float VirtualYaw { get; private set; }
        public int RenderedFrames { get; private set; }
        public string LastError { get; private set; }
        public TasFrameLine CurrentLine { get { return _currentLine; } }
        public int CurrentLineIndex { get { return _currentLineIndex; } }
        public bool IsActive { get { return Mode != EngineMode.Idle; } }
        public bool IsStandalone { get { return Mode == EngineMode.Standalone; } }
        public bool StartupPending { get { return _startupPending; } }
        public double LastFrametime { get { return _lastFrametime; } }
        public TasButtons LastButtons { get { return _lastButtons; } }
        public bool TimerRunning { get { return _timerRunning; } }
        public bool WaitingForControl { get { return _waitForControl; } }
        public bool FreeLookActive { get { return _input.FreeLookActive; } }
        public TasButtons StagedButtons { get { return _input.StagedButtons; } }
        public float StagedPitch { get { return _input.StagedPitch; } }
        public float StagedYaw { get { return _input.StagedYaw; } }
        public int PauseAtTick { get { return _pauseAtTick; } }
        public int BranchTick { get { return _branchTick; } }
        public bool HasUndoState { get { return _states.HistoryCount > 0; } }
        public bool HasBranchState { get { return _branchState != null; } }
        public double TimerElapsed
        {
            get { return _timerRunning ? _timerElapsed + (Time.unscaledTime - _timerStart) : _timerElapsed; }
        }
        public string TimerText
        {
            get { return TimerElapsed.ToString("0.000", CultureInfo.InvariantCulture) + "s"; }
        }

        public TasEngine(Plugin plugin, GameBridge bridge, TimeController time,
                         InputPatcher input, SaveStateManager states, RngController rng)
        {
            _plugin = plugin;
            _bridge = bridge;
            _time = time;
            _input = input;
            _states = states;
            _rng = rng;
            PlaybackSpeed = 1.0;
            // Savestates remember the script cursor they were taken at (see PerformLoadState).
            states.TickProvider = () => CurrentTick;
            states.TimeProvider = () => CurrentTime;
            states.ScriptProvider = () => _scriptPath;
        }

        // ------------------------------------------------------------------
        // Script management
        // ------------------------------------------------------------------

        public bool LoadScript(string path, bool announce)
        {
            // A missing file is reported and nothing else happens: the engine, the game clock and
            // the script already in memory are left exactly as they were (no pause, no stop).
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                LastError = "No script file found at: " + (string.IsNullOrEmpty(path) ? "(empty path)" : path);
                _plugin.Toast(LastError + ". Nothing was loaded; the game was not paused.", true);
                return false;
            }
            try
            {
                TasScript loaded = TasScript.Load(path);
                if (Mode == EngineMode.Recording)
                    StopRecording(true);
                StopInternal(false);
                _script = loaded;
                _scriptPath = path;
                _loadedWriteTime = File.GetLastWriteTimeUtc(path);
                ResetPlaybackPosition();
                for (int i = 0; i < loaded.Warnings.Count; i++)
                    _plugin.LogWarning(loaded.Warnings[i]);
                if (announce)
                    _plugin.Toast("Loaded " + Path.GetFileName(path) + " (" + loaded.TotalTicks + " ticks, " +
                                  loaded.TotalTime.ToString("0.###") + "s).", false);
                _plugin.LogInfo("Loaded script: " + path);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _plugin.Toast("Failed to load script: " + ex.Message, true);
                return false;
            }
        }

        public bool SaveScript(string path)
        {
            if (_script == null)
            {
                LastError = "No script to save.";
                _plugin.Toast(LastError, true);
                return false;
            }
            // Relative names go to the script folder, the same place Load looks.
            path = _plugin.ResolveScriptSavePath(path);
            if (path.Length == 0)
            {
                LastError = "Type a file path first.";
                _plugin.Toast(LastError, true);
                return false;
            }
            try
            {
                _script.Save(path);
                _scriptPath = path;
                _loadedWriteTime = File.GetLastWriteTimeUtc(path);
                _plugin.Toast("Saved " + Path.GetFileName(path) + ".", false);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _plugin.Toast("Failed to save script: " + ex.Message, true);
                return false;
            }
        }

        public void NewScript(string path)
        {
            if (Mode == EngineMode.Recording)
                StopRecording(true);
            StopInternal(false);
            _script = TasScript.CreateDefault(_plugin.DefaultSeed.Value, _plugin.DefaultHostFrametime.Value);
            // Resolved now, so a later Save or recording writes into the script folder.
            _scriptPath = _plugin.ResolveScriptSavePath(path);
            ResetPlaybackPosition();
            // A fresh script starts with the weapons and loadout the player has now; with no
            // player loaded (menu) the headers stay unset.
            string captureError;
            CaptureLoadoutIntoScript(out captureError);
            _plugin.Toast("Created a new empty script. Record input or add a frame.", false);
        }

        /// <summary>
        /// Stores the live game's owned weapons and forced loadout in the script's "weapons" and
        /// "loadout" headers. False with <paramref name="error"/> set (and the script untouched)
        /// when there is no script or no player loaded.
        /// </summary>
        public bool CaptureLoadoutIntoScript(out string error)
        {
            error = null;
            if (_script == null)
            {
                error = "No script loaded.";
                return false;
            }
            List<string> weapons;
            Dictionary<string, string> loadout;
            if (!_bridge.TryCaptureLoadout(out weapons, out loadout))
            {
                error = string.IsNullOrEmpty(_bridge.LastError) ? "Loadout capture failed." : _bridge.LastError;
                return false;
            }
            _script.Properties.Weapons = weapons;
            _script.Properties.Loadout = loadout;
            return true;
        }

        public void SetScript(TasScript script, string path)
        {
            if (Mode == EngineMode.Recording)
                StopRecording(true);
            StopInternal(false);
            _script = script;
            _scriptPath = path ?? string.Empty;
            ResetPlaybackPosition();
        }

        /// <summary>
        /// Swaps the script without moving the playback position. A fork made at the paused
        /// tick uses this so the author continues from the game state that is already live.
        /// The new script is expected to be the same route truncated at CurrentTick.
        /// </summary>
        public void SetScriptKeepPosition(TasScript script, string path)
        {
            if (Mode == EngineMode.Recording)
                StopRecording(true);
            _script = script;
            _scriptPath = path ?? string.Empty;
            _pauseAtTick = -1;
            Finished = false;
            CurrentTick = Mathf.Clamp(CurrentTick, 0, script != null ? script.TotalTicks : 0);
            CurrentTime = script != null ? script.TimeAtTick(CurrentTick) : 0.0;
        }

        void ResetPlaybackPosition()
        {
            CurrentTick = 0;
            CurrentTime = 0.0;
            Finished = false;
            _currentLine = null;
            _currentLineIndex = -1;
            _anglesInitialized = false;
            _startupPending = false;
            _startupSceneLoaded = false;
            _startupWaitFrames = 0;
            _waitForControl = false;
            _sceneSettleFrames = 0;
            _captureStartAfterWait = false;
            _pauseAtTick = -1;
            _branchTick = -1;
            _states.ClearHistory();
            _recordedTailTick = -1;
            _noRefreshTargetTick = -1;
            _pendingNoRefreshTick = -1;
        }

        // ------------------------------------------------------------------
        // Transport
        // ------------------------------------------------------------------

        /// <summary>
        /// Reload the script from disk when the file was changed by hand since it was last
        /// loaded or saved, so an edit in a text editor plays without opening the GUI. A file
        /// that is unchanged is never reloaded, so unsaved in-memory edits survive a Play.
        /// </summary>
        void ReloadIfChangedOnDisk()
        {
            if (!_plugin.AutoReloadOnPlay.Value || string.IsNullOrEmpty(_scriptPath) || !File.Exists(_scriptPath))
                return;
            if (File.GetLastWriteTimeUtc(_scriptPath) == _loadedWriteTime)
                return;
            if (LoadScript(_scriptPath, false))
                _plugin.Toast("Reloaded " + Path.GetFileName(_scriptPath) + " (changed on disk).", false);
        }

        /// <summary>
        /// Read the script file from disk as it is right now (regardless of timestamps) and play
        /// it from the start. Anything not saved in the window is discarded.
        /// </summary>
        public bool ReloadAndPlay()
        {
            if (string.IsNullOrEmpty(_scriptPath) || !File.Exists(_scriptPath))
            {
                LastError = "No script file on disk to reload. Nothing was changed and the game was not paused.";
                _plugin.Toast(LastError, true);
                return false;
            }
            if (!LoadScript(_scriptPath, false))
                return false;
            _plugin.Toast("Reloaded " + Path.GetFileName(_scriptPath) + " from disk; playing.", false);
            return Play();
        }

        public bool Play()
        {
            ReloadIfChangedOnDisk();
            if (_script == null || _script.TotalTicks == 0)
            {
                LastError = "No playable script: load a .uktas/.hltas file or record one first.";
                _plugin.Toast(LastError, true);
                return false;
            }
            if (Mode == EngineMode.Recording)
                StopRecording(true);

            StopInternal(false);
            ResetPlaybackPosition();
            ResolveStartup();

            // A cached norefresh checkpoint for this level instance lets the run resume at its tick.
            _norefreshHit = FindNorefreshHit();
            if (_norefreshHit != null)
                _startupType = TasStartType.None;

            // Before the start scene loads and before the start state is captured, so the level
            // spawns on the header's difficulty.
            ApplyDifficulty();
            // Ownership too, so a scene loaded below builds its weapon slots from the header.
            SetWeaponOverride();

            _time.Activate(FrametimeAtTick(0));
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = FastForward;
            _rng.Seed(_script.Properties.Seed);
            _input.SetActive(true);
            InitAnglesFromGame();
            WaitFrames = 0;
            WaitTime = 0.0;
            _waitForControl = _plugin.WaitForPlayerControl.Value && _script.Properties.WaitForPlayerControl;
            _sceneSettleFrames = _plugin.SceneSettleFrames.Value;
            _captureStartAfterWait = _waitForControl;
            ResetTimer();
            ExecuteLoadCommand();

            // An in-place start (current state or a startup state load) has no scene reload to
            // hide the align frame behind, so run it first and start the run afterwards. A state
            // load replaces what that frame stepped; a plain in-place start restores a snapshot.
            // A norefresh checkpoint carries the sub-tick phase the run had at that tick.
            // Unity's fixed accumulator cannot be written, so the align frame re-creates it
            // BEFORE the restore overwrites the world: aligning to the saved phase instead of
            // PhaseTarget is what makes the resumed tick step physics on the same boundary as
            // continuous playback.
            double alignPhase = TimeController.PhaseTarget;
            if (_norefreshHit != null)
            {
                double savedPhase;
                if (_states.TryGetSavedPhysicsPhase(_norefreshHit.State, out savedPhase))
                    alignPhase = savedPhase;
            }
            else if (_startupType == TasStartType.LoadState)
            {
                // Same rule for a header 'load_state': the state carries its own phase.
                double savedPhase;
                Savestate slotState = _states.GetSlot(_startupSlot);
                if (slotState != null && _states.TryGetSavedPhysicsPhase(slotState, out savedPhase))
                    alignPhase = savedPhase;
            }
            if ((_startupType == TasStartType.None || _startupType == TasStartType.LoadState) &&
                DeferForAlign(BeginRun, alignPhase))
            {
                if (_startupType == TasStartType.None && _norefreshHit == null)
                    _alignGuard = _states.CaptureTransient("align");
                Mode = EngineMode.Starting;
                return true;
            }

            BeginRun();
            return true;
        }

        /// <summary>Second half of <see cref="Play"/>: loads the startup state or scene and starts the run.</summary>
        void BeginRun()
        {
            if (_alignGuard != null)
            {
                _states.RestoreTransient(_alignGuard);
                _alignGuard = null;
            }

            bool preserveRng = _startupType == TasStartType.LoadState;
            if (_startupType == TasStartType.LoadState)
            {
                SaveResult result = _states.Load(_startupSlot);
                if (!result.Success)
                {
                    LastError = result.Message;
                    _plugin.Toast("Startup load_state failed: " + result.Message, true);
                    _startupType = TasStartType.None;
                }
                else
                {
                    RememberResumePoint(_states.GetSlot(_startupSlot));
                    NotifyStateLoaded();
                }
            }

            if (_startupType == TasStartType.RestartMission || _startupType == TasStartType.LoadLevel ||
                _startupType == TasStartType.MainMenu)
            {
                BeginSceneStartup();
                return;
            }

            StartPlayback(!preserveRng);
            if (_norefreshHit != null)
                ApplyNorefreshHit();
        }

        /// <summary>
        /// Runs <paramref name="action"/> once Unity's fixed-step phase is aligned. The align
        /// frame can run a physics step, so whatever sets up the world must come after it.
        /// Returns false (nothing queued, run it now) when alignment is off, the clock is not
        /// ours, or the phase is already aligned.
        /// </summary>
        bool DeferForAlign(Action action, double phase = TimeController.PhaseTarget)
        {
            if (!_plugin.AlignFixedStepPhase.Value || !_time.Active || !_time.QueueFixedStepAlign(phase))
                return false;
            _afterAlign = action;
            // The align frame is the one after this frame's EndFrame; one more frame lets the
            // rest of it finish, and the clock is frozen again by the time the action runs.
            _afterAlignDelay = 3;
            return true;
        }

        /// <summary>Loads a savestate slot (hotkey or GUI), aligning the physics phase first when the engine owns a frozen clock.</summary>
        public void LoadStateSlot(int slot)
        {
            // Align to the phase the state was captured on, not to PhaseTarget: the restored
            // world must keep stepping physics on the same boundary it did when saved.
            double alignPhase = TimeController.PhaseTarget;
            Savestate slotState = _states.GetSlot(slot);
            double savedPhase;
            if (slotState != null && _states.TryGetSavedPhysicsPhase(slotState, out savedPhase))
                alignPhase = savedPhase;
            if ((Mode == EngineMode.Paused || Mode == EngineMode.Standalone) &&
                DeferForAlign(() => LoadStateNow(slot), alignPhase))
                return;
            LoadStateNow(slot);
        }

        void LoadStateNow(int slot)
        {
            // A new request supersedes a checkpoint reload that has not restored yet.
            _checkpointReloadWait = 0;
            _checkpointReloadSlot = -1;

            // The game's own checkpoint load first (StatsManager.Restart -> currentCheckPoint
            // .OnRespawn, the same call the pause menu's CHECKPOINT makes). The restore then
            // waits for that respawn to settle; see EndFrame.
            if (TryBeginCheckpointReload(slot))
                return;

            // Restoring inside the engine's Update would leave the rest of this frame's game
            // Update (NewMovement included) to run on the restored world before tick K's input
            // is injected. Defer to EndFrame, which runs after that Update, so the load frame
            // cannot advance anything: restore -> first tick, with nothing in between.
            if (Mode == EngineMode.Paused || Mode == EngineMode.Playing)
            {
                _pendingLoadSlot = slot;
                return;
            }
            PerformLoadState(slot);
        }

        /// <summary>
        /// Runs the game's checkpoint reload for a user load, when the opt-in setting is on and
        /// the level has a checkpoint. The restore is then queued two frames out so the game's
        /// deferred destruction of the old room clones happens before the restore writes into
        /// the world. False: the load runs normally (no checkpoint, setting off, finished level).
        /// </summary>
        /// <summary>
        /// The pause menu's CHECKPOINT is about to run. Snapshot the sweepable world first, so
        /// the rooms its respawn builds are protected from the restore that follows.
        /// </summary>
        public void BeginCheckpointButtonReload()
        {
            _states.BeginCheckpointReloadSnapshot();
        }

        /// <summary>
        /// The pause menu's CHECKPOINT has finished the game's own checkpoint load. With the
        /// setting on, put the active savestate back on top of it: the restore runs from
        /// EndFrame two frames later, after the game's deferred room destruction has flushed.
        /// </summary>
        public void EndCheckpointButtonReload()
        {
            _states.EndCheckpointReloadSnapshot();

            if (_plugin.ResetToMapCheckpointOnLoad == null || !_plugin.ResetToMapCheckpointOnLoad.Value)
                return;
            if (Mode == EngineMode.Starting)
                return;

            // The pause button is only meaningful with a live checkpoint and an unfinished level
            // (OptionsManager.RestartCheckpoint unpauses and calls StatsManager.Restart).
            object stats = _bridge.StatsManager;
            object checkpoint = stats != null ? _bridge.GetMember(stats, new[] { "currentCheckPoint" }) : null;
            if (!GameBridge.IsAlive(checkpoint))
                return;
            object infoSent = stats != null ? _bridge.GetMember(stats, new[] { "infoSent" }) : null;
            if (infoSent is bool && (bool)infoSent)
                return;

            int slot = _plugin.ActiveStateSlot != null
                ? Mathf.Clamp(_plugin.ActiveStateSlot.Value, 0, SaveStateManager.SlotCount - 1) : 0;
            if (_states.GetSlot(slot) == null)
            {
                _plugin.Toast("CHECKPOINT: savestate slot " + slot + " is empty; only the checkpoint was loaded.", true);
                return;
            }

            _pendingLoadSlot = -1;
            _checkpointReloadSlot = slot;
            _checkpointReloadWait = 2;
            _plugin.LogInfo("Pause-menu CHECKPOINT: restoring savestate slot " + slot +
                            " after the checkpoint reload settles.");
        }

        bool TryBeginCheckpointReload(int slot)
        {
            if (_plugin.ResetToMapCheckpointOnLoad == null || !_plugin.ResetToMapCheckpointOnLoad.Value)
                return false;
            if (_states.GetSlot(slot) == null)
                return false;
            if (!_states.ResetToMapCheckpoint())
                return false;
            _checkpointReloadSlot = slot;
            _checkpointReloadWait = 2;
            _plugin.LogInfo("Savestate slot " + slot +
                            " restores after the game checkpoint reload settles.");
            return true;
        }

        /// <summary>The restore itself. Runs between frames when the engine owns the clock.</summary>
        void PerformLoadState(int slot, bool mapCheckpointAlreadyReloaded = false)
        {
            RememberResumePoint(_states.GetSlot(slot));
            SaveResult result = _states.Load(slot, !mapCheckpointAlreadyReloaded);
            if (result.Success)
                NotifyStateLoaded();
        }

        /// <summary>
        /// Store the timeline the state was captured at. A state stores the tick that was about
        /// to run (it is captured at the end of the previous frame), so resuming runs that tick
        /// once, not tick K again and not K + 2.
        /// </summary>
        void RememberResumePoint(Savestate state)
        {
            if (state != null && state.HasTimeline && state.Tick > 0)
            {
                _resumeTick = state.Tick;
                _resumeTime = state.ScriptTime;
                _resumeScript = state.ScriptPath ?? string.Empty;
            }
            else
            {
                _resumeTick = -1;
                _resumeTime = 0.0;
                _resumeScript = null;
            }
        }

        void ResolveStartup()
        {
            _startupType = _script.Properties.StartType;
            _startupLevel = _script.Properties.LoadLevelName;
            _startupSlot = _script.Properties.LoadStateSlot;

            if (_plugin.RestartLevelOnPlay.Value)
            {
                _startupType = TasStartType.RestartMission;
                return;
            }

            string mode = (_plugin.PlayStartMode.Value ?? "script").Trim().ToLowerInvariant();
            if (mode == "current" || mode == "none")
            {
                _startupType = TasStartType.None;
                _startupLevel = string.Empty;
            }
            else if (mode == "restart")
            {
                _startupType = TasStartType.RestartMission;
            }
            else if (mode == "menu" || mode == "mainmenu" || mode == "main_menu")
            {
                _startupType = TasStartType.MainMenu;
            }
            else if (mode == "level")
            {
                _startupType = TasStartType.LoadLevel;
                _startupLevel = string.IsNullOrEmpty(_plugin.PlayStartLevel.Value)
                    ? _script.Properties.LoadLevelName
                    : _plugin.PlayStartLevel.Value;
                if (string.IsNullOrEmpty(_startupLevel))
                {
                    _plugin.Toast("Startup load_level has no scene name; starting from current state.", true);
                    _startupType = TasStartType.None;
                }
            }
            else if (mode == "state" || mode == "savestate")
            {
                _startupType = TasStartType.LoadState;
                _startupSlot = Mathf.Clamp(_plugin.PlayStartStateSlot.Value, 0, SaveStateManager.SlotCount - 1);
            }
        }

        void BeginSceneStartup()
        {
            _startupPending = true;
            _startupSceneLoaded = false;
            _startupWaitFrames = 0;
            _startupSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            Mode = EngineMode.Starting;
            _time.Paused = true;
            _time.ClearPendingSteps();
            _input.InjectNeutral();
            _startupLoadDelay = 0;

            // Align Unity's fixed-step phase while the old scene is still loaded: the frame
            // that does it can run a physics step, and the reload discards that world.
            if (_plugin.AlignFixedStepPhase.Value && _time.QueueFixedStepAlign())
            {
                _startupLoadDelay = 2;
                return;
            }

            RequestStartupLoad();
        }

        /// <summary>Asks the game to load the scene the startup type needs; falls back to playing in place.</summary>
        void RequestStartupLoad()
        {
            bool started;
            if (_startupType == TasStartType.RestartMission)
                started = _bridge.RestartMission();
            else if (_startupType == TasStartType.MainMenu)
                started = _bridge.LoadMainMenu();
            else
                started = _bridge.LoadLevel(_startupLevel);

            if (!started)
            {
                LastError = _bridge.LastError ?? "Scene load failed.";
                _plugin.Toast("Startup scene change failed; starting from current state.", true);
                _startupPending = false;
                _startupType = TasStartType.None;
                StartPlayback(true);
            }
        }

        /// <summary>
        /// Equips the header's start fist ("fist knuckleblaster" / "fist parry") so the run does not
        /// depend on the fist the user had out. No header: the current fist is kept.
        /// </summary>
        void ApplyStartFist()
        {
            if (_script == null || _script.Properties.StartFist == TasStartFist.Unchanged)
                return;
            if (_bridge.PlayerComponent == null)
                return;
            TasStartFist fist = _script.Properties.StartFist;
            if (_bridge.EquipFist((int)fist))
                return;
            string message = "Start fist '" + TasScript.FormatStartFist(fist) + "' not applied: " + _bridge.LastError;
            _plugin.LogWarning(message);
            _plugin.Toast(message, true);
        }

        /// <summary>
        /// Applies the header's loadout: "weapons ..." becomes the exact set of owned gear
        /// (<see cref="WeaponUnlock.OwnedOverride"/>), "loadout ..." is written into the live
        /// ForcedLoadout objects, and the weapon slots and arms are rebuilt. A script without either
        /// header clears the override and leaves the player's current weapons alone. Never throws;
        /// a failure shows a message and the script still runs. Runs before <see cref="ApplyStartFist"/>
        /// because rebuilding the arms resets the equipped fist.
        /// </summary>
        void ApplyLoadout()
        {
            try
            {
                SetWeaponOverride();
                if (_script == null)
                    return;
                TasProperties properties = _script.Properties;
                bool hasWeapons = properties.Weapons != null;
                bool hasLoadout = properties.HasLoadout;
                if (!hasWeapons && !hasLoadout)
                    return;
                if (_bridge.PlayerComponent == null)
                    return;

                string failure = null;
                if (hasLoadout && !_bridge.ApplyLoadout(properties.Loadout))
                    failure = "Loadout header not applied: " + _bridge.LastError;
                if (!_bridge.ResetWeaponSlots() && failure == null)
                    failure = "Weapon slots not refreshed: " + _bridge.LastError;
                if (failure != null)
                {
                    _plugin.LogWarning(failure);
                    _plugin.Toast(failure, true);
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Loadout not applied: " + ex.Message);
            }
        }

        /// <summary>Makes the script's "weapons" header the game's ownership (null clears it).</summary>
        void SetWeaponOverride()
        {
            List<string> weapons = _script != null ? _script.Properties.Weapons : null;
            WeaponUnlock.OwnedOverride = weapons != null ? new HashSet<string>(weapons, StringComparer.Ordinal) : null;
        }

        /// <summary>
        /// Sets the header's difficulty ("difficulty violent") through the game's PrefsManager before
        /// the start scene loads. No header: the game's current difficulty is kept. Never throws.
        /// When the PrefsManager instance is not available yet, one retry is queued for the next
        /// scene load (<see cref="OnSceneLoaded"/>, <paramref name="isRetry"/> true) before giving up.
        /// </summary>
        void ApplyDifficulty(bool isRetry = false)
        {
            try
            {
                _difficultyRetryPending = false;
                if (_script == null || _script.Properties.Difficulty == TasDifficulty.Unchanged)
                    return;
                TasDifficulty difficulty = _script.Properties.Difficulty;
                string name = TasScript.FormatDifficulty(difficulty);
                int current = _bridge.GetDifficulty();
                if (current == (int)difficulty)
                    return;
                if (!_bridge.SetDifficulty((int)difficulty))
                {
                    if (!isRetry && _bridge.PrefsManagerRetryable)
                    {
                        _difficultyRetryPending = true;
                        string deferred = "Difficulty '" + name + "' not applied yet (" + _bridge.LastError +
                                          "); retrying once at the next scene load.";
                        _plugin.LogWarning(deferred);
                        _plugin.Toast(deferred, false);
                        return;
                    }
                    string message = "Difficulty '" + name + "' not applied; keeping the current difficulty: " + _bridge.LastError +
                                     (isRetry ? " (after retrying at the scene load)" : string.Empty);
                    _plugin.LogWarning(message);
                    _plugin.Toast(message, true);
                    return;
                }
                if (isRetry)
                {
                    // sceneLoaded runs after the new scene's Awake calls, so objects that read the
                    // difficulty there already used the old value.
                    string message = "Difficulty set to '" + name + "' at the scene load (retry); objects that read it " +
                                     "in Awake may keep the previous difficulty: press Play again for a clean start.";
                    _plugin.LogWarning(message);
                    _plugin.Toast(message, false);
                    return;
                }
                // Objects read the difficulty when they spawn, so an in-place start keeps whatever the
                // current scene already spawned on the old one.
                bool sceneLoads = _startupType == TasStartType.RestartMission || _startupType == TasStartType.LoadLevel ||
                                  _startupType == TasStartType.MainMenu;
                if (!sceneLoads)
                {
                    string message = "Difficulty set to '" + name + "', but this start does not reload the scene: " +
                                     "objects already spawned keep the previous difficulty.";
                    _plugin.LogWarning(message);
                    _plugin.Toast(message, true);
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Difficulty not applied: " + ex.Message);
            }
        }

        void StartPlayback(bool reseedRng)
        {
            ResetLookAtState();
            _startupPending = false;
            _startupType = TasStartType.None;
            Mode = EngineMode.Playing;
            Finished = false;
            BakeManager.PureInputOnly = false;
            // A resume-only frame adjust belongs to the previous norefresh run.
            if (_script != null)
                _script.SetFrameAdjust(-1, 0);
            bool resumeScript = _loadedStateAwaitingPlay && _resumeTick > 0 && _script != null &&
                                string.Equals(_resumeScript ?? string.Empty, _scriptPath ?? string.Empty, StringComparison.Ordinal);
            CurrentTick = resumeScript ? Mathf.Clamp(_resumeTick, 0, _script.TotalTicks) : 0;
            CurrentTime = resumeScript ? _resumeTime : 0.0;
            // Submission guard: a fresh run from tick 0 starts with clean per-run bake latches.
            BakeManager.BeginRun();
            // Only a norefresh resume may arm the automatic bake_end below.
            _time.Bake.CancelNorefreshAutoEnd();
            _currentLine = null;
            _currentLineIndex = -1;
            _anglesInitialized = false;
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.RequestStep(1);
            _time.Turbo = FastForward;
            // A neutral warm-up frame is right for a fresh run, but after a state load it would
            // release the held mask the restore just put back on the first frame, and NewMovement
            // cancels a slide on a release. The loaded run starts on its first tick instead.
            _skipFirstFrame = !_loadedStateAwaitingPlay;
            _forceAutoslideEngage = _loadedStateAwaitingPlay;
            _spamAutoslideOn = true;
            _loadedStateAwaitingPlay = false;
            _resumeAfterFirstTick = true;
            _resumeTickCheck = resumeScript ? CurrentTick : -1;
            _reseedAtFirstTick = reseedRng;
            // There is nothing to wait for on the title screen or during boot: with no
            // player in the scene the wait would never end. A script that starts in a
            // menu and later loads a level uses the wait_scene / wait_control commands.
            WaitFrames = 0;
            WaitTime = 0.0;
            _waitForControl = _plugin.WaitForPlayerControl.Value && _script.Properties.WaitForPlayerControl &&
                              _bridge.PlayerComponent != null;
            _captureStartAfterWait = _waitForControl;
            _sceneSettleFrames = _waitForControl ? _plugin.SceneSettleFrames.Value : 0;
            if (_waitForControl)
                _input.ReleaseControl();
            else
                _input.SetActive(true);
            if (reseedRng)
                _rng.Seed(_script.Properties.Seed);
            // The header fist is equipped before the start state is captured, so a Rewind keeps it.
            // With wait_for_control it is applied when control is granted instead.
            if (!_waitForControl && _norefreshHit == null)
            {
                ApplyLoadout();
                ApplyStartFist();
            }
            // A resumed checkpoint is mid-run: it must not become the Rewind start state.
            // A resumed state is mid-run: capturing it as the "start" state would make Rewind
            // jump to tick 0 of the script with a world from tick K.
            if (!_waitForControl && _plugin.CaptureStartState.Value && _norefreshHit == null && !resumeScript)
                _states.CaptureStartState(0, 0.0);
            InitAnglesFromGame();
            _plugin.Toast("Playing " + (string.IsNullOrEmpty(_scriptPath) ? "script" : Path.GetFileName(_scriptPath)) +
                          " (" + _script.TotalTicks + " ticks, seed " + _script.Properties.Seed + ").", false);
        }

        public void TogglePause()
        {
            if (Mode == EngineMode.Starting)
                return;

            if (Mode == EngineMode.Standalone)
            {
                _time.Paused = !_time.Paused;
                _time.Hold = false;
                if (_time.Paused)
                    _input.InjectNeutral();
                _plugin.Toast(_time.Paused ? "Standalone clock paused." : "Standalone clock running.", false);
                return;
            }

            if (Mode == EngineMode.Recording)
            {
                // Recording owns the clock but starts paused; F5 is how it is run.
                _time.Paused = !_time.Paused;
                _time.Hold = false;
                if (_time.Paused)
                    _time.ClearPendingSteps();
                _plugin.Toast(_time.Paused
                    ? "Recording paused at tick " + CurrentTick + ". F6 steps one frame."
                    : "Recording resumed at tick " + CurrentTick + ".", false);
                return;
            }

            if (Mode == EngineMode.Playing)
            {
                PauseForAuthoring();
                _plugin.Toast("Paused at tick " + CurrentTick + ". Free-look and input staging active.", false);
            }
            else if (Mode == EngineMode.Paused)
            {
                if (Finished)
                {
                    Play();
                    return;
                }
                Mode = EngineMode.Playing;
                _time.Paused = false;
                _time.ClearPendingSteps();
                _time.KickTick();
                _time.ApplyImmediately();
                _input.EndFreeLook();
                _input.SetActive(true);
                _plugin.Toast("Resumed at tick " + CurrentTick + ".", false);
            }
            else if (Mode == EngineMode.Idle)
            {
                // With no script, F5 only reports it (like the Play button): it must not start
                // the standalone clock, which pauses the game. F6 still starts frame stepping.
                if (_script != null)
                    Play();
                else
                    _plugin.Toast("No script loaded: nothing to play. The game was not paused. (F6 steps the live game one tick at a time.)", true);
            }
        }

        public void StartStandalone()
        {
            StopInternal(false);
            _time.Activate(_plugin.DefaultHostFrametime.Value);
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = false;
            _skipFirstFrame = true;
            _resumeAfterFirstTick = false;
            _anglesInitialized = false;
            Mode = EngineMode.Standalone;
            // Standalone is a native-input mode. Leaving the patcher inactive is
            // deliberate: ULTRAKILL must read the real keyboard and mouse for the
            // stepped frame, including mouse-look delta.
            _input.SetActive(false);
            InitAnglesFromGame();
            _plugin.Toast("Standalone frame stepping active. F6 steps one tick; F5 resumes/pauses.", false);
        }

        public void Step()
        {
            if (Mode == EngineMode.Starting)
                return;

            if (Mode == EngineMode.Recording)
            {
                // Step records exactly the frames it runs and leaves recording
                // paused afterwards; F5 resumes continuous recording.
                _time.Hold = false;
                _time.Paused = true;
                _time.RequestStep(1);
                return;
            }

            if (Mode == EngineMode.Idle)
            {
                if (_script == null)
                    StartStandalone();
                else
                    BeginScriptPaused(CurrentTick == 0);
            }
            else if (Mode == EngineMode.Playing)
            {
                PauseForAuthoring();
            }

            _time.Hold = false;
            _time.Paused = true;

            // One F6 press must run physics, not 1 ms of virtual time when a script
            // asks for 1000 fps: queue however many ticks cover one native step.
            // At the default 0.008 this stays exactly one tick.
            int frames = 1;
            double native = _time.NativeFixedStep;
            bool recordsStep = _plugin.StepRecord.Value || _branchMode;
            double stepFrametime = _time.HostFrametime;
            if (_script != null)
                stepFrametime = recordsStep ? _script.Properties.HostFrametime : _script.FrametimeAtTick(CurrentTick);
            if (stepFrametime > 0.0 && native > 0.0 && stepFrametime < native)
                frames = Math.Max(1, (int)Math.Ceiling(native / stepFrametime - 0.000001));

            if (Mode == EngineMode.Paused && _script != null)
            {
                // Snapshot first: the ring holds the state that exists before this tick.
                _states.RecordHistory(CurrentTick);
                if (recordsStep)
                    StageNextFrame(frames);
                // A finished script otherwise ignores the queued step entirely.
                if (Finished && (recordsStep || CurrentTick < _script.TotalTicks))
                    Finished = false;
                _input.EndFreeLook();
                _input.SetActive(true);
            }

            _time.RequestStep(frames);
        }

        void StageNextFrame(int frames)
        {
            if (_script == null)
                return;

            TasButtons buttons;
            float pitch;
            float yaw;
            _input.CaptureStaged(out buttons, out pitch, out yaw);

            // A step-record action branches at the current tick. Truncation
            // keeps the script and physical state in lockstep.
            _script.TruncateAtTick(CurrentTick);
            // The step runs `frames` ticks, so the script must hold exactly that many
            // or playback would hit the end of the script after the first one.
            TasFrameLine line = new TasFrameLine
            {
                Count = Math.Max(1, frames),
                Buttons = buttons,
                Pitch = AngleToken.Absolute(Mathf.Clamp(pitch, -89.9f, 89.9f)),
                Yaw = AngleToken.Absolute(NormalizeYaw(yaw)),
                SourceLine = 0
            };
            _script.AppendFrame(line);
            _recordedTailTick = _script.TotalTicks;
            VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            VirtualYaw = yaw;
            CurrentTime = _script.TimeAtTick(CurrentTick);
            _lastButtons = buttons;
            _stepRecordedThisFrame = true;
            _branchMode = true;
        }

        void PauseForAuthoring()
        {
            // F5 (or the game's own pause menu) aborts a headless run: the screen comes back
            // at the tick the run had reached.
            EndNoRefresh(false);
            Mode = EngineMode.Paused;
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = false;
            FastForward = false;
            _pauseAtTick = -1;
            _input.BeginFreeLook(VirtualPitch, VirtualYaw);
        }

        public void PlayToTick(int targetTick)
        {
            if (_script == null)
            {
                LastError = "Load a script before seeking.";
                _plugin.Toast(LastError, true);
                return;
            }

            targetTick = Mathf.Clamp(targetTick, 0, _script.TotalTicks);
            if (targetTick <= CurrentTick)
            {
                if (Mode == EngineMode.Idle)
                    BeginScriptPaused();
                else
                    PauseForAuthoring();
                return;
            }

            if (Mode == EngineMode.Idle)
            {
                if (!Play())
                    return;
            }
            else if (Mode == EngineMode.Paused)
            {
                Mode = EngineMode.Playing;
                _time.Paused = false;
                _time.ClearPendingSteps();
                _time.KickTick();
                _input.EndFreeLook();
                _input.SetActive(true);
            }

            _pauseAtTick = targetTick;
            _time.Turbo = true;
            FastForward = true;
        }

        public void BranchFromCurrentTick()
        {
            if (_script == null)
                return;
            _script.TruncateAtTick(CurrentTick);
            _branchTick = CurrentTick;
            _branchState = _states.CaptureTransient("branch");
            _branchMode = true;
            PauseForAuthoring();
            if (_plugin.AutoSaveToDisk.Value)
                AutoSaveScript();
            _plugin.Toast("Branch point at tick " + CurrentTick +
                          ". Free-look, stage input, then F6.", false);
        }

        public void RestoreBranch()
        {
            if (_script == null || _branchState == null)
            {
                _plugin.Toast("No branch state has been captured.", true);
                return;
            }
            SaveResult result = _states.RestoreTransient(_branchState);
            if (!result.Success)
            {
                _plugin.Toast("Branch restore failed: " + result.Message, true);
                return;
            }
            CurrentTick = Mathf.Clamp(_branchTick, 0, _script.TotalTicks);
            _script.TruncateAtTick(CurrentTick);
            CurrentTime = _script.TimeAtTick(CurrentTick);
            Finished = false;
            _branchMode = true;
            InitAnglesFromGame();
            PauseForAuthoring();
            _plugin.Toast("Restored branch at tick " + CurrentTick + ".", false);
        }

        public void UndoStep()
        {
            if (_script == null || CurrentTick <= 0 || _states.HistoryCount == 0)
            {
                _plugin.Toast("Nothing to undo.", true);
                return;
            }

            // Pops the newest snapshot taken before the cursor, so repeated Ctrl+Z
            // walks back through the whole ring (default 120 steps).
            int restoredTick;
            SaveResult result = _states.RestoreHistoryBefore(CurrentTick, out restoredTick);
            if (!result.Success)
            {
                _plugin.Toast("Undo failed: " + result.Message, true);
                return;
            }

            // Only frames that step-record appended are deleted; when the cursor was
            // merely scrubbing through a loaded script, the script stays intact.
            bool removeFrames = _recordedTailTick >= 0 && _recordedTailTick == CurrentTick;
            CurrentTick = Mathf.Max(0, restoredTick);
            if (removeFrames)
            {
                _script.TruncateAtTick(CurrentTick);
                _recordedTailTick = CurrentTick;
            }
            CurrentTime = _script.TimeAtTick(CurrentTick);
            Finished = false;
            _branchMode = true;
            InitAnglesFromGame();
            PauseForAuthoring();
            if (removeFrames && _plugin.AutoSaveToDisk.Value)
                AutoSaveScript();
            _plugin.Toast("Undid to tick " + CurrentTick + " (" + _states.HistoryCount + " snapshots left).", false);
        }

        /// <summary>Quiet save used by auto-save and by GUI edits. Does nothing when auto-save is off.</summary>
        public void AutoSaveIfEnabled()
        {
            if (_plugin.AutoSaveToDisk.Value)
                AutoSaveScript();
        }

        void AutoSaveScript()
        {
            if (_script == null)
                return;
            if (string.IsNullOrEmpty(_scriptPath))
                _scriptPath = Path.Combine(_plugin.ScriptDirectory, "studio.uktas");
            try
            {
                _script.Save(_scriptPath);
                // Our own write must not look like an outside edit to ReloadIfChangedOnDisk.
                _loadedWriteTime = File.GetLastWriteTimeUtc(_scriptPath);
                _plugin.LogInfo("Auto-saved " + _scriptPath);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Auto-save failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Enter paused authoring on the loaded script. A fresh start (tick 0) seeds the RNG and
        /// captures the start state, as Play does. Any other tick keeps the game exactly where it
        /// is: stepping from a stopped run must advance one frame, not restart the script.
        /// </summary>
        void BeginScriptPaused(bool fresh = true)
        {
            _time.Activate(FrametimeAtTick(CurrentTick));
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = FastForward;
            if (fresh)
            {
                ResetLookAtState();
                _rng.Seed(_script.Properties.Seed);
                ApplyLoadout();
                ApplyStartFist();
                if (_plugin.CaptureStartState.Value)
                    _states.CaptureStartState(0, 0.0);
                CurrentTick = 0;
                CurrentTime = 0.0;
                _states.ClearHistory();
                _recordedTailTick = -1;
                // Submission guard: a fresh run from tick 0 starts with clean per-run bake latches.
                BakeManager.BeginRun();
            }
            else
            {
                CurrentTime = _script.TimeAtTick(CurrentTick);
            }
            Finished = false;
            _currentLine = null;
            _currentLineIndex = -1;
            _anglesInitialized = false;
            _skipFirstFrame = true;
            _resumeAfterFirstTick = false;
            Mode = EngineMode.Paused;
            _pauseAtTick = -1;
            // Only an empty script starts authoring; stepping a loaded one must not
            // truncate it at tick 0 on the first F6.
            _branchMode = _script.TotalTicks == 0;
            InitAnglesFromGame();
            _input.BeginFreeLook(VirtualPitch, VirtualYaw);
        }

        public void Stop()
        {
            if (Mode == EngineMode.Recording)
            {
                RequestStopRecording();
                return;
            }
            StopInternal(true);
        }

        void StopInternal(bool announce)
        {
            // F11: stop and give the screen back at the current tick.
            EndNoRefresh(false);
            _trace.Close();
            if (_script != null)
                _script.SetFrameAdjust(-1, 0);
            Mode = EngineMode.Idle;
            Finished = false;
            FastForward = false;
            _anglesInitialized = false;
            _startupPending = false;
            _startupSceneLoaded = false;
            _startupWaitFrames = 0;
            _difficultyRetryPending = false;
            _time.Hold = false;
            _time.Turbo = false;
            _time.ClearPendingSteps();
            _skipFirstFrame = false;
            _resumeAfterFirstTick = false;
            _suppressRecordFrame = false;
            _holdScene = false;
            _releaseButtons = 0;
            _afterAlign = null;
            _alignGuard = null;
            _norefreshHit = null;
            if (_time != null && _time.Bake != null)
                _time.Bake.CancelNorefreshSync();
            _clickSceneBase = -1;
            // The scripted pointer must not outlive the script: reset it first so the
            // release below hands the real mouse straight back.
            _input.Pointer.Reset();
            _time.Shutdown();
            _input.ReleaseControl();
            _pauseAtTick = -1;
            _branchTick = -1;
            _states.ReleasePlayerLoadHold();
            _states.ClearHistory();
            _recordedTailTick = -1;
            _branchState = null;
            _branchMode = false;
            _waitForControl = false;
            _sceneSettleFrames = 0;
            _captureStartAfterWait = false;
            _stepRecordedThisFrame = false;
            // A script's weapons header must not leak into whatever is loaded or played next.
            WeaponUnlock.OwnedOverride = null;
            if (announce)
                _plugin.Toast("Stopped. Clock released.", false);
        }

        public void ToggleFastForward()
        {
            SetFastForward(!FastForward);
        }

        public void SetFastForward(bool enabled)
        {
            if (enabled && VideoRecording)
            {
                FastForward = false;
                _plugin.Toast("Fast-forward is off while a video is recording.", true);
                return;
            }
            FastForward = enabled;
            _time.Turbo = enabled || PlaybackSpeed > 1.001;
            if (enabled)
                WarnIfTraceSlowsFastForward();
            _plugin.Toast(enabled ? "Fast-forward on (uncapped rendering)." : "Fast-forward off.", false);
        }

        bool _traceWarned;

        /// <summary>
        /// The determinism trace scans every rigidbody and enemy on every tick, which caps
        /// fast-forward far below what the game can simulate. Say so once per session.
        /// </summary>
        void WarnIfTraceSlowsFastForward()
        {
            if (_traceWarned || _plugin.TraceDeterminism == null || !_plugin.TraceDeterminism.Value)
                return;
            _traceWarned = true;
            _plugin.Toast("Trace determinism is on: it scans every object each tick and slows fast-forward a lot. " +
                          "Turn it off in Advanced for full speed.", true);
        }

        public void SetPlaybackSpeed(double speed)
        {
            if (speed < 0.05)
                speed = 0.05;
            if (speed > 8.0)
                speed = 8.0;
            PlaybackSpeed = speed;
            // Wall-clock throttling lives in TimeController: the simulation delta
            // and the physics step are identical at every playback speed.
            _time.PlaybackSpeed = speed;
            _time.Hold = false;
            _time.Turbo = FastForward || speed > 1.001;
        }

        // ------------------------------------------------------------------
        // NoRefresh (FFF) fast-forward
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs headless to the next FFF breakpoint (or to a manual target), mirroring GoldSrc's
        /// r_norefresh: the simulation is not altered in any way, only the 3D screen is not
        /// drawn, so the same script reaches the breakpoint many times faster than real time.
        ///
        /// Called once per rendered frame while playing, before the frame decides whether it
        /// simulates. It only switches the mode on and off; the ticks themselves are the normal
        /// one-tick-per-frame path in <see cref="ExecuteTick"/>.
        /// </summary>
        void UpdateNoRefresh()
        {
            // Optimizer attempts: the run alone picks fast-forward or headless (SetOptimizerNoRefresh);
            // script FFF markers and breakpoints are ignored until it ends.
            if (_optimAttempts)
                return;
            if (_script == null || !_plugin.NoRefreshFastForward.Value || VideoRecording)
            {
                // Disabled in the config while a run was going, or a video is recording
                // (it must capture every tick drawn): give the screen back.
                EndNoRefresh(false);
                return;
            }

            // A paused clock, a hold or the game's own pause menu means no ticks are running:
            // put the screen back so the user can see where the run is. The one exception is
            // the single-frame pause Play uses to run tick 0 itself (_resumeAfterFirstTick):
            // that is not the user pausing, so the first segment stays dark from tick 0.
            bool startupStep = _resumeAfterFirstTick && !_waitForControl;
            if ((_time.Paused && !startupStep) || _time.Hold || _time.GamePaused)
            {
                EndNoRefresh(false);
                return;
            }

            if (_pendingNoRefreshTick >= 0)
            {
                _noRefreshTargetTick = Mathf.Clamp(_pendingNoRefreshTick, 0, _script.TotalTicks);
                _pendingNoRefreshTick = -1;
            }

            if (!_noRefreshActive)
            {
                // Arm the first breakpoint ahead of the cursor. After a breakpoint has been
                // shown, this picks the next one, so every segment before an FFF marker is
                // headless and rendering returns exactly on each marker's following framebulk.
                if (_noRefreshTargetTick <= CurrentTick)
                    _noRefreshTargetTick = _script.NextNoRefreshBreakTick(CurrentTick);
                if (_noRefreshTargetTick > CurrentTick)
                    BeginNoRefresh();
                return;
            }

            if (CurrentTick >= _noRefreshTargetTick)
            {
                // The breakpoint: this is the tick the framebulk after FFF owns, so rendering
                // (and normal playback speed) comes back here.
                EndNoRefresh(true);
                // A pause here would end an optimizer run that is seeking its start tick.
                if (_plugin.PauseAtNoRefreshBreak.Value && !OptimizerRunning)
                    PauseForAuthoring();
            }
        }

        /// <summary>
        /// FFF through the wait-for-control drop: the script's first segment is headless from
        /// the level's first frame, not from the landing. The clock is paused while waiting, so
        /// no tick is counted; frames free-flow uncapped with no 3D rendering, each advancing
        /// exactly the frametime they advance at normal speed, so nothing in the drop changes.
        /// </summary>
        void UpdateNoRefreshWhileWaiting()
        {
            if (_script == null || !_plugin.NoRefreshFastForward.Value || VideoRecording || _time.GamePaused)
            {
                EndNoRefresh(false);
                return;
            }
            if (_pendingNoRefreshTick >= 0)
            {
                _noRefreshTargetTick = Mathf.Clamp(_pendingNoRefreshTick, 0, _script.TotalTicks);
                _pendingNoRefreshTick = -1;
            }
            if (_noRefreshActive)
                return;
            if (_noRefreshTargetTick <= CurrentTick)
                _noRefreshTargetTick = _script.NextNoRefreshBreakTick(CurrentTick);
            if (_noRefreshTargetTick > CurrentTick)
                BeginNoRefresh();
        }

        void BeginNoRefresh()
        {
            _noRefreshActive = true;
            _noRefreshStartTick = CurrentTick;
            _time.EnterNoRefresh();
            WarnIfTraceSlowsFastForward();
            // An optimizer seek (every attempt on Full FFF Replay) would repeat this toast per attempt.
            if (_optimRunner == null)
                _plugin.Toast("NoRefresh: fast-forwarding from tick " + CurrentTick + " to " +
                              _noRefreshTargetTick + " with no 3D rendering. F5 or F11 aborts.", false);
        }

        /// <summary>Restore rendering and leave NoRefresh. Safe to call when it is not active.</summary>
        void EndNoRefresh(bool reachedBreakpoint)
        {
            if (!_noRefreshActive && !_time.NoRefreshActive)
                return;

            long ticks = _time.NoRefreshTicks;
            double seconds = _time.NoRefreshSeconds;
            double rate = _time.NoRefreshTicksPerSecond;
            _noRefreshActive = false;
            _pendingNoRefreshTick = -1;
            _time.ExitNoRefresh();

            if (reachedBreakpoint)
            {
                _plugin.Toast("NoRefresh reached tick " + CurrentTick + " (" + ticks + " ticks in " +
                              seconds.ToString("0.00") + " s, " + rate.ToString("0") +
                              " ticks/s); rendering restored.", false);
            }
            // An abort (F5 / F11) is announced by the pause or stop toast; nothing to add.
        }

        /// <summary>
        /// Fast-forward to an arbitrary tick without editing the script: the same headless
        /// NoRefresh run, just with a manual breakpoint. Starts playback when idle, and holds
        /// the request until the startup scene has loaded.
        /// </summary>
        public bool RunNoRefreshToTick(int targetTick)
        {
            if (_script == null)
            {
                LastError = "Load a script before running NoRefresh.";
                _plugin.Toast(LastError, true);
                return false;
            }
            if (!_plugin.NoRefreshFastForward.Value)
            {
                LastError = "FFF NoRefresh fast-forward is disabled in the General tab.";
                _plugin.Toast(LastError, true);
                return false;
            }
            if (VideoRecording)
            {
                LastError = "NoRefresh is off while a video is recording.";
                _plugin.Toast(LastError, true);
                return false;
            }

            targetTick = Mathf.Clamp(targetTick, 0, _script.TotalTicks);
            if (targetTick <= CurrentTick)
            {
                LastError = "NoRefresh target " + targetTick + " is not ahead of tick " + CurrentTick + ".";
                _plugin.Toast(LastError, true);
                return false;
            }

            if (Mode == EngineMode.Recording)
                StopRecording(true);

            if (Mode == EngineMode.Idle)
            {
                // Play() rewinds to tick 0 and may start a scene load; the request survives
                // both because it is consumed by UpdateNoRefresh, not by Play itself.
                if (!Play())
                    return false;
            }
            else if (Mode == EngineMode.Paused)
            {
                Mode = EngineMode.Playing;
                _time.Paused = false;
                _time.ClearPendingSteps();
                _time.KickTick();
                _time.ApplyImmediately();
                _input.EndFreeLook();
                _input.SetActive(true);
            }

            _pendingNoRefreshTick = targetTick;
            _noRefreshTargetTick = -1;
            _plugin.Toast("NoRefresh queued: fast-forward to tick " + targetTick + ".", false);
            return true;
        }

        /// <summary>Abort a headless run now: rendering returns and playback pauses at this tick.</summary>
        public void AbortNoRefresh()
        {
            if (!_time.NoRefreshActive)
            {
                _plugin.Toast("NoRefresh is not running.", false);
                return;
            }
            long ticks = _time.NoRefreshTicks;
            EndNoRefresh(false);
            PauseForAuthoring();
            _plugin.Toast("NoRefresh aborted at tick " + CurrentTick + " after " + ticks +
                          " tick(s); rendering restored.", false);
        }

        /// <summary>Tick at which the player died in the current run, or -1 while alive.</summary>
        public int DeathTick { get; private set; } = -1;
        /// <summary>Why <see cref="DeathTick"/> was set: health or the kill plane.</summary>
        public string DeathCause { get; private set; }

        /// <summary>
        /// Records a death (health 0 or below the kill plane) once, for the HUD and the log. The
        /// level is not reloaded here: with "Pause on death (kill plane)" off the game's own
        /// death restart is held back too (GameToggles), so the author decides what comes next.
        /// Cleared as soon as the player is alive again (savestate, rewind, checkpoint).
        /// </summary>
        void UpdateDeathRecord()
        {
            if (Mode == EngineMode.Idle || Mode == EngineMode.Starting || _bridge.PlayerComponent == null)
            {
                DeathTick = -1;
                DeathCause = null;
                return;
            }
            float health;
            float hardDamage;
            bool belowPlane = _bridge.PlayerBelowKillPlane;
            bool noHealth = _bridge.TryGetHealth(out health, out hardDamage) && health <= 0f;
            if (!belowPlane && !noHealth)
            {
                DeathTick = -1;
                DeathCause = null;
                return;
            }
            if (DeathTick >= 0)
                return;
            DeathTick = CurrentTick;
            DeathCause = belowPlane ? "fell below the kill plane" : "health 0";
            if (_optimAttempts)
                return;
            string message = "Player died at tick " + CurrentTick + " (" + DeathCause + ").";
            if (!_plugin.PauseOnDeathKillPlane.Value)
                message += " The level is not reloaded: load a savestate, rewind (F4) or reload the script.";
            _plugin.LogInfo(message);
            // With the pause on, the kill-plane pause below shows its own message.
            if (!(belowPlane && _plugin.PauseOnDeathKillPlane.Value))
                _plugin.Toast(message, true);
        }

        public void Rewind()
        {
            if (_script == null || Mode == EngineMode.Recording)
                return;

            bool restored = false;
            if (_states.HasStartState && _states.StartState.SceneName == _bridge.SceneName)
            {
                SaveResult result = _states.Restore(_states.StartState);
                restored = result.Success;
            }

            if (!_time.Active)
                _time.Activate(FrametimeAtTick(0));
            else
                _time.SetHostFrametime(FrametimeAtTick(0));
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = false;
            _skipFirstFrame = false;
            _resumeAfterFirstTick = false;
            _suppressRecordFrame = false;
            // Back to tick 0: drop the resume-only bulk frame adjust with the resume itself.
            if (_script != null)
                _script.SetFrameAdjust(-1, 0);
            CurrentTick = 0;
            CurrentTime = 0.0;
            Finished = false;
            _anglesInitialized = false;
            _currentLine = null;
            _currentLineIndex = -1;
            _startupPending = false;
            _startupSceneLoaded = false;
            _startupWaitFrames = 0;
            ResetTimer();
            Mode = EngineMode.Paused;
            _pauseAtTick = -1;
            _states.ClearHistory();
            _recordedTailTick = -1;
            _branchMode = false;
            ResetLookAtState();
            InitAnglesFromGame();
            _input.BeginFreeLook(VirtualPitch, VirtualYaw);

            if (restored)
                _plugin.Toast("Rewound to tick 0 (start state restored).", false);
            else if (_states.HasStartState)
                _plugin.Toast("Rewound to tick 0, but the start state could not be restored.", true);
            else
                _plugin.Toast("Rewound script to tick 0. No start state was captured.", true);
        }

        // ------------------------------------------------------------------
        // Recording
        // ------------------------------------------------------------------

        /// <summary>
        /// Set by the GUI. Called before a user stop (F10, F11, the Stop buttons) saves the
        /// recording over a file that already exists; returning true means the GUI asks
        /// "Are you sure?" and calls <see cref="StopRecording"/> itself once the author agrees.
        /// </summary>
        public Func<string, bool> ConfirmRecordingOverwrite;

        /// <summary>The file a recording stop saves to.</summary>
        public string RecordingPath
        {
            get
            {
                return string.IsNullOrEmpty(_recordingPath)
                    ? Path.Combine(_plugin.ScriptDirectory, "recording.uktas")
                    : _recordingPath;
            }
        }

        public void ToggleRecording()
        {
            if (Mode == EngineMode.Recording)
                RequestStopRecording();
            else
                StartRecording();
        }

        /// <summary>
        /// A user-initiated recording stop: saves straight away, unless that would overwrite an
        /// existing file and the GUI takes over to ask first. While it asks, the recording is
        /// paused so no ticks are recorded behind the dialog.
        /// </summary>
        public void RequestStopRecording()
        {
            if (Mode != EngineMode.Recording)
                return;
            string target = RecordingPath;
            bool exists;
            try { exists = File.Exists(target); }
            catch (Exception) { exists = false; }
            if (exists && ConfirmRecordingOverwrite != null && ConfirmRecordingOverwrite(target))
            {
                if (!_time.Paused)
                {
                    _time.Paused = true;
                    _time.Hold = false;
                    _time.ClearPendingSteps();
                }
                return;
            }
            StopRecording(true);
        }

        public void StartRecording()
        {
            if (Mode == EngineMode.Recording)
                return;
            StopInternal(false);

            // Record into the script that is already open. Only a session with no
            // script at all creates a fresh document.
            bool reusedScript = _script != null;
            if (!reusedScript)
                _script = TasScript.CreateDefault(_plugin.DefaultSeed.Value, _plugin.DefaultHostFrametime.Value);

            if (reusedScript)
            {
                if (CurrentTick > 0)
                {
                    // Continue from where playback was paused: anything after this
                    // tick is replaced by the new take.
                    _script.TruncateAtTick(CurrentTick);
                }
                else
                {
                    // A freshly loaded script sits at tick 0 with no matching game
                    // state ahead of it, so append after the existing run instead of
                    // silently discarding authored frames.
                    CurrentTick = _script.TotalTicks;
                }
                CurrentTime = _script.TotalTime;
            }
            else
            {
                CurrentTick = 0;
                CurrentTime = 0.0;
            }

            // Capture the player's weapons and loadout into the headers when they describe nothing
            // yet. A script that already has frames and a loadout keeps it: recording more input
            // after those frames must not rewrite the gear they were made with.
            if (!reusedScript || _script.TotalTicks == 0 ||
                (_script.Properties.Weapons == null && !_script.Properties.HasLoadout))
            {
                string captureError;
                CaptureLoadoutIntoScript(out captureError);
            }

            if (!string.IsNullOrEmpty(_scriptPath))
            {
                _recordingPath = _plugin.ResolveScriptSavePath(_scriptPath);
            }
            else
            {
                string fileName = string.IsNullOrEmpty(_plugin.RecordingFile.Value) ? "recording.uktas" : _plugin.RecordingFile.Value;
                _recordingPath = _plugin.ResolveScriptSavePath(fileName);
            }

            Mode = EngineMode.Recording;
            Finished = false;
            _currentLine = null;
            _currentLineIndex = -1;
            _anglesInitialized = false;
            _recordButtons = TasButtons.None;

            _time.Activate(_script.Properties.HostFrametime);
            // Recording starts paused. F5 / Step drives the clock from here, so
            // pressing Record never starts simulating the game on its own.
            _time.Paused = true;
            _time.Hold = false;
            _time.ClearPendingSteps();
            _time.Turbo = false;
            _skipFirstFrame = true;
            _suppressRecordFrame = false;
            _rng.Seed(_script.Properties.Seed);
            _input.SetActive(true);
            InitAnglesFromGame();
            _plugin.Toast("Recording into " + Path.GetFileName(_recordingPath) + " from tick " +
                          CurrentTick + ". F5 starts, F6 steps.", false);
        }

        public void StopRecording(bool save)
        {
            if (Mode != EngineMode.Recording)
                return;
            _script.MergeAdjacent();
            if (save)
            {
                _recordingPath = RecordingPath;
                if (SaveScript(_recordingPath))
                {
                    _scriptPath = _recordingPath;
                    _plugin.Toast("Recorded " + _script.TotalTicks + " ticks.", false);
                }
            }
            if (_plugin.AutoSaveToDisk.Value && !string.IsNullOrEmpty(_scriptPath))
                AutoSaveScript();
            Mode = EngineMode.Idle;
            Finished = true;
            FastForward = false;
            _time.Turbo = false;
            _time.ClearPendingSteps();
            _skipFirstFrame = false;
            _resumeAfterFirstTick = false;
            _suppressRecordFrame = false;
            _time.Shutdown();
            _input.SetActive(false);
            _input.ReleaseAll();
        }

        // ------------------------------------------------------------------
        // Frame hooks
        // ------------------------------------------------------------------

        /// <summary>Rendered frames the title screen gets to build its buttons before tick 1.</summary>
        const int MenuSettleFrames = 90;

        int _stallTick = -1;
        int _stallFrames;


        /// <summary>
        /// If playback is "running" but the tick counter has not moved for 300 rendered
        /// frames, write the clock state to the log once so a stall can be diagnosed
        /// from LogOutput.log instead of guessed at.
        /// </summary>
        void WatchForStall()
        {
            bool shouldAdvance = Mode == EngineMode.Playing && !_time.Paused && !Finished &&
                                 !_waitForControl && !_startupPending;
            if (!shouldAdvance || CurrentTick != _stallTick)
            {
                _stallTick = shouldAdvance ? CurrentTick : -1;
                _stallFrames = 0;
                return;
            }
            if (++_stallFrames == 300)
                _plugin.LogWarning("Playback stalled at tick " + CurrentTick + ": " + _time.StatusLine() +
                                   "  gamePaused=" + _bridge.IsGamePaused() +
                                   "  realAcc=" + _time.PendingRealSeconds.ToString("0.#####"));
        }

        // ------------------------------------------------------------------
        // norefresh checkpoints
        // ------------------------------------------------------------------

        /// <summary>
        /// The last checkpoint, in script order, whose cached state is still valid: the same
        /// checksum above it and the same level instance loaded. Null when nothing can resume.
        /// Entries whose checksum no longer matches the script are dropped here.
        /// </summary>
        /// <summary>
        /// One line of the checkpoint/resume comparison: tick, position, velocity, ground
        /// contact, held buttons and the fixed-step state, at exactly the boundary a resume has
        /// to match. Logged for the checkpoint tick and for the tick after it in both runs, so
        /// the two logs show whether the first resumed tick stepped physics like the original.
        /// </summary>
        void LogResumeProbe(string tag)
        {
            if (_plugin.TraceDeterminism == null || !_plugin.TraceDeterminism.Value)
                return;
            Vector3 position;
            Vector3 velocity;
            // While a load hold pins V1, the Rigidbody is kinematic and reads zero velocity;
            // compare against the saved body instead so the determinism probe is honest.
            if (!_states.TryGetHeldPlayerPosition(out position))
                _bridge.TryGetPlayerPosition(out position);
            if (!_states.TryGetHeldPlayerVelocity(out velocity))
                _bridge.TryGetPlayerVelocity(out velocity);
            bool onGround, sliding, dashing, jumping;
            bool known = _bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping);
            _plugin.LogInfo("probe " + tag + " tick " + CurrentTick + ": pos " + position.ToString("0.#####") +
                            " vel " + velocity.ToString("0.#####") +
                            " grounded " + (known ? onGround.ToString() : "?") +
                            " sliding " + (known ? sliding.ToString() : "?") +
                            " buttons " + _lastButtons +
                            " acc " + _time.PhysicsAccumulatorSeconds.ToString("0.######") +
                            " steps " + _time.LastTickSteps);
        }

        bool PlayerGrounded()
        {
            bool onGround, sliding, dashing, jumping;
            return _bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping) && onGround;
        }

        NorefreshEntry FindNorefreshHit()
        {
            if (!_plugin.UseNorefresh.Value || _script == null)
                return null;

            IList<TasCheckpoint> checkpoints = _script.Checkpoints;
            List<string> hashes = new List<string>(checkpoints.Count);
            for (int i = 0; i < checkpoints.Count; i++)
                hashes.Add(checkpoints[i].Hash);
            _states.PruneNorefresh(hashes);

            int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            NorefreshEntry best = null;
            for (int i = 0; i < checkpoints.Count; i++)
            {
                NorefreshEntry entry = _states.GetNorefresh(checkpoints[i].Hash);
                if (entry != null && entry.SceneHandle == handle && entry.Tick == checkpoints[i].Tick &&
                    (_norefreshHitLimit < 0 || entry.Tick <= _norefreshHitLimit))
                    best = entry;
            }
            return best;
        }

        /// <summary>
        /// Caches the state the first time a run reaches a checkpoint. Called from EndFrame on the
        /// frame whose tick precedes the checkpoint, so the state is the world as that frame left
        /// it: before the checkpoint tick's physics, after the frame's own Update. A resume then
        /// runs the checkpoint tick on an ordinary frame, which gives it exactly one physics step
        /// and one full-delta Update, like continuous playback.
        /// </summary>
        void CaptureNorefreshIfDue()
        {
            // Optimizer attempts play throwaway candidates: caching one per attempt would only grow memory.
            if (!_plugin.UseNorefresh.Value || _script == null || _optimAttempts)
                return;
            int offset = _plugin.NorefreshTickOffset != null ? _plugin.NorefreshTickOffset.Value : 0;
            TasCheckpoint checkpoint = _script.CheckpointAt(CurrentTick + offset);
            if (checkpoint == null || _states.HasNorefresh(checkpoint.Hash))
                return;
            NorefreshInput input = new NorefreshInput
            {
                Buttons = _lastButtons,
                ReleaseButtons = _releaseButtons,
                PointerActive = _input.Pointer.Active,
                PointerPosition = _input.Pointer.Position,
                PointerButtons = (_input.Pointer.Left ? ButtonLeft : 0) |
                                 (_input.Pointer.Right ? ButtonRight : 0) |
                                 (_input.Pointer.Middle ? ButtonMiddle : 0),
                PointerGlideTicks = _input.Pointer.GlideTicksLeft,
                PointerGlideTarget = _input.Pointer.GlideTarget
            };
            // The capture offset can move the state a few ticks either side of the marker, so the
            // script time stored with it comes from the marker's own tick, not from the frame:
            // the resume sets CurrentTick and CurrentTime from the entry and they must agree, or
            // every 'change ... over' ramp and the level clock start some ticks off.
            //
            // A negative offset caches the state at the end of that tick's own frame, so its
            // Update-side clock (level timer/framecount, weapon recharge, style freshness) is one
            // frame past the boundary the resume starts on. That frame is rewound out of the
            // state, or the resumed run counts one frame more than continuous playback does.
            double clockRewind = offset < 0 ? -offset * _script.FrametimeAtTick(checkpoint.Tick) : 0.0;
            if (_states.CaptureNorefresh(checkpoint.Hash, checkpoint.Label, checkpoint.Tick,
                                         VirtualPitch, VirtualYaw, _script.TimeAtTick(checkpoint.Tick), input,
                                         clockRewind))
            {
                _plugin.LogInfo("norefresh checkpoint '" + checkpoint.Label + "' cached at tick " + checkpoint.Tick + ".");
                // Optional sync bake: every tick from the resume boundary to the next bake block
                // or the script end is recorded on this continuous run into a temp .ukbake, so a
                // resume can write them back and re-sync every tick. It is stamped with the
                // script's hash over exactly that tick range.
                if (_plugin.NorefreshBakeSync != null && _plugin.NorefreshBakeSync.Value)
                    _time.Bake.BeginNorefreshSync(_script, checkpoint.Tick + 1);
                else
                    _time.Bake.CancelNorefreshSync();
                // Continuous-playback half of the determinism pair: the resume writes the same
                // row right after its restore, so the two can be compared directly.
                if (_plugin.TraceDeterminism != null && _plugin.TraceDeterminism.Value)
                {
                    _resumeProbeTick = checkpoint.Tick + 1;
                    LogResumeProbe("checkpoint");
                    _trace.Checkpoint(checkpoint.Tick, _bridge, _time.PhysicsAccumulatorSeconds,
                                      _time.LastTickSteps, VirtualPitch, VirtualYaw, _rng.UnityStateHash(),
                                      PlayerGrounded());
                }
            }
        }

        /// <summary>Restores the chosen checkpoint after Play has started, and moves the tick counter to it.</summary>
        void ApplyNorefreshHit()
        {
            NorefreshEntry entry = _norefreshHit;
            _norefreshHit = null;
            if (entry == null)
                return;

            // A checkpoint resume is a raw-input determinism test: never let baked playback
            // write the recorded world over it. StartPlayback clears this for a fresh run.
            BakeManager.PureInputOnly = true;

            // Clear input before the load, as a normal load does, so no button or
            // queued click from the run that cached the checkpoint carries into it.
            _input.ReleaseAll();
            _input.SetActive(true);
            SaveResult result = _states.RestoreNorefresh(entry);
            if (!result.Success)
            {
                LastError = result.Message;
                _plugin.Toast("norefresh restore failed: " + result.Message, true);
                return;
            }

            ResetLookAtState();
            // A restored checkpoint keeps its own random state, so no reseed at tick 0.
            _reseedAtFirstTick = false;
            CurrentTick = entry.Tick;
            CurrentTime = entry.Time;
            VirtualPitch = entry.Pitch;
            VirtualYaw = entry.Yaw;
            // Debug "Bulk frames" dial: the framebulk the resume starts in may hold a few ticks
            // more or fewer for this playback. It is a transient timeline adjustment, so the
            // script, its checksum and anything saved from it keep the authored counts.
            if (_script != null)
            {
                int bulkFrames = _plugin.NorefreshBulkFrames != null ? _plugin.NorefreshBulkFrames.Value : 0;
                TickLocation resumeLine = _script.Locate(entry.Tick);
                _script.SetFrameAdjust(bulkFrames != 0 && resumeLine.Valid ? resumeLine.LineIndex : -1, bulkFrames);
            }
            // Probe pair for the log: this tick as restored, then the tick after it. Comparing
            // those two lines with continuous playback shows which of tick/clock/pose drifts.
            _norefreshProbeTick = entry.Tick + 1;
            _plugin.LogInfo("norefresh probe at resume: " + NorefreshProbeText());
            // Sync bake: every tick recorded after this boundary is written over the same
            // resumed ticks. ArmNorefreshSync re-hashes those rows first, so an edited range
            // leaves nothing to load and the resume plays live as before.
            if (_plugin.NorefreshBakeSync != null && _plugin.NorefreshBakeSync.Value)
                _time.Bake.ArmNorefreshSync(_script, entry.Tick + 1);
            else
                _time.Bake.CancelNorefreshSync();
            // Auto bake_end: only a norefresh resume does this. The bake block the resume
            // lands in is closed when the sync window ends, as if a bake_end stood there;
            // the script, its block ranges and its .ukbake files are not changed.
            if (_plugin.NorefreshBakeSync != null && _plugin.NorefreshBakeSync.Value)
                _time.Bake.ArmNorefreshBakeEnd(entry.Tick,
                    _plugin.NorefreshSyncTicks != null ? _plugin.NorefreshSyncTicks.Value : 0);
            else
                _time.Bake.CancelNorefreshAutoEnd();
            // Raw input state, not just the action mask: a 'click' queued by the tick before the
            // checkpoint must still be released on the resumed tick, and a pointer left active by
            // an earlier 'pointer'/'glide' command must keep its position and buttons. Releasing
            // everything above and restoring the held mask here reproduces the exact frame edge
            // state continuous playback has at the start of this tick.
            _lastButtons = entry.Input.Buttons;
            _releaseButtons = entry.Input.ReleaseButtons;
            _input.Pointer.Active = entry.Input.PointerActive;
            _input.Pointer.Position = entry.Input.PointerPosition;
            _input.Pointer.Left = (entry.Input.PointerButtons & ButtonLeft) != 0;
            _input.Pointer.Right = (entry.Input.PointerButtons & ButtonRight) != 0;
            _input.Pointer.Middle = (entry.Input.PointerButtons & ButtonMiddle) != 0;
            _input.Pointer.GlideTicksLeft = entry.Input.PointerGlideTicks;
            _input.Pointer.GlideTarget = entry.Input.PointerGlideTarget;
            // ReleasePending zeroes the one-tick wheel value at the start of the tick; keeping the
            // captured notch would inject it twice.
            _input.Pointer.Scroll = 0f;
            _anglesInitialized = true;
            // The checkpoint is mid-run, so the start-of-level wait does not apply to it.
            _waitForControl = false;
            _captureStartAfterWait = false;
            // StartPlayback armed the neutral warm-up frame a fresh run needs. A resume must not
            // spend it: it runs _input.InjectNeutral(), which drops the held mask the checkpoint
            // just restored, so the first tick would see every held button as a NEW press (an
            // extra jump on a held 'j') and one edge-triggered action would fire twice.
            _skipFirstFrame = false;
            // Diagnostic: compare the restored state with the one the continuous run recorded at
            // the same tick, while both are still in hand. A mismatch here is the moment the
            // resume stopped matching, not a later symptom.
            if (_plugin.TraceDeterminism != null && _plugin.TraceDeterminism.Value)
            {
                _resumeProbeTick = entry.Tick + 1;
                LogResumeProbe("restored");
                string verdict = _trace.Restored(entry.Tick, _bridge, _time.PhysicsAccumulatorSeconds,
                                                 _time.LastTickSteps, VirtualPitch, VirtualYaw, _rng.UnityStateHash(),
                                                 PlayerGrounded());
                if (verdict != null)
                {
                    if (_trace.LastRestoreMatched)
                        _plugin.LogInfo("Determinism trace: " + verdict);
                    else
                        _plugin.LogWarning("Determinism trace: " + verdict);
                }
            }
            // Do NOT execute the restored tick here.
            //
            // The cached state is the end of the frame before this tick: its physics step has not
            // run yet, and that frame's own Update accounting (level timer, dash recharge and
            // whatever the game moves in Update) is already inside it. Resuming therefore lets the
            // next ordinary frame run the tick: Unity runs exactly one physics step for it, and the
            // game's Update runs with the tick's full frametime, exactly as continuous playback did.
            // Executing it on this frame instead would run it at the frozen capture delta: the tick
            // would be counted, its physics would be the checkpoint's own step (double), and the
            // frame's Update accumulation - timer and recharge - would be lost.
            _resumeTickCheck = entry.Tick;
            _time.ClearPendingSteps(true);
            _time.Paused = false;
            _plugin.Toast("Resumed from norefresh checkpoint at tick " + entry.Tick + ".", false);
        }

        /// <summary>
        /// One line of the values a resume has to match: the script tick and clock, the game's own
        /// level clock (StatsManager.seconds, the framecount), the player position and the angles
        /// the current framebulk resolved. Logged at the restored tick and at the tick after it.
        /// </summary>
        string NorefreshProbeText()
        {
            string position;
            Transform player = _bridge.PlayerTransform;
            if (player != null)
            {
                Vector3 p = player.position;
                position = p.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " +
                           p.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " " +
                           p.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                position = "(no player)";
            }
            float timer;
            if (!_bridge.TryGetLevelTime(out timer))
                timer = 0f;
            return "tick " + CurrentTick +
                   " time " + CurrentTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                   " timer " + timer.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                   " pos " + position +
                   " pitch " + VirtualPitch.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                   " yaw " + VirtualYaw.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Forget every cached checkpoint: the next Play runs the script from tick 0.</summary>
        public void ClearNorefreshCache()
        {
            _states.ClearNorefresh();
            _plugin.Toast("norefresh cache cleared; the next Play runs from tick 0.", false);
        }

        /// <summary>
        /// Baking, "Skip Baked Blocks to End": BakeManager has just put the world into the baked state
        /// at the end of a bake block, at the end of a frame, so the playback cursor moves past the
        /// block and the next frame runs <paramref name="tick"/> live. Only valid while a script is
        /// playing or paused on it; anything else is ignored.
        /// </summary>
        internal bool BakeJumpTo(int tick, double time, float pitch, float yaw)
        {
            if (_script == null || (Mode != EngineMode.Playing && Mode != EngineMode.Paused) || Finished)
                return false;
            if (tick <= CurrentTick || tick > _script.TotalTicks)
                return false;
            ResetLookAtState();
            // The tick-0 reseed is not skipped: BakeManager.TrySkip runs it (ApplyPendingReseed)
            // before it writes the baked RNG, so a skipped block seeds like a played one. One still
            // pending here was skipped by the caller; it is reported, and cleared so a later return to
            // tick 0 does not seed a second time (a played run also consumes it exactly once).
            if (_reseedAtFirstTick && CurrentTick == 0)
                _plugin.LogWarning("Bake jump from tick 0 without the tick-0 RNG reseed; RNG may differ from a live run.");
            _reseedAtFirstTick = false;
            CurrentTick = tick;
            CurrentTime = time;
            VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            VirtualYaw = yaw;
            // A special frame right after the block holds the buttons of the last framebulk inside it.
            TickLocation last = _script.Locate(tick - 1);
            TasFrameLine owner = last.Valid ? OwningFramebulk(last.LineIndex) : null;
            if (owner != null)
                _lastButtons = owner.Buttons;
            _anglesInitialized = true;
            _waitForControl = false;
            _captureStartAfterWait = false;
            if (CurrentTick >= _script.TotalTicks)
                FinishPlayback();
            return true;
        }

        /// <summary>
        /// The tick-0 reseed of a fresh run (StartPlayback(true)), when it is still pending. ExecuteTick
        /// runs it before tick 0; a bake skip of a block that starts at tick 0 runs it before writing
        /// the baked RNG, so bake and non-bake runs seed identically. True when it seeded.
        /// </summary>
        internal bool ApplyPendingReseed()
        {
            if (CurrentTick != 0 || !_reseedAtFirstTick || _script == null)
                return false;
            _reseedAtFirstTick = false;
            _rng.Seed(_script.Properties.Seed);
            return true;
        }

        /// <summary>A bake skip that ran <see cref="ApplyPendingReseed"/> failed: tick 0 runs live and reseeds itself.</summary>
        internal void RearmFirstTickReseed()
        {
            if (CurrentTick == 0)
                _reseedAtFirstTick = true;
        }

        public void BeginFrame()
        {
            RenderedFrames++;
            WatchForStall();

            if (_afterAlign != null)
            {
                if (Mode == EngineMode.Starting)
                    _input.ReleaseControl();
                // Wait until the queued align frame has really advanced Unity's clock: a frame
                // spent at the game's scale 0 (pause menu, hitstop) changes nothing, and the
                // restore would then start on a different sub-step boundary.
                if (!_time.AlignmentPending && --_afterAlignDelay <= 0)
                {
                    Action action = _afterAlign;
                    _afterAlign = null;
                    action();
                }
                return;
            }

            if (_skipFirstFrame)
            {
                _skipFirstFrame = false;
                if (Mode == EngineMode.Recording)
                    _suppressRecordFrame = true;
                _input.InjectNeutral();
                return;
            }

            if (Mode == EngineMode.Starting)
            {
                _input.ReleaseControl();
                if (_startupLoadDelay > 0)
                {
                    if (--_startupLoadDelay == 0)
                        RequestStartupLoad();
                    return;
                }
                _startupWaitFrames++;

                // Loading an Addressable scene does not always surface a
                // sceneLoaded callback (it is what made load_level time out).
                // Scene.handle changes on every load, including a reload of the
                // same scene, so it is a dependable completion signal.
                if (!_startupSceneLoaded)
                {
                    int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                    if (_startupSceneHandle != -1 && handle != _startupSceneHandle)
                    {
                        _startupSceneLoaded = true;
                        _startupWaitFrames = 0;
                        _bridge.InvalidateScene();
                        _time.ResetPacingAfterLoad();
                        _plugin.LogInfo("Startup scene changed (handle " + _startupSceneHandle + " -> " + handle +
                                        ") without a sceneLoaded callback.");
                    }
                }

                // The title screen has no player: give its UI a moment to build instead.
                bool menuReady = _startupType == TasStartType.MainMenu && _startupWaitFrames > MenuSettleFrames;
                if (_startupSceneLoaded &&
                    (menuReady || (_startupType != TasStartType.MainMenu &&
                                   (_bridge.PlayerComponent != null || _startupWaitFrames > 180))))
                    StartPlayback(true);
                else if (_startupWaitFrames > 900)
                {
                    LastError = "Timed out waiting for the startup scene/player.";
                    _plugin.Toast(LastError, true);
                    StopInternal(false);
                }
                return;
            }

            // 'wait_scene': the script clock is stopped until the active scene changes
            // (a menu click started a load), then a few settle frames pass. The hold does
            // not count as ticks, so load times never shift the script's timeline.
            if (Mode == EngineMode.Playing && _holdScene)
            {
                _time.Paused = true;
                ReleasePending();
                _input.ReleaseControl();
                int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                bool changed = handle != _holdSceneBaseHandle || _scenesLoaded > _holdSceneBaseCount;
                if (!changed)
                {
                    // A load that never finishes must not freeze the script silently.
                    if (++_holdWaited > HoldTimeoutFrames)
                    {
                        _holdScene = false;
                        _clickSceneBase = -1;
                        LastError = "Level load did not finish within " + HoldTimeoutFrames + " frames; playback paused.";
                        _plugin.Toast(LastError, true);
                        PauseForAuthoring();
                    }
                    return;
                }
                if (!_holdSceneArrived)
                {
                    // The load is done. The settle frames below stay frozen and the pacing is
                    // reset, so the new scene starts from the same state however long loading
                    // took and the wall time spent loading cannot turn into an extra tick.
                    _holdSceneArrived = true;
                    _time.ResetPacingAfterLoad();
                }
                if (_holdSettle > 0)
                {
                    _holdSettle--;
                    return;
                }
                _holdScene = false;
                _clickSceneBase = -1;
                _input.SetActive(true);
                if (_time.PendingSteps == 0)
                    _time.RequestStep(1);
                return;
            }

            if (Mode == EngineMode.Playing && _waitForControl)
            {
                _time.Paused = true;
                ReleasePending();
                _input.ReleaseControl();
                // The drop free-flows one frametime of game time per armed frame. Counted so a
                // recording captures it as video frames, and run headless when tick 0 sits in
                // an FFF segment.
                if (_time.FrameArmed && !_time.GamePaused)
                {
                    WaitFrames++;
                    WaitTime += Time.deltaTime;
                }
                UpdateNoRefreshWhileWaiting();
                // Control is handed over on the exact frame the player is granted control AND
                // touches the ground: the drop ends in a landing, and that landing is the tick
                // the script starts from. No settle frames, so nothing shifts it.
                bool onGround;
                bool sliding;
                bool dashing;
                bool jumping;
                bool grounded = _bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping) && onGround;
                if (!_bridge.PlayerControlActivated || !grounded)
                    return;
                _waitForControl = false;
                if (_captureStartAfterWait)
                {
                    ApplyLoadout();
                    ApplyStartFist();
                }
                if (_captureStartAfterWait && _plugin.CaptureStartState.Value)
                    _states.CaptureStartState(0, 0.0);
                _captureStartAfterWait = false;
                _input.SetActive(true);
                if (_time.PendingSteps == 0)
                    _time.RequestStep(1);
                return;
            }

            UpdateDeathRecord();

            // An optimizer attempt that falls out of the level is scored as a death and rolled back
            // by the runner (TrySample) instead of pausing, which would end the whole run.
            // Only with "Pause on death (kill plane)" on; off, the death is only recorded above.
            if (Mode == EngineMode.Playing && _plugin.PauseOnDeathKillPlane.Value &&
                _bridge.PlayerBelowKillPlane && !_optimAttempts)
            {
                LastError = "Player fell below the kill plane; playback paused to protect the level state.";
                _plugin.Toast(LastError, true);
                PauseForAuthoring();
                return;
            }

            if (Mode == EngineMode.Paused || (Mode == EngineMode.Standalone && _time.Paused))
                _input.UpdateStaging();

            if (Mode == EngineMode.Standalone)
            {
                bool run = _time.BeginFrame();
                if (run)
                {
                    _states.ReleasePlayerLoadHold();
                    ApplyStandaloneInput();
                }
                else if (!_time.PacingSkip)
                    _input.InjectNeutral();
                return;
            }

            // NoRefresh is entered, left at its breakpoint, and aborted here, before the
            // frame decides whether it simulates.
            if (Mode == EngineMode.Playing)
                UpdateNoRefresh();

            bool advance = _time.BeginFrame();

            if (Mode == EngineMode.Recording)
            {
                if (advance)
                    _recordButtons = _input.ReadPhysicalButtons();
                // Recording leaves the devices to the player, except while the TAS window
                // captures input: then the real keys and mouse are released from the game.
                if (_input.HardwareBlocked)
                    _input.InjectNeutral();
                return;
            }

            if (Mode == EngineMode.Playing || Mode == EngineMode.Paused)
            {
                if (advance && !Finished)
                {
                    long profileStart = FastForwardProfiler.Now();
                    ExecuteTick();
                    FastForwardProfiler.AddTick(FastForwardProfiler.Ms(profileStart));
                }
                else if (!advance && !_time.PacingSkip)
                    _input.InjectNeutral();
                if (_pauseAtTick >= 0 && CurrentTick >= _pauseAtTick)
                    PauseForAuthoring();
                return;
            }

            _input.InjectNeutral();
        }

        void ApplyStandaloneInput()
        {
            TasButtons buttons = _input.ReadPhysicalButtons();
            float pitch;
            float yaw;
            if (!_bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                pitch = VirtualPitch;
                yaw = VirtualYaw;
            }
            VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            VirtualYaw = yaw;
            // Do not inject here. The unpatched game input path consumes the
            // physical keyboard/mouse for this exact tick; the read above is for
            // HUD/current-state reporting only.
            // Exception: while the TAS window captures input, the player's real keys and mouse
            // are released from the devices so typing or clicking in it cannot reach the game.
            if (_input.HardwareBlocked)
                _input.InjectNeutral();
            _lastFrametime = _time.HostFrametime;
            _lastButtons = buttons;
            CurrentTick++;
            CurrentTime += _time.HostFrametime;
        }

        /// <summary>Attempt loop of a running brute-force optimizer. Null when no optimizer is running.</summary>
        TasOptimizerRunner _optimRunner;

        public bool OptimizerRunning { get { return _optimRunner != null && !_optimRunner.Finished; } }

        /// <summary>
        /// Optimizer projectile aim lock. Called on the first tick of each framebulk, before its angles resolve,
        /// with that framebulk's line index. Returns true with a world direction (camera to target) to overwrite
        /// that tick's pitch/yaw; false keeps the script's angles.
        /// </summary>
        public delegate bool OptimAimLockFunc(int lineIndex, out Vector3 direction);
        public OptimAimLockFunc OptimAimLock;

        /// <summary>True while the run plays toward its start tick (first seek, or a Full FFF Replay).</summary>
        public bool OptimizerSeeking { get { return OptimizerRunning && _optimRunner.Seeking; } }

        // Set once a run is at its start tick: attempts run at fast-forward (or headless) and script
        // FFF markers, norefresh caching and the kill-plane pause stay off until the run ends.
        bool _optimAttempts;
        // The user's fast-forward toggle before the run, given back when it ends.
        bool _optimPrevFastForward;
        // Highest tick a norefresh checkpoint may resume Play at; -1 is unlimited. Set only while an
        // optimizer seek calls Play, so a cached checkpoint never lands past the start tick.
        int _norefreshHitLimit = -1;

        /// <summary>
        /// Attaches an optimizer run and drives playback to its start tick from wherever it is:
        /// headless FFF up to the start tick (plain fast-forward when FFF is disabled), restarting the
        /// script first when the cursor is already past it. The run detaches itself when it finishes or
        /// is cancelled.
        /// </summary>
        public bool StartOptimizerRun(TasOptimizerRunner runner, out string error)
        {
            error = null;
            if (runner == null) { error = "no runner."; return false; }
            if (OptimizerRunning) { error = "an optimizer run is already active."; return false; }
            int start = runner.Optimizer.Config.StartTick;
            _optimPrevFastForward = FastForward;
            if (!SeekForOptimizer(start, false, out error))
                return false;
            _optimRunner = runner;
            // Already on the start tick (paused there): begin now, before the next frame plays past it.
            if (Mode == EngineMode.Playing && CurrentTick == start &&
                !_startupPending && !_waitForControl && _afterAlign == null)
                runner.OnFrameEnd();
            return true;
        }

        /// <summary>Full FFF Replay: restarts the script from tick 0 and runs it headless to the start tick.</summary>
        internal bool ReplayForOptimizer(int startTick, out string error)
        {
            return SeekForOptimizer(startTick, true, out error);
        }

        bool SeekForOptimizer(int tick, bool restart, out string error)
        {
            error = null;
            if (_script == null) { error = "no script loaded."; return false; }
            if (tick < 0 || tick >= _script.TotalTicks) { error = "start tick " + tick + " is outside the script."; return false; }
            if (Mode == EngineMode.Recording) { error = "stop recording before starting the optimizer."; return false; }

            _optimAttempts = false;
            if (restart || Mode == EngineMode.Idle || Mode == EngineMode.Standalone || CurrentTick > tick)
            {
                _norefreshHitLimit = tick;
                bool started = Play();
                _norefreshHitLimit = -1;
                if (!started)
                {
                    error = string.IsNullOrEmpty(LastError) ? "playback could not start." : LastError;
                    return false;
                }
            }
            else if (Mode == EngineMode.Paused)
            {
                Mode = EngineMode.Playing;
                _time.Paused = false;
                _time.ClearPendingSteps();
                _time.KickTick();
                _time.ApplyImmediately();
                _input.EndFreeLook();
                _input.SetActive(true);
            }
            _pauseAtTick = -1;

            if (CurrentTick < tick)
            {
                if (_plugin.NoRefreshFastForward.Value && !VideoRecording)
                {
                    // Consumed by UpdateNoRefresh, so it survives Play's scene load and align frame.
                    _pendingNoRefreshTick = tick;
                    _noRefreshTargetTick = -1;
                }
                else if (!VideoRecording)
                {
                    FastForward = true;
                    _time.Turbo = true;
                }
            }
            return true;
        }

        /// <summary>
        /// Playback is at the run's start tick: the seek's headless FFF ends (rendering returns) and the
        /// attempts run at fast-forward, or headless when <paramref name="headless"/> is set.
        /// </summary>
        internal void BeginOptimizerAttempts(bool headless)
        {
            if (_noRefreshActive)
                EndNoRefresh(false);
            _pendingNoRefreshTick = -1;
            _noRefreshTargetTick = -1;
            _optimAttempts = true;
            if (headless)
            {
                SetOptimizerNoRefresh(true);
            }
            else if (!VideoRecording)
            {
                FastForward = true;
                _time.Turbo = true;
            }
        }

        /// <summary>The run ended: rendering and the user's fast-forward toggle come back.</summary>
        internal void EndOptimizerAttempts()
        {
            _optimAttempts = false;
            _optimHold = false;
            SetOptimizerNoRefresh(false);
            FastForward = _optimPrevFastForward;
            _time.Turbo = FastForward || PlaybackSpeed > 1.001;
        }

        /// <summary>Engine-side playback cursor at the optimizer start tick, kept beside the master savestate.</summary>
        internal struct OptimCursor
        {
            public int Tick;
            public double Time;
            public float Pitch;
            public float Yaw;
            public TasButtons Buttons;
            public double Frametime;
        }

        internal OptimCursor CaptureOptimizerCursor()
        {
            OptimCursor cursor = new OptimCursor();
            cursor.Tick = CurrentTick;
            cursor.Time = CurrentTime;
            cursor.Pitch = VirtualPitch;
            cursor.Yaw = VirtualYaw;
            cursor.Buttons = _lastButtons;
            cursor.Frametime = _lastFrametime;
            return cursor;
        }

        /// <summary>
        /// Puts the playback cursor back at the start tick after the master savestate was restored. The
        /// savestate only restores the game; without this the next attempt would run on from the tick the
        /// previous attempt ended at.
        /// </summary>
        internal void RestoreOptimizerCursor(OptimCursor cursor)
        {
            ResetLookAtState();
            // The master carries its own random state, so no reseed at tick 0.
            _reseedAtFirstTick = false;
            CurrentTick = _script != null ? Mathf.Clamp(cursor.Tick, 0, _script.TotalTicks) : cursor.Tick;
            CurrentTime = cursor.Time;
            VirtualPitch = cursor.Pitch;
            VirtualYaw = cursor.Yaw;
            _lastButtons = cursor.Buttons;
            _lastFrametime = cursor.Frametime;
            _anglesInitialized = true;
            _currentLine = null;
            _currentLineIndex = -1;
            _pauseAtTick = -1;
            Finished = false;
        }

        public void StopOptimizerRun()
        {
            if (_optimRunner != null)
                _optimRunner.Cancel();
            _optimRunner = null;
        }

        /// <summary>
        /// Headless (FFF) optimizer mode: NoRefresh without the engine's fast-forward target. One tick per
        /// rendered frame with no camera drawing, so OnGUI and the GL overlays keep running. Separate from
        /// the user's NoRefresh state, so F5/F11 and the banner logic are not affected.
        /// </summary>
        public void SetOptimizerNoRefresh(bool on)
        {
            if (on)
            {
                if (!_time.NoRefreshActive)
                    _time.EnterNoRefresh();
            }
            else if (_time.NoRefreshActive && !_noRefreshActive)
            {
                _time.ExitNoRefresh();
            }
        }

        // The next frame holds still (no tick) so the attempt's last frame can render before the runner
        // rolls back; see ShowOptimizerEndFrame.
        bool _optimHold;

        /// <summary>
        /// Headless optimizer only: the attempt's last tick has just run, so this frame is allowed to render
        /// (cameras restored before they cull) and the next frame is held, with no tick, while the runner
        /// scores and rolls back after the picture is out. False when the screen is not dark anyway.
        /// </summary>
        internal bool ShowOptimizerEndFrame()
        {
            if (!_optimAttempts || !_time.NoRefreshActive)
                return false;
            _time.RenderThisFrame();
            _optimHold = true;
            return true;
        }

        public void EndFrame()
        {
            // Optimizer attempts run here, between game frames. With no run attached this is a null check.
            if (_optimRunner != null)
            {
                // The held frame after an attempt's last frame: the runner (below) rolls back during it,
                // and the next frame ticks again unless it asks for another hold.
                _optimHold = false;
                // Pausing, stopping or leaving playback ends the run so the user's script is restored.
                // Play's startup (scene load, align frame, wait for control) is waited out: a seek or a
                // Full FFF Replay restarts the script and runs through it.
                bool startingUp = Mode == EngineMode.Starting ||
                                  (Mode == EngineMode.Playing &&
                                   (_startupPending || _waitForControl || _afterAlign != null));
                if (!startingUp)
                {
                    if (Mode != EngineMode.Playing)
                        StopOptimizerRun();
                    else
                        _optimRunner.OnFrameEnd();
                }
                if (_optimRunner != null && _optimRunner.Finished)
                    _optimRunner = null;
            }

            // A paused, stepped or finished script freezes the game completely between ticks.
            // While the engine waits on the game itself (startup, a scene load and its settle
            // frames, player control, a queued alignment) the game's scripts must keep running.
            _time.FullFreezeAllowed = Mode != EngineMode.Idle && Mode != EngineMode.Starting &&
                                      !_startupPending && !_holdScene && !_waitForControl && _afterAlign == null;

            // A load that ran the game's checkpoint respawn restores only after the room clones
            // it replaced have been destroyed for real: Object.Destroy is deferred to the end of
            // the respawn frame, and writing the savestate into objects that are already queued
            // to die loses everything the respawn built. Two frames is one flush plus a settle.
            if (_checkpointReloadWait > 0 && --_checkpointReloadWait == 0)
            {
                int slot = _checkpointReloadSlot;
                _checkpointReloadSlot = -1;
                if (slot >= 0)
                {
                    _plugin.LogInfo("Restoring savestate slot " + slot + " after the checkpoint reload.");
                    PerformLoadState(slot, true);
                }
            }

            if (Mode == EngineMode.Recording)
            {
                if (_suppressRecordFrame)
                    _suppressRecordFrame = false;
                else if (_time.LastFrameAdvanced)
                    RecordFrame();
                // A paused recording (the state StartRecording leaves the clock in)
                // records nothing; F6 steps record exactly the frames that ran.
            }

            if (Mode == EngineMode.Standalone || Mode == EngineMode.Starting)
            {
                if (Mode == EngineMode.Starting)
                    _input.ReleaseControl();
                _time.EndFrame();
                return;
            }

            // A checkpoint belongs to the tick about to run, so its state is taken here, at the
            // end of a frame that ran a tick; only those are valid boundaries. Which marker that
            // boundary belongs to is decided by the debug capture offset (0 = the marker whose
            // tick this frame ran, +1 = one tick earlier, -1 = one tick later).
            if (_time.LastFrameAdvanced)
                CaptureNorefreshIfDue();

            if (_pendingLoadSlot >= 0)
            {
                int slot = _pendingLoadSlot;
                _pendingLoadSlot = -1;
                PerformLoadState(slot);
            }

            // A loaded state that has not run a tick yet stays pinned to the exact saved
            // pose. In a paused frame the game scripts are frozen anyway; this also covers
            // loads made while the tool does not own the clock (Idle/Standalone).
            _states.MaintainPlayerLoadHold();

            // Idle means no tick is coming, so an Idle load would otherwise stay pinned
            // forever. The restore is complete; hand the body back at the end of this frame.
            if (Mode == EngineMode.Idle)
                _states.ReleasePlayerLoadHold();

            ArmNextFrameTime();
            UpdateSlowMotion();
            _time.EndFrame();

            // A single-step frame must not leave the script's camera/input
            // ownership alive after the step has completed.
            if (Mode == EngineMode.Paused && _time.PendingSteps == 0)
            {
                if (_stepRecordedThisFrame)
                {
                    _stepRecordedThisFrame = false;
                    if (_plugin.AutoSaveToDisk.Value)
                        AutoSaveScript();
                }
                _input.BeginFreeLook(VirtualPitch, VirtualYaw);
            }
        }

        void ArmNextFrameTime()
        {
            if (_script == null || Finished)
                return;
            if (Mode != EngineMode.Playing && Mode != EngineMode.Paused)
                return;
            if (CurrentTick >= _script.TotalTicks)
                return;
            // Column 4 of the line owning CurrentTick, inherited from host_frametime
            // when it is '-'. Applied before the frame so the tick runs at its own rate.
            _time.BeginFramebulk(_script.FrametimeAtTick(CurrentTick));
        }

        double FrametimeAtTick(int tick)
        {
            if (_script == null || _script.TotalTicks == 0)
                return _plugin.DefaultHostFrametime.Value;
            return _script.FrametimeAtTick(tick);
        }

        void UpdateSlowMotion()
        {
            // Playback speed is pacing only: TimeController runs one tick per rendered
            // frame and caps the render rate to speed / host_frametime. It never sleeps
            // the main thread, never scales fixedDeltaTime and never writes
            // Time.timeScale; above 1x the frame cap is lifted.
            bool playing = Mode == EngineMode.Playing;
            // An ff line fast-forwards every tick above it, then hands back to the menu speed.
            // NoRefresh counts as fast-forward too: uncapped rate, no frame cap, one tick per
            // rendered frame.
            // A video recording turns every fast-forward off (see VideoRecording).
            bool fastForward = !VideoRecording &&
                               (FastForward || ScriptFastForwardActive || _time.NoRefreshActive);
            _time.PlaybackSpeed = playing ? PlaybackSpeed : 1.0;
            FastForwardProfiler.Enabled = playing && fastForward;
            _time.Hold = _optimHold;
            _time.Turbo = playing && (fastForward || PlaybackSpeed > 1.001);
            // host_framerate style: one tick per rendered frame, no wall-clock pacing.
            _time.FreeRun = playing && (fastForward || _plugin.FrameLockedPlayback.Value);
            // Frame-locked slow motion: cap the render rate to speed / host_frametime so
            // 1.0 is real time. Above 1.0 (or fast-forward) the cap is lifted instead.
            bool capped = playing && _plugin.FrameLockedPlayback.Value && !fastForward && PlaybackSpeed <= 1.001;
            _time.FrameCapFps = capped ? PlaybackSpeed / _time.HostFrametime : 0.0;
            // Keep the world moving while holding tick 1 for player control. A held scene load
            // runs the game only until the scene is in (the loading screen needs game time);
            // after that the settle frames stay frozen so the level starts from a fixed state.
            bool loading = _holdScene && !_holdSceneArrived;
            _time.FreeFlow = playing && (_waitForControl || loading) && !_time.GamePaused;
        }

        void ExecuteTick()
        {
            if (_script == null)
            {
                StopInternal(true);
                return;
            }
            // The load is complete: release the hold on the first tick attempt, including one
            // that turns out to be past the end of the script. Otherwise a load at the final
            // tick would leave V1 kinematic after playback ends.
            _states.ReleasePlayerLoadHold();

            if (CurrentTick >= _script.TotalTicks)
            {
                FinishPlayback();
                return;
            }

            TickLocation location = _script.Locate(CurrentTick);
            if (!location.Valid)
            {
                FinishPlayback();
                return;
            }

            ApplyPendingReseed();

            if (_norefreshProbeTick >= 0 && CurrentTick == _norefreshProbeTick)
            {
                _norefreshProbeTick = -1;
                _plugin.LogInfo("norefresh probe at next tick: " + NorefreshProbeText());
            }

            // State at the start of the tick after the checkpoint: if the resumed tick stepped
            // physics like the original, this probe matches the one the first run logged.
            if (_resumeProbeTick >= 0 && CurrentTick == _resumeProbeTick)
            {
                _resumeProbeTick = -1;
                LogResumeProbe("next-tick");
            }

            if (_plugin.TraceDeterminism != null && _plugin.TraceDeterminism.Value)
            {
                long traceStart = FastForwardProfiler.Now();
                _trace.Row(CurrentTick, _bridge, _time.PhysicsAccumulatorSeconds, _time.LastTickSteps,
                           VirtualPitch, VirtualYaw, _rng.UnityStateHash(), PlayerGrounded());
                FastForwardProfiler.AddTrace(FastForwardProfiler.Ms(traceStart));
            }

            // Optional: keep the ring filled during continuous playback too, so a
            // pause can scrub back through the last N ticks.
            if (Mode == EngineMode.Playing && _plugin.HistoryDuringPlayback.Value)
                _states.RecordHistory(CurrentTick);

            TasFrameLine line = _script.Lines[location.LineIndex];
            _currentLine = line;
            _currentLineIndex = location.LineIndex;

            // A 'click' from the previous tick is released here, before this tick's
            // commands can press anything new.
            ReleasePending();

            // Field 8: in hltas a bulk's commands run on every tick of the bulk
            // unless the individual command is prefixed with "once:". Special
            // frames (save / seed / change ... / target_yaw) act once, on the
            // first tick of the line they occupy.
            TasStartFist fistTarget = TasStartFist.Unchanged;
            if (location.TickWithinLine == 0)
            {
                // Zero-tick marker lines (fist / record_video / record_video_stop) never own a
                // tick, so the ones directly above this line fire here, ahead of its commands.
                fistTarget = ExecuteZeroTickMarkers(location);
                ExecuteSpecialFrame(line);
                ExecuteCommands(line.Commands, location, false);
            }
            else
            {
                ExecuteCommands(line.Commands, location, true);
            }
            _input.Pointer.StepGlide();

            if (!_anglesInitialized)
                InitAnglesFromGame();

            double tickFrametime = line.EffectiveFrametime(_script.Properties.HostFrametime);
            double resolvedPitch = TasFrameLine.ResolveAngle(line.Pitch, VirtualPitch,
                _script.ActivePitchChange(location.LineIndex), location.Time, tickFrametime);
            double resolvedYaw = TasFrameLine.ResolveAngle(line.Yaw, VirtualYaw,
                _script.ActiveYawChange(location.LineIndex), location.Time, tickFrametime);
            if (line.HasTargetYaw)
                resolvedYaw = line.TargetYaw.Resolve(VirtualYaw);

            // look_at: while a look_at line is in effect, a '-' angle column aims at its
            // entity instead of holding the previous angle. Explicit values (absolute,
            // d+/d-, target_yaw) still win for that tick.
            TasFrameLine lookAt = _script.ActiveLookAt(location.LineIndex);
            float aimPitch;
            float aimYaw;
            if (lookAt != null && TryAimAt(lookAt, out aimPitch, out aimYaw))
            {
                if (line.Pitch.Unchanged)
                    resolvedPitch = aimPitch;
                if (line.Yaw.Unchanged && !line.HasTargetYaw)
                    resolvedYaw = aimYaw;
            }

            // Optimizer aim lock: on a tagged firing tick the shot aims at the live projectile.
            Vector3 lockDirection;
            if (OptimAimLock != null && line.IsFramebulk && location.TickWithinLine == 0 &&
                OptimAimLock(location.LineIndex, out lockDirection))
            {
                float lockPitch = 0f;
                float lockYaw = 0f;
                if (AimFromDirection(lockDirection, ref lockPitch, ref lockYaw))
                {
                    resolvedPitch = lockPitch;
                    resolvedYaw = lockYaw;
                }
            }

            // Field 1 autofuncs of the framebulk that owns this tick (a special frame runs
            // under the framebulk before it, like its held buttons).
            TasFrameLine owner = OwningFramebulk(location.LineIndex);
            TasAutofuncs autofuncs = owner != null ? owner.Autofuncs : TasAutofuncs.None;

            // Autofunc 't' (cointhrow) is a one-shot: only the first tick of a framebulk solves
            // the toss angle, and the rest of the bulk keeps that solved angle.
            if ((autofuncs & TasAutofuncs.CoinThrow) != 0 && line.IsFramebulk && location.TickWithinLine == 0)
                ApplyCoinThrow(ref resolvedPitch, ref resolvedYaw, tickFrametime);

            VirtualPitch = Mathf.Clamp((float)resolvedPitch, -89.9f, 89.9f);
            VirtualYaw = (float)resolvedYaw;

            // A special frame (save / seed / target_yaw) is a marker, not a new
            // input state: it holds whatever the previous tick was pressing so a
            // mid-jump savestate does not drop the held keys.
            TasButtons appliedButtons = line.IsFramebulk ? line.Buttons : _lastButtons;
            TasButtons injected = ApplyShotgunSwap(ApplyMovementAutofuncs(appliedButtons, autofuncs));
            injected = ApplyAimAutofuncs(injected, autofuncs);
            injected = ApplyFistDirective(injected, appliedButtons, fistTarget);
            // 'e' turns the view only on the tick it fires, and only for that tick: the angles
            // injected here are separate from VirtualPitch/VirtualYaw, so the script's own angle
            // is back on the next tick.
            float injectPitch = VirtualPitch;
            float injectYaw = VirtualYaw;
            injected = ApplyDeadcoin(injected, autofuncs,
                line.IsFramebulk && location.TickWithinLine == 0, ref injectPitch, ref injectYaw);
            _input.ApplyFrame(injected, _autofuncRepeat, injectPitch, injectYaw);

            // First tick after a norefresh resume: say whether the clock actually spent a
            // physics step on it. A zero here with a full frametime is the resume dropping
            // that tick's simulation, not a later accumulation error.
            if (_resumeTickCheck >= 0 && CurrentTick == _resumeTickCheck)
            {
                _resumeTickCheck = -1;
                Vector3 position;
                Vector3 velocity;
                Vector3 slideDir;
                _bridge.TryGetPlayerPosition(out position);
                _bridge.TryGetPlayerVelocity(out velocity);
                _bridge.TryGetSlideDir(out slideDir);
                bool grounded, sliding, dashing, jumping;
                _bridge.TryGetMovementState(out grounded, out sliding, out dashing, out jumping);
                string detail = " pos " + position.ToString("0.###") + " vel " + velocity.ToString("0.###") +
                                " slideDir " + slideDir.ToString("0.###") + " onGround " + grounded +
                                " sliding " + sliding + " buttons " + appliedButtons;
                if (_time.LastTickSteps == 0)
                    _plugin.LogWarning("Resume tick " + CurrentTick + " ran no physics step (frametime " +
                                       _time.HostFrametime.ToString("0.000000") + " s, phase " +
                                       _time.PhysicsAccumulatorSeconds.ToString("0.000000") + " s)" + detail + ".");
                else
                    _plugin.LogInfo("Resume tick " + CurrentTick + " ran " + _time.LastTickSteps +
                                    " physics step(s), phase " +
                                    _time.PhysicsAccumulatorSeconds.ToString("0.000000") + " s" + detail + ".");
            }

            if (_resumeAfterFirstTick)
            {
                _resumeAfterFirstTick = false;
                _time.Paused = false;
            }

            double frametime = line.EffectiveFrametime(_script.Properties.HostFrametime);
            _lastFrametime = frametime;
            _lastButtons = appliedButtons;

            CurrentTick++;
            CurrentTime += frametime;

            if (CurrentTick >= _script.TotalTicks)
                FinishPlayback();
        }

        // The look_at line and entity last warned about, so a missing entity logs once.
        TasFrameLine _lookAtWarnedLine;
        int _lookAtWarnedId = -1;

        /// <summary>
        /// Drops every name-based pick plus the warning dedup state. Called whenever the
        /// timeline is replaced (playback start, savestate/norefresh load, rewind), because
        /// an ID cached before the restore can name a different object afterwards.
        /// </summary>
        void ResetLookAtState()
        {
            if (_script != null)
                _script.ResetLookAtResolutions();
            _lookAtWarnedLine = null;
            _lookAtWarnedId = -1;
            // A restore/rewind replaces the world under the playback cursor; an SSJ sequence
            // that was mid-slide or mid-wait must not continue from the old state.
            ResetSsjMacro();
        }

        /// <summary>
        /// Pitch and yaw that point the camera from its current position at the look_at
        /// target's center or named hitbox plus the line's world-space offset. The target is an
        /// ID, or for a name line the nearest live entity of that type/category. Pitch is the
        /// script's convention (positive = down, same sign as Unity's euler X); yaw is the
        /// Unity world yaw (euler Y, 0 = +Z, 90 = +X), unwrapped to the turn nearest the
        /// previous yaw so it never jumps by 360. False when the target or camera is missing.
        /// </summary>
        bool TryAimAt(TasFrameLine lookAt, out float pitch, out float yaw)
        {
            pitch = VirtualPitch;
            yaw = VirtualYaw;

            Vector3 origin;
            Transform cameraTransform;
            if (_bridge.TryGetCameraTransform(out cameraTransform))
            {
                origin = cameraTransform.position;
            }
            else
            {
                Transform player = _bridge.PlayerTransform;
                if (player == null)
                    return false;
                origin = player.position;
            }

            // "look_at x y z": a fixed world coordinate, no entity lookup. direction =
            // (target - camera position).normalized; the angles below are derived from it.
            if (lookAt.LookAtIsPoint)
                return AimFromDirection(
                    new Vector3((float)lookAt.LookAtPointX, (float)lookAt.LookAtPointY, (float)lookAt.LookAtPointZ) - origin,
                    ref pitch, ref yaw);

            // "look_at coin": the nearest matching entity is picked once, then held while it
            // stays alive and active. A punched coin is destroyed and a new one is instantiated
            // (DoubleTime/TripleTime), and pooled objects are deactivated, so a dead or inactive
            // target is replaced by the nearest live match on the same tick instead of freezing
            // the aim on its last position. Rewinding before the pick picks again from the
            // restored state.
            int entityId = lookAt.LookAtEntity;
            if (lookAt.LookAtName.Length > 0)
            {
                // Keep this a cheap liveness check: computing the center/hitbox point
                // here would scan the target's colliders twice per tick.
                EntityRegistry.Entity current = lookAt.LookAtResolvedId != 0
                    ? _bridge.Entities.Find(lookAt.LookAtResolvedId)
                    : null;
                bool valid = current != null && current.Component != null &&
                             CurrentTick >= lookAt.LookAtResolvedTick &&
                             string.Equals(lookAt.LookAtResolvedName, lookAt.LookAtName, StringComparison.OrdinalIgnoreCase) &&
                             GameBridge.IsAlive(current.Component) &&
                             current.Component.gameObject.activeInHierarchy;
                if (!valid)
                {
                    EntityRegistry.Entity nearest;
                    float nearestDistance;
                    if (!_bridge.Entities.TryFindNearest(lookAt.LookAtName, origin, out nearest, out nearestDistance))
                    {
                        WarnLookAt(lookAt, 0, "no live entity named '" + lookAt.LookAtName + "'");
                        return false;
                    }
                    lookAt.LookAtResolvedId = nearest.Id;
                    lookAt.LookAtResolvedName = lookAt.LookAtName;
                    lookAt.LookAtResolvedTick = CurrentTick;
                    if (!lookAt.LookAtAnnounced)
                    {
                        lookAt.LookAtAnnounced = true;
                        string subtype = _bridge.Entities.EnemySubtype(nearest.Component);
                        string label = subtype.Length > 0 ? subtype : nearest.TypeName;
                        _plugin.Toast("look_at " + lookAt.LookAtName + ": entity " + nearest.Id + " (" +
                                      label + ", " +
                                      nearestDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                                      " m away).", false);
                    }
                }
                entityId = lookAt.LookAtResolvedId;
            }

            Vector3 target;
            bool hasTarget = _bridge.Entities.TryGetAimPosition(entityId, lookAt.LookAtHitbox, out target);
            if (!hasTarget)
            {
                // Tell a missing hitbox apart from a dead target, so a typo in the
                // hitbox name does not look like the enemy disappeared.
                EntityRegistry.Entity targetEntity = _bridge.Entities.Find(entityId);
                bool liveTarget = targetEntity != null && targetEntity.Component != null &&
                                  GameBridge.IsAlive(targetEntity.Component) &&
                                  targetEntity.Component.gameObject.activeInHierarchy;
                string detail = liveTarget && lookAt.LookAtHitbox.Length > 0
                    ? "hitbox '" + lookAt.LookAtHitbox + "' not found on entity " + entityId
                    : lookAt.LookAtName.Length > 0
                        ? "nearest live '" + lookAt.LookAtName + "' (entity " + entityId + ") disappeared"
                        : "entity " + entityId + " not found or destroyed";
                WarnLookAt(lookAt, entityId, detail);
                return false;
            }
            target += new Vector3((float)lookAt.LookAtOffsetX, (float)lookAt.LookAtOffsetY, (float)lookAt.LookAtOffsetZ);

            return AimFromDirection(target - origin, ref pitch, ref yaw);
        }

        /// <summary>
        /// Pitch/yaw (script convention) for a direction vector; false when it has no length.
        /// The angles depend only on the direction of (target - camera), i.e. on its
        /// normalized form, so the vector is used as is.
        /// </summary>
        bool AimFromDirection(Vector3 d, ref float pitch, ref float yaw)
        {
            float horizontal = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            if (horizontal < 1e-4f && Mathf.Abs(d.y) < 1e-4f)
                return false;
            float rawYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Atan2(d.y, horizontal) * Mathf.Rad2Deg;
            // Straight up or down has no yaw of its own: keep the current one.
            yaw = horizontal < 1e-4f ? VirtualYaw : VirtualYaw + Mathf.DeltaAngle(VirtualYaw, rawYaw);
            return true;
        }

        // Revolver.ThrowCoin: the coin is spawned half a unit below the camera and its
        // Rigidbody gets +20 along the camera forward and +15 along the camera up on top of
        // V1's own velocity (riding rocket included). Pitch and yaw rotate both axes, so the
        // throw angle chooses where the coin leaves V1 with its 25 hu/s relative speed;
        // gravity and the coin's linear drag then shape the arc. Coins delete themselves 5 s
        // after the toss, and once their speed drops under 1 hu/s.
        const float CoinLaunchForward = 20f;
        const float CoinLaunchUp = 15f;
        const float CoinLifeSeconds = 5f;
        /// <summary>A coin's colliders arm 0.1 s after the toss (Coin.StartCheckingSpeed); the
        /// same 0.1 s is the deadcoin window autofunc 'e' aims at.</summary>
        const float CoinArmDelaySeconds = 0.1f;
        /// <summary>The layers Coin.OnCollisionEnter destroys the coin on: level geometry.</summary>
        const int CoinBlockMask = (1 << 8) | (1 << 24);
        /// <summary>Height above the floor under V1 at which a coin is treated as landed.</summary>
        const float CoinFloorClearance = 0.2f;

        /// <summary>
        /// Autofunc 't' (cointhrow). Runs once, on the first tick of the framebulk that holds it,
        /// and sets that tick's view angles to the toss direction that keeps the coin closest to
        /// V1 for the longest while it is still in the air. The search simulates the game's toss
        /// against a simple model of V1's own motion (current velocity; gravity while airborne,
        /// level flight while grounded) and scores only ticks in which the coin is still flying:
        /// a coin that would touch the level stops there, because the game destroys it on that
        /// contact. Each airborne tick is worth 1/(1 + distance^2), so hovering next to V1 counts
        /// far more than drifting 10 units away, and a longer stay keeps adding. Deterministic:
        /// the same player state always solves the same angle.
        /// </summary>
        void ApplyCoinThrow(ref double pitch, ref double yaw, double frametime)
        {
            Transform cameraTransform;
            Vector3 playerPosition;
            Vector3 playerVelocity;
            if (!_bridge.TryGetCameraTransform(out cameraTransform) || cameraTransform == null ||
                !_bridge.TryGetPlayerPosition(out playerPosition) ||
                !_bridge.TryGetPlayerVelocity(out playerVelocity))
                return;

            // A coin inherits the rocket's velocity while V1 rides one, not V1's own.
            Vector3 inheritedVelocity;
            if (!_bridge.TryGetCoinInheritedVelocity(out inheritedVelocity))
                inheritedVelocity = playerVelocity;

            bool grounded = false, sliding, dashing, jumping;
            bool airborne = _bridge.TryGetMovementState(out grounded, out sliding, out dashing, out jumping) && !grounded;

            float groundY = 0f;
            bool hasGround = _bridge.TryGetGroundBelow(playerPosition, out groundY);

            float drag = 0f;
            _bridge.TryGetCoinDrag(out drag);
            float gravity = Physics.gravity.y;
            float dt = (float)(frametime > 0.0 ? frametime : 0.008);
            int maxTicks = Mathf.Clamp(Mathf.RoundToInt(CoinLifeSeconds / Mathf.Max(dt, 0.0001f)), 1, 4000);
            int armTicks = Mathf.CeilToInt(CoinArmDelaySeconds / Mathf.Max(dt, 0.0001f));
            Vector3 cameraPosition = cameraTransform.position;

            float bestScore = float.MinValue;
            float bestPitch = (float)pitch;
            float bestYaw = (float)yaw;

            // Coarse pass over the whole sphere, then two finer passes around the strongest coarse
            // candidates. The coarse pass checks the level every 24 ticks, the refinements every 12
            // and 8, which is fine enough to reject a toss that would hit a wall or ceiling. The
            // best yaw of every pitch row is kept, because the pitch decides how long the coin
            // hangs next to V1; refining only the single best row would stay in its basin.
            var seeds = new List<Vector3>(18);
            for (float testPitch = -86f; testPitch <= 89f; testPitch += 10f)
            {
                float rowYaw = 0f;
                float rowScore = float.MinValue;
                for (int step = 0; step < 18; step++)
                {
                    float testYaw = step * 20f - 180f;
                    float score = ScoreCoinThrow(testPitch, testYaw, cameraPosition, playerPosition,
                        playerVelocity, inheritedVelocity, airborne, hasGround, groundY, gravity, drag,
                        dt, maxTicks, armTicks, 24, bestScore);
                    if (score > rowScore)
                    {
                        rowScore = score;
                        rowYaw = testYaw;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPitch = testPitch;
                        bestYaw = testYaw;
                    }
                }
                seeds.Add(new Vector3(testPitch, rowYaw, rowScore));
            }
            seeds.Sort((a, b) => b.z.CompareTo(a.z));

            int seedCount = Mathf.Min(2, seeds.Count);
            for (int seed = 0; seed < seedCount; seed++)
            {
                float refinePitch = seeds[seed].x;
                float refineYaw = seeds[seed].y;
                for (int pass = 0; pass < 2; pass++)
                {
                    float pitchRadius = pass == 0 ? 8f : 1.5f;
                    float yawRadius = pass == 0 ? 12f : 2f;
                    float pitchStep = pass == 0 ? 2f : 0.75f;
                    float yawStep = pass == 0 ? 3f : 1f;
                    int collisionStep = pass == 0 ? 12 : 8;
                    for (float testPitch = refinePitch - pitchRadius; testPitch <= refinePitch + pitchRadius; testPitch += pitchStep)
                    {
                        for (float offset = -yawRadius; offset <= yawRadius; offset += yawStep)
                        {
                            float testYaw = refineYaw + offset;
                            float score = ScoreCoinThrow(testPitch, testYaw, cameraPosition, playerPosition,
                                playerVelocity, inheritedVelocity, airborne, hasGround, groundY, gravity, drag,
                                dt, maxTicks, armTicks, collisionStep, bestScore);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestPitch = testPitch;
                                bestYaw = testYaw;
                            }
                        }
                    }
                    refinePitch = bestPitch;
                    refineYaw = bestYaw;
                }
            }

            float aimPitch = (float)pitch;
            float aimYaw = (float)yaw;
            if (AimFromDirection(Quaternion.Euler(-bestPitch, bestYaw, 0f) * Vector3.forward,
                                 ref aimPitch, ref aimYaw))
            {
                pitch = Mathf.Clamp(aimPitch, -89.9f, 89.9f);
                yaw = aimYaw;
            }
        }

        /// <summary>
        /// Closeness of one simulated toss over its airborne life. Every tick the coin is still
        /// flying adds 1/(1 + distance^2): 1 on V1, 0.5 at one unit, 0.1 at three, 0.01 at ten.
        /// The simulation stops where the game would destroy the coin: at the floor under V1 or
        /// at a level collider (Coin.OnCollisionEnter layers 8 and 24), after the 0.1 s the coin's
        /// colliders need to arm. The cutoff lets the search skip a candidate that cannot beat the
        /// best score even if it stayed on top of V1 from here on.
        /// </summary>
        float ScoreCoinThrow(float testPitch, float testYaw, Vector3 cameraPosition, Vector3 playerPosition,
            Vector3 playerVelocity, Vector3 inheritedVelocity, bool airborne, bool hasGround, float groundY,
            float gravity, float drag, float dt, int maxTicks, int armTicks, int collisionStep, float cutoff)
        {
            Quaternion rotation = Quaternion.Euler(-testPitch, testYaw, 0f);
            Vector3 forward = rotation * Vector3.forward;
            Vector3 up = rotation * Vector3.up;
            Vector3 coinVelocity = forward * CoinLaunchForward + up * CoinLaunchUp + inheritedVelocity;
            Vector3 coinPosition = cameraPosition - up * 0.5f;
            Vector3 playerSim = playerPosition + Vector3.up * 0.6f;
            Vector3 playerSimVelocity = playerVelocity;
            float damping = 1f / (1f + Mathf.Max(0f, drag) * dt);
            float score = 0f;

            for (int i = 0; i < maxTicks; i++)
            {
                Vector3 previous = coinPosition;
                coinVelocity.y += gravity * dt;
                coinVelocity *= damping;
                coinPosition += coinVelocity * dt;

                if (airborne)
                {
                    playerSimVelocity.y += gravity * dt;
                    playerSim += playerSimVelocity * dt;
                    // Land on the floor under V1 instead of falling through it.
                    if (hasGround && playerSim.y < groundY + 0.6f && playerSimVelocity.y < 0f)
                    {
                        playerSim.y = groundY + 0.6f;
                        playerSimVelocity.y = 0f;
                    }
                }
                else
                {
                    // Grounded: keep the horizontal speed, no falling.
                    playerSim += new Vector3(playerSimVelocity.x, 0f, playerSimVelocity.z) * dt;
                }

                // A coin dies the moment it touches the level, so ticks at or below the floor and
                // ticks after a wall or ceiling hit are not flight and do not score.
                if (hasGround && coinPosition.y <= groundY + CoinFloorClearance)
                    break;
                if (i >= armTicks && collisionStep > 0 && i % collisionStep == 0)
                {
                    RaycastHit hit;
                    if (Physics.Linecast(previous, coinPosition, out hit, CoinBlockMask, QueryTriggerInteraction.Ignore))
                        break;
                }

                // The game deletes the coin the moment its speed drops under 1 hu/s, so that
                // tick is already gone and is not flight.
                if (coinVelocity.sqrMagnitude < 1f)
                    break;

                float distance = (coinPosition - playerSim).magnitude;
                score += 1f / (1f + distance * distance);
                if (score + (maxTicks - i - 1) <= cutoff)
                    return score;
            }
            return score;
        }

        /// <summary>One warning per look_at line and target, so a held angle does not spam the log.</summary>
        void WarnLookAt(TasFrameLine lookAt, int entityId, string detail)
        {
            if (ReferenceEquals(_lookAtWarnedLine, lookAt) && _lookAtWarnedId == entityId)
                return;
            _lookAtWarnedLine = lookAt;
            _lookAtWarnedId = entityId;
            _plugin.Toast("look_at: " + detail + " (tick " + CurrentTick + "); the angle is held.", true);
        }

        // Shotgun swap token 'G2': one step of the sequence per tick.
        // The draw steps run first, then fire + select-other + select-original on one tick.
        const int SwapIdle = 0;
        const int SwapDraw = 1;          // no shotgun out: press slot 2
        const int SwapFirstVariant = 2;  // drawn: pick the first owned variant if another came out
        const int SwapFire = 3;          // primary fire (the pellet pushback is the boost)
        int _swapStep = SwapIdle;
        // 0-based variant index read on the fire step; select-original selects it again.
        int _swapOriginal;
        // Tick the next step is due on. A rewind or seek leaves it stale, which drops the sequence.
        int _swapTick = -1;
        // True once the fire step has spent its one release tick (Fire1 was held into it).
        bool _swapFireReleased;

        // Shotgun swap token 'G1': waits for a shot read from the game, then presses the
        // variation bind of the shotgun variation that is equipped. The game's variation bind
        // re-equips that variation even when it is already equipped, which resets the firing
        // cooldown on the same tick. No slot 2 presses and no release tick are needed.
        // False once a warning about an unreadable variation index has been shown.
        bool _shotSwapVariantWarned;
        // Shot signal of the previous tick: the equipped Shotgun component, its gunReady flag and
        // whether Fire1 was injected. Only compared when read on the tick right before (_shotPrevTick).
        object _shotPrevShotgun;
        bool _shotPrevReady;
        bool _shotPrevFire1;
        int _shotPrevTick = -1;

        /// <summary>
        /// Expands the script-only ShotgunSwap1 / ShotgunSwap2 flags into real inputs for
        /// this tick.
        /// 'G1' (see ApplyShotSwap): every tick that holds it watches the game for a shotgun
        /// shot; the tick that sees the shot presses the equipped variation's bind, which
        /// re-equips that same variation immediately and resets its cooldown for the next shot.
        /// 'G2': a new sequence starts on the first tick of a run of 'G2' ticks. With a
        /// shotgun out: fire, select-other and select-original all on one tick. The select
        /// keys (Variant1/2/3) are used because next/previous variation open the weapon wheel.
        /// With another weapon out, slot 2 is pressed first (and variation 1 if slot 2
        /// brought out a later one).
        /// </summary>
        TasButtons ApplyShotgunSwap(TasButtons buttons)
        {
            bool wanted1 = (buttons & TasButtons.ShotgunSwap1) != 0;
            bool wanted2 = (buttons & TasButtons.ShotgunSwap2) != 0;
            buttons &= ~(TasButtons.ShotgunSwap1 | TasButtons.ShotgunSwap2);
            if (wanted2 && !ScriptShotgunSwapAt(CurrentTick - 1, TasButtons.ShotgunSwap2))
            {
                _swapStep = ShotgunOut() ? SwapFire : SwapDraw;
                _swapTick = CurrentTick;
                _swapFireReleased = false;
            }
            if (_swapStep == SwapIdle || _swapTick != CurrentTick)
            {
                _swapStep = SwapIdle;
                // 'G2' wins when a tick holds both tokens, so 'G1' only runs with no 'G2' step.
                return ApplyShotSwap(buttons, wanted1 && !wanted2);
            }

            switch (_swapStep)
            {
                case SwapDraw:
                    buttons |= TasButtons.Slot2;
                    _swapStep = SwapFirstVariant;
                    break;
                case SwapFirstVariant:
                    if (_bridge.GunVariation > 0)
                    {
                        buttons |= TasButtons.Variant1;
                        _swapStep = SwapFire;
                        break;
                    }
                    goto case SwapFire;
                case SwapFire:
                    // Fire1 held from the previous tick gives the game no new press, so the
                    // shot never happens. Release it for one tick, then fire on the next.
                    if (!_swapFireReleased && (ScriptButtonsAt(CurrentTick - 1) & TasButtons.Fire1) != 0)
                    {
                        buttons &= ~TasButtons.Fire1;
                        _swapFireReleased = true;
                        break;
                    }
                    buttons |= TasButtons.Fire1;
                    // Remember the equipped variant (0-based); an unreadable index counts as variant 1.
                    _swapOriginal = Math.Max(0, _bridge.GunVariation);
                    buttons |= SwapOtherVariantKey(_swapOriginal) | SwapVariantKey(_swapOriginal);
                    _swapStep = SwapIdle;
                    break;
            }
            _swapTick = CurrentTick + 1;
            // A 'G2' step ran on this tick: 'G1' starts no swap here, but the shot signal is
            // still read so the next tick compares against this one.
            return ApplyShotSwap(buttons, false);
        }

        /// <summary>
        /// 'G1': shot-triggered shotgun re-equip. The shot is read from the game every tick, not
        /// guessed from tick timing: Shotgun.gunReady (on the equipped weapon) going from
        /// true on the previous tick to false on this tick, on the same Shotgun component,
        /// with Fire1 injected on the previous tick (the tick the game fired on; alt-fire
        /// pumps and core ejects also clear gunReady, so ticks without Fire1 do not count).
        /// Inputs are injected before the game's Update, so the state read here is the
        /// result of the previous tick. On the tick that sees the shot, 'G1' presses the
        /// variation bind of the equipped variation (blue/green/red). GunControl treats the
        /// bind as a fresh equip of that slot and variation, so the weapon is re-enabled with
        /// its cooldown reset immediately -- a blue-to-blue swap costs one tick, not a slot 2
        /// cycle. Then 'G1' waits for the next shot. A shot is only acted on while the tick
        /// holds 'G1'. State is keyed on tick numbers only, so a replay reads the same signal
        /// on the same ticks.
        /// </summary>
        TasButtons ApplyShotSwap(TasButtons buttons, bool wanted)
        {
            object shotgun;
            bool ready;
            bool haveShotgun = _bridge.TryGetShotgunReady(out shotgun, out ready);
            bool shot = haveShotgun && _shotPrevTick == CurrentTick - 1 && _shotPrevFire1 &&
                        _shotPrevReady && !ready && ReferenceEquals(shotgun, _shotPrevShotgun);

            if (wanted && shot)
            {
                TasButtons bind = VariationBindKey(_bridge.GunVariation);
                if (bind != TasButtons.None)
                {
                    buttons |= bind;
                    _shotSwapVariantWarned = false;
                }
                else if (!_shotSwapVariantWarned)
                {
                    _shotSwapVariantWarned = true;
                    _plugin.Toast("G1: the equipped shotgun variation could not be read; no re-equip on this shot.", true);
                }
            }

            _shotPrevShotgun = haveShotgun ? shotgun : null;
            _shotPrevReady = haveShotgun && ready;
            _shotPrevFire1 = (buttons & TasButtons.Fire1) != 0;
            _shotPrevTick = CurrentTick;
            return buttons;
        }

        /// <summary>Variation bind for a 0-based variation index: 0 blue, 1 green, 2 red.</summary>
        static TasButtons VariationBindKey(int index)
        {
            switch (index)
            {
                case 0: return TasButtons.Variant1;
                case 1: return TasButtons.Variant2;
                case 2: return TasButtons.Variant3;
                default: return TasButtons.None;
            }
        }

        /// <summary>Select key for a 0-based variant index: 0 = Variant1, 1 = Variant2, 2 and up = Variant3.</summary>
        static TasButtons SwapVariantKey(int index)
        {
            return index <= 0 ? TasButtons.Variant1 : (index == 1 ? TasButtons.Variant2 : TasButtons.Variant3);
        }

        /// <summary>Select key for the variant to swap to: Variant2 from index 0, Variant1 from index 1 or 2.</summary>
        static TasButtons SwapOtherVariantKey(int index)
        {
            return index <= 0 ? TasButtons.Variant2 : TasButtons.Variant1;
        }

        /// <summary>Buttons of the framebulk that owns the tick (None when the tick is outside the script).</summary>
        TasButtons ScriptButtonsAt(int tick)
        {
            if (tick < 0 || _script == null)
                return TasButtons.None;
            TickLocation at = _script.Locate(tick);
            if (!at.Valid)
                return TasButtons.None;
            TasFrameLine bulk = OwningFramebulk(at.LineIndex);
            return bulk != null ? bulk.Buttons : TasButtons.None;
        }

        /// <summary>The framebulk a line belongs to: the line itself, or the last framebulk before a special frame.</summary>
        TasFrameLine OwningFramebulk(int lineIndex)
        {
            for (int i = lineIndex; i >= 0; i--)
            {
                if (_script.Lines[i].IsFramebulk)
                    return _script.Lines[i];
            }
            return null;
        }

        const TasAutofuncs MovementAutofuncs =
            TasAutofuncs.AutoJump | TasAutofuncs.AutoSlide | TasAutofuncs.AutoSlideSpam |
            TasAutofuncs.AutoDash | TasAutofuncs.GroundSlam | TasAutofuncs.SSJMacro;

        /// <summary>
        /// The movement autofuncs ('j', 's', 'S', 'd', 'g', 'm'). They read NewMovement before the game's
        /// Update (this runs in the plugin's early Update, after the frame's physics) and only add
        /// or remove presses on the injected input; the script's held buttons (_lastButtons) do
        /// not change. InputPatcher stamps a press only on the first tick of a held run, so the
        /// jump and dash these autofuncs add are listed in _autofuncRepeat: they count as a new
        /// press on every eligible tick and repeat the attempt until the game accepts it. Slide
        /// is never repeated: a slide hold stays one continuous press.
        /// </summary>
        TasButtons ApplyMovementAutofuncs(TasButtons buttons, TasAutofuncs autofuncs)
        {
            _autofuncRepeat = TasButtons.None;
            // 'S' resets whenever this framebulk does not ask for spam autoslide, so the next
            // spam line starts with a fresh Slide press.
            if ((autofuncs & TasAutofuncs.AutoSlideSpam) == 0)
                _spamAutoslideOn = true;
            // The one-tick engage belongs to both autoslide forms; a resume that does not use it
            // must not leave the flag armed for a later tick.
            if ((autofuncs & TasAutofuncs.AutoSlideMask) == 0)
                _forceAutoslideEngage = false;
            if ((autofuncs & MovementAutofuncs) == 0)
                return buttons;
            bool onGround;
            bool sliding;
            bool dashing;
            bool jumping;
            if (!_bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping))
                return buttons;
            bool jumpCooldown;
            float boostCharge;
            _bridge.TryGetAutofuncState(out jumpCooldown, out boostCharge);

            // Autofunc 'm' (SSJ macro): the exact slide-stop-jump pattern. The landing tick holds
            // Slide for one tick, the next tick releases it, and the configured number of blank
            // ticks follow before Jump. Jumping while Slide is still held would call StopSlide
            // first and see frame 0, which is a plain jump, so the release frame is what makes the
            // boost happen at all. The slide starts again on the landing tick, so the pattern
            // repeats. m/m0 is slide -> blank -> jump (3 ticks); m1 adds one blank (4 ticks); m2
            // adds two (5 ticks). The optional digit is parsed into TasAutofuncs.SSJMacroVariantMask.
            bool ssjMacro = (autofuncs & TasAutofuncs.SSJMacro) != 0;
            int ssjBlankFrames = ssjMacro ? TasInputColumns.SsjMacroBlankFrames(autofuncs) : 0;
            bool ssjRelease = false;
            bool ssjHoldSlide = false;
            if (ssjMacro)
            {
                switch (_ssjMacroPhase)
                {
                    case SsjMacroPhaseIdle:
                        if (sliding)
                        {
                            // Already sliding: count this as the one slide tick, then release.
                            _ssjMacroPhase = SsjMacroPhaseSliding;
                            ssjHoldSlide = true;
                        }
                        else if (onGround && !jumpCooldown && !dashing)
                        {
                            // Landing tick: exactly one Slide input, then the release frame.
                            _ssjMacroPhase = SsjMacroPhaseSliding;
                            ssjHoldSlide = true;
                        }
                        break;

                    case SsjMacroPhaseSliding:
                        if (sliding)
                        {
                            // The slide started on the previous tick. Release it for the first
                            // blank frame; StopSlide starts framesSinceSlide on this tick.
                            _ssjMacroPhase = SsjMacroPhaseWaiting;
                            _ssjMacroBlankTicks = 1;
                            ssjRelease = true;
                        }
                        else if (!onGround)
                        {
                            // The ground disappeared during the setup; start over on landing.
                            ResetSsjMacro();
                        }
                        else if (!jumpCooldown && !dashing)
                        {
                            // The game did not start/keep the slide yet; hold it one more tick.
                            ssjHoldSlide = true;
                        }
                        break;

                    case SsjMacroPhaseWaiting:
                        if (!onGround)
                        {
                            // Do not try an air jump after leaving the floor. The macro starts
                            // its next sequence on the next landing instead.
                            ResetSsjMacro();
                        }
                        else if (_ssjMacroBlankTicks <= ssjBlankFrames)
                        {
                            // One more deliberately blank frame: no Slide and no Jump.
                            _ssjMacroBlankTicks++;
                        }
                        else if (!jumpCooldown)
                        {
                            // m0 jumps here, one frame after the release. m1 waits one extra
                            // blank frame and jumps on recovery frame 2, m2 on frame 3, etc.
                            buttons |= TasButtons.Jump;
                            _autofuncRepeat |= TasButtons.Jump;
                            ResetSsjMacro();
                        }
                        break;
                }
            }
            else
            {
                ResetSsjMacro();
            }

            if ((autofuncs & TasAutofuncs.AutoJump) != 0 && onGround &&
                !(ssjMacro && _ssjMacroPhase != SsjMacroPhaseIdle))
            {
                // Autofunc 'j' (autojump): presses Jump on every grounded tick, so the jump comes
                // on the landing tick and is retried each tick a jumpCooldown swallowed it.
                buttons |= TasButtons.Jump;
                _autofuncRepeat |= TasButtons.Jump;
            }

            // 's', 'S' and 'g' drive Slide. Each tick decides once whether the autofuncs hold it
            // (grounded: 's' holds / 'S' alternates; airborne: 'g'), so they never release and
            // re-press it on the same tick. They only add Slide; a Slide the script holds itself
            // is never removed.
            bool holdSlide = false;
            bool spamAutoslide = (autofuncs & TasAutofuncs.AutoSlideSpam) != 0;
            bool slideEligible = onGround &&
                !(ssjMacro && _ssjMacroPhase == SsjMacroPhaseWaiting);
            if (spamAutoslide && slideEligible)
            {
                // Autofunc 'S' (spam autoslide): the same grounded/jump/SSJ gate as 's', but
                // presses Slide only on alternate eligible ticks. The first eligible tick is an
                // on tick; losing the gate resets the phase, so the next landing starts with a
                // fresh press rather than an off tick.
                bool engage = _forceAutoslideEngage;
                _forceAutoslideEngage = false;
                bool jumpFires = (buttons & TasButtons.Jump) != 0 && !jumpCooldown;
                bool canPress = !jumpFires && !ssjRelease &&
                                (sliding || engage || !jumpCooldown);
                if (canPress)
                {
                    if (_spamAutoslideOn)
                        holdSlide = true;
                    _spamAutoslideOn = !_spamAutoslideOn;
                }
                else
                {
                    _spamAutoslideOn = true;
                }
            }
            else if ((autofuncs & TasAutofuncs.AutoSlide) != 0 && slideEligible)
            {
                // Autofunc 's' (autoslide): holds Slide on every grounded tick, starting with the
                // tick the ground check first reads grounded (the landing tick), so StartSlide runs
                // in that tick's Update and the slide lasts while Slide stays held; the first
                // airborne tick releases it (HandleSlideState stops the slide on the release).
                // Exception, so a jump still works: no hold on a tick whose injected Jump fires
                // (grounded, no jumpCooldown), and no new slide while jumpCooldown runs after a
                // jump unless one is already running. Jump() stops the slide, and a Slide held on
                // the jump tick, or on the tick after it while the ground check still reads
                // grounded, would start a new slide (StartSlide accepts lastJump < 0.06 s).
                bool engage = _forceAutoslideEngage;
                if (engage)
                    _forceAutoslideEngage = false;
                bool jumpFires = (buttons & TasButtons.Jump) != 0 && !jumpCooldown;
                if (!jumpFires && !ssjRelease && (sliding || engage || !jumpCooldown))
                    holdSlide = true;
            }
            if (spamAutoslide && !slideEligible)
                _spamAutoslideOn = true;
            if ((autofuncs & TasAutofuncs.GroundSlam) != 0 && !onGround)
            {
                // Autofunc 'g' (ground slam): holds Slide on every airborne tick where the game
                // would start a slam from the press (GameBridge.TryGetSlamReady: no ground within
                // 3 m, fallTime > 0.5, slamCooldown 0, no slam running), so the slam starts on the
                // first tick it is valid. Other airborne ticks release it: within 2 m of the ground
                // the press would start an air slide, and once the slam runs (heavyFall) the hold
                // is no longer needed. Grounded ticks never hold it for 'g', so 'g' never slides.
                // Because Slide is released while the slam runs, the held-slam landing shockwave
                // (NewMovement.stillHolding) is not produced.
                bool slamReady;
                if (_bridge.TryGetSlamReady(out slamReady) && slamReady)
                    holdSlide = true;
            }
            if (ssjRelease)
                holdSlide = false;
            if (holdSlide || ssjHoldSlide)
                buttons |= TasButtons.Slide;

            if ((autofuncs & TasAutofuncs.AutoDash) != 0 && !dashing && !sliding && boostCharge >= 100f)
            {
                // Autofunc 'd' (autodash): presses Dash on the first tick a dash can start: the
                // previous dash has ended (NewMovement.boost false) and the stamina meter holds a
                // full bar (boostCharge >= 100, what TryDash needs). A running slide also sets
                // boost; it is left alone, because a dash would stop it.
                buttons |= TasButtons.Dash;
                _autofuncRepeat |= TasButtons.Dash;
            }
            return buttons;
        }

        /// <summary>Buttons the movement autofuncs press this tick that must repeat their press edge each tick.</summary>
        TasButtons _autofuncRepeat;

        // SSJ macro sequence: one slide tick, then a release tick and any extra blank ticks, then
        // the jump. A phase is needed because "sliding" alone cannot distinguish the landing tick
        // from the wait before a delayed m1/m2 jump.
        const int SsjMacroPhaseIdle = 0;
        const int SsjMacroPhaseSliding = 1;
        const int SsjMacroPhaseWaiting = 2;
        int _ssjMacroPhase;
        /// <summary>Blank input frames already emitted in the current sequence (the release tick is 1).</summary>
        int _ssjMacroBlankTicks;

        void ResetSsjMacro()
        {
            _ssjMacroPhase = SsjMacroPhaseIdle;
            _ssjMacroBlankTicks = 0;
        }

        /// <summary>Whiplash hook range: HookArm gives up a throw whose line is longer than 300 units.</summary>
        const float WhipAssistRange = 300f;

        /// <summary>
        /// The aiming autofuncs ('c', 'w'). They run after the movement autofuncs and the shotgun
        /// swap, so 'c' sees the final Fire1, and they set the tick's view angles before
        /// ApplyFrame. Like look_at, the snapped angle is kept by later '-' angle columns.
        /// </summary>
        TasButtons ApplyAimAutofuncs(TasButtons buttons, TasAutofuncs autofuncs)
        {
            if ((autofuncs & (TasAutofuncs.CoinAim | TasAutofuncs.WhipAssist)) == 0)
                return buttons;
            Vector3 origin;
            Transform cameraTransform;
            if (_bridge.TryGetCameraTransform(out cameraTransform))
                origin = cameraTransform.position;
            else if (_bridge.PlayerTransform != null)
                origin = _bridge.PlayerTransform.position;
            else
                return buttons;

            bool aimed = false;
            if ((autofuncs & TasAutofuncs.CoinAim) != 0 && (buttons & TasButtons.Fire1) != 0)
            {
                // Autofunc 'c' (coin aim assist): on a tick that fires (Fire1 injected), the view
                // snaps to the nearest active coin that has not been shot yet (Coin.shot), so the
                // hitscan shot hits it. Alt fire (the coin toss) is not a fire tick. No coin: the
                // angles are left as resolved.
                Vector3 coin;
                float pitch = VirtualPitch;
                float yaw = VirtualYaw;
                if (_bridge.TryFindNearestCoin(origin, out coin) && AimFromDirection(coin - origin, ref pitch, ref yaw))
                {
                    VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
                    VirtualYaw = yaw;
                    aimed = true;
                }
            }

            if ((autofuncs & TasAutofuncs.WhipAssist) != 0 && !aimed)
            {
                // Autofunc 'w' (whiplash assist): when the hook is ready to throw (equipped,
                // state Ready, not returning, cooldown 0) and an active hook point is within range
                // with no environment in between, the view snaps to it and Whiplash is pressed.
                // Only that one tick presses, because a press while the hook is pulling cancels
                // the pull. A tick that already aimed at a coin skips it.
                bool ready;
                Vector3 hookPoint;
                float pitch = VirtualPitch;
                float yaw = VirtualYaw;
                if (_bridge.TryGetHookReady(out ready) && ready &&
                    _bridge.TryFindHookPoint(origin, WhipAssistRange, out hookPoint) &&
                    AimFromDirection(hookPoint - origin, ref pitch, ref yaw))
                {
                    VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
                    VirtualYaw = yaw;
                    buttons |= TasButtons.Whiplash;
                }
            }
            return buttons;
        }

        // Autofunc 'e' (deadcoin). A shot coin resolves its chain 0.1 s after the hit
        // (Coin.DelayedReflectRevolver), so a coin thrown inside that window takes over the
        // chain's power (+1) without being ricoshoted. 'e' only does the shot half, and ULTRAKILL
        // needs three ticks for it: it shoots along the camera it read that frame, so a tick that
        // turns and fires at once shoots where the view was a frame earlier. Aim on one tick, fire
        // on the next while still looking at the coin, restore on the tick after.
        int _deadcoinStepTick = -1;
        int _deadcoinStep;          // 0 idle, 1 aimed, 2 fired (waiting to restore)
        float _deadcoinAimPitch;
        float _deadcoinAimYaw;
        bool _deadcoinWarned;

        /// <summary>
        /// Autofunc 'e' (deadcoin), three ticks per shot. Tick 1: if an unshot coin is in the air,
        /// turn the view to the nearest one. Tick 2: keep looking at it and press primary fire,
        /// because the game shoots along the camera it read on that frame. Tick 3: touch nothing,
        /// so the script's own angle is back. It makes no other checks: no gun cooldown test and
        /// no line-of-sight test. The turn only goes into the injected pitch/yaw, never into
        /// VirtualPitch/VirtualYaw, so nothing afterwards is affected. Alt fire is never pressed:
        /// tossing the follow-up coin is the script's own input. One shot per framebulk that holds
        /// 'e'; a rewind arms it again.
        /// </summary>
        TasButtons ApplyDeadcoin(TasButtons buttons, TasAutofuncs autofuncs, bool firstTickOfBulk,
            ref float pitch, ref float yaw)
        {
            // A rewind drops a sequence that is mid-flight.
            if (_deadcoinStep != 0 && CurrentTick < _deadcoinStepTick)
            {
                _deadcoinStep = 0;
                _deadcoinWarned = false;
            }
            // The engine may evaluate a tick more than once; a step only moves on a new tick.
            bool nextTick = CurrentTick != _deadcoinStepTick;

            if (_deadcoinStep == 1)
            {
                // Aimed last tick, so the game is looking at the coin now: fire, keeping the
                // same angle for this tick so the shot goes where the view points.
                pitch = _deadcoinAimPitch;
                yaw = _deadcoinAimYaw;
                if (nextTick)
                {
                    buttons |= TasButtons.Fire1;
                    _autofuncRepeat |= TasButtons.Fire1;
                    _deadcoinStep = 2;
                    _deadcoinStepTick = CurrentTick;
                }
                return buttons;
            }

            if (_deadcoinStep == 2)
            {
                // Restore tick: touch nothing, so the script's own angle is back.
                if (nextTick)
                {
                    _deadcoinStep = 0;
                    _deadcoinStepTick = CurrentTick;
                }
                return buttons;
            }

            if ((autofuncs & TasAutofuncs.Deadcoin) == 0 || !firstTickOfBulk)
                return buttons;

            Vector3 origin;
            Transform cameraTransform;
            if (_bridge.TryGetCameraTransform(out cameraTransform))
                origin = cameraTransform.position;
            else if (_bridge.PlayerTransform != null)
                origin = _bridge.PlayerTransform.position;
            else
                return buttons;

            // No checks on the coin beyond "is there one": the nearest unshot coin is the target.
            Vector3 coin;
            if (!_bridge.TryFindNearestCoin(origin, out coin))
            {
                if (!_deadcoinWarned)
                {
                    _deadcoinWarned = true;
                    _plugin.Toast("deadcoin: no unshot coin in the air to shoot.", true);
                }
                return buttons;
            }

            // Tick 1: turn the view. The shot is left for the next tick, when the game is looking
            // where this tick points the camera.
            if (!AimFromDirection(coin - origin, ref pitch, ref yaw))
                return buttons;
            _deadcoinAimPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            _deadcoinAimYaw = yaw;
            _deadcoinStep = 1;
            _deadcoinStepTick = CurrentTick;
            pitch = _deadcoinAimPitch;
            yaw = _deadcoinAimYaw;
            return buttons;
        }

        /// <summary>True when the framebulk that owns the tick holds the given 'G1' / 'G2' flag.</summary>
        bool ScriptShotgunSwapAt(int tick, TasButtons token)
        {
            if (tick < 0 || _script == null)
                return false;
            TickLocation at = _script.Locate(tick);
            if (!at.Valid)
                return false;
            // Special frames hold the previous framebulk's input.
            for (int i = at.LineIndex; i >= 0; i--)
            {
                if (_script.Lines[i].IsFramebulk)
                    return (_script.Lines[i].Buttons & token) != 0;
            }
            return false;
        }

        /// <summary>True when the equipped weapon is one of the shotgun variants.</summary>
        bool ShotgunOut()
        {
            string name;
            int slot;
            int variation;
            return _bridge.TryGetWeapon(out name, out slot, out variation) &&
                   name.IndexOf("Shotgun", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void FinishPlayback()
        {
            EndNoRefresh(false);
            _trace.Close();
            Finished = true;
            Mode = EngineMode.Paused;
            _time.Paused = true;
            _time.Hold = false;
            _resumeAfterFirstTick = false;
            _time.ClearPendingSteps();
            _input.BeginFreeLook(VirtualPitch, VirtualYaw);
            _plugin.Toast("Playback finished at tick " + CurrentTick + " (" +
                          CurrentTime.ToString("0.###") + "s).", false);
            // Submission guard: a finished run that used baked state is never presented as valid.
            if (!BakeManager.RunIsSubmittable)
            {
                string verdict = "Run finished but is NOT SUBMITTABLE (" + BakeManager.NotSubmittableReason + "). " +
                                 BakeManager.NotLegitWarning;
                _plugin.LogWarning(verdict);
                _plugin.Toast(verdict, true);
            }
        }

        void RecordFrame()
        {
            if (_script == null)
                return;
            float pitch;
            float yaw;
            if (!_bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                pitch = VirtualPitch;
                yaw = VirtualYaw;
            }

            _script.Lines.Add(new TasFrameLine
            {
                Count = 1,
                Buttons = _recordButtons,
                Pitch = AngleToken.Absolute(pitch),
                Yaw = AngleToken.Absolute(NormalizeYaw(yaw)),
                SourceLine = 0
            });
            _script.InvalidateTimeline();
            CurrentTick = _script.TotalTicks;
            CurrentTime = _script.TotalTime;
        }

        static float NormalizeYaw(float yaw)
        {
            while (yaw < 0f)
                yaw += 360f;
            while (yaw >= 360f)
                yaw -= 360f;
            return yaw;
        }

        public void OnSceneLoaded(string sceneName)
        {
            _scenesLoaded++;
            _states.OnSceneChanged();
            // The wall time spent loading must not turn into a tick after the scene is in.
            _time.ResetPacingAfterLoad();

            // One retry of a difficulty header whose PrefsManager was not available at Play.
            if (_difficultyRetryPending)
                ApplyDifficulty(true);

            // A running script owns the inputs and the view through a level load. Releasing
            // here dropped every held key for a tick, and re-reading the camera replaced the
            // scripted yaw/pitch (relative d+ / d- deltas then drift). Both are kept.
            bool scriptRunning = Mode == EngineMode.Playing;
            if (!scriptRunning)
            {
                _anglesInitialized = false;
                _input.ReleaseAll();
            }

            if (_startupPending)
            {
                _startupSceneLoaded = true;
                _startupWaitFrames = 0;
                _plugin.LogInfo("Startup scene loaded: " + sceneName);
                return;
            }

            if (Mode == EngineMode.Recording)
            {
                _plugin.Toast("Scene changed to '" + sceneName + "' while recording; recording continues.", false);
            }
            else if (Mode == EngineMode.Playing && _plugin.PauseOnSceneChange.Value)
            {
                Mode = EngineMode.Paused;
                _time.Paused = true;
                InitAnglesFromGame();
                _input.BeginFreeLook(VirtualPitch, VirtualYaw);
                _plugin.Toast("Scene changed to '" + sceneName + "'; playback paused in free-look.", false);
            }
            else
            {
                _plugin.LogInfo("Scene loaded: " + sceneName);
            }
        }

        public void NotifyStateLoaded()
        {
            ResetLookAtState();
            _anglesInitialized = false;
            // Do not ReleaseAll here: the restore has already put the saved held mask back
            // (SaveStateManager.Timing), and a release on this frame cancels a slide or dash
            // before the first scripted tick can keep it alive. The next tick replaces the mask
            // with the script's own buttons.
            _loadedStateAwaitingPlay = true;
            _forceAutoslideEngage = true;
            _spamAutoslideOn = true;
            float pitch;
            float yaw;
            if (_bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
                VirtualYaw = yaw;
                _anglesInitialized = true;
            }
        }

        void InitAnglesFromGame()
        {
            float pitch;
            float yaw;
            if (_bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                VirtualPitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
                VirtualYaw = yaw;
            }
            else
            {
                VirtualPitch = 0f;
                VirtualYaw = 0f;
            }
            _anglesInitialized = true;
        }

        // ------------------------------------------------------------------
        // Timer and commands
        // ------------------------------------------------------------------

        void ResetTimer()
        {
            _timerRunning = false;
            _timerStart = 0.0;
            _timerElapsed = 0.0;
        }

        void ExecuteLoadCommand()
        {
            if (_script == null || string.IsNullOrEmpty(_script.Properties.LoadCommand))
                return;
            string[] commands = _script.Properties.LoadCommand.Split(';');
            List<string> list = new List<string>(commands.Length);
            for (int i = 0; i < commands.Length; i++)
            {
                string command = commands[i].Trim();
                if (command.Length > 0)
                    list.Add(command);
            }
            ExecuteCommands(list, new TickLocation { AbsoluteTick = 0 }, false);
        }

        /// <summary>
        /// The hltas special frames. "change yaw|pitch" needs no state here: the
        /// line spreads its remaining delta over its own ticks (ResolveYaw /
        /// ResolvePitch), so the camera lands exactly on the target.
        /// </summary>
        void ExecuteSpecialFrame(TasFrameLine line)
        {
            switch (line.Kind)
            {
                case TasFrameKind.Save:
                {
                    int slot = NamedStateSlot(line.SaveName);
                    _states.Save(slot);
                    _plugin.LogInfo("save " + line.SaveName + " -> savestate slot " + slot);
                    _plugin.Toast("save " + line.SaveName + " (slot " + slot + ").", false);
                    break;
                }
                case TasFrameKind.Seed:
                    _rng.Seed(line.SeedValue);
                    _plugin.LogInfo("seed " + line.SeedValue + " at tick " + CurrentTick);
                    break;
            }
        }

        /// <summary>
        /// Runs the fist / record_video / record_video_stop lines that sit directly above the
        /// line starting at this tick, in script order. They take no ticks, so they fire on the
        /// first tick of the next line that does. Returns the last fist target in the run.
        /// </summary>
        TasStartFist ExecuteZeroTickMarkers(TickLocation location)
        {
            TasStartFist fistTarget = TasStartFist.Unchanged;
            int first = _script.ZeroTickRunStart(location.LineIndex);
            for (int i = first; i < location.LineIndex; i++)
            {
                TasFrameLine marker = _script.Lines[i];
                if (marker.Kind == TasFrameKind.RecordVideoStart)
                {
                    if (!_plugin.Video.HandleCommand("bxt_rec", ("start " + marker.RecordFile).Trim()))
                        WarnCommand(location, _plugin.Video.LastError);
                }
                else if (marker.Kind == TasFrameKind.RecordVideoStop)
                {
                    _plugin.Video.Stop("record_video_stop");
                }
                else if (marker.Kind == TasFrameKind.Fist && marker.FistTarget != TasStartFist.Unchanged)
                {
                    fistTarget = marker.FistTarget;
                }
            }
            return fistTarget;
        }

        /// <summary>
        /// Applies a "fist &lt;arm&gt;" directive on the tick it fires. It reads the live arm and,
        /// when the other arm is out, adds the game's normal change-fist action to this injected
        /// tick only. The press is not stored in _lastButtons: a following special frame must
        /// release x, not hold it and swap the arm again on the next tick.
        /// </summary>
        TasButtons ApplyFistDirective(TasButtons injected, TasButtons scriptButtons, TasStartFist target)
        {
            if (target == TasStartFist.Unchanged)
                return injected;
            // The framebulk already presses a fist action: that input decides the arm, so do not
            // add a second change on top of it.
            if ((scriptButtons & (TasButtons.FistChange | TasButtons.FistFeedbacker |
                                  TasButtons.FistKnuckleblaster)) != 0)
                return injected;
            int current = _bridge.CurrentFistVariant;
            if (current < 0 || current == (int)target)
                return injected;
            return injected | TasButtons.FistChange;
        }

        /// <summary>
        /// "save &lt;name&gt;" maps a name onto the fixed savestate slots: a numeric
        /// name is the slot itself, any other name hashes to a stable slot so the
        /// same name always saves and restores the same state.
        /// </summary>
        static int NamedStateSlot(string name)
        {
            int slot;
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                return Mathf.Clamp(slot, 0, SaveStateManager.SlotCount - 1);

            string key = name ?? string.Empty;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < key.Length; i++)
                    hash = hash * 31 + key[i];
                return (hash & 0x7fffffff) % SaveStateManager.SlotCount;
            }
        }

        /// <summary>
        /// Runs a command column. Bulk commands repeat on every tick; those marked
        /// "once:" are skipped after the first. Commands that need a one-shot
        /// (timer, pause, echo) are authored with the marker.
        /// </summary>
        void ExecuteCommands(List<string> commands, TickLocation location, bool skipOnceCommands)
        {
            if (commands == null)
                return;
            for (int i = 0; i < commands.Count; i++)
            {
                string raw = commands[i];
                if (string.IsNullOrEmpty(raw))
                    continue;
                string trimmed;
                bool once = TasScript.SplitOncePrefix(raw, out trimmed);
                if (skipOnceCommands && once)
                    continue;
                if (trimmed.Length == 0)
                    continue;
                int space = trimmed.IndexOf(' ');
                string name = (space < 0 ? trimmed : trimmed.Substring(0, space)).ToLowerInvariant();
                string argument = space < 0 ? string.Empty : trimmed.Substring(space + 1).Trim();

                switch (name)
                {
                    case "load_state":
                    case "loadstate":
                    case "ls":
                    {
                        int slot;
                        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                        {
                            WarnCommand(location, "load_state needs a slot number");
                            break;
                        }
                        SaveResult result = _states.Load(slot);
                        if (!result.Success)
                            WarnCommand(location, result.Message);
                        else
                            NotifyStateLoaded();
                        break;
                    }
                    case "save_state":
                    case "savestate":
                    case "ss":
                    {
                        int slot;
                        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                        {
                            WarnCommand(location, "save_state needs a slot number");
                            break;
                        }
                        _states.Save(slot);
                        break;
                    }
                    case "bxt_rec":
                    case "start_recording":
                        if (!_plugin.Video.HandleCommand(name, argument))
                            WarnCommand(location, _plugin.Video.LastError);
                        break;
                    case "stop_recording":
                        _plugin.Video.Stop("script command");
                        break;
                    case "record_start":
                        // Alias of "bxt_rec start [file]". Mid-script start is handled by VideoRecorder.
                        if (!_plugin.Video.HandleCommand("bxt_rec", ("start " + argument).Trim()))
                            WarnCommand(location, _plugin.Video.LastError);
                        break;
                    case "record_stop":
                        _plugin.Video.Stop("record_stop");
                        break;
                    case "optim_start":
                    case "bxt_tas_optim_init":
                    case "optim_end":
                        // Segment markers for TasOptimizer.FindMarkers. Nothing to do during playback.
                        break;
                    case "echo":
                    case "print":
                    case "log":
                        _plugin.Toast("[" + CurrentTick + "] " + argument, false);
                        break;
                    case "clearcoinpaths":
                        // Wipes the coin path visualizer. Runs on every tick of the bulk, which is
                        // harmless: clearing an already empty set does nothing.
                        if (_plugin.CoinPaths != null)
                            _plugin.CoinPaths.Clear();
                        break;
                    case "weapon":
                    case "slot":
                    {
                        int slot;
                        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) ||
                            slot < 1 || slot > 6)
                        {
                            WarnCommand(location, "weapon needs a slot 1-6");
                            break;
                        }
                        if (!_bridge.SwitchWeaponSlot(slot))
                            WarnCommand(location, "GunControl is not available");
                        break;
                    }
                    case "variation":
                    {
                        int variation;
                        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out variation))
                        {
                            WarnCommand(location, "variation needs a number");
                            break;
                        }
                        _bridge.SetGunVariation(variation);
                        break;
                    }
                    case "seed":
                    {
                        int seed;
                        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                        {
                            WarnCommand(location, "seed needs an integer");
                            break;
                        }
                        _rng.Seed(seed);
                        break;
                    }
                    // ---- Pointer (title screen and in-game menus) ----
                    case "mouse":
                    case "mouse_pos":
                    {
                        float x, y;
                        if (!TryParsePair(argument, out x, out y))
                        {
                            WarnCommand(location, "mouse needs 'mouse <x> <y>' in pixels from the top-left");
                            break;
                        }
                        MovePointer(x, Screen.height - y);
                        break;
                    }
                    case "mousef":
                    case "mouse_frac":
                    {
                        float x, y;
                        if (!TryParsePair(argument, out x, out y))
                        {
                            WarnCommand(location, "mousef needs 'mousef <x> <y>' as 0..1 fractions from the top-left");
                            break;
                        }
                        MovePointer(x * Screen.width, (1f - y) * Screen.height);
                        break;
                    }
                    case "mouse_rel":
                    {
                        float dx, dy;
                        if (!TryParsePair(argument, out dx, out dy))
                        {
                            WarnCommand(location, "mouse_rel needs 'mouse_rel <dx> <dy>' in pixels (+y is down)");
                            break;
                        }
                        Vector2 current = _input.Pointer.Active ? _input.Pointer.Position : _input.LiveMousePosition;
                        MovePointer(current.x + dx, current.y - dy);
                        break;
                    }
                    case "glide":
                    case "glidef":
                    {
                        float x, y, ticks;
                        if (!TryParseTriple(argument, out x, out y, out ticks) || ticks < 1f)
                        {
                            WarnCommand(location, name + " needs '" + name + " <x> <y> <ticks>'");
                            break;
                        }
                        Vector2 target = name == "glidef"
                            ? new Vector2(x * Screen.width, (1f - y) * Screen.height)
                            : new Vector2(x, Screen.height - y);
                        if (!_input.Pointer.Active)
                        {
                            _input.Pointer.Active = true;
                            _input.Pointer.Position = _input.LiveMousePosition;
                        }
                        _input.Pointer.GlideTarget = target;
                        _input.Pointer.GlideTicksLeft = Mathf.Max(1, Mathf.RoundToInt(ticks));
                        break;
                    }
                    case "mouse_down":
                        SetPointerButton(argument, true);
                        break;
                    case "mouse_up":
                        SetPointerButton(argument, false);
                        break;
                    case "click":
                    {
                        // Down for exactly this tick, released automatically on the next.
                        int button = SetPointerButton(argument, true);
                        _releaseButtons |= button;
                        _clickSceneBase = _scenesLoaded;
                        break;
                    }
                    case "scroll":
                    {
                        float notches;
                        if (!float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out notches))
                        {
                            WarnCommand(location, "scroll needs a number of wheel notches (positive = up)");
                            break;
                        }
                        _input.Pointer.Active = true;
                        _input.Pointer.Scroll = notches;
                        break;
                    }
                    // ---- Holds: stop the script clock until the game catches up ----
                    case "load_level":
                    {
                        // Starts a level load from inside the script. The clock holds until the
                        // scene is in, then until the player has control, so every load lands at
                        // the same tick: the load takes no ticks of its own.
                        if (string.IsNullOrEmpty(argument))
                        {
                            WarnCommand(location, "load_level needs a level id, e.g. once:load_level 1-4");
                            break;
                        }
                        if (!_bridge.LoadLevel(argument))
                        {
                            WarnCommand(location, _bridge.LastError ?? "level load failed");
                            break;
                        }
                        // Hold the script clock until the load lands. Ticking through the load
                        // consumed a different number of ticks every run, because a load takes an
                        // unknown number of rendered frames - that is what shifted script time.
                        // The clock resumes once the scene is in and the settle frames passed, so
                        // the level always begins on the same tick; wait_control still waits for
                        // the player after it.
                        _holdScene = true;
                        _holdSceneArrived = false;
                        _holdSceneBaseHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                        _holdSceneBaseCount = _scenesLoaded;
                        _holdSettle = _plugin.SceneSettleFrames.Value + 2;
                        _resumeAfterFirstTick = true;
                        _plugin.Toast("load_level " + argument + ": script clock held until the level is in.", false);
                        break;
                    }
                    case "wait_scene":
                        // Hold until the active scene changes (a menu button loaded a level).
                        _holdScene = true;
                        _holdSceneArrived = false;
                        _holdSceneBaseHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                        _holdSceneBaseCount = _clickSceneBase >= 0 ? _clickSceneBase : _scenesLoaded;
                        _holdSettle = _plugin.SceneSettleFrames.Value + 2;
                        _resumeAfterFirstTick = true;
                        break;
                    case "wait_control":
                        // Hold until NewMovement.activated (elevator drops, intros).
                        _waitForControl = true;
                        _captureStartAfterWait = false;
                        _sceneSettleFrames = _plugin.SceneSettleFrames.Value;
                        _resumeAfterFirstTick = true;
                        break;
                    case "bxt_timer_start":
                    case "timer_start":
                        _timerElapsed = 0.0;
                        _timerStart = Time.unscaledTime;
                        _timerRunning = true;
                        break;
                    case "bxt_timer_stop":
                    case "timer_stop":
                        if (_timerRunning)
                        {
                            _timerElapsed += Time.unscaledTime - _timerStart;
                            _timerRunning = false;
                        }
                        break;
                    case "bxt_timer_reset":
                    case "timer_reset":
                        ResetTimer();
                        break;
                    case "pause":
                        _time.Paused = true;
                        break;
                    case "unpause":
                        _time.Paused = false;
                        break;
                    case "sv_cheats":
                        _plugin.LogInfo("load_command sv_cheats " + argument + " (handled by the game/server config).");
                        break;
                    default:
                        WarnCommand(location, "unknown command '" + name + "'");
                        break;
                }
            }
        }

        // ------------------------------------------------------------------
        // Pointer helpers
        // ------------------------------------------------------------------

        // Pointer buttons to release at the start of the next tick (the 'click' command).
        int _releaseButtons;
        int _scenesLoaded;
        int _clickSceneBase = -1;
        bool _holdScene;
        int _holdWaited;
        int _holdSceneBaseHandle;
        int _holdSceneBaseCount;
        int _holdSettle;

        /// <summary>Checkpoint chosen by Play to resume from, if any.</summary>
        NorefreshEntry _norefreshHit;
        // Tick whose start is logged for the checkpoint/resume comparison, -1 when idle.
        int _resumeProbeTick = -1;

        /// <summary>Longest real wait a script hold may take before playback pauses instead.</summary>
        const int HoldTimeoutFrames = 1800;

        const int ButtonLeft = 1;
        const int ButtonRight = 2;
        const int ButtonMiddle = 4;

        static bool TryParsePair(string text, out float a, out float b)
        {
            a = b = 0f;
            string[] parts = (text ?? string.Empty).Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 &&
                   float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                   float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        }

        static bool TryParseTriple(string text, out float a, out float b, out float c)
        {
            a = b = c = 0f;
            string[] parts = (text ?? string.Empty).Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 &&
                   float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                   float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) &&
                   float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c);
        }

        void MovePointer(float x, float y)
        {
            TasMouse pointer = _input.Pointer;
            pointer.Active = true;
            pointer.GlideTicksLeft = 0;
            pointer.Position = new Vector2(
                Mathf.Clamp(x, 0f, Mathf.Max(0, Screen.width - 1)),
                Mathf.Clamp(y, 0f, Mathf.Max(0, Screen.height - 1)));
        }

        /// <summary>Press or release a pointer button by name (left by default); returns its bit.</summary>
        int SetPointerButton(string name, bool down)
        {
            string key = (name ?? string.Empty).Trim().ToLowerInvariant();
            TasMouse pointer = _input.Pointer;
            pointer.Active = true;
            int bit;
            switch (key)
            {
                case "right":
                case "r":
                case "rmb":
                    pointer.Right = down;
                    bit = ButtonRight;
                    break;
                case "middle":
                case "m":
                case "mmb":
                    pointer.Middle = down;
                    bit = ButtonMiddle;
                    break;
                default:
                    pointer.Left = down;
                    bit = ButtonLeft;
                    break;
            }
            return bit;
        }

        /// <summary>Release buttons queued by 'click' and drop the one-tick wheel value.</summary>
        void ReleasePending()
        {
            if (_releaseButtons != 0)
            {
                TasMouse pointer = _input.Pointer;
                if ((_releaseButtons & ButtonLeft) != 0) pointer.Left = false;
                if ((_releaseButtons & ButtonRight) != 0) pointer.Right = false;
                if ((_releaseButtons & ButtonMiddle) != 0) pointer.Middle = false;
                _releaseButtons = 0;
            }
            _input.Pointer.Scroll = 0f;
        }

        void WarnCommand(TickLocation location, string message)
        {
            string text = "Script command error at tick " + location.AbsoluteTick + " (line " +
                          (_currentLine != null ? _currentLine.SourceLine : 0) + "): " + message;
            LastError = text;
            _plugin.Toast(text, true);
        }

        public double TotalTime
        {
            get { return _script != null ? _script.TotalTime : 0.0; }
        }

        public int TotalTicks
        {
            get { return _script != null ? _script.TotalTicks : 0; }
        }
    }
}
