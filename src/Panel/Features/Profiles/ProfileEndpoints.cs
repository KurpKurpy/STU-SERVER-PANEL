using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaFormats;
using Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles.Config;
using Panel.Hubs;

namespace Panel.Features.Profiles;

public record InstallCreate(Guid AgentId, string? Path, string? Name);
public record InstallDto(Guid Id, Guid AgentId, string Name, string InstallPath, string? Area,
    string ScanStatus = "NotScanned", DateTime? ScannedAt = null, string? ScanNote = null);
public record ProfileCreate(Guid ServerInstallId, string? Name, string? Slug, string? Executable, int Port, string? PublicAddress);
public record ProfilePatch(string? Name, string? PublicAddress, Guid? ServerInstallId, string? Executable, int? Port, string? Slug);
public record ProfileSummary(Guid Id, string Name, string Slug, Guid ServerInstallId, string InstallName, Guid AgentId, string Executable,
    int Port, string? PublicAddress, DateTime CreatedAt, DateTime? UpdatedAt, int Revision, int? AppliedRevision, bool NeedsCfg);
public record SecretState(bool Set);
public record SecretsView(SecretState Password, SecretState PasswordAdmin, SecretState ServerCommandPassword);
public record ProfileDetail(Guid Id, string Name, string Slug, Guid ServerInstallId, string InstallName, Guid AgentId, string Executable,
    int Port, string? PublicAddress, DateTime CreatedAt, DateTime? UpdatedAt, int Revision, int? AppliedRevision, bool NeedsCfg,
    DateTime? AppliedAt, ProfileSnapshot Snapshot, SecretsView Secrets, bool SecretsUnreadable);
public record SecretsWrite(string? Password, string? PasswordAdmin, string? ServerCommandPassword);
public record ConfigPut(int BaseRevision, string? Note, ProfileSnapshot? Snapshot, SecretsWrite? Secrets);
public record FilePut(int BaseRevision, string? Text, string? Note);
public record PreviewBody(ProfileSnapshot? Snapshot);
public record ImportBody(int BaseRevision, string? File, string? Area, string? Name);
public record DuplicateBody(string? Name, string? Slug);
public record CommandPreview(string Server, string[] Headless);
public record PreviewResult(GeneratedFiles Files, CommandPreview Command);
public record SaveResult(ProfileDetail Profile, IReadOnlyList<ConfigWarning> Warnings);

public static partial class ProfileEndpoints
{
    // Phase 2's raw server.cfg protector; ProfileUpgrade still reads it. Phase 3 keeps passwords in "profile.secrets" (ProfileStore).
    public static IDataProtector CfgProtector(IDataProtectionProvider dp) => dp.CreateProtector("profile.serverCfg");

    public static string? TryUnprotectCfg(IDataProtectionProvider dp, string protectedText)
    {
        try { return CfgProtector(dp).Unprotect(protectedText); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public static readonly string[] Executables = ["arma3server_x64.exe", "arma3serverprofiling_x64.exe"];
    private const int MaxImportBytes = 262144;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("""^[^\\/:*?"<>|]+\.cfg$""")]
    private static partial Regex CfgName();

    public static void MapProfiles(this WebApplication app)
    {
        var installs = app.MapGroup("/api/installs");
        installs.MapGet("", ListInstalls).RequirePermission(Permissions.ProfilesManage);
        installs.MapPost("", CreateInstall).RequirePermission(Permissions.AgentsManage);
        installs.MapDelete("/{id:guid}", DeleteInstall).RequirePermission(Permissions.AgentsManage);

        var g = app.MapGroup("/api/profiles");
        g.MapGet("", List).RequirePermission(Permissions.ProfilesView);
        g.MapGet("/catalog", Catalog).RequirePermission(Permissions.ProfilesView);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.ProfilesView);
        g.MapPost("", Create).RequirePermission(Permissions.ProfilesManage);
        g.MapPatch("/{id:guid}", Patch).RequirePermission(Permissions.ProfilesManage);
        g.MapPut("/{id:guid}/config", PutConfig).RequirePermission(Permissions.ProfilesManage);
        g.MapGet("/{id:guid}/files", Files).RequirePermission(Permissions.ProfilesView);
        g.MapPut("/{id:guid}/files/{file}", PutFile).RequirePermission(Permissions.ProfilesManage);
        g.MapPost("/{id:guid}/preview", Preview).RequirePermission(Permissions.ProfilesView);
        g.MapPost("/{id:guid}/import", Import).RequirePermission(Permissions.ProfilesManage);
        g.MapPost("/{id:guid}/duplicate", Duplicate).RequirePermission(Permissions.ProfilesManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.ProfilesManage);
    }

