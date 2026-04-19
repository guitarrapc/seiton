# Linter False Positive Reduction Plan

## Scope

This plan tracks false-positive reduction for running Seiton against `guitarrapc/githubactions-lab`.

## Baseline (2026-04-19)

Target input:
- `.references/githubactions-lab/.github/workflows/_reusable-dump-context.yaml`

Observed diagnostics after latest fix:
- `run-env-context-direct-use`: 8 findings (error-level)
- `template-injection`: 0 findings on this file (fixed from previous false-positive behavior)

## Completed

1. `template-injection` false-positive reduction
- Problem: `env:` mapping declarations that intentionally perform safe indirection were reported as template injection.
- Change: `template-injection` now checks direct interpolation in `run` script sinks only.
- Reasoning: aligns with the remediation guidance used by reference implementations (especially zizmor), where `env` mapping is the indirection mechanism.
- Validation:
  - `RuleRegression_TemplateInjectionRule_TableDriven` passes.
  - No `template-injection` diagnostics on `_reusable-dump-context.yaml`.

2. `run-env-context-direct-use` severity adjustment
- Problem: direct `${{ env.* }}` expansion in `run` is a high-risk pattern that can lead to shell/script injection misuse and should fail CI by default.
- Change: `run-env-context-direct-use` diagnostics are emitted as error.
- Reasoning: treat this rule as a security control, not style guidance.
- Validation target: `_reusable-dump-context.yaml` reports error diagnostics for this rule.

## Candidate Next Items

1. Reduce false positives without lowering severity
- Current behavior: `${{ env.* }}` in `run` is reported as error.
- Follow-up candidate: add narrow security-preserving exemptions only when dataflow can be proven static-safe.

2. Add regression fixture for workflow-call inputs -> env -> run pattern
- Add tests for reusable workflow input mapping patterns to ensure no reintroduction of the original false positive.

## Spec Sync

The following docs were updated for completed item #1:
- `Docs/Seiton_Linter_spec.md`
- `Docs/Seiton_Linter_csharp_spec.md`
- `Docs/Seiton_Linter_go_spec.md`

## Decision Log

- 2026-04-19: Adopted run-sink-focused template-injection detection to reduce obvious false positives for env indirection patterns.
