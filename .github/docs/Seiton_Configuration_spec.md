# Seiton Configuration Specification

> This document is language-neutral — it specifies WHAT the configuration schema is and WHY design decisions were made. For the user-facing reference, see [`docs/configuration.md`](../../docs/configuration.md).

---

## 0. Scope

This document specifies:

- Configuration file format (`.github/seiton.yaml` / `seiton.yaml`)
- Top-level schema and per-section semantics
- Pattern matching rules for configuration values
- Loader resource limits
- Configuration file discovery and trust boundaries
- Default values

Out of scope:

- CLI flag mapping and precedence (see `Seiton_CLI_spec.md`)
- Rule behavior and diagnostics (see `Seiton_Linter_spec.md`)

### Design Principles

The schema is organized around the following principles:

| Principle | Description |
|---|---|
| **Structure by the user's mental model** | Organize the schema around rule IDs, exclusions, fix, and network |
| **Place rule-effective settings near the rule** | Put rule-specific options under `rules.<rule-id>` |
| **Separate everyday settings from advanced settings** | Make `rules` / `exclusions` primary, and treat `network` as advanced configuration |
| **Name by “what the user wants to do”** | Use direct object-like key names such as `events` and `known-hosted-labels` |
| **Group similar settings together** | Consolidate network-related settings under `network` |
| **Unify naming convention** | Use kebab-case for all keys |
| **Hide internal concepts** | Do not expose `analysis` or `audit` as independent keys; integrate them into existing structures |

---

## 1. Schema Overview

### 1.1 Top-level Structure

```yaml
rules:        # Per-rule enable / severity / rule-specific options
exclusions:   # Suppress diagnostics by file or job
discovery:    # Control file discovery behavior
fix:          # Control auto-fix behavior
network:      # Common network-related settings
output:       # Control diagnostic output
```

All sections are optional. An empty file is equivalent to using the default configuration. Unknown top-level keys are configuration errors.

---

## 2. `rules`

Per-rule configuration. Each key is a rule ID in kebab-case. Unknown rule IDs are configuration errors.

```yaml
rules:
  # Disable a rule
  runner-no-latest:
    enabled: false

  # Override severity
  checkout-persist-credentials:
    severity: warning    # error | warning | info

  # Enable an opt-in online rule
  known-vulnerable-actions:
    enabled: true

  # Rule-specific: extend events (adds to built-in set)
  dangerous-triggers:
    severity: error
    events:
      - issue_comment

  # Rule-specific: extend runner labels (adds to built-in set)
  runner-label:
    known-hosted-labels:
      - ubuntu-24.04-arm

  # Rule-specific: replacement mapping for -latest runners
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: ubuntu-24.04
      windows-latest: windows-2025
      macos-latest: macos-15

  # Rule-specific: extend public registries (adds to built-in set)
  credentials:
    public-registries:
      - registry.example.com

  # Rule-specific: extend untrusted triggers (adds to built-in set)
  cache-poisoning-trigger:
    untrusted-triggers:
      - issue_comment

  # Rule-specific: extend output commands (adds to built-in set)
  unredacted-secrets:
    output-commands:
      - tee

  # Rule-specific: deny/allow uses references
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"

  # Rule-specific: assumed events for undefined variable checks in expressions
  expr-undefined-var:
    assume-events:
      - workflow_dispatch

  # Rule-specific: thresholds for overprovisioned secrets
  overprovisioned-secrets:
    max-step-env-secrets: 5
    max-job-secrets: 10
```

### 2.1 Rule-specific Options

| Rule | Key | Type | Description |
|---|---|---|---|
| `dangerous-triggers` | `events` | `string[]` | Additional dangerous trigger events; added to the built-in set |
| `runner-label` | `known-hosted-labels` | `string[]` | Additional known runner labels; added to the built-in set |
| `runner-no-latest` | `fix-mapping` | `map[string]string` | Mapping from runner labels to be detected to replacement labels used during auto-fix. Keys are matched ASCII case-insensitively, and values are used as replacement text as-is |
| `credentials` | `public-registries` | `string[]` | Additional public registries; added to the built-in set |
| `cache-poisoning-trigger` | `untrusted-triggers` | `string[]` | Additional low-trust triggers; added to built-in set (`pull_request_target`, `workflow_run`, `issue_comment`) |
| `unredacted-secrets` | `output-commands` | `string[]` | Additional output commands to monitor; added to the built-in set |
| `forbidden-uses` | `deny` / `allow` | `string[]` | Deny/allow wildcard patterns for `uses:` references |
| `expr-undefined-var` | `assume-events` | `string[]` | Events assumed during expression evaluation |
| `overprovisioned-secrets` | `max-step-env-secrets` | `int` | Maximum number of secrets at the step environment level |
| `overprovisioned-secrets` | `max-job-secrets` | `int` | Maximum number of secrets at the job level |
| `unpinned-uses` | `ignore-actions` | `{owner, refs?}[]` | Exclusions from SHA pin checks. `owner` is a glob. If `refs` is omitted, all refs are ignored; if specified, only exactly matching refs are ignored |

