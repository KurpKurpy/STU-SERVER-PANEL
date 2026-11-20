# Source spec — Private Arma 3 Unit Server & Mission Management Panel

Saved verbatim (formatting normalised to Markdown) from the user's message of 2026-11-19.
The design doc `2026-11-19-13th-console-design.md` records how this gets built; where it is silent, this spec governs.

---

MASTER AI DEVELOPMENT PROMPT — PRIVATE ARMA 3 UNIT SERVER & MISSION MANAGEMENT PANEL

## 1. YOUR ROLE

Act as a senior full-stack developer, software architect, DevOps engineer, UI/UX designer and specialist in Arma 3 dedicated server administration, Steam Workshop integrations and mission development.

Your task is to build a complete, functional, self-hosted web application for managing a private Arma 3 gaming group.

This application is NOT intended to be a commercial game server hosting business.

It is an internal management platform for a private Arma 3 unit. Its primary users are:

* Server administrators.
* Mission makers.
* Zeus operators.
* Mission commanders.
* Mod managers.
* Regular unit members.

The platform must allow the group to collaboratively manage its dedicated server, upload and edit missions, maintain mod collections, configure server profiles, prepare operations and perform routine server administration through a browser.

The goal is to combine the useful functionality of FASTER with a modern web-based management system designed specifically for an Arma 3 community.

The application should make it possible for a mission maker to prepare an entire operation without having to manually connect to the server or ask an administrator to move files around.

It should also allow Zeus operators and authorized administrators to manage the server before, during and after an operation.

## 2. MAIN REFERENCE PROJECT — FASTER

You MUST study this project before designing the application:

FASTER GitHub repository: https://github.com/Foxlider/FASTER
FASTER Wiki: https://github.com/Foxlider/FASTER/wiki

Use this project as a functional reference for how an Arma 3 server management application should work.

Pay particular attention to:

* Server profiles.
* SteamCMD automation.
* Arma 3 dedicated server installation and updating.
* Steam Workshop integration.
* Mod installation and updating.
* Importing Arma 3 Launcher HTML mod presets.
* Local mod support.
* Mod categorisation.
* Server-side and client-side mod separation.
* Headless Client mod configurations.
* Server startup parameters.
* Server configuration management.
* Mission selection and mission rotation.
* Custom difficulty configurations.
* Headless Client configuration.
* Server configuration previews.
* Generated configuration files.
* Advanced settings and manual configuration editing.

Reference the original documentation when implementing equivalent functionality.

FASTER is a desktop management application. This project must deliver a web-based equivalent management experience with additional collaborative tools designed around a private Arma 3 unit.

Do not simply recreate FASTER's interface as a static website.

Build working functionality behind every management screen.

Use FASTER as functional inspiration. Do not copy its source code or assets unless you intentionally comply with its GPL-3.0 licensing requirements.

## 3. PROJECT PURPOSE

Create a central website where our Arma 3 group can manage everything associated with running dedicated servers and conducting operations.

The most important workflows are:

1. A mission maker uploads a mission.
2. The mission maker edits and configures its files.
3. The mission maker assigns the mods required by the mission.
4. The mission maker saves and submits the mission.
5. An authorized administrator or mission manager approves it.
6. The mission is deployed to the Arma 3 server.
7. The mission becomes available in the mission selector.
8. A Zeus or mission commander selects the mission for an operation.
9. The system verifies the required mods and server configuration.
10. The server is started or prepared for the operation.
11. After the operation, the group can review logs and preserve the mission version.

Everything should be manageable from the website.

The platform should also support server maintenance, server updates, Workshop mod updates, automated backups and multiple server configurations.

Prioritise making these workflows reliable and easy to use.

## 4. CORE APPLICATION DESIGN

Build the platform around three main areas.

### A. Main Group Dashboard

This is the landing page for authenticated group members.

It provides:

* Upcoming operations.
* Current server status.
* Current mission.
* Active server profile.
* Active modpack.
* Server resource information.
* Recent mission uploads.
* Recent changes.
* Notifications.
* Announcements.
* Quick links to mission management and server management.

The dashboard should be useful to regular members as well as administrators.

### B. Mission Operations Management

This is the primary workspace for mission makers and Zeus operators.

It manages:

* Mission uploads.
* Mission editing.
* Mission versions.
* Mission approval.
* Mission deployment.
* Mission schedules.
* Mod requirements.
* Mission configuration.
* Operation preparation.
* Mission history.

### C. Server Management

This is the FASTER-inspired server management section.

It manages:

