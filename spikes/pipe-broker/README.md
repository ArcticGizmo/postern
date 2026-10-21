# Spike: pipe broker (S1 transport and identity, S2 forwarding)

Proves the load-bearing part of Postern: a Kestrel broker that listens **only on a named pipe**, knows which process
is calling, refuses to share its pipe name, and forwards `/{provider}/**` to the real API with a secret from Windows
Credential Manager injected. See `../../docs/spike-plan.md` (S1, S2).

Throwaway code. `src/` will be rewritten from what this proves.

| Piece | What it is |
|---|---|
| `Spike.Broker` | Kestrel on `\\.\pipe\<name>`, YARP `IHttpForwarder`, Credential Manager, caller identity. Default pipe `postern-spike`, default provider `github` → `https://api.github.com` |
| `Spike.Probe` | Runs the checks. Starts its own brokers on random pipe names, so it never touches one you're running |
| `Shared/` | Win32 calls used by both: pipe PIDs, process identity, Authenticode, Credential Manager |

## Run it

```powershell
cd spikes\pipe-broker
dotnet build pipe-broker.slnx
.\out\Spike.Probe.exe all          # S1 + S2, local only, about 15 s. Exit code 0 = all passed
```

Probe output redacts your SID, profile path and GitHub login, so it's safe to paste back.

## What it proved (2026-10-21, local runs)

`all`: **22 passed, 0 failed.**

| Check | Result |
|---|---|
| Client verifies the pipe server (PID → exe path + user SID) before sending anything | ✅ |
| Broker identifies the caller (PID → exe → user → signature) | ✅ 17 ms first time (WinVerifyTrust), **0.9 ms** after that with the signature cached by path + size + mtime |
| Round trip, reused connection | **p50 0.12 ms**, p99 0.23 ms |
| Round trip, new connection each time (including both identity checks) | p50 0.51 ms, p99 0.82 ms |
| 64 concurrent 250 ms requests | 264 ms (no serialisation) |
| Streamed 100 MB download / 20 MB upload | byte-exact, about 215 MB/s |
| `\\localhost\pipe\...` (SMB path) refused | ✅, but only after fixing a gap (see below) |
| No TCP listener (`netstat`) | ✅ |
| Second broker on the same name | ✅ refuses to start (exit 3) |
| **Squatter** holds the name first | ✅ broker refuses to start; ✅ client refuses the impostor |
| Injected auth replaces a client-supplied `Authorization` | ✅ |
| `Cookie`, `Postern-*`, `X-Forwarded-*`, `traceparent` stripped | ✅ |
| Missing credential | ✅ 503 `credential-missing`, upstream never called |
| Upstream 302 | ✅ passed back, not followed |
| Client with auto-redirect on | ✅ caught: 421 `misdirected-host` |
| `If-None-Match` → 304 passes through | ✅ |
| Rate-limit headers → `Postern-RateLimit-*` and `/_postern/ratelimits` | ✅ |
| Authenticode on real binaries | `gh.exe` valid (GitHub, Inc.), `dotnet.exe` valid (Microsoft). `Git\cmd\git.exe` is **unsigned** |

## Gotchas found

1. **A NETWORK-deny ACE does not stop `\\localhost\pipe\...`.** The first run connected over SMB loopback despite
   the deny ACE. Loopback authentication reuses your own token, so there's no NETWORK SID to match. The fix is
   **`PIPE_REJECT_REMOTE_CLIENTS`**, which .NET's `NamedPipeServerStream` doesn't expose. The broker now calls
   `CreateNamedPipeW` itself and wraps the handle (`PipeAcl.Factory`). Kestrel's `CreateNamedPipeServerStream` hook
   accepts it without trouble. `--allow-remote` turns both protections off for the contrast run, and the localhost
   connection then succeeds again. The deny ACE stays as belt and braces for real remote logons.
2. **Kestrel's `CurrentUserOnly` (the default) replaces any `PipeSecurity` you give it**, and per the docs it "verifies
   both the user account and elevation level". It's off, and the broker builds its own descriptor: the user SID gets
   read/write and create-instance, nobody else is listed, and the first instance is created with `FirstPipeInstance`.
3. **First-instance semantics work**, but only because the broker owns pipe creation (gotcha 1). If anything holds
   the name, `CreateNamedPipeW` returns access denied on the first instance, and the broker exits without listening.
4. **The `ConnectCallback` redirect trap is real.** A client `HttpClient` with auto-redirect on sends the redirected
   request down the pipe, whatever its host. The broker's `Host: postern` check catches it, and Postern.Client
   must turn auto-redirect off.
5. **Identity has to be resolved on every request, not only where it's used.** The first version only looked it up
   in `/whoami`, which made the new-connection latency look better than it is. It's now middleware (fail closed),
   and the numbers above include it.
6. **GitHub rejects requests without a `User-Agent`**, and `HttpClient` sends none by default. The broker adds
   `Postern-Spike/0.1` when the client didn't send one.
7. **`C:\Program Files\Git\cmd\git.exe` is unsigned** (the shim; the real `git.exe` is elsewhere). That matters for
   the later git credential helper: identify git by exe path, not by signer.

## Still to prove: needs you

### A. Live GitHub, plus corporate TLS if you run it on the work machine

Create a **fine-grained PAT** with *Public repositories (read-only)* and a 7-day expiry. That's enough for
`/user`, GraphQL and a public tarball.

```powershell
.\out\Spike.Broker.exe set-credential github     # paste the PAT; input is hidden. Stored as postern:github:default
.\out\Spike.Probe.exe live --repo cli/cli         # the probe never sees the PAT, only the broker does
.\out\Spike.Broker.exe delete-credential github  # when done
```

What it checks: `GET /user` and `POST /graphql` through the pipe; whether a **304 really costs nothing**
(`x-ratelimit-used` before and after); `core` and `graphql` buckets in `/_postern/ratelimits`; Postern's overhead
on real calls (client total minus upstream time); and the tarball `302` passed back, not followed. Run on the work
machine, a pass also means Cato's re-signing works through .NET's default trust with no extra configuration.

### B. Another Windows user can't connect

Other accounts usually can't read your profile, so copy the build somewhere they can:

```powershell
Copy-Item .\out C:\Users\Public\postern-spike -Recurse
.\out\Spike.Broker.exe                            # window 1: you, default pipe postern-spike
runas /user:<other-local-account> "cmd /k C:\Users\Public\postern-spike\Spike.Probe.exe connect"
```

Expected: `REFUSED: UnauthorizedAccessException`. `connect` prints the account name, so redact it before pasting.

### C. An elevated terminal (same user)

```powershell
.\out\Spike.Broker.exe                            # normal terminal
.\out\Spike.Probe.exe connect                     # in an *elevated* terminal
```

Expected: `CONNECTED`. The ACL grants your user SID, which the elevated token also carries. Kestrel's default
(`CurrentUserOnly`) would have refused this. Decide whether that's what Postern should do: an elevated tool is
still you, but it's also the easiest way to break things.
