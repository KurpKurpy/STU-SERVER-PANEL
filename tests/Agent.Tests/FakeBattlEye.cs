using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ServerAgent.Rcon;

namespace Agent.Tests;

// An in-process BattlEye RCon server on 127.0.0.1, written to the Phase 7a spike's notes (BattlEye 1.221 on the dev PC).
public sealed class FakeBattlEye : IAsyncDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private IPEndPoint? _client;
    private byte _messageSeq;

    public string Password { get; set; } = "secret123";
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);
    public bool Silent { get; set; }                                   // answers nothing (RCon off, or a dead server)
    public Func<string, string[]> Reply { get; set; } = _ => [""];      // one string per packet; more than one = a split reply
    public ConcurrentQueue<string> Commands { get; } = new();          // every command received ("" = keep-alive)
    public ConcurrentQueue<byte> Acks { get; } = new();                // server-message sequence numbers acknowledged
    public ConcurrentQueue<byte[]> Raw { get; } = new();               // every datagram received, as sent

    public FakeBattlEye() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); } catch (OperationCanceledException) { return; } catch (SocketException) { continue; }
            Raw.Enqueue(r.Buffer);
            if (Silent || !BattlEyePacket.TryParse(r.Buffer, out var type, out var body)) continue;
            _client = r.RemoteEndPoint;
            switch (type)
            {
                case 0x00:
                    await SendAsync([0x00, (byte)(Encoding.ASCII.GetString(body) == Password ? 0x01 : 0x00)]);
                    break;
                case 0x01:
                    var seq = body[0];
                    var command = Encoding.ASCII.GetString(body, 1, body.Length - 1);
                    Commands.Enqueue(command);
                    var parts = command.Length == 0 ? [""] : Reply(command);
                    if (parts.Length == 0) break;                      // no reply at all (a lost packet, or a hung server)
                    if (parts.Length == 1) await SendAsync([0x01, seq, .. Encoding.ASCII.GetBytes(parts[0])]);
                    else for (var i = parts.Length - 1; i >= 0; i--)  // out of order on purpose: the client reassembles by index
                            await SendAsync([0x01, seq, 0x00, (byte)parts.Length, (byte)i, .. Encoding.ASCII.GetBytes(parts[i])]);
                    break;
                case 0x02:
                    Acks.Enqueue(body[0]);
                    break;
            }
        }
    }

    // A server message, as BattlEye pushes them ("RCon admin #1 ... logged in", chat, connects).
    public Task PushAsync(string message) => SendAsync([0x02, _messageSeq++, .. Encoding.ASCII.GetBytes(message)]);

    private Task SendAsync(byte[] payload) =>
        _client is null ? Task.CompletedTask : _udp.SendAsync(BattlEyePacket.Build(payload), _client).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        try { await _loop; } catch (Exception) { }
    }
}
