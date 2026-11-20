using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Contracts;
using ServerAgent.Games;
using ServerAgent.Games.Arma3;
using ServerAgent.Logs;

namespace ServerAgent;

// Launches and watches game servers. Commands return within moments: stop waits run in the background,
// and nothing here ever kills a server because the agent (or panel) stops — Dispose only lets go.
public sealed partial class Supervisor : IDisposable
{
    private sealed class Entry
    {
        public required Guid ProfileId { get; init; }
        public required string Slug { get; init; }
        public required int Port { get; init; }
        public required string InstallRoot { get; init; }
        public required string Executable { get; init; }
        public required Process Process { get; init; }
        public required DateTime StartedAt { get; init; }
        public required IPEndPoint QueryEndpoint { get; init; }
        public required RptTail Tail { get; init; }
        public string? QueryHost { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public ServerState State;
        public QueryInfo? Query;
        public bool EverOnline, Exited, StopRequested;
        public int Misses;
        public LaunchSpec? RestartSpec;
        public LaunchSpec? Spec;                 // what it was started with; null after a re-attach
        public RconSpec? Rcon;                   // Phase 7a: from the spec, or after a re-attach from BattlEye's active cfg
        public readonly List<Hc> Headless = [];  // guarded by lock (this entry)
    }

    // A headless client beside the server: started, stopped and re-attached with it (plan Ruling 13).
    private sealed class Hc
    {
        public required int Index { get; init; }
        public required Process Process { get; init; }
        public required DateTime StartedAt { get; init; }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return true; }
    }

    private static int AliveHeadless(Entry e) => e.Headless.Count(h => !HasExited(h.Process));

    public IReadOnlyList<Process> HeadlessProcesses(Guid profileId)
    {
        if (!_entries.TryGetValue(profileId, out var e)) return [];
        lock (e) return e.Headless.Where(h => !HasExited(h.Process)).Select(h => h.Process).ToList();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    internal static partial Regex SlugPattern();

    private readonly AgentOptions _options;
    private readonly Areas _areas;
    private readonly IGameAdapter _game;
    private readonly ServerAgent.Mods.ModStore _mods;
    private readonly ILogger<Supervisor> _log;
    private readonly string _statePath;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ConcurrentDictionary<Guid, ProfileStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _disposed;

    public event Action<ProfileStatus>? StatusChanged;
    public event Action<Guid, LogLine[]>? LogLinesRead;
    public event Action<Guid, string>? RconMessage;

    // Phase 7a: the BattlEye RCon sessions of the running servers.
    public global::ServerAgent.Rcon.RconSessions Rcon { get; }

    public Task<RconReply> RconAsync(Guid profileId, string command) => Rcon.CommandAsync(profileId, command);

    public Supervisor(AgentOptions options, Areas areas, IGameAdapter game, ILogger<Supervisor> log)
    {
        Rcon = new global::ServerAgent.Rcon.RconSessions(TimeSpan.FromSeconds(Math.Max(1, options.RconRetrySeconds)), log);
        Rcon.StateChanged += id => { if (_statuses.TryGetValue(id, out var current)) Publish(current); };
        Rcon.Message += (id, text) => { try { RconMessage?.Invoke(id, text); } catch (Exception x) { log.LogWarning(x, "An RCon message listener failed"); } };
        _options = options;
        _areas = areas;
        _game = game;
        _mods = new ServerAgent.Mods.ModStore(areas);
        _log = log;
        _statePath = Path.Combine(Path.GetFullPath(options.DataRoot), "agent-state.json");
        _ = LogLoopAsync();
    }

    public IReadOnlyDictionary<Guid, Process> Running => _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Process);

