using System.Collections.Concurrent;
using Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Features.Servers;
using Panel.Settings;

namespace Panel.Hubs;

// Two jobs for /hubs/ui:
//  1. forward the agents' live events (via AgentRegistry) to the browser groups that watch them;
//  2. every max(1, Auth:StampValidationSeconds) seconds, re-check each open connection's user and close the ones
//     that are gone, disabled, must enrol 2FA, have a new security stamp (password changed or reset), lack server.view,
//     or watch a console without logs.view.
// The browser reconnects and re-watches under whatever access it has then.
public sealed class UiConnections : BackgroundService
{
    public static string StatusGroup(Guid profileId) => "status:" + profileId;
    public static string ConsoleGroup(Guid profileId) => "console:" + profileId;

    private sealed record Conn(HubCallerContext Context, Guid UserId, string Stamp, HashSet<Guid> Consoles)
    {
        public volatile bool Jobs;
    }

    private readonly ConcurrentDictionary<string, Conn> _connections = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<UiHub> _hub;
    private readonly AgentRegistry _registry;
    private readonly ILogger<UiConnections> _log;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _push = new(1, 1);

    public UiConnections(IServiceScopeFactory scopes, IHubContext<UiHub> hub, AgentRegistry registry, IConfiguration config, ILogger<UiConnections> log)
    {
        (_scopes, _hub, _registry, _log) = (scopes, hub, registry, log);
        _interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Auth:StampValidationSeconds", 60)));
        registry.StatusUpdated += (agentId, _) => Forward(() => PushServersAsync(agentId));
        registry.AgentChanged += agentId => Forward(() => PushServersAsync(agentId));
        registry.ResourcesAdded += (agentId, sample) => Forward(() => PushResourcesAsync(agentId, sample));
        registry.LinesAdded += (profileId, lines) => Forward(() => _hub.Clients.Group(ConsoleGroup(profileId)).SendAsync("console", profileId, lines));
    }

    public void Add(HubCallerContext context, Guid userId, string stamp) => _connections[context.ConnectionId] = new Conn(context, userId, stamp, []);
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void WatchConsole(string connectionId, Guid profileId)
    {
        if (_connections.TryGetValue(connectionId, out var c)) lock (c.Consoles) c.Consoles.Add(profileId);
    }

    public void WatchJobs(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var c)) c.Jobs = true;
    }

    public void UnwatchConsole(string connectionId, Guid profileId)
    {
        if (_connections.TryGetValue(connectionId, out var c)) lock (c.Consoles) c.Consoles.Remove(profileId);
    }

    // Pushes go out one at a time and each reads the registry's current state, so the last push is always the newest.
    private void Forward(Func<Task> push) => _ = Task.Run(async () =>
    {
        await _push.WaitAsync();
        try { await push(); }
        catch (Exception e) { _log.LogWarning(e, "A live push failed"); }
        finally { _push.Release(); }
    });

    private async Task<Guid[]> ProfilesOfAsync(AppDb db, Guid agentId) =>
        await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where i.AgentId == agentId select p.Id).ToArrayAsync();

    private async Task PushServersAsync(Guid agentId)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        foreach (var id in await ProfilesOfAsync(db, agentId))
            if ((await ServerViews.BuildAsync(db, _registry, id)).FirstOrDefault() is { } server)
                await _hub.Clients.Group(StatusGroup(id)).SendAsync("status", server);
    }

    // The sample feeds the charts; the refreshed ServerDto carries the per-server CPU, memory and disk figures.
    private async Task PushResourcesAsync(Guid agentId, ResourceSample sample)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        foreach (var id in await ProfilesOfAsync(db, agentId))
        {
            await _hub.Clients.Group(StatusGroup(id)).SendAsync("resources", id, sample);
            if ((await ServerViews.BuildAsync(db, _registry, id)).FirstOrDefault() is { } server)
                await _hub.Clients.Group(StatusGroup(id)).SendAsync("status", server);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await RecheckAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "Re-checking hub connections failed"); }
        }
    }

    private async Task RecheckAsync()
    {
        var open = _connections.Values.ToArray();
        if (open.Length == 0) return;
        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var permissions = scope.ServiceProvider.GetRequiredService<PermissionService>();
        var settings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();
        foreach (var group in open.GroupBy(c => c.UserId))
        {
            var user = await users.FindByIdAsync(group.Key.ToString());
            var stamp = user is null ? null : await users.GetSecurityStampAsync(user);
            var allowed = user is not null && !AuthEndpoints.IsDisabled(user) && !await TwoFactorGate.MustEnrolAsync(user, users, settings)
                ? await permissions.EffectiveAsync(group.Key)
                : new HashSet<string>();
            foreach (var c in group)
            {
                bool watchesConsole;
                lock (c.Consoles) watchesConsole = c.Consoles.Count > 0;
                if (c.Stamp != stamp || !allowed.Contains(Permissions.ServerView) || (watchesConsole && !allowed.Contains(Permissions.LogsView))
                    || (c.Jobs && !allowed.Contains(Permissions.ModsView)))
                {
                    _log.LogInformation("Closing a live connection of user {UserId}: access changed", group.Key);
                    c.Context.Abort();
                    Remove(c.Context.ConnectionId);
                }
            }
        }
    }
}
