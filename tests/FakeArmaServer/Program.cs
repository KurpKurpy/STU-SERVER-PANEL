// Stand-in for arma3server_x64.exe in tests: same argument names, an RPT shaped like the real one
// (untimestamped header, then " H:MM:SS text" lines, CRLF, no BOM), and an A2S_INFO answer on port + 1.
// Behaviour comes from server.cfg lines: hostname, fake_noQuery, fake_challenge, fake_exitAfterMs.
// No window, so a graceful stop (CloseMainWindow) falls through to the kill.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

string Arg(string name) =>
    args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..] ?? "";

var port = int.Parse(Arg("-port"));

// Headless client mode (-client): no ports of its own, just an RPT in its -profiles folder.
if (args.Contains("-client", StringComparer.OrdinalIgnoreCase))
{
    var hcDir = Arg("-profiles");
    Directory.CreateDirectory(hcDir);
    using var hcRpt = new StreamWriter(new FileStream(Path.Combine(hcDir, $"arma3server_x64_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.rpt"), FileMode.Create,
        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
    hcRpt.WriteLine("ARGS: " + string.Join(" | ", args));
    while (true)
    {
        hcRpt.WriteLine($"{DateTime.Now,8:H:mm:ss} Fake HC connecting to {Arg("-connect")}:{port}");
        await Task.Delay(200);
    }
}

var cfgPath = Arg("-config");
var cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
string Cfg(string key) => Regex.Match(cfg, $@"\b{key}\s*=\s*""?([^"";]*)""?\s*;", RegexOptions.IgnoreCase).Groups[1].Value;

var hostname = Cfg("hostname");
var noQuery = Cfg("fake_noQuery") == "1";
var challenge = Cfg("fake_challenge") == "1";
_ = int.TryParse(Cfg("fake_exitAfterMs"), out var exitAfterMs);

var profiles = Arg("-profiles");
Directory.CreateDirectory(profiles);
var rptPath = Path.Combine(profiles, $"arma3server_x64_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.rpt");
var rpt = new StreamWriter(new FileStream(rptPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
rpt.WriteLine("=====================================================================");
rpt.WriteLine("== fake arma3server_x64.exe");
rpt.WriteLine("ARGS: " + string.Join(" | ", args));
rpt.WriteLine("=====================================================================");
rpt.WriteLine();

using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
using var query = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));

// BattlEye RCon, as the Phase 7a spike found it on BattlEye 1.221: BEServer_x64.cfg in <profiles>\BattlEye is renamed to an
// "_active_" copy while running and back on a clean exit; login, commands (multi-packet not needed here), server messages;
// "#shutdown" exits cleanly with code 0. fake_rconPassword overrides the password (a refused login); fake_ignoreShutdown=1 ignores it.
var beDir = Path.Combine(profiles, "BattlEye");
var beCfg = Path.Combine(beDir, "BEServer_x64.cfg");
string? activeCfg = null;
if (File.Exists(beCfg))
{
    var be = File.ReadAllLines(beCfg).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2)
        .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
    activeCfg = Path.Combine(beDir, $"BEServer_x64_active_{Random.Shared.Next(0x1000, 0xFFFF):x}.cfg");
    File.Move(beCfg, activeCfg);
    var rconPassword = Cfg("fake_rconPassword") is { Length: > 0 } other ? other : be["RConPassword"];
    var rcon = new UdpClient(new IPEndPoint(IPAddress.Parse(be.GetValueOrDefault("RConIP", "127.0.0.1")), int.Parse(be["RConPort"])));
    _ = Task.Run(() => AnswerRcon(rcon, rconPassword, Cfg("fake_ignoreShutdown") == "1"));
}

void ExitCleanly()
{
    rpt.WriteLine($"{DateTime.Now,8:H:mm:ss} Shutting down (#shutdown)");
    if (activeCfg is not null && File.Exists(activeCfg)) File.Move(activeCfg, beCfg);
    Environment.Exit(0);
}

var exitCode = int.TryParse(Cfg("fake_exitCode"), out var code) ? code : 3;
if (exitAfterMs > 0)
    _ = Task.Delay(exitAfterMs).ContinueWith(_ => { rpt.WriteLine($"{DateTime.Now,8:H:mm:ss} Exiting with code {exitCode}"); Environment.Exit(exitCode); });

if (!noQuery) _ = Task.Run(AnswerQueries);

for (var n = 1; ; n++)
{
    rpt.WriteLine($"{DateTime.Now,8:H:mm:ss} Fake line {n}");
    await Task.Delay(200);
}

// Written apart from the agent's BattlEyePacket on purpose, so the two can't share a mistake: CRC-32 (zlib), little-endian, over 0xFF + payload.
static uint Crc(byte[] data)
{
    var c = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        c ^= b;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
    }
    return ~c;
}

