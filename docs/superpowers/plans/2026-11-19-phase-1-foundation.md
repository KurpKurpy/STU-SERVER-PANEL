# Phase 1: Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A working, self-hostable 13th Console website with invite-only accounts, roles and per-user permissions enforced by the backend, TOTP 2FA, an audit log, announcements, an honest dashboard, and Members and Settings screens in the chosen UNSC-terminal look.

**Architecture:**
- **Panel:** one ASP.NET Core (.NET 10) process serves the REST API and the built React SPA from `wwwroot`. SQLite is accessed through EF Core plus ASP.NET Core Identity.
- **Frontend:** React + TypeScript + Vite + Tailwind v4, reading the design tokens from `docs/design/tokens.css`.
- **Not in Phase 1:** no server agent and no SignalR. The dashboard's server card truthfully says no agent is connected.

**Tech Stack:**
- **Backend:** .NET 10 SDK, ASP.NET Core minimal APIs, EF Core Sqlite, ASP.NET Core Identity.
- **Tests:** xUnit and `Microsoft.AspNetCore.Mvc.Testing`.
- **Frontend:** React 19, react-router 7, @tanstack/react-query 5, Tailwind v4 (`@tailwindcss/vite`), lucide-react, @fontsource (saira, saira-condensed, share-tech-mono).

**Spec:**
- Design: `docs/superpowers/specs/2026-11-19-13th-console-design.md` (§5.2, §7, §8 Phase 1, §9, §10, §11 Phase 1).
- Source spec: `docs/superpowers/specs/2026-11-19-source-spec.md`.
- Tokens: `docs/design/tokens.css`.
- Mock-ups: https://claude.ai/artifact/Q78B5iibg8hmk8dx6i7XWy (board "Dashboard · A", "Members").

## Global Constraints

**Toolchain**
- .NET 10 (LTS) x64 SDK, `net10.0`, pinned in `global.json`. Node 24. Run `dotnet` from `C:\Program Files\dotnet\dotnet.exe` (PATH currently resolves to an x86 host with no SDKs).

**Data**
- SQLite through EF Core, with no SQLite-only SQL. Ids are `Guid`. Timestamps are UTC `DateTime`; EF's SQLite provider can't order `DateTimeOffset`.
- Data directory is config `DataDir`, default `<repo>/data` in Development. It holds `console.db`, `keys/` (Data Protection, DPAPI-protected on Windows) and `setup-token.txt`. `data/` and `src/Panel/wwwroot/` are gitignored.

