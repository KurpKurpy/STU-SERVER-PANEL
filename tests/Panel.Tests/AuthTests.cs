using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Panel.Tests;

public class AuthTests
{
    internal const string Password = "correct horse battery";

    internal static string SetupToken(PanelFactory f) => File.ReadAllText(Path.Combine(f.DataDir, "setup-token.txt")).Trim();

    internal static Task<HttpResponseMessage> PostSetup(HttpClient c, string token, string userName = "Crow") =>
        c.PostAsJsonAsync("/api/setup", new { token, userName, displayName = userName, password = Password });

    internal static async Task<string> Login(HttpClient c, string userName, string password = Password)
    {
        var r = await c.PostAsJsonAsync("/api/auth/login", new { userName, password });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Setup_creates_owner_once()
    {
        using var f = new PanelFactory();
        var c = f.CreateClient();
        Assert.True((await Json(await c.GetAsync("/api/setup"))).GetProperty("needed").GetBoolean());

        var token = SetupToken(f);
        Assert.Equal(HttpStatusCode.OK, (await PostSetup(c, token)).StatusCode);
        Assert.True((await Json(await c.GetAsync("/api/me"))).GetProperty("isOwner").GetBoolean());

        var again = await PostSetup(f.CreateClient(), token, "Intruder");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("setup_invalid", (await Json(again)).GetProperty("code").GetString());
        Assert.False((await Json(await c.GetAsync("/api/setup"))).GetProperty("needed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(f.DataDir, "setup-token.txt")));
    }

    [Fact]
    public async Task Setup_with_wrong_token_is_rejected()
    {
        using var f = new PanelFactory();
        var r = await PostSetup(f.CreateClient(), "not-the-token");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await Json(await f.CreateClient().GetAsync("/api/setup"))).GetProperty("needed").GetBoolean());
    }

    [Fact]
    public async Task Login_ok_then_me_returns_user()
    {
        using var f = new PanelFactory();
        await PostSetup(f.CreateClient(), SetupToken(f));
        var c = f.CreateClient();
        Assert.Equal("ok", await Login(c, " Crow "));
        var me = await Json(await c.GetAsync("/api/me"));
        Assert.Equal("Crow", me.GetProperty("userName").GetString());
        Assert.Contains("Owner", me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_without_session_is_401()
    {
        using var f = new PanelFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_api_request_without_header_is_400()
    {
        using var f = new PanelFactory();
        var r = await f.CreateClientWithoutHeader().PostAsJsonAsync("/api/auth/login", new { userName = "Crow", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("header_required", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reads_and_sign_outs_do_not_use_up_the_sign_in_limit()
    {
        using var f = new PanelFactory(authPerMinute: 3);
        var c = f.CreateClient();
        var setupToken = SetupToken(f);
        for (var i = 0; i < 5; i++) await c.GetAsync("/api/setup");
        await PostSetup(c, setupToken);
        for (var i = 0; i < 3; i++) await c.PostAsync("/api/auth/logout", null);
        Assert.Equal("ok", await Login(f.CreateClient(), "Crow"));
    }

    [Fact]
    public async Task Repeated_sign_in_attempts_are_rate_limited()
    {
        using var f = new PanelFactory(authPerMinute: 3);
        var c = f.CreateClient();
        for (var i = 0; i < 3; i++) await c.PostAsJsonAsync("/api/auth/login", new { userName = "Nobody", password = "guess guess guess" });
        var r = await c.PostAsJsonAsync("/api/auth/login", new { userName = "Nobody", password = "guess guess guess" });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task Five_bad_passwords_lock_the_account()
    {
        using var f = new PanelFactory();
        await PostSetup(f.CreateClient(), SetupToken(f));
        var c = f.CreateClient();
        for (var i = 0; i < 5; i++) await Login(c, "Crow", "wrong password here");
        Assert.Equal("lockedOut", await Login(c, "Crow"));
    }
}
