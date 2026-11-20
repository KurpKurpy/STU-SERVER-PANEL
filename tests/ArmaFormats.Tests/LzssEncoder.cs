using System.Text;

namespace ArmaFormats.Tests;

// Test-only BI LZSS writer: a flag byte per 8 items (bit set = literal), a back-reference is 12 bits of distance and 4 bits of
// length - 3 (3–18 bytes), and the sum of the plain bytes follows. Greedy, small window: enough to make real references.
internal static class LzssEncoder
{
    public static byte[] Encode(byte[] plain, bool badChecksum = false)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < plain.Length)
        {
            var flagAt = output.Count;
            output.Add(0);
            byte flags = 0;
            for (var bit = 0; bit < 8 && i < plain.Length; bit++)
            {
                var (distance, length) = LongestMatch(plain, i);
                if (length >= 3)
                {
                    output.Add((byte)(distance & 0xFF));
                    output.Add((byte)(((distance >> 4) & 0xF0) | (length - 3)));
                    i += length;
                }
                else
                {
                    flags |= (byte)(1 << bit);
                    output.Add(plain[i++]);
                }
            }
            output[flagAt] = flags;
        }
        uint sum = 0;
        foreach (var b in plain) sum += b;
        output.AddRange(BitConverter.GetBytes(badChecksum ? sum + 1 : sum));
        return output.ToArray();
    }

    private static (int Distance, int Length) LongestMatch(byte[] data, int at)
    {
        var best = (0, 0);
        for (var from = Math.Max(0, at - 4095); from < at; from++)
        {
            var n = 0;
            while (n < 18 && at + n < data.Length && data[from + n] == data[at + n]) n++;
            if (n > best.Item2) best = (at - from, n);
        }
        return best;
    }

    // A PBO with hand-set packing fields, for entries Pbo.Write can't produce (compressed ones).
    public static byte[] RawPbo(params (string Name, uint Packing, uint Original, byte[] Data)[] entries) => RawPbo(1, entries);

    public static byte[] RawPbo(int properties, params (string Name, uint Packing, uint Original, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Str(string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }
        Str(""); w.Write(0x56657273u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
        Str("prefix"); Str("x\\y");
        for (var i = 1; i < properties; i++) { Str($"junk{i}"); Str("x"); }
        Str("");
        foreach (var e in entries) { Str(e.Name); w.Write(e.Packing); w.Write(e.Original); w.Write(0u); w.Write(0u); w.Write((uint)e.Data.Length); }
        Str(""); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
        foreach (var e in entries) w.Write(e.Data);
        return ms.ToArray();
    }
}
