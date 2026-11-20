using System.Collections.Concurrent;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Hubs;

// Live state from connected agents, in memory only (Ruling 7). Nothing here touches the database on the hot path,
// and nothing outlives the agent's connection except the console ring: a disconnected agent's statuses are dropped,
// so the panel shows "unknown" rather than the last thing it heard.
public sealed class AgentRegistry(IServiceScopeFactory scopes, Panel.Features.Notifications.Notifier notifier)
{
    private const int LinesKept = 2000;
    private const int SamplesKept = 450;

    private sealed class Live(HubCallerContext context)
    {
        public HubCallerContext Context { get; } = context;
        public AgentHello? Hello { get; set; }
        public Queue<ResourceSample> History { get; } = new();
    }

    private readonly ConcurrentDictionary<Guid, Live> _agents = new();
    private readonly ConcurrentDictionary<Guid, (Guid AgentId, ProfileStatus Status)> _statuses = new();
    private readonly ConcurrentDictionary<Guid, Queue<LogLine>> _lines = new();
    private readonly ConcurrentDictionary<Guid, (Guid UserId, DateTime Until)> _actors = new();
    private readonly SemaphoreSlim _runs = new(1, 1);

    public event Action<Guid, ProfileStatus>? StatusUpdated;
    public event Action<Guid, LogLine[]>? LinesAdded;
    public event Action<Guid, ResourceSample>? ResourcesAdded;
    public event Action<Guid>? AgentChanged; // connected, said hello, or went away

    public bool IsConnected(Guid agentId) => _agents.ContainsKey(agentId);
    public string? ConnectionId(Guid agentId) => _agents.TryGetValue(agentId, out var a) ? a.Context.ConnectionId : null;
    public AgentHello? Hello(Guid agentId) => _agents.TryGetValue(agentId, out var a) ? a.Hello : null;

    public ProfileStatus? Status(Guid profileId) =>
        _statuses.TryGetValue(profileId, out var s) && _agents.ContainsKey(s.AgentId) ? s.Status : null;

    public IReadOnlyList<LogLine> Lines(Guid profileId)
    {
        if (!_lines.TryGetValue(profileId, out var ring)) return [];
        lock (ring) return ring.ToArray();
    }

    public IReadOnlyList<ResourceSample> History(Guid agentId)
    {
        if (!_agents.TryGetValue(agentId, out var a)) return [];
        lock (a.History) return a.History.ToArray();
    }

    public ResourceSample? Latest(Guid agentId)
    {
        if (!_agents.TryGetValue(agentId, out var a)) return null;
        lock (a.History) return a.History.LastOrDefault();
    }

    // One live connection per agent: a newer one replaces (and aborts) the older.
    public void Connected(Guid agentId, HubCallerContext context)
    {
        if (_agents.TryGetValue(agentId, out var old) && old.Context.ConnectionId != context.ConnectionId) old.Context.Abort();
        _agents[agentId] = new Live(context);
        AgentChanged?.Invoke(agentId);
    }

    public void Disconnected(Guid agentId, string connectionId)
    {
        if (!_agents.TryGetValue(agentId, out var live) || live.Context.ConnectionId != connectionId) return;
        _agents.TryRemove(new KeyValuePair<Guid, Live>(agentId, live));
        foreach (var (profileId, s) in _statuses)
            if (s.AgentId == agentId) _statuses.TryRemove(profileId, out _);
        AgentChanged?.Invoke(agentId);
    }

    public void Disconnect(Guid agentId)
    {
        if (_agents.TryGetValue(agentId, out var live)) live.Context.Abort();
    }

    public void SetHello(Guid agentId, AgentHello hello)
    {
        if (!_agents.TryGetValue(agentId, out var live)) return;
        live.Hello = hello;
        lock (live.History)
        {
            live.History.Clear();
            foreach (var s in hello.RecentResources.TakeLast(SamplesKept)) live.History.Enqueue(s);
        }
        foreach (var (profileId, s) in _statuses)
            if (s.AgentId == agentId) _statuses.TryRemove(profileId, out _);
        foreach (var status in hello.Statuses) _statuses[status.ProfileId] = (agentId, status);
        AgentChanged?.Invoke(agentId);
    }

