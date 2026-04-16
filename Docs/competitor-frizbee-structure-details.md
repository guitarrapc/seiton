# frizbee — Competitor Structure Details

> Reference: `.references/frizbee/`
> Author: Stacklok
> Purpose: Unified tool for pinning both GitHub Actions references and container image references to checksums/SHAs.

---

## 1. Summary

frizbee is a CLI tool that provides a single interface for pinning two distinct reference types:
- GitHub Actions `uses:` references (tag → commit SHA via GitHub API)
- Container image references (tag → OCI digest via registry HEAD request)

Unlike pinact (Actions-only) or dockerfile-pin (images-only), frizbee handles both in separate subcommands under one binary. It operates on workflow YAML files, docker-compose files, and arbitrary YAML files.

Core use cases:
- `frizbee actions [dir]` — pin all Actions refs in workflow files
- `frizbee image [dir]` — pin all image refs in YAML files
- `frizbee actions <owner/repo@tag>` — resolve a single reference inline

---

## 2. Architecture

```
cmd/
  actions/actions.go     CmdGHActions — Actions subcommand
  image/image.go         CmdContainerImage — Image subcommand
internal/
  cli/cli.go             Shared CLI helpers, GitHubTokenEnvKey, TokenHelpText
pkg/
  replacer/              Core replacer logic (GH actions + container images)
  interfaces/            ErrReferenceSkipped
  utils/config/
    config.go            Config struct, DefaultConfig(), MergeUserConfig()
```

---

## 3. Resolution Strategy — GitHub Actions SHA

### Library / API
- GitHub REST API (via authenticated HTTP client)
- Token injected via `os.Getenv(cli.GitHubTokenEnvKey)` = `os.Getenv("GITHUB_TOKEN")`

### Resolution Flow
```go
r := replacer.NewGitHubActionsReplacer(cfg).
    WithUserRegex(cliFlags.Regex).
    WithGitHubClientFromToken(os.Getenv(cli.GitHubTokenEnvKey))
```

1. Parse `uses: owner/repo@tag` or `uses: owner/repo/.github/workflows/file.yml@tag`.
2. Look up the tag's commit SHA via GitHub API.
3. Replace `@tag` with `@<sha>`.
4. Original tag is preserved as inline comment: `@sha # tag`.

### Skip Behavior
- References matching `ExcludeBranches` (default: `main`, `master`) are skipped.
- References matching `Exclude` patterns in config are skipped.
- References that fail resolution yield `ErrReferenceSkipped` (gracefully skipped, not fatal).

---

## 4. Resolution Strategy — Container Image Digest

### Library Used
`go-containerregistry` (same as dockerfile-pin — the ecosystem standard)

### Resolution Flow
```go
r := replacer.NewContainerImagesReplacer(cfg).
    WithUserRegex(cliFlags.Regex)
```

- Uses OCI registry `HEAD /v2/{name}/manifests/{reference}` — no full manifest download.
- No explicit token for image resolution; relies on system credential chain (`~/.docker/config.json` via `authn.DefaultKeychain`).

### Skip Behavior
- Images matching `ExcludeImages` (default: `["scratch"]`) are skipped.
- Tags matching `ExcludeTags` (default: `["latest"]`) are skipped.
- `scratch` is **always** appended to `ExcludeImages` even if user config omits it (enforced in `MergeUserConfig`).

---

## 5. Authentication

### GitHub Actions
- Single env var: `GITHUB_TOKEN`
- No tool-specific env var (unlike pinact's `PINACT_GITHUB_TOKEN`)
- No OS keyring integration
- No GHES support
- TokenHelpText: `"NOTE: It's recommended to set the GITHUB_TOKEN environment variable given that GitHub has tighter rate limits on anonymous calls."`

### Container Images
- No explicit token — uses `authn.DefaultKeychain` from `go-containerregistry`
- Reads `~/.docker/config.json` automatically
- Supports any registry where `docker login` has been run

---

## 6. Configuration File (`.frizbee.yml`)

```yaml
ghactions:
  exclude:
    - slsa-framework/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@.*
  exclude_branches:
    - main
    - master
images:
  exclude_images:
    - scratch
  exclude_tags:
    - latest
```

### Default Configuration (`DefaultConfig()`)
```go
&Config{
    GHActions: GHActions{
        Filter: Filter{
            ExcludeBranches: []string{"main", "master"},
        },
    },
    Images: Images{
        ImageFilter: ImageFilter{
            ExcludeImages: []string{"scratch"},
            ExcludeTags:   []string{"latest"},
        },
    },
}
```

### `MergeUserConfig` Safety
- `scratch` is **always** forced into `ExcludeImages` — cannot be overridden by user.
- Other defaults are not enforced — user can clear `exclude_branches`, `exclude_tags`.

---

## 7. Reference Skip Handling

```go
res, err := r.ParseString(cmd.Context(), pathOrRef)
if errors.Is(err, interfaces.ErrReferenceSkipped) {
    fmt.Fprintln(cmd.OutOrStdout(), pathOrRef)  // print as-is, no error
    return nil
}
```

Skip is not an error — it is a named sentinel (`ErrReferenceSkipped`). This enables callers to distinguish "I tried but this type of ref is not pinnable" from actual resolution failures.

---

## 8. Supported File Types

| Subcommand | Supported Fields |
|---|---|
| `actions` | `uses:` in `.github/workflows/*.yml` |
| `image` | `image:` in any YAML file (docker-compose, k8s manifests, etc.) |

frizbee `image` is broader than dockerfile-pin: it targets any YAML file with `image:` fields, not just Dockerfiles or docker-compose.yml.

---

## 9. Lessons Learned / Design Notes

- **Single `GITHUB_TOKEN` env var** — simpler than pinact's multi-var cascade, but less flexible for multi-host scenarios (no GHES). Good default for OSS projects; insufficient for enterprise with GHES.
- **`scratch` always excluded** — a safety invariant implemented in `MergeUserConfig`, not just a default. This is the right design: `scratch` has no registry, pinning it is nonsensical.
- **`latest` excluded by default** — pinning `latest` is semantically useless (it moves). frizbee's default correctly avoids it; users can remove the default if desired.
- **`exclude_branches: [main, master]`** — explicitly designed to avoid pinning reusable workflows on the caller's own default branches, where tag semantics do not apply.
- **`ErrReferenceSkipped` sentinel** — cleaner pattern than boolean flags for communicating skip vs. error in resolution pipelines.
- **Unified actions + image in one tool** — reduces tool sprawl, but also means one tool must carry both GitHub API and OCI registry dependencies. Seiton should keep them as separate resolver interfaces.
- **No in-process cache visible in public API** — unlike dockerfile-pin's explicit `CachedResolver`, frizbee's caching (if any) is internal to the replacer. For linter integration, explicit injectable cache is preferable.
