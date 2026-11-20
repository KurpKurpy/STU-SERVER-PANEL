using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Panel.Tests;

public class ContentTests
{
    private static object Announcement(string title = "Mod update Friday 18:00") =>
        new { title, body = "OPTRE and ACE3 update.\nRestart your launcher.", pinned = true };

    private static async Task<string[]> Actions(HttpClient c, string url) =>
        (await c.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToArray();

    [Fact]
    public async Task Created_announcement_appears_in_activity()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/announcements", Announcement())).StatusCode);

        var member = await TestUsers.LoginAsync(f, "Member");
        var list = await member.GetFromJsonAsync<JsonElement>("/api/announcements");
        Assert.Equal("Mod update Friday 18:00", list[0].GetProperty("title").GetString());
        Assert.Equal("Crow", list[0].GetProperty("author").GetString());
        Assert.Contains("announcement.created", await Actions(member, "/api/activity"));
    }

    [Fact]
    public async Task Non_public_audit_is_not_in_activity()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        await owner.PostAsJsonAsync("/api/roles", new { name = "Pilots", description = (string?)null, permissions = Array.Empty<string>() });
        Assert.DoesNotContain("role.created", await Actions(owner, "/api/activity"));
        Assert.Contains("role.created", await Actions(owner, "/api/audit"));
    }

    [Fact]
    public async Task Member_cannot_create_announcement()
    {
        using var f = new PanelFactory();
        var member = await TestUsers.LoginAsync(f, "Member");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/announcements", Announcement())).StatusCode);
    }

    [Fact]
    public async Task Audit_requires_permission()
    {
        using var f = new PanelFactory();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await TestUsers.LoginAsync(f, "Member")).GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await TestUsers.LoginAsync(f, "S1")).GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Servers_is_empty_until_an_agent_exists()
    {
        using var f = new PanelFactory();
        var servers = await (await TestUsers.LoginAsync(f, "Member")).GetFromJsonAsync<JsonElement>("/api/servers");
        Assert.Equal(0, servers.GetArrayLength());
    }

    [Fact]
    public async Task Settings_roundtrip_and_validation()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var ok = await owner.PutAsJsonAsync("/api/settings", new { appName = "13th Console Dev", twoFactorRequiredRoles = new[] { "S3" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("13th Console Dev", (await f.CreateClient().GetFromJsonAsync<JsonElement>("/api/settings/public")).GetProperty("appName").GetString());
        Assert.Equal(["S3"], (await owner.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("twoFactorRequiredRoles").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var bad = await owner.PutAsJsonAsync("/api/settings", new { appName = "x", twoFactorRequiredRoles = new[] { "Nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Timestamps_are_utc_iso()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        await owner.PostAsJsonAsync("/api/announcements", Announcement());
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/announcements");
        Assert.EndsWith("Z", list[0].GetProperty("createdAt").GetString());
    }
}
