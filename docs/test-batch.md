# Test batch

Step through one ID at a time. Tick the box when a step passes. If it doesn't, write what you saw on the
`Result:` line. When you're done, a list like `A2.1 ✓ · A3.3 ✗ countdown didn't restart` is enough to send back.

**One rule:** don't paste anything containing your GitHub profile (login, name, email). Where a step's output is
worth sending back, its command is already filtered to safe lines.

Branches: **postern** `poc/walking-skeleton`; **perch** `spike/postern-transport` in the worktree `..\perch-postern`
(off `main`; your `work-items` checkout is untouched).

- **Session A** (~45 min): the core loop at your desk.
- **Session B** (~15 min): lifecycle and accounts. Includes a sign-out.
- **Session C** (a few days): Perch in shadow mode, then the cutover. **Parked**: Session D comes first.
- **Session D** (~20 min, then a day or two in the background): the approval queue, the elevation guard, and a
  stack of small shadow apps comparing gh with Postern.
- **Session E** (~30 min): install from a locally built release next to Postern (Dev), then update through
  the Updates page, using a local feed because nothing is on GitHub yet. See `docs/releasing.md`.

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
  **Expect:** within about 2 s, a violet arched-gate icon on a purple tile in the tray (the dev colours; it may be in
  the ^ overflow), and the
  Postern window opens by itself on **Credentials**, with an amber *missing* badge beside it in the left nav.
  Result:

- [ ] **A2.2** In Credentials, the github row says *missing* in amber. Click **Set**, paste the PAT, click **Save**.
  **Expect:** *stored* in green, with **Replace** and **Remove** buttons, and the *missing* badge gone. Nothing shows
  the token.
  Result:

- [ ] **A2.3** Close the window, then click the tray icon and pick **Credentials** in the nav.
  **Expect:** still *stored*.
  Result:

- [ ] **A2.4** Hover over the tray icon.
  **Expect:** tooltip `Postern: 0 app(s) allowed`.
  Result:

- [ ] **A2.5** Right-click the tray icon.
  **Expect:** `Postern 0.1.0`, Open Postern…, Quit Postern. No update item unless one has been found.
  Result:

- [ ] **A2.6** Left-click the tray icon.
  **Expect:** the Postern window opens on **Apps**, saying no apps have access. **General** shows Start at login,
  the data and logs folders (each with Open), the version and the pipe. Esc closes the window.
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
  **Expect:** the terminal prints `Approved: Approved.`, the icon goes back to violet, and the tooltip says 1 app
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
  **Expect:** after 5 minutes the window closes itself and the terminal says Denied. Do A5.7 while you wait.
  Result:

- [ ] **A5.7** While A5.6's window is still open, run this in a **second** terminal (set `$cli` there first; see A0.7):
  `& $cli register --name second --scope "GET github/user"`
  **Expect:** the same Postern window now shows "(1 of 2)" with `second` queued under `timeout-test`. A program
  can have up to 10 requests waiting, one per name. Asking again under the same name replaces that name's request.
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

- [ ] **A7.1** Open Postern → Audit log.
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
  Select-String -Path "$env:LOCALAPPDATA\Postern.Dev\logs\*.log" -Pattern 'github_pat_|ghp_|Bearer '
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

- [ ] **A8.6** On the Apps page, revoke `poller-spike`, `octokit-spike` and `node-spike`.

---

## Session B: lifecycle and accounts

- [ ] **B1** Postern → General → tick **Start Postern when I sign in to Windows**. Then run:
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
  **Expect:** after the fifth kill it doesn't come back, and today's log in `%LOCALAPPDATA%\Postern.Dev\logs` contains
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

- [ ] **C0.2** Store it: Postern → Credentials → Replace.

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

- [ ] **C4.2** Revoke `perch` on Postern's Apps page.
  **Expect:** the strip shows "Postern refused: app-revoked", and Perch doesn't crash.
  Result:

- [ ] **C4.3** Quit Postern.
  **Expect:** the strip shows "Postern isn't running".
  Result:

- [ ] **C4.4** Start Postern again (`& $postern`).
  **Expect:** the strip changes from "Postern isn't running" back to "Postern refused: app-revoked" on Perch's
  next poll. It's still revoked from C4.2.

  A revoked app deliberately doesn't ask again on its own. To re-approve Perch:
  1. Quit Perch (Dev).
  2. `Remove-Item "$env:LOCALAPPDATA\Postern.Client\perch.token"`
  3. Start Perch (Dev) again and approve the new prompt.

  **Expect:** the strip fills again.
  Result:

- [ ] **C4.5** Run `gh auth login` to restore gh.

---

## Session D: queue, elevation guard, shadow stack

### D0. Build

- [ ] **D0.1** Tray → Quit Postern (the running copy locks its `bin` folder). Then:
  ```powershell
  cd C:\Users\JonHowell\Documents\git\personal\postern
  dotnet build postern.slnx; dotnet test postern.slnx
  & $postern
  ```
  **Expect:** the build succeeds, 102 tests pass, and the tray icon is back as **Postern (Dev)**. Debug builds are
  now a separate profile (pipe `postern-dev`, data in `%LOCALAPPDATA%\Postern.Dev`, credentials under
  `postern-dev:`), so it starts fresh: Postern opens on Credentials. Paste your PAT there. Earlier approvals stay
  with the old data and aren't carried over. The Debug `$cli` and the shadow apps talk to the dev profile on their own.
  Result:

### D1. Elevation guard

- [ ] **D1.1** In an **admin** PowerShell (set `$postern` there first; see A0.7):
  ```powershell
  (Start-Process $postern -Wait -PassThru).ExitCode
  ```
  **Expect:** a "Postern was started as administrator" warning box. After you click OK, it prints `5`. Your normal
  Postern keeps running.
  Result:

- [ ] **D1.2** Still in the admin PowerShell: `& $postern --headless`
  **Expect:** the same message in the terminal, and no message box.
  Result:

- [ ] **D1.3** Still in the admin PowerShell: `& $cli status`
  **Expect:** it works. Elevated *callers* are still allowed; only Postern itself refuses to run elevated.
  Result:

### D2. The approval queue

- [ ] **D2.1** Ask three times at once. All three print into this terminal. (`Start-Process` doesn't quote
  arguments that contain spaces, hence the extra `"` around the scope.)
  ```powershell
  1..3 | % { Start-Process $cli -ArgumentList 'register', '--name', "queue-$_", '--scope', '"GET github/user"' -NoNewWindow }
  ```
  **Expect:** three "Asking Postern to approve 'queue-N'" blocks here, and a single Postern window titled
  "(1 of 3)", with `queue-1`, `queue-2` and `queue-3` listed in arrival order and `queue-1` shown. Allow counts down
  from 2. Deny all is on the left. If the terminal shows `bad-scope`, the quoting was lost.
  Result:

- [ ] **D2.2** Allow `queue-1`.
  **Expect:** `queue-2` slides in, and Allow is disabled again for 2 seconds. Double-clicking Allow can't approve
  `queue-2` by accident.
  Result:

- [ ] **D2.3** Deny `queue-2`. Then close the window with **X** while `queue-3` is showing.
  **Expect:** the window closes, and the terminal shows `queue-2` and `queue-3` Denied. Check with:
  `1..3 | % { & $cli status --name "queue-$_" | Select-Object -Index 1 }`. Only `queue-1` is approved.
  Result:

- [ ] **D2.4** Asking again replaces a waiting request. In one terminal:
  `& $cli register --name queue-r --scope "GET github/user"`. Don't answer; run the same line in a second terminal.
  **Expect:** the first terminal prints `Denied: registration-denied: Registration superseded.` straight away, and
  the window shows the second request on its own. Deny it.
  Result:

- [ ] **D2.5** Postern not running. Tray → Quit Postern, then:
  `& $cli register --name queue-w --scope "GET github/user"`
  **Expect:** it fails at once (exit 3) with "nothing is listening… Start Postern.exe, and turn on Start at
  login…", plus a hint about `--wait`. No window, and nothing is queued.
  Result:

- [ ] **D2.6** Waiting for Postern. With Postern still quit:
  `& $cli register --name queue-w --scope "GET github/user" --wait 60`
  It prints "waiting up to 60 s". Within the minute, start Postern from a **second** terminal (`& $postern`).
  **Expect:** as soon as the tray icon appears, the prompt for `queue-w` opens and the first terminal moves on to
  "Approve it in the Postern window...". Deny it.
  Result:

- [ ] **D2.7** Clean up: in Apps, revoke `queue-1`. Then
  `"queue-1","queue-2","queue-3","queue-r","queue-w" | % { & $cli forget --name $_ }`.

### D3. Shadow stack

The shadow apps compare what gh sees with what Postern's PAT sees, so do C0.1 and C0.2 (a PAT with the same
reach as gh) first if you haven't. `gh auth status` must show you logged in.

- [ ] **D3.1** Start the stack. Pass one or two repos you work in to add the pulls and runs probes:
  ```powershell
  .\scripts\shadow-stack.ps1 -Repo <owner>/<repo>
  ```
  **Expect:** four minimised `postern-shadow` windows, and four requests queued in one Postern window:
  `shadow-web`, `shadow-api`, `shadow-api-jobs` and `shadow-worker`. `shadow-api` and `shadow-api-jobs` show the
  **same** Program path (one exe, two names); the others have their own folders under `out\shadow\`. Approve all four.
  Result:

- [ ] **D3.2** After about 3 minutes: `.\scripts\shadow-stack.ps1 -Summary`
  **Expect:** each app has `same` lines and no `DIFFERENT`. The `user` probe logs a hash of your login, not the
  login itself. A `DIFFERENT` on pulls or runs usually means the PAT can't see that repo, or lacks *Actions: Read*.
  Result (same / DIFFERENT per app):

- [ ] **D3.3** In Apps, revoke `shadow-web`. Wait a minute, then run `-Summary` again.
  **Expect:** `shadow-web` shows `postern refused: app-revoked` as DIFFERENT, and doesn't prompt again. The other
  apps carry on with `same`.
  Result:

- [ ] **D3.4** Tray → Quit Postern. Wait a minute, start it again (`& $postern`), and wait another minute.
  **Expect:** one round of `postern unavailable` in each log, then back to `same` with no new prompts.
  Result:

- [ ] **D3.5** Leave it running for a day or two, then run `-Summary`.
  Result (same / DIFFERENT per app):

- [ ] **D3.6** Send any `DIFFERENT` lines, with repo names redacted. Then stop the stack:
  `.\scripts\shadow-stack.ps1 -Stop`, and revoke the `shadow-*` apps in Apps.
  Result:

---

## Session E: install and update

The one-liner can't be tried until a release is on GitHub and `install.ps1` is on `main`, so this session installs
from a locally built Setup. The installer's logic is covered by `tools\test-install.ps1` (E1.4) in the meantime. Use
a new terminal, in the repo folder.

### E0. Clear the way for a fresh install

- [ ] **E0.1** If a Postern from before Session D is still running (its tray says **Postern**, not
  **Postern (Dev)**), untick Start at login on its General page, then Tray → Quit.
  Result:

- [ ] **E0.2** `%LOCALAPPDATA%\Postern` still holds the data from the builds before Session D, and it's where the
  installer puts Postern. Look at it, then move it out of the way (or delete it, if you don't want it):
  ```powershell
  Get-ChildItem "$env:LOCALAPPDATA\Postern"
  Rename-Item "$env:LOCALAPPDATA\Postern" Postern.old-dev
  ```
  Your PAT from those builds is still in Credential Manager as `postern:github:default`, and the installed Postern
  will find it there. If you want the install to start without one, delete it first: `cmdkey /delete:postern:github:default`.
  Result:

- [ ] **E0.3** In **Postern (Dev)**: open the Updates page.
  **Expect:** "This copy can't update itself", showing the `bin\Debug` path.
  Result:

### E1. Install

- [ ] **E1.1** Build a release and install it:
  ```powershell
  .\scripts\publish.ps1 0.1.0
  Get-FileHash .\releases\Postern-win-Setup.exe; Select-String .\releases\SHA256SUMS.txt -Pattern Setup
  .\releases\Postern-win-Setup.exe
  ```
  **Expect:** the two hashes match (case aside). There's no admin prompt. A second, teal tray icon appears, with
  **Postern 0.1.0** in its menu header, next to Postern (Dev)'s violet one. The Start menu has a Postern entry, and there's **no**
  desktop shortcut. Add your PAT under Credentials if it asks.
  Result:

- [ ] **E1.2** Both profiles answer, each on its own pipe:
  ```powershell
  $icli = "$env:LOCALAPPDATA\Postern\current\postern-cli.exe"
  & $icli status; & $cli status
  ```
  **Expect:** `Postern 0.1.0 on \\.\pipe\postern`, then the dev build on `\\.\pipe\postern-dev`.
  Result:

- [ ] **E1.3** Register an app with the installed copy, to watch it through the updates below:
  ```powershell
  & $icli register --name e-test --scope "GET github/user"
  & $icli call --name e-test GET github/user | Select-String '"type"'
  ```
  **Expect:** a prompt from **Postern** (not Postern (Dev)). After Allow, the call prints `"type": "User"`.
  Result:

- [ ] **E1.4** The installer's tests, against the release you just built:
  ```powershell
  powershell -NoProfile -File tools\test-install.ps1 | Select-String 'FAIL|SKIP|passed'
  ```
  **Expect:** `35 passed, 0 failed`, with no FAIL or SKIP lines.
  Result:

- [ ] **E1.5** In the installed Postern: open the Updates page.
  **Expect:** "Postern couldn't check for updates." with a not-found error, because
  `github.com/ArcticGizmo/postern` has no releases yet. That's the right failure.
  Result:

### E2. Update from a local feed

- [ ] **E2.1** Build a newer version into the same folder:
  ```powershell
  .\scripts\publish.ps1 0.1.1 -SkipTests
  ```
  Tray → Quit Postern. Then start the installed copy with the local feed:
  ```powershell
  $env:POSTERN_UPDATE_FEED = "$PWD\releases"; & "$env:LOCALAPPDATA\Postern\current\Postern.exe"
  ```
  **Expect:** about 30 seconds after start, an **Update to 0.1.1…** item appears in the tray menu, the Updates nav
  item gets a *new* badge, and the tooltip says "Update 0.1.1 available".
  Result:

- [ ] **E2.2** Click **Update to 0.1.1…** in the tray menu.
  **Expect:** Postern opens on Updates: "Postern 0.1.1 is available", the three restart bullets, and an amber line naming the local folder.
  There's no Release notes button for a local feed.
  Result:

- [ ] **E2.3** Click **Update and restart**.
  **Expect:** a download percentage, then Postern disappears from the tray, a Velopack progress window may flash
  up, and Postern comes back with **Postern 0.1.1** in its menu header. `& $icli status` shows 0.1.1, and
  `& $icli call --name e-test GET github/user` still works with no new prompt. Postern (Dev) carries on throughout.
  Result:

### E3. Update under the supervisor

- [ ] **E3.1** In the installed copy: General → tick **Start Postern when I sign in to Windows**. Then:
  ```powershell
  (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run).Postern
  ```
  **Expect:** `"…\AppData\Local\Postern\current\Postern.exe" --supervise`.
  Result:

- [ ] **E3.2** Run under the supervisor, as a login would, and offer it another version:
  ```powershell
  .\scripts\publish.ps1 0.1.2 -SkipTests
  ```
  Tray → Quit Postern. Then, in the terminal where `POSTERN_UPDATE_FEED` is set:
  ```powershell
  & "$env:LOCALAPPDATA\Postern\current\Postern.exe" --supervise
  ```
  **Expect:** two processes from the install folder (the supervisor and Postern):
  `Get-Process Postern | ? Path -like "$env:LOCALAPPDATA\Postern\*"`.
  Result:

- [ ] **E3.3** Tray → Update to 0.1.2… → Update and restart.
  **Expect:** Postern comes back as 0.1.2, and the installed copy has **two** processes again, so the watchdog came
  back too: `Get-Process Postern | ? Path -like "$env:LOCALAPPDATA\Postern\*"`. Check it's really watching:
  `Stop-Process -Id ([regex]::Match((& $icli status), 'pid (\d+)').Groups[1].Value) -Force`. Postern returns
  within about 2 seconds.
  Result:

### E4. Uninstall (optional; reinstall afterwards with E1.1's Setup)

- [ ] **E4.1** Windows Settings → Apps → Installed apps → Postern → Uninstall. Then:
  ```powershell
  Test-Path "$env:LOCALAPPDATA\Postern"; Test-Path "$env:LOCALAPPDATA\Postern.Data\postern.db"
  (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run).Postern; Test-Path HKCU:\Software\Postern
  cmdkey /list | Select-String 'postern'
  ```
  **Expect:** `False`, `True`, nothing, `False`. The `cmdkey` lines show only `postern-dev:…` entries: every
  `postern:` credential has gone with the install, and the dev profile's are untouched. Postern (Dev) keeps running.
  Result:

---

## Decisions

| Question | Decision | Where |
|---|---|---|
| Allow elevated callers? (B6) | **Allowed.** UAC isn't a security boundary between a user's own processes, so refusing elevated callers wouldn't stop anything that could just run unelevated. The pipe ACL stays by user SID | `PipeServerFactory` |
| Is an unsigned app acceptable to approve? | **Allowed**, with the amber warning. Developer machines run plenty of unsigned local builds. Identity is the exe path | `ApprovalWindow`, `CallerInfo.Matches` |
| CLI registrations all share one exe (A5.7) | **One `postern-cli` exe.** Registrations are told apart by `--name`. Requests queue (up to 10 per exe, one per name) and are shown in arrival order | `postern-cli`, `Registrations`, `ApprovalWindow` |
| Should a request start Postern if it isn't running? | **No.** Postern would inherit the app's environment, elevation and lifetime. Start at login keeps it running; clients can wait for it (`WaitUntilAvailableAsync`, `postern-cli --wait`), and the not-running message says how to start it | `PosternClient`, `postern-cli` |
| Public repo? | **Yes.** Installs and update checks go to GitHub anonymously | `install.ps1`, `Updater` |
| Uninstall and credentials | **Uninstall deletes every `postern:*` credential.** The data folder stays | `Updater.CleanUpBeforeUninstall` |
| Dev builds next to an install | **Separate profile:** Debug builds are Postern (Dev), with their own pipe, data, credentials and login entry. `POSTERN_DEV` overrides | `Profile` |
| Carry dev data into the install? | **No.** The install starts fresh | |
| One-liner installer? | **Yes**, `irm …/install.ps1 \| iex`, verifying Setup against `SHA256SUMS.txt` and refusing an elevated shell | `install.ps1`, `tools/test-install.ps1` |
| Should Postern itself run elevated? | **No.** A UAC-elevated start shows a warning and exits with 5. UAC-off machines aren't refused (there's no other token to use) | `Elevation` |
| Graph via WAM (S5) | **Deferred.** Internal approval for an Entra app registration will take time | `docs/spike-plan.md` S5 |
| Merge order | **No merging during greenfield work.** Postern keeps building on `poc/walking-skeleton`; Perch stays on `spike/postern-transport` | |
