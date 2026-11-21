// Mirrors the Panel API DTOs (C# records serialised camelCase).

export type LoginStatus = 'ok' | 'twoFactor' | 'lockedOut' | 'disabled' | 'invalid'

export interface MeDto {
  id: string
  userName: string
  displayName: string
  rank: string | null
  section: string | null
  roles: string[]
  permissions: string[]
  isOwner: boolean
  twoFactorEnabled: boolean
  mustEnrolTwoFactor: boolean
}

export interface MemberDto {
  id: string
  userName: string
  displayName: string
  rank: string | null
  section: string | null
  roles: string[]
  twoFactorEnabled: boolean
  lastLoginAt: string | null
  disabled: boolean
}

export interface MemberDetailDto {
  member: MemberDto
  grants: string[]
  denies: string[]
  effective: string[]
}

export interface MemberPatch {
  displayName?: string
  rank?: string
  section?: string
  roles?: string[]
  grants?: string[]
  denies?: string[]
  disabled?: boolean
}

export interface RoleDto {
  id: string
  name: string
  description: string | null
  isSystem: boolean
  isOwner: boolean
  permissions: string[]
  memberCount: number
}

export interface PermissionDto { key: string; group: string; label: string }

export interface InviteCreated { id: string; url: string; expiresAt: string }
export interface InviteDto { id: string; roleName: string; createdBy: string; createdAt: string; expiresAt: string }
export interface InviteInfo { valid: boolean; reason: 'expired' | 'used' | 'revoked' | 'unknown' | null; roleName: string | null; expiresAt: string | null }
export interface ResetLink { url: string; expiresAt: string }

export interface TwoFactorSetup { sharedKey: string; authenticatorUri: string }
export interface RecoveryCodes { codes: string[] }

export interface AnnouncementDto { id: string; title: string; body: string; pinned: boolean; author: string; createdAt: string; updatedAt: string | null }
export interface ActivityDto { id: string; at: string; actor: string; action: string; summary: string }
export interface AuditDto { id: string; at: string; actor: string | null; action: string; targetType: string | null; targetId: string | null; summary: string; detailsJson: string | null; ip: string | null }

export interface PublicSettings { appName: string }
export interface SettingsDto { appName: string; twoFactorRequiredRoles: string[]; unitTimeZone?: string }
// Server state as the panel reports it; "unknown" whenever the server's agent isn't connected.
export type ServerState = 'offline' | 'starting' | 'online' | 'notResponding' | 'stopping' | 'error' | 'unknown'
export interface QueryInfo { serverName: string; map: string; mission: string; players: number; maxPlayers: number }
export interface ServerDto {
  id: string
  name: string
  installName: string
  state: ServerState
  error: string | null
  address: string | null
  port: number
  startedAt: string | null
  lastStartedAt: string | null
  query: QueryInfo | null
  process: { cpuPercent: number; memoryBytes: number } | null
  disk: { freeBytes: number; totalBytes: number } | null
  revision: number
  appliedRevision: number | null
  missionTemplate: string | null
  headless: { count: number; running: number; withServer: boolean } | null
}
export interface JoinInfo { address: string | null; port: number; password: string | null }
export interface LogLine { readAt: string; text: string }
export interface DiskSample { root: string; freeBytes: number; totalBytes: number }
export interface ProcessSample { profileId: string; cpuPercent: number; memoryBytes: number }
export interface ResourceSample { at: string; hostCpuPercent: number; netInBytesPerSec: number; netOutBytesPerSec: number; disks: DiskSample[]; processes: ProcessSample[] }
export interface ServerSnapshot { server: ServerDto; resources: ResourceSample[] }
export interface FileEntry { name: string; size: number; modifiedAt: string }
export interface InstallInfo { path: string; area: string; hasServerExe: boolean }
export interface AreaInfo { area: string; path: string; exists: boolean; writable: boolean; readOnly: boolean }
export interface AgentDto { id: string; name: string; connected: boolean; machineName: string | null; version: string | null; lastSeenAt: string | null; installs: InstallInfo[]; areas: AreaInfo[] }
export interface AgentCreated { id: string; name: string; key: string }
export interface InstallDto { id: string; agentId: string; name: string; installPath: string; area: string | null; scanStatus: ScanState; scannedAt: string | null; scanNote: string | null }
// Phase 3 profiles. A key missing from a settings map isn't written, so Arma's own default applies.
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }
export type ConfigFile = 'server' | 'basic' | 'difficulty' | 'launch'
export interface HeadlessSettings { enabled: boolean; count: number; withServer: boolean; addresses: string[] | null }
export interface ProfileSettings {
  server: Record<string, JsonValue>; basic: Record<string, JsonValue>; difficulty: Record<string, JsonValue>; launch: Record<string, JsonValue>
  extraArgs: string; headless: HeadlessSettings; missionsOverride: string | null
}
export interface RotationItem { template: string; difficulty: string; paramsText: string }
export interface ProfileSnapshot {
  settings: ProfileSettings; extraServerCfg: string; extraBasicCfg: string; difficultyOverride: string
  rotation: RotationItem[]; difficultyPresetId: string | null; modCollectionId?: string | null
}
export interface ProfileSummary {
  id: string; name: string; slug: string; serverInstallId: string; installName: string; agentId: string
  executable: string; port: number; publicAddress: string | null; createdAt: string; updatedAt: string | null
  revision: number; appliedRevision: number | null; needsCfg: boolean
}
type SecretState = { set: boolean }
export interface ProfileDetail extends ProfileSummary {
  appliedAt: string | null; snapshot: ProfileSnapshot; secretsUnreadable: boolean
  secrets: { password: SecretState; passwordAdmin: SecretState; serverCommandPassword: SecretState }
}
// undefined keeps a password, '' clears it, any other text replaces it.
export interface SecretsWrite { password?: string; passwordAdmin?: string; serverCommandPassword?: string }
export type CfgType = 'Text' | 'Integer' | 'Number' | 'Bool01' | 'BoolWord' | 'TextList' | 'NumberList' | 'Flag'
export interface CfgKey {
  file: ConfigFile; name: string; type: CfgType; tab: string; group: string; label: string; default: string | null
  min: number | null; max: number | null; choices: string[] | null; length: number | null; secret: boolean; help: string | null
  pattern: string | null; class: string | null; member: string; inServerPresets: boolean
}

