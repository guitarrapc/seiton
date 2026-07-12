# Lint Rule Implementation Guidelines

Agent instructions for adding or modifying lint rules under `src/Seiton.Core/Linting/Rules/`.

## Before You Start

1. Read `.claude/skills/test-first-development/SKILL.md` — test-first is mandatory for all `src/` changes.
2. Read `.claude/skills/performance-requirements/SKILL.md` — lint rules run per-event/job/step; allocation and caching rules apply.
3. Skim an existing rule similar to what you are building (see [Rule Archetypes](#rule-archetypes)).
4. If the rule changes observable behavior, plan spec updates per `.claude/skills/document-spec-policy/SKILL.md`.

## Architecture Overview

```
LintEngine.Check()
  → WorkflowVisitor traverses AST
  → each IRule receives Visit* callbacks
  → RuleBase collects Diagnostic instances
  → (optional) OnlineAuditEngine resolves uses: refs and calls IOnlineRule.EvaluateTarget
```

Rules are **visitor passes** (`IPass` / `IRule`). They read the parsed AST through readonly-struct Ref facades (`WorkflowRef` / `JobRef` / `StepRef` / `EventRef` / `StringRef`, list/map refs) received in `Visit*` callbacks, emit diagnostics through `RuleBase` helpers, and must not re-parse YAML.

| Component | Role |
|-----------|------|
| `RuleBase` | Diagnostic helpers, `Config` access, location builders, default no-op visitor hooks |
| `OnlineRuleBase` | Collects `uses:` targets during traversal; evaluation deferred to `OnlineAuditEngine` |
| `RuleCatalog` | Factory registration, priority, opt-in policy, default severity, auto-fix metadata, allowed config keys |
| `RuleId` + `RuleIdExtensions` | Strongly typed ID and stable kebab-case string (`job-structure`, etc.) |
| `WorkflowVisitor` | `VisitWorkflowPre/Post`, `VisitEvent`, `VisitJobPre/Post`, `VisitStep` |

## Rule Archetypes

Pick the closest existing rule and follow its shape.

| Archetype | When to use | Examples |
|-----------|-------------|----------|
| **Step/job visitor** | Check a single AST field on `VisitStep` or `VisitJobPre` | `DenyReadAllRule`, `ShellNameRule` |
| **Job post visitor** | Per-job checks after all steps; simulate step order | `BackgroundStepsRule` |
| **Workflow-wide state** | Cross-job validation; reset state in `VisitWorkflowPre` | `IdNamingRule`, `ConcurrencyLimitsRule` |
| **Expression analysis** | Parse `${{ }}` via `Config.ParseExpression()` | `ExprUndefinedVarRule`, `IfCondRule` |
| **Configurable policy** | Read `rules.<id>.*` keys in `SetConfig` | `ForbiddenUsesRule`, `DangerousTriggersRule` |
| **Shared analyzer** | Logic reused by multiple rules | `RunContextDirectUseAnalyzer`, `BackgroundStepFlowAnalyzer` |
| **Data-driven** | Lookup tables from `data/` via `Seiton.Update` generators | `UnpinnedToolsRule`, `PopularActionInputsRule` |
| **Online audit** | Needs GitHub API / advisory data for `uses:` refs | `KnownVulnerableActionsRule`, `StaleActionRefsRule` |
| **Auto-fix** | Attach `DiagnosticFix` with `TextEdit[]` | `IfExprWrapperRule`, `IdNamingRule` |

### Local vs Online

- **Local rule** (`RuleBase`): synchronous AST pass. Register in `RuleCatalog.DefaultRuleFactories`.
- **Online rule** (`OnlineRuleBase` / `IOnlineRule`): collects `ActionAuditTarget` during traversal; `OnlineAuditEngine.AuditAsync` resolves refs and calls `EvaluateTarget`. Register in `RuleCatalog.OnlineRuleFactories`. **Always opt-in** (disabled by default).

### Default-on vs Opt-in

- **Default-on** (`OptIn: false` in `DefaultRuleFactories`): runs without config. Use for generally useful, low-cost checks.
- **Opt-in** (`OptIn: true`): user must add `rules.<id>.enabled: true` in `.github/seiton.yaml`. Use for expensive, noisy, or policy-specific rules (`concurrency-limits` is the current default-local opt-in example).

Online rules are always opt-in regardless of the `OptIn` flag on local entries.

## Implementation Checklist

Work in this order. Do not skip tests or catalog registration.

### 1. Define the rule contract (spec / issue)

Decide before coding:

- **Rule ID** (kebab-case, stable forever once shipped)
- **Default severity**: `error`, `warning`, `info`, or `mixed` (when the rule emits multiple severities)
- **Document kinds**: workflow only, action metadata only, or both (`SupportsDocumentKind`)
- **Activation**: default-on or opt-in
- **Config keys**: any `rules.<id>.<key>` options
- **Auto-fix**: yes/no
- **Diagnostic position policy**: which `TextRange` is reported (value vs key vs expression body)

### 2. Write failing tests first (Red)

Add `tests/Seiton.Core.Tests/RuleInterfaceTests.<YourRule>Rule.cs` as a `partial` of `RuleInterfaceTests`.

Standard table-driven pattern:

```csharp
[Test]
public async Task RuleRegression_MyRule_TableDriven()
{
    var cases = new[]
    {
        new RuleCase("ok-valid", """...""", []),
        new RuleCase("ng-violation", """...""", ["expected message fragment"]),
    };
    await AssertRuleCases(new MyRule(), "my-rule-id", cases);
}
```

Conventions:

- Case names: `ok-*` (no diagnostics), `ng-*` (expect diagnostics)
- `ExpectedSubstrings`: match `Diagnostic.Message` fragments (not full golden output)
- Pass `LintConfig` as 4th argument when testing rule-specific config
- For security / classification rules: **negative cases ≥ positive cases** (see test-first skill equivalence-class section)
- Reuse `AssertRuleCases` / `RuleCase` from an existing `RuleInterfaceTests.*.cs` file (copy the private helpers into your new file if needed)

Run the failing test:

```shell
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/RuleRegression_MyRule*
```

### 3. Implement the rule class (Green)

Create `src/Seiton.Core/Linting/Rules/<Name>Rule.cs`:

```csharp
public sealed class MyRule() : RuleBase(RuleId.MyRule)
{
    public override string Name => "My Rule";

    public override void VisitStep(StepRef step) { /* ... */ }
}
```

Implementation rules:

- **Subclass `RuleBase`** (or `OnlineRuleBase` for online rules). Do not implement `IRule` directly.
- **Primary constructor** with `RuleBase(RuleId.X)` — matches existing rules.
- **`sealed`** unless extension is required.
- **Guard early**: `if (Config.Utf8Yaml is null) return;` when reading source bytes.
- **Skip dynamic values**: if a field contains `${{ }}`, skip or handle explicitly (see `ExpressionScanHelpers`).
- **Read values through Refs**: absence is `HasValue == false` (default refs chain safely); exec dispatch is `step.Exec.Kind == StepExecKind.Action` + `step.Exec.AsAction()`; string values via `StringRef.Value` (UTF-8 span) / `.Slice` / `.ValueEquals("..."u8)`; maps via `TryGetValue(keySpan, out ...)`. Use `.Decode()` only when building diagnostic messages.
- **Emit via `Add*Error` / `Add*Warning` / `Add*Info`**: never construct `Diagnostic` directly in rules.
- **Messages are single-line**: `RuleBase` collapses embedded newlines; still avoid putting block-scalar content raw into messages when possible.
- **Locations must be actionable**: point at the YAML token the user should edit (see `Seiton_Linter_spec.md` §4.5 for intentional divergences).

Override `SupportsDocumentKind` when the rule applies to only one document type:

```csharp
public override bool SupportsDocumentKind(DocumentKind documentKind)
    => documentKind == DocumentKind.Workflow;
```

Override `SetConfig` when reading per-rule options:

```csharp
public override void SetConfig(LintConfig config)
{
    base.SetConfig(config);
    var ruleConfig = config.GetRuleConfig(Id);
    // read ruleConfig?.Events, etc.
}
```

### 4. Register the rule

Update **all** of the following in the same change:

| File | What to add |
|------|-------------|
| `Linting/RuleId.cs` | New enum member (before `Syntax` if catalog rule) |
| `Linting/RuleIdExtensions.cs` | `ToId()` case → kebab-case string |
| `Linting/RuleCatalog.cs` | `DefaultRuleFactories` or `OnlineRuleFactories` entry with **unique** `Priority` |
| `Linting/RuleCatalog.cs` | `GetDefaultSeverity()` case |
| `Linting/RuleCatalog.cs` | `GetSupportsAutoFix()` case (if applicable) |
| `Linting/RuleCatalog.cs` | `BuildAllowedRuleConfigKeys()` case (if rule has config keys) |

Priority rules:

- Lower number runs first.
- Priorities **29–32** are reserved for online rules.
- Duplicate priorities throw at startup — pick the next free integer.

If adding rule-specific config keys:

1. Add flag to `Linting/RuleKeyFlags.cs` (if new key type).
2. Add row to `LintConfigYamlParser.RuleKeyFlagEntries`.
3. Add `case` in `LintConfigYamlParser.AddRule()`.
4. Map allowed keys in `RuleCatalog.BuildAllowedRuleConfigKeys()`.

### 5. Update catalog tests

| File | Update |
|------|--------|
| `tests/Seiton.Core.Tests/RuleInterfaceTests.cs` | `RuleCatalog_DefaultRules_MatchDocumentedScope` count and order assertions |
| `tests/Seiton.Core.Tests/RuleCatalogDescriptorTests.cs` | Total descriptor count (`57 + 4 = 61` → increment) |

### 6. Run full verification

```shell
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/RuleRegression_MyRule*
dotnet test
cd src/Seiton.Benchmark && dotnet run -c Release
```

Benchmark gate (lint changes): `CoreLintBenchmark` Mean and Allocated must not regress more than **+10%**.

### 7. Update documentation

Per `.github/docs/docs_authoring_guidelines.md`, new rules require **three** doc touchpoints:

1. `.github/docs/Seiton_Linter_spec.md` §4.4 rule catalog table
2. `.github/docs/Seiton_Linter_csharp_spec.md` rule table (if C#-specific notes apply)
3. `docs/rules.md` — full user-facing section (Summary / Why / Remediation / When fixing)

Also update when counts are listed:

- `.github/docs/feature_matrix.md` rule count and list
- `docs/rules.md` CLI example table at the top (if maintained manually)

Do **not** put step-by-step implementation HOW in specs; keep WHAT/WHY there, HOW here.

## Visitor Hook Selection

| Hook | Use when |
|------|----------|
| `VisitWorkflowPre` | Initialize per-file state; workflow-level keys (`on:`, `concurrency:`, `permissions:`) |
| `VisitWorkflowPost` | Cross-job checks needing full job list |
| `VisitEvent` | Trigger configuration (`on: push`, `schedule`, etc.) |
| `VisitJobPre` | Job keys before steps (`runs-on`, `needs`, reusable `uses:`) |
| `VisitJobPost` | After all steps; cross-step flow within a job (`wait`/`cancel` refs, concurrent background peak) | `BackgroundStepsRule` |
| `VisitStep` | Step-level `uses:`, `run:`, `if:`, `env:` |
| `VisitActionMetadataPre/Post` | Composite action `action.yml` structure |

`WorkflowVisitor` resets diagnostics on each `RuleBase` at the start of `VisitWorkflowPre`.

## Diagnostic Conventions

### Severity

Choose the default in `RuleCatalog.GetDefaultSeverity` to match the most common emitted severity. Use `mixed` when the rule routinely emits both errors and warnings.

Emit helpers:

- `AddStepError`, `AddJobError`, `AddWorkflowError`, `AddEventError` → error
- `AddStepWarning`, `AddJobWarning`, … → warning
- `AddStepInfo`, … → info (verbose / informational only)

Users can override severity via config; the rule still emits using the semantic helper matching the violation type.

### Messages

- Stable, grep-friendly wording; existing rules are the style guide.
- Include the offending value in quotes when it aids debugging.
- For repeated identical messages across many steps, consider message deduplication (see performance skill §9).

### Locations

- Prefer `StringRef.Range` (or `.Expression.Range`) or specialized builders: `BuildUsesLocation`, `BuildStepLocation`, `BuildJobLocation`.
- For expressions inside `run:` scripts, use `Config.GetLineStarts()` and offset math (see `RunContextDirectUseAnalyzer`).
- Attach `help:` for non-obvious remediations.

### Auto-fix

When the rule can suggest safe edits:

1. Build `TextEdit` with byte offsets into `Config.Utf8Yaml`.
2. Pass `new DiagnosticFix(description, edits)` to an `Add*` overload that accepts `DiagnosticFix`.
3. Set `GetSupportsAutoFix` to `true` in `RuleCatalog`.
4. Add fix tests (see `RuleInterfaceTests.IfExprWrapperRule.cs`, `CheckoutPersistCredentialsRule.cs`).
5. Document side effects in `docs/rules.md` **When fixing**.

Quote-aware edits: when replacing a scalar, expand range to include YAML quotes if needed (see `IdNamingRule.BuildSliceReplacementEdit`).

## Performance Requirements (Lint-Specific)

Hot-path rules (per-step or per-expression) must follow:

| Do | Don't |
|----|-------|
| `Config.ParseExpression(expr)` | `ExpressionParser.Parse()` directly |
| `Config.GetLineStarts()` | `BuildLineStarts()` per call |
| `pair.Key.ToUtf8StringZeroCopy(utf8Yaml)` | `new Utf8String(span)` in loops |
| `ReadOnlySpan<byte>` comparisons (`"latest"u8`) | Allocating strings for comparisons |
| `stackalloc` / `ArrayPool` for scratch buffers | `new byte[]` / `new List<T>` per step |
| Static `readonly` for repeated UTF-8 literals | `.ToArray()` on literals in loops |

If a rule is inherently expensive (deep analysis, IO, network), make it **opt-in** rather than default-on.

Per-run state: use instance fields reset in `VisitWorkflowPre`, not static mutable state.

## Shared Logic

Before duplicating logic across rules:

- **Action ref parsing**: `ActionRefHelpers`
- **Run script context access**: `RunContextDirectUseAnalyzer`
- **Config list normalization**: `RuleConfigHelpers.BuildNormalizedSet`
- **Expression typing**: `ExpressionSemanticModel` (after `Config.ParseExpression`)

Extract a `*Analyzer` static class when **three or more** rules share non-trivial logic.

## Data-Driven Rules

When the rule depends on external datasets (action metadata, advisory lists, etc.):

1. Add pipeline under `src/Seiton.Update/` (see `update-pipeline` skill).
2. Store sources in `data/sources/<dataset>/`.
3. Generate lookup code into `src/Seiton.Core/Generated/`.
4. Keep the rule class thin — iterate AST, lookup generated data, emit diagnostics.

## Online Rule Extras

1. Extend `OnlineRuleBase` (override `EvaluateTarget`, optionally customize collection).
2. Register in `OnlineRuleFactories` with priority 29–32.
3. Add tests in `tests/Seiton.Core.Tests/OnlineAuditEngineTests.cs`.
4. Document network / token requirements in `docs/rules.md`.

`OnlineRuleBase` skips local paths (`./`), `docker://`, and unparseable refs automatically.

## Suppression and Exclusions

Rules participate in the standard suppression model without extra code:

- `# seiton: disable-next-line <rule-id>`
- `# seiton: disable-job <rule-id>`
- `.github/seiton.yaml` exclusions

Add suppression tests in `RuleInterfaceTests.Suppression.cs` when the rule has non-obvious span boundaries (e.g. diagnostics inside job bodies vs step keys).

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Registered in `RuleId` but not `RuleCatalog` | Rule never runs |
| Forgot `RuleIdExtensions.ToId()` | Config / CLI cannot reference the rule |
| Duplicate priority | Startup `InvalidOperationException` |
| `SupportsDocumentKind` too broad | False positives on `action.yml` |
| Strings allocated per step in tight loop | Benchmark regression |
| Missing negative tests | False positives ship to users |
| Changed rule ID after release | Breaking change — requires migration notes |
| `GetSupportsAutoFix` true but no `DiagnosticFix` | Misleading `seiton rules` output |

## Quick Reference: Files Touched by a New Rule

```
src/Seiton.Core/Linting/
  RuleId.cs
  RuleIdExtensions.cs
  RuleCatalog.cs
  RuleKeyFlags.cs              (if new config keys)
  LintConfigYamlParser.cs      (if new config keys)
  Rules/<Name>Rule.cs          (implementation)
  Rules/<Name>Analyzer.cs      (optional shared logic)

tests/Seiton.Core.Tests/
  RuleInterfaceTests.<Name>Rule.cs
  RuleInterfaceTests.cs          (catalog order/count)
  RuleCatalogDescriptorTests.cs
  OnlineAuditEngineTests.cs    (online rules only)

.github/docs/
  Seiton_Linter_spec.md
  Seiton_Linter_csharp_spec.md
  feature_matrix.md

docs/rules.md
```

## Related Skills

- `test-first-development` — red-green workflow, equivalence classes, benchmarks
- `performance-requirements` — allocation and caching patterns
- `document-spec-policy` — what belongs in `.github/docs/`
- `update-pipeline` — data-driven rule datasets
