# Phase 3: Server Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** admins manage server profiles in the browser, FASTER-style:
- structured editors for `server.cfg`, `basic.cfg` and the profile's difficulty, each with a raw editor and unknown entries kept;
- launch parameters with a full command preview, difficulty presets, mission rotation and headless clients;
- revisions with compare and reset, profile duplicate and compare;
- **Apply**, which shows a diff of the files on the server, backs them up, then writes them.

The generated files boot the real K: server with the expected settings.

**Architecture:**
- **New `src/ArmaFormats`:** a parser and writer for Arma's config text that keeps every entry it doesn't manage byte-for-byte. Phase 4 reuses it for text `mission.sqm`.
- **Panel:**
  - A catalog of the config keys it understands drives generic forms, validation and generation.
  - A profile's settings are saved as numbered revisions.
  - Apply writes a revision's generated files through the agent. Start launches whatever was last applied, never the unsaved or unapplied state.
- **Agent:** owns where files go. It writes `server.cfg`, `basic.cfg` and the `DifficultyPresets` class of the server's own `.Arma3Profile`. It backs up first and is all-or-nothing. It also launches headless clients beside the server.
- **Not in Phase 3:**
  - mods: `-mod`/`-serverMod`, the profile's Mods tab, headless-client mod collections and copying the client mods to the headless clients (Phase 5);
  - the mission library (Phase 4); rotation names `.pbo` files in the server's `mpmissions`;
  - RCon and scheduled restarts;
  - a Logs tab inside a profile; the Server section's Logs page already shows them.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs and SignalR, EF Core Sqlite, xUnit; React 19, TanStack Query 5, Tailwind v4, Phosphor icons. No new packages on either side.

**Spec:**
- Design: `docs/superpowers/specs/2026-11-19-13th-console-design.md`. Relevant sections:
  - §2 anti-goals;
  - §3 constraints;
  - §5.2 secrets;
  - §5.3 agent boundary;
  - §7 permissions;
  - §8 Phase 3 tables;
  - §9 look and UI rules;
  - §11 Phase 3;
  - §13 call 13.
- Source spec: `docs/superpowers/specs/2026-11-19-source-spec.md`. Relevant sections:
  - §11 Server Profiles;
  - §12 Configuration Editor;
  - §13 Mission Rotation;
  - §14 Headless Clients;
  - §22 profile navigation;
  - §26 Phase 3.
