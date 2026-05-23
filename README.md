# seiton

[![build](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml)

**Seiton** is a security-focused linter & fixer for [GitHub Actions](https://github.com/features/actions) workflow files and action metadata files.
It catches security issues, policy violations, and mistakes before they reach production — then optionally fixes them. Try it out in the [playground](https://guitarrapc.github.io/seiton/).

Features:

- **Security-first rules** — template injection, unpinned actions/images, dangerous triggers, secret misuse, and more.
- **Correctness checks** — job structure, needs-graph cycles, glob syntax, shell names, ID naming, expression type-checking.
- **Supply-chain hygiene** — unpinned `uses:`, archived actions, known vulnerable actions (online), impostor commits (online).
- **Auto-fix support** — `seiton fix` or `seiton --fix` applies machine-safe remediations in place (including network-assisted SHA/digest pinning).
- **Multiple output formats** — `text` (default), `json`, `sarif` (GitHub Advanced Security).
- **Config file** — optional `.github/seiton.yaml` for rule tuning, exclusions, and network options.
- **Inline suppression** — `# seiton: disable-next-line <rule-id>` directives inside workflow files.
- **NativeAOT binary** — single-file executable; no .NET runtime required at deployment.

You can check various benchmark patterns at [GitHub Actions/Benchmark](https://github.com/guitarrapc/seiton/actions/runs/26144683540).

**Example of broken workflow:**

```yaml
on:
  push:
    branch: main
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "${{ github.event.pull_request.title }}"
      - uses: actions/checkout@v6
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
```

**Seiton reports errors:**

```
test.yml:8:7: [error] template-injection: untrusted value 'github.event.pull_request.title' interpolated directly into run script; use env: indirection instead
test.yml:9:5: [warning] unpinned-uses: 'actions/checkout@v6' is not pinned to a full commit SHA
test.yml:12:9: [error] popular-action-inputs: input 'node_version' is not defined in action 'actions/setup-node@v4'; did you mean 'node-version'?
test.yml:9:5: [warning] checkout-persist-credentials: 'actions/checkout' should set 'persist-credentials: false'
test.yml:6:5: [warning] job-permissions-required: job 'test' does not declare explicit permissions
```

## Quick Start

Install or download Seiton (see [Installation](docs/installation.md) for all options):

```sh
# Download script (macOS/Linux, downloads to the current directory)
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash

# Homebrew (macOS/Linux)
brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton
brew install seiton

# Windows (Scoop)
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install seiton
```

If you used the download script, run `./seiton` in the commands below from the download directory, or move the binary into your `PATH`. For other platforms, download a prebuilt archive from the [releases page](https://github.com/guitarrapc/seiton/releases) and add `seiton` to your `PATH` (see [Installation](docs/installation.md)).

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

# List all available rules and their status
seiton rules
```

## Documents

| Page | Description |
|---|---|
| [Installation](docs/installation.md) | How to install Seiton on Windows, macOS, and Linux. |
| [Usage](docs/usage.md) | Commands, flags, environment variables, CI integration. |
| [Rules](docs/rules.md) | Full list of all lint rules with examples and remediation guidance. |
| [Configuration](docs/configuration.md) | Config file format, rule tuning, exclusions, and network options. |

## Rules

Seiton includes **50+ rules** across four categories. Each rule has a dedicated documentation page with examples, rationale, and configuration options.

Seiton keeps rules that detect mistakes, security risks, incompatibilities, spec traps, or strongly deprecated behavior with concrete downside.
Opt-in informational rules are accepted only when that downside is still specific and explainable.
Style-only checks, naming preferences, UI readability preferences, and alternative-tool preferences are out of scope.

> See the [full rules list](docs/rules.md) for a summary table. Click any rule name below for detailed documentation.

### Correctness

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [job-structure](docs/rules.md#job-structure) | ✓ | ✗ | `uses` is mutually exclusive with `steps`/`runs-on` |
| [reusable-workflow](docs/rules.md#reusable-workflow) | ✓ | ✗ | Reusable workflow call semantics |
| [permissions](docs/rules.md#permissions) | ✓ | ✗ | Invalid permission scope values |
| [needs-graph](docs/rules.md#needs-graph) | ✓ | ✗ | Unknown dependency targets and cycles |
| [shell-name](docs/rules.md#shell-name) | ✓ | ✗ | Unsupported shell names |
| [id-naming](docs/rules.md#id-naming) | ✓ | ✗ | Invalid characters in job/step IDs |
| [glob-pattern](docs/rules.md#glob-pattern) | ✓ | ✗ | Invalid glob syntax and filter conflicts |
| [runner-label](docs/rules.md#runner-label) | ✓ | ✗ | Unknown GitHub-hosted runner labels |
| [runner-no-latest](docs/rules.md#runner-no-latest) | ✓ | ✗ | Moving `*-latest` runner labels |
| [popular-action-inputs](docs/rules.md#popular-action-inputs) | ✓ | △ | Typos in popular action input names |
| [outdated-action-runner](docs/rules.md#outdated-action-runner) | ✓ | ✗ | Deprecated action `runs.using` runtimes |
| [local-action-inputs](docs/rules.md#local-action-inputs) | ✓ | ✗ | Local action metadata contract validation |
| [action-shell-is-required](docs/rules.md#action-shell-is-required) | ✓ | ✗ | Missing `shell` in composite action `run` steps |
| [matrix](docs/rules.md#matrix) | ✓ | ✗ | Invalid matrix definitions |
| [env-var](docs/rules.md#env-var) | ✓ | ✗ | Risky environment variable patterns |
| [if-cond](docs/rules.md#if-cond) | ✓ | ✗ | Constant or unsound `if` conditions |
| [fake-ternary](docs/rules.md#fake-ternary) | ✓ | ✗ | `cond && a \|\| b` fake ternary idioms |
| [unsound-condition](docs/rules.md#unsound-condition) | ✓ | △ | Block-scalar `if:` conditions that become truthy because of trailing newline |
| [concurrency-limits](docs/rules.md#concurrency-limits) | ✗ | ✗ | Missing concurrency settings with `cancel-in-progress` |
| [deprecated-commands](docs/rules.md#deprecated-commands) | ✓ | ✗ | Deprecated workflow commands |
| [dispatch-inputs](docs/rules.md#dispatch-inputs) | ✓ | ✗ | Invalid `workflow_dispatch` input definitions |
| [schedule-event](docs/rules.md#schedule-event) | ✓ | ✗ | Invalid schedule cron/timezone |
| [workflow-call-input-default](docs/rules.md#workflow-call-input-default) | ✓ | ✗ | Invalid `workflow_call` input defaults |

### Security

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [template-injection](docs/rules.md#template-injection) | ✓ | △ | Untrusted data in `run` scripts |
| [dangerous-triggers](docs/rules.md#dangerous-triggers) | ✓ | ✗ | High-risk trigger events |
| [unsound-contains](docs/rules.md#unsound-contains) | ✓ | ✗ | Bypassable `contains()` checks in conditions |
| [bot-conditions](docs/rules.md#bot-conditions) | ✓ | ✗ | Spoofable bot actor checks |
| [run-env-context-direct-use](docs/rules.md#run-env-context-direct-use) | ✓ | △ | `${{ env.* }}` in `run` scripts |
| [run-secrets-context-direct-use](docs/rules.md#run-secrets-context-direct-use) | ✓ | △ | `${{ secrets.* }}` in `run` scripts |
| [run-inputs-context-direct-use](docs/rules.md#run-inputs-context-direct-use) | ✓ | △ | `${{ inputs.* }}` in `run` scripts |
| [secrets-whole-context-access](docs/rules.md#secrets-whole-context-access) | ✓ | ✗ | `toJson(secrets)` whole-context leaks |
| [expr-undefined-var](docs/rules.md#expr-undefined-var) | ✓ | ✗ | Out-of-scope context references |
| [cache-poisoning](docs/rules.md#cache-poisoning) | ✓ | ✗ | Cache usage with untrusted triggers |
| [self-hosted-runner](docs/rules.md#self-hosted-runner) | ✓ | ✗ | Self-hosted runners with untrusted triggers |
| [insecure-commands](docs/rules.md#insecure-commands) | ✓ | ✗ | Unsafe command construction |

### Permissions & Secrets

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [deny-write-all](docs/rules.md#deny-write-all) | ✓ | ✓ | `write-all` permissions |
| [deny-read-all](docs/rules.md#deny-read-all) | ✓ | ✓ | `read-all` permissions |
| [job-permissions-required](docs/rules.md#job-permissions-required) | ✓ | ✓ | Missing job-level permissions |
| [credentials](docs/rules.md#credentials) | ✓ | ✗ | Missing container registry credentials |
| [checkout-persist-credentials](docs/rules.md#checkout-persist-credentials) | ✓ | △ | `actions/checkout` persist-credentials |
| [artipacked](docs/rules.md#artipacked) | ✓ | ✗ | Checkout + dangerous upload-artifact credential leak |
| [workflow-secrets](docs/rules.md#workflow-secrets) | ✓ | ✗ | Workflow-level secret assignments |
| [job-secrets](docs/rules.md#job-secrets) | ✓ | ✗ | Job-level secret assignments |
| [unredacted-secrets](docs/rules.md#unredacted-secrets) | ✓ | ✗ | Printing secret-derived values |
| [secrets-outside-env](docs/rules.md#secrets-outside-env) | ✓ | ✗ | Secrets outside `env:` context |
| [overprovisioned-secrets](docs/rules.md#overprovisioned-secrets) | ✓ | ✗ | Broad-scoped secret mappings |
| [deny-inherit-secrets](docs/rules.md#deny-inherit-secrets) | ✓ | ✗ | `secrets: inherit` in reusable calls |

### Supply Chain

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [unpinned-uses](docs/rules.md#unpinned-uses) | ✓ | △ | Actions not pinned to commit SHA |
| [unpinned-image](docs/rules.md#unpinned-image) | ✓ | △ | Images not pinned by digest |
| [unpinned-tools](docs/rules.md#unpinned-tools) | ✓ | ✗ | Tool setup actions with unpinned external tool version |
| [archived-uses](docs/rules.md#archived-uses) | ✓ | ✗ | Archived repository references |
| [ref-version-mismatch](docs/rules.md#ref-version-mismatch) | ✓ | ✗ | Version annotation mismatch |
| [forbidden-uses](docs/rules.md#forbidden-uses) | ✓ | ✗ | Policy-denied action references |
| [github-app-token-inputs](docs/rules.md#github-app-token-inputs) | ✓ | ✗ | Unprivileged GitHub App token inputs |
| [job-timeout-minutes-required](docs/rules.md#job-timeout-minutes-required) | ✓ | △ | Missing job timeout |
| [use-trusted-publishing](docs/rules.md#use-trusted-publishing) | ✓ | ✗ | Long-lived publish credentials |

### Online (opt-in)

| Rule | Default | Auto-fix | Description |
|---|---|---|---|
| [known-vulnerable-actions](docs/rules.md#known-vulnerable-actions) | ✗ | ✗ | Known vulnerability advisory matches |
| [impostor-commit](docs/rules.md#impostor-commit) | ✗ | ✗ | Ghost/impostor commit detection |
| [ref-confusion](docs/rules.md#ref-confusion) | ✗ | ✗ | Tag/branch name ambiguity |
| [stale-action-refs](docs/rules.md#stale-action-refs) | ✗ | ✗ | Outdated SHA pins |

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
