# Contributing

## Branch model: GitHub Flow

- **`master`** is the only long-lived branch. It must always build, pass tests and be releasable.
- Work happens on short-lived branches cut from `master`:
  - `feature/<short-name>` for features and enhancements
  - `fix/<short-name>` for bug fixes
  - `docs/<short-name>` or `chore/<short-name>` for docs, CI and tooling
- Every change reaches `master` through a pull request. Merged branches are deleted automatically.
- **A release is a tag**, not a branch: `vX.Y.Z` on a `master` commit. The released code is whatever
  that tag points to, and the image tags on GHCR (`X.Y.Z`, `X.Y`, `X`, `latest`) say what is deployable.

### Why not `master` + `develop`?

A `develop` integration branch helps when several people integrate in parallel or releases are
batched and staged. For a solo project it mostly adds ceremony:

- an extra `develop` → `master` merge for every release;
- two long-lived branches to keep in sync, with hotfixes merged back into `develop`;
- PRs that must target `develop`, which is easy to get wrong.

Tags already mark what was released, and required CI keeps `master` releasable, so GitHub Flow gives
the same guarantees with less overhead. Revisit this if more contributors join.

## Pull requests

- Reference the issue in the description, e.g. `Fixes #20`, and set the issue's milestone and labels
  (`feature` / `enhancement` / `bug`, plus an `area:*` label).
- The CI check (build + full test suite) must pass before merging; `master` is protected.
- Prefer merge commits (the history keeps each PR's commits).
- **Stacked PRs:** when B depends on A, open B with A's branch as its base. When A merges, its branch
  is deleted and GitHub retargets B to `master` automatically.
- This repository is public: keep deployment specifics (hosts, IPs, device serials, Home Assistant
  entity names) out of issues, PRs and commits.

## Project board

Issues live on the GitHub Project board with statuses:

`New` → `Ready` → `In progress` → `Released – to test` → `Done`

- Move an issue to **In progress** when you start it.
- When its PR merges, set it to **Released – to test**, not `Done`. Closing the issue may move it to
  `Done` automatically; move it back.
- **Done** means verified on a real deck after deployment.

## Releasing

1. Make sure `master` is green and contains everything for the milestone.
2. Create the release from `master`:
   ```bash
   gh release create vX.Y.Z --target master --generate-notes
   ```
3. The `Release` workflow runs the test suite, then builds the multi-arch image and pushes it to
   `ghcr.io/georgeslegros/stream-deck-pilot`. A failing test stops the image from being published.

Versioning follows semver: `fix` → patch, new features or enhancements → minor.

## Local checks

```bash
dotnet build
dotnet test                                    # full suite; integration tests need Docker
dotnet test --filter "Category!=Integration"   # fast unit tests only

# coverage report, same as CI (HTML in coverage/index.html)
dotnet test --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
dotnet tool restore
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:Html
```
