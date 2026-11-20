using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 6a Task 3: the readiness evaluator and Prepare (plan Rulings 8–10). Labels are the seeded ones.
public class ReadinessTests
{
    internal sealed record Op(PanelFactory F, ServerMissionsTests.World W, Guid Id);

    // A world where everything this check can see is ready: Workshop times known and current, Hawk as Zeus.
    internal static async Task<Op> ReadyAsync(PanelFactory f, string mission = "ace_op.Altis", Guid? list = null)
    {
        var w = await ServerMissionsTests.WorldAsync(f);
        await TestUsers.CreateAsync(f, "S3", "Hawk");
        await DbAsync(f, async db =>
        {
            foreach (var m in await db.Mods.ToListAsync())
                (m.WorkshopUpdatedAt, m.LatestWorkshopUpdate, m.WorkshopCheckedAt) = (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow);
        });
        var hawk = await OperationEndpointsTests.PersonAsync(w.Owner, "Hawk");
        var body = OperationEndpointsTests.Body(w, zeus: [hawk], mission: mission);
        var id = await OperationEndpointsTests.CreateAsync(w.Owner, body);
        if (list is { } l) await DbAsync(f, async db => (await db.Operations.FindAsync(id))!.ModCollectionId = l);
        return new Op(f, w, id);
    }

    internal static async Task DbAsync(PanelFactory f, Func<AppDb, Task> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await change(db);
        await db.SaveChangesAsync();
    }

    internal static async Task<Dictionary<string, JsonElement>> PrepareAsync(HttpClient c, Guid id)
    {
        var r = await c.PostAsync($"/api/operations/{id}/prepare", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("results").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!, x => x);
    }

    private static string State(Dictionary<string, JsonElement> r, string label) => r[label].GetProperty("state").GetString()!;
    private static string Detail(Dictionary<string, JsonElement> r, string label) => r[label].GetProperty("detail").GetString() ?? "";

    [Fact]
    public async Task A_ready_world_passes_except_what_Apply_does_and_the_manual_item()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;

        var r = await PrepareAsync(o.W.Owner, o.Id);

