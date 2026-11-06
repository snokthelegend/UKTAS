using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UltrakillTAS
{
    /// <summary>
    /// The Script Editor tab: a notepad style text editor for .uktas files with syntax colours
    /// and Notepad++ shortcuts. It edits the raw file text (comments and layout survive, unlike
    /// the in-memory script), checks the syntax with the real parser as you type, and can hand the
    /// result to the player without leaving the window.
    /// </summary>
    public sealed partial class GuiManager
    {
        readonly UktasEditorView _editor = new UktasEditorView();
        Font _editorFont;
        bool _editorFontTried;
        bool _editorInitialised;
        bool _editorTabWasActive;
        string _editorPath = string.Empty;
        string _editorStatus = string.Empty;
        bool _editorStatusError;
        int _editorSeenVersion = -1;
        int _editorCheckedVersion = -1;
        float _editorChangeTime;

        const string EditorShortcutHint =
            "Ctrl+S save    Ctrl+K comment    Ctrl+Shift+K uncomment    Tab / Shift+Tab indent    " +
            "Ctrl+Z undo    Ctrl+Y redo    Ctrl+A / C / X / V    Ctrl+D duplicate line    " +
            "Alt+Up/Down or Ctrl+Shift+Up/Down move line    Ctrl+L select line    Ctrl+F find    Ctrl+G go to line    " +
            "Click Shortcuts for the full list.";

        // Find / go-to-line bar under the text area.
        const int PromptNone = 0;
        const int PromptFind = 1;
        const int PromptGoTo = 2;
        const string PromptControl = "UktasEditorPrompt";
        int _prompt;
        string _promptText = string.Empty;
        bool _promptFocus;
        bool _findMatchCase;

        // Shortcuts popup.
        bool _shortcutsOpen;
        Vector2 _shortcutScroll;
        GUIStyle _shortcutTitleStyle;
        GUIStyle _shortcutKeyStyle;
        GUIStyle _shortcutTextStyle;

        sealed class ShortcutGroup
        {
            public readonly string Name;
            public readonly string[][] Rows;

            public ShortcutGroup(string name, params string[][] rows)
            {
                Name = name;
                Rows = rows;
            }
        }

        static string[] Sc(string keys, string action)
        {
            return new[] { keys, action };
        }

        static readonly ShortcutGroup[] ShortcutGroups =
        {
            new ShortcutGroup("File",
                Sc("Ctrl+S", "Save the file (asks before overwriting)")),
            new ShortcutGroup("Edit",
                Sc("Ctrl+Z", "Undo"),
                Sc("Ctrl+Y  /  Ctrl+Shift+Z", "Redo"),
                Sc("Ctrl+X", "Cut selection (the whole line if nothing is selected)"),
                Sc("Ctrl+C", "Copy selection (the whole line if nothing is selected)"),
                Sc("Ctrl+V", "Paste"),
                Sc("Ctrl+D", "Duplicate the selection, or the current line"),
                Sc("Ctrl+Shift+L", "Delete the current line(s)"),
                Sc("Alt+Up  /  Alt+Down", "Move the current line(s) up or down"),
                Sc("Ctrl+Shift+Up  /  Down", "Move the current line(s) up or down (same as Alt+Up / Alt+Down)"),
                Sc("Ctrl+Enter", "Insert an empty line below and move to it"),
                Sc("Ctrl+Shift+Enter", "Insert an empty line above and move to it"),
                Sc("Enter", "New line, keeps the indentation"),
                Sc("Backspace  /  Delete", "Delete a character (hold Ctrl for a whole word)")),
            new ShortcutGroup("Navigate",
                Sc("Arrow keys", "Move the caret"),
                Sc("Ctrl+Left  /  Ctrl+Right", "Jump by word"),
                Sc("Home  /  End", "Start (first text, then column 1) / end of the line"),
                Sc("Ctrl+Home  /  Ctrl+End", "Start / end of the file"),
                Sc("PageUp  /  PageDown", "Move one screen up / down"),
                Sc("Ctrl+G", "Go to line (type 12, or 12:5 for line and column)"),
                Sc("Ctrl+F", "Find in file: Enter = next, Shift+Enter = previous, Esc = close the bar")),
            new ShortcutGroup("Select",
                Sc("Shift + move keys", "Extend the selection"),
                Sc("Ctrl+A", "Select everything"),
                Sc("Ctrl+L", "Select the current line (again: add the next line)"),
                Sc("Double-click  /  Triple-click", "Select a word / a whole line")),
            new ShortcutGroup("Comment",
                Sc("Ctrl+K", "Comment the selected line(s) with //"),
                Sc("Ctrl+Shift+K", "Uncomment the selected line(s)")),
            new ShortcutGroup("Indent",
                Sc("Tab", "Indent the selected lines, or insert spaces up to the next stop"),
                Sc("Shift+Tab", "Outdent the selected lines")),
            new ShortcutGroup("Other",
                Sc("Esc", "Close this window (or the Find / Go to bar); the text is never touched"),
                Sc("Mouse wheel  /  Shift+wheel", "Scroll vertically / horizontally"),
                Sc("A-  /  A+ buttons", "Smaller / larger text"))
        };

        const string EditorTemplate =
            "// New .uktas script. Ctrl+K comments a line, Ctrl+S saves.\n" +
            "version 1\n" +
            "host_frametime 0.008\n" +
            "seed 0\n" +
            "start_type restart_mission\n" +
            "frames\n" +
            "// autofuncs|movement|actions|frametime|yaw|pitch|frames|commands\n" +
            "----------|------|------|-|0.0|0.0|60|\n" +
            "----------|f-----|------|-|0.0|0.0|120|\n";

        void EnsureEditorFont()
        {
            if (_editorFontTried)
                return;
            _editorFontTried = true;
            try
            {
                _editorFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 14);
            }
            catch (Exception ex)
            {
                _plugin.LogWarning("Script editor: could not create a monospace font (" + ex.Message + ").");
            }
        }

        /// <summary>First visit: show the script on disk, else the one in memory, else a starter script.</summary>
        void InitialiseEditor()
        {
            _editorInitialised = true;
            string resolved = ResolveScriptPath(_pathText);
            if (resolved != null && LoadIntoEditor(resolved, false))
                return;

            if (_engine.Script != null)
            {
                SetEditorText(_engine.Script.Serialize(), string.IsNullOrEmpty(_engine.ScriptPath) ? _pathText : _engine.ScriptPath);
                _editorStatus = "Showing the script that is loaded in the tool. Comments are not kept in memory, so none appear here.";
                _editorStatusError = false;
                return;
            }

            SetEditorText(EditorTemplate, _pathText);
            _editorStatus = "A starter script. Edit it, then press Save.";
            _editorStatusError = false;
        }

        void SetEditorText(string text, string path)
        {
            _editor.Buffer.SetText(text);
            _editor.ErrorLine = -1;
            _editorPath = path ?? string.Empty;
            _editorSeenVersion = -1;
            _editorCheckedVersion = -1;
            _editor.Focus();
        }

        bool LoadIntoEditor(string path, bool announce)
        {
            try
            {
                SetEditorText(File.ReadAllText(path), path);
                _editorStatus = "Opened " + path + ".";
                _editorStatusError = false;
                if (announce)
                    _plugin.Toast("Opened " + Path.GetFileName(path) + " in the Script Editor.", false);
                return true;
            }
            catch (Exception ex)
            {
                _editorStatus = "Could not open " + path + ": " + ex.Message;
                _editorStatusError = true;
                return false;
            }
        }

        /// <summary>Opens a file in the Script Editor tab, asking first if the current text has unsaved changes.</summary>
        void OpenInTextEditor(string path)
        {
            string resolved = ResolveScriptPath(path);
            if (resolved == null)
            {
                _editorStatus = "No file found at: " + path;
                _editorStatusError = true;
                _tab = TabScriptEditor;
                return;
            }
            _tab = TabScriptEditor;
            _editorInitialised = true;
            GuardUnsaved("Open another file?", delegate { LoadIntoEditor(resolved, true); });
        }

        /// <summary>Runs the action now, or after the user agrees to drop unsaved editor changes.</summary>
        void GuardUnsaved(string title, Action action)
        {
            if (!_editor.Buffer.Modified)
            {
                action();
                return;
            }
            AskConfirm(title, "The text in the Script Editor has changes that were not saved. They will be lost.",
                "Discard changes", false, action);
        }

        string EditorSavePath()
        {
            return _plugin.ResolveScriptSavePath(_editorPath);
        }

        void SaveEditor()
        {
            string path = EditorSavePath();
            RequestOverwriteSave(path, delegate { WriteEditorFile(path); });
        }

        void WriteEditorFile(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string text = _editor.Buffer.GetText(Environment.NewLine);
                // Same safe swap the script saver uses: a crash never leaves half a file behind.
                string temp = path + ".tmp";
                try
                {
                    File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                }
                catch
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw;
                }
                _editor.Buffer.MarkSaved();
                _editorPath = path;
                _pathText = path;
                RememberScriptPath();
                _editorStatus = "Saved " + path + ". Press Play (F5) to run it; the tool reloads a changed file by itself.";
                _editorStatusError = false;
                _plugin.Toast("Saved " + Path.GetFileName(path) + ".", false);
            }
            catch (Exception ex)
            {
                _editorStatus = "Could not save: " + ex.Message;
                _editorStatusError = true;
                _plugin.Toast("Could not save: " + ex.Message, true);
            }
        }

        /// <summary>Parses the editor text with the real parser. Returns it, or null after flagging the error line.</summary>
        TasScript CheckEditorText()
        {
            _editorCheckedVersion = _editor.Buffer.Version;
            try
            {
                TasScript parsed = TasScript.Parse(_editor.Buffer.GetText("\n"), _editorPath);
                _editor.ErrorLine = -1;
                _editorStatus = "Syntax OK: " + parsed.Lines.Count + " lines, " + parsed.TotalTicks + " ticks, " +
                                parsed.TotalTime.ToString("0.###", CultureInfo.InvariantCulture) + " s" +
                                (parsed.Warnings.Count > 0 ? "   (" + parsed.Warnings.Count + " warning(s): " + parsed.Warnings[0] + ")" : string.Empty);
                _editorStatusError = false;
                return parsed;
            }
            catch (TasParseException ex)
            {
                _editor.ErrorLine = ex.LineNumber - 1;
                _editorStatus = ex.Message;
                _editorStatusError = true;
                return null;
            }
            catch (Exception ex)
            {
                _editor.ErrorLine = -1;
                _editorStatus = "Check failed: " + ex.Message;
                _editorStatusError = true;
                return null;
            }
        }

        void UseEditorInTas()
        {
            TasScript parsed = CheckEditorText();
            if (parsed == null)
            {
                _plugin.Toast("Fix the highlighted line first: " + _editorStatus, true);
                return;
            }
            string path = EditorSavePath();
            _engine.SetScript(parsed, path);
            _lastSyncedTick = -1;
            _plugin.Toast("Script from the editor is loaded (" + parsed.TotalTicks + " ticks). Press Play to run it.", false);
            if (_editor.Buffer.Modified)
                _editorStatus += "   Loaded without saving: use Save to keep it on disk.";
        }

        void NewEditorFile()
        {
            GuardUnsaved("Start a new script?", delegate
            {
                string folder = _plugin.ScriptDirectory;
                string name = "new_script.uktas";
                int n = 1;
                while (File.Exists(Path.Combine(folder, name)) && n < 1000)
                    name = "new_script_" + (++n) + ".uktas";
                SetEditorText(EditorTemplate, Path.Combine(folder, name));
                _editorStatus = "A starter script. Edit it, then press Save.";
                _editorStatusError = false;
            });
        }

        void LoadEngineScriptIntoEditor()
        {
            if (_engine.Script == null)
            {
                _editorStatus = "No script is loaded in the tool yet.";
                _editorStatusError = true;
                return;
            }
            GuardUnsaved("Replace the editor text?", delegate
            {
                SetEditorText(_engine.Script.Serialize(), string.IsNullOrEmpty(_engine.ScriptPath) ? _editorPath : _engine.ScriptPath);
                _editorStatus = "Loaded the tool's current script. Comments are not kept in memory, so none appear here.";
                _editorStatusError = false;
            });
        }

        void DrawTextEditorTab()
        {
            if (!_editorInitialised)
                InitialiseEditor();
            EnsureEditorFont();
            _editor.Font = _editorFont != null ? _editorFont : GameFont();

            if (!_editorTabWasActive)
            {
                _editorTabWasActive = true;
                _editor.Focus();
            }

            // File row.
            GUILayout.BeginHorizontal();
            Lbl("File", "The .uktas file this text is saved to. A name without a folder is saved in the script folder.", GUILayout.Width(28));
            _editorPath = Field(_editorPath, "Path of the file being edited.", GUILayout.ExpandWidth(true));
            if (Btn("Open", "Load this file into the editor (asks first if you have unsaved changes).", GUILayout.Width(58)))
                OpenInTextEditor(_editorPath);
            if (BtnPrimary("Save", "Write the text to the file (Ctrl+S). Asks before overwriting an existing file.", GUILayout.Width(62)))
                SaveEditor();
            if (Btn("New", "Start a fresh script from a small template.", GUILayout.Width(50)))
                NewEditorFile();
            GUILayout.EndHorizontal();

            // Action row.
            GUILayout.BeginHorizontal();
            if (Btn("Check syntax", "Run the real script parser on the text and mark the first broken line.", GUILayout.Width(100)))
                CheckEditorText();
            if (Btn("Use in TAS", "Load this text into the tool without saving, so Play runs it right away.", GUILayout.Width(90)))
                UseEditorInTas();
            if (Btn("From loaded script", "Replace the text with the script currently loaded in the tool (comments are not kept in memory).", GUILayout.Width(130)))
                LoadEngineScriptIntoEditor();
            GUILayout.FlexibleSpace();
            Lbl("Text size", "Editor font size.", GUILayout.Width(62));
            if (Btn("A-", "Smaller text.", GUILayout.Width(30)))
                _editor.FontSize = Mathf.Max(10, _editor.FontSize - 1);
            if (Btn("A+", "Larger text.", GUILayout.Width(30)))
                _editor.FontSize = Mathf.Min(24, _editor.FontSize + 1);
            if (Btn("Shortcuts", "Show every keyboard shortcut of the editor in a pop-up window.", GUILayout.Width(80)))
                _shortcutsOpen = true;
            GUILayout.EndHorizontal();

            // The editor itself.
            _editor.Draw(250f);
            Tip("Type here. Colours: comments grey, numbers amber, header keys cyan, commands blue, |, ; pipes grey.");

            // The find / go-to bar. Its visibility is read once so the layout stays the same for the whole pass.
            int promptMode = _prompt;
            if (promptMode != PromptNone)
                DrawEditorPrompt(promptMode);

            if (_editor.SaveRequested)
            {
                _editor.SaveRequested = false;
                SaveEditor();
            }

            // Live syntax check once typing pauses.
            if (Event.current.type == EventType.Repaint)
            {
                UktasTextBuffer buffer = _editor.Buffer;
                float now = TimeController.RealTime;
                if (buffer.Version != _editorSeenVersion)
                {
                    _editorSeenVersion = buffer.Version;
                    _editorChangeTime = now;
                }
                else if (_editorCheckedVersion != buffer.Version && now - _editorChangeTime > 0.9f && buffer.LineCount <= 30000)
                {
                    CheckEditorText();
                }
            }

            // Status row.
            GUILayout.BeginHorizontal();
            GUILayout.Label(_editor.StatusText(), _statusBarStyle, GUILayout.Width(300));
            GUILayout.Label(_editor.Buffer.Modified ? "unsaved changes" : "saved", _editor.Buffer.Modified ? _sectionStyle : _statusBarStyle, GUILayout.Width(110));
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_editorStatus))
                GUILayout.Label(_editorStatus, _editorStatusError ? _errorStyle : _hintStyle);
            Hint(EditorShortcutHint);
            Tip("Keyboard shortcuts of the editor. They work while the text area has the keyboard (click into it first).");

            // Ctrl+F / Ctrl+G take effect after everything above is drawn, so the layout never changes mid-pass.
            if (_editor.FindRequested)
            {
                _editor.FindRequested = false;
                OpenEditorPrompt(PromptFind);
            }
            if (_editor.GoToRequested)
            {
                _editor.GoToRequested = false;
                OpenEditorPrompt(PromptGoTo);
            }
        }

        // ------------------------------------------------------------------
        // Find / go to line
        // ------------------------------------------------------------------

        void OpenEditorPrompt(int mode)
        {
            UktasTextBuffer buffer = _editor.Buffer;
            if (mode == PromptFind)
            {
                string selected = buffer.GetSelectedText();
                if (selected.Length > 0 && selected.Length <= 120 && selected.IndexOf('\n') < 0)
                    _promptText = selected;
                else if (_prompt != PromptFind)
                    _promptText = _lastFindText;
            }
            else
            {
                _promptText = (buffer.CaretLine + 1).ToString(CultureInfo.InvariantCulture);
            }
            _prompt = mode;
            _promptFocus = true;
        }

        string _lastFindText = string.Empty;

        void CloseEditorPrompt()
        {
            if (_prompt == PromptFind)
                _lastFindText = _promptText;
            _prompt = PromptNone;
            _promptFocus = false;
            _editor.Focus();
        }

        void RunEditorFind(bool forward, bool stayOnCurrent)
        {
            if (string.IsNullOrEmpty(_promptText))
                return;
            if (_editor.Buffer.Find(_promptText, forward, _findMatchCase, stayOnCurrent))
            {
                _editor.ScrollToCaret();
                _editorStatus = "Found \"" + _promptText + "\" at line " + (_editor.Buffer.CaretLine + 1) + ".";
                _editorStatusError = false;
            }
            else
            {
                _editorStatus = "Not found: \"" + _promptText + "\".";
                _editorStatusError = true;
            }
        }

        void RunEditorGoTo()
        {
            string[] parts = (_promptText ?? string.Empty).Trim().Split(new[] { ':', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int line;
            if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out line))
            {
                _editorStatus = "Type a line number (or line:column) to jump to.";
                _editorStatusError = true;
                return;
            }
            int col = 1;
            if (parts.Length > 1)
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out col);
            int count = _editor.Buffer.LineCount;
            line = Mathf.Clamp(line, 1, count);
            _editor.Buffer.SetCaret(line - 1, Mathf.Max(1, col) - 1, false);
            _editor.ScrollToCaret();
            _editorStatus = "Jumped to line " + line + " of " + count + ".";
            _editorStatusError = false;
            CloseEditorPrompt();
        }

        void DrawEditorPrompt(int mode)
        {
            Event e = Event.current;
            // Keys are acted on first, but the bar is always drawn in full afterwards: skipping
            // controls in a non-layout pass would shift every control that follows.
            bool fieldFocused = GUI.GetNameOfFocusedControl() == PromptControl;
            if (fieldFocused && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CloseEditorPrompt();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    bool backwards = e.shift;
                    e.Use();
                    if (mode == PromptFind)
                        RunEditorFind(!backwards, false);
                    else
                        RunEditorGoTo();
                }
            }

            GUILayout.BeginHorizontal();
            if (mode == PromptFind)
            {
                Lbl("Find", "Text to look for in the file.", GUILayout.Width(34));
                GUI.SetNextControlName(PromptControl);
                string typed = Field(_promptText, "Type the text; Enter = next match, Shift+Enter = previous, Esc = close.", GUILayout.ExpandWidth(true));
                if (typed != _promptText)
                {
                    _promptText = typed;
                    RunEditorFind(true, true);
                }
                if (Btn("Prev", "Previous match (Shift+Enter).", GUILayout.Width(52)))
                    RunEditorFind(false, false);
                if (BtnPrimary("Next", "Next match (Enter).", GUILayout.Width(52)))
                    RunEditorFind(true, false);
                _findMatchCase = Tog(_findMatchCase, "Match case", "Only match the same upper / lower case.", GUILayout.Width(110));
            }
            else
            {
                Lbl("Go to line", "Line number to jump to, or line:column.", GUILayout.Width(66));
                GUI.SetNextControlName(PromptControl);
                _promptText = Field(_promptText, "Line number (or line:column), then Enter.", GUILayout.Width(120));
                if (BtnPrimary("Go", "Jump to that line (Enter).", GUILayout.Width(52)))
                    RunEditorGoTo();
                GUILayout.FlexibleSpace();
            }
            if (Btn("Close", "Close this bar (Esc) and return to the text.", GUILayout.Width(58)))
                CloseEditorPrompt();
            GUILayout.EndHorizontal();

            if (_promptFocus && e.type == EventType.Repaint)
            {
                _promptFocus = false;
                GUI.FocusControl(PromptControl);
            }
        }

        // ------------------------------------------------------------------
        // Shortcuts pop-up
        // ------------------------------------------------------------------

        void CloseShortcuts()
        {
            _shortcutsOpen = false;
            // Hand the keyboard straight back to the text; nothing in the editor was changed.
            _editor.Focus();
        }

        void EnsureShortcutStyles()
        {
            if (_shortcutTitleStyle != null)
                return;
            _shortcutTitleStyle = new GUIStyle(_sectionStyle);
            _shortcutTitleStyle.fontSize = 14;
            _shortcutTitleStyle.margin = new RectOffset(0, 0, 0, 0);

            _shortcutKeyStyle = new GUIStyle(GUI.skin.label);
            _shortcutKeyStyle.fontStyle = FontStyle.Bold;
            _shortcutKeyStyle.fontSize = 12;
            _shortcutKeyStyle.normal.textColor = ColAccentHover;
            _shortcutKeyStyle.wordWrap = false;
            _shortcutKeyStyle.clipping = TextClipping.Clip;
            _shortcutKeyStyle.alignment = TextAnchor.MiddleLeft;

            _shortcutTextStyle = new GUIStyle(GUI.skin.label);
            _shortcutTextStyle.fontSize = 12;
            _shortcutTextStyle.wordWrap = false;
            _shortcutTextStyle.clipping = TextClipping.Clip;
            _shortcutTextStyle.alignment = TextAnchor.MiddleLeft;
        }

        /// <summary>
        /// A dark, semi-transparent veil over the whole TAS window with a centred table of every
        /// editor shortcut. Everything underneath is switched off while it is open (the caller
        /// sets GUI.enabled to false), and a click outside the panel, Escape or the Close button
        /// dismisses it. Drawn with absolute rectangles only, so it never changes the layout.
        /// </summary>
        void DrawShortcutsPopup()
        {
            EnsureShortcutStyles();
            Event e = Event.current;
            float width = _windowRect.width;
            float height = _windowRect.height;

            float panelW = Mathf.Min(680f, width - 60f);
            float panelH = Mathf.Min(580f, height - 80f);
            Rect panel = new Rect(Mathf.Floor((width - panelW) * 0.5f), Mathf.Max(44f, Mathf.Floor((height - panelH) * 0.5f)), panelW, panelH);

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                CloseShortcuts();
                return;
            }
            if (e.type == EventType.MouseDown && !panel.Contains(e.mousePosition))
            {
                e.Use();
                CloseShortcuts();
                return;
            }

            if (e.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.66f);
                GUI.DrawTexture(new Rect(0f, 0f, width, height), Texture2D.whiteTexture);
                GUI.color = previous;
            }

            GUI.Box(panel, GUIContent.none, _cardStyle);

            const float pad = 18f;
            GUI.Label(new Rect(panel.x + pad, panel.y + 12f, panel.width - 140f, 22f), "SCRIPT EDITOR SHORTCUTS", _shortcutTitleStyle);
            if (GUI.Button(new Rect(panel.xMax - pad - 84f, panel.y + 10f, 84f, 26f), "Close"))
            {
                CloseShortcuts();
                return;
            }
            GUI.Label(new Rect(panel.x + pad, panel.y + 38f, panel.width - 2f * pad, 20f),
                "They work while the text area has the keyboard. Click outside this window or press Esc to close.", _hintStyle);

            Rect view = new Rect(panel.x + pad, panel.y + 66f, panel.width - 2f * pad, panel.height - 66f - 14f);
            const float headerH = 30f;
            const float rowH = 24f;
            float contentH = 24f;
            for (int g = 0; g < ShortcutGroups.Length; g++)
                contentH += headerH + ShortcutGroups[g].Rows.Length * rowH + 6f;

            float contentW = view.width - 18f;
            float keyW = Mathf.Min(230f, contentW * 0.36f);
            _shortcutScroll = GUI.BeginScrollView(view, _shortcutScroll, new Rect(0f, 0f, contentW, contentH));

            float y = 0f;
            GUI.Label(new Rect(8f, y, keyW, 20f), "Shortcut", _hintStyle);
            GUI.Label(new Rect(8f + keyW + 8f, y, contentW - keyW - 16f, 20f), "Action", _hintStyle);
            y += 24f;

            Color old = GUI.color;
            for (int g = 0; g < ShortcutGroups.Length; g++)
            {
                ShortcutGroup group = ShortcutGroups[g];
                GUI.Label(new Rect(4f, y + 6f, contentW - 8f, 20f), group.Name.ToUpperInvariant(), _sectionStyle);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0f, y + headerH - 3f, contentW, 1f), _texSep);
                y += headerH;
                for (int r = 0; r < group.Rows.Length; r++)
                {
                    if ((r & 1) == 1)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.5f);
                        GUI.DrawTexture(new Rect(0f, y, contentW, rowH), _texSep);
                        GUI.color = old;
                    }
                    GUI.Label(new Rect(8f, y, keyW, rowH), group.Rows[r][0], _shortcutKeyStyle);
                    GUI.Label(new Rect(8f + keyW + 8f, y, contentW - keyW - 16f, rowH), group.Rows[r][1], _shortcutTextStyle);
                    y += rowH;
                }
                y += 6f;
            }
            GUI.color = old;
            GUI.EndScrollView();
        }
    }
}