**Accounts and auth**
- Invite-only: there is no public registration endpoint. Login is by username (the member's callsign); email is optional and never used.
- `MapIdentityApi` is not used: it exposes a public `/register` and treats email as the username.
- Passwords need at least 12 characters, with no composition rules. Lockout: 5 failed attempts lock the account for 15 minutes.
- Cookie is `HttpOnly`, `SameSite=Strict`, and `Secure` everywhere except Development.
- Every unsafe method (POST, PUT, PATCH, DELETE) under `/api` needs the header `X-Console: 1`, otherwise 400 `header_required`.
- Rate limit `auth`: 10 requests per minute per IP on `/api/auth/*`, `/api/setup` and `/api/invites/*`, answering 429. The limit is config `RateLimit:AuthPerMinute`.
- The security stamp is revalidated every config `Auth:StampValidationSeconds` seconds (default 60).
- 2FA (TOTP) is available to everyone. It's required for the roles in setting `twoFactorRequiredRoles`. Before any DB value exists, that setting reads config `Auth:TwoFactorRequiredRoles` (default `Owner,Server Administrator`).

**Permissions**
- Enforced on the backend on every request (design §7). The Owner role passes every check. The last Owner can't be demoted, disabled or removed: 409 `last_owner`.
- The catalog holds only Phase 1 permissions: `members.view`, `members.manage`, `roles.manage`, `settings.manage`, `audit.view`, `announcements.manage`, `server.view`.

**Nothing fake (design §2)**
- Navigation shows only Dashboard, Members (needs `members.view`) and Settings.
- The server card renders "No server agent connected" because `GET /api/servers` returns `[]`. No placeholder stats, no dead buttons.

**API conventions**
- Errors use the JSON body `{ "code": string, "message": string }`.
- Unknown `/api/*` routes return 404 JSON, never `index.html`.

**Frontend**
- Components use the semantic token classes from `tokens.css` (`bg-surface`, `text-holo-text`, `text-text-muted`…), never raw hex.
- The UI rules in design §9 apply: visible focus (inset ring on cut corners), at least 4.5:1 contrast, 44 px targets on `pointer: coarse`, 150 ms transitions, reduced motion respected, lucide SVG icons only, status always shown as a word, 16 px body text below `sm`, the z-scale.
- Native elements first: `<button>`, `<table>`, `<dialog>` (via `showModal()`), `<select>`, `<input type=checkbox>`. shadcn/Radix are added only when a native element falls short; Phase 1 needs none.
- `<html data-holo="red">` by default. Cyan comes only from `data-holo="cyan"`.

**Process**
- Commit steps apply only if the user has set up git and asked for commits; otherwise skip them.
- Never commit `data/`.

## Review Focus

1. **Callsign collisions:** inviting or accepting `" Skye "` when `skye` already exists should give a clear 409 `conflict_username`, not a second account or a 500. Test in Task 5.
2. **Stale invite links:** opening a used, revoked or expired invite days later should give a readable "this invite has expired / was already used" page, not a crash or a blank form. Test in Task 5.
3. **Disabled member with an open tab:** they're signed out on their next request once the stamp revalidates. Test in Task 5 (stamp interval 0 in the test).
4. **Owner locks themself out:** removing the Owner role from the only Owner, or disabling them, is refused with a message. Test in Task 5 (`Last_owner_cannot_be_disabled_or_demoted` covers both PATCH shapes).
5. **Refreshing a deep link** (`/members/…`) returns the app, while a typo'd API path returns JSON 404. Test in Task 1.

---

## File map

```
13th_console/
  global.json  13th_console.slnx  .gitignore  README.md
  src/Panel/Panel.csproj  Program.cs  appsettings.json  appsettings.Development.json  Properties/launchSettings.json
  src/Panel/Data/        AppDb.cs  Entities.cs  Migrations/
  src/Panel/Auth/        Permissions.cs  PermissionService.cs  PermissionAuthorization.cs  Seeder.cs  SetupState.cs  TwoFactorGate.cs
  src/Panel/Audit/       AuditLog.cs
  src/Panel/Settings/    AppSettingsService.cs
  src/Panel/Api/         ApiError.cs
  src/Panel/Features/    Auth/AuthEndpoints.cs  Account/AccountEndpoints.cs  Members/MemberEndpoints.cs  Invites/InviteEndpoints.cs
                         Roles/RoleEndpoints.cs  Announcements/AnnouncementEndpoints.cs  Activity/ActivityEndpoints.cs
                         Settings/SettingsEndpoints.cs  Servers/ServerEndpoints.cs
  tests/Panel.Tests/     Panel.Tests.csproj  PanelFactory.cs  Totp.cs  <Area>Tests.cs
  web/                   package.json  vite.config.ts  index.html  tsconfig*.json
  web/src/               main.tsx  App.tsx  styles/app.css  styles/tokens.css  lib/api.ts  lib/auth.tsx  lib/types.ts
  web/src/components/    AppShell.tsx  Sidebar.tsx  TitleBar.tsx  Panel.tsx  Button.tsx  StatusBlock.tsx  RoleChip.tsx
                         Field.tsx  Dialog.tsx  SubNav.tsx  CopyField.tsx
  web/src/features/      auth/*Page.tsx  dashboard/DashboardPage.tsx  members/*.tsx  settings/SettingsPage.tsx
```

---

### Task 1: Solution skeleton, SPA hosting and API 404

**Files:**
- Create: `global.json`, `13th_console.slnx`, `.gitignore`, `src/Panel/Panel.csproj`, `src/Panel/Program.cs`, `src/Panel/appsettings*.json`, `src/Panel/Properties/launchSettings.json`, `src/Panel/Api/ApiError.cs`
- Test: `tests/Panel.Tests/Panel.Tests.csproj`, `tests/Panel.Tests/PanelFactory.cs`, `tests/Panel.Tests/HostingTests.cs`

**Interfaces:**
- Produces:
  - `public partial class Program` (for `WebApplicationFactory<Program>`).
  - `static class ApiError { IResult Of(int status, string code, string message); }`.
  - `PanelFactory : WebApplicationFactory<Program>` with ctor options `(string? twoFactorRequiredRoles = "", int stampSeconds = 0)`.
    - Each instance uses its own temp `DataDir` and a temp `WebRootPath` containing `index.html` with the text `13th Console`.
    - Its `CreateClient()` adds the default header `X-Console: 1`.
  - Panel listens on `http://localhost:5080` in Development.

- [ ] **Step 1: Prerequisite.** The .NET 10 x64 SDK is installed (user-approved). Verify that `"C:\Program Files\dotnet\dotnet.exe" --list-sdks` lists `10.0.x`.
- [ ] **Step 2: Create** `global.json` (`{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`) and the projects:
  - `dotnet new web -o src/Panel -f net10.0`
  - `dotnet new xunit -o tests/Panel.Tests -f net10.0`, then reference Panel and add `Microsoft.AspNetCore.Mvc.Testing`
  - `dotnet new sln --format slnx`, then add both projects
  - `.gitignore` covers `bin/ obj/ data/ src/Panel/wwwroot/ node_modules/ .superdesign/tmp/`
- [ ] **Step 3: Write the failing tests** in `HostingTests.cs`:

```csharp
[Fact] public async Task Unknown_api_route_returns_json_404()
{ var r = await new PanelFactory().CreateClient().GetAsync("/api/nope");
  Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
  Assert.Equal("not_found", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); }

[Fact] public async Task Deep_link_returns_spa_index()
{ var r = await new PanelFactory().CreateClient().GetAsync("/members/3f1c");
  Assert.Equal(HttpStatusCode.OK, r.StatusCode);
  Assert.Contains("13th Console", await r.Content.ReadAsStringAsync()); }
```

- [ ] **Step 4:** Run `dotnet test`. Expected: both FAIL.
- [ ] **Step 5: Implement `Program.cs` hosting:**
  - resolve `DataDir` and create it;
  - `UseDefaultFiles`, `UseStaticFiles`;
  - `MapFallback("/api/{**rest}", () => ApiError.Of(404, "not_found", "No such API route"))`;
  - `MapFallbackToFile("index.html")`;
  - declare `public partial class Program {}`.
- [ ] **Step 6:** Run `dotnet test`. Expected: PASS.
- [ ] **Step 7:** Commit (if enabled): `chore: solution skeleton and SPA hosting`.

---

### Task 2: Data model, Identity, permission catalog and role seeding

**Files:**
- Create: `src/Panel/Data/AppDb.cs`, `src/Panel/Data/Entities.cs`, `src/Panel/Data/Migrations/*` (generated), `src/Panel/Auth/Permissions.cs`, `src/Panel/Auth/Seeder.cs`
- Modify: `src/Panel/Program.cs` (DbContext, Identity, Data Protection, migrate and seed at startup)
- Test: `tests/Panel.Tests/SeedingTests.cs`

**Interfaces:**
- Produces:
  - `User : IdentityUser<Guid>` adds `DisplayName` (required), `Rank?`, `Section?`, `CreatedAt`, `LastLoginAt?`.
  - `Role : IdentityRole<Guid>` adds `Description?`, `IsSystem`.
  - `Invite { Guid Id; string TokenHash; Guid RoleId; Guid CreatedById; DateTime CreatedAt; DateTime ExpiresAt; DateTime? UsedAt; Guid? UsedById; DateTime? RevokedAt }`
  - `AuditEntry { Guid Id; DateTime At; Guid? ActorId; string Action; string? TargetType; string? TargetId; string Summary; string? DetailsJson; string? Ip; bool IsPublic }`, indexed on `At`.
  - `Announcement { Guid Id; string Title; string Body; bool Pinned; Guid CreatedById; DateTime CreatedAt; DateTime? UpdatedAt }`. `Body` is plain text in Phase 1, rendered with `white-space: pre-line`.
  - `AppSetting { string Key (PK); string Value }`.
  - `AppDb : IdentityDbContext<User, Role, Guid>` with `DbSet`s `Invites`, `Audit`, `Announcements`, `Settings`.
  - `Permissions`:
    - `record PermissionDef(string Key, string Group, string Label)`;
    - `static IReadOnlyList<PermissionDef> All`;
    - `const string` fields `MembersView = "members.view"`, `MembersManage`, `RolesManage`, `SettingsManage`, `AuditView`, `AnnouncementsManage`, `ServerView`;
    - `static IReadOnlyDictionary<string, string[]> SystemRoleDefaults`.
  - `SystemRoleDefaults` (role → default permissions):
    - `Owner` → none (bypass)
    - `Server Administrator` → `members.view`, `audit.view`, `announcements.manage`, `server.view`
    - `Mod Manager` → `members.view`, `server.view`
    - `Mission Maker` → `members.view`, `server.view`
    - `Zeus` → `members.view`, `server.view`
    - `Member` → `server.view`
  - Role claims use type `"perm"`; user overrides use `"perm.grant"` and `"perm.deny"`.
  - `Seeder.SeedAsync(IServiceProvider)`:
    - creates the six system roles (`IsSystem = true`);
    - grants each default permission once;
    - records granted keys in `AppSetting` `seededPermissions` (comma list), so an admin's later removal is never re-added.

- [ ] **Step 1: Write the failing tests** in `SeedingTests.cs`:
  - `System_roles_exist_with_default_permissions`: resolve `RoleManager<Role>`; assert all six roles exist and `Server Administrator` has exactly the four claims above.
  - `Seeding_again_does_not_restore_a_removed_permission`: remove `announcements.manage` from Server Administrator, call `Seeder.SeedAsync` again, assert it's still absent.
- [ ] **Step 2:** Run `dotnet test --filter SeedingTests`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the entities, `AppDb`, `Permissions` and `Seeder`, and register them in `Program.cs`:
  - `AddDbContext<AppDb>` using `Data Source={DataDir}/console.db`;
  - `AddIdentityCore<User>(...)` with the password, lockout and username options from Global Constraints (`AllowedUserNameCharacters` letters, digits and `-_.`), plus `.AddRoles<Role>().AddEntityFrameworkStores<AppDb>().AddSignInManager().AddDefaultTokenProviders()`;
  - `AddDataProtection().PersistKeysToFileSystem(DataDir/keys)`, adding `.ProtectKeysWithDpapi()` when `OperatingSystem.IsWindows()`;
  - at startup, `db.Database.MigrateAsync()` then `Seeder.SeedAsync`.
- [ ] **Step 4:** Run `dotnet ef migrations add Initial -p src/Panel -o Data/Migrations` (install `dotnet-ef` as a local tool if missing), then `dotnet test --filter SeedingTests`. Expected: PASS.
- [ ] **Step 5:** Commit (if enabled): `feat: data model, identity and role seeding`.

---

### Task 3: Owner bootstrap, login, session and request guards

**Files:**
- Create: `src/Panel/Auth/SetupState.cs`, `src/Panel/Audit/AuditLog.cs`, `src/Panel/Features/Auth/AuthEndpoints.cs`
- Modify: `src/Panel/Program.cs` (cookie auth, X-Console guard, rate limiter, stamp validation)
- Test: `tests/Panel.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: Task 2's `User`, `AppDb` and `Permissions`.
- Produces:
  - `SetupState` (singleton):
    - `bool Needed`;
    - `string? Issue(string dataDir)` writes `setup-token.txt` and returns the token: 32 random bytes as base64url, stored only as SHA-256;
    - `bool TryConsume(string token)` is atomic, so a second call returns false.
  - `AuditLog` (scoped): `Task RecordAsync(string action, string summary, string? targetType = null, string? targetId = null, object? details = null, bool isPublic = false)`. It takes the actor and IP from `IHttpContextAccessor`, then saves immediately.
  - Endpoints:
    - `GET /api/setup` → `SetupStatus(bool Needed)`
    - `POST /api/setup` takes `SetupRequest(string Token, string UserName, string DisplayName, string Password)` → 200 and signs in the new Owner, or 400 `setup_invalid`. It deletes the token file and audits `setup.completed`.
    - `POST /api/auth/login` takes `LoginRequest(string UserName, string Password)` → `LoginResult(string Status)`, where Status is one of `ok`, `twoFactor`, `lockedOut`, `disabled`, `invalid`. On `ok` it stamps `LastLoginAt`; failures audit `auth.failed`.
    - `POST /api/auth/logout` → 204.
    - `GET /api/me` → `MeDto(Guid Id, string UserName, string DisplayName, string? Rank, string? Section, string[] Roles, string[] Permissions, bool IsOwner, bool TwoFactorEnabled, bool MustEnrolTwoFactor)`. `Permissions` is filled in Task 4; return `[]` until then.
  - Cookie events: redirect-to-login becomes 401 and access-denied becomes 403 (no redirects for `/api`).
  - Usernames are trimmed before every create, lookup and login.

- [ ] **Step 1: Write the failing tests** in `AuthTests.cs`:
  - `Setup_creates_owner_once`: `GET /api/setup` gives `Needed == true`. A POST with the token read from `{DataDir}/setup-token.txt` → 200 and `/api/me` shows `IsOwner`. A second POST with the same token → 400 `setup_invalid`, and `Needed == false`.
  - `Setup_with_wrong_token_is_rejected` → 400.
  - `Login_ok_then_me_returns_user`, and `Me_without_session_is_401`.
  - `Unsafe_api_request_without_header_is_400`: a POST with a client that has no default header gives `code == "header_required"`.
  - `Five_bad_passwords_lock_the_account`: five wrong logins, then the correct password returns `Status == "lockedOut"`.
- [ ] **Step 2:** Run `dotnet test --filter AuthTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `SetupState`, `AuditLog` and `AuthEndpoints`, and wire `Program.cs`:
  - `AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()` with the cookie settings and 401/403 events;
  - `Configure<SecurityStampValidatorOptions>` with the configured interval;
  - `AddRateLimiter` with the IP-partitioned fixed-window policy `auth`, applied to the auth, setup and invite groups;
  - inline middleware that rejects unsafe `/api` requests without `X-Console: 1`;
  - at startup, log the setup token when `Needed`.
- [ ] **Step 4:** Run `dotnet test --filter AuthTests`. Expected: PASS.
- [ ] **Step 5:** Commit (if enabled): `feat: owner bootstrap, login and request guards`.

---

### Task 4: Permission enforcement and roles API

**Files:**
- Create: `src/Panel/Auth/PermissionService.cs`, `src/Panel/Auth/PermissionAuthorization.cs`, `src/Panel/Features/Roles/RoleEndpoints.cs`
- Modify: `src/Panel/Features/Auth/AuthEndpoints.cs` (`MeDto.Permissions`)
- Test: `tests/Panel.Tests/PermissionTests.cs`, `tests/Panel.Tests/TestUsers.cs`

**Interfaces:**
- Consumes: Task 2's claim types and `Permissions`; Task 3's `/api/me` and login.
- Produces:
  - `PermissionService` (scoped):
    - `Task<IReadOnlySet<string>> EffectiveAsync(Guid userId)`, computed as (union of role `perm` claims ∪ `perm.grant`) − `perm.deny`; Owner gets all of `Permissions.All`. Cached per request in `HttpContext.Items`.
    - `Task<bool> IsOwnerAsync(Guid userId)`.
    - `Task<int> OwnerCountAsync()`.
  - `RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder b, string permission)` adds a `PermissionRequirement` checked by `PermissionHandler` through `PermissionService`. Also apply it to `RouteGroupBuilder`.
  - Endpoints:
    - `GET /api/permissions` (authenticated) → `PermissionDto(string Key, string Group, string Label)[]`
    - `GET /api/roles` (`members.view`) → `RoleDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsOwner, string[] Permissions, int MemberCount)[]`
    - `POST /api/roles` and `PUT /api/roles/{id}` (`roles.manage`) take `RoleUpsert(string Name, string? Description, string[] Permissions)`. Unknown keys → 400 `validation`. The Owner role → 400 `owner_role_fixed`.
    - `DELETE /api/roles/{id}` (`roles.manage`): system roles → 400 `system_role`; otherwise remove it from all members first. Every change is audited (`role.*`).
  - Test helper `TestUsers.LoginAsync(PanelFactory f, string role, string userName = "u-<guid>")` → a signed-in `HttpClient`. It creates the user via `UserManager` in a scope with password `correct horse battery`.

- [ ] **Step 1: Write the failing tests** in `PermissionTests.cs`:
  - Theory `Seeded_roles_get_expected_access` over the rows below (`GET /api/roles` needs `members.view`, `POST /api/roles` needs `roles.manage`):

    | Role | `GET /api/roles` | `POST /api/roles` |
    |---|---|---|
    | Member | 403 | 403 |
    | Zeus | 200 | 403 |
    | Mission Maker | 200 | 403 |
    | Mod Manager | 200 | 403 |
    | Server Administrator | 200 | 403 |
    | Owner | 200 | 200 |

  - `Grant_override_adds_permission`: add the user claim `perm.grant` = `roles.manage` to a Member; now `POST /api/roles` → 200.
  - `Deny_override_removes_permission`: add `perm.deny` = `members.view` to a Zeus; now `GET /api/roles` → 403.
  - `Me_lists_effective_permissions`: a Zeus gets `["members.view","server.view"]`.
  - `Owner_role_permissions_cannot_be_edited` → 400 `owner_role_fixed`.
- [ ] **Step 2:** Run `dotnet test --filter PermissionTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `PermissionService`, `PermissionAuthorization` and `RoleEndpoints`, and fill `MeDto.Permissions`.
- [ ] **Step 4:** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5:** Commit (if enabled): `feat: backend permission enforcement and roles API`.

---

### Task 5: Members, invites, reset links and the last-owner guard

**Files:**
- Create: `src/Panel/Features/Members/MemberEndpoints.cs`, `src/Panel/Features/Invites/InviteEndpoints.cs`
- Modify: `src/Panel/Features/Auth/AuthEndpoints.cs` (`POST /api/auth/reset`)
- Test: `tests/Panel.Tests/MemberTests.cs`, `tests/Panel.Tests/InviteTests.cs`

**Interfaces:**
- Consumes: `PermissionService.IsOwnerAsync` and `OwnerCountAsync` (Task 4), `AuditLog` (Task 3).
- Produces members endpoints:
  - `GET /api/members` (`members.view`) → `MemberDto(Guid Id, string UserName, string DisplayName, string? Rank, string? Section, string[] Roles, bool TwoFactorEnabled, DateTime? LastLoginAt, bool Disabled)[]`
  - `GET /api/members/{id}` (`members.view`) → `MemberDetailDto(MemberDto Member, string[] Grants, string[] Denies, string[] Effective)`
  - `PATCH /api/members/{id}` (`members.manage`) takes `MemberPatch(string? DisplayName, string? Rank, string? Section, string[]? Roles, string[]? Grants, string[]? Denies, bool? Disabled)`:
    - `Disabled: true` sets `LockoutEnd = DateTimeOffset.MaxValue` and rotates the security stamp.
    - Removing Owner from, or disabling, the last Owner → 409 `last_owner`.
    - Audits `member.updated` or `member.disabled`.
  - `POST /api/members/{id}/reset-link` (`members.manage`) → `ResetLink(string Url, DateTime ExpiresAt)`, where `Url` is `{origin}/reset?u={id}&t={base64url token}`.
  - `POST /api/auth/reset` takes `ResetRequest(Guid UserId, string Token, string NewPassword)` → 204, or 400 `reset_invalid`.
- Produces invites endpoints:
  - `POST /api/invites` (`members.manage`) takes `InviteCreate(Guid RoleId, int? ExpiresHours)`; the default is 72 h, clamped to 1–168. Returns `InviteCreated(Guid Id, string Url, DateTime ExpiresAt)`, where `Url` is `{origin}/invite/{token}`. Audits `member.invited`.
  - `GET /api/invites` (`members.manage`) → pending `InviteDto(Guid Id, string RoleName, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt)[]`.
  - `DELETE /api/invites/{id}` (`members.manage`) revokes the invite.
  - `GET /api/invites/{token}` (anonymous) → `InviteInfo(bool Valid, string? Reason, string? RoleName, DateTime? ExpiresAt)`, with Reason one of `expired`, `used`, `revoked`, `unknown`.
  - `POST /api/invites/{token}/accept` (anonymous) takes `InviteAccept(string UserName, string DisplayName, string Password)` and creates the user with the invite's role. Returns:
    - 200, signs the new member in, marks the invite used, and audits `member.joined` with `isPublic: true`;
    - 409 `conflict_username`;
    - 410 `invite_invalid`;
    - 400 `validation` with Identity's password messages.

- [ ] **Step 1: Write the failing tests:**
  - `Invite_accept_creates_member_with_role`.
  - `Invite_cannot_be_used_twice`: the second accept → 410, and `GET` returns `Reason == "used"`.
  - `Expired_invite_reports_reason`: set `ExpiresAt` in the past through `AppDb`; `GET` gives `Valid == false, Reason == "expired"`.
  - `Revoked_invite_reports_reason`.
  - `Callsign_collision_is_409`: an existing `skye`; accepting `" Skye "` → 409 `conflict_username`, and the member count is unchanged.
  - `Disabled_member_is_signed_out_on_next_request`, using a factory with `stampSeconds: 0`: the member's client gets `/api/me` 200, the Owner PATCHes `Disabled: true`, then the member's `/api/me` → 401 and login returns `disabled`.
  - `Last_owner_cannot_be_disabled_or_demoted` → 409 `last_owner` for both PATCH shapes.
  - `Reset_link_sets_new_password`: generate the link, parse `u`/`t`, POST the reset, and log in with the new password.
- [ ] **Step 2:** Run `dotnet test --filter "MemberTests|InviteTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the endpoints. Invite tokens are 32 random bytes as base64url; store only their SHA-256 and compare hashes.
- [ ] **Step 4:** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5:** Commit (if enabled): `feat: members, invites and reset links`.

---

### Task 6: Two-factor authentication and the enforcement gate

**Files:**
- Create: `src/Panel/Auth/TwoFactorGate.cs`, `src/Panel/Features/Account/AccountEndpoints.cs`, `src/Panel/Settings/AppSettingsService.cs`
- Modify: `src/Panel/Features/Auth/AuthEndpoints.cs` (`POST /api/auth/2fa`, `MeDto.MustEnrolTwoFactor`)
- Test: `tests/Panel.Tests/Totp.cs`, `tests/Panel.Tests/TwoFactorTests.cs`

**Interfaces:**
- Produces:
  - `AppSettingsService` (scoped):
    - `Task<string> AppNameAsync()`, default `"13th Console"`;
    - `Task<string[]> TwoFactorRequiredRolesAsync()`, from the DB or else config;
    - `Task SetAsync(string key, string value)`.
  - Account and auth endpoints:
    - `GET /api/account/2fa` → `TwoFactorSetup(string SharedKey, string AuthenticatorUri)`. It creates the key if missing; the URI is `otpauth://totp/13th%20Console:{userName}?secret={key}&issuer=13th%20Console&digits=6`.
    - `POST /api/account/2fa` takes `TwoFactorEnable(string Code)` → `RecoveryCodes(string[] Codes)` (10 codes), or 400 `code_invalid`. Audits `auth.2fa.enabled`.
    - `POST /api/account/password` takes `PasswordChange(string Current, string New)` → 204, or 400 `validation`.
    - `POST /api/auth/2fa` takes `TwoFactorLoginRequest(string? Code, string? RecoveryCode)` → `LoginResult`.
  - `TwoFactorGate` middleware:
    - When the signed-in user holds a required role and `TwoFactorEnabled` is false, every `/api` path except `/api/me`, `/api/auth/*` and `/api/account/*` returns 403 `2fa_enrolment_required`.
    - `MeDto.MustEnrolTwoFactor` reports the same condition.
  - Test helper `Totp.Code(string base32Key, DateTime utcNow)` implements RFC 6238 (HMAC-SHA1, 30 s step, 6 digits) with a small base32 decoder.

- [ ] **Step 1: Write the failing tests** (factory `twoFactorRequiredRoles: "Owner,Server Administrator"`):
  - `Required_role_is_gated_until_enrolled`:
    - a Server Administrator gets `GET /api/members` → 403 `2fa_enrolment_required`, and `/api/me` shows `MustEnrolTwoFactor`;
    - `GET /api/account/2fa`, then POST `Totp.Code(key, UtcNow)` → 10 recovery codes;
    - now `GET /api/members` → 200.
  - `Enrolled_user_login_requires_code`: login → `twoFactor`, `POST /api/auth/2fa` with a TOTP code → `ok`.
  - `Recovery_code_works_once`.
  - `Wrong_code_is_rejected` → 400 `code_invalid`.
- [ ] **Step 2:** Run `dotnet test --filter TwoFactorTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Use `UserManager.ResetAuthenticatorKeyAsync`, `VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)`, `GenerateNewTwoFactorRecoveryCodesAsync`, `SignInManager.TwoFactorAuthenticatorSignInAsync` and `TwoFactorRecoveryCodeSignInAsync`.
- [ ] **Step 4:** Run `dotnet test`. Expected: all PASS. Earlier tests use `twoFactorRequiredRoles: ""`.
- [ ] **Step 5:** Commit (if enabled): `feat: TOTP two-factor with role enforcement`.

---

### Task 7: Announcements, activity, audit log, settings and the servers stub

**Files:**
- Create: `src/Panel/Features/Announcements/AnnouncementEndpoints.cs`, `src/Panel/Features/Activity/ActivityEndpoints.cs`, `src/Panel/Features/Settings/SettingsEndpoints.cs`, `src/Panel/Features/Servers/ServerEndpoints.cs`
- Test: `tests/Panel.Tests/ContentTests.cs`

**Interfaces:**
- Produces:
  - Announcements:
    - `GET /api/announcements` (authenticated) → `AnnouncementDto(Guid Id, string Title, string Body, bool Pinned, string Author, DateTime CreatedAt, DateTime? UpdatedAt)[]`, pinned first then newest, at most 20.
    - `POST`, `PUT /{id}` and `DELETE /{id}` (`announcements.manage`) take `AnnouncementUpsert(string Title, string Body, bool Pinned)`. Title is 1–120 chars and Body 1–4000.
    - Creating audits `announcement.created` (`isPublic: true`); updating and deleting are audited non-public.
  - Activity and audit:
    - `GET /api/activity?limit=20` (authenticated) → `ActivityDto(Guid Id, DateTime At, string Actor, string Action, string Summary)[]`, public entries only, newest first, limit at most 100.
    - `GET /api/audit?action=&actorId=&before=&limit=50` (`audit.view`) → `AuditDto(Guid Id, DateTime At, string? Actor, string Action, string? TargetType, string? TargetId, string Summary, string? DetailsJson, string? Ip)[]`, with keyset paging by `before`.
  - Settings:
    - `GET /api/settings/public` (anonymous) → `PublicSettings(string AppName)`.
    - `GET` and `PUT /api/settings` (`settings.manage`) → `SettingsDto(string AppName, string[] TwoFactorRequiredRoles)`. Unknown role names → 400 `validation`. Audits `settings.updated`.
  - `GET /api/servers` (`server.view`) → `ServerDto(Guid Id, string Name)[]`. It is always empty in Phase 1; Phase 2 fills it from agents.

- [ ] **Step 1: Write the failing tests:**
  - `Created_announcement_appears_in_activity`.
  - `Non_public_audit_is_not_in_activity`: a role update must not appear.
  - `Member_cannot_create_announcement` → 403.
  - `Audit_requires_permission`: Member → 403, Server Administrator → 200.
  - `Servers_is_empty_until_an_agent_exists` → `[]`.
  - `Settings_roundtrip_and_validation`.
  - `Timestamps_are_utc_iso`: the `At` string ends with `Z`.
- [ ] **Step 2:** Run `dotnet test --filter ContentTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the endpoints. Make sure `DateTime` values are written as UTC: read with `DateTimeKind.Utc` through an EF value converter on all `DateTime` properties in `AppDb.ConfigureConventions`.
- [ ] **Step 4:** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5:** Commit (if enabled): `feat: announcements, activity, audit, settings, servers stub`.

