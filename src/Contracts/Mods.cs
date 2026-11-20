using System.Text.RegularExpressions;

namespace Contracts;

// A mod the panel names (Phase 5a Ruling 5): a Workshop item or a folder in the agent's localmods area. The agent maps it to a
// folder under its own roots, so the panel can never make it load anything else.
public sealed partial record ModRef(long? WorkshopId, string? LocalFolder)
{
    [GeneratedRegex(@"^[A-Za-z0-9@._ -]{1,64}$")]
    private static partial Regex LocalName();

    // Null when the reference is fine. ';' and quotes are excluded: -mod separates folders with ';'.
    public static string? Problem(ModRef? r) => r switch
    {
        null => "A mod reference is missing.",
        { WorkshopId: { } id, LocalFolder: null } => id > 0 ? null : "Workshop IDs are positive numbers.",
        { WorkshopId: null, LocalFolder: { } f } =>
            LocalName().IsMatch(f) && !f.Contains("..") && f.Trim() == f ? null : $"\"{f}\" isn't a valid local mod folder name.",
        _ => "A mod is either a Workshop item or a local folder.",
    };

    public string Label => WorkshopId is { } id ? $"Workshop item {id}" : $"local mod {LocalFolder}";
}

public sealed record DiskMod(ModRef Ref, string Name, long SizeBytes, DateTime ModifiedAt, string[] KeyFiles);

public sealed record KeyFile(string Name, long Size);

public sealed record KeysResult(bool Ok, string? Error, string? BackupFolder, string[] Added, string[] Removed);

// Addon scanning (Phase 5b): which CfgPatches (addons) and CfgWorlds (maps) a mod or an install provides. An install reports its root
// addons as folder "" and each top-level folder with an addons subfolder (DLC) by name; a mod reports one folder, "".
public sealed record AddonScanRequest(ModRef[] Mods, string[] InstallRoots);

public sealed record ScannedFolder(string Folder, string[] Addons, string[] Worlds, string[] Unreadable);

public sealed record AddonScan(ModRef? Mod, string? InstallRoot, ScannedFolder[] Folders, string? Error);
