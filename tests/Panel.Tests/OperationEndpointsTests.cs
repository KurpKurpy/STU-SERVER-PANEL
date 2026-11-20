using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 6a Task 2: creating, editing and listing operations (Rulings 2, 5–7, 13).
public class OperationEndpointsTests
{
    internal static async Task<Guid> PersonAsync(HttpClient c, string name) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/api/operations/people"))!.Single(p => p.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    internal static object Body(ServerMissionsTests.World w, string name = "Op Kestrel", string startsAt = "2027-03-06T08:00:00Z", Guid[]? zeus = null,
        Guid? version = null, string mission = "ace_op.Altis", string notes = "Zeus: spawn the QRF at grid 0412", string briefing = "Insert by Pelican.\nHold the ridge.") =>
        new
        {
            name, description = "A raid on the coast", startsAt, missionId = w.Missions[mission], missionVersionId = version, profileId = w.ProfileId,
            modListId = w.FullList, missionMakerId = (Guid?)null, zeusIds = zeus ?? [], notes, briefing,
        };

    internal static async Task<Guid> CreateAsync(HttpClient c, object body)
    {
        var r = await c.PostAsJsonAsync("/api/operations", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> GetAsync(HttpClient c, Guid id) => c.GetFromJsonAsync<JsonElement>($"/api/operations/{id}");

    [Fact]
    public async Task Create_edit_and_list_round_trip()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await TestUsers.CreateAsync(f, "S3", "Hawk");
        var hawk = await PersonAsync(w.Owner, "Hawk");

        var id = await CreateAsync(w.Owner, Body(w, zeus: [hawk], startsAt: DateTime.UtcNow.AddDays(3).ToString("O")));
        var op = await GetAsync(w.Owner, id);

        Assert.Equal(("Op Kestrel", "Altis", 1, "Full"), (op.GetProperty("name").GetString(), op.GetProperty("mission").GetProperty("map").GetString(),
            op.GetProperty("versionNumber").GetInt32(), op.GetProperty("modList").GetProperty("name").GetString()));
        Assert.Equal("Hawk", Assert.Single(op.GetProperty("zeus").EnumerateArray()).GetProperty("name").GetString());
        Assert.Contains((await w.Owner.GetFromJsonAsync<JsonElement[]>("/api/operations"))!, o => o.GetProperty("id").GetGuid() == id);

        var edit = await w.Owner.PutAsJsonAsync($"/api/operations/{id}", new { version = op.GetProperty("version").GetInt32(), name = "Op Kestrel II",
            description = "", startsAt = op.GetProperty("startsAt").GetString(), missionId = w.Missions["cba_op.VR"], missionVersionId = (Guid?)null,
            profileId = w.ProfileId, modListId = w.CbaOnly, missionMakerId = hawk, zeusIds = Array.Empty<Guid>(), notes = "", briefing = "" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await GetAsync(w.Owner, id);
        Assert.Equal(("Op Kestrel II", "VR", "Hawk", 2), (edited.GetProperty("name").GetString(), edited.GetProperty("mission").GetProperty("map").GetString(),
            edited.GetProperty("missionMaker").GetProperty("name").GetString(), edited.GetProperty("version").GetInt32()));
        Assert.Empty(edited.GetProperty("zeus").EnumerateArray());
    }

    [Fact]
    public async Task An_offset_time_is_stored_at_the_right_UTC_instant_and_listed_in_its_month()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        var id = await CreateAsync(w.Owner, Body(w, startsAt: "2027-04-04T02:30:00+11:00")); // Sydney, the night summer time ends

        Assert.Equal("2027-04-03T15:30:00Z", (await GetAsync(w.Owner, id)).GetProperty("startsAt").GetString());
        var april3 = await w.Owner.GetFromJsonAsync<JsonElement[]>("/api/operations?from=2027-04-03T00:00:00Z&to=2027-04-04T00:00:00Z");
        Assert.Contains(april3!, o => o.GetProperty("id").GetGuid() == id);
        var april4 = await w.Owner.GetFromJsonAsync<JsonElement[]>("/api/operations?from=2027-04-04T00:00:00Z&to=2027-04-05T00:00:00Z");
        Assert.DoesNotContain(april4!, o => o.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task A_stale_edit_is_409()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await CreateAsync(w.Owner, Body(w));
        object Edit(string name) => new { version = 1, name, description = "", startsAt = "2027-03-06T08:00:00Z", missionId = w.Missions["ace_op.Altis"],
            missionVersionId = (Guid?)null, profileId = w.ProfileId, modListId = w.FullList, missionMakerId = (Guid?)null, zeusIds = Array.Empty<Guid>(), notes = "", briefing = "" };

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/operations/{id}", Edit("First"))).StatusCode);
        var second = await w.Owner.PutAsJsonAsync($"/api/operations/{id}", Edit("Second"));

        Assert.Equal((HttpStatusCode.Conflict, "stale"), (second.StatusCode, await MissionKit.CodeAsync(second)));
        Assert.Equal("First", (await GetAsync(w.Owner, id)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Members_see_the_briefing_not_the_notes()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await CreateAsync(w.Owner, Body(w));
        var member = await TestUsers.LoginAsync(f, "Member");
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        var asMember = await GetAsync(member, id);
        Assert.Equal("Insert by Pelican.\nHold the ridge.", asMember.GetProperty("briefing").GetString());
        Assert.True(!asMember.TryGetProperty("notes", out var n) || n.ValueKind == JsonValueKind.Null);
        Assert.Equal("Zeus: spawn the QRF at grid 0412", (await GetAsync(zeus, id)).GetProperty("notes").GetString());
    }

    [Fact]
    public async Task The_mission_version_defaults_to_the_current_and_must_belong_to_the_mission()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        Guid otherVersion;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            otherVersion = (await db.Missions.FindAsync(w.Missions["cba_op.VR"]))!.CurrentVersionId!.Value;
        }

        var id = await CreateAsync(w.Owner, Body(w));
        Assert.Equal(1, (await GetAsync(w.Owner, id)).GetProperty("versionNumber").GetInt32());
        var wrong = await w.Owner.PostAsJsonAsync("/api/operations", Body(w, version: otherVersion));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task Editing_clears_the_stored_readiness()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await CreateAsync(w.Owner, Body(w));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var op = await db.Operations.FindAsync(id);
            (op!.LastCheckAt, op.LastCheckJson, op.ReadyCount, op.CheckCount) = (DateTime.UtcNow, "[]", 9, 11);
            await db.SaveChangesAsync();
        }
        Assert.Equal(9, (await GetAsync(w.Owner, id)).GetProperty("readiness").GetProperty("ready").GetInt32());

        await w.Owner.PutAsJsonAsync($"/api/operations/{id}", new { version = 1, name = "Renamed", description = "", startsAt = "2027-03-06T08:00:00Z",
            missionId = w.Missions["ace_op.Altis"], missionVersionId = (Guid?)null, profileId = w.ProfileId, modListId = w.FullList, missionMakerId = (Guid?)null,
            zeusIds = Array.Empty<Guid>(), notes = "", briefing = "" });

        Assert.Equal(JsonValueKind.Null, (await GetAsync(w.Owner, id)).GetProperty("readiness").ValueKind);
    }

    [Fact]
    public async Task Changes_are_audited_without_notes_or_briefing()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await CreateAsync(w.Owner, Body(w));
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/operations/{id}/status", new { status = "Cancelled" })).StatusCode);

        using var scope = f.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Where(a => a.Action.StartsWith("operation.")).ToListAsync();
        Assert.Equal(["operation.created", "operation.status"], rows.Select(a => a.Action).Order());
        Assert.All(rows, a => Assert.DoesNotContain("QRF", a.Summary + a.DetailsJson));
        Assert.All(rows, a => Assert.DoesNotContain("Pelican", a.Summary + a.DetailsJson));
    }

    // The form's choices come from one route a mission maker can read, though they can't list profiles elsewhere (no profiles.view).
    [Fact]
    public async Task Options_list_what_the_form_needs()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var maker = await TestUsers.LoginAsync(f, "S3", "Quill");
        var member = await TestUsers.LoginAsync(f, "Member");

        var o = await maker.GetFromJsonAsync<JsonElement>("/api/operations/options");

        var mission = o.GetProperty("missions").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == w.Missions["ace_op.Altis"]);
        Assert.Equal(("Altis", 1), (mission.GetProperty("map").GetString(), Assert.Single(mission.GetProperty("versions").EnumerateArray()).GetProperty("number").GetInt32()));
        Assert.Equal(w.ProfileId, Assert.Single(o.GetProperty("profiles").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(["CBA only", "Full"], o.GetProperty("modLists").EnumerateArray().Select(l => l.GetProperty("name").GetString()).Order());
        Assert.Contains(o.GetProperty("people").EnumerateArray(), p => p.GetProperty("name").GetString() == "Quill");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/operations/options")).StatusCode);
    }

    [Fact]
    public async Task Members_cant_create_or_edit()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await CreateAsync(w.Owner, Body(w));
        var member = await TestUsers.LoginAsync(f, "Member");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/operations", Body(w))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/operations/{id}/status", new { status = "Done" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/operations/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/operations/{id}")).StatusCode);
    }
}
