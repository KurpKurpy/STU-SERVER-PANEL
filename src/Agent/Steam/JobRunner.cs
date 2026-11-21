using System.Text.RegularExpressions;
using System.Threading.Channels;
using Contracts;
using ServerAgent.Backups;
using ServerAgent.Mods;

namespace ServerAgent.Steam;

public sealed record JobTimeouts(TimeSpan Answer, TimeSpan Idle, TimeSpan UpdateInterval)
{
    public static JobTimeouts Default { get; } = new(TimeSpan.FromMinutes(5), SteamCmdRun.DefaultIdleLimit, TimeSpan.FromSeconds(1));
}

// Runs SteamCMD jobs one at a time (Phase 5a Ruling 2): server install/update, Workshop download with retries (Ruling 12), Workshop
// remove. Steam prompts go out as JobUpdates and wait for Answer; an unanswered prompt fails the job after Timeouts.Answer.
public sealed partial class JobRunner : IDisposable
{
    public const long GameAppId = 107410, ServerAppId = 233780;
    private const int MaxRuns = 3;
    private const string NotAnswered = "The Steam sign-in wasn't answered in time.";

    private static readonly Dictionary<string, string[]> Branches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public"] = [],
        ["creatordlc"] = ["-beta", "creatordlc"],
        ["profiling"] = ["-beta", "profiling", "-betapassword", "CautionSpecialProfilingAndTestingBranchArma3"],
    };

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex SteamUser();

    // Older SteamCMD says "FAILED (Invalid Password)"; the one on the box (2026-11-21) says "ERROR (Invalid Password)".
    [GeneratedRegex(@"(?:FAILED|ERROR) \(([^)]*)\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoginFailed();

    private sealed class Active(Guid jobId)
    {
        public Guid JobId { get; } = jobId;
        public SteamCmdRun? Run;
        public SteamPrompt? Prompt;
        public DateTime PromptAt;
        public bool Expired;
    }

    private readonly Areas _areas;
    private readonly SteamCmdInstaller _installer;
    private readonly ILogger<JobRunner> _log;
    private readonly JobTimeouts _timeouts;
    private readonly Channel<JobSpec> _queue = Channel.CreateUnbounded<JobSpec>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly List<Guid> _jobs = [];
    private readonly HashSet<Guid> _cancelled = [];
    private readonly CancellationTokenSource _stop = new();
    private Active? _active;
    private readonly ServerBackups? _backups;

    public event Action<JobUpdate>? Updated;
    public event Action<JobResult>? Finished;

    public JobRunner(Areas areas, SteamCmdInstaller installer, ILogger<JobRunner> log, JobTimeouts? timeouts = null, ServerBackups? backups = null)
    {
        (_areas, _installer, _log, _timeouts, _backups) = (areas, installer, log, timeouts ?? JobTimeouts.Default, backups);
        _ = Task.Run(WorkAsync);
    }

    // Queued and running jobs, oldest first.
    public Guid[] Jobs { get { lock (_gate) return [.. _jobs]; } }

    public CommandResult Enqueue(JobSpec spec)
    {
        if (Problem(spec) is { } problem) return new CommandResult(false, problem);
        lock (_gate)
        {
            if (_jobs.Contains(spec.JobId)) return new CommandResult(false, "That job is already queued.");
            _jobs.Add(spec.JobId);
        }
        _queue.Writer.TryWrite(spec with { Items = spec.Items ?? [] });
        return new CommandResult(true, null);
    }

    // The answer goes to SteamCMD's input and nowhere else: never logged, never kept.
    public CommandResult Answer(Guid jobId, string answer)
    {
        SteamCmdRun run;
        lock (_gate)
        {
            if (_active is not { Run: { } r, Prompt: not (null or SteamPrompt.MobileConfirm) } a || a.JobId != jobId)
                return new CommandResult(false, "That job isn't waiting for a Steam answer.");
            if (answer is not { Length: > 0 and <= 256 } || answer.Any(char.IsControl)) return new CommandResult(false, "The answer must be one line.");
            a.Prompt = null;
            run = r;
        }
        _ = run.WriteAsync(answer);
        return new CommandResult(true, null);
    }

    public CommandResult Cancel(Guid jobId)
    {
        SteamCmdRun? run = null;
        lock (_gate)
        {
            if (!_jobs.Contains(jobId)) return new CommandResult(false, "That job isn't queued or running on this agent.");
            _cancelled.Add(jobId);
            if (_active?.JobId == jobId) run = _active.Run;
        }
        run?.Kill();
        return new CommandResult(true, null);
    }

    private string? Problem(JobSpec spec)
    {
        var items = spec.Items ?? [];
        switch (spec.Kind)
        {
            case JobKind.ServerInstall:
                if (_areas.FindInstallRoot(spec.InstallRoot) is not { } installRoot) return "That server folder isn't one of this agent's InstallRoots.";
                if (_areas.IsReadOnlyInstall(installRoot)) return Areas.ReadOnlyMessage;
                if (!Branches.ContainsKey(spec.Branch ?? "public")) return "The branch must be public, creatordlc or profiling.";
                return SteamUser().IsMatch(spec.SteamUser ?? "") ? null : "Set the Steam username first: letters, digits and underscores.";
            case JobKind.WorkshopDownload:
                if (!SteamUser().IsMatch(spec.SteamUser ?? "")) return "Set the Steam username first: letters, digits and underscores.";
                goto case JobKind.WorkshopRemove;
            case JobKind.WorkshopRemove:
                if (items.Length is 0 or > 500) return "A job needs 1 to 500 Workshop items.";
                return items.All(i => i.Id > 0) ? null : "Workshop IDs are positive numbers.";
            // Phase 7b: server backups and restores share this queue, so a backup queued before an update runs first.
            case JobKind.ServerBackup:
                if (_backups is null) return "Backups aren't set up on this agent.";
                return ServerBackups.IsLabel(spec.Label) ? null : "A backup label is lower-case letters and hyphens.";
            case JobKind.ServerRestore:
                if (_backups is null) return "Backups aren't set up on this agent.";
                if (!ServerBackups.IsName(spec.Backup)) return "That isn't a server backup.";
                return spec.Parts is { Length: > 0 } parts && parts.All(ServerBackups.Parts.Contains) ? null : "Pick what to restore: profiles, configs, missions, keys.";
            default:
                return "Unknown job type.";
        }
    }

    private bool IsCancelled(Guid jobId) { lock (_gate) return _cancelled.Contains(jobId); }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var spec in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                var active = new Active(spec.JobId);
                lock (_gate) _active = active;
                JobResult result;
                if (IsCancelled(spec.JobId)) result = Failed(spec, "Cancelled.");
                else
                {
                    _log.LogInformation("Job {Id}: {Kind} started", spec.JobId, spec.Kind);
                    try { result = await RunAsync(spec, active); }
                    // Only the agent stopping ends the loop; anything else, an HttpClient timeout included, fails this job alone.
                    catch (Exception e) when (!_stop.IsCancellationRequested)
                    {
                        _log.LogWarning(e, "Job {Id} failed", spec.JobId);
                        result = Failed(spec, e.Message);
                    }
                    if (IsCancelled(spec.JobId)) result = result with { Ok = false, Error = "Cancelled." };
                }
                lock (_gate)
                {
                    _active = null;
                    _jobs.Remove(spec.JobId);
                    _cancelled.Remove(spec.JobId);
                }
                _log.LogInformation("Job {Id}: {Kind} {Outcome}", spec.JobId, spec.Kind, result.Ok ? "succeeded" : "failed: " + result.Error);
                Finished?.Invoke(result);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<JobResult> RunAsync(JobSpec spec, Active active)
    {
        if (spec.Kind == JobKind.WorkshopRemove) return Remove(spec);
        if (spec.Kind == JobKind.ServerBackup) return new JobResult(spec.JobId, true, null, [], Made: await _backups!.CreateAsync(spec.Label, _stop.Token));
        if (spec.Kind == JobKind.ServerRestore)
        {
            try
            {
                var (before, skipped) = await _backups!.RestoreAsync(spec.Backup!, spec.Parts!, _stop.Token);
                return new JobResult(spec.JobId, true, null, [], Before: before, Note: skipped.Length > 0 ? "Skipped: " + string.Join("; ", skipped) + "." : null);
            }
            catch (RestoreException e) { return new JobResult(spec.JobId, false, e.Message, [], Before: e.Before); } // final review I3: the way back
        }
        if (await _installer.EnsureAsync(_areas.SteamCmdExe, _stop.Token)) await WarmUpAsync(spec, active);
        return spec.Kind == JobKind.ServerInstall ? await InstallServerAsync(spec, active) : await DownloadAsync(spec, active);
    }

    // A fresh SteamCMD updates itself and exits 7 (spike S1); "+quit" until it exits 0.
    private async Task WarmUpAsync(JobSpec spec, Active active)
    {
        for (var i = 0; i < 3; i++)
            if ((await RunOnceAsync(spec, active, ["+quit"], null)).Exit.ExitCode == 0) return;
    }

    private async Task<(SteamCmdExit Exit, string Text)> RunWithUpdateAsync(JobSpec spec, Active active, List<string> args, Func<Measure>? measured)
    {
        var run = await RunOnceAsync(spec, active, args, measured);
        if (run.Exit.ExitCode != 7 || active.Expired || IsCancelled(spec.JobId)) return run;
        await WarmUpAsync(spec, active);
        return await RunOnceAsync(spec, active, args, measured);
    }

    private async Task<JobResult> InstallServerAsync(JobSpec spec, Active active)
    {
        var args = new List<string> { "+force_install_dir", _areas.FindInstallRoot(spec.InstallRoot)!, "+login", spec.SteamUser!, "+app_update", ServerAppId.ToString() };
        args.AddRange(Branches[spec.Branch ?? "public"]);
        args.AddRange(["validate", "+quit"]);
        var (exit, text) = await RunWithUpdateAsync(spec, active, args, null);
        if (active.Expired) return Failed(spec, NotAnswered);
        if (LoginFailed().Match(text) is { Success: true } failed) return Failed(spec, $"Steam sign-in failed: {failed.Groups[1].Value}.");
        if (SteamCmd.AppResults(text).TryGetValue(ServerAppId, out var app))
            return app.Ok ? new JobResult(spec.JobId, true, null, []) : Failed(spec, $"SteamCMD couldn't install the server: {app.Error}.");
        return Failed(spec, exit.TimedOut ? "SteamCMD stopped responding." : $"SteamCMD finished without installing the server (exit code {exit.ExitCode}).");
    }

    private async Task<JobResult> DownloadAsync(JobSpec spec, Active active)
    {
        var ids = spec.Items.Select(i => i.Id).Distinct().ToList();
        var results = new Dictionary<long, ItemResult>();
        var pending = ids;
        var timedOut = false;
        // An update or reinstall starts with the old files in place: only what arrives in the staging folder is progress.
        var hadFiles = ids.Where(id => Directory.Exists(Path.Combine(_areas.WorkshopContent, id.ToString()))).ToHashSet();
        for (var run = 0; run < MaxRuns && pending.Count > 0 && !IsCancelled(spec.JobId); run++)
        {
            var args = new List<string> { "+force_install_dir", _areas.WorkshopRoot, "+login", spec.SteamUser! };
            foreach (var id in pending) args.AddRange(["+workshop_download_item", GameAppId.ToString(), id.ToString(), "validate"]);
            args.Add("+quit");
            var (exit, text) = await RunWithUpdateAsync(spec, active, args, () => Measured(spec, results, hadFiles));
            timedOut = exit.TimedOut;
            if (active.Expired) return Failed(spec, NotAnswered, results);
            if (LoginFailed().Match(text) is { Success: true } failed) return Failed(spec, $"Steam sign-in failed: {failed.Groups[1].Value}.", results);
            foreach (var (id, result) in SteamCmd.ItemResults(text))
                if (pending.Contains(id)) results[id] = result;
            pending = pending.Where(id => Retryable(results.GetValueOrDefault(id), exit.TimedOut)).ToList();
        }

        var items = ids.Select(id =>
        {
            var folder = Path.Combine(_areas.WorkshopContent, id.ToString());
            return results.GetValueOrDefault(id) is { Ok: true }
                ? new JobItemResult(id, true, null, ModFiles.Size(folder), ModFiles.KeyNames(folder), ModStore.Name(folder))
                : new JobItemResult(id, false, results.GetValueOrDefault(id)?.Error
                    ?? (timedOut ? "SteamCMD stopped responding." : "SteamCMD gave no result for this item."), 0, []);
        }).ToArray();
        var failures = items.Count(i => !i.Ok);
        return new JobResult(spec.JobId, failures == 0, failures == 0 ? null : $"{failures} of {items.Length} items failed.", items);
    }

    // Ruling 12: Timeout and Failure get another run, and so does an item SteamCMD never reached, unless it hung. "File Not
    // Found", "Access Denied", "No subscription" and the like won't change on a retry.
    private static bool Retryable(ItemResult? result, bool timedOut) =>
        result is null ? !timedOut : !result.Ok && result.Error is "Timeout" or "Failure";

    private readonly record struct Measure(double? Progress, long Bytes);

    // Bytes on disk against the Workshop sizes (Ruling 11); Progress is null when no size is known. Bytes is the raw total in the
    // items' staging and content folders: any change means SteamCMD is still working, even with a silent console.
    private Measure Measured(JobSpec spec, Dictionary<long, ItemResult> done, HashSet<long> hadFiles)
    {
        double total = 0, have = 0;
        long bytes = 0;
        foreach (var item in spec.Items)
        {
            var downloading = ModFiles.Size(Path.Combine(_areas.WorkshopRoot, "steamapps", "workshop", "downloads", GameAppId.ToString(), item.Id.ToString()), live: true);
            var content = ModFiles.Size(Path.Combine(_areas.WorkshopContent, item.Id.ToString()), live: true);
            bytes += downloading + content;
            if (item.ExpectedBytes is not > 0) continue;
            var expected = (double)item.ExpectedBytes.Value;
            total += expected;
            have += done.GetValueOrDefault(item.Id) is { Ok: true } ? expected
                : Math.Min(expected, hadFiles.Contains(item.Id) ? downloading : Math.Max(downloading, content));
        }
        return new Measure(total > 0 ? have / total : null, bytes);
    }

    private JobResult Remove(JobSpec spec)
    {
        var items = spec.Items.Select(i => i.Id).Distinct().Select(id =>
        {
            try
            {
                foreach (var folder in new[]
                         {
                             Path.Combine(_areas.WorkshopContent, id.ToString()),
                             Path.Combine(_areas.WorkshopRoot, "steamapps", "workshop", "downloads", GameAppId.ToString(), id.ToString()),
                         })
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return new JobItemResult(id, true, null, 0, []);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new JobItemResult(id, false, e.Message, 0, []);
            }
        }).ToArray();
        var failures = items.Count(i => !i.Ok);
        return new JobResult(spec.JobId, failures == 0, failures == 0 ? null : $"{failures} of {items.Length} items couldn't be removed.", items);
    }

    private static JobResult Failed(JobSpec spec, string error, Dictionary<long, ItemResult>? results = null) =>
        new(spec.JobId, false, error, (spec.Items ?? []).Select(i => i.Id).Distinct()
            .Select(id => new JobItemResult(id, results?.GetValueOrDefault(id) is { Ok: true }, results?.GetValueOrDefault(id)?.Error, 0, [])).ToArray());

    private async Task<(SteamCmdExit Exit, string Text)> RunOnceAsync(JobSpec spec, Active active, IReadOnlyList<string> args, Func<Measure>? measured)
    {
        var lines = new List<string>();
        double? progress = null;
        using var run = SteamCmdRun.Start(_areas.SteamCmdExe, args, idleLimit: _timeouts.Idle);
        lock (_gate)
        {
            active.Run = run;
            active.Prompt = null;
        }
        string[] Take() { lock (lines) { var batch = lines.ToArray(); lines.Clear(); return batch; } }
        run.Line += line =>
        {
            lock (lines) lines.Add(line);
            lock (_gate)
                if (active.Prompt == SteamPrompt.MobileConfirm && !line.Contains("Steam Mobile app", StringComparison.OrdinalIgnoreCase))
                    active.Prompt = null; // approved on the phone: SteamCMD moved on
        };
        run.Progress += p => progress = p;
        run.Prompt += prompt =>
        {
            lock (_gate)
            {
                active.Prompt = prompt;
                active.PromptAt = DateTime.UtcNow;
            }
            Updated?.Invoke(new JobUpdate(spec.JobId, progress, Take(), prompt));
        };

        double? measuredValue = null;
        long? measuredBytes = null;
        var measuredAt = DateTime.MinValue;
        while (!run.Completion.IsCompleted)
        {
            await Task.WhenAny(run.Completion, Task.Delay(_timeouts.UpdateInterval));
            if (measured is not null && DateTime.UtcNow - measuredAt > TimeSpan.FromSeconds(2))
            {
                var m = measured();
                if (measuredBytes is not null && m.Bytes != measuredBytes) run.Touch();
                (measuredValue, measuredBytes, measuredAt) = (m.Progress, m.Bytes, DateTime.UtcNow);
            }
            SteamPrompt? open;
            lock (_gate)
            {
                open = active.Prompt;
                if (open is not null && DateTime.UtcNow - active.PromptAt > _timeouts.Answer) active.Expired = true;
            }
            if (active.Expired) { run.Kill(); break; }
            var batch = Take();
            if (batch.Length > 0 || measuredValue is not null || progress is not null)
                Updated?.Invoke(new JobUpdate(spec.JobId, measuredValue ?? progress, batch, open));
        }
        var exit = await run.Completion;
        var rest = Take();
        if (rest.Length > 0) Updated?.Invoke(new JobUpdate(spec.JobId, measuredValue ?? progress, rest, null));
        lock (_gate)
        {
            active.Run = null;
            active.Prompt = null;
        }
        return (exit, run.Transcript);
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate) _active?.Run?.Kill();
    }
}
