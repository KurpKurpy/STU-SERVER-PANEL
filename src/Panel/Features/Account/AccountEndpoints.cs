using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Panel.Api;
using Panel.Audit;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Settings;

namespace Panel.Features.Account;

public record TwoFactorSetup(string SharedKey, string AuthenticatorUri);
public record TwoFactorEnable(string Code);
public record RecoveryCodes(string[] Codes);
public record PasswordChange(string Current, string New);

public static class AccountEndpoints
{
    public static void MapAccount(this WebApplication app)
    {
        var g = app.MapGroup("/api/account").RequireAuthorization();
        g.MapGet("/2fa", Setup);
        g.MapPost("/2fa", Enable);
        g.MapPost("/password", ChangePassword);
    }

    // Changing the key, 2FA state or password rotates the security stamp; RefreshSignInAsync keeps this session valid.
    private static async Task<IResult> Setup(ClaimsPrincipal me, UserManager<User> users, SignInManager<User> signIn, AppSettingsService settings)
    {
        var user = await users.GetUserAsync(me);
        if (user is null) return Results.Unauthorized();
        // Once enrolled the secret is never shown again: a stolen session must not be able to clone the second factor.
        if (user.TwoFactorEnabled) return ApiError.Of(409, "already_enabled", "Two-factor sign-in is already on.");
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            await signIn.RefreshSignInAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        var issuer = Uri.EscapeDataString(await settings.AppNameAsync());
        var uri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.UserName!)}?secret={key}&issuer={issuer}&digits=6";
        return Results.Ok(new TwoFactorSetup(key!, uri));
    }

    private static async Task<IResult> Enable(TwoFactorEnable r, ClaimsPrincipal me, UserManager<User> users, SignInManager<User> signIn, AuditLog audit)
    {
        var user = await users.GetUserAsync(me);
        if (user is null) return Results.Unauthorized();
        var code = (r.Code ?? "").Replace(" ", "").Replace("-", "");
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
            return ApiError.Of(400, "code_invalid", "That code didn't match. Check your authenticator app's clock and try the newest code.");
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await signIn.RefreshSignInAsync(user);
        await audit.RecordAsync("auth.2fa_enabled", $"{user.UserName} turned on two-factor sign-in", "user", user.Id.ToString());
        return Results.Ok(new RecoveryCodes(codes!.ToArray()));
    }

    private static async Task<IResult> ChangePassword(PasswordChange r, ClaimsPrincipal me, UserManager<User> users, SignInManager<User> signIn, AuditLog audit)
    {
        var user = await users.GetUserAsync(me);
        if (user is null) return Results.Unauthorized();
        var result = await users.ChangePasswordAsync(user, r.Current ?? "", r.New ?? "");
        if (!result.Succeeded) return AuthEndpoints.ValidationError(result);
        await signIn.RefreshSignInAsync(user);
        await audit.RecordAsync("auth.password_changed", $"{user.UserName} changed their password", "user", user.Id.ToString());
        return Results.NoContent();
    }
}
