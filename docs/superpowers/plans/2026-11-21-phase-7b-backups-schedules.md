# Phase 7b — Backups and schedules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Console and server backups (manual, scheduled, pre-update) with download, delete, restore and retention, plus weekday-and-time schedules that restart, update and back up without ever touching an operation.

**Architecture:**
- **Server backups and restores** are agent jobs (new `JobKind`s) in the existing one-at-a-time job runner, so a pre-update backup queued before an update always runs first. Results land through an `IJobResultHandler`.
- **Console backups** run in the panel: SQLite's online backup API, then a zip with `blobs/` and `keys/`.
- **Schedules:** a panel background service ticks, claims due runs once (a conditional update, as the 6b reminders do), and runs them through the same code paths as the buttons.

**Tech Stack:** as Phase 7a: .NET 10, EF Core Sqlite (+ `Microsoft.Data.Sqlite` `BackupDatabase`), `System.IO.Compression`, SignalR client results, xUnit, React 19, TanStack Query 5. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-20-phase-7b-backups-schedules-design.md` (approved 2026-11-21).

## Global Constraints

- **Server folders:** only the agent touches them. Zips are written to a temp file and moved into place. Nothing is ever written into a read-only install root (K:\\).
- **File names:** server backups are `backups\server\server-yyyyMMdd-HHmmss[-label].zip` on the agent; console backups are `<data>\backups\console\console-yyyyMMdd-HHmmss[-label].zip`.
  - Any name given to delete, restore or download must match `^(server|console)-\d{8}-\d{6}(-[a-z-]{1,20})?\.zip$`.
- **Defaults:**
  - keep the newest **14** scheduled and **10** pre-update backups per type; manual and pre-restore backups are kept until deleted;
  - safety copies are pruned after **30** days (0 = never);
  - the unit time zone is **`Australia/Sydney`**;
  - warnings go out at **10, 5 and 1** minutes before a restart or stop;
  - the operation window runs from **2 h before** an operation's start to **4 h after** it;
  - a missed run is one more than **2 minutes** late;
  - the scheduler ticks every **30 s**.
- **Permissions:** `backups.manage` ("Make, download, restore and delete backups (they hold passwords and keys)") and `schedules.manage` ("Create and run schedules"). Both go to S4 explicitly; S1 gets them through `Permissions.All`.
- **Copy-to folders** are set on the box, not in the browser: `Backups:CopyTo` in the panel's appsettings and `Agent:BackupCopyTo` in the agent's. The page shows them read-only. See Ruling 1.
- **Copy:** the guard and failure lines are fixed exact strings:
  - "Skipped: operation {name} at {local HH:mm}";
  - "Skipped: {n} player(s) online";
  - "Steam needs you to sign in; run one update by hand";
  - "Missed: the console wasn't running at {local HH:mm}";
  - "Stop every server on this agent first".
  - Restart warnings read "Server restart in {n} minute(s)"; stop warnings read "Server shutting down in {n} minute(s)".

## Plan Rulings

1. **Copy-to folders live in each side's config file on the box,** not in the Backups page settings the spec mentions. Otherwise anyone with `backups.manage` could name the folder the agent writes zips into. *Cost:* changing the folder means editing a file on the box.
2. **Kinds are `manual`, `scheduled`, `pre-update` and `pre-restore`.** The spec's "saved as a fresh server backup first" before a restore needs a kind of its own, so retention never deletes it. *Cost:* none.
3. **A pre-update backup is taken only for server updates and Workshop updates (`Mode = update`),** not for first-time installs of a new mod, which change nothing that exists. *Cost:* none.
4. **Run now is refused (409) while that schedule has a run in progress.** *Cost:* none.

## Review Focus

1. **Daylight saving in the unit time zone:**
   - 02:30 on the day Sydney's clocks go forward (a time that doesn't exist) runs at 03:30.
   - 02:30 on the day they go back (it happens twice) runs once, at the first one.
2. **A copy-to folder that's missing or unreachable** (a network share that's down): the backup still finishes locally, and its row notes "Copy to … failed: …".
3. **Retention** never deletes a manual backup, a pre-restore backup, or the backup that just finished.
4. **A restore asked for while a server runs, or that includes a part on a read-only install:** refused with the exact message, or that part skipped and listed. Nothing half-written goes unreported.
5. **A scheduled update that hits Steam sign-in:** the job is cancelled, the run fails with the exact line, and a server that was stopped for the update is started again.

---

### Task 1: Contracts and the agent's server backups

**Files:**
- Modify: `src/Contracts/Jobs.cs`, `src/Contracts/IAgentClient.cs`, `src/Agent/AgentOptions.cs`, `src/Agent/Steam/JobRunner.cs`, `src/Agent/PanelConnection.cs`
- Create: `src/Agent/Backups/ServerBackups.cs`
- Test: `tests/Agent.Tests/ServerBackupTests.cs`; add `OnDeleteBackup`/`OnPruneSafetyCopies` to `tests/Panel.Tests/FakeAgent.cs`

**Interfaces (produces):**
- `enum JobKind { ServerInstall, WorkshopDownload, WorkshopRemove, ServerBackup, ServerRestore }`
- `JobSpec` gains `string? Backup = null, string[]? Parts = null, string? Label = null`
- `record BackupMade(string File, long SizeBytes, string? Note)`; `JobResult` gains `BackupMade? Made = null, BackupMade? Before = null` (Before = the pre-restore backup)
- `IAgentClient`: `Task<CommandResult> DeleteBackup(string file)`; `Task<int> PruneSafetyCopies(int olderThanDays)`
- Restore parts: `"profiles"`, `"configs"`, `"missions"`, `"keys"`
- `ServerBackups` (agent):
  - `Task<BackupMade> CreateAsync(string? label, CancellationToken ct)`
  - `Task<(BackupMade Before, string[] Skipped)> RestoreAsync(string file, string[] parts, CancellationToken ct)`
  - `CommandResult Delete(string file)`
  - `int PruneSafetyCopies(int days)`
- Zip layout: `profiles/…`, `configs/…`, `installs/<InstallArea>/mpmissions/…`, `installs/<InstallArea>/keys/…`. `Areas.InstallArea` names each install.
- `AgentOptions.BackupCopyTo` (string?)

- [ ] **Step 1: Write the failing tests** in `ServerBackupTests`:
  - `Create_zips_profiles_configs_and_each_installs_missions_and_keys_but_not_mods`: the zip holds `profiles/main/x.Arma3Profile`, `configs/main/server.cfg`, `installs/<area>/mpmissions/a.pbo` and `installs/<area>/keys/a.bikey`, and no `mods/` or `!Workshop` entry. The file name matches the pattern.
  - `Copy_to_folder_gets_the_same_zip_and_delete_removes_both` (Review Focus 2).
  - `An_unreachable_copy_to_folder_still_finishes_with_a_note`: `BackupCopyTo = "Z:\\nope"`; `Made.Note` starts "Copy to".
  - `Restore_refuses_while_a_server_runs`: the job fails with "Stop every server on this agent first".
  - `Restore_writes_back_the_chosen_parts_after_a_before_restore_backup`: change `profiles` after a backup, restore `["profiles"]`, and the file is back. `configs` is untouched; `Before` exists with label `before-restore`. A read-only install's parts come back in `Skipped` (Review Focus 4).
  - `Delete_and_restore_refuse_names_that_arent_backups`: `..\\x.zip`, `server-1.zip` and `console-…zip` on the agent are refused.
  - `Prune_deletes_safety_copies_older_than_the_limit_and_keeps_server_zips`: with folders under `backups\profiles\…` set to old dates, the old ones go, `backups\server\*.zip` stays, and 0 days prunes nothing.
- [ ] **Step 2: Run them, expect failures:** `& "C:\Program Files\dotnet\dotnet.exe" test tests\Agent.Tests --filter "FullyQualifiedName~ServerBackupTests"`. They should fail to compile (no `ServerBackups`).
- [ ] **Step 3: Implement.**
  - `JobRunner` routes `ServerBackup` and `ServerRestore` to `ServerBackups` without SteamCMD. Validation: a restore needs a valid `Backup` name and at least one known part.
  - `ServerBackups` needs to know whether any server is running: take `Func<bool> anyRunning`, wired to `Supervisor.Running.Count > 0`.
  - `PanelConnection` registers the two new calls.
- [ ] **Step 4: Run them, expect PASS;** then the whole Agent suite.

### Task 2: Panel backups

**Files:**
- Create: `src/Panel/Data/BackupEntities.cs` (`Backup`, `Schedule`, `ScheduleRun`; all three in one migration), migration `Phase7bBackupsSchedules`
- Create: `src/Panel/Features/Backups/ConsoleBackup.cs`, `ConsoleRestore.cs`, `BackupService.cs`, `BackupJobHandler.cs`, `BackupEndpoints.cs`
- Modify:
  - `src/Panel/Auth/Permissions.cs` (two permissions; S4 gets both);
  - `src/Panel/Settings/AppSettingsService.cs` (`UnitTimeZoneAsync`, `BackupKeepAsync`, `SafetyCopyDaysAsync`);
  - `src/Panel/Program.cs` (`restore` command first; service registrations; `MapBackups`);
  - `src/Panel/Features/Mods/InstallTools.cs` and `ModEndpoints.cs` (pre-update backup);
  - `tests/Panel.Tests/SeedingTests.cs` (S4's set)
- Test: `tests/Panel.Tests/BackupTests.cs`

**Interfaces:**
- Consumes: Task 1's contracts.
- Produces:
  - `enum BackupType { Console, Server }`; `enum BackupKind { Manual, Scheduled, PreUpdate, PreRestore }`; `enum BackupStatus { Running, Done, Failed }`
  - `Backup { Id, Type, Kind, AgentId?, FileName?, SizeBytes, Status, Error?, Note?, JobId?, CreatedById?, CreatedAt, FinishedAt? }`
  - `Schedule { Id, Name, Task, TargetId?, Scope?, Days (bitmask by DayOfWeek), MinuteOfDay, Enabled, NextRunAt?, CreatedById?, CreatedAt }`
  - `ScheduleRun { Id, ScheduleId, DueAt, StartedAt?, FinishedAt?, Outcome (Running/Done/Failed/Skipped/Missed), Detail? }`
  - `BackupService`:
    - `Task<Backup> StartAsync(BackupType type, BackupKind kind, Guid? agentId, Guid? userId)`
    - `Task<Backup> WaitAsync(Guid backupId, TimeSpan timeout)`
    - `Task<IResult?> RestoreAsync(Guid backupId, string[] parts, Guid? userId)`
    - `Task DeleteAsync(Backup b)`
    - `Task ApplyRetentionAsync()`
    - `Task<int> PruneSafetyCopiesAsync()`: calls `PruneSafetyCopies(days)` on each connected agent; returns the total
  - `ConsoleRestore.Run(string zip, string dataDir) : int` (process exit code)
  - Routes under `/api/backups`, all `backups.manage`:
    - `GET ""`
    - `POST ""` with `{type, agentId?}`
    - `GET /{id}/download`
    - `DELETE /{id}`
    - `POST /{id}/restore` with `{parts}`
    - `GET /settings` and `PUT /settings` with `{keepScheduled, keepPreUpdate, safetyCopyDays}`; the copy-to folders are read-only in the GET

- [ ] **Step 1: Write the failing tests** in `BackupTests`:
  - `Console_backup_zips_a_consistent_database_blobs_and_keys`: the zip's `console.db` opens; `PRAGMA integrity_check` = "ok"; it has the Owner user; entries exist under `blobs/` and `keys/`.
  - `Server_backup_runs_as_an_agent_job_and_the_result_fills_in_the_row`: the FakeAgent gets `RunJob` with `ServerBackup`. After `SendJobFinishedAsync(... Made = new("server-20261121-030000.zip", 1234, null))`, the row is Done with that file and size.
  - `Server_and_mod_updates_take_a_pre_update_backup_first`: the `RunJob` order is `ServerBackup`, then `ServerInstall` (and then `WorkshopDownload` for `Mode = "update"`); there's no backup for `Mode = "install"`.
  - `Retention_keeps_the_newest_and_never_manual_pre_restore_or_the_newest` (Review Focus 3): seed 16 scheduled, 12 pre-update, 3 manual and 1 pre-restore server rows; `ApplyRetentionAsync`. Then 14 + 10 + 3 + 1 remain, and `DeleteBackup` was called for the 4 oldest.
  - `Restore_refuses_console_backups_and_records_the_before_restore_backup`: console → 400 with "Panel.exe restore"; server → a `ServerRestore` job with parts. After the result with `Before`, a `PreRestore` row exists.
  - `Backups_need_backups_manage`: S3 gets 403 on list, download and create; S4 gets 200.
  - `Backups_are_audited`: creating, restoring and deleting write `backup.created`, `backup.restored` and `backup.deleted`.
  - `Console_restore_command_swaps_in_the_backup_and_keeps_the_old_data_aside`: `ConsoleRestore.Run` on a temp data dir. The old `console.db`, `blobs` and `keys` move to `before-restore-<time>/`, the zip's are in place, it returns 0, and it returns 2 for a zip without `console.db`.
- [ ] **Step 2: Run, expect failures** (compile).
- [ ] **Step 3: Implement.**
  - **Console backup:** one at a time (`SemaphoreSlim`), off the request (`Task.Run` plus a row), and copy to `Backups:CopyTo`, noting any failure.
  - **Downloads:** console files stream from disk; server files stream through the agent's `FileRead` on the `backups` space in chunks, like the file manager's download.
  - **`ApplyRetentionAsync`** runs after each backup finishes.
- [ ] **Step 4: Run, expect PASS;** then the full Panel suite. Expect the S4 set and permission tests to need the two new permissions.

### Task 3: The scheduler

**Files:**
- Create: `src/Panel/Features/Schedules/ScheduleClock.cs`, `ScheduleRunner.cs`, `Scheduler.cs` (BackgroundService), `ScheduleOptions.cs`
- Modify: `src/Panel/Features/Notifications/NotificationEvents.cs` (event `schedule.notrun`, permission `schedules.manage`, in-app on and Discord on)
- Modify: `src/Panel/Features/Mods/InstallTools.cs` and `ModEndpoints.cs`, extracting the internals the scheduler calls:
  - `InstallTools.QueueServerUpdateAsync(...) : (Job?, IResult?)`
  - `ModEndpoints.QueueUpdatesAsync(...) : (Job[], IResult?)`

  The endpoints keep their behaviour.
- Test: `tests/Panel.Tests/ScheduleTests.cs`; `PanelFactory` sets `Schedules:TickSeconds=0`, `Schedules:MinuteSeconds=0.01`.

**Interfaces:**
- Consumes: Task 2's entities and `BackupService`; `ServerEndpoints.Control`; `AgentCalls` (RCon `say -1 …`); `Notifier`.
- Produces:
  - `ScheduleClock.Next(int days, int minuteOfDay, TimeZoneInfo tz, DateTime afterUtc) : DateTime` (UTC). A nonexistent local time moves forward by the gap; an ambiguous one takes the first occurrence.
  - Task keys: `"restart"`, `"stop"`, `"start"` (target = profile id), `"update-server"` (target = install id), `"update-mods"`, `"backup"` (scope `"console" | "server" | "both"`, target = agent id for server).
  - `ScheduleRunner.RunDueAsync(DateTime nowUtc) : Task<int>` (runs claimed).
  - `ScheduleRunner.RunNowAsync(Guid scheduleId, Guid? userId) : Task<IResult?>`.
  - `ScheduleRunner.RunAsync(Schedule s, ScheduleRun run) : Task` (executes; sets the outcome and detail).

- [ ] **Step 1: Write the failing tests** in `ScheduleTests`:
  - `Next_run_follows_weekdays_and_the_unit_time_zone_across_daylight_saving` (Review Focus 1), all in Australia/Sydney:
    - Fri and Sun 18:30 from Wed 2026-11-18 is Fri 2026-11-20 07:30Z (AEDT);
    - 02:30 on 2026-10-04 (clocks go forward) is 2026-10-03 16:30Z, i.e. 03:30 AEDT;
    - 02:30 on 2027-04-04 (clocks go back) is 2027-04-03 15:30Z, the first 02:30.
  - `A_due_schedule_runs_once_even_when_two_ticks_race`: two concurrent `RunDueAsync` calls give one `ScheduleRun` and one Restart call.
  - `A_run_missed_while_the_panel_was_down_is_recorded_not_run_and_notifies`: `NextRunAt` 10 minutes ago gives outcome Missed, the exact line, no agent call, a `schedule.notrun` notification, and `NextRunAt` moved forward.
  - `Restart_is_skipped_within_an_operations_window_and_says_which`: an operation at now + 1 h gives Skipped "Skipped: operation Op Thunder at HH:mm" with no Restart call; at now − 5 h it runs.
  - `Restart_with_players_on_warns_at_10_5_1_then_restarts`: status with 3 players and RCon Connected gives RCon calls `say -1 Server restart in 10 minute(s)`, `… 5 …` and `… 1 minute(s)`, then Restart.
  - `Update_server_is_skipped_with_players_on`: Skipped "Skipped: 3 player(s) online".
  - `Update_server_on_an_empty_running_server_stops_backs_up_updates_and_starts_again`: the order is Stop, `RunJob(ServerBackup)`, `RunJob(ServerInstall)` (finish both), then Start.
  - `A_scheduled_update_that_needs_Steam_sign_in_fails_cancels_and_restarts_the_server` (Review Focus 5): a `JobUpdate` with a Password prompt gives `CancelJob`, outcome Failed with the exact line, and Start again.
  - `Backup_runs_inside_an_operations_window`.
  - `Run_now_is_refused_while_a_run_is_in_progress`: 409.
  - `Safety_copies_are_pruned_once_a_day_on_each_agent`: the first tick of a day calls `PruneSafetyCopies(30)` on the connected agent; a second tick the same day doesn't. The last prune date is kept in a settings row, `lastSafetyPrune`.
- [ ] **Step 2: Run, expect failures** (compile).
- [ ] **Step 3: Implement.**
  - **Claiming a run:** `ExecuteUpdate` where `Id = s.Id && NextRunAt == due` sets `NextRunAt = ScheduleClock.Next(...)`. Only the claimer inserts the run.
  - **Waiting on jobs:** poll the Job row every `MinuteSeconds` / 12 until it's terminal or NeedsInput.
  - **The "operations window"** counts operations that aren't cancelled.
- [ ] **Step 4: Run, expect PASS;** then the full Panel suite.

### Task 4: Schedule API, unit time zone, dashboard

**Files:**
- Create: `src/Panel/Features/Schedules/ScheduleEndpoints.cs`
- Modify: `src/Panel/Features/Settings/SettingsEndpoints.cs` (`unitTimeZone`, validated with `TimeZoneInfo.TryFindSystemTimeZoneById`), `src/Panel/Program.cs`
- Test: `tests/Panel.Tests/ScheduleApiTests.cs`

**Interfaces (produces):**
- `/api/schedules` with `schedules.manage`:
  - `GET ""`: each schedule with `nextRunAt`, `lastRun`, `targetName`;
  - `POST ""` and `PUT /{id}` with `{name, task, targetId?, scope?, days, minuteOfDay, enabled}`;
  - `DELETE /{id}`;
  - `POST /{id}/run`;
  - `GET /{id}/runs`, the newest 50.
- `GET /api/schedules/next` (`server.view`): the next 3 enabled runs as `{name, task, at}`.
- Settings DTO gains `unitTimeZone`.

- [ ] **Step 1: Failing tests:**
  - `Schedules_validate_their_fields`: days 0 and 128 are 400; minute 1440 is 400; an unknown task is 400; a restart without a profile is 400; a backup with scope `server` and no agent is 400.
  - `Saving_a_schedule_sets_its_next_run_and_disabling_clears_it`.
  - `Schedules_need_schedules_manage_and_next_is_for_server_view`: S3 gets 403 on the list and 200 on `/next`.
  - `The_unit_time_zone_is_validated_and_changes_next_runs`: `Mars/Base` is 400; changing to `Australia/Brisbane` recomputes `NextRunAt`.
  - `Schedules_are_audited`: create, update, delete and Run now write `schedule.created`, `schedule.updated`, `schedule.deleted` and `schedule.run`; scheduled runs also write `schedule.run`, with no actor.
- [ ] **Step 2: Run, expect failures.** **Step 3: Implement.** **Step 4: Run, expect PASS;** then the full Panel suite.

### Task 5: Web

**Files:**
- Create: `web/src/features/backups/BackupsPage.tsx`, `web/src/features/schedules/SchedulesPage.tsx`
- Modify: `web/src/App.tsx` (routes `backups` and `schedules`), the side bar (Backups and Schedules under Server, shown by permission), `web/src/features/dashboard/DashboardPage.tsx` ("Next scheduled"), the settings page (Unit time zone), `web/src/lib/types.ts`

- [ ] **Step 1: Build the pages.**
  - **Backups page:**
    - **The list:** type, kind, size, when, who, status, with Download and Delete. Restore (server only) opens a dialog with part checkboxes that says "Every server on this agent must be stopped; the current files are backed up first".
    - **Back up now:** console or server (an agent picker).
    - **Settings:** the keep-counts and the safety-copy days, plus the copy-to folders read-only, labelled "set in appsettings.json on the box".
    - Console rows' Restore explains the `Panel.exe restore` command.
  - **Schedules page:**
    - **The list:** name, task and target, days and time (unit time zone shown), next run, last outcome, an on/off switch, and Run now.
    - **New/Edit dialog:** task, then target (profile, install or agent according to the task), weekday chips, time, and a name.
    - **History:** an expandable panel with the last 50 runs.
  - **Rules:** dialogs say exactly what will happen; there are empty states; no sideways scroll at 375 px.
- [ ] **Step 2:** `npm run build` exits 0.

### Task 6: Docs, live run, review, report

- [ ] **README:** a Backups section and a Schedules section, the new permissions, and the Roles table (S1/S4).
- [ ] **Deploy guide:**
  - "Restoring the console" (stop the service, `Panel.exe restore <zip>`, start it);
  - "Backups somewhere else" (`Backups:CopyTo` and `Agent:BackupCopyTo`);
  - the unit time zone.
- [ ] **Live run** (dev PC; the dev database copied aside first):
  - a console backup (open the zip: `console.db`, `blobs`, `keys`) and a server backup on the dev agent, then download both;
  - restore the server backup's `configs` with the servers stopped, and check the before-restore backup;
  - a backup schedule 2 minutes ahead runs and appears in history and on the dashboard;
  - a restart schedule on a temporary profile with an operation in 1 h is skipped with its line;
  - the pages at 1440 and 375 px.
- [ ] **Review and close:** a fresh review, one test-first fix pass, the full suites with the expected test counts, the 20-point checklist, and the report.
