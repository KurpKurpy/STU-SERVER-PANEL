namespace ArmaFormats;

// A path inside a mission ("scripts/init.sqf"): forward slashes, relative, and safe to use as a Windows file name (Phase 4 Ruling 6).
public static class MissionPath
{
    public const int MaxLength = 255;
    private static readonly char[] Bad = ['<', '>', ':', '"', '|', '?', '*', '\\'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Normalize(string path) => path.Replace('\\', '/');

    // Null when the path is fine, otherwise why it isn't.
    public static string? Problem(string path)
    {
        if (string.IsNullOrEmpty(path)) return "The path is empty.";
        if (path.Length > MaxLength) return $"\"{Clip(path)}\" is longer than {MaxLength} characters.";
        if (path.StartsWith('/')) return $"\"{Clip(path)}\" starts at the root; paths are relative to the mission.";
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) return $"\"{Clip(path)}\" has an empty folder name.";
            if (segment is "." or "..") return $"\"{Clip(path)}\" uses \".\" or \"..\".";
            if (segment.Any(c => char.IsControl(c) || Bad.Contains(c))) return $"\"{Clip(path)}\" has a character Windows doesn't allow in file names.";
            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' ')) return $"\"{Clip(path)}\" has a name that starts with a space or ends with a dot or space.";
            if (Reserved.Contains(segment.Split('.')[0])) return $"\"{Clip(path)}\" uses a name Windows reserves ({segment.Split('.')[0]}).";
        }
        return null;
    }

    // The first pair of paths that differ only in case: Windows would store them as one file.
    public static string? DuplicateProblem(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
            if (!seen.Add(p)) return $"\"{Clip(p)}\" appears twice (names that differ only in case are the same file on Windows).";
        return null;
    }

    private static string Clip(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
