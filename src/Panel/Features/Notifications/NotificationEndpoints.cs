using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Data;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 11: a member's own notifications. Every query filters on the caller, so another member's row is simply not found.
public static class NotificationEndpoints
{
    public sealed record NotificationDto(Guid Id, string Type, string Text, string? Link, DateTime CreatedAt, DateTime? ReadAt);
    public sealed record FeedDto(int Unread, NotificationDto[] Items);

    public static void MapNotifications(this WebApplication app)
    {
        var g = app.MapGroup("/api/notifications").RequireAuthorization();
        g.MapGet("", Feed);
        g.MapPost("/{id:guid}/read", Read);
        g.MapPost("/read-all", ReadAll);
    }

    private static Guid Me(HttpContext ctx) => Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<FeedDto> Feed(AppDb db, HttpContext ctx, int? limit)
    {
        var me = Me(ctx);
        var mine = db.UserNotifications.Where(n => n.UserId == me);
        var items = await mine.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(Math.Clamp(limit ?? 30, 1, 100))
            .Select(n => new NotificationDto(n.Id, n.EventType, n.Text, n.Link, n.CreatedAt, n.ReadAt)).ToArrayAsync();
        return new FeedDto(await mine.CountAsync(n => n.ReadAt == null), items);
    }

    private static async Task<IResult> Read(Guid id, AppDb db, HttpContext ctx)
    {
        var me = Me(ctx);
        if (await db.UserNotifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == me) is not { } n)
            return ApiError.Of(404, "not_found", "No such notification.");
        n.ReadAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> ReadAll(AppDb db, HttpContext ctx)
    {
        var me = Me(ctx);
        var now = DateTime.UtcNow;
        await db.UserNotifications.Where(n => n.UserId == me && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
        return Results.NoContent();
    }
}
