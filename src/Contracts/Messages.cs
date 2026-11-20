namespace Contracts;

public enum ServerState { Offline, Starting, Online, NotResponding, Stopping, Error }

// ReadOnly: the agent's Agent:ReadOnlyInstallRoots lists it, so it refuses every write there (Phase 4a Ruling 9).
public sealed record InstallInfo(string Path, string Area, bool HasServerExe, bool ReadOnly = false);
public sealed record AreaInfo(string Area, string Path, bool Exists, bool Writable, bool ReadOnly);
public sealed record QueryInfo(string ServerName, string Map, string Mission, int Players, int MaxPlayers);
public sealed record ProfileStatus(Guid ProfileId, ServerState State, int? Pid, DateTime? StartedAt, QueryInfo? Query, string? Error, int? ExitCode,
    int HeadlessRunning = 0, RconState Rcon = RconState.Off, string? RconError = null);
// Start launches the files Apply wrote (Phase 3); the spec only says whether basic.cfg is one of them.
public sealed record LaunchSpec(Guid ProfileId, string Slug, string InstallPath, string Executable, int Port, bool UseBasicCfg,
    string[] CustomArgs, HeadlessSpec? Headless = null, ModRef[]? Mods = null, ModRef[]? ServerMods = null, ModRef[]? HeadlessMods = null,
    RconSpec? Rcon = null);
public sealed record HeadlessSpec(int Count, string Connect, string? Password, bool WithServer);
// File contents only; the agent decides where each one goes (IGameAdapter.ConfigTargets).
public sealed record ConfigBundle(Guid ProfileId, string Slug, string ServerCfg, string? BasicCfg, string DifficultyPresets);
public sealed record WrittenFile(string Name, string Sha256, long Size);
public sealed record ApplyResult(bool Ok, string? Error, string? BackupFolder, WrittenFile[] Files);
public sealed record ConfigSnapshot(string? ServerCfg, string? BasicCfg, string? Arma3Profile);
public sealed record StartResult(bool Ok, string? Error, int? Pid, DateTime? StartedAt);
public sealed record CommandResult(bool Ok, string? Error);
public sealed record LogLine(DateTime ReadAt, string Text);
public sealed record DiskSample(string Root, long FreeBytes, long TotalBytes);
public sealed record ProcessSample(Guid ProfileId, double CpuPercent, long MemoryBytes);
public sealed record ResourceSample(DateTime At, double HostCpuPercent, long NetInBytesPerSec, long NetOutBytesPerSec, DiskSample[] Disks, ProcessSample[] Processes);
public sealed record FileEntry(string Name, long Size, DateTime ModifiedAt);
public sealed record FileChunk(byte[] Data, long TotalSize);
public sealed record AgentHello(string MachineName, string Version, InstallInfo[] Installs, AreaInfo[] Areas, ProfileStatus[] Statuses, ResourceSample[] RecentResources,
    Guid[]? Jobs = null);