static byte[] BePacket(params byte[] payload)
{
    var body = new byte[] { 0xFF }.Concat(payload).ToArray();
    var crc = Crc(body);
    return [(byte)'B', (byte)'E', (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24), .. body];
}

async Task AnswerRcon(UdpClient rcon, string password, bool ignoreShutdown)
{
    byte serverSeq = 0;
    while (true)
    {
        UdpReceiveResult r;
        try { r = await rcon.ReceiveAsync(); } catch (SocketException) { continue; }
        var d = r.Buffer;
        if (d.Length < 8 || d[0] != 'B' || d[1] != 'E' || d[6] != 0xFF) continue;
        if (BitConverter.ToUInt32(d, 2) != Crc(d[6..])) continue;
        var body = d[8..];
        Task Send(byte[] p) => rcon.SendAsync(p, p.Length, r.RemoteEndPoint);
        switch (d[7])
        {
            case 0x00:
                var ok = Encoding.ASCII.GetString(body) == password;
                await Send(BePacket(0x00, (byte)(ok ? 1 : 0)));
                if (ok) await Send(BePacket([0x02, serverSeq++, .. Encoding.ASCII.GetBytes($"RCon admin #0 ({r.RemoteEndPoint}) logged in")]));
                break;
            case 0x01:
                var seq = body[0];
                var command = Encoding.ASCII.GetString(body, 1, body.Length - 1);
                var reply = command == "players"
                    ? "Players on server:\n[#] [IP Address]:[Port] [Ping] [GUID] [Name]\n--------------------------------------------------\n(0 players in total)"
                    : "";
                await Send(BePacket([0x01, seq, .. Encoding.ASCII.GetBytes(reply)]));
                if (command == "#shutdown" && !ignoreShutdown) _ = Task.Delay(100).ContinueWith(_ => ExitCleanly());
                break;
        }
    }
}

async Task AnswerQueries()
{
    byte[] header = [0xFF, 0xFF, 0xFF, 0xFF];
    byte[] token = [0x0A, 0x0B, 0x0C, 0x0D];
    var prefix = header.Concat("TSource Engine Query\0"u8.ToArray()).ToArray();
    while (true)
    {
        UdpReceiveResult r;
        try { r = await query.ReceiveAsync(); } catch (SocketException) { continue; }
        if (!r.Buffer.AsSpan().StartsWith(prefix)) continue;
        if (challenge && !r.Buffer.AsSpan(prefix.Length).SequenceEqual(token))
        {
            byte[] ask = [.. header, 0x41, .. token];
            await query.SendAsync(ask, ask.Length, r.RemoteEndPoint);
            continue;
        }
        var info = new List<byte>(header) { 0x49, 0x11 };
        foreach (var s in new[] { hostname, "Altis", "Arma3", "fake_mission" }) info.AddRange(Encoding.UTF8.GetBytes(s + "\0"));
        info.AddRange([0x00, 0x00, 0, 4, 0, (byte)'d', (byte)'w', 0, 0]); // id, players, max, bots, type, env, visibility, vac
        info.AddRange("2.22.0\0"u8.ToArray());
        info.Add(0x00);
        await query.SendAsync(info.ToArray(), info.Count, r.RemoteEndPoint);
    }
}
