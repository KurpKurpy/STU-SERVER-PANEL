using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ProfileTests
{
    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Profile_crud_round_trip()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        var id = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, basicCfg: "MaxMsgSend = 128;"));

        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/profiles")).EnumerateArray().Single();
        Assert.Equal("main", listed.GetProperty("slug").GetString());
        Assert.Equal("Arma 3", listed.GetProperty("installName").GetString());
        Assert.Equal(4, listed.GetProperty("revision").GetInt32()); // created, server.cfg, basic.cfg, launch args
        Assert.Equal(4, listed.GetProperty("appliedRevision").GetInt32()); // AgentKit applies the head

        var full = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        var settings = full.GetProperty("snapshot").GetProperty("settings");
        Assert.Equal("13th", settings.GetProperty("server").GetProperty("hostname").GetString());
        Assert.Equal(128, settings.GetProperty("basic").GetProperty("MaxMsgSend").GetInt32());
        Assert.Equal("-autoInit", settings.GetProperty("extraArgs").GetString());
        Assert.True(full.GetProperty("secrets").GetProperty("passwordAdmin").GetProperty("set").GetBoolean());

        var patch = await owner.PatchAsJsonAsync($"/api/profiles/{id}", new { name = "Training", port = 2402 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        full = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        Assert.Equal(("Training", 2402), (full.GetProperty("name").GetString(), full.GetProperty("port").GetInt32()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/profiles/{id}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/profiles")).EnumerateArray());
    }

    [Fact]
    public async Task Passwords_are_encrypted_at_rest()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId));

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var row = await db.Profiles.SingleAsync();
        Assert.NotEmpty(row.SecretsProtected);
        Assert.DoesNotContain("hunter2", row.SecretsProtected + row.SettingsJson + row.ExtraServerCfg);
        Assert.All(await db.ProfileRevisions.ToListAsync(), r => Assert.DoesNotContain("hunter2", r.SnapshotJson + r.SecretsProtected));
    }

    [Fact]
    public async Task Profiles_need_profiles_view()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var id = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId));

        var member = await TestUsers.LoginAsync(f, "Member", "Rook");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/profiles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/profiles")).StatusCode);
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"/api/profiles/{id}")).StatusCode);
    }

    [Theory, InlineData("Bad Slug"), InlineData("-lead"), InlineData("")]
    public async Task Invalid_slugs_are_refused(string slug)
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/profiles", AgentKit.Profile(installId, slug: slug))).StatusCode);
    }

    // The unit's Friday and Sunday servers share port 2100 (2026-11-21): profiles may share ports; Start keeps them from running together.
    [Fact]
    public async Task Profiles_on_one_agent_may_share_ports_but_not_slugs()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, port: 2302));

        var sameSlug = await owner.PostAsJsonAsync("/api/profiles", AgentKit.Profile(installId, name: "Second", slug: "main", port: 2402));
        Assert.Equal("conflict_slug", await Code(sameSlug));

        // A near miss is a typing mistake, not a share: still refused.
        var overlap = await owner.PostAsJsonAsync("/api/profiles", AgentKit.Profile(installId, name: "Second", slug: "second", port: 2304));
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("port_conflict", await Code(overlap));

        var samePort = await owner.PostAsJsonAsync("/api/profiles", AgentKit.Profile(installId, name: "Sunday", slug: "sunday", port: 2302));
        Assert.Equal(HttpStatusCode.Created, samePort.StatusCode);
    }

    [Fact]
    public async Task Of_two_profiles_on_the_same_ports_only_one_starts_at_a_time()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var port = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}")).GetProperty("port").GetInt32();
        var sunday = await AgentKit.CreateProfileAsync(w.Owner, AgentKit.Profile(w.InstallId, name: "Sunday", slug: "sunday", port: port));
        await w.Agent.SendStatusAsync(new ProfileStatus(sunday, ServerState.Online, 4343, DateTime.UtcNow, null, null, null));
        var starts = w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Start));

        var refused = await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("port_conflict", body.GetProperty("code").GetString());
        Assert.Contains("Sunday", body.GetProperty("message").GetString());
        Assert.Equal(starts, w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Start)));

        await w.Agent.SendStatusAsync(new ProfileStatus(sunday, ServerState.Offline, null, null, null, null, 0));
        Assert.True((await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null)).IsSuccessStatusCode);
        Assert.Equal(starts + 1, w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Start)));
    }

    [Fact]
    public async Task Install_must_be_reported_by_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, agentId, key) = await AgentKit.PairAsync(f);

        var offline = await owner.PostAsJsonAsync("/api/installs", new { agentId, path = AgentKit.InstallPath, name = "Arma 3" });
        Assert.Equal("unknown_install", await Code(offline));

        await using var agent = await FakeAgent.ConnectAsync(f, key);
        await agent.HelloAsync(installs: [new InstallInfo(AgentKit.InstallPath, AgentKit.InstallArea, true), new InstallInfo(@"D:\Empty", "install-ffffffff", false)]);
        Assert.Equal("unknown_install", await Code(await owner.PostAsJsonAsync("/api/installs", new { agentId, path = @"C:\Windows", name = "x" })));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/installs", new { agentId, path = AgentKit.InstallPath, name = "Arma 3" })).StatusCode);
        // An install root without the server yet registers too, so SteamCMD can install the server into it (Phase 5a Task 11).
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/installs", new { agentId, path = @"D:\Empty", name = "Fresh" })).StatusCode);

        var installs = await owner.GetFromJsonAsync<JsonElement>("/api/installs");
        Assert.Equal(AgentKit.InstallArea, installs[0].GetProperty("area").GetString());
    }

    [Fact]
    public async Task Installs_and_agents_in_use_cannot_be_deleted()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var profileId = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId));

        Assert.Equal("in_use", await Code(await owner.DeleteAsync($"/api/installs/{installId}")));
        Assert.Equal("in_use", await Code(await owner.DeleteAsync($"/api/agents/{agentId}")));
        await owner.DeleteAsync($"/api/profiles/{profileId}");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/installs/{installId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/agents/{agentId}")).StatusCode);
    }

    [Fact]
    public async Task Running_profile_cannot_be_deleted()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, installId) = await AgentKit.InstallAsync(f);
        var id = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId));
        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Online, 4242, DateTime.UtcNow, null, null, null));
        Assert.Equal("running", await Code(await owner.DeleteAsync($"/api/profiles/{id}")));

        await agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        Assert.Equal("agent_offline", await Code(await owner.DeleteAsync($"/api/profiles/{id}")));
    }

    [Fact]
    public async Task Cfg_files_are_listed_and_the_raw_text_endpoint_is_gone()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        agent.OnListFiles = (area, _, _) => area == AgentKit.InstallArea ? [new FileEntry("server.cfg", 15, DateTime.UtcNow)] : [];

        var files = await owner.GetFromJsonAsync<JsonElement>($"/api/agents/{agentId}/cfg-files");
        var file = files.EnumerateArray().Single();
        Assert.Equal((AgentKit.InstallArea, "server.cfg"), (file.GetProperty("area").GetString(), file.GetProperty("name").GetString()));
        // Plan Ruling 10: the endpoint that returned raw server.cfg text (passwords included) is replaced by POST /api/profiles/{id}/import.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/agents/{agentId}/cfg-files/{AgentKit.InstallArea}/server.cfg")).StatusCode);
    }
}
