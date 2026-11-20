using System.Diagnostics;
using ServerAgent;

namespace Agent.Tests;

public sealed class AreasTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _installRoot = TempDir.Create();
    private readonly Areas _areas;

    public AreasTests() => _areas = new Areas(new AgentOptions { DataRoot = _dataRoot }, [_installRoot]);

    public void Dispose()
    {
        TempDir.Delete(_dataRoot);
        TempDir.Delete(_installRoot);
    }

    [Theory, InlineData(@"..\x"), InlineData(@"a\..\..\x"), InlineData(@"C:\Windows\win.ini"), InlineData(@"\\host\share\x"), InlineData("/etc")]
    public void Resolve_rejects_paths_outside_the_area(string rel) => Assert.Throws<AreaException>(() => _areas.Resolve("profiles", rel));

    [Fact]
    public void Resolve_rejects_a_junction_inside_the_area()
    {
        var link = Path.Combine(_dataRoot, "profiles", "j");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, @"C:\Windows"]) { UseShellExecute = false, CreateNoWindow = true })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.Throws<AreaException>(() => _areas.Resolve("profiles", @"j\win.ini"));
    }

    [Fact]
    public void Resolve_rejects_unknown_area() => Assert.Throws<AreaException>(() => _areas.Resolve("windows", "x.txt"));

    [Fact]
    public void Resolve_returns_full_path_inside_the_area() =>
        Assert.Equal(Path.Combine(_dataRoot, "configs", "main", "server.cfg"), _areas.Resolve("configs", @"main\server.cfg"));

    [Fact]
    public void Resolve_of_an_empty_path_is_the_area_root() =>
        Assert.Equal(Path.Combine(_dataRoot, "profiles"), _areas.Resolve("profiles", ""));

    [Fact]
    public void Install_areas_are_read_only_and_described()
    {
        var install = _areas.InstallArea(_installRoot);
        Assert.StartsWith("install-", install);
        Assert.True(_areas.IsReadOnly(install));
        Assert.False(_areas.IsReadOnly("profiles"));

        var described = _areas.Describe();
        Assert.Contains(described, a => a is { Area: "profiles", Exists: true, Writable: true, ReadOnly: false });
        Assert.Contains(described, a => a is { Area: "configs", Exists: true, Writable: true, ReadOnly: false });
        Assert.Contains(described, a => a.Area == install && a.ReadOnly && !a.Writable && a.Path == _installRoot);
    }
}

internal static class TempDir
{
    public static string Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "13c-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
