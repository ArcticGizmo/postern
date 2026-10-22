<#
.SYNOPSIS
    Tests for install.ps1 - the Windows one-liner installer.

.DESCRIPTION
    install.ps1 can't be covered by the xUnit suite, so its logic is exercised here instead. Run it after
    touching install.ps1 (CI runs it on every pull request):

        powershell -NoProfile -File tools\test-install.ps1

    It loads install.ps1's functions with the entrypoint call stripped, then drives them directly. The
    download path is tested against a real loopback HttpListener serving a real artifact out of releases\ -
    no network, no mocking of the HTTP stack - so run scripts\publish.ps1 at least once first (those checks
    skip otherwise). Nothing here talks to github.com, so the live release lookup and the installer actually
    running are still manual checks.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$installPs1 = Join-Path $root 'install.ps1'
$src = (Get-Content $installPs1 -Raw) -replace '(?m)^Install-Postern\s+.*$', ''
# Dot-source a temp copy with the entrypoint call stripped, so its functions land in this scope without
# evaluating a string as code. A try block is not a new scope, so the functions survive it.
$loader = Join-Path ([System.IO.Path]::GetTempPath()) ("postern-install-under-test-" + [guid]::NewGuid().ToString('N') + '.ps1')
[System.IO.File]::WriteAllText($loader, $src)
try { . $loader }
finally { Remove-Item -LiteralPath $loader -Force -ErrorAction SilentlyContinue }

$pass = 0; $fail = 0
function Check($name, $cond, $detail = '') {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}
function CheckThrows($name, [scriptblock]$sb, [string]$expect) {
    try { & $sb; Check $name $false 'did not throw' }
    catch { Check $name ($_.Exception.Message -like "*$expect*") "message was: $($_.Exception.Message)" }
}

Write-Host "`n=== Encoding and parse (whole release pipeline) ===" -ForegroundColor Cyan
# None of these carry a BOM, so Windows PowerShell 5.1 decodes .ps1 files as the system codepage rather than
# UTF-8. A UTF-8 em dash then arrives as three Windows-1252 chars ending in 0x94 = U+201D - a curly closing
# quote, which PowerShell honours as a string delimiter, so one em dash inside a "..." string silently
# mis-parses everything after it. The whole pipeline is held to ASCII: one rule, and legible console output
# under any codepage. Globbed, so a new script or workflow is covered without anyone remembering to add it.
$pipeline = @('install.ps1') +
@(Get-ChildItem -File -LiteralPath (Join-Path $root 'scripts') -Filter *.ps1 | ForEach-Object { "scripts\$($_.Name)" }) +
@(Get-ChildItem -File -LiteralPath (Join-Path $root 'tools') -Filter *.ps1 | ForEach-Object { "tools\$($_.Name)" }) +
@(Get-ChildItem -File -LiteralPath (Join-Path $root '.github\workflows') |
    Where-Object { $_.Extension -in '.yml', '.yaml' } | ForEach-Object { ".github\workflows\$($_.Name)" }) |
