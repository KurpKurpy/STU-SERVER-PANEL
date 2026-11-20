using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ApplyTests
{
    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid Id)> ProfileAsync(PanelFactory f, bool applied = false,
        string serverCfg = "hostname = \"13th\"; passwordAdmin = \"hunter2-long\";", string customArgs = "-autoInit")
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        return (owner, agent, await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, serverCfg: serverCfg, customArgs: customArgs), applied));
    }

    private static string Lines(JsonElement body, string name) =>
        string.Join("\n", body.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name)
            .GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("kind").GetString() + l.GetProperty("text").GetString()));

    [Fact]
    public async Task Apply_writes_through_the_agent_and_records_the_revision()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var head = await AgentKit.RevisionAsync(owner, id);

        var r = await AgentKit.ApplyAsync(owner, id);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var bundle = (ConfigBundle)agent.Calls.Single(c => c.Method == "ApplyConfig").Args[0]!;
        Assert.Equal((id, "main"), (bundle.ProfileId, bundle.Slug));
        Assert.Contains("passwordAdmin = \"hunter2-long\";", bundle.ServerCfg); // the agent gets the real file
        Assert.StartsWith("class DifficultyPresets", bundle.DifficultyPresets);
        Assert.Equal(head, (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}")).GetProperty("appliedRevision").GetInt32());
        using var scope = f.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.SingleAsync(a => a.Action == "profile.applied");
        Assert.DoesNotContain("hunter2", audit.DetailsJson + audit.Summary);
    }

    [Fact]
    public async Task Apply_with_an_outdated_revision_is_stale()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var r = await owner.PostAsJsonAsync($"/api/profiles/{id}/apply", new { revision = 1 });
        Assert.Equal("stale", await Code(r));
        Assert.DoesNotContain(agent.Calls, c => c.Method == "ApplyConfig");
    }

    [Fact]
    public async Task Apply_is_refused_while_running()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Online, 4242, DateTime.UtcNow, null, null, null));

        Assert.Equal("running", await Code(await AgentKit.ApplyAsync(owner, id)));
        var preview = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/apply-preview");
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Contains("Stop the server", preview.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Apply_with_mismatched_hashes_is_not_recorded()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnApplyConfig = b => new ApplyResult(true, null, null, [new WrittenFile("server.cfg", FakeAgent.Sha("something else"), 3)]);

        var r = await AgentKit.ApplyAsync(owner, id);

        Assert.Equal("apply_failed", await Code(r));
        Assert.Equal(JsonValueKind.Null, (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}")).GetProperty("appliedRevision").ValueKind);
        agent.OnApplyConfig = _ => new ApplyResult(false, "Couldn't write basic.cfg: in use", "profiles\\main\\x", []);
        var failed = await AgentKit.ApplyAsync(owner, id);
        Assert.Equal("apply_failed", await Code(failed));
    }

    [Fact]
    public async Task Apply_preview_masks_passwords_on_both_sides()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnReadConfig = (_, _) => new ConfigSnapshot("hostname = \"13th\";\r\npasswordAdmin = \"hunter2-old\";\r\n", null, null);

        var body = await owner.GetStringAsync($"/api/profiles/{id}/apply-preview");

        Assert.DoesNotContain("hunter2", body);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.GetProperty("canApply").GetBoolean());
        Assert.Contains("-passwordAdmin = \"********\";", Lines(root, "server.cfg"));
        Assert.Contains("+passwordAdmin = \"******** (changed)\";", Lines(root, "server.cfg"));
    }

    [Fact]
    public async Task Apply_preview_shows_only_the_difficulty_class_changing()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnReadConfig = (_, _) => new ConfigSnapshot(null, null,
            "version=1;\r\nclass DifficultyPresets\r\n{\r\n\tclass Old {};\r\n};\r\nblood=1;\r\nviewDistance=1000;\r\n");

        var root = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/apply-preview");

        var profile = Lines(root, "main.Arma3Profile");
        Assert.Contains("-\tclass Old {};", profile);
        Assert.DoesNotContain("-version=1;", profile);
        Assert.DoesNotContain("-blood=1;", profile);
        Assert.DoesNotContain("+blood=1;", profile);
    }

    [Fact]
    public async Task Start_needs_an_applied_revision()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await owner.PostAsync($"/api/servers/{id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("not_applied", await Code(r));
        Assert.DoesNotContain(agent.Calls, c => c.Method == "Start");
    }

    [Fact]
    public async Task Start_launches_the_applied_revision_not_the_saved_one()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f, customArgs: "-maxMem=4096");
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(owner, id)).StatusCode);
        await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["extraArgs"] = "-maxMem=8192");

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/start", null)).StatusCode);

        var spec = (LaunchSpec)agent.Calls.Single(c => c.Method == "Start").Args[0]!;
        Assert.Equal(["-maxMem=4096"], spec.CustomArgs);
        var server = await owner.GetFromJsonAsync<JsonElement>($"/api/servers/{id}");
        Assert.True(server.GetProperty("revision").GetInt32() > server.GetProperty("appliedRevision").GetInt32());
    }

    [Fact]
    public async Task Join_info_needs_viewJoinInfo_and_shows_the_applied_password()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f, serverCfg: "hostname = \"13th\"; password = \"join-me\";");
        await using var lease = agent;
        var member = await TestUsers.LoginAsync(f, "Member", "Rook");
        Assert.Equal(JsonValueKind.Null, (await member.GetFromJsonAsync<JsonElement>($"/api/servers/{id}/join")).GetProperty("password").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(owner, id)).StatusCode);
        await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { password = "not-applied-yet" });

        var join = await member.GetFromJsonAsync<JsonElement>($"/api/servers/{id}/join");
        Assert.Equal(("203.0.113.5", 2302, "join-me"), (join.GetProperty("address").GetString(), join.GetProperty("port").GetInt32(), join.GetProperty("password").GetString()));
        await TestUsers.AddClaimAsync(f, "Rook", "perm.deny", "server.viewJoinInfo");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/servers/{id}/join")).StatusCode);
    }

    [Fact]
    public async Task Command_preview_uses_agent_paths()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f, customArgs: "-autoInit -maxMem=8192");
        await using var lease = agent;
        var snapshot = (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}")).GetProperty("snapshot");

        var preview = await (await owner.PostAsJsonAsync($"/api/profiles/{id}/preview", new { snapshot })).Content.ReadFromJsonAsync<JsonElement>();

        var command = preview.GetProperty("command").GetProperty("server").GetString();
        Assert.Equal(@"""K:\Arma 3\arma3server_x64.exe"" -port=2302 ""-config=D:\Server Data\configs\main\server.cfg"" ""-profiles=D:\Server Data\profiles\main"" -name=main -autoInit -maxMem=8192",
            command);
    }

    [Fact]
    public async Task Mission_template_comes_from_the_rpt_lines()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f, applied: true);
        await using var lease = agent;
        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Online, 4242, DateTime.UtcNow, new QueryInfo("13th", "Altis", "Combat Patrol", 0, 6), null, null));
        await agent.SendLinesAsync(id, new LogLine(DateTime.UtcNow, "14:48:22 Starting mission:"), new LogLine(DateTime.UtcNow, "14:48:22  Mission file: MP_CombatPatrol_01"),
            new LogLine(DateTime.UtcNow, "14:48:22  Mission world: Altis"));

        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).MissionTemplate(id) is not null);
        Assert.Equal("MP_CombatPatrol_01.Altis", (await owner.GetFromJsonAsync<JsonElement>($"/api/servers/{id}")).GetProperty("missionTemplate").GetString());
    }

    [Fact]
    public async Task Missions_list_comes_from_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        agent.OnListFiles = (area, dir, pattern) => area == AgentKit.InstallArea && dir == "mpmissions" && pattern == "*.pbo"
            ? [new FileEntry("co40_Kestrel.Altis.pbo", 1234, DateTime.UtcNow)] : [];

        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/missions");

        Assert.True(list.GetProperty("connected").GetBoolean());
        Assert.Equal("co40_Kestrel.Altis.pbo", list.GetProperty("files")[0].GetProperty("name").GetString());
    }
}
