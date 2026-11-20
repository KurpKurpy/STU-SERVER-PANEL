using System.Text;
using ServerAgent.Rcon;

namespace Agent.Tests;

// Phase 7a Task 1: the BattlEye RCon wire format and client, to the spike's findings (plan Ruling 2).
public sealed class RconClientTests
{
    [Fact]
    public void The_checksum_is_zlib_crc32()
    {
        Assert.Equal(0xCBF43926u, BattlEyePacket.Crc32(Encoding.ASCII.GetBytes("123456789"))); // the standard CRC-32 check value
    }

    [Fact]
    public void Packets_carry_a_little_endian_crc_over_0xFF_and_the_payload()
    {
        var p = BattlEyePacket.Login("pw");
        Assert.Equal("BE"u8.ToArray(), p[..2]);
        Assert.Equal(0xFF, p[6]);
        Assert.Equal(new byte[] { 0x00, (byte)'p', (byte)'w' }, p[7..]);
        Assert.Equal(BitConverter.GetBytes(BattlEyePacket.Crc32([0xFF, 0x00, (byte)'p', (byte)'w'])), p[2..6]); // BitConverter is little-endian on x64

        Assert.True(BattlEyePacket.TryParse(p, out var type, out var body));
        Assert.Equal((byte)0x00, type);
        Assert.Equal("pw"u8.ToArray(), body);

        var bad = (byte[])p.Clone();
        bad[2] ^= 0xFF;
        Assert.False(BattlEyePacket.TryParse(bad, out _, out _));
        Assert.False(BattlEyePacket.TryParse("BE"u8.ToArray(), out _, out _));
    }

    [Fact]
    public void Commands_carry_their_sequence_and_send_non_ascii_as_question_marks()
    {
        var p = BattlEyePacket.Command(7, "say -1 Grüße");
        Assert.True(BattlEyePacket.TryParse(p, out var type, out var body));
        Assert.Equal((byte)0x01, type);
        Assert.Equal(7, body[0]);
        Assert.Equal("say -1 Gr??e", Encoding.ASCII.GetString(body, 1, body.Length - 1));
    }

    [Fact]
    public async Task Login_ok_and_refused()
    {
        await using var be = new FakeBattlEye();
        await using (var ok = new RconClient(be.EndPoint))
            Assert.True(await ok.LoginAsync("secret123", CancellationToken.None));
        await using var no = new RconClient(be.EndPoint);
        Assert.False(await no.LoginAsync("wrong", CancellationToken.None));
    }

    [Fact]
    public async Task No_reply_to_login_is_a_timeout()
    {
        await using var be = new FakeBattlEye { Silent = true };
        await using var c = new RconClient(be.EndPoint, replyTimeout: TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<TimeoutException>(() => c.LoginAsync("secret123", CancellationToken.None));
    }

    [Fact]
    public async Task A_command_returns_its_reply_and_a_split_reply_is_reassembled_in_index_order()
    {
        await using var be = new FakeBattlEye { Reply = c => c == "bans" ? ["GUID Bans:\n", "[#] [GUID]\n", "IP Bans:"] : ["1.221"] };
        await using var c = new RconClient(be.EndPoint);
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));

        Assert.Equal("1.221", await c.CommandAsync("version", CancellationToken.None));
        Assert.Equal("GUID Bans:\n[#] [GUID]\nIP Bans:", await c.CommandAsync("bans", CancellationToken.None));
        Assert.Equal(["version", "bans"], be.Commands);
    }

    [Fact]
    public async Task A_command_with_no_reply_times_out()
    {
        await using var be = new FakeBattlEye { Reply = _ => [] };
        await using var c = new RconClient(be.EndPoint, replyTimeout: TimeSpan.FromMilliseconds(400));
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() => c.CommandAsync("players", CancellationToken.None));
    }

    [Fact]
    public async Task Server_messages_are_acknowledged_and_raised()
    {
        await using var be = new FakeBattlEye();
        await using var c = new RconClient(be.EndPoint);
        var got = new List<string>();
        c.Message += m => { lock (got) got.Add(m); };
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));

        await be.PushAsync("RCon admin #1 (127.0.0.1:55247) logged in");
        await be.PushAsync("RCon admin #1: (Global) hello");
        await WaitAsync(() => be.Acks.Count == 2);

        Assert.Equal([0, 1], be.Acks.Select(a => (int)a));
        lock (got) Assert.Equal(["RCon admin #1 (127.0.0.1:55247) logged in", "RCon admin #1: (Global) hello"], got);
    }

    [Fact]
    public async Task Keepalive_is_sent_while_idle()
    {
        await using var be = new FakeBattlEye();
        await using var c = new RconClient(be.EndPoint, keepAlive: TimeSpan.FromMilliseconds(150));
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));
        await WaitAsync(() => be.Commands.Count(x => x == "") >= 2);
    }

    // Final review I1: acks used to reset the keep-alive timer, so a busy server (chat, joins) never got one.
    [Fact]
    public async Task Keepalive_is_sent_even_while_server_messages_keep_arriving()
    {
        await using var be = new FakeBattlEye();
        await using var c = new RconClient(be.EndPoint, keepAlive: TimeSpan.FromMilliseconds(200));
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));
        using var busy = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var chatter = Task.Run(async () => { while (!busy.IsCancellationRequested) { await be.PushAsync("RCon admin #1: (Global) hi"); await Task.Delay(40); } });
        await WaitAsync(() => be.Commands.Count(x => x == "") >= 2, seconds: 2);
        busy.Cancel();
        await chatter;
    }

    // Final review I1: a keep-alive nobody answers means BattlEye dropped the session; say so instead of reading Connected.
    [Fact]
    public async Task A_keepalive_without_a_reply_marks_the_client_lost()
    {
        await using var be = new FakeBattlEye();
        await using var c = new RconClient(be.EndPoint, keepAlive: TimeSpan.FromMilliseconds(150), replyTimeout: TimeSpan.FromMilliseconds(300));
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));
        await Task.Delay(400);
        Assert.False(c.Lost.IsCompleted); // answered keep-alives keep it alive
        be.Silent = true;
        await c.Lost.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_session_whose_server_stops_answering_leaves_connected()
    {
        await using var be = new FakeBattlEye();
        await using var sessions = new RconSessions(TimeSpan.FromSeconds(1), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            keepAlive: TimeSpan.FromMilliseconds(150), replyTimeout: TimeSpan.FromMilliseconds(300));
        var id = Guid.NewGuid();
        sessions.Open(id, new Contracts.RconSpec(be.Port, "secret123"));
        await WaitAsync(() => sessions.StateOf(id).State == Contracts.RconState.Connected);
        be.Silent = true;
        await WaitAsync(() => sessions.StateOf(id).State != Contracts.RconState.Connected, seconds: 3);
    }

    [Fact]
    public async Task Sequence_numbers_wrap_after_255()
    {
        await using var be = new FakeBattlEye { Reply = c => [c] };
        await using var c = new RconClient(be.EndPoint);
        Assert.True(await c.LoginAsync("secret123", CancellationToken.None));
        for (var i = 0; i < 260; i++) Assert.Equal($"c{i}", await c.CommandAsync($"c{i}", CancellationToken.None));
    }

    private static async Task WaitAsync(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }
}
