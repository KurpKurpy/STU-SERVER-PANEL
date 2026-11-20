using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Features.Notifications;

namespace Panel.Features.Operations;

public sealed record Ref(Guid Id, string Name);
public sealed record MissionRef(Guid Id, string Name, string Map, string FileName, bool Archived);
public sealed record ReadinessDto(int Ready, int Total, DateTime CheckedAt);
public sealed record OperationRow(Guid Id, string Name, DateTime StartsAt, string Status, MissionRef? Mission, int? VersionNumber, Ref? Profile, Ref? ModList,
    Ref? MissionMaker, Ref[] Zeus, ReadinessDto? Readiness, DateTime? AppliedAt);
public sealed record OperationDto(Guid Id, string Name, DateTime StartsAt, string Status, MissionRef? Mission, Guid? MissionVersionId, int? VersionNumber,
    Ref? Profile, Ref? ModList, Ref? MissionMaker, Ref[] Zeus, ReadinessDto? Readiness, DateTime? AppliedAt, Ref? AppliedBy, string Description,
    string Briefing, string? Notes, int Version, CheckResult[]? LastCheck, Guid[] Ticked);
public sealed record OperationBody(int? Version, string? Name, string? Description, DateTime? StartsAt, Guid? MissionId, Guid? MissionVersionId,
    Guid? ProfileId, Guid? ModListId, Guid? MissionMakerId, Guid[]? ZeusIds, string? Notes, string? Briefing);
public sealed record StatusBody(string? Status);

// Phase 6a: operations (plan Rulings 2–7, 13). Times are UTC; the browser shows them in its own zone and asks for a month by UTC range.
public static class OperationEndpoints
{
    public static void MapOperations(this WebApplication app)
    {
        var g = app.MapGroup("/api/operations");
        g.MapGet("", List).RequirePermission(Permissions.OperationsView);
        g.MapGet("/people", People).RequirePermission(Permissions.OperationsManage);
        g.MapGet("/options", Options).RequirePermission(Permissions.OperationsManage);
        g.MapGet("/{id:guid}", Get).RequirePermission(Permissions.OperationsView);
        g.MapPost("", Create).RequirePermission(Permissions.OperationsManage);
        g.MapPut("/{id:guid}", Update).RequirePermission(Permissions.OperationsManage);
        g.MapPost("/{id:guid}/status", SetStatus).RequirePermission(Permissions.OperationsManage);
        g.MapPost("/{id:guid}/prepare", Prepare).RequirePermission(Permissions.OperationsPrepare);
        g.MapPut("/{id:guid}/checks/{itemId:guid}", Tick).RequirePermission(Permissions.OperationsPrepare);
        g.MapGet("/{id:guid}/apply", OperationApply.Preview).RequirePermission(Permissions.OperationsApply);
        g.MapPost("/{id:guid}/apply", OperationApply.Apply).RequirePermission(Permissions.OperationsApply);
        app.MapGet("/api/checklist", ChecklistEndpoints.List).RequirePermission(Permissions.OperationsView);
        app.MapPut("/api/checklist", ChecklistEndpoints.Save).RequirePermission(Permissions.SettingsManage);
    }