List-type rule-specific options are **added to the built-in set**. They do not replace it.

---

## 3. `exclusions`

Suppress diagnostics by file or job. A `file`-only entry is treated specially: it suppresses workflow diagnostics for the entire file, including parse errors. Configuration diagnostics are never suppressed.

```yaml
exclusions:
  - file: ".github/workflows/legacy-*.yml"
    rules:
      - runner-no-latest
      - job-permissions-required

  - file: ".github/workflows/release.yml"
    jobs:
      - publish
    rules:
      - credentials
```

| Key | Type | Required | Description |
|---|---|---|---|
| `file` | `string` scalar | Yes | Glob pattern (`*` / `**`, path separator `/`, case-sensitive) |
| `rules` | `string[]` | No | List of rule IDs to suppress. If omitted, the entire file is excluded from all rules. `["*"]` is equivalent to omission and is an explicit alias for suppressing all rules |
| `jobs` | `string[]` | No | Target job IDs (`job.id`). If omitted, the exclusion applies to the entire file |

### 3.1 Scoping Semantics

**Additive narrowing, also called progressive narrowing**:

- `file` only → suppress workflow diagnostics for the entire file, including parse errors
- `file` + `jobs` → exclude the specified jobs from all rules
- `file` + `rules` → exclude only the specified rules for the entire file
- `file` + `jobs` + `rules` → exclude only the specified rules for the specified jobs

Notes:

- Even for a `file`-only exclusion, configuration diagnostics raised during `rules` / `exclusions` normalization are still returned.
- For `file` + `rules` / `jobs`, parse errors are not suppressed.

`rules: []`, an explicit empty list, is a no-op and has no exclusion effect. Omission and an empty list have different meanings. `rules: ["*"]` is equivalent to omitting `rules`, and is normalized to `null`, meaning all rules.

`file` is a scalar value, meaning a single pattern. Use multiple entries when multiple patterns are required.

### 3.2 `validate-config` Behavior

