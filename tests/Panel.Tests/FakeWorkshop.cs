using System.Collections.Concurrent;
using Panel.Features.Mods;

namespace Panel.Tests;

public sealed class FakeWorkshop : IWorkshopClient
{
    public static readonly DateTime Updated = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    public ConcurrentDictionary<long, WorkshopDetails> Items { get; } = new();
    public int Calls;

    public Task<WorkshopDetails[]> DetailsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(ids.Select(id => Items.TryGetValue(id, out var d) ? d
            : new WorkshopDetails(id, true, $"Mod {id}", 1000 + id % 1000, Updated, $"https://steamuserimages.example/{id}.jpg", 107410)).ToArray());
    }

    public void Set(long id, string title, DateTime? updated = null, long size = 1000, long app = 107410, bool ok = true) =>
        Items[id] = new WorkshopDetails(id, ok, title, size, updated ?? Updated, null, app);
}
