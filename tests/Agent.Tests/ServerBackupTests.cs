using System.IO.Compression;
using System.Text.RegularExpressions;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Backups;
using ServerAgent.Steam;

namespace Agent.Tests;

// Phase 7b Task 1: server backups are zips of profiles, configs and each install's mpmissions and keys (never mods), made and restored
// by the agent, with an optional copy elsewhere and pruning of the old safety copies (plan Global Constraints, Review Focus 2 and 4).
public sealed class ServerBackupTests : IDisposable
{
    private readonly string _data = TempDir.Create(), _install = TempDir.Create(), _readOnly = TempDir.Create();
    private readonly List<JobRunner> _runners = [];
    private bool _running;
    private Areas _areas = null!;

    public void Dispose() { foreach (var r in _runners) r.Dispose(); }

    private ServerBackups Make(string? copyTo = null)
    {
        var options = new AgentOptions { DataRoot = _data, InstallRoots = [_install, _readOnly], ReadOnlyInstallRoots = [_readOnly], BackupCopyTo = copyTo };
        _areas = new Areas(options, options.InstallRoots);
        return new ServerBackups(_areas, options, () => _running);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Profile => Path.Combine(_data, "profiles", "main", "Users", "x.Arma3Profile");
    private string Config => Path.Combine(_data, "configs", "main", "server.cfg");
    private string Mission => Path.Combine(_install, "mpmissions", "a.pbo");

    private void Seed()
    {
        Write(Profile, "p1");
        Write(Config, "hostname = 1;");
        Write(Mission, "pbo");
        Write(Path.Combine(_install, "keys", "a.bikey"), "key");
        Write(Path.Combine(_install, "@mod", "addons", "x.pbo"), "mod");
        Write(Path.Combine(_data, "workshop", "steamapps", "workshop", "content", "107410", "1", "x.pbo"), "workshop");
        Write(Path.Combine(_readOnly, "mpmissions", "ro.pbo"), "ro");
    }

    private string[] Entries(string file)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_data, "backups", "server", file));
        return [.. zip.Entries.Select(e => e.FullName).Order()];
    }

    [Fact]
    public async Task Create_zips_profiles_configs_and_each_installs_missions_and_keys_but_not_mods()
    {
        var backups = Make();
        Seed();

        var made = await backups.CreateAsync(null, CancellationToken.None);

        Assert.Matches(new Regex(@"^server-\d{8}-\d{6}\.zip$"), made.File);
        var area = _areas.InstallArea(_install);
        var entries = Entries(made.File);
        Assert.Contains("profiles/main/Users/x.Arma3Profile", entries);
        Assert.Contains("configs/main/server.cfg", entries);
        Assert.Contains($"installs/{area}/mpmissions/a.pbo", entries);
        Assert.Contains($"installs/{area}/keys/a.bikey", entries);
        Assert.Contains($"installs/{_areas.InstallArea(_readOnly)}/mpmissions/ro.pbo", entries); // reading a read-only install is fine
        Assert.DoesNotContain(entries, e => e.Contains("@mod") || e.Contains("workshop"));
        Assert.Equal(new FileInfo(Path.Combine(_data, "backups", "server", made.File)).Length, made.SizeBytes);
        Assert.Null(made.Note);
    }

    [Fact]
    public async Task Copy_to_folder_gets_the_same_zip_and_delete_removes_both()
    {
        var copyTo = TempDir.Create();
        var backups = Make(copyTo);
        Seed();

        var made = await backups.CreateAsync("manual", CancellationToken.None);

        var copy = Path.Combine(copyTo, "server", made.File);
        Assert.EndsWith("-manual.zip", made.File);
        Assert.Equal(made.SizeBytes, new FileInfo(copy).Length);
        Assert.True(backups.Delete(made.File).Ok);
        Assert.False(File.Exists(copy));
        Assert.False(File.Exists(Path.Combine(_data, "backups", "server", made.File)));
    }

    [Fact]
    public async Task An_unreachable_copy_to_folder_still_finishes_with_a_note()
    {
        var blocker = Path.Combine(TempDir.Create(), "a-file");
        File.WriteAllText(blocker, "x"); // a folder can't be made under a file
        var backups = Make(Path.Combine(blocker, "copies"));
        Seed();

        var made = await backups.CreateAsync(null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_data, "backups", "server", made.File)));
        Assert.StartsWith("Copy to ", made.Note);
    }

    [Fact]
    public async Task Restore_refuses_while_a_server_runs()
    {
        var backups = Make();
        Seed();
        var made = await backups.CreateAsync(null, CancellationToken.None);
        _running = true;

        var e = await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(made.File, ["profiles"], CancellationToken.None));
        Assert.Equal("Stop every server on this agent first", e.Message);
    }

    [Fact]
    public async Task Restore_writes_back_the_chosen_parts_after_a_before_restore_backup()
    {
        var backups = Make();
        Seed();
        var made = await backups.CreateAsync(null, CancellationToken.None);
        await Task.Delay(1100); // the before-restore backup gets its own second
        File.WriteAllText(Profile, "changed");
        File.WriteAllText(Config, "changed");
        File.Delete(Mission);
        File.Delete(Path.Combine(_readOnly, "mpmissions", "ro.pbo"));

        var (before, skipped) = await backups.RestoreAsync(made.File, ["profiles", "missions"], CancellationToken.None);

        Assert.Equal("p1", File.ReadAllText(Profile));
        Assert.Equal("pbo", File.ReadAllText(Mission));
        Assert.Equal("changed", File.ReadAllText(Config)); // configs weren't asked for
        Assert.False(File.Exists(Path.Combine(_readOnly, "mpmissions", "ro.pbo"))); // nothing is written to a read-only install
        Assert.Contains(skipped, s => s.Contains("read-only"));
        Assert.EndsWith("-before-restore.zip", before.File);
        using var zip = ZipFile.OpenRead(Path.Combine(_data, "backups", "server", before.File));
        using var reader = new StreamReader(zip.GetEntry("profiles/main/Users/x.Arma3Profile")!.Open());
        Assert.Equal("changed", reader.ReadToEnd()); // the state just before the restore
    }

    // Final review I3: a restore that fails part-way keeps its way back (the before-restore backup) and says what it already wrote.
    [Fact]
    public async Task A_restore_that_fails_part_way_keeps_its_way_back_and_says_what_it_wrote()
    {
        var backups = Make();
        Seed();
        var made = await backups.CreateAsync(null, CancellationToken.None);
        await Task.Delay(1100);
        File.WriteAllText(Profile, "changed");
        File.Delete(Config);
        Directory.CreateDirectory(Config); // a folder where the config file goes: writing it back fails

        var e = await Assert.ThrowsAsync<RestoreException>(() => backups.RestoreAsync(made.File, ["profiles", "configs"], CancellationToken.None));

        Assert.EndsWith("-before-restore.zip", e.Before.File);
        Assert.Contains("after writing profiles", e.Message);
        Assert.Contains(e.Before.File, e.Message);
        Assert.Equal("p1", File.ReadAllText(Profile)); // profiles went back before configs failed
        Assert.Empty(Directory.GetFiles(_data, "*.restore-tmp", SearchOption.AllDirectories));
    }

    // Final review I2: a server started during the before-restore backup stops the restore before anything is written.
    [Fact]
    public async Task Restore_checks_again_after_its_before_backup_that_no_server_started()
    {
        var calls = 0;
        var options = new AgentOptions { DataRoot = _data, InstallRoots = [_install], };
        _areas = new Areas(options, options.InstallRoots);
        var backups = new ServerBackups(_areas, options, () => ++calls > 1); // stopped at the first check, running by the second
        Seed();
        var made = await backups.CreateAsync(null, CancellationToken.None);
        await Task.Delay(1100);
        File.WriteAllText(Profile, "changed");

        var e = await Assert.ThrowsAsync<RestoreException>(() => backups.RestoreAsync(made.File, ["profiles"], CancellationToken.None));

        Assert.StartsWith("Stop every server on this agent first", e.Message);
        Assert.NotNull(e.Before);
        Assert.Equal("changed", File.ReadAllText(Profile));
    }

    [Fact]
    public async Task A_failed_restore_job_still_reports_its_before_backup()
    {
        var backups = Make();
        Seed();
        var made = await backups.CreateAsync(null, CancellationToken.None);
        await Task.Delay(1100);
        File.Delete(Config);
        Directory.CreateDirectory(Config);
        var runner = new JobRunner(_areas, new SteamCmdInstaller(new HttpClient(new NoNetwork())), NullLogger<JobRunner>.Instance, backups: backups);
        _runners.Add(runner);
        var done = new TaskCompletionSource<JobResult>();
        runner.Finished += r => done.TrySetResult(r);

        Assert.True(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerRestore, null, null, null, [], Backup: made.File, Parts: ["configs"])).Ok);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Ok);
        Assert.NotNull(result.Before);
        Assert.Contains("Nothing was restored", result.Error);
    }

    [Fact]
    public async Task Delete_and_restore_refuse_names_that_arent_backups()
    {
        var backups = Make();
        Assert.False(backups.Delete(@"..\x.zip").Ok);
        Assert.False(backups.Delete("server-1.zip").Ok);
        Assert.False(backups.Delete("console-20261121-030000.zip").Ok);
        await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(@"..\server-20261121-030000.zip", ["profiles"], CancellationToken.None));
        await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync("server-20261121-030000.zip", ["everything"], CancellationToken.None));
    }

    [Fact]
    public void Prune_deletes_safety_copies_older_than_the_limit_and_keeps_server_zips()
    {
        var backups = Make();
        string Folder(params string[] parts) { var d = Path.Combine([_data, "backups", .. parts]); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "f"), "x"); return d; }
        var old = Folder("profiles", "main", "20260101-000000-000");
        var recent = Folder("profiles", "main", "20261120-000000-000");
        var oldFiles = Folder("files", "mpmissions", "20260101-000000-000");
        var zip = Path.Combine(_data, "backups", "server", "server-20260101-000000.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.WriteAllText(zip, "zip");
        foreach (var d in new[] { old, oldFiles }) Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddDays(-60));

        Assert.Equal(0, backups.PruneSafetyCopies(0));
        Assert.Equal(2, backups.PruneSafetyCopies(30));
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(oldFiles));
        Assert.True(Directory.Exists(recent));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task Backup_jobs_run_in_the_job_queue_without_SteamCMD()
    {
        var backups = Make();
        Seed();
        var installer = new SteamCmdInstaller(new HttpClient(new NoNetwork()));
        var runner = new JobRunner(_areas, installer, NullLogger<JobRunner>.Instance, backups: backups);
        _runners.Add(runner);
        var done = new TaskCompletionSource<JobResult>();
        runner.Finished += r => done.TrySetResult(r);

        Assert.True(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerBackup, null, null, null, [], Label: "scheduled")).Ok);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Ok, result.Error);
        Assert.EndsWith("-scheduled.zip", result.Made!.File);
        Assert.False(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerRestore, null, null, null, [], Backup: "nope.zip", Parts: ["profiles"])).Ok);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No network in tests: " + request.RequestUri);
    }
}
