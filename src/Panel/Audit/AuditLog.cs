using System.Security.Claims;
using System.Text.Json;
using Panel.Data;

namespace Panel.Audit;

public sealed class AuditLog(AppDb db, IHttpContextAccessor http)
{
    public async Task RecordAsync(string action, string summary, string? targetType = null, string? targetId = null,
        object? details = null, bool isPublic = false, Guid? actorId = null)
    {
        var ctx = http.HttpContext;
        actorId ??= Guid.TryParse(ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        db.Audit.Add(new AuditEntry
        {
            ActorId = actorId,
            Action = action,
            Summary = summary.Length > 300 ? summary[..300] : summary,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
            IsPublic = isPublic,
        });
        await db.SaveChangesAsync();
    }
}
