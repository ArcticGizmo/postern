# Postern scope

A local credential broker. You set your credentials up once, in Postern. Apps on the machine that need to call
GitHub, Octopus Deploy, Microsoft Graph and so on ask Postern to make the call for them. The secret stays in
Postern.

A postern is the small guarded side gate in a castle wall that only lets known people through. That's the model:
each app gets its own narrow door, not the main gate.

Sibling product: **Pylon** (`../pylon`), the local event broker. Pylon is a normal Postern client with no special
access.

## The problem

Every app that talks to an external system builds its own auth: a PAT in a `.env` file, an API key in a
settings JSON, its own OAuth refresh, its own retry and rate-limit handling, its own corporate-TLS fixes. The cost
grows as **apps × providers**. The secrets end up spread across the disk in different formats, and revoking one
app's access means rotating a credential that every other app also uses.

## What Postern is

- **One credential store** for the user, backed by Windows Credential Manager / DPAPI. No home-made crypto.
- **An auth-injecting proxy.** The app sends a plain HTTP request to Postern. Postern checks it against the app's
  allowlist, adds the provider's auth, forwards it over TLS and returns the response unchanged.
- **Per-app approval and scopes.** Each app is approved by the user and limited to particular providers, methods
  and paths.
- **An owner of the OAuth lifecycle:** device-code or interactive login, refresh and expiry, done once for every
  app.
- **An audit log** of which app called what, and when.
- **A rate-limit accountant.** All traffic goes through Postern, so it is the only thing that can see the whole
  budget for a provider.

## What Postern is not

- **Not a wrapper per endpoint.** Each provider is a base URL plus an auth strategy, so every endpoint works on
  day one, GraphQL included.
- **Not a team vault.** No sync, no sharing. It holds one user's credentials on one machine.
- **Not a cache.** Response caching, ETags for change detection, diffing and events belong to Pylon.
- **Not protection against malware running as the same user.** See the trust model.

## Trust model

| Threat | Covered? |
|---|---|
| Other users on the machine | Yes. The named pipe's ACL is limited to the current user's SID |
| Browser pages reaching a localhost port (DNS rebinding, CSRF-style) | Yes, because there is no TCP listener, only a named pipe |
| An app quietly getting access it was never approved for | Yes, through approval plus a per-app token |
| An approved app doing more than it needs | Limited by the allowlist scopes |
| A secret leaked by an app that logs or persists it | Mostly. Under the default proxy tier the app never holds the secret |
| Revoking one app | Immediate, with no rotation, for proxy-tier access |
| A same-user process replacing an approved app's exe | Signed apps: yes, the signer has to match. Unsigned apps: **no**, because identity is the path. This is the row below by another route |
| Malware running as the same user | **No.** It can already read browser cookies and Credential Manager. Don't claim otherwise |

**Fail closed:** if Postern can't authorise a request, the request fails. It never goes out unauthenticated or
skips the allowlist.

## App identity and approval

1. An app connects to `\\.\pipe\postern` and asks to register, saying which scopes it wants.
2. Postern shows an approval prompt: app name, executable path and signature, and the requested scopes.
3. Once approved, Postern issues an **app token**. The app stores it with DPAPI and sends it with every request.
4. On every request Postern also checks the caller's process with `GetNamedPipeClientProcessId` and makes sure its
   executable path or signature matches the registration. This is defence in depth, not the main boundary.
5. The user can revoke an app or narrow its scopes at any time.

Scopes are `provider + method + path glob`, for example:

```
perch:  GET   github/repos/*/*/pulls/**
perch:  POST  github/graphql
pylon:  GET   github/repos/org/*/actions/runs
pylon:  GET   octopus/api/*/events
```

GraphQL is a single endpoint, so its scope is coarse (allowed or not). Filtering at query level could come later
if it turns out to matter.

## Access tiers

| Tier | When | Examples |
|---|---|---|
| **Proxy** (default) | HTTP APIs | GitHub REST and GraphQL, Graph, Octopus, Jira |
| **Short-lived token** | Tools that can't be pointed at Postern | GitHub App installation tokens (1 hour, scopable to repos and permissions), Graph access tokens |
| **Protocol adapter** | Standard credential hooks | A git credential helper, so `git` works without a PAT on disk |
| **Raw secret** | Last resort | Long-lived keys with no token exchange. Each secret needs its own explicit approval and gets an audit entry each time it's handed out |

## Providers

| Provider | Auth strategy | Notes |
|---|---|---|
| GitHub | Fine-grained PAT (v1), GitHub App later | A GitHub App can mint installation tokens down-scoped per consumer, which makes the token tier much safer |
| Microsoft Graph (Outlook, Calendar) | MSAL with the **WAM** broker | WAM keeps the refresh token bound to the device. Postern acquires tokens silently and injects them, so it never stores Microsoft refresh tokens itself |
| Octopus Deploy | API key per server | Can't be down-scoped. Proxy tier only, ideally |
| Jira | API token | Scoped tokens have to go through the `api.atlassian.com` gateway, not the site URL (the site returns 200 with an empty body) |

A provider definition is a base URL, an auth strategy, rate-limit header names and optional TLS settings.

## Request shape

```
app → \\.\pipe\postern
      GET /github/repos/org/perch/pulls?state=open
      Postern-App-Token: <token>

Postern → api.github.com
      GET /repos/org/perch/pulls?state=open
      Authorization: Bearer <secret>
```

- Apps talk **plain HTTP over the named pipe**, and Postern does TLS upstream. Intercepting TLS isn't needed (NVIDIA
  OpenShell's docs warn that injection only works on plaintext).
- **Corporate TLS** (for example Cato re-signing HTTPS) is set up once in Postern instead of in every app.
- Postern returns provider rate-limit state in response headers so clients can back off before they hit the limit.
- Most SDKs can be pointed at it with a custom base URL or HTTP handler (Octokit, Graph SDK, `HttpClient` with a
  named-pipe `ConnectCallback`).

## Staging

1. **v1:** credential store, app approval, GitHub proxy, rate-limit accounting, audit log.
2. **Perch as the first client.** Move Perch's GitHub calls onto Postern. Perch's secrets are gone and the design
   gets proven on real use.
3. Graph through MSAL and WAM, then Octopus and Jira.
4. Token tier (GitHub App installation tokens) and the git credential helper.
5. **Pylon** as the second client.

Once step 2 is done, check: if Postern alone removes most of the pain, revisit how much Pylon is worth.

## Operational requirements

Postern sits in the request path of every client, so it has to be boring and reliable: auto-start at login,
auto-restart on crash, a small surface, careful releases. Because it's the security-critical component, it
should change rarely.

## Open questions

- **Tech stack.** .NET 10 to match Perch and the other personal apps? Kestrel can listen on named pipes.
- **Approval UI.** A tray app, a Windows toast with a button, or a small window?
- **Perch calls GitHub through `gh` today.** Should Perch switch to `HttpClient` through Postern, or should Postern
  supply a token to `gh` (`GH_TOKEN`) under the token tier?
- **Webhook signing secrets for Pylon's future push path.** Hand the raw secret to Pylon, or have Postern offer a
  "verify this HMAC signature" endpoint so the secret never leaves Postern?
- **Rate-limit budgets per app** (beyond reporting the remaining budget). Worth it, or overkill for one user?
- **Scope format.** Path globs may be too coarse or too fiddly. Validate against Perch's real calls.
