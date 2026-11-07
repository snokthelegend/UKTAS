using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace UltrakillTAS
{
    /// <summary>
    /// Frame driver for the TAS tool, deliberately decoupled from the component
    /// BepInEx owns.
    ///
    /// BepInEx attaches a plugin to its own manager object. When that object is
    /// destroyed just after the plugin's Awake, Update and OnGUI never run again:
    /// the log shows the plugin loading normally and then nothing at all, the
    /// window cannot appear (even with "Show GUI on start"), and Insert looks
    /// dead because no code is reading it. That is exactly the failure this type
    /// exists to survive.
    ///
    /// This component lives on a GameObject the plugin creates itself, and a
    /// static heartbeat on the Input System's update event - static, so it
    /// cannot be destroyed along with a GameObject - recreates it whenever it
    /// goes away. Every tick is forwarded to the plugin state object, so the
    /// tool keeps running even if the BepInEx host component is gone.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public sealed class TasRuntime : MonoBehaviour
    {
        const string HostName = "ULTRAKILL_TAS_Runtime";

        /// <summary>Frames without an Update after which the host is assumed unticked.</summary>
        const int NoTickThreshold = 120;

        public static TasRuntime Instance { get; private set; }

        static Plugin _state;
        static bool _heartbeatRegistered;
        static bool _sceneResident;
        static bool _reportedNoTick;
        static int _lastTickFrame = -1;

        // ------------------------------------------------------------------
        // Installation
        // ------------------------------------------------------------------

        public static void Install(Plugin plugin)
        {
            _state = plugin;
            Create(false, "startup");

            if (_heartbeatRegistered)
                return;
            _heartbeatRegistered = true;
            try
            {
                InputSystem.onBeforeUpdate += Heartbeat;
                Log("Frame driver installed on " + HostName +
                    " with an Input System heartbeat.", false);
            }
            catch (Exception ex)
            {
                Log("Could not register the frame heartbeat: " + ex.Message, true);
            }
        }

        static void Create(bool sceneResident, string reason)
        {
            GameObject host = new GameObject(HostName);
            if (!sceneResident)
                UnityEngine.Object.DontDestroyOnLoad(host);

            Instance = host.AddComponent<TasRuntime>();
            _lastTickFrame = SimulationFreeze.FrameCount;
            _reportedNoTick = false;

            if (reason == "startup")
                return;
            Log("Frame driver recreated (" + reason + ") at frame " + Time.frameCount +
                " in scene '" + SceneManager.GetActiveScene().name + "'.", true);
        }

        // ------------------------------------------------------------------
        // Heartbeat: static, so it outlives every GameObject it manages
        // ------------------------------------------------------------------

        static void Heartbeat()
        {
            Plugin plugin = _state;
            if (ReferenceEquals(plugin, null) || Plugin.Quitting)
                return;

            plugin.EnsureFrameEnd();
            plugin.HookSceneEvents();

            if (ReferenceEquals(Instance, null))
            {
                Create(_sceneResident, "destroyed");
                return;
            }

            int idle = SimulationFreeze.FrameCount - _lastTickFrame;
            if (idle <= NoTickThreshold)
                return;

            // The object exists but Unity is not ticking it. Moving it into the
            // active scene gets Update/OnGUI running again; ticking directly in
            // the meantime keeps hotkeys and the script engine alive.
            if (!_reportedNoTick)
            {
                _reportedNoTick = true;
                _sceneResident = true;
                Scene active = SceneManager.GetActiveScene();
                GameObject host = Instance.gameObject;
                if (active.IsValid() && active.isLoaded && host.scene != active)
                    SceneManager.MoveGameObjectToScene(host, active);
                Log("Frame driver was not ticked for " + idle +
                    " frames; moved it into scene '" + active.name + "' and is driving ticks from the heartbeat.", true);
            }

            plugin.Tick();
        }

        // ------------------------------------------------------------------
        // Forwarding
        // ------------------------------------------------------------------

        void Update()
        {
            RunUpdate();
        }

        /// <summary>
        /// The Update tick. Also called directly by SimulationFreeze on a fully frozen
        /// frame, where no MonoBehaviour.Update runs.
        /// </summary>
        internal static void RunUpdate()
        {
            _lastTickFrame = SimulationFreeze.FrameCount;
            Plugin plugin = _state;
            if (ReferenceEquals(plugin, null))
                return;
            FastForwardProfiler.FrameStart(plugin, _lastTickFrame);
            long start = FastForwardProfiler.Now();
            plugin.Tick();
            FastForwardProfiler.AddUpdate(FastForwardProfiler.Ms(start));
        }

        void OnGUI()
        {
            Plugin plugin = _state;
            if (ReferenceEquals(plugin, null))
                return;
            plugin.DrawGui();
        }

        void OnDestroy()
        {
            if (ReferenceEquals(Instance, this))
                Instance = null;

            if (Plugin.Quitting || !Application.isPlaying)
                return;
            Log("Frame driver destroyed at frame " + Time.frameCount +
                " in scene '" + SceneManager.GetActiveScene().name + "'; the heartbeat will recreate it.", true);
        }

        // ------------------------------------------------------------------
        // Logging that still works once the host component is gone
        // ------------------------------------------------------------------

        static void Log(string message, bool warning)
        {
            Plugin plugin = _state;
            if (ReferenceEquals(plugin, null))
                return;
            try
            {
                if (warning)
                    plugin.LogWarning(message);
                else
                    plugin.LogInfo(message);
            }
            catch
            {
                // Logging must never be the reason the tool stops ticking.
            }
        }
    }
}
