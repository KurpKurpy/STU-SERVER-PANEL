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

public record SteamCmdBody(string? Branch);
public record DeployKeysBody(Guid? CollectionId);
public record CleanupKeysBody(Guid[]? CollectionIds);
public record KeyDto(string Name, long Size, string Kind, string[] Mods);

// What a server install gets from SteamCMD and its keys folder (Phase 5a Rulings 8, 9 and 15).
public static class InstallTools
{
    private static readonly string[] Branches = ["public", "creatordlc", "profiling"];

    public static void MapInstallTools(this WebApplication app)
    {
        var g = app.MapGroup("/api/installs/{id:guid}");
        g.MapPost("/steamcmd", InstallServer).RequirePermission(Permissions.ServerUpdate);
        g.MapPost("/scan", Rescan).RequirePermission(Permissions.ServerUpdate);
        g.MapGet("/keys", ListKeys).RequirePermission(Permissions.ModsView);
        g.MapPost("/keys/deploy", Deploy).RequirePermission(Permissions.KeysManage);
        g.MapPost("/keys/cleanup", Cleanup).RequirePermission(Permissions.KeysManage);
        // Names only, for the keys panel: the full install list (paths, areas) stays with profiles.manage.
        app.MapGet("/api/mods/installs", async (AppDb db) =>
                await db.Installs.OrderBy(i => i.CreatedAt).Select(i => new { i.Id, i.Name }).ToListAsync())
            .RequirePermission(Permissions.ModsView);
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such install.");

    // Phase 5b: read the install's base-game and DLC addons again.
    private static async Task<IResult> Rescan(Guid id, AppDb db, AddonScans scans, AuditLog audit)
    {
        if (await db.Installs.FindAsync(id) is not { } install) return NotFound();
        if (await scans.RequestAsync(install.AgentId, [], [install]) is { } problem) return ApiError.Of(409, "agent_refused", problem);
        await audit.RecordAsync("install.scan", $"Asked for a scan of {install.Name}", "install", id.ToString());
        return Results.Accepted();
    }

    private static async Task<IResult> InstallServer(Guid id, SteamCmdBody body, AppDb db, AgentRegistry registry, AppSettingsService settings, JobService jobs,
        AuditLog audit, HttpContext ctx, Panel.Features.Backups.BackupService backups)
    {
        var (job, error) = await QueueServerUpdateAsync(id, body.Branch, db, registry, settings, jobs, backups, audit, UserId(ctx));
        return error ?? Results.Ok(JobDto.From(job!));
    }

    // Phase 7b: the button and the scheduler share this; branch null means the branch this install was last updated on (else public).
    internal static async Task<(Job? Job, IResult? Error)> QueueServerUpdateAsync(Guid id, string? requestedBranch, AppDb db, AgentRegistry registry,
        AppSettingsService settings, JobService jobs, Panel.Features.Backups.BackupService backups, AuditLog audit, Guid? userId)
    {
        var install = await db.Installs.FindAsync(id);
        if (install is null) return (null, NotFound());
        var branch = (requestedBranch ?? await LastBranchAsync(db, id) ?? "public").ToLowerInvariant();
        if (!Branches.Contains(branch)) return (null, ApiError.Field("branch", "The branch is public, creatordlc or profiling."));
        if (await settings.SteamUsernameAsync() is not { } steamUser)
            return (null, ApiError.Of(409, "steam_user", "Set the Steam account first: the dedicated server needs an account that owns Arma 3."));
        // SteamCMD rewrites the server's files: never under a running server.
        var profiles = await db.Profiles.Where(p => p.ServerInstallId == id).Select(p => new { p.Id, p.Name }).ToListAsync();
        if (profiles.FirstOrDefault(p => registry.Status(p.Id) is { State: not (ServerState.Offline or ServerState.Error) }) is { } running)
            return (null, ApiError.Of(409, "running", $"Stop {running.Name} first: SteamCMD can't update the files of a running server."));
        // Phase 7b: a server backup first; the agent's one-at-a-time job queue runs it before the update.
        await backups.StartAsync(BackupType.Server, BackupKind.PreUpdate, install.AgentId, userId);
        var spec = new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, steamUser, install.InstallPath, branch, []);
        var job = await jobs.CreateAsync(install.AgentId, spec, JobKinds.ServerInstall, Permissions.ServerUpdate, "install", id.ToString(), userId, new { branch });
        await audit.RecordAsync("server.install", $"Started installing or updating {install.Name} ({branch})", "install", id.ToString(), actorId: userId);
        return (job, null);
    }

