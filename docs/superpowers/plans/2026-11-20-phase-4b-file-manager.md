# Phase 4b — File Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let admins, mod managers and mission makers browse, upload, download, edit, move, copy, delete and unzip files in the server's own folders from the browser, each person only in the folders their permissions name, with a backup before anything is overwritten or deleted.

**Architecture:**
- **The agent owns the rules.** It maps a named *space* (plus a registered install for per-install spaces) to a folder under its own roots. It decides what is writable and which file types may be written there, so a compromised panel still can't reach the install's program files or write an `.exe`.
- **Reads are quick calls:** list, search and read are bounded in size and time.
- **Changes report back later:** make folder, move, copy, delete, write and extract are accepted at once, run in the background, and report back through the hub, as deployments, jobs and scans already do. The panel's HTTP request waits for that report, up to 10 minutes.
- **Uploads:** they stream to a panel transfer folder. The agent pulls each one by change id, scoped to that agent and that change, and the file is deleted afterwards.

**Tech Stack:** as Phase 5b: .NET 10 (10.0.15), EF Core Sqlite, SignalR client results, React 19 + TanStack Query 5 + Tailwind v4 + Monaco (already bundled), xUnit. `System.IO.Compression` for `.zip` (in the box).

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md`. The parts that matter:
- §5.3 (paths, file transfer, launch boundary);
- §7 (`files.<area>.read` / `files.<area>.write`);
- §10 (the file calls);
- §11 Phase 4 (the file manager over scoped areas);
- the source spec §16 (`docs/superpowers/specs/2026-11-19-source-spec.md`).

It completes the Phase 4a deferral (Phase 4a Plan Ruling 1).

## Global Constraints

- **.NET:** run it as `"C:\Program Files\dotnet\dotnet.exe"`; packages pinned at 10.0.15. Panel tests run with `--artifacts-path` while the user's preview runs; Agent tests run with the default output.
- **Git:** there's no git repository and the user handles git. Commit nothing; the ledger replaces commit steps.
- **Disks:** `K:\` is read-only (`Agent:ReadOnlyInstallRoots`). The dev data root is `D:\13th_server_dev`. Start agents with `$env:RESHADE_BASE_PATH_OVERRIDE='D:\13th_server_dev\reshade'`, through `dotnet ConsoleAgent.dll` (Phase 5b Task 7).
- **Agent path rule (design §5.3):** the agent never takes a raw path. Every path is (space, install, relative path), resolved under the agent's own roots; rooted paths, `..`, and anything passing through a junction or symlink are refused.
- **Permissions and audit:** every endpoint needs sign-in plus the space's permission, and every change is audited.
- **User data:** the user's files never go into repo fixtures. Tests make their own folders, files and zips.
- **Pre-deploy security checklist** (the user's 20 points) runs at the end, after the final review.

## Review Focus

1. **Escaping a space.** This covers `..`, rooted paths, `C:`, `\\server`, `name:stream`, trailing dots or spaces, `CON`/`NUL`/`COM1`, and a junction met inside a folder during copy, delete or search.
   - Expected: refused, nothing outside the space read or written.
   - Pinned in Task 1, against real temp folders.
2. **A zip entry escaping the destination,** or one that's an `.exe`, or a zip bomb. Expected: refused before anything is written outside, the write stopped at the size cap. Pinned in Task 2.
3. **Seeing passwords.** `configs` holds `server.cfg`, `profiles` holds BattlEye's RCon config, and `backups` holds copies of both.
   - Expected: only roles holding those spaces' read permissions see them; the seeded default is Server Administrator.
   - Downloads are attachments with `nosniff`, so an uploaded `.html` never runs on the panel's origin.
   - Pinned in Task 3.
4. **The file manager fighting another owner of the same files:**
   - an overwritten library PBO in `mpmissions`;
   - a difficulty file in `profiles` rewritten by a running server;
   - a local mod changed without a rescan.
   - Pinned in Tasks 2 and 3 (Rulings 8–10).
5. **A large or slow change holding up the agent.** A 1 GB upload or a big folder copy must not block Stop, and a huge folder or search must not overflow the hub's 1 MiB message. Pinned in Task 2 (Ruling 5).

## Plan Rulings

1. **Scope.**
   - **Builds:** the spaces, list/search/read/download, upload, text editing, make folder, rename/move and copy within a space, delete, and `.zip` extraction, with backups.
   - **Not built:**
     - moving or copying between spaces (download, then upload);
     - downloading a whole folder;
     - backup restore and pruning (Phase 7);
     - archives other than `.zip`;
     - the mission library (it has its own pages).
2. **Spaces.** These are the agent's table; the panel only names them.

   | Space | Folder | Writes | Read / write permission | Seeded to |
   |---|---|---|---|---|
   | `mpmissions` | `<install>\mpmissions` | not executables | `files.mpmissions.read` / `.write` | Server Admin r/w, Mission Maker r/w, Zeus r |
   | `keys` | `<install>\keys` | `.bikey` only | `files.keys.read` / `.write` | Server Admin r/w, Mod Manager r/w |
   | `profiles` | `<data>\profiles` | not executables | `files.profiles.read` / `.write` | Server Admin r/w |
   | `localmods` | `<data>\localmods` | anything | `files.localmods.read` / `.write` | Server Admin r/w, Mod Manager r/w |
   | `configs` | `<data>\configs` | read-only | `files.configs.read` | Server Admin |
   | `workshop` | `<data>\workshop\…\107410` | read-only | `files.workshop.read` | Server Admin, Mod Manager |
   | `backups` | `<data>\backups` | read-only | `files.backups.read` | Server Admin |

   - Per-install spaces take a registered install root (`Areas.FindInstallRoot`). A read-only install (`Agent:ReadOnlyInstallRoots`) refuses every write with `Areas.ReadOnlyMessage`.
   - The install root itself, its `battleye` folder and its DLLs are never a space. BattlEye's config and bans live in `profiles\<slug>\BattlEye\` (checked on the dev install).
   - "Executables" means `.exe .dll .com .bat .cmd .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh .msi .msp .scr .lnk .reg .hta .cpl .sys .pif`. Only `localmods` may hold them: mods carry DLLs, and only admins and mod managers write there.
   - The role editor's text for `files.configs.read`, `files.profiles.read` and `files.backups.read` says "includes server passwords".
3. **Names.** Each path segment must:
   - be 1–200 characters;
   - avoid `< > : " / \ | ? *` and control characters;
   - not end in a dot or space;
   - not be a reserved device name with any extension (`CON PRN AUX NUL COM1–9 LPT1–9`);
   - not be `.13c-tmp`.

   A relative path is at most 240 characters. One pure function, `FileNames.Problem(string relativePath) → string?`, in `ArmaFormats` so the panel can pre-check with the same rule. The agent always re-checks.
