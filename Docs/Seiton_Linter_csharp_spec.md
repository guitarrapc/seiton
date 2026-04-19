# Seiton Linter C# Implementation Specification

> C# implementation specification for the linter contract defined in `Seiton_Linter_spec.md`.
> This document captures C# runtime structures and behavior for rule execution, exclusion/suppression, and diagnostic output.
> Parser behavior is specified in `Seiton_Parser_spec.md` and `Seiton_Parser_csharp_spec.md`.

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

### 0.4 Runtime Model

Linter runtime assumes parser output as structural input and never reparses YAML structure.

- Parse result consumed first
- Rule traversal performed next
- Diagnostics collected, post-processed, and filtered by linter policies

### 0.5 Design

1. Keep parser/linter responsibility boundary strict.
2. Keep lint output deterministic for identical input/config.
3. Keep rule/exclusion policy behavior aligned with language-agnostic linter contract.
4. Keep implementation status synchronized with `Docs/linter_implementation_csharp_plan.md`.

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

Current implementation status should be tracked against `Docs/linter_implementation_csharp_plan.md`.

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
		// 1. Parse(utf8Yaml, filePath) -> ParseResult
		// 2. Construct IRule set
		// 3. WorkflowVisitor.Visit(workflow)
		// 4. Collect diagnostics from each Rule
		// 5. FilterErrors -> Sort + Dedup -> Output
	}
}
```

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
| `permissions` | Validate `permissions` value domain: scalar must be `read-all` or `write-all`; scope values must be `read`, `write`, or `none`. |
| `popular-action-inputs` | Validate known action input names against maintained popular-action metadata and emit diagnostics for unknown inputs. |
| `unpinned-uses` | Warn when `uses:` references are not pinned to full commit SHA for remote actions/reusable workflows; additionally validate `uses` reference format and local action reference sanity where statically resolvable. |
| `unpinned-image` | Warn when docker image references (`docker://`, `job.container.image`, `job.services.*.image`) are not pinned by digest (`@sha256:<64-hex>`). |
| `dangerous-triggers` | Warn when dangerous trigger events are used (built-in dangerous event set plus any additive customization defined by config). |
| `job-permissions-required` | Warn when a job omits explicit `permissions` configuration. |
| `needs-graph` | Error on invalid `needs` graph: unknown dependency targets and circular dependencies. |
| `shell-name` | Error when configured shell names are outside the supported shell set for workflow/job defaults and `run` steps. |
| `runner-label` | Warn on unknown GitHub-hosted runner labels in `runs-on` (excluding self-hosted and expression-only cases), using built-in labels plus additive config labels. |
| `runner-no-latest` | Warn when moving GitHub-hosted labels (`ubuntu-latest`, `windows-latest`, `macos-latest`) are used in `runs-on`; prefer explicit version-pinned labels. |
| `id-naming` | Error when `job.id` or `step.id` contains characters outside allowed identifier set. |
| `glob-pattern` | Error on invalid event filter configuration, including invalid glob syntax, unsupported event options/types, and incompatible filter combinations (`branches` vs `branches-ignore`, `tags` vs `tags-ignore`, `paths` vs `paths-ignore`). |
| `deny-write-all` | Error when workflow/job permissions use `write-all`; this rule is fail-safe constrained by `Seiton_Linter_spec.md` §5.7. |
| `credentials` | Warn when custom/private registry images in `job.container` or `job.services.*` are used without credentials, except registries treated as public by built-in plus additive config set. |
| `template-injection` | Error when untrusted `github.event`-origin data is directly interpolated into `run`/`env` sinks in unsafe ways. |
| `expr-undefined-var` | Error when expressions reference context roots unavailable in the current scope (for example job scope vs step scope context mismatch). |
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
- `LintEngine` defaults to `RuleCatalog.CreateDefaultRules()`, applies priority sort, then deduplicates identical diagnostics.
- Network-assisted rule IDs (`known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs`) are cataloged in `RuleCatalog` for rule-id resolution and priority ordering, but C# emits them through `OnlineAuditEngine` rather than the default local `IRule` pass set.
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
- C# runtime maps all eight IDs in `RuleCatalog`; `deny-read-all` / `deny-inherit-secrets` / `job-timeout-minutes-required` / `github-app-token-inputs` are default local rules, while the four network-assisted rules are emitted by `OnlineAuditEngine`.

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

Status contract:

- `cache-poisoning` / `self-hosted-runner` / `unredacted-secrets` / `secrets-outside-env` are already in the current C# default local rule pack.
- `matrix` / `env-var` / `deprecated-commands` / `if-cond` are already in the current C# default local rule pack.
- `archived-uses` / `insecure-commands` / `overprovisioned-secrets` / `forbidden-uses` / `ref-version-mismatch` / `use-trusted-publishing` are already in the current C# default local rule pack.
- C# runtime implementation and default-catalog promotion must be synchronized with `Docs/linter_implementation_csharp_plan.md` and shared-spec catalog updates.

### 3.7 Known Partial Parity Areas (actionlint)

Current C# default local rules are intentionally partial for the following domains.

- `events`: partially covered by `dangerous-triggers` and `glob-pattern`; still missing webhook-specific activity type validation, filter cross-constraint validation, and payload-shape semantic checks.
- `action`: partially covered by `popular-action-inputs` / `unpinned-uses`; still missing full uses-format validation breadth, local/Docker action resolution depth, and metadata contract-level validation.
- `workflow-call`: partially covered by `reusable-workflow` / `deny-inherit-secrets`; still missing called-workflow contract validation (`inputs`/`secrets` required/type/default consistency and caller conformance).

These are tracked as next-step parity-hardening items in `Docs/linter_implementation_csharp_plan.md`.

---

## 4. Exclusion and Suppression Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5, §6.1, §11

C# implementation must provide:

- config-based exclusion matching
- inline next-line directive handling
- unknown rule-id as configuration error
- fail-safe checks (non-disableable, minimum severity)
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
- `null` return indicates configuration-based skip (not an error).
- Implementations must cache successful resolutions in-process for the duration of a single `RemediateAsync` call.
- Error results (non-skip failures) must not be cached.
- Resolver implementations are injected by caller — not instantiated by `LintEngine`.

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
    public int MaxConcurrency { get; init; } = 4;
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

- `Docs/Seiton_Linter_spec.md`
- `Docs/linter_implementation_csharp_plan.md`
- `Docs/Seiton_spec.md` when parser/linter boundary wording changes