export interface ServerPresetDto {
  id: string; name: string; description: string; basic: Record<string, JsonValue>; launch: Record<string, JsonValue>
  isBuiltIn: boolean; updatedAt: string | null
}
export interface GeneratedFiles { serverCfg: string; basicCfg: string | null; difficultyPresets: string }
export interface CommandPreview { server: string; headless: string[] }
export interface PreviewResult { files: GeneratedFiles; command: CommandPreview }
export interface ConfigWarning { line: number; message: string }
export interface SaveResult { profile: ProfileDetail; warnings: ConfigWarning[] }
export interface DiffLine { kind: ' ' | '+' | '-' | '…'; text: string; oldLine: number | null; newLine: number | null }
export interface FileDiff { name: string; oldExists: boolean; lines: DiffLine[] }
export interface RevisionDto { number: number; note: string | null; createdBy: string | null; createdAt: string; applied: boolean }
export interface ApplyPreview { revision: number; canApply: boolean; reason: string | null; files: FileDiff[] }
export interface PresetDto { id: string; name: string; values: Record<string, number>; isBuiltIn: boolean; updatedAt: string }
export interface MissionsList { connected: boolean; files: FileEntry[] }
export interface CfgFileDto { area: string; name: string; size: number; modifiedAt: string }

// Phase 5a: jobs, mods, mod lists, keys.
export type JobStatus = 'Queued' | 'Running' | 'NeedsInput' | 'Succeeded' | 'Failed' | 'Cancelled' | 'Interrupted'
export type SteamPromptKind = 'Password' | 'EmailCode' | 'TwoFactorCode' | 'MobileConfirm'
export interface JobDto {
  id: string; type: string; status: JobStatus; progress: number | null; prompt: SteamPromptKind | null; targetType: string | null; targetId: string | null
  createdAt: string; startedAt: string | null; finishedAt: string | null; error: string | null; logText?: string | null
}
export type ModStatus = 'NotInstalled' | 'Queued' | 'Installing' | 'Installed' | 'Failed' | 'Missing'
export interface ModDto {
  id: string; workshopId: number | null; localFolder: string | null; name: string; status: ModStatus; sizeBytes: number; expectedBytes: number | null
  installedAt: string | null; workshopUpdatedAt: string | null; latestWorkshopUpdate: string | null; updateAvailable: boolean; previewUrl: string | null
  keyFiles: string[]; lastError: string | null; lists: string[]; scanStatus: ScanState; scannedAt: string | null; scanNote: string | null
  notPublic: boolean // unlisted (or private): Steam can't say when it changes, so Update all fetches it every time
}
export interface PasteResult { added: ModDto[]; existing: ModDto[]; refused: { workshopId: number; reason: string }[]; problems: string[] }
export interface CollectionItemDto {
  modId: string; name: string; workshopId: number | null; localFolder: string | null; modStatus: ModStatus; updateAvailable: boolean
  client: boolean; serverOnly: boolean; headless: boolean; optional: boolean; enabled: boolean
}
export interface CollectionDto { id: string; name: string; description: string; updatedAt: string; items: CollectionItemDto[]; usedBy: string[] }
export interface CollectionSummary { id: string; name: string; description: string; updatedAt: string; count: number; notInstalled: number; usedBy: string[] }
export interface KeyDto { name: string; size: number; kind: 'a3' | 'mod' | 'unknown'; mods: string[] }

