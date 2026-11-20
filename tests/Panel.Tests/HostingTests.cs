using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class HostingTests
{
    [Fact]
    public async Task Unknown_api_route_returns_json_404()
    {
        using var f = new PanelFactory();
        var r = await f.CreateClient().GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("not_found", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<string?> LastFailedLoginIp(PanelFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return (await db.Audit.Where(a => a.Action == "auth.failed").OrderByDescending(a => a.At).FirstAsync()).Ip;
    }

    [Fact]
    public async Task Forwarded_for_from_loopback_is_trusted()
    {
        using var f = new PanelFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal("invalid", await AuthTests.Login(c, "nobody", "wrong password here"));
        Assert.Equal("203.0.113.9", await LastFailedLoginIp(f));
    }

    [Fact]
    public async Task Forwarded_for_from_elsewhere_is_ignored()
    {
        using var f = new PanelFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "198.51.100.7");
        c.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal("invalid", await AuthTests.Login(c, "nobody", "wrong password here"));
        Assert.Equal("198.51.100.7", await LastFailedLoginIp(f));
    }

    // Keys must stay readable when the panel moves from an interactive run to a service account (design §5.2).
    [Fact]
    public void Data_protection_keys_are_protected_with_machine_scope_dpapi()
    {
        using var f = new PanelFactory();
        using (var scope = f.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");
        var xml = XDocument.Load(Directory.GetFiles(Path.Combine(f.DataDir, "keys"), "key-*.xml").Single());
        var blob = Convert.FromBase64String(xml.Descendants("encryptedKey").Single().Element("value")!.Value);
        const uint cryptProtectLocalMachine = 0x4; // dwFlags sits at offset 40 of a DPAPI blob
        Assert.NotEqual(0u, BitConverter.ToUInt32(blob, 40) & cryptProtectLocalMachine);
    }

    [Fact]
    public async Task Deep_link_returns_spa_index()
    {
        using var f = new PanelFactory();
        var r = await f.CreateClient().GetAsync("/members/3f1c");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("13th Console", await r.Content.ReadAsStringAsync());
    }
}
