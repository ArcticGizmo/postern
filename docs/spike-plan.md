# Postern spike and proof-of-concept plan

How to take `scope.md` from paper to something Perch depends on, testing the riskiest assumptions first. Spikes
are throwaway code under `spikes/<name>/`, and each gets a README that records **what it proved**, in the same
style as Perch's `spikes/reserve-edge`. Nothing in a spike goes into `src/` without being rewritten.

## Status

| Spike | State |
|---|---|
| S1 pipe transport and identity | ✅ Proved locally (`spikes/pipe-broker`). Waiting on two manual checks: another user, elevated terminal |
| S2 forwarding | ✅ Proved against a fake upstream. Live GitHub and corporate TLS are waiting on a PAT run |
| S3–S5 | Not started |

S1 changed the design in one place: blocking remote clients needs `PIPE_REJECT_REMOTE_CLIENTS`, so Postern
creates the pipe handle itself instead of relying on .NET or Kestrel defaults. See the spike README.

## Recommended stack

| Decision | Recommendation | Why |
|---|---|---|
| Runtime | .NET 10, `TreatWarningsAsErrors` | Matches Perch and Shrike. Kestrel listens on named pipes natively |
| Host shape | **One per-user process**: an Avalonia tray app that hosts the broker through the generic host | Same pattern as Perch and Shrike. The approval prompt lives in the process that holds the pending registration, so there is no admin IPC channel to secure. One thing to auto-start |
| Not a Windows service | Runs in the user's session | DPAPI CurrentUser, Credential Manager and WAM all need the user's logon session. A LocalSystem service can't use them cleanly |
| Proxy engine | **YARP direct forwarding** (`IHttpForwarder`) behind Postern's own middleware | Streams bodies, handles hop-by-hop headers and is maintained by Microsoft. Postern writes only the policy (identity → scope → inject) and leaves the HTTP plumbing to YARP |
| Secret store | Windows Credential Manager (generic credentials, `postern:<provider>:<account>`) | As scoped. Blob limit is 2,560 bytes, enough for PATs and API keys |
| App tokens | 256-bit random. Postern stores only a SHA-256 of the token, and the client stores the token with DPAPI | A leaked registrations file can't be replayed |
| Registrations and audit | SQLite (one file, WAL) | Audit queries ("what did Perch call today?") are trivial. Same choice Pylon is leaning towards |

### Keeping the security-critical part small inside one process

The scope asks for a small surface that changes rarely. Separate projects enforce that boundary even though it
all runs as one process:

```
src/Postern.Core      scopes, registrations, audit, rate-limit accounting. No Avalonia, no Kestrel. Most tests live here
src/Postern.Broker    Kestrel pipe host, YARP forwarder, auth strategies, credential store
src/Postern.App       Avalonia tray: approvals, credentials, app list, audit viewer. Hosts Broker. Has --headless
src/Postern.Client    Consumer library: pipe handler, registration, DPAPI token, server verification, retry
src/Postern.Cli       postern.exe: register / call / status (later: git-credential)
tests/Postern.Tests
spikes/
```

**When to split into two processes:** if UI changes start forcing broker restarts often enough to annoy clients,
or if Avalonia's dependencies become a patching burden. `Postern.Broker` is already a library, so the split means
adding an admin pipe and a host. It doesn't mean a rewrite.

## Phase 0: spikes, riskiest first

### S1. Pipe transport and caller identity (load-bearing; do this first)

Kestrel `ListenNamedPipe` with a minimal echo endpoint, plus an `HttpClient` that uses a `SocketsHttpHandler.ConnectCallback` returning a `NamedPipeClientStream`.

Prove:
- Requests and streamed response bodies work, including several concurrent requests (check how Kestrel sizes the
  pipe instance pool).
- The **caller PID** can be read with `IConnectionNamedPipeFeature` → `GetNamedPipeClientProcessId`, then resolved
  to an exe path and its Authenticode signer (`WinVerifyTrust`). Measure the cost, and cache the result per
  connection.
- **ACL:** a second local account (`runas /user:...`) can't connect. `netstat -ano` shows no TCP listener.
- **Pipe squatting.** The named-pipe namespace is shared by every session, so another user can create
  `\\.\pipe\postern` before Postern starts. This is a cross-user threat, which the trust model says is covered.
  Prove both sides:
  - Postern refuses to start if the pipe already exists (first-instance semantics). Check whether Kestrel exposes
    this or whether it needs `NamedPipeTransportOptions` / a custom `PipeSecurity`.
  - The **client** verifies the server with `GetNamedPipeServerProcessId`, checking that the process runs as the
    same user SID and from the expected exe. It refuses to send its app token to an impostor.
- **Pipe-hop overhead.** Measure p50 and p99 against a local fake upstream so the cost is a known number.

