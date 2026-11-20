using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Panel.Hubs;

namespace Panel.Tests;

// A scripted agent on /hubs/agent: canned answers for every IAgentClient call, and a record of each call.
public sealed class FakeAgent : IAsyncDisposable
{
    public HubConnection Connection { get; }
    public ConcurrentQueue<(string Method, object?[] Args)> Calls { get; } = new();

    public Func<LaunchSpec, StartResult> OnStart { get; set; } = _ => new StartResult(true, null, 4242, DateTime.UtcNow);
    public Func<Guid, CommandResult> OnStop { get; set; } = _ => new CommandResult(true, null);
    public Func<Guid, CommandResult> OnForceStop { get; set; } = _ => new CommandResult(true, null);
    public Func<LaunchSpec, CommandResult> OnRestart { get; set; } = _ => new CommandResult(true, null);
    public Func<string, string, string, FileEntry[]> OnListFiles { get; set; } = (_, _, _) => [];
    public Func<string, string, long, int, FileChunk> OnReadFile { get; set; } = (_, _, _, _) => new FileChunk([], 0);
    // By default Apply succeeds and reports the SHA-256 of exactly what it was sent, as the real agent's re-read would.
    public Func<ConfigBundle, ApplyResult> OnApplyConfig { get; set; } = b => new ApplyResult(true, null, null,
    [
        new WrittenFile("server.cfg", Sha(b.ServerCfg), b.ServerCfg.Length),
        .. b.BasicCfg is null ? Array.Empty<WrittenFile>() : [new WrittenFile("basic.cfg", Sha(b.BasicCfg), b.BasicCfg.Length)],
    ]);
    public Func<Guid, string, ConfigSnapshot> OnReadConfig { get; set; } = (_, _) => new ConfigSnapshot(null, null, null);
    public Func<LaunchSpec, CommandResult> OnStartHeadless { get; set; } = _ => new CommandResult(true, null);
    public Func<Guid, CommandResult> OnStopHeadless { get; set; } = _ => new CommandResult(true, null);
    public Func<JobSpec, CommandResult> OnRunJob { get; set; } = _ => new CommandResult(true, null);
    public Func<Guid, string, CommandResult> OnAnswerJob { get; set; } = (_, _) => new CommandResult(true, null);
    public Func<Guid, CommandResult> OnCancelJob { get; set; } = _ => new CommandResult(true, null);
    public Func<DiskMod[]> OnListMods { get; set; } = () => [];
    public Func<string, KeyFile[]> OnListKeys { get; set; } = _ => [];
    public Func<string, ModRef[], KeysResult> OnDeployKeys { get; set; } = (_, _) => new KeysResult(true, null, null, [], []);
    public Func<string, string[], KeysResult> OnCleanupKeys { get; set; } = (_, _) => new KeysResult(true, null, null, [], []);
    public Func<MissionDeploySpec, DeployResult> OnDeployMission { get; set; } = s => new DeployResult(s.DeploymentId, DeployResult.Done, null, @"D:\Server Data\backups\missions\x\1");
    public Func<AddonScanRequest, CommandResult> OnScanAddons { get; set; } = _ => new CommandResult(true, null);
    public Func<FileSpace, string, FileListing> OnFileList { get; set; } = (_, _) => new FileListing([], false);
    public Func<FileSpace, string, string, FileSearchResult> OnFileSearch { get; set; } = (_, _, _) => new FileSearchResult([], false);
    public Func<FileSpace, string, long, int, FileChunk> OnFileRead { get; set; } = (_, _, _, _) => new FileChunk([], 0);
    public Func<FileChangeSpec, CommandResult> OnFileChange { get; set; } = _ => new CommandResult(true, null);
    public Func<Guid, string, RconReply> OnRcon { get; set; } = (_, _) => new RconReply(true, "", null); // Phase 7a
    public Func<string, CommandResult> OnDeleteBackup { get; set; } = _ => new CommandResult(true, null); // Phase 7b
    public Func<int, int> OnPruneSafetyCopies { get; set; } = _ => 0;
    // As the agent: an accepted change reports back through FileChanged (Phase 4b Ruling 5). Off, a test reports itself.
    public bool ReportFileChanges { get; set; } = true;
    public string Key { get; private set; } = "";

