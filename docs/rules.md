# Rules

This page documents all lint rules included in Seiton. It is the **canonical user-facing reference** for detailed rule behavior, examples, and remediation guidance. Implementation specs (`.github/docs/Seiton_Linter_*.md`) carry only brief summaries and cross-reference this document.

> **Tip:** Run `seiton rules` to see all rules and their effective enabled/disabled status in your terminal. Use `seiton rules --format json` for machine-readable output. See [Usage](usage.md#seiton-rules) for details.

Rule sections use **Summary** (what it detects), **Why** (risk and intent), **Remediation** (how to fix), when needed, and **When fixing** (side effects and follow-up checks). For auto-fixable rules, always read **When fixing** before applying broad `--fix` updates.

Default rules are enabled with no configuration required.

```shell
$ seiton rules
Rule                                     Enabled   Type     Severity   Fix   Document   Reason
---------------------------------------------------------------------------------------------------------
job-structure                            yes       local    error      no    both       default
reusable-workflow                        yes       local    error      no    both       default
permissions                              yes       local    mixed      no    both       default
popular-action-inputs                    yes       local    warning    yes   both       default
unpinned-uses                            yes       local    mixed      yes   both       default
unpinned-image                           yes       local    warning    yes   both       default
dangerous-triggers                       yes       local    warning    no    both       default
job-permissions-required                 yes       local    warning    yes   both       default
needs-graph                              yes       local    error      no    both       default
shell-name                               yes       local    mixed      no    both       default
runner-label                             yes       local    mixed      no    both       default
id-naming                                yes       local    error      yes   both       default
glob-pattern                             yes       local    error      no    both       default
dispatch-inputs                          yes       local    error      no    both       default
schedule-event                           yes       local    error      no    both       default
deny-write-all                           yes       local    error      yes   both       default
credentials                              yes       local    mixed      no    both       default
template-injection                       yes       local    error      yes   both       default
expr-undefined-var                       yes       local    error      no    both       default
run-env-context-direct-use               yes       local    error      yes   both       default
runner-no-latest                         yes       local    warning    yes   both       default
run-secrets-context-direct-use           yes       local    error      yes   both       default
run-inputs-context-direct-use            yes       local    error      yes   both       default
secrets-whole-context-access             yes       local    error      no    both       default
checkout-persist-credentials             yes       local    warning    yes   both       default
deny-read-all                            yes       local    error      yes   both       default
deny-inherit-secrets                     yes       local    error      no    both       default
job-timeout-minutes-required             yes       local    error      yes   both       default
github-app-token-inputs                  yes       local    error      no    both       default
cache-poisoning-trigger                  yes       local    warning    no    both       default
self-hosted-runner-trigger               yes       local    warning    no    both       default
unredacted-secrets                       yes       local    warning    no    both       default
secrets-outside-env                      yes       local    warning    no    both       default
workflow-secrets                         yes       local    error      no    both       default
job-secrets                              yes       local    error      no    both       default
action-shell-is-required                 yes       local    error      no    action     default
matrix                                   yes       local    warning    no    both       default
env-var                                  yes       local    warning    no    both       default
deprecated-commands                      yes       local    warning    no    both       default
if-cond                                  yes       local    warning    no    both       default
fake-ternary                             yes       local    warning    no    both       default
archived-uses                            yes       local    warning    no    both       default
insecure-commands                        yes       local    warning    no    both       default
overprovisioned-secrets                  yes       local    warning    no    both       default
forbidden-uses                           yes       local    warning    no    both       default
ref-version-mismatch                     yes       local    warning    no    both       default
use-trusted-publishing                   yes       local    warning    no    both       default
local-action-inputs                      yes       local    mixed      no    workflow   default
workflow-call-input-default              yes       local    error      no    both       default
outdated-action-runner                   yes       local    error      no    both       default
if-expr-wrapper                          yes       local    warning    yes   both       default
concurrency-limits                       no        local    warning    no    workflow   opt-in (not configured)
unsound-condition                        yes       local    warning    yes   both       default
unpinned-tools                           yes       local    warning    no    both       default
unsound-contains                         yes       local    mixed      no    workflow   default
bot-conditions                           yes       local    mixed      no    workflow   default
artipacked                               yes       local    mixed      no    workflow   default
known-vulnerable-actions                 no        online   error      no    workflow   opt-in (not configured)
impostor-commit                          no        online   error      no    workflow   opt-in (not configured)
ref-confusion                            no        online   error      no    workflow   opt-in (not configured)
stale-action-refs                        no        online   warning    no    workflow   opt-in (not configured)

61 rules total (56 enabled, 5 disabled)

To enable an opt-in rule, add to .github/seiton.yaml:
  rules:
    <rule-id>:
      enabled: true

Online rules use the GitHub API. Set GITHUB_TOKEN (or SEITON_GITHUB_TOKEN) to avoid rate limits.
```

**Legend:**

| Symbol | Meaning |
|---|---|
| ✓ | Rule is **on by default** (no config required). |
| ✗ | Rule is **off by default**; opt in with `rules.<id>.enabled: true`. |
| — | Rule works fully **offline** (no network access). |
| online | Rule requires **network access** (GitHub API); opt-in only. |
| △ | Auto-fix is **partial** (applies only to some cases). |

---

## Rule Index

### Correctness

- [job-structure](#job-structure)
- [reusable-workflow](#reusable-workflow)
- [permissions](#permissions)
- [needs-graph](#needs-graph)
- [shell-name](#shell-name)
- [id-naming](#id-naming)
- [glob-pattern](#glob-pattern)
- [runner-label](#runner-label)
- [runner-no-latest](#runner-no-latest)
- [popular-action-inputs](#popular-action-inputs)
- [action-shell-is-required](#action-shell-is-required)
- [matrix](#matrix)
- [env-var](#env-var)
- [if-cond](#if-cond)
- [fake-ternary](#fake-ternary)
- [if-expr-wrapper](#if-expr-wrapper)
- [unsound-condition](#unsound-condition)
- [concurrency-limits](#concurrency-limits)
- [deprecated-commands](#deprecated-commands)
- [dispatch-inputs](#dispatch-inputs)
- [schedule-event](#schedule-event)
- [workflow-call-input-default](#workflow-call-input-default)
- [local-action-inputs](#local-action-inputs)
- [outdated-action-runner](#outdated-action-runner)

### Security

- [template-injection](#template-injection)
- [dangerous-triggers](#dangerous-triggers)
- [run-env-context-direct-use](#run-env-context-direct-use)
- [run-secrets-context-direct-use](#run-secrets-context-direct-use)
- [run-inputs-context-direct-use](#run-inputs-context-direct-use)
- [secrets-whole-context-access](#secrets-whole-context-access)
- [expr-undefined-var](#expr-undefined-var)
- [cache-poisoning-trigger](#cache-poisoning-trigger)
- [self-hosted-runner-trigger](#self-hosted-runner-trigger)
- [insecure-commands](#insecure-commands)
- [unsound-contains](#unsound-contains)
- [bot-conditions](#bot-conditions)
- [artipacked](#artipacked)

### Permissions & Secrets

- [deny-write-all](#deny-write-all)
- [deny-read-all](#deny-read-all)
- [job-permissions-required](#job-permissions-required)
- [credentials](#credentials)
- [checkout-persist-credentials](#checkout-persist-credentials)
- [workflow-secrets](#workflow-secrets)
- [job-secrets](#job-secrets)
- [unredacted-secrets](#unredacted-secrets)
- [secrets-outside-env](#secrets-outside-env)
- [overprovisioned-secrets](#overprovisioned-secrets)
- [deny-inherit-secrets](#deny-inherit-secrets)

### Supply Chain

- [unpinned-uses](#unpinned-uses)
- [unpinned-image](#unpinned-image)
- [unpinned-tools](#unpinned-tools)
- [archived-uses](#archived-uses)
- [ref-version-mismatch](#ref-version-mismatch)
- [forbidden-uses](#forbidden-uses)
- [github-app-token-inputs](#github-app-token-inputs)
- [job-timeout-minutes-required](#job-timeout-minutes-required)
- [use-trusted-publishing](#use-trusted-publishing)

### Online (opt-in)

- [known-vulnerable-actions](#known-vulnerable-actions)
- [impostor-commit](#impostor-commit)
- [ref-confusion](#ref-confusion)
- [stale-action-refs](#stale-action-refs)

---

## Correctness

---

### `job-structure`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates core job shape. `uses` (reusable workflow call) is mutually exclusive with `steps` and `runs-on`. Each job must be either a reusable-call form (`uses`) or an executable form (`runs-on` + `steps`).

**Why:** Invalid job shape causes workflow parse/runtime failures and makes execution intent ambiguous for reviewers.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    steps:                  # ERROR: "runs-on" section is missing
      - run: echo hello
```

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1
    steps:                  # ERROR: cannot have both uses and steps
      - run: echo hello
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

---

### `reusable-workflow`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates reusable workflow call semantics. `with` and `secrets` are only valid under a `uses` job. Reusable-call jobs must not contain incompatible execution keys (`steps`, `container`, `services`, etc.).

**Why:** Reusable-call contract violations fail late at runtime and can silently bypass intended inputs/secrets wiring.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    with:                   # ERROR: key 'with' requires uses
      target: prod
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1
    container: node:20      # ERROR: incompatible with uses
```

**Remediation:**

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1
    with:
      target: prod
```

---

### `permissions`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `permissions` values. Scalar must be `read-all` or `write-all`. Per-scope values must be `read`, `write`, or `none`. Even when valid, scalar permissions (`read-all`, `write-all`) emit a warning because explicit per-scope mapping is preferred.

**Why:** Strict permission value validation prevents invalid tokens and nudges workflows toward explicit least-privilege declarations.

**Example trigger:**

```yaml
on: push
permissions: admin-all              # ERROR: must be 'read-all' or 'write-all'
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

```yaml
on: push
permissions: read-all               # WARNING: overly broad; prefer explicit per-scope mapping
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    permissions:
      contents: admin              # ERROR: must be 'read', 'write', or 'none'
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:** Replace scalar permissions with explicit per-scope mapping at the job level:

```yaml
# Before (workflow-level scalar)
on: push
permissions: read-all
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng

---
# After (job-level explicit scopes)
on: push
jobs:
  build:
    permissions:
      contents: read
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

---

### `needs-graph`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates the job dependency graph. Errors on unknown dependency targets and circular dependencies.

**Why:** Broken dependency graphs create deadlocks or incorrect execution order, leading to partial or unreliable CI results.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    needs: nonexistent       # ERROR: references unknown job
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  a:
    needs: b                 # ERROR: cyclic dependencies detected
    runs-on: ubuntu-24.04
    steps:
      - run: echo a
  b:
    needs: a
    runs-on: ubuntu-24.04
    steps:
      - run: echo b
```

**Remediation:**

```yaml
on: push
jobs:
  setup:
    runs-on: ubuntu-24.04
    steps:
      - run: echo setup
  build:
    needs: setup
    runs-on: ubuntu-24.04
    steps:
      - run: echo build
```

---

### `shell-name`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates shell names in workflow/job defaults and `run` steps. Reports shells outside the supported set for the target platform.

**Why:** Unsupported shell declarations fail at execution time and can silently diverge behavior across runner platforms.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo hello
        shell: zsh             # ERROR: invalid shell name
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo hello
        shell: cmd             # ERROR: cmd is not available on ubuntu
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo hello
        shell: bash
```

---

### `id-naming`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Validates `job.id` and `step.id` values. IDs must use only alphanumeric characters, hyphens, and underscores.

**Why:** Invalid IDs break references such as `needs`, `steps.<id>`, and outputs, which can lead to skipped dependencies or empty runtime values.

**Example trigger:**

```yaml
on: push
jobs:
  1build:                   # ERROR: must start with a letter or _
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - id: setup.v1          # ERROR: invalid step ID
        run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - id: setup-v1
        run: echo ok
```

**When fixing:**

- Auto-fix rewrites deterministic `needs` string references in the same workflow.
- Expression references (for example `${{ needs.old-id.outputs.x }}`) may still require manual updates.
- If normalization would create duplicates, auto-fix is not attached.

---

### `glob-pattern`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates glob syntax in event trigger filters. Reports invalid patterns and incompatible filter combinations (`branches` vs `branches-ignore`, `paths` vs `paths-ignore`, `tags` vs `tags-ignore`).

**Why:** Invalid trigger filters cause workflows to run too often or not at all, which can break release and protection pipelines.

**Example trigger:**

```yaml
on:
  push:
    branches:
      - "**[invalid"          # ERROR: invalid glob syntax
    branches-ignore:          # ERROR: incompatible with branches
      - develop
```

**Remediation:**

```yaml
on:
  push:
    branches:
      - main
      - "feature/**"
```

---

### `runner-label`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on unknown GitHub-hosted runner labels in `runs-on`. Self-hosted labels and expression-only values are excluded.

**Why:** Unknown or conflicting runner labels lead to scheduling failures or platform mismatches that destabilize CI.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-9999     # WARNING: unknown label
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    runs-on: [ubuntu-latest, windows-latest]  # ERROR: OS conflict
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

**Configuration:**

- `known-hosted-labels`: Add runner labels that should be treated as known GitHub-hosted labels (reduces unknown-label false positives).
- See: [configuration.md#runner-labelknown-hosted-labels](configuration.md#runner-labelknown-hosted-labels)

---

### `runner-no-latest`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Warns when moving `*-latest` runner labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used. These labels silently change the underlying runner when GitHub releases a new version. Also detects custom labels configured via `fix-mapping`.

**Why:** Moving runner aliases can change image contents and default toolchains without any workflow diff, causing sudden CI regressions. Pinning to explicit versions keeps execution environments reproducible.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest   # WARNING: prefer explicit version
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

**When fixing:**

- `seiton --fix` can rewrite labels only when `rules.runner-no-latest.fix-mapping` is configured.
- After pinning, verify tool/runtime compatibility on the new fixed image.
- For custom/self-hosted labels, ensure the replacement label exists in the runner fleet.

**Configuration:**

- `fix-mapping`: Define a label replacement map (`source -> pinned`) used by detection and `seiton --fix` rewrite targets.
- See: [configuration.md#runner-no-latestfix-mapping](configuration.md#runner-no-latestfix-mapping)

---

### `popular-action-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Validates input names for well-known popular actions. Reports unknown input keys that are likely typos.

**Why:** Input typos are often silently ignored by actions, so workflows can succeed while executing unintended defaults.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
        with:
          persist-credentials: false
          fetch-depht: 1      # ERROR: typo; did you mean 'fetch-depth'?
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
        with:
          persist-credentials: false
          fetch-depth: 1
```

**When fixing:**

- Auto-fix is attached only for unambiguous closest matches.
- Confirm the suggested key matches intent, not only spelling similarity.
- Re-run the job to verify behavior did not change unexpectedly.

---

### `action-shell-is-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Requires an explicit `shell` declaration on composite action `run` steps. Applies only to action-metadata files.

**Why:** Composite actions must be explicit about shell semantics to stay portable across caller workflows and runner OSes.

**Example trigger (action.yml):**

```yaml
name: My action
description: Sample composite action
runs:
  using: composite
  steps:
    - run: echo hello
      # ERROR: shell is required for composite action run steps
```

**Remediation:**

```yaml
name: My action
description: Sample composite action
runs:
  using: composite
  steps:
    - run: echo hello
      shell: bash
```

---

### `matrix`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `strategy.matrix` definitions. Reports inconsistent keys, invalid `include`/`exclude` shapes, and suspicious expansion patterns.

**Why:** Matrix shape errors can explode combinations, skip intended targets, or fail jobs unpredictably.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        os: []              # ERROR: axis has no values
        exclude:
          - arch: x64       # ERROR: unknown axis 'arch'
    steps:
      - run: echo ng
```

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        node: [10, 12, 14]
        os: [ubuntu-24.04, macos-14]
        exclude:
          - node: 13        # ERROR: value 13 does not match matrix combinations
            os: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        node: [10, 12, 14]
        os: [ubuntu-24.04, macos-14]
        exclude:
          - node: 10
            os: macos-14
    steps:
      - run: echo ok
```

---

### `env-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates environment variable names in `env:` blocks follow the portable naming convention `[A-Z_][A-Z0-9_]*`. Reports names that contain lowercase letters, dashes, or start with a digit.

**Why:** Portable env naming avoids shell-specific parsing differences and reduces subtle cross-platform failures.

**Example trigger:**

```yaml
on: push
env:
  foobar: x           # WARNING: not portable (lowercase)
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          foo-bar: x     # WARNING: not portable (contains dash)
        run: echo ng "${foo-bar}"
```

**Remediation:** Rename environment variables to use only uppercase letters, digits, and underscores:

```yaml
on: push
env:
  FOOBAR: x
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          FOO_BAR: x
        run: echo ok "${FOO_BAR}"
```

When a lowercase `env:` key only forwards an input or secret to a single `with:` field, you can often skip the intermediate variable and reference the context directly:

```yaml
# Avoid: lowercase env key used only to pass inputs into with:
env:
  upstream: ${{ inputs.upstream }}
steps:
  - uses: actions/checkout@v4
    with:
      ref: ${{ env.upstream }}

# Option A: uppercase env key (update every reference)
env:
  UPSTREAM: ${{ inputs.upstream }}
steps:
  - uses: actions/checkout@v4
    with:
      ref: ${{ env.UPSTREAM }}

# Option B: pass inputs directly when used once
steps:
  - uses: actions/checkout@v4
    with:
      ref: ${{ inputs.upstream }}
```

---

### `if-cond`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on malformed, constant, or unsound `if` conditions. Reports always-true / always-false conditions and context misuse.

**Why:** Unsound conditions weaken workflow control-flow guarantees and can unintentionally run or skip privileged steps.

**Example trigger:**

```yaml
jobs:
  build:
    if: ${{ false }}        # ERROR: constant expression in condition
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ !false }}   # ERROR: constant expression
        run: echo ng
      - if: ${{ 42 }}       # ERROR: constant expression
        run: echo ng
      - if: "${{ github.event_name == 'push' }} "  # ERROR: always true (trailing chars)
        run: echo ng
```

**Remediation:**

```yaml
jobs:
  build:
    if: ${{ github.ref != '' }}
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ success() }}
        run: echo ok
```

---

### `fake-ternary`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `cond && a || b` fake ternary idioms are used in expression-bearing fields. This pattern has different semantics from a true ternary when `a` is falsy.

**Why:** Fake ternary patterns are easy to misread and produce incorrect branches when the middle value can be falsy.

**Example trigger:**

```yaml
jobs:
  build:
    # ERROR: fake ternary pattern
    if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ inputs.deploy && 'yes' || 'no' }}
        run: echo ng
```

**Remediation:** Use explicit `if`-based branching or GitHub Actions' native conditional:

```yaml
jobs:
  build:
    if: ${{ github.ref_name == 'main' }}
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ inputs.deploy }}
        run: echo yes
```

---

### `if-expr-wrapper`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Warns when `if:` conditions are missing the `${{ }}` expression wrapper. Auto-fix is offered for single-line scalars without existing `${{` markers.

**Why:** Unwrapped conditions can be interpreted as plain strings and evaluated differently than intended, weakening gate logic.

**Example trigger:**

```yaml
jobs:
  build:
    if: github.ref != 'refs/heads/main'   # WARNING: missing ${{ }} wrapper
    runs-on: ubuntu-24.04
    steps:
      - if: github.event_name == 'push'   # WARNING: missing ${{ }} wrapper
        run: echo ng
      - if: "!cancelled()"                # WARNING: missing ${{ }} wrapper
        run: echo ng
```

**Remediation:** Wrap expressions in `${{ }}`:

```yaml
jobs:
  build:
    if: ${{ github.ref != 'refs/heads/main' }}
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ github.event_name == 'push' }}
        run: echo ok
      - if: ${{ !cancelled() }}
        run: echo ok
```

**When fixing:**

- Auto-fix targets single-line scalar forms only.
- Re-check quoting and operator behavior (`!`, function calls) after wrapping.

> **Note:** Bare `true`, `false`, `always()`, `failure()`, `cancelled()`, `success()` literals are intentionally excluded from this rule since GitHub Actions handles them natively.

---

### `unsound-condition`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Warns when `if:` uses a YAML block scalar (`|` or `>`) together with a fenced expression `${{ ... }}`. The trailing newline preserved by block-scalar clip chomping makes the final value a non-empty string, so the condition becomes truthy unexpectedly.

**Why:** This pattern can make a condition effectively always truthy and unintentionally run guarded jobs/steps.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    if: |
      ${{ github.event_name == 'push' }}
    steps:
      - run: echo ng
```

**Remediation:** Use strip chomping so the trailing newline is removed:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    if: |-
      ${{ github.event_name == 'push' }}
    steps:
      - run: echo ok
```

**When fixing:**

- Auto-fix rewrites `|` to `|-` and `>` to `>-` only when the scalar indicator location is deterministic.
- Validate behavior on representative events after chomping changes.

Auto-fix rewrites `|` to `|-` and `>` to `>-` when Seiton can locate the block-scalar indicator in source.

---

### `concurrency-limits`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | — | ✗ |

Warns when workflows or jobs lack `concurrency` settings with explicit `cancel-in-progress`. Without concurrency limits, parallel runs can waste resources and cause race conditions.

**Why:** Missing concurrency controls can trigger overlapping deploys and non-deterministic state transitions in shared environments.

**Example trigger:**

```yaml
on: push
# WARNING: workflow does not declare concurrency
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    concurrency: my-group  # WARNING: missing 'cancel-in-progress'
    steps:
      - run: echo ng
```

**Remediation:** Add a `concurrency` block with `group` and `cancel-in-progress`:

```yaml
on: push
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

**When fixing:**

- `cancel-in-progress: true` can terminate in-flight runs; ensure this behavior is acceptable for deploy/release jobs.
- Design `group` keys carefully to avoid unrelated branches canceling each other.

> **Note:** Reusable-only workflows (`on: workflow_call`) and workflow-call jobs (`uses:`) are skipped. When workflow-level concurrency is set, job-level checks are suppressed.

---

### `unsound-contains`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects `contains()` conditions that treat a plain string like a membership list, allowing substring matches to bypass the intended check.

**Why:** Substring membership checks are bypass-prone and can let attacker-influenced values satisfy privileged conditions.

**Example trigger:**

```yaml
jobs:
  deploy:
    if: ${{ contains('refs/heads/main refs/heads/develop', github.ref) }}
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:** Use an actual array via `fromJSON()` or explicit equality checks:

```yaml
jobs:
  deploy:
    if: ${{ contains(fromJSON('["refs/heads/main","refs/heads/develop"]'), github.ref) }}
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

> **Severity note:** This rule emits an error when the second argument is user-controllable (for example `github.ref`, `github['ref']`, `github.actor`, `env.*`, `env['NAME']`, `inputs.*`) and an info diagnostic for other context references.

---

### `bot-conditions`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a workflow gates privileged behavior on spoofable bot actor contexts such as `github.actor`, `github['actor']`, `github.triggering_actor`, `github.event.pull_request.sender.login`, `github.event['pull_request'].sender['login']`, `github.actor_id`, or `github['event']['pull_request']['sender']['id']`.

**Why:** Actor identity in trigger context can be spoofed or misattributed; privileged bot-only logic should bind to trusted PR-author context.

**Example trigger:**

```yaml
on: pull_request
jobs:
  automerge:
    if: ${{ github.actor == 'dependabot[bot]' }}
    runs-on: ubuntu-24.04
    permissions:
      contents: write
      pull-requests: write
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
        env:
          PR_URL: ${{ github.event.pull_request.html_url }}
          GH_TOKEN: ${{ github.token }}
```

**Remediation:** Prefer a context tied to the PR author rather than the trigger actor:

```yaml
on: pull_request
jobs:
  automerge:
    if: ${{ github.event.pull_request.user.login == 'dependabot[bot]' }}
    runs-on: ubuntu-24.04
    permissions:
      contents: write
      pull-requests: write
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
        env:
          PR_URL: ${{ github.event.pull_request.html_url }}
          GH_TOKEN: ${{ github.token }}
```

**Notes:**

- **Severity:**
  - **warning** — equality checks (`==`): grants privileges to a bot identity that can be spoofed.
  - **info** — inequality checks (`!=`): exclusion pattern with lower risk (attacker gains only normal processing). Reported only when `strict-detection: true` (default: off).

- **Suppression:** The diagnostic is suppressed entirely when:
  - A spoofable context comparison is AND-conjoined with a non-spoofable trigger-author context (`github.event.pull_request.user.login` or `github.event.pull_request.user.id`) checking the same literal value with the same operator (`==` with `==`, `!=` with `!=`).
  - Workflow triggers are not PR-only — for example `on: push` only, `on: schedule` only, or mixed triggers such as `push` + `pull_request` where `github.event.pull_request.user.login` is unavailable on non-PR events and `github.actor` is the practical cross-trigger bot check.
  - Diagnostics remain for PR-only workflows (`pull_request`, `pull_request_target`, `pull_request_review`, `pull_request_review_comment` only) where a trigger-author alternative is actionable.
- Known bot ID comparisons such as `github.actor_id == '49699333'` and equivalent bracket/index-style forms like `github['ACTOR_ID'] == 49699333` are also flagged. Prefer the corresponding trigger-author context like `github.event.pull_request.user.id`.

**Configuration:**

- `strict-detection`: Enable detection of inequality checks (`!=`) against spoofable contexts. This pattern is often used for exclusion (for example, "if not a bot") and has lower risk than equality checks, but it can still be bypassed by spoofing the trigger actor. Default is `false` to reduce false positives in common exclusion patterns.
- See: [configuration.md#bot-conditionsstrict-detection](configuration.md#bot-conditionsstrict-detection)

<a id="bot-conditions-decision-matrix"></a>

**Decision matrix** (when does this rule report? `*` = any value):

| `strict-detection` | Operator | Workflow triggers | Mitigation (AND-conjoined) | Outcome |
| --- | --- | --- | --- | --- |
| `false` | `==` | PR-only | none | **warning** |
| `false` | `==` | PR-only | dual `==` on `user.login` / `user.id` | no diagnostic |
| `false` | `!=` | PR-only | any | no diagnostic |
| `true` | `!=` | PR-only | none | **info** |
| `true` | `!=` | PR-only | dual `!=` on `user.login` / `user.id` | no diagnostic |
| `true` | `!=` | PR-only | mismatched operator | **info** |
| `true` | `==` | PR-only | none | **warning** |
| `*` | `*` | mixed or non-PR | `*` | no diagnostic |
| `true` | `!=` | PR-only | none | **info** |
| `true` | `!=` | PR-only | dual `!=` on `user.login` / `user.id` | none |
| `true` | `!=` | PR-only | mismatched operator | **info** |
| `true` | `==` | PR-only | none | **warning** |
| `*` | `*` | mixed or non-PR | `*` | none |

PR-only means `pull_request`, `pull_request_target`, `pull_request_review`, or `pull_request_review_comment` only (no `push`, `schedule`, `workflow_dispatch`, etc.).

---

### `artipacked`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects credential leakage risk when `actions/checkout` (without `persist-credentials: false`) is followed later in the same job by `actions/upload-artifact` uploading a dangerous path (root-like, parent-directory, or workspace-expression forms).

**Why:** Combining persisted git credentials with broad artifact uploads can leak repository credentials to downloadable artifacts.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11 # v4
      - uses: actions/upload-artifact@5d5d22a31266ced268874388b861e4b58bb5c2f3 # v4
        with:
          name: my-artifact
          path: .
          include-hidden-files: true
```

**Remediation:** Set `persist-credentials: false` on the checkout step, or upload only specific subdirectories:

```yaml
steps:
  - uses: actions/checkout@b4ffde65f46336ab88eb53be808477a3936bae11 # v4
    with:
      persist-credentials: false
  - uses: actions/upload-artifact@5d5d22a31266ced268874388b861e4b58bb5c2f3 # v4
    with:
      name: my-artifact
      path: dist/
```

**When fixing:**

- If you disable persisted credentials, verify later authenticated git operations still succeed with explicit auth.
- If you narrow artifact paths, ensure required files are still included for downstream consumers.

**Notes:**

<details>
<summary>Edge cases and detection details</summary>

- **Dangerous paths** include `.`, `..`, `*`, `./*`, `./**`, `./**/*`, `**`, `**/*`, `${{ github.workspace }}`, `${{ github.workspace }}/**`, `${{ github.workspace }}/..`, `../../_temp`, and their variants. Bracket-form expressions (`${{ github['workspace'] }}`) and normalized equivalents (`repo/..`) are also recognized. Workspace-expression suffixes are recognized only when the suffix is empty or starts with `/` or `\`.
- **Severity split:** error for legacy checkout (v1–v5, credentials in `.git/config`) with hidden-file upload risk; warning for checkout v6+ when the upload path can reach `$RUNNER_TEMP` (e.g., `../..`, `../../_temp`).
- **Hidden-file behavior:** `actions/upload-artifact@v4.4+` excludes hidden files by default. For unparseable refs (branch names, SHAs, arbitrary tags), the rule conservatively assumes hidden-file inclusion.
- **Exclusion suppression:** legacy case can be suppressed by globs like `!.git/**`, `!.git/config`, `!repo/.git/**`, or `!**/.git/**` when they cover all reachable `.git/config` locations. Bare `!.git` never suppresses. For v6+, suppression requires a recursive runner-temp subtree exclusion (`!../../_temp/**`).
- **Deferred scope:** checkout `with.path` subdirectory correlation with upload paths is not yet implemented.
- This rule is independent of `checkout-persist-credentials`. The latter flags every checkout without `persist-credentials: false`; `artipacked` only fires when a later dangerous upload in the same job can expose credentials.

</details>

---

### `deprecated-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects deprecated workflow command syntax (`::set-output`, `::save-state`, `::add-path`, `::set-env`) in `run` scripts. These commands are blocked or unsafe on modern runners.

**Why:** Deprecated command channels bypass current hardening and are increasingly unsupported on modern GitHub-hosted runners.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "::set-output name=digest::$DIGEST"
        # ERROR: workflow command "set-output" was deprecated
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "result=ok" >> "$GITHUB_OUTPUT"
```

---

### `dispatch-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `workflow_dispatch` input definitions for structural correctness. Reports excessive input count (max 25), type/option mismatches, invalid defaults, and duplicate options.

**Why:** Invalid dispatch input schemas break manual/API-triggered workflows and can invalidate release/runbook entrypoints.

**Example trigger:**

```yaml
on:
  workflow_dispatch:
    inputs:
      env:
        type: choice
        # ERROR: choice type must define non-empty options
      mode:
        type: string
        options:            # ERROR: options only valid for 'choice' type
          - fast
          - slow
      count:
        type: number
        default: abc        # ERROR: default is not a valid number
      flag:
        type: boolean
        default: yes        # ERROR: boolean default must be 'true' or 'false'
```

**Remediation:**

```yaml
on:
  workflow_dispatch:
    inputs:
      env:
        type: choice
        options:
          - staging
          - production
      mode:
        type: string
        default: fast
      count:
        type: number
        default: 10
      flag:
        type: boolean
        default: true
```

---

### `schedule-event`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `schedule` cron expressions for syntax correctness, minimum interval enforcement (5 minutes), and timezone validity.

**Why:** Invalid or overly frequent schedules can either never run or overrun CI budgets and rate limits.

**Example trigger:**

```yaml
on:
  schedule:
    - cron: "* * * * *"           # ERROR: runs too frequently (once per 60 seconds)
    - cron: "0 0 31 2 *"         # valid syntax but Feb 31 never triggers (no error, but suspicious)
    - cron: "0 25 * * *"         # ERROR: invalid hour value (0-23)
```

**Remediation:**

```yaml
on:
  schedule:
    - cron: "*/15 * * * *"       # every 15 minutes (meets 5-min minimum)
    - cron: "0 9 * * 1-5"        # weekdays at 09:00
```

---

### `workflow-call-input-default`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `workflow_call` input default values match their declared types. Also reports an error when a required input has a default value (the default will never be used).

**Why:** Invalid workflow-call defaults create reusable workflow contract drift and break downstream callers.

**Example trigger:**

```yaml
on:
  workflow_call:
    inputs:
      enabled:
        type: boolean
        default: yes              # ERROR: boolean default must be 'true' or 'false'
      retries:
        type: number
        default: many             # ERROR: number default is not numeric
      name:
        type: string
        required: true
        default: foo              # ERROR: required input's default will never be used
```

**Remediation:**

```yaml
on:
  workflow_call:
    inputs:
      enabled:
        type: boolean
        default: true
      retries:
        type: number
        default: 3
      name:
        type: string
        required: true
```

---

### `local-action-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates that local/composite action invocations (`uses: ./path`) provide all required inputs and do not pass unknown input keys. Also warns on deprecated inputs. Applies only to workflow files.

**Why:** Local action contract mismatches fail execution at call sites and are easy to miss during action evolution.

**Given the following local action (`./actions/deploy/action.yml`):**

```yaml
name: Deploy
description: Deploy to target environment
inputs:
  target:
    description: Deployment target
    required: true
  environment:
    description: Environment name
    required: true
  dry-run:
    description: Skip actual deployment
    required: false
    default: "false"
```

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: ./actions/deploy
        with:
          target: prod
          unknown-key: x          # ERROR: unknown input 'unknown-key'
          # (missing 'environment' which is required by action.yml)
```

**Remediation:** Provide all required inputs and remove unknown keys:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: ./actions/deploy
        with:
          target: prod
          environment: production
```

> **Note:** This rule reads the local action's `action.yml` / `action.yaml` from disk. It only works when the workflow file path is available and the action metadata file exists.

---

### `outdated-action-runner`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when a popular action version uses a deprecated Node.js runtime (`node12`, `node16`). These runtimes are no longer supported by GitHub Actions runners.

**Why:** Deprecated action runtimes eventually stop executing on hosted runners, causing sudden CI outages.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v3   # ERROR: runner is too old (node16)
```

**Remediation:** Update to a newer major version of the action:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v6
```

---

## Security

---

### `template-injection`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Detects unsafe direct interpolation of untrusted `github.event`-origin data into `run` script sinks. Using `${{ github.event.* }}` directly in a script can allow attackers to inject arbitrary shell commands through PR titles, comments, or labels.

**Why:** Event payload values are often user-controlled in fork/comment flows, so direct interpolation crosses trust boundaries and can lead to command injection.

**Example trigger:**

```yaml
- run: |
    echo "PR title: ${{ github.event.pull_request.title }}"
  # ERROR: pull_request.title is user-controlled
```

**Remediation:** Map untrusted values into environment variables first, then reference them as `$VAR` in the script:

```yaml
- env:
    PR_TITLE: ${{ github.event.pull_request.title }}
  run: |
    echo "PR title: $PR_TITLE"
```

**When fixing:**

- Auto-fix covers deterministic `run:` sink cases; some heredoc/quoting forms and `actions/github-script` remain manual.
- Prefer moving full expressions to `env:` and keeping shell scripts variable-only.
- Verify sensitive values are not echoed after migration.

---

### `dangerous-triggers`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when high-risk trigger events (`pull_request_target`, `workflow_run`, etc.) are used. These events execute with elevated repository context and write permissions.

**Why:** High-privilege trigger types can execute attacker-influenced code with repository-level authority, making fork and event-chain abuse significantly more dangerous.

**Example trigger:**

```yaml
on: pull_request_target    # WARNING: potentially dangerous trigger
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:** Restrict the trigger scope, add strict condition guards, or replace with a safer event. Common approaches:

- Switch to `pull_request` (unprivileged fork context) when secrets are not needed
- Add `if:` conditions (e.g. `github.event.pull_request.head.repo.full_name == github.repository`) to limit execution to trusted sources
- Isolate privileged work into a separate job gated by an explicit condition
- Use `on: push` when the workflow only needs to run on commits to the default branch

```yaml
# Approach A: switch to a safer event
on: pull_request
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

```yaml
# Approach B: guard with condition on pull_request_target
on: pull_request_target
jobs:
  build:
    runs-on: ubuntu-24.04
    if: ${{ github.event.pull_request.head.repo.full_name == github.repository }}
    steps:
      - run: echo ok
```

**When fixing:**

- Switching to `pull_request` reduces privilege and can disable secret-dependent steps by design.
- Conditional guards reduce risk but do not fully remove trust-boundary complexity; keep privileged operations isolated.
- Re-test approval/deploy flows after trigger changes to confirm expected execution paths.

**Configuration:**

- `events`: Add trigger event names treated as dangerous for this rule (additive to built-in defaults).
- See: [configuration.md#dangerous-triggersevents](configuration.md#dangerous-triggersevents)

---

### `run-env-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ env.* }}` is directly interpolated inside a `run` script. Shell variable expansion (`$VAR` / `$env:VAR`) must be used instead. By default, no diagnostic is emitted in shell no-expand contexts (single-quoted shell strings and single-quoted heredocs).

**Why:** Expression interpolation happens before shell execution and can mismatch shell quoting/expansion semantics, producing brittle behavior.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "${{ env.VERSION }}"  # ERROR: use $VERSION instead
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "$VERSION"
```

**When fixing:**

- Use `$VAR` for POSIX shells and `$env:VAR` for PowerShell.
- Enable `rules.run-env-context-direct-use.strict: true` to diagnose shell single-quoted contexts.
- Simple standalone single-quoted forms like `'${{ env.VERSION }}'` are auto-fixed by rewriting to double-quoted shell-variable form (`"${VERSION}"` / `"$env:VERSION"`) when strict mode is enabled.
- Compound expressions are not auto-fixed; move them into `env:` first, then reference shell variables.
- No auto-fix is attached inside no-expand heredocs or complex single-quoted regions (e.g. `'pre-${{ env.VERSION }}-post'`); review these manually.

Replace `${{ env.VAR }}` with `$VAR` (bash/sh) or `$env:VAR` (PowerShell).

For compound expressions (e.g. `${{ env.TAG || 'fallback' }}`), no auto-fix is available. A help message suggests moving the entire expression to an `env:` block and referencing the shell variable instead.

**Shell context policy**

| Shell context | `strict` | Outcome |
|---|---|---|
| Unquoted or double-quoted (expandable) | n/a | Diagnose |
| Shell single-quoted (`'...${{ }}...'`) | `false` | Suppress |
| Shell single-quoted (`'...${{ }}...'`) | `true` | Diagnose |
| Single-quoted heredoc (`<<'EOF'` body) | any | Suppress |

Auto-fix when a diagnostic is emitted:

| Shell context | Outcome |
|---|---|
| Unquoted or double-quoted | Fix when safe |
| Shell single-quoted | Simple standalone token only (`strict: true` only) |
| Single-quoted heredoc | n/a (suppressed) |
| Complex single-quoted (e.g. `'pre-${{ env.X }}-post'`) | No fix (diagnosed under `strict: true` only) |

- Single quotes inside a double-quoted string do not suppress detection.
- Shell single-quote detection is line-scoped (quotes are not tracked across newlines).

---

### `run-secrets-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ secrets.* }}` is directly interpolated inside a `run` script. Secrets should be mapped via `env:` and referenced through shell variables.

**Why:** Direct secret interpolation increases disclosure risk via logs and debugging output and weakens masking assumptions.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: |
          curl -H "Authorization: Bearer ${{ secrets.TOKEN }}"
        # ERROR: use env: indirection
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          TOKEN: ${{ secrets.TOKEN }}
        run: |
          curl -H "Authorization: Bearer $TOKEN"
```

**When fixing:**

- Auto-fix rewrites simple `${{ secrets.KEY }}` / `${{ secrets['KEY'] }}` by reusing an existing unique `env` mapping when present, or inserts a step `env:` entry and rewrites to the shell variable when `fix` is enabled.
- For compound expressions, move logic to `env:` manually and keep `run:` shell-variable-only; no fix is offered.
- Confirm secrets are not printed after refactoring.

Auto-fix replaces simple secret expressions when fix mode is on (`seiton --fix` or `fix` enabled in config). With no existing mapping, it adds `env: SECRET_NAME: ${{ secrets.SECRET_NAME }}` and rewrites the `run:` reference. Ambiguous mappings, heredoc no-expand bodies, and shell single-quoted strings remain no-fix. Shell single-quoted no-expand contexts still emit diagnostics with guidance for manual boundary refactoring.

**Shell context policy**

Unlike `run-env` / `run-inputs`, this rule keeps diagnostics in shell single-quoted contexts because secrets handling is security-sensitive. Only single-quoted heredocs are suppressed (no shell expansion occurs there).

| Shell context | Outcome |
|---|---|
| Unquoted or double-quoted (expandable) | Diagnose |
| Shell single-quoted (`'...${{ }}...'`) | Diagnose (manual-refactor guidance; no auto-fix) |
| Single-quoted heredoc (`<<'EOF'` body) | Suppress |

Auto-fix when a diagnostic is emitted:

| Shell context | Outcome |
|---|---|
| Unquoted or double-quoted | Fix when safe |
| Shell single-quoted | No fix |
| Single-quoted heredoc | n/a (suppressed) |

- Single quotes inside a double-quoted string do not suppress detection.
- Shell single-quote detection is line-scoped (quotes are not tracked across newlines).

---

### `run-inputs-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ inputs.* }}` or `${{ github.event.inputs.* }}` are directly interpolated inside a `run` script. Inputs may be user-controlled. By default, no diagnostic is emitted inside shell no-expand contexts (single-quoted shell strings and single-quoted heredocs).

**Why:** Inputs can carry untrusted user data; direct interpolation into shell commands introduces injection and quoting risks.

**Example trigger:**

```yaml
on:
  workflow_dispatch:
    inputs:
      benchmark:
        required: false
        type: string
jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "${{ inputs.benchmark }}"   # ERROR: use env: indirection
```

**Remediation:**

```yaml
on:
  workflow_dispatch:
    inputs:
      benchmark:
        required: false
        type: string
jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - env:
          BENCHMARK: ${{ inputs.benchmark }}
        run: echo "$BENCHMARK"
```

**When fixing:**

- Auto-fix may reuse an existing unique `env` mapping or insert a step-local mapping when deterministic.
- No fix is attached for ambiguous mappings.
- Enable `rules.run-inputs-context-direct-use.strict: true` to diagnose shell single-quoted contexts.
- Strict mode does not auto-fix shell single-quoted contexts (env mapping cannot be safely rewritten inside no-expand quotes); refactor manually.
- no-expand heredocs remain suppressed regardless of strict mode.
- Re-test dispatch/reusable-call paths after migration to confirm quoting and default behaviors.

**Notes:**

Auto-fix reuses an existing unique `env` mapping for the same input when available. Otherwise, when `fix` is enabled, it inserts a step-local `env:` entry and rewrites simple or compound expressions to a shell variable. For no-expand heredocs and shell single-quoted strings, this rule suppresses diagnostics to avoid non-actionable guidance. The env-insertion path additionally skips flow-style `env` and empty `env: {}`.

**Shell context policy**

| Shell context | `strict` | Outcome |
|---|---|---|
| Unquoted or double-quoted (expandable) | n/a | Diagnose |
| Shell single-quoted (`'...${{ }}...'`) | `false` | Suppress |
| Shell single-quoted (`'...${{ }}...'`) | `true` | Diagnose |
| Single-quoted heredoc (`<<'EOF'` body) | any | Suppress |

Auto-fix when a diagnostic is emitted:

| Shell context | Outcome |
|---|---|
| Unquoted or double-quoted | Fix when safe |
| Shell single-quoted | No fix (diagnosed under `strict: true` only) |
| Single-quoted heredoc | n/a (suppressed) |
| Complex single-quoted (e.g. `'pre-${{ inputs.X }}-post'`) | No fix (diagnosed under `strict: true` only) |

- Single quotes inside a double-quoted string do not suppress detection.
- Shell single-quote detection is line-scoped (quotes are not tracked across newlines).

---

### `secrets-whole-context-access`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when an expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`). This leaks all secrets simultaneously.

**Why:** Whole-context access collapses secret least-privilege boundaries and can expose every secret in one expression path.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "${{ toJson(secrets) }}"  # ERROR: exposes all secrets
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          MY_SECRET: ${{ secrets.MY_SECRET }}
        run: some-command --token "$MY_SECRET"
```

**When fixing:**

- Replace bulk `secrets` usage with explicit per-key mappings only for required values.
- Avoid dynamic key selection patterns that force whole-context references.
- Verify logs and diagnostic output do not serialize secret-bearing objects.

---

### `expr-undefined-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when expressions reference context roots unavailable in the current scope (e.g. `steps.*` at job level).

**Why:** Out-of-scope expression roots can evaluate unexpectedly and cause silent logic drift in conditions and computed values.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    if: ${{ steps.prep.outputs.ok == 'true' }}  # ERROR: "steps" not allowed here
    steps:
      - run: echo ok
```

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    if: ${{ matrix.os == 'ubuntu-24.04' }}     # ERROR: "matrix" not allowed here
    steps:
      - run: echo ok
```

**Remediation:** Use only the context variables available at the expression's scope:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    if: ${{ github.ref == 'refs/heads/main' }}
    steps:
      - id: prep
        run: echo "ok=true" >> "$GITHUB_OUTPUT"
      - if: ${{ steps.prep.outputs.ok == 'true' }}
        run: echo ok
```

**When fixing:**

- Move each expression to a scope where the referenced context root is valid (workflow/job/step).
- Verify differences between `matrix`, `steps`, and `needs` availability after refactoring.
- Re-test both expected true and false paths for guarded steps.

**Configuration:**

- `assume-events`: Assume additional trigger events during expression validation to reduce event-context false positives.
- See: [configuration.md#expr-undefined-varassume-events](configuration.md#expr-undefined-varassume-events)

---

### `cache-poisoning-trigger`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `actions/cache` is used in workflows that accept untrusted triggers (`pull_request`, `pull_request_target`, `workflow_run`). An attacker can write a poisoned cache entry that affects later privileged runs.

**Why:** Shared writable caches across trust boundaries allow untrusted runs to persist artifacts that later trusted jobs consume.

**Example trigger:**

```yaml
on: pull_request
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/cache@v4       # WARNING: cache on untrusted trigger
        with:
          path: ~/.npm
          key: npm-${{ runner.os }}
```

**Remediation:** Prevent attackers from poisoning shared cache entries. Common approaches:

- Move cacheable jobs to trusted triggers only (`push`, `merge_group`)
- Use `actions/cache/restore` (read-only) on untrusted triggers so forks cannot write entries
- Namespace cache keys by trust boundary (`pr-${{ github.event.number }}` vs `main-`)

```yaml
# Approach A: restrict cache to trusted triggers
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/cache@v4
        with:
          path: ~/.npm
          key: npm-${{ runner.os }}
```

```yaml
# Approach B: read-only cache on untrusted trigger
on: pull_request
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/cache/restore@v4
        with:
          path: ~/.npm
          key: npm-${{ runner.os }}
```

**When fixing:**

- Prefer strict trust-boundary separation: trusted runs write, untrusted runs restore-only.
- Namespace cache keys so PR and protected-branch keys cannot collide.
- Confirm cache hit-rate and performance after segmentation to avoid accidental regressions.

**Configuration:**

- `untrusted-triggers`: Add trigger events treated as untrusted by this rule (additive to built-in defaults).
- See: [configuration.md#cache-poisoning-triggeruntrusted-triggers](configuration.md#cache-poisoning-triggeruntrusted-triggers)

---

### `self-hosted-runner-trigger`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when self-hosted runners are used in workflows with untrusted triggers. Compromised host isolation can expose long-lived credentials and filesystem state.

**Why:** Self-hosted runners are persistent infrastructure, so untrusted workload execution increases lateral-movement and credential-exfiltration risk.

**Example trigger:**

```yaml
on: pull_request
jobs:
  build:
    runs-on: self-hosted              # WARNING: self-hosted on untrusted trigger
    steps:
      - run: echo ok
```

**Remediation:** Prevent untrusted code from running on persistent infrastructure. Common approaches:

- Route untrusted trigger paths to ephemeral GitHub-hosted runners
- Restrict triggers so forks cannot reach self-hosted runners (`push` only, or `pull_request` with branch filter for internal repos)
- Use ephemeral/just-in-time self-hosted runners that are destroyed after each job
- Gate self-hosted jobs with environment protection rules

```yaml
# Approach A: switch to GitHub-hosted runner for untrusted triggers
on: pull_request
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

```yaml
# Approach B: gate self-hosted with environment protection
on: pull_request_target
jobs:
  deploy:
    runs-on: self-hosted
    environment: production
    if: ${{ github.event.pull_request.head.repo.full_name == github.repository }}
    steps:
      - run: echo ok
```

**When fixing:**

- Moving to GitHub-hosted runners can change tooling/network assumptions; validate environment parity.
- `if` guards are defense-in-depth, not a substitute for strong runner isolation.
- For unavoidable self-hosted use, combine trigger restrictions with ephemeral runner lifecycle controls.

**Configuration:**

- `untrusted-triggers`: Add trigger events treated as untrusted by this rule (additive to built-in defaults).
- See: [configuration.md#self-hosted-runner-triggeruntrusted-triggers](configuration.md#self-hosted-runner-triggeruntrusted-triggers)

---

### `insecure-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects unsafe command construction from untrusted inputs in `run` scripts.

**Why:** Insecure command channels and untrusted command construction can reopen deprecated command-injection vectors.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    env:
      ACTIONS_ALLOW_UNSECURE_COMMANDS: true  # ERROR: insecure commands enabled
    steps:
      - run: echo "insecure commands are enabled"
```

**Remediation:** Remove `ACTIONS_ALLOW_UNSECURE_COMMANDS` and migrate to environment files:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "/usr/local/custom-bin" >> "$GITHUB_PATH"
```

**When fixing:**

- Remove `ACTIONS_ALLOW_UNSECURE_COMMANDS` at all scopes (workflow/job/step) to prevent inherited bypasses.
- Migrate scripts to environment-file APIs (`$GITHUB_OUTPUT`, `$GITHUB_ENV`, `$GITHUB_PATH`).
- Re-test custom/composite actions that may still assume legacy command behavior.

---

## Permissions & Secrets

---

### `deny-write-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `write-all`.

**Why:** `write-all` grants broad write access and maximizes impact if `GITHUB_TOKEN` or derived credentials are abused.

**Example trigger:**

```yaml
on: push
permissions: write-all             # ERROR: write-all is forbidden
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:** Replace `write-all` with an explicit minimal scope map:

```yaml
on: push
permissions:
  contents: read
  packages: write
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

**When fixing:**

- Auto-fix replaces `write-all` with `permissions: {}` as a safe baseline.
- Expect follow-up failures until required scopes are re-added explicitly.
- Reintroduce scopes incrementally per failing action/job.

---

### `deny-read-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `read-all`. Explicit least-privilege scope declarations must be used.

**Why:** Even read-only global scopes can expose excessive repository metadata and content beyond job requirements.

**Example trigger:**

```yaml
on: push
permissions: read-all               # ERROR: read-all is too broad
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ng
```

**Remediation:** Replace `read-all` with an explicit scope map:

```yaml
on: push
permissions:
  contents: read
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: echo ok
```

**When fixing:**

- Replace scalar defaults with explicit minimal scopes and verify each job.
- Jobs relying on implicit reads (for example checkout/package metadata) may fail until scopes are restored.

---

### `job-permissions-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Warns when a job omits an explicit `permissions:` declaration. Without explicit permissions the job inherits potentially broad defaults.

When auto-fix is enabled, the fix infers minimum required permission scopes from known popular actions used in the job's steps (e.g. `actions/checkout` requires `contents: read`). If multiple actions require the same scope, the highest access level wins (write > read). When no known action requirements are found, the fix inserts `permissions: {}`.

**Why:** Implicit permission inheritance hides effective access and makes least-privilege review harder.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    # WARNING: no permissions declared
    steps:
      - run: echo ng
```

**Remediation:** Add an explicit `permissions:` map to every job:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v6
```

If the job uses only actions without known permission requirements, `permissions: {}` is inserted:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    permissions: {}
    steps:
      - run: echo ok
```

**When fixing:**

- Inferred scopes are catalog-based; custom/unknown actions still need manual permission tuning.
- `permissions: {}` is intentionally strict and may break jobs until scopes are added.
- Validate reusable workflow calls against callee token expectations after adding explicit permissions.

---

### `credentials`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when custom or private registry images are used in `job.container` or `job.services.*` without a `credentials` block.

**Why:** Private image pulls without explicit credential handling either fail at runtime or encourage unsafe hardcoded secrets.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    container:
      image: registry.example.com/team/app:1.0.0  # ERROR: no credentials
    services:
      db:
        image: private.example.org/team/db:15     # ERROR: no credentials
    steps:
      - run: echo ng
```

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    container:
      image: example.com/owner/image
      credentials:
        username: user
        password: pass             # ERROR: hardcoded password
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    container:
      image: registry.example.com/team/app:1.0.0
      credentials:
        username: ${{ secrets.REG_USER }}
        password: ${{ secrets.REG_PASS }}
    steps:
      - run: echo ok
```

**Configuration:**

- `public-registries`: Add registry hosts treated as public / credential-optional (additive to built-in defaults).
- See: [configuration.md#credentialspublic-registries](configuration.md#credentialspublic-registries)

---

### `checkout-persist-credentials`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Warns when `actions/checkout` is used without `persist-credentials: false`.

Legacy `actions/checkout` versions persist credentials in `.git/config`; `actions/checkout@v6+` stores them in a separate file under `$RUNNER_TEMP`. Either way, leaving credentials persisted broadens later-step and artifact exposure.

**Why:** Persisted credentials can be consumed by later commands or leaked via unsafe artifact paths, expanding credential exposure.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2; WARNING: should set persist-credentials to false
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
        with:
          persist-credentials: false
```

**When fixing:**

- Review later authenticated git commands; for example, `git push` may require explicit auth setup such as `git remote set-url origin <url>` or `gh auth setup-git`.
- Auto-fix inserts/replaces deterministic scalar values only; expression-valued cases are manual.
- Review `artipacked` findings together when artifact upload paths are broad.

---

### `workflow-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when workflow-level `env` assigns `secrets.*` or `github.token` values in multi-job workflows. Secrets scoped this broadly are available to all jobs, including those that do not need them.

**Why:** Workflow-level secret assignment fans out sensitive values to unrelated jobs, violating least-privilege boundaries.

**Example trigger:**

```yaml
on: push
env:
  GITHUB_TOKEN: ${{ github.token }}         # ERROR: exposed to all jobs
  DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - run: npm test              # does not need secrets
  deploy:
    runs-on: ubuntu-24.04
    steps:
      - run: ./deploy.sh           # needs GITHUB_TOKEN
```

**Remediation:** Move secret assignments to the minimal job or step scope:

```yaml
on: push
jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - run: npm test
  deploy:
    runs-on: ubuntu-24.04
    steps:
      - env:
          GITHUB_TOKEN: ${{ github.token }}
        run: ./deploy.sh
```

**When fixing:**

- Moving secrets to narrower scopes can break jobs that implicitly depended on broad workflow `env`.
- Prefer step-local `env` for one-command usage; use job-level only when multiple steps truly need the same secret.
- Re-validate fork/PR behavior after scope changes because secret availability may differ by event.

---

### `job-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when job-level `env` assigns `secrets.*` or `github.token` values in jobs with multiple steps.

**Why:** Job-level secret scoping exposes sensitive values to every step, even when only one step needs them.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    env:
      GITHUB_TOKEN: ${{ github.token }}     # ERROR: exposed to all steps
    steps:
      - run: npm test               # does not need token
      - run: ./publish.sh           # needs token
```

**Remediation:** Move secret assignments to the specific step that requires them:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - run: npm test
      - env:
          GITHUB_TOKEN: ${{ github.token }}
        run: ./publish.sh
```

**When fixing:**

- Narrowing to step-level `env` requires checking all later steps that previously read the same variable.
- Keep secret-bearing steps minimal and avoid passing secret values via intermediate files/artifacts.
- Re-run publish/deploy paths to confirm required credentials are still present where needed.

---

### `unredacted-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secret-derived environment variables appear to be printed via output commands (`echo`, `printf`, `Write-Host`, `Write-Output`). GitHub masking is not guaranteed for transformed or derived secret values.

**Why:** Printed secret derivatives can bypass masking heuristics and persist in logs, summaries, and external log sinks.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    env:
      TOKEN: ${{ secrets.GITHUB_TOKEN }}
    steps:
      - run: echo "${TOKEN}"     # WARNING: secret-derived variable printed
```

**Remediation:** Avoid printing secret values. If debugging is needed, use `::add-mask`:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          echo "::add-mask::${TOKEN}"
          # use $TOKEN in commands without printing it
```

**When fixing:**

- Mask before any potential output; masking after printing is ineffective.
- Avoid printing transformed/partial secret values, not only raw secret strings.
- If debugging is required, log presence/length/state flags instead of values.

**Configuration:**

- `output-commands`: Add commands watched as output sinks for secret-printing detection (additive to built-in defaults).
- See: [configuration.md#unredacted-secretsoutput-commands](configuration.md#unredacted-secretsoutput-commands)

---

### `secrets-outside-env`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `secrets.*` appears in `if` conditions, `uses:` references, or reusable-call input values instead of a controlled `env:` handoff.

**Why:** Using secrets outside controlled `env` handoff broadens exposure surfaces and makes secret flow harder to audit.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - if: ${{ secrets.GITHUB_TOKEN != '' }}   # ERROR: secrets in step.if
        run: echo ng
```

**Remediation:** Move secret access to explicit `env:` mapping at the minimal scope needed:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          if [ -n "$TOKEN" ]; then echo ok; fi
```

**When fixing:**

- Move secret access to the narrowest scope that still satisfies runtime needs.
- Re-check condition semantics after migration because expression-time and shell-time evaluation differ.
- Avoid passing secrets through `uses:` inputs unless the called action contract explicitly requires it.

---

### `overprovisioned-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secrets are mapped at a broader scope (workflow or job) than is required. Enforces least-privilege secret handoff boundaries.

**Why:** Over-broad secret mappings increase blast radius and accidental disclosure probability across unrelated steps/jobs.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
          API_KEY: ${{ secrets.API_KEY }}
          SECRET_KEY: ${{ secrets.SECRET_KEY }}
          PRIVATE_KEY: ${{ secrets.PRIVATE_KEY }}
          APP_ID: ${{ secrets.APP_ID }}
          DEPLOY_KEY: ${{ secrets.DEPLOY_KEY }}  # ERROR: more than 5 secrets
        run: echo ng
```

**Remediation:** Restrict secret mapping to the minimum required:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: echo "Step 1 only needs TOKEN"
      - env:
          API_KEY: ${{ secrets.API_KEY }}
        run: echo "Step 2 only needs API_KEY"
```

**When fixing:**

- Split mappings by step responsibility so each step receives only required secrets.
- Watch for hidden coupling where multiple scripts assumed a single shared secret namespace.
- Verify no secrets are re-exported into broader scopes (`job.env`, artifacts, cache keys).

---

### `deny-inherit-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when a reusable workflow call job uses `secrets: inherit`. Full secret inheritance propagates all secrets across workflow boundaries without explicit declaration.

**Why:** `secrets: inherit` bypasses explicit contract boundaries and can leak unnecessary secrets into called workflows.

**Example trigger:**

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1
    secrets: inherit          # ERROR: full secret inheritance is forbidden
```

**Remediation:** Map only the required secrets explicitly:

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1
    secrets:
      token: ${{ secrets.GITHUB_TOKEN }}
```

**When fixing:**

- Enumerate only required secret keys based on the callee workflow contract.
- After replacing `inherit`, test all called workflow paths to catch missing secret mappings early.
- Keep secret key names explicit and stable to make cross-workflow review auditable.

---

## Supply Chain

---

### `unpinned-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ (✓ with `--enable-pin-network`) |

Warns when `uses:` references are not pinned to a full 40-character commit SHA. Mutable refs (`@v4`, `@main`) can be silently updated by the action maintainer.

**Why:** Mutable refs weaken supply-chain integrity because the referenced code can change without any workflow diff.

**Example trigger:**

```yaml
- uses: actions/checkout@v6       # WARNING: not SHA-pinned
- uses: actions/checkout@main     # WARNING: not SHA-pinned
```

**Remediation:** Pin to the commit SHA and retain the version as a comment:

```yaml
- uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
```

Use `seiton --fix --enable-pin-network` to automatically resolve and apply SHA pins.

**When fixing:**

- Network remediation requires connectivity/API quota and is more reliable with `GITHUB_TOKEN` set.
- When the same unpinned action appears in multiple steps or jobs, `--fix --enable-pin-network` pins each occurrence independently.
- If fix edits from different rules overlap, Seiton applies non-conflicting fixes first and retries remaining fixes on the updated file. If a fix still cannot be applied, the command reports conflicting `rule-id` values and stops on that file.
- Verify resolved SHAs match the intended release line and policy before merge.
- Keep exceptions explicit via `ignore-actions` so mutable-ref usage remains auditable.

**Configuration:**

- `ignore-actions`: Define action patterns excluded from SHA-pinning checks (`owner` required, `refs` optional).
- See: [configuration.md#unpinned-usesignore-actions](configuration.md#unpinned-usesignore-actions)

---

### `unpinned-image`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ (✓ with `--enable-image-network`) |

Warns when container image references in `docker://`, `job.container.image`, or `job.services.*.image` are not pinned by digest.

**Why:** Tag-based image refs are mutable and can resolve to different image contents over time, reducing reproducibility.

**Example trigger:**

```yaml
container:
  image: ubuntu:22.04    # WARNING: not digest-pinned
```

**Remediation:**

```yaml
container:
  image: ubuntu@sha256:a6d2f...
```

**When fixing:**

- Network digest resolution may fail under registry rate limits or missing private-registry credentials.
- Re-test jobs after pinning because digest changes can alter packages and runtime behavior.
- Maintain a periodic digest refresh process to avoid stale pins.
- Tagless refs (for example `image: redis`) are treated as the `latest` tag. By default, `fix.images.exclude-tags` includes `latest`, so `--fix --enable-image-network` does not pin them. Use an explicit tag (for example `redis:7`) or set `fix.images.exclude-tags: []` in config.
- When pinning is skipped by config, the diagnostic `help:` line explains why (for example `pinning skipped: tag 'latest' matches fix.images.exclude-tags`).

---

### `unpinned-tools`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when known tool-setup actions rely on an unpinned tool version. The list of known actions is maintained in `data/sources/unpinned-tools/unpinned_tools.json` and currently covers `aquasecurity/setup-trivy` in both workflow steps and composite action steps. To add a new action, edit the JSON and run `dotnet run --project src/Seiton.Update -- sync-unpinned-tools`.

**Why:** Floating tool versions make scan/build outputs non-reproducible and can introduce unexpected behavior changes.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: aquasecurity/setup-trivy@v0.2.0
```

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: aquasecurity/setup-trivy@v0.2.0
        with:
          version: latest
```

**Remediation:** Pin `with.version` to a concrete tool version:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: aquasecurity/setup-trivy@v0.2.0
        with:
          version: v0.51.2
```

Dynamic values such as `version: ${{ inputs.trivy-version }}` are also warned because they may resolve to an unpinned latest channel.

---

### `archived-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `uses:` references point to GitHub-archived repositories. Archived repositories are read-only and no longer receive security fixes.

**Why:** Archived dependencies are effectively unmaintained and accumulate unpatched vulnerabilities.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions-rs/toolchain@v1   # WARNING: repository is archived
```

**Remediation:** Replace with an actively maintained alternative:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: dtolnay/rust-toolchain@stable
```

---

### `ref-version-mismatch`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a version annotation or comment does not match the resolved commit's lineage. Prevents misleading provenance narratives.

**Why:** Version/lineage mismatches undermine auditability and can mislead incident response and supply-chain review.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: owner/action-v1@v2.0.0   # ERROR: path hint 'v1' mismatches ref 'v2'
```

**Remediation:** Align the version annotation with the actual pinned SHA:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: owner/action-v2@v2.1.0
```

---

### `forbidden-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors or warns (per policy) when `uses:` references violate configured allow/deny patterns.

**Why:** Policy-driven allow/deny enforcement is a primary control against risky or unapproved third-party actions.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: bad-org/unsafe-action@v1   # ERROR: denied by policy
```

**Remediation:** Replace with an allowed action, or add an explicit exception:

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: approved-org/safe-action@v1
```

**Configuration:**

- `deny` / `allow`: Define wildcard policy patterns for disallowed and explicitly allowed `uses:` references.
- See: [configuration.md#forbidden-usesdeny--allow](configuration.md#forbidden-usesdeny--allow)

---

### `github-app-token-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when `actions/create-github-app-token` is invoked without permission-limiting inputs, or when `owner`-scoped token issuance omits `repositories` to constrain the installation scope.

**Why:** Over-broad app token issuance can grant unintended cross-repository write capabilities.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      # ERROR: missing permission constraints
      - uses: actions/create-github-app-token@v2
```

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      # ERROR: owner set without repositories constraint
      - uses: actions/create-github-app-token@v2
        with:
          owner: ${{ github.repository_owner }}
          permission-issues: write
```

**Remediation:**

```yaml
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/create-github-app-token@v2
        with:
          app-id: ${{ secrets.APP_ID }}
          private-key: ${{ secrets.APP_PRIVATE_KEY }}
          repositories: repo-a,repo-b
          permission-contents: read
```

---

### `job-timeout-minutes-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when executable jobs omit `timeout-minutes`. Prevents runaway jobs from consuming unlimited runner time.

**Why:** Missing timeouts allow hung jobs to consume runner capacity indefinitely and delay other pipelines.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    # ERROR: missing timeout-minutes
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    timeout-minutes: 15
    steps:
      - run: echo ok
```

**When fixing:**

- Auto-fix is attached only when `fix.defaults.job-timeout-minutes` is configured.
- Choose timeout values per workload type; one global value may be too strict or too loose.
- Monitor new timeouts to tune false cancellations.

Auto-fix is available when `fix.defaults.job-timeout-minutes` is set in [configuration](configuration.md).
When this default is not configured, diagnostics include a help hint showing the exact config key to add.

---

### `use-trusted-publishing`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a publishing or release workflow uses long-lived credentials instead of a trusted OIDC/provenance-based publishing flow.

**Why:** Long-lived publish credentials expand breach impact; trusted publishing reduces secret lifetime and provenance ambiguity.

**Example trigger:**

```yaml
on: push
jobs:
  publish:
    runs-on: ubuntu-24.04
    steps:
      - run: npm publish             # WARNING: no id-token permission
```

```yaml
on: push
jobs:
  publish:
    runs-on: ubuntu-24.04
    steps:
      - run: twine upload dist/*     # WARNING: use trusted publishing
```

**Remediation:** Adopt trusted publishing with OIDC:

```yaml
on: push
jobs:
  publish:
    permissions:
      id-token: write
    runs-on: ubuntu-24.04
    steps:
      - run: npm publish
```

---

## Online (opt-in)

These rules require a GitHub API token and network access. Enable them manually:

```yaml
# seiton.yaml
rules:
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
```

---

### `known-vulnerable-actions`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Errors when `uses:` references resolve to action versions listed in known vulnerability advisory data.

**Why:** Known-vulnerable action versions represent documented exploit paths and should be removed from the pipeline trust chain.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/example@v1   # ERROR: known vulnerable version
```

**Remediation:** Upgrade to the fixed release line or pin to a non-vulnerable commit.

---

### `impostor-commit`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Errors when a SHA-pinned `uses:` reference points to a commit that is not reachable in the referenced repository's expected history. Detects ghost or impostor commit supply-chain abuse.

**Why:** Unreachable SHAs can indicate impostor commit attacks that bypass normal tag/branch trust assumptions.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@abc1234567890abc1234567890abc1234567890a
        # ERROR: commit is not reachable in the repository
```

**Remediation:** Replace with a verified commit from the trusted tag/release mapping.

---

### `ref-confusion`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Errors when a symbolic ref (tag or branch name) in `uses:` is ambiguous — the same name exists in both refs/tags and refs/heads.

**Why:** Ambiguous refs create non-deterministic resolution and weaken provenance guarantees for reviewed workflows.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: owner/action@v2      # ERROR: ambiguous ref (tag and branch both exist)
```

**Remediation:** Use a full SHA pin, or enforce ref-namespace disambiguation policy.

---

### `stale-action-refs`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Warns when a SHA-pinned `uses:` reference is stale relative to the maintained release/tag mapping.

**Why:** Stale pins miss upstream fixes and security patches, increasing long-term supply-chain exposure.

**Remediation:** Update the pinned SHA to the current approved SHA for the intended release family.
