using System.Diagnostics;
using Contracts;
using ServerAgent;

namespace Agent.Tests;

// Phase 7a Task 2: the BattlEye cfg at launch, RCon sessions, Stop through #shutdown (plan Rulings 3, 5, 6). The fake server answers
// RCon the way the spike found BattlEye 1.221 does.
public sealed class RconSupervisorTests : IDisposable
{
    private readonly SupervisorHarness _h = new();
    public void Dispose() => _h.Dispose();

    private LaunchSpec Spec(string cfg = "hostname = \"Rcon\";", bool rcon = true)
    {
        var spec = _h.Spec(cfg: cfg);
        return rcon ? spec with { Rcon = new RconSpec(spec.Port + 10, "Pw0123456789abcdefXYZ123") } : spec;
    }

    private string BattlEyeDir(LaunchSpec spec) => Path.Combine(_h.DataRoot, "profiles", spec.Slug, "BattlEye");

    [Fact]
    public async Task Launch_writes_the_BattlEye_cfg_and_removes_stale_active_copies()
    {
        var sup = _h.Create();
        var spec = Spec();
        Directory.CreateDirectory(BattlEyeDir(spec));
        File.WriteAllText(Path.Combine(BattlEyeDir(spec), "BEServer_x64_active_dead.cfg"), "RConPassword old"); // left by a killed server

        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        var active = Assert.Single(Directory.GetFiles(BattlEyeDir(spec), "BEServer_x64_active_*.cfg")); // the fake renamed ours, as BattlEye does
        var text = File.ReadAllText(active);
        Assert.Contains($"RConPort {spec.Rcon!.Port}", text);
        Assert.Contains("RConIP 127.0.0.1", text);
        Assert.Contains("RConPassword Pw0123456789abcdefXYZ123", text);
    }

