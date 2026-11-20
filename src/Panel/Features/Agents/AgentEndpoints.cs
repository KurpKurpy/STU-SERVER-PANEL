using System.Text;
using System.Text.RegularExpressions;
using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Hubs;

namespace Panel.Features.Agents;

public record AgentDto(Guid Id, string Name, bool Connected, string? MachineName, string? Version, DateTime? LastSeenAt,
    InstallInfo[] Installs, AreaInfo[] Areas);
public record AgentCreate(string? Name);
public record AgentCreated(Guid Id, string Name, string Key);
public record AgentKey(string Key);
public record CfgFileDto(string Area, string Name, long Size, DateTime ModifiedAt);

public static partial class AgentEndpoints
{
    public static void MapAgents(this WebApplication app)
    {
        var g = app.MapGroup("/api/agents");
        g.MapGet("", List).RequirePermission(Permissions.AgentsManage);
        g.MapPost("", Create).RequirePermission(Permissions.AgentsManage);
        g.MapPost("/{id:guid}/key", RotateKey).RequirePermission(Permissions.AgentsManage);
        g.MapDelete("/{id:guid}", Delete).RequirePermission(Permissions.AgentsManage);
        g.MapGet("/{id:guid}/cfg-files", CfgFiles).RequirePermission(Permissions.ProfilesManage);
    }

    private static async Task<AgentDto[]> List(AppDb db, AgentRegistry registry) =>
        (await db.Agents.OrderBy(a => a.CreatedAt).ToListAsync()).Select(a =>
        {
            var hello = registry.Hello(a.Id);
            return new AgentDto(a.Id, a.Name, registry.IsConnected(a.Id), a.MachineName, a.Version, a.LastSeenAt,
                hello?.Installs ?? [], hello?.Areas ?? []);
        }).ToArray();

    private static async Task<IResult> Create(AgentCreate r, AppDb db, AuditLog audit)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 40) return ApiError.Of(400, "validation", "Name must be 1–40 characters.");
        var agent = new Agent { Id = Guid.NewGuid(), Name = name };
        var key = AgentKeyAuthentication.NewKey(agent.Id, out var hash);
        agent.KeyHash = hash;
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        await audit.RecordAsync("agent.created", $"Added server agent {name}", "agent", agent.Id.ToString());
        return Results.Created($"/api/agents/{agent.Id}", new AgentCreated(agent.Id, name, key));
    }

    private static async Task<IResult> RotateKey(Guid id, AppDb db, AgentRegistry registry, AuditLog audit)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return ApiError.Of(404, "not_found", "No such agent.");
        var key = AgentKeyAuthentication.NewKey(agent.Id, out var hash);
        agent.KeyHash = hash;
        await db.SaveChangesAsync();
        registry.Disconnect(id);
        await audit.RecordAsync("agent.keyRotated", $"Issued a new key for server agent {agent.Name}", "agent", id.ToString());
        return Results.Ok(new AgentKey(key));
    }

    private static async Task<IResult> Delete(Guid id, AppDb db, AgentRegistry registry, AuditLog audit)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return ApiError.Of(404, "not_found", "No such agent.");
        if (await db.Installs.AnyAsync(i => i.AgentId == id)) return ApiError.Of(409, "in_use", "Remove this agent's server installs first.");
        db.Agents.Remove(agent);
        await db.SaveChangesAsync();
        registry.Disconnect(id);
        await audit.RecordAsync("agent.deleted", $"Removed server agent {agent.Name}", "agent", id.ToString());
        return Results.NoContent();
    }

    // Existing server.cfg / basic.cfg files the setup wizard can import: the top level of each install and of the configs area.
    private static async Task<IResult> CfgFiles(Guid id, AgentRegistry registry, AgentCalls agents)
    {
        if (registry.Hello(id) is not { } hello) return ApiError.Of(409, "agent_offline", "The server agent isn't connected.");
        var found = new List<CfgFileDto>();
        try
        {
            foreach (var area in hello.Installs.Select(i => i.Area).Append("configs"))
            {
                try { found.AddRange((await agents.CallAsync(id, c => c.ListFiles(area, "", "*.cfg"))).Select(f => new CfgFileDto(area, f.Name, f.Size, f.ModifiedAt))); }
                catch (HubException) { } // an area the agent can't list right now (e.g. a missing folder) just has no files
            }
        }
        catch (AgentUnavailableException e) { return ApiError.Of(409, "agent_offline", e.Message); }
        return Results.Ok(found);
    }
}