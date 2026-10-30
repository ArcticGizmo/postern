# Changelog

All notable changes to Postern are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

- One Postern window with a page per job, instead of a window per tray item
- Connections: built-in templates for 14 services, or your own as JSON
- Several accounts per service (`github` and `github-work`, side by side)
- A credential only goes to the address it was entered for
- How it works page, with copy-and-paste client code
- Changelog page, and a "What's new" window after an update
- New icon (a gate, unsurprisingly)

---

## [v0.1.0] - 2026-10-22

- First release: a tray app that holds your GitHub token so your apps don't have to
- Approve each app once, limited to the methods and paths it asked for
- Revoke an app and its next request fails, with no token to rotate
- Audit log of every call, and how much rate limit is left
- `postern-cli` for scripts
- Starts at sign-in and restarts itself if it crashes
- One-line install, and in-app updates when you click for them (never before)

---
