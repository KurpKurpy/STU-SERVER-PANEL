namespace Panel.Auth;

public record PermissionDef(string Key, string Group, string Label);

public static class Permissions
{
    public const string MembersView = "members.view";
    public const string MembersManage = "members.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditView = "audit.view";
    public const string AnnouncementsManage = "announcements.manage";
    public const string ServerView = "server.view";
    public const string AgentsManage = "agents.manage";
    public const string ServerControl = "server.control";
    public const string ServerForceStop = "server.forceStop";
    public const string ProfilesManage = "profiles.manage";
    public const string LogsView = "logs.view";
    public const string ProfilesView = "profiles.view";
    public const string ProfilesApply = "profiles.apply";
    public const string ServerViewJoinInfo = "server.viewJoinInfo";
    public const string ModsView = "mods.view";
    public const string ModsManage = "mods.manage";
    public const string ModsInstall = "mods.install";
    public const string CollectionsManage = "collections.manage";
    public const string KeysManage = "keys.manage";
    public const string ServerUpdate = "server.update";
    public const string MissionsView = "missions.view";
    public const string MissionsDownload = "missions.download";
    public const string MissionsCreate = "missions.create";
    public const string MissionsEditOwn = "missions.editOwn";
    public const string MissionsEditAny = "missions.editAny";
    public const string MissionsStart = "missions.start";
    // Phase 4b: the file manager, one read and (where the space can be written) one write permission per space (design §7).
    public const string FilesMpmissionsRead = "files.mpmissions.read", FilesMpmissionsWrite = "files.mpmissions.write";
    public const string FilesKeysRead = "files.keys.read", FilesKeysWrite = "files.keys.write";
    public const string FilesProfilesRead = "files.profiles.read", FilesProfilesWrite = "files.profiles.write";
    public const string FilesLocalmodsRead = "files.localmods.read", FilesLocalmodsWrite = "files.localmods.write";
    public const string FilesConfigsRead = "files.configs.read", FilesWorkshopRead = "files.workshop.read", FilesBackupsRead = "files.backups.read";
    // Phase 6a: operations (design §7; plan Ruling 4).
    public const string OperationsView = "operations.view", OperationsManage = "operations.manage", OperationsPrepare = "operations.prepare",
        OperationsApply = "operations.apply";
    public const string PlayersManage = "players.manage", ServerConsole = "server.console";
    public const string BackupsManage = "backups.manage", SchedulesManage = "schedules.manage"; // Phase 7b

    public const string ClaimPerm = "perm";
    public const string ClaimGrant = "perm.grant";
    public const string ClaimDeny = "perm.deny";

    public const string OwnerRole = "Owner";

    public static readonly IReadOnlyList<PermissionDef> All =
    [
        new(MembersView, "Members", "See the member roster and roles"),
        new(MembersManage, "Members", "Invite, edit and disable members"),
        new(RolesManage, "Members", "Create and edit roles"),
        new(AnnouncementsManage, "Content", "Post and edit announcements"),
        new(AuditView, "Admin", "Read the audit log"),
        new(SettingsManage, "Admin", "Change unit settings"),
        new(ServerView, "Server", "See server status"),
        new(ServerViewJoinInfo, "Server", "See the server's join password"),
        new(ServerControl, "Server", "Start, stop and restart the server"),
        new(ServerForceStop, "Server", "Force-stop the server"),
        new(LogsView, "Server", "See the live console and server logs"),
        new(ProfilesView, "Server", "See server profiles and their settings (passwords stay hidden)"),
        new(ProfilesManage, "Server", "Set up server profiles"),
        new(ProfilesApply, "Server", "Write profile settings onto the server"),
        new(AgentsManage, "Server", "Pair server agents and register installs"),
        new(ServerUpdate, "Server", "Install and update the Arma 3 server through SteamCMD"),
        new(ModsView, "Mods", "See the mod library and mod lists"),
        new(ModsManage, "Mods", "Add and remove mods in the library"),
        new(ModsInstall, "Mods", "Download, update and reinstall mods"),
        new(CollectionsManage, "Mods", "Create and edit mod lists"),
        new(KeysManage, "Mods", "Deploy and clean up the server's mod keys"),
        new(MissionsView, "Missions", "See the mission library"),
        new(MissionsDownload, "Missions", "Download mission PBOs and files"),
        new(MissionsCreate, "Missions", "Upload new missions"),
        new(MissionsEditOwn, "Missions", "Edit missions they made or collaborate on"),
        new(MissionsEditAny, "Missions", "Edit any mission"),
        new(MissionsStart, "Missions", "Start a mission the server can run (sets the rotation and mod list, then starts or restarts the server)"),
        new(FilesMpmissionsRead, "Files", "See and download files in the server's mpmissions folder"),
        new(FilesMpmissionsWrite, "Files", "Upload, edit, move and delete files in the server's mpmissions folder"),
        new(FilesKeysRead, "Files", "See and download the server's mod keys"),
        new(FilesKeysWrite, "Files", "Upload and delete the server's mod keys"),
        new(FilesProfilesRead, "Files", "See and download server profile folders: logs, BattlEye (includes the RCon password)"),
        new(FilesProfilesWrite, "Files", "Upload, edit, move and delete files in server profile folders"),
        new(FilesLocalmodsRead, "Files", "See and download local mod folders"),
        new(FilesLocalmodsWrite, "Files", "Upload, unzip, move and delete local mods"),
        new(FilesConfigsRead, "Files", "See and download the generated server.cfg and basic.cfg (includes server passwords)"),
        new(FilesWorkshopRead, "Files", "See and download Workshop mod folders"),
        new(FilesBackupsRead, "Files", "See and download backups (includes old copies of server passwords)"),
        new(OperationsView, "Operations", "See operations, their briefing and readiness"),
        new(OperationsManage, "Operations", "Plan and edit operations (also sees mission notes)"),
        new(OperationsPrepare, "Operations", "Run an operation's readiness check and tick its manual items (also sees mission notes)"),
        new(OperationsApply, "Operations", "Apply a ready operation to its server (sets the mission and mod list, then starts or restarts it)"),
        new(PlayersManage, "Server", "Kick, ban and message players; see their IP and GUID, the ban list and the RCon log"),
        new(ServerConsole, "Server", "Send RCon commands from the allowed list (also needs players.manage: the replies show IPs and GUIDs)"),
        new(BackupsManage, "Admin", "Make, download, restore and delete backups (they hold passwords and keys)"),
        new(SchedulesManage, "Admin", "Create and run schedules (restarts, updates, backups)"),
    ];

