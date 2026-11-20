using Microsoft.EntityFrameworkCore;
using Panel.Auth;
using Panel.Data;

namespace Panel.Features.Activity;

public record ActivityDto(Guid Id, DateTime At, string Actor, string Action, string Summary);
public record AuditDto(Guid Id, DateTime At, string? Actor, string Action, string? TargetType, string? TargetId, string Summary, string? DetailsJson, string? Ip);

public static class ActivityEndpoints
{
    public static void MapActivity(this WebApplication app)
    {
        app.MapGet("/api/activity", Activity).RequireAuthorization();
        app.MapGet("/api/audit", AuditLogQuery).RequirePermission(Permissions.AuditView);
    }

    private static IQueryable<(AuditEntry Entry, string? Actor)> WithActor(AppDb db, IQueryable<AuditEntry> source) =>
        from a in source
        join u in db.Users on a.ActorId equals u.Id into actors
        from u in actors.DefaultIfEmpty()
        select ValueTuple.Create(a, u != null ? u.UserName : null);

    private static async Task<ActivityDto[]> Activity(AppDb db, int? limit)
    {
        var rows = await WithActor(db, db.Audit.Where(a => a.IsPublic).OrderByDescending(a => a.At).Take(Math.Clamp(limit ?? 20, 1, 100))).ToListAsync();
        return rows.OrderByDescending(r => r.Entry.At)
            .Select(r => new ActivityDto(r.Entry.Id, r.Entry.At, r.Actor ?? "System", r.Entry.Action, r.Entry.Summary)).ToArray();
    }

    private static async Task<AuditDto[]> AuditLogQuery(AppDb db, string? action, Guid? actorId, DateTime? before, int? limit)
    {
        var q = db.Audit.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (actorId is { } id) q = q.Where(a => a.ActorId == id);
        if (before is { } b) { var utc = b.ToUniversalTime(); q = q.Where(a => a.At < utc); }
        var rows = await WithActor(db, q.OrderByDescending(a => a.At).Take(Math.Clamp(limit ?? 50, 1, 200))).ToListAsync();
        return rows.OrderByDescending(r => r.Entry.At).Select(r => new AuditDto(r.Entry.Id, r.Entry.At, r.Actor, r.Entry.Action,
            r.Entry.TargetType, r.Entry.TargetId, r.Entry.Summary, r.Entry.DetailsJson, r.Entry.Ip)).ToArray();
    }
}
