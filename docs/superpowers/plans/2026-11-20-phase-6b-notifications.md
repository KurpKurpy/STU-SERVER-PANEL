# Phase 6b — Notifications (in-app and Discord) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tell members what happened (operations announced, reminded, cancelled and ready; servers started and stopped; missions written; updates failed) in a bell inside the console, and, where the unit wants it, in a Discord channel through one webhook.

**Architecture:**
- **Hooks only enqueue.** Each event's existing code path, after its own save succeeds, puts one `NotificationEvent` on a bounded in-memory queue (`Notifier`, a singleton). It never waits on Discord or the database for it.
- **One worker does the rest** (`NotificationWorker`, a `BackgroundService`):
  - looks up the event's rule;
  - writes in-app rows for every enabled user who holds the event's permission;
  - then, separately, posts to Discord.
  - Each event is handled in its own try/catch, so nothing it does can stop the host.
- **Discord** is one webhook URL, stored encrypted and write-only. The sender:
  - posts only to Discord's own webhook host, never follows redirects and never logs the URL;
  - suppresses all mentions;
  - honours 429s with a capped wait;
  - stops using a webhook Discord says is gone.
- **Reminders** come from a second small `BackgroundService` that calls `OperationReminders.RunOnceAsync(now)` every minute.

**Tech Stack:** as Phase 6a: .NET 10 (10.0.15), EF Core Sqlite, ASP.NET Data Protection, `IHttpClientFactory`, React 19 + TanStack Query 5 + Tailwind v4, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-13th-console-design.md`. The parts that matter:
- §5 Secrets: "Discord webhook URLs are encrypted with ASP.NET Data Protection … The API only ever returns 'set' or 'not set' for them, and replacing one is write-only";
- §8 `NotificationRule(EventType, Discord, InApp)` and `UserNotification(Id, UserId, Text, Link, CreatedAt, ReadAt)`;
- §11 Phase 6, "Discord webhooks and in-app notifications".

Source spec:
- §6, the dashboard's "Notifications";
- §19, "allow automatic notifications before the scheduled start time";
- §20, Optional Discord integration: announce upcoming operations; mission approved; mission deployed; server starts; server goes offline; update fails; operation ready; "Allow administrators to choose which events generate notifications"; "Do not make Discord integration a requirement".

Discord's own docs, read for this plan (docs.discord.com, 20 Nov):
- the webhook URL is `https://discord.com/api/webhooks/{id}/{token}`;
- `content` is up to 2,000 characters;
- `{"allowed_mentions": {"parse": []}}` "completely suppress[es] all mentions";
- a webhook POST without `wait` returns 204;
- a 429 body has `retry_after` (seconds, float), plus a `Retry-After` header;
- "If a webhook returns a 404 status you should not attempt to use it again";
- 401/403/429 count toward Cloudflare's invalid-request ban;
- a `User-Agent` of the form `DiscordBot ($url, $versionNumber)` is required;
- timestamps are written `<t:UNIX:F>` (full date and time) and `<t:UNIX:R>` (relative), shown in each reader's own time zone.

## Global Constraints