* Dedicated server installation.
* Server startup and shutdown.
* Server profiles.
* Mod installation.
* Workshop management.
* Configuration.
* Mission rotation.
* Server files.
* Console.
* Logs.
* Backups.
* Performance.

All three areas must connect to the same backend and share data.

## 5. USER ACCOUNTS AND PERMISSIONS

Build a private authentication system.

This website is for an existing group, so registration should be invitation-only or administrator-controlled.

Allow administrators to create accounts and invite group members.

Implement roles with configurable individual permissions.

### Owner / Super Administrator

Full access to everything.

Can manage users, server infrastructure, configurations, permissions, missions, mods, backups and application settings.

### Server Administrator

Can:

* Start and stop servers.
* Restart servers.
* Manage server profiles.
* Edit server configurations.
* Install and update mods.
* Manage Workshop downloads.
* Upload and deploy missions.
* Manage files.
* Manage backups.
* View logs.
* Manage server schedules.

### Mod Manager

Can:

* Search Workshop mods.
* Add and remove mods.
* Update mods.
* Manage mod collections.
* Import HTML presets.
* Manage client and server modlists.
* Manage mod keys.
* Assign mod collections to missions.
* View mod installation status.

### Mission Maker

Can:

* Upload missions.
* Create mission projects.
* Edit mission files.
* Upload supporting files.
* Create mission versions.
* Assign required mods.
* Preview mission information.
* Submit missions for approval.
* View deployment status.
* Download mission files.

Mission makers should not automatically receive unrestricted access to server configuration or infrastructure.

### Zeus / Mission Commander

Can:

* View upcoming operations.
* View approved missions.
* Select an approved mission for an operation.
* View required mods.
* View server readiness.
* Request mission deployment.
* View server status.
* Access permitted operational controls.
* View relevant server logs.

Allow an administrator to give individual Zeus operators additional permissions where needed.

### Regular Member

Can:

* View announcements.
* View upcoming operations.
* View approved missions.
* Download mission files.
* View required modlists.
* Download mod presets.
* View server information.

All permissions must be enforced by the backend, not just hidden in the interface.

Allow administrators to customise individual permissions.

## 6. MAIN DASHBOARD

Build a professional dashboard specifically for an Arma 3 group.

### Server status panel

Display:

* Server name.
* Server IP and port.
* Server status.
* Current mission.
* Current map.
* Current player count.
* Maximum player count.
* Server uptime.
* CPU utilisation.
* RAM usage.
* Disk usage.
* Last restart.
* Last update.

Provide appropriate controls for authorised users.

* Start.
* Stop.
* Restart.
* View Console.
* Manage Server.

Show whether the server is online, offline, starting, stopping, updating or experiencing an error.

### Upcoming operations

Display a calendar or event list showing:

* Operation name.
* Date and time.
* Mission.
* Map.
* Zeus.
* Mission maker.
* Required mods.
* Operation description.
* Preparation status.

Allow an authorised user to open an operation and access all its associated information.

### Recent activity

Display events such as:

* Mission uploaded.
* Mission approved.
* Mission deployed.
* Mod collection changed.
* Workshop mod updated.
* Server restarted.
* Server configuration modified.
* Operation created.

Record who performed each action.

## 7. MISSION MANAGEMENT SYSTEM

THIS IS ONE OF THE MOST IMPORTANT FEATURES.

Build a comprehensive mission management system for the group's mission makers.

The website should become the central location for storing, editing, preparing and deploying Arma 3 missions.

### Mission library

Create a searchable mission library.

Every mission should have its own page.

Store:

* Mission name.
* Mission description.
* Mission maker.
* Mission version.
* Map.
* Mission type.
* Required mod collection.
* Mission PBO.
* Source files, when available.
* Upload date.
* Last modified date.
* Approval status.
* Deployment status.
* Operation history.
* Change history.

Allow missions to be organised using tags and categories.

Example categories:

* Main Operations.
* Training.
* Zeus Operations.
* Campaigns.
* Side Operations.
* Testing.
* Archived Missions.

Include search and filters.

### Mission upload system

Mission makers must be able to upload missions through the browser.

Support:

* `.pbo` mission files.
* ZIP archives containing mission source folders.
* Individual mission files.
* Complete mission directories through drag and drop.

Provide upload progress.

Validate the upload before accepting it.

Check:

* File format.
* File size.
* Archive integrity.
* Duplicate mission names.
* Required mission structure.
* Potentially missing files.
* Whether the mission already exists.

Allow mission makers to create a new mission or upload a new version of an existing mission.

Never overwrite an existing published mission without a backup.

### Mission file editor

Create a browser-based file editor for editable mission source files.

