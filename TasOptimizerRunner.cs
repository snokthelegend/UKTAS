using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// What the runner needs from the game side. The engine implements it through
    /// <see cref="EngineOptimizerHost"/>; tests or a headless driver can supply their own.
    /// </summary>
    public interface IOptimizerHost
    {
        /// <summary>Playback tick after the frame that just ended.</summary>
        int CurrentTick { get; }
        /// <summary>True when every attempt replays the script from tick 0 instead of loading a savestate.</summary>
        bool FullReplay { get; }
        /// <summary>Captures the master savestate at the segment start tick. False when it cannot be taken.</summary>
        bool CaptureMaster();
        /// <summary>
        /// Rewinds to the segment start for the next attempt. Savestate rollback is at the start tick on
        /// return; a full replay restarts the script and reaches it on a later frame. False on failure.
        /// </summary>
        bool RestoreMaster();
        /// <summary>Playback is at the start tick: attempt playback (fast-forward or headless) begins.</summary>
        void EnterSegment();
        /// <summary>Splices the candidate segment into the live script without moving playback. False when it cannot be spliced.</summary>
        bool ActivateCandidate(List<TasFrameLine> segment);
        /// <summary>
        /// The attempt just played its last tick. True when the host keeps this frame on screen and holds
        /// the next one still, so the runner scores and rolls back on its next call; false to do it now.
        /// </summary>
        bool ShowEndFrame();
        /// <summary>Reads the player state after the current tick. False when it cannot be read.</summary>
        bool TrySample(out OptimSample sample);
        /// <summary>Restores the original script and the master state, then reports the reason.</summary>
        void Finish(string message);
    }

    /// <summary>
    /// Frame-driven attempt loop. The engine calls <see cref="OnFrameEnd"/> after every game frame.
    /// Each attempt: restore master at StartTick, play the candidate to EndTick, sampling every tick,
    /// stop early on death, then hand the samples to <see cref="TasOptimizer.CompleteAttempt"/>.
    /// Attempts repeat on the same segment until the user stops the run; a candidate that cannot be
    /// spliced or rewound counts as an invalid attempt and the next frame rolls a new one.
    /// The runner does nothing until the engine has played up to StartTick, so ordinary playback is untouched
    /// unless a run was explicitly started.
    /// </summary>
    public sealed class TasOptimizerRunner
    {
        readonly TasOptimizer _optimizer;
        readonly IOptimizerHost _host;
        readonly List<OptimSample> _samples = new List<OptimSample>();
        bool _seeking = true;
        bool _attemptActive;
        // Full replay: the attempt's script restarted and is still playing toward StartTick.
        bool _waitingForStart;
        bool _finished;
        // The attempt's last frame is on screen (headless); it is scored and rolled back on the next call.
        bool _endFramePending;

        public TasOptimizerRunner(TasOptimizer optimizer, IOptimizerHost host)
        {
            if (optimizer == null) throw new ArgumentNullException("optimizer");
            if (host == null) throw new ArgumentNullException("host");
            _optimizer = optimizer;
            _host = host;
        }

        public bool Finished { get { return _finished; } }
        public TasOptimizer Optimizer { get { return _optimizer; } }
        /// <summary>True while playback runs toward StartTick (first seek or a full replay).</summary>
        public bool Seeking { get { return !_finished && (_seeking || _waitingForStart); } }

        /// <summary>Stop early (button). The best segment found so far is kept.</summary>
        public void Cancel()
        {
            if (_finished) return;
            _optimizer.Stop();
            Finish("Optimizer stopped.");
        }

        public void OnFrameEnd()
        {
            if (_finished) return;
            int tick = _host.CurrentTick;
            int start = _optimizer.Config.StartTick;
            int end = _optimizer.Config.EndTick;

            // The held frame after the attempt's last one: that frame has been shown, now score and roll.
            if (_endFramePending)
            {
                _endFramePending = false;
                CompleteAttempt();
                return;
            }

            if (_seeking)
            {
                if (tick < start)
                    return;
                if (tick > start)
                {
                    Finish("Playback passed the start tick before the optimizer began. Rewind and start again.");
                    return;
                }
                _seeking = false;
                if (!_host.CaptureMaster())
                {
                    Finish("Optimizer stopped: the master savestate could not be captured at the start tick.");
                    return;
                }
                _optimizer.BeginAsync();
                BeginAttempt();
                return;
            }

            // The previous candidate was rejected before it could play: roll the next one now.
            if (!_attemptActive)
            {
                BeginAttempt();
                return;
            }

            if (_waitingForStart)
            {
                if (tick < start)
                    return;
                _waitingForStart = false;
                if (tick > start)
                {
                    // The replay overshot the start tick (nothing to compare): count it and roll again.
                    if (RejectAttempt())
                        BeginAttempt();
                    return;
                }
                _host.EnterSegment();
                return;
            }

            if (tick > start && tick <= end)
            {
                OptimSample sample;
                if (_host.TrySample(out sample))
                {
                    sample.Tick = tick;
                    _samples.Add(sample);
                    if (sample.Dead)
                    {
                        EndAttempt();
                        return;
                    }
                }
            }
            if (tick >= end)
                EndAttempt();
        }

        /// <summary>
        /// The attempt is over (EndTick reached or death). Headless, its last frame renders first and the
        /// scoring waits one frame; otherwise it is scored now.
        /// </summary>
        void EndAttempt()
        {
            if (_host.ShowEndFrame())
            {
                _endFramePending = true;
                return;
            }
            CompleteAttempt();
        }

        // Rejected candidates re-rolled within one frame before the loop waits for the next frame.
        const int MaxRollsPerFrame = 16;

        void BeginAttempt()
        {
            for (int roll = 0; roll < MaxRollsPerFrame; roll++)
            {
                List<TasFrameLine> candidate = _optimizer.NextAttempt();
                if (candidate == null)
                {
                    // Only after Stop(): a running optimizer always hands out a candidate.
                    Finish("Optimizer finished.");
                    return;
                }
                _samples.Clear();
                // The candidate goes in first: if it cannot be spliced, the previous script would keep
                // playing, so the attempt is rejected instead of being scored as this candidate.
                if (_host.ActivateCandidate(candidate) && _host.RestoreMaster())
                {
                    _attemptActive = true;
                    _waitingForStart = _host.FullReplay;
                    if (!_waitingForStart)
                        _host.EnterSegment();
                    return;
                }
                if (!RejectAttempt())
                    return;
            }
            // Still nothing playable: park playback at the start tick (savestate rollback) so it cannot run
            // off the end of the script, and roll again next frame.
            if (!_host.FullReplay)
                _host.RestoreMaster();
        }

        void CompleteAttempt()
        {
            _attemptActive = false;
            _optimizer.CompleteAttempt(new List<OptimSample>(_samples));
            if (!_optimizer.Running)
            {
                Finish("Optimizer stopped: baseline could not be simulated.");
                return;
            }
            BeginAttempt();
        }

        /// <summary>
        /// The pending candidate never ran: it counts as an invalid attempt (no samples) and a new one is
        /// rolled. Only a rejected baseline ends the run, since nothing can be compared. False when it ended.
        /// </summary>
        bool RejectAttempt()
        {
            _attemptActive = false;
            _waitingForStart = false;
            _optimizer.CompleteAttempt(null);
            if (!_optimizer.Running)
            {
                Finish("Optimizer stopped: baseline could not be simulated.");
                return false;
            }
            return true;
        }

        void Finish(string message)
        {
            if (_finished) return;
            _finished = true;
            _attemptActive = false;
            _waitingForStart = false;
            _endFramePending = false;
            _optimizer.Stop();
            _host.Finish(message);
        }
    }

    /// <summary>Engine adapter: samples player state through GameBridge and swaps candidate scripts.</summary>
    public sealed class EngineOptimizerHost : IOptimizerHost
    {
        readonly TasEngine _engine;
        readonly SaveStateManager _states;
        readonly GameBridge _bridge;
        readonly TasScript _original;
        readonly string _path;
        readonly int _startTick;
        readonly int _endTick;
        readonly Action<string> _log;
        Savestate _master;
        // Line indices bounding the optimized segment in the original script, resolved once.
        readonly int _segmentFirstLine;
        readonly int _segmentEndLine;
        readonly bool _segmentValid;

        public EngineOptimizerHost(TasEngine engine, SaveStateManager states, GameBridge bridge,
                                   TasScript original, string path, int startTick, int endTick, Action<string> log)
        {
            _engine = engine;
            _states = states;
            _bridge = bridge;
            _original = original;
            _path = path;
            _startTick = startTick;
            _endTick = endTick;
            _log = log ?? delegate(string s) { };
            TickLocation a = original.Locate(startTick);
            TickLocation b = original.Locate(endTick);
            _segmentValid = a.Valid && b.Valid && a.TickWithinLine == 0 && b.TickWithinLine == 0
                            && b.LineIndex > a.LineIndex;
            _segmentFirstLine = a.LineIndex;
            _segmentEndLine = b.LineIndex;
        }

        public int CurrentTick { get { return _engine.CurrentTick; } }

        /// <summary>True when the run uses headless NoRefresh (the "Headless FFF" execution mode).</summary>
        public bool Headless { get; set; }

        /// <summary>"Lock shots to airborne projectiles/coins" (Optimizer tab, on by default).</summary>
        public bool AimLockEnabled { get; set; }

        // Framebulk ordinal (0-based count of framebulks inside the segment) keyed by candidate line index.
        // Ordinals survive repeat/framecount mutations, unlike absolute ticks.
        readonly Dictionary<int, int> _ordinalOfLine = new Dictionary<int, int>();
        // Candidate lines as spliced into the engine script (same indexing as TickLocation.LineIndex).
        List<TasFrameLine> _candidateLines = new List<TasFrameLine>();
        // Projectile tagged per firing framebulk ordinal during the baseline pass.
        readonly Dictionary<int, LockedShot> _lockedShots = new Dictionary<int, LockedShot>();
        int _attempts;
        bool _recordingBaseline;

        sealed class LockedShot
        {
            public readonly string TypeName;
            public readonly int InstanceId;
            public LockedShot(string typeName, int instanceId) { TypeName = typeName; InstanceId = instanceId; }
        }

        /// <summary>Rollback "Full FFF Replay": every attempt replays from tick 0 instead of loading the master.</summary>
        public bool FullReplay { get; set; }

        // Engine cursor (tick, time, angles, held buttons) at the start tick; the savestate only restores the game.
        TasEngine.OptimCursor _cursor;
        // Candidate script last spliced in, re-applied after a full replay's Play (which may reload from disk).
        TasScript _candidateScript;

        public bool CaptureMaster()
        {
            _cursor = _engine.CaptureOptimizerCursor();
            _engine.OptimAimLock = AimLock;
            if (FullReplay)
                return true;
            _master = _states.CaptureOptimizerMaster(_startTick);
            if (_master == null)
            {
                _log("Optimizer: master savestate could not be captured at tick " + _startTick + ".");
                return false;
            }
            return true;
        }

        public void EnterSegment()
        {
            // Rendering returns after the seek; Headless keeps it off for the attempts, one tick per frame.
            _engine.BeginOptimizerAttempts(Headless);
        }

        /// <summary>
        /// Aim lock, called on the first tick of a framebulk. The bulk is identified by its ordinal inside the
        /// segment, and fires only if its own buttons hold Fire1/Fire2/Punch. Baseline pass: tag the nearest
        /// airborne projectile for that ordinal and keep the script's aim. Candidate passes: aim at the tagged
        /// projectile (live instance if still present, else the nearest of its type) from the camera position.
        /// </summary>
        bool AimLock(int lineIndex, out Vector3 direction)
        {
            direction = Vector3.zero;
            int ordinal;
            if (!AimLockEnabled || !_ordinalOfLine.TryGetValue(lineIndex, out ordinal))
                return false;
            if (lineIndex < 0 || lineIndex >= _candidateLines.Count)
                return false;
            const TasButtons fire = TasButtons.Fire1 | TasButtons.Fire2 | TasButtons.Punch;
            if ((_candidateLines[lineIndex].Buttons & fire) == 0)
                return false;
            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera) || camera == null)
                return false;
            Vector3 origin = camera.position;

            if (_recordingBaseline)
            {
                string typeName;
                int id;
                Vector3 point;
                if (_bridge.TryFindNearestAimLockProjectile(origin, out typeName, out id, out point))
                    _lockedShots[ordinal] = new LockedShot(typeName, id);
                return false;
            }

            LockedShot shot;
            if (!_lockedShots.TryGetValue(ordinal, out shot))
                return false;
            Vector3 target;
            int liveId;
            if (!_bridge.TryFindProjectile(shot.TypeName, shot.InstanceId, origin, out target, out liveId))
                return false;
            direction = target - origin;
            return direction.sqrMagnitude > 1e-6f;
        }


        public bool RestoreMaster()
        {
            if (FullReplay)
            {
                string error;
                if (!_engine.ReplayForOptimizer(_startTick, out error))
                {
                    _log("Optimizer: replay from tick 0 failed: " + error);
                    return false;
                }
                if (_candidateScript != null && _engine.Script != _candidateScript)
                    _engine.SetScriptKeepPosition(_candidateScript, _path);
                return true;
            }
            if (_master == null)
                return false;
            SaveResult result = _states.RestoreOptimizerMaster(_master);
            // The cursor goes back even on a failed load, so playback never runs on toward the script end.
            _engine.RestoreOptimizerCursor(_cursor);
            if (!result.Success)
            {
                _log("Optimizer: master restore failed: " + result.Message);
                return false;
            }
            return true;
        }

        public bool ActivateCandidate(List<TasFrameLine> segment)
        {
            // Attempt 1 is the unmodified baseline; it tags projectiles for the aim lock.
            _attempts++;
            _recordingBaseline = _attempts == 1;

            // In-memory build: clone the original lines outside the segment, then the candidate.
            // No serialization and no text parsing per attempt. The original script is never mutated.
            if (!_segmentValid)
            {
                _log("Optimizer: segment boundaries are not on framebulk lines.");
                return false;
            }
            List<TasFrameLine> source = _original.Lines;
            List<TasFrameLine> lines = new List<TasFrameLine>(source.Count + segment.Count - (_segmentEndLine - _segmentFirstLine));
            for (int i = 0; i < _segmentFirstLine; i++)
                lines.Add(source[i].Clone());
            for (int i = 0; i < segment.Count; i++)
                lines.Add(segment[i].Clone());
            for (int i = _segmentEndLine; i < source.Count; i++)
                lines.Add(source[i].Clone());

            TasScript candidate = new TasScript();
            candidate.Properties = _original.Properties;
            candidate.SourceName = _original.SourceName;
            candidate.Lines = lines;

            // Same length as the original (the script's own tick rules, so special frames count too), or the
            // tail after EndTick would shift. Expected with framecount mutations and Keep Total Ticks off:
            // counted as invalid and re-rolled. Logged once, not per attempt, so the toast area is not flooded.
            int delta = candidate.TotalTicks - _original.TotalTicks;
            if (delta != 0)
            {
                if (!_warnedTickCount)
                    _log("Optimizer: candidate has " + (_endTick - _startTick + delta) + " ticks, expected " +
                         (_endTick - _startTick) + "; such candidates are skipped as invalid.");
                _warnedTickCount = true;
                return false;
            }

            _ordinalOfLine.Clear();
            int ordinal = 0;
            for (int i = 0; i < segment.Count; i++)
                if (segment[i].IsFramebulk)
                    _ordinalOfLine[_segmentFirstLine + i] = ordinal++;
            _candidateLines = lines;
            _candidateScript = candidate;
            _engine.SetScriptKeepPosition(candidate, _path);
            return true;
        }

        bool _warnedTickCount;

        public bool ShowEndFrame()
        {
            return Headless && _engine.ShowOptimizerEndFrame();
        }

        public bool TrySample(out OptimSample sample)
        {
            sample = new OptimSample();
            Vector3 position;
            if (!_bridge.TryGetPlayerPosition(out position))
                return false;
            Vector3 velocity;
            _bridge.TryGetPlayerVelocity(out velocity);
            bool onGround;
            bool sliding, dashing, jumping;
            _bridge.TryGetMovementState(out onGround, out sliding, out dashing, out jumping);
            float health, hardDamage;
            bool hasHealth = _bridge.TryGetHealth(out health, out hardDamage);
            sample.Position = position;
            sample.Velocity = velocity;
            sample.Grounded = onGround;
            sample.Health = hasHealth ? health : 0f;
            // Falling out of the level counts as a death (the engine skips its kill-plane pause during attempts).
            sample.Dead = (hasHealth && health <= 0f) || _bridge.PlayerBelowKillPlane;
            return true;
        }

        public void Finish(string message)
        {
            _engine.OptimAimLock = null;
            _recordingBaseline = false;
            _engine.SetScriptKeepPosition(_original, _path);
            // Savestate rollback: back to the start tick with the original script. A full replay is not
            // restarted here; playback carries on from where the run stopped.
            if (!FullReplay && _master != null)
                RestoreMaster();
            _engine.EndOptimizerAttempts();
            _log(message);
        }
    }
}
