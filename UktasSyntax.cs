using System;
using System.Collections.Generic;

namespace UltrakillTAS
{
    /// <summary>One coloured run inside a single line of a .uktas script.</summary>
    public struct SyntaxSpan
    {
        public int Start;
        public int Length;
        public byte Kind;
        /// <summary>The covered text, kept so drawing never allocates.</summary>
        public string Text;

        public SyntaxSpan(int start, int length, byte kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
            Text = null;
        }
    }

    /// <summary>
    /// Line tokenizer for the .uktas / .hltas script format (the same groups as the
    /// Notepad++ definition in uktas.xml): comments, header keys, numbers, enum values,
    /// commands, special-frame keywords, and the pipe separated framebulk columns.
    /// Pure string code with no Unity dependency, so it can be tested on its own.
    /// </summary>
    public static class UktasSyntax
    {
        public const byte Plain = 0;
        public const byte Comment = 1;
        public const byte Number = 2;
        public const byte HeaderKey = 3;
        public const byte Value = 4;
        public const byte Command = 5;
        public const byte Special = 6;
        public const byte Modifier = 7;
        public const byte Pipe = 8;
        public const byte Dim = 9;
        public const byte Autofunc = 10;
        public const byte Move = 11;
        public const byte Action = 12;
        public const int KindCount = 13;

        const int ModeHeader = 0;
        const int ModeSpecial = 1;
        const int ModeArgs = 2;

        static readonly HashSet<string> HeaderKeys = Set(
            "version", "seed", "rng_seed", "random_seed", "host_frametime", "frametime", "default_frametime", "ft",
            "demo", "save", "savestate", "start_type", "starttype", "start", "load", "load_level", "loadlevel",
            "load_command", "loadcommand", "wait_for_control", "wait_for_player_control", "wait_for_player",
            "waitforcontrol", "fist", "start_fist", "difficulty", "weapons", "loadout", "author", "description",
            "desc", "record_video", "record_fps", "blend_samples", "shutter_angle");

        static readonly HashSet<string> Values = Set(
            "none", "current", "restart_mission", "main_menu", "load_level", "load_state", "true", "false",
            "harmless", "lenient", "standard", "violent", "brutal", "knuckleblaster", "parry");

        static readonly HashSet<string> SpecialWords = Set(
            "save", "seed", "target_yaw", "fist", "norefresh", "ff", "fff", "change", "look_at", "yaw", "pitch",
            "to", "over", "bake_start", "bake_start_nme", "bake_end", "record_video", "record_video_stop");

        static readonly HashSet<string> Modifiers = Set("once", "left", "right", "middle", "lmb", "rmb", "mmb");

        static HashSet<string> Set(params string[] items)
        {
            return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Index where a // or # comment starts (whichever comes first), or -1. Mirrors the
        /// parser's own comment rule, so what is coloured as a comment is what is ignored.
        /// </summary>
        public static int CommentStart(string line)
        {
            if (line == null)
                return -1;
            int cut = -1;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '/' && line[i + 1] == '/')
                {
                    cut = i;
                    break;
                }
            }
            int hash = line.IndexOf('#');
            if (hash >= 0 && (cut < 0 || hash < cut))
                cut = hash;
            return cut;
        }