Support syntax highlighting for relevant formats, including:

* SQF.
* SQM.
* EXT.
* HPP.
* CPP.
* CFG.
* JSON.
* XML.
* TXT.

Allow users with permission to:

* Open files.
* Edit files.
* Save files.
* Create files.
* Delete files.
* Rename files.
* Create directories.
* Upload supporting files.
* Download files.
* Search within files.

Include:

* Syntax highlighting.
* Line numbers.
* Search and replace.
* Automatic indentation.
* Unsaved change warnings.
* File history.
* Change comparison.
* Save confirmation.

Support editing common Arma mission files, including:

* `mission.sqm`
* `description.ext`
* `init.sqf`
* `initServer.sqf`
* `initPlayerLocal.sqf`
* Mission-specific SQF scripts.
* Custom configuration files.

Where possible, provide a file tree similar to a proper development environment.

### PBO handling

A PBO is a packaged binary mission archive. Do not attempt to edit it as if it were an ordinary text file.

Provide proper PBO handling:

1. Upload the PBO.
2. Store the original PBO.
3. Allow extraction using a configured compatible PBO utility.
4. Allow editing of extracted mission source files.
5. Repack the updated mission.
6. Validate the resulting PBO.
7. Preserve the previous published version.
8. Allow the mission maker to download the new PBO.

Make the PBO utility configurable.

Do not bundle third-party proprietary tools without the appropriate permission or licence.

### Mission versioning

Implement proper mission version control.

Every saved or published version should be recorded.

Allow users to:

* View previous versions.
* Compare versions.
* Restore older versions.
* Download previous PBOs.
* See who changed something.
* Add version notes.
* View timestamps.

Use a version history system that protects against accidental file loss.

### Mission approval workflow

Implement:

Draft → Submitted → Under Review → Approved → Published → Archived.

Administrators should be able to configure whether mission approval is required.

Mission makers should be able to save work without affecting the currently published mission.

An approved mission should be deployable to a server through an authorised action.

## 8. MISSION DEPLOYMENT

Create a dedicated deployment system.

An authorised user should be able to deploy a mission to the server without manually navigating directories.

The system should:

1. Verify the mission version.
2. Verify that the mission is approved, if approval is required.
3. Check the required map.
4. Check the required mod collection.
5. Check that the necessary server files exist.
6. Back up an existing mission with the same destination name.
7. Copy the mission to the correct `mpmissions` directory.
8. Confirm that deployment completed.
9. Refresh the mission list.
10. Record the deployment in the activity history.

Display progress.

Provide meaningful errors if deployment fails.

Allow deployment to a testing server before production.

Support deployment to multiple configured server profiles where authorised.

Never silently overwrite the only copy of a previously published mission.

## 9. MISSION CONFIGURATION AND OPERATION PLANNER

Mission makers and Zeus operators need an easy way to prepare events.

Create an operation planner.

An operation should contain:

* Operation name.
* Description.
* Date and time.
* Selected mission.
* Selected map.
* Selected server profile.
* Selected mod collection.
* Mission maker.
* Zeus operators.
* Mission notes.
* Player briefing.
* Preparation checklist.
* Status.

### Operation preparation checklist

Include configurable checks such as:

* Mission uploaded.
* Mission approved.
* Mission deployed.
* Correct server profile selected.
* Correct mods assigned.
* Workshop updates completed.
* Mod keys deployed where required.
* Mission configuration checked.
* Mission rotation configured.
* Server ready.
* Zeus operators assigned.

Display a readiness indicator.

Show exactly which requirements are incomplete.

### Prepare Operation button

Create a central action called `Prepare Operation`.

When activated, it should:

1. Load the selected operation.
2. Verify its mission.
3. Verify its mission version.
4. Verify the required mod collection.
5. Check if the mods are installed and updated.
6. Check server configuration.
7. Check mission deployment.
8. Check the selected server profile.
9. Identify missing requirements.
10. Provide actions to resolve issues.

Once all checks pass, provide an option to apply the operation configuration to the selected server.

Require permission and confirmation before changing the live server.

## 10. STEAM WORKSHOP AND MOD MANAGEMENT

Build a dedicated Arma 3 Workshop management system inspired by FASTER.

This should be a core part of the platform.

Use SteamCMD and appropriate Steam Workshop interfaces for actual installations and updates.

### Workshop browser

Allow mod managers to:

* Search for mods.
* Add mods through Workshop IDs.
* Add mods through Workshop URLs.
* View mod names.
* View Workshop IDs.
* View available metadata.
* View installation status.
* View update status.
* View local mods.
* Install mods.
* Update mods.
* Remove mods.
* Reinstall mods.

