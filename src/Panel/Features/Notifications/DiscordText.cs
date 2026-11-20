using System.Text.RegularExpressions;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 12: what members wrote (operation, mission, profile names) goes into Discord escaped, so it can't add links,
// formatting, timestamps or emoji; times go in as Discord timestamps, which each reader sees in their own time zone.
public static partial class DiscordText
{
    [GeneratedRegex(@"([\\*_~`|>#\[\]()<@:])")]
    private static partial Regex Special();

    public static string Escape(string s) => Special().Replace(s.Replace("\r", " ").Replace("\n", " "), @"\$1");

    public static string Time(DateTime utc, char style) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:{style}>";
}
