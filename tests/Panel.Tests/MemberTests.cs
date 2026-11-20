using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Panel.Tests;

public class MemberTests
{
    private static async Task<string> IdOf(HttpClient c, string userName) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/members")).EnumerateArray()
            .First(m => m.GetProperty("userName").GetString() == userName).GetProperty("id").GetString()!;

    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Disabled_member_is_signed_out_on_next_request()
    {
        using var f = new PanelFactory(stampSeconds: 0);
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var member = await TestUsers.LoginAsync(f, "Member", "Nomad");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/me")).StatusCode);

        var r = await owner.PatchAsJsonAsync($"/api/members/{await IdOf(owner, "Nomad")}", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/me")).StatusCode);
        Assert.Equal("disabled", await AuthTests.Login(f.CreateClient(), "Nomad"));
    }

    [Fact]
    public async Task Last_owner_cannot_be_disabled_or_demoted()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var id = await IdOf(owner, "Crow");
        var disable = await owner.PatchAsJsonAsync($"/api/members/{id}", new { disabled = true });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Equal("last_owner", await Code(disable));
        var demote = await owner.PatchAsJsonAsync($"/api/members/{id}", new { roles = new[] { "Member" } });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_owner", await Code(demote));
    }

    [Fact]
    public async Task Non_owner_cannot_grant_beyond_own_permissions()
    {
        using var f = new PanelFactory();
        await TestUsers.CreateAsync(f, "S4", "Admin");
        await TestUsers.AddClaimAsync(f, "Admin", "perm.grant", "members.manage");
        var admin = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(admin, "Admin"));
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        var nomad = await IdOf(admin, "Nomad");
        var self = await IdOf(admin, "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PatchAsJsonAsync($"/api/members/{self}", new { grants = new[] { "settings.manage" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PatchAsJsonAsync($"/api/members/{nomad}", new { roles = new[] { "Owner" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PatchAsJsonAsync($"/api/members/{nomad}", new { roles = new[] { "S3" } })).StatusCode); // S3 holds what an S4 manager doesn't
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/members/{nomad}", new { roles = new[] { "S6" }, rank = "PVT" })).StatusCode); // a role inside their own access
    }

    [Fact]
    public async Task Reset_link_sets_new_password()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        await TestUsers.CreateAsync(f, "Member", "Nomad");
        var link = await (await owner.PostAsync($"/api/members/{await IdOf(owner, "Nomad")}/reset-link", null)).Content.ReadFromJsonAsync<JsonElement>();
        var query = QueryHelpers.ParseQuery(new Uri(link.GetProperty("url").GetString()!).Query);

        var r = await f.CreateClient().PostAsJsonAsync("/api/auth/reset",
            new { userId = query["u"].ToString(), token = query["t"].ToString(), newPassword = "a brand new passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("ok", await AuthTests.Login(f.CreateClient(), "Nomad", "a brand new passphrase"));
    }
}
