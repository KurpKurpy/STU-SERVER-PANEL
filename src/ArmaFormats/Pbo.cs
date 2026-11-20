using System.Security.Cryptography;
using System.Text;

namespace ArmaFormats;

// Path uses forward slashes ("scripts/init.sqf"); on disk PBOs use backslashes.
public sealed record PboEntry(string Path, byte[] Data, uint Timestamp);
public sealed record PboFile(IReadOnlyDictionary<string, string> Properties, IReadOnlyList<PboEntry> Entries);
public sealed class PboException(string message) : Exception(message);
public sealed record PboHeaderEntry(string Path, uint Packing, uint OriginalSize, uint DataSize, long Offset);
public sealed record PboHeader(IReadOnlyDictionary<string, string> Properties, IReadOnlyList<PboHeaderEntry> Entries, long DataStart);

// Arma's mission archive (design §3): a "sreV" product entry with header properties, entry headers, an empty terminator, the data in
// entry order, then 0x00 and a SHA1 of everything before it. Only uncompressed entries — every one of 1,774 real entries was (Phase 4 Ruling 2).
public static class Pbo
{
    public const int MaxEntries = 10_000;
    public const long MaxEntryBytes = 200L * 1024 * 1024;
    private const uint Vers = 0x56657273; // "sreV"
    private const uint Cprs = 0x43707273; // "srpC"

