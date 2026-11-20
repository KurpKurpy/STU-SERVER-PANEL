using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Features.Operations;

// One checklist item's state for an operation (Ruling 10): pass | fail | unknown | apply ("Apply does this"). Fix is a relative link.
public sealed record CheckResult(Guid ItemId, string Label, string State, string? Detail, string? Fix);

// Phase 6a Ruling 8: the spec §9 checklist, with "Mission approved" replaced by "Mission matches the mod list" (Amendment A).
// A null key is a manual item, ticked per operation.
public static class Checklist
{
    public static readonly IReadOnlyList<(string? Key, string Label)> BuiltIn =
    [
        ("mission.uploaded", "Mission uploaded"),
        ("mission.matches", "Mission matches the mod list"),
        ("mission.deployed", "Mission version on the server"),
        ("profile.selected", "Server profile selected"),
        ("mods.installed", "Mod list assigned and installed"),
        ("mods.updated", "Workshop updates done"),
        ("keys.deployed", "Mod keys on the server"),
        (null, "Mission configuration checked"),
        ("rotation.set", "Mission rotation and mod list on the server"),
        ("server.ready", "Server ready"),
        ("zeus.assigned", "Zeus assigned"),
    ];

    // Names of the planned operations matching, for an "in use" refusal (Ruling 3), or null when none.
    public static async Task<string?> PlannedUsingAsync(AppDb db, Expression<Func<Operation, bool>> uses)
    {
        var names = await db.Operations.Where(o => o.Status == OperationStatus.Planned).Where(uses).OrderBy(o => o.StartsAt).Select(o => o.Name).Take(5).ToListAsync();
        return names.Count == 0 ? null : string.Join(", ", names);
    }
}
