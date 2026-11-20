using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerAgent.Rcon;

// One BattlEye RCon session over UDP (Phase 7a Ruling 2). Commands go one at a time with a wrapping 0–255 sequence number; split replies
// are put back together by index; server messages are acknowledged (BattlEye resends five times, then drops the session) and raised once;
// an empty command keeps the session alive (BattlEye drops one idle for 45 s). Commands are never resent: a resent kick or ban would act twice.
// Final review I1: the keep-alive goes out every period, busy or not (acks don't count), and waits for its reply like any command; one
// that goes unanswered completes Lost, so a dropped session stops reading Connected.
public sealed class RconClient : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly TimeSpan _keepAlive, _replyTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<byte, Reply> _pending = new();
    private readonly Task _receive;
    private Task? _keep;
    private TaskCompletionSource<bool>? _login;
    private int _seq, _lastMessage = -1;

    public event Action<string>? Message;
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Lost => _lost.Task;

    public RconClient(IPEndPoint server, TimeSpan? keepAlive = null, TimeSpan? replyTimeout = null)
    {
        _keepAlive = keepAlive ?? TimeSpan.FromSeconds(30);
        _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(8);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Connect(server);
        _receive = Task.Run(ReceiveAsync);
    }

    // True when BattlEye accepted the password, false when it refused it; a TimeoutException when nothing answered.
    public async Task<bool> LoginAsync(string password, CancellationToken ct)
    {
        _login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(BattlEyePacket.Login(password));
        bool ok;
        try { ok = await _login.Task.WaitAsync(_replyTimeout, ct); }
        catch (TimeoutException) { throw new TimeoutException("BattlEye RCon didn't answer: it isn't running on that port, or BattlEye is off."); }
        if (ok) _keep ??= Task.Run(KeepAliveAsync);
        return ok;
    }

    public async Task<string> CommandAsync(string command, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try
        {
            var seq = NextSeq();
            var reply = new Reply();
            _pending[seq] = reply;
            try
            {
                await SendAsync(BattlEyePacket.Command(seq, command));
                return await reply.Task.WaitAsync(_replyTimeout, ct);
            }
            catch (TimeoutException) { throw new TimeoutException("BattlEye didn't answer in time."); }
            finally { _pending.TryRemove(seq, out _); }
        }
        finally { _send.Release(); }
    }

    private byte NextSeq() => (byte)(_seq++ & 0xFF);

    private async Task SendAsync(byte[] packet) => await _udp.SendAsync(packet, _stop.Token);

    private async Task ReceiveAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. "port unreachable" (ICMP) while nothing listens yet
            if (!BattlEyePacket.TryParse(r.Buffer, out var type, out var body)) continue;
            switch (type)
            {
                case 0x00:
                    _login?.TrySetResult(body.Length > 0 && body[0] == 0x01);
                    break;
                case 0x01 when body.Length > 0:
                    if (_pending.TryGetValue(body[0], out var reply)) reply.Add(body.AsSpan(1));
                    break;
                case 0x02 when body.Length > 0:
                    try { await SendAsync(BattlEyePacket.Ack(body[0])); } catch (Exception) when (_stop.IsCancellationRequested) { return; }
                    if (Interlocked.Exchange(ref _lastMessage, body[0]) == body[0]) break; // a resend of one already raised
                    try { Message?.Invoke(Encoding.UTF8.GetString(body, 1, body.Length - 1)); } catch (Exception) { }
                    break;
            }
        }
    }

    private async Task KeepAliveAsync()
    {
        using var timer = new PeriodicTimer(_keepAlive);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token)) await CommandAsync("", _stop.Token);
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception) { _lost.TrySetResult(); } // unanswered (TimeoutException) or the socket failed
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _udp.Dispose();
        try { await _receive; } catch (Exception) { }
        if (_keep is { } k) try { await k; } catch (Exception) { }
    }

    // One command's reply: a single packet, or "0x00 | count | index" parts put back together in index order.
    private sealed class Reply
    {
        private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, byte[]> _parts = [];
        private const int MaxBytes = 256 * 1024; // keeps a reply well inside the hub's 1 MiB message (Ruling 5)

        public Task<string> Task => _done.Task;

        public void Add(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 3 && data[0] == 0x00)
            {
                int total = data[1], index = data[2];
                lock (_parts)
                {
                    if (total == 0 || index >= total) return;
                    _parts[index] = data[3..].ToArray();
                    if (_parts.Count < total) return;
                    var all = Enumerable.Range(0, total).SelectMany(i => _parts[i]).Take(MaxBytes).ToArray();
                    _done.TrySetResult(Encoding.UTF8.GetString(all));
                }
            }
            else _done.TrySetResult(Encoding.UTF8.GetString(data.Length > MaxBytes ? data[..MaxBytes] : data));
        }
    }
}