        /// <summary>True for the bare "frames" line that ends the header.</summary>
        public static bool IsFramesLine(string line)
        {
            if (line == null)
                return false;
            int comment = CommentStart(line);
            string code = comment >= 0 ? line.Substring(0, comment) : line;
            return string.Equals(code.Trim(), "frames", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Splits one line into coloured runs. <paramref name="afterFrames"/> is true below the "frames" line.</summary>
        public static SyntaxSpan[] Tokenize(string line, bool afterFrames)
        {
            if (string.IsNullOrEmpty(line))
                return new SyntaxSpan[0];

            List<SyntaxSpan> spans = new List<SyntaxSpan>(16);
            int comment = CommentStart(line);
            int end = comment >= 0 ? comment : line.Length;
            if (end > 0)
            {
                if (IsFramesLine(line))
                {
                    Add(spans, 0, end, HeaderKey);
                }
                else if (afterFrames && line.IndexOf('|', 0, end) >= 0)
                {
                    Framebulk(spans, line, end);
                }
                else if (afterFrames)
                {
                    Words(spans, line, 0, end, ModeSpecial);
                }
                else
                {
                    Header(spans, line, end);
                }
            }
            if (comment >= 0)
                Add(spans, comment, line.Length - comment, Comment);

            SyntaxSpan[] result = spans.ToArray();
            for (int i = 0; i < result.Length; i++)
                result[i].Text = line.Substring(result[i].Start, result[i].Length);
            return result;
        }

        static void Add(List<SyntaxSpan> spans, int start, int length, byte kind)
        {
            if (length <= 0)
                return;
            int last = spans.Count - 1;
            if (last >= 0 && spans[last].Kind == kind && spans[last].Start + spans[last].Length == start)
            {
                SyntaxSpan merged = spans[last];
                merged.Length += length;
                spans[last] = merged;
                return;
            }
            spans.Add(new SyntaxSpan(start, length, kind));
        }

        // ------------------------------------------------------------------
        // Framebulk: autofuncs|movement|actions|frametime|yaw|pitch|frames|commands
        // ------------------------------------------------------------------

        static void Framebulk(List<SyntaxSpan> spans, string line, int end)
        {
            int field = 0;
            int start = 0;
            for (int i = 0; i <= end; i++)
            {
                bool boundary = i == end || (line[i] == '|' && field < 7);
                if (!boundary)
                    continue;
                if (field == 7)
                    Commands(spans, line, start, i);
                else
                    Field(spans, line, start, i, field);
                if (i < end)
                    Add(spans, i, 1, Pipe);
                field++;
                start = i + 1;
            }
        }

        static void Field(List<SyntaxSpan> spans, string line, int start, int end, int field)
        {
            if (field >= 3)
            {
                // Numeric columns: a lone '-' means "inherit", anything else is a value.
                string text = line.Substring(start, end - start).Trim();
                Add(spans, start, end - start, text == "-" || text.Length == 0 ? Dim : Number);
                return;
            }

            byte letter = field == 0 ? Autofunc : (field == 1 ? Move : Action);
            byte previous = Dim;
            for (int i = start; i < end; i++)
            {
                char c = line[i];
                byte kind;
                if (c == '-')
                    kind = Dim;
                else if (c == ' ' || c == '\t')
                    kind = previous;
                else
                    kind = letter;
                previous = kind;
                Add(spans, i, 1, kind);
            }
        }

        // ------------------------------------------------------------------
        // Commands: "once:glidef 0.5 0.4 40; click"
        // ------------------------------------------------------------------

        static void Commands(List<SyntaxSpan> spans, string line, int start, int end)
        {
            int segment = start;
            for (int i = start; i <= end; i++)
            {
                if (i != end && line[i] != ';')
                    continue;
                OneCommand(spans, line, segment, i);
                if (i < end)
                    Add(spans, i, 1, Pipe);
                segment = i + 1;
            }
        }

        static void OneCommand(List<SyntaxSpan> spans, string line, int start, int end)
        {
            int p = start;
            while (p < end && char.IsWhiteSpace(line[p]))
                p++;
            if (p >= end)
                return;

            if (end - p >= 5 && string.Compare(line, p, "once:", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                Add(spans, p, 5, Modifier);
                p += 5;
                while (p < end && char.IsWhiteSpace(line[p]))
                    p++;
                if (p >= end)
                    return;
            }

            int q = p;
            while (q < end && !char.IsWhiteSpace(line[q]))
                q++;
            Add(spans, p, q - p, Command);
            Words(spans, line, q, end, ModeArgs);
        }

        // ------------------------------------------------------------------
        // Header lines and special frames
        // ------------------------------------------------------------------

        static void Header(List<SyntaxSpan> spans, string line, int end)
        {
            int p = 0;
            while (p < end && char.IsWhiteSpace(line[p]))
                p++;
            int keyEnd = p;
            while (keyEnd < end && !char.IsWhiteSpace(line[keyEnd]) && line[keyEnd] != ':' && line[keyEnd] != '=')
                keyEnd++;
            string key = line.Substring(p, keyEnd - p);
            Add(spans, p, keyEnd - p, HeaderKeys.Contains(key) ? HeaderKey : Plain);

            int valueStart = keyEnd;
            while (valueStart < end && (char.IsWhiteSpace(line[valueStart]) || line[valueStart] == ':' || line[valueStart] == '='))
                valueStart++;
            // The separator (space, ':' or '=') is drawn too, so no character is ever skipped.
            Add(spans, keyEnd, valueStart - keyEnd, Plain);
            if (valueStart >= end)
                return;

            if (key.Equals("load_command", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("loadcommand", StringComparison.OrdinalIgnoreCase))
                Commands(spans, line, valueStart, end);
            else
                Words(spans, line, valueStart, end, ModeHeader);
        }

        static void Words(List<SyntaxSpan> spans, string line, int start, int end, int mode)
        {
            int index = 0;
            int i = start;
            while (i < end)
            {
                while (i < end && char.IsWhiteSpace(line[i]))
                    i++;
                if (i >= end)
                    break;
                int wordStart = i;
                while (i < end && !char.IsWhiteSpace(line[i]))
                    i++;
                int length = i - wordStart;
                string word = line.Substring(wordStart, length);

                byte kind = Plain;
                if (IsNumber(word))
                    kind = Number;
                else if (mode == ModeSpecial && SpecialWords.Contains(word) && (index == 0 || IsSecondaryKeyword(word)))
                    kind = Special;
                else if (mode == ModeHeader && Values.Contains(word))
                    kind = Value;
                else if (mode == ModeArgs && Modifiers.Contains(word))
                    kind = Modifier;
                else if (mode == ModeArgs && (word.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                              word.Equals("false", StringComparison.OrdinalIgnoreCase)))
                    kind = Value;
                else if (mode == ModeSpecial && Modifiers.Contains(word))
                    kind = Modifier;

                Add(spans, wordStart, length, kind);
                index++;
            }
        }

        static bool IsSecondaryKeyword(string word)
        {
            return word.Equals("yaw", StringComparison.OrdinalIgnoreCase) ||
                   word.Equals("pitch", StringComparison.OrdinalIgnoreCase) ||
                   word.Equals("to", StringComparison.OrdinalIgnoreCase) ||
                   word.Equals("over", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsNumber(string word)
        {
            bool digit = false;
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c >= '0' && c <= '9')
                    digit = true;
                else if (c != '.' && c != '+' && c != '-')
                    return false;
            }
            return digit;
        }
    }
}
