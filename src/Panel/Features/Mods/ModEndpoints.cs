using System.Security.Claims;
using System.Text.Json;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Jobs;
using Panel.Hubs;
using Panel.Settings;

namespace Panel.Features.Mods;

public record ModDto(Guid Id, long? WorkshopId, string? LocalFolder, string Name, string Status, long SizeBytes, long? ExpectedBytes, DateTime? InstalledAt,
    DateTime? WorkshopUpdatedAt, DateTime? LatestWorkshopUpdate, bool UpdateAvailable, string? PreviewUrl, string[] KeyFiles, string? LastError, string[] Lists,
    string ScanStatus = "NotScanned", DateTime? ScannedAt = null, string? ScanNote = null, bool NotPublic = false);
public record PasteBody(string? Text);
public record InstallBody(Guid[]? ModIds, bool? All, string? Mode);
public record RefusedItem(long WorkshopId, string Reason);
public record PasteResult(ModDto[] Added, ModDto[] Existing, RefusedItem[] Refused, string[] Problems);

public static class ModLibrary
{
    public const int MaxText = 512 * 1024;

    // The first running server that loads any of these mods: enabled, non-optional items of its applied list. SteamCMD would
    // replace their files under the live server, or fail on the PBOs it holds open.
    public static async Task<(string Profile, string[] Mods)?> LoadedByRunningAsync(AppDb db, AgentRegistry registry, List<Guid> modIds)
    {
        var profiles = await db.Profiles.Where(p => p.AppliedRevisionId != null).Select(p => new { p.Id, p.Name, Revision = p.AppliedRevisionId!.Value }).ToListAsync();
        foreach (var p in profiles.Where(p => registry.Status(p.Id) is { State: not (ServerState.Offline or ServerState.Error) }))
        {
            if (ModLaunch.ListOf(await db.ProfileRevisions.Where(r => r.Id == p.Revision).Select(r => r.SnapshotJson).FirstAsync()) is not { } listId) continue;
            var names = await db.ModCollectionItems.Where(i => i.CollectionId == listId && i.Enabled && !i.Optional && modIds.Contains(i.ModId))
                .Select(i => i.Mod!.Name).ToListAsync();
            if (names.Count > 0) return (p.Name, names.Order().ToArray());
        }
        return null;
    }

    public static bool UpdateAvailable(Mod m) =>
        m.Status == ModStatus.Installed && m.LatestWorkshopUpdate is { } latest && (m.WorkshopUpdatedAt is null || latest > m.WorkshopUpdatedAt);

    // An unlisted mod (or a private or removed one): Steam's public lookup can't describe it, so it has no update time and a placeholder
    // name until its own files give it one. SteamCMD, signed in, can still download an unlisted mod; Update all fetches it every time.
    public static string Placeholder(long workshopId) => $"Workshop item {workshopId}";
    public static bool NotPublic(Mod m) => m.WorkshopId is not null && m.LatestWorkshopUpdate is null;

    public static void NameFromFiles(Mod m, string? name)
    {
        if (m.WorkshopId is { } id && m.Name == Placeholder(id) && !string.IsNullOrWhiteSpace(name)) m.Name = Clip(name.Trim(), 200)!;
    }

    public static string[] Keys(Mod m) => JsonSerializer.Deserialize<string[]>(m.KeyFilesJson) ?? [];

    public static async Task<ModDto[]> DtosAsync(AppDb db, IEnumerable<Mod> mods)
    {
        var list = mods.ToList();
        var ids = list.Select(m => m.Id).ToList();
        var lists = (await (from i in db.ModCollectionItems join c in db.ModCollections on i.CollectionId equals c.Id where ids.Contains(i.ModId)
                select new { i.ModId, c.Name }).ToListAsync())
            .ToLookup(x => x.ModId, x => x.Name);
        return list.Select(m => new ModDto(m.Id, m.WorkshopId, m.LocalFolder, m.Name, m.Status.ToString(), m.SizeBytes, m.ExpectedBytes, m.InstalledAt,
            m.WorkshopUpdatedAt, m.LatestWorkshopUpdate, UpdateAvailable(m), m.PreviewUrl, Keys(m), m.LastError, lists[m.Id].Order().ToArray(),
            m.ScanStatus.ToString(), m.ScannedAt, m.ScanNote, NotPublic(m))).ToArray();
    }

