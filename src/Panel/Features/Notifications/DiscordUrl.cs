using System.Text.RegularExpressions;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 10: a Discord webhook URL, checked by parsing (never by prefix) and stored in one canonical form. The worker checks
// it again before every send, so nothing but Discord's own webhook host is ever contacted.
public static partial class DiscordUrl
{
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    [GeneratedRegex(@"^/api(?:/v\d{1,2})?/webhooks/(\d{17,20})/([A-Za-z0-9_-]{20,200})$")]
    private static partial Regex WebhookPath();

    public static bool TryCanonical(string? input, out string canonical)
    {
        canonical = "";
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (!Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;
        var m = WebhookPath().Match(uri.AbsolutePath);
        if (!m.Success) return false;
        canonical = $"https://discord.com/api/webhooks/{m.Groups[1].Value}/{m.Groups[2].Value}";
        return true;
    }
}
