using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Notifications;

namespace Panel.Tests;

// Phase 6b Task 1: rules, the per-user feed and the in-app half of the worker (plan Rulings 2–4, 11).
public class NotificationTests
{
    internal static Notifier Notifier(PanelFactory f) => f.Services.GetRequiredService<Notifier>();

    internal static async Task<List<UserNotification>> RowsAsync(PanelFactory f, string? type = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.UserNotifications.Where(n => type == null || n.EventType == type).OrderBy(n => n.CreatedAt).ToListAsync();
    }

    // Waits until the worker has written at least `count` rows of that type.
    internal static async Task<List<UserNotification>> WaitRowsAsync(PanelFactory f, string type, int count)
    {
        await AgentKit.WaitUntilAsync(() => RowsAsync(f, type).GetAwaiter().GetResult().Count >= count);
        return await RowsAsync(f, type);
    }

    private static NotificationEvent Created(string name = "Op Kestrel") =>
        new("operation.created", $"New operation: {name}.", $"**New operation:** {name}", "/operations/x", Permissions.OperationsView);

    [Fact]
    public async Task Rules_are_seeded_once_and_admin_changes_stay()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var rules = await db.NotificationRules.ToDictionaryAsync(r => r.EventType);
        Assert.Equal(["mission.deployed", "operation.cancelled", "operation.created", "operation.ready", "operation.reminder", "schedule.notrun", "server.offline",
            "server.started", "update.failed"], rules.Keys.Order()); // schedule.notrun: Phase 7b
        Assert.Equal((true, true), (rules["operation.created"].InApp, rules["operation.created"].Discord));
        Assert.Equal((true, false), (rules["server.started"].InApp, rules["server.started"].Discord));

        rules["operation.created"].Discord = false;
        db.NotificationRules.Remove(rules["update.failed"]);
        await db.SaveChangesAsync();
        await Seeder.SeedAsync(scope.ServiceProvider);

        var again = await db.NotificationRules.AsNoTracking().ToDictionaryAsync(r => r.EventType);
        Assert.False(again["operation.created"].Discord);
        Assert.False(again.ContainsKey("update.failed"));
    }

    [Fact]
    public async Task An_event_reaches_every_enabled_holder_of_its_permission_and_nobody_else()
    {
        using var f = new PanelFactory();
        await TestUsers.LoginAsync(f, "Owner", "Crow");
        await TestUsers.CreateAsync(f, "S3", "Hawk");
        await TestUsers.CreateAsync(f, "Member", "Pvt");
        var gone = await TestUsers.CreateAsync(f, "Member", "Gone");
        await ReadinessTests.DbAsync(f, async db => (await db.Users.FindAsync(gone.Id))!.LockoutEnd = DateTimeOffset.MaxValue);
        await TestUsers.AddClaimAsync(f, "Pvt", "perm.deny", "operations.view");

        Assert.True(Notifier(f).Enqueue(Created()));

        var rows = await WaitRowsAsync(f, "operation.created", 2);
        await Task.Delay(300); // nothing else arrives
        var names = await NamesAsync(f, await RowsAsync(f, "operation.created"));
        Assert.Equal(["Crow", "Hawk"], names.Order());
        Assert.All(rows, r => Assert.Equal(("New operation: Op Kestrel.", "/operations/x"), (r.Text, r.Link)));
    }

    private static async Task<string[]> NamesAsync(PanelFactory f, IEnumerable<UserNotification> rows)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var ids = rows.Select(r => r.UserId).ToList();
        return await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.UserName!).ToArrayAsync();
    }

    [Fact]
    public async Task A_rule_with_in_app_off_writes_no_rows()
    {
        using var f = new PanelFactory();
        await TestUsers.LoginAsync(f, "Owner", "Crow");
        await ReadinessTests.DbAsync(f, async db => (await db.NotificationRules.FindAsync("operation.created"))!.InApp = false);

        Notifier(f).Enqueue(Created());
        Notifier(f).Enqueue(new NotificationEvent("server.started", "Main started.", null, "/server", Permissions.ServerView));

        await WaitRowsAsync(f, "server.started", 1); // the later event is handled, so the earlier one was too
        Assert.Empty(await RowsAsync(f, "operation.created"));
    }

    [Fact]
    public async Task Users_list_and_mark_only_their_own_rows()
    {
        using var f = new PanelFactory();
        var crow = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var hawk = await TestUsers.LoginAsync(f, "S3", "Hawk");
        Notifier(f).Enqueue(Created("Op One"));
        Notifier(f).Enqueue(Created("Op Two"));
        await WaitRowsAsync(f, "operation.created", 4);

        var mine = await hawk.GetFromJsonAsync<JsonElement>("/api/notifications");
        Assert.Equal(2, mine.GetProperty("unread").GetInt32());
        Assert.Equal(["New operation: Op Two.", "New operation: Op One."], mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));

        var crowRow = (await crow.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await hawk.PostAsync($"/api/notifications/{crowRow}/read", null)).StatusCode);

        var hawkRow = mine.GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await hawk.PostAsync($"/api/notifications/{hawkRow}/read", null)).StatusCode);
        Assert.Equal(1, (await hawk.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("unread").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await hawk.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, (await hawk.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("unread").GetInt32());
        Assert.Equal(2, (await crow.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("unread").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/notifications")).StatusCode);
    }

    [Fact]
    public void A_full_queue_drops_and_counts()
    {
        var n = new Notifier(capacity: 2);
        Assert.True(n.Enqueue(Created("1")));
        Assert.True(n.Enqueue(Created("2")));
        Assert.False(n.Enqueue(Created("3")));
        Assert.Equal(1, n.Dropped);
    }
}
