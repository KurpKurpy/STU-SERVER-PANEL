using System.Globalization;
using static ArmaFormats.Tests.RapBuilder;

namespace ArmaFormats.Tests;

public class RapTests
{
    private static readonly ConfigItem[] Tree =
    [
        Set("version", I(54)),
        Set("addons", A(S("A3_Characters_F"), S("cba_main"))),
        Set("nested", A(I(1), A(F(0.5f), S("x")))),
        Set("extra", A(S("y")), append: true),
        Class("EditorData", Set("moveGridStep", F(1.25f)), Set("name", S("say \"hi\""))),
        ClassOf("Child", "EditorData", Set("n", I(-3))),
        new CfgClass("Ext", null, null, 0, 0, 0),
    ];

    private static ConfigItem Get(IReadOnlyList<ConfigItem> items, string name) =>
        items.Single(i => i is CfgAssignment a && a.Name == name || i is CfgClass c && c.Name == name);

    // One canonical line per item, numbers compared as the 32-bit floats raP stores.
    internal static string Shape(IEnumerable<ConfigItem> items) => string.Join("|", items.Where(i => i is not CfgTrivia).Select(i => i switch
    {
        CfgClass c => $"class {c.Name}:{c.Base}{(c.Body is null ? ";" : "{" + Shape(c.Body) + "}")}",
        CfgAssignment a => $"{a.Name}{(a.IsArray ? "[]" : "")}{(a.Append ? "+=" : "=")}{Value(a.Value)}",
        CfgDelete d => $"delete {d.Name}",
        _ => "?",
    }));

    private static string Value(ConfigValue v) => v switch
    {
        CfgString s => "\"" + s.Value + "\"",
        CfgNumber n => ((float)n.Value).ToString("R", CultureInfo.InvariantCulture),
        CfgWord w => w.Value,
        CfgArray a => "{" + string.Join(",", a.Items.Select(Value)) + "}",
        _ => "?",
    };

    [Fact]
    public void Rap_reads_classes_values_arrays_extern_delete_and_append()
    {
        var bytes = Build([.. Tree, new CfgDelete("Gone", 0, 0, 0)]);

        var items = Rap.Read(bytes);

        Assert.True(Rap.IsRap(bytes));
        Assert.Equal(Shape([.. Tree, new CfgDelete("Gone", 0, 0, 0)]), Shape(items));
        Assert.Equal("54", ((CfgNumber)((CfgAssignment)Get(items, "version")).Value).Raw);
        Assert.Equal("say \"hi\"", ((CfgString)((CfgAssignment)Get(((CfgClass)Get(items, "EditorData")).Body!, "name")).Value).Value);
        Assert.Null(((CfgClass)Get(items, "Ext")).Body);
    }

    [Fact]
    public void Rap_to_text_parses_back_to_the_same_tree()
    {
        var items = Rap.Read(Build(Tree));

        var text = ConfigText.Write(items);

        Assert.Equal(Shape(items), Shape(ConfigDocument.Parse(text).Items));
        Assert.Contains("delete Gone;", ConfigText.Write([new CfgDelete("Gone", 0, 0, 0)]));
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(1e-7f)]
    [InlineData(3.4e38f)]
    [InlineData(-123456.78f)]
    [InlineData(1.4e-45f)]
    [InlineData(16777217f)]
    public void Floats_written_as_text_read_back_exactly(float value)
    {
        var text = ConfigText.Write(Rap.Read(Build([Set("x", F(value))])));

        var back = (CfgNumber)((CfgAssignment)ConfigDocument.Parse(text).Items.First(i => i is CfgAssignment)).Value;

        Assert.DoesNotContain("E", text, StringComparison.OrdinalIgnoreCase); // no exponent form in text Arma reads
        Assert.Equal(value, (float)back.Value);
    }

    [Fact]
    public void Rap_with_trailing_garbage_or_truncation_throws()
    {
        var bytes = Build(Tree);

        Assert.Throws<RapException>(() => Rap.Read([.. bytes, 0x42]));
        for (var cut = 4; cut < bytes.Length; cut += 3)
            Assert.Throws<RapException>(() => Rap.Read(bytes[..cut]));
        Assert.False(Rap.IsRap("version=54;"u8));
    }

    [Fact]
    public void Deep_nesting_is_refused_not_a_crash()
    {
        ConfigItem inner = Set("x", I(1));
        for (var i = 0; i < 70; i++) inner = Class("C" + i, inner);

        Assert.Throws<RapException>(() => Rap.Read(Build([inner])));
    }

    // Final review I-1: bodies in a chain, each with two classes pointing at the next one, cost 2^n steps unless a second visit is refused.
    [Fact]
    public void A_class_body_shared_by_two_classes_is_refused_fast()
    {
        Assert.Contains("used twice", Assert.Throws<RapException>(() => Rap.Read(SharedChain(3))).Message);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<RapException>(() => Rap.Read(SharedChain(64)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    private static byte[] SharedChain(int bodies)
    {
        const int Full = 1 + 1 + 2 * (1 + 2 + 4) + 4; // inherits "", count 2, two class entries "a"/"b", subtree end
        var last = 16 + Full * (bodies - 1);
        var enumOffset = last + 6;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[] { 0, (byte)'r', (byte)'a', (byte)'P' });
        w.Write(0u); w.Write(8u); w.Write((uint)enumOffset);
        for (var i = 0; i < bodies - 1; i++)
        {
            var next = (uint)(16 + Full * (i + 1));
            w.Write((byte)0); w.Write((byte)2);
            foreach (var name in "ab") { w.Write((byte)0); w.Write((byte)name); w.Write((byte)0); w.Write(next); }
            w.Write((uint)enumOffset);
        }
        w.Write((byte)0); w.Write((byte)0); w.Write((uint)enumOffset); // the last body: no entries
        w.Write(0u); // no enums
        return ms.ToArray();
    }

    // Spike S2: real 3den/ui_f configs declare more enum entries than they hold.
    [Fact]
    public void Rap_reads_an_enum_table_whose_count_is_larger_than_its_entries()
    {
        var rap = Build([Class("CfgPatches", Class("a"))]);
        byte[] enums = [.. BitConverter.GetBytes(3u), (byte)'x', 0, .. BitConverter.GetBytes(1)];
        var data = rap[..^4].Concat(enums).ToArray();
        Assert.Equal("a", ((CfgClass)Assert.Single(((CfgClass)Assert.Single(Rap.Read(data))).Body!)).Name);
    }

    [Fact]
    public void Rap_still_refuses_a_cut_enum_entry()
    {
        var rap = Build([Class("CfgPatches", Class("a"))]);
        byte[] enums = [.. BitConverter.GetBytes(1u), (byte)'x', 0, 1, 0];
        Assert.Throws<RapException>(() => Rap.Read(rap[..^4].Concat(enums).ToArray()));
    }
}
