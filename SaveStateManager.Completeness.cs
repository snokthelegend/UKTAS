using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// Cross-cutting state that no single gameplay script owns.
    ///
    /// This module deliberately covers the pieces that used to fall between modules: the
    /// renderer trails left by dashes and projectiles, audio filter state, Unity's global
    /// physics setup and layer-collision matrix, the game's InputSource action states and
    /// the procedural room progression list. It is additive: older states simply have no
    /// entry and every restore path is a no-op.
    /// </summary>
    public sealed partial class SaveStateManager
    {
        const int MaxVisualTrails = 4096;
        const int MaxVisualLines = 4096;
        const int MaxAudioFilters = 2048;
        const int MaxInputFields = 4096;

        sealed class TrailVisualState
        {
            public TrailRenderer Trail;
            public bool Enabled;
            public bool Emitting;
            public bool Autodestruct;
            public float Time;
            public float MinVertexDistance;
            public float WidthMultiplier;
            public AnimationCurve WidthCurve;
            public Gradient ColorGradient;
            public LineTextureMode TextureMode;
            public LineAlignment Alignment;
            public int NumCapVertices;
            public int NumCornerVertices;
            public Vector3[] Positions;
        }

        sealed class LineVisualState
        {
            public LineRenderer Line;
            public bool Enabled;
            public bool Loop;
            public bool UseWorldSpace;
            public float WidthMultiplier;
            public AnimationCurve WidthCurve;
            public Gradient ColorGradient;
            public LineTextureMode TextureMode;
            public LineAlignment Alignment;
            public int NumCapVertices;
            public int NumCornerVertices;
            public Vector3[] Positions;
        }

        sealed class AudioFilterState
        {
            public Component Filter;
            public List<FieldState> Fields;
            public bool Enabled;
            public bool HasEnabled;
        }

        sealed class InputModuleState
        {
            public object Source;
            public List<FieldState> Fields;
            public TasButtons Held;
            public bool HasHeld;
        }

        sealed class RoomProgressState
        {
            public Component Manager;
            public string ManagerType;
            public string FieldName;
            public List<int> Visited;
        }

        sealed class CompletenessState
        {
            public List<TrailVisualState> Trails = new List<TrailVisualState>();
            public List<LineVisualState> Lines = new List<LineVisualState>();
            public List<AudioFilterState> Filters = new List<AudioFilterState>();
            public InputModuleState Input = new InputModuleState();
            public RoomProgressState Rooms;
            public bool HasPhysicsSetup;
            public bool[] IgnoreLayers;
            public Vector3 Gravity;
            public int SolverIterations;
            public int SolverVelocityIterations;
            public float BounceThreshold;
            public float SleepThreshold;
            public float DefaultContactOffset;
            public bool QueriesHitTriggers;
            public SimulationMode SimulationMode;
            public bool AutoSyncTransforms;
        }

        static readonly string[] AudioFilterTypeNames =
        {
            "AudioLowPassFilter", "AudioHighPassFilter", "AudioReverbFilter",
            "AudioEchoFilter", "AudioDistortionFilter", "AudioChorusFilter"
        };

        partial void CaptureCompleteness(Savestate state)
        {
            CompletenessState complete = new CompletenessState();
            CaptureVisuals(complete);
            CaptureAudioFilters(complete);
            CaptureInput(complete);
            CaptureRoomProgress(complete);
            CaptureGlobalPhysics(complete);
            SetExt(state, "Completeness", complete);
        }

        partial void RestoreCompleteness(Savestate state, ref int restored, ref int missing)
        {
            CompletenessState complete = GetExt<CompletenessState>(state, "Completeness");
            if (complete == null)
                return;

            RestoreInput(complete);
            RestoreGlobalPhysics(complete);
            RestoreRoomProgress(complete, ref restored);
            RestoreAudioFilters(complete, ref restored, ref missing);
            RestoreVisuals(complete, ref restored, ref missing);
        }

        // ------------------------------------------------------------------
        // Visual trails and lines
        // ------------------------------------------------------------------

        void CaptureVisuals(CompletenessState complete)
        {
            UnityEngine.Object[] trails;
            try { trails = Resources.FindObjectsOfTypeAll(typeof(TrailRenderer)); }
            catch { trails = new UnityEngine.Object[0]; }
            for (int i = 0; i < trails.Length && complete.Trails.Count < MaxVisualTrails; i++)
            {
                TrailRenderer trail = trails[i] as TrailRenderer;
                if (trail == null || !trail.gameObject.scene.IsValid() ||
                    IsTemplateClone(trail.gameObject) || IsToolObject(trail.gameObject))
                    continue;
                try
                {
                    int count = trail.positionCount;
                    Vector3[] positions = new Vector3[count];
                    for (int p = 0; p < count; p++)
                        positions[p] = trail.GetPosition(p);
                    complete.Trails.Add(new TrailVisualState
                    {
                        Trail = trail,
                        Enabled = trail.enabled,
                        Emitting = trail.emitting,
                        Autodestruct = trail.autodestruct,
                        Time = trail.time,
                        MinVertexDistance = trail.minVertexDistance,
                        WidthMultiplier = trail.widthMultiplier,
                        WidthCurve = trail.widthCurve,
                        ColorGradient = trail.colorGradient,
                        TextureMode = trail.textureMode,
                        Alignment = trail.alignment,
                        NumCapVertices = trail.numCapVertices,
                        NumCornerVertices = trail.numCornerVertices,
                        Positions = positions
                    });
                }
                catch
                {
                }
            }

            UnityEngine.Object[] lines;
            try { lines = Resources.FindObjectsOfTypeAll(typeof(LineRenderer)); }
            catch { lines = new UnityEngine.Object[0]; }
            for (int i = 0; i < lines.Length && complete.Lines.Count < MaxVisualLines; i++)
            {
                LineRenderer line = lines[i] as LineRenderer;
                if (line == null || !line.gameObject.scene.IsValid() ||
                    IsTemplateClone(line.gameObject) || IsToolObject(line.gameObject))
                    continue;
                try
                {
                    int count = line.positionCount;
                    Vector3[] positions = new Vector3[count];
                    for (int p = 0; p < count; p++)
                        positions[p] = line.GetPosition(p);
                    complete.Lines.Add(new LineVisualState
                    {
                        Line = line,
                        Enabled = line.enabled,
                        Loop = line.loop,
                        UseWorldSpace = line.useWorldSpace,
                        WidthMultiplier = line.widthMultiplier,
                        WidthCurve = line.widthCurve,
                        ColorGradient = line.colorGradient,
                        TextureMode = line.textureMode,
                        Alignment = line.alignment,
                        NumCapVertices = line.numCapVertices,
                        NumCornerVertices = line.numCornerVertices,
                        Positions = positions
                    });
                }
                catch
                {
                }
            }
        }

        void RestoreVisuals(CompletenessState complete, ref int restored, ref int missing)
        {
            for (int i = 0; i < complete.Trails.Count; i++)
            {
                TrailVisualState saved = complete.Trails[i];
                if (saved == null)
                    continue;
                TrailRenderer trail = saved.Trail;
                if (trail == null || !GameBridge.IsAlive(trail))
                {
                    missing++;
                    continue;
                }
                try
                {
                    trail.emitting = false;
                    trail.Clear();
                    if (saved.Positions != null)
                    {
                        for (int p = 0; p < saved.Positions.Length; p++)
                            trail.AddPosition(saved.Positions[p]);
                    }
                    trail.time = saved.Time;
                    trail.minVertexDistance = saved.MinVertexDistance;
                    trail.widthMultiplier = saved.WidthMultiplier;
                    trail.widthCurve = saved.WidthCurve;
                    trail.colorGradient = saved.ColorGradient;
                    trail.textureMode = saved.TextureMode;
                    trail.alignment = saved.Alignment;
                    trail.numCapVertices = saved.NumCapVertices;
                    trail.numCornerVertices = saved.NumCornerVertices;
                    trail.autodestruct = saved.Autodestruct;
                    trail.emitting = saved.Emitting;
                    trail.enabled = saved.Enabled;
                    restored++;
                }
                catch
                {
                }
            }

            for (int i = 0; i < complete.Lines.Count; i++)
            {
                LineVisualState saved = complete.Lines[i];
                if (saved == null)
                    continue;
                LineRenderer line = saved.Line;
                if (line == null || !GameBridge.IsAlive(line))
                {
                    missing++;
                    continue;
                }
                try
                {
                    line.positionCount = saved.Positions != null ? saved.Positions.Length : 0;
                    if (saved.Positions != null)
                        line.SetPositions(saved.Positions);
                    line.loop = saved.Loop;
                    line.useWorldSpace = saved.UseWorldSpace;
                    line.widthMultiplier = saved.WidthMultiplier;
                    line.widthCurve = saved.WidthCurve;
                    line.colorGradient = saved.ColorGradient;
                    line.textureMode = saved.TextureMode;
                    line.alignment = saved.Alignment;
                    line.numCapVertices = saved.NumCapVertices;
                    line.numCornerVertices = saved.NumCornerVertices;
                    line.enabled = saved.Enabled;
                    restored++;
                }
                catch
                {
                }
            }
        }

        static bool IsToolObject(GameObject go)
        {
            return go != null && go.name.StartsWith("ULTRAKILL_TAS", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Audio filters
        // ------------------------------------------------------------------

        void CaptureAudioFilters(CompletenessState complete)
        {
            for (int t = 0; t < AudioFilterTypeNames.Length; t++)
            {
                Type type = _bridge.FindType(AudioFilterTypeNames[t]);
                if (type == null)
                    continue;
                UnityEngine.Object[] found;
                try { found = Resources.FindObjectsOfTypeAll(type); }
                catch { continue; }
                for (int i = 0; i < found.Length && complete.Filters.Count < MaxAudioFilters; i++)
                {
                    Component filter = found[i] as Component;
                    if (filter == null || !filter.gameObject.scene.IsValid() ||
                        IsTemplateClone(filter.gameObject) || IsToolObject(filter.gameObject))
                        continue;
                    try
                    {
                        Behaviour behaviour = filter as Behaviour;
                        complete.Filters.Add(new AudioFilterState
                        {
                            Filter = filter,
                            Fields = CaptureFields(filter, 64),
                            HasEnabled = behaviour != null,
                            Enabled = behaviour == null || behaviour.enabled
                        });
                    }
                    catch
                    {
                    }
                }
            }
        }

        void RestoreAudioFilters(CompletenessState complete, ref int restored, ref int missing)
        {
            for (int i = 0; i < complete.Filters.Count; i++)
            {
                AudioFilterState saved = complete.Filters[i];
                if (saved == null)
                    continue;
                Component filter = Resolve(saved.Filter);
                if (filter == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    ApplyFields(filter, saved.Fields);
                    Behaviour behaviour = filter as Behaviour;
                    if (behaviour != null && saved.HasEnabled)
                        behaviour.enabled = saved.Enabled;
                    restored++;
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // InputSource
        // ------------------------------------------------------------------

        void CaptureInput(CompletenessState complete)
        {
            try
            {
                complete.Input.Source = _bridge.InputSource;
                complete.Input.Fields = complete.Input.Source != null
                    ? CaptureFields(complete.Input.Source, MaxInputFields)
                    : null;
                InputPatcher injector = _plugin.Injector;
                if (injector != null)
                {
                    complete.Input.HasHeld = true;
                    complete.Input.Held = injector.HeldButtons;
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Input state capture skipped: " + ex.Message);
            }
        }

        void RestoreInput(CompletenessState complete)
        {
            if (complete.Input.Source == null || complete.Input.Fields == null)
                return;
            try
            {
                // The InputSource is the game's authoritative action state. Restoring its
                // fields brings back held/analog/edge data; the later InputPatcher pass then
                // reinstates the TAS mask without generating fresh press edges.
                object live = _bridge.InputSource;
                if (live == null)
                    return;
                ApplyFields(live, complete.Input.Fields);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Input state restore skipped: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Room progression
        // ------------------------------------------------------------------

        void CaptureRoomProgress(CompletenessState complete)
        {
            Type roomType = _bridge.FindType("RoomManager");
            if (roomType == null)
                return;
            UnityEngine.Object manager;
            try { manager = UnityEngine.Object.FindObjectOfType(roomType); }
            catch { manager = null; }
            if (manager == null)
                return;
            FieldInfo field = NamedField(roomType, "visitedRooms");
            if (field == null)
                return;
            List<int> values = CaptureIntCollection(field.GetValue(manager));
            if (values == null)
                return;
            complete.Rooms = new RoomProgressState
            {
                Manager = manager as Component,
                ManagerType = roomType.Name,
                FieldName = field.Name,
                Visited = values
            };
        }

        static List<int> CaptureIntCollection(object value)
        {
            List<int> result = new List<int>();
            if (value is int[] array)
            {
                result.AddRange(array);
                return result;
            }
            if (value is List<int> list)
            {
                result.AddRange(list);
                return result;
            }
            if (value is HashSet<int> set)
            {
                result.AddRange(set);
                result.Sort();
                return result;
            }
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null)
                return null;
            foreach (object item in enumerable)
            {
                if (item is int)
                    result.Add((int)item);
            }
            return result;
        }

        void RestoreRoomProgress(CompletenessState complete, ref int restored)
        {
            RoomProgressState saved = complete.Rooms;
            if (saved == null || saved.Visited == null)
                return;
            Component manager = Resolve(saved.Manager);
            if (manager == null && !string.IsNullOrEmpty(saved.ManagerType))
            {
                Type type = _bridge.FindType(saved.ManagerType);
                if (type != null)
                {
                    try { manager = UnityEngine.Object.FindObjectOfType(type) as Component; }
                    catch { manager = null; }
                }
            }
            if (manager == null || string.IsNullOrEmpty(saved.FieldName))
                return;
            try
            {
                FieldInfo field = NamedField(manager.GetType(), saved.FieldName);
                if (field == null)
                    return;
                object live = field.GetValue(manager);
                if (live is List<int> list)
                {
                    list.Clear();
                    list.AddRange(saved.Visited);
                }
                else if (live is HashSet<int> set)
                {
                    set.Clear();
                    for (int i = 0; i < saved.Visited.Count; i++)
                        set.Add(saved.Visited[i]);
                }
                else if (live is int[] array)
                {
                    if (array.Length == saved.Visited.Count)
                    {
                        for (int i = 0; i < array.Length; i++)
                            array[i] = saved.Visited[i];
                    }
                    else if (!field.IsInitOnly)
                    {
                        field.SetValue(manager, saved.Visited.ToArray());
                    }
                }
                else if (field.FieldType.IsAssignableFrom(typeof(List<int>)) && !field.IsInitOnly)
                {
                    field.SetValue(manager, new List<int>(saved.Visited));
                }
                else
                {
                    return;
                }
                restored++;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Room progression restore skipped: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Global physics
        // ------------------------------------------------------------------

        void CaptureGlobalPhysics(CompletenessState complete)
        {
            complete.HasPhysicsSetup = true;
            complete.Gravity = Physics.gravity;
            complete.SolverIterations = Physics.defaultSolverIterations;
            complete.SolverVelocityIterations = Physics.defaultSolverVelocityIterations;
            complete.BounceThreshold = Physics.bounceThreshold;
            complete.SleepThreshold = Physics.sleepThreshold;
            complete.DefaultContactOffset = Physics.defaultContactOffset;
            complete.QueriesHitTriggers = Physics.queriesHitTriggers;
            complete.SimulationMode = Physics.simulationMode;
            complete.AutoSyncTransforms = Physics.autoSyncTransforms;
            complete.IgnoreLayers = new bool[32 * 32];
            for (int a = 0; a < 32; a++)
            {
                for (int b = 0; b < 32; b++)
                    complete.IgnoreLayers[a * 32 + b] = Physics.GetIgnoreLayerCollision(a, b);
            }
        }

        void RestoreGlobalPhysics(CompletenessState complete)
        {
            if (!complete.HasPhysicsSetup)
                return;
            try
            {
                Physics.gravity = complete.Gravity;
                Physics.defaultSolverIterations = complete.SolverIterations;
                Physics.defaultSolverVelocityIterations = complete.SolverVelocityIterations;
                Physics.bounceThreshold = complete.BounceThreshold;
                Physics.sleepThreshold = complete.SleepThreshold;
                Physics.defaultContactOffset = complete.DefaultContactOffset;
                Physics.queriesHitTriggers = complete.QueriesHitTriggers;
                Physics.simulationMode = complete.SimulationMode;
                Physics.autoSyncTransforms = complete.AutoSyncTransforms;
                if (complete.IgnoreLayers != null && complete.IgnoreLayers.Length == 32 * 32)
                {
                    for (int a = 0; a < 32; a++)
                    {
                        for (int b = 0; b < 32; b++)
                            Physics.IgnoreLayerCollision(a, b, complete.IgnoreLayers[a * 32 + b]);
                    }
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Global physics restore skipped: " + ex.Message);
            }
        }
    }
}
