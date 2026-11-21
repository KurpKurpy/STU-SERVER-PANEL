using System.Security.Claims;
using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Hubs;
using Panel.Settings;

namespace Panel.Features.Jobs;

public record AnswerBody(string? Answer);
public record ClearJobsBody(string[]? Types);
public record SteamBody(string? Username);

public static partial class JobEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex SteamUser();

    public static void MapJobs(this WebApplication app)
    {
        var g = app.MapGroup("/api/jobs");
        g.MapGet("", List).RequirePermission(Permissions.ModsView);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.ModsView);
        // Answering or cancelling needs the job's own permission, checked in the handler (Ruling 14).
        g.MapPost("/{id:guid}/answer", Answer).RequirePermission(Permissions.ModsView);
        g.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(Permissions.ModsView);
        g.MapPost("/clear", Clear).RequirePermission(Permissions.ModsView);

        var steam = app.MapGroup("/api/steam");
        steam.MapGet("", async (AppSettingsService settings) => Results.Ok(new { username = await settings.SteamUsernameAsync() })).RequirePermission(Permissions.ModsView);
        steam.MapPut("", SetSteam).RequirePermission(Permissions.ModsInstall);
    }

    private static async Task<JobDto[]> List(AppDb db, bool? active) =>
        (await db.Jobs.Where(j => active != true || j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)
            .OrderByDescending(j => j.CreatedAt).Take(100).ToListAsync()).Select(j => JobDto.From(j)).ToArray();

    // Clear history (2026-11-21): the finished jobs of the kinds a page shows, only those this person may run. Open jobs always stay.
    private static async Task<IResult> Clear(ClearJobsBody body, AppDb db, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var types = (body.Types ?? []).Distinct().ToArray();
        if (types.Length == 0) return ApiError.Field("types", "Say which kinds of job to clear.");
        var userId = Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var mine = await permissions.IsOwnerAsync(userId) ? null : (await permissions.EffectiveAsync(userId)).ToArray();
        JobStatus[] finished = [JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Interrupted];
        var cleared = await db.Jobs.Where(j => types.Contains(j.Type) && finished.Contains(j.Status) && (mine == null || j.Permission == null || mine.Contains(j.Permission)))
            .ExecuteDeleteAsync();
        if (cleared > 0) await audit.RecordAsync("jobs.cleared", $"Cleared {cleared} finished job(s)", "job", null, new { types }, actorId: userId);
        return Results.Ok(new { cleared });
    }

    private static async Task<IResult> Get(Guid id, AppDb db) =>
        await db.Jobs.FindAsync(id) is { } job ? Results.Ok(JobDto.From(job, withLog: true)) : ApiError.Of(404, "not_found", "No such job.");

    private static async Task<IResult?> MayActAsync(Job job, HttpContext ctx, PermissionService permissions)
    {
        var userId = Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (job.Permission is null || await permissions.IsOwnerAsync(userId) || (await permissions.EffectiveAsync(userId)).Contains(job.Permission)) return null;
        return ApiError.Of(403, "forbidden", "You can't answer or cancel this job.");
    }

    // The answer goes to the agent and nowhere else: not the database, the audit log or any response (Review Focus 1).
    private static async Task<IResult> Answer(Guid id, AnswerBody body, AppDb db, AgentCalls agents, JobSecrets secrets, PermissionService permissions,
        AuditLog audit, IHubContext<UiHub> ui, HttpContext ctx)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null) return ApiError.Of(404, "not_found", "No such job.");
        if (await MayActAsync(job, ctx, permissions) is { } denied) return denied;
        if (job.Status != JobStatus.NeedsInput || job.Prompt is null or nameof(SteamPrompt.MobileConfirm))
            return ApiError.Of(409, "not_waiting", "That job isn't waiting for an answer.");
        var answer = body.Answer ?? "";
        if (answer.Length is 0 or > 256 || answer.Any(char.IsControl)) return ApiError.Field("answer", "Enter it on one line.");
        secrets.Remember(job.Id, answer);
        CommandResult r;
        try { r = await agents.CallAsync(job.AgentId!.Value, c => c.AnswerJob(job.Id, answer)); }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        catch (HubException e) { return ApiError.Of(400, "agent_refused", e.Message); }
        if (!r.Ok) return ApiError.Of(409, "not_waiting", r.Error ?? "The agent didn't take the answer.");
        (job.Status, job.Prompt) = (JobStatus.Running, null);
        await db.SaveChangesAsync();
        await audit.RecordAsync("job.answered", "Answered a Steam sign-in prompt", "job", job.Id.ToString());
        await ui.Clients.Group(JobService.Group).SendAsync("job", JobDto.From(job), Array.Empty<string>());
        return Results.Ok(JobDto.From(job));
    }

    private static async Task<IResult> Cancel(Guid id, AppDb db, AgentCalls agents, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null) return ApiError.Of(404, "not_found", "No such job.");
        if (await MayActAsync(job, ctx, permissions) is { } denied) return denied;
        if (!JobService.IsOpen(job.Status)) return ApiError.Of(409, "finished", "That job has already finished.");
        try { await agents.CallAsync(job.AgentId!.Value, c => c.CancelJob(job.Id)); }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        catch (HubException e) { return ApiError.Of(400, "agent_refused", e.Message); }
        await audit.RecordAsync("job.cancelled", $"Cancelled a {job.Type} job", "job", job.Id.ToString());
        return Results.Ok(JobDto.From(job)); // the agent's result marks it Cancelled
    }

    private static async Task<IResult> SetSteam(SteamBody body, AppSettingsService settings, AuditLog audit)
    {
        var username = (body.Username ?? "").Trim();
        if (username.Length > 0 && !SteamUser().IsMatch(username)) return ApiError.Field("username", "Steam account names use letters, digits and underscores.");
        await settings.SetSteamUsernameAsync(username);
        await audit.RecordAsync("steam.username", username.Length > 0 ? $"Set the Steam account to {username}" : "Cleared the Steam account", "settings", "steam");
        return Results.Ok(new { username = username.Length > 0 ? username : null });
    }
}
