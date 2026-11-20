using ServerAgent;
using ServerAgent.Files;

namespace Agent.Tests;

public sealed class FileServiceTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly FileService _files;

    public FileServiceTests() => _files = new FileService(new Areas(new AgentOptions { DataRoot = _dataRoot }, []));
    public void Dispose() => TempDir.Delete(_dataRoot);

    private string Put(string rel, byte[] data, DateTime? modified = null)
    {
        var path = Path.Combine(_dataRoot, "profiles", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    [Fact]
    public void List_rpt_newest_first()
    {
        Put(@"main\old.rpt", [1], DateTime.UtcNow.AddHours(-2));
        Put(@"main\new.rpt", [1, 2], DateTime.UtcNow.AddHours(-1));
        Put(@"main\notes.txt", [1]);
        var list = _files.ListFiles("profiles", "main", "*.rpt");
        Assert.Equal(["new.rpt", "old.rpt"], list.Select(f => f.Name));
        Assert.Equal(2, list[0].Size);
    }

    [Fact]
    public void List_of_a_missing_folder_is_empty() => Assert.Empty(_files.ListFiles("profiles", "nobody", "*.rpt"));

    [Fact]
    public void List_pbo_files_in_mpmissions()
    {
        var install = TempDir.Create();
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "mpmissions"));
            File.WriteAllBytes(Path.Combine(install, "mpmissions", "co40_Kestrel.Altis.pbo"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(install, "mpmissions", "readme.txt"), [1]);
            var areas = new Areas(new AgentOptions { DataRoot = _dataRoot }, [install]);
            var files = new FileService(areas);

            Assert.Equal(["co40_Kestrel.Altis.pbo"], files.ListFiles(areas.InstallArea(install), "mpmissions", "*.pbo").Select(f => f.Name));
            Assert.Throws<AreaException>(() => files.ReadFile(areas.InstallArea(install), @"mpmissions\co40_Kestrel.Altis.pbo", 0, 3));
        }
        finally { TempDir.Delete(install); }
    }

    [Fact]
    public void Read_returns_chunk_and_total_size()
    {
        Put(@"main\a.rpt", Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        var chunk = _files.ReadFile("profiles", @"main\a.rpt", 10, 5);
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, chunk.Data);
        Assert.Equal(100, chunk.TotalSize);
        Assert.Empty(_files.ReadFile("profiles", @"main\a.rpt", 100, 5).Data);
    }

    [Fact]
    public void Read_rejects_length_over_limit()
    {
        Put(@"main\a.rpt", [1]);
        Assert.Throws<AreaException>(() => _files.ReadFile("profiles", @"main\a.rpt", 0, 262145));
        Assert.Throws<AreaException>(() => _files.ReadFile("profiles", @"main\a.rpt", 0, 0));
        Assert.Throws<AreaException>(() => _files.ReadFile("profiles", @"main\a.rpt", 2, 1));
    }

    [Fact]
    public void Read_rejects_other_file_types()
    {
        Put(@"main\Arma3.cfg.bak", [1]);
        Assert.Throws<AreaException>(() => _files.ReadFile("profiles", @"main\Arma3.cfg.bak", 0, 1));
    }

    [Fact]
    public void List_rejects_unlisted_pattern() => Assert.Throws<AreaException>(() => _files.ListFiles("profiles", "main", "*.*"));
}
