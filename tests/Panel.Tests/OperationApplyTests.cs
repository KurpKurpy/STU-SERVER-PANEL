using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 6a Task 4: Apply to server, gated by a fresh readiness check and run through Start Mission's shared core (Rulings 11–12).
public class OperationApplyTests
{
    private static async Task TickManualAsync(ReadinessTests.Op o, bool done = true)
    {
        Guid manual;
        using (var scope = o.F.Services.CreateScope())
            manual = (await scope.ServiceProvider.GetRequiredService<AppDb>().ChecklistItems.SingleAsync(i => i.AutoKey == null)).Id;
        Assert.Equal(HttpStatusCode.OK, (await o.W.Owner.PutAsJsonAsync($"/api/operations/{o.Id}/checks/{manual}", new { done })).StatusCode);
    }

    // Sends the version the page shows, as the web does; a test passes another to act on an old page.
    private static async Task<HttpResponseMessage> ApplyAsync(HttpClient c, Guid id, int? players = null, int? version = null)
    {
        version ??= (await c.GetFromJsonAsync<JsonElement>($"/api/operations/{id}")).GetProperty("version").GetInt32();
        return await c.PostAsJsonAsync($"/api/operations/{id}/apply", new { players, version });
    }

    private static string[] Calls(ServerMissionsTests.World w) => w.Agent.Calls.Select(c => c.Method).ToArray();

    private static async Task<JsonElement> HeadSnapshotAsync(ServerMissionsTests.World w) =>
        (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}")).GetProperty("snapshot");

    [Fact]
    public async Task Apply_refuses_until_every_item_passes_or_is_Apply_does_this_and_manual_items_are_ticked()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        var head = (await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{o.W.ProfileId}")).GetProperty("revision").GetInt32();
        var before = Calls(o.W).Length;

        var r = await ApplyAsync(o.W.Owner, o.Id);

        Assert.Equal((HttpStatusCode.Conflict, "not_ready"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Contains("Mission configuration checked", await r.Content.ReadAsStringAsync());
        Assert.Equal(head, (await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{o.W.ProfileId}")).GetProperty("revision").GetInt32());
        Assert.DoesNotContain("ApplyConfig", Calls(o.W)[before..]);
    }

    // Final review I-3: a Zeus on an old page could restart a server the operation no longer uses.
    [Fact]
    public async Task Apply_refuses_an_old_version_and_the_preview_names_the_server_and_version()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        var version = op.GetProperty("version").GetInt32();

        var preview = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}/apply");
        Assert.Equal((op.GetProperty("profile").GetProperty("name").GetString(), op.GetProperty("name").GetString(), version),
            (preview.GetProperty("profileName").GetString(), preview.GetProperty("operationName").GetString(), preview.GetProperty("version").GetInt32()));

        await ReadinessTests.DbAsync(f, async db => (await db.Operations.FindAsync(o.Id))!.Version++);
        var before = Calls(o.W).Length;
        var r = await ApplyAsync(o.W.Owner, o.Id, version: version);

        Assert.Equal((HttpStatusCode.Conflict, "stale"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Empty(Calls(o.W)[before..]);
    }

    [Fact]
    public async Task Apply_on_a_stopped_server_sets_the_mission_and_list_applies_and_starts()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        var before = Calls(o.W).Length;

        var r = await ApplyAsync(o.W.Owner, o.Id);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(["ApplyConfig", "DeployKeys", "Start"], Calls(o.W)[before..]);
        var snapshot = await HeadSnapshotAsync(o.W);
        Assert.Equal("ace_op.Altis", Assert.Single(snapshot.GetProperty("rotation").EnumerateArray()).GetProperty("template").GetString());
        Assert.Equal(o.W.FullList, snapshot.GetProperty("modCollectionId").GetGuid());
        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        Assert.Equal((11, 11), (op.GetProperty("readiness").GetProperty("ready").GetInt32(), op.GetProperty("readiness").GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task Apply_on_a_running_server_stops_applies_and_starts_with_a_fresh_player_count()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        await o.W.Agent.SendStatusAsync(new ProfileStatus(o.W.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, new QueryInfo("13th", "Altis", "x", 3, 40), null, null));

        var preview = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}/apply");
        Assert.Equal(("restart", 3), (preview.GetProperty("action").GetString(), preview.GetProperty("players").GetInt32()));
        var stale = await ApplyAsync(o.W.Owner, o.Id, players: 2);
        Assert.Equal((HttpStatusCode.Conflict, "stale_players"), (stale.StatusCode, await MissionKit.CodeAsync(stale)));

        var before = Calls(o.W).Length;
        Assert.Equal(HttpStatusCode.Accepted, (await ApplyAsync(o.W.Owner, o.Id, players: 3)).StatusCode);
        Assert.Equal(["Stop", "ApplyConfig", "DeployKeys", "Start"], Calls(o.W)[before..]);
    }

    [Fact]
    public async Task Apply_and_Start_Mission_share_the_per_server_lock()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        var release = new ManualResetEventSlim();
        var defaultApply = o.W.Agent.OnApplyConfig;
        o.W.Agent.OnApplyConfig = b => { release.Wait(TimeSpan.FromSeconds(10)); return defaultApply(b); };

        var startMission = o.W.Owner.PostAsJsonAsync($"/api/servers/{o.W.ProfileId}/start-mission",
            new { missionId = o.W.Missions["ace_op.Altis"], modListId = o.W.FullList, players = (int?)null });
        await AgentKit.WaitUntilAsync(() => o.W.Agent.Calls.Any(c => c.Method == "ApplyConfig"));

        var apply = await ApplyAsync(o.W.Owner, o.Id);
        release.Set();

        Assert.Equal((HttpStatusCode.Conflict, "busy"), (apply.StatusCode, await MissionKit.CodeAsync(apply)));
        Assert.Equal(HttpStatusCode.Accepted, (await startMission).StatusCode);
    }

    [Fact]
    public async Task Apply_records_who_and_when_bumps_the_version_and_is_audited()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        var version = (await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}")).GetProperty("version").GetInt32();

        Assert.Equal(HttpStatusCode.Accepted, (await ApplyAsync(o.W.Owner, o.Id)).StatusCode);

        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        Assert.NotEqual(JsonValueKind.Null, op.GetProperty("appliedAt").ValueKind);
        Assert.Equal(version + 1, op.GetProperty("version").GetInt32());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.True(await db.Audit.AnyAsync(a => a.Action == "operation.applied" && a.Summary.Contains("Op Kestrel")));
        Assert.Contains("Operation Op Kestrel", (await db.ProfileRevisions.OrderByDescending(r => r.Number).FirstAsync(r => r.ProfileId == o.W.ProfileId)).Note);
    }

    [Fact]
    public async Task Apply_needs_operations_apply()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await TickManualAsync(o);
        var maker = await TestUsers.LoginAsync(f, "S7", "Quill"); // plans operations, can't apply them

        Assert.Equal(HttpStatusCode.Forbidden, (await ApplyAsync(maker, o.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.GetAsync($"/api/operations/{o.Id}/apply")).StatusCode);
    }
}
