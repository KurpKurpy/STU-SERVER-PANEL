using System.Text.Json;
using ArmaFormats;
using Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Profiles;

// Turns a Phase 2 profile (raw encrypted server.cfg, BasicCfg text, CustomArgs) into revision 1 at startup (plan Ruling 9).
// Decryption needs Data Protection, so this can't live in the EF migration. On failure the old columns stay and NeedsCfg is set.
public static class ProfileUpgrade
{
    public static async Task RunAsync(AppDb db, ProfileStore store, IDataProtectionProvider dp, ILogger log)
    {
        foreach (var p in await db.Profiles.Where(p => p.SettingsJson == null).ToListAsync())
        {
            var cfg = ProfileEndpoints.TryUnprotectCfg(dp, p.ServerCfgProtected);
            if (cfg is null)
            {
                Flag(p, log, "its server.cfg can't be decrypted (the panel's keys changed)");
                continue;
            }
            try
            {
                var server = ConfigFiles.ParseServerCfg(cfg);
                var basic = string.IsNullOrWhiteSpace(p.BasicCfg) ? null : ConfigFiles.ParseBasicCfg(p.BasicCfg);
                var (launch, extraArgs) = SplitArgs(p.CustomArgs);
                var settings = new ProfileSettings(server.Values, basic?.Values ?? [], [], launch, extraArgs,
                    server.Headless ?? new HeadlessSettings(), server.MissionsOverride);
                var snapshot = new ProfileSnapshot(settings, server.Extra, basic?.Extra ?? "", "", server.Rotation?.ToArray() ?? [], null);
                await store.SaveRevisionAsync(p, snapshot, server.Secrets ?? Secrets.None, "Imported from Phase 2", null);
                (p.ServerCfgProtected, p.BasicCfg, p.NeedsCfg) = ("", null, false);
                await db.SaveChangesAsync();
                log.LogInformation("Upgraded profile {Slug} to revision 1", p.Slug);
            }
            catch (ConfigSyntaxException e)
            {
                Flag(p, log, $"its server.cfg or basic.cfg doesn't parse ({e.Message})");
            }
        }
        await db.SaveChangesAsync();
    }

    private static void Flag(Profile p, ILogger log, string why)
    {
        p.NeedsCfg = true;
        log.LogWarning("Profile {Slug} wasn't upgraded: {Why}. Its old configuration is kept; paste or import server.cfg again.", p.Slug, why);
    }

    // Known launch arguments become form values; anything else stays as extra arguments.
    private static (Dictionary<string, JsonElement> Launch, string Extra) SplitArgs(string customArgs)
    {
        var launch = new Dictionary<string, JsonElement>();
        var extra = new List<string>();
        foreach (var arg in Arma3LaunchArgs.Parse(customArgs))
        {
            var eq = arg.IndexOf('=');
            var key = ConfigCatalog.Find(ConfigCatalog.Launch, eq < 0 ? arg : arg[..eq]);
            JsonElement? value = key?.Type switch
            {
                CfgType.Flag when eq < 0 => JsonSerializer.SerializeToElement(true),
                CfgType.Integer when eq > 0 && long.TryParse(arg[(eq + 1)..], out var n) => JsonSerializer.SerializeToElement(n),
                CfgType.Text when eq > 0 => JsonSerializer.SerializeToElement(arg[(eq + 1)..]),
                _ => null,
            };
            if (key is not null && value is { } v && !launch.ContainsKey(key.Name) && ConfigFiles.Check(key, v) is null) launch[key.Name] = v;
            else extra.Add(arg);
        }
        return (launch, string.Join(' ', extra));
    }
}
