using Microsoft.AspNetCore.Identity;
using Panel.Audit;
using Panel.Data;
using Panel.Settings;

namespace Panel.Auth;

// S-shop roles design (2026-11-20): a database from before the S-shops still has the old built-in roles. Once the new ones exist, their
// members move across, the stored 2FA list follows, one audit entry records who moved where, and the old roles are deleted. It needs no
// "done" flag: it only acts while an old built-in role exists, and a custom role that happens to share an old name is never touched.
public static class RoleSwitch
{
    public static readonly IReadOnlyDictionary<string, string> Moves = new Dictionary<string, string>
    {
        ["Server Administrator"] = "S1", ["Mod Manager"] = "S4", ["Mission Maker"] = "S3", ["Zeus"] = "S3",
    };

    public static async Task RunAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<Role>>();
        var old = new List<Role>();
        foreach (var name in Moves.Keys)
            if (await roles.FindByNameAsync(name) is { IsSystem: true } role) old.Add(role);
        if (old.Count == 0) return;

        var users = services.GetRequiredService<UserManager<User>>();
        var moved = new List<object>();
        foreach (var role in old)
        {
            var to = Moves[role.Name!];
            foreach (var user in await users.GetUsersInRoleAsync(role.Name!))
            {
                if (!await users.IsInRoleAsync(user, to))
                {
                    var added = await users.AddToRoleAsync(user, to);
                    if (!added.Succeeded) throw new InvalidOperationException($"Moving {user.UserName} to {to} failed: {string.Join(" ", added.Errors.Select(e => e.Description))}");
                }
                moved.Add(new { user = user.UserName, from = role.Name, to });
            }
        }

        // The list in effect (saved, else the config file, else the default) is translated and saved, so an older config file on the
        // box can't keep naming roles that no longer exist (final review C-2).
        var settings = services.GetRequiredService<AppSettingsService>();
        await settings.SetAsync(AppSettingsService.TwoFactorRolesKey, TranslateTwoFactor(string.Join(',', await settings.TwoFactorRequiredRolesAsync())));

        await services.GetRequiredService<AuditLog>().RecordAsync("roles.switched",
            $"Switched the built-in roles to S-shops; moved {moved.Count} role membership(s)", "role", null, moved);

        foreach (var role in old)
        {
            var deleted = await roles.DeleteAsync(role);
            if (!deleted.Succeeded) throw new InvalidOperationException($"Deleting the old role {role.Name} failed.");
        }
    }

    // Old names become their new ones, S4 joins wherever S1 is (it now looks after the server too), duplicates go; empty stays empty.
    public static string TranslateTwoFactor(string stored)
    {
        var names = new List<string>();
        foreach (var raw in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = Moves.TryGetValue(raw, out var to) ? to : raw;
            if (!names.Contains(name)) names.Add(name);
            if (name == "S1" && !names.Contains("S4")) names.Add("S4");
        }
        return string.Join(',', names);
    }
}
