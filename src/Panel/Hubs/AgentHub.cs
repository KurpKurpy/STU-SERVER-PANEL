using Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Hubs;

// Agents dial out to this hub with their key. The panel calls them back with SignalR client results (AgentCalls).
[Authorize(AuthenticationSchemes = AgentKeyAuthentication.SchemeName)]
public sealed class AgentHub(AgentRegistry registry, AppDb db, Panel.Features.Jobs.JobService jobs, Panel.Features.Missions.MissionDeployments deployments,
    IServiceScopeFactory scopes, ILogger<AgentHub> log, Panel.Features.Files.FileChanges fileChanges, Panel.Features.Players.RconLog rconLog)
    : Hub<IAgentClient>, IAgentHub
{
    private Guid AgentId => Guid.Parse(Context.User!.FindFirst(AgentKeyAuthentication.AgentIdClaim)!.Value);

    public override Task OnConnectedAsync()
    {
        registry.Connected(AgentId, Context);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Disconnected(AgentId, Context.ConnectionId);
        if (await db.Agents.FindAsync(AgentId) is { } agent)
        {
            agent.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task Hello(AgentHello hello)
    {
        registry.SetHello(AgentId, hello); // live state first: a failed DB write must not leave every server "unknown"
        if (await db.Agents.FindAsync(AgentId) is { } agent)
        {
            agent.MachineName = Clip(hello.MachineName, 64);
            agent.Version = Clip(hello.Version, 32);
            agent.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        foreach (var status in hello.Statuses) await registry.RecordRunAsync(status);
        await jobs.HelloAsync(AgentId, hello.Jobs ?? []);
        // After Hello returns: calling this agent back from inside its own invocation would deadlock (one invocation per client).
        var agentId = AgentId;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Panel.Features.Missions.MissionDeployments>().ResendAsync(agentId);
            }
            catch (Exception e) { log.LogWarning(e, "Sending pending missions to agent {AgentId} failed", agentId); }
        });
        RescanLater(agentId);
    }

    // Phase 5b: whatever needs an addon scan (never scanned, or changed by an install job), asked for after this call returns.
    private void RescanLater(Guid agentId) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(50);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Panel.Features.Mods.AddonScans>().RescanAsync(agentId);
        }
        catch (Exception e) { log.LogWarning(e, "Asking agent {AgentId} for addon scans failed", agentId); }
    });

    public async Task AddonsScanned(AddonScan scan)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Panel.Features.Mods.AddonScans>().ApplyAsync(AgentId, scan);
    }

    public async Task StatusChanged(ProfileStatus status)
    {
        registry.SetStatus(AgentId, status);
        await registry.RecordRunAsync(status);
    }

    public Task LogLines(Guid profileId, LogLine[] lines)
    {
        registry.AddLines(profileId, lines);
        return Task.CompletedTask;
    }

    public Task DeploymentChanged(DeployResult result) => deployments.ChangedAsync(AgentId, result);

    // Phase 4b: a file-manager change finished; only the agent it was sent to can report it.
    public Task FileChanged(FileChangeResult result)
    {
        fileChanges.Complete(AgentId, result);
        return Task.CompletedTask;
    }

    // Phase 7a: a BattlEye server message (logins, chat, kicks), kept in the panel's RCon log, only for this agent's own servers.
    public async Task RconMessage(Guid profileId, DateTime at, string text)
    {
        var mine = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id
                          where p.Id == profileId && i.AgentId == AgentId select p.Id).AnyAsync();
        if (mine) rconLog.Add(profileId, at, text);
    }

    public Task Resources(ResourceSample sample)
    {
        registry.AddResources(AgentId, sample);
        return Task.CompletedTask;
    }

    public Task JobUpdated(JobUpdate update) => jobs.UpdatedAsync(AgentId, update);
    public async Task JobFinished(JobResult result)
    {
        await jobs.FinishedAsync(AgentId, result);
        RescanLater(AgentId); // an install or update marks what it changed as needing a scan
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
