# Postern prior art

Searched on 2026-10-21. Nothing found is a local, Windows-native credential broker for desktop apps with per-app
approval and scopes. The pieces exist separately.

| Candidate | What it does | Why it doesn't fit as-is | What to take |
|---|---|---|---|
| **Nango** | OAuth flows, token storage and refresh, an auth-injecting proxy. Free self-hosting covers auth and proxy only | A server-side, multi-tenant backend for SaaS products. Elastic License | The provider-definition model: base URL plus auth strategy, no wrappers |
| **Composio / Arcade** | Cloud token vaults for AI agents | Cloud-first, and self-hosting is enterprise-only. **Composio's May 2026 breach** exposed about 5,000 GitHub OAuth tokens and about 5,000 API keys | A strong argument for keeping tokens local |
| **Local injecting proxies** (nono, secret-broker, Clavitor, AgentSecrets, Aegis, NVIDIA OpenShell) | The app calls localhost and the proxy adds the real key on the way out | Mostly static LLM API keys. No OAuth lifecycle, and most are young projects | Confirms the proxy pattern. OpenShell's note that injection needs plaintext HTTP |
| **Windows WAM** (via MSAL) | OS-level token broker for Microsoft accounts. Device-bound refresh tokens, SSO, Conditional Access | Microsoft identities only | **Use it** for Graph rather than storing refresh tokens |
| **Git Credential Manager** | Credential helper for git | git only | **Use it**, or put Postern's git helper behind it |
| **1Password `op run`** | Resolves `op://` references into a child process's environment | Hands the raw secret to the app | The reference-file idea, if a raw-secret tier is ever needed |
| **Windows Credential Manager / DPAPI** | Per-user secret storage | Storage only, no access control between apps | **Use it** as Postern's backing store |

Caveat: several of the comparison sources are vendor blogs (Nango, Arcade), and the small proxy projects' internals
weren't reviewed.

## Sources

- [Nango – Self-host Nango](https://nango.dev/docs/guides/platform/self-hosting/self-hosting)
- [Nango – Best self-hosted API integration platforms for AI agents](https://nango.dev/blog/best-self-hosted-api-integration-platforms-for-ai-agents/)
- [Composio – May 2026 security incident](https://composio.dev/blog/composio-may-2026-security-incident)
- [Material Security – The Composio breach](https://material.security/resources/the-composio-breach-one-token-10242-doors)
- [Arcade – Best AI agent authentication platforms](https://www.arcade.dev/blog/best-ai-agent-authentication-platforms/)
- [Developers Digest – Arcade vs Composio vs Nango vs Stytch](https://www.developersdigest.tech/blog/ai-agent-auth-platforms-comparison-2026)
- [secret-broker (GitHub)](https://github.com/Thiruvenkatesan1322/Secret_Broker)
- [nono – Credential injection](https://nono.sh/docs/cli/features/credential-injection)
- [Clavitor – Credential proxy](https://clavitor.ai/en/proxy)
- [AgentSecrets (DEV Community)](https://dev.to/the_seventeen/agentsecrets-the-complete-zero-knowledge-credential-platform-for-ai-teams-33g2)
- [NVIDIA OpenShell – Manage providers](https://docs.nvidia.com/openshell/sandboxes/manage-providers.md)
- [Microsoft Learn – Acquire a token using WAM](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)
- [1Password – Secret references](https://developer.1password.com/docs/cli/secret-references)
