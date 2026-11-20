# S-shop roles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The built-in roles become Owner, S1, S2, S3, S4, S6, S7 and Member, and an existing database switches over once at startup without anyone losing access.

**Architecture:** `Permissions.SystemRoleDefaults` gets the S-shop sets (the seeder already creates and seeds whatever it lists). A small `RoleSwitch` step, called by the seeder after the roles exist, moves members out of the four old built-in roles, translates the stored 2FA list, records one audit entry and deletes the old roles. It needs no flag: it only acts while an old built-in role exists.

**Tech Stack:** as Phase 7a: .NET 10, ASP.NET Core Identity (`RoleManager<Role>`, `UserManager<User>`), EF Core Sqlite, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-20-s-shop-roles-design.md` (approved 2026-11-20).

## Global Constraints

- Role names exactly: `Owner`, `S1`, `S2`, `S3`, `S4`, `S6`, `S7`, `Member`. No S5.
- Descriptions exactly: Owner "Head developer"; S1 "Unit leadership"; S2 "Section leaders"; S3 "Mission makers and Zeus"; S4 "Development"; S6 "Sheets and bot infrastructure"; S7 "Trainers (FTXs)"; Member "Everyone in the unit". Set when a built-in role has no description; an admin's own description is never overwritten.
- Permission sets: exactly the spec's table. S1 = every permission in `Permissions.All` except `agents.manage`.
- Moves: Server Administrator → S1, Mod Manager → S4, Mission Maker → S3, Zeus → S3.
- 2FA default for new installs: `Owner,S1,S4`. A stored list is translated (old names → new, S4 added wherever S1 is, duplicates dropped); a stored empty list stays empty.
- Only roles with `IsSystem = true` and an old name are switched; a custom role someone named "Zeus" is left alone.
- No new permission keys, no change to permission checks, no migration file.

## Review Focus

1. **Someone in both Mission Maker and Zeus** (Hawk-style dual members): they end up in S3 once, with no Identity "already in role" failure stopping the switch.
2. **A stored 2FA list in the three shapes:** default-like `Owner,Server Administrator` → `Owner,S1,S4`; custom `Owner,Zeus` → `Owner,S3`; empty `` → stays empty (empty means "no roles", not "use the default").
3. **A second startup after the switch:** nothing changes and no second `roles.switched` audit entry.
4. **A custom role already named S3 with members:** reused (made built-in), its members kept, no duplicate role, the old roles' members join it.
5. **Per-member grants/denies and the Owner:** an Owner who was also in Server Administrator keeps Owner; a `perm.deny` claim on a moved member still applies after the move.

---

### Task 1: S-shop defaults and the one-time switch

**Files:**
- Modify: `src/Panel/Auth/Permissions.cs` (`SystemRoleDefaults`; add `SystemRoleDescriptions`)
- Create: `src/Panel/Auth/RoleSwitch.cs`
- Modify: `src/Panel/Auth/Seeder.cs` (description on create/empty; reuse a same-named custom role as built-in; call `RoleSwitch.RunAsync`)
- Modify: `src/Panel/Settings/AppSettingsService.cs` (default `Owner,S1,S4`)
- Test: `tests/Panel.Tests/SeedingTests.cs` (rewrite for the new roles), create `tests/Panel.Tests/RoleSwitchTests.cs`

**Interfaces:**
- Produces: `Permissions.SystemRoleDefaults` keys `Owner, S1, S2, S3, S4, S6, S7, Member`; `Permissions.SystemRoleDescriptions : IReadOnlyDictionary<string, string>`; `RoleSwitch.Moves : IReadOnlyDictionary<string, string>` (old → new); `RoleSwitch.RunAsync(IServiceProvider services) : Task`; `RoleSwitch.TranslateTwoFactor(string stored) : string`.

- [ ] **Step 1: Write the failing tests**
  - `SeedingTests.System_roles_exist_with_default_permissions`: the system roles are exactly `["Owner","S1","S2","S3","S4","S6","S7","Member"]`, each with its description; S1's permissions equal `Permissions.All` keys minus `agents.manage`; S3 contains `missions.create`, `operations.apply`, `players.manage` and not `server.control`; S4 contains `server.control`, `server.console`, `files.configs.read` and not `missions.editAny`, `members.manage`; Member unchanged.
  - `SeedingTests.Seeded_roles_get_the_file_permissions_of_Ruling_2`: S3 `["files.mpmissions.read","files.mpmissions.write"]`; S1 and S4 all 11; S2, S6, S7, Member none.
  - Keep `Seeding_again_does_not_restore_a_removed_permission` and `Phase2_permissions_are_seeded` with S4 in place of Server Administrator (S4 keeps `server.forceStop` removable the same way).
  - `RoleSwitchTests.Old_built_in_roles_are_switched_once` (Review Focus 1, 3, 5): plant system roles "Server Administrator", "Mod Manager", "Mission Maker", "Zeus"; users A (Server Administrator + Owner), B (Mod Manager), C (Mission Maker + Zeus, with a `perm.deny` of `players.manage`); run `Seeder.SeedAsync` twice. Assert A in Owner and S1, B in S4, C in S3 once and C's effective permissions lack `players.manage`; the four old roles are gone; exactly one `roles.switched` audit entry naming A, B and C.
  - `RoleSwitchTests.Two_factor_list_is_translated` (Review Focus 2): `TranslateTwoFactor("Owner,Server Administrator") == "Owner,S1,S4"`, `("Owner,Zeus") == "Owner,S3"`, `("Mission Maker,Zeus") == "S3"`, `("") == ""`; and end to end, a stored `Owner,Server Administrator` reads back `Owner,S1,S4` after seeding.
  - `RoleSwitchTests.A_custom_role_named_S3_is_reused` (Review Focus 4): delete the seeded S3, create a non-system "S3" with member D, plant a system "Zeus" with member E; seed; exactly one S3, now `IsSystem`, holding D and E.
  - `RoleSwitchTests.A_custom_role_called_Zeus_is_left_alone`: a non-system "Zeus" with a member survives seeding untouched.

- [ ] **Step 2: Run them, expect failures** — `dotnet test tests/Panel.Tests --filter "FullyQualifiedName~SeedingTests|FullyQualifiedName~RoleSwitchTests"`: compile errors (no `RoleSwitch`) or role-name failures.

- [ ] **Step 3: Implement.**
  - `SystemRoleDefaults` to the spec table (S1 built from `All` minus `agents.manage`).
  - `RoleSwitch.RunAsync`: find system roles named in `Moves`; if none, return. For each member of each old role, `AddToRoleAsync(new)` unless already in it. Translate the stored `twoFactorRequiredRoles` setting if present. One `AuditLog.RecordAsync("roles.switched", …, details: [{user, from, to}])`. Then `RoleManager.DeleteAsync` each old role.
  - Seeder: after the roles loop, call `RoleSwitch.RunAsync(services)`; a found role that isn't `IsSystem` becomes `IsSystem = true`; an empty `Description` gets the default.

- [ ] **Step 4: Run them, expect PASS.** Same command.

### Task 2: The rest of the suite on the new names

**Files:**
- Modify: every test file naming an old role (about 25; `grep -rl "Server Administrator\|Mod Manager\|Mission Maker\|\"Zeus\"" tests`), including `TestUsers.ManagerAsync` (S4 + `members.manage`).

**Interfaces:** consumes Task 1's role names.

- [ ] **Step 1: Run the full Panel suite** and list the failures; they're the old names.
- [ ] **Step 2: Rename by meaning, not by text:** Server Administrator → S4 where the test is about running the server, S1 where it's about members, audit or announcements; Mission Maker and Zeus → S3; Mod Manager → S4. Where an assertion relied on an old role *lacking* a permission S3/S4 now hold (for example "a Zeus can't upload a mission"), switch the actor to a role that still lacks it (S2, S7 or Member) so the test keeps its meaning. Ledger each such change as a ruling.
- [ ] **Step 3: Full Panel suite green;** Agent suite untouched (it has no roles).

### Task 3: Docs, live switch, review, report

**Files:** `README.md` (roles section, 2FA default, 7a's `players.manage` note), `docs/deploy-windows-server.md` (role mentions).

- [ ] **Step 1: Docs** to the new names and the S1/S4 split.
- [ ] **Step 2: Live switch on the dev database:** copy the dev database to the scratch folder first, restart the panel, then check the roster (old Zeus/Mission Maker members in S3, and so on), the Roles page, the 2FA setting and the `roles.switched` audit entry; restart once more and confirm nothing changes.
- [ ] **Step 3:** fresh whole-change review, one test-first fix pass, the 20-point checklist, report with the commit message.