// ---------- Phase 4: missions ----------
export interface PersonDto { id: string; name: string }
export interface MissionDto {
  id: string; className: string; map: string; fileName: string; displayName: string; description: string; gameType: string | null
  category: string | null; tags: string[]; author: PersonDto | null; collaborators: PersonDto[]; currentVersion: number | null
  currentVersionAt: string | null; versions: number; isArchived: boolean; createdAt: string; updatedAt: string; canEdit: boolean; canShare: boolean
}
export interface MissionFileDto { path: string; size: number; sha: string; text: boolean; binarized: boolean; updatedAt: string; updatedBy: string | null }
export interface FileContentDto { path: string; sha: string | null; size: number; text: string | null; binary: boolean; readOnly: boolean; binarized: boolean; deleted: boolean }
export interface RevisionDto { id: string; sha: string | null; at: string; by: string | null; note: string | null }
export interface SearchHit { path: string; line: number; text: string }
export interface AddonDto { addon: string; modName: string | null; modUrl: string | null }
export interface VersionDto {
  number: number; note: string | null; createdAt: string; createdBy: string | null; pboSize: number; binarized: boolean; files: number
  current: boolean; hasOriginal: boolean; addons: AddonDto[]
}
export type DeploymentStatus = 'Pending' | 'Waiting' | 'Done' | 'Failed'
export interface DeploymentDto {
  installId: string; installName: string; status: DeploymentStatus; version: number; error: string | null; backupPath: string | null
  createdAt: string; deployedAt: string | null
}
export interface VersionCompare { added: string[]; removed: string[]; changed: { path: string; aSha: string; bSha: string }[] }

// Phase 5b: addon scans, compatibility, Server Missions and Start Mission.
export type ScanState = 'NotScanned' | 'Scanned' | 'Partial' | 'Failed'
export type CompatStatus = 'Ready' | 'Missing' | 'Unknown'
export interface MissingAddon { addon: string; edenMod: string | null; libraryMod: string | null }
export interface CompatResult { status: CompatStatus; missingMap: string | null; missing: MissingAddon[]; unreadableMods: string[] }
export interface ServerMissionRow extends CompatResult { missionId: string; displayName: string; fileName: string; version: number }
export interface ServerMissionsDto { modListId: string | null; appliedModListId: string | null; modLists: { id: string; name: string }[]; missions: ServerMissionRow[]; notInstalled: string[] }
export interface StartMissionPreview {
  action: 'start' | 'restart'; players: number | null; modListChanges: boolean; unappliedChanges: boolean; compat: CompatResult; fileName: string; modListName: string
  profileName: string
}
// What an operation's Apply will do, with the server, operation and version it was worked out for (Phase 6a final review I-3).
export interface OperationApplyPreview extends StartMissionPreview { operationName: string; version: number }
export interface InstallCompat { installId: string; installName: string; version: number; lists: (CompatResult & { id: string; name: string })[] }

// Phase 4b file manager
export interface FileInstall { id: string; name: string; readOnly: boolean }
export interface FileSpaceInfo { name: string; label: string; perInstall: boolean; hasPasswords: boolean; writable: boolean; canWrite: boolean }
export interface FileSpacesDto { installs: FileInstall[]; spaces: FileSpaceInfo[] }
export interface FileItem { name: string; isFolder: boolean; size: number; modifiedAt: string }
export interface FileListing { items: FileItem[]; truncated: boolean }
export interface FileHit { path: string; isFolder: boolean; size: number; modifiedAt: string }
export interface FileSearchResult { hits: FileHit[]; truncated: boolean }
export interface FileText { text: string; bom: boolean; eol: string; sha256: string; size: number }

