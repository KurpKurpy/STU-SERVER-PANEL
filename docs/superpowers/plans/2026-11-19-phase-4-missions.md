# Phase 4a — Missions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mission makers upload a mission (PBO, zip or folder). They then edit its files in the browser and save versions, and every saved version goes onto the server. Rollback writes an older version back.

**Architecture:**
- **Panel storage.** The panel keeps every file in a content-addressed blob store. Database manifests give the working copy, file revisions and immutable versions.
- **ArmaFormats.** The shared library gains:
  - a PBO reader and writer;
  - a read-only reader for binarized configs (raP);
  - a text writer for configs;
  - the scan that reads a mission's required addons.
- **Deployment.** Saving a version packs its PBO and checks it, scans its addons, and asks each target agent to deploy it.
  - The agent pulls the PBO by hash over HTTP with its agent key.
  - It writes the PBO into `mpmissions` through a temp file and checks the hash. A file already there is backed up before it's replaced.
  - It never writes into an install root its own settings mark read-only.

**Tech Stack:**
- .NET 10 (packages pinned at 10.0.15), ASP.NET Core minimal APIs, SignalR client results, EF Core Sqlite, xUnit.
- React 19, TanStack Query 5, Tailwind v4.
- Monaco (`monaco-editor` and `@monaco-editor/react`, bundled locally) is new in this phase. The design lists it for Phase 4.

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md`. The parts that matter:
- Amendment A;
- §3 (binarized missions and the mission PBO format);
- §5.4 (Mission upload → version → on the server);
- §7 (permissions);
- §8 Phase 4;
- §11 Phase 4.

Also source spec §7 and §8. Ledger: `.superpowers/sdd/2026-11-19-phase-4-missions/progress.md`. Its pre-plan survey lines are the facts this plan argues from.

## Global Constraints

- **Environment:**
  - Run .NET as `"C:\Program Files\dotnet\dotnet.exe"`. Stop the `panel` preview before `dotnet test`.
  - There is no git repository, and the user handles git: commit nothing. The ledger replaces the commit steps.
- **K: is read-only.** The dev agent's data root is `D:\13th_server_dev`.
  - Start agents and servers with `$env:RESHADE_BASE_PATH_OVERRIDE='D:\13th_server_dev\reshade'`.
  - From this phase, the dev agent's `ReadOnlyInstallRoots` lists the K: install. The agent itself refuses writes there.
- **The user's missions never go into repo fixtures.**
  - Tests build their PBOs and raP files in code.
  - The real corpus is read only, for spike checks.
  - Copies of it go only to `D:\13th_server_dev` for the end-to-end run.
- **Source spec rules:**
  - "Never overwrite an existing published mission without a backup."
  - "Never silently overwrite the only copy of a previously published mission."
  - Both §7 and §8. A stored blob is never overwritten or deleted in this phase.
- **Same rules as before:**
  - Every endpoint requires a permission, and every state change is audited.
  - The panel names files; the agent maps the names to paths under its own roots (design §5.3).
- **Uploads:**
  - They are streamed to a temp file, never buffered whole in memory.
  - The cap is 200 MB per request, set on the endpoint (Kestrel's default is about 30 MB). The largest real mission PBO is 21.8 MB.

## Plan Rulings

1. **Phase 4 is split.**
   - **This plan (4a) builds:**
     - the mission library with search, filters, categories and tags;
     - uploads with validation and progress;
     - PBO read and write;
     - the blob store;
     - the working copy and its editor;
     - file history;
     - versions with compare and restore;
     - writing every version onto the server, and rollback;
     - the required-addon scan;
     - downloads.
   - **Moved to a 4b plan:** the file manager over the server's own folders (source spec §16: `mpmissions`, keys, profiles, BattlEye).
   - **Why:** §11's "Done when" for Phase 4 doesn't need it, and it's the larger security surface.
   - **Also deferred, to Phase 5b:** source spec §8's "check the required map" and "check the required mod collection". Both need the install's and mods' CfgPatches scan, which Phase 5b builds with the Server Missions tab.
   - **Cost if wrong:** admins use remote desktop for raw server folders until 4b. A mission for a map the server lacks deploys, and fails when it's started.
2. **No external PBO tool.**
   - The survey found 0 compressed entries among the 1,774 entries in 100 real PBOs.
   - The panel reads and writes uncompressed PBOs itself. A compressed entry is refused, with "This PBO uses compressed entries; export it from Eden again or upload the mission folder".
   - There's no setting for an external tool (YAGNI).
   - **Cost if wrong:** someone with a compressed PBO uploads the folder instead.
3. **Binarized `mission.sqm` is read by our own raP reader.** That covers 95 of 100 real PBOs.
   - The editor shows the file as text, read-only.
   - **Convert to text** is an explicit action. It writes a text `mission.sqm` into the working copy as a new file revision, so the original stays in history. After that the file is editable, and Arma and Eden read text `mission.sqm`.
   - **This changes design §3 and call 13.1**, which needed Eden or CfgConvert to edit.
   - **Gate:** Convert ships only if Task 1 shows a lossless round trip on all 147 real binarized files and Task 2 boots a converted mission on the D: server. Otherwise the read-only view stays, and the ledger says why.
   - **Cost if wrong:** a converted file Arma reads differently. The gate and the kept original limit that.
4. **Identity and file name.**
   - **Identity:** a mission is `ClassName.Map`, taken from the PBO or folder name (`co10_Escape.Altis`). The map is the text after the last dot.
   - **Uniqueness:** the pair is unique, ignoring case, and gives the server file name `<ClassName>.<Map>.pbo`.
   - **Allowed names:** 1–150 characters. Refused: path separators, `<>:"|?*`, control characters, a leading or trailing dot or space, and reserved Windows names. `%20` and spaces inside are allowed; the survey has `MOSTOVIAN%20INTERVENTION.Tanoa`.
   - **Cost if wrong:** two missions can't share a file name. That is what Arma needs anyway.
