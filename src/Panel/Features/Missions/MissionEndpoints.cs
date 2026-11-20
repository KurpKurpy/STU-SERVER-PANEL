using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Storage;

namespace Panel.Features.Missions;

public record MissionPut(string? DisplayName, string? Description, string? GameType, string? Category, string[]? Tags, Guid[]? Collaborators);
public record PersonDto(Guid Id, string Name);
public record MissionDto(Guid Id, string ClassName, string Map, string FileName, string DisplayName, string Description, string? GameType, string? Category,
    string[] Tags, PersonDto? Author, PersonDto[] Collaborators, int? CurrentVersion, DateTime? CurrentVersionAt, int Versions, bool IsArchived,
    DateTime CreatedAt, DateTime UpdatedAt, bool CanEdit, bool CanShare);

// Who may change a mission (Phase 4a Ruling 14): editAny, or editOwn for its author and collaborators.
public static class MissionAccess
{
    public static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static async Task<bool> CanEditAsync(AppDb db, PermissionService permissions, HttpContext ctx, Mission m)
    {
        if (UserId(ctx) is not { } uid) return false;
        var perms = await permissions.EffectiveAsync(uid);
        if (perms.Contains(Permissions.MissionsEditAny)) return true;
        return perms.Contains(Permissions.MissionsEditOwn)
            && (m.AuthorId == uid || await db.MissionCollaborators.AnyAsync(c => c.MissionId == m.Id && c.UserId == uid));
    }

    // Ruling 15: the author (with editOwn) or a mission editor chooses the collaborators; collaborators only edit.
    public static async Task<bool> CanShareAsync(PermissionService permissions, HttpContext ctx, Mission m)
    {
        if (UserId(ctx) is not { } uid) return false;
        var perms = await permissions.EffectiveAsync(uid);
        return perms.Contains(Permissions.MissionsEditAny) || (perms.Contains(Permissions.MissionsEditOwn) && m.AuthorId == uid);
    }

    public static IResult Forbidden() => ApiError.Of(403, "forbidden", "Only the mission's author, its collaborators and mission editors can change it.");
}

public static class MissionEndpoints
{
    public static readonly string[] Categories = ["Main Operations", "Training", "Zeus Operations", "Campaigns", "Side Operations", "Testing"];

