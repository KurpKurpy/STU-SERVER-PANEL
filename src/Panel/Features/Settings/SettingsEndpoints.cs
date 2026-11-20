using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Settings;

namespace Panel.Features.Settings;

public record PublicSettings(string AppName);
// Phase 7b: UnitTimeZone (an IANA zone like Australia/Sydney); left out, it stays as it is.
public record SettingsDto(string AppName, string[] TwoFactorRequiredRoles, string? UnitTimeZone = null);

public static class SettingsEndpoints
{
    public static void MapSettings(this WebApplication app)
    {
        app.MapGet("/api/settings/public", async (AppSettingsService s) => new PublicSettings(await s.AppNameAsync()));
        app.MapGet("/api/settings", Get).RequirePermission(Permissions.SettingsManage);
        app.MapPut("/api/settings", Put).RequirePermission(Permissions.SettingsManage);

    }

    private static async Task<SettingsDto> Get(AppSettingsService s) =>
        new(await s.AppNameAsync(), await s.TwoFactorRequiredRolesAsync(), (await s.UnitTimeZoneAsync()).Id);

    private static async Task<IResult> Put(SettingsDto r, AppSettingsService s, AppDb db, AuditLog audit)
    {
        var name = (r.AppName ?? "").Trim();
        if (name.Length is 0 or > 40) return ApiError.Of(400, "validation", "App name must be 1–40 characters.");
        var roles = (r.TwoFactorRequiredRoles ?? []).Select(x => x.Trim()).Distinct().ToArray();
        var known = await db.Roles.Select(x => x.Name!).ToListAsync();
        var unknown = roles.Except(known).ToArray();
        if (unknown.Length > 0) return ApiError.Of(400, "validation", $"Unknown roles: {string.Join(", ", unknown)}");
        TimeZoneInfo? zone = null;
        if (r.UnitTimeZone is { } tzId && !TimeZoneInfo.TryFindSystemTimeZoneById(tzId.Trim(), out zone))
            return ApiError.Field("unitTimeZone", $"\"{tzId}\" isn't a time zone; use a name like Australia/Sydney.");

        await s.SetAsync(AppSettingsService.AppNameKey, name);
        await s.SetAsync(AppSettingsService.TwoFactorRolesKey, string.Join(',', roles));
        if (zone is not null && zone.Id != (await s.UnitTimeZoneAsync()).Id)
        {
            await s.SetAsync(AppSettingsService.UnitTimeZoneKey, r.UnitTimeZone!.Trim());
            await Panel.Features.Schedules.ScheduleEndpoints.RecomputeAsync(db, zone);
        }
        await audit.RecordAsync("settings.updated", "Changed unit settings", "settings", null, new { appName = name, twoFactorRequiredRoles = roles });
        return Results.Ok(await Get(s));
    }
}
