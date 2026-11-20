namespace ServerAgent.Rcon;

// Phase 7a Ruling 2, from BattlEye's protocol document and the spike against BattlEye 1.221 on the dev PC:
// "BE" | CRC32 (little-endian) over 0xFF + payload | 0xFF | payload. Payload: 0x00 login, 0x01 command (seq), 0x02 server message (seq).
public static class BattlEyePacket
{
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    // The zlib/PNG CRC-32.
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public static byte[] Build(ReadOnlySpan<byte> payload)
    {
        var p = new byte[7 + payload.Length];
        p[0] = (byte)'B';
        p[1] = (byte)'E';
        p[6] = 0xFF;
        payload.CopyTo(p.AsSpan(7));
        BitConverter.TryWriteBytes(p.AsSpan(2, 4), Crc32(p.AsSpan(6)));
        if (!BitConverter.IsLittleEndian) p.AsSpan(2, 4).Reverse();
        return p;
    }

    public static byte[] Login(string password) => Build([0x00, .. Ascii(password)]);
    public static byte[] Command(byte seq, string command) => Build([0x01, seq, .. Ascii(command)]);
    public static byte[] Ack(byte seq) => Build([0x02, seq]);

    // Type and the bytes after it; false for anything that isn't a well-formed packet with a matching checksum.
    public static bool TryParse(ReadOnlySpan<byte> data, out byte type, out byte[] body)
    {
        type = 0;
        body = [];
        if (data.Length < 8 || data[0] != 'B' || data[1] != 'E' || data[6] != 0xFF) return false;
        var crc = (uint)(data[2] | data[3] << 8 | data[4] << 16 | data[5] << 24);
        if (crc != Crc32(data[6..])) return false;
        type = data[7];
        body = data[8..].ToArray();
        return true;
    }

    // BattlEye takes ASCII; the spike couldn't confirm how it shows anything else, so other characters go as '?' (Ruling 2).
    public static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++) b[i] = s[i] switch { < ' ' => (byte)' ', > '~' => (byte)'?', var c => (byte)c };
        return b;
    }
}
