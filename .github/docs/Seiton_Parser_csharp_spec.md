# Seiton Parser C# Implementation Specification

> C# implementation specification for the parser contract defined in `Seiton_Parser_spec.md`. This document captures C# runtime structures and targeting C# with zero-allocation / high-performance design. See `Seiton_Parser_go_spec.md` for the Go target. Both language specs share the same outline; only language-specific content differs. Linter behavior is specified in `Seiton_Linter_spec.md` and `Seiton_Linter_csharp_spec.md`.

> **Cross-document synchronization rule**: `Seiton_Parser_spec.md` is the source of truth. When this C# spec is updated, also review and update `Seiton_Parser_spec.md`, `Seiton_Parser_go_spec.md`, and `parser_implementation_csharp_plan.md` in the same PR/commit scope.

---

## 0. C# Preamble

### 0.1 Contract

#### 0.1.1 Current Contract vs Reference Parity Gap

This document uses the following terms consistently:

- **Current contract**: behavior that Seiton currently implements and treats as part of its supported C# parser/lint surface. This is the behavior that should be covered by regression tests and kept consistent with `Seiton_Parser_spec.md`.
- **Reference parity gap**: behavior present in the reference implementation (`actionlint`) but not fully matched by Seiton yet. A parity gap is not automatically part of Seiton's current contract.
- **Out of scope**: behavior intentionally excluded from this spec's completion criteria. Use this only for explicit non-goals, not as a synonym for "not yet parity-complete".

The source of truth for Seiton's supported behavior is `Seiton_Parser_spec.md`. The actionlint comparison in this section is informational and is used only to highlight parity gaps, not to silently expand Seiton's contract.

#### 0.1.2 actionlint Feature Parity Status

All parser and expression features from actionlint have been implemented in the C# codebase. Linter-side features (Visitor/Pass, Rule Engine) are defined in `Seiton_Linter_csharp_spec.md`.

<details>
<summary>Full parity table (all categories implemented)</summary>

| Category | Summary |
|---|---|
| **AST Construction** | `ParseResult.Workflow` returns typed `Workflow` AST |
| **AST Range Coverage** | Scalar nodes keep scalar ranges; structural nodes build composite `TextRange` spans |
| **Event Detail Parse** | All five structured event types (`schedule`, `workflow_dispatch`, `workflow_call`, `repository_dispatch`, `image_version`) |
| **workflow_dispatch inputs** | `type`, `options`, `required`, `default` parsed individually |
| **workflow_call inputs/secrets/outputs** | Required validation for `type`/`required`/`value` |
| **schedule cron/timezone** | Parsed individually in mapping |
| **Permissions Structure** | scalar or mapping form |
| **Defaults / Concurrency** | Required-key enforcement in both top-level and job-level forms |
| **Environment** | scalar or mapping form (`name`, `url`, `deployment`) |
| **Runner (runs-on)** | scalar/sequence/mapping + expression into `Runner` |
| **Step ExecRun / ExecAction** | `run`→`ExecRun`, `uses`→`ExecAction`, Docker `entrypoint`/`args` |
| **Matrix & Strategy** | Recursive `RawYAMLValue` parse, `fail-fast`/`max-parallel` typed |
| **Container / Services** | Expression-or-mapping polymorphism for `services`, `credentials`, container/service `env` |
| **YAML Alias Resolution** | Adapter-owned + fatal diagnostic normalization |
| **Duplicate Key Detection** | Case-insensitive via `TryRegisterMappingKey` |
| **Expression Type System** | Full `ExprType` hierarchy, dynamic context, `DynamicContextTypeBuilder`, template/env/index type checks |
| **Expression AST Nodes** | `Identifier`, `MemberAccess`, `WildcardAccess`, `IndexAccess`, `FunctionCall`, `Unary`, `Binary` |
| **Generated Data** | `WebhookTypes.g.cs`, `Availability.g.cs`, `PopularActions.g.cs`, `ContextTypes.g.cs`, `FunctionSpecs.g.cs` |

</details>

#### 0.1.3 Perspectives to Supplement from ghalint

| Perspective | Details |
|---|---|
| Polymorphic YAML fields | Custom parsing patterns for `permissions` (scalar or mapping), `container` (scalar or mapping), `secrets` (`"inherit"` or mapping) — implemented in current C# parser |
| Minimal policy model | ghalint defines structs only for needed fields. This spec builds a full AST but maintains all Job/Step fields to support future rules |

#### 0.1.4 Perspectives to Supplement from zizmor

| Perspective | Details |
|---|---|
| `${{ }}` fenced extraction | Already implemented in C# (`ExpressionExtractor`) |
| JSON Schema auxiliary validation | Explicit non-goal for this parser spec. This is out of scope rather than a parity gap |
| Context risk table (`context-capabilities`) | Managed as generated data. Belongs to the rule layer, not the parser |

### 0.2 Overview

The Seiton Parser C# implementation provides:

1. YAML event-stream parsing via adapter-backed reader (`IYamlStreamReader`)
2. Alias resolution responsibility delegated to YAML adapter/library boundary
3. Hand-written recursive descent parser that builds typed AST
4. Expression parser for `${{ }}` grammar
5. Expression-language intrinsic validation (function existence, arity, operator-local type checks)
6. Expression semantic analyzer with operator-local type validation and type inference utilities (context-dependent checks are linter-owned)
7. Generated metadata usage (webhooks, availability, popular actions)
8. Input document-kind classification (workflow vs action metadata) using path-hint candidate + structure-confirm finalization

> **Boundary note**: Under the refined expression validation boundary (`Seiton_spec.md` §3), the parser owns expression-language intrinsic validation. GitHub Actions context-dependent validation (context availability, function availability by position, dynamic properties, site-aware types) is owned by the linter. The C# implementation reflects this boundary: the parser produces only intrinsic diagnostics, while the linter performs all context-dependent checks via `ExprUndefinedVarRule` and `ExpressionSemanticModel`.

Linter-side runtime details are specified in `Seiton_Linter_csharp_spec.md`.

### 0.3 Structure

Representative source layout for parser-side responsibilities:

| File/Area | Responsibility |
|---|---|
| `src/Seiton.Core/Parsing/WorkflowParser.cs` | Parser entrypoint and recursive-descent implementation |
| `src/Seiton.Core/Parsing/Ast/*` | AST type definitions |
| `src/Seiton.Core/Parsing/ExpressionParser.cs` | Expression recursive descent parser |
| `src/Seiton.Core/Parsing/ExpressionSemanticAnalyzer.cs` | Expression semantic checking/inference |
| `src/Seiton.Core/Parsing/IYamlStreamReader.cs` | YAML adapter boundary contract |
| `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs` | VYaml-backed reader adapter |
| `src/Seiton.Core/Generated/*.g.cs` | Generated parser metadata tables |

### 0.4 YAML/Alias

#### 0.4.1 YAML Adapter Layer (Anti-Corruption Layer)

An **Anti-Corruption Layer** is placed between the parser core and the YAML library.
This layer ensures that replacing the YAML serializer/deserializer does not propagate changes to the parser core.

#### 0.4.2 Architecture

```
┌───────────────────────────────────────────────────────────┐
│  WorkflowParser / parse functions                         │
│  (YAML library independent)                               │
└────────────────────┬──────────────────────────────────────┘
                     │ depends on
                     ▼
┌───────────────────────────────────────────────────────────┐
│  IYamlStreamReader (interface)                            │
│  Minimal YAML reading contract for the parser             │
└────────────────────┬──────────────────────────────────────┘
                     │ implemented by
          ┌──────────┼──────────┐
          ▼          ▼          ▼
   VYamlStream   (future)    FakeYaml
   Adapter       YamlDotNet  StreamReader
                 Adapter     (for testing)
```

#### 0.4.3 IYamlStreamReader Interface

The **sole YAML reading contract** that the parser core depends on.

