using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>One sampled player state, taken once per simulation tick.</summary>
    public struct TrajectoryNode
    {
        public int Tick;
        public double Time;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Speed;
        public float Pitch;
        public float Yaw;
        public bool Grounded;
        public bool Sliding;
        public bool Dashing;
        /// <summary>True while V1 is in a ground slam on this tick.</summary>
        public bool Slamming;
        /// <summary>False when the current slam state could not be read.</summary>
        public bool SlamNowKnown;
        /// <summary>True when a Slide press on this tick would start a ground slam.</summary>
        public bool SlamReady;
        /// <summary>False when the slam state could not be read (e.g. an old game build).</summary>
        public bool SlamKnown;
        /// <summary>True while V1 is pressed against a wall.</summary>
        public bool TouchingWall;
        /// <summary>False when the wall checks could not be read.</summary>
        public bool WallKnown;
        public float Stamina;
        public float Health;
        public float HardDamage;
        public TasButtons Held;
        public int WeaponSlot;
        /// <summary>The game's Time.timeScale on this tick (read only).</summary>
        public float TimeScale;
        /// <summary>Player collision bounds at this tick: local center offset and world-space size.</summary>
        public Vector3 HitboxCenter;
        public Vector3 HitboxSize;
        public bool HasHitbox;
    }

    /// <summary>When the path is drawn. Configured in the Overlays tab.</summary>
    public enum TrajectoryVisibility
    {
        Always = 0,
        FreecamOnly = 1,
        Hidden = 2
    }

    /// <summary>
    /// SAR-style player trajectory: records one node per simulation tick, draws the
    /// path with GL lines after the main camera renders, and lets a freecam cursor
    /// hover a node (metadata tooltip) or click it (scrub playback to that tick).
    /// Recording only reads game state; it never writes to the player or the camera.
    /// </summary>
    public sealed class TrajectoryManager
    {
        /// <summary>Speed (hu/s) that maps to the far end of the velocity gradient.</summary>
        const float GradientTopSpeed = 120f;
        /// <summary>Hover radius in screen pixels.</summary>
        const float HoverRadiusPx = 12f;
        /// <summary>World-space spacing between parallel lines that fake line thickness.</summary>
        const float ThicknessStep = 0.02f;

        static readonly Color SlowColor = new Color(0.25f, 1f, 0.35f);
        static readonly Color MidColor = new Color(1f, 0.95f, 0.2f);
        static readonly Color FastColor = new Color(1f, 0.25f, 0.85f);
        /// <summary>Same green as the local player box in the hitbox overlay.</summary>
        static readonly Color PlayerBoxColor = new Color(0.25f, 1f, 0.45f, 0.95f);
        // The 12 edges of a box, as corner index pairs (corner bit 0 = x, 1 = y, 2 = z).
        static readonly int[] BoxEdges =
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7
        };

        readonly Plugin _plugin;
        readonly List<TrajectoryNode> _nodes = new List<TrajectoryNode>(4096);
        Material _material;
        int _lastSampledTick = -1;
        int _hoverIndex = -1;
        /// <summary>World position of the hover cursor: a node, or a point between two nodes.</summary>
        Vector3 _hoverWorld;
        string _tooltip = string.Empty;
        string _notice = string.Empty;
        float _noticeUntil;

        public TrajectoryManager(Plugin plugin)
        {
            _plugin = plugin;
            Camera.onPostRender += OnCameraPostRender;
        }

        public void Dispose()
        {
            Camera.onPostRender -= OnCameraPostRender;
            if (_material != null)
                UnityEngine.Object.Destroy(_material);
            _material = null;
        }

        public int Count { get { return _nodes.Count; } }
        public int HoverIndex { get { return _hoverIndex; } }
        public TrajectoryNode this[int index] { get { return _nodes[index]; } }

        public void Clear()
        {
            _nodes.Clear();
            _lastSampledTick = -1;
            _hoverIndex = -1;
        }

        /// <summary>
        /// Records the player state for a simulation tick. Ticks that are not newer than
        /// the last node drop the nodes at or after them, so a rewind or restart replaces
        /// the old path instead of stacking a second one on top.
        /// </summary>
        public void Sample(int tick, double time)
        {
            if (!_plugin.TrajectoryEnabled.Value || tick == _lastSampledTick)
                return;
            GameBridge bridge = _plugin.Bridge;
            Transform player = bridge != null ? bridge.PlayerTransform : null;
            if (player == null)
                return;

            while (_nodes.Count > 0 && _nodes[_nodes.Count - 1].Tick >= tick)
                _nodes.RemoveAt(_nodes.Count - 1);

            TrajectoryNode node = new TrajectoryNode { Tick = tick, Time = time, Position = player.position, TimeScale = UnityEngine.Time.timeScale };

            Vector3 velocity;
            if (bridge.TryGetPlayerVelocity(out velocity))
            {
                node.Velocity = velocity;
                node.Speed = velocity.magnitude;
            }

            float pitch, yaw;
            if (bridge.TryGetCameraAngles(out pitch, out yaw))
            {
                node.Pitch = pitch;
                node.Yaw = yaw;
            }

            bool grounded, sliding, dashing, jumping;
            if (bridge.TryGetMovementState(out grounded, out sliding, out dashing, out jumping))
            {
                node.Grounded = grounded;
                node.Sliding = sliding;
                node.Dashing = dashing;
            }

            bool slam;
            if (bridge.TryGetSlamReady(out slam))
            {
                node.SlamReady = slam;
                node.SlamKnown = true;
            }

            bool slamming;
            if (bridge.TryGetSlamming(out slamming))
            {
                node.Slamming = slamming;
                node.SlamNowKnown = true;
            }

            bool onWall;
            if (bridge.TryGetTouchingWall(out onWall))
            {
                node.TouchingWall = onWall;
                node.WallKnown = true;
            }

            Vector3 hitboxCenter;
            Vector3 hitboxSize;
            if (bridge.TryGetPlayerHitboxBounds(out hitboxCenter, out hitboxSize))
            {
                node.HitboxSize = hitboxSize;
                node.HitboxCenter = hitboxCenter;
                node.HasHitbox = true;
            }

            float health, hardDamage;
            if (bridge.TryGetHealth(out health, out hardDamage))
            {
                node.Health = health;
                node.HardDamage = hardDamage;
            }

            float stamina;
            if (bridge.TryGetStamina(out stamina))
                node.Stamina = stamina;

            node.WeaponSlot = bridge.GunSlot;

            TasScript script = _plugin.Engine != null ? _plugin.Engine.Script : null;
            if (script != null)
            {
                TickLocation location = script.Locate(tick);
                if (location.Valid && location.LineIndex < script.Lines.Count)
                    node.Held = script.Lines[location.LineIndex].Buttons;
            }

            _nodes.Add(node);
            _lastSampledTick = tick;

            int cap = Mathf.Max(1000, _plugin.TrajectoryMaxNodes.Value);
            if (_nodes.Count > cap)
            {
                int excess = _nodes.Count - cap;
                _nodes.RemoveRange(0, excess);
            }
        }

        /// <summary>
        /// Interpolated player pose at a simulation time, used by the video renderer for
        /// sub-frame samples. Returns false when the path has no nodes.
        /// </summary>
        public bool SampleAt(double time, out Vector3 position, out float pitch, out float yaw)
        {
            position = Vector3.zero;
            pitch = 0f;
            yaw = 0f;
            if (_nodes.Count == 0)
                return false;
            if (time <= _nodes[0].Time)
            {
                TrajectoryNode first = _nodes[0];
                position = first.Position;
                pitch = first.Pitch;
                yaw = first.Yaw;
                return true;
            }
            int last = _nodes.Count - 1;
            if (time >= _nodes[last].Time)
            {
                TrajectoryNode end = _nodes[last];
                position = end.Position;
                pitch = end.Pitch;
                yaw = end.Yaw;
                return true;
            }

            int lo = 0;
            int hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_nodes[mid].Time <= time)
                    lo = mid;
                else
                    hi = mid;
            }

            TrajectoryNode a = _nodes[lo];
            TrajectoryNode b = _nodes[hi];
            double span = b.Time - a.Time;
            float t = span <= 0.0 ? 0f : (float)((time - a.Time) / span);
            position = Vector3.Lerp(a.Position, b.Position, t);
            pitch = Mathf.LerpAngle(a.Pitch, b.Pitch, t);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
            return true;
        }

        bool ShouldDraw()
        {
            if (!_plugin.TrajectoryEnabled.Value)
                return false;
            switch ((TrajectoryVisibility)Mathf.Clamp(_plugin.TrajectoryVisibilityMode.Value, 0, 2))
            {
                case TrajectoryVisibility.Always: return true;
                case TrajectoryVisibility.FreecamOnly: return _plugin.FreecamActive;
                default: return false;
            }
        }

        /// <summary>
        /// Called once per frame from Plugin. Hover and click only apply while freecam
        /// has the cursor free, so normal gameplay input never reaches the path.
        /// </summary>
        public void Update(bool cursorFree)
        {
            _hoverIndex = -1;
            if (!cursorFree || !ShouldDraw() || _nodes.Count == 0)
                return;
            Camera camera = Camera.main;
            if (camera == null)
                return;

            Vector2 mouse = Input.mousePosition;
            float bestDistance = HoverRadiusPx * HoverRadiusPx;
            int best = -1;
            for (int i = 0; i < _nodes.Count; i++)
            {
                Vector3 screen = camera.WorldToScreenPoint(_nodes[i].Position);
                if (screen.z <= 0f)
                    continue;
                float distance = (mouse - new Vector2(screen.x, screen.y)).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best >= 0)
            {
                _hoverIndex = best;
                _hoverWorld = _nodes[best].Position;
                _tooltip = BuildTooltip(_nodes[best]);
            }
            else
            {
                // No sample within the radius: look for the nearest point on the polyline
                // between two consecutive samples.
                int segment;
                float t;
                if (TryFindNearestOnSegment(camera, mouse, out segment, out t))
                {
                    // No fractional hover: every point on the line answers with the nearest
                    // recorded tick and its own position, so there is no gap between ticks.
                    int nearest = t < 0.5f ? segment - 1 : segment;
                    _hoverIndex = nearest;
                    _hoverWorld = _nodes[nearest].Position;
                    _tooltip = BuildTooltip(_nodes[nearest]);
                }
            }

            if (_hoverIndex >= 0 && Input.GetMouseButtonDown(0))
                ScrubTo(_nodes[_hoverIndex]);
        }

        /// <summary>
        /// Projects the mouse onto every segment between consecutive nodes in screen space
        /// and returns the closest one within the hover radius. segment is the index of the
        /// later node (segment i joins nodes i-1 and i); t is 0..1 from node i-1 to node i.
        /// Segments are clipped to the camera's front half-space; zero-length (or
        /// near-zero) screen segments collapse to their first point instead of dividing by 0.
        /// </summary>
        bool TryFindNearestOnSegment(Camera camera, Vector2 mouse, out int segment, out float t)
        {
            segment = -1;
            t = 0f;
            float bestDistance = HoverRadiusPx * HoverRadiusPx;
            Vector3 prev = camera.WorldToScreenPoint(_nodes[0].Position);
            for (int i = 1; i < _nodes.Count; i++)
            {
                Vector3 next = camera.WorldToScreenPoint(_nodes[i].Position);
                Vector3 s0 = prev;
                Vector3 s1 = next;
                prev = next;

                // Screen z is linear along a world segment, so clipping to z > 0 is a
                // linear interpolation of the whole screen point.
                float lo = 0f;
                float hi = 1f;
                const float MinZ = 0.01f;
                if (s0.z <= MinZ && s1.z <= MinZ)
                    continue;
                if (s0.z <= MinZ)
                    lo = (MinZ - s0.z) / (s1.z - s0.z);
                else if (s1.z <= MinZ)
                    hi = (MinZ - s0.z) / (s1.z - s0.z);

                Vector2 p0 = Vector2.Lerp(new Vector2(s0.x, s0.y), new Vector2(s1.x, s1.y), lo);
                Vector2 p1 = Vector2.Lerp(new Vector2(s0.x, s0.y), new Vector2(s1.x, s1.y), hi);

                Vector2 d = p1 - p0;
                float lengthSq = d.sqrMagnitude;
                float u = lengthSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(mouse - p0, d) / lengthSq) : 0f;
                float distance = (mouse - (p0 + d * u)).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    segment = i;
                    t = lo + (hi - lo) * u;
                }
            }
            return segment >= 0;
        }

        void ScrubTo(TrajectoryNode node)
        {
            TasEngine engine = _plugin.Engine;
            if (engine == null || engine.Script == null)
                return;
            if (node.Tick > engine.CurrentTick)
            {
                engine.PlayToTick(node.Tick);
                Notice("Scrubbing to tick " + node.Tick + ".");
            }
            else
            {
                // Backward scrubs need a savestate at or before the tick; the engine's
                // rolling history owns that, so this path only pauses on the tick.
                Notice("Tick " + node.Tick + " is behind playback. Load a savestate at or before it, then click again.");
            }
        }

        void Notice(string text)
        {
            _notice = text;
            _noticeUntil = TimeController.RealTime + 3f;
        }

        /// <summary>Draws the path. Runs after the main camera renders (Camera.onPostRender).</summary>
        void OnCameraPostRender(Camera camera)
        {
            if (camera == null || camera != Camera.main)
                return;
            // Offline video frames stay free of the overlay.
            if (_plugin.Video != null && _plugin.Video.Rendering)
                return;
            if (_nodes.Count >= 2 && ShouldDraw())
                DrawMainPath(camera);
            DrawOptimizerOverlay(camera);
        }

        void DrawMainPath(Camera camera)
        {
            EnsureMaterial();

            float opacity = Mathf.Clamp01(_plugin.TrajectoryOpacity.Value);
            int passes = Mathf.Clamp(Mathf.RoundToInt(_plugin.TrajectoryThickness.Value), 1, 6);
            bool gradient = _plugin.TrajectoryGradient.Value;
            Color single = ParseColor(_plugin.TrajectoryColor.Value, new Color(0.25f, 1f, 0.35f));
            int markerEvery = Mathf.Max(0, _plugin.TrajectoryMarkerEvery.Value);

            _material.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);

            Vector3 right = camera.transform.right;
            for (int pass = 0; pass < passes; pass++)
            {
                float offset = (pass - (passes - 1) * 0.5f) * ThicknessStep;
                GL.Begin(GL.LINES);
                for (int i = 1; i < _nodes.Count; i++)
                {
                    TrajectoryNode a = _nodes[i - 1];
                    TrajectoryNode b = _nodes[i];
                    Color color = gradient ? SpeedColor(b.Speed, opacity) : WithAlpha(single, opacity);
                    GL.Color(color);
                    GL.Vertex(a.Position + right * offset);
                    GL.Vertex(b.Position + right * offset);
                }
                GL.End();
            }

            if (markerEvery > 0)
            {
                GL.Begin(GL.LINES);
                for (int i = 0; i < _nodes.Count; i += markerEvery)
                {
                    TrajectoryNode node = _nodes[i];
                    Color color = WithAlpha(single, opacity);
                    GL.Color(color);
                    const float s = 0.06f;
                    GL.Vertex(node.Position + Vector3.up * s);
                    GL.Vertex(node.Position - Vector3.up * s);
                    GL.Vertex(node.Position + right * s);
                    GL.Vertex(node.Position - right * s);
                }
                GL.End();
            }

            if (_hoverIndex >= 0 && _hoverIndex < _nodes.Count)
            {
                GL.Begin(GL.LINES);
                GL.Color(Color.white);
                Vector3 p = _hoverWorld;
                const float h = 0.25f;
                GL.Vertex(p + Vector3.up * h);
                GL.Vertex(p - Vector3.up * h);
                GL.Vertex(p + right * h);
                GL.Vertex(p - right * h);
                GL.End();
                if (_plugin.TrajectoryHoverHitbox.Value)
                    DrawHoverHitbox();
            }
            GL.PopMatrix();
        }

        // ---- Optimizer overlay: baseline (white), current best (cyan), live candidate (dark teal),
        // plus spatial boxes (goal green, keep-out red, waypoint yellow) as GL wireframes.

        readonly List<Vector3> _optBaseline = new List<Vector3>();
        readonly List<Vector3> _optBest = new List<Vector3>();
        readonly List<Vector3> _optCandidate = new List<Vector3>();
        OptimBoxSet _optBoxes;
        OptimGeometryProbe _optGeometry;
        bool _optShowWhiskers;
        int _optimizerPathVersion;
        /// <summary>Bumped whenever the three path lists are replaced or cleared, so the GUI can
        /// re-push them after an external clear (a scene change) instead of trusting stale refs.</summary>
        public int OptimizerPathVersion { get { return _optimizerPathVersion; } }
        static readonly Color OptBaselineColor = new Color(1f, 1f, 1f, 0.85f);
        static readonly Color OptBestColor = new Color(0.2f, 1f, 0.95f, 0.95f);
        static readonly Color OptCandidateColor = new Color(0f, 0.45f, 0.45f, 0.9f);

        /// <summary>Replaces the three optimizer paths. Pass null to leave a path unchanged-empty.</summary>
        public void SetOptimizerPaths(IList<Vector3> baseline, IList<Vector3> best, IList<Vector3> candidate)
        {
            CopyInto(_optBaseline, baseline);
            CopyInto(_optBest, best);
            CopyInto(_optCandidate, candidate);
            _optimizerPathVersion++;
        }

        public void SetOptimizerBoxes(OptimBoxSet boxes)
        {
            _optBoxes = boxes;
        }

        /// <summary>Last bonk probe and whether the optimizer tab wants its whisker fan drawn.</summary>
        public void SetOptimizerGeometry(OptimGeometryProbe probe, bool visible)
        {
            _optGeometry = probe;
            _optShowWhiskers = visible;
        }

        public void ClearOptimizer()
        {
            ClearOptimizerPaths();
            _optBoxes = null;
        }

        /// <summary>Clears the three paths but leaves the spatial boxes in place.</summary>
        public void ClearOptimizerPaths()
        {
            _optBaseline.Clear();
            _optBest.Clear();
            _optCandidate.Clear();
            _optGeometry = null;
            _optimizerPathVersion++;
        }

        static void CopyInto(List<Vector3> target, IList<Vector3> source)
        {
            target.Clear();
            if (source != null)
                target.AddRange(source);
        }

        void DrawOptimizerOverlay(Camera camera)
        {
            bool hasPath = _optBaseline.Count >= 2 || _optBest.Count >= 2 || _optCandidate.Count >= 2;
            bool hasBoxes = _optBoxes != null && _optBoxes.Boxes.Count > 0;
            bool hasWhiskers = _optShowWhiskers && _optGeometry != null && _optGeometry.HasImpact &&
                               _optGeometry.Whiskers.Count > 0;
            if (!hasPath && !hasBoxes && !hasWhiskers)
                return;
            EnsureMaterial();
            _material.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            DrawPolyline(_optBaseline, OptBaselineColor);
            DrawPolyline(_optBest, OptBestColor);
            DrawPolyline(_optCandidate, OptCandidateColor);
            if (hasBoxes)
            {
                foreach (OptimBox box in _optBoxes.Boxes)
                {
                    if (!box.Enabled)
                        continue;
                    DrawBoxWire(box, BoxColor(box.Kind));
                }
            }
            if (hasWhiskers)
            {
                // Debug probes read best through walls and floors, then the normal depth test is
                // restored for the next frame's paths and boxes.
                _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _material.SetPass(0);
                DrawGeometryWhiskers(_optGeometry);
                _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                _material.SetPass(0);
            }
            GL.PopMatrix();
        }

        /// <summary>
        /// The geometry probe fan: red rays for wall hits, a bright green line for the chosen
        /// clearance direction and a yellow cross where the nearest corner / doorway lip is.
        /// </summary>
        static void DrawGeometryWhiskers(OptimGeometryProbe probe)
        {
            Vector3 origin = probe.ProbeOrigin;
            GL.Begin(GL.LINES);
            for (int i = 0; i < probe.Whiskers.Count; i++)
            {
                OptimWhisker whisker = probe.Whiskers[i];
                if (!whisker.Hit)
                    continue;
                float alpha = Mathf.Lerp(0.85f, 0.15f, Mathf.Clamp01(whisker.Distance / 20f));
                GL.Color(new Color(1f, 0.16f, 0.1f, alpha));
                GL.Vertex(origin);
                GL.Vertex(whisker.Point);
            }
            GL.End();

            Vector3 direction = probe.ClearanceDirection;
            float length = Mathf.Clamp(probe.ClearanceDistance, 2f, 8f);
            Vector3 tip = origin + direction * length;
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            GL.Begin(GL.LINES);
            GL.Color(new Color(0.25f, 1f, 0.3f, 0.95f));
            GL.Vertex(origin);
            GL.Vertex(tip);
            GL.Vertex(tip);
            GL.Vertex(tip - direction * 0.4f + side * 0.18f);
            GL.Vertex(tip);
            GL.Vertex(tip - direction * 0.4f - side * 0.18f);
            GL.End();

            if (probe.CornerFound)
            {
                const float s = 0.3f;
                Vector3 p = probe.CornerPoint;
                GL.Begin(GL.LINES);
                GL.Color(new Color(1f, 0.92f, 0.15f, 0.95f));
                GL.Vertex(p + Vector3.up * s);
                GL.Vertex(p - Vector3.up * s);
                GL.Vertex(p + Vector3.right * s);
                GL.Vertex(p - Vector3.right * s);
                GL.Vertex(p + Vector3.forward * s);
                GL.Vertex(p - Vector3.forward * s);
                GL.End();
            }
        }

        static void DrawPolyline(List<Vector3> points, Color color)
        {
            if (points.Count < 2)
                return;
            GL.Begin(GL.LINES);
            GL.Color(color);
            for (int i = 1; i < points.Count; i++)
            {
                GL.Vertex(points[i - 1]);
                GL.Vertex(points[i]);
            }
            GL.End();
        }

        static Color BoxColor(OptimBoxKind kind)
        {
            switch (kind)
            {
                case OptimBoxKind.Goal: return new Color(0.2f, 1f, 0.2f, 0.9f);
                case OptimBoxKind.KeepOut: return new Color(1f, 0.2f, 0.2f, 0.9f);
                default: return new Color(1f, 0.95f, 0.2f, 0.9f);
            }
        }

        // Corner index bit layout: bit0=x, bit1=y, bit2=z. Pairs differ in exactly one bit.
        static readonly int[] OptBoxEdges =
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7
        };

        static void DrawBoxWire(OptimBox box, Color color)
        {
            Vector3 lo = box.Min;
            Vector3 hi = box.Max;
            Vector3[] c = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                c[i] = new Vector3(
                    (i & 1) == 0 ? lo.x : hi.x,
                    (i & 2) == 0 ? lo.y : hi.y,
                    (i & 4) == 0 ? lo.z : hi.z);
            }
            GL.Begin(GL.LINES);
            GL.Color(color);
            for (int e = 0; e < 12; e++)
            {
                int a = OptBoxEdges[e * 2];
                int b = OptBoxEdges[e * 2 + 1];
                GL.Vertex(c[a]);
                GL.Vertex(c[b]);
            }
            GL.End();
        }

        /// <summary>
        /// The local player bounding box at the hovered tick: the same 12 world-space edges and
        /// green as the "Show player hitbox" overlay, drawn where the player was then. The box is
        /// captured with the node, so a tick spent sliding shows the shorter slide box. Always-on-top
        /// like the overlay, then the depth test is restored.
        /// </summary>
        void DrawHoverHitbox()
        {
            if (_hoverIndex < 0 || _hoverIndex >= _nodes.Count)
                return;
            TrajectoryNode node = _nodes[_hoverIndex];
            if (!node.HasHitbox)
                return;
            EnsureMaterial();
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.SetPass(0);

            Vector3 center = node.Position + node.HitboxCenter;
            Vector3 half = node.HitboxSize * 0.5f;
            Vector3 min = center - half;
            Vector3 max = center + half;

            GL.Begin(GL.LINES);
            GL.Color(PlayerBoxColor);
            for (int e = 0; e < BoxEdges.Length; e += 2)
            {
                GL.Vertex(BoxCorner(min, max, BoxEdges[e]));
                GL.Vertex(BoxCorner(min, max, BoxEdges[e + 1]));
            }
            GL.End();
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
        }

        static Vector3 BoxCorner(Vector3 min, Vector3 max, int index)
        {
            return new Vector3((index & 1) != 0 ? max.x : min.x,
                               (index & 2) != 0 ? max.y : min.y,
                               (index & 4) != 0 ? max.z : min.z);
        }

        /// <summary>Screen-space tooltip and notice. Called from Plugin.OnGUI.</summary>
        public void DrawOverlay()
        {
            if (_hoverIndex >= 0 && _tooltip.Length > 0 && ShouldDraw())
            {
                Vector2 mouse = Input.mousePosition;
                float x = mouse.x + 16f;
                float y = Screen.height - mouse.y + 8f;
                int lines = 1;
                for (int i = 0; i < _tooltip.Length; i++)
                {
                    if (_tooltip[i] == '\n')
                        lines++;
                }
                Rect rect = new Rect(x, y, 300f, lines * 18f + 10f);
                if (rect.xMax > Screen.width)
                    rect.x = mouse.x - 16f - rect.width;
                GUI.Box(rect, _tooltip);
            }

            if (_notice.Length > 0 && TimeController.RealTime < _noticeUntil)
                GUI.Label(new Rect(16f, Screen.height - 48f, Screen.width - 32f, 24f), _notice);
        }

        /// <summary>Tooltip text for one recorded tick. The hover always snaps to a sample.</summary>
        string BuildTooltip(TrajectoryNode node)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("Tick ").Append(node.Tick);
            sb.Append("  t=")
              .Append(node.Time.ToString("F4", CultureInfo.InvariantCulture)).Append("s")
              .Append("  timescale ").Append(node.TimeScale.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            string bulk = DescribeBulkFrame(node.Tick);
            if (bulk.Length > 0)
                sb.Append(bulk).Append('\n');
            sb.Append("Speed ").Append(node.Speed.ToString("F2", CultureInfo.InvariantCulture)).Append(" hu/s\n");
            sb.Append("Vel ")
              .Append(node.Velocity.x.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
              .Append(node.Velocity.y.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
              .Append(node.Velocity.z.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Pitch ").Append(node.Pitch.ToString("F2", CultureInfo.InvariantCulture))
              .Append("  Yaw ").Append(node.Yaw.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Ground ").Append(node.Grounded ? "yes" : "no")
              .Append("  Slide ").Append(node.Sliding ? "yes" : "no")
              .Append("  Dash ").Append(node.Dashing ? "yes" : "no").Append('\n');
            sb.Append("Slam now ").Append(node.SlamNowKnown ? (node.Slamming ? "yes" : "no") : "-")
              .Append("  can slam ").Append(node.SlamKnown ? (node.SlamReady ? "yes" : "no") : "-")
              .Append("  Wall ").Append(node.WallKnown ? (node.TouchingWall ? "yes" : "no") : "-").Append('\n');
            sb.Append("HP ").Append(node.Health.ToString("F0", CultureInfo.InvariantCulture))
              .Append("  Hard ").Append(node.HardDamage.ToString("F0", CultureInfo.InvariantCulture))
              .Append("  Stamina ").Append(node.Stamina.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Held ").Append(node.Held == TasButtons.None ? "-" : TasInputColumns.FormatActions(node.Held))
              .Append("  Weapon slot ").Append(node.WeaponSlot);
            return sb.ToString();
        }

        /// <summary>
        /// How far into its own framebulk a tick is, for the hover tooltip, for example
        /// "Framebulk line 12: frame 4 / 10". Empty when no script is loaded.
        /// </summary>
        string DescribeBulkFrame(int tick)
        {
            TasEngine engine = _plugin.Engine;
            TasScript script = engine != null ? engine.Script : null;
            if (script == null || script.Lines.Count == 0)
                return string.Empty;
            TickLocation location = script.Locate(tick);
            if (!location.Valid || location.LineIndex < 0 || location.LineIndex >= script.Lines.Count)
                return string.Empty;
            TasFrameLine line = script.Lines[location.LineIndex];
            int ticks = line.Count < 1 ? 1 : line.Count;
            int frame = Mathf.Clamp(location.TickWithinLine + 1, 1, ticks);
            return "Framebulk line " + (location.LineIndex + 1) + ": frame " + frame + " / " + ticks;
        }

        void EnsureMaterial()
        {
            if (_material != null)
                return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _material = new Material(shader);
            _material.hideFlags = HideFlags.HideAndDontSave;
            _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
        }

        static Color SpeedColor(float speed, float opacity)
        {
            float t = Mathf.Clamp01(speed / GradientTopSpeed);
            Color c = t < 0.5f
                ? Color.Lerp(SlowColor, MidColor, t * 2f)
                : Color.Lerp(MidColor, FastColor, (t - 0.5f) * 2f);
            return WithAlpha(c, opacity);
        }

        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;
            string hex = text.TrimStart('#');
            if (hex.Length != 6)
                return fallback;
            int value;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return fallback;
            return new Color(((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f, 1f);
        }
    }
}
