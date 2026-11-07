using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>One simulation tick of a coin's flight.</summary>
    public struct CoinPathPoint
    {
        public int Tick;
        public Vector3 Position;
    }

    /// <summary>
    /// One coin's trail. Copied out of the coin, so it survives the coin being punched away,
    /// shot, pooled or destroyed; a punched coin is a new object with its own trail.
    /// </summary>
    public sealed class CoinPath
    {
        public int Id;
        public int LastTick = -1;
        /// <summary>The coin is gone (shot, punched, expired, despawned): the arc is complete.</summary>
        public bool Closed;
        /// <summary>This is the coin a punch spawned (power &gt; 1 / "NewCoin").</summary>
        public bool Punched;
        public bool Shot;
        public bool ShotByEnemy;
        public bool Targeting;
        /// <summary>Where the coin was when it was shot or punched, for the event marker.</summary>
        public Vector3 EventPoint;
        public bool HasEventPoint;
        /// <summary>The enemy the coin was tracking, drawn as a dashed line from the event point.</summary>
        public Vector3 TargetPoint;
        public bool HasTargetPoint;
        public readonly List<CoinPathPoint> Points = new List<CoinPathPoint>(512);
    }

    /// <summary>
    /// Coin path visualizer: samples every live coin once per simulation tick and draws the
    /// trails with GL lines after the main camera renders.
    ///
    ///   yellow   in flight
    ///   green    punched coin (the new coin a coin punch spawns)
    ///   cyan     shot / ricoshot coin
    ///   red      coin that an enemy shot back
    ///   magenta  coin tracking an enemy, with a dashed line to it
    ///
    /// A trail is kept after its coin is gone, so a full arc stays readable; the script
    /// command "clearcoinpaths" (field 8) wipes them, as does a level load. Reading only:
    /// the visualizer never writes to a coin, the player or the camera.
    /// </summary>
    public sealed class CoinPathManager
    {
        const int MaxTrails = 256;
        const int MaxPointsPerTrail = 8000;
        const float ThicknessStep = 0.02f;
        const float DashLength = 0.6f;

        static readonly Color FlightColor = new Color(1f, 0.83f, 0.25f);
        static readonly Color PunchedColor = new Color(0.35f, 1f, 0.5f);
        static readonly Color ShotColor = new Color(0.3f, 0.8f, 1f);
        static readonly Color EnemyShotColor = new Color(1f, 0.35f, 0.35f);
        static readonly Color TargetColor = new Color(1f, 0.3f, 0.85f);

        readonly Plugin _plugin;
        readonly List<CoinPath> _paths = new List<CoinPath>(64);
        readonly List<GameBridge.CoinInfo> _coins = new List<GameBridge.CoinInfo>(16);
        readonly HashSet<int> _seen = new HashSet<int>();
        Material _material;
        int _lastSampledTick = -1;

        public CoinPathManager(Plugin plugin)
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

        public int Count { get { return _paths.Count; } }

        public IList<CoinPath> Paths { get { return _paths; } }

        /// <summary>Drops every recorded trail. Called by the 'clearcoinpaths' command.</summary>
        public void Clear()
        {
            _paths.Clear();
            _lastSampledTick = -1;
        }

        /// <summary>
        /// Records the coins of one simulation tick. Called once per frame from Plugin, next to
        /// the player trajectory. A tick that is not newer than the last sample truncates the
        /// points after it, so a rewind replaces the old continuation instead of stacking.
        /// </summary>
        public void Sample(int tick)
        {
            if (_plugin.CoinPathEnabled == null || !_plugin.CoinPathEnabled.Value)
                return;
            if (tick == _lastSampledTick)
                return;
            GameBridge bridge = _plugin.Bridge;
            if (bridge == null)
                return;
            _lastSampledTick = tick;

            int count = bridge.CollectCoins(_coins);
            _seen.Clear();
            for (int i = 0; i < count; i++)
            {
                GameBridge.CoinInfo coin = _coins[i];
                _seen.Add(coin.Id);

                CoinPath path = Find(coin.Id);
                if (path == null)
                {
                    path = new CoinPath { Id = coin.Id };
                    _paths.Add(path);
                    TrimTrails();
                }

                if (path.LastTick >= 0 && tick < path.LastTick)
                {
                    int keep = FirstPointAfter(path, tick);
                    if (keep < path.Points.Count)
                        path.Points.RemoveRange(keep, path.Points.Count - keep);
                }

                if (path.Points.Count == 0 || path.Points[path.Points.Count - 1].Tick != tick)
                {
                    path.Points.Add(new CoinPathPoint { Tick = tick, Position = coin.Position });
                    if (path.Points.Count > MaxPointsPerTrail)
                        path.Points.RemoveRange(0, path.Points.Count - MaxPointsPerTrail);
                }

                path.LastTick = tick;
                path.Closed = false;
                if (coin.Power > 1f ||
                    coin.Name.StartsWith("NewCoin", System.StringComparison.OrdinalIgnoreCase))
                    path.Punched = true;
                if (coin.Shot)
                    path.Shot = true;
                if (coin.ShotByEnemy)
                    path.ShotByEnemy = true;
                if (coin.HasTargetPoint)
                {
                    path.Targeting = true;
                    path.TargetPoint = coin.TargetPoint;
                    path.HasTargetPoint = true;
                }
                if (!path.HasEventPoint && (coin.Shot || coin.ShotByEnemy))
                {
                    path.EventPoint = coin.Position;
                    path.HasEventPoint = true;
                    // A ricochet picks the nearest live enemy it can see; when the coin itself
                    // does not expose a target, resolve that enemy here so the line to what the
                    // shot went for can still be drawn.
                    if (!path.HasTargetPoint)
                    {
                        Vector3 enemy;
                        if (TryFindEnemy(path.EventPoint, out enemy))
                        {
                            path.Targeting = true;
                            path.TargetPoint = enemy;
                            path.HasTargetPoint = true;
                        }
                    }
                }
            }

            for (int i = 0; i < _paths.Count; i++)
            {
                CoinPath path = _paths[i];
                if (!path.Closed && !_seen.Contains(path.Id))
                    path.Closed = true;
            }
        }

        /// <summary>
        /// Aim point of the nearest live enemy a ricochet from <paramref name="origin"/> would
        /// pick. False when no enemy is alive.
        /// </summary>
        bool TryFindEnemy(Vector3 origin, out Vector3 point)
        {
            point = Vector3.zero;
            GameBridge bridge = _plugin.Bridge;
            if (bridge == null)
                return false;
            EntityRegistry.Entity enemy;
            float distance;
            if (!bridge.Entities.TryFindNearest("enemy", origin, out enemy, out distance) || enemy == null)
                return false;
            if (bridge.Entities.TryGetAimPosition(enemy.Id, out point))
                return true;
            if (enemy.Component == null)
                return false;
            point = enemy.Component.transform.position;
            return true;
        }

        CoinPath Find(int id)
        {
            for (int i = 0; i < _paths.Count; i++)
            {
                if (_paths[i].Id == id)
                    return _paths[i];
            }
            return null;
        }

        /// <summary>Index of the first point recorded after <paramref name="tick"/>.</summary>
        static int FirstPointAfter(CoinPath path, int tick)
        {
            for (int i = 0; i < path.Points.Count; i++)
            {
                if (path.Points[i].Tick > tick)
                    return i;
            }
            return path.Points.Count;
        }

        void TrimTrails()
        {
            while (_paths.Count > MaxTrails)
            {
                int drop = 0;
                for (int i = 0; i < _paths.Count; i++)
                {
                    if (_paths[i].Closed)
                    {
                        drop = i;
                        break;
                    }
                }
                _paths.RemoveAt(drop);
            }
        }

        bool ShouldDraw()
        {
            if (_plugin.CoinPathEnabled == null || !_plugin.CoinPathEnabled.Value)
                return false;
            // Offline video frames stay free of the overlay, like the player trajectory.
            if (_plugin.Video != null && _plugin.Video.Rendering)
                return false;
            if (_plugin.CleanRecordingActive)
                return false;
            TasEngine engine = _plugin.Engine;
            if (engine != null && engine.RenderSuppressed)
                return false;
            return true;
        }

        static Color ColorFor(CoinPath path, float opacity)
        {
            Color color;
            if (path.ShotByEnemy)
                color = EnemyShotColor;
            else if (path.Shot)
                color = ShotColor;
            else if (path.Punched)
                color = PunchedColor;
            else if (path.Targeting)
                color = TargetColor;
            else
                color = FlightColor;
            color.a = Mathf.Clamp01(path.Closed ? opacity * 0.65f : opacity);
            return color;
        }

        void OnCameraPostRender(Camera camera)
        {
            if (camera == null || camera != Camera.main || _paths.Count == 0 || !ShouldDraw())
                return;
            EnsureMaterial();

            float opacity = Mathf.Clamp01(_plugin.CoinPathOpacity != null ? _plugin.CoinPathOpacity.Value : 0.9f);
            int passes = Mathf.Clamp(Mathf.RoundToInt(_plugin.CoinPathThickness != null ? _plugin.CoinPathThickness.Value : 2f), 1, 6);
            Vector3 right = camera.transform.right;

            _material.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);

            for (int pass = 0; pass < passes; pass++)
            {
                float offset = (pass - (passes - 1) * 0.5f) * ThicknessStep;
                GL.Begin(GL.LINES);
                for (int p = 0; p < _paths.Count; p++)
                {
                    CoinPath path = _paths[p];
                    if (path.Points.Count < 2)
                        continue;
                    GL.Color(ColorFor(path, opacity));
                    for (int i = 1; i < path.Points.Count; i++)
                    {
                        GL.Vertex(path.Points[i - 1].Position + right * offset);
                        GL.Vertex(path.Points[i].Position + right * offset);
                    }
                }
                GL.End();
            }

            GL.Begin(GL.LINES);
            for (int p = 0; p < _paths.Count; p++)
            {
                CoinPath path = _paths[p];
                if (path.HasEventPoint)
                {
                    // Shot / punch event: a small cross at the point it happened.
                    Color eventColor = path.ShotByEnemy ? EnemyShotColor : ShotColor;
                    eventColor.a = opacity;
                    GL.Color(eventColor);
                    Cross(path.EventPoint, right, 0.3f);
                }
                if (path.HasEventPoint && path.HasTargetPoint)
                {
                    // Dashed line to the enemy the coin went for.
                    Color targetColor = TargetColor;
                    targetColor.a = opacity;
                    GL.Color(targetColor);
                    Dashed(path.EventPoint, path.TargetPoint);
                }
                else if (path.Targeting && path.Points.Count > 0 && path.HasTargetPoint)
                {
                    Color targetColor = TargetColor;
                    targetColor.a = opacity;
                    GL.Color(targetColor);
                    Dashed(path.Points[path.Points.Count - 1].Position, path.TargetPoint);
                }
            }
            GL.End();

            // A live coin gets a small head cross so the newest position is readable.
            GL.Begin(GL.LINES);
            for (int p = 0; p < _paths.Count; p++)
            {
                CoinPath path = _paths[p];
                if (path.Closed || path.Points.Count == 0)
                    continue;
                GL.Color(ColorFor(path, opacity));
                Cross(path.Points[path.Points.Count - 1].Position, right, 0.15f);
            }
            GL.End();

            GL.PopMatrix();
        }

        static void Cross(Vector3 point, Vector3 right, float size)
        {
            GL.Vertex(point + Vector3.up * size);
            GL.Vertex(point - Vector3.up * size);
            GL.Vertex(point + right * size);
            GL.Vertex(point - right * size);
            GL.Vertex(point + Vector3.forward * size);
            GL.Vertex(point - Vector3.forward * size);
        }

        static void Dashed(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-4f)
                return;
            Vector3 direction = delta / length;
            for (float at = 0f; at < length; at += DashLength * 2f)
            {
                float end = Mathf.Min(at + DashLength, length);
                GL.Vertex(from + direction * at);
                GL.Vertex(from + direction * end);
            }
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
    }
}
