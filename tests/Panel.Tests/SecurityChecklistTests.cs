using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Panel.Tests;

// The pre-deploy security checklist (headers, HTTPS, route protection), pinned so a later phase can't quietly undo it.
public class SecurityChecklistTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/api/me")]
    [InlineData("/api/nope")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var f = new PanelFactory();
        var r = await f.CreateClient().GetAsync(path);
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(r, "Referrer-Policy"));
        var csp = Header(r, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }

    [Fact]
    public async Task Https_through_the_proxy_adds_hsts_and_plain_http_does_not()
    {
        using var f = new PanelFactory();
        var plain = await f.CreateClient().GetAsync("/");
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); // what Caddy sends; trusted from loopback only
        Assert.Equal("max-age=31536000", Header(await c.GetAsync("/"), "Strict-Transport-Security"));
    }

    // Every API and hub route needs a signed-in user (or an agent key), except these, which sign people in or up.
    private static readonly string[] Open =
    [
        "GET /api/setup/", "POST /api/setup/", "GET /api/settings/public", // the console's name, for the sign-in page
        "POST /api/auth/login", "POST /api/auth/logout", "POST /api/auth/reset", "POST /api/auth/2fa",
        "GET /api/invites/{token}", "POST /api/invites/{token}/accept",
        "* /api/{**rest}",
    ];

    [Fact]
    public void Only_the_sign_in_routes_are_open()
    {
        using var f = new PanelFactory();
        _ = f.CreateClient();
        var open = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } p && (p.StartsWith("/api") || p.StartsWith("/hubs")))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null && e.Metadata.GetMetadata<AuthorizationPolicy>() is null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(Open.Order(StringComparer.Ordinal).SequenceEqual(open), "Open routes:\n" + string.Join("\n", open));
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";
}