---

### Task 8: Web app scaffold, tokens and the shared shell

**Files:**
- Create: `web/*` (from the Vite `react-ts` template), `web/src/styles/app.css`, `web/src/styles/tokens.css` (a copy of `docs/design/tokens.css`), `web/src/lib/api.ts`, `web/src/lib/auth.tsx`, `web/src/lib/types.ts`, and `web/src/components/*` from the File map
- Modify: `web/vite.config.ts`, `web/index.html`

**Interfaces:**
- Consumes: the DTO shapes from Tasks 3–7. Mirror them as TypeScript types in `lib/types.ts`, with the same names and camelCase fields.
- Produces:
  - `api<T>(path: string, init?: RequestInit & { json?: unknown }): Promise<T>`:
    - uses same-origin credentials;
    - adds `X-Console: 1` on unsafe methods;
    - on a non-2xx it throws `ApiError { status: number; code: string; message: string }`.
  - `auth.tsx`:
    - `MeProvider` holds a TanStack query `['me']`; on a 401 it routes to `/login`; on `mustEnrolTwoFactor` it routes to `/account/2fa`.
    - `useMe(): MeDto | undefined`.
    - `useCan(): (perm: string) => boolean`.
    - `<RequirePermission perm>` shows a "You don't have access to this" panel; it never hides a broken page.
  - Components, all styled only with token classes:
    - `AppShell` (Sidebar + TitleBar + holo stripe + `<Outlet/>`, laid out as in the mock-up);
    - `Sidebar` (items shown by permission, `aria-current="page"`);
    - `TitleBar(title)`;
    - `Panel({ code, title, meta?, children })` with a `chamfer` frame and `tab-cut` header;
    - `Button({ variant: 'default'|'primary'|'danger', size?: 'sm', busy?: boolean })`, disabled with a busy label while `busy`;
    - `StatusBlock({ tone: 'ok'|'warn'|'bad'|'off', children })`;
    - `RoleChip`;
    - `Field({ label, error?, ...input })`;
    - `Dialog({ open, onClose, title, children })` wrapping `<dialog>` with `showModal()`;
    - `SubNav({ items })`;
    - `CopyField({ value })`, which announces "Copied" through `aria-live`.