    // Phase 4b Ruling 9: a running server's own profile files are left alone by the file manager.
    public bool IsRunningSlug(string slug) => _entries.Values.Any(e => e.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public ProfileStatus[] Snapshot() => _statuses.Values.ToArray();

    // The lock covers only the check-and-change of a profile's state, never a wait for the process to exit.
    private SemaphoreSlim Gate(Guid profileId) => _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    public async Task<StartResult> StartAsync(LaunchSpec spec)
    {
        var gate = Gate(spec.ProfileId);
        await gate.WaitAsync();
        try { return StartLocked(spec with { CustomArgs = spec.CustomArgs ?? [] }); }
        finally { gate.Release(); }
    }

    private StartResult StartLocked(LaunchSpec spec)
    {
        if (_entries.TryGetValue(spec.ProfileId, out var current))
            return Refuse(current.State == ServerState.Stopping
                ? "The server is still stopping. Wait until it is offline, or force-stop it."
                : "The server is already running.");
        if (Check(spec, checkPorts: true, out var installRoot) is { } problem) return Refuse(problem);
        if (spec.Rcon is { } rcon && global::ServerAgent.Rcon.RconConfig.Problem(rcon, spec.Port) is { } rconProblem) return Refuse(rconProblem);

        // Start launches what Apply wrote (Phase 3 Ruling 1); it never writes config itself.
        string configDir, profileDir;
        try
        {
            configDir = _areas.Resolve("configs", spec.Slug);
            profileDir = _areas.Resolve("profiles", spec.Slug);
            if (!File.Exists(Path.Combine(configDir, "server.cfg"))) return Refuse("This profile hasn't been applied to the server yet.");
            if (spec.UseBasicCfg && !File.Exists(Path.Combine(configDir, "basic.cfg"))) return Refuse("basic.cfg is missing; apply the profile again.");
            Directory.CreateDirectory(profileDir);
            if (spec.Rcon is { } rconSpec) global::ServerAgent.Rcon.RconConfig.Write(profileDir, rconSpec); // BattlEye renames it while running (Ruling 3)
        }
        catch (Exception e) when (e is AreaException or IOException or UnauthorizedAccessException)
        {
            return Refuse($"Couldn't prepare the server's folders: {e.Message}");
        }

        var psi = new ProcessStartInfo(Path.Combine(installRoot, spec.Executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installRoot,
        };
        foreach (var arg in _game.BuildArguments(spec, configDir, profileDir, _mods.Folder)) psi.ArgumentList.Add(arg);
        var tail = new RptTail(profileDir, _game.LogPattern);
        tail.SeekToEnd(0); // the new launch writes a new RPT; never replay an older one

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("no process was started"); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            _log.LogError(e, "Starting {Slug} failed", spec.Slug);
            return Refuse($"Couldn't start the server: {e.Message}");
        }

        var queryHost = spec.CustomArgs.Select(a => a.StartsWith("-ip=", StringComparison.OrdinalIgnoreCase) ? a[4..] : null).LastOrDefault(a => a is not null);
        var entry = NewEntry(spec.ProfileId, spec.Slug, spec.Port, installRoot, spec.Executable, process, process.StartTime.ToUniversalTime(), queryHost, tail);
        entry.Spec = spec;
        entry.Rcon = spec.Rcon;
        _entries[spec.ProfileId] = entry;
        Supervise(entry); // before anything that can fail: a launched server is always watched
        _ = QueryLoopAsync(entry, delayFirst: false);
        SaveState();
        _log.LogInformation("Started {Slug} (pid {Pid}) on port {Port}", spec.Slug, process.Id, spec.Port);
        return new StartResult(true, null, process.Id, entry.StartedAt);
    }

    private static StartResult Refuse(string message) => new(false, message, null, null);

    // The agent's own boundary (design §5.3): the panel's spec only selects among what this agent allows.
    private string? Check(LaunchSpec spec, bool checkPorts, out string installRoot)
    {
        installRoot = "";
        string requested;
        try { requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.InstallPath ?? "")); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { requested = ""; }
        var root = _areas.InstallRoots.FirstOrDefault(r => r.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (root is null) return "That install isn't one of this agent's install roots (Agent:InstallRoots in the agent's appsettings.json).";
        installRoot = root;

        if (!SlugPattern().IsMatch(spec.Slug ?? "")) return "The profile slug is invalid.";
        if (!_game.AllowedExecutables.Contains(spec.Executable ?? "", StringComparer.OrdinalIgnoreCase))
            return $"\"{spec.Executable}\" isn't an allowed server executable.";
        if (!File.Exists(Path.Combine(root, spec.Executable!))) return $"{spec.Executable} wasn't found in {root}.";
        if (Arma3LaunchArgs.Validate(spec.CustomArgs) is { } badArg) return badArg;
        // Every listed mod must be a valid name for a folder this agent has (Phase 5a Ruling 5).
        if (_mods.Check([.. spec.Mods ?? [], .. spec.ServerMods ?? [], .. spec.HeadlessMods ?? []]) is { } badMod) return badMod;
        if (spec.Port is < 1024 or > 65530) return "The game port must be between 1024 and 65530.";
        if (!checkPorts) return null;

        // Final review I5: BattlEye binds the RCon port too, so it's busy-checked and reserved like the game ports, both ways.
        var queryPort = _game.QueryPort(spec.Port);
        var rconPort = spec.Rcon?.Port;
        var busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().FirstOrDefault(l => l.Port == spec.Port || l.Port == queryPort || l.Port == rconPort);
        if (busy is not null) return $"Port {busy.Port} is already in use.";
        static bool In(int? port, int from) => port is { } p && from <= p && p <= from + 4;
        var other = _entries.Values.FirstOrDefault(e => e.ProfileId != spec.ProfileId
            && ((e.Port <= spec.Port + 4 && spec.Port <= e.Port + 4) || In(rconPort, e.Port) || In(e.Rcon?.Port, spec.Port)));
        if (other is not null)
            return $"Ports {spec.Port}–{spec.Port + 4}{(rconPort is { } r ? $" (RCon {r})" : "")} overlap the server running on port {other.Port}{(other.Rcon is { } o ? $" (RCon {o.Port})" : "")}.";
        return null;
    }

    public async Task<CommandResult> StopAsync(Guid profileId)
    {
        var gate = Gate(profileId);
        await gate.WaitAsync();
        try
        {
            if (!_entries.TryGetValue(profileId, out var e)) return new CommandResult(false, "The server isn't running.");
            lock (e)
            {
                if (e.State == ServerState.Stopping) return new CommandResult(false, "The server is already stopping.");
                e.StopRequested = true;
                SetState(e, ServerState.Stopping, null);
            }
            BeginGracefulStop(e);
            return new CommandResult(true, null);
        }
        finally { gate.Release(); }
    }

    public async Task<CommandResult> RestartAsync(LaunchSpec spec)
    {
        spec = spec with { CustomArgs = spec.CustomArgs ?? [] };
        var gate = Gate(spec.ProfileId);
        await gate.WaitAsync();
        try
        {
            if (!_entries.TryGetValue(spec.ProfileId, out var e)) return new CommandResult(false, "The server isn't running.");
            if (Check(spec, checkPorts: false, out _) is { } problem) return new CommandResult(false, problem);
            lock (e)
            {
                if (e.State == ServerState.Stopping) return new CommandResult(false, "The server is already stopping.");
                e.StopRequested = true;
                e.RestartSpec = spec;
                SetState(e, ServerState.Stopping, null);
            }
            BeginGracefulStop(e);
            return new CommandResult(true, null);
        }
        finally { gate.Release(); }
    }

    // Never waits on the profile lock, so it also cuts a graceful stop or a restart short.
    public Task<CommandResult> ForceStopAsync(Guid profileId)
    {
        if (!_entries.TryGetValue(profileId, out var e)) return Task.FromResult(new CommandResult(false, "The server isn't running."));
        List<Hc> headless;
        lock (e)
        {
            e.StopRequested = true;
            e.RestartSpec = null;
            headless = [.. e.Headless];
        }
        foreach (var h in headless) KillProcess(h.Process, e.Slug);
        Kill(e);
        return Task.FromResult(new CommandResult(true, null));
    }

    // Graceful = headless clients first, then the server: close the main window (spike F10: exits in ~3 s),
    // then kill after StopTimeoutSeconds. RCon #shutdown replaces the server's first step in Phase 7.
    private void BeginGracefulStop(Entry e) => _ = GracefulStopAsync(e);

    private async Task GracefulStopAsync(Entry e)
    {
        List<Hc> headless;
        lock (e) headless = [.. e.Headless];
        if (headless.Count > 0) await CloseHeadlessAsync(headless, e.Slug);
        // Phase 7a Ruling 6: RCon #shutdown first (StopRequested is already set, so the exit reads as asked for); without RCon, close the
        // window as before. Either way the kill after StopTimeoutSeconds stays the backstop.
        if ((await Rcon.CommandAsync(e.ProfileId, "#shutdown")).Ok) _log.LogInformation("Sent #shutdown to {Slug} over RCon", e.Slug);
        else
            try
            {
                e.Process.Refresh();
                e.Process.CloseMainWindow();
            }
            catch (InvalidOperationException) { }
        await KillAfterTimeoutAsync(e);
    }

    private async Task CloseHeadlessAsync(IReadOnlyList<Hc> headless, string slug)
    {
        foreach (var h in headless)
            try { h.Process.Refresh(); h.Process.CloseMainWindow(); } catch (InvalidOperationException) { }
        await Task.WhenAll(headless.Select(async h =>
        {
            try { await h.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(_options.StopTimeoutSeconds)); }
            catch (TimeoutException) { KillProcess(h.Process, slug); }
            catch (InvalidOperationException) { }
        }));
    }

