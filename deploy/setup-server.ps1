<#
13th Console: sets up the server box (the deploy guide's Parts C to F, and H).

Run it on the server, in PowerShell as Administrator:
    powershell -ExecutionPolicy Bypass -File C:\13thConsole\setup-server.ps1

Before you run it, C:\13thConsole holds panel\, agent\ and caddy\caddy.exe.

It's safe to run again: every step checks first, skips what's done, and deletes nothing.
- It sets up the folders, the arma-svc account, folder permissions, the panel, the firewall, Caddy, and the agent service (left stopped).
- At the end it asks for the agent key. Press Enter there to stop, and run it again once you have the key.

-WhatIf prints what it would do and changes nothing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\13thConsole',
    [string]$Domain,
    [string]$InstallRoot = 'C:\Arma3Server',
    [string]$DataRoot = 'C:\ArmaData',
    [ValidateRange(1024, 65530)][int]$GamePort = 2302
)

$ErrorActionPreference = 'Stop'
$DryRun = [bool]$WhatIfPreference
$WhatIfPreference = $false

$PanelService = '13th Console'
$AgentService = '13th Console Agent'
$CaddyService = 'caddy'
$AgentUser = 'arma-svc'
# Well-known SIDs, so the names work on any Windows language.
$Admins = '*S-1-5-32-544'
$System = '*S-1-5-18'
$LocalService = '*S-1-5-19'

