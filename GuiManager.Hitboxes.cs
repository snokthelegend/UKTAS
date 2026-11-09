using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// "Show hitboxes": outlines the colliders on active enemies (body hitboxes, limb
    /// hitboxes and hurtboxes) and optionally the local player's collision bounds.
    /// Read-only: colliders are never enabled, disabled or resized.
    /// </summary>
    public sealed partial class GuiManager
    {
        sealed class HitboxMarker
        {
            public Collider Collider;
            public bool IsPlayer;
        }

        const int HitboxScanInterval = 20;
        const int MaxHitboxMarkers = 512;
        const float HitboxRange = 150f;

        readonly List<HitboxMarker> _hitboxes = new List<HitboxMarker>(256);
        int _hitboxScanFrame = -1000;

        /// <summary>Called from UpdateCache (every 10 frames). Cached, never per-Repaint.</summary>
        void RefreshHitboxOverlay()
        {
            // NoRefresh draws nothing, so the scene-wide collider scan is skipped entirely.
            // The cached markers are kept: the overlay is back the instant rendering returns.
            if (_engine != null && _engine.RenderSuppressed)
                return;

            bool showEnemies = _plugin.ShowHitboxes.Value;
            bool showPlayer = _plugin.ShowPlayerHitbox.Value;
            if (!showEnemies && !showPlayer)
            {
                _hitboxes.Clear();
                return;
            }
            if (Time.frameCount - _hitboxScanFrame < HitboxScanInterval)
                return;
            _hitboxScanFrame = Time.frameCount;
            _hitboxes.Clear();

            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera))
                camera = null;
            Vector3 origin = camera != null ? camera.position : Vector3.zero;
            HashSet<int> seen = new HashSet<int>();

            // Player first: its marker must not be crowded out by the enemy cap.
            if (showPlayer)
                ScanPlayerHitbox(seen, origin);
            if (showEnemies)
                ScanEnemyHitboxes(seen, origin);
        }

        void ScanPlayerHitbox(HashSet<int> seen, Vector3 origin)
        {
            Component player = _bridge.PlayerComponent;
            if (player == null)
                return;

            // NewMovement.playerCollider is the capsule that collides with walls and
            // that trigger volumes test against. Prefer it over helper colliders
            // (such as the portal-aware clone) that share the player hierarchy.
            Collider playerCollider = _bridge.GetMember(player, new[] { "playerCollider" }) as Collider;
            if (playerCollider != null && playerCollider.enabled && playerCollider.gameObject.activeInHierarchy)
            {
                if (seen.Add(playerCollider.GetInstanceID()) && _hitboxes.Count < MaxHitboxMarkers)
                    _hitboxes.Add(new HitboxMarker { Collider = playerCollider, IsPlayer = true });
                return;
            }

            // Fallback for a renamed field or an unusual player rig: use non-trigger
            // colliders first, then everything if there is no wall-collision shape.
            Collider[] colliders;
            try
            {
                colliders = player.GetComponentsInChildren<Collider>(true);
            }
            catch
            {
                return;
            }
            int before = _hitboxes.Count;
            AddHitboxColliders(colliders, seen, origin, true, false);
            if (_hitboxes.Count == before)
                AddHitboxColliders(colliders, seen, origin, true, true);
        }

        void ScanEnemyHitboxes(HashSet<int> seen, Vector3 origin)
        {
            Type enemyType = _bridge.FindType("EnemyIdentifier");
            if (enemyType == null)
                return;
            UnityEngine.Object[] enemies;
            try
            {
                enemies = UnityEngine.Object.FindObjectsOfType(enemyType, false);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < enemies.Length; i++)
            {
                Component enemy = enemies[i] as Component;
                if (enemy == null)
                    continue;
                AddHitboxColliders(enemy, seen, origin, false, true);

                // Some limbs live outside the enemy hierarchy and point back to it.
                Array limbs = _bridge.GetMember(enemy, new[] { "limbs" }) as Array;
                if (limbs == null)
                    continue;
                for (int j = 0; j < limbs.Length; j++)
                {
                    Component limb = limbs.GetValue(j) as Component;
                    if (limb != null)
                        AddHitboxColliders(limb, seen, origin, false, true);
                }
            }
        }

        void AddHitboxColliders(Component owner, HashSet<int> seen, Vector3 origin,
                                bool isPlayer, bool includeTriggers)
        {
            if (owner == null)
                return;
            Collider[] colliders;
            try
            {
                colliders = owner.GetComponentsInChildren<Collider>(true);
            }
            catch
            {
                return;
            }
            AddHitboxColliders(colliders, seen, origin, isPlayer, includeTriggers);
        }

        void AddHitboxColliders(Collider[] colliders, HashSet<int> seen, Vector3 origin,
                                bool isPlayer, bool includeTriggers)
        {
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (!includeTriggers && collider.isTrigger)
                    continue;
                if (!seen.Add(collider.GetInstanceID()))
                    continue;
                // The player is shown even from freecam far outside the enemy range.
                if (!isPlayer && (collider.bounds.center - origin).sqrMagnitude > HitboxRange * HitboxRange)
                    continue;
                if (_hitboxes.Count >= MaxHitboxMarkers)
                    return;
                _hitboxes.Add(new HitboxMarker { Collider = collider, IsPlayer = isPlayer });
            }
        }

        void ResetHitboxes()
        {
            _hitboxes.Clear();
            _hitboxScanFrame = -1000;
        }

        /// <summary>Called from OnGUI. Draws cached collider bounds as screen-space boxes.</summary>
        void DrawHitboxOverlay()
        {
            // NoRefresh: no 3D screen, so no hitbox overlay and no GL draw calls.
            if (_engine != null && _engine.RenderSuppressed)
                return;
            if ((!_plugin.ShowHitboxes.Value && !_plugin.ShowPlayerHitbox.Value) || _hitboxes.Count == 0)
                return;
            if (Event.current.type != EventType.Repaint)
                return;

            Camera camera = _overlayCamera;
            if (camera == null)
            {
                Transform cameraTransform;
                if (_bridge.TryGetCameraTransform(out cameraTransform) && cameraTransform != null)
                {
                    camera = cameraTransform.GetComponent<Camera>();
                    if (camera == null)
                        camera = cameraTransform.GetComponentInChildren<Camera>();
                }
                if (camera == null)
                    camera = Camera.main;
                _overlayCamera = camera;
            }
            if (camera == null || !camera.isActiveAndEnabled || !EnsureLineMaterial())
                return;

            float width = Screen.width;
            float height = Screen.height;
            Color enemyColor = new Color(1f, 0.22f, 0.22f, 0.9f);
            Color playerColor = new Color(0.25f, 1f, 0.45f, 0.95f);

            Plane near = NearPlane(camera);
            GL.PushMatrix();
            _lineMaterial.SetPass(0);
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            for (int i = 0; i < _hitboxes.Count; i++)
            {
                HitboxMarker marker = _hitboxes[i];
                Collider collider = marker.Collider;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                GL.Color(marker.IsPlayer ? playerColor : enemyColor);
                Bounds bounds = collider.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                for (int e = 0; e < BoxEdges.Length; e += 2)
                {
                    Vector3 a = HitboxCorner(min, max, BoxEdges[e]);
                    Vector3 b = HitboxCorner(min, max, BoxEdges[e + 1]);
                    Vector3 clippedA;
                    Vector3 clippedB;
                    if (!ClipSegmentToNear(a, b, near, out clippedA, out clippedB))
                        continue;
                    Vector3 va = ToViewport(camera, clippedA);
                    Vector3 vb = ToViewport(camera, clippedB);
                    if (va.z <= 0f || vb.z <= 0f)
                        continue;
                    GL.Vertex3(va.x * width, va.y * height, 0f);
                    GL.Vertex3(vb.x * width, vb.y * height, 0f);
                }
            }
            GL.End();
            GL.PopMatrix();
        }

        static Vector3 HitboxCorner(Vector3 min, Vector3 max, int index)
        {
            return new Vector3((index & 1) != 0 ? max.x : min.x,
                               (index & 2) != 0 ? max.y : min.y,
                               (index & 4) != 0 ? max.z : min.z);
        }
    }
}
