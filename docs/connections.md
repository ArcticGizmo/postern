# Connections

A connection is what an app's requests go through: a **name**, a **base URL**, the **fields** its credential is made of, and where each field goes on the request (**inject**). Apps route and scope by the name, so the name is the whole interface:

```
app → \\.\pipe\postern   GET /github-work/repos/acme/api/pulls
scope                    GET github-work/repos/acme/*/pulls
```

Manage them in Postern → **Connections**: add one from a template, set or replace its credential, remove it, or edit the lot as JSON. Changes apply on the next request; there's no restart.

## Several accounts for one service

Add a second connection from the same template, under another name (`github` and `github-work`, `jira` and `jira-client`). Each has its own credential, and an app asks for scopes on whichever it should use. One app can use both. The approval prompt lists each connection an app asks for, with the service and host it points at, so "github-work" is never just a name the app chose.

## Built-in templates

| Template | Base URL | What's sent | Credential |
|---|---|---|---|
| `github` | `https://api.github.com` | `Authorization: Bearer` | Personal access token |
| `github-enterprise` | `https://{host}/api/v3` | `Authorization: Bearer` | Personal access token |
| `gitlab` | `https://{host}/api/v4` (host defaults to gitlab.com) | `Authorization: Bearer` | Personal, project or group token |
| `bitbucket` | `https://api.bitbucket.org/2.0` | `Authorization: Bearer` | Repository, project or workspace access token |
| `azure-devops` | `https://dev.azure.com/{organization}` | Basic, empty user | Personal access token |
| `jira` | `https://{site}.atlassian.net` | Basic | Account email + API token |
| `confluence` | `https://{site}.atlassian.net/wiki` | Basic | Account email + API token |
| `octopus` | `https://{host}` | `X-Octopus-ApiKey` | API key |
| `linear` | `https://api.linear.app` | `Authorization` (no Bearer) | Personal API key |
| `sentry` | `https://{host}/api/0` (host defaults to sentry.io) | `Authorization: Bearer` | Auth token |
| `slack` | `https://slack.com/api` | `Authorization: Bearer` | Bot or user token |
| `openai` | `https://api.openai.com/v1` | `Authorization: Bearer` | API key |
| `anthropic` | `https://api.anthropic.com` | `x-api-key`, plus `anthropic-version` | API key |
| `datadog` | `https://api.{site}` (site defaults to datadoghq.com) | `DD-API-KEY`, `DD-APPLICATION-KEY` | API key + application key |

A `{host}`, `{site}` or `{organization}` is filled in when the connection is added. It has to be a single name (letters, digits, `.`, `-`, optionally `:port`), so it can't add a path or point the URL somewhere unexpected.

Microsoft Graph isn't here on purpose: it needs token acquisition through MSAL and WAM, not a stored secret.

## providers.json

The data folder's `providers.json` holds every connection except the built-in `github` (an entry named `github` replaces it, for GitHub Enterprise say). Postern → Connections → **Edit as JSON** edits it in place, validates before saving, and refuses a file that doesn't validate. Comments and trailing commas are allowed.

```jsonc
{ "providers": [
  // From a template. baseUrl only when the template's URL has a {host}, {site} or {organization} in it.
  { "name": "github-work", "template": "github" },
  { "name": "jira", "template": "jira", "baseUrl": "https://contoso.atlassian.net" },

  // Custom: anything that takes a static credential.
  { "name": "trello", "baseUrl": "https://api.trello.com/1",
    "fields": [ { "name": "key", "label": "API key" }, { "name": "token", "label": "Token" } ],
    "inject": [ { "query": "key", "value": "{key}" }, { "query": "token", "value": "{token}" } ] },

  // The original shorthand still works: auth is bearer, header (with authHeader) or basic (secret stored as user:token).
  { "name": "legacy", "baseUrl": "https://api.example.com", "auth": "header", "authHeader": "X-Api-Key" }
] }
```

A template entry can also carry its own `fields` and `inject`, which replace the template's.

### Fields

`{ "name": "token", "label": "API token", "secret": true }`. Names are lowercase letters, digits and `_`. `secret: false` shows the value while it's typed (an email address); it's still never shown back once stored. Up to six fields.

### Inject rules

Each rule sets exactly one of these. Values are templates: `{field}` is replaced with that field's value, and a value with no placeholders is sent as it is (an API version header).

| Rule | Sends |
|---|---|
| `{ "header": "Authorization", "value": "Bearer {token}" }` | A header. Any client-sent header of the same name is dropped first |
| `{ "basic": { "username": "{email}", "password": "{token}" } }` | `Authorization: Basic base64(username:password)`. Omit `password` when the username template already is `user:password` |
| `{ "query": "api_key", "value": "{key}" }` | A query parameter, replacing any the client sent under that name. Upstream access logs will record it, so use a header when the API offers one |

Validation refuses:

- a placeholder that isn't one of the fields, or a field that's never used
- headers Postern or HTTP own: `Host`, `Content-*`, `Connection`, `Transfer-Encoding`, `Proxy-*`, `Forwarded`, `X-Forwarded-*`, `Postern-*`, and the like
- the same header twice (Basic counts as `Authorization`), or the same query parameter twice
- non-ASCII or control characters in a value
- a base URL that isn't https (plain http only for loopback), or that carries a user, query, fragment or unfilled `{placeholder}`

## Where a credential may go

Each stored credential records the origin (scheme, host and port) it was entered for, and the proxy only sends it there. If a connection is repointed, whether in the app or by editing `providers.json` while Postern isn't running, its requests fail with `credential-invalid` until the credential is entered again, and the Connections page says why. Removing a connection deletes its credential.

Credentials stored before origins were recorded are bare GitHub tokens; they're trusted for `https://api.github.com` only.

In Credential Manager a credential is `postern:<connection>:default` (`postern-dev:` for the dev build), holding `{"origin": "...", "values": {...}}`.

## From scripts

```powershell
postern-cli credential list
postern-cli credential set github-work                     # prompts, input hidden
'{"email":"me@example.com","token":"..."}' | postern-cli credential set jira
postern-cli credential delete jira
```

A connection with several fields prompts for each, or takes a JSON object of them on stdin. `set` only works for a connection that exists; add it in the app first.
