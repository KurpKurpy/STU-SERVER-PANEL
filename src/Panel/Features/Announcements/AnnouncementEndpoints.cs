using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;

namespace Panel.Features.Announcements;

public record AnnouncementDto(Guid Id, string Title, string Body, bool Pinned, string Author, DateTime CreatedAt, DateTime? UpdatedAt);
public record AnnouncementUpsert(string Title, string Body, bool Pinned);

public static class AnnouncementEndpoints
{
    public static void MapAnnouncements(this WebApplication app)
    {
        var g = app.MapGroup("/api/announcements");
        g.MapGet("", List).RequireAuthorization();
        g.MapPost("", Create).RequirePermission(Permissions.AnnouncementsManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.AnnouncementsManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.AnnouncementsManage);
    }

    private static IQueryable<AnnouncementDto> Query(AppDb db, Guid? id = null) =>
        from a in db.Announcements
        where id == null || a.Id == id
        join u in db.Users on a.CreatedById equals u.Id into authors
        from u in authors.DefaultIfEmpty()
        orderby a.Pinned descending, a.CreatedAt descending
        select new AnnouncementDto(a.Id, a.Title, a.Body, a.Pinned, u != null ? u.UserName! : "Former member", a.CreatedAt, a.UpdatedAt);

    private static Task<AnnouncementDto[]> List(AppDb db) => Query(db).Take(20).ToArrayAsync();

    private static IResult? Validate(AnnouncementUpsert r) =>
        string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 120 ? ApiError.Of(400, "validation", "Title must be 1–120 characters.")
        : string.IsNullOrWhiteSpace(r.Body) || r.Body.Trim().Length > 4000 ? ApiError.Of(400, "validation", "Text must be 1–4000 characters.")
        : null;

    private static async Task<IResult> Create(AnnouncementUpsert r, AppDb db, ClaimsPrincipal me, AuditLog audit)
    {
        if (Validate(r) is { } invalid) return invalid;
        var a = new Announcement { Title = r.Title.Trim(), Body = r.Body.Trim(), Pinned = r.Pinned, CreatedById = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!) };
        db.Announcements.Add(a);
        await db.SaveChangesAsync();
        await audit.RecordAsync("announcement.created", $"Posted “{a.Title}”", "announcement", a.Id.ToString(), isPublic: true);
        return Results.Ok(await Query(db, a.Id).FirstAsync());
    }

    private static async Task<IResult> Update(Guid id, AnnouncementUpsert r, AppDb db, AuditLog audit)
    {
        var a = await db.Announcements.FindAsync(id);
        if (a is null) return ApiError.Of(404, "not_found", "No such announcement.");
        if (Validate(r) is { } invalid) return invalid;
        (a.Title, a.Body, a.Pinned, a.UpdatedAt) = (r.Title.Trim(), r.Body.Trim(), r.Pinned, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await audit.RecordAsync("announcement.updated", $"Edited “{a.Title}”", "announcement", id.ToString());
        return Results.Ok(await Query(db, id).FirstAsync());
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AuditLog audit)
    {
        var a = await db.Announcements.FindAsync(id);
        if (a is null) return ApiError.Of(404, "not_found", "No such announcement.");
        db.Announcements.Remove(a);
        await db.SaveChangesAsync();
        await audit.RecordAsync("announcement.deleted", $"Deleted “{a.Title}”", "announcement", id.ToString());
        return Results.NoContent();
    }
}