// Phase 6a operations
export interface NamedRef { id: string; name: string }
export interface OperationMission { id: string; name: string; map: string; fileName: string; archived: boolean }
export interface ReadinessInfo { ready: number; total: number; checkedAt: string }
export type OperationStatus = 'Planned' | 'Done' | 'Cancelled'
export interface OperationRow {
  id: string; name: string; startsAt: string; status: OperationStatus; mission: OperationMission | null; versionNumber: number | null
  profile: NamedRef | null; modList: NamedRef | null; missionMaker: NamedRef | null; zeus: NamedRef[]; readiness: ReadinessInfo | null; appliedAt: string | null
}
export type CheckState = 'pass' | 'fail' | 'unknown' | 'apply'
export interface CheckResult { itemId: string; label: string; state: CheckState; detail: string | null; fix: string | null }
export interface OperationDto extends OperationRow {
  missionVersionId: string | null; appliedBy: NamedRef | null; description: string; briefing: string; notes: string | null; version: number
  lastCheck: CheckResult[] | null; ticked: string[]
}
export interface ChecklistItemDto { id: string; label: string; autoKey: string | null; order: number; enabled: boolean }

// Phase 7b backups and schedules
export interface BackupDto { id: string; type: 'Console' | 'Server'; kind: 'Manual' | 'Scheduled' | 'PreUpdate' | 'PreRestore'; agentId: string | null; agentName: string | null; fileName: string | null; sizeBytes: number; status: 'Running' | 'Done' | 'Failed'; error: string | null; note: string | null; createdBy: string | null; createdAt: string; finishedAt: string | null }
export interface BackupSettings { keepScheduled: number; keepPreUpdate: number; safetyCopyDays: number; consoleCopyTo: string | null; agents: { id: string; name: string }[] }
export type ScheduleTask = 'restart' | 'stop' | 'start' | 'update-server' | 'update-mods' | 'backup'
export interface ScheduleRunDto { id: string; dueAt: string; startedAt: string | null; finishedAt: string | null; outcome: 'Running' | 'Done' | 'Failed' | 'Skipped' | 'Missed'; detail: string | null }
export interface ScheduleDto { id: string; name: string; task: ScheduleTask; targetId: string | null; targetName: string | null; scope: string | null; days: number; minuteOfDay: number; enabled: boolean; nextRunAt: string | null; lastRun: ScheduleRunDto | null }
export interface ScheduleTargets { profiles: { id: string; name: string }[]; installs: { id: string; name: string }[]; agents: { id: string; name: string }[]; timeZone: string }
export interface NextScheduled { id: string; name: string; task: ScheduleTask; at: string }

// Phase 7a RCon
export interface PlayerDto { number: number; name: string; ping: number; lobby: boolean; firstSeen: string | null; guid: string | null; verified: boolean | null; ip: string | null }
export interface PlayersDto { rcon: 'Off' | 'Connecting' | 'Connected' | 'Failed'; rconError: string | null; unavailable: string | null; players: PlayerDto[]; unparsed: string[] | null }
export interface BanDto { number: number; kind: 'guid' | 'ip'; target: string; minutesLeft: string; reason: string }
export interface BansDto { unavailable: string | null; bans: BanDto[]; unparsed: string[] }
export interface RconLine { at: string; text: string }

// Phase 6b notifications
export interface NotificationItem { id: string; type: string; text: string; link: string | null; createdAt: string; readAt: string | null }
export interface NotificationFeed { unread: number; items: NotificationItem[] }
export interface NotificationRuleDto { type: string; label: string; inApp: boolean; discord: boolean }
export interface NotificationSettingsDto {
  discord: { set: boolean; broken: boolean; lastError: string | null; lastErrorAt: string | null; lastSentAt: string | null }
  reminderMinutes: number
  rules: NotificationRuleDto[]
}
export interface OperationOptions {
  missions: { id: string; name: string; map: string; currentVersionId: string | null; versions: { id: string; number: number }[] }[]
  profiles: NamedRef[]; modLists: NamedRef[]; people: NamedRef[]
}
