# seiton Fix Mode Reference

Auto-fix workflows, flags, and behavior for rules that support automatic remediation.

## Fix before exclusions

When many diagnostics appear (especially on first adoption), **always preview fixes before adding `exclusions`**:

```bash
seiton --fix --dry-run    # review Would Fix / Remaining tables and diffs
seiton --fix              # apply when satisfied
seiton                    # confirm remaining issues
```

Use `exclusions` only for issues that are intentional, unfixable, or outside scope (demo workflows, generated files). See `references/adoption-workflow.md` for the full rollout order.

## Commands

```bash
# Preview fixes as unified diff (no file changes)
seiton --fix --dry-run

# Apply fixes and show unified diff
seiton --fix --show-diff

# Apply fixes in place
seiton --fix

# Exit non-zero if fixable issues exist (CI gate)
seiton --fix --check

# Pin actions to SHA via network lookup
seiton --fix --enable-pin-network

# Pin container images to digest via network lookup
seiton --fix --enable-image-network

# Both network-assisted pins
seiton --fix --enable-pin-network --enable-image-network
```

## Flags

| Flag | Description |
|------|-------------|
| `--fix` | Enable fix mode (required for all fix operations) |
| `--dry-run` | Show unified diff without writing files |
| `--show-diff` | Apply fixes and show unified diff (ignored when `--dry-run` or `--check` is active) |
| `--check` | Exit code 1 if any fixable issues exist (no file writes) |
| `--enable-pin-network` | Resolve action commit SHAs via GitHub API |
| `--enable-image-network` | Resolve container image digests via registry API |
| `--verbose` | Show timing and progress during fix |

## Rules with Auto-Fix

The following rules support `--fix`:

- `popular-action-inputs` — adds missing required inputs
- `unpinned-uses` — pins action refs to SHA (requires `--enable-pin-network`)
- `unpinned-image` — pins images to digest (requires `--enable-image-network`)
- `job-permissions-required` — adds minimal permissions block
- `id-naming` — renames IDs to kebab-case
- `deny-write-all` — replaces `write-all` with specific permissions
- `deny-read-all` — replaces `read-all` with specific permissions
- `template-injection` — moves expressions to env variables
- `run-env-context-direct-use` — moves `env.*` to step env
- `run-secrets-context-direct-use` — moves `secrets.*` to step env
- `run-inputs-context-direct-use` — moves `inputs.*` to step env
- `runner-no-latest` — replaces `-latest` with pinned version
- `checkout-persist-credentials` — adds `persist-credentials: false`
- `checkout-unsafe-pr` — replaces `allow-unsafe-pr-checkout: true` with `false`
- `job-timeout-minutes-required` — adds `timeout-minutes`
- `if-expr-wrapper` — removes redundant `${{ }}` wrapper in `if:`
- `unsound-condition` — fixes always-true/false conditions

## Context direct-use fixes (`run-*-context-direct-use`)

These three rules flag `${{ env.* }}`, `${{ secrets.* }}`, and `${{ inputs.* }}` inside `run:` shell scripts. `--fix` rewrites the script to use shell variables and adds a step `env:` mapping when one is missing.

**bash (default shell)** — before:

```yaml
steps:
  - run: echo "${{ env.BRANCH_NAME }}"
```

After `--fix`:

```yaml
steps:
  - run: echo "${BRANCH_NAME}"
    env:
      BRANCH_NAME: ${{ env.BRANCH_NAME }}
```

**PowerShell (`shell: pwsh`)** — before:

```yaml
steps:
  - shell: pwsh
    run: Write-Host "${{ secrets.MY_TOKEN }}"
```

After `--fix`:

```yaml
steps:
  - shell: pwsh
    run: Write-Host "$env:MY_TOKEN"
    env:
      MY_TOKEN: ${{ secrets.MY_TOKEN }}
```

Tips:

- Run `seiton --fix --dry-run` across the repo; context rules often dominate fixable counts.
- Sync README or doc snippets if they quote workflow YAML you changed.
- Compound expressions in `run:` may get a `help:` hint instead of auto-fix — move the expression to `env:` manually.

## Fix Configuration

In `.github/seiton.yaml`:

```yaml
fix:
  defaults:
    job-timeout-minutes: 15        # Value used by job-timeout-minutes-required fix

  pinning:
    enable-network: false          # Default; use --enable-pin-network to override
    min-age-days: 14               # Minimum commit age for SHA stability
    exclude-branches:              # Skip pinning for these refs
      - main
      - master
    ignore-actions:                # Skip pinning for matched patterns
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: false          # Default; use --enable-image-network to override
    exclude-images:                # Skip pinning these images
      - scratch
    exclude-tags:                  # Skip images with these tags (default: latest)
      - latest                    # Tagless refs (e.g. image: redis) count as latest
    ignore-images:                 # Skip glob-matched images
      - "mcr.microsoft.com/**"
```

When `--enable-image-network` is on but an image is still not pinned, check the diagnostic `help:` line. Skips due to `exclude-tags` / `exclude-images` are explained there.

## Workflow Example

Typical CI integration:

```yaml
# In CI: fail if fixable issues exist
- run: seiton --fix --check

# Local development: preview then apply
# Step 1: See what would change
- run: seiton --fix --dry-run
# Step 2: Apply
- run: seiton --fix
```

## Exit Codes in Fix Mode

| Mode | Exit 0 | Exit 1 | Exit 2 | Exit 3 |
|------|--------|--------|--------|--------|
| `--fix` | All fixable issues resolved | Unfixable issues remain | Invalid options | Fatal error |
| `--fix --dry-run` | No fixable issues | Fixable issues shown | Invalid options | Fatal error |
| `--fix --check` | No fixable issues | Fixable issues exist | Invalid options | Fatal error |

## Network Requirements

- `--enable-pin-network` requires `GITHUB_TOKEN` or `SEITON_GITHUB_TOKEN` for private repos
- `--enable-image-network` connects to container registries (Docker Hub, GHCR, etc.)
- Network errors handled per `network.on-error` config (`skip` or `fail`)
- Timeout configurable via `network.timeout-seconds` (default: 30s)
