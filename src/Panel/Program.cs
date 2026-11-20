using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Audit;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Account;
using Panel.Features.Auth;
using Panel.Features.Roles;
using Panel.Features.Members;
using Panel.Features.Invites;
using Panel.Features.Announcements;
using Panel.Features.Activity;
using Panel.Features.Settings;
using Panel.Features.Agents;
using Panel.Features.Profiles;
using Panel.Features.Servers;
using Panel.Features.Jobs;
using Panel.Features.Mods;
using Panel.Hubs;

// Phase 7b: `Panel.exe restore <zip>` swaps a console backup in, with the service stopped, and exits.
if (args is ["restore", var restoreZip])
{
    var restoreConfig = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
    Environment.ExitCode = Panel.Features.Backups.ConsoleRestore.Run(restoreZip,
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, restoreConfig["DataDir"] ?? "data")));
    return;
}

// As a Windows service the working directory is System32, so the content root (and with it DataDir) is the exe's folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "13th Console");
var config = builder.Configuration;

var paths = new PanelPaths(Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, config["DataDir"] ?? "data")));
Directory.CreateDirectory(paths.DataDir);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SetupState>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<Panel.Settings.AppSettingsService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();

builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(paths.DataDir, "console.db")}"));

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentKeyAuthentication>(AgentKeyAuthentication.SchemeName, null);
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .AddHubOptions<AgentHub>(o => o.MaximumReceiveMessageSize = 1_048_576); // a 256 KiB file chunk is ~350 KB as JSON
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentCalls>();
builder.Services.AddScoped<ServerEndpoints.Control>();
builder.Services.AddScoped<Panel.Features.Profiles.ProfileStore>();
builder.Services.AddSingleton<JobSecrets>();
builder.Services.AddSingleton<Panel.Storage.BlobStore>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<Panel.Features.Missions.MissionVersions>();
builder.Services.AddScoped<Panel.Features.Missions.MissionDeployments>();
builder.Services.AddScoped<IJobResultHandler, ModJobHandler>();
builder.Services.AddScoped<IJobResultHandler, Panel.Features.Backups.BackupJobHandler>(); // Phase 7b
builder.Services.AddSingleton<Panel.Features.Backups.ConsoleBackups>();
builder.Services.AddScoped<Panel.Features.Backups.BackupService>();
builder.Services.AddSingleton<Panel.Features.Schedules.ScheduleGate>();
builder.Services.AddScoped<Panel.Features.Schedules.ScheduleRunner>();
builder.Services.AddHostedService<Panel.Features.Schedules.Scheduler>();
builder.Services.AddScoped<IJobResultHandler, InstallScanHandler>();
builder.Services.AddScoped<IJobResultHandler, Panel.Features.Notifications.UpdateFailedNotifications>();
builder.Services.AddScoped<AddonScans>();
builder.Services.AddSingleton<Panel.Features.Files.FileTransfers>();
builder.Services.AddSingleton<Panel.Features.Files.FileChanges>();
builder.Services.AddHttpClient<IWorkshopClient, WorkshopClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<Panel.Features.Players.RconLog>();
builder.Services.AddSingleton<Panel.Features.Notifications.Notifier>();
builder.Services.AddHostedService<Panel.Features.Notifications.NotificationWorker>();
builder.Services.AddHostedService<Panel.Features.Notifications.ReminderService>();
builder.Services.AddScoped<Panel.Features.Notifications.DiscordSettings>();
Panel.Features.Notifications.DiscordWebhook.Register(builder.Services);
builder.Services.AddSingleton<UiConnections>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UiConnections>());
builder.Services.AddAuthorization();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "13c.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
    o.ValidationInterval = TimeSpan.FromSeconds(config.GetValue("Auth:StampValidationSeconds", 60)));

