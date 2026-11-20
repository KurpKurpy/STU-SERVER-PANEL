using System.Collections.Concurrent;

namespace Panel.Features.Players;

public sealed record RconLine(DateTime At, string Text);

// Phase 7a Ruling 10: the last 500 RCon lines per server (BattlEye's server messages and the panel's own actions), in memory only.
// It holds player IPs and GUIDs, so only players.manage reads it. Also remembers when this panel first saw each player, for "time on".
public sealed class RconLog
{
    private const int Keep = 500, MaxText = 500;
    private readonly ConcurrentDictionary<Guid, Queue<RconLine>> _lines = new();
    private sealed record Run(DateTime? StartedAt, Dictionary<string, DateTime> Seen);
    private readonly ConcurrentDictionary<Guid, Run> _runs = new();

    public void Add(Guid profileId, DateTime at, string text)
    {
        var q = _lines.GetOrAdd(profileId, _ => new Queue<RconLine>());
        lock (q)
        {
            q.Enqueue(new RconLine(at, text.Length > MaxText ? text[..MaxText] : text));
            while (q.Count > Keep) q.Dequeue();
        }
    }

    public RconLine[] Get(Guid profileId)
    {
        if (!_lines.TryGetValue(profileId, out var q)) return [];
        lock (q) return [.. q];
    }

    // Final review I4: when this run of the server first listed each GUID, in the order given. A server restart starts again, and so does
    // a player missing from a listing (they left), so a rejoin counts from the rejoin. No GUID yet ("-") is unknown, not shared.
    public DateTime?[] FirstSeen(Guid profileId, DateTime? startedAt, string[] guids, DateTime now)
    {
        var run = _runs.AddOrUpdate(profileId, _ => new Run(startedAt, []), (_, r) => r.StartedAt == startedAt ? r : new Run(startedAt, []));
        lock (run.Seen)
        {
            foreach (var gone in run.Seen.Keys.Except(guids).ToList()) run.Seen.Remove(gone);
            return guids.Select(g => g == "-" ? (DateTime?)null : run.Seen.TryAdd(g, now) ? now : run.Seen[g]).ToArray();
        }
    }
}
