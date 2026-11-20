using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Hubs;
using Panel.Storage;

namespace Panel.Features.Missions;

public record DeploymentDto(Guid InstallId, string InstallName, string Status, int Version, string? Error, string? BackupPath, DateTime CreatedAt, DateTime? DeployedAt);

// Every saved or restored version goes onto every install (Phase 4a Rulings 9–11): a Deployment row per install, sent to its agent, which
// pulls the PBO by hash. A row stays Pending while the agent is offline and is sent again on its hello, so a restart never strands it.
public sealed partial class MissionDeployments(AppDb db, AgentRegistry registry, AgentCalls agents, AuditLog audit, Panel.Features.Notifications.Notifier notifier)
{
    private const string ReadOnlyReason = "Read-only on the agent (Agent:ReadOnlyInstallRoots), so missions aren't written there.";

    public async Task DeployAsync(Mission m, MissionVersion v, Guid? userId)
    {
        var rows = new List<Deployment>();
        foreach (var install in await db.Installs.OrderBy(i => i.CreatedAt).ToListAsync())
        {
            foreach (var old in await db.Deployments.Where(d => d.MissionId == m.Id && d.ServerInstallId == install.Id
                         && (d.Status == DeploymentStatus.Pending || d.Status == DeploymentStatus.Waiting)).ToListAsync())
                (old.Status, old.Error) = (DeploymentStatus.Failed, $"Replaced by version {v.Number}.");
            var d = new Deployment
            {
                Id = Guid.NewGuid(), MissionId = m.Id, MissionVersionId = v.Id, ServerInstallId = install.Id, FileName = m.FileName,
                Status = DeploymentStatus.Pending, DeployedById = userId,
            };
            if (ReadOnly(install)) (d.Status, d.Error) = (DeploymentStatus.Failed, ReadOnlyReason);
            db.Deployments.Add(d);
            rows.Add(d);
        }
        await db.SaveChangesAsync();
        foreach (var d in rows.Where(r => r.Status == DeploymentStatus.Pending)) await SendAsync(d);
    }

    private bool ReadOnly(ServerInstall install) =>
        registry.Hello(install.AgentId)?.Installs.FirstOrDefault(i => i.Path.Equals(install.InstallPath, StringComparison.OrdinalIgnoreCase))?.ReadOnly == true;

    private async Task SendAsync(Deployment d)
    {
        var install = await db.Installs.FindAsync(d.ServerInstallId);
        var version = await db.MissionVersions.FindAsync(d.MissionVersionId);
        if (install is null || version is null) { await ApplyAsync(d, new DeployResult(d.Id, DeployResult.Failed, "The install or version is gone.", null)); return; }
        if (!registry.IsConnected(install.AgentId)) return; // stays Pending; sent again on the agent's hello
        if (ReadOnly(install)) { await ApplyAsync(d, new DeployResult(d.Id, DeployResult.Failed, ReadOnlyReason, null)); return; }
        DeployResult result;
        try { result = await agents.CallAsync(install.AgentId, c => c.DeployMission(new MissionDeploySpec(d.Id, install.InstallPath, d.FileName, version.PboSha256, version.PboSize))); }
        catch (AgentUnavailableException) { return; }
        catch (HubException e) { result = new DeployResult(d.Id, DeployResult.Failed, e.Message, null); }
        await ApplyAsync(d, result);
    }

    // The agent's hello: everything still Pending or Waiting for its installs goes again (the write is idempotent by hash).
    public async Task ResendAsync(Guid agentId)
    {
        var installs = await db.Installs.Where(i => i.AgentId == agentId).Select(i => i.Id).ToListAsync();
        foreach (var d in await db.Deployments.Where(d => installs.Contains(d.ServerInstallId)
                     && (d.Status == DeploymentStatus.Pending || d.Status == DeploymentStatus.Waiting)).OrderBy(d => d.CreatedAt).ToListAsync())
            await SendAsync(d);
    }

    public async Task ChangedAsync(Guid agentId, DeployResult result)
    {
        var d = await db.Deployments.FindAsync(result.DeploymentId);
        if (d is null || d.Status is DeploymentStatus.Done or DeploymentStatus.Failed) return;
        if (await db.Installs.FindAsync(d.ServerInstallId) is not { } install || install.AgentId != agentId) return;
        await ApplyAsync(d, result);
    }

