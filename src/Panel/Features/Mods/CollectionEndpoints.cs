using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles;

namespace Panel.Features.Mods;

public record CollectionItemBody(Guid ModId, bool Client, bool ServerOnly, bool Headless, bool Optional, bool Enabled);
public record CollectionBody(string? Name, string? Description, CollectionItemBody[]? Items);
public record ImportBody(string? Name, string? Text);
public record DuplicateBody(string? Name);
public record CollectionItemDto(Guid ModId, string Name, long? WorkshopId, string? LocalFolder, string ModStatus, bool UpdateAvailable,
    bool Client, bool ServerOnly, bool Headless, bool Optional, bool Enabled);
public record CollectionDto(Guid Id, string Name, string Description, DateTime UpdatedAt, CollectionItemDto[] Items, string[] UsedBy);
public record CollectionSummary(Guid Id, string Name, string Description, DateTime UpdatedAt, int Count, int NotInstalled, string[] UsedBy);

// Mod lists (Phase 5a Ruling 6). Bulk actions (enable all, sort, copy Client → Headless …) run in the browser; this saves the result.
public static class CollectionEndpoints
{
    private const int MaxItems = 500;

    public static void MapCollections(this WebApplication app)
    {
        var g = app.MapGroup("/api/collections");
        g.MapGet("", List).RequirePermission(Permissions.ModsView);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.ModsView);
        g.MapPost("", Create).RequirePermission(Permissions.CollectionsManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.CollectionsManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.CollectionsManage);
        g.MapPost("/{id:guid}/duplicate", Duplicate).RequirePermission(Permissions.CollectionsManage);
        g.MapPost("/import", Import).RequirePermission(Permissions.CollectionsManage);
        g.MapGet("/{id:guid}/export", Export).RequirePermission(Permissions.ModsView);
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    // Profiles whose head or applied revision uses each list: editing one changes their next Start (Ruling 7).
    public static async Task<ILookup<Guid, string>> UsedByAsync(AppDb db)
    {
        var profiles = await db.Profiles.Select(p => new { p.Name, p.ModCollectionId, p.AppliedRevisionId }).ToListAsync();
        var applied = profiles.Where(p => p.AppliedRevisionId is not null).Select(p => p.AppliedRevisionId!.Value).ToList();
        var revisions = await db.ProfileRevisions.Where(r => applied.Contains(r.Id)).Select(r => new { r.Id, r.SnapshotJson }).ToListAsync();
        var appliedList = revisions.ToDictionary(r => r.Id, r => ModLaunch.ListOf(r.SnapshotJson));
        return profiles.SelectMany(p => new[] { p.ModCollectionId, p.AppliedRevisionId is { } r ? appliedList.GetValueOrDefault(r) : null }
                .OfType<Guid>().Distinct().Select(id => (id, p.Name)))
            .ToLookup(x => x.id, x => x.Name);
    }

    private static async Task<CollectionSummary[]> List(AppDb db)
    {
        var used = await UsedByAsync(db);
        var counts = await (from i in db.ModCollectionItems join m in db.Mods on i.ModId equals m.Id
                group m by i.CollectionId into g
                select new { Id = g.Key, Count = g.Count(), NotInstalled = g.Count(m => m.Status != ModStatus.Installed) }).ToListAsync();
        return (await db.ModCollections.OrderBy(c => c.Name).ToListAsync()).Select(c =>
        {
            var n = counts.FirstOrDefault(x => x.Id == c.Id);
            return new CollectionSummary(c.Id, c.Name, c.Description, c.UpdatedAt, n?.Count ?? 0, n?.NotInstalled ?? 0, used[c.Id].Distinct().Order().ToArray());
        }).ToArray();
    }

    public static async Task<CollectionDto?> DtoAsync(AppDb db, Guid id)
    {
        var c = await db.ModCollections.FindAsync(id);
        if (c is null) return null;
        var items = await db.ModCollectionItems.Include(i => i.Mod).Where(i => i.CollectionId == id).OrderBy(i => i.Order).ToListAsync();
        return new CollectionDto(c.Id, c.Name, c.Description, c.UpdatedAt,
            items.Select(i => new CollectionItemDto(i.ModId, i.Mod!.Name, i.Mod.WorkshopId, i.Mod.LocalFolder, i.Mod.Status.ToString(), ModLibrary.UpdateAvailable(i.Mod),
                i.Client, i.ServerOnly, i.Headless, i.Optional, i.Enabled)).ToArray(),
            (await UsedByAsync(db))[id].Distinct().Order().ToArray());
    }

    private static async Task<IResult> Get(Guid id, AppDb db) =>
        await DtoAsync(db, id) is { } dto ? Results.Ok(dto) : ApiError.Of(404, "not_found", "No such mod list.");

