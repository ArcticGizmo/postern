<h1 align="center">Postern</h1>
<p align="center">
 <img src="./landing-icon.png" width="150" />
</p>

<p align="center">
<strong>Keep your tokens in one place. Let your apps use them without ever holding them.</strong>
</p>

<br>

Every script and tool that talks to GitHub, Jira, Azure DevOps or Octopus wants a token of its own: a PAT in a `.env` file, an API key in a settings JSON. Before long, the same secrets are copied all over your disk, and taking access away from one app means rotating a token every other app uses too.

Postern is a **Windows** tray app that holds those credentials for you. Apps on your machine send their requests **through** Postern instead of straight to the service. Postern checks each request against what you approved for that app, adds the credential, and sends it on. **The app never sees the secret.**

- **You approve each app**, once, and see exactly which services and paths it's asking for.
- **Revoke one app** and it stops at once, with no token to rotate.
- **See every call** in the audit log, and how much of each service's rate limit is left.

Already know you want it? [Skip the details and install it ▼](#installing)

### At a glance

- **One store for your credentials**: kept in Windows Credential Manager, never shown back once saved.
- **Built-in services**: GitHub, GitHub Enterprise, GitLab, Bitbucket, Azure DevOps, Jira, Confluence, Octopus, Linear, Sentry, Slack, OpenAI, Anthropic and Datadog, plus anything else that takes a static key.
- **Several accounts per service**: `github` and `github-work` side by side, each with its own token.
- **An approval prompt you can trust**: it shows the program's path and publisher, and denies by default. Allow is held back for a moment, so a stray keypress can't approve anything.
- **Nothing to reach from the network**: apps talk to Postern over a named pipe only your Windows account can open. There's no port for a browser page or another user to find.
- **Quiet and dependable**: lives in the tray, starts when you sign in, restarts itself if it ever crashes, and updates in-app.

## Installing

From a normal (not admin) PowerShell:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/postern/main/install.ps1 | iex
```

That's the whole install. There's no admin prompt (it goes into `%LocalAppData%\Postern\`). You get a Start menu entry and a normal uninstaller in Settings → Apps, and Postern starts in the tray when it's done.

What the script does, in order: finds the latest release, downloads `Postern-win-Setup.exe` and `SHA256SUMS.txt`, **checks the installer against the checksums and deletes it rather than run it if they don't match**, then runs it. It's [`install.ps1`](install.ps1) in this repo. Read it before piping it into your shell, as you should with any installer.

Pin a version instead of taking the latest:

```powershell
$env:POSTERN_VERSION = '0.1.0'; irm https://raw.githubusercontent.com/ArcticGizmo/postern/main/install.ps1 | iex
```

PowerShell does the downloading, not a browser, so nothing is tagged with the mark-of-the-web, and this route never hits the **"Windows protected your PC"** SmartScreen dialog.

### Installer by hand

Prefer to click things? Download `Postern-win-Setup.exe` from the [latest release](https://github.com/ArcticGizmo/postern/releases/latest) and run it. The install is identical, and so are the updates.

A browser download *is* tagged with the mark-of-the-web, so SmartScreen shows the blue **"Windows protected your PC"** dialog. Click **More info → Run anyway**, or use the one-liner above to skip it. To check the download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Postern-win-Setup.exe').Line.Split()[0]
(Get-FileHash Postern-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

## Getting started

1. **Add a credential.** Click the tray icon to open Postern. It opens on **Connections** the first time: click **Set** on `github` and paste a [fine-grained personal access token](https://github.com/settings/personal-access-tokens). For another service, or a second GitHub account, click **Add connection**.
2. **Keep it running.** Under **General**, tick **Start Postern when I sign in to Windows**. Apps can't reach your services while Postern isn't running.
3. **Point an app at it.** Open **How it works** and pick a connection. It draws where a request goes and gives you copy-and-paste code for Node, .NET, Octokit.NET and PowerShell, with your names already filled in. Moving an existing client over takes two changes: its base URL becomes `http://postern/<connection>/`, and it connects to the pipe instead of a port.
4. **Approve it.** The first time the app runs, Postern asks you to allow it, listing everything it wants to call. From then on, **Apps** shows what each app may do and lets you revoke it, and **Audit log** shows what it did.

From a script, `postern-cli` (installed next to Postern) does the same in two lines:

```powershell
$cli = "$env:LOCALAPPDATA\Postern\current\postern-cli.exe"
& $cli register --name my-script --scope "GET github/user"   # approve it in the Postern window
& $cli call --name my-script GET github/user
```

## How it works

```
your app ──plain HTTP over \\.\pipe\postern──▶ Postern ──HTTPS + your credential──▶ api.github.com
           GET /github/user                     checks: which app? approved?        GET /user
           Postern-App-Token: <app's own key>   which connection? its credential    Authorization: Bearer …
```

1. **Who's calling?** Each app gets its own Postern token when you approve it. Postern also checks which program is really on the other end of the pipe.
2. **Allowed?** The method and path must match a scope you approved for that app, like `GET github/repos/*/*/pulls`.
3. **Add the credential**: only for the address it was entered for. Repoint a connection and its credential has to be entered again.
4. **Send it on** and hand the response back unchanged, with `Postern-RateLimit-*` headers when the service reports its limits.

If any step fails, the request gets a Postern error and never leaves the machine. Either way, it's in the audit log.

What Postern doesn't protect against: malware already running as you can read your browser's cookies and Credential Manager too. Postern keeps secrets out of apps' files and logs, and gives you a single place to see and cut off access. The trust model in [`docs/scope.md`](docs/scope.md) covers this in full.

## Updating

Postern checks GitHub in the background. When there's a new version, the tray menu shows **Update to x.y.z…** and the **Updates** page gets a badge. Nothing is applied until you click **Update and restart**. Postern is back within seconds, and apps reconnect on their own.

The one-liner only downloads and checks the installer, so a copy installed through it is an ordinary install: there's no need to run the script again.

## Uninstalling

Settings → Apps → Installed apps → Postern → Uninstall. It removes Postern, every credential it stored, and its Start at login entry. Your approvals and audit log stay in `%LocalAppData%\Postern.Data`; delete that folder too if you want them gone.

## Documentation

- Why and what: [`docs/scope.md`](docs/scope.md), [`docs/prior-art.md`](docs/prior-art.md)
- Connections, built-in services and custom ones: [`docs/connections.md`](docs/connections.md)
- Releases, installing and updates: [`docs/releasing.md`](docs/releasing.md)
- How it's being proved: [`docs/spike-plan.md`](docs/spike-plan.md) (status at the top)
- What needs a human next: [`docs/test-batch.md`](docs/test-batch.md)

## Development

Requirements: .NET 10 SDK

```powershell
dotnet build postern.slnx
dotnet test tests\Postern.Tests\Postern.Tests.csproj
.\run.cmd                                                       # builds and starts the dev tray; add a PAT under Connections
$cli = ".\src\Postern.Cli\bin\Debug\net10.0-windows\postern-cli.exe"
& $cli register --name scripts --scope "GET github/user"         # approve in the window
& $cli call --name scripts GET github/user
```

Debug builds run as a separate **Postern (Dev)**, with a pink icon, its own pipe (`postern-dev`), its own data and its own credentials, so they can run next to an installed copy. See [`docs/releasing.md`](docs/releasing.md).

`Postern.exe` options: `--headless` (no tray; registrations are denied), `--supervise` (restart on crash), `--pipe NAME` (default `postern`, or `postern-dev` for a Debug build), `--data-dir DIR` (default `%LOCALAPPDATA%\Postern.Data`, or `Postern.Dev`), and in Debug builds only `--dev-auto-approve name1,name2` for unattended tests.

To see the windows without launching the tray, `dotnet run --project tools\Postern.Snapshots` renders each one offscreen, with made-up data, to `captures/`.

### Layout

| Path | What |
|---|---|
| `src/Postern.Core` | Policy: scopes, registrations, tokens, access decisions, audit and rate-limit models. No Windows/UI deps |
| `src/Postern.Broker` | The request path: named-pipe Kestrel host, caller identity, YARP forwarding, Credential Manager, SQLite |
| `src/Postern.App` | `Postern.exe`, the Avalonia tray app that hosts the broker (approval prompt; Apps, Connections, Audit log, General, Updates and How it works pages) |
| `src/Postern.Client` | The library apps reference: pipe handler with server verification, registration, DPAPI token |
| `src/Postern.Cli` | `postern-cli`: register/call/status from scripts, and `credential set` |
| `tests/Postern.Tests` | Unit tests plus end-to-end tests through a real pipe to a fake upstream |
| `scripts/smoke.ps1` | The real exes end to end, including crash -> supervisor restart -> client retry |
| `install.ps1` | The one-liner installer: latest release's Setup, checked against `SHA256SUMS.txt`. Tested by `tools/test-install.ps1` |
| `scripts/publish.ps1` | A release built locally into `releases\`, the same way the release workflow does |
| `.github/workflows` | `ci.yml` (build and test on pull requests), `release.yml` (a `v*` tag becomes a GitHub Release) |
| `scripts/shadow-stack.ps1` | Starts several `postern-shadow` apps at once against your Postern (approval queue, then gh vs Postern comparisons) |
| `spikes/` | Throwaway proofs: `pipe-broker` (S1/S2), `consumers` (Octokit, Node, poller, Perch E2E) |
| `tools/` | `fake-upstream` (GitHub-shaped stand-in), `postern-snapshots` (offscreen UI renders to `captures/`), `postern-shadow` (asks gh and Postern the same questions and logs whether they agree), `Postern.IconGen` (icons from `postern.svg`) |

### Building a release (maintainers)

Push a version tag (`git tag v0.2.0; git push origin v0.2.0`) and the release workflow tests, builds, packs and publishes it, with `SHA256SUMS.txt`. `.\scripts\publish.ps1 0.2.0` does the same locally into `releases\`. Details are in [`docs/releasing.md`](docs/releasing.md).

### Icons

The logo lives in one vector file, [`postern.svg`](./postern.svg). Every raster asset is generated from it: the tray and window icons, the `.exe` and installer icon, the in-app logo and the README header. The dev build's pink and the amber shown while an approval waits are rendered from the same SVG with the fill swapped, so every colour is as sharp as the original.

After changing `postern.svg`, regenerate the assets and commit the results:

```powershell
.\tools\gen-icons.ps1
```

This writes `postern.ico`, `postern-dev.ico` and `postern-attention.ico` (each with a frame drawn at every tray and taskbar size from 16 to 256 px, so Windows never shrinks one), `icon.png` and `icon-dev.png` (256×256, the in-app logo), all in `src/Postern.App/Assets/`, and `landing-icon.png` (512×512).
