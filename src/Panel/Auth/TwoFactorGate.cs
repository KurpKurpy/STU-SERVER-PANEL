using Microsoft.AspNetCore.Identity;
using Panel.Api;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Settings;

namespace Panel.Auth;

// Per-request account checks after authentication:
//  1. a disabled (or deleted) account loses its session on its very next request, whatever its cookie's stamp says;
//  2. members of roles that require 2FA can only reach their own account endpoints until they enrol.
public sealed class TwoFactorGate(RequestDelegate next)
{
    private static readonly string[] Open = ["/api/me", "/api/auth", "/api/account", "/api/settings/public"];

    public async Task InvokeAsync(HttpContext ctx, UserManager<User> users, SignInManager<User> signIn, AppSettingsService settings)
    {
        var path = ctx.Request.Path;
        // Member sessions only: an agent's key (it pulls mission PBOs from /api/agent/blobs) has no user behind it.
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.Identity.AuthenticationType != Panel.Hubs.AgentKeyAuthentication.SchemeName
            && path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth"))
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is null || AuthEndpoints.IsDisabled(user))
            {
                await signIn.SignOutAsync();
                await ApiError.Of(401, "signed_out", "This account is disabled.").ExecuteAsync(ctx);
                return;
            }
            if (!Open.Any(p => path.StartsWithSegments(p)) && await MustEnrolAsync(user, users, settings))
            {
                await ApiError.Of(403, "2fa_enrolment_required", "Set up two-factor sign-in before continuing.").ExecuteAsync(ctx);
                return;
            }
        }
        await next(ctx);
    }

    public static async Task<bool> MustEnrolAsync(User user, UserManager<User> users, AppSettingsService settings)
    {
        if (user.TwoFactorEnabled) return false;
        var required = await settings.TwoFactorRequiredRolesAsync();
        return required.Length > 0 && (await users.GetRolesAsync(user)).Intersect(required).Any();
    }
}
