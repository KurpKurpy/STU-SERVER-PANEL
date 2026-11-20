using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

internal static class TestUsers
{
    public static async Task<User> CreateAsync(PanelFactory f, string role, string? userName = null)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { UserName = userName ?? "u-" + Guid.NewGuid().ToString("N")[..8], DisplayName = "Test" };
        var created = await users.CreateAsync(user, AuthTests.Password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public static async Task<HttpClient> LoginAsync(PanelFactory f, string role, string? userName = null)
    {
        var user = await CreateAsync(f, role, userName);
        var client = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(client, user.UserName!));
        return client;
    }

    // Signs in with a cookie-less client and returns the auth cookie as a "name=value" Cookie header (for hub clients).
    public static async Task<string> CookieAsync(PanelFactory f, string role, string? userName = null)
    {
        var user = await CreateAsync(f, role, userName);
        var raw = f.Server.CreateClient();
        raw.DefaultRequestHeaders.Add("X-Console", "1");
        var r = await raw.PostAsJsonAsync("/api/auth/login", new { userName = user.UserName, password = AuthTests.Password });
        r.EnsureSuccessStatusCode();
        var setCookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("13c.auth="));
        return setCookie[..setCookie.IndexOf(';')];
    }

    public static async Task<Guid> IdAsync(PanelFactory f, string userName)
    {
        using var scope = f.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByNameAsync(userName))!.Id;
    }

    // An S4 (looks after the server) who was also granted members.manage: the "trusted but not Owner" manager.
    public static async Task<HttpClient> ManagerAsync(PanelFactory f, params string[] extraGrants)
    {
        await CreateAsync(f, "S4", "Admin");
        foreach (var p in extraGrants.Prepend("members.manage")) await AddClaimAsync(f, "Admin", "perm.grant", p);
        var client = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(client, "Admin"));
        return client;
    }

    public static async Task AddClaimAsync(PanelFactory f, string userName, string type, string value)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByNameAsync(userName);
        Assert.True((await users.AddClaimAsync(user!, new Claim(type, value))).Succeeded);
    }
}
