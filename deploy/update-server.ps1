<#
13th Console: puts a new version of the console on the server box.

Unzip the update anywhere (it holds panel\ and/or agent\ next to this script), then in Terminal (Admin):
    powershell -ExecutionPolicy Bypass -File <that folder>\update-server.ps1

For each part the update holds, it:
- stops that part's service and keeps the current version (and the console's database) in C:\13thConsole\update-backups\<time>;
- copies the new files in, never appsettings.json or the data folder, and deletes nothing;
- starts the service again and checks the console answers. If it doesn't, the previous version goes back.
A running Arma server keeps running: the agent finds it again when it starts.

-WhatIf prints what it would do and changes nothing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\13thConsole',
    [string]$From
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell leaves $PSScriptRoot empty in a param default when run with -File (seen on the box), so the folder is found here.
if (-not $From) { $From = Split-Path -Parent $MyInvocation.MyCommand.Path }
$DryRun = [bool]$WhatIfPreference
$WhatIfPreference = $false

$Parts = [ordered]@{
    panel = @{ Service = '13th Console'; Exe = 'Panel.exe' }
    agent = @{ Service = '13th Console Agent'; Exe = 'ConsoleAgent.exe' }
}

function Say([string]$Text) { Write-Host $Text }
function Head([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Stop-Update([string]$Text) { Write-Host ''; Write-Host $Text -ForegroundColor Red; exit 1 }
function Step([string]$What, [scriptblock]$Action) {
    if ($DryRun) { Write-Host "  would: $What"; return }
    Write-Host "  $What"
    & $Action
}

# Copies a part's files over another copy: never its data folder or appsettings.json (the box's own settings), and deletes nothing.
function Copy-Part([string]$Source, [string]$Target) {
    $out = & robocopy $Source $Target /E /XD (Join-Path $Source 'data') /XF appsettings.json appsettings.Development.json /R:2 /W:2 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { $out | Out-Host; throw "Copying $Source to $Target failed (robocopy exit code $LASTEXITCODE)." }
}

function Wait-State([string]$Name, [string]$State, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        if ((Get-Service -Name $Name).Status -eq $State) { return $true }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Test-Panel([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try { if ((Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5080/' -TimeoutSec 5).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Main {
    if ($DryRun) { Say 'Dry run: nothing will be changed.' }
    $from = [IO.Path]::GetFullPath($From).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $todo = @($Parts.Keys | Where-Object { Test-Path (Join-Path $from "$_\$($Parts[$_].Exe)") })
    if ($todo.Count -eq 0) { Stop-Update "Nothing to update in $from. Unzip the update so its panel or agent folder sits next to this script." }
    if ($from -ieq $root) { Stop-Update "Unzip the update into its own folder, not into $root itself." }
    if (-not $DryRun) {
        $me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Stop-Update 'Run this as Administrator: right-click the Start button, choose Terminal (Admin), and run the same command again.'
        }
    }
    $kept = Join-Path $root ('update-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

    foreach ($part in $todo) {
        $service = $Parts[$part].Service
        $target = Join-Path $root $part
        Head "The $part ($service)"
        if (-not (Test-Path (Join-Path $target $Parts[$part].Exe))) { Stop-Update "$target isn't installed. Run setup-server.ps1 first." }
        if (-not $DryRun -and -not (Get-Service -Name $service -ErrorAction SilentlyContinue)) { Stop-Update "There's no '$service' service. Run setup-server.ps1 first." }

        # Seen on the box: the console takes up to ~30 s to stop while browsers and the agent are connected, and Stop-Service waits
        # (printing "Waiting for service...") without a limit. So it's asked without waiting, given 90 s, then closed if still going.
        Step "stop '$service' (up to a minute while browsers disconnect)" {
            Stop-Service -Name $service -Force -NoWait
            if (-not (Wait-State $service 'Stopped' 90)) {
                $procId = (Get-CimInstance Win32_Service -Filter "Name='$service'").ProcessId
                if ($procId -gt 0) { Write-Host "  ! '$service' is taking too long to stop; closing it." -ForegroundColor Yellow; Stop-Process -Id $procId -Force }
                if (-not (Wait-State $service 'Stopped' 30)) { Stop-Update "'$service' didn't stop. Nothing was changed; try again in a minute." }
            }
        }
        Step "keep the current $part in $kept\$part" { Copy-Part $target (Join-Path $kept $part) }
        if ($part -eq 'panel') {
            Step "keep a copy of the console's database in $kept\panel-data" {
                New-Item -ItemType Directory -Force -Path (Join-Path $kept 'panel-data') | Out-Null
                Get-ChildItem -Path (Join-Path $target 'data') -Filter 'console.db*' -File | Copy-Item -Destination (Join-Path $kept 'panel-data')
            }
        }
        Step "copy in the new $part" { Copy-Part (Join-Path $from $part) $target }
        Step "start '$service'" {
            Start-Service -Name $service
            $ok = (Wait-State $service 'Running' 30) -and ($part -ne 'panel' -or (Test-Panel 60))
            if (-not $ok) {
                Write-Host "  ! The new $part didn't start. Putting the previous version back." -ForegroundColor Yellow
                Stop-Service -Name $service -Force -ErrorAction SilentlyContinue
                [void](Wait-State $service 'Stopped' 60)
                Copy-Part (Join-Path $kept $part) $target
                Start-Service -Name $service
                Stop-Update "The previous $part is back and running. Look in Event Viewer -> Windows Logs -> Application for why the new one failed, and send that message."
            }
        }
    }

    Head 'Done'
    Say "  Updated: $($todo -join ', '). The previous version is kept in $kept (delete old ones there whenever you like)."
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Main }
    catch {
        Write-Host ''
        Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'If a service is stopped, start it again in services.msc. The previous version is kept in C:\13thConsole\update-backups.'
        exit 1
    }
}
