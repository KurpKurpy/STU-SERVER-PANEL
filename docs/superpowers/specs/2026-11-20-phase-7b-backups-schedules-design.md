# Phase 7b — Backups and schedules (design)

**Date:** 2026-11-20. **Size:** a project (two features, panel and agent). The head developer asked for the whole thing proposed rather than question by question, so every call below is mine and marked as such; approval of this document approves them.

## Intent

Take routine server care off people's hands and make losing data recoverable:

- **Backups** that cover what's actually precious, including the console's own data, which nothing backs up today.
- **Schedules** for the jobs an admin does by hand (restarts, updates, backups), which never touch an operation.

## What exists today

- **Safety copies before changes** go into the agent's `backups` folder: configs on Apply, mission PBOs on write, the keys folder before key changes, and file-manager edits and deletes.
  - They can be read (not restored) under Files → Backups.
  - Nothing ever prunes them.
- **The console's data has no backup:** `console.db` (members, roles, missions and every version's metadata, mod lists, operations, settings), `blobs/` (every uploaded mission file) and `keys/` (the encryption keys that unlock saved passwords).
- **Everything runs on one Windows box.** The panel and the agent are separate processes and may later run on separate machines.
- **There are no schedules.** Operation times are stored in UTC and shown in each viewer's local time; the console has no time zone of its own.
- **Already reusable:**
  - Control (start, stop, restart, with graceful RCon shutdown);
  - SteamCMD server update jobs;
  - Workshop "check for updates" and update-all;
  - RCon `say`;
  - 6b notifications.

## Goals

1. **Two backup types.** Each makes one zip.
   - **Console backup (panel side):**
     - a consistent copy of `console.db`, using SQLite's online backup while the panel runs;
     - `blobs/`;
     - `keys/`.
   - **Server backup (agent side):**
     - the agent's `profiles` (server profiles, persistence variables, BattlEye bans);
     - `configs`;
     - each install's `mpmissions` and `keys`.
     - *My call:* mods and the Arma install are left out. Workshop and SteamCMD re-download them, and they're tens of GB.