### S2. Forwarding, injection and fail-closed behaviour

YARP forwarder in front of `api.github.com`, using a real fine-grained PAT read from Credential Manager.

Prove:
- `GET /github/user` and `POST /github/graphql` work end to end.
- **Header hygiene.** A client-supplied `Authorization`, `Cookie` or `Postern-App-Token` header never reaches
  upstream. The client can't override the injected auth.
- **Redirects.** GitHub returns `302` to signed URLs on other hosts for Actions logs and archives. Postern must
  *not* follow a redirect while carrying auth to another host. Decide whether to pass the 302 back or follow it
  without auth. Also prove the **client side**: a `ConnectCallback` ignores the host, so a client that
  auto-follows a redirect would send the request for `objects.githubusercontent.com` down the pipe. Postern.Client
  must turn auto-redirect off, or route only the `postern` host to the pipe.
- **Conditional requests** pass through untouched. `If-None-Match` goes up, `304` comes back, and the call doesn't
  use up any rate limit. This is Pylon's whole model, so prove it now.
- **Rate limits.** Read `x-ratelimit-*` per resource bucket (`core`, `graphql`, `search`) and return
  `Postern-RateLimit-*` headers.
- **Postern errors are clearly Postern's.** Scope-denied, unregistered and missing-credential errors come back as
  `problem+json` with a `Postern-Error` header, so a client can tell "Postern said no" from "GitHub said 403".
- **Fail closed.** If the credential is missing or Credential Manager can't be read, Postern returns an error and
  never forwards the request unauthenticated.
- **Corporate TLS** on the work machine (Cato). The expected result is that Windows' trust store already covers it.
  Record either way.

### S3. Approval UX and credential setup (Avalonia)

Prove:
- The registration flow when the app is waiting on approval: `POST /_postern/register` either holds the request
  open (long-poll, about 2 minutes) or returns `202` and the client polls. Pick one.
- The approval prompt is a **small topmost Avalonia window**, not a toast. It shows the app name, exe path, signer
  (or "unsigned"), and each requested scope on its own line. Toasts from unpackaged apps need AUMID setup, are
  easy to dismiss by accident and can't show scopes clearly. A toast can still be used as a nudge that opens the
  window.
- **Foreground lock:** check whether the window can get focus when the request comes from a background process. If
  it can't, flash the window and badge the tray icon.
- Credential entry is write-only: you can paste and replace a PAT, but Postern never shows it again.
- Revoking an app takes effect on the very next request.

### S4. Lifecycle: auto-start, restart, single instance

Prove:
- Auto-start at login. Compare a **Task Scheduler logon task** (with restart-on-failure) against an **HKCU Run key**
  plus a tiny supervisor. Verify Task Scheduler's restart-on-failure really fires when a running process crashes.
  It's known to be fiddly, and its minimum interval is 1 minute.
- `Stop-Process -Force` → Postern is back within the target time, with no user action.
- A second launch exits cleanly (named mutex, with the existing pipe as backstop).
- What a client sees during a restart: connection refused, then a retry with backoff in Postern.Client.

### S5. Graph through WAM (run in parallel; mostly an external risk)

Not needed until stage 3, but it is the assumption most likely to be blocked by the organisation rather than
the code. Perch's calendar probe deliberately went the "no Entra app" route, which hints this may already be a
constraint.

Prove:
- **Paper first:** can you get a public-client Entra app registration in the work tenant, with redirect URI
  `ms-appx-web://microsoft.aad.brokerplugin/{clientId}` and consent for `Mail.Read` / `Calendars.Read`? If not, the
  Graph stage is blocked and the scope should say so.
- **Code:** MSAL plus `Microsoft.Identity.Client.Broker`, `AcquireTokenSilent` with the OS account, and the parent
  HWND from an Avalonia window. Confirm Postern never sees a refresh token.
- Check that the auth-strategy abstraction from S2 fits "acquire a token per request" as well as "static secret".

## Phase 1: walking skeleton (v1 scope, thin)

Build the `src/` projects for real, using what the spikes proved. Ship only:
- The GitHub provider (PAT), scopes in the form `METHOD provider/path-glob`, registration and approval, audit rows,
  and rate-limit headers.
- **Postern.Client** for .NET consumers: `PosternHandler` (pipe plus server verification), `RegisterAsync`, a
  DPAPI token cache, and transient retry.
- **Postern.Cli** as the first consumer and the test driver: `postern register --name smoke --scope "GET github/user"`,
  then `postern call GET github/user`. curl on Windows can't talk to a named pipe, so the CLI fills that gap for
  scripts.
- **Integration tests** run the broker on a random pipe name against a fake upstream (a Kestrel TCP server or
  WireMock.Net), so every fail-closed path is tested without touching GitHub:

