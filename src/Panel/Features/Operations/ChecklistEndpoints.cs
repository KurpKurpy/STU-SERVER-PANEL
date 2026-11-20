using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Data;

namespace Panel.Features.Operations;

public sealed record ChecklistItemDto(Guid Id, string Label, string? AutoKey, int Order, bool Enabled);
public sealed record ChecklistEdit(Guid? Id, string? Label, int Order, bool Enabled, bool Delete);
public sealed record ChecklistBody(ChecklistEdit[]? Items);

// Phase 6a Ruling 8: the unit's checklist. Admins rename, reorder, disable and add manual items; auto items can be disabled, never deleted.
public static class ChecklistEndpoints
{
    public static async Task<IResult> List(AppDb db) =>
        Results.Ok(await db.ChecklistItems.OrderBy(i => i.Order).ThenBy(i => i.Label)
            .Select(i => new ChecklistItemDto(i.Id, i.Label, i.AutoKey, i.Order, i.Enabled)).ToArrayAsync());

    public static async Task<IResult> Save(ChecklistBody body, AppDb db, AuditLog audit)
    {
        var edits = body.Items ?? [];
        var existing = await db.ChecklistItems.ToDictionaryAsync(i => i.Id);
        foreach (var e in edits)
        {
            var label = (e.Label ?? "").Trim();
            if (!e.Delete && label.Length is 0 or > 120) return ApiError.Field("items", "Each item needs a label of up to 120 characters.");
            if (e.Id is { } id && !existing.ContainsKey(id)) return ApiError.Field("items", "An item no longer exists; reload the list.");
            if (e.Delete && e.Id is { } d && existing[d].AutoKey is not null)
                return ApiError.Field("items", $"\"{existing[d].Label}\" is checked automatically; disable it instead of deleting it.");
        }
        foreach (var e in edits)
        {
            if (e.Id is { } id)
            {
                var item = existing[id];
                if (e.Delete) { db.ChecklistItems.Remove(item); continue; }
                (item.Label, item.Order, item.Enabled) = (e.Label!.Trim(), e.Order, e.Enabled);
            }
            else if (!e.Delete) db.ChecklistItems.Add(new ChecklistItem { Id = Guid.NewGuid(), Label = e.Label!.Trim(), Order = e.Order, Enabled = e.Enabled });
        }
        await db.SaveChangesAsync();
        await audit.RecordAsync("checklist.updated", "Edited the operations checklist", "checklist", null);
        return await List(db);
    }
}