4. **Containment.** A new `Areas.ResolveUnder(root, relative)` holds the existing rule: no rooted path, no `..`, the full path inside the root, and no reparse point on the way. It is used for every path, including each entry met during a recursive copy, delete or search and each zip entry. Recursive walks skip reparse points instead of following them; delete and copy refuse a folder containing one.
5. **Quick calls versus changes.**
   - **Quick calls:** `FileList` (at most 5,000 entries, then `Truncated`), `FileSearch` (name contains, case-insensitive, under a folder; at most 500 hits or 5 s, then `Truncated`) and `FileRead` (256 KiB chunks).
   - **Changes:** `FileChange` checks the request and answers at once. The work runs in the background, one change at a time per agent, and is reported through `IAgentHub.FileChanged`.
   - **Why:** a long handler would hold up the agent's other commands. Whether the .NET client serialises handlers isn't tested; the design is safe either way.
6. **Backups.**
   - **Before a delete and before an overwrite,** the old item goes to `backups\files\<space>[-<install-area>]\<yyyyMMdd-HHmmss-fff>\<relative path>`. On the same volume it's moved there. Across volumes it's copied, then deleted; over 2 GB the change is refused ("too large to back up across drives").
   - **No overwrite on move, copy or extract:** onto an existing name they're refused.
   - **Write:** it overwrites only with `Overwrite = true`, and a text save also needs `ExpectSha256` to match the file's current hash (`conflict` otherwise).
   - **No pruning** (Phase 7).
