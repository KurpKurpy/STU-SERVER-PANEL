using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Profiles;

public record PresetDto(Guid Id, string Name, JsonElement Values, bool IsBuiltIn, DateTime UpdatedAt);
public record PresetBody(string? Name, Dictionary<string, JsonElement>? Values);

// A library of difficulty values. Profiles copy them (plan Ruling 6), so editing a preset never changes a server.
public static class PresetEndpoints
{
    public static void MapPresets(this WebApplication app)
    {
        var g = app.MapGroup("/api/difficulty-presets");
        g.MapGet("", List).RequirePermission(Permissions.ProfilesView);
        g.MapPost("", Create).RequirePermission(Permissions.ProfilesManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.ProfilesManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.ProfilesManage);
    }

    private static PresetDto Dto(DifficultyPreset p) => new(p.Id, p.Name, JsonDocument.Parse(p.ValuesJson).RootElement.Clone(), p.IsBuiltIn, p.UpdatedAt);

    private static async Task<PresetDto[]> List(AppDb db) =>
        (await db.DifficultyPresets.OrderByDescending(p => p.IsBuiltIn).ThenBy(p => p.Name).ToListAsync()).Select(Dto).ToArray();

    // Returns an error result, or null after copying the validated values onto the preset.
    private static async Task<IResult?> Check(PresetBody body, DifficultyPreset preset, AppDb db)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > 40) return ApiError.Field("name", "Name must be 1–40 characters.");
        if (await db.DifficultyPresets.AnyAsync(p => p.Id != preset.Id && p.Name == name)) return ApiError.Of(409, "conflict", $"A preset called {name} already exists.");
        var values = body.Values ?? [];
        foreach (var (key, value) in values)
        {
            var cfg = ConfigCatalog.All.FirstOrDefault(k => k.File == ConfigCatalog.Difficulty && k.Name == key);
            if (cfg is null) return ApiError.Field($"values.{key}", $"\"{key}\" isn't a difficulty option.");
            if (ConfigFiles.Check(cfg, value) is { } error) return ApiError.Field($"values.{key}", error);
        }
        (preset.Name, preset.ValuesJson, preset.UpdatedAt) = (name, JsonSerializer.Serialize(values), DateTime.UtcNow);
        return null;
    }

    private static async Task<IResult> Create(PresetBody body, AppDb db, AuditLog audit)
    {
        var preset = new DifficultyPreset { Id = Guid.NewGuid() };
        if (await Check(body, preset, db) is { } error) return error;
        db.DifficultyPresets.Add(preset);
        await db.SaveChangesAsync();
        await audit.RecordAsync("preset.created", $"Created difficulty preset {preset.Name}", "preset", preset.Id.ToString());
        return Results.Created($"/api/difficulty-presets/{preset.Id}", Dto(preset));
    }

    private static async Task<IResult> Update(Guid id, PresetBody body, AppDb db, AuditLog audit)
    {
        var preset = await db.DifficultyPresets.FindAsync(id);
        if (preset is null) return ApiError.Of(404, "not_found", "No such preset.");
        if (preset.IsBuiltIn) return ApiError.Of(409, "built_in", "Recruit, Regular and Veteran are Bohemia's presets and can't change. Copy one instead.");
        if (await Check(body, preset, db) is { } error) return error;
        await db.SaveChangesAsync();
        await audit.RecordAsync("preset.updated", $"Changed difficulty preset {preset.Name}", "preset", id.ToString());
        return Results.Ok(Dto(preset));
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AuditLog audit)
    {
        var preset = await db.DifficultyPresets.FindAsync(id);
        if (preset is null) return ApiError.Of(404, "not_found", "No such preset.");
        if (preset.IsBuiltIn) return ApiError.Of(409, "built_in", "Recruit, Regular and Veteran are Bohemia's presets and can't be deleted.");
        db.DifficultyPresets.Remove(preset);
        await db.SaveChangesAsync();
        await audit.RecordAsync("preset.deleted", $"Deleted difficulty preset {preset.Name}", "preset", id.ToString());
        return Results.NoContent();
    }
}
