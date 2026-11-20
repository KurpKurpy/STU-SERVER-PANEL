using System.IO.Compression;

namespace Panel.Features.Backups;

// Phase 7b: `Panel.exe restore <zip>`, run on the box with the 13th Console service stopped. The current console.db (and its -wal/-shm),
// blobs/ and keys/ move aside to before-restore-<time>/, then the backup's are written in. Nothing else in the data folder is touched.
// Exit codes: 0 done, 1 failed, 2 not a console backup.
public static class ConsoleRestore
{
    public static int Run(string zipPath, string dataDir)
    {
        try
        {
            dataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry("console.db") is null)
            {
                Console.Error.WriteLine($"{zipPath} isn't a console backup: it has no console.db.");
                return 2;
            }
            var aside = Path.Combine(dataDir, $"before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(aside);
            foreach (var file in new[] { "console.db", "console.db-wal", "console.db-shm" })
                if (File.Exists(Path.Combine(dataDir, file))) File.Move(Path.Combine(dataDir, file), Path.Combine(aside, file));
            foreach (var folder in new[] { "blobs", "keys" })
                if (Directory.Exists(Path.Combine(dataDir, folder))) Directory.Move(Path.Combine(dataDir, folder), Path.Combine(aside, folder));

            var written = 0;
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                if (entry.FullName != "console.db" && !entry.FullName.StartsWith("blobs/") && !entry.FullName.StartsWith("keys/")) continue;
                var target = Path.GetFullPath(Path.Combine(dataDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // a crafted zip can't climb out
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                written++;
            }
            Console.WriteLine($"Restored {written} files from {zipPath} into {dataDir}. The previous data is in {aside}. Start the 13th Console service now.");
            Console.WriteLine("If this backup came from another box or an earlier Windows installation, its keys won't unlock here: set each server profile's passwords and raw server.cfg lines again (then Apply), and the Discord webhook.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"The restore failed: {e.Message}");
            return 1;
        }
    }
}
