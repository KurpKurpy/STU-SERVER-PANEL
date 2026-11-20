using System.Collections.Concurrent;
using System.Security.Cryptography;
using Contracts;
using Panel.Auth;
using Panel.Hubs;

namespace Panel.Features.Files;

// Phase 4b Ruling 12: an upload waits in <DataDir>\transfers\<change id> until the agent named for it pulls it, and is deleted when the
// change finishes. Not the BlobStore, which keeps everything forever.
public sealed class FileTransfers(PanelPaths paths)
{
    public const long MaxBytes = 2L << 30;
    private readonly string _dir = Path.Combine(paths.DataDir, "transfers");
    private readonly ConcurrentDictionary<Guid, Guid> _agentOf = new(); // change id → the one agent that may pull it

    public void CleanAtStart()
    {
        if (Directory.Exists(_dir)) foreach (var f in Directory.EnumerateFiles(_dir)) File.Delete(f);
    }

    // Streams into the transfer file, hashing as it goes; null if it's over the cap (the partial file is removed).
    public async Task<(long Size, string Sha256)?> SaveAsync(Guid id, Stream data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, id.ToString("N"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await data.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += n) > MaxBytes) break;
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
        }
        catch
        {
            File.Delete(path); // a cut-off or malformed upload leaves nothing behind (final review I-2)
            throw;
        }
        if (total > MaxBytes) { File.Delete(path); return null; }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public void Grant(Guid id, Guid agentId) => _agentOf[id] = agentId;

    public string? PathFor(Guid id, Guid agentId)
    {
        var path = Path.Combine(_dir, id.ToString("N"));
        return _agentOf.TryGetValue(id, out var a) && a == agentId && File.Exists(path) ? path : null;
    }

    public void Remove(Guid id)
    {
        _agentOf.TryRemove(id, out _);
        try { File.Delete(Path.Combine(_dir, id.ToString("N"))); } catch (IOException) { } // CleanAtStart takes it later
    }
}

// Phase 4b Ruling 5: a change is accepted by the agent at once and reported through the hub when done. The request waits for that
// report up to a limit; null means it is still running.
public sealed class FileChanges(AgentCalls agents, FileTransfers transfers)
{
    private readonly ConcurrentDictionary<Guid, (Guid AgentId, TaskCompletionSource<FileChangeResult> Done)> _pending = new();

    public async Task<FileChangeResult?> RunAsync(Guid agentId, FileChangeSpec spec, TimeSpan wait)
    {
        var done = new TaskCompletionSource<FileChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[spec.Id] = (agentId, done);
        CommandResult accepted;
        try { accepted = await agents.CallAsync(agentId, c => c.FileChange(spec)); }
        catch { Forget(spec.Id); throw; }
        if (!accepted.Ok)
        {
            Forget(spec.Id);
            return new FileChangeResult(spec.Id, false, accepted.Error ?? "The agent refused.", null);
        }
        return await Task.WhenAny(done.Task, Task.Delay(wait)) == done.Task ? await done.Task : null;
    }

    // Only the agent the change was sent to can finish it.
    public void Complete(Guid agentId, FileChangeResult result)
    {
        if (!_pending.TryGetValue(result.Id, out var p) || p.AgentId != agentId || !_pending.TryRemove(result.Id, out _)) return;
        transfers.Remove(result.Id);
        p.Done.TrySetResult(result);
    }

    private void Forget(Guid id)
    {
        _pending.TryRemove(id, out _);
        transfers.Remove(id);
    }
}
