using System.Security.Cryptography;
using System.Text;
using Contracts;

namespace ServerAgent;

public sealed class AreaException(string message) : Exception(message);

// Every path the panel names is (area, relativePath). Area roots come only from the agent's own settings.
public sealed class Areas
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnly = new(StringComparer.Ordinal);

    public IReadOnlyList<string> InstallRoots { get; }
    public string WorkshopRoot { get; }
    public string WorkshopContent { get; }
    public string LocalMods { get; }
    public string SteamCmdExe { get; }

    // The configured install root that matches, case-insensitively, or null.
    public string? FindInstallRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return InstallRoots.FirstOrDefault(r => r.Equals(Normalize(path), StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public Areas(AgentOptions options, IReadOnlyList<string> installRoots)
    {
        if (string.IsNullOrWhiteSpace(options.DataRoot)) throw new InvalidOperationException("Agent:DataRoot is not set in appsettings.json.");
        var dataRoot = Normalize(options.DataRoot);
        foreach (var area in new[] { "profiles", "configs", "backups" })
        {
            _roots[area] = Path.Combine(dataRoot, area);
            Directory.CreateDirectory(_roots[area]);
        }
        // Mods (Phase 5a Ruling 4): SteamCMD's Workshop download folder and admin-copied local mods; FileService only reads them.
        WorkshopRoot = Path.Combine(dataRoot, "workshop");
        WorkshopContent = Path.Combine(WorkshopRoot, "steamapps", "workshop", "content", "107410");
        LocalMods = Path.Combine(dataRoot, "localmods");
        SteamCmdExe = string.IsNullOrWhiteSpace(options.SteamCmdPath) ? Path.Combine(dataRoot, "steamcmd", "steamcmd.exe") : Normalize(options.SteamCmdPath);
        foreach (var (area, root) in new[] { ("workshop", WorkshopContent), ("localmods", LocalMods) })
        {
            _roots[area] = root;
            _readOnly.Add(area);
            Directory.CreateDirectory(root);
        }
        InstallRoots = installRoots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in InstallRoots)
        {
            var area = InstallArea(root);
            _roots[area] = root;
            _readOnly.Add(area);
        }
        foreach (var root in options.ReadOnlyInstallRoots ?? []) _readOnlyInstalls.Add(Normalize(root));
    }

    private readonly HashSet<string> _readOnlyInstalls = new(StringComparer.OrdinalIgnoreCase);

    // Agent:ReadOnlyInstallRoots (Phase 4a Ruling 9): the agent writes nothing there — no missions, keys or SteamCMD installs.
    public bool IsReadOnlyInstall(string installRoot) => _readOnlyInstalls.Contains(Normalize(installRoot));

    public const string ReadOnlyMessage = "That server folder is read-only on this agent (Agent:ReadOnlyInstallRoots), so nothing is written there.";

    public string InstallArea(string installRoot) =>
        "install-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(installRoot).ToLowerInvariant())))[..8];

    public bool IsReadOnly(string area) => _readOnly.Contains(area);

    public AreaInfo[] Describe() =>
        _roots.Select(kv => new AreaInfo(kv.Key, kv.Value, Directory.Exists(kv.Value), !IsReadOnly(kv.Key) && CanWrite(kv.Value), IsReadOnly(kv.Key))).ToArray();

    // "" is the area root itself (used for listing it).
    public string Resolve(string area, string relativePath)
    {
        if (!_roots.TryGetValue(area ?? "", out var root)) throw new AreaException($"Unknown area \"{area}\".");
        return ResolveUnder(root, relativePath);
    }

    // A relative path inside root: no rooted path, no "..", nothing outside it, and no junction or symlink on the way (design §5.3).
    public static string ResolveUnder(string root, string? relativePath)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        relativePath ??= "";
        if (Path.IsPathRooted(relativePath) || relativePath.Split('\\', '/').Contains(".."))
            throw new AreaException("Paths must stay inside their area.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relativePath)));
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return root;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AreaException("Paths must stay inside their area.");

        // A junction or symlink below the root could point anywhere, so none is followed.
        var current = root;
        foreach (var segment in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new AreaException("Paths may not pass through a link.");
            }
            else break;
        }
        return full;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".13c-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
