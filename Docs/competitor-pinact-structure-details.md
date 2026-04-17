# pinact — Competitor Structure Details

> Reference: `.references/pinact/`
> Author: suzuki-shunsuke
> Purpose: Pin GitHub Actions and Reusable Workflows to full commit SHA, with optional version update and annotation verify.

---

## 1. Summary

pinact is a CLI tool that rewrites GitHub workflow and composite action files so that every `uses:` reference becomes pinned to a full 40-character commit SHA. It also adds a human-readable tag comment next to the SHA (e.g. `# v4.0.0`). It can additionally **update** already-pinned references to their latest SHA and **verify** that existing annotations match the real SHA.

pinact also supports age-based update filtering via `--min-age` / `PINACT_MIN_AGE` (default `0` = disabled). This filter is applied when selecting the update target version (`-u` path), not as a post-resolution gate on a single input ref.

Core use cases:
- `pinact run` — pin/update
- `pinact check` — verify only (no writes)
- `pinact create-review` — post GitHub PR review comments for unpinned actions

---

## 2. Architecture

```
cmd/pinact/main.go
  └─ pkg/cli/           App entrypoint, flag parsing, config loading
  └─ pkg/di/            Dependency injection, env var wiring
  └─ pkg/config/        Config file (.pinact.yaml) reading and validation
  └─ pkg/github/        GitHub API clients
  │   ├─ github.go      Client factory, OAuth2 setup
  │   ├─ service.go     ClientResolver — GHES vs github.com routing
  │   ├─ registry.go    Commit SHA resolver via Repositories API
  │   └─ keyring.go     OS keyring token storage
  └─ pkg/controller/    Core pin/check/review orchestration
  └─ pkg/sarif/         SARIF output formatter
```

---

## 3. Resolution Strategy — GitHub Actions SHA

### API Used
- `GET /repos/{owner}/{repo}/git/refs/{ref}` (tags / branches)
- `GET /repos/{owner}/{repo}/releases`
- `GET /repos/{owner}/{repo}/tags`
- `GET /repos/{owner}/{repo}/commits/{sha}` — for tag cooldown (`--min-age`) checks

### Resolution Flow
1. Parse `uses: owner/repo@ref` (or `owner/repo/.github/workflows/file.yml@ref`).
2. Look up the ref via GitHub Repositories API.
3. If the ref is an annotated tag, follow the `object.sha` to the commit object.
4. Replace `@ref` with `@<commit-sha> # ref`.

### Update Target Selection with `--min-age`

`--min-age` affects only update flows (`pinact run -u ...`) where pinact chooses a newer tag/version:

1. Compute `cutoff = now - minAgeDays` when `min-age > 0`.
2. Query releases first and keep only candidates that pass cooldown:
  - release prerelease is skipped when the current version is stable,
  - `release.published_at > cutoff` is skipped.
3. If no eligible release remains, query tags and keep only candidates that pass cooldown:
  - tags already represented by releases are skipped,
  - prerelease tags are skipped when current version is stable,
  - for each tag, pinact fetches `commit.committer.date`; `date > cutoff` is skipped,
  - if commit-date lookup fails, that tag is skipped conservatively.
4. From remaining candidates, choose the highest version (semver first, string fallback).

This is a candidate-selection model (release/tag enumeration + filtering), not a single-ref post-check.

### Caching
- In-process caches exist in wrapper services:
  - `RepositoriesServiceImpl.Commits` / `Tags` / `Releases`
  - `GitServiceImpl.Commits`
- Repository host routing (GHES vs github.com) is also cached in-process via `ClientResolver.repoHosts`.

---

## 4. Authentication

### Token Priority (GitHub.com)
```
PINACT_GITHUB_TOKEN  →  GITHUB_TOKEN  →  OS Keyring  →  ghtkn App Token  →  unauthenticated
```

Source: `pkg/di/env.go`
```go
s.GitHubToken = getEnv("PINACT_GITHUB_TOKEN")
if s.GitHubToken == "" {
    s.GitHubToken = getEnv("GITHUB_TOKEN")
}
```

### GHES Token Priority
```
PINACT_GHES_TOKEN  →  GHES_TOKEN  →  GITHUB_TOKEN_ENTERPRISE  →  GITHUB_ENTERPRISE_TOKEN
```

