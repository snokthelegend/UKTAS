using System;
using System.Diagnostics;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Where a fast-forward frame's time goes. While F7, an ff line or FFF NoRefresh runs,
    /// one log line a second splits the average frame into the tool's own work (Update,
    /// ticks, end of frame, OnGUI, trace) and everything else (game scripts, physics,
    /// rendering, present), plus Unity's own CPU/GPU/present-wait timings when available.
    /// The point is to name the limiter instead of guessing it.
    /// </summary>
    public static class FastForwardProfiler
    {
        static readonly double StampToMs = 1000.0 / Stopwatch.Frequency;
        static readonly FrameTiming[] Timings = new FrameTiming[1];

        static long _lastFrameStamp;
        static long _windowStart;
        static int _frames;
        static int _ticks;
        static double _frameMs;
        static double _updateMs;
        static double _tickMs;
        static double _traceMs;
        static double _endMs;
        static double _guiMs;
        static double _cpuMs;
        static double _gpuMs;
        static double _presentMs;
        static double _renderThreadMs;
        static int _unitySamples;

        // Per-frame accumulators, folded into the window at the next frame start.
        static double _frameUpdate;
        static double _frameTick;
        static double _frameTrace;
        static double _frameEnd;
        static double _frameGui;
        static bool _frameTicked;

        /// <summary>Set by the plugin each frame: true while any fast-forward is running.</summary>
        public static bool Enabled;

        public static long Now() { return Stopwatch.GetTimestamp(); }
        public static double Ms(long start) { return (Stopwatch.GetTimestamp() - start) * StampToMs; }

        public static void AddUpdate(double ms) { _frameUpdate += ms; }
        public static void AddTick(double ms) { _frameTick += ms; _frameTicked = true; }
        public static void AddTrace(double ms) { _frameTrace += ms; }
        public static void AddEnd(double ms) { _frameEnd += ms; }
        public static void AddGui(double ms) { _frameGui += ms; }

        /// <summary>Call first thing in the frame (plugin Update). Logs once a second.</summary>
        static int _lastFrame = int.MinValue;

        public static void FrameStart(Plugin plugin, int frame)
        {
            // The update can be driven twice in one frame (frozen path); count it once.
            if (frame == _lastFrame)
                return;
            _lastFrame = frame;
            long now = Stopwatch.GetTimestamp();
            if (!Enabled)
            {
                Reset(now);
                return;
            }

            if (_lastFrameStamp != 0L)
            {
                _frames++;
                _frameMs += (now - _lastFrameStamp) * StampToMs;
                _updateMs += _frameUpdate;
                _tickMs += _frameTick;
                _traceMs += _frameTrace;
                _endMs += _frameEnd;
                _guiMs += _frameGui;
                if (_frameTicked)
                    _ticks++;
                SampleUnity();
            }
            _lastFrameStamp = now;
            ClearFrame();

            if (_windowStart == 0L)
                _windowStart = now;
            if ((now - _windowStart) * StampToMs < 1000.0 || _frames == 0)
                return;

            double n = _frames;
            double frameAvg = _frameMs / n;
            double tool = (_updateMs + _endMs + _guiMs) / n;
            double other = Math.Max(0.0, frameAvg - tool);
            string unity = _unitySamples > 0
                ? "  | unity cpu " + (_cpuMs / _unitySamples).ToString("0.00") +
                  " gpu " + (_gpuMs / _unitySamples).ToString("0.00") +
                  " render-thread " + (_renderThreadMs / _unitySamples).ToString("0.00") +
                  " present-wait " + (_presentMs / _unitySamples).ToString("0.00") + " ms"
                : "  | unity frame timings unavailable";
            if (!ReferenceEquals(plugin, null))
                plugin.LogInfo("FF profile: " + (n * 1000.0 / ((now - _windowStart) * StampToMs)).ToString("0") + " fps, " +
                               _ticks + "/" + _frames + " frames ticked | frame " + frameAvg.ToString("0.00") +
                               " ms = tool " + tool.ToString("0.00") +
                               " (update " + ((_updateMs - _tickMs) / n).ToString("0.00") +
                               ", ticks " + ((_tickMs - _traceMs) / n).ToString("0.00") +
                               ", trace " + (_traceMs / n).ToString("0.00") +
                               ", end-frame " + (_endMs / n).ToString("0.00") +
                               ", gui " + (_guiMs / n).ToString("0.00") +
                               ") + game/physics/render/present " + other.ToString("0.00") + " ms" +
                               unity +
                               " | vsync " + QualitySettings.vSyncCount +
                               " target " + Application.targetFrameRate +
                               " queued " + QualitySettings.maxQueuedFrames +
                               " fixedDt " + Time.fixedDeltaTime.ToString("0.####") +
                               " capture " + Time.captureDeltaTime.ToString("0.####") +
                               " screen " + Screen.fullScreenMode);
            Reset(now);
            _lastFrameStamp = now;
            _windowStart = now;
        }

        static void SampleUnity()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, Timings) < 1)
                    return;
                FrameTiming t = Timings[0];
                if (t.cpuFrameTime <= 0.0)
                    return;
                _cpuMs += t.cpuMainThreadFrameTime;
                _gpuMs += t.gpuFrameTime;
                _presentMs += t.cpuMainThreadPresentWaitTime;
                _renderThreadMs += t.cpuRenderThreadFrameTime;
                _unitySamples++;
            }
            catch (Exception)
            {
                // Frame timing stats are off in this player build; the tool's split still logs.
            }
        }

        static void ClearFrame()
        {
            _frameUpdate = 0.0;
            _frameTick = 0.0;
            _frameTrace = 0.0;
            _frameEnd = 0.0;
            _frameGui = 0.0;
            _frameTicked = false;
        }

        static void Reset(long now)
        {
            _lastFrameStamp = 0L;
            _windowStart = 0L;
            _frames = 0;
            _ticks = 0;
            _frameMs = _updateMs = _tickMs = _traceMs = _endMs = _guiMs = 0.0;
            _cpuMs = _gpuMs = _presentMs = _renderThreadMs = 0.0;
            _unitySamples = 0;
            ClearFrame();
        }
    }
}