5. **An upload is a version.** Amendment A says "uploads go straight onto the server".
   - **New mission:** uploading creates it, fills its working copy and saves version 1, which then goes onto the server.
   - **Existing mission:** uploading replaces its working copy, recording the old files as revisions, and saves the next version.
   - **Editing in the browser** changes only the working copy until **Save version**.
   - **Cost if wrong:** a mission maker can't stage an upload without it going live. They edit in the browser instead.
6. **Validation** (design §5.4, source spec §7):
   - the type is `.pbo`, `.zip`, or files with relative paths (a folder drop);
   - the 200 MB request cap;
   - a zip has at most 5,000 entries and 500 MB uncompressed, with no single file over 200 MB;
   - entry names refused: `..`, an absolute path, a drive, `:`, control characters, an empty segment, and two paths that differ only in case;
   - exactly one `mission.sqm`, at the mission root;
   - the root folder or file is named `Name.Map`;
   - for a new mission, `ClassName.Map` isn't already used.

   Each failure is a field error naming the rule, never a 500.
7. **Storage.**
   - Blobs live at `<DataDir>/blobs/<ab>/<cd>/<sha256>`, written through a temp file and a rename. They are never overwritten or deleted in 4a.
   - There's no garbage collection yet, so every restore target still exists.
   - The database follows design §8 Phase 4. `MissionVersion.PboSha256` and `OriginalUploadSha256` keep the packed and the uploaded PBOs.
   - **Cost if wrong:** disk use grows with history. 4b or Phase 7 can add pruning.
8. **Versions.**
   - **Save version:**
     1. snapshots the working copy (path → sha) in one transaction;
     2. packs the PBO in the uncompressed format, keeping the properties of the last uploaded PBO's header (for example `prefix`) when there was one;
     3. reads the PBO back and compares each entry's hash;
     4. scans the addons.
   - **Restore** makes an older version current and writes its PBO onto the server again. It doesn't create a new version.
   - **Open in working copy** replaces the working copy with that version's files, as revisions.
   - **Cost if wrong:** "current" and "newest" can differ. The UI says which version is current.
