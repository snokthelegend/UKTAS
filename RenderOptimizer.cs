using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltrakillTAS
{
    /// <summary>
    /// Performance Render Mode (the visual pipeline stripper).
    ///
    /// While the master toggle is on, this removes GPU-only work:
    ///  - global real-time shadows and reflection probes,
    ///  - the post-process/CRT output (the handler stays alive, so restarts keep rendering),
    ///  - decal / particle layers and the blood-oil composite pass,
    ///  - particle renderers, scene lights (fullbright), enemy skinning, pixel lights,
    ///    particle raycasts, MSAA and anisotropic filtering.
    ///
    /// What it never touches, on purpose:
    ///  - Colliders, triggers, Rigidbodies, Joints, physics layer assignments,
    ///    Physics.autoSimulation, Physics.autoSyncTransforms, Time.fixedDeltaTime and the TAS clock;
    ///  - any gameplay script's enabled state, Update/FixedUpdate, or the ParticleSystem
    ///    simulation. Particle systems keep simulating, so OnParticleCollision /
    ///    OnParticleTrigger and every AI/weapon schedule stay identical.
    ///
    /// Camera.main.cullingMask is deliberately changed for the dedicated Decal and Particles
    /// layers only. That is a rendering-only camera property; it does not change a GameObject's
    /// layer or any physics collision matrix entry.
    ///
    /// Every override is cached and restored exactly when its section (or the master toggle)
    /// is switched off, so a run simulated with the mode on and one with it off produce the
    /// same physics and the same script result.
    /// </summary>
    public sealed class RenderOptimizer
    {
        readonly Plugin _plugin;
        static RenderOptimizer _instance;

        // ULTRAKILL's actual layer names. Do not add Gib/GibLit here: those layers are used by
        // enemy limbs and gore, and culling them makes enemies disappear.
        static readonly string[] DecalLayerNames = { "Decal", "Decals" };
        static readonly string[] ParticleLayerNames = { "Particles" };

        bool _master;
        bool _shadowsOn;
        bool _postOn;
        bool _particlesOn;
        bool _allParticlesOn;
        bool _decalsOn;
        bool _pixelLightsOn;
        bool _fullbrightOn;
        bool _skinWeightsOn;
        bool _particleRaycastsOn;

        /// <summary>True while the master Performance Render Mode switch is on.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>True while the decal section is suppressing spawn and visual updates.</summary>
        public static bool DisableDecals { get; private set; }

        // ------------------------------------------------------------------
        // Original audio/visual state
        // ------------------------------------------------------------------

        sealed class ProbeState
        {
            public int Id;
            public ReflectionProbe Probe;
            public bool Enabled;
        }

        sealed class RendererState
        {
            public int Id;
            public Renderer Renderer;
            public bool ForceRenderingOff;
        }

        sealed class LightState
        {
            public int Id;
            public Light Light;
            public bool Enabled;
        }

        sealed class SkinState
        {
            public int Id;
            public SkinnedMeshRenderer Renderer;
            public SkinQuality Quality;
        }

        readonly List<ProbeState> _probes = new List<ProbeState>(16);
        readonly HashSet<int> _probeIds = new HashSet<int>();

        readonly List<RendererState> _particleRenderers = new List<RendererState>(64);
        readonly HashSet<int> _particleRendererIds = new HashSet<int>();

        readonly List<LightState> _lights = new List<LightState>(64);
        readonly HashSet<int> _lightIds = new HashSet<int>();

        readonly List<SkinState> _skinned = new List<SkinState>(64);
        readonly HashSet<int> _skinnedIds = new HashSet<int>();

        bool _shadowQualitySaved;
        ShadowQuality _savedShadowQuality;

        bool _postHandlerTypeSearched;
        Type _postHandlerType;
        FieldInfo _postVirtualCamField;
        FieldInfo _postMainTexField;
        FieldInfo _postMainCamField;
        FieldInfo _postBloodOilField;
        UnityEngine.Object _postHandler;
        Camera _postVirtualCam;
        Texture _postMainTex;
        CommandBuffer _postCopyBuffer;
        int _savedPostVirtualCamMask;
        bool _postVirtualCamMaskSaved;

        Camera _bloodOilCamera;
        CommandBuffer _bloodOilBuffer;
        float _nextBloodOilBind;

        // Camera.main culling mask, owned while a cosmetic layer section is active.
        Camera _maskCamera;
        int _removedLayerMask;
        bool _maskSaved;
        readonly int _decalLayerMask;
        readonly int _particleLayerMask;
        bool _layerMasksLogged;

        // Engine Quality Reductions. Each value is cached before its first override.
        bool _pixelLightsSaved;
        int _savedPixelLightCount;
        bool _skinWeightsSaved;
        SkinWeights _savedSkinWeights;
        bool _particleRaycastsSaved;
        int _savedParticleRaycastBudget;
        bool _antiAliasingSaved;
        int _savedAntiAliasing;
        bool _anisotropicSaved;
        AnisotropicFiltering _savedAnisotropicFiltering;

        // Fullbright light/render settings.
        bool _renderSettingsSaved;
        AmbientMode _savedAmbientMode;
        Color _savedAmbientLight;
        float _savedAmbientIntensity;
        float _savedReflectionIntensity;
        bool _savedFog;

        Harmony _harmony;
        bool _decalPatchesInstalled;
        bool _refreshRequested;
        float _nextParticleScan;
        float _nextLightScan;
        float _nextSkinScan;

        public RenderOptimizer(Plugin plugin)
        {
            _plugin = plugin;
            _instance = this;
            _decalLayerMask = ResolveLayerMask(DecalLayerNames);
            _particleLayerMask = ResolveLayerMask(ParticleLayerNames);
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Once per rendered frame from Plugin.Tick.</summary>
        public void Update()
        {
            // Video must capture the full visual pipeline: suspend the stripped mode for the
            // whole encode, then restore the configured state when the recording ends.
            bool recording = _plugin.Video != null && _plugin.Video.IsRecording;
            bool master = Value(_plugin.PerformanceRenderMode) && !recording;
            bool shadows = master && Value(_plugin.PerfDisableShadows);
            bool post = master && Value(_plugin.PerfDisablePostProcessing);
            bool particles = master && Value(_plugin.PerfDisableCosmeticParticles);
            bool allParticles = master && Value(_plugin.PerfSuppressAllParticles);
            bool decals = master && Value(_plugin.PerfDisableDecals);
            bool pixelLights = master && Value(_plugin.PerfDisablePixelLights);
            bool fullbright = master && Value(_plugin.PerfFullbright);
            bool skinWeights = master && Value(_plugin.PerfFastSkinning);
            bool particleRaycasts = master && Value(_plugin.PerfZeroParticleRaycasts);

            IsActive = master;

            if (master != _master)
            {
                _master = master;
                Log("Performance Render Mode " + (master ? "ON" : "OFF") + " (rendering only).");
            }

            if (shadows != _shadowsOn)
            {
                _shadowsOn = shadows;
                if (shadows) ApplyShadows();
                else RestoreShadows();
            }
            if (post != _postOn)
            {
                _postOn = post;
                if (post) ApplyPostProcessing();
                else RestorePostProcessing();
            }
            if (particles != _particlesOn)
            {
                _particlesOn = particles;
                if (particles) ApplyParticles();
                else RestoreParticles();
            }
            if (allParticles != _allParticlesOn)
            {
                _allParticlesOn = allParticles;
                if (allParticles) ApplyAllParticles();
                else RestoreAllParticles();
            }
            if (decals != _decalsOn)
            {
                _decalsOn = decals;
                DisableDecals = decals;
                if (decals) ApplyDecals();
                else RestoreDecals();
            }
            if (fullbright != _fullbrightOn)
            {
                _fullbrightOn = fullbright;
                if (fullbright) ApplyFullbright();
                else RestoreFullbright();
            }

            _pixelLightsOn = pixelLights;
            _skinWeightsOn = skinWeights;
            _particleRaycastsOn = particleRaycasts;
            DisableDecals = _decalsOn;

            if (_decalsOn)
            {
                InstallDecalPatches();
                BindBloodOil();
            }

            // These are all O(1) checks. Re-asserting them keeps a quality-level change from
            // silently undoing the mode; none of them scans the scene.
            if (_shadowsOn && QualitySettings.shadows != ShadowQuality.Disable)
                QualitySettings.shadows = ShadowQuality.Disable;
            if (_postOn)
                ReassertPostProcessing();
            ApplyEngineQuality();
            UpdateCameraCulling();

            if (_allParticlesOn)
                RefreshAllParticles();
            if (_fullbrightOn)
                RefreshLights();
            if (_skinWeightsOn)
                RefreshEnemySkinning();

            if (_refreshRequested)
            {
                _refreshRequested = false;
                if (_postOn)
                    ApplyPostProcessing();
                if (_decalsOn)
                {
                    InstallDecalPatches();
                    BindBloodOil();
                }
                if (_allParticlesOn)
                    ScanParticleRenderers();
                if (_fullbrightOn)
                    ScanLights();
                if (_skinWeightsOn)
                    ApplyEnemySkinning();
                UpdateCameraCulling();
            }
        }

        /// <summary>Makes the next frame re-apply the cheap camera/quality state.</summary>
        public void RequestRefresh()
        {
            _refreshRequested = true;
        }

        /// <summary>
        /// A level load destroys cached probes, cameras, materials and renderers. Re-scan once
        /// here; no interval scan is ever scheduled for shadows or post-processing.
        /// </summary>
        public void OnSceneChanged()
        {
            if (_shadowsOn)
            {
                PruneProbes();
                ApplyShadows();
            }
            if (_postOn)
            {
                RestorePostProcessing();
                ApplyPostProcessing();
            }
            if (_decalsOn)
            {
                _bloodOilCamera = null;
                _bloodOilBuffer = null;
                _nextBloodOilBind = 0f;
                InstallDecalPatches();
                BindBloodOil();
            }
            if (_allParticlesOn)
            {
                RestoreAllParticles();
                ApplyAllParticles();
            }
            if (_fullbrightOn)
            {
                RestoreFullbright();
                ApplyFullbright();
            }
            if (_skinWeightsOn)
            {
                RestoreEnemySkinning();
                ApplyEnemySkinning();
            }
            UpdateCameraCulling();
        }

        /// <summary>Restores everything this mode owns. Called when the plugin really shuts down.</summary>
        public void Shutdown()
        {
            if (_shadowsOn) RestoreShadows();
            if (_postOn) RestorePostProcessing();
            if (_decalsOn) RestoreDecals();
            if (_allParticlesOn) RestoreAllParticles();
            if (_fullbrightOn) RestoreFullbright();
            if (_skinWeightsOn) RestoreEnemySkinning();
            RestoreCameraCulling();
            RestoreEngineQuality();

            _shadowsOn = _postOn = _particlesOn = _allParticlesOn = _decalsOn = false;
            _pixelLightsOn = _fullbrightOn = _skinWeightsOn = _particleRaycastsOn = false;
            _master = false;
            IsActive = false;
            DisableDecals = false;
            _instance = null;

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception ex) { Log("Could not remove render patches: " + ex.Message); }
                _harmony = null;
                _decalPatchesInstalled = false;
            }
        }

        // ------------------------------------------------------------------
        // A. Shadows and reflection probes
        // ------------------------------------------------------------------

        void ApplyShadows()
        {
            if (!_shadowQualitySaved)
            {
                _savedShadowQuality = QualitySettings.shadows;
                _shadowQualitySaved = true;
            }
            QualitySettings.shadows = ShadowQuality.Disable;

            for (int i = 0; i < _probes.Count; i++)
            {
                ReflectionProbe probe = _probes[i].Probe;
                if (probe != null && probe.enabled)
                    probe.enabled = false;
            }

            ReflectionProbe[] probes = null;
            try { probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(); }
            catch (Exception ex) { Log("Reflection probe scan failed: " + ex.Message); }
            if (probes != null)
            {
                for (int i = 0; i < probes.Length; i++)
                {
                    ReflectionProbe probe = probes[i];
                    if (probe == null)
                        continue;
                    int id = probe.GetInstanceID();
                    if (_probeIds.Add(id))
                        _probes.Add(new ProbeState { Id = id, Probe = probe, Enabled = probe.enabled });
                    if (probe.enabled)
                        probe.enabled = false;
                }
            }
        }

        void RestoreShadows()
        {
            for (int i = 0; i < _probes.Count; i++)
            {
                ReflectionProbe probe = _probes[i].Probe;
                if (probe != null)
                    probe.enabled = _probes[i].Enabled;
            }
            _probes.Clear();
            _probeIds.Clear();

            if (_shadowQualitySaved)
            {
                QualitySettings.shadows = _savedShadowQuality;
                _shadowQualitySaved = false;
            }
            Log("Performance Render Mode: shadows and reflection probes restored.");
        }

        void PruneProbes()
        {
            for (int i = _probes.Count - 1; i >= 0; i--)
            {
                if (_probes[i].Probe != null)
                    continue;
                _probeIds.Remove(_probes[i].Id);
                _probes.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------
        // B. Post-processing
        // ------------------------------------------------------------------

        void ApplyPostProcessing()
        {
            if (!BindPostProcessing())
                return;
            if (_postCopyBuffer == null)
                RebuildPostCopy();
            if (_postCopyBuffer == null)
                return;
            Log("Performance Render Mode: post-processing/CRT bypassed by copying the final color buffer; ULTRAKILL's render handler stays enabled.");
        }

        void ReassertPostProcessing()
        {
            if (!BindPostProcessing())
                return;
            if (_postCopyBuffer == null)
            {
                RebuildPostCopy();
                return;
            }
            // The game or a resolution change can reset the virtual camera's mask; keep its
            // post-process quad hidden while the copy command is active.
            if (_postVirtualCam != null && _postVirtualCam.cullingMask != 0)
                _postVirtualCam.cullingMask = 0;
        }

        /// <summary>
        /// Finds the live handler and its virtual camera without doing a scene-wide search every
        /// frame. The camera re-renders the post quad into the screen; hiding that quad and
        /// copying mainTex directly is safer than replacing the game's post material shader.
        /// </summary>
        bool BindPostProcessing()
        {
            Type type = PostHandlerType();
            if (type == null)
                return false;

            UnityEngine.Object handler = _postHandler;
            if (handler == null)
            {
                try { handler = UnityEngine.Object.FindObjectOfType(type); }
                catch { handler = null; }
                if (handler == null)
                    return false;
            }

            if (_postVirtualCamField == null)
                _postVirtualCamField = AccessTools.Field(type, "virtualCam");
            if (_postMainTexField == null)
                _postMainTexField = AccessTools.Field(type, "mainTex");
            if (_postVirtualCamField == null || _postMainTexField == null)
                return false;

            Camera virtualCam = _postVirtualCamField.GetValue(handler) as Camera;
            Texture mainTex = _postMainTexField.GetValue(handler) as Texture;
            if (virtualCam == null || mainTex == null)
            {
                ReleasePostBypass();
                return false;
            }

            if (!ReferenceEquals(handler, _postHandler) ||
                !ReferenceEquals(virtualCam, _postVirtualCam) ||
                !ReferenceEquals(mainTex, _postMainTex))
            {
                ReleasePostBypass();
                _postHandler = handler;
                _postVirtualCam = virtualCam;
                _postMainTex = mainTex;
            }
            return true;
        }

        void RebuildPostCopy()
        {
            if (_postVirtualCam == null || _postMainTex == null)
                return;

            DestroyPostBuffer();
            if (!_postVirtualCamMaskSaved)
            {
                _savedPostVirtualCamMask = _postVirtualCam.cullingMask;
                _postVirtualCamMaskSaved = true;
            }
            _postVirtualCam.cullingMask = 0;

            CommandBuffer buffer = new CommandBuffer();
            buffer.name = "UKTAS post-process bypass";
            buffer.Blit(_postMainTex, BuiltinRenderTextureType.CameraTarget);
            _postVirtualCam.AddCommandBuffer(CameraEvent.AfterEverything, buffer);
            _postCopyBuffer = buffer;
        }

        void DestroyPostBuffer()
        {
            if (_postCopyBuffer == null)
                return;
            if (_postVirtualCam != null)
            {
                try { _postVirtualCam.RemoveCommandBuffer(CameraEvent.AfterEverything, _postCopyBuffer); }
                catch { }
            }
            try { _postCopyBuffer.Release(); }
            catch { }
            _postCopyBuffer = null;
        }

        void ReleasePostBypass()
        {
            DestroyPostBuffer();
            if (_postVirtualCam != null && _postVirtualCamMaskSaved)
            {
                try { _postVirtualCam.cullingMask = _savedPostVirtualCamMask; }
                catch { }
            }
            _postVirtualCamMaskSaved = false;
            _postVirtualCam = null;
            _postMainTex = null;
            _postHandler = null;
        }

        void RestorePostProcessing()
        {
            ReleasePostBypass();
            Log("Performance Render Mode: post-processing output restored.");
        }

        Type PostHandlerType()
        {
            if (_postHandlerTypeSearched)
                return _postHandlerType;
            _postHandlerTypeSearched = true;
            try { _postHandlerType = AccessTools.TypeByName("PostProcessV2_Handler"); }
            catch { _postHandlerType = null; }
            if (_postHandlerType == null)
                Log("Performance Render Mode: PostProcessV2_Handler was not found; post-processing is unchanged.");
            return _postHandlerType;
        }

        // ------------------------------------------------------------------
        // C. Cosmetic particles
        // ------------------------------------------------------------------

        void ApplyParticles()
        {
            // Dedicated particle layers are removed from Camera.main.cullingMask by
            // UpdateCameraCulling; particle systems keep simulating.
            Log("Performance Render Mode: cosmetic particle layers culled from the main camera.");
        }

        void RestoreParticles()
        {
            Log("Performance Render Mode: cosmetic particle layers restored.");
        }

        void ApplyAllParticles()
        {
            ScanParticleRenderers();
            Log("Performance Render Mode: all ParticleSystemRenderer output hidden (simulation still runs).");
        }

        void RefreshAllParticles()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextParticleScan)
                return;
            _nextParticleScan = now + 2f;
            ScanParticleRenderers();
        }

        void ScanParticleRenderers()
        {
            ParticleSystemRenderer[] renderers = null;
            try { renderers = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>(); }
            catch (Exception ex) { Log("Particle renderer scan failed: " + ex.Message); return; }
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                int id = renderer.GetInstanceID();
                if (_particleRendererIds.Add(id))
                {
                    _particleRenderers.Add(new RendererState
                    {
                        Id = id,
                        Renderer = renderer,
                        ForceRenderingOff = renderer.forceRenderingOff
                    });
                }
                if (!renderer.forceRenderingOff)
                    renderer.forceRenderingOff = true;
            }
            // Activation and the periodic pass must not both scan on the same frame.
            _nextParticleScan = Time.realtimeSinceStartup + 2f;
        }

        void RestoreAllParticles()
        {
            for (int i = 0; i < _particleRenderers.Count; i++)
            {
                Renderer renderer = _particleRenderers[i].Renderer;
                if (renderer != null)
                    renderer.forceRenderingOff = _particleRenderers[i].ForceRenderingOff;
            }
            _particleRenderers.Clear();
            _particleRendererIds.Clear();
            Log("Performance Render Mode: particle renderer output restored.");
        }

        // ------------------------------------------------------------------
        // D. Decals, bloodstains and bullet holes
        // ------------------------------------------------------------------

        void ApplyDecals()
        {
            InstallDecalPatches();
            BindBloodOil();
            Log("Performance Render Mode: decal suppression active (Decal layer + blood/oil pass).");
        }

        void RestoreDecals()
        {
            DisableDecals = false;
            _bloodOilCamera = null;
            _bloodOilBuffer = null;
            _nextBloodOilBind = 0f;
            Log("Performance Render Mode: decal suppression off.");
        }

        void InstallDecalPatches()
        {
            if (_decalPatchesInstalled)
                return;
            _decalPatchesInstalled = true;

            try
            {
                _harmony = new Harmony("com.tas.ultrakill.renderoptimizer");
                MethodInfo prefix = typeof(RenderOptimizer).GetMethod(
                    "SkipDecalWork", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(RenderOptimizer).GetMethod(
                    "SuppressBloodOilBuffer", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                    return;

                int patched = 0;
                patched += PatchNoArgument("Bloodstain", "Update", prefix);
                patched += PatchNoArgument("BloodsplatterManager", "LateUpdate", prefix);
                patched += PatchBloodstainSpawn(prefix);
                patched += PatchBloodOilBuffer(postfix);

                if (patched == 0)
                    Log("Decal suppression: no ULTRAKILL visual methods found; only the Decal camera layer is hidden.");
                else
                    Log("Decal suppression installed on " + patched + " visual methods.");
            }
            catch (Exception ex)
            {
                Log("Decal suppression patch failed: " + ex.Message);
            }
        }

        int PatchNoArgument(string typeName, string methodName, MethodInfo prefix)
        {
            Type type = null;
            try { type = AccessTools.TypeByName(typeName); }
            catch { type = null; }
            if (type == null)
                return 0;

            MethodInfo method = AccessTools.Method(type, methodName, Type.EmptyTypes);
            if (method == null)
                return 0;

            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            return 1;
        }

        int PatchBloodstainSpawn(MethodInfo prefix)
        {
            Type type = null;
            try { type = AccessTools.TypeByName("Bloodsplatter"); }
            catch { type = null; }
            if (type == null)
                return 0;

            // CreateBloodstain(ref RaycastHit, BloodsplatterManager) is void and purely visual.
            // The manager overload returns an index, so it is intentionally not skipped.
            List<MethodInfo> methods = AccessTools.GetDeclaredMethods(type);
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "CreateBloodstain" || method.GetParameters().Length != 2 ||
                    method.ReturnType != typeof(void))
                    continue;
                _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                return 1;
            }
            return 0;
        }

        int PatchBloodOilBuffer(MethodInfo postfix)
        {
            Type type = PostHandlerType();
            if (type == null || postfix == null)
                return 0;
            MethodInfo method = AccessTools.Method(type, "OnPreRenderCallback", new[] { typeof(Camera) });
            if (method == null)
                return 0;
            _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
            return 1;
        }

        void BindBloodOil()
        {
            if (_bloodOilCamera != null && _bloodOilBuffer != null)
                return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextBloodOilBind)
                return;
            _nextBloodOilBind = now + 1f;

            Type type = PostHandlerType();
            if (type == null)
                return;
            if (_postMainCamField == null)
                _postMainCamField = AccessTools.Field(type, "mainCam");
            if (_postBloodOilField == null)
                _postBloodOilField = AccessTools.Field(type, "bloodOilCB");
            if (_postMainCamField == null || _postBloodOilField == null)
                return;

            UnityEngine.Object handler = null;
            try { handler = UnityEngine.Object.FindObjectOfType(type); }
            catch { handler = null; }
            if (handler == null)
                return;

            _bloodOilCamera = _postMainCamField.GetValue(handler) as Camera;
            _bloodOilBuffer = _postBloodOilField.GetValue(handler) as CommandBuffer;
        }

        void RemoveBloodOilBuffer()
        {
            if (_bloodOilCamera == null || _bloodOilBuffer == null)
                BindBloodOil();
            Camera camera = _bloodOilCamera;
            CommandBuffer buffer = _bloodOilBuffer;
            if (camera == null || buffer == null)
                return;
            try { camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, buffer); }
            catch { }
        }

        /// <summary>Harmony prefix: false skips the visual method; true runs it unchanged.</summary>
        static bool SkipDecalWork()
        {
            return !(IsActive && DisableDecals);
        }

        /// <summary>Harmony postfix: removes the game's blood/oil composite from this camera pass.</summary>
        static void SuppressBloodOilBuffer()
        {
            if (!(IsActive && DisableDecals) || _instance == null)
                return;
            _instance.RemoveBloodOilBuffer();
        }

        // ------------------------------------------------------------------
        // E. Fullbright lighting
        // ------------------------------------------------------------------

        void ApplyFullbright()
        {
            if (!_renderSettingsSaved)
            {
                _savedAmbientMode = RenderSettings.ambientMode;
                _savedAmbientLight = RenderSettings.ambientLight;
                _savedAmbientIntensity = RenderSettings.ambientIntensity;
                _savedReflectionIntensity = RenderSettings.reflectionIntensity;
                _savedFog = RenderSettings.fog;
                _renderSettingsSaved = true;
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;

            ScanLights();
            Log("Performance Render Mode: fullbright lighting (scene lights disabled, ambient raised).");
        }

        void RestoreFullbright()
        {
            RestoreLights();
            if (_renderSettingsSaved)
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientLight = _savedAmbientLight;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
                RenderSettings.reflectionIntensity = _savedReflectionIntensity;
                RenderSettings.fog = _savedFog;
                _renderSettingsSaved = false;
            }
            Log("Performance Render Mode: scene lighting restored.");
        }

        void RefreshLights()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextLightScan)
                return;
            _nextLightScan = now + 2f;
            ScanLights();
        }

        void ScanLights()
        {
            Light[] lights = null;
            try { lights = UnityEngine.Object.FindObjectsOfType<Light>(); }
            catch (Exception ex) { Log("Light scan failed: " + ex.Message); return; }
            if (lights == null)
                return;

            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null)
                    continue;
                int id = light.GetInstanceID();
                if (_lightIds.Add(id))
                    _lights.Add(new LightState { Id = id, Light = light, Enabled = light.enabled });
                if (light.enabled)
                    light.enabled = false;
            }
            _nextLightScan = Time.realtimeSinceStartup + 2f;
        }

        void RestoreLights()
        {
            for (int i = 0; i < _lights.Count; i++)
            {
                Light light = _lights[i].Light;
                if (light != null)
                    light.enabled = _lights[i].Enabled;
            }
            _lights.Clear();
            _lightIds.Clear();
        }

        // ------------------------------------------------------------------
        // F. Enemy skinning
        // ------------------------------------------------------------------

        void ApplyEnemySkinning()
        {
            Type enemyType = _plugin.Bridge != null ? _plugin.Bridge.FindType("EnemyIdentifier") : null;
            if (enemyType == null)
                return;

            UnityEngine.Object[] enemies = null;
            try { enemies = UnityEngine.Object.FindObjectsOfType(enemyType, true); }
            catch (Exception ex) { Log("Enemy skinning scan failed: " + ex.Message); return; }
            if (enemies == null)
                return;

            for (int i = 0; i < enemies.Length; i++)
            {
                Component enemy = enemies[i] as Component;
                if (enemy == null)
                    continue;
                SkinnedMeshRenderer[] renderers = enemy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    SkinnedMeshRenderer renderer = renderers[r];
                    if (renderer == null)
                        continue;
                    int id = renderer.GetInstanceID();
                    if (_skinnedIds.Add(id))
                    {
                        _skinned.Add(new SkinState
                        {
                            Id = id,
                            Renderer = renderer,
                            Quality = renderer.quality
                        });
                    }
                    if (renderer.quality != SkinQuality.Bone1)
                        renderer.quality = SkinQuality.Bone1;
                }
            }
            _nextSkinScan = Time.realtimeSinceStartup + 3f;
        }

        void RefreshEnemySkinning()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextSkinScan)
                return;
            _nextSkinScan = now + 3f;
            ApplyEnemySkinning();
        }

        void RestoreEnemySkinning()
        {
            for (int i = 0; i < _skinned.Count; i++)
            {
                SkinnedMeshRenderer renderer = _skinned[i].Renderer;
                if (renderer != null)
                    renderer.quality = _skinned[i].Quality;
            }
            _skinned.Clear();
            _skinnedIds.Clear();
        }

        // ------------------------------------------------------------------
        // G. Engine quality reductions
        // ------------------------------------------------------------------

        void ApplyEngineQuality()
        {
            ApplyPixelLights(_master && (_pixelLightsOn || _fullbrightOn));
            ApplySkinWeights(_master && _skinWeightsOn);
            ApplyParticleRaycastBudget(_master && _particleRaycastsOn);
            ApplyAntiAliasing(_master);
            ApplyAnisotropicFiltering(_master);
        }

        void ApplyPixelLights(bool enabled)
        {
            if (enabled)
            {
                if (!_pixelLightsSaved)
                {
                    _savedPixelLightCount = QualitySettings.pixelLightCount;
                    _pixelLightsSaved = true;
                }
                if (QualitySettings.pixelLightCount != 0)
                    QualitySettings.pixelLightCount = 0;
            }
            else if (_pixelLightsSaved)
            {
                QualitySettings.pixelLightCount = _savedPixelLightCount;
                _pixelLightsSaved = false;
            }
        }

        void ApplySkinWeights(bool enabled)
        {
            if (enabled)
            {
                bool firstApply = !_skinWeightsSaved;
                if (firstApply)
                {
                    _savedSkinWeights = QualitySettings.skinWeights;
                    _skinWeightsSaved = true;
                }
                if (QualitySettings.skinWeights != SkinWeights.OneBone)
                    QualitySettings.skinWeights = SkinWeights.OneBone;
                // The 3 s RefreshEnemySkinning pass picks up newly spawned enemies. Only scan
                // synchronously on the transition into the mode, never every frame.
                if (firstApply)
                    ApplyEnemySkinning();
            }
            else
            {
                RestoreEnemySkinning();
                if (_skinWeightsSaved)
                {
                    QualitySettings.skinWeights = _savedSkinWeights;
                    _skinWeightsSaved = false;
                }
            }
        }

        void ApplyParticleRaycastBudget(bool enabled)
        {
            if (enabled)
            {
                if (!_particleRaycastsSaved)
                {
                    _savedParticleRaycastBudget = QualitySettings.particleRaycastBudget;
                    _particleRaycastsSaved = true;
                }
                if (QualitySettings.particleRaycastBudget != 0)
                    QualitySettings.particleRaycastBudget = 0;
            }
            else if (_particleRaycastsSaved)
            {
                QualitySettings.particleRaycastBudget = _savedParticleRaycastBudget;
                _particleRaycastsSaved = false;
            }
        }

        void ApplyAntiAliasing(bool enabled)
        {
            if (enabled)
            {
                if (!_antiAliasingSaved)
                {
                    _savedAntiAliasing = QualitySettings.antiAliasing;
                    _antiAliasingSaved = true;
                }
                if (QualitySettings.antiAliasing != 0)
                    QualitySettings.antiAliasing = 0;
            }
            else if (_antiAliasingSaved)
            {
                QualitySettings.antiAliasing = _savedAntiAliasing;
                _antiAliasingSaved = false;
            }
        }

        void ApplyAnisotropicFiltering(bool enabled)
        {
            if (enabled)
            {
                if (!_anisotropicSaved)
                {
                    _savedAnisotropicFiltering = QualitySettings.anisotropicFiltering;
                    _anisotropicSaved = true;
                }
                if (QualitySettings.anisotropicFiltering != AnisotropicFiltering.Disable)
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            }
            else if (_anisotropicSaved)
            {
                QualitySettings.anisotropicFiltering = _savedAnisotropicFiltering;
                _anisotropicSaved = false;
            }
        }

        void RestoreEngineQuality()
        {
            ApplyPixelLights(false);
            ApplySkinWeights(false);
            ApplyParticleRaycastBudget(false);
            ApplyAntiAliasing(false);
            ApplyAnisotropicFiltering(false);
        }

        // ------------------------------------------------------------------
        // H. Camera layer culling
        // ------------------------------------------------------------------

        void UpdateCameraCulling()
        {
            if (!_decalsOn && !_particlesOn)
            {
                RestoreCameraCulling();
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                RestoreCameraCulling();
                return;
            }

            if (!_maskSaved || !ReferenceEquals(camera, _maskCamera))
            {
                // A saved camera may still be alive when the game swaps Camera.main. Put the
                // bits we removed back before switching to the new camera.
                RestoreCameraCulling();
                _maskCamera = camera;
                _removedLayerMask = 0;
                _maskSaved = true;
            }

            int layers = 0;
            if (_decalsOn) layers |= _decalLayerMask;
            if (_particlesOn) layers |= _particleLayerMask;

            // NoRefresh deliberately owns a zero mask while it renders headless. Never undo
            // that from this cosmetic layer path; the next non-zero mask is handled normally.
            if (camera.cullingMask == 0)
                return;

            // Add back only the bits this optimizer removed, then strip the active cosmetic
            // layers. Any other culling-mask change made by the game is preserved.
            int restored = camera.cullingMask | _removedLayerMask;
            int desired = restored & ~layers;
            _removedLayerMask = restored & layers;
            if (camera.cullingMask != desired)
                camera.cullingMask = desired;

            if (!_layerMasksLogged && layers != 0)
            {
                _layerMasksLogged = true;
                Log("Cosmetic camera culling active (layer bits " + layers + ").");
            }
        }

        void RestoreCameraCulling()
        {
            if (!_maskSaved)
                return;
            if (_maskCamera != null && _maskCamera.cullingMask != 0 && _removedLayerMask != 0)
                _maskCamera.cullingMask |= _removedLayerMask;
            _maskCamera = null;
            _removedLayerMask = 0;
            _maskSaved = false;
        }

        static int ResolveLayerMask(string[] names)
        {
            int mask = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int layer = -1;
                try { layer = LayerMask.NameToLayer(names[i]); }
                catch { layer = -1; }
                if (layer >= 0 && layer < 32)
                    mask |= 1 << layer;
            }
            return mask;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static bool Value(BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            return entry != null && entry.Value;
        }

        void Log(string message)
        {
            if (_plugin != null)
                _plugin.LogInfo(message);
        }
    }
}
