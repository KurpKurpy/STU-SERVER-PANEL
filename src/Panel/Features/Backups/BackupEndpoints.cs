using System.Security.Claims;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Panel.Api;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Files;
using Panel.Features.Jobs;
using Panel.Hubs;
using Panel.Settings;

namespace Panel.Features.Backups;

// Phase 7b: the Backups page. Everything needs backups.manage: the zips hold the server passwords, the console's keys and its database.
public static class BackupEndpoints
{
    public sealed record BackupDto(Guid Id, string Type, string Kind, Guid? AgentId, string? AgentName, string? FileName, long SizeBytes, string Status,
        string? Error, string? Note, string? CreatedBy, DateTime CreatedAt, DateTime? FinishedAt);
    public sealed record StartBody(string? Type, Guid? AgentId);
    public sealed record RestoreBody(string[]? Parts);
    public sealed record SettingsDto(int KeepScheduled, int KeepPreUpdate, int SafetyCopyDays);
    public sealed record AgentName(Guid Id, string Name);
    public sealed record SettingsView(int KeepScheduled, int KeepPreUpdate, int SafetyCopyDays, string? ConsoleCopyTo, AgentName[] Agents);

    public static void MapBackups(this WebApplication app)
    {
        var g = app.MapGroup("/api/backups").RequirePermission(Permissions.BackupsManage);
        g.MapGet("", List);
        g.MapPost("", Start);
        g.MapGet("/{id:guid}/download", Download);
        g.MapDelete("/{id:guid}", Delete);
        g.MapPost("/{id:guid}/restore", Restore);
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", PutSettings);
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<IResult> List(AppDb db)
    {
        var agents = await db.Agents.ToDictionaryAsync(a => a.Id, a => a.Name);
        var users = await db.Users.ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName);
        var rows = await db.Backups.OrderByDescending(b => b.CreatedAt).Take(500).ToListAsync();
        return Results.Ok(rows.Select(b => new BackupDto(b.Id, b.Type.ToString(), b.Kind.ToString(), b.AgentId,
            b.AgentId is { } a && agents.TryGetValue(a, out var an) ? an : null, b.FileName, b.SizeBytes, b.Status.ToString(), b.Error, b.Note,
            b.CreatedById is { } u && users.TryGetValue(u, out var un) ? un : null, b.CreatedAt, b.FinishedAt)));
    }

    private static async Task<IResult> Start(StartBody body, AppDb db, BackupService backups, HttpContext ctx)
    {
        if (!Enum.TryParse<BackupType>(body.Type, ignoreCase: true, out var type)) return ApiError.Field("type", "Back up the console or a server.");
        if (type == BackupType.Server && (body.AgentId is not { } agentId || await db.Agents.FindAsync(agentId) is null))
            return ApiError.Field("agentId", "Pick the server agent to back up.");
        var row = await backups.StartAsync(type, BackupKind.Manual, body.AgentId, UserId(ctx));
        return Results.Accepted($"/api/backups/{row.Id}", new { id = row.Id, status = row.Status.ToString() });
    }

    private static async Task<IResult> Download(Guid id, AppDb db, ConsoleBackups console, AgentCalls agents, HttpContext ctx)
    {
        if (await db.Backups.FindAsync(id) is not { Status: BackupStatus.Done, FileName: { } name } b || !BackupService.IsBackupName(name))
            return ApiError.Of(404, "not_found", "No finished backup with that id.");
        if (b.Type == BackupType.Server) return await FileEndpoints.StreamAsync(agents, b.AgentId!.Value, new FileSpace("backups", null), "server/" + name, ctx);
        var path = Path.Combine(console.Folder, name);
        if (!File.Exists(path)) return ApiError.Of(404, "missing", "That backup's file isn't on the box any more.");
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = name }.ToString();
        return Results.File(path, "application/octet-stream");
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, BackupService backups, HttpContext ctx)
    {
        if (await db.Backups.FindAsync(id) is not { } b) return ApiError.Of(404, "not_found", "No such backup.");
        return await backups.DeleteAsync(b, UserId(ctx)) ?? Results.NoContent();
    }

    private static async Task<IResult> Restore(Guid id, RestoreBody body, AppDb db, BackupService backups, HttpContext ctx)
    {
        if (await db.Backups.FindAsync(id) is not { } b) return ApiError.Of(404, "not_found", "No such backup.");
        var (job, error) = await backups.RestoreAsync(b, body.Parts, UserId(ctx));
        return error ?? Results.Accepted($"/api/jobs/{job!.Id}", JobDto.From(job));
    }

    // The agents are for "Back up now" (agents.manage isn't needed just to pick one).
    private static async Task<IResult> GetSettings(AppSettingsService settings, ConsoleBackups console, AppDb db) =>
        Results.Ok(new SettingsView(await settings.IntAsync(AppSettingsService.BackupKeepScheduledKey, 14),
            await settings.IntAsync(AppSettingsService.BackupKeepPreUpdateKey, 10), await settings.IntAsync(AppSettingsService.SafetyCopyDaysKey, 30), console.CopyTo,
            await db.Agents.OrderBy(a => a.Name).Select(a => new AgentName(a.Id, a.Name)).ToArrayAsync()));

    private static async Task<IResult> PutSettings(SettingsDto body, AppSettingsService settings, Panel.Audit.AuditLog audit)
    {
        if (body.KeepScheduled is < 1 or > 365) return ApiError.Field("keepScheduled", "Keep 1 to 365 scheduled backups.");
        if (body.KeepPreUpdate is < 1 or > 100) return ApiError.Field("keepPreUpdate", "Keep 1 to 100 pre-update backups.");
        if (body.SafetyCopyDays is < 0 or > 3650) return ApiError.Field("safetyCopyDays", "Safety copies: 0 (keep forever) to 3650 days.");
        await settings.SetAsync(AppSettingsService.BackupKeepScheduledKey, body.KeepScheduled.ToString());
        await settings.SetAsync(AppSettingsService.BackupKeepPreUpdateKey, body.KeepPreUpdate.ToString());
        await settings.SetAsync(AppSettingsService.SafetyCopyDaysKey, body.SafetyCopyDays.ToString());
        await audit.RecordAsync("backup.settings", "Changed the backup keep-counts", "settings", null, new { body.KeepScheduled, body.KeepPreUpdate, body.SafetyCopyDays });
        return Results.Ok(body);
    }
}
