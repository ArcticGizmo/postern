<#
.SYNOPSIS
  C2 + C3 against a fake upstream: Octokit.NET, Node, and the Pylon-shaped poller (across a Postern crash).
  Own pipe (postern-consumers), data dir (%TEMP%\postern-consumers) and provider (fakegh). Debug build required:
    dotnet build postern.slnx; dotnet build spikes\consumers\consumers.slnx
  For the real thing against GitHub, see README.md ("Against real GitHub").
#>
$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$postern = Join-Path $root 'src\Postern.App\bin\Debug\net10.0-windows\Postern.exe'
$cli = Join-Path $root 'src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe'
$fake = Join-Path $root 'tools\Postern.FakeUpstream\bin\Debug\net10.0\fake-upstream.exe'
$octokit = Join-Path $PSScriptRoot 'Octokit\bin\Debug\net10.0-windows\octokit-spike.exe'
$poller = Join-Path $PSScriptRoot 'Poller\bin\Debug\net10.0-windows\poller-spike.exe'
$pipe = 'postern-consumers'
$data = Join-Path $env:TEMP 'postern-consumers'
$failed = 0
function Section($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Result($ok, $what) { if ($ok) { Write-Host "  [PASS] $what" -ForegroundColor Green } else { Write-Host "  [FAIL] $what" -ForegroundColor Red; $script:failed++ } }

New-Item -ItemType Directory -Force $data | Out-Null
'{ "providers": [ { "name": "fakegh", "baseUrl": "http://127.0.0.1:5599" } ] }' | Out-File -Encoding ascii (Join-Path $data 'providers.json')
'consumer-secret-0123456789' | & $cli credential set fakegh --data-dir $data | Out-Null
Remove-Item (Join-Path $env:LOCALAPPDATA "Postern.Client\*.$pipe*") -ErrorAction SilentlyContinue

$upstream = Start-Process $fake -ArgumentList '5599' -PassThru -WindowStyle Hidden
$supervisor = Start-Process $postern -ArgumentList '--supervise', '--headless', '--pipe', $pipe, '--data-dir', "`"$data`"",
    '--dev-auto-approve', 'octokit-spike,poller-spike,node-spike' -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(15)
do { Start-Sleep -Milliseconds 300; & $cli status --pipe $pipe *> $null } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)

try {
    Section 'C2: Octokit.NET'
    & $octokit --provider fakegh --repo example/repo --pipe $pipe
    Result ($LASTEXITCODE -eq 0) 'octokit-spike'

    Section 'C2: Node (http socketPath)'
    node (Join-Path $PSScriptRoot 'node\demo.mjs') --provider fakegh --pipe $pipe
    Result ($LASTEXITCODE -eq 0) 'node demo'

    Section 'C3: poller across a Postern crash'
    $out = Join-Path $env:TEMP 'postern-consumers-poller.txt'
    $p = Start-Process $poller -ArgumentList '--provider', 'fakegh', '--repo', 'example/repo', '--interval', '2', '--count', '8', '--pipe', $pipe `
        -PassThru -NoNewWindow -RedirectStandardOutput $out
    $null = $p.Handle # without touching Handle early, ExitCode reads as null once the process has exited
    Start-Sleep -Seconds 5
    $brokerPid = [regex]::Match((& $cli status --pipe $pipe | Out-String), 'pid (\d+)').Groups[1].Value
    Write-Host "  killing Postern pid $brokerPid mid-poll"
    Stop-Process -Id $brokerPid -Force
    $p.WaitForExit()
    Get-Content $out | ForEach-Object { "    $_" }
    $text = Get-Content $out -Raw
    Result ($p.ExitCode -eq 0) 'poller finished every poll despite the crash'
    Result ($text -match '304 not modified') 'saw 304s (conditional requests passed through)'
}
finally {
    $brokerPid = [regex]::Match((& $cli status --pipe $pipe 2>$null | Out-String), 'pid (\d+)').Groups[1].Value
    Stop-Process -Id $supervisor.Id -Force -ErrorAction SilentlyContinue
    if ($brokerPid) { Stop-Process -Id $brokerPid -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $upstream.Id -Force -ErrorAction SilentlyContinue
    & $cli credential delete fakegh | Out-Null
}

Write-Host ''
if ($failed -eq 0) { Write-Host 'consumers: all passed' -ForegroundColor Green; exit 0 }
Write-Host "consumers: $failed failed" -ForegroundColor Red; exit 1
