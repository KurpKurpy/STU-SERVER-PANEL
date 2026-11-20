using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;

namespace Panel.Features.Invites;

public record InviteCreate(Guid RoleId, int? ExpiresHours);
public record InviteCreated(Guid Id, string Url, DateTime ExpiresAt);
public record InviteDto(Guid Id, string RoleName, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt);
public record InviteInfo(bool Valid, string? Reason, string? RoleName, DateTime? ExpiresAt);
public record InviteAccept(string UserName, string DisplayName, string Password);

public static class InviteEndpoints
{
    public static string Origin(HttpRequest r) => $"{r.Scheme}://{r.Host}";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static void MapInvites(this WebApplication app)
    {
        var g = app.MapGroup("/api/invites");
        g.MapPost("", Create).RequirePermission(Permissions.MembersManage);
        g.MapGet("", Pending).RequirePermission(Permissions.MembersManage);
        g.MapDelete("/{id:guid}", Revoke).RequirePermission(Permissions.MembersManage);
        g.MapGet("/{token}", Info);
        g.MapPost("/{token}/accept", Accept).RequireRateLimiting("auth");
    }

    private static async Task<IResult> Create(InviteCreate r, AppDb db, ClaimsPrincipal me, HttpRequest req, AuditLog audit, PermissionService perms)
    {
        var role = await db.Roles.FindAsync(r.RoleId);
        if (role is null) return ApiError.Of(400, "validation", "Pick a role for the invite.");

        // Same escalation rule as editing members: only Owners invite Owners, others only to roles within their own access.
        var callerId = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await perms.IsOwnerAsync(callerId))
        {
            var callerPerms = await perms.EffectiveAsync(callerId);
            var rolePerms = await db.RoleClaims.Where(c => c.RoleId == role.Id && c.ClaimType == Permissions.ClaimPerm).Select(c => c.ClaimValue!).ToListAsync();
            if (role.Name == Permissions.OwnerRole || rolePerms.Any(p => !callerPerms.Contains(p)))
                return ApiError.Of(403, "escalation", "You can't invite someone to a role with more access than you have.");
        }
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var invite = new Invite
        {
            TokenHash = Hash(token),
            RoleId = role.Id,
            CreatedById = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!),
            ExpiresAt = DateTime.UtcNow.AddHours(Math.Clamp(r.ExpiresHours ?? 72, 1, 168)),
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
        await audit.RecordAsync("member.invited", $"Created an invite for role {role.Name}", "invite", invite.Id.ToString());
        return Results.Ok(new InviteCreated(invite.Id, $"{Origin(req)}/invite/{token}", invite.ExpiresAt));
    }

    private static async Task<InviteDto[]> Pending(AppDb db)
    {
        var now = DateTime.UtcNow;
        return await (from i in db.Invites
                      where i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now
                      join r in db.Roles on i.RoleId equals r.Id
                      join u in db.Users on i.CreatedById equals u.Id
                      orderby i.CreatedAt descending
                      select new InviteDto(i.Id, r.Name!, u.UserName!, i.CreatedAt, i.ExpiresAt)).ToArrayAsync();
    }

    private static async Task<IResult> Revoke(Guid id, AppDb db, AuditLog audit)
    {
        var invite = await db.Invites.FindAsync(id);
        if (invite is null) return ApiError.Of(404, "not_found", "No such invite.");
        invite.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("member.invite_revoked", "Revoked an invite", "invite", id.ToString());
        return Results.NoContent();
    }

    private static async Task<(Invite? Invite, string? Reason)> Find(AppDb db, string token)
    {
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.TokenHash == Hash(token));
        var reason = invite switch
        {
            null => "unknown",
            { RevokedAt: not null } => "revoked",
            { UsedAt: not null } => "used",
            _ when invite.ExpiresAt <= DateTime.UtcNow => "expired",
            _ => null,
        };
        // An invite to a role that no longer exists can never be accepted, so it is reported as cancelled.
        if (reason is null && !await db.Roles.AnyAsync(r => r.Id == invite!.RoleId)) reason = "revoked";
        return (invite, reason);
    }

    private static async Task<InviteInfo> Info(string token, AppDb db)
    {
        var (invite, reason) = await Find(db, token);
        if (reason is not null) return new InviteInfo(false, reason, null, null);
        var role = await db.Roles.FindAsync(invite!.RoleId);
        return new InviteInfo(true, null, role?.Name, invite.ExpiresAt);
    }

    private static async Task<IResult> Accept(string token, InviteAccept r, AppDb db, UserManager<User> users,
        SignInManager<User> signIn, AuditLog audit)
    {
        var (invite, reason) = await Find(db, token);
        var role = invite is null ? null : await db.Roles.FindAsync(invite.RoleId);
        if (reason is not null || role is null) return ApiError.Of(410, "invite_invalid", "This invite can no longer be used.");

        var name = (r.UserName ?? "").Trim();
        if (await users.FindByNameAsync(name) is not null)
            return ApiError.Of(409, "conflict_username", $"The callsign {name} is already taken.");
        var displayName = (r.DisplayName ?? "").Trim();
        if (displayName.Length is 0 or > 64) return ApiError.Of(400, "validation", "Display name must be 1–64 characters.");

        var user = new User { UserName = name, DisplayName = displayName, LastLoginAt = DateTime.UtcNow };
        var created = await users.CreateAsync(user, r.Password ?? "");
        if (!created.Succeeded)
            return created.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName))
                ? ApiError.Of(409, "conflict_username", $"The callsign {name} is already taken.")
                : AuthEndpoints.ValidationError(created);
        await users.AddToRoleAsync(user, role.Name!);

        invite!.UsedAt = DateTime.UtcNow;
        invite.UsedById = user.Id;
        await db.SaveChangesAsync();
        await signIn.SignInAsync(user, isPersistent: true);
        await audit.RecordAsync("member.joined", $"{name} joined as {role.Name}", "user", user.Id.ToString(), isPublic: true, actorId: user.Id);
        return Results.Ok();
    }
}
