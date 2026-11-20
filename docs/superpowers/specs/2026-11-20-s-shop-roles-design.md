# S-shop roles — design (short)

**Date:** 2026-11-20. **Size:** a small change (one seeding change, one one-time switch, tests and docs).

## Intent

The console's built-in roles should match how the 13th STU actually runs: staff sections (S-shops), not the generic Server Administrator / Mod Manager / Mission Maker / Zeus. The head developer decided this on 2026-11-20, to land straight after Phase 7a.

## Goals

- The built-in roles become **Owner, S1, S2, S3, S4, S6, S7, Member**, each with a one-line description of who it is in the unit.
- Existing databases switch over once, with nobody losing access: members move to their new role, the 2FA rule follows, the old built-in roles go.
- **S1 and S4 look after the server** (decided): start/stop/force-stop, profiles, applying, Arma updates, the file manager and the RCon command box.

## Non-goals and anti-goals

- No new permissions, no change to how permissions are checked, no element (Mythic, Jaeger, …) tags. Element tags are a separate idea.
- **Anti-goal:** someone silently losing access, or the Owner being touched. Per-member grants and denies stay as they are.

## The roles (defaults; admins can still edit them)

| Role | Description | Permissions |
|---|---|---|
| Owner | Head developer | everything (unchanged) |
| S1 | Unit leadership | everything except `agents.manage` (pairing agents stays the Owner's): members, roles, settings, audit, announcements, all server, mod, mission, operation, player, RCon and file permissions |
| S2 | Section leaders | `members.view`, `server.view`, `server.viewJoinInfo`, `missions.view`, `missions.download`, `operations.view`, `operations.prepare` |
| S3 | Mission makers and Zeus | the old Mission Maker and Zeus sets combined: `members.view`, `server.view`, `server.viewJoinInfo`, `logs.view`, `profiles.view`, `mods.view`, `missions.view/download/create/editOwn/start`, `files.mpmissions.read/write`, `operations.view/manage/prepare/apply`, `players.manage` |
| S4 | Development | the old Mod Manager set plus the old Server Administrator's server set: `members.view`, `server.view/viewJoinInfo/control/forceStop/update`, `logs.view`, `profiles.view/manage/apply`, `mods.view/manage/install`, `collections.manage`, `keys.manage`, `missions.view/download/start` (making and editing missions stays with S3 and S1), every `files.*`, `operations.view`, `players.manage`, `server.console` |
| S6 | Sheets and bot infrastructure | `members.view`, `server.view`, `server.viewJoinInfo`, `missions.view`, `missions.download`, `operations.view` (their work is outside the console) |
| S7 | Trainers (FTXs) | `members.view`, `server.view`, `server.viewJoinInfo`, `mods.view`, `missions.view/download/start`, `operations.view/manage/prepare` |
| Member | Everyone | unchanged: `server.view`, `server.viewJoinInfo`, `missions.view`, `missions.download`, `operations.view` |

## Approaches

1. **A one-time switch at startup, in the seeder (chosen).** The seeder already creates built-in roles and tracks what it did in a settings row; the switch runs once (its own settings key), using the same Identity calls the rest of the app uses.
2. **An EF migration in SQL.** Rejected: it would hand-write Identity's role, claim and normalized-name rows, and the permission defaults live in C#, so it would duplicate them.
3. **Keep the old roles as ordinary custom roles** and let an admin tidy up. Rejected: every install would carry four stale roles beside the new ones, and the 2FA rule would keep pointing at "Server Administrator".

## The switch (existing databases)

Runs once at startup, after the new roles exist, when any old built-in role is found:

- **Members move:** Server Administrator → S1, Mod Manager → S4, Mission Maker → S3, Zeus → S3 (someone in both Mission Maker and Zeus gets S3 once). Anyone in Server Administrator also keeps everything they had, since S1 is a superset.
- **2FA rule:** each old role name in the stored list becomes its new one (duplicates dropped), and S4 is added wherever S1 is, since S4 now holds server powers. The default for new installs becomes `Owner,S1,S4`.
- **The old roles are deleted**, with their permission rows. Edits an admin made to an old role's permissions are not carried over (the new roles start from the defaults above); the audit entry lists who moved where so nothing is invisible.
- **One audit entry** (`roles.switched`) names each moved member and their new role.
- A custom role that already happens to be called S1…S7 is reused and becomes built-in rather than duplicated.

## What's left to the builder

- Test updates: about 25 test files use the old role names; they move to the new ones.
- Docs: README's roles section and the deploy guide's role mentions.
- Exact description wording in the role list.
