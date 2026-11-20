using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ServerAgent;

// Used only when Agent:InstallRoots is empty. On the box, listing InstallRoots explicitly is the expected setup.
public static partial class InstallDetector
{
    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex PathEntry();

    public static string[] ParseLibraryFolders(string vdf) =>
        PathEntry().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToArray();

    public static string[] Detect()
    {
        if (!OperatingSystem.IsWindows()) return [];
        // A service account's HKCU has no Steam, so fall back to the machine-wide install path.
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrEmpty(steam)) return [];

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? ParseLibraryFolders(File.ReadAllText(vdf)).Append(steam) : [steam];
        return libraries
            .SelectMany(lib => new[] { "Arma 3 Server", "Arma 3" }.Select(name => Path.GetFullPath(Path.Combine(lib, "steamapps", "common", name))))
            .Where(dir => File.Exists(Path.Combine(dir, "arma3server_x64.exe")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