- **.NET:** run it as `"C:\Program Files\dotnet\dotnet.exe"`; packages pinned at 10.0.15.
- **Panel tests** run with `--artifacts-path` (the preview holds `bin\Debug`); Agent tests run with the default output.
- **Git:** there's no git repository and the user handles git. Commit nothing; the ledger replaces commit steps.
- **Disks:** `K:\` is read-only and nothing is written there. The dev data root is `D:\13th_server_dev`.
- **Times** are stored in UTC. Notification text never contains a local time: in-app text names no time, and Discord text uses `<t:UNIX:…>`.
- **Discord is optional.** With no webhook set, everything else works and nothing tries to reach Discord.
- **Tests never reach Discord:** `PanelFactory` registers `FakeDiscord` for `IDiscordWebhook`.

## Review Focus

1. **The webhook URL leaking.**
   - **Cases:** the settings API, logs (`IHttpClientFactory`'s default handlers log the full request URL), audit details, the stored "last error", exception text, an unreadable stored value after a key change.
   - **Expected:** only "set / not set / not working" is ever shown, logs and audit never contain the token, and an unreadable value reads "not set".
2. **Discord hurting the panel.**
   - **Cases:** a slow, 429-ing, 404-ing or throwing sender; a flood of events.
   - **Expected:** no request waits on Discord, the worker keeps going after any failure, and the queue is bounded (drops are counted).
   - **Also:** waits are capped, a 404'd webhook is never used again until replaced, and in-app rows are written even when Discord fails.
3. **Repeats.**
   - **Cases:** the agent's hello re-sending a running server on reconnect; an Offline status with no open run; a manual item unticked and ticked again; two reminder scans; an edit that moves the start time; Apply after ready.
   - **Expected:** one notification per real event, a reminder re-armed only when `StartsAt` changes, and "ready" only once per edit and never after Apply.
4. **Per-user scope.**
   - **Expected:** a user lists and marks only their own rows (another user's id is 404); disabled users get nothing; recipients are worked out at creation from the permission that guards the linked page.
5. **What reaches Discord.**
   - **Expected:**
     - only names, mission, map and times — never mission notes, the briefing, join details, job or server error text;
     - no pings (mentions suppressed);
     - user text has its Markdown escaped, so a name can't add links or formatting;
     - at most 2,000 characters.

---

## Plan Rulings

1. **Scope.**
   - **Builds:**
     - in-app notifications (a bell with an unread count and the latest 30);
     - one Discord webhook;
     - per-event rules (in-app and Discord) and a reminder lead time;
     - a "Send a test message" button.
   - **Not built:**
     - a Discord bot;
     - more than one webhook;
     - per-user mutes;
     - email;
     - links to the console inside Discord messages (there's no public-address setting yet);
     - a "moved" event (a moved operation is re-reminded at its new time; Ruling 6).
2. **Events.** A fixed list in code (`NotificationEvents`), seeded once into `NotificationRule` through the `seededNotificationRules` setting, the way the checklist is. Rules for events this app can't produce aren't created: there's no mission approval step, so there's no "mission approved".

   | Type | When | In-app text (example) | Discord text (example) | Recipients hold | Link | Default in-app / Discord |
   |---|---|---|---|---|---|---|
   | `operation.created` | an operation is created | "New operation: Op Kestrel (helitest on VR)." | "**New operation:** Op Kestrel, <t:…:F> (<t:…:R>). helitest on VR." | `operations.view` | `/operations/{id}` | on / on |
   | `operation.reminder` | `ReminderMinutes` before a planned op starts | "Op Kestrel starts in 60 minutes." | "**Op Kestrel** starts <t:…:R> (<t:…:t>)." | `operations.view` | op | on / on |
   | `operation.cancelled` | a planned op is marked Cancelled | "Op Kestrel is cancelled." | "**Op Kestrel** (<t:…:F>) is cancelled." | `operations.view` | op | on / on |
   | `operation.ready` | Apply's gate first passes for an op not yet applied | "Op Kestrel is ready to apply." | "**Op Kestrel** is ready." | `operations.apply` | op | on / off |
   | `server.started` | a server run opens (new pid) | "Main Operations started." | "**Main Operations** started." | `server.view` | `/server?p={id}` | on / off |
   | `server.offline` | an open run closes | "Main Operations stopped." / "Main Operations went offline unexpectedly: {error}." | "**Main Operations** stopped." / "…went offline unexpectedly." | `server.view` | `/server?p={id}` | on / off |
   | `mission.deployed` | a deployment is Done | "helitest v3 is on Arma 3." | "**helitest** v3 is on the server." | `missions.view` | `/missions/{id}` | on / off |
   | `update.failed` | a Workshop download or server install/update job Fails | "A Workshop download failed: {job.Error}." | "A Workshop download failed." | the job's own `Permission` | `/mods` or `/server/setup` | on / off |
3. **Storage.**
   - **`NotificationRule`:** `EventType` (key, ≤40), `InApp`, `Discord`.
   - **`UserNotification`:** `Id`, `UserId` (FK to users, Cascade), `EventType` (≤40), `Text` (≤300), `Link` (≤200, nullable), `CreatedAt`, `ReadAt?`. Indexed on (`UserId`, `CreatedAt`).
     - Rows older than 60 days are deleted by the worker when it writes.
     - ponytail: the global prune runs on each write; move it to a daily pass if writes get heavy.
   - **`Operation`** gains `ReminderSentAt?` and `ReadyNotifiedAt?`.
   - **Settings:**
     - `discordWebhook`: the canonical URL, Data-Protection-protected with purpose `notifications.discord`;
     - `discordStatus`: JSON with `broken`, `lastError`, `lastErrorAt` and `lastSentAt`;
     - `operationReminderMinutes`: default 60; 0 turns reminders off; range 0–1440.
4. **Hooks only enqueue, after their own save.**
   - **The queue:** `Notifier.Enqueue(NotificationEvent)` uses a bounded `Channel` (capacity 1,000, `TryWrite`). A full queue drops the event and counts it in `Notifier.Dropped`, logged once per 100 drops.
   - **Recipients:** worked out by the worker when it handles the event, from `PermissionService.EffectiveAsync` over enabled users.
5. **Server events come from `AgentRegistry.RecordRunAsync`, only when a run actually opens or closes.**
   - **A new pid** opens a run → `server.started`.
   - **Offline or Error** closing at least one open run → `server.offline`. Error reads "went offline unexpectedly", plus the reason in-app only.
   - **A new pid over an open run** (the agent missed the stop) closes it silently and sends only `server.started`.
   - **An Offline status with no open run, or a status repeated by a reconnecting agent's hello,** opens and closes nothing and sends nothing.
   - **A Start Mission restart** that goes Offline then starts reads as two events, which is accurate.
6. **Reminders.**
   - **The scan:** `OperationReminders.RunOnceAsync(AppDb db, Notifier notifier, int minutes, DateTime now)` looks at planned operations with `ReminderSentAt == null` and `now < StartsAt <= now + minutes`.
   - **Claiming:** each one is claimed with a conditional `ExecuteUpdateAsync` (`… WHERE ReminderSentAt IS NULL`), which doesn't touch `Version`. Only a claim that changed one row enqueues.
   - **Re-arming:** a PUT that changes `StartsAt`, or a status change back to Planned, sets `ReminderSentAt = null`.
   - **Past starts:** an operation that already started is never reminded.
   - **The timer:** `ReminderService` runs the scan every 60 s.
7. **"Ready" fires on Apply's gate.** The gate: nothing Fail or Unknown in the stored result.
   - **When:** Prepare or a tick stores a result that passes the gate.
   - **Conditions:** the op is Planned, `AppliedAt == null` and `ReadyNotifiedAt == null`. The handler then sets `ReadyNotifiedAt` in the same save, and enqueues after it.
   - **Re-arming:** an edit (PUT) clears it, as it clears the stored readiness. Unticking and ticking again doesn't refire.
8. **Update failed:** Failed only, not Cancelled.
   - **Interrupted jobs** (the agent restarted) aren't notified: they're shown as Interrupted and may come back on the next hello.
   - **The text** carries the already-masked `job.Error`, and only in-app, never to Discord.
9. **Discord sender** (`IDiscordWebhook.SendAsync(string url, string content, CancellationToken ct) → DiscordResult`).
   - **The client:** a typed `HttpClient`, registered by `DiscordWebhook.Register(IServiceCollection)` (used by Program.cs and the logging test):
     - `.RemoveAllLoggers()`;
     - a primary `SocketsHttpHandler { AllowAutoRedirect = false }`;
     - a 15 s timeout;
     - `User-Agent: DiscordBot (13th Console, 6b)`.
   - **The body:** `{"content": <clipped to 2000>, "allowed_mentions": {"parse": []}}`, sent without `wait`.
   - **Results:**
     - 2xx → `Ok`.
     - 429 → wait `retry_after`, capped at 30 s, then retry. That makes at most 3 attempts, using the stopping token.
     - 5xx and network errors → retry after 2 s, then 5 s.
     - 401, 403 and 404 → `Gone`. The worker marks the webhook broken and skips Discord until a new one is pasted.
     - Any other code → `Failed("Discord refused the message (HTTP {code}).")`.
   - **Error text:** never exception text, only "Couldn't reach Discord." or "Discord didn't answer in time."
10. **The webhook URL.** `DiscordUrl.TryCanonical(string? input, out string canonical)` parses with `Uri`:
    - the scheme is https;
    - the host is `discord.com`, `discordapp.com`, `canary.discord.com` or `ptb.discord.com`;
    - the port is the default, and there's no user info, query or fragment;
    - the path is `/api[/vN]/webhooks/{17–20 digits}/{token: [A-Za-z0-9_-]{20,200}}`;
    - the result is `https://discord.com/api/webhooks/{id}/{token}`.

    It's checked again by the worker before every send. An unreadable stored value (Data Protection keys changed) reads as not set.
