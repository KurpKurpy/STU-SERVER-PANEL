using System.Text.RegularExpressions;
using Contracts;

namespace ServerAgent.Rcon;

// Phase 7a Ruling 3. BattlEye reads BEServer_x64.cfg from <profiles>\BattlEye, renames it to BEServer_x64_active_<hex>.cfg while the
// server runs and back only on a clean exit (spike, BattlEye 1.221), so the agent writes it at every launch and clears active copies a
// killed server left. RCon listens on 127.0.0.1 only.
public static partial class RconConfig
{
    public const string FileName = "BEServer_x64.cfg";
    private const string ActivePattern = "BEServer_x64_active_*.cfg";

    [GeneratedRegex("^[A-Za-z0-9]{8,64}$")]
    private static partial Regex PasswordShape();

    // Null when it's fine; otherwise why the launch is refused. The password goes into a cfg line, so it must be one plain word.
    public static string? Problem(RconSpec spec, int gamePort) =>
        spec.Port is < 1024 or > 65535 ? "The RCon port must be between 1024 and 65535."
        : spec.Port >= gamePort && spec.Port <= gamePort + 4 ? $"The RCon port can't be one of the game's ports ({gamePort}-{gamePort + 4})."
        : !PasswordShape().IsMatch(spec.Password) ? "The RCon password must be 8 to 64 letters and digits."
        : null;

    public static void Write(string profileDir, RconSpec spec)
    {
        var dir = Path.Combine(profileDir, "BattlEye");
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, ActivePattern)) File.Delete(stale);
        File.WriteAllText(Path.Combine(dir, FileName), $"RConPassword {spec.Password}\r\nRConPort {spec.Port}\r\nRConIP 127.0.0.1\r\n");
    }

    // After an agent restart: what the running server was launched with, from its active copy (or the cfg, if BattlEye hasn't renamed it yet).
    public static RconSpec? TryRead(string profileDir)
    {
        var dir = Path.Combine(profileDir, "BattlEye");
        if (!Directory.Exists(dir)) return null;
        var file = Directory.GetFiles(dir, ActivePattern).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
            ?? (File.Exists(Path.Combine(dir, FileName)) ? Path.Combine(dir, FileName) : null);
        if (file is null) return null;
        try
        {
            var values = File.ReadAllLines(file).Select(l => l.Trim().Split(' ', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return values.TryGetValue("RConPassword", out var pw) && values.TryGetValue("RConPort", out var port) && int.TryParse(port, out var p)
                ? new RconSpec(p, pw) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
