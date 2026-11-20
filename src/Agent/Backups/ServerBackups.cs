using System.IO.Compression;
using System.Text.RegularExpressions;
using Contracts;

namespace ServerAgent.Backups;

public class BackupException(string message) : Exception(message);

// Final review I3: a restore that stopped after its before-restore backup: the way back travels with the failure.
public sealed class RestoreException(string message, BackupMade before) : BackupException(message)
{
    public BackupMade Before { get; } = before;
}

// Phase 7b: a server backup is one zip of the agent's profiles and configs and each install's mpmissions and keys (never mods or the
// Arma install, which re-download), written to a temp file and moved into backups\server, then copied to Agent:BackupCopyTo if set.
// A restore needs every server stopped, saves the current state first, and only overwrites files the backup holds (nothing is deleted);
// nothing is ever written into a read-only install.
public sealed partial class ServerBackups(Areas areas, AgentOptions options, Func<bool> anyServerRunning)
{
    public const string ServersRunning = "Stop every server on this agent first";
    public static readonly string[] Parts = ["profiles", "configs", "missions", "keys"];
    private static readonly string[] SafetyFolders = ["profiles", "files", "keys", "missions"];
    private static readonly EnumerationOptions Walk = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    [GeneratedRegex(@"^server-\d{8}-\d{6}(-[a-z-]{1,20})?\.zip$")]
    private static partial Regex Name();

    [GeneratedRegex("^[a-z-]{1,20}$")]
    private static partial Regex Label();

    public static bool IsName(string? file) => file is not null && Name().IsMatch(file);
    public static bool IsLabel(string? label) => label is null || Label().IsMatch(label);

    private string Folder => areas.Resolve("backups", "server");