function Say([string]$Text) { Write-Host $Text }
function Head([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Warn([string]$Text) { Write-Host "  ! $Text" -ForegroundColor Yellow }
function Stop-Setup([string]$Text) { Write-Host ''; Write-Host $Text -ForegroundColor Red; exit 1 }

# Does the change, or in a dry run only says what it would do.
function Step([string]$What, [scriptblock]$Action) {
    if ($DryRun) { Write-Host "  would: $What"; return }
    Write-Host "  $What"
    & $Action
}

# Runs icacls and friends; they report failure by exit code, not by a PowerShell error.
function Native([string]$Exe, [string[]]$ArgList) {
    $out = & $Exe @ArgList
    if ($LASTEXITCODE -ne 0) { $out | Out-Host; throw "$Exe failed (exit code $LASTEXITCODE)" }
}

function Plain([Security.SecureString]$Secure) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

function Read-NewPassword {
    while ($true) {
        $first = Read-Host -AsSecureString "  Choose a password for $AgentUser (write it down somewhere safe)"
        $second = Read-Host -AsSecureString '  Type it again'
        $a = Plain $first; $b = Plain $second
        $same = $a.Length -gt 0 -and $a -ceq $b
        $a = $null; $b = $null
        if ($same) { return $first }
        Warn "They didn't match, or were empty. Try again."
    }
}

# Writes the agent's connection settings and keeps everything else in the file.
function Set-AgentSettings([string]$Path, [string]$Key, [string]$InstallRoot, [string]$DataRoot) {
    $json = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    $json.Agent.PanelUrl = 'http://127.0.0.1:5080'
    $json.Agent.Key = $Key
    $json.Agent.InstallRoots = [object[]]@($InstallRoot)
    $json.Agent.DataRoot = $DataRoot
    [IO.File]::WriteAllText($Path, ($json | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
}

# "Log on as a service" for the agent's account. services.msc grants it when you pick an account
# there; New-Service doesn't, so the service would fail to start with a logon error.
$LsaSource = @'
using System;
using System.Runtime.InteropServices;
namespace Setup13 {
  public static class Lsa {
    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    const uint PolicyCreateAccount = 0x10, PolicyLookupNames = 0x800;
    public static int Grant(byte[] sid, string right) {
      var attributes = new ObjectAttributes();
      attributes.Length = Marshal.SizeOf(attributes);
      IntPtr policy;
      uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out policy);
      if (status != 0) return LsaNtStatusToWinError(status);
      try {
        var name = new UnicodeString();
        name.Buffer = Marshal.StringToHGlobalUni(right);
        name.Length = (ushort)(right.Length * 2);
        name.MaximumLength = (ushort)(name.Length + 2);
        try { status = LsaAddAccountRights(policy, sid, new[] { name }, 1); }
        finally { Marshal.FreeHGlobal(name.Buffer); }
        return status == 0 ? 0 : LsaNtStatusToWinError(status);
      } finally { LsaClose(policy); }
    }
  }
}
'@

function Grant-ServiceLogon([string]$User) {
    if (-not ('Setup13.Lsa' -as [type])) { Add-Type -TypeDefinition $LsaSource }
    $sid = (New-Object Security.Principal.NTAccount "$env:COMPUTERNAME\$User").Translate([Security.Principal.SecurityIdentifier])
    $bytes = New-Object byte[] $sid.BinaryLength
    $sid.GetBinaryForm($bytes, 0)
    $err = [Setup13.Lsa]::Grant($bytes, 'SeServiceLogonRight')
    if ($err -ne 0) { throw "Couldn't give $User 'Log on as a service' (Windows error $err)." }
}

function Wait-Running([string]$Name, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($s -and $s.Status -eq 'Running') { return $true }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Get-ServiceAccount([string]$Name) {
    $s = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    if ($s) { $s.StartName } else { $null }
}

# Phase 7a final review M18: whether a firewall rule already opens exactly these ports (order doesn't matter).
function Test-SamePorts([string[]]$Have, [string[]]$Want) {
    (@($Have | Sort-Object) -join ',') -eq (@($Want | Sort-Object) -join ',')
}

# Whether a service runs as one of the allowed accounts (Windows spells them in its own case, e.g. "NT Authority\LocalService").
function Test-RunsAs([string]$Actual, [string[]]$Allowed) {
    [bool]$Actual -and [bool]($Allowed | Where-Object { $_ -ieq $Actual })
}

# Caddy's admin API listens on localhost:2019 with no authentication, so anything on this box (say a mod's DLL running as arma-svc)
# could rewrite Caddy's config; it's switched off (Phase 6b final review C1). A config change then needs a service restart.
function Get-Caddyfile([string]$Domain) { "{`n`tadmin off`n}`n$Domain {`n`treverse_proxy 127.0.0.1:5080`n}`n" }

function Get-CaddyDomain([string]$Text) {
    foreach ($line in $Text -split "`n") { if ($line -match '^([a-z0-9][a-z0-9.-]*)\s*\{\s*$') { return $Matches[1] } }
    return $null
}

# A service that can't be given its own account is stopped and kept from starting with Windows, never left running as SYSTEM.
function Stop-Unsafe([string]$Name, [string]$Why) {
    Set-Service -Name $Name -StartupType Manual
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    Stop-Setup "$Why It's stopped and won't start with Windows until this is fixed. Run this script again; if it fails the same way, follow the guide by hand."
}

function Main {
    if ($DryRun) { Say 'Dry run: nothing will be changed.' }

    Head 'Checking'
    if (-not $DryRun) {
        $me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Stop-Setup 'Run this as Administrator: right-click the Start button, choose Terminal (Admin), and run the same command again.'
        }
        $os = Get-CimInstance Win32_OperatingSystem
        if ($os.ProductType -eq 1) {
            Warn "This computer runs $($os.Caption), a desktop Windows. This script is for the server box."
            $typed = Read-Host "  To carry on anyway, type this computer's name ($env:COMPUTERNAME). Press Enter to stop"
            if ($typed -ne $env:COMPUTERNAME) { Stop-Setup 'Stopped. Nothing was changed.' }
        }
    }
    $panelExe = Join-Path $Root 'panel\Panel.exe'
    $agentExe = Join-Path $Root 'agent\ConsoleAgent.exe'
    $agentSettings = Join-Path $Root 'agent\appsettings.json'
    $caddyExe = Join-Path $Root 'caddy\caddy.exe'
    $caddyfile = Join-Path $Root 'caddy\Caddyfile'
    $missing = @($panelExe, $agentExe, $agentSettings) | Where-Object { -not (Test-Path $_) }
    if ($missing) { Stop-Setup ("These are missing:`n  " + ($missing -join "`n  ") + "`nCopy the panel and agent folders into $Root, then run this again.") }
    if (-not (Test-Path $caddyExe)) {
        Stop-Setup "Caddy isn't there yet. Download it from https://caddyserver.com/download (Platform: Windows amd64) and save it as:`n  $caddyExe`nThen run this again."
    }
    Say "  Computer: $env:COMPUTERNAME. Console folder: $Root"

    Head 'Web address'
    if (-not $Domain -and (Test-Path $caddyfile)) {
        $found = Get-CaddyDomain ([IO.File]::ReadAllText($caddyfile))
        if ($found) { $Domain = $found; Say "  Using $Domain from the Caddyfile (pass -Domain to change it)." }
    }
    if (-not $Domain) { $Domain = Read-Host '  The console web address, for example console.your-domain.org' }
    $Domain = $Domain.Trim().ToLowerInvariant()
    if ($Domain -notmatch '^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$') {
        Stop-Setup "'$Domain' isn't a web address. Type just the name, like console.your-domain.org, with no https:// and no slash."
    }
    $ips = @()
    try { $ips = @(Resolve-DnsName -Name $Domain -Type A -DnsOnly | Where-Object { $_.Type -eq 'A' } | ForEach-Object { $_.IPAddress }) } catch { }
    $dnsReady = $false
    if ($ips.Count -eq 0) {
        Warn "$Domain doesn't point anywhere yet. Add the A record at your domain's site (guide Part B)."
    } else {
        Say "  $Domain points to $($ips -join ', ')"
        $dnsReady = $DryRun -or ((Read-Host "  Is that this box's public IP address? (y/n)") -match '^\s*y')
        if (-not $dnsReady) { Warn 'Then fix the A record at your domain site first.' }
    }

    Head 'Folders'
    foreach ($folder in @($InstallRoot, $DataRoot, (Join-Path $Root 'panel\data'))) {
        if (Test-Path $folder) { Say "  $folder is there" }
        else { Step "create $folder" { New-Item -ItemType Directory -Force -Path $folder | Out-Null } }
    }

    Head "Windows account for the agent ($AgentUser)"
    $user = Get-LocalUser -Name $AgentUser -ErrorAction SilentlyContinue
    $agentSvc = Get-Service -Name $AgentService -ErrorAction SilentlyContinue
    $password = $null
    if (-not $user) {
        if ($DryRun) { Say "  would: ask for a new password for $AgentUser" } else { $password = Read-NewPassword }
        Step "create the account $AgentUser" {
            try { New-LocalUser -Name $AgentUser -Password $password -PasswordNeverExpires -UserMayNotChangePassword -Description '13th Console agent: runs Arma 3' | Out-Null }
            catch { Stop-Setup "Windows refused that password: $($_.Exception.Message)`nPick a longer one with upper and lower case letters and a number, and run this again." }
        }
    } else {
        Say "  $AgentUser is there"
        if (-not $agentSvc) {
            if ($DryRun) { Say "  would: ask for the $AgentUser password" }
            else {
                $password = Read-Host -AsSecureString "  Type the $AgentUser password you chose before"
                if ($password.Length -eq 0) { Stop-Setup "No password typed. Run this again and type the $AgentUser password." }
            }
        }
    }
    Step "let $AgentUser run as a service ('Log on as a service')" { Grant-ServiceLogon $AgentUser }
    if (-not $agentSvc) {
        Step "install the '$AgentService' service (runs as $AgentUser; it starts once its key is in)" {
            $cred = New-Object Management.Automation.PSCredential (".\$AgentUser", $password)
            New-Service -Name $AgentService -BinaryPathName ('"' + $agentExe + '"') -StartupType Manual -Credential $cred -Description '13th Console agent: starts and stops Arma 3' | Out-Null
        }
    } else {
        Say "  the '$AgentService' service is there"
        # Never start Arma under another account: as LocalSystem every mod would run with SYSTEM's rights (final review I3).
        $agentAccounts = @(".\$AgentUser", "$env:COMPUTERNAME\$AgentUser")
        if (-not (Test-RunsAs (Get-ServiceAccount $AgentService) $agentAccounts)) {
            if ($DryRun) { Say "  would: ask for the $AgentUser password and make '$AgentService' run as $AgentUser" }
            else {
                $pw = Read-Host -AsSecureString "  '$AgentService' runs as '$(Get-ServiceAccount $AgentService)'. Type the $AgentUser password to switch it"
                $plain = Plain $pw
                Step "make '$AgentService' run as $AgentUser" {
                    $r = Invoke-CimMethod -Query "SELECT * FROM Win32_Service WHERE Name='$AgentService'" -MethodName Change -Arguments @{ StartName = ".\$AgentUser"; StartPassword = $plain }
                    if ($r.ReturnValue -ne 0 -or -not (Test-RunsAs (Get-ServiceAccount $AgentService) $agentAccounts)) {
                        Stop-Unsafe $AgentService "Couldn't make the agent run as $AgentUser (Windows error $($r.ReturnValue)); check the password."
                    }
                    if ((Get-Service -Name $AgentService).Status -eq 'Running') { Restart-Service -Name $AgentService }
                }
                $plain = $null
            }
        }
    }
    $password = $null

    Head 'Folder permissions'
    Step "lock $Root to administrators and the console's services" {
        Native icacls @($Root, '/inheritance:r', '/grant:r', "${Admins}:(OI)(CI)F", "${System}:(OI)(CI)F")
        Native icacls @((Join-Path $Root 'panel'), '/grant', "${LocalService}:(OI)(CI)RX")
        Native icacls @((Join-Path $Root 'panel\data'), '/grant', "${LocalService}:(OI)(CI)M")
        Native icacls @((Join-Path $Root 'agent'), '/grant', "${AgentUser}:(OI)(CI)RX")
    }
    Step "give $AgentUser the Arma folders, and keep everyone else out of $DataRoot" {
        Native icacls @($InstallRoot, '/grant', "${AgentUser}:(OI)(CI)M")
        Native icacls @($DataRoot, '/inheritance:r', '/grant:r', "${Admins}:(OI)(CI)F", "${System}:(OI)(CI)F", "${AgentUser}:(OI)(CI)M")
    }

    Head 'The console (panel)'
    $panelSvc = Get-Service -Name $PanelService -ErrorAction SilentlyContinue
    if (-not $panelSvc) {
        Step "install the '$PanelService' service" {
            New-Service -Name $PanelService -BinaryPathName ('"' + $panelExe + '"') -StartupType Manual -Description '13th Console web panel' | Out-Null
        }
    } else { Say "  the '$PanelService' service is there" }
    # Checked every run: New-Service makes it LocalSystem and Manual, and it only becomes Automatic once it reads back as Local
    # Service, so a failed change can't leave it starting as SYSTEM after a reboot. Windows reports "NT Authority\LocalService".
    $localService = @('NT AUTHORITY\LocalService', 'NT AUTHORITY\LOCAL SERVICE')
    $panelRestart = $false
    if (-not (Test-RunsAs (Get-ServiceAccount $PanelService) $localService)) {
        Step "make '$PanelService' run as Local Service" {
            $r = Invoke-CimMethod -Query "SELECT * FROM Win32_Service WHERE Name='$PanelService'" -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' }
            if ($r.ReturnValue -ne 0 -or -not (Test-RunsAs (Get-ServiceAccount $PanelService) $localService)) {
                Stop-Unsafe $PanelService "Couldn't make the panel run as Local Service (Windows error $($r.ReturnValue))."
            }
        }
        $panelRestart = $true
    }
    Step "let '$PanelService' start with Windows" { Set-Service -Name $PanelService -StartupType Automatic }
    $panelNow = Get-Service -Name $PanelService -ErrorAction SilentlyContinue
    $panelFresh = $false
    if ($panelNow -and $panelNow.Status -eq 'Running' -and -not $panelRestart) { Say '  it is running' }
    else {
        Step "start '$PanelService'" {
            if ($panelNow -and $panelNow.Status -eq 'Running') { Restart-Service -Name $PanelService } else { Start-Service -Name $PanelService }
            if (-not (Wait-Running $PanelService 20)) { Stop-Setup "The panel didn't start. Look in Event Viewer -> Windows Logs -> Application for its error." }
        }
        $panelFresh = $true
    }
    $setupToken = $null
    if (-not $DryRun) {
        $tokenFile = Join-Path $Root 'panel\data\setup-token.txt'
        if ($panelFresh) {
            $deadline = (Get-Date).AddSeconds(30)
            while (-not (Test-Path $tokenFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        }
        if (Test-Path $tokenFile) { $setupToken = ([IO.File]::ReadAllText($tokenFile)).Trim() }
    }

    Head 'Firewall'
    $rules = @(
        @{ Name = '13th Console HTTPS'; Protocol = 'TCP'; Ports = @('80', '443') },
        @{ Name = 'Arma 3 server'; Protocol = 'UDP'; Ports = @("$GamePort-$($GamePort + 4)") }
    )
    foreach ($rule in $rules) {
        $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
        if ($existing) {
            $have = @($existing | Get-NetFirewallPortFilter | ForEach-Object { $_.LocalPort })
            if (Test-SamePorts $have $rule.Ports) { Say "  '$($rule.Name)' is open" }
            else {
                # A re-run with another -GamePort: move the rule rather than leave the old ports open and the new ones shut.
                Step "change '$($rule.Name)' from $($have -join ', ') to $($rule.Ports -join ', ')" {
                    Set-NetFirewallRule -DisplayName $rule.Name -LocalPort $rule.Ports
                }
            }
        }
        else {
            Step "open $($rule.Protocol) $($rule.Ports -join ', ') ('$($rule.Name)')" {
                New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol $rule.Protocol -LocalPort $rule.Ports -Action Allow | Out-Null
            }
        }
    }
    Say "  If your hosting company has a firewall in its control panel, open the same ports there too."

    Head 'HTTPS (Caddy)'
    $wanted = Get-Caddyfile $Domain
    $current = if (Test-Path $caddyfile) { [IO.File]::ReadAllText($caddyfile) } else { $null }
    $caddyfileChanged = $current -ne $wanted
    if ($caddyfileChanged) {
        Step "write the Caddyfile for $Domain" { [IO.File]::WriteAllText($caddyfile, $wanted, (New-Object Text.UTF8Encoding $false)) }
    } else { Say "  the Caddyfile is there for $Domain" }
    $caddySvc = Get-Service -Name $CaddyService -ErrorAction SilentlyContinue
    if (-not $caddySvc) {
        # Manual until it's started for real, so a reboot can't start it before the domain points here.
        Step "install the '$CaddyService' service" {
            New-Service -Name $CaddyService -BinaryPathName ('"' + $caddyExe + '" run --config "' + $caddyfile + '"') -StartupType Manual -Description 'Caddy: HTTPS for 13th Console' | Out-Null
        }
    } else { Say "  the '$CaddyService' service is there" }
    # Caddy faces the internet, so it runs as its own virtual account, not SYSTEM (final review I4). It only reads its folder; its
    # certificates are expected under C:\Windows\ServiceProfiles\caddy (not seen yet: the dev PC has no virtual-account service).
    # Checked every run, like the panel's.
    $caddyAccount = "NT SERVICE\$CaddyService"
    $caddyRestart = $false
    if (-not (Test-RunsAs (Get-ServiceAccount $CaddyService) @($caddyAccount))) {
        Step "make '$CaddyService' run as $caddyAccount" {
            $r = Invoke-CimMethod -Query "SELECT * FROM Win32_Service WHERE Name='$CaddyService'" -MethodName Change -Arguments @{ StartName = $caddyAccount; StartPassword = '' }
            if ($r.ReturnValue -ne 0 -or -not (Test-RunsAs (Get-ServiceAccount $CaddyService) @($caddyAccount))) {
                Stop-Unsafe $CaddyService "Couldn't make Caddy run as $caddyAccount (Windows error $($r.ReturnValue))."
            }
        }
        $caddyRestart = $true
    }
    Step "let $caddyAccount read $(Join-Path $Root 'caddy')" { Native icacls @((Join-Path $Root 'caddy'), '/grant', "${caddyAccount}:(OI)(CI)RX") }
    $caddyRunning = $caddySvc -and $caddySvc.Status -eq 'Running'
    if (-not $dnsReady) {
        Warn "Caddy isn't started: $Domain has to point at this box first, or it can't get the padlock certificate."
    } elseif ($caddyRunning -and ($caddyfileChanged -or $caddyRestart)) {
        Step 'restart Caddy for the new address' { Restart-Service -Name $CaddyService }
    } elseif ($caddyRunning) { Say '  Caddy is running' }
    else {
        Step 'start Caddy (it gets the HTTPS certificate by itself)' {
            Set-Service -Name $CaddyService -StartupType Automatic
            Start-Service -Name $CaddyService
            if (-not (Wait-Running $CaddyService 15)) { Warn "Caddy didn't start. Look in Event Viewer -> Windows Logs -> Application." }
        }
    }
    $caddyNow = Get-Service -Name $CaddyService -ErrorAction SilentlyContinue
    if (-not $DryRun -and -not ($caddyNow -and $caddyNow.Status -eq 'Running')) {
        Head 'Next'
        Say "  The console can't be opened in a browser until Caddy runs. Once $Domain points at this box,"
        Say '  run this script again: it starts Caddy, then shows your setup code. Everything done so far is kept.'
        return
    }
    $siteUrl = "https://$Domain"

    $savedKey = ([IO.File]::ReadAllText($agentSettings) | ConvertFrom-Json).Agent.Key
    Head 'Your account'
    if ($setupToken -or $DryRun) {
        $shown = if ($DryRun) { '<the setup code>' } else { $setupToken }
        Say "  1. Open $siteUrl/setup (if it doesn't load yet, give Caddy a minute to get its certificate)"
        Say "  2. Paste this setup code (don't share it with anyone):"
        Write-Host "       $shown" -ForegroundColor Green
        Say '  3. Make the Owner account. The password needs 12 or more characters.'
        Say '  4. Turn on two-factor sign-in: scan the QR code with an authenticator app on your phone'
        Say '     (Microsoft Authenticator or Google Authenticator). Keep the recovery codes safe.'
        Say '  5. Go to Server -> Setup -> Add agent, and copy the key it shows.'
    } else {
        Say "  No setup code: the Owner account is already made. Sign in at $siteUrl."
        if (-not $savedKey) { Say '  Then go to Server -> Setup -> Add agent, and copy the key it shows.' }
    }

    if ($DryRun) { Say ''; Say '  would: ask for the agent key, save it, and start the agent'; return }
    Say ''
    $prompt = if ($savedKey) { '  Paste a new agent key, or press Enter to keep the saved one' } else { '  Paste the agent key here (right-click pastes), or press Enter to stop for now' }
    $key = (Plain (Read-Host -AsSecureString $prompt)).Trim()
    if (-not $key -and -not $savedKey) {
        Say ''
        Say 'Stopped before the agent. Run this script again once you have the key: it skips everything already done.'
        return
    }
    if ($key) {
        if ($key -notmatch '^[0-9a-f]{32}\.[A-Za-z0-9_-]{43}$') { Stop-Setup "That doesn't look like an agent key. Copy it again from Server -> Setup (New key gives you another), and run this again." }
        Step 'save the key in the agent settings' { Set-AgentSettings $agentSettings $key $InstallRoot $DataRoot }
    }
    $key = $null
    Step "start '$AgentService'" {
        Set-Service -Name $AgentService -StartupType Automatic
        try {
            if ((Get-Service -Name $AgentService).Status -eq 'Running') { Restart-Service -Name $AgentService } else { Start-Service -Name $AgentService }
        } catch { Warn "Windows says: $($_.Exception.Message)" }
        if (-not (Wait-Running $AgentService 15)) {
            Stop-Setup "The agent didn't start. Open services.msc -> 13th Console Agent -> Log On -> This account: .\$AgentUser and its password -> OK, then right-click it and Start. If it still stops, look in Event Viewer -> Windows Logs -> Application."
        }
    }

    Head 'Done'
    Say "  In the console ($siteUrl), Server -> Setup shows the agent as Connected within a few seconds."
    Say "  Register the install it reports ($InstallRoot), then carry on with the guide's Part I: install Arma 3 and the mods."
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Main }
    catch {
        Write-Host ''
        Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'Running this script again is safe: it skips what is already done.'
        Write-Host 'If you ask for help, send this message, but never the setup code, the agent key or a password.'
        exit 1
    }
}
