using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Features.Missions;

public sealed record RequiredAddon(string Addon, string? ModName);
public sealed record Unscanned(string Name, string Note, bool IsInstall = false);
// What a mod list on an install provides (Ruling 7), plus who in the library provides what it doesn't, for naming the gaps.
public sealed record Provided(IReadOnlySet<string> Addons, IReadOnlySet<string> Worlds, IReadOnlyList<Unscanned> Unscanned,
    IReadOnlyDictionary<string, string> LibraryAddons, IReadOnlyDictionary<string, string> LibraryWorlds, IReadOnlyList<string>? NotInstalled = null);
public sealed record MissingAddon(string Addon, string? EdenMod, string? LibraryMod);
public sealed record CompatResult(string Status, string? MissingMap, MissingAddon[] Missing, string[] UnreadableMods);

// Phase 5b Rulings 7–8 (Amendment A's "match"): a mission version is Ready for a mod list when every addon its mission.sqm needs and
// its map are provided; Missing when something isn't and nothing unreadable could account for it; Unknown when every gap could
// come from a mod (or the install) whose files couldn't all be read — said, never guessed.
public static class Compatibility
{
    public const string Ready = "Ready", Missing = "Missing", Unknown = "Unknown";

    // Creator DLC folders don't count: no profile loads them yet (Ruling 7).
    public static readonly IReadOnlySet<string> CreatorDlc =
        new HashSet<string>(["gm", "vn", "csla", "ws", "spe", "rf", "ef", "contact"], StringComparer.OrdinalIgnoreCase);

    public static CompatResult Check(IReadOnlyList<RequiredAddon> required, string map, Provided provided)
    {
        var missing = required.Where(r => !provided.Addons.Contains(r.Addon)).DistinctBy(r => r.Addon.ToUpperInvariant())
            .Select(r => new MissingAddon(r.Addon, r.ModName, provided.LibraryAddons.GetValueOrDefault(r.Addon))).ToArray();
        var missingMap = map.Length > 0 && !provided.Worlds.Contains(map) ? map : null;
        if (missing.Length == 0 && missingMap is null) return new CompatResult(Ready, null, [], []);

        // Who could account for each gap: nobody when a scanned library mod provides it; else the install while it isn't fully read
        // (Eden names vanilla addons "Arma 3 …", never the install: final review M-2), plus the unreadable mod Eden named, or any.
        var could = new HashSet<string>(StringComparer.Ordinal);
        var unattributed = provided.Unscanned.Count > 0;
        var install = provided.Unscanned.Where(u => u.IsInstall).ToArray();
        foreach (var m in missing)
        {
            if (!unattributed) break;
            var mods = provided.Unscanned.Where(u => !u.IsInstall);
            var candidates = m.LibraryMod is not null ? []
                : install.Concat(m.EdenMod is { } eden ? mods.Where(u => Norm(u.Name) == Norm(eden)) : mods).ToArray();
            if (candidates.Length == 0) unattributed = false;
            else could.UnionWith(candidates.Select(c => c.Name));
        }
        if (unattributed && missingMap is not null)
        {
            if (provided.LibraryWorlds.ContainsKey(missingMap)) unattributed = false;
            else could.UnionWith(provided.Unscanned.Select(u => u.Name));
        }
        return unattributed && could.Count > 0
            ? new CompatResult(Unknown, missingMap, missing, could.Order(StringComparer.Ordinal).ToArray())
            : new CompatResult(Missing, missingMap, missing, []);
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Ruling 7: the install's root and official DLC folders, plus the list's enabled Client or Server-only items.
    public static async Task<Provided> ProvidedAsync(AppDb db, Guid installId, Guid? modListId)
    {
        var addons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var library = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var libraryWorlds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unscanned = new List<Unscanned>();

        // Rows waiting for a rescan (an install or update job ran) describe files that may be gone: they don't count (final review I-2).
        var install = await db.Installs.FindAsync(installId);
        if (install is { ScanStatus: not ScanStatus.NotScanned })
            foreach (var row in await db.InstallAddons.Where(a => a.ServerInstallId == installId).ToListAsync())
            {
                if (CreatorDlc.Contains(row.Folder)) (row.Kind == AddonKind.Addon ? library : libraryWorlds).TryAdd(row.Name, $"{row.Folder} (Creator DLC, not loaded)");
                else (row.Kind == AddonKind.Addon ? addons : worlds).Add(row.Name);
            }
        if (install is not null && InstallGap(install) is { } gap) unscanned.Add(new Unscanned(install.Name, gap, IsInstall: true));

        var listIds = modListId is { } listId
            ? await db.ModCollectionItems.Where(i => i.CollectionId == listId && i.Enabled && (i.Client || i.ServerOnly)).Select(i => i.ModId).ToListAsync()
            : [];
        var mods = await db.Mods.ToDictionaryAsync(m => m.Id);
        // Only installed list mods provide; one that isn't installed is a hard gap (Missing), named for the list, never "not scanned".
        var notInstalled = listIds.Where(id => mods.GetValueOrDefault(id) is not { Status: ModStatus.Installed }).Select(id => mods.GetValueOrDefault(id)?.Name ?? "?").ToList();
        var providing = listIds.Where(id => mods.GetValueOrDefault(id) is { Status: ModStatus.Installed, ScanStatus: not ScanStatus.NotScanned }).ToHashSet();
        foreach (var row in await db.ModAddons.ToListAsync())
        {
            var m = mods[row.ModId];
            if (providing.Contains(row.ModId)) (row.Kind == AddonKind.Addon ? addons : worlds).Add(row.Name);
            else if (!listIds.Contains(row.ModId)) (row.Kind == AddonKind.Addon ? library : libraryWorlds).TryAdd(row.Name, m.Name);
            else if (m.Status != ModStatus.Installed) (row.Kind == AddonKind.Addon ? library : libraryWorlds).TryAdd(row.Name, $"{m.Name} (in this list, not installed)");
        }
        foreach (var id in listIds)
            if (mods.GetValueOrDefault(id) is { Status: ModStatus.Installed, ScanStatus: not ScanStatus.Scanned } m)
                unscanned.Add(new Unscanned(m.Name, m.ScanStatus == ScanStatus.NotScanned ? "Not scanned yet." : m.ScanNote ?? m.ScanStatus.ToString()));
        return new Provided(addons, worlds, unscanned, library, libraryWorlds, notInstalled);
    }

    // Unreadable parts of the install outside Creator DLC folders (whose .ebo files are unreadable by design), or null.
    private static string? InstallGap(ServerInstall install) => install.ScanStatus switch
    {
        ScanStatus.Scanned => null,
        ScanStatus.NotScanned => "Not scanned yet.",
        ScanStatus.Failed => install.ScanNote ?? "The scan failed.",
        _ => (install.ScanNote ?? "").Split('\n').Where(l => l.Length > 0 && !CreatorDlc.Contains(l.Split('/')[0])).ToArray() is { Length: > 0 } lines
            ? string.Join("\n", lines) : null,
    };
}
