using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ArmaFormats;
using Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Mods;
using Panel.Hubs;

namespace Panel.Features.Files;

public sealed record FileSpaceDef(string Name, string Label, bool PerInstall, string Read, string? Write, bool HasPasswords);
public sealed record FileAction(Guid Install, string Space, string Path, string? Target);
public sealed record SaveText(Guid Install, string Space, string Path, string Text, bool Bom, string? Sha256);
public sealed record FileText(string Text, bool Bom, string Eol, string Sha256, long Size);

// Phase 4b: the file manager over the server's own folders. The panel checks the person's permission for the space; the agent owns where
// each space is and what may be written there (Rulings 2–4). Every request names an install: per-install spaces are inside it, and the
// others are on that install's agent.
public static class FileEndpoints
{
    public const int Chunk = 262144, MaxText = 1_048_576;

    public static readonly IReadOnlyList<FileSpaceDef> Spaces =
    [
        new("mpmissions", "Missions folder (mpmissions)", true, Permissions.FilesMpmissionsRead, Permissions.FilesMpmissionsWrite, false),
        new("keys", "Mod keys", true, Permissions.FilesKeysRead, Permissions.FilesKeysWrite, false),
        new("profiles", "Server profiles (logs, BattlEye)", false, Permissions.FilesProfilesRead, Permissions.FilesProfilesWrite, true),
        new("localmods", "Local mods", false, Permissions.FilesLocalmodsRead, Permissions.FilesLocalmodsWrite, false),
        new("configs", "Generated configs", false, Permissions.FilesConfigsRead, null, true),
        new("workshop", "Workshop mods", false, Permissions.FilesWorkshopRead, null, false),
        new("backups", "Backups", false, Permissions.FilesBackupsRead, null, true),
    ];

