using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ServerTests
{
    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<JsonElement> Server(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/servers/{id}");

    private static async Task<(HttpClient Owner, Guid AgentId, FakeAgent Agent, Guid ProfileId)> ProfileAsync(PanelFactory f)
    {
        var (owner, agentId, agent, installId) = await AgentKit.InstallAsync(f);
        var profileId = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, customArgs: "-autoInit -maxMem=8192"));
        return (owner, agentId, agent, profileId);
    }

    private static ProfileStatus Online(Guid id, int pid = 4242) =>
        new(id, ServerState.Online, pid, DateTime.UtcNow.AddMinutes(-3), new QueryInfo("13th", "Altis", "co40_Kestrel", 3, 40), null, null);

    [Fact]
    public async Task Servers_list_shows_agent_status()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        Assert.Equal("offline", (await Server(owner, id)).GetProperty("state").GetString());

        await agent.SendStatusAsync(Online(id));
        await agent.SendResourcesAsync(new ResourceSample(DateTime.UtcNow, 12, 100, 200,
            [new DiskSample(@"K:\", 5_000, 10_000)], [new ProcessSample(id, 34.5, 9_000_000)]));

        var s = (await owner.GetFromJsonAsync<JsonElement>("/api/servers")).EnumerateArray().Single();
        Assert.Equal("online", s.GetProperty("state").GetString());
        Assert.Equal("Altis", s.GetProperty("query").GetProperty("map").GetString());
        Assert.Equal("co40_Kestrel", s.GetProperty("query").GetProperty("mission").GetString());
        Assert.Equal(("203.0.113.5", 2302), (s.GetProperty("address").GetString(), s.GetProperty("port").GetInt32()));
        Assert.Equal(34.5, s.GetProperty("process").GetProperty("cpuPercent").GetDouble());
        Assert.Equal(5_000, s.GetProperty("disk").GetProperty("freeBytes").GetInt64());
        Assert.Equal("Arma 3", s.GetProperty("installName").GetString());
        Assert.DoesNotContain("hunter2", await owner.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task Not_responding_reads_as_camel_case()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.NotResponding, 4242, DateTime.UtcNow, null, null, null));
        Assert.Equal("notResponding", (await Server(owner, id)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_is_unknown_while_agent_disconnected()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, id) = await ProfileAsync(f);
        await agent.SendStatusAsync(Online(id));
        Assert.Equal("online", (await Server(owner, id)).GetProperty("state").GetString());

        await agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        var s = await Server(owner, id);
        Assert.Equal("unknown", s.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("query").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("process").ValueKind);
    }

    [Fact]
    public async Task Status_is_unknown_before_any_agent_connects()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var profileId = Guid.NewGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var agent = new Agent { Id = Guid.NewGuid(), Name = "Box", KeyHash = new string('0', 64) };
            var install = new ServerInstall { Id = Guid.NewGuid(), AgentId = agent.Id, Name = "Arma 3", InstallPath = @"K:\Arma 3" };
            db.AddRange(agent, install, new Profile { Id = profileId, ServerInstallId = install.Id, Name = "Main", Slug = "main", Port = 2302 });
            db.ProcessRuns.Add(new ProcessRun { ProfileId = profileId, Pid = 1, StartedAt = DateTime.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
        }
        var s = await Server(owner, profileId);
        Assert.Equal("unknown", s.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastStartedAt").ValueKind);
    }

    [Fact]
    public async Task Status_is_unknown_until_the_agent_says_hello()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, id) = await ProfileAsync(f);
        await agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));

        var key = await RotateKey(owner, agentId);
        await using var again = await FakeAgent.ConnectAsync(f, key);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(agentId));
        Assert.Equal("unknown", (await Server(owner, id)).GetProperty("state").GetString());

        await again.HelloAsync(installs: [new InstallInfo(AgentKit.InstallPath, AgentKit.InstallArea, true)], statuses: [Online(id)]);
        Assert.Equal("online", (await Server(owner, id)).GetProperty("state").GetString());
    }

    private static async Task<string> RotateKey(HttpClient owner, Guid agentId) =>
        (await (await owner.PostAsync($"/api/agents/{agentId}/key", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

    [Fact]
    public async Task Unreadable_passwords_give_a_clear_error()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.FindAsync(id))!.SecretsProtected = "CfDJ8-not-a-protected-payload";
            foreach (var r in await db.ProfileRevisions.Where(r => r.ProfileId == id).ToListAsync()) r.SecretsProtected = "CfDJ8-not-a-protected-payload";
            await db.SaveChangesAsync();
        }
        foreach (var action in new[] { "start", "restart" })
        {
            var r = await owner.PostAsync($"/api/servers/{id}/{action}", null);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal("secrets_unreadable", await Code(r));
        }
        Assert.Empty(agent.Calls.Where(c => c.Method != "ApplyConfig")); // setup applied the profile

        // The profile still opens, so an admin can set the passwords again.
        var full = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        Assert.True(full.GetProperty("secretsUnreadable").GetBoolean());
    }

    [Fact]
    public async Task Members_without_server_view_get_403()
    {
        using var f = new PanelFactory();
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        await TestUsers.AddClaimAsync(f, "Nomad", "perm.deny", "server.view");
        var c = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(c, "Nomad"));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/servers/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Start_sends_the_launch_spec_and_records_the_run()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/start", null)).StatusCode);
        var spec = (LaunchSpec)agent.Calls.Single(c => c.Method == "Start").Args[0]!;
        Assert.Equal((id, "main", AgentKit.InstallPath, "arma3server_x64.exe", 2302), (spec.ProfileId, spec.Slug, spec.InstallPath, spec.Executable, spec.Port));
        Assert.False(spec.UseBasicCfg); // Phase 3: the spec carries no file text; Start launches what Apply wrote
        Assert.Equal(["-autoInit", "-maxMem=8192"], spec.CustomArgs);

        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Starting, 4242, DateTime.UtcNow, null, null, null));
        await agent.SendStatusAsync(Online(id));
        var ownerId = await TestUsers.IdAsync(f, "Crow");
        using (var scope = f.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<AppDb>().ProcessRuns.SingleAsync();
            Assert.Equal((4242, ownerId, (DateTime?)null), (run.Pid, run.StartedById, run.StoppedAt));
        }

        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Offline, null, null, null, null, 0));
        using (var scope = f.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<AppDb>().ProcessRuns.SingleAsync();
            Assert.NotNull(run.StoppedAt);
            Assert.Equal(("stopped", 0), (run.StopReason, run.ExitCode));
        }
        Assert.NotEqual(JsonValueKind.Null, (await Server(owner, id)).GetProperty("lastStartedAt").ValueKind);
    }

    [Fact]
    public async Task Stop_restart_and_force_stop_reach_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        foreach (var action in new[] { "stop", "restart", "force-stop" })
            Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/{action}", null)).StatusCode);
        Assert.Equal(["Stop", "Restart", "ForceStop"], agent.Calls.Select(c => c.Method).Where(m => m != "ApplyConfig")); // setup applied the profile
        Assert.Equal(["-autoInit", "-maxMem=8192"], ((LaunchSpec)agent.Calls.Single(c => c.Method == "Restart").Args[0]!).CustomArgs);
    }

    [Fact]
    public async Task Control_needs_server_control()
    {
        using var f = new PanelFactory();
        var (_, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        foreach (var action in new[] { "start", "stop", "restart", "force-stop" })
            Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsync($"/api/servers/{id}/{action}", null)).StatusCode);
        Assert.Empty(agent.Calls.Where(c => c.Method != "ApplyConfig")); // setup applied the profile
    }

    [Fact]
    public async Task Force_stop_needs_server_forceStop()
    {
        using var f = new PanelFactory();
        var (_, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        await TestUsers.AddClaimAsync(f, "Nomad", "perm.grant", "server.control");
        var c = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(c, "Nomad"));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/servers/{id}/force-stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"/api/servers/{id}/stop", null)).StatusCode);
    }

    [Fact]
    public async Task Start_with_agent_offline_is_409()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, id) = await ProfileAsync(f);
        await agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        var r = await owner.PostAsync($"/api/servers/{id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("agent_offline", await Code(r));
    }

    [Fact]
    public async Task Agent_refusal_is_reported()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnStart = _ => new StartResult(false, "Port 2302 is already in use.", null, null);
        var r = await owner.PostAsync($"/api/servers/{id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("agent_refused", "Port 2302 is already in use."), (body.GetProperty("code").GetString(), body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Control_actions_appear_in_recent_activity()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await owner.PostAsync($"/api/servers/{id}/start", null);
        await owner.PostAsync($"/api/servers/{id}/force-stop", null);
        var member = await TestUsers.LoginAsync(f, "Member", "Nomad");
        var summaries = (await member.GetFromJsonAsync<JsonElement>("/api/activity")).EnumerateArray().Select(a => a.GetProperty("summary").GetString()).ToArray();
        Assert.Contains("Started Main Operations", summaries);
        Assert.Contains("Force-stopped Main Operations", summaries);
    }

    [Fact]
    public async Task Logs_need_logs_view()
    {
        using var f = new PanelFactory();
        var (_, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnListFiles = (_, _, _) => [new FileEntry("arma3server_x64_2026-11-19_03-54-57.rpt", 10, DateTime.UtcNow)];
        var member = await TestUsers.LoginAsync(f, "Member", "Nomad");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/servers/{id}/logs")).StatusCode);
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var files = await zeus.GetFromJsonAsync<JsonElement>($"/api/servers/{id}/logs");
        Assert.Equal("arma3server_x64_2026-11-19_03-54-57.rpt", files[0].GetProperty("name").GetString());
        Assert.Equal(("profiles", "main", "*.rpt"), ((string)agent.Calls.Last().Args[0]!, (string)agent.Calls.Last().Args[1]!, (string)agent.Calls.Last().Args[2]!));
    }

    [Theory, InlineData("..%5Cserver.cfg"), InlineData("server.cfg"), InlineData("a%20b.rpt")]
    public async Task Log_file_name_with_a_path_is_refused(string file)
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/servers/{id}/logs/{file}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/servers/{id}/logs/{file}/download")).StatusCode);
        Assert.DoesNotContain(agent.Calls, c => c.Method == "ReadFile");
    }

    private static Func<string, string, long, int, FileChunk> Serve(byte[] file, Func<long>? totalSize = null) =>
        (_, _, offset, length) => new FileChunk(file.AsSpan((int)Math.Min(offset, file.Length), (int)Math.Min(length, Math.Max(0, file.Length - offset))).ToArray(),
            totalSize?.Invoke() ?? file.Length);

    [Fact]
    public async Task Log_tail_returns_the_last_bytes_as_text()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var text = string.Concat(Enumerable.Range(1, 20000).Select(n => $" 3:54:57 line {n}\r\n"));
        agent.OnReadFile = Serve(Encoding.UTF8.GetBytes(text));
        var r = await owner.GetAsync($"/api/servers/{id}/logs/a.rpt?tailBytes=1000");
        Assert.Equal("text/plain", r.Content.Headers.ContentType?.MediaType);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(body.Length <= 1000);
        Assert.EndsWith("line 20000\r\n", body);
        Assert.StartsWith(" 3:54:57 line ", body); // starts on a whole line
    }

    [Fact]
    public async Task Download_streams_the_whole_file_in_chunks()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var file = new byte[600_000];
        Random.Shared.NextBytes(file);
        agent.OnReadFile = Serve(file);

        var r = await owner.GetAsync($"/api/servers/{id}/logs/a.rpt/download");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(file, await r.Content.ReadAsByteArrayAsync());
        Assert.True(agent.Calls.Count(c => c.Method == "ReadFile") >= 3);
    }

    [Fact]
    public async Task Download_of_a_growing_file_stops_at_its_starting_length()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var file = new byte[700_000];
        Random.Shared.NextBytes(file);
        var calls = 0;
        agent.OnReadFile = Serve(file, () => Interlocked.Increment(ref calls) == 1 ? 600_000 : 700_000);

        var body = await owner.GetByteArrayAsync($"/api/servers/{id}/logs/a.rpt/download");
        Assert.Equal(600_000, body.Length);
        Assert.Equal(file.AsSpan(0, 600_000).ToArray(), body);
    }
}
