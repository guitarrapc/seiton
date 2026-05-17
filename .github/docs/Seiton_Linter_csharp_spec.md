# Seiton Linter C# Implementation Specification

> C# implementation specification for the linter contract defined in `Seiton_Linter_spec.md`. This document captures C# runtime structures and behavior for rule execution, exclusion/suppression, and diagnostic output. See `Seiton_Linter_go_spec.md` for the Go target. Both language specs share the same outline; only language-specific content differs. Parser behavior is specified in `Seiton_Parser_spec.md` and `Seiton_Parser_csharp_spec.md`.

> **Cross-document synchronization rule**: `Seiton_Linter_spec.md` is the source of truth. When this C# spec is updated, also review and update `Seiton_Linter_spec.md`, `Seiton_Linter_go_spec.md`, and `linter_implementation_csharp_plan.md` in the same PR/commit scope.

---

## 0. C# Preamble

### 0.1 Contract

This document defines the C# implementation contract for linter behavior under `Seiton_Linter_spec.md`.

In scope:

- `LintEngine` orchestration in C#
- `IPass` / `IRule` callbacks and traversal integration
- Rule configuration and exclusion/suppression application model
- Suppression observability output model in C# result types

Out of scope:

- YAML parse algorithm details
- AST schema definitions

### 0.2 Overview

The Seiton Linter C# implementation provides:

1. Input document kind classification and parse-first lint entrypoint (`LintEngine.Check`)
2. Visitor/pass traversal for workflow/event/job/step callbacks
3. Rule pack orchestration via `RuleCatalog`
4. Deterministic diagnostics post-processing (sort/dedup/filter)
5. Exclusion/suppression application and observability output (contract-driven)

### 0.3 Structure

Representative implementation surface:

- `LintEngine`
- `LintConfig`
- `LintResult`
- `WorkflowVisitor`
- `IPass`
- `IRule`
- `RuleCatalog`
- `RuleDescriptor`
- `RuleStatus`
- `RuleListResolver`

Current public ergonomics note:

- `LintResult` mirrors `ParseResult` value-resolution helpers for external callers (`GetString(StringNodeId)`, `GetString(Utf8Slice)`, `GetUtf8`, `GetBool/GetInt/GetFloat`, `GetRange`, copy methods).
- `RuleBase` exposes protected scalar-resolution helpers with the same public-type vocabulary so external custom rules can resolve `StringNodeId` / `BoolNodeId` / `IntNodeId` / `FloatNodeId` without any `AstArena` access.
- `AstArena` itself is internal implementation detail and not part of the public extension contract.

### 0.4 Runtime Model

Linter runtime assumes parser output as structural input and never reparses YAML structure.

- Parse result consumed first
- Rule traversal performed next
- Diagnostics collected, post-processed, and filtered by linter policies

### 0.5 Design

1. Keep parser/linter responsibility boundary strict.
2. Keep lint output deterministic for identical input/config.
3. Keep rule/exclusion policy behavior aligned with language-agnostic linter contract.
4. Keep implementation status synchronized with `.github/docslinter_implementation_csharp_plan.md`.

---

## 1. C# Runtime Surface

Primary types:

- `LintEngine`
- `LintConfig`
- `LintResult`
- `WorkflowVisitor`
- `IPass`
- `IRule`
- `RuleCatalog`
- `RuleDescriptor`
- `RuleStatus`
- `RuleListResolver`

Current implementation status should be tracked against `.github/docslinter_implementation_csharp_plan.md`.

---

## 2. Entry Point Mapping

Shared contract (`Seiton_Linter_spec.md` §2):

```
Check(utf8Yaml, filePath) -> LintResult
```

C# mapping:

- `LintEngine.Check(byte[] utf8Yaml, string filePath)`

Normative behavior follows `Seiton_Linter_spec.md` for:

- parser kind classification/routing
- parse-first flow
- fatal parse short-circuit
- rule execution
- deterministic post-processing

Current C# routing note:

- `LintEngine.Check` uses parser kind classification and executes rule traversal with per-rule kind filtering.
- Each `IRule` declares document-kind applicability, and `LintEngine` activates only rules that support the finalized kind.
- `RuleBase` default applicability includes both workflow and action-metadata documents.

Reference runtime shape:

```csharp
public sealed class LintEngine
{
	public LintResult Check(byte[] utf8Yaml, string filePath)
	{
		// 1. Parse(utf8Yaml, filePath) -> ParseResult / internal ParseResultData
		// 2. Construct IRule set
		// 3. WorkflowVisitor.Visit(workflow)
		// 4. Collect diagnostics from each Rule
		// 5. FilterErrors -> Sort + Dedup -> Output
	}
}
```

### 2.1. Multi-File Parallel Execution