    // The branch of the install's last SteamCMD job, so a scheduled update never switches it.
    private static async Task<string?> LastBranchAsync(AppDb db, Guid installId)
    {
        var target = installId.ToString();
        var payload = await db.Jobs.Where(j => j.Type == JobKinds.ServerInstall && j.TargetId == target).OrderByDescending(j => j.CreatedAt)
            .Select(j => j.PayloadJson).FirstOrDefaultAsync();
        if (payload is null) return null;
        try { return JsonDocument.Parse(payload).RootElement.TryGetProperty("branch", out var b) ? b.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private static async Task<IResult> ListKeys(Guid id, AppDb db, AgentCalls agents)
    {
        var install = await db.Installs.FindAsync(id);
        if (install is null) return NotFound();
        KeyFile[] keys;
        try { keys = await agents.CallAsync(install.AgentId, c => c.ListKeys(install.InstallPath)); }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        catch (HubException e) { return ApiError.Of(400, "agent_refused", e.Message); }
        var providers = (await db.Mods.Select(m => new { m.Name, m.KeyFilesJson }).ToListAsync())
            .SelectMany(m => (JsonSerializer.Deserialize<string[]>(m.KeyFilesJson) ?? []).Select(k => (Key: k, m.Name)))
            .ToLookup(x => x.Key, x => x.Name, StringComparer.OrdinalIgnoreCase);
        return Results.Ok(new
        {
            keys = keys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Select(k => new KeyDto(k.Name, k.Size,
                k.Name.Equals("a3.bikey", StringComparison.OrdinalIgnoreCase) ? "a3" : providers[k.Name].Any() ? "mod" : "unknown",
                providers[k.Name].Distinct().Order().ToArray())),
        });
    }

    private static async Task<(ServerInstall? Install, ModRef[] Refs, IResult? Error)> PrepareAsync(Guid id, IEnumerable<Guid> listIds, AppDb db)
    {
        var install = await db.Installs.FindAsync(id);
        if (install is null) return (null, [], NotFound());
        var refs = new List<ModRef>();
        foreach (var listId in listIds.Distinct())
        {
            if (await ModLaunch.ResolveAsync(db, listId) is not { } mods) return (null, [], ApiError.Field("collectionId", "Pick an existing mod list."));
            if (mods.NotInstalled.Length > 0)
                return (null, [], ApiError.Of(409, "mods_missing", $"These mods in {mods.ListName} aren't installed: {string.Join(", ", mods.NotInstalled)}."));
            refs.AddRange(mods.Keys.Where(r => !refs.Contains(r)));
        }
        return (install, refs.ToArray(), null);
    }

    private static async Task<IResult> Deploy(Guid id, DeployKeysBody body, AppDb db, AgentCalls agents, AuditLog audit)
    {
        if (body.CollectionId is not { } listId) return ApiError.Field("collectionId", "Pick a mod list.");
        var (install, refs, error) = await PrepareAsync(id, [listId], db);
        if (error is not null) return error;
        return await CallAsync(install!, agents, audit, "keys.deployed", c => c.DeployKeys(install!.InstallPath, refs));
    }

    // Removes the keys of library mods that the chosen lists don't use, after a backup (Ruling 8). Keys no library mod provides —
    // a3.bikey, Creator DLC keys, hand-added ones — are never named, so they always stay.
    private static async Task<IResult> Cleanup(Guid id, CleanupKeysBody body, AppDb db, AgentCalls agents, AuditLog audit)
    {
        if (body.CollectionIds is not { Length: > 0 } listIds) return ApiError.Field("collectionIds", "Pick the mod lists whose keys stay.");
        var install = await db.Installs.FindAsync(id);
        if (install is null) return NotFound();
        var lists = listIds.Distinct().ToList();
        if (await db.ModCollections.CountAsync(c => lists.Contains(c.Id)) != lists.Count) return ApiError.Field("collectionIds", "Pick existing mod lists.");
        var keptMods = await db.ModCollectionItems.Where(i => lists.Contains(i.CollectionId) && i.Enabled).Select(i => i.ModId).ToListAsync();
        var keyFiles = await db.Mods.Select(m => new { m.Id, m.KeyFilesJson }).ToListAsync();
        string[] Keys(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
        var keep = new HashSet<string>(keyFiles.Where(m => keptMods.Contains(m.Id)).SelectMany(m => Keys(m.KeyFilesJson)), StringComparer.OrdinalIgnoreCase) { "a3.bikey" };
        var remove = keyFiles.SelectMany(m => Keys(m.KeyFilesJson)).Where(k => !keep.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return await CallAsync(install, agents, audit, "keys.cleaned", c => c.CleanupKeys(install.InstallPath, remove));
    }

    private static async Task<IResult> CallAsync(ServerInstall install, AgentCalls agents, AuditLog audit, string action, Func<IAgentClient, Task<KeysResult>> call)
    {
        KeysResult result;
        try { result = await agents.CallAsync(install.AgentId, call); }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        catch (HubException e) { return ApiError.Of(400, "agent_refused", e.Message); }
        if (!result.Ok) return ApiError.Of(409, "keys_failed", result.Error ?? "The agent couldn't change the keys.");
        if (result.Added.Length + result.Removed.Length > 0)
            await audit.RecordAsync(action, $"{(action == "keys.deployed" ? "Deployed" : "Cleaned up")} mod keys on {install.Name}", "install", install.Id.ToString(),
                new { added = result.Added, removed = result.Removed, backup = result.BackupFolder });
        return Results.Ok(new { added = result.Added, removed = result.Removed, backupFolder = result.BackupFolder });
    }
}
