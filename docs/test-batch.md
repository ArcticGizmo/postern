# Test batch

Step through one ID at a time. Tick the box when a step passes. If it doesn't, write what you saw on the
`Result:` line. When you're done, a list like `A2.1 ✓ · A3.3 ✗ countdown didn't restart` is enough to send back.

**One rule:** don't paste anything containing your GitHub profile (login, name, email). Where a step's output is
worth sending back, its command is already filtered to safe lines.

Branches: **postern** `poc/walking-skeleton`; **perch** `spike/postern-transport` in the worktree `..\perch-postern`
(off `main`; your `work-items` checkout is untouched).

- **Session A** (~45 min): the core loop at your desk.
- **Session B** (~15 min): lifecycle and accounts. Includes a sign-out.
- **Session C** (a few days): Perch in shadow mode, then the cutover.

---

## Session A: the core loop

### A0. Build and automated checks (~3 min, no clicks)

- [ ] **A0.1** Build:
  ```powershell
  cd C:\Users\JonHowell\Documents\git\personal\postern
  git checkout poc/walking-skeleton
  dotnet build postern.slnx; dotnet build spikes\consumers\consumers.slnx; dotnet build tools\Postern.FakeUpstream
  ```
  **Expect:** all three builds succeed.
  Result:

- [ ] **A0.2** `dotnet test tests\Postern.Tests\Postern.Tests.csproj`
  **Expect:** `Passed! - Failed: 0, Passed: 96`.
  Result:

- [ ] **A0.3** `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke.ps1`
  **Expect:** every line `[PASS]`, ending `smoke: all passed`.
  Result:

- [ ] **A0.4** `powershell -NoProfile -ExecutionPolicy Bypass -File spikes\consumers\run.ps1`
  **Expect:** `consumers: all passed`.
  Result:

- [ ] **A0.5** `powershell -NoProfile -ExecutionPolicy Bypass -File spikes\consumers\perch-e2e.ps1`
  **Expect:** `Passed: 1`.
  Result:

- [ ] **A0.6** Did CrowdStrike or Defender react to anything during A0.3–A0.5? These scripts create pipes, spawn
  and kill processes, and write to Credential Manager.
  Result:

- [ ] **A0.7** Set the paths the rest of the steps use (repeat this in any new terminal):
  ```powershell
  $postern = "C:\Users\JonHowell\Documents\git\personal\postern\src\Postern.App\bin\Debug\net10.0-windows\Postern.exe"
  $cli     = "C:\Users\JonHowell\Documents\git\personal\postern\src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe"
  ```

### A1. Test PAT

- [ ] **A1.1** GitHub → Settings → Developer settings → Fine-grained tokens → Generate.
  - Name: `postern-test`
  - Expiration: 7 days
  - Resource owner: **you**
  - Repository access: **Public repositories**. No permissions needed.

### A2. Tray app first run

- [ ] **A2.1** `& $postern`
  **Expect:** within about 2 s, a teal arched-gate icon in the tray (it may be in the ^ overflow), and the
  **Credentials** window opens by itself.
  Result:

- [ ] **A2.2** In Credentials, the github row says *missing* in amber. Click **Set**, paste the PAT, click **Save**.
  **Expect:** *stored* in green, with **Replace** and **Remove** buttons. Nothing shows the token.
  Result:

- [ ] **A2.3** Close the window, then reopen it from the tray (Credentials…).
  **Expect:** still *stored*.
  Result:

- [ ] **A2.4** Hover over the tray icon.
  **Expect:** tooltip `Postern: 0 app(s) allowed`.
  Result:

- [ ] **A2.5** Right-click the tray icon.
  **Expect:** `Postern 0.1.0`, Apps…, Credentials…, Audit log…, Start at login, Open data folder, Quit Postern.
  Result:

- [ ] **A2.6** Left-click the tray icon.
  **Expect:** the Apps window opens, saying no apps have access.
  Result:

- [ ] **A2.7** `& $cli status`
  **Expect:** `Postern 0.1.0 on \\.\pipe\postern (pid N), providers: github`, then `'cli' isn't registered.`
  Result:

- [ ] **A2.8** Anything about the windows' look, wording or icon that felt wrong?
  Result:

### A3. The approval window

- [ ] **A3.1** Run:
  ```powershell
  & $cli register --name scripts --scope "GET github/user" --scope "POST github/graphql" --scope "GET github/repos/*/*/pulls"
  ```
  **Expect:** a centred, topmost window titled "Postern: allow an app?". The tray icon turns amber, and its
  tooltip says 1 app is waiting. Don't click anything yet.
  Result:

- [ ] **A3.2** Check the window's contents:
  - **Heading:** "scripts" wants to call services through Postern.
  - **Program:** the path to `postern-cli.exe`.
  - **Publisher:** "Unsigned…" in amber.
  - **Process:** a pid. Compare it with `Get-Process postern-cli`.
  - **Scopes:** exactly the three you asked for.

  Result:

- [ ] **A3.3** Watch the Allow button count down `Allow (2)` → `(1)` → `Allow`. Press any letter key during the
  countdown.
  **Expect:** the countdown restarts.
  Result:

- [ ] **A3.4** Click **Allow**.
  **Expect:** the terminal prints `Approved: Approved.`, the icon goes back to teal, and the tooltip says 1 app
  is allowed.
  Result:

- [ ] **A3.5** Enter means Deny. Run `& $cli register --name enter-test --scope "GET github/user"`, then press
  **Enter** on the window.
  **Expect:** the window closes and the terminal prints `Denied: registration-denied…`.
  Result:

- [ ] **A3.6** Same command again, and press **Esc**.
  **Expect:** Denied.
  Result:

- [ ] **A3.7** Same command again, and close the window with the **X**.
  **Expect:** Denied.
  Result:

### A4. Calls

- [ ] **A4.0** Write the GraphQL body to a file. On Windows PowerShell 5.1, inline JSON loses its inner quotes
  when passed to an exe.
  ```powershell
  '{"query":"{ rateLimit { remaining cost } }"}' | Set-Content $env:TEMP\q.json -Encoding ascii
  ```

- [ ] **A4.1** `& $cli call --name scripts GET github/user -i | Select-String '^HTTP|^Postern-'`
  **Expect:**
  - `HTTP 200`
  - `Postern-Upstream-Ms`
  - `Postern-RateLimit-Resource: core`, plus `-Remaining`, `-Limit: 5000` and `-Reset`

  On the work machine, this passing also proves Cato's TLS re-signing works with no extra configuration.
  Result (paste-safe):

- [ ] **A4.2** `& $cli call --name scripts POST github/graphql --data "@$env:TEMP\q.json"`
  **Expect:** JSON containing `rateLimit`.
  Result:

- [ ] **A4.3** `& $cli call --name scripts GET github/repos/cli/cli; $LASTEXITCODE`
  **Expect:** `postern: 403 scope-denied: …`, then exit code `3`.
  Result:

- [ ] **A4.4** `& $cli call --name scripts GET "github/repos/cli/cli/pulls?per_page=1" -i | Select-String '^HTTP|^ETag|^Postern-RateLimit-Remaining'`
  **Expect:** `HTTP 200`, an ETag, and a Remaining value. Note the value (R).
  Result:

- [ ] **A4.5** Repeat that request with the ETag:
  ```powershell
  $etag = ((& $cli call --name scripts GET "github/repos/cli/cli/pulls?per_page=1" -i | Select-String '^ETag:') -replace '^ETag:\s*','').Trim()
  & $cli call --name scripts GET "github/repos/cli/cli/pulls?per_page=1" -i -H ("If-None-Match: " + $etag.Replace('"','\"')) | Select-String '^HTTP|^Postern-RateLimit-Remaining'
  ```
  **Expect:** `HTTP 304`, and Remaining unchanged from the request just before it (a 304 is free). cli/cli is a
  busy repo, so if you get a 200, run both lines again.
  Result:

