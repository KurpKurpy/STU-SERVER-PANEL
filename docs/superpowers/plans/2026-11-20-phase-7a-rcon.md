# Phase 7a — RCon (BattlEye) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The console talks to each running server's BattlEye RCon, so admins and Zeus can see who's on, kick, ban, unban and message players, and Stop shuts the server down through RCon.

**Architecture:**
- **The agent owns RCon.**
  - It writes `BEServer_x64.cfg` into the profile's `BattlEye` folder at every launch: a generated password, a port of game port + 10, and `RConIP 127.0.0.1`.
  - It keeps one RCon session per running server: login, a 30 s keep-alive, acknowledging server messages, and reconnecting.
  - It answers `Rcon(profileId, command)` calls and pushes server messages and the RCon state to the panel.
- **The panel** turns that into players, bans, kick, ban, unban, say and an allowlisted command box. It keeps an in-memory RCon log per server and audits every action.

**Tech Stack:** as Phase 6b: .NET 10 (10.0.15), raw `UdpClient` (no new packages), SignalR client results, EF Core Sqlite, React 19, TanStack Query 5, xUnit.

**Spec:**
- `docs/superpowers/specs/2026-11-19-13th-console-design.md`:
  - §5.3 "Graceful stop: RCon `#shutdown` when available (Phase 7), otherwise close the process's main window, then kill";
  - §5 Secrets (the RCon password is encrypted, write-only);
  - §7 `server.console`, `players.manage`;
  - §8 "The ban list stays in BattlEye's `bans.txt` … not copied into the database";
  - §11 Phase 7 "the RCon player list, kick, ban, unban, say, and ban list; RCon settings and log".
- Source spec §15:
  - **Player information:** name, player ID, ping, time connected, connection status, "Clearly show when a particular player statistic is not available".
  - **RCON and BattlEye:** connection status, settings, password management, ban management, player management, RCON logs, "never expose secrets to ordinary accounts".
- **Spike:** `.superpowers/sdd/2026-11-20-phase-7a-rcon/spike.md`, from a real BattlEye 1.221 on the dev PC. It's the authority on the wire format and the command outputs.

**Split:** Phase 7 is three plans, 7a RCon (this one), 7b backups and schedules, and 7c diagnostics and audit. RCon comes first because graceful stop and scheduled restarts depend on it.

## Global Constraints

- **.NET:** run it as `"C:\Program Files\dotnet\dotnet.exe"`.
- **Tests:** Panel tests run with `--artifacts-path`; Agent tests use the default output.
- **Git:** there's no git repository and the user handles git. Commit nothing.
- **Disks:** `K:\` is read-only. The dev data root is `D:\13th_server_dev`.
- **Agent build:** this phase changes agent code and the contracts. Before the live run, publish to `D:\13th_server_dev\agent-build`, robocopy into `agent\` excluding `appsettings*.json`, and restart the agent with `dotnet ConsoleAgent.dll` and `RESHADE_BASE_PATH_OVERRIDE`.
- **The RCon password** never appears in API responses, logs, audit, hub messages to browsers, or the RCon log. It's generated: 24 alphanumerics.
- **Exposure:** RCon binds to `127.0.0.1` only; the spike showed the LAN address is unreachable. The RCon port is game port + 10, outside the guide's firewall range (port … port + 4).

## Review Focus

1. **Acting on the wrong player or ban.**
   - **Cases:** player numbers are reused after a leave or rejoin; ban numbers shift after a removal.
   - **Expected:** kick, ban and unban carry the GUID, and are refused (409) unless a fresh listing shows that GUID at that number.
2. **Text injection.**
   - **Cases:** a reason or message containing line breaks, control characters or very long text; a crafted raw command.
   - **Expected:** line breaks and control characters are stripped and lengths capped (reason 120, message 200); raw commands are allowlisted.
3. **Stop.**
   - **Cases:** RCon is down, `#shutdown` never arrives, or the server ignores it.
   - **Expected:** it falls back to closing the window and then killing, as before. A `#shutdown` exit reads as an asked-for stop ("stopped"), never "unexpectedly". Force Stop is unchanged.
4. **No RCon.**
   - **Cases:** BattlEye off (`BattlEye = 0`); a server just started; a wrong password; RCon dropped.
   - **Expected:** the status reads Off / Connecting / Connected / Can't connect with a reason. The player list says "RCon isn't available" rather than erroring, and the page still works.