    // Returns an error, or null after writing name, description and items onto the list.
    private static async Task<IResult?> CheckAsync(AppDb db, ModCollection c, CollectionBody body, HttpContext ctx)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl)) return ApiError.Field("name", "Name must be 1–60 characters on one line.");
        var description = (body.Description ?? "").Trim();
        if (description.Length > 500) return ApiError.Field("description", "Description: at most 500 characters.");
        var lower = name.ToLowerInvariant();
        if (await db.ModCollections.AnyAsync(x => x.Id != c.Id && x.Name.ToLower() == lower)) return ApiError.Of(409, "conflict", $"A mod list called {name} already exists.");

        var items = body.Items ?? [];
        if (items.Length > MaxItems) return ApiError.Field("items", $"A list holds at most {MaxItems} mods.");
        if (items.Select(i => i.ModId).Distinct().Count() != items.Length) return ApiError.Field("items", "A mod can be in a list only once.");
        var ids = items.Select(i => i.ModId).ToList();
        var mods = await db.Mods.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        foreach (var i in items)
        {
            if (!mods.TryGetValue(i.ModId, out var mod)) return ApiError.Field("items", "One of the mods isn't in the library.");
            if (!(i.Client || i.ServerOnly || i.Headless || i.Optional)) return ApiError.Field("items", $"{mod.Name}: pick Client, Server only, Headless or Optional.");
            if (i.Client && i.ServerOnly) return ApiError.Field("items", $"{mod.Name}: a mod is either loaded by clients (-mod) or by the server only (-serverMod).");
            if (i.Optional && (i.Client || i.ServerOnly || i.Headless)) return ApiError.Field("items", $"{mod.Name}: an optional mod isn't loaded by the server or headless clients.");
        }

        (c.Name, c.Description, c.UpdatedAt, c.UpdatedById) = (name, description, DateTime.UtcNow, UserId(ctx));
        db.ModCollectionItems.RemoveRange(db.ModCollectionItems.Where(i => i.CollectionId == c.Id));
        db.ModCollectionItems.AddRange(items.Select((i, n) => new ModCollectionItem
        {
            CollectionId = c.Id, ModId = i.ModId, Order = n, Client = i.Client, ServerOnly = i.ServerOnly, Headless = i.Headless, Optional = i.Optional, Enabled = i.Enabled,
        }));
        return null;
    }

    private static async Task<IResult> Create(CollectionBody body, AppDb db, AuditLog audit, HttpContext ctx)
    {
        var c = new ModCollection { Id = Guid.NewGuid() };
        if (await CheckAsync(db, c, body, ctx) is { } error) return error;
        db.ModCollections.Add(c);
        await db.SaveChangesAsync();
        await audit.RecordAsync("collection.created", $"Created mod list {c.Name}", "collection", c.Id.ToString());
        return Results.Created($"/api/collections/{c.Id}", await DtoAsync(db, c.Id));
    }

    private static async Task<IResult> Update(Guid id, CollectionBody body, AppDb db, AuditLog audit, HttpContext ctx)
    {
        var c = await db.ModCollections.FindAsync(id);
        if (c is null) return ApiError.Of(404, "not_found", "No such mod list.");
        if (await CheckAsync(db, c, body, ctx) is { } error) return error;
        await db.SaveChangesAsync();
        await audit.RecordAsync("collection.updated", $"Changed mod list {c.Name}", "collection", c.Id.ToString(), new { mods = body.Items?.Length ?? 0 });
        return Results.Ok(await DtoAsync(db, id));
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AuditLog audit)
    {
        var c = await db.ModCollections.FindAsync(id);
        if (c is null) return ApiError.Of(404, "not_found", "No such mod list.");
        var used = (await UsedByAsync(db))[id].Distinct().Order().ToArray();
        if (used.Length > 0) return ApiError.Of(409, "in_use", $"These profiles use it: {string.Join(", ", used)}. Pick another mod list for them first.");
        if (await Panel.Features.Operations.Checklist.PlannedUsingAsync(db, o => o.ModCollectionId == id) is { } ops)
            return ApiError.Of(409, "in_use", $"These planned operations use it: {ops}. Pick another mod list for them first.");
        db.ModCollections.Remove(c);
        await db.SaveChangesAsync();
        await audit.RecordAsync("collection.deleted", $"Deleted mod list {c.Name}", "collection", id.ToString());
        return Results.NoContent();
    }

    private static async Task<string> FreeNameAsync(AppDb db, string wanted)
    {
        var name = wanted.Length > 60 ? wanted[..60] : wanted;
        for (var n = 2; await db.ModCollections.AnyAsync(c => c.Name.ToLower() == name.ToLower()); n++)
        {
            var suffix = $" ({n})";
            name = (wanted.Length + suffix.Length > 60 ? wanted[..(60 - suffix.Length)] : wanted) + suffix;
        }
        return name;
    }

    private static async Task<IResult> Duplicate(Guid id, DuplicateBody body, AppDb db, AuditLog audit, HttpContext ctx)
    {
        var source = await DtoAsync(db, id);
        if (source is null) return ApiError.Of(404, "not_found", "No such mod list.");
        var c = new ModCollection { Id = Guid.NewGuid() };
        var name = string.IsNullOrWhiteSpace(body.Name) ? await FreeNameAsync(db, source.Name + " copy") : body.Name;
        if (await CheckAsync(db, c, new CollectionBody(name, source.Description,
                source.Items.Select(i => new CollectionItemBody(i.ModId, i.Client, i.ServerOnly, i.Headless, i.Optional, i.Enabled)).ToArray()), ctx) is { } error)
            return error;
        db.ModCollections.Add(c);
        await db.SaveChangesAsync();
        await audit.RecordAsync("collection.created", $"Duplicated mod list {source.Name} as {c.Name}", "collection", c.Id.ToString());
        return Results.Created($"/api/collections/{c.Id}", await DtoAsync(db, c.Id));
    }

    // A Launcher preset or a pasted ID list becomes a list; mods not in the library are added as Not installed (Ruling 13).
    private static async Task<IResult> Import(ImportBody body, AppDb db, IWorkshopClient workshop, AuditLog audit, HttpContext ctx)
    {
        var text = body.Text ?? "";
        if (text.Length > ModLibrary.MaxText) return ApiError.Field("text", "The preset can be at most 512 KB.");
        var preset = LauncherPreset.Parse(text);
        if (preset.Mods.Length == 0) return ApiError.Field("text", preset.Problems.FirstOrDefault() ?? "No mods found in that preset.");
        if (preset.Mods.Length > MaxItems) return ApiError.Field("text", $"A list holds at most {MaxItems} mods.");
        var problems = preset.Problems.ToList();
        var workshopIds = preset.Mods.Where(m => m.WorkshopId is not null).Select(m => m.WorkshopId!.Value).ToList();
        try
        {
            var (_, _, refused) = await ModLibrary.AddWorkshopAsync(db, workshop, workshopIds);
            problems.AddRange(refused.Select(r => $"Workshop item {r.WorkshopId}: {r.Reason}"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ApiError.Of(502, "steam_unreachable", "Steam's Workshop service couldn't be reached. Try again in a moment.");
        }

        var library = await db.Mods.ToListAsync();
        var items = new List<CollectionItemBody>();
        foreach (var m in preset.Mods)
        {
            var mod = m.WorkshopId is { } wid ? library.FirstOrDefault(x => x.WorkshopId == wid)
                : library.FirstOrDefault(x => string.Equals(x.LocalFolder, m.LocalFolder, StringComparison.OrdinalIgnoreCase)
                    || x.LocalFolder is not null && string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
            if (mod is null)
            {
                if (m.LocalFolder is not null)
                    problems.Add($"Local mod {m.LocalFolder} isn't on the server: copy it into the agent's localmods folder, refresh the library, then add it.");
                continue;
            }
            if (items.All(i => i.ModId != mod.Id)) items.Add(new CollectionItemBody(mod.Id, true, false, false, false, true));
        }

        var c = new ModCollection { Id = Guid.NewGuid() };
        var name = await FreeNameAsync(db, string.IsNullOrWhiteSpace(body.Name) ? preset.Name ?? "Imported mod list" : body.Name.Trim());
        if (await CheckAsync(db, c, new CollectionBody(name, "Imported from a Launcher preset.", items.ToArray()), ctx) is { } error) return error;
        db.ModCollections.Add(c);
        await db.SaveChangesAsync();
        await audit.RecordAsync("collection.imported", $"Imported mod list {c.Name} ({items.Count} mods)", "collection", c.Id.ToString());
        var dto = (await DtoAsync(db, c.Id))!;
        var missing = library.Where(m => items.Any(i => i.ModId == m.Id) && m.Status != ModStatus.Installed);
        return Results.Ok(new { collection = dto, missing = await ModLibrary.DtosAsync(db, missing), problems });
    }

    // The players' side: client and optional mods that are enabled, as a Launcher preset.
    private static async Task<IResult> Export(Guid id, AppDb db)
    {
        var dto = await DtoAsync(db, id);
        if (dto is null) return ApiError.Of(404, "not_found", "No such mod list.");
        var html = LauncherPreset.Export(dto.Name, dto.Items.Where(i => i.Enabled && (i.Client || i.Optional)).Select(i => (i.Name, i.WorkshopId, i.LocalFolder)));
        var file = string.Concat(dto.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Results.File(Encoding.UTF8.GetBytes(html), "text/html", file + ".html");
    }
}