- [ ] **A4.6** `& $cli call --name scripts GET "github/repos/a%2Fb/x/pulls"`
  **Expect:** `400 bad-path`.
  Result:

- [ ] **A4.7** `& $cli status --name scripts`
  **Expect:** `'scripts' is approved` and the three scopes.
  Result:

### A5. Replacing, revoking and timing out an approval

- [ ] **A5.1** Replace the scopes:
  ```powershell
  & $cli register --name scripts --scope "GET github/user" --scope "GET github/notifications"
  ```
  **Expect:**
  - An amber line saying "It already has access…".
  - In the scope list: `  GET github/user`, then `+ GET github/notifications` in amber, then
    `- POST github/graphql` and `- GET github/repos/*/*/pulls` in grey.

  Click Allow.
  Result:

- [ ] **A5.2** Open Apps.
  **Expect:** one `scripts` entry, approved, with 2 scopes.
  Result:

- [ ] **A5.3** Tick "Show denied, revoked and replaced".
  **Expect:** the old `scripts` shows as *superseded*, and the `enter-test` attempts as *denied*.
  Result:

- [ ] **A5.4** `& $cli call --name scripts POST github/graphql --data "@$env:TEMP\q.json"`
  **Expect:** `scope-denied`, because that scope was just removed.
  Result:

- [ ] **A5.5** In Apps, click **Revoke** on `scripts`. Then run `& $cli call --name scripts GET github/user`.
  **Expect:** `403 app-revoked`, immediately.
  Result:

- [ ] **A5.6** Run `& $cli register --name timeout-test --scope "GET github/user"` and don't touch the window.
  **Expect:** after 2 minutes the window closes itself and the terminal says Denied. Do A5.7 while you wait.
  Result:

- [ ] **A5.7** While A5.6's window is still open, run this in a **second** terminal (set `$cli` there first; see A0.7):
  `& $cli register --name second --scope "GET github/user"`
  **Expect:** an immediate `registration-pending`. Only one registration per exe can wait at a time, and every
  CLI registration is the same exe.
  Result:

### A6. A prompt while you're busy

- [ ] **A6.1** Run the command below, then click into your browser and start typing before the 8 seconds are up.
  ```powershell
  Start-Sleep 8; & $cli register --name focus-test --scope "GET github/user"
  ```
  **Report:** did the prompt come to the front with focus, or stay behind with the taskbar flashing?
  Result:

- [ ] **A6.2** **Report:** did your typing land in the prompt (restarting the countdown), or stay in the browser?
  Result:

- [ ] **A6.3** Click Deny.

### A7. Audit log and secret hygiene

- [ ] **A7.1** Open Tray → Audit log.
  **Expect at the top:** `github/core: N/5000 left…`, and probably a `github/graphql` line.
  Result:

- [ ] **A7.2** Check the rows, newest first.
  **Expect:**
  - allowed requests in white
  - `scope-denied`, `app-revoked` and `bad-path` in amber
  - registration rows (`_postern`, with `registration-requested` and `token-issued`)
  - queries shown only as `?…`, never their contents

  Result:

- [ ] **A7.3** Check that no secret reached the logs:
  ```powershell
  Select-String -Path "$env:LOCALAPPDATA\Postern\logs\*.log" -Pattern 'github_pat_|ghp_|Bearer '
  ```
  **Expect:** no output.
  Result:

- [ ] **A7.4** `cmdkey /list | Select-String postern`
  **Expect:** exactly one entry, `postern:github:default`.
  Result:

### A8. Consumers against real GitHub

Do these before Session C. Perch's traffic would otherwise muddy the rate-limit numbers.

