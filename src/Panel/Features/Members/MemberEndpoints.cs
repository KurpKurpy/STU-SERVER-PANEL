using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Features.Invites;

namespace Panel.Features.Members;

public record MemberDto(Guid Id, string UserName, string DisplayName, string? Rank, string? Section, string[] Roles,
    bool TwoFactorEnabled, DateTime? LastLoginAt, bool Disabled);
public record MemberDetailDto(MemberDto Member, string[] Grants, string[] Denies, string[] Effective);
public record MemberPatch(string? DisplayName, string? Rank, string? Section, string[]? Roles, string[]? Grants, string[]? Denies, bool? Disabled);
public record ResetLink(string Url, DateTime ExpiresAt);

public static class MemberEndpoints
{
    public static void MapMembers(this WebApplication app)
    {
        var g = app.MapGroup("/api/members");
        g.MapGet("", List).RequirePermission(Permissions.MembersView);
        g.MapGet("/{id:guid}", Detail).RequirePermission(Permissions.MembersView);
        g.MapPatch("/{id:guid}", Patch).RequirePermission(Permissions.MembersManage);
        g.MapPost("/{id:guid}/reset-link", Reset).RequirePermission(Permissions.MembersManage);
    }

    private static async Task<MemberDto[]> Load(AppDb db, Guid? only = null)
    {
        var users = await db.Users.Where(u => only == null || u.Id == only).OrderBy(u => u.UserName).ToListAsync();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
        return users.Select(u => new MemberDto(u.Id, u.UserName!, u.DisplayName, u.Rank, u.Section,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).Order().ToArray(),
            u.TwoFactorEnabled, u.LastLoginAt, AuthEndpoints.IsDisabled(u))).ToArray();
    }

    private static Task<MemberDto[]> List(AppDb db) => Load(db);

    private static async Task<IResult> Detail(Guid id, AppDb db, PermissionService perms)
    {
        var member = (await Load(db, id)).SingleOrDefault();
        if (member is null) return ApiError.Of(404, "not_found", "No such member.");
        var claims = await db.UserClaims.Where(c => c.UserId == id).ToListAsync();
        string[] Of(string type) => claims.Where(c => c.ClaimType == type).Select(c => c.ClaimValue!).Order().ToArray();
        return Results.Ok(new MemberDetailDto(member, Of(Permissions.ClaimGrant), Of(Permissions.ClaimDeny),
            (await perms.EffectiveAsync(id)).Order().ToArray()));
    }

    private static async Task<IResult> Patch(Guid id, MemberPatch r, ClaimsPrincipal me, UserManager<User> users, AppDb db,
        PermissionService perms, AuditLog audit)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return ApiError.Of(404, "not_found", "No such member.");
        var callerId = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var callerIsOwner = await perms.IsOwnerAsync(callerId);
        var callerPerms = await perms.EffectiveAsync(callerId);
        var targetIsOwner = await perms.IsOwnerAsync(id);
        var currentRoles = await users.GetRolesAsync(user);

        if (r.Roles is not null)
        {
            var unknown = r.Roles.Where(n => !db.Roles.Any(x => x.Name == n)).ToArray();
            if (unknown.Length > 0) return ApiError.Of(400, "validation", $"Unknown roles: {string.Join(", ", unknown)}");
        }
        foreach (var key in (r.Grants ?? []).Concat(r.Denies ?? []))
            if (!Permissions.Exists(key)) return ApiError.Of(400, "validation", $"Unknown permission {key}.");
        if (r.Grants is not null && r.Denies is not null && r.Grants.Intersect(r.Denies).Any())
            return ApiError.Of(400, "validation", "A permission can't be both granted and denied.");

        // Escalation guard: only Owners touch Owners or the Owner role. Anyone else may only make changes whose
        // newly added access (target before vs after, so lifting a deny counts) is access they hold themselves.
        if (!callerIsOwner)
        {
            if (targetIsOwner || (r.Roles?.Contains(Permissions.OwnerRole) ?? false)) return Escalation();
            var claims = await users.GetClaimsAsync(user);
            string[] Current(string type) => claims.Where(c => c.Type == type).Select(c => c.Value).ToArray();
            var before = await perms.EffectiveAsync(id);
            var after = await perms.ComputeAsync(r.Roles ?? currentRoles, r.Grants ?? Current(Permissions.ClaimGrant), r.Denies ?? Current(Permissions.ClaimDeny));
            if (after.Except(before).Any(p => !callerPerms.Contains(p))) return Escalation();
        }

        var losingOwner = targetIsOwner && ((r.Roles is not null && !r.Roles.Contains(Permissions.OwnerRole)) || r.Disabled == true);
        if (losingOwner && await perms.OwnerCountAsync() <= 1)
            return ApiError.Of(409, "last_owner", "The unit needs at least one Owner.");

        if (r.DisplayName is not null)
        {
            var dn = r.DisplayName.Trim();
            if (dn.Length is 0 or > 64) return ApiError.Of(400, "validation", "Display name must be 1–64 characters.");
            user.DisplayName = dn;
        }
        if (r.Rank is not null) user.Rank = string.IsNullOrWhiteSpace(r.Rank) ? null : r.Rank.Trim();
        if (r.Section is not null) user.Section = string.IsNullOrWhiteSpace(r.Section) ? null : r.Section.Trim();
        await users.UpdateAsync(user);

        if (r.Roles is not null)
        {
            await users.RemoveFromRolesAsync(user, currentRoles.Except(r.Roles));
            await users.AddToRolesAsync(user, r.Roles.Except(currentRoles));
        }
        await ReplaceClaims(users, user, Permissions.ClaimGrant, r.Grants);
        await ReplaceClaims(users, user, Permissions.ClaimDeny, r.Denies);

        if (r.Disabled is { } disabled && disabled != AuthEndpoints.IsDisabled(user))
        {
            await users.SetLockoutEndDateAsync(user, disabled ? DateTimeOffset.MaxValue : null);
            if (disabled) await users.UpdateSecurityStampAsync(user);
            else await users.ResetAccessFailedCountAsync(user);
            await audit.RecordAsync(disabled ? "member.disabled" : "member.enabled", $"{(disabled ? "Disabled" : "Enabled")} {user.UserName}", "user", id.ToString());
        }
        await audit.RecordAsync("member.updated", $"Updated {user.UserName}", "user", id.ToString(), r);
        perms.Forget(id);
        return await Detail(id, db, perms);
    }

    private static IResult Escalation() =>
        ApiError.Of(403, "escalation", "You can't give out or take over access you don't have yourself.");

    private static async Task ReplaceClaims(UserManager<User> users, User user, string type, string[]? wanted)
    {
        if (wanted is null) return;
        var current = (await users.GetClaimsAsync(user)).Where(c => c.Type == type).ToList();
        if (current.Count > 0) await users.RemoveClaimsAsync(user, current);
        if (wanted.Length > 0) await users.AddClaimsAsync(user, wanted.Distinct().Select(v => new Claim(type, v)));
    }

    private static async Task<IResult> Reset(Guid id, ClaimsPrincipal me, UserManager<User> users, HttpRequest req,
        IOptions<DataProtectionTokenProviderOptions> tokenOptions, AuditLog audit, PermissionService perms)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return ApiError.Of(404, "not_found", "No such member.");

        // A reset link is a way into the account, so it follows the escalation rule: no links for Owners or
        // for anyone holding access the caller lacks, unless the caller is an Owner.
        var callerId = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await perms.IsOwnerAsync(callerId)
            && (await perms.IsOwnerAsync(id) || (await perms.EffectiveAsync(id)).Except(await perms.EffectiveAsync(callerId)).Any()))
            return Escalation();
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
        await audit.RecordAsync("member.reset_link", $"Created a password reset link for {user.UserName}", "user", id.ToString());
        return Results.Ok(new ResetLink($"{InviteEndpoints.Origin(req)}/reset?u={id}&t={token}", DateTime.UtcNow + tokenOptions.Value.TokenLifespan));
    }
}
