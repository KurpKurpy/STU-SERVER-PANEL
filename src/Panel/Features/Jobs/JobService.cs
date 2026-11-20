using System.Collections.Concurrent;
using System.Text.Json;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Hubs;

namespace Panel.Features.Jobs;

public record JobDto(Guid Id, string Type, string Status, double? Progress, string? Prompt, string? TargetType, string? TargetId,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, string? Error, string? LogText = null)
{
    public static JobDto From(Job j, bool withLog = false) => new(j.Id, j.Type, j.Status.ToString(), j.Progress, j.Prompt, j.TargetType, j.TargetId,
        j.CreatedAt, j.StartedAt, j.FinishedAt, j.Error, withLog ? j.LogText : null);
}

// What a finished job means for the rest of the panel (Task 7: mods installed or removed).
public interface IJobResultHandler
{
    bool Handles(string jobType);
    Task HandleAsync(Job job, JobResult result, AppDb db);
}

// Steam answers stay in memory only while their job runs, so any line that echoes one can be masked (Ruling 3; Review Focus 1).
public sealed class JobSecrets
{
    private readonly ConcurrentDictionary<Guid, List<string>> _answers = new();

    public void Remember(Guid jobId, string answer) => _answers.AddOrUpdate(jobId, _ => [answer], (_, list) => { lock (list) list.Add(answer); return list; });

    public string Mask(Guid jobId, string text)
    {
        if (!_answers.TryGetValue(jobId, out var list)) return text;
        lock (list) foreach (var a in list) text = text.Replace(a, "****");
        return text;
    }

    public void Forget(Guid jobId) => _answers.TryRemove(jobId, out _);
}

// Creates Job rows, dispatches them to the agent, and records what the agent reports (Phase 5a Ruling 2).
public sealed class JobService(AppDb db, AgentCalls agents, IHubContext<UiHub> ui, JobSecrets secrets, IEnumerable<IJobResultHandler> handlers)
{
    public const string Group = "jobs";
    private const int MaxLines = 200;
    private static readonly JobStatus[] Open = [JobStatus.Queued, JobStatus.Running, JobStatus.NeedsInput];

    public static bool IsOpen(JobStatus s) => Open.Contains(s);

    // The payload records what was asked (never a secret); permission is what answering or cancelling the job needs.
    public async Task<Job> CreateAsync(Guid agentId, JobSpec spec, string type, string permission, string? targetType, string? targetId, Guid? userId,
        object payload)
    {
        var job = new Job
        {
            Id = spec.JobId, Type = type, Status = JobStatus.Queued, AgentId = agentId, Permission = permission,
            TargetType = targetType, TargetId = targetId, CreatedById = userId, PayloadJson = JsonSerializer.Serialize(payload),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        CommandResult sent;
        try { sent = await agents.CallAsync(agentId, c => c.RunJob(spec)); }
        catch (AgentUnavailableException e) { sent = new CommandResult(false, e.Message); }
        catch (HubException e) { sent = new CommandResult(false, e.Message); }
        if (!sent.Ok) await FinishAsync(job, new JobResult(job.Id, false, sent.Error ?? "The agent refused the job.", []));
        else await PushAsync(job, []);
        return job;
    }

    public async Task UpdatedAsync(Guid agentId, JobUpdate update)
    {
        var job = await db.Jobs.FindAsync(update.JobId);
        if (job is null || job.AgentId != agentId || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) return;
        job.StartedAt ??= DateTime.UtcNow;
        job.Status = update.Prompt is null ? JobStatus.Running : JobStatus.NeedsInput; // an interrupted job that reports in is alive
        job.Prompt = update.Prompt?.ToString();
        if (update.Progress is { } p && double.IsFinite(p)) job.Progress = Math.Clamp(p, 0, 1);
        var lines = (update.Lines ?? []).Take(500).Select(l => secrets.Mask(job.Id, l.Length > 500 ? l[..500] : l)).ToArray();
        job.LogText = Append(job.LogText, lines);
        await db.SaveChangesAsync();
        await PushAsync(job, lines);
    }

    public async Task FinishedAsync(Guid agentId, JobResult result)
    {
        var job = await db.Jobs.FindAsync(result.JobId);
        if (job is null || job.AgentId != agentId || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) return;
        await FinishAsync(job, result);
    }

    private async Task FinishAsync(Job job, JobResult result)
    {
        job.Status = result.Ok ? JobStatus.Succeeded : result.Error == "Cancelled." ? JobStatus.Cancelled : JobStatus.Failed;
        job.Error = result.Ok ? null : Clip(secrets.Mask(job.Id, result.Error ?? "The job failed."), 1000);
        if (result.Ok) job.Progress = 1;
        job.Prompt = null;
        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        foreach (var handler in handlers.Where(h => h.Handles(job.Type))) await handler.HandleAsync(job, result, db);
        secrets.Forget(job.Id);
        await PushAsync(job, []);
    }

    // The agent's hello lists what it still has: jobs it doesn't list are interrupted; listed ones come back.
    public async Task HelloAsync(Guid agentId, Guid[] agentJobs)
    {
        var mine = await db.Jobs.Where(j => j.AgentId == agentId && (Open.Contains(j.Status) || j.Status == JobStatus.Interrupted)).ToListAsync();
        foreach (var job in mine)
        {
            if (agentJobs.Contains(job.Id))
            {
                if (job.Status != JobStatus.Interrupted) continue;
                (job.Status, job.Error, job.FinishedAt) = (job.StartedAt is null ? JobStatus.Queued : JobStatus.Running, null, null);
                await db.SaveChangesAsync();
            }
            else if (job.Status != JobStatus.Interrupted)
            {
                await InterruptAsync(job, "The agent restarted while this job was waiting or running.", db, handlers);
                secrets.Forget(job.Id);
            }
            else continue;
            await PushAsync(job, []);
        }
    }

    // Program runs this at startup: nothing can still be running for a panel that just started.
    public static async Task InterruptOpenJobsAsync(AppDb db, IEnumerable<IJobResultHandler> handlers)
    {
        foreach (var job in await db.Jobs.Where(j => Open.Contains(j.Status)).ToListAsync())
            await InterruptAsync(job, "The panel restarted while this job was waiting or running.", db, handlers);
    }

    // An interrupted job tells its handlers it failed, so its mods don't stay Queued for good; a result that still arrives
    // later runs them again with the real outcome (Ruling 6).
    private static async Task InterruptAsync(Job job, string error, AppDb db, IEnumerable<IJobResultHandler> handlers)
    {
        (job.Status, job.Prompt, job.Error, job.FinishedAt) = (JobStatus.Interrupted, null, error, DateTime.UtcNow);
        await db.SaveChangesAsync();
        foreach (var handler in handlers.Where(h => h.Handles(job.Type))) await handler.HandleAsync(job, new JobResult(job.Id, false, error, []), db);
    }

    private Task PushAsync(Job job, string[] lines) => ui.Clients.Group(Group).SendAsync("job", JobDto.From(job), lines);

    private static string Append(string log, string[] lines)
    {
        if (lines.Length == 0) return log;
        var all = (log.Length == 0 ? [] : log.Split('\n')).Concat(lines).ToArray();
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - MaxLines)));
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
