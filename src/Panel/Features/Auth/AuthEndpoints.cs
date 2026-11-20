using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;

namespace Panel.Features.Auth;

public record SetupStatus(bool Needed);
public record SetupRequest(string Token, string UserName, string DisplayName, string Password);
public record LoginRequest(string UserName, string Password);
public record LoginResult(string Status);
public record ResetRequest(Guid UserId, string Token, string NewPassword);
public record TwoFactorLoginRequest(string? Code, string? RecoveryCode);
public record MeDto(Guid Id, string UserName, string DisplayName, string? Rank, string? Section, string[] Roles,
    string[] Permissions, bool IsOwner, bool TwoFactorEnabled, bool MustEnrolTwoFactor);

public static class AuthEndpoints
{
    public static bool IsDisabled(User u) => u.LockoutEnd is { } end && end.Year >= 9999;

    public static IResult ValidationError(IdentityResult result) =>
        ApiError.Of(400, "validation", string.Join(" ", result.Errors.Select(e => e.Description)));

    public static void MapAuth(this WebApplication app)
    {
        // Only endpoints that take a credential or secret count toward the "auth" limit; status reads and sign-out never do.
        var setup = app.MapGroup("/api/setup");
        setup.MapGet("", (SetupState state) => new SetupStatus(state.Needed));
        setup.MapPost("", Setup).RequireRateLimiting("auth");

        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", Login).RequireRateLimiting("auth");
        auth.MapPost("/logout", async (SignInManager<User> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });
        auth.MapPost("/reset", Reset).RequireRateLimiting("auth");
        auth.MapPost("/2fa", TwoFactor).RequireRateLimiting("auth");

        app.MapGet("/api/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Setup(SetupRequest r, SetupState state, PanelPaths paths, UserManager<User> users,
        SignInManager<User> signIn, AuditLog audit)
    {
        await state.Gate.WaitAsync();
        try
        {
            if (!state.Matches(r.Token)) return ApiError.Of(400, "setup_invalid", "That setup token isn't valid, or setup is already done.");
            var owner = new User { UserName = r.UserName.Trim(), DisplayName = r.DisplayName.Trim(), LastLoginAt = DateTime.UtcNow };
            var created = await users.CreateAsync(owner, r.Password);
            if (!created.Succeeded) return ValidationError(created);
            await users.AddToRoleAsync(owner, Permissions.OwnerRole);
            state.Complete(paths);
            await signIn.SignInAsync(owner, isPersistent: true);
            await audit.RecordAsync("setup.completed", $"{owner.UserName} created the Owner account", "user", owner.Id.ToString(), actorId: owner.Id);
            return Results.Ok();
        }
        finally { state.Gate.Release(); }
    }

    private static async Task<LoginResult> Login(LoginRequest r, UserManager<User> users, SignInManager<User> signIn, AuditLog audit)
    {
        var name = r.UserName.Trim();
        var user = await users.FindByNameAsync(name);
        if (user is null)
        {
            // Never log what was typed: members sometimes type their password into the callsign field.
            await audit.RecordAsync("auth.failed", "Sign-in failed for an unknown callsign");
            return new LoginResult("invalid");
        }
        if (IsDisabled(user)) return new LoginResult("disabled");

        var result = await signIn.PasswordSignInAsync(user, r.Password, isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await users.UpdateAsync(user);
            return new LoginResult("ok");
        }
        if (result.RequiresTwoFactor) return new LoginResult("twoFactor");
        await audit.RecordAsync("auth.failed", $"Sign-in failed for {user.UserName}", "user", user.Id.ToString(), actorId: user.Id);
        return new LoginResult(result.IsLockedOut ? "lockedOut" : "invalid");
    }

    private static async Task<LoginResult> TwoFactor(TwoFactorLoginRequest r, SignInManager<User> signIn, UserManager<User> users, AuditLog audit)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return new LoginResult("invalid");
        var result = !string.IsNullOrWhiteSpace(r.RecoveryCode)
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(r.RecoveryCode.Replace(" ", ""))
            : await signIn.TwoFactorAuthenticatorSignInAsync((r.Code ?? "").Replace(" ", ""), isPersistent: true, rememberClient: false);
        if (result.Succeeded)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await users.UpdateAsync(user);
            return new LoginResult("ok");
        }
        await audit.RecordAsync("auth.failed", $"Two-factor code rejected for {user.UserName}", "user", user.Id.ToString(), actorId: user.Id);
        return new LoginResult(result.IsLockedOut ? "lockedOut" : "invalid");
    }

    private static async Task<IResult> Reset(ResetRequest r, UserManager<User> users, AuditLog audit)
    {
        var user = await users.FindByIdAsync(r.UserId.ToString());
        string token;
        try { token = System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(r.Token ?? "")); }
        catch (FormatException) { token = ""; }
        if (user is null || token.Length == 0) return ApiError.Of(400, "reset_invalid", "This reset link isn't valid any more. Ask an admin for a new one.");
        var result = await users.ResetPasswordAsync(user, token, r.NewPassword ?? "");
        if (result.Succeeded)
        {
            await audit.RecordAsync("auth.password_reset", $"{user.UserName} reset their password", "user", user.Id.ToString(), actorId: user.Id);
            return Results.NoContent();
        }
        return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
            ? ApiError.Of(400, "reset_invalid", "This reset link isn't valid any more. Ask an admin for a new one.")
            : ValidationError(result);
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, UserManager<User> users, PermissionService permissions,
        Panel.Settings.AppSettingsService settings)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        var roles = (await users.GetRolesAsync(user)).ToArray();
        var effective = (await permissions.EffectiveAsync(user.Id)).Order().ToArray();
        return Results.Ok(new MeDto(user.Id, user.UserName!, user.DisplayName, user.Rank, user.Section, roles, effective,
            roles.Contains(Permissions.OwnerRole), user.TwoFactorEnabled, await TwoFactorGate.MustEnrolAsync(user, users, settings)));
    }
}