- Previous phase: `docs/superpowers/plans/2026-11-19-phase-2-server-connection.md`, with its ledger at `.superpowers/sdd/2026-11-19-phase-2-server-connection/progress.md`. Read its rulings and spike facts F1–F13; this plan's facts continue from F14.
- **Bohemia sources** (design §3: config keys come from Bohemia's documentation, never from FASTER):
  - local corpus `D:\13th_Github\.claude\reference\biki\pages\`:
    - `Arma 3_ Startup Parameters.wiki`;
    - `Arma 3_ Difficulty Settings.wiki`;
    - `Multiplayer Scripting.wiki`;
  - plus the pages named in **Config catalog** below.

## Global Constraints

**Toolchain and process** (unchanged from Phase 2)
- **.NET:** always run `"C:\Program Files\dotnet\dotnet.exe"`. Pin Microsoft packages to `10.0.15`.
- **Panel dev server:** stop the `panel` preview before `dotnet test`, because it locks `Panel.exe`. Restart it afterwards.
- **No git:** there is no repository; the user handles git. Commit nothing and run no `git init`. Keep the ledger by hand at `.superpowers/sdd/2026-11-19-phase-3-profiles/progress.md`:
  - the first line is `# SDD ledger — plan: <this plan's path>`;
  - one `Task N: complete (tests: <command> → <result>)` line per task;
  - a `Ruling:` line for every deviation.
- **EF migrations:** `"C:\Program Files\dotnet\dotnet.exe" tool run dotnet-ef migrations add <Name> -p src/Panel`. This phase adds one: `Phase3Profiles`.
- **Baseline:** Panel.Tests 107 and Agent.Tests 66 pass before Task 1. Every task ends with both projects green.

**Machines and paths**
- **K:** `K:\Steam\steamapps\common\Arma 3` is read-only. Nothing in Phase 3 may write under K:. Every dev `server.cfg` sets `BattlEye = 0;`.
- **ReShade:** start every dev agent and every spike launch from a shell with `$env:RESHADE_BASE_PATH_OVERRIDE='D:\13th_server_dev\reshade'`. The K: install has ReShade's `dxgi.dll`, which otherwise rewrites `K:\…\ReShade.log` (Phase 2 F12).
- **Dev agent:** published at `D:\13th_server_dev\agent\`. Its data root is `D:\13th_server_dev`. Run it detached with `Start-Process`.
- **Dev ports:** 2302 for the end-to-end profile, 2402 for the spike; tests use random free ports from 40000 upwards.
- **Sign-in:** dev sign-in details are in `13th_console/data/dev-accounts.txt`. Use them on localhost only and never repeat them in chat, reports or the ledger. The same goes for the agent key.

**Security boundary** (design §5.3; Phase 2's rules all stand)
- **The agent picks every path.**
  - `ApplyConfig` carries file contents, never names or paths. The agent maps them to:
    - `configs\<slug>\server.cfg`;
    - `configs\<slug>\basic.cfg`;
    - `profiles\<slug>\Users\<slug>\<slug>.Arma3Profile`.
  - Backups go to `backups\profiles\<slug>\<UTC yyyyMMdd-HHmmss-fff>\`.
- **Headless-client arguments are generated by the agent:** `-client`, `-connect`, `-port`, `-password`, `-profiles` and `-name`. `-connect` must be an IPv4 literal.
- **Custom launch arguments** still pass `Arma3LaunchArgs.Validate` in both the panel and the agent.

**Secrets** (design §5.2)
- `password`, `passwordAdmin` and `serverCommandPassword` are taken out of the config text. They're stored only in Data-Protection-encrypted columns with purpose `"profile.secrets"`, as JSON `{ password, passwordAdmin, serverCommandPassword }`.
- **No API response** returns a secret value, with one exception: `GET /api/servers/{id}/join` returns the applied join password to `server.viewJoinInfo`. Everywhere else a secret is `{ set: bool }`.
- **Writes are write-only:** `null` or absent keeps the value, `""` clears it, and any other text replaces it.
- **Generated text:**
  - shown in the UI, it omits secret lines and adds one comment: `// Passwords are set on the General tab and are not shown here.`
  - in diffs, both sides mask the value as `"********"`; when the values differ, the new side reads `"******** (changed)"`.
- **Never** in audit details, revision `SnapshotJson`, logs, command previews (`-password=********`) or errors.

**Data**
- Ids are GUIDs and timestamps are UTC `DateTime`.
- Generated files are UTF-8 without a BOM, with CRLF line endings.
- **Size caps:** `server.cfg` 65,536 chars, `basic.cfg` 16,384, the difficulty class 16,384, a rotation entry's params 4,096, a revision note 200.

**Permissions (catalog additions; Owner passes everything)**

| Key | Label | Seeded to |
|---|---|---|
| `profiles.view` | See server profiles and their settings (passwords stay hidden) | Server Administrator, Zeus |
| `profiles.apply` | Write profile settings onto the server | Server Administrator |
| `server.viewJoinInfo` | See the server's join password | Server Administrator, Mod Manager, Mission Maker, Zeus, Member |

`profiles.manage` (Phase 2) now means editing. Every profile GET needs `profiles.view`.

**API conventions:** as in Phases 1–2. Errors use `{ code, message, field? }`, and unsafe methods need `X-Console: 1`. Config syntax errors are 400 `config_syntax`, with a message that starts `Line <n>: `. A save made against an outdated revision is 409 `stale`.

**Frontend:** Phase 2's rules stand:
- semantic tokens; stencil titles; Phosphor icons;
- status is always a word on a `StatusBlock`;
- 4.5:1 contrast; 44 px targets on `pointer: coarse`; reduced motion respected;
- no page-level horizontal scroll at 375 px;
- generated text, diffs and tables scroll inside their own box.

Diffs use the plain `DiffView` (Task 10). Monaco waits for Phase 4.

## Rulings made in this plan

These need the user's approval, like Phase 2's. Carry them into the ledger as `Plan: Ruling: …`.

1. **Saved is not applied.** Saving creates a revision and never touches the server.
   - **Apply** (`profiles.apply`) writes one revision's files. The agent backs up, writes, re-reads and hashes. Only after the hashes match does the panel record `AppliedRevisionId`.
   - **Start and Restart** launch the applied revision. The agent no longer writes config files at Start; it refuses when they're missing.
   - *Cost if wrong:* one extra click before the first start of a new profile.
2. **Apply only while the server is stopped** (agent connected, state Offline or Error). The server rewrites its `.Arma3Profile` when it exits, so an Apply during a run would be lost. Admins save during an op and apply afterwards. *Cost if wrong:* no hot-apply for `server.cfg`; "Apply and restart" can come later.
3. **Identity versus configuration.**
   - **Identity:** name, public address, install, executable and port. Edited directly, not revisioned; install, executable and port only while the server is stopped. This closes the Phase 2 deferred minor.
   - **Configuration:** everything that ends up in a file or in the launch arguments. Revisioned and applied.
   - **The slug can't change after creation:** it names the folders, `-name` and the profile file.
4. **Passwords are revisioned, encrypted.** Each revision row carries its own `SecretsProtected`, so Reset restores passwords too. The join info shows the applied revision's join password. *Cost if wrong:* none known.
5. **Unknown entries are kept as source text.** The raw editor's text is parsed:
   - managed keys become settings;
   - everything else (unknown keys, classes, comments, `#` lines) is kept in its original order, byte-for-byte, in `Extra*`;
   - generation writes managed keys first, in catalog order, then the extras;
   - a comment above a managed key therefore moves to the extras block: moved, never dropped;
   - a managed key set twice refuses the save and names both lines, because which one Arma uses is ambiguous;
   - **a value read from text** (raw save, import, extras box, Phase 2 upgrade) that fails its catalog type, range or choices is never refused. Arma may accept it, and several ranges here are form guards, not documented limits. Its line is kept verbatim in the extras, the save returns a warning naming the line, and the field shows "Set in the raw editor";
   - **a value entered in the form** is validated strictly;
   - choices match case-insensitively; SCF's own example writes `"regular"`;
   - a managed class (`Missions`, `AntiFlood`, `AdvancedOptions`, `sockets`, `DifficultyPresets`) that holds anything unknown is kept whole as text, so nothing inside it is lost. Its form fields say "Set in the raw editor".
   - an empty list stays `{}`, distinct from "not set", because SCF gives the two different meanings.
6. **Difficulty: profiles copy a preset's values instead of linking to it.**
   - Editing a library preset never changes a server's config silently (design §2 anti-goal).
   - `Profile.DifficultyPresetId` only records which preset was loaded last.
   - The seeded presets Recruit, Regular and Veteran use Bohemia's `CfgDifficultyPresets` values and are read-only; "Save as preset" makes a custom one.
7. **The panel manages only `class DifficultyPresets` in `<slug>.Arma3Profile`.** Every other line in that file is the server's own state (Phase 3 fact: the server writes `version`, `blood`, `viewDistance`, … itself). The agent merges the class in with `ConfigDocument.ReplaceClass`, so the diff shows only the panel's changes.
8. **`basic.cfg` is optional.** It's written only when the profile has any basic setting or extra text. Otherwise Apply removes the old file (after the backup) and Start omits `-cfg`. This matches Phase 2.
9. **Phase 2 profiles are upgraded at startup, not in the migration** (decryption needs Data Protection).
   - `ProfileUpgrade` parses the old encrypted `server.cfg` and `BasicCfg` into revision 1, "Imported from Phase 2", then clears the old columns.
   - **On failure** (unreadable or unparseable text), the old columns are kept, `NeedsCfg` is set, and the profile page asks for the file again.
   - The old columns are dropped in Phase 4's migration.
10. **The Phase 2 config-text endpoint is replaced.** `GET /api/agents/{id}/cfg-files/{area}/{name}` returned a raw `server.cfg`, passwords included, to `profiles.manage`. `POST /api/profiles/{id}/import` replaces it, parses on the server and returns nothing secret. The list endpoint stays.
11. **One generator.** The command line, live preview and files all come from C#:
    - the panel's generator;
    - `Arma3Command`, moved into Contracts and shared with the agent.

    The UI asks `POST /api/profiles/{id}/preview` (debounced) and never re-implements generation.
12. **A small `backups` area** (`<DataRoot>\backups`) arrives now for Apply's backups. It's not pruned; Phase 7 manages backups.
13. **Headless clients run only beside a running server**, as children of its supervision entry:
    - they're stopped before the server stops;
    - they re-attach with it;
    - they're never auto-restarted.

    "Start with the server" is opt-in per profile (default off), per source spec §14. Their RPTs go to `profiles\<slug>\hc<n>\`, so the server's console tail isn't hijacked.
14. **Some documented keys are edited only in the raw editor:**
    - the nested arrays `allowedVoteCmds[]`, `allowedVotedAdminCmds[]`, `kickTimeout[]` and `disableChannels[]`;
    - `equalModRequired` (SCF: outdated);
    - the plain-number forms of the four timeouts.

    They work and keep exactly what's typed; a form for nested arrays isn't worth its complexity yet. *Cost if wrong:* admins edit those few in text.
15. **The difficulty file's path follows the server, not the wiki.** SAP's example implies `<profiles>\<name>\<name>.Arma3Profile`. The real server wrote `<profiles>\Users\<name>\<name>.Arma3Profile`, so that path is used, and spike F20 confirms the server reads it. *Cost if wrong:* the custom difficulty silently doesn't apply; F20 catches that before any product code.

## Config catalog

**Status: complete.** Every row cites a Bohemia page:
- the local-corpus pages under **Spec**;
- `setSkill` (skill range 0..1);
- three pages the user supplied on 2026-11-19, because both wiki hosts block automated reading: **SCF** ("Arma 3: Server Config File"), **BSCF** ("Arma 3: Basic Server Config File") and **SAP** ("server.armaprofile").

The tables hold every value an executor needs; nobody has to re-read the pages. A key that isn't listed stays unmanaged: it lives in the extras text and still works, but it gets no form field.

`CfgKey(File, Name, Type, Tab, Group, Label, Default?, Min?, Max?, Choices?, Length?, Secret?, Help?)`:
- **File:** `server`, `basic`, `difficulty` or `launch`.
- **Default:** Arma's own default, shown as the field's placeholder. It's never written.
- **Name:** a dotted name (`AntiFlood.cycleTime`) is a member of that top-level class.
- **Types:**
  - `Text` (quoted string);
  - `Integer` (Int64);
  - `Number` (double, written invariant without an exponent: `10000000000`, `0.001`);
  - `Bool01` (written `1`/`0`) and `BoolWord` (written `true`/`false`); both parse `0`, `1`, `true` and `false`, case-insensitive;
  - `TextList` (`name[] = {"a", "b"};`);
  - `NumberList` (`name[] = {1, 2};`, with an exact `Length` when given);
  - `Flag` (launch only).
- **Lists:** a missing list key means "not set" and isn't written. An empty list `[]` is written as `{}`. SCF says these differ for the `allowed*` keys: unset allows everything, `{}` allows nothing. The form warns when a list is empty.

**`launch`** (cited from `Arma 3_ Startup Parameters.wiki`). These are the only arguments `Arma3LaunchArgs` allows; `-noLogs` is added.

| Name | Type | Rule from the page |
|---|---|---|
| `-autoInit` | Flag | The UI warns that it needs `persistent = 1` and breaks mission parameters. |
| `-loadMissionToMemory` | Flag | |
| `-enableHT` | Flag | Ignored when `-cpuCount` is set. |
| `-hugePages` | Flag | |
| `-filePatching` | Flag | Phase 2 allowlist. |
| `-netlog` | Flag | |
| `-noSound` | Flag | |
| `-noLogs` | Flag | **New.** The UI warns that the console and the Logs page stay empty. |
| `-maxMem` | Integer | Min 1024. |
| `-cpuCount` | Integer | Min 2. |
| `-exThreads` | Integer | Choices 0, 1, 3, 5, 7. |
| `-limitFPS` | Integer | 5–1000. |
| `-bandwidthAlg` | Integer | Choice 2. |
| `-malloc` | Text | Safe-value pattern. |
| `-world` | Text | Safe-value pattern, e.g. `empty`. |
| `-ip` | Text | IPv4. |

**`difficulty`** (cited from `Arma 3_ Difficulty Settings.wiki`, *CfgDifficultyPresets*). Every key is an Integer inside `class Options`.

| Key | Choices | Recruit | Regular | Veteran |
|---|---|---|---|---|
| `reducedDamage` | 0–1 | 1 | 0 | 0 |
| `groupIndicators` | 0–2 | 2 | 1 | 0 |
| `friendlyTags` | 0–2 | 2 | 1 | 0 |
| `enemyTags` | 0–2 | 0 | 0 | 0 |
| `detectedMines` | 0–2 | 2 | 1 | 0 |
| `commands` | 0–2 | 2 | 1 | 1 |
| `waypoints` | 0–2 | 2 | 2 | 1 |
| `tacticalPing` | 0–3 | 3 | 3 | 0 |
| `weaponInfo` | 0–2 | 2 | 2 | 1 |
| `stanceIndicator` | 0–2 | 2 | 2 | 1 |
| `staminaBar` | 0–1 | 1 | 1 | 0 |
| `weaponCrosshair` | 0–1 | 1 | 1 | 0 |
| `visionAid` | 0–1 | 1 | 0 | 0 |
| `thirdPersonView` | 0–2 | 1 | 1 | 0 |
| `cameraShake` | 0–1 | 1 | 1 | 1 |
| `scoreTable` | 0–1 | 1 | 1 | 1 |
| `deathMessages` | 0–1 | 1 | 1 | 1 |
| `vonID` | 0–1 | 1 | 1 | 1 |
| `mapContentFriendly` | 0–1 | 1 | 1 | 0 |
| `mapContentEnemy` | 0–1 | 1 | 1 | 0 |
| `mapContentMines` | 0–1 | 1 | 1 | 0 |
| `autoReport` | 0–1 | 1 | 1 | 0 |
| `multipleSaves` | 0–1 | 1 | 1 | 0 |

**More difficulty keys:**
- **`mapContent`** (0–1): unset by default. Help: "Older key (before 1.68); when set it overrides the three map keys." SAP's example uses it.
- **`CustomDifficulty.aiLevelPreset`** (Integer). Choices: 0 Low, 1 Normal, 2 High, 3 Custom. Seeds: Recruit 0, Regular 1, Veteran 2, taken from the Difficulty page's `levelAI` = AILevelLow, AILevelMedium and AILevelHigh.
- **`CustomAILevel.skillAI`** and **`CustomAILevel.precisionAI`** (Number, 0–1, from `setSkill`). Used only when `aiLevelPreset = 3`; unset in the seeds.

**Layout (SAP):**

```
class DifficultyPresets { class CustomDifficulty { class Options { …options… }; aiLevelPreset = n; }; class CustomAILevel { skillAI = x; precisionAI = y; }; };
```

- When a parsed `DifficultyPresets` class contains anything else, the whole class is kept verbatim as `DifficultyOverride`, and the Difficulty form shows "Set in the raw editor".
- SCF: `difficulty = "Custom"` and `forcedDifficulty = "Custom"` take their flags from `CustomDifficulty` in the server's profile.
- **Profile file path:** SAP's example implies `<profiles>\<name>\<name>.Arma3Profile`. The server we ran wrote `<profiles>\Users\<name>\<name>.Arma3Profile` (`D:\13th_server_dev\profiles\main\Users\main\main.Arma3Profile`). The observed path is used, and spike F20 confirms the server reads it.

**`server`** (SCF, unless noted). Rows read: key, type, default, rule.

*General tab:*
- **Server:**
  - `hostname`, Text, machine name;
  - `maxPlayers`, Integer, 64, min 1;
  - `motd`, TextList. Empty items are kept: SCF uses `""` lines as deliberate pauses.
  - `motdInterval`, Number, 5, min 0;
  - `admins`, TextList: Steam UIDs that can `#login` without the password.
- **Passwords:** `password`, `passwordAdmin` and `serverCommandPassword`, Text, Secret. `serverCommandPassword` is case-sensitive.
- **Security:**
  - `BattlEye`, Bool01, 1;
  - `verifySignatures`, Integer, 2, choices 0, 1 and 2. Help: "1 behaves as 2" (SCF).
  - `allowedFilePatching`, Integer, 0, choices 0 none, 1 headless clients, 2 all;
  - `filePatchingExceptions`, TextList;
  - `kickDuplicate`, Bool01, 0;
  - `requiredBuild`, Integer, 0, min 0;
  - `loopback`, Bool01, 0. Help: "LAN only; blocks everyone not on this machine".
  - `upnp`, Bool01, 0. Warning: can delay start-up by 600 s behind a firewall.

*Missions tab, Mission cycle:*
- `autoSelectMission`, BoolWord, false;
- `randomMissionOrder`, BoolWord, false;
- `persistent`, Bool01, 0;
- `missionsToServerRestart` and `missionsToShutdown`, Integer, 0, min 0. Both above 0 is an error, because SCF says they can't be combined.
- `forcedDifficulty`, Text, "". Suggestions: Recruit, Regular, Veteran, Custom. It uses the rotation's difficulty pattern (empty allowed), since mods add presets. SCF: a mission cycle's difficulty overrides it.
- `missionWhitelist`, TextList.

*Server config tab:*
- **Voting:**
  - `voteThreshold`, Number, 0.5, 0–1;
  - `voteMissionPlayers`, Integer, 1, min 0.
- **Network limits:**
  - `MaxPing`, `MaxPacketLoss` and `MaxDesync`, Integer, −1, min −1;
  - `DisconnectTimeout`, Integer, 15, 1–90;
  - `kickClientsOnSlowNetwork`, NumberList, {1,1,1,1}, Length 4, items 0–1, one per limit: 0 logs, 1 kicks;
  - `enablePlayerDiag`, Bool01, 0;
  - `callExtReportLimit`, Integer, 1000, min 0;
  - `steamProtocolMaxDataSize`, Integer, 1024, min 1024. Warning: above the MTU, packets fragment.
  - `armaUnitsTimeout`, Integer, 30, min 0;
  - `idleFPSLimit`, Integer, 30, 5–60.
- **Timeouts:**
  - `votingTimeOut`, NumberList, {60,90};
  - `roleTimeOut`, NumberList, {90,120};
  - `briefingTimeOut`, NumberList, {60,90};
  - `debriefingTimeOut`, NumberList, {45,60};
  - each has Length 2 (ready, not ready), min 0;
  - `lobbyIdleTimeout`, Integer, 0, min 0.
- **In game:**
  - `disableVoN`, Bool01, 0;
  - `vonCodec`, Integer, 1, choices 0 SPEEX and 1 OPUS;
  - `vonCodecQuality`, Integer, 3, 0–30 (SCF: 0–10 is 8 kHz);
  - `drawingInMap`, BoolWord, true;
  - `skipLobby`, BoolWord, false;
  - `allowProfileGlasses`, BoolWord, true;
  - `zeusCompositionScriptLevel`, Integer, 1, 0–2;
  - `forceRotorLibSimulation`, Integer, 0, 0–2;
  - `overrideHazeQuality`, Integer, −1, −1–2.
- **Scripts and files:**
  - `allowedLoadFileExtensions`, `allowedPreprocessFileExtensions`, `allowedHTMLLoadExtensions` and `allowedHTMLLoadURIs`, TextList. The Lists rule above applies.
  - `missionHTTPDownloadBaseURL`, Text, "";
  - `statisticsEnabled`, Bool01, 1.
- **Server events:** `doubleIdDetected`, `onUserConnected`, `onUserDisconnected`, `onHackedData`, `onDifferentData`, `onUnsignedData`, `onUserKicked` and `regularCheck`, Text, "".
- **Logging:**
  - `logFile`, Text, "";
  - `timeStampFormat`, Text, "short", choices none, short, full;
  - `timeStampFormatConsole`, Text, "short", choices none, short, full.
- **Anti-flood:**
  - `AntiFlood.cycleTime`, Number, 0.5, min 0;
  - `AntiFlood.cycleLimit`, Integer, 400, min 0;
  - `AntiFlood.cycleHardLimit`, Integer, 4000, min 0;
  - `AntiFlood.enableKick`, Bool01, 0.
- **Advanced:**
  - `AdvancedOptions.logObjectNotFound`, Bool01, 1;
  - `AdvancedOptions.skipDescriptionParsing`, Bool01, 0;
  - `AdvancedOptions.ignoreMissionLoadErrors`, Bool01, 0;
  - `AdvancedOptions.queueSizeLogG`, Integer, 0, min 0.

*Headless tab:* `headlessClients` and `localClient`, TextList, generated from one address list (Task 12). Also cited by `Multiplayer Scripting.wiki`.

*Left unmanaged on purpose*, so they stay in the extras: `allowedVoteCmds[]`, `allowedVotedAdminCmds[]`, `kickTimeout[]`, `disableChannels[]` (nested arrays); `equalModRequired` (SCF: outdated); and the plain-number forms of the four timeouts.

*Mission cycle layout (SCF):*

```
class Missions { class <Name> { template = "<mission>.<terrain>"; difficulty = "<preset>"; class Params { <param> = <value>; … }; }; };
```

- `template` and `difficulty` are required; `Params` is optional.
- Entry names may use letters, digits and underscores only, with no spaces. Generated names are `Mission_01`, `Mission_02`, and so on.
- `difficulty` accepts Recruit, Regular, Veteran, Custom, or a mod's own preset class. Validate it with `^[A-Za-z0-9_]{1,32}$`.
- The `class` keyword must be lower-case, or Arma fails to parse the file. The generator always writes `class`.
- A campaign entry (`class Apex {};`), or any entry with other members, makes the whole class a `MissionsOverride`.

**`basic`** (BSCF; Performance tab, group Network tuning; nothing is marked obsolete):
- `MaxMsgSend`, Integer, 128, min 1;
- `MaxSizeGuaranteed`, Integer, 512, min 1;
- `MaxSizeNonguaranteed`, Integer, 256, min 1;
- `MinBandwidth`, Integer, 131072 bps, min 0;
- `MaxBandwidth`, Integer, default unknown, min 0;
- `MinErrorToSend`, Number, 0.001, min 0;
- `MinErrorToSendNear`, Number, 0.01, min 0;
- `MaxCustomFileSize`, Integer bytes, default unknown, min 0;
- `sockets.maxPacketSize`, Integer, 1400, min 1. Warning: only when the router or ISP needs smaller packets; guaranteed and non-guaranteed sizes above it can desync.

**Class members** (`AntiFlood.*`, `AdvancedOptions.*`, `sockets.*`, `CustomDifficulty.*`, `CustomAILevel.*`):
- They're generated as `class <Name> { … };` after the top-level keys, and only when at least one member is set.
- A parsed class becomes values only when every member in it is in the catalog. Otherwise the whole class stays in the extras and its fields show "Set in the raw editor".

---

## Review Focus

1. **Secrets on a new surface.** A `profiles.view` holder, a revision, a compare, the Apply diff, a duplicate's response, the command preview (headless `-password`), the audit log and a raw save with a password typed into it must never reveal a password.
   - Tests: Task 5 `No_profile_surface_returns_a_password`, `Password_typed_into_raw_text_becomes_a_secret`; Task 8 `Apply_preview_masks_passwords_on_both_sides`; Task 12 `Headless_command_preview_masks_the_password`.
2. **Losing hand-written config on a round trip.** Unknown keys, classes (including a managed class with one unknown member), comments, `#include` lines, odd spacing, `+=` arrays, an empty list, a duplicated managed key, a value of the wrong type. Import, save unchanged and re-generate: nothing may be lost or change meaning.
   - Tests: Task 2 `Unknown_items_are_written_back_byte_for_byte`; Task 3 `Real_style_server_cfg_round_trips_without_loss`, `Duplicate_managed_key_is_refused_with_both_lines`.
3. **The server's own `.Arma3Profile` lines** being overwritten, or shown in the diff as panel changes.
   - Tests: Task 7 `Apply_merges_only_the_difficulty_class`; Task 8 `Apply_preview_shows_only_the_difficulty_class_changing`.
4. **Apply and Start races:**
   - Apply while running, or with a preview older than the head revision;
   - Start of a never-applied profile;
   - a failure halfway through writing.

   Tests: Task 7 `Apply_is_refused_while_running`, `Apply_is_all_or_nothing`; Task 8 `Apply_with_an_outdated_revision_is_stale`, `Start_needs_an_applied_revision`, `Start_launches_the_applied_revision_not_the_saved_one`.
5. **The existing Phase 2 profile on upgrade:** readable, unreadable (keys changed), and unparseable. Its old encrypted `server.cfg` is never deleted before a successful upgrade.
   - Tests: Task 4 `Phase2_profile_is_upgraded_to_revision_1`, `Unreadable_phase2_cfg_is_kept_and_flagged`, `Unparseable_phase2_cfg_is_kept_and_flagged`.

---

## File map

```
src/ArmaFormats/        ArmaFormats.csproj  ConfigText.cs  ConfigDocument.cs
src/Contracts/          Messages.cs (+records)  IAgentClient.cs (+3 commands)  Arma3Command.cs (new)  Arma3LaunchArgs.cs (+-noLogs)
src/Agent/              ConfigWriter.cs (new)  Supervisor.cs  StateFile.cs  Areas.cs (+backups)  PanelConnection.cs  Program.cs
                        Games/IGameAdapter.cs  Games/Arma3/Arma3Adapter.cs  Files/FileService.cs (+*.pbo listing)
src/Panel/Features/Profiles/  ProfileEndpoints.cs  RevisionEndpoints.cs  ApplyEndpoints.cs  PresetEndpoints.cs  ProfileUpgrade.cs
                        Config/ConfigCatalog.cs  Config/ProfileSettings.cs  Config/ConfigFiles.cs  Config/LineDiff.cs
src/Panel/              Data/Entities.cs  Data/AppDb.cs  Data/Migrations/Phase3Profiles  Auth/Permissions.cs  Program.cs
                        Features/Servers/ServerEndpoints.cs  Features/Agents/AgentEndpoints.cs  Hubs/AgentRegistry.cs
tests/ArmaFormats.Tests/ ArmaFormats.Tests.csproj  ConfigTextTests.cs
tests/Agent.Tests/      ConfigWriterTests.cs  HeadlessTests.cs  (+SupervisorTests, FileServiceTests edits)
tests/FakeArmaServer/   Program.cs (+-client mode)
tests/Panel.Tests/      ConfigFilesTests.cs  ProfileConfigTests.cs  RevisionTests.cs  ApplyTests.cs  UpgradeTests.cs  HeadlessApiTests.cs
web/src/features/profiles/  ProfilesPage.tsx  ProfileLayout.tsx  useProfileDraft.ts  SettingsForm.tsx  ConfigPreview.tsx  SaveBar.tsx
                        GeneralTab.tsx  ServerConfigTab.tsx  PerformanceTab.tsx  DifficultyTab.tsx  PresetsPage.tsx  MissionsTab.tsx
                        StartupTab.tsx  HeadlessTab.tsx  RevisionsTab.tsx  ComparePage.tsx  ApplyDialog.tsx
web/src/components/     DiffView.tsx  SecretField.tsx
web/src/features/server/ ServerPage.tsx (join info, unapplied banner, headless row)  SetupPage.tsx (profile form removed)  ProfileForm.tsx (deleted)
```

---

### Task 1: Spike: profile facts from the real K: server

Later tasks rest on these facts, so they're measured first. Record each in the ledger as `Spike: Fact F<n>: …`. No product code is written.

**Files:** none in the repo. Scratch files go under `D:\13th_server_dev\spike3\`.

- [ ] **Step 1: Prepare.**
  - Create `spike3\configs`, `spike3\profiles\Users\spike` and `spike3\profiles\hc1`.
  - Write `spike3\configs\server.cfg` with:
    - `hostname = "13th spike3"; maxPlayers = 6; password = "spike"; BattlEye = 0; persistent = 1; autoSelectMission = true;`;
    - `forcedDifficulty = "Custom";` in the format the catalog cites;
    - `headlessClients[] = {"127.0.0.1"}; localClient[] = {"127.0.0.1"};`;
    - `fake_unknownKey = 1;`;
    - a `class Missions` with one entry whose template is the Step 2 candidate.
  - Write `spike3\profiles\Users\spike\spike.Arma3Profile` containing only a `class DifficultyPresets` that sets `thirdPersonView = 0` and `mapContentEnemy = 0` and `aiLevelPreset = 1`, in the SAP layout.
  - Record the SHA-256 of both files.
  - Set `$env:RESHADE_BASE_PATH_OVERRIDE`.
- [ ] **Step 2: Pick a mission.**
  - Candidates: the K: `MPMissions` PBOs on vanilla maps: `13FTX.Stratis`, `13th Zeus training.Altis` and `13FTX STABLE.Malden`.
  - Read each one's addon names from its PBO bytes (read-only), as printable strings near `addons`.
  - **F14:** which candidates need only `A3_*` addons. Use the first such one.
- [ ] **Step 3: Boot.** Launch `arma3server_x64.exe -port=2402 -config=… -profiles=…\spike3\profiles -name=spike -autoInit` with K: as the working directory. Wait for A2S on port 2403, then:
  - **F15:** the A2S `game` and `map` fields once the mission auto-initialises, and the seconds it took. Then repeat once without `-autoInit`: SCF says `autoSelectMission` alone starts the mission and waits in role selection. Record whether A2S then shows the mission. If it does, Task 13 can drop `-autoInit`.
  - **F16:** the A2S visibility byte (byte 1 after the 5 header bytes and 4 string fields, before the player counts) with a join password set.
  - **F17:** the RPT lines for:
    - loading the mission;
    - missing addons, if any;
    - the difficulty, if any;
    - `fake_unknownKey`, whether there's a warning or nothing.
- [ ] **Step 4: Headless client.**
  - Launch `arma3server_x64.exe -client -connect=127.0.0.1 -port=2402 -password=spike -profiles=…\spike3\profiles\hc1 -name=hc1`.
  - **F18:** whether the process stays alive for 60 s, what the server RPT logs about it, and what the HC's own RPT (in `hc1\`) logs. Note whether the mission has a headless-client slot; without one, record what the HC does.
- [ ] **Step 5: Graceful stop.** Close the HC, then the server, with `CloseMainWindow()`. Then:
  - **F19:** hash `server.cfg` again. Did the server rewrite it?
  - **F20:** diff `spike.Arma3Profile` against Step 1. Did the server rewrite the file, and did `class DifficultyPresets` survive unchanged?
- [ ] **Step 6: K: untouched.** Run the Phase 2 scan for files under K: written since Step 1, excluding `!Workshop`.
  - **F21:** expected none.
- [ ] **Step 7: Rule on any contradiction.**
  - **F20, class dropped or changed at exit:** Task 7 also merges the applied class into the profile file at every Start. The panel sends it in `LaunchSpec.DifficultyPresets`.
  - **F14, no vanilla candidate:** Task 13 proves the rotation from the RPT's mission-load or missing-addon line, instead of from A2S.
  - **F18, the HC exits without a slot:** Task 13 proves headless clients by process launch, the generated arguments and the RPT connect attempt, and says so.

---

### Task 2: ArmaFormats: config text parser and writer

**Files:**
- Create:
  - `src/ArmaFormats/ArmaFormats.csproj` (net10.0 classlib), `ConfigText.cs`, `ConfigDocument.cs`;
  - `tests/ArmaFormats.Tests/ArmaFormats.Tests.csproj`, `ConfigTextTests.cs`.
- Modify: `13th_console.slnx` (add both).

**Interfaces (Produces):**

```csharp
namespace ArmaFormats;
public abstract record ConfigValue;
public sealed record CfgString(string Value) : ConfigValue;          // "a ""b""" → a "b"
public sealed record CfgNumber(double Value, string Raw) : ConfigValue;
public sealed record CfgWord(string Value) : ConfigValue;             // unquoted token, e.g. true
public sealed record CfgArray(IReadOnlyList<ConfigValue> Items) : ConfigValue;

public abstract record ConfigItem(int Line, int Start, int Length);   // Start/Length: the item's exact source slice
public sealed record CfgAssignment(string Name, bool IsArray, bool Append, ConfigValue Value, int Line, int Start, int Length) : ConfigItem(Line, Start, Length);
public sealed record CfgClass(string Name, string? Base, IReadOnlyList<ConfigItem>? Body, int Line, int Start, int Length) : ConfigItem(Line, Start, Length); // Body null = `class X;`
public sealed record CfgTrivia(int Line, int Start, int Length) : ConfigItem(Line, Start, Length);  // comment, blank run, # line

public sealed class ConfigSyntaxException(int line, string message) : Exception($"Line {line}: {message}") { public int Line { get; } = line; }

public sealed class ConfigDocument
{
    public static ConfigDocument Parse(string text);          // throws ConfigSyntaxException
    public string Text { get; }
    public IReadOnlyList<ConfigItem> Items { get; }            // top level, in source order
    public string SourceOf(ConfigItem item);                   // exact slice, trailing newline included
    public string ReplaceClass(string name, string classText); // whole text with that top-level class replaced (case-insensitive) or appended
}
public static class ConfigText
{
    public static string Format(ConfigValue value);            // CfgString → "…" with "" escapes; arrays → {a, b}
    public static string Assignment(string name, ConfigValue value); // `name = v;` or `name[] = {…};`
    public const string NewLine = "\r\n";
}
```

**Grammar:**
- `name = value;`
- `name[] = {…};` and `name[] += {…};`, with nesting
- `class X : Y { … };` and `class X;`
- `//` and `/* */` comments
- lines starting with `#`, which are kept as trivia
- **Names are case-insensitive.**
- **An unquoted value** runs to the `;`, trimmed: a number when it parses, otherwise a `CfgWord`.

- [ ] **Step 1: Write failing tests** in `ConfigTextTests.cs`:
  - `Parses_assignments_arrays_and_classes`: `hostname = "A ""B"""; maxPlayers = 40; motd[] = {"x", "y"}; class Missions { class M1 { template = "a.Altis"; }; };` → the three assignments and the class, with `CfgString("A \"B\"")`, `CfgNumber(40)`, and a 2-item `CfgArray`.
  - `Unknown_items_are_written_back_byte_for_byte`:
    - text with a `/* block */` comment, `#include "x.hpp"`, tabs, `kickTimeout[] = { {0, -1}, {1, 180} };` and `x[] += {1};`;
    - concatenating `SourceOf` of every item equals the input exactly.
  - `Format_escapes_quotes_and_writes_arrays`.
  - `ReplaceClass_replaces_only_that_class`:
    - in `version=1;\r\nclass DifficultyPresets { a=1; };\r\nblood=1;\r\n`, replacing `DifficultyPresets` keeps the other two lines byte-exact;
    - in text without that class, it appends.
  - `Syntax_errors_name_the_line`: an unclosed `{` on line 3 throws `ConfigSyntaxException` with `Line == 3`; so does a missing `;`.
  - `Both_line_endings_parse`.
- [ ] **Step 2:** Run `dotnet test tests/ArmaFormats.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement a hand-written tokenizer and recursive-descent parser over the original string, recording each item's `Start`/`Length`.
- [ ] **Step 4:** Run `dotnet test tests/ArmaFormats.Tests`. Expected: PASS.

---

### Task 3: Config catalog, settings model and generation

**Config catalog** is the source for every key, type, default and range used here.

**Files:**
- Create: `src/Panel/Features/Profiles/Config/ConfigCatalog.cs`, `ProfileSettings.cs`, `ConfigFiles.cs`, `tests/Panel.Tests/ConfigFilesTests.cs`.
- Modify: `src/Panel/Panel.csproj` (reference ArmaFormats), `src/Contracts/Arma3LaunchArgs.cs` (`-noLogs` in `Flags`).

**Interfaces (Produces):**

```csharp
namespace Panel.Features.Profiles.Config;
public enum CfgType { Text, Integer, Number, Bool01, BoolWord, TextList, NumberList, Flag }
public sealed record CfgKey(string File, string Name, CfgType Type, string Tab, string Group, string Label,
    string? Default = null, double? Min = null, double? Max = null, string[]? Choices = null, int? Length = null,
    bool Secret = false, string? Help = null);
public static class ConfigCatalog { public static IReadOnlyList<CfgKey> All { get; } }   // the Config catalog rows, in generation order

public sealed record HeadlessSettings(bool Enabled = false, int Count = 1, bool WithServer = false, string[]? Addresses = null); // Addresses default ["127.0.0.1"]
public sealed record ProfileSettings(Dictionary<string, JsonElement> Server, Dictionary<string, JsonElement> Basic,
    Dictionary<string, JsonElement> Difficulty, Dictionary<string, JsonElement> Launch, string ExtraArgs,
    HeadlessSettings Headless, string? MissionsOverride);                                   // a missing key = not written (Arma's default)
public sealed record RotationItem(string Template, string Difficulty, string ParamsText);
public sealed record Secrets(string? Password, string? PasswordAdmin, string? ServerCommandPassword);
public sealed record ProfileConfig(ProfileSettings Settings, string ExtraServerCfg, string ExtraBasicCfg, string DifficultyOverride,
    IReadOnlyList<RotationItem> Rotation, Secrets Secrets);
public enum SecretMode { Real, Omit }
public sealed record GeneratedFiles(string ServerCfg, string? BasicCfg, string DifficultyPresets);
public sealed record ConfigError(string Field, string Message);                              // Field e.g. "server.maxPlayers" or "server.cfg"
public sealed record ConfigWarning(int Line, string Message);                               // a file value kept raw (Ruling 5)
public sealed record ParsedFile(Dictionary<string, JsonElement> Values, string Extra, Secrets? Secrets,
    IReadOnlyList<RotationItem>? Rotation, string? MissionsOverride, HeadlessSettings? Headless,
    IReadOnlyList<ConfigWarning> Warnings);

public static class ConfigFiles
{
    public static GeneratedFiles Generate(ProfileConfig config, SecretMode mode);
    public static ParsedFile ParseServerCfg(string text);      // throws ConfigSyntaxException; 400 config_syntax upstream
    public static ParsedFile ParseBasicCfg(string text);
    public static ParsedFile ParseDifficulty(string classText); // the class DifficultyPresets text
    public static IReadOnlyList<ConfigError> Validate(ProfileConfig config);
    public static string[] LaunchArguments(ProfileSettings settings); // catalog order, then ExtraArgs; Validate()s with Arma3LaunchArgs
    public static string MaskSecrets(string text, Secrets? compareWith = null); // line-based; a value that differs from compareWith's reads "******** (changed)"
}
```

**Rules:**
- **Generated `server.cfg`:**
  1. the managed `server` keys in catalog order;
  2. the secrets (when `Real`), or the "Passwords are set…" comment (when `Omit`);
  3. `headlessClients[]` and `localClient[]` when `Headless.Enabled`;
  4. the managed classes (`class AntiFlood`, `class AdvancedOptions`) with their set members;
  5. `class Missions`, from `MissionsOverride` verbatim when set, otherwise from `Rotation` as `Mission_01`, `Mission_02`, …;
  6. `ExtraServerCfg`.
- **`BasicCfg`** is null when `Basic` is empty and `ExtraBasicCfg` is blank.
- **`DifficultyPresets`** is the whole `class DifficultyPresets { … };` text in the SAP layout, generated from `Difficulty`. When `DifficultyOverride` isn't blank, it's that text verbatim instead.
- **Class members and lists** follow the catalog's *Class members* and *Lists* rules.
- **`missionsToServerRestart` and `missionsToShutdown`:** both above 0 is a `ConfigError` on `server.missionsToShutdown`.
- **Parsing `server.cfg`:**
  - catalog keys (case-insensitive) become values;
  - secret keys become `Secrets`;
  - `class Missions` becomes `Rotation` when every entry holds only `template`, `difficulty` and an optional `class Params`, and becomes `MissionsOverride` (its source) otherwise;
  - `headlessClients[]` becomes `Headless` (Enabled, with those Addresses); a `localClient[]` that differs from it is a validation error;
  - everything else is appended to `Extra` through `SourceOf`.
- **Errors:**
  - a managed key twice is a `ConfigError` naming both line numbers;
  - in `Validate` (form values), a wrong type or an out-of-range value is a `ConfigError` with `Field = "<file>.<key>"`;
  - in `Parse*` (text), the same problem keeps the line in `Extra` and adds a `ConfigWarning` (Ruling 5).

- [ ] **Step 1: Write failing tests** in `ConfigFilesTests.cs`:
  - `Real_style_server_cfg_round_trips_without_loss`. Use a fixture written in the test, with invented values, containing:
    - six managed keys;
    - `passwordAdmin`;
    - a comment above `hostname`;
    - an unknown `kickTimeout[]` array;
    - an unknown `class AdvancedOptions { LogObjectNotFound = false; };`;
    - a `class Missions` with two plain entries.

    Then parse and generate with `Real`. Every managed value matches, the secret matches, and the extras contain the comment, `kickTimeout[]` and `AdvancedOptions` byte-exact. A second parse and generate gives the same text.
  - `Missions_with_extra_entries_become_an_override`.
  - `Duplicate_managed_key_is_refused_with_both_lines`.
  - `Form_values_out_of_range_are_field_errors`: `maxPlayers = 0` gives `server.maxPlayers`; `-maxMem = 512` is an error too.
  - `File_values_out_of_range_stay_in_extras_with_a_warning`:
    - `vonCodecQuality = 40;` and `maxPlayers = "x";` both stay byte-exact in `Extra`, each with a warning naming its line;
    - `forcedDifficulty = "regular";` and `verifySignatures = 1;` parse as values.
  - `Motd_keeps_empty_lines`: `motd[] = {"", "", "Welcome"};` round-trips exactly.
  - `Omit_mode_never_contains_a_password` and `Real_mode_writes_them_quoted`.
  - `Basic_cfg_is_null_when_nothing_is_set`.
  - `Bohemia_difficulty_example_parses_into_values`: SAP's example class, with `mapContent` and `aiLevelPreset = 3` and `CustomAILevel`, fills `Difficulty` and leaves `DifficultyOverride` empty.
  - `Difficulty_class_with_unknown_members_becomes_an_override`.
  - `Class_with_unknown_members_stays_in_extras`: `class AntiFlood { cycleTime = 1; fake = 2; };` gives no `AntiFlood.*` values, and the class appears byte-exact in the extras. The same class without `fake` gives values.
  - `Empty_list_is_kept_distinct_from_unset`: `allowedLoadFileExtensions[] = {};` round-trips as `{}`; an absent key isn't written.
  - `Restart_and_shutdown_counts_cannot_combine`.
  - `Numbers_are_written_without_exponents`: `MaxBandwidth = 10000000000` and `MinErrorToSend = 0.001` come out exactly like that.
  - `Missions_class_is_written_lowercase_with_template_and_difficulty`.
  - `Launch_arguments_follow_catalog_order_then_extra_args`, and `Extra_args_must_pass_the_allowlist` (`-profiles=C:\x` fails).
  - `Headless_addresses_generate_both_arrays`.
  - `MaskSecrets_marks_changed_values`.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter ConfigFiles`. Expected: FAIL.
- [ ] **Step 3:** Implement on top of `ConfigDocument` and `ConfigText`.
- [ ] **Step 4:** Run the same filter. Expected: PASS. Then run the whole `dotnet test`. Expected: PASS.

---

### Task 4: Data, permissions and the Phase 2 upgrade

**Files:**
- Create: `src/Panel/Features/Profiles/ProfileUpgrade.cs`, migration `Phase3Profiles`, `tests/Panel.Tests/UpgradeTests.cs`.
- Modify:
  - `Data/Entities.cs`, `Data/AppDb.cs`;
  - `Auth/Permissions.cs`;
  - `Program.cs` (run `ProfileUpgrade` after seeding);
  - `tests/Panel.Tests/SeedingTests.cs` and `PermissionTests.cs`, whose exact permission lists change.

**Interfaces:**
- **`Profile` gains:**
  - `SettingsJson` (text, null = not upgraded);
  - `ExtraServerCfg`, `ExtraBasicCfg`, `DifficultyOverride` (text, default "");
  - `SecretsProtected` (text);
  - `DifficultyPresetId?`, `ModCollectionId?` (no FK until Phase 5);
  - `AppliedRevisionId?`, `AppliedAt?`, `AppliedById?`;
  - `NeedsCfg` (bool).

  `ServerCfgProtected` and `BasicCfg` stay; Ruling 9 covers them.
- **`ProfileRevision`:** `Id`, `ProfileId` (FK, cascade), `Number` (unique with `ProfileId`), `SnapshotJson`, `SecretsProtected`, `Note?` (200), `CreatedById?`, `CreatedAt`.
- **`DifficultyPreset`:** `Id`, `Name` (40, unique), `ValuesJson`, `IsBuiltIn`, `UpdatedAt`. Seeded once with Recruit, Regular and Veteran from the catalog's difficulty table.
- **`RotationEntry`:** `ProfileId` + `Order` (key), `FileName` (128), `MissionId?`, `Difficulty` (16), `ParamsText`. It mirrors the head revision's rotation, for Phase 4 and 5 queries.
- **`ProfileSnapshot`:** `(ProfileSettings Settings, string ExtraServerCfg, string ExtraBasicCfg, string DifficultyOverride, RotationItem[] Rotation, Guid? DifficultyPresetId)`, serialised to `SnapshotJson` and `Profile.SettingsJson`.
- **`ProfileStore`** (scoped, in `ProfileUpgrade.cs` or its own file):
  - `Task<ProfileRevision> SaveRevisionAsync(Profile p, ProfileSnapshot s, Secrets secrets, string? note, Guid? byId)` writes the next `Number`, copies the snapshot to the profile and replaces its `RotationEntry` rows;
  - `(ProfileSnapshot, Secrets) Load(ProfileRevision r)` and `Secrets LoadSecrets(Profile p)`;
  - the protector purpose is `"profile.secrets"`.
- **`ProfileUpgrade.RunAsync(AppDb, IDataProtectionProvider, ILogger)`** runs for each profile with `SettingsJson == null`. Rulings 9 and 3 describe what it does.
- **Permissions:** the three keys in Global Constraints, with their seeds.

- [ ] **Step 1: Write failing tests:**
  - `Phase2_profile_is_upgraded_to_revision_1`:
    - insert a Phase 2-shaped profile (encrypted cfg with `hostname`, `passwordAdmin`, an unknown key; `BasicCfg` with one unknown line; `CustomArgs = "-autoInit -maxMem=8192"`);
    - run the upgrade;
    - expect revision 1 with the note "Imported from Phase 2", the hostname value, the secret in `SecretsProtected`, the unknown key in `ExtraServerCfg`, `-autoInit` and `-maxMem` in `Launch`, and `ServerCfgProtected == ""`.
  - `Unreadable_phase2_cfg_is_kept_and_flagged`: the cfg is protected under a different purpose; the old column is unchanged, `NeedsCfg` is set and there's no revision.
  - `Unparseable_phase2_cfg_is_kept_and_flagged`: the same, for `hostname = "x` with no closing quote.
  - `Upgrade_runs_once`.
  - `Phase3_permissions_are_seeded`: the Global Constraints seeds; a second seed pass doesn't re-add a removed key.
  - `Builtin_difficulty_presets_are_seeded` with Bohemia's values: Veteran `thirdPersonView == 0`, Recruit `reducedDamage == 1`.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter "Upgrade|Seeding"`. Expected: FAIL.
- [ ] **Step 3:** Implement, then add the migration.
- [ ] **Step 4:** Run the whole `dotnet test`. Expected: PASS.

---

### Task 5: Profile API: identity, configuration, raw files, preview, import, duplicate

**Files:**
- Modify:
  - `Features/Profiles/ProfileEndpoints.cs`, which replaces Phase 2's `ProfileUpsert` flow;
  - `Features/Agents/AgentEndpoints.cs` (remove the cfg text endpoint, Ruling 10);
  - `tests/Panel.Tests/ProfileTests.cs`, whose Phase 2 tests move to the new shapes;
  - `Features/Servers/ServerEndpoints.cs`. **Interim until Task 8:** `Control` builds `LaunchSpec.ServerCfg`, `BasicCfg` and `CustomArgs` from the head revision (`ConfigFiles.Generate(…, Real)` and `LaunchArguments`) instead of `ServerCfgProtected`, which new profiles no longer have. Phase 2's `cfg_unreadable` result becomes `secrets_unreadable`.
  - `tests/Panel.Tests/FakeAgent.cs`: `AgentKit.CreateProfileAsync` uses the new `POST` plus `PUT /config`, so the Phase 2 `ServerTests` keep passing.
- Create: `tests/Panel.Tests/ProfileConfigTests.cs`.

**Endpoints** (`profiles.view` for GET, `profiles.manage` otherwise):
- **Reading:**
  - `GET /api/profiles` → summaries plus `revision`, `appliedRevision` and `needsCfg`.
  - `GET /api/profiles/catalog` → `ConfigCatalog.All` and the difficulty presets' names.
  - `GET /api/profiles/{id}` → `ProfileDetail`:
    - identity, `snapshot`;
    - `secrets: { password: { set }, passwordAdmin: { set }, serverCommandPassword: { set } }`;
    - `revision`, `appliedRevision`, `appliedAt`, `needsCfg`.
  - `GET /api/profiles/{id}/files` → `{ serverCfg, basicCfg, difficultyPresets }`, generated with `SecretMode.Omit`.
- **Creating and identity:**
  - `POST /api/profiles { serverInstallId, name, slug, executable, port, publicAddress }` → 201. The config is empty, revision 1 is "Created", and Phase 2's validation and conflicts apply.
  - `PATCH /api/profiles/{id} { name?, publicAddress?, serverInstallId?, executable?, port? }`. Changing install, executable or port needs the agent connected and the server Offline or Error; otherwise it's 409 `running` or `agent_offline`. A slug in the body is 400 `field: slug`, "The slug can't change".
- **Saving configuration:**
  - `PUT /api/profiles/{id}/config { baseRevision, note?, snapshot, secrets: { password?, passwordAdmin?, serverCommandPassword? } }` → a new revision. `baseRevision ≠ head` is 409 `stale`. Validation errors come from `ConfigFiles.Validate`, with `field`.
    - The submitted `extraServerCfg` and `extraBasicCfg` go through the same parser as the raw PUT.
    - Secrets found in them are lifted out (write-only) and never stored in `SnapshotJson`.
    - A managed key found in them is 400 `field: extraServerCfg` (or `extraBasicCfg`), "<key> is set in the form".
    - `MissionsOverride` and `DifficultyOverride` must parse, or it's 400 `config_syntax`.
  - `PUT /api/profiles/{id}/files/{file} { baseRevision, text, note? }`, where `file` is `server.cfg`, `basic.cfg` or `difficulty`. It parses and replaces only that file's part of the snapshot. Secrets present in the text replace the stored ones; absent ones are kept.
  - `POST /api/profiles/{id}/preview { snapshot }` → `{ files: GeneratedFiles (Omit), command: { server, headless[] } }` (Task 8 fills `command`).
  - `POST /api/profiles/{id}/import { baseRevision, file, area, name }` reads through `AgentCalls` `ReadFile`, then works like the raw PUT. It's 400 `too_large` above the caps.
- **Duplicate and delete:**
  - `POST /api/profiles/{id}/duplicate { name, slug }` → 201. It copies the head snapshot and secrets, picks the lowest free 5-port block at or above the source port + 5 on the same agent, and creates revision 1 "Duplicated from <name> revision <n>". The new profile isn't applied.
  - `DELETE` is unchanged.
- **Audit** (details never hold values, only key names):
  - `profile.created`, `profile.updated` (identity);
  - `profile.saved` (`{ revision, note, changed: ["server.hostname", …] }`; a secret appears as `"server.passwordAdmin"`);
  - `profile.duplicated`, `profile.imported`.

- [ ] **Step 1: Write failing tests** in `ProfileConfigTests.cs`:
  - `Config_save_creates_revision_and_head_moves`.
  - `Outdated_base_revision_is_stale`.
  - `Raw_server_cfg_save_keeps_unknown_entries`.
  - `Password_typed_into_raw_text_becomes_a_secret`: a raw PUT containing `passwordAdmin = "hunter2-long";` → `secrets.passwordAdmin.set == true`, and neither `GET /files` nor `GET /{id}` contains `hunter2`.
  - `No_profile_surface_returns_a_password`. Set all three secrets to strings containing `hunter2`, and also save a `PUT /config` whose `extraServerCfg` holds `passwordAdmin = "hunter2-extra";`. Then call every GET in this task, plus `duplicate`'s response and `preview`, as Zeus (`profiles.view`) and as Server Administrator. No body contains `hunter2`, and neither does any `AuditEntry` or `ProfileRevision.SnapshotJson`.
  - `Managed_key_in_the_extras_box_is_refused`.
  - `Secret_write_semantics`: absent keeps, `""` clears, text replaces.
  - `Identity_change_is_refused_while_running`: the FakeAgent status is Online → PATCH port → 409 `running`.
  - `Slug_cannot_change`.
  - `Duplicate_copies_config_and_picks_free_ports`.
  - `Import_parses_on_the_server`: FakeAgent `ReadFile` returns a cfg with `passwordAdmin`; the response has `set: true` and no value.
  - `Zeus_can_view_but_not_save`: GET 200, PUT 403.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter "Profile"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole `dotnet test`. Expected: PASS.

---

### Task 6: Revisions, compare, reset, difficulty presets

**Files:**
- Create:
  - `Features/Profiles/RevisionEndpoints.cs`, `PresetEndpoints.cs`;
  - `Config/LineDiff.cs`;
  - `tests/Panel.Tests/RevisionTests.cs`.

**Interfaces:**
- **`LineDiff`:**
  - `public sealed record DiffLine(char Kind, string Text, int? OldLine, int? NewLine)`, where Kind is `' '`, `'+'`, `'-'` or `'…'` (collapsed context);
  - `public static DiffLine[] LineDiff.Compute(string oldText, string newText, int context = 3)`, an LCS over lines; both inputs are ≤ 65,536 chars.
- **`FileDiff(string Name, bool OldExists, DiffLine[] Lines)`.**
- **Endpoints** (`profiles.view` for GET):
  - `GET /api/profiles/{id}/revisions` → `[{ number, note, createdBy, createdAt, applied }]`.
  - `GET /api/profiles/{id}/revisions/{n}/files` → generated (Omit).
  - `GET /api/profiles/{id}/compare?from=&to=` → `{ files: FileDiff[] }`. Each side is generated with `Real`. The `from` side is masked with `MaskSecrets(text)`, and the `to` side with `MaskSecrets(text, fromSecrets)`, so a password change shows as changed without its value. `to` defaults to the head.
  - `POST /api/profiles/{id}/revisions/{n}/reset { baseRevision }` (`profiles.manage`) → a new revision with revision n's snapshot and secrets, note "Reset to revision n". Audit `profile.reset`.
  - `GET /api/profiles/compare?a=&b=` → `{ identity: [{ field, a, b }], files: FileDiff[] }` for the two heads, masked the same way.
  - `GET /api/difficulty-presets`; `POST`, `PUT /{id}` and `DELETE /{id}` (`profiles.manage`).
    - Built-ins are 409 `built_in` on PUT and DELETE.
    - Names are 1–40 characters and unique.
    - Values are validated against the difficulty catalog.
    - Audit `preset.created`, `preset.updated`, `preset.deleted`.

- [ ] **Step 1: Write failing tests:**
  - `LineDiff_marks_added_and_removed_lines_with_context`.
  - `Revisions_list_marks_the_applied_one`.
  - `Reset_creates_a_new_revision_with_old_values_and_passwords`.
  - `Compare_shows_a_password_change_without_its_value`: r1 `passwordAdmin = "hunter2-a"`, r2 `"hunter2-b"`; the compare contains `******** (changed)` and no `hunter2`.
  - `Compare_profiles_lists_identity_and_file_differences`.
  - `Builtin_preset_cannot_change`.
  - `Editing_a_preset_never_changes_a_profile`: load a preset's values into a profile and save, then edit the preset; the profile's head snapshot is unchanged.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter "Revision"`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole `dotnet test`. Expected: PASS.

---

### Task 7: Agent: apply config, read config, launch without writing

**Files:**
- Create: `src/Contracts/Arma3Command.cs`, `src/Agent/ConfigWriter.cs`, `tests/Agent.Tests/ConfigWriterTests.cs`.
- Modify:
  - `src/Contracts/Messages.cs`, `IAgentClient.cs`;
  - Panel, so it compiles against the new `LaunchSpec`:
    - `src/Panel/Features/Servers/ServerEndpoints.cs`: `Control` sends `UseBasicCfg` (the head revision's generated `BasicCfg != null`) and no file text; the head revision is still the interim source until Task 8;
    - `tests/Panel.Tests/FakeAgent.cs`: `ApplyConfig` and `ReadConfig` funcs. The default `ApplyConfig` returns `Ok` with the SHA-256 of the text it received; the default `ReadConfig` returns empty;
  - `src/Agent/Games/IGameAdapter.cs`, `Arma3/Arma3Adapter.cs`, `Supervisor.cs`, `Areas.cs`, `Files/FileService.cs`, `PanelConnection.cs`, `Program.cs`, `Agent.csproj` (references ArmaFormats);
  - `tests/Agent.Tests/SupervisorTests.cs` and `FileServiceTests.cs`.

**Interfaces (Produces):**

```csharp
namespace Contracts;
public sealed record LaunchSpec(Guid ProfileId, string Slug, string InstallPath, string Executable, int Port, bool UseBasicCfg,
    string[] CustomArgs, HeadlessSpec? Headless = null, string? DifficultyPresets = null);   // DifficultyPresets only if F20 ruled so
public sealed record HeadlessSpec(int Count, string Connect, string? Password, bool WithServer); // used from Task 11
public sealed record ConfigBundle(Guid ProfileId, string Slug, string ServerCfg, string? BasicCfg, string DifficultyPresets);
public sealed record WrittenFile(string Name, string Sha256, long Size);
public sealed record ApplyResult(bool Ok, string? Error, string? BackupFolder, WrittenFile[] Files);
public sealed record ConfigSnapshot(string? ServerCfg, string? BasicCfg, string? Arma3Profile);
// IAgentClient gains:
Task<ApplyResult> ApplyConfig(ConfigBundle bundle);
Task<ConfigSnapshot> ReadConfig(Guid profileId, string slug);

public static class Arma3Command
{
    public static string[] ServerArguments(LaunchSpec spec, string configDir, string profileDir); // Phase 2 order; -cfg only when UseBasicCfg
    public static string Display(string exePath, IReadOnlyList<string> args);                      // Windows quoting, -password= masked
}
namespace ServerAgent.Games;
public sealed record ConfigTarget(string Name, string Area, string RelativePath);
// IGameAdapter: ConfigFiles(LaunchSpec) is removed; added:
IReadOnlyList<ConfigTarget> ConfigTargets(string slug); // server.cfg, basic.cfg, <slug>.Arma3Profile (Security boundary paths)
namespace ServerAgent;
public sealed class ConfigWriter(Areas areas, IGameAdapter game, Supervisor supervisor, ILogger<ConfigWriter> log)
{
    public ApplyResult Apply(ConfigBundle bundle);
    public ConfigSnapshot Read(Guid profileId, string slug);
}
```

**Rules:**
- **`Apply`** refuses with `Ok = false` when:
  - the profile is in `supervisor.Running`;
  - the slug fails the pattern;
  - a size cap is exceeded;
  - the difficulty text doesn't parse as one `class DifficultyPresets`.
- **`Apply` then:**
  1. Back up every existing target to `backups\profiles\<slug>\<stamp>\<name>`.
  2. Build the new profile file with `ConfigDocument.ReplaceClass("DifficultyPresets", …)` over the current file, or over "" when it's missing.
  3. Write temp files next to each target, then move them into place.
  4. If `BasicCfg == null`, delete `basic.cfg`.
  5. Re-read and hash every target.

  On any failure it restores from the backup, deletes files that didn't exist before, and returns `Ok = false`, with the error naming the file.
- **`Read`** returns each target's text, or null when the file is missing. It enforces the same caps and needs no file-extension allowlist.
- **`Supervisor.StartLocked`** no longer writes files. It refuses with "This profile hasn't been applied to the server yet." when `server.cfg` is missing, and with "basic.cfg is missing; apply the profile again." when `UseBasicCfg` is set and `basic.cfg` is missing. Arguments come from `Arma3Command.ServerArguments`.
- **`FileService.ListFiles`** also accepts `*.pbo` (listing only; `ReadFile` is unchanged).
- **`Areas`** gains `backups` → `<DataRoot>\backups`, shown in `Describe()`.

- [ ] **Step 1: Write failing tests:**
  - **`ConfigWriterTests`:**
    - `Apply_writes_files_and_returns_their_hashes`;
    - `Apply_backs_up_existing_files_first`;
    - `Apply_merges_only_the_difficulty_class`: an existing profile file with `version=1;`, `blood=1;` and an old `DifficultyPresets` keeps its other lines byte-exact;
    - `Apply_is_refused_while_running`, using the FakeServer;
    - `Apply_is_all_or_nothing`: hold `basic.cfg` open with `FileShare.None`, then apply; `server.cfg` still has its old content, the result names `basic.cfg`, and the backup exists;
    - `Null_basic_cfg_removes_the_file`;
    - `Read_returns_null_for_missing_files`.
  - **`SupervisorTests`:**
    - `Start_refuses_an_unapplied_profile`;
    - `Start_uses_the_applied_files_without_rewriting_them`: apply, change `server.cfg` on disk, then start; the fake's RPT `ARGS:` line names the file, and the file still has the changed content;
    - the existing launch tests apply first.
  - **`FileServiceTests`:** `List_pbo_files_in_mpmissions`.
- [ ] **Step 2:** Run `dotnet test tests/Agent.Tests`. Expected: the new tests FAIL.
- [ ] **Step 3:** Implement, and register `ApplyConfig` and `ReadConfig` in `PanelConnection`.
- [ ] **Step 4:** Run `dotnet test tests/Agent.Tests`, then the whole `dotnet test`. Expected: PASS. Panel.Tests must still compile: `FakeAgent` gains `ApplyConfig` and `ReadConfig` funcs.

---

### Task 8: Panel: apply, start from the applied revision, command preview, join info, missions list

**Files:**
- Create: `Features/Profiles/ApplyEndpoints.cs`, `tests/Panel.Tests/ApplyTests.cs`.
- Modify:
  - `Features/Servers/ServerEndpoints.cs` (`Control`, `ServerDto`, join);
  - `Features/Profiles/ProfileEndpoints.cs` (preview `command`);
  - `tests/Panel.Tests/FakeAgent.cs`: `AgentKit.CreateProfileAsync(…, bool applied = true)` applies after creating, so every Phase 2 start test has an applied revision. Pass `applied: false` to test `not_applied`.
  - `ServerTests.cs`.

**Endpoints:**
- **Applying:**
  - `GET /api/profiles/{id}/apply-preview` (`profiles.apply`) → `{ revision, canApply, reason?, files: FileDiff[] }`.
    - The current side comes from `ReadConfig`, masked with `MaskSecrets(text)`.
    - The new side is the head generated with `Real` and masked with `MaskSecrets(text, <secrets parsed from the current server.cfg>)`. Its profile file is `ReplaceClass` over the current text.
    - `canApply` is false, with a reason, while the server is running or the agent is offline.
  - `POST /api/profiles/{id}/apply { revision }` (`profiles.apply`).
    - **Refusals:** `revision ≠ head` is 409 `stale`; running is 409 `running`; agent offline is 409 `agent_offline`; secrets that can't be decrypted are 409 `secrets_unreadable`.
    - It calls `ApplyConfig` with `Real` text. It checks that `ApplyResult.Ok` is true and that the `server.cfg` and `basic.cfg` hashes equal the SHA-256 of the UTF-8 text it sent; otherwise 409 `apply_failed` with the agent's message.
    - Then it sets `AppliedRevisionId`, `AppliedAt` and `AppliedById`. Audit `profile.applied`, `{ revision, backupFolder }`.
- **Starting:** `Control.RunAsync` for Start and Restart.
  - No applied revision is 409 `not_applied`, "Apply the profile to the server before starting it."
  - The `LaunchSpec` comes from identity plus the applied snapshot: `UseBasicCfg = generated BasicCfg != null`, `CustomArgs = ConfigFiles.LaunchArguments`, and `Headless` from Task 11.
  - The interim head-revision source from Tasks 5 and 7 is removed.
- **Command preview:** `command.server` = `Arma3Command.Display(<installPath>\<exe>, ServerArguments(spec, configsPath\slug, profilesPath\slug))`, using the agent's reported `configs` and `profiles` area paths. When the agent hasn't said hello, the paths are `<agent data folder>\configs\…`.
- **Join info:** `GET /api/servers/{id}/join` (`server.viewJoinInfo`) → `{ address, port, password }`, where `password` comes from the applied revision and is null when there's no applied revision or no password.
- **Missions:** `GET /api/profiles/{id}/missions` (`profiles.view`) → `{ connected, files: [{ name, size, modifiedAt }] }`, from `ListFiles(<install area>, "mpmissions", "*.pbo")`.
- **`ServerDto`** gains `revision` and `appliedRevision`.

- [ ] **Step 1: Write failing tests:**
  - `Apply_writes_through_the_agent_and_records_the_revision`.
  - `Apply_with_an_outdated_revision_is_stale`.
  - `Apply_is_refused_while_running`.
  - `Apply_with_mismatched_hashes_is_not_recorded`.
  - `Apply_preview_masks_passwords_on_both_sides`: the current file on the FakeAgent has `passwordAdmin = "hunter2-old"`; no `hunter2` appears and the line is marked changed.
  - `Apply_preview_shows_only_the_difficulty_class_changing`: the current profile file has server lines plus an old class.
  - `Start_needs_an_applied_revision`.
  - `Start_launches_the_applied_revision_not_the_saved_one`: apply r1 with `-maxMem=4096`, save r2 with `-maxMem=8192`, then start; FakeAgent's recorded `LaunchSpec.CustomArgs` contains `-maxMem=4096`.
  - `Join_info_needs_viewJoinInfo_and_shows_the_applied_password`: a Member gets the password; a role without the key gets 403.
  - `Command_preview_uses_agent_paths_and_masks_passwords`.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter "Apply|Server"`. Expected: the new tests FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole `dotnet test`. Expected: PASS.

---

### Task 9: Frontend: Profiles section, generic settings forms, live preview and raw editors

**Files:**
- Create:
  - `web/src/features/profiles/`: `ProfilesPage.tsx`, `ProfileLayout.tsx`, `useProfileDraft.ts`, `SettingsForm.tsx`, `ConfigPreview.tsx`, `SaveBar.tsx`, `GeneralTab.tsx`, `ServerConfigTab.tsx`, `PerformanceTab.tsx`;
  - `web/src/components/SecretField.tsx`.
- Modify:
  - `App.tsx` (routes), `Sidebar.tsx` (a "Profiles" item: `SlidersHorizontalIcon`, `profiles.view`, after Server), `lib/types.ts`;
  - `features/server/SetupPage.tsx` (the profile form is removed; it links to Profiles);
  - delete `features/server/ProfileForm.tsx`.

**Interfaces:**
- **Routes:**
  - `/profiles` → `ProfilesPage`;
  - `/profiles/:id` → `ProfileLayout` with the SubNav tabs Overview (index), General, Server config, Performance, Difficulty, Missions, Startup, Headless clients, Revisions. A tab appears only once its task builds it.
- **`useProfileDraft(id)`** → `{ detail, draft, setValue(file, key, value), setExtra(file, text), dirty, save(note), discard, preview }`.
  - `preview` is the debounced (400 ms) `POST /preview` result.
  - `save` sends `baseRevision`. A 409 `stale` shows "Someone saved revision N meanwhile. Reload to see it; your edits are kept until you reload."
- **`SettingsForm({ file, tab, values, onChange, errors })`** renders the catalog rows for that file and tab, grouped by `Group`:
  - Bool01 and BoolWord → checkbox;
  - NumberList → one number input per item (`Length` of them);
  - a key whose class is kept raw (catalog *Class members* rule) → disabled, with "Set in the raw editor";
  - Integer and Number → number input with min/max;
  - Choices → select;
  - TextList → textarea, one item per line; blank lines are kept as `""` items, and a trailing newline adds none. A "Back to default" button removes the key, and an empty list shows the catalog's warning.
  - Text → input;
  - Flag → checkbox.

  An unset value shows "Default" as a placeholder. Errors appear under their field (`field` matches `<file>.<key>`).
- **`SecretField({ label, set, onChange })`:** shows "Set" or "Not set", with Replace (a password input) and Clear. It never shows a value.
- **`ConfigPreview({ title, text, file, baseRevision })`:** the generated text in a scroll box, plus an "Edit raw" toggle.
  - In edit mode it shows a mono `TextArea`, Save raw (`PUT /files/{file}`) and Cancel.
  - A 400 `config_syntax` message shows under the box.
  - The hint reads: "Comments and settings the form doesn't know are kept below the managed settings."
- **`SaveBar`:** sticky at the foot of the tab while `dirty`, with "Unsaved changes", a note input (≤ 200), Save (primary, busy while saving) and Discard.
- **Tabs:**
  - **General:** identity. Install, executable and port are disabled while the server runs, with the reason as hint. Then the server-tab groups and the three `SecretField`s.
  - **Server config:** the remaining server groups, the extras box and `ConfigPreview` of `server.cfg`.
  - **Performance:** basic keys and `ConfigPreview` of `basic.cfg` ("No basic.cfg is written while nothing is set.").
- **`ProfilesPage`:** a table with name, slug, install, port and revision, plus applied state as a StatusBlock:
  - "Applied r N" (ok);
  - "Changes not applied" (warn);
  - "Never applied" (off);
  - "Needs server.cfg" (bad).

  Also: New profile (a Dialog with identity fields; the slug follows the name until edited), Duplicate (a Dialog with name and slug), Delete (confirm), and Compare (pick two → `/profiles/compare?a=&b=`, Task 10).

- [ ] **Step 1:** Run `npm run build` in `web`. Expected: exit 0 (baseline).
- [ ] **Step 2: Implement.**
- [ ] **Step 3:** Run `npm run build`. Expected: exit 0.
- [ ] **Step 4: Browser check**, with `panel` and `web` running, signed in as the dev Owner:
  - create a profile and set hostname and maxPlayers; the preview updates within a second;
  - save → revision 2;
  - edit raw: add `fake_x = 1;` and save; the form is unchanged and `fake_x` stays in the extras;
  - set a password; the preview shows the "Passwords are set…" comment and no value;
  - at 375 px, the page doesn't scroll sideways.

  Record the results in the ledger.

---

### Task 10: Frontend: missions, difficulty, startup, revisions, compare, apply, server page

**Files:**
- Create:
  - `web/src/features/profiles/`: `MissionsTab.tsx`, `DifficultyTab.tsx`, `PresetsPage.tsx`, `StartupTab.tsx`, `RevisionsTab.tsx`, `ComparePage.tsx`, `ApplyDialog.tsx`;
  - `web/src/components/DiffView.tsx`.
- Modify: `App.tsx` (`/profiles/compare` and `/profiles/presets`), `ProfileLayout.tsx` (an "Apply to server" button for `profiles.apply`), `features/server/ServerPage.tsx`.

**Interfaces:**
- **`DiffView({ files: FileDiff[] })`:**
  - one block per file, with a "New file" or "No changes" label;
  - each line has a gutter (`+`, `−` or blank) and old and new line numbers;
  - added lines are `bg-ok/10`, removed lines are `bg-bad/10`, and each carries a visually hidden "added" or "removed";
  - `…` rows read "N unchanged lines";
  - it scrolls inside its own box.
- **Missions tab:**
  - **Rotation list:** each row has a file name, a Difficulty select (Recruit, Regular, Veteran, Custom; default Custom), a Params textarea (collapsed), up and down buttons and Remove.
  - **Adding entries:** "Add mission" is a select over `GET /missions` plus a "Type a name" input for addon missions. A Refresh button re-reads the list.
  - A row whose file isn't in the listing shows a warn StatusBlock, "Not in mpmissions".
  - A `persistent` checkbox, with the `-autoInit` note.
  - **"Use a hand-written Missions class"** toggles `MissionsOverride` (mono textarea) and hides the list.
  - **Now and next:** "Now running: <A2S mission>" from the live server, and "Next in rotation: <entry after it>". When the server is offline, "Not running".
- **Difficulty tab:** `SettingsForm` for difficulty; "Load preset" (a select that copies values and sets `difficultyPresetId`); "Save as preset" (a name dialog → `POST /api/difficulty-presets`); `ConfigPreview` of the class.
- **`PresetsPage`:** a list with built-ins marked; New, Edit, Copy and Delete for custom presets.
- **Startup tab:**
  - `SettingsForm` for launch, plus an "Extra arguments" input whose hint lists the allowlist;
  - the warnings for `-autoInit` and `-noLogs` from the catalog;
  - a "Command line" `<pre>` scroll box with the server line (and headless lines) and a copy button through `CopyField`.
- **Revisions tab:**
  - a list (number, note, who, when, plus an "Applied" StatusBlock);
  - choosing two shows a `DiffView` (`GET /compare`);
  - "Reset to this revision" opens a confirm Dialog: "Creates revision N+1 with these settings and passwords. The server keeps running what was applied until you apply again."
- **`ApplyDialog`:**
  - loads `apply-preview`;
  - **text:** "Backs up the current files on the server first. Takes effect the next time the server starts.";
  - shows `DiffView`;
  - when `canApply` is false, the primary button is disabled and `reason` is shown;
  - confirm → `POST /apply`; on 409 `stale` it reloads the preview.
- **`ComparePage`:** two profile selects, an identity table and a `DiffView`.
- **`ServerPage`:**
  - a **Join info** StatCard for `server.viewJoinInfo` showing address:port and the password (hidden until "Show") with copy buttons, or "No join password";
  - an **"Unapplied changes"** emphasis Panel for `profiles.apply`, when `revision > appliedRevision`, linking to the profile;
  - Start is disabled with "Apply the profile first" when `appliedRevision` is null.

- [ ] **Step 1: Implement.**
- [ ] **Step 2:** Run `npm run build`. Expected: exit 0.
- [ ] **Step 3: Browser check**, with the dev agent connected:
  - add a K: mission to the rotation and save;
  - apply; the dialog shows the files as New, then applying succeeds;
  - change maxPlayers, save and apply again; the diff shows one line;
  - the agent's `backups` folder has the earlier files;
  - reset to revision 1 → a new revision; compare r1 with the head shows the change;
  - the command line copies;
  - at 375 px, nothing scrolls sideways (diffs scroll in their box).

  Record the results in the ledger.

---

### Task 11: Agent: headless clients

**Files:**
- Create: `tests/Agent.Tests/HeadlessTests.cs`.
- Modify:
  - `src/Contracts/Messages.cs` (`ProfileStatus` gains `int HeadlessRunning = 0` as the last parameter);
  - `IAgentClient.cs` (`Task<CommandResult> StartHeadless(LaunchSpec spec)`, `Task<CommandResult> StopHeadless(Guid profileId)`);
  - `Arma3Command.cs` (`HeadlessArguments(LaunchSpec spec, int index, string profileDir)`);
  - `Supervisor.cs`, `StateFile.cs` (`StateEntry` gains `HcState[]? Headless = null`, with `HcState(int Index, int Pid, DateTime StartedAt)`);
  - `PanelConnection.cs`;
  - `tests/FakeArmaServer/Program.cs`. With `-client`, it binds no UDP port, writes an RPT line `Fake HC connecting to <connect>:<port>` under its `-profiles`, and exits on close.

**Rules:**
- **Arguments:**
  - `HeadlessArguments`: `-client`, `-connect=<Connect>`, `-port=<Port>`, `-password=<Password>` (only when set), `-profiles=<profileDir>\hc<n>` and `-name=hc<n>`;
  - `n` runs from 1 to `Count`, and `Count` is 1–8;
  - `Connect` must parse as IPv4, or the start is refused.
- **`StartHeadless`** refuses unless the profile's server is Starting or Online. It starts the missing indexes up to `Count`, with the same exe and working directory as the server, and records them in the server's `StateEntry`.
- **`WithServer`:** the agent starts the headless clients once the server first reaches Online after a Start.
- **Stop and Restart** close the headless clients first (`CloseMainWindow`, then kill after `StopTimeoutSeconds`), then the server. **ForceStop** kills them all. A server exit kills its remaining headless clients.
- **`StopHeadless`** closes only the headless clients.
- **Re-attach** checks each `HcState` with the same pid, process-name and start-time rules as the server.
- **An unexpected headless-client exit** lowers `HeadlessRunning` and doesn't restart it.

- [ ] **Step 1: Write failing tests** in `HeadlessTests.cs`, using the FakeServer:
  - `Headless_clients_start_beside_a_running_server`: Count 2 → two processes with `-client` and `-name=hc1`/`hc2`, and `HeadlessRunning == 2`;
  - `Headless_needs_a_running_server`;
  - `With_server_starts_them_when_online`;
  - `Stop_closes_headless_clients_before_the_server`;
  - `Headless_rpt_does_not_replace_the_server_console`: the server's log lines keep arriving, and no `Fake HC` line reaches `LogLinesRead`;
  - `Reattach_keeps_headless_clients`;
  - `Connect_must_be_ipv4`.
- [ ] **Step 2:** Run `dotnet test tests/Agent.Tests --filter Headless`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `dotnet test tests/Agent.Tests`, then the whole `dotnet test`. Expected: PASS, with the Phase 2 re-attach tests still green.

---

### Task 12: Panel and UI: headless clients

**Files:**
- Create: `tests/Panel.Tests/HeadlessApiTests.cs`, `web/src/features/profiles/HeadlessTab.tsx`.
- Modify:
  - `Features/Servers/ServerEndpoints.cs`: `POST /api/servers/{id}/headless/start` and `/headless/stop` (`server.control`), with audit `server.headlessStarted` and `server.headlessStopped`; `ServerDto` gains `headless: { enabled, count, running }`;
  - `Control` (`LaunchSpec.Headless` from the applied snapshot: password from the applied secrets, `Connect` from the first address);
  - `ProfileEndpoints.cs` (preview `command.headless`, masked);
  - `ServerPage.tsx` (a headless row with Start and Stop buttons when enabled);
  - `ProfileLayout.tsx` (the Headless clients tab).

**Headless tab:**
- Enabled toggle; Count (1–8); "Start with the server" checkbox; Addresses (TextList, IPv4, default `127.0.0.1`).
- **Warning:** "Only enable headless clients for missions that have headless client slots."
- The headless command lines come from the preview.

- [ ] **Step 1: Write failing tests:**
  - `Headless_settings_generate_server_cfg_arrays`;
  - `Headless_command_preview_masks_the_password`;
  - `Start_passes_the_applied_headless_spec`;
  - `Headless_start_needs_server_control`.
- [ ] **Step 2:** Run `dotnet test tests/Panel.Tests --filter Headless`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the whole `dotnet test` and `npm run build`. Expected: PASS and exit 0.

---

### Task 13: End to end on the K: server, README

**Files:** modify `README.md` (Phase 3 status, the Profiles and Apply workflow, and that headless clients need mission slots).

- [ ] **Step 1: Publish and restart.**
  - Stop the `panel` preview. Run `dotnet test` (both projects) and `npm run build`. Expected: all green.
  - Publish the agent to `D:\13th_server_dev\agent\`, keeping its `appsettings.json`.
  - Restart it detached, with the ReShade variable set.
  - Start `panel` and `web`. The Phase 2 profile "Main Operations" is upgraded to revision 1. Check that its hostname and passwords show as set.
- [ ] **Step 2: Build the test configuration.** In the browser, on "Main Operations":
  - hostname "13th Console E2E", maxPlayers 12, a join password, `persistent` on, `-autoInit`;
  - one difficulty change from Regular (`thirdPersonView = 0`);
  - a rotation with the F14 mission;
  - two unknown lines added in the raw editor.

  Save, then Apply. Expected: the dialog lists the files and the apply succeeds.
- [ ] **Step 3: The files.** On disk, `configs\main\server.cfg` equals the preview with the real password, and the unknown lines are present. `main.Arma3Profile` keeps the server's own lines and holds the new class.
- [ ] **Step 4: Boot.** Start from the browser. Expected:
  - A2S shows hostname "13th Console E2E" and max players 12;
  - the F15 mission and map in the server page's Mission field;
  - per F16, the visibility byte shows the password;
  - the RPT has no config errors and shows the mission loading (F17).
- [ ] **Step 5: Applied versus saved.** While the server runs, save a change. The server page shows "Unapplied changes" and Apply is refused with the running reason. Stop the server; Apply now succeeds; the backup folder holds the previous files.
- [ ] **Step 6: Headless clients.** Enable 1 headless client, apply and start. Then start the headless client from the server page. Record the F18-based outcome:
  - the process is running, and `HeadlessRunning == 1`;
  - the server RPT shows the connect attempt.

  Stop from the browser: the headless client exits first, then the server.
- [ ] **Step 7: Re-attach.** Start again. Restart the agent: the same server pid re-attaches and its headless clients are still counted. Then stop.
- [ ] **Step 8: K: untouched.** Run the Phase 2 scan. Expected: no files written under K: since Step 1.
- [ ] **Step 9: Screenshots** of the profile's Server config tab, the Apply dialog and the Revisions compare, at desktop and 375 px. Ask the user to show the browser pane (Ctrl+Shift+B) if it's hidden.

## After the last task

- **Final review:** one fresh reviewer on the most capable model, given:
  - the file list (there's no git, so no review package);
  - this plan, the spec and the ledger's `Ruling:` lines;
  - the Review Focus.
- **Fix pass:** Critical and Important findings get one fix pass, each fix RED→GREEN with the suite green. Minors go to the ledger.
- **Report:** every ruling with its cost if wrong, the deferred minors, and the Task 13 screenshots.
