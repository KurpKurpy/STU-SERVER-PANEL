using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Profiles;

public record RevisionDto(int Number, string? Note, string? CreatedBy, DateTime CreatedAt, bool Applied);
public record ResetBody(int BaseRevision);
public record IdentityDiff(string Field, string? A, string? B);
public record CompareResult(FileDiff[] Files);
public record ProfileCompareResult(IdentityDiff[] Identity, FileDiff[] Files);

public static class RevisionEndpoints
{
    public static void MapRevisions(this WebApplication app)
    {
        var g = app.MapGroup("/api/profiles");
        g.MapGet("/{id:guid}/revisions", List).RequirePermission(Permissions.ProfilesView);
        g.MapGet("/{id:guid}/revisions/{n:int}/files", Files).RequirePermission(Permissions.ProfilesView);
        g.MapGet("/{id:guid}/compare", Compare).RequirePermission(Permissions.ProfilesView);
        g.MapPost("/{id:guid}/revisions/{n:int}/reset", Reset).RequirePermission(Permissions.ProfilesManage);
        g.MapGet("/compare", CompareProfiles).RequirePermission(Permissions.ProfilesView);
    }

    private static async Task<IResult> List(Guid id, AppDb db)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return ApiError.Of(404, "not_found", "No such profile.");
        var rows = await (from r in db.ProfileRevisions
                          where r.ProfileId == id
                          join u in db.Users on r.CreatedById equals u.Id into users
                          from u in users.DefaultIfEmpty()
                          orderby r.Number descending
                          select new RevisionDto(r.Number, r.Note, u == null ? null : u.DisplayName != "" ? u.DisplayName : u.UserName, r.CreatedAt, r.Id == p.AppliedRevisionId))
            .ToListAsync();
        return Results.Ok(rows);
    }

    private static async Task<(ProfileSnapshot Snapshot, Secrets Secrets)?> LoadAsync(AppDb db, ProfileStore store, Guid id, int number)
    {
        var revision = await db.ProfileRevisions.FirstOrDefaultAsync(r => r.ProfileId == id && r.Number == number);
        if (revision is null) return null;
        var (snapshot, secrets) = store.Load(revision);
        return (snapshot, secrets ?? Secrets.None);
    }

    private static async Task<IResult> Files(Guid id, int n, AppDb db, ProfileStore store) =>
        await LoadAsync(db, store, id, n) is { } r
            ? Results.Ok(ConfigFiles.Generate(r.Snapshot.ToConfig(r.Secrets), SecretMode.Omit))
            : ApiError.Of(404, "not_found", "No such revision.");

    // Both sides as the server would get them, with passwords masked; a password that differs reads "(changed)".
    internal static FileDiff[] Diffs(ProfileConfig a, ProfileConfig b)
    {
        var fa = ConfigFiles.Generate(a, SecretMode.Real);
        var fb = ConfigFiles.Generate(b, SecretMode.Real);
        return
        [
            new("server.cfg", true, LineDiff.Compute(ConfigFiles.MaskSecrets(fa.ServerCfg), ConfigFiles.MaskSecrets(fb.ServerCfg, a.Secrets))),
            new("basic.cfg", fa.BasicCfg is not null, LineDiff.Compute(fa.BasicCfg ?? "", fb.BasicCfg ?? "")),
            new("difficulty", true, LineDiff.Compute(fa.DifficultyPresets, fb.DifficultyPresets)),
        ];
    }

    private static async Task<IResult> Compare(Guid id, int from, int? to, AppDb db, ProfileStore store)
    {
        var head = await store.HeadNumberAsync(id);
        if (await LoadAsync(db, store, id, from) is not { } a || await LoadAsync(db, store, id, to ?? head) is not { } b)
            return ApiError.Of(404, "not_found", "No such revision.");
        return Results.Ok(new CompareResult(Diffs(a.Snapshot.ToConfig(a.Secrets), b.Snapshot.ToConfig(b.Secrets))));
    }

    private static async Task<IResult> Reset(Guid id, int n, ResetBody body, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return ApiError.Of(404, "not_found", "No such profile.");
        var head = await store.HeadNumberAsync(id);
        if (body.BaseRevision != head) return ApiError.Of(409, "stale", $"Someone saved revision {head} meanwhile. Reload to see it.");
        var revision = await db.ProfileRevisions.FirstOrDefaultAsync(r => r.ProfileId == id && r.Number == n);
        if (revision is null) return ApiError.Of(404, "not_found", "No such revision.");
        var (snapshot, secrets) = store.Load(revision);
        if (secrets is null)
            return ApiError.Of(409, "secrets_unreadable", "That revision's passwords can't be decrypted (the panel's encryption keys changed).");
        return await ProfileEndpoints.SaveAsync(p, snapshot, secrets, $"Reset to revision {n}", [], db, store, audit, ctx, "profile.reset");
    }

    private static async Task<IResult> CompareProfiles(Guid a, Guid b, AppDb db, ProfileStore store)
    {
        var rows = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id
                          where p.Id == a || p.Id == b select new { p, InstallName = i.Name }).ToListAsync();
        var pa = rows.FirstOrDefault(r => r.p.Id == a);
        var pb = rows.FirstOrDefault(r => r.p.Id == b);
        if (pa is null || pb is null) return ApiError.Of(404, "not_found", "No such profile.");
        IdentityDiff Field(string name, Func<Profile, string, string?> get) => new(name, get(pa.p, pa.InstallName), get(pb.p, pb.InstallName));
        IdentityDiff[] identity =
        [
            Field("name", (p, _) => p.Name), Field("slug", (p, _) => p.Slug), Field("install", (_, i) => i),
            Field("executable", (p, _) => p.Executable), Field("port", (p, _) => p.Port.ToString()), Field("publicAddress", (p, _) => p.PublicAddress),
        ];
        var ca = (await store.HeadAsync(pa.p)).ToConfig(store.LoadSecrets(pa.p) ?? Secrets.None);
        var cb = (await store.HeadAsync(pb.p)).ToConfig(store.LoadSecrets(pb.p) ?? Secrets.None);
        return Results.Ok(new ProfileCompareResult(identity, Diffs(ca, cb)));
    }
}
