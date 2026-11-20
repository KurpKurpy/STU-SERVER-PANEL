using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Hubs;

namespace Panel.Features.Players;

// Phase 7a Rulings 7–11: players, bans, kick/ban/unban/say, allowlisted RCon commands and the RCon log, all through the agent's RCon
// session. Every action naming a player or ban number re-reads the list first and refuses unless that number still shows that GUID.
public static partial class PlayerEndpoints
{
    public sealed record PlayerDto(int Number, string Name, int Ping, bool Lobby, DateTime? FirstSeen, string? Guid, bool? Verified, string? Ip);
    public sealed record PlayersDto(string Rcon, string? RconError, string? Unavailable, PlayerDto[] Players, string[]? Unparsed);
    public sealed record BansDto(string? Unavailable, BanRow[] Bans, string[] Unparsed);
    public sealed record KickBody(int Number, string? Guid, string? Reason);
    public sealed record BanBody(int Number, string? Guid, int Minutes, string? Reason);
    public sealed record UnbanBody(int Number, string? Target);
    public sealed record SayBody(string? Message, int? Number, string? Guid);
    public sealed record CommandBody(string? Command);

    private static readonly string[] Allowed = ["players", "bans", "admins", "missions", "version", "say", "loadBans", "writeBans", "#lock", "#unlock"];
    private const int MaxReason = 120, MaxMessage = 200, MaxMinutes = 525600;

    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex GuidShape();
    [GeneratedRegex("^[0-9a-f]{32}$|^\\d{1,3}(\\.\\d{1,3}){3}$")] private static partial Regex TargetShape();
    [GeneratedRegex(" {2,}")] private static partial Regex Spaces();

    // Final review I2: re-check and act as one step per server, or two quick actions both pass their re-check and the second lands on
    // whoever shifted into its number (ban numbers shift after every removal).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private sealed class Held(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
    private static async Task<IDisposable> HoldAsync(Guid id)
    {
        var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Held(gate);
    }

    public static void MapPlayers(this WebApplication app)
    {
        var g = app.MapGroup("/api/servers/{id:guid}");
        g.MapGet("/players", Players).RequirePermission(Permissions.ServerView);
        g.MapPost("/players/kick", Kick).RequirePermission(Permissions.PlayersManage);
        g.MapPost("/players/ban", Ban).RequirePermission(Permissions.PlayersManage);
        g.MapGet("/bans", ListBans).RequirePermission(Permissions.PlayersManage);
        g.MapPost("/bans/remove", Unban).RequirePermission(Permissions.PlayersManage);
        g.MapPost("/say", Say).RequirePermission(Permissions.PlayersManage);
        // Final review I3: players, bans and admins print every IP and GUID, so the command box also needs players.manage.
        g.MapPost("/rcon", Command).RequirePermission(Permissions.ServerConsole).RequirePermission(Permissions.PlayersManage);
        g.MapGet("/rcon/log", (Guid id, RconLog log) => log.Get(id)).RequirePermission(Permissions.PlayersManage);
        app.MapPost("/api/profiles/{id:guid}/rcon-password", RenewPassword).RequirePermission(Permissions.ProfilesManage);
    }

