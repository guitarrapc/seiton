<a href="https://github.com/guitarrapc/seiton/releases/latest"><img src="https://img.shields.io/github/v/release/guitarrapc/seiton" alt="GitHub Release"></a>
[![build](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/seiton/actions/workflows/build.yaml)

# seiton

[Overview](docs/index.md) | [Installation](docs/installation.md) | [Usage](docs/usage.md) | [Rules](docs/rules.md) | [Configuration](docs/configuration.md) | [Playground](https://guitarrapc.github.io/seiton/)

**Seiton** is a security-focused linter & fixer for [GitHub Actions](https://github.com/features/actions) workflow files and action metadata files.
It catches security issues, policy violations, and mistakes before they reach production — then optionally fixes them. Try it out in the [playground](https://guitarrapc.github.io/seiton/).

<table align="center">
  <tr>
    <td align="center" width="50%">
      <a href="https://guitarrapc.github.io/seiton/">
        <img src="https://raw.githubusercontent.com/guitarrapc/seiton/main/assets/playground2.png" alt="Seiton playground — lint results" width="420" />
      </a>
      <br />
      <sub><b>Result</b> — diagnostics &amp; auto-fix</sub>
    </td>
    <td align="center" width="50%">
      <a href="https://guitarrapc.github.io/seiton/">
        <img src="https://raw.githubusercontent.com/guitarrapc/seiton/main/assets/playground.png" alt="Seiton playground — flow tab" width="420" />
      </a>
      <br />
      <sub><b>Flow</b> — job DAG &amp; step visualization</sub>
    </td>
  </tr>
</table>

Features:

- **Security-first rules** — template injection, unpinned actions/images, dangerous triggers, secret misuse, and more.
- **Correctness checks** — job structure, needs-graph cycles, glob syntax, shell names, ID naming, expression type-checking.
- **Supply-chain hygiene** — unpinned `uses:`, archived actions, known vulnerable actions (online), impostor commits (online).
- **Auto-fix support** — `seiton --fix` applies machine-safe remediations in place (including network-assisted SHA/digest pinning).
- **Multiple output formats** — `text` (default locally), `github-actions` (default on GitHub Actions: job summary Markdown + rich stdout), `json`, `sarif` (GitHub Advanced Security), plus `flow-json` / `flow-mermaid` for workflow structure (same contract as the Playground Flow tab).
- **Config file** — optional `.github/seiton.yaml` for rule tuning, exclusions, and network options.
- **Inline suppression** — `# seiton: disable-next-line <rule-id>` directives inside workflow files.
- **NativeAOT binary** — single-file executable; no .NET runtime required at deployment.

You can check various benchmark patterns at [GitHub Actions/Benchmark](https://github.com/guitarrapc/seiton/actions/runs/29309440727).

## Example: Fixing broken workflow

Seiton can be used with AI agents (Claude Code, GitHub Copilot, Cursor, etc.) or manually. Here's an example of the manual flow.

<details><summary>Using LLM with skills</summary>

Install the agent skill files for your preferred agent.

```sh
# Default: Install for Claude Code
seiton install --skills

# Install for GitHub Copilot
seiton install --skills --target copilot
```

Then ask your agent to fix the workflow file with `/seiton` command. For example, following prompt can be used.

```text
/seiton
Lint and fix github workflows issuee with Seiton. The workflow file has various issues, such as unpinned actions, unsafe inline expressions, and policy violations. Please run Seiton with appropriate flags/config to fix these issues, and explain the changes you made.
```

</details>

<details><summary>Manually</summary>

This example shows the full flow in 4 steps: lint, auto-fix, tune config, done.

**Step1. Start with a broken workflow**

Sample file: [`samples/readme/.github/workflows/test.yaml`](samples/readme/.github/workflows/test.yaml)

```yaml
on:
  pull_request:
    branch: main
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "title is ${{ github.event.pull_request.title }}"
      - uses: actions/checkout@v6
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
```

**Step2. Lint it**

```sh
seiton --oneline samples/readme/.github/workflows/test.yaml
```

Output shows 3 errors and 6 warnings

```sh
samples/readme/.github/workflows/test.yaml:3:5: error [syntax-check] on.pull_request has unexpected key "branch" for "pull_request" section. did you mean "branches"? expected one of "types", "branches", "branches-ignore", "paths", "paths-ignore"
samples/readme/.github/workflows/test.yaml:5:3: warning [job-permissions-required] jobs.'test' does not have permissions defined; set explicit permissions to follow least-privilege principle
samples/readme/.github/workflows/test.yaml:5:3: error [job-timeout-minutes-required] jobs.'test' should define timeout-minutes (default is 360 minutes); if not possible, set timeout-minutes on each step instead
samples/readme/.github/workflows/test.yaml:6:14: warning [runner-no-latest] jobs.'test'.runs-on label 'ubuntu-latest' is a moving latest label; prefer explicit version-pinned runner labels
samples/readme/.github/workflows/test.yaml:8:33: error [template-injection] "github.event.pull_request.title" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details
samples/readme/.github/workflows/test.yaml:9:15: warning [checkout-persist-credentials] action 'actions/checkout@v6' should set with.persist-credentials to false to avoid leaving credentials accessible to subsequent steps; after changing this, review later authenticated git commands; for example, git push may require explicit auth setup such as `git remote set-url origin <url>` or `gh auth setup-git`
samples/readme/.github/workflows/test.yaml:9:31: warning [unpinned-uses] 'actions/checkout@v6' is not pinned to a full-length commit SHA. see https://github.com/actions/checkout/tree/v6 (fixable with --fix --enable-pin-network)
samples/readme/.github/workflows/test.yaml:10:33: warning [unpinned-uses] 'actions/setup-node@v4' is not pinned to a full-length commit SHA. see https://github.com/actions/setup-node/tree/v4 (fixable with --fix --enable-pin-network)
samples/readme/.github/workflows/test.yaml:12:25: warning [popular-action-inputs] unknown input 'node_version' for action 'actions/setup-node@v4'. available inputs are "architecture", "cache", "cache-dependency-path", "check-latest", "mirror", "mirror-token", "node-version", "node-version-file", "package-manager-cache", "registry-url", "scope", "token". did you mean 'node-version'? see https://github.com/actions/setup-node/tree/v4
3 errors, 6 warnings in 1 file

| File      | Errors | Warnings |
|-----------|-------:|---------:|
| test.yaml |      3 |        6 |

| Rule                         | Count |
|------------------------------|------:|
| unpinned-uses                |     2 |
| checkout-persist-credentials |     1 |
| job-permissions-required     |     1 |
| job-timeout-minutes-required |     1 |
| popular-action-inputs        |     1 |
| runner-no-latest             |     1 |
| syntax-check                 |     1 |
| template-injection           |     1 |
```

**Step3. Apply safe auto-fixes**

```sh
seiton --fix --enable-pin-network -c samples/readme/.github/seiton.yaml samples/readme/.github/workflows/test.yaml
```

Auto-fix updates include:

- `branch` -> `branches`
- unsafe inline expression -> env var indirection
- action refs pinned to full commit SHAs
- `persist-credentials: false` added to checkout
- `node_version` -> `node-version`

```yaml
on:
  pull_request:
    branches: main
jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - run: echo "title is ${GITHUB_EVENT_PULL_REQUEST_TITLE}"
        env:
          GITHUB_EVENT_PULL_REQUEST_TITLE: ${{ github.event.pull_request.title }}
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
        with:
          persist-credentials: false
      - uses: actions/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020 # v4.4.0
        with:
          node-version: 18.x
```

At this point, only policy-level settings remain:

```text
test.yaml:5:3: error [job-timeout-minutes-required] jobs.'test' should define timeout-minutes (default is 360 minutes); if not possible, set timeout-minutes on each step instead
test.yaml:6:14: warning [runner-no-latest] jobs.'test'.runs-on label 'ubuntu-latest' is a moving latest label; prefer explicit version-pinned runner labels
| File      | Errors | Warnings |
|-----------|-------:|---------:|
| test.yaml |      1 |        1 |
```

**Step4. Tune config to resolve the remaining two diagnostics**

Generate a starter config with `seiton init`.

```shell
seiton init -c samples/readme/.github/seiton.yaml
```

Then customize [`samples/readme/.github/seiton.yaml`](samples/readme/.github/seiton.yaml):

```yaml
# .github/seiton.yaml
rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
fix:
  defaults:
    job-timeout-minutes: 30
```

Run seiton again, and all diagnostics are resolved.

```shell
seiton --fix --enable-pin-network -c samples/readme/.github/seiton.yaml samples/readme/.github/workflows/test.yaml
```

</details>

## Quick Start

**No install required:** open the [Playground](https://guitarrapc.github.io/seiton/) in your browser to lint workflow YAML and explore the interactive Flow tab (WebAssembly — your YAML is not uploaded for analysis).

Install Seiton using your preferred method. See [Installation](docs/installation.md) for prebuilt binaries, Docker, and build-from-source details. If you prefer a direct download instead of a package manager, use a release archive from the [GitHub Releases page](https://github.com/guitarrapc/seiton/releases).

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

# Export workflow structure (jobs, needs, steps) as JSON or Mermaid
seiton check --format flow-json .github/workflows/ci.yml
seiton check --format flow-mermaid .github/workflows/ci.yml

# Apply auto-fixes
seiton --fix

# List all available rules and their status
seiton rules
```

To generate a starter config file:

```sh
seiton init
seiton validate-config
seiton --verbose
```

The three-step flow creates, validates, and confirms config discovery before tuning rules. See [Configuration](docs/configuration.md) for nested-repo guidance and common recipes.

To install agent skill files (for Claude Code, GitHub Copilot, Cursor, etc.):

```sh
# Install for Claude Code (default)
seiton install --skills

# Install for GitHub Copilot
seiton install --skills --target copilot

# Install for Cursor
seiton install --skills --target cursor

# Install CI workflow template
seiton install --ci
```

## Documentation

| Page | What it covers |
|---|---|
| [Overview](docs/index.md) | Product overview and [comparison with other tools](docs/index.md#comparison-with-other-tools). |
| [Installation](docs/installation.md) | Package managers, prebuilt binaries, Docker, attestation verification, and building from source. |
| [Usage](docs/usage.md) | Commands, flags, environment variables, output formats, CI examples, and exit codes. |
| [Rules](docs/rules.md) | Canonical rule reference with behavior, examples, remediation, and configuration notes. |
| [Configuration](docs/configuration.md) | Config file discovery, schema, exclusions, fix settings, and network options. |

## Release flow

When releasing a new version, follow these steps:

1. (manual) From the repository root, bump version strings in `.props`, `.md`, `.sh`, and `.yml` files (latest git tag → next semver; skips `references/` and `*.rb`):

```sh
dotnet ./sandbox/scripts/bump_version.cs patch   # e.g. 0.1.0 → 0.1.1
dotnet ./sandbox/scripts/bump_version.cs minor   # e.g. 0.1.0 → 0.2.0
dotnet ./sandbox/scripts/bump_version.cs major   # e.g. 0.1.0 → 1.0.0
```

2. (manual) Commit the version bump with a message like `chore: Bump version to 0.1.1` and push to the main branch.
3. (manual) Create new tag with the new version (e.g. `git tag v0.1.1`) and push the tag (`git push origin v0.1.1`).
4. (auto) GitHub Actions will trigger on the new tag, build the release artifacts, publish new Playground, and create a draft release with the new version. The release notes will be auto-generated based on merged PRs since the last release.
5. (manual) Check draft release created by GitHub Actions in the [Releases page](https://github.com/guitarrapc/seiton/releases). If the release notes look good, publish the release.
6. (auto) New homebrew rb will be automatically created by GitHub Actions.

## License

Seiton is distributed under the [MIT license](./LICENSE.md).
