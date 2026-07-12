---
name: architecture
description: Guidelines for the architecture and design principles of the parser in the `src/Seiton.Core/Parsing/` folder. This includes layer responsibilities, hand-written parsing rationale, and evolution strategies.
---

# Parser Architecture

## Purpose

This document explains the parser architecture and design principles used.
It is intentionally implementation-oriented so contributors can quickly answer:

1. What each layer is responsible for.
2. Why hand-written parsing is used.
3. How to evolve the parser without breaking performance and diagnostics.

To understand the rationale behind design decisions, see the related specs:

- `.github/docs/architecture_spec_csharp.md`
- `.github/docs/Seiton_Parser_spec.md` — パーサー仕様（言語非依存）
- `.github/docs/Seiton_Parser_csharp_spec.md` — C# 実装仕様
- `.github/docs/architecture_spec_performance.md`

## Core Design Principles

Parser design follows actionlint-style architecture, adapted to C# and VYaml.

1. Parse by structure, not by full object deserialization.
2. Build typed model while validating shape (single pass where possible).
3. Preserve source positions for diagnostics at every stage.
4. Continue parsing after recoverable errors to return multiple diagnostics.
5. Keep hot paths allocation-aware (UTF-8 span checks, minimal string materialization).
6. Separate syntax validation from semantic/policy validation.

## Why This Architecture

GitHub Actions workflows require more than schema validation.
Many constraints are contextual (for example key combinations, event-dependent behavior, expression semantics).

Therefore the parser uses a hybrid model:

1. Hand-written parser for syntax shape and recovery behavior.
2. Expression parser/analyzer for `${{ }}` domain rules.
3. Rule-style semantic validation over parsed model.
4. Optional external schema and generated metadata as supporting data, not primary truth.

## AST Storage Model (Data-Oriented)

The AST is not an object graph. Every composite node is a struct row in a typed `NodeTable<T>` owned by `AstArena`:

1. Handles are 1-based typed ID record structs (`JobId`, `StepId`, ...); `default` = absent.
2. Child lists are `(first, count)` ranges — over shared ID stores (`StringIdRange`, `StepIdRange`) when nested parsing makes row tables non-contiguous, or `NodeRange` directly over contiguous rows (key-embedded maps).
3. Maps embed the key `Utf8Slice` in the row; lookup is a linear scan within the range. Case sensitivity is fixed per map type (permissions scopes and env vars are case-sensitive; all others case-insensitive).
4. Polymorphic nodes are tagged unions: a `Kind` enum (`None = 0` first) plus a 1-based payload index into a kind-specific payload table (`StepExecKind`, `EventKind`, `RawYamlKind`).
5. Consumers (rules, tests) read through readonly-struct Ref facades (`WorkflowRef` / `JobRef` / `StepRef` / `StringRef`, ...); default refs chain safely (`HasValue == false`, never throw).
6. Arena reset clears table counters only — no object pools, no per-node `Reset()`, no manual buffer registration. A DEBUG-only generation counter turns use-after-dispose into an immediate exception.

Contract details: `.github/docs/Seiton_Parser_csharp_spec.md` §2. Design conventions, invariants (lifecycle wiring, contiguity rule, incremental-parse invariants), and lessons learned: `.github/docs/architecture_spec_ast.md`.

## Layered Architecture

### 1) Input and YAML Stream Layer

Responsibilities:

1. Read UTF-8 YAML input.
2. Expose YAML events/tokens with location metadata.
3. Enable subtree skip for recovery.

Design notes:

1. Parsing logic should compare keys/values via UTF-8 spans on hot path.
2. Avoid string conversion unless needed for diagnostics.

### 2) Workflow Syntax Parsing Layer

Responsibilities:

1. Traverse workflow/job/step mappings and sequences.
2. Validate shape constraints (required keys, key types, key combinations).
3. Record parser diagnostics with text positions.
4. Produce parsed workflow document model.

Design notes:

1. Unknown keys should emit diagnostics and skip value subtree.
2. Missing required keys should be validated after scope parse completes.
3. Do not stop at first syntax error when safe recovery is possible.

### 3) Expression Parsing and Semantics Layer

Responsibilities:

1. Extract `${{ }}` expressions from relevant YAML fields.
2. Parse expression grammar into compact expression nodes.
3. Run semantic checks (function/identifier usage, context validity).

Design notes:

1. Expression parser should be independent from YAML parser state.
2. Keep expression representation compact for frequent evaluations.

### 4) Diagnostics Layer

Responsibilities:

1. Represent severity/message/location consistently.
2. Keep locations stable and useful for users.
3. Support multiple findings from one parse run.


Design notes:

1. Prefer key-span diagnostics for key-level problems.
2. Prefer value-span diagnostics for type/value problems.
3. For relationship errors, keep one primary location and add related locations when needed.

## End-to-End Parse Flow

1. Read YAML stream with location-aware reader.
2. Parse top-level workflow mapping.
3. Parse nested structures (`on`, `jobs`, steps, etc.) with local constraints.
4. Extract and parse expressions where applicable.
5. Run expression semantic checks.
6. Return parsed workflow model + collected diagnostics.

## Error Recovery Strategy

Parser behavior is recovery-first, not fail-fast.

1. On invalid key/value node, emit diagnostic.
2. Skip current subtree safely.
3. Resume at next sibling boundary.
4. Preserve structural parsing state to avoid cascading false errors.

This strategy maximizes actionable feedback for users in a single run.

## Performance and Allocation Principles

For the parser, the following are mandatory:

1. Use UTF-8 span comparisons for key checks in hot paths.
2. Avoid `GetScalarString()` and `Encoding.UTF8.GetString(...)` on success paths.
3. Allow string conversion only for diagnostics/fallbacks.
4. Do not introduce `List<T>`, `Dictionary<TKey, TValue>`, LINQ, regex, or per-node allocations in new hot paths unless justified and measured.
5. Reuse parsed metadata instead of repeated lookups.
6. Prefer offset/length slices (`Utf8Slice`) over materialized strings when values must be retained.

## Architectural Boundaries

To keep the system maintainable and fast, keep these boundaries strict:

1. YAML stream handling code should not own semantic decisions.
2. Workflow shape parser should not perform deep expression semantics.
3. Expression semantic analyzer should not depend on YAML event internals.
4. Diagnostics format should be independent from parser control flow.

## What to Change vs. What to Keep Stable

Easily evolvable:

1. Supported workflow keys and constraints.
2. Event metadata and compatibility tables.
3. Expression semantic rules.

Keep stable:

1. Layer boundaries.
2. Recovery-first parser behavior.
3. UTF-8 span-based hot path checks.
4. Position-preserving diagnostics contract.

## Implementation Checklist for Parser Changes

Before completing parser/AST changes, verify all of the following:

1. No new success-path string materialization was added in hot loops.
2. New key checks are UTF-8 span based.
3. Diagnostics remain location-accurate and human-readable.
4. Recovery behavior still allows multi-error reporting.
5. Parser-related tests pass.

## Non-Goals

This architecture intentionally does not aim for:

1. Full behavior definition by JSON Schema alone.
2. Immediate termination on first parse error.
3. Rich object graph deserialization as primary parse strategy.
4. Premature abstraction that hides parse-state control.

## Summary

The parser architecture is a performance-aware, recovery-first, hand-written parser design with explicit layer separation:

1. YAML stream reading with location fidelity.
2. Shape-validating workflow parser.
3. Dedicated expression parse/semantic pipeline.
4. Consistent diagnostics model.

This enables high-quality diagnostics, predictable extensibility, and controlled allocation behavior while tracking GitHub Actions spec changes over time.
