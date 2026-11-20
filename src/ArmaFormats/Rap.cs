using System.Globalization;
using System.Text;

namespace ArmaFormats;

// `delete Name;` — only binarized configs carry it here; the text parser doesn't read it.
public sealed record CfgDelete(string Name, int Line, int Start, int Length) : ConfigItem(Line, Start, Length);
public sealed class RapException(string message) : Exception(message);

// Read-only reader for binarized configs ("raP"), e.g. the binarized mission.sqm Eden writes by default (95 of 100 real PBOs).
// Layout: 00 'r' 'a' 'P', u32 0, u32 8, u32 enum offset; class bodies (inherited name, compressed count, entries) up to the enum offset,
// then the enum table. Every read is bounds-checked, nesting stops at 64, and every byte must be accounted for.
public static class Rap
{
    public const int MaxDepth = 64;

    public static bool IsRap(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0 && data[1] == 'r' && data[2] == 'a' && data[3] == 'P';

    public static IReadOnlyList<ConfigItem> Read(byte[] data)
    {
        if (!IsRap(data) || data.Length < 16) throw new RapException("This isn't a binarized config.");
        var r = new Reader(data);
        r.Pos = 4;
        if (r.U32() != 0 || r.U32() != 8) throw new RapException("The binarized config has an unknown header.");
        var enumOffset = r.U32();
        if (enumOffset < 16 || enumOffset > data.Length) throw new RapException("The binarized config's enum offset is outside the file.");
        r.Limit = (int)enumOffset;
        long used = 16;
        var root = Body(r, 16, 0, ref used, []);
        if (used != enumOffset) throw new RapException("The binarized config has bytes no class uses.");
        r.Limit = data.Length;
        r.Pos = (int)enumOffset;
        r.U32(); // the enum count: not trusted, real configs (3den, ui_f) declare more entries than they hold (Phase 5b spike S2)
        while (r.Pos < data.Length) { r.CString(); r.U32(); } // every entry up to the end must be whole
        return root.Body!;
    }

    // Scanning mods and installs (Phase 5b): the classes defined (not declared) directly under the given top-level classes. Only
    // the root body and those sections are read, bounded by the file's end rather than the enum offset — some community binarizers
    // put the last class's end marker past it, and Arma reads those files (40 configs on the K: install).
    public static IReadOnlyDictionary<string, string[]> SectionClasses(byte[] data, params string[] sections)
    {
        if (!IsRap(data) || data.Length < 16) throw new RapException("This isn't a binarized config.");
        var r = new Reader(data) { Pos = 4 };
        if (r.U32() != 0 || r.U32() != 8) throw new RapException("The binarized config has an unknown header.");
        r.U32(); // the enum offset: not a bound here
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, offset) in ClassEntries(r, 16).Where(c => sections.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
        {
            // The first one only (real configs have one): repeated or shared sections made this quadratic (Phase 5b final review C-1).
            if (result.ContainsKey(name)) continue;
            result[name] = ClassEntries(r, offset).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return result;
    }

    // A body's class entries (type 0: defined here, with the offset of their body); values and arrays are read past.
    private static List<(string Name, int Offset)> ClassEntries(Reader r, int offset)
    {
        if (offset < 16 || offset >= r.Limit) throw new RapException("A class points outside the binarized config.");
        r.Pos = offset;
        r.CString();
        var count = r.Compressed();
        var classes = new List<(string, int)>();
        for (var i = 0; i < count; i++)
        {
            switch (r.Byte())
            {
                case 0: classes.Add((r.CString(), (int)r.U32())); break;
                case 1: { var sub = r.Byte(); r.CString(); Scalar(r, sub); break; }
                case 2: r.CString(); Array(r, 0); break;
                case 3: case 4: r.CString(); break;
                case 5: r.U32(); r.CString(); Array(r, 0); break;
                default: throw new RapException("The binarized config has an unknown entry type.");
            }
        }
        return classes;
    }

    private static CfgClass Body(Reader r, int offset, int depth, ref long used, HashSet<int> visited)
    {
        if (depth > MaxDepth) throw new RapException($"The binarized config nests classes deeper than {MaxDepth} levels.");
        if (offset < 16 || offset >= r.Limit) throw new RapException("A class points outside the binarized config.");
        // Two classes sharing one body would make the walk exponential (final review I-1); real files never share.
        if (!visited.Add(offset)) throw new RapException("A class body is used twice in the binarized config.");
        r.Pos = offset;
        var inherits = r.CString();
        var count = r.Compressed();
        var items = new List<ConfigItem>();
        var children = new List<(int Index, string Name, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            var type = r.Byte();
            switch (type)
            {
                case 0:
                    var name = r.CString();
                    children.Add((items.Count, name, (int)r.U32()));
                    items.Add(null!);
                    break;
                case 1:
                    var sub = r.Byte();
                    var valueName = r.CString();
                    items.Add(new CfgAssignment(valueName, false, false, Scalar(r, sub), 0, 0, 0));
                    break;
                case 2:
                    var arrayName = r.CString();
                    items.Add(new CfgAssignment(arrayName, true, false, Array(r, depth), 0, 0, 0));
                    break;
                case 3:
                    items.Add(new CfgClass(r.CString(), null, null, 0, 0, 0));
                    break;
                case 4:
                    items.Add(new CfgDelete(r.CString(), 0, 0, 0));
                    break;
                case 5:
                    r.U32(); // flags (1 = +=)
                    var appendName = r.CString();
                    items.Add(new CfgAssignment(appendName, true, true, Array(r, depth), 0, 0, 0));
                    break;
                default:
                    throw new RapException($"The binarized config has an unknown entry type {type}.");
            }
        }
        // Each body ends with the offset of the end of this class and its sub-classes (the root's is the enum offset).
        var subtreeEnd = r.U32();
        if (subtreeEnd < r.Pos || subtreeEnd > r.Limit) throw new RapException("A class's end points outside the binarized config.");
        used += r.Pos - offset;
        foreach (var (index, name, childOffset) in children)
        {
            var child = Body(r, childOffset, depth + 1, ref used, visited);
            items[index] = child with { Name = name };
        }
        return new CfgClass("", inherits.Length == 0 ? null : inherits, items, 0, 0, 0);
    }

    private static CfgArray Array(Reader r, int depth)
    {
        if (depth > MaxDepth) throw new RapException($"The binarized config nests arrays deeper than {MaxDepth} levels.");
        var count = r.Compressed();
        var items = new List<ConfigValue>(Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            var type = r.Byte();
            items.Add(type == 3 ? Array(r, depth + 1) : Scalar(r, type));
        }
        return new CfgArray(items);
    }

    // 0 string, 1 float, 2 int32, 4 variable name, 6 int64.
    private static ConfigValue Scalar(Reader r, byte type) => type switch
    {
        0 => new CfgString(r.CString()),
        1 => Float(r.F32()),
        2 => Int(r.I32()),
        4 => new CfgWord(r.CString()),
        6 => Int(r.I64()),
        _ => throw new RapException($"The binarized config has an unknown value type {type}."),
    };

    private static CfgNumber Int(long v) => new(v, v.ToString(CultureInfo.InvariantCulture));
    private static CfgNumber Float(float f) => new(f, ConfigText.FormatFloat(f));

    private sealed class Reader(byte[] data)
    {
        public int Pos;
        public int Limit = data.Length;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > Limit) throw new RapException("The binarized config ends early: the file is damaged or incomplete.");
            var span = data.AsSpan(Pos, n);
            Pos += n;
            return span;
        }

        public byte Byte() => Take(1)[0];
        public uint U32() => BitConverter.ToUInt32(Take(4));
        public int I32() => BitConverter.ToInt32(Take(4));
        public long I64() => BitConverter.ToInt64(Take(8));
        public float F32() => BitConverter.ToSingle(Take(4));

        public string CString()
        {
            var end = System.Array.IndexOf(data, (byte)0, Pos, Limit - Pos);
            if (end < 0) throw new RapException("The binarized config ends early: the file is damaged or incomplete.");
            var s = Encoding.UTF8.GetString(data, Pos, end - Pos);
            Pos = end + 1;
            return s;
        }

        public int Compressed()
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = Byte();
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value < 0 ? throw new RapException("The binarized config has an impossible count.") : value;
            }
            throw new RapException("The binarized config has an impossible count.");
        }
    }
}
