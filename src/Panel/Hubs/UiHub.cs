using System.Security.Claims;
using Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Features.Servers;
using Panel.Settings;

namespace Panel.Hubs;

public record ServerSnapshot(ServerDto Server, ResourceSample[] Resources);

// The browser's live feed (cookie auth). Joining a group checks permission; UiConnections re-checks every open
// connection on the stamp interval and closes any that lost access, so pushes stop within one interval.
[Authorize]
public sealed class UiHub(AppDb db, AgentRegistry registry, PermissionService permissions, UserManager<User> users,
    AppSettingsService settings, UiConnections connections, IOptions<IdentityOptions> identity) : Hub
{
    private Guid UserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override async Task OnConnectedAsync()
    {
        // The cookie's security stamp must be the current one: a password change or reset signs every session out,
        // and an open WebSocket is never re-authenticated, so UiConnections keeps comparing it.
        var stamp = Context.User?.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType);
        var user = await users.FindByIdAsync(UserId.ToString());
        if (user is null || AuthEndpoints.IsDisabled(user) || await TwoFactorGate.MustEnrolAsync(user, users, settings)
            || stamp is null || stamp != await users.GetSecurityStampAsync(user))
        {
            Context.Abort();
            return;
        }
        connections.Add(Context, UserId, stamp);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return Task.CompletedTask;
    }

    private async Task RequireAsync(string permission)
    {
        permissions.Forget(UserId); // the per-request cache would otherwise live as long as the connection
        if (!(await permissions.EffectiveAsync(UserId)).Contains(permission)) throw new HubException("forbidden");
    }

    public async Task<ServerSnapshot> Watch(Guid profileId)
    {
        await RequireAsync(Permissions.ServerView);
        var server = (await ServerViews.BuildAsync(db, registry, profileId)).FirstOrDefault() ?? throw new HubException("not_found");
        var agentId = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == profileId select i.AgentId).FirstAsync();
        await Groups.AddToGroupAsync(Context.ConnectionId, UiConnections.StatusGroup(profileId));
        return new ServerSnapshot(server, registry.History(agentId).ToArray());
    }

    public async Task<LogLine[]> WatchConsole(Guid profileId)
    {
        await RequireAsync(Permissions.LogsView);
        if (!await db.Profiles.AnyAsync(p => p.Id == profileId)) throw new HubException("not_found");
        connections.WatchConsole(Context.ConnectionId, profileId);
        await Groups.AddToGroupAsync(Context.ConnectionId, UiConnections.ConsoleGroup(profileId));
        return registry.Lines(profileId).ToArray();
    }

    // Live job progress for the Mods pages (Phase 5a).
    public async Task WatchJobs()
    {
        await RequireAsync(Permissions.ModsView);
        connections.WatchJobs(Context.ConnectionId); // the re-check closes it if mods.view is taken away
        await Groups.AddToGroupAsync(Context.ConnectionId, Panel.Features.Jobs.JobService.Group);
    }

    public async Task Unwatch(Guid profileId)
    {
        connections.UnwatchConsole(Context.ConnectionId, profileId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UiConnections.StatusGroup(profileId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UiConnections.ConsoleGroup(profileId));
    }
}
