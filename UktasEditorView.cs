using System;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// Notepad-style code editor drawn with IMGUI. IMGUI's own TextArea cannot colour text and
    /// breaks on very long files, so this is a small purpose-built editor: it draws only the lines
    /// that are on screen, colours them with <see cref="UktasSyntax"/>, and handles the keyboard and
    /// mouse itself (Ctrl+K / Ctrl+Shift+K comment, Ctrl+S, Ctrl+Z/Y, Ctrl+C/X/V/A, Tab / Shift+Tab,
    /// Alt+Up/Down and Ctrl+Shift+Up/Down move lines, Ctrl+L / Ctrl+Shift+L, Ctrl+Enter, Ctrl+F, Ctrl+G; see GuiManager.EditorTab).
    /// A monospace font is expected; tokens are placed on a character grid.
    /// </summary>
    public sealed class UktasEditorView
    {
        public readonly UktasTextBuffer Buffer = new UktasTextBuffer();

        /// <summary>Set by Ctrl+S; the owner reads and clears it.</summary>
        public bool SaveRequested;
        /// <summary>Set by Ctrl+F; the owner opens its find bar and clears it.</summary>
        public bool FindRequested;
        /// <summary>Set by Ctrl+G; the owner opens its go-to-line bar and clears it.</summary>
        public bool GoToRequested;
        /// <summary>Zero based line to flag as a script error, or -1.</summary>
        public int ErrorLine = -1;
        public Font Font;
        public int FontSize = 14;

        // Palette (One Dark inspired, matching the Notepad++ language file).
        static readonly Color BackColor = new Color32(27, 31, 39, 255);
        static readonly Color GutterColor = new Color32(22, 25, 32, 255);
        static readonly Color GutterLineColor = new Color32(45, 51, 64, 255);
        static readonly Color CurrentLineColor = new Color32(35, 41, 54, 255);
        static readonly Color SelectionColor = new Color32(97, 175, 239, 90);
        static readonly Color CaretColor = new Color32(255, 196, 77, 255);
        static readonly Color LineNumberColor = new Color32(88, 97, 116, 255);
        static readonly Color LineNumberActiveColor = new Color32(200, 207, 220, 255);
        static readonly Color ErrorBackColor = new Color32(90, 32, 36, 120);
        static readonly Color ErrorColor = new Color32(255, 102, 102, 255);

        static readonly Color[] KindColors =
        {
            new Color32(216, 222, 233, 255), // plain
            new Color32(107, 118, 140, 255), // comment
            new Color32(209, 154, 102, 255), // number
            new Color32(86, 182, 194, 255),  // header key
            new Color32(229, 192, 123, 255), // value
            new Color32(97, 175, 239, 255),  // command
            new Color32(224, 108, 117, 255), // special frame keyword
            new Color32(152, 195, 121, 255), // modifier (once:, left)
            new Color32(120, 132, 158, 255), // pipes and semicolons
            new Color32(62, 71, 90, 255),    // dashes (off slots)
            new Color32(198, 120, 221, 255), // autofunc letters
            new Color32(152, 195, 121, 255), // movement letters
            new Color32(240, 150, 100, 255)  // action letters
        };

        const float SbWidth = 14f;
        const float TextPad = 8f;
        const float LineGap = 3f;

        GUIStyle _textStyle;
        GUIStyle _numberStyle;
        GUISkin _styleSkin;
        Font _styleFont;
        int _styleSize;
        float _charW = 8f;
        float _lineH = 18f;
        float _scrollX;
        float _scrollY;
        float _viewH = 300f;
        int _controlId;
        bool _wantFocus;
        bool _reveal;
        bool _dragging;
        float _lastInput;
        readonly GUIContent _measure = new GUIContent();

        public bool HasFocus { get { return _controlId != 0 && GUIUtility.keyboardControl == _controlId; } }

        public void Focus()
        {
            _wantFocus = true;
        }

        public void GoToLine(int line)
        {
            Buffer.SetCaret(line, 0, false);
            _reveal = true;
        }

        /// <summary>Scrolls the view to the caret on the next paint (after a find or a jump).</summary>
        public void ScrollToCaret()
        {
            _reveal = true;
        }

        /// <summary>Caret position and size for the status bar.</summary>
        public string StatusText()
        {
            string text = "Ln " + (Buffer.CaretLine + 1) + ", Col " + (Buffer.CaretCol + 1) + "   |   " +
                          Buffer.LineCount + " lines";
            if (Buffer.HasSelection)
                text += "   |   " + Buffer.GetSelectedText().Length + " selected";
            return text;
        }

        void EnsureStyle()
        {
            if (_textStyle != null && ReferenceEquals(_styleSkin, GUI.skin) && ReferenceEquals(_styleFont, Font) &&
                _styleSize == FontSize)
                return;

            _styleSkin = GUI.skin;
            _styleFont = Font;
            _styleSize = FontSize;

            _textStyle = new GUIStyle(GUI.skin.label);
            _textStyle.padding = new RectOffset(0, 0, 0, 0);
            _textStyle.margin = new RectOffset(0, 0, 0, 0);
            _textStyle.wordWrap = false;
            _textStyle.richText = false;
            _textStyle.clipping = TextClipping.Overflow;
            _textStyle.alignment = TextAnchor.UpperLeft;
            _textStyle.fontStyle = FontStyle.Normal;
            _textStyle.font = Font;
            _textStyle.fontSize = FontSize;
            _textStyle.normal.textColor = Color.white;

            _numberStyle = new GUIStyle(_textStyle);
            _numberStyle.alignment = TextAnchor.UpperRight;

            _measure.text = new string('M', 40);
            float width = _textStyle.CalcSize(_measure).x / 40f;
            _charW = width > 1f ? width : 8f;
            _measure.text = "Mg|";
            _lineH = Mathf.Ceil(_textStyle.CalcSize(_measure).y) + LineGap;
        }

        static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        float GutterWidth()
        {
            int digits = Mathf.Max(3, Buffer.LineCount.ToString().Length);
            return Mathf.Ceil(digits * _charW) + 22f;
        }

        // ------------------------------------------------------------------
        // Entry point: lays itself out, handles input, paints
        // ------------------------------------------------------------------

        public void Draw(float minHeight)
        {
            EnsureStyle();
            Event e = Event.current;

            Rect outer = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.MinHeight(minHeight), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            _controlId = GUIUtility.GetControlID(FocusType.Keyboard, outer);

            Rect textRect = new Rect(outer.x, outer.y, Mathf.Max(10f, outer.width - SbWidth), Mathf.Max(10f, outer.height - SbWidth));
            Rect vRect = new Rect(outer.xMax - SbWidth, outer.y, SbWidth, textRect.height);
            Rect hRect = new Rect(outer.x, outer.yMax - SbWidth, textRect.width, SbWidth);

            float gutter = GutterWidth();
            float contentH = Buffer.LineCount * _lineH + _lineH * 2f;
            float contentW = Buffer.MaxLineLength * _charW + 60f;
            float viewW = textRect.width - gutter - TextPad;

            if (_wantFocus && e.type != EventType.Layout)
            {
                _wantFocus = false;
                GUIUtility.keyboardControl = _controlId;
            }

            if (e.type != EventType.Layout && e.type != EventType.Repaint)
                HandleEvent(e, textRect, outer, gutter);

            // Scrollbars run on every event type so control ids stay stable between passes.
            float vSize = Mathf.Min(textRect.height, Mathf.Max(1f, contentH));
            float hSize = Mathf.Min(viewW, Mathf.Max(1f, contentW));
            float newY = GUI.VerticalScrollbar(vRect, _scrollY, vSize, 0f, Mathf.Max(contentH, textRect.height));
            float newX = GUI.HorizontalScrollbar(hRect, _scrollX, hSize, 0f, Mathf.Max(contentW, viewW));
            if (e.type != EventType.Layout && e.type != EventType.Repaint)
            {
                _scrollY = newY;
                _scrollX = newX;
            }

            if (e.type == EventType.Repaint)
            {
                _viewH = textRect.height;
                if (_reveal)
                {
                    _reveal = false;
                    Reveal(viewW, textRect.height);
                }
                _scrollY = Mathf.Clamp(_scrollY, 0f, Mathf.Max(0f, contentH - textRect.height));
                _scrollX = Mathf.Clamp(_scrollX, 0f, Mathf.Max(0f, contentW - viewW));
                Paint(textRect, gutter);
            }
        }

        void Reveal(float viewW, float viewH)
        {
            float top = Buffer.CaretLine * _lineH;
            if (top < _scrollY)
                _scrollY = top;
            else if (top + _lineH > _scrollY + viewH)
                _scrollY = top + _lineH - viewH;

            float x = Buffer.CaretCol * _charW;
            if (x < _scrollX)
                _scrollX = Mathf.Max(0f, x - 40f);
            else if (x + _charW > _scrollX + viewW)
                _scrollX = x + _charW - viewW + 40f;
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        void PositionAt(Vector2 mouse, Rect textRect, float gutter, out int line, out int col)
        {
            float y = mouse.y - textRect.y + _scrollY;
            line = Mathf.Clamp((int)Mathf.Floor(y / _lineH), 0, Buffer.LineCount - 1);
            float x = mouse.x - textRect.x - gutter - TextPad + _scrollX;
            col = Mathf.Clamp((int)Mathf.Round(x / _charW), 0, Buffer.GetLine(line).Length);
        }

        void HandleEvent(Event e, Rect textRect, Rect outer, float gutter)
        {
            EventType type = e.GetTypeForControl(_controlId);
            switch (type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && textRect.Contains(e.mousePosition))
                    {
                        GUIUtility.keyboardControl = _controlId;
                        GUIUtility.hotControl = _controlId;
                        int line;
                        int col;
                        PositionAt(e.mousePosition, textRect, gutter, out line, out col);
                        _dragging = false;
                        if (e.clickCount >= 3)
                        {
                            Buffer.SelectLine(line);
                        }
                        else if (e.clickCount == 2)
                        {
                            Buffer.SelectWordAt(line, col);
                        }
                        else
                        {
                            Buffer.SetCaret(line, col, e.shift);
                            _dragging = true;
                        }
                        Touch();
                        e.Use();
                    }
                    else if (GUIUtility.keyboardControl == _controlId && !outer.Contains(e.mousePosition))
                    {
                        // A click anywhere else gives up the keyboard.
                        GUIUtility.keyboardControl = 0;
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == _controlId && _dragging)
                    {
                        int line;
                        int col;
                        PositionAt(e.mousePosition, textRect, gutter, out line, out col);
                        Buffer.SetCaret(line, col, true);
                        Touch();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == _controlId)
                    {
                        GUIUtility.hotControl = 0;
                        _dragging = false;
                        e.Use();
                    }
                    break;

                case EventType.ScrollWheel:
                    if (outer.Contains(e.mousePosition))
                    {
                        if (e.shift)
                            _scrollX = Mathf.Max(0f, _scrollX + e.delta.y * _charW * 3f);
                        else
                            _scrollY = Mathf.Max(0f, _scrollY + e.delta.y * _lineH * 1.5f);
                        e.Use();
                    }
                    break;

                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl == _controlId && HandleKey(e))
                        e.Use();
                    break;
            }
        }

        void Touch()
        {
            _reveal = true;
            _lastInput = TimeController.RealTime;
        }

        bool HandleKey(Event e)
        {
            bool ctrl = e.control || e.command;
            bool shift = e.shift;
            bool alt = e.alt;
            KeyCode key = e.keyCode;

            bool handled = false;
            if (ctrl && !alt)
                handled = HandleControlKey(key, shift);
            else if (alt && !ctrl)
                handled = HandleAltKey(key);
            if (!handled)
                handled = HandleNavigationKey(key, ctrl, shift);
            if (!handled)
            {
                char c = e.character;
                bool printable = c >= ' ' && c != '\u007f' && !(ctrl && !alt);
                if (printable)
                {
                    Buffer.TypeChar(c);
                    handled = true;
                }
            }
            if (handled)
                Touch();
            return handled;
        }

        bool HandleControlKey(KeyCode key, bool shift)
        {
            switch (key)
            {
                case KeyCode.A:
                    Buffer.SelectAll();
                    return true;
                case KeyCode.C:
                    GUIUtility.systemCopyBuffer = Buffer.CopyText(false);
                    return true;
                case KeyCode.X:
                    GUIUtility.systemCopyBuffer = Buffer.CopyText(true);
                    return true;
                case KeyCode.V:
                    Buffer.Paste(GUIUtility.systemCopyBuffer);
                    return true;
                case KeyCode.Z:
                    if (shift)
                        Buffer.Redo();
                    else
                        Buffer.Undo();
                    return true;
                case KeyCode.Y:
                    Buffer.Redo();
                    return true;
                case KeyCode.S:
                    SaveRequested = true;
                    return true;
                case KeyCode.K:
                    if (shift)
                        Buffer.UncommentLines();
                    else
                        Buffer.CommentLines();
                    return true;
                case KeyCode.D:
                    Buffer.Duplicate();
                    return true;
                case KeyCode.L:
                    if (shift)
                        Buffer.DeleteLines();
                    else
                        Buffer.SelectLineExtend();
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Buffer.InsertBlankLine(shift);
                    return true;
                case KeyCode.G:
                    GoToRequested = true;
                    return true;
                case KeyCode.F:
                    FindRequested = true;
                    return true;
                case KeyCode.UpArrow:
                    // Ctrl+Shift+Up / Down: same as Alt+Up / Down. Without Shift the key falls through to navigation.
                    if (!shift)
                        return false;
                    Buffer.MoveLines(-1);
                    return true;
                case KeyCode.DownArrow:
                    if (!shift)
                        return false;
                    Buffer.MoveLines(1);
                    return true;
            }
            return false;
        }

        bool HandleAltKey(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.UpArrow:
                    Buffer.MoveLines(-1);
                    return true;
                case KeyCode.DownArrow:
                    Buffer.MoveLines(1);
                    return true;
            }
            return false;
        }

        bool HandleNavigationKey(KeyCode key, bool ctrl, bool shift)
        {
            int page = Mathf.Max(1, (int)(_viewH / _lineH) - 1);
            switch (key)
            {
                case KeyCode.LeftArrow:
                    Buffer.MoveLeft(shift, ctrl);
                    return true;
                case KeyCode.RightArrow:
                    Buffer.MoveRight(shift, ctrl);
                    return true;
                case KeyCode.UpArrow:
                    Buffer.MoveVertical(-1, shift);
                    return true;
                case KeyCode.DownArrow:
                    Buffer.MoveVertical(1, shift);
                    return true;
                case KeyCode.PageUp:
                    Buffer.MoveVertical(-page, shift);
                    return true;
                case KeyCode.PageDown:
                    Buffer.MoveVertical(page, shift);
                    return true;
                case KeyCode.Home:
                    Buffer.MoveHome(shift, ctrl);
                    return true;
                case KeyCode.End:
                    Buffer.MoveEnd(shift, ctrl);
                    return true;
                case KeyCode.Backspace:
                    Buffer.Backspace(ctrl);
                    return true;
                case KeyCode.Delete:
                    Buffer.Delete(ctrl);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Buffer.NewLine();
                    return true;
                case KeyCode.Tab:
                    Buffer.Tab(shift);
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------

        void Paint(Rect textRect, float gutter)
        {
            GUI.BeginGroup(textRect);
            float width = textRect.width;
            float height = textRect.height;
            Fill(new Rect(0f, 0f, width, height), BackColor);

            int count = Buffer.LineCount;
            int first = Mathf.Max(0, (int)(_scrollY / _lineH));
            int last = Mathf.Min(count - 1, (int)((_scrollY + height) / _lineH));
            float originX = gutter + TextPad - _scrollX;

            int sl, sc, el, ec;
            bool selection = Buffer.GetSelection(out sl, out sc, out el, out ec);

            Color previous = GUI.color;
            for (int i = first; i <= last; i++)
            {
                float y = i * _lineH - _scrollY;
                string line = Buffer.GetLine(i);

                if (i == ErrorLine)
                    Fill(new Rect(gutter, y, width - gutter, _lineH), ErrorBackColor);
                else if (!selection && i == Buffer.CaretLine)
                    Fill(new Rect(gutter, y, width - gutter, _lineH), CurrentLineColor);

                if (selection && i >= sl && i <= el)
                {
                    int from = i == sl ? sc : 0;
                    int to = i == el ? ec : line.Length + 1; // +1 shows the selected line break
                    if (to > from)
                        Fill(new Rect(originX + from * _charW, y, (to - from) * _charW, _lineH), SelectionColor);
                }

                SyntaxSpan[] spans = Buffer.GetTokens(i);
                for (int s = 0; s < spans.Length; s++)
                {
                    float x = originX + spans[s].Start * _charW;
                    float w = spans[s].Length * _charW;
                    if (x + w < gutter || x > width)
                        continue;
                    GUI.color = KindColors[spans[s].Kind];
                    GUI.Label(new Rect(x, y, w + _charW * 2f, _lineH), spans[s].Text, _textStyle);
                }

                if (i == ErrorLine)
                    Fill(new Rect(originX, y + _lineH - 2f, Mathf.Max(line.Length, 1) * _charW, 1f), ErrorColor);
            }
            GUI.color = previous;

            // Caret.
            bool focused = HasFocus && Application.isFocused;
            if (focused && Buffer.CaretLine >= first && Buffer.CaretLine <= last)
            {
                float now = TimeController.RealTime;
                bool solid = now - _lastInput < 0.6f;
                if (solid || ((int)(now * 2f) & 1) == 0)
                {
                    float cx = originX + Buffer.CaretCol * _charW;
                    float cy = Buffer.CaretLine * _lineH - _scrollY;
                    if (cx >= gutter - 1f && cx <= width)
                        Fill(new Rect(Mathf.Floor(cx), cy + 1f, 2f, _lineH - 2f), CaretColor);
                }
            }

            // Gutter drawn last so horizontally scrolled text slides under it.
            Fill(new Rect(0f, 0f, gutter, height), GutterColor);
            Fill(new Rect(gutter - 1f, 0f, 1f, height), GutterLineColor);
            for (int i = first; i <= last; i++)
            {
                float y = i * _lineH - _scrollY;
                bool active = i == Buffer.CaretLine;
                GUI.color = i == ErrorLine ? ErrorColor : (active ? LineNumberActiveColor : LineNumberColor);
                GUI.Label(new Rect(0f, y, gutter - 10f, _lineH), (i + 1).ToString(), _numberStyle);
            }
            GUI.color = previous;
            GUI.EndGroup();
        }
    }
}
