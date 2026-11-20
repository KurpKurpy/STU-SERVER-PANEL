using System.Globalization;
using System.Text.RegularExpressions;
using Contracts;

namespace ServerAgent.Mods;

// The mods on this box: Workshop items SteamCMD downloaded, and folders admins copied into localmods (Phase 5a Ruling 4).
public sealed partial class ModStore(Areas areas)
{
    [GeneratedRegex(@"^\s*name\s*=\s*""([^""\r\n]*)""", RegexOptions.Multiline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameLine();

    public string Folder(ModRef r) =>
        r.WorkshopId is { } id ? Path.Combine(areas.WorkshopContent, id.ToString(CultureInfo.InvariantCulture)) : Path.Combine(areas.LocalMods, r.LocalFolder!);

    // The first problem with these references (invalid or not on disk), or null.
    public string? Check(IEnumerable<ModRef>? refs)
    {
        foreach (var r in refs ?? [])
        {
            if (ModRef.Problem(r) is { } problem) return problem;
            if (!Directory.Exists(Folder(r))) return $"{char.ToUpperInvariant(r.Label[0])}{r.Label[1..]} isn't installed on this server.";
        }
        return null;
    }

    public DiskMod[] List()
    {
        var workshop = Directory.Exists(areas.WorkshopContent)
            ? Directory.GetDirectories(areas.WorkshopContent)
                .Select(d => long.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? new ModRef(id, null) : null)
            : [];
        var local = Directory.Exists(areas.LocalMods)
            ? Directory.GetDirectories(areas.LocalMods).Select(d => new ModRef(null, Path.GetFileName(d)))
            : [];
        return workshop.Concat(local).OfType<ModRef>().Where(r => ModRef.Problem(r) is null).Select(Describe).ToArray();
    }

    private DiskMod Describe(ModRef r)
    {
        var folder = Folder(r);
        return new DiskMod(r, Name(folder) ?? Path.GetFileName(folder), ModFiles.Size(folder), Directory.GetLastWriteTimeUtc(folder), ModFiles.KeyNames(folder));
    }

    // The Workshop title from meta.cpp, else the mod's own name from mod.cpp.
    private static string? Name(string folder)
    {
        foreach (var file in new[] { "meta.cpp", "mod.cpp" })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) continue;
            if (NameLine().Match(File.ReadAllText(path)) is { Success: true } m && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim();
        }
        return null;
    }
}