- [ ] **Step 1: Scaffold.**
  - `npm create vite@latest web -- --template react-ts`, then `npm i react-router @tanstack/react-query lucide-react @fontsource/saira @fontsource/saira-condensed @fontsource/share-tech-mono` and `npm i -D tailwindcss @tailwindcss/vite`.
  - In `vite.config.ts`: the react and tailwind plugins, `server.proxy` sending `/api` to `http://localhost:5080`, and `build.outDir: '../src/Panel/wwwroot'` with `emptyOutDir: true`.
  - `app.css`: `@import "tailwindcss"; @import "./tokens.css";`, the font imports, `body` on `bg-bg text-text font-sans`, and the `holo-grid` utility on the page background.
  - `index.html`: `<html lang="en" data-holo="red">`, the viewport meta, and `<title>13th Console</title>`.
- [ ] **Step 2:** Write `api.ts`, `auth.tsx`, `types.ts` and the components.
- [ ] **Step 3: Verify** with `npm run build` in `web/`. Expected: it exits 0, `src/Panel/wwwroot/index.html` exists, and there are no TypeScript errors.
- [ ] **Step 4:** Commit (if enabled): `feat: web scaffold, tokens and shell components`.

---

### Task 9: Sign-in flows

**Files:**
- Create: `web/src/features/auth/SetupPage.tsx`, `LoginPage.tsx`, `TwoFactorLoginPage.tsx`, `InvitePage.tsx`, `ResetPage.tsx`, `EnrolTwoFactorPage.tsx`
- Modify: `web/src/App.tsx` (routes `/setup`, `/login`, `/login/2fa`, `/invite/:token`, `/reset`, `/account/2fa`; the app routes sit under `AppShell` inside `MeProvider`)
- Create: `.claude/launch.json` with entries `panel` (`dotnet run --project src/Panel`, port 5080) and `web` (`npm run dev --prefix web`, port 5173)

