using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Contracts;
using ServerAgent.Games.Arma3;

namespace Agent.Tests;

public sealed class A2sTests : IDisposable
{
    private readonly string _install = FakeServer.CreateInstallRoot();
    private readonly string _dir = TempDir.Create();
    private Process? _fake;

    public void Dispose()
    {
        try { _fake?.Kill(true); _fake?.WaitForExit(5000); } catch (InvalidOperationException) { }
        _fake?.Dispose();
        TempDir.Delete(_install);
        TempDir.Delete(_dir);
    }

    private int StartFake(string cfg)
    {
        var port = FakeServer.FreePort();
        var cfgPath = Path.Combine(_dir, "server.cfg");
        File.WriteAllText(cfgPath, cfg);
        _fake = Process.Start(new ProcessStartInfo(Path.Combine(_install, "arma3server_x64.exe"),
            [$"-port={port}", $"-config={cfgPath}", $"-profiles={Path.Combine(_dir, "profiles")}", "-name=test"])
            { UseShellExecute = false, CreateNoWindow = true })!;
        return port;
    }

    private static async Task<QueryInfo?> QueryUntilAnswered(int port)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var info = await A2sClient.QueryAsync(new IPEndPoint(IPAddress.Loopback, port + 1), TimeSpan.FromMilliseconds(500), CancellationToken.None);
            if (info is not null) return info;
            await Task.Delay(200);
        }
        return null;
    }

    [Fact]
    public async Task Query_reads_name_map_mission_and_players()
    {
        var info = await QueryUntilAnswered(StartFake("hostname = \"Fake Op\"; maxPlayers = 4;"));
        Assert.NotNull(info);
        Assert.Equal("Fake Op", info.ServerName);
        Assert.Equal("Altis", info.Map);
        Assert.Equal("fake_mission", info.Mission);
        Assert.Equal(0, info.Players);
        Assert.Equal(4, info.MaxPlayers);
    }

    [Fact]
    public async Task Query_answers_a_challenge()
    {
        var info = await QueryUntilAnswered(StartFake("hostname = \"Challenged\"; fake_challenge = 1;"));
        Assert.Equal("Challenged", info?.ServerName);
    }

    [Fact]
    public async Task Query_returns_null_when_nothing_answers()
    {
        var sw = Stopwatch.StartNew();
        Assert.Null(await A2sClient.QueryAsync(new IPEndPoint(IPAddress.Loopback, FakeServer.FreePort() + 1), TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Query_returns_null_when_a_bound_port_stays_silent()
    {
        var port = FakeServer.FreePort();
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
        var sw = Stopwatch.StartNew();
        Assert.Null(await A2sClient.QueryAsync(new IPEndPoint(IPAddress.Loopback, port + 1), TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2));
    }
}
