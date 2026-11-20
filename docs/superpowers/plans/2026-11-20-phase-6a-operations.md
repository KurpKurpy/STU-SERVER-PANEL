# Phase 6a — Operations, Readiness and Apply Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the unit plan operations (when, which mission and version, which server profile and mod list, who's Zeus, notes and briefing), see exactly what isn't ready yet, and, once everything passes, apply the operation to its server in one confirmed action.

**Architecture:**
- **Operations** are rows with a stale check (a `Version` number), listed by a UTC range the browser asks for.
- **Readiness** is one evaluator over the database plus live agent state (key listing, server state). It returns pass, fail, "can't tell" or "Apply does this" per checklist item. Prepare runs it and stores the result with its time, so lists show "ready 9/11 · checked 2 h ago", never a count nobody checked.
- **Apply** is Start Mission's core, shared: the same per-server lock, stop-first, pre-checks, stale-player confirm and recovery. It's gated by a fresh evaluation.

**Tech Stack:** as Phase 4b: .NET 10 (10.0.15), EF Core Sqlite, SignalR client results, React 19 + TanStack Query 5 + Tailwind v4, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md`. The parts that matter:
- §5.4 "Prepare Operation";
- §7 (operations permissions);
- §8 `Operation`, `OperationZeus`, `ChecklistItem`, `OperationCheck`;
- §9 (Segments readiness bar, the dashboard's Upcoming Operations);
- §11 Phase 6.

Source spec: §5 (Zeus, Member), §6 "Upcoming operations", §9 (planner, checklist, Prepare), §27.

**Split:** Phase 6 is two plans. **6b** (later) is notifications: in-app, and Discord webhooks. This plan sends none.

## Global Constraints

- **.NET:** run it as `"C:\Program Files\dotnet\dotnet.exe"`; packages pinned at 10.0.15. Panel tests run with `--artifacts-path` (the preview holds `bin\Debug`); Agent tests run with the default output.
- **Git:** there's no git repository and the user handles git. Commit nothing; the ledger replaces commit steps.
- **Disks:** `K:\` is read-only and nothing is written there. The dev data root is `D:\13th_server_dev`. Start agents through `dotnet ConsoleAgent.dll` with `RESHADE_BASE_PATH_OVERRIDE`.
- **Times:** stored and returned in UTC (AppDb's converters add the `Z`); shown in the viewer's local time by the browser.
- **Permissions and audit:** every endpoint needs a permission, and every change is audited. Audit lines name operations, never their notes or briefing.
- **User data:** the user's missions and mods never go into fixtures.
- **Fake agent:** stays as strict as the real one (Phase 5b lesson).
- **Phone width:** every new view and dialog is checked at 375 px in the browser (it failed in both 5b and 4b).
- **Pre-deploy security checklist** (the user's 20 points) runs at the end.

## Review Focus

1. **A readiness claim the panel didn't check.**
   - Cases: an offline agent, an empty registry after a panel restart, Workshop updates never checked, a deleted reference.
   - Expected: "can't tell" with the reason, never a pass; counts show when they were checked.
2. **Apply changing a live server it shouldn't.**
   - Cases: failing checks, unticked manual items, a stale player count, two Applies at once, an Apply racing a Start Mission on the same server.
   - Expected: refused before anything is saved; one at a time per server.
3. **Deleting something an operation uses.**
   - Expected: a profile or mod list in use by a planned operation can't be deleted (the error names it); done or cancelled operations keep their history.
4. **Times across a DST change or an offset.**
   - Expected: an operation posted with an offset is stored at the right UTC instant and listed in the right month.
5. **Who sees notes.** Expected: members see the briefing, never the mission notes, and audit lines carry neither.

## Plan Rulings

1. **Scope.**
   - **Builds:**
     - operations: create, edit, cancel, mark done;
     - a list (upcoming and past) and a month calendar;
     - Zeus and mission-maker assignment;
     - the configurable checklist and the readiness evaluator;
     - Prepare and Apply;
     - the dashboard's Upcoming Operations panel.
   - **Not built:** notifications and Discord (6b), and scheduled actions (Phase 7).
2. **Storage** (design §8, with these choices):
   - **`Operation`:** `Id`, `Name` (≤120), `Description` (≤2,000), `StartsAt` (UTC), `MissionId?`, `MissionVersionId?`, `ProfileId?`, `ModCollectionId?`, `MissionMakerId?`, `Notes` (≤10,000), `Briefing` (≤20,000), `Status` (`Planned`/`Done`/`Cancelled`), `Version` (int, the stale check), `AppliedAt?`, `AppliedById?`, `LastCheckJson?`, `LastCheckAt?`, `ReadyCount`, `CheckCount`, `CreatedById`, `CreatedAt`, `UpdatedAt`.
   - **`OperationZeus(OperationId, UserId)`**.
   - **`ChecklistItem`:** `Id`, `Label` (≤120), `AutoKey?`, `Order`, `Enabled`.
   - **`OperationCheck`:** `OperationId`, `ChecklistItemId`, `Done`, `ById`, `At`. Manual items only.
   - **Map:** the map comes from the mission (`Mission.Map`); it isn't stored twice.
   - **The design's `BriefingMarkdown`** becomes `Briefing`, plain text (Ruling 7).
3. **Deleted and changed references.**
   - **Profiles and mod lists:** a profile or mod list used by a `Planned` operation can't be deleted (`409 in_use`, naming the operations). The existing mod-list "used by" check and the profile delete gain this.
   - **History:** done or cancelled operations keep their row, and the reference is set null on delete (FK `SetNull`).
   - **Missions** are only ever archived: an archived mission fails "Mission uploaded".
   - **Disabled users** don't count toward "Zeus assigned".
4. **Permissions** (design §7), seeded:
   - `operations.view`: every role.
   - `operations.manage`: create and edit. Server Administrator, Mission Maker, Zeus.
   - `operations.prepare`: run Prepare, tick manual items. Server Administrator, Mission Maker, Zeus.
   - `operations.apply`: Server Administrator, Zeus.
   - **Deliberate:** manage plus apply lets a Zeus put any listed mission on any server, which `missions.start` already allows.
   - **Checklist items** are unit settings: `settings.manage`.
5. **Notes and briefing.** `Briefing` is for everyone who can view operations. `Notes` (mission notes) only go to holders of `operations.manage` or `operations.prepare`; for others the API leaves the field out.
6. **Stale edits.** `PUT` carries the `Version` it edited, and a mismatch is `409 stale`. Each save bumps it, and so does Apply.
7. **The briefing is plain text.** It's shown with its line breaks kept, and not rendered as Markdown: no new dependency, no HTML from users. A later phase can add Markdown with a sanitizer.
8. **The checklist** (spec §9, with Amendment A). Eleven items are seeded once, using the `seededChecklist` setting the way permissions are seeded, so an admin's renames, reorders and disables stay.

   | Order | AutoKey | Label | Pass | Fail | Can't tell |
   |---|---|---|---|---|---|
   | 1 | `mission.uploaded` | Mission uploaded | mission set, not archived, version set | otherwise | — |
   | 2 | `mission.matches` | Mission matches the mod list | compatibility Ready on the profile's install | Missing (names the addons) | Unknown, or no profile or list |
   | 3 | `mission.deployed` | Mission version on the server | the pinned version is the Done deployment on the profile's install | another version is there (says which) or none | no profile |
   | 4 | `profile.selected` | Server profile selected | profile set and present | none | — |
   | 5 | `mods.installed` | Mod list assigned and installed | list set, every enabled item installed | not installed (names them) or no list | — |
   | 6 | `mods.updated` | Workshop updates done | no list mod has a newer Workshop time and no mod job is queued or running | updates pending (names them) | a mod never update-checked ("as of the last check") |
   | 7 | `keys.deployed` | Mod keys on the server | every list mod's key file is in the install's `keys` | missing: **Apply does this** | agent offline |
   | 8 | — (manual) | Mission configuration checked | ticked | unticked | — |
   | 9 | `rotation.set` | Mission rotation and mod list on the server | the applied revision's rotation is just this mission and its list is the op's | **Apply does this** | no profile |
   | 10 | `server.ready` | Server ready | agent connected, no SteamCMD job on the install, state Offline or Online | Error, Starting, Stopping, or an install job | agent offline or state unknown |
   | 11 | `zeus.assigned` | Zeus assigned | at least one enabled user | none | — |

   - **Unknown keys:** an `AutoKey` the evaluator doesn't know reads "can't tell".
   - **Admins** can rename, reorder, disable and add manual items. Auto items can't be deleted, only disabled.
9. **Readiness numbers.**
   - `ReadyCount` / `CheckCount` count enabled items, where "Apply does this" counts as not ready.
   - They are stored only by Prepare, with `LastCheckAt`; the list and dashboard show them with that time ("not checked yet" before the first).
   - Editing the operation clears the stored result, since it no longer describes it.
10. **Prepare** (`POST /api/operations/{id}/prepare`, `operations.prepare`): runs the evaluator, stores the result, and returns each item as `{ label, state: pass|fail|unknown|apply, detail, fix? }`. `fix` is a relative link to the page that fixes it (Mods, Missions, the profile, the server).
11. **Apply** (`GET` preview / `POST /api/operations/{id}/apply`, `operations.apply`):
    - **Fresh evaluation:** it re-runs the evaluator server-side. It refuses (`409 not_ready`, naming the items) unless every enabled item is pass or "Apply does this", and every manual item is ticked.
    - **Then:** it runs Start Mission's shared core with the operation's profile, mission and mod list and the confirmed player count, under the same per-server lock, so an Apply and a Start Mission on one server never interleave.
    - **The version:** the server's Done version must be the pinned one (item 3 passes), so Apply starts exactly that version.
    - **Afterwards:** sets `AppliedAt`/`AppliedById`, bumps `Version`, re-runs Prepare and stores the result, and audits `operation.applied`.
12. **Shared core.**
    - **The new method:** `StartMission.RunAsync(StartRequest, StartDeps) → IResult`, extracted unchanged from `StartLockedAsync`, plus a revision note and an audit verb.
    - **Callers:** both endpoints call it inside the existing `Gates` lock.
    - **Tests:** Start Mission's tests stay green unchanged.
13. **Calendar range.** `GET /api/operations?from&to` takes UTC instants the browser computed for the visible month in its own zone. With no range it returns upcoming (`StartsAt ≥ now − 6 h`, `Planned`) and the 20 most recent past ones.
14. **Dashboard.** An Upcoming Operations panel (`operations.view`) shows the next five planned operations with time, mission and map, Zeus, and stored readiness with its time.

## Interfaces (Task 1 produces, later tasks consume)

```csharp
public enum OperationStatus { Planned, Done, Cancelled }
public sealed record CheckResult(Guid ItemId, string Label, string State, string? Detail, string? Fix); // State: pass | fail | unknown | apply
public static class Readiness { Task<CheckResult[]> EvaluateAsync(AppDb db, AgentRegistry registry, AgentCalls agents, Operation op) }
public sealed record StartRequest(Guid ProfileId, Guid MissionId, Guid ModListId, int? Players, string Note, string AuditVerb, string AuditSummary);
```

---

### Task 1: Storage, permissions, seeding and reference rules

**Files:**
- Create: `src/Panel/Data/OperationEntities.cs`; migration `Phase6aOperations`
- Modify: `AppDb.cs`, `Auth/Permissions.cs`, `Auth/Seeder.cs`, `Features/Mods/CollectionEndpoints.cs` (in-use), `Features/Profiles/ProfileEndpoints.cs` (in-use)
- Test: `tests/Panel.Tests/OperationStorageTests.cs`; the seeded-permission lists

- [ ] **Step 1: Write the failing tests.**
  - `The_checklist_is_seeded_once_and_admin_changes_stay`: rename, disable and reorder, reseed, and the changes are still there; 11 items in Ruling 8's order.
  - `Operations_permissions_are_seeded_per_Ruling_4`.
  - `A_mod_list_or_profile_used_by_a_planned_operation_cant_be_deleted`: 409 `in_use` naming the operation.
  - `A_done_operation_keeps_its_row_when_its_profile_is_deleted`: the reference is null.
- [ ] **Step 2: Run, RED.**
- [ ] **Step 3: Implement.** Entities, `AppDb` sets and keys (FKs per Ruling 3), the migration (`--configuration Release`, the preview holds Debug), permissions and defaults, `Seeder` checklist seeding, and the two in-use checks.
- [ ] **Step 4: Full Panel suite, GREEN.**
- [ ] **Step 5: Ledger.**

### Task 2: Operations API

**Files:** `src/Panel/Features/Operations/OperationEndpoints.cs`; `tests/Panel.Tests/OperationEndpointsTests.cs`

**Interfaces:**
- **Routes:**
  - `GET /api/operations?from&to` (view)
  - `GET /api/operations/{id}` (view)
  - `POST /api/operations` (manage)
  - `PUT /api/operations/{id}` (manage, with `version`)
  - `POST /api/operations/{id}/status` (manage: `Planned`/`Done`/`Cancelled`)
  - `GET /api/operations/people` (manage): active users, id and display name
- **DTO:** names for the mission (with map), version number, profile, list, mission maker and Zeus; `notes` only per Ruling 5; the stored readiness.

- [ ] **Step 1: Failing tests.**
  - `Create_edit_and_list_round_trip`.
  - `An_offset_time_is_stored_at_the_right_UTC_instant_and_listed_in_its_month`: `2027-04-04T02:30:00+11:00` (Sydney's DST end) is stored and returned as `2027-04-03T15:30:00Z`, and appears in a range covering April 3 UTC.
  - `A_stale_edit_is_409`.
  - `Members_see_the_briefing_not_the_notes`.
  - `The_mission_version_defaults_to_the_current_and_must_belong_to_the_mission`.
  - `Editing_clears_the_stored_readiness`.
  - `Changes_are_audited_without_notes_or_briefing`.
  - `Members_cant_create_or_edit` (403).
- [ ] **Step 2: RED. Step 3: Implement. Step 4: Panel suite GREEN. Step 5: Ledger.**

### Task 3: Readiness and Prepare

**Files:** `src/Panel/Features/Operations/Readiness.cs`, `ChecklistEndpoints.cs`; `tests/Panel.Tests/ReadinessTests.cs`

**Interfaces:**
- **Routes:**
  - `POST /api/operations/{id}/prepare` (prepare)
  - `PUT /api/operations/{id}/checks/{itemId}` (prepare: `{ done }`, manual items only)
  - `GET`/`PUT /api/checklist` (settings.manage: rename, order, enable, add manual, delete manual)
- **Evaluator:** uses `Compatibility`, the deployments, `ModLaunch.ResolveAsync`, `agents.ListKeys(installRoot)`, `registry.Status/IsConnected`, and the jobs.

- [ ] **Step 1: Failing tests.** One per Ruling 8 row, covering pass, fail and can't tell where the table has them. Also:
  - `An_offline_agent_makes_keys_and_server_cant_tell_not_pass`;
  - `A_server_with_another_version_says_which`;
  - `Prepare_stores_the_count_and_time`;
  - `Disabled_items_dont_count_and_an_unknown_key_cant_tell`;
  - `Only_manual_items_can_be_ticked`;
  - `Checklist_admin_needs_settings_manage`.
- [ ] **Step 2: RED. Step 3: Implement. Step 4: Panel suite GREEN. Step 5: Ledger.**

### Task 4: Apply

**Files:** `src/Panel/Features/Missions/StartMission.cs` (extract `RunAsync`), `src/Panel/Features/Operations/OperationApply.cs`; `tests/Panel.Tests/OperationApplyTests.cs`

- [ ] **Step 1: Failing tests.**
  - `Apply_refuses_until_every_item_passes_or_is_Apply_does_this_and_manual_items_are_ticked`: nothing is saved or sent.
  - `Apply_on_a_stopped_server_saves_the_rotation_and_list_applies_and_starts`: same agent calls as Start Mission.
  - `Apply_on_a_running_server_stops_applies_and_starts_with_a_fresh_player_count`.
  - `Apply_and_Start_Mission_share_the_per_server_lock`: the second is `busy`.
  - `Apply_records_who_and_when_and_bumps_the_version`.
  - `Apply_needs_operations_apply` (Mission Maker 403).
  - The existing `StartMissionTests` stay unchanged and green.
- [ ] **Step 2: RED. Step 3: Implement (Rulings 11–12). Step 4: Panel suite GREEN. Step 5: Ledger.**

### Task 5: Web

**Files:** `web/src/features/operations/` (`OperationsPage.tsx` list and month calendar, `OperationPage.tsx` details, edit and readiness, `OperationForm.tsx`), the `ChecklistSettings` section in Settings, the dashboard panel, nav and route, `lib/types.ts`.

- [ ] **Step 1: Build.**
  - The list has Upcoming and Past and a month calendar, with ops in day cells; on phones it's a list.
  - The form uses `datetime-local`, converted to UTC in the browser, plus mission, version, profile, list, mission maker, Zeus multi-select, description, notes and briefing.
  - The operation page has the details, briefing (pre-wrap) and notes (when returned), and the readiness Segments bar ("n of N · checked …"). Its checklist rows show state with a word and a fix link, and manual items can be ticked. It has Prepare, and Apply with a confirm built from the Start Mission preview plus the operation's name.
  - The dashboard panel.
  - Settings → Checklist.
- [ ] **Step 2: `npm run build` exit 0.**
- [ ] **Step 3: Ledger.** The browser check is in Task 6.

### Task 6: Docs, review, live run, checklist

- [ ] **Step 1: Docs.** README status Phase 6a, an Operations section, and the checklist table; deploy guide: who plans and applies.
- [ ] **Step 2: Final whole-phase review** (fresh reviewer, Opus), then one fix pass, test-first.
- [ ] **Step 3: Live run.**
  - **Needs the user:** DevOwner signed in in the pane, an OK for the pane, and an OK for a temporary profile on the dev install with `persistent`, `-autoInit` and a join password.
  - **Steps:**
    1. Create an operation for helitest with the CBA+ACE list and the temporary profile; Hawk as Zeus.
    2. Prepare: expect "Apply does this" for keys and rotation, and the manual item unticked.
    3. Tick it, then Apply as Hawk: the server boots helitest (RPT and A2S).
    4. Prepare again: all pass.
    5. Check the calendar, list, operation page, dashboard and Settings → Checklist at 1440 and 375 px.
    6. Clean up the temporary profile and operation.
- [ ] **Step 4: The 20-point checklist, then the report** (`phase6a-report.md` in the notes folder) with every ruling, the deferred minors, the checklist and a commit message.
