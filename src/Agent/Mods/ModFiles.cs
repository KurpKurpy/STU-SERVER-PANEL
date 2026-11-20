namespace ServerAgent.Mods;

// What's inside a mod folder. Keys live in a top-level key/keys/serverkey/serverkeys folder, in any case (Phase 5a Ruling 8).
public static class ModFiles
{
    private static readonly string[] KeyFolders = ["key", "keys", "serverkey", "serverkeys"];

    public static string[] KeyPaths(string modFolder) =>
        Directory.Exists(modFolder)
            ? Directory.GetDirectories(modFolder)
                .Where(d => KeyFolders.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .SelectMany(d => Directory.GetFiles(d, "*.bikey"))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    public static string[] KeyNames(string modFolder) =>
        KeyPaths(modFolder).Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // live re-reads each file: a folder listing shows 0 bytes for a file SteamCMD still has open while it downloads.
    public static long Size(string folder, bool live = false)
    {
        if (!Directory.Exists(folder)) return 0;
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { if (live) f.Refresh(); return f.Exists ? f.Length : 0; }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; } // SteamCMD is moving files
    }
}