7. **Temp files.**
   - Writes and extractions go through `<space root>\.13c-tmp\<change id>\`, then a same-volume move into place.
   - `.13c-tmp` is hidden from listings and search. It's deleted at agent start for each writable space root.
8. **`mpmissions` and the mission library** (panel side).
   - A top-level file in `mpmissions` whose name matches a deployment's `FileName` for that install is library-managed. Writing it, moving it, copying onto it, and renaming anything to that name are refused: "Change it in the mission library."
   - Deleting it is allowed (backed up), and those deployment rows become `Failed` with "Removed in the file manager", so the Missions tab stops listing it.
9. **`profiles` while a server runs.** The agent refuses writing, moving, deleting or overwriting `*.Arma3Profile` under `profiles\<slug>\` while that slug's server runs: "Stop the server first: it rewrites this file when it exits." Other files there, like BattlEye's bans and config, may change while it runs.
10. **`localmods` and scans.** After a change under `localmods\<folder>\…`, the library mod with that `LocalFolder` becomes `NotScanned` and a rescan is requested (Phase 5b `AddonScans.RequestAsync`). A new folder is picked up by the existing **Refresh**.
11. **Owners left alone.**
    - `configs` is read-only: Apply records "applied" by hash.
    - `keys`: KeyService's cleanup touches only keys of library mods, so a hand-added key stays; a deleted library key comes back at the next Start.
    - `workshop` is read-only: SteamCMD owns it.
12. **Transfers.**
    - **Upload:** `POST /api/files/upload` streams one file (request limit 2 GB, set on the endpoint) to `<DataDir>\transfers\<change id>` with its SHA-256. These files aren't in `BlobStore`, which never deletes.
    - **Agent pull:** the agent pulls `GET /api/agent/transfers/{changeId}`, allowed only while that change is pending for that agent. It checks size and hash.
    - **Clean-up:** the transfer file is deleted when the change finishes or fails, and leftovers at panel start.
    - Caddy has no body limit by default (deploy guide unchanged).
13. **Downloads and text.**
    - **Download:** `GET /api/files/download` streams chunks with `Content-Disposition: attachment` and `application/octet-stream` (nosniff is global).
    - **Open as text:** `GET /api/files/text` returns UTF-8 text when the file is at most 1 MB, valid UTF-8, and has no NUL. It also returns whether it had a BOM, its line ending (`\r\n` if any line has one), and its SHA-256. Otherwise: "Not a text file; download it instead."
    - **Save:** puts the BOM back and keeps the editor's line endings, as a write with `Overwrite` and `ExpectSha256`.
14. **Extract.**
    - `.zip` only, into a new folder (default: the zip's name without `.zip`), refused if that exists.
    - Every entry goes through Rulings 2–4, so names, containment and the space's file types apply.
    - At most 20,000 entries and 4 GB written, counted as bytes are written, not as the zip claims. Over either, the change fails and the partial folder is removed.
15. **Permissions plumbing.**
    - The file endpoints use `RequireAuthorization()` plus a runtime check against `FileSpaces.Read(space)` / `.Write(space)`. An unknown space is `404 not_found`, before any permission lookup leaks which spaces exist.
    - The `/api/me` permission list hides the Files page unless some `files.*.read` is held.
16. **Audit.** Each change writes `file.<op>` (`file.uploaded`, `file.saved`, `file.folderCreated`, `file.moved`, `file.copied`, `file.deleted`, `file.extracted`) with the space, install, paths and backup folder. These aren't public.

## Amended by the final review's fix pass

- **Ruling 5:** listings and search results also stop at a 900,000-byte budget, each item costed as its JSON-escaped name plus 100 bytes, so a reply never reaches the hub's 1 MiB limit (review I-1).
- **Ruling 12:** a cut-off or malformed upload deletes its partial transfer and answers 400 (review I-2).
- **Ruling 14:** the 20,000-entry cap is read from the zip's own end-of-central-directory count (Zip64 included) before the zip is opened (review I-3).
- **Ruling 15:** `GET /api/files/spaces` is 403 without any `files.*.read` and doesn't return install paths (review M-17, graded up).

## Contracts (Task 1 produces, later tasks consume)

```csharp
// Contracts/Files.cs
public sealed record FileSpace(string Name, string? InstallRoot);
public sealed record FileItem(string Name, bool IsFolder, long Size, DateTime ModifiedAt);
public sealed record FileListing(FileItem[] Items, bool Truncated);
public sealed record FileHit(string Path, bool IsFolder, long Size, DateTime ModifiedAt);
public sealed record FileSearchResult(FileHit[] Hits, bool Truncated);
public sealed record FileChangeSpec(Guid Id, FileSpace Space, string Op, string Path, string? Target, long Size, string? Sha256,
    bool Overwrite, string? ExpectSha256); // Op: mkdir | move | copy | delete | write | extract
