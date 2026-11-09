using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>One horizontal probe in the geometry whisker fan.</summary>
    public sealed class OptimWhisker
    {
        public int Index;
        /// <summary>World yaw of the probe, degrees.</summary>
        public float Angle;
        /// <summary>Yaw relative to the direction the player was travelling, degrees. Positive is right.</summary>
        public float RelativeAngle;
        public Vector3 Direction;
        public bool Hit;
        /// <summary>Hit distance, or the probe range when <see cref="Hit"/> is false.</summary>
        public float Distance;
        public Vector3 Point;
        public Vector3 Normal;
    }

    /// <summary>
    /// Geometry around one wall bonk. Produced at the end of an attempt from its samples, using
    /// read-only raycasts against static level geometry. Never changes the simulation.
    /// </summary>
    public sealed class OptimGeometryProbe
    {
        public bool HasImpact;
        public int Tick;
        public Vector3 ImpactPosition;
        public Vector3 ProbeOrigin;
        public Vector3 WallNormal;
        /// <summary>Horizontal travel direction on the tick before the collision.</summary>
        public Vector3 IntendedDirection;
        /// <summary>Direction toward the goal box, or the objective's primary axis.</summary>
        public Vector3 TargetDirection;
        /// <summary>Open direction the mutator steers toward, already carrying the corner margin.</summary>
        public Vector3 ClearanceDirection;
        /// <summary>Clearance yaw relative to <see cref="IntendedDirection"/>, degrees. Positive is right.</summary>
        public float ClearanceAngle;
        public float ClearanceDistance;
        /// <summary>Nearest static surface below the probe origin.</summary>
        public bool FloorHit;
        public float FloorDistance;
        /// <summary>Nearest static surface above the probe origin.</summary>
        public bool CeilingHit;
        public float CeilingDistance;
        public bool CornerFound;
        public Vector3 CornerPoint;
        public float CornerDistance;
        public bool GoalDefined;
        public bool GoalVisible;
        public float GoalDistance;
        public readonly List<OptimWhisker> Whiskers = new List<OptimWhisker>(24);

        public string ClearanceLabel()
        {
            float angle = ClearanceAngle;
            string side;
            if (Mathf.Abs(angle) <= 12f)
                side = "Ahead";
            else if (Mathf.Abs(angle) >= 165f)
                side = "Behind";
            else
                side = angle > 0f ? "Right" : "Left";
            return side + " (" + (angle >= 0f ? "+" : string.Empty) +
                   angle.ToString("0.0", CultureInfo.InvariantCulture) + " deg)";
        }
    }

    /// <summary>
    /// Environment-aware half of the optimizer: wall-bonk detection, whisker probing, clearance
    /// selection, and geometry-guided candidate generation.
    ///
    /// The probes only read the physics scene (static Environment / Default colliders, no player,
    /// viewmodels, triggers or Rigidbody props). They never write a transform, a collider or an
    /// input, so a run behaves identically whether guidance is on or off.
    /// </summary>
    public sealed partial class TasOptimizer
    {
        public OptimGeometryProbe LastGeometry { get; private set; }

        const int GeometryWhiskerCount = 24;
        const float GeometryProbeRange = 20f;
        const float GeometryVerticalProbeRange = 12f;
        /// <summary>Chest height above the player transform; the fan is horizontal.</summary>
        const float GeometryEyeHeight = 0.9f;
        /// <summary>Outward buffer added to a clearance angle when a corner is beside it.</summary>
        const float GeometryCornerMargin = 0.3f;
        const float GeometryNearWall = 3.5f;
        const float GeometryContactDistance = 1.8f;

        static readonly TasButtons GeometryMovementKeys =
            TasButtons.Forward | TasButtons.Back | TasButtons.Left | TasButtons.Right;

        int _geometryCandidatesLeft;
        // Segment from the attempt that produced the current probe, so the steering edits the
        // framebulks that actually hit the wall.
        List<TasFrameLine> _geometrySource;
        readonly RaycastHit[] _geometryHits = new RaycastHit[32];
        static int _geometryMask = int.MinValue;

        /// <summary>
        /// Looks for a wall bonk in the attempt that just finished and, when one is found,
        /// records the probe for the GUI and the next few candidates. Called from
        /// CompleteAttempt while the physics scene is still on the attempt's last tick.
        /// </summary>
        bool UpdateGeometryFromAttempt(List<OptimSample> samples)
        {
            if (!Config.GeometryAwareness || samples == null || samples.Count < 2)
                return false;

            // Newest first: the obstacle that ended (or last interrupted) this attempt is the one
            // the next candidate should steer around.
            int casts = 0;
            for (int i = samples.Count - 1; i >= 1; i--)
            {
                OptimSample previous = samples[i - 1];
                OptimSample current = samples[i];
                float previousSpeed = HorizontalSpeed(previous.Velocity);
                float currentSpeed = HorizontalSpeed(current.Velocity);

                // The requested bonk signature: over 60% of horizontal speed gone in one tick.
                if (previousSpeed < 3.5f || currentSpeed > previousSpeed * 0.4f)
                    continue;

                // Buttons are recorded with each sample. A fast fallback keeps headless tests and
                // older hosts working when they supply no input flags.
                bool movementHeld = ((previous.Buttons | current.Buttons) & GeometryMovementKeys) != 0;
                if (!movementHeld && previousSpeed < 8f)
                    continue;

                Vector3 intended = Horizontal(previous.Velocity);
                if (intended.sqrMagnitude < 0.01f)
                    intended = Horizontal(current.Position - previous.Position);
                if (intended.sqrMagnitude < 0.01f)
                    continue;
                intended.Normalize();

                if (++casts > 4)
                    break;
                OptimGeometryProbe probe = ProbeGeometry(current.Position, intended);
                if (probe == null || !probe.HasImpact)
                    continue;

                probe.Tick = current.Tick;
                LastGeometry = probe;
                // Give the search a few chances to use the new clearance, then let the normal
                // sweep/random generator take over again.
                _geometryCandidatesLeft = 3;
                return true;
            }

            // Contact without a large speed drop: V1 can already be pressed against a wall at
            // the last tick. One cheap forward check catches that case; only a hit triggers the
            // full 24-ray fan.
            OptimSample last = samples[samples.Count - 1];
            OptimSample beforeLast = samples[samples.Count - 2];
            bool held = ((last.Buttons | beforeLast.Buttons) & GeometryMovementKeys) != 0;
            if (!held)
                return false;
            Vector3 facing = Horizontal(last.Velocity);
            if (facing.sqrMagnitude < 0.01f)
                facing = Horizontal(beforeLast.Velocity);
            if (facing.sqrMagnitude < 0.01f)
                return false;
            facing.Normalize();

            int mask = GeometryMask();
            RaycastHit wall;
            if (!CastWorld(last.Position + Vector3.up * GeometryEyeHeight, facing, 1.1f, mask, out wall) ||
                Vector3.Dot(wall.normal, facing) > -0.15f)
                return false;

            OptimGeometryProbe contact = ProbeGeometry(last.Position, facing);
            if (contact == null || !contact.HasImpact)
                return false;
            contact.Tick = last.Tick;
            LastGeometry = contact;
            _geometryCandidatesLeft = 3;
            return true;
        }

        /// <summary>
        /// Casts the radial whisker fan and derives the best open direction. Returns null when the
        /// player is not actually against a surface that opposes the travel direction.
        /// </summary>
        OptimGeometryProbe ProbeGeometry(Vector3 position, Vector3 intended)
        {
            int mask = GeometryMask();
            Vector3 origin = position + Vector3.up * GeometryEyeHeight;
            float baseYaw = YawOf(intended);

            OptimGeometryProbe probe = new OptimGeometryProbe();
            probe.ImpactPosition = position;
            probe.ProbeOrigin = origin;
            probe.IntendedDirection = intended;

            // Vertical clearance lets the speed-priority guidance choose a jump or slam that
            // the objective actually rewards, instead of only steering horizontally.
            RaycastHit floorHit;
            probe.FloorHit = CastWorld(origin, Vector3.down, GeometryVerticalProbeRange, mask, out floorHit);
            probe.FloorDistance = probe.FloorHit ? floorHit.distance : GeometryVerticalProbeRange;
            RaycastHit ceilingHit;
            probe.CeilingHit = CastWorld(origin, Vector3.up, GeometryVerticalProbeRange, mask, out ceilingHit);
            probe.CeilingDistance = probe.CeilingHit ? ceilingHit.distance : GeometryVerticalProbeRange;

            for (int i = 0; i < GeometryWhiskerCount; i++)
            {
                float relative = -180f + i * (360f / GeometryWhiskerCount);
                float angle = baseYaw + relative;
                Vector3 direction = YawDirection(angle);
                RaycastHit hit;
                bool found = CastWorld(origin, direction, GeometryProbeRange, mask, out hit);

                OptimWhisker whisker = new OptimWhisker();
                whisker.Index = i;
                whisker.Angle = angle;
                whisker.RelativeAngle = relative;
                whisker.Direction = direction;
                whisker.Hit = found;
                whisker.Distance = found ? hit.distance : GeometryProbeRange;
                whisker.Point = found ? hit.point : origin + direction * GeometryProbeRange;
                whisker.Normal = found ? hit.normal : Vector3.zero;
                probe.Whiskers.Add(whisker);
            }

            // Require a nearby surface whose normal opposes the direction the player was moving.
            // This rejects speed loss from landings and ordinary friction.
            bool impact = false;
            float nearestWall = float.MaxValue;
            for (int i = 0; i < probe.Whiskers.Count; i++)
            {
                OptimWhisker whisker = probe.Whiskers[i];
                if (!whisker.Hit || whisker.Distance > GeometryContactDistance)
                    continue;
                if (Vector3.Dot(whisker.Normal, intended) > -0.15f)
                    continue;
                impact = true;
                if (whisker.Distance < nearestWall)
                {
                    nearestWall = whisker.Distance;
                    probe.WallNormal = whisker.Normal;
                }
            }

            RaycastHit forwardHit;
            if (CastWorld(origin, intended, GeometryContactDistance, mask, out forwardHit) &&
                Vector3.Dot(forwardHit.normal, intended) < -0.1f)
            {
                impact = true;
                if (forwardHit.distance < nearestWall)
                {
                    nearestWall = forwardHit.distance;
                    probe.WallNormal = forwardHit.normal;
                }
            }

            if (!impact)
                return null;

            Vector3 goalCenter;
            float goalDistance;
            bool hasGoal = TryNearestGoal(position, out goalCenter, out goalDistance);
            Vector3 targetDirection = hasGoal ? Horizontal(goalCenter - position) : Horizontal(ObjectiveDirection(intended));
            if (targetDirection.sqrMagnitude < 0.01f)
                targetDirection = intended;
            targetDirection.Normalize();

            probe.GoalDefined = hasGoal;
            probe.GoalDistance = hasGoal ? goalDistance : 0f;
            probe.TargetDirection = targetDirection;
            float targetYaw = YawOf(targetDirection);
            bool directGoalClear = hasGoal && HasLineOfSight(origin, goalCenter, mask);
            probe.GoalVisible = directGoalClear;

            bool chosen = false;
            float chosenYaw = baseYaw;
            float chosenDistance = GeometryProbeRange;
            bool chosenSeesGoal = false;

            if (directGoalClear)
            {
                chosen = true;
                chosenYaw = YawOf(Horizontal(goalCenter - position));
                chosenDistance = goalDistance;
                chosenSeesGoal = true;
            }
            else
            {
                float bestScore = float.MaxValue;
                for (int i = 0; i < probe.Whiskers.Count; i++)
                {
                    OptimWhisker whisker = probe.Whiskers[i];
                    float openDistance = whisker.Hit ? whisker.Distance : GeometryProbeRange;
                    if (openDistance < 3f)
                        continue;

                    float candidateYaw = whisker.Angle;
                    float blockedDistance;
                    int blockedIndex;
                    bool corner = TryNearestBlockedNeighbour(probe, i, out blockedDistance, out blockedIndex);
                    if (corner)
                    {
                        float blockedAngle = probe.Whiskers[blockedIndex].Angle;
                        float margin = Mathf.Clamp(
                            Mathf.Atan2(GeometryCornerMargin, Mathf.Max(0.5f, blockedDistance)) * Mathf.Rad2Deg,
                            4f, 22f);
                        candidateYaw += Mathf.Sign(Mathf.DeltaAngle(blockedAngle, whisker.Angle)) * margin;
                    }

                    // The margin must not point into the wall on the other side of a doorway.
                    RaycastHit marginHit;
                    if (CastWorld(origin, YawDirection(candidateYaw), 1.25f, mask, out marginHit))
                        continue;

                    bool seesGoal = true;
                    if (hasGoal)
                    {
                        Vector3 lookFrom = origin + YawDirection(candidateYaw) *
                                            Mathf.Min(2.5f, Mathf.Max(0.6f, openDistance - 0.5f));
                        seesGoal = HasLineOfSight(lookFrom, goalCenter, mask);
                    }

                    float deviation = Mathf.Abs(Mathf.DeltaAngle(targetYaw, candidateYaw));
                    float score = deviation + (seesGoal ? 0f : 1000f) + (corner ? 0f : 2f) -
                                  Mathf.Min(openDistance, GeometryProbeRange) * 0.01f;
                    if (!chosen || score < bestScore)
                    {
                        chosen = true;
                        bestScore = score;
                        chosenYaw = candidateYaw;
                        chosenDistance = openDistance;
                        chosenSeesGoal = seesGoal;
                    }
                }
            }

            if (!chosen)
            {
                // Every fan ray hits something close. Face the longest ray; the mutation is still
                // more informed than a blind yaw roll.
                OptimWhisker longest = probe.Whiskers[0];
                for (int i = 1; i < probe.Whiskers.Count; i++)
                    if (probe.Whiskers[i].Distance > longest.Distance)
                        longest = probe.Whiskers[i];
                chosenYaw = longest.Angle;
                chosenDistance = longest.Distance;
                chosenSeesGoal = false;
            }

            probe.ClearanceDirection = YawDirection(chosenYaw);
            probe.ClearanceAngle = Mathf.DeltaAngle(baseYaw, chosenYaw);
            probe.ClearanceDistance = chosenDistance;
            probe.GoalVisible = hasGoal && chosenSeesGoal;

            FindNearestCorner(probe, origin, position);
            probe.HasImpact = true;
            return probe;
        }

        /// <summary>The closest blocked immediate neighbour of an open whisker, if either side is blocked.</summary>
        static bool TryNearestBlockedNeighbour(OptimGeometryProbe probe, int index, out float distance, out int blockedIndex)
        {
            int count = probe.Whiskers.Count;
            int beforeIndex = (index + count - 1) % count;
            int afterIndex = (index + 1) % count;
            OptimWhisker before = probe.Whiskers[beforeIndex];
            OptimWhisker after = probe.Whiskers[afterIndex];
            float beforeDistance = before.Hit && before.Distance < GeometryNearWall ? before.Distance : -1f;
            float afterDistance = after.Hit && after.Distance < GeometryNearWall ? after.Distance : -1f;

            if (beforeDistance < 0f && afterDistance < 0f)
            {
                distance = 0f;
                blockedIndex = -1;
                return false;
            }
            if (beforeDistance < 0f || (afterDistance >= 0f && afterDistance < beforeDistance))
            {
                distance = afterDistance;
                blockedIndex = afterIndex;
                return true;
            }
            distance = beforeDistance;
            blockedIndex = beforeIndex;
            return true;
        }

        /// <summary>Records the closest short-to-open probe pair as the doorway / corner threshold.</summary>
        static void FindNearestCorner(OptimGeometryProbe probe, Vector3 origin, Vector3 impact)
        {
            for (int i = 0; i < probe.Whiskers.Count; i++)
            {
                OptimWhisker a = probe.Whiskers[i];
                OptimWhisker b = probe.Whiskers[(i + 1) % probe.Whiskers.Count];
                float ad = a.Hit ? a.Distance : GeometryProbeRange;
                float bd = b.Hit ? b.Distance : GeometryProbeRange;
                float near = Mathf.Min(ad, bd);
                float far = Mathf.Max(ad, bd);
                if (near >= GeometryNearWall || far < 3f || far - near < 1.5f)
                    continue;

                float midAngle = a.Angle + Mathf.DeltaAngle(a.Angle, b.Angle) * 0.5f;
                Vector3 point = origin + YawDirection(midAngle) * (near + GeometryCornerMargin);
                float distance = Horizontal(impact - point).magnitude;
                if (!probe.CornerFound || distance < probe.CornerDistance)
                {
                    probe.CornerFound = true;
                    probe.CornerPoint = point;
                    probe.CornerDistance = distance;
                }
            }
        }

        /// <summary>
        /// Builds one geometry-guided candidate. Only the two framebulks closest to the impact in
        /// the last 18 ticks are touched: yaw is nudged toward the clearance vector, lateral input
        /// is biased toward the open side, and dash/slide may be added when those mutations are on.
        /// </summary>
        List<TasFrameLine> MakeGeometryCandidate(OptimGeometryProbe probe)
        {
            if (probe == null || !probe.HasImpact || _best == null)
                return null;

            OptimMutationSettings m = Config.Mutations;
            bool verticalPriority = Config.SpeedPriority == OptimSpeedPriority.Vertical;
            if (!m.MutateYaw && !m.MutateMovement && !m.MutateDash && !m.MutateSlide &&
                !(verticalPriority && m.MutateJump))
                return null;

            int segmentTicks = Config.EndTick - Config.StartTick;
            if (segmentTicks <= 0)
                return null;

            List<TasFrameLine> source = _geometrySource != null ? _geometrySource : _best;
            List<TasFrameLine> candidate = CloneAll(source);
            int localImpact = Mathf.Clamp(probe.Tick - Config.StartTick, 0, segmentTicks - 1);
            int windowStart = Mathf.Max(0, localImpact - 18);
            int windowEnd = localImpact;

            List<int> window = new List<int>();
            int cursor = 0;
            for (int i = 0; i < candidate.Count; i++)
            {
                TasFrameLine line = candidate[i];
                if (line == null)
                    continue;
                int start = cursor;
                int end = cursor + GeometryLineTicks(line);
                // Zero-tick markers (look_at, checkpoint, change, ...) do not advance the cursor.
                if (line.IsFramebulk && start <= windowEnd && end > windowStart)
                    window.Add(i);
                cursor = end;
            }
            if (window.Count == 0)
                return null;

            float targetYaw = YawOf(probe.ClearanceDirection);
            bool lateral = m.MutateMovement && Mathf.Abs(probe.ClearanceAngle) > 6f;
            double maxYawStep = Mathf.Clamp(m.YawStep * 4f, 5f, 30f);
            bool changed = false;
            int take = Mathf.Min(2, window.Count);

            for (int k = 0; k < take; k++)
            {
                int index = window[window.Count - 1 - k];
                TasFrameLine line = candidate[index];
                if (line == null || !line.IsFramebulk)
                    continue;

                if (m.MutateYaw)
                {
                    double anchor = AngleAnchor(candidate, index, true);
                    double current = line.Yaw.Resolve(anchor);
                    double delta = Mathf.DeltaAngle((float)current, targetYaw);
                    if (delta > maxYawStep) delta = maxYawStep;
                    else if (delta < -maxYawStep) delta = -maxYawStep;
                    if (Mathf.Abs((float)delta) > 0.01f)
                    {
                        line.Yaw = AngleToken.Absolute(current + delta);
                        changed = true;
                    }
                }

                if (lateral)
                {
                    TasButtons openSide = probe.ClearanceAngle > 0f ? TasButtons.Right : TasButtons.Left;
                    TasButtons closedSide = probe.ClearanceAngle > 0f ? TasButtons.Left : TasButtons.Right;
                    line.Buttons = (line.Buttons | openSide) & ~closedSide;
                    changed = true;
                }

                if (k == 0)
                {
                    // Dash and slide are only added where the game would accept them: a dash needs
                    // a full stamina bar and no dash running, and an air slide needs a jump behind
                    // it, otherwise the press does nothing or turns into a slam.
                    if (m.MutateDash && Mathf.Abs(probe.ClearanceAngle) > 18f && probe.ClearanceDistance > 2f &&
                        GeometryActionWorks(candidate, index, true))
                    {
                        line.Buttons |= TasButtons.Dash;
                        changed = true;
                    }
                    else if (m.MutateSlide && Mathf.Abs(probe.ClearanceAngle) > 25f &&
                             probe.ClearanceDistance > 3f && GeometryActionWorks(candidate, index, false) &&
                             _rng.NextDouble() < 0.5)
                    {
                        line.Buttons |= TasButtons.Slide;
                        changed = true;
                    }

                    // With vertical speed preferred, use the scanned floor / ceiling clearance
                    // for a grounded jump or an airborne slam. Horizontal steering above still
                    // gets the player around the wall; this chooses the axis the objective wants.
                    if (verticalPriority && m.MutateJump && (line.Buttons & TasButtons.Slide) == 0 &&
                        (!probe.CeilingHit || probe.CeilingDistance > 2.2f) &&
                        GeometryJumpWorks(candidate, index))
                    {
                        line.Buttons |= TasButtons.Jump;
                        changed = true;
                    }
                    else if (verticalPriority && m.MutateSlide && SlamHelps() &&
                             (line.Buttons & TasButtons.Jump) == 0 &&
                             (!probe.FloorHit || probe.FloorDistance > 1f) &&
                             GeometrySlamWorks(candidate, index))
                    {
                        line.Buttons |= TasButtons.Slide;
                        changed = true;
                    }
                }
            }

            if (!changed)
                return null;

            PhaseText = "geometry steering " + probe.ClearanceLabel() +
                        (probe.CornerFound
                            ? ", corner " + probe.CornerDistance.ToString("0.0", CultureInfo.InvariantCulture) + "m"
                            : string.Empty) +
                        (verticalPriority ? " | vertical speed" : string.Empty);
            return candidate;
        }

        /// <summary>
        /// True when a dash (or slide) added to this framebulk is a press the game accepts, judged
        /// from the samples of the attempt that produced the current geometry probe.
        /// </summary>
        bool GeometryActionWorks(List<TasFrameLine> candidate, int index, bool dash)
        {
            ActionWindow window;
            if (!TryActionWindow(candidate, index, _geometryActionSamples, out window))
                return false;
            return dash
                ? IsDashReady(_geometryActionSamples, window.Pre)
                : IsSlideUseful(_geometryActionSamples, window.Pre);
        }

        bool GeometryJumpWorks(List<TasFrameLine> candidate, int index)
        {
            ActionWindow window;
            if (!TryActionWindow(candidate, index, _geometryActionSamples, out window))
                return false;
            return IsJumpReady(_geometryActionSamples, window.Pre);
        }

        bool GeometrySlamWorks(List<TasFrameLine> candidate, int index)
        {
            ActionWindow window;
            if (!TryActionWindow(candidate, index, _geometryActionSamples, out window))
                return false;
            OptimSample pre = _geometryActionSamples[window.Pre];
            return pre.SlamReady && !pre.Slamming;
        }

        /// <summary>Total ticks in a candidate segment, including any special frames.</summary>
        static int SegmentTickCount(List<TasFrameLine> segment)
        {
            int ticks = 0;
            if (segment == null)
                return 0;
            for (int i = 0; i < segment.Count; i++)
            {
                if (segment[i] != null)
                    ticks += GeometryLineTicks(segment[i]);
            }
            return ticks;
        }

        /// <summary>Same tick accounting as TasScript: zero-tick markers occupy no timeline space.</summary>
        static int GeometryLineTicks(TasFrameLine line)
        {
            switch (line.Kind)
            {
                case TasFrameKind.Checkpoint:
                case TasFrameKind.ChangeYaw:
                case TasFrameKind.ChangePitch:
                case TasFrameKind.FastForwardEnd:
                case TasFrameKind.NoRefreshBreak:
                case TasFrameKind.LookAt:
                case TasFrameKind.Fist:
                case TasFrameKind.BakeStart:
                case TasFrameKind.BakeEnd:
                case TasFrameKind.RecordVideoStart:
                case TasFrameKind.RecordVideoStop:
                    return 0;
                default:
                    return Mathf.Max(1, line.Count);
            }
        }

        // ---- Probe helpers -------------------------------------------------------------------

        static int GeometryMask()
        {
            if (_geometryMask != int.MinValue)
                return _geometryMask;

            int mask = 0;
            int environment = LayerMask.NameToLayer("Environment");
            int defaultLayer = LayerMask.NameToLayer("Default");
            if (environment >= 0) mask |= 1 << environment;
            if (defaultLayer >= 0) mask |= 1 << defaultLayer;

            // Explicitly keep the player, viewmodels and helper layers out even if the game puts
            // them on Default.
            mask = ExcludeLayer(mask, "Ignore Raycast");
            mask = ExcludeLayer(mask, "Player");
            mask = ExcludeLayer(mask, "ViewModel");
            mask = ExcludeLayer(mask, "Viewmodel");
            mask = ExcludeLayer(mask, "Viewmodels");

            if (mask == 0)
                mask = Physics.DefaultRaycastLayers;
            _geometryMask = mask;
            return mask;
        }

        static int ExcludeLayer(int mask, string name)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? mask & ~(1 << layer) : mask;
        }

        bool CastWorld(Vector3 origin, Vector3 direction, float distance, int mask, out RaycastHit nearest)
        {
            nearest = default(RaycastHit);
            if (distance <= 0.001f)
                return false;

            int count = Physics.RaycastNonAlloc(origin, direction, _geometryHits, distance, mask,
                                                QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _geometryHits[i];
                if (!IsWorldSurface(hit) || hit.distance >= best)
                    continue;
                nearest = hit;
                best = hit.distance;
                found = true;
            }
            return found;
        }

        /// <summary>Only static level surfaces: no triggers, player, viewmodels, particles or Rigidbody props.</summary>
        static bool IsWorldSurface(RaycastHit hit)
        {
            Collider collider = hit.collider;
            if (collider == null || collider.isTrigger)
                return false;
            if (hit.rigidbody != null || collider.GetComponentInParent<Rigidbody>() != null)
                return false;
            if (collider.GetComponentInParent<Camera>() != null)
                return false;
            if (collider.GetComponentInParent<ParticleSystem>() != null)
                return false;
            return true;
        }

        bool HasLineOfSight(Vector3 from, Vector3 to, int mask)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.05f)
                return true;
            RaycastHit ignored;
            return !CastWorld(from, delta / distance, distance - 0.05f, mask, out ignored);
        }

        bool TryNearestGoal(Vector3 position, out Vector3 center, out float distance)
        {
            center = Vector3.zero;
            distance = 0f;
            bool found = false;
            float best = float.MaxValue;
            foreach (OptimBox goal in Boxes.Enabled(OptimBoxKind.Goal))
            {
                float d = Vector3.Distance(position, goal.Center);
                if (d >= best)
                    continue;
                best = d;
                center = goal.Center;
                found = true;
            }
            distance = best;
            return found;
        }

        Vector3 ObjectiveDirection(Vector3 intended)
        {
            Vector3 axis;
            switch (Config.Objective)
            {
                case OptimObjective.PosX:
                case OptimObjective.VelX:
                    axis = Vector3.right;
                    break;
                case OptimObjective.PosZ:
                case OptimObjective.VelZ:
                    axis = Vector3.forward;
                    break;
                default:
                    return intended;
            }
            return Config.Direction == OptimDirection.Minimize ? -axis : axis;
        }

        static Vector3 Horizontal(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        static float HorizontalSpeed(Vector3 velocity)
        {
            velocity.y = 0f;
            return velocity.magnitude;
        }

        static float YawOf(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        static Vector3 YawDirection(float yaw)
        {
            float radians = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }
    }
}
