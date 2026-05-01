# Checks

This page documents all lint rules included in Seiton.

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
jobs:
  build:
    uses: org/shared/.github/workflows/build.yml@main
    runs-on: ubuntu-latest  # ERROR: incompatible with uses
    steps:                  # ERROR: incompatible with uses
      - run: echo hello
```

**Remediation:** Remove `steps` / `runs-on` from reusable-call jobs. Add them only to executable jobs.

---

### `reusable-workflow`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates reusable workflow call semantics. `with` and `secrets` are only valid under a `uses` job. Reusable-call jobs must not contain incompatible execution keys (`steps`, `container`, `services`, etc.).

**Example trigger:**

```yaml
jobs:
  deploy:
    with:               # ERROR: with requires uses
      env: production
```

**Remediation:** Add `uses:` to the job when passing `with`/`secrets`. Remove incompatible execution keys from reusable-call jobs.

---

### `permissions`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `permissions` values. Scalar must be `read-all` or `write-all`. Per-scope values must be `read`, `write`, or `none`.

**Example trigger:**

```yaml
permissions:
  contents: admin   # ERROR: invalid value
```

**Remediation:** Use `read`, `write`, or `none` for each scope. `read-all` and `write-all` are also accepted but seiton warn against their use in favor of explicit scopes.

---

### `needs-graph`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates the job dependency graph. Errors on unknown dependency targets and circular dependencies.

**Example trigger:**

```yaml
jobs:
  deploy:
    needs: [build, test, missing-job]   # ERROR: missing-job does not exist
```

**Remediation:** Fix the `needs` list to reference existing job IDs. Redesign to eliminate circular dependencies.

---

### `shell-name`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates shell names in workflow/job defaults and `run` steps. Reports shells outside the supported set for the target platform.

**Example trigger:**

```yaml
steps:
  - run: echo hello
    shell: fish    # ERROR: unsupported shell
```

**Remediation:** Use a supported shell name (`bash`, `sh`, `pwsh`, `powershell`, `cmd`, `python`).

---

### `id-naming`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Validates `job.id` and `step.id` values. IDs must use only alphanumeric characters, hyphens, and underscores.

**Example trigger:**

```yaml
jobs:
  "my job":    # ERROR: spaces not allowed in job ID
```

**Remediation:** Use slug-style identifiers (e.g. `my-job`). Update all `needs`, `steps.<id>`, and expression references after renaming.

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
      - "**[invalid"    # ERROR: invalid glob syntax
    branches-ignore:    # ERROR: incompatible with branches
      - develop
```

**Remediation:** Fix the glob syntax. Do not combine `branches` and `branches-ignore` in the same event.

---

### `runner-label`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on unknown GitHub-hosted runner labels in `runs-on`. Self-hosted labels and expression-only values are excluded.

**Example trigger:**

```yaml
runs-on: ubuntu-9999    # WARNING: unknown label
```

**Remediation:** Use a known GitHub-hosted label. For self-hosted runners, add their labels to `rules.runner-label.known-hosted-labels.extend` in [configuration](configuration.md).

---

### `runner-no-latest`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when moving `*-latest` runner labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used. These labels silently change the underlying runner when GitHub releases a new version.

**Example trigger:**

```yaml
runs-on: ubuntu-latest    # WARNING: prefer explicit version
```

**Remediation:** Use explicit versioned labels such as `ubuntu-24.04`.

---

### `popular-action-inputs`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Validates input names for well-known popular actions. Reports unknown input keys that are likely typos.

**Example trigger:**

```yaml
- uses: actions/checkout@v4
  with:
    fetch-depht: 1    # ERROR: typo; correct key is fetch-depth
```

**Remediation:** Fix the input name to match the action's documented keys.

---

### `action-shell-is-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Requires an explicit `shell` declaration on composite action `run` steps. Applies only to action-metadata files.

**Example trigger (action.yml):**

```yaml
runs:
  using: composite
  steps:
    - run: echo hello
      # ERROR: shell is required for composite action run steps
```

**Remediation:** Add `shell: bash` (or your target shell) to every `run` step in composite actions.

---

### `matrix`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Validates `strategy.matrix` definitions. Reports inconsistent keys, invalid `include`/`exclude` shapes, and suspicious expansion patterns.

