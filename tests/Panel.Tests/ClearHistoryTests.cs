using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;

namespace Panel.Tests;

// The unit asked (2026-11-21) for a way to clear the history of jobs, notifications and profile revisions.
public class ClearHistoryTests
{
    private static async Task SeedJobsAsync(PanelFactory f, params (string Type, JobStatus Status, string Permission)[] jobs)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        foreach (var (type, status, permission) in jobs)
            db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = type, Status = status, Permission = permission, FinishedAt = status is JobStatus.Running ? null : DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static List<(string, JobStatus)> Jobs(PanelFactory f)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDb>().Jobs.AsNoTracking().OrderBy(j => j.Type).ThenBy(j => j.Status).Select(j => new { j.Type, j.Status })
            .AsEnumerable().Select(j => (j.Type, j.Status)).ToList();
    }

    [Fact]
    public async Task Clearing_jobs_removes_finished_ones_of_those_kinds_and_keeps_the_rest()
    {
        using var f = new PanelFactory();
        var s4 = await TestUsers.LoginAsync(f, "S4");
        await SeedJobsAsync(f, ("workshop.download", JobStatus.Succeeded, Permissions.ModsInstall), ("workshop.download", JobStatus.Failed, Permissions.ModsInstall),
            ("workshop.download", JobStatus.Running, Permissions.ModsInstall), ("server.install", JobStatus.Failed, Permissions.ServerUpdate));

        var r = await s4.PostAsJsonAsync("/api/jobs/clear", new { types = new[] { "workshop.download", "workshop.remove" } });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        Assert.Equal([("server.install", JobStatus.Failed), ("workshop.download", JobStatus.Running)], Jobs(f));
    }

    [Fact]
    public async Task Jobs_you_may_not_run_stay()
    {
        using var f = new PanelFactory();
        var s3 = await TestUsers.LoginAsync(f, "S3"); // sees the jobs (mods.view), can't run downloads (mods.install)
        await SeedJobsAsync(f, ("workshop.download", JobStatus.Succeeded, Permissions.ModsInstall));

        var r = await s3.PostAsJsonAsync("/api/jobs/clear", new { types = new[] { "workshop.download" } });

        Assert.Equal(0, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        Assert.Single(Jobs(f));
    }

    [Fact]
    public async Task Clearing_notifications_removes_only_your_own()
    {
        using var f = new PanelFactory();
        var keeper = await TestUsers.LoginAsync(f, "Member", "Keeper");
        await TestUsers.CreateAsync(f, "Member", "Other");
        var (keeperId, otherId) = (await TestUsers.IdAsync(f, "Keeper"), await TestUsers.IdAsync(f, "Other"));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            foreach (var user in new[] { keeperId, keeperId, otherId })
                db.UserNotifications.Add(new UserNotification { Id = Guid.NewGuid(), UserId = user, EventType = "operation.created", Text = "Op Thunder planned." });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await keeper.PostAsync("/api/notifications/clear", null)).StatusCode);

        Assert.Empty((await keeper.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("items").EnumerateArray());
        using var check = f.Services.CreateScope();
        Assert.Equal(otherId, Assert.Single(check.ServiceProvider.GetRequiredService<AppDb>().UserNotifications).UserId);
    }

    [Fact]
    public async Task Clearing_revisions_keeps_the_current_one_and_the_one_on_the_server_and_numbering_carries_on()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var id = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.FindAsync(id))!.AppliedRevisionId = (await db.ProfileRevisions.SingleAsync(r => r.ProfileId == id && r.Number == 2)).Id;
            await db.SaveChangesAsync();
        }
        await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["hostname"] = "Sunday Op");
        Assert.Equal(new[] { 4, 3, 2, 1 }, await NumbersAsync(owner, id));
        Assert.Equal(HttpStatusCode.Forbidden, (await (await TestUsers.LoginAsync(f, "S3")).PostAsync($"/api/profiles/{id}/revisions/clear", null)).StatusCode);

        var r = await owner.PostAsync($"/api/profiles/{id}/revisions/clear", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
        Assert.Equal(new[] { 4, 2 }, await NumbersAsync(owner, id));
        await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["hostname"] = "Sunday Op 2");
        Assert.Equal(new[] { 5, 4, 2 }, await NumbersAsync(owner, id));
    }

    private static async Task<int[]> NumbersAsync(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/revisions")).EnumerateArray().Select(r => r.GetProperty("number").GetInt32()).ToArray();
}
