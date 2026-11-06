using System;
using System.Collections.Generic;
using System.Text;

namespace UltrakillTAS
{
    /// <summary>
    /// The text model behind the script editor: lines, a caret and selection, undo/redo and the
    /// Notepad++ style line commands (comment, indent, duplicate). It knows nothing about Unity or
    /// drawing; <see cref="UktasEditorView"/> feeds it keys and draws it. Lines never contain a
    /// newline, tabs are stored as spaces, and every change goes through <see cref="Replace"/>
    /// so one undo entry always describes exactly one edit.
    /// </summary>
    public sealed class UktasTextBuffer
    {
        public const int TabSize = 4;
        const int MaxUndo = 500;
        const int MergeWindowMs = 1200;
        const int MaxMergedLength = 80;

        const int KindNone = 0;
        const int KindTyping = 1;
        const int KindBackspace = 2;
        const int KindDelete = 3;

        sealed class UndoEntry
        {
            public int Line;
            public int Col;
            public string Removed = string.Empty;
            public string Inserted = string.Empty;
            public int BeforeCaretLine, BeforeCaretCol, BeforeAnchorLine, BeforeAnchorCol;
            public int AfterCaretLine, AfterCaretCol, AfterAnchorLine, AfterAnchorCol;
            public int Kind;
            public int Stamp;
        }

        readonly List<string> _lines = new List<string>(1024);
        readonly List<SyntaxSpan[]> _tokens = new List<SyntaxSpan[]>(1024);
        readonly List<UndoEntry> _undo = new List<UndoEntry>();
        readonly List<UndoEntry> _redo = new List<UndoEntry>();

        int _caretLine;
        int _caretCol;
        int _anchorLine;
        int _anchorCol;
        int _wantCol = -1;
        int _version;
        int _framesLine = -1;
        bool _framesDirty = true;
        int _maxLength;
        bool _maxLengthDirty = true;

        public UktasTextBuffer()
        {
            Reset(string.Empty);
        }

        public int LineCount { get { return _lines.Count; } }
        public int CaretLine { get { return _caretLine; } }
        public int CaretCol { get { return _caretCol; } }
        public int AnchorLine { get { return _anchorLine; } }
        public int AnchorCol { get { return _anchorCol; } }
        public bool HasSelection { get { return _caretLine != _anchorLine || _caretCol != _anchorCol; } }
        public bool Modified { get; private set; }
        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        /// <summary>Increases on every change to the text (not on caret moves).</summary>
        public int Version { get { return _version; } }

        public string GetLine(int index)
        {
            return _lines[index];
        }

        public int MaxLineLength
        {
            get
            {
                if (_maxLengthDirty)
                {
                    int max = 0;
                    for (int i = 0; i < _lines.Count; i++)
                        if (_lines[i].Length > max)
                            max = _lines[i].Length;
                    _maxLength = max;
                    _maxLengthDirty = false;
                }
                return _maxLength;
            }
        }

        /// <summary>Index of the "frames" line, or -1. Lines below it are framebulks, lines above are header.</summary>
        public int FramesLine
        {
            get
            {
                if (_framesDirty)
                {
                    int found = -1;
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        if (UktasSyntax.IsFramesLine(_lines[i]))
                        {
                            found = i;
                            break;
                        }
                    }
                    if (found != _framesLine)
                    {
                        _framesLine = found;
                        for (int i = 0; i < _tokens.Count; i++)
                            _tokens[i] = null;
                    }
                    _framesDirty = false;
                }
                return _framesLine;
            }
        }

        public SyntaxSpan[] GetTokens(int line)
        {
            int frames = FramesLine;
            SyntaxSpan[] spans = _tokens[line];
            if (spans == null)
            {
                spans = UktasSyntax.Tokenize(_lines[line], frames >= 0 && line > frames);
                _tokens[line] = spans;
            }
            return spans;
        }

