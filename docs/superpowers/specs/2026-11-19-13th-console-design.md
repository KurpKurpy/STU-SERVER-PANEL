# 13th Console — design

Date: 2026-11-19 · Status: draft, awaiting approval
Source spec: [2026-11-19-source-spec.md](2026-11-19-source-spec.md), saved verbatim from the user's message. It is referred to as "spec §N" below. This doc records how the spec gets built. Where this doc is silent, the spec governs. This doc doesn't repeat the spec's feature lists.

## Amendment A: no approval; mod compatibility decides what's playable (user, 2026-11-19)

The user replaced the spec's approval workflow:
- Mission makers upload a mission and it goes straight onto the server. There is no submit, review, approve or publish step.
- A **Server Missions** tab lists the missions on the server for a chosen modlist. It defaults to the modlist the server currently has selected.
- The panel scans which mods each mission needs and which mods the modlist provides. A mission shows up for that modlist only when they match.
- Zeus and mission makers can start a listed mission themselves, without an admin's sign-off.

**What "match" means (my reading; the user to confirm).** A mission matches when the modlist provides **every** addon the mission needs. Extra mods in the modlist don't hide a mission.
- **Why not an exact match:** `mission.sqm` records only the addons actually used by placed objects and modules (its `addons[]` list), never the full set of mods loaded in Eden. CBA, TFAR, sound mods and UI mods normally never appear, so an exact match would hide nearly every mission. This is how Eden is known to behave, but the offline reference doesn't document it. **Verify against real unit missions in Phase 4.**
- **Non-matching missions:** hidden by default. A "Show missions that don't match" toggle lists them, naming the missing mods, so a mission maker can see why theirs isn't listed.

**What this replaces in the spec:**
- The approval workflow (§7) and the approval check in deployment (§8).
- The "submit for approval" and "approved missions" items in §5, §9 and §27.
- Manually assigning required mods (§5, §7, §27). The scan does that job, and mission pages show which modlists each mission is compatible with.

## 1. Intent

13th Console is a private, self-hosted web panel for the 13th's Arma 3 unit.
- **Mission makers** can prepare a whole operation in the browser. They upload straight onto the server, then edit and version there. They never need remote access to the server box or an admin to move files for them.
- **Admins** get FASTER-equivalent server management (profiles, Workshop mods through SteamCMD, configs, launch parameters, mission rotation, headless clients), backed by real processes on the real server.
- **Zeus and mission makers** start any mission that matches the selected modlist. Commanders run a readiness check before an op.

Priority order (spec §28): mission management → FASTER-style server management → operations → server control → file management → automation → other games.

## 2. Goals, non-goals, anti-goals

**Goal:** every acceptance item in spec §27 works, backed by real backend behaviour and tested against a real Arma 3 dedicated server.

**Non-goals**
- Billing, hosting plans, public sign-up, marketplace (spec §28).
- A Linux server host. The server box is Windows; Linux would be a later adapter concern.
- A second server machine. The design allows more agents later (§5.3); nothing is built for it now.
- Game integrations beyond the adapter seam (spec §21).
- Sending email. Invites and password resets are links an admin copies into Discord.

**Anti-goals** (the project fails if any of these happen, even if everything else works)
- Fake or hardcoded stats, buttons that do nothing, placeholder screens (spec §28). A section that isn't built yet doesn't appear in the navigation.
- Claiming a state without checking it. *Online* means the process is alive and answers a Steam query. *Deployed* means the file was written and its hash re-checked on the server. *Installed* means the mod files are present and verified. *Progress* bars show real measurements, or go indeterminate when nothing can be measured.
- Losing a mission file. Every overwrite of a published or server-side file is preceded by a recoverable copy.
- A mission maker (or any non-admin) reaching host files outside their scoped areas, or seeing secrets (RCon password, admin password, Steam credentials, API keys).
- Changing the live server's modset or config without an explicit, confirmed, authorised action.

## 3. Constraints and facts found

- **Hosts.** The real server runs on a Windows machine the unit controls (user's decision). The panel and the agent both run on that machine. Development happens on the current Windows 10 PC.
- **Dev test server.** The Arma 3 client install on `K:\Steam\steamapps\common\Arma 3` includes `arma3server_x64.exe`, which is usable for local tests. Treat K: as read-only. Dev data and test server folders go on D: (C: and K: each have under 30 GB free).
- **Toolchain.** Node 24 is installed.
  - .NET: only SDKs 7.0.401 and 9.0.305 are installed, both at or past end of support. .NET 10 is the current LTS.
  - `dotnet` on PATH resolves to `C:\Program Files (x86)\dotnet`, an x86 host with no SDKs.
  - Install the .NET 10 (LTS) x64 SDK and make sure the x64 host comes first on PATH. The user approves the install first.
- **FASTER licence.** FASTER is GPL-3.0. We use it as a functional reference only and copy no code or assets. Config keys and launch parameters come from Bohemia's documentation (the `arma-reference` corpus), not from FASTER's model classes.
- **FASTER doesn't use SteamCMD.** It downloads with BytexDigital.Steam, a reverse-engineered SteamKit2 wrapper, and its wiki has a "Steam broke Faster" page about that library breaking. We use SteamCMD (spec §10).
- **Steam ownership.** Both the dedicated server (app 233780) and Arma 3 Workshop items (app 107410) need a Steam account that owns Arma 3. Corrected by the Phase 5a spike (fact S5): an anonymous `+app_update 233780` fails with "No subscription". The first login needs Steam Guard (a code or mobile approval); after that SteamCMD reuses its cached login.
- **Workshop API.** Search needs a Steam Web API key (`IPublishedFileService/QueryFiles`). Adding a mod by ID or URL and reading its metadata works without a key (`ISteamRemoteStorage/GetPublishedFileDetails`).
- **Console and RCon.** On Windows, the dedicated server's console window can't be read as a stdout stream (verify early in Phase 2). The live log is the newest `.rpt` in the profile directory. Commands go into a running server through BattlEye RCon. Without BattlEye and an RCon password, the console is read-only and the UI says so.
- **Binarized missions.** Eden can save `mission.sqm` binarized (rapified, "raP" format), which isn't text.
  - Because the compatibility scan has to read `addons[]` from every mission, the panel includes its own read-only raP reader.
  - It uses that reader to show a binarized file as text, read-only.
  - Editing a binarized `mission.sqm` still needs it re-saved unbinarized from Eden, or an admin-configured external converter (for example CfgConvert from Arma 3 Tools). We never bundle that tool.
  - **Verify in Phase 4:** whether Eden's "Binarize the Scenario File" option is on by default (the offline reference doesn't say). If it is, binarized files are the usual case. The fixes are mission makers unticking the option, or an admin configuring CfgConvert.
