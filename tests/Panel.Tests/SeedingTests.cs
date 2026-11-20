using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;

namespace Panel.Tests;

// The built-in roles are the unit's S-shops (S-shop roles design, 2026-11-20).
public class SeedingTests
{
    private static readonly string[] SystemRoles = ["Owner", "S1", "S2", "S3", "S4", "S6", "S7", "Member"];

    internal static async Task<string[]> PermsOf(RoleManager<Role> roles, string name)
    {
        var role = await roles.FindByNameAsync(name);
        return (await roles.GetClaimsAsync(role!)).Where(c => c.Type == "perm").Select(c => c.Value).OrderBy(v => v).ToArray();
    }

    [Fact]
    public async Task System_roles_exist_with_default_permissions()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();

        Assert.Equal(SystemRoles.Order(), roles.Roles.Where(r => r.IsSystem).Select(r => r.Name!).ToList().Order());
        Assert.Equal(
            [("Owner", "Head developer"), ("S1", "Unit leadership"), ("S2", "Section leaders"), ("S3", "Mission makers and Zeus"), ("S4", "Development"),
             ("S6", "Sheets and bot infrastructure"), ("S7", "Trainers (FTXs)"), ("Member", "Everyone in the unit")],
            SystemRoles.Select(n => (n, roles.Roles.Single(r => r.Name == n).Description)));

        Assert.Equal(Permissions.All.Select(p => p.Key).Where(k => k != "agents.manage").Order(), await PermsOf(roles, "S1"));
        var s3 = await PermsOf(roles, "S3");
        Assert.All(["missions.create", "operations.apply", "players.manage", "logs.view"], p => Assert.Contains(p, s3));
        Assert.DoesNotContain("server.control", s3);
        var s4 = await PermsOf(roles, "S4");
        Assert.All(["server.control", "server.console", "server.update", "files.configs.read", "mods.install", "players.manage"], p => Assert.Contains(p, s4));
        Assert.All(["missions.editAny", "missions.create", "members.manage", "roles.manage", "settings.manage", "audit.view"], p => Assert.DoesNotContain(p, s4));
        Assert.Equal(["members.view", "missions.download", "missions.view", "operations.prepare", "operations.view", "server.view", "server.viewJoinInfo"],
            await PermsOf(roles, "S2"));
        Assert.Equal(["members.view", "missions.download", "missions.view", "operations.view", "server.view", "server.viewJoinInfo"], await PermsOf(roles, "S6"));
        Assert.Equal(["members.view", "missions.download", "missions.start", "missions.view", "mods.view", "operations.manage", "operations.prepare",
            "operations.view", "server.view", "server.viewJoinInfo"], await PermsOf(roles, "S7"));
        Assert.Equal(["missions.download", "missions.view", "operations.view", "server.view", "server.viewJoinInfo"], await PermsOf(roles, "Member"));
        Assert.Empty(await PermsOf(roles, "Owner"));
    }

    // Phase 4b Ruling 2: the spaces holding passwords (configs, profiles, backups) only go to the roles that look after the server (S1, S4).
    [Fact]
    public async Task Seeded_roles_get_the_file_permissions_of_Ruling_2()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        async Task<string[]> Files(string role) => (await PermsOf(roles, role)).Where(p => p.StartsWith("files.")).ToArray();

        Assert.Equal(["files.mpmissions.read", "files.mpmissions.write"], await Files("S3"));
        Assert.Equal(11, (await Files("S1")).Length);
        Assert.Equal(11, (await Files("S4")).Length);
        foreach (var name in new[] { "S2", "S6", "S7", "Member" }) Assert.Empty(await Files(name));
    }

    [Fact]
    public async Task Phase2_permissions_are_seeded()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        string[] server = ["server.control", "server.forceStop", "profiles.manage", "logs.view"];

        Assert.All(server, p => Assert.Contains(p, PermsOf(roles, "S4").Result));
        Assert.Empty((await PermsOf(roles, "Member")).Intersect(server.Append("agents.manage")));
        foreach (var name in SystemRoles) Assert.DoesNotContain("agents.manage", await PermsOf(roles, name));

        var s4 = (await roles.FindByNameAsync("S4"))!;
        await roles.RemoveClaimAsync(s4, new Claim("perm", "server.forceStop"));
        await Seeder.SeedAsync(scope.ServiceProvider);
        Assert.DoesNotContain("server.forceStop", await PermsOf(roles, "S4"));
    }

    [Fact]
    public async Task Seeding_again_does_not_restore_a_removed_permission_or_description()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        var s1 = (await roles.FindByNameAsync("S1"))!;
        await roles.RemoveClaimAsync(s1, new Claim("perm", "announcements.manage"));
        s1.Description = "Command staff";
        await roles.UpdateAsync(s1);

        await Seeder.SeedAsync(scope.ServiceProvider);

        Assert.DoesNotContain("announcements.manage", await PermsOf(roles, "S1"));
        Assert.Contains("audit.view", await PermsOf(roles, "S1"));
        Assert.Equal("Command staff", (await roles.FindByNameAsync("S1"))!.Description);
    }
}
