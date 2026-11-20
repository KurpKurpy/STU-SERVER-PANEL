# Phase 5b — Server Missions and Start Mission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Know which addons and maps the server install and every mod provide, show for each server which of its missions a mod list can run, and let Zeus and mission makers start a listed mission themselves.

**Architecture:**
- **Scanning:** the agent scans, because it owns the files. It reads only PBO headers and the `config.bin`/`config.cpp` entries (decompressing BI LZSS when needed), takes `CfgPatches` and `CfgWorlds` class names, and reports them through the hub like deployments.
- **Storage:** the panel keeps one row per name per mod or install.
- **Compatibility:** one pure function in the panel, over a mission version's required addons, its map and a mod list.
- **Start Mission:** a scoped action. It saves a profile revision with a one-mission rotation and the chosen mod list, applies it (allowed while running, because `server.cfg` isn't locked), then starts or restarts the server.

**Tech Stack:** as Phase 4a: .NET 10 (10.0.15), EF Core Sqlite, SignalR client results, React 19 + TanStack Query 5 + Tailwind v4, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md`. The parts that matter:
- Amendment A;
- §3 "Which addons a mod provides";
- §5.4 "Server Missions tab and Start Mission";
- §7 `missions.start`;
- §8 Phase 5 `ModAddon`/`InstallAddon`;
- §11 Phase 5 "Done when";
- §13 calls 18–21.

It also completes the Phase 4a deferral (Phase 4a Plan Ruling 1): the source spec §8 map and mod checks.

## Global Constraints

- **.NET:** run it as `"C:\Program Files\dotnet\dotnet.exe"`; packages pinned at 10.0.15. Stop the `panel` preview before `dotnet test`.
- **Git:** there's no git repository and the user handles git. Commit nothing; the ledger replaces commit steps.
- **Disks:** `K:\` is read-only. Reading it is fine; nothing is ever written there (`Agent:ReadOnlyInstallRoots`). The dev agent's data root is `D:\13th_server_dev`. Start agents and servers with `$env:RESHADE_BASE_PATH_OVERRIDE='D:\13th_server_dev\reshade'`.
- **Agent path rule (design §5.3):** the panel names mods (`ModRef`) and installs (registered roots). The agent maps them to folders under its own roots.
- **No guessing (design §3):** a mod whose configs can't be read is flagged "couldn't scan" instead of being guessed at.
- **Permissions and audit:** every endpoint requires a permission, and every state change is audited.
- **User data:** the user's missions and mods never go into repo fixtures. Tests synthesize PBOs, raP, LZSS and text configs.
- **Pre-deploy security checklist** (the user's 20 points) runs at the end, after the final review.

## Plan Rulings

1. **Scope.**
   - **This plan builds:** `CfgPatches`/`CfgWorlds` scanning of mods and installs, the compatibility check, the Server Missions tab, the mission page's mod-list compatibility, and Start Mission.
   - **Not built:** RCon `#mission` (Phase 7, design call 20); Creator DLC loading (no profile loads CDLC yet); Workshop search (Phase 7 settings); the 4b file manager.
2. **What the scan reads (spike facts below):**
   - every `*.pbo` in a mod's `addons` folder (not `optionals`);
   - for an install, its root `addons` plus the `addons` of every top-level folder, each recorded with its folder name;
   - from each PBO, every entry named `config.bin` or `config.cpp` at any depth.
   - Compressed entries are decompressed with BI LZSS, whose checksum must match.
   - `.ebo` files are unreadable by design.
3. **Text configs** are read by a token-level `CfgPatches` reader, not the full text parser (S7):
   - it skips comments, strings and `#` lines (with `\` continuations);
   - it takes the direct child classes of the first top-level `class CfgPatches`.
   - **A name we can't trust** marks that config unreadable: one `#define`d in the same file, one followed by `(`, or an ALL_CAPS name in a file with `#include`. The rest of the mod still counts.
4. **raP enum table:** the declared count isn't trusted (S2). Entries are read until the end of the file, and each must be complete.
5. **Storage** (design §8, with one change):
   - `ModAddon(ModId, Name, Kind)` and `InstallAddon(ServerInstallId, Folder, Name, Kind)`, where `Kind` is `addon` or `world`. Worlds share the tables instead of getting two more.
   - `Mod` and `ServerInstall` gain `ScanStatus` (`NotScanned`, `Scanned`, `Partial`, `Failed`), `ScannedAt` and `ScanNote` (the unreadable files, at most 2,000 characters).
6. **When scans run:**
   - a mod after an install, update or reinstall job succeeds;
   - an install after its server install/update job succeeds;
   - on an agent's hello, every installed mod and registered install of that agent whose `ScanStatus` is `NotScanned`;
   - a **Rescan** button (mods: `mods.manage`; installs: `server.setup`).
   - Each scan replaces that target's rows.
   - The agent runs one scan at a time and reports each target through `IAgentHub.AddonsScanned`. Reports wait in the agent's unsent list.
7. **What a mod list provides:**
   - the install's root `addons` and its official DLC folders;
   - plus every **enabled** item flagged Client or Server-only, so Optional-only and Headless-only items don't count (design §5.4).
   - Creator DLC folders (`gm`, `vn`, `csla`, `ws`, `spe`, `rf`, `ef`, `contact`) don't count, because no profile loads them yet (Ruling 1).
   - Worlds come from the same sources.
8. **Compatibility** of one mission version with one mod list. `Compatibility.Check(Required, Map, Provided)`, where `Provided` holds the addons, the worlds, and the not-fully-scanned mods with their names. The result:
   - **`Ready`:** every required addon is provided, and the map is a provided world.
   - **`Missing`:** some addon or the map isn't provided, and nothing unscanned could provide it.
     - Each missing addon is named with Eden's mod name, or "not recorded".
     - It also names the library mod that provides it, when a scanned library mod outside the list does.
   - **`Unknown`:** everything not provided is unattributed, and the list holds a not-fully-scanned mod that could account for it. The UI says which mod couldn't be read.
     - "Unattributed" means no scanned library mod provides it, and Eden's mod name for it is either not recorded or matches that mod's name (normalised: lower case, letters and digits only).
   - **Display:** the Server Missions tab lists `Ready` and `Unknown`, with Unknown marked "couldn't confirm". "Show missions that don't match" adds `Missing`, with the reasons.
   - **Cost if wrong:** an `Unknown` mission that really can't run is listed with a warning instead of being hidden.
9. **Which missions are "on the server":** library missions (not archived) that have a `Done` deployment on the profile's install. Their required addons come from the **deployed** version, not the current one.
   - Unmanaged PBOs in `mpmissions` aren't listed (YAGNI). Admins see them in the rotation picker already.
10. **The source spec §8 checks (Phase 4a deferral):**
    - The map check and the mod check are compatibility results shown on the Server Missions tab and the mission page. They don't block a deploy: under Amendment A every save goes onto the server, and a file that can't start harms nothing.
    - Start Mission refuses a `Missing` mission.
11. **Start Mission** (`POST /api/servers/{id}/start-mission`, `missions.start`). The body is `{ missionId, modListId, players }`, where `players` is the count the confirmation showed.
    - **Refuses with 409** in these cases:
      - the mission isn't Done on this install, or its compatibility is `Missing`;
      - the agent is offline, or the install has an install job running;
      - the saved passwords can't be read;
      - `players` no longer matches A2S (`stale_players`, so the person confirms again).
    - **Otherwise:**
      1. It saves a revision from the **head** snapshot with `Rotation = [ (mission file name without .pbo, previous first entry's difficulty or "Regular", "") ]` and `ModCollectionId = modListId`. The note is "Start Mission: <file> with <list>", by the caller.
      2. It applies that revision with the shared apply core, which allows a running server here only.
      3. It calls Start when the server is offline or errored, and Restart otherwise, through the existing control code (mod check and key deploy included).
      4. It audits `mission.started`.
    - **Ownership:** it never changes other settings. Anything the head had unapplied gets applied too, because the head is the base. The confirmation says so when the head is newer than the applied revision.
    - **Cost if wrong:** a Zeus applies an admin's half-finished edits. The confirmation names that case.
    - **Amended in Task 7 (live run):** steps 2–3 were wrong for a running server. The agent's ConfigWriter refuses every apply while the profile runs (Phase 3: Arma rewrites its profile file at exit), and S6 only checked that `server.cfg` isn't locked. A running server is now stopped through the control code, the panel waits until the agent reports it offline (up to `StartMission:StopWaitSeconds`, 150), then applies with the ordinary apply core and calls Start. If the apply fails after the stop, the previous applied revision is started again; if the stop doesn't finish in time, nothing is written. This also retires the final review's I-4 refusal (`difficulty_needs_stop`): the exit rewrite now happens before the new files land.
12. **Start Mission preview** (`GET /api/servers/{id}/start-mission?missionId&modListId`, `missions.start`) returns:
    - the action (`start` or `restart`) and the A2S player count;
    - whether the mod list changes;
    - whether unapplied head changes ride along;
    - the compatibility.
13. **`missions.start`** is granted by default to Server Administrator, Zeus and Mission Maker (design §7). The Owner has every permission.
14. **The scan isn't a Job row.** It's quick (dev install about 10 s cold) and has no prompts. Its state lives on the scanned row (`ScanStatus`), which the Mods page and Setup show.

## Spike facts (gathered before this plan; scratch harness, read-only)

- **S1 Mods:** CBA (32 PBOs) and ACE (196) read fully: all `config.bin`, none compressed. CBA gives 53 `CfgPatches` (`cba_main` included); ACE gives 317 (`ace_realisticnames` included).
- **S2 The dev server install** (`D:\13th_server_dev\arma3server`, 482 PBOs, about 10 s cold):
  - Results: 1,493 `CfgPatches` (including `A3_Characters_F`, `A3_Air_F_Heli_Heli_Transport_03`, `A3_Map_Altis`, `A3_Map_VR`) and `CfgWorlds` Altis, Stratis, Tanoa, Malden, Enoch, VR. Folders: root, aow, argo, curator, enoch, expansion, heli, jets, kart, mark, orange, tacops, tank.
  - **Failures:** 5 configs fail in our reader: `3den`, `editor_f`, `ui_f`, `ui_f_enoch` and `missions_f_oldman`. The class walk is fine; their **enum table's declared count is larger than the entries present** (3den declares 24 and holds 14). Ruling 4.
- **S3 The K: client install with about 280 Workshop mods** (4,176 PBOs plus 75 `.ebo`):
  - 1,599 compressed configs. BI LZSS decompresses 1,595 of them with a matching checksum.
  - The lenient raP walk fails on none.
  - 376 text configs are read by the token reader; 29 have macro names.
  - **Folders with something unreadable after all that:** 11 of about 280: 3 `.ebo` (the WS and Expeditionary Forces DLC), 7 macro-name mods, 1 LZSS.
  - With only the plain reader, 65 folders failed.
- **S4 Real missions:** 410 of the user's missions (K: MPMissions plus Eden folders).
  - 58 have every required addon on this PC. Most of the rest use mods that aren't installed here (3AS, JLTS and similar).
  - Eden's `addonsMetaData` names those mods but with **empty URLs**, so matching by Workshop ID from the metadata isn't possible. Names are for display and the `Unknown` rule only.
- **S5 Maps:** the mission's map (the part after the last `.` in the file name) is a `CfgWorlds` class for 295 of 410 missions on K:. The rest are maps whose mods aren't installed.
- **S6 Configs while running:** while Arma runs a mission, `configs\<slug>\server.cfg` opens for writing. Ruling 11 depends on this.
- **S7 Text configs:** `ConfigDocument.Parse` fails on many real mod `config.cpp` files (macros, `delete` without `=`, the unit's own 13th STU mods among them). Hence the token reader (Ruling 3).

## Review Focus

1. **Hostile mod files:** a PBO or config crafted to make the scan loop, allocate without limit or hang. Examples: a header count, an LZSS length, a raP body shared or self-referencing (Phase 4a I-1), a text config with an unterminated comment or string. Expected: the scan bounds time and memory per file, marks the file unreadable and continues. (T1 `Lzss_refuses_bad_input`, `Header_refuses_hostile_counts`, `Text_patches_survive_unterminated_input`; T2 `A_bad_pbo_is_marked_unreadable_and_the_scan_goes_on`.)
2. **The scan and the files disagreeing:** a mod updated or reinstalled, or an install updated. Expected: its rows are replaced by the next scan, which the job triggers, and nothing lists addons from the old files. (T3 `A_successful_mod_job_rescans_the_mod`, `A_new_scan_replaces_the_rows`.)
3. **Start Mission racing other changes:** an admin edits the profile, a second Start Mission, players joining after the confirmation, the server stopping meanwhile. Expected: one revision per action, a stale player count refused, and an offline server started rather than restarted. (T5 `Start_mission_refuses_a_stale_player_count`, `Start_mission_on_an_offline_server_starts_it`.)
4. **Permissions:** a Zeus can start a listed mission but can't edit the profile otherwise or start a `Missing` one. A member without `missions.start` can't. (T5 `Zeus_starts_a_listed_mission_and_nothing_else`, `Missing_missions_cant_be_started`.)
5. **Compatibility edges:** case differences in addon and map names, a mission with no addons, a list with a disabled or Optional-only mod, an `Unknown` caused by one unreadable mod. Expected: Rulings 7–8 exactly. (T4 tests.)

---

### Task 1: Formats for scanning (ArmaFormats)

**Files:**
- Create: `src/ArmaFormats/Lzss.cs`, `src/ArmaFormats/ConfigScan.cs`
- Modify: `src/ArmaFormats/Rap.cs` (the enum table, Ruling 4), `src/ArmaFormats/Pbo.cs` (header-only read and one entry's data)
- Test: `tests/ArmaFormats.Tests/LzssTests.cs`, `ConfigScanTests.cs`, `PboTests.cs`, `RapTests.cs`; test helper `LzssEncoder.cs`

**Interfaces:**
- Produces:
  - `Lzss.Decompress(ReadOnlySpan<byte> input, int length) → byte[]`. It throws `PboException` on truncated input, an out-of-range length (cap 64 MB) or a bad checksum.
  - `PboHeader(IReadOnlyDictionary<string,string> Properties, IReadOnlyList<PboHeaderEntry> Entries, long DataStart)` and `PboHeaderEntry(string Path, uint Packing, uint OriginalSize, uint DataSize, long Offset)`.
  - `Pbo.ReadHeader(Stream) → PboHeader`, with the same name and count checks as `Read`.
  - `Pbo.ReadEntry(Stream, PboHeader, PboHeaderEntry) → byte[]`, which decompresses `Cprs` or `OriginalSize != 0 && != DataSize`, with an entry cap of 64 MB for scanning.
  - `ConfigNames(string[] Addons, string[] Worlds, string? Unreadable)` and `ConfigScan.Names(byte[] config) → ConfigNames`. For raP it uses `Rap.Read`; for text, the token reader. `Unreadable` is the reason, for example "macro class names".

- [ ] **Step 1: Write failing tests.**
  - **`Rap_reads_an_enum_table_whose_count_is_larger_than_its_entries`:** RapBuilder plus an appended enum table declaring 3 with 1 entry → it reads. `Rap_still_refuses_a_cut_enum_entry` → `RapException`.
  - **`Lzss_round_trips_literals_and_back_references`:** a test-only encoder (literals plus one 3–18 byte back-reference) → equal bytes. `Lzss_fills_a_reference_before_the_start_with_spaces`.
  - **`Lzss_refuses_bad_input`:** a bad checksum, a truncation, a length of 0 or over 64 MB.
  - **`Header_reads_entries_without_data` and `Read_entry_decompresses_a_compressed_config`.**
  - **`Header_refuses_hostile_counts`:** more than 10,000 entries, and a name over 1,024 bytes.
  - **`Patches_and_worlds_from_raP`:** `CfgPatches{a;b}`, `CfgWorlds{Altis;VR}` → those names; nested `CfgPatches` inside another class are ignored.
  - **`Patches_from_text_skip_comments_strings_and_directives`:** includes `#define X \` continuations.
  - **`Text_macro_names_are_unreadable`:** a `#define`d name, `class DOUBLES(a,b)`, and ALL_CAPS with `#include`.
  - **`Text_patches_survive_unterminated_input`:** an open comment or string gives a result, never an exception or a hang.
- [ ] **Step 2: Run the tests, then implement until they pass.** Run: `dotnet test tests/ArmaFormats.Tests`. Expected: RED for the reasons named, then GREEN.

### Task 2: Agent scanning

**Files:**
- Create: `src/Agent/Mods/AddonScanner.cs`
- Modify: `src/Contracts/Mods.cs`, `src/Contracts/IAgentClient.cs`, `src/Agent/PanelConnection.cs`, `src/Agent/Program.cs`
- Test: `tests/Agent.Tests/AddonScannerTests.cs`

**Interfaces:**
- Produces (Contracts):
  - `AddonScanRequest(ModRef[] Mods, string[] InstallRoots)`;
  - `ScannedFolder(string Folder, string[] Addons, string[] Worlds, string[] Unreadable)`;
  - `AddonScan(ModRef? Mod, string? InstallRoot, ScannedFolder[] Folders, string? Error)`;
  - `IAgentClient.ScanAddons(AddonScanRequest) → CommandResult`, which answers at once after validating every ref and root;
  - `IAgentHub.AddonsScanned(AddonScan)`.
- Agent: `AddonScanner(Areas, ModStore, ILogger)` with `Enqueue(request) → CommandResult` and `event Action<AddonScan> Scanned`. Scans run one at a time.
  - A mod has one folder, `""`.
  - An install gets `""` for its root `addons`, plus each top-level folder with an `addons` subfolder, by folder name.
  - `Unreadable` holds the file name and reason, for example `"ws_data.ebo: encrypted"` or `"x.pbo: macro class names"`.

- [ ] **Step 1: Write failing tests:**
  - `Scans_a_mod_and_reports_its_addons` (a synthetic mod folder with two PBOs, one raP and one text);
  - `Scans_an_install_by_folder`;
  - `A_bad_pbo_is_marked_unreadable_and_the_scan_goes_on`;
  - `Ebo_files_are_unreadable_not_errors`;
  - `Unknown_refs_and_roots_are_refused`;
  - `Read_only_installs_can_be_scanned` (reading is allowed);
  - `Scans_run_one_at_a_time`.
- [ ] **Step 2: Implement until they pass.** `PanelConnection` relays `Scanned` through the unsent list. Run: `dotnet test tests/Agent.Tests`.

### Task 3: Panel scan storage and triggers

**Files:**
- Modify: `src/Panel/Data/Entities.cs` (Mod, ServerInstall scan columns, `ScanStatus`), `src/Panel/Data/AppDb.cs`, the migration `Phase5bScans`, `src/Panel/Hubs/AgentHub.cs`, `src/Panel/Features/Mods/ModEndpoints.cs` (the job handler, Rescan, DTO), `src/Panel/Features/Mods/InstallTools.cs` (install Rescan and scan status)
- Create: `src/Panel/Features/Mods/AddonScans.cs`
- Test: `tests/Panel.Tests/AddonScanTests.cs`; `FakeAgent` gains `OnScanAddons` and `SendAddonsScannedAsync`

**Interfaces:**
- Produces: `AddonScans(AppDb, AgentCalls, AuditLog)` with:
  - `RequestAsync(Guid agentId, IEnumerable<Mod> mods, IEnumerable<ServerInstall> installs)`, which sets nothing on failure;
  - `ApplyAsync(Guid agentId, AddonScan scan)`, which replaces rows, sets the status (`Partial` when any `Unreadable`) and `ScannedAt`;
  - `RescanUnscannedAsync(Guid agentId)`, called on hello.
- `ModDto` and the install DTO gain `ScanStatus`, `ScannedAt` and `ScanNote`.

- [ ] **Step 1: Write failing tests:**
  - `A_scan_report_stores_addons_and_worlds`;
  - `A_new_scan_replaces_the_rows`;
  - `Unreadable_files_make_the_scan_partial`;
  - `A_successful_mod_job_rescans_the_mod`;
  - `A_server_install_job_rescans_the_install`;
  - `Hello_rescans_what_was_never_scanned`;
  - `Rescan_needs_mods_manage_or_server_setup`;
  - `A_report_for_an_unknown_mod_or_another_agents_install_is_ignored`.
- [ ] **Step 2: Implement until they pass.** Run: `dotnet test tests/Panel.Tests --filter AddonScanTests`.

### Task 4: Compatibility and the Server Missions list

**Files:**
- Create: `src/Panel/Features/Missions/Compatibility.cs`, `src/Panel/Features/Missions/ServerMissionsEndpoints.cs`
- Modify: `src/Panel/Auth/Permissions.cs` (`missions.start`, Ruling 13), the seeding lists in tests, `Program.cs`
- Test: `tests/Panel.Tests/CompatibilityTests.cs` (the pure function), `tests/Panel.Tests/ServerMissionsTests.cs`

**Interfaces:**
- Produces:
  - `Provided(HashSet<string> Addons, HashSet<string> Worlds, (string ModName, string[] Unreadable)[] Unscanned)`, all sets ignoring case;
  - `Compatibility.ProvidedAsync(AppDb, Guid installId, Guid? modListId) → Provided` (Ruling 7);
  - `CompatResult(string Status, string? MissingMap, MissingAddon[] Missing, string[] UnreadableMods)` and `MissingAddon(string Addon, string? EdenMod, string? LibraryMod)`;
  - `Compatibility.Check(IReadOnlyList<(string Addon, string? ModName)> required, string map, Provided provided) → CompatResult`;
  - `GET /api/servers/{id}/missions?modListId=` (`missions.view`): `{ modListId, modLists:[{id,name}], missions:[{missionId, displayName, fileName, version, status, missingMap, missing, unreadableMods}] }`. The default list is the applied revision's;
  - `GET /api/missions/{id}/compatibility` (`missions.view`): one row per mod list against each install the mission is Done on.

- [ ] **Step 1: Write failing tests:**
  - **The pure function:** `Ready_when_every_addon_and_the_map_are_provided`; `Names_and_maps_ignore_case`; `Missing_names_eden_mod_and_library_mod`; `Missing_map_alone_is_missing`; `Unknown_only_when_an_unreadable_mod_could_account_for_it` (Eden names it, or not recorded); `Eden_naming_another_mod_keeps_it_missing`; `No_required_addons_is_ready_when_the_map_exists`.
  - **The list endpoint:** `Optional_and_headless_only_and_disabled_items_dont_provide`; `Creator_dlc_folders_dont_provide`; `Server_missions_lists_only_done_missions_on_this_install_with_the_deployed_version`; `The_applied_list_is_the_default`; `A_mission_using_a_mod_is_listed_only_for_lists_with_it` (the Phase 5 Done-when, in a test).
- [ ] **Step 2: Implement until they pass.** Run: `dotnet test tests/Panel.Tests --filter "CompatibilityTests|ServerMissionsTests|PermissionTests|SeedingTests"`.

### Task 5: Start Mission

**Files:**
- Modify: `src/Panel/Features/Profiles/ApplyEndpoints.cs` (extract `ApplyCoreAsync(Target, revision, bool allowRunning)`), `src/Panel/Features/Servers/ServerEndpoints.cs` (Control usable from code with the action and the caller)
- Create: `src/Panel/Features/Missions/StartMission.cs`
- Test: `tests/Panel.Tests/StartMissionTests.cs`

**Interfaces:**
- Consumes: `Compatibility` (T4), `ProfileStore.SaveRevisionAsync`, `Control.RunAsync`.
- Produces:
  - `GET /api/servers/{id}/start-mission?missionId&modListId` → `StartMissionPreview(string Action, int? Players, bool ModListChanges, bool UnappliedChanges, CompatResult Compat)`;
  - `POST /api/servers/{id}/start-mission` with body `StartMissionBody(Guid MissionId, Guid ModListId, int? Players)` → `202 { revision }`.

- [ ] **Step 1: Write failing tests:**
  - `Start_mission_saves_a_one_mission_rotation_with_the_list_applies_and_starts`;
  - `Start_mission_on_a_running_server_applies_and_restarts`;
  - `Start_mission_on_an_offline_server_starts_it`;
  - `Start_mission_refuses_a_stale_player_count`;
  - `Missing_missions_cant_be_started`;
  - `A_mission_not_done_on_this_install_cant_be_started`;
  - `Zeus_starts_a_listed_mission_and_nothing_else` (Zeus is still 403 on profile PUT and apply);
  - `Members_without_missions_start_get_403`;
  - `The_preview_names_restart_players_and_unapplied_changes`;
  - `Start_mission_is_audited`.
- [ ] **Step 2: Implement until they pass.** Run: `dotnet test tests/Panel.Tests --filter "StartMissionTests|ApplyTests|ServerTests"`.

### Task 6: Web

**Files:**
- Create: `web/src/features/servers/ServerMissions.tsx`
- Modify:
  - the Server page (a **Missions** tab);
  - `web/src/features/missions/MissionPage.tsx` (the "Mod lists" panel);
  - `web/src/features/mods/ModsPage.tsx` (scan status and Rescan);
  - the Setup install rows (scan status and Rescan);
  - `web/src/lib/types.ts`.

**What the person sees:**
- **The Missions tab:**
  - a mod-list picker, defaulting to the applied list, which it labels "on the server";
  - missions with a status chip (`Ready`, `Couldn't confirm`, `Doesn't match`) and the reasons: missing map, missing addons grouped by Eden mod or library mod, unreadable mods;
  - the "Show missions that don't match" toggle;
  - **Start mission** on Ready and Unknown rows, for `missions.start` holders.
- **The confirm dialog** spells out the preview: "Restarts the server; 14 players will be disconnected", "Switches the mod list to X", "Also applies changes saved on the profile since it was last applied".
- **The mission page:** a table of mod lists × status per install.
- **Mods and Setup:** a scan status (Scanned / Partly scanned / Not scanned / Failed) with a hover listing the unreadable files, and Rescan.

- [ ] **Step 1:** Build it, then `npm run build`. Expected: exit 0.
- [ ] **Step 2:** Check it in the browser at 1440 and 375 px against the built panel. Check: the console is clean, the content policy isn't violated, and there's no sideways scroll at 375.

### Task 7: Real run, docs

- [ ] **Step 1: Scans.** Republish the dev agent. Trigger scans of the dev install, CBA and ACE, and check the counts against S1–S2 (with the S2 configs now read).
- [ ] **Step 2: The Done-when.**
  - **Lists:** two mod lists, one with CBA+ACE and one with CBA only. helitest (needs `ace_realisticnames`) is listed for the first and "Doesn't match" for the second, naming ACE.
  - **Start Mission as a Zeus** (or Mission Maker if no Zeus test account exists, ledgered): the server boots helitest, shown by the RPT and A2S. Then Start Mission again on the running server restarts it.
- [ ] **Step 3: Docs.** Screenshots of the tab, the confirm and the mission page at 1440 and 375 px. A README section. The deploy guide: what a scan is, Rescan, and Start Mission for Zeus.