    private async Task ApplyAsync(Deployment d, DeployResult r)
    {
        var now = DateTime.UtcNow;
        switch (r.Status)
        {
            case DeployResult.Done: (d.Status, d.Error, d.BackupPath, d.DeployedAt) = (DeploymentStatus.Done, null, r.BackupFolder, now); break;
            case DeployResult.Failed: (d.Status, d.Error) = (DeploymentStatus.Failed, Clip(r.Error ?? "The agent couldn't write the mission.")); break;
            case DeployResult.Waiting: (d.Status, d.Error) = (DeploymentStatus.Waiting, Clip(r.Error)); break;
            // Accepted: the agent has it. A resend of a Waiting row is answered Accepted too; that mustn't turn it back into Pending.
            default: if (d.Status != DeploymentStatus.Pending) return; break;
        }
        await db.SaveChangesAsync();
        if (d.Status is DeploymentStatus.Done or DeploymentStatus.Failed)
        {
            var install = await db.Installs.FindAsync(d.ServerInstallId);
            var number = await db.MissionVersions.Where(v => v.Id == d.MissionVersionId).Select(v => v.Number).FirstOrDefaultAsync();
            await audit.RecordAsync(d.Status == DeploymentStatus.Done ? "mission.deployed" : "mission.deploy.failed",
                $"{(d.Status == DeploymentStatus.Done ? "Wrote" : "Couldn't write")} {d.FileName} version {number} to {install?.Name}", "mission", d.MissionId.ToString(),
                new { install = install?.Name, version = number, error = d.Error, backup = d.BackupPath }, actorId: d.DeployedById);
            if (d.Status == DeploymentStatus.Done && await db.Missions.FindAsync(d.MissionId) is { } mission)
                notifier.Enqueue(Panel.Features.Notifications.Notices.MissionDeployed(mission, number, install?.Name));
        }
    }

    private static string? Clip(string? s) => s is { Length: > 1000 } ? s[..1000] : s;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha();

    public static void MapDeployments(WebApplication app)
    {
        app.MapGet("/api/missions/{id:guid}/deployments", List).RequirePermission(Permissions.MissionsView);
        // Agents pull a PBO only when it is a version with a deployment for one of their installs (Ruling 11).
        app.MapGet("/api/agent/blobs/{sha}", Blob).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AgentKeyAuthentication.SchemeName });
    }

    private static async Task<IResult> List(Guid id, AppDb db)
    {
        var rows = await db.Deployments.Where(d => d.MissionId == id).OrderByDescending(d => d.CreatedAt).ToListAsync();
        var installs = await db.Installs.ToDictionaryAsync(i => i.Id, i => i.Name);
        var versionIds = rows.Select(r => r.MissionVersionId).Distinct().ToList();
        var numbers = await db.MissionVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number);
        return Results.Ok(rows.GroupBy(r => r.ServerInstallId).Select(g => g.First())
            .Select(d => new DeploymentDto(d.ServerInstallId, installs.GetValueOrDefault(d.ServerInstallId) ?? "?", d.Status.ToString(),
                numbers.GetValueOrDefault(d.MissionVersionId), d.Error, d.BackupPath, d.CreatedAt, d.DeployedAt))
            .OrderBy(d => d.InstallName));
    }

    private static async Task<IResult> Blob(string sha, AppDb db, BlobStore blobs, HttpContext ctx)
    {
        if (!Guid.TryParse(ctx.User.FindFirst(AgentKeyAuthentication.AgentIdClaim)?.Value, out var agentId) || !Sha().IsMatch(sha)) return NotFound();
        var allowed = await (from d in db.Deployments
                             join v in db.MissionVersions on d.MissionVersionId equals v.Id
                             join i in db.Installs on d.ServerInstallId equals i.Id
                             where v.PboSha256 == sha && i.AgentId == agentId
                             select d.Id).AnyAsync();
        return allowed && blobs.Exists(sha) ? Results.File(blobs.Open(sha), "application/octet-stream") : NotFound();
    }

    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such mission file for this agent.");
}