    public static PboFile Read(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var r = new Reader(stream, hash);
        var properties = new Dictionary<string, string>();
        var headers = new List<(string Path, uint Method, uint Original, uint Timestamp, uint Size)>();
        var first = true;
        while (true)
        {
            var name = r.CString();
            var (method, original, _, timestamp, size) = (r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
            if (first && name.Length == 0 && method == Vers)
            {
                first = false;
                while (r.CString() is { Length: > 0 } key) properties[key] = r.CString();
                continue;
            }
            first = false;
            if (name.Length == 0) break;
            if (headers.Count >= MaxEntries) throw new PboException($"The PBO has more than {MaxEntries} files.");
            var path = MissionPath.Normalize(name);
            if (MissionPath.Problem(path) is { } problem) throw new PboException($"The PBO has an unsafe file name: {problem}");
            if (method == Cprs || method != 0 || (original != 0 && original != size))
                throw new PboException($"This PBO uses compressed entries ({path}); export it from Eden again or upload the mission folder.");
            if (size > MaxEntryBytes) throw new PboException($"{path} is larger than {MaxEntryBytes / 1024 / 1024} MB.");
            headers.Add((path, method, original, timestamp, size));
        }
        if (MissionPath.DuplicateProblem(headers.Select(h => h.Path)) is { } duplicate) throw new PboException(duplicate);

        var entries = headers.Select(h => new PboEntry(h.Path, r.Bytes((int)h.Size), h.Timestamp)).ToList();
        var digest = hash.GetHashAndReset();
        var trailer = r.Rest(22);
        if (trailer.Length == 0) return new PboFile(properties, entries); // an older PBO without a checksum
        if (trailer.Length != 21 || trailer[0] != 0 || !trailer.AsSpan(1).SequenceEqual(digest))
            throw new PboException("The PBO's checksum doesn't match its contents: the file is damaged or incomplete.");
        return new PboFile(properties, entries);
    }

    // Scanning mods and installs (Phase 5b): headers only, from a seekable stream. Names aren't held to mission rules — nothing is
    // written from them — but counts and sizes are capped, and data is read only for the entries asked for.
    public const int MaxScanEntries = 100_000;
    public const int MaxScanProperties = 1_000;

    public static PboHeader ReadHeader(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); // the reader feeds one; scanning doesn't check it
        var r = new Reader(stream, hash);
        var properties = new Dictionary<string, string>();
        var entries = new List<PboHeaderEntry>();
        long offset = 0;
        var first = true;
        while (true)
        {
            var name = r.CString();
            var (packing, original, _, _, size) = (r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
            if (first && name.Length == 0 && packing == Vers)
            {
                first = false;
                // Protected mod PBOs pad this with junk (three on K: have over 1,000); keep the first ones, read past the rest.
                while (r.CString() is { Length: > 0 } key)
                {
                    var value = r.CString();
                    if (properties.Count < MaxScanProperties) properties[key] = value;
                }
                continue;
            }
            first = false;
            if (name.Length == 0) break;
            if (entries.Count >= MaxScanEntries) throw new PboException($"The PBO has more than {MaxScanEntries} files.");
            entries.Add(new PboHeaderEntry(name.Replace('\\', '/'), packing, original, size, offset));
            offset += size;
        }
        return new PboHeader(properties, entries, stream.Position);
    }

    // One entry's data, decompressed when the header marks it compressed; never allocated from a header's word alone.
    public static byte[] ReadEntry(Stream stream, PboHeader header, PboHeaderEntry entry)
    {
        if (entry.DataSize > Lzss.MaxLength) throw new PboException($"{entry.Path} is larger than {Lzss.MaxLength / 1024 / 1024} MB.");
        var at = header.DataStart + entry.Offset;
        if (at + entry.DataSize > stream.Length) throw new PboException("The PBO ends early: the file is incomplete.");
        stream.Position = at;
        var data = new byte[entry.DataSize];
        stream.ReadExactly(data);
        var compressed = entry.Packing == Cprs || (entry.OriginalSize != 0 && entry.OriginalSize != entry.DataSize);
        return compressed ? Lzss.Decompress(data, (int)Math.Min(entry.OriginalSize, int.MaxValue)) : data;
    }

    public static void Write(PboFile pbo, Stream stream)
    {
        foreach (var e in pbo.Entries)
            if (MissionPath.Problem(e.Path) is { } problem) throw new PboException(problem);
        if (MissionPath.DuplicateProblem(pbo.Entries.Select(e => e.Path)) is { } duplicate) throw new PboException(duplicate);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        void Put(ReadOnlySpan<byte> bytes) { hash.AppendData(bytes); stream.Write(bytes); }
        void Str(string s) { Put(Encoding.UTF8.GetBytes(s)); Put([0]); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v); Put(b); }
        void Header(string name, uint method, uint timestamp, uint size) { Str(name); U32(method); U32(0); U32(0); U32(timestamp); U32(size); }

        Header("", Vers, 0, 0);
        foreach (var (key, value) in pbo.Properties) { Str(key); Str(value); }
        Put([0]);
        foreach (var e in pbo.Entries) Header(e.Path.Replace('/', '\\'), 0, e.Timestamp, (uint)e.Data.Length);
        Header("", 0, 0, 0);
        foreach (var e in pbo.Entries) Put(e.Data);
        stream.WriteByte(0);
        stream.Write(hash.GetHashAndReset());
    }

    // Bounds-checked reads that feed the checksum; running out of bytes is a PboException, never EndOfStream.
    private sealed class Reader(Stream s, IncrementalHash hash)
    {
        private readonly byte[] _one = new byte[1];

        public byte[] Bytes(int count)
        {
            var buffer = new byte[count];
            var read = s.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            if (read < count) throw new PboException("The PBO ends early: the file is incomplete.");
            hash.AppendData(buffer);
            return buffer;
        }

        public uint U32() => BitConverter.ToUInt32(Bytes(4));

        public string CString()
        {
            var bytes = new List<byte>();
            while (true)
            {
                if (s.Read(_one, 0, 1) == 0) throw new PboException("The PBO ends early: the file is incomplete.");
                hash.AppendData(_one);
                if (_one[0] == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                if (bytes.Count >= 1024) throw new PboException("The PBO has a file name longer than 1,024 bytes.");
                bytes.Add(_one[0]);
            }
        }

        // What's left (the trailer), not hashed; at most max bytes are read.
        public byte[] Rest(int max)
        {
            var buffer = new byte[max];
            var read = s.ReadAtLeast(buffer, max, throwOnEndOfStream: false);
            return buffer[..read];
        }
    }
}
