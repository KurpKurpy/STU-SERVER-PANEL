using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Panel.Tests;

public class TwoFactorTests
{
    private static PanelFactory Factory() => new(twoFactorRequiredRoles: "Owner,S4");

    internal static async Task<(string Key, string[] Codes)> Enrol(HttpClient c)
    {
        var setup = await c.GetFromJsonAsync<JsonElement>("/api/account/2fa");
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("authenticatorUri").GetString());
        var r = await c.PostAsJsonAsync("/api/account/2fa", new { code = Totp.Code(key, DateTime.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var codes = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (key, codes);
    }

    private static async Task<string> Status(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;

    [Fact]
    public async Task Required_role_is_gated_until_enrolled()
    {
        using var f = Factory();
        var admin = await TestUsers.LoginAsync(f, "S4");
        var blocked = await admin.GetAsync("/api/members");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("2fa_enrolment_required", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("mustEnrolTwoFactor").GetBoolean());

        var (_, codes) = await Enrol(admin);
        Assert.Equal(10, codes.Length);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/members")).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("mustEnrolTwoFactor").GetBoolean());
    }

    [Fact]
    public async Task Enrolled_user_login_requires_code()
    {
        using var f = Factory();
        var user = await TestUsers.CreateAsync(f, "S3", "Hawk");
        var first = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(first, "Hawk"));
        var (key, _) = await Enrol(first);

        var c = f.CreateClient();
        Assert.Equal("twoFactor", await AuthTests.Login(c, "Hawk"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me")).StatusCode);
        Assert.Equal("ok", await Status(await c.PostAsJsonAsync("/api/auth/2fa", new { code = Totp.Code(key, DateTime.UtcNow) })));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Recovery_code_works_once()
    {
        using var f = Factory();
        await TestUsers.CreateAsync(f, "S3", "Hawk");
        var first = f.CreateClient();
        await AuthTests.Login(first, "Hawk");
        var (_, codes) = await Enrol(first);

        var c = f.CreateClient();
        await AuthTests.Login(c, "Hawk");
        Assert.Equal("ok", await Status(await c.PostAsJsonAsync("/api/auth/2fa", new { recoveryCode = codes[0] })));

        var again = f.CreateClient();
        await AuthTests.Login(again, "Hawk");
        Assert.Equal("invalid", await Status(await again.PostAsJsonAsync("/api/auth/2fa", new { recoveryCode = codes[0] })));
    }

    [Fact]
    public async Task Wrong_code_is_rejected()
    {
        using var f = Factory();
        var c = await TestUsers.LoginAsync(f, "S3");
        await c.GetAsync("/api/account/2fa");
        var r = await c.PostAsJsonAsync("/api/account/2fa", new { code = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("code_invalid", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