### OS Keyring
- Enabled via `PINACT_KEYRING_ENABLED=true`
- Uses Windows Credential Manager / macOS Keychain / GNOME Keyring
- Managed by `pinact token set` / `pinact token get`

### ghtkn Integration
- Enabled via `PINACT_GHTKN=true`
- Creates a GitHub App User Access Token on demand via `ghtkn` CLI

### Unauthenticated Fallback
- If no token is available, GitHub REST API is called without authentication.
- Rate limit is lower (60 req/hour vs 5000 req/hour authenticated).

---

## 5. GitHub Enterprise Server (GHES) Support

`ClientResolver` (in `pkg/github/service.go`) routes API calls to either github.com or a GHES instance:

```go
type ClientResolver struct {
    defaultRepoService  RepositoriesService  // github.com
    ghesRepoService     RepositoriesService  // GHES
    repoHosts           map[string]repoHost  // cache
    fallback            bool                 // fallback to github.com if not on GHES
}
```

- Config: `.pinact.yaml` → `ghes.api_url` + `ghes.fallback`
- Env: `GHES_API_URL` overrides config
- If `fallback: true`, repositories not found on GHES are resolved via github.com

---

## 6. Configuration File (`.pinact.yaml`)

Schema version 3 (v2 abandoned):

```yaml
version: 3
files:
  - pattern: ".github/workflows/*.yaml"
ignore_actions:
  - name: slsa-framework/slsa-github-generator/\.github/workflows/generator_generic_slsa3\.yml
    ref: .*
  - name: peaceiris/.*
    ref: .*
ghes:
  api_url: https://ghes.example.com
  fallback: false
separator: " # "
```

- `files` — glob patterns for target files (overridden by CLI positional args)
- `ignore_actions` — name/ref as regex patterns
- `ghes` — GHES configuration
- `separator` — string between SHA and tag comment; defaults to ` # `

`min-age` is not part of `.pinact.yaml`; it is provided via CLI flag (`--min-age`) or env var (`PINACT_MIN_AGE`).

---

## 7. Verification Mode (`pinact check`)

- Reads existing `uses: owner/repo@sha # tag` annotations
- Resolves the tag via GitHub API to confirm it matches the SHA
- Reports mismatches as errors (without writing files)
- Exit code 1 on any mismatch

Error code `001`: version annotation mismatch — documented at `docs/codes/001.md`.

---

## 8. Reusable Workflow Support

pinact handles both:
- Actions: `owner/repo@ref`
- Reusable Workflows: `owner/repo/.github/workflows/file.yml@ref`

Both are resolved identically via the Repositories API; the path prefix does not change the SHA resolution mechanism.

---

## 9. Output Formats

- Diff output to stdout (default)
- SARIF for CI integration: `--format sarif`
- GitHub PR review comments: `pinact create-review`

---

## 10. Lessons Learned / Design Notes

- **Tool-specific env var** (`PINACT_GITHUB_TOKEN`) takes priority over the generic `GITHUB_TOKEN`, allowing different tokens for different tools in the same environment.
- **No image pinning** — pinact is GitHub Actions-only. Docker image digest resolution is outside its scope.
- **`--min-age` is update-target filtering** — it filters release/tag candidates before choosing a target version, then resolves that version to SHA.
- **GHES fallback** is a deliberate design choice: some organizations host their own fork of common actions on GHES, but may also use public github.com actions. The fallback flag enables this hybrid.
- **Regex-based ignore_actions** is more flexible than glob, at the cost of matching complexity.

---

## 11. pinact vs Seiton (`min_age_days`)

| Aspect | pinact (`--min-age`) | Seiton (`pin_resolution.github_actions.min_age_days`) |
|---|---|---|
| Default | `0` (disabled) | `14` (enabled) |
| Config surface | CLI/env only (`--min-age`, `PINACT_MIN_AGE`) | Config file key (`min_age_days`) |
| Activation point | Update flow only (`run -u`) | Pin remediation resolution flow |
| Selection model | Enumerate release/tag candidates, then filter by age and choose best | Enumerate release/tag candidates for version-like refs (`vN`, `vN.M`, `vN.M.P`) in the same version family, then choose best eligible; non-version refs use direct resolution |
| If candidate/target is too new | Skip that candidate and continue searching older eligible candidates | Skip too-new candidates and continue; return skip (`null`) only when no eligible candidate remains |
| `0` semantics | No cooldown filtering | No age constraint |
