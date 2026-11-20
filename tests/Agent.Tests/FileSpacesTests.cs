using System.Diagnostics;
using Contracts;
using ServerAgent;
using ServerAgent.Files;

namespace Agent.Tests;

// Phase 4b Rulings 2–4 and 9: the agent's own table of file-manager spaces, against real folders.
public sealed class FileSpacesTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _install = TempDir.Create();
    private readonly string _readOnly = TempDir.Create();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSpaces _spaces;

    public FileSpacesTests()
    {
        var areas = new Areas(new AgentOptions { DataRoot = _dataRoot, ReadOnlyInstallRoots = [_readOnly] }, [_install, _readOnly]);
        _spaces = new FileSpaces(areas, _running.Contains);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dataRoot, _install, _readOnly }) TempDir.Delete(d);
    }

    private FileSpace Install(string name, string? root = null) => new(name, root ?? _install);
    private static FileSpace Global(string name) => new(name, null);

    [Fact]
    public void Each_space_resolves_under_its_own_folder()
    {
        Assert.Equal(Path.Combine(_install, "mpmissions", "a.pbo"), _spaces.Resolve(Install("mpmissions"), "a.pbo").Full);
        Assert.Equal(Path.Combine(_install, "keys", "a.bikey"), _spaces.Resolve(Install("keys"), "a.bikey").Full);
        Assert.Equal(Path.Combine(_dataRoot, "profiles", "main", "x.rpt"), _spaces.Resolve(Global("profiles"), @"main\x.rpt").Full);
        Assert.Equal(Path.Combine(_dataRoot, "configs", "main", "server.cfg"), _spaces.Resolve(Global("configs"), @"main\server.cfg").Full);
        Assert.Equal(Path.Combine(_dataRoot, "localmods", "@x"), _spaces.Resolve(Global("localmods"), "@x").Full);
        Assert.Equal(Path.Combine(_dataRoot, "workshop", "steamapps", "workshop", "content", "107410", "1"), _spaces.Resolve(Global("workshop"), "1").Full);
        Assert.Equal(Path.Combine(_dataRoot, "backups"), _spaces.Resolve(Global("backups"), "").Full);
    }

    [Theory, InlineData(@"..\x"), InlineData(@"a\..\..\x"), InlineData(@"C:\Windows\win.ini"), InlineData(@"\\host\share\x"), InlineData("/etc"), InlineData("x:stream")]
    public void Dot_dot_rooted_and_stream_paths_are_refused(string rel) => Assert.Throws<AreaException>(() => _spaces.Resolve(Global("profiles"), rel));

    [Fact]
    public void A_junction_inside_a_space_is_refused()
    {
        var link = Path.Combine(_dataRoot, "profiles", "j");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, @"C:\Windows"]) { UseShellExecute = false, CreateNoWindow = true })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.Throws<AreaException>(() => _spaces.Resolve(Global("profiles"), "j"));
        Assert.Throws<AreaException>(() => _spaces.Resolve(Global("profiles"), @"j\win.ini"));
    }

    [Fact]
    public void An_unregistered_install_is_refused()
    {
        var other = TempDir.Create();
        try { Assert.Throws<AreaException>(() => _spaces.Resolve(Install("mpmissions", other), "a.pbo")); }
        finally { TempDir.Delete(other); }
        Assert.Throws<AreaException>(() => _spaces.Resolve(new FileSpace("mpmissions", null), "a.pbo"));
    }

    [Theory, InlineData("install"), InlineData("battleye"), InlineData("dll"), InlineData(""), InlineData("blobs")]
    public void Only_the_named_spaces_exist(string name)
    {
        Assert.Throws<AreaException>(() => _spaces.Resolve(Install(name), ""));
        Assert.Throws<AreaException>(() => _spaces.Resolve(Install("mpmissions"), @"..\battleye\BEServer_x64.dll"));
    }

    [Theory, InlineData("configs"), InlineData("workshop"), InlineData("backups")]
    public void Read_only_spaces_refuse_writes(string space) =>
        Assert.Contains("read-only", Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(Global(space), "x.txt", isFile: true)).Message);

    [Fact]
    public void A_read_only_install_refuses_writes()
    {
        Assert.Equal(Areas.ReadOnlyMessage, Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(Install("mpmissions", _readOnly), "a.pbo", isFile: true)).Message);
        Assert.NotNull(_spaces.Resolve(Install("mpmissions", _readOnly), "a.pbo")); // reading is fine
    }

    [Fact]
    public void The_space_root_cant_be_written()
    {
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(Global("localmods"), "", isFile: false));
    }

    [Fact]
    public void Keys_take_only_bikey_files()
    {
        Assert.NotNull(_spaces.ResolveForWrite(Install("keys"), "cba.bikey", isFile: true));
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(Install("keys"), "notes.txt", isFile: true));
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(Install("keys"), "old", isFile: false)); // no folders either
    }

    [Theory, InlineData("mpmissions", "x.exe"), InlineData("mpmissions", "x.DLL"), InlineData("profiles", @"main\run.bat"), InlineData("profiles", @"main\a.ps1")]
    public void Executables_are_refused_outside_localmods(string space, string path)
    {
        var s = space == "mpmissions" ? Install(space) : Global(space);
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(s, path, isFile: true));
        Assert.NotNull(_spaces.ResolveForWrite(Global("localmods"), @"@x\" + Path.GetFileName(path), isFile: true));
    }

    [Fact]
    public void A_running_servers_profile_file_is_refused()
    {
        _running.Add("main");
        var profile = Global("profiles");
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(profile, @"main\Users\main\main.Arma3Profile", isFile: true));
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(profile, "main", isFile: false));
        Assert.Throws<AreaException>(() => _spaces.ResolveForWrite(profile, @"main\Users", isFile: false));
        Assert.NotNull(_spaces.ResolveForWrite(profile, @"main\BattlEye\bans.txt", isFile: true)); // BattlEye's files may change while it runs
        Assert.NotNull(_spaces.ResolveForWrite(profile, @"other\Users\other\other.Arma3Profile", isFile: true));
    }

    [Fact]
    public void Backup_keys_name_the_space_and_install()
    {
        Assert.Equal("profiles", _spaces.Resolve(Global("profiles"), "x").BackupKey);
        Assert.StartsWith("mpmissions-install-", _spaces.Resolve(Install("mpmissions"), "x").BackupKey);
    }
}