11. **API.**
    - **`/api/notifications`** (any signed-in user, own rows only):
      - `GET ?limit=30` returns `{ unread, items[] }`;
      - `POST /{id}/read` (404 for a row that isn't yours);
      - `POST /read-all`.
    - **`/api/notifications/settings`** (`settings.manage`):
      - `GET` returns `{ discord: { set, broken, lastError, lastErrorAt, lastSentAt }, reminderMinutes, rules[] }`;
      - `PUT` takes `{ reminderMinutes, rules[] }`, and refuses unknown types with 400.
    - **`PUT /api/notifications/discord`** takes `{ url }`. Empty clears it. An invalid URL is a field error.
    - **`POST /api/notifications/discord/test`** sends now and answers with the outcome.
    - **Audit:** `notifications.rules` and `notifications.discord` ("Set the Discord webhook" or "Removed the Discord webhook"), never the URL.
12. **What Discord text may carry:** operation, mission, profile and install names, the map, and times as `<t:…>`. Never notes, briefing, join details, or job and server error text.
    - **Escaping:** user-written names go through `DiscordText.Escape`, which backslash-escapes `\ * _ ~ ` | > # [ ] ( ) < @ :` and turns line breaks into spaces.
13. **Web.**
    - **The bell:** a bell in the title bar with the unread count, polled every 60 s and on focus.
      - The dashboard already polls every 5 s, so this adds no new keep-alive.
      - A popover lists the latest 30, read ones dimmed. Clicking one marks it read and opens its link. **Mark all read** clears the rest.
    - **Settings → Notifications** (`settings.manage`):
      - the Discord webhook through the existing write-only `SecretField`, with its status and **Send a test message**;
      - the rules table (event, In-app, Discord);
      - the reminder lead time.
14. **Live run.** The in-app part runs on the dev PC. The Discord part needs the user's OK at that moment, and the user pastes the webhook of a private test channel into the Settings field themselves, never into chat. Without that, the report says Discord delivery wasn't verified live.

## Interfaces (Task 1 produces, later tasks consume)

```csharp
public sealed record NotificationEvent(string Type, string Text, string? DiscordText, string? Link, string Permission);
public sealed class Notifier { public bool Enqueue(NotificationEvent e); public long Dropped { get; } }   // singleton
public static class NotificationEvents { public static readonly (string Type, string Label, bool InApp, bool Discord)[] All; }
public interface IDiscordWebhook { Task<DiscordResult> SendAsync(string url, string content, CancellationToken ct); }
public sealed record DiscordResult(DiscordOutcome Outcome, string? Error); public enum DiscordOutcome { Ok, Gone, Failed }
public static class DiscordUrl { public static bool TryCanonical(string? input, out string canonical); }
public static class DiscordText { public static string Escape(string s); public static string Time(DateTime utc, char style); }
public static class OperationReminders { public static Task<int> RunOnceAsync(AppDb db, Notifier notifier, int minutes, DateTime now); }
```

---

### Task 1: Storage, rules, the feed, and the in-app worker

**Files:**
- Create: `src/Panel/Data/NotificationEntities.cs`, `src/Panel/Features/Notifications/{NotificationEvents,Notifier,NotificationWorker,NotificationEndpoints}.cs`, migration `Phase6bNotifications`
- Modify: `AppDb.cs` (sets, keys, index, FK), `Seeder.cs` (rules seeded once), `Program.cs`, `Operation` (two fields)
- Test: `tests/Panel.Tests/NotificationTests.cs`

- [ ] **Step 1: Failing tests.**
  - `Rules_are_seeded_once_and_admin_changes_stay`: 8 types with the defaults in Ruling 2; a changed rule survives a restart.
  - `An_event_reaches_every_enabled_holder_of_its_permission_and_nobody_else`: enqueue `operation.created` with `operations.view`. Owner, Hawk (Zeus) and a Member get a row; a disabled user doesn't.
  - `A_rule_with_in_app_off_writes_no_rows`.
  - `Users_list_and_mark_only_their_own_rows`: B's `POST /{A's id}/read` is 404; read-all marks only B's; `unread` counts.
  - `A_full_queue_drops_and_counts`: a `Notifier` built with capacity 2 takes 2 and refuses the third; `Dropped == 1`.
- [ ] **Step 2: Run them.** Expected: FAIL (404 or missing types).
- [ ] **Step 3: Implement** to Rulings 2–4 and 11 (feed half).
  - The worker resolves the rule and recipients inside `try/catch` per event, and logs the type, never the text.
  - The `Notifier` constructor takes an optional capacity for the test.
- [ ] **Step 4: Run.** Expected: PASS. Panel suite green.

### Task 2: Discord

**Files:**
- Create: `src/Panel/Features/Notifications/{DiscordWebhook,DiscordUrl,DiscordText,NotificationSettingsEndpoints}.cs`
- Modify: `NotificationWorker.cs` (Discord half), `Program.cs`
- Test: `tests/Panel.Tests/DiscordTests.cs`, `FakeDiscord.cs`, `PanelFactory.cs` (register `FakeDiscord`)

- [ ] **Step 1: Failing tests.**
  - `Only_Discord_webhook_urls_are_accepted_and_stored_canonically`:
    - accepted: `https://discord.com/api/webhooks/1234567890123456789/abc_DEF-…`, `canary.` and `ptb.` hosts, `discordapp.com`, `/api/v10/` → canonical;
    - refused: http, another host, `discord.com.evil.net`, port 444, user info, a query, a fragment, a wrong path, an empty token.
  - `The_sender_posts_suppressed_mentions_and_clips_to_2000` (stub handler): the body's `allowed_mentions.parse` is empty and `content.Length == 2000`.
  - `A_429_waits_retry_after_then_retries_and_gives_up_after_3`.
  - `A_redirect_is_not_followed`: the stub answers 301 with a Location; exactly one request, and the result is Failed.
  - `A_404_is_Gone`.
  - `The_sender_never_logs_the_token`: `Register` plus a capturing logger provider; no log line contains the token.
  - `Settings_never_return_the_url_and_audit_never_records_it`: PUT, GET, the audit rows, and the in-app text all lack the token.
  - `An_unreadable_stored_webhook_reads_as_not_set`: overwrite the setting with garbage.
  - `Events_reach_Discord_only_when_the_rule_says_so_and_a_webhook_is_set` (`FakeDiscord`).
  - `A_gone_webhook_is_marked_broken_and_not_used_again_until_replaced`.
  - `A_throwing_sender_still_leaves_the_in_app_rows_and_the_worker_running`: the next event is still handled.
  - `Discord_text_escapes_names` (`DiscordText.Escape("[x](https://e) *b* @everyone")` has no unescaped `[`, `*` or `@`).
  - `The_test_message_reports_the_outcome`: 200 when Ok; 409 with "Discord says this webhook no longer exists" when Gone.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** to Rulings 9–12.
  - `RemoveAllLoggers()` must compile against 10.0.15; if it doesn't, register a plain `HttpClient` without the factory's logging handlers, and ledger it.
