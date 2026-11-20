using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Jobs;
using Panel.Hubs;
using Panel.Settings;

namespace Panel.Features.Backups;

// Phase 7b: starts console and server backups, restores server backups (as agent jobs), deletes, and keeps the newest N scheduled and
// pre-update backups of each type (per agent for server backups). Manual and pre-restore backups are only ever deleted by a person.
public sealed partial class BackupService(AppDb db, JobService jobs, AgentCalls agents, AgentRegistry registry, ConsoleBackups console, AppSettingsService settings,
    AuditLog audit)
{
    public const string ServerJob = "backup.server", RestoreJob = "backup.restore";
    public static readonly string[] Parts = ["profiles", "configs", "missions", "keys"];

    [GeneratedRegex(@"^(server|console)-\d{8}-\d{6}(-[a-z-]{1,20})?\.zip$")]
    private static partial Regex FileShape();

    public static bool IsBackupName(string? name) => name is not null && FileShape().IsMatch(name);

    public static string Label(BackupKind kind) => kind switch
    {
        BackupKind.Scheduled => "scheduled", BackupKind.PreUpdate => "pre-update", BackupKind.PreRestore => "before-restore", _ => "manual",
    };

    public async Task<Backup> StartAsync(BackupType type, BackupKind kind, Guid? agentId, Guid? userId)
    {
        var row = new Backup { Id = Guid.NewGuid(), Type = type, Kind = kind, AgentId = type == BackupType.Server ? agentId : null, Status = BackupStatus.Running, CreatedById = userId };
        db.Backups.Add(row);
        if (type == BackupType.Console)
        {
            await db.SaveChangesAsync();
            console.Queue(row.Id, Label(kind));
        }
        else
        {
            var spec = new JobSpec(Guid.NewGuid(), JobKind.ServerBackup, null, null, null, [], Label: Label(kind));
            row.JobId = spec.JobId;
            await db.SaveChangesAsync();
            await jobs.CreateAsync(agentId!.Value, spec, ServerJob, Permissions.BackupsManage, "backup", row.Id.ToString(), userId, new { kind = kind.ToString() });
        }
        await audit.RecordAsync("backup.created", $"Started a {type.ToString().ToLowerInvariant()} backup ({Label(kind)})", "backup", row.Id.ToString(),
            new { type = type.ToString(), kind = kind.ToString(), agentId }, actorId: userId);
        return (await db.Backups.FindAsync(row.Id))!;
    }

    public async Task<(Job? Job, IResult? Error)> RestoreAsync(Backup b, string[]? parts, Guid? userId)
    {
        if (b.Type == BackupType.Console)
            return (null, ApiError.Of(400, "console_restore", "A console backup is restored on the box: stop the 13th Console service, run Panel.exe restore <zip>, then start it again (the deploy guide's \"Restoring the console\")."));
        if (b.Status != BackupStatus.Done || b.FileName is null || b.AgentId is not { } agentId) return (null, ApiError.Of(409, "not_ready", "Only a finished backup can be restored."));
        if (parts is not { Length: > 0 } || parts.Any(p => !Parts.Contains(p)))
            return (null, ApiError.Field("parts", "Pick what to restore: profiles, configs, missions, keys."));
        // Final review I4: the agent checks this too, but by then the person has left the dialog.
        var profiles = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where i.AgentId == agentId select p.Id).ToListAsync();
        if (profiles.Any(id => registry.Status(id) is { State: not (ServerState.Offline or ServerState.Error) }))
            return (null, ApiError.Of(409, "servers_running", "Stop every server on this agent first"));
        var spec = new JobSpec(Guid.NewGuid(), JobKind.ServerRestore, null, null, null, [], Backup: b.FileName, Parts: [.. parts.Distinct()]);
        var job = await jobs.CreateAsync(agentId, spec, RestoreJob, Permissions.BackupsManage, "backup", b.Id.ToString(), userId, new { file = b.FileName, parts });
        if (job.Status == JobStatus.Failed) return (null, ApiError.Of(409, "agent_refused", job.Error ?? "The agent refused the restore."));
        await audit.RecordAsync("backup.restored", $"Started restoring {string.Join(", ", parts)} from {b.FileName}", "backup", b.Id.ToString(),
            new { file = b.FileName, parts }, actorId: userId);
        return (job, null);
    }

    // The zip goes first (console: here; server: its agent, which also removes the copy elsewhere), then the row.
    public async Task<IResult?> DeleteAsync(Backup b, Guid? userId)
    {
        if (b.Status == BackupStatus.Running) return ApiError.Of(409, "running", "That backup is still being made.");
        if (b.FileName is { } name)
        {
            if (!IsBackupName(name)) return ApiError.Of(409, "bad_name", "That backup's file name isn't one the console made.");
            if (b.Type == BackupType.Console) console.Delete(name);
            else if (b.AgentId is { } agentId)
            {
                try
                {
                    var r = await agents.CallAsync(agentId, c => c.DeleteBackup(name));
                    if (!r.Ok && r.Error?.Contains("isn't on this agent") != true) return ApiError.Of(409, "agent_refused", r.Error ?? "The agent refused.");
                }
                catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
                catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
            }
        }
        db.Backups.Remove(b);
        await db.SaveChangesAsync();
        await audit.RecordAsync("backup.deleted", $"Deleted {b.FileName ?? "a failed backup"}", "backup", b.Id.ToString(), new { file = b.FileName }, actorId: userId);
        return null;
    }

    // Plan Global Constraints / Review Focus 3: the newest N scheduled and pre-update backups stay; nothing else is touched. A server zip whose
    // agent can't be reached stays for next time.
    public async Task ApplyRetentionAsync()
    {
        var keep = new Dictionary<BackupKind, int>
        {
            [BackupKind.Scheduled] = Math.Max(1, await settings.IntAsync(AppSettingsService.BackupKeepScheduledKey, 14)),
            [BackupKind.PreUpdate] = Math.Max(1, await settings.IntAsync(AppSettingsService.BackupKeepPreUpdateKey, 10)),
        };
        var rows = await db.Backups.Where(b => b.Status == BackupStatus.Done && (b.Kind == BackupKind.Scheduled || b.Kind == BackupKind.PreUpdate)).ToListAsync();
        foreach (var group in rows.GroupBy(b => (b.Type, b.Kind, b.AgentId)))
            foreach (var old in group.OrderByDescending(b => b.CreatedAt).Skip(keep[group.Key.Kind]).ToList())
            {
                if (old.FileName is { } name && IsBackupName(name))
                {
                    if (old.Type == BackupType.Console) console.Delete(name);
                    else
                    {
                        try
                        {
                            var r = await agents.CallAsync(old.AgentId!.Value, c => c.DeleteBackup(name));
                            if (!r.Ok && r.Error?.Contains("isn't on this agent") != true) continue;
                        }
                        catch (Exception e) when (e is AgentUnavailableException or HubException) { continue; }
                    }
                }
                db.Backups.Remove(old);
            }
        await db.SaveChangesAsync();
    }

    // Phase 7b: the agents' automatic safety copies older than the setting (0 = never); returns how many went.
    public async Task<int> PruneSafetyCopiesAsync(IEnumerable<Guid> agentIds)
    {
        var days = await settings.IntAsync(AppSettingsService.SafetyCopyDaysKey, 30);
        if (days <= 0) return 0;
        var total = 0;
        foreach (var agentId in agentIds)
            try { total += await agents.CallAsync(agentId, c => c.PruneSafetyCopies(days)); }
            catch (Exception e) when (e is AgentUnavailableException or HubException) { }
        return total;
    }
}
