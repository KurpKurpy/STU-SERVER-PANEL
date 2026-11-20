using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;

namespace Panel.Features.Roles;

public record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsOwner, string[] Permissions, int MemberCount);
public record RoleUpsert(string Name, string? Description, string[] Permissions);

public static class RoleEndpoints
{
    public static void MapRoles(this WebApplication app)
    {
        app.MapGet("/api/permissions", () => Permissions.All).RequireAuthorization();

        var roles = app.MapGroup("/api/roles");
        roles.MapGet("", List).RequirePermission(Permissions.MembersView);
        roles.MapPost("", Create).RequirePermission(Permissions.RolesManage);
        roles.MapPut("/{id:guid}", Update).RequirePermission(Permissions.RolesManage);
        roles.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.RolesManage);
    }

    private static async Task<RoleDto[]> List(AppDb db)
    {
        var claims = await db.RoleClaims.Where(c => c.ClaimType == Permissions.ClaimPerm).ToListAsync();
        var counts = await db.UserRoles.GroupBy(ur => ur.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return (await db.Roles.OrderBy(r => r.Name).ToListAsync()).Select(r => new RoleDto(
            r.Id, r.Name!, r.Description, r.IsSystem, r.Name == Permissions.OwnerRole,
            claims.Where(c => c.RoleId == r.Id).Select(c => c.ClaimValue!).Order().ToArray(),
            counts.GetValueOrDefault(r.Id))).ToArray();
    }

    private static IResult? Validate(RoleUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 40) return ApiError.Of(400, "validation", "Role name must be 1–40 characters.");
        var unknown = r.Permissions.Where(p => !Permissions.Exists(p)).ToArray();
        return unknown.Length > 0 ? ApiError.Of(400, "validation", $"Unknown permissions: {string.Join(", ", unknown)}") : null;
    }

    private static async Task SetPermissions(RoleManager<Role> roles, Role role, IEnumerable<string> wanted)
    {
        var current = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimPerm).ToList();
        var target = wanted.ToHashSet();
        foreach (var c in current.Where(c => !target.Contains(c.Value))) await roles.RemoveClaimAsync(role, c);
        foreach (var p in target.Where(p => current.All(c => c.Value != p))) await roles.AddClaimAsync(role, new Claim(Permissions.ClaimPerm, p));
    }

    // Non-owners can't give a role access they don't hold themselves (they could then hold that role).
    private static async Task<IResult?> Guard(ClaimsPrincipal me, PermissionService perms, IEnumerable<string> added)
    {
        var callerId = Guid.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (await perms.IsOwnerAsync(callerId)) return null;
        var mine = await perms.EffectiveAsync(callerId);
        return added.Any(p => !mine.Contains(p)) ? ApiError.Of(403, "escalation", "You can't give a role access you don't have yourself.") : null;
    }

    private static async Task<IResult> Create(RoleUpsert r, RoleManager<Role> roles, AppDb db, AuditLog audit, ClaimsPrincipal me, PermissionService perms)
    {
        if (Validate(r) is { } invalid) return invalid;
        if (await Guard(me, perms, r.Permissions) is { } denied) return denied;
        var name = r.Name.Trim();
        if (await roles.RoleExistsAsync(name)) return ApiError.Of(409, "conflict_role", $"A role called {name} already exists.");
        var role = new Role(name) { Description = r.Description?.Trim() };
        await roles.CreateAsync(role);
        await SetPermissions(roles, role, r.Permissions);
        await audit.RecordAsync("role.created", $"Created role {name}", "role", role.Id.ToString(), r.Permissions);
        return Results.Ok((await List(db)).First(x => x.Id == role.Id));
    }

    private static async Task<IResult> Update(Guid id, RoleUpsert r, RoleManager<Role> roles, AppDb db, AuditLog audit, ClaimsPrincipal me, PermissionService perms)
    {
        var role = await roles.FindByIdAsync(id.ToString());
        if (role is null) return ApiError.Of(404, "not_found", "No such role.");
        if (role.Name == Permissions.OwnerRole) return ApiError.Of(400, "owner_role_fixed", "The Owner role always has every permission and can't be edited.");
        if (Validate(r) is { } invalid) return invalid;
        var existing = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimPerm).Select(c => c.Value);
        if (await Guard(me, perms, r.Permissions.Except(existing)) is { } denied) return denied;
        var name = r.Name.Trim();
        if (role.IsSystem && name != role.Name) return ApiError.Of(400, "system_role", "Built-in roles can't be renamed.");
        if (name != role.Name && await roles.RoleExistsAsync(name)) return ApiError.Of(409, "conflict_role", $"A role called {name} already exists.");
        role.Name = name;
        role.Description = r.Description?.Trim();
        await roles.UpdateAsync(role);
        await SetPermissions(roles, role, r.Permissions);
        await audit.RecordAsync("role.updated", $"Updated role {name}", "role", role.Id.ToString(), r.Permissions);
        return Results.Ok((await List(db)).First(x => x.Id == role.Id));
    }

    private static async Task<IResult> Delete(Guid id, RoleManager<Role> roles, AppDb db, AuditLog audit)
    {
        var role = await roles.FindByIdAsync(id.ToString());
        if (role is null) return ApiError.Of(404, "not_found", "No such role.");
        if (role.IsSystem) return ApiError.Of(400, "system_role", "Built-in roles can't be deleted.");
        db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.RoleId == id));
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        await db.Invites.Where(i => i.RoleId == id && i.UsedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now));
        await roles.DeleteAsync(role);
        await audit.RecordAsync("role.deleted", $"Deleted role {role.Name}", "role", id.ToString());
        return Results.NoContent();
    }
}
