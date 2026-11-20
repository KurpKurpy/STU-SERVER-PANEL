using Contracts;
using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Features.Mods;

// A mod list turned into what the agent launches (Phase 5a Ruling 6): enabled Client mods → -mod, ServerOnly → -serverMod,
// Headless → the headless clients' -mod. Keys cover every enabled, installed mod, optional ones too, so players using them can join.
public sealed record ResolvedMods(string ListName, ModRef[] Client, ModRef[] Server, ModRef[] Headless, ModRef[] Keys, string[] NotInstalled);

public static class ModLaunch
{
    // A revision's mod list, read with the options the snapshot was written with (camelCase).
    public static Guid? ListOf(string snapshotJson) =>
        System.Text.Json.JsonSerializer.Deserialize<Panel.Features.Profiles.ProfileSnapshot>(snapshotJson, Panel.Features.Profiles.ProfileStore.Json)?.ModCollectionId;

    public static ModRef Ref(Mod m) => new(m.WorkshopId, m.LocalFolder);

    public static async Task<ResolvedMods?> ResolveAsync(AppDb db, Guid listId)
    {
        var list = await db.ModCollections.FindAsync(listId);
        if (list is null) return null;
        var items = await db.ModCollectionItems.Include(i => i.Mod).Where(i => i.CollectionId == listId && i.Enabled).OrderBy(i => i.Order).ToListAsync();
        return new ResolvedMods(list.Name,
            items.Where(i => i.Client).Select(i => Ref(i.Mod!)).ToArray(),
            items.Where(i => i.ServerOnly).Select(i => Ref(i.Mod!)).ToArray(),
            items.Where(i => i.Headless).Select(i => Ref(i.Mod!)).ToArray(),
            items.Where(i => i.Mod!.Status == ModStatus.Installed).Select(i => Ref(i.Mod!)).ToArray(),
            items.Where(i => !i.Optional && i.Mod!.Status != ModStatus.Installed).Select(i => i.Mod!.Name).ToArray());
    }

    // The folder the agent uses for a mod, from the areas its hello reported (the same paths ModStore.Folder builds).
    public static Func<ModRef, string> Paths(AgentHello? hello)
    {
        string Root(string area, string fallback) => hello?.Areas.FirstOrDefault(a => a.Area == area)?.Path ?? $@"<agent data folder>\{fallback}";
        var workshop = Root("workshop", @"workshop\steamapps\workshop\content\107410");
        var local = Root("localmods", "localmods");
        return r => r.WorkshopId is { } id ? Path.Combine(workshop, id.ToString()) : Path.Combine(local, r.LocalFolder!);
    }
}