    // Adds the Workshop items not yet in the library (Not installed), refusing ones that aren't Arma 3 items (Ruling 10). One Steam's public
    // lookup can't see (unlisted, private or removed) is added under a placeholder name: SteamCMD can download an unlisted one, and the
    // download says so if it's private or gone.
    public static async Task<(List<Mod> Added, List<Mod> Existing, List<RefusedItem> Refused)> AddWorkshopAsync(AppDb db, IWorkshopClient workshop, IReadOnlyList<long> ids)
    {
        var existing = await db.Mods.Where(m => m.WorkshopId != null && ids.Contains(m.WorkshopId.Value)).ToListAsync();
        var fresh = ids.Distinct().Where(id => existing.All(m => m.WorkshopId != id)).ToList();
        var added = new List<Mod>();
        var refused = new List<RefusedItem>();
        if (fresh.Count > 0)
        {
            var details = await workshop.DetailsAsync(fresh);
            foreach (var id in fresh)
            {
                var d = details.FirstOrDefault(x => x.Id == id);
                if (d is { Ok: true } && d.AppId != JobKinds.GameAppId) refused.Add(new RefusedItem(id, "That isn't an Arma 3 Workshop item."));
                else
                {
                    var mod = d is { Ok: true }
                        ? new Mod
                        {
                            Id = Guid.NewGuid(), WorkshopId = id, Name = Clip(string.IsNullOrWhiteSpace(d.Title) ? Placeholder(id) : d.Title.Trim(), 200)!,
                            Status = ModStatus.NotInstalled, ExpectedBytes = d.FileSize, LatestWorkshopUpdate = d.TimeUpdated, WorkshopCheckedAt = DateTime.UtcNow, PreviewUrl = Clip(d.PreviewUrl, 500),
                        }
                        : new Mod { Id = Guid.NewGuid(), WorkshopId = id, Name = Placeholder(id), Status = ModStatus.NotInstalled, WorkshopCheckedAt = DateTime.UtcNow };
                    db.Mods.Add(mod);
                    added.Add(mod);
                }
            }
            await db.SaveChangesAsync();
        }
        return (added, existing, refused);
    }

    // The agent that runs the unit's server: the one with the first registered install (Phase 5a supports one).
    public static Task<Guid?> AgentAsync(AppDb db) => db.Installs.OrderBy(i => i.CreatedAt).Select(i => (Guid?)i.AgentId).FirstOrDefaultAsync();

    public static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

public static class JobKinds
{
    public const long GameAppId = 107410;
    public const string WorkshopDownload = "workshop.download", WorkshopRemove = "workshop.remove", ServerInstall = "server.install";
}

public static class ModEndpoints
{
    public static void MapMods(this WebApplication app)
    {
        var g = app.MapGroup("/api/mods");
        g.MapGet("", List).RequirePermission(Permissions.ModsView);
        g.MapPost("", Paste).RequirePermission(Permissions.ModsManage);
        g.MapPost("/refresh", Refresh).RequirePermission(Permissions.ModsManage);
        g.MapPost("/install", Install).RequirePermission(Permissions.ModsInstall);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.ModsManage);
        g.MapPost("/{id:guid}/scan", Rescan).RequirePermission(Permissions.ModsManage);
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<ModDto[]> List(AppDb db) => await ModLibrary.DtosAsync(db, await db.Mods.OrderBy(m => m.Name).ToListAsync());

    private static async Task<IResult> Paste(PasteBody body, AppDb db, IWorkshopClient workshop, AuditLog audit)
    {
        var text = body.Text ?? "";
        if (text.Length > ModLibrary.MaxText) return ApiError.Field("text", "Paste at most 512 KB.");
        var parsed = LauncherPreset.Parse(text);
        var ids = parsed.Mods.Where(m => m.WorkshopId is not null).Select(m => m.WorkshopId!.Value).Distinct().ToList();
        if (ids.Count == 0) return ApiError.Field("text", parsed.Problems.FirstOrDefault() ?? "No Workshop IDs or links found.");
        if (ids.Count > 500) return ApiError.Field("text", "Add at most 500 mods at once.");
        (List<Mod> Added, List<Mod> Existing, List<RefusedItem> Refused) result;
        try { result = await ModLibrary.AddWorkshopAsync(db, workshop, ids); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ApiError.Of(502, "steam_unreachable", "Steam's Workshop service couldn't be reached. Try again in a moment.");
        }
        if (result.Added.Count > 0)
            await audit.RecordAsync("mods.added", $"Added {result.Added.Count} mods to the library", "mods", null,
                new { workshopIds = result.Added.Select(m => m.WorkshopId) });
        return Results.Ok(new PasteResult(await ModLibrary.DtosAsync(db, result.Added), await ModLibrary.DtosAsync(db, result.Existing),
            result.Refused.ToArray(), parsed.Problems));
    }