```csharp
/// <summary>
/// YAML event stream reading abstraction.
/// The parser core depends only on this interface,
/// never referencing concrete YAML libraries directly.
/// </summary>
public interface IYamlStreamReader
{
    // --- State inspection ---
    YamlEventKind CurrentKind { get; }
    bool End { get; }

    // --- Advancement ---
    bool Read();
    void SkipCurrentNode();
    void SkipAfter(YamlEventKind kind);

    // --- Scalar value retrieval ---
    ReadOnlySpan<byte> GetScalarUtf8();
    Utf8Slice GetScalarSlice();
    string? GetScalarString();       // for diagnostics / fallback only
    ScalarTag GetScalarTag();        // !!str, !!bool, !!int, !!float, !!null
    bool IsScalarQuoted();           // single/double quoted
    bool IsExplicitNull();           // true for `null`/`~` literal, false for implicit empty (`key:`)

    // --- Position information ---
    TextPosition CurrentStart { get; }   // line, column, byte offset
    TextPosition CurrentEnd { get; }
}
```

#### 0.4.4 Custom Enumerations

The YAML event types and tag types referenced by the parser core are custom enums independent of any YAML library.

```csharp
/// YAML event type (YAML library independent)
public enum YamlEventKind
{
    None,
    StreamStart,
    StreamEnd,
    DocumentStart,
    DocumentEnd,
    MappingStart,
    MappingEnd,
    SequenceStart,
    SequenceEnd,
    Scalar,
    Alias,
}

/// Scalar tag type (YAML library independent)
public enum ScalarTag
{
    Unknown,
    Str,        // !!str
    Bool,       // !!bool
    Int,        // !!int
    Float,      // !!float
    Null,       // !!null
}

/// Source position
public readonly record struct TextPosition(
    int Offset,
    int Line,
    int Column);
```

#### 0.4.5 VYamlStreamAdapter (VYaml Implementation)

The current default implementation. Holds a VYaml `YamlParser` internally and converts it to `IYamlStreamReader`.

```csharp
internal sealed ref struct VYamlStreamAdapter : IYamlStreamReader
{
    private YamlParser _parser;  // VYaml-specific type

    // --- IYamlStreamReader implementation ---
    public YamlEventKind CurrentKind => MapEventKind(_parser.CurrentEventType);
    public bool End => _parser.End;
    public bool Read() => _parser.Read();
    public void SkipCurrentNode() => _parser.SkipCurrentNode();
    public ReadOnlySpan<byte> GetScalarUtf8() => _parser.GetScalarAsUtf8();
    // ... other members also convert VYaml API to custom enum/struct

    // VYaml ParseEventType → YamlEventKind conversion
    private static YamlEventKind MapEventKind(ParseEventType vt) => vt switch
    {
        ParseEventType.MappingStart  => YamlEventKind.MappingStart,
        ParseEventType.MappingEnd    => YamlEventKind.MappingEnd,
        ParseEventType.SequenceStart => YamlEventKind.SequenceStart,
        ParseEventType.SequenceEnd   => YamlEventKind.SequenceEnd,
        ParseEventType.Scalar        => YamlEventKind.Scalar,
        // ...
    };
}
```

**Important**: VYaml-specific types (`ParseEventType`, `Marker`, `YamlParser`, etc.) appear only in this file. The parser core and tests never reference them.

#### 0.4.6 Rationale for the Adapter Layer

| Problem | Solved by adapter |
|---|---|
| VYaml event API changes propagate to the entire parser | Changes are contained within `VYamlStreamAdapter` |
| Tests are coupled to VYaml implementation details | `FakeYamlStreamReader` can inject minimal event sequences directly |
| Parser core responsibilities are mixed with YAML library absorption | Responsibilities are cleanly separated |
| Need to replace with another serializer like YamlDotNet | Just implement a new adapter; parser remains unchanged |
| Scalar tag retrieval (`!!str`, `!!int`, etc.) differs per library | Absorbed by normalizing to `ScalarTag` enum |

#### 0.4.7 Replacement Procedure

1. Create a new adapter class implementing `IYamlStreamReader` (e.g., `YamlDotNetStreamAdapter`)
2. Replace the adapter factory in the entry point (`WorkflowParser.Parse()`)
3. Parse functions in the parser core require **no changes at all**
4. Existing tests pass as-is (because the `IYamlStreamReader` contract is the same)

#### 0.4.8 Scalar Tag Information

Tag information equivalent to actionlint (Go)'s `yaml.Node.Tag` (`!!str`, `!!bool`, `!!int`, `!!float`, `!!null`) is returned by the adapter layer's `IYamlStreamReader.GetScalarTag()` as a `ScalarTag` enum.

- VYaml adapter: Converts from VYaml internal tag information
- YamlDotNet adapter: Converts from `NodeEvent.Tag`
- For libraries without tag information: Fallback estimation based on value content (`"true"` / `"false"` / numeric patterns)

The parser core references only the `ScalarTag` enum and has no knowledge of library-specific tag representations.

#### 0.4.9 Relationship with Current VYaml Adapter

The parser has already completed the adapter migration:

1. `IYamlStreamReader` defines the parser-facing contract.
2. `VYamlStreamAdapter` is the production adapter used by `WorkflowParser.Parse(byte[], string)`.
3. Parser internals run through generic core methods (`ParseCore<TReader> where TReader : IYamlStreamReader, allows ref struct`) to keep adapter calls devirtualizable.
4. VYaml-specific event/type absorption is contained in the adapter boundary.

This means the entry point remains stable while alternate adapters can be introduced without rewriting parser core logic.

#### 0.4.10 Alias Resolution Responsibility (Spec §1.1 step 1b, §3.1.1)

Alias resolution is a current-contract feature owned entirely by the adapter layer.

- **Mechanism**: `VYamlStreamAdapter` implements event-buffering alias resolution. When a YAML anchor event (`&name`) is encountered, the associated node's event sequence is recorded in an in-memory store keyed by VYaml's internal anchor ID. When a YAML alias event (`*name`) is encountered, the stored event sequence is replayed to the parser core as if the aliased node appeared inline.
- **Parser core contract**: The parser core receives alias-normalized events. It never sees raw `Alias` events on a success path.
- **Supported anchor targets**: scalar, sequence, mapping, step mapping, job mapping (see Spec §3.1.1).
- **Known VYaml behavior**: VYaml's `TryGetCurrentAnchor()` continues to return the last-seen anchor ID for all events following the anchored node (including `MappingEnd`, `SequenceEnd`, etc.). The adapter guards against this by restricting new anchor recordings to opener events (Scalar, MappingStart, SequenceStart) and by skipping anchors that are already stored.
- **Adapter alias failures** are normalized into fatal parse diagnostics at the `WorkflowParser.Parse` entrypoint.
- **Parser core** does not directly manipulate anchor/alias graph structures.

### 0.5 Design

#### 0.5.1 Zero-Allocation Policy

1. Accept UTF-8 input as `ReadOnlySpan<byte>`
2. Use `ReadOnlySpan<byte>` comparisons on hot paths for scalar comparison
3. Store `Utf8Slice` (offset + length) in AST, not `Span<T>`
4. Use `Utf8String` (`ReadOnlyMemory<byte>`-backed) for dictionary keys; never `System.String`
5. Use generated static tables for metadata lookup
6. Do not hold YAML library-specific types outside the adapter layer
7. **`System.String` is banned on the normal success path** — AST node fields, dictionary keys, parse function return types, and intermediate values must use the UTF-8 type vocabulary defined in §0.2.4

#### 0.5.2 When `System.String` is Permitted (Exhaustive)

`System.String` may appear **only** in the following locations:

1. **Diagnostic output** — `Diagnostic.Message`, `Diagnostic.RuleId`, `Diagnostic.Help`
2. **Rule metadata in diagnostics** — rule ID text in diagnostic output
3. **Diagnostic-only adapter method** — `IYamlStreamReader.GetScalarString()`
4. **Compile-time literal parameters** — section name strings passed to error-reporting helpers (e.g., `"jobs"` in `ParseMapping`)

All other locations — AST node fields, dictionary keys, parse function return types, and intermediate values — must use the UTF-8 type vocabulary (§0.2.4).

#### 0.5.3 Things to Avoid

1. DOM construction of the entire YAML
2. Conversion to `Dictionary<string, object>`
3. Post-processing with `string.Split` or regex
4. LINQ during parsing
5. `new T[]`, `List<T>`, `Dictionary<TKey, TValue>` on hot paths
6. `GetScalarString()` on success paths
7. `System.String` in AST fields or dictionary keys (use `Utf8Slice` / `Utf8String`)
8. UTF-16 transcoding on the normal path

