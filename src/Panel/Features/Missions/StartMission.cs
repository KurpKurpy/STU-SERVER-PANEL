using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles;
using Panel.Features.Profiles.Config;
using Panel.Features.Servers;
using Panel.Hubs;

namespace Panel.Features.Missions;

public sealed record StartMissionBody(Guid MissionId, Guid ModListId, int? Players);
public sealed record StartMissionPreview(string Action, int? Players, bool ModListChanges, bool UnappliedChanges, CompatResult Compat,
    string FileName, string ModListName, string ProfileName);

// Phase 5b (design §5.4, Rulings 11–12): a Zeus or mission maker starts a mission the server can run. One scoped action: a revision
// with a one-mission rotation and the chosen mod list, applied, then Start. A running server is stopped before the files are written,
// since the agent refuses to write them while it runs (Task 7, live). It changes nothing else on the profile and grants no other control.
public static class StartMission
{
    public static void MapStartMission(this WebApplication app)
    {
        app.MapGet("/api/servers/{id:guid}/start-mission", Preview).RequirePermission(Permissions.MissionsStart);
        app.MapPost("/api/servers/{id:guid}/start-mission", Start).RequirePermission(Permissions.MissionsStart);
    }

    private sealed record Plan(Profile Profile, ServerInstall Install, ServerMissionsEndpoints.OnServer Mission, ModCollection List, CompatResult Compat,
        string Action, ServerState? State, int? Players, bool ModListChanges, bool UnappliedChanges, int Head);

    // One Start Mission per server at a time: two at once could boot one request's server.cfg with the other's mods (final review I-5).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    private static async Task<(Plan? Plan, IResult? Error)> PlanAsync(Guid id, Guid missionId, Guid modListId, AppDb db, ProfileStore store, AgentRegistry registry)
    {
        if (await db.Profiles.FindAsync(id) is not { } p || await db.Installs.FindAsync(p.ServerInstallId) is not { } install)
            return (null, ApiError.Of(404, "not_found", "No such server."));
        if (await db.ModCollections.FindAsync(modListId) is not { } list) return (null, ApiError.Field("modListId", "Pick a mod list."));
        if ((await ServerMissionsEndpoints.OnServerAsync(db, p.ServerInstallId, missionId)).FirstOrDefault() is not { } on)
            return (null, ApiError.Of(409, "not_on_server", "That mission isn't on this server (it hasn't been written here, or it's archived)."));
        var compat = Compatibility.Check(on.Required, on.Mission.Map, await Compatibility.ProvidedAsync(db, p.ServerInstallId, modListId));
        var status = registry.Status(p.Id);
        var running = status is { State: not (ServerState.Offline or ServerState.Error) };
        var appliedNumber = p.AppliedRevisionId is { } revId ? (await db.ProfileRevisions.FindAsync(revId))?.Number : null;
        var head = await store.HeadNumberAsync(p.Id);
        return (new Plan(p, install, on, list, compat, running ? "restart" : "start", status?.State, running ? status!.Query?.Players : null,
            await ServerMissionsEndpoints.AppliedListAsync(db, store, p) != modListId, appliedNumber != head, head), null);
    }

    internal static async Task<(StartMissionPreview? Preview, IResult? Error)> PreviewAsync(Guid id, Guid missionId, Guid modListId, AppDb db,
        ProfileStore store, AgentRegistry registry)
    {
        var (plan, error) = await PlanAsync(id, missionId, modListId, db, store, registry);
        return plan is null ? (null, error) : (new StartMissionPreview(plan.Action, plan.Players, plan.ModListChanges, plan.UnappliedChanges, plan.Compat,
            plan.Mission.Mission.FileName, plan.List.Name, plan.Profile.Name), null);
    }

    private static async Task<IResult> Preview(Guid id, Guid missionId, Guid modListId, AppDb db, ProfileStore store, AgentRegistry registry)
    {
        var (preview, error) = await PreviewAsync(id, missionId, modListId, db, store, registry);
        return preview is null ? error! : Results.Ok(preview);
    }

    private static Task<IResult> Start(Guid id, StartMissionBody body, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents,
        ServerEndpoints.Control control, AuditLog audit, IConfiguration config, HttpContext ctx) =>
        RunAsync(id, body, db, store, registry, agents, control, audit, config, ctx, "Start Mission");

