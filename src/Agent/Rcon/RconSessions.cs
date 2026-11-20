using System.Collections.Concurrent;
using System.Net;
using Contracts;

namespace ServerAgent.Rcon;

// Phase 7a Ruling 5: one RCon session per running server, opened once it answers A2S. Its state (Off, Connecting, Connected, Failed with
// a reason) rides on the server's status. A refused or unanswered login is retried every RconRetrySeconds; a command that times out drops
// the session so the next attempt logs in again. Never throws to the caller.
public sealed class RconSessions(TimeSpan retry, ILogger log, TimeSpan? keepAlive = null, TimeSpan? replyTimeout = null) : IAsyncDisposable
{
    private sealed class Session(IPEndPoint endPoint, string password)
    {
        public IPEndPoint EndPoint { get; } = endPoint;
        public string Password { get; } = password;
        public CancellationTokenSource Cts { get; } = new();
        public RconClient? Client;
        public RconState State = RconState.Connecting;
        public string? Error;
        public TaskCompletionSource Lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public event Action<Guid>? StateChanged;
    public event Action<Guid, string>? Message;

    public (RconState State, string? Error) StateOf(Guid profileId) =>
        _sessions.TryGetValue(profileId, out var s) ? (s.State, s.Error) : (RconState.Off, null);

    public bool IsOpen(Guid profileId) => _sessions.ContainsKey(profileId);

    public void Open(Guid profileId, RconSpec spec)
    {
        var s = new Session(new IPEndPoint(IPAddress.Loopback, spec.Port), spec.Password);
        if (!_sessions.TryAdd(profileId, s)) return;
        _ = RunAsync(profileId, s);
    }

    public async Task CloseAsync(Guid profileId)
    {
        if (!_sessions.TryRemove(profileId, out var s)) return;
        s.Cts.Cancel();
        if (s.Client is { } c) await c.DisposeAsync();
        Raise(profileId);
    }

    public async Task<RconReply> CommandAsync(Guid profileId, string command, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(profileId, out var s)) return new(false, null, "RCon isn't set up for this server, or it isn't running.");
        if (s.State != RconState.Connected || s.Client is not { } client) return new(false, null, s.Error ?? "RCon isn't connected yet.");
        try { return new(true, await client.CommandAsync(command, ct), null); }
        catch (TimeoutException)
        {
            s.Lost.TrySetResult(); // log in again
            return new(false, null, "BattlEye didn't answer in time.");
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return new(false, null, "RCon was disconnected.");
        }
    }

    private async Task RunAsync(Guid id, Session s)
    {
        while (!s.Cts.IsCancellationRequested)
        {
            Set(id, s, RconState.Connecting, s.Error);
            var client = new RconClient(s.EndPoint, keepAlive, replyTimeout);
            var connected = false;
            try
            {
                if (await client.LoginAsync(s.Password, s.Cts.Token))
                {
                    connected = true;
                    client.Message += text => { try { Message?.Invoke(id, text); } catch (Exception) { } };
                    s.Client = client;
                    s.Lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Set(id, s, RconState.Connected, null);
                    await Task.WhenAny(s.Lost.Task, client.Lost).WaitAsync(s.Cts.Token); // a command or the keep-alive went unanswered
                    s.Client = null;
                    Set(id, s, RconState.Connecting, "The RCon session was lost; logging in again.");
                }
                else Set(id, s, RconState.Failed, "BattlEye refused the RCon password.");
            }
            catch (TimeoutException) { Set(id, s, RconState.Failed, "BattlEye RCon isn't answering yet."); }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "RCon session error"); Set(id, s, RconState.Failed, "RCon failed; retrying."); }
            await client.DisposeAsync();
            if (s.Cts.IsCancellationRequested) return;
            if (connected) continue; // log straight back in after a lost session
            try { await Task.Delay(retry, s.Cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Set(Guid id, Session s, RconState state, string? error)
    {
        if (s.Cts.IsCancellationRequested || (s.State == state && s.Error == error)) return;
        (s.State, s.Error) = (state, error);
        Raise(id);
    }

    private void Raise(Guid id)
    {
        try { StateChanged?.Invoke(id); } catch (Exception e) { log.LogWarning(e, "An RCon state listener failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await CloseAsync(id);
    }
}
