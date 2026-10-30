---
name: bump-version
description: Bump the Postern version and refresh the changelog. Determines the next version from the LAST GIT TAG (not Directory.Build.props, which can drift), bumps the <Version> in Directory.Build.props, backfills changelog sections for any tagged versions that were never documented, summarises every change since the last tag into a new CHANGELOG.md section (keeping the dry humour), clears the Unreleased section, and prints the new version plus its changelog entries. Use when the user says "bump version", "bump the version", "/bump-version", or wants to cut a new release entry.
---

# Bump Version

Bumps Postern to the next patch version and brings `CHANGELOG.md` up to date.

`CHANGELOG.md` is embedded in `Postern.exe`: the Changelog page shows all of it, and the first start after an update shows the sections between the old version and the new one. So a version without a section is a version whose users are told nothing.

## Why the tag, not the props file

The version in `Directory.Build.props` and the `[Unreleased]` changelog section both drift: sometimes a version gets tagged without the props file or changelog catching up. The release workflow stamps the binaries from the **tag**, so the **last git tag is the source of truth**. Always derive the next version from it.

## Steps

### 1. Gather everything in one batch

Run this single block first. It collects the tag, the new commits, and the tag list for any backfill, so you don't make repeated round-trips:

```bash
LAST_TAG=$(git tag --sort=-v:refname | head -1)
echo "LAST_TAG=$LAST_TAG"
echo "=== all tags (oldest→newest) ==="; git tag --sort=v:refname
echo "=== new commits on HEAD since $LAST_TAG ==="
git log $LAST_TAG..HEAD --no-merges --date=short --pretty=format:'%h %ad %s'
```

Parse `LAST_TAG` as `vMAJOR.MINOR.PATCH`. The next version is the same with **PATCH + 1** (e.g. `v0.1.0` → `v0.1.1`). Call it `$NEXT` (the props file wants it without the `v`, e.g. `0.1.1`). If there are no tags at all, fall back to the `<Version>` in `Directory.Build.props` and bump that, and say so.

**Scope is `HEAD`, deliberately.** Only commits reachable from `HEAD` are releasing. Do **not** use `--all`: it drags in unmerged branch and worktree WIP that isn't shipping, which is both wrong and slow. If you know specific work landed on another branch that *is* part of this release, add that branch explicitly; otherwise stay on `HEAD`.

Most commit subjects are self-explanatory, so summarise straight from them. Only `git show <hash>` a commit when its subject is genuinely opaque (e.g. "tweaks", "first pass"). Skip noise entirely: stash entries (`WIP on…`, `index on…`), pure-WIP commits, Dependabot action bumps, and plumbing a user would never notice. Collapse related commits into the single *net* change they add up to (see "What to write" in step 4).

### 2. Bump the props file

Edit the `<Version>` element in `Directory.Build.props` to `$NEXT` (no `v` prefix). Leave everything else untouched, and set it to `$NEXT` regardless of its current value.

### 3. Backfill any missing tagged versions

A version can be tagged without ever getting a changelog section. From the tag list in step 1, find tags with **no** matching `## [v…]` heading in `CHANGELOG.md`. For each missing tag (oldest first), get its commits and date in one call, scoped to that tag's range:

```bash
# fill in <prev_tag> and <tag>; the last line gives the heading date
git log <prev_tag>..<tag> --no-merges --date=short --pretty=format:'%h %ad %s'; \
  git log -1 --format='HEADING_DATE=%cd' --date=short <tag>
```

Insert each backfilled `## [v<tag>] - YYYY-MM-DD` (using its own tag date, not today) in the correct reverse-chronological slot, applying the same summarising and tone rules below. Do every missing tag, not just the latest.

> Concrete example: tag `v0.1.1` exists but the changelog's newest section is `v0.1.0`, so `v0.1.1` needs backfilling from `v0.1.0..v0.1.1` before the new version goes on top.

### 4. Rewrite the changelog

In `CHANGELOG.md`:

- **Create a new section** directly under `## [Unreleased]`, titled `## [v$NEXT] - YYYY-MM-DD` using today's date.
- Fill it with the summarised, user-facing bullets from step 1. **Fold in** anything already sitting in the `[Unreleased]` section: those changes are part of this release.
- **Clear the `[Unreleased]` section** so it sits empty (keep the `## [Unreleased]` heading and the `---` separators; just remove its bullets).
- Preserve the existing file format exactly: heading style, the `---` separators, and the reverse-chronological order (newest version on top). The app finds versions by the `## [vX.Y.Z]` headings, so keep that shape.

#### What to write

- **Write for the end user, not the developer.** Describe what changed for someone *using* Postern: the window, approvals, connections, what apps can and can't do. Mention implementation details only when they genuinely matter to the user (e.g. "approvals and credentials carry over"). Internal refactors, file moves, tests, CI and plumbing don't get a bullet at all.
- **Security-relevant changes always get a bullet**, even small ones: anything that changes what a request is allowed to do, where a credential is sent, or what's logged. Say it plainly; this is the one place a joke never goes.
- **Keep bullets snappy. This is the rule that gets ignored most, so enforce it hard.** Aim for a short phrase, roughly ten words or fewer; one line, never two. Cut "now", "the ability to", "you can now". Drop the trailing "so you can…" / "so you know…" rationale: the change speaks for itself. Keep an aside only when it earns the dry humour. Prefer "Several accounts per service" over "You can now add several accounts for the same service so that you can use a work and a personal token". After drafting, reread every bullet and shorten any that runs long.
- **Describe the cumulative change, not the commit trail.** A version's section is the net difference from the previous version: the end state, not the journey. If a feature was added, then fixed, then tweaked across several commits, that's *one* bullet describing the finished feature. Never list intra-version fixes to something that didn't exist in the last release; the user never saw the broken version.

#### Tone: keep the dry humour

The changelog has a deadpan, self-aware voice, shared with Perch's. Match it. Look at existing entries for the register, e.g.:

> - New icon (a gate, unsurprisingly)
> - One-line install, and in-app updates when you click for them (never before)

Write straight bullets for ordinary changes; reserve the parenthetical asides and dry understatement for the moments that earn them. Don't force a joke into every line: the humour lands because most lines are plain. Never invent changes that didn't happen for the sake of a punchline.

### 5. Report back

Print, plainly:

- The next version number (e.g. `v0.1.1`).
- The exact changelog entries you wrote for that version.
- If you backfilled any missing tagged versions, list which ones and show their entries too.

## Notes

- This skill does **not** commit, tag, or push. It only edits the two files and reports. Tagging happens separately: commit the bump, then `/release-next-version` PRs, merges and tags it (or `.\scripts\publish.ps1 <version>` for a local build).
- Only the patch component is bumped. If the user wants a minor or major bump, they'll say so: follow their instruction instead of auto-incrementing patch.
