using System.Text.Json;
using Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Backups;
using Panel.Features.Jobs;
using Panel.Features.Mods;
using Panel.Features.Notifications;
using Panel.Features.Servers;
using Panel.Hubs;
using Panel.Settings;
using ServerAction = Panel.Features.Servers.ServerEndpoints.Action;

namespace Panel.Features.Schedules;

// How long a schedule "minute" is (tests make it 10 ms), how often the scheduler ticks, and how long a run waits on a job or a stop.
public sealed record ScheduleOptions(double TickSeconds, double MinuteSeconds, double WaitLimitMinutes)
{
    public static ScheduleOptions From(IConfiguration c) =>
        new(c.GetValue("Schedules:TickSeconds", 30.0), c.GetValue("Schedules:MinuteSeconds", 60.0), c.GetValue("Schedules:WaitLimitMinutes", 180.0));

    public TimeSpan Minutes(double minutes) => TimeSpan.FromSeconds(minutes * MinuteSeconds);
    public TimeSpan Poll => TimeSpan.FromMilliseconds(Math.Max(50, Minutes(1).TotalMilliseconds / 12));
}

// Phase 7b: claims due runs once (a conditional update, like the 6b reminders), records missed ones, and runs each task through the same
// code as its button. Anything that touches a server is skipped inside an operation's window (2 h before to 4 h after its start); restarts
// and stops warn players in game at 10, 5 and 1 minutes; updates are skipped while anyone is on and otherwise stop an empty server, update
// (after a pre-update backup) and start it again. Failed, skipped and missed runs notify schedules.manage holders.
public sealed class ScheduleRunner(AppDb db, IServiceScopeFactory scopes, AppSettingsService settings, AgentRegistry registry, AgentCalls agents,
    BackupService backups, ServerEndpoints.Control control, JobService jobs, IWorkshopClient workshop, Notifier notifier, AuditLog audit,
    ScheduleGate gate, IConfiguration config, ILogger<ScheduleRunner> log)
{
    public static readonly string[] Tasks = ["restart", "stop", "start", "update-server", "update-mods", "backup"];
    public static readonly string[] Scopes = ["console", "server", "both"];
    private static readonly int[] Warnings = [10, 5, 1];
    private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(2);
    private const double OpAheadHours = 2, UpdateAheadHours = 3; // final review M1: an update can run for hours
    private const string StillGoing = "Skipped: the previous run is still going";
    public const string SteamSignIn = "Steam needs you to sign in; run one update by hand";
    private const string LastPruneKey = "lastSafetyPrune";
    private readonly ScheduleOptions _o = ScheduleOptions.From(config);

    private sealed record Result(RunOutcome Outcome, string Detail, string? Plain = null); // Plain: the reason without a local time, for notifications

    public async Task<int> RunDueAsync(DateTime nowUtc)
    {
        var due = await db.Schedules.AsNoTracking().Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= nowUtc).ToListAsync();
        if (due.Count == 0) return 0;
        var tz = await settings.UnitTimeZoneAsync();
        var claimed = 0;
        foreach (var s in due)
        {
            var dueAt = s.NextRunAt!.Value;
            var next = ScheduleClock.Next(s.Days, s.MinuteOfDay, tz, nowUtc);
            if (await db.Schedules.Where(x => x.Id == s.Id && x.NextRunAt == dueAt).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAt, next)) != 1) continue;
            claimed++;
            if (nowUtc - dueAt > MissedAfter)
            {
                db.ScheduleRuns.Add(new ScheduleRun
                {
                    Id = Guid.NewGuid(), ScheduleId = s.Id, DueAt = dueAt, FinishedAt = nowUtc, Outcome = RunOutcome.Missed,
                    Detail = $"Missed: the console wasn't running at {ScheduleClock.LocalHourMinute(dueAt, tz)}",
                });
                await db.SaveChangesAsync();
                Notify(s, new Result(RunOutcome.Missed, "", "the console wasn't running when it was due"));
                continue;
            }
            var run = new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = s.Id, DueAt = dueAt, StartedAt = nowUtc, Outcome = RunOutcome.Running };
            if (await TryOpenAsync(run)) { Launch(s.Id, run.Id, null); continue; }
            db.ScheduleRuns.Add(new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = s.Id, DueAt = dueAt, FinishedAt = nowUtc, Outcome = RunOutcome.Skipped, Detail = StillGoing });
            await db.SaveChangesAsync();
            Notify(s, new Result(RunOutcome.Skipped, StillGoing, "the previous run is still going"));
        }
        return claimed;
    }

    // Plan Ruling 4: refused while this schedule has a run in progress.
    public async Task<IResult?> RunNowAsync(Guid scheduleId, Guid? userId)
    {
        if (await db.Schedules.FindAsync(scheduleId) is not { } s) return ApiError.Of(404, "not_found", "No such schedule.");
        if (await db.ScheduleRuns.AnyAsync(r => r.ScheduleId == scheduleId && r.Outcome == RunOutcome.Running))
            return ApiError.Of(409, "running", "That schedule is running now; wait for it to finish.");
        var run = new ScheduleRun { Id = Guid.NewGuid(), ScheduleId = scheduleId, DueAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, Outcome = RunOutcome.Running };
        if (!await TryOpenAsync(run)) return ApiError.Of(409, "running", "That schedule is running now; wait for it to finish.");
        await audit.RecordAsync("schedule.run", $"Ran {s.Name} now", "schedule", s.Id.ToString(), new { s.Task }, actorId: userId);
        Launch(scheduleId, run.Id, userId);
        return null;
    }

    // Final review I1: the unique index on a schedule's Running run makes "one run at a time" hold across the tick and Run now.
    private async Task<bool> TryOpenAsync(ScheduleRun run)
    {
        db.ScheduleRuns.Add(run);
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException) { db.Entry(run).State = EntityState.Detached; return false; }
    }

    private void Launch(Guid scheduleId, Guid runId, Guid? userId) => _ = Task.Run(async () =>
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().ExecuteAsync(scheduleId, runId, userId);
    });

    public async Task ExecuteAsync(Guid scheduleId, Guid runId, Guid? userId)
    {
        if (await db.Schedules.FindAsync(scheduleId) is not { } s || await db.ScheduleRuns.FindAsync(runId) is not { } run) return;
        Result r;
        try { r = await RunTaskAsync(s); }
        catch (Exception e)
        {
            log.LogWarning(e, "Scheduled task {Name} failed", s.Name);
            r = new Result(RunOutcome.Failed, e.Message);
        }
        (run.Outcome, run.Detail, run.FinishedAt) = (r.Outcome, Clip(r.Detail, 500), DateTime.UtcNow);
        await db.SaveChangesAsync();
        if (userId is null)
            await audit.RecordAsync("schedule.run", $"Scheduled task {s.Name}: {r.Outcome.ToString().ToLowerInvariant()}", "schedule", s.Id.ToString(),
                new { s.Task, outcome = r.Outcome.ToString(), detail = run.Detail });
        if (r.Outcome is RunOutcome.Failed or RunOutcome.Skipped) Notify(s, r);
    }

    // Final review I1: tasks that touch servers run one at a time, and the operation window is checked once a task holds the gate.
    private async Task<Result> RunTaskAsync(Schedule s)
    {
        if (s.Task == "backup") return await BackupAsync(s);
        await gate.One.WaitAsync();
        try
        {
            var ahead = s.Task.StartsWith("update-") ? UpdateAheadHours : OpAheadHours;
            if (await OperationWindowAsync(DateTime.UtcNow, ahead) is { } op)
            {
                var tz = await settings.UnitTimeZoneAsync();
                return new Result(RunOutcome.Skipped, $"Skipped: operation {op.Name} at {ScheduleClock.LocalHourMinute(op.StartsAt, tz)}", $"operation {op.Name} is on");
            }
            return s.Task switch
            {
                "restart" or "stop" => await RestartOrStopAsync(s),
                "start" => await StartAsync(s),
                "update-server" => await UpdateServerAsync(s),
                "update-mods" => await UpdateModsAsync(),
                _ => new Result(RunOutcome.Failed, $"Unknown task \"{s.Task}\"."),
            };
        }
        finally { gate.One.Release(); }
    }

    // The source spec's "never during an event": an operation that isn't cancelled starting within 2 h (3 h for updates), or that started
    // less than 4 h ago.
    private Task<Operation?> OperationWindowAsync(DateTime now, double aheadHours) =>
        db.Operations.AsNoTracking().Where(o => o.Status != OperationStatus.Cancelled && o.StartsAt >= now.AddHours(-4) && o.StartsAt <= now.AddHours(aheadHours))
            .OrderBy(o => o.StartsAt).FirstOrDefaultAsync();

    private static bool Running(ProfileStatus? s) => s is { State: not (ServerState.Offline or ServerState.Error) };

    private async Task<Result> RestartOrStopAsync(Schedule s)
    {
        if (s.TargetId is not { } pid || await db.Profiles.FindAsync(pid) is not { } p) return new Result(RunOutcome.Failed, "The server profile is gone.");
        var restart = s.Task == "restart";
        var status = registry.Status(pid);
        if (!Running(status)) return new Result(RunOutcome.Done, restart ? $"{p.Name} wasn't running; nothing to restart." : $"{p.Name} was already stopped.");
        if (status!.Query?.Players > 0 && status.Rcon == RconState.Connected) await WarnAsync(pid, restart ? "Server restart" : "Server shutting down");
        return Describe(await control.RunAsync(pid, restart ? ServerAction.Restart : ServerAction.Stop), restart ? $"Restarted {p.Name}." : $"Stopped {p.Name}.");
    }

    private async Task WarnAsync(Guid profileId, string what)
    {
        var agentId = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == profileId select i.AgentId).FirstAsync();
        for (var i = 0; i < Warnings.Length; i++)
        {
            var minutes = Warnings[i];
            try { await agents.CallAsync(agentId, c => c.Rcon(profileId, $"say -1 {what} in {minutes} minute(s)")); }
            catch (Exception e) when (e is AgentUnavailableException or HubException) { }
            await Task.Delay(_o.Minutes(minutes - (i + 1 < Warnings.Length ? Warnings[i + 1] : 0)));
        }
    }

    private async Task<Result> StartAsync(Schedule s)
    {
        if (s.TargetId is not { } pid || await db.Profiles.FindAsync(pid) is not { } p) return new Result(RunOutcome.Failed, "The server profile is gone.");
        if (Running(registry.Status(pid))) return new Result(RunOutcome.Done, $"{p.Name} was already running.");
        return Describe(await control.RunAsync(pid, ServerAction.Start), $"Started {p.Name}.");
    }

    private async Task<Result> UpdateServerAsync(Schedule s)
    {
        if (s.TargetId is not { } installId || await db.Installs.FindAsync(installId) is not { } install) return new Result(RunOutcome.Failed, "The server install is gone.");
        var profiles = await db.Profiles.Where(p => p.ServerInstallId == installId).Select(p => p.Id).ToListAsync();
        return await StoppedAroundAsync(profiles, async () =>
        {
            var (job, error) = await InstallTools.QueueServerUpdateAsync(installId, null, db, registry, settings, jobs, backups, audit, null);
            return error is not null ? Describe(error, "") : await WaitJobAsync(job!, $"Updated {install.Name}.");
        });
    }

    private async Task<Result> UpdateModsAsync()
    {
        if (await ModLibrary.AgentAsync(db) is not { } agentId) return new Result(RunOutcome.Failed, "Register a server install first.");
        var warnings = await ModEndpoints.RefreshAsync(db, workshop, agents);
        var updates = (await db.Mods.Where(m => m.WorkshopId != null).ToListAsync()).Count(ModLibrary.UpdateAvailable);
        if (updates == 0) return new Result(RunOutcome.Done, string.Join(" ", warnings.Prepend("No mod updates.")));
        var profiles = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where i.AgentId == agentId select p.Id).ToListAsync();
        return await StoppedAroundAsync(profiles, async () =>
        {
            var (job, error) = await ModEndpoints.QueueInstallAsync(new InstallBody(null, true, "update"), db, settings, jobs, registry, audit, backups, null);
            return error is not null ? Describe(error, "") : await WaitJobAsync(job!, $"Updated {updates} mod(s).");
        });
    }

    // Skipped while anyone is on; otherwise the running (empty) servers stop first, and every one that stopped starts again afterwards,
    // whatever happened (final review M2: also when another refused to stop or didn't stop in time, and then nothing is updated).
    private async Task<Result> StoppedAroundAsync(List<Guid> profiles, Func<Task<Result>> work)
    {
        var running = profiles.Where(id => Running(registry.Status(id))).ToList();
        var players = running.Sum(id => registry.Status(id)?.Query?.Players ?? 0);
        if (players > 0) return new Result(RunOutcome.Skipped, $"Skipped: {players} player(s) online", $"{players} player(s) online");
        var stopped = new List<Guid>();
        var refusals = new List<string>();
        foreach (var id in running)
            if (Describe(await control.RunAsync(id, ServerAction.Stop), "") is { Outcome: RunOutcome.Failed } refused) refusals.Add(refused.Detail);
            else stopped.Add(id);
        var deadline = DateTime.UtcNow + _o.Minutes(_o.WaitLimitMinutes);
        while (stopped.Any(id => Running(registry.Status(id))) && DateTime.UtcNow <= deadline) await Task.Delay(_o.Poll);
        Result result;
        if (refusals.Count > 0) result = new Result(RunOutcome.Failed, $"A server refused to stop, so nothing was updated: {string.Join(" ", refusals)}");
        else if (stopped.Any(id => Running(registry.Status(id)))) result = new Result(RunOutcome.Failed, "A server didn't stop in time, so nothing was updated.");
        else
            try { result = await work(); }
            catch (Exception e) { result = new Result(RunOutcome.Failed, e.Message); }
        var notStarted = new List<string>();
        foreach (var id in stopped.Where(id => !Running(registry.Status(id))))
            if (Describe(await control.RunAsync(id, ServerAction.Start), "") is { Outcome: RunOutcome.Failed } refused) notStarted.Add(refused.Detail);
        return notStarted.Count == 0 ? result
            : result with { Outcome = RunOutcome.Failed, Detail = $"{result.Detail} Starting the server again failed: {string.Join(" ", notStarted)}" };
    }

    private async Task<Result> WaitJobAsync(Job job, string done)
    {
        var deadline = DateTime.UtcNow + _o.Minutes(_o.WaitLimitMinutes);
        while (DateTime.UtcNow < deadline)
        {
            var now = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
            switch (now.Status)
            {
                case JobStatus.Succeeded: return new Result(RunOutcome.Done, done);
                case JobStatus.NeedsInput:
                    try { await agents.CallAsync(now.AgentId!.Value, c => c.CancelJob(now.Id)); }
                    catch (Exception e) when (e is AgentUnavailableException or HubException) { }
                    // The server can't start again while the job is open, so wait (briefly) for the agent to report it cancelled.
                    var closeBy = DateTime.UtcNow + TimeSpan.FromSeconds(60); // real time: the agent confirms a cancel in seconds
                    while (DateTime.UtcNow < closeBy && JobService.IsOpen((await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id)).Status))
                        await Task.Delay(_o.Poll);
                    return new Result(RunOutcome.Failed, SteamSignIn);
                case JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted:
                    return new Result(RunOutcome.Failed, now.Error ?? "The update failed.");
            }
            await Task.Delay(_o.Poll);
        }
        return new Result(RunOutcome.Failed, "The update didn't finish in time.");
    }

    private async Task<Result> BackupAsync(Schedule s)
    {
        var ids = new List<Guid>();
        if (s.Scope is "server" or "both" && s.TargetId is null) return new Result(RunOutcome.Failed, "Pick the server agent to back up.");
        if (s.Scope is "console" or "both") ids.Add((await backups.StartAsync(BackupType.Console, BackupKind.Scheduled, null, null)).Id);
        if (s.Scope is "server" or "both") ids.Add((await backups.StartAsync(BackupType.Server, BackupKind.Scheduled, s.TargetId, null)).Id);
        var deadline = DateTime.UtcNow + _o.Minutes(_o.WaitLimitMinutes);
        List<Backup> rows;
        while ((rows = await db.Backups.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync()).Any(b => b.Status == BackupStatus.Running))
        {
            if (DateTime.UtcNow > deadline) return new Result(RunOutcome.Failed, "The backup didn't finish in time.");
            await Task.Delay(_o.Poll);
        }
        var failed = rows.Where(b => b.Status == BackupStatus.Failed).ToList();
        return failed.Count > 0
            ? new Result(RunOutcome.Failed, string.Join(" ", failed.Select(b => $"{b.Type} backup failed: {b.Error}")))
            : new Result(RunOutcome.Done, "Made " + string.Join(", ", rows.Select(b => b.FileName)) + ".");
    }

    // A refused control or queue call (an ApiError) becomes a failed run with its message.
    private static Result Describe(IResult result, string done)
    {
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status < 300) return new Result(RunOutcome.Done, done);
        string? message = null;
        if (result is IValueHttpResult { Value: { } value })
            try { message = JsonSerializer.SerializeToElement(value).TryGetProperty("message", out var m) ? m.GetString() : null; }
            catch (NotSupportedException) { }
        return new Result(RunOutcome.Failed, message ?? $"Refused ({status}).");
    }

    private void Notify(Schedule s, Result r)
    {
        var why = r.Plain ?? r.Detail;
        var text = r.Outcome switch
        {
            RunOutcome.Missed => $"{s.Name} didn't run: {why}.",
            RunOutcome.Skipped => $"{s.Name} was skipped: {why}.",
            _ => $"{s.Name} failed: {why.TrimEnd('.')}.",
        };
        notifier.Enqueue(new NotificationEvent(NotificationEvents.ScheduleNotRun, Clip(text, 300), DiscordText.Escape(Clip(text, 1900)), "/schedules",
            Permissions.SchedulesManage));
    }

    // At startup: a run the console was in the middle of is closed as failed (nothing will finish it) and its people are told (final
    // review M3), since an update or restart cut off part-way can leave a server stopped.
    public async Task SweepInterruptedAsync()
    {
        var cut = await (from r in db.ScheduleRuns join s in db.Schedules on r.ScheduleId equals s.Id where r.Outcome == RunOutcome.Running select new { r, s }).ToListAsync();
        foreach (var x in cut)
            (x.r.Outcome, x.r.FinishedAt, x.r.Detail) = (RunOutcome.Failed, DateTime.UtcNow,
                "The console restarted while this ran." + (x.s.Task is "restart" or "update-server" or "update-mods" ? " A server stopped for it may still be stopped." : ""));
        await db.SaveChangesAsync();
        foreach (var x in cut) Notify(x.s, new Result(RunOutcome.Failed, x.r.Detail!));
    }

    // Once a day (the unit's date), each connected agent prunes its old safety copies.
    public async Task<bool> PruneDailyAsync(DateTime nowUtc)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, await settings.UnitTimeZoneAsync()).ToString("yyyy-MM-dd");
        if ((await db.Settings.FindAsync(LastPruneKey))?.Value == today) return false;
        await settings.SetAsync(LastPruneKey, today);
        var connected = (await db.Agents.Select(a => a.Id).ToListAsync()).Where(registry.IsConnected).ToList();
        await backups.PruneSafetyCopiesAsync(connected);
        return true;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}

// Phase 7b: ticks every Schedules:TickSeconds (0 = off, as in the tests). At startup, a run the console was in the middle of is closed as
// failed, since nothing will finish it.
public sealed class Scheduler(IServiceScopeFactory scopes, IConfiguration config, ILogger<Scheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using (var scope = scopes.CreateScope()) await scope.ServiceProvider.GetRequiredService<ScheduleRunner>().SweepInterruptedAsync();
        var options = ScheduleOptions.From(config);
        if (options.TickSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.TickSeconds));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
                    await runner.RunDueAsync(DateTime.UtcNow);
                    await runner.PruneDailyAsync(DateTime.UtcNow);
                }
                catch (Exception e) when (!stop.IsCancellationRequested) { log.LogWarning(e, "A schedule tick failed"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}

// One server-touching scheduled task at a time across the whole console (a singleton).
public sealed class ScheduleGate
{
    public SemaphoreSlim One { get; } = new(1, 1);
}