        // ------------------------------------------------------------------
        // Whole-text access
        // ------------------------------------------------------------------

        /// <summary>Replaces everything. Tabs become spaces so columns line up in a monospace font.</summary>
        public void SetText(string text)
        {
            Reset(text);
            Modified = false;
        }

        void Reset(string text)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n')
                .Replace("\t", new string(' ', TabSize));
            _lines.Clear();
            _tokens.Clear();
            _lines.AddRange(normalized.Split('\n'));
            for (int i = 0; i < _lines.Count; i++)
                _tokens.Add(null);
            _undo.Clear();
            _redo.Clear();
            _caretLine = _caretCol = _anchorLine = _anchorCol = 0;
            _wantCol = -1;
            _framesDirty = true;
            _maxLengthDirty = true;
            _version++;
        }

        public string GetText(string newline)
        {
            return string.Join(newline, _lines.ToArray());
        }

        public void MarkSaved()
        {
            Modified = false;
        }

        // ------------------------------------------------------------------
        // Selection and caret
        // ------------------------------------------------------------------

        /// <summary>The selection in document order. False (and the caret in both ends) when nothing is selected.</summary>
        public bool GetSelection(out int startLine, out int startCol, out int endLine, out int endCol)
        {
            if (_caretLine < _anchorLine || (_caretLine == _anchorLine && _caretCol <= _anchorCol))
            {
                startLine = _caretLine;
                startCol = _caretCol;
                endLine = _anchorLine;
                endCol = _anchorCol;
            }
            else
            {
                startLine = _anchorLine;
                startCol = _anchorCol;
                endLine = _caretLine;
                endCol = _caretCol;
            }
            return HasSelection;
        }

        public void SetCaret(int line, int col, bool extend)
        {
            line = Clamp(line, 0, _lines.Count - 1);
            col = Clamp(col, 0, _lines[line].Length);
            _caretLine = line;
            _caretCol = col;
            if (!extend)
            {
                _anchorLine = line;
                _anchorCol = col;
            }
            _wantCol = -1;
        }

        public void SetSelection(int anchorLine, int anchorCol, int caretLine, int caretCol)
        {
            anchorLine = Clamp(anchorLine, 0, _lines.Count - 1);
            anchorCol = Clamp(anchorCol, 0, _lines[anchorLine].Length);
            caretLine = Clamp(caretLine, 0, _lines.Count - 1);
            caretCol = Clamp(caretCol, 0, _lines[caretLine].Length);
            _anchorLine = anchorLine;
            _anchorCol = anchorCol;
            _caretLine = caretLine;
            _caretCol = caretCol;
            _wantCol = -1;
        }

        public void SelectAll()
        {
            _anchorLine = 0;
            _anchorCol = 0;
            _caretLine = _lines.Count - 1;
            _caretCol = _lines[_caretLine].Length;
            _wantCol = -1;
        }

        public void SelectLine(int line)
        {
            line = Clamp(line, 0, _lines.Count - 1);
            _anchorLine = line;
            _anchorCol = 0;
            if (line + 1 < _lines.Count)
            {
                _caretLine = line + 1;
                _caretCol = 0;
            }
            else
            {
                _caretLine = line;
                _caretCol = _lines[line].Length;
            }
            _wantCol = -1;
        }

        public void SelectWordAt(int line, int col)
        {
            line = Clamp(line, 0, _lines.Count - 1);
            string text = _lines[line];
            col = Clamp(col, 0, text.Length);
            int start = col;
            int end = col;
            if (text.Length > 0)
            {
                int probe = col < text.Length ? col : col - 1;
                int cls = CharClass(text[probe]);
                start = probe;
                end = probe + 1;
                while (start > 0 && CharClass(text[start - 1]) == cls)
                    start--;
                while (end < text.Length && CharClass(text[end]) == cls)
                    end++;
            }
            _anchorLine = line;
            _anchorCol = start;
            _caretLine = line;
            _caretCol = end;
            _wantCol = -1;
        }

        public string GetSelectedText()
        {
            int sl, sc, el, ec;
            if (!GetSelection(out sl, out sc, out el, out ec))
                return string.Empty;
            return RangeText(sl, sc, el, ec);
        }

        public void MoveLeft(bool extend, bool word)
        {
            // A plain Left with a selection collapses it to its start, like every editor does.
            if (!extend && HasSelection)
            {
                int sl, sc, el, ec;
                GetSelection(out sl, out sc, out el, out ec);
                SetCaret(sl, sc, false);
                return;
            }
            int line = _caretLine;
            int col = _caretCol;
            if (col == 0)
            {
                if (line > 0)
                {
                    line--;
                    col = _lines[line].Length;
                }
            }
            else if (word)
            {
                col = WordLeft(_lines[line], col);
            }
            else
            {
                col--;
            }
            SetCaret(line, col, extend);
        }

        public void MoveRight(bool extend, bool word)
        {
            if (!extend && HasSelection)
            {
                int sl, sc, el, ec;
                GetSelection(out sl, out sc, out el, out ec);
                SetCaret(el, ec, false);
                return;
            }
            int line = _caretLine;
            int col = _caretCol;
            if (col >= _lines[line].Length)
            {
                if (line + 1 < _lines.Count)
                {
                    line++;
                    col = 0;
                }
            }
            else if (word)
            {
                col = WordRight(_lines[line], col);
            }
            else
            {
                col++;
            }
            SetCaret(line, col, extend);
        }

        public void MoveVertical(int lines, bool extend)
        {
            int want = _wantCol >= 0 ? _wantCol : _caretCol;
            int target = _caretLine + lines;
            int col;
            if (target < 0)
            {
                target = 0;
                col = 0;
            }
            else if (target >= _lines.Count)
            {
                target = _lines.Count - 1;
                col = _lines[target].Length;
            }
            else
            {
                col = Math.Min(want, _lines[target].Length);
            }
            SetCaret(target, col, extend);
            _wantCol = want;
        }

        public void MoveHome(bool extend, bool document)
        {
            if (document)
            {
                SetCaret(0, 0, extend);
                return;
            }
            string text = _lines[_caretLine];
            int first = 0;
            while (first < text.Length && text[first] == ' ')
                first++;
            SetCaret(_caretLine, _caretCol == first ? 0 : first, extend);
        }

        public void MoveEnd(bool extend, bool document)
        {
            if (document)
                SetCaret(_lines.Count - 1, _lines[_lines.Count - 1].Length, extend);
            else
                SetCaret(_caretLine, _lines[_caretLine].Length, extend);
        }

        // ------------------------------------------------------------------
        // Editing
        // ------------------------------------------------------------------

        /// <summary>Types one character (replacing the selection). Consecutive characters share one undo step.</summary>
        public void TypeChar(char c)
        {
            if (c == '\t')
            {
                Tab(false);
                return;
            }
            if (c < ' ')
                return;
            string text = c.ToString();
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec))
                Replace(sl, sc, el, ec, text, KindNone);
            else
                Replace(_caretLine, _caretCol, _caretLine, _caretCol, text, KindTyping);
        }

        /// <summary>Inserts text at the caret, replacing the selection (paste).</summary>
        public void Paste(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", new string(' ', TabSize));
            int sl, sc, el, ec;
            GetSelection(out sl, out sc, out el, out ec);
            Replace(sl, sc, el, ec, normalized, KindNone);
        }

        public void Backspace(bool word)
        {
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec))
            {
                Replace(sl, sc, el, ec, string.Empty, KindNone);
                return;
            }
            if (_caretCol > 0)
            {
                int start = word ? WordLeft(_lines[_caretLine], _caretCol) : _caretCol - 1;
                Replace(_caretLine, start, _caretLine, _caretCol, string.Empty, word ? KindNone : KindBackspace);
            }
            else if (_caretLine > 0)
            {
                Replace(_caretLine - 1, _lines[_caretLine - 1].Length, _caretLine, 0, string.Empty, KindNone);
            }
        }

        public void Delete(bool word)
        {
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec))
            {
                Replace(sl, sc, el, ec, string.Empty, KindNone);
                return;
            }
            string text = _lines[_caretLine];
            if (_caretCol < text.Length)
            {
                int end = word ? WordRight(text, _caretCol) : _caretCol + 1;
                Replace(_caretLine, _caretCol, _caretLine, end, string.Empty, word ? KindNone : KindDelete);
            }
            else if (_caretLine + 1 < _lines.Count)
            {
                Replace(_caretLine, text.Length, _caretLine + 1, 0, string.Empty, KindNone);
            }
        }

        /// <summary>Enter: splits the line and keeps its indentation.</summary>
        public void NewLine()
        {
            string text = _lines[_caretLine];
            int indent = 0;
            while (indent < text.Length && indent < _caretCol && text[indent] == ' ')
                indent++;
            int sl, sc, el, ec;
            GetSelection(out sl, out sc, out el, out ec);
            Replace(sl, sc, el, ec, "\n" + new string(' ', indent), KindNone);
        }

        /// <summary>Tab indents the selected lines (or inserts spaces up to the next stop); Shift+Tab outdents.</summary>
        public void Tab(bool shift)
        {
            int sl, sc, el, ec;
            bool selection = GetSelection(out sl, out sc, out el, out ec);
            bool multiLine = selection && LastSelectedLine(sl, el, ec) > sl;
            if (shift)
            {
                Outdent();
                return;
            }
            if (multiLine)
            {
                Indent();
                return;
            }
            int col = selection ? sc : _caretCol;
            Replace(sl, sc, el, ec, new string(' ', TabSize - (col % TabSize)), KindNone);
        }

        /// <summary>Ctrl+K: puts "// " in front of the selected (or current) lines.</summary>
        public void CommentLines()
        {
            TransformLines(CommentOne);
        }

        /// <summary>Ctrl+Shift+K: removes one leading "//" or "#" (and a following space) from the lines.</summary>
        public void UncommentLines()
        {
            TransformLines(UncommentOne);
        }

        void Indent()
        {
            TransformLines(IndentOne);
        }

        void Outdent()
        {
            TransformLines(OutdentOne);
        }

        static string CommentOne(string line)
        {
            return line.Trim().Length == 0 ? line : "// " + line;
        }

        static string UncommentOne(string line)
        {
            int first = 0;
            while (first < line.Length && line[first] == ' ')
                first++;
            int remove = 0;
            if (string.CompareOrdinal(line, first, "//", 0, 2) == 0)
                remove = 2;
            else if (first < line.Length && line[first] == '#')
                remove = 1;
            if (remove == 0)
                return line;
            int strip = remove;
            if (first + strip < line.Length && line[first + strip] == ' ')
                strip++;
            return line.Substring(0, first) + line.Substring(first + strip);
        }

        static string IndentOne(string line)
        {
            return line.Length == 0 ? line : new string(' ', TabSize) + line;
        }

        static string OutdentOne(string line)
        {
            int remove = 0;
            while (remove < TabSize && remove < line.Length && line[remove] == ' ')
                remove++;
            return line.Substring(remove);
        }

        static int LastSelectedLine(int startLine, int endLine, int endCol)
        {
            // A selection that ends at column 0 of a line does not include that line.
            return endLine > startLine && endCol == 0 ? endLine - 1 : endLine;
        }

        void TransformLines(Func<string, string> transform)
        {
            int sl, sc, el, ec;
            bool selection = GetSelection(out sl, out sc, out el, out ec);
            int first = sl;
            int last = selection ? LastSelectedLine(sl, el, ec) : sl;

            StringBuilder before = new StringBuilder();
            StringBuilder after = new StringBuilder();
            for (int i = first; i <= last; i++)
            {
                string line = _lines[i];
                if (i > first)
                {
                    before.Append('\n');
                    after.Append('\n');
                }
                before.Append(line);
                after.Append(transform(line));
            }
            string oldText = before.ToString();
            string newText = after.ToString();
            if (oldText == newText)
                return;

            int caretCol = _caretCol;
            int caretLine = _caretLine;
            int delta = transform(_lines[caretLine]).Length - _lines[caretLine].Length;

            Replace(first, 0, last, _lines[last].Length, newText, KindNone);

            if (selection)
            {
                SetSelection(first, 0, last, _lines[last].Length);
            }
            else
            {
                SetCaret(caretLine, Math.Max(0, caretCol + delta), false);
            }
            SyncLastUndoAfter();
        }

        /// <summary>Ctrl+D: duplicates the selection, or the current line when nothing is selected.</summary>
        public void Duplicate()
        {
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec))
            {
                string text = RangeText(sl, sc, el, ec);
                Replace(el, ec, el, ec, text, KindNone);
                return;
            }
            string line = _lines[_caretLine];
            int col = _caretCol;
            int number = _caretLine;
            Replace(number, line.Length, number, line.Length, "\n" + line, KindNone);
            SetCaret(number + 1, col, false);
            SyncLastUndoAfter();
        }

        /// <summary>First and last line touched by the selection (or the caret line). A selection that ends at column 0 leaves that line out.</summary>
        void LineSpan(out int first, out int last)
        {
            int sl, sc, el, ec;
            bool selection = GetSelection(out sl, out sc, out el, out ec);
            first = sl;
            last = selection ? LastSelectedLine(sl, el, ec) : sl;
        }

        /// <summary>Alt+Up / Alt+Down: moves the selected (or current) lines one line up (-1) or down (+1). One undo step.</summary>
        public bool MoveLines(int direction)
        {
            int first, last;
            LineSpan(out first, out last);
            if (direction < 0 ? first == 0 : last == _lines.Count - 1)
                return false;

            int aL = _anchorLine, aC = _anchorCol, cL = _caretLine, cC = _caretCol;
            int from = direction < 0 ? first - 1 : first;
            int to = direction < 0 ? last : last + 1;

            StringBuilder block = new StringBuilder();
            if (direction < 0)
            {
                for (int i = first; i <= last; i++)
                    block.Append(_lines[i]).Append('\n');
                block.Append(_lines[first - 1]);
            }
            else
            {
                block.Append(_lines[last + 1]).Append('\n');
                for (int i = first; i <= last; i++)
                {
                    block.Append(_lines[i]);
                    if (i < last)
                        block.Append('\n');
                }
            }

            Replace(from, 0, to, _lines[to].Length, block.ToString(), KindNone);
            SetSelection(aL + direction, aC, cL + direction, cC);
            SyncLastUndoAfter();
            return true;
        }

        /// <summary>Ctrl+Shift+L: removes the selected (or current) lines completely. One undo step.</summary>
        public void DeleteLines()
        {
            int first, last;
            LineSpan(out first, out last);
            int col = _caretCol;
            int versionBefore = _version;
            if (last + 1 < _lines.Count)
                Replace(first, 0, last + 1, 0, string.Empty, KindNone);
            else if (first > 0)
                Replace(first - 1, _lines[first - 1].Length, last, _lines[last].Length, string.Empty, KindNone);
            else
                Replace(0, 0, last, _lines[last].Length, string.Empty, KindNone);
            int line = Math.Min(first, _lines.Count - 1);
            SetCaret(line, Math.Min(col, _lines[line].Length), false);
            if (_version != versionBefore)
                SyncLastUndoAfter();
        }

        /// <summary>Ctrl+L: selects the current line; pressed again it adds the next line to the selection.</summary>
        public void SelectLineExtend()
        {
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec) && sc == 0 && ec == 0 && el > sl)
            {
                if (el + 1 < _lines.Count)
                    SetSelection(sl, 0, el + 1, 0);
                else
                    SetSelection(sl, 0, el, _lines[el].Length);
                return;
            }
            SelectLine(_caretLine);
        }

        /// <summary>
        /// Ctrl+Enter / Ctrl+Shift+Enter: opens an empty line below (or above) the current one, keeps the
        /// indentation and puts the caret on it. One undo step.
        /// </summary>
        public void InsertBlankLine(bool above)
        {
            int number = _caretLine;
            string text = _lines[number];
            int indent = 0;
            while (indent < text.Length && text[indent] == ' ')
                indent++;
            string pad = new string(' ', indent);
            if (above)
            {
                Replace(number, 0, number, 0, pad + "\n", KindNone);
                SetCaret(number, indent, false);
            }
            else
            {
                Replace(number, text.Length, number, text.Length, "\n" + pad, KindNone);
                SetCaret(number + 1, indent, false);
            }
            SyncLastUndoAfter();
        }

        /// <summary>
        /// Selects the next (or previous) occurrence of a single-line text, wrapping around the file.
        /// With <paramref name="stayOnCurrent"/> a match that starts at the selection counts, which is what
        /// find-as-you-type needs. Returns false and leaves the caret alone when there is no match.
        /// </summary>
        public bool Find(string needle, bool forward, bool matchCase, bool stayOnCurrent)
        {
            if (string.IsNullOrEmpty(needle) || needle.IndexOf('\n') >= 0)
                return false;
            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int sl, sc, el, ec;
            GetSelection(out sl, out sc, out el, out ec);
            int count = _lines.Count;

            if (forward)
            {
                int line = stayOnCurrent ? sl : el;
                int col = stayOnCurrent ? sc : ec;
                for (int pass = 0; pass <= count; pass++)
                {
                    string text = _lines[line];
                    int index = col <= text.Length ? text.IndexOf(needle, col, comparison) : -1;
                    if (index >= 0)
                    {
                        SetSelection(line, index, line, index + needle.Length);
                        return true;
                    }
                    line = (line + 1) % count;
                    col = 0;
                }
            }
            else
            {
                int line = sl;
                int limit = sc;
                for (int pass = 0; pass <= count; pass++)
                {
                    string text = _lines[line];
                    string head = limit < text.Length ? text.Substring(0, limit) : text;
                    int index = head.LastIndexOf(needle, comparison);
                    if (index >= 0)
                    {
                        SetSelection(line, index, line, index + needle.Length);
                        return true;
                    }
                    line = (line + count - 1) % count;
                    limit = _lines[line].Length;
                }
            }
            return false;
        }

        /// <summary>
        /// Text for the clipboard. With no selection the whole current line is copied, and cutting
        /// removes that line, as in Notepad++.
        /// </summary>
        public string CopyText(bool cut)
        {
            int sl, sc, el, ec;
            if (GetSelection(out sl, out sc, out el, out ec))
            {
                string text = RangeText(sl, sc, el, ec);
                if (cut)
                    Replace(sl, sc, el, ec, string.Empty, KindNone);
                return text;
            }

            int line = _caretLine;
            string whole = _lines[line] + "\n";
            if (cut)
            {
                if (line + 1 < _lines.Count)
                    Replace(line, 0, line + 1, 0, string.Empty, KindNone);
                else if (line > 0)
                    Replace(line - 1, _lines[line - 1].Length, line, _lines[line].Length, string.Empty, KindNone);
                else
                    Replace(0, 0, 0, _lines[0].Length, string.Empty, KindNone);
            }
            return whole;
        }

        // ------------------------------------------------------------------
        // Undo / redo
        // ------------------------------------------------------------------

        public bool Undo()
        {
            if (_undo.Count == 0)
                return false;
            UndoEntry entry = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            int endLine, endCol;
            EndOf(entry.Line, entry.Col, entry.Inserted, out endLine, out endCol);
            int ignoredLine, ignoredCol;
            ApplyRaw(entry.Line, entry.Col, endLine, endCol, entry.Removed, out ignoredLine, out ignoredCol);
            SetSelection(entry.BeforeAnchorLine, entry.BeforeAnchorCol, entry.BeforeCaretLine, entry.BeforeCaretCol);
            entry.Kind = KindNone;
            _redo.Add(entry);
            Modified = true;
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0)
                return false;
            UndoEntry entry = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            int endLine, endCol;
            EndOf(entry.Line, entry.Col, entry.Removed, out endLine, out endCol);
            int ignoredLine, ignoredCol;
            ApplyRaw(entry.Line, entry.Col, endLine, endCol, entry.Inserted, out ignoredLine, out ignoredCol);
            SetSelection(entry.AfterAnchorLine, entry.AfterAnchorCol, entry.AfterCaretLine, entry.AfterCaretCol);
            entry.Kind = KindNone;
            _undo.Add(entry);
            Modified = true;
            return true;
        }

        void SyncLastUndoAfter()
        {
            if (_undo.Count == 0)
                return;
            UndoEntry top = _undo[_undo.Count - 1];
            top.AfterCaretLine = _caretLine;
            top.AfterCaretCol = _caretCol;
            top.AfterAnchorLine = _anchorLine;
            top.AfterAnchorCol = _anchorCol;
        }

        // ------------------------------------------------------------------
        // Core edit
        // ------------------------------------------------------------------

        /// <summary>Replaces the range (in either order) with text, records undo and puts the caret after the new text.</summary>
        void Replace(int l1, int c1, int l2, int c2, string text, int kind)
        {
            if (l1 > l2 || (l1 == l2 && c1 > c2))
            {
                int tl = l1;
                int tc = c1;
                l1 = l2;
                c1 = c2;
                l2 = tl;
                c2 = tc;
            }
            string removed = RangeText(l1, c1, l2, c2);
            if (removed.Length == 0 && text.Length == 0)
                return;

            UndoEntry entry = new UndoEntry();
            entry.Line = l1;
            entry.Col = c1;
            entry.Removed = removed;
            entry.Inserted = text;
            entry.BeforeCaretLine = _caretLine;
            entry.BeforeCaretCol = _caretCol;
            entry.BeforeAnchorLine = _anchorLine;
            entry.BeforeAnchorCol = _anchorCol;
            entry.Kind = kind;
            entry.Stamp = Environment.TickCount;

            int endLine, endCol;
            ApplyRaw(l1, c1, l2, c2, text, out endLine, out endCol);
            _caretLine = _anchorLine = endLine;
            _caretCol = _anchorCol = endCol;
            _wantCol = -1;
            entry.AfterCaretLine = entry.AfterAnchorLine = endLine;
            entry.AfterCaretCol = entry.AfterAnchorCol = endCol;

            PushUndo(entry);
            _redo.Clear();
            Modified = true;
        }

        void PushUndo(UndoEntry entry)
        {
            if (entry.Kind != KindNone && _undo.Count > 0)
            {
                UndoEntry top = _undo[_undo.Count - 1];
                if (top.Kind == entry.Kind && unchecked(entry.Stamp - top.Stamp) < MergeWindowMs && TryMerge(top, entry))
                    return;
            }
            _undo.Add(entry);
            if (_undo.Count > MaxUndo)
                _undo.RemoveAt(0);
        }

        static bool TryMerge(UndoEntry top, UndoEntry entry)
        {
            if (entry.Kind == KindTyping)
            {
                if (top.Removed.Length != 0 || top.Inserted.IndexOf('\n') >= 0 || top.Inserted.Length >= MaxMergedLength)
                    return false;
                if (entry.Line != top.Line || entry.Col != top.Col + top.Inserted.Length)
                    return false;
                top.Inserted += entry.Inserted;
            }
            else if (entry.Kind == KindBackspace)
            {
                if (top.Inserted.Length != 0 || entry.Removed.IndexOf('\n') >= 0 || top.Removed.Length >= MaxMergedLength)
                    return false;
                if (entry.Line != top.Line || entry.Col + entry.Removed.Length != top.Col)
                    return false;
                top.Col = entry.Col;
                top.Removed = entry.Removed + top.Removed;
            }
            else if (entry.Kind == KindDelete)
            {
                if (top.Inserted.Length != 0 || entry.Removed.IndexOf('\n') >= 0 || top.Removed.Length >= MaxMergedLength)
                    return false;
                if (entry.Line != top.Line || entry.Col != top.Col)
                    return false;
                top.Removed += entry.Removed;
            }
            else
            {
                return false;
            }
            top.AfterCaretLine = entry.AfterCaretLine;
            top.AfterCaretCol = entry.AfterCaretCol;
            top.AfterAnchorLine = entry.AfterAnchorLine;
            top.AfterAnchorCol = entry.AfterAnchorCol;
            top.Stamp = entry.Stamp;
            return true;
        }

        string RangeText(int l1, int c1, int l2, int c2)
        {
            if (l1 == l2)
                return _lines[l1].Substring(c1, c2 - c1);
            StringBuilder sb = new StringBuilder();
            sb.Append(_lines[l1], c1, _lines[l1].Length - c1);
            for (int i = l1 + 1; i < l2; i++)
            {
                sb.Append('\n');
                sb.Append(_lines[i]);
            }
            sb.Append('\n');
            sb.Append(_lines[l2], 0, c2);
            return sb.ToString();
        }

        static void EndOf(int line, int col, string text, out int endLine, out int endCol)
        {
            int newlines = 0;
            int lastBreak = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    newlines++;
                    lastBreak = i;
                }
            }
            if (newlines == 0)
            {
                endLine = line;
                endCol = col + text.Length;
            }
            else
            {
                endLine = line + newlines;
                endCol = text.Length - lastBreak - 1;
            }
        }

        void ApplyRaw(int l1, int c1, int l2, int c2, string text, out int endLine, out int endCol)
        {
            string head = _lines[l1].Substring(0, c1);
            string tail = _lines[l2].Substring(c2);
            string[] parts = text.Split('\n');
            parts[0] = head + parts[0];
            endLine = l1 + parts.Length - 1;
            endCol = parts[parts.Length - 1].Length;
            parts[parts.Length - 1] = parts[parts.Length - 1] + tail;

            _lines.RemoveRange(l1, l2 - l1 + 1);
            _lines.InsertRange(l1, parts);
            _tokens.RemoveRange(l1, l2 - l1 + 1);
            _tokens.InsertRange(l1, new SyntaxSpan[parts.Length][]);

            _version++;
            _framesDirty = true;
            _maxLengthDirty = true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        static int CharClass(char c)
        {
            if (c == ' ' || c == '\t')
                return 0;
            if (char.IsLetterOrDigit(c) || c == '_')
                return 1;
            return 2;
        }

        static int WordLeft(string text, int col)
        {
            int i = col;
            while (i > 0 && CharClass(text[i - 1]) == 0)
                i--;
            if (i > 0)
            {
                int cls = CharClass(text[i - 1]);
                while (i > 0 && CharClass(text[i - 1]) == cls)
                    i--;
            }
            return i;
        }

        static int WordRight(string text, int col)
        {
            int i = col;
            if (i < text.Length && CharClass(text[i]) != 0)
            {
                int cls = CharClass(text[i]);
                while (i < text.Length && CharClass(text[i]) == cls)
                    i++;
            }
            while (i < text.Length && CharClass(text[i]) == 0)
                i++;
            return i;
        }
    }
}
