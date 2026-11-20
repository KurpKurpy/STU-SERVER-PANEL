using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Panel.Data;

namespace Panel.Hubs;

// "Authorization: AgentKey <agentId:N>.<base64url secret>" — used only by /hubs/agent.
public sealed class AgentKeyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    public const string AgentIdClaim = "agent_id";

    public static string NewKey(Guid agentId, out string hash)
    {
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        hash = Hash(secret);
        return $"{agentId:N}.{secret}";
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var parts = header[(SchemeName.Length + 1)..].Trim().Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var agentId)) return AuthenticateResult.Fail("Malformed agent key");

        var agent = await db.Agents.FindAsync(agentId);
        if (agent is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(agent.KeyHash), Encoding.ASCII.GetBytes(Hash(parts[1]))))
            return AuthenticateResult.Fail("Unknown agent key");

        var identity = new ClaimsIdentity([new Claim(AgentIdClaim, agentId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
