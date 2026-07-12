# Seiton AST Architecture (Data-Oriented Design)

> This is the permanent design document for the AST: concepts, conventions, and invariants.
> For the C# type-signature-level contract see `.github/docs/Seiton_Parser_csharp_spec.md` §2,
> for the rule-author read API see `src/Seiton.Core/Linting/Rules/AGENTS.md`,
> and for performance requirements see `.github/docs/architecture_spec_performance.md` and
> `.claude/skills/performance-requirements/SKILL.md`.

## 1. Concept (WHAT)

The AST is not a graph of class objects. It is a **set of flat struct row tables solely owned by `AstArena`**.

- Every composite node (Job / Step / Event / Permissions / Matrix / ...) is a **row** in a per-node-kind `NodeTable<T>` (an ArrayPool-backed append-only array).
- Node references are **1-based typed IDs** (`JobId`, `StepId`, `PermissionsId`, ...). `default` = absent.
- Child lists are **(first, count) ranges** over shared stores (`NodeRange` / `StringIdRange` / `StepIdRange`).
- Maps are **ranges over row tables with the key (`Utf8Slice`) embedded in the row**; lookup is a linear scan within the range.
- Polymorphic nodes are **tagged unions**: a `Kind` enum plus a 1-based index into a kind-specific payload table.
- The only surface rules and tests touch is the **readonly-struct Ref facade layer** (`WorkflowRef` / `JobRef` / `StepRef` / `StringRef` and the list/map refs). Raw arena accessors are not a rule-author API.
- Only the roots (`Workflow` / `ActionMetadata`) remain as thin classes that bundle IDs and ranges.

## 2. Rationale (WHY)

The goal was to pay down the complexity that had accumulated in the previous implementation (pooled mutable classes with an arena bolted on). **Speed was not the goal** — Medium/Large workflows were already at zero Gen0 collections before the migration, so the GC did not appear in wall-clock time.

Problems that were eliminated:

1. A dual lifetime mechanism: scalars lived in a true arena while 28 composite node kinds were mutable pooled classes with `Reset()` methods that had to be hand-maintained on every field addition.
2. Assigning `ArenaList<T>` (a struct) to an `IReadOnlyList<T>` field boxed it — a type built for zero allocation was producing allocations.
3. Multiple parallel pooling abstractions (`ArenaList` / `SliceMap` / `AstNodePool` / manual buffer registration) where a missed registration leaked.
4. Use-after-reset silently returned **another file's data**.
5. Arena constraints (`Arena.GetStringValue`, zero-copy conventions) leaked directly into the rule-author API, making rules hard to write.

All of these mechanisms are deleted. Resetting the arena is now nothing more than zeroing every table counter.

Trade-off accepted deliberately:

- The **compile-time `switch` exhaustiveness that sealed class hierarchies provided is weakened to warning-based checking** over `Kind` enums. This was exchanged for uniform node lifetime, zero boxing, and pool-free reset.

## 3. Storage Model Conventions

### 3.1 IDs and ranges

| Type | Representation | Meaning of `default` |
|---|---|---|
| Typed ID (`JobId`, ...) | 1-based int `readonly record struct` with `HasValue` / internal `Index` | key absent (the former `null`) |
| `NodeRange` | (first, count) over a row table | key absent |
| `StringIdRange` / `StepIdRange` | (first, count) over a shared ID store (`StringNodeId[]` / `StepId[]`) | key absent |

**"Present but empty" and "absent" are distinct**: the former is an anchored empty range (`HasValue == true`, `Count == 0`), the latter is `default`. Which one a parser recovery path returns was chosen to preserve the observable behavior of the previous implementation (example: `ParseSteps` always returns an anchored range as long as the `steps:` value is a sequence — even if every element errors; if the value is not a sequence at all, it is never called and the field stays `default` = absent). Breaking this distinction changes which diagnostics rules emit.

### 3.2 The two list shapes and the contiguity rule

A list's representation is decided by **which table nested parsing inserts rows into**:

- If nested parsing **inserts rows into the list's own table** (e.g. `parallel:` inside a step appends Step rows; RawYaml recursion), a direct row-table range would be non-contiguous. **Collect IDs in a scratch `PooledBuffer<T>` and bulk-append them to a shared ID store**, then hold that range (`StepIdRange` / `StringIdRange` style).
- If nested parsing **only touches other tables** (scalars etc. — e.g. env vars, `with:` inputs, jobs map entries), rows are contiguous, so **direct append to the row table + `NodeRange`** is fine.

**Re-verify this premise whenever you introduce a new nested structure.** If a map that direct-appends on the assumption "value parsing only touches scalar tables" later gains a nested parse that touches row tables, the range breaks silently.

### 3.3 Key-embedded maps and case sensitivity

Map rows embed the key `Utf8Slice` in the row; lookup is a linear scan within the range (same complexity as the old SliceMap; GitHub Actions maps are assumed small). Case sensitivity is **fixed separately for the lookup (Ref maps) and the parser's duplicate-key detection**, and changing either is a behavior change:

- **Ref map lookup**: only `permissions:` scopes and `env:` variable names are case-SENSITIVE (exact byte equality). Everything else (jobs / outputs / `with:` / secrets / services / action metadata inputs·outputs / dispatch inputs / ...) is case-INSENSITIVE.
- **Parser duplicate-key detection** (`TryRegisterDynamicKey`): unconditionally case-INSENSITIVE — including permissions and env, matching actionlint's "note that this key is case insensitive" diagnostics.

Case-insensitive comparison is centralized in `SpanHelpers.EqualsAsciiIgnoreCase`.

### 3.4 Tagged unions

- Discriminator enums **must place `None = 0` first** (prevents a `default` ref's `Kind` from reading as a valid value).
- Payloads are **1-based indexes** into kind-specific tables (0 = no payload).
- Parse order is "**append the payload row first → append the body row once at the end**" (keeps the body table contiguous).
- Current tagged unions: `StepData.ExecKind + ExecPayload`, `EventData.Kind + Payload`, `RawYamlData.Kind`.

### 3.5 Row immutability and local accumulation

Row structs have `init` properties only — **rows cannot be mutated after append**. Parsers accumulate values in locals and materialize the row once when the node's parse completes. When one node is assembled from multiple YAML keys (e.g. the former workflow-call's uses/with/secrets), the same local-accumulation pattern applies.

## 4. Ref Facade Conventions (Public API)

