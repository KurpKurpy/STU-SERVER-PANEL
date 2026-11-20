using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ArmaFormats;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Storage;

namespace Panel.Features.Missions;

public record SaveFileBody(string? Path, string? Content, string? BaseSha);
public record RenameBody(string? From, string? To);
public record FileDto(string Path, long Size, string Sha, bool Text, bool Binarized, DateTime UpdatedAt, string? UpdatedBy);
public record ContentDto(string Path, string? Sha, long Size, string? Text, bool Binary, bool ReadOnly, bool Binarized, bool Deleted = false);
public record RevisionDto(Guid Id, string? Sha, DateTime At, string? By, string? Note);
public record SearchHit(string Path, int Line, string Text);

// The working copy behind the editor (Phase 4a Task 5, Rulings 3 and 13). Paths use forward slashes and Task 1's name rules; there are no
// empty folders (a PBO can't hold one), so a folder exists while a file is in it.
public static class WorkingCopyEndpoints
{
    public const int MaxTextBytes = 2 * 1024 * 1024;
    private const string Sqm = "mission.sqm";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void MapWorkingCopy(this WebApplication app)
    {
        var g = app.MapGroup("/api/missions/{id:guid}/files");
        g.MapGet("", Tree).RequirePermission(Permissions.MissionsView);
        g.MapGet("/content", Content).RequirePermission(Permissions.MissionsView);
        g.MapGet("/download", Download).RequirePermission(Permissions.MissionsDownload);
        g.MapGet("/zip", Zip).RequirePermission(Permissions.MissionsDownload);
        g.MapGet("/history", History).RequirePermission(Permissions.MissionsView);
        g.MapGet("/revision/{revId:guid}", Revision).RequirePermission(Permissions.MissionsView);
        g.MapGet("/search", Search).RequirePermission(Permissions.MissionsView);
        g.MapPut("/content", Save).RequirePermission(Permissions.MissionsView);   // edit right checked inside
        g.MapPost("/rename", Rename).RequirePermission(Permissions.MissionsView);
        g.MapDelete("", Delete).RequirePermission(Permissions.MissionsView);
        g.MapPost("/convert", Convert).RequirePermission(Permissions.MissionsView);
        g.MapPost("/upload", Upload).RequirePermission(Permissions.MissionsView)
            .WithMetadata(new RequestSizeLimitAttribute(MissionUpload.MaxRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MissionUpload.MaxRequestBytes, ValueCountLimit = MissionUpload.MaxEntries + 16 });
    }

    private static IResult NotFound(string what = "No such mission.") => ApiError.Of(404, "not_found", what);

