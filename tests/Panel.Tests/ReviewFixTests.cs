using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Panel.Tests;

// Findings from the final whole-build review, each pinned by a test that failed before its fix.
public class ReviewFixTests
{
    [Fact]
    public async Task Disabled_member_cannot_keep_session_by_changing_password()
    {
        using var f = new PanelFactory(stampSeconds: 600);
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var member = await TestUsers.LoginAsync(f, "Member", "Nomad");
        var id = await TestUsers.IdAsync(f, "Nomad");
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/members/{id}", new { disabled = true })).StatusCode);

        var change = await member.PostAsJsonAsync("/api/account/password", new { current = AuthTests.Password, @new = "another long passphrase" });
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_mint_reset_link_for_higher_access_member()
    {
        using var f = new PanelFactory();
        await TestUsers.CreateAsync(f, "Owner", "Crow");
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        var admin = await TestUsers.ManagerAsync(f);

        var forOwner = await admin.PostAsync($"/api/members/{await TestUsers.IdAsync(f, "Crow")}/reset-link", null);
        Assert.Equal(HttpStatusCode.Forbidden, forOwner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/members/{await TestUsers.IdAsync(f, "Nomad")}/reset-link", null)).StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_lift_a_deny_on_themselves()
    {
        using var f = new PanelFactory();
        var admin = await TestUsers.ManagerAsync(f);
        await TestUsers.AddClaimAsync(f, "Admin", "perm.deny", "logs.view"); // one their role grants
        var r = await admin.PatchAsJsonAsync($"/api/members/{await TestUsers.IdAsync(f, "Admin")}", new { denies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Non_owner_can_edit_member_holding_access_they_lack_when_not_changing_it()
    {
        using var f = new PanelFactory();
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        await TestUsers.AddClaimAsync(f, "Nomad", "perm.grant", "settings.manage");
        var admin = await TestUsers.ManagerAsync(f);
        var r = await admin.PatchAsJsonAsync($"/api/members/{await TestUsers.IdAsync(f, "Nomad")}",
            new { rank = "PVT", roles = new[] { "Member" }, grants = new[] { "settings.manage" }, denies = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_add_permissions_they_lack_to_a_role()
    {
        using var f = new PanelFactory();
        var admin = await TestUsers.ManagerAsync(f, "roles.manage");
        var zeus = (await admin.GetFromJsonAsync<JsonElement>("/api/roles")).EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "S3").GetProperty("id").GetString();

        var widen = await admin.PutAsJsonAsync($"/api/roles/{zeus}", new { name = "S3", description = (string?)null, permissions = new[] { "members.view", "server.view", "settings.manage" } });
        Assert.Equal(HttpStatusCode.Forbidden, widen.StatusCode);
        var create = await admin.PostAsJsonAsync("/api/roles", new { name = "Sneaky", description = (string?)null, permissions = new[] { "settings.manage" } });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        var narrow = await admin.PutAsJsonAsync($"/api/roles/{zeus}", new { name = "S3", description = (string?)null, permissions = new[] { "members.view" } });
        Assert.Equal(HttpStatusCode.OK, narrow.StatusCode);
    }

    [Fact]
    public async Task Invite_for_deleted_role_is_not_valid()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        await owner.PostAsJsonAsync("/api/roles", new { name = "Pilots", description = (string?)null, permissions = Array.Empty<string>() });
        var (token, _) = await InviteTests.CreateInvite(owner, "Pilots");
        var pilots = (await owner.GetFromJsonAsync<JsonElement>("/api/roles")).EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "Pilots").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/roles/{pilots}")).StatusCode);

        var info = await f.CreateClient().GetFromJsonAsync<JsonElement>($"/api/invites/{token}");
        Assert.False(info.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Shared_key_is_not_returned_after_enrolment()
    {
        using var f = new PanelFactory();
        var c = await TestUsers.LoginAsync(f, "S3");
        await TwoFactorTests.Enrol(c);
        Assert.Equal(HttpStatusCode.Conflict, (await c.GetAsync("/api/account/2fa")).StatusCode);
    }

    [Fact]
    public async Task Unknown_callsign_is_not_written_to_the_audit_log()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        await f.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = "hunter2 my secret words", password = "x" });
        var audit = await owner.GetFromJsonAsync<JsonElement>("/api/audit");
        Assert.DoesNotContain(audit.EnumerateArray(), e => e.GetProperty("summary").GetString()!.Contains("hunter2"));
    }
}