    private void KillProcess(Process p, string slug)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception x) { _log.LogWarning(x, "Killing a headless client of {Slug} failed", slug); }
    }

    // Starts the headless clients that aren't running yet, up to the spec's count; only beside a running server.
    public async Task<CommandResult> StartHeadlessAsync(LaunchSpec spec)
    {
        spec = spec with { CustomArgs = spec.CustomArgs ?? [] };
        var gate = Gate(spec.ProfileId);
        await gate.WaitAsync();
        try
        {
            if (!_entries.TryGetValue(spec.ProfileId, out var e)) return new CommandResult(false, "The server isn't running.");
            if (spec.Headless is not { } hc) return new CommandResult(false, "This profile has no headless clients set up.");
            if (hc.Count is < 1 or > 8) return new CommandResult(false, "Headless clients: 1 to 8.");
            if (!IPAddress.TryParse(hc.Connect, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !hc.Connect.Contains('.'))
                return new CommandResult(false, "The headless client address must be an IPv4 address, for example 127.0.0.1.");
            // It goes on the command line as -password=: a quote or control character could split it into extra arguments.
            if (hc.Password is { } pw && (pw.Length > 128 || pw.Any(c => c == '"' || char.IsControl(c))))
                return new CommandResult(false, "The join password can't be passed to headless clients: it must be one line of at most 128 characters, without quotes.");
            if (Check(spec, checkPorts: false, out var installRoot) is { } problem) return new CommandResult(false, problem);
            lock (e)
                if (e.State is not (ServerState.Starting or ServerState.Online or ServerState.NotResponding))
                    return new CommandResult(false, "Start headless clients once the server is running.");

            var profileDir = _areas.Resolve("profiles", spec.Slug);
            for (var n = 1; n <= hc.Count; n++)
            {
                lock (e)
                    if (e.Headless.Any(h => h.Index == n && !HasExited(h.Process))) continue;
                Directory.CreateDirectory(Path.Combine(profileDir, $"hc{n}"));
                var psi = new ProcessStartInfo(Path.Combine(installRoot, spec.Executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installRoot };
                foreach (var arg in Arma3Command.HeadlessArguments(spec, n, profileDir, _mods.Folder)) psi.ArgumentList.Add(arg);
                Process p;
                try { p = Process.Start(psi) ?? throw new InvalidOperationException("no process was started"); }
                catch (Exception x) when (x is Win32Exception or InvalidOperationException)
                {
                    return new CommandResult(false, $"Couldn't start headless client {n}: {x.Message}");
                }
                AddHeadless(e, new Hc { Index = n, Process = p, StartedAt = p.StartTime.ToUniversalTime() });
                _log.LogInformation("Started headless client {N} for {Slug} (pid {Pid})", n, spec.Slug, p.Id);
            }
            SaveState();
            return new CommandResult(true, null);
        }
        catch (AreaException x) { return new CommandResult(false, x.Message); }
        finally { gate.Release(); }
    }

    public Task<CommandResult> StopHeadlessAsync(Guid profileId)
    {
        if (!_entries.TryGetValue(profileId, out var e)) return Task.FromResult(new CommandResult(false, "The server isn't running."));
        List<Hc> headless;
        lock (e) headless = e.Headless.Where(h => !HasExited(h.Process)).ToList();
        if (headless.Count == 0) return Task.FromResult(new CommandResult(false, "No headless clients are running."));
        _ = CloseHeadlessAsync(headless, e.Slug);
        return Task.FromResult(new CommandResult(true, null));
    }

    // Never auto-restarted: an exit only lowers the running count.
    private void AddHeadless(Entry e, Hc h)
    {
        lock (e)
        {
            e.Headless.RemoveAll(x => x.Index == h.Index);
            e.Headless.Add(h);
            if (!e.Exited) SetState(e, e.State, e.Query);
        }
        h.Process.Exited += (_, _) =>
        {
            lock (e) if (!e.Exited) SetState(e, e.State, e.Query);
            SaveState();
        };
        h.Process.EnableRaisingEvents = true;
    }

    private async Task KillAfterTimeoutAsync(Entry e)
    {
        try { await e.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(_options.StopTimeoutSeconds)); }
        catch (TimeoutException)
        {
            _log.LogInformation("{Slug} didn't close within {Seconds} s; killing it", e.Slug, _options.StopTimeoutSeconds);
            Kill(e);
        }
        catch (InvalidOperationException) { }
    }

    private void Kill(Entry e)
    {
        try { e.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception x) { _log.LogWarning(x, "Killing {Slug} failed", e.Slug); }
    }

    public async Task ReattachAsync()
    {
        foreach (var (id, saved) in await LoadStateAsync())
        {
            var process = SameProcess(saved.Pid, saved.Executable, saved.StartedAt);
            if (process is null)
            {
                // A headless client never outlives its server, even one that died while the agent was down (review I7).
                foreach (var h in saved.Headless ?? [])
                    if (SameProcess(h.Pid, saved.Executable, h.StartedAt) is { } orphan)
                        using (orphan) KillProcess(orphan, saved.Slug);
                Publish(new ProfileStatus(id, ServerState.Offline, null, null, null, "Stopped while the agent was not running", null));
                continue;
            }
            var tail = new RptTail(_areas.Resolve("profiles", saved.Slug), _game.LogPattern);
            tail.SeekToEnd(65536);
            var entry = NewEntry(id, saved.Slug, saved.Port, saved.InstallPath, saved.Executable, process, saved.StartedAt, saved.QueryHost, tail);
            entry.Rcon = global::ServerAgent.Rcon.RconConfig.TryRead(_areas.Resolve("profiles", saved.Slug));
            _entries[id] = entry;
            Supervise(entry);
            foreach (var h in saved.Headless ?? [])
                if (SameProcess(h.Pid, saved.Executable, h.StartedAt) is { } hp)
                    AddHeadless(entry, new Hc { Index = h.Index, Process = hp, StartedAt = h.StartedAt });
            await PollAsync(entry);
            _ = QueryLoopAsync(entry, delayFirst: true);
            _log.LogInformation("Re-attached to {Slug} (pid {Pid})", saved.Slug, saved.Pid);
        }
        SaveState();
    }

    // A briefly locked file (antivirus, backup) is retried; giving up re-attaches nothing but keeps the agent running.
    private async Task<Dictionary<Guid, StateEntry>> LoadStateAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return StateFile.Load(_statePath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    _log.LogError(e, "Couldn't read {Path}; servers started before this agent run are not re-attached", _statePath);
                    return [];
                }
                await Task.Delay(500);
            }
        }
    }

    // A pid alone can be reused by another program, so the name and start time must match too.
    private static Process? SameProcess(int pid, string executable, DateTime startedAt)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals(Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase)
                && Math.Abs((p.StartTime.ToUniversalTime() - startedAt.ToUniversalTime()).TotalSeconds) < 2)
                return p;
            p.Dispose();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        return null;
    }

    private Entry NewEntry(Guid id, string slug, int port, string installRoot, string executable, Process process, DateTime startedAt, string? queryHost, RptTail tail) => new()
    {
        ProfileId = id,
        Slug = slug,
        Port = port,
        InstallRoot = installRoot,
        Executable = executable,
        Process = process,
        StartedAt = startedAt,
        QueryHost = queryHost,
        Tail = tail,
        QueryEndpoint = new IPEndPoint(IPAddress.TryParse(queryHost, out var ip) ? ip : IPAddress.Loopback, _game.QueryPort(port)),
    };

    private void Supervise(Entry e)
    {
        e.Process.Exited += (_, _) => _ = OnExitedAsync(e);
        e.Process.EnableRaisingEvents = true;
        lock (e) SetState(e, ServerState.Starting, null);
    }

    private async Task QueryLoopAsync(Entry e, bool delayFirst)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.QueryIntervalSeconds));
        try
        {
            if (delayFirst) await Task.Delay(interval, e.Cts.Token);
            while (true)
            {
                await PollAsync(e);
                await Task.Delay(interval, e.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Online only while A2S answers; Starting until the first answer; NotResponding after 3 misses once online.
    private async Task PollAsync(Entry e)
    {
        var info = await A2sClient.QueryAsync(e.QueryEndpoint, TimeSpan.FromSeconds(2), e.Cts.Token);
        var startHeadless = false;
        lock (e)
        {
            if (e.Exited || e.State == ServerState.Stopping) return;
            if (info is not null)
            {
                e.Misses = 0;
                // "Start with the server" is opt-in per profile (plan Ruling 13): the first time the server answers.
                startHeadless = !e.EverOnline && e.Spec?.Headless is { WithServer: true };
                e.EverOnline = true;
                if (e.State != ServerState.Online || e.Query != info) SetState(e, ServerState.Online, info);
            }
            else if (e.EverOnline && ++e.Misses >= 3 && e.State == ServerState.Online)
                SetState(e, ServerState.NotResponding, null);
        }
        if (e.Rcon is { } rconSpec && !Rcon.IsOpen(e.ProfileId) && e.EverOnline && !e.Exited && !e.StopRequested) Rcon.Open(e.ProfileId, rconSpec);
        if (startHeadless)
        {
            var r = await StartHeadlessAsync(e.Spec!);
            if (!r.Ok) _log.LogWarning("Headless clients for {Slug} didn't start: {Error}", e.Slug, r.Error);
        }
    }

    private async Task OnExitedAsync(Entry e)
    {
        if (_disposed) return;
        var gate = Gate(e.ProfileId);
        await gate.WaitAsync();
        LaunchSpec? restart;
        try
        {
            if (_disposed) return;
            ReadLog(e); // the last lines before exit
            int? code = null;
            try { code = e.Process.ExitCode; } catch (Exception x) when (x is InvalidOperationException or Win32Exception) { }
            await Rcon.CloseAsync(e.ProfileId);
            // Ruling 6: an exit nobody here asked for but with code 0, once it was online, is a deliberate shutdown (an in-game admin's
            // #shutdown), not a crash. Final review I6: before it ever came online, code 0 is still a failed start.
            var selfShutdown = !e.StopRequested && code == 0 && e.EverOnline;
            var stopped = e.StopRequested || selfShutdown;
            List<Hc> leftovers;
            lock (e)
            {
                e.Exited = true;
                e.Cts.Cancel();
                leftovers = e.Headless.Where(h => !HasExited(h.Process)).ToList();
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(e.ProfileId, e));
                restart = e.StopRequested ? e.RestartSpec : null;
                Publish(stopped
                    ? new ProfileStatus(e.ProfileId, ServerState.Offline, null, null, null, selfShutdown ? "The server shut itself down (exit code 0)." : null, code)
                    : new ProfileStatus(e.ProfileId, ServerState.Error, null, null, null, !e.EverOnline && code == 0
                        ? "The server closed before it came online (exit code 0). Its RPT log says why."
                        : $"Server exited unexpectedly (exit code {code?.ToString() ?? "unknown"}).", code));
            }
            foreach (var h in leftovers) KillProcess(h.Process, e.Slug); // a headless client never outlives its server
            SaveState();
            _log.LogInformation("{Slug} exited (code {Code}, requested {Requested})", e.Slug, code, e.StopRequested);
        }
        finally { gate.Release(); }

        if (restart is null) return;
        var r = await StartAsync(restart);
        if (!r.Ok) Publish(new ProfileStatus(restart.ProfileId, ServerState.Error, null, null, null, r.Error, null));
    }

    private async Task LogLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
                foreach (var e in _entries.Values) ReadLog(e);
        }
        catch (OperationCanceledException) { }
    }

    // At most 500 lines per profile per tick; the rest waits for the next tick.
    private void ReadLog(Entry e)
    {
        try
        {
            IReadOnlyList<string> lines;
            lock (e.Tail) lines = e.Tail.Poll(500, 256 * 1024); // well under the panel hub's 1 MiB message limit
            if (lines.Count == 0 || _disposed) return;
            var now = DateTime.UtcNow;
            LogLinesRead?.Invoke(e.ProfileId, lines.Select(l => new LogLine(now, l)).ToArray());
        }
        catch (Exception x) { _log.LogWarning(x, "Reading the log of {Slug} failed", e.Slug); }
    }

    private void SetState(Entry e, ServerState state, QueryInfo? query)
    {
        e.State = state;
        e.Query = query;
        Publish(new ProfileStatus(e.ProfileId, state, e.Process.Id, e.StartedAt, query, null, null, AliveHeadless(e)));
    }

    private void Publish(ProfileStatus status)
    {
        if (_disposed) return;
        var (rconState, rconError) = Rcon.StateOf(status.ProfileId);
        status = status with { Rcon = rconState, RconError = rconError };
        _statuses[status.ProfileId] = status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception x) { _log.LogWarning(x, "A status listener failed"); }
    }

    // Never throws: a failed write is logged and the next state change writes the whole map again.
    private void SaveState()
    {
        if (_disposed) return;
        try
        {
            // Snapshot each entry under its own lock first, then write under the file lock (never both at once the other way round).
            var map = _entries.Values.ToDictionary(e => e.ProfileId, e =>
            {
                lock (e)
                {
                    var hc = e.Headless.Where(h => !HasExited(h.Process)).Select(h => new HcState(h.Index, h.Process.Id, h.StartedAt)).ToArray();
                    return new StateEntry(e.Process.Id, e.StartedAt, e.Slug, e.Port, e.InstallRoot, e.Executable, e.QueryHost, hc.Length > 0 ? hc : null);
                }
            });
            lock (_stateLock) StateFile.Save(_statePath, map);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Couldn't write {Path}; a server started now may not be re-attached after an agent restart", _statePath);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _stopping.Cancel();
        _ = Rcon.DisposeAsync().AsTask();
        foreach (var e in _entries.Values) e.Cts.Cancel();
    }
}