- [ ] **A8.1** Run the poller against a **quiet** public repo (one of yours is ideal):
  ```powershell
  spikes\consumers\Poller\bin\Debug\net10.0-windows\poller-spike.exe --repo <owner/quiet-repo> --interval 20 --count 10
  ```
  Approve `poller-spike`.
  **Expect:** one `200 changed`, then `304 not modified` lines. The summary's `remaining X -> Y` should drop by
  (number of 200s − 1), which would confirm 304s are free.
  Result (paste the summary line):

- [ ] **A8.2** Run the poller again. Partway through, Quit Postern from the tray, wait 3 s, then run `& $postern`.
  **Expect:** at most one poll reports "Postern unavailable", and the polls after it carry on.
  Result:

- [ ] **A8.3** Run Octokit, and approve it:
  ```powershell
  spikes\consumers\Octokit\bin\Debug\net10.0-windows\octokit-spike.exe --repo cli/cli
  ```
  **Expect:** four `[PASS]` lines (the last is GetArchive's redirect being refused) and one `[INFO]` line.
  Result (paste it all; the login is masked):

- [ ] **A8.4** Run `node spikes\consumers\node\demo.mjs` and approve it.
  **Expect:** 4 `[PASS]` lines.
  Result:

- [ ] **A8.5** Delete the Node demo's plain-text token:
  ```powershell
  Remove-Item "$env:LOCALAPPDATA\Postern.Client\node-spike.postern.plain-token"
  ```

- [ ] **A8.6** In the Apps window, revoke `poller-spike`, `octokit-spike` and `node-spike`.

---

## Session B: lifecycle and accounts

- [ ] **B1** Tray → **Start at login** (it gets a tick). Then run:
  ```powershell
  (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run).Postern
  ```
  **Expect:** `"…\Postern.exe" --supervise`. **Note:** any EDR alert.
  Result:

- [ ] **B2** Quit Postern, sign out, and sign back in. Then run `Get-Process Postern`.
  **Expect:**
  - **two** processes: the watchdog and Postern
  - the tray icon present
  - `& $cli status` working (set `$cli` again first; see A0.7)

  Result:

- [ ] **B3** Kill Postern and check the watchdog brings it back:
  ```powershell
  Stop-Process -Id ([regex]::Match((& $cli status), 'pid (\d+)').Groups[1].Value) -Force
  ```
  **Expect:** the icon returns within about 2 s. Windows may leave a ghost icon until you hover over it.
  `& $cli status` shows a new pid.
  Result:

- [ ] **B4** Test the crash budget:
  ```powershell
  1..5 | % { $p=[regex]::Match((& $cli status 2>$null), 'pid (\d+)').Groups[1].Value; if ($p) { Stop-Process -Id $p -Force }; Start-Sleep 4 }
  ```
  **Expect:** after the fifth kill it doesn't come back, and today's log in `%LOCALAPPDATA%\Postern\logs` contains
  "crashed 5 times … giving up". Then start it again with `& $postern`.
  Result:

- [ ] **B5** Tray → Quit Postern.
  **Expect:** `Get-Process Postern` returns nothing.
  Result:

- [ ] **B6** Start Postern again (`& $postern`). Open an **admin** PowerShell, set `$cli` again (see A0.7), and
  run `& $cli status`.
  **Expect:** it works.
  **Decide:** should elevated callers be allowed?
  Result:

- [ ] **B7** Optional; only if you already have a second local account:
  ```powershell
  Copy-Item src\Postern.Cli\bin\Debug\net10.0-windows C:\Users\Public\postern-cli -Recurse
  runas /user:<other> "cmd /k C:\Users\Public\postern-cli\postern-cli.exe status"
  ```
  **Expect:** an immediate `pipe refused this account: it belongs to another user's Postern`.
  Result:

- [ ] **B8** Untick Start at login if you don't want it yet.

---

## Session C: Perch

### C0. A PAT that sees what gh sees

Shadow mode compares answers, so Postern's PAT needs the same reach as your gh login.

- [ ] **C0.1** Create one of these:
  - **Fine-grained token:** resource owner = your work org, all repositories, *Pull requests: Read*,
    *Metadata: Read* and *Commit statuses: Read* (plus *Checks: Read* if it's offered). The org may need to allow
    or approve fine-grained tokens.
  - **Classic token:** `repo` and `read:org`, then *Configure SSO → Authorize*.

  **Report:** which route the org allowed. That's a finding for the scope doc.
  Result:

- [ ] **C0.2** Store it: Tray → Credentials → Replace.

### C1. Run Perch in shadow mode

- [ ] **C1.1** Build and run:
  ```powershell
  cd C:\Users\JonHowell\Documents\git\personal\perch-postern
  dotnet build perch.slnx
  $env:PERCH_GITHUB_TRANSPORT = 'shadow'
  dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
  ```
  **Expect:** it runs as **Perch (Dev)**, alongside your installed Perch, with its own settings.
  Result:

- [ ] **C1.2** In Perch (Dev)'s Settings, search "GitHub" and turn on PR status and the GitHub alerts strip.

- [ ] **C1.3** **Expect:** a Postern prompt for `perch`, asking for `GET github/user` and `POST github/graphql`,
  with Program pointing at perch-postern's `perch.exe`. Allow it.
  Result:

### C2. Read the comparison log

- [ ] **C2.1** After a few minutes:
  ```powershell
  Get-Content "$env:APPDATA\Perch (Dev)\logs\github-transport.log" -Tail 20
  ```
  **Expect:** lines like `alerts: same (gh N ms, postern M ms)` and `pr <folder>: same`. If the Postern side
  shows fewer PRs, the PAT has less access than gh.
  Result:

### C3. Leave it running for a day or two

- [ ] **C3.1** Count the outcomes:
  ```powershell
  $log = "$env:APPDATA\Perch (Dev)\logs\github-transport.log"
  (Select-String $log ': same').Count; (Select-String $log 'DIFFERENT').Count
  ```
  Result (same / DIFFERENT):

- [ ] **C3.2** Send any `DIFFERENT` lines, with repo names and PR titles redacted.
  Result:

### C4. Cut over

- [ ] **C4.1** Quit Perch (Dev). Then:
  ```powershell
  $env:PERCH_GITHUB_TRANSPORT = 'postern'
  gh auth logout
  ```
  Start Perch (Dev) again (the `dotnet run` line from C1.1).
  **Expect:** the alerts strip and PR glyphs work as before. Perch now holds no GitHub token.
  Result:

- [ ] **C4.2** Revoke `perch` in Postern's Apps window.
  **Expect:** the strip shows "Postern refused: app-revoked", and Perch doesn't crash.
  Result:

- [ ] **C4.3** Quit Postern.
  **Expect:** the strip shows "Postern isn't running".
  Result:

- [ ] **C4.4** Start Postern again (`& $postern`).
  **Expect:** Perch recovers on its next poll. It stays revoked from C4.2 (Perch reports "Postern refused"), so
  restart Perch (Dev) to re-register, approve it, and check the strip fills again.
  Result:

- [ ] **C4.5** Run `gh auth login` to restore gh.

---

## Decisions waiting on you

| Question | Current behaviour | Where |
|---|---|---|
| Allow elevated callers? (B6) | Yes: the pipe ACL is by user SID | `PipeServerFactory` |
| Is an unsigned app acceptable to approve? | Allowed, with an amber warning; identity is the exe path | `ApprovalWindow`, `CallerInfo.Matches` |
| CLI registrations all share one exe (A5.7) | They're told apart only by `--name`, and only one can be pending at a time | `postern-cli` |
| Graph via WAM (S5) | Not started. Needs an Entra app registration with `Mail.Read`/`Calendars.Read` consent in the work tenant. Is that obtainable? | `docs/spike-plan.md` S5 |
| Merge order | Postern branches are local only. Perch's branch is a spike: the env var should become a setting before it merges | |
