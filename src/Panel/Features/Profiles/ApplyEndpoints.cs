using System.Security.Claims;
using ArmaFormats;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles.Config;
using Panel.Hubs;

namespace Panel.Features.Profiles;

public record ApplyPreview(int Revision, bool CanApply, string? Reason, FileDiff[] Files);
public record ApplyBody(int Revision);
public record ApplyDone(int Revision, string? BackupFolder);
public record MissionFile(string Name, long Size, DateTime ModifiedAt);
public record MissionsList(bool Connected, MissionFile[] Files);

// Apply (plan Ruling 1, 2): the head revision's files go onto the server through the agent, which backs up first.
// "Applied" is recorded only after the agent's re-read hashes match what was sent.
public static class ApplyEndpoints
{
    public static void MapApply(this WebApplication app)
    {
        var g = app.MapGroup("/api/profiles");
        g.MapGet("/{id:guid}/apply-preview", Preview).RequirePermission(Permissions.ProfilesApply);
        g.MapPost("/{id:guid}/apply", Apply).RequirePermission(Permissions.ProfilesApply);
        g.MapGet("/{id:guid}/missions", Missions).RequirePermission(Permissions.ProfilesView);
    }

    private sealed record Target(Profile Profile, Guid AgentId, string InstallPath);

    private static Task<Target?> FindAsync(AppDb db, Guid id) =>
        (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == id select new Target(p, i.AgentId, i.InstallPath))
            .FirstOrDefaultAsync();

    // Why Apply can't run now, or null.
    private static string? Blocked(AgentRegistry registry, Target t, Secrets? secrets)
    {
        if (!registry.IsConnected(t.AgentId)) return "The server agent isn't connected.";
        if (registry.Status(t.Profile.Id) is { State: not (ServerState.Offline or ServerState.Error) })
            return "Stop the server to apply; changes take effect the next time it starts.";
        if (secrets is null) return "The saved passwords can't be decrypted (the panel's encryption keys changed). Set them again and save.";
        return null;
    }

    private static Secrets SecretsIn(string? serverCfg)
    {
        if (string.IsNullOrEmpty(serverCfg)) return Secrets.None;
        try { return ConfigFiles.ParseServerCfg(serverCfg).Secrets ?? Secrets.None; }
        catch (ConfigSyntaxException) { return Secrets.None; }
    }

    private static async Task<IResult> Preview(Guid id, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents)
    {
        if (await FindAsync(db, id) is not { } t) return ApiError.Of(404, "not_found", "No such profile.");
        var head = await store.HeadNumberAsync(id);
        var secrets = store.LoadSecrets(t.Profile);
        var reason = Blocked(registry, t, secrets);
        var current = new ConfigSnapshot(null, null, null);
        if (registry.IsConnected(t.AgentId))
        {
            try { current = await agents.CallAsync(t.AgentId, c => c.ReadConfig(id, t.Profile.Slug)); }
            catch (AgentUnavailableException) { reason ??= "The server agent isn't connected."; }
            catch (HubException e) { reason ??= $"The agent couldn't read the current files: {e.Message}"; }
        }
        var proposed = ConfigFiles.Generate((await store.HeadAsync(t.Profile)).ToConfig(secrets ?? Secrets.None), SecretMode.Real);
        string newProfile;
        try { newProfile = ConfigDocument.Parse(current.Arma3Profile ?? "").ReplaceClass("DifficultyPresets", proposed.DifficultyPresets); }
        catch (ConfigSyntaxException e)
        {
            newProfile = proposed.DifficultyPresets;
            reason ??= $"The server's profile file doesn't parse ({e.Message}).";
        }
        FileDiff[] files =
        [
            new("server.cfg", current.ServerCfg is not null,
                LineDiff.Compute(ConfigFiles.MaskSecrets(current.ServerCfg ?? ""), ConfigFiles.MaskSecrets(proposed.ServerCfg, SecretsIn(current.ServerCfg)))),
            new("basic.cfg", current.BasicCfg is not null, LineDiff.Compute(current.BasicCfg ?? "", proposed.BasicCfg ?? "")),
            new($"{t.Profile.Slug}.Arma3Profile", current.Arma3Profile is not null, LineDiff.Compute(current.Arma3Profile ?? "", newProfile)),
        ];
        return Results.Ok(new ApplyPreview(head, reason is null, reason, files));
    }