    public static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private FakeAgent(HubConnection connection)
    {
        Connection = connection;
        connection.On<LaunchSpec, StartResult>(nameof(IAgentClient.Start), s => Record(nameof(IAgentClient.Start), [s], () => OnStart(s)));
        connection.On<Guid, CommandResult>(nameof(IAgentClient.Stop), id => Record(nameof(IAgentClient.Stop), [id], () =>
        {
            var r = OnStop(id);
            if (r.Ok && ExitOnStop && IsRunning(id)) _ = Task.Run(() => SendStatusAsync(new ProfileStatus(id, ServerState.Offline, null, null, null, null, 0)));
            return r;
        }));
        connection.On<Guid, CommandResult>(nameof(IAgentClient.ForceStop), id => Record(nameof(IAgentClient.ForceStop), [id], () => OnForceStop(id)));
        connection.On<LaunchSpec, CommandResult>(nameof(IAgentClient.Restart), s => Record(nameof(IAgentClient.Restart), [s], () => OnRestart(s)));
        connection.On<string, string, string, FileEntry[]>(nameof(IAgentClient.ListFiles),
            (a, d, p) => Record(nameof(IAgentClient.ListFiles), [a, d, p], () => OnListFiles(a, d, p)));
        connection.On<string, string, long, int, FileChunk>(nameof(IAgentClient.ReadFile),
            (a, p, o, l) => Record(nameof(IAgentClient.ReadFile), [a, p, o, l], () => OnReadFile(a, p, o, l)));
        connection.On<ConfigBundle, ApplyResult>(nameof(IAgentClient.ApplyConfig), b => Record(nameof(IAgentClient.ApplyConfig), [b], () =>
            IsRunning(b.ProfileId) ? new ApplyResult(false, "Stop the server before applying; it rewrites its profile file when it exits.", null, []) : OnApplyConfig(b)));
        connection.On<Guid, string, ConfigSnapshot>(nameof(IAgentClient.ReadConfig), (id, slug) => Record(nameof(IAgentClient.ReadConfig), [id, slug], () => OnReadConfig(id, slug)));
        connection.On<LaunchSpec, CommandResult>(nameof(IAgentClient.StartHeadless), s => Record(nameof(IAgentClient.StartHeadless), [s], () => OnStartHeadless(s)));
        connection.On<Guid, CommandResult>(nameof(IAgentClient.StopHeadless), id => Record(nameof(IAgentClient.StopHeadless), [id], () => OnStopHeadless(id)));
        connection.On<JobSpec, CommandResult>(nameof(IAgentClient.RunJob), s => Record(nameof(IAgentClient.RunJob), [s], () => OnRunJob(s)));
        connection.On<Guid, string, CommandResult>(nameof(IAgentClient.AnswerJob), (id, a) => Record(nameof(IAgentClient.AnswerJob), [id, a], () => OnAnswerJob(id, a)));
        connection.On<Guid, CommandResult>(nameof(IAgentClient.CancelJob), id => Record(nameof(IAgentClient.CancelJob), [id], () => OnCancelJob(id)));
        connection.On(nameof(IAgentClient.ListMods), () => Record(nameof(IAgentClient.ListMods), [], () => OnListMods()));
        connection.On<string, KeyFile[]>(nameof(IAgentClient.ListKeys), r => Record(nameof(IAgentClient.ListKeys), [r], () => OnListKeys(r)));
        connection.On<string, ModRef[], KeysResult>(nameof(IAgentClient.DeployKeys), (r, m) => Record(nameof(IAgentClient.DeployKeys), [r, m], () => OnDeployKeys(r, m)));
        connection.On<string, string[], KeysResult>(nameof(IAgentClient.CleanupKeys), (r, n) => Record(nameof(IAgentClient.CleanupKeys), [r, n], () => OnCleanupKeys(r, n)));
        connection.On<MissionDeploySpec, DeployResult>(nameof(IAgentClient.DeployMission), s => Record(nameof(IAgentClient.DeployMission), [s], () => OnDeployMission(s)));
        // Kept apart from Calls: hello and finished jobs ask for scans in the background (Phase 5b), at times tests can't predict.
        connection.On<AddonScanRequest, CommandResult>(nameof(IAgentClient.ScanAddons), r => { ScanRequests.Enqueue(r); return OnScanAddons(r); });
        connection.On<FileSpace, string, FileListing>(nameof(IAgentClient.FileList), (s, p) => Record(nameof(IAgentClient.FileList), [s, p], () => OnFileList(s, p)));
        connection.On<FileSpace, string, string, FileSearchResult>(nameof(IAgentClient.FileSearch),
            (s, p, q) => Record(nameof(IAgentClient.FileSearch), [s, p, q], () => OnFileSearch(s, p, q)));
        connection.On<FileSpace, string, long, int, FileChunk>(nameof(IAgentClient.FileRead),
            (s, p, o, l) => Record(nameof(IAgentClient.FileRead), [s, p, o, l], () => OnFileRead(s, p, o, l)));
        connection.On<Guid, string, RconReply>(nameof(IAgentClient.Rcon), (id, c) => Record(nameof(IAgentClient.Rcon), [id, c], () => OnRcon(id, c)));
        connection.On<string, CommandResult>(nameof(IAgentClient.DeleteBackup), f => Record(nameof(IAgentClient.DeleteBackup), [f], () => OnDeleteBackup(f)));
        connection.On<int, int>(nameof(IAgentClient.PruneSafetyCopies), d => Record(nameof(IAgentClient.PruneSafetyCopies), [d], () => OnPruneSafetyCopies(d)));
        connection.On<FileChangeSpec, CommandResult>(nameof(IAgentClient.FileChange), spec => Record(nameof(IAgentClient.FileChange), [spec], () =>
        {
            var r = OnFileChange(spec);
            if (r.Ok && ReportFileChanges) _ = Task.Run(() => SendFileChangedAsync(new FileChangeResult(spec.Id, true, null, null)));
            return r;
        }));
    }

