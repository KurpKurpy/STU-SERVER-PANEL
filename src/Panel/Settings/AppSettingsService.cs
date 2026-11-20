using Panel.Data;

namespace Panel.Settings;

public sealed class AppSettingsService(AppDb db, IConfiguration config)
{
    public const string AppNameKey = "appName";
    public const string TwoFactorRolesKey = "twoFactorRequiredRoles";
    public const string SteamUsernameKey = "steamUsername";

    // The Steam account SteamCMD signs in as (Phase 5a Ruling 3). Only the name: the password is never stored.
    // Phase 7b: schedules run in the unit's time zone; backups keep the newest N scheduled and pre-update zips; safety copies are pruned
    // after so many days (0 = never).
    public const string UnitTimeZoneKey = "unitTimeZone", BackupKeepScheduledKey = "backupKeepScheduled", BackupKeepPreUpdateKey = "backupKeepPreUpdate",
        SafetyCopyDaysKey = "safetyCopyDays";
    public const string DefaultTimeZone = "Australia/Sydney";

    public async Task<TimeZoneInfo> UnitTimeZoneAsync() =>
        TimeZoneInfo.TryFindSystemTimeZoneById((await db.Settings.FindAsync(UnitTimeZoneKey))?.Value ?? DefaultTimeZone, out var tz) ? tz
            : TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);

    public async Task<int> IntAsync(string key, int fallback) =>
        int.TryParse((await db.Settings.FindAsync(key))?.Value, out var v) ? v : fallback;

    public async Task<string?> SteamUsernameAsync() => (await db.Settings.FindAsync(SteamUsernameKey))?.Value is { Length: > 0 } v ? v : null;

    public async Task SetSteamUsernameAsync(string? username)
    {
        var row = await db.Settings.FindAsync(SteamUsernameKey);
        if (row is null) db.Settings.Add(row = new Data.AppSetting { Key = SteamUsernameKey });
        row.Value = username ?? "";
        await db.SaveChangesAsync();
    }

    public async Task<string> AppNameAsync() => (await db.Settings.FindAsync(AppNameKey))?.Value ?? "13th Console";

    // Empty means "no roles" (tests and units that opt out); a missing value falls back to config, then the safe default.
    public async Task<string[]> TwoFactorRequiredRolesAsync()
    {
        var raw = (await db.Settings.FindAsync(TwoFactorRolesKey))?.Value
                  ?? config["Auth:TwoFactorRequiredRoles"]
                  ?? "Owner,S1,S4";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task SetAsync(string key, string value)
    {
        var row = await db.Settings.FindAsync(key);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }
}
