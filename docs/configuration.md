# Configuration

This page describes how to configure Seiton behavior using a configuration file.

Configuration is **optional**. Running Seiton without a config file works in most cases and all defaults are safe and sensible.

---

## Config File Location

Seiton auto-discovers a config file by looking for the following names, starting from the current working directory and walking up parent directories:

1. `.github/seiton.yaml`
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

The first file found is used. If no file is found, built-in defaults apply.

To specify a config file explicitly:

```sh
seiton --config path/to/seiton.yaml
```

or via environment variable:

```sh
export SEITON_CONFIG=path/to/seiton.yaml
seiton
```

### Trust, `SEITON_CONFIG`, and CI

- **Prefer** a committed file (`.github/seiton.yaml` or discovery) so policy changes go through normal review.
- **`SEITON_CONFIG`** and **`--config`** select **any** path on disk. On **shared runners**, only set them to paths you trust (typically under the checked-out repository). Do not pass PR-provided or untrusted strings as the path.
- **Fork pull request** jobs often run with an untrusted merge ref. Avoid `SEITON_CONFIG` pointing at a path writable by that ref; rely on discovery from the base branch checkout or omit a config file to use defaults.
- **Observation**: with **`seiton check --verbose`** or **`seiton fix --verbose`**, Seiton prints **`config: …`** (absolute resolved path) or **`config: (none, using defaults)`** to stderr immediately after loading the config.

**Governance in *your* repository** (when you adopt Seiton): treat `seiton.yaml` like security policy — wide `exclusions` or disabling online rules can blunt detection. Teams often add rules under **CODEOWNERS** plus branch protection (**require review from Code Owners**) for paths such as `.github/seiton.yaml` and root `seiton.yaml`. See GitHub’s [About code owners](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners).

This is guidance for **consumer** repos; Seiton itself does not ship a CODEOWNERS file for adopters — you configure that in **your** project.

---

## Generating a Starter Config

Use `seiton init` to generate a minimal commented config at `.github/seiton.yaml`:

```sh
seiton init
```

Edit it with your preferences:

```sh
$EDITOR .github/seiton.yaml
```

---

## Config File Format

The config file is YAML. All top-level sections are optional. An empty file is valid and behaves identically to running without a config.

Unknown top-level keys and unknown rule IDs are reported as configuration errors.

### Loader resource limits

To limit denial-of-service from maliciously large configuration inputs, validation enforces:

- **Maximum UTF‑8 payload size**: `1 048 576` bytes for both `--config` / `ValidateFile` on-disk reads and `LintConfigLibrary.Validate`.
- **Maximum YAML DOM depth**: **`64`** nested mappings/sequences when building the config parse tree (`lint config YAML exceeds maximum nesting depth`).
- **Maximum DOM structural units**: **`50 000`** scalar keys/scalar leaves/compound containers while building the DOM (`lint config YAML exceeds maximum structural size`).
- **`fix.pinning.ignore-actions`** uses **wildcard matching** (`*` = any sequence, `?` = single char) — no regex, no ReDoS risk. **`fix.pinning.exclude-branches`** uses exact string equality (`string.Equals`, ordinal).

### Annotated Example

```yaml
# .github/seiton.yaml

# ─── Rule settings ───────────────────────────────────────────────────────────
rules:

  # Disable a rule globally.
  runner-no-latest:
    enabled: false

  # Override a rule's severity.
  checkout-persist-credentials:
    severity: warning

  # Enable an opt-in online audit rule.
  known-vulnerable-actions:
    enabled: true

  # Extend the built-in dangerous-trigger event set.
  dangerous-triggers:
    events:
      extend:
        - issue_comment
        - pull_request_review_comment

  # Add self-hosted runner labels that Seiton should treat as known.
  runner-label:
    known-hosted-labels:
      extend:
        - ubuntu-24.04-arm
        - windows-2025-vs2026

  # Treat additional registries as public (no credentials required).
  credentials:
    public-registries:
      extend:
        - registry.example.com
        - mirror.example.net:5000


  # Extend the trigger set that cache-poisoning considers untrusted.
  cache-poisoning:
    untrusted-triggers:
      extend:
        - issue_comment

  # Extend output commands that unredacted-secrets watches for secret printing.
  unredacted-secrets:
    output-commands:
      extend:
        - tee

  # Deny specific uses references.
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"

  # Ignore specific actions from unpinned-uses checks.
  unpinned-uses:
    ignore-actions:
      - "my-org/internal-action"
      - "my-org/setup-*"

# ─── Exclusions ──────────────────────────────────────────────────────────────
exclusions:
  # Suppress specific rules for all files matching a path glob.
  - file: ".github/workflows/legacy-*.yml"
    rules:
      - unpinned-uses

  # Suppress specific rules in a specific job within a file.
  - file: ".github/workflows/publish.yml"
    jobs:
      - publish
    rules:
      - credentials

# ─── Fix settings ─────────────────────────────────────────────────────────────
fix:
  defaults:
    job-timeout-minutes: 15    # Default value for job-timeout-minutes-required auto-fix.

  pinning:
    enable-network: false      # Set true to allow network-assisted SHA pinning.
    min-age-days: 14           # Minimum commit age before a SHA pin is considered stable.
    exclude-branches:          # Skip SHA pinning for these branch refs.
      - main
      - master
    ignore-actions:            # Skip pinning for actions matching these wildcard patterns.
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: false      # Set true to allow network-assisted digest pinning.
    exclude-images:            # Skip pinning for these image names.
      - scratch
    exclude-tags:              # Skip pinning for images with these tags.
      - latest
    ignore-images:             # Skip pinning for images matching these glob patterns.
      - "mcr.microsoft.com/**"

# ─── Network settings ─────────────────────────────────────────────────────────
network:
  on-error: skip               # skip | fail. How to handle network errors from online rules.
  timeout-seconds: 30          # Per-request timeout for GitHub API calls.
  max-concurrency: 4           # Optional. Omitted default: min(4, logical CPUs). Max: logical CPU count.
  github:
    ghes-api-url: ""           # GHES REST API URL (empty = github.com). Must be https; userinfo prohibited.
    ghes-fallback: false       # Fall back to github.com if GHES request fails.

# ─── Output settings ──────────────────────────────────────────────────────────
output:
  sort-order: location         # location (default) | rule. Controls diagnostic output ordering.
```

