using System.Globalization;
using System.Text;

namespace ArmaFormats.Tests;

// Test-only raP (binarized config) writer, so tests never need a real mission. Layout: "\0raP", 0, 8, enum offset; class bodies
// (inherited name, compressed count, entries), each child body written after its parent with its offset patched in; an empty enum table.
internal static class RapBuilder
{
    public static byte[] Build(IReadOnlyList<ConfigItem> root)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[] { 0, (byte)'r', (byte)'a', (byte)'P' });
        w.Write(0u);
        w.Write(8u);
        w.Write(0u); // enum offset, patched below
        Body(w, null, root);
        var enums = (uint)ms.Position;
        w.Write(0u); // no enums
        ms.Position = 12;
        w.Write(enums);
        return ms.ToArray();
    }

    private static void Body(BinaryWriter w, string? inherits, IReadOnlyList<ConfigItem> all)
    {
        var items = all.Where(i => i is not CfgTrivia).ToList(); // comments and blank lines from parsed text aren't entries
        Str(w, inherits ?? "");
        Compressed(w, items.Count);
        var children = new List<(long Patch, CfgClass Class)>();
        foreach (var item in items)
        {
            switch (item)
            {
                case CfgClass { Body: null } c:
                    w.Write((byte)3); Str(w, c.Name); break;
                case CfgClass c:
                    w.Write((byte)0); Str(w, c.Name); children.Add((w.BaseStream.Position, c)); w.Write(0u); break;
                case CfgDelete d:
                    w.Write((byte)4); Str(w, d.Name); break;
                case CfgAssignment { Value: CfgArray a, Append: true } x:
                    w.Write((byte)5); w.Write(1u); Str(w, x.Name); Array(w, a); break;
                case CfgAssignment { Value: CfgArray a } x:
                    w.Write((byte)2); Str(w, x.Name); Array(w, a); break;
                case CfgAssignment x:
                    w.Write((byte)1);
                    var (type, write) = Scalar(x.Value);
                    w.Write(type); Str(w, x.Name); write(w); break;
            }
        }
        // Every body ends with the offset of the end of this class and all its sub-classes (the root's is the enum offset),
        // as real Arma files do (Phase 4 corpus gate).
        var endPatch = w.BaseStream.Position;
        w.Write(0u);
        foreach (var (patch, c) in children)
        {
            var at = (uint)w.BaseStream.Position;
            Body(w, c.Base, c.Body!);
            var end = w.BaseStream.Position;
            w.BaseStream.Position = patch;
            w.Write(at);
            w.BaseStream.Position = end;
        }
        var subtreeEnd = (uint)w.BaseStream.Position;
        w.BaseStream.Position = endPatch;
        w.Write(subtreeEnd);
        w.BaseStream.Position = subtreeEnd;
    }

    private static void Array(BinaryWriter w, CfgArray a)
    {
        Compressed(w, a.Items.Count);
        foreach (var v in a.Items)
        {
            if (v is CfgArray nested) { w.Write((byte)3); Array(w, nested); continue; }
            var (type, write) = Scalar(v);
            w.Write(type == 0 && v is CfgWord ? (byte)4 : type);
            write(w);
        }
    }

    // Value subtypes: 0 string, 1 float, 2 int32, 4 variable (arrays) — a whole-number Raw is an int, anything else a float.
    private static (byte Type, Action<BinaryWriter> Write) Scalar(ConfigValue v) => v switch
    {
        CfgString s => (0, w => Str(w, s.Value)),
        CfgWord word => (0, w => Str(w, word.Value)),
        CfgNumber n when int.TryParse(n.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) => (2, w => w.Write(i)),
        CfgNumber n => (1, w => w.Write((float)n.Value)),
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    private static void Str(BinaryWriter w, string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }

    private static void Compressed(BinaryWriter w, int value)
    {
        var v = (uint)value;
        do { var b = (byte)(v & 0x7F); v >>= 7; w.Write(v != 0 ? (byte)(b | 0x80) : b); } while (v != 0);
    }

    // Shorthands for building trees.
    public static CfgAssignment Set(string name, ConfigValue value, bool append = false) => new(name, value is CfgArray, append, value, 0, 0, 0);
    public static CfgClass Class(string name, params ConfigItem[] body) => new(name, null, body, 0, 0, 0);
    public static CfgClass ClassOf(string name, string inherits, params ConfigItem[] body) => new(name, inherits, body, 0, 0, 0);
    public static CfgString S(string s) => new(s);
    public static CfgNumber I(int i) => new(i, i.ToString(CultureInfo.InvariantCulture));
    public static CfgNumber F(float f) => new(f, "");
    public static CfgArray A(params ConfigValue[] items) => new(items);
}
