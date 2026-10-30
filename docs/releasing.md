# Releasing and updates

Postern ships the same way as Perch: Velopack packages, published as GitHub Releases on
[ArcticGizmo/postern](https://github.com/ArcticGizmo/postern), a one-liner installer, and an in-app updater that
reads the releases. The repo is public, so installing and update checks need no token.

## Installing

From a normal (not elevated) PowerShell:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/postern/main/install.ps1 | iex
```

`install.ps1` looks up the latest release (or `$env:POSTERN_VERSION`), downloads `Postern-win-Setup.exe`, checks it
against the release's `SHA256SUMS.txt`, refuses on a mismatch, and runs it. It refuses an elevated session, because
Setup starts Postern when it finishes and Postern won't run elevated. The script is fetched from `main`, so it only
works once it's merged there. `tools/test-install.ps1` tests it, and CI runs those tests.

You can also download Setup from the release page yourself and check it with `Get-FileHash`.

## Cutting a release

With Claude Code, two skills (in `.claude/skills/`) do the whole thing:

- **`/bump-version`** sets the next patch version in `Directory.Build.props` (from the last tag, not the props file) and writes its `CHANGELOG.md` section from the commits since that tag. It doesn't commit; review the entries, then commit both files on the branch.
- **`/release-next-version`** opens a PR for the branch, waits for CI, squash-merges, tags the new `main` head with the props version, and watches the Release workflow until the GitHub Release is up with its assets. It stops at the first failure and says what state it left things in.

By hand:

1. Push a tag: `git tag v0.2.0; git push origin v0.2.0`. The tag is the version, and it has to be
   `vMAJOR.MINOR.PATCH`. Keep `<Version>` in `Directory.Build.props` and a `## [v0.2.0]` section in `CHANGELOG.md`
   in step with it.
2. `.github/workflows/release.yml` then:
   - runs the tests (a red test never ships)
   - publishes `Postern.exe` and `postern-cli.exe` self-contained into one folder, stamped with the version
   - packs it with `vpk` (pinned in `.config/dotnet-tools.json`, the same version as the `Velopack` package), with a
     Start menu shortcut only
   - writes `SHA256SUMS.txt` over exactly the files being uploaded
   - creates the GitHub Release with generated notes
3. The build job is read-only. Only the final job can write, and it builds nothing. Actions are pinned to commit
   SHAs, and Dependabot proposes bumps weekly.

`scripts/publish.ps1 <version>` does the same locally, into `releases\` (git-ignored).

## What a release contains

| Asset | For |
|---|---|
| `Postern-win-Setup.exe` | Installing. No admin rights; installs to `%LOCALAPPDATA%\Postern` and starts Postern |
| `Postern-win-Portable.zip` | Running without installing |
| `Postern-<version>-full.nupkg`, `RELEASES`, `releases.win.json`, `assets.win.json` | The update feed the in-app updater reads |
| `SHA256SUMS.txt` | What `install.ps1` checks Setup against, and anyone can check any asset against |

## Where things live

| What | Installed | Dev build (Debug) |
|---|---|---|
| Postern and postern-cli | `%LOCALAPPDATA%\Postern\current\` (the path stays the same across updates, so clients' server check keeps passing) | `src\…\bin\Debug\…` |
| Pipe | `postern` | `postern-dev` |
| Data: `postern.db`, `providers.json`, `logs\` | `%LOCALAPPDATA%\Postern.Data` | `%LOCALAPPDATA%\Postern.Dev` |
| Credentials (Credential Manager) | `postern:<provider>:<account>` | `postern-dev:<provider>:<account>` |
| Start at login | `HKCU\…\Run\Postern` | `HKCU\…\Run\Postern (Dev)` |
| Tray | Postern | Postern (Dev) |
| Icon | Purple (`postern.svg` as drawn) | Pink (both turn amber while an approval waits) |

**Dev builds are a separate profile**, like Perch (Dev), so one can run next to an installed Postern without either
touching the other. A Debug postern-cli and the Debug shadow apps follow the same rule, so they talk to the dev
Postern. `POSTERN_DEV=0` runs a Debug build as the real profile, any other value runs a Release build as Dev, and
`POSTERN_PIPE` still overrides the pipe for clients. The dev profile has its own credentials, so the first dev run
asks for a PAT.

**Uninstall** (Windows Settings → Apps) removes the install folder, every `postern:*` credential, the Start at login
entry and `HKCU\Software\Postern`. A PAT left behind for a broker that's gone would be a secret with no owner. The
data folder (approvals, audit log) stays; without a credential it can't call anything. The dev profile isn't
touched.

## Updates

- **The Updates page** checks GitHub the first time it's opened (and on **Check again**), and offers the newer
  version, saying what the restart will do. Nothing is applied until you click **Update and restart**.
- **A background check** runs 30 seconds after start and then every 6 hours. It only adds an "Update to 0.3.0…"
  item to the tray menu, a "new" badge on the Updates page, and a line in the tooltip. Anonymous GitHub API calls
  are limited per IP, and an office NAT shares one IP.
- **Applying:** Postern downloads the package, hands over to Velopack's updater, and exits with code 6. The
  supervisor treats 6 as "stop", so nothing holds the install folder. Velopack swaps `current\` and starts
  `Postern.exe --supervise` again (or plain `Postern.exe` if it wasn't supervised).
- **Clients** see a few seconds of "Postern isn't answering" and reconnect through their retry. Approval requests
  still waiting are denied, and those apps ask again.
- **A copy run from a build folder** isn't installed, so the window says so and nothing is checked.
- **What's new:** `CHANGELOG.md` is built into `Postern.exe`. The Changelog page shows all of it. Each start records
  its version in `settings.json` in the data folder, and the first start on a newer version opens a "What's new"
  window with the sections in between (so skipped versions are included). A fresh install shows nothing, and so
  does a version without a section. The page's checkbox, or the window's **Don't show again**, turns it off.

Updates aren't code-signed (nor is anything else yet), so trust rests on the GitHub repo and its release workflow.
That's why applying is always the user's click, never automatic.

### Testing an update without GitHub

Set `POSTERN_UPDATE_FEED` to a folder of `vpk pack` output, and the updater reads that instead of GitHub (the window
says so in amber):

```powershell
.\scripts\publish.ps1 0.1.0
.\releases\Postern-win-Setup.exe
.\scripts\publish.ps1 0.1.1
# Quit Postern from the tray, then:
$env:POSTERN_UPDATE_FEED = "$PWD\releases"; & "$env:LOCALAPPDATA\Postern\current\Postern.exe"
```
