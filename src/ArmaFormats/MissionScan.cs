using System.Text;

namespace ArmaFormats;

public sealed record AddonMeta(string ClassName, string? Name, string? Url);
public sealed record MissionAddons(string[] Addons, AddonMeta[] Meta, bool Binarized);

// What a mission needs (design §5.4 step 5): mission.sqm's root addons[] plus AddonsMetaData.List.Item*, where Eden records the mod each
// addon came from. Text or binarized; a broken file throws ConfigSyntaxException or RapException.
public static class MissionScan
{
    public static IReadOnlyList<ConfigItem> Tree(byte[] sqm) => Rap.IsRap(sqm) ? Rap.Read(sqm) : ConfigDocument.Parse(Text(sqm)).Items;

    // UTF-8, without a byte-order mark.
    public static string Text(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    public static MissionAddons Addons(byte[] sqm)
    {
        var root = Tree(sqm);
        var addons = root.OfType<CfgAssignment>().FirstOrDefault(a => a.Name.Equals("addons", StringComparison.OrdinalIgnoreCase))?.Value is CfgArray list
            ? list.Items.OfType<CfgString>().Select(s => s.Value).ToArray()
            : [];
        var items = Class(Class(root, "AddonsMetaData")?.Body, "List")?.Body ?? [];
        var meta = items.OfType<CfgClass>().Where(c => c.Body is not null)
            .Select(c => (ClassName: Str(c.Body!, "className"), Name: Str(c.Body!, "name"), Url: Str(c.Body!, "url")))
            .Where(m => m.ClassName is not null)
            .Select(m => new AddonMeta(m.ClassName!, m.Name, m.Url))
            .ToArray();
        return new MissionAddons(addons, meta, Rap.IsRap(sqm));
    }

    public static CfgClass? Class(IReadOnlyList<ConfigItem>? items, string name) =>
        items?.OfType<CfgClass>().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string? Str(IReadOnlyList<ConfigItem> items, string name) =>
        items.OfType<CfgAssignment>().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value is CfgString s ? s.Value : null;
}
