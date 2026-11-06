using System;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// The small PAUSED text above the crosshair. It is drawn from ULTRAKILL's own glyph atlas
    /// (the same VCR OSD Mono the speedometer uses) and falls back to a system font until the
    /// game's font has been found, so it never shows up missing.
    /// </summary>
    public sealed partial class GuiManager
    {
        GUIStyle _pausedStyle;

        /// <summary>True while the TAS clock is held: paused playback, a paused recording or a paused standalone clock.</summary>
        bool IsClockPaused()
        {
            switch (_engine.Mode)
            {
                case EngineMode.Paused:
                    return true;
                case EngineMode.Recording:
                case EngineMode.Standalone:
                    return _time.Paused;
                default:
                    return false;
            }
        }

        void DrawPausedLabel()
        {
            if (!_plugin.PausedLabelEnabled.Value || _plugin.CleanRecordingActive)
                return;
            if (Event.current.type != EventType.Repaint || !IsClockPaused())
                return;
            EnsureHudStyles();

            float size = Mathf.Clamp(_plugin.PausedLabelSize.Value, 12, 96);
            float centreX = Screen.width * 0.5f;
            float centreY = Screen.height * 0.5f - Mathf.Clamp(_plugin.PausedLabelOffsetY.Value, 0, 600);

            // A slow breathing pulse keeps it noticeable without being loud.
            float pulse = 0.82f + 0.18f * Mathf.Sin(TimeController.RealTime * 3.2f);
            Color color = new Color(1f, 0.88f, 0.62f, pulse);

            if (DrawGameFontText(centreX, centreY, "PAUSED", size, color, true))
                return;

            if (_pausedStyle == null)
            {
                _pausedStyle = new GUIStyle(_speedoStyle);
                _pausedStyle.alignment = TextAnchor.MiddleCenter;
                _pausedStyle.fontStyle = FontStyle.Bold;
            }
            _pausedStyle.font = GameFont();
            _pausedStyle.fontSize = Mathf.RoundToInt(size);
            Rect rect = new Rect(centreX - 200f, centreY - size * 0.7f, 400f, size * 1.4f);
            DrawOutlined(rect, "PAUSED", _pausedStyle, color, pulse, size);
        }
    }
}
