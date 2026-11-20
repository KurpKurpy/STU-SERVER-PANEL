using ArmaFormats;
using Contracts;

namespace ServerAgent.Mods;

// Which addons (CfgPatches) and maps (CfgWorlds) a mod or a server install provides (Phase 5b Rulings 2–3, 6). Only PBO headers and
// their config.bin/config.cpp entries are read, never written; anything unreadable is listed with its reason, never guessed at.
// Enqueue answers at once; scans run one at a time and each target is reported through Scanned.
public sealed class AddonScanner(Areas areas, ModStore mods, ILogger<AddonScanner> log) : IDisposable
{
    private const int MaxUnreadable = 200; // per folder, to keep a report small
    public const int MaxNames = 20_000;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public event Action<AddonScan>? Scanned;

    public CommandResult Enqueue(AddonScanRequest request)
    {
        foreach (var m in request.Mods ?? [])
            if (ModRef.Problem(m) is { } problem) return new CommandResult(false, problem);
        foreach (var m in request.Mods ?? []) Run(() => ScanMod(m));
        // A root the agent no longer lists is reported failed rather than refusing the batch, which stopped every automatic scan (final review M-4).
        foreach (var r in request.InstallRoots ?? [])
            Run(() => areas.FindInstallRoot(r) is { } root ? ScanInstall(root)
                : new AddonScan(null, r, [], "That server folder isn't one of this agent's InstallRoots."));
        return new CommandResult(true, null);
    }

    private void Run(Func<AddonScan> scan) => _ = Task.Run(async () =>
    {
        try { await _one.WaitAsync(_stop.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            var result = scan();
            Scanned?.Invoke(result);
        }
        catch (Exception e) { log.LogWarning(e, "An addon scan failed"); }
        finally { _one.Release(); }
    });

    private AddonScan ScanMod(ModRef mod)
    {
        var folder = mods.Folder(mod);
        if (!Directory.Exists(folder)) return new AddonScan(mod, null, [], $"{char.ToUpperInvariant(mod.Label[0])}{mod.Label[1..]} isn't installed on this server.");
        return new AddonScan(mod, null, [ScanFolder("", Path.Combine(folder, "addons"))], null);
    }

    // The root addons plus each top-level folder with addons (the DLC). Folders starting with @ or ! are mods, loaded only by -mod.
    private AddonScan ScanInstall(string root)
    {
        try
        {
            var folders = new List<ScannedFolder> { ScanFolder("", Path.Combine(root, "addons")) };
            foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('@') || name.StartsWith('!') || !Directory.Exists(Path.Combine(dir, "addons"))) continue;
                folders.Add(ScanFolder(name, Path.Combine(dir, "addons")));
            }
            return new AddonScan(null, root, folders.ToArray(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AddonScan(null, root, [], $"Couldn't read the server folder: {e.Message}"); }
    }

    private static ScannedFolder ScanFolder(string name, string addonsDir)
    {
        var addons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        void Unreadable(string what) { if (unreadable.Count < MaxUnreadable) unreadable.Add(what); }
        // A report over the hub's 1 MiB limit closes the link in a loop (final review I-6): names are capped per folder, long ones dropped.
        var overflow = false;
        void Add(HashSet<string> set, IEnumerable<string> names)
        {
            foreach (var n in names.Where(n => n.Length is > 0 and <= 200))
                if (set.Count < MaxNames) set.Add(n);
                else overflow = true;
        }
        if (Directory.Exists(addonsDir))
        {
            foreach (var file in Directory.EnumerateFiles(addonsDir).Order(StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".ebo", StringComparison.OrdinalIgnoreCase)) { Unreadable($"{fileName}: encrypted"); continue; }
                if (!fileName.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // A running server holds its PBOs open for reading; sharing lets the scan read them too.
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var header = Pbo.ReadHeader(fs);
                    foreach (var entry in header.Entries.Where(IsConfig))
                    {
                        var names = ConfigScan.Names(Pbo.ReadEntry(fs, header, entry));
                        Add(addons, names.Addons);
                        Add(worlds, names.Worlds);
                        if (names.Unreadable is { } why) Unreadable($"{fileName}: {why}");
                    }
                }
                catch (Exception e) when (e is PboException or IOException or UnauthorizedAccessException) { Unreadable($"{fileName}: {e.Message}"); }
            }
        }
        if (overflow) unreadable.Insert(0, $"too many classes (over {MaxNames:N0}); the rest weren't read");
        return new ScannedFolder(name, addons.Order(StringComparer.OrdinalIgnoreCase).ToArray(), worlds.Order(StringComparer.OrdinalIgnoreCase).ToArray(), unreadable.ToArray());
    }

    private static bool IsConfig(PboHeaderEntry e) => Path.GetFileName(e.Path) is var n
        && (n.Equals("config.bin", StringComparison.OrdinalIgnoreCase) || n.Equals("config.cpp", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _stop.Cancel();
}
