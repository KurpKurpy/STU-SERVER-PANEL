using System.Text.RegularExpressions;

namespace Contracts;

// Custom launch arguments are an allowlist (Bohemia's "Arma 3: Startup Parameters"): names whose values cannot carry a path.
// Checked by the panel when a profile is saved and again by the agent before every launch.
public static partial class Arma3LaunchArgs
{
    public static readonly IReadOnlyCollection<string> Flags =
        ["-autoInit", "-loadMissionToMemory", "-enableHT", "-hugePages", "-filePatching", "-netlog", "-noSound", "-noLogs"];

    public static readonly IReadOnlyCollection<string> Valued =
        ["-maxMem", "-cpuCount", "-exThreads", "-limitFPS", "-bandwidthAlg", "-malloc", "-world", "-ip"];

    private static readonly string[] Generated = ["-port", "-config", "-cfg", "-profiles", "-name"];

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeValue();

    public static string[] Parse(string? text) => (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static string? Validate(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            var name = eq < 0 ? arg : arg[..eq];
            if (Generated.Contains(name, StringComparer.OrdinalIgnoreCase))
                return $"\"{arg}\": {name} is set by the agent from the profile.";
            if (eq < 0)
            {
                if (Flags.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                return Valued.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? $"\"{arg}\" needs a value, for example {name}=4."
                    : $"\"{arg}\" isn't an allowed launch argument.";
            }
            if (!Valued.Contains(name, StringComparer.OrdinalIgnoreCase))
                return Flags.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? $"\"{arg}\": {name} takes no value."
                    : $"\"{arg}\" isn't an allowed launch argument.";
            if (!SafeValue().IsMatch(arg[(eq + 1)..]))
                return $"\"{arg}\": the value may only use letters, digits, '.', '_' and '-'.";
        }
        return null;
    }
}