---

## Rules

### Enabling and Disabling Rules

All default-on rules can be disabled. Set `enabled: false` to turn a rule off:

```yaml
rules:
  runner-no-latest:
    enabled: false
```

Opt-in rules (default off) require `enabled: true`:

```yaml
rules:
  known-vulnerable-actions:
    enabled: true
```

> **Note:** Some rules cannot be disabled. Attempting to disable `deny-write-all` is a configuration error. See the [Checks](checks.md) page for each rule's constraints.

### Overriding Severity

Override a rule's severity level with `severity`. Valid values: `error`, `warning`, `info`.

```yaml
rules:
  checkout-persist-credentials:
    severity: warning
```

> **Note:** Some rules enforce a minimum severity. Attempting to lower severity below the minimum is a configuration error.

### Rule-Specific Options

Some rules accept additional configuration keys. All `extend` lists add to the built-in set — they never replace it.

| Rule | Key | Description |
|---|---|---|
| `dangerous-triggers` | `events.extend` | Additional trigger event names to treat as dangerous. |
| `runner-label` | `known-hosted-labels.extend` | Additional GitHub-hosted runner labels to treat as known. |
| `credentials` | `public-registries.extend` | Additional container registries to treat as public. |
| `cache-poisoning` | `untrusted-triggers.extend` | Additional trigger events to treat as untrusted. |
| `unredacted-secrets` | `output-commands.extend` | Additional shell commands to watch for secret printing. |
| `forbidden-uses` | `deny` / `allow` | Glob patterns for denying or allowing `uses:` references. |
| `unpinned-uses` | `ignore-actions` | Glob patterns for actions to exclude from SHA-pinning checks. |
| `overprovisioned-secrets` | `max-step-env-secrets` / `max-job-secrets` | Integer thresholds for secret over-provisioning detection. |

---

## Exclusions

Exclusions suppress **rule diagnostics** for specific files, jobs, or rule combinations. Parser errors and configuration errors are never suppressed by exclusions. Fields are additive (progressive narrowing):

- **`file` only** → suppress all rule diagnostics for the entire file
- **`file` + `jobs`** → suppress all rule diagnostics for specified jobs only
- **`file` + `rules`** → suppress specified rule diagnostics for the whole file
- **`file` + `jobs` + `rules`** → suppress specified rule diagnostics for specified jobs

### File-Level Exclusion (all rules)

Suppress all rule diagnostics for a file:

```yaml
exclusions:
  - file: ".github/workflows/generated.yml"
```

### File-Level Exclusion (specific rules)

Suppress one or more rules for all files matching a glob pattern:

```yaml
exclusions:
  - file: ".github/workflows/legacy-*.yml"
    rules:
      - unpinned-uses
      - runner-no-latest
```

Path separator is always `/` and matching is case-sensitive. The glob base is the repository root.

### Job-Level Exclusion

Suppress rules for a specific job ID within a file:

```yaml
exclusions:
  - file: ".github/workflows/publish.yml"
    jobs:
      - publish
    rules:
      - credentials
```

Job matching uses `job.id` only (not `job.name`).

---

## Inline Suppression Directives

For one-off suppressions inside a workflow file, use inline comment directives.

### Suppress the next line

```yaml
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v4
```

### Suppress within a job

```yaml
# seiton: disable-job build unpinned-uses,job-permissions-required
jobs:
  build:
    ...
```

### Suppress for the entire file

Place at the top of the workflow file:

```yaml
# seiton: disable-file dangerous-triggers
```

Multiple rule IDs are comma-separated. Inline directives take precedence over config-file exclusions.

---

## Fix Configuration

`seiton fix` (or `seiton --fix`) applies auto-fixes. The `fix` section controls behavior for network-assisted fixes.