Where supported, provide mod images and descriptions.

Clearly display download and update progress.

### SteamCMD automation

Implement actual SteamCMD-based automation for:

* Initial Arma 3 server installation.
* Arma 3 dedicated server updates.
* Workshop mod downloads.
* Workshop mod updates.
* Required server files.
* Selected supported branches or versions.

Support installation checks and retries.

Support authenticated SteamCMD workflows where required.

Securely manage Steam credentials and Steam Guard authentication. Never log passwords or expose authentication secrets through the frontend.

Do not assume all Workshop items can be downloaded anonymously.

### Mod collections

Allow mod managers and mission makers with suitable permissions to create saved mod collections.

Example collections:

* Main Unit Modset.
* Training.
* Zeus Toolkit.
* Medical Operations.
* Armour Operations.
* Testing Modset.
* Vanilla.

Every collection should store:

* Workshop IDs.
* Local mod references.
* Mod names.
* Mod versions where available.
* Load order.
* Enabled state.
* Mod classification.
* Additional information.
* Last update date.

Allow collections to be duplicated, edited, exported and imported.

### FASTER-style mod categorisation

Build separate mod lists for:

* Client Mods.
* Server-Only Mods.
* Headless Client Mods.
* Optional Mods.

Allow authorised users to enable or disable mods individually.

Provide convenient actions such as:

* Copy Client Mods to Headless Client.
* Copy from another profile.
* Enable all.
* Disable all.
* Clear selection.
* Import preset.
* Export preset.
* Sort by name.
* Sort by Workshop ID.

Keep the distinction between `-mod` and `-serverMod` clear.

Generate the appropriate startup parameters from the selected mod categories.

### Import Arma 3 Launcher presets

Support importing the HTML preset exported by the Arma 3 Launcher.

The system should:

1. Accept an HTML preset upload.
2. Parse its Workshop IDs.
3. Identify the included mods.
4. Match them against installed Workshop items.
5. Show missing mods.
6. Allow the user to download missing mods.
7. Generate a saved mod collection.
8. Allow the collection to be applied to a server profile.

Where possible, also support compatible Workshop collection imports.

### Mod keys

Provide a key management system.

Allow authorised users to:

* Find `.bikey` files.
* Deploy mod keys.
* Copy keys to the server `keys` folder.
* Detect missing keys where possible.
* Remove obsolete keys.
* Back up existing keys.

Never automatically remove existing keys without a recoverable backup.

### Mod update management

Show:

* Installed mods.
* Available updates.
* Last update time.
* Download status.
* Installation errors.
* Missing Workshop items.

Allow:

* Update individual mods.
* Update selected mods.
* Update all mods.
* Schedule updates.
* Update before a planned operation.

Do not change a live server's active mod configuration without an authorised action.

## 11. SERVER PROFILES

Recreate the useful server profile system found in FASTER.

Allow administrators to create and save multiple Arma 3 server profiles.

Examples:

* Main Operations.
* Training Server.
* Zeus Testing.
* Mission Development.
* Campaign Server.
* Public Event Server.

Each profile should have its own:

* Profile name.
* Server executable.
* Server port.
* Server configuration.
* Basic configuration.
* Startup parameters.
* Mod collection.
* Mission rotation.
* Mission difficulty.
* Headless Client configuration.
* Server settings.
* Performance settings.
* Profile directory.
* Additional custom parameters.

Allow profiles to be duplicated and edited.

Provide a profile comparison tool so administrators can see differences between configurations.

Allow a profile to be selected and applied through the browser.

Prevent conflicts involving ports, shared directories and simultaneously running instances.

### Server profile controls

Provide:

* Save Profile.
* Duplicate Profile.
* Rename Profile.
* Delete Profile.
* Load Profile.
* Apply Profile.
* Reset to Previous Version.
* Compare Profiles.

Before applying changes, show a preview of the files and settings that will change.

Allow manual editing alongside visual configuration fields.

Preserve custom configuration entries that are not represented by the visual editor.

## 12. ARMA 3 SERVER CONFIGURATION EDITOR

Create a proper configuration management interface inspired by FASTER.

Support the main configuration files, including:

* `server.cfg`
* `basic.cfg`
* `Arma3Profile`
* Relevant profile configuration files.

Generate configuration files from structured settings.

Provide an easy-to-use visual interface.

Also provide a raw text editor for experienced administrators.

### Server settings

Support settings such as:

