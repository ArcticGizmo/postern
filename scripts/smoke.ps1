<#
.SYNOPSIS
  End-to-end smoke test with the real exes: fake upstream -> supervised headless Postern -> postern-cli.
  Uses its own pipe (postern-smoke), data dir (%TEMP%\postern-smoke) and provider (fakegh), so it never touches a
  Postern you're running. Needs a Debug build (dev auto-approve only exists there): dotnet build postern.slnx
#>
param([switch]$KeepRunning)

# Not 'Stop': in Windows PowerShell 5.1 any stderr from a native exe becomes a terminating error, and postern-cli
# writes its refusals to stderr on purpose.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$postern = Join-Path $root 'src\Postern.App\bin\Debug\net10.0-windows\Postern.exe'
$cli = Join-Path $root 'src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe'
$fake = Join-Path $root 'tools\Postern.FakeUpstream\bin\Debug\net10.0\fake-upstream.exe'
$pipe = 'postern-smoke'
$data = Join-Path $env:TEMP 'postern-smoke'
$failed = 0

function Check([bool]$ok, [string]$what, [string]$detail = '') {
    if ($ok) { Write-Host "  [PASS] $what $detail" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $what $detail" -ForegroundColor Red; $script:failed++ }
}
trap { Write-Host "  [FAIL] unexpected: $_" -ForegroundColor Red; $script:failed++; continue }
function Pst { & $cli @args --pipe $pipe --name smoke 2>&1 | ForEach-Object { "$_" } | Out-String }

foreach ($exe in $postern, $cli, $fake) { if (-not (Test-Path $exe)) { throw "Missing $exe. Run: dotnet build postern.slnx" } }

New-Item -ItemType Directory -Force $data | Out-Null
'{ "providers": [ { "name": "fakegh", "baseUrl": "http://127.0.0.1:5599" } ] }' | Out-File -Encoding ascii (Join-Path $data 'providers.json')
'smoke-secret-0123456789' | & $cli credential set fakegh --data-dir $data | Out-Null

$upstream = Start-Process $fake -ArgumentList '5599' -PassThru -WindowStyle Hidden
$supervisor = Start-Process $postern -ArgumentList '--supervise', '--headless', '--pipe', $pipe, '--data-dir', "`"$data`"", '--dev-auto-approve', 'smoke' -PassThru -WindowStyle Hidden

try {
    Write-Host "== waiting for Postern on \\.\pipe\$pipe"
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 300; & $cli status --pipe $pipe --name smoke *> $null } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)
    Check ($LASTEXITCODE -eq 0) 'Postern is up'

    Write-Host "== register and call"
    & $cli forget --pipe $pipe --name smoke | Out-Null
    $out = Pst register --scope 'GET fakegh/**' --scope 'POST fakegh/graphql'
    Check ($LASTEXITCODE -eq 0 -and $out -match 'Approved') 'register (dev auto-approve)'

    $out = Pst call GET fakegh/echo?x=1
    $echo = $out | ConvertFrom-Json
    Check ($echo.path -eq '/echo' -and $echo.headers.Authorization -like 'Bearer smo*') 'GET through Postern with injected auth' "(upstream saw $($echo.headers.Authorization))"
    Check ($null -eq $echo.headers.'Postern-App-Token') 'app token not forwarded'

    $out = Pst call POST fakegh/graphql --data '{"query":"{ viewer { login } }"}'
    Check ($out -match '"login":"fake"') 'POST graphql'

    $out = Pst call DELETE fakegh/echo
    Check ($LASTEXITCODE -eq 3 -and $out -match 'scope-denied') 'out-of-scope request refused' "(exit $LASTEXITCODE)"

    $out = Pst call GET fakegh/redirect -i
    Check ($out -match 'HTTP 302') 'redirect passed back, not followed'

    $out = Pst call GET fakegh/repos/o/r/pulls -i
    $etag = ($out -split "`r?`n" | Where-Object { $_ -like 'ETag:*' }) -replace '^ETag:\s*', ''
    # Windows PowerShell 5.1 strips embedded double quotes from native arguments unless they're backslash-escaped.
    $out = Pst call GET fakegh/repos/o/r/pulls -i -H ('If-None-Match: ' + $etag.Replace('"', '\"'))
    Check ($out -match 'HTTP 304') 'conditional request returns 304' "(ETag $etag)"
    Check ($out -match 'Postern-RateLimit-Remaining') 'rate-limit headers surfaced'

    Write-Host "== crash and restart"
    $status = Pst status
    $before = [regex]::Match($status, 'pid (\d+)').Groups[1].Value
    Stop-Process -Id $before -Force
    $out = Pst call GET fakegh/echo --wait 15   # dead pipe for a moment; the supervisor restarts Postern while the CLI waits
    Check ($LASTEXITCODE -eq 0) 'request during restart succeeded' "(exit $LASTEXITCODE)"
    $after = [regex]::Match((Pst status), 'pid (\d+)').Groups[1].Value
    Check ($after -and $after -ne $before) 'supervisor restarted Postern' "(pid $before -> $after)"
    $out = Pst status
    Check ($out -match "'smoke' is approved") 'registration survived the restart (SQLite)'

    Write-Host "== audit"
    $db = Join-Path $data 'postern.db'
    Check (Test-Path $db) 'audit database exists' "($db)"
    $log = Get-ChildItem (Join-Path $data 'logs') -Filter 'postern-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check ($null -ne $log -and (Get-Content $log.FullName -Raw) -match 'DEV AUTO-APPROVE') 'log file records the dev auto-approve' "($($log.FullName))"
}
finally {
    if (-not $KeepRunning) {
        $pid2 = [regex]::Match((& $cli status --pipe $pipe 2>$null | Out-String), 'pid (\d+)').Groups[1].Value
        Stop-Process -Id $supervisor.Id -Force -ErrorAction SilentlyContinue
        if ($pid2) { Stop-Process -Id $pid2 -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id $upstream.Id -Force -ErrorAction SilentlyContinue
        & $cli credential delete fakegh | Out-Null
    }
}

Write-Host ''
if ($failed -eq 0) { Write-Host 'smoke: all passed' -ForegroundColor Green; exit 0 }
Write-Host "smoke: $failed failed" -ForegroundColor Red; exit 1
