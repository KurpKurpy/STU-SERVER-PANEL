using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;

namespace Panel.Tests;

public class ModPermissionTests
{
    private static async Task<string[]> PermsOf(RoleManager<Role> roles, string name) =>
        (await roles.GetClaimsAsync((await roles.FindByNameAsync(name))!)).Where(c => c.Type == Permissions.ClaimPerm).Select(c => c.Value).Order().ToArray();

    [Fact]
    public async Task Seeded_roles_get_the_phase_5_permissions()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        string[] mods = ["collections.manage", "keys.manage", "mods.install", "mods.manage", "mods.view", "server.update"];

        Assert.Equal(mods, (await PermsOf(roles, "S1")).Intersect(mods));
        Assert.Equal(mods, (await PermsOf(roles, "S4")).Intersect(mods));
        Assert.Equal(["mods.view"], (await PermsOf(roles, "S3")).Intersect(mods));
        Assert.Equal(["mods.view"], (await PermsOf(roles, "S7")).Intersect(mods));
        foreach (var role in new[] { "S2", "S6", "Member" }) Assert.Empty((await PermsOf(roles, role)).Intersect(mods));
    }

    [Fact]
    public async Task Mod_rows_and_a_profile_mod_list_are_stored()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var mod = new Mod { Id = Guid.NewGuid(), WorkshopId = 450814997, Name = "CBA_A3", Status = ModStatus.NotInstalled };
        var list = new ModCollection { Id = Guid.NewGuid(), Name = "Main" };
        db.Mods.Add(mod);
        db.ModCollections.Add(list);
        db.ModCollectionItems.Add(new ModCollectionItem { CollectionId = list.Id, ModId = mod.Id, Order = 0, Client = true, Enabled = true });
        db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = "workshop.download", Status = JobStatus.Queued });
        await db.SaveChangesAsync();

        Assert.Equal("CBA_A3", (await db.ModCollectionItems.Include(i => i.Mod).SingleAsync()).Mod!.Name);
        Assert.Equal(JobStatus.Queued, (await db.Jobs.SingleAsync()).Status);
        // A Workshop item is in the library once.
        db.Mods.Add(new Mod { Id = Guid.NewGuid(), WorkshopId = 450814997, Name = "again" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