**Remediation:** Normalize matrix axes and `include`/`exclude` rules. Test expected expansion counts.

---

### `env-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on risky environment variable naming and usage patterns across workflow, job, and step scopes.

**Remediation:** Use stable uppercase snake-case names and minimize the scope at which environment values are declared.

---

### `if-cond`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns on malformed, constant, or unsound `if` conditions. Reports always-true / always-false conditions and context misuse.

**Remediation:** Rewrite with explicit boolean intent and scope-valid contexts.

---

### `fake-ternary`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `cond && a || b` fake ternary idioms are used in expression-bearing fields. This pattern has different semantics from a true ternary when `a` is falsy.

**Remediation:** Use explicit `if`-based branching instead of short-circuit ternary emulation.

---

### `deprecated-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects deprecated workflow command syntax (`::set-output`, `::save-state`, `::add-path`, `::set-env`) in `run` scripts. These commands are blocked or unsafe on modern runners.

**Example trigger:**

```yaml
- run: echo "::set-output name=digest::$DIGEST"
```

**Remediation:** Replace with `$GITHUB_OUTPUT`, `$GITHUB_STATE`, `$GITHUB_PATH`, `$GITHUB_ENV` file mechanisms.

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
on:
  pull_request_target:    # WARNING: dangerous trigger
```

**Remediation:** Restrict the trigger scope, add strict `if` condition guards, or replace with a safer event (`pull_request` without `_target`).

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
- run: echo "${{ env.VERSION }}"
  # ERROR: use $VERSION instead
```

**Remediation:** Replace `${{ env.VAR }}` with `$VAR` (bash/sh) or `$env:VAR` (PowerShell).

---

### `run-secrets-context-direct-use`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when `${{ secrets.* }}` is directly interpolated inside a `run` script. Secrets should be mapped via `env:` and referenced through shell variables.

**Example trigger:**

```yaml
- run: curl -H "Authorization: Bearer ${{ secrets.TOKEN }}"
  # ERROR: use env: indirection
```

**Remediation:**

```yaml
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

**Remediation:** Map via `env:` and validate/normalize the value before use in the script body.

---

### `secrets-whole-context-access`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when an expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`). This leaks all secrets simultaneously.

**Example trigger:**

```yaml
- run: echo "${{ toJson(secrets) }}"
  # ERROR: exposes all secrets
```

**Remediation:** Access only the specific secret key needed: `${{ secrets.MY_SECRET }}`.

---

### `expr-undefined-var`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when expressions reference context roots unavailable in the current scope (e.g. `steps.*` at job level).

**Remediation:** Use only the context variables available at the expression's scope.

---

### `cache-poisoning`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when `actions/cache` is used in workflows that accept untrusted triggers (`pull_request`, `pull_request_target`, `workflow_run`). An attacker can write a poisoned cache entry that affects later privileged runs.

**Remediation:** Split trusted and untrusted jobs. Namespace cache keys by trust boundary and avoid broad `restore-keys` fallback patterns.

---

### `self-hosted-runner`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when self-hosted runners are used in workflows with untrusted triggers. Compromised host isolation can expose long-lived credentials and filesystem state.

**Remediation:** Add strict `if` guards, isolate runner groups, and route untrusted trigger paths to ephemeral GitHub-hosted runners.

---

### `insecure-commands`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Detects unsafe command construction from untrusted inputs in `run` scripts.

**Remediation:** Use argument-safe invocation, strict quoting, and allowlist validation.

---

## Permissions & Secrets

---

### `deny-write-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `write-all`. This rule cannot be disabled.

**Remediation:** Replace `write-all` with `read-all` or an explicit minimal scope map.

---

### `deny-read-all`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Errors when workflow or job permissions are set to `read-all`. Explicit least-privilege scope declarations must be used.

**Remediation:** Replace `read-all` with an explicit scope map such as `contents: read`.

---

### `job-permissions-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✓ |

Warns when a job omits an explicit `permissions:` declaration. Without explicit permissions the job inherits potentially broad defaults.

**Remediation:** Add an explicit `permissions:` map to every job, using the minimum required scopes.

---

