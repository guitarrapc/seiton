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
- Full suite was run after documentation mirror sync and showed all test assemblies passed; the process did not return promptly after assembly-level pass output on that run.

Final `CoreLintBenchmark` after the review pass, lazy step-scope construction, cached step item line lookup, and indentation-aware step binding:

| Size | FixEnabled | Baseline Mean | Final Mean | Baseline Allocated | Final Allocated |
|---|---:|---:|---:|---:|---:|
| Small | False | 288.3 us | 338.8 us | 9.89 KB | 9.88 KB |
| Small | True | 192.3 us | 223.1 us | 11.2 KB | 11.2 KB |
| Medium | False | 3,739.6 us | 11,349.5 us | 52.91 KB | 53.48 KB |
| Medium | True | 4,894.2 us | 6,644.5 us | 66.13 KB | 66.84 KB |
| Large | False | 50,190.3 us | 56,502.3 us | 256.86 KB | 233.77 KB |
| Large | True | 76,443.6 us | 81,744.6 us | 317.51 KB | 288.61 KB |

BenchmarkDotNet ShortRun timings were noisy on this machine, especially the Medium/False case (`Error = 93,460.4 us`, `StdDev = 5,122.88 us`, `RatioSD = 0.82`). Allocation stayed within the +10% threshold for every scenario. The largest allocation increase was Medium/False at about +1.1%, while large scenarios allocated less than the baseline. Documents without `disable-step` do not eagerly build step scopes.

## Review Focus

- API wording is intuitive: `disable-step` names the scope, while placement selects the next step.
- `disable-next-line` remains a physical-line directive and keeps backward compatibility.
- Tests cover valid suppression, invalid placement, comment/blank-line skipping, merge behavior, composite action steps, non-step sequence items, representative summary source, and no unused-suppression warnings.
- Review fix: step binding now checks the actual nearest sequence item line for the AST step scope, so a directive before another YAML sequence item such as `services.*.ports` cannot suppress a later step.
- Review fix: step item line lookup is cached in `StepScope`; this avoids per-directive/per-scope source rescans when a file contains multiple `disable-step` directives.
