using Contracts;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Features.Jobs;

namespace Panel.Features.Backups;

// Phase 7b: a finished server backup fills in its row; a finished restore records the backup the agent took just before it (with what
// it had to skip). Retention calls the agent, so it runs after the hub's JobFinished returns, never inside it (that would deadlock).
public sealed class BackupJobHandler(IServiceScopeFactory scopes) : IJobResultHandler
{
    public bool Handles(string jobType) => jobType is BackupService.ServerJob or BackupService.RestoreJob;

    public async Task HandleAsync(Job job, JobResult result, AppDb db)
    {
        if (job.Type == BackupService.ServerJob)
        {
            if (await db.Backups.FirstOrDefaultAsync(b => b.JobId == job.Id) is not { } row) return;
            (row.Status, row.Error, row.FinishedAt) = (result.Ok && result.Made is not null ? BackupStatus.Done : BackupStatus.Failed,
                result.Ok ? null : result.Error, DateTime.UtcNow);
            if (result.Made is { } made) (row.FileName, row.SizeBytes, row.Note) = (made.File, made.SizeBytes, made.Note);
            await db.SaveChangesAsync();
            if (row.Status == BackupStatus.Done)
                _ = Task.Run(async () =>
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<BackupService>().ApplyRetentionAsync();
                });
            return;
        }

        var restored = Guid.TryParse(job.TargetId, out var id) ? await db.Backups.FindAsync(id) : null;
        if (restored is not null)
        {
            var failed = result.Ok ? null : $"Last restore failed: {result.Error ?? "no reason given"}";
            restored.Error = failed?.Length > 1000 ? failed[..1000] : failed;
        }
        if (result.Before is { } before && BackupService.IsBackupName(before.File))
        {
            var note = string.Join(" ", new[] { $"Taken before restoring {restored?.FileName ?? "a backup"}.", result.Note, before.Note }.Where(n => n is { Length: > 0 }));
            db.Backups.Add(new Backup
            {
                Id = Guid.NewGuid(), Type = BackupType.Server, Kind = BackupKind.PreRestore, AgentId = job.AgentId, FileName = before.File, SizeBytes = before.SizeBytes,
                Status = BackupStatus.Done, Note = note.Length > 1000 ? note[..1000] : note, CreatedById = job.CreatedById, FinishedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }
}
