using System.IO.Compression;

namespace ServerAgent.Steam;

// Puts SteamCMD in place from Valve's own zip when it's missing (Phase 5a Ruling 4).
public sealed class SteamCmdInstaller(HttpClient http)
{
    public const string DefaultUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    // True when it was just installed: the caller then lets SteamCMD update itself first (spike S1).
    public async Task<bool> EnsureAsync(string exe, CancellationToken ct)
    {
        if (File.Exists(exe)) return false;
        var folder = Path.GetDirectoryName(exe)!;
        Directory.CreateDirectory(folder);
        var bytes = await http.GetByteArrayAsync(DefaultUrl, ct);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            zip.ExtractToDirectory(folder, overwriteFiles: true); // .NET refuses entries that would land outside the folder
        if (!File.Exists(exe)) throw new InvalidOperationException($"Valve's steamcmd.zip didn't contain {Path.GetFileName(exe)}.");
        return true;
    }
}