5. **Secrets and personal data.**
   - **Expected:**
     - the password never reaches a browser, log or audit;
     - IPs and GUIDs reach only `players.manage`;
     - the RCon log (it holds IPs) is limited to `players.manage` and capped at 500 lines per server.

---

## Plan Rulings

1. **Scope.**
   - **Builds:**
     - the RCon client and sessions (agent);
     - the BattlEye config written at launch;
     - `#shutdown` as the first step of Stop;
     - the RCon state on the server status;
     - players (with GUID and IP for `players.manage`);
     - kick, ban (minutes and reason) and unban;
     - say, to everyone or one player;
     - the ban list;
     - the RCon log;
     - an allowlisted command box (`server.console`);
     - a new RCon password on demand.
   - **Not built (ledgered):**
     - the fast `#mission` switch for Start Mission;
     - add-a-ban-by-GUID for someone offline;
     - external RCon tools (RCon stays loopback-only);
     - a persistent RCon log.
2. **Wire format (spike):** `"BE"` + CRC32, little-endian, over `0xFF` + payload, then `0xFF` + payload.
   - **Types:** `0x00` login (reply `0x00 0x01` = OK); `0x01` command (seq 0–255, wrapping); `0x02` server message, which is acknowledged with `0x02 seq`.
   - **Multi-packet replies:** body `0x00 | count | index`.
   - **Keep-alive:** an empty command every 30 s.
   - **Text:** commands are sent as ASCII. Non-ASCII in say and reasons is replaced with `?`, since the spike couldn't confirm how BattlEye shows UTF-8.
3. **Config at launch.**
   - **The agent** writes `<profiles>\<slug>\BattlEye\BEServer_x64.cfg` (`RConPassword`, `RConPort`, `RConIP 127.0.0.1`), creating the folder.
   - **Stale copies:** it first deletes any `BEServer_x64_active_*.cfg`. BattlEye renames the cfg to an active copy while running and only renames it back on a clean exit.
   - **When:** only when the `LaunchSpec` carries `Rcon`, which the panel sends only when the profile's BattlEye setting is on.
4. **Password and port.**
   - **Secrets** gain `RconPassword`. It's generated at the first launch that needs it and saved; it's write-only.
   - **New RCon password** generates a new one, which takes effect at the next start.
   - **The port** is game port + 10.
   - **Collisions:** profile create and port change refuse a port whose game range (port … port + 4) or RCon port collides with another profile's, on any install.
5. **Sessions.** The agent's `RconSessions` opens a session when a server's status first reaches Online, and closes it when the server stops.
   - **State:** `RconState { Off, Connecting, Connected, Failed }`, plus an `RconError`, on `ProfileStatus`.
   - **A refused login** is Failed "the RCon password was refused", with no retry storm: it waits 30 s between tries.
   - **Commands:** `Rcon(profileId, command)` waits at most 8 s, inside the 20 s agent-call limit.
   - **The reply** is capped at 256 KB, inside the 1 MiB hub limit.
