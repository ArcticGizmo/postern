<#
.SYNOPSIS
  Every Sallyport scenario, headless, against a throwaway Postern (Sandbox): fake-upstream -> sandbox Postern ->
  sallyport --run-all. Uses its own pipe (postern-testbed-run) and data folder (%TEMP%\postern-testbed-run), so it
  never touches a test bed you have open. Needs Debug builds (dev auto-approve only exists there):
    dotnet build src\Postern.App; dotnet build tools\Postern.FakeUpstream; dotnet build tools\Postern.Sallyport
#>
param([switch]$KeepRunning)

# Not 'Stop': in Windows PowerShell 5.1 any stderr from a native exe becomes a terminating error.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$postern = Join-Path $root 'src\Postern.App\bin\Debug\net10.0-windows\Postern.exe'
$fake = Join-Path $root 'tools\Postern.FakeUpstream\bin\Debug\net10.0\fake-upstream.exe'
$sallyport = Join-Path $root 'tools\Postern.Sallyport\bin\Debug\net10.0-windows\sallyport.exe'
$pipe = 'postern-testbed-run'
$data = Join-Path $env:TEMP 'postern-testbed-run'

foreach ($exe in $postern, $fake, $sallyport) { if (-not (Test-Path $exe)) { throw "Missing $exe. Build it first (see the top of this script)." } }

function Answering {
    try { (Invoke-WebRequest 'http://127.0.0.1:5599/_knobs' -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200 } catch { $false }
}

# A fresh sandbox every run: no approvals left over, and the seeded connections and credentials.
if (Test-Path (Join-Path $data 'providers.json')) { Remove-Item $data -Recurse -Force }

# An open test bed's fake-upstream is the same thing, so use it rather than fight it for the port.
$upstream = $null
if (-not (Answering)) { $upstream = Start-Process $fake -PassThru -WindowStyle Hidden }
$broker = Start-Process $postern -ArgumentList '--sandbox', '--headless', '--pipe', $pipe, '--data-dir', "`"$data`"", '--dev-auto-approve', 'sallyport' -PassThru -WindowStyle Hidden

try {
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Answering) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    # sallyport is a WinExe: -Wait and -NoNewWindow keep its output here and give us its exit code.
    $run = Start-Process $sallyport -ArgumentList '--run-all', '--pipe', $pipe -NoNewWindow -Wait -PassThru
    $code = $run.ExitCode
}
finally {
    if (-not $KeepRunning) {
        Stop-Process -Id $broker.Id -Force -ErrorAction SilentlyContinue
        if ($upstream) { Stop-Process -Id $upstream.Id -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
if ($code -eq 0) { Write-Host 'testbed: all scenarios passed' -ForegroundColor Green } else { Write-Host "testbed: sallyport exited $code" -ForegroundColor Red }
exit $code
