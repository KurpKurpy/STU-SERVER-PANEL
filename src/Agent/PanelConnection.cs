using System.Reflection;
using System.Text.Json.Serialization;
using Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ServerAgent.Files;
using ServerAgent.Mods;
using ServerAgent.Resources;
using ServerAgent.Steam;

namespace ServerAgent;

// The agent's one link to the panel: it dials out (no inbound port on the box), says hello, answers the panel's
// commands as SignalR client results, and pushes status, log lines and resource samples. It reconnects forever.
public sealed class PanelConnection(AgentOptions options, Supervisor supervisor, FileService files, ResourceSampler sampler, Areas areas,
    ConfigWriter configs, ILogger<PanelConnection> log, Func<HttpMessageHandler>? handlerFactory = null, JobRunner? jobs = null, ModStore? mods = null, KeyService? keys = null,
    ServerAgent.Missions.MissionWriter? missions = null, AddonScanner? scanner = null, FileManager? fileManager = null,
    ServerAgent.Backups.ServerBackups? backups = null) : BackgroundService
{
    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => TimeSpan.FromSeconds(context.PreviousRetryCount switch { 0 => 0, 1 => 2, 2 => 10, _ => 30 });
    }

    private HubConnection? _connection;
    // Job results and deployment reports must reach the panel even across a disconnect: each waits here until the panel takes it.
    private readonly List<(string Method, object Payload)> _unsent = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Before the first hello, so it already reports a server the agent re-attached to as running.
        await supervisor.ReattachAsync();
        _ = SampleLoopAsync(ct);

        if (string.IsNullOrWhiteSpace(options.PanelUrl) || string.IsNullOrWhiteSpace(options.Key))
        {
            log.LogError("Agent:PanelUrl and Agent:Key must be set in appsettings.json; not connecting to the panel");
            return;
        }

        var connection = _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(options.PanelUrl.TrimEnd('/') + "/"), "hubs/agent"), o =>
            {
                o.Headers["Authorization"] = "AgentKey " + options.Key;
                if (handlerFactory is not null)
                {
                    o.HttpMessageHandlerFactory = _ => handlerFactory();
                    o.Transports = HttpTransportType.LongPolling;
                }
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        connection.On<LaunchSpec, StartResult>(nameof(IAgentClient.Start), supervisor.StartAsync);
        connection.On<Guid, CommandResult>(nameof(IAgentClient.Stop), supervisor.StopAsync);
        connection.On<Guid, CommandResult>(nameof(IAgentClient.ForceStop), supervisor.ForceStopAsync);
        connection.On<LaunchSpec, CommandResult>(nameof(IAgentClient.Restart), supervisor.RestartAsync);
        connection.On<string, string, string, FileEntry[]>(nameof(IAgentClient.ListFiles), files.ListFiles);
        connection.On<string, string, long, int, FileChunk>(nameof(IAgentClient.ReadFile), files.ReadFile);
        connection.On<ConfigBundle, ApplyResult>(nameof(IAgentClient.ApplyConfig), configs.Apply);
        connection.On<Guid, string, ConfigSnapshot>(nameof(IAgentClient.ReadConfig), configs.Read);
        connection.On<LaunchSpec, CommandResult>(nameof(IAgentClient.StartHeadless), supervisor.StartHeadlessAsync);
        connection.On<Guid, CommandResult>(nameof(IAgentClient.StopHeadless), supervisor.StopHeadlessAsync);
        connection.On<Guid, string, RconReply>(nameof(IAgentClient.Rcon), supervisor.RconAsync); // Phase 7a
        if (jobs is not null)
        {
            connection.On<JobSpec, CommandResult>(nameof(IAgentClient.RunJob), jobs.Enqueue);
            connection.On<Guid, string, CommandResult>(nameof(IAgentClient.AnswerJob), jobs.Answer);
            connection.On<Guid, CommandResult>(nameof(IAgentClient.CancelJob), jobs.Cancel);
            jobs.Updated += update => Send(nameof(IAgentHub.JobUpdated), update);
            jobs.Finished += result => Keep(nameof(IAgentHub.JobFinished), result);
        }
        if (backups is not null) // Phase 7b
        {
            connection.On<string, CommandResult>(nameof(IAgentClient.DeleteBackup), backups.Delete);
            connection.On<int, int>(nameof(IAgentClient.PruneSafetyCopies), backups.PruneSafetyCopies);
        }
        if (mods is not null) connection.On(nameof(IAgentClient.ListMods), mods.List);
        if (missions is not null)
        {
            connection.On<MissionDeploySpec, DeployResult>(nameof(IAgentClient.DeployMission), missions.Deploy);
            missions.Changed += result => Keep(nameof(IAgentHub.DeploymentChanged), result);
        }
        if (fileManager is not null)
        {
            connection.On<FileSpace, string, FileListing>(nameof(IAgentClient.FileList), fileManager.List);
            connection.On<FileSpace, string, string, FileSearchResult>(nameof(IAgentClient.FileSearch), fileManager.Search);
            connection.On<FileSpace, string, long, int, FileChunk>(nameof(IAgentClient.FileRead), fileManager.Read);
            connection.On<FileChangeSpec, CommandResult>(nameof(IAgentClient.FileChange), fileManager.Change);
            fileManager.Changed += result => Keep(nameof(IAgentHub.FileChanged), result);
        }
        if (scanner is not null)
        {
            connection.On<AddonScanRequest, CommandResult>(nameof(IAgentClient.ScanAddons), scanner.Enqueue);
            scanner.Scanned += scan => Keep(nameof(IAgentHub.AddonsScanned), scan);
        }
        if (keys is not null)
        {
            connection.On<string, KeyFile[]>(nameof(IAgentClient.ListKeys), keys.List);
            connection.On<string, ModRef[], KeysResult>(nameof(IAgentClient.DeployKeys), keys.Deploy);
            connection.On<string, string[], KeysResult>(nameof(IAgentClient.CleanupKeys), keys.Cleanup);
        }
        connection.Reconnected += connectionId =>
        {
            log.LogInformation("Reconnected to the panel at {Url}", options.PanelUrl);
            _ = HelloUntilAcceptedAsync(connection, ct);
            return Task.CompletedTask;
        };
        connection.Reconnecting += e => { log.LogWarning("Lost the panel ({Error}); reconnecting", e?.Message); return Task.CompletedTask; };

        supervisor.StatusChanged += status => Send(nameof(IAgentHub.StatusChanged), status);
        supervisor.LogLinesRead += (profileId, lines) => Send(nameof(IAgentHub.LogLines), profileId, lines);
        supervisor.RconMessage += (profileId, text) => Send(nameof(IAgentHub.RconMessage), profileId, DateTime.UtcNow, text);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(ct);
                log.LogInformation("Connected to the panel at {Url}", options.PanelUrl);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("Can't reach the panel ({Error}); trying again in 5 s", e.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { }
            }
        }
        await HelloUntilAcceptedAsync(connection, ct);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    }

    // Without a hello the panel shows every server as unknown, so one it didn't take (the call failed, or the link
    // dropped mid-call) is sent again while connected: 1, 2, 4, 8, 16, then every 30 s.
    private async Task HelloUntilAcceptedAsync(HubConnection connection, CancellationToken ct)
    {
        for (var attempt = 0; connection.State == HubConnectionState.Connected && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await SendHelloAsync(connection);
                await FlushResultsAsync();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("The panel didn't take this agent's hello ({Error}); sending it again", e.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5))), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_connection is not null) await _connection.DisposeAsync();
        supervisor.Dispose(); // lets go of the servers; never stops them
    }

    private Task SendHelloAsync(HubConnection connection)
    {
        var installs = areas.InstallRoots
            .Select(r => new InstallInfo(r, areas.InstallArea(r), File.Exists(Path.Combine(r, "arma3server_x64.exe")), areas.IsReadOnlyInstall(r)))
            .ToArray();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        return connection.InvokeAsync(nameof(IAgentHub.Hello),
            new AgentHello(Environment.MachineName, version.Split('+')[0], installs, areas.Describe(), supervisor.Snapshot(), sampler.Recent, jobs?.Jobs ?? []));
    }

    // Results wait here until the panel has taken them; a failed send leaves them for the next hello.
    private async Task FlushResultsAsync()
    {
        while (_connection is { State: HubConnectionState.Connected } c)
        {
            (string Method, object Payload) next;
            lock (_unsent)
            {
                if (_unsent.Count == 0) return;
                next = _unsent[0];
            }
            try { await c.InvokeCoreAsync(next.Method, [next.Payload]); }
            catch (Exception e)
            {
                log.LogWarning("The panel didn't take a {Method} report yet ({Error})", next.Method, e.Message);
                return;
            }
            lock (_unsent) _unsent.Remove(next);
        }
    }

    private void Keep(string method, object payload)
    {
        lock (_unsent) _unsent.Add((method, payload));
        _ = FlushResultsAsync();
    }

    // While disconnected nothing is queued: the next hello carries the current statuses and resource history.
    private void Send(string method, params object?[] args)
    {
        if (_connection is not { State: HubConnectionState.Connected } c) return;
        _ = c.SendCoreAsync(method, args).ContinueWith(t => log.LogDebug(t.Exception, "Sending {Method} failed", method), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { Send(nameof(IAgentHub.Resources), sampler.Sample(supervisor.Running)); }
                catch (Exception e) { log.LogWarning(e, "Sampling resources failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