Shared contract reference: `Seiton_Linter_spec.md` §2.1.

C# implementation:

- `CheckCommand` dispatches files via `Parallel.For` with `MaxDegreeOfParallelism = Environment.ProcessorCount`.
- Each worker thread owns an independent `LintEngine` instance via `ThreadLocal<LintEngine>`. No engine state is shared across threads.
- Results are written to a pre-allocated `FileCheckResult[]` slot array indexed by file position, guaranteeing deterministic output order.
- Each worker calls `CopyDiagnostics()` to create caller-owned diagnostic copies that survive engine reuse.
- Sequential fast path: when `resolvedFiles.Length <= 1`, input is stdin, or `Environment.ProcessorCount <= 1`, a single `LintEngine` is used without `Parallel.For`.

---

## 3. Pass/Rule Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §4.1, §4.2, §4.3

C# mapping:

- `IPass` callbacks
- `WorkflowVisitor.Visit(Workflow)` traversal order
- `IRule : IPass` + `SetConfig` + `GetDiagnostics`

### 3.1 Pass Interface

```csharp
public interface IPass
{
	void VisitWorkflowPre(Workflow workflow);
	void VisitWorkflowPost(Workflow workflow);
	void VisitEvent(Event ev);
	void VisitJobPre(Job job);
	void VisitJobPost(Job job);
	void VisitStep(Step step);
}
```

### 3.2 Visitor

```csharp
public sealed class WorkflowVisitor
{
	private readonly List<IPass> _passes = new();

	public void AddPass(IPass pass) => _passes.Add(pass);

	public void Visit(Workflow workflow)
	{
		foreach (var pass in _passes)
			pass.VisitWorkflowPre(workflow);

		foreach (var ev in workflow.On)
		{
			foreach (var pass in _passes)
				pass.VisitEvent(ev);
		}

		foreach (var (_, job) in workflow.Jobs)
		{
			foreach (var pass in _passes)
				pass.VisitJobPre(job);

			if (job.Steps is not null)
			{
				foreach (var step in job.Steps)
				{
					foreach (var pass in _passes)
						pass.VisitStep(step);
				}
			}

			foreach (var pass in _passes)
				pass.VisitJobPost(job);
		}

		foreach (var pass in _passes)
			pass.VisitWorkflowPost(workflow);
	}
}
```

Traversal order:

```
VisitWorkflowPre(workflow)      // all passes
	for each event in workflow.On:
		VisitEvent(event)       // all passes
  for each job:
	VisitJobPre(job)            // all passes
	for each step:
	  VisitStep(step)           // all passes
	VisitJobPost(job)           // all passes
VisitWorkflowPost(workflow)     // all passes
```

### 3.3 Rule Interface

```csharp
public interface IRule : IPass
{
	string Id { get; }
	string Name { get; }
	bool SupportsDocumentKind(DocumentKind documentKind);
	Diagnostic[] GetDiagnostics();
	void SetConfig(LintConfig config);
}
```

Each rule inspects AST nodes in `IPass` callbacks and accumulates diagnostics internally.

### 3.4 Current C# Default Rule Pack

The current default rule scope in C# is:

| Rule ID | Required Behavior Summary |
|---|---|
| `job-structure` | Validate core job shape constraints: `uses` is mutually exclusive with `steps`/`runs-on`, and each job requires either reusable-call form (`uses`) or executable form (`runs-on` + `steps`). |
| `reusable-workflow` | Validate reusable workflow call semantics: `with`/`secrets` require `uses`, reusable-call jobs must reject incompatible execution keys, and local reusable calls should validate caller `with`/`secrets` against called workflow `on.workflow_call` contracts when statically resolvable. |
| `permissions` | Validate `permissions` value domain: scalar must be `read-all` or `write-all`; scope values must be `read`, `write`, or `none`. Valid scalar values (`read-all`, `write-all`) emit a warning recommending explicit per-scope mapping; workflow-level warning additionally suggests moving to job-level permissions. |
| `popular-action-inputs` | Validate known action input names against maintained popular-action metadata and emit diagnostics for unknown inputs. |
| `outdated-action-runner` | Error when a popular action's `runs.using` runtime is deprecated. Catalog-driven: looks up the action in `PopularActions` generated catalog, reads `GetRunsUsing()`, and checks against a maintained list of deprecated runtimes (`node12`, `node16`). |
| `unpinned-uses` | Warn when `uses:` references are not pinned to full commit SHA for remote actions/reusable workflows; additionally validate `uses` reference format and local action reference sanity where statically resolvable. |
| `unpinned-image` | Warn when docker image references (`docker://`, `job.container.image`, `job.services.*.image`) are not pinned by digest (`@sha256:<64-hex>`). |
| `dangerous-triggers` | Warn when dangerous trigger events are used (built-in dangerous event set plus any additive customization defined by config). |
| `job-permissions-required` | Warn when a job omits explicit `permissions` configuration. |
| `needs-graph` | Error on invalid `needs` graph: unknown dependency targets and circular dependencies. Cycle diagnostics report at the `needs` value position that closes the cycle, with the full cycle path in the message. |
| `shell-name` | Error when configured shell names are outside the supported shell set for workflow/job defaults and `run` steps. |
| `runner-label` | Warn on unknown GitHub-hosted runner labels in `runs-on` (excluding self-hosted and expression-only cases), using built-in labels plus additive config labels. Error on conflicting OS families among static labels and matrix-expanded expression labels. Bare self-hosted preset OS labels (`linux`, `windows`, `macos`) are recognized for OS family detection. |
| `runner-no-latest` | Warn when moving GitHub-hosted labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used in `runs-on`; prefer explicit version-pinned labels. |
| `id-naming` | Error when `job.id` or `step.id` contains characters outside allowed identifier set. |
| `glob-pattern` | Error on invalid event filter configuration, including invalid glob syntax (triple-star, unclosed bracket, reversed range, `*+` sequences), ref-name forbidden characters (`^`, `~`, `:`, space), single-dot and double-dot path segments, unsupported event options/types, and incompatible filter combinations (`branches` vs `branches-ignore`, `tags` vs `tags-ignore`, `paths` vs `paths-ignore`). |
| `deny-write-all` | Error when workflow/job permissions use `write-all`. |
| `credentials` | Warn when custom/private registry images in `job.container` or `job.services.*` are used without credentials, except registries treated as public by built-in plus additive config set. Error when `credentials.password` is a hardcoded literal instead of an expression (`${{ ... }}`). |
| `template-injection` | Error when untrusted `github.event`-origin data is directly interpolated into `run` script sinks or `actions/github-script` `script` input in unsafe ways. `env:` declarations are treated as indirection and are not reported by this rule. |
| `expr-undefined-var` | Error when expressions reference context roots unavailable in the current scope (for example job scope vs step scope context mismatch). Validates `step.run`, `step.if`, `step.env`, and `step.with` expressions. For `matrix` context, builds strict per-job types from matrix row definitions (including nested object property inference, array row detection, and scalar row detection) and flags undefined axis keys. For `steps` context, builds strict per-job types from step IDs and validates forward references. For `needs` context, validates that referenced job IDs are declared in the job's `needs` list. For popular actions with known outputs, builds strict step output types and flags unknown output names. For local actions (`uses: ./...`), resolves `action.yml`/`action.yaml` metadata via `LocalActionOutputResolver` to build strict step output types and flags unknown output property names. For local reusable workflow call jobs (`uses: ./.github/workflows/...` at job level), resolves the called workflow's `on.workflow_call.outputs` via `LocalReusableWorkflowOutputResolver` to build strict needs output types and flags unknown output names. For remote reusable workflow call jobs (`uses: owner/repo/path@ref` at job level), `needs.<job>.outputs.*` is treated as loose (non-strict) because the called workflow's outputs cannot be determined statically without fetching the remote definition. Template type checks (`CheckTemplateType` / `CheckTemplateTypeWithOverrides`) warn when `${{ }}` interpolation evaluates to object (`"[Object]"`), array (`"[Array]"`), or null (empty string); the override-aware variant uses dynamic context types for matrix/inputs/needs/steps so that e.g. `${{ matrix.bar }}` where `bar` is an array axis is correctly flagged. Env mapping type checks (`CheckEnvMappingType`) warn when `env: ${{ expr }}` evaluates to a non-object type (string, number, bool, array, null). Index access type checks (`ValidateIndexAccess` / `ValidateIndexAccessWithOverrides`) error when the index expression type is incompatible with the container type (e.g. boolean index on object, string index on array); the override-aware variant resolves dynamic context types so that e.g. `env[inputs.verbose]` where `verbose` is boolean is correctly flagged. |
| `run-env-context-direct-use` | Error when `run:` script text directly references `${{ env.* }}`; shell variable expansion must be used instead. |
| `run-secrets-context-direct-use` | Error when `run:` script text directly references `${{ secrets.* }}`; secret values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `run-inputs-context-direct-use` | Error when `run:` script text directly references `${{ inputs.* }}` or `${{ github.event.inputs.* }}`; values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `secrets-whole-context-access` | Error when any expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`, `${{ format('{0}', secrets) }}`), rather than accessing a specific secret key (`secrets.MY_KEY`). Checked in `run:`, `env:`, and `with:` sinks at step and job level. |
| `checkout-persist-credentials` | Warn when `actions/checkout` does not explicitly set `with.persist-credentials: false`; persisting credentials in `.git/config` increases secret exposure risk when repository data is reused or uploaded. |
| `workflow-secrets` | Error when workflow-level `env` assigns values from `secrets.*` or `github.token` in workflows with multiple jobs. |
| `job-secrets` | Error when job-level `env` assigns values from `secrets.*` or `github.token` in jobs with multiple steps. |
| `action-shell-is-required` | Error when a composite action `run` step omits explicit `shell` declaration (including empty shell values). This rule is scoped to action-metadata documents. |

Scope notes:

- Parser diagnostics remain primary for YAML shape and required-key errors.
- Rule diagnostics add policy and metadata checks over parsed AST.
- `LintEngine` defaults to `RuleCatalog.CreateDefaultRules()` for local rules and `RuleCatalog.CreateOnlineRules()` for network-assisted rules, applies priority sort, then deduplicates identical diagnostics.
- Network-assisted rule IDs (`known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs`) are registered in `RuleCatalog` with `IOnlineRule` factories. They extend `OnlineRuleBase` (which extends `RuleBase`) and participate in `WorkflowVisitor` traversal to collect `ActionAuditTarget` references. Post-traversal, `OnlineAuditEngine.AuditAsync` resolves targets asynchronously and calls `EvaluateTarget` on each rule. These rules are opt-in (disabled by default; enabled via `rules.<rule-id>.enabled: true`). **`OnlineAuditEngine`** accepts **optional** `fix.pinning.ignore-actions` entries (`IReadOnlyList<IgnoreActionEntry>`): patterns use **wildcard matching** (`*` / `?`), not regex. No ReDoS risk. Same semantics as `GitHubActionShaResolver.ShouldSkip`.
- `ActionRefResolution` includes `IsReachable` (bool): true when the commit is either the HEAD of at least one branch or is tagged in the repository's own ref namespace. When `IsTaggedCommit` is false, this is determined via the `branches-where-head` API, which establishes branch-HEAD equality rather than ancestry reachability. The `impostor-commit` rule uses this to detect fork-origin commits that exist in the repository's shared object storage but are not the HEAD of any legitimate branch and are not referenced by a tag.
- Rule ID stability and compatibility policy follow `Seiton_Linter_spec.md` §4.4.

### 3.5 Phase 14 Catalog Additions

The language-agnostic rule catalog includes the following Phase 14 rule IDs.

- `known-vulnerable-actions`
- `impostor-commit`
- `ref-confusion`
- `stale-action-refs`
- `deny-read-all`
- `deny-inherit-secrets`
- `job-timeout-minutes-required`
- `github-app-token-inputs`

Status contract:

- These rule IDs are normative at the shared-spec level.
- C# runtime maps all eight IDs in `RuleCatalog`; `deny-read-all` / `deny-inherit-secrets` / `job-timeout-minutes-required` / `github-app-token-inputs` are default local rules, while the four network-assisted rules are registered as `IOnlineRule` factories (`OnlineRuleFactories`) and participate in visitor traversal + post-traversal async resolution via `OnlineAuditEngine`.

### 3.6 Planned High-Priority Candidate Rules

The shared spec (§13) additionally defines the following high-priority candidate rule IDs.

- `cache-poisoning`
- `self-hosted-runner`
- `unredacted-secrets`
- `secrets-outside-env`
- `matrix`
- `env-var`
- `deprecated-commands`
- `if-cond`
- `archived-uses`
- `insecure-commands`
- `overprovisioned-secrets`
- `forbidden-uses`
- `ref-version-mismatch`
- `use-trusted-publishing`
- `unsound-condition`
- `unpinned-tools`

Status contract:

- `cache-poisoning` / `self-hosted-runner` / `unredacted-secrets` / `secrets-outside-env` are already in the current C# default local rule pack.
- `matrix` / `env-var` / `deprecated-commands` / `if-cond` are already in the current C# default local rule pack.
- `archived-uses` / `insecure-commands` / `overprovisioned-secrets` / `forbidden-uses` / `ref-version-mismatch` / `use-trusted-publishing` / `unsound-condition` / `unpinned-tools` are already in the current C# default local rule pack.
- C# runtime implementation and default-catalog promotion must be synchronized with `.github/docslinter_implementation_csharp_plan.md` and shared-spec catalog updates.

### 3.7 Known Partial Parity Areas (actionlint)

Current C# default local rules are intentionally partial for the following domains.

- `events`: partially covered by `dangerous-triggers` and `glob-pattern`; still missing webhook-specific activity type validation, filter cross-constraint validation, and payload-shape semantic checks.
- `action`: covered by `popular-action-inputs` / `outdated-action-runner` / `unpinned-uses` / `unpinned-tools` / `local-action-inputs` / `expr-undefined-var` (local action outputs). `popular-action-inputs` validates input names against catalog; `outdated-action-runner` flags deprecated `runs.using` runtimes via catalog `GetRunsUsing()`; `unpinned-tools` warns on known setup actions with unpinned `with.version` (action list is data-driven via `data/sources/unpinned-tools/unpinned_tools.json` and code-generated into `UnpinnedToolsActions.g.cs`); `local-action-inputs` validates local action contracts, runner policy, metadata completeness (required `description`, JS `env` prohibition, entry-point file existence, branding forwarding); `expr-undefined-var` resolves local action metadata outputs for strict `steps.<id>.outputs.<name>` validation via `LocalActionOutputResolver`. Still missing full remote-action metadata depth and complete Docker action / uses-format edge-case breadth.
- `workflow-call`: partially covered by `reusable-workflow` / `deny-inherit-secrets`; still missing called-workflow contract validation (`inputs`/`secrets` required/type/default consistency and caller conformance).

These are tracked as next-step parity-hardening items in `.github/docslinter_implementation_csharp_plan.md`.

### 3.8 Rule Catalog Introspection API

Public types for rule catalog introspection (used by `seiton rules` CLI command):

```csharp
// Describes a lint rule's static metadata without requiring instantiation for lint purposes.
public readonly record struct RuleDescriptor(
    string Id,
    string Name,
    bool IsOptIn,
    bool IsOnline,
    bool SupportsWorkflow,
    bool SupportsAction,
    string DefaultSeverity,
    bool SupportsAutoFix);