#### 0.5.4 UTF-8 Type Vocabulary

The following types form the string representation layer for the C# implementation. The YAML adapter delivers all scalars as UTF-8 bytes (`ReadOnlySpan<byte>`), and the parser preserves this representation throughout the AST.

| Type | Ownership | Lifetime | Use Case |
|---|---|---|---|
| `ReadOnlySpan<byte>` | Non-owning, stack-only | Current parse position only | Transient key matching, value inspection in parse functions |
| `Utf8Slice` | Non-owning (offset + length into input buffer) | Input buffer lifetime | AST scalar values (`StringNode.Value`, etc.) |
| `Utf8String` | Owning (`ReadOnlyMemory<byte>`) | Unbounded | Dictionary keys in AST, case-normalized identifiers |

```csharp
/// Owned immutable UTF-8 byte sequence.
/// Used as dictionary key where the value must outlive the current parse position.
/// Implements IEquatable<Utf8String> and GetHashCode over raw bytes (XXH64 truncated to 32-bit).
/// No UTF-16 transcoding occurs.
public readonly struct Utf8String : IEquatable<Utf8String>
{
    private readonly ReadOnlyMemory<byte> _memory;
    public ReadOnlySpan<byte> Span => _memory.Span;
    public int Length => _memory.Length;

    // Copying construction (for static literals and parse-time keys)
    public Utf8String(ReadOnlySpan<byte> utf8) => _memory = utf8.ToArray();

    // Zero-copy construction (for linter hot paths referencing source YAML)
    internal Utf8String(ReadOnlyMemory<byte> memory) => _memory = memory;

    // Equality and hashing operate directly on UTF-8 bytes
    public bool Equals(Utf8String other) => Span.SequenceEqual(other.Span);
    public override int GetHashCode() => XxHash64.Hash32(_memory.Span);
}
```

**Construction from parse context:**
- From `ReadOnlySpan<byte>`: `new Utf8String(reader.GetScalarUtf8())` — copies the key bytes
- From `Utf8Slice` (copying): `slice.ToUtf8String(sourceBuffer)` — copies the referenced range
- From `Utf8Slice` (zero-copy): `slice.ToUtf8StringZeroCopy(sourceArray)` — wraps `byte[].AsMemory()` without copying; valid only while the source `byte[]` is alive
- Case-normalized: `Utf8String.FromLowerAscii(span)` — copies with ASCII lower-case conversion

**Design rationale:**
- `Utf8Slice` avoids allocation for the vast majority of AST scalar values (names, expressions, etc.) that are read but never used as lookup keys
- `Utf8String` uses `ReadOnlyMemory<byte>` as its backing store. For static/literal keys (generated code, builtins), the copying constructor `Utf8String(ReadOnlySpan<byte>)` allocates a `byte[]`. For linter hot paths where the source YAML `byte[]` is guaranteed to outlive the `Utf8String`, the zero-copy constructor `Utf8String(ReadOnlyMemory<byte>)` wraps a slice of the existing array without allocation
- `System.String` is never constructed on the normal path — the parser operates entirely in UTF-8 byte space

---

## 1. Overall Parser Flow (Spec §1)

### 1.0.1 Input Document Kind Classification (Spec §1.1.2)

C# parser entrypoint classifies input kind before kind-specific parse traversal.

Normative path hints for action-metadata candidate:

- Basename `action.yml` or `action.yaml`
- `.github/actions/<name>/action.yml` or `.github/actions/<name>/action.yaml`

Normative structural hints for finalization:

- Root `jobs` => workflow
- Root `runs` => action-metadata
- Root has both `jobs` and `runs` => `unknown` + ambiguity diagnostic
- Neither `jobs` nor `runs` => fall back to the path-hint candidate kind (e.g., `action.yml` path resolves to action-metadata even without structural confirmation). This enables required-key diagnostics for malformed action metadata files.

Final kind is confirmed from top-level structure; structure has priority over path hint on conflict.

### 1.0.2 Action Metadata Parsing (Spec §2.16)

When `ParseClassified` resolves the document kind to action-metadata, the parser enters action-metadata mode and parses:

- `name`, `description`, `inputs`, `outputs`, `runs`, `branding` sections
- Required-key checks: `description` and `runs` must be present at root level. Missing keys produce error diagnostics at position `1:1`.
- Input/output duplicate key detection
- `runs.using` value parsing and `runs.steps` for composite actions

Implemented in `WorkflowParser.ActionMetadata.cs` (partial class).

### 1.1 Entry Point (Spec §1.1)

```csharp
public static ParseResult Parse(byte[] utf8Yaml, string filePath)
```

- Return: `ParseResult { Workflow?, DiagnosticList Diagnostics, HasFatalError, GetString(StringNodeId), GetString(Utf8Slice), GetUtf8, GetBool/GetInt/GetFloat, GetRange..., CopyDiagnostics(), IDisposable }`
- Returns parse diagnostics even if YAML parsing itself fails; `Workflow` is null
- Errors during AST construction are accumulated, not immediately fatal
- Internal-only: `ParseClassified(byte[] utf8Yaml, string filePath, out AstArena? arena)` additionally returns `DocumentKindClassification` (`PathHintKind`, `FinalKind`, `HasHintMismatch`, `IsAmbiguous`) for linter/CLI routing.

### 1.2 Parse Pipeline

```
Parse(byte[], string)
  1. Create IYamlStreamReader via VYamlStreamAdapter
  2. reader.SkipHeader()
    3. WorkflowParser.ParseWorkflow(reader) → Workflow AST + Diagnostic[]
        4. Return ParseResult
```

### 1.3 Linter Integration

Linter integration behavior for C# is specified in `Seiton_Linter_csharp_spec.md`.

This parser document only assumes the integration boundary from `Seiton_Parser_spec.md` §8:

- Parser emits `ParseResult` (AST + parser diagnostics)
- Linter consumes `ParseResult` as structural input

---

## 2. AST Definitions (Spec §2)

> For field semantics and constraints, see `Seiton_Parser_spec.md` §2.
> Only the C# type structure is defined here.

### 2.1 Primitive Types (Spec §2.6)

```csharp
public sealed class StringNode
{
    public Utf8Slice Value { get; init; }
    public bool Quoted { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

public sealed class BoolNode
{
    public bool Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

public sealed class IntNode
{
    public long Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

public sealed class FloatNode
{
    public double Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}
```

AST design principles:

- Use `sealed class` with `{ get; init; }` properties
- TextRange is held as `TextRange Range` on every node; scalar nodes use scalar locations and structural mapping nodes use composite spans derived from mapping start/end marks
- Nullable types represent YAML omission
- Scalar values use `Utf8Slice` (non-owning reference into input buffer), never `System.String`
- Dictionary keys use `Utf8String` (owned UTF-8 byte copy), never `System.String`
- Collections use `IReadOnlyList<T>` or `IReadOnlyDictionary<Utf8String, TValue>` for public API; internally built with arrays or dictionaries
- No AST node stores `System.String` — see §0.2.1 and §0.2.4 for the complete type vocabulary

### 2.2 Workflow (Spec §2.2)

```csharp
public sealed class Workflow
{
    public StringNode? Name { get; init; }
    public StringNode? RunName { get; init; }
    public IReadOnlyList<Event> On { get; init; } = Array.Empty<Event>();
    public Permissions? Permissions { get; init; }
    public Env? Env { get; init; }
    public Defaults? Defaults { get; init; }
    public Concurrency? Concurrency { get; init; }
    public IReadOnlyDictionary<Utf8String, Job> Jobs { get; init; }
        = new Dictionary<Utf8String, Job>();
    public TextRange Range { get; init; }
}
```

### 2.3 Events (Spec §2.3)

