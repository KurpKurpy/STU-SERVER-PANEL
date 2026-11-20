using System.Security.Cryptography;
using System.Text;

namespace ArmaFormats.Tests;

public class PboTests
{
    private static PboFile Sample(Dictionary<string, string>? properties = null) => new(properties ?? [],
    [
        new PboEntry("mission.sqm", "version=54;"u8.ToArray(), 1_700_000_000),
        new PboEntry("scripts/init.sqf", "hint \"hi\";"u8.ToArray(), 1_700_000_001),
        new PboEntry("description.ext", "onLoadName = \"Op\";"u8.ToArray(), 1_700_000_002),
    ]);

    private static byte[] Written(PboFile pbo)
    {
        using var ms = new MemoryStream();
        Pbo.Write(pbo, ms);
        return ms.ToArray();
    }

    // A PBO built byte by byte, for inputs Pbo.Write refuses to produce.
    private static byte[] Raw(params (string Name, byte[] Data, uint Method)[] entries)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Header(string name, uint method, uint original, uint size)
        {
            w.Write(Encoding.UTF8.GetBytes(name)); w.Write((byte)0);
            w.Write(method); w.Write(original); w.Write(0u); w.Write(0u); w.Write(size);
        }
        Header("", 0x56657273, 0, 0); w.Write((byte)0); // "sreV", no properties
        foreach (var e in entries) Header(e.Name, e.Method, e.Method == 0 ? 0u : (uint)e.Data.Length * 2, (uint)e.Data.Length);
        Header("", 0, 0, 0);
        foreach (var e in entries) w.Write(e.Data);
        w.Flush();
        var body = ms.ToArray();
        w.Write((byte)0);
        w.Write(SHA1.HashData(body));
        return ms.ToArray();
    }

    private static PboFile Read(byte[] bytes) => Pbo.Read(new MemoryStream(bytes));

    [Fact]
    public void Pbo_round_trips_entries_properties_and_trailer()
    {
        foreach (var props in new Dictionary<string, string>?[] { null, new() { ["prefix"] = "x\\y" } })
        {
            var bytes = Written(Sample(props));

            var back = Read(bytes);

            Assert.Equal(Sample().Entries.Select(e => (e.Path, Convert.ToHexString(e.Data), e.Timestamp)),
                back.Entries.Select(e => (e.Path, Convert.ToHexString(e.Data), e.Timestamp)));
            Assert.Equal(props ?? [], back.Properties);
            Assert.Equal(SHA1.HashData(bytes.AsSpan(0, bytes.Length - 21)), bytes[^20..]);
            Assert.Equal(0, bytes[^21]);
            Assert.Contains("scripts\\init.sqf", Encoding.UTF8.GetString(bytes)); // Arma's separator on disk
        }
    }

    [Fact]
    public void Compressed_entries_are_refused_with_the_reason()
    {
        var bytes = Raw(("mission.sqm", "x"u8.ToArray(), 0x43707273));

        var e = Assert.Throws<PboException>(() => Read(bytes));

        Assert.Contains("compressed", e.Message);
    }

    [Fact]
    public void A_bad_trailer_is_refused()
    {
        var bytes = Written(Sample());
        bytes[^1] ^= 0xFF;

        Assert.Throws<PboException>(() => Read(bytes));
    }

    [Fact]
    public void A_pbo_without_a_trailer_is_read()
    {
        var bytes = Raw(("mission.sqm", "x"u8.ToArray(), 0));

        Assert.Equal("mission.sqm", Assert.Single(Read(bytes[..^21]).Entries).Path);
    }

    [Theory]
    [InlineData("..\\x.sqf")]
    [InlineData("C:\\x.sqf")]
    [InlineData("a:b.sqf")]
    [InlineData("\\x.sqf")]
    [InlineData("a\u0001.sqf")]
    [InlineData("CON")]
    [InlineData("scripts\\con.txt")]
    [InlineData("x\\.\\y")]
    [InlineData("x\\\\y")]
    [InlineData("trailing.")]
    [InlineData("a<b")]
    public void Hostile_entry_names_are_refused(string name)
    {
        Assert.Throws<PboException>(() => Read(Raw((name, "x"u8.ToArray(), 0))));
        Assert.NotNull(MissionPath.Problem(name.Replace('\\', '/')));
    }

    [Fact]
    public void Case_only_duplicates_are_refused()
    {
        Assert.Throws<PboException>(() => Read(Raw(("A.sqf", "x"u8.ToArray(), 0), ("a.sqf", "y"u8.ToArray(), 0))));
        Assert.Throws<PboException>(() => Written(new PboFile(new Dictionary<string, string>(), [new("A.sqf", [], 0), new("a.SQF", [], 0)])));
    }

    [Fact]
    public void Truncated_pbo_is_refused_not_a_crash()
    {
        var bytes = Written(Sample());

        for (var cut = 1; cut < bytes.Length - 21; cut += 7)
            Assert.Throws<PboException>(() => Read(bytes[..cut]));
    }

    [Fact]
    public void Ordinary_mission_paths_are_fine()
    {
        foreach (var path in new[] { "mission.sqm", "scripts/init.sqf", "MOSTOVIAN%20INTERVENTION/a b.paa", "a.b.c/d" })
            Assert.Null(MissionPath.Problem(path));
    }

    [Fact]
    public void Header_reads_entries_without_data()
    {
        using var ms = new MemoryStream(Written(Sample(new() { ["prefix"] = @"x\y" })));
        var header = Pbo.ReadHeader(ms);
        Assert.Equal(@"x\y", header.Properties["prefix"]);
        Assert.Equal(["mission.sqm", "scripts/init.sqf", "description.ext"], header.Entries.Select(e => e.Path));
        var init = header.Entries[1];
        Assert.Equal("hint \"hi\";"u8.ToArray(), Pbo.ReadEntry(ms, header, init));
        Assert.Equal("version=54;"u8.ToArray(), Pbo.ReadEntry(ms, header, header.Entries[0]));
    }

    [Fact]
    public void Read_entry_decompresses_a_compressed_config()
    {
        var plain = Encoding.ASCII.GetBytes("class CfgPatches { class abc {}; class abcd {}; class abcde {}; };");
        var packed = LzssEncoder.Encode(plain);
        var raw = LzssEncoder.RawPbo(("readme.txt", 0, 0, "hello"u8.ToArray()), (@"sub\config.cpp", 0x43707273, (uint)plain.Length, packed));
        using var ms = new MemoryStream(raw);
        var header = Pbo.ReadHeader(ms);
        Assert.Equal(plain, Pbo.ReadEntry(ms, header, header.Entries.Single(e => e.Path == "sub/config.cpp")));
        Assert.Equal("hello"u8.ToArray(), Pbo.ReadEntry(ms, header, header.Entries[0]));
    }

    // Three protected mod PBOs on K: carry over 1,000 junk header properties; their entries must still be readable.
    [Fact]
    public void Header_keeps_reading_past_many_properties()
    {
        var raw = LzssEncoder.RawPbo(1_500, ("config.cpp", 0u, 0u, "class CfgPatches {};"u8.ToArray()));
        using var ms = new MemoryStream(raw);
        var header = Pbo.ReadHeader(ms);
        Assert.Equal("config.cpp", Assert.Single(header.Entries).Path);
        Assert.True(header.Properties.Count <= Pbo.MaxScanProperties);
        Assert.Equal("class CfgPatches {};"u8.ToArray(), Pbo.ReadEntry(ms, header, header.Entries[0]));
    }

    [Fact]
    public void Header_refuses_hostile_counts()
    {
        var many = Enumerable.Range(0, Pbo.MaxScanEntries + 1).Select(i => ($"f{i}", 0u, 0u, Array.Empty<byte>())).ToArray();
        Assert.Throws<PboException>(() => Pbo.ReadHeader(new MemoryStream(LzssEncoder.RawPbo(many))));
        Assert.Throws<PboException>(() => Pbo.ReadHeader(new MemoryStream(LzssEncoder.RawPbo((new string('a', 1100), 0u, 0u, [1])))));
        var huge = LzssEncoder.RawPbo(("big.bin", 0u, 0u, [1, 2, 3]));
        using var ms = new MemoryStream(huge);
        var header = Pbo.ReadHeader(ms);
        var lying = header.Entries[0] with { DataSize = 70 * 1024 * 1024 };
        Assert.Throws<PboException>(() => Pbo.ReadEntry(ms, header, lying)); // never allocated from a header's word
    }
}
