# Checks update-server.ps1 without changing this computer: ASCII only, it parses, the copy keeps the box's
# appsettings.json and data folder, the kept copy of the old version leaves the data out, and a dry run
# (-WhatIf) changes nothing.
#   powershell -ExecutionPolicy Bypass -File deploy\check-update-script.ps1
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'update-server.ps1'
$failed = 0
function Check([string]$Name, [bool]$Ok) { if ($Ok) { Write-Host "ok   $Name" } else { Write-Host "FAIL $Name" -ForegroundColor Red; $script:failed++ } }
function Put([string]$Path, [string]$Text) { New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null; [IO.File]::WriteAllText($Path, $Text) }
function Text([string]$Path) { if (Test-Path $Path) { [IO.File]::ReadAllText($Path) } else { $null } }

$bytes = [IO.File]::ReadAllBytes($script)
Check 'only ASCII bytes' (-not ($bytes | Where-Object { $_ -gt 127 }))
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
Check 'parses' ($errors.Count -eq 0)

. $script

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("13c-update-check-" + [guid]::NewGuid().ToString('N'))
try {
    # The box as it is: the old version, its own settings, its data.
    $box = Join-Path $tmp 'console'
    Put "$box\panel\Panel.exe" 'v1'
    Put "$box\panel\appsettings.json" 'box settings'
    Put "$box\panel\data\console.db" 'the database'
    Put "$box\panel\old.dll" 'left over'
    # The update: the new version, with the settings file it was built with.
    $new = Join-Path $tmp 'update'
    Put "$new\panel\Panel.exe" 'v2'
    Put "$new\panel\appsettings.json" 'shipped settings'
    Put "$new\panel\wwwroot\app.js" 'new page'

    $kept = Join-Path $tmp 'kept\panel'
    Copy-Part "$box\panel" $kept
    Check 'kept copy: has the old version' ((Text "$kept\Panel.exe") -eq 'v1')
    Check 'kept copy: leaves the data out' (-not (Test-Path "$kept\data"))

    Copy-Part "$new\panel" "$box\panel"
    Check 'update: new files in' ((Text "$box\panel\Panel.exe") -eq 'v2' -and (Text "$box\panel\wwwroot\app.js") -eq 'new page')
    Check "update: the box's appsettings.json is kept" ((Text "$box\panel\appsettings.json") -eq 'box settings')
    Check 'update: the data folder is untouched' ((Text "$box\panel\data\console.db") -eq 'the database')
    Check 'update: nothing is deleted' ((Text "$box\panel\old.dll") -eq 'left over')

    Copy-Part $kept "$box\panel"
    Check 'put back: the old version returns' ((Text "$box\panel\Panel.exe") -eq 'v1' -and (Text "$box\panel\data\console.db") -eq 'the database')

    # A dry run against a copy of the update changes nothing on the box.
    Put "$box\panel\Panel.exe" 'v1'
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -WhatIf -Root $box -From $new | Out-String
    Check 'dry run exits cleanly' ($LASTEXITCODE -eq 0)
    Check 'dry run changes nothing' ((Text "$box\panel\Panel.exe") -eq 'v1' -and -not (Test-Path "$box\update-backups"))
    Check "dry run stops, copies and starts the panel" ($out -match "would: stop '13th Console'" -and $out -match 'would: copy in the new panel' -and $out -match "would: start '13th Console'")
    Check 'dry run leaves the agent alone when the update has none' ($out -notmatch 'Agent')
    if ($failed) { Write-Host $out }

    # Seen on the box (2026-11-21): run as the guide says, with no -From, it must find the update next to itself.
    Copy-Item $script "$new\update-server.ps1"
    $own = & powershell -NoProfile -ExecutionPolicy Bypass -File "$new\update-server.ps1" -WhatIf -Root $box | Out-String
    Check 'without -From it updates from its own folder' ($LASTEXITCODE -eq 0 -and $own -match 'would: copy in the new panel')
    if ($LASTEXITCODE -ne 0) { Write-Host $own }

    $none = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -WhatIf -Root $box -From (Join-Path $tmp 'kept-nothing') | Out-String
    Check 'an empty update folder is refused' ($LASTEXITCODE -ne 0 -and $none -match 'Nothing to update')
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

if ($failed) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all checks passed'