6. **Stop.** Graceful stop sends `#shutdown` first, after setting `StopRequested`, when the session is Connected. It then waits up to `StopTimeoutSeconds` for the exit, with the same window-close and kill fallback.
   - **An exit nobody asked for, with exit code 0** (an in-game admin's `#shutdown`), reads as Offline "The server shut itself down (exit code 0)", not as a crash.
   - **Non-zero exit codes** stay Error.
7. **Players** (`GET /api/servers/{id}/players`, `server.view`).
   - **Everyone** gets each row's number, name, ping, lobby flag and "first seen" (when the panel first saw that GUID in this session), plus the RCon state.
   - **`players.manage` also gets** the GUID, whether it's verified (`(OK)` or `(?)`), and the IP.
   - **Rows that don't parse** keep their raw text (for `players.manage`).
   - **The output format** comes from the spike's header, `[#] [IP Address]:[Port] [Ping] [GUID] [Name]`. Real rows weren't seen (an empty server), so the parser falls back to the raw line.
8. **Acting** (`players.manage`).
   - **Kick:** `{number, guid, reason}`. Ban: `{number, guid, minutes (0 = permanent, ≤ 525600), reason}`.
   - **Unban:** `{number, guid}` against `bans`, then `removeBan <#>`.
   - **Say:** `{message, number?, guid?}`. Without a number it goes to everyone (`say -1`).
   - **GUID re-check:** every action with a number first re-runs `players` or `bans` and refuses with 409 `changed` unless that number shows that GUID.
   - **Text:** reasons and messages have control characters and line breaks stripped and are capped (120 and 200).
   - **Audit** records `players.kick`, `players.ban`, `players.unban` and `players.say`, with the name, a GUID prefix and the reason.
9. **Raw commands** (`POST /api/servers/{id}/rcon`, `server.console`).
   - **Allowed first words:** `players`, `bans`, `admins`, `missions`, `version`, `say`, `loadBans`, `writeBans`, `#lock`, `#unlock`. Anything else is refused (400) and named.
   - **Why:** the spike showed RCon also accepts config lines (`MaxPing`, `loadScripts`, …).
   - **Audit:** `rcon.command`.
10. **The RCon log** is a panel-side ring of 500 lines per server: server messages from the agent, plus each panel action with who did it.
    - **Delivery:** `GET /api/servers/{id}/rcon/log` and a `rcon` hub event to `players.manage` viewers.
    - **Not kept** across a panel restart.
11. **Permissions.**
    - **New:** `players.manage` ("Kick, ban and message players; see their IP and GUID; the RCon log") and `server.console` ("Send RCon commands from the allowlist").
    - **Defaults:** Server Administrator gets both; Zeus gets `players.manage`.
12. **Web:** a **Players** tab on the server page.
    - the RCon state;
    - the players table (name, ping, time on; for `players.manage` also GUID and IP);
    - Kick and Ban dialogs, with reason and minutes, that say what they'll do;
    - Say to all;
    - Bans with Unban;
    - the RCon log and command box, for those who hold its permissions;
    - **New RCon password** on the profile's General tab, beside the other secrets.
13. **Live run.** It needs a temporary dev profile (approved) and the agent republished. Real player rows and kick/ban on a real player need someone to join; ask then, or the report says they weren't checked.

## Interfaces (Task 1 produces, later tasks consume)

```csharp
// Contracts
public enum RconState { Off, Connecting, Connected, Failed }
public sealed record RconSpec(int Port, string Password);                // LaunchSpec gains RconSpec? Rcon
public sealed record RconReply(bool Ok, string? Text, string? Error);
// ProfileStatus gains RconState Rcon = RconState.Off, string? RconError = null
// IAgentClient: Task<RconReply> Rcon(Guid profileId, string command);
// IAgentHub:    Task RconMessage(Guid profileId, DateTime at, string text);
// Agent
public static class BattlEyePacket { byte[] Login(string pw); byte[] Command(byte seq, string cmd); byte[] Ack(byte seq); bool TryParse(ReadOnlySpan<byte> data, out byte type, out ReadOnlySpan<byte> body); }
public sealed class RconClient : IAsyncDisposable { Task<bool> LoginAsync(...); Task<string> CommandAsync(string cmd, CancellationToken ct); event Action<string> Message; }
```

---

### Task 1: The protocol and the client (agent)

**Files:** `src/Contracts/Rcon.cs`; `src/Agent/Rcon/{BattlEyePacket,RconClient}.cs`; `tests/Agent.Tests/{BattlEyePacketTests,RconClientTests,FakeBattlEye}.cs`.

- [ ] **Failing tests:**
  - `Packets_match_the_spike`: the login packet's CRC is little-endian over `0xFF` + payload, checked against a golden byte array computed in the test from zlib-equal CRC32; a reply with a bad CRC is rejected.
  - `Login_ok_and_refused`.
  - `A_split_reply_is_reassembled_in_index_order`.
  - `Server_messages_are_acknowledged_and_raised`.
  - `Keepalive_is_sent_while_idle` (with a short interval injected).
  - `A_command_with_no_reply_times_out`.
  - `Non_ascii_is_sent_as_question_marks`.
  - `FakeBattlEye` is an in-process UDP server on 127.0.0.1 written to the spike's notes.
- [ ] **Run:** the tests fail. **Implement** to Ruling 2. **Run:** they pass, and so does the Agent suite.

### Task 2: Launch config, sessions, stop and status (agent)

**Files:** `src/Contracts/Messages.cs` (`LaunchSpec.Rcon`, `ProfileStatus.Rcon/RconError`), `IAgentClient.cs`, `src/Agent/Rcon/RconSessions.cs`, `src/Agent/Supervisor.cs`, `PanelConnection.cs`, `Program.cs`; tests in `tests/Agent.Tests/RconSessionTests.cs` and `SupervisorTests.cs`.

- [ ] **Failing tests:**
  - `Launch_writes_the_BattlEye_cfg_and_removes_stale_active_copies`: no cfg when `Rcon` is null.
  - `A_session_opens_when_online_and_reports_its_state`: Connecting → Connected; a wrong password gives Failed with its reason.
  - `Rcon_call_returns_the_reply_or_says_why_not`: not running, or no session.
  - `Graceful_stop_sends_shutdown_first_and_the_exit_is_a_requested_stop`: the fake process exits with 0 → Offline.
  - `Without_RCon_stop_falls_back_to_closing_the_window`.
  - `An_unrequested_exit_with_code_0_is_offline_and_nonzero_is_error`.
- [ ] **Run:** they fail. **Implement** to Rulings 3, 5 and 6. **Run:** they pass, and so does the Agent suite.

### Task 3: Panel — settings, permissions, players, actions, log

**Files:**
- **New:** `src/Panel/Features/Players/{PlayerEndpoints,PlayerList,RconLog}.cs`.
- **Changed:**
  - `ProfileSettings.cs` (`Secrets.RconPassword`);
  - `ProfileEndpoints.cs` (port collisions, New RCon password, the `LaunchSpec.Rcon` builder shared with `ServerEndpoints`);
  - `Permissions.cs`;
  - `AgentHub.cs` (`RconMessage`);
  - `FakeAgent.cs` (`OnRcon`, `SendRconMessageAsync`).
- **Tests:** `tests/Panel.Tests/PlayerTests.cs`, plus the seeding and permission lists.

- [ ] **Failing tests:**
  - `Start_sends_an_rcon_spec_only_with_BattlEye_on_and_generates_the_password_once`.
  - `The_password_never_appears_in_responses_audit_or_the_hub`.
  - `A_profile_port_colliding_with_another_profiles_ports_is_refused`.
  - `Players_hides_GUID_and_IP_without_players_manage_and_says_when_RCon_is_unavailable`.
  - `Kick_and_ban_recheck_the_GUID_and_refuse_a_changed_player`.
  - `Reasons_and_messages_are_cleaned_and_capped`.
  - `Unban_rechecks_the_ban_number_by_GUID`.
  - `Raw_commands_are_allowlisted`.
  - `The_rcon_log_keeps_500_lines_and_needs_players_manage`.
  - `New_rcon_password_changes_it_without_showing_it`.
- [ ] **Run:** they fail. **Implement** to Rulings 4 and 7–11. **Run:** they pass, and so does the full Panel suite.

### Task 4: Web

**Files:** `web/src/features/server/PlayersPage.tsx`, the server sub-nav, `ProfileGeneral` secrets ("New RCon password"), `lib/types.ts`, `lib/hub.ts` (`rcon` event).

- [ ] **Build** to Ruling 12: dialogs say exactly what they'll do; empty and unavailable states; no sideways scroll at 375 px. Then `npm run build`: exit 0.

### Task 5: Docs, review, live run, report

- [ ] **README and guide:** a Players / RCon section, and the RCon port (loopback, game port + 10, not opened in the firewall).
- [ ] **Final review** by a fresh Opus reviewer, then one test-first fix pass.
- [ ] **Agent:** republish and restart it (Global Constraints).
- [ ] **Live run** with a temporary dev profile:
  - RCon Connected; players (empty) and bans read;
  - say to everyone (seen in the RCon log);
  - an allowlisted and a refused command;
  - New RCon password, then start: still Connected;
  - Stop through `#shutdown`: "stopped", not "unexpectedly";
  - BattlEye off reads Off;
  - with the user's OK, someone joins for real rows, kick and ban;
  - pages at 1440 and 375 px;
  - cleanup.
- [ ] **Report:** the 20-point checklist, then `phase7a-report.md` with the rulings, deferred minors and a commit message.