    // Text the editor can open: at most 2 MB of strict UTF-8 without NUL bytes, and not binarized. Null otherwise.
    public static string? AsText(byte[] data)
    {
        if (data.Length > MaxTextBytes || Rap.IsRap(data) || data.AsSpan(0, Math.Min(data.Length, 8192)).IndexOf((byte)0) >= 0) return null;
        try { return MissionScan.Text(StrictUtf8.GetBytes(StrictUtf8.GetString(data))); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string? CleanPath(string? path, bool allowRoot = false)
    {
        var p = MissionPath.Normalize(path ?? "").TrimEnd('/'); // a leading slash is refused, never quietly made relative
        if (p.Length == 0) return allowRoot ? "" : null;
        return MissionPath.Problem(p) is null ? p : null;
    }

    private static async Task<(Mission? Mission, IResult? Error)> EditableAsync(Guid id, AppDb db, PermissionService permissions, HttpContext ctx)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return (null, NotFound());
        return await MissionAccess.CanEditAsync(db, permissions, ctx, m) ? (m, null) : (null, MissionAccess.Forbidden());
    }

    private static async Task<IResult> Tree(Guid id, AppDb db, BlobStore blobs)
    {
        if (!await db.Missions.AnyAsync(m => m.Id == id)) return NotFound();
        var files = await db.MissionWorkingFiles.Where(w => w.MissionId == id).OrderBy(w => w.Path).ToListAsync();
        var ids = files.Select(f => f.UpdatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!);
        var dtos = new List<FileDto>();
        foreach (var f in files)
        {
            var head = new byte[Math.Min(f.Size, 8192)];
            await using (var s = blobs.Open(f.Sha256)) await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
            var binarized = Rap.IsRap(head);
            var text = !binarized && f.Size <= MaxTextBytes && head.AsSpan().IndexOf((byte)0) < 0;
            dtos.Add(new FileDto(f.Path, f.Size, f.Sha256, text, binarized, f.UpdatedAt, f.UpdatedById is { } u ? names.GetValueOrDefault(u) : null));
        }
        return Results.Ok(dtos);
    }

    internal static ContentDto Describe(string path, string sha, byte[] data)
    {
        if (Rap.IsRap(data))
        {
            try { return new ContentDto(path, sha, data.LongLength, ConfigText.Write(Rap.Read(data)), false, true, true); }
            catch (RapException) { return new ContentDto(path, sha, data.LongLength, null, true, true, true); }
        }
        var text = AsText(data);
        return new ContentDto(path, sha, data.LongLength, text, text is null, text is null, false);
    }

    private static async Task<IResult> Content(Guid id, string? path, AppDb db, BlobStore blobs)
    {
        var p = CleanPath(path);
        var file = p is null ? null : await db.MissionWorkingFiles.FirstOrDefaultAsync(w => w.MissionId == id && w.Path == p);
        return file is null ? NotFound("No such file.") : Results.Ok(Describe(file.Path, file.Sha256, await blobs.ReadAsync(file.Sha256)));
    }

    private static async Task<IResult> Download(Guid id, string? path, AppDb db, BlobStore blobs)
    {
        var p = CleanPath(path);
        var file = p is null ? null : await db.MissionWorkingFiles.FirstOrDefaultAsync(w => w.MissionId == id && w.Path == p);
        return file is null ? NotFound("No such file.") : Results.File(blobs.Open(file.Sha256), "application/octet-stream", file.Path.Split('/')[^1]);
    }

    private static async Task<IResult> Zip(Guid id, AppDb db, BlobStore blobs)
    {
        if (await db.Missions.FindAsync(id) is not { } m) return NotFound();
        var files = await db.MissionWorkingFiles.Where(w => w.MissionId == id).OrderBy(w => w.Path).ToListAsync();
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in files)
            {
                await using var entry = zip.CreateEntry($"{m.ClassName}.{m.Map}/{f.Path}").Open();
                await using var blob = blobs.Open(f.Sha256);
                await blob.CopyToAsync(entry);
            }
        ms.Position = 0;
        return Results.File(ms, "application/zip", $"{m.ClassName}.{m.Map}.zip");
    }

    private static async Task<IResult> History(Guid id, string? path, AppDb db)
    {
        var p = CleanPath(path);
        if (p is null) return ApiError.Field("path", "Pick a file.");
        var revisions = await db.MissionFileRevisions.Where(r => r.MissionId == id && r.Path == p).OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Take(200).ToListAsync();
        var ids = revisions.Select(r => r.ById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName!);
        return Results.Ok(revisions.Select(r => new RevisionDto(r.Id, r.Sha256, r.At, r.ById is { } u ? names.GetValueOrDefault(u) : null, r.Note)));
    }

    private static async Task<IResult> Revision(Guid id, Guid revId, AppDb db, BlobStore blobs)
    {
        var r = await db.MissionFileRevisions.FirstOrDefaultAsync(x => x.Id == revId && x.MissionId == id);
        if (r is null) return NotFound("No such revision.");
        return r.Sha256 is null
            ? Results.Ok(new ContentDto(r.Path, null, 0, null, false, true, false, Deleted: true))
            : Results.Ok(Describe(r.Path, r.Sha256, await blobs.ReadAsync(r.Sha256)) with { ReadOnly = true });
    }

