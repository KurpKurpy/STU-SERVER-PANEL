using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArmaFormats;
using Contracts;

namespace ServerAgent.Missions;

// Writes mission PBOs into an install's mpmissions (Phase 4a Rulings 9–12). Deploy checks the request and answers at once; the pull and
// the write run in the background and report through Changed. The PBO is pulled by hash into a temp file next to the target and checked,
// the old file is backed up to backups\missions\<install-area>\<time>, then a rename replaces it. A running mission holds its PBO open
// (spike S3), so a locked target means Waiting and a retry until the file is free or a newer deployment for the same file arrives.
public sealed partial class MissionWriter(Areas areas, HttpClient http, ILogger<MissionWriter> log, TimeSpan? retry = null) : IDisposable
{
    public const long MaxBytes = 200L * 1024 * 1024;
    private readonly TimeSpan _retry = retry ?? TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Guid> _newest = new(StringComparer.OrdinalIgnoreCase); // target file → newest deployment
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase); // one per target file
    private readonly CancellationTokenSource _stop = new();
    private readonly int _leftoverTemps = RemoveLeftoverTemps(areas, log);

    public event Action<DeployResult>? Changed;

    [GeneratedRegex(@"^[^\\/]{1,200}\.pbo$", RegexOptions.IgnoreCase)]
    private static partial Regex PboName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha();

    [GeneratedRegex(@"\.pbo\.[0-9a-f]{32}\.tmp$", RegexOptions.IgnoreCase)]
    private static partial Regex TempName();

    private object LockOf(string target) => _locks.GetOrAdd(target, _ => new object());

    public DeployResult Deploy(MissionDeploySpec spec)
    {
        DeployResult Fail(string error) => new(spec.DeploymentId, DeployResult.Failed, error, null);
        if (areas.FindInstallRoot(spec.InstallRoot) is not { } root) return Fail("That server folder isn't one of this agent's InstallRoots.");
        if (areas.IsReadOnlyInstall(root)) return Fail(Areas.ReadOnlyMessage);
        if (!PboName().IsMatch(spec.FileName ?? "") || MissionPath.Problem(spec.FileName!) is not null) return Fail("That mission file name isn't allowed.");
        if (!Sha().IsMatch(spec.Sha256 ?? "") || spec.Size is < 1 or > MaxBytes) return Fail("The mission's hash or size isn't valid.");
        var target = Path.Combine(root, "mpmissions", spec.FileName!);
        lock (LockOf(target))
        {
            // Newest first, even when no write is needed: an older version still waiting for this file must give up (final review C-1).
            _newest[target] = spec.DeploymentId;
            if (HashOf(target) == spec.Sha256) return new DeployResult(spec.DeploymentId, DeployResult.Done, null, null); // already there
        }
        if (_running.TryAdd(spec.DeploymentId, 0)) _ = Task.Run(() => RunAsync(spec, root, target));
        return new DeployResult(spec.DeploymentId, DeployResult.Accepted, null, null);
    }

    private async Task RunAsync(MissionDeploySpec spec, string root, string target)
    {
        var temp = $"{target}.{spec.DeploymentId:N}.tmp";
        void Report(string status, string? error = null, string? backup = null) => Changed?.Invoke(new DeployResult(spec.DeploymentId, status, error, backup));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (await PullAsync(spec, temp) is { } problem) { Report(DeployResult.Failed, problem); return; }
            string? backup = null;
            var waiting = false;
            while (true)
            {
                bool superseded = false, written = false;
                // The newest check, the backup and the move happen under the file's lock, so a same-hash Deploy can't slip between them.
                lock (LockOf(target))
                {
                    if (_newest.TryGetValue(target, out var newest) && newest != spec.DeploymentId) superseded = true;
                    else
                    {
                        try
                        {
                            backup ??= Backup(root, target);
                            File.Move(temp, target, overwrite: true);
                            written = true;
                        }
                        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && InUse(target)) { } // a running mission (S3)
                    }
                }
                if (superseded)
                {
                    Report(DeployResult.Failed, "A newer version arrived before this one could be written.");
                    return;
                }
                if (written)
                {
                    log.LogInformation("Wrote mission {File} (backup {Backup})", target, backup ?? "none needed");
                    Report(DeployResult.Done, null, backup);
                    return;
                }
                if (!waiting) Report(DeployResult.Waiting, "The mission is running on the server, so its file is in use. It's written when the mission ends.");
                waiting = true;
                await Task.Delay(_retry, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } // the agent is stopping; the panel resends on hello
        catch (Exception e) // anything else must still end the row, or it says "Sending" until the next reconnect (final review M-1)
        {
            Report(DeployResult.Failed, $"Couldn't write the mission: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            _running.TryRemove(spec.DeploymentId, out _);
            _newest.TryRemove(new KeyValuePair<string, Guid>(target, spec.DeploymentId));
        }
    }

    // The PBO from the panel into temp, checked against its size and hash; a problem leaves no temp file behind.
    private async Task<string?> PullAsync(MissionDeploySpec spec, string temp)
    {
        using var response = await http.GetAsync($"api/agent/blobs/{spec.Sha256}", HttpCompletionOption.ResponseHeadersRead, _stop.Token);
        if (!response.IsSuccessStatusCode) return $"The panel didn't send the mission (HTTP {(int)response.StatusCode}); the old file is untouched.";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var body = await response.Content.ReadAsStreamAsync(_stop.Token))
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) // the id is unique: a same-name file is a dead agent's
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, _stop.Token)) > 0)
            {
                if ((total += n) > spec.Size) break;
                hash.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
            }
        }
        if (total == spec.Size && Convert.ToHexStringLower(hash.GetHashAndReset()) == spec.Sha256) return null;
        File.Delete(temp);
        return "The mission arrived damaged (its size or hash didn't match), so the old file is untouched.";
    }

    private string? Backup(string root, string target)
    {
        if (!File.Exists(target)) return null;
        var baseName = Path.Combine("missions", areas.InstallArea(root), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var folder = areas.Resolve("backups", baseName);
        for (var n = 2; Directory.Exists(folder); n++) folder = areas.Resolve("backups", $"{baseName}-{n}");
        Directory.CreateDirectory(folder);
        File.Copy(target, Path.Combine(folder, Path.GetFileName(target)));
        return folder;
    }

    // A running mission holds its PBO open (S3), so it can't be opened exclusively. Any other failure (permissions, a read-only
    // file, a folder in the way) isn't something waiting fixes (final review M-4).
    private static bool InUse(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) { return true; } // sharing or lock violation
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Temp files a dead agent left (a crash mid-pull or while Waiting). Deployment ids are unique, so at start every one is stale.
    private static int RemoveLeftoverTemps(Areas areas, ILogger log)
    {
        var removed = 0;
        foreach (var root in areas.InstallRoots.Where(r => !areas.IsReadOnlyInstall(r)))
        {
            try
            {
                var folder = Path.Combine(root, "mpmissions");
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.tmp").Where(f => TempName().IsMatch(f)))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Couldn't clear old mission temp files in {Root}: {Error}", root, e.Message); }
        }
        if (removed > 0) log.LogInformation("Removed {Count} mission temp files left by an earlier run", removed);
        return removed;
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _stop.Cancel();
}
