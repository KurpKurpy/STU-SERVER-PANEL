using System.Net;
using System.Net.Sockets;

namespace Agent.Tests;

// The fake Arma server (tests/FakeArmaServer, built as arma3server_x64.exe) copied into a throwaway install root.
internal static class FakeServer
{
    public static string CreateInstallRoot()
    {
        var testBin = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)); // tests/Agent.Tests/bin/<Configuration>/net10.0
        var source = Path.GetFullPath(Path.Combine(testBin.FullName, "..", "..", "..", "..", "FakeArmaServer", "bin", testBin.Parent!.Name, testBin.Name));
        if (!File.Exists(Path.Combine(source, "arma3server_x64.exe"))) throw new FileNotFoundException("FakeArmaServer build output not found", source);
        var root = TempDir.Create();
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        return root;
    }

    // A port p >= 40000 where p and p + 1 are free for UDP.
    public static int FreePort()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var p = Random.Shared.Next(40000, 60000);
            if (IsFree(p) && IsFree(p + 1)) return p;
        }
        throw new InvalidOperationException("No free UDP port pair found");
    }

    private static bool IsFree(int port)
    {
        try { using var _ = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); return true; }
        catch (SocketException) { return false; }
    }
}
