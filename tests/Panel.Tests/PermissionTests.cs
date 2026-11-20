using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Panel.Tests;

public class PermissionTests
{
    private static object NewRole() => new { name = "Role " + Guid.NewGuid().ToString("N")[..6], description = (string?)null, permissions = new[] { "members.view" } };

    [Theory]
    [InlineData("Member", 403, 403)]
    [InlineData("S1", 200, 200)]
    [InlineData("S2", 200, 403)]
    [InlineData("S3", 200, 403)]
    [InlineData("S4", 200, 403)]
    [InlineData("S6", 200, 403)]
    [InlineData("S7", 200, 403)]
    [InlineData("Owner", 200, 200)]
    public async Task Seeded_roles_get_expected_access(string role, int getRoles, int postRoles)
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, role);
        Assert.Equal(getRoles, (int)(await c.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(postRoles, (int)(await c.PostAsJsonAsync("/api/roles", NewRole())).StatusCode);
    }

    [Fact]
    public async Task Grant_override_adds_permission()
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, "Member", "granted");
        await TestUsers.AddClaimAsync(f, "granted", "perm.grant", "roles.manage");
        var role = new { name = "Role " + Guid.NewGuid().ToString("N")[..6], description = (string?)null, permissions = new[] { "server.view" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/roles", role)).StatusCode);
    }

    [Fact]
    public async Task Deny_override_removes_permission()
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, "S3", "denied");
        await TestUsers.AddClaimAsync(f, "denied", "perm.deny", "members.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/roles")).StatusCode);
    }

    [Fact]
    public async Task Me_lists_effective_permissions()
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, "S3");
        var me = await c.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(["files.mpmissions.read", "files.mpmissions.write", "logs.view", "members.view", "missions.create", "missions.download", "missions.editOwn",
                "missions.start", "missions.view", "mods.view", "operations.apply", "operations.manage", "operations.prepare", "operations.view", "players.manage",
                "profiles.view", "server.view", "server.viewJoinInfo"],
            me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Owner_role_permissions_cannot_be_edited()
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, "Owner");
        var roles = await c.GetFromJsonAsync<JsonElement>("/api/roles");
        var ownerId = roles.EnumerateArray().First(r => r.GetProperty("isOwner").GetBoolean()).GetProperty("id").GetString();
        var r = await c.PutAsJsonAsync($"/api/roles/{ownerId}", new { name = "Owner", description = "x", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("owner_role_fixed", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
