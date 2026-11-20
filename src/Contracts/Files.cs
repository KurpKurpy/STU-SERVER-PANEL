namespace Contracts;

// Phase 4b file manager. A space is a name from the agent's own table, plus a registered install root for mpmissions and keys.
public sealed record FileSpace(string Name, string? InstallRoot);
public sealed record FileItem(string Name, bool IsFolder, long Size, DateTime ModifiedAt);
public sealed record FileListing(FileItem[] Items, bool Truncated);
public sealed record FileHit(string Path, bool IsFolder, long Size, DateTime ModifiedAt);
public sealed record FileSearchResult(FileHit[] Hits, bool Truncated);

// Op: mkdir | move | copy | delete | write | extract. Target: move/copy destination, extract folder. Size and Sha256: the transfer a write pulls.
// A write replaces an existing file only with Overwrite, and then only when ExpectSha256 (if given) is the file's current hash.
public sealed record FileChangeSpec(Guid Id, FileSpace Space, string Op, string Path, string? Target, long Size, string? Sha256,
    bool Overwrite, string? ExpectSha256);
public sealed record FileChangeResult(Guid Id, bool Ok, string? Error, string? Backup);

public static class FileOps
{
    public const string Mkdir = "mkdir", Move = "move", Copy = "copy", Delete = "delete", Write = "write", Extract = "extract";
}
