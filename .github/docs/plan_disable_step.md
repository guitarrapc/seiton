# Plan: Inline `disable-step` Suppression

## Goal

Add `# seiton: disable-step <rule-id list>` so users can suppress diagnostics that belong to one GitHub Actions step, including diagnostics reported inside multi-line `run:` block scalar content where `disable-next-line` is too narrow.

## User-Facing Specification

- `disable-step` targets the next step item in the same `steps:` sequence.
- It applies to workflow steps and composite action steps.
- The rule-id list is required and uses the same comma/whitespace separation as existing inline directives.
- Blank lines, ordinary comments, and other seiton inline directives between `disable-step` and the target step are ignored.
- Any intervening YAML content, or no following step item in the same `steps:` sequence, produces a configuration diagnostic on the directive.
- Multiple `disable-step` directives that target the same step are merged.
- Suppressed diagnostics are recorded with `SuppressionSource.InlineStep` when it is the representative suppression source.
- Step scope is inline-only. `.github/seiton.yaml` exclusions remain file/job scoped.
- Unused `disable-step` directives do not produce warnings, matching existing inline directives.

## Implementation Plan

1. Update `.github/docs/Seiton_Linter_spec.md` and `.github/docs/Seiton_Linter_csharp_spec.md` before production code.
2. Add red tests in `RuleInterfaceTests.Suppression.cs` for the missing behavior and equivalence classes.
3. Implement step scope collection using AST `Step.Range` and existing inline directive parsing patterns.
4. Update user docs and installed skill references.
5. Run targeted tests, then full tests.
6. Run `CoreLintBenchmark` before and after implementation and compare mean time and allocated bytes against the committed baseline.

## Performance Plan

- Reuse existing per-run inline suppression parsing and dictionary structures.
- Build step suppression scopes lazily only after a `disable-step` directive is encountered; documents without `disable-step` keep the existing suppression path.
- Resolve step item lines once while building step scopes, using a per-run line-start table, so each `disable-step` directive can bind with integer comparisons instead of rescanning source text.
- Avoid string decoding for step matching; use line/range metadata from AST and existing rule-id normalization.
- No parser changes are planned.

## Verification Results

- Red tests were added before production code for step-scope suppression, following-step isolation, directive merging, invalid placement, composite action steps, and non-step sequence items.
- Targeted disable-step tests: `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/DisableStep_*` passed, 6 tests.
- Rule interface regression tests: `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/*` passed, 518 tests.
- Full suite: `dotnet test` passed across `Seiton.Update.Tests`, `Seiton.Tests`, `Seiton.Core.Tests`, and `Seiton.Playground.Tests` (`[✓142/x0/↓1]`, 1 existing skip).

Final `CoreLintBenchmark` after the review pass, lazy step-scope construction, cached step item line lookup, cached line-start reuse, and indentation-aware step binding:

| Size | FixEnabled | Baseline Mean | Final Mean | Baseline Allocated | Final Allocated |
|---|---:|---:|---:|---:|---:|
| Small | False | 288.3 us | 368.5 us | 9.89 KB | 9.89 KB |
| Small | True | 192.3 us | 465.8 us | 11.2 KB | 11.35 KB |
| Medium | False | 3,739.6 us | 4,304.5 us | 52.91 KB | 52.91 KB |
| Medium | True | 4,894.2 us | 5,763.2 us | 66.13 KB | 66.27 KB |
| Large | False | 50,190.3 us | 56,396.3 us | 256.86 KB | 262.75 KB |
| Large | True | 76,443.6 us | 89,062.1 us | 317.51 KB | 346.7 KB |

BenchmarkDotNet ShortRun timings were noisy on this machine. The latest run still had wide error margins, for example Small/True `Error = 889.0 us` for a `Mean = 465.8 us`, and Large/True `Error = 116,857.5 us` for a `Mean = 89,062.1 us`. Allocation stayed within the +10% threshold for every scenario; the largest increase was Large/True at about +9.2%. Documents without `disable-step` do not eagerly build step scopes.

Focused `DisableStepInlineSuppressionBenchmark` was added to measure files that actually contain `# seiton: disable-step` directives and trigger suppressed `unredacted-secrets` diagnostics. That benchmark showed the cached line-start change reduced allocation for the feature-specific path:

| Size | Before Mean | After Mean | Before Allocated | After Allocated |
|---|---:|---:|---:|---:|
| Small | 38.95 us | 61.12 us | 6.74 KB | 6.76 KB |
| Medium | 1,062.19 us | 931.33 us | 53.17 KB | 51.94 KB |
| Large | 19,789.37 us | 18,884.40 us | 266.88 KB | 261.61 KB |

This benchmark also confirmed that the earlier aggregate `CoreLintBenchmark` was not suitable for attributing `disable-step` cost directly, because its synthetic workflows did not contain inline suppression directives.

## Review Focus

- API wording is intuitive: `disable-step` names the scope, while placement selects the next step.
- `disable-next-line` remains a physical-line directive and keeps backward compatibility.
- Tests cover valid suppression, invalid placement, comment/blank-line skipping, merge behavior, composite action steps, non-step sequence items, representative summary source, and no unused-suppression warnings.
- Review fix: step binding now checks the actual nearest sequence item line for the AST step scope, so a directive before another YAML sequence item such as `services.*.ports` cannot suppress a later step.
- Review fix: step item line lookup is cached in `StepScope`; this avoids per-directive/per-scope source rescans when a file contains multiple `disable-step` directives.
- Review fix: `disable-step` scope construction now reuses the per-run `LintConfig.GetLineStarts()` cache instead of allocating a fresh line-start array for each lint run that contains the directive.
- Review fix: step item line lookup skips deeper nested sequence items (for example a list under `with:`) and binds to the nearest owning step item, while still handling block scalar diagnostic ranges.
- Review fix: per-diagnostic step suppression lookup now uses an allocation-free binary search over source-ordered step scopes instead of scanning all steps in reverse for every diagnostic.
