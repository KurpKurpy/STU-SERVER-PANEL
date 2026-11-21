# Checks setup-server.ps1 without changing this computer: ASCII only (Windows PowerShell 5.1 reads
# BOM-less UTF-8 as ANSI), it parses, its C# compiles, the settings writer keeps the rest of the
# file, and a full dry run (-WhatIf) gets to the end.
#   powershell -ExecutionPolicy Bypass -File deploy\check-setup-script.ps1
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'setup-server.ps1'
$failed = 0
function Check([string]$Name, [bool]$Ok) { if ($Ok) { Write-Host "ok   $Name" } else { Write-Host "FAIL $Name" -ForegroundColor Red; $script:failed++ } }

$bytes = [IO.File]::ReadAllBytes($script)
Check 'only ASCII bytes' (-not ($bytes | Where-Object { $_ -gt 127 }))

$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
Check 'parses' ($errors.Count -eq 0)

. $script
Add-Type -TypeDefinition $LsaSource
Check 'the Log on as a service code compiles' ([bool]('Setup13.Lsa' -as [type]))

# Phase 6b final review C1: Caddy's admin API (localhost:2019, no authentication) is switched off in the Caddyfile we write.
$caddyfileText = Get-Caddyfile 'console.example.org'
Check 'Caddyfile turns the admin API off' ($caddyfileText -match '(?m)^\{\s*$' -and $caddyfileText -match '(?m)^\s*admin off\s*$')
Check 'Caddyfile proxies to the panel' ($caddyfileText -match 'console\.example\.org \{\s+reverse_proxy 127\.0\.0\.1:5080')
Check 'domain read back from our Caddyfile' ((Get-CaddyDomain $caddyfileText) -eq 'console.example.org')
Check 'domain read back from an older Caddyfile' ((Get-CaddyDomain "old.example.org {`n`treverse_proxy 127.0.0.1:5080`n}`n") -eq 'old.example.org')

# I3/I4: one gate decides whether a service may start under the account it has.
Check 'gate: arma-svc either way' ((Test-RunsAs '.\arma-svc' @('.\arma-svc', 'BOX\arma-svc')) -and (Test-RunsAs 'box\ARMA-SVC' @('.\arma-svc', 'BOX\arma-svc')))
Check 'gate: SYSTEM refused' (-not (Test-RunsAs 'LocalSystem' @('.\arma-svc')) -and -not (Test-RunsAs $null @('.\arma-svc')))
Check 'gate: Windows spelling of Local Service' (Test-RunsAs 'NT Authority\LocalService' @('NT AUTHORITY\LocalService'))

# Phase 7a final review M18: a re-run with another -GamePort moves the game's firewall rule instead of calling the old one open.
Check 'firewall: a rule for another game port is noticed' (-not (Test-SamePorts @('2302-2306') @('2100-2104')))
Check 'firewall: the same ports in any order match' ((Test-SamePorts @('443', '80') @('80', '443')) -and (Test-SamePorts @('2100-2104') @('2100-2104')))

# Four servers (Friday, Sunday, Training, Dev) open their blocks in one run, however the list is typed.
Check 'game ports: one port' ((@(Get-GamePorts '2302') -join ',') -eq '2302-2306')
Check 'game ports: four, with commas' ((@(Get-GamePorts '2100,2200,2300,2400') -join ',') -eq '2100-2104,2200-2204,2300-2304,2400-2404')
Check 'game ports: spaces, repeats and any order' ((@(Get-GamePorts '2400 2100, 2100') -join ',') -eq '2100-2104,2400-2404')
Check 'game ports: nonsense refused' ($null -eq (Get-GamePorts 'abc') -and $null -eq (Get-GamePorts '80') -and $null -eq (Get-GamePorts ''))

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("13c-setup-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $settings = Join-Path $tmp 'appsettings.json'
    Copy-Item (Join-Path $PSScriptRoot '..\src\Agent\appsettings.json') $settings
    $key = ('0' * 32) + '.' + ('A' * 43)
    Set-AgentSettings $settings $key 'C:\Arma3Server' 'C:\ArmaData'
    $j = [IO.File]::ReadAllText($settings) | ConvertFrom-Json
    Check 'settings: panel address' ($j.Agent.PanelUrl -eq 'http://127.0.0.1:5080')
    Check 'settings: key' ($j.Agent.Key -eq $key)
    Check 'settings: InstallRoots stays a list' (([IO.File]::ReadAllText($settings) -replace '\s', '') -match '"InstallRoots":\["C:\\\\Arma3Server"\]')
    Check 'settings: data folder' ($j.Agent.DataRoot -eq 'C:\ArmaData')
    Check 'settings: the rest is kept' ($j.Agent.StopTimeoutSeconds -eq 60 -and $j.Logging.LogLevel.'Microsoft.AspNetCore.SignalR' -eq 'Warning')
    Check 'key pattern takes a real key' ($key -match '^[0-9a-f]{32}\.[A-Za-z0-9_-]{43}$')

    $root = Join-Path $tmp 'console'
    foreach ($f in 'panel\Panel.exe', 'agent\ConsoleAgent.exe', 'caddy\caddy.exe') { New-Item -ItemType File -Force -Path (Join-Path $root $f) | Out-Null }
    Copy-Item (Join-Path $PSScriptRoot '..\src\Agent\appsettings.json') (Join-Path $root 'agent\appsettings.json')
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -WhatIf -Root $root -Domain 'console.example.invalid' -InstallRoot (Join-Path $tmp 'arma') -DataRoot (Join-Path $tmp 'data') -GamePort 2100,2200,2300,2400 | Out-String
    Check 'dry run exits cleanly' ($LASTEXITCODE -eq 0)
    Check 'dry run opens all four game port blocks' ($out -match 'UDP 2100-2104, 2200-2204, 2300-2304, 2400-2404')
    Check 'dry run changes nothing' (-not (Test-Path (Join-Path $root 'caddy\Caddyfile')) -and -not (Test-Path (Join-Path $tmp 'arma')))
    Check 'dry run reaches the end' ($out -match 'would: ask for the agent key')
    Check 'dry run shows the setup address' ($out -match 'Open https://console.example.invalid/setup ')
    if ($failed) { Write-Host $out }
} finally { Remove-Item -Recurse -Force $tmp }

if ($failed) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all checks passed'