// Describes a rule's effective enabled state given a configuration.
public readonly record struct RuleStatus(
    RuleDescriptor Rule,
    bool Enabled,
    string Reason);
```

- `RuleCatalog.GetAllRuleDescriptors()` (internal) returns cached `IReadOnlyList<RuleDescriptor>` covering all registered rules (default local + online). Uses `Lazy<RuleDescriptor[]>` for thread-safe one-time initialization. External consumers access rule metadata through the public `RuleListResolver` facade.
- `RuleListResolver.Resolve(LintConfig?)` (public) computes `IReadOnlyList<RuleStatus>` reflecting the effective enabled/disabled state for each rule under the given configuration.
- `DefaultSeverity`: `"error"`, `"warning"`, or `"mixed"` (rule emits diagnostics at multiple severity levels depending on the specific condition).
- `SupportsAutoFix`: `true` when the rule can produce `DiagnosticFix` payloads for at least some of its diagnostics.

Reason values: `"default"`, `"config (enabled)"`, `"config (disabled)"`, `"opt-in (not configured)"`.

---

## 4. Exclusion and Suppression Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5, §6.1, §11

C# implementation must provide:

- config-based exclusion matching
- inline next-line directive handling
- unknown rule-id as configuration error
- severity override application
- suppression observability in `LintResult`

### 4.1 Rule-Specific Configuration Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5.8

C# implementation must support rule-specific configuration within `rules.<rule-id>` entries. Each rule accepts the shared `Enabled` / `Severity` keys plus rule-specific keys.

Additive merge (`effective = built-in U user-extended`) is used for all `extend` lists:

- `rules.dangerous-triggers.events.extend`
- `rules.runner-label.known-hosted-labels.extend`
- `rules.credentials.public-registries.extend`
- `rules.cache-poisoning.untrusted-triggers.extend`
- `rules.self-hosted-runner.untrusted-triggers.extend`
- `rules.unredacted-secrets.output-commands.extend`

Direct list keys:

- `rules.forbidden-uses.allow` / `rules.forbidden-uses.deny`
- `rules.expr-undefined-var.assume-events`

Integer threshold keys (non-negative integer scalar, not a list):

- `rules.overprovisioned-secrets.max-step-env-secrets` — maximum `secrets.*` references in a step `env:` block before warning. Default: `5`.
- `rules.overprovisioned-secrets.max-job-secrets` — maximum explicit secrets in a reusable workflow call `secrets:` block before warning. Default: `5`.

Mapping requirements:

- Use deterministic deduplication after normalization.
- Normalization uses ASCII lower-case matching for event names, runner labels, and registry hosts.
- Invalid customization entries are configuration errors.
- Extension never removes built-in defaults.
- Unknown rule-specific keys for a given rule ID are configuration errors (validated via `RuleCatalog` field mapping).

### 4.1.1 Security Analysis: Current Model vs Discriminated Union

Current model (`RuleConfig` with nullable per-rule fields + runtime allow-list validation) is simple and fast, but has the following security-relevant weaknesses:

- It permits representationally invalid intermediate states in memory (for example, `runner-label` carrying `events`).
- Correctness depends on every call path invoking validation/normalization before rule execution.
- Future code paths that construct `LintConfig` directly can accidentally bypass parser-level key rejection.

Discriminated-union style projection reduces these risks by making post-n‘‘‘alization state rule-shaped:

- Each rule receives a typed payload (`RuleSpecificConfig` derived type) or `None`.
- Mismatch between rule ID and customization shape is rejected during projection.
- Rule implementations can pattern-match on typed payloads instead of reading unrelated nullable fields.

Adopted direction in C# runtime:

- Keep the shared envelope (`Enabled`, `Severity`) in `RuleConfig`.
- Add `RuleConfig.Specific: RuleSpecificConfig` as the authoritative typed payload after normalization.

### 4.1.2 Implemented Typed Payload Contract

The C# runtime defines a discriminated-union style payload hierarchy:

- `DangerousTriggersSpecificConfig`
- `RunnerLabelSpecificConfig`
- `CredentialsSpecificConfig`
- `UntrustedTriggersSpecificConfig`
- `UnredactedSecretsSpecificConfig`
- `ExprUndefinedVarSpecificConfig`
- `ForbiddenUsesSpecificConfig`
- `RuleSpecificConfig.None`

Projection contract:

- `LintConfigLibrary.NormalizeRules` MUST normalize and validate `RuleConfig.Specific` after field validation.
- `LintEngine.NormalizeRules` MUST perform the same normalization for directly supplied in-memory configs.
- Rule implementations MUST consume `RuleConfig.Specific` only (no legacy field fallback).
- External callers that invoke `IRule.SetConfig` directly MUST pass configs produced by `LintEngine` or an equivalent projector/normalizer path; passing raw, unnormalized `RuleConfig` is out of contract.

Security outcome:

- The normalized runtime config consumed by rules is now rule-ID aligned and strongly typed.
- Attack surface from malformed cross-rule customization payloads is reduced to parser diagnostics.

### 4.2 Auto-Fix Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §8

C# runtime mapping for fix-capable diagnostics:

- `Diagnostic` carries optional fix payload (`DiagnosticFix?`)
- `DiagnosticFix` carries human description and one-or-more `TextEdit`
- `TextEdit` uses UTF-8 byte offset/length semantics aligned with `TextRange.Start`/`Length`

Reference shape:

```csharp
public readonly record struct TextEdit(int Offset, int Length, string NewText);

