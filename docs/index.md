# Seiton — Security-Focused Linter & Fixer for GitHub Actions

**Seiton** is a security-focused linter & fixer for GitHub Actions.
It parses workflow files and action metadata files, then runs a curated set of lint rules to catch security issues, policy violations, and mistakes before they reach production.

---

## What Seiton Does

Seiton rules:

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
| 62 lint rules | 58 default local rules plus 4 opt-in online audit rules (`RuleCatalog`). See [Rules](rules.md) for the full list. |
| Security-first rules | Template injection, unpinned actions/images, dangerous triggers, secret misuse, and more. |
| Correctness checks | Job structure, needs-graph cycles, glob syntax, shell names, ID naming, schedule/dispatch validation. |
| Supply-chain hygiene | Unpinned `uses:` / images, archived actions, and optional online checks (known vulnerabilities, impostor commits, ref confusion, stale refs). |
| Auto-fix support | `seiton --fix` applies machine-safe remediations in place, including optional network-assisted SHA/digest pinning. |
| Multiple output formats | `text` (default locally), `github-actions` (default on GitHub Actions: job summary + rich stdout), `json`, `sarif` (GitHub Advanced Security), plus `flow-json` / `flow-mermaid` for workflow structure. |
| Playground | Browser-based linting and interactive flow visualization via WebAssembly — no install, YAML stays local. |
| Config file | Optional `.github/seiton.yaml` for rule tuning, exclusions, and network options. |
| Inline suppression | `# seiton: disable-next-line <rule-id>` directives inside workflow files. |
| NativeAOT binary | Single-file executable; no .NET runtime required at deployment. |
| Agent skill install | `seiton install --skills` deploys agent instructions for Claude Code, GitHub Copilot, Cursor, etc. |

---

## Quick Start

