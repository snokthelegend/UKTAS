using System;
using System.Collections.Generic;
using System.Globalization;

namespace UltrakillTAS
{
    /// <summary>
    /// Pattern-search half of the optimizer ("sweep").
    ///
    /// An attempt costs a whole segment replay, so the attempts spent on hopeless changes are
    /// what dominates a run. Random mutation samples the entire space and mostly repeats
    /// itself; the sweep instead walks the segment one framebulk at a time and tries a fixed
    /// list of small changes around that bulk: toggle one input, nudge the angles, borrow a
    /// tick, flip one autofunc, fit the slide-jump pattern, or isolate a single tick. Every
    /// change that scores better is kept and the same bulk is retried from the new working
    /// point, which is coordinate descent: no attempt is spent on a change outside the current
    /// focus, and the focus only moves on once it is exhausted.
    ///
    /// The step size halves after a pass that finds nothing, so the search takes the coarse
    /// shape of a route first and refines it afterwards. Hybrid mode drops a burst of random
    /// candidates when a pass comes up empty, which is what leaves a local optimum.
    /// </summary>
    public sealed partial class TasOptimizer
    {
        enum SweepKind { Yaw, Pitch, Framecount, Buttons, Autofuncs, SSJ, JumpSlide, SingleTick }

        struct SweepStep
        {
            public SweepKind Kind;
            public double Delta;
            public TasButtons Buttons;
            public TasAutofuncs Autofuncs;
        }

        /// <summary>Smallest step multiplier; below this a nudge is under the script's own precision.</summary>
        const double MinSweepScale = 0.125;

        /// <summary>Random-mode attempts without an improvement that force a restart from the baseline.</summary>
        const int StagnationLimit = 400;

        /// <summary>Score difference under which two attempts count as equal (lets trims happen).</summary>
        const double NeutralEpsilon = 1e-7;

        readonly List<SweepStep> _sweepSteps = new List<SweepStep>();
        readonly List<int> _bulkScratch = new List<int>();
        int _sweepStepsFor = -1;
        int _sweepStepsVersion = -1;
        int _sweepOrdinal;
        int _sweepStep;
        int _sweepTested;
        int _sweepPasses;
        double _sweepScale = 1.0;
        int _randomBurst;
        int _structureVersion;

        /// <summary>Where the search currently is, for the telemetry line.</summary>
        public string PhaseText { get; private set; }

        /// <summary>Attempts that replaced the best segment with a simpler one at the same score.</summary>
        public int TrimmedCount { get; private set; }

        // ---- Candidate generation ---------------------------------------------------------

        List<TasFrameLine> MakeSweepCandidate()
        {
            if (Config.SearchMode == OptimSearchMode.Hybrid && _randomBurst > 0)
            {
                _randomBurst--;
                return MakeRandomCandidate();
            }

            // The sweep refines the best route found so far. Annealing walks the separate
            // working point in random mode, so it can never drag a sweep off the best route.
            List<TasFrameLine> source = _best;
            FillFramebulks(source);
            if (_bulkScratch.Count == 0)
            {
                PhaseText = "no framebulk to sweep";
                return CloneAll(source);
            }

            for (int guard = 0; guard < 4096; guard++)
            {
                if (_sweepOrdinal < 0 || _sweepOrdinal >= _bulkScratch.Count)
                {
                    if (_sweepTested > 0)
                        FinishSweepPass();
                    _sweepTested = 0;
                    _sweepOrdinal = 0;
                    _sweepStep = 0;
                    _sweepStepsFor = -1;
                }

                if (_sweepStepsFor != _sweepOrdinal || _sweepStepsVersion != _structureVersion)
                {
                    BuildSweepSteps(source, _bulkScratch[_sweepOrdinal], _bestActionSamples);
                    _sweepStepsFor = _sweepOrdinal;
                    _sweepStepsVersion = _structureVersion;
                }

                if (_sweepStep >= _sweepSteps.Count)
                {
                    _sweepStep = 0;
                    _sweepOrdinal++;
                    continue;
                }

                int lineIndex = _bulkScratch[_sweepOrdinal];
                SweepStep step = _sweepSteps[_sweepStep++];
                List<TasFrameLine> candidate = CloneAll(source);
                if (!ApplySweepStep(candidate, lineIndex, step, _bestActionSamples))
                    continue;
                _sweepTested++;
                PhaseText = "sweep bulk " + (_sweepOrdinal + 1) + "/" + _bulkScratch.Count +
                            ", variation " + _sweepStep + "/" + _sweepSteps.Count +
                            ", step x" + _sweepScale.ToString("0.###", CultureInfo.InvariantCulture) +
                            ", pass " + (_sweepPasses + 1);
                return candidate;
            }

            PhaseText = "sweep: no variation applied";
            return CloneAll(source);
        }

