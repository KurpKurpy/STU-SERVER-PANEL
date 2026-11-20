using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Panel.Auth;

public sealed record PanelPaths(string DataDir)
{
    public string SetupTokenFile => Path.Combine(DataDir, "setup-token.txt");
}

// One-time Owner bootstrap token. Only its hash is kept in memory; the plain token goes to the
// data directory and the log so whoever runs the server can claim it.
public sealed class SetupState
{
    private readonly Lock _lock = new();
    private byte[]? _hash;

    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Needed { get { lock (_lock) return _hash is not null; } }

    public string Issue(PanelPaths paths)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (_lock) _hash = Hash(token);
        File.WriteAllText(paths.SetupTokenFile, token);
        return token;
    }

    public bool Matches(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_lock) return _hash is not null && CryptographicOperations.FixedTimeEquals(_hash, Hash(token.Trim()));
    }

    public void Complete(PanelPaths paths)
    {
        lock (_lock) _hash = null;
        File.Delete(paths.SetupTokenFile);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
