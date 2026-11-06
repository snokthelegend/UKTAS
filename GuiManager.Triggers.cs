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
    /// The scene is scanned on a slow timer; the distance filter runs on the HUD timer;
    /// drawing only projects the cached boxes.
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

        const int TriggerScanInterval = 300;
        const int MaxTriggerMarkers = 128;
        const int MaxTriggerScan = 4000;

        readonly List<Type> _triggerTypes = new List<Type>(64);
        bool _triggerTypesResolved;
        readonly List<TriggerMarker> _triggerAll = new List<TriggerMarker>(256);
        readonly List<TriggerMarker> _triggerVisible = new List<TriggerMarker>(MaxTriggerMarkers);
        readonly List<Collider> _triggerColliders = new List<Collider>(8);
        readonly HashSet<int> _triggerSeen = new HashSet<int>();
        readonly Vector3[] _triggerCorners = new Vector3[8];
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
                    _triggerTypes.Add(type);
            }
            for (int i = 0; i < TriggerExtraTypeNames.Length; i++)
            {
                Type type = _bridge.FindType(TriggerExtraTypeNames[i]);
                if (type == null)
                    _plugin.LogWarning("Show triggers: game type '" + TriggerExtraTypeNames[i] + "' not found; it is skipped.");
                else if (!_triggerTypes.Contains(type))
                    _triggerTypes.Add(type);
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

        void ScanTriggers()
        {
            _triggerAll.Clear();
            ResolveTriggerTypes();
            _triggerSeen.Clear();
            for (int t = 0; t < _triggerTypes.Count && _triggerAll.Count < MaxTriggerScan; t++)
            {
                UnityEngine.Object[] found;
                try
                {
                    found = UnityEngine.Object.FindObjectsOfType(_triggerTypes[t]);
                }
                catch
                {
                    continue;
                }
                for (int i = 0; i < found.Length && _triggerAll.Count < MaxTriggerScan; i++)
                {
                    Component owner = found[i] as Component;
                    if (owner == null)
                        continue;
                    string label = owner.GetType().Name + "  " + owner.gameObject.name;
                    owner.GetComponents(_triggerColliders);
                    bool any = false;
                    for (int c = 0; c < _triggerColliders.Count; c++)
                    {
                        Collider collider = _triggerColliders[c];
                        if (collider == null || !collider.isTrigger)
                            continue;
                        any = true;
                        // One outline per collider, even when several scripts share it.
                        if (!_triggerSeen.Add(collider.GetInstanceID()))
                            continue;
                        _triggerAll.Add(new TriggerMarker { Owner = owner, Collider = collider, Label = label });
                    }
                    if (!any && _triggerSeen.Add(owner.gameObject.GetInstanceID()))
                        _triggerAll.Add(new TriggerMarker { Owner = owner, Label = label });
                }
            }
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
                    distanceSq = marker.Collider.bounds.SqrDistance(eye);
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

            if (gl)
            {
                GL.PushMatrix();
                _lineMaterial.SetPass(0);
                GL.LoadPixelMatrix();
                if (_plugin.ShowTriggersSolid.Value)
                    DrawSolidTriggerBoxes(camera, width, height, fillColor);
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
                        Vector3 corner = new Vector3((c & 1) != 0 ? max.x : min.x,
                                                     (c & 2) != 0 ? max.y : min.y,
                                                     (c & 4) != 0 ? max.z : min.z);
                        _triggerCorners[c] = ToViewport(camera, corner);
                    }
                    if (gl)
                    {
                        GL.Color(boxColor);
                        for (int e = 0; e < BoxEdges.Length; e += 2)
                        {
                            Vector3 a = _triggerCorners[BoxEdges[e]];
                            Vector3 b = _triggerCorners[BoxEdges[e + 1]];
                            if (a.z <= 0f || b.z <= 0f)
                                continue;
                            GL.Vertex3(a.x * width, a.y * height, 0f);
                            GL.Vertex3(b.x * width, b.y * height, 0f);
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

        /// <summary>
        /// Fills each visible trigger volume with translucent quads. A box is skipped when any of
        /// its corners is behind the camera, so nothing is projected across the view.
        /// </summary>
        void DrawSolidTriggerBoxes(Camera camera, float width, float height, Color color)
        {
            GL.Begin(GL.QUADS);
            GL.Color(color);
            for (int i = 0; i < _triggerVisible.Count; i++)
            {
                TriggerMarker marker = _triggerVisible[i];
                if (marker.Collider == null || !GameBridge.IsAlive(marker.Collider) || !marker.Collider.enabled)
                    continue;
                Bounds bounds = marker.Collider.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                bool behind = false;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3((c & 1) != 0 ? max.x : min.x,
                                                 (c & 2) != 0 ? max.y : min.y,
                                                 (c & 4) != 0 ? max.z : min.z);
                    _triggerCorners[c] = ToViewport(camera, corner);
                    if (_triggerCorners[c].z <= 0f)
                        behind = true;
                }
                if (behind)
                    continue;
                for (int f = 0; f < BoxFaces.Length; f += 4)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 v = _triggerCorners[BoxFaces[f + k]];
                        GL.Vertex3(v.x * width, v.y * height, 0f);
                    }
                }
            }
            GL.End();
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
