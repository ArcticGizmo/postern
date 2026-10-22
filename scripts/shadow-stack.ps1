<#
.SYNOPSIS
  Starts a small "stack" of postern-shadow apps against the Postern you're running, so several registrations arrive
  at once (the approval queue), and then each app compares gh and Postern every minute in the background.

  Each app runs from its own copy under out\shadow\, so Postern sees separate programs. shadow-api and
  shadow-api-jobs share one copy, to show two names from one exe. Logs: %LOCALAPPDATA%\Postern.Shadow\logs.
.EXAMPLE
  .\scripts\shadow-stack.ps1 -Repo octo-org/octo-repo     # build, copy, start (repos are optional; they add pulls/runs probes)
  .\scripts\shadow-stack.ps1 -Summary                     # same / DIFFERENT counts per app, and the latest differences
  .\scripts\shadow-stack.ps1 -Stop
  .\scripts\shadow-stack.ps1 -Fresh                       # forget the shadow apps' tokens first, so they all ask again
#>
param([string[]]$Repo = @(), [int]$Every = 60, [switch]$Summary, [switch]$Stop, [switch]$Fresh)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'tools\Postern.Shadow\Postern.Shadow.csproj'
$built = Join-Path $root 'tools\Postern.Shadow\bin\Debug\net10.0-windows'
$out = Join-Path $root 'out\shadow'
$logs = Join-Path $env:LOCALAPPDATA 'Postern.Shadow\logs'
$tokens = Join-Path $env:LOCALAPPDATA 'Postern.Client'

function Running { Get-Process postern-shadow -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out\*" } }

if ($Summary) {
    $files = Get-ChildItem $logs -Filter 'shadow-*.log' -ErrorAction SilentlyContinue
    if (-not $files) { Write-Host "No logs in $logs yet."; exit 0 }
    foreach ($file in $files) {
        $same = (Select-String $file.FullName -Pattern ': same \(').Count
        $different = (Select-String $file.FullName -Pattern ': DIFFERENT \(').Count
        $colour = 'Green'
        if ($different -gt 0) { $colour = 'Yellow' }
        Write-Host ("{0,-18} same {1,5}   DIFFERENT {2,4}" -f $file.BaseName, $same, $different) -ForegroundColor $colour
        Select-String $file.FullName -Pattern ': DIFFERENT \(' -Context 0, 2 | Select-Object -Last 3 | ForEach-Object {
            Write-Host "    $($_.Line)"
            $_.Context.PostContext | ForEach-Object { Write-Host "    $_" }
        }
        $last = Get-Content $file.FullName -Tail 1
        Write-Host "    last: $last" -ForegroundColor DarkGray
    }
    $count = @(Running).Count
    Write-Host "$count shadow app(s) running."
    exit 0
}

if ($Stop -or (Running)) {
    $stopped = @(Running)
    $stopped | Stop-Process -Force
    Write-Host "Stopped $($stopped.Count) shadow app(s)."
    if ($Stop) { exit 0 }
}

$apps = @(
    @{ Name = 'shadow-web'; Copy = 'web'; Probes = @('user', 'my-prs') },
    @{ Name = 'shadow-api'; Copy = 'api'; Probes = @('review-requests') },
    @{ Name = 'shadow-api-jobs'; Copy = 'api'; Probes = @('user') }
)
if ($Repo.Count -gt 0) {
    $apps += @{ Name = 'shadow-worker'; Copy = 'worker'; Probes = @($Repo | ForEach-Object { "pulls:$_"; "runs:$_" }) }
}
else {
    Write-Host 'No -Repo given, so shadow-worker (pulls and runs probes) is skipped.' -ForegroundColor DarkGray
}

Write-Host '== building postern-shadow'
dotnet build $project -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if ($Fresh) {
    foreach ($app in $apps) { Remove-Item (Join-Path $tokens "$($app.Name).token") -ErrorAction SilentlyContinue }
    Write-Host 'Forgot the shadow apps'' tokens: each one will ask Postern again.'
}

foreach ($copy in $apps.Copy | Select-Object -Unique) {
    $dest = Join-Path $out $copy
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $built '*') $dest -Recurse -Force
}

Write-Host '== starting (approve each in Postern; they queue in the order they arrive)'
foreach ($app in $apps) {
    $exe = Join-Path (Join-Path $out $app.Copy) 'postern-shadow.exe'
    $arguments = @('--name', $app.Name, '--every', $Every) + @($app.Probes | ForEach-Object { '--probe'; $_ })
    $process = Start-Process $exe -ArgumentList $arguments -WindowStyle Minimized -PassThru
    Write-Host ("  {0,-16} pid {1,-6} {2}" -f $app.Name, $process.Id, ($app.Probes -join ', '))
}
Write-Host ''
Write-Host "Logs: $logs. Check with: .\scripts\shadow-stack.ps1 -Summary"