* Hostname.
* Server password.
* Administrator password.
* Maximum players.
* BattlEye.
* Signature verification.
* Allowed file patching.
* Persistent battlefield.
* Voice over network.
* VON quality.
* Server ranking.
* Admin IDs.
* MOTD.
* Voting settings.
* Network limits.
* Anti-flood settings.
* Logging settings.
* Mission cycle.
* Mission selection.

### Startup parameters

Support relevant Arma 3 dedicated server startup arguments.

Examples:

* `-config`
* `-cfg`
* `-profiles`
* `-name`
* `-port`
* `-maxMem`
* `-cpuCount`
* `-exThreads`
* `-world`
* `-mod`
* `-serverMod`
* `-autoinit`
* `-filePatching`
* `-noSound`
* `-nologs`
* `-hugePages`
* `-loadMissionToMemory`

Only generate parameters appropriate for the configured server executable and operating environment.

Allow administrators to add custom arguments.

Display the complete generated startup command.

Provide a copy button.

### Basic configuration

Create a visual editor for network and performance settings in `basic.cfg`.

Allow the generated output to be viewed and manually edited.

### Difficulty settings

Allow administrators to configure custom difficulty presets.

Provide common difficulty configurations and allow saving custom presets.

Allow individual server profiles to use different difficulty configurations.

### Configuration preview

Whenever a visual field changes, regenerate the relevant configuration output.

Show a preview of the generated file.

Allow administrators to compare current and proposed configuration.

Never silently discard manually added settings.

## 13. MISSION ROTATION AND SELECTION

Create a dedicated mission selection interface.

Allow users to:

* View available missions.
* Refresh the mission list.
* Select a default mission.
* Set up a mission rotation.
* Add missions to rotation.
* Remove missions from rotation.
* Change mission order.
* Configure difficulty presets.
* Enable or disable automatic selection.
* Enable random rotation.
* Configure mission parameters where supported.
* Configure automatic mission initialisation where supported.
* Configure persistent battlefield behaviour.

Support both:

* A visual mission selector.
* An advanced mission override editor.

Generate the appropriate mission configuration output.

Show the exact mission currently selected and the mission that will load next.

Provide a warning when a selected mission is missing or has not been deployed.

## 14. HEADLESS CLIENT MANAGEMENT

Provide dedicated Headless Client configuration.

Allow administrators to:

* Enable or disable Headless Clients.
* Configure Headless Client IP addresses.
* Configure the number of Headless Clients.
* Configure startup behaviour.
* Configure Headless Client mod collections.
* View Headless Client launch parameters.
* Configure separate Headless Client processes where supported.

Allow copying the client mod selection into the Headless Client mod selection.

Warn users that Headless Clients should only be enabled for missions that support them.

Do not automatically start headless clients when the selected profile or mission does not require them.

## 15. SERVER CONTROL PANEL

Build a proper browser-based server management interface.

This should provide the controls expected from a dedicated server management tool.

### Server overview

Show:

* Server name.
* IP address.
* Port.
* Status.
* Current mission.
* Current profile.
* Active mod collection.
* Player count.
* CPU.
* Memory.
* Uptime.
* Last restart.

### Controls

Implement:

* Start.
* Stop.
* Restart.
* Force Stop.
* Update Server.
* Update Mods.
* Apply Profile.
* Change Mission.
* Open Console.
* View Logs.

Require confirmation for disruptive actions.

Provide status feedback from the actual server process.

### Live console

Build a real-time console.

Include:

* Live output.
* Timestamps.
* Search.
* Copy.
* Download logs.
* Command history.
* Authorised command input.

Use WebSockets or an equivalent real-time technology.

Only allow commands through a controlled and authenticated backend.

Do not expose unrestricted host shell access to ordinary users.

### Player information

Where supported by reliable server information or RCON, display:

* Player name.
* Player ID.
* Ping.
* Time connected.
* Connection status.

Allow authorised administrators to:

* Kick players.
* Ban players.
* Unban players.
* Send appropriate server messages.
* View ban lists.

Clearly show when a particular player statistic is not available.

### RCON and BattlEye

Provide management for supported BattlEye and RCON configurations.

Include:

* RCON connection status.
* RCON settings.
* Password management.
* Ban management.
* Player management.
* RCON logs.

Protect credentials and never expose secrets to ordinary accounts.

## 16. FILE MANAGER

Create an integrated file manager for authorised users.

The website must let administrators and mission makers access the appropriate server and mission directories without needing separate remote access software.

Features:

* Browse directories.
* Upload files.
* Download files.
* Rename files.
* Delete files.
* Move files.
* Copy files.
* Create folders.
* Edit text files.
* Extract supported archives.
* Search files.
* View file sizes.
* View modification dates.