    public static bool Exists(string key) => All.Any(p => p.Key == key);

    // The unit's staff sections (S-shop roles design, 2026-11-20). S1 and S4 look after the server; S1 gets everything but pairing agents.
    public static readonly IReadOnlyDictionary<string, string[]> SystemRoleDefaults = new Dictionary<string, string[]>
    {
        [OwnerRole] = [],
        ["S1"] = [.. All.Select(p => p.Key).Where(k => k != AgentsManage)],
        ["S2"] = [MembersView, ServerView, ServerViewJoinInfo, MissionsView, MissionsDownload, OperationsView, OperationsPrepare],
        ["S3"] = [MembersView, ServerView, ServerViewJoinInfo, LogsView, ProfilesView, ModsView, MissionsView, MissionsDownload, MissionsCreate,
            MissionsEditOwn, MissionsStart, FilesMpmissionsRead, FilesMpmissionsWrite, OperationsView, OperationsManage, OperationsPrepare,
            OperationsApply, PlayersManage],
        ["S4"] = [MembersView, ServerView, ServerViewJoinInfo, ServerControl, ServerForceStop, ServerUpdate, LogsView, ProfilesView, ProfilesManage,
            ProfilesApply, ModsView, ModsManage, ModsInstall, CollectionsManage, KeysManage, MissionsView, MissionsDownload, MissionsStart,
            FilesMpmissionsRead, FilesMpmissionsWrite, FilesKeysRead, FilesKeysWrite, FilesProfilesRead, FilesProfilesWrite,
            FilesLocalmodsRead, FilesLocalmodsWrite, FilesConfigsRead, FilesWorkshopRead, FilesBackupsRead, OperationsView, PlayersManage, ServerConsole,
            BackupsManage, SchedulesManage],
        ["S6"] = [MembersView, ServerView, ServerViewJoinInfo, MissionsView, MissionsDownload, OperationsView],
        ["S7"] = [MembersView, ServerView, ServerViewJoinInfo, ModsView, MissionsView, MissionsDownload, MissionsStart, OperationsView,
            OperationsManage, OperationsPrepare],
        ["Member"] = [ServerView, ServerViewJoinInfo, MissionsView, MissionsDownload, OperationsView],
    };

    public static readonly IReadOnlyDictionary<string, string> SystemRoleDescriptions = new Dictionary<string, string>
    {
        [OwnerRole] = "Head developer", ["S1"] = "Unit leadership", ["S2"] = "Section leaders", ["S3"] = "Mission makers and Zeus",
        ["S4"] = "Development", ["S6"] = "Sheets and bot infrastructure", ["S7"] = "Trainers (FTXs)", ["Member"] = "Everyone in the unit",
    };
}
