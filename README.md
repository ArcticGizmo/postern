# postern

A local credential broker for Windows. You store your credentials once, in Postern. Apps on the machine call
GitHub (and later Graph, Octopus, Jira) **through** Postern over a named pipe; Postern checks each request against
what you approved for that app, adds the credential, and forwards it. The app never holds the secret.

- Why and what: [`docs/scope.md`](docs/scope.md), [`docs/prior-art.md`](docs/prior-art.md)
- How it's being proved: [`docs/spike-plan.md`](docs/spike-plan.md) (status at the top)
- What needs a human next: [`docs/test-batch.md`](docs/test-batch.md)

## Layout

| Path | What |
|---|---|
| `src/Postern.Core` | Policy: scopes, registrations, tokens, access decisions, audit and rate-limit models. No Windows/UI deps |
| `src/Postern.Broker` | The request path: named-pipe Kestrel host, caller identity, YARP forwarding, Credential Manager, SQLite |
| `src/Postern.App` | `Postern.exe`, the Avalonia tray app that hosts the broker (approval prompt, Apps, Credentials, Audit) |
| `src/Postern.Client` | The library apps reference: pipe handler with server verification, registration, DPAPI token |
| `src/Postern.Cli` | `postern-cli`: register/call/status from scripts, and `credential set` |
| `tests/Postern.Tests` | Unit tests plus end-to-end tests through a real pipe to a fake upstream |
| `scripts/smoke.ps1` | The real exes end to end, including crash -> supervisor restart -> client retry |
| `spikes/` | Throwaway proofs: `pipe-broker` (S1/S2), `consumers` (Octokit, Node, poller, Perch E2E) |
| `tools/` | `fake-upstream` (GitHub-shaped stand-in), `postern-snapshots` (offscreen UI renders to `captures/`) |

## Quick start (dev)

```powershell
dotnet build postern.slnx
dotnet test tests\Postern.Tests\Postern.Tests.csproj
.\src\Postern.App\bin\Debug\net10.0-windows\Postern.exe          # tray; add a GitHub PAT under Credentials
$cli = ".\src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe"
& $cli register --name scripts --scope "GET github/user"         # approve in the window
& $cli call --name scripts GET github/user
```

`Postern.exe` options: `--headless` (no tray; registrations are denied), `--supervise` (restart on crash),
`--pipe NAME`, `--data-dir DIR` (default `%LOCALAPPDATA%\Postern`), and in Debug builds only
`--dev-auto-approve name1,name2` for unattended tests.
