using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaFormats;
using Contracts;
using static Panel.Features.Profiles.Config.ConfigCatalog;

namespace Panel.Features.Profiles.Config;

// The one generator and parser for a profile's files (plan Ruling 11). Managed keys come from ConfigCatalog;
// anything else is kept as its source text (Ruling 5).
public static partial class ConfigFiles
{
    public const string PasswordsComment = "// Passwords are set on the General tab and are not shown here.";
    public const int ServerCfgMax = 65536, BasicCfgMax = 16384, DifficultyMax = 16384, ParamsMax = 4096, SecretMax = 128;
    private const string NL = ConfigText.NewLine;
    private static readonly string[] ServerClasses = ["AntiFlood", "AdvancedOptions"];
    private static readonly string[] BasicClasses = ["sockets"];

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex DifficultyName();

    // Anywhere in the text (mid-line, in comments, value on the next line); an unquoted or unclosed value runs to ';' or the line's end.
    [GeneratedRegex("""(?<pre>(?<![A-Za-z0-9_])(?<name>passwordAdmin|serverCommandPassword|password)\s*=\s*)(?:"(?<q>(?:[^"]|"")*)"|(?<u>[^;\r\n]*?))(?<post>[ \t]*(?:;|(?=[\r\n])|$))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretLine();

    // ---------- generation ----------

    public static GeneratedFiles Generate(ProfileConfig config, SecretMode mode)
    {
        var s = config.Settings;
        var server = new StringBuilder();
        foreach (var key in Of(Server).Where(k => k.Class is null && !k.Secret && k.Tab != "headless"))
            if (s.Server.TryGetValue(key.Name, out var v)) Line(server, ConfigText.Assignment(key.Name, ToValue(key, v)));
        var setSecrets = Secrets.Names.Where(n => !string.IsNullOrEmpty(config.Secrets.Get(n))).ToList();
        if (mode == SecretMode.Real)
            foreach (var name in setSecrets) Line(server, ConfigText.Assignment(name, new CfgString(config.Secrets.Get(name)!)));
        else if (setSecrets.Count > 0) Line(server, PasswordsComment);
        if (s.Headless.Enabled)
        {
            var addresses = new CfgArray(s.Headless.AddressList.Select(a => (ConfigValue)new CfgString(a)).ToList());
            Line(server, ConfigText.Assignment("headlessClients", addresses));
            Line(server, ConfigText.Assignment("localClient", addresses));
        }
        foreach (var cls in ServerClasses) AppendClass(server, cls, Server, s.Server);
        if (!string.IsNullOrWhiteSpace(s.MissionsOverride)) Block(server, s.MissionsOverride);
        else if (config.Rotation.Count > 0) server.Append(Missions(config.Rotation));
        Block(server, config.ExtraServerCfg);

        string? basic = null;
        if (s.Basic.Count > 0 || !string.IsNullOrWhiteSpace(config.ExtraBasicCfg))
        {
            var b = new StringBuilder();
            foreach (var key in Of(Basic).Where(k => k.Class is null))
                if (s.Basic.TryGetValue(key.Name, out var v)) Line(b, ConfigText.Assignment(key.Name, ToValue(key, v)));
            foreach (var cls in BasicClasses) AppendClass(b, cls, Basic, s.Basic);
            Block(b, config.ExtraBasicCfg);
            basic = b.ToString();
        }

        var difficulty = string.IsNullOrWhiteSpace(config.DifficultyOverride) ? DifficultyClass(s.Difficulty) : EnsureNewLine(config.DifficultyOverride);
        return new GeneratedFiles(server.ToString(), basic, difficulty);
    }

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append(NL);

    private static void Block(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.Append(EnsureNewLine(text));
    }

    private static string EnsureNewLine(string text) => text.EndsWith('\n') ? text : text + NL;

    private static void AppendClass(StringBuilder sb, string cls, string file, Dictionary<string, JsonElement> values)
    {
        var members = Of(file).Where(k => k.Class == cls && values.ContainsKey(k.Name)).ToList();
        if (members.Count == 0) return;
        sb.Append("class ").Append(cls).Append(NL).Append('{').Append(NL);
        foreach (var m in members) sb.Append('\t').Append(ConfigText.Assignment(m.Member, ToValue(m, values[m.Name]))).Append(NL);
        sb.Append("};").Append(NL);
    }

    private static string Missions(IReadOnlyList<RotationItem> rotation)
    {
        var sb = new StringBuilder("class Missions" + NL + "{" + NL);
        for (var i = 0; i < rotation.Count; i++)
        {
            var r = rotation[i];
            sb.Append($"\tclass Mission_{i + 1:00}{NL}\t{{{NL}");
            sb.Append("\t\t").Append(ConfigText.Assignment("template", new CfgString(r.Template))).Append(NL);
            sb.Append("\t\t").Append(ConfigText.Assignment("difficulty", new CfgString(r.Difficulty))).Append(NL);
            var lines = ParamLines(r.ParamsText);
            if (lines.Count > 0)
            {
                sb.Append($"\t\tclass Params{NL}\t\t{{{NL}");
                foreach (var line in lines) sb.Append("\t\t\t").Append(line).Append(NL);
                sb.Append($"\t\t}};{NL}");
            }
            sb.Append($"\t}};{NL}");
        }
        return sb.Append("};").Append(NL).ToString();
    }

    private static List<string> ParamLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();

    // The server's own format (spike F20): tabs, name=value; and CRLF, so a server rewrite leaves it byte-identical.
    private static string DifficultyClass(Dictionary<string, JsonElement> values)
    {
        var sb = new StringBuilder($"class DifficultyPresets{NL}{{{NL}\tclass CustomDifficulty{NL}\t{{{NL}\t\tclass Options{NL}\t\t{{{NL}");
        foreach (var key in Of(Difficulty).Where(k => k.Class is null))
            if (values.TryGetValue(key.Name, out var v)) sb.Append("\t\t\t").Append(ConfigText.Assignment(key.Name, ToValue(key, v), compact: true)).Append(NL);
        sb.Append($"\t\t}};{NL}");
        if (Find(Difficulty, "CustomDifficulty.aiLevelPreset") is { } preset && values.TryGetValue(preset.Name, out var level))
            sb.Append("\t\t").Append(ConfigText.Assignment(preset.Member, ToValue(preset, level), compact: true)).Append(NL);
        sb.Append($"\t}};{NL}");
        var ai = Of(Difficulty).Where(k => k.Class == "CustomAILevel" && values.ContainsKey(k.Name)).ToList();
        if (ai.Count > 0)
        {
            sb.Append($"\tclass CustomAILevel{NL}\t{{{NL}");
            foreach (var k in ai) sb.Append("\t\t").Append(ConfigText.Assignment(k.Member, ToValue(k, values[k.Name]), compact: true)).Append(NL);
            sb.Append($"\t}};{NL}");
        }
        return sb.Append("};").Append(NL).ToString();
    }

    private static ConfigValue ToValue(CfgKey key, JsonElement v) => key.Type switch
    {
        CfgType.Text => new CfgString(v.GetString() ?? ""),
        CfgType.Integer => new CfgNumber(v.GetInt64(), ""),
        CfgType.Number => new CfgNumber(v.GetDouble(), ""),
        CfgType.Bool01 => new CfgNumber(v.GetBoolean() ? 1 : 0, ""),
        CfgType.BoolWord => new CfgWord(v.GetBoolean() ? "true" : "false"),
        CfgType.TextList => new CfgArray(v.EnumerateArray().Select(e => (ConfigValue)new CfgString(e.GetString() ?? "")).ToList()),
        CfgType.NumberList => new CfgArray(v.EnumerateArray().Select(e => (ConfigValue)new CfgNumber(e.GetDouble(), "")).ToList()),
        _ => throw new InvalidOperationException($"{key.Name} isn't a config file key."),
    };

    // The agent's headless-client spec: the first address to connect to, and the join password when one is set.
    public static HeadlessSpec? Headless(ProfileSettings settings, Secrets secrets) =>
        settings.Headless.Enabled
            ? new HeadlessSpec(settings.Headless.Count, settings.Headless.AddressList[0], string.IsNullOrEmpty(secrets.Password) ? null : secrets.Password, settings.Headless.WithServer)
            : null;

    public static string[] LaunchArguments(ProfileSettings settings)
    {
        var args = new List<string>();
        foreach (var key in Of(Launch))
        {
            if (!settings.Launch.TryGetValue(key.Name, out var v)) continue;
            switch (key.Type)
            {
                case CfgType.Flag when v.ValueKind == JsonValueKind.True: args.Add(key.Name); break;
                case CfgType.Integer: args.Add($"{key.Name}={v.GetInt64()}"); break;
                case CfgType.Text: args.Add($"{key.Name}={v.GetString()}"); break;
            }
        }
        args.AddRange(Arma3LaunchArgs.Parse(settings.ExtraArgs));
        if (Arma3LaunchArgs.Validate(args) is { } bad) throw new InvalidOperationException(bad);
        return args.ToArray();
    }

    // ---------- parsing ----------

    public static ParsedFile ParseServerCfg(string text) => ParseFile(text, Server, ServerClasses);

    public static ParsedFile ParseBasicCfg(string text) => ParseFile(text, Basic, BasicClasses);

    private sealed class Collector(ConfigDocument doc)
    {
        public readonly Dictionary<string, JsonElement> Values = [];
        public readonly StringBuilder Extra = new();
        public readonly List<ConfigWarning> Warnings = [];
        private readonly Dictionary<string, int> _seen = new(StringComparer.OrdinalIgnoreCase);

        public void Seen(string name, int line)
        {
            if (_seen.TryGetValue(name, out var first)) throw new ConfigSyntaxException(line, $"{name} is set twice (lines {first} and {line}).");
            _seen[name] = line;
        }

        public void Raw(ConfigItem item) => Extra.Append(doc.SourceOf(item));

        public void Trivia(CfgTrivia t)
        {
            var kept = string.Join("\n", doc.SourceOf(t).Split('\n').Where(l => l.Trim() != PasswordsComment));
            if (!string.IsNullOrWhiteSpace(kept)) Extra.Append(kept);
        }
    }

    private static ParsedFile ParseFile(string text, string file, string[] classes)
    {
        var doc = ConfigDocument.Parse(text);
        var c = new Collector(doc);
        Secrets? secrets = null;
        List<RotationItem>? rotation = null;
        string? missionsOverride = null;
        CfgAssignment? headless = null, local = null;

        foreach (var item in doc.Items)
        {
            switch (item)
            {
                case CfgTrivia t:
                    c.Trivia(t);
                    break;
                case CfgAssignment a when file == Server && !a.IsArray && !a.Append && Secrets.Names.Contains(a.Name, StringComparer.OrdinalIgnoreCase):
                    c.Seen(a.Name, a.Line);
                    // Unquoted values are lifted too: anything left in the extras would come back in clear (review C1).
                    var secret = a.Value switch { CfgString str => str.Value, CfgNumber n => n.Raw, CfgWord w => w.Value, _ => null };
                    if (secret is not null) secrets = (secrets ?? Secrets.None).With(a.Name, secret);
                    else { c.Raw(a); c.Warnings.Add(new(a.Line, $"{a.Name} must be a quoted string; it's kept as written.")); }
                    break;
                case CfgAssignment a when file == Server && a.IsArray && !a.Append && a.Name.Equals("headlessClients", StringComparison.OrdinalIgnoreCase):
                    c.Seen(a.Name, a.Line);
                    headless = a;
                    break;
                case CfgAssignment a when file == Server && a.IsArray && !a.Append && a.Name.Equals("localClient", StringComparison.OrdinalIgnoreCase):
                    c.Seen(a.Name, a.Line);
                    local = a;
                    break;
                case CfgAssignment a when Find(file, a.Name) is { Class: null, Secret: false } key && key.Tab != "headless" && a.IsArray == key.IsList && !a.Append:
                    c.Seen(key.Name, a.Line);
                    if (TryJson(key, a.Value, out var json, out var error)) c.Values[key.Name] = json;
                    else { c.Raw(a); c.Warnings.Add(new(a.Line, $"{a.Name}: {error} It's kept as written.")); }
                    break;
                case CfgClass { Body: not null } cls when file == Server && cls.Name.Equals("Missions", StringComparison.OrdinalIgnoreCase):
                    c.Seen("class Missions", cls.Line);
                    if (TryRotation(doc, cls) is { } list) rotation = list;
                    else missionsOverride = doc.SourceOf(cls);
                    break;
                case CfgClass { Body: not null } cls when classes.Contains(cls.Name, StringComparer.OrdinalIgnoreCase):
                    c.Seen("class " + cls.Name, cls.Line);
                    if (!TryManagedClass(doc, cls, file, c.Values, c.Warnings)) c.Raw(cls);
                    break;
                default:
                    c.Raw(item);
                    break;
            }
        }

        HeadlessSettings? hc = null;
        if (headless is not null)
        {
            var addresses = Strings(headless.Value);
            if (addresses is not null && (local is null || Strings(local.Value) is { } l && l.SequenceEqual(addresses)))
                hc = new HeadlessSettings(Enabled: true, Addresses: addresses);
            else
            {
                c.Raw(headless);
                if (local is not null) c.Raw(local);
                c.Warnings.Add(new(headless.Line, "headlessClients[] and localClient[] differ; both are kept as written."));
            }
        }
        else if (local is not null) c.Raw(local);

        return new ParsedFile(c.Values, c.Extra.ToString(), secrets, rotation, missionsOverride, hc, c.Warnings);
    }

    private static string[]? Strings(ConfigValue v) =>
        v is CfgArray a && a.Items.All(i => i is CfgString) ? a.Items.Cast<CfgString>().Select(s => s.Value).ToArray() : null;

    private static bool IsComment(ConfigDocument doc, ConfigItem item) => item is CfgTrivia && !string.IsNullOrWhiteSpace(doc.SourceOf(item));

    // A Missions class becomes a rotation only when every entry is a plain template + difficulty (+ Params); otherwise null (override).
    private static List<RotationItem>? TryRotation(ConfigDocument doc, CfgClass missions)
    {
        var list = new List<RotationItem>();
        foreach (var item in missions.Body!)
        {
            if (item is CfgTrivia)
            {
                if (IsComment(doc, item)) return null;
                continue;
            }
            if (item is not CfgClass { Body: not null, Base: null } entry) return null;
            string? template = null, difficulty = null, parameters = "";
            foreach (var member in entry.Body)
            {
                switch (member)
                {
                    case CfgTrivia when IsComment(doc, member): return null;
                    case CfgTrivia: break;
                    case CfgAssignment { IsArray: false, Append: false } a when a.Name.Equals("template", StringComparison.OrdinalIgnoreCase) && template is null:
                        template = a.Value is CfgString ts ? ts.Value : null;
                        if (template is null) return null;
                        break;
                    case CfgAssignment { IsArray: false, Append: false } a when a.Name.Equals("difficulty", StringComparison.OrdinalIgnoreCase) && difficulty is null:
                        difficulty = a.Value switch { CfgString ds => ds.Value, CfgWord dw => dw.Value, _ => null };
                        if (difficulty is null || !DifficultyName().IsMatch(difficulty)) return null;
                        break;
                    case CfgClass { Body: not null, Base: null } p when p.Name.Equals("Params", StringComparison.OrdinalIgnoreCase) && parameters == "":
                        if (p.Body.Any(i => i is CfgClass)) return null;
                        var stmts = p.Body.Where(i => i is not CfgTrivia || IsComment(doc, i)).ToList();
                        parameters = stmts.Count == 0 ? "" : string.Join(NL, ParamLines(doc.Text[stmts[0].Start..(stmts[^1].Start + stmts[^1].Length)])) + NL;
                        break;
                    default: return null;
                }
            }
            if (string.IsNullOrEmpty(template) || difficulty is null) return null;
            list.Add(new RotationItem(template, difficulty, parameters));
        }
        return list;
    }

    // AntiFlood, AdvancedOptions, sockets: values only when every member is a catalog key; comments are dropped with a warning.
    private static bool TryManagedClass(ConfigDocument doc, CfgClass cls, string file, Dictionary<string, JsonElement> values, List<ConfigWarning> warnings)
    {
        if (cls.Base is not null) return false;
        var found = new Dictionary<string, JsonElement>();
        var hadComment = false;
        foreach (var member in cls.Body!)
        {
            if (member is CfgTrivia) { hadComment |= IsComment(doc, member); continue; }
            if (member is not CfgAssignment { Append: false } a) return false;
            var key = Find(file, cls.Name + "." + a.Name);
            if (key is null || a.IsArray != key.IsList || found.ContainsKey(key.Name)) return false;
            if (!TryJson(key, a.Value, out var json, out var error))
            {
                warnings.Add(new(a.Line, $"{cls.Name}.{a.Name}: {error} The class is kept as written."));
                return false;
            }
            found[key.Name] = json;
        }
        foreach (var (k, v) in found) values[k] = v;
        if (hadComment) warnings.Add(new(cls.Line, $"Comments inside class {cls.Name} aren't kept."));
        return true;
    }

    public static ParsedFile ParseDifficulty(string classText)
    {
        if (string.IsNullOrWhiteSpace(classText)) return new ParsedFile([], "", null, null, null, null, []);
        var doc = ConfigDocument.Parse(classText);
        var top = doc.Items.Where(i => i is not CfgTrivia).ToList();
        if (top is not [CfgClass { Body: not null } presets] || !presets.Name.Equals("DifficultyPresets", StringComparison.OrdinalIgnoreCase))
            throw new ConfigSyntaxException(1, "The difficulty text must be one class DifficultyPresets.");
        var values = new Dictionary<string, JsonElement>();
        var warnings = new List<ConfigWarning>();
        var hadComment = false;

        bool Members(IReadOnlyList<ConfigItem> body, Func<ConfigItem, bool> each)
        {
            foreach (var item in body)
            {
                if (item is CfgTrivia) { hadComment |= IsComment(doc, item); continue; }
                if (!each(item)) return false;
            }
            return true;
        }

        bool Assign(CfgAssignment a, string name)
        {
            var key = Find(Difficulty, name);
            if (key is null || a.IsArray || a.Append || values.ContainsKey(key.Name)) return false;
            if (!TryJson(key, a.Value, out var json, out var error)) { warnings.Add(new(a.Line, $"{a.Name}: {error} The class is kept as written.")); return false; }
            values[key.Name] = json;
            return true;
        }

        var ok = Members(presets.Body!, item => item switch
        {
            CfgClass { Body: not null, Base: null, Name: var n } custom when n.Equals("CustomDifficulty", StringComparison.OrdinalIgnoreCase) =>
                Members(custom.Body!, m => m switch
                {
                    CfgClass { Body: not null, Base: null, Name: var o } options when o.Equals("Options", StringComparison.OrdinalIgnoreCase) =>
                        Members(options.Body!, opt => opt is CfgAssignment oa && Find(Difficulty, oa.Name) is { Class: null } && Assign(oa, oa.Name)),
                    CfgAssignment a when a.Name.Equals("aiLevelPreset", StringComparison.OrdinalIgnoreCase) => Assign(a, "CustomDifficulty.aiLevelPreset"),
                    _ => false,
                }),
            CfgClass { Body: not null, Base: null, Name: var n } ai when n.Equals("CustomAILevel", StringComparison.OrdinalIgnoreCase) =>
                Members(ai.Body!, m => m is CfgAssignment a && Assign(a, "CustomAILevel." + a.Name)),
            _ => false,
        });

        if (!ok) return new ParsedFile([], EnsureNewLine(classText), null, null, null, null, warnings);
        if (hadComment) warnings.Add(new(presets.Line, "Comments inside class DifficultyPresets aren't kept."));
        return new ParsedFile(values, "", null, null, null, null, warnings);
    }

    // ---------- values and validation ----------

    private static bool TryJson(CfgKey key, ConfigValue value, out JsonElement json, out string? error)
    {
        json = default;
        object? raw = key.Type switch
        {
            CfgType.Text => value is CfgString s ? s.Value : null,
            CfgType.Integer => value is CfgNumber { Value: var n } && n == Math.Floor(n) && Math.Abs(n) < 1e15 ? (long)n : null,
            CfgType.Number => value is CfgNumber nn && double.IsFinite(nn.Value) ? nn.Value : null,
            CfgType.Bool01 or CfgType.BoolWord => value switch
            {
                CfgNumber { Value: 0 } => false,
                CfgNumber { Value: 1 } => true,
                CfgWord w when w.Value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
                CfgWord w when w.Value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
                _ => null,
            },
            CfgType.TextList => value is CfgArray a && a.Items.All(i => i is CfgString) ? a.Items.Cast<CfgString>().Select(x => x.Value).ToArray() : null,
            CfgType.NumberList => value is CfgArray b && b.Items.All(i => i is CfgNumber { Value: var f } && double.IsFinite(f)) ? b.Items.Cast<CfgNumber>().Select(x => x.Value).ToArray() : null,
            _ => null,
        };
        if (raw is null) { error = $"expected {Describe(key)}."; return false; }
        json = JsonSerializer.SerializeToElement(raw);
        error = Check(key, json);
        return error is null;
    }

    private static string Describe(CfgKey key) => key.Type switch
    {
        CfgType.Text => "a quoted string",
        CfgType.Integer => "a whole number",
        CfgType.Number => "a number",
        CfgType.Bool01 or CfgType.BoolWord or CfgType.Flag => "0/1 or true/false",
        CfgType.TextList => "a list of quoted strings",
        _ => "a list of numbers",
    };

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    // Form-strict checks of one value; null when it's fine.
    public static string? Check(CfgKey key, JsonElement v)
    {
        bool InRange(double d) => (key.Min is not { } min || d >= min) && (key.Max is not { } max || d <= max);
        string Range() => key.Min is { } min && key.Max is { } max ? $"between {Num(min)} and {Num(max)}" : key.Min is { } lo ? $"at least {Num(lo)}" : $"at most {Num(key.Max!.Value)}";
        switch (key.Type)
        {
            case CfgType.Text:
                if (v.ValueKind != JsonValueKind.String) return $"{key.Label} must be text.";
                var text = v.GetString()!;
                if (text.Length > 1024 || text.Any(char.IsControl)) return $"{key.Label} must be one line of at most 1,024 characters.";
                if (key.Choices is { } textChoices && !textChoices.Contains(text, StringComparer.OrdinalIgnoreCase)) return $"{key.Label} must be one of {string.Join(", ", textChoices)}.";
                if (key.Pattern is { } p && !Regex.IsMatch(text, p)) return $"{key.Label} isn't valid here.";
                return null;
            case CfgType.Integer or CfgType.Number:
                // 1e999 reads as Infinity, which a min-only range would let through.
                if (v.ValueKind != JsonValueKind.Number || !double.IsFinite(v.GetDouble()) || key.Type == CfgType.Integer && !v.TryGetInt64(out _))
                    return $"{key.Label} must be {Describe(key)}.";
                var d = v.GetDouble();
                if (key.Choices is { } numChoices && !numChoices.Contains(Num(d))) return $"{key.Label} must be one of {string.Join(", ", numChoices)}.";
                return InRange(d) ? null : $"{key.Label} must be {Range()}.";
            case CfgType.Bool01 or CfgType.BoolWord or CfgType.Flag:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{key.Label} must be on or off.";
            case CfgType.TextList:
                if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) return $"{key.Label} must be a list of text.";
                return v.EnumerateArray().Any(e => e.GetString()!.Length > 1024 || e.GetString()!.Any(char.IsControl)) ? $"{key.Label}: each item must be one line." : null;
            case CfgType.NumberList:
                if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !double.IsFinite(e.GetDouble()))) return $"{key.Label} must be a list of numbers.";
                if (key.Length is { } len && v.GetArrayLength() != len) return $"{key.Label} needs exactly {len} values.";
                return v.EnumerateArray().All(e => InRange(e.GetDouble())) ? null : $"{key.Label}: each value must be {Range()}.";
            default:
                return $"{key.Label} isn't a valid setting.";
        }
    }

    public static IReadOnlyList<ConfigError> Validate(ProfileConfig config)
    {
        var errors = new List<ConfigError>();
        var s = config.Settings;
        void Dict(string file, Dictionary<string, JsonElement> values)
        {
            foreach (var (name, v) in values)
            {
                var key = All.FirstOrDefault(k => k.File == file && k.Name == name);
                if (key is null || key.Secret || key.Tab == "headless") { errors.Add(new($"{file}.{name}", $"\"{name}\" isn't a setting this form manages.")); continue; }
                if (Check(key, v) is { } error) errors.Add(new($"{file}.{name}", error));
            }
        }
        Dict(Server, s.Server);
        Dict(Basic, s.Basic);
        Dict(Difficulty, s.Difficulty);
        Dict(Launch, s.Launch);

        // BSCF: "Desync might happen if used MaxSizeGuaranteed/MaxSizeNonguaranteed values over the maxPacketSize."
        var packet = s.Basic.TryGetValue("sockets.maxPacketSize", out var mp) && mp.ValueKind == JsonValueKind.Number && mp.TryGetInt64(out var m) ? m : 1400;
        foreach (var size in new[] { "MaxSizeGuaranteed", "MaxSizeNonguaranteed" })
            if (s.Basic.TryGetValue(size, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > packet)
                errors.Add(new($"basic.{size}", $"Packets can't be larger than the max packet size ({packet:N0} bytes); bigger ones desync."));

        if (s.Server.TryGetValue("missionsToServerRestart", out var restart) && s.Server.TryGetValue("missionsToShutdown", out var shutdown)
            && restart.ValueKind == JsonValueKind.Number && shutdown.ValueKind == JsonValueKind.Number && restart.GetDouble() > 0 && shutdown.GetDouble() > 0)
            errors.Add(new("server.missionsToShutdown", "Missions before a shutdown can't be combined with missions before a restart."));

        for (var i = 0; i < config.Rotation.Count; i++)
        {
            var r = config.Rotation[i];
            if (string.IsNullOrWhiteSpace(r.Template) || r.Template.Length > 128 || r.Template.Any(ch => ch == '"' || char.IsControl(ch)))
                errors.Add(new($"rotation.{i}.template", "The mission name must be 1–128 characters, without quotes."));
            if (!DifficultyName().IsMatch(r.Difficulty ?? ""))
                errors.Add(new($"rotation.{i}.difficulty", "Difficulty: letters, digits and underscores, for example Custom."));
            if ((r.ParamsText ?? "").Length > ParamsMax) errors.Add(new($"rotation.{i}.paramsText", $"Mission parameters: at most {ParamsMax:N0} characters."));
            else if (SyntaxError(r.ParamsText, d => d.Items.All(it => it is CfgTrivia or CfgAssignment), "Mission parameters must be name = value; lines.") is { } pe)
                errors.Add(new($"rotation.{i}.paramsText", pe));
        }

        foreach (var name in Secrets.Names)
            if (config.Secrets.Get(name) is { } secret && (secret.Length > SecretMax || secret.Any(char.IsControl) || name == "password" && secret.Contains('"')))
                errors.Add(new($"secrets.{name}", name == "password"
                    ? $"The join password must be one line of at most {SecretMax} characters, without quotes."
                    : $"Passwords must be one line of at most {SecretMax} characters."));

        // Capped before parsing: a huge text costs every parse and generate.
        if ((s.MissionsOverride ?? "").Length > ServerCfgMax) errors.Add(new("missionsOverride", $"At most {ServerCfgMax:N0} characters."));
        else if (!string.IsNullOrWhiteSpace(s.MissionsOverride)
            && SyntaxError(s.MissionsOverride, d => d.Items.Where(it => it is not CfgTrivia).ToList() is [CfgClass { Name: var n }] && n.Equals("Missions", StringComparison.OrdinalIgnoreCase),
                "The hand-written text must be one class Missions.") is { } me)
            errors.Add(new("missionsOverride", me));
        if (config.DifficultyOverride.Length > DifficultyMax) errors.Add(new("difficultyOverride", $"At most {DifficultyMax:N0} characters."));
        else if (!string.IsNullOrWhiteSpace(config.DifficultyOverride)
            && SyntaxError(config.DifficultyOverride, d => d.Items.Where(it => it is not CfgTrivia).ToList() is [CfgClass { Name: var n }] && n.Equals("DifficultyPresets", StringComparison.OrdinalIgnoreCase),
                "The hand-written text must be one class DifficultyPresets.") is { } de)
            errors.Add(new("difficultyOverride", de));
        if (config.ExtraServerCfg.Length > ServerCfgMax) errors.Add(new("extraServerCfg", $"At most {ServerCfgMax:N0} characters."));
        else if (SyntaxError(config.ExtraServerCfg, _ => true, "") is { } xe) errors.Add(new("extraServerCfg", xe));
        if (config.ExtraBasicCfg.Length > BasicCfgMax) errors.Add(new("extraBasicCfg", $"At most {BasicCfgMax:N0} characters."));
        else if (SyntaxError(config.ExtraBasicCfg, _ => true, "") is { } be) errors.Add(new("extraBasicCfg", be));

        if (s.Headless.Count is < 1 or > 8) errors.Add(new("headless.count", "Headless clients: 1 to 8."));
        if (s.Headless.Enabled && s.Headless.AddressList.Any(a => !IPAddress.TryParse(a, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !a.Contains('.')))
            errors.Add(new("headless.addresses", "Headless client addresses must be IPv4 addresses, for example 127.0.0.1."));

        var extraArgs = Arma3LaunchArgs.Parse(s.ExtraArgs);
        if (s.ExtraArgs.Length > 500) errors.Add(new("launch.extraArgs", "Extra arguments: at most 500 characters."));
        else if (Arma3LaunchArgs.Validate(extraArgs) is { } bad) errors.Add(new("launch.extraArgs", bad));
        else if (extraArgs.Select(a => a.Split('=')[0]).FirstOrDefault(n => s.Launch.Keys.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } twice)
            errors.Add(new("launch.extraArgs", $"{twice} is already set in the form above."));

        if (errors.Count == 0)
        {
            var files = Generate(config, SecretMode.Real);
            if (files.ServerCfg.Length > ServerCfgMax) errors.Add(new("server.cfg", $"server.cfg would be over {ServerCfgMax:N0} characters."));
            if (files.BasicCfg is { Length: > BasicCfgMax }) errors.Add(new("basic.cfg", $"basic.cfg would be over {BasicCfgMax:N0} characters."));
            if (files.DifficultyPresets.Length > DifficultyMax) errors.Add(new("difficulty", $"The difficulty class would be over {DifficultyMax:N0} characters."));
        }
        return errors;
    }

    // A key or managed class that extra text keeps as written while the form sets it too, so the file would set it twice (review I5).
    public static string? SetTwice(string file, string extra, ProfileSettings s)
    {
        if (string.IsNullOrWhiteSpace(extra)) return null;
        var values = file == Server ? s.Server : s.Basic;
        foreach (var item in ConfigDocument.Parse(extra).Items)
        {
            switch (item)
            {
                case CfgAssignment a when file == Server && s.Headless.Enabled && Find(Server, a.Name) is { Tab: "headless" }:
                    return a.Name;
                case CfgAssignment a when Find(file, a.Name) is { Class: null } key && values.ContainsKey(key.Name):
                    return a.Name;
                case CfgClass c when Of(file).Any(k => c.Name.Equals(k.Class, StringComparison.OrdinalIgnoreCase) && values.ContainsKey(k.Name)):
                    return "class " + c.Name;
            }
        }
        return null;
    }

    private static string? SyntaxError(string? text, Func<ConfigDocument, bool> shape, string shapeMessage)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return shape(ConfigDocument.Parse(text)) ? null : shapeMessage; }
        catch (ConfigSyntaxException e) { return e.Message; }
    }

    // Line-based, so it works on any file, even one that doesn't parse. compareWith marks values that differ from it.
    public static string MaskSecrets(string text, Secrets? compareWith = null) =>
        SecretLine().Replace(text, m =>
        {
            var value = m.Groups["q"].Success ? m.Groups["q"].Value.Replace("\"\"", "\"") : m.Groups["u"].Value.Trim();
            var masked = "********";
            if (compareWith is not null && value != (compareWith.Get(m.Groups["name"].Value) ?? "")) masked += " (changed)";
            return m.Groups["pre"].Value + "\"" + masked + "\"" + m.Groups["post"].Value;
        });
}
