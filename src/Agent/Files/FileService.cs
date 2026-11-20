using Contracts;

namespace ServerAgent.Files;

// Read-only file access for the panel: server logs and config files, always through an area.
public sealed class FileService(Areas areas)
{
    public const int MaxChunk = 262144;
    private static readonly string[] Patterns = ["*.rpt", "*.cfg", "*.pbo"]; // .pbo: listing only, for mission rotation
    private static readonly string[] Extensions = [".rpt", ".cfg"];

    public FileEntry[] ListFiles(string area, string relativeDir, string pattern)
    {
        if (!Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase)) throw new AreaException($"Listing \"{pattern}\" isn't allowed.");
        var dir = new DirectoryInfo(areas.Resolve(area, relativeDir));
        if (!dir.Exists) return [];
        return dir.EnumerateFiles(pattern)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new FileEntry(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToArray();
    }

    public FileChunk ReadFile(string area, string relativePath, long offset, int length)
    {
        if (length is < 1 or > MaxChunk) throw new AreaException($"Read between 1 and {MaxChunk} bytes at a time.");
        if (!Extensions.Contains(Path.GetExtension(relativePath ?? ""), StringComparer.OrdinalIgnoreCase))
            throw new AreaException("Only .rpt and .cfg files can be read.");
        var path = areas.Resolve(area, relativePath!);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var size = fs.Length;
            if (offset < 0 || offset > size) throw new AreaException("The offset is outside the file.");
            var data = new byte[(int)Math.Min(length, size - offset)];
            fs.Position = offset;
            fs.ReadExactly(data);
            return new FileChunk(data, size);
        }
        catch (FileNotFoundException) { throw new AreaException("No such file."); }
        catch (DirectoryNotFoundException) { throw new AreaException("No such file."); }
    }
}