    // Ruling 10: run every check now and store the result with its time.
    private static async Task<IResult> Prepare(Guid id, AppDb db, Panel.Features.Profiles.ProfileStore store, Panel.Hubs.AgentRegistry registry,
        Panel.Hubs.AgentCalls agents, Notifier notifier)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return NotFound();
        var results = await Readiness.PrepareAsync(db, store, registry, agents, op);
        await Notices.NotifyIfReadyAsync(db, notifier, op);
        return Results.Ok(new { ready = op.ReadyCount, total = op.CheckCount, checkedAt = op.LastCheckAt, results });
    }

    public sealed record TickBody(bool Done);

    // A manual item's tick; a stored Prepare result is updated in place so the count stays true.
    private static async Task<IResult> Tick(Guid id, Guid itemId, TickBody body, AppDb db, AuditLog audit, HttpContext ctx, Notifier notifier)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return NotFound();
        if (await db.ChecklistItems.FindAsync(itemId) is not { AutoKey: null, Enabled: true } item)
            return ApiError.Of(400, "validation", "Only an enabled manual item can be ticked; the others are checked by Prepare.");
        var check = await db.OperationChecks.FindAsync(id, itemId);
        if (check is null) db.OperationChecks.Add(check = new OperationCheck { OperationId = id, ChecklistItemId = itemId });
        (check.Done, check.ById, check.At) = (body.Done, UserId(ctx), DateTime.UtcNow);
        if (op.LastCheckJson is { } json && JsonSerializer.Deserialize<CheckResult[]>(json, Json) is { } stored)
            Readiness.Store(op, stored.Select(r => r.ItemId == itemId ? r with { State = body.Done ? Readiness.Pass : Readiness.Fail,
                Detail = body.Done ? "Ticked." : "Not ticked yet." } : r).ToArray(), op.LastCheckAt);
        await db.SaveChangesAsync();
        await audit.RecordAsync("operation.check", $"{(body.Done ? "Ticked" : "Unticked")} \"{item.Label}\" on {op.Name}", "operation", id.ToString(),
            new { name = op.Name, item = item.Label, done = body.Done });
        await Notices.NotifyIfReadyAsync(db, notifier, op);
        return Results.Ok(new { ready = op.ReadyCount, total = op.CheckCount });
    }

    private static Guid? UserId(HttpContext ctx) => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var u) ? u : null;
    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such operation.");

    // Upcoming (planned, from six hours ago), the last 20 past ones with past=true, or a UTC range for the calendar.
    private static async Task<IResult> List(DateTime? from, DateTime? to, bool? past, AppDb db)
    {
        var since = DateTime.UtcNow.AddHours(-6);
        IQueryable<Operation> q = db.Operations;
        if (from is { } f && to is { } t)
        {
            if (t <= f || t - f > TimeSpan.FromDays(62)) return ApiError.Of(400, "validation", "Ask for a range of up to 62 days.");
            var (fu, tu) = (f.ToUniversalTime(), t.ToUniversalTime());
            q = q.Where(o => o.StartsAt >= fu && o.StartsAt < tu).OrderBy(o => o.StartsAt);
        }
        else if (past == true) q = q.Where(o => o.StartsAt < since || o.Status != OperationStatus.Planned).OrderByDescending(o => o.StartsAt).Take(20);
        else q = q.Where(o => o.StartsAt >= since && o.Status == OperationStatus.Planned).OrderBy(o => o.StartsAt).Take(100);
        var ops = await q.ToListAsync();
        var names = await Names.LoadAsync(db, ops);
        return Results.Ok(ops.Select(o => names.Row(o)).ToArray());
    }

    private static async Task<IResult> Get(Guid id, AppDb db, PermissionService permissions, HttpContext ctx)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return NotFound();
        var perms = UserId(ctx) is { } u ? await permissions.EffectiveAsync(u) : new HashSet<string>();
        var seesNotes = perms.Contains(Permissions.OperationsManage) || perms.Contains(Permissions.OperationsPrepare); // Ruling 5
        var names = await Names.LoadAsync(db, [op]);
        var row = names.Row(op);
        var ticked = await db.OperationChecks.Where(c => c.OperationId == id && c.Done).Select(c => c.ChecklistItemId).ToArrayAsync();
        var lastCheck = op.LastCheckJson is { } json ? JsonSerializer.Deserialize<CheckResult[]>(json, Json) : null;
        return Results.Ok(new OperationDto(op.Id, op.Name, op.StartsAt, row.Status, row.Mission, op.MissionVersionId, row.VersionNumber, row.Profile, row.ModList,
            row.MissionMaker, row.Zeus, row.Readiness, op.AppliedAt, op.AppliedById is { } a ? names.Person(a) : null, op.Description, op.Briefing,
            seesNotes ? op.Notes : null, op.Version, lastCheck, ticked));
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<IResult> People(AppDb db) =>
        Results.Ok((await db.Users.ToListAsync()).Where(u => !AuthEndpoints.IsDisabled(u)).OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(u => new Ref(u.Id, u.UserName ?? "")).ToArray());

    // The form's choices in one call, for people who can plan operations but not see the profile or mod pages themselves.
    private static async Task<IResult> Options(AppDb db)
    {
        var missions = await db.Missions.Where(m => !m.IsArchived).OrderBy(m => m.DisplayName).ToListAsync();
        var ids = missions.Select(m => m.Id).ToList();
        var versions = (await db.MissionVersions.Where(v => ids.Contains(v.MissionId)).Select(v => new { v.Id, v.MissionId, v.Number }).ToListAsync())
            .ToLookup(v => v.MissionId);
        return Results.Ok(new
        {
            missions = missions.Select(m => new
            {
                m.Id, name = m.DisplayName, m.Map, m.CurrentVersionId,
                versions = versions[m.Id].OrderByDescending(v => v.Number).Select(v => new { v.Id, v.Number }),
            }),
            profiles = await db.Profiles.OrderBy(p => p.Name).Select(p => new Ref(p.Id, p.Name)).ToListAsync(),
            modLists = await db.ModCollections.OrderBy(c => c.Name).Select(c => new Ref(c.Id, c.Name)).ToListAsync(),
            people = (await db.Users.ToListAsync()).Where(u => !AuthEndpoints.IsDisabled(u)).OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new Ref(u.Id, u.UserName ?? "")),
        });
    }

    private static async Task<IResult> Create(OperationBody body, AppDb db, AuditLog audit, HttpContext ctx, Notifier notifier)
    {
        var op = new Operation { Id = Guid.NewGuid(), CreatedById = UserId(ctx) ?? Guid.Empty };
        if (await ApplyBodyAsync(op, body, db) is { } error) return error;
        db.Operations.Add(op);
        await SetZeusAsync(db, op.Id, body.ZeusIds);
        await db.SaveChangesAsync();
        await audit.RecordAsync("operation.created", $"Planned {op.Name} for {op.StartsAt:yyyy-MM-dd HH:mm} UTC", "operation", op.Id.ToString(),
            new { name = op.Name, startsAt = op.StartsAt }, isPublic: true);
        notifier.Enqueue(await Notices.OperationCreatedAsync(db, op));
        return Results.Created($"/api/operations/{op.Id}", new { id = op.Id });
    }

    // Ruling 6: the edit carries the version it was made on. Any edit clears the stored readiness (Ruling 9); changing what runs
    // (mission, version, profile, mod list) also clears the manual ticks, which were about the old setup.
    private static async Task<IResult> Update(Guid id, OperationBody body, AppDb db, AuditLog audit)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return NotFound();
        if (body.Version != op.Version) return ApiError.Of(409, "stale", "Someone changed this operation meanwhile. Reload it before saving.");
        var before = (op.MissionId, op.MissionVersionId, op.ProfileId, op.ModCollectionId);
        var startsBefore = op.StartsAt;
        if (await ApplyBodyAsync(op, body, db) is { } error) return error;
        if (op.StartsAt != startsBefore) op.ReminderSentAt = null; // Phase 6b Ruling 6: a moved op is reminded at its new time
        op.ReadyNotifiedAt = null;                                  // Ruling 7: an edit re-arms "ready", as it clears the stored result
        if (before != (op.MissionId, op.MissionVersionId, op.ProfileId, op.ModCollectionId))
            await db.OperationChecks.Where(c => c.OperationId == id).ExecuteDeleteAsync();
        (op.LastCheckJson, op.LastCheckAt, op.ReadyCount, op.CheckCount) = (null, null, 0, 0);
        op.Version++;
        op.UpdatedAt = DateTime.UtcNow;
        await db.OperationZeus.Where(z => z.OperationId == id).ExecuteDeleteAsync();
        await SetZeusAsync(db, id, body.ZeusIds);
        await db.SaveChangesAsync();
        await audit.RecordAsync("operation.updated", $"Edited {op.Name}", "operation", id.ToString(), new { name = op.Name, startsAt = op.StartsAt });
        return Results.Ok(new { version = op.Version });
    }

    private static async Task<IResult> SetStatus(Guid id, StatusBody body, AppDb db, AuditLog audit, Notifier notifier)
    {
        if (await db.Operations.FindAsync(id) is not { } op) return NotFound();
        if (!Enum.TryParse<OperationStatus>(body.Status, out var status)) return ApiError.Field("status", "Planned, Done or Cancelled.");
        var was = op.Status;
        (op.Status, op.UpdatedAt) = (status, DateTime.UtcNow);
        if (status == OperationStatus.Planned && was != OperationStatus.Planned) (op.ReminderSentAt, op.ReadyNotifiedAt) = (null, null);
        op.Version++;
        await db.SaveChangesAsync();
        await audit.RecordAsync("operation.status", $"Marked {op.Name} {status}", "operation", id.ToString(), new { name = op.Name, status = status.ToString() },
            isPublic: true);
        if (was == OperationStatus.Planned && status == OperationStatus.Cancelled) notifier.Enqueue(Notices.OperationCancelled(op));
        return Results.Ok(new { version = op.Version });
    }

    private static async Task<IResult?> ApplyBodyAsync(Operation op, OperationBody b, AppDb db)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length is 0 or > 120) return ApiError.Field("name", "Name it, in up to 120 characters.");
        if (b.StartsAt is not { } startsAt) return ApiError.Field("startsAt", "Pick a date and time.");
        if ((b.Description ?? "").Length > 2000) return ApiError.Field("description", "Up to 2,000 characters.");
        if ((b.Notes ?? "").Length > 10000) return ApiError.Field("notes", "Up to 10,000 characters.");
        if ((b.Briefing ?? "").Length > 20000) return ApiError.Field("briefing", "Up to 20,000 characters.");
        Guid? versionId = null;
        if (b.MissionId is { } missionId)
        {
            if (await db.Missions.FindAsync(missionId) is not { } mission) return ApiError.Field("missionId", "No such mission.");
            versionId = b.MissionVersionId ?? mission.CurrentVersionId;
            if (versionId is { } v && !await db.MissionVersions.AnyAsync(x => x.Id == v && x.MissionId == missionId))
                return ApiError.Field("missionVersionId", "That version isn't one of this mission's.");
        }
        else if (b.MissionVersionId is not null) return ApiError.Field("missionVersionId", "Pick the mission first.");
        if (b.ProfileId is { } p && !await db.Profiles.AnyAsync(x => x.Id == p)) return ApiError.Field("profileId", "No such server profile.");
        if (b.ModListId is { } l && !await db.ModCollections.AnyAsync(x => x.Id == l)) return ApiError.Field("modListId", "No such mod list.");
        if (b.MissionMakerId is { } mm && !await db.Users.AnyAsync(x => x.Id == mm)) return ApiError.Field("missionMakerId", "No such member.");
        var zeus = b.ZeusIds ?? [];
        if (zeus.Length > 10) return ApiError.Field("zeusIds", "Up to 10 Zeus.");
        if (zeus.Length > 0 && await db.Users.CountAsync(u => zeus.Contains(u.Id)) != zeus.Distinct().Count()) return ApiError.Field("zeusIds", "No such member.");
        (op.Name, op.Description, op.StartsAt, op.MissionId, op.MissionVersionId, op.ProfileId, op.ModCollectionId, op.MissionMakerId, op.Notes, op.Briefing) =
            (name, b.Description ?? "", startsAt.ToUniversalTime(), b.MissionId, versionId, b.ProfileId, b.ModListId, b.MissionMakerId, b.Notes ?? "", b.Briefing ?? "");
        return null;
    }

    private static async Task SetZeusAsync(AppDb db, Guid id, Guid[]? zeus)
    {
        foreach (var u in (zeus ?? []).Distinct()) db.OperationZeus.Add(new OperationZeus { OperationId = id, UserId = u });
        await Task.CompletedTask;
    }

    // The names a set of operations shows, loaded once.
    private sealed class Names
    {
        private Dictionary<Guid, Mission> _missions = [];
        private Dictionary<Guid, int> _versions = [];
        private Dictionary<Guid, string> _profiles = [], _lists = [], _people = [];
        private ILookup<Guid, Guid> _zeus = Array.Empty<OperationZeus>().ToLookup(z => z.OperationId, z => z.UserId);

        public static async Task<Names> LoadAsync(AppDb db, IReadOnlyCollection<Operation> ops)
        {
            var ids = ops.Select(o => o.Id).ToList();
            var zeus = await db.OperationZeus.Where(z => ids.Contains(z.OperationId)).ToListAsync();
            var missionIds = ops.Select(o => o.MissionId).OfType<Guid>().Distinct().ToList();
            var versionIds = ops.Select(o => o.MissionVersionId).OfType<Guid>().Distinct().ToList();
            var people = zeus.Select(z => z.UserId).Concat(ops.Select(o => o.MissionMakerId).OfType<Guid>()).Concat(ops.Select(o => o.AppliedById).OfType<Guid>())
                .Distinct().ToList();
            return new Names
            {
                _zeus = zeus.ToLookup(z => z.OperationId, z => z.UserId),
                _missions = await db.Missions.Where(m => missionIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id),
                _versions = await db.MissionVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number),
                _profiles = await db.Profiles.ToDictionaryAsync(p => p.Id, p => p.Name),
                _lists = await db.ModCollections.ToDictionaryAsync(c => c.Id, c => c.Name),
                _people = await db.Users.Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? ""),
            };
        }

        public Ref? Person(Guid id) => _people.TryGetValue(id, out var n) ? new Ref(id, n) : null;

        public OperationRow Row(Operation o) => new(o.Id, o.Name, o.StartsAt, o.Status.ToString(),
            o.MissionId is { } m && _missions.TryGetValue(m, out var mission) ? new MissionRef(m, mission.DisplayName, mission.Map, mission.FileName, mission.IsArchived) : null,
            o.MissionVersionId is { } v && _versions.TryGetValue(v, out var n) ? n : null,
            o.ProfileId is { } p && _profiles.TryGetValue(p, out var pn) ? new Ref(p, pn) : null,
            o.ModCollectionId is { } l && _lists.TryGetValue(l, out var ln) ? new Ref(l, ln) : null,
            o.MissionMakerId is { } mm ? Person(mm) : null,
            _zeus[o.Id].Select(Person).OfType<Ref>().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            o.LastCheckAt is { } at ? new ReadinessDto(o.ReadyCount, o.CheckCount, at) : null,
            o.AppliedAt);
    }
}
