using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;
using Panel.Settings;

namespace Panel.Tests;

// S-shop roles design: a database from before the S-shops switches once at startup: members move, the 2FA list follows, the old
// built-in roles go, and one audit entry says who moved where (plan Review Focus 1–5).
public class RoleSwitchTests
{
    private static async Task PlantAsync(PanelFactory f, string name, bool system = true)
    {
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        Assert.True((await roles.CreateAsync(new Role(name) { IsSystem = system })).Succeeded);
    }

    private static async Task AddToAsync(PanelFactory f, string user, string role)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByNameAsync(user))!, role)).Succeeded);
    }

    private static async Task SeedAsync(PanelFactory f)
    {
        using var scope = f.Services.CreateScope();
        await Seeder.SeedAsync(scope.ServiceProvider);
    }

    private static async Task<string[]> RolesOfAsync(PanelFactory f, string user)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return [.. (await users.GetRolesAsync((await users.FindByNameAsync(user))!)).Order()];
    }

    [Fact]
    public async Task Old_built_in_roles_are_switched_once()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        foreach (var old in new[] { "Server Administrator", "Mod Manager", "Mission Maker", "Zeus" }) await PlantAsync(f, old);
        await TestUsers.CreateAsync(f, "Server Administrator", "Alpha");
        await AddToAsync(f, "Alpha", "Owner");
        await TestUsers.CreateAsync(f, "Mod Manager", "Bravo");
        await TestUsers.CreateAsync(f, "Mission Maker", "Charlie");
        await AddToAsync(f, "Charlie", "Zeus");
        await TestUsers.AddClaimAsync(f, "Charlie", Permissions.ClaimDeny, Permissions.PlayersManage);

        await SeedAsync(f);
        await SeedAsync(f); // a second startup changes nothing

        Assert.Equal(["Owner", "S1"], await RolesOfAsync(f, "Alpha"));
        Assert.Equal(["S4"], await RolesOfAsync(f, "Bravo"));
        Assert.Equal(["S3"], await RolesOfAsync(f, "Charlie"));
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        foreach (var old in new[] { "Server Administrator", "Mod Manager", "Mission Maker", "Zeus" }) Assert.Null(await roles.FindByNameAsync(old));
        var charlie = await TestUsers.IdAsync(f, "Charlie");
        var effective = await scope.ServiceProvider.GetRequiredService<PermissionService>().EffectiveAsync(charlie);
        Assert.Contains(Permissions.MissionsCreate, effective);
        Assert.DoesNotContain(Permissions.PlayersManage, effective); // the member's own deny still applies

        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var entry = Assert.Single(await db.Audit.Where(a => a.Action == "roles.switched").ToListAsync());
        Assert.Contains("Alpha", entry.DetailsJson);
        Assert.Contains("Bravo", entry.DetailsJson);
        Assert.Contains("Charlie", entry.DetailsJson);
    }

    [Fact]
    public void Two_factor_list_is_translated()
    {
        Assert.Equal("Owner,S1,S4", RoleSwitch.TranslateTwoFactor("Owner,Server Administrator"));
        Assert.Equal("Owner,S3", RoleSwitch.TranslateTwoFactor("Owner,Zeus"));
        Assert.Equal("S3", RoleSwitch.TranslateTwoFactor("Mission Maker,Zeus"));
        Assert.Equal("S1,S4,Pilots", RoleSwitch.TranslateTwoFactor("Server Administrator, Mod Manager, Pilots"));
        Assert.Equal("", RoleSwitch.TranslateTwoFactor(""));
    }

    [Fact]
    public async Task A_stored_two_factor_list_follows_the_switch_and_new_installs_default_to_S1_and_S4()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var bare = new AppSettingsService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()); // no stored list, no config
            Assert.Equal(["Owner", "S1", "S4"], await bare.TwoFactorRequiredRolesAsync());
            await bare.SetAsync(AppSettingsService.TwoFactorRolesKey, "Owner,Server Administrator");
        }
        await PlantAsync(f, "Server Administrator");

        await SeedAsync(f);

        using var after = f.Services.CreateScope();
        Assert.Equal(["Owner", "S1", "S4"], await after.ServiceProvider.GetRequiredService<AppSettingsService>().TwoFactorRequiredRolesAsync());
    }

    // Final review C-2: real installs always load appsettings.json, so its list (not the code's default) decides who needs 2FA.
    [Fact]
    public void The_shipped_settings_require_two_factor_for_Owner_S1_and_S4()
    {
        static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
        var file = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "Panel", "appsettings.json");
        var list = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("Auth").GetProperty("TwoFactorRequiredRoles").GetString()!;
        Assert.Equal("Owner,S1,S4", list);
        Assert.All(list.Split(','), name => Assert.Contains(name, Permissions.SystemRoleDefaults.Keys));
    }

    // Final review C-2: a console that never saved its Settings page reads the list from its config file, which may predate the S-shops.
    [Fact]
    public async Task An_unsaved_two_factor_list_from_config_is_translated_and_saved_by_the_switch()
    {
        using var f = new PanelFactory(twoFactorRequiredRoles: "Owner,Server Administrator");
        f.CreateClient();
        await PlantAsync(f, "Server Administrator");

        await SeedAsync(f);

        using var scope = f.Services.CreateScope();
        Assert.Equal(["Owner", "S1", "S4"], await scope.ServiceProvider.GetRequiredService<AppSettingsService>().TwoFactorRequiredRolesAsync());
    }

    [Fact]
    public async Task A_custom_role_named_S3_is_reused()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using (var scope = f.Services.CreateScope())
        {
            // An older database never had S3: no role, and no record of having seeded its permissions.
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
            Assert.True((await roles.DeleteAsync((await roles.FindByNameAsync("S3"))!)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var seeded = (await db.Settings.FindAsync("seededPermissions"))!;
            seeded.Value = string.Join(',', seeded.Value.Split(',').Where(e => !e.StartsWith("S3:")));
            await db.SaveChangesAsync();
        }
        await PlantAsync(f, "S3", system: false);
        await TestUsers.CreateAsync(f, "S3", "Delta");
        await PlantAsync(f, "Zeus");
        await TestUsers.CreateAsync(f, "Zeus", "Echo");

        await SeedAsync(f);

        using var after = f.Services.CreateScope();
        var all = after.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        var s3 = Assert.Single(all.Roles.Where(r => r.Name == "S3").ToList());
        Assert.True(s3.IsSystem);
        Assert.Equal("Mission makers and Zeus", s3.Description);
        Assert.Equal(["S3"], await RolesOfAsync(f, "Delta"));
        Assert.Equal(["S3"], await RolesOfAsync(f, "Echo"));
        Assert.Contains(Permissions.MissionsCreate, await SeedingTests.PermsOf(all, "S3"));
    }

    [Fact]
    public async Task A_custom_role_called_Zeus_is_left_alone()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        await PlantAsync(f, "Zeus", system: false);
        await TestUsers.CreateAsync(f, "Zeus", "Foxtrot");

        await SeedAsync(f);

        Assert.Equal(["Zeus"], await RolesOfAsync(f, "Foxtrot"));
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Where(a => a.Action == "roles.switched").ToListAsync());
    }
}
