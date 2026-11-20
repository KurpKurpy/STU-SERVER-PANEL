using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 5b Rulings 11–12: Start Mission (missions.start) sets a one-mission rotation and the mod list, applies, then starts or restarts.
public class StartMissionTests
{
    private static Task<HttpResponseMessage> StartAsync(HttpClient c, ServerMissionsTests.World w, string mission, Guid list, int? players = null) =>
        c.PostAsJsonAsync($"/api/servers/{w.ProfileId}/start-mission", new { missionId = w.Missions[mission], modListId = list, players });

    private static Task<JsonElement> PreviewAsync(HttpClient c, ServerMissionsTests.World w, string mission, Guid list) =>
        c.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/start-mission?missionId={w.Missions[mission]}&modListId={list}");

    private static Task RunningAsync(ServerMissionsTests.World w, int players) =>
        w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, new QueryInfo("13th", "Altis", "ace_op", players, 40), null, null));

    private static async Task<JsonElement> ProfileAsync(HttpClient owner, Guid id) => await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");

    private static string[] AgentCalls(ServerMissionsTests.World w) => w.Agent.Calls.Select(c => c.Method).ToArray();

    [Fact]
    public async Task Start_mission_on_an_offline_server_saves_a_one_mission_rotation_with_the_list_applies_and_starts()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var before = AgentCalls(w).Length;

        var r = await StartAsync(zeus, w, "cba_op.VR", w.CbaOnly);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var profile = await ProfileAsync(w.Owner, w.ProfileId);
        var snapshot = profile.GetProperty("snapshot");
        var rotation = Assert.Single(snapshot.GetProperty("rotation").EnumerateArray());
        Assert.Equal("cba_op.VR", rotation.GetProperty("template").GetString());
        Assert.Equal(w.CbaOnly, snapshot.GetProperty("modCollectionId").GetGuid());
        Assert.Equal(profile.GetProperty("revision").GetInt32(), profile.GetProperty("appliedRevision").GetInt32());
        Assert.Equal(["ApplyConfig", "DeployKeys", "Start"], AgentCalls(w)[before..]);
        var cfg = ((ConfigBundle)w.Agent.Calls.Last(c => c.Method == "ApplyConfig").Args[0]!).ServerCfg;
        Assert.Contains("template = \"cba_op.VR\"", cfg);
        Assert.Single(((LaunchSpec)w.Agent.Calls.Last(c => c.Method == "Start").Args[0]!).Mods); // the CBA-only list
    }

    // Task 7 (live): the agent writes no files while a server runs, so a running server is stopped, then applied and started.
    [Fact]
    public async Task Start_mission_on_a_running_server_stops_it_applies_and_starts()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await RunningAsync(w, 14);
        var before = AgentCalls(w).Length;

        var r = await StartAsync(w.Owner, w, "ace_op.Altis", w.FullList, players: 14);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(["Stop", "ApplyConfig", "DeployKeys", "Start"], AgentCalls(w)[before..]);
        var profile = await ProfileAsync(w.Owner, w.ProfileId);
        Assert.Equal(profile.GetProperty("revision").GetInt32(), profile.GetProperty("appliedRevision").GetInt32());
    }

    [Fact]
    public async Task A_server_that_doesnt_stop_in_time_gets_no_new_files()
    {
        using var f = new PanelFactory(stopWaitSeconds: 1);
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        w.Agent.ExitOnStop = false;
        await RunningAsync(w, 0);
        var applied = (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("appliedRevision").GetInt32();
        var before = AgentCalls(w).Length;

        var r = await StartAsync(w.Owner, w, "ace_op.Altis", w.FullList, players: 0);

        Assert.Equal((HttpStatusCode.Conflict, "stop_timeout"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(["Stop"], AgentCalls(w)[before..]);
        Assert.Equal(applied, (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("appliedRevision").GetInt32());
    }

    [Fact]
    public async Task If_the_files_cant_be_written_after_the_stop_the_old_settings_start_again()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await RunningAsync(w, 0);
        w.Agent.OnApplyConfig = _ => new ApplyResult(false, "Couldn't write server.cfg: in use", null, []);
        var applied = (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("appliedRevision").GetInt32();
        var before = AgentCalls(w).Length;

        var r = await StartAsync(w.Owner, w, "ace_op.Altis", w.FullList, players: 0);

        Assert.Equal((HttpStatusCode.Conflict, "apply_failed"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Contains("started again with its previous settings", await r.Content.ReadAsStringAsync());
        Assert.Equal(["Stop", "ApplyConfig", "DeployKeys", "Start"], AgentCalls(w)[before..]);
        Assert.Equal(applied, (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("appliedRevision").GetInt32());
    }

    [Fact]
    public async Task Start_mission_refuses_a_stale_player_count()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await RunningAsync(w, 14);
        var revision = (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("revision").GetInt32();
        var before = AgentCalls(w).Length;

        var r = await StartAsync(w.Owner, w, "ace_op.Altis", w.FullList, players: 12);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("stale_players", await MissionKit.CodeAsync(r));
        Assert.Equal(revision, (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("revision").GetInt32());
        Assert.Equal(before, AgentCalls(w).Length);
    }

    [Fact]
    public async Task Missing_missions_cant_be_started()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var revision = (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("revision").GetInt32();

        var r = await StartAsync(w.Owner, w, "ace_op.Altis", w.CbaOnly);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("missing", await MissionKit.CodeAsync(r));
        Assert.Equal(revision, (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task A_mission_not_done_on_this_install_cant_be_started()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f, deployDone: file => file != "hc_op.Altis.pbo");
        await using var lease = w.Agent;

        var r = await StartAsync(w.Owner, w, "hc_op.Altis", w.FullList);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("not_on_server", await MissionKit.CodeAsync(r));
    }

    [Fact]
    public async Task Zeus_starts_a_listed_mission_and_nothing_else()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.Forbidden, (await AgentKit.EditConfigAsync(zeus, w.ProfileId, x => x["modCollectionId"] = w.CbaOnly.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AgentKit.ApplyAsync(zeus, w.ProfileId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsync($"/api/servers/{w.ProfileId}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(zeus, w, "cba_op.VR", w.FullList)).StatusCode);
    }

    [Fact]
    public async Task Members_without_missions_start_get_403()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var member = await TestUsers.LoginAsync(f, "Member");

        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(member, w, "cba_op.VR", w.FullList)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync($"/api/servers/{w.ProfileId}/start-mission?missionId={w.Missions["cba_op.VR"]}&modListId={w.FullList}")).StatusCode);
    }

    [Fact]
    public async Task The_preview_names_restart_players_and_unapplied_changes()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await RunningAsync(w, 14);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(w.Owner, w.ProfileId, x => x["extraServerCfg"] = "// saved, not applied")).StatusCode);

        var p = await PreviewAsync(w.Owner, w, "cba_op.VR", w.CbaOnly);

        Assert.Equal(("restart", 14), (p.GetProperty("action").GetString(), p.GetProperty("players").GetInt32()));
        Assert.True(p.GetProperty("modListChanges").GetBoolean());
        Assert.True(p.GetProperty("unappliedChanges").GetBoolean());
        Assert.Equal("Ready", p.GetProperty("compat").GetProperty("status").GetString());
        var offline = await PreviewAsync(w.Owner, w, "cba_op.VR", w.FullList);
        Assert.False(offline.GetProperty("modListChanges").GetBoolean());
    }

    private static async Task<int> HeadAsync(ServerMissionsTests.World w) => (await ProfileAsync(w.Owner, w.ProfileId)).GetProperty("revision").GetInt32();

    private static async Task WithDbAsync(PanelFactory f, Func<AppDb, Task> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await change(db);
        await db.SaveChangesAsync();
    }

    // Final review I-1: the launch's own refusals came after the revision was saved and applied.
    [Fact]
    public async Task Start_mission_refuses_a_list_with_a_mod_that_isnt_installed()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await WithDbAsync(f, async db => (await db.Mods.SingleAsync(m => m.WorkshopId == 333)).Status = ModStatus.NotInstalled); // headless-only in Full
        var head = await HeadAsync(w);
        var before = AgentCalls(w).Length;

        var r = await StartAsync(w.Owner, w, "cba_op.VR", w.FullList);

        Assert.Equal((HttpStatusCode.Conflict, "mods_missing"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(head, await HeadAsync(w));
        Assert.Equal(before, AgentCalls(w).Length);
    }

    [Fact]
    public async Task Start_mission_refuses_while_the_install_updates()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await WithDbAsync(f, db =>
        {
            db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = Panel.Features.Mods.JobKinds.ServerInstall, Status = JobStatus.Running, TargetId = w.InstallId.ToString() });
            return Task.CompletedTask;
        });
        var head = await HeadAsync(w);

        var r = await StartAsync(w.Owner, w, "cba_op.VR", w.FullList);

        Assert.Equal((HttpStatusCode.Conflict, "installing"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(head, await HeadAsync(w));
    }

    // Final review I-3: a hand-written class Missions won over the rotation, so the server restarted into the old missions.
    [Fact]
    public async Task Start_mission_replaces_a_hand_written_missions_class()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(w.Owner, w.ProfileId, x => x["settings"]!["missionsOverride"] =
            "class Missions\n{\n\t// ours\n\tclass Old\n\t{\n\t\ttemplate = \"old_op.Altis\";\n\t\tdifficulty = \"Regular\";\n\t};\n};")).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(w.Owner, w, "cba_op.VR", w.FullList)).StatusCode);

        var cfg = ((ConfigBundle)w.Agent.Calls.Last(c => c.Method == "ApplyConfig").Args[0]!).ServerCfg;
        Assert.Contains("template = \"cba_op.VR\"", cfg);
        Assert.DoesNotContain("old_op.Altis", cfg);
    }

    // Task 7 reverses final review I-4: the server is stopped before the files are written, so Arma's rewrite at exit comes first
    // and a saved difficulty change goes out with the mission instead of being refused.
    [Fact]
    public async Task Difficulty_changes_reach_a_running_server_after_it_stops()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(w.Owner, w.ProfileId, x => x["difficultyOverride"] =
            "class DifficultyPresets\n{\n\tclass CustomDifficulty\n\t{\n\t\tclass Options\n\t\t{\n\t\t\tgroupIndicators = 1;\n\t\t};\n\t};\n};")).StatusCode);
        await RunningAsync(w, 3);

        var r = await StartAsync(w.Owner, w, "cba_op.VR", w.FullList, players: 3);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("groupIndicators", ((ConfigBundle)w.Agent.Calls.Last(c => c.Method == "ApplyConfig").Args[0]!).DifficultyPresets);
    }

    // Final review I-5: a Start Mission during a restart or stop could boot one request's server.cfg with another's mods.
    [Theory]
    [InlineData(ServerState.Starting)]
    [InlineData(ServerState.Stopping)]
    public async Task Start_mission_refuses_while_the_server_is_starting_or_stopping(ServerState state)
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, state, 4242, DateTime.UtcNow, null, null, null));
        var head = await HeadAsync(w);

        var r = await StartAsync(w.Owner, w, "cba_op.VR", w.FullList);

        Assert.Equal((HttpStatusCode.Conflict, "busy"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(head, await HeadAsync(w));
    }

    // Phase 6a final review I-1: before its hello the panel can't tell a running server from a stopped one.
    [Fact]
    public async Task Start_mission_waits_for_the_agents_hello()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        var head = await HeadAsync(w);
        Guid agentId;
        using (var scope = f.Services.CreateScope()) agentId = scope.ServiceProvider.GetRequiredService<AppDb>().Installs.Single().AgentId;
        await w.Agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        await using var quiet = await FakeAgent.ConnectAsync(f, w.Agent.Key);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(agentId));

        var r = await StartAsync(w.Owner, w, "cba_op.VR", w.FullList);

        Assert.Equal((HttpStatusCode.Conflict, "agent_offline"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(head, await HeadAsync(w));
    }

    [Fact]
    public async Task Start_mission_is_audited()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(w.Owner, w, "cba_op.VR", w.FullList)).StatusCode);

        using var scope = f.Services.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.SingleAsync(a => a.Action == "mission.started");
        Assert.Contains("cba_op", entry.Summary);
    }
}