    // Workshop details for every Workshop mod, then the agent's disk: what's there is Installed, what's gone is Missing.
    private static async Task<IResult> Refresh(AppDb db, IWorkshopClient workshop, AgentCalls agents, AuditLog audit)
    {
        var warnings = await RefreshAsync(db, workshop, agents);
        return Results.Ok(new { mods = await ModLibrary.DtosAsync(db, await db.Mods.OrderBy(m => m.Name).ToListAsync()), warnings });
    }

    // Phase 7b: "Check for updates", shared by the button and the scheduler; returns the warnings.
    internal static async Task<List<string>> RefreshAsync(AppDb db, IWorkshopClient workshop, AgentCalls agents)
    {
        var mods = await db.Mods.ToListAsync();
        var warnings = new List<string>();
        var workshopMods = mods.Where(m => m.WorkshopId is not null).ToList();
        if (workshopMods.Count > 0)
        {
            try
            {
                var details = (await workshop.DetailsAsync(workshopMods.Select(m => m.WorkshopId!.Value).ToList())).ToDictionary(d => d.Id);
                foreach (var m in workshopMods)
                    if (details.GetValueOrDefault(m.WorkshopId!.Value) is { Ok: true } d)
                    {
                        if (!string.IsNullOrWhiteSpace(d.Title)) m.Name = ModLibrary.Clip(d.Title.Trim(), 200)!;
                        (m.ExpectedBytes, m.LatestWorkshopUpdate, m.PreviewUrl) = (d.FileSize, d.TimeUpdated, ModLibrary.Clip(d.PreviewUrl, 500));
                        m.WorkshopCheckedAt = DateTime.UtcNow;
                    }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                warnings.Add("Steam's Workshop service couldn't be reached, so update checks are from the last refresh.");
            }
        }

        if (await ModLibrary.AgentAsync(db) is { } agentId)
        {
            try
            {
                var disk = await agents.CallAsync(agentId, c => c.ListMods());
                Sync(db, mods, disk);
            }
            catch (AgentUnavailableException) { warnings.Add("The server agent isn't connected, so install states weren't checked."); }
            catch (HubException e) { warnings.Add($"The agent couldn't list its mods: {e.Message}"); }
        }
        await db.SaveChangesAsync();
        return warnings;
    }

