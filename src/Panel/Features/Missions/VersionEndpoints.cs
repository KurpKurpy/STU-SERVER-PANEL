using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Storage;

namespace Panel.Features.Missions;

public record SaveVersionBody(string? Note);
public record AddonDto(string Addon, string? ModName, string? ModUrl);
public record VersionDto(int Number, string? Note, DateTime CreatedAt, string? CreatedBy, long PboSize, bool Binarized, int Files, bool Current,
    bool HasOriginal, AddonDto[] Addons);
public record ChangedPath(string Path, string ASha, string BSha);

// Versions: save, list, compare, restore, open in the working copy, downloads (Phase 4a Task 6, Ruling 8).
public static class VersionEndpoints
{
    public static void MapVersions(this WebApplication app)
    {
        var g = app.MapGroup("/api/missions/{id:guid}/versions");
        g.MapGet("", List).RequirePermission(Permissions.MissionsView);
        g.MapPost("", Save).RequirePermission(Permissions.MissionsView); // edit right checked inside
        g.MapGet("/compare", Compare).RequirePermission(Permissions.MissionsView);
        g.MapGet("/{n:int}/file", File).RequirePermission(Permissions.MissionsView);
        g.MapGet("/{n:int}/pbo", Pbo).RequirePermission(Permissions.MissionsDownload);
        g.MapGet("/{n:int}/original", Original).RequirePermission(Permissions.MissionsDownload);
        g.MapPost("/{n:int}/restore", Restore).RequirePermission(Permissions.MissionsView);
        g.MapPost("/{n:int}/open", Open).RequirePermission(Permissions.MissionsView);
    }

    private static IResult NoVersion() => ApiError.Of(404, "not_found", "No such version.");

    private static Task<MissionVersion?> FindAsync(AppDb db, Guid id, int n) => db.MissionVersions.FirstOrDefaultAsync(v => v.MissionId == id && v.Number == n);

    private static async Task<IResult> List(Guid id, AppDb db)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return ApiError.Of(404, "not_found", "No such mission.");
        var versions = await db.MissionVersions.Where(v => v.MissionId == id).OrderByDescending(v => v.Number).ToListAsync();
        var ids = versions.Select(v => v.Id).ToList();
        var addons = (await db.MissionVersionAddons.Where(a => ids.Contains(a.VersionId)).ToListAsync()).ToLookup(a => a.VersionId);
        var counts = await db.MissionVersionFiles.Where(f => ids.Contains(f.VersionId)).GroupBy(f => f.VersionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        var userIds = versions.Select(v => v.CreatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!);
        return Results.Ok(versions.Select(v => new VersionDto(v.Number, v.Note, v.CreatedAt, v.CreatedById is { } u ? names.GetValueOrDefault(u) : null, v.PboSize,
            v.Binarized, counts.GetValueOrDefault(v.Id), v.Id == m.CurrentVersionId, v.OriginalUploadSha256 is not null,
            addons[v.Id].OrderBy(a => a.Addon, StringComparer.OrdinalIgnoreCase).Select(a => new AddonDto(a.Addon, a.ModName, a.ModUrl)).ToArray())));
    }

    private static async Task<IResult> Save(Guid id, SaveVersionBody body, AppDb db, MissionVersions versions, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return ApiError.Of(404, "not_found", "No such mission.");
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        if (body.Note is { Length: > 500 }) return ApiError.Field("note", "The note is at most 500 characters.");
        MissionVersion v;
        try { v = await versions.SaveAsync(m, body.Note, MissionAccess.UserId(ctx)); }
        catch (MissionException e) { return ApiError.Of(409, "invalid_mission", e.Message); }
        await audit.RecordAsync("mission.version.saved", $"Saved {m.FileName} as version {v.Number}", "mission", id.ToString(), new { version = v.Number, note = v.Note });
        return Results.Ok(new { version = v.Number });
    }

