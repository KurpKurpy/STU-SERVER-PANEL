using System.Text;
using static ArmaFormats.Tests.RapBuilder;

namespace ArmaFormats.Tests;

public class ConfigScanTests
{
    private static ConfigNames Text(string config) => ConfigScan.Names(Encoding.UTF8.GetBytes(config));

    [Fact]
    public void Patches_and_worlds_from_raP()
    {
        var rap = Build([
            Class("CfgPatches", Class("a", Set("units", A())), Class("b")),
            Class("CfgWorlds", new CfgClass("CAWorld", null, null, 0, 0, 0), Class("Altis"), ClassOf("VR", "CAWorld")),
            Class("Other", Class("CfgPatches", Class("nested"))),
        ]);
        var names = ConfigScan.Names(rap);
        Assert.Equal(["a", "b"], names.Addons);
        Assert.Equal(["Altis", "VR"], names.Worlds); // an extern declaration isn't a world this config defines
        Assert.Null(names.Unreadable);
    }

    [Fact]
    public void A_damaged_raP_is_unreadable()
    {
        var rap = Build([Class("CfgPatches", Class("a"))]);
        var names = ConfigScan.Names(rap[..20]);
        Assert.Empty(names.Addons);
        Assert.NotNull(names.Unreadable);
    }

    // Some community binarizers put the last class's end marker past the enum offset; Arma reads them (40 configs on K:).
    [Fact]
    public void Patches_from_raP_whose_enum_offset_cuts_the_last_body()
    {
        var rap = Build([Class("CfgPatches", Class("a"), Class("b")), Class("CfgVehicles", Class("Car", Class("Inner")))]);
        var enumOffset = BitConverter.ToUInt32(rap, 12);
        BitConverter.GetBytes(enumOffset - 4).CopyTo(rap, 12);
        Assert.Throws<RapException>(() => Rap.Read(rap));
        var names = ConfigScan.Names(rap);
        Assert.Equal(["a", "b"], names.Addons);
        Assert.Null(names.Unreadable);
    }

    // Final review C-1: N root entries named CfgPatches sharing one body of N classes made the read quadratic (31 s, 2 GB at 53 KB).
    [Fact]
    public void Repeated_and_shared_sections_are_read_once()
    {
        var shared = ConfigScan.Names(RepeatedSections(entries: 500, classes: 500, shared: true));
        Assert.Equal(500, shared.Addons.Length);
        var separate = ConfigScan.Names(RepeatedSections(entries: 500, classes: 1, shared: false));
        Assert.Equal(["c0"], separate.Addons); // the first CfgPatches only: real configs have one
    }

    // Final review M-1 (graded up): tokenizing a 64 MB text config took ~1.4 GB on the server box.
    [Fact]
    public void A_huge_text_config_is_unreadable()
    {
        var names = Text("class CfgPatches { class a {}; };\n" + new string(' ', 17 * 1024 * 1024));
        Assert.Empty(names.Addons);
        Assert.Contains("too large", names.Unreadable);
    }

    private static byte[] RepeatedSections(int entries, int classes, bool shared)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Compressed(int v) { do { var b = (byte)(v & 0x7F); v >>= 7; w.Write(v != 0 ? (byte)(b | 0x80) : b); } while (v != 0); }
        void Str(string s) { w.Write(Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }
        w.Write(new byte[] { 0, (byte)'r', (byte)'a', (byte)'P' }); w.Write(0u); w.Write(8u); w.Write(0u);
        var patches = new List<long>();
        w.Write((byte)0); Compressed(entries);
        for (var i = 0; i < entries; i++) { w.Write((byte)0); Str("CfgPatches"); patches.Add(ms.Position); w.Write(0u); }
        w.Write(0u);
        var empty = new List<long>();
        var bodies = new List<uint>();
        for (var b = 0; b < (shared ? 1 : entries); b++)
        {
            bodies.Add((uint)ms.Position);
            w.Write((byte)0); Compressed(classes);
            for (var c = 0; c < classes; c++) { w.Write((byte)0); Str($"c{c}"); empty.Add(ms.Position); w.Write(0u); }
            w.Write(0u);
        }
        var emptyBody = (uint)ms.Position;
        w.Write((byte)0); w.Write((byte)0); w.Write(0u);
        var enumOffset = (uint)ms.Position;
        w.Write(0u);
        for (var i = 0; i < patches.Count; i++) { ms.Position = patches[i]; w.Write(bodies[shared ? 0 : i]); }
        foreach (var at in empty) { ms.Position = at; w.Write(emptyBody); }
        ms.Position = 12; w.Write(enumOffset);
        return ms.ToArray();
    }

    // The unit's own mods: 13th_patches, 13thSTUAUX_loading_screen, 3AS_... (spike: these were wrongly taken for macros).
    [Fact]
    public void Addon_names_may_start_with_a_digit()
    {
        var names = Text("#include \"x.hpp\"\nclass CfgPatches { class 13th_patches {}; class 3AS_Props {}; };");
        Assert.Equal(["13th_patches", "3AS_Props"], names.Addons);
        Assert.Null(names.Unreadable);
    }

    [Fact]
    public void Patches_from_text_skip_comments_strings_and_directives()
    {
        var names = Text("""
            #define FAKE \
                class CfgPatches { class from_macro {}; };
            // class CfgPatches { class commented {}; };
            /* class CfgPatches { class hidden {}; }; */
            class CfgPatches
            {
                class my_addon { units[] = {"a""b"}; requiredAddons[] = {"A3_Data_F"}; name = "class fake {"; };
                class my_addon_two : my_addon {};
                class declared_only;
            };
            class CfgWorlds { class CAWorld; class MyIsland : CAWorld { class Inner {}; }; };
            class CfgVehicles { class my_addon_vehicle {}; };
            """);
        Assert.Equal(["my_addon", "my_addon_two"], names.Addons);
        Assert.Equal(["MyIsland"], names.Worlds);
        Assert.Null(names.Unreadable);
    }

    [Theory]
    [InlineData("#define ADDON my_x\nclass CfgPatches { class ADDON {}; };")]
    [InlineData("class CfgPatches { class DOUBLES(a,b) {}; };")]
    [InlineData("#include \"script_component.hpp\"\nclass CfgPatches { class ADDON {}; };")]
    public void Text_macro_names_are_unreadable(string config)
    {
        var names = Text(config);
        Assert.Empty(names.Addons);
        Assert.Contains("macro", names.Unreadable);
    }

    // Real ALL-CAPS addon names in files that #include something (CHVD, TPW_AOR2 on K:) were taken for macros by the first rule.
    [Fact]
    public void All_caps_names_are_real_names()
    {
        var names = Text("#include \"x.hpp\"\nclass CfgPatches { class CHVD {}; class TPW_AOR2 {}; };");
        Assert.Equal(["CHVD", "TPW_AOR2"], names.Addons);
        Assert.Null(names.Unreadable);
    }

    [Fact]
    public void Real_names_beside_a_macro_name_still_count()
    {
        var names = Text("#include \"x.hpp\"\nclass CfgPatches { class real_one {}; class ADDON {}; };");
        Assert.Equal(["real_one"], names.Addons);
        Assert.NotNull(names.Unreadable);
    }

    [Theory]
    [InlineData("class CfgPatches { class a {}; /* never closed")]
    [InlineData("class CfgPatches { class a { name = \"open")]
    [InlineData("class CfgPatches {")]
    [InlineData("#define X \\")]
    public async Task Text_patches_survive_unterminated_input(string config)
    {
        var names = await Task.Run(() => Text(config)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("", names.Addons);
    }
}