    private static void Sync(AppDb db, List<Mod> mods, DiskMod[] disk)
    {
        var busy = new[] { ModStatus.Queued, ModStatus.Installing };
        foreach (var m in mods.Where(m => !busy.Contains(m.Status)))
        {
            var found = disk.FirstOrDefault(d => m.WorkshopId is { } id ? d.Ref.WorkshopId == id
                : string.Equals(d.Ref.LocalFolder, m.LocalFolder, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                if (m.Status != ModStatus.Installed)
                {
                    m.Status = ModStatus.Installed;
                    m.InstalledAt ??= DateTime.UtcNow;
                    m.WorkshopUpdatedAt ??= m.LatestWorkshopUpdate; // installed outside the panel: assume it's current
                    m.LastError = null;
                }
                (m.SizeBytes, m.KeyFilesJson) = (found.SizeBytes, JsonSerializer.Serialize(found.KeyFiles));
                ModLibrary.NameFromFiles(m, found.Name);
            }
            else if (m.Status == ModStatus.Installed) m.Status = ModStatus.Missing;
        }
        foreach (var d in disk.Where(d => d.Ref.LocalFolder is { } f && ModRef.Problem(d.Ref) is null
                     && !mods.Any(m => string.Equals(m.LocalFolder, f, StringComparison.OrdinalIgnoreCase))))
            db.Mods.Add(new Mod
            {
                Id = Guid.NewGuid(), LocalFolder = d.Ref.LocalFolder, Name = ModLibrary.Clip(d.Name, 200)!, Status = ModStatus.Installed,
                SizeBytes = d.SizeBytes, InstalledAt = DateTime.UtcNow, KeyFilesJson = JsonSerializer.Serialize(d.KeyFiles),
            });
    }

    private static async Task<IResult> Install(InstallBody body, AppDb db, AppSettingsService settings, JobService jobs, AgentRegistry registry, AuditLog audit,
        HttpContext ctx, Panel.Features.Backups.BackupService backups)
    {
        var (job, error) = await QueueInstallAsync(body, db, settings, jobs, registry, audit, backups, UserId(ctx));
        return error ?? Results.Ok(JobDto.From(job!));
    }

    // Phase 7b: the button and the scheduler share this.
    internal static async Task<(Job? Job, IResult? Error)> QueueInstallAsync(InstallBody body, AppDb db, AppSettingsService settings, JobService jobs,
        AgentRegistry registry, AuditLog audit, Panel.Features.Backups.BackupService backups, Guid? userId)
    {
        var mode = (body.Mode ?? "install").ToLowerInvariant();
        if (mode is not ("install" or "update" or "reinstall")) return (null, ApiError.Field("mode", "Mode is install, update or reinstall."));
        if (await settings.SteamUsernameAsync() is not { } steamUser)
            return (null, ApiError.Of(409, "steam_user", "Set the Steam account first (Mods page, Steam account): Workshop downloads need an account that owns Arma 3."));
        if (await ModLibrary.AgentAsync(db) is not { } agentId) return (null, ApiError.Of(409, "no_install", "Register a server install first."));

        var busy = new[] { ModStatus.Queued, ModStatus.Installing };
        var candidates = await db.Mods.Where(m => m.WorkshopId != null && !busy.Contains(m.Status)).ToListAsync();
        List<Mod> selection;
        if (body.ModIds is { Length: > 0 } ids) selection = candidates.Where(m => ids.Contains(m.Id)).ToList(); // explicit: download whatever state
        else if (body.All == true)
            selection = mode switch
            {
                "install" => candidates.Where(m => m.Status is ModStatus.NotInstalled or ModStatus.Failed or ModStatus.Missing).ToList(),
                "update" => candidates.Where(m => ModLibrary.UpdateAvailable(m) || (m.Status == ModStatus.Installed && ModLibrary.NotPublic(m))).ToList(),
                _ => candidates.Where(m => m.Status == ModStatus.Installed).ToList(),
            };
        else return (null, ApiError.Field("modIds", "Pick mods, or all."));
        if (selection.Count == 0) return (null, ApiError.Of(400, "nothing_to_do", "There's nothing to download."));
        if (selection.Count > 500) return (null, ApiError.Field("modIds", "Download at most 500 mods at once."));
        if (await ModLibrary.LoadedByRunningAsync(db, registry, selection.Select(m => m.Id).ToList()) is { } running)
            return (null, ApiError.Of(409, "running", $"Stop {running.Profile} first: it loads {string.Join(", ", running.Mods)}, and SteamCMD can't replace the files of a running server."));

        foreach (var m in selection) (m.Status, m.LastError) = (ModStatus.Queued, null);
        await db.SaveChangesAsync();
        // Phase 7b (plan Ruling 3): updating mods that are in use gets a server backup first; a first-time install changes nothing that exists.
        if (mode == "update") await backups.StartAsync(BackupType.Server, BackupKind.PreUpdate, agentId, userId);
        var spec = new JobSpec(Guid.NewGuid(), JobKind.WorkshopDownload, steamUser, null, null,
            selection.Select(m => new WorkshopItem(m.WorkshopId!.Value, m.ExpectedBytes)).ToArray());
        var job = await jobs.CreateAsync(agentId, spec, JobKinds.WorkshopDownload, Permissions.ModsInstall, "mods", null, userId,
            new { mode, modIds = selection.Select(m => m.Id) });
        await audit.RecordAsync("mods.install", $"Started downloading {selection.Count} mods ({mode})", "job", job.Id.ToString(),
            new { mode, workshopIds = selection.Select(m => m.WorkshopId) }, actorId: userId);
        return (job, null);
    }

    private static async Task<IResult> Delete(Guid id, bool? files, AppDb db, JobService jobs, AuditLog audit, HttpContext ctx)
    {
        var mod = await db.Mods.FindAsync(id);
        if (mod is null) return ApiError.Of(404, "not_found", "No such mod.");
        var lists = await (from i in db.ModCollectionItems join c in db.ModCollections on i.CollectionId equals c.Id where i.ModId == id select c.Name).ToListAsync();
        if (lists.Count > 0) return ApiError.Of(409, "in_use", $"It's in these mod lists: {string.Join(", ", lists.Order())}. Take it out of them first.");
        if (files == true && mod.WorkshopId is { } workshopId)
        {
            if (await ModLibrary.AgentAsync(db) is not { } agentId) return ApiError.Of(409, "no_install", "Register a server install first.");
            var spec = new JobSpec(Guid.NewGuid(), JobKind.WorkshopRemove, null, null, null, [new WorkshopItem(workshopId, null)]);
            var job = await jobs.CreateAsync(agentId, spec, JobKinds.WorkshopRemove, Permissions.ModsManage, "mods", mod.Id.ToString(), UserId(ctx),
                new { modIds = new[] { mod.Id } });
            await audit.RecordAsync("mods.filesRemoved", $"Started removing {mod.Name} and its files", "mod", mod.Id.ToString(), new { workshopId });
            return Results.Ok(JobDto.From(job)); // the mod leaves the library when its files are gone
        }
        db.Mods.Remove(mod);
        await db.SaveChangesAsync();
        await audit.RecordAsync("mods.removed", $"Removed {mod.Name} from the library", "mod", id.ToString());
        return Results.NoContent();
    }

    // Phase 5b: read the mod's addons and maps again (after copying in a local mod, say).
    private static async Task<IResult> Rescan(Guid id, AppDb db, AddonScans scans, AuditLog audit)
    {
        if (await db.Mods.FindAsync(id) is not { } mod) return ApiError.Of(404, "not_found", "No such mod.");
        if (mod.Status != ModStatus.Installed) return ApiError.Of(409, "not_installed", "Install the mod first.");
        if (await ModLibrary.AgentAsync(db) is not { } agentId) return ApiError.Of(409, "no_install", "Register a server install first.");
        if (await scans.RequestAsync(agentId, [mod], []) is { } problem) return ApiError.Of(409, "agent_refused", problem);
        await audit.RecordAsync("mods.scan", $"Asked for a scan of {mod.Name}", "mod", id.ToString());
        return Results.Accepted();
    }
}

// What a finished Workshop job does to the library.
public sealed class ModJobHandler : IJobResultHandler
{
    public bool Handles(string jobType) => jobType is JobKinds.WorkshopDownload or JobKinds.WorkshopRemove;

