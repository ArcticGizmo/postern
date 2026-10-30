# Test bed

A Postern to try things against that can't leak anything, and an app to try them with. Three processes, each with its own window or tray icon, so stopping one shows exactly what the others see:

```
sallyport.exe ──\\.\pipe\postern-sandbox──▶ Postern (Sandbox) ──http://127.0.0.1:5599──▶ fake-upstream
     │                                      only loopback, fake credentials                 │
     └──────────────── GET /_seen: what did fake-upstream actually receive? ────────────────┘
```

The design sketch is [`testbed-design.html`](testbed-design.html).

## Starting it

```powershell
.\testbed.cmd                    # builds and starts all three
.\testbed.cmd --reset-sandbox    # the same, from a fresh sandbox
```

Quit Postern (Dev) and Postern (Sandbox) first: they run from the same `bin` folder, which the build replaces. Sallyport asks to be approved the first time, through Postern's own prompt.

From Postern (Dev), **Testbed → Start Postern (Sandbox)** does the Postern part. From the sandbox's own **Testbed** page you can open Sallyport, start or stop fake-upstream, and reset the sandbox.

## Postern (Sandbox)

`Postern.exe --sandbox`, or `POSTERN_SANDBOX=1` for postern-cli and the tools. It has its own pipe (`postern-sandbox`), data folder (`%LOCALAPPDATA%\Postern.Sandbox`) and credentials (`postern-sandbox:` in Credential Manager), and a sand-coloured icon.

What makes it safe:

- **Loopback only.** Saving a connection that points anywhere but 127.0.0.1, ::1 or localhost is refused. A request through one (from a hand-edited `providers.json`) is refused with `sandbox-host` and never sent. Upstream calls skip the system proxy.
- **Made-up credentials only.** Anything shaped like a real GitHub, GitLab, Slack, Anthropic, OpenAI, Atlassian or Octopus token is refused, in the app and in `postern-cli credential set`.
- **No update checks**, and the Testbed page never appears in the installed Postern.

A new sandbox is seeded with `github`, `github-work`, `jira`, `slack`, `octopus` and `anthropic`, each pointing at fake-upstream with a credential like `sandbox-not-a-secret-github-token`, plus `jira-empty`, which never gets one. `--reset-sandbox` deletes the sandbox's database, `providers.json` and `postern-sandbox:` credentials, then seeds it again.

## fake-upstream

`tools\Postern.FakeUpstream`, on `127.0.0.1:5599`. Under `/github`, `/jira`, `/slack`, `/octopus` and `/anthropic` it answers like each service, and refuses (401) a request whose credential didn't arrive the way that service expects. The root keeps the original GitHub shapes for `smoke.ps1` and the consumer spikes.

| Control | What it does |
|---|---|
| `GET /_seen?id=X` | What arrived carrying `X-Testbed-Id: X`, newest first, credentials masked, bodies hashed |
| `POST /_knobs` | `{ "service": "github", "fault": "429", "count": 1 }`. Faults: `429`, `500`, `hang`, `slow` (with `seconds`), `drop`. `"*"` is every service |
| `DELETE /_knobs` | Clears them |

Generated bodies (the big unicode issue, Octopus packages) carry `X-Fake-Body-Sha256`, so a client can check what arrived.

## Sallyport

`tools\Postern.Sallyport`. It references `Postern.Client` and nothing else from the solution, so it behaves like any other app. It talks to `postern-sandbox` by default, and refuses `postern` and `postern-dev` outright.

- **Scenarios** are in `scenarios\*.json`, one group per file. Each is a request and what should happen to it: the status, the `Postern-Error` code (or none), whether fake-upstream saw it, which headers it got, whether the body arrived byte for byte, and so on. Edit or add one and restart Sallyport; there's nothing to rebuild. The fields are documented on `Expectation` in `Scenario.cs`.
- **Run** checks one scenario, **Run all** checks the lot. Each shows the request as sent, the response, what fake-upstream received, and a verdict per expectation.
- **Soak** sends `GET github/user` every second and colours a strip: green for ok, amber for answered after waiting for the pipe, red for Postern unavailable, sand for refused by Postern. It pauses while scenarios run.
- **Crash Postern** kills the sandbox Postern's process, after checking that it really is the exe serving `postern-sandbox`. **Start Postern** starts it again under its supervisor.

### Drills

| Drill | How | Expect |
|---|---|---|
| Crash | **Crash Postern** | A red cell or two, maybe an amber one, then green, with a new pid. No new approval prompt |
| Quit | Postern (Sandbox)'s tray → Quit | Red until you **Start Postern**, then green with the same approval |
| Revoke | Postern's Apps page → Revoke Sallyport | Sand cells (`app-revoked`) at once; Sallyport doesn't ask again by itself. **Ask Postern** asks again |
| Wait for start | Open Sallyport with Postern not running | "waiting for Postern", then the approval prompt as soon as it starts |

## Headless

```powershell
.\scripts\testbed-run.ps1
```

Starts fake-upstream (or uses one already running) and a throwaway sandbox Postern on its own pipe and data folder, approves Sallyport with `--dev-auto-approve`, runs `sallyport --run-all` and exits with its code: 0 when every scenario passed. CI runs it after the unit tests.