```csharp
public abstract class Event
{
    public StringNode EventName { get; init; }
    public TextRange Range { get; init; }
}

public sealed class WebhookEvent : Event
{
    public StringNode Hook { get; init; }
    public IReadOnlyList<StringNode>? Types { get; init; }
    public WebhookEventFilter? Branches { get; init; }
    public WebhookEventFilter? BranchesIgnore { get; init; }
    public WebhookEventFilter? Tags { get; init; }
    public WebhookEventFilter? TagsIgnore { get; init; }
    public WebhookEventFilter? Paths { get; init; }
    public WebhookEventFilter? PathsIgnore { get; init; }
    public IReadOnlyList<StringNode>? Workflows { get; init; }
}

public sealed class WebhookEventFilter
{
    public StringNode Name { get; init; }
    public IReadOnlyList<StringNode> Values { get; init; }
        = Array.Empty<StringNode>();
}

public sealed class ScheduledEvent : Event
{
    public IReadOnlyList<ScheduleEntry> Schedules { get; init; }
        = Array.Empty<ScheduleEntry>();
}

public sealed class ScheduleEntry
{
    public StringNode? Cron { get; init; }
    public StringNode? Timezone { get; init; }
    public TextRange Range { get; init; }
}

public sealed class WorkflowDispatchEvent : Event
{
    public IReadOnlyDictionary<Utf8String, DispatchInput>? Inputs { get; init; }
}

public sealed class DispatchInput
{
    public StringNode Name { get; init; }
    public StringNode? Description { get; init; }
    public BoolNode? Required { get; init; }
    public StringNode? Default { get; init; }
    public DispatchInputType Type { get; init; }
    public IReadOnlyList<StringNode>? Options { get; init; }
    public TextRange Range { get; init; }
}

public enum DispatchInputType
{
    None,
    String,
    Number,
    Boolean,
    Choice,
    Environment,
}

public sealed class WorkflowCallEvent : Event
{
    public IReadOnlyList<WorkflowCallEventInput>? Inputs { get; init; }
    public IReadOnlyDictionary<Utf8String, WorkflowCallEventSecret>? Secrets { get; init; }
    public IReadOnlyDictionary<Utf8String, WorkflowCallEventOutput>? Outputs { get; init; }
}

public sealed class WorkflowCallEventInput
{
    public StringNode Name { get; init; }
    public Utf8String Id { get; init; }   // lower-case (Utf8String.FromLowerAscii)
    public StringNode? Description { get; init; }
    public BoolNode? Required { get; init; }
    public StringNode? Default { get; init; }
    public WorkflowCallInputType Type { get; init; }
    public TextRange Range { get; init; }
}

public enum WorkflowCallInputType
{
    Invalid,
    Boolean,
    Number,
    String,
}

public sealed class WorkflowCallEventSecret
{
    public StringNode Name { get; init; }
    public StringNode? Description { get; init; }
    public BoolNode? Required { get; init; }
    public TextRange Range { get; init; }
}

public sealed class WorkflowCallEventOutput
{
    public StringNode Name { get; init; }
    public StringNode? Description { get; init; }
    public StringNode? Value { get; init; }
    public TextRange Range { get; init; }
}

public sealed class RepositoryDispatchEvent : Event
{
    public IReadOnlyList<StringNode>? Types { get; init; }
}
```

### 2.4 Job (Spec §2.4)

```csharp
public sealed class Job
{
    public StringNode Id { get; init; }
    public StringNode? Name { get; init; }
    public IReadOnlyList<StringNode>? Needs { get; init; }
    public Runner? RunsOn { get; init; }
    public Permissions? Permissions { get; init; }
    public Environment? Environment { get; init; }
    public Concurrency? Concurrency { get; init; }
    public IReadOnlyDictionary<Utf8String, StringNode>? Outputs { get; init; }
    public Env? Env { get; init; }
    public Defaults? Defaults { get; init; }
    public StringNode? If { get; init; }
    public IReadOnlyList<Step>? Steps { get; init; }
    public FloatNode? TimeoutMinutes { get; init; }
    public Strategy? Strategy { get; init; }
    public BoolNode? ContinueOnError { get; init; }
    public Container? Container { get; init; }
    public Services? Services { get; init; }
    public WorkflowCall? WorkflowCall { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.5 Step and Exec (Spec §2.5)

```csharp
public sealed class Step
{
    public StringNode? Id { get; init; }
    public StringNode? If { get; init; }
    public StringNode? Name { get; init; }
    public StepExec Exec { get; init; }
    public Env? Env { get; init; }
    public BoolNode? ContinueOnError { get; init; }
    public FloatNode? TimeoutMinutes { get; init; }
    public TextRange Range { get; init; }
}

public abstract class StepExec
{
    public StepExecKind Kind { get; init; }
    public TextRange Range { get; init; }
}

public enum StepExecKind { Run, Action }

public sealed class ExecRun : StepExec
{
    public StringNode Run { get; init; }
    public StringNode? Shell { get; init; }
    public StringNode? WorkingDirectory { get; init; }
}

public sealed class ExecAction : StepExec
{
    public StringNode Uses { get; init; }
    public IReadOnlyDictionary<Utf8String, StringNode>? Inputs { get; init; }
    public StringNode? Entrypoint { get; init; }   // docker only
    public StringNode? Args { get; init; }          // docker only
}
```

### 2.6 Structural Nodes (Spec §2.7–§2.11)

```csharp
public sealed class Permissions
{
    public StringNode? All { get; init; }               // "read-all" / "write-all"
    public IReadOnlyDictionary<Utf8String, PermissionScope>? Scopes { get; init; }
    public TextRange Range { get; init; }
}

public sealed class PermissionScope
{
    public StringNode Name { get; init; }
    public StringNode Value { get; init; }
}

public sealed class Env
{
    public StringNode? Expression { get; init; }        // entire ${{ }}
    public IReadOnlyDictionary<Utf8String, EnvVar>? Vars { get; init; }
    public TextRange Range { get; init; }
}

public sealed class EnvVar
{
    public StringNode Name { get; init; }
    public StringNode Value { get; init; }
}

public sealed class Defaults
{
    public DefaultsRun Run { get; init; }
    public TextRange Range { get; init; }
}

