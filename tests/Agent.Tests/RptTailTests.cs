using System.Text;
using ServerAgent.Logs;

namespace Agent.Tests;

public sealed class RptTailTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private string Write(string name, string text, bool append = false)
    {
        var path = Path.Combine(_dir, name);
        using (var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(Encoding.UTF8.GetBytes(text));
        return path;
    }

    [Fact]
    public void Poll_returns_appended_complete_lines_only()
    {
        var tail = new RptTail(_dir, "*.rpt");
        Write("a.rpt", "a\nb\nc");
        Assert.Equal(["a", "b"], tail.Poll());
        Write("a.rpt", "d\n", append: true);
        Assert.Equal(["cd"], tail.Poll());
        Assert.Empty(tail.Poll());
    }

    [Fact]
    public void Poll_strips_crlf_line_endings()
    {
        var tail = new RptTail(_dir, "*.rpt");
        Write("a.rpt", "== header\r\n 3:54:57 first\r\n");
        Assert.Equal(["== header", " 3:54:57 first"], tail.Poll());
    }

    [Fact]
    public void Poll_switches_to_a_newer_file()
    {
        var tail = new RptTail(_dir, "*.rpt");
        var old = Write("old.rpt", "old 1\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(["old 1"], tail.Poll());
        Write("new.rpt", "new 1\nnew 2\n");
        Assert.Equal(["new 1", "new 2"], tail.Poll());
    }

    [Fact]
    public void Poll_honours_the_line_limit_and_continues_next_time()
    {
        var tail = new RptTail(_dir, "*.rpt");
        Write("a.rpt", "1\n2\n3\n");
        Assert.Equal(["1", "2"], tail.Poll(maxLines: 2));
        Assert.Equal(["3"], tail.Poll(maxLines: 2));
    }

    [Fact]
    public void Poll_honours_a_byte_limit_and_continues_next_time()
    {
        var tail = new RptTail(_dir, "*.rpt");
        Write("a.rpt", string.Concat(Enumerable.Range(0, 10).Select(i => $"line {i:D3}\n"))); // 9 bytes a line
        Assert.Equal(["line 000", "line 001"], tail.Poll(maxBytes: 25));
        Assert.Equal(["line 002", "line 003"], tail.Poll(maxBytes: 25));
    }

    [Fact]
    public void SeekToEnd_keeps_only_the_last_bytes_rounded_to_a_line()
    {
        Write("a.rpt", "first line\nsecond line\nthird\n");
        var tail = new RptTail(_dir, "*.rpt");
        tail.SeekToEnd(10); // lands inside "second line\n" → starts at "third"
        Assert.Equal(["third"], tail.Poll());
    }

    [Fact]
    public void SeekToEnd_zero_skips_everything_already_written()
    {
        Write("a.rpt", "old\n");
        var tail = new RptTail(_dir, "*.rpt");
        tail.SeekToEnd(0);
        Assert.Empty(tail.Poll());
        Write("a.rpt", "new\n", append: true);
        Assert.Equal(["new"], tail.Poll());
    }

    [Fact]
    public void Poll_on_missing_directory_returns_nothing() =>
        Assert.Empty(new RptTail(Path.Combine(_dir, "nope"), "*.rpt").Poll());
}