    private T Record<T>(string method, object?[] args, Func<T> answer)
    {
        Calls.Enqueue((method, args));
        return answer();
    }

    public static HubConnection Build(PanelFactory f, string path, Action<HttpConnectionOptions>? configure = null) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, path), HttpTransportType.LongPolling, o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                configure?.Invoke(o);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    public static async Task<FakeAgent> ConnectAsync(PanelFactory f, string key)
    {
        var agent = new FakeAgent(Build(f, "/hubs/agent", o => o.Headers["Authorization"] = "AgentKey " + key)) { Key = key };
        await agent.Connection.StartAsync();
        return agent;
    }

    public Task HelloAsync(InstallInfo[]? installs = null, ProfileStatus[]? statuses = null, ResourceSample[]? resources = null,
        string machine = "TESTBOX", string version = "1.2.3", AreaInfo[]? areas = null, Guid[]? jobs = null) =>
        Connection.InvokeAsync(nameof(IAgentHub.Hello), new AgentHello(machine, version, installs ?? [], areas ?? [], statuses ?? [], resources ?? [], jobs ?? []));

    public Task SendRconMessageAsync(Guid profileId, string text) =>
        Connection.InvokeAsync(nameof(IAgentHub.RconMessage), profileId, DateTime.UtcNow, text);

    public Task SendStatusAsync(ProfileStatus status)
    {
        States[status.ProfileId] = status.State;
        return Connection.InvokeAsync(nameof(IAgentHub.StatusChanged), status);
    }

    // As the real agent: the last state reported per profile; Apply is refused while one runs, and a Stop ends in Offline.
    public ConcurrentDictionary<Guid, ServerState> States { get; } = new();
    public bool ExitOnStop { get; set; } = true;
    private bool IsRunning(Guid id) => States.TryGetValue(id, out var s) && s is not (ServerState.Offline or ServerState.Error);
    public Task SendLinesAsync(Guid profileId, params LogLine[] lines) => Connection.InvokeAsync(nameof(IAgentHub.LogLines), profileId, lines);
    public Task SendResourcesAsync(ResourceSample sample) => Connection.InvokeAsync(nameof(IAgentHub.Resources), sample);
    public Task SendJobUpdateAsync(JobUpdate update) => Connection.InvokeAsync(nameof(IAgentHub.JobUpdated), update);
    public Task SendJobFinishedAsync(JobResult result) => Connection.InvokeAsync(nameof(IAgentHub.JobFinished), result);
    public Task SendDeploymentChangedAsync(DeployResult result) => Connection.InvokeAsync(nameof(IAgentHub.DeploymentChanged), result);
    public Task SendAddonsScannedAsync(AddonScan scan) => Connection.InvokeAsync(nameof(IAgentHub.AddonsScanned), scan);
    public Task SendFileChangedAsync(FileChangeResult result) => Connection.InvokeAsync(nameof(IAgentHub.FileChanged), result);

    // What the real agent does for a write: pull the upload from the panel with its key.
    public Task<HttpResponseMessage> PullTransferAsync(PanelFactory f, Guid changeId)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("Authorization", "AgentKey " + Key);
        return c.GetAsync($"/api/agent/transfers/{changeId}");
    }
    public System.Collections.Concurrent.ConcurrentQueue<AddonScanRequest> ScanRequests { get; } = new();
    public AddonScanRequest[] Scans() => ScanRequests.ToArray();
    // Distinct deployments, in the order first sent: the hub's resend after hello can race a deployment's first send under load
    // and send it twice, which the real agent ignores (same id: already running; same hash: Done).
    public MissionDeploySpec[] Deploys() => Calls.Where(c => c.Method == nameof(IAgentClient.DeployMission)).Select(c => (MissionDeploySpec)c.Args[0]!)
        .DistinctBy(s => s.DeploymentId).ToArray();

    public JobSpec LastJob() => (JobSpec)Calls.Last(c => c.Method == nameof(IAgentClient.RunJob)).Args[0]!;

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

