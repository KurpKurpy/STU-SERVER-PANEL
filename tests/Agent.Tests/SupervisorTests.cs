using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Games.Arma3;

namespace Agent.Tests;

// Every test gets its own fake install root and DataRoot; Dispose kills any fake started from that root.
public sealed class SupervisorHarness : IDisposable
{
    public string InstallRoot { get; } = FakeServer.CreateInstallRoot();
    public string DataRoot { get; } = TempDir.Create();
    private readonly List<Supervisor> _supervisors = [];

    public Supervisor Create(int stopTimeoutSeconds = 1)
    {
        var options = new AgentOptions { DataRoot = DataRoot, StopTimeoutSeconds = stopTimeoutSeconds, QueryIntervalSeconds = 1, RconRetrySeconds = 1 };
        var sup = new Supervisor(options, new Areas(options, [InstallRoot]), new Arma3Adapter(), NullLogger<Supervisor>.Instance);
        _supervisors.Add(sup);
        return sup;
    }

    // Writes the profile's files as Apply would (Phase 3: Start only launches what's already applied) and returns its spec.
    public LaunchSpec Spec(string cfg = "hostname = \"Sup Test\";", string slug = "main", int? port = null, string? basicCfg = null,
        string? installPath = null, string executable = "arma3server_x64.exe", string[]? customArgs = null, bool apply = true)
    {
        if (apply)
        {
            var dir = Path.Combine(DataRoot, "configs", slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "server.cfg"), cfg);
            if (basicCfg is not null) File.WriteAllText(Path.Combine(dir, "basic.cfg"), basicCfg);
        }
        return new(Guid.NewGuid(), slug, installPath ?? InstallRoot, executable, port ?? FakeServer.FreePort(), basicCfg is not null, customArgs ?? []);
    }

    public static async Task<ProfileStatus> WaitAsync(Supervisor sup, Guid id, Func<ProfileStatus, bool> until, int seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        ProfileStatus? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            last = sup.Snapshot().FirstOrDefault(s => s.ProfileId == id);
            if (last is not null && until(last)) return last;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Status never matched; last: {last}");
    }

    public void Dispose()
    {
        foreach (var sup in _supervisors) sup.Dispose();
        foreach (var p in Process.GetProcessesByName("arma3server_x64"))
        {
            try
            {
                if (p.MainModule?.FileName.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase) == true) { p.Kill(true); p.WaitForExit(5000); }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        TempDir.Delete(InstallRoot);
        TempDir.Delete(DataRoot);
    }
}

public sealed class SupervisorTests : IDisposable
{
    private readonly SupervisorHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Start_launches_with_generated_arguments_and_reaches_online()
    {
        var sup = _h.Create();
        var spec = _h.Spec();
        var r = await sup.StartAsync(spec);
        Assert.True(r.Ok, r.Error);

        var online = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);
        Assert.Equal("Sup Test", online.Query?.ServerName);
        Assert.Equal(r.Pid, online.Pid);

        var rpt = Directory.GetFiles(Path.Combine(_h.DataRoot, "profiles", "main"), "*.rpt").Single();
        using var reader = new StreamReader(new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var args = reader.ReadToEnd().Split("\r\n").First(l => l.StartsWith("ARGS:"));
        Assert.Contains($"-port={spec.Port}", args);
        Assert.Contains($@"-config={_h.DataRoot}\configs\main\server.cfg", args);
        Assert.Contains($@"-profiles={_h.DataRoot}\profiles\main", args);
        Assert.Contains("-name=main", args);
    }