    public static void MapMissions(this WebApplication app)
    {
        var g = app.MapGroup("/api/missions");
        g.MapGet("", List).RequirePermission(Permissions.MissionsView);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.MissionsView);
        g.MapPost("/upload", Upload).RequirePermission(Permissions.MissionsCreate).WithMetadata(UploadLimits());
        g.MapPost("/{id:guid}/upload", UploadNext).RequirePermission(Permissions.MissionsView).WithMetadata(UploadLimits()); // edit right checked inside
        g.MapPut("/{id:guid}", Put).RequirePermission(Permissions.MissionsView);
        g.MapPost("/{id:guid}/archive", (Guid id, AppDb db, PermissionService p, AuditLog a, HttpContext ctx) => Archive(id, true, db, p, a, ctx))
            .RequirePermission(Permissions.MissionsView);
        g.MapPost("/{id:guid}/unarchive", (Guid id, AppDb db, PermissionService p, AuditLog a, HttpContext ctx) => Archive(id, false, db, p, a, ctx))
            .RequirePermission(Permissions.MissionsView);
    }

    // The 200 MB cap is set on these endpoints only; Kestrel's default elsewhere is ~30 MB (Global Constraints).
    private static object[] UploadLimits() =>
    [
        new RequestSizeLimitAttribute(MissionUpload.MaxRequestBytes),
        new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MissionUpload.MaxRequestBytes, ValueCountLimit = MissionUpload.MaxEntries + 16 },
    ];

    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such mission.");

    private static async Task<IResult> List(string? q, string? map, string? category, string? tag, Guid? author, bool? archived, AppDb db,
        PermissionService permissions, HttpContext ctx)
    {
        var showArchived = archived ?? false;
        var query = db.Missions.Where(m => m.IsArchived == showArchived);
        if (!string.IsNullOrWhiteSpace(map)) query = query.Where(m => m.Map == map.Trim());
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(m => m.Category == category.Trim());
        if (author is { } a) query = query.Where(m => m.AuthorId == a);
        if (!string.IsNullOrWhiteSpace(tag)) query = query.Where(m => db.MissionTags.Any(t => t.MissionId == m.Id && t.Tag!.Name == tag.Trim()));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(m => EF.Functions.Like(m.DisplayName, like, "\\") || EF.Functions.Like(m.ClassName, like, "\\")
                || EF.Functions.Like(m.Description, like, "\\"));
        }
        var missions = await query.OrderByDescending(m => m.UpdatedAt).Take(1000).ToListAsync();
        return Results.Ok(await DtosAsync(db, permissions, ctx, missions));
    }

    private static async Task<IResult> Get(Guid id, AppDb db, PermissionService permissions, HttpContext ctx) =>
        await db.Missions.FindAsync(id) is { } m ? Results.Ok((await DtosAsync(db, permissions, ctx, [m]))[0]) : NotFound();

    public static async Task<MissionDto[]> DtosAsync(AppDb db, PermissionService permissions, HttpContext ctx, IReadOnlyList<Mission> missions)
    {
        var ids = missions.Select(m => m.Id).ToList();
        var tags = (await db.MissionTags.Where(t => ids.Contains(t.MissionId)).Select(t => new { t.MissionId, t.Tag!.Name }).ToListAsync())
            .ToLookup(t => t.MissionId, t => t.Name);
        var collaborators = (await db.MissionCollaborators.Where(c => ids.Contains(c.MissionId)).ToListAsync()).ToLookup(c => c.MissionId, c => c.UserId);
        var userIds = missions.Select(m => m.AuthorId).OfType<Guid>().Concat(collaborators.SelectMany(c => c)).Distinct().ToList();
        var people = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new PersonDto(u.Id, u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!));
        var versions = (await db.MissionVersions.Where(v => ids.Contains(v.MissionId)).Select(v => new { v.Id, v.MissionId, v.Number, v.CreatedAt }).ToListAsync());
        var uid = MissionAccess.UserId(ctx);
        var perms = uid is { } u ? await permissions.EffectiveAsync(u) : new HashSet<string>();
        bool CanEdit(Mission m) => perms.Contains(Permissions.MissionsEditAny)
            || (perms.Contains(Permissions.MissionsEditOwn) && uid is { } me && (m.AuthorId == me || collaborators[m.Id].Contains(me)));
        bool CanShare(Mission m) => perms.Contains(Permissions.MissionsEditAny)
            || (perms.Contains(Permissions.MissionsEditOwn) && uid is { } me && m.AuthorId == me);
        return missions.Select(m =>
        {
            var current = versions.FirstOrDefault(v => v.Id == m.CurrentVersionId);
            return new MissionDto(m.Id, m.ClassName, m.Map, m.FileName, m.DisplayName, m.Description, m.GameType, m.Category,
                tags[m.Id].Order(StringComparer.OrdinalIgnoreCase).ToArray(), m.AuthorId is { } a ? people.GetValueOrDefault(a) : null,
                collaborators[m.Id].Select(c => people.GetValueOrDefault(c)).OfType<PersonDto>().ToArray(),
                current?.Number, current?.CreatedAt, versions.Count(v => v.MissionId == m.Id), m.IsArchived, m.CreatedAt, m.UpdatedAt, CanEdit(m), CanShare(m));
        }).ToArray();
    }

    private static async Task<IResult> Upload(HttpRequest request, AppDb db, MissionVersions versions, BlobStore blobs, PermissionService permissions,
        AuditLog audit, HttpContext ctx, CancellationToken ct)
    {
        var (up, problem) = await MissionUpload.ReadAsync(request, ct);
        if (up is null) return ApiError.Field("file", problem!);
        if (await db.Missions.AnyAsync(m => m.ClassName == up.ClassName && m.Map == up.Map, ct))
            return ApiError.Of(409, "exists", $"{up.ClassName}.{up.Map} is already in the library. Open it and upload a new version there.");
        var (name, type) = MissionUpload.DescriptionInfo(up.Files.GetValueOrDefault("description.ext"));
        var m = new Mission
        {
            Id = Guid.NewGuid(), ClassName = up.ClassName, Map = up.Map, DisplayName = Clip(name ?? up.ClassName, 120), GameType = type is null ? null : Clip(type, 40),
            AuthorId = MissionAccess.UserId(ctx), PboPropertiesJson = JsonSerializer.Serialize(up.PboProperties),
        };
        db.Missions.Add(m);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return ApiError.Of(409, "exists", $"{up.ClassName}.{up.Map} is already in the library."); }
        return await StoreAsync(m, up, db, versions, blobs, permissions, audit, ctx, "mission.created");
    }

    private static async Task<IResult> UploadNext(Guid id, HttpRequest request, AppDb db, MissionVersions versions, BlobStore blobs, PermissionService permissions,
        AuditLog audit, HttpContext ctx, CancellationToken ct)
    {
        if (await db.Missions.FindAsync([id], ct) is not { } m) return NotFound();
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        var (up, problem) = await MissionUpload.ReadAsync(request, ct);
        if (up is null) return ApiError.Field("file", problem!);
        if (!up.ClassName.Equals(m.ClassName, StringComparison.OrdinalIgnoreCase) || !up.Map.Equals(m.Map, StringComparison.OrdinalIgnoreCase))
            return ApiError.Field("file", $"This upload is {up.ClassName}.{up.Map}, but the mission is {m.ClassName}.{m.Map}.");
        if (up.OriginalPbo is not null) m.PboPropertiesJson = JsonSerializer.Serialize(up.PboProperties);
        return await StoreAsync(m, up, db, versions, blobs, permissions, audit, ctx, "mission.uploaded");
    }

    // An upload is a version (Ruling 5): the files become the working copy, then the next version is saved from it.
    private static async Task<IResult> StoreAsync(Mission m, UploadedMission up, AppDb db, MissionVersions versions, BlobStore blobs,
        PermissionService permissions, AuditLog audit, HttpContext ctx, string action)
    {
        var userId = MissionAccess.UserId(ctx);
        var note = $"Uploaded {up.FileName}";
        await versions.ReplaceWorkingCopyAsync(m, up.Files, userId, note);
        var original = up.OriginalPbo is { } o ? await blobs.PutAsync(o) : null;
        MissionVersion version;
        try { version = await versions.SaveAsync(m, note, userId, original); }
        catch (MissionException e) { return ApiError.Field("file", e.Message); }
        await audit.RecordAsync(action, $"Uploaded {m.FileName} as version {version.Number}", "mission", m.Id.ToString(),
            new { version = version.Number, file = up.FileName, files = up.Files.Count });
        return Results.Ok(new { mission = (await DtosAsync(db, permissions, ctx, [m]))[0], version = version.Number });
    }

    private static async Task<IResult> Put(Guid id, MissionPut body, AppDb db, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return NotFound();
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        var name = (body.DisplayName ?? "").Trim();
        if (name.Length is 0 or > 120) return ApiError.Field("displayName", "The name is 1–120 characters.");
        if ((body.Description ?? "").Length > 4000) return ApiError.Field("description", "The description is at most 4,000 characters.");
        var type = string.IsNullOrWhiteSpace(body.GameType) ? null : body.GameType.Trim();
        if (type is { Length: > 40 }) return ApiError.Field("gameType", "The type is at most 40 characters.");
        var category = string.IsNullOrWhiteSpace(body.Category) ? null : Categories.FirstOrDefault(c => c.Equals(body.Category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(body.Category) && category is null) return ApiError.Field("category", "Pick one of the categories.");
        var tagNames = (body.Tags ?? []).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tagNames.Count > 10 || tagNames.Any(t => t.Length > 30)) return ApiError.Field("tags", "At most 10 tags of up to 30 characters.");
        var collaborators = (body.Collaborators ?? []).Distinct().Where(c => c != m.AuthorId).ToList();
        if (collaborators.Count > 20) return ApiError.Field("collaborators", "At most 20 collaborators.");
        if (await db.Users.CountAsync(u => collaborators.Contains(u.Id)) != collaborators.Count) return ApiError.Field("collaborators", "Pick existing members.");
        var current = await db.MissionCollaborators.Where(c => c.MissionId == id).Select(c => c.UserId).ToListAsync();
        if (!current.ToHashSet().SetEquals(collaborators) && !await MissionAccess.CanShareAsync(permissions, ctx, m))
            return ApiError.Of(403, "forbidden", "Only the mission's author or a mission editor can change its collaborators.");

        (m.DisplayName, m.Description, m.GameType, m.Category, m.UpdatedAt) = (name, (body.Description ?? "").Trim(), type, category, DateTime.UtcNow);
        var tags = new List<Tag>();
        foreach (var t in tagNames)
        {
            var existing = await db.Tags.FirstOrDefaultAsync(x => x.Name == t);
            if (existing is null) db.Tags.Add(existing = new Tag { Id = Guid.NewGuid(), Name = t });
            tags.Add(existing);
        }
        db.MissionTags.RemoveRange(await db.MissionTags.Where(x => x.MissionId == id).ToListAsync());
        db.MissionTags.AddRange(tags.Select(t => new MissionTag { MissionId = id, TagId = t.Id }));
        db.MissionCollaborators.RemoveRange(await db.MissionCollaborators.Where(c => c.MissionId == id).ToListAsync());
        db.MissionCollaborators.AddRange(collaborators.Select(c => new MissionCollaborator { MissionId = id, UserId = c }));
        await db.SaveChangesAsync();
        await audit.RecordAsync("mission.updated", $"Changed the details of {m.FileName}", "mission", id.ToString(),
            new { name, category, tags = tagNames, collaborators = collaborators.Count });
        return Results.Ok((await DtosAsync(db, permissions, ctx, [m]))[0]);
    }

    // Hides the mission from the library; the files on the servers stay (Ruling 14).
    private static async Task<IResult> Archive(Guid id, bool archive, AppDb db, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return NotFound();
        if (!await MissionAccess.CanEditAsync(db, permissions, ctx, m)) return MissionAccess.Forbidden();
        (m.IsArchived, m.UpdatedAt) = (archive, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await audit.RecordAsync(archive ? "mission.archived" : "mission.unarchived", $"{(archive ? "Archived" : "Restored")} {m.FileName}", "mission", id.ToString());
        return Results.Ok((await DtosAsync(db, permissions, ctx, [m]))[0]);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
