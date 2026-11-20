using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;

namespace Panel.Tests;

// Phase 6a Task 1: operations storage, the seeded checklist and permissions, and what deleting a used profile or mod list does.
public class OperationStorageTests
{
    internal static async Task<Guid> AddOperationAsync(PanelFactory f, ServerMissionsTests.World w, OperationStatus status = OperationStatus.Planned, string name = "Op Kestrel",
        Guid? list = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var creator = await db.Users.Select(u => u.Id).FirstAsync();
        var op = new Operation
        {
            Id = Guid.NewGuid(), Name = name, StartsAt = DateTime.UtcNow.AddDays(2), ProfileId = w.ProfileId, ModCollectionId = list ?? w.FullList,
            Status = status, CreatedById = creator,
        };
        db.Operations.Add(op);
        await db.SaveChangesAsync();
        return op.Id;
    }

    [Fact]
    public async Task The_checklist_is_seeded_once_and_admin_changes_stay()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var items = await db.ChecklistItems.OrderBy(i => i.Order).ToListAsync();
        Assert.Equal(["mission.uploaded", "mission.matches", "mission.deployed", "profile.selected", "mods.installed", "mods.updated", "keys.deployed",
            null, "rotation.set", "server.ready", "zeus.assigned"], items.Select(i => i.AutoKey));
        Assert.Equal("Mission configuration checked", items[7].Label);

        var deployed = items.Single(i => i.AutoKey == "mission.deployed");
        (deployed.Label, deployed.Enabled, deployed.Order) = ("PBO on the box", false, 99);
        db.ChecklistItems.Remove(items[7]); // a manual item an admin deleted
        await db.SaveChangesAsync();

        await Seeder.SeedAsync(scope.ServiceProvider);

        var again = await db.ChecklistItems.AsNoTracking().ToListAsync();
        Assert.Equal(10, again.Count);
        var kept = again.Single(i => i.AutoKey == "mission.deployed");
        Assert.Equal(("PBO on the box", false, 99), (kept.Label, kept.Enabled, kept.Order));
    }

    [Fact]
    public async Task Operations_permissions_are_seeded_per_Ruling_4()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
        async Task<string[]> Ops(string role) =>
            (await roles.GetClaimsAsync((await roles.FindByNameAsync(role))!)).Where(c => c.Type == "perm" && c.Value.StartsWith("operations."))
            .Select(c => c.Value).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["operations.apply", "operations.manage", "operations.prepare", "operations.view"], await Ops("S1"));
        Assert.Equal(["operations.apply", "operations.manage", "operations.prepare", "operations.view"], await Ops("S3"));
        Assert.Equal(["operations.manage", "operations.prepare", "operations.view"], await Ops("S7"));
        Assert.Equal(["operations.prepare", "operations.view"], await Ops("S2"));
        foreach (var role in new[] { "S4", "S6", "Member" }) Assert.Equal(["operations.view"], await Ops(role));
    }

    [Fact]
    public async Task A_mod_list_or_profile_used_by_a_planned_operation_cant_be_deleted()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await AddOperationAsync(f, w, list: w.CbaOnly); // no profile uses the CBA-only list, so only the operation holds it

        var list = await w.Owner.DeleteAsync($"/api/collections/{w.CbaOnly}");
        var profile = await w.Owner.DeleteAsync($"/api/profiles/{w.ProfileId}");

        foreach (var r in new[] { list, profile })
        {
            Assert.Equal((HttpStatusCode.Conflict, "in_use"), (r.StatusCode, await MissionKit.CodeAsync(r)));
            Assert.Contains("Op Kestrel", await r.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_done_operation_keeps_its_row_when_its_profile_is_deleted()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var id = await AddOperationAsync(f, w, OperationStatus.Done);

        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.DeleteAsync($"/api/profiles/{w.ProfileId}")).StatusCode);

        using var scope = f.Services.CreateScope();
        var op = await scope.ServiceProvider.GetRequiredService<AppDb>().Operations.SingleAsync(o => o.Id == id);
        Assert.Null(op.ProfileId);
        Assert.Equal(w.FullList, op.ModCollectionId);
    }
}
