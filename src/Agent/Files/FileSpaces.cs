using ArmaFormats;
using Contracts;

namespace ServerAgent.Files;

public sealed record ResolvedPath(string Root, string Full, string Relative, string BackupKey);

// Phase 4b Ruling 2: the file manager's spaces, the agent's own table. The panel names a space; the agent decides where it is,
// whether it may be written and which files may go there. The install root itself, its battleye folder and its DLLs are never a space.
public sealed class FileSpaces(Areas areas, Func<string, bool> isRunningSlug)
{
    private enum Writes { No, Any, NotExecutables, KeysOnly }
    private sealed record Def(bool PerInstall, Writes Writes, Func<string, string> Root);

    private readonly Dictionary<string, Def> _defs = new(StringComparer.Ordinal)
    {
        ["mpmissions"] = new(true, Writes.NotExecutables, install => Path.Combine(install, "mpmissions")),
        ["keys"] = new(true, Writes.KeysOnly, install => Path.Combine(install, "keys")),
        ["profiles"] = new(false, Writes.NotExecutables, _ => areas.Resolve("profiles", "")),
        ["localmods"] = new(false, Writes.Any, _ => areas.LocalMods),
        ["configs"] = new(false, Writes.No, _ => areas.Resolve("configs", "")),
        ["workshop"] = new(false, Writes.No, _ => areas.WorkshopContent),
        ["backups"] = new(false, Writes.No, _ => areas.Resolve("backups", "")),
    };

    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".lnk",
        ".reg", ".hta", ".cpl", ".sys", ".pif",
    };

    public IEnumerable<string> WritableRoots() =>
        _defs.Where(d => d.Value.Writes != Writes.No).SelectMany(d => d.Value.PerInstall
            ? areas.InstallRoots.Where(r => !areas.IsReadOnlyInstall(r)).Select(d.Value.Root)
            : [d.Value.Root("")]);

    public ResolvedPath Resolve(FileSpace space, string relative)
    {
        var (def, install) = Find(space);
        if (FileNames.Problem(relative ?? "") is { } problem) throw new AreaException(problem);
        var root = def.Root(install ?? "");
        var key = install is null ? space.Name : $"{space.Name}-{areas.InstallArea(install)}";
        return new ResolvedPath(root, Areas.ResolveUnder(root, relative), (relative ?? "").Replace('/', '\\'), key);
    }

    // What a change may touch: a writable space (and install), never the space root, the space's file types, and not a running
    // server's own profile files, which it rewrites when it exits (Ruling 9).
    public ResolvedPath ResolveForWrite(FileSpace space, string relative, bool isFile)
    {
        var (def, install) = Find(space);
        if (def.Writes == Writes.No) throw new AreaException($"The {space.Name} folder is read-only in the file manager.");
        if (install is not null && areas.IsReadOnlyInstall(install)) throw new AreaException(Areas.ReadOnlyMessage);
        var resolved = Resolve(space, relative);
        if (resolved.Relative.Length == 0) throw new AreaException("The folder itself can't be changed, only what's in it.");
        var ext = Path.GetExtension(resolved.Relative);
        switch (def.Writes)
        {
            case Writes.KeysOnly when !isFile || !ext.Equals(".bikey", StringComparison.OrdinalIgnoreCase):
                throw new AreaException("Only .bikey files go in the keys folder.");
            case Writes.NotExecutables when isFile && Executables.Contains(ext):
                throw new AreaException($"{ext} files can't be written to the {space.Name} folder.");
        }
        if (space.Name == "profiles" && RunningProfilePart(resolved.Relative) is { } slug)
            throw new AreaException($"Stop the {slug} server first: it rewrites its profile files when it exits.");
        return resolved;
    }

    public bool IsExecutableAllowed(FileSpace space) => Find(space).Def.Writes == Writes.Any;

    private string? RunningProfilePart(string relative)
    {
        var parts = relative.Split('\\');
        if (!isRunningSlug(parts[0])) return null;
        var touchesProfile = parts.Length == 1 || parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".Arma3Profile", StringComparison.OrdinalIgnoreCase);
        return touchesProfile ? parts[0] : null;
    }

    private (Def Def, string? Install) Find(FileSpace space)
    {
        if (space?.Name is not { } name || !_defs.TryGetValue(name, out var def)) throw new AreaException($"Unknown folder \"{space?.Name}\".");
        if (!def.PerInstall) return (def, null);
        return (def, areas.FindInstallRoot(space.InstallRoot) ?? throw new AreaException("That server folder isn't one of this agent's InstallRoots."));
    }
}
