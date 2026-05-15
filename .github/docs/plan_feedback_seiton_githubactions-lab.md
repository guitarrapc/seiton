# seiton feedback on githubactions-lab

This is seiton cli feedback for the repository guitarrrapc/githubactions-lab. You can find the repository here .references/githubactions-lab.
This feedback is based on an evaluation of seiton 0.9.9 on Windows using PowerShell, and it focuses on the usability of seiton in a sample-heavy repository with many intentionally demonstrative workflows. The goal was to confirm whether detections are appropriate, tune the config to exclude non-actionable findings, and evaluate the overall usability and log readability of seiton in this context.

## Summary

- Environment: Windows, PowerShell, `seiton 0.9.9`
- Repository type: GitHub Actions sample/lab repository with many intentionally demonstrative workflows
- Goal: confirm whether detections are appropriate in this repository, tune config to exclude non-actionable findings, and evaluate usability/log readability

## Execution log

### 1. CLI confirmation

Executed:

```powershell
seiton --help
seiton version
```

Observed:

- Help text is compact and enough to start quickly.
- `--verbose`, `--oneline`, `--format`, `--fix`, `--dry-run`, `--check`, `--min-severity` are easy to discover.
- Installed version was `0.9.9`.

### 2. Initial run on this repository

Executed:

```powershell
seiton --verbose --color never --oneline
```

Initial result:

- `40 errors, 33 warnings in 122 files`

Main observations:

- Verbose output is easy to follow. The `config: ...` line and `checking ...` lines make it clear what file is loaded and what file is being analyzed.
- Output was dominated by two generated Agentic Workflow files:
  - `.github/workflows/agentics-maintenance.yml`
  - `.github/workflows/monthly-oss-repo-status.lock.yml`
- In this repository those files are generated and should not be edited directly, so reporting them as actionable findings is not useful for repository-level evaluation.
- A large portion of remaining errors came from `run-env-context-direct-use`, which is technically understandable but very noisy in a sample repository that intentionally demonstrates context and env usage patterns.

### 3. Agentic Workflow exclusion attempt

Started with the documented file-only exclusion style:

```yaml
exclusions:
  - file: .github/workflows/agentics-maintenance.yml
  - file: .github/workflows/monthly-oss-repo-status.lock.yml
```

Observed:

- `seiton validate-config` reported the config as valid.
- However, the exclusions did not suppress diagnostics on Windows.
- The same was true even when using explicit `rules:` entries with those repo-root relative paths.

Then changed the exclusion to basename glob style:

```yaml
exclusions:
  - file: "**/agentics-maintenance.yml"
  - file: "**/monthly-oss-repo-status.lock.yml"
```

Observed:

- This worked correctly on Windows.
- Running `seiton --oneline` against each file then returned `0 issues in 1 file`.

This is the most important behavior gap found during this evaluation.

### 4. Repository-specific tuning for a sample-heavy repo

This repository intentionally contains unsafe, unusual, or educational workflows. To get a usable final baseline, the following config decisions were made:

- Excluded the two generated Agentic Workflow files entirely.
- Disabled `run-env-context-direct-use` globally for this repository.
- Added targeted exclusions for intentionally demonstrative workflows:
  - `dangerous-triggers` on sample `pull_request_target` workflows
  - `if-cond` on `job-needs-skip-handling-bad.yaml`
  - `deny-inherit-secrets` on `reusable-workflow-caller-nest.yaml`
  - `run-secrets-context-direct-use` on `secrets-access.yaml`
  - `env-var` / `unredacted-secrets` on sample workflows where those patterns are the point of the example

Rationale:

- These suppressions are appropriate for this repository as a lab/samples repository.
- They would not be a good default for a production repository.

### 5. Final run after tuning

Executed:

```powershell
seiton validate-config
seiton --verbose --color never --oneline
```

Final result:

- `5 warnings in 122 files`
- Remaining findings:
  - `if-expr-wrapper` in `.github/workflows/cache.yaml`
  - `unpinned-image` in:
    - `.github/workflows/container-job.yaml`
    - `.github/workflows/container-service.yaml`
    - `.github/workflows/dotnet-build.yaml`
    - `.github/workflows/dotnet-build-only-tag.yaml`

Assessment of final findings:

- These are appropriate detections.
- They are understandable and still useful in this repository.
- The final signal-to-noise ratio is much better than the initial run.

### 6. CI-oriented behavior check

Executed:

```powershell
seiton --min-severity error --oneline
```

Observed:

- Result became `0 issues in 122 files`.
- This is a practical way to use seiton in CI when only warnings remain.

This is useful, because the default command still exits non-zero when warnings exist.

### 7. Fix UX check

Executed:

```powershell
seiton --fix --dry-run .github/workflows/cache.yaml
seiton --fix --dry-run .github/workflows/container-job.yaml
```

