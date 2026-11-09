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
        DistanceToGoal,
        /// <summary>Total quality of Super Slide Jumps found in the segment (1.0 + 0.5 + 0.25 per SSJ).</summary>
        SSJ,
        /// <summary>Player health (NewMovement.hp) on the evaluated tick.</summary>
        Health,
        /// <summary>Hard damage (NewMovement.antiHp) on the evaluated tick; lower is better.</summary>
        HardDamage,
        /// <summary>Health gained between the first sampled tick and the evaluated tick.</summary>
        HealthDelta
    }

    public enum OptimDirection { Maximize, Minimize }

    /// <summary>Which component of speed the optimizer should favour.</summary>
    public enum OptimSpeedPriority { Balanced, Horizontal, Vertical }

    /// <summary>Evaluate a constraint or objective on the last tick of the segment, or on any tick.</summary>
    public enum OptimScope { LastFrame, AllFrames }

    public enum OptimOperator { Less, LessEq, Greater, GreaterEq }

    /// <summary>How candidates are generated: a deterministic sweep, random mutation, or both.</summary>
    public enum OptimSearchMode
    {
        /// <summary>Coordinate sweep, with a burst of random candidates whenever a pass finds nothing.</summary>
        Hybrid,
        /// <summary>Coordinate sweep only: repeatable, best for polishing a route.</summary>
        Sweep,
        /// <summary>Random mutation only (the previous behaviour).</summary>
        Random,
        /// <summary>
        /// Adaptive evolutionary search: a UCB1 bandit picks the mutation operator from the
        /// rewards it has learned, and an elite population breeds by temporal crossover.
        /// Elite consensus locks agreed inputs, differential projection follows the pool's
        /// trend, mined input rhythms are spliced into flat spans, and stagnation detection
        /// thaws the locks and pivots to a divergent route when the search stops improving.
        /// </summary>
        Adaptive
    }

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
        /// <summary>Horizontal speed (velocity XZ) on this tick, u/s.</summary>
        public float SpeedXZ;
        /// <summary>Change in horizontal speed since the previous sampled tick, u/s per tick.</summary>
        public float AccelXZ;
        public bool Grounded;
        public bool Sliding;
        public bool Dashing;
        public bool Jumping;
        /// <summary>True on the first sampled tick where NewMovement.jumping became true.</summary>
        public bool Jumped;
        /// <summary>False when this game build does not expose NewMovement.framesSinceSlide.</summary>
        public bool SlideRecoveryKnown;
        public int FramesSinceSlide;
        public int SSJMaxFrames;
        /// <summary>True when this tick performed an SSJ: a grounded jump 1-3 fixed frames after StopSlide.</summary>
        public bool SSJ;
        /// <summary>1 / 2^(framesSinceSlide - 1): 1.0, 0.5 or 0.25.</summary>
        public float SSJQuality;
        public float Health;
        /// <summary>Hard damage (NewMovement.antiHp): unhealable damage on top of hp; lower is better.</summary>
        public float HardDamage;
        public bool Dead;
        /// <summary>Buttons the candidate held on this tick, for geometry-aware bonk detection.</summary>
        public TasButtons Buttons;
        /// <summary>True when a Slide press this tick would start a ground slam.</summary>
        public bool SlamReady;
        /// <summary>True while GroundCheck.heavyFall is active (a slam is running).</summary>
        public bool Slamming;
        /// <summary>True while Jump()'s 0.2 s cooldown would swallow another jump press.</summary>
        public bool JumpCooldown;
        /// <summary>NewMovement.boostCharge: 100 is one full dash bar.</summary>
        public float BoostCharge;
        /// <summary>
        /// Ticks since the sample where NewMovement.jumping last turned on, or -1 when no jump
        /// was seen since the attempt began. The game only accepts an air slide within 0.06 s
        /// (about three fixed ticks) of a jump, so a Slide press further out would not slide.
        /// </summary>
        public int TicksSinceJump;
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
        public bool MutateDash = true;        // d in field 2
        public bool MutateSlide = true;       // s in field 2
        public bool MutateJump = true;        // j in field 3
        public bool MutateSSJ = true;         // split a 3+ tick bulk into slide / wait / jump
        public bool MutateJumpSlide = true;   // grounded jump, then a Slide on the next tick (Dive)
        // Every field-1 autofunc has its own switch. Defaults preserve the old group
        // behaviour (j s d g c w m on, coin throw / deadcoin off).
        public bool MutateAutoJump = true;    // j
        public bool MutateAutoSlide = true;   // s / S
        public bool MutateAutoDash = true;    // d
        public bool MutateGroundSlam = true;  // g
        public bool MutateSSJMacro = true;    // m
        public bool MutateCoinAim = true;     // c
        public bool MutateWhipAssist = true;  // w
        public bool MutateCoinThrow = false;  // t
        public bool MutateDeadcoin = false;   // e
        public bool MutateSingleTick = false; // split a bulk and change one isolated tick
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
        /// <summary>Last tick candidates may change. EndTick remains the evaluation tick.</summary>
        public int MutateEndTick;
        public OptimObjective Objective = OptimObjective.ReachGoalFastest;
        public OptimDirection Direction = OptimDirection.Maximize;
        /// <summary>
        /// Axis preference for Speed 3D scoring, reach-goal entry ties, adaptive credit and
        /// geometry actions. Speed XZ and numeric constraints still use their raw values.
        /// </summary>
        public OptimSpeedPriority SpeedPriority = OptimSpeedPriority.Balanced;
        public OptimScope Scope = OptimScope.LastFrame;
        public bool ProjectileAwareness = true;
        /// <summary>Probe static geometry after a wall bonk and bias the next candidates around it.</summary>
        public bool GeometryAwareness = true;
        /// <summary>Chance (0.1..1) that a post-bonk candidate uses clearance guidance instead of the normal roll.</summary>
        public float GeometryGuidanceBias = 0.7f;
        /// <summary>Draw the last attempt's whisker fan in the 3D view.</summary>
        public bool GeometryShowWhiskers = false;
        public bool UseFastForwardHeadless = false;
        public bool RollbackFullReplay = false;   // false: restore master savestate at Start; true: replay from tick 0 headless
        public bool SimulatedAnnealing = false;
        public double Temperature = 0.1;      // accept worse candidates with exp(-delta / T) when annealing
        /// <summary>Candidate generator. Default: sweep first, random kicks when a pass stalls.</summary>
        public OptimSearchMode SearchMode = OptimSearchMode.Hybrid;
        /// <summary>
        /// Adaptive ML generations without a new best before it reloads the current best and
        /// mutates from there. 1 reloads on every non-improving generation.
        /// </summary>
        public int AdaptiveRouteRetryLimit = 4;
        /// <summary>Accept same-score candidates that use less input (bxt-rs "minimize").</summary>
        public bool TrimRedundantInputs = true;
        /// <summary>
        /// Spend the attempts on Super Slide Jumps: every candidate is the slide / release / jump
        /// pattern on a bulk the sampled frames accept, before any other change is tried. The
        /// score still decides whether the candidate is kept.
        /// </summary>
        public bool ForceSSJ;
        // Effective camera angles at the start tick. A '-' yaw/pitch column inherits these
        // unless an earlier absolute column in the segment gives a better anchor.
        public double YawOrigin;
        public double PitchOrigin;
        public readonly List<OptimConstraint> Constraints = new List<OptimConstraint>();
        public readonly OptimMutationSettings Mutations = new OptimMutationSettings();
    }

    public enum OptimOutcomeKind { Valid, InvalidConstraint, InvalidKeepOut, InvalidWaypoint, InvalidDeath, NoSimulation }

    public sealed partial class TasOptimizer
    {
        public const int LogCapacity = 10;
        public const int TopCapacity = 5;

        public readonly OptimConfig Config;
        public readonly OptimBoxSet Boxes;
        public readonly List<string> Log = new List<string>(LogCapacity);

        readonly List<TasFrameLine> _baseline;
        readonly List<TasFrameLine> _best;
        // Random mode (and annealing) explore from here; the best route is never given up.
        readonly List<TasFrameLine> _working;
        // Attempts since the last improvement, used for the random-mode restart.
        int _sinceImprovement;
        // Best routes kept with the samples that produced them, so a mutation from a top entry
        // is still gated on the movement state that route actually had.
        sealed class TopEntry
        {
            public double Score;
            public List<TasFrameLine> Segment;
            public List<OptimSample> Samples;
        }
        readonly List<TopEntry> _top = new List<TopEntry>();
        readonly System.Random _rng;
        readonly OptimRunner _runner;
        // Internal copies used only to gate actions. ClearPaths() may drop the public overlay
        // samples, but these stay so the next candidate still knows where V1 was grounded.
        List<OptimSample> _baselineActionSamples;
        List<OptimSample> _bestActionSamples;
        List<OptimSample> _workingActionSamples;
        List<OptimSample> _geometryActionSamples;

        public double BaselineScore { get; private set; }
        public double BestScore { get; private set; }
        /// <summary>Score of the point random mode mutates from. Only annealing moves it, and it may be worse.</summary>
        public double WorkingScore { get; private set; }
        public int Iterations { get; private set; }
        public int InvalidCount { get; private set; }
        public OptimOutcomeKind LastOutcome { get; private set; }
        public List<OptimSample> LastCandidateSamples { get; private set; }
        public List<OptimSample> BestSamples { get; private set; }
        public List<OptimSample> BaselineSamples { get; private set; }
        public int BestSSJCount { get; private set; }
        public float BestSSJQuality { get; private set; }
        public bool Running { get; private set; }

        public IList<TasFrameLine> BestSegment { get { return _best; } }

        /// <summary>
        /// End of the mutation window. Candidates only differ from the baseline up to this tick;
        /// playback and scoring continue to <see cref="OptimConfig.EndTick"/>.
        /// </summary>
        public int EffectiveMutationEndTick
        {
            get
            {
                int end = Config.MutateEndTick;
                if (end <= Config.StartTick || end > Config.EndTick)
                    return Config.EndTick;
                return end;
            }
        }

        /// <summary>Ticks from StartTick to the end of the mutation window.</summary>
        public int MutationWindowTicks { get { return EffectiveMutationEndTick - Config.StartTick; } }

        /// <summary>
        /// Restricts a generated candidate to StartTick..MutateEndTick by keeping its mutated
        /// prefix and splicing the untouched baseline tail back on. The generator still works on
        /// the whole segment; this is the single place the mutation horizon is enforced.
        /// </summary>
        List<TasFrameLine> ApplyMutationWindow(List<TasFrameLine> candidate)
        {
            int total = Config.EndTick - Config.StartTick;
            int window = EffectiveMutationEndTick - Config.StartTick;
            if (candidate == null || _baseline == null || window <= 0 || window >= total)
                return candidate;
            if (SegmentTickCount(candidate) != total || SegmentTickCount(_baseline) != total)
                return candidate;

            List<TasFrameLine> head = SliceTicks(candidate, 0, window);
            // A framecount change that borrowed ticks from the discarded tail leaves the head
            // short or long; borrow the difference back inside the mutation window.
            int drift = SegmentTickCount(head) - window;
            if (drift != 0 && !AbsorbWindowDrift(head, drift))
                return candidate;
            head.AddRange(SliceTicks(_baseline, window, total));
            return head;
        }

        static bool AbsorbWindowDrift(List<TasFrameLine> head, int drift)
        {
            for (int i = head.Count - 1; i >= 0; i--)
            {
                TasFrameLine line = head[i];
                if (line == null || !line.IsFramebulk)
                    continue;
                if (line.Count - drift < 1)
                    continue;
                line.Count -= drift;
                return true;
            }
            return false;
        }

        /// <summary>Drops the recorded paths without touching the search, the best segment, or the scores.</summary>
        public void ClearPaths()
        {
            BaselineSamples = null;
            BestSamples = null;
            LastCandidateSamples = null;
        }

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
            _working = CloneAll(segment);
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
            _pendingOperator = -1;
        }

        /// <summary>Next candidate to simulate, or null when stopped. Counts one iteration.</summary>
        public List<TasFrameLine> NextAttempt()
        {
            if (!Running || _pendingCandidate != null)
                return _pendingCandidate;
            Iterations++;
            List<TasFrameLine> candidate = ApplyMutationWindow(MakeCandidate());
            if (Config.SearchMode == OptimSearchMode.Adaptive && candidate != null)
            {
                // The mutation window can turn a tail-only change back into the exact current
                // best, and an arm can keep reproducing a route that already scored nothing.
                // Do not spend another replay on it: rebuild from the best with a fresh shape.
                bool repeated = SameRoute(candidate, _best) ||
                                SameRoute(candidate, _baseline) ||
                                (_lastAttemptRoute != null && SameRoute(candidate, _lastAttemptRoute)) ||
                                WasRouteTried(candidate);
                if (repeated)
                {
                    _freshRouteBurst = true;
                    List<TasFrameLine> fresh = MakeFreshRouteCandidate();
                    _freshRouteBurst = false;
                    fresh = ApplyMutationWindow(fresh);
                    if (fresh != null && !SameRoute(fresh, _best) &&
                        !SameRoute(fresh, _baseline) && !WasRouteTried(fresh))
                    {
                        candidate = fresh;
                        PhaseText = "ML fresh route from best (gen " + Generation + ", pop " +
                                    _elite.Count + "/" + EliteCapacity + ")";
                    }
                }
                if (candidate != null)
                {
                    _lastAttemptRoute = candidate;
                    RememberRoute(candidate);
                }
            }
            _pendingCandidate = candidate;
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
            _pendingOperator = -1;
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
            {
                RecordOperatorOutcome(null, OptimOutcomeKind.NoSimulation, 0.0);
                return;
            }

            // The physics scene is still on the attempt's last tick here, so this can probe the
            // environment around the recorded impact without rewinding or touching the run.
            // A playable source keeps the smart candidate's framebulks tied to the attempt that
            // actually hit the wall.
            bool bonked = UpdateGeometryFromAttempt(samples);
            if (bonked && SegmentTickCount(candidate) == Config.EndTick - Config.StartTick)
            {
                _geometrySource = CloneAll(candidate);
                _geometryActionSamples = samples;
                // Adaptive ML pins its next generations to the crash window: the launch before
                // the impact is frozen and only the corner is retried.
                BeginFailureWindow(LastGeometry, samples);
            }

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
                _baselineActionSamples = samples;
                _bestActionSamples = samples;
                _workingActionSamples = samples;
                BestScore = BaselineScore;
                WorkingScore = BaselineScore;
                BestSamples = samples;
                ReplaceSegment(_working, _baseline);
                UpdateCreditAssignment(_best, samples);
                MeasureSSJ(samples, out int baselineSSJs, out float baselineSSJQuality);
                BestSSJCount = baselineSSJs;
                BestSSJQuality = baselineSSJQuality;
                // The untouched segment seeds the elite pool, so the first generations have
                // a parent whose trajectory the mutations can be gated on.
                if (kind == OptimOutcomeKind.Valid)
                    ConsiderElite(_baseline, BaselineScore, samples);
                _stagnantAttempts = 0;
                _pivotNoticeLeft = 0;
                _thawAttempts = 0;
                ResetAdaptiveRouteFocus();
                Running = true;
                AddLog("Baseline score: " + BaselineScore.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            LastCandidateSamples = samples;
            _stagnantAttempts++;
            double score;
            OptimOutcomeKind outcome = Evaluate(samples, out score);
            LastOutcome = outcome;
            RecordOperatorOutcome(samples, outcome, score);
            if (outcome != OptimOutcomeKind.Valid)
            {
                InvalidCount++;
                _sinceImprovement++;
                return;
            }

            _sinceImprovement++;
            ConsiderElite(candidate, score, samples);
            if (IsBetter(score, BestScore))
            {
                double delta = score - BestScore;
                BestScore = score;
                WorkingScore = score;
                ReplaceSegment(_best, candidate);
                ReplaceSegment(_working, candidate);
                BestSamples = samples;
                _bestActionSamples = samples;
                _workingActionSamples = samples;
                UpdateCreditAssignment(_best, samples);
                MeasureSSJ(samples, out int bestSSJs, out float bestSSJQuality);
                BestSSJCount = bestSSJs;
                BestSSJQuality = bestSSJQuality;
                RememberTop(score, candidate, samples);
                OnImproved();
                _stagnantAttempts = 0;
                _pivotNoticeLeft = 0;
                _thawAttempts = 0;
                ResetAdaptiveRouteFocus();
                // A new best that no longer bonks means the corner is solved; otherwise keep the
                // crash window armed for the remaining budget.
                if (!bonked)
                    ClearFailureWindow();
                AddLog("Found new best value: " + score.ToString("0.####", CultureInfo.InvariantCulture) +
                       " (delta: " + delta.ToString("+0.####;-0.####", CultureInfo.InvariantCulture) + ")");
                return;
            }

            // Same score using less input: keep the simpler route and carry on from it. This is
            // bxt-rs's "minimize" step folded into the search, and it also gives the search a way
            // across a flat stretch instead of stopping dead at the first plateau.
            if (Config.TrimRedundantInputs && SameScore(score, BestScore) && Complexity(candidate) < Complexity(_best))
            {
                ReplaceSegment(_best, candidate);
                ReplaceSegment(_working, candidate);
                BestSamples = samples;
                _bestActionSamples = samples;
                _workingActionSamples = samples;
                UpdateCreditAssignment(_best, samples);
                TrimmedCount++;
                OnTrimmed();
                AddLog("Trimmed redundant input; score unchanged at " +
                       score.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            if (Config.SimulatedAnnealing && score < WorkingScore - NeutralEpsilon &&
                AcceptWorse(score - WorkingScore))
            {
                // Annealing walks the working point away from the best route so the search can climb
                // a different slope. BestScore, the best segment and BestSamples are left alone:
                // they are what "Apply Best" writes.
                ReplaceSegment(_working, candidate);
                WorkingScore = score;
                _workingActionSamples = samples;
            }
        }

        // ---- Candidate generation -------------------------------------------------------------

        List<TasFrameLine> MakeCandidate()
        {
            // Only an adaptive generation sets this; clear it so another mode never credits an arm.
            _pendingOperator = -1;
            // After a detected wall bonk, spend a few attempts steering around the lip before
            // falling back to the normal generator. The bias is the exploration/exploitation
            // split: below it the smart candidate is used, above it the normal search explores.
            if (Config.GeometryAwareness && !_failureActive && _geometryCandidatesLeft > 0 &&
                LastGeometry != null && LastGeometry.HasImpact)
            {
                int left = _geometryCandidatesLeft;
                _geometryCandidatesLeft = left - 1;
                if (_rng.NextDouble() < Mathf.Clamp01(Config.GeometryGuidanceBias))
                {
                    List<TasFrameLine> guided = MakeGeometryCandidate(LastGeometry);
                    if (guided != null)
                        return guided;
                }
                else if (Config.SearchMode != OptimSearchMode.Sweep)
                {
                    // Exploration half of the split: a pure random roll, so the search does not
                    // tunnel-vision on the same corner. Sweep-only mode keeps its determinism.
                    return Config.SearchMode == OptimSearchMode.Adaptive
                        ? MakeAdaptiveCandidate()
                        : MakeRandomCandidate();
                }
            }

            if (Config.SearchMode == OptimSearchMode.Random)
                return MakeRandomCandidate();
            if (Config.SearchMode == OptimSearchMode.Adaptive)
                return MakeAdaptiveCandidate();
            return MakeSweepCandidate();
        }

        List<TasFrameLine> MakeRandomCandidate()
        {
            OptimMutationSettings m = Config.Mutations;
            float roll = (float)_rng.NextDouble();
            List<TasFrameLine> source;
            // The state the source actually had, so the mutations are gated on real physics.
            List<OptimSample> sourceSamples;
            // A long run without an improvement means the working point is a flat local optimum,
            // so start over from the untouched segment instead of polishing it further.
            bool stagnant = _sinceImprovement >= StagnationLimit;
            if (stagnant || roll < m.RestartChance)
            {
                source = _baseline;
                sourceSamples = _baselineActionSamples;
                if (stagnant)
                    _sinceImprovement = 0;
            }
            else if (roll < m.RestartChance + m.TopFiveChance && _top.Count > 0)
            {
                TopEntry entry = _top[_rng.Next(_top.Count)];
                source = entry.Segment;
                sourceSamples = entry.Samples;
            }
            else
            {
                source = _working;
                sourceSamples = _workingActionSamples;
            }

            List<TasFrameLine> candidate = CloneAll(source);
            // Force-SSJ mode spends the attempt on the pattern itself instead of an ordinary
            // mutation, so the route accumulates SSJs before anything else is tuned.
            if (Config.ForceSSJ && TryForceSSJ(candidate, sourceSamples))
            {
                PhaseText = "force SSJ";
                return candidate;
            }
            int bulks = Math.Max(1, m.BulksPerIteration);
            for (int i = 0; i < bulks; i++)
            {
                int index = PickFramebulk(candidate);
                if (index < 0) break;
                MutateLine(candidate, index, sourceSamples);
            }
            // If every random option failed, force a visible input change on a window bulk
            // instead of handing the same route back to the simulation loop.
            if (SameRoute(candidate, source))
            {
                int index = PickFramebulk(candidate);
                if (index >= 0)
                    candidate[index].Buttons ^= TasButtons.Forward;
            }
            PhaseText = stagnant ? "random restart" : "random";
            return candidate;
        }

        int PickFramebulk(List<TasFrameLine> segment)
        {
            List<int> indices = new List<int>();
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && cursor < MutationWindowTicks)
                    indices.Add(i);
                cursor += ticks;
            }
            return indices.Count == 0 ? -1 : indices[_rng.Next(indices.Count)];
        }

        void MutateLine(List<TasFrameLine> segment, int index, List<OptimSample> samples)
        {
            OptimMutationSettings m = Config.Mutations;
            TasFrameLine line = segment[index];
            List<Func<bool>> options = new List<Func<bool>>();

            // Where this framebulk sits on the source trajectory. Every action that only works in
            // one movement state is gated on it, so a jump is never rolled on an airborne frame.
            ActionWindow window;
            bool windowKnown = TryActionWindow(segment, index, samples, out window);

            // A '-' column is allowed: it becomes an absolute angle anchored to the previous
            // absolute angle, or to the camera angle at the optimizer start tick.
            if (m.MutateYaw)
                options.Add(() =>
                {
                    line.Yaw = NudgeRandom(line.Yaw, m.YawStep, AngleAnchor(segment, index, true));
                    return true;
                });
            if (m.MutatePitch)
                options.Add(() =>
                {
                    line.Pitch = NudgeRandom(line.Pitch, m.PitchStep, AngleAnchor(segment, index, false));
                    return true;
                });
            if (m.MutateFramecount)
                options.Add(() => MutateFramecount(segment, index));
            if (m.MutateMovement)
                options.Add(() =>
                {
                    line.Buttons ^= RandomFlag(TasButtons.Forward, TasButtons.Back, TasButtons.Left, TasButtons.Right);
                    return true;
                });
            // Removing an input is always worth trying (it may be redundant), but adding one is
            // only offered when the game would actually accept the press on this framebulk.
            if (m.MutateDash)
                options.Add(() =>
                {
                    if ((line.Buttons & TasButtons.Dash) == 0 &&
                        !(windowKnown && IsDashReady(samples, window.Pre)))
                        return false;
                    line.Buttons ^= TasButtons.Dash;
                    return true;
                });
            if (m.MutateSlide)
                options.Add(() =>
                {
                    if ((line.Buttons & TasButtons.Slide) == 0 &&
                        !(windowKnown && IsSlideUseful(samples, window.Pre)))
                        return false;
                    line.Buttons ^= TasButtons.Slide;
                    return true;
                });
            if (m.MutateJump)
                options.Add(() =>
                {
                    if ((line.Buttons & TasButtons.Jump) == 0 &&
                        !(windowKnown && IsJumpReady(samples, window.Pre)))
                        return false;
                    line.Buttons ^= TasButtons.Jump;
                    return true;
                });
            if (AnyAutofuncEnabled())
                options.Add(() =>
                {
                    List<TasAutofuncs> choices = EligibleAutofuncs(line, samples, windowKnown, window);
                    if (choices.Count == 0)
                        return false;
                    line.Autofuncs ^= choices[_rng.Next(choices.Count)];
                    return true;
                });
            if (m.MutateAutoDash)
                options.Add(() =>
                {
                    if ((line.Autofuncs & TasAutofuncs.AutoDash) == 0 &&
                        !(windowKnown && WindowHasDash(samples, window)))
                        return false;
                    line.Autofuncs ^= TasAutofuncs.AutoDash;
                    return true;
                });
            if (m.MutateCombat)
                options.Add(() =>
                {
                    line.Buttons ^= RandomFlag(TasButtons.Fire1, TasButtons.Fire2, TasButtons.Punch, TasButtons.Whiplash);
                    return true;
                });
            if (m.MutateWeapons)
                options.Add(() =>
                {
                    line.Buttons ^= RandomFlag(TasButtons.Slot1, TasButtons.Slot2, TasButtons.Slot3,
                                               TasButtons.Slot4, TasButtons.Slot5, TasButtons.Slot6);
                    return true;
                });
            if (m.MutateSSJ)
                options.Add(() => TryMutateSSJ(segment, index, samples));
            if (m.MutateJumpSlide)
                options.Add(() => TryMutateJumpSlide(segment, index, samples));
            if (m.MutateSingleTick)
                options.Add(() => TryMutateSingleTickRandom(segment, index, samples));
            if (options.Count == 0)
                return;

            // Random order, first option that applies wins: a structural change that cannot
            // happen here (a bulk too short to split) then never costs the attempt.
            int[] order = ShuffledOrder(options.Count);
            for (int i = 0; i < order.Length; i++)
                if (options[order[i]]())
                    return;
        }

        /// <summary>
        /// Turn a framebulk of at least three ticks into the exact Super Slide Jump input pattern:
        /// one tick sliding, one tick with slide released, then jump. The three replacement
        /// framebulks keep the original tick count, so the segment length never changes.
        /// </summary>
        bool TryMutateSSJ(List<TasFrameLine> segment, int index, List<OptimSample> samples)
        {
            TasFrameLine source = segment[index];
            if (source == null || !source.IsFramebulk || source.Count < 3 || HasCommands(source))
                return false;

            // SSJ is a jump from the ground. The jump row is the third tick of the bulk, and its
            // pre-state is sample `offset` (sample k is the state after tick StartTick+1+k).
            ActionWindow window;
            if (!TryActionWindow(segment, index, samples, out window))
                return false;
            if (!samples[window.Pre].Grounded)
                return false;
            if (!IsJumpReady(samples, window.First))
                return false;

            // A dash can leave NewMovement.boost set, and TrySSJ refuses to fire while boost
            // is true, so the three rows must be a pure slide / release / jump pattern.
            TasAutofuncs autofuncs = source.Autofuncs &
                                      ~(TasAutofuncs.AutoSlideMask | TasAutofuncs.AutoJump |
                                        TasAutofuncs.AutoDash | TasAutofuncs.SSJMacroMask);
            TasFrameLine slide = source.Clone();
            slide.Count = 1;
            slide.Buttons = (slide.Buttons | TasButtons.Slide) & ~(TasButtons.Jump | TasButtons.Dash);
            slide.Autofuncs = autofuncs;

            TasFrameLine wait = source.Clone();
            wait.Count = 1;
            wait.Buttons &= ~(TasButtons.Slide | TasButtons.Jump | TasButtons.Dash);
            wait.Autofuncs = autofuncs;
            wait.Commands.Clear();

            TasFrameLine jump = source.Clone();
            jump.Count = source.Count - 2;
            jump.Buttons = (jump.Buttons | TasButtons.Jump) & ~(TasButtons.Slide | TasButtons.Dash);
            jump.Autofuncs = autofuncs;
            jump.Commands.Clear();

            segment[index] = slide;
            segment.Insert(index + 1, wait);
            segment.Insert(index + 2, jump);
            return true;
        }

        /// <summary>
        /// Force-SSJ mode: pick a framebulk the sampled state can turn into the slide / release /
        /// jump pattern and apply it. Bulks already driven by the SSJ macro are skipped, since the
        /// macro produces the pattern itself. False when no bulk in this source can host an SSJ.
        /// </summary>
        bool TryForceSSJ(List<TasFrameLine> segment, List<OptimSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return false;
            List<int> candidates = new List<int>();
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (cursor < MutationWindowTicks && line.IsFramebulk && line.Count >= 3 &&
                    !HasCommands(line) && (line.Autofuncs & TasAutofuncs.SSJMacro) == 0)
                    candidates.Add(i);
                cursor += ticks;
            }
            int[] order = ShuffledOrder(candidates.Count);
            for (int i = 0; i < order.Length; i++)
                if (TryMutateSSJ(segment, candidates[order[i]], samples))
                    return true;
            return false;
        }

        /// <summary>
        /// Splits a framebulk into a grounded Jump tick and, on the very next tick, a Slide press:
        /// a Dive. ULTRAKILL only starts a slide in the air when Slide is pressed within 0.06 s of
        /// a jump from the ground, so this pattern is the only structural way to create an air
        /// slide; a Slide on an unrelated airborne tick does nothing, or starts a slam instead.
        /// The two rows keep the original tick count, and the rest of the bulk keeps its inputs.
        /// </summary>
        bool TryMutateJumpSlide(List<TasFrameLine> segment, int index, List<OptimSample> samples)
        {
            TasFrameLine source = segment[index];
            if (source == null || !source.IsFramebulk || source.Count < 2 || HasCommands(source))
                return false;

            ActionWindow window;
            if (!TryActionWindow(segment, index, samples, out window))
                return false;
            // The jump tick is the first tick of the bulk, so its pre-state is window.Pre.
            if (!IsJumpReady(samples, window.Pre))
                return false;

            // The pattern has to be exact: no slide held on the jump tick (that would jump out of
            // a slide), and no dash, whose boost flag changes the jump. The movement autofuncs are
            // stripped from the two new ticks for the same reason; the untouched tail keeps them.
            TasAutofuncs autofuncs = source.Autofuncs &
                                      ~(TasAutofuncs.AutoJump | TasAutofuncs.AutoSlideMask |
                                        TasAutofuncs.AutoDash | TasAutofuncs.GroundSlam |
                                        TasAutofuncs.SSJMacroMask);

            TasFrameLine jump = source.Clone();
            jump.Count = 1;
            jump.Buttons = (jump.Buttons | TasButtons.Jump) & ~(TasButtons.Slide | TasButtons.Dash);
            jump.Autofuncs = autofuncs;

            TasFrameLine slide = source.Clone();
            slide.Count = 1;
            slide.Buttons = (slide.Buttons | TasButtons.Slide) & ~(TasButtons.Jump | TasButtons.Dash);
            slide.Autofuncs = autofuncs;
            slide.Commands.Clear();

            segment[index] = jump;
            segment.Insert(index + 1, slide);

            int tailTicks = source.Count - 2;
            if (tailTicks > 0)
            {
                TasFrameLine tail = source.Clone();
                tail.Count = tailTicks;
                segment.Insert(index + 2, tail);
            }
            return true;
        }

        // ---- Action gating ------------------------------------------------------------------

        /// <summary>
        /// Which part of a source trajectory a framebulk covers. Samples are recorded for ticks
        /// StartTick+1 .. EndTick, so sample k is the player state after tick StartTick+1+k and
        /// the state before segment tick t is sample t-2.
        /// </summary>
        struct ActionWindow
        {
            /// <summary>Ticks before the line's first tick, counted from the segment start.</summary>
            public int Offset;
            /// <summary>Ticks the line covers.</summary>
            public int Count;
            /// <summary>First sample index overlapping the line.</summary>
            public int First;
            /// <summary>Last sample index overlapping the line.</summary>
            public int Last;
            /// <summary>Closest sampled state before the line's first tick.</summary>
            public int Pre;
        }

        /// <summary>
        /// Maps a framebulk to the samples of the trajectory it came from. False when there are no
        /// samples to gate on, in which case action mutations are skipped rather than guessed.
        /// </summary>
        static bool TryActionWindow(List<TasFrameLine> segment, int lineIndex, List<OptimSample> samples,
                                    out ActionWindow window)
        {
            window = new ActionWindow();
            if (segment == null || samples == null || samples.Count == 0)
                return false;
            if (lineIndex < 0 || lineIndex >= segment.Count)
                return false;
            TasFrameLine line = segment[lineIndex];
            if (line == null || !line.IsFramebulk)
                return false;

            int offset = 0;
            for (int i = 0; i < lineIndex; i++)
                if (segment[i] != null)
                    offset += GeometryLineTicks(segment[i]);
            int count = Mathf.Max(1, GeometryLineTicks(line));

            window.Offset = offset;
            window.Count = count;
            window.First = Mathf.Clamp(offset, 0, samples.Count - 1);
            window.Last = Mathf.Clamp(offset + count - 1, 0, samples.Count - 1);
            // The first two bulks fall outside the sampled range (the state at StartTick is not
            // sampled); there the attempt's first sample stands in for the missing pre-state.
            window.Pre = Mathf.Clamp(offset - 2, 0, samples.Count - 1);
            return true;
        }

        /// <summary>
        /// A Jump press only works from the ground, and only when Jump()'s 0.2 s cooldown is not
        /// still swallowing presses. This is the rule the whole optimizer follows: no jump on a
        /// frame the game would ignore.
        /// </summary>
        static bool IsJumpReady(List<OptimSample> samples, int index)
        {
            OptimSample pre = samples[Mathf.Clamp(index, 0, samples.Count - 1)];
            return pre.Grounded && !pre.JumpCooldown;
        }

        /// <summary>TryDash needs a full stamina bar (boostCharge 100) and no dash already running.</summary>
        static bool IsDashReady(List<OptimSample> samples, int index)
        {
            OptimSample pre = samples[Mathf.Clamp(index, 0, samples.Count - 1)];
            return !pre.Dashing && pre.BoostCharge >= 100f;
        }

        /// <summary>
        /// A Slide press is only worth adding where the game can start one: on the ground, while a
        /// slam is running (held-slam control), as an intentional slam drop, or in the air within
        /// three ticks of a jump from the ground. That last case is the Dive: the game refuses an
        /// air slide at any other time.
        /// </summary>
        bool IsSlideUseful(List<OptimSample> samples, int index)
        {
            OptimSample pre = samples[Mathf.Clamp(index, 0, samples.Count - 1)];
            if (pre.Grounded || pre.Slamming)
                return true;
            if (pre.SlamReady)
                return SlamHelps();
            return pre.TicksSinceJump >= 0 && pre.TicksSinceJump <= AirSlideTicks;
        }

        /// <summary>0.06 s of air-slide grace at 60 fps, rounded down.</summary>
        const int AirSlideTicks = 3;

        /// <summary>
        /// True when a slam is worth using: it trades height and horizontal speed for a much
        /// faster fall, so only objectives that want to get down or get somewhere benefit.
        /// </summary>
        bool SlamHelps()
        {
            switch (Config.Objective)
            {
                case OptimObjective.PosY:
                case OptimObjective.VelY:
                    return Config.Direction == OptimDirection.Minimize;
                case OptimObjective.PosX:
                case OptimObjective.VelX:
                case OptimObjective.PosZ:
                case OptimObjective.VelZ:
                case OptimObjective.SpeedXZ:
                case OptimObjective.SSJ:
                case OptimObjective.Health:
                case OptimObjective.HardDamage:
                case OptimObjective.HealthDelta:
                    return false;
                case OptimObjective.Speed3D:
                    // A slam buys vertical speed, so it only helps when that axis is preferred.
                    return Config.SpeedPriority == OptimSpeedPriority.Vertical;
                default:
                    // ReachGoalFastest / DistanceToGoal: falling faster arrives sooner.
                    return true;
            }
        }

        static bool WindowHasGrounded(List<OptimSample> samples, ActionWindow window)
        {
            for (int i = window.First; i <= window.Last; i++)
                if (samples[i].Grounded)
                    return true;
            return false;
        }

        /// <summary>True when a Slide press on this bulk would start a slam (not already slamming).</summary>
        static bool WindowHasSlam(List<OptimSample> samples, ActionWindow window)
        {
            for (int i = window.First; i <= window.Last; i++)
                if (samples[i].SlamReady && !samples[i].Slamming)
                    return true;
            return false;
        }

        static bool WindowHasDash(List<OptimSample> samples, ActionWindow window)
        {
            for (int i = window.First; i <= window.Last; i++)
                if (!samples[i].Dashing && samples[i].BoostCharge >= 100f)
                    return true;
            return false;
        }

        bool AnyAutofuncEnabled()
        {
            OptimMutationSettings m = Config.Mutations;
            return m.MutateAutoJump || m.MutateAutoSlide || m.MutateGroundSlam ||
                   m.MutateSSJMacro || m.MutateCoinAim || m.MutateWhipAssist ||
                   m.MutateCoinThrow || m.MutateDeadcoin;
        }

        /// <summary>
        /// Autofuncs that can be toggled on this framebulk. Each field-1 autofunc has its own
        /// optimizer switch. An autofunc that is already set is always offered (removing it can
        /// trim redundant input); adding a movement autofunc needs a tick in the window where it
        /// would actually do something. AutoDash is handled by its own mutation switch.
        /// </summary>
        List<TasAutofuncs> EligibleAutofuncs(TasFrameLine line, List<OptimSample> samples,
                                             bool windowKnown, ActionWindow window)
        {
            List<TasAutofuncs> choices = new List<TasAutofuncs>(8);
            OptimMutationSettings m = Config.Mutations;
            bool grounded = windowKnown && WindowHasGrounded(samples, window);
            if (m.MutateAutoJump && (grounded || (line.Autofuncs & TasAutofuncs.AutoJump) != 0))
                choices.Add(TasAutofuncs.AutoJump);
            if (m.MutateAutoSlide &&
                (grounded || (line.Autofuncs & TasAutofuncs.AutoSlideMask) != 0))
                choices.Add((line.Autofuncs & TasAutofuncs.AutoSlideSpam) != 0
                    ? TasAutofuncs.AutoSlideSpam
                    : TasAutofuncs.AutoSlide);
            if (m.MutateGroundSlam &&
                ((line.Autofuncs & TasAutofuncs.GroundSlam) != 0 ||
                 (windowKnown && SlamHelps() && WindowHasSlam(samples, window))))
                choices.Add(TasAutofuncs.GroundSlam);
            // The SSJ macro starts a slide itself, so it only needs ground somewhere in the bulk;
            // once set it has to be removable on any framebulk.
            if (m.MutateSSJMacro && (grounded || (line.Autofuncs & TasAutofuncs.SSJMacro) != 0))
                choices.Add(TasAutofuncs.SSJMacro);
            if (m.MutateCoinAim)
                choices.Add(TasAutofuncs.CoinAim);
            if (m.MutateWhipAssist)
                choices.Add(TasAutofuncs.WhipAssist);
            if (m.MutateCoinThrow)
                choices.Add(TasAutofuncs.CoinThrow);
            if (m.MutateDeadcoin)
                choices.Add(TasAutofuncs.Deadcoin);
            return choices;
        }

        /// <summary>
        /// Effective value for a '-' angle column. The nearest earlier absolute column is the
        /// best in-script anchor; otherwise the camera angle captured at the start tick is used.
        /// </summary>
        double AngleAnchor(List<TasFrameLine> segment, int index, bool yaw)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                TasFrameLine prior = segment[i];
                if (prior == null || !prior.IsFramebulk)
                    continue;
                AngleToken token = yaw ? prior.Yaw : prior.Pitch;
                if (token != null && !token.Unchanged && !token.IsDelta)
                    return token.Value;
            }
            return yaw ? Config.YawOrigin : Config.PitchOrigin;
        }

        /// <summary>
        /// Moves the tick difference of a framecount change onto another framebulk so the segment
        /// keeps its length, and returns false when no bulk can absorb it. Bulks further from the
        /// one being optimized are preferred, the way BXT's optimizer splits a repeat: that leaves
        /// the frames around the change untouched.
        /// </summary>
        bool CompensateTotal(List<TasFrameLine> segment, int changedIndex, int oldCount)
        {
            int delta = segment[changedIndex].Count - oldCount;
            if (delta == 0) return true;
            // Bulks without commands first: changing their length cannot change what a command does.
            if (TryAbsorbTicks(segment, changedIndex, delta, false)) return true;
            return TryAbsorbTicks(segment, changedIndex, delta, true);
        }

        bool TryAbsorbTicks(List<TasFrameLine> segment, int changedIndex, int delta, bool allowCommands)
        {
            // Sweep mode takes the farthest bulk that can absorb the change, so a sweep run stays
            // repeatable. Random mode draws one, weighted toward farther bulks.
            int farthest = -1;
            int farthestWeight = -1;
            int totalWeight = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                if (i == changedIndex || segment[i] == null || !segment[i].IsFramebulk) continue;
                if (!allowCommands && HasCommands(segment[i])) continue;
                if (segment[i].Count - delta < 1) continue;
                int weight = Math.Abs(i - changedIndex) + 1;
                totalWeight += weight;
                if (weight > farthestWeight)
                {
                    farthestWeight = weight;
                    farthest = i;
                }
            }
            if (totalWeight == 0)
                return false;
            if (Config.SearchMode == OptimSearchMode.Sweep)
            {
                segment[farthest].Count -= delta;
                return true;
            }

            int pick = _rng.Next(totalWeight);
            for (int i = 0; i < segment.Count; i++)
            {
                if (i == changedIndex || segment[i] == null || !segment[i].IsFramebulk) continue;
                if (!allowCommands && HasCommands(segment[i])) continue;
                if (segment[i].Count - delta < 1) continue;
                int weight = Math.Abs(i - changedIndex) + 1;
                if (pick < weight)
                {
                    segment[i].Count -= delta;
                    return true;
                }
                pick -= weight;
            }
            return false;
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

        /// <summary>
        /// True when the attempt can no longer be valid: the player died, a keep-out box was
        /// touched, or a constraint that must hold on every frame is already broken. The runner
        /// ends such an attempt on the spot, which is where most of the throughput on long
        /// windows comes from. Only the newest sample is examined, so this stays cheap.
        /// </summary>
        public bool IsDoomed(List<OptimSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return false;
            int last = samples.Count - 1;
            OptimSample current = samples[last];
            if (current.Dead)
                return true;

            foreach (OptimBox box in Boxes.Enabled(OptimBoxKind.KeepOut))
            {
                if (last == 0)
                {
                    if (box.Contains(current.Position)) return true;
                    continue;
                }
                float fraction;
                if (OptimBoxSet.Touches(box, samples[last - 1].Position, current.Position, false, out fraction))
                    return true;
            }

            for (int i = 0; i < Config.Constraints.Count; i++)
            {
                OptimConstraint c = Config.Constraints[i];
                if (c.Scope != OptimScope.AllFrames) continue;
                if (!Compare(Value(c.Variable, current), c)) return true;
            }
            return false;
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

        /// <summary>Sum of SSJ qualities. A first-frame SSJ is worth 1, then 0.5, then 0.25.</summary>
        static double SSJScore(List<OptimSample> samples)
        {
            double score = 0.0;
            if (samples == null)
                return score;
            for (int i = 0; i < samples.Count; i++)
                if (samples[i].SSJ)
                    score += samples[i].SSJQuality;
            return score;
        }

        static void MeasureSSJ(List<OptimSample> samples, out int count, out float bestQuality)
        {
            count = 0;
            bestQuality = 0f;
            if (samples == null)
                return;
            for (int i = 0; i < samples.Count; i++)
            {
                if (!samples[i].SSJ)
                    continue;
                count++;
                if (samples[i].SSJQuality > bestQuality)
                    bestQuality = samples[i].SSJQuality;
            }
        }

        double Objective(List<OptimSample> samples)
        {
            OptimConfig cfg = Config;
            if (cfg.Objective == OptimObjective.ReachGoalFastest)
                return ReachGoalScore(samples);
            if (cfg.Objective == OptimObjective.SSJ)
            {
                double ssj = SSJScore(samples);
                return cfg.Direction == OptimDirection.Maximize ? ssj : -ssj;
            }
            if (cfg.Objective == OptimObjective.HealthDelta)
            {
                double first = samples[0].Health;
                double rawDelta = samples[samples.Count - 1].Health - first;
                if (cfg.Scope == OptimScope.AllFrames)
                    for (int i = 0; i < samples.Count; i++)
                        rawDelta = Math.Max(rawDelta, samples[i].Health - first);
                return cfg.Direction == OptimDirection.Maximize ? rawDelta : -rawDelta;
            }
            if (cfg.Objective == OptimObjective.Speed3D)
            {
                double weighted;
                if (cfg.Scope == OptimScope.LastFrame)
                {
                    weighted = PrioritySpeed(samples[samples.Count - 1].Velocity);
                }
                else
                {
                    weighted = double.NegativeInfinity;
                    for (int i = 0; i < samples.Count; i++)
                        weighted = Math.Max(weighted, PrioritySpeed(samples[i].Velocity));
                }
                return cfg.Direction == OptimDirection.Maximize ? weighted : -weighted;
            }

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

        /// <summary>Speed 3D under the selected horizontal / vertical axis preference.</summary>
        double PrioritySpeed(Vector3 velocity)
        {
            double horizontal = Math.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z);
            double vertical = Math.Abs(velocity.y);
            switch (Config.SpeedPriority)
            {
                case OptimSpeedPriority.Horizontal: return horizontal + vertical * 0.25;
                case OptimSpeedPriority.Vertical: return vertical + horizontal * 0.25;
                default: return Math.Sqrt(horizontal * horizontal + vertical * vertical);
            }
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
                        speedAtEntry = PrioritySpeed(samples[i].Velocity);
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
                case OptimObjective.SSJ: return s.SSJQuality;
                case OptimObjective.Health: return s.Health;
                case OptimObjective.HardDamage: return s.HardDamage;
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
                case OptimObjective.SSJ: return "ssj_boost";
                case OptimObjective.Health: return "hp";
                case OptimObjective.HardDamage: return "hard_damage";
                case OptimObjective.HealthDelta: return "hp_delta";
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

        void RememberTop(double score, List<TasFrameLine> segment, List<OptimSample> samples)
        {
            TopEntry entry = new TopEntry();
            entry.Score = score;
            entry.Segment = CloneAll(segment);
            entry.Samples = samples;
            _top.Add(entry);
            _top.Sort((a, b) => b.Score.CompareTo(a.Score));
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
