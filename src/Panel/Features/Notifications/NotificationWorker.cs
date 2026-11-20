using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;

namespace Panel.Features.Notifications;

// Phase 6b: takes events off the queue one at a time and writes the in-app rows; Discord texts go on a second, smaller queue with its
// own loop, so a slow or rate-limited Discord never holds up the bell (final review I1). Each item is handled in its own try/catch,
// so nothing can stop the host (a BackgroundService's unhandled exception does).
public sealed class NotificationWorker(Notifier notifier, IServiceScopeFactory scopes, ILogger<NotificationWorker> log) : BackgroundService
{
    private const int KeepDays = 60;
    private readonly Channel<(string Type, string Text)> _discord = Channel.CreateBounded<(string, string)>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _discordDropped;

    protected override Task ExecuteAsync(CancellationToken stop) => Task.WhenAll(InAppLoopAsync(stop), DiscordLoopAsync(stop));

    private async Task InAppLoopAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var e in notifier.Reader.ReadAllAsync(stop))
            {
                try { await HandleAsync(e, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) { log.LogWarning(ex, "Couldn't handle a {Type} notification", e.Type); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task DiscordLoopAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var (type, text) in _discord.Reader.ReadAllAsync(stop))
            {
                // Only the exception's type is logged: a sender's message could hold the webhook URL (Review Focus 1).
                try { await SendToDiscordAsync(text, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) { log.LogWarning("Couldn't send a {Type} notification to Discord ({Error})", type, ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NotificationEvent e, CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        if (await db.NotificationRules.AsNoTracking().FirstOrDefaultAsync(r => r.EventType == e.Type, stop) is not { } rule) return;
        if (rule.InApp)
        {
            try { await WriteRowsAsync(db, scope.ServiceProvider.GetRequiredService<PermissionService>(), e, stop); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Couldn't write {Type} notifications", e.Type); }
        }
        if (rule.Discord && e.DiscordText is { } text && !_discord.Writer.TryWrite((e.Type, text)) && Interlocked.Increment(ref _discordDropped) % 100 == 1)
            log.LogWarning("The Discord queue is full; {Dropped} Discord notifications dropped so far", Interlocked.Read(ref _discordDropped));
    }

    // Discord is optional: no webhook (or one that can't be read, or one Discord said is gone) means nothing is sent.
    private async Task SendToDiscordAsync(string text, CancellationToken stop)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<DiscordSettings>();
        if (await settings.UrlAsync() is not { } url || (await settings.StatusAsync()).Broken || !DiscordUrl.TryCanonical(url, out var target)) return;
        await settings.RecordAsync(await scope.ServiceProvider.GetRequiredService<IDiscordWebhook>().SendAsync(target, text, stop));
    }

    // Recipients are worked out now, from the permission guarding the linked page; disabled members get nothing (Ruling 4).
    private static async Task WriteRowsAsync(AppDb db, PermissionService perms, NotificationEvent e, CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        foreach (var user in await db.Users.AsNoTracking().ToListAsync(stop))
        {
            if (AuthEndpoints.IsDisabled(user) || !(await perms.EffectiveAsync(user.Id)).Contains(e.Permission)) continue;
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(), UserId = user.Id, EventType = e.Type, CreatedAt = now,
                Text = e.Text.Length > 300 ? e.Text[..300] : e.Text, Link = e.Link is { Length: > 200 } l ? l[..200] : e.Link,
            });
        }
        await db.SaveChangesAsync(stop);
        // ponytail: a global prune on every write; a daily pass if writes ever get heavy.
        await db.UserNotifications.Where(n => n.CreatedAt < now.AddDays(-KeepDays)).ExecuteDeleteAsync(stop);
    }
}