    public async Task<BackupMade> CreateAsync(string? label, CancellationToken ct)
    {
        if (!IsLabel(label)) throw new BackupException("A backup label is lower-case letters and hyphens.");
        Directory.CreateDirectory(Folder);
        string name;
        while (File.Exists(Path.Combine(Folder, name = $"server-{DateTime.UtcNow:yyyyMMdd-HHmmss}{(label is null ? "" : "-" + label)}.zip")))
            await Task.Delay(250, ct); // names are per second
        var final = Path.Combine(Folder, name);
        var temp = final + ".tmp";
        var unreadable = 0;
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                unreadable += Add(zip, areas.Resolve("profiles", ""), "profiles", ct);
                unreadable += Add(zip, areas.Resolve("configs", ""), "configs", ct);
                foreach (var root in areas.InstallRoots)
                {
                    var area = areas.InstallArea(root);
                    unreadable += Add(zip, Path.Combine(root, "mpmissions"), $"installs/{area}/mpmissions", ct);
                    unreadable += Add(zip, Path.Combine(root, "keys"), $"installs/{area}/keys", ct);
                }
            }
            File.Move(temp, final);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        var notes = new List<string>();
        if (unreadable > 0) notes.Add($"{unreadable} file(s) were in use and left out.");
        if (CopyOut(final, name) is { } copyProblem) notes.Add(copyProblem);
        return new BackupMade(name, new FileInfo(final).Length, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    // Files another process holds open without sharing are left out and counted; junctions and symlinks are never followed.
    private static int Add(ZipArchive zip, string root, string prefix, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", Walk))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry(prefix + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                using var target = entry.Open();
                source.CopyTo(target);
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        return skipped;
    }

    private string? CopyOut(string file, string name)
    {
        if (string.IsNullOrWhiteSpace(options.BackupCopyTo)) return null;
        try
        {
            var folder = Path.Combine(options.BackupCopyTo, "server");
            Directory.CreateDirectory(folder);
            File.Copy(file, Path.Combine(folder, name), overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Copy to {options.BackupCopyTo} failed: {e.Message}";
        }
    }

    public async Task<(BackupMade Before, string[] Skipped)> RestoreAsync(string file, string[] parts, CancellationToken ct)
    {
        if (!IsName(file)) throw new BackupException("That isn't a server backup.");
        if (parts is not { Length: > 0 } || parts.Any(p => !Parts.Contains(p))) throw new BackupException("Pick what to restore: profiles, configs, missions, keys.");
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) throw new BackupException("That backup isn't on this agent any more.");
        if (anyServerRunning()) throw new BackupException(ServersRunning);

        var before = await CreateAsync("before-restore", ct);
        // Final review I2: the before-restore backup takes a while; a server started meanwhile stops the restore before anything is written.
        if (anyServerRunning())
            throw new RestoreException($"{ServersRunning}: a server started during the restore's own backup, so nothing was restored. {before.File} holds the files as they were.", before);
        var skipped = new List<string>();
        var written = new List<string>();
        var installs = areas.InstallRoots.ToDictionary(areas.InstallArea, r => r);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                ct.ThrowIfCancellationRequested();
                var segments = entry.FullName.Split('/');
                string? root = null, relative = null, part = null;
                if (segments[0] is "profiles" or "configs" && parts.Contains(segments[0]))
                    (root, relative, part) = (areas.Resolve(segments[0], ""), string.Join('/', segments[1..]), segments[0]);
                else if (segments is ["installs", var area, var kind, ..] && kind is "mpmissions" or "keys" && parts.Contains(kind == "mpmissions" ? "missions" : "keys"))
                {
                    var what = kind == "mpmissions" ? "missions" : "keys";
                    if (!installs.TryGetValue(area, out var install)) { Note(skipped, $"{what} for a server folder this agent no longer has"); continue; }
                    if (areas.IsReadOnlyInstall(install)) { Note(skipped, $"{what} on {install} (read-only)"); continue; }
                    (root, relative, part) = (Path.Combine(install, kind), string.Join('/', segments[3..]), what);
                }
                if (root is null) continue;
                var target = Areas.ResolveUnder(root, relative!.Replace('/', Path.DirectorySeparatorChar)); // a crafted zip can't climb out
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".restore-tmp";
                try
                {
                    entry.ExtractToFile(temp, overwrite: true);
                    File.Move(temp, target, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                if (!written.Contains(part!)) written.Add(part!);
            }
        }
        // Final review I3: say what was already written, and keep the way back with the failure.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AreaException or InvalidDataException)
        {
            var what = written.Count == 0 ? "Nothing was restored" : $"Restore stopped after writing {string.Join(", ", written)}";
            throw new RestoreException($"{what}: {e.Message} The before-restore backup {before.File} puts it back.", before);
        }
        return (before, [.. skipped]);
    }

    private static void Note(List<string> skipped, string line) { if (!skipped.Contains(line)) skipped.Add(line); }

    public CommandResult Delete(string file)
    {
        if (!IsName(file)) return new CommandResult(false, "That isn't a server backup.");
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) return new CommandResult(false, "That backup isn't on this agent any more.");
        File.Delete(path);
        if (!string.IsNullOrWhiteSpace(options.BackupCopyTo))
            try { File.Delete(Path.Combine(options.BackupCopyTo, "server", file)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new CommandResult(true, null);
    }

    // The automatic safety copies (backups\<profiles|files|keys|missions>\<group>\<time>) older than the limit; zips are the panel's call.
    public int PruneSafetyCopies(int olderThanDays)
    {
        if (olderThanDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        var deleted = 0;
        foreach (var folder in SafetyFolders)
        {
            var top = areas.Resolve("backups", folder);
            if (!Directory.Exists(top)) continue;
            foreach (var group in Directory.EnumerateDirectories(top))
                foreach (var copy in Directory.EnumerateDirectories(group))
                    if (Directory.GetLastWriteTimeUtc(copy) < cutoff)
                    {
                        try { Directory.Delete(copy, recursive: true); deleted++; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
        }
        return deleted;
    }
}