- **Mission PBO format.** It's simple: a header, entries and a SHA1 trailer. Eden-exported missions use uncompressed entries.
  - The panel reads and writes uncompressed mission PBOs itself.
  - It rejects compressed entries with a clear message, and falls back to the configured external extractor when one is set.
- **Which addons a mod provides.** These come from the `CfgPatches` classes in the `config.bin` or `config.cpp` of each PBO in the mod's `addons` folder. The agent reads them with the same PBO and raP readers.
  - Base-game and DLC addons come from scanning the server install the same way.
  - A mod whose configs can't be read (compressed or encrypted) is flagged "couldn't scan" instead of being guessed at.
  - Encrypted `.ebo` files, used by some Creator DLC, can't be read. For those, the mission's `addonsMetaData` (which names the mod each addon came from) is matched against the CDLC folders the profile loads. **Verify in Phase 5.**

## 4. Approaches (decided: B)

- **A. The spec's suggested split:** a Next.js frontend server, an ASP.NET Core API and a Windows-service agent.
  - Three processes in two runtimes, with login and permission checks in two places.
  - Server-side rendering gives a logged-in internal tool nothing.
- **B. Chosen:** an ASP.NET Core (.NET 10) panel serving a React + TypeScript single-page app, plus a .NET agent running as a Windows service.
  - Two processes, one backend language.
  - The panel can restart or update during an op without touching the Arma server.
  - Only the agent has rights to launch processes and write server folders.
  - Another machine later means another agent.
- **C. A single ASP.NET process that launches Arma itself.**
  - Fewest parts.
  - A panel restart can drop or orphan the server, and the internet-facing process would hold full server rights (spec §23 asks for separation).
- **All-Node: dropped.** Windows services and process control are awkward in Node, and the spec leans toward .NET.

The user picked B on 2026-11-19.

## 5. Technical architecture

### 5.1 Processes

```
Browser ──HTTPS, cookie auth──► Panel  (ASP.NET Core, .NET 10)
                                  │  React SPA (static files), REST /api, SignalR /hubs/ui
                                  │  SQLite (EF Core), content-addressed blob store
                                  │
                                  ▲  agent dials out: SignalR /hubs/agent (agent key)
                                  │  + HTTPS GET /agent/blobs/{sha256} to pull files
                                Agent  (.NET 10 worker, Windows service, restricted account)
                                  ├─ arma3server_x64.exe (+ headless client processes)
                                  ├─ steamcmd.exe
                                  ├─ BattlEye RCon (UDP) + Steam A2S query (UDP)
                                  └─ area roots: mpmissions, keys, mods, profiles, configs, logs, BattlEye, backups
```

### 5.2 Panel

- **Endpoints.** ASP.NET Core minimal APIs, one `Endpoints.cs` per feature folder. Each endpoint declares its permission with `.RequirePermission("…")` (§7).
- **Auth.** ASP.NET Core Identity handles password hashing, lockout, TOTP authenticator 2FA and the security stamp, using cookie auth.
  - Cookie is `HttpOnly; Secure; SameSite=Strict`.
  - A small middleware checks an antiforgery header on every unsafe method.
  - Disabling a user sets a permanent lockout and rotates their security stamp. The stamp check interval is about 1 minute, so open sessions end within a minute.
