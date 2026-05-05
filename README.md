# seiton

**Seiton** is a security-focused linter & fixer for [GitHub Actions](https://github.com/features/actions) workflow files and action metadata files.
It catches security issues, policy violations, and mistakes before they reach production — then optionally fixes them.

Features:

- **Security-first rules** — template injection, unpinned actions/images, dangerous triggers, secret misuse, and more.
- **Correctness checks** — job structure, needs-graph cycles, glob syntax, shell names, ID naming, expression type-checking.
- **Supply-chain hygiene** — unpinned `uses:`, archived actions, known vulnerable actions (online), impostor commits (online).
- **Auto-fix support** — `seiton fix` or `seiton --fix` applies machine-safe remediations in place (including network-assisted SHA/digest pinning).
- **Multiple output formats** — `text` (default), `json`, `sarif` (GitHub Advanced Security).
- **Config file** — optional `.github/seiton.yaml` for rule tuning, exclusions, and network options.
- **Inline suppression** — `# seiton: disable-next-line <rule-id>` directives inside workflow files.
- **NativeAOT binary** — single-file executable; no .NET runtime required at deployment.

You can check various benchmark patterns at [GitHub Actions/Benchmark](https://github.com/guitarrapc/seiton/actions/runs/25385637566).

**Example of broken workflow:**

```yaml
on:
  push:
    branch: main
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "Title: ${{ github.event.pull_request.title }}"
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
```

**Seiton reports errors:**

```
test.yml:8:7: [error] template-injection: untrusted value 'github.event.pull_request.title' interpolated directly into run script; use env: indirection instead
test.yml:9:5: [warning] unpinned-uses: action 'actions/checkout@v4' is not pinned to a full commit SHA
test.yml:12:9: [error] popular-action-inputs: input 'node_version' is not defined in action 'actions/setup-node@v4'; did you mean 'node-version'?
test.yml:9:5: [warning] checkout-persist-credentials: 'actions/checkout' should set 'persist-credentials: false'
test.yml:6:5: [warning] job-permissions-required: job 'test' does not declare explicit permissions
```

## Quick Start

Install Seiton (see [Installation](docs/installation.md) for all options):

```sh
# Homebrew (macOS/Linux)
brew install guitarrapc/tap/seiton

# Windows
winget install guitarrapc.seiton

# Or download the latest release
curl -L https://github.com/guitarrapc/seiton/releases/latest/download/install.sh | sh
```

Run it:

```sh
# Lint all workflow files in the current repository
seiton

# Lint specific files
seiton .github/workflows/ci.yml action.yml

# See all issues as JSON
seiton --format json

# Apply auto-fixes
seiton --fix
```

## Documents

| Page | Description |
|---|---|
| [Installation](docs/installation.md) | How to install Seiton on Windows, macOS, and Linux. |
| [Usage](docs/usage.md) | Commands, flags, environment variables, CI integration. |
| [Checks](docs/checks.md) | Full list of all lint rules with examples and remediation guidance. |
| [Configuration](docs/configuration.md) | Config file format, rule tuning, exclusions, and network options. |

## Checks

Seiton includes **50+ rules** across four categories. Each rule has a dedicated documentation page with examples, rationale, and configuration options.

> See the [full checks list](docs/checks.md) for a summary table. Click any rule name below for detailed documentation.

### Correctness

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [job-structure](docs/checks.md#job-structure) | ✓ | ✗ | `uses` is mutually exclusive with `steps`/`runs-on` |
| [reusable-workflow](docs/checks.md#reusable-workflow) | ✓ | ✗ | Reusable workflow call semantics |
| [permissions](docs/checks.md#permissions) | ✓ | ✗ | Invalid permission scope values |
| [needs-graph](docs/checks.md#needs-graph) | ✓ | ✗ | Unknown dependency targets and cycles |
| [shell-name](docs/checks.md#shell-name) | ✓ | ✗ | Unsupported shell names |
| [id-naming](docs/checks.md#id-naming) | ✓ | ✗ | Invalid characters in job/step IDs |
| [glob-pattern](docs/checks.md#glob-pattern) | ✓ | ✗ | Invalid glob syntax and filter conflicts |
| [runner-label](docs/checks.md#runner-label) | ✓ | ✗ | Unknown GitHub-hosted runner labels |
| [runner-no-latest](docs/checks.md#runner-no-latest) | ✓ | ✗ | Moving `*-latest` runner labels |
| [popular-action-inputs](docs/checks.md#popular-action-inputs) | ✓ | △ | Typos in popular action input names |
| [outdated-action-runner](docs/checks.md#outdated-action-runner) | ✓ | ✗ | Deprecated action `runs.using` runtimes |
| [local-action-inputs](docs/checks.md#local-action-inputs) | ✓ | ✗ | Local action metadata contract validation |
| [action-shell-is-required](docs/checks.md#action-shell-is-required) | ✓ | ✗ | Missing `shell` in composite action `run` steps |
| [matrix](docs/checks.md#matrix) | ✓ | ✗ | Invalid matrix definitions |
| [env-var](docs/checks.md#env-var) | ✓ | ✗ | Risky environment variable patterns |
| [if-cond](docs/checks.md#if-cond) | ✓ | ✗ | Constant or unsound `if` conditions |
| [fake-ternary](docs/checks.md#fake-ternary) | ✓ | ✗ | `cond && a \|\| b` fake ternary idioms |
| [deprecated-commands](docs/checks.md#deprecated-commands) | ✓ | ✗ | Deprecated workflow commands |
| [dispatch-inputs](docs/checks.md#dispatch-inputs) | ✓ | ✗ | Invalid `workflow_dispatch` input definitions |
| [schedule-event](docs/checks.md#schedule-event) | ✓ | ✗ | Invalid schedule cron/timezone |
| [workflow-call-input-default](docs/checks.md#workflow-call-input-default) | ✓ | ✗ | Invalid `workflow_call` input defaults |

### Security

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [template-injection](docs/checks.md#template-injection) | ✓ | △ | Untrusted data in `run` scripts |
| [dangerous-triggers](docs/checks.md#dangerous-triggers) | ✓ | ✗ | High-risk trigger events |
| [run-env-context-direct-use](docs/checks.md#run-env-context-direct-use) | ✓ | △ | `${{ env.* }}` in `run` scripts |
| [run-secrets-context-direct-use](docs/checks.md#run-secrets-context-direct-use) | ✓ | △ | `${{ secrets.* }}` in `run` scripts |
| [run-inputs-context-direct-use](docs/checks.md#run-inputs-context-direct-use) | ✓ | △ | `${{ inputs.* }}` in `run` scripts |
| [secrets-whole-context-access](docs/checks.md#secrets-whole-context-access) | ✓ | ✗ | `toJson(secrets)` whole-context leaks |
| [expr-undefined-var](docs/checks.md#expr-undefined-var) | ✓ | ✗ | Out-of-scope context references |
| [cache-poisoning](docs/checks.md#cache-poisoning) | ✓ | ✗ | Cache usage with untrusted triggers |
| [self-hosted-runner](docs/checks.md#self-hosted-runner) | ✓ | ✗ | Self-hosted runners with untrusted triggers |
| [insecure-commands](docs/checks.md#insecure-commands) | ✓ | ✗ | Unsafe command construction |

### Permissions & Secrets

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [deny-write-all](docs/checks.md#deny-write-all) | ✓ | ✓ | `write-all` permissions (cannot be disabled) |
| [deny-read-all](docs/checks.md#deny-read-all) | ✓ | ✓ | `read-all` permissions |
| [job-permissions-required](docs/checks.md#job-permissions-required) | ✓ | ✓ | Missing job-level permissions |
| [credentials](docs/checks.md#credentials) | ✓ | ✗ | Missing container registry credentials |
| [checkout-persist-credentials](docs/checks.md#checkout-persist-credentials) | ✓ | △ | `actions/checkout` persist-credentials |
| [workflow-secrets](docs/checks.md#workflow-secrets) | ✓ | ✗ | Workflow-level secret assignments |
| [job-secrets](docs/checks.md#job-secrets) | ✓ | ✗ | Job-level secret assignments |
| [unredacted-secrets](docs/checks.md#unredacted-secrets) | ✓ | ✗ | Printing secret-derived values |
| [secrets-outside-env](docs/checks.md#secrets-outside-env) | ✓ | ✗ | Secrets outside `env:` context |
| [overprovisioned-secrets](docs/checks.md#overprovisioned-secrets) | ✓ | ✗ | Broad-scoped secret mappings |
| [deny-inherit-secrets](docs/checks.md#deny-inherit-secrets) | ✓ | ✗ | `secrets: inherit` in reusable calls |

### Supply Chain

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [unpinned-uses](docs/checks.md#unpinned-uses) | ✓ | △ | Actions not pinned to commit SHA |
| [unpinned-image](docs/checks.md#unpinned-image) | ✓ | △ | Images not pinned by digest |
| [archived-uses](docs/checks.md#archived-uses) | ✓ | ✗ | Archived repository references |
| [ref-version-mismatch](docs/checks.md#ref-version-mismatch) | ✓ | ✗ | Version annotation mismatch |
| [forbidden-uses](docs/checks.md#forbidden-uses) | ✓ | ✗ | Policy-denied action references |
| [github-app-token-inputs](docs/checks.md#github-app-token-inputs) | ✓ | ✗ | Unprivileged GitHub App token inputs |
| [job-timeout-minutes-required](docs/checks.md#job-timeout-minutes-required) | ✓ | △ | Missing job timeout |
| [use-trusted-publishing](docs/checks.md#use-trusted-publishing) | ✓ | ✗ | Long-lived publish credentials |

### Online (opt-in)

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [known-vulnerable-actions](docs/checks.md#known-vulnerable-actions) | ✗ | ✗ | Known vulnerability advisory matches |
| [impostor-commit](docs/checks.md#impostor-commit) | ✗ | ✗ | Ghost/impostor commit detection |
| [ref-confusion](docs/checks.md#ref-confusion) | ✗ | ✗ | Tag/branch name ambiguity |
| [stale-action-refs](docs/checks.md#stale-action-refs) | ✗ | ✗ | Outdated SHA pins |

## Comparison with Other Tools

| Tool | Category | Primary Goal |
|---|---|---|
| **Seiton** | Lint + Fix | Static analysis (security + correctness) with integrated auto-fix |
| [actionlint](https://github.com/rhysd/actionlint) | Lint | Syntax and type correctness of workflow files |
| [zizmor](https://github.com/zizmorcore/zizmor) | Lint | Security-focused static analysis |
| [ghalint](https://github.com/suzuki-shunsuke/ghalint) | Lint | Security policy compliance (focused rule set) |
| [frizbee](https://github.com/stacklok/frizbee) | Pin / Update | Replace action/image tags with SHA checksums |
| [pinact](https://github.com/suzuki-shunsuke/pinact) | Pin / Update | Pin and update action versions; verify version annotations |

See the [full comparison](docs/index.md#comparison-with-other-tools) for detailed feature-by-feature analysis.

## License

Seiton is distributed under the [MIT license](./LICENSE.md).