### `credentials`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when custom or private registry images are used in `job.container` or `job.services.*` without a `credentials` block.

**Remediation:** Add a `credentials:` block with `username` and `password` fields. Alternatively, move to an approved public registry.

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

**Remediation:**

```yaml
- uses: actions/checkout@v4
  with:
    persist-credentials: false
```

---

### `workflow-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when workflow-level `env` assigns `secrets.*` or `github.token` values in multi-job workflows. Secrets scoped this broadly are available to all jobs, including those that do not need them.

**Remediation:** Move secret assignments from workflow-level `env` to the minimal job or step scope that actually requires them.

---

### `job-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when job-level `env` assigns `secrets.*` or `github.token` values in jobs with multiple steps.

**Remediation:** Move secret assignments from job-level `env` to the specific step that requires them.

---

### `unredacted-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secret-derived environment variables appear to be printed via output commands (`echo`, `printf`, `Write-Host`, `Write-Output`). GitHub masking is not guaranteed for transformed or derived secret values.

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

**Remediation:** Move secret access to explicit `env:` mapping at the minimal scope needed.

---

### `overprovisioned-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when secrets are mapped at a broader scope (workflow or job) than is required. Enforces least-privilege secret handoff boundaries.

**Remediation:** Restrict secret mapping to the minimum execution unit that actually consumes the value.

---

### `deny-inherit-secrets`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors when a reusable workflow call job uses `secrets: inherit`. Full secret inheritance propagates all secrets across workflow boundaries without explicit declaration.

**Remediation:** Map only the required secrets explicitly:

```yaml
jobs:
  call:
    uses: org/repo/.github/workflows/shared.yml@main
    secrets:
      MY_SECRET: ${{ secrets.MY_SECRET }}
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
- uses: actions/checkout@v4       # WARNING: not SHA-pinned
- uses: actions/checkout@main     # WARNING: not SHA-pinned
```

**Remediation:** Pin to the commit SHA and retain the version as a comment:

```yaml
- uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683  # v4.2.2
```

Use `seiton fix --enable-pin-network` to automatically resolve and apply SHA pins.

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

**Remediation:** Replace with an actively maintained alternative, or maintain a governed fork.

---

### `ref-version-mismatch`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a version annotation or comment does not match the resolved commit's lineage. Prevents misleading provenance narratives.

**Remediation:** Align the version annotation with the actual pinned SHA.

---

### `forbidden-uses`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Errors or warns (per policy) when `uses:` references violate configured allow/deny patterns.

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

**Remediation:** Add `permissions` or `permission-*` inputs. If `owner` is set, also set `repositories`.

---

### `job-timeout-minutes-required`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | △ |

Errors when executable jobs omit `timeout-minutes`. Prevents runaway jobs from consuming unlimited runner time.

**Remediation:** Add `timeout-minutes:` to each job. Auto-fix is available when `fix.defaults.job-timeout-minutes` is set in [configuration](configuration.md).

---

### `use-trusted-publishing`

| Default | Network | Auto-fix |
|---|---|---|
| ✓ | — | ✗ |

Warns when a publishing or release workflow uses long-lived credentials instead of a trusted OIDC/provenance-based publishing flow.

**Remediation:** Adopt trusted publishing (e.g. PyPI Trusted Publishers, npm provenance) and remove long-lived publish secrets.

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

**Remediation:** Upgrade to the fixed release line or pin to a non-vulnerable commit.

---

### `impostor-commit`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Errors when a SHA-pinned `uses:` reference points to a commit that is not reachable in the referenced repository's expected history. Detects ghost or impostor commit supply-chain abuse.

**Remediation:** Replace with a verified commit from the trusted tag/release mapping.

---

### `ref-confusion`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Errors when a symbolic ref (tag or branch name) in `uses:` is ambiguous — the same name exists in both refs/tags and refs/heads.

**Remediation:** Use a full SHA pin, or enforce ref-namespace disambiguation policy.

---

### `stale-action-refs`

| Default | Network | Auto-fix |
|---|---|---|
| ✗ | online | ✗ |

Warns when a SHA-pinned `uses:` reference is stale relative to the maintained release/tag mapping.

**Remediation:** Update the pinned SHA to the current approved SHA for the intended release family.