Try the [Playground](https://guitarrapc.github.io/seiton/) first — paste workflow YAML, run lint in the browser, and switch to the **Flow** tab to explore jobs and steps interactively. No install required.

```sh
# Lint all workflow files in the current repository (same as `seiton check`)
seiton

# Lint specific files
seiton .github/workflows/ci.yml action.yml

# See all issues as JSON
seiton --format json

# Export workflow structure as JSON or Mermaid (check-only; no lint diagnostics)
seiton --format flow-json .github/workflows/ci.yml
seiton --format flow-mermaid .github/workflows/ci.yml

# On GitHub Actions, omit --format (job summary + default GHA output)
seiton

# Apply auto-fixes
seiton --fix

# List all available rules and their status
seiton rules
```

Example `--oneline` output:

```
.github/workflows/ci.yml:4:5: error [job-timeout-minutes-required] jobs.'test' should define timeout-minutes (default is 360 minutes); if not possible, set timeout-minutes on each step instead
.github/workflows/ci.yml:7:38: error [template-injection] "github.event.pull_request.title" is potentially untrusted. avoid using it directly in inline scripts. instead, pass it through an environment variable. see https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#good-practices-for-mitigating-script-injection-attacks for more details
.github/workflows/ci.yml:8:37: warning [unpinned-uses] 'actions/checkout@v6' is not pinned to a full-length commit SHA. see https://github.com/actions/checkout/tree/v6 (fixable with --fix --enable-pin-network)
.github/workflows/ci.yml:11:33: warning [popular-action-inputs] unknown input 'node_version' for action 'actions/setup-node@v4'. available inputs are "architecture", "cache", "cache-dependency-path", "check-latest", "mirror", "mirror-token", "node-version", "node-version-file", "package-manager-cache", "registry-url", "scope", "token". did you mean 'node-version'? see https://github.com/actions/setup-node/tree/v4
```

---

## Documentation

| Page | Description |
|---|---|
| [Installation](installation.md) | How to install Seiton on Windows, macOS, and Linux. |
| [Usage](usage.md) | Commands, flags, environment variables, CI integration. |
| [Rules](rules.md) | Full list of all lint rules with examples and remediation guidance. |
| [Configuration](configuration.md) | Config file format, rule tuning, exclusions, and network options. |

---

## Related Tools

Several mature tools help with GitHub Actions workflows. They overlap in places but often emphasize different goals — correctness, security policy, or supply-chain pinning. None of them has to be an either/or choice.

### By category

| Tool | Category | What it focuses on |
|---|---|---|
| **Seiton** | Lint + Fix | Security, correctness, and supply-chain checks for workflow and action metadata files, with optional auto-fix |
| [actionlint] | Lint | Workflow syntax, expression typing, and optional shellcheck / pyflakes integration |
| [zizmor] | Lint | Security audits with persona profiles; also supports dependabot config and remote inputs |
| [ghalint] | Lint | A small, opinionated set of security policies that are easy to adopt |
| [frizbee] | Pin / Update | Rewriting action and image tags to checksums across YAML files |
| [pinact] | Pin / Update | Pinning, updating, and verifying version annotations on action refs |
| [dockerfile-pin] | Pin | Digest pins for Dockerfile `FROM`, docker-compose `image`, and related references |

[actionlint]: https://github.com/rhysd/actionlint
[zizmor]: https://github.com/zizmorcore/zizmor
[ghalint]: https://github.com/suzuki-shunsuke/ghalint
[frizbee]: https://github.com/stacklok/frizbee
[pinact]: https://github.com/suzuki-shunsuke/pinact
[dockerfile-pin]: https://github.com/azu/dockerfile-pin

**Linters** report diagnostics and usually leave files unchanged unless you opt into a fix mode.
**Pinners / updaters** rewrite version references as their main job; they are not general-purpose security linters.
Seiton sits primarily in the linter category, with optional fixes — including network-assisted SHA and digest pinning when you pass `--fix --enable-pin-network` / `--enable-image-network`.

### Where Seiton fits

Seiton is built for teams that want one tool to lint `.github/workflows/` and `action.yml` files: structural correctness, security policy, supply-chain hygiene, and optional online audits in a single pass. It also supports safe auto-fix for a subset of rules.

Seiton does **not** try to replace every specialized workflow:

| Area | Seiton today | Often handled elsewhere |
|---|---|---|
| Shell/Python script checks inside `run:` | Not built in | [actionlint] with shellcheck / pyflakes |
| Dependabot config analysis | Out of scope | [zizmor] |
| Remote repository auditing (`user/repo@ref`) | Out of scope | [zizmor] |
| Upgrading pinned actions to newer releases | Not built in | [pinact] |
| Verifying version comments next to SHA pins | Not built in | [pinact] |
| Dockerfile / docker-compose digest pinning | Out of scope | [dockerfile-pin], [frizbee] |
| Broad YAML image pinning outside Actions files | Out of scope | [frizbee] |

### Working alongside other tools

Many teams combine tools rather than picking one winner:

- **[actionlint](https://github.com/rhysd/actionlint)** — strong workflow correctness and script-level checks. Pairs well with Seiton when you want shellcheck/pyflakes in addition to Seiton's security and policy rules.
- **[zizmor](https://github.com/zizmorcore/zizmor)** — deep security audit catalog with persona-based noise control. Overlaps with several Seiton rules; some teams run one, others run both for defense in depth.
- **[ghalint](https://github.com/suzuki-shunsuke/ghalint)** — minimal policy set that is quick to roll out. Seiton covers the same policy areas and extends with additional rules and per-rule configuration.
- **[frizbee](https://github.com/stacklok/frizbee)**, **[pinact](https://github.com/suzuki-shunsuke/pinact)**, **[dockerfile-pin](https://github.com/azu/dockerfile-pin)** — dedicated pinning and update workflows. Seiton can pin during `--fix`, but these tools offer scope and workflows (compose files, comment verification, release updates) that Seiton intentionally leaves to specialized tools.
