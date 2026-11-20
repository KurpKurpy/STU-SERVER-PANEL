using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Settings;

namespace Panel.Features.Schedules;

// Phase 7b: the Schedules page (schedules.manage) and the dashboard's next three runs (server.view).
public static class ScheduleEndpoints
{
    public sealed record ScheduleBody(string? Name, string? Task, Guid? TargetId, string? Scope, int Days, int MinuteOfDay, bool Enabled = true);
    public sealed record RunDto(Guid Id, DateTime DueAt, DateTime? StartedAt, DateTime? FinishedAt, string Outcome, string? Detail);
    public sealed record ScheduleDto(Guid Id, string Name, string Task, Guid? TargetId, string? TargetName, string? Scope, int Days, int MinuteOfDay,
        bool Enabled, DateTime? NextRunAt, RunDto? LastRun);
    public sealed record NextDto(Guid Id, string Name, string Task, DateTime At);

    public static void MapSchedules(this WebApplication app)
    {
        var g = app.MapGroup("/api/schedules");
        g.MapGet("/next", Next).RequirePermission(Permissions.ServerView);
        g.MapGet("/targets", Targets).RequirePermission(Permissions.SchedulesManage);
        g.MapGet("", List).RequirePermission(Permissions.SchedulesManage);
        g.MapPost("", Create).RequirePermission(Permissions.SchedulesManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.SchedulesManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.SchedulesManage);
        g.MapPost("/{id:guid}/run", RunNow).RequirePermission(Permissions.SchedulesManage);
        g.MapGet("/{id:guid}/runs", Runs).RequirePermission(Permissions.SchedulesManage);
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static RunDto Dto(ScheduleRun r) => new(r.Id, r.DueAt, r.StartedAt, r.FinishedAt, r.Outcome.ToString(), r.Detail);

    private static async Task<ScheduleDto[]> DtosAsync(AppDb db, IEnumerable<Schedule> schedules)
    {
        var list = schedules.ToList();
        var ids = list.Select(s => s.Id).ToList();
        var names = new Dictionary<Guid, string>();
        foreach (var p in await db.Profiles.Select(p => new { p.Id, p.Name }).ToListAsync()) names[p.Id] = p.Name;
        foreach (var i in await db.Installs.Select(i => new { i.Id, i.Name }).ToListAsync()) names[i.Id] = i.Name;
        foreach (var a in await db.Agents.Select(a => new { a.Id, a.Name }).ToListAsync()) names[a.Id] = a.Name;
        var last = (await db.ScheduleRuns.Where(r => ids.Contains(r.ScheduleId)).ToListAsync())
            .GroupBy(r => r.ScheduleId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt ?? r.DueAt).First());
        return list.Select(s => new ScheduleDto(s.Id, s.Name, s.Task, s.TargetId, s.TargetId is { } t && names.TryGetValue(t, out var n) ? n : null, s.Scope,
            s.Days, s.MinuteOfDay, s.Enabled, s.NextRunAt, last.TryGetValue(s.Id, out var r) ? Dto(r) : null)).ToArray();
    }

    // What a schedule can name, for the form (agents.manage isn't needed just to pick one), and the zone its times are in.
    private static async Task<IResult> Targets(AppDb db, AppSettingsService settings) => Results.Ok(new
    {
        profiles = await db.Profiles.OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
        installs = await db.Installs.OrderBy(i => i.Name).Select(i => new { i.Id, i.Name }).ToListAsync(),
        agents = await db.Agents.OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync(),
        timeZone = (await settings.UnitTimeZoneAsync()).Id,
    });

    private static async Task<IResult> List(AppDb db) => Results.Ok(await DtosAsync(db, await db.Schedules.OrderBy(s => s.Name).ToListAsync()));

