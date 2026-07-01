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
- Avoid string decoding for step matching; use line/range metadata from AST and existing rule-id normalization.
- No parser changes are planned.

## Verification Results

- Red tests were added before production code for step-scope suppression, following-step isolation, directive merging, invalid placement, composite action steps, and non-step sequence items.
- Targeted disable-step tests: `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/DisableStep_*` passed, 6 tests.
- Rule interface regression tests: `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/*` passed, 518 tests.
- Full suite was run after documentation mirror sync and showed all test assemblies passed; the process did not return promptly after assembly-level pass output on that run.

Final `CoreLintBenchmark` after lazy step-scope construction and indentation-aware step binding:

| Size | FixEnabled | Baseline Mean | Final Mean | Baseline Allocated | Final Allocated |
|---|---:|---:|---:|---:|---:|
| Small | False | 288.3 us | 142.6 us | 9.89 KB | 9.75 KB |
| Small | True | 192.3 us | 367.7 us | 11.2 KB | 11.35 KB |
| Medium | False | 3,739.6 us | 2,490.7 us | 52.91 KB | 52.91 KB |
| Medium | True | 4,894.2 us | 3,975.1 us | 66.13 KB | 66.27 KB |
| Large | False | 50,190.3 us | 46,260.8 us | 256.86 KB | 250.22 KB |
| Large | True | 76,443.6 us | 58,689.7 us | 317.51 KB | 321.78 KB |

BenchmarkDotNet ShortRun timings are noisy on this machine, but allocation stayed within the +10% threshold for every scenario. The only allocation increase in the large fix-enabled case is about +1.3%, and documents without `disable-step` do not eagerly build step scopes.

## Review Focus

- API wording is intuitive: `disable-step` names the scope, while placement selects the next step.
- `disable-next-line` remains a physical-line directive and keeps backward compatibility.
- Tests cover valid suppression, invalid placement, comment/blank-line skipping, merge behavior, composite action steps, non-step sequence items, representative summary source, and no unused-suppression warnings.
- Review fix: step binding now checks the actual nearest sequence item line for the AST step scope, so a directive before another YAML sequence item such as `services.*.ports` cannot suppress a later step.