    // Control characters (line breaks included) become spaces, runs of spaces one, then the length is capped (Review Focus 2).
    internal static string Clean(string? s, int max)
    {
        var t = Spaces().Replace(new string((s ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()), " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    private static string User(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "someone";

    private static async Task<(Profile? Profile, Guid AgentId)> FindAsync(AppDb db, Guid id) =>
        await (from p in db.Profiles join i in db.Installs on p.ServerInstallId equals i.Id where p.Id == id select new { p, i.AgentId })
            .FirstOrDefaultAsync() is { } row ? (row.p, row.AgentId) : (null, Guid.Empty);

    private static async Task<RconReply> RconAsync(AgentCalls agents, Guid agentId, Guid profileId, string command)
    {
        try { return await agents.CallAsync(agentId, c => c.Rcon(profileId, command)); }
        catch (AgentUnavailableException) { return new RconReply(false, null, "The server agent isn't connected."); }
        catch (HubException) { return new RconReply(false, null, "The server agent couldn't run that."); }
    }

    private static IResult NotFound() => ApiError.Of(404, "not_found", "No such server.");
    private static IResult Unavailable(RconReply r) => ApiError.Of(409, "rcon_unavailable", r.Error ?? "RCon isn't available.");

    private static async Task<IResult> Players(Guid id, AppDb db, AgentCalls agents, AgentRegistry registry, RconLog log, PermissionService perms, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        var status = registry.Status(id);
        var state = status?.Rcon ?? RconState.Off;
        var error = status?.RconError ?? (state == RconState.Off && status is { State: ServerState.Online or ServerState.NotResponding }
            ? "BattlEye is off for this server, or it was started before RCon was set up." : null);
        var reply = await RconAsync(agents, agentId, id, "players");
        if (!reply.Ok) return Results.Ok(new PlayersDto(state.ToString(), error, reply.Error ?? "RCon isn't available.", [], null));
        var manage = Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var me) && (await perms.EffectiveAsync(me)).Contains(Permissions.PlayersManage);
        var (rows, unparsed) = PlayerList.Players(reply.Text ?? "");
        return Results.Ok(new PlayersDto(state.ToString(), error, null,
            rows.Zip(log.FirstSeen(id, status?.StartedAt, rows.Select(r => r.Guid).ToArray(), DateTime.UtcNow), (r, seen) => new PlayerDto(r.Number, r.Name, r.Ping, r.Lobby, seen,
                manage ? r.Guid : null, manage ? r.Verified : null, manage ? r.Ip : null)).ToArray(),
            manage && unparsed.Length > 0 ? unparsed : null));
    }

    // Review Focus 1: the number must still belong to that GUID, or the action would land on whoever took the number since.
    private static async Task<(PlayerRow? Row, IResult? Error)> CheckPlayerAsync(AgentCalls agents, Guid agentId, Guid id, int number, string? guid)
    {
        if (number is < 0 or > 999) return (null, ApiError.Field("number", "That isn't a player number."));
        if (guid is null || !GuidShape().IsMatch(guid)) return (null, ApiError.Field("guid", "That isn't a BattlEye GUID."));
        var reply = await RconAsync(agents, agentId, id, "players");
        if (!reply.Ok) return (null, Unavailable(reply));
        var row = PlayerList.Players(reply.Text ?? "").Rows.FirstOrDefault(r => r.Number == number);
        return row is null || row.Guid != guid
            ? (null, ApiError.Of(409, "changed", "That player left or changed number. Refresh the list and try again."))
            : (row, null);
    }

    private static async Task<IResult> Kick(Guid id, KickBody body, AppDb db, AgentCalls agents, AuditLog audit, RconLog log, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        using var held = await HoldAsync(id);
        var (row, error) = await CheckPlayerAsync(agents, agentId, id, body.Number, body.Guid);
        if (error is not null) return error;
        var reason = Clean(body.Reason, MaxReason);
        var reply = await RconAsync(agents, agentId, id, reason.Length > 0 ? $"kick {body.Number} {reason}" : $"kick {body.Number}");
        if (!reply.Ok) return Unavailable(reply);
        log.Add(id, DateTime.UtcNow, $"{User(ctx)} kicked {row!.Name}{(reason.Length > 0 ? $": {reason}" : "")}");
        await audit.RecordAsync("players.kick", $"Kicked {row.Name} from {p.Name}", "profile", id.ToString(), new { name = row.Name, guid = row.Guid[..8], reason });
        return Results.Ok();
    }

    private static async Task<IResult> Ban(Guid id, BanBody body, AppDb db, AgentCalls agents, AuditLog audit, RconLog log, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        if (body.Minutes is < 0 or > MaxMinutes) return ApiError.Field("minutes", "Minutes: 0 (permanent) to 525600 (a year).");
        using var held = await HoldAsync(id);
        var (row, error) = await CheckPlayerAsync(agents, agentId, id, body.Number, body.Guid);
        if (error is not null) return error;
        var reason = Clean(body.Reason, MaxReason);
        var reply = await RconAsync(agents, agentId, id, $"ban {body.Number} {body.Minutes}{(reason.Length > 0 ? $" {reason}" : "")}");
        if (!reply.Ok) return Unavailable(reply);
        var length = body.Minutes == 0 ? "permanently" : $"for {body.Minutes} minutes";
        log.Add(id, DateTime.UtcNow, $"{User(ctx)} banned {row!.Name} {length}{(reason.Length > 0 ? $": {reason}" : "")}");
        await audit.RecordAsync("players.ban", $"Banned {row.Name} from {p.Name} {length}", "profile", id.ToString(),
            new { name = row.Name, guid = row.Guid[..8], minutes = body.Minutes, reason });
        return Results.Ok();
    }

    private static async Task<IResult> ListBans(Guid id, AppDb db, AgentCalls agents)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        var reply = await RconAsync(agents, agentId, id, "bans");
        if (!reply.Ok) return Results.Ok(new BansDto(reply.Error ?? "RCon isn't available.", [], []));
        var (rows, unparsed) = PlayerList.Bans(reply.Text ?? "");
        return Results.Ok(new BansDto(null, rows, unparsed));
    }

    private static async Task<IResult> Unban(Guid id, UnbanBody body, AppDb db, AgentCalls agents, AuditLog audit, RconLog log, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        if (body.Number is < 0 or > 99999) return ApiError.Field("number", "That isn't a ban number.");
        if (body.Target is null || !TargetShape().IsMatch(body.Target)) return ApiError.Field("target", "That isn't a GUID or IP address.");
        using var held = await HoldAsync(id);
        var listing = await RconAsync(agents, agentId, id, "bans");
        if (!listing.Ok) return Unavailable(listing);
        var row = PlayerList.Bans(listing.Text ?? "").Rows.FirstOrDefault(b => b.Number == body.Number);
        if (row is null || row.Target != body.Target)
            return ApiError.Of(409, "changed", "The ban list changed. Refresh it and try again.");
        var reply = await RconAsync(agents, agentId, id, $"removeBan {body.Number}");
        if (!reply.Ok) return Unavailable(reply);
        var shown = row.Kind == "guid" ? row.Target[..8] + "…" : row.Target;
        log.Add(id, DateTime.UtcNow, $"{User(ctx)} removed the ban on {shown}");
        await audit.RecordAsync("players.unban", $"Removed a ban on {p.Name}", "profile", id.ToString(), new { target = shown, reason = row.Reason });
        return Results.Ok();
    }

    private static async Task<IResult> Say(Guid id, SayBody body, AppDb db, AgentCalls agents, AuditLog audit, RconLog log, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        var message = Clean(body.Message, MaxMessage);
        if (message.Length == 0) return ApiError.Field("message", "Write the message.");
        using var held = await HoldAsync(id);
        string to = "everyone", target = "-1";
        if (body.Number is { } number)
        {
            var (row, error) = await CheckPlayerAsync(agents, agentId, id, number, body.Guid);
            if (error is not null) return error;
            (to, target) = (row!.Name, number.ToString());
        }
        var reply = await RconAsync(agents, agentId, id, $"say {target} {message}");
        if (!reply.Ok) return Unavailable(reply);
        log.Add(id, DateTime.UtcNow, $"{User(ctx)} said to {to}: {message}");
        await audit.RecordAsync("players.say", $"Sent a message to {to} on {p.Name}", "profile", id.ToString(), new { to, message });
        return Results.Ok();
    }

    // Ruling 9: BattlEye also takes config lines over RCon (MaxPing, loadScripts, …), so only these first words go through.
    private static async Task<IResult> Command(Guid id, CommandBody body, AppDb db, AgentCalls agents, AuditLog audit, RconLog log, HttpContext ctx)
    {
        var (p, agentId) = await FindAsync(db, id);
        if (p is null) return NotFound();
        var command = (body.Command ?? "").Trim();
        if (command.Length is 0 or > MaxMessage + 20 || command.Any(char.IsControl))
            return ApiError.Field("command", "One line, up to 220 characters.");
        var first = command.Split(' ', 2)[0];
        if (!Allowed.Contains(first, StringComparer.OrdinalIgnoreCase))
            return ApiError.Field("command", $"\"{first}\" isn't allowed here. Allowed: {string.Join(", ", Allowed)}.");
        var reply = await RconAsync(agents, agentId, id, command);
        if (!reply.Ok) return Unavailable(reply);
        log.Add(id, DateTime.UtcNow, $"{User(ctx)} ran: {command}");
        await audit.RecordAsync("rcon.command", $"Ran an RCon command on {p.Name}", "profile", id.ToString(), new { command });
        return Results.Ok(new { text = reply.Text ?? "" });
    }

    private static async Task<IResult> RenewPassword(Guid id, AppDb db, IDataProtectionProvider dp, AuditLog audit)
    {
        if (await db.Profiles.FindAsync(id) is not { } p) return ApiError.Of(404, "not_found", "No such profile.");
        RconSettings.Renew(p, dp);
        await db.SaveChangesAsync();
        await audit.RecordAsync("profile.rcon_password", $"Made a new RCon password for {p.Name} (used from its next start)", "profile", id.ToString());
        return Results.Ok(new { set = true });
    }
}