    private static async Task<IResult?> ValidateAsync(ScheduleBody b, AppDb db)
    {
        if ((b.Name ?? "").Trim().Length is 0 or > 80) return ApiError.Field("name", "Name it: 1–80 characters.");
        if (!ScheduleRunner.Tasks.Contains(b.Task)) return ApiError.Field("task", $"The task is one of: {string.Join(", ", ScheduleRunner.Tasks)}.");
        if (b.Days is < 1 or > ScheduleClock.AllDays) return ApiError.Field("days", "Pick at least one day.");
        if (b.MinuteOfDay is < 0 or > 1439) return ApiError.Field("minuteOfDay", "The time is 00:00 to 23:59.");
        switch (b.Task)
        {
            case "restart" or "stop" or "start":
                if (b.TargetId is not { } p || await db.Profiles.FindAsync(p) is null) return ApiError.Field("targetId", "Pick the server profile.");
                break;
            case "update-server":
                if (b.TargetId is not { } i || await db.Installs.FindAsync(i) is null) return ApiError.Field("targetId", "Pick the server install.");
                break;
            case "backup":
                if (!ScheduleRunner.Scopes.Contains(b.Scope)) return ApiError.Field("scope", "Back up the console, a server, or both.");
                if (b.Scope is "server" or "both" && (b.TargetId is not { } a || await db.Agents.FindAsync(a) is null))
                    return ApiError.Field("targetId", "Pick the server agent to back up.");
                break;
        }
        return null;
    }

    private static async Task Apply(Schedule s, ScheduleBody b, AppSettingsService settings)
    {
        (s.Name, s.Task, s.Days, s.MinuteOfDay, s.Enabled) = (b.Name!.Trim(), b.Task!, b.Days, b.MinuteOfDay, b.Enabled);
        s.TargetId = b.Task == "update-mods" || (b.Task == "backup" && b.Scope == "console") ? null : b.TargetId;
        s.Scope = b.Task == "backup" ? b.Scope : null;
        s.NextRunAt = s.Enabled ? ScheduleClock.Next(s.Days, s.MinuteOfDay, await settings.UnitTimeZoneAsync(), DateTime.UtcNow) : null;
    }

    private static async Task<IResult> Create(ScheduleBody body, AppDb db, AppSettingsService settings, AuditLog audit, HttpContext ctx)
    {
        if (await ValidateAsync(body, db) is { } error) return error;
        var s = new Schedule { Id = Guid.NewGuid(), CreatedById = UserId(ctx) };
        await Apply(s, body, settings);
        db.Schedules.Add(s);
        await db.SaveChangesAsync();
        await audit.RecordAsync("schedule.created", $"Created the schedule {s.Name}", "schedule", s.Id.ToString(), body);
        return Results.Created($"/api/schedules/{s.Id}", (await DtosAsync(db, [s]))[0]);
    }

    private static async Task<IResult> Update(Guid id, ScheduleBody body, AppDb db, AppSettingsService settings, AuditLog audit)
    {
        if (await db.Schedules.FindAsync(id) is not { } s) return ApiError.Of(404, "not_found", "No such schedule.");
        if (await ValidateAsync(body, db) is { } error) return error;
        await Apply(s, body, settings);
        await db.SaveChangesAsync();
        await audit.RecordAsync("schedule.updated", $"Changed the schedule {s.Name}", "schedule", s.Id.ToString(), body);
        return Results.Ok((await DtosAsync(db, [s]))[0]);
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AuditLog audit)
    {
        if (await db.Schedules.FindAsync(id) is not { } s) return ApiError.Of(404, "not_found", "No such schedule.");
        db.Schedules.Remove(s);
        await db.SaveChangesAsync();
        await audit.RecordAsync("schedule.deleted", $"Deleted the schedule {s.Name}", "schedule", s.Id.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> RunNow(Guid id, ScheduleRunner runner, HttpContext ctx) =>
        await runner.RunNowAsync(id, UserId(ctx)) ?? Results.Accepted();

    private static async Task<IResult> Runs(Guid id, AppDb db) =>
        Results.Ok((await db.ScheduleRuns.Where(r => r.ScheduleId == id).OrderByDescending(r => r.DueAt).Take(50).ToListAsync()).Select(Dto));

    private static async Task<IResult> Next(AppDb db) =>
        Results.Ok(await db.Schedules.Where(s => s.Enabled && s.NextRunAt != null).OrderBy(s => s.NextRunAt).Take(3)
            .Select(s => new NextDto(s.Id, s.Name, s.Task, s.NextRunAt!.Value)).ToListAsync());

    // The unit time zone changed: every enabled schedule's next run moves to the same local time in the new zone.
    public static async Task RecomputeAsync(AppDb db, TimeZoneInfo tz)
    {
        foreach (var s in await db.Schedules.Where(s => s.Enabled).ToListAsync())
            s.NextRunAt = ScheduleClock.Next(s.Days, s.MinuteOfDay, tz, DateTime.UtcNow);
        await db.SaveChangesAsync();
    }
}
