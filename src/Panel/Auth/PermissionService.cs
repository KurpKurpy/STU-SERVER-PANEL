using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Auth;

// Effective permissions are read from the database on every request, so role and override
// changes apply to the very next request. ponytail: per-request DB read; cache with invalidation if it ever shows in profiling.
public sealed class PermissionService(AppDb db, IHttpContextAccessor http)
{
    private static readonly HashSet<string> Known = Permissions.All.Select(p => p.Key).ToHashSet();

    private static string CacheKey(Guid userId) => "perms:" + userId;

    public async Task<IReadOnlySet<string>> EffectiveAsync(Guid userId)
    {
        if (http.HttpContext?.Items[CacheKey(userId)] is IReadOnlySet<string> cached) return cached;

        var roleNames = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
                               where ur.UserId == userId select r.Name!).ToListAsync();
        var overrides = await db.UserClaims
            .Where(c => c.UserId == userId && (c.ClaimType == Permissions.ClaimGrant || c.ClaimType == Permissions.ClaimDeny))
            .Select(c => new { c.ClaimType, c.ClaimValue }).ToListAsync();
        var set = await ComputeAsync(roleNames,
            overrides.Where(o => o.ClaimType == Permissions.ClaimGrant).Select(o => o.ClaimValue!),
            overrides.Where(o => o.ClaimType == Permissions.ClaimDeny).Select(o => o.ClaimValue!));

        if (http.HttpContext is { } ctx) ctx.Items[CacheKey(userId)] = set;
        return set;
    }

    // What a member would be able to do with these roles and overrides (used to check a change before applying it).
    public async Task<HashSet<string>> ComputeAsync(IEnumerable<string> roleNames, IEnumerable<string> grants, IEnumerable<string> denies)
    {
        var names = roleNames.ToList();
        if (names.Contains(Permissions.OwnerRole)) return [.. Known];
        var set = (await db.RoleClaims
            .Where(c => c.ClaimType == Permissions.ClaimPerm && db.Roles.Any(r => r.Id == c.RoleId && names.Contains(r.Name!)))
            .Select(c => c.ClaimValue!).ToListAsync()).ToHashSet();
        set.UnionWith(grants);
        set.ExceptWith(denies);
        set.IntersectWith(Known);
        return set;
    }

    public void Forget(Guid userId) => http.HttpContext?.Items.Remove(CacheKey(userId));

    public Task<bool> IsOwnerAsync(Guid userId) =>
        db.UserRoles.AnyAsync(ur => ur.UserId == userId && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Permissions.OwnerRole));

    // Active (not disabled) Owners. Lockout is compared in memory: EF's SQLite provider can't compare DateTimeOffset.
    public async Task<int> OwnerCountAsync()
    {
        var lockouts = await (from ur in db.UserRoles
                              join r in db.Roles on ur.RoleId equals r.Id
                              join u in db.Users on ur.UserId equals u.Id
                              where r.Name == Permissions.OwnerRole
                              select u.LockoutEnd).ToListAsync();
        return lockouts.Count(end => end is not { Year: >= 9999 });
    }
}