    // The shared core (Phase 6a Ruling 12): Start Mission and an operation's Apply run here, under one lock per server, so they never
    // interleave on the same server. Source names the caller in the revision note.
    internal static async Task<IResult> RunAsync(Guid id, StartMissionBody body, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents,
        ServerEndpoints.Control control, AuditLog audit, IConfiguration config, HttpContext ctx, string source)
    {
        var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) return ApiError.Of(409, "busy", "Another Start Mission or operation Apply is running on this server. Try again in a moment.");
        try { return await StartLockedAsync(id, body, db, store, registry, agents, control, audit, config, ctx, source); }
        finally { gate.Release(); }
    }

    private static async Task<IResult> StartLockedAsync(Guid id, StartMissionBody body, AppDb db, ProfileStore store, AgentRegistry registry,
        AgentCalls agents, ServerEndpoints.Control control, AuditLog audit, IConfiguration config, HttpContext ctx, string source)
    {
        var (plan, error) = await PlanAsync(id, body.MissionId, body.ModListId, db, store, registry);
        if (plan is null) return error!;
        var (p, mission) = (plan.Profile, plan.Mission.Mission);
        if (plan.Compat.Status == Compatibility.Missing)
            return ApiError.Of(409, "missing", $"{mission.DisplayName} can't run with {plan.List.Name}: "
                + string.Join(", ", plan.Compat.Missing.Select(m => m.Addon).Concat(plan.Compat.MissingMap is { } map ? [$"the map {map}"] : []).Take(8)) + ".");
        if (plan.Action == "restart" && body.Players != plan.Players)
            return ApiError.Of(409, "stale_players", $"The player count changed (now {plan.Players?.ToString() ?? "unknown"}). Look again before starting.");
        if (plan.State is ServerState.Starting or ServerState.Stopping)
            return ApiError.Of(409, "busy", "The server is starting or stopping. Try again in a moment.");
        if (store.LoadSecrets(p) is not { } secrets)
            return ApiError.Of(409, "secrets_unreadable", "The saved passwords can't be decrypted (the panel's encryption keys changed). An admin must set them again first.");
        // Before its hello the panel can't tell a running server from a stopped one (Phase 6a final review I-1).
        if (registry.Hello(plan.Install.AgentId) is null)
            return ApiError.Of(409, "agent_offline", registry.IsConnected(plan.Install.AgentId)
                ? "The server agent hasn't reported its servers yet. Try again in a few seconds." : "The server agent isn't connected.");
        // What Start itself would refuse, checked before anything is saved (final review I-1).
        if (await ServerEndpoints.LaunchBlockedAsync(db, plan.Install, body.ModListId) is { } blocked) return blocked;

        var head = await store.HeadAsync(p);
        var template = mission.FileName.EndsWith(".pbo", StringComparison.OrdinalIgnoreCase) ? mission.FileName[..^4] : mission.FileName;
        var snapshot = head with
        {
            // A hand-written class Missions wins over the rotation when the files are generated (final review I-3).
            Settings = head.Settings with { MissionsOverride = null },
            Rotation = [new RotationItem(template, head.Rotation.FirstOrDefault()?.Difficulty is { Length: > 0 } d ? d : "Regular", "")],
            ModCollectionId = body.ModListId,
        };
        var userId = Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : (Guid?)null;
        if (await store.HeadNumberAsync(p.Id) != plan.Head) return ApiError.Of(409, "stale", "The server's profile changed meanwhile. Look again before starting.");
        ProfileRevision revision;
        var note = $"{source}: {template} with {plan.List.Name}";
        try { revision = await store.SaveRevisionAsync(p, snapshot, secrets, note.Length > 200 ? note[..200] : note, userId); }
        catch (DbUpdateException) { return ApiError.Of(409, "stale", "The server's profile changed meanwhile. Look again before starting."); }

        // The agent writes no files while a server runs (Arma rewrites its profile when it exits), so a running server is stopped
        // first and started again afterwards (Task 7, live). Its rewrite at exit then comes before the new files.
        var restart = plan.Action == "restart";
        if (restart)
        {
            var stopped = await control.RunAsync(p.Id, ServerEndpoints.Action.Stop);
            if (stopped is not Accepted) return stopped; // still running, nothing written
            var wait = TimeSpan.FromSeconds(config.GetValue("StartMission:StopWaitSeconds", 150)); // above the agent's own 60 s + 60 s for headless clients
            if (await WaitStoppedAsync(registry, p.Id, plan.Install.AgentId, wait) is { } notStopped) return notStopped;
        }
        var (applyError, _) = await ApplyEndpoints.ApplyCoreAsync(db, store, registry, agents, audit, p.Id, revision, userId);
        if (applyError is not null)
        {
            if (!restart) return applyError;
            // A Zeus can't start the server by hand, so the previous applied settings go back up.
            var again = await control.RunAsync(p.Id, ServerEndpoints.Action.Start);
            return ApiError.Of(409, CodeOf(applyError) ?? "apply_failed", $"{MessageOf(applyError)} " + (again is Accepted
                ? "The server was started again with its previous settings."
                : $"The server is stopped, and starting it with its previous settings failed too: {MessageOf(again)}"));
        }
        var started = await control.RunAsync(p.Id, ServerEndpoints.Action.Start);
        if (started is not Accepted) // the revision is saved and applied; the error says why it didn't start
            return restart ? ApiError.Of(409, CodeOf(started) ?? "start_failed", $"The server is stopped: {MessageOf(started)}") : started;
        await audit.RecordAsync("mission.started", $"Started {mission.DisplayName} ({template}) on {p.Name} with {plan.List.Name}", "profile", p.Id.ToString(),
            new { mission = template, version = plan.Mission.Version.Number, list = plan.List.Name, action = plan.Action, revision = revision.Number }, isPublic: true);
        return Results.Accepted(value: new { revision = revision.Number, action = plan.Action });
    }

    // Until the agent reports the server offline (it removes it from its running list in the same step), or the wait runs out.
    private static async Task<IResult?> WaitStoppedAsync(AgentRegistry registry, Guid profileId, Guid agentId, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (registry.Status(profileId) is { State: not (ServerState.Offline or ServerState.Error) })
        {
            if (!registry.IsConnected(agentId)) return ApiError.Of(409, "agent_offline", "The server agent disconnected while the server was stopping. Nothing new was written.");
            if (DateTime.UtcNow > until)
                return ApiError.Of(409, "stop_timeout", $"The server didn't stop within {wait.TotalSeconds:0} seconds, so nothing new was written. The agent closes it by force soon after; then start the mission again.");
            await Task.Delay(200);
        }
        return null;
    }

    private static JsonElement? Body(IResult r) => r is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : null;
    private static string? CodeOf(IResult r) => Body(r) is { } b && b.TryGetProperty("code", out var c) ? c.GetString() : null;
    private static string MessageOf(IResult r) => Body(r) is { } b && b.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
}