Observed:

- For `if-expr-wrapper`, dry-run output was very good. A unified diff was shown immediately, and the fix was obvious.
- For `unpinned-image`, there was no diff because the fix requires network-assisted image pinning. The warning remained understandable, but the output did not strongly guide the user toward `--enable-image-network`.

## Usability evaluation

### What felt good

- `--help` is short and practical.
- `version` output is clear enough for support/bug reports.
- `--verbose` is useful. Seeing `config: ...` first is especially good.
- `--oneline` is effective when scanning many files.
- Rule IDs and messages are concrete. It is usually obvious what needs to change.
- `--fix --dry-run` gives a good reviewable experience when a local fix is possible.

### What felt awkward

- Documented file-only exclusions using repo-root relative paths did not work on Windows in this repository, while basename globs did work.
- In a sample-heavy repository, `run-env-context-direct-use` quickly dominates the output and hides more actionable findings.
- Warning-only runs still exit non-zero by default. This is manageable with `--min-severity error`, but users need to know that knob exists.
- `--fix --dry-run` for image pinning is less self-explanatory when network flags are required.
- Verbose mode lists every checked file, which is useful, but after large runs a rule-count summary would make triage faster.

## Feedback for seiton

### High priority

1. Exclusion path matching on Windows should be reviewed.

- Docs say repo-root relative paths such as `.github/workflows/generated.yml` should work.
- In this evaluation, those patterns validated successfully but did not suppress diagnostics.
- `**/generated.yml` style glob did suppress correctly.
- This suggests a path normalization or path base mismatch on Windows.

2. Consider adding a clearer summary for large runs.

- Example: total counts by rule after the normal summary.
- In repositories with many examples, this would make it easier to find the rules causing most noise.

### Medium priority

1. Consider improving the guidance around warning-only exit codes.

- Current behavior is workable, but users may expect warning-only runs to be visually and operationally different from error runs.
- A hint in help output or summary text about `--min-severity error` would help.

2. Consider stronger guidance when a fix requires network.

- For `unpinned-image`, a message such as `re-run with --enable-image-network to auto-fix` would make the fix path more discoverable.

3. Consider a repository profile or tuning pattern for sample/demo repositories.

- In educational repositories, some rules are correct but not actionable.
- A documented strategy for tuning noisy-but-correct rules would improve first-run experience.

## Final config outcome in this repository

The final tuned config is as follows.

Net effect:

- Initial baseline: `40 errors, 33 warnings`
- Final curated baseline: `5 warnings`
- Remaining detections are appropriate and understandable.

```
# Seiton linter configuration
# Preferred location: .github/seiton.yaml

rules:
  run-env-context-direct-use:
    enabled: false

  # Example: override a rule's behavior.
  # dangerous-triggers:
  #   severity: warning
  #   events:
  #     extend:
  #       - issue_comment

  # runner-label:
  #   known-hosted-labels:
  #     extend:
  #       - ubuntu-24.04-large

  # credentials:
  #   public-registries:
  #     extend:
  #       - ghcr.io

  # cache-poisoning:
  #   untrusted-triggers:
  #     extend:
  #       - issue_comment

  # unredacted-secrets:
  #   output-commands:
  #     extend:
  #       - tee

  # forbidden-uses:
  #   allow:
  #     - actions/*
  #   deny:
  #     - some-untrusted-org/*

  # unpinned-uses:
  #   ignore-actions:
  #     - my-org/internal-action
  #     - my-org/setup-*

  # overprovisioned-secrets:
  #   max-step-env-secrets: 5
  #   max-job-secrets: 5

  # expr-undefined-var:
  #   assume-events:
  #     - workflow_dispatch

  # Online rules (default: disabled). Enable individually:
  # known-vulnerable-actions:
  #   enabled: true
  # impostor-commit:
  #   enabled: true
  # ref-confusion:
  #   enabled: true
  # stale-action-refs:
  #   enabled: true

exclusions:
  # - file: .github/workflows/legacy-*.yml
  #   rules:
  #     - runner-no-latest
  #   jobs:
  #     - legacy
  # File-only exclusion (excludes entire file from rule checks):
  # - file: .github/workflows/generated.yml
  - file: "**/agentics-maintenance.yml"
  - file: "**/monthly-oss-repo-status.lock.yml"
  - file: "**/auto-dump-context.yaml"
    rules:
      - dangerous-triggers
  - file: "**/dump-context.yaml"
    rules:
      - dangerous-triggers
  - file: "**/job-needs-skip-handling-bad.yaml"
    rules:
      - if-cond
  - file: "**/matrix-secret.yaml"
    rules:
      - env-var
      - unredacted-secrets
  - file: "**/merge-branch.yaml"
    rules:
      - env-var
  - file: "**/prevent-file-change2.yaml"
    rules:
      - dangerous-triggers
  - file: "**/reusable-workflow-caller-nest.yaml"
    rules:
      - deny-inherit-secrets
  - file: "**/secrets-access.yaml"
    rules:
      - run-secrets-context-direct-use
  - file: "**/_reusable-workflow-called.yaml"
    rules:
      - unredacted-secrets

fix:
  defaults:
    # job-timeout-minutes: 15
  pinning:
    # enable-network: false
    # min-age-days: 14
    # exclude-branches:
    #   - main
    #   - master
    # ignore-actions:
    #   - uses: "slsa-framework/*"
    #     ref: "*"
  images:
    # exclude-images:
    #   - scratch
    # exclude-tags:
    #   - latest
    # ignore-images:
    #   - mcr.microsoft.com/**

network:
  # on-error: skip
  # timeout-seconds: 30
  # max-concurrency: (omit; default is min(4, logical CPUs))
  # github:
  #   ghes-api-url: ""
  #   ghes-fallback: false

output:
  # sort-order: location    # location (default) | rule
```

