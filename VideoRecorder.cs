using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Offline recorder with motion blur from interpolated sub-frames, in the BXT-RS style.
    ///
    /// Playback is frame-locked (one rendered frame per tick), and the recorder never adds
    /// game frames: extra frames run Update and frame-based coroutines, which changes the
    /// simulation. Instead, at the end of each tick frame, after the game has drawn its real
    /// frame:
    ///   - the real frame is read from the screen (sub-frame at the current tick, alpha 1);
    ///   - the gameplay camera is moved to poses interpolated between the previous and the
    ///     current tick (alpha 1/N .. (N-1)/N), HUD canvases are refreshed so they stay
    ///     attached to the view, and the game's own camera stack is rendered again into an
    ///     offscreen texture: texture cameras of the player's rig (world, viewmodel) into
    ///     their own textures, then the screen cameras into the recorder's texture;
    ///   - the camera and canvases are put back before the next frame.
    /// The sub-frames that fall inside each output frame's shutter window are averaged and
    /// piped to FFmpeg. blend_samples = sub-frames per tick (8 = 1000 fps virtual).
    ///
    /// Only the camera pose is interpolated; enemies and projectiles keep their tick pose.
    /// No interpolation in freecam or third person. TAS overlays are screen-only, so Clean
    /// render hides them and the TAS window. Audio is not recorded.
    /// </summary>
    public sealed class VideoRecorder
    {
        public static readonly int[] FpsChoices = { 30, 60, 120 };
        public static readonly int[] SampleChoices = { 1, 4, 8, 16, 32 };

        const int TailLines = 24;
        const int FfmpegExitTimeoutMs = 120000;

        readonly Plugin _plugin;
        readonly List<string> _ffmpegTail = new List<string>(TailLines);
        Process _process;
        Stream _stdin;
        Texture2D _shot;
        RenderTexture _target;
        GameObject _hookObject;
        int _width;
        int _height;
        int _pixels;
        bool _sawActive;
        bool _headerConsumed;
        TasScript _headerScript;

        // Output frame accumulation.
        double _startTime;
        long _outIndex;
        int[] _sumR;
        int[] _sumG;
        int[] _sumB;
        int _count;
        byte[] _current;
        byte[] _previous;
        byte[] _out;

        // Tick poses.
        readonly List<HudCanvas> _hudCanvases = new List<HudCanvas>();

        // Audio: Unity's offline mixer (AudioRenderer) pulled once per frame and written to a
        // WAV next to the video, then muxed into the final MP4.
        string _ffmpegExe;
        string _finalPath;
        string _videoPath;
        string _audioPath;
        WavWriter _wav;
        int _audioChannels;
        bool _audioRunning;
        string _audioNote = string.Empty;
        int _audioPulls;
        int _audioEmpty;
        int _audioRejected;
        long _audioFramesKept;
        string _volume = "1.0";

        /// <summary>One-shot volume for the next Start (script header record_volume).</summary>
        string _pendingVolume;
        int _lastTick = -1;
        bool _havePose;
        Vector3 _prevPos;
        Vector3 _curPos;
        Quaternion _prevRot;
        Quaternion _curRot;
        double _prevTime;
        double _curTime;

        // TAS visual settings saved while a clean render is in progress.
        bool _visualsSaved;
        bool _savedShowHud;
        bool _savedHideViewmodels;
        bool _savedShowHitboxes;
        bool _savedShowPlayerHitbox;
        bool _savedShowTriggers;
        int _savedTrajectoryVisibility;

        public VideoRecorder(Plugin plugin)
        {
            _plugin = plugin;
        }

        public bool IsRecording { get; private set; }

        /// <summary>
        /// True while the recorder re-renders the camera stack. Camera overrides and the
        /// trajectory overlay check this and stay out of the sub-frames.
        /// </summary>
        public bool Rendering { get; private set; }

        public string OutputPath { get; private set; }
        public int Fps { get; private set; }
        public int Samples { get; private set; }
        public float Shutter { get; private set; }
        public int FramesEncoded { get; private set; }
        public string LastError { get; private set; }
        public string Status { get; private set; }

        /// <summary>
        /// Searches for ffmpeg in order: the configured path, the game folder, then every
        /// PATH entry. Returns null when none exists.
        /// </summary>
        public static string LocateFfmpeg(string configured, string gameRoot)
        {
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;

            string[] names = { "ffmpeg.exe", "ffmpeg" };
            if (!string.IsNullOrEmpty(gameRoot))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    string candidate = Path.Combine(gameRoot, names[i]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] directories = path.Split(Path.PathSeparator);
            for (int d = 0; d < directories.Length; d++)
            {
                string directory = directories[d].Trim().Trim('"');
                if (directory.Length == 0)
                    continue;
                try
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        string candidate = Path.Combine(directory, names[i]);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                catch (Exception)
                {
                    // A malformed PATH entry is skipped, not fatal.
                }
            }
            return null;
        }

        public static int SnapToChoice(int value, int[] choices)
        {
            int best = choices[0];
            for (int i = 1; i < choices.Length; i++)
            {
                if (Math.Abs(choices[i] - value) < Math.Abs(best - value))
                    best = choices[i];
            }
            return best;
        }

        /// <summary>
        /// Starts an encode. samples = sub-frames per simulation tick, shutter = fraction of
        /// an output frame that is exposed (0.5 = 180 degrees, max 1.0).
        /// Returns false and sets LastError on failure.
        /// </summary>
        public bool Start(string outputPath, int fps, int samples, float shutter)
        {
            if (IsRecording)
            {
                LastError = "A recording is already running.";
                return false;
            }
            TasEngine engine = _plugin.Engine;
            if (engine == null || engine.Script == null)
            {
                LastError = "Load a script before recording.";
                return false;
            }

            string ffmpeg = LocateFfmpeg(_plugin.FfmpegPath.Value, GameRoot());
            if (ffmpeg == null)
            {
                LastError = "ffmpeg not found. Set its path in the Recording tab.";
                return false;
            }

            fps = SnapToChoice(fps, FpsChoices);
            samples = Mathf.Clamp(samples, 1, 64);
            shutter = Mathf.Clamp(shutter, 0.05f, 1f);

            try
            {
                string fullPath = Path.GetFullPath(outputPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                _width = Screen.width & ~1;
                _height = Screen.height & ~1;
                if (_width < 16 || _height < 16)
                {
                    LastError = "The game window is too small to record.";
                    return false;
                }

                // With audio the video goes to a temporary file and is muxed with the WAV at the end.
                bool audio = _plugin.RecordAudio.Value;
                _ffmpegExe = ffmpeg;
                _finalPath = fullPath;
                _videoPath = audio ? Path.ChangeExtension(fullPath, ".video.mp4") : null;
                _audioPath = audio ? Path.ChangeExtension(fullPath, ".audio.wav") : null;
                _audioNote = string.Empty;
                _volume = _pendingVolume ?? "1.0";
                _pendingVolume = null;

                string arguments = string.Format(CultureInfo.InvariantCulture,
                    "-y -f rawvideo -pix_fmt rgba -s {0}x{1} -r {2} -i - -vf vflip " +
                    "-c:v libx264 -preset medium -crf 16 -pix_fmt yuv420p \"{3}\"",
                    _width, _height, fps, audio ? _videoPath : fullPath);

                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                };

                _ffmpegTail.Clear();
                _process = Process.Start(info);
                _process.ErrorDataReceived += OnFfmpegOutput;
                _process.BeginErrorReadLine();
                _stdin = _process.StandardInput.BaseStream;

                // Same size as the screen, so screen cameras render at their normal size.
                _target = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
                _target.Create();
                _shot = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                _pixels = _width * _height;
                _sumR = new int[_pixels];
                _sumG = new int[_pixels];
                _sumB = new int[_pixels];
                _current = new byte[_pixels * 4];
                _previous = new byte[_pixels * 4];
                _out = new byte[_pixels * 4];
                CreateHook();
                if (audio)
                    StartAudio();
            }
            catch (Exception ex)
            {
                LastError = "Could not start ffmpeg: " + ex.Message;
                Teardown();
                return false;
            }

            OutputPath = outputPath;
            Fps = fps;
            Samples = samples;
            Shutter = shutter;
            FramesEncoded = 0;
            LastError = string.Empty;
            _sawActive = false;
            _startTime = engine.VideoTime;
            _outIndex = 0;
            _count = 0;
            _lastTick = engine.VideoFrameIndex;
            _havePose = false;

            IsRecording = true;
            // Fast-forward (F7, ff lines, FFF NoRefresh) is off for the whole recording.
            engine.CancelFastForwardForRecording();
            SuppressTasVisuals();
            Status = "Recording " + Path.GetFileName(outputPath) + ": " + fps + " fps, " + samples +
                     " sub-frames per tick (" + Mathf.RoundToInt((float)(samples / engine.Script.Properties.HostFrametime)) +
                     " fps virtual), shutter " + Mathf.RoundToInt(shutter * 360f) + " deg.";
            _plugin.Toast("Recording started: " + Path.GetFileName(outputPath) + ".", false);
            // Submission guard: the capture still runs, but it never shows a valid run while baking is in use.
            if (!BakeManager.RunIsSubmittable)
            {
                string warning = "Video of a NOT SUBMITTABLE run (" + BakeManager.NotSubmittableReason + "). " +
                                 BakeManager.NotLegitWarning;
                _plugin.LogWarning(warning);
                _plugin.Toast(warning, true);
            }
            return true;
        }

        /// <summary>Closes the pipe and waits for ffmpeg to finish writing the MP4.</summary>
        public bool Stop(string reason)
        {
            if (!IsRecording)
                return false;
            IsRecording = false;

            bool ok = true;
            try
            {
                if (_stdin != null)
                {
                    _stdin.Flush();
                    _stdin.Close();
                }
                if (_process != null && !_process.WaitForExit(FfmpegExitTimeoutMs))
                {
                    LastError = "ffmpeg did not finish within 2 minutes.";
                    ok = false;
                    try { _process.Kill(); } catch (Exception) { }
                }
                else if (_process != null && _process.ExitCode != 0)
                {
                    LastError = "ffmpeg exited with code " + _process.ExitCode + ": " + LastFfmpegLine();
                    ok = false;
                }
            }
            catch (Exception ex)
            {
                LastError = "Finishing the encode failed: " + ex.Message;
                ok = false;
            }
            finally
            {
                Teardown();
            }

            if (ok && _videoPath != null)
                MuxAudio();

            if (ok)
            {
                Status = "Saved " + FramesEncoded + " frames to " + OutputPath + "." + _audioNote;
                _plugin.Toast("Recording saved (" + FramesEncoded + " frames" + (reason != null ? ", " + reason : string.Empty) + ").", false);
                // Submission guard: a video of a run that used baked state must not be submitted as a TAS run.
                if (!BakeManager.RunIsSubmittable)
                {
                    Status += " NOT SUBMITTABLE: " + BakeManager.NotSubmittableReason + ".";
                    string warning = "Saved video shows a NOT SUBMITTABLE run (" + BakeManager.NotSubmittableReason +
                                     "). Do not submit it as a TAS run. " + BakeManager.NotLegitWarning;
                    _plugin.LogWarning(warning);
                    _plugin.Toast(warning, true);
                }
            }
            else
            {
                Status = "Recording failed: " + LastError;
                _plugin.Toast(Status, true);
            }
            _plugin.LogInfo(Status);
            return ok;
        }

        /// <summary>
        /// Called once per game frame from Plugin. Starts header-triggered recordings and
        /// stops any recording when the TAS that was playing stops or finishes.
        /// </summary>
        public void OnFrameEnd()
        {
            TasEngine engine = _plugin.Engine;
            if (engine == null)
                return;

            if (!IsRecording)
            {
                TryStartFromHeader(engine);
                return;
            }

            if (engine.IsActive)
                _sawActive = true;
            if (_sawActive && (engine.Mode == EngineMode.Idle || engine.Finished))
                Stop("TAS stopped");
        }

        void TryStartFromHeader(TasEngine engine)
        {
            TasScript script = engine.Script;
            if (script == null || engine.Mode != EngineMode.Playing)
                return;
            if (!ReferenceEquals(script, _headerScript))
            {
                _headerScript = script;
                _headerConsumed = false;
            }
            if (_headerConsumed)
                return;
            _headerConsumed = true;

            string file = HeaderValue(script, "record_video");
            if (file == null)
                return;
            int fps = ParseInt(HeaderValue(script, "record_fps"), 60);
            int samples = ParseInt(HeaderValue(script, "blend_samples"), 1);
            float shutter = ParseFloat(HeaderValue(script, "shutter_angle"), 0.5f);
            string volume = HeaderValue(script, "record_volume");
            if (volume != null)
            {
                string parsed = ParseVolume(volume);
                if (parsed == null)
                    _plugin.Toast("record_volume must be a number (1.0 = unchanged) or decibels such as -3dB; using 1.0.", true);
                _pendingVolume = parsed;
            }
            if (!Start(ResolveOutput(file), fps, samples, shutter) && LastError.Length > 0)
                _plugin.Toast(LastError, true);
        }

        /// <summary>
        /// Script commands: "bxt_rec start [filename]", "bxt_rec stop", and the longer
        /// form "start_recording [filename] [fps] [samples] [shutter]". Returns false
        /// with LastError set when the arguments are invalid.
        /// </summary>
        public bool HandleCommand(string command, string argument)
        {
            string[] parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (command == "bxt_rec")
            {
                if (parts.Length > 0 && parts[0] == "stop")
                {
                    Stop("bxt_rec stop");
                    return true;
                }
                if (parts.Length > 0 && parts[0] == "start")
                {
                    string file = parts.Length > 1 ? parts[1] : "recording.mp4";
                    HeaderDefaults(out int hFps, out int hSamples, out float hShutter);
                    return Start(ResolveOutput(file), hFps, hSamples, hShutter);
                }
                LastError = "bxt_rec takes start [filename] or stop.";
                return false;
            }

            // start_recording [filename] [fps] [samples] [shutter]
            if (parts.Length == 0)
            {
                LastError = "start_recording needs a filename.";
                return false;
            }
            HeaderDefaults(out int defFps, out int defSamples, out float defShutter);
            int fpsArg = parts.Length > 1 ? ParseInt(parts[1], defFps) : defFps;
            int samplesArg = parts.Length > 2 ? ParseInt(parts[2], defSamples) : defSamples;
            float shutterArg = parts.Length > 3 ? ParseFloat(parts[3], defShutter) : defShutter;
            return Start(ResolveOutput(parts[0]), fpsArg, samplesArg, shutterArg);
        }

        /// <summary>
        /// Settings for a recording started from the script body: the header's record_fps,
        /// blend_samples and shutter_angle when present, otherwise 60, 1 and 0.5. Also queues
        /// the header's record_volume for the next Start.
        /// </summary>
        void HeaderDefaults(out int fps, out int samples, out float shutter)
        {
            TasScript script = _plugin.Engine != null ? _plugin.Engine.Script : null;
            fps = 60;
            samples = 1;
            shutter = 0.5f;
            if (script == null)
                return;
            fps = ParseInt(HeaderValue(script, "record_fps"), 60);
            samples = ParseInt(HeaderValue(script, "blend_samples"), 1);
            shutter = ParseFloat(HeaderValue(script, "shutter_angle"), 0.5f);
            string volume = HeaderValue(script, "record_volume");
            if (volume != null)
                _pendingVolume = ParseVolume(volume);
        }

        void CreateHook()
        {
            _hookObject = new GameObject("TAS video capture");
            UnityEngine.Object.DontDestroyOnLoad(_hookObject);
            _hookObject.hideFlags = HideFlags.HideAndDontSave;
            VideoCaptureHook hook = _hookObject.AddComponent<VideoCaptureHook>();
            hook.Callback = () =>
            {
                try
                {
                    OnEndOfFrameCapture();
                }
                catch (Exception ex)
                {
                    Rendering = false;
                    LastError = "Capture failed: " + ex.Message;
                    if (IsRecording)
                        Stop("capture error");
                }
            };
        }

        /// <summary>
        /// Runs after the game has drawn its frame. On a frame where a tick ran, renders the
        /// interpolated sub-frames between the previous tick and this one, then adds the real
        /// frame, all in time order. Frames without a tick (pause, load) add nothing.
        /// </summary>
        void OnEndOfFrameCapture()
        {
            if (!IsRecording)
                return;
            TasEngine engine = _plugin.Engine;
            if (engine == null)
                return;

            int tick = engine.VideoFrameIndex;
            // Audio is pulled every frame so the offline mixer never backs up; only frames that
            // ran a tick keep their samples, so the WAV runs on simulation time like the video.
            PullAudio(tick != _lastTick);
            if (tick == _lastTick)
                return;

            Transform camera = null;
            bool haveCamera = _plugin.Bridge != null && _plugin.Bridge.TryGetCameraTransform(out camera) && camera != null;

            // A restart or rewind must not interpolate from the old position.
            if (tick < _lastTick)
                _havePose = false;
            _lastTick = tick;

            double time = engine.VideoTime;
            Vector3 pos = haveCamera ? camera.position : Vector3.zero;
            Quaternion rot = haveCamera ? camera.rotation : Quaternion.identity;
            _prevPos = _havePose ? _curPos : pos;
            _prevRot = _havePose ? _curRot : rot;
            _prevTime = _havePose ? _curTime : time;
            _curPos = pos;
            _curRot = rot;
            _curTime = time;
            bool hadPose = _havePose;
            _havePose = haveCamera;

            // The real frame is captured first: the sub-frame renders below draw into the
            // recorder's texture, not the screen, so the screen still holds the game's frame.
            byte[] real = _current;
            CaptureInto(real, null);

            bool interpolate = haveCamera && hadPose && Samples > 1 &&
                               !_plugin.FreecamActive &&
                               (_plugin.Cameras == null || _plugin.Cameras.Mode == TasCameraMode.FirstPerson);
            if (interpolate)
            {
                CollectHudCanvases(camera);
                BeginOverlayCapture(camera);
                try
                {
                for (int s = 1; s < Samples && IsRecording; s++)
                {
                    float alpha = s / (float)Samples;
                    if (!RenderStackAt(camera,
                            Vector3.Lerp(_prevPos, _curPos, alpha),
                            Quaternion.Slerp(_prevRot, _curRot, alpha)))
                    {
                        // Pipeline cameras missing: fall back to the real frame for this sample.
                        AddSubframe(_prevTime + alpha * (_curTime - _prevTime), real);
                        continue;
                    }
                    byte[] sub = _previous;
                    CaptureInto(sub, _target);
                    AddSubframe(_prevTime + alpha * (_curTime - _prevTime), sub);
                }
                }
                finally
                {
                    EndOverlayCapture();
                }
                _hudCanvases.Clear();
            }
            else if (Samples > 1)
            {
                // No interpolation (first tick, freecam, third person): weight the real frame
                // like a full set of samples so the blend stays balanced.
                for (int s = 1; s < Samples && IsRecording; s++)
                    AddSubframe(_prevTime + s / (double)Samples * (_curTime - _prevTime), real);
            }

            if (IsRecording)
                AddSubframe(_curTime, real);
        }

        /// <summary>
        /// Renders one sub-frame through ULTRAKILL's own pipeline (PostProcessV2_Handler),
        /// with the gameplay camera at (position, rotation), into the recorder's texture:
        ///   1. gameplay camera  - draws the world into the game's buffers (mainTex, depth,
        ///                         normals) via SetTargetBuffers; its targets are not touched;
        ///   2. "HUD Camera"     - draws the HUD and viewmodel into mainTex;
        ///   3. "Virtual Camera" - puts mainTex on screen with the post-process shader; this
        ///                         is the only camera retargeted, to the recorder's texture.
        /// The HUD and Virtual cameras are children of the gameplay camera, so they move
        /// with it; HUD canvases are moved too. Everything is put back afterwards.
        /// Returns false when the pipeline cameras are not found.
        /// </summary>
        bool RenderStackAt(Transform camera, Vector3 position, Quaternion rotation)
        {
            Camera main = camera.GetComponent<Camera>();
            Camera hud = FindChildCamera(camera, "HUD Camera");
            Camera screen = FindChildCamera(camera, "Virtual Camera");
            if (main == null || screen == null || !main.enabled)
                return false;

            Vector3 savedPos = camera.position;
            Quaternion savedRot = camera.rotation;
            RenderTexture screenTarget = screen.targetTexture;

            Rendering = true;
            try
            {
                camera.SetPositionAndRotation(position, rotation);
                MoveHudCanvases(camera);
                Canvas.ForceUpdateCanvases();

                main.Render();
                if (hud != null && hud.enabled && hud.gameObject.activeInHierarchy)
                    hud.Render();

                screen.targetTexture = _target;
                try
                {
                    screen.Render();
                    RenderOverlays();
                }
                finally
                {
                    screen.targetTexture = screenTarget;
                }
            }
            finally
            {
                camera.SetPositionAndRotation(savedPos, savedRot);
                RestoreHudCanvases();
                Canvas.ForceUpdateCanvases();
                Rendering = false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Screen overlays in sub-frames
        // ------------------------------------------------------------------
        //
        // The real frame is a screen grab, so it holds everything Unity drew: the camera
        // stack plus every "Screen Space - Overlay" canvas (the elevator's level map, menus,
        // fades) and any camera drawing on top of the final image. The re-rendered sub-frames
        // only ran the gameplay stack, so those layers showed in one sample of N and came out
        // faint, with their animation blended away. While sub-frames render, overlay canvases
        // are drawn by a private camera into the recorder's texture instead, and on-top screen
        // cameras are rendered into it too, so every sample carries them.

        struct OverlayCanvas
        {
            public Canvas Canvas;
            public Camera WorldCamera;
            public float PlaneDistance;
        }

        readonly List<OverlayCanvas> _overlayCanvases = new List<OverlayCanvas>();
        readonly List<Camera> _overlayCameras = new List<Camera>();
        Camera _overlayCaptureCamera;

        void BeginOverlayCapture(Transform gameplayCamera)
        {
            _overlayCanvases.Clear();
            _overlayCameras.Clear();

            Camera screen = FindChildCamera(gameplayCamera, "Virtual Camera");
            float screenDepth = screen != null ? screen.depth : float.MinValue;
            Camera main = gameplayCamera.GetComponent<Camera>();
            Camera hud = FindChildCamera(gameplayCamera, "HUD Camera");
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera c = cameras[i];
                if (c == null || c == main || c == hud || c == screen || c == _overlayCaptureCamera)
                    continue;
                // Only cameras that draw onto the screen after the game's final image.
                if (c.targetTexture != null || c.depth <= screenDepth)
                    continue;
                _overlayCameras.Add(c);
            }
            _overlayCameras.Sort((a, b) => a.depth.CompareTo(b.depth));

            int mask = 0;
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !canvas.isRootCanvas || !canvas.enabled ||
                    canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                mask |= 1 << canvas.gameObject.layer;
                _overlayCanvases.Add(new OverlayCanvas
                {
                    Canvas = canvas,
                    WorldCamera = canvas.worldCamera,
                    PlaneDistance = canvas.planeDistance
                });
            }
            if (_overlayCanvases.Count == 0)
                return;

            Camera capture = EnsureOverlayCaptureCamera();
            capture.cullingMask = mask;
            for (int i = 0; i < _overlayCanvases.Count; i++)
            {
                Canvas canvas = _overlayCanvases[i].Canvas;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = capture;
                canvas.planeDistance = 1f;
            }
            Canvas.ForceUpdateCanvases();
        }

        void EndOverlayCapture()
        {
            for (int i = 0; i < _overlayCanvases.Count; i++)
            {
                OverlayCanvas saved = _overlayCanvases[i];
                if (saved.Canvas == null)
                    continue;
                saved.Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                saved.Canvas.worldCamera = saved.WorldCamera;
                saved.Canvas.planeDistance = saved.PlaneDistance;
            }
            if (_overlayCanvases.Count > 0)
                Canvas.ForceUpdateCanvases();
            _overlayCanvases.Clear();
            _overlayCameras.Clear();
        }

        /// <summary>Draws the on-top screen cameras, then the overlay canvases, into _target.</summary>
        void RenderOverlays()
        {
            for (int i = 0; i < _overlayCameras.Count; i++)
            {
                Camera c = _overlayCameras[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
                    continue;
                RenderTexture saved = c.targetTexture;
                c.targetTexture = _target;
                try { c.Render(); }
                finally { c.targetTexture = saved; }
            }
            if (_overlayCanvases.Count > 0 && _overlayCaptureCamera != null)
            {
                _overlayCaptureCamera.targetTexture = _target;
                _overlayCaptureCamera.Render();
                _overlayCaptureCamera.targetTexture = null;
            }
        }

        Camera EnsureOverlayCaptureCamera()
        {
            if (_overlayCaptureCamera != null)
                return _overlayCaptureCamera;
            GameObject host = new GameObject("TAS overlay capture camera");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            // Far from the level, so world objects on a UI layer never fall in its view.
            host.transform.position = new Vector3(0f, -100000f, 0f);
            Camera c = host.AddComponent<Camera>();
            c.enabled = false; // rendered by hand only
            c.clearFlags = CameraClearFlags.Nothing;
            c.orthographic = true;
            c.nearClipPlane = 0.01f;
            c.farClipPlane = 10f;
            c.depth = 100f;
            c.useOcclusionCulling = false;
            c.allowHDR = false;
            c.allowMSAA = false;
            _overlayCaptureCamera = c;
            return c;
        }

        static Camera FindChildCamera(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<Camera>() : null;
        }

        /// <summary>
        /// Root canvases drawn in "Screen Space - Camera" by the gameplay camera or a camera
        /// under it (ULTRAKILL's HUD). Their transforms are set once per frame, before
        /// rendering, so moving the camera alone would leave the HUD behind and it would
        /// smear. Each is stored with its offset from the gameplay camera.
        /// </summary>
        void CollectHudCanvases(Transform camera)
        {
            _hudCanvases.Clear();
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            Matrix4x4 toCamera = camera.worldToLocalMatrix;
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera ||
                    canvas.worldCamera == null)
                    continue;
                Transform owner = canvas.worldCamera.transform;
                if (owner != camera && !owner.IsChildOf(camera))
                    continue;
                Transform t = canvas.transform;
                _hudCanvases.Add(new HudCanvas
                {
                    Transform = t,
                    Relative = toCamera * t.localToWorldMatrix,
                    Position = t.position,
                    Rotation = t.rotation
                });
            }
        }

        void MoveHudCanvases(Transform camera)
        {
            Matrix4x4 fromCamera = camera.localToWorldMatrix;
            for (int i = 0; i < _hudCanvases.Count; i++)
            {
                HudCanvas hud = _hudCanvases[i];
                if (hud.Transform == null)
                    continue;
                Matrix4x4 m = fromCamera * hud.Relative;
                Vector3 p = m.GetColumn(3);
                hud.Transform.SetPositionAndRotation(p, Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1)));
            }
        }

        void RestoreHudCanvases()
        {
            for (int i = 0; i < _hudCanvases.Count; i++)
            {
                HudCanvas hud = _hudCanvases[i];
                if (hud.Transform != null)
                    hud.Transform.SetPositionAndRotation(hud.Position, hud.Rotation);
            }
        }

        struct HudCanvas
        {
            public Transform Transform;
            public Matrix4x4 Relative;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        void CaptureInto(byte[] destination, RenderTexture source)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = source;
            _shot.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
            RenderTexture.active = active;
            NativeArray<byte> raw = _shot.GetRawTextureData<byte>();
            NativeArray<byte>.Copy(raw, destination, destination.Length);
        }

        /// <summary>
        /// Emits every output frame whose time has passed, then adds the sub-frame to the
        /// current output frame when it falls inside its shutter window.
        /// </summary>
        void AddSubframe(double time, byte[] frame)
        {
            double frameDuration = 1.0 / Fps;
            int guard = 0;
            while (IsRecording && guard++ < 64)
            {
                double frameTime = _startTime + _outIndex * frameDuration;
                if (time <= frameTime)
                    break;
                if (!EmitFrame(frame))
                    return;
                _outIndex++;
            }

            double windowEnd = _startTime + _outIndex * frameDuration;
            if (time >= windowEnd - Shutter * frameDuration)
            {
                for (int i = 0, p = 0; i < _pixels; i++, p += 4)
                {
                    _sumR[i] += frame[p];
                    _sumG[i] += frame[p + 1];
                    _sumB[i] += frame[p + 2];
                }
                _count++;
            }
        }

        /// <summary>Writes the averaged output frame, or the latest sub-frame if none landed in the window.</summary>
        bool EmitFrame(byte[] latest)
        {
            byte[] data;
            if (_count > 0)
            {
                int n = _count;
                for (int i = 0, p = 0; i < _pixels; i++, p += 4)
                {
                    _out[p] = (byte)(_sumR[i] / n);
                    _out[p + 1] = (byte)(_sumG[i] / n);
                    _out[p + 2] = (byte)(_sumB[i] / n);
                    _out[p + 3] = 255;
                }
                Array.Clear(_sumR, 0, _pixels);
                Array.Clear(_sumG, 0, _pixels);
                Array.Clear(_sumB, 0, _pixels);
                _count = 0;
                data = _out;
            }
            else
            {
                data = latest;
            }

            try
            {
                _stdin.Write(data, 0, data.Length);
                FramesEncoded++;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "Writing to ffmpeg failed: " + ex.Message + " " + LastFfmpegLine();
                Stop("encode error");
                return false;
            }
        }

        /// <summary>
        /// Clean render: hides the TAS HUD, hitboxes, triggers and trajectory path, and
        /// shows the viewmodels, for the length of the recording. ULTRAKILL's own HUD is
        /// not touched. Settings are restored by RestoreTasVisuals when the recording ends.
        /// </summary>
        void SuppressTasVisuals()
        {
            if (!_plugin.RecordCleanRender.Value || _visualsSaved)
                return;
            _visualsSaved = true;
            _savedShowHud = _plugin.ShowHud.Value;
            _savedHideViewmodels = _plugin.HideViewmodels.Value;
            _savedShowHitboxes = _plugin.ShowHitboxes.Value;
            _savedShowPlayerHitbox = _plugin.ShowPlayerHitbox.Value;
            _savedShowTriggers = _plugin.ShowTriggers.Value;
            _savedTrajectoryVisibility = _plugin.TrajectoryVisibilityMode.Value;

            _plugin.ShowHud.Value = false;
            _plugin.HideViewmodels.Value = false;
            _plugin.ShowHitboxes.Value = false;
            _plugin.ShowPlayerHitbox.Value = false;
            _plugin.ShowTriggers.Value = false;
            _plugin.TrajectoryVisibilityMode.Value = (int)TrajectoryVisibility.Hidden;
        }

        void RestoreTasVisuals()
        {
            if (!_visualsSaved)
                return;
            _visualsSaved = false;
            _plugin.ShowHud.Value = _savedShowHud;
            _plugin.HideViewmodels.Value = _savedHideViewmodels;
            _plugin.ShowHitboxes.Value = _savedShowHitboxes;
            _plugin.ShowPlayerHitbox.Value = _savedShowPlayerHitbox;
            _plugin.ShowTriggers.Value = _savedShowTriggers;
            _plugin.TrajectoryVisibilityMode.Value = _savedTrajectoryVisibility;
        }

        void StartAudio()
        {
            try
            {
                _audioPulls = 0;
                _audioEmpty = 0;
                _audioRejected = 0;
                _audioFramesKept = 0;
                _audioChannels = ChannelCount(AudioSettings.speakerMode);
                _wav = new WavWriter(_audioPath, _audioChannels, AudioSettings.outputSampleRate);
                if (AudioRenderer.Start())
                {
                    _audioRunning = true;
                    return;
                }
                _audioNote = " Audio: offline audio could not start, video only.";
            }
            catch (Exception ex)
            {
                _audioNote = " Audio failed to start (" + ex.Message + "), video only.";
            }
            if (_wav != null)
            {
                _wav.Dispose();
                _wav = null;
            }
            _plugin.LogWarning(_audioNote.Trim());
        }

        void PullAudio(bool keep)
        {
            if (!_audioRunning)
                return;
            _audioPulls++;
            // Render must be called every frame, also when the count is 0 (Unity discards
            // frames that are skipped), so the offline mixer keeps advancing.
            int frames = Math.Max(0, AudioRenderer.GetSampleCountForCaptureFrame());
            if (frames == 0)
            {
                _audioEmpty++;
                TryRender(0, _audioChannels, false);
                return;
            }

            if (TryRender(frames, _audioChannels, keep))
                return;

            // The buffer must be frames x the mixer's real channel count. If the speaker mode
            // reported something else, find the layout the mixer accepts and use it from now on.
            int[] layouts = { 2, 1, 6, 8, 4, 5 };
            for (int i = 0; i < layouts.Length; i++)
            {
                int channels = layouts[i];
                if (channels == _audioChannels)
                    continue;
                if (!TryRender(frames, channels, false))
                    continue;
                _plugin.LogInfo("Audio: mixer uses " + channels + " channels (speaker mode said " + _audioChannels + ").");
                _audioChannels = channels;
                if (_wav != null && _wav.DataBytes == 0)
                {
                    _wav.Dispose();
                    _wav = new WavWriter(_audioPath, _audioChannels, AudioSettings.outputSampleRate);
                }
                return;
            }
            _audioRejected++;
        }

        bool TryRender(int frames, int channels, bool keep)
        {
            NativeArray<float> buffer = new NativeArray<float>(frames * channels, Allocator.Temp);
            try
            {
                if (!AudioRenderer.Render(buffer))
                    return false;
                if (keep && _wav != null)
                {
                    _wav.Write(buffer);
                    _audioFramesKept += frames;
                }
                return true;
            }
            finally
            {
                buffer.Dispose();
            }
        }

        void StopAudio()
        {
            if (_audioRunning)
            {
                _plugin.LogInfo("Audio: " + _audioPulls + " pulls, " + _audioEmpty + " empty, " + _audioRejected +
                                " rejected, " + _audioFramesKept + " sample frames kept (" + _audioChannels + " ch, " +
                                AudioSettings.outputSampleRate + " Hz).");
                _audioRunning = false;
                try { AudioRenderer.Stop(); } catch (Exception) { }
            }
            if (_wav != null)
            {
                _wav.Dispose();
                _wav = null;
            }
        }

        /// <summary>
        /// Muxes the temporary video and WAV into the final file. If there is no audio or the
        /// mux fails, the video alone becomes the final file and the WAV is kept.
        /// </summary>
        void MuxAudio()
        {
            try
            {
                bool haveAudio = _audioPath != null && File.Exists(_audioPath) && new FileInfo(_audioPath).Length > 44;
                if (haveAudio)
                {
                    string arguments = string.Format(CultureInfo.InvariantCulture,
                        "-y -i \"{0}\" -i \"{1}\" -map 0:v -map 1:a -c:v copy " +
                        "-af \"loudnorm=I=-14:TP=-1.5:LRA=11,volume={3}\" -ar 48000 -c:a aac -b:a 320k \"{2}\"",
                        _videoPath, _audioPath, _finalPath, _volume);
                    ProcessStartInfo info = new ProcessStartInfo
                    {
                        FileName = _ffmpegExe,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using (Process mux = Process.Start(info))
                    {
                        string errors = mux.StandardError.ReadToEnd();
                        mux.WaitForExit(FfmpegExitTimeoutMs);
                        if (mux.HasExited && mux.ExitCode == 0)
                        {
                            File.Delete(_videoPath);
                            File.Delete(_audioPath);
                            _audioNote = " With audio (normalized to -14 LUFS, volume " + _volume + ").";
                            return;
                        }
                        string[] lines = errors.Trim().Split('\n');
                        _audioNote = " Audio mux failed (" + lines[lines.Length - 1].Trim() + "); audio kept at " + _audioPath + ".";
                    }
                }
                else if (string.IsNullOrEmpty(_audioNote))
                {
                    _audioNote = " No audio was captured.";
                }

                if (File.Exists(_finalPath))
                    File.Delete(_finalPath);
                File.Move(_videoPath, _finalPath);
            }
            catch (Exception ex)
            {
                _audioNote = " Audio step failed: " + ex.Message + ". Video is at " + _videoPath + ".";
            }
        }

        static int ChannelCount(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        void Teardown()
        {
            StopAudio();
            Rendering = false;
            RestoreTasVisuals();
            if (_hookObject != null)
            {
                UnityEngine.Object.Destroy(_hookObject);
                _hookObject = null;
            }
            if (_process != null)
            {
                try { _process.Dispose(); } catch (Exception) { }
                _process = null;
            }
            _stdin = null;
            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }
            if (_shot != null)
            {
                UnityEngine.Object.Destroy(_shot);
                _shot = null;
            }
            if (_overlayCaptureCamera != null)
            {
                UnityEngine.Object.Destroy(_overlayCaptureCamera.gameObject);
                _overlayCaptureCamera = null;
            }
            _sumR = _sumG = _sumB = null;
            _current = _previous = _out = null;
            _count = 0;
        }

        void OnFfmpegOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;
            lock (_ffmpegTail)
            {
                _ffmpegTail.Add(e.Data);
                if (_ffmpegTail.Count > TailLines)
                    _ffmpegTail.RemoveAt(0);
            }
        }

        string LastFfmpegLine()
        {
            lock (_ffmpegTail)
            {
                return _ffmpegTail.Count == 0 ? string.Empty : _ffmpegTail[_ffmpegTail.Count - 1];
            }
        }

        string ResolveOutput(string file)
        {
            if (Path.IsPathRooted(file))
                return file;
            string directory = _plugin.RecordingDirectory.Value;
            if (string.IsNullOrEmpty(directory))
                directory = Path.Combine(_plugin.ScriptDirectory, "Recordings");
            return Path.Combine(directory, file);
        }

        static string HeaderValue(TasScript script, string key)
        {
            List<KeyValuePair<string, string>> extras = script.Properties.Extras;
            for (int i = 0; i < extras.Count; i++)
            {
                if (string.Equals(extras[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return extras[i].Value;
            }
            return null;
        }

        static int ParseInt(string text, int fallback)
        {
            int value;
            return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        /// <summary>
        /// record_volume: a multiplier ("0.8", "1.5") or decibels ("-3dB"). Returns the value
        /// in FFmpeg volume-filter syntax, or null when it is not valid.
        /// </summary>
        static string ParseVolume(string text)
        {
            string value = text.Trim();
            float number;
            if (value.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            {
                string db = value.Substring(0, value.Length - 2).Trim();
                if (float.TryParse(db, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number >= -60f && number <= 30f)
                    return number.ToString("0.###", CultureInfo.InvariantCulture) + "dB";
                return null;
            }
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number >= 0f && number <= 16f)
                return number.ToString("0.###", CultureInfo.InvariantCulture);
            return null;
        }

        static float ParseFloat(string text, float fallback)
        {
            float value;
            return text != null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        static string GameRoot()
        {
            return BepInEx.Paths.GameRootPath;
        }
    }

    /// <summary>32-bit float PCM WAV writer; sizes are patched in on Dispose.</summary>
    internal sealed class WavWriter : IDisposable
    {
        readonly FileStream _stream;
        readonly BinaryWriter _writer;
        long _dataBytes;

        public long DataBytes { get { return _dataBytes; } }

        public WavWriter(string path, int channels, int sampleRate)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            _writer = new BinaryWriter(_stream);
            _writer.Write(new[] { 'R', 'I', 'F', 'F' });
            _writer.Write(0);
            _writer.Write(new[] { 'W', 'A', 'V', 'E' });
            _writer.Write(new[] { 'f', 'm', 't', ' ' });
            _writer.Write(16);
            _writer.Write((short)3);
            _writer.Write((short)channels);
            _writer.Write(sampleRate);
            _writer.Write(sampleRate * channels * 4);
            _writer.Write((short)(channels * 4));
            _writer.Write((short)32);
            _writer.Write(new[] { 'd', 'a', 't', 'a' });
            _writer.Write(0);
        }

        public void Write(NativeArray<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
                _writer.Write(samples[i]);
            _dataBytes += samples.Length * 4L;
        }

        public void Dispose()
        {
            _writer.Flush();
            _stream.Seek(4, SeekOrigin.Begin);
            _writer.Write((int)(36 + _dataBytes));
            _stream.Seek(40, SeekOrigin.Begin);
            _writer.Write((int)_dataBytes);
            _writer.Flush();
            _writer.Close();
        }
    }

    /// <summary>Runs a callback at WaitForEndOfFrame, after the frame is fully drawn.</summary>
    public sealed class VideoCaptureHook : MonoBehaviour
    {
        public Action Callback;

        void Start()
        {
            StartCoroutine(CaptureLoop());
        }

        IEnumerator CaptureLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (Callback != null)
                    Callback();
            }
        }
    }
}