    private static async Task<IResult> Search(Guid id, string? q, bool? regex, AppDb db, BlobStore blobs)
    {
        if (string.IsNullOrEmpty(q) || q.Length > 200) return ApiError.Field("q", "Search for 1–200 characters.");
        Regex? pattern = null;
        if (regex == true)
        {
            try { pattern = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException e) { return ApiError.Field("q", $"That isn't a valid regular expression: {e.Message}"); }
        }
        var hits = new List<SearchHit>();
        foreach (var f in await db.MissionWorkingFiles.Where(w => w.MissionId == id).OrderBy(w => w.Path).ToListAsync())
        {
            if (f.Size > MaxTextBytes || AsText(await blobs.ReadAsync(f.Sha256)) is not { } text) continue;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < 500; i++)
            {
                bool match;
                try { match = pattern?.IsMatch(lines[i]) ?? lines[i].Contains(q, StringComparison.OrdinalIgnoreCase); }
                catch (RegexMatchTimeoutException) { return ApiError.Field("q", "That expression takes too long; make it simpler."); }
                if (match) hits.Add(new SearchHit(f.Path, i + 1, lines[i].TrimEnd('\r').Trim() is var l && l.Length > 200 ? l[..200] : l));
            }
        }
        return Results.Ok(hits);
    }

    // Writes one file of the working copy (new or changed) and its revision.
    private static async Task WriteAsync(AppDb db, BlobStore blobs, Mission m, string path, byte[] data, Guid? userId, string note, MissionWorkingFile? existing)
    {
        var sha = await blobs.PutAsync(data);
        var now = DateTime.UtcNow;
        if (existing is null) db.MissionWorkingFiles.Add(new MissionWorkingFile { MissionId = m.Id, Path = path, Sha256 = sha, Size = data.LongLength, UpdatedById = userId, UpdatedAt = now });
        else (existing.Sha256, existing.Size, existing.UpdatedById, existing.UpdatedAt) = (sha, data.LongLength, userId, now);
        db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = m.Id, Path = existing?.Path ?? path, Sha256 = sha, ById = userId, At = now, Note = note });
        m.UpdatedAt = now;
    }

    private static async Task<IResult?> PlaceProblemAsync(AppDb db, Guid missionId, string path)
    {
        var paths = await db.MissionWorkingFiles.Where(w => w.MissionId == missionId).Select(w => w.Path).ToListAsync();
        if (paths.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) return ApiError.Of(409, "conflict", $"{path} is a folder.");
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var parent = string.Join('/', parts[..i]);
            if (paths.Any(p => p.Equals(parent, StringComparison.OrdinalIgnoreCase))) return ApiError.Of(409, "conflict", $"{parent} is a file, so it can't hold {path}.");
        }
        return null;
    }

    private static async Task<IResult> Save(Guid id, SaveFileBody body, AppDb db, BlobStore blobs, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var (m, error) = await EditableAsync(id, db, permissions, ctx);
        if (m is null) return error!;
        if (CleanPath(body.Path) is not { } path) return ApiError.Field("path", MissionPath.Problem(MissionPath.Normalize(body.Path ?? "").Trim('/')) ?? "Name the file.");
        var data = Encoding.UTF8.GetBytes(body.Content ?? "");
        if (data.Length > MaxTextBytes) return ApiError.Field("content", "A file edited in the browser is at most 2 MB.");
        var existing = await db.MissionWorkingFiles.FirstOrDefaultAsync(w => w.MissionId == id && w.Path == path);
        if (existing is null && body.BaseSha is not null) return ApiError.Of(409, "stale", "The file was deleted or renamed meanwhile. Reload to see the files.");
        if (existing is not null && existing.Sha256 != body.BaseSha) return ApiError.Of(409, "stale", "Someone saved this file meanwhile. Reload it to see their change.");
        if (existing is not null && Rap.IsRap(await blobs.ReadAsync(existing.Sha256)))
            return ApiError.Of(409, "binarized", "This file is binarized. Convert it to text first.");
        if (existing is null && await PlaceProblemAsync(db, id, path) is { } place) return place;
        if (path.Equals(Sqm, StringComparison.OrdinalIgnoreCase))
        {
            try { MissionScan.Addons(data); }
            catch (ConfigSyntaxException e) { return ApiError.Field("content", $"mission.sqm wouldn't be readable: {e.Message}"); }
        }
        await WriteAsync(db, blobs, m, path, data, MissionAccess.UserId(ctx), "Edited in the browser", existing);
        await db.SaveChangesAsync();
        await audit.RecordAsync("mission.file.saved", $"Saved {path} in {m.FileName}", "mission", id.ToString(), new { path, created = existing is null });
        var saved = await db.MissionWorkingFiles.FirstAsync(w => w.MissionId == id && w.Path == path);
        return Results.Ok(Describe(saved.Path, saved.Sha256, data));
    }

    private static async Task<IResult> Upload(Guid id, string? folder, HttpRequest request, AppDb db, BlobStore blobs, PermissionService permissions, AuditLog audit,
        HttpContext ctx, CancellationToken ct)
    {
        var (m, error) = await EditableAsync(id, db, permissions, ctx);
        if (m is null) return error!;
        if (CleanPath(folder, allowRoot: true) is not { } target) return ApiError.Field("folder", "That folder name isn't allowed.");
        if (!request.HasFormContentType) return ApiError.Field("file", "Choose files to upload.");
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count == 0) return ApiError.Field("file", "Choose files to upload.");
        var userId = MissionAccess.UserId(ctx);
        var written = new List<string>();
        foreach (var f in form.Files)
        {
            var path = (target.Length > 0 ? target + "/" : "") + MissionPath.Normalize(f.FileName).Trim('/');
            if (MissionPath.Problem(path) is { } problem) return ApiError.Field("file", problem);
            if (f.Length > MissionUpload.MaxFileBytes) return ApiError.Field("file", $"{path} is larger than {MissionUpload.MaxFileBytes / 1024 / 1024} MB.");
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms, ct);
            var data = ms.ToArray();
            if (path.Equals(Sqm, StringComparison.OrdinalIgnoreCase))
            {
                try { MissionScan.Addons(data); }
                catch (Exception e) when (e is ConfigSyntaxException or RapException) { return ApiError.Field("file", $"mission.sqm can't be read: {e.Message}"); }
            }
            var existing = await db.MissionWorkingFiles.FirstOrDefaultAsync(w => w.MissionId == id && w.Path == path, ct);
            if (existing is null && await PlaceProblemAsync(db, id, path) is { } place) return place;
            await WriteAsync(db, blobs, m, path, data, userId, "Uploaded in the editor", existing);
            await db.SaveChangesAsync(ct);
            written.Add(path);
        }
        await audit.RecordAsync("mission.file.uploaded", $"Uploaded {written.Count} files into {m.FileName}", "mission", id.ToString(), new { paths = written });
        return Results.Ok(new { paths = written });
    }

    // Renames a file, or a folder with everything in it; both sides are recorded as revisions.
    private static async Task<IResult> Rename(Guid id, RenameBody body, AppDb db, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var (m, error) = await EditableAsync(id, db, permissions, ctx);
        if (m is null) return error!;
        if (CleanPath(body.From) is not { } from) return ApiError.Field("from", "Pick a file or folder.");
        if (CleanPath(body.To) is not { } to) return ApiError.Field("to", MissionPath.Problem(MissionPath.Normalize(body.To ?? "").Trim('/')) ?? "Name it.");
        var files = await db.MissionWorkingFiles.Where(w => w.MissionId == id).ToListAsync();
        var moving = files.Where(f => f.Path.Equals(from, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (moving.Count == 0) return NotFound("No such file or folder.");
        if (moving.Any(f => f.Path.Equals(Sqm, StringComparison.OrdinalIgnoreCase))) return ApiError.Field("from", "mission.sqm stays at the top of the mission.");
        var renamed = moving.Select(f => (File: f, To: to + f.Path[from.Length..])).ToList();
        var staying = files.Except(moving).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (renamed.FirstOrDefault(r => staying.Contains(r.To) || staying.Any(s => s.StartsWith(r.To + "/", StringComparison.OrdinalIgnoreCase))) is { File: not null } clash)
            return ApiError.Of(409, "conflict", $"{clash.To} already exists.");
        var userId = MissionAccess.UserId(ctx);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();
        db.MissionWorkingFiles.RemoveRange(moving);
        await db.SaveChangesAsync(); // removed first: a case-only rename reuses the same key
        foreach (var (file, newPath) in renamed)
        {
            db.MissionWorkingFiles.Add(new MissionWorkingFile { MissionId = id, Path = newPath, Sha256 = file.Sha256, Size = file.Size, UpdatedById = userId, UpdatedAt = now });
            db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = id, Path = file.Path, Sha256 = null, ById = userId, At = now, Note = $"Renamed to {newPath}" });
            db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = id, Path = newPath, Sha256 = file.Sha256, ById = userId, At = now, Note = $"Renamed from {file.Path}" });
        }
        m.UpdatedAt = now;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.RecordAsync("mission.file.renamed", $"Renamed {from} to {to} in {m.FileName}", "mission", id.ToString(), new { from, to, files = moving.Count });
        return Results.Ok(new { renamed = renamed.Count });
    }

    private static async Task<IResult> Delete(Guid id, string? path, AppDb db, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var (m, error) = await EditableAsync(id, db, permissions, ctx);
        if (m is null) return error!;
        if (CleanPath(path) is not { } p) return ApiError.Field("path", "Pick a file or folder.");
        var gone = await db.MissionWorkingFiles.Where(w => w.MissionId == id).ToListAsync();
        gone = gone.Where(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (gone.Count == 0) return NotFound("No such file or folder.");
        if (gone.Any(f => f.Path.Equals(Sqm, StringComparison.OrdinalIgnoreCase))) return ApiError.Field("path", "mission.sqm can't be deleted.");
        var userId = MissionAccess.UserId(ctx);
        var now = DateTime.UtcNow;
        db.MissionWorkingFiles.RemoveRange(gone);
        db.MissionFileRevisions.AddRange(gone.Select(f => new MissionFileRevision { Id = Guid.NewGuid(), MissionId = id, Path = f.Path, Sha256 = null, ById = userId, At = now, Note = "Deleted" }));
        m.UpdatedAt = now;
        await db.SaveChangesAsync();
        await audit.RecordAsync("mission.file.deleted", $"Deleted {p} from {m.FileName}", "mission", id.ToString(), new { path = p, files = gone.Count });
        return Results.Ok(new { deleted = gone.Count });
    }

    // Binarized → text (Ruling 3; Task 1 and S2 gates passed): the binarized original stays in the file's history.
    private static async Task<IResult> Convert(Guid id, string? path, AppDb db, BlobStore blobs, PermissionService permissions, AuditLog audit, HttpContext ctx)
    {
        var (m, error) = await EditableAsync(id, db, permissions, ctx);
        if (m is null) return error!;
        var p = CleanPath(path);
        var file = p is null ? null : await db.MissionWorkingFiles.FirstOrDefaultAsync(w => w.MissionId == id && w.Path == p);
        if (file is null) return NotFound("No such file.");
        var data = await blobs.ReadAsync(file.Sha256);
        if (!Rap.IsRap(data)) return ApiError.Of(409, "not_binarized", "This file is already text.");
        string text;
        try { text = ConfigText.Write(Rap.Read(data)); }
        catch (RapException e) { return ApiError.Of(409, "unreadable", $"The binarized file can't be read: {e.Message}"); }
        await WriteAsync(db, blobs, m, file.Path, Encoding.UTF8.GetBytes(text), MissionAccess.UserId(ctx), "Converted to text", file);
        await db.SaveChangesAsync();
        await audit.RecordAsync("mission.file.converted", $"Converted {file.Path} in {m.FileName} to text", "mission", id.ToString(), new { path = file.Path });
        return Results.Ok(Describe(file.Path, file.Sha256, Encoding.UTF8.GetBytes(text)));
    }
}