### Auto-fix for `job-timeout-minutes-required`

Set a default timeout value to use when auto-fixing jobs that lack `timeout-minutes`:

```yaml
fix:
  defaults:
    job-timeout-minutes: 15
```

If this is `null` or omitted, `job-timeout-minutes-required` does not apply an auto-fix.

### Network-Assisted SHA Pinning

Auto-pin `uses:` references to commit SHAs by enabling the network:

```yaml
fix:
  pinning:
    enable-network: true
```

Or use the CLI flag for a one-off run:

```sh
seiton fix --enable-pin-network
```

Configure pinning behavior:

```yaml
fix:
  pinning:
    min-age-days: 14           # Only accept commits at least 14 days old.
    exclude-branches:          # Do not replace branch-style refs.
      - main
      - master
    ignore-actions:            # Skip pinning for actions matching these wildcard patterns.
      - uses: "slsa-framework/*"
        ref: "*"
```

### Network-Assisted Image Digest Pinning

Auto-pin container images to `@sha256:<digest>`:

```yaml
fix:
  images:
    enable-network: true
```

Or use the CLI flag:

```sh
seiton fix --enable-image-network
```

Configure image pinning behavior:

```yaml
fix:
  images:
    exclude-images:
      - scratch
    exclude-tags:
      - latest
    ignore-images:
      - "mcr.microsoft.com/**"
```

---

## Network Configuration

Used by online audit rules and network-assisted fix operations.

```yaml
network:
  on-error: skip           # skip | fail
  timeout-seconds: 30
  max-concurrency: 4       # optional; omit uses min(4, logical CPUs)
  github:
    ghes-api-url: ""       # Leave empty for github.com
    ghes-fallback: false
```

| Key | Default | Description |
|---|---|---|
| `on-error` | `skip` | `skip` silently ignores network failures. `fail` treats them as errors. |
| `timeout-seconds` | `30` | Per-request GitHub REST timeout (**`0`**–**`300`** seconds; larger values emit an error diagnostic and clamp to **`300`**). |
| `max-concurrency` | `min(4, ProcessorCount)` | Concurrent GitHub requests. When omitted, effective default is **`min(4, N)`**, where **N** = `Environment.ProcessorCount`, minimum **`1`** (never exceeds **N**). When set explicitly: **`1`**–**N**; larger values emit an error diagnostic and clamp to **N**. |
| `github.ghes-api-url` | `""` | GitHub Enterprise Server API base URL. Empty = github.com only. Must be an absolute **`https`** URL (non-HTTPS schemes and embedded user credentials are rejected during config validation). |
| `github.ghes-fallback` | `false` | Fall back to github.com if GHES request fails. |

Outbound GitHub/GitHub Enterprise HTTP clients used by network-assisted pinning and GitHub-hosted online rules **`AllowAutoRedirect` is disabled** at the socket layer and follow **same-origin redirects only**. If the API returns `3xx` to a different scheme/host/port than the preceding request URL, Seiton surfaces the redirect response and does **not** issue a second request carrying the Bearer token — this limits token replay to other origins after hostile redirects.

### GitHub API Token

Seiton resolves a GitHub token in this order:

1. `SEITON_GITHUB_TOKEN` environment variable
2. `GITHUB_TOKEN` environment variable

This order is hardcoded and cannot be changed via config file.

---

## Output Configuration

Controls diagnostic output behavior.

```yaml
output:
  sort-order: location         # location | rule
```

| Key | Default | Description |
|---|---|---|
| `sort-order` | `location` | `location` sorts diagnostics by source position (line/column). `rule` groups diagnostics by rule priority. |

### Sort Order

By default, diagnostics are sorted by source location (line, then column), with rule ID as a tiebreaker. This matches the natural reading order of the file.

Set `sort-order: rule` to group diagnostics by rule instead:

```yaml
output:
  sort-order: rule
```

This is useful when batch-fixing all instances of a single rule at a time.

---

## Defaults Reference

| Config Key | Default |
|---|---|
| `rules.<rule-id>.enabled` | `true` for local rules; `false` for online rules |
| `rules.<rule-id>.severity` | Rule-defined default |
| `exclusions` | (empty) |
| `fix.defaults.job-timeout-minutes` | `null` (auto-fix disabled) |
| `fix.pinning.enable-network` | `false` |
| `fix.pinning.min-age-days` | `14` |
| `fix.pinning.exclude-branches` | `main`, `master` |
| `fix.images.enable-network` | `false` |
| `fix.images.exclude-images` | `scratch` |
| `fix.images.exclude-tags` | `latest` |
| `network.on-error` | `skip` |
| `network.timeout-seconds` | `30` (`0`–`300` enforced; excess rejected + clamped) |
| `network.max-concurrency` | `min(4, logical processors)` | Same rules as **`max-concurrency`** above (**`1`**–logical processor count for explicit values; excess rejected + clamped). |
| `network.github.ghes-api-url` | `""` |
| `network.github.ghes-fallback` | `false` |
| `output.sort-order` | `location` |
