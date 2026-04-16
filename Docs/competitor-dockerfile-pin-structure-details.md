# dockerfile-pin — Competitor Structure Details

> Reference: `.references/dockerfile-pin/`
> Author: azu
> Purpose: Add `@sha256:<digest>` to Docker image references in Dockerfiles, docker-compose.yml, and GitHub Actions files.

---

## 1. Summary

dockerfile-pin is a CLI tool that resolves OCI image digests and pins them directly into source files. It handles three file types: Dockerfiles (`FROM` lines), docker-compose.yml (`image:` fields), and GitHub Actions (container/service `image:` fields and `uses: docker://` references). It does **not** pin GitHub Actions `uses: owner/repo@ref` references.

Core use cases:
- `dockerfile-pin run` — dry-run by default; `--write` to apply
- `dockerfile-pin check` — verify digests are present and exist in registry
- `dockerfile-pin run --update` — refresh already-pinned digests

---

## 2. Architecture

```
cmd/
  root.go              Cobra root command, global flags
internal/
  resolver/
    resolver.go        CraneResolver + CachedResolver (go-containerregistry)
    resolver_test.go
  dockerfile/
    parse.go           Dockerfile parser (FROM line extraction)
    rewrite.go         Digest insertion into text
  compose/             docker-compose.yml parser and rewriter
  actions/             GitHub Actions YAML image field parser and rewriter
```

---

## 3. Resolution Strategy — OCI Image Digest

### Library Used
[`google/go-containerregistry`](https://github.com/google/go-containerregistry) (`crane` library pattern)

### API Used
`remote.Head(ref, ...)` — OCI Distribution `HEAD /v2/{name}/manifests/{reference}`

Returns the manifest digest as `sha256:<hex>` without downloading the full manifest body.

### Resolver Interface
```go
type DigestResolver interface {
    Resolve(ctx context.Context, imageRef string) (string, error)
    Exists(ctx context.Context, imageRef string) (bool, error)
}
```

### `CraneResolver` Implementation
```go
func (r *CraneResolver) Resolve(ctx context.Context, imageRef string) (string, error) {
    ref, _ := name.ParseReference(imageRef)
    desc, _ := remote.Head(ref,
        remote.WithAuthFromKeychain(authn.DefaultKeychain),
        remote.WithContext(reqCtx))
    return desc.Digest.String(), nil
}
```

- Per-request timeout: **30 seconds** (hardcoded constant `perRequestTimeout`)
- Authentication: `authn.DefaultKeychain` (see §4)

### `CachedResolver` Wrapping
```go
type CachedResolver struct {
    inner        DigestResolver
    resolveCache map[string]resolveEntry   // imageRef → {digest, err}
    existsCache  map[string]existsEntry    // imageRef → {exists, err}
    mu           sync.RWMutex
}
```

- In-process, in-memory cache per CLI invocation
- Separate caches for `Resolve` and `Exists` results
- Concurrency-safe via `sync.RWMutex`
- Error results are **not** cached (only successful resolves are cached)
- No TTL or size limit — cache is valid for the duration of the run

---

## 4. Authentication — OCI Registry

### Mechanism
`authn.DefaultKeychain` from `go-containerregistry`:
1. Reads `~/.docker/config.json`
2. Supports Docker credential helpers (`credHelpers`, `credsStore`)
3. Supports Docker Hub, GHCR (`ghcr.io`), GCR (`gcr.io`), ECR, ACR, and any OCI-compliant registry
4. No explicit token injection — relies entirely on the system Docker credential chain

### No token environment variables
dockerfile-pin has **no dedicated env var** for registry authentication. Users must `docker login` beforehand, or configure a credential helper.

### Private Registry Access
- Works for any registry where `docker login <registry>` has been run, or where a credential helper is configured.
- Authenticated via `~/.docker/config.json` natively; `go-containerregistry` handles the credential lookup.

---

## 5. Skip / Ignore Behavior

### Always Skipped (hardcoded)
- `FROM scratch` — no registry, skip silently
- Multi-stage references (`FROM <stage>`)
- `ARG BASE` + `FROM ${BASE}` with no default — skip with warning

### Already Pinned
- `FROM image:tag@sha256:...` — skipped unless `--update` flag is passed

### User-Configured Ignores
Config file (`.dockerfile-pin.yaml`) or CLI `--ignore-images` flag:
```yaml
ignore-images:
  - "ghcr.io/myorg/*"               # glob: ignore all images under myorg
  - "!ghcr.io/myorg/public-*"        # negation: except public-*
  - "*.dkr.ecr.*.amazonaws.com/**"   # ECR images
  - "scratch"                         # exact match
```

Pattern syntax: doublestar glob (`**` for multi-segment match). Negation patterns (`!`) override previous matches (last match wins). CLI flags are evaluated after config file patterns.

---

## 6. Error Handling

- `resolve.Exists()` returning `false` with HTTP 404 → image genuinely not found; logged, not cached
- Any other error (timeout, auth failure, network) → propagated to caller; **not cached** to prevent false-negative caching
- `check` subcommand: exit code 1 when any check fails (configurable `--exit-code`)

---

## 7. Supported File Types

| File Type | Pinned Fields |
|---|---|
| Dockerfile | `FROM image:tag` |
| docker-compose.yml | `image: image:tag` (skips images with `build:` directive) |
| GitHub Actions YAML | `container.image:`, `services.*.image:`, `uses: docker://image:tag` |

**Not supported**: `uses: owner/repo@ref` (GitHub Actions SHA) — that is pinact's domain.

---

## 8. Output Formats

- Diff to stdout (default dry-run)
- `--write` flag for in-place modification
- `--format json` for machine-readable output
- `check` subcommand produces `FAIL / OK / SKIP` table output

---

## 9. Lessons Learned / Design Notes

- **`authn.DefaultKeychain` is the cleanest OCI auth pattern** — no token plumbing required in application code; delegates entirely to Docker credential chain. Suitable for CI (where `docker login` is usually already run) and developer machines.
- **Separate `Resolve`/`Exists` cache entries** avoids a subtle bug: a cached successful `Resolve` should not suppress a later `Exists` check, and vice versa.
- **30-second per-request timeout** avoids hanging on slow registries; for batch runs this should be user-configurable.
- **HEAD request only** — far more efficient than GET manifest for digest resolution (no manifest body download).
- **No GHES/GHCR-specific logic** — OCI is OCI; authentication is handled generically by credential chain.