        void FillFramebulks(List<TasFrameLine> segment)
        {
            _bulkScratch.Clear();
            int cursor = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                int ticks = GeometryLineTicks(line);
                if (line.IsFramebulk && cursor < MutationWindowTicks)
                    _bulkScratch.Add(i);
                cursor += ticks;
            }
        }

        /// <summary>
        /// Every change the sweep may try on one framebulk, in the order it tries them. Discrete
        /// inputs come first because turning a key on or off changes the shape of the route,
        /// while an angle nudge only moves it; then the angles, the tick split, the autofuncs,
        /// and the structural patterns last.
        /// </summary>
        void BuildSweepSteps(List<TasFrameLine> segment, int lineIndex, List<OptimSample> samples)
        {
            _sweepSteps.Clear();
            if (lineIndex < 0 || lineIndex >= segment.Count)
                return;
            TasFrameLine line = segment[lineIndex];
            if (line == null || !line.IsFramebulk)
                return;
            OptimMutationSettings m = Config.Mutations;

            // Gated on the state this bulk had in the trajectory the sweep is refining. Turning an
            // input off is always tried; turning one on must be a press the game would accept.
            ActionWindow window;
            bool windowKnown = TryActionWindow(segment, lineIndex, samples, out window);

            // Force SSJ: the slide / release / jump pattern is tried before any other change on
            // every bulk the sampled state can host it, so a forced run converts the route before
            // it spends attempts on angles. The checks mirror TryMutateSSJ, so no attempt is spent
            // on a bulk it would reject.
            if (Config.ForceSSJ && line.Count >= 3 && !HasCommands(line) && windowKnown &&
                samples[window.Pre].Grounded && IsJumpReady(samples, window.First))
                AddSweep(SweepKind.SSJ, 0.0, TasButtons.None, TasAutofuncs.None);

            if (m.MutateJump &&
                ((line.Buttons & TasButtons.Jump) != 0 ||
                 (windowKnown && IsJumpReady(samples, window.Pre))))
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Jump, TasAutofuncs.None);
            if (m.MutateSlide &&
                ((line.Buttons & TasButtons.Slide) != 0 ||
                 (windowKnown && IsSlideUseful(samples, window.Pre))))
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slide, TasAutofuncs.None);
            if (m.MutateDash &&
                ((line.Buttons & TasButtons.Dash) != 0 ||
                 (windowKnown && IsDashReady(samples, window.Pre))))
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Dash, TasAutofuncs.None);
            if (m.MutateMovement)
            {
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Forward, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Back, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Left, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Right, TasAutofuncs.None);
            }

            if (m.MutateYaw)
            {
                double yawStep = Math.Max(0.01, m.YawStep * _sweepScale);
                AddSweep(SweepKind.Yaw, -2.0 * yawStep, TasButtons.None, TasAutofuncs.None);
                AddSweep(SweepKind.Yaw, -yawStep, TasButtons.None, TasAutofuncs.None);
                AddSweep(SweepKind.Yaw, yawStep, TasButtons.None, TasAutofuncs.None);
                AddSweep(SweepKind.Yaw, 2.0 * yawStep, TasButtons.None, TasAutofuncs.None);
            }
            if (m.MutatePitch)
            {
                double pitchStep = Math.Max(0.01, m.PitchStep * _sweepScale);
                AddSweep(SweepKind.Pitch, -pitchStep, TasButtons.None, TasAutofuncs.None);
                AddSweep(SweepKind.Pitch, pitchStep, TasButtons.None, TasAutofuncs.None);
            }

            if (m.MutateFramecount)
            {
                if (line.Count > 1)
                    AddSweep(SweepKind.Framecount, -1.0, TasButtons.None, TasAutofuncs.None);
                AddSweep(SweepKind.Framecount, 1.0, TasButtons.None, TasAutofuncs.None);
            }

            List<TasAutofuncs> choices = EligibleAutofuncs(line, samples, windowKnown, window);
            for (int i = 0; i < choices.Count; i++)
                AddSweep(SweepKind.Autofuncs, 0.0, TasButtons.None, choices[i]);
            // Autodash is deliberately kept out of EligibleAutofuncs: it is the only autofunc that
            // spends stamina, so it gets its own mutation switch instead of riding along.
            if (m.MutateAutoDash &&
                (((line.Autofuncs & TasAutofuncs.AutoDash) != 0) ||
                 (windowKnown && WindowHasDash(samples, window))))
                AddSweep(SweepKind.Autofuncs, 0.0, TasButtons.None, TasAutofuncs.AutoDash);

            if (m.MutateCombat)
            {
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Fire1, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Fire2, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Punch, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Whiplash, TasAutofuncs.None);
            }
            if (m.MutateWeapons)
            {
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot1, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot2, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot3, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot4, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot5, TasAutofuncs.None);
                AddSweep(SweepKind.Buttons, 0.0, TasButtons.Slot6, TasAutofuncs.None);
            }

            if (m.MutateSSJ && !Config.ForceSSJ && line.Count >= 3 && !HasCommands(line))
                AddSweep(SweepKind.SSJ, 0.0, TasButtons.None, TasAutofuncs.None);
            if (m.MutateJumpSlide && line.Count >= 2 && !HasCommands(line))
                AddSweep(SweepKind.JumpSlide, 0.0, TasButtons.None, TasAutofuncs.None);

            if (m.MutateSingleTick && line.Count >= 2 && !HasCommands(line))
            {
                AddSweep(SweepKind.SingleTick, 0.0, TasButtons.Jump, TasAutofuncs.None);
                AddSweep(SweepKind.SingleTick, 0.0, TasButtons.Slide, TasAutofuncs.None);
                AddSweep(SweepKind.SingleTick, 0.0, TasButtons.Dash, TasAutofuncs.None);
            }
        }

        void AddSweep(SweepKind kind, double delta, TasButtons buttons, TasAutofuncs autofuncs)
        {
            SweepStep step = new SweepStep();
            step.Kind = kind;
            step.Delta = delta;
            step.Buttons = buttons;
            step.Autofuncs = autofuncs;
            _sweepSteps.Add(step);
        }

        bool ApplySweepStep(List<TasFrameLine> segment, int lineIndex, SweepStep step, List<OptimSample> samples)
        {
            if (lineIndex < 0 || lineIndex >= segment.Count)
                return false;
            TasFrameLine line = segment[lineIndex];
            if (line == null || !line.IsFramebulk)
                return false;

            switch (step.Kind)
            {
                case SweepKind.Buttons:
                    line.Buttons ^= step.Buttons;
                    return true;

                case SweepKind.Autofuncs:
                    line.Autofuncs ^= step.Autofuncs;
                    return true;

                case SweepKind.Yaw:
                    line.Yaw = NudgeBy(line.Yaw, step.Delta, AngleAnchor(segment, lineIndex, true));
                    return true;

                case SweepKind.Pitch:
                    line.Pitch = NudgeBy(line.Pitch, step.Delta, AngleAnchor(segment, lineIndex, false));
                    return true;

                case SweepKind.Framecount:
                {
                    int old = line.Count;
                    int wanted = old + (int)Math.Round(step.Delta);
                    if (wanted < 1 || wanted == old)
                        return false;
                    line.Count = wanted;
                    if (Config.Mutations.KeepTotalTicks && !CompensateTotal(segment, lineIndex, old))
                    {
                        line.Count = old;
                        return false;
                    }
                    return true;
                }

                case SweepKind.SSJ:
                    return TryMutateSSJ(segment, lineIndex, samples);

                case SweepKind.JumpSlide:
                    return TryMutateJumpSlide(segment, lineIndex, samples);

                case SweepKind.SingleTick:
                    return TrySplitSingleTick(segment, lineIndex, step.Buttons, samples);
            }
            return false;
        }

        /// <summary>A whole pass found nothing: shrink the steps, and in hybrid mode kick the search.</summary>
        void FinishSweepPass()
        {
            _sweepPasses++;
            if (_sweepScale > MinSweepScale)
                _sweepScale = Math.Max(MinSweepScale, _sweepScale * 0.5);
            if (Config.SearchMode == OptimSearchMode.Hybrid)
                _randomBurst = 8 * Math.Max(1, Config.Mutations.BulksPerIteration);
        }

        /// <summary>The best segment improved: retry the bulk that produced it from the new point.</summary>
        void OnImproved()
        {
            // The guidance that produced this improvement has done its job; the next bonk seeds
            // a fresh probe and source segment.
            _geometryCandidatesLeft = 0;
            _geometrySource = null;
            _sinceImprovement = 0;
            _sweepPasses = 0;
            _sweepScale = 1.0;
            _randomBurst = 0;
            _sweepStep = 0;
            _sweepStepsFor = -1;
            _structureVersion++;
        }

        /// <summary>The best segment was replaced by an equally scoring, simpler one.</summary>
        void OnTrimmed()
        {
            _sweepStepsFor = -1;
            _structureVersion++;
        }

        // ---- Single-tick mutations ---------------------------------------------------------

        /// <summary>
        /// Splits one tick out of a multi-tick framebulk and changes that tick's input. Bulks
        /// that carry console commands are skipped: a command runs again in every part after a
        /// split, so the candidate would not mean what it says. The tick count never changes,
        /// which is what the segment splice requires.
        /// </summary>
        bool TrySplitSingleTick(List<TasFrameLine> segment, int index, TasButtons toggle, List<OptimSample> samples)
        {
            TasFrameLine source = segment[index];
            if (source == null || !source.IsFramebulk || source.Count < 2 || HasCommands(source))
                return false;

            // The isolated tick is the last one of the bulk. A press that the game would ignore
            // there is not worth spending an attempt on unless the input is being removed.
            if ((source.Buttons & toggle) == 0)
            {
                ActionWindow window;
                if (!TryActionWindow(segment, index, samples, out window))
                    return false;
                int pre = Math.Min(Math.Max(window.Offset + source.Count - 3, 0), samples.Count - 1);
                if (!PressWorks(toggle, samples, pre))
                    return false;
            }

            int isolated = SplitBulk(segment, index, source.Count - 1);
            segment[isolated].Buttons ^= toggle;
            return true;
        }

        /// <summary>True when the game would accept this press at the given sample index.</summary>
        bool PressWorks(TasButtons toggle, List<OptimSample> samples, int pre)
        {
            if (toggle == TasButtons.Jump)
                return IsJumpReady(samples, pre);
            if (toggle == TasButtons.Slide)
                return IsSlideUseful(samples, pre);
            if (toggle == TasButtons.Dash)
                return IsDashReady(samples, pre);
            return true;
        }

        /// <summary>Split-and-mutate used by random mode: a random tick gets a random change.</summary>
        bool TryMutateSingleTickRandom(List<TasFrameLine> segment, int index, List<OptimSample> samples)
        {
            TasFrameLine source = segment[index];
            if (source == null || !source.IsFramebulk || source.Count < 2 || HasCommands(source))
                return false;
            int isolated = SplitBulk(segment, index, _rng.Next(source.Count));
            MutateTickContent(segment, isolated, samples);
            return true;
        }

        /// <summary>
        /// Cuts the bulk at index so that tick number <paramref name="tick"/> stands alone, and
        /// returns the line index of that isolated tick. The head and tail keep the original
        /// buttons and angles, so only the isolated tick behaves differently.
        /// </summary>
        int SplitBulk(List<TasFrameLine> segment, int index, int tick)
        {
            TasFrameLine source = segment[index];
            int total = source.Count;
            segment.RemoveAt(index);

            int cursor = index;
            if (tick > 0)
            {
                TasFrameLine head = source.Clone();
                head.Count = tick;
                segment.Insert(cursor++, head);
            }
            int isolated = cursor;
            TasFrameLine single = source.Clone();
            single.Count = 1;
            segment.Insert(cursor++, single);
            int tail = total - tick - 1;
            if (tail > 0)
            {
                TasFrameLine rest = source.Clone();
                rest.Count = tail;
                segment.Insert(cursor, rest);
            }
            return isolated;
        }

        /// <summary>One random change to one tick: an input flag, an autofunc, or an angle.</summary>
        void MutateTickContent(List<TasFrameLine> segment, int index, List<OptimSample> samples)
        {
            OptimMutationSettings m = Config.Mutations;
            TasFrameLine line = segment[index];
            List<Func<bool>> options = new List<Func<bool>>();

            // The isolated tick's pre-state, from the same trajectory the bulk came from.
            ActionWindow window;
            bool windowKnown = TryActionWindow(segment, index, samples, out window);

            if (m.MutateJump)
                options.Add(() =>
                {
                    if ((line.Buttons & TasButtons.Jump) == 0 &&
                        !(windowKnown && IsJumpReady(samples, window.Pre)))
                        return false;
                    line.Buttons ^= TasButtons.Jump;
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
            if (m.MutateDash)
                options.Add(() =>
                {
                    if ((line.Buttons & TasButtons.Dash) == 0 &&
                        !(windowKnown && IsDashReady(samples, window.Pre)))
                        return false;
                    line.Buttons ^= TasButtons.Dash;
                    return true;
                });
            if (m.MutateMovement)
                options.Add(() =>
                {
                    line.Buttons ^= RandomFlag(TasButtons.Forward, TasButtons.Back, TasButtons.Left, TasButtons.Right);
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

            if (options.Count == 0)
                return;
            options[_rng.Next(options.Count)]();
        }

        static bool HasCommands(TasFrameLine line)
        {
            return line.Commands != null && line.Commands.Count > 0;
        }

        // ---- Angles ------------------------------------------------------------------------

        /// <summary>Fixed angle change used by the sweep. A '-' column becomes absolute at its anchor.</summary>
        static AngleToken NudgeBy(AngleToken angle, double delta, double baseValue)
        {
            AngleToken copy = angle.Clone();
            if (copy.Unchanged)
            {
                copy.Unchanged = false;
                copy.IsDelta = false;
                copy.Value = baseValue + delta;
            }
            else
            {
                copy.Value += delta;
            }
            // Raw is what ToPipeLine() writes, so it must follow Value or Apply Best would
            // save the old angle while the in-memory candidate used the new one.
            copy.Raw = copy.IsDelta
                ? "d" + (copy.Value >= 0 ? "+" : string.Empty) + AngleToken.Format(copy.Value)
                : AngleToken.Format(copy.Value);
            return copy;
        }

        /// <summary>
        /// Random angle change in the style of bxt-rs: almost always a small step, rarely a big
        /// jump. The small steps are what actually climbs; the rare jump is what escapes a
        /// basin without needing a random restart of the whole segment.
        /// </summary>
        AngleToken NudgeRandom(AngleToken angle, float maxStep, double baseValue)
        {
            double delta;
            if (_rng.NextDouble() < 0.01)
                delta = (_rng.NextDouble() * 2.0 - 1.0) * 90.0;
            else
                delta = (_rng.NextDouble() * 2.0 - 1.0) * maxStep;
            if (delta > -0.01 && delta < 0.01)
                delta = delta < 0 ? -0.01 : 0.01;
            return NudgeBy(angle, delta, baseValue);
        }

        // ---- Random-mode helpers -----------------------------------------------------------

        /// <summary>0..count-1 in random order, so a mutation that cannot apply never wastes the attempt.</summary>
        int[] ShuffledOrder(int count)
        {
            int[] order = new int[count];
            for (int i = 0; i < count; i++)
                order[i] = i;
            for (int i = count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                int swap = order[i];
                order[i] = order[j];
                order[j] = swap;
            }
            return order;
        }

        /// <summary>A random tick-count change on one bulk, borrowing ticks so the length holds.</summary>
        bool MutateFramecount(List<TasFrameLine> segment, int index, int stepScale = 1)
        {
            OptimMutationSettings m = Config.Mutations;
            TasFrameLine line = segment[index];
            int old = line.Count;
            int step = m.FramecountStep * Math.Max(1, stepScale);
            int next = old + SignedStep(step);
            if (next < 1)
                next = 1;
            if (next == old)
                return false;
            line.Count = next;
            if (m.KeepTotalTicks && !CompensateTotal(segment, index, old))
            {
                line.Count = old;
                return false;
            }
            return true;
        }

        static bool SameScore(double a, double b)
        {
            return Math.Abs(a - b) <= NeutralEpsilon;
        }

        /// <summary>
        /// How much input a segment carries. A same-score candidate with a lower number replaces
        /// the best one, so redundant presses are removed during the search instead of needing a
        /// separate pass over the whole route (bxt-rs does this in its "minimize" step).
        /// </summary>
        static int Complexity(List<TasFrameLine> segment)
        {
            int total = 0;
            for (int i = 0; i < segment.Count; i++)
            {
                TasFrameLine line = segment[i];
                if (line == null)
                    continue;
                if (line.IsFramebulk)
                {
                    total += CountBits((ulong)line.Buttons);
                    total += CountBits((ulong)line.Autofuncs) * 2;
                }
                total += (line.Commands != null ? line.Commands.Count : 0) * 3;
                if (!line.IsFramebulk)
                    total += 2;
            }
            return total;
        }

        static int CountBits(ulong value)
        {
            int bits = 0;
            while (value != 0)
            {
                value &= value - 1;
                bits++;
            }
            return bits;
        }
    }
}
