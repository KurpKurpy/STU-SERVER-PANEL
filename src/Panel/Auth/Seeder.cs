using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Panel.Data;

namespace Panel.Auth;

public static class Seeder
{
    private const string SeededKey = "seededPermissions";
    private const string SeededChecklistKey = "seededChecklist";
    private const string SeededRulesKey = "seededNotificationRules";

    // Grants each system-role default once; "Role:perm" pairs already granted are never re-added,
    // so a permission an admin removes stays removed when later phases seed new ones.
    public static async Task SeedAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<Role>>();
        var db = services.GetRequiredService<AppDb>();
        var row = await db.Settings.FindAsync(SeededKey);
        var seeded = (row?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        foreach (var (name, perms) in Permissions.SystemRoleDefaults)
        {
            var role = await roles.FindByNameAsync(name);
            var description = Permissions.SystemRoleDescriptions[name];
            if (role is null)
            {
                role = new Role(name) { IsSystem = true, Description = description };
                await roles.CreateAsync(role);
            }
            else if (!role.IsSystem || string.IsNullOrWhiteSpace(role.Description))
            {
                // A custom role that already has a built-in name becomes that built-in role (S-shop roles design); an admin's own
                // description is never overwritten.
                role.IsSystem = true;
                if (string.IsNullOrWhiteSpace(role.Description)) role.Description = description;
                await roles.UpdateAsync(role);
            }

            var existing = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimPerm).Select(c => c.Value).ToHashSet();
            foreach (var perm in perms)
            {
                if (!seeded.Add($"{name}:{perm}")) continue;
                if (!existing.Contains(perm)) await roles.AddClaimAsync(role, new Claim(Permissions.ClaimPerm, perm));
            }
        }

        if (row is null) db.Settings.Add(row = new AppSetting { Key = SeededKey });
        row.Value = string.Join(',', seeded.Order());
        await db.SaveChangesAsync();

        await RoleSwitch.RunAsync(services);

        // Phase 6a Ruling 8: the built-in checklist items are added once each (tracked like permissions), so an admin's renames,
        // reorders, disables and deletes stay.
        var checklistRow = await db.Settings.FindAsync(SeededChecklistKey);
        var seededItems = (checklistRow?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var order = 0;
        foreach (var (key, label) in Features.Operations.Checklist.BuiltIn)
        {
            order++;
            if (!seededItems.Add(key ?? "manual.config")) continue;
            db.ChecklistItems.Add(new ChecklistItem { Id = Guid.NewGuid(), AutoKey = key, Label = label, Order = order, Enabled = true });
        }
        if (checklistRow is null) db.Settings.Add(checklistRow = new AppSetting { Key = SeededChecklistKey });
        checklistRow.Value = string.Join(',', seededItems.Order());

        // Phase 6b Ruling 2: each event's rule is added once, so an admin's choices (and deletions) stay.
        var rulesRow = await db.Settings.FindAsync(SeededRulesKey);
        var seededRules = (rulesRow?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var (type, _, inApp, discord) in Features.Notifications.NotificationEvents.All)
            if (seededRules.Add(type) && await db.NotificationRules.FindAsync(type) is null)
                db.NotificationRules.Add(new NotificationRule { EventType = type, InApp = inApp, Discord = discord });
        if (rulesRow is null) db.Settings.Add(rulesRow = new AppSetting { Key = SeededRulesKey });
        rulesRow.Value = string.Join(',', seededRules.Order());

        // Built-in difficulty presets are read-only, so they're (re)added by name only when missing.
        var presets = await db.DifficultyPresets.Select(p => p.Name).ToListAsync();
        foreach (var (name, values) in Features.Profiles.Config.ConfigCatalog.BuiltInPresets)
            if (!presets.Contains(name))
                db.DifficultyPresets.Add(new DifficultyPreset { Id = Guid.NewGuid(), Name = name, IsBuiltIn = true, ValuesJson = System.Text.Json.JsonSerializer.Serialize(values) });
        await db.SaveChangesAsync();
    }
}
