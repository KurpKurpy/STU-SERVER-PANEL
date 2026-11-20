using System.Globalization;

namespace ArmaFormats;

// A parsed config text that remembers where every item came from, so anything the caller doesn't manage
// can be written back byte-for-byte. The top-level items cover the whole text, in order.
public sealed class ConfigDocument
{
    private ConfigDocument(string text, IReadOnlyList<ConfigItem> items) => (Text, Items) = (text, items);

    public string Text { get; }
    public IReadOnlyList<ConfigItem> Items { get; }

    public static ConfigDocument Parse(string text) => new(text, new Parser(text).Document());

    public string SourceOf(ConfigItem item) => Text.Substring(item.Start, item.Length);

    // The whole text with that top-level class (case-insensitive) replaced, or appended when it isn't there.
    public string ReplaceClass(string name, string classText)
    {
        var replacement = classText.EndsWith('\n') ? classText : classText + ConfigText.NewLine;
        var cls = Items.OfType<CfgClass>().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (cls is not null) return Text[..cls.Start] + replacement + Text[(cls.Start + cls.Length)..];
        var separator = Text.Length > 0 && !Text.EndsWith('\n') ? ConfigText.NewLine : "";
        return Text + separator + replacement;
    }

    private sealed class Parser(string s)
    {
        // Classes and arrays recurse; without a cap a hostile nest overflows the stack, which .NET can't catch.
        private const int MaxDepth = 64;
        private int _depth;
        private int _pos;

        private void Enter(int line)
        {
            if (++_depth > MaxDepth) throw new ConfigSyntaxException(line, $"classes or arrays are nested more than {MaxDepth} levels deep.");
        }
        private readonly List<int> _newlines = Enumerable.Range(0, s.Length).Where(i => s[i] == '\n').ToList();

        private int LineAt(int position)
        {
            var i = _newlines.BinarySearch(position);
            return (i < 0 ? ~i : i) + 1;
        }

        private char Peek => _pos < s.Length ? s[_pos] : '\0';
        private char PeekAt(int offset) => _pos + offset < s.Length ? s[_pos + offset] : '\0';
        private ConfigSyntaxException Error(int line, string message) => new(line, message);

        public List<ConfigItem> Document() => Items(topLevel: true, openLine: 0);

        private List<ConfigItem> Items(bool topLevel, int openLine)
        {
            var items = new List<ConfigItem>();
            while (true)
            {
                var start = _pos;
                SkipTrivia();
                if (_pos > start) items.Add(new CfgTrivia(LineAt(start), start, _pos - start));
                if (_pos >= s.Length)
                {
                    if (!topLevel) throw Error(openLine, "this class's '{' is never closed.");
                    return items;
                }
                if (Peek == '}')
                {
                    if (topLevel) throw Error(LineAt(_pos), "unexpected '}'.");
                    return items;
                }
                items.Add(Statement());
            }
        }

        // Whitespace, // and /* */ comments, and # preprocessor lines.
        private void SkipTrivia()
        {
            while (_pos < s.Length)
            {
                if (char.IsWhiteSpace(Peek)) _pos++;
                else if (Peek == '/' && PeekAt(1) == '/') SkipToLineEnd();
                else if (Peek == '/' && PeekAt(1) == '*') SkipBlockComment();
                else if (Peek == '#') SkipToLineEnd();
                else return;
            }
        }

        private void SkipToLineEnd()
        {
            while (_pos < s.Length && s[_pos] != '\n') _pos++;
            if (_pos < s.Length) _pos++;
        }

        private void SkipBlockComment()
        {
            var line = LineAt(_pos);
            var end = s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
            if (end < 0) throw Error(line, "a /* comment is never closed.");
            _pos = end + 2;
        }

        // Spaces, tabs and newlines, plus comments, between the tokens of one statement.
        private void SkipGap()
        {
            while (_pos < s.Length)
            {
                if (char.IsWhiteSpace(Peek)) _pos++;
                else if (Peek == '/' && PeekAt(1) == '/') SkipToLineEnd();
                else if (Peek == '/' && PeekAt(1) == '*') SkipBlockComment();
                else return;
            }
        }

        private void SkipSpaces()
        {
            while (Peek is ' ' or '\t') _pos++;
        }

        private string? Identifier()
        {
            var start = _pos;
            while (_pos < s.Length && (char.IsAsciiLetterOrDigit(s[_pos]) || s[_pos] == '_')) _pos++;
            return _pos > start ? s[start.._pos] : null;
        }

        private void Expect(char c, int line, string what)
        {
            if (Peek != c) throw Error(line, $"expected '{c}' {what}.");
            _pos++;
        }

        // After a statement's ';': trailing spaces and one newline belong to it; a comment after it is trivia.
        private void EndOfStatement()
        {
            var save = _pos;
            SkipSpaces();
            if (Peek == '\r' && PeekAt(1) == '\n') _pos += 2;
            else if (Peek == '\n') _pos++;
            else if (Peek != '\0') _pos = save;
        }

