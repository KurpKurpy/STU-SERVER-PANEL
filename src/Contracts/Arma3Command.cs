namespace Contracts;

// The server's command line, shared so the agent launches exactly what the panel previews (plan Ruling 11).
public static class Arma3Command
{
    // Generated, path-carrying arguments first (Phase 2 order), then the allowlisted custom ones.
    public static string[] ServerArguments(LaunchSpec spec, string configDir, string profileDir, Func<ModRef, string>? modPath = null)
    {
        var args = new List<string> { $"-port={spec.Port}", $"-config={Path.Combine(configDir, "server.cfg")}" };
        if (spec.UseBasicCfg) args.Add($"-cfg={Path.Combine(configDir, "basic.cfg")}");
        args.Add($"-profiles={profileDir}");
        args.Add($"-name={spec.Slug}");
        AddMods(args, "-mod", spec.Mods, modPath);
        AddMods(args, "-serverMod", spec.ServerMods, modPath);
        args.AddRange(spec.CustomArgs);
        return args.ToArray();
    }

    // One argument per list, folders joined by ';' (Phase 5a Ruling 6). modPath maps each mod to the agent's folder for it.
    private static void AddMods(List<string> args, string name, ModRef[]? mods, Func<ModRef, string>? modPath)
    {
        if (mods is not { Length: > 0 }) return;
        if (modPath is null) throw new InvalidOperationException("Mod folders need the agent's paths.");
        args.Add($"{name}={string.Join(';', mods.Select(modPath))}");
    }

    // A headless client (spike F18): the same exe with -client, its own profiles folder (they all call themselves "headlessclient").
    public static string[] HeadlessArguments(LaunchSpec spec, int index, string profileDir, Func<ModRef, string>? modPath = null)
    {
        var hc = spec.Headless ?? throw new InvalidOperationException("This launch spec has no headless clients.");
        var args = new List<string> { "-client", $"-connect={hc.Connect}", $"-port={spec.Port}" };
        if (!string.IsNullOrEmpty(hc.Password)) args.Add($"-password={hc.Password}");
        args.Add($"-profiles={Path.Combine(profileDir, $"hc{index}")}");
        args.Add($"-name=hc{index}");
        AddMods(args, "-mod", spec.HeadlessMods, modPath);
        return args.ToArray();
    }

    // One line for people to read and copy: Windows quoting, and any -password= value masked.
    public static string Display(string exePath, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { Quote(exePath) }.Concat(args.Select(a =>
            Quote(a.StartsWith("-password=", StringComparison.OrdinalIgnoreCase) ? "-password=********" : a))));

    private static string Quote(string s) => s.Any(c => c is ' ' or '\t' or '"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
}