    private static async Task<IResult> Apply(Guid id, ApplyBody body, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents,
        AuditLog audit, HttpContext ctx)
    {
        if (await FindAsync(db, id) is null) return ApiError.Of(404, "not_found", "No such profile.");
        var head = await store.HeadNumberAsync(id);
        if (body.Revision != head) return ApiError.Of(409, "stale", $"Revision {head} is the latest now. Look at the changes again before applying.");
        var revision = await db.ProfileRevisions.SingleAsync(r => r.ProfileId == id && r.Number == head);
        var userId = Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : (Guid?)null;
        var (error, backup) = await ApplyCoreAsync(db, store, registry, agents, audit, id, revision, userId);
        return error ?? Results.Ok(new ApplyDone(head, backup));
    }

    // Writes a revision's files through the agent and records it as applied once the agent's re-read hashes match.
    // Returns the error to answer with, or the backup folder.
    internal static async Task<(IResult? Error, string? BackupFolder)> ApplyCoreAsync(AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents,
        AuditLog audit, Guid id, ProfileRevision revision, Guid? userId)
    {
        if (await FindAsync(db, id) is not { } t) return (ApiError.Of(404, "not_found", "No such profile."), null);
        var p = t.Profile;
        var (snapshot, secrets) = store.Load(revision);
        if (!registry.IsConnected(t.AgentId)) return (ApiError.Of(409, "agent_offline", "The server agent isn't connected."), null);
        if (Blocked(registry, t, secrets) is { } reason)
            return (ApiError.Of(409, secrets is null ? "secrets_unreadable" : "running", reason), null);

        var files = ConfigFiles.Generate(snapshot.ToConfig(secrets!), SecretMode.Real);
        ApplyResult result;
        try { result = await agents.CallAsync(t.AgentId, c => c.ApplyConfig(new ConfigBundle(id, p.Slug, files.ServerCfg, files.BasicCfg, files.DifficultyPresets))); }
        catch (AgentUnavailableException) { return (ApiError.Of(409, "agent_offline", "The server agent isn't connected."), null); }
        catch (HubException e) { return (ApiError.Of(409, "apply_failed", e.Message), null); }
        if (!result.Ok) return (ApiError.Of(409, "apply_failed", result.Error ?? "The agent couldn't write the files."), null);

        string Hash(string name) => result.Files.FirstOrDefault(f => f.Name == name)?.Sha256 ?? "";
        var basicOk = files.BasicCfg is null ? result.Files.All(f => f.Name != "basic.cfg") : Hash("basic.cfg") == Sha(files.BasicCfg);
        if (Hash("server.cfg") != Sha(files.ServerCfg) || !basicOk)
            return (ApiError.Of(409, "apply_failed", "The files on the server don't match what was sent, so the revision wasn't recorded as applied."), null);

        p.AppliedRevisionId = revision.Id;
        p.AppliedAt = DateTime.UtcNow;
        p.AppliedById = userId;
        await db.SaveChangesAsync();
        await audit.RecordAsync("profile.applied", $"Applied server profile {p.Name} revision {revision.Number}", "profile", id.ToString(),
            new { revision = revision.Number, backupFolder = result.BackupFolder });
        return (null, result.BackupFolder);
    }

    internal static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    // The .pbo files in the server's mpmissions folder, for the rotation picker (Phase 4 brings the mission library).
    private static async Task<IResult> Missions(Guid id, AppDb db, AgentRegistry registry, AgentCalls agents)
    {
        if (await FindAsync(db, id) is not { } t) return ApiError.Of(404, "not_found", "No such profile.");
        var area = registry.Hello(t.AgentId)?.Installs.FirstOrDefault(i => i.Path.Equals(t.InstallPath, StringComparison.OrdinalIgnoreCase))?.Area;
        if (area is null) return Results.Ok(new MissionsList(false, []));
        try
        {
            var files = await agents.CallAsync(t.AgentId, c => c.ListFiles(area, "mpmissions", "*.pbo"));
            return Results.Ok(new MissionsList(true, files.OrderBy(f => f.Name).Select(f => new MissionFile(f.Name, f.Size, f.ModifiedAt)).ToArray()));
        }
        catch (AgentUnavailableException) { return Results.Ok(new MissionsList(false, [])); }
        catch (HubException) { return Results.Ok(new MissionsList(true, [])); }
    }
}
