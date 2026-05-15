# seiton feedback on githubactions-lab

This note records a fresh evaluation of the current `seiton` implementation against `.references/githubactions-lab`.
The focus is practical usability: whether detections feel appropriate for a sample-heavy repository, whether the CLI behavior is straightforward, and whether the logs make it easy to understand what happened.

## Environment

- Evaluated repository: `.references/githubactions-lab`
- Evaluated binary: current workspace build, published from `src/Seiton`
- OS: Windows
- Shell: PowerShell
- Version observed: `seiton 0.9.9`
- Runtime observed: `.NET 10.0.6, win-x64`

## Execution flow

### 1. Publish the current binary

Executed:

```powershell
cd d:\github\guitarrapc\seiton-gh
dotnet publish src/Seiton -c Release -o artifacts\feedback-seiton-publish
```

Observed:

- Publish succeeded.
- Evaluation used `artifacts/feedback-seiton-publish/seiton.exe`.

### 2. Confirm CLI discoverability

Executed:

```powershell
seiton --help
seiton version
```

Observed:

- Help is compact and easy to start from.
- `--fix`, `--dry-run`, `--check`, `--min-severity`, `--include-actions`, and network flags are discoverable.
- `version` output is concise and support-friendly.

### 3. Initial run without repository tuning

Executed from `.references/githubactions-lab`:

```powershell
seiton --verbose --color never --oneline
```

Initial result:

- `40 errors, 33 warnings in 122 files`
- Exit code `1`

Main observations:

- `config: ...` and `checking ...` in verbose mode are useful. They make the command progress legible.
- The dominant noise source is still `run-env-context-direct-use` in sample workflows that intentionally demonstrate context usage patterns.
- Generated Agentic Workflow files still dominate the output if not excluded:
  - `.github/workflows/agentics-maintenance.yml`
  - `.github/workflows/monthly-oss-repo-status.lock.yml`
- Per-rule breakdown is now very useful for large runs. The initial top offenders were:
  - `run-env-context-direct-use: 31`
  - `if-expr-wrapper: 15`
  - `job-timeout-minutes-required: 7`
  - `unpinned-image: 4`

## Repository-specific tuning

Because this repository is intentionally demonstrative, a local Seiton config was added at `.github/seiton.yaml` with the following policy:

- Disable `run-env-context-direct-use` globally for this repository.
- Exclude generated Agentic Workflow files entirely.
- Add targeted exclusions for sample workflows whose purpose is to demonstrate risky or unusual patterns.

Config used:

```yaml
rules:
  run-env-context-direct-use:
    enabled: false

exclusions:
  - file: .github/workflows/agentics-maintenance.yml
  - file: .github/workflows/monthly-oss-repo-status.lock.yml
  - file: .github/workflows/auto-dump-context.yaml
    rules:
      - dangerous-triggers
  - file: .github/workflows/dump-context.yaml
    rules:
      - dangerous-triggers
  - file: .github/workflows/job-needs-skip-handling-bad.yaml
    rules:
      - if-cond
  - file: .github/workflows/matrix-secret.yaml
    rules:
      - env-var
      - unredacted-secrets
  - file: .github/workflows/merge-branch.yaml
    rules:
      - env-var
  - file: .github/workflows/prevent-file-change2.yaml
    rules:
      - dangerous-triggers
  - file: .github/workflows/reusable-workflow-caller-nest.yaml
    rules:
      - deny-inherit-secrets
  - file: .github/workflows/secrets-access.yaml
    rules:
      - run-secrets-context-direct-use
  - file: .github/workflows/_reusable-workflow-called.yaml
    rules:
      - unredacted-secrets
```

Validation:

```powershell
seiton validate-config
```

Observed:

- Config validated successfully.
- Repo-root relative `file:` exclusions now work correctly on Windows.
- Running Seiton directly on `.github/workflows/agentics-maintenance.yml` returned `0 issues in 1 file`, confirming the prior Windows path-matching issue is fixed.

## Final run after tuning

Executed:

```powershell
seiton --verbose --color never --oneline
```

Final result:

