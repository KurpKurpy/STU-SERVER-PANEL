namespace ArmaFormats;

// Phase 4b Ruling 3: the names the file manager accepts. The panel pre-checks with it; the agent always checks again.
// "" is a space's root (fine to read); every other path is relative, separated by \ or /.
public static class FileNames
{
    public const int MaxSegment = 200, MaxPath = 240;
    public const string TempFolder = ".13c-tmp";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string? Problem(string relativePath)
    {
        if (relativePath is null) return "A path is needed.";
        if (relativePath.Length == 0) return null;
        if (relativePath.Length > MaxPath) return $"Paths are at most {MaxPath} characters.";
        foreach (var segment in relativePath.Split('\\', '/'))
        {
            if (segment.Length == 0) return "Paths are relative, with no empty parts.";
            if (segment is "." or "..") return "Paths must stay inside their folder.";
            if (segment.Length > MaxSegment) return $"Names are at most {MaxSegment} characters.";
            if (segment.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return $"\"{Clean(segment)}\" has a character Windows doesn't allow in names.";
            if (segment[^1] is '.' or ' ') return $"\"{segment}\" ends with a dot or space, which Windows drops.";
            if (Reserved.Contains(segment.Split('.')[0])) return $"\"{segment}\" is a name Windows reserves for a device.";
            if (segment.Equals(TempFolder, StringComparison.OrdinalIgnoreCase)) return $"{TempFolder} is the agent's own temporary folder.";
        }
        return null;
    }

    private static string Clean(string s) => new(s.Select(c => c < 32 ? '?' : c).ToArray());
}
