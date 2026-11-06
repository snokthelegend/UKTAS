using System;
using System.IO;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Look and feel of the TAS window: one dark skin built in code (rounded boxes, an orange
    /// accent), card/section helpers so every tab groups its controls the same way, hover
    /// tooltips, and the "are you sure" dialog. IMGUI has no theme files, so the textures are
    /// painted at start-up and live as long as the game does.
    /// </summary>
    public sealed partial class GuiManager
    {
        // ------------------------------------------------------------------
        // Palette
        // ------------------------------------------------------------------

        static readonly Color ColAccent = new Color32(255, 140, 60, 255);
        static readonly Color ColAccentHover = new Color32(255, 163, 98, 255);
        static readonly Color ColAccentPressed = new Color32(214, 108, 36, 255);
        static readonly Color ColWindow = new Color32(22, 25, 31, 250);
        static readonly Color ColHeader = new Color32(31, 35, 45, 255);
        static readonly Color ColWindowBorder = new Color32(58, 66, 84, 255);
        static readonly Color ColCard = new Color32(30, 34, 43, 255);
        static readonly Color ColCardBorder = new Color32(46, 53, 67, 255);
        static readonly Color ColButton = new Color32(46, 53, 68, 255);
        static readonly Color ColButtonHover = new Color32(60, 69, 88, 255);
        static readonly Color ColButtonPressed = new Color32(76, 88, 112, 255);
        static readonly Color ColField = new Color32(17, 20, 26, 255);
        static readonly Color ColFieldBorder = new Color32(55, 63, 80, 255);
        static readonly Color ColDanger = new Color32(176, 58, 58, 255);
        static readonly Color ColDangerHover = new Color32(206, 76, 76, 255);
        static readonly Color ColDangerPressed = new Color32(146, 44, 44, 255);
        static readonly Color ColText = new Color32(226, 231, 240, 255);
        static readonly Color ColMuted = new Color32(146, 156, 175, 255);
        static readonly Color ColError = new Color32(255, 125, 118, 255);
        static readonly Color ColGood = new Color32(130, 214, 140, 255);

        // ------------------------------------------------------------------
        // Generated textures and styles
        // ------------------------------------------------------------------

        GUISkin _skin;
        GUISkin _skinBase;
        Texture2D _texWindow, _texCard, _texButton, _texButtonHover, _texButtonPressed, _texPrimary,
                  _texPrimaryHover, _texPrimaryPressed, _texDanger, _texDangerHover, _texDangerPressed,
                  _texField, _texFieldHover, _texFieldFocus, _texTabHover, _texTabActive, _texSep,
                  _texTrack, _texThumb, _texThumbHover, _texScrollTrack, _texScrollThumb, _texScrollThumbHover,
                  _texCheckOff, _texCheckOn, _texCheckHover, _texTip, _texBanner;

        GUIStyle _cardStyle, _sectionStyle, _hintStyle, _sepStyle, _primaryStyle, _dangerStyle, _selectedStyle,
                 _tabStyle, _tabActiveStyle, _toggleStyle, _tipStyle, _statusBarStyle, _bannerStyle, _bodyStyle;

        Texture2D NewTexture(int width, int height, Func<int, int, Color> pixel)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = pixel(x, y);
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>Signed distance (negative inside) to a rounded rectangle, in pixels.</summary>
        static float RoundedDistance(float x, float y, float width, float height, float radius)
        {
            float px = Mathf.Abs(x + 0.5f - width * 0.5f) - (width * 0.5f - radius);
            float py = Mathf.Abs(y + 0.5f - height * 0.5f) - (height * 0.5f - radius);
            float ox = Mathf.Max(px, 0f);
            float oy = Mathf.Max(py, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
        }

        Texture2D RoundedTexture(int size, float radius, Color fill, Color border, float borderWidth)
        {
            return NewTexture(size, size, delegate(int x, int y)
            {
                float d = RoundedDistance(x, y, size, size, radius);
                float shape = Mathf.Clamp01(0.5f - d);
                float edge = Mathf.Clamp01(0.5f + d + borderWidth);
                Color c = Color.Lerp(fill, border, edge);
                c.a *= shape;
                return c;
            });
        }

        Texture2D SolidTexture(Color color)
        {
            return NewTexture(4, 4, delegate(int x, int y) { return color; });
        }

        Texture2D CircleTexture(int size, Color fill, Color rim)
        {
            return NewTexture(size, size, delegate(int x, int y)
            {
                float d = RoundedDistance(x, y, size, size, size * 0.5f);
                float shape = Mathf.Clamp01(0.5f - d);
                Color c = Color.Lerp(fill, rim, Mathf.Clamp01(0.5f + d + 1.5f));
                c.a *= shape;
                return c;
            });
        }

        /// <summary>A horizontal bar inside a transparent square: the slider track.</summary>
        Texture2D TrackTexture(int size, int barHeight, Color fill)
        {
            return NewTexture(size, size, delegate(int x, int y)
            {
                float top = (size + barHeight) * 0.5f;
                float bottom = (size - barHeight) * 0.5f;
                float d = RoundedDistance(x, y - bottom, size, barHeight, barHeight * 0.5f);
                float inside = (y + 0.5f >= bottom && y + 0.5f <= top) ? Mathf.Clamp01(0.5f - d) : 0f;
                Color c = fill;
                c.a *= inside;
                return c;
            });
        }

        Texture2D CheckTexture(bool on, bool hover)
        {
            const int size = 32;
            Color[] pixels = new Color[size * size];
            Color fill = on ? ColAccent : ColField;
            Color border = on ? ColAccentPressed : (hover ? ColAccent : ColFieldBorder);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = RoundedDistance(x, y, size, size, 7f);
                    float shape = Mathf.Clamp01(0.5f - d);
                    Color c = Color.Lerp(fill, border, Mathf.Clamp01(0.5f + d + 2.2f));
                    c.a *= shape;
                    pixels[y * size + x] = c;
                }
            }
            if (on)
            {
                Color tick = new Color32(26, 20, 14, 255);
                StampLine(pixels, size, 8.5f, 16.5f, 13.5f, 10.5f, 2.2f, tick);
                StampLine(pixels, size, 13.5f, 10.5f, 23.5f, 22.5f, 2.2f, tick);
            }
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        static void StampLine(Color[] pixels, int size, float x0, float y0, float x1, float y1, float radius, Color color)
        {
            int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float cx = Mathf.Lerp(x0, x1, t);
                float cy = Mathf.Lerp(y0, y1, t);
                int minX = Mathf.Max(0, (int)(cx - radius - 1f));
                int maxX = Mathf.Min(size - 1, (int)(cx + radius + 1f));
                int minY = Mathf.Max(0, (int)(cy - radius - 1f));
                int maxY = Mathf.Min(size - 1, (int)(cy + radius + 1f));
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float dx = x + 0.5f - cx;
                        float dy = y + 0.5f - cy;
                        float cover = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                        if (cover <= 0f)
                            continue;
                        Color existing = pixels[y * size + x];
                        pixels[y * size + x] = Color.Lerp(existing, new Color(color.r, color.g, color.b, existing.a), cover);
                    }
                }
            }
        }

        static void SetState(GUIStyleState state, Texture2D background, Color text)
        {
            state.background = background;
            state.textColor = text;
        }

        static void SetAllStates(GUIStyle style, Texture2D normal, Texture2D hover, Texture2D active, Color text)
        {
            SetState(style.normal, normal, text);
            SetState(style.hover, hover, text);
            SetState(style.active, active, text);
            SetState(style.focused, normal, text);
            SetState(style.onNormal, normal, text);
            SetState(style.onHover, hover, text);
            SetState(style.onActive, active, text);
            SetState(style.onFocused, normal, text);
        }

        void EnsureTheme()
        {
            if (_skin != null)
                return;

            // Textures first.
            _texWindow = NewTexture(48, 96, delegate(int x, int y)
            {
                float d = RoundedDistance(x, y, 48, 96, 10f);
                float shape = Mathf.Clamp01(0.5f - d);
                Color fill = y >= 96 - 34 ? ColHeader : ColWindow;
                Color c = Color.Lerp(fill, ColWindowBorder, Mathf.Clamp01(0.5f + d + 1.2f));
                c.a *= shape;
                return c;
            });
            _texCard = RoundedTexture(24, 6f, ColCard, ColCardBorder, 1f);
            _texButton = RoundedTexture(24, 6f, ColButton, ColButton, 0f);
            _texButtonHover = RoundedTexture(24, 6f, ColButtonHover, ColButtonHover, 0f);
            _texButtonPressed = RoundedTexture(24, 6f, ColButtonPressed, ColButtonPressed, 0f);
            _texPrimary = RoundedTexture(24, 6f, ColAccent, ColAccent, 0f);
            _texPrimaryHover = RoundedTexture(24, 6f, ColAccentHover, ColAccentHover, 0f);
            _texPrimaryPressed = RoundedTexture(24, 6f, ColAccentPressed, ColAccentPressed, 0f);
            _texDanger = RoundedTexture(24, 6f, ColDanger, ColDanger, 0f);
            _texDangerHover = RoundedTexture(24, 6f, ColDangerHover, ColDangerHover, 0f);
            _texDangerPressed = RoundedTexture(24, 6f, ColDangerPressed, ColDangerPressed, 0f);
            _texField = RoundedTexture(24, 5f, ColField, ColFieldBorder, 1f);
            _texFieldHover = RoundedTexture(24, 5f, ColField, new Color32(86, 98, 122, 255), 1f);
            _texFieldFocus = RoundedTexture(24, 5f, ColField, ColAccent, 1.4f);
            _texTabHover = RoundedTexture(24, 6f, new Color32(40, 46, 58, 255), new Color32(40, 46, 58, 255), 0f);
            _texTabActive = NewTexture(16, 32, delegate(int x, int y)
            {
                if (y < 3)
                    return ColAccent;
                return ColCard;
            });
            _texSep = SolidTexture(ColCardBorder);
            _texTrack = TrackTexture(16, 6, new Color32(14, 17, 22, 255));
            _texThumb = CircleTexture(16, ColAccent, ColAccentPressed);
            _texThumbHover = CircleTexture(16, ColAccentHover, ColAccent);
            _texScrollTrack = RoundedTexture(16, 5f, new Color32(20, 23, 29, 255), new Color32(20, 23, 29, 255), 0f);
            _texScrollThumb = RoundedTexture(16, 5f, new Color32(66, 75, 96, 255), new Color32(66, 75, 96, 255), 0f);
            _texScrollThumbHover = RoundedTexture(16, 5f, new Color32(96, 108, 134, 255), new Color32(96, 108, 134, 255), 0f);
            _texCheckOff = CheckTexture(false, false);
            _texCheckOn = CheckTexture(true, false);
            _texCheckHover = CheckTexture(false, true);
            _texTip = RoundedTexture(24, 5f, new Color32(12, 14, 19, 245), ColAccent, 1f);
            _texBanner = RoundedTexture(24, 6f, new Color32(0, 0, 0, 150), new Color32(255, 255, 255, 40), 1f);

            // A private copy of the default skin, so the game's own IMGUI is never restyled.
            _skinBase = GUI.skin;
            _skin = UnityEngine.Object.Instantiate(GUI.skin);
            _skin.hideFlags = HideFlags.HideAndDontSave;

            _skin.settings.cursorColor = ColAccentHover;
            _skin.settings.selectionColor = new Color(ColAccent.r, ColAccent.g, ColAccent.b, 0.45f);
            _skin.settings.cursorFlashSpeed = 1.6f;
            _skin.settings.doubleClickSelectsWord = true;
            _skin.settings.tripleClickSelectsLine = true;

            GUIStyle window = _skin.window;
            SetAllStates(window, _texWindow, _texWindow, _texWindow, ColText);
            window.border = new RectOffset(14, 14, 34, 14);
            window.padding = new RectOffset(14, 14, 40, 12);
            window.alignment = TextAnchor.UpperLeft;
            window.contentOffset = new Vector2(6f, -31f);
            window.fontStyle = FontStyle.Bold;
            window.fontSize = 13;
            window.clipping = TextClipping.Clip;

            GUIStyle label = _skin.label;
            label.normal.textColor = ColText;
            label.fontSize = 12;
            label.margin = new RectOffset(2, 2, 2, 2);

            GUIStyle button = _skin.button;
            SetAllStates(button, _texButton, _texButtonHover, _texButtonPressed, ColText);
            button.border = new RectOffset(8, 8, 8, 8);
            button.padding = new RectOffset(11, 11, 5, 6);
            button.margin = new RectOffset(3, 3, 3, 3);
            button.alignment = TextAnchor.MiddleCenter;
            button.fontSize = 12;
            button.clipping = TextClipping.Clip;

            GUIStyle field = _skin.textField;
            SetState(field.normal, _texField, ColText);
            SetState(field.hover, _texFieldHover, ColText);
            SetState(field.active, _texFieldFocus, ColText);
            SetState(field.focused, _texFieldFocus, ColText);
            SetState(field.onNormal, _texField, ColText);
            SetState(field.onHover, _texFieldHover, ColText);
            SetState(field.onActive, _texFieldFocus, ColText);
            SetState(field.onFocused, _texFieldFocus, ColText);
            field.border = new RectOffset(8, 8, 8, 8);
            field.padding = new RectOffset(8, 8, 5, 6);
            field.margin = new RectOffset(3, 3, 3, 3);
            field.fontSize = 12;

            GUIStyle area = _skin.textArea;
            SetState(area.normal, _texField, ColText);
            SetState(area.hover, _texFieldHover, ColText);
            SetState(area.active, _texFieldFocus, ColText);
            SetState(area.focused, _texFieldFocus, ColText);
            area.border = new RectOffset(8, 8, 8, 8);
            area.padding = new RectOffset(8, 8, 6, 6);
            area.fontSize = 12;

            GUIStyle box = _skin.box;
            SetState(box.normal, _texCard, ColText);
            box.border = new RectOffset(8, 8, 8, 8);
            box.padding = new RectOffset(8, 8, 6, 6);

            GUIStyle slider = _skin.horizontalSlider;
            SetState(slider.normal, _texTrack, ColText);
            slider.border = new RectOffset(7, 7, 0, 0);
            slider.fixedHeight = 16f;
            slider.margin = new RectOffset(4, 4, 6, 4);
            GUIStyle thumb = _skin.horizontalSliderThumb;
            SetState(thumb.normal, _texThumb, ColText);
            SetState(thumb.hover, _texThumbHover, ColText);
            SetState(thumb.active, _texThumbHover, ColText);
            thumb.border = new RectOffset(0, 0, 0, 0);
            thumb.fixedWidth = 16f;
            thumb.fixedHeight = 16f;
            thumb.overflow = new RectOffset(0, 0, 0, 0);

            StyleScrollbar(_skin.verticalScrollbar, _skin.verticalScrollbarThumb, true);
            StyleScrollbar(_skin.horizontalScrollbar, _skin.horizontalScrollbarThumb, false);
            HideScrollButton(_skin.verticalScrollbarUpButton);
            HideScrollButton(_skin.verticalScrollbarDownButton);
            HideScrollButton(_skin.horizontalScrollbarLeftButton);
            HideScrollButton(_skin.horizontalScrollbarRightButton);

            // ---- custom styles ------------------------------------------------
            _cardStyle = new GUIStyle(GUIStyle.none);
            SetState(_cardStyle.normal, _texCard, ColText);
            _cardStyle.border = new RectOffset(8, 8, 8, 8);
            _cardStyle.padding = new RectOffset(14, 14, 10, 12);
            _cardStyle.margin = new RectOffset(2, 2, 4, 6);

            _sectionStyle = new GUIStyle(label);
            _sectionStyle.fontSize = 11;
            _sectionStyle.fontStyle = FontStyle.Bold;
            _sectionStyle.normal.textColor = ColAccent;
            _sectionStyle.margin = new RectOffset(2, 2, 0, 4);

            _hintStyle = new GUIStyle(label);
            _hintStyle.fontSize = 11;
            _hintStyle.wordWrap = true;
            _hintStyle.normal.textColor = ColMuted;

            _bodyStyle = new GUIStyle(label);
            _bodyStyle.wordWrap = true;
            _bodyStyle.fontSize = 13;

            _sepStyle = new GUIStyle(GUIStyle.none);
            SetState(_sepStyle.normal, _texSep, ColText);
            _sepStyle.margin = new RectOffset(0, 0, 6, 6);

            _primaryStyle = new GUIStyle(button);
            SetAllStates(_primaryStyle, _texPrimary, _texPrimaryHover, _texPrimaryPressed, new Color32(26, 18, 10, 255));
            _primaryStyle.fontStyle = FontStyle.Bold;

            _dangerStyle = new GUIStyle(button);
            SetAllStates(_dangerStyle, _texDanger, _texDangerHover, _texDangerPressed, Color.white);
            _dangerStyle.fontStyle = FontStyle.Bold;

            _selectedStyle = new GUIStyle(button);
            SetAllStates(_selectedStyle, _texPrimary, _texPrimaryHover, _texPrimaryPressed, new Color32(26, 18, 10, 255));

            _tabStyle = new GUIStyle(button);
            SetAllStates(_tabStyle, null, _texTabHover, _texTabHover, ColMuted);
            _tabStyle.hover.textColor = ColText;
            _tabStyle.active.textColor = ColText;
            _tabStyle.padding = new RectOffset(12, 12, 6, 7);
            _tabStyle.margin = new RectOffset(1, 1, 0, 0);
            _tabStyle.fontSize = 12;

            _tabActiveStyle = new GUIStyle(_tabStyle);
            SetAllStates(_tabActiveStyle, _texTabActive, _texTabActive, _texTabActive, Color.white);
            _tabActiveStyle.border = new RectOffset(4, 4, 0, 3);
            _tabActiveStyle.fontStyle = FontStyle.Bold;

            _toggleStyle = new GUIStyle(label);
            _toggleStyle.padding = new RectOffset(26, 4, 3, 3);
            _toggleStyle.margin = new RectOffset(3, 3, 3, 3);
            _toggleStyle.normal.textColor = ColText;
            _toggleStyle.hover.textColor = Color.white;
            _toggleStyle.onNormal.textColor = ColText;
            _toggleStyle.onHover.textColor = Color.white;
            _toggleStyle.active.textColor = Color.white;
            _toggleStyle.onActive.textColor = Color.white;
            _toggleStyle.clipping = TextClipping.Overflow;

            _statusBarStyle = new GUIStyle(label);
            _statusBarStyle.fontSize = 11;
            _statusBarStyle.normal.textColor = ColMuted;
            _statusBarStyle.margin = new RectOffset(4, 4, 4, 0);

            _bannerStyle = new GUIStyle(GUIStyle.none);
            SetState(_bannerStyle.normal, _texBanner, Color.white);
            _bannerStyle.border = new RectOffset(8, 8, 8, 8);

            _tipStyle = new GUIStyle(GUIStyle.none);
            SetState(_tipStyle.normal, _texTip, ColText);
            _tipStyle.border = new RectOffset(8, 8, 8, 8);
            _tipStyle.padding = new RectOffset(10, 10, 7, 8);
            _tipStyle.wordWrap = true;
            _tipStyle.fontSize = 12;
            _tipStyle.alignment = TextAnchor.UpperLeft;
            _tipStyle.richText = false;
            _tipStyle.clipping = TextClipping.Overflow;
        }

        void StyleScrollbar(GUIStyle track, GUIStyle thumb, bool vertical)
        {
            SetState(track.normal, _texScrollTrack, ColText);
            track.border = new RectOffset(6, 6, 6, 6);
            track.margin = new RectOffset(0, 0, 0, 0);
            track.padding = new RectOffset(2, 2, 2, 2);
            if (vertical)
                track.fixedWidth = 14f;
            else
                track.fixedHeight = 14f;

            SetState(thumb.normal, _texScrollThumb, ColText);
            SetState(thumb.hover, _texScrollThumbHover, ColText);
            SetState(thumb.active, _texScrollThumbHover, ColText);
            thumb.border = new RectOffset(6, 6, 6, 6);
            thumb.overflow = new RectOffset(0, 0, 0, 0);
            thumb.padding = new RectOffset(0, 0, 0, 0);
            thumb.margin = new RectOffset(0, 0, 0, 0);
            if (vertical)
            {
                thumb.fixedWidth = 14f;
                thumb.fixedHeight = 0f;
            }
            else
            {
                thumb.fixedHeight = 14f;
                thumb.fixedWidth = 0f;
            }
        }

        static void HideScrollButton(GUIStyle button)
        {
            SetState(button.normal, null, Color.clear);
            SetState(button.hover, null, Color.clear);
            SetState(button.active, null, Color.clear);
            button.fixedWidth = 0f;
            button.fixedHeight = 0f;
            button.padding = new RectOffset(0, 0, 0, 0);
            button.margin = new RectOffset(0, 0, 0, 0);
            button.border = new RectOffset(0, 0, 0, 0);
        }

        // ------------------------------------------------------------------
        // Layout helpers
        // ------------------------------------------------------------------

        void BeginCard(string title)
        {
            GUILayout.BeginVertical(_cardStyle);
            if (!string.IsNullOrEmpty(title))
                GUILayout.Label(title.ToUpperInvariant(), _sectionStyle);
        }

        void EndCard()
        {
            GUILayout.EndVertical();
        }

        void Separator()
        {
            GUILayout.Box(GUIContent.none, _sepStyle, GUILayout.Height(1f), GUILayout.ExpandWidth(true));
        }

        void Hint(string text)
        {
            GUILayout.Label(text, _hintStyle);
        }

        /// <summary>A label with a hover tooltip.</summary>
        void Lbl(string text, string tip, params GUILayoutOption[] options)
        {
            GUILayout.Label(text, options);
            Tip(tip);
        }

        bool Btn(string label, string tip, params GUILayoutOption[] options)
        {
            bool clicked = GUILayout.Button(label, options);
            Tip(tip);
            return clicked;
        }

        bool BtnPrimary(string label, string tip, params GUILayoutOption[] options)
        {
            bool clicked = GUILayout.Button(label, _primaryStyle, options);
            Tip(tip);
            return clicked;
        }

        bool BtnDanger(string label, string tip, params GUILayoutOption[] options)
        {
            bool clicked = GUILayout.Button(label, _dangerStyle, options);
            Tip(tip);
            return clicked;
        }

        /// <summary>A button that stays lit while it is the current choice (playback speed, presets).</summary>
        bool BtnChoice(bool selected, string label, string tip, params GUILayoutOption[] options)
        {
            bool clicked = GUILayout.Button(label, selected ? _selectedStyle : GUI.skin.button, options);
            Tip(tip);
            return clicked;
        }

        string Field(string value, string tip, params GUILayoutOption[] options)
        {
            string result = GUILayout.TextField(value ?? string.Empty, options);
            Tip(tip);
            return result;
        }

        bool Tog(bool value, string label, string tip, params GUILayoutOption[] options)
        {
            bool result = GUILayout.Toggle(value, label, _toggleStyle, options);
            if (Event.current.type == EventType.Repaint)
            {
                Rect rect = GUILayoutUtility.GetLastRect();
                bool hover = rect.Contains(Event.current.mousePosition);
                Texture2D box = result ? _texCheckOn : (hover ? _texCheckHover : _texCheckOff);
                Color previous = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(rect.x + 4f, rect.y + (rect.height - 16f) * 0.5f, 16f, 16f), box);
                GUI.color = previous;
            }
            Tip(tip);
            return result;
        }

        float Slide(float value, float min, float max, string tip, params GUILayoutOption[] options)
        {
            float result = GUILayout.HorizontalSlider(value, min, max, options);
            Tip(tip);
            return result;
        }

        /// <summary>Statuses and notes under a control group: red for errors, muted otherwise.</summary>
        void StatusLine()
        {
            if (!string.IsNullOrEmpty(_statusText))
                GUILayout.Label(_statusText, _statusIsError ? _errorStyle : _hintStyle);
        }

        // ------------------------------------------------------------------
        // Tooltips
        // ------------------------------------------------------------------

        string _tipText;
        string _tipShown;
        float _tipSince;
        bool _tipClipActive;
        Rect _tipClip;
        Vector2 _tipWindowMouse;

        /// <summary>
        /// Registers a tooltip for the control that was just drawn. Only the control under the mouse
        /// wins; the bubble itself is drawn once per frame by <see cref="DrawTooltip"/>.
        /// </summary>
        void Tip(string text)
        {
            if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint || _shortcutsOpen)
                return;
            if (_tipClipActive && !_tipClip.Contains(_tipWindowMouse))
                return;
            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                _tipText = text;
        }

        /// <summary>Draws the pending tooltip next to the mouse, after a short hover delay. Call last inside the window.</summary>
        void DrawTooltip()
        {
            if (Event.current.type != EventType.Repaint)
                return;
            if (string.IsNullOrEmpty(_tipText))
            {
                _tipShown = null;
                return;
            }
            float now = TimeController.RealTime;
            if (!string.Equals(_tipShown, _tipText, StringComparison.Ordinal))
            {
                _tipShown = _tipText;
                _tipSince = now;
            }
            if (now - _tipSince < 0.35f)
                return;

            const float maxWidth = 340f;
            GUIContent content = _measureContent;
            content.text = _tipText;
            float width = Mathf.Min(maxWidth, _tipStyle.CalcSize(content).x + 4f);
            float height = _tipStyle.CalcHeight(content, width);
            // Drawn inside the window callback (window contents are drawn after, and above, all
            // other IMGUI), so the position is in window space and is kept inside the window.
            Vector2 mouse = Event.current.mousePosition;
            float limitW = _windowRect.width;
            float limitH = _windowRect.height;
            float x = mouse.x + 16f;
            float y = mouse.y + 20f;
            if (x + width > limitW - 6f)
                x = Mathf.Max(6f, mouse.x - width - 10f);
            if (y + height > limitH - 6f)
                y = Mathf.Max(6f, mouse.y - height - 12f);

            Color previous = GUI.color;
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, width, height), content, _tipStyle);
            GUI.color = previous;
        }

        // ------------------------------------------------------------------
        // "Are you sure?" dialog
        // ------------------------------------------------------------------

        Action _confirmAction;
        string _confirmTitle = string.Empty;
        string _confirmBody = string.Empty;
        string _confirmYes = "Yes";
        bool _confirmOffersSkip;
        bool _confirmSkipChecked;
        bool _skipOverwriteConfirm;

        void AskConfirm(string title, string body, string yesLabel, bool offerSkip, Action action)
        {
            _confirmTitle = title;
            _confirmBody = body;
            _confirmYes = yesLabel;
            _confirmOffersSkip = offerSkip;
            _confirmSkipChecked = false;
            _confirmAction = action;
        }

        /// <summary>
        /// Runs <paramref name="save"/> straight away when the target file does not exist yet;
        /// otherwise asks "Are you sure? This will overwrite your file." first.
        /// </summary>
        void RequestOverwriteSave(string path, Action save)
        {
            if (string.IsNullOrEmpty(path))
            {
                _statusText = "Type a file path first.";
                _statusIsError = true;
                _plugin.Toast("Type a file path first.", true);
                return;
            }
            if (_skipOverwriteConfirm || !File.Exists(path))
            {
                save();
                return;
            }
            AskConfirm("Overwrite this file?",
                "Are you sure? This will overwrite your file:\n" + path,
                "Overwrite", true, save);
        }

        /// <summary>
        /// Engine hook: a recording stop would save over <paramref name="path"/>, which exists.
        /// Opens the window and asks first; Cancel keeps recording (paused). False when the
        /// "don't ask again" box was ticked earlier, so the engine saves straight away.
        /// </summary>
        bool AskRecordingOverwrite(string path)
        {
            if (_skipOverwriteConfirm)
                return false;
            _visible = true;
            AskConfirm("Overwrite this file?",
                "Are you sure? Stopping the recording will overwrite your file:\n" + path +
                "\nThe file is replaced by the script in memory (the recording), and its comments are dropped. Cancel keeps recording, paused.",
                "Overwrite", true, delegate { _engine.StopRecording(true); });
            _plugin.Toast("Stopping the recording would overwrite " + Path.GetFileName(path) + ": confirm in the TAS window.", false);
            return true;
        }

        void DrawConfirm()
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(_cardStyle, GUILayout.Width(560f));
            GUILayout.Label(_confirmTitle.ToUpperInvariant(), _sectionStyle);
            GUILayout.Label(_confirmBody, _bodyStyle);
            if (_confirmOffersSkip)
            {
                GUILayout.Space(4f);
                _confirmSkipChecked = Tog(_confirmSkipChecked, "Don't ask again until the game closes",
                    "Skips this confirmation for every save until you restart ULTRAKILL.");
            }
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (Btn("Cancel", "Keep the existing file and go back.", GUILayout.Width(110f)))
            {
                _confirmAction = null;
                _statusText = "Cancelled. Nothing was changed.";
                _statusIsError = false;
            }
            else if (BtnDanger(_confirmYes, "Yes, replace the file on disk.", GUILayout.Width(130f)))
            {
                Action action = _confirmAction;
                if (_confirmOffersSkip && _confirmSkipChecked)
                    _skipOverwriteConfirm = true;
                _confirmAction = null;
                if (action != null)
                    action();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }
    }
}
