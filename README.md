# postern

A local credential broker for Windows. You store your credentials once, in Postern. Apps on the machine call
GitHub, GitLab, Azure DevOps, Jira, Octopus and the like **through** Postern over a named pipe; Postern checks each
request against what you approved for that app, adds the credential, and forwards it. The app never holds the secret.

- Why and what: [`docs/scope.md`](docs/scope.md), [`docs/prior-art.md`](docs/prior-art.md)
- Connections, built-in services and custom ones: [`docs/connections.md`](docs/connections.md)
- How it's being proved: [`docs/spike-plan.md`](docs/spike-plan.md) (status at the top)
- What needs a human next: [`docs/test-batch.md`](docs/test-batch.md)
- Releases, installing and updates: [`docs/releasing.md`](docs/releasing.md)

## Install

From a normal (not elevated) PowerShell:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/postern/main/install.ps1 | iex
```

It downloads `Postern-win-Setup.exe` from the latest [release](https://github.com/ArcticGizmo/postern/releases),
checks it against the release's `SHA256SUMS.txt`, and runs it. No admin rights are needed. Click the tray icon to open
Postern, add your GitHub token under Connections, and tick Start at login under General. Updates are on the Updates page.

Debug builds run as a separate **Postern (Dev)** (pipe `postern-dev`, its own data and credentials), so they can run
next to an installed copy. See [`docs/releasing.md`](docs/releasing.md).

## Layout

| Path | What |
|---|---|
| `src/Postern.Core` | Policy: scopes, registrations, tokens, access decisions, audit and rate-limit models. No Windows/UI deps |
| `src/Postern.Broker` | The request path: named-pipe Kestrel host, caller identity, YARP forwarding, Credential Manager, SQLite |
| `src/Postern.App` | `Postern.exe`, the Avalonia tray app that hosts the broker (approval prompt; Apps, Connections, Audit log, General and Updates pages) |
| `src/Postern.Client` | The library apps reference: pipe handler with server verification, registration, DPAPI token |
| `src/Postern.Cli` | `postern-cli`: register/call/status from scripts, and `credential set` |
| `tests/Postern.Tests` | Unit tests plus end-to-end tests through a real pipe to a fake upstream |
| `scripts/smoke.ps1` | The real exes end to end, including crash -> supervisor restart -> client retry |
| `install.ps1` | The one-liner installer: latest release's Setup, checked against `SHA256SUMS.txt`. Tested by `tools/test-install.ps1` |
| `scripts/publish.ps1` | A release built locally into `releases\`, the same way the release workflow does |
| `.github/workflows` | `ci.yml` (build and test on pull requests), `release.yml` (a `v*` tag becomes a GitHub Release) |
| `scripts/shadow-stack.ps1` | Starts several `postern-shadow` apps at once against your Postern (approval queue, then gh vs Postern comparisons) |
| `spikes/` | Throwaway proofs: `pipe-broker` (S1/S2), `consumers` (Octokit, Node, poller, Perch E2E) |
| `tools/` | `fake-upstream` (GitHub-shaped stand-in), `postern-snapshots` (offscreen UI renders to `captures/`), `postern-shadow` (asks gh and Postern the same questions and logs whether they agree) |

## Quick start (dev)

```powershell
dotnet build postern.slnx
dotnet test tests\Postern.Tests\Postern.Tests.csproj
.\src\Postern.App\bin\Debug\net10.0-windows\Postern.exe          # tray; add a GitHub PAT under Connections
$cli = ".\src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe"
& $cli register --name scripts --scope "GET github/user"         # approve in the window
& $cli call --name scripts GET github/user
```

Or just `.\run.cmd`, which builds and starts the dev tray in one go and passes its arguments on to `Postern.exe`.

`Postern.exe` options: `--headless` (no tray; registrations are denied), `--supervise` (restart on crash),
`--pipe NAME` (default `postern`, or `postern-dev` for a Debug build), `--data-dir DIR` (default
`%LOCALAPPDATA%\Postern.Data`, or `Postern.Dev`), and in Debug builds only
`--dev-auto-approve name1,name2` for unattended tests.
