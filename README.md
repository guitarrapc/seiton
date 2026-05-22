# seiton

[![build](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml)

[Installation](docs/installation.md) | [Usage](docs/usage.md) | [Rules](docs/rules.md) | [Configuration](docs/configuration.md) | [Overview](docs/index.md) | [Playground](https://guitarrapc.github.io/seiton/)

**Seiton** is a security-focused linter & fixer for [GitHub Actions](https://github.com/features/actions) workflow files and action metadata files.
It catches security issues, policy violations, and mistakes before they reach production — then optionally fixes them. Try it out in the [playground](https://guitarrapc.github.io/seiton/).

Features:

- **Security-first rules** — template injection, unpinned actions/images, dangerous triggers, secret misuse, and more.
- **Correctness checks** — job structure, needs-graph cycles, glob syntax, shell names, ID naming, expression type-checking.
- **Supply-chain hygiene** — unpinned `uses:`, archived actions, known vulnerable actions (online), impostor commits (online).
- **Auto-fix support** — `seiton --fix` applies machine-safe remediations in place (including network-assisted SHA/digest pinning).
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

Install Seiton using your preferred method. See [Installation](docs/installation.md) for prebuilt binaries, Docker, and build-from-source details.

```sh
# Homebrew (macOS/Linux)
brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton
brew install seiton

# Windows (Scoop)
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install seiton
```

Then run it:

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

To generate a starter config file:

```sh
seiton init
```

If you prefer a direct download instead of a package manager, use the release archive or the download script described in [Installation](docs/installation.md).

## Documentation

| Page | What it covers |
|---|---|
| [Installation](docs/installation.md) | Package managers, prebuilt binaries, Docker, attestation verification, and building from source. |
| [Usage](docs/usage.md) | Commands, flags, environment variables, output formats, CI examples, and exit codes. |
| [Rules](docs/rules.md) | Canonical rule reference with behavior, examples, remediation, and configuration notes. |
| [Configuration](docs/configuration.md) | Config file discovery, schema, exclusions, fix settings, and network options. |
| [Overview](docs/index.md) | Product overview and comparison with actionlint, zizmor, ghalint, frizbee, and pinact. |

## Rule Coverage

Seiton ships 50+ rules across correctness, security, permissions/secrets, and supply-chain hygiene. The README intentionally keeps this summary short; use [Rules](docs/rules.md) for the canonical catalog and `seiton rules` to inspect the effective enabled/disabled state in your environment.

## Tool Positioning

Seiton is best used as the main GitHub Actions linter when you want both detection and remediation. If you specifically need `shellcheck` or `pyflakes` integration, add actionlint alongside it. For broader comparison and trade-offs, see [Overview](docs/index.md#comparison-with-other-tools).

## License

Seiton is distributed under the [MIT license](./LICENSE.md).
