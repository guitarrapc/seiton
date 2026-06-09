# Configuration

This page describes how to configure Seiton behavior using a configuration file.

Configuration is **optional**. Running Seiton without a config file works in most cases and all defaults are safe and sensible.

---

## Config File Location

**Precedence** (highest wins):

1. `--config <path>` flag
2. `SEITON_CONFIG` environment variable
3. Auto-discovery (below)

Seiton auto-discovers a config file by looking for the following names **under the current working directory only** (`<cwd>/`):

1. `.github/seiton.yaml`
2. `.github/seiton.yml`
3. `seiton.yaml`
4. `seiton.yml`

The first name that exists is used. If no file is found, built-in defaults apply.

To specify a config file explicitly:

```sh
seiton --config path/to/seiton.yaml
# short form
seiton -c path/to/seiton.yaml
```

or via environment variable:

```sh
export SEITON_CONFIG=path/to/seiton.yaml
seiton
```

### Nested repositories and monorepos

Both **config discovery** and **input discovery** (workflow/action files when no paths are passed) are **CWD-scoped only**.

In a nested clone or CI job with multiple checkouts, set `working-directory` to the repository you intend to lint, or pass explicit paths (`--config`, `-c`, `SEITON_CONFIG`, or file arguments).

**Recommended workflow for a nested repo:**

```sh
cd .references/actions

# 1. Create a config in the nested repo
seiton init --output .github/seiton.yaml

# 2. Validate it
seiton validate-config

# 3. Lint (config is discovered under cwd)
seiton --verbose
```

When the nested repo has its own `.github/seiton.yaml`, discovery picks it up automatically. To use a config outside `cwd`, pass `-c` or `SEITON_CONFIG`:

```text
verbose: config: /nested/.github/seiton.yaml (discovered under cwd /nested)
verbose: config: /other/seiton.yaml (from --config)
verbose: config: (none, using defaults) (searched under cwd /nested)
```

### Config setup workflow

For a new repository, use this three-step flow:

1. **`seiton init`** — create `.github/seiton.yaml` with commented defaults.
2. **`seiton validate-config --verbose`** — confirm YAML/rule IDs and inspect parse summary (config path, parse time, enabled rules, exclusions).
3. **`seiton --verbose`** — run lint once and confirm the resolved config path on stderr.

