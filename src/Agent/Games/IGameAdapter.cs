using Contracts;

namespace ServerAgent.Games;

// Where one of a profile's config files lives: (area, relative path). The panel never names it.
public sealed record ConfigTarget(string Name, string Area, string RelativePath);

// Design §5.3 seam (spec §21). Arma3Adapter is the only implementation.
public interface IGameAdapter
{
    IReadOnlyCollection<string> AllowedExecutables { get; }
    IReadOnlyList<ConfigTarget> ConfigTargets(string slug);
    IReadOnlyList<string> BuildArguments(LaunchSpec spec, string configDir, string profileDir, Func<ModRef, string> modPath);
    int QueryPort(int gamePort);
    string LogPattern { get; }
}