- [ ] **Step 4: Run.** Expected: PASS. Suite green.

### Task 3: The hooks and reminders

**Files:**
- Modify: `Hubs/AgentRegistry.cs` (inject `Notifier`), `Features/Missions/MissionDeployments.cs`, `Features/Operations/{OperationEndpoints,Readiness}.cs`, `Program.cs`
- Create: `Features/Notifications/{UpdateFailedNotifications (IJobResultHandler), OperationReminders, ReminderService}.cs`
- Test: `tests/Panel.Tests/NotificationHookTests.cs`

- [ ] **Step 1: Failing tests** (through the fake agent and API, waiting for rows):
  - `A_new_pid_is_server_started_once_even_when_hello_repeats_it`;
  - `Stopping_is_server_offline_and_a_crash_says_unexpectedly`;
  - `An_offline_status_with_no_open_run_notifies_nothing`;
  - `A_finished_deployment_notifies_mission_viewers`;
  - `A_failed_workshop_download_notifies_mods_managers_and_a_cancelled_one_nothing`;
  - `Creating_and_cancelling_an_operation_notifies_viewers`;
  - `Ready_fires_once_and_not_after_untick_and_tick_and_again_after_an_edit_and_never_after_Apply`;
  - `RunOnce_reminds_once_rearms_on_a_new_time_skips_started_and_cancelled_ops_and_keeps_Version`, which calls `OperationReminders.RunOnceAsync` with explicit `now` values.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** to Rulings 5–8: enqueue after each save, with texts as in Ruling 2.
