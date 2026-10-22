<#
.SYNOPSIS
  C1 wiring check: Perch's own Postern transport (registration, token, alerts fetch, scope denial) against a
  throwaway Postern with a fake GitHub provider. Never touches your real GitHub credential (provider "fakegh").
.PARAMETER PerchRoot
  The Perch checkout with the Postern transport (default: the sibling worktree ..\perch-postern).
#>
param([string]$PerchRoot = (Join-Path $PSScriptRoot '..\..\..\perch-postern'))

$ErrorActionPreference = 'Continue'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$postern = Join-Path $root 'src\Postern.App\bin\Debug\net10.0-windows\Postern.exe'
$cli = Join-Path $root 'src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe'
$fake = Join-Path $root 'tools\Postern.FakeUpstream\bin\Debug\net10.0\fake-upstream.exe'
$pipe = 'postern-perch-e2e'
$data = Join-Path $env:TEMP 'postern-perch-e2e'

New-Item -ItemType Directory -Force $data | Out-Null
'{ "providers": [ { "name": "fakegh", "baseUrl": "http://127.0.0.1:5599" } ] }' | Out-File -Encoding ascii (Join-Path $data 'providers.json')
'perch-e2e-secret-0123456789' | & $cli credential set fakegh --data-dir $data | Out-Null
Remove-Item (Join-Path $env:LOCALAPPDATA "Postern.Client\perch.$pipe.token") -ErrorAction SilentlyContinue

$upstream = Start-Process $fake -ArgumentList '5599' -PassThru -WindowStyle Hidden
$broker = Start-Process $postern -ArgumentList '--headless', '--pipe', $pipe, '--data-dir', "`"$data`"", '--dev-auto-approve', 'perch' -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(15)
do { Start-Sleep -Milliseconds 300; & $cli status --pipe $pipe *> $null } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)

try {
    $env:PERCH_POSTERN_E2E = '1'
    $env:POSTERN_PIPE = $pipe
    $env:PERCH_POSTERN_PROVIDER = 'fakegh'
    dotnet test (Join-Path $PerchRoot 'tests\Perch.Tests\Perch.Tests.csproj') --filter 'FullyQualifiedName~PosternTransportE2ETests' -nologo -v q
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:PERCH_POSTERN_E2E, Env:POSTERN_PIPE, Env:PERCH_POSTERN_PROVIDER -ErrorAction SilentlyContinue
    Stop-Process -Id $broker.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $upstream.Id -Force -ErrorAction SilentlyContinue
    & $cli credential delete fakegh | Out-Null
}
Write-Host "Postern audit/log for this run: $data"
exit $code
