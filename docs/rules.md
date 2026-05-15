# Rules

This page documents all lint rules included in Seiton.

> **Tip:** Run `seiton rules` to see all rules and their effective enabled/disabled status in your terminal. Use `seiton rules --format json` for machine-readable output. See [Usage](usage.md#seiton-rules) for details.

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
runner-no-latest                         yes       local    warning    no    both       default
run-secrets-context-direct-use           yes       local    error      yes   both       default
run-inputs-context-direct-use            yes       local    error      yes   both       default
secrets-whole-context-access             yes       local    error      no    both       default
checkout-persist-credentials             yes       local    warning    yes   both       default
deny-read-all                            yes       local    error      yes   both       default
deny-inherit-secrets                     yes       local    error      no    both       default
job-timeout-minutes-required             yes       local    error      yes   both       default
github-app-token-inputs                  yes       local    error      no    both       default
cache-poisoning                          yes       local    warning    no    both       default
self-hosted-runner                       yes       local    warning    no    both       default
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
known-vulnerable-actions                 no        online   error      no    workflow   opt-in (not configured)
impostor-commit                          no        online   error      no    workflow   opt-in (not configured)
ref-confusion                            no        online   error      no    workflow   opt-in (not configured)
stale-action-refs                        no        online   warning    no    workflow   opt-in (not configured)

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
- [concurrency-limits](#concurrency-limits)
- [deprecated-commands](#deprecated-commands)

### Security

- [template-injection](#template-injection)
- [dangerous-triggers](#dangerous-triggers)
- [run-env-context-direct-use](#run-env-context-direct-use)
- [run-secrets-context-direct-use](#run-secrets-context-direct-use)
- [run-inputs-context-direct-use](#run-inputs-context-direct-use)
- [secrets-whole-context-access](#secrets-whole-context-access)
- [expr-undefined-var](#expr-undefined-var)
- [cache-poisoning](#cache-poisoning)
- [self-hosted-runner](#self-hosted-runner)
- [insecure-commands](#insecure-commands)

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
    uses: owner/repo/.github/workflows/reuse.yml@main
    steps:                  # ERROR: cannot have both uses and steps
      - run: echo hello
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

---

### `reusable-workflow`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates reusable workflow call semantics. `with` and `secrets` are only valid under a `uses` job. Reusable-call jobs must not contain incompatible execution keys (`steps`, `container`, `services`, etc.).

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    with:                   # ERROR: key 'with' requires uses
      target: prod
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@main
    container: node:20      # ERROR: incompatible with uses
```

**Remediation:**

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@main
    with:
      target: prod
```

---

### `permissions`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `permissions` values. Scalar must be `read-all` or `write-all`. Per-scope values must be `read`, `write`, or `none`. Even when valid, scalar permissions (`read-all`, `write-all`) emit a warning because explicit per-scope mapping is preferred.

**Example trigger:**

```yaml
on: push
permissions: admin-all              # ERROR: must be 'read-all' or 'write-all'
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

```yaml
on: push
permissions: read-all               # WARNING: overly broad; prefer explicit per-scope mapping
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    permissions:
      contents: admin              # ERROR: must be 'read', 'write', or 'none'
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - run: echo ng

---
# After (job-level explicit scopes)
on: push
jobs:
  build:
    permissions:
      contents: read
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

---

### `needs-graph`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates the job dependency graph. Errors on unknown dependency targets and circular dependencies.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    needs: nonexistent       # ERROR: references unknown job
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  a:
    needs: b                 # ERROR: cyclic dependencies detected
    runs-on: ubuntu-latest
    steps:
      - run: echo a
  b:
    needs: a
    runs-on: ubuntu-latest
    steps:
      - run: echo b
```

**Remediation:**

```yaml
on: push
jobs:
  setup:
    runs-on: ubuntu-latest
    steps:
      - run: echo setup
  build:
    needs: setup
    runs-on: ubuntu-latest
    steps:
      - run: echo build
```

---

### `shell-name`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates shell names in workflow/job defaults and `run` steps. Reports shells outside the supported set for the target platform.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo hello
        shell: zsh             # ERROR: invalid shell name
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo hello
        shell: cmd             # ERROR: cmd is not available on ubuntu
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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

**Example trigger:**

```yaml
on: push
jobs:
  1build:                   # ERROR: must start with a letter or _
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - id: setup.v1          # ERROR: invalid step ID
        run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - id: setup-v1
        run: echo ok
```

---

### `glob-pattern`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates glob syntax in event trigger filters. Reports invalid patterns and incompatible filter combinations (`branches` vs `branches-ignore`, `paths` vs `paths-ignore`, `tags` vs `tags-ignore`).

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

For self-hosted runners, add their labels to `rules.runner-label.known-hosted-labels.extend` in [configuration](configuration.md).

---

### `runner-no-latest`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when moving `*-latest` runner labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used. These labels silently change the underlying runner when GitHub releases a new version.

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

---

### `popular-action-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Validates input names for well-known popular actions. Reports unknown input keys that are likely typos.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depht: 1      # ERROR: typo; did you mean 'fetch-depth'?
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
        with:
          fetch-depth: 1
```

---

### `action-shell-is-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Requires an explicit `shell` declaration on composite action `run` steps. Applies only to action-metadata files.

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

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    strategy:
      matrix:
        node: [10, 12, 14]
        os: [ubuntu-latest, macos-latest]
        exclude:
          - node: 13        # ERROR: value 13 does not match matrix combinations
            os: ubuntu-latest
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        os: [ubuntu-latest, windows-latest]
        node: [20]
        exclude:
          - node: 20
            os: windows-latest
    steps:
      - run: echo ok
```

---

### `env-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on risky environment variable naming and usage patterns across workflow, job, and step scopes.

**Example trigger:**

```yaml
on: push
env:
  github_token: x           # ERROR: not portable (lowercase)
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN-NAME: x     # ERROR: not portable (contains dash)
        run: echo ng
```

**Remediation:**

```yaml
on: push
env:
  GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN_NAME: x
        run: echo ok
```

---

### `if-cond`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on malformed, constant, or unsound `if` conditions. Reports always-true / always-false conditions and context misuse.

**Example trigger:**

```yaml
jobs:
  build:
    if: ${{ false }}        # ERROR: constant expression in condition
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
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

**Example trigger:**

```yaml
jobs:
  build:
    # ERROR: fake ternary pattern
    if: ${{ github.ref_name == 'main' && 'prod' || 'dev' }}
    runs-on: ubuntu-latest
    steps:
      - if: ${{ inputs.deploy && 'yes' || 'no' }}
        run: echo ng
```

**Remediation:** Use explicit `if`-based branching or GitHub Actions' native conditional:

```yaml
jobs:
  build:
    if: ${{ github.ref_name == 'main' }}
    runs-on: ubuntu-latest
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

**Example trigger:**

```yaml
jobs:
  build:
    if: github.ref != 'refs/heads/main'   # WARNING: missing ${{ }} wrapper
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - if: ${{ github.event_name == 'push' }}
        run: echo ok
      - if: ${{ !cancelled() }}
        run: echo ok
```

> **Note:** Bare `true`, `false`, `always()`, `failure()`, `cancelled()`, `success()` literals are intentionally excluded from this rule since GitHub Actions handles them natively.

---

### `concurrency-limits`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | — | ✗ |

Warns when workflows or jobs lack `concurrency` settings with explicit `cancel-in-progress`. Without concurrency limits, parallel runs can waste resources and cause race conditions.

**Example trigger:**

```yaml
on: push
# WARNING: workflow does not declare concurrency
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

> **Note:** Reusable-only workflows (`on: workflow_call`) and workflow-call jobs (`uses:`) are skipped. When workflow-level concurrency is set, job-level checks are suppressed.

---

### `deprecated-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects deprecated workflow command syntax (`::set-output`, `::save-state`, `::add-path`, `::set-env`) in `run` scripts. These commands are blocked or unsafe on modern runners.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "::set-output name=digest::$DIGEST"
        # ERROR: workflow command "set-output" was deprecated
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "result=ok" >> "$GITHUB_OUTPUT"
```

---

## Security

---

### `template-injection`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Detects unsafe direct interpolation of untrusted `github.event`-origin data into `run` script sinks. Using `${{ github.event.* }}` directly in a script can allow attackers to inject arbitrary shell commands through PR titles, comments, or labels.

**Example trigger:**

```yaml
- run: echo "PR title: ${{ github.event.pull_request.title }}"
  # ERROR: pull_request.title is user-controlled
```

**Remediation:** Map untrusted values into environment variables first, then reference them as `$VAR` in the script:

```yaml
- env:
    PR_TITLE: ${{ github.event.pull_request.title }}
  run: echo "PR title: $PR_TITLE"
```

---

### `dangerous-triggers`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when high-risk trigger events (`pull_request_target`, `workflow_run`, etc.) are used. These events execute with elevated repository context and write permissions.

**Example trigger:**

```yaml
on: pull_request_target    # WARNING: potentially dangerous trigger
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

Restrict the trigger scope, add strict `if` condition guards, or replace with a safer event (`pull_request` without `_target`).

**Configuration — extend the dangerous-events set:**

```yaml
rules:
  dangerous-triggers:
    events:
      extend:
        - issue_comment
```

---

### `run-env-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ env.* }}` is directly interpolated inside a `run` script. Shell variable expansion (`$VAR` / `$env:VAR`) must be used instead.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "${{ env.VERSION }}"  # ERROR: use $VERSION instead
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "$VERSION"
```

Replace `${{ env.VAR }}` with `$VAR` (bash/sh) or `$env:VAR` (PowerShell).

---

### `run-secrets-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ secrets.* }}` is directly interpolated inside a `run` script. Secrets should be mapped via `env:` and referenced through shell variables.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: curl -H "Authorization: Bearer ${{ secrets.TOKEN }}"
        # ERROR: use env: indirection
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN: ${{ secrets.TOKEN }}
        run: curl -H "Authorization: Bearer $TOKEN"
```

---

### `run-inputs-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ inputs.* }}` or `${{ github.event.inputs.* }}` are directly interpolated inside a `run` script. Inputs may be user-controlled.

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
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - env:
          BENCHMARK: ${{ inputs.benchmark }}
        run: echo "$BENCHMARK"
```

---

### `secrets-whole-context-access`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when an expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`). This leaks all secrets simultaneously.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "${{ toJson(secrets) }}"  # ERROR: exposes all secrets
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          MY_SECRET: ${{ secrets.MY_SECRET }}
        run: some-command --token "$MY_SECRET"
```

---

### `expr-undefined-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when expressions reference context roots unavailable in the current scope (e.g. `steps.*` at job level).

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    if: ${{ steps.prep.outputs.ok == 'true' }}  # ERROR: "steps" not allowed here
    steps:
      - run: echo ok
```

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    if: ${{ matrix.os == 'ubuntu-latest' }}     # ERROR: "matrix" not allowed here
    steps:
      - run: echo ok
```

**Remediation:** Use only the context variables available at the expression's scope:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    if: ${{ github.ref == 'refs/heads/main' }}
    steps:
      - id: prep
        run: echo "ok=true" >> "$GITHUB_OUTPUT"
      - if: ${{ steps.prep.outputs.ok == 'true' }}
        run: echo ok
```

---

### `cache-poisoning`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `actions/cache` is used in workflows that accept untrusted triggers (`pull_request`, `pull_request_target`, `workflow_run`). An attacker can write a poisoned cache entry that affects later privileged runs.

**Example trigger:**

```yaml
on: pull_request
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/cache@v4       # WARNING: cache on untrusted trigger
        with:
          path: ~/.npm
          key: npm-${{ runner.os }}
```

**Remediation:** Split trusted and untrusted jobs. Namespace cache keys by trust boundary:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/cache@v4
        with:
          path: ~/.npm
          key: npm-${{ runner.os }}
```

**Configuration — extend untrusted triggers:**

```yaml
rules:
  cache-poisoning:
    untrusted-triggers:
      extend:
        - issue_comment
```

---

### `self-hosted-runner`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when self-hosted runners are used in workflows with untrusted triggers. Compromised host isolation can expose long-lived credentials and filesystem state.

**Example trigger:**

```yaml
on: pull_request
jobs:
  build:
    runs-on: self-hosted              # WARNING: self-hosted on untrusted trigger
    steps:
      - run: echo ok
```

**Remediation:** Route untrusted trigger paths to ephemeral GitHub-hosted runners:

```yaml
on: pull_request
jobs:
  build:
    runs-on: ubuntu-latest              # use GitHub-hosted runner
    steps:
      - run: echo ok
```

---

### `insecure-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects unsafe command construction from untrusted inputs in `run` scripts.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      ACTIONS_ALLOW_UNSECURE_COMMANDS: true  # ERROR: insecure commands enabled
    steps:
      - run: echo ng
```

**Remediation:** Remove `ACTIONS_ALLOW_UNSECURE_COMMANDS` and migrate to environment files:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo "MY_VAR=value" >> "$GITHUB_ENV"
```

---

## Permissions & Secrets

---

### `deny-write-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `write-all`.

**Example trigger:**

```yaml
on: push
permissions: write-all             # ERROR: write-all is forbidden
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

---

### `deny-read-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `read-all`. Explicit least-privilege scope declarations must be used.

**Example trigger:**

```yaml
on: push
permissions: read-all               # ERROR: read-all is too broad
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - run: echo ok
```

---

### `job-permissions-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Warns when a job omits an explicit `permissions:` declaration. Without explicit permissions the job inherits potentially broad defaults.

When auto-fix is enabled, the fix infers minimum required permission scopes from known popular actions used in the job's steps (e.g. `actions/checkout` requires `contents: read`). If multiple actions require the same scope, the highest access level wins (write > read). When no known action requirements are found, the fix inserts `permissions: {}`.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    # WARNING: no permissions declared
    steps:
      - run: echo ng
```

**Remediation:** Add an explicit `permissions:` map to every job:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    permissions: {}
    steps:
      - run: echo ok
```

---

### `credentials`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when custom or private registry images are used in `job.container` or `job.services.*` without a `credentials` block.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    container:
      image: registry.example.com/team/app:1.0.0
      credentials:
        username: ${{ secrets.REG_USER }}
        password: ${{ secrets.REG_PASS }}
    steps:
      - run: echo ok
```

**Configuration — extend public registries:**

```yaml
rules:
  credentials:
    public-registries:
      extend:
        - registry.example.com
```

---

### `checkout-persist-credentials`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Warns when `actions/checkout` is used without `persist-credentials: false`. Persisting credentials in `.git/config` increases secret exposure risk.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6  # ERROR: should set persist-credentials to false
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
        with:
          persist-credentials: false
```

---

### `workflow-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when workflow-level `env` assigns `secrets.*` or `github.token` values in multi-job workflows. Secrets scoped this broadly are available to all jobs, including those that do not need them.

**Example trigger:**

```yaml
on: push
env:
  GITHUB_TOKEN: ${{ github.token }}         # ERROR: exposed to all jobs
  DATADOG_API_KEY: ${{ secrets.DATADOG_API_KEY }}
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - run: echo a
  b:
    runs-on: ubuntu-latest
    steps:
      - run: echo b
```

**Remediation:** Move secret assignments to the minimal job or step scope:

```yaml
on: push
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - env:
          GITHUB_TOKEN: ${{ github.token }}
        run: echo a
  b:
    runs-on: ubuntu-latest
    steps:
      - run: echo b
```

---

### `job-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when job-level `env` assigns `secrets.*` or `github.token` values in jobs with multiple steps.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    env:
      GITHUB_TOKEN: ${{ github.token }}     # ERROR: exposed to all steps
    steps:
      - run: echo first
      - run: echo second
```

**Remediation:** Move secret assignments to the specific step that requires them:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          GITHUB_TOKEN: ${{ github.token }}
        run: echo first
      - run: echo second
```

---

### `unredacted-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secret-derived environment variables appear to be printed via output commands (`echo`, `printf`, `Write-Host`, `Write-Output`). GitHub masking is not guaranteed for transformed or derived secret values.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          echo "::add-mask::${TOKEN}"
          # use $TOKEN in commands without printing it
```

**Configuration — extend output commands:**

```yaml
rules:
  unredacted-secrets:
    output-commands:
      extend:
        - tee
```

---

### `secrets-outside-env`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `secrets.*` appears in `if` conditions, `uses:` references, or reusable-call input values instead of a controlled `env:` handoff.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - if: ${{ secrets.GITHUB_TOKEN != '' }}   # ERROR: secrets in step.if
        run: echo ng
```

**Remediation:** Move secret access to explicit `env:` mapping at the minimal scope needed:

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          if [ -n "$TOKEN" ]; then echo ok; fi
```

---

### `overprovisioned-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secrets are mapped at a broader scope (workflow or job) than is required. Enforces least-privilege secret handoff boundaries.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - env:
          TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: echo "Step 1 only needs TOKEN"
      - env:
          API_KEY: ${{ secrets.API_KEY }}
        run: echo "Step 2 only needs API_KEY"
```

---

### `deny-inherit-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when a reusable workflow call job uses `secrets: inherit`. Full secret inheritance propagates all secrets across workflow boundaries without explicit declaration.

**Example trigger:**

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@main
    secrets: inherit          # ERROR: full secret inheritance is forbidden
```

**Remediation:** Map only the required secrets explicitly:

```yaml
on: push
jobs:
  reuse:
    uses: owner/repo/.github/workflows/reuse.yml@main
    secrets:
      token: ${{ secrets.GITHUB_TOKEN }}
```

---

## Supply Chain

---

### `unpinned-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ (✓ with `--enable-pin-network`) |

Warns when `uses:` references are not pinned to a full 40-character commit SHA. Mutable refs (`@v4`, `@main`) can be silently updated by the action maintainer.

**Example trigger:**

```yaml
- uses: actions/checkout@v6       # WARNING: not SHA-pinned
- uses: actions/checkout@main     # WARNING: not SHA-pinned
```

**Remediation:** Pin to the commit SHA and retain the version as a comment:

```yaml
- uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
```

Use `seiton fix --enable-pin-network` to automatically resolve and apply SHA pins.

**Configuration — ignore specific actions:**

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - "my-org/internal-action"
      - "my-org/setup-*"
```

Patterns use wildcard matching (`*` = any sequence, `?` = single character) against `owner/repo`.

---

### `unpinned-image`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ (✓ with `--enable-image-network`) |

Warns when container image references in `docker://`, `job.container.image`, or `job.services.*.image` are not pinned by digest.

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

---

### `archived-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `uses:` references point to GitHub-archived repositories. Archived repositories are read-only and no longer receive security fixes.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions-rs/toolchain@v1   # WARNING: repository is archived
```

**Remediation:** Replace with an actively maintained alternative:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: dtolnay/rust-toolchain@stable
```

---

### `ref-version-mismatch`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a version annotation or comment does not match the resolved commit's lineage. Prevents misleading provenance narratives.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: owner/action-v1@v2.0.0   # ERROR: path hint 'v1' mismatches ref 'v2'
```

**Remediation:** Align the version annotation with the actual pinned SHA:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: owner/action-v2@v2.1.0
```

---

### `forbidden-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors or warns (per policy) when `uses:` references violate configured allow/deny patterns.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: bad-org/unsafe-action@v1   # ERROR: denied by policy
```

**Remediation:** Replace with an allowed action, or add an explicit exception:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: approved-org/safe-action@v1
```

**Configuration:**

```yaml
rules:
  forbidden-uses:
    deny:
      - "deprecated-org/*"
    allow:
      - "approved-org/*"
```

---

### `github-app-token-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when `actions/create-github-app-token` is invoked without permission-limiting inputs, or when `owner`-scoped token issuance omits `repositories` to constrain the installation scope.

**Example trigger:**

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      # ERROR: missing permission constraints
      - uses: actions/create-github-app-token@v2
```

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - uses: actions/create-github-app-token@v2
        with:
          repositories: repo-a,repo-b
          permission-contents: read
```

---

### `job-timeout-minutes-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when executable jobs omit `timeout-minutes`. Prevents runaway jobs from consuming unlimited runner time.

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    # ERROR: missing timeout-minutes
    steps:
      - run: echo ng
```

**Remediation:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - run: echo ok
```

Auto-fix is available when `fix.defaults.job-timeout-minutes` is set in [configuration](configuration.md).

---

### `use-trusted-publishing`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a publishing or release workflow uses long-lived credentials instead of a trusted OIDC/provenance-based publishing flow.

**Example trigger:**

```yaml
on: push
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - run: npm publish             # WARNING: no id-token permission
```

```yaml
on: push
jobs:
  publish:
    runs-on: ubuntu-latest
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
    runs-on: ubuntu-latest
    steps:
      - run: npm publish
```

---

## Online (opt-in)

These rules require a GitHub API token and network access. Enable them manually:

```yaml
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

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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

**Example trigger:**

```yaml
on: push
jobs:
  build:
    runs-on: ubuntu-latest
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

**Remediation:** Update the pinned SHA to the current approved SHA for the intended release family.