Provide scoped access to different directory types.

Examples:

* Mission library.
* `mpmissions`.
* `@mod` folders.
* `keys`.
* `profiles`.
* `config`.
* `logs`.
* `BattlEye`.

Only expose files that the user's permission level allows.

Protect system files, application files and unrelated directories.

Provide confirmation and backups for destructive operations.

## 17. SERVER LOGS AND DIAGNOSTICS

Create a log management page.

Support viewing:

* Arma 3 RPT logs.
* Server console logs.
* System logs.
* Installation logs.
* SteamCMD logs.
* Mod update logs.
* Deployment logs.
* Crash reports where available.

Include:

* Live log viewing.
* Search.
* Filtering.
* Timestamps.
* Severity indicators.
* File downloads.
* Historical log access.

Where possible, provide basic Arma-specific diagnostics.

Identify common problems such as:

* Missing addons.
* Missing mission files.
* Missing configuration files.
* Invalid startup parameters.
* Port conflicts.
* Failed Workshop downloads.
* Server startup failures.
* Mission loading problems.

Display useful explanations and potential fixes.

Do not claim that automated diagnostics can identify every mission or mod problem.

## 18. BACKUPS AND RECOVERY

Create an automatic backup system.

Support backups of:

* Missions.
* Mission source files.
* Published PBOs.
* Server configuration.
* Server profiles.
* Modlists.
* Mod keys.
* Profile directories.
* Relevant persistence data.

Allow:

* Manual backup.
* Automatic scheduled backup.
* Pre-deployment backup.
* Pre-update backup.
* Restore.
* Download.
* Delete.
* Backup history.

Before applying destructive updates or replacing published missions, create a recoverable backup where appropriate.

Provide a rollback system for mission deployment and server configuration.

## 19. SCHEDULED OPERATIONS AND AUTOMATION

Build a scheduling system.

Allow authorised users to schedule:

* Server restarts.
* Server updates.
* Workshop mod updates.
* Backups.
* Server shutdowns.
* Server startup.
* Mission deployment.
* Maintenance windows.

Support recurring schedules.

Display the next scheduled tasks.

Provide a history of completed and failed tasks.

For operations, allow automatic notifications before the scheduled start time.

Never automatically update the active operational modpack during an event unless an administrator explicitly authorises it.

## 20. OPTIONAL DISCORD INTEGRATION

Create an optional Discord integration using a configurable bot or webhook.

Useful features:

* Announce upcoming operations.
* Notify when a mission is approved.
* Notify when a mission is deployed.
* Notify when a server starts.
* Notify when a server goes offline.
* Notify when an update fails.
* Notify when an operation is ready.

Allow administrators to choose which events generate notifications.

Do not make Discord integration a requirement for the panel to function.

## 21. FUTURE SUPPORT FOR OTHER GAME SERVERS

Although Arma 3 is the primary purpose, build the architecture so the application can eventually manage other dedicated game servers.

This is a secondary feature, not the main focus.

Implement a modular game adapter system.

Each supported game should define its own:

* Installation method.
* Update method.
* Startup command.
* Configuration format.
* Port configuration.
* Console integration.
* Health checks.
* Backup rules.

Potential future games include:

* Arma Reforger.
* Minecraft.
* Rust.
* DayZ.
* Counter-Strike 2.
* Squad.
* Project Zomboid.
* Valheim.

Do not attempt to build every game integration immediately.

Complete the Arma 3 functionality first.

Adding another game should be possible through a separate integration module rather than requiring the entire application to be rewritten.

## 22. USER INTERFACE AND DESIGN

The design should resemble a proper dedicated server administration application inspired by FASTER, but adapted for the web.

Do not make it look like a generic hosting company dashboard.

### Design direction

* Modern dark theme.
* Optional light theme.
* Professional typography.
* Clean panels.
* Tabbed configuration pages.
* Clear navigation.
* Responsive layout.
* Useful icons.
* Consistent spacing.
* Real-time server indicators.
* Clear progress indicators.
* Useful error messages.

Use a functional management interface with enough information visible for administrators to work efficiently.

Avoid unnecessary decorative cards and excessive gradients.

### Main navigation

The main sidebar should contain:

1. Dashboard.
2. Operations.
3. Missions.
4. Mod Manager.
5. Server Profiles.
6. Server Control.
7. File Manager.
8. Logs.
9. Backups.
10. Members.
11. Settings.

### Server profile navigation

Inside a profile, organise settings into tabs:

* Overview.
* General.
* Mods.
* Missions.
* Difficulty.
* Headless Clients.
* Server Configuration.
* Performance.
* Startup Parameters.
* Logs.