During `validate-config`, if multiple exclusions have the same normalized `file` and the same `jobs` scope, one info diagnostic is emitted per scope. Example: `exclusion for '.github/workflows/ci.yml' appears 2 times at exclusions[1] (line 2), exclusions[2] (line 5); consider merging rules into one entry`. The message lists 1-based `exclusions[N]` indexes and YAML start lines. The diagnostic location points to the first duplicate entry. Even if there are three or more duplicates, only one diagnostic is emitted, with the final count shown. Path separators in the `file` pattern (`\` / `/`) are normalized when determining whether entries belong to the same scope. Entries are not merged automatically.

**Cross-workflow job ID validation**, only for job-scoped exclusions:

- Discover `.github/workflows/` under the current working directory, parse workflows matching each exclusion's `file` pattern, and validate the IDs listed under `jobs`.
- Unknown job IDs produce error diagnostics on the **configuration file path**, using the same message as lint. They are not mixed with `error[parse]` diagnostics on workflow files.
- If a `file` pattern does not match any discovered workflow, emit a **warning** rather than an error, considering the possibility that files were not checked out in CI.
- Parse only matched workflows. Files with no job-scoped exclusion, or files whose patterns do not match, are not read.
- If a matched workflow cannot be parsed, or if its `jobs` section is empty, skip job ID validation, matching `LintEngine` behavior during lint. This avoids false positives for unknown job IDs.
- If a glob in a single exclusion entry matches multiple workflows and the same unknown job ID is missing from multiple files, emit only **one error per job ID**, avoiding duplicate messages. Job ID duplication checks are case-insensitive.
- Workflow path collection is **ordinal and case-sensitive**. This avoids accidentally merging distinct files on case-sensitive file systems, consistent with the `file` glob used in exclusions.
- With `--verbose`, output `verbose: job-id-check: N workflow file(s) scanned for M job-scoped exclusion(s)` to stderr.

---

## 4. `discovery`

Controls file discovery behavior.

```yaml
discovery:
  skip-agentic-workflows: true
```

| Key | Type | Default | Description |
|---|---|---|---|
| `skip-agentic-workflows` | `bool` | `false` | When `true`, workflows containing `# gh-aw-metadata:` within the first 10 lines are excluded from lint targets as an opt-in behavior. This can be overridden by the CLI option `--skip-agentic-workflows`. |

### 4.1 Agentic Workflow (gh-aw)

- `skip-agentic-workflows` detects only the **`# gh-aw-metadata:` comment**. It does not inspect filenames or `DO NOT EDIT` headers. Many gh-aw lock files, for example `monthly-oss-repo-status.lock.yml`, include this marker.
- gh-aw-generated files without metadata, for example `agentics-maintenance.yml` with only `DO NOT EDIT`, are **not skipped**. Exclude them at the file level using `exclusions`, with `file` only and all rules suppressed.

```yaml
exclusions:
  - file: ".github/workflows/agentics-maintenance.yml"
```

---

## 5. `fix`

Controls auto-fix behavior for `seiton fix`.

```yaml
fix:
  defaults:
    job-timeout-minutes: 15

  pinning:
    enable-network: true
    min-age-days: 14
    exclude-branches:
      - main
      - master
    ignore-actions:
      - uses: "slsa-framework/*"
        ref: "*"

  images:
    enable-network: true
    exclude-images:
      - scratch
    exclude-tags:
      - latest
    ignore-images:
      - "mcr.microsoft.com/**"
```

| Key | Type | Default | Description |
|---|---|---|---|
| `defaults.job-timeout-minutes` | `int?` | `null` | Value inserted by auto-fix for `job-timeout-minutes-required`. `null` disables auto-fix |
| `pinning.enable-network` | `bool` | `false` | Enables SHA resolution over the network |
| `pinning.min-age-days` | `int` | `14` | Minimum age in days required for commits |
| `pinning.exclude-branches` | `string[]` | `["main", "master"]` | Branch names for which pinning is skipped; **exact match**, ordinal |
| `pinning.ignore-actions` | `IgnoreActionEntry[]` | `[]` | Actions for which pinning is skipped. **Wildcard matching**: `*` = any sequence, `?` = any single character. Regex is not used, so there is no ReDoS risk |
| `images.enable-network` | `bool` | `false` | Enables digest resolution over the network |
| `images.exclude-images` | `string[]` | `["scratch"]` | Image names for which pinning is skipped |
| `images.exclude-tags` | `string[]` | `["latest"]` | Tag names for which pinning is skipped |
| `images.ignore-images` | `string[]` | `[]` | Images for which pinning is skipped, using glob patterns |

---

## 6. `network`

Common network-related settings. These apply to both online rules and network-assisted fixes.

```yaml
network:
  on-error: skip
  timeout-seconds: 30
  max-concurrency: 4
  github:
    ghes-api-url: ""
    ghes-fallback: false
```

| Key | Type | Default | Constraints | Description |
|---|---|---|---|---|
| `on-error` | `string` | `skip` | `skip` \| `fail` | Behavior on network errors |
| `timeout-seconds` | `int` | `30` | `0`–`300`; values outside the range are errors and are clamped | Timeout per request |
| `max-concurrency` | `int` | `min(4, CPU count)` | `1`–`CPU count`; values outside the range are errors and are clamped | Number of parallel requests |
| `github.ghes-api-url` | `string` | `""` | Empty = github.com only. HTTPS required; userinfo prohibited | GHES API base URL |
| `github.ghes-fallback` | `bool` | `false` | — | Fall back to github.com if GHES fails |

The HTTP client has **`AllowAutoRedirect` disabled** and follows **only same-origin redirects**. For `3xx` redirects to a different origin, no request is issued, preventing token leakage.

---

## 7. `output`

```yaml
output:
  sort-order: location    # location | rule
```

| Key | Type | Default | Description |
|---|---|---|---|
| `sort-order` | `string` | `location` | `location`: source-location order. `rule`: rule-priority order |

---

## 8. Pattern Matching Types

Pattern matching used in configuration values differs by use case.

| Configuration Location | Algorithm | Details |
|---|---|---|
| `exclusions[].file` | `GlobMatch` | Segment-based `*` / `**`, case-sensitive |
| `fix.pinning.ignore-actions` | `WildcardMatch` (char) | `*` = any sequence, `?` = any single character. Regex is not used |
| `fix.images.ignore-images` | `GlobMatch` | Same as `exclusions[].file` |
| `rules.forbidden-uses.deny/allow` | `WildcardMatchUsesPolicy` (byte) | `*` can cross path separators `/`; `?` = any single character |
| CLI `--ignore` | `string.Contains` | Substring match, case-insensitive |
| `fix.pinning.exclude-branches` | `string.Equals` | Exact match, ordinal |

`WildcardMatch` and `WildcardMatchUsesPolicy` use the same algorithm, a two-pointer implementation with star-index backtracking, with `char` and `byte` overloads. They are implemented as shared logic in `ActionRefHelpers`. The algorithm is deterministic and does not have exponential blow-up, so there is no ReDoS risk.

---

## 9. Loader Resource Limits

Defense against malicious configuration input.

| Limit | Value | Description |
|---|---|---|
| UTF-8 payload limit | `1,048,576` bytes | Common limit for `--config`, `ValidateFile`, and `Validate` |
| Maximum YAML DOM depth | `64` | Nesting depth of mappings and sequences |
| DOM structure unit limit | `50,000` | Total number of scalar keys, scalar leaves, and compound nodes |

If a limit is exceeded, validation fails and the configuration is not loaded.

---

## 10. Configuration File Discovery

1. `--config` option / `SEITON_CONFIG` environment variable, explicit specification
2. Search upward from the current directory:
   - `.github/seiton.yaml`
   - `.github/seiton.yml`
   - `seiton.yaml`
   - `seiton.yml`
3. If none is found, use built-in defaults

### Trust Boundary

- Configuration files are recommended to be committed files subject to review
- `SEITON_CONFIG` / `--config` accept arbitrary paths, so on shared runners, specify only trusted paths
- Be careful when reading configuration from the merge ref of a fork PR
- `seiton check --verbose` / `seiton fix --verbose` output the resolved `config:` path to stderr

---

## 11. Default Values

| Setting | Default |
|---|---|
| `rules.<rule-id>.enabled` | `true` for local rules / `false` for online rules |
| `rules.<rule-id>.severity` | Rule-specific default |
| `exclusions` | `[]` |
| `discovery.skip-agentic-workflows` | `false` |
| `fix.defaults.job-timeout-minutes` | `null`; auto-fix disabled |
| `fix.pinning.enable-network` | `false` |
| `fix.pinning.min-age-days` | `14` |
| `fix.pinning.exclude-branches` | `["main", "master"]` |
| `fix.pinning.ignore-actions` | `[]` |
| `fix.images.enable-network` | `false` |
| `fix.images.exclude-images` | `["scratch"]` |
| `fix.images.exclude-tags` | `["latest"]` |
| `fix.images.ignore-images` | `[]` |
| `network.on-error` | `skip` |
| `network.timeout-seconds` | `30` |
| `network.max-concurrency` | `min(4, logical processor count)` |
| `network.github.ghes-api-url` | `""`; github.com only |
| `network.github.ghes-fallback` | `false` |
| `output.sort-order` | `location` |

---

## Appendix A: Design History

This appendix records problems with the initial configuration design and the trade-offs that shaped the current schema. It is retained for context; the normative specification is in the sections above.

### A.1 Problems with the Previous Design

| # | Problem | Example |
|---|---|---|
| **U-1** | The user's mental model did not align with the config structure | Users think in terms of rule IDs, but concepts split for internal implementation reasons, such as `additiveCustomization`, `exprContext`, `pin_resolution`, and `online_audit`, were exposed in the config |
| **U-2** | Setting names described “how it is implemented” rather than “what the user wants to do” | Internal module names such as `additiveCustomization` and `exprContext` were surfaced directly |
| **U-3** | Rule IDs and their settings were not directly connected | Related settings were separated, such as `dangerous-triggers` and `additionalDangerousEvents`, or `runner-label` and `additionalKnownHostedLabels` |
| **U-4** | Similar settings were scattered across multiple locations | Network-related settings such as timeout, concurrency, and fail-open were duplicated under `pin_resolution` and `online_audit` |
| **U-5** | Settings with different levels of importance were placed at the same level | Everyday settings such as `rules` were placed alongside low-level settings such as `token_env_vars` and `request_timeout_sec` |
| **U-6** | The add-only design was too visible in the UI | `additionalDangerousEvents` — users only want to know the final effective set |
| **U-7** | Naming conventions and abstraction levels were inconsistent | Mixed kebab-case and snake_case, and verbose names such as `additional...` |

### A.2 Design Trade-offs

| Item | Decision | Reason |
|---|---|---|
| Top-level `analysis` key | **Not adopted** | `assume-events` fits naturally under the rule as `rules.expr-undefined-var.assume-events`. There is no need for an independent section |
| Top-level `audit` key | **Not adopted** | Enabling an online rule is unified as `rules.<rule-id>.enabled: true`. A separate section would create duplicated management |
| `network.fail-open` | **Use `network.on-error: skip \| fail`** | fail-open/fail-closed are ambiguous security terms. Explicit enum values communicate intent more clearly |
| `exclusions[].files` → `exclusions[].file` | **Use a scalar value, a single glob** | The singular form matches the type and avoids confusion. Multiple patterns are represented by multiple entries |
| `extend` keyword | **Not adopted; removed** | It leaked the internal concept of built-in sets to users. The design was changed to flat lists, with the documentation explicitly stating that these values are additive |
