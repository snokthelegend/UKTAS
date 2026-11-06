using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace UltrakillTAS
{
    /// <summary>
    /// The pointer a script drives while it runs: position, buttons and wheel. Used to TAS
    /// the title screen and every in-game menu. Positions are stored in Unity screen
    /// pixels (origin bottom-left); the script commands convert from the friendlier
    /// top-left pixel / fraction forms.
    /// </summary>
    public sealed class TasMouse
    {
        /// <summary>True once a script has used any mouse command; until then the real mouse is left alone.</summary>
        public bool Active;
        public Vector2 Position;
        public bool Left;
        public bool Right;
        public bool Middle;
        /// <summary>Wheel notches for the next injected state only (positive = up).</summary>
        public float Scroll;

        // Smooth pointer travel set up by the glide commands.
        public int GlideTicksLeft;
        public Vector2 GlideTarget;

        public void Reset()
        {
            Active = false;
            Left = Right = Middle = false;
            Scroll = 0f;
            GlideTicksLeft = 0;
        }

        /// <summary>Advance an active glide by one tick; the last tick lands exactly on the target.</summary>
        public void StepGlide()
        {
            if (GlideTicksLeft <= 0)
                return;
            Position = Vector2.Lerp(Position, GlideTarget, 1f / GlideTicksLeft);
            GlideTicksLeft--;
            if (GlideTicksLeft == 0)
                Position = GlideTarget;
        }
    }

    /// <summary>One physical control an ULTRAKILL action is bound to.</summary>
    internal sealed class BoundControl
    {
        public Key Key = Key.None;
        /// <summary>0 left, 1 right, 2 middle, 3 forward, 4 back, -1 none.</summary>
        public int MouseButton = -1;
        /// <summary>+1 wheel up, -1 wheel down, 0 none.</summary>
        public int Scroll;
    }

    public sealed partial class InputPatcher
    {
        /// <summary>The scripted pointer. Written by the engine, injected every tick.</summary>
        public readonly TasMouse Pointer = new TasMouse();

        /// <summary>Everything currently held on the real or injected devices, for the HUD.</summary>
        public TasButtons LiveButtons { get; private set; }
        public Vector2 LiveMousePosition { get; private set; }
        public bool LiveMouseLeft { get; private set; }
        public bool LiveMouseRight { get; private set; }
        public bool LiveMouseMiddle { get; private set; }

        /// <summary>TasButtons flag, action path in the game's InputActionAsset, composite part (or null).</summary>
        struct ActionBinding
        {
            public TasButtons Flag;
            public string Action;
            public string Part;

            public ActionBinding(TasButtons flag, string action, string part)
            {
                Flag = flag;
                Action = action;
                Part = part;
            }
        }

        // The complete ULTRAKILL action list (InputActions asset: Movement, Fist, Weapon, HUD).
        static readonly ActionBinding[] ActionBindings =
        {
            new ActionBinding(TasButtons.Forward, "Movement/Move", "up"),
            new ActionBinding(TasButtons.Back, "Movement/Move", "down"),
            new ActionBinding(TasButtons.Left, "Movement/Move", "left"),
            new ActionBinding(TasButtons.Right, "Movement/Move", "right"),
            new ActionBinding(TasButtons.Jump, "Movement/Jump", null),
            new ActionBinding(TasButtons.Slide, "Movement/Slide", null),
            new ActionBinding(TasButtons.Dash, "Movement/Dodge", null),
            new ActionBinding(TasButtons.Fire1, "Weapon/PrimaryFire", null),
            new ActionBinding(TasButtons.Fire2, "Weapon/SecondaryFire", null),
            new ActionBinding(TasButtons.Punch, "Fist/Punch", null),
            new ActionBinding(TasButtons.Whiplash, "Fist/Hook", null),
            new ActionBinding(TasButtons.Slot1, "Weapon/Revolver", null),
            new ActionBinding(TasButtons.Slot2, "Weapon/Shotgun", null),
            new ActionBinding(TasButtons.Slot3, "Weapon/Nailgun", null),
            new ActionBinding(TasButtons.Slot4, "Weapon/Railcannon", null),
            // The game's action is named "Rocket Launcher" (with a space); "RocketLauncher" is not found.
            new ActionBinding(TasButtons.Slot5, "Weapon/Rocket Launcher", null),
            // The game's action is named "Spawner Arm" (with a space), like "Rocket Launcher".
            new ActionBinding(TasButtons.Slot6, "Weapon/Spawner Arm", null),
            new ActionBinding(TasButtons.FistChange, "Fist/ChangeFist", null),
            new ActionBinding(TasButtons.FistFeedbacker, "Fist/PunchFeedbacker", null),
            new ActionBinding(TasButtons.FistKnuckleblaster, "Fist/PunchKnuckleblaster", null),
            new ActionBinding(TasButtons.WeaponNext, "Weapon/NextWeapon", null),
            new ActionBinding(TasButtons.WeaponPrev, "Weapon/PreviousWeapon", null),
            new ActionBinding(TasButtons.WeaponLast, "Weapon/LastUsedWeapon", null),
            new ActionBinding(TasButtons.VariationNext, "Weapon/NextVariation", null),
            new ActionBinding(TasButtons.VariationPrev, "Weapon/PreviousVariation", null),
            new ActionBinding(TasButtons.Variant1, "Weapon/VariationSlot1", null),
            new ActionBinding(TasButtons.Variant2, "Weapon/VariationSlot2", null),
            new ActionBinding(TasButtons.Variant3, "Weapon/VariationSlot3", null),
            new ActionBinding(TasButtons.Stats, "HUD/Stats", null)
        };

        readonly Dictionary<TasButtons, List<BoundControl>> _bound = new Dictionary<TasButtons, List<BoundControl>>();
        bool _boundFromAsset;
        int _boundBuildFrame = -100000;

        /// <summary>
        /// Resolve every action to the keys/mouse buttons it is actually bound to by reading
        /// the game's InputActionAsset. This follows rebinds and needs no name guessing; the
        /// hard-coded defaults are used only for actions the asset does not expose.
        /// </summary>
        void EnsureBound()
        {
            int age = Time.frameCount - _boundBuildFrame;
            // Retry quickly until the asset exists, then refresh now and then for rebinds.
            if (age < (_boundFromAsset ? 600 : 60) && _bound.Count > 0)
                return;
            _boundBuildFrame = Time.frameCount;

            InputActionAsset asset = FindActionAsset();
            _bound.Clear();
            _boundFromAsset = asset != null;

            for (int i = 0; i < ActionBindings.Length; i++)
            {
                ActionBinding entry = ActionBindings[i];
                List<BoundControl> controls;
                if (!_bound.TryGetValue(entry.Flag, out controls))
                {
                    controls = new List<BoundControl>(2);
                    _bound[entry.Flag] = controls;
                }

                if (asset != null)
                    CollectBindings(asset, entry, controls);

                if (controls.Count == 0)
                    AddFallback(entry.Flag, controls);
            }
        }

        InputActionAsset FindActionAsset()
        {
            try
            {
                object source = _bridge.InputSource;
                if (source != null)
                {
                    object actions = _bridge.GetMember(source, new[] { "Actions" });
                    InputActionAsset asset = _bridge.GetMember(actions, new[] { "asset", "<asset>k__BackingField" }) as InputActionAsset;
                    if (asset != null)
                        return asset;
                }
                object manager = _bridge.InputManager;
                if (manager != null)
                    return _bridge.GetMember(manager, new[] { "defaultActions" }) as InputActionAsset;
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Could not read the input action asset: " + ex.Message);
            }
            return null;
        }

        static void CollectBindings(InputActionAsset asset, ActionBinding entry, List<BoundControl> into)
        {
            InputAction action = asset.FindAction(entry.Action, false);
            if (action == null)
                return;
            ReadOnlyArray<InputBinding> bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding binding = bindings[i];
                if (binding.isComposite)
                    continue;
                if (entry.Part != null)
                {
                    if (!binding.isPartOfComposite ||
                        !string.Equals(binding.name, entry.Part, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (binding.isPartOfComposite)
                {
                    continue;
                }

                BoundControl control = ParseControlPath(binding.effectivePath);
                if (control != null)
                    into.Add(control);
            }
        }

        /// <summary>Only keyboard and mouse controls are injectable; gamepad paths are skipped.</summary>
        static BoundControl ParseControlPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            const string keyboard = "<Keyboard>/";
            const string mouse = "<Mouse>/";
            if (path.StartsWith(keyboard, StringComparison.OrdinalIgnoreCase))
            {
                Key key = ResolveKey(path.Substring(keyboard.Length));
                return key == Key.None ? null : new BoundControl { Key = key };
            }
            if (path.StartsWith(mouse, StringComparison.OrdinalIgnoreCase))
            {
                string name = path.Substring(mouse.Length).ToLowerInvariant();
                switch (name)
                {
                    case "leftbutton": return new BoundControl { MouseButton = 0 };
                    case "rightbutton": return new BoundControl { MouseButton = 1 };
                    case "middlebutton": return new BoundControl { MouseButton = 2 };
                    case "forwardbutton": return new BoundControl { MouseButton = 3 };
                    case "backbutton": return new BoundControl { MouseButton = 4 };
                    case "scroll/up": return new BoundControl { Scroll = 1 };
                    case "scroll/down": return new BoundControl { Scroll = -1 };
                }
            }
            return null;
        }

        static Key ResolveKey(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "shift": return Key.LeftShift;
                case "ctrl": return Key.LeftCtrl;
                case "alt": return Key.LeftAlt;
            }
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return Key.None;
            try
            {
                KeyControl control = keyboard.TryGetChildControl<KeyControl>(name);
                return control != null ? control.keyCode : Key.None;
            }
            catch
            {
                return Key.None;
            }
        }

        void AddFallback(TasButtons flag, List<BoundControl> into)
        {
            KeyCode code = FallbackKey(flag);
            if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6)
            {
                into.Add(new BoundControl { MouseButton = code - KeyCode.Mouse0 });
                return;
            }
            Key key = ToInputSystemKey(code);
            if (key != Key.None)
                into.Add(new BoundControl { Key = key });
        }

        List<BoundControl> BoundFor(TasButtons flag)
        {
            List<BoundControl> list;
            return _bound.TryGetValue(flag, out list) ? list : null;
        }

        static bool IsMouseButtonDown(Mouse mouse, int button)
        {
            switch (button)
            {
                case 0: return mouse.leftButton.isPressed;
                case 1: return mouse.rightButton.isPressed;
                case 2: return mouse.middleButton.isPressed;
                case 3: return mouse.forwardButton.isPressed;
                case 4: return mouse.backButton.isPressed;
                default: return false;
            }
        }

        /// <summary>Reads which actions are held on the Input System devices (physical or injected).</summary>
        TasButtons ReadBoundButtons()
        {
            EnsureBound();
            TasButtons result = TasButtons.None;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse_Current();
            float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;

            for (int i = 0; i < ActionBindings.Length; i++)
            {
                TasButtons flag = ActionBindings[i].Flag;
                if ((result & flag) != 0)
                    continue;
                List<BoundControl> controls = BoundFor(flag);
                if (controls == null)
                    continue;
                for (int c = 0; c < controls.Count; c++)
                {
                    BoundControl control = controls[c];
                    bool held = false;
                    if (control.Key != Key.None && keyboard != null)
                        held = keyboard[control.Key].isPressed;
                    else if (control.MouseButton >= 0 && mouse != null)
                        held = IsMouseButtonDown(mouse, control.MouseButton);
                    else if (control.Scroll > 0)
                        held = wheel > 0.01f;
                    else if (control.Scroll < 0)
                        held = wheel < -0.01f;
                    if (held)
                    {
                        result |= flag;
                        break;
                    }
                }
            }
            return result;
        }

        static Mouse Mouse_Current()
        {
            return UnityEngine.InputSystem.Mouse.current;
        }

        /// <summary>Refresh the HUD snapshot of live inputs. Cheap: a few dozen device reads, no allocation.</summary>
        public void UpdateLive()
        {
            TasButtons held = ReadBoundButtons();
            if (KeyUtil.Held(_plugin.MenuUpKey.Value)) held |= TasButtons.MenuUp;
            if (KeyUtil.Held(_plugin.MenuDownKey.Value)) held |= TasButtons.MenuDown;
            if (KeyUtil.Held(_plugin.MenuLeftKey.Value)) held |= TasButtons.MenuLeft;
            if (KeyUtil.Held(_plugin.MenuRightKey.Value)) held |= TasButtons.MenuRight;
            if (KeyUtil.Held(_plugin.MenuConfirmKey.Value)) held |= TasButtons.MenuConfirm;
            if (KeyUtil.Held(_plugin.MenuCancelKey.Value)) held |= TasButtons.MenuCancel;
            if (KeyUtil.Held(KeyCode.Escape)) held |= TasButtons.MenuPause;
            LiveButtons = held;

            Mouse mouse = Mouse_Current();
            if (mouse != null)
            {
                LiveMousePosition = mouse.position.ReadValue();
                LiveMouseLeft = mouse.leftButton.isPressed;
                LiveMouseRight = mouse.rightButton.isPressed;
                LiveMouseMiddle = mouse.middleButton.isPressed;
            }
        }

        /// <summary>Description of what each action resolved to, for the Diagnostics tab.</summary>
        public string DescribeBindings()
        {
            EnsureBound();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            sb.Append("Input bindings (").Append(_boundFromAsset ? "from the game's action asset" : "defaults, asset not found").Append("):");
            for (int i = 0; i < ActionBindings.Length; i++)
            {
                List<BoundControl> controls = BoundFor(ActionBindings[i].Flag);
                sb.Append("\n  ").Append(ActionBindings[i].Flag).Append(" <- ");
                if (controls == null || controls.Count == 0)
                {
                    sb.Append("(none)");
                    continue;
                }
                for (int c = 0; c < controls.Count; c++)
                {
                    if (c > 0)
                        sb.Append(", ");
                    BoundControl control = controls[c];
                    if (control.Key != Key.None)
                        sb.Append(control.Key);
                    else if (control.MouseButton >= 0)
                        sb.Append("Mouse").Append(control.MouseButton);
                    else
                        sb.Append(control.Scroll > 0 ? "WheelUp" : "WheelDown");
                }
            }
            return sb.ToString();
        }
    }
}
