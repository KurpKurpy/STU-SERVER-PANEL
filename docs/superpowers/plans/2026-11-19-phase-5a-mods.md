# Phase 5a — Mods Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Install and update the Arma 3 server and Workshop mods through SteamCMD from the panel, build mod lists (paste IDs/links or import a Launcher preset), deploy keys, and start a profile with its mod list as `-mod`/`-serverMod`.

**Architecture:** The panel owns the library (Mod, ModCollection, Job rows) and asks the agent to do disk work. Long work is a `Job`: the panel creates the row and sends a `JobSpec`; the agent runs SteamCMD one job at a time, streams progress and prompts over the agent hub, and reports a `JobResult`. Steam passwords and Steam Guard codes go browser → panel → agent → SteamCMD's stdin and are never stored or logged. A profile's revision names a mod list; Start turns the list's enabled items into `ModRef`s that the agent resolves to folders under its own data root.

**Tech Stack:** .NET 10 (pinned packages 10.0.15), ASP.NET Core minimal APIs, SignalR client results, EF Core Sqlite, React 19 + TanStack Query 5 + Tailwind v4, xUnit. Real SteamCMD for the spike and the end-to-end run; `tests/FakeSteamCmd` for tests.

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md` (§5.3, §7, §8 Phase 5, §11 Phase 5) and the source spec §10.

## Global Constraints

- Run .NET as `"C:\Program Files\dotnet\dotnet.exe"`; packages pinned at 10.0.15. Stop the `panel` preview before `dotnet test`.
- No git repository and the user handles git: commit nothing; the ledger replaces commit steps.
- `K:\` is read-only. The dev agent's data root is `D:\13th_server_dev`. Start agents and servers with `$env:RESHADE_BASE_PATH_OVERRIDE='D:\13th_server_dev\reshade'`.
- Steam passwords and Steam Guard codes are never stored, logged, audited, put in a process argument list or returned by any endpoint (source spec §10: "Never log passwords or expose authentication secrets through the frontend").
- "Do not assume all Workshop items can be downloaded anonymously" (source spec §10). The server app 233780 downloads anonymously; Workshop items (app 107410) need an account that owns Arma 3 (design §3).
- "Never automatically remove existing keys without a recoverable backup" (source spec §10).
- "Do not change a live server's active mod configuration without an authorised action" (source spec §10).
- Every endpoint requires a permission; every state change is audited without secret values.
- Agent path rule (design §5.3): the panel names things, the agent maps them to paths under its own roots. The panel never sends a filesystem path the agent then trusts.

## Plan Rulings

1. **Phase 5 is split.** This plan (5a) builds the following:
   - SteamCMD jobs;
   - Steam sign-in with Steam Guard;
   - the mod library with paste-by-ID/URL;
   - mod lists with FASTER's four flags;
   - Launcher preset import/paste/export;
   - keys deploy, backup and cleanup;
   - profile mod lists with `-mod`/`-serverMod`.

   Moved after Phase 4, which builds the PBO/raP reader they share: CfgPatches scanning, the Server Missions tab and Start Mission. Workshop search (needs a Steam Web API key) moves to Phase 7 settings. Scheduled updates and "update before an operation" stay in Phases 7 and 6. Cost if wrong: admins add mods by ID/link or preset until search arrives.
2. **Jobs:**
   - **Storage:** a `Job` row per design §8, but `LogText` (the last 200 lines, secrets masked) replaces `LogBlobSha`, because the blob store arrives in Phase 4.
   - **Concurrency:** the agent runs one SteamCMD job at a time; a second is queued. SteamCMD can't share its folder between two processes.
   - **Restarts:** after a panel restart, a running job is marked Interrupted. After an agent restart, the panel marks the agent's unfinished jobs Interrupted when the agent's Hello doesn't report them.

   Cost if wrong: a big download restarts from SteamCMD's own resume point.
3. **Steam sign-in:**
   - **What's stored:** the Steam username is a panel setting; the password is never stored.
   - **How it's sent:** a job runs `+login <user>`. If SteamCMD asks for the password or a Steam Guard code, the job turns NeedsInput and the browser asks. The answer goes once to the agent, which writes it to SteamCMD's stdin.
   - **Masking:** the agent masks the value in any output line.
   - **Afterwards:** SteamCMD's cached login is reused.
   - **Anonymous jobs:** server installs log in anonymously.

   Cost if wrong: an admin types the password again when Steam's cache expires.
4. **Disk layout under the agent's DataRoot:**
   - **SteamCMD:** `steamcmd\steamcmd.exe`, bootstrapped by the agent from Valve's `steamcmd.zip` when missing; `Agent:SteamCmdPath` overrides it.
   - **Workshop items:** `workshop\steamapps\workshop\content\107410\<id>` (`+force_install_dir <DataRoot>\workshop`).
   - **Local mods:** `localmods\<folder>`, copied in by admins.
   - **No junctions:** `-mod` uses the absolute folders directly (design §11 said junctions). `Areas` never follows junctions, and nothing reads them.

   Cost if wrong: RPT lines show numeric folder names instead of `@names`.
5. **Mod references:** `ModRef(long? WorkshopId, string? LocalFolder)`. Workshop IDs are 1–20 digits; local folder names match `^[A-Za-z0-9@._ -]{1,64}$` without `..`. The agent maps each to a folder and refuses Start when one is missing. Cost if wrong: none (the panel can't name arbitrary folders).
6. **Mod lists** (`ModCollection`, `ModCollectionItem`, design §8) carry `Client`, `ServerOnly`, `Headless`, `Optional`, `Enabled` and `Order`.
   - **`-mod`:** enabled Client items in order.
   - **`-serverMod`:** enabled ServerOnly items.
   - **Headless clients:** get enabled Headless items as their `-mod`.
   - **Optional:** loaded by nobody on the server, but their keys are deployed so players who use them can join.
   - **Editor actions:** the bulk actions (enable/disable all, clear, sort by name or ID, copy Client → Headless, copy from another list) run in the browser; the server saves the result.

   Cost if wrong: one more round trip per bulk action if moved server-side later.
7. **A profile's mod list is revisioned:** `ProfileSnapshot.ModCollectionId`. Start uses the applied revision's list and that list's current contents, as FASTER does. Editing a list never touches a running server; it applies at the next Start or Restart. Cost if wrong: a list edit reaches the next Start without a profile revision.
8. **Keys:**
   - **Deploy:** copies every `.bikey` in a mod folder's `keys`/`key`/`serverkey`/`serverkeys` subfolders (any case) into `<install>\keys`. It never deletes.
   - **Start:** deploys the selected list's missing keys automatically.
   - **Cleanup:** an explicit `keys.manage` action. It removes keys that neither `a3.bikey` nor any mod in the chosen lists provides.
   - **Backups:** every change first copies the whole `keys` folder to `backups\keys\<install-area>\<time>`.

   Cost if wrong: one extra backup folder per Start that adds keys.
9. **Install write access:** the install stays read-only for `FileService`. Only SteamCMD (server install or update into a configured `InstallRoots` folder) and the keys commands (only `<install>\keys`) write there. Cost if wrong: none.
10. **Workshop metadata** comes from the panel calling `ISteamRemoteStorage/GetPublishedFileDetails/v1` (no API key), behind an injectable `IWorkshopClient`:
    - **Fields used:** title, `file_size`, `time_updated` and `preview_url`.
    - **App check:** items whose `consumer_app_id` isn't 107410 are refused.
    - **Updates:** "Update available" means the current `time_updated` is newer than the `WorkshopUpdatedAt` recorded at install.

    Cost if wrong: an item updated within the same second isn't flagged.
11. **Progress:**
    - **Server installs:** parse SteamCMD's `progress: NN.NN (a / b)` lines.
    - **Workshop items:** bytes on disk (the item's `downloads` then `content` folder) against `file_size`.
    - **Neither available:** the progress is null and the UI bar is indeterminate (design §2).

    Cost if wrong: a bar that jumps when SteamCMD moves the folder.
12. **Workshop retries:** an item that fails with `Timeout` or `Failure` is retried in a fresh SteamCMD run, up to 3 runs per job. SteamCMD resumes partial downloads. Cost if wrong: a slow item fails after 3 tries with SteamCMD's message.
13. **Launcher presets:**
    - **Import:** reads `<tr data-type="ModContainer">` rows: `DisplayName`, a `filedetails/?id=` link for Steam mods, `from-local` for local ones. It's capped at 512 KB and parsed with regexes that have timeouts.
    - **Paste:** pasting plain text accepts Workshop IDs and URLs one per line.
    - **Export:** writes the Launcher's own HTML shape.
    - **Missing mods:** added to the library as Not installed.

    Cost if wrong: a Launcher format change breaks import until the regexes follow.
14. **Permissions** (design §7, Phase 5 additions): `mods.view`, `mods.manage`, `mods.install`, `collections.manage`, `keys.manage`, `server.update`.
    - **Server Administrator:** all six.
    - **Mod Manager:** all but `server.update`.
    - **Mission Maker and Zeus:** `mods.view`.
    - **Steam answers:** answering a job's Steam prompt needs the permission that created the job.

    Cost if wrong: a role edit in the panel.
15. **Branches** for the server install:
    - `public`;
    - `creatordlc`, which adds the Creator DLC server data;
    - `profiling`, with Bohemia's published branch password `CautionSpecialProfilingAndTestingBranchArma3`.

    Only these three are offered. Cost if wrong: another branch needs a code change.

## Review Focus

1. **Secrets leaking:** a Steam password or code reaching `Job.LogText`, the audit log, an RPT, an exception message, a SteamCMD argument list or any GET. Expected: none anywhere. (Tests: T3 `Input_is_masked_in_output_and_never_in_arguments`, T6 `Steam_answers_are_never_stored_or_returned`.)
2. **SteamCMD output quirks:**
   - prompts without a trailing newline;
   - `\r` progress lines;
   - the first-run self-update;
   - a hung process.

   Expected: prompts are detected mid-line, progress parsed, and a job with no output for 10 minutes is failed and its process killed. (T2 `Prompt_without_newline_is_detected`, `Silent_steamcmd_times_out`.)
3. **Job state across restarts and concurrency:**
   - two jobs at once;
   - a panel restart mid-job;
   - an agent restart mid-job.

   Expected: one runs and one queues; restarts mark the job Interrupted; nothing stays Running forever. (T3 `Second_job_waits_for_the_first`, T6 `Running_jobs_are_interrupted_after_a_panel_restart`.)
4. **Hostile mod references and preset input:**
   - a `..` folder, a path, a 10 MB preset, a pathological regex input;
   - a semicolon or quote in a local folder name.

   Expected: refused with a field error, never a crash or a folder outside the roots. (T4 `Mod_refs_cannot_escape_the_roots`, T7 `Preset_import_is_bounded`.)
5. **Key cleanup:**
   - removing `a3.bikey`;
   - removing keys without a backup;
   - deploying into a missing `keys` folder.

   Expected: `a3.bikey` is always kept, a backup precedes every removal, and the folder is created. (T4 `Cleanup_keeps_a3_and_backs_up_first`.)

## Spike facts (Task 1 records them here; later tasks consume them)

Placeholder. Task 1 writes facts S1–S6 into the ledger, and its rulings adjust T2's prompt strings if real SteamCMD differs.

---

### Task 1: Spike — real SteamCMD behaviour

**Files:** none in the product. Scratch scripts and output stay under `D:\13th_server_dev\spike5`.

- [ ] **Step 1:** With the user's permission (download: Valve's `steamcmd.zip`, ~3 MB, plus its ~40 MB self-update), extract SteamCMD to `D:\13th_server_dev\steamcmd`.
- [ ] **Step 2: Facts.** Run it with stdin/stdout redirected from a .NET or PowerShell harness and record:
  - **S1:** the first-run self-update output and whether it exits or continues.
  - **S2:** whether output arrives unbuffered, and its line endings (`\r`, `\n`).
  - **S3:** the exact password prompt text for `+login <unknown-user>`, captured without sending a password (then kill).
  - **S4:** the output of `+login anonymous +workshop_download_item 107410 450814997 +quit` (CBA_A3).
  - **S5:** the progress line format of `+app_update 233780` (stopped after the first progress lines, unless the user agreed to the full download).
  - **S6:** the exit codes seen.
- [ ] **Step 3:** Rule on any difference from the prompt strings T2 assumes. Expected: facts S1–S6 in the ledger.

### Task 2: SteamCMD runner (agent)

**Files:**
- Create: `src/Agent/Steam/SteamCmd.cs`, `tests/FakeSteamCmd/` (console exe), `tests/Agent.Tests/SteamCmdTests.cs`.

**Interfaces:**
- Produces:
  - `SteamCmdRun SteamCmd.Start(string exe, IReadOnlyList<string> args, string? secretToMask)`
  - `SteamCmdRun`: `event Action<string>? Line`, `event Action<SteamPrompt>? Prompt`, `event Action<double>? Progress`, `Task WriteAsync(string answer)`, `Task<SteamCmdExit> Completion`, `void Kill()`
  - `enum SteamPrompt { Password, EmailCode, TwoFactorCode, MobileConfirm }`
  - `record SteamCmdExit(int ExitCode, bool TimedOut, IReadOnlyList<string> Errors)`
  - Static parsers: `SteamCmd.ParseProgress(string line) → double?`, `SteamCmd.ParseItemResult(string line) → (long Id, bool Ok, string? Error)?`

- [ ] **Step 1: Write the failing tests** against `FakeSteamCmd`. The fake reads its arguments, prints the scripted output, and prompts on stdin:
  - `Lines_and_progress_are_reported`: `progress: 45.23 (1 / 2)` → 0.4523.
  - `Prompt_without_newline_is_detected`: `password: ` without `\n` raises `Prompt(Password)`; also `Steam Guard code:`, `Two-factor code:`, and "confirm the login in the Steam Mobile app".
  - `Answer_is_written_and_masked`: the answer reaches the fake's stdin; a line echoing it shows `****`.
  - `Item_results_are_parsed`: `Success. Downloaded item 450814997 to "..." (123 bytes)` → ok; `ERROR! Download item 1 failed (Timeout).` → Timeout.
  - `Silent_steamcmd_times_out`: no output for the idle limit → `TimedOut` and the process is killed. The idle limit is injectable, 10 minutes by default.
- [ ] **Step 2:** Run `dotnet test tests/Agent.Tests --filter SteamCmdTests`. Expected: build fails (types missing).
- [ ] **Step 3:** Implement. Read stdout char by char into a line buffer, splitting on `\r` and `\n`. Check prompts against the unfinished buffer after each read pause of 200 ms.
- [ ] **Step 4:** Run the tests. Expected: PASS.

### Task 3: Agent jobs (server install, workshop download/remove)

**Files:**
- Create: `src/Contracts/Jobs.cs`, `src/Agent/Steam/JobRunner.cs`, `src/Agent/Steam/SteamCmdInstaller.cs`, `tests/Agent.Tests/JobRunnerTests.cs`.
- Modify: `src/Contracts/IAgentClient.cs`, `src/Agent/AgentOptions.cs` (`SteamCmdPath`), `src/Agent/Areas.cs` (areas `workshop`, `localmods` read-only for FileService), `src/Agent/PanelConnection.cs`, `src/Agent/Program.cs`.

**Interfaces:**
- Produces (Contracts):
  - `enum JobKind { ServerInstall, WorkshopDownload, WorkshopRemove }`
  - `record WorkshopItem(long Id, long? ExpectedBytes)`
  - `record JobSpec(Guid JobId, JobKind Kind, string? SteamUser, string? InstallRoot, string? Branch, WorkshopItem[] Items)`
  - `record JobUpdate(Guid JobId, double? Progress, string[] Lines, SteamPrompt? Prompt)`
  - `record JobItemResult(long WorkshopId, bool Ok, string? Error, long SizeBytes, string[] KeyFiles)`
  - `record JobResult(Guid JobId, bool Ok, string? Error, JobItemResult[] Items)`
  - `IAgentClient`: `Task<CommandResult> RunJob(JobSpec spec)`, `Task<CommandResult> AnswerJob(Guid jobId, string answer)`, `Task<CommandResult> CancelJob(Guid jobId)`
  - `IAgentHub`: `Task JobUpdated(JobUpdate update)`, `Task JobFinished(JobResult result)`
  - `AgentHello` gains `Guid[] Jobs` (queued or running), defaulting to an empty array.

- [ ] **Step 1: Write the failing tests** (FakeSteamCmd):
  - `Server_install_runs_app_update_into_the_install_root`: the args contain `+force_install_dir <root>`, `+login anonymous`, `+app_update 233780`, and `-beta creatordlc` for that branch.
  - `Install_root_must_be_configured`: any other root is refused.
  - `Workshop_download_logs_in_as_the_user_and_reports_items`: size and keys found.
  - `Failed_items_are_retried_up_to_three_runs`.
  - `Second_job_waits_for_the_first`.
  - `Password_prompt_waits_for_an_answer_and_times_out_after_5_minutes` (injectable).
  - `Input_is_masked_in_output_and_never_in_arguments`.
  - `Remove_deletes_only_that_items_folder`.
  - `SteamCmd_is_bootstrapped_from_the_zip_when_missing` (injected `HttpMessageHandler`).
- [ ] **Step 2:** Run the tests. Expected: build fails.
- [ ] **Step 3:** Implement `JobRunner`:
  - a single-worker queue;
  - progress per Ruling 11;
  - `JobUpdated` throttled to one message a second;
  - lines batched.
- [ ] **Step 4:** Run `dotnet test tests/Agent.Tests`. Expected: PASS, the Phase 3 tests unchanged.

### Task 4: Mods on disk, keys, and `-mod` (agent + contracts)

**Files:**
- Create: `src/Contracts/Mods.cs`, `src/Agent/Mods/ModStore.cs`, `src/Agent/Mods/KeyService.cs`, `tests/Agent.Tests/ModStoreTests.cs`, `tests/Agent.Tests/KeyServiceTests.cs`.
- Modify: `src/Contracts/Messages.cs` (`LaunchSpec` gains `ModRef[]? Mods = null, ModRef[]? ServerMods = null, ModRef[]? HeadlessMods = null`), `src/Contracts/Arma3Command.cs`, `src/Agent/Supervisor.cs`, `src/Contracts/IAgentClient.cs`.

**Interfaces:**
- Produces:
  - `record ModRef(long? WorkshopId, string? LocalFolder)` and `static string? ModRef.Problem(ModRef r)`
  - `record DiskMod(ModRef Ref, string Name, long SizeBytes, DateTime ModifiedAt, string[] KeyFiles)` (Name from `mod.cpp`/`meta.cpp` `name=` when present, else the folder name)
  - `record KeyFile(string Name, long Size)`, `record KeysResult(bool Ok, string? Error, string? BackupFolder, string[] Added, string[] Removed)`
  - `IAgentClient`: `Task<DiskMod[]> ListMods()`, `Task<KeyFile[]> ListKeys(string installRoot)`, `Task<KeysResult> DeployKeys(string installRoot, ModRef[] mods)`, `Task<KeysResult> CleanupKeys(string installRoot, ModRef[] keep)`
  - `Arma3Command.ServerArguments(LaunchSpec spec, string configDir, string profileDir, Func<ModRef, string> modPath)`, which adds `-mod=a;b` and `-serverMod=c` after the Phase 3 arguments; `HeadlessArguments(..., Func<ModRef, string> modPath)` adds `-mod=` from `HeadlessMods`.

- [ ] **Step 1: Write the failing tests:**
  - `Mod_refs_cannot_escape_the_roots`: `..`, rooted paths, `;`, quotes and 21-digit IDs are refused.
  - `ListMods_reports_workshop_and_local_mods_with_keys`.
  - `Deploy_copies_new_keys_after_a_backup_and_never_deletes`.
  - `Cleanup_keeps_a3_and_backs_up_first`.
  - `Keys_folder_is_created_when_missing`.
  - `Start_refuses_a_missing_mod`.
  - `Mod_arguments_are_generated` (`-mod` order kept, `-serverMod` separate, headless clients get Headless items).
  - `A_path_with_spaces_stays_one_argument`.
- [ ] **Step 2:** Run the tests. Expected: build fails.
- [ ] **Step 3:** Implement. `KeyService` writes only below `<installRoot>\keys`, and only for roots in `Areas.InstallRoots`.
- [ ] **Step 4:** Run `dotnet test tests/Agent.Tests`. Expected: PASS.

### Task 5: Panel data, permissions, settings

**Files:**
- Modify: `src/Panel/Data/Entities.cs`, `AppDb.cs`, `src/Panel/Auth/Permissions.cs`, `src/Panel/Seeder.cs`, `src/Panel/Settings/AppSettingsService.cs` (`SteamUsername`), `src/Panel/Features/Profiles/ProfileStore.cs` (`ProfileSnapshot.ModCollectionId`).
- Create: migration `Phase5Mods`; `tests/Panel.Tests/ModPermissionTests.cs`.

**Interfaces:**
- Produces:
  - `Job { Id, Type, Status, Progress, Prompt, TargetType, TargetId, PayloadJson, CreatedById, CreatedAt, StartedAt, FinishedAt, Error, LogText }`, where `Status` is one of Queued, Running, NeedsInput, Succeeded, Failed, Cancelled, Interrupted.
  - `Mod { Id, WorkshopId?, LocalFolder?, Name, Status, SizeBytes, ExpectedBytes, WorkshopUpdatedAt, InstalledAt, PreviewUrl, KeyFilesJson, LastError }`, where `Status` is one of NotInstalled, Queued, Installing, Installed, Failed, Missing.
  - `ModCollection { Id, Name, Description, UpdatedById, UpdatedAt }`.
  - `ModCollectionItem { CollectionId, ModId, Order, Client, ServerOnly, Headless, Optional, Enabled }`.
  - The permission constants of Ruling 14.

- [ ] **Step 1: Write the failing tests:**
  - `Seeded_roles_get_the_phase_5_permissions`, per Ruling 14.
  - The updated exact-list tests (`SeedingTests`, `PermissionTests`), as in Phase 3 Task 4.
- [ ] **Step 2:** Run. Expected: build fails.
- [ ] **Step 3:** Implement and add the migration: `dotnet tool run dotnet-ef migrations add Phase5Mods -p src/Panel -o Data/Migrations`.
- [ ] **Step 4:** Run `dotnet test tests/Panel.Tests`. Expected: PASS.

### Task 6: Jobs and Steam sign-in (panel)

**Files:**
- Create: `src/Panel/Features/Jobs/JobService.cs`, `JobEndpoints.cs`, `tests/Panel.Tests/JobTests.cs`.
- Modify: `src/Panel/Hubs/AgentHub.cs` (`JobUpdated`, `JobFinished`, Hello's `Jobs`), `UiHub.cs` (`WatchJobs`), `UiConnections.cs`, `Program.cs`, `tests/Panel.Tests/FakeAgent.cs`.

**Interfaces:**
- Produces:
  - `JobService.CreateAsync(JobKind kind, Guid agentId, JobSpec spec, string targetType, string? targetId)` → `Job`. The job is dispatched to the agent; `agent_offline` leaves it Failed.
  - `GET /api/jobs?active=` and `GET /api/jobs/{id}` (needs `mods.view` or `server.update`).
  - `POST /api/jobs/{id}/answer { answer }`: the answer goes to the agent and is never persisted.
  - `POST /api/jobs/{id}/cancel`.
  - `GET/PUT /api/steam { username }` (`mods.install`).
  - UI hub event `"job"` carrying a `JobDto`.

- [ ] **Step 1: Write the failing tests:**
  - `Job_runs_and_finishes_through_the_agent`.
  - `Prompt_turns_the_job_to_needs_input_and_the_answer_reaches_the_agent`.
  - `Steam_answers_are_never_stored_or_returned`: grep the DB, audit, `LogText` and every GET for the answer.
  - `Running_jobs_are_interrupted_after_a_panel_restart`.
  - `Jobs_missing_from_the_agents_hello_are_interrupted`.
  - `Only_the_creating_permission_can_answer`.
- [ ] **Step 2:** Run. Expected: build fails.
- [ ] **Step 3:** Implement. Keep the last 200 lines; the agent has already masked secrets, and the panel masks the answer again before storing.
- [ ] **Step 4:** Run the Panel tests. Expected: PASS.

### Task 7: Mod library, metadata, install jobs, presets, collections (panel)

**Files:**
- Create:
  - `src/Panel/Features/Mods/WorkshopClient.cs` (`IWorkshopClient`);
  - `ModEndpoints.cs`, `CollectionEndpoints.cs`, `LauncherPreset.cs`;
  - `tests/Panel.Tests/ModTests.cs`, `CollectionTests.cs`, `LauncherPresetTests.cs`.
- Modify: `Program.cs`, `tests/Panel.Tests/PanelFactory.cs` (a fake `IWorkshopClient`).

**Interfaces:**
- Produces:
  - `IWorkshopClient.DetailsAsync(long[] ids)` → `WorkshopDetails[] (Id, Ok, Title, FileSize, TimeUpdated, PreviewUrl, AppId)`.
  - `LauncherPreset.Parse(string text)` → `(string? Name, PresetMod[] Mods, string[] Problems)`, where `PresetMod(string Name, long? WorkshopId, string? LocalFolder)`. It also accepts plain ID/URL lists.
  - `LauncherPreset.Export(string name, IEnumerable<(string Name, long? WorkshopId, string? Local)> mods)` → string.
  - **Mod endpoints:**
    - `GET /api/mods`;
    - `POST /api/mods { text }` (paste);
    - `POST /api/mods/refresh`;
    - `POST /api/mods/install { modIds?, all?, mode: install|update|reinstall }` → job;
    - `DELETE /api/mods/{id}?files=`;
    - `GET /api/mods/local` (from the agent).
  - **Collection endpoints:**
    - `GET/POST /api/collections`;
    - `GET/PUT/DELETE /api/collections/{id}`;
    - `POST /api/collections/{id}/duplicate`;
    - `POST /api/collections/import { name?, text }` → `{ collection, missing[] }`;
    - `GET /api/collections/{id}/export` → `text/html`.

- [ ] **Step 1: Write the failing tests:**
  - `Paste_accepts_ids_and_urls_and_refuses_other_games`.
  - `Install_creates_a_download_job_and_marks_mods_installed_on_success`.
  - `Update_available_when_workshop_is_newer`.
  - `Remove_refuses_a_mod_in_a_list_a_profile_uses`.
  - `Launcher_preset_round_trips`.
  - `Preset_import_is_bounded`: 512 KB cap and regex timeouts.
  - `Import_adds_missing_mods_as_not_installed_and_builds_the_list`.
  - `Collection_flags_and_order_are_saved`.
  - `Delete_refuses_a_list_a_profile_uses`.
  - `Duplicate_copies_items`.
  - `Viewers_cannot_change_anything`.
- [ ] **Step 2:** Run. Expected: build fails.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the Panel tests. Expected: PASS.

### Task 8: Profiles, Start with mods, keys endpoints, server install (panel)

**Files:**
- Modify:
  - `src/Panel/Features/Profiles/ProfileEndpoints.cs` (`Command` preview with mods);
  - `ProfileStore`;
  - `src/Panel/Features/Servers/ServerEndpoints.cs` (Start resolves mods, refuses `mods_missing`, deploys keys);
  - `src/Panel/Features/Profiles/ProfileEndpoints.cs` installs: `POST /api/installs/{id}/steamcmd { branch }`.
- Create: `src/Panel/Features/Mods/KeyEndpoints.cs`, `tests/Panel.Tests/ModLaunchTests.cs`, `KeyTests.cs`.

**Interfaces:**
- Consumes: T4 `LaunchSpec` mod fields; T6 `JobService`; T7 collections.
- Produces:
  - `GET /api/installs/{id}/keys`, which marks each key as `a3`, provided-by-mods (names) or unknown.
  - `POST /api/installs/{id}/keys/deploy { collectionId }`.
  - `POST /api/installs/{id}/keys/cleanup { collectionIds[] }`.

- [ ] **Step 1: Write the failing tests:**
  - `Profile_saves_its_mod_list_as_part_of_the_revision`.
  - `Start_sends_client_server_and_headless_mods`.
  - `Start_refuses_when_a_listed_mod_is_not_installed`.
  - `Optional_mods_are_not_loaded_but_their_keys_deploy`.
  - `Preview_shows_the_mod_arguments_with_agent_paths`.
  - `Server_install_job_needs_server_update_and_a_known_branch`.
  - `Key_cleanup_needs_keys_manage`.
- [ ] **Step 2:** Run. Expected: build fails.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run all suites. Expected: PASS.

### Task 9: Web — Mods library, jobs and Steam sign-in

**Files:**
- Create: `web/src/features/mods/ModsPage.tsx`, `JobsPanel.tsx`, `SteamPrompt.tsx`, `useJobs.ts`.
- Modify: `web/src/App.tsx`, `web/src/components/Sidebar.tsx` (group "Mods": Library, Mod lists), `web/src/lib/types.ts`.

What it shows:
- the library table: name, Workshop ID with link, size, status, "Update available", installed date;
- selection with Install, Update, Reinstall and Remove, plus Update all;
- the paste box;
- live job progress (indeterminate when null);
- a Steam prompt dialog (password field `autocomplete="off"`, the code field) for holders of the permission;
- the Steam username setting.

- [ ] **Step 1:** `npm run build`. Expected: exit 0.
- [ ] **Step 2:** Check in the browser against the FakeSteamCmd-backed dev agent:
  - paste IDs, install, see progress;
  - the prompt dialog accepts a code;
  - no console errors at 1440 or 375 px.

### Task 10: Web — Mod lists, preset import/export, profile Mods tab, keys, server install

**Files:**
- Create: `web/src/features/mods/CollectionsPage.tsx`, `CollectionEditor.tsx`, `KeysPanel.tsx`.
- Modify:
  - `web/src/features/profiles/ProfileTabs.tsx` (Mods tab: pick a list, show `-mod`/`-serverMod` and missing mods);
  - `App.tsx` (route `mods`);
  - `web/src/features/server/SetupPage.tsx` (install/update the server with a branch).

What it shows:
- **List editor:** the four flag columns, order (move up/down), enabled, and the bulk actions of Ruling 6.
- **Import:** an HTML file or a paste box.
- **Export:** a download.
- **Keys:** a panel per install: list, deploy for a list, cleanup with a confirmation naming the backup.

- [ ] **Step 1:** `npm run build`. Expected: exit 0.
- [ ] **Step 2:** In the browser:
  - import a preset;
  - flags and order survive a reload;
  - the profile's Startup command shows `-mod=`;
  - no sideways scroll at 375 px.

### Task 11: End to end on real SteamCMD

- [ ] **Step 1:** With the user's permission and their own Steam sign-in typed by them in the panel (never by Claude):
  - install the dedicated server into `D:\13th_server_dev\arma3server` (an `InstallRoots` entry);
  - import the user's real Launcher preset;
  - download its missing mods;
  - deploy keys.
- [ ] **Step 2: Start the profile with that list.** Expected:
  - the RPT shows the mods loaded;
  - A2S answers;
  - the keys folder holds the mods' keys and a backup exists.
- [ ] **Step 3: Restart.** Update one mod (or reinstall it), then restart. Expected: the job and progress are visible and the server restarts with it.
- [ ] **Step 4:** Scan for the secrets typed (DB, audit, logs, RPTs, agent log). Expected: none. Also scan `K:`. Expected: 0 files written.
- [ ] **Step 5:** Six screenshots (Mods library, Mod list editor, profile Mods tab, at 1440 and 375 px) and a README section.
