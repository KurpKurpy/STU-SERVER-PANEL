using Contracts;
using Panel.Data;
using Panel.Features.Jobs;
using Panel.Features.Mods;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 8: a Workshop download or server install/update that Failed. Cancelled ones were asked for; Interrupted ones (the
// agent restarted) may come back on the next hello. The job's error is already masked; it goes in-app only, never to Discord.
public sealed class UpdateFailedNotifications(Notifier notifier) : IJobResultHandler
{
    public bool Handles(string jobType) => jobType is JobKinds.WorkshopDownload or JobKinds.ServerInstall;

    public Task HandleAsync(Job job, JobResult result, AppDb db)
    {
        if (job.Status != JobStatus.Failed) return Task.CompletedTask;
        var (what, link) = job.Type == JobKinds.WorkshopDownload ? ("A Workshop download", "/mods") : ("A server install or update", "/server/setup");
        notifier.Enqueue(new NotificationEvent(NotificationEvents.UpdateFailed, $"{what} failed{(job.Error is { Length: > 0 } e ? ": " + e.TrimEnd('.') : "")}.",
            $"{what} failed.", link, job.Permission));
        return Task.CompletedTask;
    }
}