### Mission navigation

Inside a mission, organise settings into tabs:

* Overview.
* Files.
* Editor.
* Required Mods.
* Versions.
* Deployment.
* History.

The website should work properly on desktop first but remain usable on tablets and mobile devices.

## 23. TECHNICAL ARCHITECTURE

Build a real application with a proper backend.

Recommended technology direction:

Frontend:

* React.
* TypeScript.
* Next.js or an equivalent supported framework.
* Tailwind CSS.
* A suitable component library.
* Monaco Editor or an equivalent code editor for mission files.

Backend:

* ASP.NET Core using a currently supported .NET release, or another well-structured backend if there is a strong technical reason.
* REST APIs.
* SignalR or WebSockets for live updates.
* Background jobs for installations, updates and deployment.

A .NET backend is a particularly suitable option to investigate because FASTER itself uses C# and the application needs reliable integration with Windows-based server processes and filesystem operations.

Database:

* SQLite for a simple single-instance deployment if appropriate.
* PostgreSQL as an alternative for more demanding multi-user deployments.

Server integration:

Create a local server management agent or Windows service where appropriate.

The web application should not directly execute server processes from the browser.

The service should perform authorised operations such as:

* Starting and stopping Arma 3.
* Updating dedicated server files.
* Downloading Workshop mods.
* Managing server profiles.
* Editing permitted configuration files.
* Managing mission deployments.
* Handling backups.
* Reading logs.
* Monitoring resources.

Use a secure communication mechanism between the web application and the service.

The initial version should be designed to work with one dedicated server machine.

Allow future support for additional machines without requiring a complete redesign.

## 24. SECURITY

Although this is a private group application, security is still essential.

Implement:

* Secure authentication.
* Password hashing.
* Invitation-only registration.
* Role-based permissions.
* Secure sessions.
* Multi-factor authentication where appropriate.
* API authentication.
* File upload validation.
* File access restrictions.
* Path traversal protection.
* Audit logs.
* Rate limiting.
* Secure secret storage.
* HTTPS.
* Secure WebSocket authentication.

Never allow mission makers to access unrestricted system directories.

Run server processes under an appropriate restricted account.

Never expose SSH credentials, RCON passwords, Steam credentials or application secrets to ordinary members.

Ensure mission uploads cannot execute arbitrary host commands through the web application.

Validate uploaded archives and prevent malicious archive paths from escaping the intended directory.

## 25. INITIAL INSTALLATION AND SETUP

Create a simple installation wizard for the application.

The administrator should be able to configure:

* Application name.
* Administrator account.
* Server operating system.
* Arma 3 server location.
* Arma 3 server executable.
* SteamCMD location.
* Workshop download location.
* Server profile directory.
* Mission directory.
* Mod directory.
* Configuration directory.
* Backup location.
* Network ports.
* Server management service.

Provide a connection test.

Automatically detect existing Arma 3 server installations where possible.

Allow the administrator to import existing server files and configurations instead of being forced to install everything from scratch.

Provide a guide for initial SteamCMD setup.

Include instructions for Steam authentication and Steam Guard where required.

## 26. DEVELOPMENT PHASES

Build the system in manageable stages.

Do not create a large frontend prototype and claim that the platform is finished.

### Phase 1 — Application Foundation

Build:

* Project structure.
* Backend.
* Frontend.
* Database.
* Authentication.
* User management.
* Roles and permissions.
* Main dashboard.
* Navigation.

Deliver a working website.

### Phase 2 — Server Connection

Build:

* Local server agent.
* Server registration.
* Process management.
* Start and stop functionality.
* Live server status.
* Resource monitoring.
* Console output.
* Log access.

Test against an actual Arma 3 dedicated server.

### Phase 3 — Server Profiles

Build:

* Profile creation.
* Profile editing.
* Profile duplication.
* Server settings editor.
* Startup parameter editor.
* Basic.cfg editor.
* Difficulty settings.
* Mission rotation.
* Configuration output preview.

Test that generated configuration files work correctly.

### Phase 4 — Mission Management

Build:

* Mission library.
* Mission upload.
* PBO storage.
* Mission extraction and repacking integration.
* File manager.
* Browser code editor.
* Mission versioning.
* Mission approval.
* Mission deployment.
* Rollback.

This phase is especially important.

### Phase 5 — Workshop and Mod Management

Build:

* SteamCMD integration.
* Workshop searching and metadata.
* Mod downloading.
* Mod updating.
* Mod deletion.
* Mod collections.
* HTML preset import.
* Client modlist.
* Server modlist.
* Headless Client modlist.
* Optional modlist.
* Key management.

