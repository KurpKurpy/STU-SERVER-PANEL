using Contracts;

namespace ServerAgent.Games.Arma3;

public sealed class Arma3Adapter : IGameAdapter
{
    public IReadOnlyCollection<string> AllowedExecutables { get; } = ["arma3server_x64.exe", "arma3serverprofiling_x64.exe"];

    // The server reads its difficulty from <profiles>\Users\<name>\<name>.Arma3Profile (spike F20).
    public IReadOnlyList<ConfigTarget> ConfigTargets(string slug) =>
    [
        new("server.cfg", "configs", Path.Combine(slug, "server.cfg")),
        new("basic.cfg", "configs", Path.Combine(slug, "basic.cfg")),
        new($"{slug}.Arma3Profile", "profiles", Path.Combine(slug, "Users", slug, $"{slug}.Arma3Profile")),
    ];

    public IReadOnlyList<string> BuildArguments(LaunchSpec spec, string configDir, string profileDir, Func<ModRef, string> modPath) =>
        Arma3Command.ServerArguments(spec, configDir, profileDir, modPath);

    public int QueryPort(int gamePort) => gamePort + 1; // spike F6

    public string LogPattern => "*.rpt";
}