- [ ] **Step 4: Run.** Expected: PASS. Full Panel suite green, and the Agent suite unchanged.

### Task 4: Web

**Files:**
- Create: `web/src/features/notifications/{NotificationBell,NotificationSettings}.tsx`
- Modify: `components/TitleBar.tsx`, `features/settings/SettingsPage.tsx`, `lib/types.ts`

- [ ] **Step 1: Bell and popover** to Ruling 13:
  - the button has `aria-expanded` and is labelled with the unread count;
  - Escape and an outside click close the popover;
  - each item is a link;
  - **Mark all read**;
  - the empty state reads "Nothing yet.";
  - on a phone, the popover is full-width under the title bar.
- [ ] **Step 2: Settings panel:**
  - the webhook `SecretField`, its status line (Set / Not set / Not working: {lastError}) and **Send a test message**;
  - the rules table with checkboxes;
  - the reminder minutes;
  - Save.
- [ ] **Step 3:** `npm run build`. Expected: exit 0.

### Task 5: Docs, review, live run, report

- [ ] **Docs:**
  - README status Phase 6b, with a Notifications section and the events table;
  - deploy guide: making a webhook in Discord (Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL) and pasting it in Settings.
- [ ] **Final review:** a fresh Opus reviewer, with `review-files.txt`, the Review Focus verbatim and the ledger's rulings. Then one test-first fix pass.
- [ ] **Live run, dev PC:**
  1. Create an operation; Owner and Hawk see it in the bell.
  2. Start and stop "Main Operations"'s dev profile or a temporary one (with the user's OK), and check started and offline.
  3. A mission upload is written, giving `mission.deployed`.
  4. Prepare to ready, giving `operation.ready` once.
  5. Set reminder minutes so the op falls inside it, giving a reminder within a minute.
  6. Discord only with the user's OK and their own pasted webhook (Ruling 14).
  7. Check the pages at 1440 and 375 px, and that the console is clean.
  8. Clean up.
- [ ] **Report:** the 20-point checklist, then `phase6b-report.md` in the notes folder, with every ruling, the deferred minors and a commit message.
