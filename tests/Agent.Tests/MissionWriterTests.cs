using System.Net;
using System.Security.Cryptography;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Missions;
using ServerAgent.Mods;
using ServerAgent.Steam;

namespace Agent.Tests;

public class MissionWriterTests
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _install = TempDir.Create();
    private readonly string _readOnly = TempDir.Create();

    private Areas NewAreas() => new(new AgentOptions { DataRoot = _dataRoot, InstallRoots = [_install, _readOnly], ReadOnlyInstallRoots = [_readOnly] },
        [_install, _readOnly]);

    // Serves one blob the way the panel's /api/agent/blobs/{sha} does, or misbehaves on request.
    private sealed class Panel(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request.RequestUri.AbsolutePath));
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private (MissionWriter Writer, Panel Panel) New(Func<string, HttpResponseMessage> respond, TimeSpan? retry = null)
    {
        var panel = new Panel(respond);
        var http = new HttpClient(panel) { BaseAddress = new Uri("http://panel.test/") };
        return (new MissionWriter(NewAreas(), http, NullLogger<MissionWriter>.Instance, retry ?? TimeSpan.FromMilliseconds(100)), panel);
    }

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static Task<DeployResult> FinalAsync(MissionWriter writer, Guid id)
    {
        var done = new TaskCompletionSource<DeployResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Changed += r => { if (r.DeploymentId == id && r.Status is DeployResult.Done or DeployResult.Failed) done.TrySetResult(r); };
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private string Mission(string name = "co10_Escape.Altis.pbo") => Path.Combine(_install, "mpmissions", name);

    [Fact]
    public async Task Write_replaces_after_backup_and_verifies_the_hash()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        File.WriteAllBytes(Mission(), "old version"u8.ToArray());
        var data = "new version"u8.ToArray();
        var (writer, panel) = New(_ => Ok(data));
        var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
        var final = FinalAsync(writer, spec.DeploymentId);

        Assert.Equal(DeployResult.Accepted, writer.Deploy(spec).Status);
        var result = await final;

        Assert.Equal(DeployResult.Done, result.Status);
        Assert.Equal(data, File.ReadAllBytes(Mission()));
        Assert.Equal("old version"u8.ToArray(), File.ReadAllBytes(Path.Combine(result.BackupFolder!, "co10_Escape.Altis.pbo")));
        Assert.StartsWith(Path.Combine(_dataRoot, "backups", "missions"), result.BackupFolder);
        Assert.Equal($"/api/agent/blobs/{Sha(data)}", Assert.Single(panel.Asked));
        Assert.Empty(Directory.GetFiles(Path.Combine(_install, "mpmissions"), "*.tmp"));
    }

    [Fact]
    public async Task A_bad_pull_leaves_the_old_file()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        File.WriteAllBytes(Mission(), "old version"u8.ToArray());
        var data = "new version"u8.ToArray();
        foreach (var respond in new Func<string, HttpResponseMessage>[]
                 {
                     _ => Ok("tampered"u8.ToArray()),                         // hash mismatch
                     _ => Ok(data[..4]),                                       // truncated
                     _ => new HttpResponseMessage(HttpStatusCode.NotFound),     // the panel refused
                 })
        {
            var (writer, _) = New(respond);
            var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
            var final = FinalAsync(writer, spec.DeploymentId);
            writer.Deploy(spec);

            Assert.Equal(DeployResult.Failed, (await final).Status);
            Assert.Equal("old version"u8.ToArray(), File.ReadAllBytes(Mission()));
            Assert.Empty(Directory.GetFiles(Path.Combine(_install, "mpmissions"), "*.tmp"));
        }
    }

    [Fact]
    public void Same_hash_is_a_no_op()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        var data = "same"u8.ToArray();
        File.WriteAllBytes(Mission(), data);
        var (writer, panel) = New(_ => throw new InvalidOperationException("no pull expected"));

        var result = writer.Deploy(new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length));

        Assert.Equal(DeployResult.Done, result.Status);
        Assert.Empty(panel.Asked);
    }

    [Theory]
    [InlineData("..\\evil.pbo")]
    [InlineData("C:\\evil.pbo")]
    [InlineData("evil.sqf")]
    [InlineData("sub\\evil.pbo")]
    public void Bad_names_and_roots_are_refused(string name)
    {
        var (writer, panel) = New(_ => Ok([1]));

        Assert.Equal(DeployResult.Failed, writer.Deploy(new MissionDeploySpec(Guid.NewGuid(), _install, name, new string('a', 64), 1)).Status);
        Assert.Equal(DeployResult.Failed, writer.Deploy(new MissionDeploySpec(Guid.NewGuid(), TempDir.Create(), "ok.Altis.pbo", new string('a', 64), 1)).Status);
        Assert.Empty(panel.Asked);
    }

    [Fact]
    public async Task Locked_file_waits_then_finishes()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        File.WriteAllBytes(Mission(), "running"u8.ToArray());
        var data = "next"u8.ToArray();
        var (writer, _) = New(_ => Ok(data), TimeSpan.FromMilliseconds(200));
        var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Changed += r => { if (r.Status == DeployResult.Waiting) waiting.TrySetResult(); };
        var final = FinalAsync(writer, spec.DeploymentId);
        var running = new FileStream(Mission(), FileMode.Open, FileAccess.Read, FileShare.Read); // what a running Arma mission does (spike S3)

        writer.Deploy(spec);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("running"u8.ToArray(), File.ReadAllBytes(Mission()));
        await running.DisposeAsync(); // the mission ended

        Assert.Equal(DeployResult.Done, (await final).Status);
        Assert.Equal(data, File.ReadAllBytes(Mission()));
    }

    // Final review C-1: v1 runs, v2 waits on the lock, then Restore v1 (already the file on the server) must cancel v2's write.
    [Fact]
    public async Task Restoring_the_version_on_the_server_cancels_the_waiting_write()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        var v1 = "version one"u8.ToArray();
        var v2 = "version two"u8.ToArray();
        File.WriteAllBytes(Mission(), v1);
        var (writer, _) = New(_ => Ok(v2), TimeSpan.FromMilliseconds(100));
        var save = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(v2), v2.Length);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Changed += r => { if (r.DeploymentId == save.DeploymentId && r.Status == DeployResult.Waiting) waiting.TrySetResult(); };
        var final = FinalAsync(writer, save.DeploymentId);
        var running = new FileStream(Mission(), FileMode.Open, FileAccess.Read, FileShare.Read);

        writer.Deploy(save);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var restore = writer.Deploy(new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(v1), v1.Length));
        await running.DisposeAsync(); // the mission ended

        Assert.Equal(DeployResult.Done, restore.Status);
        Assert.Equal(DeployResult.Failed, (await final).Status);
        Assert.Equal(v1, File.ReadAllBytes(Mission()));
    }

    // Final review I-2: an agent that died mid-pull leaves <target>.<id>.tmp; the panel resends the same deployment id.
    [Fact]
    public async Task A_dead_agents_temp_file_doesnt_block_the_resend()
    {
        Directory.CreateDirectory(Path.Combine(_install, "mpmissions"));
        var data = "new version"u8.ToArray();
        var (writer, _) = New(_ => Ok(data));
        var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
        File.WriteAllBytes($"{Mission()}.{spec.DeploymentId:N}.tmp", "half"u8.ToArray());
        var final = FinalAsync(writer, spec.DeploymentId);

        writer.Deploy(spec);

        Assert.Equal(DeployResult.Done, (await final).Status);
        Assert.Equal(data, File.ReadAllBytes(Mission()));
        Assert.Empty(Directory.GetFiles(Path.Combine(_install, "mpmissions"), "*.tmp"));
    }

    [Fact]
    public void Leftover_temp_files_are_removed_at_start()
    {
        var mp = Directory.CreateDirectory(Path.Combine(_install, "mpmissions")).FullName;
        var readOnlyMp = Directory.CreateDirectory(Path.Combine(_readOnly, "mpmissions")).FullName;
        var stale = Path.Combine(mp, $"co10_Escape.Altis.pbo.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(mp, "notes.tmp");
        var readOnlyStale = Path.Combine(readOnlyMp, $"x.Altis.pbo.{Guid.NewGuid():N}.tmp");
        foreach (var p in new[] { stale, unrelated, readOnlyStale }) File.WriteAllBytes(p, [1]);

        New(_ => Ok([1]));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(readOnlyStale)); // never written to, never cleaned
    }

    // Final review M-4 (graded up): only a file held open by the server means Waiting; any other failure is Failed with its reason.
    [Fact]
    public async Task A_write_that_fails_for_another_reason_fails_instead_of_waiting()
    {
        Directory.CreateDirectory(Mission()); // the target name is taken by a folder: no mission is running, so waiting would never end
        var data = "new"u8.ToArray();
        var (writer, _) = New(_ => Ok(data));
        var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        writer.Changed += r => statuses.Enqueue(r.Status);
        var final = FinalAsync(writer, spec.DeploymentId);

        writer.Deploy(spec);

        Assert.Equal(DeployResult.Failed, (await final).Status);
        Assert.DoesNotContain(DeployResult.Waiting, statuses);
    }

    // Final review M-1 (graded up): a pull that times out or breaks oddly must still report, or the row says "Sending" until a reconnect.
    [Theory]
    [InlineData("timeout")]
    [InlineData("unexpected")]
    public async Task A_pull_that_breaks_unexpectedly_reports_failed(string how)
    {
        var data = "new"u8.ToArray();
        var (writer, _) = New(_ => how == "timeout" ? throw new TaskCanceledException("The request timed out.") : throw new InvalidOperationException("Something odd."));
        var spec = new MissionDeploySpec(Guid.NewGuid(), _install, "co10_Escape.Altis.pbo", Sha(data), data.Length);
        var final = FinalAsync(writer, spec.DeploymentId);

        writer.Deploy(spec);

        Assert.Equal(DeployResult.Failed, (await final).Status);
    }

    [Fact]
    public async Task Read_only_install_roots_refuse_every_write()
    {
        var areas = NewAreas();
        var (writer, _) = New(_ => Ok([1]));
        var keys = new KeyService(areas, new ModStore(areas), NullLogger<KeyService>.Instance);
        var jobs = new JobRunner(areas, new SteamCmdInstaller(new HttpClient()), NullLogger<JobRunner>.Instance);

        Assert.Contains("read-only", writer.Deploy(new MissionDeploySpec(Guid.NewGuid(), _readOnly, "ok.Altis.pbo", new string('a', 64), 1)).Error);
        Assert.Contains("read-only", keys.Deploy(_readOnly, []).Error);
        Assert.Contains("read-only", keys.Cleanup(_readOnly, ["x.bikey"]).Error);
        Assert.Contains("read-only", jobs.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow", _readOnly, null, [])).Error);
        Assert.False(Directory.Exists(Path.Combine(_readOnly, "mpmissions")));
        Assert.True(areas.IsReadOnlyInstall(_readOnly));
        Assert.False(areas.IsReadOnlyInstall(_install));
        jobs.Dispose();
        await Task.CompletedTask;
    }
}