**Interfaces:**
- Consumes: the Task 3/5/6 endpoints, and Task 8's `api`, `Field`, `Button`, `Panel`.
- Produces:
  - `/setup` asks for the setup token, callsign, display name and password (it explains the token is in `data/setup-token.txt` or the panel console). If `GET /api/setup` gives `needed: false`, it redirects to `/login`.
  - `/login` handles every `LoginResult` status with its own message:
    - `lockedOut`: "Too many attempts. Try again in 15 minutes."
    - `disabled`: "This account is disabled. Ask an admin."
    - `invalid`: "Wrong callsign or password."
    - `twoFactor`: navigate to `/login/2fa`.
  - `/invite/:token` shows the role and expiry, or a readable reason for `expired`, `used`, `revoked` or `unknown`.
  - `/account/2fa` shows the shared key (grouped in 4s, with copy) and the `otpauth` link, then a code input. On success it shows the 10 recovery codes once, with a download-as-text button.

- [ ] **Step 1:** Implement the pages and routes.
- [ ] **Step 2: Verify in the browser.**
  - `preview_start panel` and `preview_start web`.
  - Read the token from `data/setup-token.txt` and complete `/setup`. Expect a redirect to `/account/2fa`, because Owner requires 2FA by default.
  - Enrol with a TOTP code. Compute it from the shown shared key with a throwaway RFC 6238 snippet (Python `hmac`/`hashlib`/`base64.b32decode`, 30 s step, 6 digits) kept in the session scratchpad, never in the repo.
  - Log out and log in again: the 2FA step appears.
  - Check `read_console_messages` for errors: expect none.