    public static void MapFiles(this WebApplication app)
    {
        var g = app.MapGroup("/api/files").RequireAuthorization();
        g.MapGet("/spaces", ListSpaces);
        g.MapGet("/list", List);
        g.MapGet("/search", Search);
        g.MapGet("/text", Text);
        g.MapGet("/download", Download);
        g.MapPost("/upload", Upload).WithMetadata(new RequestSizeLimitAttribute(FileTransfers.MaxBytes + 1_048_576));
        g.MapPut("/text", Save);
        g.MapPost("/folder", (FileAction b, Ctx c) => ActAsync(b, FileOps.Mkdir, c));
        g.MapPost("/move", (FileAction b, Ctx c) => ActAsync(b, FileOps.Move, c));
        g.MapPost("/copy", (FileAction b, Ctx c) => ActAsync(b, FileOps.Copy, c));
        g.MapPost("/extract", (FileAction b, Ctx c) => ActAsync(b, FileOps.Extract, c));
        g.MapDelete("", (Guid install, string space, string path, Ctx c) => ActAsync(new FileAction(install, space, path, null), FileOps.Delete, c));
        // The agent pulls an upload only while that change is pending for it (Ruling 12).
        app.MapGet("/api/agent/transfers/{id:guid}", Pull).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AgentKeyAuthentication.SchemeName });
    }

    // The services every file request needs, bound as one parameter.
    public sealed record Ctx(AppDb Db, PermissionService Permissions, AgentRegistry Registry, AgentCalls Agents, FileChanges Changes, FileTransfers Transfers,
        AddonScans Scans, AuditLog Audit, IConfiguration Config, HttpContext Http)
    {
        public static ValueTask<Ctx?> BindAsync(HttpContext http) => ValueTask.FromResult<Ctx?>(new Ctx(
            http.RequestServices.GetRequiredService<AppDb>(), http.RequestServices.GetRequiredService<PermissionService>(),
            http.RequestServices.GetRequiredService<AgentRegistry>(), http.RequestServices.GetRequiredService<AgentCalls>(),
            http.RequestServices.GetRequiredService<FileChanges>(), http.RequestServices.GetRequiredService<FileTransfers>(),
            http.RequestServices.GetRequiredService<AddonScans>(), http.RequestServices.GetRequiredService<AuditLog>(),
            http.RequestServices.GetRequiredService<IConfiguration>(), http));

        public async Task<IReadOnlySet<string>> PermsAsync() =>
            Guid.TryParse(Http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? await Permissions.EffectiveAsync(id) : new HashSet<string>();
    }

    private sealed record Target(FileSpaceDef Def, FileSpace Space, ServerInstall Install);

    // An unknown space is 404 before any permission is looked at (Ruling 15); names are checked before anything reaches the agent.
    private static async Task<(Target? Target, IResult? Error)> ResolveAsync(Guid install, string? space, string? path, bool write, Ctx c)
    {
        if (Spaces.FirstOrDefault(s => s.Name == space) is not { } def || await c.Db.Installs.FindAsync(install) is not { } i)
            return (null, ApiError.Of(404, "not_found", "No such folder."));
        var perms = await c.PermsAsync();
        if (!perms.Contains(def.Read)) return (null, ApiError.Of(403, "forbidden", "You don't have access to that folder."));
        if (write && (def.Write is null || !perms.Contains(def.Write)))
            return (null, ApiError.Of(403, "forbidden", def.Write is null ? "That folder is read-only." : "You can't change files in that folder."));
        if (FileNames.Problem(path ?? "") is { } problem) return (null, ApiError.Field("path", problem));
        if (def.Name == "backups" && !perms.Contains(Permissions.BackupsManage)
            && string.Equals((path ?? "").Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), "server", StringComparison.OrdinalIgnoreCase))
            return (null, ApiError.Of(403, "forbidden", "Server backups are on the Backups page."));
        if (!c.Registry.IsConnected(i.AgentId)) return (null, Offline());
        return (new Target(def, new FileSpace(def.Name, def.PerInstall ? i.InstallPath : null), i), null);
    }

    private static async Task<IResult> ListSpaces(Ctx c)
    {
        var perms = await c.PermsAsync();
        // Only for people with some file permission, and without the installs' paths (final review M-17, graded up).
        if (!Spaces.Any(s => perms.Contains(s.Read))) return ApiError.Of(403, "forbidden", "You don't have access to any server folders.");
        var installs = (await c.Db.Installs.OrderBy(i => i.CreatedAt).ToListAsync()).Select(i => new
        {
            i.Id, i.Name,
            ReadOnly = c.Registry.Hello(i.AgentId)?.Installs.FirstOrDefault(x => x.Path.Equals(i.InstallPath, StringComparison.OrdinalIgnoreCase))?.ReadOnly ?? false,
        });
        var spaces = Spaces.Where(s => perms.Contains(s.Read)).Select(s => new
        {
            s.Name, s.Label, s.PerInstall, s.HasPasswords, Writable = s.Write is not null, CanWrite = s.Write is not null && perms.Contains(s.Write),
        });
        return Results.Ok(new { installs, spaces });
    }

    private static async Task<IResult> List(Guid install, string space, string? path, Ctx c)
    {
        var (t, error) = await ResolveAsync(install, space, path, false, c);
        return error ?? await CallAsync(() => c.Agents.CallAsync(t!.Install.AgentId, a => a.FileList(t.Space, path ?? "")));
    }

    private static async Task<IResult> Search(Guid install, string space, string? path, string q, Ctx c)
    {
        var (t, error) = await ResolveAsync(install, space, path, false, c);
        return error ?? await CallAsync(() => c.Agents.CallAsync(t!.Install.AgentId, a => a.FileSearch(t.Space, path ?? "", q ?? "")));
    }

    private static async Task<IResult> CallAsync<T>(Func<Task<T>> call)
    {
        try { return Results.Ok(await call()); }
        catch (AgentUnavailableException) { return Offline(); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
    }

    // Ruling 13: UTF-8 text up to 1 MB with no NUL; the BOM and line ending are reported so a save writes them back.
    private static async Task<IResult> Text(Guid install, string space, string path, Ctx c)
    {
        var (t, error) = await ResolveAsync(install, space, path, false, c);
        if (error is not null) return error;
        byte[] data;
        try
        {
            var first = await c.Agents.CallAsync(t!.Install.AgentId, a => a.FileRead(t.Space, path, 0, Chunk));
            if (first.TotalSize > MaxText) return ApiError.Of(400, "too_large", "That file is over 1 MB; download it instead.");
            var all = new MemoryStream();
            all.Write(first.Data);
            while (all.Length < first.TotalSize)
            {
                var o = all.Length;
                var chunk = await c.Agents.CallAsync(t.Install.AgentId, a => a.FileRead(t.Space, path, o, (int)Math.Min(Chunk, first.TotalSize - o)));
                if (chunk.Data.Length == 0) break;
                all.Write(chunk.Data);
            }
            data = all.ToArray();
        }
        catch (AgentUnavailableException) { return Offline(); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }

        var bom = data is [0xEF, 0xBB, 0xBF, ..];
        var body = bom ? data[3..] : data;
        string text;
        try { text = Array.IndexOf(body, (byte)0) >= 0 ? throw new DecoderFallbackException() : new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { return ApiError.Of(400, "not_text", "That isn't a UTF-8 text file; download it instead."); }
        return Results.Ok(new FileText(text, bom, text.Contains("\r\n") ? "\r\n" : "\n", Convert.ToHexStringLower(SHA256.HashData(data)), data.Length));
    }

    // Always an attachment of application/octet-stream (nosniff is global), so nothing uploaded ever renders on the panel's origin.
    private static async Task<IResult> Download(Guid install, string space, string path, Ctx c)
    {
        var (t, error) = await ResolveAsync(install, space, path, false, c);
        if (error is not null) return error;
        return await StreamAsync(c.Agents, t!.Install.AgentId, t.Space, path, c.Http);
    }

    // An agent file streamed to the browser in chunks (also Phase 7b's server backup downloads).
    internal static async Task<IResult> StreamAsync(AgentCalls agents, Guid agentId, FileSpace space, string path, HttpContext ctx)
    {
        FileChunk first;
        try { first = await agents.CallAsync(agentId, a => a.FileRead(space, path, 0, Chunk)); }
        catch (AgentUnavailableException) { return Offline(); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }

        var name = path.Split('\\', '/')[^1];
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            { FileNameStar = name, FileName = new string(name.Select(ch => ch is < (char)32 or > (char)126 or '"' or '\\' ? '_' : ch).ToArray()) }.ToString();
        ctx.Response.ContentLength = first.TotalSize;
        try
        {
            var offset = Math.Min(first.Data.Length, first.TotalSize);
            await ctx.Response.Body.WriteAsync(first.Data.AsMemory(0, (int)offset));
            while (offset < first.TotalSize)
            {
                var o = offset;
                var chunk = await agents.CallAsync(agentId, a => a.FileRead(space, path, o, (int)Math.Min(Chunk, first.TotalSize - o)));
                if (chunk.Data.Length == 0) break;
                var n = (int)Math.Min(chunk.Data.Length, first.TotalSize - offset);
                await ctx.Response.Body.WriteAsync(chunk.Data.AsMemory(0, n));
                offset += n;
            }
            if (offset < first.TotalSize) ctx.Abort(); // the file shrank or the agent went away
        }
        catch (Exception e) when (e is AgentUnavailableException or HubException) { ctx.Abort(); }
        return Results.Empty;
    }

    // One file per request, streamed to the transfer folder after the permission check, so a refused upload is never read.
    private static async Task<IResult> Upload(Guid install, string space, string? path, bool? overwrite, Ctx c, CancellationToken ct)
    {
        var folder = path ?? "";
        var (t, error) = await ResolveAsync(install, space, folder, true, c);
        if (error is not null) return error;
        var request = c.Http.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 } boundary)
            return ApiError.Field("file", "Send the file as multipart/form-data.");
        var reader = new MultipartReader(boundary, request.Body);
        (Guid Id, string Path, long Size, string Sha256)? upload = null;
        try
        {
            for (var section = await reader.ReadNextSectionAsync(ct); section is not null && upload is null; section = await reader.ReadNextSectionAsync(ct))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.IsFileDisposition()) continue;
                var name = HeaderUtilities.RemoveQuotes(cd.FileNameStar.HasValue ? cd.FileNameStar : cd.FileName).Value ?? "";
                var full = folder.Length == 0 ? name : $"{folder}\\{name}";
                if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || FileNames.Problem(full) is not null)
                    return ApiError.Field("file", FileNames.Problem(full) ?? "That file name isn't allowed.");
                if (await LibraryGuardAsync(t!, FileOps.Write, full, null, c) is { } guarded) return guarded;
                var id = Guid.NewGuid();
                if (await c.Transfers.SaveAsync(id, section.Body, ct) is not { } s) return ApiError.Of(413, "too_large", "Uploads are at most 2 GB per file.");
                upload = (id, full, s.Size, s.Sha256);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException) // the browser went away, or the body isn't valid multipart
        {
            if (upload is { } partial) c.Transfers.Remove(partial.Id);
            return ApiError.Field("file", "The upload was cut off or malformed; nothing was written.");
        }
        if (upload is not { } u) return ApiError.Field("file", "No file was sent.");
        c.Transfers.Grant(u.Id, t!.Install.AgentId);
        return await RunAsync(t, new FileChangeSpec(u.Id, t.Space, FileOps.Write, u.Path, null, u.Size, u.Sha256, overwrite ?? false, null), c);
    }

    // A text save is a write over the file, only if it's still the version the editor loaded (Ruling 6).
    private static async Task<IResult> Save(SaveText body, Ctx c)
    {
        var (t, error) = await ResolveAsync(body.Install, body.Space, body.Path, true, c);
        if (error is not null) return error;
        if (body.Path.Length == 0) return ApiError.Field("path", "Name the file to save.");
        if (await LibraryGuardAsync(t!, FileOps.Write, body.Path, null, c) is { } guarded) return guarded;
        var bytes = (body.Bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(new UTF8Encoding(false).GetBytes(body.Text ?? "")).ToArray();
        if (bytes.Length > MaxText) return ApiError.Of(400, "too_large", "Text files are edited up to 1 MB.");
        var id = Guid.NewGuid();
        var (size, sha) = (await c.Transfers.SaveAsync(id, new MemoryStream(bytes)))!.Value;
        c.Transfers.Grant(id, t!.Install.AgentId);
        return await RunAsync(t, new FileChangeSpec(id, t.Space, FileOps.Write, body.Path, null, size, sha, true, body.Sha256), c);
    }

    private static async Task<IResult> ActAsync(FileAction body, string op, Ctx c)
    {
        var (t, error) = await ResolveAsync(body.Install, body.Space, body.Path, true, c);
        if (error is not null) return error;
        if (body.Target is { } target && FileNames.Problem(target) is { } problem) return ApiError.Field("target", problem);
        if (op is FileOps.Move or FileOps.Copy && string.IsNullOrEmpty(body.Target)) return ApiError.Field("target", "Name where it goes.");
        if (await LibraryGuardAsync(t!, op, body.Path, body.Target, c) is { } guarded) return guarded;
        return await RunAsync(t!, new FileChangeSpec(Guid.NewGuid(), t!.Space, op, body.Path, body.Target, 0, null, false, null), c);
    }

    private static async Task<IResult> RunAsync(Target t, FileChangeSpec spec, Ctx c)
    {
        var wait = TimeSpan.FromSeconds(c.Config.GetValue("Files:ChangeWaitSeconds", 600));
        FileChangeResult? result;
        try { result = await c.Changes.RunAsync(t.Install.AgentId, spec, wait); }
        catch (AgentUnavailableException) { return Offline(); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
        var (verb, done) = spec.Op switch
        {
            FileOps.Write when spec.ExpectSha256 is not null => ("file.saved", "Saved"),
            FileOps.Write => ("file.uploaded", "Uploaded"),
            FileOps.Mkdir => ("file.folderCreated", "Made the folder"),
            FileOps.Move => ("file.moved", "Moved"),
            FileOps.Copy => ("file.copied", "Copied"),
            FileOps.Delete => ("file.deleted", "Deleted"),
            _ => ("file.extracted", "Extracted"),
        };
        var where = $"{t.Def.Name}\\{spec.Path}" + (spec.Target is { } to ? $" to {to}" : "") + $" on {t.Install.Name}";
        if (result is null)
        {
            await c.Audit.RecordAsync(verb, $"{done} {where} (still running when the request ended)", "install", t.Install.Id.ToString(),
                new { space = t.Def.Name, path = spec.Path, target = spec.Target });
            return Results.Accepted(value: new { message = "The agent is still working on it; refresh the folder in a while." });
        }
        if (!result.Ok) return ApiError.Of(409, "file_failed", result.Error ?? "The change failed.");

        if (t.Def.Name == "mpmissions" && spec.Op == FileOps.Delete) await MarkRemovedAsync(t, spec.Path, c);
        if (t.Def.Name == "localmods") await RescanAsync(t, [spec.Path, spec.Target], c);
        await c.Audit.RecordAsync(verb, $"{done} {where}", "install", t.Install.Id.ToString(),
            new { space = t.Def.Name, path = spec.Path, target = spec.Target, backup = result.Backup });
        return Results.Ok(new { backup = result.Backup });
    }

    // Ruling 8: a PBO a library deployment wrote is changed through the library; deleting it is allowed and recorded on the deployments.
    private static async Task<IResult?> LibraryGuardAsync(Target t, string op, string path, string? target, Ctx c)
    {
        if (t.Def.Name != "mpmissions" || op is FileOps.Delete or FileOps.Mkdir or FileOps.Extract) return null;
        var names = await LibraryNamesAsync(t.Install.Id, c);
        var touched = op == FileOps.Copy ? [target] : new[] { path, target };
        return touched.Any(p => p is not null && !p.Contains('\\') && !p.Contains('/') && names.Contains(p))
            ? ApiError.Of(409, "library_managed", "That mission file is managed by the mission library. Change it there.")
            : null;
    }

    private static async Task<HashSet<string>> LibraryNamesAsync(Guid installId, Ctx c) =>
        (await c.Db.Deployments.Where(d => d.ServerInstallId == installId && d.Status != DeploymentStatus.Failed).Select(d => d.FileName).ToListAsync())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task MarkRemovedAsync(Target t, string path, Ctx c)
    {
        var rows = (await c.Db.Deployments.Where(d => d.ServerInstallId == t.Install.Id && d.Status != DeploymentStatus.Failed).ToListAsync())
            .Where(d => d.FileName.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var d in rows) (d.Status, d.Error) = (DeploymentStatus.Failed, "Removed in the file manager");
        if (rows.Count > 0) await c.Db.SaveChangesAsync();
    }

    // Ruling 10: a change inside a local mod's folder makes its addon scan stale.
    private static async Task RescanAsync(Target t, string?[] paths, Ctx c)
    {
        var folders = paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!.Split('\\', '/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = (await c.Db.Mods.Where(m => m.LocalFolder != null).ToListAsync()).Where(m => folders.Contains(m.LocalFolder!)).ToList();
        if (mods.Count == 0) return;
        foreach (var m in mods) m.ScanStatus = ScanStatus.NotScanned;
        await c.Db.SaveChangesAsync();
        await c.Scans.RequestAsync(t.Install.AgentId, mods, []);
    }

    private static IResult Pull(Guid id, FileTransfers transfers, HttpContext ctx) =>
        Guid.TryParse(ctx.User.FindFirst(AgentKeyAuthentication.AgentIdClaim)?.Value, out var agentId) && transfers.PathFor(id, agentId) is { } path
            ? Results.File(path, "application/octet-stream")
            : ApiError.Of(404, "not_found", "No such upload for this agent.");

    private static IResult Offline() => ApiError.Of(409, "agent_offline", "The server agent isn't connected.");
}