- `ParseResult.Workflow` / `LintResult.Workflow` return `WorkflowRef`. Rules and tests work exclusively through refs.
- **Default refs chain safely**: `job.Strategy.Matrix.Rows` never throws no matter how much of the chain is absent — the tail simply has `HasValue == false` / is empty. Rule-side null guards are unnecessary as a rule.
- Absence checks use `HasValue`. These are structs, so `is null` / `is { }` patterns do not work (`is { }` is always true — a mechanical-replacement trap). In tests, `IsNull()` / `IsNotNull()` on a boxed struct compiles but misjudges at runtime — always assert `HasValue`.
- Polymorphism is a `Kind` switch plus typed accessors (`AsRun()` / `AsAction()` / ...). An `As*()` call with a mismatched kind returns a default ref.
- Ref equality is (arena, id) value equality — stable within one parse, so `Dictionary<StepRef, T>` has the same semantics as the old object-identity keys. Caveat: equality is generation-blind (it never throws on stale refs by design), and arenas are pooled per thread — a ref retained across a re-parse can compare equal to a same-index ref from the new parse. Never key a collection with refs from more than one parse.
- List indexers and map `GetAt` are bounds-checked against the range and throw `ArgumentOutOfRangeException` on misuse (the backing stores are shared across lists, so an unchecked out-of-range read would silently return another list's data).
- Read strings via `StringRef.Value` (UTF-8 span) / `.Slice` / `.Range`. `.Decode()` (string materialization) is **only for building diagnostic messages**.

## 5. Lifetime and Safety

- Arenas are reused through a thread-static cache (`Rent` → `ResetForSource` → parse → `Dispose`). Reset only clears table counters.
- **`NodeTable` invariant: whenever the backing array is released, the count must also be zeroed.** Releasing the array while the count survives makes the next parse index a shrunken array with the old count → `IndexOutOfRange` → fatal (this accident actually happened; `AstArenaReuseTests` is the permanent black-box regression, 40 same-thread reuses).
- **DEBUG generation counter**: the arena increments its generation at `ResetForSource` / `Dispose` (the counter itself runs in Release too — an int increment). In DEBUG builds every ref captures the generation at construction, and resolving a handle after dispose throws `InvalidOperationException` immediately (in Release the capture fields and checks compile out — zero cost). `HasValue` and equality never throw on stale refs (safe to call).
- To keep a value beyond the arena's lifetime, **copy it out before dispose** (a `Decode()`d string, or a value snapshot like `LocalWorkflowContract`).

## 6. Incremental Parse Invariants

The Playground's `IncrementalParseContext` (D-5b/5c/5d) rests on these invariants:

1. A section/job is reused only when it is **byte-identical at the identical byte offset (same offset + same content hash)**. Therefore `Utf8Slice`s inside reused nodes remain valid against the new source.
2. The new arena's `BulkImportFrom` **copies every node table wholesale** from the previous arena (only the 4 scalar tables are capped at base counts). Therefore previous-parse IDs and ranges resolve unchanged in the new arena.
3. Job reuse is **`JobId`-based** (`JobSkipEntry` carries a JobId). The jobs map is an entry indirection over `JobEntryData {Key, JobId}` precisely because reused JobIds (low row indexes from the import) and freshly parsed JobIds coexist in one map.
4. The old arena is disposed immediately after every parse. There is no arena retention via object ownership.
5. Table rows accumulate across parses, so the scalar growth threshold (3×) forces a full parse to bound growth.

**Wiring rule derived from these invariants**: whenever you add a table to the arena, wire it at ALL of —
(a) `Reset` in `ResetForSource`, (b) `Reset` + `ReleaseOversized` in `Dispose` (runs on both the retain and discard paths), (c) `ReleaseAll` on the `Dispose` discard path (cache already occupied), and (d) **`CopyFrom` in `BulkImportFrom`**. Grep an existing table (`_stringIdItems` etc.) to enumerate all wiring sites and grep-verify the landing. Missing (d) passes every single-parse test and breaks only incremental parsing, silently.

## 7. Checklist for Adding a Node Kind

Conventions when adding a new AST node kind (the type-level contract lives in the Parser spec §2):

1. Define the row struct (`Ast/*Data.cs`). Fields are scalar IDs / other node IDs / ranges / `TextRange` only — never object references or strings.
2. Add the typed ID to `Ast/NodeIds.cs` (1-based; clone an existing ID).
3. Add the `NodeTable<T>` + accessors to the arena (`AddXxx` / `GetXxx`; for maps `GetXxxAt(NodeRange, i)` + `XxxCount`), and do the 4-point lifecycle wiring from §6.
4. Add the Ref (and list/map refs if needed). Clone an existing ref of the same shape and keep the public surface consistent (HasValue / TryGetValue / enumerator).
5. Parser construction sites accumulate locals → one `Add` at parse completion (§3.5). Choose the list shape by the contiguity rule (§3.2). For maps, state the case sensitivity explicitly per §3.3.
6. Tests: preserve the semantics mapping (§3.1 absent vs present-empty) and assert with `HasValue`.

## 8. Performance Characteristics and Caveats

- Steady-state allocation is dominated by ArrayPool rent/return; both parse and lint run at **Gen0 = 0 for Medium/Large**. Measured at migration completion (ShortRun, cool machine): Parse Large 15.70ms / 2,600B; Lint Large/False 16.26ms / 34.01KB (pre-migration baseline: 21.8ms / 234KB).
- Map lookup is a linear scan. **Do not put repeated lookups against large maps on a hot path** (maps in real GitHub Actions files are small; this is the standing assumption).
- Ref properties are thin wrappers that read arena rows and are JIT-inlined. Do not drop to raw arena access on the grounds of "ref overhead".
- Benchmark measurement trap: `Program.cs` always uses `Job.ShortRun`, and for 20–30ms/op cases the measurement may land inside dynamic PGO's instrumented tier — **Mean can swing ±40% between runs with no code change**. When the ±10% gate is exceeded, determine real-vs-artifact with three checks: (a) stash A/B against HEAD, (b) a Stopwatch phase-split (`WorkflowParser.Parse` + pre-parsed `Check`), (c) steady-state after 400+ warmup ops. **The Allocated column and the control benchmark (`ExpressionExtractor`) are the primary signals** — they are largely immune to thermal and JIT-phase noise.
- Conversely, treat **suspiciously good numbers with size-independent Allocated** as a correctness failure (a broken op returns fatal early and is fast). The benchmark is a corruption detector as much as a performance meter.

## 9. Lessons Learned (baked into the design)

Discoveries made only by building it, kept here because they permanently shape design decisions:

1. **A struct assigned to an interface-typed field boxes.** `ArenaList<T> : IReadOnlyList<T>` escaped to the heap the moment it was stored as `IReadOnlyList<T>`. "Wrap it in a struct and it's allocation-free" does not hold when the field's static type is an interface. This is why the current design is uniformly ranges + concrete struct refs.
2. **Zero-allocation work stops paying at Gen0 = 0.** Further reductions tend to be pure complexity increases; judge the investment by the benchmark's Gen0 column first.
3. **Default-safe ref chaining deleted the majority of rule-side null guards** and paid for much of the migration. Its flip side is the struct traps in §4 (`is { }` / `IsNull()`), which are mechanical-replacement hazards.
4. **`None = 0` on discriminator enums** closes off default-ref misbehavior at the type level. Always follow it when adding tagged unions.
5. **A missed Reset wiring on a shared store is undetectable by single-parse tests.** Counts accumulate across parses → the retention cap releases the array while the count survives → a later parse collapses. (§5's NodeTable invariant and `AstArenaReuseTests` are the recurrence guards.)
6. **The "reuse only at identical offset + identical hash" invariant converted incremental-parse complexity into the simplicity of wholesale table copies** (§6). Relaxing it (e.g. allowing offset shifts) would re-open ID relocation — treat that as a design decision, not a tweak.

## 10. Related Documents

- `.github/docs/Seiton_Parser_csharp_spec.md` §2 — the type-signature-level storage/Ref contract (the concrete form of this document's conventions)
- `.github/docs/Seiton_Linter_csharp_spec.md` — IPass/IRule/visitor Ref signatures
- `.github/docs/Seiton_Playground_csharp_spec.md` — incremental parse (D-5b/5c/5d) contract
- `.github/docs/architecture_spec_performance.md` — overall performance architecture and the language-selection record
- `.claude/skills/architecture/SKILL.md` / `.claude/skills/performance-requirements/SKILL.md` — implementation-time guides
- `src/Seiton.Core/Linting/Rules/AGENTS.md` — rule-author Ref API conventions and typical patterns
