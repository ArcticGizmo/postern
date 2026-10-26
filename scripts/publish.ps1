<#
.SYNOPSIS
  Builds a release locally, the same way .github/workflows/release.yml does: tests, Postern + postern-cli published
  into one folder, a Velopack pack, and SHA256SUMS.txt. Output goes to releases\ (git-ignored).

  For trying an install, or an update, before anything is on GitHub:
    .\scripts\publish.ps1 0.1.1
    .\releases\Postern-win-Setup.exe            # installs to %LOCALAPPDATA%\Postern and starts it
    .\scripts\publish.ps1 0.1.2                 # a newer version into the same releases\ folder
    # Quit Postern, then start the installed copy with the local folder as its update feed:
    $env:POSTERN_UPDATE_FEED = "$PWD\releases"; & "$env:LOCALAPPDATA\Postern\current\Postern.exe"
    # Tray -> Check for updates now offers 0.1.2.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$SkipTests
)

# Not 'Stop': in Windows PowerShell 5.1 any stderr from a native exe becomes a terminating error. Exit codes are
# checked instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot

function Step([string]$what) { Write-Host "== $what" -ForegroundColor Cyan }
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." } }

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' isn't MAJOR.MINOR.PATCH (Velopack needs SemVer)." }
# vpk refuses a version at or below one already in releases\, but only after the build. Say so before it.
if (Test-Path (Join-Path $root "releases\Postern-$Version-full.nupkg")) {
    throw "releases\ already has $Version. Use a higher version, or delete releases\ to start the local feed again."
}

Push-Location $root
try {
    # The tool manifest is found from the current directory, which is why this script runs from the repo root.
    Step 'restoring the pinned vpk'
    dotnet tool restore
    Check 'dotnet tool restore'

    if (-not $SkipTests) {
        Step 'tests'
        dotnet test postern.slnx -c Release -nologo -v q
        Check 'dotnet test'
    }

    Step "publishing $Version"
    if (Test-Path publish) { Remove-Item publish -Recurse -Force }
    foreach ($project in 'src\Postern.App\Postern.App.csproj', 'src\Postern.Cli\Postern.Cli.csproj') {
        dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o publish -nologo -v q
        Check "dotnet publish $project"
    }

    Step 'packing'
    # Start menu only: a tray broker has no business on the desktop.
    dotnet vpk pack --packId Postern --packTitle Postern --packVersion $Version --packDir publish --mainExe Postern.exe --shortcuts StartMenuRoot --icon src\Postern.App\Assets\postern.ico --outputDir releases
    Check 'vpk pack'

    # Same format as the workflow's (sha256sum: lower-case hex, two spaces, LF), so `sha256sum -c` accepts it.
    # releases\ keeps older versions' nupkgs between local runs, so only this version's are listed.
    Step 'checksums'
    $lines = Get-ChildItem -File releases | Where-Object {
        $_.Name -ne 'SHA256SUMS.txt' -and ($_.Extension -ne '.nupkg' -or $_.Name.StartsWith("Postern-$Version-", [StringComparison]::OrdinalIgnoreCase))
    } | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    [IO.File]::WriteAllText((Join-Path $root 'releases\SHA256SUMS.txt'), ($lines -join "`n") + "`n")
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "Release $Version is in $(Join-Path $root 'releases')" -ForegroundColor Green
Write-Host '  Install:  .\releases\Postern-win-Setup.exe'
