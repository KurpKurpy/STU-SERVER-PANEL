using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class UiHubTests
{
    private static HubConnection Ui(PanelFactory f, string? cookie) =>
        FakeAgent.Build(f, "/hubs/ui", o => { if (cookie is not null) o.Headers["Cookie"] = cookie; });

    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid ProfileId)> ServerAsync(PanelFactory f)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        return (owner, agent, await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId)));
    }

    private static Task<bool> ClosedWithin(HubConnection c, int seconds)
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Closed += _ => { closed.TrySetResult(true); return Task.CompletedTask; };
        if (c.State == HubConnectionState.Disconnected) closed.TrySetResult(true);
        return closed.Task.WaitAsync(TimeSpan.FromSeconds(seconds)).ContinueWith(t => t.IsCompletedSuccessfully);
    }

    private static async Task<string> MemberId(HttpClient owner, string userName) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/members")).EnumerateArray()
            .First(m => m.GetProperty("userName").GetString() == userName).GetProperty("id").GetString()!;

    [Fact]
    public async Task Hub_requires_sign_in()
    {
        using var f = new PanelFactory();
        await using var anonymous = Ui(f, null);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
    }

    [Fact]
    public async Task Watch_needs_server_view()
    {
        using var f = new PanelFactory(stampSeconds: 60);
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        var cookie = await TestUsers.CookieAsync(f, "Member", "Nomad");
        await TestUsers.AddClaimAsync(f, "Nomad", "perm.deny", "server.view");
        await using var hub = Ui(f, cookie);
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<JsonElement>("Watch", id));
    }

    [Fact]
    public async Task Status_pushes_reach_watchers()
    {
        using var f = new PanelFactory();
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        await using var hub = Ui(f, await TestUsers.CookieAsync(f, "Member", "Nomad"));
        var online = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("status", s => { if (s.GetProperty("state").GetString() == "online") online.TrySetResult(s); });
        await hub.StartAsync();

        var snapshot = await hub.InvokeAsync<JsonElement>("Watch", id);
        Assert.Equal("offline", snapshot.GetProperty("server").GetProperty("state").GetString());

        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Online, 4242, DateTime.UtcNow, new QueryInfo("13th", "Altis", "m", 1, 8), null, null));
        var pushed = await online.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(id, pushed.GetProperty("id").GetGuid());
        Assert.Equal("Altis", pushed.GetProperty("query").GetProperty("map").GetString());

        var unknown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("status", s => { if (s.GetProperty("state").GetString() == "unknown") unknown.TrySetResult(); });
        await agent.DisposeAsync();
        await unknown.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Resource_pushes_reach_watchers()
    {
        using var f = new PanelFactory();
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        await using var hub = Ui(f, await TestUsers.CookieAsync(f, "Member", "Nomad"));
        var got = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<Guid, JsonElement>("resources", (pid, s) => { if (pid == id) got.TrySetResult(s); });
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Watch", id);

        await agent.SendResourcesAsync(new ResourceSample(DateTime.UtcNow, 42, 1, 2, [], [new ProcessSample(id, 10, 100)]));
        Assert.Equal(42, (await got.Task.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("hostCpuPercent").GetDouble());
    }

    [Fact]
    public async Task Console_lines_reach_watchers_with_logs_view()
    {
        using var f = new PanelFactory();
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        await agent.SendLinesAsync(id, new LogLine(DateTime.UtcNow, "earlier line"));

        await using var zeus = Ui(f, await TestUsers.CookieAsync(f, "S3", "Hawk"));
        var got = new TaskCompletionSource<LogLine[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        zeus.On<Guid, LogLine[]>("console", (pid, lines) => { if (pid == id) got.TrySetResult(lines); });
        await zeus.StartAsync();
        var history = await zeus.InvokeAsync<LogLine[]>("WatchConsole", id);
        Assert.Equal("earlier line", Assert.Single(history).Text);

        await agent.SendLinesAsync(id, new LogLine(DateTime.UtcNow, " 3:54:57 Fake line 1"));
        Assert.Equal(" 3:54:57 Fake line 1", (await got.Task.WaitAsync(TimeSpan.FromSeconds(2))).Single().Text);

        await using var member = Ui(f, await TestUsers.CookieAsync(f, "Member", "Nomad"));
        await member.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => member.InvokeAsync<LogLine[]>("WatchConsole", id));
    }

    [Fact]
    public async Task Disabled_user_connection_is_closed()
    {
        using var f = new PanelFactory(stampSeconds: 1);
        var (owner, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        await using var hub = Ui(f, await TestUsers.CookieAsync(f, "Member", "Nomad"));
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Watch", id);
        var closed = ClosedWithin(hub, 3);

        var r = await owner.PatchAsJsonAsync($"/api/members/{await MemberId(owner, "Nomad")}", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(await closed);
    }

    // Production browsers use WebSockets: one authenticated request, then nothing re-checks the cookie.
    private static HubConnection UiWebSocket(PanelFactory f, string cookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hubs/ui"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var client = f.Server.CreateWebSocketClient();
                    client.ConfigureRequest = r => r.Headers["Cookie"] = cookie;
                    return await client.ConnectAsync(ctx.Uri, ct);
                };
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    [Fact]
    public async Task Security_stamp_change_closes_the_connection()
    {
        using var f = new PanelFactory(stampSeconds: 1);
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        await using var hub = UiWebSocket(f, await TestUsers.CookieAsync(f, "Member", "Nomad"));
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Watch", id);
        await Task.Delay(1500); // a re-check passes while the stamp is unchanged
        Assert.Equal(HubConnectionState.Connected, hub.State);
        var closed = ClosedWithin(hub, 3);

        using (var scope = f.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByNameAsync("Nomad"))!)).Succeeded);
        }
        Assert.True(await closed);
    }

    [Fact]
    public async Task Losing_logs_view_closes_the_connection()
    {
        using var f = new PanelFactory(stampSeconds: 1);
        var (_, agent, id) = await ServerAsync(f);
        await using var lease = agent;
        var cookie = await TestUsers.CookieAsync(f, "S3", "Hawk");
        await using var hub = Ui(f, cookie);
        await hub.StartAsync();
        await hub.InvokeAsync<LogLine[]>("WatchConsole", id);
        var closed = ClosedWithin(hub, 3);

        await TestUsers.AddClaimAsync(f, "Hawk", "perm.deny", "logs.view");
        Assert.True(await closed);

        await using var again = Ui(f, cookie);
        await again.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => again.InvokeAsync<LogLine[]>("WatchConsole", id));
        await again.InvokeAsync<JsonElement>("Watch", id); // server.view is still there
    }

    [Fact]
    public async Task Losing_mods_view_closes_a_jobs_watcher()
    {
        using var f = new PanelFactory(stampSeconds: 1);
        var (_, agent, _) = await ServerAsync(f);
        await using var lease = agent;
        var cookie = await TestUsers.CookieAsync(f, "S3", "Hawk");
        await using var hub = Ui(f, cookie);
        await hub.StartAsync();
        await hub.InvokeAsync("WatchJobs");
        var closed = ClosedWithin(hub, 3);

        await TestUsers.AddClaimAsync(f, "Hawk", "perm.deny", "mods.view");

        // Job lines are masked, but they're still mod and server activity the member may no longer see.
        Assert.True(await closed);
    }
}
