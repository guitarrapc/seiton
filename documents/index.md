# Seiton — GitHub Actions Lint & Fix Tool

**Seiton** is a fast, opinionated static analysis tool for GitHub Actions.
It parses workflow files and action metadata files, then runs a curated set of lint rules to catch mistakes, security issues, and policy violations before they reach production.

---

## What Seiton Does

Seiton checks:

- **Workflow files** — `.github/workflows/*.yml` / `.github/workflows/*.yaml`
- **Action metadata files** — `action.yml` / `action.yaml`, including `.github/actions/<name>/action.yml`

For every file it analyzes, Seiton:

1. Parses the YAML into a typed AST, reporting structural errors immediately.
2. Runs a default rule set that covers security, correctness, and supply-chain hygiene.
3. Outputs diagnostics with file path, line/column, severity, message, and source snippet.
4. Optionally applies auto-fixes for supported rules.

---

## Key Features

| Feature | Description |
|---|---|
| Security-first rules | Template injection, unpinned actions/images, dangerous triggers, secret misuse, and more. |
| Correctness checks | Job structure, needs-graph cycles, glob syntax, shell names, ID naming. |
| Supply-chain hygiene | Unpinned `uses:`, archived actions, known vulnerable actions (online), impostor commits (online). |
| Auto-fix support | `seiton fix` or `seiton --fix` applies machine-safe remediations in place. |
| Multiple output formats | `text` (default), `json`, `sarif` (GitHub Advanced Security). |
| Config file | Optional `.github/seiton.yaml` for rule tuning, exclusions, and network options. |
| Inline suppression | `# seiton: disable-next-line <rule-id>` directives inside workflow files. |
| NativeAOT binary | Single-file executable; no .NET runtime required at deployment. |

---

## Quick Start

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

Example output:

```
.github/workflows/ci.yml:18:7: [error] template-injection: untrusted value 'github.event.pull_request.title' interpolated directly into run script; use env: indirection instead
.github/workflows/ci.yml:42:5: [warning] unpinned-uses: action 'actions/checkout@v4' is not pinned to a full commit SHA
.github/workflows/ci.yml:55:5: [warning] job-permissions-required: job 'build' does not declare explicit permissions
```

---

## Documentation

| Page | Description |
|---|---|
| [Installation](installation.md) | How to install Seiton on Windows, macOS, and Linux. |
| [Usage](usage.md) | Commands, flags, environment variables, CI integration. |
| [Checks](checks.md) | Full list of all lint rules with examples and remediation guidance. |
| [Configuration](configuration.md) | Config file format, rule tuning, exclusions, and network options. |

---

## Comparison with Other Tools

Several tools exist in the GitHub Actions analysis space. They differ significantly in **concept** — some are linters that find problems, others are pinners/updaters that rewrite version references. Seiton covers both.

### Concept Overview

| Tool | Category | Primary Goal |
|---|---|---|
| **Seiton** | Lint + Fix | Static analysis (security + correctness) with integrated auto-fix |
| [actionlint] | Lint | Syntax and type correctness of workflow files |
| [zizmor] | Lint | Security-focused static analysis |
| [ghalint] | Lint | Security policy compliance (focused rule set) |
| [frizbee] | Pin / Update | Replace action/image tags with SHA checksums |
| [pinact] | Pin / Update | Pin and update action versions; verify version annotations |
| [dockerfile-pin] | Pin | Add digest pins to Dockerfile `FROM` and compose `image` fields |

[actionlint]: https://github.com/rhysd/actionlint
[zizmor]: https://github.com/zizmorcore/zizmor
[ghalint]: https://github.com/suzuki-shunsuke/ghalint
[frizbee]: https://github.com/stacklok/frizbee
[pinact]: https://github.com/suzuki-shunsuke/pinact
[dockerfile-pin]: https://github.com/azu/dockerfile-pin

**Linters** (actionlint, zizmor, ghalint, Seiton) report diagnostics without modifying files by default.
**Pinners/Updaters** (frizbee, pinact, dockerfile-pin) are primarily file-rewriting tools — they exist specifically to pin or update version references, and do not lint for broad security issues.
Seiton bridges the two: it lints like a linter and can also apply fixes (including network-assisted SHA/digest pinning) like a pinner.

---

### Seiton vs. actionlint

