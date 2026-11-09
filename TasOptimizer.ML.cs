using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// The mutation arms the adaptive bandit chooses between: one per active mutation toggle.
    /// ForceSSJ is the Super Slide Jump pattern itself, which its checkbox controls.
    /// </summary>
    public enum MutationOperator
    {
        Yaw,
        Pitch,
        Framecount,
        MovementKeys,
        Dash,
        Slide,
        Jump,
        SSJ,
        ForceSSJ,
        JumpSlideDive,
        Autofuncs,
        SingleTicks,
        AutoDash,
        Combat,
        Weapons
    }

    /// <summary>
    /// Adaptive evolutionary half of the optimizer.
    ///
    /// A UCB1 multi-armed bandit learns which mutation operators pay off on the current
    /// segment. Every arm keeps N (attempts), S (successes) and R (summed positive score
    /// deltas); selection samples from weights built as mean reward plus an exploration
    /// bonus, so an arm that keeps adding speed gets most of the budget while every other
    /// arm is still tried often enough to notice when the segment changes.
    ///
    /// An elite population keeps the best diverse candidates with the trajectory that
    /// produced them. Two thirds of generations mutate one elite parent; the rest splice the
    /// early ticks of one parent onto the exit ticks of another and smooth the join. Attempts
    /// the runner could not play come back as null samples and are removed from the arm's N,
    /// so a splice failure is not mistaken for a losing mutation.
    /// </summary>
    public sealed partial class TasOptimizer
    {
        public const int EliteCapacity = 12;
        public const int OperatorCount = 15;
        /// <summary>UCB1 exploration constant; the classic sqrt(2) is close.</summary>
        const double BanditExploration = 1.4;
        /// <summary>Share of generations bred from two elite parents instead of mutated.</summary>
        const double CrossoverChance = 0.4;
        /// <summary>End positions closer than this count as the same trajectory for diversity.</summary>
        const float EliteNovelDistance = 0.5f;
        /// <summary>Different arms one generation may try before falling back to random mutation.</summary>
        const int BanditSelections = 12;

        sealed class EliteMember
        {
            public double Score;
            public List<TasFrameLine> Segment;
            public List<OptimSample> Samples;
            public Vector3 EndPosition;
            public Vector3 EndVelocity;
        }

        readonly List<EliteMember> _elite = new List<EliteMember>(EliteCapacity);
        readonly int[] _opAttempts = new int[OperatorCount];
        readonly int[] _opSuccesses = new int[OperatorCount];
        readonly double[] _opReward = new double[OperatorCount];
        int _opTotalAttempts;
        int _operatorVersion;
        int _topOperatorVersion = -1;
        string _topOperatorCache = string.Empty;
        readonly List<MutationOperator> _enabledScratch = new List<MutationOperator>(OperatorCount);
        readonly double[] _weightScratch = new double[OperatorCount];

        // Which arm produced the pending candidate, and the score it started from, so the
        // attempt can be credited when it comes back.
        int _pendingOperator = -1;
        double _pendingSourceScore;

        /// <summary>A bulk that gains at least this much horizontal speed is worth protecting, u/s.</summary>
        const float HighValueAccel = 15f;
        /// <summary>Protected bulks keep this share of their normal mutation probability (90% reduction).</summary>
        const double ProtectedMutationWeight = 0.1;
        /// <summary>Ticks before the impact that make up the crash correction window.</summary>
        const int PreCrashWindow = 20;
        /// <summary>Generations the crash window owns the whole mutation budget before the search resumes.</summary>
        const int FailureFocusAttempts = 64;

        /// <summary>One tagged stretch of segment ticks, kept in relative ticks from the segment start.</summary>
        struct TickSpan
        {
            public int From;
            public int To;
            /// <summary>How far past the protection threshold this bulk went (speed gain or spike).</summary>
            public float Confidence;
        }

        // High-confidence acceleration zones of the best trajectory: the launch, SSJ boost or
        // dash that must not be disturbed while the crash is fixed.
        readonly List<TickSpan> _highValueZones = new List<TickSpan>();

        // Crash attribution: the preserved prefix and the window the next generations may edit.
        bool _failureActive;
        int _failureAttemptsLeft;
        int _failureCorrectionFrom;
        int _failureCorrectionTo;
        float _failurePeakSpeed;
        string _failureSteerText = string.Empty;

        /// <summary>Candidates produced by adaptive mode (mutated and crossover generations).</summary>
        public int Generation { get; private set; }
        public int EliteCount { get { return _elite.Count; } }

        /// <summary>True while a detected bonk keeps every generation inside the crash window.</summary>
        public bool HasFailureAttribution { get { return _failureActive; } }

        /// <summary>Live credit/failure line for the GUI; empty when no crash is being corrected.</summary>
        public string FailureStatusLine
        {
            get
            {
                if (!_failureActive)
                    return string.Empty;
                string preserve = string.Empty;
                if (_failureCorrectionFrom > 0)
                    preserve = "Preserving Ticks " + Config.StartTick + ".." +
                               (Config.StartTick + _failureCorrectionFrom - 1) +
                               " (Speed: " + _failurePeakSpeed.ToString("0", CultureInfo.InvariantCulture) +
                               " u/s) | ";
                return "ML Status: " + preserve + "Correcting Ticks " +
                       (Config.StartTick + _failureCorrectionFrom) + ".." +
                       (Config.StartTick + _failureCorrectionTo) + " -> " + _failureSteerText;
            }
        }

        /// <summary>Top elite members sampled for consensus genes and combo mining (the spec's Top 8).</summary>
        const int ConsensusTopN = 8;
        const int ConsensusNeeded = 2;
        /// <summary>Share of elite runs that must agree before a button gene is locked in.</summary>
        const int ConsensusPercentNeeded = 75;
        /// <summary>Yaw/pitch standard deviation under which a tick counts as angle-clustered.</summary>
        const float ConsensusAngleStd = 2f;
        /// <summary>Angle mutation cap on an angle-clustered tick, degrees.</summary>
        const float ConsensusMicroStep = 0.5f;
        /// <summary>Differential Evolution projection factor: Xbest + F * (X1 - X2).</summary>
        const double DifferentialWeight = 0.7;
        const double DifferentialChance = 0.3;
        const int ComboCapacity = 24;
        const double ComboSpliceChance = 0.25;
        const float FlatAccel = 5f;

        // Stagnation escape thresholds and the pivot notice shown by the GUI.
        const int MildStagnation = 35;
        const int SevereStagnation = 70;
        const int DeadlockStagnation = 100;
        const int PivotNoticeGenerations = 24;

        /// <summary>Elite agreement on one tick: locked button genes and whether angles agree.</summary>
        sealed class ConsensusTick
        {
            public TasButtons Set;
            public TasButtons Clear;
            public bool ButtonsLocked;
            public bool AnglesClustered;
            public bool Locked { get { return ButtonsLocked || AnglesClustered; } }
        }

        sealed class WinningCombo
        {
            public TasButtons[] Ticks;
        }

        ConsensusTick[] _consensus;
        bool _consensusDirty = true;
        int _consensusPercent;
        readonly List<WinningCombo> _combos = new List<WinningCombo>();
        int _stagnantAttempts;
        int _pivotNoticeLeft;
        int _thawAttempts;

        int _adaptiveRouteAttempts;
        int _adaptiveLastOperator = -1;
        bool _freshRouteBurst;
        List<TasFrameLine> _lastAttemptRoute;
        const int RecentRouteCapacity = 64;
        readonly ulong[] _recentRouteHashes = new ulong[RecentRouteCapacity];
        int _recentRouteCount;
        int _recentRouteCursor;

        /// <summary>Called whenever the route actually changes, so the retry budget starts over.</summary>
        void ResetAdaptiveRouteFocus()
        {
            _adaptiveRouteAttempts = 0;
            _adaptiveLastOperator = -1;
            _freshRouteBurst = false;
            _lastAttemptRoute = null;
        }

        /// <summary>Fraction of segment ticks the elite pool agrees on, 0..100.</summary>
        public int ConsensusPercent { get { return _consensusPercent; } }
        /// <summary>True while consensus genes are enforced; severe stagnation thaws them.</summary>
        public bool ConsensusFrozen { get { return _thawAttempts <= 0 && _stagnantAttempts < SevereStagnation; } }
        /// <summary>True once the pool holds enough distinct runs for differential projection.</summary>
        public bool DifferentialActive { get { return _elite.Count >= 3; } }
        public int WinningComboCount { get { return _combos.Count; } }
        public int StagnantAttempts { get { return _stagnantAttempts; } }
        public bool StagnationPivotActive { get { return _pivotNoticeLeft > 0; } }

        /// <summary>
        /// The learned operator distribution as a short line: the three highest-weighted
        /// active arms with their normalized percentages. Cached until the statistics change.
        /// </summary>
        public string TopOperatorLine
        {
            get
            {
                if (_topOperatorVersion != _operatorVersion)
                {
                    _topOperatorVersion = _operatorVersion;
                    _topOperatorCache = BuildTopOperatorLine();
                }
                return _topOperatorCache;
            }
        }

        public static string OperatorName(MutationOperator op)
        {
            switch (op)
            {
                case MutationOperator.Yaw: return "Yaw";
                case MutationOperator.Pitch: return "Pitch";
                case MutationOperator.Framecount: return "Framecount";
                case MutationOperator.MovementKeys: return "Movement";
                case MutationOperator.Dash: return "Dash";
                case MutationOperator.Slide: return "Slide";
                case MutationOperator.Jump: return "Jump";
                case MutationOperator.SSJ: return "SSJ";
                case MutationOperator.ForceSSJ: return "Force SSJ";
                case MutationOperator.JumpSlideDive: return "Jump-Slide";
                case MutationOperator.Autofuncs: return "Autofuncs";
                case MutationOperator.SingleTicks: return "Single Tick";
                case MutationOperator.AutoDash: return "AutoDash";
                case MutationOperator.Combat: return "Combat";
                case MutationOperator.Weapons: return "Weapons";
                default: return op.ToString();
            }
        }

        // ---- Candidate generation -------------------------------------------------------------

        List<TasFrameLine> MakeAdaptiveCandidate()
        {
            Generation++;
            _pendingOperator = -1;

            // A wall bonk owns the whole budget: only the crash window is mutated until the
            // corner is solved or the focus budget runs out. The preserved prefix is untouched.
            if (_failureActive)
            {
                List<TasFrameLine> correction = MakeFailureCorrectionCandidate();
                if (correction != null)
                    return correction;
            }

            if (_pivotNoticeLeft > 0)
                _pivotNoticeLeft--;
            if (_thawAttempts > 0)
                _thawAttempts--;

            // Deadlock: give up the stuck best and restart around the most divergent elite run
            // (or a fresh early-input burst) before the consensus weights can trap the search.
            if (_stagnantAttempts >= DeadlockStagnation)
            {
                List<TasFrameLine> pivot = MakeAlternativeRouteCandidate();
                if (pivot != null)
                {
                    _stagnantAttempts = 0;
                    _adaptiveRouteAttempts = 0;
                    _adaptiveLastOperator = -1;
                    _pivotNoticeLeft = PivotNoticeGenerations;
                    // Let the fresh route establish itself before the elite lock can freeze it again.
                    _thawAttempts = MildStagnation;
                    PhaseText = "ML stagnation pivot (gen " + Generation + ", pop " +
                                _elite.Count + "/" + EliteCapacity + ")";
                    return pivot;
                }
            }

            // A route that stops producing improvements must not be retried forever. After a
            // short budget, fall back to the current best and force a different mutation shape
            // (structural arms first, with consensus and protection weighting bypassed).
            int routeRetryLimit = Config.AdaptiveRouteRetryLimit;
            if (routeRetryLimit < 1)
                routeRetryLimit = 1;
            _adaptiveRouteAttempts++;
            if (_adaptiveRouteAttempts >= routeRetryLimit)
            {
                _adaptiveRouteAttempts = 0;
                _freshRouteBurst = true;
                List<TasFrameLine> fresh = MakeFreshRouteCandidate();
                _freshRouteBurst = false;
                if (fresh != null)
                {
                    PhaseText = "ML fresh route from best (gen " + Generation + ", pop " +
                                _elite.Count + "/" + EliteCapacity + ")";
                    return fresh;
                }
            }

            // Consensus is rebuilt whenever the pool changed: settled inputs are locked below.
            EnsureConsensus();
            float branch = (float)_rng.NextDouble();

            // 30%: extrapolate the direction the elite pool is already moving in.
            if (_elite.Count >= 3 && branch < DifferentialChance)
            {
                List<TasFrameLine> extrapolated = MakeDifferentialCandidate();
                if (extrapolated != null)
                {
                    ApplyConsensus(extrapolated);
                    if (!SameRoute(extrapolated, _best))
                    {
                        PhaseText = "ML differential trend (gen " + Generation + ", pop " +
                                    _elite.Count + "/" + EliteCapacity + ")";
                        return extrapolated;
                    }
                }
            }

            // Next 40%: splice two elite parents. The remaining 30% mutates one parent.
            if (_elite.Count >= 2 && branch >= DifferentialChance &&
                branch < DifferentialChance + CrossoverChance)
            {
                List<TasFrameLine> child = TryCrossover();
                if (child != null)
                {
                    ApplyConsensus(child);
                    if (!SameRoute(child, _best))
                    {
                        PhaseText = "ML crossover (gen " + Generation + ", pop " + _elite.Count + "/" + EliteCapacity + ")";
                        return child;
                    }
                }
            }

            EliteMember parent = PickEliteParent();
            List<TasFrameLine> source = parent != null ? parent.Segment : _best;
            List<OptimSample> samples = parent != null ? parent.Samples : _bestActionSamples;
            double sourceScore = parent != null ? parent.Score : BestScore;
            int total = Config.EndTick - Config.StartTick;

            for (int tries = 0; tries < BanditSelections; tries++)
            {
                MutationOperator op;
                if (!TrySelectOperator(out op))
                    break;
                List<TasFrameLine> candidate = CloneAll(source);
                if (!ApplyOperator(op, candidate, samples))
                    continue;
                if (SegmentTickCount(candidate) != total)
                {
                    // The arm built a candidate the segment cannot splice (a framecount change
                    // without keep-total). That is an invalid attempt, not a free retry: count
                    // it so the bandit learns to avoid that arm on this route.
                    _opAttempts[(int)op]++;
                    _opTotalAttempts++;
                    _operatorVersion++;
                    continue;
                }

                // Lock the genes the elite pool agrees on, then occasionally drop a mined
                // rhythm into a flat stretch so the mutation can discover new acceleration.
                ApplyConsensus(candidate);
                if (_combos.Count > 0 && _rng.NextDouble() < ComboSpliceChance)
                    TrySpliceCombo(candidate, samples);
                if (SegmentTickCount(candidate) != total)
                    continue;
                // Do not hand the exact parent or current best back to the simulation loop.
                if (SameRoute(candidate, source) || SameRoute(candidate, _best))
                    continue;

                _opAttempts[(int)op]++;
                _opTotalAttempts++;
                _operatorVersion++;
                _pendingOperator = (int)op;
                _pendingSourceScore = sourceScore;
                _adaptiveLastOperator = (int)op;
                PhaseText = "ML " + OperatorName(op) + " (gen " + Generation + ", pop " +
                            _elite.Count + "/" + EliteCapacity + ")";
                return candidate;
            }

            // Every enabled arm failed on this parent. Revert to the current best and try a
            // different mutation shape instead of replaying the same route.
            _freshRouteBurst = true;
            List<TasFrameLine> fallback = MakeFreshRouteCandidate();
            _freshRouteBurst = false;
            if (fallback != null)
            {
                PhaseText = "ML fresh route from best (gen " + Generation + ", pop " +
                            _elite.Count + "/" + EliteCapacity + ")";
                return fallback;
            }
            fallback = MakeRandomCandidate();
            _pendingOperator = -1;
            _pendingSourceScore = BestScore;
            PhaseText = "ML fallback (gen " + Generation + ", pop " + _elite.Count + "/" + EliteCapacity + ")";
            return fallback;
        }

        /// <summary>
        /// Builds a deliberately different route from the current best. Structural changes are
        /// tried first, the last successful operator is skipped, and consensus/protection are
        /// bypassed so a locked local optimum can actually move.
        /// </summary>
        List<TasFrameLine> MakeFreshRouteCandidate()
        {
            int total = Config.EndTick - Config.StartTick;
            if (_best == null || SegmentTickCount(_best) != total)
                return null;

            MutationOperator[] preferred =
            {
                MutationOperator.MovementKeys, MutationOperator.Framecount,
                MutationOperator.SSJ, MutationOperator.JumpSlideDive,
                MutationOperator.SingleTicks, MutationOperator.Jump,
                MutationOperator.Slide, MutationOperator.Dash,
                MutationOperator.Autofuncs, MutationOperator.AutoDash,
                MutationOperator.Yaw, MutationOperator.Pitch,
                MutationOperator.Combat, MutationOperator.Weapons,
                MutationOperator.ForceSSJ
            };
            int[] order = ShuffledOrder(preferred.Length);
            for (int i = 0; i < order.Length; i++)
            {
                MutationOperator op = preferred[order[i]];
                if (!OperatorEnabled(op) || (int)op == _adaptiveLastOperator)
                    continue;
                List<TasFrameLine> candidate = CloneAll(_best);
                if (!ApplyOperator(op, candidate, _bestActionSamples))
                    continue;
                if (SegmentTickCount(candidate) != total || SameRoute(candidate, _best))
                    continue;
                _pendingOperator = (int)op;
                _pendingSourceScore = BestScore;
                _adaptiveLastOperator = (int)op;
                return candidate;
            }

            // Last resort: guarantee a different input on a bulk inside the window.
            List<TasFrameLine> fallback = CloneAll(_best);
            int index = PickTargetBulk(fallback);
            if (index < 0)
                return null;
            fallback[index].Buttons ^= TasButtons.Forward;
            _pendingOperator = -1;
            _pendingSourceScore = BestScore;
            return fallback;
        }

        static bool SameRoute(List<TasFrameLine> a, List<TasFrameLine> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                TasFrameLine x = a[i];
                TasFrameLine y = b[i];
                if (x == null || y == null)
                {
                    if (!ReferenceEquals(x, y)) return false;
                    continue;
                }
                if (x.Kind != y.Kind || x.Count != y.Count || x.Buttons != y.Buttons ||
                    x.Autofuncs != y.Autofuncs)
                    return false;
                if (!SameAngle(x.Yaw, y.Yaw) || !SameAngle(x.Pitch, y.Pitch) ||
                    !SameAngle(x.TargetYaw, y.TargetYaw))
                    return false;
                int commands = x.Commands != null ? x.Commands.Count : 0;
                int otherCommands = y.Commands != null ? y.Commands.Count : 0;
                if (commands != otherCommands)
                    return false;
                for (int c = 0; c < commands; c++)
                    if (!string.Equals(x.Commands[c], y.Commands[c], StringComparison.Ordinal))
                        return false;
                if (!x.IsFramebulk &&
                    !string.Equals(x.ToPipeLine(), y.ToPipeLine(), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        static bool SameAngle(AngleToken a, AngleToken b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.Unchanged == b.Unchanged && a.IsDelta == b.IsDelta && a.Value == b.Value;
        }

        bool WasRouteTried(List<TasFrameLine> route)
        {
            ulong hash = RouteHash(route);
            for (int i = 0; i < _recentRouteCount; i++)
                if (_recentRouteHashes[i] == hash)
                    return true;
            return false;
        }

        void RememberRoute(List<TasFrameLine> route)
        {
            _recentRouteHashes[_recentRouteCursor] = RouteHash(route);
            _recentRouteCursor = (_recentRouteCursor + 1) % RecentRouteCapacity;
            if (_recentRouteCount < RecentRouteCapacity)
                _recentRouteCount++;
        }

        static ulong RouteHash(List<TasFrameLine> route)
        {
            if (route == null)
                return 0UL;
            ulong hash = 1469598103934665603UL;
            for (int i = 0; i < route.Count; i++)
            {
                TasFrameLine line = route[i];
                if (line == null)
                {
                    hash = MixHash(hash, 0x9E3779B97F4A7C15UL);
                    continue;
                }
                hash = MixHash(hash, (ulong)line.Kind);
                hash = MixHash(hash, (ulong)line.Count);
                hash = MixHash(hash, (ulong)line.Buttons);
                hash = MixHash(hash, (ulong)line.Autofuncs);
                hash = MixHash(hash, line.IsFramebulk ? 1UL : 0UL);
                hash = MixAngleHash(hash, line.Yaw);
                hash = MixAngleHash(hash, line.Pitch);
                hash = MixAngleHash(hash, line.TargetYaw);
                int commands = line.Commands != null ? line.Commands.Count : 0;
                hash = MixHash(hash, (ulong)commands);
                for (int c = 0; c < commands; c++)
                {
                    string command = line.Commands[c] ?? string.Empty;
                    for (int k = 0; k < command.Length; k++)
                        hash = MixHash(hash, command[k]);
                    hash = MixHash(hash, 0xFFFFUL);
                }
                if (!line.IsFramebulk)
                {
                    string text = line.ToPipeLine();
                    for (int k = 0; k < text.Length; k++)
                        hash = MixHash(hash, text[k]);
                }
            }
            return hash;
        }

        static ulong MixHash(ulong hash, ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
            return hash;
        }

        static ulong MixAngleHash(ulong hash, AngleToken angle)
        {
            if (angle == null)
                return MixHash(hash, 0xABCDEFUL);
            hash = MixHash(hash, angle.Unchanged ? 1UL : 0UL);
            hash = MixHash(hash, angle.IsDelta ? 1UL : 0UL);
            hash = MixHash(hash, (ulong)BitConverter.DoubleToInt64Bits(angle.Value));
            return hash;
        }

        // ---- UCB1 bandit ----------------------------------------------------------------------

        bool OperatorEnabled(MutationOperator op)
        {
            OptimMutationSettings m = Config.Mutations;
            switch (op)
            {
                case MutationOperator.Yaw: return m.MutateYaw;
                case MutationOperator.Pitch: return m.MutatePitch;
                case MutationOperator.Framecount: return m.MutateFramecount;
                case MutationOperator.MovementKeys: return m.MutateMovement;
                case MutationOperator.Dash: return m.MutateDash;
                case MutationOperator.Slide: return m.MutateSlide;
                case MutationOperator.Jump: return m.MutateJump;
                case MutationOperator.SSJ: return m.MutateSSJ;
                case MutationOperator.ForceSSJ: return Config.ForceSSJ;
                case MutationOperator.JumpSlideDive: return m.MutateJumpSlide;
                case MutationOperator.Autofuncs: return AnyAutofuncEnabled();
                case MutationOperator.SingleTicks: return m.MutateSingleTick;
                case MutationOperator.AutoDash: return m.MutateAutoDash;
                case MutationOperator.Combat: return m.MutateCombat;
                case MutationOperator.Weapons: return m.MutateWeapons;
                default: return false;
            }
        }

        /// <summary>UCB1: mean reward of the arm plus its exploration bonus.</summary>
        double OperatorWeight(MutationOperator op)
        {
            int i = (int)op;
            int attempts = _opAttempts[i];
            double mean = attempts > 0 ? _opReward[i] / attempts : 0.0;
            double explore = BanditExploration *
                             Math.Sqrt(Math.Log(_opTotalAttempts + 1) / Math.Max(1, attempts));
            return mean + explore;
        }

        void FillEnabledOperators()
        {
            _enabledScratch.Clear();
            for (int i = 0; i < OperatorCount; i++)
            {
                MutationOperator op = (MutationOperator)i;
                if (OperatorEnabled(op))
                    _enabledScratch.Add(op);
            }
        }

        bool TrySelectOperator(out MutationOperator op)
        {
            FillEnabledOperators();
            if (_enabledScratch.Count == 0)
            {
                op = MutationOperator.Yaw;
                return false;
            }

            double total = 0.0;
            for (int i = 0; i < _enabledScratch.Count; i++)
            {
                _weightScratch[i] = OperatorWeight(_enabledScratch[i]);
                total += _weightScratch[i];
            }
            if (total <= 0.0)
            {
                // Nothing has a reward or an exploration bonus yet: uniform start.
                op = _enabledScratch[_rng.Next(_enabledScratch.Count)];
                return true;
            }

            double roll = _rng.NextDouble() * total;
            for (int i = 0; i < _enabledScratch.Count; i++)
            {
                if (roll < _weightScratch[i])
                {
                    op = _enabledScratch[i];
                    return true;
                }
                roll -= _weightScratch[i];
            }
            op = _enabledScratch[_enabledScratch.Count - 1];
            return true;
        }

        string BuildTopOperatorLine()
        {
            FillEnabledOperators();
            if (_enabledScratch.Count == 0)
                return "none (every mutation is off)";

            double total = 0.0;
            for (int i = 0; i < _enabledScratch.Count; i++)
            {
                _weightScratch[i] = OperatorWeight(_enabledScratch[i]);
                total += _weightScratch[i];
            }
            if (total <= 0.0)
            {
                for (int i = 0; i < _enabledScratch.Count; i++)
                    _weightScratch[i] = 1.0;
                total = _enabledScratch.Count;
            }

            int picks = Math.Min(3, _enabledScratch.Count);
            bool[] used = new bool[_enabledScratch.Count];
            StringBuilder text = new StringBuilder();
            for (int p = 0; p < picks; p++)
            {
                int best = -1;
                double bestWeight = -1.0;
                for (int i = 0; i < _enabledScratch.Count; i++)
                {
                    if (used[i] || _weightScratch[i] <= bestWeight)
                        continue;
                    bestWeight = _weightScratch[i];
                    best = i;
                }
                if (best < 0)
                    break;
                used[best] = true;
                if (p > 0)
                    text.Append(", ");
                text.Append(OperatorName(_enabledScratch[best]));
                text.Append(" (");
                text.Append((100.0 * _weightScratch[best] / total).ToString("0", CultureInfo.InvariantCulture));
                text.Append("%)");
            }
            return text.ToString();
        }

        /// <summary>
        /// Credits the arm that produced the pending attempt. The attempt itself was counted
        /// when the arm was chosen; here it either earns its positive delta or stays a miss.
        /// A null/empty sample list means the candidate never reached the game, so the count is
        /// given back instead of penalizing the arm.
        /// </summary>
        void RecordOperatorOutcome(List<OptimSample> samples, OptimOutcomeKind outcome, double score)
        {
            int op = _pendingOperator;
            _pendingOperator = -1;
            if (op < 0 || op >= OperatorCount)
                return;

            if (samples == null || samples.Count == 0)
            {
                if (_opAttempts[op] > 0)
                    _opAttempts[op]--;
                if (_opTotalAttempts > 0)
                    _opTotalAttempts--;
                _operatorVersion++;
                return;
            }

            if (outcome != OptimOutcomeKind.Valid)
                return;
            double delta = score - _pendingSourceScore;
            if (delta <= 0.0)
                return;
            _opSuccesses[op]++;
            _opReward[op] += delta;
            _operatorVersion++;
        }

        // ---- Operator application -------------------------------------------------------------

        bool ApplyOperator(MutationOperator op, List<TasFrameLine> segment, List<OptimSample> samples)
        {
            OptimMutationSettings m = Config.Mutations;
            switch (op)
            {
                case MutationOperator.ForceSSJ:
                    return TryForceSSJ(segment, samples);

                case MutationOperator.Yaw:
                case MutationOperator.Pitch:
                {
                    int index = PickTargetBulk(segment);
                    if (index < 0)
                        return false;
                    TasFrameLine line = segment[index];
                    int spanStart;
                    int spanEnd;
                    bool clustered = LineTickSpan(segment, index, out spanStart, out spanEnd) &&
                                     SpanAnglesClustered(spanStart, spanEnd);
                    float yawStep = m.YawStep * StagnationStepScale;
                    float pitchStep = m.PitchStep * StagnationStepScale;
                    if (op == MutationOperator.Yaw)
                        line.Yaw = clustered
                            ? NudgeMicro(line.Yaw, ConsensusMicroStep, AngleAnchor(segment, index, true))
                            : NudgeRandom(line.Yaw, yawStep, AngleAnchor(segment, index, true));
                    else
                        line.Pitch = clustered
                            ? NudgeMicro(line.Pitch, ConsensusMicroStep, AngleAnchor(segment, index, false))
                            : NudgeRandom(line.Pitch, pitchStep, AngleAnchor(segment, index, false));
                    return true;
                }

                case MutationOperator.MovementKeys:
                {
                    int index = PickTargetBulk(segment);
                    if (index < 0)
                        return false;
                    segment[index].Buttons ^= RandomFlag(TasButtons.Forward, TasButtons.Back,
                                                         TasButtons.Left, TasButtons.Right);
                    return true;
                }

                case MutationOperator.Dash:
                    return TryTogglePress(segment, samples, TasButtons.Dash);
                case MutationOperator.Slide:
                    return TryTogglePress(segment, samples, TasButtons.Slide);
                case MutationOperator.Jump:
                    return TryTogglePress(segment, samples, TasButtons.Jump);

                case MutationOperator.Autofuncs:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                    {
                        int index = bulks[i];
                        ActionWindow window;
                        bool known = TryActionWindow(segment, index, samples, out window);
                        List<TasAutofuncs> choices = EligibleAutofuncs(segment[index], samples, known, window);
                        if (choices.Count == 0)
                            continue;
                        segment[index].Autofuncs ^= choices[_rng.Next(choices.Count)];
                        return true;
                    }
                    return false;
                }

                case MutationOperator.AutoDash:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                    {
                        TasFrameLine line = segment[bulks[i]];
                        if ((line.Autofuncs & TasAutofuncs.AutoDash) != 0)
                        {
                            line.Autofuncs &= ~TasAutofuncs.AutoDash;
                            return true;
                        }
                        ActionWindow window;
                        if (TryActionWindow(segment, bulks[i], samples, out window) &&
                            WindowHasDash(samples, window))
                        {
                            line.Autofuncs |= TasAutofuncs.AutoDash;
                            return true;
                        }
                    }
                    return false;
                }

                case MutationOperator.Framecount:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                        if (MutateFramecount(segment, bulks[i], StagnationStepScale))
                            return true;
                    return false;
                }

                case MutationOperator.SSJ:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                        if (TryMutateSSJ(segment, bulks[i], samples))
                            return true;
                    return false;
                }

                case MutationOperator.JumpSlideDive:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                        if (TryMutateJumpSlide(segment, bulks[i], samples))
                            return true;
                    return false;
                }

                case MutationOperator.SingleTicks:
                {
                    int[] bulks = ShuffledBulks(segment);
                    for (int i = 0; i < bulks.Length; i++)
                        if (TryMutateSingleTickRandom(segment, bulks[i], samples))
                            return true;
                    return false;
                }

                case MutationOperator.Combat:
                {
                    int index = PickTargetBulk(segment);
                    if (index < 0)
                        return false;
                    segment[index].Buttons ^= RandomFlag(TasButtons.Fire1, TasButtons.Fire2,
                                                         TasButtons.Punch, TasButtons.Whiplash);
                    return true;
                }

                case MutationOperator.Weapons:
                {
                    int index = PickTargetBulk(segment);
                    if (index < 0)
                        return false;
                    segment[index].Buttons ^= RandomFlag(TasButtons.Slot1, TasButtons.Slot2, TasButtons.Slot3,
                                                         TasButtons.Slot4, TasButtons.Slot5, TasButtons.Slot6);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Toggles a press on one bulk. Removing a press is always offered; adding one is only
        /// offered where the sampled state says the game would accept it.
        /// </summary>
        bool TryTogglePress(List<TasFrameLine> segment, List<OptimSample> samples, TasButtons button)
        {
            int[] bulks = ShuffledBulks(segment);
            for (int i = 0; i < bulks.Length; i++)
            {
                int index = bulks[i];
                TasFrameLine line = segment[index];
                if ((line.Buttons & button) != 0)
                {
                    line.Buttons &= ~button;
                    return true;
                }
                ActionWindow window;
                if (!TryActionWindow(segment, index, samples, out window))
                    continue;
                bool ready = button == TasButtons.Jump ? IsJumpReady(samples, window.Pre)
                           : button == TasButtons.Slide ? IsSlideUseful(samples, window.Pre)
                           : IsDashReady(samples, window.Pre);
                if (!ready)
                    continue;
                line.Buttons |= button;
                return true;
            }
            return false;
        }

        int[] ShuffledBulks(List<TasFrameLine> segment, bool protect = true)
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
                {
                    double weight = protect ? BulkMutationWeight(cursor, cursor + ticks) : 1.0;
                    if (weight >= 1.0 || _rng.NextDouble() < weight)
                        indices.Add(i);
                }
                cursor += ticks;
            }
            // Every weighted candidate was rejected: try every bulk inside the window rather
            // than falling back to frozen downstream frames.
            if (indices.Count == 0)
            {
                cursor = 0;
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
            }

            int[] order = ShuffledOrder(indices.Count);
            int[] bulks = new int[order.Length];
            for (int i = 0; i < order.Length; i++)
                bulks[i] = indices[order[i]];
            return bulks;
        }

        // ---- Temporal credit assignment and failure attribution -------------------------------

        bool IsProtectedSpan(int start, int end)
        {
            for (int i = 0; i < _highValueZones.Count; i++)
            {
                TickSpan zone = _highValueZones[i];
                if (start < zone.To && end > zone.From)
                    return true;
            }
            return false;
        }

        /// <summary>Picks a mutation bulk, weighting protected and settled frames at a fraction.</summary>
        int PickTargetBulk(List<TasFrameLine> segment)
        {
            double total = 0.0;
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && cursor < MutationWindowTicks)
                    total += BulkMutationWeight(cursor, cursor + ticks);
                cursor += ticks;
            }
            if (total <= 0.0)
                return -1;

            double roll = _rng.NextDouble() * total;
            cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && cursor < MutationWindowTicks)
                {
                    double weight = BulkMutationWeight(cursor, cursor + ticks);
                    if (roll < weight)
                        return i;
                    roll -= weight;
                }
                cursor += ticks;
            }
            return -1;
        }

        /// <summary>
        /// Target weight for one bulk. High-value frames keep a tenth of their weight; ticks
        /// the elite pool already agrees on lose up to three quarters, so mutations drift to
        /// the unresolved part of the route. Severe stagnation thaws the consensus part.
        /// </summary>
        double BulkMutationWeight(int start, int end)
        {
            if (_freshRouteBurst)
                return 1.0;
            double weight = IsProtectedSpan(start, end) ? ProtectedMutationWeight : 1.0;
            if (!ConsensusFrozen || _consensus == null)
                return weight;
            int ticks = end - start;
            if (ticks <= 0)
                return weight;
            int settled = 0;
            int last = Mathf.Min(end, _consensus.Length);
            for (int t = Mathf.Max(0, start); t < last; t++)
                if (_consensus[t].Locked)
                    settled++;
            return weight * (1.0 - 0.75 * settled / ticks);
        }

        /// <summary>
        /// Speed measured on the axis the user asked the search to favour. Balanced keeps the
        /// historical horizontal measure; Vertical uses |vel.y| so slams and jumps become the
        /// protected, high-value inputs instead of only horizontal boosts.
        /// </summary>
        float CreditSpeed(OptimSample sample)
        {
            if (Config.SpeedPriority == OptimSpeedPriority.Vertical)
                return Mathf.Abs(sample.Velocity.y);
            return sample.SpeedXZ > 0f ? sample.SpeedXZ : HorizontalSpeed(sample.Velocity);
        }

        float CreditAccel(List<OptimSample> samples, int index)
        {
            if (Config.SpeedPriority != OptimSpeedPriority.Vertical)
                return samples[index].AccelXZ;
            if (index <= 0)
                return 0f;
            return Mathf.Abs(samples[index].Velocity.y) - Mathf.Abs(samples[index - 1].Velocity.y);
        }

        /// <summary>
        /// Reads the per-tick trace of a finished trajectory and tags the bulks that created
        /// speed: either a bulk that gained more than <see cref="HighValueAccel"/> over its ticks,
        /// or one that contains a tick with that much acceleration (an SSJ boost, dash, slam
        /// redirect). Those bulks become the protected zones the mutation engine avoids.
        /// </summary>
        void UpdateCreditAssignment(List<TasFrameLine> segment, List<OptimSample> samples)
        {
            if (Config.SearchMode != OptimSearchMode.Adaptive)
                return;
            _highValueZones.Clear();
            if (segment == null || samples == null || samples.Count == 0)
                return;
            int total = Config.EndTick - Config.StartTick;
            if (total <= 0)
                return;

            float[] speed = new float[total + 1];
            float[] accel = new float[total + 1];
            for (int i = 0; i < samples.Count; i++)
            {
                int rel = samples[i].Tick - Config.StartTick;
                if (rel < 1 || rel > total)
                    continue;
                speed[rel] = CreditSpeed(samples[i]);
                accel[rel] = CreditAccel(samples, i);
            }

            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                int start = cursor;
                int end = cursor + ticks;
                cursor = end;
                if (!line.IsFramebulk || ticks <= 0)
                    continue;

                int first = Mathf.Max(0, start);
                int last = Mathf.Min(end, total);
                float gain = speed[last] - speed[first];
                float spike = 0f;
                for (int t = Mathf.Max(1, start); t <= last; t++)
                    if (accel[t] > spike)
                        spike = accel[t];

                float confidence = Mathf.Max(gain, spike);
                if (confidence < HighValueAccel)
                    continue;
                TickSpan zone = new TickSpan();
                zone.From = start;
                zone.To = end;
                zone.Confidence = confidence;
                _highValueZones.Add(zone);
            }
        }

        /// <summary>
        /// A bonk pins the search to the crash: the prefix keeps its speed and every candidate
        /// edits only the ticks in the correction window before the impact.
        /// </summary>
        void BeginFailureWindow(OptimGeometryProbe probe, List<OptimSample> samples)
        {
            if (Config.SearchMode != OptimSearchMode.Adaptive || probe == null || !probe.HasImpact ||
                samples == null || samples.Count == 0)
                return;
            int total = Config.EndTick - Config.StartTick;
            if (total <= 0)
                return;

            int localImpact = Mathf.Clamp(probe.Tick - Config.StartTick, 0, total - 1);
            // A crash after the mutation horizon cannot be corrected without editing frozen
            // downstream frames, so leave the normal search running.
            if (localImpact > EffectiveMutationEndTick - Config.StartTick)
                return;
            _failureActive = true;
            _failureAttemptsLeft = FailureFocusAttempts;
            _failureCorrectionTo = localImpact;
            _failureCorrectionFrom = Mathf.Max(0, localImpact - PreCrashWindow);
            _failureSteerText = SteeringText(probe.ClearanceAngle);

            _failurePeakSpeed = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                int rel = samples[i].Tick - Config.StartTick;
                if (rel < 0 || rel >= _failureCorrectionFrom)
                    continue;
                float speed = samples[i].SpeedXZ > 0f
                    ? samples[i].SpeedXZ
                    : HorizontalSpeed(samples[i].Velocity);
                if (speed > _failurePeakSpeed)
                    _failurePeakSpeed = speed;
            }
            if (_failurePeakSpeed <= 0f)
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    float speed = samples[i].SpeedXZ > 0f
                        ? samples[i].SpeedXZ
                        : HorizontalSpeed(samples[i].Velocity);
                    if (speed > _failurePeakSpeed)
                        _failurePeakSpeed = speed;
                }
            }
        }

        void ClearFailureWindow()
        {
            _failureActive = false;
            _failureAttemptsLeft = 0;
        }

        List<int> BulksOverlapping(List<TasFrameLine> segment, int from, int to)
        {
            List<int> result = new List<int>();
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                int start = cursor;
                int end = cursor + ticks;
                cursor = end;
                if (line.IsFramebulk && start < to && end > from)
                    result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// One targeted candidate for the crash window: steer yaw toward the clearance angle,
        /// hold the strafe key that opens the corner, and retime slide / jump on the last bulk
        /// before the impact. Every other framebulk is copied untouched, so the speed gained
        /// before the window is preserved exactly.
        /// </summary>
        List<TasFrameLine> MakeFailureCorrectionCandidate()
        {
            if (!_failureActive)
                return null;
            if (LastGeometry == null || !LastGeometry.HasImpact)
            {
                ClearFailureWindow();
                return null;
            }

            OptimMutationSettings m = Config.Mutations;
            if (!m.MutateYaw && !m.MutateMovement && !m.MutateSlide && !m.MutateJump && !m.MutateSingleTick)
            {
                ClearFailureWindow();
                return null;
            }

            // Correct the current best route: a correction that improves it is kept, and the
            // next attempt builds on that improvement instead of restarting from the crash.
            List<TasFrameLine> source = _best;
            List<OptimSample> samples = _bestActionSamples;
            int total = Config.EndTick - Config.StartTick;
            if (source == null || samples == null || SegmentTickCount(source) != total)
            {
                ClearFailureWindow();
                return null;
            }

            List<TasFrameLine> candidate = CloneAll(source);
            List<int> window = BulksOverlapping(candidate, _failureCorrectionFrom, _failureCorrectionTo + 1);
            if (window.Count == 0)
            {
                ClearFailureWindow();
                return null;
            }

            bool changed = false;
            float targetYaw = YawOf(LastGeometry.ClearanceDirection);
            double maxYawStep = Mathf.Clamp(m.YawStep * 4f, 5f, 30f);

            if (m.MutateYaw)
            {
                int steerCount = Mathf.Min(3, window.Count);
                for (int k = 0; k < steerCount; k++)
                {
                    int index = window[window.Count - 1 - k];
                    TasFrameLine line = candidate[index];
                    if (line == null || !line.IsFramebulk)
                        continue;
                    double current = line.Yaw.Resolve(AngleAnchor(candidate, index, true));
                    double delta = Mathf.DeltaAngle((float)current, targetYaw);
                    if (Mathf.Abs((float)delta) < 0.05f)
                        continue;
                    double step = Mathf.Min(Mathf.Abs((float)delta), (float)maxYawStep);
                    step *= 0.6 + _rng.NextDouble() * 0.4;
                    double sign = delta < 0.0 ? -1.0 : 1.0;
                    line.Yaw = AngleToken.Absolute(current + sign * step);
                    changed = true;
                }
            }

            if (m.MutateMovement && Mathf.Abs(LastGeometry.ClearanceAngle) > 6f)
            {
                TasButtons open = LastGeometry.ClearanceAngle > 0f ? TasButtons.Right : TasButtons.Left;
                TasButtons closed = open == TasButtons.Right ? TasButtons.Left : TasButtons.Right;
                int lateralCount = Mathf.Min(2, window.Count);
                for (int k = 0; k < lateralCount; k++)
                {
                    int index = window[window.Count - 1 - k];
                    TasFrameLine line = candidate[index];
                    if (line == null || !line.IsFramebulk)
                        continue;
                    TasButtons next = (line.Buttons | open) & ~closed;
                    if (next != line.Buttons)
                    {
                        line.Buttons = next;
                        changed = true;
                    }
                }
            }

            int lastIndex = window[window.Count - 1];
            TasFrameLine last = candidate[lastIndex];
            bool timed = false;
            if (last != null && m.MutateSingleTick && last.Count >= 2 && !HasCommands(last))
            {
                TasButtons toggle = _rng.NextDouble() < 0.5 ? TasButtons.Jump : TasButtons.Slide;
                timed = TrySplitSingleTick(candidate, lastIndex, toggle, samples);
                if (timed)
                    changed = true;
            }
            if (!timed && last != null)
            {
                ActionWindow actionWindow;
                if (TryActionWindow(candidate, lastIndex, samples, out actionWindow))
                {
                    if (m.MutateSlide && IsSlideUseful(samples, actionWindow.Pre))
                    {
                        last.Buttons ^= TasButtons.Slide;
                        changed = true;
                    }
                    else if (m.MutateJump && IsJumpReady(samples, actionWindow.Pre))
                    {
                        last.Buttons ^= TasButtons.Jump;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                ClearFailureWindow();
                return null;
            }

            _failureAttemptsLeft--;
            if (_failureAttemptsLeft <= 0)
                ClearFailureWindow();
            PhaseText = "ML crash correction " + LastGeometry.ClearanceLabel() + " (ticks " +
                        (Config.StartTick + _failureCorrectionFrom) + ".." +
                        (Config.StartTick + _failureCorrectionTo) + ")";
            return candidate;
        }

        static string SteeringText(float angle)
        {
            string side;
            if (Mathf.Abs(angle) <= 12f)
                side = "Ahead";
            else if (Mathf.Abs(angle) >= 165f)
                side = "Behind";
            else
                side = angle > 0f ? "Right" : "Left";
            return "Steering " + side + " (" + (angle >= 0f ? "+" : string.Empty) +
                   angle.ToString("0.0", CultureInfo.InvariantCulture) + "\u00B0)";
        }

        // ---- Consensus gene freezing -----------------------------------------------------------

        void EnsureConsensus()
        {
            if (!_consensusDirty)
                return;
            _consensusDirty = false;
            _consensus = null;
            _consensusPercent = 0;

            int total = Config.EndTick - Config.StartTick;
            int members = Math.Min(ConsensusTopN, _elite.Count);
            if (members < ConsensusNeeded || total <= 0)
                return;

            TasButtons[][] buttons = new TasButtons[members][];
            float[][] yaw = new float[members][];
            float[][] pitch = new float[members][];
            for (int m = 0; m < members; m++)
            {
                if (!BuildTickGenes(_elite[m], total, out buttons[m], out yaw[m], out pitch[m]))
                    return;
            }

            ConsensusTick[] ticks = new ConsensusTick[total];
            int lockedTicks = 0;
            for (int t = 0; t < total; t++)
            {
                ConsensusTick info = new ConsensusTick();
                for (int bit = 0; bit < 24; bit++)
                {
                    TasButtons flag = (TasButtons)(1 << bit);
                    int held = 0;
                    for (int m = 0; m < members; m++)
                        if ((buttons[m][t] & flag) != 0)
                            held++;
                    if (held * 100 >= members * ConsensusPercentNeeded)
                    {
                        info.Set |= flag;
                        info.ButtonsLocked = true;
                    }
                    else if (held * 100 <= members * (100 - ConsensusPercentNeeded))
                    {
                        info.Clear |= flag;
                        info.ButtonsLocked = true;
                    }
                }

                float referenceYaw = yaw[0][t];
                float referencePitch = pitch[0][t];
                double sum = 0.0;
                double sumSq = 0.0;
                double pSum = 0.0;
                double pSumSq = 0.0;
                for (int m = 0; m < members; m++)
                {
                    double dy = Mathf.DeltaAngle(referenceYaw, yaw[m][t]);
                    double dp = Mathf.DeltaAngle(referencePitch, pitch[m][t]);
                    sum += dy;
                    sumSq += dy * dy;
                    pSum += dp;
                    pSumSq += dp * dp;
                }
                double mean = sum / members;
                double pMean = pSum / members;
                double variance = Math.Max(0.0, sumSq / members - mean * mean);
                double pVariance = Math.Max(0.0, pSumSq / members - pMean * pMean);
                info.AnglesClustered = Math.Sqrt(variance) < ConsensusAngleStd &&
                                       Math.Sqrt(pVariance) < ConsensusAngleStd;

                ticks[t] = info;
                if (info.Locked)
                    lockedTicks++;
            }

            _consensus = ticks;
            _consensusPercent = (int)Math.Round(100.0 * lockedTicks / total);
        }

        /// <summary>Resolves every tick's buttons and view angles for one elite member.</summary>
        bool BuildTickGenes(EliteMember member, int total, out TasButtons[] buttons,
                            out float[] yaw, out float[] pitch)
        {
            buttons = null;
            yaw = null;
            pitch = null;
            if (member == null || member.Segment == null || SegmentTickCount(member.Segment) != total)
                return false;

            buttons = new TasButtons[total];
            yaw = new float[total];
            pitch = new float[total];
            int cursor = 0;
            for (int i = 0; i < member.Segment.Count; i++)
            {
                TasFrameLine line = member.Segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && ticks > 0)
                {
                    double lineYaw = line.Yaw != null
                        ? line.Yaw.Resolve(AngleAnchor(member.Segment, i, true)) : 0.0;
                    double linePitch = line.Pitch != null
                        ? line.Pitch.Resolve(AngleAnchor(member.Segment, i, false)) : 0.0;
                    int end = Mathf.Min(cursor + ticks, total);
                    for (int t = cursor; t < end; t++)
                    {
                        buttons[t] = line.Buttons;
                        yaw[t] = (float)lineYaw;
                        pitch[t] = (float)linePitch;
                    }
                }
                cursor += ticks;
            }
            return true;
        }

        /// <summary>
        /// Applies elite agreement to a finished candidate: agreed presses are forced on and
        /// presses the pool agrees are off are cleared. Unresolved ticks are left to mutate.
        /// Severe stagnation thaws the lock so the foundation can change.
        /// </summary>
        void ApplyConsensus(List<TasFrameLine> segment)
        {
            if (!ConsensusFrozen || _consensus == null || segment == null)
                return;
            int total = _consensus.Length;
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && ticks > 0)
                {
                    TasButtons set = TasButtons.None;
                    TasButtons clear = TasButtons.None;
                    bool any = false;
                    int end = Mathf.Min(cursor + ticks, total);
                    for (int t = Mathf.Max(0, cursor); t < end; t++)
                    {
                        if (!_consensus[t].ButtonsLocked)
                            continue;
                        set |= _consensus[t].Set;
                        clear |= _consensus[t].Clear;
                        any = true;
                    }
                    if (any)
                        line.Buttons = (line.Buttons | set) & ~clear;
                }
                cursor += ticks;
            }
        }

        bool LineTickSpan(List<TasFrameLine> segment, int index, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (segment == null)
                return false;
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (i == index)
                {
                    start = cursor;
                    end = cursor + ticks;
                    return ticks > 0;
                }
                cursor += ticks;
            }
            return false;
        }

        bool SpanAnglesClustered(int start, int end)
        {
            if (!ConsensusFrozen || _consensus == null)
                return false;
            int first = Mathf.Max(0, start);
            int last = Mathf.Min(end, _consensus.Length);
            if (last <= first)
                return false;
            for (int t = first; t < last; t++)
                if (!_consensus[t].AnglesClustered)
                    return false;
            return true;
        }

        bool SpanHasConsensus(int start, int end)
        {
            if (_consensus == null)
                return false;
            int first = Mathf.Max(0, start);
            int last = Mathf.Min(end, _consensus.Length);
            for (int t = first; t < last; t++)
                if (_consensus[t].Locked)
                    return true;
            return false;
        }

        AngleToken NudgeMicro(AngleToken angle, double maxStep, double anchor)
        {
            double delta = (_rng.NextDouble() * 2.0 - 1.0) * maxStep;
            if (Mathf.Abs((float)delta) < 0.01f)
                delta = 0.01;
            return NudgeBy(angle, delta, anchor);
        }

        // ---- Differential momentum extrapolation ----------------------------------------------

        /// <summary>
        /// Differential Evolution projection: Xbest + 0.7 * (X1 - X2) on the resolved yaw and
        /// pitch of every line, plus one aligned framecount. The pool's disagreement is the
        /// gradient; pushing the best along it follows the trend the top runs share.
        /// </summary>
        List<TasFrameLine> MakeDifferentialCandidate()
        {
            int total = Config.EndTick - Config.StartTick;
            if (_elite.Count < 3 || total <= 0)
                return null;
            EliteMember best = _elite[0];
            float[] bestYaw;
            float[] bestPitch;
            if (!BuildTickGenes(best, total, out _, out bestYaw, out bestPitch))
                return null;

            int index1 = 1 + _rng.Next(_elite.Count - 1);
            int index2 = 1 + _rng.Next(_elite.Count - 1);
            for (int guard = 0; guard < 8 && index2 == index1; guard++)
                index2 = 1 + _rng.Next(_elite.Count - 1);
            if (index1 == index2)
                return null;

            float[] yaw1;
            float[] pitch1;
            float[] yaw2;
            float[] pitch2;
            if (!BuildTickGenes(_elite[index1], total, out _, out yaw1, out pitch1) ||
                !BuildTickGenes(_elite[index2], total, out _, out yaw2, out pitch2))
                return null;

            List<TasFrameLine> candidate = CloneAll(best.Segment);
            int cursor = 0;
            for (int i = 0; i < candidate.Count; i++)
            {
                TasFrameLine line = candidate[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && ticks > 0)
                {
                    int tick = Mathf.Clamp(cursor, 0, total - 1);
                    double yaw = bestYaw[tick] +
                                 DifferentialWeight * Mathf.DeltaAngle(yaw2[tick], yaw1[tick]);
                    double pitch = bestPitch[tick] + DifferentialWeight * (pitch1[tick] - pitch2[tick]);
                    line.Yaw = AngleToken.Absolute(yaw);
                    line.Pitch = AngleToken.Absolute(pitch);
                }
                cursor += ticks;
            }

            // Framecounts are discrete: extrapolate one bulk whose span exists in both donors,
            // and keep the total fixed when the option is on.
            OptimMutationSettings m = Config.Mutations;
            if (m.MutateFramecount)
            {
                List<int> alignedIndex = new List<int>();
                List<int> count1 = new List<int>();
                List<int> count2 = new List<int>();
                cursor = 0;
                for (int i = 0; i < candidate.Count; i++)
                {
                    TasFrameLine line = candidate[i];
                    if (line == null)
                        continue;
                    int ticks = GeometryLineTicks(line);
                    if (line.IsFramebulk && ticks > 0)
                    {
                        int c1;
                        int c2;
                        if (TryAlignedCount(_elite[index1].Segment, cursor, cursor + ticks, out c1) &&
                            TryAlignedCount(_elite[index2].Segment, cursor, cursor + ticks, out c2))
                        {
                            alignedIndex.Add(i);
                            count1.Add(c1);
                            count2.Add(c2);
                        }
                    }
                    cursor += ticks;
                }
                if (alignedIndex.Count > 0)
                {
                    int pick = _rng.Next(alignedIndex.Count);
                    int index = alignedIndex[pick];
                    int old = candidate[index].Count;
                    int wanted = old + (int)Math.Round(DifferentialWeight * (count1[pick] - count2[pick]));
                    if (wanted < 1)
                        wanted = 1;
                    if (wanted != old)
                    {
                        candidate[index].Count = wanted;
                        if (m.KeepTotalTicks)
                        {
                            if (!CompensateTotal(candidate, index, old))
                                candidate[index].Count = old;
                        }
                        else if (SegmentTickCount(candidate) != total)
                        {
                            candidate[index].Count = old;
                        }
                    }
                }
            }

            if (SegmentTickCount(candidate) != total)
                return null;
            return candidate;
        }

        bool TryAlignedCount(List<TasFrameLine> segment, int start, int end, out int count)
        {
            count = 0;
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && cursor == start && cursor + ticks == end)
                {
                    count = line.Count;
                    return true;
                }
                cursor += ticks;
            }
            return false;
        }

        // ---- Action combo mining ---------------------------------------------------------------

        /// <summary>
        /// Mines the top three elite runs for 3-5 tick input rhythms immediately before a
        /// significant acceleration spike, and keeps the unique ones for the session.
        /// </summary>
        void MineWinningCombos()
        {
            if (Config.SearchMode != OptimSearchMode.Adaptive || _elite.Count == 0)
                return;
            int total = Config.EndTick - Config.StartTick;
            int top = Math.Min(3, _elite.Count);
            for (int e = 0; e < top; e++)
            {
                EliteMember member = _elite[e];
                TasButtons[] buttons;
                if (!BuildTickGenes(member, total, out buttons, out _, out _))
                    continue;
                List<OptimSample> samples = member.Samples;
                if (samples == null)
                    continue;
                for (int i = 1; i < samples.Count; i++)
                {
                    float accel = samples[i].AccelXZ;
                    if (accel <= 0f)
                        accel = HorizontalSpeed(samples[i].Velocity) - HorizontalSpeed(samples[i - 1].Velocity);
                    if (accel < HighValueAccel)
                        continue;
                    int rel = samples[i].Tick - Config.StartTick;
                    for (int length = 3; length <= 5; length++)
                    {
                        int start = rel - length;
                        if (start < 0 || start + length > total)
                            continue;
                        TasButtons[] combo = new TasButtons[length];
                        for (int k = 0; k < length; k++)
                            combo[k] = buttons[start + k];
                        AddCombo(combo);
                    }
                }
            }
        }

        void AddCombo(TasButtons[] combo)
        {
            for (int i = 0; i < _combos.Count; i++)
            {
                WinningCombo existing = _combos[i];
                if (existing.Ticks.Length != combo.Length)
                    continue;
                bool same = true;
                for (int k = 0; k < combo.Length && same; k++)
                    if (existing.Ticks[k] != combo[k])
                        same = false;
                if (same)
                    return;
            }
            WinningCombo entry = new WinningCombo();
            entry.Ticks = combo;
            _combos.Add(entry);
            if (_combos.Count > ComboCapacity)
                _combos.RemoveAt(0);
        }

        /// <summary>
        /// Drops a mined rhythm into a flat, unprotected stretch of the route: the chosen 3-5
        /// ticks are split out and replaced with the mined button masks.
        /// </summary>
        bool TrySpliceCombo(List<TasFrameLine> segment, List<OptimSample> samples)
        {
            if (_combos.Count == 0 || segment == null || samples == null)
                return false;
            int total = Config.EndTick - Config.StartTick;
            if (total <= 0 || SegmentTickCount(segment) != total)
                return false;

            WinningCombo combo = _combos[_rng.Next(_combos.Count)];
            int length = combo.Ticks.Length;
            if (length <= 0 || length > total)
                return false;

            float[] accel = new float[total + 1];
            for (int i = 0; i < samples.Count; i++)
            {
                int rel = samples[i].Tick - Config.StartTick;
                if (rel >= 1 && rel <= total)
                    accel[rel] = samples[i].AccelXZ;
            }

            List<int> flat = new List<int>();
            for (int start = 0; start + length <= total; start++)
            {
                bool clean = true;
                for (int t = start + 1; t <= start + length; t++)
                {
                    if (Mathf.Abs(accel[t]) > FlatAccel)
                    {
                        clean = false;
                        break;
                    }
                }
                if (!clean || IsProtectedSpan(start, start + length))
                    continue;
                if (ConsensusFrozen && SpanHasConsensus(start, start + length))
                    continue;
                flat.Add(start);
            }
            if (flat.Count == 0)
                return false;

            int at = flat[_rng.Next(flat.Count)];
            List<TasFrameLine> spliced = SliceTicks(segment, 0, at);
            int cursor = at;
            for (int k = 0; k < length; k++)
            {
                TasFrameLine template = LineAtTick(segment, cursor);
                if (template == null)
                    return false;
                TasFrameLine line = template.Clone();
                line.Count = 1;
                line.Buttons = combo.Ticks[k];
                if (line.Commands != null)
                    line.Commands.Clear();
                spliced.Add(line);
                cursor++;
            }
            spliced.AddRange(SliceTicks(segment, at + length, total));
            if (SegmentTickCount(spliced) != total)
                return false;

            segment.Clear();
            segment.AddRange(spliced);
            PhaseText = "ML combo splice (" + length + " ticks)";
            return true;
        }

        TasFrameLine LineAtTick(List<TasFrameLine> segment, int tick)
        {
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && tick >= cursor && tick < cursor + ticks)
                    return line;
                cursor += ticks;
            }
            return null;
        }

        // ---- Stagnation escape -----------------------------------------------------------------

        /// <summary>Stage 1 doubles yaw, pitch and framecount steps while the route is stuck.</summary>
        int StagnationStepScale { get { return _stagnantAttempts >= MildStagnation ? 2 : 1; } }

        /// <summary>
        /// Stage 3: give up the stuck best. Prefer mutating the most spatially divergent elite
        /// run; when the pool has no distinct endpoint, force a fresh early-input burst.
        /// </summary>
        List<TasFrameLine> MakeAlternativeRouteCandidate()
        {
            int total = Config.EndTick - Config.StartTick;
            if (total <= 0)
                return null;

            float distance;
            EliteMember divergent = PickDivergentElite(out distance);
            if (divergent != null && distance > EliteNovelDistance)
            {
                for (int tries = 0; tries < BanditSelections; tries++)
                {
                    MutationOperator op;
                    if (!TrySelectOperator(out op))
                        break;
                    List<TasFrameLine> candidate = CloneAll(divergent.Segment);
                    if (!ApplyOperator(op, candidate, divergent.Samples))
                        continue;
                    if (SegmentTickCount(candidate) != total)
                        continue;
                    if (_combos.Count > 0 && _rng.NextDouble() < ComboSpliceChance)
                        TrySpliceCombo(candidate, divergent.Samples);
                    if (SegmentTickCount(candidate) != total)
                        continue;
                    _opAttempts[(int)op]++;
                    _opTotalAttempts++;
                    _operatorVersion++;
                    _pendingOperator = (int)op;
                    _pendingSourceScore = divergent.Score;
                    return candidate;
                }
            }
            return MakeExplorationBurst();
        }

        EliteMember PickDivergentElite(out float distance)
        {
            distance = 0f;
            EliteMember divergent = null;
            if (_elite.Count == 0)
                return null;
            Vector3 origin = _elite[0].EndPosition;
            if (_bestActionSamples != null && _bestActionSamples.Count > 0)
                origin = _bestActionSamples[_bestActionSamples.Count - 1].Position;
            for (int i = 0; i < _elite.Count; i++)
            {
                float d = Vector3.Distance(_elite[i].EndPosition, origin);
                if (d > distance)
                {
                    distance = d;
                    divergent = _elite[i];
                }
            }
            return divergent;
        }

        /// <summary>Forces a different early movement choice (opposite strafe, early jump).</summary>
        List<TasFrameLine> MakeExplorationBurst()
        {
            int total = Config.EndTick - Config.StartTick;
            if (total <= 0)
                return null;
            List<TasFrameLine> source = _baseline != null && SegmentTickCount(_baseline) == total
                ? _baseline
                : (_elite.Count > 0 ? _elite[0].Segment : null);
            if (source == null || SegmentTickCount(source) != total)
                return null;
            List<OptimSample> samples = ReferenceEquals(source, _baseline)
                ? _baselineActionSamples
                : (_elite.Count > 0 ? _elite[0].Samples : null);

            List<TasFrameLine> candidate = CloneAll(source);
            for (int i = 0; i < candidate.Count; i++)
            {
                TasFrameLine line = candidate[i];
                if (line == null || !line.IsFramebulk)
                    continue;
                if ((line.Buttons & TasButtons.Right) != 0)
                    line.Buttons = (line.Buttons & ~TasButtons.Right) | TasButtons.Left;
                else if ((line.Buttons & TasButtons.Left) != 0)
                    line.Buttons = (line.Buttons & ~TasButtons.Left) | TasButtons.Right;
                else
                    line.Buttons |= TasButtons.Left;

                ActionWindow window;
                if (samples != null && TryActionWindow(candidate, i, samples, out window) &&
                    IsJumpReady(samples, window.Pre))
                    line.Buttons ^= TasButtons.Jump;
                PhaseText = "ML exploration burst (fresh route)";
                return candidate;
            }
            return null;
        }

        // ---- Elite population -----------------------------------------------------------------

        EliteMember PickEliteParent()
        {
            if (_elite.Count == 0)
                return null;
            int total = 0;
            for (int i = 0; i < _elite.Count; i++)
                total += _elite.Count - i;
            int roll = _rng.Next(total);
            for (int i = 0; i < _elite.Count; i++)
            {
                int weight = _elite.Count - i;
                if (roll < weight)
                    return _elite[i];
                roll -= weight;
            }
            return _elite[_elite.Count - 1];
        }

        /// <summary>
        /// Offers a finished valid attempt to the population. A candidate enters when it beats
        /// the lowest score, or when its endpoint is somewhere the pool has not reached yet and
        /// its score is competitive; the pool is sorted by score and trimmed to capacity.
        /// </summary>
        void ConsiderElite(List<TasFrameLine> segment, double score, List<OptimSample> samples)
        {
            if (Config.SearchMode != OptimSearchMode.Adaptive)
                return;
            if (segment == null || samples == null || samples.Count == 0)
                return;
            if (double.IsNaN(score) || double.IsInfinity(score))
                return;
            if (SegmentTickCount(segment) != Config.EndTick - Config.StartTick)
                return;

            OptimSample last = samples[samples.Count - 1];
            bool novel = true;
            for (int i = 0; i < _elite.Count; i++)
            {
                if ((_elite[i].EndPosition - last.Position).sqrMagnitude <
                    EliteNovelDistance * EliteNovelDistance)
                {
                    novel = false;
                    break;
                }
            }

            if (_elite.Count >= EliteCapacity)
            {
                if (!IsBetter(score, _elite[EliteCapacity - 1].Score))
                    return;
            }
            else if (_elite.Count > 0 && !IsBetter(score, _elite[_elite.Count - 1].Score))
            {
                double band = Math.Max(1e-3, Math.Abs(_elite[_elite.Count - 1].Score) * 0.01);
                if (!(novel && score >= _elite[_elite.Count - 1].Score - band))
                    return;
            }

            // Do not store the same trajectory twice: same score and the same endpoint.
            for (int i = 0; i < _elite.Count; i++)
            {
                if (SameScore(_elite[i].Score, score) &&
                    (_elite[i].EndPosition - last.Position).sqrMagnitude < 0.01f)
                    return;
            }

            EliteMember member = new EliteMember();
            member.Score = score;
            member.Segment = CloneAll(segment);
            member.Samples = samples;
            member.EndPosition = last.Position;
            member.EndVelocity = last.Velocity;
            _elite.Add(member);
            _elite.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (_elite.Count > EliteCapacity)
                _elite.RemoveRange(EliteCapacity, _elite.Count - EliteCapacity);
            _consensusDirty = true;
            MineWinningCombos();
        }

        // ---- Temporal crossover ----------------------------------------------------------------

        /// <summary>
        /// Breeds two distinct elite parents at a random tick: the child keeps parent A up to
        /// the cut and continues with parent B from there. Slicing preserves the total tick count
        /// by splitting the bulk that straddles the cut, and the join gets a light angle
        /// smoothing so the transition is not a hard turn.
        /// </summary>
        List<TasFrameLine> TryCrossover()
        {
            int total = Config.EndTick - Config.StartTick;
            if (total < 3)
                return null;

            EliteMember a = PickEliteParent();
            EliteMember b = PickEliteParent();
            for (int guard = 0; guard < 8 && ReferenceEquals(a, b); guard++)
                b = PickEliteParent();
            if (a == null || b == null || ReferenceEquals(a, b))
                return null;
            if (SegmentTickCount(a.Segment) != total || SegmentTickCount(b.Segment) != total)
                return null;

            int cut = 1 + _rng.Next(total - 1);
            List<TasFrameLine> child = SliceTicks(a.Segment, 0, cut);
            List<TasFrameLine> tail = SliceTicks(b.Segment, cut, total);
            if (child.Count == 0 || tail.Count == 0)
                return null;
            int boundary = child.Count;
            child.AddRange(tail);
            if (SegmentTickCount(child) != total)
                return null;

            SmoothSpliceBoundary(child, boundary);
            return child;
        }

        /// <summary>Clones the part of a segment covering [from, to) ticks, splitting edge bulks.</summary>
        List<TasFrameLine> SliceTicks(List<TasFrameLine> segment, int from, int to)
        {
            List<TasFrameLine> slice = new List<TasFrameLine>();
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                int start = cursor;
                int end = cursor + ticks;
                cursor = end;

                if (ticks == 0)
                {
                    // Zero-tick markers occupy no timeline space; keep the ones that still sit
                    // inside the slice, plus a leading block at the very start of the segment.
                    if ((from == 0 && start == 0) || (start > from && start < to))
                        slice.Add(line.Clone());
                    continue;
                }

                int a = Math.Max(from, start);
                int b = Math.Min(to, end);
                if (b <= a)
                    continue;
                TasFrameLine piece = line.Clone();
                piece.Count = b - a;
                // Commands run at the start of their line: a tail piece must not repeat them.
                if (a != start && piece.Commands != null)
                    piece.Commands.Clear();
                slice.Add(piece);
            }
            return slice;
        }

        /// <summary>
        /// Light mutation at the splice: the boundary bulk turns halfway toward the previous
        /// one when both carry absolute angles, so the two parents' camera directions meet.
        /// </summary>
        void SmoothSpliceBoundary(List<TasFrameLine> segment, int boundary)
        {
            int index = boundary;
            while (index < segment.Count && (segment[index] == null || !segment[index].IsFramebulk))
                index++;
            if (index <= 0 || index >= segment.Count)
                return;
            TasFrameLine line = segment[index];
            TasFrameLine previous = segment[index - 1];
            if (previous == null || !previous.IsFramebulk)
                return;

            if (Config.Mutations.MutateYaw && line.Yaw != null && previous.Yaw != null &&
                !line.Yaw.Unchanged && !line.Yaw.IsDelta &&
                !previous.Yaw.Unchanged && !previous.Yaw.IsDelta)
            {
                double delta = Mathf.DeltaAngle((float)previous.Yaw.Value, (float)line.Yaw.Value) * 0.5;
                line.Yaw = NudgeBy(line.Yaw, delta, 0.0);
            }
            if (Config.Mutations.MutatePitch && line.Pitch != null && previous.Pitch != null &&
                !line.Pitch.Unchanged && !line.Pitch.IsDelta &&
                !previous.Pitch.Unchanged && !previous.Pitch.IsDelta)
            {
                double delta = Mathf.DeltaAngle((float)previous.Pitch.Value, (float)line.Pitch.Value) * 0.5;
                line.Pitch = NudgeBy(line.Pitch, delta, 0.0);
            }
        }
    }
}