    [Fact]
    public async Task Without_an_rcon_spec_no_cfg_is_written_and_rcon_is_off()
    {
        var sup = _h.Create();
        var spec = Spec(rcon: false);
        Assert.True((await sup.StartAsync(spec)).Ok);
        var online = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);
        Assert.False(Directory.Exists(BattlEyeDir(spec)));
        Assert.Equal(RconState.Off, online.Rcon);
        Assert.False((await sup.RconAsync(spec.ProfileId, "players")).Ok);
    }

    [Fact]
    public async Task Launch_refuses_an_rcon_port_inside_the_game_ports_or_a_bad_password()
    {
        var sup = _h.Create();
        var spec = _h.Spec();
        var inRange = await sup.StartAsync(spec with { Rcon = new RconSpec(spec.Port + 3, "Pw0123456789abcdefXYZ123") });
        Assert.False(inRange.Ok);
        Assert.Contains("RCon port", inRange.Error);
        var badPassword = await sup.StartAsync(spec with { Rcon = new RconSpec(spec.Port + 10, "has space\r\nRConIP 0.0.0.0") });
        Assert.False(badPassword.Ok);
    }

    [Fact]
    public async Task A_session_opens_once_online_and_answers_commands()
    {
        var sup = _h.Create();
        var spec = Spec();
        Assert.True((await sup.StartAsync(spec)).Ok);
        var connected = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.Rcon == RconState.Connected);
        Assert.Null(connected.RconError);

        var reply = await sup.RconAsync(spec.ProfileId, "players");
        Assert.True(reply.Ok, reply.Error);
        Assert.Contains("Players on server:", reply.Text);
        Assert.False((await sup.RconAsync(Guid.NewGuid(), "players")).Ok);
    }

    [Fact]
    public async Task Server_messages_are_raised_for_the_profile()
    {
        var sup = _h.Create();
        var got = new List<(Guid, string)>();
        sup.RconMessage += (id, text) => { lock (got) got.Add((id, text)); };
        var spec = Spec();
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.Rcon == RconState.Connected);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5)) { lock (got) if (got.Count > 0) break; await Task.Delay(50); }
        lock (got) Assert.Contains(got, m => m.Item1 == spec.ProfileId && m.Item2.Contains("logged in"));
    }

    [Fact]
    public async Task A_refused_password_reads_failed_with_the_reason()
    {
        var sup = _h.Create();
        var spec = Spec(cfg: "hostname = \"Rcon\"; fake_rconPassword = \"somethingElse\";");
        Assert.True((await sup.StartAsync(spec)).Ok);
        var failed = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.Rcon == RconState.Failed);
        Assert.Contains("password", failed.RconError);
    }

    [Fact]
    public async Task Graceful_stop_goes_through_shutdown_and_reads_as_a_requested_stop()
    {
        var sup = _h.Create(stopTimeoutSeconds: 30); // the fake has no window: without #shutdown this would take the full 30 s
        var spec = Spec();
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.Rcon == RconState.Connected);

        var sw = Stopwatch.StartNew();
        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);
        var offline = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State is ServerState.Offline or ServerState.Error, 15);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal((ServerState.Offline, null, 0, RconState.Off), (offline.State, offline.Error, offline.ExitCode, offline.Rcon));
        Assert.True(File.Exists(Path.Combine(BattlEyeDir(spec), "BEServer_x64.cfg"))); // a clean exit renames it back
    }

    [Fact]
    public async Task When_the_server_ignores_shutdown_stop_still_kills_after_the_timeout()
    {
        var sup = _h.Create(stopTimeoutSeconds: 2);
        var spec = Spec(cfg: "hostname = \"Rcon\"; fake_ignoreShutdown = \"1\";");
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.Rcon == RconState.Connected);

        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);
        var offline = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State is ServerState.Offline or ServerState.Error, 20);
        Assert.Equal(ServerState.Offline, offline.State);
    }

    [Fact]
    public async Task An_unrequested_exit_with_code_0_after_coming_online_is_offline_and_says_why()
    {
        var sup = _h.Create();
        var spec = Spec(cfg: "hostname = \"Rcon\"; fake_exitAfterMs = 3000; fake_exitCode = 0;", rcon: false);
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);
        var after = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State is ServerState.Offline or ServerState.Error);
        Assert.Equal((ServerState.Offline, 0, "The server shut itself down (exit code 0)."), (after.State, after.ExitCode, after.Error));
    }

    // Final review I6: a start that fails and quits with code 0 must not read as a plain stop.
    [Fact]
    public async Task A_code_0_exit_before_it_ever_came_online_is_still_an_error()
    {
        var sup = _h.Create();
        var spec = Spec(cfg: "hostname = \"Rcon\"; fake_noQuery = \"1\"; fake_exitAfterMs = 600; fake_exitCode = 0;", rcon: false);
        Assert.True((await sup.StartAsync(spec)).Ok);
        var after = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State is ServerState.Offline or ServerState.Error);
        Assert.Equal((ServerState.Error, 0), (after.State, after.ExitCode));
        Assert.Contains("before it came online", after.Error);
    }

    // Final review I5: BattlEye binds the RCon port, so it's checked like the game ports.
    [Fact]
    public async Task Start_refuses_an_rcon_port_already_in_use()
    {
        var sup = _h.Create();
        var spec = Spec();
        using var squatter = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, spec.Rcon!.Port));
        var r = await sup.StartAsync(spec);
        Assert.False(r.Ok);
        Assert.Contains(spec.Rcon.Port.ToString(), r.Error);
    }

    [Fact]
    public async Task Start_refuses_an_rcon_port_inside_a_running_servers_game_ports_and_the_other_way_round()
    {
        var sup = _h.Create(stopTimeoutSeconds: 5);
        var a = Spec();
        Assert.True((await sup.StartAsync(a)).Ok);
        var b = _h.Spec(slug: "second", port: a.Port - 8);
        var intoA = await sup.StartAsync(b with { Rcon = new RconSpec(a.Port + 2, "Pw0123456789abcdefXYZ123") }); // b's RCon lands in a's game ports
        Assert.False(intoA.Ok);
        Assert.Contains("overlap", intoA.Error);
        var c = _h.Spec(slug: "third", port: a.Port + 10 - 2);
        var overA = await sup.StartAsync(c); // c's game ports cover a's RCon port
        Assert.False(overA.Ok);
        Assert.Contains("overlap", overA.Error);
        await sup.ForceStopAsync(a.ProfileId);
    }

    [Fact]
    public async Task Reattach_reads_the_rcon_settings_from_the_active_cfg()
    {
        var first = _h.Create(stopTimeoutSeconds: 30);
        var spec = Spec();
        Assert.True((await first.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(first, spec.ProfileId, s => s.Rcon == RconState.Connected);
        first.Dispose(); // the agent restarts; the server keeps running

        var second = _h.Create(stopTimeoutSeconds: 30);
        await second.ReattachAsync();
        await SupervisorHarness.WaitAsync(second, spec.ProfileId, s => s.Rcon == RconState.Connected, 20);
        Assert.True((await second.RconAsync(spec.ProfileId, "players")).Ok);
    }
}
