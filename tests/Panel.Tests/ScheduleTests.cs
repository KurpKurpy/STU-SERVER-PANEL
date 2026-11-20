using System.Net;
using System.Net.Http.Json;
using Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using Panel.Features.Schedules;

namespace Panel.Tests;

// Phase 7b Task 3: when schedules run (unit time zone, daylight saving), that a run happens once, missed runs, the operation window,
// in-game warnings, and the update flow (stop an empty server, back up, update, start again). PanelFactory scales a minute to 10 ms.
public class ScheduleTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const int Sun = 1 << 0, Fri = 1 << 5, Every = 127;
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static async Task<Guid> ScheduleAsync(PanelFactory f, string task, Guid? target, DateTime nextRunAt, string? scope = null)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDb>();
        var row = new Schedule { Id = Guid.NewGuid(), Name = "Nightly " + task, Task = task, TargetId = target, Scope = scope, Days = Every, MinuteOfDay = 240, NextRunAt = nextRunAt };
        db.Schedules.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<int> TickAsync(PanelFactory f, DateTime now)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ScheduleRunner>().RunDueAsync(now);
    }

    private static async Task<ScheduleRun> FinishedRunAsync(PanelFactory f, Guid scheduleId, int seconds = 15)
    {
        ScheduleRun? run = null;
        await AgentKit.WaitUntilAsync(() =>
        {
            using var s = f.Services.CreateScope();
            run = s.ServiceProvider.GetRequiredService<AppDb>().ScheduleRuns.AsNoTracking().FirstOrDefault(r => r.ScheduleId == scheduleId && r.Outcome != RunOutcome.Running);
            return run is not null;
        }, seconds);
        return run!;
    }

    private static int IndexOf(FakeAgent a, Func<(string Method, object?[] Args), bool> match) => a.Calls.ToList().FindIndex(c => match(c));
    private static int IndexOf(FakeAgent a, string method) => IndexOf(a, c => c.Method == method);
    private static int IndexOfJob(FakeAgent a, JobKind kind) => IndexOf(a, c => c.Method == nameof(IAgentClient.RunJob) && ((JobSpec)c.Args[0]!).Kind == kind);
    private static JobSpec Job(FakeAgent a, JobKind kind) => (JobSpec)a.Calls.Last(c => c.Method == nameof(IAgentClient.RunJob) && ((JobSpec)c.Args[0]!).Kind == kind).Args[0]!;

    private static Task OnlineAsync(ServerMissionsTests.World w, int players, RconState rcon = RconState.Connected) =>
        w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, new QueryInfo("13th", "Altis", "m", players, 40), null, null, 0, rcon));

    private static async Task OperationAsync(PanelFactory f, string name, DateTime startsAt)
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDb>();
        db.Operations.Add(new Operation { Id = Guid.NewGuid(), Name = name, StartsAt = startsAt, Status = OperationStatus.Planned });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void Next_run_follows_weekdays_and_the_unit_time_zone_across_daylight_saving()
    {
        Assert.Equal(Utc(2026, 11, 20, 7, 30), ScheduleClock.Next(Fri | Sun, 18 * 60 + 30, Sydney, Utc(2026, 11, 18, 0, 0)));  // Fri 18:30 AEDT
        Assert.Equal(Utc(2026, 10, 3, 16, 30), ScheduleClock.Next(Sun, 150, Sydney, Utc(2026, 10, 3, 12, 0)));  // 02:30 doesn't exist: 03:30 AEDT
        Assert.Equal(Utc(2027, 4, 3, 15, 30), ScheduleClock.Next(Sun, 150, Sydney, Utc(2027, 4, 3, 12, 0)));    // 02:30 happens twice: the first
        Assert.Equal(Utc(2027, 4, 10, 16, 30), ScheduleClock.Next(Sun, 150, Sydney, Utc(2027, 4, 3, 15, 30)));  // and only once: next week
    }

    [Fact]
    public async Task A_due_schedule_runs_once_even_when_two_ticks_race()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await OnlineAsync(w, 0);
        var now = DateTime.UtcNow;
        var id = await ScheduleAsync(f, "restart", w.ProfileId, now.AddSeconds(-30));

        var claimed = await Task.WhenAll(TickAsync(f, now), TickAsync(f, now));

        Assert.Equal(1, claimed.Sum());
        Assert.Equal(RunOutcome.Done, (await FinishedRunAsync(f, id)).Outcome);
        Assert.Equal(1, w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Restart)));
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Equal(1, await db.ScheduleRuns.CountAsync(r => r.ScheduleId == id));
        Assert.True((await db.Schedules.FindAsync(id))!.NextRunAt > now);
    }

    [Fact]
    public async Task A_run_missed_while_the_panel_was_down_is_recorded_not_run_and_notifies()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await TestUsers.CreateAsync(f, "S4", "Keeper");
        var now = DateTime.UtcNow;
        var due = now.AddMinutes(-10);
        var id = await ScheduleAsync(f, "restart", w.ProfileId, due);

        Assert.Equal(1, await TickAsync(f, now));

        var run = await FinishedRunAsync(f, id);
        Assert.Equal(RunOutcome.Missed, run.Outcome);
        Assert.Equal($"Missed: the console wasn't running at {TimeZoneInfo.ConvertTimeFromUtc(due, Sydney):HH:mm}", run.Detail);
        Assert.Equal(-1, IndexOf(w.Agent, nameof(IAgentClient.Restart)));
        var keeper = await TestUsers.IdAsync(f, "Keeper");
        await AgentKit.WaitUntilAsync(() =>
        {
            using var s = f.Services.CreateScope();
            return s.ServiceProvider.GetRequiredService<AppDb>().UserNotifications.Any(n => n.UserId == keeper && n.EventType == "schedule.notrun");
        });
    }

    [Fact]
    public async Task Restart_is_skipped_within_an_operations_window_and_says_which()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await OnlineAsync(w, 0);
        var now = DateTime.UtcNow;
        var opAt = now.AddHours(1);
        await OperationAsync(f, "Op Thunder", opAt);
        var skipped = await ScheduleAsync(f, "restart", w.ProfileId, now.AddSeconds(-30));

        await TickAsync(f, now);

        var run = await FinishedRunAsync(f, skipped);
        Assert.Equal(RunOutcome.Skipped, run.Outcome);
        Assert.Equal($"Skipped: operation Op Thunder at {TimeZoneInfo.ConvertTimeFromUtc(opAt, Sydney):HH:mm}", run.Detail);
        Assert.Equal(-1, IndexOf(w.Agent, nameof(IAgentClient.Restart)));

        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Operations.SingleAsync()).StartsAt = now.AddHours(-5);
            await db.SaveChangesAsync();
        }
        var runs = await ScheduleAsync(f, "restart", w.ProfileId, now.AddSeconds(-30));
        await TickAsync(f, now);
        Assert.Equal(RunOutcome.Done, (await FinishedRunAsync(f, runs)).Outcome);
    }

    [Fact]
    public async Task Restart_with_players_on_warns_at_10_5_1_then_restarts()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await OnlineAsync(w, 3);
        var id = await ScheduleAsync(f, "restart", w.ProfileId, DateTime.UtcNow.AddSeconds(-30));

        await TickAsync(f, DateTime.UtcNow);

        Assert.Equal(RunOutcome.Done, (await FinishedRunAsync(f, id)).Outcome);
        var says = w.Agent.Calls.Where(c => c.Method == nameof(IAgentClient.Rcon)).Select(c => (string)c.Args[1]!).ToArray();
        Assert.Equal(["say -1 Server restart in 10 minute(s)", "say -1 Server restart in 5 minute(s)", "say -1 Server restart in 1 minute(s)"], says);
        Assert.True(IndexOf(w.Agent, nameof(IAgentClient.Restart)) > IndexOf(w.Agent, c => c.Method == nameof(IAgentClient.Rcon) && ((string)c.Args[1]!).Contains(" 1 minute")));
    }

    [Fact]
    public async Task Update_server_is_skipped_with_players_on()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await OnlineAsync(w, 3);
        var id = await ScheduleAsync(f, "update-server", w.InstallId, DateTime.UtcNow.AddSeconds(-30));

        await TickAsync(f, DateTime.UtcNow);

        var run = await FinishedRunAsync(f, id);
        Assert.Equal((RunOutcome.Skipped, "Skipped: 3 player(s) online"), (run.Outcome, run.Detail));
        Assert.Equal(-1, IndexOf(w.Agent, nameof(IAgentClient.RunJob)));
    }

    private static async Task<(Guid Schedule, JobSpec Install)> UpdateEmptyServerAsync(PanelFactory f, ServerMissionsTests.World w)
    {
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        await OnlineAsync(w, 0);
        var id = await ScheduleAsync(f, "update-server", w.InstallId, DateTime.UtcNow.AddSeconds(-30));
        await TickAsync(f, DateTime.UtcNow);
        await AgentKit.WaitUntilAsync(() => IndexOf(w.Agent, nameof(IAgentClient.Stop)) >= 0, 15);
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Offline, null, null, null, null, 0));
        await AgentKit.WaitUntilAsync(() => IndexOfJob(w.Agent, JobKind.ServerInstall) >= 0, 15);
        var backup = Job(w.Agent, JobKind.ServerBackup);
        await w.Agent.SendJobFinishedAsync(new JobResult(backup.JobId, true, null, [], Made: new BackupMade("server-20261121-030000-pre-update.zip", 1, null)));
        return (id, Job(w.Agent, JobKind.ServerInstall));
    }

    [Fact]
    public async Task Update_server_on_an_empty_running_server_stops_backs_up_updates_and_starts_again()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var (id, install) = await UpdateEmptyServerAsync(f, w);

        await w.Agent.SendJobFinishedAsync(new JobResult(install.JobId, true, null, []));

        Assert.Equal(RunOutcome.Done, (await FinishedRunAsync(f, id)).Outcome);
        var (stop, backup, update, start) = (IndexOf(w.Agent, nameof(IAgentClient.Stop)), IndexOfJob(w.Agent, JobKind.ServerBackup),
            IndexOfJob(w.Agent, JobKind.ServerInstall), IndexOf(w.Agent, nameof(IAgentClient.Start)));
        Assert.True(stop < backup && backup < update && update < start, $"order {stop} {backup} {update} {start}");
    }

    [Fact]
    public async Task A_scheduled_update_that_needs_Steam_sign_in_fails_cancels_and_restarts_the_server()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var (id, install) = await UpdateEmptyServerAsync(f, w);
        w.Agent.OnCancelJob = jobId => // as the agent does: the cancelled job reports back
        {
            _ = Task.Run(async () => { await Task.Delay(50); await w.Agent.SendJobFinishedAsync(new JobResult(jobId, false, "Cancelled.", [])); });
            return new CommandResult(true, null);
        };

        await w.Agent.SendJobUpdateAsync(new JobUpdate(install.JobId, null, [], SteamPrompt.Password));

        var run = await FinishedRunAsync(f, id);
        Assert.Equal("Steam needs you to sign in; run one update by hand", run.Detail);
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Contains(w.Agent.Calls, c => c.Method == nameof(IAgentClient.CancelJob) && (Guid)c.Args[0]! == install.JobId);
        Assert.True(IndexOf(w.Agent, nameof(IAgentClient.Start)) > IndexOf(w.Agent, nameof(IAgentClient.CancelJob)));
    }

    // Final review I1: a schedule never runs twice at once; a due run while a Run now is still going is skipped, not started.
    [Fact]
    public async Task A_due_run_is_skipped_while_a_run_now_of_the_same_schedule_is_still_going()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        await OnlineAsync(w, 0);
        var id = await ScheduleAsync(f, "update-server", w.InstallId, DateTime.UtcNow.AddDays(1));
        using (var s = f.Services.CreateScope()) Assert.Null(await s.ServiceProvider.GetRequiredService<ScheduleRunner>().RunNowAsync(id, null));
        await AgentKit.WaitUntilAsync(() => IndexOf(w.Agent, nameof(IAgentClient.Stop)) >= 0, 15);
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Offline, null, null, null, null, 0));
        await AgentKit.WaitUntilAsync(() => IndexOfJob(w.Agent, JobKind.ServerInstall) >= 0, 15);

        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Schedules.FindAsync(id))!.NextRunAt = DateTime.UtcNow.AddSeconds(-30);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await TickAsync(f, DateTime.UtcNow));

        using (var s = f.Services.CreateScope())
        {
            var skipped = await s.ServiceProvider.GetRequiredService<AppDb>().ScheduleRuns.SingleAsync(r => r.ScheduleId == id && r.Outcome == RunOutcome.Skipped);
            Assert.Equal("Skipped: the previous run is still going", skipped.Detail);
        }
        Assert.Equal(1, w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.RunJob) && ((JobSpec)c.Args[0]!).Kind == JobKind.ServerInstall));
    }

    // Final review M1 (graded up): an update can take hours, so updates keep 3 hours clear before an operation; restarts keep 2.
    [Fact]
    public async Task Updates_keep_three_hours_clear_before_an_operation_and_restarts_two()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await OnlineAsync(w, 0);
        var now = DateTime.UtcNow;
        await OperationAsync(f, "Op Thunder", now.AddHours(2.5));
        var update = await ScheduleAsync(f, "update-server", w.InstallId, now.AddSeconds(-30));
        var restart = await ScheduleAsync(f, "restart", w.ProfileId, now.AddSeconds(-30));

        await TickAsync(f, now);

        Assert.Equal(RunOutcome.Skipped, (await FinishedRunAsync(f, update)).Outcome);
        Assert.Equal(RunOutcome.Done, (await FinishedRunAsync(f, restart)).Outcome);
    }

    // Final review M2 (graded up): if one server refuses to stop, the ones already stopped for the update are started again.
    [Fact]
    public async Task Servers_stopped_for_an_update_start_again_when_another_refuses_to_stop()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var second = await AgentKit.CreateProfileAsync(w.Owner, AgentKit.Profile(w.InstallId, "Training", "training", 2402));
        await OnlineAsync(w, 0);
        await w.Agent.SendStatusAsync(new ProfileStatus(second, ServerState.Online, 4343, DateTime.UtcNow, new QueryInfo("13th", "Altis", "m", 0, 40), null, null));
        w.Agent.OnStop = id =>
        {
            if (id == second) return new CommandResult(false, "The server is already stopping.");
            _ = Task.Run(() => w.Agent.SendStatusAsync(new ProfileStatus(id, ServerState.Offline, null, null, null, null, 0))); // as the agent reports a stop
            return new CommandResult(true, null);
        };
        w.Agent.OnStart = s => { _ = w.Agent.SendStatusAsync(new ProfileStatus(s.ProfileId, ServerState.Starting, 4242, DateTime.UtcNow, null, null, null)); return new StartResult(true, null, 4242, DateTime.UtcNow); };
        var id = await ScheduleAsync(f, "update-server", w.InstallId, DateTime.UtcNow.AddSeconds(-30));

        await TickAsync(f, DateTime.UtcNow);

        var run = await FinishedRunAsync(f, id);
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Contains(w.Agent.Calls, c => c.Method == nameof(IAgentClient.Start) && ((LaunchSpec)c.Args[0]!).ProfileId == w.ProfileId);
        Assert.Equal(-1, IndexOfJob(w.Agent, JobKind.ServerInstall));
    }

    // Final review M3 (graded up): a run the console restarted in the middle of is closed and its people are told.
    [Fact]
    public async Task A_run_cut_off_by_a_restart_is_closed_and_notifies()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        await TestUsers.CreateAsync(f, "S4", "Keeper");
        var id = await ScheduleAsync(f, "backup", null, DateTime.UtcNow.AddDays(1), scope: "console");
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDb>();
            db.ScheduleRuns.Add(new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = id, DueAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Outcome = RunOutcome.Running });
            await db.SaveChangesAsync();
        }

        using (var s = f.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<ScheduleRunner>().SweepInterruptedAsync();

        var run = await FinishedRunAsync(f, id);
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.StartsWith("The console restarted while this ran", run.Detail);
        var keeper = await TestUsers.IdAsync(f, "Keeper");
        await AgentKit.WaitUntilAsync(() =>
        {
            using var s = f.Services.CreateScope();
            return s.ServiceProvider.GetRequiredService<AppDb>().UserNotifications.Any(n => n.UserId == keeper && n.EventType == "schedule.notrun");
        });
    }

    [Fact]
    public async Task Backup_runs_inside_an_operations_window()
    {
        using var f = new PanelFactory();
        await OperationAsync(f, "Op Thunder", DateTime.UtcNow.AddHours(1));
        var id = await ScheduleAsync(f, "backup", null, DateTime.UtcNow.AddSeconds(-30), scope: "console");

        await TickAsync(f, DateTime.UtcNow);

        var run = await FinishedRunAsync(f, id, 30);
        Assert.Equal(RunOutcome.Done, run.Outcome);
        Assert.Contains("console-", run.Detail);
    }

    [Fact]
    public async Task Run_now_is_refused_while_a_run_is_in_progress()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        var id = await ScheduleAsync(f, "backup", null, DateTime.UtcNow.AddDays(1), scope: "console");
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDb>();
        db.ScheduleRuns.Add(new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = id, DueAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Outcome = RunOutcome.Running });
        await db.SaveChangesAsync();

        var refused = await s.ServiceProvider.GetRequiredService<ScheduleRunner>().RunNowAsync(id, null);

        Assert.Equal(409, (refused as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public async Task Safety_copies_are_pruned_once_a_day_on_each_agent()
    {
        using var f = new PanelFactory();
        var (_, _, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        using var s = f.Services.CreateScope();
        var runner = s.ServiceProvider.GetRequiredService<ScheduleRunner>();

        Assert.True(await runner.PruneDailyAsync(DateTime.UtcNow));
        Assert.False(await runner.PruneDailyAsync(DateTime.UtcNow));

        Assert.Equal([30], agent.Calls.Where(c => c.Method == nameof(IAgentClient.PruneSafetyCopies)).Select(c => (int)c.Args[0]!));
    }
}