- [ ] **Step 3:** Commit (if enabled): `feat: sign-in, invite, reset and 2FA screens`.

---

### Task 10: Dashboard and Settings

**Files:**
- Create: `web/src/features/dashboard/DashboardPage.tsx`, `web/src/features/settings/SettingsPage.tsx`

**Interfaces:**
- Consumes: `GET /api/servers`, `/api/announcements`, `/api/activity`, `/api/settings`, `/api/settings/public`, `/api/account/password`, and `MeDto`.
- Produces dashboard (`/`), laid out as in mock-up board "Dashboard · A" minus the Phase 2+ panels:
  - telemetry line `SYSTEM CHECK >>> {n} server agents connected`;
  - `13:SRV` panel: when `servers` is empty, `StatusBlock tone="off"` reading "No agent" and the text "No server agent connected. Server status and controls appear once the server agent is installed.";
  - `13:INFO // ANNOUNCEMENTS` with create/edit/delete in a `Dialog` (shown only with `announcements.manage`);
  - `13:LOG // RECENT ACTIVITY`, refetching on window focus and every 60 s.
- Produces settings (`/settings`):
  - **My account:** change password, and 2FA status with a link to enrol;
  - **Unit settings** (`settings.manage`): app name, and 2FA-required roles as checkboxes of role names.

