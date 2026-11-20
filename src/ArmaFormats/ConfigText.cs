using System.Globalization;

namespace ArmaFormats;

// Values in Arma's config text (server.cfg, basic.cfg, .Arma3Profile, text mission.sqm).
public abstract record ConfigValue;
public sealed record CfgString(string Value) : ConfigValue;           // "a ""b""" → a "b"
public sealed record CfgNumber(double Value, string Raw) : ConfigValue; // Raw keeps the source spelling; "" when generated
public sealed record CfgWord(string Value) : ConfigValue;              // unquoted token, e.g. true
public sealed record CfgArray(IReadOnlyList<ConfigValue> Items) : ConfigValue;

// Start/Length: the item's exact source slice. A statement's slice includes its trailing newline; comments after it are trivia.
public abstract record ConfigItem(int Line, int Start, int Length);
public sealed record CfgAssignment(string Name, bool IsArray, bool Append, ConfigValue Value, int Line, int Start, int Length) : ConfigItem(Line, Start, Length);
public sealed record CfgClass(string Name, string? Base, IReadOnlyList<ConfigItem>? Body, int Line, int Start, int Length) : ConfigItem(Line, Start, Length);
public sealed record CfgTrivia(int Line, int Start, int Length) : ConfigItem(Line, Start, Length);

public sealed class ConfigSyntaxException(int line, string message) : Exception($"Line {line}: {message}")
{
    public int Line { get; } = line;
}

public static class ConfigText
{
    public const string NewLine = "\r\n";

    public static string Format(ConfigValue value) => value switch
    {
        // A line break becomes Eden's "a" \n "b" join, so the text stays one statement per line.
        CfgString s => string.Join(" \\n ", s.Value.Split('\n').Select(piece => "\"" + piece.Replace("\"", "\"\"") + "\"")),
        CfgNumber n => string.IsNullOrEmpty(n.Raw) ? FormatNumber(n.Value) : n.Raw,
        CfgWord w => w.Value,
        CfgArray a => "{" + string.Join(", ", a.Items.Select(Format)) + "}",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    // Invariant and never in exponent form: 10000000000, 0.001.
    public static string FormatNumber(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    // `name = v;` or `name[] = {…};`; compact is the server's own `name=v;` style (spike F20).
    public static string Assignment(string name, ConfigValue value, bool compact = false)
    {
        var left = value is CfgArray ? name + "[]" : name;
        return compact ? $"{left}={Format(value)};" : $"{left} = {Format(value)};";
    }

    // The shortest text that reads back as the same 32-bit float, in plain decimals: Arma's text configs don't use exponents.
    public static string FormatFloat(float value)
    {
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        var e = s.IndexOfAny(['E', 'e']);
        if (e < 0) return s;
        var exponent = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = s[..e];
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        var plain = point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length)
            : digits[..point] + "." + digits[point..];
        return negative ? "-" + plain : plain;
    }

    // A whole config tree as text, Eden's layout: tab indents, `name=value;`, `class X\r\n{\r\n…\r\n};`. Trivia isn't written.
    public static string Write(IReadOnlyList<ConfigItem> items)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, items, 0);
        return sb.ToString();
    }

    private static void Write(System.Text.StringBuilder sb, IReadOnlyList<ConfigItem> items, int depth)
    {
        var pad = new string('\t', depth);
        foreach (var item in items)
        {
            switch (item)
            {
                case CfgClass { Body: null } c:
                    sb.Append(pad).Append("class ").Append(c.Name).Append(';').Append(NewLine);
                    break;
                case CfgClass c:
                    sb.Append(pad).Append("class ").Append(c.Name);
                    if (c.Base is not null) sb.Append(" : ").Append(c.Base);
                    sb.Append(NewLine).Append(pad).Append('{').Append(NewLine);
                    Write(sb, c.Body, depth + 1);
                    sb.Append(pad).Append("};").Append(NewLine);
                    break;
                case CfgDelete d:
                    sb.Append(pad).Append("delete ").Append(d.Name).Append(';').Append(NewLine);
                    break;
                case CfgAssignment a:
                    sb.Append(pad).Append(a.Name).Append(a.IsArray ? "[]" : "").Append(a.Append ? "+=" : "=").Append(Format(a.Value)).Append(';').Append(NewLine);
                    break;
            }
        }
    }
}
