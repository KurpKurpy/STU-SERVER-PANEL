using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles;
using Panel.Features.Profiles.Config;
using Panel.Hubs;

namespace Panel.Features.Servers;

public record ProcessDto(double CpuPercent, long MemoryBytes);
public record DiskDto(long FreeBytes, long TotalBytes);
public record ServerDto(Guid Id, string Name, string InstallName, string State, string? Error, string? Address, int Port,
    DateTime? StartedAt, DateTime? LastStartedAt, QueryInfo? Query, ProcessDto? Process, DiskDto? Disk,
    int Revision, int? AppliedRevision, string? MissionTemplate, HeadlessDto? Headless);
public record HeadlessDto(int Count, int Running, bool WithServer);
public record JoinInfo(string? Address, int Port, string? Password);

// One builder for every place a server's state is shown (REST and the browser hub), so they never disagree.
public static class ServerViews
{
    public static async Task<ServerDto[]> BuildAsync(AppDb db, AgentRegistry registry, Guid? id = null)
    {
        var rows = await (from p in db.Profiles
                          join i in db.Installs on p.ServerInstallId equals i.Id
                          where id == null || p.Id == id
                          orderby p.Name
                          select new
                          {
                              p.Id, p.Name, InstallName = i.Name, i.AgentId, i.InstallPath, p.PublicAddress, p.Port,
                              LastStartedAt = db.ProcessRuns.Where(r => r.ProfileId == p.Id).OrderByDescending(r => r.StartedAt).Select(r => (DateTime?)r.StartedAt).FirstOrDefault(),
                              Revision = db.ProfileRevisions.Where(r => r.ProfileId == p.Id).Max(r => (int?)r.Number) ?? 0,
                              AppliedRevision = db.ProfileRevisions.Where(r => r.Id == p.AppliedRevisionId).Select(r => (int?)r.Number).FirstOrDefault(),
                              AppliedJson = db.ProfileRevisions.Where(r => r.Id == p.AppliedRevisionId).Select(r => r.SnapshotJson).FirstOrDefault(),
                          }).ToListAsync();
        // Headless clients as applied (what Start will use), with the agent's live running count.
        HeadlessSettings? Hc(string? json) =>
            json is null ? null : JsonSerializer.Deserialize<ProfileSnapshot>(json, ProfileStore.Json)?.Settings.Headless is { Enabled: true } h ? h : null;
        return rows.Select(r =>
        {
            // Never a last-known state: no connected agent, or one that hasn't said hello yet, means "unknown".
            if (registry.Hello(r.AgentId) is null)
                return new ServerDto(r.Id, r.Name, r.InstallName, "unknown", null, r.PublicAddress, r.Port, null, r.LastStartedAt, null, null, null,
                    r.Revision, r.AppliedRevision, null, Hc(r.AppliedJson) is { } h0 ? new HeadlessDto(h0.Count, 0, h0.WithServer) : null);
            var status = registry.Status(r.Id);
            var sample = registry.Latest(r.AgentId);
            var process = sample?.Processes.FirstOrDefault(p => p.ProfileId == r.Id);
            var disk = sample?.Disks.FirstOrDefault(d => d.Root.Equals(Path.GetPathRoot(r.InstallPath), StringComparison.OrdinalIgnoreCase));
            var running = status is { State: not (ServerState.Offline or ServerState.Error) };
            return new ServerDto(r.Id, r.Name, r.InstallName, StateName(status?.State ?? ServerState.Offline), status?.Error, r.PublicAddress, r.Port,
                running ? status!.StartedAt : null, r.LastStartedAt, running ? status!.Query : null,
                running && process is not null ? new ProcessDto(process.CpuPercent, process.MemoryBytes) : null,
                disk is null ? null : new DiskDto(disk.FreeBytes, disk.TotalBytes),
                r.Revision, r.AppliedRevision, running ? registry.MissionTemplate(r.Id) : null,
                Hc(r.AppliedJson) is { } h ? new HeadlessDto(h.Count, running ? status!.HeadlessRunning : 0, h.WithServer) : null);
        }).ToArray();
    }