- [ ] **Step 1:** Implement both pages.
- [ ] **Step 2: Verify in the browser** as the Owner:
  - Create a pinned announcement: it appears in Announcements and in Recent activity.
  - The server card shows "No agent". There are no other server stats anywhere.
  - Change the app name: the title bar and the login page show it.
  - `resize_window` to the mobile preset: panels stack, no horizontal page scroll, body text 16 px. Then reset to desktop.
- [ ] **Step 3:** Commit (if enabled): `feat: dashboard and settings`.

---

### Task 11: Members area: roster, member detail, invites, roles and audit log

**Files:**
- Create: `web/src/features/members/MembersPage.tsx`, `MemberDetail.tsx`, `InvitesPage.tsx`, `RolesPage.tsx`, `AuditPage.tsx`

**Interfaces:**
- Consumes: the Task 4/5/7 endpoints.
- Produces routes under `/members`, with a `SubNav` of Members, Roles, Invites and Audit log (each tab shown only with its permission), laid out like mock-up board "Members":
  - **Roster:** a table of rank, callsign, section, role chips, 2FA status block and last seen. Selecting a row opens `MemberDetail` in the right column.
  - **MemberDetail:**
    - edit display name, rank and section;
    - roles as checkboxes;
    - per-permission overrides as a three-state control: inherit / grant / deny;
    - "Can do" list from `Effective`;
    - reset-password link (`CopyField`) and Disable/Enable, each confirmed in a `Dialog`.
    - The 409 `last_owner` message is shown inline: "The unit needs at least one Owner."
  - **Invites:** create (role select, expiry), show the link once in a `CopyField` with "Paste it into a Discord DM. No email is sent.", list pending invites, revoke.
  - **Roles:** list with member counts; create, edit and delete custom roles with a permission checklist grouped by `PermissionDto.Group`. System roles can be edited but not deleted; the Owner role is read-only.
  - **Audit log:** a table filterable by action, with "Load older" paging.