        Assert.Equal(11, r.Count);
        foreach (var label in new[] { "Mission uploaded", "Mission matches the mod list", "Mission version on the server", "Server profile selected",
                     "Mod list assigned and installed", "Workshop updates done", "Mod keys on the server", "Server ready", "Zeus assigned" })
            Assert.True(State(r, label) == "pass", $"{label}: {State(r, label)} {Detail(r, label)}");
        Assert.Equal("apply", State(r, "Mission rotation and mod list on the server"));
        Assert.Equal("fail", State(r, "Mission configuration checked"));
    }

    [Fact]
    public async Task An_archived_mission_fails_uploaded_and_another_list_fails_matching_naming_the_addon()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f, list: null);
        await using var lease = o.W.Agent;
        await DbAsync(f, async db =>
        {
            (await db.Missions.FindAsync(o.W.Missions["ace_op.Altis"]))!.IsArchived = true;
            (await db.Operations.FindAsync(o.Id))!.ModCollectionId = o.W.CbaOnly;
        });

        var r = await PrepareAsync(o.W.Owner, o.Id);

        Assert.Equal("fail", State(r, "Mission uploaded"));
        Assert.Equal("fail", State(r, "Mission matches the mod list"));
        Assert.Contains("ace_realisticnames", Detail(r, "Mission matches the mod list"));
    }

    [Fact]
    public async Task A_server_with_another_version_says_which()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await DbAsync(f, async db =>
        {
            var missionId = o.W.Missions["ace_op.Altis"];
            var v2 = new MissionVersion { Id = Guid.NewGuid(), MissionId = missionId, Number = 2, PboSha256 = new string('a', 64) };
            db.MissionVersions.Add(v2);
            db.Deployments.Add(new Deployment { Id = Guid.NewGuid(), MissionId = missionId, MissionVersionId = v2.Id, ServerInstallId = o.W.InstallId,
                FileName = "ace_op.Altis.pbo", Status = DeploymentStatus.Done, DeployedAt = DateTime.UtcNow.AddMinutes(1) });
        });

        var r = await PrepareAsync(o.W.Owner, o.Id);

        Assert.Equal("fail", State(r, "Mission version on the server"));
        Assert.Contains("v2", Detail(r, "Mission version on the server"));
        Assert.Contains("v1", Detail(r, "Mission version on the server"));
    }

    [Fact]
    public async Task Mods_not_installed_or_with_updates_fail_and_not_checked_in_a_day_cant_tell()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await DbAsync(f, async db =>
        {
            (await db.Mods.SingleAsync(m => m.WorkshopId == 111)).Status = ModStatus.NotInstalled;
            (await db.Mods.SingleAsync(m => m.WorkshopId == 222)).LatestWorkshopUpdate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        var r = await PrepareAsync(o.W.Owner, o.Id);
        Assert.Equal("fail", State(r, "Mod list assigned and installed"));
        Assert.Equal("fail", State(r, "Workshop updates done"));

        await DbAsync(f, async db =>
        {
            foreach (var m in await db.Mods.ToListAsync())
                (m.Status, m.LatestWorkshopUpdate, m.WorkshopCheckedAt) = (ModStatus.Installed, m.WorkshopUpdatedAt, DateTime.UtcNow.AddDays(-2));
        });
        r = await PrepareAsync(o.W.Owner, o.Id);
        Assert.Equal("unknown", State(r, "Workshop updates done"));
        Assert.Contains("Check for updates", Detail(r, "Workshop updates done"));
    }

    [Fact]
    public async Task Missing_keys_are_Apply_does_this_and_an_offline_agent_makes_keys_and_server_cant_tell()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await DbAsync(f, async db => (await db.Mods.SingleAsync(m => m.WorkshopId == 111)).KeyFilesJson = "[\"cba_3.bikey\"]");
        o.W.Agent.OnListKeys = _ => [new KeyFile("a3.bikey", 100)];

        var r = await PrepareAsync(o.W.Owner, o.Id);
        Assert.Equal("apply", State(r, "Mod keys on the server"));
        Assert.Contains("cba_3.bikey", Detail(r, "Mod keys on the server"));

        await o.W.Agent.DisposeAsync();
        var agentId = GetAgent(f);
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        r = await PrepareAsync(o.W.Owner, o.Id);
        Assert.Equal("unknown", State(r, "Mod keys on the server"));
        Assert.Equal("unknown", State(r, "Server ready"));
    }

    // Final review I-1: after a panel restart the agent reconnects before its hello lists the running servers.
    [Fact]
    public async Task An_agent_that_hasnt_said_hello_yet_makes_server_ready_cant_tell()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        var agentId = GetAgent(f);
        await o.W.Agent.DisposeAsync();
        await AgentKit.WaitUntilAsync(() => !AgentKit.Registry(f).IsConnected(agentId));
        await using var quiet = await FakeAgent.ConnectAsync(f, o.W.Agent.Key);
        await AgentKit.WaitUntilAsync(() => AgentKit.Registry(f).IsConnected(agentId));

        var r = await PrepareAsync(o.W.Owner, o.Id);

        Assert.Equal("unknown", State(r, "Server ready"));
        Assert.Contains("hasn't reported", Detail(r, "Server ready"));
    }

    private static Guid GetAgent(PanelFactory f)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDb>().Installs.Single().AgentId;
    }

    [Theory]
    [InlineData(ServerState.Error, "fail")]
    [InlineData(ServerState.Starting, "fail")]
    [InlineData(ServerState.Online, "pass")]
    public async Task Server_state_decides_server_ready(ServerState state, string expected)
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await o.W.Agent.SendStatusAsync(new ProfileStatus(o.W.ProfileId, state, 4242, DateTime.UtcNow, null, state == ServerState.Error ? "crashed" : null, null));

        Assert.Equal(expected, State(await PrepareAsync(o.W.Owner, o.Id), "Server ready"));
    }

    [Fact]
    public async Task An_install_job_makes_the_server_not_ready()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await DbAsync(f, db =>
        {
            db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = Panel.Features.Mods.JobKinds.ServerInstall, Status = JobStatus.Running, TargetId = o.W.InstallId.ToString() });
            return Task.CompletedTask;
        });

        Assert.Equal("fail", State(await PrepareAsync(o.W.Owner, o.Id), "Server ready"));
    }

    [Fact]
    public async Task No_or_only_disabled_Zeus_fails()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await DbAsync(f, async db => (await db.Users.SingleAsync(u => u.UserName == "Hawk")).LockoutEnd = DateTimeOffset.MaxValue);

        Assert.Equal("fail", State(await PrepareAsync(o.W.Owner, o.Id), "Zeus assigned"));
    }

    [Fact]
    public async Task Prepare_stores_the_count_and_time()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;

        await PrepareAsync(o.W.Owner, o.Id);

        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        var readiness = op.GetProperty("readiness");
        Assert.Equal((9, 11), (readiness.GetProperty("ready").GetInt32(), readiness.GetProperty("total").GetInt32()));
        Assert.Equal(11, op.GetProperty("lastCheck").GetArrayLength());
        var listed = (await o.W.Owner.GetFromJsonAsync<JsonElement[]>("/api/operations"))!.Single(x => x.GetProperty("id").GetGuid() == o.Id);
        Assert.Equal(9, listed.GetProperty("readiness").GetProperty("ready").GetInt32());
    }

    [Fact]
    public async Task Disabled_items_dont_count_and_an_unknown_key_cant_tell()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await DbAsync(f, async db =>
        {
            (await db.ChecklistItems.SingleAsync(i => i.AutoKey == "zeus.assigned")).Enabled = false;
            db.ChecklistItems.Add(new ChecklistItem { Id = Guid.NewGuid(), AutoKey = "future.thing", Label = "From a newer version", Order = 50, Enabled = true });
        });

        var r = await PrepareAsync(o.W.Owner, o.Id);

        Assert.False(r.ContainsKey("Zeus assigned"));
        Assert.Equal("unknown", State(r, "From a newer version"));
    }

    [Fact]
    public async Task Only_manual_items_can_be_ticked_and_a_tick_counts()
    {
        using var f = new PanelFactory();
        var o = await ReadyAsync(f);
        await using var lease = o.W.Agent;
        await PrepareAsync(o.W.Owner, o.Id);
        Guid manual, auto;
        using (var scope = f.Services.CreateScope())
        {
            var items = await scope.ServiceProvider.GetRequiredService<AppDb>().ChecklistItems.ToListAsync();
            (manual, auto) = (items.Single(i => i.AutoKey == null).Id, items.Single(i => i.AutoKey == "zeus.assigned").Id);
        }
        var zeus = await TestUsers.LoginAsync(f, "S3", "Raven");
        var member = await TestUsers.LoginAsync(f, "Member");

        Assert.Equal(HttpStatusCode.BadRequest, (await zeus.PutAsJsonAsync($"/api/operations/{o.Id}/checks/{auto}", new { done = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/operations/{o.Id}/checks/{manual}", new { done = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await zeus.PutAsJsonAsync($"/api/operations/{o.Id}/checks/{manual}", new { done = true })).StatusCode);

        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        Assert.Equal(10, op.GetProperty("readiness").GetProperty("ready").GetInt32());
        Assert.Equal("pass", State(await PrepareAsync(o.W.Owner, o.Id), "Mission configuration checked"));
    }

    [Fact]
    public async Task Checklist_admin_needs_settings_manage_and_auto_items_cant_be_deleted()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner");
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var items = (await zeus.GetFromJsonAsync<JsonElement[]>("/api/checklist"))!;
        Assert.Equal(11, items.Length);

        object Item(JsonElement i, string? label = null, bool? enabled = null, bool delete = false) => new
        {
            id = i.GetProperty("id").GetGuid(), label = label ?? i.GetProperty("label").GetString(), order = i.GetProperty("order").GetInt32(),
            enabled = enabled ?? i.GetProperty("enabled").GetBoolean(), delete,
        };
        var edited = items.Select(i => i.GetProperty("autoKey").GetString() switch
        {
            "mods.updated" => Item(i, enabled: false),
            null => Item(i, label: "Briefing slides ready"),
            _ => Item(i),
        }).Append(new { id = (Guid?)null, label = "Radios checked", order = 12, enabled = true, delete = false }).ToArray();

        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PutAsJsonAsync("/api/checklist", new { items = edited })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/checklist", new { items = edited })).StatusCode);
        var after = (await owner.GetFromJsonAsync<JsonElement[]>("/api/checklist"))!;
        Assert.Equal(12, after.Length);
        Assert.Contains(after, i => i.GetProperty("label").GetString() == "Briefing slides ready");
        Assert.False(after.Single(i => i.GetProperty("autoKey").GetString() == "mods.updated").GetProperty("enabled").GetBoolean());

        var deleteAuto = after.Select(i => i.GetProperty("autoKey").GetString() == "zeus.assigned" ? Item(i, delete: true) : Item(i)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/checklist", new { items = deleteAuto })).StatusCode);
    }
}
