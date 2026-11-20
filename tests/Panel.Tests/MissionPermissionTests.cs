using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;

namespace Panel.Tests;

public class MissionPermissionTests
{
    private static async Task<string[]> PermsOf(RoleManager<Role> roles, string name) =>
        (await roles.GetClaimsAsync((await roles.FindByNameAsync(name))!)).Where(c => c.Type == Permissions.ClaimPerm).Select(c => c.Value).Order().ToArray();

    [Fact]
    public async Task Seeded_roles_get_the_phase_4_permissions()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        string[] missions = ["missions.create", "missions.download", "missions.editAny", "missions.editOwn", "missions.view"];

        Assert.Equal(missions, (await PermsOf(roles, "S1")).Intersect(missions));
        Assert.Equal(["missions.create", "missions.download", "missions.editOwn", "missions.view"], (await PermsOf(roles, "S3")).Intersect(missions));
        foreach (var role in new[] { "S2", "S4", "S6", "S7", "Member" })
            Assert.Equal(["missions.download", "missions.view"], (await PermsOf(roles, role)).Intersect(missions));
    }

    [Fact]
    public async Task Mission_rows_are_stored_and_names_are_unique_ignoring_case()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var mission = new Mission { Id = Guid.NewGuid(), ClassName = "co10_Escape", Map = "Altis", DisplayName = "Escape" };
        var version = new MissionVersion { Id = Guid.NewGuid(), MissionId = mission.Id, Number = 1, PboSha256 = new string('a', 64), PboSize = 10 };
        db.Missions.Add(mission);
        db.MissionVersions.Add(version);
        db.MissionVersionFiles.Add(new MissionVersionFile { VersionId = version.Id, Path = "mission.sqm", Sha256 = new string('b', 64), Size = 3 });
        db.MissionVersionAddons.Add(new MissionVersionAddon { VersionId = version.Id, Addon = "cba_main", ModName = "Community Base Addons" });
        db.MissionWorkingFiles.Add(new MissionWorkingFile { MissionId = mission.Id, Path = "mission.sqm", Sha256 = new string('b', 64), Size = 3 });
        db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = mission.Id, Path = "mission.sqm", Sha256 = new string('b', 64) });
        await db.SaveChangesAsync();

        Assert.Equal("cba_main", (await db.MissionVersionAddons.SingleAsync()).Addon);
        db.Missions.Add(new Mission { Id = Guid.NewGuid(), ClassName = "CO10_ESCAPE", Map = "altis", DisplayName = "Again" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
