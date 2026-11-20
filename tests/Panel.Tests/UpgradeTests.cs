using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles;

namespace Panel.Tests;

public class UpgradeTests
{
    // A profile as Phase 2 stored it: raw server.cfg encrypted under "profile.serverCfg", BasicCfg and CustomArgs as text.
    private static async Task<Guid> Phase2ProfileAsync(PanelFactory f, Func<IDataProtectionProvider, string> protectedCfg, string? basicCfg = "MaxCustomFileSize = 0;\r\n")
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var agent = new Agent { Id = Guid.NewGuid(), Name = "Box", KeyHash = new string('0', 64) };
        var install = new ServerInstall { Id = Guid.NewGuid(), AgentId = agent.Id, Name = "Arma", InstallPath = @"C:\arma" };
        var profile = new Profile
        {
            Id = Guid.NewGuid(), ServerInstallId = install.Id, Name = "Main", Slug = "main", Port = 2302,
            ServerCfgProtected = protectedCfg(dp), BasicCfg = basicCfg, CustomArgs = "-autoInit -maxMem=8192",
        };
        db.AddRange(agent, install, profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static async Task UpgradeAsync(PanelFactory f)
    {
        using var scope = f.Services.CreateScope();
        await ProfileUpgrade.RunAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), scope.ServiceProvider.GetRequiredService<ProfileStore>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), NullLogger.Instance);
    }

    private static AppDb Db(PanelFactory f) => f.Services.CreateScope().ServiceProvider.GetRequiredService<AppDb>();

    [Fact]
    public async Task Phase2_profile_is_upgraded_to_revision_1()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        var id = await Phase2ProfileAsync(f, dp => ProfileEndpoints.CfgProtector(dp).Protect(
            "hostname = \"Main Ops\";\r\npasswordAdmin = \"hunter2-admin\";\r\nfake_unknownKey = 1;\r\n"));

        await UpgradeAsync(f);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var store = scope.ServiceProvider.GetRequiredService<ProfileStore>();
        var p = await db.Profiles.SingleAsync(x => x.Id == id);
        var rev = await db.ProfileRevisions.SingleAsync(r => r.ProfileId == id);
        Assert.Equal(1, rev.Number);
        Assert.Equal("Imported from Phase 2", rev.Note);
        var (snapshot, secrets) = store.Load(rev);
        Assert.Equal("Main Ops", snapshot.Settings.Server["hostname"].GetString());
        Assert.Equal("hunter2-admin", secrets!.PasswordAdmin);
        Assert.Contains("fake_unknownKey = 1;", snapshot.ExtraServerCfg);
        Assert.Contains("MaxCustomFileSize", snapshot.Settings.Basic.Keys);
        Assert.True(snapshot.Settings.Launch["-autoInit"].GetBoolean());
        Assert.Equal(8192, snapshot.Settings.Launch["-maxMem"].GetInt64());
        Assert.DoesNotContain("hunter2", rev.SnapshotJson);
        Assert.DoesNotContain("hunter2", p.SettingsJson);
        Assert.Equal("", p.ServerCfgProtected);
        Assert.Null(p.BasicCfg);
        Assert.False(p.NeedsCfg);
    }

    [Fact]
    public async Task Unreadable_phase2_cfg_is_kept_and_flagged()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        string? stored = null;
        var id = await Phase2ProfileAsync(f, dp => stored = dp.CreateProtector("some.other.purpose").Protect("hostname = \"x\";"));

        await UpgradeAsync(f);

        var db = Db(f);
        var p = await db.Profiles.SingleAsync(x => x.Id == id);
        Assert.True(p.NeedsCfg);
        Assert.Equal(stored, p.ServerCfgProtected);
        Assert.Null(p.SettingsJson);
        Assert.False(await db.ProfileRevisions.AnyAsync(r => r.ProfileId == id));
    }

    [Fact]
    public async Task Unparseable_phase2_cfg_is_kept_and_flagged()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        string? stored = null;
        var id = await Phase2ProfileAsync(f, dp => stored = ProfileEndpoints.CfgProtector(dp).Protect("hostname = \"x"));

        await UpgradeAsync(f);

        var db = Db(f);
        var p = await db.Profiles.SingleAsync(x => x.Id == id);
        Assert.True(p.NeedsCfg);
        Assert.Equal(stored, p.ServerCfgProtected);
        Assert.False(await db.ProfileRevisions.AnyAsync(r => r.ProfileId == id));
    }

    [Fact]
    public async Task Upgrade_runs_once()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        var id = await Phase2ProfileAsync(f, dp => ProfileEndpoints.CfgProtector(dp).Protect("hostname = \"x\";"), basicCfg: null);

        await UpgradeAsync(f);
        await UpgradeAsync(f);

        Assert.Equal(1, await Db(f).ProfileRevisions.CountAsync(r => r.ProfileId == id));
    }

    private static async Task<string[]> PermsOf(RoleManager<Role> roles, string name) =>
        (await roles.GetClaimsAsync((await roles.FindByNameAsync(name))!)).Where(c => c.Type == "perm").Select(c => c.Value).OrderBy(v => v).ToArray();

    [Fact]
    public async Task Phase3_permissions_are_seeded()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();

        var admin = await PermsOf(roles, "S4");
        Assert.Contains("profiles.view", admin);
        Assert.Contains("profiles.apply", admin);
        Assert.Contains("server.viewJoinInfo", admin);
        Assert.Contains("profiles.view", await PermsOf(roles, "S3"));
        Assert.DoesNotContain("profiles.apply", await PermsOf(roles, "S3"));
        foreach (var name in new[] { "S4", "S3", "S3", "Member" })
            Assert.Contains("server.viewJoinInfo", await PermsOf(roles, name));
        Assert.DoesNotContain("profiles.view", await PermsOf(roles, "Member"));

        var role = (await roles.FindByNameAsync("Member"))!;
        await roles.RemoveClaimAsync(role, new Claim("perm", "server.viewJoinInfo"));
        await Seeder.SeedAsync(scope.ServiceProvider);
        Assert.DoesNotContain("server.viewJoinInfo", await PermsOf(roles, "Member"));
    }

    [Fact]
    public async Task Builtin_difficulty_presets_are_seeded()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        var presets = await Db(f).DifficultyPresets.ToListAsync();

        Assert.Equal(["Recruit", "Regular", "Veteran"], presets.Where(p => p.IsBuiltIn).Select(p => p.Name).Order());
        JsonElement Values(string name) => JsonDocument.Parse(presets.Single(p => p.Name == name).ValuesJson).RootElement;
        Assert.Equal(0, Values("Veteran").GetProperty("thirdPersonView").GetInt32());
        Assert.Equal(1, Values("Recruit").GetProperty("reducedDamage").GetInt32());
        Assert.Equal(1, Values("Regular").GetProperty("CustomDifficulty.aiLevelPreset").GetInt32());
    }
}
