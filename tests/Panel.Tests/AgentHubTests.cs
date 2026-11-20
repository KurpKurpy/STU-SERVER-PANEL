using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Hubs;
using ServerAgent;
using ServerAgent.Files;
using ServerAgent.Games.Arma3;
using ServerAgent.Resources;

namespace Panel.Tests;

public class AgentHubTests
{
    private static async Task<JsonElement> AgentJson(HttpClient owner, Guid id) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/agents")).EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Panel_can_call_the_agent_and_get_a_result()
    {
        using var f = new PanelFactory();
        var (_, id, key) = await AgentKit.PairAsync(f);
        await using var agent = await FakeAgent.ConnectAsync(f, key);
        agent.OnListFiles = (_, _, _) => [new FileEntry("a.rpt", 12, DateTime.UtcNow)];
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(id));

        var files = await f.Services.GetRequiredService<AgentCalls>().CallAsync(id, c => c.ListFiles("profiles", "main", "*.rpt"));

        Assert.Equal("a.rpt", Assert.Single(files).Name);
        Assert.Equal(("ListFiles", "profiles"), (agent.Calls.Single().Method, agent.Calls.Single().Args[0]));
    }

    [Fact]
    public async Task Result_near_the_size_limit_round_trips()
    {
        using var f = new PanelFactory();
        var (_, id, key) = await AgentKit.PairAsync(f);
        await using var agent = await FakeAgent.ConnectAsync(f, key);
        var data = new byte[262144];
        Random.Shared.NextBytes(data);
        agent.OnReadFile = (_, _, _, _) => new FileChunk(data, data.Length);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(id));

        var chunk = await f.Services.GetRequiredService<AgentCalls>().CallAsync(id, c => c.ReadFile("profiles", @"main\a.rpt", 0, 262144));

        Assert.Equal(data, chunk.Data);
    }

    [Fact]
    public async Task Agent_with_valid_key_connects_and_hello_shows_it()
    {
        using var f = new PanelFactory();
        var (owner, id, key) = await AgentKit.PairAsync(f);
        Assert.False((await AgentJson(owner, id)).GetProperty("connected").GetBoolean());

        await using var agent = await FakeAgent.ConnectAsync(f, key);
        await agent.HelloAsync(installs: [new InstallInfo(@"K:\Arma 3", "install-12345678", true)]);

        var json = await AgentJson(owner, id);
        Assert.True(json.GetProperty("connected").GetBoolean());
        Assert.Equal("TESTBOX", json.GetProperty("machineName").GetString());
        Assert.Equal("1.2.3", json.GetProperty("version").GetString());
        Assert.Equal(@"K:\Arma 3", json.GetProperty("installs")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Agent_with_wrong_or_missing_key_is_refused()
    {
        using var f = new PanelFactory();
        var (_, id, key) = await AgentKit.PairAsync(f);
        var wrong = id.ToString("N") + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        foreach (var bad in new[] { wrong, "", "garbage" })
        {
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => FakeAgent.ConnectAsync(f, bad));
            Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
        }
        await using var none = FakeAgent.Build(f, "/hubs/agent");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => none.StartAsync())).StatusCode);
    }

    [Fact]
    public async Task Signed_in_member_cannot_use_the_agent_hub()
    {
        using var f = new PanelFactory();
        var cookie = await TestUsers.CookieAsync(f, "Owner", "Crow");
        await using var hub = FakeAgent.Build(f, "/hubs/agent", o => o.Headers["Cookie"] = cookie);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
    }

    [Fact]
    public async Task Rotating_the_key_disconnects_the_agent_and_the_old_key_fails()
    {
        using var f = new PanelFactory();
        var (owner, id, key) = await AgentKit.PairAsync(f);
        await using var agent = await FakeAgent.ConnectAsync(f, key);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(id));

        var r = await owner.PostAsync($"/api/agents/{id}/key", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var newKey = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        await AgentKit.WaitUntilAsync(() => agent.Connection.State == HubConnectionState.Disconnected && !AgentKit.Registry(f).IsConnected(id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => FakeAgent.ConnectAsync(f, key))).StatusCode);
        await using var again = await FakeAgent.ConnectAsync(f, newKey);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(id));
    }

    [Fact]
    public async Task Agents_need_agents_manage()
    {
        using var f = new PanelFactory();
        var admin = await TestUsers.LoginAsync(f, "S4", "Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/agents", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/agents")).StatusCode);

        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var created = await owner.PostAsJsonAsync("/api/agents", new { name = "Dev PC" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        Assert.Matches("^[0-9a-f]{32}\\.[A-Za-z0-9_-]{43}$", key);

        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/agents"))[0];
        Assert.False(listed.TryGetProperty("key", out _));
        Assert.False(listed.TryGetProperty("keyHash", out _));
        Assert.DoesNotContain(key.Split('.')[1], await owner.GetStringAsync("/api/agents"));
    }

    [Fact]
    public async Task Deleting_an_agent_disconnects_it()
    {
        using var f = new PanelFactory();
        var (owner, id, key) = await AgentKit.PairAsync(f);
        await using var agent = await FakeAgent.ConnectAsync(f, key);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(id));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/agents/{id}")).StatusCode);
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => FakeAgent.ConnectAsync(f, key))).StatusCode);
    }

    private sealed class FailFirstHello : IHubFilter
    {
        private int _seen;
        public ValueTask<object?> InvokeMethodAsync(HubInvocationContext ctx, Func<HubInvocationContext, ValueTask<object?>> next) =>
            ctx.HubMethodName == nameof(IAgentHub.Hello) && Interlocked.Increment(ref _seen) == 1 ? throw new HubException("try again") : next(ctx);
    }

    [Fact]
    public async Task Agent_retries_hello_until_the_panel_accepts_it()
    {
        using var f = new PanelFactory(services: s => s.Configure<HubOptions<AgentHub>>(o => o.AddFilter(new FailFirstHello())));
        var (_, id, key) = await AgentKit.PairAsync(f);
        var options = new AgentOptions { PanelUrl = f.Server.BaseAddress.ToString(), Key = key, DataRoot = Path.Combine(f.DataDir, "agent") };
        var areas = new Areas(options, []);
        using var supervisor = new Supervisor(options, areas, new Arma3Adapter(), NullLogger<Supervisor>.Instance);
        var connection = new PanelConnection(options, supervisor, new FileService(areas), new ResourceSampler(areas), areas,
            new ConfigWriter(areas, new Arma3Adapter(), supervisor, NullLogger<ConfigWriter>.Instance), NullLogger<PanelConnection>.Instance, () => f.Server.CreateHandler());

        await connection.StartAsync(CancellationToken.None);
        try { await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).Hello(id) is not null, 10); }
        finally { await connection.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Real_agent_connection_round_trip()
    {
        using var f = new PanelFactory();
        var (_, id, key) = await AgentKit.PairAsync(f);
        var dataRoot = Path.Combine(f.DataDir, "agent");
        var options = new AgentOptions { PanelUrl = f.Server.BaseAddress.ToString(), Key = key, DataRoot = dataRoot };
        var areas = new Areas(options, []);
        File.WriteAllText(Path.Combine(dataRoot, "profiles", "x.rpt"), "hello\r\n");
        using var supervisor = new Supervisor(options, areas, new Arma3Adapter(), NullLogger<Supervisor>.Instance);
        var connection = new PanelConnection(options, supervisor, new FileService(areas), new ResourceSampler(areas), areas,
            new ConfigWriter(areas, new Arma3Adapter(), supervisor, NullLogger<ConfigWriter>.Instance), NullLogger<PanelConnection>.Instance, () => f.Server.CreateHandler());

        await connection.StartAsync(CancellationToken.None);
        try
        {
            await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).Hello(id) is not null, 10);
            Assert.Equal(Environment.MachineName, AgentKit.Registry(f).Hello(id)!.MachineName);
            var files = await f.Services.GetRequiredService<AgentCalls>().CallAsync(id, c => c.ListFiles("profiles", "", "*.rpt"));
            Assert.Equal("x.rpt", Assert.Single(files).Name);
        }
        finally { await connection.StopAsync(CancellationToken.None); }
    }
}