    public static string StateName(ServerState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}

public static partial class ServerEndpoints
{
    private const int Chunk = 262144;

    [GeneratedRegex("^[A-Za-z0-9_.-]+\\.rpt$")]
    private static partial Regex LogName();

    public static void MapServers(this WebApplication app)
    {
        var g = app.MapGroup("/api/servers");
        g.MapGet("", (AppDb db, AgentRegistry registry) => ServerViews.BuildAsync(db, registry)).RequirePermission(Permissions.ServerView);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.ServerView);
        g.MapGet("/{id:guid}/join", Join).RequirePermission(Permissions.ServerViewJoinInfo);
        g.MapPost("/{id:guid}/start", (Guid id, Control c) => c.RunAsync(id, Action.Start)).RequirePermission(Permissions.ServerControl);
        g.MapPost("/{id:guid}/stop", (Guid id, Control c) => c.RunAsync(id, Action.Stop)).RequirePermission(Permissions.ServerControl);
        g.MapPost("/{id:guid}/restart", (Guid id, Control c) => c.RunAsync(id, Action.Restart)).RequirePermission(Permissions.ServerControl);
        g.MapPost("/{id:guid}/force-stop", (Guid id, Control c) => c.RunAsync(id, Action.ForceStop)).RequirePermission(Permissions.ServerForceStop);
        g.MapPost("/{id:guid}/headless/start", (Guid id, Control c) => c.RunAsync(id, Action.StartHeadless)).RequirePermission(Permissions.ServerControl);
        g.MapPost("/{id:guid}/headless/stop", (Guid id, Control c) => c.RunAsync(id, Action.StopHeadless)).RequirePermission(Permissions.ServerControl);
        g.MapGet("/{id:guid}/logs", Logs).RequirePermission(Permissions.LogsView);
        g.MapGet("/{id:guid}/logs/{file}", Tail).RequirePermission(Permissions.LogsView);
        g.MapGet("/{id:guid}/logs/{file}/download", Download).RequirePermission(Permissions.LogsView);
    }

    private static async Task<IResult> Get(Guid id, AppDb db, AgentRegistry registry) =>
        (await ServerViews.BuildAsync(db, registry, id)).FirstOrDefault() is { } s ? Results.Ok(s) : ApiError.Of(404, "not_found", "No such server.");

