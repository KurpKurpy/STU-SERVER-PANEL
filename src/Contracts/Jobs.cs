namespace Contracts;

// Long agent work (Phase 5a Ruling 2). The panel owns the Job row; the agent runs one SteamCMD job at a time and reports back.
// Phase 7b: ServerBackup and ServerRestore run in the same one-at-a-time queue, so a backup queued before an update finishes first.
public enum JobKind { ServerInstall, WorkshopDownload, WorkshopRemove, ServerBackup, ServerRestore }

public enum SteamPrompt { Password, EmailCode, TwoFactorCode, MobileConfirm }

public sealed record WorkshopItem(long Id, long? ExpectedBytes);

// InstallRoot must be one of the agent's own InstallRoots; Branch is null/public, creatordlc or profiling (Ruling 15).
// Phase 7b: Label names a new backup ("scheduled", "pre-update", …); Backup and Parts say what a restore writes back.
public sealed record JobSpec(Guid JobId, JobKind Kind, string? SteamUser, string? InstallRoot, string? Branch, WorkshopItem[] Items,
    string? Backup = null, string[]? Parts = null, string? Label = null);

// Progress is 0..1, or null when nothing can be measured. Prompt is set while the job waits for a Steam answer.
public sealed record JobUpdate(Guid JobId, double? Progress, string[] Lines, SteamPrompt? Prompt);

public sealed record JobItemResult(long WorkshopId, bool Ok, string? Error, long SizeBytes, string[] KeyFiles);

// Phase 7b: a backup made (Made), the backup taken just before a restore (Before), and what a restore had to skip (Note).
public sealed record BackupMade(string File, long SizeBytes, string? Note);

public sealed record JobResult(Guid JobId, bool Ok, string? Error, JobItemResult[] Items, BackupMade? Made = null, BackupMade? Before = null,
    string? Note = null);
