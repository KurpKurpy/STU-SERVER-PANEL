using System.Text.RegularExpressions;

namespace ArmaFormats;

// What a config defines for the compatibility check (Phase 5b): the classes directly under its top-level CfgPatches (addons) and
// CfgWorlds (maps). Unreadable says why some or all of it couldn't be read; nothing is guessed (design §3).
public sealed record ConfigNames(string[] Addons, string[] Worlds, string? Unreadable);

public static partial class ConfigScan
{
    public const int MaxTextBytes = 16 * 1024 * 1024; // real config.cpp files are a few MB; tokenizing costs ~20× the size

    public static ConfigNames Names(byte[] config)
    {
        if (!Rap.IsRap(config))
            return config.Length > MaxTextBytes ? new ConfigNames([], [], "text config too large (over 16 MB)") : FromText(MissionScan.Text(config));
        try
        {
            var sections = Rap.SectionClasses(config, "CfgPatches", "CfgWorlds");
            return new ConfigNames(sections.GetValueOrDefault("CfgPatches", []), sections.GetValueOrDefault("CfgWorlds", []), null);
        }
        catch (RapException e) { return new ConfigNames([], [], $"binarized config: {e.Message}"); }
    }

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+(\w+)", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Define();

    [GeneratedRegex("^[A-Za-z0-9_]+$")] // addon names may start with a digit: 13th_patches, 3AS_Props
    private static partial Regex Identifier();

    // CBA's conventional macros for a component's own name; defined in an #included script_component.hpp, so not visible here.
    private static readonly HashSet<string> CbaNames = new(["ADDON", "SUBADDON", "COMPONENT", "PREFIX"], StringComparer.Ordinal);

    // Text configs aren't preprocessed, so the full parser fails on most real mods (spike S7). This reads tokens instead: comments,
    // strings and # lines are skipped, and a class name that's a macro (a #define in this file, a macro call, or one of CBA's names)
    // makes the config partly unreadable rather than a guessed name (Ruling 3, amended: ALL-CAPS names like CHVD are real).
    private static ConfigNames FromText(string text)
    {
        HashSet<string> defines;
        try { defines = Define().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal); }
        catch (RegexMatchTimeoutException) { return new ConfigNames([], [], "text config: too complex to read"); }

        var tokens = Tokens(text);
        var addons = new List<string>();
        var worlds = new List<string>();
        var macro = false;
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "{") { depth++; continue; }
            if (t == "}") { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0 || !IsClass(t) || i + 2 >= tokens.Count || tokens[i + 2] != "{") continue;
            var target = tokens[i + 1].Equals("CfgPatches", StringComparison.OrdinalIgnoreCase) ? addons
                : tokens[i + 1].Equals("CfgWorlds", StringComparison.OrdinalIgnoreCase) ? worlds : null;
            if (target is null) continue;
            var inner = 0;
            var k = i + 2;
            for (; k < tokens.Count; k++)
            {
                if (tokens[k] == "{") { inner++; continue; }
                if (tokens[k] == "}") { if (--inner == 0) break; continue; }
                if (inner != 1 || !IsClass(tokens[k]) || k + 1 >= tokens.Count) continue;
                var name = tokens[k + 1];
                var after = k + 2 < tokens.Count ? tokens[k + 2] : "";
                if (after == ";") continue; // a declaration of a class defined elsewhere
                if (!Identifier().IsMatch(name) || after == "(" || defines.Contains(name) || CbaNames.Contains(name)) macro = true;
                else target.Add(name);
            }
            i = k;
        }
        return new ConfigNames(addons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), worlds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            macro ? "text config: macro class names" : null);
    }

    private static bool IsClass(string token) => token.Equals("class", StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        var lineStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#' && lineStart)
            {
                while (i < text.Length && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\n' or '\r') i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 3 : 2;
                    else i++;
                }
                continue;
            }
            lineStart = false;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] != '"') { i++; continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; } // "" is a quote inside the string
                    i++;
                    break;
                }
                tokens.Add("\"\"");
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(text[start..i]);
                continue;
            }
            tokens.Add(c.ToString());
            i++;
        }
        return tokens;
    }
}
