using System.Text;

namespace ServerAgent.Logs;

// Follows the newest log file in a folder (the server starts a new RPT per launch, spike F3).
// Returns complete lines only; CRLF is stripped (spike F4) and bytes decode as UTF-8 with replacement.
// Not thread-safe: the caller polls it from one place at a time.
public sealed class RptTail(string directory, string pattern)
{
    private const int MaxReadBytes = 1 << 20;
    private string? _file;
    private long _offset;

    // At most maxLines whole lines and maxBytes raw bytes; the rest waits for the next poll.
    public IReadOnlyList<string> Poll(int maxLines = int.MaxValue, int maxBytes = MaxReadBytes)
    {
        maxBytes = Math.Clamp(maxBytes, 1, MaxReadBytes);
        var newest = Newest();
        if (newest is null) return [];
        if (!newest.Equals(_file, StringComparison.OrdinalIgnoreCase))
        {
            _file = newest;
            _offset = 0;
        }

        byte[] buffer;
        try
        {
            using var fs = Open(_file);
            if (fs.Length < _offset) _offset = 0; // recreated under the same name
            buffer = new byte[(int)Math.Min(fs.Length - _offset, maxBytes)];
            fs.Position = _offset;
            fs.ReadExactly(buffer);
        }
        catch (IOException) { return []; }

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length && lines.Count < maxLines; i++)
        {
            if (buffer[i] != '\n') continue;
            var end = i > start && buffer[i - 1] == '\r' ? i - 1 : i;
            lines.Add(Encoding.UTF8.GetString(buffer, start, end - start));
            start = i + 1;
        }
        if (start == 0 && buffer.Length == maxBytes) // one line longer than the limit: hand it over rather than stall
        {
            lines.Add(Encoding.UTF8.GetString(buffer));
            start = buffer.Length;
        }
        _offset += start;
        return lines;
    }

    // Start near the end of the newest file: max(0, length - keepLastBytes), moved forward to the next line start.
    public void SeekToEnd(long keepLastBytes)
    {
        _file = Newest();
        _offset = 0;
        if (_file is null) return;
        try
        {
            using var fs = Open(_file);
            var offset = Math.Max(0, fs.Length - keepLastBytes);
            if (offset > 0)
            {
                fs.Position = offset - 1;
                int b;
                while ((b = fs.ReadByte()) != -1 && b != '\n') { }
                offset = fs.Position;
            }
            _offset = offset;
        }
        catch (IOException) { }
    }

    private string? Newest()
    {
        if (!Directory.Exists(directory)) return null;
        try { return new DirectoryInfo(directory).EnumerateFiles(pattern).MaxBy(f => f.LastWriteTimeUtc)?.FullName; }
        catch (IOException) { return null; }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