9. **Onto the server: which installs.**
   - Every registered install receives every version, unless its agent marks it read-only. This is a new agent setting, `Agent:ReadOnlyInstallRoots`, empty by default.
   - The agent refuses every write into a read-only root: mission deploys, key deploy and cleanup, and SteamCMD server installs. It reports the flag in its hello, so the panel skips that install and says why.
   - The real box needs no extra step. The dev agent lists K:.
   - **Cost if wrong:** a test install still gets every version. It can be marked read-only.
10. **Deployments.**
    - **Rows:** each install gets a `Deployment` row with a status:
      - Pending (sent);
      - Waiting (the file was in use; spike S4 decides whether this exists);
      - Done (hash matched);
      - Failed (with the reason).
    - **CurrentVersionId** moves when the version is saved or restored, which records intent. Each install's row records whether that install has it.
    - **Restarts:** pending and waiting rows are sent again whenever the agent says hello. The agent's write is idempotent (it skips the write when the file already has the hash), so a restart never leaves a deployment stuck.
    - **Cost if wrong:** a deploy can repeat; that's harmless because the hash matches.
11. **The agent pulls the PBO.**
    - `GET /api/agent/blobs/{sha}` uses the agent-key scheme. It serves a blob only when it's the PBO of a version that has a deployment row for one of that agent's installs.
    - SignalR carries only the command and the hash.
    - **Cost if wrong:** none. Large files stay off the hub.
