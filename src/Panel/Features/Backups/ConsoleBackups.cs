using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Panel.Auth;
using Panel.Data;

namespace Panel.Features.Backups;

// Phase 7b: a console backup is one zip of console.db (a consistent copy through SQLite's online backup, so the panel keeps running),
// blobs/ (every uploaded mission file) and keys/ (without them a restored console can't read its saved passwords). One at a time, off
// the request; the row says Running until it's Done or Failed. The zip is also copied to Backups:CopyTo when that's set on the box.
public sealed class ConsoleBackups(PanelPaths paths, IServiceScopeFactory scopes, IConfiguration config, ILogger<ConsoleBackups> log)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public string Folder => Path.Combine(paths.DataDir, "backups", "console");
    public string? CopyTo => config["Backups:CopyTo"] is { Length: > 0 } c ? c : null;

    // Final review I5: at startup a Running console backup can't finish (it ran in this process), so it's closed and its half-written files go.
    public async Task SweepAsync(AppDb db)
    {
        await db.Backups.Where(b => b.Type == BackupType.Console && b.Status == BackupStatus.Running).ExecuteUpdateAsync(u => u
            .SetProperty(b => b.Status, BackupStatus.Failed).SetProperty(b => b.Error, "The console restarted while this backup was being made.")
            .SetProperty(b => b.FinishedAt, DateTime.UtcNow));
        if (Directory.Exists(Folder))
            foreach (var temp in Directory.EnumerateFiles(Folder, "*.tmp")) File.Delete(temp);
    }

    public void Queue(Guid backupId, string label) => _ = Task.Run(() => RunAsync(backupId, label));

    private async Task RunAsync(Guid backupId, string label)
    {
        await _one.WaitAsync();
        try
        {
            (string Name, long Size, string? Note)? made = null;
            string? error = null;
            try { made = await CreateAsync(label); }
            catch (Exception e) { log.LogWarning(e, "Console backup failed"); error = e.Message; }
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            if (await db.Backups.FindAsync(backupId) is { } row)
            {
                (row.Status, row.Error, row.FinishedAt) = (made is null ? BackupStatus.Failed : BackupStatus.Done, error, DateTime.UtcNow);
                if (made is { } m) (row.FileName, row.SizeBytes, row.Note) = (m.Name, m.Size, m.Note);
                await db.SaveChangesAsync();
            }
            if (made is not null) await scope.ServiceProvider.GetRequiredService<BackupService>().ApplyRetentionAsync();
        }
        finally { _one.Release(); }
    }

    public async Task<(string Name, long Size, string? Note)> CreateAsync(string label)
    {
        Directory.CreateDirectory(Folder);
        string name;
        while (File.Exists(Path.Combine(Folder, name = $"console-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}.zip"))) await Task.Delay(250);
        var final = Path.Combine(Folder, name);
        var temp = final + ".tmp";
        var copy = final + ".db.tmp";
        try
        {
            using (var source = new SqliteConnection($"Data Source={Path.Combine(paths.DataDir, "console.db")}"))
            using (var target = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(copy, "console.db", CompressionLevel.Optimal);
                Add(zip, Path.Combine(paths.DataDir, "blobs"), "blobs");
                Add(zip, Path.Combine(paths.DataDir, "keys"), "keys");
            }
            File.Move(temp, final);
        }
        finally
        {
            foreach (var t in new[] { temp, copy }) if (File.Exists(t)) File.Delete(t);
        }
        return (name, new FileInfo(final).Length, Copy(final, name));
    }

    private static void Add(ZipArchive zip, string root, string prefix)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            zip.CreateEntryFromFile(file, prefix + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
    }

    private string? Copy(string file, string name)
    {
        if (CopyTo is null) return null;
        try
        {
            var folder = Path.Combine(CopyTo, "console");
            Directory.CreateDirectory(folder);
            File.Copy(file, Path.Combine(folder, name), overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Copy to {CopyTo} failed: {e.Message}";
        }
    }

    public void Delete(string name)
    {
        var path = Path.Combine(Folder, name);
        if (File.Exists(path)) File.Delete(path);
        if (CopyTo is not null)
            try { File.Delete(Path.Combine(CopyTo, "console", name)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
