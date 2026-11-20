using System.IO.Compression;
using ArmaFormats;

namespace Panel.Features.Missions;

public sealed record UploadedMission(string ClassName, string Map, Dictionary<string, byte[]> Files, IReadOnlyDictionary<string, string> PboProperties,
    byte[]? OriginalPbo, string FileName);

// Reads and checks an upload before anything is stored (design §5.4 step 1, Phase 4a Ruling 6): one .pbo, one .zip, or the files of a
// dropped folder named by their relative paths. Every problem comes back as text for a field error on "file".
public static class MissionUpload
{
    public const long MaxRequestBytes = 200_000_000;
    public const int MaxEntries = 5000;
    public const long MaxUnpackedBytes = 500L * 1024 * 1024;
    public const long MaxFileBytes = 200L * 1024 * 1024;
    public const int MaxNameLength = 150;
    private const string NameRule = "The mission must be named Name.Map, like co10_Escape.Altis (the map is the part after the last dot).";

    public static async Task<(UploadedMission? Mission, string? Problem)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return (null, "Upload a .pbo, a .zip or a mission folder.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException or IOException)
        {
            return (null, $"The upload couldn't be read ({e.Message}). Uploads are at most {MaxRequestBytes / 1_000_000} MB.");
        }
        var files = form.Files;
        if (files.Count == 0) return (null, "Choose a .pbo, a .zip or a mission folder to upload.");
        var single = files.Count == 1 ? files[0] : null;
        try
        {
            if (single is not null && Ext(single.FileName) == ".pbo") return FromPbo(single);
            if (single is not null && Ext(single.FileName) == ".zip") return FromZip(single);
            if (files.Any(f => !Normalize(f.FileName).Contains('/')))
                return (null, "Upload a .pbo, a .zip, or a whole mission folder.");
            return FromFolder(files);
        }
        catch (InvalidDataException e) { return (null, $"The archive is damaged: {e.Message}"); }
    }

    // "co10_Escape.Altis" → (co10_Escape, Altis).
    public static (string ClassName, string Map)? SplitName(string name)
    {
        if (name.Length is 0 or > MaxNameLength || name.Contains('/') || MissionPath.Problem(name) is not null) return null;
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? null : (name[..dot], name[(dot + 1)..]);
    }

