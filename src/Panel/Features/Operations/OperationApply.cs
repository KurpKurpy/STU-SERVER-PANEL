using System.Security.Claims;
using Panel.Api;
using Panel.Audit;
using Panel.Data;
using Panel.Features.Missions;
using Panel.Features.Profiles;
using Panel.Features.Servers;
using Panel.Hubs;

namespace Panel.Features.Operations;

public sealed record ApplyBody(int? Players, int? Version);

// Phase 6a Rulings 11–12: Apply to server. A fresh check must leave nothing failing or unknown and every manual item ticked; then the
// operation's profile, mission and mod list go through Start Mission's shared core, under the same per-server lock.
public static class OperationApply
{
    private static async Task<(Operation? Op, IResult? Error)> FindAsync(Guid id, AppDb db)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return (null, ApiError.Of(404, "not_found", "No such operation."));
        if (op.Status != OperationStatus.Planned) return (null, ApiError.Of(409, "closed", "Only a planned operation can be applied."));
        if (op.ProfileId is null || op.MissionId is null || op.ModCollectionId is null)
            return (null, ApiError.Of(409, "not_ready", "Choose the mission, server profile and mod list first."));
        return (op, null);
    }

    // What Apply will do on that server, from Start Mission's own preview, with the server, operation and version it was made for: the
    // confirm shows these and sends the version back (final review I-3).
    public static async Task<IResult> Preview(Guid id, AppDb db, ProfileStore store, AgentRegistry registry)
    {
        var (op, error) = await FindAsync(id, db);
        if (error is not null) return error;
        var (p, planError) = await StartMission.PreviewAsync(op!.ProfileId!.Value, op.MissionId!.Value, op.ModCollectionId!.Value, db, store, registry);
        return p is null ? planError! : Results.Ok(new
        {
            p.Action, p.Players, p.ModListChanges, p.UnappliedChanges, p.Compat, p.FileName, p.ModListName, p.ProfileName,
            OperationName = op.Name, op.Version,
        });
    }

    public static async Task<IResult> Apply(Guid id, ApplyBody body, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents,
        ServerEndpoints.Control control, AuditLog audit, IConfiguration config, HttpContext ctx)
    {
        var (op, error) = await FindAsync(id, db);
        if (error is not null) return error;
        if (body.Version != op!.Version)
            return ApiError.Of(409, "stale", "This operation changed since you opened it. Look at it again before applying.");
        var results = await Readiness.EvaluateAsync(db, store, registry, agents, op!);
        var blockers = results.Where(r => !Readiness.PassesGate([r])).ToList();
        if (blockers.Count > 0)
            return ApiError.Of(409, "not_ready", "Not ready: " + string.Join("; ", blockers.Select(b => $"{b.Label} ({b.Detail})")));

        var started = await StartMission.RunAsync(op!.ProfileId!.Value, new StartMissionBody(op.MissionId!.Value, op.ModCollectionId!.Value, body.Players),
            db, store, registry, agents, control, audit, config, ctx, $"Operation {op.Name}");
        if (started is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted }) return started;

        (op.AppliedAt, op.AppliedById) = (DateTime.UtcNow, Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : null);
        op.Version++;
        await Readiness.PrepareAsync(db, store, registry, agents, op); // stores the check after Apply, with its time
        await audit.RecordAsync("operation.applied", $"Applied {op.Name} to its server", "operation", op.Id.ToString(), new { name = op.Name }, isPublic: true);
        return started;
    }
}
