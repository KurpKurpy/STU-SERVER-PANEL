using System.Buffers.Binary;

namespace ArmaFormats;

// BI's LZSS, used by compressed PBO entries (1,595 of 1,599 compressed configs on the K: install, spike S3). A flag byte covers
// 8 items, low bit first: set = a literal byte, clear = a back-reference (12-bit distance, 4-bit length + 3); a reference before the
// start yields spaces. The 32-bit sum of the output bytes follows. Bad input is a PboException, never a crash or a huge allocation.
public static class Lzss
{
    public const int MaxLength = 64 * 1024 * 1024;

    public static byte[] Decompress(ReadOnlySpan<byte> input, int length)
    {
        // Each 2-byte reference expands to at most 18 bytes, so a real stream is never more than ~9× smaller than its output.
        if (length is < 1 or > MaxLength || (long)input.Length * 9 < length) throw new PboException("A compressed entry has an impossible size.");
        var output = new byte[length];
        int i = 0, o = 0;
        while (o < length)
        {
            if (i >= input.Length) throw Truncated();
            var flags = input[i++];
            for (var bit = 0; bit < 8 && o < length; bit++, flags >>= 1)
            {
                if ((flags & 1) != 0)
                {
                    if (i >= input.Length) throw Truncated();
                    output[o++] = input[i++];
                    continue;
                }
                if (i + 1 >= input.Length) throw Truncated();
                int b1 = input[i++], b2 = input[i++];
                var from = o - (b1 | ((b2 & 0xF0) << 4));
                var run = (b2 & 0x0F) + 3;
                for (var k = 0; k < run && o < length; k++, from++) output[o++] = from < 0 ? (byte)' ' : output[from];
            }
        }
        if (i + 4 > input.Length) throw Truncated();
        uint sum = 0;
        foreach (var b in output) sum += b;
        if (BinaryPrimitives.ReadUInt32LittleEndian(input[i..]) != sum) throw new PboException("A compressed entry's checksum doesn't match: the file is damaged.");
        return output;
    }

    private static PboException Truncated() => new("A compressed entry ends early: the file is damaged or incomplete.");
}
