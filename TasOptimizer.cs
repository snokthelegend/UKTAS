using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>Built-in numeric objectives. Score is always "bigger is better" after direction is applied.</summary>
    public enum OptimObjective
    {
        ReachGoalFastest,
        PosX, PosY, PosZ,
        VelX, VelY, VelZ,
        SpeedXZ, Speed3D,
        DistanceToGoal
    }

    public enum OptimDirection { Maximize, Minimize }

    /// <summary>Evaluate a constraint or objective on the last tick of the segment, or on any tick.</summary>
    public enum OptimScope { LastFrame, AllFrames }

    public enum OptimOperator { Less, LessEq, Greater, GreaterEq }

    public sealed class OptimConstraint
    {
        public OptimObjective Variable = OptimObjective.PosY;
        public OptimOperator Op = OptimOperator.Less;
        public double Value;
        public OptimScope Scope = OptimScope.LastFrame;

        public override string ToString()
        {
            string op = Op == OptimOperator.Less ? "<" : Op == OptimOperator.LessEq ? "<=" :
                        Op == OptimOperator.Greater ? ">" : ">=";
            return TasOptimizer.VariableName(Variable) + " " + op + " " +
                   Value.ToString("0.###", CultureInfo.InvariantCulture) +
                   (Scope == OptimScope.LastFrame ? " (Last Frame)" : " (All Frames)");
        }
    }

    /// <summary>Per-tick state recorded while a candidate segment runs.</summary>
    public struct OptimSample
    {
        public int Tick;
        public Vector3 Position;
        public Vector3 Velocity;
        public bool Grounded;
        public float Health;
        public bool Dead;
    }

    /// <summary>
    /// Runs one candidate segment from the master savestate at the start tick. The caller restores
    /// the savestate, plays the given framebulks and returns one sample per tick. It must return
    /// null when the run cannot be simulated (no player, scene change).
    /// </summary>
    public delegate List<OptimSample> OptimRunner(List<TasFrameLine> candidate, int startTick, int endTick);

    /// <summary>Which mutations the brute-forcer may apply. Defaults follow the spec: combat and weapons off.</summary>
    public sealed class OptimMutationSettings
    {
        public bool MutateYaw = true;
        public float YawStep = 2f;            // degrees, 0.01 .. 25
        public bool MutatePitch = true;
        public float PitchStep = 2f;          // degrees, 0.01 .. 25
        public bool MutateFramecount = true;
        public int FramecountStep = 1;        // ticks, 1 .. 15
        public bool KeepTotalTicks = true;
        public bool MutateMovement = true;    // f, b, l, r in field 2
        public bool MutateDashSlide = true;   // d, s in field 2
        public bool MutateJump = true;        // j in field 3
        public bool MutateCombat = false;     // 1, 2, p, v in field 3
        public bool MutateWeapons = false;    // w1..w6
        public int BulksPerIteration = 1;     // 1 .. 4
        public float RestartChance = 0.05f;   // restart from baseline
        public float TopFiveChance = 0.2f;    // mutate from a previous top-5 entry
    }

    /// <summary>Everything the user can set on the Optimizer tab.</summary>
    public sealed class OptimConfig
    {
        public int StartTick;
        public int EndTick;
        public OptimObjective Objective = OptimObjective.ReachGoalFastest;
        public OptimDirection Direction = OptimDirection.Maximize;
        public OptimScope Scope = OptimScope.LastFrame;
        public bool ProjectileAwareness = true;
        public bool UseFastForwardHeadless = false;
        public bool RollbackFullReplay = false;   // false: restore master savestate at Start; true: replay from tick 0 headless
        public bool SimulatedAnnealing = false;
        public double Temperature = 0.1;      // accept worse candidates with exp(-delta / T) when annealing
        public readonly List<OptimConstraint> Constraints = new List<OptimConstraint>();
        public readonly OptimMutationSettings Mutations = new OptimMutationSettings();
    }

    public enum OptimOutcomeKind { Valid, InvalidConstraint, InvalidKeepOut, InvalidWaypoint, InvalidDeath, NoSimulation }

    public sealed class TasOptimizer
    {
        public const int LogCapacity = 10;
        public const int TopCapacity = 5;

        public readonly OptimConfig Config;
        public readonly OptimBoxSet Boxes;
        public readonly List<string> Log = new List<string>(LogCapacity);

        readonly List<TasFrameLine> _baseline;
        readonly List<TasFrameLine> _best;
        readonly List<KeyValuePair<double, List<TasFrameLine>>> _top = new List<KeyValuePair<double, List<TasFrameLine>>>();
        readonly System.Random _rng;
        readonly OptimRunner _runner;

        public double BaselineScore { get; private set; }
        public double BestScore { get; private set; }
        public int Iterations { get; private set; }
        public int InvalidCount { get; private set; }
        public OptimOutcomeKind LastOutcome { get; private set; }
        public List<OptimSample> LastCandidateSamples { get; private set; }
        public List<OptimSample> BestSamples { get; private set; }
        public List<OptimSample> BaselineSamples { get; private set; }
        public bool Running { get; private set; }

        public IList<TasFrameLine> BestSegment { get { return _best; } }

        public TasOptimizer(OptimConfig config, OptimBoxSet boxes, List<TasFrameLine> segment, OptimRunner runner, int seed)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (boxes == null) throw new ArgumentNullException("boxes");
            if (segment == null) throw new ArgumentNullException("segment");
            if (runner == null) throw new ArgumentNullException("runner");
            Config = config;
            Boxes = boxes;
            _runner = runner;
            // Untracked: the game's RNG savestate set (and every master restore) must not rewind it,
            // and creating it must not advance the game's System.Random seed stream.
            _rng = RngController.CreateUntracked(seed);
            _baseline = CloneAll(segment);
            _best = CloneAll(segment);
        }

        // Attempts are split into request and result so the engine can simulate them over several
        // game frames: NextAttempt() hands out the candidate, CompleteAttempt() takes its samples.
        // The synchronous Begin()/Step() wrap the same pair with the runner delegate.

        List<TasFrameLine> _pendingCandidate;
        bool _pendingIsBaseline;

        public bool HasPendingAttempt { get { return _pendingCandidate != null; } }
        public List<TasFrameLine> PendingCandidate { get { return _pendingCandidate; } }

        /// <summary>Queues the untouched segment as the first attempt (the baseline run).</summary>
        public void BeginAsync()
        {
            _pendingCandidate = CloneAll(_baseline);
            _pendingIsBaseline = true;
        }

        /// <summary>Next candidate to simulate, or null when stopped. Counts one iteration.</summary>
        public List<TasFrameLine> NextAttempt()
        {
            if (!Running || _pendingCandidate != null)
                return _pendingCandidate;
            Iterations++;
            _pendingCandidate = MakeCandidate();
            _pendingIsBaseline = false;
            return _pendingCandidate;
        }

        /// <summary>Simulate the untouched segment once and store it as the initial Current Best.</summary>
        public bool Begin()
        {
            BeginAsync();
            CompleteAttempt(_runner(CloneAll(_baseline), Config.StartTick, Config.EndTick));
            return Running;
        }

        public void Stop()
        {
            Running = false;
            _pendingCandidate = null;
        }

        /// <summary>One brute-force iteration: mutate, simulate, validate, compare.</summary>
        public void Step()
        {
            List<TasFrameLine> candidate = NextAttempt();
            if (candidate == null)
                return;
            CompleteAttempt(_runner(CloneAll(candidate), Config.StartTick, Config.EndTick));
        }

        /// <summary>Consumes the samples of the pending attempt (baseline or candidate).</summary>
        public void CompleteAttempt(List<OptimSample> samples)
        {
            List<TasFrameLine> candidate = _pendingCandidate;
            bool isBaseline = _pendingIsBaseline;
            _pendingCandidate = null;
            if (candidate == null)
                return;

            if (isBaseline)
            {
                double baseline;
                OptimOutcomeKind kind = Evaluate(samples, out baseline);
                BaselineScore = baseline;
                LastOutcome = kind;
                if (kind == OptimOutcomeKind.NoSimulation)
                {
                    AddLog("Baseline could not be simulated; optimizer not started.");
                    Running = false;
                    return;
                }
                // A baseline that breaks a constraint still gives a starting score, so the search can
                // climb back to validity.
                if (kind != OptimOutcomeKind.Valid)
                    AddLog("Baseline is invalid (" + kind + "); searching from it anyway.");
                BaselineSamples = samples;
                BestScore = BaselineScore;
                BestSamples = samples;
                Running = true;
                AddLog("Baseline score: " + BaselineScore.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            LastCandidateSamples = samples;
            double score;
            OptimOutcomeKind outcome = Evaluate(samples, out score);
            LastOutcome = outcome;
            if (outcome != OptimOutcomeKind.Valid)
            {
                InvalidCount++;
                return;
            }

            if (IsBetter(score, BestScore))
            {
                double delta = score - BestScore;
                BestScore = score;
                ReplaceSegment(_best, candidate);
                BestSamples = samples;
                RememberTop(score, candidate);
                AddLog("Found new best value: " + score.ToString("0.####", CultureInfo.InvariantCulture) +
                       " (delta: " + delta.ToString("+0.####;-0.####", CultureInfo.InvariantCulture) + ")");
            }
            else if (Config.SimulatedAnnealing && AcceptWorse(score - BestScore))
            {
                // Annealing lets the search walk away from the current best. Keep it as the working
                // point without claiming a new best.
                ReplaceSegment(_best, candidate);
                BestScore = score;
            }
        }

        // ---- Candidate generation -------------------------------------------------------------

        List<TasFrameLine> MakeCandidate()
        {
            OptimMutationSettings m = Config.Mutations;
            float roll = (float)_rng.NextDouble();
            List<TasFrameLine> source;
            if (roll < m.RestartChance)
                source = _baseline;
            else if (roll < m.RestartChance + m.TopFiveChance && _top.Count > 0)
                source = _top[_rng.Next(_top.Count)].Value;
            else
                source = _best;

            List<TasFrameLine> candidate = CloneAll(source);
            int bulks = Math.Max(1, m.BulksPerIteration);
            for (int i = 0; i < bulks; i++)
            {
                int index = PickFramebulk(candidate);
                if (index < 0) break;
                MutateLine(candidate, index);
            }
            return candidate;
        }

        int PickFramebulk(List<TasFrameLine> segment)
        {
            List<int> indices = new List<int>();
            for (int i = 0; i < segment.Count; i++)
                if (segment[i].IsFramebulk)
                    indices.Add(i);
            return indices.Count == 0 ? -1 : indices[_rng.Next(indices.Count)];
        }

        void MutateLine(List<TasFrameLine> segment, int index)
        {
            OptimMutationSettings m = Config.Mutations;
            TasFrameLine line = segment[index];

            List<Action<TasFrameLine>> options = new List<Action<TasFrameLine>>();
            if (m.MutateYaw && !line.Yaw.Unchanged)
                options.Add(l => l.Yaw = Nudge(l.Yaw, m.YawStep));
            if (m.MutatePitch && !line.Pitch.Unchanged)
                options.Add(l => l.Pitch = Nudge(l.Pitch, m.PitchStep));
            if (m.MutateFramecount)
                options.Add(l => l.Count = Math.Max(1, l.Count + SignedStep(m.FramecountStep)));
            if (m.MutateMovement)
                options.Add(l => l.Buttons ^= RandomFlag(TasButtons.Forward, TasButtons.Back, TasButtons.Left, TasButtons.Right));
            if (m.MutateDashSlide)
                options.Add(l => l.Buttons ^= RandomFlag(TasButtons.Dash, TasButtons.Slide));
            if (m.MutateJump)
                options.Add(l => l.Buttons ^= TasButtons.Jump);
            if (m.MutateCombat)
                options.Add(l => l.Buttons ^= RandomFlag(TasButtons.Fire1, TasButtons.Fire2, TasButtons.Punch, TasButtons.Whiplash));
            if (m.MutateWeapons)
                options.Add(l => l.Buttons ^= RandomFlag(TasButtons.Slot1, TasButtons.Slot2, TasButtons.Slot3,
                                                          TasButtons.Slot4, TasButtons.Slot5, TasButtons.Slot6));
            if (options.Count == 0) return;

            int oldCount = line.Count;
            options[_rng.Next(options.Count)](line);

            // Framecount changes can keep the segment length fixed by borrowing from another bulk.
            if (line.Count != oldCount && m.KeepTotalTicks)
                CompensateTotal(segment, index, oldCount);
        }

        /// <summary>Count change is compensated on another framebulk when total length must stay fixed.</summary>
        public static void CompensateTotal(List<TasFrameLine> segment, int changedIndex, int oldCount)
        {
            int delta = segment[changedIndex].Count - oldCount;
            if (delta == 0) return;
            for (int i = 0; i < segment.Count; i++)
            {
                if (i == changedIndex || !segment[i].IsFramebulk) continue;
                int c = segment[i].Count - delta;
                if (c >= 1)
                {
                    segment[i].Count = c;
                    return;
                }
            }
            // Nothing could absorb the change, so revert it.
            segment[changedIndex].Count = oldCount;
        }

        AngleToken Nudge(AngleToken angle, float maxStep)
        {
            double step = (_rng.NextDouble() * 2.0 - 1.0) * maxStep;
            if (step > -0.01 && step < 0.01) step = step < 0 ? -0.01 : 0.01;
            AngleToken copy = angle.Clone();
            copy.Value = copy.Value + step;
            return copy;
        }

        int SignedStep(int max)
        {
            int s = 1 + _rng.Next(Math.Max(1, max));
            return _rng.Next(2) == 0 ? -s : s;
        }

        TasButtons RandomFlag(params TasButtons[] flags)
        {
            return flags[_rng.Next(flags.Length)];
        }

        bool AcceptWorse(double delta)
        {
            if (Config.Temperature <= 0) return false;
            double p = Math.Exp(delta / Config.Temperature);
            return _rng.NextDouble() < p;
        }

        // ---- Evaluation -----------------------------------------------------------------------

        public OptimOutcomeKind Evaluate(List<OptimSample> samples, out double score)
        {
            score = double.NegativeInfinity;
            if (samples == null || samples.Count == 0)
                return OptimOutcomeKind.NoSimulation;

            for (int i = 0; i < samples.Count; i++)
                if (samples[i].Dead)
                    return OptimOutcomeKind.InvalidDeath;

            // Keep-out boxes: any touch invalidates the attempt.
            foreach (OptimBox box in Boxes.Enabled(OptimBoxKind.KeepOut))
                if (TouchesAny(box, samples))
                    return OptimOutcomeKind.InvalidKeepOut;

            // Waypoints: every enabled yellow box must be touched at least once.
            foreach (OptimBox box in Boxes.Enabled(OptimBoxKind.Waypoint))
                if (!TouchesAny(box, samples))
                    return OptimOutcomeKind.InvalidWaypoint;

            // Numeric constraints.
            for (int c = 0; c < Config.Constraints.Count; c++)
                if (!ConstraintHolds(Config.Constraints[c], samples))
                    return OptimOutcomeKind.InvalidConstraint;

            score = Objective(samples);
            return OptimOutcomeKind.Valid;
        }

        bool TouchesAny(OptimBox box, List<OptimSample> samples)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                if (i == 0)
                {
                    if (box.Contains(samples[0].Position)) return true;
                    continue;
                }
                float fraction;
                if (OptimBoxSet.Touches(box, samples[i - 1].Position, samples[i].Position, false, out fraction))
                    return true;
            }
            return false;
        }

        bool ConstraintHolds(OptimConstraint c, List<OptimSample> samples)
        {
            if (c.Scope == OptimScope.LastFrame)
                return Compare(Value(c.Variable, samples[samples.Count - 1]), c);

            for (int i = 0; i < samples.Count; i++)
                if (!Compare(Value(c.Variable, samples[i]), c))
                    return false;
            return true;
        }

        static bool Compare(double v, OptimConstraint c)
        {
            switch (c.Op)
            {
                case OptimOperator.Less: return v < c.Value;
                case OptimOperator.LessEq: return v <= c.Value;
                case OptimOperator.Greater: return v > c.Value;
                default: return v >= c.Value;
            }
        }

        double Objective(List<OptimSample> samples)
        {
            OptimConfig cfg = Config;
            if (cfg.Objective == OptimObjective.ReachGoalFastest)
                return ReachGoalScore(samples);

            double raw;
            if (cfg.Objective == OptimObjective.DistanceToGoal)
            {
                raw = double.PositiveInfinity;
                foreach (OptimBox goal in Boxes.Enabled(OptimBoxKind.Goal))
                    for (int i = 0; i < samples.Count; i++)
                        raw = Math.Min(raw, goal.DistanceTo(samples[i].Position));
                if (double.IsPositiveInfinity(raw)) raw = 0;
                // Distance is always minimised, whatever the dropdown says.
                return -raw;
            }

            if (cfg.Scope == OptimScope.LastFrame)
            {
                raw = Value(cfg.Objective, samples[samples.Count - 1]);
            }
            else
            {
                raw = double.NegativeInfinity;
                for (int i = 0; i < samples.Count; i++)
                    raw = Math.Max(raw, Value(cfg.Objective, samples[i]));
            }
            return cfg.Direction == OptimDirection.Maximize ? raw : -raw;
        }

        /// <summary>
        /// Earliest sub-tick entry into the first enabled goal box wins. Ties go to the higher speed
        /// at entry. If the box is never reached, the score is a gradient toward it (closest distance).
        /// </summary>
        double ReachGoalScore(List<OptimSample> samples)
        {
            double bestTime = double.PositiveInfinity;
            double speedAtEntry = 0;
            bool reached = false;
            foreach (OptimBox goal in Boxes.Enabled(OptimBoxKind.Goal))
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    float fraction = 0f;
                    bool hit;
                    if (i == 0)
                    {
                        hit = goal.Contains(samples[0].Position);
                    }
                    else
                    {
                        hit = OptimBoxSet.Touches(goal, samples[i - 1].Position, samples[i].Position, false, out fraction);
                    }
                    if (!hit) continue;
                    double t = samples[Math.Max(0, i - 1)].Tick + (i == 0 ? 0 : fraction);
                    if (t < bestTime)
                    {
                        bestTime = t;
                        speedAtEntry = samples[i].Velocity.magnitude;
                        reached = true;
                    }
                    break;
                }
            }
            if (reached)
                return 1e6 - bestTime * 1000.0 + speedAtEntry * 0.001;

            double dist = double.PositiveInfinity;
            foreach (OptimBox goal in Boxes.Enabled(OptimBoxKind.Goal))
                for (int i = 0; i < samples.Count; i++)
                    dist = Math.Min(dist, goal.DistanceTo(samples[i].Position));
            if (double.IsPositiveInfinity(dist)) dist = 0;
            return -dist;
        }

        public static double Value(OptimObjective v, OptimSample s)
        {
            switch (v)
            {
                case OptimObjective.PosX: return s.Position.x;
                case OptimObjective.PosY: return s.Position.y;
                case OptimObjective.PosZ: return s.Position.z;
                case OptimObjective.VelX: return s.Velocity.x;
                case OptimObjective.VelY: return s.Velocity.y;
                case OptimObjective.VelZ: return s.Velocity.z;
                case OptimObjective.SpeedXZ: return Math.Sqrt(s.Velocity.x * s.Velocity.x + s.Velocity.z * s.Velocity.z);
                case OptimObjective.Speed3D: return s.Velocity.magnitude;
                default: return 0;
            }
        }

        public static string VariableName(OptimObjective v)
        {
            switch (v)
            {
                case OptimObjective.PosX: return "pos.x";
                case OptimObjective.PosY: return "pos.y";
                case OptimObjective.PosZ: return "pos.z";
                case OptimObjective.VelX: return "vel.x";
                case OptimObjective.VelY: return "vel.y";
                case OptimObjective.VelZ: return "vel.z";
                case OptimObjective.SpeedXZ: return "speed_xz";
                case OptimObjective.Speed3D: return "speed_3d";
                case OptimObjective.ReachGoalFastest: return "reach_goal";
                case OptimObjective.DistanceToGoal: return "distance_to_goal";
                default: return v.ToString();
            }
        }

        public bool IsBetter(double candidate, double best)
        {
            return candidate > best + 1e-9;
        }

        // ---- Bookkeeping ----------------------------------------------------------------------

        void RememberTop(double score, List<TasFrameLine> segment)
        {
            _top.Add(new KeyValuePair<double, List<TasFrameLine>>(score, CloneAll(segment)));
            _top.Sort((a, b) => b.Key.CompareTo(a.Key));
            if (_top.Count > TopCapacity)
                _top.RemoveRange(TopCapacity, _top.Count - TopCapacity);
        }

        void AddLog(string line)
        {
            Log.Add(line);
            if (Log.Count > LogCapacity)
                Log.RemoveAt(0);
        }

        static List<TasFrameLine> CloneAll(List<TasFrameLine> source)
        {
            List<TasFrameLine> copy = new List<TasFrameLine>(source.Count);
            for (int i = 0; i < source.Count; i++)
                copy.Add(source[i].Clone());
            return copy;
        }

        static void ReplaceSegment(List<TasFrameLine> target, List<TasFrameLine> source)
        {
            target.Clear();
            target.AddRange(CloneAll(source));
        }

        /// <summary>
        /// Finds the in-script markers "optim_start" / "optim_end" (or "bxt_tas_optim_init") in the
        /// command field of framebulks. Returns false when the pair is missing or out of order.
        /// </summary>
        public static bool FindMarkers(TasScript script, out int startIndex, out int endIndex)
        {
            startIndex = -1;
            endIndex = -1;
            for (int i = 0; i < script.Lines.Count; i++)
            {
                List<string> commands = script.Lines[i].Commands;
                for (int c = 0; c < commands.Count; c++)
                {
                    string cmd = commands[c].Trim().ToLowerInvariant();
                    if (startIndex < 0 && (cmd == "optim_start" || cmd == "bxt_tas_optim_init"))
                        startIndex = i;
                    else if (cmd == "optim_end" && startIndex >= 0)
                        endIndex = i;
                }
            }
            return startIndex >= 0 && endIndex > startIndex;
        }

        /// <summary>
        /// Resolves optim_start / optim_end markers to absolute ticks: start is the first tick of the
        /// marked framebulk, end is the first tick after the optim_end framebulk's start (exclusive).
        /// </summary>
        public static bool ResolveMarkerTicks(TasScript script, out int startTick, out int endTick)
        {
            startTick = -1;
            endTick = -1;
            int startIndex, endIndex;
            if (!FindMarkers(script, out startIndex, out endIndex))
                return false;
            startTick = script.LineStartTick(startIndex);
            endTick = script.LineStartTick(endIndex);
            return startTick >= 0 && endTick > startTick;
        }

        // ---- Projectile / coin aim lock -------------------------------------------------------

        /// <summary>
        /// Yaw and pitch (degrees) that point from eye toward target. Unity convention: pitch is
        /// positive when looking down. ASSUMPTION: matches the TAS pitch/yaw fields; verify in game.
        /// </summary>
        public static void AimAngles(Vector3 eye, Vector3 target, float jitterDegrees, System.Random rng,
                                     out double yaw, out double pitch)
        {
            Vector3 dir = target - eye;
            if (dir.sqrMagnitude < 1e-9f)
                dir = Vector3.forward;
            Vector3 euler = Quaternion.LookRotation(dir.normalized).eulerAngles;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            yaw = euler.y;
            if (jitterDegrees > 0f && rng != null)
            {
                yaw += (rng.NextDouble() * 2.0 - 1.0) * jitterDegrees;
                pitch += (rng.NextDouble() * 2.0 - 1.0) * jitterDegrees;
            }
        }

        /// <summary>
        /// Overwrites a candidate framebulk's absolute yaw and pitch so the shot points at target.
        /// Used on the firing tick of a tracked projectile or coin.
        /// </summary>
        public static void LockFrameToTarget(TasFrameLine line, Vector3 eye, Vector3 target, float jitterDegrees, System.Random rng)
        {
            double yaw;
            double pitch;
            AimAngles(eye, target, jitterDegrees, rng, out yaw, out pitch);
            line.Yaw = AngleToken.Absolute(yaw);
            line.Pitch = AngleToken.Absolute(pitch);
        }

        // ---- Segment splicing / save ----------------------------------------------------------

        /// <summary>
        /// Replaces the framebulks covering [startTick, endTick) in script with a clone of segment.
        /// Both ticks must sit on framebulk line boundaries. Returns false with error set otherwise.
        /// </summary>
        public static bool SpliceSegment(TasScript script, int startTick, int endTick, IList<TasFrameLine> segment, out string error)
        {
            error = null;
            if (script == null || segment == null) { error = "No script or segment."; return false; }
            TickLocation a = script.Locate(startTick);
            TickLocation b = script.Locate(endTick);
            if (!a.Valid || !b.Valid) { error = "Start or end tick is outside the script."; return false; }
            if (a.TickWithinLine != 0 || b.TickWithinLine != 0)
            {
                error = "Start and end must fall on framebulk boundaries. Split the bulk first.";
                return false;
            }
            int removeCount = b.LineIndex - a.LineIndex;
            if (removeCount <= 0) { error = "Segment is empty."; return false; }

            int newTicks = 0;
            List<TasFrameLine> clones = new List<TasFrameLine>(segment.Count);
            for (int i = 0; i < segment.Count; i++)
            {
                clones.Add(segment[i].Clone());
                if (segment[i].IsFramebulk) newTicks += segment[i].Count;
            }
            if (newTicks != endTick - startTick)
            {
                error = "Winning segment has " + newTicks + " ticks, original has " + (endTick - startTick) +
                        ". Splicing would shift later ticks.";
                return false;
            }

            script.Lines.RemoveRange(a.LineIndex, removeCount);
            script.Lines.InsertRange(a.LineIndex, clones);
            script.InvalidateTimeline();
            return true;
        }

        /// <summary>Writes segment as a standalone .uktas file with the script header copied over.</summary>
        public static void SaveSegment(TasScript script, IList<TasFrameLine> segment, string path)
        {
            TasScript output = new TasScript();
            output.Properties = script != null ? script.Properties : new TasProperties();
            for (int i = 0; i < segment.Count; i++)
                output.Lines.Add(segment[i].Clone());
            output.Save(path);
        }

        /// <summary>HUD line: camera forward resolved to the dominant world axis, plus origin.</summary>
        public static string FacingText(Vector3 forward, Vector3 origin, float yaw, float pitch)
        {
            if (forward.sqrMagnitude < 1e-9f) forward = Vector3.forward;
            forward.Normalize();

            float ax = Mathf.Abs(forward.x), ay = Mathf.Abs(forward.y), az = Mathf.Abs(forward.z);
            string primary = AxisName(forward, ax, ay, az, out float pv, exclude: -1);
            int primaryAxis = AxisIndex(ax, ay, az, -1);
            string secondary = AxisName(forward, ax, ay, az, out float sv, primaryAxis);

            return "Facing: " + primary + " (" + pv.ToString("0.00", CultureInfo.InvariantCulture) + ")" +
                   " | Secondary: " + secondary + " (" + sv.ToString("0.00", CultureInfo.InvariantCulture) + ")" +
                   " | Yaw: " + yaw.ToString("0.0", CultureInfo.InvariantCulture) +
                   " Pitch: " + pitch.ToString("0.0", CultureInfo.InvariantCulture) +
                   " | Origin: " + origin.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                   origin.y.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                   origin.z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        static int AxisIndex(float ax, float ay, float az, int exclude)
        {
            int best = -1;
            float bv = -1f;
            if (exclude != 0 && ax > bv) { bv = ax; best = 0; }
            if (exclude != 1 && ay > bv) { bv = ay; best = 1; }
            if (exclude != 2 && az > bv) { bv = az; best = 2; }
            return best;
        }

        static string AxisName(Vector3 f, float ax, float ay, float az, out float value, int exclude)
        {
            int i = AxisIndex(ax, ay, az, exclude);
            value = i == 0 ? ax : i == 1 ? ay : az;
            float signed = i == 0 ? f.x : i == 1 ? f.y : f.z;
            string letter = i == 0 ? "X" : i == 1 ? "Y" : "Z";
            return (signed < 0 ? "-" : "+") + letter;
        }
    }
}