    // Design §5.2's one exception: the join password, from the applied revision, for server.viewJoinInfo.
    private static async Task<IResult> Join(Guid id, AppDb db, ProfileStore store)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return ApiError.Of(404, "not_found", "No such server.");
        var applied = p.AppliedRevisionId is { } revId ? await db.ProfileRevisions.FindAsync(revId) : null;
        var password = applied is null ? null : store.Load(applied).Secrets?.Password;
        return Results.Ok(new JoinInfo(p.PublicAddress, p.Port, string.IsNullOrEmpty(password) ? null : password));
    }

    public enum Action { Start, Stop, Restart, ForceStop, StartHeadless, StopHeadless }

    // What makes Start refuse, for callers that must know before they change anything (Start Mission, final review I-1):
    // SteamCMD writing the install's files, or a mod list naming mods that aren't installed.
    internal static async Task<IResult?> LaunchBlockedAsync(AppDb db, ServerInstall install, Guid? modListId)
    {
        var installId = install.Id.ToString();
        if (await db.Jobs.AnyAsync(j => j.Type == Panel.Features.Mods.JobKinds.ServerInstall && j.TargetId == installId
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)))
            return ApiError.Of(409, "installing", $"SteamCMD is installing or updating {install.Name}. Start the server once that job finishes.");
        if (modListId is not { } listId) return null;
        if (await Panel.Features.Mods.ModLaunch.ResolveAsync(db, listId) is not { } mods)
            return ApiError.Of(409, "mods_missing", "That mod list no longer exists.");
        if (mods.NotInstalled.Length > 0)
            return ApiError.Of(409, "mods_missing", $"These mods in {mods.ListName} aren't installed: {string.Join(", ", mods.NotInstalled)}. Install them on the Mods page first.");
        return null;
    }

    // A scoped service (registered in Program.cs) shared by the four control endpoints.
    public sealed class Control(AppDb db, AgentRegistry registry, AgentCalls agents, ProfileStore store, AuditLog audit, IHttpContextAccessor http,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp)
    {
        public async Task<IResult> RunAsync(Guid id, Action action)
        {
            var row = await (from pr in db.Profiles join i in db.Installs on pr.ServerInstallId equals i.Id where pr.Id == id select new { pr, i }).FirstOrDefaultAsync();
            if (row is null) return ApiError.Of(404, "not_found", "No such server.");
            var (p, install) = (row.pr, row.i);
            if (!registry.IsConnected(install.AgentId)) return Offline();

            // Start launches the applied revision, never the saved head (plan Ruling 1).
            var spec = new LaunchSpec(p.Id, p.Slug, install.InstallPath, p.Executable, p.Port, false, []);
            if (action is Action.Start or Action.Restart or Action.StartHeadless)
            {
                // Arma would start on half-written files, and SteamCMD can't replace files a running server holds open.
                var installId = install.Id.ToString();
                if (await db.Jobs.AnyAsync(j => j.Type == Panel.Features.Mods.JobKinds.ServerInstall && j.TargetId == installId
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)))
                    return ApiError.Of(409, "installing", $"SteamCMD is installing or updating {install.Name}. Start the server once that job finishes.");
                // Final review I2: a restore is overwriting this agent's profiles, configs, missions or keys.
                if (await db.Jobs.AnyAsync(j => j.Type == Panel.Features.Backups.BackupService.RestoreJob && j.AgentId == install.AgentId
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)))
                    return ApiError.Of(409, "restoring", "A backup is being restored on this server's machine. Start the server once that finishes.");
                var applied = p.AppliedRevisionId is { } revId ? await db.ProfileRevisions.FindAsync(revId) : null;
                if (applied is null)
                    return ApiError.Of(409, "not_applied", "Apply the profile to the server before starting it.");
                var (snapshot, secrets) = store.Load(applied);
                if (secrets is null)
                    return ApiError.Of(409, "secrets_unreadable", "The saved passwords can't be decrypted (the panel's encryption keys changed). Set them again on the profile's General tab, save and apply.");
                var files = ConfigFiles.Generate(snapshot.ToConfig(secrets), SecretMode.Real);
                spec = spec with
                {
                    UseBasicCfg = files.BasicCfg is not null, CustomArgs = ConfigFiles.LaunchArguments(snapshot.Settings),
                    Headless = ConfigFiles.Headless(snapshot.Settings, secrets),
                };
                // Profiles may share ports (Friday and Sunday on 2100), or touch another's RCon port (game port + 10): only one of them runs.
                if (action is Action.Start or Action.Restart
                    && (await Panel.Features.Profiles.ProfileEndpoints.PortSharersAsync(db, p.Id, install.AgentId, p.Port))
                        .FirstOrDefault(o => registry.Status(o.Id) is { State: not (ServerState.Offline or ServerState.Error) }) is { } clash)
                    return ApiError.Of(409, "port_conflict", $"{clash.Name} is running on ports that overlap this server's ({p.Port}–{p.Port + 4}, RCon "
                        + $"{Panel.Features.Players.RconSettings.Port(p.Port)}). Only one of them can run at a time: stop {clash.Name} first.");
                // Phase 7a Rulings 3–4: RCon on loopback, game port + 10, with the profile's own generated password, whenever BattlEye is on.
                if (action is Action.Start or Action.Restart && Panel.Features.Players.RconSettings.BattlEyeOn(snapshot.Settings))
                    spec = spec with { Rcon = new RconSpec(Panel.Features.Players.RconSettings.Port(p.Port), await Panel.Features.Players.RconSettings.PasswordAsync(p, db, dp)) };
                if (action is Action.StartHeadless && spec.Headless is null)
                    return ApiError.Of(409, "no_headless", "Headless clients aren't enabled in the applied revision.");

                // The applied revision's mod list, with its current contents (Phase 5a Ruling 7).
                if (snapshot.ModCollectionId is { } listId)
                {
                    if (await Panel.Features.Mods.ModLaunch.ResolveAsync(db, listId) is not { } mods)
                        return ApiError.Of(409, "mods_missing", "The applied mod list no longer exists. Pick a mod list on the profile's Mods tab, save and apply.");
                    if (mods.NotInstalled.Length > 0)
                        return ApiError.Of(409, "mods_missing", $"These mods in {mods.ListName} aren't installed: {string.Join(", ", mods.NotInstalled)}. Install them on the Mods page first.");
                    spec = spec with { Mods = mods.Client, ServerMods = mods.Server, HeadlessMods = mods.Headless };
                    if (action is Action.Start or Action.Restart && await DeployKeysAsync(p, install, mods) is { } keysError) return keysError;
                }
            }
            if (action is Action.Start or Action.Restart && Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                registry.ExpectActor(p.Id, userId);

            CommandResult result;
            try
            {
                result = action switch
                {
                    Action.Start => ToCommand(await agents.CallAsync(install.AgentId, c => c.Start(spec))),
                    Action.Stop => await agents.CallAsync(install.AgentId, c => c.Stop(p.Id)),
                    Action.Restart => await agents.CallAsync(install.AgentId, c => c.Restart(spec)),
                    Action.StartHeadless => await agents.CallAsync(install.AgentId, c => c.StartHeadless(spec)),
                    Action.StopHeadless => await agents.CallAsync(install.AgentId, c => c.StopHeadless(p.Id)),
                    _ => await agents.CallAsync(install.AgentId, c => c.ForceStop(p.Id)),
                };
            }
            catch (AgentUnavailableException) { return Offline(); }
            catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
            if (!result.Ok) return ApiError.Of(409, "agent_refused", result.Error ?? "The agent refused.");

            var (verb, done) = action switch
            {
                Action.Start => ("server.started", "Started"),
                Action.Stop => ("server.stopped", "Stopped"),
                Action.Restart => ("server.restarted", "Restarted"),
                Action.StartHeadless => ("server.headlessStarted", "Started headless clients for"),
                Action.StopHeadless => ("server.headlessStopped", "Stopped headless clients for"),
                _ => ("server.forceStopped", "Force-stopped"),
            };
            await audit.RecordAsync(verb, $"{done} {p.Name}", "profile", p.Id.ToString(), isPublic: true);
            return Results.Accepted();
        }

        // Missing keys go in before the server starts; deploy only copies, after a backup (Ruling 8).
        private async Task<IResult?> DeployKeysAsync(Profile p, ServerInstall install, Panel.Features.Mods.ResolvedMods mods)
        {
            KeysResult keys;
            try { keys = await agents.CallAsync(install.AgentId, c => c.DeployKeys(install.InstallPath, mods.Keys)); }
            catch (AgentUnavailableException) { return Offline(); }
            catch (HubException e) { return ApiError.Of(409, "keys_failed", e.Message); }
            if (!keys.Ok) return ApiError.Of(409, "keys_failed", $"The mod keys couldn't be deployed: {keys.Error}");
            if (keys.Added.Length > 0)
                await audit.RecordAsync("keys.deployed", $"Deployed {keys.Added.Length} mod keys for {p.Name}", "install", install.Id.ToString(),
                    new { added = keys.Added, backup = keys.BackupFolder });
            return null;
        }

        private static CommandResult ToCommand(StartResult r) => new(r.Ok, r.Error);
        private static IResult Offline() => ApiError.Of(409, "agent_offline", "The server agent isn't connected.");
    }

    private sealed record LogTarget(Guid AgentId, string RelativePath);

    private static async Task<(LogTarget? Target, IResult? Error)> Locate(Guid id, string? file, AppDb db, AgentRegistry registry)
    {
        var row = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == id select new { p.Slug, i.AgentId }).FirstOrDefaultAsync();
        if (row is null) return (null, ApiError.Of(404, "not_found", "No such server."));
        if (file is not null && !LogName().IsMatch(file)) return (null, ApiError.Of(400, "validation", "That isn't a server log file name."));
        if (!registry.IsConnected(row.AgentId)) return (null, ApiError.Of(409, "agent_offline", "The server agent isn't connected."));
        return (new LogTarget(row.AgentId, file is null ? row.Slug : $"{row.Slug}\\{file}"), null);
    }

    private static async Task<IResult> Logs(Guid id, AppDb db, AgentRegistry registry, AgentCalls agents)
    {
        var (target, error) = await Locate(id, null, db, registry);
        if (error is not null) return error;
        try { return Results.Ok(await agents.CallAsync(target!.AgentId, c => c.ListFiles("profiles", target.RelativePath, "*.rpt"))); }
        catch (AgentUnavailableException) { return ApiError.Of(409, "agent_offline", "The server agent isn't connected."); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
    }

    // The last tailBytes (≤ 1 MiB) of a log as text, starting on a whole line.
    private static async Task<IResult> Tail(Guid id, string file, int? tailBytes, AppDb db, AgentRegistry registry, AgentCalls agents)
    {
        var (target, error) = await Locate(id, file, db, registry);
        if (error is not null) return error;
        var want = Math.Clamp(tailBytes ?? Chunk, 1, 1 << 20);
        try
        {
            var size = (await agents.CallAsync(target!.AgentId, c => c.ReadFile("profiles", target.RelativePath, 0, 1))).TotalSize;
            var start = Math.Max(0, size - want);
            var bytes = new MemoryStream();
            for (var offset = start; offset < size;)
            {
                var o = offset;
                var chunk = await agents.CallAsync(target.AgentId, c => c.ReadFile("profiles", target.RelativePath, o, (int)Math.Min(Chunk, size - o)));
                if (chunk.Data.Length == 0) break;
                bytes.Write(chunk.Data);
                offset += chunk.Data.Length;
            }
            var data = bytes.ToArray().AsSpan();
            if (start > 0 && data.IndexOf((byte)'\n') is var nl and >= 0) data = data[(nl + 1)..];
            return Results.Text(Encoding.UTF8.GetString(data), "text/plain", Encoding.UTF8);
        }
        catch (AgentUnavailableException) { return ApiError.Of(409, "agent_offline", "The server agent isn't connected."); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }
    }

    // Streams the whole file in 256 KiB reads. Its length is fixed by the first read, so a log that is still
    // being written downloads as it was when the request started.
    private static async Task<IResult> Download(Guid id, string file, HttpContext ctx, AppDb db, AgentRegistry registry, AgentCalls agents)
    {
        var (target, error) = await Locate(id, file, db, registry);
        if (error is not null) return error;
        FileChunk first;
        try { first = await agents.CallAsync(target!.AgentId, c => c.ReadFile("profiles", target.RelativePath, 0, Chunk)); }
        catch (AgentUnavailableException) { return ApiError.Of(409, "agent_offline", "The server agent isn't connected."); }
        catch (HubException e) { return ApiError.Of(409, "agent_refused", e.Message); }

        var total = first.TotalSize;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{file}\"";
        ctx.Response.ContentLength = total;
        try
        {
            var offset = (long)Math.Min(first.Data.Length, total);
            await ctx.Response.Body.WriteAsync(first.Data.AsMemory(0, (int)offset));
            while (offset < total)
            {
                var o = offset;
                var chunk = await agents.CallAsync(target.AgentId, c => c.ReadFile("profiles", target.RelativePath, o, (int)Math.Min(Chunk, total - o)));
                if (chunk.Data.Length == 0) break;
                var n = (int)Math.Min(chunk.Data.Length, total - offset);
                await ctx.Response.Body.WriteAsync(chunk.Data.AsMemory(0, n));
                offset += n;
            }
            if (offset < total) ctx.Abort(); // the file shrank or the agent went away mid-download
        }
        catch (Exception e) when (e is AgentUnavailableException or HubException) { ctx.Abort(); }
        return Results.Empty;
    }
}
