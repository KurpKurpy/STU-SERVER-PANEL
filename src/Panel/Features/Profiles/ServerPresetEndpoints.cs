using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Profiles;

public record ServerPresetDto(Guid Id, string Name, string Description, Dictionary<string, JsonElement> Basic,
    Dictionary<string, JsonElement> Launch, bool IsBuiltIn, DateTime? UpdatedAt);
public record ServerPresetBody(string? Name, string? Description, Dictionary<string, JsonElement>? Basic, Dictionary<string, JsonElement>? Launch);

// Performance presets: the built-ins from ConfigCatalog, then the ones admins saved. A profile copies the values into its
// draft (like difficulty presets, plan Ruling 6), so changing or deleting a preset never changes a server.
public static class ServerPresetEndpoints
{
    public static void MapServerPresets(this WebApplication app)
    {
        var g = app.MapGroup("/api/server-presets");
        g.MapGet("", List).RequirePermission(Permissions.ProfilesView);
        g.MapPost("", Create).RequirePermission(Permissions.ProfilesManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.ProfilesManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.ProfilesManage);
    }

    private static Dictionary<string, JsonElement> Values(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    private static ServerPresetDto Dto(ServerPreset p) => new(p.Id, p.Name, p.Description, Values(p.BasicJson), Values(p.LaunchJson), false, p.UpdatedAt);

    private static async Task<ServerPresetDto[]> List(AppDb db) =>
        ConfigCatalog.BuiltInServerPresets.Select(b => new ServerPresetDto(b.Id, b.Name, b.Description, b.Basic.ToDictionary(), b.Launch.ToDictionary(), true, null))
            .Concat((await db.ServerPresets.OrderBy(p => p.Name).ToListAsync()).Select(Dto)).ToArray();

    private static bool BuiltIn(Guid id) => ConfigCatalog.BuiltInServerPresets.Any(b => b.Id == id);

    // Returns an error result, or null after copying the validated values onto the preset.
    private static async Task<IResult?> Check(ServerPresetBody body, ServerPreset preset, AppDb db)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > 40 || name.Any(char.IsControl)) return ApiError.Field("name", "Name must be 1–40 characters on one line.");
        var description = (body.Description ?? "").Trim();
        if (description.Length > 200 || description.Any(char.IsControl)) return ApiError.Field("description", "Description: one line of at most 200 characters.");
        var lower = name.ToLowerInvariant();
        if (ConfigCatalog.BuiltInServerPresets.Any(b => b.Name.ToLowerInvariant() == lower)
            || await db.ServerPresets.AnyAsync(p => p.Id != preset.Id && p.Name.ToLower() == lower))
            return ApiError.Of(409, "conflict", $"A preset called {name} already exists.");

        // Keys are matched like the config files (case-insensitive) and stored in the catalog's spelling.
        var basic = new Dictionary<string, JsonElement>();
        var launch = new Dictionary<string, JsonElement>();
        foreach (var (file, given, kept, allowed) in new[]
                 { (ConfigCatalog.Basic, body.Basic, basic, ConfigCatalog.PresetBasicKeys), (ConfigCatalog.Launch, body.Launch, launch, ConfigCatalog.PresetLaunchKeys) })
            foreach (var (key, value) in given ?? [])
            {
                var cfg = ConfigCatalog.Find(file, key);
                var canonical = cfg?.Name ?? key;
                if (cfg is null || !allowed.Contains(cfg.Name) || kept.ContainsKey(canonical))
                    return ApiError.Field($"{file}.{canonical}", $"\"{canonical}\" isn't a performance setting, so a server preset can't hold it.");
                if (ConfigFiles.Check(cfg, value) is { } error) return ApiError.Field($"{file}.{canonical}", error);
                kept[canonical] = value;
            }
        (preset.Name, preset.Description, preset.BasicJson, preset.LaunchJson, preset.UpdatedAt) =
            (name, description, JsonSerializer.Serialize(basic), JsonSerializer.Serialize(launch), DateTime.UtcNow);
        return null;
    }

    private static async Task<IResult> Create(ServerPresetBody body, AppDb db, AuditLog audit)
    {
        var preset = new ServerPreset { Id = Guid.NewGuid() };
        if (await Check(body, preset, db) is { } error) return error;
        db.ServerPresets.Add(preset);
        await db.SaveChangesAsync();
        await audit.RecordAsync("serverPreset.created", $"Created server preset {preset.Name}", "serverPreset", preset.Id.ToString());
        return Results.Created($"/api/server-presets/{preset.Id}", Dto(preset));
    }

    private static async Task<IResult> Update(Guid id, ServerPresetBody body, AppDb db, AuditLog audit)
    {
        if (BuiltIn(id)) return ApiError.Of(409, "built_in", "Built-in presets can't change. Save your own instead.");
        var preset = await db.ServerPresets.FindAsync(id);
        if (preset is null) return ApiError.Of(404, "not_found", "No such preset.");
        if (await Check(body, preset, db) is { } error) return error;
        await db.SaveChangesAsync();
        await audit.RecordAsync("serverPreset.updated", $"Changed server preset {preset.Name}", "serverPreset", id.ToString());
        return Results.Ok(Dto(preset));
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AuditLog audit)
    {
        if (BuiltIn(id)) return ApiError.Of(409, "built_in", "Built-in presets can't be deleted.");
        var preset = await db.ServerPresets.FindAsync(id);
        if (preset is null) return ApiError.Of(404, "not_found", "No such preset.");
        db.ServerPresets.Remove(preset);
        await db.SaveChangesAsync();
        await audit.RecordAsync("serverPreset.deleted", $"Deleted server preset {preset.Name}", "serverPreset", id.ToString());
        return Results.NoContent();
    }
}
