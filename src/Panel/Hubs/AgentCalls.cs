using Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Panel.Hubs;

public sealed class AgentUnavailableException() : Exception("The server agent isn't connected.");

// Panel → agent calls. An agent that isn't connected, drops mid-call or doesn't answer in time is "unavailable"
// (endpoints answer 409 agent_offline). An error the agent itself raised arrives as a HubException.
public sealed class AgentCalls(IHubContext<AgentHub, IAgentClient> hub, AgentRegistry registry)
{
    public async Task<T> CallAsync<T>(Guid agentId, Func<IAgentClient, Task<T>> call, TimeSpan? timeout = null)
    {
        var connectionId = registry.ConnectionId(agentId) ?? throw new AgentUnavailableException();
        try { return await call(hub.Clients.Client(connectionId)).WaitAsync(timeout ?? TimeSpan.FromSeconds(20)); }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) { throw new AgentUnavailableException(); }
    }
}