public readonly record struct DiagnosticFix(string Description, TextEdit[] Edits);
```

Implementation requirements:

- Rules attach fix payload only when remediation is deterministic and safe.
- A single diagnostic fix must not contain overlapping edits.
- Cross-diagnostic overlapping edits are conflict cases and must be rejected by fix-application layer.
- Fix application must be independent from `LintEngine.Check` (no in-place mutation during linting).

### 4.3 Fix Engine Formatting Preservation Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §9

C# fix-engine implementation must enforce the following preservation policies:

- Indentation: infer from sibling keys in same mapping scope; fallback to parent + one YAML level.
- Line endings: preserve dominant file style (`LF`/`CRLF`) and use it for inserted lines.
- Quote style: preserve existing quote form for scalar-to-scalar replacement when valid.
- YAML context safety: keep node kind unless rule contract explicitly defines a kind transition.
- Whitespace stability: avoid churn outside edit ranges and never introduce trailing spaces.
- Fallback: when style-safe edit synthesis is ambiguous, do not emit fix.

C# implementation note:

- Quote and range data come from AST (`StringNode.Quoted`, `TextRange`).
- Indentation and line-ending style are recovered from original source bytes/text.

### 4.4 Fix Observability Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §10

C# result model must allow caller-side fix operations:

- enumerate diagnostics that include fix payload
- count fixable diagnostics
- apply selected fixes (single or batch)

`LintResult` remains immutable as lint output; fix application produces separate updated source content.

Dry-run preview mapping:

- `FixEngine` provides unified diff generation from source + selected fixes.
- Preview APIs return diff text and support direct writer output (for CLI standard output use).
- Preview operation does not mutate source bytes.

Representative shape:

```csharp
public static string BuildUnifiedDiff(
	byte[] utf8Yaml,
	IEnumerable<Diagnostic> diagnosticsWithFix,
	string filePath,
	int contextLines = 2);