public sealed class DefaultsRun
{
    public StringNode? Shell { get; init; }
    public StringNode? WorkingDirectory { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Concurrency
{
    public StringNode Group { get; init; }
    public BoolNode? CancelInProgress { get; init; }
    public StringNode? Queue { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Environment
{
    public StringNode Name { get; init; }
    public StringNode? Url { get; init; }
    public BoolNode? Deployment { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Runner
{
    public IReadOnlyList<StringNode>? Labels { get; init; }
    public StringNode? LabelsExpr { get; init; }
    public StringNode? Group { get; init; }
    public TextRange Range { get; init; }
}

public sealed class WorkflowCall
{
    public StringNode Uses { get; init; }
    public Dictionary<Utf8String, WorkflowCallInput>? Inputs { get; init; }
    public Dictionary<Utf8String, WorkflowCallSecret>? Secrets { get; init; }
    public bool InheritSecrets { get; init; }
}
```

### 2.7 Strategy / Matrix (Spec §2.13)

```csharp
public sealed class Strategy
{
    public Matrix? Matrix { get; init; }
    public BoolNode? FailFast { get; init; }
    public IntNode? MaxParallel { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Matrix
{
    public StringNode? Expression { get; init; }
    public IReadOnlyList<MatrixCombinations>? Include { get; init; }
    public IReadOnlyList<MatrixCombinations>? Exclude { get; init; }
    public IReadOnlyDictionary<Utf8String, MatrixRow>? Rows { get; init; }
    public TextRange Range { get; init; }
}

public sealed class MatrixRow
{
    public StringNode? Expression { get; init; }
    public IReadOnlyList<RawYamlValue>? Values { get; init; }
    public StringNode Name { get; init; }
}

public sealed class MatrixCombinations
{
    public StringNode? Expression { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<Utf8String, RawYamlValue>>? Entries { get; init; }
}
```

### 2.8 Container / Services / Credentials (Spec §2.14)

```csharp
public sealed class Container
{
    public StringNode Image { get; init; }
    public Credentials? Credentials { get; init; }
    public Env? Env { get; init; }
    public IReadOnlyList<StringNode>? Ports { get; init; }
    public IReadOnlyList<StringNode>? Volumes { get; init; }
    public StringNode? Options { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Services
{
    public StringNode? Expression { get; init; }
    public IReadOnlyDictionary<Utf8String, Service>? ServiceMap { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Service
{
    public StringNode Name { get; init; }
    public Container Container { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Credentials
{
    public StringNode? Username { get; init; }
    public StringNode? Password { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}
```

Implementation notes:

- `Services` accepts either a mapping of named services or a single expression scalar.
- `Credentials` accepts either an expression scalar or a mapping with required `username` + `password`.
- Container-level and service-level `env` reuse the same expression-or-mapping `Env` shape as top-level / job-level `env`.

### 2.9 RawYAMLValue

```csharp
public abstract class RawYamlValue { }
public sealed class RawYamlString : RawYamlValue
{
    public StringNode Value { get; init; }
}
public sealed class RawYamlArray : RawYamlValue
{
    public IReadOnlyList<RawYamlValue> Items { get; init; }
}
public sealed class RawYamlObject : RawYamlValue
{
    public IReadOnlyDictionary<Utf8String, RawYamlValue> Properties { get; init; }
}
```

---

## 3. Parse Algorithms (Spec §3)

> For the full parse function → C# method mapping table, see Appendix A.

### 3.1 Parser State

```csharp
public ref struct WorkflowParser
{
    private IYamlStreamReader _reader;
    private List<Diagnostic> _diagnostics;
}
```

The parser accumulates diagnostics in a list and never aborts on the first error (multi-error recovery, Spec §5.1).

### 3.2 Mapping Traversal (Spec §3.3)

Mapping traversal is **inlined** at each call site rather than using a callback delegate. This is because `ReadOnlySpan<byte>` (the key representation) cannot be captured in `Action<T>` delegates.

```csharp
// Conceptual pattern — each parse function embeds this loop directly.
// No callback delegate; key matching is performed inline via ReadOnlySpan<byte>.
private void ParseMappingInline(
    ReadOnlySpan<byte> sectionNameUtf8,  // for diagnostics only
    bool allowEmpty,
    bool caseSensitive)
{
    // 1. Check for null scalar → allowEmpty ? return : error
    // 2. Expect MappingStart
    // 3. Loop: Read key as ReadOnlySpan<byte>
    //    - Normalize to lower-case via Ascii.ToLower if !caseSensitive
    //    - Detect duplicate keys (tracked via Utf8String set)
    //    - Detect "<<" merge key → error
    //    - Switch on key bytes:
    //        if (keyUtf8.SequenceEqual("name"u8)) ...
    //        else if (keyUtf8.SequenceEqual("jobs"u8)) ...
    //        else → UnexpectedKey
    // 4. If !allowEmpty && 0 entries → error
}
```

**Key design:** The caller reads `reader.GetScalarUtf8()` to get the key as `ReadOnlySpan<byte>`, performs `SequenceEqual` comparisons against UTF-8 literal constants (`"name"u8`, `"jobs"u8`, etc.), and never materializes `System.String`.

### 3.3 Workflow Parse (Spec §3.2)

```csharp
private Workflow ParseWorkflow(IYamlStreamReader reader)
```

Top-level mapping traversal:
- `"name"` → `ParseString`
- `"run-name"` → `ParseString`
- `"on"` → `ParseEvents`
- `"permissions"` → `ParsePermissions`
- `"env"` → `ParseEnv`
- `"defaults"` → `ParseDefaults`
- `"concurrency"` → `ParseConcurrency`
- `"jobs"` → `ParseJobs`
- Other → `unexpectedKey`

Post-validation: `on` and `jobs` are required.

### 3.4 Events Parse (Spec §3.4)

```csharp
private IReadOnlyList<Event> ParseEvents(IYamlStreamReader reader)
private Event ParseEventWithNoConfig(IYamlStreamReader reader)
```

Three forms: scalar → single event, sequence → multiple events, mapping → events with config.

For mapping form, dispatches by event name:
- `"schedule"` → `ParseScheduleEvent`
- `"workflow_dispatch"` → `ParseWorkflowDispatchEvent`
- `"repository_dispatch"` → `ParseRepositoryDispatchEvent`
- `"workflow_call"` → `ParseWorkflowCallEvent`
- other → `ParseWebhookEvent`

### 3.5 Structural Section Parse (Spec §3.5–§3.8)

```csharp
private Permissions? ParsePermissions(IYamlStreamReader reader)   // Spec §3.5
private Env? ParseEnv(IYamlStreamReader reader)                   // Spec §3.6
private Defaults? ParseDefaults(IYamlStreamReader reader)         // Spec §3.7
private Concurrency? ParseConcurrency(IYamlStreamReader reader)   // Spec §3.8
private Environment? ParseEnvironment(IYamlStreamReader reader)   // Spec §3.14
private IReadOnlyDictionary<Utf8String, StringNode>? ParseOutputs(IYamlStreamReader reader) // Spec §3.10
```

### 3.6 Job Parse (Spec §3.9–§3.10)

```csharp
private IReadOnlyDictionary<Utf8String, Job> ParseJobs(IYamlStreamReader reader) // Spec §3.9
private Job ParseJob(StringNode id, IYamlStreamReader reader)                // Spec §3.10
private Runner? ParseRunsOn(IYamlStreamReader reader)                        // Spec §3.13
private FloatNode? ParseTimeoutMinutes(IYamlStreamReader reader)             // validates > 0
```

Job parsing includes reusable workflow detection and constraint validation (Spec §3.10.1):
- If `uses` is present → reusable workflow call; certain keys are forbidden
- If `uses` is absent → normal job; `steps` and `runs-on` are required

Implementation note (2026-04-14):
- C# implementation emits these job-structure diagnostics at parse time in `WorkflowParser.ParseJobNode`.
- Lint rules may still report overlapping diagnostics for AST-only visitor scenarios, but parser output is the primary contract.

### 3.7 Step Parse (Spec §3.11–§3.12)

```csharp
private IReadOnlyList<Step> ParseSteps(IYamlStreamReader reader)                     // Spec §3.11
private Step ParseStep(IYamlStreamReader reader)                                      // Spec §3.12
private ExecAction ParseStepExecAction(/* entries */, bool isDocker)                  // Spec §3.12.1
private ExecRun ParseStepExecRun(/* entries */)                                       // Spec §3.12.2
```

Step parsing uses a **2-pass design** (Spec §3.12):
1. **Pass 1**: Collect all entries, find `run` or `uses` key
2. **Pass 2**: Dispatch to `ParseStepExecRun` or `ParseStepExecAction`

### 3.8 Strategy / Matrix Parse (Spec §3.15)

```csharp
private Strategy? ParseStrategy(IYamlStreamReader reader)
private Matrix? ParseMatrix(IYamlStreamReader reader)
private MatrixCombinations? ParseMatrixCombinations(string section, IYamlStreamReader reader)
private RawYamlValue ParseRawYamlValue(IYamlStreamReader reader)
```

### 3.9 Container / Services Parse (Spec §3.16–§3.18)

```csharp
private Container? ParseContainer(string section, IYamlStreamReader reader)  // Spec §3.16
private Services? ParseServices(IYamlStreamReader reader)                    // Spec §3.17
private Credentials? ParseCredentials(IYamlStreamReader reader)              // Spec §3.18
```

---

## 4. Scalar Parsing Helpers (Spec §4)

Scalar tag information (`!!str`, `!!bool`, `!!int`, `!!float`, `!!null`) is obtained via `IYamlStreamReader.GetScalarTag()`, which returns a `ScalarTag` enum normalized by the adapter layer.

### 4.1 parseString (Spec §4.1)

```csharp
private StringNode? ParseString(IYamlStreamReader reader, bool allowEmpty = false)
```

### 4.2 parseBool (Spec §4.2)

```csharp
private BoolNode? ParseBool(IYamlStreamReader reader)
```

### 4.3 parseInt (Spec §4.3)

```csharp
private IntNode? ParseInt(IYamlStreamReader reader)
```

### 4.4 parseFloat (Spec §4.4)

```csharp
private FloatNode? ParseFloat(IYamlStreamReader reader)
```

### 4.5 parseExpression (Spec §4.5)

```csharp
private StringNode? ParseExpression(IYamlStreamReader reader, string expecting)
```

### 4.6 mayParseExpression (Spec §4.6)

```csharp
private StringNode? MayParseExpression(IYamlStreamReader reader)
```

### 4.7 Collection Helpers (Spec §4.7)

```csharp
private IReadOnlyList<StringNode> ParseStringOrStringSequence(
    string section, IYamlStreamReader reader, bool allowEmpty = false, bool allowElemEmpty = false)
```

---

## 5. Error Recovery (Spec §5)

### 5.1 Error Reporting

```csharp
private void AddError(TextRange location, string message)
private void AddErrorf(TextRange location, string format, params object[] args)
private void UnexpectedKey(StringNode key, string section, string[] expected)
private void MissingExpression(TextRange location, string expecting)
```

### 5.2 Recovery Patterns

The parser never aborts on a single error. Each parse function:
1. Validates the current event kind
2. On mismatch, reports an error and calls `reader.SkipCurrentNode()`
3. The caller continues processing remaining entries

| Situation | Recovery |
|---|---|
| Unknown key | error + `SkipCurrentNode()` |
| Type mismatch | error + `SkipCurrentNode()` |
| Missing required key | aggregate error after mapping traversal |
| Exclusive constraint violation | aggregate error after mapping traversal |
| YAML parse failure | Add a fatal `yaml parse failure` diagnostic and preserve any parser diagnostics already emitted earlier in the same file; AST may be partial or null |
| Duplicate key | error + ignore the later key (first wins) |

#### Fatal Parse Explanatory Hints (C# Implementation)

After a fatal YAML parse, `ParseCore`, `ParseClassified`, and `ParseIncremental` catch blocks first require a reliable VYaml position in the exception message (`Line:`, `Col:`, and parseable `Idx:` markers), then call `TryGetPlainScalarColonHint(source, errorOffset)` to detect common authoring mistakes. If a `run:` or `script:` key with a plain scalar value containing `: ` is found near the error position, the diagnostic's `Help` field is populated with an explanatory message.

Implementation: `WorkflowParser.PlainScalarHint.cs` (partial class).

Heuristic conditions (all must be true):
1. Exception message contains reliable VYaml position markers (`Line:`, `Col:`, `Idx:`) and `Idx:` parses successfully
2. Error line or up to 3 lines above contains `run:` or `script:` as a YAML key
3. YAML node properties (`&anchor`, `!tag`) are skipped before determining the scalar kind
4. Horizontal whitespace after the key or node property may be spaces or tabs
5. The value after the key starts as a plain scalar (not `'`, `"`, `|`, `>`, `#`, `[`, `{`, `*`)
6. The plain scalar value itself (excluding inline comments) contains `: ` (colon-space)
7. Empty/comment-only values (for example `run: # note`) do not trigger the hint

Performance: runs only on the error path (no impact on success-path parsing).

---

## 6. Expression Parser (Spec §6)

### 6.1 Lexer (Spec §6.3)

Inline lexing within `ExpressionParser`. Tokenizes the expression string during recursive descent.

**Double-quote rejection:** When the lexer encounters a `"` character, it emits a diagnostic ("only single quotes are available for string delimiter in expressions") and skips to the closing `"` for error recovery. GitHub Actions expressions only support single-quoted string literals.

### 6.2 Parser (Spec §6.2)

```csharp
public static class ExpressionParser
{
    public static ExpressionNode[] Parse(ReadOnlySpan<char> expression, out int[] arguments);
}
```

**Recursive descent precedence layers (lowest to highest):**

| Precedence | Method | Operators |
|---|---|---|
| 1 (lowest) | `ParseOr()` | `\|\|` |
| 2 | `ParseAnd()` | `&&` |
| 3 | `ParseEquality()` + `ParseRelational()` | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| 4 | `ParseUnary()` | `!` (unary) |
| 5 | Loop within `ParsePrimary()` | `.prop`, `.*`, `[idx]`, `(args)` |
| 6 (highest) | `ParsePrimary()` | literals, identifiers, `(expr)` |

**Note**: Arithmetic operators (`+`, `-`, `*`, `/`, `%`) are intentionally not supported to align with the GitHub Actions expression spec.

### 6.3 Expression AST (Spec §6.4)

| Spec Node | C# Counterpart |
|---|---|
| `VariableNode` | `Identifier` |
| `ObjectDerefNode` | `MemberAccess` |
| `ArrayDerefNode` | `WildcardAccess` |
| `IndexAccessNode` | `IndexAccess` |
| `FuncCallNode` | `FunctionCall` |
| `NotOpNode` | `Unary (Not)` |
| `CompareOpNode` | `Binary (Equal/NotEqual/Less/…)` |
| `LogicalOpNode` | `Binary (And/Or)` |

### 6.4 Expression Visitor (Spec §6.5)

```csharp
public delegate void ExprNodeVisitor(ExpressionNode node, int parentId, bool entering);

public static void VisitExprNode(
    int nodeId,
    ExpressionNode[] nodes,
    int[] arguments,
    ExprNodeVisitor visitor);
```

---

## 7. Expression Semantic Analysis (Spec §7)

### 7.1 Function Signatures (Spec §7.1)

```csharp
// Current implementation: typed overload-based signature check
// Function name is received as ReadOnlySpan<byte> for zero-allocation lookup.
public static bool TryGetFunctionArity(ReadOnlySpan<byte> nameUtf8, out int minArgs, out int maxArgs)
```

Function name lookup uses UTF-8 byte comparison against a static table of known function names (`"contains"u8`, `"startsWith"u8`, etc.). No `System.String` is materialized for function resolution.

The current C# implementation validates built-in functions through typed overload metadata in `ExpressionSemanticAnalyzer`:
- overload resolution is performed by argument count and `ExprType` compatibility
- diagnostics are emitted for unknown functions, arity mismatches, argument type mismatches, and `format()` placeholder/index mismatches
- supported built-ins currently include `contains`, `startsWith`, `endsWith`, `format`, `join`, `toJson`, `fromJson`, `hashFiles`, `success`, `failure`, `cancelled`, and `always`

This list defines the current C# contract. Additional actionlint built-ins that are not listed here should be treated as reference parity gaps until they are added to `Seiton_Parser_spec.md` and implemented here.

#### 7.1.1 Function Context Restrictions

Two categories of built-in functions have position-dependent availability:

1. **Status check functions** (`success`, `failure`, `cancelled`, `always`): Only available in `if` conditions. Controlled by the `allowStatusCheckFunctions` flag passed from parser call sites.
2. **`hashFiles` function**: Only available in step-level keys (`jobs.<job_id>.steps.*`). Controlled by `Availability.IsStepLevel(context)` check in `ValidateFunctionCall`. Using `hashFiles()` in workflow-level env, job.if, strategy, or other non-step contexts emits an error diagnostic.

### 7.2 Context Availability (Spec §7.2)

```csharp
// Current implementation: generated root-context validation at expression positions
public class ExpressionSemanticAnalyzer
{
    // Checks that root identifiers (github, env, steps, etc.)
    // are available for the current expression position / key location
}
```

The current C# implementation uses `Availability.g.cs` together with the parser call site to enforce position-dependent root availability. The `ExpressionValidationContext` enum is **auto-generated** from `availability.json` with one value per workflow key (34 entries), providing per-key context availability:

- Each workflow key (e.g., `run-name`, `jobs.<job_id>.env`, `jobs.<job_id>.steps.run`) maps to a distinct `ExpressionValidationContext` enum value (e.g., `RunName`, `JobEnv`, `StepRun`).
- The generated `Availability.g.cs` contains:
  - Per-key `byte[][]` root arrays with the exact context roots allowed at that position.
  - `IsRootContextAvailable(context, rootName)` — switch dispatching to the per-key array.
  - `IsStepLevel(context)` — returns true for all `Step*` enum values.
  - `GetContextText(context)` — parser-level category text (e.g., "workflow", "job", "strategy", "step", "step if", "job if").
  - `GetLintCategoryText(context)` — lint-level collapsed text (e.g., "workflow", "job", "step").
- The hand-written enum was removed; it is now generated by `AvailabilityCSharpGenerator` from `availability.json`.
- Parser call sites pass the exact per-key enum value (e.g., `ExpressionValidationContext.StepRun` for `steps.run`, `ExpressionValidationContext.JobIf` for `jobs.<job_id>.if`).
- This design means context check changes require only parser-side call site updates — no pipeline changes needed.

This implements the current C# contract for position-based root-context availability with key-level granularity for all 34 workflow keys Seiton models.

### 7.3 Type System (Spec §7.3)

The current C# implementation provides an `ExprType` hierarchy equivalent in shape to actionlint's core expression types:
- `AnyType` / `NullType` / `BoolType` / `NumberType` / `StringType`
- `ObjectType` (properties map) / `ArrayType` (element type)
- `EmptyObjectType` / `EmptyArrayType`

Type inference is performed bottom-up in `ExpressionSemanticAnalyzer.InferType()` while traversing expressions.

Implemented inference currently covers the current C# contract:
- literals, unary `!`, comparison/logical operators
- member/index/wildcard access over inferred object/array types
- built-in function return types
- `fromJson('<literal-json>')` shape inference for object/array/property/index access

Additional inference behavior from the reference implementation should be described as parity work, not as silently implied current scope.

---

## 8. Linter Integration Reference

Linter-side implementation details are intentionally out of scope in this parser document.

- C# linter runtime contract and implementation mapping: `Seiton_Linter_csharp_spec.md`
- Language-agnostic linter contract: `Seiton_Linter_spec.md`

This section remains as a boundary marker so the §0–§11 outline stays consistent across language companion documents.

---

## 9. Generated Data (Spec §9)

The generated-data pipeline specification has been moved to `Seiton_Update_spec.md`.

This section remains as a boundary marker so the §0–§11 outline stays consistent across language companion documents.

### 9.1 C# Generated Files

| Data | File | Description |
|---|---|---|
| Webhook event + activity types | `WebhookTypes.g.cs` | Static table mapping event names to allowed activity types, filter options, and allowed option names for suggestion |
| Context availability | `Availability.g.cs` | Which expression contexts and special functions are available at each workflow position |
| Popular actions metadata | `PopularActions.g.cs` | Well-known GitHub Actions with expected input/output names and types |
| Context type definitions | `ContextTypes.g.cs` | Built-in context type schemas for all 11 context roots (source: `data/sources/context-types/github/context-types.json`) |
| Function signatures | `FunctionSpecs.g.cs` | Built-in function specs with parameter types and overloads (source: `data/sources/function-specs/github/function-specs.json`) |

For pipeline architecture, CLI commands, data paths, update policy, and conflict resolution, see `Seiton_Update_spec.md`.

### 9.2 C#-Specific Notes

#### 9.2.1 Relationship with Current `OnEventSpecs`

`OnEventSpecs` is a hand-implemented event name + activity types table. It is an implementation detail that may later be replaced by `WebhookTypes.g.cs`; this migration does not change Seiton's current support contract by itself.

---

## 10. Diagnostic Model (Spec §10)

### 10.1 Diagnostic Structure (Spec §10.1)

```csharp
public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null,
    string? FilePath = null,
    DiagnosticFix? Fix = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public readonly record struct DiagnosticFix(
    string Description,
    TextEdit[] Edits);

public readonly record struct TextEdit(
    int Offset,
    int Length,
    string NewText);
```

TextRange:

```csharp
public readonly record struct TextRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
```

### 10.2 Location Policy (Spec §10.2)

| Situation | Primary location |
|---|---|
| Unknown key | Key position |
| Type mismatch | Value position |
| Missing required key | Section start position |
| Exclusive constraint violation | Position of the causative key |
| Duplicate key | Position of the 2nd key |
| Expression error | Offset within expression mapped to source position |

### 10.3 Parser-Originated Auto-Fix (Spec §3.4.2a)

The parser attaches `DiagnosticFix` on error paths where a deterministic fix is available.

**Implementation:**
- `SuggestionHelper.FindClosest(input, candidates)` — Case-insensitive Levenshtein distance-based suggestion utility in `Parsing/SuggestionHelper.cs`. Used only on error paths (allocations acceptable).
- `SuggestionHelper.FindClosestFromFormattedKeys(input, formattedKeys)` — Parses pre-formatted `ExpectedKeys` const strings (e.g. `"\"a\", \"b\""`) into candidates and delegates to `FindClosest`. Used for sections whose expected keys come from `Generated/ExpectedKeys.g.cs`.
- `EventSpec.GetAllowedOptionNames()` — generated method in `WebhookTypes.g.cs` returning `string[]` of valid option names per event.
- Fix uses `Utf8Slice` (captured before `reader.Read()`) for byte offset and length of the key to replace.

**Message ordering:** When a suggestion is found, `did you mean "{suggestion}"?` always appears **before** `expected one of {list}`.

**Fixable parser diagnostics:**
- Unknown webhook event option with close Levenshtein match → replaces key with suggestion
- Unknown `image_version` option with close match → replaces key with suggestion (candidates: `names`, `versions`)

**Suggestion-only diagnostics (no auto-fix, all sections):**
- Workflow/action-metadata top-level keys, job keys, step keys, container/service/credential keys, defaults/defaults.run keys, concurrency keys, strategy keys, runs-on keys, schedule/repository_dispatch/workflow_call/workflow_dispatch keys, action metadata input/output/branding/runs keys

---

## 11. Design Decisions

### 11.1 String-Free Normal Path

The C# implementation enforces a **`System.String`-free normal path**: no AST node, dictionary key, or parse function intermediate value uses `System.String`. The UTF-8 type vocabulary (§0.2.4) provides three tiers:

- `ReadOnlySpan<byte>` for transient comparisons (key matching in mapping loops)
- `Utf8Slice` for AST scalar values (non-owning reference into the input buffer)
- `Utf8String` for dictionary keys (owned copy of UTF-8 bytes)

This constraint is enforced at the language specification level, not as a guideline. Any code change introducing `System.String` on the success path is a spec violation.

`System.String` appears only in diagnostic output, rule metadata, and compile-time literal parameters — see §0.2.2 for the exhaustive list.

### 11.2 Event-Stream vs DOM

The C# implementation uses an event-stream parser (via `IYamlStreamReader`) rather than a DOM tree. This enables zero-allocation parsing where the YAML document is never fully materialized in memory. Trade-off: no random access to nodes (compared to Go's `yaml.Node` tree model).

### 11.3 ref struct Adapter → Generic Type Parameter

`ref struct` cannot implement interfaces in current C#. To maintain both the `ref struct` performance advantage and the adapter abstraction:

- Use `WorkflowParser<TReader> where TReader : IYamlStreamReader` to enable JIT devirtualization
- All parse functions become generic on `TReader`
- The concrete adapter type is known at compile-time, eliminating virtual dispatch overhead

### 11.4 Two-Pass Step Parsing (Spec §3.12)

Same pattern as Go: steps are parsed in two passes because the step kind determines which keys are valid, but the `run`/`uses` key may appear anywhere in the mapping. With event-stream parsing, entries are buffered during Pass 1 since they cannot be re-read.

### 11.5 Polymorphic YAML Fields (Spec §14)

The event-stream equivalent of Go's `switch n.Kind` pattern:

```csharp
// Check current event kind for polymorphic dispatch
switch (reader.CurrentKind)
{
    case YamlEventKind.Scalar:
        // simple form
        break;
    case YamlEventKind.MappingStart:
        // detailed form
        break;
    default:
        AddError(reader.CurrentStart, "...");
        reader.SkipCurrentNode();
        break;
}
```

### 11.6 Case Sensitivity (Spec §13)

Same rules as Go. The `ParseMapping` helper supports case-insensitive mode via `ReadOnlySpan<byte>` comparison with `ToLowerInvariant` equivalence, avoiding string materialization.

---

## Appendix A: Seiton Parser Function → C# Mapping

> The "Spec Function" column lists the canonical function names defined in `Seiton_Parser_spec.md` §1–§4.
> The "C# Conceptual counterpart" column points to the closest corresponding code location in C#. Exact method signatures are listed when they exist verbatim; otherwise the entry names the representative helper or dispatch site that owns the behavior.

### A.1 Entry Point

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `Parse(utf8Yaml, filePath)` | `WorkflowParser.Parse(byte[], string)` | §1.1 |

### A.2 Workflow-Level Parse Functions

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `ParseWorkflow(utf8Yaml)` | `WorkflowParser.ParseCoreInner<TReader>(...)` | §3.2 |
| `ParseEvents(node)` | `WorkflowParser.ParseOnEvents<TReader>(...)` | §3.4 |
| `ParsePermissions(node)` | `WorkflowParser.ParsePermissionsNode<TReader>(...)` | §3.5 |
| `ParseEnv(node)` | `WorkflowParser.ParseEnvNode<TReader>(...)` | §3.6 |
| `ParseDefaults(node)` | `WorkflowParser.ParseDefaultsNode<TReader>(...)` | §3.7 |
| `ParseConcurrency(node)` | `WorkflowParser.ParseConcurrencyNode<TReader>(...)` | §3.8 |
| `ParseJobs(node)` | `WorkflowParser.ParseCoreInner<TReader>(...)` root dispatch + `WorkflowParser.ParseJobNode<TReader>(...)` | §3.9 |

### A.3 Event Parse Functions

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `parseEventWithNoConfig(node)` | `BuildSimpleEvent(...)` and scalar/sequence branches in `WorkflowParser.ParseOnEvents<TReader>(...)` | §3.4.1 |
| `ParseWebhookEvent(name, configNode)` | `WorkflowParser.ParseWebhookEventWithOptions<TReader>(...)` | §3.4.2 |
| `parseWebhookEventFilter(name, node)` | Inline filter branches inside `WorkflowParser.ParseWebhookEventWithOptions<TReader>(...)` | §3.4.2 |
| `ParseScheduleEvent(pos, node)` | `WorkflowParser.ParseScheduleEvent<TReader>(...)` | §3.4 |
| `ParseWorkflowDispatchEvent(pos, node)` | `WorkflowParser.ParseWorkflowDispatchEvent<TReader>(...)` | §3.4 |
| `ParseWorkflowCallEvent(pos, node)` | `WorkflowParser.ParseWorkflowCallEvent<TReader>(...)` | §3.4 |
| `ParseRepositoryDispatchEvent(pos, node)` | `WorkflowParser.ParseRepositoryDispatchEvent<TReader>(...)` | §3.4 |

### A.4 Job / Step Parse Functions

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `ParseJob(id, node)` | `WorkflowParser.ParseJobNode<TReader>(...)` | §3.10 |
| `ParseSteps(node)` | `WorkflowParser.ParseSteps<TReader>(...)` | §3.11 |
| `ParseStep(node)` | `WorkflowParser.ParseStep<TReader>(...)` | §3.12 |
| `parseStepExecAction(entries, isDocker)` | Inline assembly in `WorkflowParser.ParseStep<TReader>(...)` | §3.12.1 |
| `parseStepExecRun(entries)` | Inline assembly in `WorkflowParser.ParseStep<TReader>(...)` | §3.12.2 |

### A.5 Structural Section Parse Functions

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `ParseRunsOn(node)` | `WorkflowParser.ParseRunsOnNode<TReader>(...)` | §3.13 |
| `ParseEnvironment(node)` | `WorkflowParser.ParseEnvironmentNode<TReader>(...)` | §3.14 |
| `ParseOutputs(node)` | `WorkflowParser.ParseOutputsNode<TReader>(...)` | §3.10 |
| `ParseStrategy(node)` | `WorkflowParser.ParseStrategy<TReader>(...)` | §3.15 |
| `ParseMatrix(node)` | `WorkflowParser.ParseMatrix<TReader>(...)` | §3.15 |
| `parseMatrixCombinations(sec, node)` | `WorkflowParser.ParseMatrixCombinations<TReader>(...)` | §3.15 |
| `parseRawYAMLValue(node)` | `WorkflowParser.ParseRawYamlValue<TReader>(...)` | §3.15 |
| `ParseContainer(section, node)` | `WorkflowParser.ParseContainerLike<TReader>(...)` | §3.16 |
| `ParseServices(node)` | `WorkflowParser.ParseServices<TReader>(...)` | §3.17 |
| `ParseCredentials(node)` | `WorkflowParser.ParseCredentials<TReader>(...)` | §3.18 |

### A.6 Generic Mapping / Collection Helpers

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `ParseMapping(sectionName, allowEmpty, caseSensitive)` | Inline mapping traversal pattern + `TryRegisterDynamicKey(...)` | §3.3 |
| `parseStringOrStringSequence(sec, node, allowEmpty, allowElemEmpty)` | `WorkflowParser.ParseStringOrStringSequence<TReader>(...)` | §4.7 |

### A.7 Scalar Helpers

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `parseString(node, allowEmpty)` | `WorkflowParser.ParseString<TReader>(...)` | §4.1 |
| `parseBool(node)` | `WorkflowParser.ParseBool<TReader>(...)` | §4.2 |
| `parseInt(node)` | `WorkflowParser.ParseInt<TReader>(...)` | §4.3 |
| `parseFloat(node)` | `WorkflowParser.ParseFloat<TReader>(...)` | §4.4 |
| `parseExpression(node, expecting)` | `WorkflowParser.ParseExpression<TReader>(...)` | §4.5 |
| `mayParseExpression(node)` | `WorkflowParser.MayParseExpression<TReader>(...)` | §4.6 |
| `parseTimeoutMinutes(node)` | Inline `ParseFloat<TReader>(...)` + `> 0` validation in job/step parse sites | §3.10 |

### A.8 Visitor / Pass

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `Visitor.Visit(workflow)` | `WorkflowVisitor.Visit(Workflow)` | `Seiton_Linter_spec.md` §4.2 |
| `Pass` interface | `IPass` | `Seiton_Linter_spec.md` §4.1 |
| `Rule` interface | `IRule : IPass` | `Seiton_Linter_spec.md` §4.3 |

### A.9 Alias Resolution

| Spec Function | C# Conceptual counterpart | Spec § |
|---|---|---|
| `resolveAliases(root)` | Handled by YAML adapter layer (`VYaml`) | §1.1 step 1b |

## Appendix B: Seiton Expression Parser → C# Mapping

> The "Spec Element" column lists the canonical expression parser components defined in `Seiton_Parser_spec.md` §6–§7.
> The "C# Counterpart" column shows the actual C# type or method name in the `ExpressionParser` nested class.

| Spec Element | C# Counterpart |
|---|---|
| Expression Lexer (§6.3) | Inline lexing within `ExpressionParser` |
| `parseLogicalOr` (§6.2) | `ExpressionParser.ParseOr()` |
| `parseLogicalAnd` (§6.2) | `ExpressionParser.ParseAnd()` |
| `parseComparison` (§6.2) | `ExpressionParser.ParseEquality()` + `ParseRelational()` |
| `parsePrimary` (§6.2) | `ExpressionParser.ParsePrimary()` |
| `parseIdent` (§6.2) | `ExpressionParser.ParseKeywordOrIdentifier()` |
| `parsePostfix` (§6.2) | Loop within `ExpressionParser.ParsePrimary()` |
| `VariableNode` (§6.4) | `Identifier` |
| `ObjectDerefNode` (§6.4) | `MemberAccess` |
| `ArrayDerefNode` (§6.4) | `WildcardAccess` |
| `IndexAccessNode` (§6.4) | `IndexAccess` |
| `FuncCallNode` (§6.4) | `FunctionCall` |
| `NotOpNode` (§6.4) | `Unary (Not)` |
| `CompareOpNode` (§6.4) | `Binary (Equal/NotEqual/Less/…)` |
| `LogicalOpNode` (§6.4) | `Binary (And/Or)` |
| arithmetic ops | — (not supported, aligned with GHA spec) |
| Expression Visitor (§6.5) | `ExprNodeVisitor` delegate + `VisitExprNode()` |
| Expression Semantic Checker (§7) | `ExpressionSemanticAnalyzer` |
| Built-in Function Signatures (§7.1) | `TryGetFunctionArity()` + typed overload metadata |
| Context Availability (§7.2) | Generated availability checks |
| ExprType hierarchy (§7.3) | `ExprType` hierarchy + `InferType()` |