    // ---------- installs (Phase 2) ----------

    private static string? AreaOf(AgentRegistry registry, Guid agentId, string path) =>
        registry.Hello(agentId)?.Installs.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Area;

    private static async Task<InstallDto[]> ListInstalls(AppDb db, AgentRegistry registry) =>
        (await db.Installs.OrderBy(i => i.CreatedAt).ToListAsync())
            .Select(i => new InstallDto(i.Id, i.AgentId, i.Name, i.InstallPath, AreaOf(registry, i.AgentId, i.InstallPath), i.ScanStatus.ToString(), i.ScannedAt, i.ScanNote)).ToArray();

    private static async Task<IResult> CreateInstall(InstallCreate r, AppDb db, AgentRegistry registry, AuditLog audit)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 40) return ApiError.Field("name", "Name must be 1–40 characters.");
        // Any install root the agent reports, with or without the server yet: SteamCMD can install it afterwards (Phase 5a Task 11).
        var reported = registry.Hello(r.AgentId)?.Installs.FirstOrDefault(i => i.Path.Equals(r.Path?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (reported is null)
            return ApiError.Of(400, "unknown_install", "The agent must be connected and report this folder as one of its install roots.");
        if (await db.Installs.AnyAsync(i => i.AgentId == r.AgentId && i.InstallPath == reported.Path))
            return ApiError.Of(409, "conflict", "That install is already registered.");

        var install = new ServerInstall { Id = Guid.NewGuid(), AgentId = r.AgentId, Name = name, InstallPath = reported.Path };
        db.Installs.Add(install);
        await db.SaveChangesAsync();
        await audit.RecordAsync("install.created", $"Registered server install {name}", "install", install.Id.ToString(), new { name, path = install.InstallPath });
        return Results.Created($"/api/installs/{install.Id}", new InstallDto(install.Id, install.AgentId, name, install.InstallPath, reported.Area));
    }

    private static async Task<IResult> DeleteInstall(Guid id, AppDb db, AuditLog audit)
    {
        var install = await db.Installs.FindAsync(id);
        if (install is null) return ApiError.Of(404, "not_found", "No such install.");
        if (await db.Profiles.AnyAsync(p => p.ServerInstallId == id)) return ApiError.Of(409, "in_use", "Profiles still use this install.");
        db.Installs.Remove(install);
        await db.SaveChangesAsync();
        await audit.RecordAsync("install.deleted", $"Removed server install {install.Name}", "install", id.ToString());
        return Results.NoContent();
    }

    // ---------- reading ----------

    private static IQueryable<ProfileSummary> Summaries(AppDb db, Guid? id = null) =>
        from p in db.Profiles
        join i in db.Installs on p.ServerInstallId equals i.Id
        where id == null || p.Id == id
        orderby p.Name
        select new ProfileSummary(p.Id, p.Name, p.Slug, p.ServerInstallId, i.Name, i.AgentId, p.Executable, p.Port, p.PublicAddress,
            p.CreatedAt, p.UpdatedAt,
            db.ProfileRevisions.Where(r => r.ProfileId == p.Id).Max(r => (int?)r.Number) ?? 0,
            db.ProfileRevisions.Where(r => r.Id == p.AppliedRevisionId).Select(r => (int?)r.Number).FirstOrDefault(),
            p.NeedsCfg);

    private static Task<ProfileSummary[]> List(AppDb db) => Summaries(db).ToArrayAsync();

    private static IResult Catalog() => Results.Ok(new { keys = ConfigCatalog.All });

    private static async Task<ProfileDetail?> DetailAsync(AppDb db, ProfileStore store, Guid id)
    {
        var s = await Summaries(db, id).FirstOrDefaultAsync();
        if (s is null) return null;
        var p = (await db.Profiles.FindAsync(id))!;
        var secrets = store.LoadSecrets(p);
        var set = (string n) => new SecretState(!string.IsNullOrEmpty(secrets?.Get(n)));
        return new ProfileDetail(s.Id, s.Name, s.Slug, s.ServerInstallId, s.InstallName, s.AgentId, s.Executable, s.Port, s.PublicAddress,
            s.CreatedAt, s.UpdatedAt, s.Revision, s.AppliedRevision, s.NeedsCfg, p.AppliedAt, await store.HeadAsync(p),
            new SecretsView(set("password"), set("passwordAdmin"), set("serverCommandPassword")), secrets is null);
    }

    private static async Task<IResult> Get(Guid id, AppDb db, ProfileStore store) =>
        await DetailAsync(db, store, id) is { } d ? Results.Ok(d) : NotFound();

    private static async Task<IResult> Files(Guid id, AppDb db, ProfileStore store)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return NotFound();
        var head = await store.HeadAsync(p);
        return Results.Ok(ConfigFiles.Generate(head.ToConfig(store.LoadSecrets(p) ?? Secrets.None), SecretMode.Omit));
    }

    private static async Task<IResult> Preview(Guid id, PreviewBody body, AppDb db, ProfileStore store, AgentRegistry registry)
    {
        var row = await (from x in db.Profiles join i in db.Installs on x.ServerInstallId equals i.Id where x.Id == id select new { p = x, i.AgentId, i.InstallPath })
            .FirstOrDefaultAsync();
        if (row is null) return NotFound();
        var snapshot = Normalize(body.Snapshot);
        var config = snapshot.ToConfig(store.LoadSecrets(row.p) ?? Secrets.None);
        if (ConfigFiles.Validate(config) is [var first, ..]) return ApiError.Field(first.Field, first.Message);
        var files = ConfigFiles.Generate(config, SecretMode.Omit);
        var mods = snapshot.ModCollectionId is { } listId ? await Panel.Features.Mods.ModLaunch.ResolveAsync(db, listId) : null;
        return Results.Ok(new PreviewResult(files, Command(row.p, row.InstallPath, registry.Hello(row.AgentId), config.Settings, files.BasicCfg is not null, config.Secrets, mods)));
    }

    // The exact command lines the agent will run (Arma3Command, shared with the agent), with the agent's own folder paths.
    // Display() masks -password=, so the join password never reaches the browser.
    internal static CommandPreview Command(Profile p, string installPath, AgentHello? hello, ProfileSettings settings, bool useBasicCfg, Secrets secrets,
        Panel.Features.Mods.ResolvedMods? mods = null)
    {
        string Root(string area) => hello?.Areas.FirstOrDefault(a => a.Area == area)?.Path ?? $@"<agent data folder>\{area}";
        var spec = new LaunchSpec(p.Id, p.Slug, installPath, p.Executable, p.Port, useBasicCfg, ConfigFiles.LaunchArguments(settings),
            ConfigFiles.Headless(settings, secrets), mods?.Client, mods?.Server, mods?.Headless);
        var modPath = Panel.Features.Mods.ModLaunch.Paths(hello);
        var exe = Path.Combine(installPath, p.Executable);
        var profileDir = Path.Combine(Root("profiles"), p.Slug);
        var server = Arma3Command.Display(exe, Arma3Command.ServerArguments(spec, Path.Combine(Root("configs"), p.Slug), profileDir, modPath));
        var headless = spec.Headless is { } hc
            ? Enumerable.Range(1, hc.Count).Select(n => Arma3Command.Display(exe, Arma3Command.HeadlessArguments(spec, n, profileDir, modPath))).ToArray()
            : [];
        return new CommandPreview(server, headless);
    }

    // ---------- identity ----------

    private static async Task<IResult?> CheckIdentity(Profile p, string name, string slug, Guid installId, string executable, int port, string? address, AppDb db)
    {
        if (name.Length is 0 or > 40) return ApiError.Field("name", "Name must be 1–40 characters.");
        if (!SlugPattern().IsMatch(slug)) return ApiError.Field("slug", "Slug: 1–32 lower-case letters, digits or hyphens, starting with a letter or digit.");
        if (port is < 1024 or > 65530) return ApiError.Field("port", "Port must be between 1024 and 65530.");
        if (!Executables.Contains(executable)) return ApiError.Field("executable", $"Executable must be {string.Join(" or ", Executables)}.");
        if (address is not null && (address.Length > 253 || address.Any(char.IsWhiteSpace))) return ApiError.Field("publicAddress", "Public address: at most 253 characters, no spaces.");
        var install = await db.Installs.FindAsync(installId);
        if (install is null) return ApiError.Field("serverInstallId", "Pick a registered server install.");
        if (await db.Profiles.AnyAsync(o => o.Id != p.Id && o.Slug == slug))
            return ApiError.Of(409, "conflict_slug", $"Another profile already uses the slug \"{slug}\".");
        // Each profile reserves its game port through port + 4 on its agent's machine.
        var clash = await PortClashAsync(db, p.Id, install.AgentId, port);
        if (clash is not null)
            return ApiError.Of(409, "port_conflict", $"Ports {port}–{port + 4} (and RCon port {port + Panel.Features.Players.RconSettings.PortOffset}) overlap profile {clash}.");
        return null;
    }

    internal static Task<string?> PortClashAsync(AppDb db, Guid profileId, Guid agentId, int port) =>
        (from o in db.Profiles
         join i in db.Installs on o.ServerInstallId equals i.Id
         where o.Id != profileId && i.AgentId == agentId
            && ((o.Port <= port + 4 && port <= o.Port + 4)                                                        // the game ports overlap
                || (port <= o.Port + Panel.Features.Players.RconSettings.PortOffset && o.Port + Panel.Features.Players.RconSettings.PortOffset <= port + 4)  // ours cover its RCon port
                || (o.Port <= port + Panel.Features.Players.RconSettings.PortOffset && port + Panel.Features.Players.RconSettings.PortOffset <= o.Port + 4)) // its cover ours (Phase 7a Ruling 4)
         select o.Name).FirstOrDefaultAsync();

    private static object AuditDetails(Profile p) => new { name = p.Name, slug = p.Slug, port = p.Port, executable = p.Executable };

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<IResult> Create(ProfileCreate r, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx)
    {
        var p = new Profile { Id = Guid.NewGuid() };
        var name = (r.Name ?? "").Trim();
        var slug = (r.Slug ?? "").Trim();
        var address = string.IsNullOrWhiteSpace(r.PublicAddress) ? null : r.PublicAddress.Trim();
        if (await CheckIdentity(p, name, slug, r.ServerInstallId, r.Executable ?? "", r.Port, address, db) is { } error) return error;
        (p.ServerInstallId, p.Name, p.Slug, p.Executable, p.Port, p.PublicAddress) = (r.ServerInstallId, name, slug, r.Executable!, r.Port, address);
        db.Profiles.Add(p);
        await store.SaveRevisionAsync(p, ProfileSnapshot.Empty, Secrets.None, "Created", UserId(ctx));
        await audit.RecordAsync("profile.created", $"Created server profile {p.Name}", "profile", p.Id.ToString(), AuditDetails(p));
        return Results.Created($"/api/profiles/{p.Id}", await Summaries(db, p.Id).FirstAsync());
    }

    // Name and address any time; install, executable and port only while the server is stopped (plan Ruling 3). The slug never changes.
    private static async Task<IResult> Patch(Guid id, ProfilePatch r, AppDb db, ProfileStore store, AgentRegistry registry, AuditLog audit)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return NotFound();
        if (r.Slug is not null && r.Slug.Trim() != p.Slug) return ApiError.Field("slug", "The slug can't change after a profile is created.");
        var installId = r.ServerInstallId ?? p.ServerInstallId;
        var executable = r.Executable ?? p.Executable;
        var port = r.Port ?? p.Port;
        if (installId != p.ServerInstallId || executable != p.Executable || port != p.Port)
        {
            var agentId = await db.Installs.Where(i => i.Id == p.ServerInstallId).Select(i => i.AgentId).FirstAsync();
            if (await StoppedAsync(registry, agentId, id) is { } notStopped) return notStopped;
        }
        var name = r.Name is null ? p.Name : r.Name.Trim();
        var address = r.PublicAddress is null ? p.PublicAddress : string.IsNullOrWhiteSpace(r.PublicAddress) ? null : r.PublicAddress.Trim();
        if (await CheckIdentity(p, name, p.Slug, installId, executable, port, address, db) is { } error) return error;
        (p.ServerInstallId, p.Name, p.Executable, p.Port, p.PublicAddress) = (installId, name, executable, port, address);
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync("profile.updated", $"Changed server profile {p.Name}", "profile", id.ToString(), AuditDetails(p));
        return Results.Ok(await DetailAsync(db, store, id));
    }

    private static Task<IResult?> StoppedAsync(AgentRegistry registry, Guid agentId, Guid profileId)
    {
        if (!registry.IsConnected(agentId))
            return Task.FromResult<IResult?>(ApiError.Of(409, "agent_offline", "The server agent isn't connected, so it can't confirm the server is stopped."));
        if (registry.Status(profileId) is { State: not (ServerState.Offline or ServerState.Error) })
            return Task.FromResult<IResult?>(ApiError.Of(409, "running", "Stop the server first."));
        return Task.FromResult<IResult?>(null);
    }

    // ---------- configuration ----------

    private static ProfileSnapshot Normalize(ProfileSnapshot? s)
    {
        s ??= ProfileSnapshot.Empty;
        var st = s.Settings ?? ProfileSettings.Empty;
        var settings = new ProfileSettings(st.Server ?? [], st.Basic ?? [], st.Difficulty ?? [], st.Launch ?? [], st.ExtraArgs ?? "",
            st.Headless ?? new HeadlessSettings(), string.IsNullOrWhiteSpace(st.MissionsOverride) ? null : st.MissionsOverride);
        var rotation = (s.Rotation ?? []).Select(r => new RotationItem((r.Template ?? "").Trim(), (r.Difficulty ?? "").Trim(), r.ParamsText ?? "")).ToArray();
        return new ProfileSnapshot(settings, s.ExtraServerCfg ?? "", s.ExtraBasicCfg ?? "", s.DifficultyOverride ?? "", rotation, s.DifficultyPresetId, s.ModCollectionId);
    }

    private static Secrets Merge(Secrets current, SecretsWrite? write)
    {
        if (write is null) return current;
        foreach (var (name, value) in new[] { ("password", write.Password), ("passwordAdmin", write.PasswordAdmin), ("serverCommandPassword", write.ServerCommandPassword) })
            if (value is not null) current = current.With(name, value.Length == 0 ? null : value);
        return current;
    }

    private static Secrets Lift(Secrets current, Secrets? found)
    {
        if (found is null) return current;
        foreach (var name in Secrets.Names)
            if (found.Get(name) is { } value) current = current.With(name, value.Length == 0 ? null : value);
        return current;
    }

    private static IResult Stale(int head) => ApiError.Of(409, "stale", $"Someone saved revision {head} meanwhile. Reload to see it.");
    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such profile.");
    private static IResult Syntax(string field, ConfigSyntaxException e) => Results.Json(new { code = "config_syntax", message = e.Message, field }, statusCode: 400);

    private static async Task<IResult> PutConfig(Guid id, ConfigPut body, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return NotFound();
        var head = await store.HeadNumberAsync(id);
        if (body.BaseRevision != head) return Stale(head);
        if (body.Note is { Length: > 200 }) return ApiError.Field("note", "The note can be at most 200 characters.");
        var snapshot = Normalize(body.Snapshot);
        var stored = store.LoadSecrets(p);
        // Unreadable passwords would otherwise be saved as "none set": refuse until all three are given again.
        if (stored is null && body.Secrets is not { Password: not null, PasswordAdmin: not null, ServerCommandPassword: not null }) return Unreadable();
        var secrets = Merge(stored ?? Secrets.None, body.Secrets);
        var warnings = new List<ConfigWarning>();

        // The extras boxes go through the same parser as the raw editor: passwords are lifted out, managed keys are refused.
        foreach (var (field, file, text, parse) in new (string, string, string, Func<string, ParsedFile>)[]
                 { ("extraServerCfg", ConfigCatalog.Server, snapshot.ExtraServerCfg, ConfigFiles.ParseServerCfg), ("extraBasicCfg", ConfigCatalog.Basic, snapshot.ExtraBasicCfg, ConfigFiles.ParseBasicCfg) })
        {
            ParsedFile parsed;
            try { parsed = parse(text); }
            catch (ConfigSyntaxException e) { return Syntax(field, e); }
            var managed = parsed.Values.Keys.FirstOrDefault() ?? (parsed.Rotation is not null || parsed.MissionsOverride is not null ? "class Missions" : parsed.Headless is not null ? "headlessClients" : null);
            if (managed is not null) return ApiError.Field(field, $"{managed} is set in the form, not in the extra text.");
            if (ConfigFiles.SetTwice(file, parsed.Extra, snapshot.Settings) is { } twice)
                return ApiError.Field(field, $"{twice} is set both in the form and in the extra text. Remove one of them.");
            secrets = Lift(secrets, parsed.Secrets);
            warnings.AddRange(parsed.Warnings);
            snapshot = field == "extraServerCfg" ? snapshot with { ExtraServerCfg = parsed.Extra } : snapshot with { ExtraBasicCfg = parsed.Extra };
        }
        return await SaveAsync(p, snapshot, secrets, body.Note, warnings, db, store, audit, ctx);
    }

    private static IResult Unreadable() => ApiError.Of(409, "secrets_unreadable",
        "This profile's passwords can't be decrypted (the panel's encryption keys changed). Set all three passwords again on the General tab, then save.");

    internal static async Task<IResult> SaveAsync(Profile p, ProfileSnapshot snapshot, Secrets secrets, string? note, IReadOnlyList<ConfigWarning> warnings,
        AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx, string action = "profile.saved")
    {
        if (ConfigFiles.Validate(snapshot.ToConfig(secrets)) is [var first, ..]) return ApiError.Field(first.Field, first.Message);
        // Every save path (edit, reset, preset): an old revision may name a list deleted since.
        if (snapshot.ModCollectionId is { } listId && !await db.ModCollections.AnyAsync(c => c.Id == listId))
            return ApiError.Field("modCollectionId", "That mod list no longer exists. Pick an existing one on the Mods tab.");
        var before = await store.HeadAsync(p);
        var beforeSecrets = store.LoadSecrets(p) ?? Secrets.None;
        var revision = await store.SaveRevisionAsync(p, snapshot, secrets, note, UserId(ctx));
        if (p.NeedsCfg) (p.NeedsCfg, p.ServerCfgProtected, p.BasicCfg) = (false, "", null);
        await db.SaveChangesAsync();
        await audit.RecordAsync(action, $"Saved server profile {p.Name} revision {revision.Number}", "profile", p.Id.ToString(),
            new { revision = revision.Number, note = revision.Note, changed = Changed(before, beforeSecrets, snapshot, secrets) });
        return Results.Ok(new SaveResult((await DetailAsync(db, store, p.Id))!, warnings));
    }

    // Names of what changed, never values.
    private static string[] Changed(ProfileSnapshot a, Secrets aSecrets, ProfileSnapshot b, Secrets bSecrets)
    {
        var changed = new List<string>();
        void Dict(string file, Dictionary<string, JsonElement> x, Dictionary<string, JsonElement> y) =>
            changed.AddRange(x.Keys.Union(y.Keys).Where(k => !x.TryGetValue(k, out var u) || !y.TryGetValue(k, out var v) || u.GetRawText() != v.GetRawText())
                .Select(k => $"{file}.{k}"));
        Dict("server", a.Settings.Server, b.Settings.Server);
        Dict("basic", a.Settings.Basic, b.Settings.Basic);
        Dict("difficulty", a.Settings.Difficulty, b.Settings.Difficulty);
        Dict("launch", a.Settings.Launch, b.Settings.Launch);
        changed.AddRange(Secrets.Names.Where(n => aSecrets.Get(n) != bSecrets.Get(n)).Select(n => $"server.{n}"));
        if (a.Settings.ExtraArgs != b.Settings.ExtraArgs) changed.Add("launch.extraArgs");
        if (JsonSerializer.Serialize(a.Settings.Headless) != JsonSerializer.Serialize(b.Settings.Headless)) changed.Add("headless");
        if (a.Settings.MissionsOverride != b.Settings.MissionsOverride || JsonSerializer.Serialize(a.Rotation) != JsonSerializer.Serialize(b.Rotation)) changed.Add("rotation");
        if (a.ExtraServerCfg != b.ExtraServerCfg) changed.Add("extraServerCfg");
        if (a.ExtraBasicCfg != b.ExtraBasicCfg) changed.Add("extraBasicCfg");
        if (a.DifficultyOverride != b.DifficultyOverride) changed.Add("difficultyOverride");
        if (a.ModCollectionId != b.ModCollectionId) changed.Add("modCollection");
        return changed.ToArray();
    }

    private static async Task<IResult> PutFile(Guid id, string file, FilePut body, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx)
    {
        var p = await db.Profiles.FindAsync(id);
        if (p is null) return NotFound();
        var head = await store.HeadNumberAsync(id);
        if (body.BaseRevision != head) return Stale(head);
        return await SaveFileAsync(p, file, body.Text ?? "", body.Note, db, store, audit, ctx);
    }

    // The raw editor and Import: parse one file and replace only that file's part of the head (Ruling 5).
    private static async Task<IResult> SaveFileAsync(Profile p, string file, string text, string? note, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx,
        string action = "profile.saved")
    {
        var max = file switch { "server.cfg" => ConfigFiles.ServerCfgMax, "basic.cfg" => ConfigFiles.BasicCfgMax, "difficulty" => ConfigFiles.DifficultyMax, _ => -1 };
        if (max < 0) return ApiError.Of(404, "not_found", "No such file. Use server.cfg, basic.cfg or difficulty.");
        if (text.Length > max) return ApiError.Of(400, "too_large", $"{file} can be at most {max:N0} characters.");
        if (note is { Length: > 200 }) return ApiError.Field("note", "The note can be at most 200 characters.");
        var head = await store.HeadAsync(p);
        if (store.LoadSecrets(p) is not { } secrets) return Unreadable();
        ParsedFile parsed;
        try
        {
            parsed = file switch
            {
                "server.cfg" => ConfigFiles.ParseServerCfg(text),
                "basic.cfg" => ConfigFiles.ParseBasicCfg(text),
                _ => ConfigFiles.ParseDifficulty(text),
            };
        }
        catch (ConfigSyntaxException e) { return Syntax(file, e); }

        var s = head.Settings;
        var snapshot = file switch
        {
            "server.cfg" => head with
            {
                Settings = s with
                {
                    Server = parsed.Values,
                    Headless = parsed.Headless is { } hc ? s.Headless with { Enabled = true, Addresses = hc.Addresses } : s.Headless with { Enabled = false },
                    MissionsOverride = parsed.MissionsOverride,
                },
                Rotation = parsed.Rotation?.ToArray() ?? [],
                ExtraServerCfg = parsed.Extra,
            },
            "basic.cfg" => head with { Settings = s with { Basic = parsed.Values }, ExtraBasicCfg = parsed.Extra },
            _ => head with { Settings = s with { Difficulty = parsed.Values }, DifficultyOverride = parsed.Extra },
        };
        return await SaveAsync(p, snapshot, Lift(secrets, parsed.Secrets), note, parsed.Warnings, db, store, audit, ctx, action);
    }

    private static async Task<IResult> Import(Guid id, ImportBody body, AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents, AuditLog audit, HttpContext ctx)
    {
        var row = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == id select new { p, i.AgentId }).FirstOrDefaultAsync();
        if (row is null) return NotFound();
        var head = await store.HeadNumberAsync(id);
        if (body.BaseRevision != head) return Stale(head);
        if (body.File is not ("server.cfg" or "basic.cfg")) return ApiError.Field("file", "Import server.cfg or basic.cfg.");
        if (registry.Hello(row.AgentId) is not { } hello) return ApiError.Of(409, "agent_offline", "The server agent isn't connected.");
        var area = body.Area ?? "";
        if (area != "configs" && !hello.Installs.Any(i => i.Area == area)) return ApiError.Of(400, "validation", "Config files can only be imported from an install or the configs area.");
        if (!CfgName().IsMatch(body.Name ?? "")) return ApiError.Of(400, "validation", "That isn't a .cfg file name.");
        string text;
        try
        {
            var chunk = await agents.CallAsync(row.AgentId, c => c.ReadFile(area, body.Name!, 0, MaxImportBytes));
            if (chunk.TotalSize > MaxImportBytes) return ApiError.Of(400, "too_large", "That file is larger than 256 KB.");
            text = Encoding.UTF8.GetString(chunk.Data).TrimStart('﻿');
        }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        catch (HubException e) { return ApiError.Of(400, "agent_refused", e.Message); }
        return await SaveFileAsync(row.p, body.File, text, $"Imported {body.Name}", db, store, audit, ctx, "profile.imported");
    }

    private static async Task<IResult> Duplicate(Guid id, DuplicateBody body, AppDb db, ProfileStore store, AuditLog audit, HttpContext ctx)
    {
        var source = await db.Profiles.FindAsync(id);
        if (source is null) return NotFound();
        if (store.LoadSecrets(source) is not { } sourceSecrets) return Unreadable();
        var agentId = await db.Installs.Where(i => i.Id == source.ServerInstallId).Select(i => i.AgentId).FirstAsync();
        var port = source.Port + 5;
        while (port <= 65530 && await PortClashAsync(db, Guid.Empty, agentId, port) is not null) port += 5;
        if (port > 65530) return ApiError.Of(409, "port_conflict", "No free block of five ports is left above this profile's port.");

        var copy = new Profile { Id = Guid.NewGuid() };
        var name = (body.Name ?? "").Trim();
        var slug = (body.Slug ?? "").Trim();
        if (await CheckIdentity(copy, name, slug, source.ServerInstallId, source.Executable, port, source.PublicAddress, db) is { } error) return error;
        (copy.ServerInstallId, copy.Name, copy.Slug, copy.Executable, copy.Port, copy.PublicAddress) =
            (source.ServerInstallId, name, slug, source.Executable, port, source.PublicAddress);
        db.Profiles.Add(copy);
        var head = await store.HeadNumberAsync(id);
        await store.SaveRevisionAsync(copy, await store.HeadAsync(source), sourceSecrets,
            $"Duplicated from {source.Name} revision {head}", UserId(ctx));
        await audit.RecordAsync("profile.duplicated", $"Duplicated server profile {source.Name} as {copy.Name}", "profile", copy.Id.ToString(), AuditDetails(copy));
        return Results.Created($"/api/profiles/{copy.Id}", await Summaries(db, copy.Id).FirstAsync());
    }

    // Only while the agent is connected and says the server isn't running: a profile mid-run must not vanish.
    private static async Task<IResult> Delete(Guid id, AppDb db, AgentRegistry registry, AuditLog audit)
    {
        var row = await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == id select new { p, i.AgentId }).FirstOrDefaultAsync();
        if (row is null) return NotFound();
        if (await StoppedAsync(registry, row.AgentId, id) is { } notStopped) return notStopped;
        if (await Panel.Features.Operations.Checklist.PlannedUsingAsync(db, o => o.ProfileId == id) is { } ops)
            return ApiError.Of(409, "in_use", $"These planned operations use it: {ops}. Pick another server profile for them first.");
        db.Profiles.Remove(row.p);
        await db.SaveChangesAsync();
        await audit.RecordAsync("profile.deleted", $"Deleted server profile {row.p.Name}", "profile", id.ToString(), AuditDetails(row.p));
        return Results.NoContent();
    }
}
