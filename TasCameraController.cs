using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltrakillTAS
{
    /// <summary>
    /// Which camera the author is looking through while a TAS plays.
    /// The simulation never reads these: V1 keeps running the scripted pitch, yaw,
    /// movement and attacks in every mode.
    /// </summary>
    public enum TasCameraMode
    {
        FirstPerson = 0,
        ThirdPerson = 1,
        Freecam = 2
    }

    /// <summary>
    /// Detached viewing for TAS playback.
    ///
    /// Third person is a render-only override: Camera.onPreCull moves the main camera
    /// behind V1 right before it is culled, and Camera.onPostRender puts the game's own
    /// camera pose back. The game's transform is therefore the one FixedUpdate and
    /// shooting read on the next simulation step, so the scripted inputs are unaffected.
    ///
    /// Freecam reuses the existing InputPatcher fly camera (WASD, Space/Q, Shift/Ctrl)
    /// through Plugin.SetFreecam, and adds a cursor toggle so the trajectory path can be
    /// hovered and clicked.
    /// </summary>
    public sealed class TasCameraController
    {
        /// <summary>Default offsets, also used when a config entry is missing.</summary>
        public const float DefaultThirdDistance = 4f;
        public const float DefaultThirdHeight = 0.5f;

        readonly Plugin _plugin;
        TasCameraMode _mode = TasCameraMode.FirstPerson;
        bool _cursorFree;

        bool _savedPose;
        Camera _savedCamera;
        Vector3 _savedPosition;
        Quaternion _savedRotation;

        public TasCameraController(Plugin plugin)
        {
            _plugin = plugin;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        public void Dispose()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            RestoreViewmodelsAfterWorld();
            EndHudDepthIsolation();
            if (_hudDepthCopy != null)
                UnityEngine.Object.Destroy(_hudDepthCopy);
            _hudDepthCopy = null;
            if (_hudDepthClear != null)
                _hudDepthClear.Release();
            if (_hudDepthRestore != null)
                _hudDepthRestore.Release();
            _hudDepthClear = null;
            _hudDepthRestore = null;
            DestroyV1Clone();
            ReleaseCursor();
        }

        public TasCameraMode Mode { get { return _mode; } }

        /// <summary>True while freecam has the cursor released for trajectory hover and click.</summary>
        public bool CursorFree { get { return _cursorFree && _mode == TasCameraMode.Freecam; } }

        public string ModeName
        {
            get
            {
                switch (_mode)
                {
                    case TasCameraMode.ThirdPerson: return "third person";
                    case TasCameraMode.Freecam: return "freecam";
                    default: return "first person";
                }
            }
        }

        public void SetMode(TasCameraMode mode)
        {
            if (mode == _mode)
                return;

            if (_mode == TasCameraMode.Freecam && mode != TasCameraMode.Freecam)
            {
                _plugin.SetFreecam(false);
                ReleaseCursor();
            }

            _mode = mode;
            if (mode == TasCameraMode.Freecam)
            {
                // Every freecam session starts with the cursor captured for fly look.
                _cursorFree = false;
                if (!_plugin.FreecamActive)
                    _plugin.SetFreecam(true);
            }

            _plugin.Toast("Camera: " + ModeName + ".", false);
        }

        public void CycleMode()
        {
            SetMode((TasCameraMode)(((int)_mode + 1) % 3));
        }

        /// <summary>Releases or recaptures the cursor. Only meaningful in freecam.</summary>
        public void ToggleCursor()
        {
            if (!_plugin.FreecamActive)
            {
                _plugin.Toast("The cursor is only released in freecam.", false);
                return;
            }

            // Plugin.UpdateCursorState applies the lock every frame; this only records the choice.
            _mode = TasCameraMode.Freecam;
            _cursorFree = !_cursorFree;
            _plugin.Toast(_cursorFree
                ? "Cursor free: hover the path to inspect ticks, click to scrub."
                : "Cursor captured for fly look.", false);
        }

        /// <summary>Back to first person, default freecam speed, cursor captured.</summary>
        public void ResetCamera()
        {
            _cursorFree = false;
            if (_mode == TasCameraMode.Freecam)
                _plugin.SetFreecam(false);
            _mode = TasCameraMode.FirstPerson;
            ReleaseCursor();
            _plugin.FreecamSpeed.Value = 12f;
            _plugin.Toast("Camera reset to first person.", false);
        }

        public void AdjustFreecamSpeed(float factor)
        {
            float speed = Mathf.Clamp(_plugin.FreecamSpeed.Value * factor, 1f, 100f);
            _plugin.FreecamSpeed.Value = Mathf.Round(speed * 10f) / 10f;
            _plugin.Toast("Freecam speed " + _plugin.FreecamSpeed.Value.ToString("0.0") + " u/s.", false);
        }

        // Visual-only copy of V1's real playermodel (PlatformerMovement.playerModel). A normal level keeps
        // that model under the inactive platformer controller, never under the Player, so it is cloned
        // with its original materials and posed on the Player each frame.
        GameObject _v1Clone;      // top object of the copy, destroyed when freecam ends
        Transform _v1Root;        // transform posed on the Player (the clone, or the animator wrapper)
        Vector3 _v1LocalPosition;
        Quaternion _v1LocalRotation;
        float _v1FootOffset;
        float _v1NextSearch;
        bool _v1Warned;

        // V1's platformer animation. PlatformerMovement drives its Animator ('anim') with the bools
        // Running, InAir, Sliding, Spinning and direct Play calls of the states Landing, SlamStart,
        // SlamEnd, Dash, Jump and Spin. The copy keeps that Animator (scripts are stripped, Animator is
        // not a MonoBehaviour) and is driven the same way each tick, without running any game code.
        Animator _v1Anim;
        Animator _v1SourceAnim;
        AnimatorControllerParameter[] _v1SourceParams;
        readonly HashSet<int> _v1BoolParams = new HashSet<int>();
        bool _v1ParamsRead;
        bool _v1AnimWarned;
        bool _v1AnimPrimed;
        bool _v1WasGrounded;
        bool _v1WasHeavy;
        bool _v1WasDashing;
        bool _v1WasJumping;

        static readonly string[] AnimMember = { "anim" };
        static readonly string[] GroundMember = { "gc" };
        static readonly string[] OnGroundMember = { "onGround" };
        static readonly string[] HeavyFallMember = { "heavyFall" };
        static readonly string[] SlidingMember = { "sliding" };
        static readonly string[] BoostMember = { "boost" };
        static readonly string[] JumpingMember = { "jumping" };
        static readonly string[] WalkingMember = { "walking" };
        static readonly string[] BodyMember = { "rb" };
        static readonly int RunningParam = Animator.StringToHash("Running");
        static readonly int InAirParam = Animator.StringToHash("InAir");
        static readonly int SlidingParam = Animator.StringToHash("Sliding");
        static readonly int SpinningParam = Animator.StringToHash("Spinning");

        // Fallback only: skinned meshes under the Player that the game keeps disabled.
        readonly List<Renderer> _v1BodyEnabled = new List<Renderer>();

        /// <summary>
        /// Shows V1's body while freecam is on and the option is set. The clone and any renderer this
        /// controller enabled are removed again when freecam ends, so the game's own visibility state
        /// is preserved.
        /// </summary>
        void SyncV1Body()
        {
            // Freecam now survives a level load: the renderers listed here died with the old level.
            if (_v1BodyEnabled.Count > 0 && _v1BodyEnabled[0] == null)
                _v1BodyEnabled.Clear();

            bool want = _plugin.FreecamActive && _plugin.FreecamShowV1Body.Value;
            if (want)
            {
                Transform player = _plugin.Bridge != null ? _plugin.Bridge.PlayerTransform : null;
                if (player == null)
                    return;
                if (_v1Clone == null && _v1BodyEnabled.Count == 0 && TimeController.RealTime >= _v1NextSearch)
                {
                    _v1NextSearch = TimeController.RealTime + 1f;
                    if (!CreateV1Clone() && !EnableV1SkinnedMeshes(player) && !_v1Warned)
                    {
                        _v1Warned = true;
                        _plugin.LogWarning("Show V1 playermodel: no V1 model found in this level.");
                    }
                }
                if (_v1Clone != null && _v1Root != null)
                {
                    PoseV1Clone(player);
                    AnimateV1Clone();
                }
            }
            else
            {
                DestroyV1Clone();
                for (int i = 0; i < _v1BodyEnabled.Count; i++)
                    if (_v1BodyEnabled[i] != null)
                        _v1BodyEnabled[i].enabled = false;
                _v1BodyEnabled.Clear();
            }
        }

        bool CreateV1Clone()
        {
            GameBridge bridge = _plugin.Bridge;
            Type type = bridge != null ? bridge.FindType("PlatformerMovement") : null;
            if (type == null)
                return false;
            UnityEngine.Object[] found;
            try { found = UnityEngine.Object.FindObjectsOfType(type, true); }
            catch (Exception) { return false; }

            for (int i = 0; i < found.Length; i++)
            {
                Component mover = found[i] as Component;
                if (mover == null || !mover.gameObject.scene.IsValid())
                    continue;
                object model = bridge.GetMember(mover, new[] { "playerModel" });
                GameObject source = model as GameObject;
                if (source == null)
                {
                    Component part = model as Component;
                    source = part != null ? part.gameObject : null;
                }
                if (source == null)
                    continue;

                // Built under an inactive holder so no game script runs before it is stripped.
                GameObject holder = new GameObject("UKTAS V1 holder");
                holder.SetActive(false);
                GameObject clone = UnityEngine.Object.Instantiate(source, holder.transform, false);
                clone.name = "UKTAS V1 playermodel";
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (MonoBehaviour script in clone.GetComponentsInChildren<MonoBehaviour>(true))
                        StripComponent(script);
                    foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
                        StripComponent(body);
                    foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
                        StripComponent(collider);
                }
                // The model's layer can be one the game camera does not draw.
                foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = 0;
                foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = true;

                // The platformer's Animator, matched to its counterpart in the copy.
                Animator sourceAnim = bridge.GetMember(mover, AnimMember) as Animator;
                GameObject top = clone;
                Transform reference = source.transform;
                Animator cloneAnim = null;
                if (sourceAnim != null)
                {
                    Transform animTransform = sourceAnim.transform;
                    if (animTransform == source.transform || animTransform.IsChildOf(source.transform))
                    {
                        string path = RelativePath(source.transform, animTransform);
                        Transform match = path.Length == 0 ? clone.transform : clone.transform.Find(path);
                        cloneAnim = match != null ? match.GetComponent<Animator>() : null;
                    }
                    else if (source.transform.IsChildOf(animTransform))
                    {
                        // The Animator sits above the model: rebuild the path between them, with the
                        // original names, so the controller's bone bindings resolve on the copy.
                        GameObject wrapper = new GameObject("UKTAS V1 animator");
                        wrapper.transform.SetParent(holder.transform, false);
                        Transform parent = wrapper.transform;
                        List<Transform> chain = new List<Transform>();
                        for (Transform link = source.transform.parent; link != null && link != animTransform; link = link.parent)
                            chain.Add(link);
                        chain.Reverse();
                        for (int c = 0; c < chain.Count; c++)
                        {
                            Transform link = new GameObject(chain[c].name).transform;
                            link.SetParent(parent, false);
                            link.localPosition = chain[c].localPosition;
                            link.localRotation = chain[c].localRotation;
                            link.localScale = chain[c].localScale;
                            parent = link;
                        }
                        clone.name = source.name;
                        clone.transform.SetParent(parent, false);
                        clone.transform.localPosition = source.transform.localPosition;
                        clone.transform.localRotation = source.transform.localRotation;
                        clone.transform.localScale = source.transform.localScale;
                        cloneAnim = wrapper.AddComponent<Animator>();
                        cloneAnim.runtimeAnimatorController = sourceAnim.runtimeAnimatorController;
                        cloneAnim.avatar = sourceAnim.avatar;
                        top = wrapper;
                        reference = animTransform;
                    }
                }
                if (cloneAnim == null)
                    cloneAnim = clone.GetComponentInChildren<Animator>(true);
                if (cloneAnim != null)
                {
                    cloneAnim.enabled = true;
                    // Posed on the Player every tick: no root motion, never culled, and no animation
                    // events (their receivers were stripped with the scripts).
                    cloneAnim.applyRootMotion = false;
                    cloneAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    cloneAnim.fireEvents = false;
                }

                _v1LocalPosition = mover.transform.InverseTransformPoint(reference.position);
                _v1LocalRotation = Quaternion.Inverse(mover.transform.rotation) * reference.rotation;
                top.transform.SetParent(null, false);
                top.transform.localScale = reference.lossyScale;
                clone.SetActive(true);
                top.SetActive(true);
                UnityEngine.Object.Destroy(holder);

                // Lowest point of the model relative to its origin, used to stand it on the Player's feet.
                float low = 0f;
                bool any = false;
                foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                {
                    float min = renderer.bounds.min.y - top.transform.position.y;
                    low = any ? Mathf.Min(low, min) : min;
                    any = true;
                }
                _v1FootOffset = any ? low : 0f;
                _v1Clone = top;
                _v1Root = top.transform;
                _v1Anim = cloneAnim;
                _v1SourceAnim = sourceAnim;
                _v1SourceParams = null;
                _v1ParamsRead = false;
                _v1AnimPrimed = false;
                if (cloneAnim == null && !_v1AnimWarned)
                {
                    _v1AnimWarned = true;
                    _plugin.LogWarning("Show V1 playermodel: the model has no Animator; it is shown unanimated.");
                }
                return true;
            }
            return false;
        }

        void DestroyV1Clone()
        {
            if (_v1Clone != null)
                UnityEngine.Object.Destroy(_v1Clone);
            _v1Clone = null;
            _v1Root = null;
            _v1Anim = null;
            _v1SourceAnim = null;
            _v1SourceParams = null;
            _v1ParamsRead = false;
            _v1AnimPrimed = false;
        }

        static string RelativePath(Transform root, Transform target)
        {
            string path = "";
            for (Transform t = target; t != null && t != root; t = t.parent)
                path = path.Length == 0 ? t.name : t.name + "/" + path;
            return path;
        }

        /// <summary>
        /// Plays V1's animations on the copy. When the platformer's own Animator is running (a
        /// platformer section), every parameter, the current state and time of each layer, the layer
        /// weights and the speed are copied from it. In a normal level that Animator sits on the
        /// inactive platformer controller and never changes, so the copy is driven from the Player's
        /// movement state the way PlatformerMovement drives its own: the Running, InAir and Sliding
        /// bools, and Play of Landing, SlamStart, SlamEnd, Dash and Jump on the matching events.
        /// </summary>
        void AnimateV1Clone()
        {
            Animator anim = _v1Anim;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null || !anim.isInitialized)
                return;

            Animator source = _v1SourceAnim;
            if (source != null && source.isActiveAndEnabled && source.isInitialized &&
                source.runtimeAnimatorController == anim.runtimeAnimatorController)
            {
                CopyV1Animator(source, anim);
                _v1AnimPrimed = false;
                return;
            }
            DriveV1AnimatorFromPlayer(anim);
        }

        void CopyV1Animator(Animator source, Animator target)
        {
            target.speed = source.speed;
            if (_v1SourceParams == null)
                _v1SourceParams = source.parameters;
            for (int i = 0; i < _v1SourceParams.Length; i++)
            {
                AnimatorControllerParameter parameter = _v1SourceParams[i];
                int hash = parameter.nameHash;
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        target.SetFloat(hash, source.GetFloat(hash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        target.SetInteger(hash, source.GetInteger(hash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        target.SetBool(hash, source.GetBool(hash));
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        if (source.GetBool(hash))
                            target.SetTrigger(hash);
                        else
                            target.ResetTrigger(hash);
                        break;
                }
            }

            int layers = Mathf.Min(source.layerCount, target.layerCount);
            for (int layer = 0; layer < layers; layer++)
            {
                if (layer > 0)
                    target.SetLayerWeight(layer, source.GetLayerWeight(layer));
                // States the platformer enters with Play (not by a parameter) are matched here too.
                AnimatorStateInfo from = source.GetCurrentAnimatorStateInfo(layer);
                AnimatorStateInfo to = target.GetCurrentAnimatorStateInfo(layer);
                if (from.fullPathHash != to.fullPathHash || Mathf.Abs(from.normalizedTime - to.normalizedTime) > 0.05f)
                    target.Play(from.fullPathHash, layer, from.normalizedTime);
            }
        }

        void DriveV1AnimatorFromPlayer(Animator anim)
        {
            GameBridge bridge = _plugin.Bridge;
            object player = bridge != null ? bridge.NewMovement : null;
            if (player == null)
                return;

            if (!_v1ParamsRead)
            {
                _v1ParamsRead = true;
                _v1BoolParams.Clear();
                AnimatorControllerParameter[] parameters = anim.parameters;
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].type == AnimatorControllerParameterType.Bool)
                        _v1BoolParams.Add(parameters[i].nameHash);
            }

            object ground = bridge.GetMember(player, GroundMember);
            bool grounded = ground == null || ReadBool(bridge, ground, OnGroundMember);
            bool heavy = ground != null && ReadBool(bridge, ground, HeavyFallMember);
            bool sliding = ReadBool(bridge, player, SlidingMember);
            // NewMovement.boost is also set while sliding; only a dodge counts as a dash.
            bool dashing = ReadBool(bridge, player, BoostMember) && !sliding;
            bool jumping = ReadBool(bridge, player, JumpingMember);
            bool walking = ReadBool(bridge, player, WalkingMember);
            Rigidbody body = bridge.GetMember(player, BodyMember) as Rigidbody;
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            float horizontal = new Vector2(velocity.x, velocity.z).magnitude;

            SetV1Bool(anim, RunningParam, grounded && !sliding && (walking || horizontal > 1.5f));
            SetV1Bool(anim, InAirParam, !grounded);
            SetV1Bool(anim, SlidingParam, sliding);
            SetV1Bool(anim, SpinningParam, false);

            // Events, in PlatformerMovement's order. The last Play before the Animator updates wins, so
            // a jump or dash on the landing tick shows the jump or dash.
            if (_v1AnimPrimed)
            {
                if (grounded && !_v1WasGrounded)
                {
                    if (_v1WasHeavy || heavy)
                        PlayV1State(anim, "SlamEnd");
                    else if (!sliding)
                        PlayV1State(anim, "Landing");
                }
                if (heavy && !_v1WasHeavy && !grounded)
                    PlayV1State(anim, "SlamStart");
                if (dashing && !_v1WasDashing)
                    PlayV1State(anim, "Dash");
                if (jumping && !_v1WasJumping)
                    PlayV1State(anim, "Jump");
            }
            _v1AnimPrimed = true;
            _v1WasGrounded = grounded;
            _v1WasHeavy = heavy;
            _v1WasDashing = dashing;
            _v1WasJumping = jumping;
        }

        static bool ReadBool(GameBridge bridge, object target, string[] member)
        {
            object value = bridge.GetMember(target, member);
            return value is bool && (bool)value;
        }

        void SetV1Bool(Animator anim, int hash, bool value)
        {
            if (_v1BoolParams.Contains(hash))
                anim.SetBool(hash, value);
        }

        /// <summary>Animator.Play of a platformer state by name, only when the controller has it.</summary>
        static void PlayV1State(Animator anim, string name)
        {
            int hash = Animator.StringToHash(name);
            for (int layer = 0; layer < anim.layerCount; layer++)
            {
                if (anim.HasState(layer, hash))
                {
                    anim.Play(hash, layer, 0f);
                    return;
                }
            }
            int fullPath = Animator.StringToHash("Base Layer." + name);
            if (anim.layerCount > 0 && anim.HasState(0, fullPath))
                anim.Play(fullPath, 0, 0f);
        }

        static void StripComponent(Component component)
        {
            if (component == null)
                return;
            try { UnityEngine.Object.DestroyImmediate(component); }
            catch (Exception) { }
        }

        void PoseV1Clone(Transform player)
        {
            Vector3 position = player.TransformPoint(_v1LocalPosition);
            Collider body = player.GetComponent<Collider>();
            if (body != null)
                position.y = body.bounds.min.y - _v1FootOffset;
            _v1Root.SetPositionAndRotation(position, player.rotation * _v1LocalRotation);
        }

        /// <summary>Fallback when the level has no platformer model: enable disabled skinned meshes of the Player rig.</summary>
        bool EnableV1SkinnedMeshes(Transform player)
        {
            SkinnedMeshRenderer[] renderers = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i].enabled || renderers[i].sharedMesh == null ||
                    renderers[i].GetComponentInParent<Camera>() != null)
                    continue;
                renderers[i].enabled = true;
                _v1BodyEnabled.Add(renderers[i]);
            }
            return _v1BodyEnabled.Count > 0;
        }

        /// <summary>Per-frame sync: F2 can end freecam without going through this controller.</summary>
        public void Tick()
        {
            // Render-pass state normally ends in OnPostRender; never let it outlive a frame.
            RestoreViewmodelsAfterWorld();
            EndHudDepthIsolation();
            SyncV1Body();
            if (_mode == TasCameraMode.Freecam && !_plugin.FreecamActive)
            {
                _mode = TasCameraMode.FirstPerson;
                ReleaseCursor();
            }
            else if (_plugin.FreecamActive && _mode != TasCameraMode.Freecam)
            {
                // Freecam can also start from F2, which bypasses SetMode.
                _mode = TasCameraMode.Freecam;
                _cursorFree = false;
            }
        }

        /// <summary>
        /// Drops the free-cursor choice. Plugin.UpdateCursorState sees the change and locks the
        /// cursor once, then leaves it to ULTRAKILL's own mouse handling.
        /// </summary>
        void ReleaseCursor()
        {
            _cursorFree = false;
        }

        void OnPreCull(Camera camera)
        {
            Vector3 freePosition;
            Quaternion freeRotation;
            bool freecam = _plugin.TryGetFreecamPose(out freePosition, out freeRotation);
            if ((_mode != TasCameraMode.ThirdPerson && !freecam) || camera == null)
                return;
            // The video renderer positions the camera for its own sub-frame samples.
            if (_plugin.Video != null && _plugin.Video.Rendering)
                return;
            if (camera != Camera.main)
            {
                BeginViewmodelPass(camera, freecam);
                return;
            }

            Transform player = _plugin.Bridge != null ? _plugin.Bridge.PlayerTransform : null;
            if (player == null)
                return;

            Transform cameraTransform = camera.transform;
            _savedCamera = camera;
            _savedPosition = cameraTransform.position;
            _savedRotation = cameraTransform.rotation;
            _savedPose = true;

            // The weapon and arm models hang under the game camera, so they would move with it. They
            // are kept out of this world pass; the HUD camera draws them from the TAS pose afterwards.
            HideViewmodelsForWorld(cameraTransform);

            // Freecam only moves the view: the game's transform, which shots and aim read, is put
            // back right after this camera renders.
            if (freecam)
            {
                cameraTransform.SetPositionAndRotation(freePosition, freeRotation);
                return;
            }

            // The game camera sits at V1's head. Orbit it behind that point along the
            // look direction, so the view follows V1's aim and V1 stays in frame.
            Vector3 head = _savedPosition;
            Vector3 back = -(_savedRotation * Vector3.forward);
            float distance = _plugin.ThirdPersonDistance != null ? _plugin.ThirdPersonDistance.Value : DefaultThirdDistance;
            float height = _plugin.ThirdPersonHeight != null ? _plugin.ThirdPersonHeight.Value : DefaultThirdHeight;
            Vector3 position = head + back * Mathf.Max(0.5f, distance) + Vector3.up * height;
            Quaternion rotation = Quaternion.LookRotation(head - position, Vector3.up);
            cameraTransform.SetPositionAndRotation(position, rotation);
        }

        // Camera pose swapped by ApplyView for the duration of one world-to-screen overlay pass.
        Transform _viewTransform;
        Vector3 _viewPosition;
        Quaternion _viewRotation;

        /// <summary>
        /// Puts the game camera at the freecam view pose until the returned scope is disposed. Overlays
        /// that project world points to the screen (hitboxes, triggers, the path hover) and the box
        /// placement raycast use it, because the game camera itself stays at the TAS pose. No-op
        /// outside freecam.
        /// </summary>
        public ViewScope ApplyView()
        {
            ViewScope scope = new ViewScope();
            Vector3 position;
            Quaternion rotation;
            Transform camera;
            if (_viewTransform != null || !_plugin.TryGetFreecamPose(out position, out rotation) ||
                _plugin.Bridge == null || !_plugin.Bridge.TryGetCameraTransform(out camera) || camera == null)
                return scope;
            _viewTransform = camera;
            _viewPosition = camera.position;
            _viewRotation = camera.rotation;
            camera.SetPositionAndRotation(position, rotation);
            scope.Owner = this;
            return scope;
        }

        void EndView()
        {
            if (_viewTransform != null)
                _viewTransform.SetPositionAndRotation(_viewPosition, _viewRotation);
            _viewTransform = null;
        }

        public struct ViewScope : IDisposable
        {
            internal TasCameraController Owner;

            public void Dispose()
            {
                if (Owner != null)
                    Owner.EndView();
            }
        }

        void OnPostRender(Camera camera)
        {
            if (camera == null)
                return;
            if (camera == _hudDepthCamera)
                EndHudDepthIsolation();
            if (!_savedPose || camera != _savedCamera)
                return;
            _savedPose = false;
            camera.transform.SetPositionAndRotation(_savedPosition, _savedRotation);
            RestoreViewmodelsAfterWorld();
        }

        // ------------------------------------------------------------------
        // Viewmodels while the view is moved
        // ------------------------------------------------------------------
        //
        // ULTRAKILL draws in three cameras: the game camera (world, into PostProcessV2_Handler's
        // mainTex and depthBuffer), its child "HUD Camera" (HUD canvas, weapons and arms, into the same
        // mainTex and depthBuffer, with its own viewmodel FOV) and its child "Virtual Camera" (puts mainTex
        // on screen with the post-process shader). The weapon (GunControl) and arm (FistControl) models
        // are children of the game camera.
        //
        // Freecam and third person move only the game camera, only for its own world pass:
        //   - the viewmodel renderers are kept out of that pass, so they never show at the moved pose;
        //   - the HUD camera renders after OnPostRender has put the game camera back, so it draws the
        //     weapons and arms from V1's position at the TAS angle. If it were ever reached first, the
        //     TAS pose is put back before it culls;
        //   - in freecam the depth it tests against belongs to the freecam view, which would cut the
        //     weapons wherever freecam geometry is nearer. For the HUD pass only, depth is cleared and
        //     the freecam depth copied back afterwards, so the post-process still sees the world depth.

        static readonly string[] HudCameraMember = { "hudCamera" };
        static readonly string[] HudCamMember = { "hudCam" };
        static readonly string[] DepthBufferMember = { "depthBuffer" };

        readonly List<Renderer> _vmRenderers = new List<Renderer>();
        readonly List<bool> _vmWasForcedOff = new List<bool>();
        readonly List<Renderer> _vmScratch = new List<Renderer>();
        Transform _vmGunRoot;
        Transform _vmFistRoot;
        int _vmScanFrame = -1000;
        bool _vmHidden;

        Camera _hudCamera;
        float _hudNextSearch;
        UnityEngine.Object _postProcess;
        float _postProcessNextSearch;
        Camera _hudDepthCamera;
        CommandBuffer _hudDepthClear;
        CommandBuffer _hudDepthRestore;
        RenderTexture _hudDepthCopy;
        bool _hudDepthDisabled;

        void HideViewmodelsForWorld(Transform cameraTransform)
        {
            RestoreViewmodelsAfterWorld();
            GameBridge bridge = _plugin.Bridge;
            if (bridge == null)
                return;
            Transform gun = TransformOf(bridge.GunControl);
            Transform fist = TransformOf(bridge.FistControl);
            // Only models that hang under the game camera follow its pose.
            if (gun != null && !gun.IsChildOf(cameraTransform))
                gun = null;
            if (fist != null && !fist.IsChildOf(cameraTransform))
                fist = null;

            // Weapons are swapped and arms re-instantiated now and then: rescan a few times a second.
            if (gun != _vmGunRoot || fist != _vmFistRoot || Time.frameCount - _vmScanFrame > 30)
            {
                _vmGunRoot = gun;
                _vmFistRoot = fist;
                _vmScanFrame = Time.frameCount;
                _vmRenderers.Clear();
                AddViewmodelRenderers(gun);
                if (fist != gun)
                    AddViewmodelRenderers(fist);
            }

            _vmWasForcedOff.Clear();
            for (int i = 0; i < _vmRenderers.Count; i++)
            {
                Renderer renderer = _vmRenderers[i];
                bool was = renderer != null && renderer.forceRenderingOff;
                _vmWasForcedOff.Add(was);
                if (renderer != null)
                    renderer.forceRenderingOff = true;
            }
            _vmHidden = true;
        }

        void AddViewmodelRenderers(Transform root)
        {
            if (root == null)
                return;
            _vmScratch.Clear();
            root.GetComponentsInChildren(true, _vmScratch);
            _vmRenderers.AddRange(_vmScratch);
            _vmScratch.Clear();
        }

        /// <summary>Puts back each viewmodel renderer's own forceRenderingOff (also the "Hide viewmodels" option's).</summary>
        void RestoreViewmodelsAfterWorld()
        {
            if (!_vmHidden)
                return;
            _vmHidden = false;
            int count = Mathf.Min(_vmRenderers.Count, _vmWasForcedOff.Count);
            for (int i = 0; i < count; i++)
            {
                Renderer renderer = _vmRenderers[i];
                if (renderer != null)
                    renderer.forceRenderingOff = _vmWasForcedOff[i];
            }
        }

        static Transform TransformOf(object value)
        {
            Component component = value as Component;
            return component != null ? component.transform : null;
        }

        void BeginViewmodelPass(Camera camera, bool freecam)
        {
            Camera hud = FindHudCamera();
            if (hud == null || camera != hud)
                return;

            if (_savedPose && _savedCamera != null && camera.transform.IsChildOf(_savedCamera.transform))
            {
                _savedPose = false;
                _savedCamera.transform.SetPositionAndRotation(_savedPosition, _savedRotation);
                RestoreViewmodelsAfterWorld();
            }

            if (freecam)
                BeginHudDepthIsolation(camera);
        }

        Camera FindHudCamera()
        {
            if (_hudCamera != null)
                return _hudCamera;
            if (TimeController.RealTime < _hudNextSearch)
                return null;
            _hudNextSearch = TimeController.RealTime + 1f;

            GameBridge bridge = _plugin.Bridge;
            if (bridge == null)
                return null;
            object controller = bridge.CameraController;
            Camera hud = controller != null ? bridge.GetMember(controller, HudCameraMember) as Camera : null;
            if (hud == null)
            {
                UnityEngine.Object post = FindPostProcess();
                hud = post != null ? bridge.GetMember(post, HudCamMember) as Camera : null;
            }
            _hudCamera = hud;
            return hud;
        }

        UnityEngine.Object FindPostProcess()
        {
            if (_postProcess != null)
                return _postProcess;
            if (TimeController.RealTime < _postProcessNextSearch)
                return null;
            _postProcessNextSearch = TimeController.RealTime + 1f;
            Type type = _plugin.Bridge != null ? _plugin.Bridge.FindType("PostProcessV2_Handler") : null;
            if (type == null)
                return null;
            try { _postProcess = UnityEngine.Object.FindObjectOfType(type); }
            catch (Exception) { _postProcess = null; }
            return _postProcess;
        }

        void BeginHudDepthIsolation(Camera camera)
        {
            EndHudDepthIsolation();
            if (_hudDepthDisabled)
                return;
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
            {
                _hudDepthDisabled = true;
                _plugin.LogWarning("Freecam: this GPU cannot copy textures; viewmodels are depth tested against the freecam view.");
                return;
            }

            UnityEngine.Object post = FindPostProcess();
            RenderTexture depth = post != null ? _plugin.Bridge.GetMember(post, DepthBufferMember) as RenderTexture : null;
            if (depth == null || !depth.IsCreated())
                return;

            try
            {
                if (_hudDepthCopy == null || !SameLayout(_hudDepthCopy, depth))
                {
                    if (_hudDepthCopy != null)
                        UnityEngine.Object.Destroy(_hudDepthCopy);
                    _hudDepthCopy = new RenderTexture(depth.descriptor);
                    _hudDepthCopy.name = "UKTAS freecam depth";
                    _hudDepthCopy.Create();
                }
                if (_hudDepthClear == null)
                {
                    _hudDepthClear = new CommandBuffer();
                    _hudDepthClear.name = "UKTAS freecam viewmodel depth clear";
                    _hudDepthRestore = new CommandBuffer();
                    _hudDepthRestore.name = "UKTAS freecam viewmodel depth restore";
                }
                _hudDepthClear.Clear();
                _hudDepthClear.CopyTexture(depth, _hudDepthCopy);
                _hudDepthClear.ClearRenderTarget(true, false, Color.clear);
                _hudDepthRestore.Clear();
                _hudDepthRestore.CopyTexture(_hudDepthCopy, depth);
                camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _hudDepthClear);
                camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _hudDepthRestore);
                _hudDepthCamera = camera;
            }
            catch (Exception ex)
            {
                _hudDepthDisabled = true;
                _plugin.LogWarning("Freecam viewmodel depth isolation disabled: " + ex.Message);
                EndHudDepthIsolation();
            }
        }

        void EndHudDepthIsolation()
        {
            Camera camera = _hudDepthCamera;
            _hudDepthCamera = null;
            if (camera == null || _hudDepthClear == null)
                return;
            try
            {
                camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _hudDepthClear);
                camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _hudDepthRestore);
            }
            catch (Exception)
            {
                // The camera died with its scene.
            }
        }

        static bool SameLayout(RenderTexture a, RenderTexture b)
        {
            return a.width == b.width && a.height == b.height && a.volumeDepth == b.volumeDepth &&
                   a.dimension == b.dimension && a.graphicsFormat == b.graphicsFormat &&
                   a.depthStencilFormat == b.depthStencilFormat && a.antiAliasing == b.antiAliasing;
        }
    }
}
