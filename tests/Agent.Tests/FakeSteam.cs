using System.Text.Json;

namespace Agent.Tests;

// The fake SteamCMD (tests/FakeSteamCmd, built as steamcmd.exe) copied into a throwaway folder with its scenario file.
internal static class FakeSteam
{
    public static string Create(object? scenario = null)
    {
        var testBin = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)); // tests/Agent.Tests/bin/<Configuration>/net10.0
        var source = Path.GetFullPath(Path.Combine(testBin.FullName, "..", "..", "..", "..", "FakeSteamCmd", "bin", testBin.Parent!.Name, testBin.Name));
        if (!File.Exists(Path.Combine(source, "steamcmd.exe"))) throw new FileNotFoundException("FakeSteamCmd build output not found", source);
        var dir = TempDir.Create();
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        if (scenario is not null)
            File.WriteAllText(Path.Combine(dir, "fake-steamcmd.json"), JsonSerializer.Serialize(scenario, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return Path.Combine(dir, "steamcmd.exe");
    }

    public static string[] ArgsSeen(string exe) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "fake-steamcmd-args.txt"))
            ? File.ReadAllLines(Path.Combine(Path.GetDirectoryName(exe)!, "fake-steamcmd-args.txt"))
            : [];
}
