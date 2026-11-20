using System.Text.RegularExpressions;

namespace Panel.Features.Players;

public sealed record PlayerRow(int Number, string Ip, int Ping, string Guid, bool Verified, string Name, bool Lobby);
public sealed record BanRow(int Number, string Kind, string Target, string MinutesLeft, string Reason);

// Phase 7a Ruling 7: BattlEye's "players" and "bans" text. The headers come from the spike (BattlEye 1.221); real player rows weren't
// seen on an empty server, so a line that doesn't parse is kept raw rather than dropped.
public static partial class PlayerList
{
    [GeneratedRegex(@"^(?<n>\d+)\s+(?<ip>[0-9.]+):(?<port>\d+)\s+(?<ping>-?\d+)\s+(?<guid>[0-9a-f]{32}|-)(?:\((?<v>OK|\?)\))?\s+(?<name>.*?)(?<lobby>\s+\(Lobby\))?\s*$")]
    private static partial Regex PlayerLine();

    [GeneratedRegex(@"^(?<n>\d+)\s+(?<target>[0-9a-f]{32}|\d{1,3}(?:\.\d{1,3}){3})\s+(?<left>perm|-|\d+)\s*(?<reason>.*)$")]
    private static partial Regex BanLine();

    private static bool IsChrome(string line) =>
        line.Length == 0 || line.StartsWith("Players on server:") || line.StartsWith("[#]") || line.All(c => c == '-')
        || line.StartsWith("GUID Bans:") || line.StartsWith("IP Bans:") || (line.StartsWith('(') && line.EndsWith("in total)"));

    public static (PlayerRow[] Rows, string[] Unparsed) Players(string text)
    {
        var rows = new List<PlayerRow>();
        var raw = new List<string>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r').Trim()))
        {
            if (IsChrome(line)) continue;
            var m = PlayerLine().Match(line);
            if (!m.Success) { raw.Add(line); continue; }
            rows.Add(new PlayerRow(int.Parse(m.Groups["n"].Value), m.Groups["ip"].Value, int.Parse(m.Groups["ping"].Value), m.Groups["guid"].Value,
                m.Groups["v"].Value == "OK", m.Groups["name"].Value, m.Groups["lobby"].Success));
        }
        return (rows.ToArray(), raw.ToArray());
    }

    public static (BanRow[] Rows, string[] Unparsed) Bans(string text)
    {
        var rows = new List<BanRow>();
        var raw = new List<string>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r').Trim()))
        {
            if (IsChrome(line)) continue;
            var m = BanLine().Match(line);
            if (!m.Success) { raw.Add(line); continue; }
            var target = m.Groups["target"].Value;
            rows.Add(new BanRow(int.Parse(m.Groups["n"].Value), target.Contains('.') ? "ip" : "guid", target, m.Groups["left"].Value, m.Groups["reason"].Value.Trim()));
        }
        return (rows.ToArray(), raw.ToArray());
    }
}