2. **Kinds:**
   - **manual** ("Back up now");
   - **scheduled**;
   - **pre-update:** a server backup is taken automatically before any server update or Workshop mod update starts, manual or scheduled.
   - The existing safety copies stay as they are and cover mission deploys and config applies (the design's "pre-deploy").
3. **Backups page** (`backups.manage`):
   - **The list:** type, kind, size, when, who, and status (running, done or failed).
   - **Back up now**, Download and Delete.
   - **Restore** for server backups.
   - **Settings:** how many to keep, and a "copy to" folder.
4. **Restore:**
   - **Server backup:** pick the parts (server profiles, configs, missions, keys).
     - Every server on that agent must be stopped.
     - The current files are saved as a fresh server backup first, then the parts are written back.
   - **Console backup:** not from the browser (*my call*). A command on the box, with the panel service stopped: `Panel.exe restore <zip>`. The deploy guide gets a "Restoring the console" section.
     - Why: the running panel can't safely replace the database it's using, and a bad browser restore could lock everyone out.
5. **Keeping it tidy:**
   - **Scheduled backups:** the newest 14 of each type are kept (a setting).
   - **Pre-update backups:** the newest 10.
   - **Manual backups:** kept until deleted.
   - **Safety copies** older than 30 days are deleted once a day (a setting; 0 = never).
6. **Copy elsewhere (optional):** a folder on each side, such as another drive, a network share, or a synced folder like OneDrive. Every finished zip is also copied there, and the same keep-counts apply there.
   - Why: a copy on the same disk protects against mistakes, not against losing the disk.
7. **Schedules page** (`schedules.manage`).
   - **Tasks:**
     - restart, stop or start a server (a profile);
     - update the server (an install, through SteamCMD);
     - update Workshop mods (check for updates, then update those with updates);
     - back up (console, server, or both).
   - **When:** chosen weekdays and a time, in the **unit's time zone**. That's a new setting; *my call:* default `Australia/Sydney`. Change it if the unit runs on Brisbane time, which has no daylight saving.
   - **Each schedule:** on/off, next run shown, and **Run now**.
8. **Guards (keep clear of ops and players):**
   - **Operation window:**
     - A restart, stop, start or update is **skipped** if an operation starts within the next 2 hours or started within the last 4 hours. The history says which operation.
     - Backups still run, since they don't touch the server.
     - This is the source spec's "never update the op modpack during an event".
   - **Players online:**
     - Restarts and stops warn in game through RCon at 10, 5 and 1 minutes ("Server restart in 10 minutes") when players are on.
     - Updates are skipped if anyone is on.
   - **Updates need the server stopped:**
     - SteamCMD can't replace files a running server holds open.
     - An empty running server is stopped, updated, then started again; a stopped one stays stopped.
   - **Steam sign-in:** a scheduled server update that needs Steam sign-in (password or Steam Guard) fails with "Steam needs you to sign in; run one update by hand", rather than waiting forever.
9. **Run history:**
   - Each run records start, end, outcome (done, failed, skipped or missed) and a line of detail.
   - **Missed runs** (the panel was down at the time) are recorded as missed and not run late. *My call:* a surprise restart or update at an odd hour is worse than a skipped one.
   - **Notifications:** failed, skipped and missed runs notify `schedules.manage` holders through 6b, as a new event "Scheduled task didn't run".
10. **Dashboard:** the next three scheduled runs, for anyone with `server.view`.
11. **Permissions:** `backups.manage` and `schedules.manage` (both already named in the master design), for S1 and S4 by default.
    - Backups hold passwords and keys, so the list and downloads need `backups.manage`.
12. **Audit:**
    - `backup.created`, `backup.restored`, `backup.deleted`;
    - `schedule.created`, `schedule.updated`, `schedule.deleted`, `schedule.run`.

## Non-goals (not in 7b)

- Scheduled mission deployment and "maintenance windows" from the source spec. Start Mission and operations already cover deploying, and a maintenance window is a stop plus a start.
- Cron expressions (see Approaches).
- Backing up mods or the Arma install.
- Restoring the console from the browser.
- Restoring individual safety copies with a button. Missions and configs already roll back through versions and revisions; files can be copied back through Files.

## Anti-goals

- **A schedule ever restarting or updating during an operation.**
- **A backup that silently misses the encryption keys,** so a restored console can't read its saved passwords.
- **Backups filling the disk** (hence the keep-counts and pruning).

## Approaches

1. **When schedules run:**
   - **Weekdays plus a time, in the unit's time zone (chosen).** It covers every task listed and is simple to set in a form.
   - **Cron expressions:** rejected. They need a parser dependency and are easy to get wrong.
   - **"Every N hours":** rejected. Nothing on the list needs it.
2. **Console restore:**
   - **An offline command on the box (chosen).**
   - **In the browser, with the panel restarting itself:** rejected. Replacing the live database is risky, and a failure locks everyone out.
3. **Backup format:**
   - **One zip per backup (chosen).** A single file to download, copy elsewhere and count.
   - **Folders like today's safety copies:** rejected for these. A folder is hard to download and copy as one piece.
4. **Who backs up what:**
   - **Each side backs up its own data (chosen).** The panel never touches server folders directly, and they may be on different machines.
5. **Running schedules:**
   - **A panel background service (chosen).** It ticks every 30 seconds and runs due schedules through the same code paths as the buttons. It reuses the 6b reminder pattern: claim each run once, so a double tick can't run it twice.

## Constraints

- The agent stays the only thing that touches server folders; the panel asks it, through new agent calls, to create, list, delete, copy and restore server backups. Zips are written to a temp file and moved into place.
- **Large transfers:** server backup downloads go through the existing agent-to-panel file transfer, not hub messages (1 MiB cap).
- **Time zones:** times are stored in UTC and converted with the unit time zone, so daylight saving is handled.
- **K:\\ stays read-only:** backups never write into a read-only install root; restore refuses to write there.
- **Concurrency:** one backup or restore at a time per side; a scheduled task never runs twice concurrently.

## What's left to the builder

- Exact page layouts, which follow the existing pages' patterns (list plus dialogs).
- Table and contract names, and the agent call shapes.
- How a failed half-way restore reports which parts were written. It must say so plainly, and the pre-restore backup is the way back.