builder.Services.AddIdentityCore<User>(o =>
    {
        o.Password.RequiredLength = 12;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequiredUniqueChars = 1;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.";
        o.User.RequireUniqueEmail = false;
    })
    .AddRoles<Role>()
    .AddEntityFrameworkStores<AppDb>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("13th-console")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDir, "keys")));
// Machine scope (design §5.2): the key ring stays readable when the panel moves from an interactive run to a service account.
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

var authPerMinute = config.GetValue("RateLimit:AuthPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    await Seeder.SeedAsync(scope.ServiceProvider);
    app.Services.GetRequiredService<Panel.Features.Files.FileTransfers>().CleanAtStart(); // uploads a stopped panel never finished
    await Panel.Features.Profiles.ProfileUpgrade.RunAsync(db, scope.ServiceProvider.GetRequiredService<Panel.Features.Profiles.ProfileStore>(),
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), app.Logger);
    await JobService.InterruptOpenJobsAsync(db, scope.ServiceProvider.GetServices<IJobResultHandler>());
    await app.Services.GetRequiredService<Panel.Features.Backups.ConsoleBackups>().SweepAsync(db);
    if (!await db.Users.AnyAsync())
    {
        var token = app.Services.GetRequiredService<SetupState>().Issue(paths);
        app.Logger.LogWarning("First run: open /setup and enter setup token {Token} (also saved to {File})", token, paths.SetupTokenFile);
    }
}

// Behind Caddy on the same box: trust X-Forwarded-For/-Proto only from loopback proxies (the default KnownProxies).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

// Pre-deploy checklist (headers, HTTPS): no framing, no MIME sniffing, no referrer, scripts only from the panel. Styles allow inline
// because Monaco injects them; workers allow blob: for Monaco. HSTS once Caddy says the visitor came over HTTPS.
const string contentPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; "
    + "worker-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = contentPolicy;
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});

// Same-origin cookies plus a custom header no other site can send without CORS: blocks cross-site form posts.
app.Use(async (ctx, next) =>
{
    var method = ctx.Request.Method;
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method) && ctx.Request.Headers["X-Console"] != "1")
    {
        await ApiError.Of(400, "header_required", "Requests must come from the console app.").ExecuteAsync(ctx);
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TwoFactorGate>();
app.UseRateLimiter();

app.MapAuth();
app.MapAccount();
app.MapRoles();
app.MapMembers();
app.MapInvites();
app.MapAnnouncements();
app.MapActivity();
app.MapSettings();
app.MapAgents();
app.MapProfiles();
app.MapRevisions();
app.MapApply();
app.MapPresets();
app.MapServerPresets();
app.MapJobs();
app.MapMods();
Panel.Features.Backups.BackupEndpoints.MapBackups(app); // Phase 7b
Panel.Features.Schedules.ScheduleEndpoints.MapSchedules(app);
Panel.Features.Missions.MissionEndpoints.MapMissions(app);
Panel.Features.Missions.WorkingCopyEndpoints.MapWorkingCopy(app);
Panel.Features.Missions.VersionEndpoints.MapVersions(app);
Panel.Features.Missions.MissionDeployments.MapDeployments(app);
Panel.Features.Missions.ServerMissionsEndpoints.MapServerMissions(app);
Panel.Features.Missions.StartMission.MapStartMission(app);
app.MapCollections();
app.MapInstallTools();
app.MapServers();
Panel.Features.Files.FileEndpoints.MapFiles(app);
Panel.Features.Operations.OperationEndpoints.MapOperations(app);
Panel.Features.Notifications.NotificationEndpoints.MapNotifications(app);
Panel.Features.Notifications.NotificationSettingsEndpoints.MapNotificationSettings(app);
Panel.Features.Players.PlayerEndpoints.MapPlayers(app);
app.MapHub<AgentHub>("/hubs/agent");
app.MapHub<UiHub>("/hubs/ui");

app.MapFallback("/api/{**rest}", () => ApiError.Of(404, "not_found", "No such API route"));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