## Overall impression

Seiton is already usable and the rule messages are generally good. The strongest positive point is that `--verbose` plus `--oneline` makes it possible to understand what happened without guessing. The strongest issue found in this repository was exclusion behavior on Windows: the documented repo-root relative file exclusion shape did not work here, while basename glob exclusions did.

For this repository specifically, a tuned config made the output much more natural. After tuning, the remaining warnings were the kinds of findings that are easy to discuss and easy to act on.

## Action plan

### Current status

seiton 0.9.9 is at a practical, usable stage. Rule message quality, `--verbose`/`--oneline` output design, and `--fix --dry-run` review experience are all positively evaluated. Being able to narrow output to 5 warnings in a 122-file repository after config tuning demonstrates that rule accuracy and config flexibility are sufficient.

### Identified issues

| Priority | Issue | Impact |
|----------|-------|--------|
| **High** | Windows repo-root relative path exclusion does not work | `.github/workflows/foo.yml` passes validate-config but does not suppress diagnostics. `**/foo.yml` glob works. Path normalization bug. |
| **High** | No per-rule count summary for large runs | Users must visually scan full output to identify which rules are causing the most noise. |
| **Medium** | warning-only runs still exit non-zero | CI users need `--min-severity error` but this is not easily discoverable. |
| **Medium** | No guidance when fix requires network flag | `--fix` for `unpinned-image` produces no diff without `--enable-image-network`, but the user is not told this. |
| **Medium** | No tuning guide for sample/demo repositories | First-run experience in educational repos feels noisy even when detections are technically correct. |

### Implementation order (recommended)

1. **Fix Windows path normalization for exclusions** (bug, high priority) — **DONE**
   - Exclusion file matching likely fails due to OS path separator (`\` vs `/`) or relative path resolution mismatch.
   - `**/foo.yml` works but `.github/workflows/foo.yml` does not — suggests the exclusion pattern is not normalized, or the target file path has a different prefix.
   - **Root cause**: `InputDiscovery` returns absolute paths via `Path.GetFullPath` (e.g. `D:\repo\.github\workflows\ci.yml`). After slash normalization this becomes `D:/repo/.github/workflows/ci.yml`. A relative exclusion pattern like `.github/workflows/ci.yml` was matched from position 0, so it could never match the absolute path prefix. Patterns starting with `**/` worked because `**` consumes arbitrary leading segments.
   - **Fix**: Added `NormalizeExclusionPattern()` in `LintEngine.cs` that prepends `**/` to relative patterns (those not starting with `**/`, `/`, or a drive letter). This makes `.github/workflows/ci.yml` become `**/.github/workflows/ci.yml`, which correctly suffix-matches any absolute path.
   - **Benchmark**: Zero allocation increase. Timing within noise (±3%).
   - **Tests**: 2 new tests (`LintEngine_ConfigExclusion_RepoRootRelativePath_SuppressesDiagnostics`, `LintEngine_ConfigExclusion_RepoRootRelativeGlob_SuppressesDiagnostics`). All 8 existing exclusion tests still pass.

2. **Add per-rule count summary** (UX, high priority)
   - After the existing `N errors, M warnings in F files` line, show rule-level counts (e.g. `run-env-context-direct-use: 28, dangerous-triggers: 5, ...`).
   - Decide whether to show in `--verbose` only or always.

3. **Improve exit code / severity guidance** (UX, medium priority)
   - In summary line or `--help`, hint that `--min-severity error` ignores warnings in CI.

4. **Improve message when fix requires network** (UX, medium priority)
   - When `--fix` produces no changes for a rule that needs network, emit a hint like: `this rule's fix requires network access: re-run with --enable-image-network`.

5. **Add tuning guide for sample/demo repos** (documentation, medium priority)
   - Document recommended config patterns for educational/lab repositories.