public static void WriteUnifiedDiff(
	TextWriter writer,
	byte[] utf8Yaml,
	IEnumerable<Diagnostic> diagnosticsWithFix,
	string filePath,
	int contextLines = 2);
```

Output contract:

- Unified diff hunk format with `@@ -a,b +c,d @@`
- Changed-line focused output with configurable context lines
- Deterministic output for identical input bytes + fix selection

### 4.5 Network-Assisted Pin Remediation Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §12

C# implementation mapping for network-assisted pin remediation.

#### 4.5.1 Resolver Interfaces

```csharp
/// <summary>
/// Resolves a GitHub Actions / Reusable Workflow reference to a pinned commit SHA.
/// </summary>
public interface IActionShaResolver
{
    /// <summary>
    /// Resolves owner/repo@ref to (sha40, originalRef).
    /// Returns null for both when the ref is excluded by configuration.
    /// </summary>
    Task<(string? Sha, string? TagComment)> ResolveAsync(
        string owner, string repo, string refStr,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an OCI image reference to a pinned digest.
/// </summary>
public interface IImageDigestResolver
{
    /// <summary>
    /// Resolves imageRef (e.g. "node:20.11.1") to a sha256 digest string.
    /// Returns null when the image is excluded by configuration.
    /// </summary>
    Task<string?> ResolveAsync(
        string imageRef,
        CancellationToken cancellationToken = default);
}
```

C# implementation notes:

- Both interfaces are `async`; resolution may perform network I/O.
- `null` return indicates configuration-based skip (not an error) **or** 404 image-not-found (also not an error — callers should not generate a fix for nonexistent images).
- Implementations must cache successful resolutions in-process for the duration of a single `RemediateAsync` call.
- Error results (non-skip failures) must not be cached.
- Resolver implementations are injected by caller — not instantiated by `LintEngine`.

#### 4.5.1a `OciImageDigestResolver` — Implementation Constraints

`OciImageDigestResolver` is the concrete `IImageDigestResolver` implementation. Key behavioral guarantees:

**Request protocol:**
- Uses `HEAD /v2/{repo}/manifests/{reference}` (never GET manifest). This avoids consuming Docker Hub's pull-rate quota (HEAD counts as an API request, not a pull).
- Sends OCI + Docker manifest `Accept` headers to ensure multi-arch index digests are returned.
- Reads `Docker-Content-Digest` response header for the digest value.

**Authentication order:**
1. If `~/.docker/config.json` (or `$DOCKER_CONFIG/config.json`) contains credentials for the registry host, the stored `auth` value (Basic) is sent on the first request.
2. If no stored credentials exist and the registry returns `401 Unauthorized` with `WWW-Authenticate: Bearer ...`, the anonymous bearer token flow (§12.2.3) is executed:
   - `realm`, `service`, and `scope` are extracted from the challenge header.
   - `GET {realm}?service={service}&scope={scope}` is sent to the auth endpoint.
   - The returned `access_token` (or `token`) is used as a Bearer credential for the manifest HEAD retry.
3. If stored credentials are present and the registry still returns 401 (wrong password, expired credentials), resolution fails with an error — the bearer challenge flow is **not** attempted when credentials were already sent.
4. The `realm` URL from the challenge **must** be HTTPS; HTTP realm URLs are rejected without requesting a token.

**HTTP status handling:**

| Status | Behavior |
|---|---|
| `200 OK` | Extract `Docker-Content-Digest` header; validate as `sha256:<64-hex>` |
| `401 Unauthorized` (no stored auth) | Trigger bearer token challenge flow (see above) |
| `401 Unauthorized` (with stored auth) | Throw `InvalidOperationException` — permanent auth failure |
| `404 Not Found` | Return `null` — image does not exist, no error |
| Any other non-2xx | Throw `InvalidOperationException` with status code |

**Caching:**
- `_successCache: ConcurrentDictionary<string, string>` keyed by normalized `{registry}/{repo}:{tag}`.
- Only successful digest resolutions are cached; 404 and error results are not cached so transient failures can be retried.

#### 4.5.2 Remediation Entry Point

```csharp
public sealed class PinRemediationEngine
{
    public PinRemediationEngine(
        IActionShaResolver? actionShaResolver,
        IImageDigestResolver? imageDigestResolver,
        FixPinningConfig pinningConfig,
        FixImagesConfig imagesConfig,
        NetworkConfig networkConfig)
    { }

    /// <summary>
    /// Attaches network-resolved fix payloads to unpinned-uses / unpinned-image diagnostics.
    /// Returns a new collection where fixable diagnostics carry DiagnosticFix.
    /// Does not mutate LintResult.
    /// </summary>
    public Task<IReadOnlyList<Diagnostic>> RemediateAsync(
        IReadOnlyList<Diagnostic> diagnostics,
        byte[] utf8Yaml,
        CancellationToken cancellationToken = default);
}
```

#### 4.5.3 Configuration Mapping

Pin remediation configuration maps from the `fix` and `network` sections of the configuration file (§5.12, §5.13, §12.3):

```csharp
public sealed record FixConfig
{
    public FixDefaultsConfig Defaults { get; init; } = new();
    public FixPinningConfig Pinning { get; init; } = new();
    public FixImagesConfig Images { get; init; } = new();
}

public sealed record FixDefaultsConfig
{
    /// <summary>
    /// Default timeout-minutes value for job-timeout-minutes-required auto-fix.
    /// null or <= 0 disables fix attachment.
    /// </summary>
    public int? JobTimeoutMinutes { get; init; }
}

public sealed record FixPinningConfig
{
    public bool EnableNetwork { get; init; } = false;
    public int MinAgeDays { get; init; } = 14;
    public IReadOnlyList<string> ExcludeBranches { get; init; } = ["main", "master"];
    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];
}

public sealed record FixImagesConfig
{
    public bool EnableNetwork { get; init; } = false;
    public IReadOnlyList<string> ExcludeImages { get; init; } = ["scratch"];
    public IReadOnlyList<string> ExcludeTags { get; init; } = ["latest"];
    public IReadOnlyList<string> IgnoreImages { get; init; } = [];
}

public sealed record IgnoreActionEntry(string NamePattern, string RefPattern);

public sealed record NetworkConfig
{
    public NetworkErrorMode OnError { get; init; } = NetworkErrorMode.Skip;
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxConcurrency { get; init; } = LintConfigResourceLimits.DefaultNetworkMaxConcurrency;
    public GitHubNetworkConfig GitHub { get; init; } = new();
}

public enum NetworkErrorMode { Skip, Fail }

public sealed record GitHubNetworkConfig
{
    // Token env var order (SEITON_GITHUB_TOKEN → GITHUB_TOKEN) is hardcoded
    // and not configurable via config file — prevents malicious config injection.
    public string? GhesApiUrl { get; init; } = null;
    public bool GhesFallback { get; init; } = false;
}
```

Safety invariants:

- `NormalizeNetwork` caps `NetworkConfig.MaxConcurrency` at `Environment.ProcessorCount` (minimum `1`). Values greater than this emit an error and clamp (see `.github/docs/Seiton_Linter_spec.md` § network). Omitted `max-concurrency` defaults to **`LintConfigResourceLimits.DefaultNetworkMaxConcurrency`**, i.e. **`min(4, max(1, Environment.ProcessorCount))`**, so implicit defaults never exceed the cap.
- `LintConfigYamlParser` builds the config DOM from VYaml’s pull parser. For the normal `LintConfigLibrary.Validate` path, DOM parsing uses the **same** `byte[]` as `LintConfig.Utf8Yaml` (no redundant full-size copy). Non–array-backed `ReadOnlyMemory<byte>` inputs fall back to an `ArrayPool<byte>` copy.
- `scratch` must always be in `ExcludeImages` (enforced at construction, matching §12.3.8).
- `EnableNetwork: false` (the default) prevents resolver construction — `PinRemediationEngine` with `EnableNetwork: false` must not make any network calls even if resolver implementations are injected.
- Token resolution order is hardcoded as a code-internal constant: `["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"]`. This value is not exposed in config to prevent config-injection attacks.

#### 4.5.4 Fix Format

Actions SHA fix (§12.5.1):
- `TextEdit` replaces the `@ref` portion of the `uses:` scalar value.
- Replacement: `@<sha40> # <originalRef>` using ` # ` separator.
- If ref is already a 40-hex SHA, no fix is generated.

OCI digest fix (§12.5.2):
- `TextEdit` appends `@sha256:<hex>` immediately after the tag in the image reference.
- Tag is preserved; digest is appended.
- If image reference already contains `@sha256:`, no fix is generated.

#### 4.5.5 Integration with LintEngine and Fix Catalog

- `LintEngine.Check()` is unchanged — it never performs network I/O (§8.3 preserved).
- `PinRemediationEngine.RemediateAsync()` is a separate operation, not called from `Check()`.
- When `EnableNetwork: true`, `unpinned-uses` and `unpinned-image` diagnostics may receive fixes from `RemediateAsync()`; §8.4 catalog status changes to ✓ Fixable (network-assisted) for those rules.
- Diagnostics without a resolver result (skip or on-error: skip) remain without fix payload.

#### 4.5.6 Observability

`RemediateAsync` must return enough information to distinguish:

- Diagnostics that received a fix (resolved successfully)
- Diagnostics skipped by configuration (excluded by `IgnoreActions`/`ExcludeImages`/`ExcludeBranches`)
- Diagnostics where resolution failed and `on-error: skip` left them without fix

This maps to a `RemediationResult` wrapper:

```csharp
public sealed record RemediationResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    int ResolvedCount,
    int SkippedCount,
    int FailedCount);
```

---

## 5. Cross-Document Consistency Rule

When this document is revised, also review and update:

- `.github/docsSeiton_Linter_spec.md`
- `.github/docslinter_implementation_csharp_plan.md`
- `.github/docsSeiton_spec.md` when parser/linter boundary wording changes
