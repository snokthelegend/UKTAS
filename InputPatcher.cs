using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace UltrakillTAS
{
    /// <summary>
    /// Owns all input injection.
    ///
    /// ULTRAKILL reads gameplay through its own InputManager.InputSource action
    /// states (verified: InputSource.Fire1.IsPressed / .PerformedFrame), so the
    /// authoritative path is writing those states every tick. As a second layer
    /// the same actions are translated back into their currently bound KeyCodes
    /// (InputManager.inputsDictionary) and queued as a full Input System
    /// keyboard/mouse state. That means:
    ///   - the game's own InputManager recomputes exactly what the TAS wants,
    ///     even if it overwrites our action states later in the frame;
    ///   - rebinds keep working;
    ///   - every physical key not requested by the script is released, so the
    ///     player's keyboard cannot leak into a replay.
    ///
    /// Menus additionally use the standard navigation keys, and legacy uGUI
    /// menus are driven through BaseInput patches. Camera angles are enforced
    /// immediately before CameraController runs (prefix patch), so no smoothing
    /// or one-frame drift can alter authored pitch/yaw.
    /// </summary>
    public sealed partial class InputPatcher
    {
        public static InputPatcher Instance;

        readonly Plugin _plugin;
        readonly GameBridge _bridge;
        Harmony _harmony;

        bool _active;
        bool _hasAngles;
        bool _freeLook;
        bool _freecam;
        bool _staging;
        int _lastFreeLookFrame = -1;
        int _lastFreecamFrame = -1;
        float _freecamRealStamp = -1f;
        Vector3 _freecamPosition;
        float _freecamPitch;
        float _freecamYaw;
        // The camera transform the freecam is currently driving; a level load swaps it for a new one.
        Transform _freecamCamera;
        bool _holdAngles;
        float _holdPitch;
        float _holdYaw;
        TasButtons _stagedButtons;
        float _stagedPitch;
        float _stagedYaw;
        float _pitch;
        float _yaw;
        TasButtons _buttons;
        bool _menuConfirmWasDown;
        bool _menuCancelWasDown;
        bool _menuPauseWasDown;
        bool _menuUpWasDown;
        bool _menuDownWasDown;
        bool _menuLeftWasDown;
        bool _menuRightWasDown;
        bool _confirmDown;
        bool _cancelDown;
        bool _pauseDown;
        bool _upDown;
        bool _downDown;
        bool _leftDown;
        bool _rightDown;

        object _cachedSource;
        readonly Dictionary<string, ActionAccessor> _accessors = new Dictionary<string, ActionAccessor>(StringComparer.Ordinal);
        Dictionary<string, KeyCode> _bindings;
        int _lastBindingRefreshFrame = -1000;

        bool _legacyUiPatched;
        bool _cameraPatched;
        bool _legacyUiAttempted;
        bool _cameraAttempted;
        int _lastPatchAttemptFrame = -1000;

        readonly bool[] _mouseButtonScratch = new bool[5];
        bool _warpFailed;
        readonly List<UnityEngine.InputSystem.Key> _keyBuffer = new List<UnityEngine.InputSystem.Key>(16);
        bool _inputSystemUpdateFailed;

        /// <summary>
        /// True while the TAS window captures input (see KeyUtil.GuiCapturesInput): real keyboard and
        /// mouse input is withheld from the game, scripted input still goes through.
        /// </summary>
        public bool HardwareBlocked { get { return KeyUtil.GuiCapturesInput; } }
        public bool HasAngles { get { return _hasAngles; } }
        public float CurrentPitch { get { return _pitch; } }
        public float CurrentYaw { get { return _yaw; } }
        public TasButtons CurrentButtons { get { return _buttons; } }
        public bool IsActive { get { return _active; } }
        public bool FreeLookActive { get { return _freeLook; } }
        public bool FreecamActive { get { return _freecam; } }
        /// <summary>Freecam is on and the camera it drives still exists (false in the gap of a level load).</summary>
        public bool FreecamAttached { get { return _freecam && _freecamCamera != null; } }
        public TasButtons StagedButtons { get { return _stagedButtons; } }
        public float StagedPitch { get { return _stagedPitch; } }
        public float StagedYaw { get { return _stagedYaw; } }
        public bool LegacyUiPatched { get { return _legacyUiPatched; } }
        public bool CameraPatched { get { return _cameraPatched; } }

        static readonly Dictionary<TasButtons, string[]> ActionNames =
            new Dictionary<TasButtons, string[]>
        {
            { TasButtons.Forward, new[] { "W", "Forward", "MoveForward", "Up" } },
            { TasButtons.Back, new[] { "S", "Back", "MoveBack", "Down" } },
            { TasButtons.Left, new[] { "A", "Left", "MoveLeft" } },
            { TasButtons.Right, new[] { "D", "Right", "MoveRight" } },
            { TasButtons.Jump, new[] { "Jump" } },
            { TasButtons.Slide, new[] { "Slide" } },
            { TasButtons.Dash, new[] { "Dodge", "Dash" } },
            { TasButtons.Fire1, new[] { "Fire1", "Fire", "Shoot" } },
            { TasButtons.Fire2, new[] { "Fire2", "AltFire", "SecondaryFire" } },
            { TasButtons.Punch, new[] { "Punch", "Fist" } },
            { TasButtons.Whiplash, new[] { "Hook", "Whiplash" } },
            { TasButtons.Slot1, new[] { "Slot1" } },
            { TasButtons.Slot2, new[] { "Slot2" } },
            { TasButtons.Slot3, new[] { "Slot3" } },
            { TasButtons.Slot4, new[] { "Slot4" } },
            { TasButtons.Slot5, new[] { "Slot5" } },
            { TasButtons.Slot6, new[] { "Slot6" } },
            // PlayerInput's own action-state names (verified against the game assembly).
            { TasButtons.FistChange, new[] { "ChangeFist" } },
            { TasButtons.FistFeedbacker, new[] { "PunchFeedbacker" } },
            { TasButtons.FistKnuckleblaster, new[] { "PunchKnuckleblaster" } },
            { TasButtons.WeaponNext, new[] { "NextWeapon" } },
            { TasButtons.WeaponPrev, new[] { "PrevWeapon" } },
            { TasButtons.WeaponLast, new[] { "LastWeapon" } },
            { TasButtons.VariationNext, new[] { "NextVariation" } },
            { TasButtons.VariationPrev, new[] { "PreviousVariation" } },
            { TasButtons.Variant1, new[] { "SelectVariant1" } },
            { TasButtons.Variant2, new[] { "SelectVariant2" } },
            { TasButtons.Variant3, new[] { "SelectVariant3" } },
            { TasButtons.Stats, new[] { "Stats" } }
        };

        static readonly TasButtons[] AllActionButtons =
        {
            TasButtons.Forward, TasButtons.Back, TasButtons.Left, TasButtons.Right,
            TasButtons.Jump, TasButtons.Slide, TasButtons.Dash,
            TasButtons.Fire1, TasButtons.Fire2, TasButtons.Punch, TasButtons.Whiplash,
            TasButtons.Slot1, TasButtons.Slot2, TasButtons.Slot3,
            TasButtons.Slot4, TasButtons.Slot5, TasButtons.Slot6,
            TasButtons.FistChange, TasButtons.FistFeedbacker, TasButtons.FistKnuckleblaster,
            TasButtons.WeaponNext, TasButtons.WeaponPrev, TasButtons.WeaponLast,
            TasButtons.VariationNext, TasButtons.VariationPrev,
            TasButtons.Variant1, TasButtons.Variant2, TasButtons.Variant3,
            TasButtons.Stats
        };

        sealed class ActionAccessor
        {
            public string Name;
            public MemberInfo SourceMember;
            public MemberInfo PressedMember;
            public MemberInfo PerformedMember;
            public bool Resolved;
        }

        public InputPatcher(Plugin plugin, GameBridge bridge)
        {
            _plugin = plugin;
            _bridge = bridge;
            Instance = this;
        }

        public void Initialize()
        {
            _harmony = new Harmony("com.tas.ultrakill.input");
            TryPatchLegacyUi();
            TryPatchGameHooks();
        }

        public void Shutdown()
        {
            EndFreecam();
            EndFreeLook();
            ReleaseAll();
            if (_harmony != null)
                _harmony.UnpatchSelf();
        }

        // ------------------------------------------------------------------
        // Frame input
        // ------------------------------------------------------------------

        public void SetActive(bool active)
        {
            if (!active)
            {
                ReleaseControl();
                return;
            }

            EndFreeLook();
            _active = true;
            // Do not enforce stale angles while recording or before the first
            // tick has supplied a viewangle.
            _hasAngles = false;
            RefreshBindings(true);
        }

        /// <summary>
        /// Return control to the game: clear the angle override, release every
        /// injected action/device state, and stop the camera patch from writing
        /// scripted pitch/yaw. This is intentionally stronger than InjectNeutral,
        /// which keeps ownership of the camera while a script is paused.
        /// </summary>
        public void ReleaseControl()
        {
            _freeLook = false;
            _staging = false;
            _active = false;
            _hasAngles = false;
            ReleaseAll();
        }

        /// <summary>
        /// Enter the paused authoring camera. Gameplay actions are forced neutral
        /// (staged instead of applied), while CameraController.Update is replaced
        /// by raw mouse-look that ignores Time.timeScale and the frame's delta (the TAS
        /// clock arms paused frames with an almost-zero captureDeltaTime).
        /// </summary>
        public void BeginFreeLook(float pitch, float yaw)
        {
            if (_freeLook)
            {
                _stagedPitch = _pitch;
                _stagedYaw = _yaw;
                return;
            }

            ReleaseControl();
            _freeLook = true;
            _staging = true;
            _pitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            _yaw = yaw;
            _stagedPitch = _pitch;
            _stagedYaw = _yaw;
            _stagedButtons = TasButtons.None;
            _lastFreeLookFrame = -1;
            _buttons = TasButtons.None;
            ApplyActionStates(TasButtons.None);
            ApplyDeviceState(TasButtons.None);
            TryPatchGameHooks();
        }

        public void EndFreeLook()
        {
            _freeLook = false;
            _staging = false;
            _stagedButtons = TasButtons.None;
        }

        /// <summary>
        /// Detach the view from the player without touching gameplay. The camera patch skips
        /// CameraController while this is active, so the transform can be moved with unscaled
        /// input and wall-clock time even when the game's Time.timeScale is 0 or the TAS
        /// clock has armed a frozen (almost-zero) captureDeltaTime.
        /// </summary>
        public bool BeginFreecam()
        {
            if (_freecam)
                return true;
            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera) || camera == null)
                return false;
            TryPatchGameHooks();
            _freecam = true;
            _lastFreecamFrame = -1;
            _freecamRealStamp = -1f;
            _freecamCamera = camera;
            _freecamPosition = camera.position;
            Vector3 euler = camera.eulerAngles;
            _freecamPitch = euler.x > 180f ? euler.x - 360f : euler.x;
            _freecamYaw = euler.y;
            return true;
        }

        public void EndFreecam()
        {
            if (!_freecam)
                return;
            _freecam = false;
            // The game camera transform is never moved by freecam (see TryGetFreecamPose), so there is
            // nothing to put back.
            _freecamCamera = null;
        }

        /// <summary>
        /// The freecam view pose. Freecam is render-only: the game camera keeps the pose the TAS runs
        /// with (shots, aim and weapons read it) and TasCameraController draws this pose instead.
        /// </summary>
        public bool TryGetFreecamPose(out Vector3 position, out Quaternion rotation)
        {
            position = _freecamPosition;
            rotation = Quaternion.Euler(_freecamPitch, _freecamYaw, 0f);
            return _freecam && _freecamCamera != null;
        }

        /// <summary>
        /// Real mouse movement for freecam look. Legacy Input.GetAxisRaw is read from the OS and never
        /// carries script input; the Input System mouse is only a fallback while no script is injecting
        /// device state (the injected mouse state is written by ApplyDeviceState).
        /// </summary>
        void ReadUserMouseDelta(out float mouseX, out float mouseY)
        {
            mouseX = 0f;
            mouseY = 0f;
            try
            {
                mouseX = Input.GetAxisRaw("Mouse X");
                mouseY = Input.GetAxisRaw("Mouse Y");
            }
            catch (Exception)
            {
                // The Input System-only path below is still available.
            }
            if (!_active && Mathf.Abs(mouseX) < 0.00001f && Mathf.Abs(mouseY) < 0.00001f &&
                UnityEngine.InputSystem.Mouse.current != null)
            {
                Vector2 delta = UnityEngine.InputSystem.Mouse.current.delta.ReadValue();
                mouseX = delta.x;
                mouseY = delta.y;
            }
        }

        /// <summary>A freecam key: the user's real key only, never the script's injected key.</summary>
        bool FreecamKeyHeld(KeyCode key)
        {
            return KeyUtil.UserHeld(key, !_active);
        }

        public void ToggleFreecam()
        {
            if (_freecam)
                EndFreecam();
            else
                BeginFreecam();
        }

        void ApplyFreecam()
        {
            // CameraController has both Update and LateUpdate on some builds; move once a frame.
            if (_lastFreecamFrame == SimulationFreeze.FrameCount)
                return;
            _lastFreecamFrame = SimulationFreeze.FrameCount;

            Transform camera;
            if (!_bridge.TryGetCameraTransform(out camera) || camera == null)
                return;

            // A level load replaced the game camera: follow the new one with the pose kept here
            // (position, pitch, yaw are world-space and survive).
            if (camera != _freecamCamera)
            {
                _freecamCamera = camera;
                _freecamRealStamp = -1f;
            }

            if (Cursor.lockState == CursorLockMode.Locked && !HardwareBlocked)
            {
                float mouseX;
                float mouseY;
                ReadUserMouseDelta(out mouseX, out mouseY);
                float sensitivity = Mathf.Max(0.01f, _plugin.FreeLookSensitivity.Value);
                _freecamYaw += mouseX * sensitivity;
                _freecamPitch = Mathf.Clamp(_freecamPitch - mouseY * sensitivity, -89.9f, 89.9f);
            }

            float speed = Mathf.Max(0.1f, _plugin.FreecamSpeed.Value);
            if (FreecamKeyHeld(KeyCode.LeftShift) || FreecamKeyHeld(KeyCode.RightShift))
                speed *= 4f;
            if (FreecamKeyHeld(KeyCode.LeftControl) || FreecamKeyHeld(KeyCode.RightControl))
                speed *= 0.25f;

            Quaternion look = Quaternion.Euler(_freecamPitch, _freecamYaw, 0f);
            Vector3 move = Vector3.zero;
            if (FreecamKeyHeld(KeyCode.W))
                move += look * Vector3.forward;
            if (FreecamKeyHeld(KeyCode.S))
                move -= look * Vector3.forward;
            if (FreecamKeyHeld(KeyCode.D))
                move += look * Vector3.right;
            if (FreecamKeyHeld(KeyCode.A))
                move -= look * Vector3.right;
            if (FreecamKeyHeld(KeyCode.Space))
                move += Vector3.up;
            if (FreecamKeyHeld(KeyCode.Q))
                move -= Vector3.up;
            if (move.sqrMagnitude > 1f)
                move.Normalize();

            // Wall-clock delta: Time.unscaledDeltaTime follows captureDeltaTime, which is
            // almost 0 on the TAS clock's frozen (paused) frames.
            float now = TimeController.RealTime;
            float deltaTime = _freecamRealStamp < 0f ? 0f : Mathf.Clamp(now - _freecamRealStamp, 0f, 0.1f);
            _freecamRealStamp = now;
            _freecamPosition += move * speed * deltaTime;
        }

        public void UpdateStaging()
        {
            if (!_freeLook)
                return;
            TryPatchGameHooks();
            _stagedButtons = ReadPhysicalButtonsLegacy();
            _stagedPitch = _pitch;
            _stagedYaw = _yaw;
        }

        public void CaptureStaged(out TasButtons buttons, out float pitch, out float yaw)
        {
            UpdateStaging();
            buttons = _freeLook ? _stagedButtons : _buttons;
            pitch = _freeLook ? _stagedPitch : _pitch;
            yaw = _freeLook ? _stagedYaw : _yaw;
        }

        /// <summary>Inject one exact tick. Called before ULTRAKILL's Update methods.</summary>
        public void ApplyFrame(TasButtons buttons, float pitch, float yaw)
        {
            ApplyFrame(buttons, TasButtons.None, pitch, yaw);
        }

        /// <summary>
        /// Weapon slot keys (1-6), variant select keys and change fist (x) always count as a new
        /// press on every tick they are set, so a script can switch weapon, variant or fist on
        /// every frame (a held x therefore swaps arms once per tick instead of once per hold).
        /// They are merged into the repeatPress mask of every ApplyFrame.
        /// </summary>
        const TasButtons AlwaysRepeatPress =
            TasButtons.Slot1 | TasButtons.Slot2 | TasButtons.Slot3 |
            TasButtons.Slot4 | TasButtons.Slot5 | TasButtons.Slot6 |
            TasButtons.Variant1 | TasButtons.Variant2 | TasButtons.Variant3 |
            TasButtons.FistChange;

        /// <summary>
        /// Inject one exact tick. A button that was down on the previous injected tick and is
        /// still down is injected as held: IsPressed stays true and its PerformedFrame stamp is
        /// left alone, so the game sees one continuous press (Slide especially: a fresh press
        /// edge every tick reads as crouch spam and can restart or break a slide). Only the first
        /// tick of a run stamps a press; the release is the first tick the button is off (or a
        /// neutral injection, which also forgets the previous tick). <paramref name="repeatPress"/>
        /// names buttons that must count as a new press on every tick they are down regardless,
        /// for the movement autofuncs that retry a jump or dash until the game accepts it, and for
        /// the slot / variant / change-fist keys that may fire once per frame.
        /// </summary>
        public void ApplyFrame(TasButtons buttons, TasButtons repeatPress, float pitch, float yaw)
        {
            repeatPress |= AlwaysRepeatPress;
            TasButtons previous = _buttons;
            _buttons = buttons;
            _pitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            _yaw = yaw;
            _hasAngles = true;

            ComputeMenuEdges(buttons);

            if (_active || HardwareBlocked)
            {
                TryPatchGameHooks();
                // Device state first. Queueing a full keyboard/mouse state makes the
                // Input System recompute the game's actions, so action states must be
                // written after it or they are immediately overwritten.
                ApplyDeviceState(buttons);
                // Held across ticks (or across a savestate load, see RestoreHeldState): no
                // fresh press edge. Only a button that is new this tick is stamped.
                ApplyActionStates(buttons, buttons & previous & ~repeatPress);
            }
        }

        /// <summary>
        /// The buttons the script is injecting right now. A savestate stores this mask so a
        /// held jump or punch does not fire its one-shot action again on the frame after load.
        /// </summary>
        public TasButtons HeldButtons { get { return _buttons; } }

        /// <summary>
        /// Put the held mask of a loaded state back, and suppress the press edge for every
        /// button that was already down when it was captured. ApplyActionStates still writes
        /// IsPressed (so the game sees the button held) but leaves the performed-frame stamp
        /// from the saved timeline, so a one-shot action does not repeat. _buttons becomes the
        /// "previous tick" the next ApplyFrame compares against, so the held buttons stay
        /// edge-free until they are released once.
        /// </summary>
        public void RestoreHeldState(TasButtons buttons)
        {
            _buttons = buttons;
            if (!_active && !HardwareBlocked)
                return;
            ApplyDeviceState(buttons);
            // Same suppression as the next ApplyFrame: held state must not stamp a press edge.
            ApplyActionStates(buttons, buttons);
        }

        /// <summary>Release everything while paused or while the GUI owns the mouse.</summary>
        public void InjectNeutral()
        {
            if (!_active && !HardwareBlocked)
                return;
            _buttons = TasButtons.None;
            ComputeMenuEdges(TasButtons.None);
            ApplyActionStates(TasButtons.None);
            ApplyDeviceState(TasButtons.None);
        }

        /// <summary>
        /// The GUI stopped capturing input: drop everything that was injected on its behalf (the
        /// neutral device state, a passed-through Escape) so nothing stays held. A running script
        /// owns the devices again on its next tick, so it is left alone.
        /// </summary>
        public void OnGuiCaptureEnded()
        {
            _holdAngles = false;
            if (!_active)
                ReleaseAll();
        }

        /// <summary>
        /// The TAS window took the pointer and the keyboard (it just opened): remember the viewangles
        /// the game has right now. EnforceCameraAngles writes them back every frame until the window
        /// closes, so moving the mouse with the menu open cannot turn the view even on a mouse path
        /// the device injection does not cover.
        /// </summary>
        public void OnGuiCaptureBegan()
        {
            float pitch;
            float yaw;
            _holdAngles = _bridge.TryGetCameraAngles(out pitch, out yaw);
            _holdPitch = pitch;
            _holdYaw = yaw;
        }

        /// <summary>New level: the held viewangles belong to the old scene, so they are read again.</summary>
        public void OnSceneChanged()
        {
            _holdAngles = false;
        }

        public void ReleaseAll()
        {
            _buttons = TasButtons.None;
            ComputeMenuEdges(TasButtons.None);
            ApplyActionStates(TasButtons.None);
            ApplyDeviceState(TasButtons.None);
        }

        void ComputeMenuEdges(TasButtons buttons)
        {
            bool confirm = Has(buttons, TasButtons.MenuConfirm);
            bool cancel = Has(buttons, TasButtons.MenuCancel);
            bool pause = Has(buttons, TasButtons.MenuPause);
            bool up = Has(buttons, TasButtons.MenuUp);
            bool down = Has(buttons, TasButtons.MenuDown);
            bool left = Has(buttons, TasButtons.MenuLeft);
            bool right = Has(buttons, TasButtons.MenuRight);

            _confirmDown = confirm && !_menuConfirmWasDown;
            _cancelDown = cancel && !_menuCancelWasDown;
            _pauseDown = pause && !_menuPauseWasDown;
            _upDown = up && !_menuUpWasDown;
            _downDown = down && !_menuDownWasDown;
            _leftDown = left && !_menuLeftWasDown;
            _rightDown = right && !_menuRightWasDown;

            _menuConfirmWasDown = confirm;
            _menuCancelWasDown = cancel;
            _menuPauseWasDown = pause;
            _menuUpWasDown = up;
            _menuDownWasDown = down;
            _menuLeftWasDown = left;
            _menuRightWasDown = right;
        }

        static bool Has(TasButtons value, TasButtons flag)
        {
            return (value & flag) != 0;
        }

        // ------------------------------------------------------------------
        // ULTRAKILL InputSource action states
        // ------------------------------------------------------------------

        void ApplyActionStates(TasButtons buttons)
        {
            ApplyActionStates(buttons, TasButtons.None);
        }

        void ApplyActionStates(TasButtons buttons, TasButtons suppressEdges)
        {
            object source = _bridge.InputSource;
            if (source == null)
                return;
            if (!ReferenceEquals(source, _cachedSource))
            {
                _cachedSource = source;
                _accessors.Clear();
            }

            for (int i = 0; i < AllActionButtons.Length; i++)
            {
                TasButtons flag = AllActionButtons[i];
                string[] names = ActionNames[flag];
                ActionAccessor accessor;
                if (!_accessors.TryGetValue(names[0], out accessor))
                {
                    accessor = new ActionAccessor { Name = names[0] };
                    _accessors[names[0]] = accessor;
                }

                bool pressed = Has(buttons, flag);
                WriteActionState(accessor, source, names, pressed, Has(suppressEdges, flag));
            }
        }

        void WriteActionState(ActionAccessor accessor, object source, string[] candidateNames, bool pressed, bool suppressEdge)
        {
            try
            {
                if (!accessor.Resolved)
                {
                    for (int n = 0; n < candidateNames.Length; n++)
                    {
                        MemberInfo member = _bridge.FindMember(source.GetType(), candidateNames[n]);
                        if (member == null)
                            continue;
                        object state = _bridge.GetMemberValue(source, member);
                        if (state == null)
                            continue;
                        Type stateType = state.GetType();
                        MemberInfo pressedMember = _bridge.FindMember(stateType, "IsPressed") ??
                                                   _bridge.FindMember(stateType, "Pressed");
                        if (pressedMember == null)
                            continue;
                        accessor.SourceMember = member;
                        accessor.PressedMember = pressedMember;
                        accessor.PerformedMember = _bridge.FindMember(stateType, "PerformedFrame") ??
                                                   _bridge.FindMember(stateType, "PerformedThisFrame") ??
                                                   _bridge.FindMember(stateType, "PressFrame");
                        accessor.Name = candidateNames[n];
                        accessor.Resolved = true;
                        break;
                    }
                }

                if (!accessor.Resolved)
                    return;

                object actionState = _bridge.GetMemberValue(source, accessor.SourceMember);
                if (actionState == null)
                    return;

                _bridge.SetMemberValue(actionState, accessor.PressedMember, pressed);
                if (pressed && !suppressEdge && accessor.PerformedMember != null)
                {
                    int frame = Time.frameCount + _plugin.PerformedFrameOffset.Value;
                    _bridge.SetMemberValue(actionState, accessor.PerformedMember, frame);
                }

                // If InputSource exposes the action as a struct, write the mutated
                // copy back to the source object.
                FieldInfo sourceField = accessor.SourceMember as FieldInfo;
                if (sourceField != null && actionState.GetType().IsValueType)
                    sourceField.SetValue(source, actionState);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("InputSource write failed for " + accessor.Name + ": " + ex.Message);
            }
        }

        bool ReadActionState(string[] candidateNames)
        {
            object source = _bridge.InputSource;
            if (source != null)
            {
                for (int n = 0; n < candidateNames.Length; n++)
                {
                    object state = _bridge.GetMember(source, new[] { candidateNames[n] });
                    if (state == null)
                        continue;
                    object pressed = _bridge.GetMember(state, new[] { "IsPressed", "Pressed" });
                    if (pressed != null)
                        return GameBridge.ToBool(pressed, false);
                }
            }

            // Fallback for installs where InputSource was renamed: read the bound key.
            RefreshBindings(false);
            KeyCode key;
            if (_bindings != null)
            {
                for (int n = 0; n < candidateNames.Length; n++)
                {
                    if (_bindings.TryGetValue(candidateNames[n], out key))
                        return KeyUtil.GameHeld(key);
                }
            }
            return false;
        }

        /// <summary>Read the player's real buttons for recording.</summary>
        public TasButtons ReadPhysicalButtons()
        {
            TasButtons result = TasButtons.None;
            // While the TAS window captures input none of the player's real keys or mouse
            // buttons count (they are released from the devices as well, see InjectNeutral).
            if (!HardwareBlocked)
            {
                for (int i = 0; i < AllActionButtons.Length; i++)
                {
                    TasButtons flag = AllActionButtons[i];
                    if (ReadActionState(ActionNames[flag]))
                        result |= flag;
                }
                // The game's real bindings cover movement and the actions whose names the
                // InputSource wrapper does not expose (feedbacker/knuckleblaster, wheel).
                result |= ReadBoundButtons();
            }

            return result | ReadMenuKeys();
        }

        /// <summary>Menu navigation keys, through the GUI-capture gate (an Escape the GUI uses is not a pause).</summary>
        TasButtons ReadMenuKeys()
        {
            TasButtons result = TasButtons.None;
            if (KeyUtil.GameHeld(_plugin.MenuUpKey.Value)) result |= TasButtons.MenuUp;
            if (KeyUtil.GameHeld(_plugin.MenuDownKey.Value)) result |= TasButtons.MenuDown;
            if (KeyUtil.GameHeld(_plugin.MenuLeftKey.Value)) result |= TasButtons.MenuLeft;
            if (KeyUtil.GameHeld(_plugin.MenuRightKey.Value)) result |= TasButtons.MenuRight;
            if (KeyUtil.GameHeld(_plugin.MenuConfirmKey.Value)) result |= TasButtons.MenuConfirm;
            if (KeyUtil.GameHeld(_plugin.MenuCancelKey.Value)) result |= TasButtons.MenuCancel;
            if (KeyUtil.GameHeld(KeyCode.Escape)) result |= TasButtons.MenuPause;
            return result;
        }

        /// <summary>
        /// Direct legacy-key staging path. This deliberately bypasses InputSource
        /// because staging forces InputSource to neutral while the clock is paused.
        /// </summary>
        public TasButtons ReadPhysicalButtonsLegacy()
        {
            TasButtons result = TasButtons.None;
            if (!HardwareBlocked)
            {
                RefreshBindings(false);
                for (int i = 0; i < AllActionButtons.Length; i++)
                {
                    TasButtons flag = AllActionButtons[i];
                    if (IsBoundHeld(ActionNames[flag], FallbackKey(flag)))
                        result |= flag;
                }
                result |= ReadBoundButtons();
            }

            return result | ReadMenuKeys();
        }

        static readonly TasButtons[] SlotButtons =
        {
            TasButtons.Slot1, TasButtons.Slot2, TasButtons.Slot3,
            TasButtons.Slot4, TasButtons.Slot5, TasButtons.Slot6,
        };

        /// <summary>
        /// The 1-based weapon slot whose real key (the game's own binding, or 1-6) is physically down
        /// right now, or 0. A raw device read like the management hotkeys: it ignores the GUI-capture
        /// gate and the TAS injection, and is for a display-only preview, never for game input.
        /// </summary>
        public int ReadHeldSlotRaw()
        {
            RefreshBindings(false);
            for (int i = 0; i < SlotButtons.Length; i++)
            {
                if (IsBoundHeld(ActionNames[SlotButtons[i]], FallbackKey(SlotButtons[i]), true))
                    return i + 1;
            }
            return 0;
        }

        bool IsBoundHeld(string[] names, KeyCode fallback)
        {
            return IsBoundHeld(names, fallback, false);
        }

        bool IsBoundHeld(string[] names, KeyCode fallback, bool raw)
        {
            if (_bindings != null)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    KeyCode key;
                    if (_bindings.TryGetValue(names[i], out key) && key != KeyCode.None)
                    {
                        if (raw ? KeyUtil.Held(key) : KeyUtil.GameHeld(key))
                            return true;
                    }
                }
            }
            return fallback != KeyCode.None && (raw ? KeyUtil.Held(fallback) : KeyUtil.GameHeld(fallback));
        }

        static KeyCode FallbackKey(TasButtons button)
        {
            switch (button)
            {
                case TasButtons.Forward: return KeyCode.W;
                case TasButtons.Back: return KeyCode.S;
                case TasButtons.Left: return KeyCode.A;
                case TasButtons.Right: return KeyCode.D;
                case TasButtons.Jump: return KeyCode.Space;
                case TasButtons.Slide: return KeyCode.LeftControl;
                case TasButtons.Dash: return KeyCode.LeftShift;
                case TasButtons.Fire1: return KeyCode.Mouse0;
                case TasButtons.Fire2: return KeyCode.Mouse1;
                case TasButtons.Punch: return KeyCode.F;
                case TasButtons.Whiplash: return KeyCode.E;
                case TasButtons.Slot1: return KeyCode.Alpha1;
                case TasButtons.Slot2: return KeyCode.Alpha2;
                case TasButtons.Slot3: return KeyCode.Alpha3;
                case TasButtons.Slot4: return KeyCode.Alpha4;
                case TasButtons.Slot5: return KeyCode.Alpha5;
                case TasButtons.Slot6: return KeyCode.Alpha6;
                case TasButtons.Stats: return KeyCode.Tab;
                default: return KeyCode.None;
            }
        }

        // ------------------------------------------------------------------
        // Bound key + menu device injection
        // ------------------------------------------------------------------

        void RefreshBindings(bool force)
        {
            if (!force && _bindings != null && Time.frameCount - _lastBindingRefreshFrame < 120)
                return;
            _lastBindingRefreshFrame = Time.frameCount;
            Dictionary<string, KeyCode> bindings = _bridge.InputBindings;
            if (bindings != null)
                _bindings = bindings;
        }

        void ApplyDeviceState(TasButtons buttons)
        {
            if (!_plugin.InjectDeviceState.Value)
                return;
            if (Keyboard.current == null && Mouse.current == null)
                return;

            RefreshBindings(false);
            EnsureBound();
            _keyBuffer.Clear();
            bool[] mouseButtons = _mouseButtonScratch;
            Array.Clear(mouseButtons, 0, mouseButtons.Length);
            float wheel = 0f;

            // Every held action is injected as the exact control the game has it bound to
            // (keys, mouse buttons, wheel), read from the game's own InputActionAsset.
            for (int i = 0; i < AllActionButtons.Length; i++)
            {
                TasButtons flag = AllActionButtons[i];
                if (!Has(buttons, flag))
                    continue;
                List<BoundControl> controls = BoundFor(flag);
                if (controls == null)
                    continue;
                for (int c = 0; c < controls.Count; c++)
                {
                    BoundControl control = controls[c];
                    if (control.Key != Key.None)
                        AddKey(control.Key);
                    else if (control.MouseButton >= 0 && control.MouseButton < mouseButtons.Length)
                        mouseButtons[control.MouseButton] = true;
                    else if (control.Scroll != 0)
                        wheel += control.Scroll * 120f;
                }
            }

            if (Has(buttons, TasButtons.MenuUp)) AddKey(_plugin.MenuUpKey.Value);
            if (Has(buttons, TasButtons.MenuDown)) AddKey(_plugin.MenuDownKey.Value);
            if (Has(buttons, TasButtons.MenuLeft)) AddKey(_plugin.MenuLeftKey.Value);
            if (Has(buttons, TasButtons.MenuRight)) AddKey(_plugin.MenuRightKey.Value);
            if (Has(buttons, TasButtons.MenuConfirm)) AddKey(_plugin.MenuConfirmKey.Value);
            if (Has(buttons, TasButtons.MenuCancel)) AddKey(_plugin.MenuCancelKey.Value);
            if (Has(buttons, TasButtons.MenuPause)) AddKey(KeyCode.Escape);
            // An Escape the GUI is not using still reaches the game (pause menu) when no script
            // drives the devices; it is the only real key that does while the GUI has the input.
            if (HardwareBlocked && !_active && KeyUtil.GameHeld(KeyCode.Escape))
                AddKey(KeyCode.Escape);

            // The scripted pointer (menus, title screen). Until a script uses a mouse
            // command the real pointer position is passed through untouched.
            TasMouse pointer = Pointer;
            if (pointer.Active)
            {
                mouseButtons[0] |= pointer.Left;
                mouseButtons[1] |= pointer.Right;
                mouseButtons[2] |= pointer.Middle;
                wheel += pointer.Scroll * 120f;
            }

            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    KeyboardState state = _keyBuffer.Count == 0
                        ? new KeyboardState()
                        : new KeyboardState(_keyBuffer.ToArray());
                    InputSystem.QueueStateEvent(keyboard, state, -1.0);
                }

                Mouse mouse = Mouse.current;
                if (mouse != null)
                {
                    Vector2 position = pointer.Active ? pointer.Position : mouse.position.ReadValue();
                    MouseState state = new MouseState
                    {
                        position = position,
                        delta = Vector2.zero,
                        scroll = new Vector2(0f, wheel)
                    };
                    if (mouseButtons[0]) state.WithButton(MouseButton.Left, true);
                    if (mouseButtons[1]) state.WithButton(MouseButton.Right, true);
                    if (mouseButtons[2]) state.WithButton(MouseButton.Middle, true);
                    if (mouseButtons[3]) state.WithButton(MouseButton.Forward, true);
                    if (mouseButtons[4]) state.WithButton(MouseButton.Back, true);
                    InputSystem.QueueStateEvent(mouse, state, -1.0);
                    // Keep the OS cursor under the scripted pointer so hover and
                    // click targets agree with what is drawn on screen.
                    if (pointer.Active && !_warpFailed)
                    {
                        try { mouse.WarpCursorPosition(position); }
                        catch { _warpFailed = true; }
                    }
                }
                if (!_inputSystemUpdateFailed)
                    InputSystem.Update();
            }
            catch (Exception ex)
            {
                if (!_inputSystemUpdateFailed)
                {
                    _inputSystemUpdateFailed = true;
                    _plugin.LogWarning("InputSystem injection disabled: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        void AddBoundKey(KeyCode key, ref bool mouseLeft, ref bool mouseRight)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6)
            {
                if (key == KeyCode.Mouse0)
                    mouseLeft = true;
                else if (key == KeyCode.Mouse1)
                    mouseRight = true;
                return;
            }
            AddKey(key);
        }

        void AddKey(KeyCode keyCode)
        {
            UnityEngine.InputSystem.Key key = ToInputSystemKey(keyCode);
            if (key != UnityEngine.InputSystem.Key.None && !_keyBuffer.Contains(key))
                _keyBuffer.Add(key);
        }

        internal static UnityEngine.InputSystem.Key ToInputSystemKey(KeyCode code)
        {
            if (code >= KeyCode.A && code <= KeyCode.Z)
                return (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.A + (code - KeyCode.A));
            // Input System orders the digit keys Digit1..Digit9, Digit0 (Digit0 = 50, then
            // LeftShift 51 .. LeftCtrl 55), so Digit0 + n is wrong: Alpha5 became LeftCtrl.
            if (code == KeyCode.Alpha0)
                return UnityEngine.InputSystem.Key.Digit0;
            if (code >= KeyCode.Alpha1 && code <= KeyCode.Alpha9)
                return (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.Digit1 + (code - KeyCode.Alpha1));
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9)
                return (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.Numpad0 + (code - KeyCode.Keypad0));
            if (code >= KeyCode.F1 && code <= KeyCode.F12)
                return (UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.F1 + (code - KeyCode.F1));

            switch (code)
            {
                case KeyCode.Space: return UnityEngine.InputSystem.Key.Space;
                case KeyCode.Insert: return UnityEngine.InputSystem.Key.Insert;
                case KeyCode.Delete: return UnityEngine.InputSystem.Key.Delete;
                case KeyCode.Home: return UnityEngine.InputSystem.Key.Home;
                case KeyCode.End: return UnityEngine.InputSystem.Key.End;
                case KeyCode.PageUp: return UnityEngine.InputSystem.Key.PageUp;
                case KeyCode.PageDown: return UnityEngine.InputSystem.Key.PageDown;
                case KeyCode.Numlock: return UnityEngine.InputSystem.Key.NumLock;
                case KeyCode.CapsLock: return UnityEngine.InputSystem.Key.CapsLock;
                case KeyCode.ScrollLock: return UnityEngine.InputSystem.Key.ScrollLock;
                case KeyCode.Pause: return UnityEngine.InputSystem.Key.Pause;
                case KeyCode.Print: return UnityEngine.InputSystem.Key.PrintScreen;
                case KeyCode.Return: return UnityEngine.InputSystem.Key.Enter;
                case KeyCode.KeypadEnter: return UnityEngine.InputSystem.Key.NumpadEnter;
                case KeyCode.Escape: return UnityEngine.InputSystem.Key.Escape;
                case KeyCode.Tab: return UnityEngine.InputSystem.Key.Tab;
                case KeyCode.Backspace: return UnityEngine.InputSystem.Key.Backspace;
                case KeyCode.UpArrow: return UnityEngine.InputSystem.Key.UpArrow;
                case KeyCode.DownArrow: return UnityEngine.InputSystem.Key.DownArrow;
                case KeyCode.LeftArrow: return UnityEngine.InputSystem.Key.LeftArrow;
                case KeyCode.RightArrow: return UnityEngine.InputSystem.Key.RightArrow;
                case KeyCode.LeftShift: return UnityEngine.InputSystem.Key.LeftShift;
                case KeyCode.RightShift: return UnityEngine.InputSystem.Key.RightShift;
                case KeyCode.LeftControl: return UnityEngine.InputSystem.Key.LeftCtrl;
                case KeyCode.RightControl: return UnityEngine.InputSystem.Key.RightCtrl;
                case KeyCode.LeftAlt: return UnityEngine.InputSystem.Key.LeftAlt;
                case KeyCode.RightAlt: return UnityEngine.InputSystem.Key.RightAlt;
                case KeyCode.Mouse0: return UnityEngine.InputSystem.Key.None;
                case KeyCode.Mouse1: return UnityEngine.InputSystem.Key.None;
                case KeyCode.Mouse2: return UnityEngine.InputSystem.Key.None;
                case KeyCode.Comma: return UnityEngine.InputSystem.Key.Comma;
                case KeyCode.Period: return UnityEngine.InputSystem.Key.Period;
                case KeyCode.Slash: return UnityEngine.InputSystem.Key.Slash;
                case KeyCode.Semicolon: return UnityEngine.InputSystem.Key.Semicolon;
                case KeyCode.Quote: return UnityEngine.InputSystem.Key.Quote;
                case KeyCode.LeftBracket: return UnityEngine.InputSystem.Key.LeftBracket;
                case KeyCode.RightBracket: return UnityEngine.InputSystem.Key.RightBracket;
                case KeyCode.BackQuote: return UnityEngine.InputSystem.Key.Backquote;
                case KeyCode.Minus: return UnityEngine.InputSystem.Key.Minus;
                case KeyCode.Equals: return UnityEngine.InputSystem.Key.Equals;
                default: return UnityEngine.InputSystem.Key.None;
            }
        }

        void AddKey(UnityEngine.InputSystem.Key key)
        {
            if (key != UnityEngine.InputSystem.Key.None && !_keyBuffer.Contains(key))
                _keyBuffer.Add(key);
        }

        // ------------------------------------------------------------------
        // Harmony patches
        // ------------------------------------------------------------------

        void TryPatchGameHooks()
        {
            if (Time.frameCount - _lastPatchAttemptFrame < 60)
                return;
            _lastPatchAttemptFrame = Time.frameCount;

            if (!_cameraPatched)
                TryPatchCamera();
            if (!_legacyUiPatched)
                TryPatchLegacyUi();
        }

        void TryPatchCamera()
        {
            // The patch is always installed when available: free-look also needs
            // to replace CameraController.Update, even if scripted angle
            // enforcement was disabled in config.
            if (_cameraPatched || _cameraAttempted)
                return;
            Type type = _bridge.CameraControllerType;
            if (type == null)
                return;
            _cameraAttempted = true;
            try
            {
                MethodInfo prefix = typeof(InputPatcher).GetMethod("CameraControllerPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                // Plain reflection probes: AccessTools logs a HarmonyX warning for
                // every optional method that a game version does not have, and
                // ULTRAKILL's CameraController only implements LateUpdate.
                MethodInfo lateUpdate = FindInstanceMethod(type, "LateUpdate");
                MethodInfo update = FindInstanceMethod(type, "Update");
                bool patched = false;
                if (lateUpdate != null)
                {
                    _harmony.Patch(lateUpdate, prefix: new HarmonyMethod(prefix));
                    patched = true;
                }
                if (update != null && update != lateUpdate)
                {
                    _harmony.Patch(update, prefix: new HarmonyMethod(prefix));
                    patched = true;
                }
                if (patched)
                {
                    _cameraPatched = true;
                    _plugin.LogInfo("CameraController angle enforcement patched.");
                }
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("CameraController patch failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolve a parameterless instance method without HarmonyX's warning.
        /// ULTRAKILL's CameraController has LateUpdate only (no Update), and an
        /// AccessTools.Method probe for a missing optional hook logs a scary but
        /// harmless warning at startup.
        /// </summary>
        static MethodInfo FindInstanceMethod(Type type, string name)
        {
            try
            {
                return type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        void TryPatchLegacyUi()
        {
            if (_legacyUiPatched || _legacyUiAttempted)
                return;
            _legacyUiAttempted = true;
            try
            {
                MethodInfo axisRaw = FindBaseInputMethod("GetAxisRaw");
                MethodInfo axis = FindBaseInputMethod("GetAxis");
                MethodInfo buttonDown = FindBaseInputMethod("GetButtonDown");
                MethodInfo button = FindBaseInputMethod("GetButton");
                MethodInfo buttonUp = FindBaseInputMethod("GetButtonUp");
                if (axisRaw == null || buttonDown == null)
                    return;

                _harmony.Patch(axisRaw, prefix: new HarmonyMethod(
                    typeof(InputPatcher).GetMethod("BaseInputAxisRawPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                if (axis != null && axis != axisRaw)
                    _harmony.Patch(axis, prefix: new HarmonyMethod(
                        typeof(InputPatcher).GetMethod("BaseInputAxisPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(buttonDown, prefix: new HarmonyMethod(
                    typeof(InputPatcher).GetMethod("BaseInputButtonDownPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                if (button != null)
                    _harmony.Patch(button, prefix: new HarmonyMethod(
                        typeof(InputPatcher).GetMethod("BaseInputButtonPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                if (buttonUp != null)
                    _harmony.Patch(buttonUp, prefix: new HarmonyMethod(
                        typeof(InputPatcher).GetMethod("BaseInputButtonUpPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                _legacyUiPatched = true;
                _plugin.LogInfo("Legacy uGUI BaseInput patched for menu injection.");
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("BaseInput patch failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Raw mouse look while the TAS clock is frozen. Time.deltaTime is zero at
        /// this point, so the original CameraController would discard the mouse;
        /// this path reads the raw delta and writes the logical pitch/yaw fields.
        /// </summary>
        void ApplyFreeLook()
        {
            float mouseX = 0f;
            float mouseY = 0f;
            try
            {
                mouseX = Input.GetAxisRaw("Mouse X");
                mouseY = Input.GetAxisRaw("Mouse Y");
            }
            catch (Exception)
            {
                // Legacy input can be switched off entirely; the raw device delta
                // below is the only mouse source left in that configuration.
            }
            if (Mathf.Abs(mouseX) < 0.00001f && Mathf.Abs(mouseY) < 0.00001f &&
                UnityEngine.InputSystem.Mouse.current != null)
            {
                Vector2 delta = UnityEngine.InputSystem.Mouse.current.delta.ReadValue();
                mouseX = delta.x;
                mouseY = delta.y;
            }

            if (HardwareBlocked)
            {
                // The pointer belongs to the TAS window: it must not turn the camera.
                mouseX = 0f;
                mouseY = 0f;
            }

            float sensitivity = Mathf.Max(0.01f, _plugin.FreeLookSensitivity.Value);
            _pitch = Mathf.Clamp(_pitch - mouseY * sensitivity, -89.9f, 89.9f);
            _yaw += mouseX * sensitivity;
            _stagedPitch = _pitch;
            _stagedYaw = _yaw;
            _bridge.SetCameraAngles(_pitch, _yaw);
            _bridge.ApplyCameraTransform(_pitch, _yaw);
        }

        void EnforceCameraAngles()
        {
            if (_active && _hasAngles)
            {
                // A script owns the view: drop the menu hold so it is read again when the script stops.
                _holdAngles = false;
                if (_plugin.EnforceCameraAngles.Value)
                    _bridge.SetCameraAngles(_pitch, _yaw);
                return;
            }

            // No script owns the view and the TAS window is open: keep the camera where it was when
            // the window opened, so the real mouse cannot turn it.
            if (!HardwareBlocked)
                return;
            if (!_holdAngles)
                OnGuiCaptureBegan();
            if (_holdAngles)
                _bridge.SetCameraAngles(_holdPitch, _holdYaw);
        }

        /// <summary>
        /// A fully frozen frame (SimulationFreeze) runs no CameraController.LateUpdate, so
        /// the paused freecam and free-look are driven from here instead, once per frame,
        /// exactly as CameraControllerPrefix would have.
        /// </summary>
        public void UpdateFrozenCamera()
        {
            if (_freecam)
            {
                ApplyFreecam();
                return;
            }
            if (_freeLook && Cursor.lockState == CursorLockMode.Locked &&
                _lastFreeLookFrame != SimulationFreeze.FrameCount)
            {
                _lastFreeLookFrame = SimulationFreeze.FrameCount;
                ApplyFreeLook();
            }
        }

        static bool CameraControllerPrefix()
        {
            InputPatcher self = Instance;
            if (self != null)
            {
                if (self._freecam)
                {
                    self.ApplyFreecam();
                    // A script drives V1's view through the game's own CameraController, so the shot
                    // origin stays on the TAS camera. Without one V1's view is frozen, so the real
                    // mouse only moves the freecam.
                    if (!self._active)
                        return false;
                    self.EnforceCameraAngles();
                    return true;
                }
                if (self._freeLook && Cursor.lockState == CursorLockMode.Locked)
                {
                    if (self._lastFreeLookFrame != SimulationFreeze.FrameCount)
                    {
                        self._lastFreeLookFrame = SimulationFreeze.FrameCount;
                        self.ApplyFreeLook();
                    }
                    return false;
                }
                self.EnforceCameraAngles();
            }
            return true;
        }

        bool BlockLegacyInput()
        {
            return _active || HardwareBlocked || _staging;
        }

        static bool BaseInputAxisRawPrefix(string axisName, ref float __result)
        {
            InputPatcher self = Instance;
            if (self == null || !self.BlockLegacyInput())
                return true;
            string name = axisName ?? string.Empty;
            if (name == "Horizontal")
            {
                __result = (Has(self._buttons, TasButtons.MenuRight) ? 1f : 0f) -
                           (Has(self._buttons, TasButtons.MenuLeft) ? 1f : 0f);
                return false;
            }
            if (name == "Vertical")
            {
                __result = (Has(self._buttons, TasButtons.MenuUp) ? 1f : 0f) -
                           (Has(self._buttons, TasButtons.MenuDown) ? 1f : 0f);
                return false;
            }
            return true;
        }

        static bool BaseInputAxisPrefix(string axisName, ref float __result)
        {
            return BaseInputAxisRawPrefix(axisName, ref __result);
        }

        static bool BaseInputButtonDownPrefix(string buttonName, ref bool __result)
        {
            InputPatcher self = Instance;
            if (self == null || !self.BlockLegacyInput())
                return true;
            string name = buttonName ?? string.Empty;
            if (IsSubmit(name))
            {
                __result = self._confirmDown;
                return false;
            }
            if (IsCancel(name))
            {
                __result = self._cancelDown || self._pauseDown;
                return false;
            }
            __result = false;
            return false;
        }

        static bool BaseInputButtonPrefix(string buttonName, ref bool __result)
        {
            InputPatcher self = Instance;
            if (self == null || !self.BlockLegacyInput())
                return true;
            string name = buttonName ?? string.Empty;
            if (IsSubmit(name))
            {
                __result = Has(self._buttons, TasButtons.MenuConfirm);
                return false;
            }
            if (IsCancel(name))
            {
                __result = Has(self._buttons, TasButtons.MenuCancel) || Has(self._buttons, TasButtons.MenuPause);
                return false;
            }
            __result = false;
            return false;
        }

        static bool BaseInputButtonUpPrefix(string buttonName, ref bool __result)
        {
            InputPatcher self = Instance;
            if (self == null || !self.BlockLegacyInput())
                return true;
            string name = buttonName ?? string.Empty;
            if (IsSubmit(name))
            {
                __result = !Has(self._buttons, TasButtons.MenuConfirm) && self._menuConfirmWasDown;
                return false;
            }
            if (IsCancel(name))
            {
                __result = !Has(self._buttons, TasButtons.MenuCancel) && self._menuCancelWasDown;
                return false;
            }
            __result = false;
            return false;
        }

        /// <summary>Optional uGUI hook lookup that stays silent when a method does not exist.</summary>
        static MethodInfo FindBaseInputMethod(string name)
        {
            try
            {
                return typeof(BaseInput).GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        static bool IsSubmit(string name)
        {
            return name == "Submit" || name == "Confirm" || name == "MenuSubmit";
        }

        static bool IsCancel(string name)
        {
            return name == "Cancel" || name == "Back" || name == "MenuCancel";
        }

        public string Describe()
        {
            int resolved = 0;
            foreach (KeyValuePair<string, ActionAccessor> pair in _accessors)
            {
                if (pair.Value.Resolved)
                    resolved++;
            }
            return "TAS input: " + (_active ? "active" : "idle") +
                   ", actions resolved " + resolved + "/" + AllActionButtons.Length +
                   ", InputSystem " + (Keyboard.current != null ? "keyboard ok" : "no keyboard") +
                   ", camera patch " + (_cameraPatched ? "yes" : "no") +
                   ", legacy UI patch " + (_legacyUiPatched ? "yes" : "no");
        }
    }
}
