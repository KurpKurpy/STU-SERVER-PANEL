using System.Text.RegularExpressions;
using Contracts;

namespace ServerAgent.Mods;

// The server's keys folder (Phase 5a Ruling 8): deploy copies mod keys in and never deletes; cleanup removes only the keys the
// panel names (keys of library mods no kept list uses), never a3.bikey, so Creator DLC and hand-added keys stay. Every change
// first copies the whole folder to backups\keys\<install-area>\<time>.
public sealed partial class KeyService(Areas areas, ModStore mods, ILogger<KeyService> log)
{
    private const string VanillaKey = "a3.bikey";

    [GeneratedRegex(@"^[A-Za-z0-9 ._()+-]{1,128}\.bikey$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyName();

    public KeyFile[] List(string installRoot) =>
        areas.FindInstallRoot(installRoot) is { } root && Directory.Exists(Path.Combine(root, "keys"))
            ? Directory.GetFiles(Path.Combine(root, "keys"), "*.bikey").Select(f => new KeyFile(Path.GetFileName(f), new FileInfo(f).Length)).ToArray()
            : [];

    public KeysResult Deploy(string installRoot, ModRef[] refs)
    {
        if (areas.FindInstallRoot(installRoot) is not { } root) return Fail("That server folder isn't one of this agent's InstallRoots.");
        if (areas.IsReadOnlyInstall(root)) return Fail(Areas.ReadOnlyMessage);
        if (mods.Check(refs) is { } problem) return Fail(problem);
        var keysDir = Path.Combine(root, "keys");
        var incoming = (refs ?? []).SelectMany(r => ModFiles.KeyPaths(mods.Folder(r)))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var toCopy = incoming.Where(p => !File.Exists(Path.Combine(keysDir, Path.GetFileName(p))) || !Same(p, Path.Combine(keysDir, Path.GetFileName(p)))).ToList();
        if (toCopy.Count == 0) return new KeysResult(true, null, null, [], []);
        try
        {
            var backup = Backup(root, keysDir);
            Directory.CreateDirectory(keysDir);
            foreach (var key in toCopy) File.Copy(key, Path.Combine(keysDir, Path.GetFileName(key)), overwrite: true);
            var added = toCopy.Select(Path.GetFileName).OfType<string>().ToArray();
            log.LogInformation("Deployed {Count} keys into {Folder} (backup {Backup})", added.Length, keysDir, backup ?? "none needed");
            return new KeysResult(true, null, backup, added, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail($"Couldn't copy the keys: {e.Message}"); }
    }

    public KeysResult Cleanup(string installRoot, string[] keyNames)
    {
        if (areas.FindInstallRoot(installRoot) is not { } root) return Fail("That server folder isn't one of this agent's InstallRoots.");
        if (areas.IsReadOnlyInstall(root)) return Fail(Areas.ReadOnlyMessage);
        if ((keyNames ?? []).Any(n => !KeyName().IsMatch(n ?? ""))) return Fail("Keys are named by their .bikey file name only.");
        var keysDir = Path.Combine(root, "keys");
        var named = new HashSet<string>(keyNames ?? [], StringComparer.OrdinalIgnoreCase);
        named.Remove(VanillaKey);
        var remove = Directory.Exists(keysDir) ? Directory.GetFiles(keysDir, "*.bikey").Where(f => named.Contains(Path.GetFileName(f))).ToList() : [];
        if (remove.Count == 0) return new KeysResult(true, null, null, [], []);
        try
        {
            var backup = Backup(root, keysDir) ?? throw new IOException("the keys folder couldn't be backed up");
            foreach (var key in remove) File.Delete(key);
            var removed = remove.Select(Path.GetFileName).OfType<string>().ToArray();
            log.LogInformation("Removed {Count} keys from {Folder} (backup {Backup})", removed.Length, keysDir, backup);
            return new KeysResult(true, null, backup, [], removed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail($"Couldn't clean up the keys: {e.Message}"); }
    }

    // The whole keys folder, before any change; null when there's nothing in it.
    private string? Backup(string root, string keysDir)
    {
        if (!Directory.Exists(keysDir) || Directory.GetFiles(keysDir).Length == 0) return null;
        var baseName = Path.Combine("keys", areas.InstallArea(root), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var folder = areas.Resolve("backups", baseName);
        for (var n = 2; Directory.Exists(folder); n++) folder = areas.Resolve("backups", $"{baseName}-{n}");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(keysDir)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return folder;
    }

    private static bool Same(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static KeysResult Fail(string error) => new(false, error, null, [], []);
}
