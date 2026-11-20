using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Mods;

namespace Agent.Tests;

public sealed class KeyServiceTests
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _install = TempDir.Create();
    private readonly Areas _areas;
    private readonly KeyService _keys;

    public KeyServiceTests()
    {
        _areas = new Areas(new AgentOptions { DataRoot = _dataRoot, InstallRoots = [_install] }, [_install]);
        _keys = new KeyService(_areas, new ModStore(_areas), NullLogger<KeyService>.Instance);
    }

    private string KeysDir => Path.Combine(_install, "keys");

    private void ServerKeys(params string[] names)
    {
        Directory.CreateDirectory(KeysDir);
        foreach (var n in names) File.WriteAllText(Path.Combine(KeysDir, n), n);
    }

    private ModRef Mod(long id, params string[] keys)
    {
        var folder = Path.Combine(_areas.WorkshopContent, id.ToString(), "keys");
        Directory.CreateDirectory(folder);
        foreach (var k in keys) File.WriteAllText(Path.Combine(folder, k), k);
        return new ModRef(id, null);
    }

    private static string[] Names(string dir) => Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public void Deploy_copies_new_keys_after_a_backup_and_never_deletes()
    {
        ServerKeys("a3.bikey", "old.bikey");
        var cba = Mod(450814997, "cba.bikey");

        var r = _keys.Deploy(_install, [cba]);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(["cba.bikey"], r.Added);
        Assert.Empty(r.Removed);
        Assert.Equal(["a3.bikey", "cba.bikey", "old.bikey"], Names(KeysDir));
        Assert.Equal(["a3.bikey", "old.bikey"], Names(r.BackupFolder!));

        var again = _keys.Deploy(_install, [cba]);
        Assert.True(again.Ok);
        Assert.Empty(again.Added);
        Assert.Null(again.BackupFolder); // nothing changed, nothing backed up
    }

    [Fact]
    public void Cleanup_removes_only_the_named_keys_keeps_a3_and_backs_up_first()
    {
        // The panel names the keys of library mods no kept list uses; Creator DLC keys (gm.bikey …) are never named.
        ServerKeys("a3.bikey", "old.bikey", "cba.bikey", "gm.bikey");

        var r = _keys.Cleanup(_install, ["old.bikey", "a3.bikey", "gone.bikey"]);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(["old.bikey"], r.Removed);
        Assert.Equal(["a3.bikey", "cba.bikey", "gm.bikey"], Names(KeysDir));
        Assert.Equal(["a3.bikey", "cba.bikey", "gm.bikey", "old.bikey"], Names(r.BackupFolder!));
        Assert.StartsWith(Path.Combine(_dataRoot, "backups", "keys"), r.BackupFolder);
    }

    [Fact]
    public void Keys_folder_is_created_when_missing()
    {
        var r = _keys.Deploy(_install, [Mod(1, "one.bikey")]);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(["one.bikey"], Names(KeysDir));
        Assert.Null(r.BackupFolder); // there was nothing to back up
    }

    [Fact]
    public void Foreign_installs_and_missing_mods_are_refused_without_changes()
    {
        ServerKeys("a3.bikey");

        Assert.False(_keys.Deploy(TempDir.Create(), [Mod(1, "one.bikey")]).Ok);
        Assert.False(_keys.Deploy(_install, [new ModRef(424242, null)]).Ok);
        Assert.False(_keys.Cleanup(_install, [@"..\a3.bikey"]).Ok);
        Assert.False(_keys.Cleanup(_install, ["x.pbo"]).Ok);
        Assert.Equal(["a3.bikey"], Names(KeysDir));
    }

    [Fact]
    public void List_shows_the_server_keys()
    {
        ServerKeys("a3.bikey", "cba.bikey");
        Assert.Equal(["a3.bikey", "cba.bikey"], _keys.List(_install).Select(k => k.Name).OrderBy(n => n));
    }
}
