using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Panel.Features.Mods;

public sealed record PresetMod(string Name, long? WorkshopId, string? LocalFolder);

public sealed record ParsedPreset(string? Name, PresetMod[] Mods, string[] Problems);

// The Arma 3 Launcher's HTML preset (Phase 5a Ruling 13), or plain pasted Workshop IDs and links. Regexes have timeouts and the
// endpoints cap the text at 512 KB (Review Focus 4).
public static partial class LauncherPreset
{
    [GeneratedRegex(@"<tr\s+data-type=""ModContainer""\s*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Row();

    [GeneratedRegex(@"<td\s+data-type=""DisplayName""\s*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"filedetails/\?id=(\d{1,19})", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WorkshopLink();

    [GeneratedRegex(@"data-meta=""local:([^|""]*)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LocalMeta();

    [GeneratedRegex(@"<meta\s+name=""arma:PresetName""\s+content=""([^""]*)""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PresetName();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^\d{1,19}$")]
    private static partial Regex BareId();

    public static ParsedPreset Parse(string text)
    {
        try { return text.Contains("ModContainer", StringComparison.OrdinalIgnoreCase) ? ParseHtml(text) : ParseLines(text); }
        catch (RegexMatchTimeoutException) { return new ParsedPreset(null, [], ["The preset is too unusual to read. Export it again from the Launcher."]); }
    }

    private static ParsedPreset ParseHtml(string html)
    {
        var name = PresetName().Match(html) is { Success: true } n ? WebUtility.HtmlDecode(n.Groups[1].Value).Trim() : null;
        var mods = new List<PresetMod>();
        var problems = new List<string>();
        foreach (Match row in Row().Matches(html))
        {
            var cells = row.Groups[1].Value;
            var display = DisplayName().Match(cells) is { Success: true } d ? WebUtility.HtmlDecode(Tag().Replace(d.Groups[1].Value, "")).Trim() : "";
            if (WorkshopLink().Match(cells) is { Success: true } link && long.TryParse(link.Groups[1].Value, out var id) && id > 0)
            {
                if (!mods.Any(m => m.WorkshopId == id)) mods.Add(new PresetMod(display.Length > 0 ? display : $"Workshop item {id}", id, null));
            }
            else if ((LocalMeta().Match(cells) is { Success: true } local ? WebUtility.HtmlDecode(local.Groups[1].Value).Trim() : display) is { Length: > 0 } folder)
                mods.Add(new PresetMod(display.Length > 0 ? display : folder, null, folder));
            else problems.Add("A row in the preset names no mod.");
        }
        return new ParsedPreset(name is { Length: > 0 } ? name : null, mods.ToArray(), problems.ToArray());
    }

    private static ParsedPreset ParseLines(string text)
    {
        var mods = new List<PresetMod>();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            long id;
            // 19 digits can still be past the largest id: that's a bad line too, not a crash.
            if (WorkshopLink().Match(line) is { Success: true } link && long.TryParse(link.Groups[1].Value, out id)) { }
            else if (BareId().IsMatch(line) && long.TryParse(line, out id)) { }
            else
            {
                problems.Add($"Line {i + 1} isn't a Workshop ID or link: {(line.Length > 60 ? line[..60] + "…" : line)}");
                continue;
            }
            if (id > 0 && !mods.Any(m => m.WorkshopId == id)) mods.Add(new PresetMod($"Workshop item {id}", id, null));
        }
        return new ParsedPreset(null, mods.ToArray(), problems.ToArray());
    }

    // The Launcher's own shape, so the file imports there (MODS → PRESET → IMPORT).
    public static string Export(string name, IEnumerable<(string Name, long? WorkshopId, string? Local)> mods)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html>\n  <!--Created by 13th Console-->\n  <head>\n");
        sb.Append("    <meta name=\"arma:Type\" content=\"preset\" />\n");
        sb.Append($"    <meta name=\"arma:PresetName\" content=\"{E(name)}\" />\n");
        sb.Append("    <meta name=\"generator\" content=\"13th Console\" />\n    <title>Arma 3</title>\n  </head>\n  <body>\n");
        sb.Append($"    <h1>Arma 3 - Preset <strong>{E(name)}</strong></h1>\n");
        sb.Append("    <p class=\"before-list\"><em>Import this file in the Arma 3 Launcher: MODS, then PRESET, then IMPORT.</em></p>\n");
        sb.Append("    <div class=\"mod-list\">\n      <table>\n");
        foreach (var (modName, workshopId, local) in mods)
        {
            sb.Append("        <tr data-type=\"ModContainer\">\n");
            sb.Append($"          <td data-type=\"DisplayName\">{E(modName)}</td>\n");
            if (workshopId is { } id)
            {
                var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
                sb.Append("          <td><span class=\"from-steam\">Steam</span></td>\n");
                sb.Append($"          <td><a href=\"{url}\" data-type=\"Link\">{url}</a></td>\n");
            }
            else
            {
                sb.Append("          <td><span class=\"from-local\">Local</span></td>\n");
                sb.Append($"          <td><span data-type=\"Link\" data-meta=\"local:{E(local ?? modName)}|{E(local ?? modName)}|\"></span></td>\n");
            }
            sb.Append("        </tr>\n");
        }
        sb.Append("      </table>\n    </div>\n  </body>\n</html>\n");
        return sb.ToString();
    }
}