        private ConfigItem Statement()
        {
            var start = _pos;
            var line = LineAt(_pos);
            var name = Identifier() ?? throw Error(line, $"expected a setting or a class, found '{Peek}'.");

            if (name == "class") // Arma accepts only the lower-case keyword (Server Config File page)
            {
                SkipGap();
                var className = Identifier() ?? throw Error(LineAt(_pos), "expected a class name.");
                SkipGap();
                string? baseName = null;
                if (Peek == ':')
                {
                    _pos++;
                    SkipGap();
                    baseName = Identifier() ?? throw Error(LineAt(_pos), $"expected the base class of {className}.");
                    SkipGap();
                }
                if (Peek == ';')
                {
                    _pos++;
                    EndOfStatement();
                    return new CfgClass(className, baseName, null, line, start, _pos - start);
                }
                Expect('{', LineAt(_pos), $"after class {className}");
                Enter(line);
                var body = Items(topLevel: false, openLine: line);
                _depth--;
                _pos++; // the closing '}'
                SkipSpaces();
                Expect(';', LineAt(_pos), $"after the '}}' of class {className}");
                EndOfStatement();
                return new CfgClass(className, baseName, body, line, start, _pos - start);
            }

            SkipSpaces();
            var isArray = false;
            if (Peek == '[')
            {
                _pos++;
                SkipSpaces();
                Expect(']', LineAt(_pos), $"after {name}[");
                isArray = true;
                SkipSpaces();
            }
            var append = false;
            if (Peek == '+' && PeekAt(1) == '=')
            {
                if (!isArray) throw Error(line, $"+= needs an array ({name}[]).");
                append = true;
                _pos += 2;
            }
            else Expect('=', LineAt(_pos), $"after {name}");
            SkipGap();

            ConfigValue value = isArray ? Array(LineAt(_pos)) : Scalar(name);
            SkipSpaces();
            Expect(';', LineAt(_pos), $"after the value of {name}");
            EndOfStatement();
            return new CfgAssignment(name, isArray, append, value, line, start, _pos - start);
        }

        private CfgString QuotedString()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                QuotedPiece(sb);
                // Eden joins the pieces of a multi-line string with a bare \n token: "a" \n "b" (Phase 4 corpus gate).
                var save = _pos;
                SkipSpaces();
                if (_pos + 1 < s.Length && s[_pos] == '\\' && s[_pos + 1] == 'n')
                {
                    _pos += 2;
                    SkipSpaces();
                    if (Peek == '"') { sb.Append('\n'); continue; }
                }
                _pos = save;
                return new CfgString(sb.ToString());
            }
        }

        private void QuotedPiece(System.Text.StringBuilder sb)
        {
            var line = LineAt(_pos);
            _pos++; // opening quote
            while (true)
            {
                if (_pos >= s.Length) throw Error(line, "a quoted string is never closed.");
                var c = s[_pos++];
                if (c != '"') { sb.Append(c); continue; }
                if (Peek == '"') { sb.Append('"'); _pos++; continue; }
                return;
            }
        }

        // An unquoted value runs to the ';' on the same line.
        private ConfigValue Scalar(string name)
        {
            if (Peek == '"') return QuotedString();
            var start = _pos;
            while (_pos < s.Length && s[_pos] is not (';' or '\r' or '\n')) _pos++;
            var raw = s[start.._pos].Trim();
            if (raw.Length == 0) throw Error(LineAt(start), $"{name} has no value.");
            if (Peek != ';') throw Error(LineAt(_pos), $"expected ';' after the value of {name}.");
            _pos -= s[start.._pos].Length - s[start.._pos].TrimEnd().Length; // leave trailing spaces for SkipSpaces
            return Bare(raw);
        }

        private static ConfigValue Bare(string raw) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? new CfgNumber(number, raw) : new CfgWord(raw);

        private CfgArray Array(int openLine)
        {
            Expect('{', openLine, "to start the array");
            Enter(openLine);
            var items = new List<ConfigValue>();
            while (true)
            {
                SkipGap();
                if (_pos >= s.Length) throw Error(openLine, "this array's '{' is never closed.");
                if (Peek == '}') { _pos++; _depth--; return new CfgArray(items); }
                if (Peek == '{') items.Add(Array(LineAt(_pos)));
                else if (Peek == '"') items.Add(QuotedString());
                else
                {
                    var start = _pos;
                    while (_pos < s.Length && s[_pos] is not (',' or '}' or '\r' or '\n') && !(s[_pos] == '/' && PeekAt(1) is '/' or '*')) _pos++;
                    var raw = s[start.._pos].Trim();
                    if (raw.Length == 0) throw Error(LineAt(start), "an array has an empty item.");
                    items.Add(Bare(raw));
                }
                SkipGap();
                if (Peek == ',') { _pos++; continue; }
                if (Peek != '}') throw Error(LineAt(_pos), "expected ',' or '}' in an array.");
            }
        }
    }
}
