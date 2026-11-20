using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Settings;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 11: the unit's notification settings (settings.manage). The webhook is write-only: the API says set, not working
// or not set, and the audit says it was set or removed, never what it is.
public static class NotificationSettingsEndpoints
{
    public sealed record DiscordDto(bool Set, bool Broken, string? LastError, DateTime? LastErrorAt, DateTime? LastSentAt);
    public sealed record RuleDto(string Type, string Label, bool InApp, bool Discord);
    public sealed record SettingsDto(DiscordDto Discord, int ReminderMinutes, RuleDto[] Rules);
    public sealed record RuleBody(string Type, bool InApp, bool Discord);
    public sealed record SettingsBody(int ReminderMinutes, RuleBody[]? Rules);
    public sealed record UrlBody(string? Url);

    public static void MapNotificationSettings(this WebApplication app)
    {
        var g = app.MapGroup("/api/notifications");
        g.MapGet("/settings", Get).RequirePermission(Permissions.SettingsManage);
        g.MapPut("/settings", Put).RequirePermission(Permissions.SettingsManage);
        g.MapPut("/discord", SetUrl).RequirePermission(Permissions.SettingsManage);
        g.MapPost("/discord/test", Test).RequirePermission(Permissions.SettingsManage);
    }

    private static async Task<SettingsDto> Get(AppDb db, DiscordSettings discord)
    {
        var rules = await db.NotificationRules.AsNoTracking().ToDictionaryAsync(r => r.EventType);
        var s = await discord.StatusAsync();
        return new SettingsDto(new DiscordDto(await discord.UrlAsync() is not null, s.Broken, s.LastError, s.LastErrorAt, s.LastSentAt),
            await discord.ReminderMinutesAsync(),
            NotificationEvents.All.Select(e => rules.GetValueOrDefault(e.Type) is { } r ? new RuleDto(e.Type, e.Label, r.InApp, r.Discord)
                : new RuleDto(e.Type, e.Label, false, false)).ToArray());
    }

    private static async Task<IResult> Put(SettingsBody body, AppDb db, DiscordSettings discord, AuditLog audit)
    {
        if (body.ReminderMinutes is < 0 or > 1440) return ApiError.Field("reminderMinutes", "Between 0 (no reminders) and 1440 minutes.");
        var rules = body.Rules ?? [];
        if (rules.FirstOrDefault(r => !NotificationEvents.IsKnown(r.Type)) is { } unknown) return ApiError.Field("rules", $"Unknown event: {unknown.Type}.");
        foreach (var r in rules)
        {
            if (await db.NotificationRules.FindAsync(r.Type) is { } row) (row.InApp, row.Discord) = (r.InApp, r.Discord);
            else db.NotificationRules.Add(new NotificationRule { EventType = r.Type, InApp = r.InApp, Discord = r.Discord });
        }
        await db.SaveChangesAsync();
        await discord.SetReminderMinutesAsync(body.ReminderMinutes);
        await audit.RecordAsync("notifications.rules", "Changed the notification settings", details: new { body.ReminderMinutes, rules });
        return Results.Ok(await Get(db, discord));
    }

    private static async Task<IResult> SetUrl(UrlBody body, DiscordSettings discord, AuditLog audit)
    {
        if (string.IsNullOrWhiteSpace(body.Url))
        {
            await discord.SetUrlAsync(null);
            await audit.RecordAsync("notifications.discord", "Removed the Discord webhook");
            return Results.Ok();
        }
        if (!DiscordUrl.TryCanonical(body.Url, out var url))
            return ApiError.Field("url", "Paste the webhook's address from Discord: https://discord.com/api/webhooks/… (Server Settings → Integrations → Webhooks).");
        await discord.SetUrlAsync(url);
        await audit.RecordAsync("notifications.discord", "Set the Discord webhook");
        return Results.Ok();
    }

    private static async Task<IResult> Test(DiscordSettings discord, IDiscordWebhook webhook, AppSettingsService settings, CancellationToken ct)
    {
        if (await discord.UrlAsync() is not { } url) return ApiError.Of(409, "not_set", "There's no Discord webhook set (or it can't be read any more): paste it first.");
        // Discord: a webhook that answered 404 must not be used again (final review I2), so not even Test tries it.
        if ((await discord.StatusAsync()).Broken)
            return ApiError.Of(409, "discord_gone", "Discord said this webhook no longer exists. Paste a new one (or the same one, if you've fixed it in Discord).");
        var result = await webhook.SendAsync(url, $"Test message from {DiscordText.Escape(await settings.AppNameAsync())}: Discord notifications work.", ct);
        await discord.RecordAsync(result);
        return result.Outcome == DiscordOutcome.Ok ? Results.Ok() : ApiError.Of(409, result.Outcome == DiscordOutcome.Gone ? "discord_gone" : "discord_failed", result.Error!);
    }
}