- [ ] **Step 1:** Implement the pages.
- [ ] **Step 2: Verify in the browser:**
  - As Owner, create an invite for role Zeus and open the link in a new tab while signed out. Accept as callsign `Hawk`: you're signed in, and the nav shows Dashboard, Members and Settings.
  - As Owner, grant Hawk `announcements.manage`. Hawk's dashboard shows "+ New" after a refetch.
  - Try to disable the only Owner: the inline last-owner message appears.
  - Tab through the roster and the dialog with the keyboard only: focus is visible everywhere, `Esc` closes the dialog, and focus returns to the trigger.
- [ ] **Step 3:** Commit (if enabled): `feat: members, invites, roles and audit screens`.

---

### Task 12: Publish, README and end-to-end check

**Files:**
- Create: `README.md`
- Modify: `src/Panel/appsettings.json` (production defaults: `DataDir` `data` next to the binary)

**Interfaces:**
- Produces README sections:
  - **Requirements:** .NET 10 SDK and Node 24.
  - **Develop:** the two `preview_start` entries, or `dotnet run` plus `npm run dev`.
  - **First run:** where the setup token is.
  - **Publish:** `npm run build --prefix web`, then `dotnet publish src/Panel -c Release -o publish`, then run `publish/Panel.exe`.
  - **Tests:** `dotnet test`.
  - **Note:** HTTPS and the public access route are decided in Phase 2.

- [ ] **Step 1:** Write the README and production `appsettings.json`.
- [ ] **Step 2:** Run `dotnet test`. Expected: all PASS, with the count reported.
- [ ] **Step 3: Publish and run.**
  - Run the publish commands, start `publish/Panel.exe` with `ASPNETCORE_URLS=http://localhost:5090` and `DataDir` set to a fresh temp folder.
  - Open `http://localhost:5090` in the browser pane and complete setup.
  - Reload `/members` to confirm the deep link works.
  - `read_network_requests` shows no 5xx.
  - Stop the process.
- [ ] **Step 4:** Commit (if enabled): `docs: README and publish check`.