[actionlint](https://github.com/rhysd/actionlint) is the most comprehensive correctness checker for GitHub Actions workflow files. It excels at syntax validation, expression type-checking, shellcheck/pyflakes integration, and reusable-workflow contract validation.

Seiton matches actionlint on a wide range of structural checks (including `schedule` cron constraints, `workflow_dispatch` inputs, and local action metadata contracts) while emphasizing **security policy**, **supply-chain** rules that actionlint does not implement, and **auto-fix** support.

| Aspect | Seiton | actionlint |
|---|---|---|
| Syntax / structural validation | ✓ | ✓ |
| Expression type checking | ✓ | ✓ |
| shellcheck / pyflakes integration | ✗ | ✓ |
| Security rules (injection, secrets, permissions) | ✓ (broad) | Partial |
| Supply-chain rules (pinning, archived, vulnerable) | ✓ | ✗ |
| Auto-fix | ✓ | ✗ |
| Online audit rules | ✓ (opt-in) | ✗ |
| Action metadata file support | ✓ | ✗ (lints as secondary only) |
| Local action input/output resolution | ✓ | ✓ |
| Config model | Rule-ID-centric | Global + path-based |

**When to use actionlint alongside Seiton:** keep **Seiton** as the main linter; add **actionlint** when you need its integrated **shellcheck** / **pyflakes** or marginally deeper expression typing for a different class of bugs.

---

### Seiton vs. zizmor

[zizmor](https://github.com/zizmorcore/zizmor) is a security-focused static analysis tool written in Rust. It targets a similar security rule space to Seiton's security-oriented rules.

| Aspect | Seiton | zizmor |
|---|---|---|
| Template injection detection | ✓ | ✓ |
| Dangerous triggers | ✓ | ✓ |
| Permissions and secret misuse | ✓ (broad) | ✓ |
| Supply-chain / pinning rules | ✓ | ✓ |
| Correctness rules (job structure, glob, etc.) | ✓ | Partial |
| Auto-fix | ✓ | ✓ (growing) |
| Online audit rules | ✓ (opt-in) | ✓ (opt-in) |
| Dependabot config analysis | ✗ | ✓ |
| Remote repository auditing | ✗ | ✓ |
| Config model | Rule-ID-centric YAML | YAML |

---

### Seiton vs. ghalint

[ghalint](https://github.com/suzuki-shunsuke/ghalint) is a focused security-policy linter by suzuki-shunsuke. It enforces a curated set of ~13 policies (permissions, secrets scope, action pinning, timeouts, etc.) with no auto-fix.

| Aspect | Seiton | ghalint |
|---|---|---|
| Rule breadth | Large (50+ rules) | Small (~13 policies) |
| Rule configurability | High (per-rule tuning) | Low (enable/disable per policy) |
| Auto-fix | ✓ | ✗ |
| Inline suppression | ✓ | ✗ |
| Action metadata support | ✓ | ✓ |

**Concept difference:** ghalint is intentionally minimal — it enforces a small opinionated policy set without configuration complexity. Seiton provides a much larger rule set with per-rule tunability and fix capabilities.

---

### Seiton vs. frizbee

[frizbee](https://github.com/stacklok/frizbee) is a tag-to-checksum replacement tool, not a linter. It rewrites `uses: action@tag` to `uses: action@sha256` and does the same for container images. It does not detect security issues beyond unpinned references.

| Aspect | Seiton | frizbee |
|---|---|---|
| Lint / detect issues | ✓ | ✗ |
| Pin action refs to SHA | ✓ (fix, opt-in network) | ✓ (primary purpose) |
| Pin container image digests | ✓ (fix, opt-in network) | ✓ (primary purpose) |
| Dockerfile `FROM` pinning | ✗ | ✗ |
| docker-compose image pinning | ✗ | ✓ |

**Concept difference:** frizbee is a file-rewriting utility in the pinner category. Seiton includes pinning as one fix capability among many, but its primary value is detection and reporting.

---

### Seiton vs. pinact

[pinact](https://github.com/suzuki-shunsuke/pinact) is a pinner/updater for GitHub Actions version references. It pins tags to commit SHAs, updates pinned versions to newer releases, and verifies version annotations in comments. It does not perform general security linting.

| Aspect | Seiton | pinact |
|---|---|---|
| Lint / detect issues | ✓ | ✗ |
| Pin action refs to SHA | ✓ (fix, opt-in network) | ✓ (primary purpose) |
| Update pinned actions to latest | ✗ | ✓ |
| Verify version annotation comments | ✗ | ✓ |
| PR review creation | ✗ | ✓ |

**Concept difference:** pinact's primary workflow is "pin → update → annotate". Seiton's primary workflow is "detect → report → optionally fix". They solve different problems and can be used together.

---

### Seiton vs. dockerfile-pin

[dockerfile-pin](https://github.com/azu/dockerfile-pin) adds `@sha256:<digest>` to `FROM` lines in Dockerfiles, `image` fields in docker-compose.yml, and Docker image references in GitHub Actions. It is a single-purpose file-rewriting tool.

| Aspect | Seiton | dockerfile-pin |
|---|---|---|
| Lint / detect issues | ✓ | ✗ |
| Pin GitHub Actions image refs | ✓ (fix) | ✓ (primary purpose) |
| Pin Dockerfile `FROM` lines | ✗ | ✓ |
| Pin docker-compose image fields | ✗ | ✓ |

**Concept difference:** dockerfile-pin is a Dockerfile/compose-oriented tool that also touches GitHub Actions Docker image references. Seiton focuses on GitHub Actions files and does not modify Dockerfiles.

---

### Summary

Use **Seiton** as your primary GitHub Actions linter: it covers security, supply-chain hygiene, workflow and action metadata structure (including schedule, dispatch inputs, and local action contracts), optional online audits, and fixes. Combine other tools only where Seiton intentionally does not go:

- Add **actionlint** if you need integrated **shellcheck** / **pyflakes** or actionlint’s expression checker as an extra pass.
- Add **frizbee** or **pinact** if you want a dedicated update workflow that also refreshes version annotation comments and upgrades pinned actions to newer releases.
- Add **dockerfile-pin** if you need Dockerfile `FROM` and docker-compose `image` digest pinning outside of GitHub Actions files.
