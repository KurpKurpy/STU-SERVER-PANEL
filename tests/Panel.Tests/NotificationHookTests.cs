using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Notifications;

namespace Panel.Tests;

// Phase 6b Task 3: where events come from (plan Rulings 5–8, Review Focus 3).
public class NotificationHookTests
{
    // The worker handles events in order, so once a marker enqueued now has been written, everything before it has been handled.
    private static async Task FlushAsync(PanelFactory f)
    {
        var marker = "flush-" + Guid.NewGuid().ToString("N");
        NotificationTests.Notifier(f).Enqueue(new NotificationEvent(NotificationEvents.UpdateFailed, marker, null, null, Permissions.ServerView));
        await AgentKit.WaitUntilAsync(() => NotificationTests.RowsAsync(f).GetAwaiter().GetResult().Any(r => r.Text == marker), 10);
    }

    private static async Task<int> CountAsync(PanelFactory f, string type, Func<string, bool>? text = null) =>
        (await NotificationTests.RowsAsync(f, type)).Count(r => text?.Invoke(r.Text) ?? true);

    private static Task RunAsync(ServerMissionsTests.World w, ServerState state, int? pid, string? error = null) =>
        w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, state, pid, pid is null ? null : DateTime.UtcNow, null, error, null));

    [Fact]
    public async Task A_new_pid_is_server_started_once_even_when_hello_repeats_it()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        await RunAsync(w, ServerState.Online, 4242);
        await FlushAsync(f);
        var recipients = await CountAsync(f, NotificationEvents.ServerStarted);
        Assert.True(recipients > 0);
        Assert.Contains("started.", (await NotificationTests.RowsAsync(f, NotificationEvents.ServerStarted))[0].Text);

        await RunAsync(w, ServerState.Online, 4242); // the same run again (a reconnecting agent's hello sends exactly this)
        await FlushAsync(f);
        Assert.Equal(recipients, await CountAsync(f, NotificationEvents.ServerStarted));
    }

    [Fact]
    public async Task Stopping_is_server_offline_and_a_crash_says_unexpectedly()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        await RunAsync(w, ServerState.Online, 4242);
        await RunAsync(w, ServerState.Offline, null);
        await RunAsync(w, ServerState.Online, 5151);
        await RunAsync(w, ServerState.Error, null, "Server exited unexpectedly (exit code 3).");
        await FlushAsync(f);

        var offline = await NotificationTests.RowsAsync(f, NotificationEvents.ServerOffline);
        Assert.Contains(offline, r => r.Text.EndsWith(" stopped."));
        Assert.Contains(offline, r => r.Text.Contains("went offline unexpectedly") && r.Text.Contains("exit code 3"));
        Assert.Equal(2 * await CountAsync(f, NotificationEvents.ServerOffline, t => t.EndsWith(" stopped.")), offline.Count);
    }

    [Fact]
    public async Task An_offline_status_with_no_open_run_notifies_nothing()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        await RunAsync(w, ServerState.Offline, null);
        await RunAsync(w, ServerState.Offline, null);
        await FlushAsync(f);
        Assert.Equal(0, await CountAsync(f, NotificationEvents.ServerOffline));
    }

    [Fact]
    public async Task A_finished_deployment_notifies_mission_viewers()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f); // its missions are written to the install, so each deployment finished
        await using var lease = w.Agent;
        await FlushAsync(f);

        var rows = await NotificationTests.RowsAsync(f, NotificationEvents.MissionDeployed);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Matches(@"^.+ v\d+ is on .+\.$", r.Text));
        Assert.All(rows, r => Assert.StartsWith("/missions/", r.Link));
    }

    [Fact]
    public async Task A_failed_workshop_download_notifies_mods_managers_and_a_cancelled_one_nothing()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        await ModTests.PasteAsync(owner, "111\n222");

        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });
        await agent.SendJobFinishedAsync(new JobResult(agent.LastJob().JobId, false, "Disk full.", []));
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });
        await agent.SendJobFinishedAsync(new JobResult(agent.LastJob().JobId, false, "Cancelled.", []));
        await FlushAsync(f);

        var rows = (await NotificationTests.RowsAsync(f, NotificationEvents.UpdateFailed)).Where(r => !r.Text.StartsWith("flush-")).ToList();
        var row = Assert.Single(rows); // the Owner alone holds mods.manage here; the cancelled job sends nothing
        Assert.Equal(("A Workshop download failed: Disk full.", "/mods"), (row.Text, row.Link));
    }

    [Fact]
    public async Task Creating_and_cancelling_an_operation_notifies_viewers_and_Discord_gets_no_notes()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await w.Owner.PutAsJsonAsync("/api/notifications/discord", new { url = "https://discord.com/api/webhooks/1234567890123456789/" + new string('t', 68) });
        var id = await OperationEndpointsTests.CreateAsync(w.Owner,
            OperationEndpointsTests.Body(w, name: "Op [Kestrel](https://x) @everyone", notes: "secret planner note", briefing: "briefing text"));

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/operations/{id}/status", new { status = "Cancelled" })).StatusCode);
        await FlushAsync(f);

        Assert.Contains("New operation: Op [Kestrel](https://x) @everyone", (await NotificationTests.RowsAsync(f, NotificationEvents.OperationCreated))[0].Text);
        Assert.Contains("is cancelled", (await NotificationTests.RowsAsync(f, NotificationEvents.OperationCancelled))[0].Text);
        await AgentKit.WaitUntilAsync(() => f.Discord.Sent.Count == 2); // Discord has its own queue since the 6b fix pass (final review I1)
        Assert.All(f.Discord.Sent, s =>
        {
            Assert.DoesNotContain("secret planner note", s.Content);
            Assert.DoesNotContain("briefing text", s.Content);
            Assert.Contains("<t:", s.Content);
            Assert.DoesNotContain("[Kestrel]", s.Content); // escaped
        });
    }

    // An edit of what the operation already has, under a new name (and maybe time), carrying its current version.
    private static object Edit(JsonElement op, string name, string? startsAt = null) => new
    {
        version = op.GetProperty("version").GetInt32(), name, description = "A raid on the coast", startsAt = startsAt ?? op.GetProperty("startsAt").GetString(),
        missionId = op.GetProperty("mission").GetProperty("id").GetGuid(),
        missionVersionId = op.GetProperty("missionVersionId").ValueKind == JsonValueKind.Null ? (Guid?)null : op.GetProperty("missionVersionId").GetGuid(),
        profileId = op.GetProperty("profile").GetProperty("id").GetGuid(), modListId = op.GetProperty("modList").GetProperty("id").GetGuid(),
        missionMakerId = (Guid?)null, zeusIds = op.GetProperty("zeus").EnumerateArray().Select(z => z.GetProperty("id").GetGuid()).ToArray(),
        notes = "", briefing = "",
    };

    private static async Task TickAsync(ReadinessTests.Op o, bool done)
    {
        Guid manual;
        using (var scope = o.F.Services.CreateScope())
            manual = (await scope.ServiceProvider.GetRequiredService<AppDb>().ChecklistItems.SingleAsync(i => i.AutoKey == null)).Id;
        Assert.Equal(HttpStatusCode.OK, (await o.W.Owner.PutAsJsonAsync($"/api/operations/{o.Id}/checks/{manual}", new { done })).StatusCode);
    }

    [Fact]
    public async Task Ready_fires_once_not_after_untick_and_tick_again_after_an_edit_and_never_after_Apply()
    {
        using var f = new PanelFactory();
        var o = await ReadinessTests.ReadyAsync(f);
        await using var lease = o.W.Agent;
        await ReadinessTests.PrepareAsync(o.W.Owner, o.Id); // the manual item is unticked: not ready
        await FlushAsync(f);
        Assert.Equal(0, await CountAsync(f, NotificationEvents.OperationReady));

        await TickAsync(o, true);
        await FlushAsync(f);
        var once = await CountAsync(f, NotificationEvents.OperationReady);
        Assert.True(once > 0);

        await TickAsync(o, false);
        await TickAsync(o, true);
        await ReadinessTests.PrepareAsync(o.W.Owner, o.Id);
        await FlushAsync(f);
        Assert.Equal(once, await CountAsync(f, NotificationEvents.OperationReady));

        var op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        Assert.Equal(HttpStatusCode.OK, (await o.W.Owner.PutAsJsonAsync($"/api/operations/{o.Id}", Edit(op, "Renamed"))).StatusCode);
        await ReadinessTests.PrepareAsync(o.W.Owner, o.Id);
        await FlushAsync(f);
        Assert.Equal(2 * once, await CountAsync(f, NotificationEvents.OperationReady));

        await ReadinessTests.DbAsync(f, async db => (await db.Operations.FindAsync(o.Id))!.AppliedAt = DateTime.UtcNow);
        op = await o.W.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{o.Id}");
        Assert.Equal(HttpStatusCode.OK, (await o.W.Owner.PutAsJsonAsync($"/api/operations/{o.Id}", Edit(op, "Again"))).StatusCode);
        await ReadinessTests.PrepareAsync(o.W.Owner, o.Id);
        await FlushAsync(f);
        Assert.Equal(2 * once, await CountAsync(f, NotificationEvents.OperationReady));
    }

    [Fact]
    public async Task RunOnce_reminds_once_rearms_on_a_new_time_skips_started_and_cancelled_ops_and_keeps_Version()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var now = DateTime.UtcNow;
        var soon = await OperationEndpointsTests.CreateAsync(w.Owner, OperationEndpointsTests.Body(w, name: "Soon", startsAt: now.AddMinutes(30).ToString("O")));
        var started = await OperationEndpointsTests.CreateAsync(w.Owner, OperationEndpointsTests.Body(w, name: "Started", startsAt: now.AddMinutes(-5).ToString("O")));
        var later = await OperationEndpointsTests.CreateAsync(w.Owner, OperationEndpointsTests.Body(w, name: "Later", startsAt: now.AddHours(5).ToString("O")));
        var gone = await OperationEndpointsTests.CreateAsync(w.Owner, OperationEndpointsTests.Body(w, name: "Gone", startsAt: now.AddMinutes(20).ToString("O")));
        await w.Owner.PostAsJsonAsync($"/api/operations/{gone}/status", new { status = "Cancelled" });
        var versionBefore = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{soon}")).GetProperty("version").GetInt32();

        async Task<int> RunOnce(DateTime at)
        {
            using var scope = f.Services.CreateScope();
            return await OperationReminders.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), NotificationTests.Notifier(f), 60, at);
        }

        Assert.Equal(1, await RunOnce(now));
        Assert.Equal(0, await RunOnce(now.AddMinutes(1)));
        await FlushAsync(f);
        var reminders = await NotificationTests.RowsAsync(f, NotificationEvents.OperationReminder);
        Assert.NotEmpty(reminders);
        Assert.All(reminders, r => Assert.Equal("Soon starts in 30 minutes.", r.Text));
        Assert.Equal(versionBefore, (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{soon}")).GetProperty("version").GetInt32());

        var op = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/operations/{soon}");
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/operations/{soon}", Edit(op, "Soon", now.AddMinutes(50).ToString("O")))).StatusCode);
        Assert.Equal(1, await RunOnce(now.AddMinutes(2)));
        Assert.Equal(0, await RunOnce(now.AddMinutes(3)));
        _ = started; _ = later;
    }
}