    // The library fields description.ext can fill in (Ruling 15); preprocessor lines are skipped, and a file that won't parse gives nothing.
    public static (string? Name, string? GameType) DescriptionInfo(byte[]? ext)
    {
        if (ext is null) return (null, null);
        try
        {
            var text = string.Join('\n', MissionScan.Text(ext).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            var items = ConfigDocument.Parse(text).Items;
            var name = MissionScan.Str(items, "onLoadName") ?? MissionScan.Str(items, "briefingName");
            var type = MissionScan.Class(items, "Header")?.Body?.OfType<CfgAssignment>()
                .FirstOrDefault(a => a.Name.Equals("gameType", StringComparison.OrdinalIgnoreCase))?.Value switch
            {
                CfgWord w => w.Value,
                CfgString s => s.Value,
                _ => null,
            };
            return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), type);
        }
        catch (ConfigSyntaxException) { return (null, null); }
    }

    private static string Ext(string fileName) => Path.GetExtension(fileName).ToLowerInvariant();
    private static string Normalize(string path) => MissionPath.Normalize(path).Trim('/');

    private static (UploadedMission?, string?) FromPbo(IFormFile file)
    {
        if (SplitName(Path.GetFileNameWithoutExtension(Normalize(file.FileName).Split('/')[^1])) is not { } name) return (null, NameRule);
        var bytes = Read(file.OpenReadStream(), MaxRequestBytes);
        PboFile pbo;
        try { pbo = Pbo.Read(new MemoryStream(bytes)); }
        catch (PboException e) { return (null, e.Message); }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in pbo.Entries) files[e.Path] = e.Data;
        return Checked(name, files, pbo.Properties, bytes, file.FileName);
    }

    private static (UploadedMission?, string?) FromZip(IFormFile file)
    {
        using var zip = new ZipArchive(file.OpenReadStream(), ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        if (entries.Count > MaxEntries) return (null, $"The archive has more than {MaxEntries} files.");
        var files = new List<(string Path, byte[] Data)>();
        long total = 0;
        foreach (var entry in entries)
        {
            var path = Normalize(entry.FullName);
            if (MissionPath.Problem(path) is { } problem) return (null, problem);
            using var s = entry.Open();
            var data = Read(s, MaxFileBytes); // the real bytes, not the size the archive claims
            if (data.LongLength > MaxFileBytes) return (null, $"{path} is larger than {MaxFileBytes / 1024 / 1024} MB.");
            if ((total += data.LongLength) > MaxUnpackedBytes) return (null, $"The archive unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB.");
            files.Add((path, data));
        }
        return Rooted(files, Path.GetFileNameWithoutExtension(Normalize(file.FileName).Split('/')[^1]), file.FileName);
    }

    private static (UploadedMission?, string?) FromFolder(IFormFileCollection form)
    {
        if (form.Count > MaxEntries) return (null, $"The folder has more than {MaxEntries} files.");
        var files = new List<(string Path, byte[] Data)>();
        long total = 0;
        foreach (var f in form)
        {
            var path = Normalize(f.FileName);
            if (MissionPath.Problem(path) is { } problem) return (null, problem);
            var data = Read(f.OpenReadStream(), MaxFileBytes);
            if ((total += data.LongLength) > MaxUnpackedBytes) return (null, $"The folder is larger than {MaxUnpackedBytes / 1024 / 1024} MB.");
            files.Add((path, data));
        }
        return Rooted(files, null, form[0].FileName.Split('/', '\\')[0]);
    }

    // The mission root is the folder holding mission.sqm; its name (or the archive's, when mission.sqm sits at the top) is Name.Map.
    private static (UploadedMission?, string?) Rooted(List<(string Path, byte[] Data)> files, string? topName, string uploadName)
    {
        var sqms = files.Where(f => f.Path.Split('/')[^1].Equals("mission.sqm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sqms.Count == 0) return (null, "There's no mission.sqm in the upload.");
        if (sqms.Count > 1) return (null, "The upload has more than one mission.sqm; upload one mission at a time.");
        var root = sqms[0].Path.Contains('/') ? sqms[0].Path[..sqms[0].Path.LastIndexOf('/')] : "";
        var rootName = root.Length > 0 ? root.Split('/')[^1] : topName ?? "";
        if (SplitName(rootName) is not { } name) return (null, NameRule);
        var prefix = root.Length > 0 ? root + "/" : "";
        var outside = files.FirstOrDefault(f => !f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (outside.Path is not null) return (null, $"\"{outside.Path}\" is outside the mission folder {rootName}.");
        var mission = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, data) in files)
        {
            var inner = path[prefix.Length..];
            if (!mission.TryAdd(inner, data)) return (null, $"\"{inner}\" appears twice (names that differ only in case are the same file on Windows).");
        }
        return Checked(name, mission, new Dictionary<string, string>(), null, uploadName);
    }

    private static (UploadedMission?, string?) Checked((string ClassName, string Map) name, Dictionary<string, byte[]> files,
        IReadOnlyDictionary<string, string> properties, byte[]? original, string uploadName)
    {
        if (!files.TryGetValue("mission.sqm", out var sqm)) return (null, "mission.sqm must be at the top of the mission folder.");
        try { MissionScan.Addons(sqm); }
        catch (Exception e) when (e is ConfigSyntaxException or RapException) { return (null, $"mission.sqm can't be read: {e.Message}"); }
        return (new UploadedMission(name.ClassName, name.Map, files, properties, original, Normalize(uploadName).Split('/')[^1]), null);
    }

    // At most max + 1 bytes, so an oversized stream is noticed without reading all of it.
    private static byte[] Read(Stream s, long max)
    {
        using (s)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > max) break;
            }
            return ms.ToArray();
        }
    }
}
