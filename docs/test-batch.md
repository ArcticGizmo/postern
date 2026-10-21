# Test batch

Everything that needs a human (a PAT, a click, a second account, a sign-out), queued in one place. Work top to
bottom; each section says what to run, what should happen, and what to send back. Anything marked *paste-safe*
already redacts your SID, profile path and GitHub login.

Built on branches:

- **postern** `poc/walking-skeleton` (on top of `spike/pipe-transport`)
- **perch** `spike/postern-transport`, in the worktree `..\perch-postern` (off `main`). Your `work-items` checkout
  is untouched.

## 0. Build and the automated checks (about 3 minutes, no human)

```powershell
cd C:\Users\JonHowell\Documents\git\personal\postern
dotnet build postern.slnx; dotnet build spikes\consumers\consumers.slnx; dotnet build tools\Postern.FakeUpstream
dotnet test tests\Postern.Tests\Postern.Tests.csproj                                # 96 tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke.ps1               # real exes, crash + restart
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\consumers\run.ps1        # Octokit, Node, poller
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\consumers\perch-e2e.ps1  # Perch's Postern path
```

Expected: all green. These use their own pipes, data folders and a fake GitHub provider (`fakegh`), so they never
touch a Postern you're running or your real GitHub credential. *Send back:* any red line.

Binaries used below:

```powershell
$postern = "$PWD\src\Postern.App\bin\Debug\net10.0-windows\Postern.exe"
$cli     = "$PWD\src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe"
```

## 1. First run of the tray app

1. Create a **fine-grained PAT**: *Public repositories (read-only)*, 7-day expiry. (For Perch's private-repo PRs
   later, add *Pull requests: read* + *Metadata: read* on the repos you care about.)
2. Start `& $postern`. Expect a teal gate icon in the tray, and the **Credentials** window opens because there's
   no GitHub credential yet.
3. Credentials -> github -> Set -> paste -> Save. It should say "stored" and never show the token again.
4. Tray menu: header shows `Postern 0.1.0`; Apps..., Credentials..., Audit log..., Start at login, Open data folder,
   Quit Postern.

*Send back:* anything that looked wrong (layout, wording, icon) and whether the Credentials window took focus.

## 2. Register, approve, call (the core loop)

```powershell
& $cli register --name scripts --scope "GET github/user" --scope "POST github/graphql" --scope "GET github/repos/*/*/pulls"
```

In the approval window, check:

- [ ] Allow is disabled, counting down `Allow (2)`, then enables. Any key pressed during the countdown restarts it.
- [ ] **Enter** and **Escape** both mean Deny (Deny is the default button). Try it on a throwaway registration.
- [ ] Publisher says *Unsigned* in amber; Program is the postern-cli path.
- [ ] The three scopes are listed. Then click **Allow**; the CLI prints `Approved`.

Then:

```powershell
& $cli call --name scripts GET github/user                        # your profile JSON
& $cli call --name scripts GET github/user -i | Select-Object -First 25   # Postern-RateLimit-* and Postern-Upstream-Ms headers
& $cli call --name scripts POST github/graphql --data '{"query":"{ viewer { login } rateLimit { remaining } }"}'
& $cli call --name scripts GET github/repos/cli/cli               # exit 3, "scope-denied": not in scope
& $cli call --name scripts GET github/repos/cli/cli/pulls         # allowed
& $cli status --name scripts
```

On the work machine, the first call succeeding also proves **Cato's TLS re-signing works** with no extra
configuration.

*Send back:* pass/fail per line and the `Postern-Upstream-Ms` value against how long the call felt.

## 3. Re-register, revoke, deny, timeout

```powershell
& $cli register --name scripts --scope "GET github/user" --scope "GET github/notifications"   # replacement
```

- [ ] The window warns "It already has access", shows `+ GET github/notifications` in amber, and
      `- POST github/graphql` / `- GET github/repos/*/*/pulls` in grey (being removed). Allow it.
- [ ] Tray -> Apps... shows `scripts` once, approved, with the new scopes. Tick "Show denied, revoked and replaced"
      and the old registration shows as *superseded*.
- [ ] Apps... -> Revoke. Then `& $cli call --name scripts GET github/user` -> exit 3, `app-revoked`.
- [ ] `& $cli register --name denied-test --scope "GET github/user"` -> click **Deny** -> CLI prints `Denied`.
- [ ] Same again, but **close the window** with the X -> `Denied`.
- [ ] Same again, and **ignore it for 2 minutes** -> the window closes itself, CLI prints `Denied`.

## 4. The prompt while you're busy (foreground lock)

```powershell
Start-Sleep 8; & $cli register --name focus-test --scope "GET github/user"
```

Click into another app (a browser, say) during the 8 seconds. When the prompt appears, does it come to the front,
or flash in the taskbar? Is the tray icon amber while it waits?