See [Common configuration recipes](#common-configuration-recipes) below for patterns that reduce noise in large action monorepos.

### Trust, `SEITON_CONFIG`, and CI

- **Prefer** a committed file (`.github/seiton.yaml` or discovery) so policy changes go through normal review.
- **`SEITON_CONFIG`** and **`--config`** select **any** path on disk. On **shared runners**, only set them to paths you trust (typically under the checked-out repository). Do not pass PR-provided or untrusted strings as the path.
- **Fork pull request** jobs often run with an untrusted merge ref. Avoid `SEITON_CONFIG` pointing at a path writable by that ref; rely on discovery from the base branch checkout or omit a config file to use defaults.
- **Observation**: with **`seiton check --verbose`** or **`seiton --fix --verbose`**, Seiton prints the resolved config path and how it was chosen to stderr immediately after loading the config. Discovery reports the cwd searched; explicit `--config`, `-c` / `SEITON_CONFIG` paths include their source.

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

Then validate and confirm discovery:

```sh
seiton validate-config --verbose
seiton --verbose
```

If your repository also contains composite actions under `.github/actions/`, lint them explicitly:

```sh
seiton --include-actions --verbose
```

---

## Common configuration recipes

These patterns reduce noise in large action/workflow monorepos while keeping security signal on production paths. They mirror adjustments that proved useful when linting [Cysharp/Actions](https://github.com/Cysharp/Actions)-scale repositories.

### Ignore self-hosted org actions (`unpinned-uses`)

When a repository heavily reuses its own actions at `@main`, pin enforcement can dominate the report. Ignore your org's actions while keeping checks on third-party references:

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "my-org/*"
```

Optional `refs` narrows the ignore to specific refs (for example only `main`).

### Exclude internal test workflows (`_test-*`)

Test harness workflows often trigger rules (`bot-conditions`, `deny-inherit-secrets`, etc.) that are not relevant to production review. Exclude them by glob:

```yaml
exclusions:
  - file: .github/workflows/_test-*.yaml
```

Use a consistent naming convention so exclusions stay predictable.

### Wrapper action: `checkout-persist-credentials`

A composite checkout wrapper may pass `inputs.persist-credentials` through to `actions/checkout`. Requiring `false` on the wrapper is often impractical. Suppress the rule for that file only:

```yaml
exclusions:
  - file: .github/actions/checkout/action.yaml
    rules:
      - checkout-persist-credentials
```

### Combined example

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "my-org/*"
exclusions:
  - file: .github/workflows/_test-*.yaml
  - file: .github/actions/checkout/action.yaml
    rules:
      - checkout-persist-credentials
```

After editing, run `seiton validate-config` then `seiton --verbose` to confirm the file is loaded.

---

## Config File Format

The config file is YAML. All top-level sections are optional. An empty file is valid and behaves identically to running without a config.

Unknown top-level keys and unknown rule IDs are reported as configuration errors.

### Error Reporting

Seiton validates configuration before linting begins. Invalid configuration causes a non-zero exit code and diagnostics on stderr:

| Error condition | Example message |
|---|---|
| Unknown top-level key | `unknown top-level key '<key>'` or `unknown top-level key '<key>'. Did you mean '<suggested-key>'?` |
| Unknown rule ID | `unknown rule-id '<rule-id>'. Did you mean '<suggested-rule-id>'?` |
| Invalid severity value | `severity must be one of info, warning, error` |
| Invalid rule-specific key | `unknown rule option '<key>'` |
| YAML syntax error | `invalid lint config YAML: <parser message>` |

Use `seiton check --verbose` to confirm which config file was loaded:

```text
verbose: config: /repo/.github/seiton.yaml (discovered under cwd /repo)
verbose: config: /repo/.github/seiton.yaml (from --config)
verbose: config: (none, using defaults) (searched under cwd /repo)
```

### Loader resource limits

Config files are limited to **1 MB**. Deeply nested or excessively large YAML is rejected with a diagnostic. See [Appendix: Loader Limits](#appendix-loader-limits) for exact thresholds.

### Annotated Example

```yaml
# .github/seiton.yaml

# ─── Rule settings ───────────────────────────────────────────────────────────
rules:

  # Disable a rule globally.
  runner-no-latest:
    enabled: false

  # Or configure fix-mapping for auto-fix support.
  # runner-no-latest:
  #   fix-mapping:
  #     ubuntu-latest: "ubuntu-24.04"
  #     windows-latest: "windows-2025"
  #     macos-latest: "macos-15"

  # Override a rule's severity.
  checkout-persist-credentials:
    severity: warning

  # Enable an opt-in online audit rule.
  known-vulnerable-actions:
    enabled: true

  # Extend the built-in dangerous-trigger event set.
  dangerous-triggers:
    events:
      - issue_comment
      - pull_request_review_comment

  # Add self-hosted runner labels that Seiton should treat as known.
  runner-label:
    known-hosted-labels:
      - ubuntu-24.04-arm
      - windows-2025-vs2026

  # Treat additional registries as public (no credentials required).
  credentials:
    public-registries:
      - registry.example.com
      - mirror.example.net:5000

  # Extend the trigger set that cache-poisoning-trigger considers untrusted.
  cache-poisoning-trigger:
    untrusted-triggers:
      - issue_comment

  # Extend the trigger set that self-hosted-runner-trigger considers untrusted.
  self-hosted-runner-trigger:
    untrusted-triggers:
      - issue_comment

  # Extend output commands that unredacted-secrets watches for secret printing.
  unredacted-secrets:
    output-commands:
      - tee

  # Assume additional events when evaluating event-scoped expressions.
  expr-undefined-var:
    assume-events:
      - workflow_dispatch
      - workflow_call

  # Deny specific uses references.
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"

  # Ignore specific actions from unpinned-uses checks.
  unpinned-uses:
    ignore-actions:
      - owner: "my-org/internal-action"
      - owner: "my-org/setup-*"
      # Ref-conditional: ignore only specific refs (e.g. trust @main from your org)
      - owner: "my-org/*"
        refs: [main, master]

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
  concurrency-limits:
    enabled: true
```

### Overriding Severity

Override a rule's severity level with `severity`. Valid values: `error`, `warning`, `info`.

```yaml
rules:
  checkout-persist-credentials:
    severity: warning
```

### Rule-Specific Options

Some rules accept additional configuration keys. All additive list keys append to the built-in set, they never replace it. See the [Annotated Example](#annotated-example) for complete YAML examples of each rule's options.

| Rule | Key | Description |
|---|---|---|
| `dangerous-triggers` | `events` | Additional trigger event names to treat as dangerous (appended to built-in set). |
| `runner-label` | `known-hosted-labels` | Additional GitHub-hosted runner labels to treat as known (appended to built-in set). |
| `runner-no-latest` | `fix-mapping` | Map of label → replacement pairs for auto-fix and custom detection. |
| `credentials` | `public-registries` | Additional container registries to treat as public (appended to built-in set). |
| `cache-poisoning-trigger` | `untrusted-triggers` | Additional trigger events to treat as untrusted (appended to built-in set). |
| `self-hosted-runner-trigger` | `untrusted-triggers` | Additional trigger events to treat as untrusted for self-hosted runner checks (appended to built-in set). |
| `unredacted-secrets` | `output-commands` | Additional shell commands to watch for secret printing (appended to built-in set). |
| `expr-undefined-var` | `assume-events` | Additional event names to assume when evaluating event-scoped expressions. |
| `forbidden-uses` | `deny` / `allow` | Glob patterns for denying or allowing `uses:` references. |
| `unpinned-uses` | `ignore-actions` | Object entries for actions to exclude from SHA-pinning checks. `owner` is required; optional `refs` narrows the ignore to exact refs. |
| `overprovisioned-secrets` | `max-step-env-secrets` / `max-job-secrets` | Integer thresholds for secret over-provisioning detection. |
| `bot-conditions` | `strict-detection` | When `true`, report exclusion checks (`!=`) on PR-only workflows at info severity. Defaults to `false`. |
| `run-env-context-direct-use` | `strict` | When `true`, diagnose shell single-quoted no-expand contexts (default suppresses them). Defaults to `false`. |
| `run-inputs-context-direct-use` | `strict` | When `true`, diagnose shell single-quoted no-expand contexts (default suppresses them). Defaults to `false`. |

<a id="bot-conditionsstrict-detection"></a>
#### `bot-conditions.strict-detection`

By default, `bot-conditions` warns only on equality checks (`==`) that grant privileges to a spoofable bot identity. Common exclusion patterns such as `github.actor != 'dependabot[bot]'` are not reported unless you opt in:

```yaml
rules:
  bot-conditions:
    strict-detection: true
```

When enabled, inequality checks on PR-only workflows are reported at info severity. Mixed or non-PR triggers remain suppressed. Dual exclusion filters (`github.actor != '…[bot]' && github.event.pull_request.user.login != '…[bot]'`) are not reported.

For the full outcome matrix (`strict-detection` × operator × triggers × mitigation), see [rules.md#bot-conditions-decision-matrix](rules.md#bot-conditions-decision-matrix).

<a id="run-env-context-direct-usestrict"></a>
#### `run-env-context-direct-use.strict`

By default, `run-env-context-direct-use` suppresses diagnostics in shell no-expand contexts to avoid non-actionable findings.

```yaml
rules:
  run-env-context-direct-use:
    strict: true
```

When enabled, shell single-quoted contexts are diagnosed. no-expand heredoc (`<<'EOF'`) remains suppressed.

<a id="run-inputs-context-direct-usestrict"></a>
#### `run-inputs-context-direct-use.strict`

By default, `run-inputs-context-direct-use` suppresses diagnostics in shell no-expand contexts to reduce noise for intentional remote-shell patterns.

```yaml
rules:
  run-inputs-context-direct-use:
    strict: true
```

When enabled, shell single-quoted contexts are diagnosed. no-expand heredoc (`<<'EOF'`) remains suppressed.

<a id="runner-labelknown-hosted-labels"></a>
#### `runner-label.known-hosted-labels`

Additional GitHub-hosted runner labels treated as known by `runner-label` (additive, case-insensitive normalization).

```yaml
rules:
  runner-label:
    known-hosted-labels:
      - my-org-runner-v2
```

<a id="runner-no-latestfix-mapping"></a>
#### `runner-no-latest.fix-mapping`

Label replacement map (`source -> pinned`) used by `runner-no-latest` detection and auto-fix.

```yaml
rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
      my-org-runner-latest: "my-org-runner-v2"
```

<a id="dangerous-triggersevents"></a>
#### `dangerous-triggers.events`

Additional trigger event names treated as dangerous by `dangerous-triggers` (additive set).

```yaml
rules:
  dangerous-triggers:
    events:
      - issue_comment
```

<a id="expr-undefined-varassume-events"></a>
#### `expr-undefined-var.assume-events`

Use this when expression validation needs to assume additional trigger events for context resolution (especially `inputs.*` availability).

```yaml
rules:
  expr-undefined-var:
    assume-events:
      - workflow_dispatch
      - workflow_call
```

Behavior:

- Entries are additive and case-insensitive.
- `workflow_dispatch` / `workflow_call` assumptions allow `inputs.*` to be treated as runtime-available even when the current workflow trigger set is mixed or cannot be narrowed statically.
- This is intended to reduce false positives in `expr-undefined-var` for event-dependent expressions.

<a id="cache-poisoning-triggeruntrusted-triggers"></a>
#### `cache-poisoning-trigger.untrusted-triggers`

Additional trigger event names treated as untrusted by `cache-poisoning-trigger` (additive set).

```yaml
rules:
  cache-poisoning-trigger:
    untrusted-triggers:
      - issue_comment
```

<a id="self-hosted-runner-triggeruntrusted-triggers"></a>
#### `self-hosted-runner-trigger.untrusted-triggers`

Additional trigger event names treated as untrusted by `self-hosted-runner-trigger` (additive set).

```yaml
rules:
  self-hosted-runner-trigger:
    untrusted-triggers:
      - issue_comment
```

<a id="credentialspublic-registries"></a>
#### `credentials.public-registries`

Additional registry hosts treated as public/credential-optional by `credentials` (additive set).

```yaml
rules:
  credentials:
    public-registries:
      - registry.example.com
      - mirror.example.net:5000
```

<a id="unredacted-secretsoutput-commands"></a>
#### `unredacted-secrets.output-commands`

Additional shell commands watched as output sinks by `unredacted-secrets` (additive set).

```yaml
rules:
  unredacted-secrets:
    output-commands:
      - tee
```

<a id="unpinned-usesignore-actions"></a>
#### `unpinned-uses.ignore-actions`

Action ignore entries for `unpinned-uses` (`owner` glob required, optional `refs` exact-match list).

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "my-org/internal-action"
      - owner: "my-org/setup-*"
      - owner: "my-org/*"
        refs: [main, master]
```

<a id="forbidden-usesdeny--allow"></a>
#### `forbidden-uses.deny` / `forbidden-uses.allow`

Wildcard policy patterns controlling denied and explicitly allowed `uses:` references for `forbidden-uses`.

```yaml
rules:
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"
```

---

## Exclusions

Exclusions suppress diagnostics for specific files, jobs, or rule combinations. Behavior depends on scope:

- **`file` only** → suppress all workflow diagnostics for the entire file
- **`file` + `jobs`** → suppress all rule diagnostics for specified jobs only
- **`file` + `rules`** → suppress specified rule diagnostics for the whole file
- **`file` + `jobs` + `rules`** → suppress specified rule diagnostics for specified jobs

> **Note:** When only `file` is specified (no `rules` or `jobs`), parse errors are also suppressed. When `rules` or `jobs` are present, parse errors are still reported. Configuration diagnostics (e.g. unknown rule IDs, invalid exclusion patterns) are never suppressed, even for fully excluded files.

### File-Level Exclusion (all rules)

Suppress all workflow diagnostics for a file:

```yaml
exclusions:
  - file: ".github/workflows/generated.yml"
```

Equivalent explicit form (same behavior):

```yaml
exclusions:
  - file: ".github/workflows/generated.yml"
    rules:
      - "*"
```

This is the broadest exclusion form. When `rules` is omitted or `rules: ["*"]`, and `jobs` is omitted, Seiton short-circuits linting for matching files and suppresses parser diagnostics too. Prefer omitting `rules` for readability; `"*"` is supported for clarity and tooling compatibility. Configuration diagnostics produced while loading the config still appear.

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

## Discovery

Controls how Seiton finds workflow files when no paths are passed on the command line.

```yaml
discovery:
  skip-agentic-workflows: true
```

| Key | Type | Default | Description |
|---|---|---|---|
| `skip-agentic-workflows` | `bool` | `false` | When `true`, skip workflow files whose **first 10 lines** contain `# gh-aw-metadata:` (GitHub Agentic Workflow marker). Also available as CLI `--skip-agentic-workflows`. |

### Agentic Workflow (gh-aw) files

gh-aw can emit more than one workflow shape. Seiton uses **two mechanisms** — do not confuse them:

| Mechanism | What it matches | Example |
|---|---|---|
| `discovery.skip-agentic-workflows: true` | `# gh-aw-metadata:` in the first 10 lines only (not file name, not `DO NOT EDIT`) | `monthly-oss-repo-status.lock.yml` |
| `exclusions` with `file` only (no `rules`) | Explicit path/glob you list | `agentics-maintenance.yml` |

Many gh-aw lock files include the metadata comment and are skipped automatically when the flag is enabled. Other generated files (for example `agentics-maintenance.yml` with only a `DO NOT EDIT` header and **no** `# gh-aw-metadata:` line) are **not** skipped by this flag — add a file-level exclusion:

```yaml
exclusions:
  - file: ".github/workflows/agentics-maintenance.yml"
```

With `--verbose`, skipped agentic workflows appear on stderr as `discovery: skipped <file> (agentic workflow)`.

---

## Inline Suppression Directives

For one-off suppressions inside a workflow file, use inline comment directives.

### Suppress the next line

```yaml
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v6
```

`disable-next-line` suppresses diagnostics reported on **the very next YAML line** only. The comment must be placed directly above the key that the rule reports on — not above the parent node.

For example, to suppress an `if-cond` diagnostic, the comment must be directly above the `if:` key:

```yaml
steps:
  # ✗ Does NOT work — targets the step line, but if-cond reports on the if: line
  # seiton: disable-next-line if-cond
  - run: echo ok
    if: ${{ true }}

  # ✓ Works — comment is directly above the if: key
  - run: echo ok
    # seiton: disable-next-line if-cond
    if: ${{ true }}
```

Similarly, for `matrix` diagnostics that report on axis names inside the matrix block:

```yaml
jobs:
  build:
    strategy:
      matrix:
        # ✓ Works — directly above the axis that triggers the diagnostic
        # seiton: disable-next-line matrix
        os: []
```

> **Block scalars:** For multi-line `if:` conditions using block scalars (`|` or `>`), `disable-next-line` above the `if:` key works correctly. The diagnostic is adjusted to the `if:` key line, not the content line.
>
> ```yaml
> # ✓ Works — block scalar diagnostic is adjusted to the if: key line
> # seiton: disable-next-line if-cond
> if: |
>     ${{ contains(github.event.head_commit.message, 'skip') }}
> ```

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

### Multiple rule IDs

Multiple rule IDs are **comma-separated**. Spaces between commas are allowed, but space-separated rule IDs are **not** supported:

```yaml
# ✓ Comma-separated — both rules are suppressed
# seiton: disable-next-line dangerous-triggers, job-permissions-required

# ✗ Space-separated — treated as a single unknown rule ID, produces a config error
# seiton: disable-next-line dangerous-triggers job-permissions-required
```

Inline directives take precedence over config-file exclusions.

---

## Fix Configuration

`seiton --fix` applies auto-fixes. The `fix` section controls behavior for network-assisted fixes.

### Auto-fix for `job-timeout-minutes-required`

Set a default timeout value to use when auto-fixing jobs that lack `timeout-minutes`:

```yaml
fix:
  defaults:
    job-timeout-minutes: 15
```

If this is `null` or omitted, `job-timeout-minutes-required` does not apply an auto-fix.

---

## Tuning for Sample / Demo Repositories

Sample and demo repos often trigger many warnings because they intentionally keep workflows simple. Recommended approach:

1. **Disable noisy rules** in config and use `--min-severity error` in CI:

```yaml
# .github/seiton.yaml — demo/sample repo
rules:
  job-permissions-required:
    enabled: false
  job-timeout-minutes-required:
    enabled: false
  unpinned-uses:
    enabled: false
  dangerous-triggers:
    enabled: false
```

```sh
# CI step — exits 0 when only warnings remain
seiton check --min-severity error
```

2. **Use inline directives** for individual exceptions rather than disabling a rule globally:

```yaml
on: push

jobs:
  demo:
    runs-on: ubuntu-24.04
    steps:
      # seiton: disable-next-line unpinned-uses
      - uses: actions/checkout@v4
```

### Network-Assisted SHA Pinning

Auto-pin `uses:` references to commit SHAs. Enable via config or CLI flag:

```sh
seiton --fix --enable-pin-network
```

For persistent configuration, set `fix.pinning.enable-network: true` in the config file (see the [Annotated Example](#annotated-example) for the full `fix.pinning` block).

### Network-Assisted Image Digest Pinning

Auto-pin container images to `@sha256:<digest>`. Enable via config or CLI flag:

```sh
seiton --fix --enable-image-network
```

For persistent configuration, set `fix.images.enable-network: true` in the config file (see the [Annotated Example](#annotated-example) for the full `fix.images` block).

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
| `rules.<rule-id>.enabled` | `true` for default-on local rules; `false` for online rules and opt-in local rules |
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
| `network.max-concurrency` | `min(4, logical processors)` — same rules as **`max-concurrency`** above (`1`–logical processor count; excess rejected + clamped) |
| `network.github.ghes-api-url` | `""` |
| `network.github.ghes-fallback` | `false` |
| `output.sort-order` | `location` |

---

## Appendix: Loader Limits

To limit denial-of-service from maliciously large configuration inputs, validation enforces:

| Limit | Value | Diagnostic on violation |
|---|---|---|
| Maximum UTF‑8 payload size | 1 048 576 bytes (1 MB) | `seiton configuration exceeds maximum size (1048576 UTF-8 bytes)` or `seiton configuration file exceeds maximum size (1048576 bytes): '<path>'` |
| Maximum YAML DOM depth | 64 nested levels | `invalid lint config YAML: lint config YAML exceeds maximum nesting depth (64)` |
| Maximum DOM structural units | 50 000 nodes | `invalid lint config YAML: lint config YAML exceeds maximum structural size (50000 units)` |

Pattern matching notes:
- `fix.pinning.ignore-actions` uses **wildcard matching** (`*` = any sequence, `?` = single char) — no regex, no ReDoS risk.
- `fix.pinning.exclude-branches` uses exact string equality (ordinal).
- `fix` always annotates alias-like version refs (`vN`, `vN.M`) with the highest compatible concrete tag on the same resolved SHA when available (for example `v1` -> `v1.0.2`).
