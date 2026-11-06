using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UltrakillTAS
{
    /// <summary>
    /// Full freeze of the game while the TAS clock is paused, without writing
    /// Time.timeScale.
    ///
    /// The tool never writes Time.timeScale, and Time.captureDeltaTime cannot be 0
    /// (0 means "real time"). A non-tick frame armed with a tiny captureDeltaTime still
    /// advanced game time: Unity clamps a frame's delta up to its minimum (about 1e-5 s),
    /// so every paused frame moved Time.time, ran every Update/LateUpdate/coroutine with
    /// that delta (NewMovement.Update reads Time.deltaTime 14 times) and, after ~800
    /// paused frames, a whole 0.008 s physics step.
    ///
    /// Instead, while the clock wants a frozen frame, Unity's player loop is swapped for
    /// a copy without the systems that advance or simulate the game:
    ///   - the time update (Time.time, deltaTime, fixed-step accumulator stay put),
    ///   - the whole FixedUpdate loop (physics and script FixedUpdate),
    ///   - script Update/LateUpdate, coroutines, Invoke timers,
    ///   - animators, timelines, particles, cloth, NavMesh agents, wind, video, VFX.
    /// Input, scene preloading, UI layout, rendering, audio output and IMGUI (the
    /// tool's window) keep running. The tool's own per-frame work (TasRuntime.Update and
    /// the FrameEndHook) is called from two systems put in place of script Update and
    /// LateUpdate, so hotkeys, the GUI, frame steps, free-look and freecam keep working.
    ///
    /// Swaps only happen in a system appended as the last top-level entry of both
    /// loops, so a frame always runs entirely on one loop whatever Unity does with a
    /// loop replaced while it runs.
    /// </summary>
    public static class SimulationFreeze
    {
        /// <summary>Player loop marker types for the tool's own systems.</summary>
        public struct TasFrameTail { }
        public struct TasFrozenUpdate { }
        public struct TasFrozenLateUpdate { }

        // "Category.System" names of systems a frozen frame does not run. Everything in
        // the FixedUpdate category is dropped as well (see BuildFrozen).
        static readonly HashSet<string> Removed = new HashSet<string>(StringComparer.Ordinal)
        {
            "PreUpdate.PhysicsClothUpdate",
            "PreUpdate.SendMouseEvents",
            "PreUpdate.AIUpdate",
            "PreUpdate.WindUpdate",
            "PreUpdate.UpdateVideo",
            "Update.DirectorUpdate",
            "Update.ScriptRunDelayedDynamicFrameRate",
            "Update.ScriptRunDelayedTasks",
            "PreLateUpdate.AIUpdatePostScript",
            "PreLateUpdate.DirectorUpdateAnimationBegin",
            "PreLateUpdate.LegacyAnimationUpdate",
            "PreLateUpdate.DirectorUpdateAnimationEnd",
            "PreLateUpdate.DirectorDeferredEvaluate",
            "PreLateUpdate.ParticleSystemBeginUpdateAll",
            "PreLateUpdate.ConstraintManagerUpdate",
            "PostLateUpdate.UpdateVideo",
            "PostLateUpdate.DirectorLateUpdate",
            "PostLateUpdate.ScriptRunDelayedDynamicFrameRate",
            "PostLateUpdate.VFXUpdate",
            "PostLateUpdate.ParticleSystemEndUpdateAll",
            "PostLateUpdate.PhysicsSkinnedClothBeginUpdate",
            "PostLateUpdate.PhysicsSkinnedClothFinishUpdate",
        };

        // The systems that advance Unity's clock (Time.time, deltaTime, frameCount).
        static readonly HashSet<string> TimeSystems = new HashSet<string>(StringComparer.Ordinal)
        {
            "TimeUpdate.WaitForLastPresentationAndUpdateTime",
            "Initialization.PlayerUpdateTime",
        };

        const string FixedCategory = "FixedUpdate";
        const string UpdateAnchor = "Update.ScriptRunBehaviourUpdate";
        const string LateUpdateAnchor = "PreLateUpdate.ScriptRunBehaviourLateUpdate";

        static readonly double StampToSeconds = 1.0 / Stopwatch.Frequency;

        // Kept alive here: the player loop only holds these delegates through Unity.
        static readonly PlayerLoopSystem.UpdateFunction TailCallback = OnFrameTail;
        static readonly PlayerLoopSystem.UpdateFunction FrozenUpdateCallback = OnFrozenUpdate;
        static readonly PlayerLoopSystem.UpdateFunction FrozenLateUpdateCallback = OnFrozenLateUpdate;

        static Plugin _plugin;
        static TimeController _clock;
        static PlayerLoopSystem _original;
        static PlayerLoopSystem _normal;
        static PlayerLoopSystem _frozen;
        static bool _attached;
        static bool _frozenBuilt;
        static bool _frozenActive;
        static bool _releaseRequested;
        static int _frozenFrames;
        static long _lastFrozenStamp;
        static double _frozenFrameSeconds = 1.0 / 60.0;
        static bool _updateFailureLogged;
        static bool _lateFailureLogged;

        /// <summary>The current frame runs on the frozen loop: no game time, no game code.</summary>
        public static bool Frozen { get { return _frozenActive; } }

        /// <summary>A frozen loop could be built for this Unity version.</summary>
        public static bool Available { get { return _frozenBuilt; } }

        /// <summary>
        /// Rendered-frame counter for the tool's once-per-frame guards. Time.frameCount is
        /// advanced by the time update, which a frozen frame does not run, so it can stand
        /// still while frozen; this value still changes on every frame.
        /// </summary>
        public static int FrameCount { get { return Time.frameCount + _frozenFrames; } }

        /// <summary>
        /// Put the tool's frame-tail system into the player loop (taking ownership of the
        /// loop as it is now) and build the frozen copy. Safe to call repeatedly.
        /// </summary>
        public static void Attach(Plugin plugin, TimeController clock)
        {
            _plugin = plugin;
            _clock = clock;
            if (_attached)
            {
                _releaseRequested = false;
                return;
            }
            try
            {
                _original = PlayerLoop.GetCurrentPlayerLoop();
                _normal = WithTail(_original);
                string missing;
                _frozenBuilt = BuildFrozen(_original, out _frozen, out missing);
                PlayerLoop.SetPlayerLoop(_normal);
                _attached = true;
                _frozenActive = false;
                _releaseRequested = false;
                _lastFrozenStamp = 0L;
                _frozenFrameSeconds = FrozenFrameSeconds();
                if (!ReferenceEquals(_plugin, null))
                {
                    if (_frozenBuilt)
                        _plugin.LogInfo("Pause freeze ready: paused frames run no time update, physics, game scripts, " +
                                        "animation or particles.");
                    else
                        _plugin.LogWarning("Pause freeze unavailable (player loop lacks " + missing + "); paused frames " +
                                           "fall back to an almost-zero captureDeltaTime.");
                }
            }
            catch (Exception ex)
            {
                _attached = false;
                _frozenBuilt = false;
                _frozenActive = false;
                if (!ReferenceEquals(_plugin, null))
                    _plugin.LogWarning("Pause freeze unavailable: " + ex.Message);
            }
        }

        /// <summary>
        /// Give the original player loop back. Applied by the frame tail at the end of the
        /// current frame, so the frame in progress finishes on the loop it started on.
        /// </summary>
        public static void Release()
        {
            if (_attached)
                _releaseRequested = true;
        }

        static void OnFrameTail()
        {
            if (!_attached)
                return;
            try
            {
                if (_releaseRequested || _clock == null || !_clock.Active)
                {
                    _releaseRequested = false;
                    _attached = false;
                    _frozenActive = false;
                    _lastFrozenStamp = 0L;
                    PlayerLoop.SetPlayerLoop(_original);
                    return;
                }

                bool freezeNext = _frozenBuilt && _clock.WantsFullFreeze;
                if (freezeNext)
                    _frozenFrames++;
                if (freezeNext == _frozenActive)
                    return;
                _frozenActive = freezeNext;
                if (!freezeNext)
                    _lastFrozenStamp = 0L;
                PlayerLoop.SetPlayerLoop(freezeNext ? _frozen : _normal);
            }
            catch (Exception ex)
            {
                // Never leave the game stuck on the frozen loop.
                _frozenBuilt = false;
                _frozenActive = false;
                try { PlayerLoop.SetPlayerLoop(_normal); } catch { }
                if (!ReferenceEquals(_plugin, null))
                    _plugin.LogWarning("Pause freeze disabled: " + ex.Message);
            }
        }

        static void OnFrozenUpdate()
        {
            PaceFrozenFrame();
            try
            {
                TasRuntime.RunUpdate();
            }
            catch (Exception ex)
            {
                if (!_updateFailureLogged && !ReferenceEquals(_plugin, null))
                {
                    _updateFailureLogged = true;
                    _plugin.LogWarning("Frozen frame update failed: " + ex);
                }
            }
        }

        static void OnFrozenLateUpdate()
        {
            try
            {
                if (!ReferenceEquals(_plugin, null))
                    _plugin.RunFrozenLateUpdate();
            }
            catch (Exception ex)
            {
                if (!_lateFailureLogged && !ReferenceEquals(_plugin, null))
                {
                    _lateFailureLogged = true;
                    _plugin.LogWarning("Frozen frame end failed: " + ex);
                }
            }
        }

        /// <summary>
        /// The frozen loop has no time update, and with it no frame-rate wait. Keep the
        /// paused GUI near the display's refresh rate instead of spinning the CPU.
        /// </summary>
        static void PaceFrozenFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastFrozenStamp != 0L)
            {
                double elapsed = (now - _lastFrozenStamp) * StampToSeconds;
                double wait = _frozenFrameSeconds - elapsed;
                if (wait > 0.001)
                {
                    Thread.Sleep((int)(wait * 1000.0));
                    now = Stopwatch.GetTimestamp();
                }
            }
            _lastFrozenStamp = now;
        }

        static double FrozenFrameSeconds()
        {
            double hz = 60.0;
            try
            {
                double refresh = Screen.currentResolution.refreshRateRatio.value;
                if (refresh >= 30.0 && refresh <= 1000.0)
                    hz = refresh;
            }
            catch
            {
            }
            return 1.0 / Math.Min(hz, 240.0);
        }

        static PlayerLoopSystem WithTail(PlayerLoopSystem root)
        {
            PlayerLoopSystem[] top = root.subSystemList ?? new PlayerLoopSystem[0];
            PlayerLoopSystem[] list = new PlayerLoopSystem[top.Length + 1];
            Array.Copy(top, list, top.Length);
            list[top.Length] = Tail();
            root.subSystemList = list;
            return root;
        }

        static PlayerLoopSystem Tail()
        {
            return new PlayerLoopSystem
            {
                type = typeof(TasFrameTail),
                subSystemList = new[]
                {
                    new PlayerLoopSystem { type = typeof(TasFrameTail), updateDelegate = TailCallback }
                }
            };
        }

        /// <summary>
        /// Copy of the loop with every simulating system removed and the tool's systems in
        /// place of script Update/LateUpdate. Top-level categories are all kept (only their
        /// contents change), so the frame tail sits at the same index in both loops.
        /// </summary>
        static bool BuildFrozen(PlayerLoopSystem root, out PlayerLoopSystem frozen, out string missing)
        {
            bool timeRemoved = false;
            bool updateReplaced = false;
            bool lateReplaced = false;
            PlayerLoopSystem[] top = root.subSystemList ?? new PlayerLoopSystem[0];
            PlayerLoopSystem[] list = new PlayerLoopSystem[top.Length + 1];
            for (int i = 0; i < top.Length; i++)
            {
                PlayerLoopSystem category = top[i];
                PlayerLoopSystem[] children = category.subSystemList;
                if (children == null)
                {
                    list[i] = category;
                    continue;
                }
                bool fixedCategory = category.type != null && category.type.Name == FixedCategory;
                List<PlayerLoopSystem> kept = new List<PlayerLoopSystem>(children.Length);
                for (int j = 0; j < children.Length; j++)
                {
                    PlayerLoopSystem child = children[j];
                    string key = Key(child.type);
                    if (fixedCategory)
                        continue;
                    if (TimeSystems.Contains(key))
                    {
                        timeRemoved = true;
                        continue;
                    }
                    if (key == UpdateAnchor)
                    {
                        kept.Add(new PlayerLoopSystem { type = typeof(TasFrozenUpdate), updateDelegate = FrozenUpdateCallback });
                        updateReplaced = true;
                        continue;
                    }
                    if (key == LateUpdateAnchor)
                    {
                        kept.Add(new PlayerLoopSystem { type = typeof(TasFrozenLateUpdate), updateDelegate = FrozenLateUpdateCallback });
                        lateReplaced = true;
                        continue;
                    }
                    if (Removed.Contains(key))
                        continue;
                    kept.Add(child);
                }
                category.subSystemList = kept.ToArray();
                list[i] = category;
            }
            list[top.Length] = Tail();
            frozen = root;
            frozen.subSystemList = list;

            missing = string.Empty;
            if (!timeRemoved)
                missing += " time update";
            if (!updateReplaced)
                missing += " " + UpdateAnchor;
            if (!lateReplaced)
                missing += " " + LateUpdateAnchor;
            missing = missing.Trim();
            return timeRemoved && updateReplaced && lateReplaced;
        }

        static string Key(Type type)
        {
            if (type == null)
                return string.Empty;
            return type.DeclaringType != null ? type.DeclaringType.Name + "." + type.Name : type.Name;
        }
    }
}