- **Login** is by username (the member's callsign). Email is optional and not used for delivery.
- **2FA.** Available to everyone. A setting lists the roles that must enrol; it defaults to Owner and Server Administrator. Users in those roles are sent to enrolment after login until they've done it.
- **Rate limiting.** ASP.NET's built-in limiter applies tight limits to login, 2FA, invite-accept and setup, plus a generous per-user global limit.
- **Owner bootstrap.** When no users exist, the panel generates a one-time setup token, logs it and writes it to `data/setup-token.txt`. The `/setup` page needs that token to create the Owner account. The token is deleted once used.
- **Secrets.** The RCon password, server admin password, Steam Web API key and Discord webhook URLs are encrypted with ASP.NET Data Protection (keys in the data directory, protected with machine-scope DPAPI). The API only ever returns "set" or "not set" for them, and replacing one is write-only.
  - Exception: the server *join* password is shown to holders of `server.viewJoinInfo`, because members need it to connect. This arrives in Phase 3, when the structured `server.cfg` editor separates the password; Phase 2 shows address and port to `server.view`.
- **Real time (from Phase 2).** Phase 1 has nothing live to push; the activity feed refetches on focus and on an interval. From Phase 2, the SignalR hub `/hubs/ui` is authenticated with the same-origin cookie. Clients join groups per profile to receive status, console lines and job progress. Joining and invoking check permissions.
- **Jobs.** Long work (install, update, deploy, backup) is a `Job` row dispatched to the agent. The row records progress and the outcome. After a panel restart, a running job is marked *interrupted*, never left as *running*.
- **Static files.** The SPA build is served from `wwwroot`. In development, the Vite dev server proxies `/api` and `/hubs` to the panel.
- **Data directory.** Configurable as `DataDir`. It holds `console.db`, `blobs/` and `keys/`. In development it's `13th_console/data/`, which is gitignored.

### 5.3 Agent

- **Hosting.** A .NET worker using `UseWindowsService()`. It's installed with `sc.exe` under a dedicated local account (e.g. `arma-svc`) that has modify rights only on the server root, the SteamCMD folder and the backup folder.
- **Pairing.** An admin creates an agent in the panel and gets a one-time-shown key (random 256-bit; the panel stores only its hash). The admin pastes the key into the agent's `appsettings.json`, which is ACL'd to the service account.
- **Connection.** The agent connects out to `/hubs/agent` with that key and reconnects automatically. Outbound-only means no inbound port on the server box, and remote agents later work unchanged.
- **Commands.** The panel calls the agent with SignalR client-result invocations. The agent replies with a result, and for long work, a job id.
- **Events.** The agent pushes process-state changes, A2S status, log lines, job progress and resource stats.
- **Launch boundary.** The agent launches only allowlisted executables: `arma3server_x64.exe` (and its profiling variant) under a registered install root, and `steamcmd.exe` in the configured SteamCMD folder. Arguments are passed as an argument list, never through a shell. Custom args are validated against the known Arma parameter syntax (`-name` or `-name=value`) and can't name another executable. A compromised panel therefore still can't run arbitrary programs on the box.
- **Paths.** The area roots live in the agent's own `appsettings.json`. The setup wizard shows them with their checks but can't change them, so a compromised panel can't redirect where the agent writes. The agent never takes a raw path. Every path is an `(area, relativePath)` pair resolved against the agent's configured area roots. Anything rooted, containing `..`, or resolving outside its area (including through junctions or symlinks) is rejected.
- **File transfer.** The agent pulls file content from the panel by SHA-256 (authenticated with its agent key), verifies the hash, writes to a temp file in the target folder, then moves it into place atomically. Files going the other way (log or backup downloads) are streamed through a panel endpoint.
- **Supervision.** The agent launches Arma with the command line it generates and records the PID and start time in its state file, so it can re-attach after an agent restart. It never kills the server just because the agent or panel restarted.
  - **Graceful stop:** RCon `#shutdown` when available (Phase 7), otherwise close the process's main window, then kill after a timeout (60 s by default). Stop returns at once and the wait runs in the background. Force Stop kills immediately, even during a stop.
- **Status truth.** *Online* only while the process is alive **and** A2S_INFO answers on the query port (game port + 1). That reply also supplies mission, map, players and max players. *Starting*, *Stopping*, *Updating*, *Error* and *Offline* come from the process and job state. *Not responding* means the server was Online, then missed 3 queries in a row. Whenever the agent isn't connected, the panel shows the state as *unknown*, never the last one it saw.
- **Resources.** CPU and RAM per Arma process (Process API), host CPU and host network in/out (performance counters; Windows has no cheap per-process network figure, so the UI labels it "host"), and free disk on the server drive. The agent keeps a short history so the panel can draw charts.
- **Logs.** The agent tails the newest `*.rpt` in the profile directory and captures SteamCMD output per job.
- **Progress.** SteamCMD's own progress lines are used when it prints them. Otherwise progress is bytes on disk against the Workshop `file_size`. With neither available, the bar is indeterminate.
- **Game adapter seam.** `IGameAdapter` covers install and update arguments, the launch command, the config files to write, the health check, backup paths and log locations. It has one implementation, `Arma3Adapter`, and profiles carry `Game = "arma3"`. Spec §21 asks for this seam; nothing else implements it yet.

### 5.4 Key flows

**Mission upload → version → on the server** (Phase 4)

1. **Upload** a PBO, a zip, or a folder dropped into the browser. Uploads are streamed with real progress and validated server-side:
   - extension and a size cap;
   - zip-slip and zip-bomb limits (entry count, total uncompressed size);
   - `mission.sqm` present at the root;
   - folder or file named `Name.Map`, which gives the map;
   - duplicate-name check.
2. **Store.** A PBO upload is stored as-is (the original is kept) and unpacked into the mission's working copy. A zip or folder upload becomes the working copy directly.
3. **Edit.** Each editor save writes a new blob and a file revision. A stored blob is never overwritten.
4. **Save version.** This snapshots the working copy into an immutable version: a manifest of path→hash, a note and an author. The version's PBO is packed at that moment, then validated by reading it back and comparing hashes.
5. **Scan.** Saving a version reads `addons[]` and `addonsMetaData` from its `mission.sqm`, text or binarized. It stores the required addons together with the mod names Eden recorded for them.
6. **Onto the server.** Every saved version goes straight onto the server, with no review step:
   - The agent backs up any existing `mpmissions/<file>.pbo`, pulls the new PBO by hash, writes it, re-hashes it and reports.
   - A `Deployment` row is recorded only after the hash matches. `Mission.CurrentVersionId` then points at that version.
   - If the file is locked because that mission is running, the write waits, and the mission page says it will apply when the mission ends. **Verify locking behaviour in Phase 4.**
   - **Rollback:** restoring an older version makes it current and writes it the same way.
   - **Work in progress:** mission makers edit in the working copy, which never touches the server until they save a version.

**Server Missions tab and Start Mission** (Phase 5)

- **Modlist picker.** Defaults to the modlist the server currently has selected.
- **Provided addons.** The base-game and loaded-DLC addons scanned from the install, plus the addons of every mod flagged Client or Server-only in that modlist. Optional and HC-only mods don't count, because the server doesn't load them.
- **Listing.** A mission is listed when all its required addons are provided. The toggle shows the rest, with their missing addons named by mod wherever Eden recorded the mod.
- **Start Mission** needs `missions.start`, which Zeus and Mission Maker hold by default. It's a single scoped action:
  - If the server is already running this modlist and RCon is available (Phase 7+), it switches mission with `#mission`, with no process restart.
  - Otherwise it writes the profile's mission config so that mission loads on start, sets the profile's modlist to the selected one if it differs, then starts or restarts the server.
  - The confirmation spells out what will happen, e.g. "Restarts the server; 14 players online will be disconnected."
  - It grants no config editing or raw server control. It's recorded in the audit log and can notify Discord.

**Prepare Operation** (Phase 6)

- Runs the spec §9 checks as a function over the database plus live agent state. It returns pass, fail or "can't tell" for each check, with a fix action where one exists.
- **Apply to server** is a separate, confirmed action. It applies the profile, collection and rotation, then (re)starts the server.

## 6. Project directory structure

```
13th_console/
  13th_console.slnx
  global.json                  pins .NET 10 SDK
  README.md                    dev setup, run, publish, agent install
  .gitignore                   data/, wwwroot/, bin/, obj/, node_modules/
  docs/superpowers/specs/      this doc + source spec
  docs/superpowers/plans/      one implementation plan per phase
  src/
    Panel/                     ASP.NET Core panel
      Program.cs
      appsettings.json
      Data/                    AppDb, entities, Migrations/
      Auth/                    Permissions.cs (catalog), policies, setup bootstrap
      Features/                one folder per area, each with Endpoints.cs and its types
        Members/ Roles/ Invites/ Announcements/ Activity/ Settings/      Phase 1
        Agents/ Servers/ Logs/                                            Phase 2
        Profiles/                                                         Phase 3
        Missions/ Files/                                                  Phase 4
        Mods/                                                             Phase 5
        Operations/ Notifications/                                        Phase 6
        Backups/ Schedules/ Players/                                      Phase 7
      Hubs/                    UiHub, AgentHub (Phase 2)
      Storage/                 BlobStore (Phase 4)
      wwwroot/                 SPA build output
    Agent/                     Windows service (Phase 2)
      Program.cs
      Areas.cs                 scoped path resolution
      Games/IGameAdapter.cs
      Games/Arma3/             launch args, cfg writers, A2S query, keys
      Steam/SteamCmd.cs
      Rcon/BattlEyeRcon.cs
    Contracts/                 message types shared by Panel and Agent (Phase 2)
    ArmaFormats/               PBO read/write, raP reader, addons[]/CfgPatches extraction (Phase 4); used by Panel for missions and Agent for mods and the install
  web/                         React + TypeScript + Vite
    package.json  vite.config.ts  index.html
    src/
      main.tsx  routes.tsx
      components/ui/           shadcn/ui components (copied in, owned)
      components/              AppShell, Sidebar, StatusBadge, ...
      lib/                     api.ts (fetch + antiforgery), hub.ts (SignalR), auth.ts (me + can())
      features/                dashboard/ members/ settings/ auth/ in Phase 1; later phases add servers/ logs/ profiles/ missions/ files/ mods/ operations/ backups/
      styles/tokens.css        theme tokens (dark + light)
  tests/
    Panel.Tests/               xUnit + WebApplicationFactory integration tests
    Agent.Tests/               Phase 2+
  data/                        dev runtime data (gitignored)
```

Frontend libraries: React Router, TanStack Query, Tailwind v4, shadcn/ui (Radix), Phosphor icons, `@microsoft/signalr`, Monaco (`@monaco-editor/react`, Phase 4), and the unit website's fonts through `@fontsource`.

## 7. Permissions model

- **Catalog.** A static list in `Auth/Permissions.cs`, grouped by area:
  - Members & admin: `members.view`, `members.manage`, `roles.manage`, `settings.manage`, `audit.view`, `announcements.manage`
  - Server: `server.view`, `server.viewJoinInfo`, `server.control` (start/stop/restart), `server.forceStop`, `server.update`, `server.console` (send RCon commands), `players.manage`, `agents.manage`
  - Profiles: `profiles.view`, `profiles.manage`, `profiles.apply`
  - Missions: `missions.view`, `missions.download`, `missions.create`, `missions.editOwn`, `missions.editAny`, `missions.start`
  - Mods: `mods.view`, `mods.manage`, `mods.install`, `collections.manage`, `keys.manage`
  - Operations: `operations.view`, `operations.manage`, `operations.prepare`, `operations.apply`
  - Files: `files.<area>.read` / `files.<area>.write` for each file-manager area
  - Other: `logs.view`, `logs.viewAll`, `backups.manage`, `schedules.manage`
- **The catalog grows by phase.** Each phase adds only its own permissions, and its migration grants them to the seeded roles per spec §5. In Phase 1 the role editor therefore lists only Phase 1 permissions, never controls for features that don't exist yet.
- **Phase 2 additions:** `server.control`, `server.forceStop`, `profiles.manage` and `logs.view` (Server Administrator; Zeus also gets `logs.view`), plus `agents.manage` (Owner only). `profiles.manage` arrives here rather than in Phase 3, because someone has to create the first minimal profile.
- **Phase 3 additions:**
  - `profiles.view` (Server Administrator, Zeus);
  - `profiles.apply` (Server Administrator);
  - `server.viewJoinInfo` (every seeded role).

  `profiles.manage` now means editing.
- **Starting missions.** `missions.start` defaults to Zeus and Mission Maker (Amendment A). There is no approval permission.
- **Roles** use Identity's role tables. A role's permissions are role claims of type `perm`. Seeded roles follow spec §5: Owner, Server Administrator, Mod Manager, Mission Maker, Zeus, Member.
  - Owner passes every check and can't be deleted.
  - The last Owner can't be demoted, disabled or removed.
- **Users** can hold several roles, plus per-user overrides stored as user claims `perm.grant` and `perm.deny`. Effective permissions = (union of role permissions ∪ grants) − denies. This covers "give an individual Zeus extra permissions".
- **Enforcement.**
  - Every endpoint requires a permission policy.
  - The authorization handler reads the user's effective permissions from the database on each request, so a change applies to the very next request.
  - Rules about a specific resource are checked in the handler. Example: `missions.editOwn` lets the author and listed collaborators edit, but nobody else.
- **Frontend.** The SPA gets the effective list from `/api/me` and only uses it to hide or disable UI.
- **Tests.** Integration tests sign in as each seeded role and assert 403/200 on one representative endpoint per permission group, plus the override cases.

## 8. Database schema

SQLite through EF Core, with no SQLite-only SQL, so the PostgreSQL provider can be swapped in later. Ids are GUIDs. Timestamps are UTC `DateTime`, because EF's SQLite provider can't order by `DateTimeOffset`. Tables are listed by the phase that introduces them.

**Phase 1**
- Identity tables (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetRoleClaims`, `AspNetUserClaims`, …).
  - `User` adds: `DisplayName`, `Rank` and `Section` (optional free text, e.g. `GYSGT`, `13:M2`, as on the unit's order of battle), `CreatedAt`, `LastLoginAt`. Ownership is membership of the system Owner role; there is no separate flag.
  - `Role` adds: `Description`, `IsSystem`.
- `Invite`: `Id`, `TokenHash`, `RoleId`, `CreatedById`, `CreatedAt`, `ExpiresAt`, `UsedAt`, `UsedById`
- `AuditEntry`: `Id`, `At`, `ActorId`, `Action` (e.g. `member.invited`, `mission.deployed`), `TargetType`, `TargetId`, `Summary`, `DetailsJson`, `Ip`, `IsPublic` (shown in Recent Activity)
- `Announcement`: `Id`, `Title`, `BodyMarkdown`, `Pinned`, `CreatedById`, `CreatedAt`, `UpdatedAt`
- `AppSetting`: `Key`, `Value`, `IsSecret` (secret values are Data-Protection-encrypted)

Password reset uses Identity's built-in reset token, delivered as a link. It needs no table.

**Phase 2**
- `Agent`: `Id`, `Name`, `KeyHash`, `MachineName`, `Version`, `LastSeenAt`
- `ServerInstall`: `Id`, `AgentId`, `Name`, `Game`, `InstallPath` (`Branch` waits for Phase 5's SteamCMD work)
- `Profile` (minimal in Phase 2, extended in Phase 3): `Id`, `ServerInstallId`, `Name`, `Slug`, `Executable`, `Port`, `PublicAddress`, `ServerCfgRaw` (encrypted with Data Protection; only `profiles.manage` can read it), `BasicCfg` (for the Phase 2 import; Phase 3 can fold it into `SettingsJson`), `CustomArgs`
- `ProcessRun`: `Id`, `ProfileId`, `Kind` (server/hc), `Pid`, `StartedAt`, `StartedById`, `StoppedAt`, `ExitCode`, `StopReason`
- `Job`: `Id`, `Type`, `Status`, `Progress` (nullable), `TargetType`, `TargetId`, `CreatedById`, `CreatedAt`, `StartedAt`, `FinishedAt`, `Error`, `LogBlobSha`. Created in the first phase with a long job (Phase 4 or 5), because nothing in Phase 2 runs long.

**Phase 3**
- `Profile` gains:
  - `SettingsJson` (structured server.cfg / basic.cfg / difficulty / launch / headless-client settings);
  - `ExtraServerCfg`, `ExtraBasicCfg` (unknown entries kept verbatim);
  - `DifficultyOverride` (a hand-written `DifficultyPresets` class, kept verbatim);
  - `SecretsProtected` (the three passwords, Data-Protection-encrypted);
  - `DifficultyPresetId` (only records which preset was loaded last: profiles copy a preset's values);
  - `ModCollectionId` (FK added in Phase 5);
  - `AppliedRevisionId`, `AppliedAt`, `AppliedById`, `NeedsCfg`, `UpdatedAt`.

  The Phase 2 `ServerCfgRaw` and `BasicCfg` columns are emptied by the Phase 3 upgrade and dropped in Phase 4.
- `ProfileRevision`: `Id`, `ProfileId`, `Number`, `SnapshotJson`, `SecretsProtected` (passwords are revisioned, encrypted), `Note`, `CreatedById`, `CreatedAt`
- `DifficultyPreset`: `Id`, `Name`, `ValuesJson`, `IsBuiltIn` (Recruit, Regular and Veteran seeded read-only), `UpdatedAt`
- `RotationEntry`: `ProfileId`, `Order`, `FileName`, `MissionId` (nullable), `Difficulty`, `ParamsText`

**Phase 4**
- `Blob`: `Sha256` (PK), `Size`, `CreatedAt`. The file lives at `blobs/ab/cd/<sha256>`.
- `Mission`: `Id`, `ClassName`, `Map`, `DisplayName`, `Description`, `Type`, `Category`, `AuthorId`, `CurrentVersionId`, `IsArchived`, `CreatedAt`, `UpdatedAt`
- `MissionCollaborator`: `MissionId`, `UserId`
- `Tag`: `Id`, `Name`; `MissionTag`: `MissionId`, `TagId`
- `MissionWorkingFile`: `MissionId`, `Path`, `Sha256`, `UpdatedById`, `UpdatedAt`
- `MissionFileRevision`: `Id`, `MissionId`, `Path`, `Sha256` (null = deleted), `ById`, `At`
- `MissionVersion`: `Id`, `MissionId`, `Number`, `Note`, `CreatedById`, `CreatedAt`, `PboSha256`, `OriginalUploadSha256`
- `MissionVersionFile`: `VersionId`, `Path`, `Sha256`
- `MissionVersionAddon`: `VersionId`, `Addon` (CfgPatches class), `ModName`, `ModUrl` (the last two from `addonsMetaData`, nullable)
- `Deployment`: `Id`, `MissionVersionId`, `ProfileId`, `FileName`, `Status`, `JobId`, `BackupPath`, `DeployedById`, `DeployedAt`

**Phase 5**
- `Mod`: `Id`, `WorkshopId` (nullable), `LocalFolder` (nullable), `Name`, `InstallPath`, `SizeBytes`, `WorkshopUpdatedAt`, `InstalledAt`, `Status`, `LastError`, `PreviewUrl`, `KeyFilesJson`, `ScanStatus`, `ScannedAt`
- `ModAddon`: `ModId`, `Addon` (CfgPatches class it provides)
- `InstallAddon`: `ServerInstallId`, `Folder` (root, a DLC or a CDLC folder), `Addon`
- `ModCollection`: `Id`, `Name`, `Description`, `UpdatedById`, `UpdatedAt`
- `ModCollectionItem`: `CollectionId`, `ModId`, `Order`, `Client`, `ServerOnly`, `Headless`, `Optional`. These are FASTER-style flags; a mod can be both Client and Headless.

**Phase 6**
- `Operation`: `Id`, `Name`, `Description`, `StartsAt`, `MissionId`, `MissionVersionId`, `ProfileId`, `ModCollectionId`, `MissionMakerId`, `Notes`, `BriefingMarkdown`, `Status`
- `OperationZeus`: `OperationId`, `UserId`
- `ChecklistItem`: `Id`, `Label`, `AutoCheckKey` (nullable; null = manual), `Order`, `Enabled`
- `OperationCheck`: `OperationId`, `ChecklistItemId`, `Done`, `ById`, `At` (manual items only)
- `NotificationRule`: `EventType`, `Discord`, `InApp`
- `UserNotification`: `Id`, `UserId`, `Text`, `Link`, `CreatedAt`, `ReadAt`

**Phase 7**
- `Schedule`: `Id`, `Type`, `Cron`, `TargetType`, `TargetId`, `Enabled`, `NextRunAt`, `CreatedById`
- `Backup`: `Id`, `Kind` (manual/scheduled/pre-deploy/pre-update), `Scope`, `Path`, `SizeBytes`, `JobId`, `CreatedById`, `CreatedAt`, `Note`

The ban list stays in BattlEye's `bans.txt`, which the agent reads and writes. It is not copied into the database.

## 9. Dashboard design

**Look** (restyled 2026-11-19 after the user found the first build "too AI-looking"): the console is a sibling of the unit website ("Drop Zone", `13th-STU-Website`). It keeps the Halo/UNSC terminal feel and the order-of-battle sheets' colours, and takes the website's type, patch and in-game photography. The first mock-ups (https://claude.ai/artifact/Q78B5iibg8hmk8dx6i7XWy) show the earlier look and are now out of date; the live app is the reference.
- **Shape language:**
  - cards are square with a hairline border; a short red bar marks each card's top-left corner, the way the sheets head each block in red;
  - cut corners are the signature and appear only in a few places: the primary button, the active nav item (a red arrow with black text), and the dashboard hero band;
  - no section codes, `>>>`/`//` filler, grid, scanlines, holo stripe or glow.
- **Colour:**
  - the website's neutrals: background `#0E100F`, cards `#171A19` and `#1D2120`, text `#ECEDED`, secondary `#D3D6D4`, dim `#A2A8A5`, hairlines at 12% white;
  - unit red `#E8453C` (text `#F2716A`) with black text on solid red, as on the sheets; a red-tinted card (`#241513`) marks anything that needs action now (Panel `emphasis`);
  - status shown as solid blocks with black text: green `#93C47D` (the sheets' "Eligible"), amber `#F6B26B`, red, grey.
- **Cyan variant:** classic UNSC cyan `#4FD1F0` stays a one-attribute swap (`data-accent="cyan"` on `<html>`). Red ships.
- **Fonts** (the website's): Big Shoulders Stencil Display 900 for page, panel and hero titles (uppercase, tight leading), Barlow for UI text and labels, IBM Plex Mono only for figures, IDs, times, logs and code.
- **Brand assets:** the unit patch ("ALL WE NEED IS LUCK", `web/public/brand/patch-*.webp`) in the sidebar and on sign-in; in-game photos (`breach-smoke`, `pelican-deploy`) under a scrim on the sign-in screen and the dashboard hero. The motto lives on the patch and is not typeset again.
- **Restraint:** photos sit behind scrims and never under data. Status colours always come with a text label.
- **Light theme:** the spec asks for one, but this look is dark by nature. Decide later.

**UI rules.** From the ui-ux-pro-max checklist, applied to the mock-ups on 2026-11-19. Every screen follows them.
- **Focus:**
  - Every interactive element shows a visible `:focus-visible` state.
  - Cut-corner elements clip outlines, so they use an inset 2 px ring instead; everything else gets a 2 px outline offset by 2 px.
  - The current nav item carries `aria-current="page"`.
- **Contrast:** at least 4.5:1 for all text, including small mono labels and editor line numbers. Every pair in the mock-ups was checked at 5:1 or better.
- **Touch:** at least 44×44 px targets on touch devices (`pointer: coarse`). Mouse users may get 34–40 px compact controls in dense tables and toolbars.
- **Feedback:** every clickable element has a hover state and a pointer cursor. Colour, border and background transitions take 150 ms. Nothing scales or shifts layout on hover.
- **Motion** (added 2026-11-19 from the high-end-visual-design pass):
  - everything is spring-eased (`--ease-spring`, cubic-bezier(.32,.72,0,1)) and animates `transform`/`opacity` only;
  - panels cascade in on each page (fade-up 14 px, 600 ms, 70 ms steps);
  - dialogs scale in over 400 ms with a fading backdrop;
  - buttons press to 98%.

  `prefers-reduced-motion` turns all of it off. Rejected from that skill because they clash with direction A: rounded pills and squircles, glass blur, 96–160 px section padding, a floating island nav.
- **Animated components** (from the animated-component-libraries pass, built with CSS and a small hook, no new dependencies):
  - **Removed in the 2026-11-19 restyle:** the decrypted-text title and the border beam read as generic effects. Attention is now shown with the red-tinted `emphasis` card (used for required 2FA enrolment).
  - **Animated list** (Magic UI pattern): activity and announcement rows cascade in; on refresh only new rows animate.
  - **Rejected** as landing-page effects that would distract in an admin console or clash with direction A: particles, aurora/plasma backgrounds, marquees, a dock, shimmer buttons. CountUp is held for Phase 2's live server stats.
- **Icons:** Phosphor (`@phosphor-icons/react`, regular weight; bold for the active nav item), SVG only. No emoji, and no glyphs that platforms may render as emoji.
- **Status and charts:** status is never colour alone; it always has a text label. Chart series differ by line style as well as colour, and every chart has a text summary for screen readers.
- **Phone width:**
  - body and table text is 16 px;
  - multi-column rows stack to one column;
  - wide tables and code scroll inside their own box, never the page.
- **Layers:** a fixed z-index scale: content 0, sticky headers 10, dropdowns and popovers 20, dialog overlays 50, toasts 60.
- **Forms:** every input has a real `<label>`. Async buttons are disabled while they run and show their progress. Errors appear next to the field they're about.
- **Build:** tokens live in Tailwind v4's `@theme` and components use the theme classes, so red↔cyan stays a one-token swap.

**Design tokens:** [`docs/design/tokens.css`](../../design/tokens.css), a Tailwind v4 `@theme`. It holds the semantic colours, the type scale, breakpoints, cut sizes, motion and the z-index scale, plus the cyan override and the `chamfer`, `chamfer-sm` and `focus-ring-inset` utilities; `web/src/styles/app.css` adds the `display` (stencil title) utility. Every text/background pair in it was measured at 4.5:1 or better. Components use the semantic tokens (`bg-surface`, `text-accent-text`), never raw hex. Phase 1 copies the file into `web/src/styles/`.

**Component inventory.** Each component is built once and reused, and the tokens give the look. Native elements come first (`<button>`, `<table>`, `<dialog>`, `<select>`, checkbox). shadcn/Radix are added only when a native element falls short, such as combobox, menus or tooltips; Phase 1 needs none. Phase 1 builds the ones marked ●.

| Component | Built on | Variants and notes |
|---|---|---|
| ● AppShell (Sidebar, TitleBar) | plain | patch + stencil unit name; sidebar groups; active item `aria-current`, red fill, arrow cut; stencil page title; collapses to a drawer below `md` |
| ● SubNav | plain `<nav>` links | underline marks the active tab; wraps |
| ● Panel | plain | square card, red corner bar, stencil title and right-hand meta; `emphasis` = red-tinted card for "act now" |
| ● Button | shadcn Button | default (square outline) · primary (red fill, ink text, `chamfer-sm`) · danger (red outline) · sm (34–40 px on mouse, 44 px on touch) · icon (needs `aria-label`); `focus-ring-inset`; disabled while busy |
| ● StatusBlock | plain | ok / warn / bad / off; ink text, always with a word |
| ● RoleChip | plain | hairline outline, Barlow uppercase |
| ● Table | shadcn Table | Barlow semibold uppercase dim header; row hover; scrolls in its own box |
| ● KeyValue | `<dl>` | 2- or 4-column grids |
| ● Field | shadcn Input / Select / Switch / Textarea + `<label>` | Secret field (shows "set", Replace, never reveals); error text under the field |
| ● Dialog | shadcn Dialog | confirm pattern: what will happen + impact + Cancel / primary; overlay `--z-overlay`, square card with the red corner bar |
| ● Toast | Sonner | queued / done / failed; `--z-toast` |
| Gauge | SVG | ring with dashed outer ticks; numerals `text-3xl` |
| Segments | plain | skewed readiness bar (n of 11) |
| StatCard | Panel (small cut) | icon tile + label + value + "/ max" |
| Console | plain + virtualised list | timestamped lines with info/warn/error/BattlEye colours, find, RCon input |
| CodeView / Editor | Monaco | SQF and Arma config grammars, theme built from the tokens, diff view |
| Chart | small SVG or a chart lib | area + line; series differ by colour and dash; text summary |
| FileTree | plain | binarized/unsaved tags; 44 px rows on touch |

**Server Control layout** (from the user's Pterodactyl reference):
- start/restart/stop at the top right;
- the live console on the left with the RCon command line at its foot;
- stacked stat cards on the right (address, uptime, CPU, memory, disk, network in and out);
- three history charts below;
- then players and headless clients.

**Desktop wireframe** (once all phases exist):

```
┌───────────┬───────────────────────────────────────────────────────────────┐
│ 13TH      │ Dashboard                                         [name ▾]    │
│ CONSOLE   ├────────────────────────────────┬──────────────────────────────┤
│           │ SERVER · Main Operations       │ UPCOMING OPERATIONS          │
│ Dashboard │ ● Online   12 / 64 players     │ Sat 21 Nov 19:00  Op Kestrel │
│ Operations│ co40_Kestrel.Altis · up 3h12m  │   Altis · Zeus 2 · ready 9/11│
│ Missions  │ CPU 34%  RAM 9.1 GB  Disk 412GB│ Wed 25 Nov 19:30  Training   │
│ Mods      │ 203.0.113.5:2302  [copy]       │   Stratis · ready 11/11      │
│ Profiles  │ [Start] [Stop] [Restart] [Console]                            │
│ Server    ├────────────────────────────────┼──────────────────────────────┤
│ Files     │ ANNOUNCEMENTS                  │ RECENT ACTIVITY              │
│ Logs      │ ▲ Mod update Friday 18:00 …    │ 18:02 uploaded Kestrel v4    │
│ Backups   │   New members: read #rules …   │ 17:40 started Kestrel v4     │
│ Members   ├────────────────────────────────┴──────────────────────────────┤
│ Settings  │ RECENT MISSIONS   name · map · maker · status · updated        │
└───────────┴───────────────────────────────────────────────────────────────┘
```

**Phase 1 reality**
- The navigation shows only Dashboard, Members and Settings.
- The server card says "No server agent connected"; admins also see a link to the agent setup guide.
- Announcements and Recent Activity are live.
- The Operations and Recent Missions panels appear in the phases that build them.
- Each control renders only if the user holds its permission.

**Mobile.** A single column, with the sidebar becoming a drawer.

## 10. Backend and agent interfaces

**Panel REST, Phase 1**
- `POST /api/setup`: only while no users exist; needs the setup token.
- `POST /api/auth/login`, `POST /api/auth/logout`, `POST /api/auth/2fa`; `GET /api/me` (user plus effective permissions)
- `POST /api/invites` → link; `GET` and `POST /api/invites/{token}`: inspect and accept
- `GET /api/members`; `GET` and `PATCH /api/members/{id}` (roles, overrides, disable); `POST /api/members/{id}/reset-link`
- `GET`, `POST`, `PUT` and `DELETE /api/roles`; `GET`, `POST`, `PUT` and `DELETE /api/announcements`
- `GET /api/activity`: audit entries with `IsPublic`. `GET /api/audit` (`audit.view`): everything, filterable.
- `GET` and `PUT /api/settings`
- `GET /api/servers`: an empty list until an agent exists. The UI turns that into "No server agent connected".

**Agent commands, Phase 2+**
- Process: `GetStatus`, `Start(profile launch spec)`, `Stop`, `ForceStop`
- Files: `WriteFiles(area, files[])`, `PullBlob(area, path, sha)`, `List`, `Read`, `Write`, `Move`, `Copy`, `Delete` (all area-scoped)
- Other: `RunSteamCmd(job)`, `Rcon(command)`, `TailLog(path)`

**Agent events:** `StatusChanged`, `LogLines`, `JobProgress`, `Resources`.

## 11. Implementation roadmap

The spec's phase order is kept. Each phase gets its own plan in `docs/superpowers/plans/` and ends with a "done" check against real things.

**Phase 1 — Foundation** (planned next)
- **Builds:**
  - the solution, the SPA scaffold, and SQLite with migrations;
  - Identity login with lockout, TOTP 2FA, enforced 2FA for chosen roles, and rate limits;
  - Owner bootstrap, invites, members, roles with per-user overrides, and the audit log;
  - announcements, a dashboard (announcements, activity, honest server card), settings (app name, 2FA roles; later phases add their own settings);
  - the chosen look (§9) and a responsive shell. Whether a light theme is built is decided with the direction.
- **Done when:**
  - the Owner bootstraps with the setup token and invites a member who accepts;
  - role and override changes apply on the next request;
  - integration tests prove 403 for every seeded role lacking a permission;
  - nothing on screen is fake;
  - `dotnet publish` produces one folder that serves the SPA and API on localhost on this PC. HTTPS is configured with the real access route in Phase 2. A trusted local dev certificate would need the user to run `dotnet dev-certs https --trust`.

**Phase 2 — Server connection**
- **Builds:**
  - the agent service, pairing, and the minimal `Profile` with a raw `server.cfg`;
  - start, stop, restart and force stop;
  - status from the process plus A2S;
  - RPT live tail with search, copy and download;
  - resource stats and the logs page;
  - the setup wizard (paths, detect an existing install, import an existing `server.cfg`/`basic.cfg`, connection test).
- **Done when:**
  - the K: test server starts and stops from the browser;
  - *Online* shows only while A2S answers;
  - restarting the panel or the agent leaves the server running and re-attached.

**Phase 3 — Profiles**
- **Builds:**
  - profile create, duplicate, rename and delete;
  - revisions with compare and reset;
  - visual and raw editors for `server.cfg`, `basic.cfg` and `Arma3Profile`, with unknown entries preserved;
  - difficulty presets;
  - the launch-parameter editor with a full command preview and a copy button;
  - mission rotation (easy selector and override editor);
  - headless-client config and launch;
  - port and folder conflict checks (each profile reserves its game port through port + 4);
  - Apply, with a file diff preview and a backup first.
- **Done when:** the generated files boot the real server with the expected settings.

**Phase 4 — Missions** (top priority)
- **Builds:**
  - the library with search, filters, tags and categories, and uploads with progress and validation;
  - PBO read/write with validation, the blob store, and the working-copy editor (Monaco with SQF and Arma config highlighting, find/replace, unsaved-change warnings);
  - file history, versions with compare (Monaco's diff view) and restore;
  - every saved version written straight to the server with a backup, and restore as rollback;
  - required-addon scanning of `mission.sqm`, text or binarized;
  - the file manager over scoped areas, and PBO and version downloads.
- **Done when:**
  - a real mission goes uploaded PBO → edited → saved as a version → on the server, then boots;
  - rollback restores the previous file;
  - a binarized `mission.sqm` has its addons read and is shown read-only.

**Phase 5 — Workshop and mods**
- **Builds:**
  - SteamCMD server install and update (branch and CDLC options);
  - the Steam Guard flow: the panel relays the prompt, the password is never stored, and SteamCMD's cached login is used afterwards;
  - metadata, search (with an API key), mod list and status, update detection, and install/update/remove/reinstall;
  - collections with Client / Server-only / HC / Optional flags and FASTER's bulk actions;
  - Launcher HTML preset import and export;
  - key deploy, backup and cleanup;
  - mod folders exposed as junctions `mods\@<workshopId>`, and `-mod`/`-serverMod` generation;
  - CfgPatches scanning of mods and the server install;
  - the Server Missions tab with the compatibility filter, and Start Mission.
- **Done when:**
  - a real Launcher preset is imported, its missing mods downloaded and keys deployed, and the server boots with it;
  - a mission that uses one of that preset's mods is listed only for modlists that include the mod;
  - Start Mission, run as a Zeus account, boots that mission.

**Phase 6 — Operations**
- **Builds:**
  - the calendar and list, and create/edit;
  - mission, version, profile, collection and Zeus assignment;
  - the configurable checklist (automatic and manual items) and the readiness indicator, where "mission approved" is replaced by "mission matches the operation's modlist";
  - Prepare Operation and Apply to server;
  - Discord webhooks and in-app notifications.

**Phase 7 — Automation and admin**
- **Builds:**
  - backups (manual, scheduled, pre-deploy, pre-update) with restore, download and delete;
  - cron schedules with run history;
  - RPT diagnostics rules with explanations;
  - the RCon player list, kick, ban, unban, say, and ban list;
  - RCon settings and log;
  - the audit history UI.

**Phase 8 — Acceptance**
- Walk the spec §27 list on the real server box, fix what fails, and write the deployment guide.

## 12. Decided by the user

- The real server runs on a Windows machine the unit controls.
- Approach B.
- Amendment A: no approval step; uploads go straight onto the server; matching the selected modlist decides what's listed; Zeus and mission makers start missions themselves.
- Look: a Halo/UNSC terminal style in the unit's colours (from the order-of-battle sheets). Server Control is laid out after the user's Pterodactyl console reference.
- **Direction chosen (2026-11-19):** canvas board "Dashboard · A: UNSC terminal in unit red", red by default with the cyan switch kept. The Superdesign project (https://superdesign.dev/teams/df0f1961-c03b-42c9-9f02-2777bfda9de8/projects/5fcf0412-b166-49f7-b2e9-63b839a94a17) is reference only, not the target.
- Everything else in the source spec: features, roles, phase order, priorities.
- Design approved with Amendment A on 2026-11-19.
- **Access and hosting (2026-11-19):** members reach the console on a public domain the unit owns, through Caddy as a reverse proxy with a Let's Encrypt certificate. The real server box is a separate machine from this PC. Phase 2 is built and tested here against the K: test server; installing on the box (Caddy, DNS, firewall, the panel and agent as Windows services) gets its own short deployment plan.
- **Phase 2 plan approved (2026-11-19)** with its eight rulings: agent paths shown but not edited in the wizard; `profiles.manage` in Phase 2; a `BasicCfg` column; no `Job` table or `Branch` yet; window-close-then-kill stop until RCon; a *Not responding* state; live log and charts kept in memory; join password in Phase 3. They're written into §5.2, §5.3, §7 and §8 above.
- **Phase 3 plan approved (2026-11-19)** with its fifteen rulings, run inline. Among them:
  - **Configuration:**
    - saving creates a revision; only Apply (`profiles.apply`, server stopped, backup first) writes files, and Start launches the applied revision;
    - name, address, install, executable and port are edited directly, not revisioned; the slug is fixed once a profile exists;
    - passwords are revisioned, encrypted, and never returned, except the applied join password to `server.viewJoinInfo`.
  - **Config text:**
    - entries the forms don't know are kept as source text; a value read from a file is never refused;
    - the panel manages only the `DifficultyPresets` class of the server's own `.Arma3Profile`;
    - difficulty presets are copied into a profile, not linked.
  - **Headless clients** run only beside a running server.
  - **Sources:** config keys are cited from Bohemia's Server Config File, Basic Server Config File and server.armaprofile pages, supplied by the user.

  See `docs/superpowers/plans/2026-11-19-phase-3-profiles.md`.
- **Restyle (2026-11-19, kept by the user):** the user found the first build "too AI-looking". The console now borrows the unit website's type, patch and photography while keeping the terminal feel and the order-of-battle colours (see §9 Look). Current patch and motto: "ALL WE NEED IS LUCK" (the user's `13Logo3.png`).

## 13. Calls made in this design (open to correction)

1. The panel reads and writes uncompressed mission PBOs and reads binarized (raP) configs itself. A configurable external tool is the fallback for compressed PBOs and for converting binarized `mission.sqm` files for editing. This meets the spec's "configurable PBO utility" without depending on one.
2. Mission files live in a content-addressed blob store with database manifests, not a git repo per mission. That gives immutable history and de-duplication, with diffs from Monaco and no native git dependency on the server box.
3. The agent dials out to the panel over SignalR, and files are pulled by hash.
4. Permissions are stored as Identity role and user claims, with per-user grant and deny overrides, and checked against the database on every request.
5. Invites and password resets are copy-paste links; there is no email server.
6. The live console is an RPT tail plus RCon command input. It's read-only without BattlEye RCon.
7. *Online* means the process is alive and A2S answers.
8. Mod folders are exposed to Arma as junctions `mods\@<workshopId>`, which keeps the command line short.
9. The navigation shows only sections that work in the current phase.
10. Timestamps are stored as UTC `DateTime`.
11. The Steam password is never stored, and Steam Guard is relayed through the panel.
12. 2FA is enforced for Owner and Server Administrator by default, and the set of roles is configurable.
13. The Phase 2 profile is minimal (raw `server.cfg` + args), and Phase 3 builds the structured editors on the same entity, so nothing is throwaway.
14. Login is by username (callsign), not email.
15. Permissions and settings appear only in the phase that makes them do something.
16. The panel runs on the same machine as the server and agent. The agent design still allows the panel to live elsewhere later.
17. The agent launches only allowlisted executables, with no shell.
18. "Match" means the modlist provides every addon the mission needs, not an identical set (Amendment A; the user to confirm).
19. Missions that don't match are hidden by default and viewable behind a toggle, with the missing mods named.
20. Start Mission restarts the server when it has to; a fast switch through RCon arrives with RCon in Phase 7.
21. The scan replaces manually assigning required mods; mission pages show compatible modlists instead.
22. Members carry optional Rank and Section text, so the roster reads like the order of battle. These are display only and grant no permissions.

## 14. Left to the builder

- Endpoint request and response shapes, the component breakdown, EF configuration and copy text.
- How the A2S and RCon clients are written (small hand-written clients or vetted packages).
- The SQF Monarch grammar for Monaco.
- Test granularity beyond the minimums named in each phase's "done when".

## 15. Open, not blocking Phase 1

- **Existing setup:** whether the server box already runs an Arma server or FASTER whose files should be imported. The setup wizard supports import either way.
