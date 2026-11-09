using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// "Show triggers": draws the level's trigger and event volumes near the camera, as
    /// solid translucent boxes (optional) under their outlines, with their names. Read-only.
    /// The scene is scanned on a slow timer; the distance and frustum filters run on the HUD
    /// timer; drawing only projects the cached boxes.
    /// </summary>
    public sealed partial class GuiManager
    {
        sealed class TriggerMarker
        {
            public Component Owner;
            /// <summary>The trigger collider to outline, or null for a point marker at the owner.</summary>
            public Collider Collider;
            public string Label = string.Empty;
        }

        // Level volume scripts whose names do not contain Trigger or Event. All verified in
        // Assembly-CSharp.dll; a missing one is skipped with a logged warning.
        static readonly string[] TriggerExtraTypeNames =
        {
            "ObjectActivator", "ActivateArena", "CheckPoint", "DoorOpener", "HurtZone", "DeathZone",
            "OutOfBounds", "PlayerActivator", "FinalPit", "SecretMissionPit"
        };
        static readonly string[] OverlayCameraNames = { "cam", "camera" };
        // The 12 edges of a box, as corner index pairs (corner bit 0 = x, 1 = y, 2 = z).
        static readonly int[] BoxEdges =
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7
        };
        // The 6 box faces, as 4 corner indices in ring order (same corner bits).
        static readonly int[] BoxFaces =
        {
            0, 2, 6, 4,   // x = min
            1, 3, 7, 5,   // x = max
            0, 1, 5, 4,   // y = min
            2, 3, 7, 6,   // y = max
            0, 1, 3, 2,   // z = min
            4, 5, 7, 6    // z = max
        };
        static readonly Vector3[] BoxNormals =
        {
            Vector3.left, Vector3.right, Vector3.down, Vector3.up, Vector3.back, Vector3.forward
        };

        const int TriggerScanInterval = 600;
        const int MaxTriggerMarkers = 128;
        const int MaxTriggerScan = 4000;

        readonly List<Type> _triggerTypes = new List<Type>(64);
        readonly HashSet<Type> _triggerTypeSet = new HashSet<Type>();
        bool _triggerTypesResolved;
        readonly List<TriggerMarker> _triggerAll = new List<TriggerMarker>(256);
        readonly List<TriggerMarker> _triggerVisible = new List<TriggerMarker>(MaxTriggerMarkers);
        readonly List<Collider> _triggerColliders = new List<Collider>(8);
        readonly HashSet<int> _triggerSeen = new HashSet<int>();
        readonly Vector3[] _triggerCorners = new Vector3[8];
        readonly Vector3[] _triggerFace = new Vector3[4];
        readonly Vector3[] _triggerClipped = new Vector3[16];
        int _triggerScanFrame = -100000;
        int _triggerSceneHandle = int.MinValue;
        Camera _overlayCamera;
        Material _lineMaterial;
        bool _lineMaterialTried;
        GUIStyle _triggerLabelStyle;

        /// <summary>Game components whose names contain Trigger or Event, plus the known level volume scripts.</summary>
        void ResolveTriggerTypes()
        {
            if (_triggerTypesResolved)
                return;
            Type anchor = _bridge.FindType("ObjectActivator") ?? _bridge.FindType("EnemyIdentifier");
            if (anchor == null)
                return; // game assembly not found yet; try again on the next refresh
            _triggerTypesResolved = true;

            Type[] types;
            try
            {
                types = anchor.Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Show triggers: could not list game types: " + ex.Message);
                types = new Type[0];
            }
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || type.IsAbstract || type.ContainsGenericParameters ||
                    !typeof(Component).IsAssignableFrom(type))
                    continue;
                if (type.Name.IndexOf("Trigger", StringComparison.Ordinal) >= 0 ||
                    type.Name.IndexOf("Event", StringComparison.Ordinal) >= 0)
                {
                    _triggerTypes.Add(type);
                    _triggerTypeSet.Add(type);
                }
            }
            for (int i = 0; i < TriggerExtraTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(TriggerExtraTypeNames[i]);
                if (type == null)
                    _plugin.LogWarning("Show triggers: game type '" + TriggerExtraTypeNames[i] + "' not found; it is skipped.");
                else if (_triggerTypeSet.Add(type))
                {
                    _triggerTypes.Add(type);
                }
            }
            _plugin.LogInfo("Show triggers: " + _triggerTypes.Count + " trigger/event component types.");
        }

        bool _triggerWarned;

        /// <summary>Called from UpdateCache (every 10 frames). Never throws.</summary>
        void RefreshTriggerOverlay()
        {
            try
            {
                RefreshTriggerOverlayCore();
            }
            catch (Exception ex)
            {
                _triggerVisible.Clear();
                if (!_triggerWarned)
                {
                    _triggerWarned = true;
                    _plugin.LogWarning("Show triggers: refresh failed: " + ex.Message);
                }
            }
        }

        void RefreshTriggerOverlayCore()
        {
            // NoRefresh draws nothing, so the trigger scan is skipped (the cached list is
            // kept and is valid again as soon as rendering returns).
            if (_engine != null && _engine.RenderSuppressed)
                return;

            if (!_plugin.ShowTriggers.Value)
            {
                if (_triggerAll.Count > 0)
                {
                    _triggerAll.Clear();
                    _triggerVisible.Clear();
                    _triggerSceneHandle = int.MinValue;
                }
                return;
            }

            object cameraValue = _bridge.GetMember(_bridge.CameraController, OverlayCameraNames);
            _overlayCamera = cameraValue as Camera;
            if (_overlayCamera == null)
                _overlayCamera = Camera.main;

            int scene = SceneManager.GetActiveScene().handle;
            if (scene != _triggerSceneHandle || Time.frameCount - _triggerScanFrame >= TriggerScanInterval)
            {
                _triggerSceneHandle = scene;
                _triggerScanFrame = Time.frameCount;
                ScanTriggers();
            }
            FilterTriggers();
        }

        /// <summary>
        /// One collider pass plus one trigger-script pass. The old code called
        /// FindObjectsOfType once per type, which made enabling this overlay hitch the game.
        /// </summary>
        void ScanTriggers()
        {
            _triggerAll.Clear();
            ResolveTriggerTypes();
            _triggerSeen.Clear();
            if (_triggerTypes.Count == 0)
                return;

            HashSet<int> ownersWithCollider = new HashSet<int>();
            Collider[] colliders = null;
            try { colliders = UnityEngine.Object.FindObjectsOfType<Collider>(); }
            catch (Exception ex) { _plugin.LogWarning("Show triggers: collider scan failed: " + ex.Message); }
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length && _triggerAll.Count < MaxTriggerScan; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || !collider.enabled || !collider.isTrigger)
                        continue;
                    Component owner = FindTriggerOwner(collider.gameObject);
                    if (owner == null)
                        owner = collider;
                    ownersWithCollider.Add(collider.gameObject.GetInstanceID());
                    if (!_triggerSeen.Add(collider.GetInstanceID()))
                        continue;
                    _triggerAll.Add(new TriggerMarker
                    {
                        Owner = owner,
                        Collider = collider,
                        Label = TriggerLabel(owner, collider.gameObject.name)
                    });
                }
            }

            // Some level volumes are point markers or use a collider on a child. Find their
            // trigger components once instead of scanning the whole scene for every type.
            MonoBehaviour[] behaviours = null;
            try { behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(); }
            catch (Exception ex) { _plugin.LogWarning("Show triggers: script scan failed: " + ex.Message); }
            if (behaviours != null)
            {
                for (int i = 0; i < behaviours.Length && _triggerAll.Count < MaxTriggerScan; i++)
                {
                    MonoBehaviour behaviour = behaviours[i];
                    if (behaviour == null || !_triggerTypeSet.Contains(behaviour.GetType()))
                        continue;
                    int ownerId = behaviour.gameObject.GetInstanceID();
                    if (ownersWithCollider.Contains(ownerId))
                        continue;
                    if (!_triggerSeen.Add(ownerId))
                        continue;
                    _triggerAll.Add(new TriggerMarker
                    {
                        Owner = behaviour,
                        Label = TriggerLabel(behaviour, behaviour.gameObject.name)
                    });
                }
            }

            _plugin.LogInfo("Show triggers: " + _triggerAll.Count + " markers cached.");
        }

        Component FindTriggerOwner(GameObject gameObject)
        {
            Component[] components = gameObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && _triggerTypeSet.Contains(component.GetType()))
                    return component;
            }
            return null;
        }

        static string TriggerLabel(Component owner, string objectName)
        {
            string typeName = owner is Collider ? "Trigger" : owner.GetType().Name;
            return typeName + "  " + objectName;
        }

        void FilterTriggers()
        {
            _triggerVisible.Clear();
            Camera camera = _overlayCamera;
            if (camera == null)
                return;
            Vector3 eye = camera.transform.position;
            float range = Mathf.Clamp(_plugin.ShowTriggersDistance.Value, 5f, 500f);
            float rangeSq = range * range;
            Plane[] planes = null;
            try { planes = GeometryUtility.CalculateFrustumPlanes(camera); }
            catch { planes = null; }

            for (int i = 0; i < _triggerAll.Count && _triggerVisible.Count < MaxTriggerMarkers; i++)
            {
                TriggerMarker marker = _triggerAll[i];
                if (!GameBridge.IsAlive(marker.Owner) || !marker.Owner.gameObject.activeInHierarchy)
                    continue;
                float distanceSq;
                if (marker.Collider != null)
                {
                    if (!marker.Collider.enabled)
                        continue;
                    Bounds bounds = marker.Collider.bounds;
                    distanceSq = bounds.SqrDistance(eye);
                    // Frustum culling keeps partially visible boxes; it only drops boxes that
                    // are completely outside the view.
                    if (planes != null && !GeometryUtility.TestPlanesAABB(planes, bounds))
                        continue;
                }
                else
                {
                    distanceSq = (marker.Owner.transform.position - eye).sqrMagnitude;
                }
                if (distanceSq <= rangeSq)
                    _triggerVisible.Add(marker);
            }
        }

        bool EnsureLineMaterial()
        {
            if (_lineMaterial != null)
                return true;
            if (_lineMaterialTried)
                return false;
            _lineMaterialTried = true;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                _plugin.LogWarning("Show triggers: shader Hidden/Internal-Colored not found; drawing screen rectangles instead.");
                return false;
            }
            _lineMaterial = new Material(shader);
            _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return true;
        }

        /// <summary>Viewport point of a world position (x, y in 0..1, z = depth; z &lt;= 0 is behind the camera).</summary>
        static Vector3 ToViewport(Camera camera, Vector3 world)
        {
            return camera.WorldToViewportPoint(world);
        }

        static Plane NearPlane(Camera camera)
        {
            Vector3 forward = camera.transform.forward;
            return new Plane(forward, camera.transform.position + forward * camera.nearClipPlane);
        }

        static bool ClipSegmentToNear(Vector3 a, Vector3 b, Plane near, out Vector3 clippedA, out Vector3 clippedB)
        {
            float da = near.GetDistanceToPoint(a);
            float db = near.GetDistanceToPoint(b);
            bool insideA = da >= 0f;
            bool insideB = db >= 0f;
            clippedA = a;
            clippedB = b;
            if (!insideA && !insideB)
                return false;

            if (!insideA)
            {
                float t = da / (da - db);
                clippedA = a + (b - a) * t;
            }
            else if (!insideB)
            {
                float t = db / (db - da);
                clippedB = b + (a - b) * t;
            }
            return true;
        }

        /// <summary>Clips a convex polygon to the near plane, preserving winding.</summary>
        static int ClipPolygonToNear(Vector3[] input, int count, Plane near, Vector3[] output)
        {
            int outCount = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 current = input[i];
                Vector3 previous = input[(i + count - 1) % count];
                float dc = near.GetDistanceToPoint(current);
                float dp = near.GetDistanceToPoint(previous);
                bool currentInside = dc >= 0f;
                bool previousInside = dp >= 0f;
                if (currentInside)
                {
                    if (!previousInside && outCount < output.Length)
                    {
                        float t = dp / (dp - dc);
                        output[outCount++] = previous + (current - previous) * t;
                    }
                    if (outCount < output.Length)
                        output[outCount++] = current;
                }
                else if (previousInside && outCount < output.Length)
                {
                    float t = dp / (dp - dc);
                    output[outCount++] = previous + (current - previous) * t;
                }
            }
            return outCount;
        }

        /// <summary>Called from OnGUI. Draws only what FilterTriggers kept; no allocation.</summary>
        void DrawTriggerOverlay()
        {
            // NoRefresh: no 3D screen, so no trigger overlay and no GL draw calls.
            if (_engine != null && _engine.RenderSuppressed)
                return;
            if (!_plugin.ShowTriggers.Value || _triggerVisible.Count == 0)
                return;
            if (Event.current.type != EventType.Repaint)
                return;
            Camera camera = _overlayCamera;
            if (camera == null || !camera.isActiveAndEnabled)
                return;
            if (_triggerLabelStyle == null)
            {
                _triggerLabelStyle = new GUIStyle(GUI.skin.label);
                _triggerLabelStyle.fontSize = 11;
                _triggerLabelStyle.alignment = TextAnchor.LowerCenter;
                _triggerLabelStyle.wordWrap = false;
                _triggerLabelStyle.richText = false;
                _triggerLabelStyle.clipping = TextClipping.Overflow;
            }

            float width = Screen.width;
            float height = Screen.height;
            Color boxColor = new Color(1f, 0.78f, 0.2f, 0.9f);
            Color pointColor = new Color(0.4f, 0.9f, 1f, 0.9f);
            bool gl = EnsureLineMaterial();
            Color oldColor = GUI.color;
            Color fillColor = new Color(boxColor.r, boxColor.g, boxColor.b,
                Mathf.Clamp(_plugin.ShowTriggersOpacity.Value, 0.02f, 1f));
            Plane near = NearPlane(camera);

            if (gl)
            {
                GL.PushMatrix();
                _lineMaterial.SetPass(0);
                GL.LoadPixelMatrix();
                if (_plugin.ShowTriggersSolid.Value)
                    DrawSolidTriggerBoxes(camera, near, width, height, fillColor);
                GL.Begin(GL.LINES);
            }
            for (int i = 0; i < _triggerVisible.Count; i++)
            {
                TriggerMarker marker = _triggerVisible[i];
                if (!GameBridge.IsAlive(marker.Owner))
                    continue;
                if (marker.Collider != null && GameBridge.IsAlive(marker.Collider))
                {
                    Bounds bounds = marker.Collider.bounds;
                    Vector3 min = bounds.min;
                    Vector3 max = bounds.max;
                    for (int c = 0; c < 8; c++)
                    {
                        _triggerCorners[c] = new Vector3((c & 1) != 0 ? max.x : min.x,
                                                        (c & 2) != 0 ? max.y : min.y,
                                                        (c & 4) != 0 ? max.z : min.z);
                    }
                    if (gl)
                    {
                        GL.Color(boxColor);
                        for (int e = 0; e < BoxEdges.Length; e += 2)
                        {
                            Vector3 a;
                            Vector3 b;
                            if (!ClipSegmentToNear(_triggerCorners[BoxEdges[e]],
                                                   _triggerCorners[BoxEdges[e + 1]], near, out a, out b))
                                continue;
                            Vector3 va = ToViewport(camera, a);
                            Vector3 vb = ToViewport(camera, b);
                            if (va.z <= 0f || vb.z <= 0f)
                                continue;
                            if (SegmentOffScreen(va, vb))
                                continue;
                            GL.Vertex3(va.x * width, va.y * height, 0f);
                            GL.Vertex3(vb.x * width, vb.y * height, 0f);
                        }
                    }
                    else
                    {
                        DrawScreenRect(boxColor, width, height);
                    }
                }
                else if (gl)
                {
                    Vector3 p = ToViewport(camera, marker.Owner.transform.position);
                    if (p.z > 0f)
                    {
                        float x = p.x * width;
                        float y = p.y * height;
                        GL.Color(pointColor);
                        GL.Vertex3(x - 6f, y, 0f);
                        GL.Vertex3(x + 6f, y, 0f);
                        GL.Vertex3(x, y - 6f, 0f);
                        GL.Vertex3(x, y + 6f, 0f);
                    }
                }
            }
            if (gl)
            {
                GL.End();
                GL.PopMatrix();
            }

            // Labels after the lines: GUI calls cannot run between GL.Begin and GL.End.
            for (int i = 0; i < _triggerVisible.Count; i++)
            {
                TriggerMarker marker = _triggerVisible[i];
                if (!GameBridge.IsAlive(marker.Owner))
                    continue;
                Vector3 anchor;
                bool isBox = marker.Collider != null && GameBridge.IsAlive(marker.Collider);
                if (isBox)
                {
                    Bounds bounds = marker.Collider.bounds;
                    anchor = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
                }
                else
                {
                    anchor = marker.Owner.transform.position;
                }
                Vector3 p = ToViewport(camera, anchor);
                if (p.z <= 0f)
                    continue;
                float x = p.x * width;
                float y = (1f - p.y) * height;
                _triggerLabelStyle.normal.textColor = isBox ? boxColor : pointColor;
                GUI.Label(new Rect(x - 200f, y - 18f, 400f, 16f), marker.Label, _triggerLabelStyle);
            }
            GUI.color = oldColor;
        }

        static bool SegmentOffScreen(Vector3 a, Vector3 b)
        {
            return (a.x < 0f && b.x < 0f) || (a.x > 1f && b.x > 1f) ||
                   (a.y < 0f && b.y < 0f) || (a.y > 1f && b.y > 1f);
        }

        /// <summary>
        /// Fills each visible trigger volume with translucent triangles. Faces are clipped to
        /// the near plane instead of skipping a volume when one corner is behind the camera;
        /// outside the volume only front-facing faces are drawn, which cuts overdraw in half.
        /// </summary>
        void DrawSolidTriggerBoxes(Camera camera, Plane near, float width, float height, Color color)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            Vector3 eye = camera.transform.position;
            for (int i = 0; i < _triggerVisible.Count; i++)
            {
                TriggerMarker marker = _triggerVisible[i];
                if (marker.Collider == null || !GameBridge.IsAlive(marker.Collider) || !marker.Collider.enabled)
                    continue;
                Bounds bounds = marker.Collider.bounds;
                bool inside = bounds.Contains(eye);
                for (int f = 0; f < 6; f++)
                {
                    FillFace(bounds, f, _triggerFace);
                    Vector3 center = (_triggerFace[0] + _triggerFace[1] + _triggerFace[2] + _triggerFace[3]) * 0.25f;
                    Vector3 normal = BoxNormals[f];
                    if (!inside && Vector3.Dot(normal, eye - center) <= 0f)
                        continue;

                    int clipped = ClipPolygonToNear(_triggerFace, 4, near, _triggerClipped);
                    if (clipped < 3)
                        continue;

                    float minX = float.MaxValue;
                    float minY = float.MaxValue;
                    float maxX = float.MinValue;
                    float maxY = float.MinValue;
                    for (int k = 0; k < clipped; k++)
                    {
                        Vector3 v = ToViewport(camera, _triggerClipped[k]);
                        if (v.z <= 0f)
                        {
                            minX = float.MaxValue;
                            break;
                        }
                        minX = Mathf.Min(minX, v.x);
                        minY = Mathf.Min(minY, v.y);
                        maxX = Mathf.Max(maxX, v.x);
                        maxY = Mathf.Max(maxY, v.y);
                    }
                    if (minX == float.MaxValue || maxX < 0f || minX > 1f || maxY < 0f || minY > 1f)
                        continue;

                    for (int k = 1; k < clipped - 1; k++)
                    {
                        Vector3 a = ToViewport(camera, _triggerClipped[0]);
                        Vector3 b = ToViewport(camera, _triggerClipped[k]);
                        Vector3 c = ToViewport(camera, _triggerClipped[k + 1]);
                        if (a.z <= 0f || b.z <= 0f || c.z <= 0f)
                            continue;
                        GL.Vertex3(a.x * width, a.y * height, 0f);
                        GL.Vertex3(b.x * width, b.y * height, 0f);
                        GL.Vertex3(c.x * width, c.y * height, 0f);
                    }
                }
            }
            GL.End();
        }

        static void FillFace(Bounds bounds, int face, Vector3[] output)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            switch (face)
            {
                case 0:
                    output[0] = new Vector3(min.x, min.y, min.z);
                    output[1] = new Vector3(min.x, min.y, max.z);
                    output[2] = new Vector3(min.x, max.y, max.z);
                    output[3] = new Vector3(min.x, max.y, min.z);
                    break;
                case 1:
                    output[0] = new Vector3(max.x, min.y, min.z);
                    output[1] = new Vector3(max.x, max.y, min.z);
                    output[2] = new Vector3(max.x, max.y, max.z);
                    output[3] = new Vector3(max.x, min.y, max.z);
                    break;
                case 2:
                    output[0] = new Vector3(min.x, min.y, min.z);
                    output[1] = new Vector3(max.x, min.y, min.z);
                    output[2] = new Vector3(max.x, min.y, max.z);
                    output[3] = new Vector3(min.x, min.y, max.z);
                    break;
                case 3:
                    output[0] = new Vector3(min.x, max.y, min.z);
                    output[1] = new Vector3(min.x, max.y, max.z);
                    output[2] = new Vector3(max.x, max.y, max.z);
                    output[3] = new Vector3(max.x, max.y, min.z);
                    break;
                case 4:
                    output[0] = new Vector3(min.x, min.y, min.z);
                    output[1] = new Vector3(min.x, max.y, min.z);
                    output[2] = new Vector3(max.x, max.y, min.z);
                    output[3] = new Vector3(max.x, min.y, min.z);
                    break;
                default:
                    output[0] = new Vector3(min.x, min.y, max.z);
                    output[1] = new Vector3(max.x, min.y, max.z);
                    output[2] = new Vector3(max.x, max.y, max.z);
                    output[3] = new Vector3(min.x, max.y, max.z);
                    break;
            }
        }

        /// <summary>Fallback without the line shader: the screen rectangle around the projected box corners.</summary>
        void DrawScreenRect(Color color, float width, float height)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 v = _triggerCorners[c];
                if (v.z <= 0f)
                    return;
                float x = v.x * width;
                float y = (1f - v.y) * height;
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }
            GUI.color = color;
            Texture2D white = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(minX, minY, maxX - minX, 1f), white);
            GUI.DrawTexture(new Rect(minX, maxY, maxX - minX, 1f), white);
            GUI.DrawTexture(new Rect(minX, minY, 1f, maxY - minY), white);
            GUI.DrawTexture(new Rect(maxX, minY, 1f, maxY - minY), white);
        }
    }
}