Test against real Workshop content.

### Phase 6 — Zeus and Operations Management

Build:

* Operation calendar.
* Operation creation.
* Mission assignment.
* Zeus assignment.
* Required mod assignment.
* Operation preparation checklist.
* Server readiness checks.
* Prepare Operation workflow.
* Discord notifications.

### Phase 7 — Final Management Tools

Build:

* Automated backups.
* Scheduling.
* Diagnostics.
* Player information.
* RCON integration.
* Audit history.
* Optional secondary server integrations.

### Phase 8 — Testing and Finalisation

Test all critical features.

Verify:

* User permissions.
* File management.
* Mission uploads.
* PBO processing.
* Mission deployments.
* Configuration generation.
* Server startup.
* Server shutdown.
* Workshop installation.
* Mod updates.
* Profile switching.
* Backups.
* Recovery.
* Live console.
* Scheduled tasks.

Do not mark functionality as complete until it has actually been tested.

## 27. FINAL ACCEPTANCE REQUIREMENTS

The project must satisfy the following requirements before it can be considered a successful implementation.

Mission makers must be able to:

* Log into the website.
* Upload an Arma 3 mission.
* Edit mission source files.
* Upload supporting files.
* Save mission changes.
* View version history.
* Assign required mods.
* Submit a mission for approval.
* See when it has been deployed.
* Download previous versions.

Zeus operators must be able to:

* View available approved missions.
* View upcoming operations.
* See the selected server profile.
* See the required mods.
* See server readiness.
* Select an approved mission where authorised.
* Access authorised server controls.
* View relevant operational logs.

Mod managers must be able to:

* Search Steam Workshop.
* Add mods.
* Install mods.
* Update mods.
* Import Arma 3 Launcher HTML presets.
* Create mod collections.
* Separate client and server mods.
* Manage Headless Client mods.
* Deploy mod keys.
* Check for missing or outdated mods.

Server administrators must be able to:

* Start and stop the server.
* Restart the server.
* Update the server.
* Manage server profiles.
* Edit server configuration.
* Manage startup parameters.
* Configure missions.
* Manage mission rotation.
* Manage Headless Clients.
* Manage files.
* View live console output.
* Access RPT logs.
* Create backups.
* Restore backups.

General platform requirements:

* Multiple users can work on the platform.
* Permissions are correctly enforced.
* Changes are recorded.
* Mission files are protected against accidental loss.
* Server configuration can be recovered.
* All important actions have real backend functionality.
* Installation and deployment progress is accurate.
* The application can be self-hosted.
* Additional game integrations can be added in the future.

## 28. IMPORTANT FINAL INSTRUCTIONS

This application is intended for an existing Arma 3 unit, not a commercial hosting service.

Do not waste development time building:

* Public hosting plan pages.
* Customer billing.
* Subscription management.
* Payment processing.
* Public hosting sales.
* A customer hosting marketplace.

Those features are not required.

Instead, spend development time on making mission management, server administration and operation preparation excellent.

The main priorities, in order, are:

1. Mission management: Uploading, editing, versioning, approving and deploying missions.
2. FASTER-style server management: Profiles, Workshop mods, SteamCMD, configurations and startup parameters.
3. Operation management: Allowing Zeus operators and mission makers to prepare and manage operations.
4. Server control: Starting, stopping, monitoring and maintaining the real dedicated server.
5. File management: Giving authorised members easy access to relevant mission and server files.
6. Automation: Reducing manual work for the administrators.
7. Future expansion: Keeping the architecture flexible enough to support other dedicated games.

Do not sacrifice functional mission management to add unnecessary commercial hosting features.

Do not create fake buttons, demonstration-only functionality or hardcoded server statistics.

Do not claim a server is online unless its actual process and health have been checked.

Do not claim a mission has been deployed until the correct file has been successfully written to the server.

Do not claim mods are installed until their files have been verified.

BEGIN DEVELOPMENT:

First, study the FASTER GitHub repository and documentation: https://github.com/Foxlider/FASTER

Then produce:

1. A technical architecture.
2. A complete project directory structure.
3. A database schema.
4. A frontend design for the main dashboard.
5. A backend and local server agent design.
6. A detailed implementation roadmap.

After that, start building Phase 1 and progressively implement the entire platform.

The end goal is a private Arma 3 group management website that combines the functionality of FASTER with collaborative mission development, mission uploads, Zeus operation management, browser-based file editing and complete dedicated server control.

The website should become the central place our entire Arma 3 group uses to prepare, organise, manage and run its operations.