*Send back:* which one happened. This decides whether the window needs more (a toast nudge, or a sound).

## 5. Audit log

Tray -> Audit log... Expect every call from sections 2-3: allowed in white, denied (`scope-denied`,
`app-revoked`, `no-token`) in amber, the registration events, the query shown as `?...` (never its contents),
and a `github/core` (and `github/graphql`) rate-limit line at the top.

## 6. Lifecycle: start at login, crash restart, quit

1. Tray -> **Start at login** (tick it). Check
   `Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run | Select-Object Postern` reads
   `"...\Postern.exe" --supervise`.
2. Quit Postern from the tray, **sign out and back in**. Expect *two* `Postern.exe` processes (supervisor + Postern)
   and the tray icon back. `& $cli status` works.
3. Task Manager -> end the Postern.exe that *isn't* the supervisor (the newer one; or use the pid from
   `& $cli status`). Expect the tray icon back within about 2 seconds.
4. Tray -> Quit Postern. Both processes exit; nothing restarts.
5. Untick **Start at login** if you don't want it yet.

*Send back:* any step that didn't happen, plus whether CrowdStrike or Defender said anything about the Run key or
the restarts. That's the reason a supervisor was chosen over a scheduled task, so it's worth knowing.

## 7. Consumers against real GitHub

With the tray running and the PAT stored (each asks for approval once):

```powershell
spikes\consumers\Octokit\bin\Debug\net10.0-windows\octokit-spike.exe --repo cli/cli          # paste-safe
node spikes\consumers\node\demo.mjs
spikes\consumers\Poller\bin\Debug\net10.0-windows\poller-spike.exe --repo cli/cli --interval 30 --count 10
```

The poller answers the open S2 question: its summary line shows GitHub's `x-ratelimit-remaining` across the
run. If the 304s are free, it drops only by the number of `200 changed` lines.

*Send back:* the Octokit output, and the poller's summary line.

## 8. Other user and elevated (S1 leftovers)

The other account can't read your profile, so copy the CLI somewhere it can:

```powershell
Copy-Item src\Postern.Cli\bin\Debug\net10.0-windows C:\Users\Public\postern-cli -Recurse
runas /user:<other-local-account> "cmd /k C:\Users\Public\postern-cli\postern-cli.exe status"
```

- [ ] **Other user:** refused at once with "the 'postern' pipe refused this account: it belongs to another user's
      Postern" (the pipe ACL). It must never show your Postern's status.
- [ ] **Elevated terminal, you:** `& $cli status` -> works. The pipe ACL grants your SID, which the elevated token
      also carries. **Decision for you:** should elevated callers be allowed? (Kestrel's default would refuse them.)

## 9. Perch: shadow, then cut over (a few days)

In `..\perch-postern`:

```powershell
dotnet build perch.slnx
$env:PERCH_GITHUB_TRANSPORT = 'shadow'
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
```

Approve `perch` when Postern asks (scopes: `GET github/user`, `POST github/graphql`). Use Perch normally. Then look at
`github-transport.log` in Perch's `logs` folder, next to its settings file (`DiagnosticLog.Dir`). Each line is `alerts: same ...`,
`pr <folder>: same ...`, or `DIFFERENT` with both summaries.

- [ ] Mostly `same`? *Send back:* any `DIFFERENT` lines, **with repo names and PR titles redacted** if they're
      work-confidential. They point at gaps in the `gh pr view` emulation (forks, renamed upstream branches).
- [ ] Then `PERCH_GITHUB_TRANSPORT=postern` and `gh auth logout`. The alerts strip and PR glyphs still work, and Perch now
      holds no GitHub token. (`gh auth login` again afterwards.)
- [ ] Revoke `perch` in Postern's Apps window while Perch runs: the alerts strip should show a Postern error, not crash.

## 10. Optional: the original S1/S2 spike

`spikes/pipe-broker/README.md` still has its own live-GitHub run (`Spike.Probe.exe live`). Sections 2 and 7 above
cover the same ground through the real product, so skip it unless you want the spike's timing numbers.

## Decisions waiting on you

| Question | Current behaviour | Where |
|---|---|---|
| Allow elevated callers? | Yes (ACL is by user SID) | `PipeServerFactory` |
| Is an unsigned app acceptable to approve? | Allowed, with an amber warning; identity is the exe path | `ApprovalWindow`, `CallerInfo.Matches` |
| CLI registrations all share one exe | They're told apart only by `--name` | `postern-cli` |
| Graph via WAM (S5) | Not started. Needs an Entra app registration with `Mail.Read`/`Calendars.Read` consent in the work tenant. Is that obtainable? | `docs/spike-plan.md` S5 |
| Merge order | Postern branches are local only. Perch's branch is a spike: the env var should become a setting before it merges | |
