using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 7b Task 4: the schedules API, the unit time zone, the dashboard's next runs and their audit.
public class ScheduleApiTests
{
    private static object Body(string task = "restart", Guid? targetId = null, int days = 127, int minute = 240, bool enabled = true, string? scope = null,
        string name = "Nightly restart") => new { name, task, targetId, scope, days, minuteOfDay = minute, enabled };

    [Fact]
    public async Task Schedules_validate_their_fields()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        async Task<HttpStatusCode> Post(object body) => (await admin.PostAsJsonAsync("/api/schedules", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(targetId: w.ProfileId, days: 0)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(targetId: w.ProfileId, days: 128)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(targetId: w.ProfileId, minute: 1440)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(task: "reboot-the-world", targetId: w.ProfileId)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(targetId: null)));                          // a restart needs a profile
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(task: "update-server", targetId: w.ProfileId))); // an install, not a profile
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(task: "backup", scope: "server")));        // which agent?
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body(name: "")));
        Assert.Equal(HttpStatusCode.Created, await Post(Body(task: "update-server", targetId: w.InstallId, name: "Server update")));
        Assert.Equal(HttpStatusCode.Created, await Post(Body(task: "backup", scope: "console", name: "Nightly backup")));
    }

    [Fact]
    public async Task Saving_a_schedule_sets_its_next_run_and_disabling_clears_it()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var admin = await TestUsers.LoginAsync(f, "S4");

        var created = await (await admin.PostAsJsonAsync("/api/schedules", Body(targetId: w.ProfileId))).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var next = created.GetProperty("nextRunAt").GetDateTime();
        Assert.InRange(next, DateTime.UtcNow, DateTime.UtcNow.AddDays(1));

        var off = await (await admin.PutAsJsonAsync($"/api/schedules/{id}", Body(targetId: w.ProfileId, enabled: false))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, off.GetProperty("nextRunAt").ValueKind);
        var list = await admin.GetFromJsonAsync<JsonElement[]>("/api/schedules");
        Assert.Equal(("Nightly restart", "Main Operations"), (list![0].GetProperty("name").GetString(), list[0].GetProperty("targetName").GetString()));
    }

    [Fact]
    public async Task Schedules_need_schedules_manage_and_next_is_for_server_view()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        await admin.PostAsJsonAsync("/api/schedules", Body(targetId: w.ProfileId));
        await admin.PostAsJsonAsync("/api/schedules", Body(task: "backup", scope: "console", name: "Nightly backup", minute: 300));
        await admin.PostAsJsonAsync("/api/schedules", Body(targetId: w.ProfileId, name: "Off one", enabled: false));

        var zeus = await TestUsers.LoginAsync(f, "S3");
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync("/api/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/schedules", Body(targetId: w.ProfileId))).StatusCode);
        var next = await zeus.GetFromJsonAsync<JsonElement[]>("/api/schedules/next");
        Assert.Equal(["Nightly restart", "Nightly backup"], next!.Select(n => n.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_unit_time_zone_is_validated_and_changes_next_runs()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var owner = w.Owner;
        var created = await (await owner.PostAsJsonAsync("/api/schedules", Body(targetId: w.ProfileId))).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var sydney = created.GetProperty("nextRunAt").GetDateTime();
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("Australia/Sydney", settings.GetProperty("unitTimeZone").GetString());
        object Put(string tz) => new { appName = settings.GetProperty("appName").GetString(), twoFactorRequiredRoles = Array.Empty<string>(), unitTimeZone = tz };

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/settings", Put("Mars/Base"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/settings", Put("Australia/Perth"))).StatusCode);

        var perth = (await owner.GetFromJsonAsync<JsonElement[]>("/api/schedules"))!.Single(s => s.GetProperty("id").GetGuid() == id).GetProperty("nextRunAt").GetDateTime();
        Assert.NotEqual(sydney, perth);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Perth");
        Assert.Equal(new TimeSpan(4, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(perth.ToUniversalTime(), tz).TimeOfDay); // still 04:00, now Perth's
    }

    [Fact]
    public async Task Targets_list_the_profiles_installs_and_agents_a_schedule_can_name()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var t = await (await TestUsers.LoginAsync(f, "S4")).GetFromJsonAsync<JsonElement>("/api/schedules/targets");
        Assert.Contains(t.GetProperty("profiles").EnumerateArray(), p => p.GetProperty("id").GetGuid() == w.ProfileId);
        Assert.Contains(t.GetProperty("installs").EnumerateArray(), i => i.GetProperty("id").GetGuid() == w.InstallId);
        Assert.Contains(t.GetProperty("agents").EnumerateArray(), a => a.GetProperty("name").GetString() == "Dev PC");
        Assert.Equal("Australia/Sydney", t.GetProperty("timeZone").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await (await TestUsers.LoginAsync(f, "S3")).GetAsync("/api/schedules/targets")).StatusCode);
    }

    [Fact]
    public async Task Schedules_are_audited()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        var id = (await (await admin.PostAsJsonAsync("/api/schedules", Body(task: "backup", scope: "console", name: "Nightly backup")))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/api/schedules/{id}", Body(task: "backup", scope: "console", name: "Nightly backup", minute: 300));
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/schedules/{id}/run", null)).StatusCode);
        await AgentKit.WaitUntilAsync(() =>
        {
            using var s = f.Services.CreateScope();
            return !s.ServiceProvider.GetRequiredService<AppDb>().ScheduleRuns.Any(r => r.Outcome == RunOutcome.Running);
        }, 30);
        var runs = await admin.GetFromJsonAsync<JsonElement[]>($"/api/schedules/{id}/runs");
        Assert.Equal("Done", runs!.Single().GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/schedules/{id}")).StatusCode);

        using var scope = f.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync();
        Assert.All(["schedule.created", "schedule.updated", "schedule.run", "schedule.deleted"], a => Assert.Contains(a, actions));
    }
}