internal static class AgentKit
{
    public static async Task<(HttpClient Owner, Guid AgentId, string Key)> PairAsync(PanelFactory f, HttpClient? owner = null)
    {
        owner ??= await TestUsers.LoginAsync(f, "Owner", "Crow");
        var r = await owner.PostAsJsonAsync("/api/agents", new { name = "Dev PC" });
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (owner, body.GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(25);
        }
    }

    public static AgentRegistry Registry(PanelFactory f) => f.Services.GetRequiredService<AgentRegistry>();

    public const string InstallPath = @"K:\Arma 3";
    public const string InstallArea = "install-0a1b2c3d";
    public const string DataRoot = @"D:\Server Data";

    // Owner + a connected FakeAgent that reports one install, registered as "Arma 3".
    public static async Task<(HttpClient Owner, Guid AgentId, FakeAgent Agent, Guid InstallId)> InstallAsync(PanelFactory f, HttpClient? owner = null)
    {
        var (o, agentId, key) = await PairAsync(f, owner);
        var agent = await FakeAgent.ConnectAsync(f, key);
        await agent.HelloAsync(installs: [new InstallInfo(InstallPath, InstallArea, true)], areas:
        [
            new AreaInfo("configs", DataRoot + @"\configs", true, true, false),
            new AreaInfo("profiles", DataRoot + @"\profiles", true, true, false),
        ]);
        var r = await o.PostAsJsonAsync("/api/installs", new { agentId, path = InstallPath, name = "Arma 3" });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        return (o, agentId, agent, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    // Identity fields go to POST /api/profiles (which ignores the rest); the config is saved through the Phase 3 endpoints.
    public sealed record ProfileSpec(Guid serverInstallId, string name, string slug, string executable, int port, string publicAddress,
        string serverCfg, string? basicCfg, string customArgs);

    public static ProfileSpec Profile(Guid installId, string name = "Main Operations", string slug = "main", int port = 2302,
        string serverCfg = "hostname = \"13th\"; passwordAdmin = \"hunter2-long\";", string? basicCfg = null, string customArgs = "-autoInit") =>
        new(installId, name, slug, "arma3server_x64.exe", port, "203.0.113.5", serverCfg, basicCfg, customArgs);

    // Applied by default (through the FakeAgent), so Start has an applied revision; pass applied: false to test not_applied.
    public static async Task<Guid> CreateProfileAsync(HttpClient c, ProfileSpec profile, bool applied = true)
    {
        var r = await c.PostAsJsonAsync("/api/profiles", profile);
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await PutFileAsync(c, id, "server.cfg", profile.serverCfg)).StatusCode);
        if (profile.basicCfg is not null) Assert.Equal(System.Net.HttpStatusCode.OK, (await PutFileAsync(c, id, "basic.cfg", profile.basicCfg)).StatusCode);
        if (profile.customArgs.Length > 0)
            Assert.Equal(System.Net.HttpStatusCode.OK, (await EditConfigAsync(c, id, s => s["settings"]!["extraArgs"] = profile.customArgs)).StatusCode);
        if (applied) Assert.Equal(System.Net.HttpStatusCode.OK, (await ApplyAsync(c, id)).StatusCode);
        return id;
    }

    public static async Task<HttpResponseMessage> ApplyAsync(HttpClient c, Guid id) =>
        await c.PostAsJsonAsync($"/api/profiles/{id}/apply", new { revision = await RevisionAsync(c, id) });

    public static async Task<int> RevisionAsync(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}")).GetProperty("revision").GetInt32();

    public static async Task<HttpResponseMessage> PutFileAsync(HttpClient c, Guid id, string file, string text) =>
        await c.PutAsJsonAsync($"/api/profiles/{id}/files/{file}", new { baseRevision = await RevisionAsync(c, id), text });

    // Loads the head snapshot, lets the test change it, and saves it as the next revision.
    public static async Task<HttpResponseMessage> EditConfigAsync(HttpClient c, Guid id, Action<System.Text.Json.Nodes.JsonNode> edit,
        object? secrets = null, string? note = null)
    {
        var detail = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync($"/api/profiles/{id}"))!;
        var snapshot = detail["snapshot"]!;
        edit(snapshot);
        return await c.PutAsJsonAsync($"/api/profiles/{id}/config", new { baseRevision = detail["revision"]!.GetValue<int>(), note, snapshot, secrets });
    }
}
