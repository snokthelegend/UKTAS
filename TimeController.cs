using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Owns Unity's frame clock (captureDeltaTime and the render rate) while a TAS is
    /// active. It never writes Time.timeScale.
    ///
    /// While the clock runs, every rendered frame is one simulation tick, and the render
    /// rate is capped to PlaybackSpeed / host_frametime (uncapped in fast-forward,
    /// NoRefresh and above 1x), so a 0.1x run renders at 0.1 / host_frametime fps.
    /// While paused or held, frames keep rendering but run no tick: they run on the
    /// frozen player loop (SimulationFreeze), which has no time update, no physics and
    /// no game scripts, so nothing in the game moves. Only while the engine waits on the
    /// game (startup, wait_scene, wait_control) are non-tick frames armed with an
    /// almost-zero captureDeltaTime (FrozenCaptureDelta) instead.
    ///
    /// Playback speed is pacing only. It changes how many real seconds pass between
    /// ticks, never how much virtual time a tick advances and never the physics step.
    /// Time.fixedDeltaTime stays at ULTRAKILL's native 0.008 s, so 0.016 s ticks run
    /// two native steps and 0.25 s ticks run ~31 through Unity's own fixed accumulator.
    ///
    /// Time scale: the tool never writes Time.timeScale; it only reads it. ULTRAKILL
    /// owns the scale (hitstop/parry stops, impact frames, the weapon wheel, cutscene
    /// slow motion, the assist speed slider, its pause menu), so a tick advances
    /// frametime x the game's current scale of game time.
    ///
    /// Because the scale is not available to freeze a frame, the clock is frame-locked
    /// while it runs: every rendered frame is exactly one tick and the render rate is
    /// capped (Application.targetFrameRate) to speed / host_frametime for real-time
    /// pacing. While paused, held or waiting between frame steps, the next frame runs on
    /// the frozen player loop and advances no game time at all (its captureDeltaTime is
    /// armed to FrozenCaptureDelta, which only matters when it cannot be frozen).
    ///
    /// Hitstops and parry stops: the game's own HitStop/TrueStop runs and sets the
    /// scale to 0 itself; TimeControllerHitstopPatch only replaces the realtime wait
    /// with a virtual timer. While one holds, ticks still run (input, rendering, flash,
    /// shake, sound) at the game's scale 0, so no game time passes and no physics step
    /// runs; each such tick takes its frametime off the timer, and the game's own
    /// ContinueTime ends the stop (and restores the scale) on the same tick every run.
    /// </summary>
    public sealed class TimeController
    {
        /// <summary>Used only if the game's fixed step cannot be read (ULTRAKILL is 0.008).</summary>
        public const double NativeFixedStepFallback = 0.008;

        /// <summary>Safety stop for an absurd Field 4 (a 1 s tick is 125 native steps).</summary>
        const int MaxNativeStepsPerTick = 100000;

        static readonly double StampToSeconds = 1.0 / Stopwatch.Frequency;
        const double MaxRealDelta = 0.1;

        /// <summary>
        /// captureDeltaTime armed for a rendered frame that must not be a tick (paused,
        /// held, waiting for a frame step). captureDeltaTime 0 would mean "real time", so
        /// it cannot be used here, and Unity clamps a frame's delta up to its minimum
        /// (~1e-5 s), so such a frame still advances game time a little. It is only what a
        /// frame runs on when SimulationFreeze cannot take it: while the engine waits on
        /// the game (startup, wait_scene, wait_control) or if the frozen player loop could
        /// not be built. A paused or finished script runs on the frozen loop instead,
        /// where no time update, physics step or game script runs at all.
        /// </summary>
        public const float FrozenCaptureDelta = 0.000001f;

        static readonly long RealTimeOrigin = Stopwatch.GetTimestamp();

        /// <summary>
        /// Wall-clock seconds for the tool's own UI timers (toasts, notices, freecam).
        /// Time.unscaledTime follows captureDeltaTime, which barely moves on frozen frames.
        /// </summary>
        public static float RealTime
        {
            get { return (float)((Stopwatch.GetTimestamp() - RealTimeOrigin) * StampToSeconds); }
        }

        long _lastStamp;

        readonly GameBridge _bridge;
        readonly Plugin _plugin;

        bool _saved;
        float _savedCaptureDeltaTime;
        int _savedCaptureFramerate;
        float _savedFixedDeltaTime;
        float _savedMaximumDeltaTime;
        float _savedTimeScale;
        int _savedTargetFrameRate;
        int _savedVSync;

        bool _turboApplied;
        int _appliedRate;
        // Fixed-step phase alignment (see QueueFixedStepAlign).
        bool _alignQueued;
        double _alignDelta;
        bool _physicsApplied;
        float _savedSleepThreshold;
        bool _savedAutoSync;
        float _fpsSmoothed;

        // NoRefresh (GoldSrc r_norefresh 1) state. The cameras are kept alive but their
        // culling masks go to 0, so the game keeps a valid Camera.main while nothing in
        // the scene is submitted for drawing: no geometry, no shadow pass, no lighting.
        readonly List<Camera> _suspendedCameras = new List<Camera>();
        readonly List<int> _suspendedCameraCulling = new List<int>();
        readonly List<CameraClearFlags> _suspendedCameraClear = new List<CameraClearFlags>();
        readonly List<Canvas> _suspendedCanvases = new List<Canvas>();
        Camera[] _cameraBuffer = new Camera[16];
        int _cameraScanCount;
        // Whether the cameras and canvases are currently held dark (NoRefreshActive and the
        // TAS window closed); see RenderSuppressed.
        bool _renderSuppressed;
        // Time.frameCount of the one frame RenderThisFrame let through, -1 for none.
        int _renderFrame = -1;
        long _noRefreshLogStamp;
        long _noRefreshLogTicks;
        long _noRefreshStartStamp;
        double _noRefreshSeconds;
        long _noRefreshTicks;
        int _noRefreshFrames;

        // Real seconds * PlaybackSpeed not yet spent on a tick.
        double _realAccumulator;
        // Whether the next rendered frame is allowed to run one simulation tick.
        bool _advanceScheduled;
        // This frame is a tick frozen by a game hitstop; EndFrame takes its frametime
        // off the hitstop timers.
        bool _hitstopTick;
        double _hitstopTickSeconds;
        // The next rendered frame is armed as a non-tick frame (FrozenCaptureDelta).
        bool _frozenNext;
        // The current rendered frame was armed with the full frametime by the last
        // EndFrame, so it can carry a tick.
        bool _frameArmed;


        // Section 3 of the format spec: the per-framebulk physics accumulator.
        //
        //   accumulator += frameTime;
        //   while (accumulator >= native) { step native; accumulator -= native; }
        //
        // The fractional remainder is carried across both ticks and framebulks, so a
        // 0.001 s coin setup and a 0.25 s wait block can sit next to each other
        // without changing jump heights, gravity or slide momentum.
        double _physicsAccumulator;
        int _lastBulkSteps;
        long _nativeStepsRun;

        public bool Active { get; private set; }

        /// <summary>
        /// Set by the engine every frame: false while it waits on the game (startup, a
        /// scene load, settle frames, player control), which needs the game's own scripts
        /// to keep running between ticks.
        /// </summary>
        public bool FullFreezeAllowed { get; set; }

        /// <summary>
        /// Decided in EndFrame: the next frame is not a tick and may run on the frozen
        /// player loop (SimulationFreeze), with no time update and no game code at all.
        /// </summary>
        public bool WantsFullFreeze { get; private set; }
        public bool Paused { get; set; }
        public bool Hold { get; set; }
        public bool IsRunning { get { return Active && !Paused && !Hold; } }
        public int PendingSteps { get; private set; }
        public bool LastFrameAdvanced { get; private set; }
        /// <summary>
        /// True on a rendered frame that is frozen only because the real-time
        /// accumulator has not reached the next tick yet. Input must be left alone
        /// on these frames: injecting neutral between ticks would release held
        /// buttons and cancel charge/coin setups at slow motion.
        /// </summary>
        public bool PacingSkip { get; private set; }
        public bool GamePaused { get; private set; }

        /// <summary>
        /// The current rendered frame was armed with a full frametime: it advanced game time
        /// (a tick, or a free-flowing frame such as the wait-for-control elevator drop).
        /// </summary>
        public bool FrameArmed { get { return _frameArmed; } }
        public double HostFrametime = TasProperties.DefaultHostFrametime;
        public bool Turbo { get; set; }
        public bool AdvanceScheduled { get { return _advanceScheduled; } }

        /// <summary>
        /// The current frame is a hitstop tick whose frametime has not yet been taken off
        /// the hitstop timers (that happens in EndFrame). Part of a savestate, so a state
        /// taken mid-frame finishes the frame the same way after a load.
        /// </summary>
        public bool HitstopTickPending { get { return _hitstopTick; } }
        public double HitstopTickSeconds { get { return _hitstopTickSeconds; } }

        /// <summary>Time.timeScale when the clock was first taken (read only, for diagnostics).</summary>
        public float SavedTimeScale { get { return _savedTimeScale; } }

        public void RestoreHitstopTick(bool pending, double seconds)
        {
            _hitstopTick = pending && !double.IsNaN(seconds) && seconds > 0.0;
            _hitstopTickSeconds = _hitstopTick ? seconds : 0.0;
        }

        /// <summary>
        /// Let the game run on its own clock without simulating script ticks. Used
        /// while waiting for player control (elevator drops, intros): the world must
        /// keep moving for NewMovement.activated to ever become true. Previously this
        /// relied on a queued step staying pending, which pausing discarded, so a
        /// pause during the intro froze the game permanently.
        /// </summary>
        public bool FreeFlow { get; set; }
        /// <summary>Skip real-time pacing entirely (fast-forward): a tick on every rendered frame.</summary>
        public bool FreeRun { get; set; }
        /// <summary>
        /// Frame-locked playback at a speed of 1.0 or less: the frame rate is capped to
        /// speed / host_frametime, so each rendered frame lasts host_frametime / speed of
        /// real time and game time runs at exactly speed x real time. 0 = no cap.
        /// </summary>
        public double FrameCapFps { get; set; }

        /// <summary>
        /// True while the screen is not being refreshed: every enabled camera renders an empty
        /// culling mask, the frame rate is uncapped, and exactly one simulation tick still runs
        /// per rendered frame (see <see cref="EnterNoRefresh"/>).
        /// </summary>
        public bool NoRefreshActive { get; private set; }

        /// <summary>
        /// The single gate for every FFF rendering suppression (user NoRefresh, ff lines and the
        /// optimizer's headless seek all run through NoRefresh). The TAS window is exempt: while it
        /// is open the screen is refreshed and the window draws normally, and the fast-forward
        /// itself (one tick per frame, uncapped) keeps running underneath. Closing the window
        /// suppresses rendering again if NoRefresh is still active.
        /// </summary>
        public bool RenderSuppressed
        {
            get
            {
                if (!NoRefreshActive)
                    return false;
                // One frame lifted on request (RenderThisFrame); the next frame is dark again.
                if (_renderFrame == Time.frameCount)
                    return false;
                if (ReferenceEquals(_plugin, null))
                    return true;
                GuiManager gui = _plugin.Gui;
                return !(gui != null && gui.Visible);
            }
        }

        /// <summary>Ticks simulated since NoRefresh was entered.</summary>
        public long NoRefreshTicks { get { return _noRefreshTicks; } }

        /// <summary>Rendered frames NoRefresh has run for.</summary>
        public int NoRefreshFrames { get { return _noRefreshFrames; } }

        /// <summary>Cameras whose culling mask is currently 0.</summary>
        public int SuspendedCameraCount { get { return _suspendedCameras.Count; } }

        /// <summary>Wall-clock seconds spent in NoRefresh (live while it is active).</summary>
        public double NoRefreshSeconds
        {
            get
            {
                if (NoRefreshActive && _noRefreshStartStamp != 0L)
                    return _noRefreshSeconds +
                           (Stopwatch.GetTimestamp() - _noRefreshStartStamp) * StampToSeconds;
                return _noRefreshSeconds;
            }
        }

        /// <summary>Ticks per wall-clock second of the current (or last) NoRefresh run.</summary>
        public double NoRefreshTicksPerSecond
        {
            get
            {
                double seconds = NoRefreshSeconds;
                return seconds > 0.0001 ? _noRefreshTicks / seconds : 0.0;
            }
        }

        /// <summary>
        /// Called when playback resumes from a pause: credit one full tick so the very
        /// next frame boundary schedules a tick instead of waiting a tick's worth of
        /// real time (0.1x would otherwise look frozen for 160 ms after Play).
        /// </summary>
        public void KickTick()
        {
            _realAccumulator = HostFrametime;
        }
        public float MeasuredFps { get { return _fpsSmoothed; } }

        // ------------------------------------------------------------------
        // NoRefresh: headless fast-forward (GoldSrc r_norefresh)
        // ------------------------------------------------------------------

        /// <summary>
        /// Enter NoRefresh. This is GoldSrc's r_norefresh 1, not a savestate: the game keeps
        /// simulating completely normally, but the 3D screen is never refreshed again.
        ///
        /// Every enabled camera (Camera.main, the weapon/viewmodel camera and any other) has
        /// its culling mask set to 0, so Unity submits no geometry, runs no shadow pass and
        /// lights nothing. The cameras stay enabled on purpose: the game's own camera logic and
        /// every Camera.main lookup keep working, so gameplay code behaves exactly as it does at
        /// 1x. Combined with <see cref="EndFrame"/> running the next tick on every rendered
        /// frame with no wall-clock pacing, the run advances at whatever rate the CPU can reach
        /// instead of the monitor's refresh rate. Nothing in the simulation is skipped: the same
        /// input injection, the same native physics steps, the same triggers, enemy logic and
        /// per-tick commands run as during normal playback.
        /// </summary>
        public void EnterNoRefresh()
        {
            if (NoRefreshActive)
                return;
            NoRefreshActive = true;
            _noRefreshTicks = 0;
            _noRefreshFrames = 0;
            _noRefreshSeconds = 0.0;
            _noRefreshStartStamp = Stopwatch.GetTimestamp();
            _noRefreshLogStamp = 0L;
            _noRefreshLogTicks = 0L;
            _cameraScanCount = 0;
            ApplyRenderSuppression();
            if (!ReferenceEquals(_plugin, null))
                _plugin.LogInfo("NoRefresh entered: 3D rendering off (" + _suspendedCameras.Count +
                                " camera(s) with an empty culling mask); one simulation tick per frame, no pacing.");
        }

        /// <summary>Leave NoRefresh and put every camera's culling mask back.</summary>
        public void ExitNoRefresh()
        {
            if (!NoRefreshActive)
            {
                // A clock that was released or destroyed while the screen was dark: restore.
                if (_suspendedCameras.Count > 0)
                    RestoreCameras();
                return;
            }

            NoRefreshActive = false;
            if (_noRefreshStartStamp != 0L)
            {
                _noRefreshSeconds += (Stopwatch.GetTimestamp() - _noRefreshStartStamp) * StampToSeconds;
                _noRefreshStartStamp = 0L;
            }
            ApplyRenderSuppression();
            if (!ReferenceEquals(_plugin, null))
                _plugin.LogInfo("NoRefresh left after " + _noRefreshTicks + " tick(s) in " +
                                _noRefreshSeconds.ToString("0.000") + " s (" +
                                NoRefreshTicksPerSecond.ToString("0") + " ticks/s); rendering restored.  " +
                                "vsync " + QualitySettings.vSyncCount + ", target " + Application.targetFrameRate +
                                ", refresh " + RefreshHz() + "Hz, masked cameras " +
                                MaskedCameraCount() + "/" + _suspendedCameras.Count);
        }

        /// <summary>
        /// Let the current frame render while NoRefresh is active. Call it from the frame's LateUpdate
        /// (before the cameras cull): the cameras and canvases come back for this frame only, and the
        /// next frame's EndFrame darkens them again. The headless optimizer uses it to show the last
        /// frame of each attempt. Does nothing when NoRefresh is off.
        /// </summary>
        public void RenderThisFrame()
        {
            if (!NoRefreshActive)
                return;
            _renderFrame = Time.frameCount;
            ApplyRenderSuppression();
        }

        /// <summary>Per-camera hook: nothing is submitted for any camera while NoRefresh runs.</summary>
        void OnNoRefreshPreCull(Camera camera)
        {
            if (!_renderSuppressed || camera == null)
                return;
            camera.cullingMask = 0;
        }

        /// <summary>
        /// Bring the cameras and overlay canvases in line with <see cref="RenderSuppressed"/>:
        /// dark while NoRefresh runs with the TAS window closed, restored while the window is open
        /// or NoRefresh is off. Safe to call every frame; it only acts when the state changes.
        /// </summary>
        void ApplyRenderSuppression()
        {
            bool suppress = RenderSuppressed;
            if (suppress == _renderSuppressed)
                return;
            _renderSuppressed = suppress;
            if (suppress)
            {
                _cameraScanCount = 0;
                SuspendCameras();
                SuspendOverlayCanvases();
                // Last word on rendering: Unity calls this for every camera immediately before
                // culling, after every game script has had its say. A camera whose mask is reset
                // in LateUpdate still submits nothing.
                Camera.onPreCull += OnNoRefreshPreCull;
            }
            else
            {
                Camera.onPreCull -= OnNoRefreshPreCull;
                RestoreOverlayCanvases();
                RestoreCameras();
            }
        }

        /// <summary>The display's vertical refresh, used only to label the diagnostic line.</summary>
        static string RefreshHz()
        {
            Resolution resolution = Screen.currentResolution;
            return resolution.refreshRateRatio.value.ToString("0");
        }

        /// <summary>Cameras still holding an empty mask (a camera reset shows up here).</summary>
        int MaskedCameraCount()
        {
            int masked = 0;
            for (int i = 0; i < _suspendedCameras.Count; i++)
            {
                Camera camera = _suspendedCameras[i];
                if (camera != null && camera.cullingMask == 0)
                    masked++;
            }
            return masked;
        }

        /// <summary>
        /// Once a second during NoRefresh: the measured tick rate and every limiter that could
        /// explain it. A hidden frame cap, a monitor refresh lock and a camera mask the game put
        /// back all look identical from outside; this line tells them apart.
        /// </summary>
        void LogNoRefreshRate()
        {
            long now = Stopwatch.GetTimestamp();
            if (_noRefreshLogStamp == 0L)
            {
                _noRefreshLogStamp = now;
                _noRefreshLogTicks = _noRefreshTicks;
                return;
            }
            double seconds = (now - _noRefreshLogStamp) * StampToSeconds;
            if (seconds < 1.0)
                return;
            long ticks = _noRefreshTicks - _noRefreshLogTicks;
            _noRefreshLogStamp = now;
            _noRefreshLogTicks = _noRefreshTicks;
            if (ReferenceEquals(_plugin, null))
                return;
            _plugin.LogInfo("NoRefresh: " + (ticks / seconds).ToString("0") + " ticks/s  (" +
                            (seconds * 1000.0 / Math.Max(1L, ticks)).ToString("0.0") + " ms/frame)" +
                            "  vsync " + QualitySettings.vSyncCount +
                            "  targetFrameRate " + Application.targetFrameRate +
                            "  refresh " + RefreshHz() + "Hz" +
                            "  cameras " + _suspendedCameras.Count + " masked " + MaskedCameraCount() +
                            "  canvases off " + _suspendedCanvases.Count +
                            "  gui " + _plugin.LastGuiMilliseconds.ToString("0.00") + " ms");
        }

        /// <summary>Set the culling mask of every enabled camera to 0.</summary>
        void SuspendCameras()
        {
            int count = Camera.allCamerasCount;
            if (count <= 0)
                return;
            if (_cameraBuffer.Length < count)
                _cameraBuffer = new Camera[Mathf.NextPowerOfTwo(count)];
            Camera.GetAllCameras(_cameraBuffer);
            for (int i = 0; i < count; i++)
                SuspendCamera(_cameraBuffer[i]);
        }

        void SuspendCamera(Camera camera)
        {
            if (camera == null)
                return;
            for (int i = 0; i < _suspendedCameras.Count; i++)
            {
                if (ReferenceEquals(_suspendedCameras[i], camera))
                    return;
            }
            _suspendedCameras.Add(camera);
            _suspendedCameraCulling.Add(camera.cullingMask);
            _suspendedCameraClear.Add(camera.clearFlags);
            // Nothing is visible to this camera any more: no renderers are submitted, so
            // there is no geometry, no shadow casters and nothing to light. The camera stays
            // enabled and its target, aspect and pixel rect are never touched: the game's own
            // Camera.main lookups, aim rays and screen-space maths must stay bit-identical to
            // 1x playback, or the run would desync. The culling mask is render-only.
            camera.cullingMask = 0;
            // Screen cameras also skip their fullscreen clear. A camera rendering into a
            // texture keeps its clear so nothing reading that texture sees stale garbage.
            if (camera.targetTexture == null)
                camera.clearFlags = CameraClearFlags.Nothing;
        }

        /// <summary>
        /// Screen Space - Overlay canvases are drawn without a camera, so clearing culling
        /// masks alone still pays for the game's HUD, style meter and health bars every frame.
        /// Canvas.enabled is render-only: no gameplay code reads it.
        /// </summary>
        void SuspendOverlayCanvases()
        {
            Canvas[] canvases;
            try
            {
                canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            }
            catch
            {
                return;
            }
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !canvas.enabled || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                bool known = false;
                for (int j = 0; j < _suspendedCanvases.Count; j++)
                {
                    if (ReferenceEquals(_suspendedCanvases[j], canvas))
                    {
                        known = true;
                        break;
                    }
                }
                if (known)
                    continue;
                _suspendedCanvases.Add(canvas);
                canvas.enabled = false;
            }
        }

        void RestoreOverlayCanvases()
        {
            for (int i = 0; i < _suspendedCanvases.Count; i++)
            {
                if (_suspendedCanvases[i] != null)
                    _suspendedCanvases[i].enabled = true;
            }
            _suspendedCanvases.Clear();
        }

        /// <summary>Put every camera's culling mask back exactly as it was.</summary>
        void RestoreCameras()
        {
            for (int i = 0; i < _suspendedCameras.Count; i++)
            {
                Camera camera = _suspendedCameras[i];
                if (camera == null)
                    continue;
                camera.cullingMask = _suspendedCameraCulling[i];
                camera.clearFlags = _suspendedCameraClear[i];
            }
            _suspendedCameras.Clear();
            _suspendedCameraCulling.Clear();
            _suspendedCameraClear.Clear();
        }

        /// <summary>Wall-clock pacing multiplier. 1.0 = normal speed, 0.1 = one tenth.</summary>
        public double PlaybackSpeed = 1.0;

        public int LastTickSteps { get; private set; }

        /// <summary>Field 4 of the framebulk currently being simulated.</summary>
        public double FramebulkFrametime = TasProperties.DefaultHostFrametime;
        /// <summary>Seconds of frametime received but not yet spent on a native step.</summary>
        public double PhysicsAccumulatorSeconds { get { return _physicsAccumulator; } }
        /// <summary>Native 0.008 s physics steps the last framebulk advanced.</summary>
        public int StepsLastFramebulk { get { return _lastBulkSteps; } }
        /// <summary>Native steps that have run since the clock was activated.</summary>
        public long NativeStepsRun { get { return _nativeStepsRun; } }

        /// <summary>Compat alias: the accumulator replaced the old reporting carry.</summary>
        public double PhysicsCarrySeconds { get { return _physicsAccumulator; } }
        public double PendingRealSeconds { get { return _realAccumulator; } }

        /// <summary>Clock values the game has had to be talked out of during this run.</summary>
        readonly HashSet<string> _clockChanged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Deprecated: kept so old configs still load. The native step is never
        /// scaled by this value any more.
        /// </summary>
        public int PhysicsSubsteps = 1;

        public TimeController(Plugin plugin, GameBridge bridge)
        {
            _plugin = plugin;
            _bridge = bridge;
            Bake = new BakeManager(plugin, bridge);
        }

        /// <summary>
        /// Optional TAS baking (bake_start / bake_end blocks). It samples and applies the world state
        /// at the end of every frame that ran a tick; a script without bake blocks returns from both
        /// hooks on its first check.
        /// </summary>
        public BakeManager Bake { get; private set; }

        /// <summary>ULTRAKILL's own physics step, captured before we touched the clock.</summary>
        public double NativeFixedStep
        {
            get
            {
                if (_saved && _savedFixedDeltaTime > 0f)
                    return _savedFixedDeltaTime;
                return Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : NativeFixedStepFallback;
            }
        }

        /// <summary>Kept for the GUI: the physics step is always the native one now.</summary>
        public double FixedStep { get { return NativeFixedStep; } }

        /// <summary>How many native physics steps one TAS tick covers (1.0 at 0.008 s).</summary>
        public double StepsPerTick
        {
            get
            {
                double native = NativeFixedStep;
                return native > 0.0 ? HostFrametime / native : 1.0;
            }
        }

        /// <summary>Snapshot the player's original time settings before we touch them.</summary>
        public void SaveOriginals()
        {
            if (_saved)
                return;
            _saved = true;
            _savedCaptureDeltaTime = Time.captureDeltaTime;
            _savedCaptureFramerate = Time.captureFramerate;
            _savedFixedDeltaTime = Time.fixedDeltaTime;
            _savedMaximumDeltaTime = Time.maximumDeltaTime;
            _savedTimeScale = Time.timeScale;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
        }

        /// <summary>Take ownership of the clock. Safe to call repeatedly.</summary>
        public void Activate(double hostFrametime)
        {
            SaveOriginals();
            HostFrametime = TasScript.ClampFrametime(hostFrametime);
            PlaybackSpeed = 1.0;
            Active = true;
            Paused = false;
            Hold = false;
            FreeFlow = false;
            PendingSteps = 0;
            LastFrameAdvanced = false;
            _realAccumulator = 0.0;
            _advanceScheduled = false;
            // The frame Activate runs in was sampled before the clock was taken.
            _frameArmed = false;
            _physicsAccumulator = 0.0;
            _lastBulkSteps = 0;
            _nativeStepsRun = 0;
            FramebulkFrametime = HostFrametime;
            LastTickSteps = 0;
            _clockChanged.Clear();

            // A long tick (0.25 s) must run all of its native steps; Unity's default
            // 0.333 s catch-up budget is not enough headroom once hitstop is involved.
            if (Time.maximumDeltaTime < 1.0f)
                Time.maximumDeltaTime = 1.0f;

            ApplyDeterministicPhysics();

            // Put the game's own step back if anything (including an older build)
            // changed it. From here on this value is read-only for the tool.
            if (_savedFixedDeltaTime > 0f && Math.Abs(Time.fixedDeltaTime - _savedFixedDeltaTime) > 0.0000001f)
                Time.fixedDeltaTime = _savedFixedDeltaTime;

            WantsFullFreeze = false;
            SimulationFreeze.Attach(_plugin, this);

            ApplyImmediately();
        }

        /// <summary>Release the clock and restore every value we changed.</summary>
        public void Shutdown()
        {
            // Releasing the clock must never leave the screen dark.
            ExitNoRefresh();
            if (!Active)
                return;
            Active = false;
            Paused = false;
            Hold = false;
            FreeFlow = false;
            PendingSteps = 0;
            Turbo = false;
            _realAccumulator = 0.0;
            _advanceScheduled = false;
            _frozenNext = false;
            _frameArmed = false;
            // The original player loop comes back at the end of this frame.
            WantsFullFreeze = false;
            SimulationFreeze.Release();
            RestoreTurbo();
            RestorePhysics();
            _alignQueued = false;
            if (_saved)
            {
                Time.captureDeltaTime = _savedCaptureDeltaTime;
                Time.fixedDeltaTime = _savedFixedDeltaTime;
                Time.maximumDeltaTime = _savedMaximumDeltaTime;
                // Time.timeScale is the game's; the tool never wrote it, so nothing to restore.
                Application.targetFrameRate = _savedTargetFrameRate;
                QualitySettings.vSyncCount = _savedVSync;
            }
        }

        /// <summary>
        /// Choose how much virtual time a tick advances. Only captureDeltaTime
        /// changes: a tick of 0.016 runs two native 0.008 physics steps, a tick of
        /// 0.004 runs one every other tick, and 0.008 is exactly 1:1.
        /// </summary>
        public void SetHostFrametime(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                return;
            HostFrametime = TasScript.ClampFrametime(value);
            if (Active)
            {
                ApplyCapture();
                if (Time.maximumDeltaTime < 1.0f)
                    Time.maximumDeltaTime = 1.0f;
            }
        }

        /// <summary>
        /// GoldSrc host_framerate: every rendered frame advances game time by exactly
        /// host_frametime, whatever the real frame time was. Unity's equivalent is
        /// Time.captureDeltaTime. Time.captureFramerate is only a property over the
        /// same field (assigning 0 resets captureDeltaTime to 0), so it must never be
        /// written after this: doing so silently disabled the lock and left Unity on
        /// real time, which made host_frametime unable to change the game's speed.
        /// </summary>
        void ApplyCapture()
        {
            Time.captureDeltaTime = CaptureTarget;
        }

        /// <summary>The captureDeltaTime the next frame is armed with: a tick's frametime, or the frozen delta.</summary>
        float CaptureTarget { get { return _frozenNext ? FrozenCaptureDelta : (float)HostFrametime; } }

        /// <summary>
        /// Called by the script runner once per tick with Field 4 of the framebulk
        /// that owns the tick. A '-' in Field 4 arrives here already resolved to
        /// host_frametime. This value is what the accumulator consumes: it is the
        /// only thing that may differ between a 1000 fps setup and a 4 fps wait.
        /// </summary>
        public void BeginFramebulk(double frametime)
        {
            if (double.IsNaN(frametime) || double.IsInfinity(frametime) || frametime <= 0.0)
                return;
            FramebulkFrametime = TasScript.ClampFrametime(frametime);
            SetHostFrametime(FramebulkFrametime);
        }

        /// <summary>
        /// A savestate load discards the timeline that owned any hitstop. Time.timeScale
        /// is the game's and is not touched here (TimeControllerHitstopPatch.Restore ends
        /// a discarded stop through the game's own ContinueTime); only the physics carry
        /// is resynced for the restored world.
        /// </summary>
        public void ResetHitstop()
        {
            ResyncPhysicsCarry();
        }

        /// <summary>
        /// After a savestate load the carry from the old timeline no longer applies.
        /// Unity's fixed-step clock is not part of a savestate, so the leftover that
        /// decides the next physics step is Unity's live remainder
        /// (Time.time - Time.fixedTime). Mirror it so the accumulator model and the
        /// HUD's step counts match what Unity runs next, and drop the step counts.
        /// </summary>
        public void ResyncPhysicsCarry()
        {
            double native = NativeFixedStep;
            double remainder = Time.timeAsDouble - Time.fixedTimeAsDouble;
            if (double.IsNaN(remainder) || remainder < 0.0 || native <= 0.0)
                remainder = 0.0;
            else if (remainder >= native)
                remainder %= native;
            _physicsAccumulator = remainder;
            _lastBulkSteps = 0;
            LastTickSteps = 0;
        }

        /// <summary>
        /// Put the sub-step phase captured in a savestate back: the fractional frametime
        /// that had not yet been spent on a native 0.008 s step when the state was taken.
        /// Restoring it keeps a 0.001 s coin setup or a 0.004 s framebulk landing its
        /// physics steps on the same ticks after an undo or a branch switch.
        /// </summary>
        public void RestorePhysicsAccumulator(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                seconds = 0.0;
            double native = NativeFixedStep;
            if (native > 0.0 && seconds >= native)
                seconds %= native;
            _physicsAccumulator = seconds;
            _lastBulkSteps = 0;
            LastTickSteps = 0;
        }

        /// <summary>Discard queued advances and restart the pacing accumulator.</summary>
        public void ClearPendingSteps()
        {
            PendingSteps = 0;
            _realAccumulator = 0.0;
            _advanceScheduled = false;
            _physicsAccumulator = 0.0;
            _lastBulkSteps = 0;
            LastTickSteps = 0;
        }

        /// <summary>Queue frame advances while paused. One step = one tick = one rendered frame.</summary>
        public void RequestStep(int frames)
        {
            if (frames < 1)
                frames = 1;
            if (PendingSteps > 100000)
                PendingSteps = 100000;
            PendingSteps += frames;
            // A manual step should not be followed by a scheduled tick from surplus
            // real time; the clock resumes pacing from zero afterwards.
            _realAccumulator = 0.0;
            _advanceScheduled = false;
        }

        /// <summary>True when this frame runs a simulation tick (and consumes a queued step).</summary>
        public bool BeginFrame()
        {
            if (!Active)
            {
                LastFrameAdvanced = false;
                return false;
            }

            // Baking: remember the tick before the engine may run one this frame.
            Bake.OnBeginFrame();

            GamePaused = _bridge != null && _bridge.IsGamePaused();

            // A frame sampled at scale 0 because a game hitstop holds: it is a real tick,
            // it just advances no game time (and so steps no physics). A fully frozen frame
            // never is: its Time.deltaTime is the last frame's, and no game script runs in
            // it, so a step requested during it waits for the next (unfrozen) frame.
            bool hitstopFrame = !SimulationFreeze.Frozen && Time.deltaTime <= 0f && TimeControllerHitstopPatch.Holding;

            bool run;
            // A step is requested from Update (F6), after Unity already sampled this
            // frame's delta, which is FrozenCaptureDelta while paused. Consuming it now
            // would count the tick but simulate nothing. Leave it queued: EndFrame arms
            // the full frametime, and the next frame runs it for real. The game's own
            // scale is read (never written): at 0 outside a hitstop (pause menu, a
            // cutscene freeze) the step also stays queued. A hitstop tick is the
            // exception: the game's scale 0 is what that tick is meant to simulate.
            if (PendingSteps > 0 && ((_frameArmed && Time.timeScale > 0.0001f) || hitstopFrame))
            {
                PendingSteps--;
                run = true;
            }
            else
            {
                run = !Paused && !Hold && _advanceScheduled && _frameArmed;
            }

            // NoRefresh: a tick on every rendered frame, whatever the wall clock says. The
            // frame itself costs almost nothing because no camera draws, which is what turns
            // the uncapped frame rate into simulation speed.
            if (NoRefreshActive && !Paused && !Hold && !GamePaused && _frameArmed)
                run = true;

            LastFrameAdvanced = run;
            PacingSkip = !run && Active && !Paused && !Hold && PendingSteps == 0;
            if (run && !GamePaused)
            {
                if (NoRefreshActive)
                {
                    _noRefreshTicks++;
                    _noRefreshFrames++;
                }
                // Unity runs no fixed step on a zero-delta frame, so the physics carry
                // must not grow either.
                if (!hitstopFrame)
                    AccumulatePhysics(HostFrametime * SampledGameScale());
            }

            // The tick's frametime comes off the hitstop timers in EndFrame, after the
            // frame's game code ran. HostFrametime is read now: EndFrame arms the next
            // tick's frametime first.
            _hitstopTick = hitstopFrame && !GamePaused && (run || FreeFlow);
            _hitstopTickSeconds = _hitstopTick ? HostFrametime : 0.0;
            return run;
        }

        /// <summary>
        /// The game's Time.timeScale as Unity sampled it for this frame (read only):
        /// deltaTime / captureDeltaTime. 1 unless the game slowed time, so the physics
        /// model matches the scaled delta Unity's fixed-step loop actually got.
        /// </summary>
        static double SampledGameScale()
        {
            float capture = Time.captureDeltaTime;
            if (capture <= 0f)
                return 1.0;
            double scale = Time.deltaTime / (double)capture;
            if (double.IsNaN(scale) || scale < 0.0)
                return 1.0;
            return Math.Abs(scale - 1.0) < 0.0001 ? 1.0 : scale;
        }

        /// <summary>
        /// Pacing decision for the next rendered frame. Runs once per rendered frame;
        /// it never waits, sleeps or spins, so rendering stays at full refresh rate.
        /// </summary>
        public void EndFrame()
        {
            // Baking: the frame's game code has run, so this is the end-of-tick sampling point
            // (record, enforced apply, drift check, skip-to-end). Before the Active check so the
            // tick that finishes a script is still seen.
            Bake.OnEndFrame();

            if (!Active)
            {
                // The clock was released without a Stop (a scene replaced the run); the
                // screen must not stay dark.
                ExitNoRefresh();
                // Hitstop timers left from the run finish on the wall clock, as the
                // game's own realtime wait would.
                TimeControllerHitstopPatch.AdvanceRealtime(Time.unscaledDeltaTime);
                _hitstopTick = false;
                WantsFullFreeze = false;
                return;
            }

            GamePaused = _bridge != null && _bridge.IsGamePaused();

            double speed = PlaybackSpeed;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
                speed = 1.0;

            // Wall-clock delta from a monotonic timer. Time.unscaledDeltaTime is not
            // trustworthy here: captureDeltaTime overrides it, and on frozen frames it
            // can read 0, which meant a paused replay could never accumulate enough
            // time to schedule its next tick. Clamped so a stall (alt-tab, scene load)
            // cannot burst ticks afterwards.
            long stamp = Stopwatch.GetTimestamp();
            double realDelta = _lastStamp == 0L ? 0.0 : (stamp - _lastStamp) * StampToSeconds;
            _lastStamp = stamp;
            if (realDelta < 0.0)
                realDelta = 0.0;
            else if (realDelta > MaxRealDelta)
                realDelta = MaxRealDelta;

            bool running = !Paused && !Hold;
            // A scene load or the level's start (the wait-for-control elevator drop, where
            // the clock is paused but frames free-flow) can bring a fresh camera or canvas
            // in, so the masks are re-checked periodically whenever NoRefresh is on.
            // The TAS window opening or closing flips RenderSuppressed; follow it here.
            ApplyRenderSuppression();
            if (_renderSuppressed && ++_cameraScanCount >= 10)
            {
                _cameraScanCount = 0;
                SuspendCameras();
                SuspendOverlayCanvases();
            }

            if (running && NoRefreshActive)
            {
                // No pacing at all: one tick per rendered frame.
                _advanceScheduled = true;
                _realAccumulator = 0.0;
                // Once a second, write what the mode actually achieves: this is the only
                // way to tell a real ceiling (simulation cost) from a hidden limiter.
                LogNoRefreshRate();
            }
            else if (running)
            {
                // Frame-locked: one tick per rendered frame. The tool never writes
                // Time.timeScale, so a rendered frame between ticks cannot be frozen and
                // the old real-time accumulator (render at refresh rate, tick when due)
                // is gone. Real-time pacing is the render-rate cap applied below.
                _advanceScheduled = true;
                _realAccumulator = 0.0;
            }
            else
            {
                _advanceScheduled = false;
                _realAccumulator = 0.0;
            }

            // Game hitstops (HitStop/TrueStop) run on tick time: a frozen tick takes its
            // frametime off the stop's timer, and the game's ContinueTime ends the stop
            // here, before the next scale is chosen. Every other frame only ages stops
            // started during it, so a stop always lasts the same ticks after its start.
            TimeControllerHitstopPatch.EndFrame(_hitstopTick, _hitstopTickSeconds);
            _hitstopTick = false;
            _hitstopTickSeconds = 0.0;
            bool hitstopHolding = TimeControllerHitstopPatch.Holding;

            // The game owns the scale (read only here), so a tick advances frametime x
            // the game's scale. Hitstops are handled above; any other stop or slow-down
            // the game makes now applies to ticks too. Log the first one so it is visible.
            if (LastFrameAdvanced && !GamePaused && !hitstopHolding && Time.timeScale < 0.999f &&
                _clockChanged.Add("gameTimeScale"))
            {
                _plugin.LogInfo("The game stopped or slowed time (impact frame, slow motion or cutscene); " +
                                "the TAS clock follows the game's Time.timeScale (" +
                                Time.timeScale.ToString("0.###") + "), so this tick advanced " +
                                (HostFrametime * Time.timeScale).ToString("0.######") + " s of game time.");
            }

            // While a hitstop holds, the next tick still runs (input, rendering, flash,
            // shake, sound) at the game's own scale 0: no game time and no physics step.
            // A frame that is not a tick is armed with FrozenCaptureDelta instead; when it
            // runs fully frozen (below) Unity does not read that value at all.
            bool nextRuns = PendingSteps > 0 || _advanceScheduled || FreeFlow;
            _frozenNext = !nextRuns;
            _frameArmed = nextRuns;
            ApplyCapture();
            AssertClock();

            // An alignment frame needs the game at scale 1; it waits until the hitstop is over.
            bool aligning = false;
            if (_alignQueued && !hitstopHolding)
            {
                // One real frame that advances Unity's clock by the alignment delta (a physics
                // step lands in it at most once), then the clock resumes next frame. The
                // alignment assumes the game's own scale is 1 (it is at a level start).
                _alignQueued = false;
                aligning = true;
                Time.captureDeltaTime = (float)_alignDelta;
            }

            // A non-tick frame runs on the frozen player loop (SimulationFreeze): no time
            // update, no physics, no game script, so Time.time, the fixed-step remainder and
            // the world stay exactly where the last tick left them. Not while the engine
            // waits on the game, and not in the game's own pause menu, whose UI needs its
            // scripts (the game stops its own clock there with Time.timeScale 0).
            WantsFullFreeze = _frozenNext && !aligning && FullFreezeAllowed && !GamePaused;

            // NoRefresh is always uncapped: vsync off, no targetFrameRate, no frame cap. A
            // monitor refresh limit (73 fps on a 75 Hz panel) would otherwise cap the tick
            // rate, which is exactly what this mode exists to escape.
            if (NoRefreshActive || Turbo)
                ApplyRate(-1);
            else if (FrameCapFps > 0.0)
                ApplyRate(CapFpsToTarget(FrameCapFps));
            else if (running)
                // Frame-locked pacing (every frame is a tick): cap the render rate to
                // speed / host_frametime so game time runs at speed x real time.
                ApplyRate(CapFpsToTarget(speed / HostFrametime));
            else
                RestoreTurbo();

            if (realDelta > 0.00001)
            {
                float instant = (float)(1.0 / realDelta);
                _fpsSmoothed = _fpsSmoothed <= 0f ? instant : Mathf.Lerp(_fpsSmoothed, instant, 0.05f);
            }
        }

        /// <summary>
        /// The dynamic per-framebulk clock runner (section 3 of the format spec).
        ///
        /// Every tick of a framebulk adds that framebulk's Field 4 value to the
        /// accumulator and spends it in whole native 0.008 s steps. A value below
        /// the native step (0.001 for a 1000 fps coin setup) advances the camera and
        /// the input every tick while physics steps only when the accumulator has
        /// covered 0.008 s; a large value (0.25 for a 4 fps wait) runs ~31 steps in
        /// order. The remainder always carries, so neither case changes an arc.
        /// </summary>
        void AccumulatePhysics(double tickTime)
        {
            double native = NativeFixedStep;
            if (native <= 0.0 || tickTime <= 0.0)
            {
                LastTickSteps = 0;
                return;
            }

            _physicsAccumulator += tickTime;

            int steps = 0;
            while (_physicsAccumulator + 1e-9 >= native)
            {
                StepNativePhysics(native);
                _physicsAccumulator -= native;
                steps++;
                if (steps >= MaxNativeStepsPerTick)
                    break;
            }

            LastTickSteps = steps;
            _lastBulkSteps = steps;
        }

        /// <summary>
        /// One native physics step of the loop above.
        ///
        /// The step itself is executed by Unity, not by us: with Time.captureDeltaTime
        /// set to the framebulk's frametime, Unity's own fixed-step accumulator runs
        /// this exact loop and calls physics + FixedUpdate once per 0.008 s, in the
        /// order ULTRAKILL expects. Calling Physics.Simulate here as well would
        /// advance the world twice per step, because NewMovement and every projectile
        /// already move in FixedUpdate. This method therefore records the step; the
        /// arithmetic above is the authoritative model of what Unity just ran.
        /// </summary>
        void StepNativePhysics(double step)
        {
            _nativeStepsRun++;
        }

        /// <summary>
        /// Loading a scene is the one time the game rewrites the clock for itself: it resets the
        /// render rate and some loading paths retune the fixed step. Those writes used to stick,
        /// so the run's speed and physics depended on whether a load had happened. Every value
        /// the tool owns is compared and taken back here, once per frame, with one log line per
        /// value so the change is visible instead of silent.
        /// </summary>
        void AssertClock()
        {
            if (!Active)
                return;
            float capture = CaptureTarget;
            if (Math.Abs(Time.captureDeltaTime - capture) > 1e-9f)
            {
                Time.captureDeltaTime = capture;
                if (_clockChanged.Add("captureDeltaTime"))
                    _plugin.LogWarning("The game changed Time.captureDeltaTime (a scene load resets it); the TAS clock took it back to " +
                                       capture.ToString("0.######") + " s.");
            }
            if (_saved && _savedFixedDeltaTime > 0f &&
                Math.Abs(Time.fixedDeltaTime - _savedFixedDeltaTime) > 1e-6f)
            {
                Time.fixedDeltaTime = _savedFixedDeltaTime;
                if (_clockChanged.Add("fixedDeltaTime"))
                    _plugin.LogWarning("The game changed Time.fixedDeltaTime (a scene load can retune it); restored to " +
                                       _savedFixedDeltaTime.ToString("0.######") + " s so every physics step keeps its size.");
            }
            if (Time.maximumDeltaTime < 1.0f)
                Time.maximumDeltaTime = 1.0f;
            if (_turboApplied)
            {
                if (Application.targetFrameRate != _appliedRate)
                {
                    Application.targetFrameRate = _appliedRate;
                    if (_clockChanged.Add("targetFrameRate"))
                        _plugin.LogWarning("The game changed the render rate (a scene load resets it); the TAS clock took it back to " +
                                           _appliedRate + " fps.");
                }
                if (QualitySettings.vSyncCount != 0)
                {
                    QualitySettings.vSyncCount = 0;
                    if (_clockChanged.Add("vSyncCount"))
                        _plugin.LogWarning("The game re-enabled vsync (a scene load resets it); the TAS clock turned it off again.");
                }
            }
        }

        /// <summary>
        /// A scene load spent an unknown number of rendered frames while the clock was ours.
        /// Forget the wall time that piled up during it so the next tick is scheduled from now
        /// instead of from before the load. The physics carry is deliberately left alone: the
        /// step phase belongs to the run, and a load must not shift it.
        /// </summary>
        public void ResetPacingAfterLoad()
        {
            _realAccumulator = 0.0;
            _advanceScheduled = false;
            PendingSteps = 0;
            // The next EndFrame measures from here, so the load frame counts as no time at all.
            _lastStamp = 0L;
        }

        /// <summary>Force the clock settings without waiting for the next frame (used at activation).</summary>
        public void ApplyImmediately()
        {
            if (!Active)
                return;
            // The game keeps its own Time.timeScale (pause menu, hitstop); only the next
            // frame's captureDeltaTime is armed: frozen while paused or held, a full tick
            // otherwise.
            _frozenNext = Paused || Hold;
            ApplyCapture();
            AssertClock();
        }

        /// <summary>Remainder Unity's fixed-step clock is aligned to: just past a step boundary.</summary>
        public const double PhaseTarget = 0.0001;

        /// <summary>
        /// Unity carries the time left over after its last physics step into the next run,
        /// and nothing resets it. With 0.001 s ticks that leftover decides which tick a
        /// physics step lands on, and it shifts by one tick on every run. This measures it
        /// (Time.time - Time.fixedTime) and queues one frame that advances the clock to
        /// PhaseTarget past a step boundary, so every run starts at the same phase. The
        /// frame can run one physics step, so call it only before the scene is replaced.
        /// Returns false when the clock is already aligned.
        /// </summary>
        public bool QueueFixedStepAlign()
        {
            double fixedStep = Time.fixedDeltaTime;
            if (fixedStep <= 0.0)
                return false;

            double remainder = Time.timeAsDouble - Time.fixedTimeAsDouble;
            if (remainder < 0.0)
                remainder = 0.0;
            if (remainder >= fixedStep)
                remainder %= fixedStep;
            if (Math.Abs(remainder - PhaseTarget) < 0.000001)
                return false;

            double advance = remainder <= PhaseTarget
                ? PhaseTarget - remainder
                : fixedStep - remainder + PhaseTarget;
            _alignDelta = advance;
            _alignQueued = true;
            _plugin.LogInfo("Aligning the physics step phase: remainder " + remainder.ToString("0.000000") +
                            " s, advancing " + advance.ToString("0.000000") + " s.");
            return true;
        }

        /// <summary>
        /// Remove physics state that differs between runs of the same script. A body that
        /// fell asleep at a different moment during loading wakes differently when hit, and
        /// a transform change that reaches the physics scene one step late moves a contact.
        /// </summary>
        void ApplyDeterministicPhysics()
        {
            if (_plugin == null || _plugin.DeterministicPhysics == null || !_plugin.DeterministicPhysics.Value)
            {
                RestorePhysics();
                return;
            }
            if (!_physicsApplied)
            {
                _physicsApplied = true;
                _savedSleepThreshold = Physics.sleepThreshold;
                _savedAutoSync = Physics.autoSyncTransforms;
            }
            Physics.sleepThreshold = 0f;
            Physics.autoSyncTransforms = true;
        }

        void RestorePhysics()
        {
            if (!_physicsApplied)
                return;
            _physicsApplied = false;
            Physics.sleepThreshold = _savedSleepThreshold;
            Physics.autoSyncTransforms = _savedAutoSync;
        }

        /// <summary>Rounds the cap to a whole rate Unity accepts (1 to 10000 fps).</summary>
        static int CapFpsToTarget(double fps)
        {
            return (int)Math.Max(1.0, Math.Min(10000.0, Math.Round(fps)));
        }

        /// <summary>
        /// Overrides the render rate: -1 is uncapped (turbo), a positive value is a
        /// frame cap. Vsync is off so the cap is the limit, not the monitor.
        /// </summary>
        void ApplyRate(int target)
        {
            if (_turboApplied && _appliedRate == target)
                return;
            _turboApplied = true;
            _appliedRate = target;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = target;
        }

        void RestoreTurbo()
        {
            if (!_turboApplied)
                return;
            _turboApplied = false;
            if (_saved)
            {
                QualitySettings.vSyncCount = _savedVSync;
                Application.targetFrameRate = _savedTargetFrameRate;
            }
            else
            {
                QualitySettings.vSyncCount = 1;
                Application.targetFrameRate = -1;
            }
        }

        /// <summary>Human readable summary used by the GUI and logs.</summary>
        public string StatusLine()
        {
            if (!Active)
                return "clock: released  native=" + NativeFixedStep.ToString("0.######") + "s";

            double speed = PlaybackSpeed;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0.0)
                speed = 1.0;
            double waitMs = Math.Max(0.0, (HostFrametime - _realAccumulator) / speed * 1000.0);

            return "clock: " + (IsRunning ? "running" : "paused") +
                   "  ft=" + HostFrametime.ToString("0.######") +
                   "  native=" + NativeFixedStep.ToString("0.######") +
                   "  steps/tick=" + StepsPerTick.ToString("0.###") +
                   "  steps now=" + LastTickSteps +
                   "  accumulator=" + (_physicsAccumulator * 1000.0).ToString("0.00") + "ms" +
                   "  speed=" + speed.ToString("0.##") + "x" +
                   "  tick in=" + waitMs.ToString("0.0") + "ms" +
                   "  scale=" + Time.timeScale.ToString("0.###") +
                   "  capture=" + Time.captureDeltaTime.ToString("0.######") +
                   (FrameCapFps > 0.0 ? "  cap=" + FrameCapFps.ToString("0.#") + "fps" : string.Empty) +
                   "  queued=" + PendingSteps +
                   (Turbo ? "  turbo" : string.Empty) +
                   (GamePaused ? "  game-paused" : string.Empty) +
                   (NoRefreshActive
                       ? "  NOREFRESH ticks=" + _noRefreshTicks + " cameras-off=" + _suspendedCameras.Count
                       : string.Empty);
        }
    }
}