    [Fact]
    public async Task Start_refuses_an_unapplied_profile()
    {
        var sup = _h.Create();
        var r = await sup.StartAsync(_h.Spec(slug: "never", apply: false));
        Assert.False(r.Ok);
        Assert.Contains("hasn't been applied", r.Error);

        var dir = Path.Combine(_h.DataRoot, "configs", "never");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "server.cfg"), "hostname = \"x\";");
        var missingBasic = await sup.StartAsync(_h.Spec(slug: "never", apply: false) with { UseBasicCfg = true });
        Assert.False(missingBasic.Ok);
        Assert.Contains("basic.cfg is missing", missingBasic.Error);
    }

    [Fact]
    public async Task Start_uses_the_applied_files_without_rewriting_them()
    {
        var sup = _h.Create();
        var spec = _h.Spec(cfg: "hostname = \"Applied\";", basicCfg: "MaxMsgSend = 128;");
        var cfg = Path.Combine(_h.DataRoot, "configs", "main", "server.cfg");
        File.WriteAllText(cfg, "hostname = \"Changed on disk\";");

        Assert.True((await sup.StartAsync(spec)).Ok);

        var online = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);
        Assert.Equal("Changed on disk", online.Query?.ServerName);
        Assert.Equal("hostname = \"Changed on disk\";", File.ReadAllText(cfg));
        var rpt = Directory.GetFiles(Path.Combine(_h.DataRoot, "profiles", "main"), "*.rpt").Single();
        using var reader = new StreamReader(new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Contains($@"-cfg={_h.DataRoot}\configs\main\basic.cfg", reader.ReadToEnd());
    }

    [Fact]
    public async Task Silent_server_stays_starting()
    {
        var sup = _h.Create();
        var spec = _h.Spec(cfg: "hostname = \"Mute\"; fake_noQuery = 1;");
        Assert.True((await sup.StartAsync(spec)).Ok);
        await Task.Delay(3000);
        Assert.Equal(ServerState.Starting, sup.Snapshot().Single(s => s.ProfileId == spec.ProfileId).State);
    }

    [Fact]
    public async Task Stop_ends_the_process_and_reports_offline()
    {
        var sup = _h.Create();
        var spec = _h.Spec();
        var r = await sup.StartAsync(spec);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);
        var offline = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Offline);
        Assert.Null(offline.Error);
        Assert.False(Alive(r.Pid!.Value));
        Assert.Empty(sup.Running);
    }

    [Fact]
    public async Task ForceStop_kills_immediately()
    {
        var sup = _h.Create(stopTimeoutSeconds: 30);
        var spec = _h.Spec();
        var r = await sup.StartAsync(spec);
        var process = Process.GetProcessById(r.Pid!.Value);

        var sw = Stopwatch.StartNew();
        Assert.True((await sup.ForceStopAsync(spec.ProfileId)).Ok);
        Assert.True(process.WaitForExit(1000));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Offline);
    }

    [Fact]
    public async Task Unexpected_exit_reports_error_with_exit_code()
    {
        var sup = _h.Create();
        var spec = _h.Spec(cfg: "hostname = \"Crash\"; fake_exitAfterMs = 300;");
        Assert.True((await sup.StartAsync(spec)).Ok);
        var error = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Error);
        Assert.Equal(3, error.ExitCode);
        Assert.Contains("exit code 3", error.Error);
    }

    [Fact]
    public async Task Restart_starts_a_new_process_after_the_old_one_exits()
    {
        var sup = _h.Create();
        var spec = _h.Spec();
        var first = await sup.StartAsync(spec);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        Assert.True((await sup.RestartAsync(spec)).Ok);
        var again = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online && s.Pid != first.Pid);
        Assert.False(Alive(first.Pid!.Value));
        Assert.True(Alive(again.Pid!.Value));
    }

    [Fact]
    public async Task Reattach_after_agent_restart_keeps_the_server()
    {
        var a = _h.Create();
        var spec = _h.Spec();
        var r = await a.StartAsync(spec);
        await SupervisorHarness.WaitAsync(a, spec.ProfileId, s => s.State == ServerState.Online);
        a.Dispose();
        await Task.Delay(300);
        Assert.True(Alive(r.Pid!.Value));

        var b = _h.Create();
        await b.ReattachAsync();
        var online = await SupervisorHarness.WaitAsync(b, spec.ProfileId, s => s.State == ServerState.Online);
        Assert.Equal(r.Pid, online.Pid);
        Assert.Contains(spec.ProfileId, b.Running.Keys);
    }

    [Fact]
    public async Task Reattach_ignores_a_reused_pid()
    {
        var id = Guid.NewGuid();
        var statePath = Path.Combine(_h.DataRoot, "agent-state.json");
        File.WriteAllText(statePath, JsonSerializer.Serialize(new Dictionary<Guid, StateEntry>
        {
            [id] = new(Environment.ProcessId, DateTime.UtcNow.AddHours(-1), "main", 2302, _h.InstallRoot, "arma3server_x64.exe"),
        }));

        var sup = _h.Create();
        await sup.ReattachAsync();
        var status = sup.Snapshot().Single(s => s.ProfileId == id);
        Assert.Equal(ServerState.Offline, status.State);
        Assert.Equal("Stopped while the agent was not running", status.Error);
        Assert.Empty(sup.Running);
        Assert.DoesNotContain(id.ToString(), File.ReadAllText(statePath));
    }

    [Fact]
    public async Task Start_supervises_the_server_even_when_the_state_file_cannot_be_written()
    {
        Directory.CreateDirectory(Path.Combine(_h.DataRoot, "agent-state.json.tmp")); // writing the temp file now fails
        var sup = _h.Create();
        var spec = _h.Spec();
        var r = await sup.StartAsync(spec);
        Assert.True(r.Ok, r.Error);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);
        Assert.True((await sup.ForceStopAsync(spec.ProfileId)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Offline);
    }

    [Fact]
    public async Task Reattach_survives_an_unreadable_state_file_and_leaves_it_alone()
    {
        var path = Path.Combine(_h.DataRoot, "agent-state.json");
        var id = Guid.NewGuid();
        var saved = JsonSerializer.Serialize(new Dictionary<Guid, StateEntry> { [id] = new(1, DateTime.UtcNow, "main", 2302, _h.InstallRoot, "arma3server_x64.exe") });
        File.WriteAllText(path, saved);
        // Another program holds it open for writing: our read is refused (and so is a replace while it stays open).
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write | FileShare.Delete))
        {
            var sup = _h.Create();
            await sup.ReattachAsync();
            Assert.Empty(sup.Running);
        }
        Assert.Equal(saved, File.ReadAllText(path)); // what couldn't be read is not overwritten with an empty map
    }

    [Fact]
    public async Task Second_start_while_running_is_refused()
    {
        var sup = _h.Create();
        var spec = _h.Spec();
        var first = await sup.StartAsync(spec);
        Assert.True(first.Ok);
        var second = await sup.StartAsync(spec);
        Assert.False(second.Ok);
        Assert.NotNull(second.Error);
        Assert.Single(sup.Running);
    }

    [Fact]
    public async Task Start_while_stopping_is_refused()
    {
        var sup = _h.Create(stopTimeoutSeconds: 5);
        var spec = _h.Spec();
        Assert.True((await sup.StartAsync(spec)).Ok);
        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);
        var r = await sup.StartAsync(spec);
        Assert.False(r.Ok);
        Assert.Contains("stopping", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_returns_at_once_and_force_stop_during_stopping_is_immediate()
    {
        var sup = _h.Create(stopTimeoutSeconds: 5);
        var spec = _h.Spec();
        var r = await sup.StartAsync(spec);
        var process = Process.GetProcessById(r.Pid!.Value);

        var sw = Stopwatch.StartNew();
        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(ServerState.Stopping, sup.Snapshot().Single(s => s.ProfileId == spec.ProfileId).State);

        sw.Restart();
        Assert.True((await sup.ForceStopAsync(spec.ProfileId)).Ok);
        Assert.True(process.WaitForExit(1000));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Start_refuses_port_in_use()
    {
        var sup = _h.Create();
        var port = FakeServer.FreePort();
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var r = await sup.StartAsync(_h.Spec(port: port));
        Assert.False(r.Ok);
        Assert.Contains(port.ToString(), r.Error);
        Assert.Empty(sup.Running);
    }

    [Fact]
    public async Task Start_refuses_install_path_not_in_agent_roots()
    {
        var other = FakeServer.CreateInstallRoot();
        try
        {
            var r = await _h.Create().StartAsync(_h.Spec(installPath: other));
            Assert.False(r.Ok);
            Assert.DoesNotContain(Process.GetProcessesByName("arma3server_x64"), p => p.MainModule?.FileName.StartsWith(other, StringComparison.OrdinalIgnoreCase) == true);
        }
        finally { TempDir.Delete(other); }
    }

    [Fact]
    public async Task Start_refuses_unknown_executable()
    {
        File.Copy(@"C:\Windows\System32\cmd.exe", Path.Combine(_h.InstallRoot, "cmd.exe"));
        var r = await _h.Create().StartAsync(_h.Spec(executable: "cmd.exe"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Running_server_log_lines_are_raised()
    {
        var sup = _h.Create();
        var got = new TaskCompletionSource<LogLine[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.LogLinesRead += (_, lines) => { if (lines.Any(l => l.Text.Contains("Fake line"))) got.TrySetResult(lines); };
        Assert.True((await sup.StartAsync(_h.Spec())).Ok);
        var lines = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(lines, l => Assert.Equal(DateTimeKind.Utc, l.ReadAt.Kind));
    }

    [Fact]
    public async Task Start_does_not_replay_an_older_log()
    {
        var old = Path.Combine(_h.DataRoot, "profiles", "main", "arma3server_x64_2020-01-01_00-00-00.rpt");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "OLD LINE\r\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        var sup = _h.Create();
        var texts = new List<string>();
        var fake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.LogLinesRead += (_, lines) => { lock (texts) texts.AddRange(lines.Select(l => l.Text)); if (lines.Any(l => l.Text.Contains("Fake line"))) fake.TrySetResult(); };
        Assert.True((await sup.StartAsync(_h.Spec())).Ok);
        await fake.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock (texts) Assert.DoesNotContain("OLD LINE", texts);
    }

    [Fact]
    public async Task Start_refuses_invalid_custom_args()
    {
        var r = await _h.Create().StartAsync(_h.Spec(customArgs: [@"-profiles=C:\x"]));
        Assert.False(r.Ok);
        Assert.Contains("-profiles", r.Error);
    }
}
