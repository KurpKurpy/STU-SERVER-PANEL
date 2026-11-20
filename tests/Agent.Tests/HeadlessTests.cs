using System.Diagnostics;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Games.Arma3;

namespace Agent.Tests;

public sealed class HeadlessTests : IDisposable
{
    private readonly SupervisorHarness _h = new();
    public void Dispose() => _h.Dispose();

    private LaunchSpec Spec(int count = 2, bool withServer = false, string connect = "127.0.0.1") =>
        _h.Spec() with { Headless = new HeadlessSpec(count, connect, "join-pw", withServer) };

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; }
    }

    // The fake HC writes its arguments to the first line of its RPT, under profiles\main\hc<n>.
    private string[] HcRpts() =>
        Directory.Exists(Path.Combine(_h.DataRoot, "profiles", "main"))
            ? Directory.GetDirectories(Path.Combine(_h.DataRoot, "profiles", "main"), "hc*").SelectMany(d => Directory.GetFiles(d, "*.rpt")).ToArray()
            : [];

    private static string FirstLine(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadLine() ?? "";
    }

    [Fact]
    public async Task Headless_clients_start_beside_a_running_server()
    {
        var sup = _h.Create();
        var spec = Spec(count: 2);
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        var r = await sup.StartHeadlessAsync(spec);

        Assert.True(r.Ok, r.Error);
        var status = await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.HeadlessRunning == 2);
        await AgentKitWait(() => HcRpts().Length == 2);
        var args = HcRpts().Select(FirstLine).OrderBy(a => a).ToArray();
        Assert.All(args, a => Assert.Contains("-client", a));
        Assert.Contains("-name=hc1", args[0]);
        Assert.Contains("-name=hc2", args[1]);
        Assert.All(args, a => Assert.Contains($"-port={spec.Port}", a));
        Assert.All(args, a => Assert.Contains("-connect=127.0.0.1", a));
        Assert.All(args, a => Assert.Contains("-password=join-pw", a));
        Assert.Equal(ServerState.Online, status.State);
    }

    [Fact]
    public async Task Headless_needs_a_running_server()
    {
        var r = await _h.Create().StartHeadlessAsync(Spec());
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task With_server_starts_them_when_online()
    {
        var sup = _h.Create();
        var spec = Spec(count: 1, withServer: true);
        Assert.True((await sup.StartAsync(spec)).Ok);

        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online && s.HeadlessRunning == 1);
    }

    [Fact]
    public async Task Stop_closes_headless_clients_before_the_server()
    {
        var sup = _h.Create();
        var spec = Spec(count: 1, withServer: true);
        var start = await sup.StartAsync(spec);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.HeadlessRunning == 1);
        var server = sup.Running[spec.ProfileId]; // the supervisor's own objects: only those can report ExitTime
        var hc = sup.HeadlessProcesses(spec.ProfileId).Single();
        Assert.Equal(start.Pid, server.Id);

        Assert.True((await sup.StopAsync(spec.ProfileId)).Ok);

        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Offline);
        Assert.True(hc.HasExited && server.HasExited);
        Assert.True(hc.ExitTime <= server.ExitTime, $"HC exited {hc.ExitTime:O}, server {server.ExitTime:O}");
    }

    [Fact]
    public async Task Headless_rpt_does_not_replace_the_server_console()
    {
        var sup = _h.Create();
        var texts = new List<string>();
        sup.LogLinesRead += (_, lines) => { lock (texts) texts.AddRange(lines.Select(l => l.Text)); };
        var spec = Spec(count: 1, withServer: true);
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.HeadlessRunning == 1);
        await AgentKitWait(() => HcRpts().Length == 1);
        await Task.Delay(1500);

        lock (texts)
        {
            Assert.Contains(texts, t => t.Contains("Fake line"));
            Assert.DoesNotContain(texts, t => t.Contains("Fake HC"));
        }
    }

    [Fact]
    public async Task Reattach_keeps_headless_clients()
    {
        var a = _h.Create();
        var spec = Spec(count: 1, withServer: true);
        var start = await a.StartAsync(spec);
        await SupervisorHarness.WaitAsync(a, spec.ProfileId, s => s.HeadlessRunning == 1);
        var hcPid = a.HeadlessProcesses(spec.ProfileId).Single().Id;
        a.Dispose();

        var b = _h.Create();
        await b.ReattachAsync();

        var status = await SupervisorHarness.WaitAsync(b, spec.ProfileId, s => s.HeadlessRunning == 1);
        Assert.Equal(start.Pid, status.Pid);
        Assert.Equal(hcPid, b.HeadlessProcesses(spec.ProfileId).Single().Id);
        Assert.True(Alive(hcPid));
    }

    [Fact]
    public async Task Headless_password_with_a_quote_is_refused()
    {
        // Review I6: a quote in -password= could split into extra arguments if Arma doesn't honour \" escaping.
        var sup = _h.Create();
        var spec = _h.Spec() with { Headless = new HeadlessSpec(1, "127.0.0.1", "a\" -mod=evil", false) };
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        var r = await sup.StartHeadlessAsync(spec);

        Assert.False(r.Ok);
        Assert.Contains("password", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Orphaned_headless_clients_are_stopped_when_their_server_is_gone()
    {
        // Review I7: the server died while the agent was down; its headless clients must not run on unsupervised.
        var a = _h.Create();
        var spec = Spec(count: 1, withServer: true);
        var start = await a.StartAsync(spec);
        await SupervisorHarness.WaitAsync(a, spec.ProfileId, s => s.HeadlessRunning == 1);
        var hc = Process.GetProcessById(a.HeadlessProcesses(spec.ProfileId).Single().Id);
        a.Dispose();
        Process.GetProcessById(start.Pid!.Value).Kill();

        await _h.Create().ReattachAsync();

        Assert.True(hc.WaitForExit(10_000), "the orphaned headless client is still running");
    }

    [Fact]
    public async Task Connect_must_be_ipv4()
    {
        var sup = _h.Create();
        var spec = Spec(connect: "evil -mod=x");
        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        var r = await sup.StartHeadlessAsync(spec);

        Assert.False(r.Ok);
        Assert.Contains("IPv4", r.Error);
    }

    private static async Task AgentKitWait(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}