public sealed record FileChangeResult(Guid Id, bool Ok, string? Error, string? Backup);
// IAgentClient
Task<FileListing> FileList(FileSpace space, string path);
Task<FileSearchResult> FileSearch(FileSpace space, string path, string query);
Task<FileChunk> FileRead(FileSpace space, string path, long offset, int length);
Task<CommandResult> FileChange(FileChangeSpec spec);
// IAgentHub
Task FileChanged(FileChangeResult result);
```

The existing `ListFiles` and `ReadFile` stay unchanged for the Logs page, the import and the rotation picker.

---

### Task 1: Names, containment and the agent's space table

**Files:**
- Create: `src/ArmaFormats/FileNames.cs`, `src/Contracts/Files.cs`, `src/Agent/Files/FileSpaces.cs`
- Modify: `src/Agent/Areas.cs` (extract `ResolveUnder`)
- Test: `tests/ArmaFormats.Tests/FileNamesTests.cs`, `tests/Agent.Tests/FileSpacesTests.cs`

**Interfaces:**
- Produces:
  - `FileNames.Problem(string relativePath) → string?`
  - `Areas.ResolveUnder(string root, string relative) → string` (throws `AreaException`)
  - `FileSpaces.Resolve(FileSpace space, string relative, bool forWrite) → ResolvedPath(string Root, string Full, string BackupKey)`
  - `FileSpaces.CheckWrite(FileSpace, string relative)`, which applies Ruling 2's file types and Ruling 9 (it takes `Supervisor` for running slugs)
  - the Contracts records above

- [ ] **Step 1: Write the failing tests.**
  - `FileNamesTests`:
    - `Reserved_names_are_refused` (`CON`, `nul.txt`, `COM1.pbo`);
    - `Trailing_dot_or_space_is_refused`;
    - `Streams_and_bad_characters_are_refused` (`a:b`, `a|b`, `\t`);
    - `Long_paths_are_refused` (241 characters);
    - `The_temp_folder_name_is_refused`;
    - `Ordinary_names_pass` (`ace_3.21.bikey`, `helitest.VR.pbo`, `BattlEye\bans.txt`).
  - `FileSpacesTests`, using a temp data root and install root with real folders:
    - `Each_space_resolves_under_its_own_folder`;
    - `Dot_dot_and_rooted_paths_are_refused`;
    - `A_junction_inside_a_space_is_refused`, using `mklink /J` through `cmd`, or skipped with a reason if junction creation fails;
    - `An_unregistered_install_is_refused`;
    - `The_install_root_itself_isnt_a_space` (the `battleye` and `dll` folders are unreachable);
    - `Read_only_spaces_refuse_writes` (configs, workshop, backups);
    - `A_read_only_install_refuses_writes`;
    - `Keys_take_only_bikey_files`;
    - `Executables_are_refused_outside_localmods`;
    - `A_running_servers_profile_file_is_refused`.
- [ ] **Step 2: Run them and watch them fail.**
  - ArmaFormats: `dotnet test tests/ArmaFormats.Tests --filter FileNamesTests`. Agent: `dotnet test tests/Agent.Tests --filter FileSpacesTests`.
  - Expected: they fail to compile, because the types are missing.
- [ ] **Step 3: Implement `FileNames`, `ResolveUnder` and `FileSpaces`, and add the Contracts records** (no hub wiring yet).
- [ ] **Step 4: Run both test suites. Expected: PASS.**
- [ ] **Step 5: Ledger.**

### Task 2: Agent file operations

**Files:**
- Create: `src/Agent/Files/FileManager.cs`
- Modify: `src/Agent/PanelConnection.cs`, `src/Agent/Program.cs`, `src/Contracts/IAgentClient.cs`
- Test: `tests/Agent.Tests/FileManagerTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `FileManager.List`, `FileManager.Search`, `FileManager.Read`, `FileManager.Change(FileChangeSpec) → CommandResult`, and `event Action<FileChangeResult> Changed`.
  - `FileManager` takes an `HttpClient` for transfer pulls (the MissionWriter's authenticated client) and a `Func<Guid, string> transferUrl`.
  - At start it deletes `.13c-tmp` in each writable space root.

- [ ] **Step 1: Write the failing tests** (a temp data root and install; the transfer pull is served by a stub `HttpMessageHandler`).

  **Listing and search:**
  - `List_hides_the_temp_folder_and_links_and_truncates_at_5000`;
  - `Search_finds_names_under_a_folder_and_stops_at_500`.

  **Writes:**
  - `Write_pulls_the_transfer_checks_its_hash_and_moves_it_into_place`;
  - `Write_with_a_wrong_hash_or_size_writes_nothing`;
  - `Overwrite_needs_the_flag_and_backs_the_old_file_up`;
  - `A_text_save_with_a_stale_hash_is_a_conflict`.

  **Delete and move:**
  - `Delete_moves_the_item_into_backups_on_the_same_volume`;
  - `Delete_refuses_a_folder_containing_a_junction`;
  - `Move_and_copy_refuse_an_existing_target`;
  - `Copy_copies_a_folder_tree`;
  - `The_space_root_cant_be_moved_or_deleted`.

  **Extract:**
  - `Extract_unzips_into_a_new_folder`;
  - `Extract_refuses_an_entry_that_escapes`;
  - `Extract_refuses_an_executable_outside_localmods`;
  - `Extract_stops_at_the_size_cap_and_removes_the_partial_folder` (the cap is a constructor parameter so the test uses a small one).

  **Reporting:**
  - `Changes_run_one_at_a_time_and_each_is_reported`;
  - `A_refused_change_is_answered_at_once_and_not_reported`.
- [ ] **Step 2: Run them.** `dotnet test tests/Agent.Tests --filter FileManagerTests` → compile failure, then RED.
- [ ] **Step 3: Implement `FileManager`.**
  - Register it in `Program.cs`.
  - Wire `FileList`/`FileSearch`/`FileRead`/`FileChange` in `PanelConnection`, with `Changed` → `Keep(nameof(IAgentHub.FileChanged), …)`.
  - Add the four methods to `IAgentClient` and `FileChanged` to `IAgentHub`.
- [ ] **Step 4: Run all Agent tests (default output). Expected: PASS** (155 + the new ones).
- [ ] **Step 5: Ledger.**

### Task 3: Panel: permissions, transfers and file endpoints

**Files:**
- Create: `src/Panel/Features/Files/FileSpacesMap.cs`, `src/Panel/Features/Files/FileTransfers.cs`, `src/Panel/Features/Files/FileChanges.cs`, `src/Panel/Features/Files/FileEndpoints.cs`
- Modify: `src/Panel/Auth/Permissions.cs` (11 permissions, defaults per Ruling 2), `src/Panel/Hubs/AgentHub.cs` (`FileChanged`), `src/Panel/Program.cs`
- Test: `tests/Panel.Tests/FileEndpointsTests.cs`; `FakeAgent.cs` gains the four calls; `PermissionTests`/`SeedingTests` lists; `SecurityChecklistTests` stays green.

**Interfaces:**
- Consumes: the Contracts records; Phase 5b `AddonScans.RequestAsync`; `AgentCalls`.
- Produces the routes under `/api/files`. All of them take `space`, `install?` (a `ServerInstall` id) and `path`:
  - `GET list`
  - `GET search?q`
  - `GET text`
  - `GET download`
  - `POST upload` (multipart, one file, `overwrite`)
  - `PUT text` (`{ text, bom, sha256 }`)
  - `POST folder`
  - `POST move` (`{ target }`)
  - `POST copy` (`{ target }`)
  - `DELETE`
  - `POST extract` (`{ target? }`)
  - `GET spaces`: the spaces this person can read, with installs and writability.
- `FileChanges.RunAsync(agentId, spec, TimeSpan wait) → FileChangeResult?` (null = still running). It is completed by `AgentHub.FileChanged`, and only for the agent that owns the pending change.

- [ ] **Step 1: Write the failing tests** (`FileEndpointsTests`, with the FakeAgent).

  **Permissions:**
  - `Each_space_needs_its_read_permission` (Zeus reads mpmissions, gets 403 on keys and configs);
  - `Writes_need_the_write_permission` (Zeus gets 403 on upload to mpmissions; Mission Maker gets 202/200);
  - `An_unknown_space_is_404`;
  - `Spaces_lists_only_what_the_person_can_read`;
  - `Seeded_roles_get_the_file_permissions_of_Ruling_2` (in `SeedingTests`).

  **Transfers and downloads:**
  - `Upload_stores_a_transfer_the_agent_can_pull_once_and_it_is_deleted_after`;
  - `Another_agent_cant_pull_a_transfer`;
  - `Download_is_an_attachment_with_octet_stream`.

  **Text:**
  - `Text_refuses_a_binary_or_large_file`;
  - `A_text_save_round_trips_bom_and_line_endings`.

  **Owners:**
  - `A_library_pbo_cant_be_overwritten_or_renamed`;
  - `Deleting_a_library_pbo_marks_its_deployments_removed`;
  - `A_change_under_localmods_marks_the_mod_for_rescan`.

  **Results and audit:**
  - `A_change_that_outlasts_the_wait_answers_202_still_running`;
  - `Every_change_is_audited`.
- [ ] **Step 2: Run them.** `dotnet test tests/Panel.Tests --artifacts-path <scratch> --filter FileEndpointsTests` → RED.
- [ ] **Step 3: Implement.**
  - Add the permissions and defaults.
  - `FileSpacesMap`: names, permissions, and whether a space is per-install.
  - `FileTransfers`: save, grant, pull endpoint, delete, startup clean-up.
  - `FileChanges`: pending results by id with a timeout.
  - The endpoints, with the Ruling 8 and 10 hooks and audit.
  - Set the upload's `RequestSizeLimit` to 2 GB.
- [ ] **Step 4: Run the full Panel suite. Expected: PASS** (325 + the new ones).
- [ ] **Step 5: Ledger.**

### Task 4: The Files page

**Files:**
- Create: `web/src/features/files/FilesPage.tsx`, `web/src/features/files/TextEditorDialog.tsx`
- Modify: `web/src/App.tsx` (route `/files`, `RequirePermission` on any `files.*.read`), the nav (shown when any `files.*.read` is held), `web/src/lib/types.ts`

**Interfaces:** consumes Task 3's routes.

- [ ] **Step 1: Build the page.**
  - **Picking where you are:** a space picker (and install picker for `mpmissions`/`keys`), a breadcrumb, and a search box.
  - **Table:** name, size, modified.
  - **Row actions:** download, open as text, rename/move, copy, delete, and extract for `.zip`. Delete's confirm names the item and says it will be backed up to `backups\files\…`.
  - **Toolbar:** upload (several files, each with progress, asking before replacing), new folder.
  - **Editor:** the text editor reuses `CodeView`'s Monaco setup. Save is disabled while unchanged, and on a 409 conflict it says the file changed and offers to reload.
  - **Results:** errors from the agent are shown as written. A 202 "still running" says to refresh later.
  - **Phone width:** at 375 px the table scrolls inside its box and the page doesn't; actions sit in a menu per row.
- [ ] **Step 2: `npm run build` → exit 0.**
- [ ] **Step 3: Ledger.** The browser check is in Task 5.

### Task 5: Docs, live run and checklist

**Files:** `README.md` (status Phase 4b, a Files section, the spaces table), `docs/deploy-windows-server.md` (who gets which spaces; the password note).

- [ ] **Step 1: Docs.**
- [ ] **Step 2: Final whole-phase review** (fresh reviewer), with a fix pass for Critical and Important findings.
- [ ] **Step 3: Live run on the dev PC** (needs the user: their DevOwner sign-in in the pane, and OK for the browser pane).
  1. Republish the agent and start it through `dotnet`.
  2. As DevOwner:
     - upload a small text file to `profiles\<slug>\BattlEye\`, edit it, then delete it, and check the backup exists;
     - zip-extract a small test archive into `localmods` and check the mod is marked for rescan;
     - check a write to `mpmissions` on the K: install is refused.
  3. As Hawk (Zeus): `mpmissions` is listed and download works; `configs` and `profiles` are refused.
  4. Check that renaming the deployed `helitest.VR.pbo` is refused.
  5. Browser check at 1440 and 375 px: console and network clean.
  6. Delete the test files afterwards.
- [ ] **Step 4: The 20-point checklist, then the report** (`phase4b-report.md` in the notes folder) with every ruling, the deferred minors, the checklist and a suggested commit message.
