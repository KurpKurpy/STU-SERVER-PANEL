using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class InviteTests
{
    internal static async Task<(string Token, Guid Id)> CreateInvite(HttpClient owner, string roleName = "S3")
    {
        var roles = await owner.GetFromJsonAsync<JsonElement>("/api/roles");
        var roleId = roles.EnumerateArray().First(r => r.GetProperty("name").GetString() == roleName).GetProperty("id").GetString();
        var r = await owner.PostAsJsonAsync("/api/invites", new { roleId, expiresHours = (int?)null });
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("url").GetString()!.Split('/').Last(), Guid.Parse(body.GetProperty("id").GetString()!));
    }

    private static Task<HttpResponseMessage> Accept(HttpClient c, string token, string userName) =>
        c.PostAsJsonAsync($"/api/invites/{token}/accept", new { userName, displayName = userName.Trim(), password = AuthTests.Password });

    private static async Task<string?> Reason(HttpClient c, string token) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/invites/{token}")).GetProperty("reason").GetString();

    [Fact]
    public async Task Invite_accept_creates_member_with_role()
    {
        using var f = new PanelFactory();
        var (token, _) = await CreateInvite(await TestUsers.LoginAsync(f, "Owner", "Crow"));
        var guest = f.CreateClient();
        var info = await guest.GetFromJsonAsync<JsonElement>($"/api/invites/{token}");
        Assert.True(info.GetProperty("valid").GetBoolean());
        Assert.Equal("S3", info.GetProperty("roleName").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Accept(guest, token, "Hawk")).StatusCode);
        var me = await guest.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("Hawk", me.GetProperty("userName").GetString());
        Assert.Contains("S3", me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        var hawk = (await guest.GetFromJsonAsync<JsonElement>("/api/members")).EnumerateArray().First(m => m.GetProperty("userName").GetString() == "Hawk");
        Assert.Equal(JsonValueKind.String, hawk.GetProperty("lastLoginAt").ValueKind);
    }

    [Fact]
    public async Task Invite_cannot_be_used_twice()
    {
        using var f = new PanelFactory();
        var (token, _) = await CreateInvite(await TestUsers.LoginAsync(f, "Owner", "Crow"));
        await Accept(f.CreateClient(), token, "Hawk");
        var second = await Accept(f.CreateClient(), token, "Viper");
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
        Assert.Equal("used", await Reason(f.CreateClient(), token));
    }

    [Fact]
    public async Task Expired_invite_reports_reason()
    {
        using var f = new PanelFactory();
        var (token, id) = await CreateInvite(await TestUsers.LoginAsync(f, "Owner", "Crow"));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Invites.FindAsync(id))!.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var info = await f.CreateClient().GetFromJsonAsync<JsonElement>($"/api/invites/{token}");
        Assert.False(info.GetProperty("valid").GetBoolean());
        Assert.Equal("expired", info.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await Accept(f.CreateClient(), token, "Hawk")).StatusCode);
    }

    [Fact]
    public async Task Revoked_invite_reports_reason()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var (token, id) = await CreateInvite(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/invites/{id}")).StatusCode);
        Assert.Equal("revoked", await Reason(f.CreateClient(), token));
        Assert.Equal("unknown", await Reason(f.CreateClient(), "not-a-real-token"));
    }

    [Fact]
    public async Task Non_owner_cannot_invite_beyond_own_access()
    {
        using var f = new PanelFactory();
        await TestUsers.CreateAsync(f, "S4", "Admin");
        await TestUsers.AddClaimAsync(f, "Admin", "perm.grant", "members.manage");
        var admin = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(admin, "Admin"));
        var roles = (await admin.GetFromJsonAsync<JsonElement>("/api/roles")).EnumerateArray().ToArray();
        string IdOf(string name) => roles.First(r => r.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;

        var owner = await admin.PostAsJsonAsync("/api/invites", new { roleId = IdOf("Owner"), expiresHours = (int?)null });
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal("escalation", (await owner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var wider = await admin.PostAsJsonAsync("/api/invites", new { roleId = IdOf("S3"), expiresHours = (int?)null }); // S3 holds what an S4 manager doesn't
        Assert.Equal(HttpStatusCode.Forbidden, wider.StatusCode);
        Assert.Equal("escalation", (await wider.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/invites", new { roleId = IdOf("S6"), expiresHours = (int?)null })).StatusCode); // a role inside their own access
    }

    [Fact]
    public async Task Callsign_collision_is_409()
    {
        using var f = new PanelFactory();
        var (token, _) = await CreateInvite(await TestUsers.LoginAsync(f, "Owner", "Crow"));
        await TestUsers.CreateAsync(f, "Member", "skye");
        var r = await Accept(f.CreateClient(), token, " Skye ");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("conflict_username", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = f.Services.CreateScope();
        Assert.Equal(2, scope.ServiceProvider.GetRequiredService<AppDb>().Users.Count());
    }
}