12. **The mission file on the server.**
    1. The PBO is written to `mpmissions\<name>.pbo.<guid>.tmp` and its SHA-256 checked.
    2. An existing `<name>.pbo` is copied to `backups\missions\<install-area>\<yyyyMMdd-HHmmss-fff>\` first.
    3. The temp file then replaces the old one.

    A failed pull or a bad hash leaves the old file untouched, and the temp file is deleted.
13. **Editor.**
    - Monaco is bundled with `loader.config({ monaco })`; there's no CDN.
    - Our own small Monarch tokenizers handle:
      - SQF: `.sqf`, `.fsm`;
      - Arma config: `.sqm`, `.ext`, `.hpp`, `.cpp`, `.cfg`, `.h`, `.inc`.
    - JSON, XML and plain text use Monaco's built-ins.
    - Text files up to 2 MB are editable. Others (images, sounds, `.paa`) can be downloaded or replaced.
    - **Saving:** a save sends the hash the file was opened at; a stale save gets 409, as in Phase 3.
    - **Search:** search inside files runs on the server over the working copy's text files.
14. **Permissions (Phase 4 additions):** `missions.view`, `missions.download`, `missions.create`, `missions.editOwn`, `missions.editAny`.
    - **Grants:**
      - Server Administrator: all five;
      - Mission Maker: view, download, create and editOwn;
      - Zeus, Member and Mod Manager: view and download.
    - **editOwn** lets the author and the listed collaborators edit, checked per mission.
    - **`missions.start` waits for Phase 5's Start Mission.**
    - **Archive** needs edit rights. It hides the mission from the library and never removes files from a server.
    - There's no hard delete in 4a.
15. **Library fields.**
    - **Category:** one of the spec's list (Main Operations, Training, Zeus Operations, Campaigns, Side Operations, Testing). The spec's "Archived Missions" is the archive flag, not a category.
    - **Tags:** free-form, at most 10 per mission, 30 characters each.
    - **Display name:** the editable name, filled at upload from `description.ext`'s `onLoadName` or `briefingName` when it's there, otherwise `ClassName`.
    - **Type:** filled from `class Header { gameType }` when it's there, and editable.
    - **Author:** the uploader.
    - **Collaborators:** members the author or an editAny holder adds.

## Review Focus

1. **Stuck deployments:** an agent or panel restart while a deployment is Pending or Waiting. Expected: it's sent again on hello and ends Done or Failed; nothing stays Pending for good. (T7 `Pending_deployments_are_resent_on_hello`.)
2. **Hostile names:** archive and PBO entry names with `..`, absolute paths, a drive, `:`, control characters, reserved device names and case-only duplicates; and zip bombs. Expected: refused with a field error, never a file written outside the working copy, never a 500. (T1 `Hostile_entry_names_are_refused`, T4 `Hostile_uploads_are_refused`.)
3. **Half-written files:** a PBO half-written into `mpmissions` (pull fails mid-way, hash mismatch, or the agent dies). Expected: the old file is untouched and no temp file stays. (T7 `A_bad_pull_leaves_the_old_file`.)
4. **Still-referenced missions:** archiving a mission still in a profile's rotation, restoring after a newer upload, or opening a version whose files are all in the blob store. Expected: archive never removes server files, restore always finds its blobs, and the rotation keeps working. (T6 `Restore_writes_the_older_pbo_again`, T4 `Archive_keeps_the_server_file`.)
5. **Concurrent edits:** two editors saving the same file, or a version saved while a file is being saved. Expected: the second save gets 409, and a version holds one consistent snapshot. (T5 `A_stale_save_is_refused`, T6 `A_version_is_one_snapshot`.)

---

### Task 1: ArmaFormats — PBO, raP, config text writer, mission addon scan

**Files:**
- Create in `src/ArmaFormats/`: `Pbo.cs`, `Rap.cs`, `MissionScan.cs`.
- Modify: `src/ArmaFormats/ConfigText.cs` (writer for a whole tree).
- Tests in `tests/ArmaFormats.Tests/`: `PboTests.cs`, `RapTests.cs`, `MissionScanTests.cs`, and `RapBuilder.cs` (a test-only raP writer).

**Interfaces (produces):**
- `PboFile(IReadOnlyDictionary<string,string> Properties, IReadOnlyList<PboEntry> Entries)` and `PboEntry(string Path, byte[] Data, uint Timestamp)`.
- `Pbo.Read(Stream) → PboFile`. It throws `PboException`:
  - for a compressed entry (`Cprs` packing method) or a bad trailer SHA1;
  - for a hostile name (Ruling 6) or a duplicate that differs only in case;
  - when there are more than 10,000 entries or an entry is over 200 MB.
- `Pbo.Write(PboFile, Stream)`: the uncompressed format, with a `sreV` product entry and its properties, and the trailer `0x00` + SHA1.
- `Rap.IsRap(ReadOnlySpan<byte>)` and `Rap.Read(byte[]) → IReadOnlyList<ConfigItem>`. The tree uses the existing `CfgClass`, `CfgAssignment` and `ConfigValue` records, so text and binarized configs share one model.
- `ConfigText.Write(IReadOnlyList<ConfigItem>) → string`. It writes text Arma reads back: strings with `""` escaping and numbers that round-trip.
- `MissionScan.Addons(byte[] missionSqm) → MissionAddons(string[] Addons, AddonMeta[] Meta, bool Binarized)`, where `AddonMeta(string ClassName, string? Name, string? Url)`. It takes text or raP and reads `addons[]` and `AddonsMetaData.List.Item*`.

- [ ] **Step 1: Write failing tests.**
  - `Pbo_round_trips_entries_properties_and_trailer`;
  - `Compressed_entries_are_refused_with_the_reason`;
  - `A_bad_trailer_is_refused`;
  - `Hostile_entry_names_are_refused`, covering `..\x`, `C:\x`, `a:b`, `\x`, a control character, `CON`, and `A.sqf` with `a.sqf`;
  - `Rap_reads_classes_values_arrays_extern_delete_and_append`;
  - `Rap_to_text_parses_back_to_the_same_tree`;
  - `Scan_reads_addons_and_metadata_from_text_and_rap`;
  - `Rap_with_trailing_garbage_or_truncation_throws`.
- [ ] **Step 2:** Run `dotnet test tests/ArmaFormats.Tests`. Expected: FAIL (the types don't exist yet).
- [ ] **Step 3: Implement.** The raP layout:
  - signature `00 72 61 50`, then `u32 0`, `u32 8`, and the `u32` enum offset;
  - class bodies: the inherited name as an asciiz string, then a compressed-int entry count, then entries;
  - entry types:
    - 0: class (name, `u32` body offset);
    - 1: value (subtype 0 string, 1 float, 2 int32, 6 int64);
    - 2: array;
    - 3: extern;
    - 4: delete;
    - 5: append array (`u32` flags, then name and array);
  - array elements: 0 string, 1 float, 2 int32, 3 nested array, 4 variable, 6 int64.

  Every read is bounds-checked, nesting is capped at 64 levels like the Phase 3 parser, and the whole input must be consumed.
- [ ] **Step 4:** Run `dotnet test tests/ArmaFormats.Tests`. Expected: PASS.
- [ ] **Step 5: Corpus gate.** A scratch harness, outside the repo, references ArmaFormats and runs over the real corpus (read-only):
  - all 147 binarized `mission.sqm` files (52 Eden folders plus 95 in PBOs) parse with no bytes left over;
  - for each: raP → `ConfigText.Write` → `ConfigDocument.Parse` → the same tree (classes, names, values, array shapes, numbers equal to float precision);
  - all 100 PBOs read, and `Pbo.Write(Pbo.Read(x))` reads back with equal entries;
  - the 13 text `mission.sqm` files parse, with time and maximum depth recorded.

  Expected: all pass. Record the counts, timings and any failure in the ledger. A round-trip failure turns Convert off (Ruling 3).

### Task 2: Spike — real server behaviour

**Files:** none in the repo. Facts go to the ledger as `S1`–`S5`; later tasks consume them.

- [ ] **S1. Does a PBO packed by `Pbo.Write` boot?**
  1. Pick a real PBO whose addons (from `MissionScan`) are all vanilla, CBA_A3 or ACE.
  2. Re-pack it into `D:\13th_server_dev\arma3server\mpmissions`.
  3. Set the dev profile's rotation to it, Apply, then Start.

  Expected: the RPT shows the mission read, and A2S reports it.
- [ ] **S2. Does a converted mission boot?** The same mission, with `mission.sqm` converted to text by `ConfigText.Write`. Expected: it boots the same way. If not, Convert is off (Ruling 3).
- [ ] **S3. Does Arma lock the running mission's PBO?** With the mission running (after the RPT shows it read), try to open the PBO for writing, then try a replace by rename. Do this once with and once without `-loadMissionToMemory`. Record which fail. This decides whether the Waiting status exists (Ruling 10) and how the agent retries.
- [ ] **S4. Starting a mission without players.** Can the dev server start the mission with no player connected (`persistent = 1` or `-autoInit`), so the end-to-end run can see it in the RPT? Record what the allowlist and server.cfg need.
- [ ] **S5. What's in real PBO headers.** The property keys in the 100 real PBO headers (for example `prefix`) and the entry path conventions (backslash separators, case). Record which to keep (Ruling 8).

### Task 3: Panel data, blob store, permissions

**Files:**
- Create: `src/Panel/Storage/BlobStore.cs`.
- Modify: `src/Panel/Data/Entities.cs`, `src/Panel/Data/AppDb.cs`, and `src/Panel/Auth/Permissions.cs`. The seeded grants move by migration.
- Create the migration `Phase4Missions`.
- Tests: `tests/Panel.Tests/BlobStoreTests.cs` and `MissionPermissionTests.cs`; update `SeedingTests` and `PermissionTests`.

**Interfaces (produces):**
- `BlobStore(PanelPaths)` with:
  - `Task<string> PutAsync(Stream)`, which returns the sha256 and is idempotent;
  - `Stream Open(string sha)`;
  - `bool Exists(string sha)`;
  - `long Size(string sha)`.
- Entities per design §8 Phase 4: `Blob`, `Mission`, `MissionCollaborator`, `Tag`, `MissionTag`, `MissionWorkingFile`, `MissionFileRevision`, `MissionVersion`, `MissionVersionFile`, `MissionVersionAddon`, `Deployment`.
  - `Mission` adds `Category` (string), `GameType` and `PboPropertiesJson`.
  - `Deployment` adds `ServerInstallId`, `Status` (Pending, Waiting, Done or Failed) and `Error`. Its `ProfileId` is replaced by the install, because `mpmissions` belongs to an install.
- `Permissions`: the five constants and grants from Ruling 14.

- [ ] Tests first:
  - `Blobs_are_content_addressed_and_never_overwritten`;
  - `Blob_writes_are_atomic`;
  - `Mission_permissions_per_role`;
  - `Seeded_roles_get_mission_permissions`.

  Then RED, then the implementation, then GREEN with `dotnet test tests/Panel.Tests --filter "Blob|MissionPermission|Seeding|Permission"`.

### Task 4: Upload, validation, library

**Files:**
- Create in `src/Panel/Features/Missions/`: `MissionUpload.cs`, `MissionEndpoints.cs` and `MissionLibrary.cs`.
- Modify: `Program.cs`.
- Tests: `tests/Panel.Tests/MissionUploadTests.cs` and `MissionLibraryTests.cs`, with `MissionKit.cs` building PBOs, zips and folders in code.

**Interfaces (produces):**
- `POST /api/missions/upload` (missions.create) and `POST /api/missions/{id}/upload` (edit right).
  - The body is multipart: one `.pbo`, one `.zip`, or many files whose names are relative paths.
  - It's streamed to a temp file, with the 200 MB cap on the endpoint (`RequestSizeLimit`), and validated per Ruling 6.
  - It returns `MissionDto` and the new version number. The version is saved through Task 6's `MissionVersions.SaveAsync`.
- `GET /api/missions?q=&map=&category=&tag=&author=&archived=` (missions.view). Search covers display name, class name and description.
- `GET /api/missions/{id}` gives the detail: metadata, current version, versions count and deployments.
- `PUT /api/missions/{id}` (edit right): display name, description, type, category, tags and collaborators.
- `POST /api/missions/{id}/archive` and `/unarchive` (edit right).
- `MissionAccess.CanEditAsync(user, mission)` covers editOwn (author or collaborator) and editAny.

- [ ] Tests first:
  - `Pbo_upload_creates_the_mission_and_version_1`;
  - `Zip_and_folder_uploads_find_the_mission_root`;
  - `Upload_reads_names_from_the_pbo_or_folder_name`, including `MOSTOVIAN%20INTERVENTION.Tanoa`;
  - `Duplicate_mission_names_are_refused`;
  - `Hostile_uploads_are_refused`: zip-slip, a zip bomb (entry count and size), no `mission.sqm`, two `mission.sqm` files, a compressed PBO, a wrong extension;
  - `Upload_to_an_existing_mission_saves_the_next_version_and_keeps_old_files_as_revisions`;
  - `Library_search_and_filters`;
  - `Edit_own_means_author_or_collaborator`;
  - `Archive_keeps_the_server_file`;
  - `Display_name_and_type_come_from_description_ext`.

  Then RED, implementation and GREEN.

### Task 5: Working copy, editor API, history

**Files:**
- Create: `src/Panel/Features/Missions/WorkingCopyEndpoints.cs`.
- Tests: `tests/Panel.Tests/WorkingCopyTests.cs`.

**Interfaces (produces):** all under `/api/missions/{id}/files`, with view or download rights to read and edit rights to change.
- `GET` returns the tree: path, size, sha, whether it's text, whether it's binarized, and who changed it and when.
- `GET ?path=` returns the content. It's text when the file is text and at most 2 MB; for a raP file it's the converted text with `readOnly: true`; otherwise it's a download.
- `PUT ?path=` takes `{ content, baseSha }`; a stale `baseSha` gets 409.
- `POST` creates a file, a folder, or uploads supporting files (multipart).
- `POST /rename` takes `{ from, to }` and `DELETE ?path=` removes a file. A folder rename or delete applies to every path under it.
- `GET /history?path=` lists the revisions, and `GET /revision/{revId}` returns one revision's content.
- `GET /search?q=&regex=` searches the text files, returning at most 500 hits.
- `POST /convert?path=mission.sqm` runs Ruling 3's conversion, if Task 1 and Task 2 passed the gate.
- `GET /zip` downloads the working copy as a zip (download right).
- **Path rules:** every path is checked with Task 1's name rules. `mission.sqm` can't be deleted or renamed away from the root.

- [ ] Tests first:
  - `Save_creates_a_revision_and_a_stale_save_is_refused` (`A_stale_save_is_refused`);
  - `Create_rename_delete_and_folders`;
  - `Binarized_mission_sqm_is_read_only_text`;
  - `Convert_to_text_keeps_the_original_in_history`;
  - `History_and_revision_content`;
  - `Search_finds_text_and_skips_binary`;
  - `Paths_cannot_escape_or_drop_mission_sqm`;
  - `Readers_cannot_change_files`.

  Then RED, implementation and GREEN.

### Task 6: Versions, compare, restore, downloads

**Files:**
- Create: `src/Panel/Features/Missions/MissionVersions.cs` and `VersionEndpoints.cs`.
- Tests: `tests/Panel.Tests/VersionTests.cs`.

**Interfaces (produces):**
- `MissionVersions.SaveAsync(Mission, string? note, Guid? userId) → MissionVersion`, per Ruling 8:
  1. snapshot the files, in a transaction;
  2. pack;
  3. read back and verify;
  4. scan the addons;
  5. set `CurrentVersionId`;
  6. call Task 7's `MissionDeployments.DeployAsync(version)`.
- `POST /api/missions/{id}/versions {note}` (edit right).
- `GET /versions` (view).
- `GET /versions/compare?a=&b=` returns the added, removed and changed paths with their shas. The web side fetches the two contents for its diff view.
- `POST /versions/{n}/restore` (edit right).
- `POST /versions/{n}/open` (edit right) puts that version's files into the working copy.
- `GET /versions/{n}/pbo` and `GET /versions/{n}/original` (download right).

- [ ] Tests first:
  - `Save_version_packs_a_pbo_that_reads_back_identical`;
  - `A_version_is_one_snapshot`;
  - `Versions_store_required_addons_with_mod_names`;
  - `Compare_lists_changed_paths`;
  - `Restore_writes_the_older_pbo_again`;
  - `Open_in_working_copy_replaces_files_as_revisions`;
  - `Downloads_need_missions_download`;
  - `Header_properties_from_the_upload_are_kept`.

  Then RED, implementation and GREEN.

### Task 7: Onto the server (agent and panel)

**Files:**
- **Contracts:** create `src/Contracts/Missions.cs`. In `src/Contracts/IAgentClient.cs`, add `DeployMission`. In `src/Contracts/Messages.cs`, `InstallInfo` gains `ReadOnly`.
- **Agent:**
  - create `src/Agent/Missions/MissionWriter.cs`;
  - modify `AgentOptions.cs` (`ReadOnlyInstallRoots`), `Areas.cs`, `Mods/KeyService.cs` and `Steam/JobRunner.cs` (refuse read-only roots), `PanelConnection.cs` and `Program.cs`.
- **Panel:** create `src/Panel/Features/Missions/MissionDeployments.cs` and `AgentBlobEndpoints.cs`, and modify `src/Panel/Hubs/AgentHub.cs` (send again on hello).
- **Dev settings:** `src/Agent/appsettings.Development.json` adds `ReadOnlyInstallRoots` listing K:.
- **Tests:** `tests/Agent.Tests/MissionWriterTests.cs`, `tests/Panel.Tests/DeploymentTests.cs`, and the FakeAgent's `OnDeployMission`.

**Interfaces (produces):**
- `MissionDeploySpec(Guid DeploymentId, string InstallRoot, string FileName, string Sha256, long Size)`.
- `DeployResult(Guid DeploymentId, string Status, string? Error, string? BackupFolder)`, where `Status` is Done, Waiting or Failed.
- The agent's write follows Ruling 12. Its HTTP pull uses `PanelUrl` and the agent key.
- After S3: if the file is locked, the agent answers Waiting and retries every 30 s in the background, then reports through a new `IAgentHub.DeploymentFinished(DeployResult)`. Results are kept until they're sent, the 5a pattern.
- `MissionDeployments.DeployAsync(version)` creates a row for each eligible install and sends it.
- `ResendAsync(agentId)` is called from the agent's hello.

- [ ] Tests first.
  - **Agent:**
    - `Write_replaces_after_backup_and_verifies_the_hash`;
    - `A_bad_pull_leaves_the_old_file`, for a hash mismatch, a truncated stream and an HTTP error;
    - `Read_only_install_roots_refuse_every_write`, for missions, keys and SteamCMD;
    - `Same_hash_is_a_no_op`;
    - `Locked_file_waits_then_finishes`, only if S3 found a lock.
  - **Panel:**
    - `Saving_a_version_deploys_to_every_writable_install`;
    - `Read_only_installs_are_skipped_and_say_why`;
    - `Pending_deployments_are_resent_on_hello`;
    - `Agents_can_pull_only_their_deployment_blobs`;
    - `Deployment_results_are_audited`.

  Then RED, implementation and GREEN with `dotnet test 13th_console.slnx`.

### Task 8: Web — library, upload, mission page

**Files:**
- Create in `web/src/features/missions/`: `MissionsPage.tsx`, `UploadDialog.tsx`, `MissionPage.tsx`, `VersionsPanel.tsx`, `DeploymentsPanel.tsx` and `useMissions.ts`.
- Modify: `App.tsx`, `Sidebar` (Missions), and `lib/types.ts`.

**Interfaces (consumes):** Task 4–7 endpoints.
- **Library:** search, filters, category and tag chips, and an archived toggle.
- **Upload dialog:** for a PBO, a zip, or a folder (drag and drop or a picker, using `webkitRelativePath`). It shows real progress through `XMLHttpRequest.upload.onprogress` and the server's field errors.
- **Mission page:**
  - metadata editing;
  - the current version and the required addons, grouped by mod name;
  - the version list with note, author and time;
  - compare, restore, open in working copy, and downloads;
  - each install's deployment status, live through the jobs push or a refetch.

- [ ] `npm run build` exit 0. Browser check against the dev panel:
  - an upload with its progress;
  - the library's filters;
  - restore;
  - no console errors;
  - no sideways scroll at 375 px.

### Task 9: Web — editor

**Files:**
- Create in `web/src/features/missions/`: `EditorPage.tsx`, `FileTree.tsx`, `monaco.ts` (local loader plus the SQF and Arma config Monarch languages), `HistoryPanel.tsx` and `SearchPanel.tsx`.
- Add `monaco-editor` and `@monaco-editor/react` to `web/package.json`.

**What it does:**
- the file tree with create, rename, delete and folder actions, and supporting-file upload;
- tabs, with unsaved-change dots and a `beforeunload` warning;
- save with confirmation, and the 409 conflict message;
- Monaco's own find and replace;
- search across files;
- each file's history, with a diff against the current content (Monaco diff);
- binarized `mission.sqm` read-only, with a "Convert to text" button behind a confirmation (gated by Ruling 3);
- **Save version** with a note.

- [ ] `npm run build` exit 0. Browser check:
  - edit `description.ext`, save, and see the stale-save conflict from a second tab;
  - history diff;
  - search;
  - convert (if gated in);
  - save a version;
  - no CDN requests (network panel);
  - 375 px usable: the tree collapses into a drawer.

### Task 10: End to end on the real server

- [ ] **Step 1: Upload and edit.**
  1. Copy a real all-vanilla/CBA/ACE PBO (picked in S1) from the corpus to the dev PC.
  2. Upload it through the panel.
  3. Edit `description.ext` (`onLoadName`), then save a version.

  Expected:
  - version 2 is on `D:\13th_server_dev\arma3server\mpmissions` with a matching hash and a backup of version 1;
  - the K: install shows "read-only on the agent".
- [ ] **Step 2: Boot.** The dev profile rotation uses the mission. Apply, then Start. Expected: the RPT shows the edited name (S4 decides how it's started without players).
- [ ] **Step 3: Rollback.** Stop the server and restore version 1. Expected: the file's hash equals version 1's PBO, and there's a new backup.
- [ ] **Step 4: Binarized.** A binarized mission's page lists its required addons with mod names, the editor shows `mission.sqm` read-only, and Convert works if it's gated in.
- [ ] **Step 5: K: scan.** Scope: `K:\Steam\steamapps\common\Arma 3`. Expected: 0 files written.
- [ ] **Step 6: Screenshots.** Library, mission page and editor, each at 1440 and 375 px. Add a README section, and add the deployment guide's mission steps.