| Test | Expectation |
|---|---|
| No token / wrong token | 401 from Postern, upstream never called |
| Token valid, caller exe doesn't match the registration | 403, audit row written |
| Path outside scope | 403 `scope-denied`, upstream never called |
| Client sends its own `Authorization` | Stripped. Upstream sees only the injected auth |
| Credential missing | 5xx from Postern, upstream never called |
| Upstream 302 to another host | Not followed with auth |
| Upstream 304 | Passed through unchanged |
| App revoked mid-session | The next request is denied |

## Phase 2: proving consumers

The scope names Perch as the first client. Perch is a good test because it calls GitHub in two different ways
today:

| Perch call site | Today | Under Postern |
|---|---|---|
| `GitHubAlertsClient` | `gh api graphql --input -` | `POST github/graphql`. A direct port |
| `PrStatusService` | `gh pr view --json ...` in the repo's cwd (gh works out repo and branch itself) | Read the remote and branch with Perch's existing `GitRunner`, then run a GraphQL `pullRequests(headRefName:)` query |
| Readiness checks | `gh --version`, `gh auth status` | `postern status` / the pipe is present and the app is registered |

**Answering the open question about `gh`:** switch Perch to `HttpClient` through Postern. Supplying `GH_TOKEN` under
v1 would hand Perch the raw PAT, which defeats the point. Revisit `GH_TOKEN` when GitHub App installation tokens
(stage 4) make a short-lived, down-scoped token safe to hand out.

### C1. Perch, in three steps

1. **Transport seam.** Add `IGitHubTransport` to Perch with `GhCliTransport` (today's behaviour) and
   `PosternTransport`. Perch also has a macOS head (`Perch.Platform.Mac`), and Postern is Windows-only, so the
   `gh` path stays as the fallback permanently. Choose Postern when the pipe exists and Perch is registered.
   Otherwise use `gh`.
2. **Shadow mode** (a settings flag) for about a week. Perch calls both transports, uses `gh`'s result, and logs
   normalised differences. This proves equivalence on real traffic before anything depends on it. Postern's
   audit log for the week gives the **real scope list**, which answers the "scope format" open question with data
   instead of guesswork.
3. **Cutover.** Make Postern primary and run `gh auth logout` on the test machine. If PR status and alerts still
   work, Perch's secret really is gone.

### C2. A third-party SDK and a non-.NET runtime

This proves the claim that "most SDKs can be pointed at it" with a few lines each, as spikes, not product code:
- **Octokit.NET** through `new HttpClientAdapter(() => new PosternHandler(...))`.
- **Node** `http.request({ socketPath: '\\\\.\\pipe\\postern', ... })`. Node supports Windows named pipes
  natively, which covers any TypeScript tooling.
- Record the runtimes that *can't* do it simply (curl, Python `requests` without a custom adapter), and point
  them at `postern.exe` instead.

### C3. A Pylon-shaped probe (before Pylon exists)

A console app of about 50 lines that polls `github/repos/{o}/{r}/pulls` every 30 seconds with ETags through
Postern, as a separately registered app. Leave it running across a Postern restart and a revoke/re-approve.

Prove:
- 304s pass through and don't use rate-limit budget.
- A long-running client recovers from Postern restarts without help.
- Two registered apps show up separately in the audit log and rate-limit view.

This de-risks Pylon stage 1 cheaply, and it gives data for the scope's check: "if Postern alone removes most of the
pain, revisit how much Pylon is worth".

## Exit criteria for the proof of concept

- [ ] Perch has run on Postern for a week with `gh` logged out, and PR status and alerts work.
- [ ] Revoking Perch in the tray denies its next request, and Perch shows a clear "Postern access revoked" state
      rather than a generic error.
- [ ] A forced kill of Postern recovers by itself, and Perch and the C3 probe carry on.
- [ ] Negative tests pass: other user, no TCP listener, pipe squatter detected by the client, all fail-closed rows.
- [ ] Pipe-hop overhead is measured and small compared with GitHub latency.
- [ ] The scope list Perch actually needed is written back into `scope.md`.
- [ ] A go/no-go note on WAM (S5).

## Deliberately deferred

The token tier and GitHub App, the git credential helper, Octopus and Jira, per-app rate-limit budgets, and the
HMAC-verify endpoint for webhooks. None of these change the transport, identity or approval design proved above.
Each is a new auth strategy or a new endpoint on the same pipeline.

## Suggested order

```
S1 ──► S2 ──► Phase 1 skeleton ──► C1 seam ──► C1 shadow (1 week) ──► C1 cutover
        └──► S3 ─┘        │                 └──► C2, C3 (during the shadow week)
S4 ───────────────────────┘
S5 (in parallel, starting with the paper check)
```
