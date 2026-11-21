# Setting up 13th Console on the server

This takes the console from your PC to the unit's server (AMD EPYC 4344P, 64 GB, Windows Server 2025, 1 Gbit/s), so members can sign in from a browser and players can join the game.

When you're done:
- **Members** open `https://console.<your-domain>` in any browser, sign in, and see the server, profiles and the join password (if their role allows it).
- **Players** join the Arma 3 server at the box's public IP and the profile's game port (2302 in these examples; the 13th's server uses 2100).

Everything runs on the one box: the panel, the agent that starts Arma, Caddy for HTTPS, and the Arma 3 server itself.

## Read this first: three limits today

1. **This hasn't been run end to end on the box yet.** Each step was checked against the code, and the panel, the agent, SteamCMD, the Workshop downloads, mission uploads and deploys, and addon scans were run on the dev PC. Treat the first install as the test, and report anything that doesn't match.
2. **Stopping the server as a Windows service is unproven.** The agent asks Arma to close its window. That may not work from a service (Windows session 0); if not, every Stop waits 60 seconds and then kills the server. Step 24 checks it.
3. **SteamCMD under a Windows service is unproven.** The agent runs SteamCMD in a pseudo console, which worked from a signed-in session on the dev PC. Step 22 is the first time it runs from the service. If that job never shows the Steam sign-in box or any progress, report it and run the agent from a signed-in session for the install.

## Part A: build on your PC

In PowerShell, from the repository folder (`STU-SERVER-PANEL`):

1. Build the web pages into the panel:
   ```powershell
   npm run build --prefix web
   ```
2. Publish the panel. `--self-contained` bundles .NET, so the server needs nothing installed:
   ```powershell
   dotnet publish src/Panel -c Release -r win-x64 --self-contained -o publish\panel
   ```
3. Publish the agent:
   ```powershell
   dotnet publish src/Agent -c Release -r win-x64 --self-contained -o publish\agent
   ```
4. Copy `publish\panel` to the server as `C:\13thConsole\panel`, and `publish\agent` as `C:\13thConsole\agent`. Copy-paste over Remote Desktop works, or zip them first. Don't copy your PC's `data` folder: the server starts with a fresh database.

## The quick way: one script

`deploy\setup-server.ps1` does Parts C to F and Part H for you. You still do Part B (the domain), Part G (your account, in the browser) and Part I onwards (the server and mods, in the console).

1. Do Part B first: the console's address has to point at the box before the script can finish.
2. Copy `deploy\setup-server.ps1` into `C:\13thConsole` on the server, next to `panel` and `agent`.
3. Download Caddy for **Windows amd64** from `https://caddyserver.com/download` and save it as `C:\13thConsole\caddy\caddy.exe`.
4. Right-click the Start button, choose **Terminal (Admin)**, and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\13thConsole\setup-server.ps1 -GamePort 2100,2300,2400
   ```
   `-GamePort` lists each server's game port: here Ops (Friday and Sunday share 2100, since they never run together), Training and Dev. The firewall opens each one through port + 4. Leave it out for a single server on 2302. Space ports at least 100 apart: each server's RCon port is its game port + 10. Two profiles may use exactly the same port, but only one of them can run at a time; Start says which one to stop first. A partial overlap is refused when the profile is saved.
5. Answer its questions: the console's web address, and a new password for `arma-svc` (write it down).
6. It shows the setup code. Do Part G in the browser, then **Server → Setup → Add agent**, and paste the key into the script. Or press Enter to stop there, and run it again when you have the key.

It checks before every step, skips what's already done and deletes nothing, so running it again is safe. If the address doesn't point at the box yet, it sets everything else up, leaves Caddy stopped and asks you to run it again once it does. Add `-WhatIf` to see what it would do without changing anything.

## Part B: the domain

5. At your domain's DNS provider, add an **A record**: `console` → the server's public IPv4 address. Wait until `console.<your-domain>` resolves to that IP; `nslookup console.<your-domain>` shows it.

## Part C: the Arma 3 server folder

On the server, in PowerShell as Administrator:

6. Create the empty folder the server will be installed into. The console installs it later (step 22): the agent downloads SteamCMD from Valve by itself, into `C:\ArmaData\steamcmd`.
   ```powershell
   New-Item -ItemType Directory -Force C:\Arma3Server
   ```
7. Have a Steam account that owns Arma 3. The dedicated server (app 233780) and the Workshop mods need it: an anonymous sign-in fails with "No subscription". You'll type its password and Steam Guard code into the console once, in step 22.

## Part D: accounts and folder permissions

The agent runs Arma under its own Windows account, so it can only touch the Arma folders. The panel runs as the built-in Local Service account.

8. Create the agent's account. You'll be asked for a password; keep it for step 19:
   ```powershell
   New-LocalUser arma-svc -Password (Read-Host -AsSecureString "Password for arma-svc") -PasswordNeverExpires -UserMayNotChangePassword
   ```
9. Create the agent's data folder and the panel's data folder:
   ```powershell
   New-Item -ItemType Directory -Force C:\ArmaData, C:\13thConsole\panel\data
   ```
10. Lock `C:\13thConsole` to administrators and the services. The agent's key and the panel's database live there:
    ```powershell
    icacls C:\13thConsole /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
    ```
    ```powershell
    icacls C:\13thConsole\panel /grant "NT AUTHORITY\LOCAL SERVICE:(OI)(CI)RX"
    ```
    ```powershell
    icacls C:\13thConsole\panel\data /grant "NT AUTHORITY\LOCAL SERVICE:(OI)(CI)M"
    ```
    ```powershell
    icacls C:\13thConsole\agent /grant "arma-svc:(OI)(CI)RX"
    ```
11. Give the agent's account the Arma folders:
    ```powershell
    icacls C:\Arma3Server /grant "arma-svc:(OI)(CI)M"
    ```
    ```powershell
    icacls C:\ArmaData /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "arma-svc:(OI)(CI)M"
    ```
    The second one also stops other Windows users reading `C:\ArmaData`: SteamCMD keeps its saved Steam sign-in there.

## Part E: the panel

In PowerShell, always type `sc.exe`: plain `sc` is a different PowerShell command.

12. Install and start the panel as a service. It listens on `127.0.0.1:5080` only; Caddy is the way in from outside:
    ```powershell
    sc.exe create "13th Console" binPath= "C:\13thConsole\panel\Panel.exe" start= auto obj= "NT AUTHORITY\LocalService" password= ""
    ```
    ```powershell
    sc.exe start "13th Console"
    ```
13. On the first start it writes a one-time setup token. Keep it for step 17:
    ```powershell
    Get-Content C:\13thConsole\panel\data\setup-token.txt
    ```
    If the file isn't there, check **Event Viewer → Windows Logs → Application** for errors from the panel.

## Part F: HTTPS with Caddy

14. Download Caddy for **Windows amd64** from `https://caddyserver.com/download` and save it as `C:\13thConsole\caddy\caddy.exe`.
15. Create `C:\13thConsole\caddy\Caddyfile` (no file extension) with your domain:
    ```
    {
    	admin off
    }
    console.your-domain.org {
    	reverse_proxy 127.0.0.1:5080
    }
    ```
    Caddy gets the Let's Encrypt certificate itself and passes the live updates (WebSockets) and the visitor's address through to the panel. `admin off` closes Caddy's built-in admin API (`localhost:2019`, no password), which anything running on the box could otherwise use to rewrite Caddy's settings; after changing the Caddyfile, restart the caddy service.
16. Open the ports and start Caddy. Ports 80 and 443 are for the console; UDP 2302–2306 is the game (the profile's port through port + 4; for port 2100, 2100–2104). Don't open 5080, and don't open the RCon port (game port + 10): RCon only listens on the box itself.
    ```powershell
    New-NetFirewallRule -DisplayName "13th Console HTTPS" -Direction Inbound -Protocol TCP -LocalPort 80,443 -Action Allow
    ```
    ```powershell
    New-NetFirewallRule -DisplayName "Arma 3 server" -Direction Inbound -Protocol UDP -LocalPort 2302-2306 -Action Allow
    ```
    ```powershell
    sc.exe create caddy start= auto binPath= "C:\13thConsole\caddy\caddy.exe run --config C:\13thConsole\caddy\Caddyfile"
    ```
    Caddy faces the internet, so give it its own account instead of SYSTEM: **services.msc** → **caddy** → **Log On** → **This account**: `NT SERVICE\caddy`, with both password boxes empty → OK. Then let that account read its folder:
    ```powershell
    icacls C:\13thConsole\caddy /grant "NT SERVICE\caddy:(OI)(CI)RX"
    ```
    ```powershell
    sc.exe start caddy
    ```
    Its certificates are expected under `C:\Windows\ServiceProfiles\caddy` (first install: check that Caddy starts and gets its certificate).
    If your hosting company has a network firewall in its control panel, open the same ports there too.

## Part G: first sign-in

17. Open `https://console.<your-domain>/setup`, paste the setup token, and create the **Owner** account. The password needs at least 12 characters.
18. Two-factor sign-in is optional: anyone can turn it on from their account page with an authenticator app, and **Settings** can require it for chosen roles. Nobody is made to use it by default.

## Part H: connect the agent

19. In the console, open **Server → Setup → Add agent** and copy the key. It's shown once; **New key** issues another.
20. Edit `C:\13thConsole\agent\appsettings.json` (Notepad as Administrator). The agent talks to the panel on the same box, so it uses the local address:
    ```json
    "Agent": {
      "PanelUrl": "http://127.0.0.1:5080",
      "Key": "<the key from step 19>",
      "InstallRoots": ["C:\\Arma3Server"],
      "DataRoot": "C:\\ArmaData",
      "StopTimeoutSeconds": 60,
      "QueryIntervalSeconds": 5
    }
    ```
21. Install the agent as a service:
    ```powershell
    sc.exe create "13th Console Agent" binPath= "C:\13thConsole\agent\ConsoleAgent.exe" start= auto
    ```
    Then open **services.msc** → **13th Console Agent** → **Log On** → **This account**: `.\arma-svc` with the password from step 8. Windows grants it "Log on as a service" when you click OK. Start the service.
    Back in the console, **Server → Setup** shows the agent as **Connected** within a few seconds. Register the install it reports (`C:\Arma3Server`); it shows **No server yet** until step 22.

## Part I: the server, its mods and the first profile

22. **Install the server:**
    1. **Mods → Steam account:** type the account name from step 7 and press **Save**. Only the name is stored.
    2. **Server → Setup → 3 · Server files:** pick the branch and press **Install or update** for your install. **Public** is the normal one; **Creator DLC** adds the server data of the Creator DLCs, if your missions use them.
    3. The job shows a box when Steam asks. Type the password, then the Steam Guard code from the email or the Steam app (or approve the sign-in in the app). The answers go straight to SteamCMD; the console stores neither. SteamCMD remembers the sign-in afterwards, so later jobs don't ask. To make it forget (for example after changing the Steam password), delete `C:\ArmaData\steamcmd\config\config.vdf`; the next job asks again.
    4. Wait for **Done**. The install now shows **Server exe found**.

    If `arma3server_x64.exe` later won't start because a DLL is missing (for example `XAPOFX1_5.dll` or `MSVCP140.dll`), install Microsoft's DirectX End-User Runtime and the Visual C++ x64 redistributable.
23. **Mods:**
    1. In the Arma 3 Launcher on your PC, export your mod preset to an `.html` file.
    2. **Mods → Mod lists → Import preset**, and pick the file. The console makes a mod list from it and adds the mods it doesn't have yet as **Not installed**. Pasting Workshop links or IDs on the **Mods** page works too.
    3. On the **Mods** page, press **Install missing**. SteamCMD downloads them; the job shows the progress.
    4. Open the list to choose what each mod is: a client mod (`-mod`, players need it too), server only (`-serverMod`), headless, or optional (its key is allowed, nothing loads it).
24. **Profiles → New profile:**
    - **Slug:** `main`.
    - **Install:** the one you registered.
    - **Port:** 2302.
    - **Public address:** the box's IP or domain, which is what members see as the join address.

    Then fill in the profile:
    1. **Overview → Server presets:** load **Performance** (any mission) or **Maximum** (heavy ACE/Zeus ops).
    2. **General:** server name, max players and the passwords.
    3. **Mods:** the mod list from step 23.
    4. **Missions:** the rotation. Upload the missions first, on the **Missions** page (`.pbo`, `.zip` or a mission folder). Each upload is written into `C:\Arma3Server\mpmissions` within seconds, and the mission page's **On the servers** list shows **Done** for the install.
    5. **Save.**

    Then press **Apply to server**, and **Start** on the Server page. Start first copies the list's mod keys into the server's `keys` folder (after backing the folder up to `C:\ArmaData\backups\keys`). It reads **Online** once the server answers Steam's query.

    **Check stopping now:** press **Stop**. It should go Offline within a few seconds. If it takes about 60 seconds every time, the service can't close Arma's window (see limit 2 above). The server still stops, but by being killed. Report it, and run the agent from a logged-in session until it's fixed.

    **Addon scans:** once the server and mods are installed, **Server → Setup** shows the install as **Scanned**, and the **Mods** page shows each mod's scan. **Partly scanned** names the files that couldn't be read (encrypted Creator DLC, for example); **Rescan** reads them again.

## Part J: let people in

25. **Members:** **Members → Invites**, pick a role (Member, S1–S4, S6, S7), copy the link and send it in a Discord DM. No email is sent.
    - The link lasts 72 hours unless you set another time (1 hour to 7 days).
    - The member opens the link, picks a username, a display name and a password of 12 or more characters, and signs in.
26. **Players:** in Arma 3, **Server Browser → Direct Connect**, enter the box's IP and port 2302 (or find the server by name). Members whose role includes "See the server's join password" see it on the Server page.

## Looking after it

- **Backups:** **Backups** in the side bar makes console and server backups, and **Schedules** can make them every night.
  - **A console backup** holds `console.db` plus the `keys` and `blobs` folders. Without `keys`, the saved server passwords can't be decrypted; without `blobs`, the mission library lists missions whose files are gone.
  - **Somewhere else:** a copy on the same disk doesn't survive losing the disk. In `C:\13thConsole\panel\appsettings.json` add `"Backups": { "CopyTo": "E:\\13thBackups" }`, and in `C:\13thConsole\agent\appsettings.json` add `"BackupCopyTo": "E:\\13thBackups"` inside `"Agent"`. Use another drive, a network share the services' accounts can write to, or a folder OneDrive or Google Drive syncs. Restart both services.
- **Restoring the console** from a console backup (the browser can't, because the running console can't replace its own database):
  1. Download the backup from **Backups**, or take it from `C:\13thConsole\panel\data\backups\console`, and put it on the box.
  2. Stop **13th Console** (`Stop-Service "13th Console"`).
  3. In an administrator terminal: `C:\13thConsole\panel\Panel.exe restore C:\path\to\console-….zip`. The current data moves to `panel\data\before-restore-<time>`.
  4. Start it again (`Start-Service "13th Console"`) and sign in.

  On a new or reinstalled box, the backup's keys can't be unlocked (they're tied to the Windows installation that made them). Everything else comes back. Set the saved secrets again: each server profile's passwords and raw server.cfg lines (then Apply), and the Discord webhook.
- **Unit time zone:** schedules run in **Settings → Unit time zone** (`Australia/Sydney` by default, which follows daylight saving; use `Australia/Brisbane` if the unit doesn't).
- **Updating:**
  1. Build again (Part A).
  2. Stop **13th Console** and **13th Console Agent**.
  3. Copy the new files over, keeping `panel\data` and `agent\appsettings.json`.
  4. Start both again.

  A running Arma server survives this: the agent re-attaches to it when it comes back.
- **Missions on the server:**
  - A new mission version is written over the old `.pbo` in `mpmissions` once nothing holds the file. While that mission is running it shows **Waiting**, and goes in when the mission ends.
  - The file it replaced is kept in `C:\ArmaData\backups\missions` for 30 days (**Backups → Keeping backups**).
  - **Restore** on a mission's Versions tab puts an older version back the same way.
- **Discord (optional):** in Discord, make a webhook for the channel that should hear about operations (Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL). In the console, paste it under Settings → **Notifications** → Discord webhook, **Save**, then **Send a test message**. Choose which events go there in the table below it.
- **Operations:** **Operations** plans an op and checks it. Before an op, run **Prepare** on it, tick the manual items, then **Apply to server** (S1 and S3 by default). Run **Check for updates** on the Mods page within the 24 hours before, or "Workshop updates done" reads "Can't tell".
- **Files:** **Files** in the side bar shows the server's own folders to whoever holds their `files.*` permissions (README, "Files"). Server profiles, Generated configs and Backups hold server passwords; keep those for S1 and S4. Deleted and overwritten files go to `C:\ArmaData\backups\files\` first. They're deleted after 30 days (**Backups → Keeping backups**).
- **Starting a mission (S3: mission makers and Zeus; S7 for FTXs):** **Server → Missions**, pick the mod list, then **Start mission** on a listed mission. It sets the rotation and mod list, stops the server if it's running, applies, and starts it, after saying what will happen. Missions that need a mod the list doesn't have are hidden; **Show missions that don't match** says what's missing.
- **Arma and mod updates:** stop the server, then **Server → Setup → Install or update** for the game, and **Mods → Check for updates → Update all** for the mods. Start it again.
- **Logs:**
  - **Panel and agent:** errors go to **Event Viewer → Windows Logs → Application**.
  - **Arma:** writes its RPT logs to `C:\ArmaData\profiles\<slug>`. They're also on the console's **Logs** page.