    public async Task HandleAsync(Job job, JobResult result, AppDb db)
    {
        var modIds = JsonDocument.Parse(job.PayloadJson).RootElement.TryGetProperty("modIds", out var ids)
            ? ids.EnumerateArray().Select(i => i.GetGuid()).ToList() : [];
        var mods = await db.Mods.Where(m => modIds.Contains(m.Id)).ToListAsync();
        foreach (var mod in mods)
        {
            var item = (result.Items ?? []).FirstOrDefault(i => i.WorkshopId == mod.WorkshopId);
            if (job.Type == JobKinds.WorkshopRemove)
            {
                if (item is { Ok: true } && !await db.ModCollectionItems.AnyAsync(i => i.ModId == mod.Id)) db.Mods.Remove(mod);
                else mod.LastError = item?.Error ?? job.Error;
                continue;
            }
            if (item is { Ok: true })
            {
                (mod.Status, mod.InstalledAt, mod.SizeBytes, mod.KeyFilesJson, mod.LastError) =
                    (ModStatus.Installed, DateTime.UtcNow, item.SizeBytes, JsonSerializer.Serialize(item.KeyFiles ?? []), null);
                mod.WorkshopUpdatedAt = mod.LatestWorkshopUpdate ?? DateTime.UtcNow;
                ModLibrary.NameFromFiles(mod, item.Name);
                mod.ScanStatus = ScanStatus.NotScanned; // new files: the hub's rescan after JobFinished reads them (Phase 5b Ruling 6)
            }
            else
                // A failed update or reinstall leaves the old files in place: still installed, with the error shown.
                (mod.Status, mod.LastError) = (mod.InstalledAt is null ? ModStatus.Failed : ModStatus.Installed,
                    ModLibrary.Clip(item?.Error ?? job.Error ?? "The download failed.", 1000));
        }
        await db.SaveChangesAsync();
    }
}
