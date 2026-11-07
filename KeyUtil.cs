using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UltrakillTAS
{
    /// <summary>
    /// Keyboard reads that work whether the player uses the legacy Input Manager,
    /// the Input System package, or both.
    ///
    /// ULTRAKILL is built with the Input System package. If the player is set to
    /// "Input System Package (New)" only, every UnityEngine.Input call throws
    /// InvalidOperationException. That exception would abort Plugin.Update before
    /// the hotkeys run, which makes the TAS window impossible to open even though
    /// BepInEx and the plugin loaded correctly. Reading Keyboard.current directly
    /// keeps the hotkeys working in that configuration.
    /// </summary>
    internal static class KeyUtil
    {
        static bool _legacyUnavailable;

        public static bool LegacyInputWorks { get { return !_legacyUnavailable; } }

        public static bool NewKeyboardAvailable { get { return Keyboard.current != null; } }

        // ------------------------------------------------------------------
        // The single "GUI captures input" gate
        // ------------------------------------------------------------------
        //
        // Held/Down below are raw device reads: the management hotkeys (open GUI, play/pause...)
        // use them and must keep working while the TAS window is open. Everything that feeds the
        // GAME (recorded/staged buttons, freecam movement, menu keys) uses GameHeld instead, and
        // the injector releases the real devices while the gate is up.

        /// <summary>True while the TAS window has the pointer or the keyboard focus. Set once per frame by Plugin.Tick.</summary>
        public static bool GuiCapturesInput { get; private set; }

        static bool _guiOwnsEscape;
        static bool _escapeLatch;

        /// <summary>
        /// Called once per frame. <paramref name="guiOwnsEscape"/> is true while a popup, the Find / Go to
        /// bar, a rebind or a text field can use Escape; that press must not also open the pause menu,
        /// including the frames after the GUI has already closed the popup but the key is still down.
        /// </summary>
        public static void SetGuiCapture(bool captures, bool guiOwnsEscape)
        {
            bool escapeHeld = captures && Held(KeyCode.Escape);
            if (!captures)
                _escapeLatch = false;
            else if (guiOwnsEscape && escapeHeld)
                _escapeLatch = true;
            else if (!escapeHeld)
                _escapeLatch = false;

            GuiCapturesInput = captures;
            _guiOwnsEscape = captures && guiOwnsEscape;
        }

        /// <summary>True when a real press of this key/mouse button must not reach the game right now.</summary>
        public static bool IsBlocked(KeyCode key)
        {
            if (!GuiCapturesInput)
                return false;
            if (key == KeyCode.Escape)
                return _guiOwnsEscape || _escapeLatch;
            return true;
        }

        /// <summary>Held, for the game-input paths: a key the GUI has captured reads as not pressed.</summary>
        public static bool GameHeld(KeyCode key)
        {
            return !IsBlocked(key) && Held(key);
        }

        /// <summary>
        /// Held, for paths that must see only the user's real keys (freecam). The legacy Input Manager
        /// is read straight from the OS and never carries the script's keys: InputPatcher injects those
        /// into the Input System devices only. The Input System is therefore a fallback solely when
        /// legacy input is unavailable AND the caller says no scripted device state is queued
        /// (<paramref name="allowInputSystem"/>), because Keyboard.current then holds the script's keys.
        /// </summary>
        public static bool UserHeld(KeyCode key, bool allowInputSystem)
        {
            if (key == KeyCode.None || IsBlocked(key))
                return false;

            if (!_legacyUnavailable)
            {
                try
                {
                    return Input.GetKey(key);
                }
                catch (Exception)
                {
                    _legacyUnavailable = true;
                }
            }

            return allowInputSystem && HeldFromInputSystem(key);
        }

        public static bool Held(KeyCode key)
        {
            if (key == KeyCode.None)
                return false;

            if (!_legacyUnavailable)
            {
                try
                {
                    if (Input.GetKey(key))
                        return true;
                }
                catch (Exception)
                {
                    _legacyUnavailable = true;
                }
            }

            return HeldFromInputSystem(key);
        }

        public static bool Down(KeyCode key)
        {
            if (key == KeyCode.None)
                return false;

            if (!_legacyUnavailable)
            {
                try
                {
                    if (Input.GetKeyDown(key))
                        return true;
                }
                catch (Exception)
                {
                    _legacyUnavailable = true;
                }
            }

            return DownFromInputSystem(key);
        }

        static bool HeldFromInputSystem(KeyCode key)
        {
            if (IsMouseButton(key))
            {
                UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
                return mouse != null && MouseButtonControl(mouse, key).isPressed;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;
            Key mapped = InputPatcher.ToInputSystemKey(key);
            return mapped != Key.None && keyboard[mapped].isPressed;
        }

        static bool IsMouseButton(KeyCode key)
        {
            return key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
        }

        /// <summary>
        /// Unity's KeyCode.Mouse3 is the side button labelled "mouse4" (back), and Mouse4 is
        /// "mouse5" (forward). Mouse0-2 are left, right and middle.
        /// </summary>
        static UnityEngine.InputSystem.Controls.ButtonControl MouseButtonControl(UnityEngine.InputSystem.Mouse mouse, KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Mouse0: return mouse.leftButton;
                case KeyCode.Mouse1: return mouse.rightButton;
                case KeyCode.Mouse2: return mouse.middleButton;
                case KeyCode.Mouse3: return mouse.backButton;
                case KeyCode.Mouse4: return mouse.forwardButton;
                default: return null;
            }
        }

        static bool DownFromInputSystem(KeyCode key)
        {
            if (IsMouseButton(key))
            {
                UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null)
                    return false;
                UnityEngine.InputSystem.Controls.ButtonControl button = MouseButtonControl(mouse, key);
                return button != null && button.wasPressedThisFrame;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;
            Key mapped = InputPatcher.ToInputSystemKey(key);
            return mapped != Key.None && keyboard[mapped].wasPressedThisFrame;
        }
    }
}
