using System.Net;
using System.Net.Sockets;
using System.Text;
using Contracts;

namespace ServerAgent.Games.Arma3;

// Steam A2S_INFO. Arma 3 2.22 answers without a challenge (spike F7) but newer Steam servers may ask for one.
// No answer, an ICMP "port unreachable" (spike F12: instant SocketException on Windows) or a malformed reply → null.
public static class A2sClient
{
    private static readonly byte[] Query = [0xFF, 0xFF, 0xFF, 0xFF, .. "TSource Engine Query\0"u8];

    public static async Task<QueryInfo?> QueryAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var udp = new UdpClient(endpoint.AddressFamily);
        try
        {
            udp.Connect(endpoint);
            await udp.SendAsync(Query, cts.Token);
            var reply = (await udp.ReceiveAsync(cts.Token)).Buffer;
            if (reply.Length == 9 && IsHeader(reply) && reply[4] == 0x41)
            {
                byte[] withChallenge = [.. Query, .. reply.AsSpan(5, 4)];
                await udp.SendAsync(withChallenge.AsMemory(), cts.Token);
                reply = (await udp.ReceiveAsync(cts.Token)).Buffer;
            }
            return Parse(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }

    private static bool IsHeader(byte[] b) => b.Length >= 5 && b[0] == 0xFF && b[1] == 0xFF && b[2] == 0xFF && b[3] == 0xFF;

    private static QueryInfo? Parse(byte[] b)
    {
        if (!IsHeader(b) || b[4] != 0x49) return null;
        var i = 6; // header, type, protocol
        string? Str()
        {
            var end = Array.IndexOf(b, (byte)0, i);
            if (end < 0) return null;
            var s = Encoding.UTF8.GetString(b, i, end - i);
            i = end + 1;
            return s;
        }
        var name = Str();
        var map = Str();
        var folder = Str();
        var game = Str();
        if (name is null || map is null || folder is null || game is null || i + 4 > b.Length) return null;
        i += 2; // app id
        // Spike F8: the "game" field carries the mission (it reads "Waiting" with no mission loaded).
        return new QueryInfo(name, map, game, b[i], b[i + 1]);
    }
}
