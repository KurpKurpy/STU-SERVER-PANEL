using Panel.Features.Missions;

namespace Panel.Tests;

// Phase 5b Rulings 7–8: the pure check of one mission version against what a mod list provides.
public class CompatibilityTests
{
    private static Provided P(string[] addons, string[] worlds, (string Name, string Note)[]? unscanned = null,
        Dictionary<string, string>? library = null, Dictionary<string, string>? libraryWorlds = null) =>
        new(new HashSet<string>(addons, StringComparer.OrdinalIgnoreCase), new HashSet<string>(worlds, StringComparer.OrdinalIgnoreCase),
            (unscanned ?? []).Select(u => new Unscanned(u.Name, u.Note)).ToArray(),
            new Dictionary<string, string>(library ?? [], StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(libraryWorlds ?? [], StringComparer.OrdinalIgnoreCase));

    private static RequiredAddon R(string addon, string? mod = null) => new(addon, mod);

    [Fact]
    public void Ready_when_every_addon_and_the_map_are_provided()
    {
        var r = Compatibility.Check([R("A3_Core"), R("cba_main")], "Altis", P(["A3_Core", "cba_main", "extra"], ["Altis"]));
        Assert.Equal(("Ready", null), (r.Status, r.MissingMap));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Names_and_maps_ignore_case()
    {
        Assert.Equal("Ready", Compatibility.Check([R("a3_core")], "altis", P(["A3_Core"], ["Altis"])).Status);
    }

    [Fact]
    public void Missing_names_eden_mod_and_library_mod()
    {
        var r = Compatibility.Check([R("A3_Core"), R("ace_realisticnames", "ACE3")], "Altis",
            P(["A3_Core"], ["Altis"], library: new() { ["ace_realisticnames"] = "ACE" }));
        Assert.Equal("Missing", r.Status);
        Assert.Equal(new MissingAddon("ace_realisticnames", "ACE3", "ACE"), Assert.Single(r.Missing));
    }

    [Fact]
    public void Missing_map_alone_is_missing()
    {
        var r = Compatibility.Check([R("A3_Core")], "Chernarus", P(["A3_Core"], ["Altis"], libraryWorlds: new() { ["Chernarus"] = "CUP Terrains" }));
        Assert.Equal(("Missing", "Chernarus"), (r.Status, r.MissingMap));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void Unknown_only_when_an_unreadable_mod_could_account_for_it()
    {
        var unscanned = new[] { ("ACE No Medical", "x.pbo: macro"), ("Other", "y.ebo: encrypted") };
        var named = Compatibility.Check([R("A3_Core"), R("ace_thing", "ACE - No medical!")], "Altis", P(["A3_Core"], ["Altis"], unscanned));
        Assert.Equal("Unknown", named.Status);
        Assert.Equal(["ACE No Medical"], named.UnreadableMods); // Eden named it: only that mod could account for it

        var unnamed = Compatibility.Check([R("mystery")], "Altis", P([], ["Altis"], unscanned));
        Assert.Equal("Unknown", unnamed.Status);
        Assert.Equal(["ACE No Medical", "Other"], unnamed.UnreadableMods);
    }

    [Fact]
    public void Eden_naming_another_mod_keeps_it_missing()
    {
        var r = Compatibility.Check([R("3AS_Props", "3AS Props")], "Altis", P([], ["Altis"], [("ACE No Medical", "x.pbo: macro")]));
        Assert.Equal("Missing", r.Status);
    }

    [Fact]
    public void A_scanned_library_mod_providing_it_keeps_it_missing()
    {
        var r = Compatibility.Check([R("ace_main")], "Altis", P([], ["Altis"], [("Unreadable", "x")], library: new() { ["ace_main"] = "ACE" }));
        Assert.Equal("Missing", r.Status);
        Assert.Equal("ACE", Assert.Single(r.Missing).LibraryMod);
    }

    [Fact]
    public void An_unknown_map_with_an_unreadable_mod_is_unknown()
    {
        var r = Compatibility.Check([], "SomeIsland", P([], ["Altis"], [("Terrain pack", "z.pbo: macro")]));
        Assert.Equal(("Unknown", "SomeIsland"), (r.Status, r.MissingMap));
    }

    // Final review M-2 (graded up): Eden names vanilla addons "Arma 3 …", so an install awaiting its scan must cover any gap.
    [Fact]
    public void An_install_waiting_for_its_scan_could_account_for_any_gap()
    {
        var p = new Provided(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(["Altis"], StringComparer.OrdinalIgnoreCase),
            [new Unscanned("Arma 3", "Not scanned yet.", IsInstall: true)], new Dictionary<string, string>(), new Dictionary<string, string>());
        var r = Compatibility.Check([R("A3_Characters_F", "Arma 3 Alpha - Characters and Clothing")], "Altis", p);
        Assert.Equal("Unknown", r.Status);
        Assert.Equal(["Arma 3"], r.UnreadableMods);
    }

    [Fact]
    public void No_required_addons_is_ready_when_the_map_exists_and_a_blank_map_isnt_checked()
    {
        Assert.Equal("Ready", Compatibility.Check([], "VR", P([], ["VR"])).Status);
        Assert.Equal("Ready", Compatibility.Check([], "", P([], [])).Status);
    }
}
