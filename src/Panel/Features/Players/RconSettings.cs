using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Players;

// Phase 7a Rulings 3–4: RCon runs on 127.0.0.1 at game port + 10 (outside the firewall's port … port + 4) with a generated password that's
// kept encrypted on the profile, written by the agent into BattlEye's cfg at each start, and never shown to anyone.
public static class RconSettings
{
    public const int PortOffset = 10;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static int Port(int gamePort) => gamePort + PortOffset;

    // The server.cfg "BattlEye" setting; on unless it's been turned off.
    public static bool BattlEyeOn(ProfileSettings settings) =>
        !settings.Server.TryGetValue("BattlEye", out var v) || v.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is not ("0" or "false"),
            _ => true,
        };

    private static IDataProtector Protector(IDataProtectionProvider dp) => dp.CreateProtector("profile.rcon");

    public static string NewPassword() => RandomNumberGenerator.GetString(Alphabet, 24);

    // The profile's password, made (and saved) the first time it's needed; one that can't be decrypted any more is simply replaced,
    // since the agent writes BattlEye's cfg at every start anyway.
    public static async Task<string> PasswordAsync(Profile p, AppDb db, IDataProtectionProvider dp)
    {
        if (p.RconPasswordProtected is { Length: > 0 } stored)
            try { return Protector(dp).Unprotect(stored); } catch (CryptographicException) { }
        var fresh = NewPassword();
        p.RconPasswordProtected = Protector(dp).Protect(fresh);
        await db.SaveChangesAsync();
        return fresh;
    }

    public static void Renew(Profile p, IDataProtectionProvider dp) => p.RconPasswordProtected = Protector(dp).Protect(NewPassword());
}
