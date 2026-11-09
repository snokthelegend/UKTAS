using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>Optimizer tab: window controls, objective and constraints, mutations, telemetry, apply/save.</summary>
    public sealed partial class GuiManager
    {
        OptimConfig _optimConfig;
        OptimBoxSet _optimBoxes;
        TasOptimizer _optimizer;
        List<OptimSample> _optimPushedBaseline, _optimPushedBest, _optimPushedCandidate;
        bool _optimPathPushed;
        int _optimPathVersion = -1;
        string _optimStartText = "0";
        string _optimEndText = "0";
        string _optimMutateEndText = "0";
        string _optimConstraintValue = "0";
        int _optimConstraintIndex = -1;
        readonly string[] _optimObjectiveLabels =
        {
            "Reach Goal Box", "Position X", "Position Y", "Position Z", "Velocity X", "Velocity Y",
            "Velocity Z", "Speed XZ", "Speed 3D", "Distance to Point", "SSJ Boost",
            "Player Health (hp)", "Hard Damage", "Net Health Gained (hp_delta)"
        };
        readonly OptimObjective[] _optimObjectives =
        {
            OptimObjective.ReachGoalFastest, OptimObjective.PosX, OptimObjective.PosY, OptimObjective.PosZ,
            OptimObjective.VelX, OptimObjective.VelY, OptimObjective.VelZ, OptimObjective.SpeedXZ,
            OptimObjective.Speed3D, OptimObjective.DistanceToGoal, OptimObjective.SSJ,
            OptimObjective.Health, OptimObjective.HardDamage, OptimObjective.HealthDelta
        };

        void EnsureOptimizerState()
        {
            if (_optimConfig != null) return;
            _optimConfig = new OptimConfig();
            _optimBoxes = new OptimBoxSet();
            _optimConfig.Mutations.MutateCombat = false;
            _optimConfig.Mutations.MutateWeapons = false;
        }

        /// <summary>No engine runner is connected yet, so Start reports that instead of running.</summary>
        static List<OptimSample> UnconnectedRunner(List<TasFrameLine> candidate, int startTick, int endTick)
        {
            return null;
        }

        int ParseTickField(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        void DrawOptimizerTab()
        {
            EnsureOptimizerState();
            DrawOptimizerWindow();
            DrawOptimizerObjective();
            DrawOptimizerMutations();
            DrawOptimizerGeometry();
            DrawOptimizerBoxes();
            DrawOptimizerControl();
        }

        /// <summary>Feeds the 3-path overlay and the spatial boxes to TrajectoryManager every frame.</summary>
        /// <remarks>Called from Plugin's frame loop, not only from OnGUI, so the current best path is
        /// available in freecam and while the TAS window is hidden.</remarks>
        public void PushOptimizerOverlay()
        {
            if (_plugin == null || _plugin.Trajectory == null || _optimBoxes == null)
                return;
            _plugin.Trajectory.SetOptimizerBoxes(_optimBoxes);
            _plugin.Trajectory.SetOptimizerGeometry(_optimizer != null ? _optimizer.LastGeometry : null,
                                                      _optimConfig != null && _optimConfig.GeometryShowWhiskers);
            if (_optimizer == null)
            {
                if (_optimPathPushed)
                {
                    _plugin.Trajectory.SetOptimizerPaths(null, null, null);
                    _optimPathPushed = false;
                    _optimPushedBaseline = _optimPushedBest = _optimPushedCandidate = null;
                    _optimPathVersion = _plugin.Trajectory.OptimizerPathVersion;
                }
                return;
            }
            List<OptimSample> baseline = _optimizer.BaselineSamples;
            List<OptimSample> best = _optimizer.BestSamples;
            List<OptimSample> candidate = _optimizer.LastCandidateSamples;
            int pathVersion = _plugin.Trajectory.OptimizerPathVersion;
            if (_optimPathPushed && pathVersion == _optimPathVersion &&
                ReferenceEquals(baseline, _optimPushedBaseline) &&
                ReferenceEquals(best, _optimPushedBest) && ReferenceEquals(candidate, _optimPushedCandidate))
                return;
            _plugin.Trajectory.SetOptimizerPaths(PositionsOf(baseline), PositionsOf(best), PositionsOf(candidate));
            _optimPushedBaseline = baseline;
            _optimPushedBest = best;
            _optimPushedCandidate = candidate;
            _optimPathVersion = _plugin.Trajectory.OptimizerPathVersion;
            _optimPathPushed = true;
        }

        void ClearOptimizerPaths()
        {
            if (_optimizer != null)
                _optimizer.ClearPaths();
            _optimPushedBaseline = _optimPushedBest = _optimPushedCandidate = null;
            _optimPathPushed = false;
            _optimPathVersion = -1;
            if (_plugin != null && _plugin.Trajectory != null)
                _plugin.Trajectory.ClearOptimizerPaths();
            if (_plugin != null)
                _plugin.Toast("Optimizer paths cleared.", false);
        }

        static List<Vector3> PositionsOf(List<OptimSample> samples)
        {
            if (samples == null)
                return null;
            List<Vector3> points = new List<Vector3>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
                points.Add(samples[i].Position);
            return points;
        }

        int _optimBoxKind;
        string _optimBoxStep = "0.5";
        readonly string[] _optimBoxKindLabels = { "Goal (green)", "Keep-out (red)", "Waypoint (yellow)" };

        /// <summary>
        /// Box placement: "Place at Crosshair" raycasts from the active camera (the freecam while
        /// flying) and drops a 3-unit box on the hit point. Each box row nudges its center and size.
        /// </summary>
        void DrawOptimizerBoxes()
        {
            BeginCard("Spatial boxes (freecam)");
            _optimBoxKind = ChoiceRow(_optimBoxKind, _optimBoxKindLabels,
                new[] { "Reach this box to win (earliest / fastest entry).",
                        "Any tick inside invalidates the attempt.",
                        "The path must touch this box at least once." }, 0f);
            GUILayout.BeginHorizontal();
            if (Btn("Place at Crosshair", "Raycast from the camera center and drop a 3-unit box on the hit (or 10 units ahead).", GUILayout.Width(180)))
                PlaceBoxAtCrosshair((OptimBoxKind)_optimBoxKind);
            Lbl("Nudge step", "Distance per nudge button.", GUILayout.Width(80));
            _optimBoxStep = Field(_optimBoxStep, "Nudge distance.", GUILayout.Width(60));
            GUILayout.EndHorizontal();

            float step;
            if (!float.TryParse(_optimBoxStep, NumberStyles.Float, CultureInfo.InvariantCulture, out step) || step <= 0f)
                step = 0.5f;

            for (int i = 0; i < _optimBoxes.Boxes.Count; i++)
            {
                OptimBox box = _optimBoxes.Boxes[i];
                Vector3 c = box.Center;
                Vector3 s = box.Size;
                GUILayout.BeginHorizontal();
                box.Enabled = Tog(box.Enabled, box.Name, "Show and use this box.");
                GUILayout.Label("c " + c.x.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                                c.y.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                                c.z.ToString("0.#", CultureInfo.InvariantCulture));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                Vector3 delta = Vector3.zero;
                if (Btn("-X", "Move center -X.", GUILayout.Width(40))) delta.x -= step;
                if (Btn("+X", "Move center +X.", GUILayout.Width(40))) delta.x += step;
                if (Btn("-Y", "Move center -Y.", GUILayout.Width(40))) delta.y -= step;
                if (Btn("+Y", "Move center +Y.", GUILayout.Width(40))) delta.y += step;
                if (Btn("-Z", "Move center -Z.", GUILayout.Width(40))) delta.z -= step;
                if (Btn("+Z", "Move center +Z.", GUILayout.Width(40))) delta.z += step;
                if (Btn("Smaller", "Shrink the box by one step on each axis.", GUILayout.Width(70)))
                    s -= Vector3.one * step;
                if (Btn("Bigger", "Grow the box by one step on each axis.", GUILayout.Width(70)))
                    s += Vector3.one * step;
                if (BtnDanger("X", "Remove this box.", GUILayout.Width(26)))
                {
                    _optimBoxes.Remove(box);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
                if (delta != Vector3.zero || s != box.Size)
                {
                    Vector3 nc = c + delta;
                    Vector3 half = new Vector3(Mathf.Max(0.05f, s.x), Mathf.Max(0.05f, s.y), Mathf.Max(0.05f, s.z)) * 0.5f;
                    box.SetCorners(nc - half, nc + half);
                }
            }
            EndCard();
        }

        /// <summary>Geometry awareness: switch, guidance strength, last-obstacle readout and probe overlay.</summary>
        void DrawOptimizerGeometry()
        {
            BeginCard("Geometry awareness");
            _optimConfig.GeometryAwareness = Tog(_optimConfig.GeometryAwareness,
                "Enable Obstacle & Corner Awareness (Smart Steering)",
                "After a wall bonk, probe the static map around the impact and steer the next candidates around the corner or doorway instead of rolling blind yaw.");
            _optimConfig.GeometryGuidanceBias = Slide(_optimConfig.GeometryGuidanceBias, 0.1f, 1.0f,
                "Geometry Guidance Bias: 1.0 always tries a clearance-guided candidate after a bonk; 0.1 mostly keeps exploring.",
                GUILayout.Width(220));
            _optimConfig.GeometryShowWhiskers = Tog(_optimConfig.GeometryShowWhiskers,
                "Show Geometry Probe Whiskers",
                "Draw the last attempt's 360-degree probe fan: red = wall hit, bright green = chosen clearance, yellow = corner/doorway threshold.");

            OptimGeometryProbe probe = _optimizer != null ? _optimizer.LastGeometry : null;
            if (probe != null && probe.HasImpact)
            {
                Hint("Last Obstacle: Wall at Tick " + probe.Tick + " | Clearance: " + probe.ClearanceLabel() +
                     " | Distance to Corner: " + (probe.CornerFound
                         ? probe.CornerDistance.ToString("0.0", CultureInfo.InvariantCulture) + "m"
                         : "none"));
                Hint("Target: " + (probe.GoalDefined
                    ? (probe.GoalVisible ? "goal box visible around the edge" : "goal box blocked")
                    : "movement axis") +
                    " | Impact: " + probe.ImpactPosition.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                    probe.ImpactPosition.y.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                    probe.ImpactPosition.z.ToString("0.0", CultureInfo.InvariantCulture));
                Hint("Vertical clearance: floor " +
                     (probe.FloorHit ? probe.FloorDistance.ToString("0.0", CultureInfo.InvariantCulture) + "m" : "open") +
                     " | ceiling " +
                     (probe.CeilingHit ? probe.CeilingDistance.ToString("0.0", CultureInfo.InvariantCulture) + "m" : "open") +
                     " | Speed priority: " + _optimConfig.SpeedPriority);
            }
            else
            {
                Hint("No wall bonk detected yet. Run the optimizer; a >60% horizontal speed drop under movement input is probed automatically.");
            }
            if (!_optimConfig.GeometryAwareness)
                Hint("Smart steering is off: mutation uses the sweep/random generator only.");
            EndCard();
        }

        void PlaceBoxAtCrosshair(OptimBoxKind kind)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                _plugin.Toast("No camera to raycast from.", true);
                return;
            }
            Ray ray;
            using (_plugin.ViewPose())
                ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            RaycastHit hit;
            Vector3 point = Physics.Raycast(ray, out hit, 500f) ? hit.point : ray.origin + ray.direction * 10f;
            Vector3 half = Vector3.one * 1.5f;
            _optimBoxes.Add(kind, point - half, point + half);
        }

        // Marker sync state: the script last scanned, the marker ticks last applied, and the next scan time.
        TasScript _optimMarkerScript;
        bool _optimMarkerValid;
        int _optimMarkerStart = -1;
        int _optimMarkerEnd = -1;
        float _optimMarkerNextScan;

        /// <summary>
        /// Keeps the Start / End Tick fields in step with the script's optim_start / optim_end markers.
        /// Rescans at most 4 times a second (and at once when the loaded script changes) and only
        /// overwrites the fields when the resolved marker ticks differ from the last applied pair,
        /// so manual edits survive until the markers move. No markers: fields stay as typed.
        /// </summary>
        void SyncOptimizerMarkerFields()
        {
            if (_engine == null || Event.current == null || Event.current.type != EventType.Layout)
                return;
            TasScript script = _engine.Script;
            bool scriptChanged = !ReferenceEquals(script, _optimMarkerScript);
            float now = Time.realtimeSinceStartup;
            if (!scriptChanged && now < _optimMarkerNextScan)
                return;
            _optimMarkerNextScan = now + 0.25f;
            if (scriptChanged)
            {
                _optimMarkerScript = script;
                _optimMarkerValid = false;
            }
            int markerStart, markerEnd;
            if (script == null || !TasOptimizer.ResolveMarkerTicks(script, out markerStart, out markerEnd))
            {
                _optimMarkerValid = false;
                return;
            }
            if (_optimMarkerValid && markerStart == _optimMarkerStart && markerEnd == _optimMarkerEnd)
                return;
            _optimMarkerValid = true;
            _optimMarkerStart = markerStart;
            _optimMarkerEnd = markerEnd;
            _optimStartText = markerStart.ToString(CultureInfo.InvariantCulture);
            _optimEndText = markerEnd.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(_optimMutateEndText) || _optimMutateEndText == "0")
                _optimMutateEndText = _optimEndText;
        }

        void DrawOptimizerWindow()
        {
            SyncOptimizerMarkerFields();
            BeginCard("Optimization window");
            if (string.IsNullOrEmpty(_optimMutateEndText) || _optimMutateEndText == "0")
                _optimMutateEndText = _optimEndText;

            GUILayout.BeginHorizontal();
            Lbl("Start Tick", "First tick of the segment to optimize.", GUILayout.Width(90));
            _optimStartText = Field(_optimStartText, "Start tick.", GUILayout.Width(90));
            if (Btn("Current", "Use the current tick as the start.", GUILayout.Width(80)))
                _optimStartText = _engine.CurrentTick.ToString(CultureInfo.InvariantCulture);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Mutate Until Tick", "Mutations are confined to Start Tick..this tick.", GUILayout.Width(110));
            _optimMutateEndText = Field(_optimMutateEndText, "End of the mutation window.", GUILayout.Width(90));
            if (Btn("Current", "Use the current tick as the mutation end.", GUILayout.Width(80)))
                _optimMutateEndText = _engine.CurrentTick.ToString(CultureInfo.InvariantCulture);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Evaluate At Tick", "Simulation and scoring continue to this tick (exclusive). The baseline frames after the mutation window are played unchanged.", GUILayout.Width(110));
            _optimEndText = Field(_optimEndText, "Evaluation tick.", GUILayout.Width(90));
            if (Btn("Current", "Use the current tick as the evaluation end.", GUILayout.Width(80)))
                _optimEndText = _engine.CurrentTick.ToString(CultureInfo.InvariantCulture);
            GUILayout.EndHorizontal();

            if (Btn("Sync Eval to Mutate End", "Set Evaluate At Tick = Mutate Until Tick for standard 1:1 optimization.", GUILayout.Width(210)))
                _optimEndText = _optimMutateEndText;

            GUILayout.Label("Rollback");
            _optimConfig.RollbackFullReplay = ChoiceRow(_optimConfig.RollbackFullReplay ? 1 : 0,
                new[] { "Savestate at Start", "Full FFF Replay" },
                new[] { "Restore a master savestate taken at the start tick (fast).",
                        "Replay from tick 0 in headless FFF before every attempt (slow, no savestates)." }, 0f) == 1;

            GUILayout.Label("Execution");
            _optimConfig.UseFastForwardHeadless = ChoiceRow(_optimConfig.UseFastForwardHeadless ? 1 : 0,
                new[] { "Visual FF", "Headless FFF" },
                new[] { "Render each attempt so you can watch it.", "Disable 3D cameras and batch physics for throughput." }, 0f) == 1;
            EndCard();
        }

        void DrawOptimizerObjective()
        {
            BeginCard("Objective and constraints");
            int objIndex = Array.IndexOf(_optimObjectives, _optimConfig.Objective);
            if (objIndex < 0) objIndex = 0;
            GUILayout.BeginHorizontal();
            Lbl("Target", "What the optimizer tries to improve.", GUILayout.Width(90));
            if (Btn(_optimObjectiveLabels[objIndex] + "  ▾", "Click to cycle the target variable.", GUILayout.Width(220)))
            {
                OptimObjective next = _optimObjectives[(objIndex + 1) % _optimObjectives.Length];
                _optimConfig.Objective = next;
                // Match the direction the new target is normally used with; the toggle below
                // still overrides it for the current run.
                if (next == OptimObjective.HardDamage)
                    _optimConfig.Direction = OptimDirection.Minimize;
                else if (next == OptimObjective.Health || next == OptimObjective.HealthDelta)
                    _optimConfig.Direction = OptimDirection.Maximize;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Direction", "Maximize or minimize the target.", GUILayout.Width(90));
            _optimConfig.Direction = ChoiceRow((int)_optimConfig.Direction,
                new[] { "Maximize", "Minimize" }, new[] { "Bigger is better.", "Smaller is better." }, 0f) == 1
                ? OptimDirection.Minimize : OptimDirection.Maximize;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Lbl("Speed priority", "Which speed axis the search should favour.", GUILayout.Width(90));
            _optimConfig.SpeedPriority = (OptimSpeedPriority)ChoiceRow((int)_optimConfig.SpeedPriority,
                new[] { "Balanced", "Horizontal", "Vertical" },
                new[] { "Score the full 3D velocity vector; adaptive credit stays horizontal.",
                        "Weight horizontal speed and protect horizontal acceleration zones.",
                        "Weight vertical speed, protect slam/jump acceleration, and let geometry guidance add vertical actions." }, 0f);
            GUILayout.EndHorizontal();
            Hint("Speed priority applies to Speed 3D, Reach Goal entry ties, adaptive credit, and geometry-guided actions. Speed XZ and numeric constraints keep their raw values.");

            GUILayout.Label("Evaluation scope");
            _optimConfig.Scope = ChoiceRow((int)_optimConfig.Scope,
                new[] { "Last Frame Only", "Peak Across All Frames" },
                new[] { "Judge the final tick of the segment.", "Judge the best value on any tick (e.g. apex height)." }, 0f) == 1
                ? OptimScope.AllFrames : OptimScope.LastFrame;

            GUILayout.Label("Constraints");
            for (int i = 0; i < _optimConfig.Constraints.Count; i++)
            {
                OptimConstraint c = _optimConfig.Constraints[i];
                GUILayout.BeginHorizontal();
                if (Btn(TasOptimizer.VariableName(c.Variable), "Cycle variable.", GUILayout.Width(110)))
                    c.Variable = NextVariable(c.Variable);
                if (Btn(OpText(c.Op), "Cycle operator (<, <=, >, >=).", GUILayout.Width(44)))
                    c.Op = (OptimOperator)(((int)c.Op + 1) % 4);
                if (Btn(c.Value.ToString("0.###", CultureInfo.InvariantCulture), "Click to edit: select row, type a value below.", GUILayout.Width(70)))
                    _optimConstraintIndex = i;
                if (Btn(c.Scope == OptimScope.LastFrame ? "Last" : "All", "Toggle last frame / all frames.", GUILayout.Width(44)))
                    c.Scope = c.Scope == OptimScope.LastFrame ? OptimScope.AllFrames : OptimScope.LastFrame;
                if (BtnDanger("X", "Remove this constraint.", GUILayout.Width(26)))
                {
                    _optimConfig.Constraints.RemoveAt(i);
                    _optimConstraintIndex = -1;
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (_optimConstraintIndex >= 0 && _optimConstraintIndex < _optimConfig.Constraints.Count)
            {
                _optimConstraintValue = Field(_optimConstraintValue, "Value for the selected constraint.", GUILayout.Width(90));
                double parsed;
                if (double.TryParse(_optimConstraintValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    _optimConfig.Constraints[_optimConstraintIndex].Value = parsed;
            }
            if (Btn("Add constraint", "Add a row: pos.y < 0 (Last Frame).", GUILayout.Width(120)))
            {
                _optimConfig.Constraints.Add(new OptimConstraint());
                _optimConstraintIndex = _optimConfig.Constraints.Count - 1;
                _optimConstraintValue = "0";
            }
            GUILayout.EndHorizontal();
            EndCard();
        }

        void DrawOptimizerMutations()
        {
            OptimMutationSettings m = _optimConfig.Mutations;

            BeginCard("Search strategy");
            _optimConfig.SearchMode = (OptimSearchMode)ChoiceRow((int)_optimConfig.SearchMode,
                new[] { "Sweep + Random", "Sweep Only", "Random Only", "Adaptive ML" },
                new[] { "Test one framebulk at a time; add random candidates when a pass finds nothing.",
                        "Deterministic sweep only. Repeatable, and the best way to polish a route.",
                        "Random mutations only. The old behaviour: samples the whole segment.",
                        "Learn which mutations score: a UCB1 bandit picks the operator, an elite pool breeds by crossover." }, 0f);
            _optimConfig.TrimRedundantInputs = Tog(_optimConfig.TrimRedundantInputs, "Trim Redundant Inputs",
                "Accept a candidate at the same score when it presses fewer keys (BXT's optimizer minimize step).");
            if (_optimConfig.SearchMode == OptimSearchMode.Adaptive)
            {
                _optimConfig.AdaptiveRouteRetryLimit = Mathf.RoundToInt(Slide(_optimConfig.AdaptiveRouteRetryLimit,
                    1f, 30f, "Generations without a new best before loading the current best and mutating from there.",
                    GUILayout.Width(260)));
                Hint("Adaptive ML: a UCB1 bandit concentrates attempts on the operators that score, and 40% of generations splice two elite parents (early ticks of one, exit ticks of the other).");
            }
            else if (_optimConfig.SearchMode == OptimSearchMode.Random)
                Hint("Random mode samples the whole segment; every attempt is a fresh roll.");
            else
                Hint("Sweep retries the same framebulk after each improvement, then moves on. Steps halve after a pass with no gain.");
            EndCard();

            BeginCard("Mutations");
            m.MutateYaw = Tog(m.MutateYaw, "Mutate Yaw", "Change yaw on framebulks.");
            if (m.MutateYaw) m.YawStep = Slide(m.YawStep, 0.1f, 25f, "Max yaw change per attempt (deg).", GUILayout.Width(220));
            m.MutatePitch = Tog(m.MutatePitch, "Mutate Pitch", "Change pitch on framebulks.");
            if (m.MutatePitch) m.PitchStep = Slide(m.PitchStep, 0.1f, 25f, "Max pitch change per attempt (deg).", GUILayout.Width(220));
            m.MutateFramecount = Tog(m.MutateFramecount, "Mutate Framecount / Repeats", "Change tick counts on framebulks.");
            if (m.MutateFramecount)
            {
                m.FramecountStep = Mathf.RoundToInt(Slide(m.FramecountStep, 1f, 15f, "Max tick change.", GUILayout.Width(220)));
                m.KeepTotalTicks = Tog(m.KeepTotalTicks, "Keep total segment ticks", "Borrow ticks from another bulk so the segment length stays fixed.");
            }
            m.MutateMovement = Tog(m.MutateMovement, "Mutate Movement Keys (WASD / flrb)", "Toggle movement keys in field 2.");
            m.MutateDash = Tog(m.MutateDash, "Mutate Dash (d)", "Toggle dash in field 2.");
            m.MutateSlide = Tog(m.MutateSlide, "Mutate Slide (s)", "Toggle slide in field 2.");
            m.MutateJump = Tog(m.MutateJump, "Mutate Jump (j)", "Toggle jump in field 3.");
            m.MutateSSJ = Tog(m.MutateSSJ, "Mutate Slide-Stop Jump (SSJ)",
                "Split a 3+ tick bulk into slide / wait one tick / jump. The jump must be grounded and 1-3 frames after StopSlide.");
            _optimConfig.ForceSSJ = Tog(_optimConfig.ForceSSJ, "Force SSJ (fill the route)",
                "Spend the attempts on the slide / release / jump pattern first, and only on bulks the sampled frames can turn into an SSJ. Works even when the SSJ mutation is off. In Adaptive ML it is one of the learned operators.");
            m.MutateJumpSlide = Tog(m.MutateJumpSlide, "Mutate Jump-Slide (Dive)",
                "Split a bulk into a grounded jump tick then a slide tick. The game only starts an air slide right after a jump.");
            m.MutateAutoJump = Tog(m.MutateAutoJump, "Mutate AutoJump (j)",
                "Toggle autojump independently in field 1.");
            m.MutateAutoSlide = Tog(m.MutateAutoSlide, "Mutate AutoSlide (s)",
                "Toggle autoslide independently in field 1.");
            m.MutateAutoDash = Tog(m.MutateAutoDash, "Mutate AutoDash (d)",
                "Toggle autodash independently; it is the only autofunc that spends stamina.");
            m.MutateGroundSlam = Tog(m.MutateGroundSlam, "Mutate Auto Ground Slam (g)",
                "Toggle the auto ground-slam autofunc independently in field 1.");
            m.MutateSSJMacro = Tog(m.MutateSSJMacro, "Mutate SSJ Macro (m)",
                "Toggle the SSJ macro autofunc independently in field 1.");
            m.MutateCoinAim = Tog(m.MutateCoinAim, "Mutate Coin Aim (c)",
                "Toggle the coin aim autofunc independently in field 1.");
            m.MutateWhipAssist = Tog(m.MutateWhipAssist, "Mutate Whip Assist (w)",
                "Toggle the whiplash aim assist autofunc independently in field 1.");
            m.MutateCoinThrow = Tog(m.MutateCoinThrow, "Mutate Coin Throw (t)",
                "Toggle the first-tick coin throw autofunc independently. Off by default.");
            m.MutateDeadcoin = Tog(m.MutateDeadcoin, "Mutate Deadcoin (e)",
                "Toggle the first-tick deadcoin autofunc independently. Off by default.");
            m.MutateSingleTick = Tog(m.MutateSingleTick, "Mutate Single Ticks",
                "Split a 2+ tick bulk and change one tick of it. Bulks with commands are skipped.");
            m.MutateCombat = Tog(m.MutateCombat, "Mutate Combat (1, 2, p, v)", "Off by default.");
            m.MutateWeapons = Tog(m.MutateWeapons, "Mutate Weapon Switches", "Off by default.");
            _optimConfig.ProjectileAwareness = Tog(_optimConfig.ProjectileAwareness,
                "Lock Shots to Airborne Projectiles/Coins", "Aim firing frames at the tracked grenade or coin.");
            m.BulksPerIteration = Mathf.RoundToInt(Slide(m.BulksPerIteration, 1f, 4f, "Framebulks mutated per attempt.", GUILayout.Width(220)));
            Hint("Sweep focuses one framebulk per attempt; random mode uses the count above.");
            EndCard();
        }

        void DrawOptimizerControl()
        {
            BeginCard("Control and telemetry");
            bool running = _engine.OptimizerRunning || (_optimizer != null && _optimizer.Running);
            GUILayout.BeginHorizontal();
            if (!running && BtnPrimary("START OPTIMIZER", "Begin the brute-force search on the window above.", GUILayout.Width(180)))
                StartOptimizer();
            if (running && BtnDanger("STOP OPTIMIZER", "Stop the search and keep the best found.", GUILayout.Width(180)))
                _engine.StopOptimizerRun();
            if (Btn("Clear Paths", "Clear the baseline, current best and live candidate paths from the 3D view.", GUILayout.Width(100)))
                ClearOptimizerPaths();
            GUILayout.EndHorizontal();

            int windowStart = _optimizer != null ? _optimizer.Config.StartTick : ParseTickField(_optimStartText, 0);
            int windowEval = _optimizer != null ? _optimizer.Config.EndTick : ParseTickField(_optimEndText, 0);
            int windowMutate = _optimizer != null
                ? _optimizer.EffectiveMutationEndTick
                : ParseTickField(_optimMutateEndText, windowEval);
            if (windowMutate <= windowStart || windowMutate > windowEval)
                windowMutate = windowEval;
            if (windowEval > windowStart)
                GUILayout.Label("Window: Mutating Ticks " + windowStart + ".." + windowMutate +
                                " (" + (windowMutate - windowStart) + " ticks) | Evaluating at Tick " +
                                windowEval + " (+" + (windowEval - windowMutate) + " lookahead)");
            DrawOptimizerHealthTelemetry();

            if (_engine.OptimizerRunning)
            {
                GUILayout.Label(_engine.OptimizerSeeking
                    ? "Status: fast-forwarding to start tick " + _optimConfig.StartTick + " (tick " + _engine.CurrentTick + ")"
                    : "Status: attempt " + (_optimizer != null ? _optimizer.Iterations : 0) + " on ticks " +
                      _optimConfig.StartTick + ".." + _optimConfig.EndTick + " (tick " + _engine.CurrentTick + ")");
            }

            if (_optimizer != null)
            {
                float secs = Mathf.Max(0.001f, Time.realtimeSinceStartup - _optimStartedAt);
                double rate = _optimizer.Iterations / secs;
                double invalidPct = _optimizer.Iterations > 0 ? 100.0 * _optimizer.InvalidCount / _optimizer.Iterations : 0.0;
                GUILayout.Label("Speed: " + rate.ToString("0.0", CultureInfo.InvariantCulture) + " it/s (" +
                                invalidPct.ToString("0", CultureInfo.InvariantCulture) + "% invalid) | Attempts: " + _optimizer.Iterations);
                if (_optimConfig.SearchMode == OptimSearchMode.Adaptive)
                {
                    GUILayout.Label("ML Strategy: Generation " + _optimizer.Generation + " | Pop: " +
                                    _optimizer.EliteCount + "/" + TasOptimizer.EliteCapacity);
                    GUILayout.Label("Top Operators: " + _optimizer.TopOperatorLine);
                    string consensus = "ML Consensus: " + _optimizer.ConsensusPercent +
                                       "% of route locked by elite agreement";
                    if (!_optimizer.ConsensusFrozen && _optimizer.EliteCount >= 2)
                        consensus += " (thawed)";
                    GUILayout.Label(consensus);
                    GUILayout.Label("Active Learning: Differential Trend Extrapolation " +
                                    (_optimizer.DifferentialActive ? "active" : "idle") + " | " +
                                    _optimizer.WinningComboCount + " Combos mined");
                    GUILayout.Label("Stagnation: " + _optimizer.StagnantAttempts + "/100 attempts");
                    if (_optimizer.StagnationPivotActive)
                        GUILayout.Label("[STAGNATION: PIVOTING TO ALTERNATIVE ROUTE]");
                    if (!string.IsNullOrEmpty(_optimizer.FailureStatusLine))
                        GUILayout.Label(_optimizer.FailureStatusLine);
                }
                if (!string.IsNullOrEmpty(_optimizer.PhaseText) || _optimizer.TrimmedCount > 0)
                    Hint("Phase: " + (string.IsNullOrEmpty(_optimizer.PhaseText) ? "baseline" : _optimizer.PhaseText) +
                         (_optimizer.TrimmedCount > 0 ? "  |  trimmed: " + _optimizer.TrimmedCount : string.Empty));
                DrawOptimizerBestBlock();
                foreach (string line in _optimizer.Log)
                    Hint(line);
            }

            GUILayout.BeginHorizontal();
            bool hasBest = _optimizer != null && _optimizer.BestSegment != null && _optimizer.BestSegment.Count > 0;
            if (hasBest && Btn("Apply Best to Current Script", "Splice the best framebulks over the window.", GUILayout.Width(200)))
                ApplyBest();
            if (hasBest && Btn("Save Best to File", "Write optimization-best.uktas in the scripts folder.", GUILayout.Width(150)))
                SaveBest();
            GUILayout.EndHorizontal();
            if (hasBest && Btn("Save as New Branch", "Project open: add a branch at Start Tick. No project: save <name>_optimized.uktas.", GUILayout.Width(170)))
                SaveAsNewBranch();
            DrawOptimizerSegmentBox();
            EndCard();
        }

        /// <summary>Live health readout while a health-related objective is selected.</summary>
        void DrawOptimizerHealthTelemetry()
        {
            if (_optimizer == null || _optimConfig == null || !IsHealthObjective(_optimConfig.Objective))
                return;
            GUILayout.Label("Baseline HP: " + LastHealthText(_optimizer.BaselineSamples, false) +
                            " -> Best HP: " + LastHealthText(_optimizer.BestSamples, false) +
                            " (Hard Damage: " + LastHealthText(_optimizer.BestSamples, true) + ")");
        }

        static string LastHealthText(List<OptimSample> samples, bool hardDamage)
        {
            if (samples == null || samples.Count == 0)
                return "n/a";
            OptimSample sample = samples[samples.Count - 1];
            float value = hardDamage ? sample.HardDamage : sample.Health;
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static bool IsHealthObjective(OptimObjective objective)
        {
            return objective == OptimObjective.Health ||
                   objective == OptimObjective.HardDamage ||
                   objective == OptimObjective.HealthDelta;
        }

        // ------------------------------------------------------------------
        // "New best" display and framebulk copy box
        // ------------------------------------------------------------------

        const float OptimBestFlashSeconds = 4f;
        const string OptimSegmentControlName = "UktasOptimBestSegment";

        // Best-score tracking. Updated on the Layout event only, so Layout and Repaint see the same state.
        TasOptimizer _optimTrackedFor;
        bool _optimTrackReady;
        double _optimLastSeenBest;
        double _optimLastImprovement;
        bool _optimHasImprovement;
        float _optimBestChangedAt = -1000f;
        bool _optimBestFlashing;

        // Cached framebulk text. Rebuilt only when the optimizer, its best score or the segment length changes.
        TasOptimizer _optimTextFor;
        double _optimTextScore = double.NaN;
        int _optimTextCount = -1;
        string _optimSegmentText = string.Empty;
        Vector2 _optimSegmentScroll;

        GUIStyle _optimBestBoxStyle, _optimBestFlashBoxStyle, _optimBestHeadStyle, _optimBestFlashHeadStyle,
                 _optimBestValueStyle, _optimSegmentAreaStyle;
        Texture2D _texOptimBest, _texOptimBestFlash;

        void EnsureOptimizerBestStyles()
        {
            if (_optimBestBoxStyle != null)
                return;
            EnsureEditorFont();

            _texOptimBest = RoundedTexture(24, 6f, new Color32(40, 34, 28, 255), ColAccent, 1f);
            _texOptimBestFlash = RoundedTexture(24, 6f, new Color32(30, 58, 36, 255), ColGood, 2f);

            _optimBestBoxStyle = new GUIStyle(GUIStyle.none);
            _optimBestBoxStyle.normal.background = _texOptimBest;
            _optimBestBoxStyle.border = new RectOffset(8, 8, 8, 8);
            _optimBestBoxStyle.padding = new RectOffset(12, 12, 8, 8);
            _optimBestBoxStyle.margin = new RectOffset(2, 2, 4, 4);

            _optimBestFlashBoxStyle = new GUIStyle(_optimBestBoxStyle);
            _optimBestFlashBoxStyle.normal.background = _texOptimBestFlash;

            _optimBestHeadStyle = new GUIStyle(_sectionStyle);
            _optimBestHeadStyle.margin = new RectOffset(0, 0, 0, 2);
            _optimBestFlashHeadStyle = new GUIStyle(_optimBestHeadStyle);
            _optimBestFlashHeadStyle.normal.textColor = ColGood;

            _optimBestValueStyle = new GUIStyle(_bodyStyle);
            _optimBestValueStyle.fontSize = 18;
            _optimBestValueStyle.fontStyle = FontStyle.Bold;
            _optimBestValueStyle.normal.textColor = ColText;

            GUIStyle area = _skin != null ? _skin.textArea : GUI.skin.textArea;
            _optimSegmentAreaStyle = new GUIStyle(area);
            _optimSegmentAreaStyle.wordWrap = false;
            _optimSegmentAreaStyle.fontSize = 12;
            if (_editorFont != null)
                _optimSegmentAreaStyle.font = _editorFont;
        }

        static string FormatOptimScore(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string FormatOptimDelta(double value)
        {
            return value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Layout-event bookkeeping: spots a changed best score (flash timer + delta vs the previous best)
        /// and refreshes the cached framebulk text. Never allocates per frame while nothing changes.
        /// </summary>
        void UpdateOptimizerBestTracking()
        {
            if (Event.current.type != EventType.Layout)
                return;
            if (_optimizer == null)
            {
                _optimTrackedFor = null;
                _optimTextFor = null;
                _optimSegmentText = string.Empty;
                _optimBestFlashing = false;
                return;
            }

            double best = _optimizer.BestScore;
            if (!ReferenceEquals(_optimTrackedFor, _optimizer))
            {
                _optimTrackedFor = _optimizer;
                _optimTrackReady = false;
                _optimHasImprovement = false;
                _optimLastImprovement = 0.0;
                _optimBestChangedAt = -1000f;
            }
            // Start watching once the baseline run is done (Running turns true), so the baseline itself is no flash.
            if (!_optimTrackReady)
            {
                if (_optimizer.Running)
                {
                    _optimTrackReady = true;
                    _optimLastSeenBest = best;
                }
            }
            else if (best != _optimLastSeenBest)
            {
                double change = best - _optimLastSeenBest;
                _optimLastSeenBest = best;
                bool maximize = _optimizer.Config.Direction == OptimDirection.Maximize;
                // Annealing can step the working point backwards; only a real improvement counts as a new best.
                if (maximize ? change > 0.0 : change < 0.0)
                {
                    _optimHasImprovement = true;
                    _optimLastImprovement = change;
                    _optimBestChangedAt = Time.realtimeSinceStartup;
                }
            }
            _optimBestFlashing = Time.realtimeSinceStartup - _optimBestChangedAt < OptimBestFlashSeconds;

            IList<TasFrameLine> segment = _optimizer.BestSegment;
            int count = segment != null ? segment.Count : 0;
            if (!ReferenceEquals(_optimTextFor, _optimizer) || _optimTextScore != best || _optimTextCount != count)
            {
                _optimTextFor = _optimizer;
                _optimTextScore = best;
                _optimTextCount = count;
                StringBuilder sb = new StringBuilder(count * 48);
                for (int i = 0; i < count; i++)
                    sb.AppendLine(segment[i].ToPipeLine());
                _optimSegmentText = sb.ToString();
            }
        }

        /// <summary>Highlighted best-score block. Same controls every event; only colours change while flashing.</summary>
        void DrawOptimizerBestBlock()
        {
            UpdateOptimizerBestTracking();
            EnsureOptimizerBestStyles();
            bool flash = _optimBestFlashing;
            double best = _optimizer.BestScore;
            double sinceBaseline = best - _optimizer.BaselineScore;
            double shown = _optimHasImprovement ? _optimLastImprovement : sinceBaseline;

            GUILayout.BeginVertical(flash ? _optimBestFlashBoxStyle : _optimBestBoxStyle);
            GUILayout.Label(flash ? "NEW BEST" : "CURRENT BEST", flash ? _optimBestFlashHeadStyle : _optimBestHeadStyle);
            GUILayout.Label("Best: " + FormatOptimScore(best) + "  (" + FormatOptimDelta(shown) + ")", _optimBestValueStyle);
            Hint("Baseline: " + FormatOptimScore(_optimizer.BaselineScore) +
                 "  |  Total vs baseline: " + FormatOptimDelta(sinceBaseline) +
                 (_optimHasImprovement ? "  |  Last step shown in brackets" : ""));
            if (_optimizer.BestSSJCount > 0)
                Hint("Super Slide Jumps: " + _optimizer.BestSSJCount + "  |  best boost: x" +
                     _optimizer.BestSSJQuality.ToString("0.###", CultureInfo.InvariantCulture));
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Read-only copy box with the framebulk lines of the best segment (StartTick..EndTick). The text
        /// area is selectable (Ctrl+A / Ctrl+C); edits are swallowed and the cached text is passed back each frame.
        /// </summary>
        void DrawOptimizerSegmentBox()
        {
            if (_optimizer == null || _optimizer.BestSegment == null || _optimizer.BestSegment.Count == 0)
                return;
            UpdateOptimizerBestTracking();
            EnsureOptimizerBestStyles();

            GUILayout.BeginHorizontal();
            Hint("Framebulks for ticks " + _optimizer.Config.StartTick + ".." + _optimizer.Config.EndTick +
                 " (best so far). Click inside, Ctrl+A then Ctrl+C to copy.");
            if (Btn("Copy", "Copy these framebulk lines to the clipboard.", GUILayout.Width(60)))
            {
                GUIUtility.systemCopyBuffer = _optimSegmentText;
                _plugin.Toast("Copied " + _optimizer.BestSegment.Count + " framebulk line(s).", false);
            }
            GUILayout.EndHorizontal();

            // Keep the box read-only: drop typing, delete, enter and paste while it has focus.
            Event e = Event.current;
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == OptimSegmentControlName)
            {
                bool ctrl = e.control || e.command;
                bool printable = !ctrl && e.character >= ' ' && e.character != (char)127;
                bool edit = e.keyCode == KeyCode.Backspace || e.keyCode == KeyCode.Delete ||
                            e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter ||
                            (ctrl && (e.keyCode == KeyCode.V || e.keyCode == KeyCode.X));
                if (printable || edit)
                    e.Use();
            }

            _optimSegmentScroll = GUILayout.BeginScrollView(_optimSegmentScroll, GUILayout.Height(150));
            GUI.SetNextControlName(OptimSegmentControlName);
            GUILayout.TextArea(_optimSegmentText, _optimSegmentAreaStyle);
            GUILayout.EndScrollView();
        }

        float _optimStartedAt;

        void StartOptimizer()
        {
            if (_engine.Script == null)
            {
                _plugin.Toast("Optimizer: no script loaded.", true);
                return;
            }
            int start = ParseTickField(_optimStartText, -1);
            int end = ParseTickField(_optimEndText, -1);
            int mutateEnd = ParseTickField(_optimMutateEndText, -1);
            int markerStart, markerEnd;
            // In-script optim_start / optim_end markers take precedence over the tick fields.
            if (TasOptimizer.ResolveMarkerTicks(_engine.Script, out markerStart, out markerEnd))
            {
                start = markerStart;
                end = markerEnd;
                _optimStartText = start.ToString(CultureInfo.InvariantCulture);
                _optimEndText = end.ToString(CultureInfo.InvariantCulture);
            }
            if (start < 0 || end <= start)
            {
                _plugin.Toast("Optimizer: set Start Tick below End Tick.", true);
                return;
            }
            if (mutateEnd <= start || mutateEnd > end)
                mutateEnd = end;
            _optimMutateEndText = mutateEnd.ToString(CultureInfo.InvariantCulture);
            _optimConfig.StartTick = start;
            _optimConfig.EndTick = end;
            _optimConfig.MutateEndTick = mutateEnd;
            float originPitch, originYaw;
            if (_bridge != null && _bridge.TryGetCameraAngles(out originPitch, out originYaw))
            {
                _optimConfig.PitchOrigin = originPitch;
                _optimConfig.YawOrigin = originYaw;
            }
            List<TasFrameLine> segment;
            string error;
            if (!ExtractSegment(start, end, out segment, out error))
            {
                _plugin.Toast("Optimizer: " + error, true);
                return;
            }
            _optimizer = new TasOptimizer(_optimConfig, _optimBoxes, segment, UnconnectedRunner,
                                          unchecked((int)DateTime.Now.Ticks));
            _optimStartedAt = Time.realtimeSinceStartup;
            EngineOptimizerHost host = new EngineOptimizerHost(_engine, _states, _bridge, _engine.Script,
                                                               _engine.ScriptPath, start, end,
                                                               msg => _plugin.Toast(msg, false));
            host.Headless = _optimConfig.UseFastForwardHeadless;
            host.FullReplay = _optimConfig.RollbackFullReplay;
            host.AimLockEnabled = _optimConfig.ProjectileAwareness;
            TasOptimizerRunner runner = new TasOptimizerRunner(_optimizer, host);
            string startError;
            if (!_engine.StartOptimizerRun(runner, out startError))
            {
                _plugin.Toast("Optimizer: " + startError, true);
                return;
            }
            _plugin.Toast("Optimizer started. Fast-forwarding (FFF) to tick " + start + " first.", false);
        }

        /// <summary>
        /// Writes the Current Best route as a complete script. With a .uktasproj open it is added as a
        /// branch diverging at StartTick; otherwise it is saved beside the script as "&lt;name&gt;_optimized.uktas".
        /// </summary>
        void SaveAsNewBranch()
        {
            if (_optimizer == null || _optimizer.BestSegment == null || _optimizer.BestSegment.Count == 0)
            {
                _plugin.Toast("Optimizer: no best segment yet.", true);
                return;
            }
            try
            {
                int start = _optimConfig.StartTick;
                int end = _optimConfig.EndTick;
                TasScript full = TasScript.Parse(_engine.Script.Serialize(), "optimized");
                string error;
                if (!TasOptimizer.SpliceSegment(full, start, end, _optimizer.BestSegment, out error))
                {
                    _plugin.Toast("Save as branch failed: " + error, true);
                    return;
                }

                if (_plugin.Project != null)
                {
                    BranchProject project = _plugin.Project;
                    UktasBranch branch = project.AddDivergentBranch("Optimized @" + start,
                                                                    _engine.Script, full, start);
                    _plugin.Toast("Added branch '" + branch.Name + "' to " + Path.GetFileName(project.ProjectPath) +
                                  "; bridged script updated. Switch in the Branches tab.", false);
                    return;
                }

                string dir = string.IsNullOrEmpty(_engine.ScriptPath)
                    ? _plugin.ScriptDirectory
                    : Path.GetDirectoryName(_engine.ScriptPath);
                string baseName = string.IsNullOrEmpty(_engine.ScriptPath)
                    ? "script"
                    : Path.GetFileNameWithoutExtension(_engine.ScriptPath);
                string path = Path.Combine(dir, baseName + "_optimized.uktas");
                full.Save(path);
                _plugin.Toast("Saved " + path, false);
            }
            catch (Exception ex)
            {
                _plugin.Toast("Save as branch failed: " + ex.Message, true);
            }
        }

        /// <summary>Copies the framebulk lines covering [start, end) out of the script.</summary>
        bool ExtractSegment(int start, int end, out List<TasFrameLine> segment, out string error)
        {
            segment = new List<TasFrameLine>();
            error = null;
            TickLocation a = _engine.Script.Locate(start);
            TickLocation b = _engine.Script.Locate(end);
            if (!a.Valid || !b.Valid || a.TickWithinLine != 0 || b.TickWithinLine != 0)
            {
                error = "start and end must fall on framebulk boundaries.";
                return false;
            }
            for (int i = a.LineIndex; i < b.LineIndex; i++)
                segment.Add(_engine.Script.Lines[i].Clone());
            return true;
        }

        void ApplyBest()
        {
            string error;
            int start = ParseTickField(_optimStartText, -1);
            int end = ParseTickField(_optimEndText, -1);
            if (!TasOptimizer.SpliceSegment(_engine.Script, start, end, _optimizer.BestSegment, out error))
            {
                _plugin.Toast("Apply failed: " + error, true);
                return;
            }
            if (string.IsNullOrEmpty(_engine.ScriptPath))
            {
                _plugin.Toast("Spliced in memory. Script has no file path; save from the editor.", false);
                return;
            }
            try
            {
                _engine.Script.Save(_engine.ScriptPath);
                _plugin.Toast("Applied best segment and saved " + Path.GetFileName(_engine.ScriptPath), false);
            }
            catch (Exception ex)
            {
                _plugin.Toast("Save failed: " + ex.Message, true);
            }
        }

        void SaveBest()
        {
            try
            {
                string path = Path.Combine(_plugin.ScriptDirectory, "optimization-best.uktas");
                TasOptimizer.SaveSegment(_engine.Script, _optimizer.BestSegment, path);
                _plugin.Toast("Saved " + path, false);
            }
            catch (Exception ex)
            {
                _plugin.Toast("Save failed: " + ex.Message, true);
            }
        }

        static string OpText(OptimOperator op)
        {
            switch (op)
            {
                case OptimOperator.Less: return "<";
                case OptimOperator.LessEq: return "<=";
                case OptimOperator.Greater: return ">";
                default: return ">=";
            }
        }

        static OptimObjective NextVariable(OptimObjective v)
        {
            OptimObjective[] all =
            {
                OptimObjective.PosX, OptimObjective.PosY, OptimObjective.PosZ,
                OptimObjective.VelX, OptimObjective.VelY, OptimObjective.VelZ,
                OptimObjective.SpeedXZ, OptimObjective.Speed3D, OptimObjective.SSJ,
                OptimObjective.Health, OptimObjective.HardDamage
            };
            int i = Array.IndexOf(all, v);
            return all[(i + 1) % all.Length];
        }
    }
}
