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

1. Parse-first lint entrypoint (`LintEngine.Check`)
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

- parse-first flow
- fatal parse short-circuit
- rule execution
- deterministic post-processing

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
	Diagnostic[] GetDiagnostics();
	void SetConfig(LintConfig config);
}
```

Each rule inspects AST nodes in `IPass` callbacks and accumulates diagnostics internally.

### 3.4 Current C# Default Rule Pack

The current default rule scope in C# is:

- `job-structure`
- `reusable-workflow`
- `permissions`
- `popular-action-inputs`

Scope notes:

- Parser diagnostics remain primary for YAML shape and required-key errors.
- Rule diagnostics add policy and metadata checks over parsed AST.
- `LintEngine` defaults to `RuleCatalog.CreateDefaultRules()`, applies priority sort, then deduplicates identical diagnostics.

---

## 4. Exclusion and Suppression Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5, §6.1, §8

C# implementation must provide:

- config-based exclusion matching
- inline next-line directive handling
- unknown rule-id as configuration error
- fail-safe checks (non-disableable, minimum severity)
- suppression observability in `LintResult`

---

## 5. Cross-Document Consistency Rule

When this document is revised, also review and update:

- `Docs/Seiton_Linter_spec.md`
- `Docs/linter_implementation_csharp_plan.md`
- `Docs/Seiton_spec.md` when parser/linter boundary wording changes