- `5 warnings in 122 files`
- Exit code `1`
- Per-rule breakdown: `unpinned-image: 4, if-expr-wrapper: 1`
- Hint shown: `use --min-severity error to treat warnings as non-blocking in CI`

Remaining findings:

- `if-expr-wrapper` in `.github/workflows/cache.yaml`
- `unpinned-image` in:
  - `.github/workflows/container-job.yaml`
  - `.github/workflows/container-service.yaml`
  - `.github/workflows/dotnet-build.yaml`
  - `.github/workflows/dotnet-build-only-tag.yaml`

Assessment:

- These remaining findings feel appropriate.
- They are actionable and not obviously sample-only noise.
- The final signal-to-noise ratio is good.

## CI-oriented behavior

Executed:

```powershell
seiton --min-severity error --oneline
```

Observed:

- `0 issues in 122 files`
- Exit code `0`

Assessment:

- This is a straightforward CI path when only warnings remain.
- The summary hint makes this behavior much easier to discover than before.

## Fix UX check

### Local fixable case

Executed:

```powershell
seiton --fix --dry-run .github/workflows/cache.yaml
```

Observed:

- A unified diff was shown immediately.
- The `if-expr-wrapper` fix is obvious and reviewable.
- The auto-fix itself feels straightforward.

### Network-assisted case

Executed:

```powershell
seiton --fix --dry-run .github/workflows/container-job.yaml
```

Observed:

- No diff is shown by default, which is expected because image pinning requires network resolution.
- The current implementation now gives a direct hint:
  - `hint: re-run with --enable-image-network to auto-fix image pinning`
- This is much better than leaving the user to infer the next step.

## What felt good

- `--help` is short and usable.
- `version` output is clear.
- `--verbose` gives enough progress detail without being cryptic.
- The per-rule summary is valuable for large noisy repos.
- Repo-root relative exclusion paths now behave as users would expect on Windows.
- The warning-only CI hint is now clear and practical.
- The network-fix hint is now explicit enough to guide the user.

## What still felt awkward

- High-count findings are not inherently a problem when they are clearly fixable. In practice, rules such as `run-env-context-direct-use` can appear in large numbers on sample/demo repositories, but that does not materially harm the experience because `--fix` can clear them in one pass.
- Repository-local tuning is still useful when a sample repository intentionally wants to keep insecure or pedagogical patterns unfixed for demonstration purposes. That is a legitimate exception case, not the normal path.
- `--fix --dry-run` output can feel slightly awkward when diff output and summary/diagnostic text are mixed in one terminal stream. In practice, the fix is understandable, but the summary can appear visually close to the diff body and reduce scanability.
- Verbose mode lists every checked file, which is useful, but on long runs the final summary is still the main anchor. Because the current per-rule breakdown already keeps the run easy to interpret, this is a secondary concern rather than a meaningful product gap.

## Feedback for Seiton

### High value improvements already confirmed as fixed

1. Windows exact-path exclusion behavior is now correct.
2. Warning-only CI guidance is now discoverable.
3. Network-assisted fix guidance is now discoverable.
4. Per-rule count summary materially improves large-run triage.

### Remaining feedback

1. Consider making dry-run output sequencing a bit cleaner.
   - The fix itself is good.
   - The remaining rough edge is visual ordering when diff output and summary text are emitted close together.

2. Keep emphasizing actionable summaries rather than suppressing large fixable result sets.
  - The number of findings is not the main problem when those findings are correct and auto-fixable.
  - The current per-rule breakdown is useful because it helps users distinguish bulk-fixable issues from the smaller set of findings that still require human judgment.

## Overall impression

The current `seiton` experience on `githubactions-lab` is much better than the earlier baseline.
The two most important behavior gaps from the previous evaluation are no longer present:

- exact file exclusions work naturally on Windows
- fix guidance for network-required pinning is explicit

With a small repository-local config, the output becomes easy to reason about and the remaining findings look legitimate.
At this point, the main caveat is not incorrect detection and not the raw number of findings. The key question is whether the output makes the next action obvious.
On that axis, the current implementation is in good shape: large fixable result sets are acceptable as long as they are accurate, summarized clearly, and easy to remediate with `--fix`, while the smaller set of non-auto-fixable findings remains visible for human review.
