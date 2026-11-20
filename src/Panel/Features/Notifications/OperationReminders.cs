using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 6: each planned operation is reminded once, `minutes` before it starts. The claim is one conditional update, so two
// scans can't both send it, and it doesn't touch Version (a reminder isn't an edit). An edit that moves StartsAt re-arms it.
public static class OperationReminders
{
    public static async Task<int> RunOnceAsync(AppDb db, Notifier notifier, int minutes, DateTime now)
    {
        if (minutes <= 0) return 0;
        var until = now.AddMinutes(minutes);
        var due = await db.Operations.AsNoTracking()
            .Where(o => o.Status == OperationStatus.Planned && o.ReminderSentAt == null && o.StartsAt > now && o.StartsAt <= until).ToListAsync();
        var sent = 0;
        foreach (var op in due)
        {
            if (await db.Operations.Where(o => o.Id == op.Id && o.ReminderSentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.ReminderSentAt, now)) != 1) continue;
            notifier.Enqueue(Notices.OperationReminder(op, (int)Math.Round((op.StartsAt - now).TotalMinutes)));
            sent++;
        }
        return sent;
    }
}

// Runs the scan every minute (Notifications:ReminderScanSeconds; 0 turns it off, as the tests do so they can drive it themselves).
public sealed class ReminderService(IServiceScopeFactory scopes, Notifier notifier, IConfiguration config, ILogger<ReminderService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var seconds = config.GetValue("Notifications:ReminderScanSeconds", 60);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var minutes = await scope.ServiceProvider.GetRequiredService<DiscordSettings>().ReminderMinutesAsync();
                await OperationReminders.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), notifier, minutes, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "The operation reminder scan failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