    public void SetStatus(Guid agentId, ProfileStatus status)
    {
        _statuses[status.ProfileId] = (agentId, status);
        StatusUpdated?.Invoke(agentId, status);
    }

    public void AddLines(Guid profileId, LogLine[] lines)
    {
        var ring = _lines.GetOrAdd(profileId, _ => new Queue<LogLine>());
        lock (ring)
        {
            foreach (var line in lines) ring.Enqueue(line);
            while (ring.Count > LinesKept) ring.Dequeue();
        }
        TrackMission(profileId, lines);
        LinesAdded?.Invoke(profileId, lines);
    }

    // A2S gives the mission's display name, not its template (spike F15), so the template comes from the RPT's
    // " Mission file: X" and " Mission world: Y" lines (F17). Null until a mission starts while the panel is watching.
    private readonly ConcurrentDictionary<Guid, (string? File, string? Template)> _missions = new();

    public string? MissionTemplate(Guid profileId) => _missions.TryGetValue(profileId, out var m) ? m.Template : null;

    private void TrackMission(Guid profileId, LogLine[] lines)
    {
        foreach (var line in lines)
        {
            var text = line.Text;
            var file = text.IndexOf(" Mission file: ", StringComparison.Ordinal);
            if (file >= 0) { _missions[profileId] = (text[(file + 15)..].Trim(), MissionTemplate(profileId)); continue; }
            var world = text.IndexOf(" Mission world: ", StringComparison.Ordinal);
            if (world >= 0 && _missions.TryGetValue(profileId, out var m) && m.File is { Length: > 0 })
                _missions[profileId] = (null, $"{m.File}.{text[(world + 16)..].Trim()}");
        }
    }

    public void AddResources(Guid agentId, ResourceSample sample)
    {
        if (!_agents.TryGetValue(agentId, out var live)) return;
        lock (live.History)
        {
            live.History.Enqueue(sample);
            while (live.History.Count > SamplesKept) live.History.Dequeue();
        }
        ResourcesAdded?.Invoke(agentId, sample);
    }

    // Who asked for the next launch of a profile, so the ProcessRun the agent's next new pid opens is attributed.
    public void ExpectActor(Guid profileId, Guid userId) => _actors[profileId] = (userId, DateTime.UtcNow.AddMinutes(2));

    private Guid? TakeActor(Guid profileId) =>
        _actors.TryRemove(profileId, out var a) && a.Until > DateTime.UtcNow ? a.UserId : null;

    // ProcessRun history: a status with a pid no open run has opens one (closing any other open run first);
    // Offline or Error closes the open run. Statuses change rarely, so this is the one place the registry uses the database.
    public async Task RecordRunAsync(ProfileStatus status)
    {
        await _runs.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            if (await db.Profiles.FindAsync(status.ProfileId) is not { } profile) return;
            var open = await db.ProcessRuns.Where(r => r.ProfileId == status.ProfileId && r.StoppedAt == null).ToListAsync();
            var now = DateTime.UtcNow;
            Panel.Features.Notifications.NotificationEvent notice;
            // Phase 6b Ruling 5: notify only when a run actually opens or closes, so a reconnecting agent's hello repeating a running
            // server (or an Offline with no open run) says nothing. A new pid over an open run closes it quietly: one "started".
            if (status.State is ServerState.Offline or ServerState.Error)
            {
                if (open.Count == 0) return;
                var reason = status.Error ?? "stopped";
                foreach (var r in open) (r.StoppedAt, r.ExitCode, r.StopReason) = (now, status.ExitCode, reason.Length > 200 ? reason[..200] : reason);
                notice = Panel.Features.Notifications.Notices.ServerOffline(profile, status);
            }
            else if (status.Pid is int pid && !open.Any(r => r.Pid == pid))
            {
                foreach (var r in open) (r.StoppedAt, r.StopReason) = (now, "stopped");
                db.ProcessRuns.Add(new ProcessRun { ProfileId = status.ProfileId, Pid = pid, StartedAt = status.StartedAt ?? now, StartedById = TakeActor(status.ProfileId) });
                notice = Panel.Features.Notifications.Notices.ServerStarted(profile);
            }
            else return;
            await db.SaveChangesAsync();
            notifier.Enqueue(notice);
        }
        finally { _runs.Release(); }
    }
}