Sort-Object -Unique
foreach ($rel in $pipeline) {
    $path = Join-Path $root $rel
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $offender = [System.IO.File]::ReadAllLines($path) |
        Where-Object { $_.ToCharArray() | Where-Object { [int]$_ -gt 127 } } | Select-Object -First 1
    $ok = @($bytes | Where-Object { $_ -gt 127 }).Count -eq 0 -and
    -not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    Check "$rel is pure ASCII, no BOM" $ok "first offending line: $offender"

    if ($rel -like '*.ps1') {
        $perr = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$perr)
        Check "$rel parses with no errors" ($perr.Count -eq 0) `
        (($perr | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; ')
    }
}

Write-Host "`n=== Get-ExpectedHash ===" -ForegroundColor Cyan
$h = 'a' * 64
# Both sha256sum output styles appear in practice: two spaces (text mode, what Linux/CI emits) and a `*`
# prefix (binary mode, what Git Bash emits locally).
$sums = @"
$h  Postern-win-Setup.exe
$('b' * 64)  Postern-win-Portable.zip
$('c' * 64) *Postern-0.1.0-full.nupkg
"@
Check 'finds the setup entry' ((Get-ExpectedHash -Sums $sums -Name 'Postern-win-Setup.exe' -Tag v1) -eq $h.ToUpperInvariant())
Check 'finds a binary-mode (*) entry' ((Get-ExpectedHash -Sums $sums -Name 'Postern-0.1.0-full.nupkg' -Tag v1) -eq ('c' * 64).ToUpperInvariant())
Check 'does not prefix-match a different asset' ((Get-ExpectedHash -Sums $sums -Name 'Postern-win-Portable.zip' -Tag v1) -eq ('b' * 64).ToUpperInvariant())
Check 'handles CRLF manifests' ((Get-ExpectedHash -Sums "$h  Postern-win-Setup.exe`r`n" -Name 'Postern-win-Setup.exe' -Tag v1) -eq $h.ToUpperInvariant())
# The fail-closed cases. A manifest we can't find our entry in must never be treated as "nothing to check".
CheckThrows 'throws when the file is absent from the manifest' { Get-ExpectedHash -Sums $sums -Name 'Nope.exe' -Tag 'v1.2.3' } 'no entry for Nope.exe'
CheckThrows 'throws on an empty manifest' { Get-ExpectedHash -Sums '' -Name 'Postern-win-Setup.exe' -Tag 'v1' } 'no entry for'
CheckThrows 'throws on an HTML error page as the manifest' { Get-ExpectedHash -Sums '<html>404</html>' -Name 'Postern-win-Setup.exe' -Tag 'v1' } 'no entry for'

Write-Host "`n=== Manifest decoding (byte[] vs string response) ===" -ForegroundColor Cyan
# GitHub serves release assets as application/octet-stream, and Invoke-WebRequest on Windows PowerShell 5.1
# then hands back a byte[] .Content, not a string. The decode in install.ps1 is what makes the checksum lookup
# work at all; the third check proves an undecoded byte[] would match no hash line.
function Decode($raw) { if ($raw -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($raw) } else { [string]$raw } }
$asString = "$h  Postern-win-Setup.exe`n"
$asBytes = [System.Text.Encoding]::UTF8.GetBytes($asString)
Check 'an already-decoded string parses' ((Get-ExpectedHash -Sums (Decode $asString) -Name 'Postern-win-Setup.exe' -Tag v1) -eq $h.ToUpperInvariant())
Check 'byte[] content (what GitHub actually returns) parses the same' ((Get-ExpectedHash -Sums (Decode $asBytes) -Name 'Postern-win-Setup.exe' -Tag v1) -eq $h.ToUpperInvariant())
Check 'an undecoded byte[] would have failed, so the decode is required' `
(-not (($asBytes -split "`r?`n") -match '^\s*[0-9a-fA-F]{64}\s'))

Write-Host "`n=== Get-AssetUrl ===" -ForegroundColor Cyan
$rel = [pscustomobject]@{
    tag_name = 'v9.9.9'; html_url = 'https://example/rel'
    assets   = @([pscustomobject]@{ name = 'Postern-win-Setup.exe'; browser_download_url = 'https://example/setup' })
}
Check 'resolves a present asset' ((Get-AssetUrl -Release $rel -Name 'Postern-win-Setup.exe') -eq 'https://example/setup')
CheckThrows 'explains a release with no SHA256SUMS.txt' { Get-AssetUrl -Release $rel -Name 'SHA256SUMS.txt' } 'has no SHA256SUMS.txt asset'

Write-Host "`n=== Test-Elevated ===" -ForegroundColor Cyan
# Whichever way this shell is running, the check has to agree with Windows' own answer.
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Check "reports this shell as $(if ($elevated) { 'elevated' } else { 'not elevated' })" ((Test-Elevated) -eq $elevated)

Write-Host "`n=== Save-File + verification (real HTTP, real artifact) ===" -ForegroundColor Cyan
# The largest file in releases\, so the chunked read loop and the progress throttle actually run.
$payload = Get-ChildItem -File (Join-Path $root 'releases') -ErrorAction SilentlyContinue |
    Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Length -Descending | Select-Object -First 1
if (-not $payload) {
    Write-Host '  SKIP  releases\ is empty - run scripts\publish.ps1 first to exercise the download path.' -ForegroundColor Yellow
}
else {
    $bytes = [System.IO.File]::ReadAllBytes($payload.FullName)
    $realHash = (Get-FileHash -LiteralPath $payload.FullName -Algorithm SHA256).Hash
    Write-Host "  serving $($payload.Name) ($('{0:N1}' -f ($bytes.Length / 1MB)) MB)" -ForegroundColor DarkGray

    $port = 18743
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://localhost:$port/")
    $listener.Start()
    $server = [powershell]::Create()
    $null = $server.AddScript({
            param($listener, $bytes)
            while ($listener.IsListening) {
                try { $ctx = $listener.GetContext() } catch { break }
                if ($ctx.Request.Url.AbsolutePath -eq '/truncated') {
                    # Advertise the full length, send half, then kill the connection: the shape of a download
                    # cut short. Save-File must fail however that surfaces - reset, short read, or timeout.
                    $ctx.Response.ContentLength64 = $bytes.Length
                    $ctx.Response.OutputStream.Write($bytes, 0, [int]($bytes.Length / 2))
                    try { $ctx.Response.Abort() } catch { }
                    continue
                }
                $ctx.Response.ContentLength64 = $bytes.Length
                $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                try { $ctx.Response.OutputStream.Close() } catch { }
                try { $ctx.Response.Close() } catch { }
            }
        }).AddArgument($listener).AddArgument($bytes)
    $null = $server.BeginInvoke()

    $tmp = Join-Path $env:TEMP ("postern-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $out = Join-Path $tmp 'payload.bin'
        Save-File -Uri "http://localhost:$port/ok" -OutFile $out -Label 'payload.bin'
        Check 'downloads the exact byte count' ((Get-Item -LiteralPath $out).Length -eq $bytes.Length) "$((Get-Item -LiteralPath $out).Length) vs $($bytes.Length)"
        Check 'downloaded bytes hash to the source hash' ((Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash -eq $realHash)

        # The comparison install.ps1 performs, both ways round.
        Check 'verify accepts a matching hash' `
        ((Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash -eq (Get-ExpectedHash -Sums "$realHash  payload.bin" -Name 'payload.bin' -Tag v1))
        Check 'verify rejects a tampered hash' `
        ((Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash -ne (Get-ExpectedHash -Sums ("$('d' * 64)  payload.bin") -Name 'payload.bin' -Tag v1))

        $truncFile = Join-Path $tmp 't.bin'
        $threw = $false
        try { Save-File -Uri "http://localhost:$port/truncated" -OutFile $truncFile -Label 't.bin' }
        catch { $threw = $true }
        Check 'a truncated transfer never returns success' $threw
        Check 'and the partial file would fail the hash check regardless' `
        (-not (Test-Path -LiteralPath $truncFile) -or (Get-FileHash -LiteralPath $truncFile -Algorithm SHA256).Hash -ne $realHash)
    }
    finally {
        $listener.Stop(); $listener.Close()
        try { $server.Stop() } catch { }
        $server.Dispose()
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n=== The real manifest scripts\publish.ps1 writes ===" -ForegroundColor Cyan
$localSums = Join-Path $root 'releases\SHA256SUMS.txt'
$localSetup = Join-Path $root 'releases\Postern-win-Setup.exe'
if (-not (Test-Path -LiteralPath $localSums) -or -not (Test-Path -LiteralPath $localSetup)) {
    Write-Host '  SKIP  no local SHA256SUMS.txt + Postern-win-Setup.exe - run scripts\publish.ps1 to cover this.' -ForegroundColor Yellow
}
else {
    $want = Get-ExpectedHash -Sums (Get-Content -LiteralPath $localSums -Raw) -Name 'Postern-win-Setup.exe' -Tag 'local'
    Check 'install.ps1 would accept the locally packed installer' `
    ($want -eq (Get-FileHash -LiteralPath $localSetup -Algorithm SHA256).Hash)
    $raw = [System.IO.File]::ReadAllBytes($localSums)
    Check 'manifest is LF-only (so sha256sum -c accepts it)' (($raw -contains 13) -eq $false)
    Check 'manifest has no BOM' ($raw[0] -ne 0xEF)
    Check 'manifest does not list itself' (-not (Select-String -LiteralPath $localSums -Pattern 'SHA256SUMS' -Quiet))
}

Write-Host "`n$pass passed, $fail failed" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
if ($fail) { exit 1 }
