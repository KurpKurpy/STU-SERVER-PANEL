using System.Text;

namespace ArmaFormats.Tests;

public class LzssTests
{
    [Fact]
    public void Lzss_round_trips_literals_and_back_references()
    {
        var plain = Encoding.ASCII.GetBytes("class CfgPatches { class abc { units[] = {}; }; class abcd { units[] = {}; }; };");
        var packed = LzssEncoder.Encode(plain);
        Assert.True(packed.Length < plain.Length + 4); // it really used back-references
        Assert.Equal(plain, Lzss.Decompress(packed, plain.Length));
    }

    [Fact]
    public void Lzss_fills_a_reference_before_the_start_with_spaces()
    {
        // flags 0b10 → item 0 is a reference (distance 2 from position 0, length 3), item 1 a literal 'x'; then the sum.
        byte[] packed = [0b0000_0010, 0x02, 0x00, (byte)'x', .. BitConverter.GetBytes((uint)(' ' * 3 + 'x'))];
        Assert.Equal("   x"u8.ToArray(), Lzss.Decompress(packed, 4));
    }

    [Fact]
    public void Lzss_refuses_bad_input()
    {
        var plain = Encoding.ASCII.GetBytes("abcabcabcabc");
        Assert.Throws<PboException>(() => Lzss.Decompress(LzssEncoder.Encode(plain, badChecksum: true), plain.Length));
        Assert.Throws<PboException>(() => Lzss.Decompress(LzssEncoder.Encode(plain)[..5], plain.Length));
        Assert.Throws<PboException>(() => Lzss.Decompress(LzssEncoder.Encode(plain), 0));
        Assert.Throws<PboException>(() => Lzss.Decompress(LzssEncoder.Encode(plain), 65 * 1024 * 1024));
    }
}