    private static async Task<IResult> Compare(Guid id, int a, int b, AppDb db)
    {
        var va = await FindAsync(db, id, a);
        var vb = await FindAsync(db, id, b);
        if (va is null || vb is null) return NoVersion();
        var fa = await db.MissionVersionFiles.Where(f => f.VersionId == va.Id).ToDictionaryAsync(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var fb = await db.MissionVersionFiles.Where(f => f.VersionId == vb.Id).ToDictionaryAsync(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        return Results.Ok(new
        {
            added = fb.Keys.Where(p => !fa.ContainsKey(p)).Order(StringComparer.Ordinal).ToArray(),
            removed = fa.Keys.Where(p => !fb.ContainsKey(p)).Order(StringComparer.Ordinal).ToArray(),
            changed = fa.Where(kv => fb.TryGetValue(kv.Key, out var s) && s != kv.Value).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ChangedPath(kv.Key, kv.Value, fb[kv.Key])).ToArray(),
        });
    }

    private static async Task<IResult> File(Guid id, int n, string? path, AppDb db, BlobStore blobs)
    {
        if (await FindAsync(db, id, n) is not { } v) return NoVersion();
        var p = ArmaFormats.MissionPath.Normalize(path ?? "");
        var f = await db.MissionVersionFiles.FirstOrDefaultAsync(x => x.VersionId == v.Id && x.Path == p);
        return f is null ? ApiError.Of(404, "not_found", "That file isn't in this version.")
            : Results.Ok(WorkingCopyEndpoints.Describe(f.Path, f.Sha256, await blobs.ReadAsync(f.Sha256)) with { ReadOnly = true });
    }

    private static async Task<IResult> Pbo(Guid id, int n, AppDb db, BlobStore blobs)
    {
        if (await db.Missions.FindAsync(id) is not { } m || await FindAsync(db, id, n) is not { } v) return NoVersion();
        return Results.File(blobs.Open(v.PboSha256), "application/octet-stream", m.FileName);
    }

    private static async Task<IResult> Original(Guid id, int n, AppDb db, BlobStore blobs)
    {
        if (await db.Missions.FindAsync(id) is not { } m || await FindAsync(db, id, n) is not { } v) return NoVersion();
        return v.OriginalUploadSha256 is { } sha
            ? Results.File(blobs.Open(sha), "application/octet-stream", $"{m.ClassName}.{m.Map}.original.pbo")
            : ApiError.Of(404, "not_found", "This version wasn't an uploaded PBO.");
    }

    // Rollback: the older version becomes current and is written onto the servers again; no new version is made (Ruling 8).
    private static async Task<IResult> Restore(Guid id, int n, AppDb db, MissionDeployments deployments, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return ApiError.Of(404, "not_found", "No such mission.");
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        if (await FindAsync(db, id, n) is not { } v) return NoVersion();
        (m.CurrentVersionId, m.UpdatedAt) = (v.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await deployments.DeployAsync(m, v, MissionAccess.UserId(ctx));
        await audit.RecordAsync("mission.version.restored", $"Restored {m.FileName} to version {n}", "mission", id.ToString(), new { version = n });
        return Results.Ok(new { version = n });
    }

    private static async Task<IResult> Open(Guid id, int n, AppDb db, BlobStore blobs, MissionVersions versions, PermissionService permissions, AuditLog audit,
        HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return ApiError.Of(404, "not_found", "No such mission.");
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        if (await FindAsync(db, id, n) is not { } v) return NoVersion();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in await db.MissionVersionFiles.Where(x => x.VersionId == v.Id).ToListAsync()) files[f.Path] = await blobs.ReadAsync(f.Sha256);
        await versions.ReplaceWorkingCopyAsync(m, files, MissionAccess.UserId(ctx), $"Opened version {n}");
        await audit.RecordAsync("mission.version.opened", $"Opened version {n} of {m.FileName} in the working copy", "mission", id.ToString(), new { version = n });
        return Results.Ok(new { version = n });
    }
}
