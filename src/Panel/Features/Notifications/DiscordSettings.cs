using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Panel.Data;
using Panel.Settings;

namespace Panel.Features.Notifications;

// Whether the last send worked; never holds the URL or exception text (Review Focus 1).
public sealed record DiscordStatus(bool Broken, string? LastError, DateTime? LastErrorAt, DateTime? LastSentAt)
{
    public static readonly DiscordStatus None = new(false, null, null, null);
}

// Phase 6b Rulings 3 and 10: the webhook URL is kept encrypted (design §5) and read back only for sending. A value that can't be
// decrypted any more (the panel's keys changed) reads as not set, so an admin pastes it again.
public sealed class DiscordSettings(AppDb db, IDataProtectionProvider dp, AppSettingsService settings)
{
    public const string UrlKey = "discordWebhook", StatusKey = "discordStatus", ReminderKey = "operationReminderMinutes";
    public const int DefaultReminderMinutes = 60;

    private IDataProtector Protector => dp.CreateProtector("notifications.discord");

    public async Task<string?> UrlAsync()
    {
        if ((await db.Settings.FindAsync(UrlKey))?.Value is not { Length: > 0 } stored) return null;
        try { return DiscordUrl.TryCanonical(Protector.Unprotect(stored), out var url) ? url : null; }
        catch (CryptographicException) { return null; }
    }

    // canonical == null removes it. Either way the status starts over.
    public async Task SetUrlAsync(string? canonical)
    {
        if (canonical is null)
        {
            if (await db.Settings.FindAsync(UrlKey) is { } row) db.Settings.Remove(row);
            await db.SaveChangesAsync();
        }
        else await settings.SetAsync(UrlKey, Protector.Protect(canonical));
        await SaveStatusAsync(DiscordStatus.None);
    }

    public async Task<DiscordStatus> StatusAsync()
    {
        try { return (await db.Settings.FindAsync(StatusKey))?.Value is { Length: > 0 } v ? JsonSerializer.Deserialize<DiscordStatus>(v) ?? DiscordStatus.None : DiscordStatus.None; }
        catch (JsonException) { return DiscordStatus.None; }
    }

    public Task SaveStatusAsync(DiscordStatus status) => settings.SetAsync(StatusKey, JsonSerializer.Serialize(status));

    // A Gone webhook is marked broken and skipped until a new one is pasted (Discord: don't reuse a webhook that returned 404).
    public async Task RecordAsync(DiscordResult result)
    {
        var now = DateTime.UtcNow;
        var s = await StatusAsync();
        await SaveStatusAsync(result.Outcome switch
        {
            DiscordOutcome.Ok => new DiscordStatus(false, null, null, now), // a good send clears an old error (final review I2)
            DiscordOutcome.Gone => s with { Broken = true, LastError = result.Error, LastErrorAt = now },
            _ => s with { LastError = result.Error, LastErrorAt = now },
        });
    }

    public async Task<int> ReminderMinutesAsync() =>
        int.TryParse((await db.Settings.FindAsync(ReminderKey))?.Value, out var m) ? m : DefaultReminderMinutes;

    public Task SetReminderMinutesAsync(int minutes) => settings.SetAsync(ReminderKey, minutes.ToString());
}
