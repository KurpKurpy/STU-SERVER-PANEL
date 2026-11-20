using System.Text.Json;

namespace ServerAgent;

// QueryHost: the -ip= custom arg, when set, since the server then answers A2S only on that address.
public sealed record StateEntry(int Pid, DateTime StartedAt, string Slug, int Port, string InstallPath, string Executable, string? QueryHost = null,
    HcState[]? Headless = null);

// A headless client started beside the server; re-attached with it (plan Ruling 13).
public sealed record HcState(int Index, int Pid, DateTime StartedAt);

// <DataRoot>\agent-state.json: what the agent launched, so a restarted agent re-attaches instead of losing the server.
// IO errors are the caller's to handle (Supervisor retries reads and logs failed writes).
public static class StateFile
{
    public static Dictionary<Guid, StateEntry> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<Guid, StateEntry>>(File.ReadAllText(path)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public static void Save(string path, IReadOnlyDictionary<Guid, StateEntry> entries)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, path, overwrite: true);
    }
}
