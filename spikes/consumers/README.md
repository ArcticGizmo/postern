# Spike: consumers (C2 SDK + non-.NET, C3 Pylon-shaped poller)

Shows that existing clients can be pointed at Postern with a few lines, and that a long-running poller behaves.
See `../../docs/spike-plan.md` (C2, C3). Throwaway code; the Perch integration is the real consumer work.

| Consumer | What changes in the app | Result against the fake upstream (2026-10-21) |
|---|---|---|
| **Octokit.NET 14** (`Octokit/`) | `new Connection(..., new Uri("http://postern/github/"), anonymous credentials, new HttpClientAdapter(postern.CreateHandler), ...)` | ✅ `User.Current()`, `PullRequest.GetAllForRepository()`; out-of-scope `Repository.Get()` refused with `403 scope-denied` (readable from `ApiException.HttpResponse.Headers`); `GetArchive`'s cross-host redirect refused clearly (finding 2) |
| **Node 24** (`node/`) | `http.request({ socketPath: '\\\\.\\pipe\\postern', headers: { Host: 'postern', 'Postern-App-Token': ... } })` | ✅ GET, GraphQL POST, scope denial, `421` for a request addressed to another host |
| **Poller** (`Poller/`, Pylon-shaped) | `postern.CreateHttpClient("github")` + `If-None-Match` | ✅ 8 polls with Postern killed mid-run: 0 failures (one poll took ~440 ms while the supervisor restarted Postern); 304s don't move the fake's rate-limit count |

Run all three against the fake upstream (no GitHub, no human):

```powershell
dotnet build postern.slnx; dotnet build spikes\consumers\consumers.slnx; dotnet build tools\Postern.FakeUpstream
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\consumers\run.ps1
```

## Against real GitHub (needs your PAT in Postern and you clicking Allow)

With the tray app running on the default pipe and a GitHub credential stored:

```powershell
spikes\consumers\Octokit\bin\Debug\net10.0-windows\octokit-spike.exe --repo cli/cli
node spikes\consumers\node\demo.mjs
spikes\consumers\Poller\bin\Debug\net10.0-windows\poller-spike.exe --repo cli/cli --interval 30 --count 10
```

Each registers on first run, so you'll get one approval prompt per app. The poller's summary line shows whether
GitHub's `x-ratelimit-remaining` moved across the 304s (it shouldn't).

## Findings

1. **Octokit needs `GitHubClient(IConnection)`, not `GitHubClient(ProductHeaderValue, Uri)`.** The latter "fixes up" any
   non-github.com base address by appending `/api/v3/` (GitHub Enterprise convention), which breaks the Postern path.
2. **Octokit follows redirects itself** (its own `RedirectHandler` wraps the handler you give it). Seen with
   `Repository.Content.GetArchive`: the 302 to `https://codeload...` was followed, the pipe `ConnectCallback` dialled
   Postern anyway (it ignores the host), and .NET attempted a **TLS handshake against Postern's plain HTTP**. Nothing
   leaked (TLS fails before any header is sent), but the app got a baffling "SSL connection could not be established".
   **Fixed in Postern.Client:** `PosternHandler` now refuses any request not addressed to `http://postern/` before
   connecting or attaching the token, with a message that says to fetch pre-signed redirect targets directly. The
   broker's `421 misdirected-host` stays as the backstop for clients without that check (Node, hand-rolled).
   Consequence for consumers: Actions log downloads and archives need the app to read `Location` and fetch it
   itself, outside Postern. The URL is pre-signed, so it needs no auth.
3. **Node can't verify the pipe server without native code** (`GetNamedPipeServerProcessId`), and has no DPAPI. The
   spike accepts both gaps. Options for a real Node consumer: a tiny N-API addon, or shelling out to `postern-cli`.
4. **curl on Windows can't talk to a named pipe** (`--unix-socket` is AF_UNIX only), which is why `postern-cli call` exists.
