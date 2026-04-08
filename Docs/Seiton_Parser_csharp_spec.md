# Seiton Parser C# Implementation Specification

> Implementation specification for the parser described in `Seiton_Parser_spec.md`, targeting C# with zero-allocation / high-performance design.

---

## 0. Gap Analysis of Current C# Implementation

### 0.1 Features Missing Compared to actionlint (Go)

Differences between `.references/actionlint-main` implementation and `src/Seiton.Core/Parsing`.

| Category | Implemented in actionlint | Current C# State |
|---|---|---|
| **AST Construction** | Parser returns typed AST (`Workflow`, `Job`, `Step`, …) | `WorkflowDocument` has only `HasName` / `HasJobs` flags. No typed Job/Step/Event nodes |
| **Event Detail Parse** | Dedicated parsers for `schedule`, `workflow_dispatch`, `workflow_call`, `repository_dispatch`, `image_version` | Key name and option validation exists for `on`, but no structured AST nodes generated |
| **workflow_dispatch inputs** | `type` (string/number/boolean/choice/environment), `options`, `required`, `default` parsed individually | Not implemented |
| **workflow_call inputs/secrets/outputs** | Required validation for `type` on inputs, `required` on secrets, `value` on outputs | Not implemented |
| **schedule cron/timezone** | `cron` / `timezone` keys parsed individually in mapping | Not implemented |
| **Permissions Structure** | scalar (`read-all` / `write-all`) or mapping (scope → value) returned as typed node | skip only |
| **Defaults / Concurrency** | `defaults.run.shell`, `defaults.run.working-directory` returned as typed node | skip only |
| **Environment** | scalar (name) or mapping (`name`, `url`, `deployment`) as typed node | Not implemented |
| **Runner (runs-on)** | scalar/sequence → labels, mapping → `labels` + `group`, expression supported | Shape validation only, no typed node |
| **Step ExecRun / ExecAction** | `run` step → `ExecRun`, `uses` step → `ExecAction` as variant. Docker step separates `entrypoint` / `args` | `hasRun` / `hasUses` flags only |
| **Matrix & Strategy** | `matrix` row/include/exclude recursively parsed as `RawYAMLValue`, `fail-fast` / `max-parallel` typed | Shape validation only |
| **Container / Services** | `Container` node (image, credentials, env, ports, volumes, options), Services as `map[string]*Service` | Shape validation only |
| **YAML Alias Resolution** | All aliases resolved before parsing; recursive aliases detected and reported as errors | Not implemented (VYaml may resolve internally) |
| **Duplicate Key Detection** | Case-insensitive duplicate key detection during mapping traversal | Not implemented |
| **Visitor / Pass** | `Pass` interface → `WorkflowPre → JobPre → Step → JobPost → WorkflowPost` | Does not exist |
| **Rule Engine** | `Rule` interface × 15+ rules | Does not exist |
| **Expression Type System** | `ExprType` hierarchy + `ExprSemanticsChecker` with type inference and availability checking | `ExpressionSemanticAnalyzer` has context root / function arity only. No type inference |
| **Expression AST Nodes** | `VariableNode`, `ObjectDerefNode`, `ArrayDerefNode`, `IndexAccessNode`, `NotOpNode`, `CompareOpNode`, `LogicalOpNode`, `FuncCallNode` | Equivalent nodes exist. `ObjectDerefNode` (`.` access) and `ArrayDerefNode` (`.*` access) are covered by `MemberAccess` / `WildcardAccess` |
| **Generated Data** | `all_webhooks.go`, `availability.go`, `popular_actions.go` | `OnEventSpecs` has webhook events + activity types (hand-implemented). Availability / popular actions not implemented |

### 0.2 Perspectives to Supplement from ghalint

| Perspective | Details |
|---|---|
| Polymorphic YAML fields | Custom parsing patterns for `permissions` (scalar or mapping), `container` (scalar or mapping), `secrets` (`"inherit"` or mapping) — current C# only handles `secrets` |
| Minimal policy model | ghalint defines structs only for needed fields. This spec builds a full AST but maintains all Job/Step fields to support future rules |

### 0.3 Perspectives to Supplement from zizmor

| Perspective | Details |
|---|---|
| `${{ }}` fenced extraction | Already implemented in C# (`ExpressionExtractor`) |
| JSON Schema auxiliary validation | May be implemented later with vendored schema. Out of scope for this parser spec |
| Context risk table (`context-capabilities`) | Managed as generated data. Belongs to the rule layer, not the parser |

---

## 1. Design Principles

### 1.1 Zero-Allocation Policy

1. Accept UTF-8 input as `ReadOnlySpan<byte>`
2. Use `ReadOnlySpan<byte>` comparisons on hot paths for scalar comparison; avoid `string` comparison
3. Store `Utf8Slice` (offset + length) in AST, not `Span<T>`
4. Never create temporary strings except for diagnostics
5. Use generated static tables for metadata lookup
6. Do not hold YAML library-specific types outside the adapter layer

### 1.2 When String Materialization is Allowed

String conversion is permitted only in these cases:

1. Display strings embedded in diagnostic messages
2. Locations requiring hash key conversion where `Utf8Slice` handling is prohibitively expensive
3. Values frequently referenced by downstream semantic rules where interning is beneficial

As a rule, **do not materialize strings for AST storage**.

### 1.3 Things to Avoid

1. DOM construction of the entire YAML
2. Conversion to `Dictionary<string, object>`
3. Post-processing with `string.Split` or regex
4. LINQ during parsing
5. `new T[]`, `List<T>`, `Dictionary<TKey, TValue>` on hot paths
6. `GetScalarString()` on success paths

---

## 2. YAML Adapter Layer (Anti-Corruption Layer)

An **Anti-Corruption Layer** is placed between the parser core and the YAML library.
This layer ensures that replacing the YAML serializer/deserializer does not propagate changes to the parser core.

### 2.1 Architecture

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

### 2.2 IYamlStreamReader Interface

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

    // --- Position information ---
    TextPosition CurrentStart { get; }   // line, column, byte offset
    TextPosition CurrentEnd { get; }
}
```

### 2.3 Custom Enumerations

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

### 2.4 VYamlStreamAdapter (VYaml Implementation)

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

### 2.5 Rationale for the Adapter Layer

| Problem | Solved by adapter |
|---|---|
| VYaml event API changes propagate to the entire parser | Changes are contained within `VYamlStreamAdapter` |
| Tests are coupled to VYaml implementation details | `FakeYamlStreamReader` can inject minimal event sequences directly |
| Parser core responsibilities are mixed with YAML library absorption | Responsibilities are cleanly separated |
| Need to replace with another serializer like YamlDotNet | Just implement a new adapter; parser remains unchanged |
| Scalar tag retrieval (`!!str`, `!!int`, etc.) differs per library | Absorbed by normalizing to `ScalarTag` enum |

### 2.6 Replacement Procedure

1. Create a new adapter class implementing `IYamlStreamReader` (e.g., `YamlDotNetStreamAdapter`)
2. Replace the adapter factory in the entry point (`WorkflowParser.Parse()`)
3. Parse functions in the parser core require **no changes at all**
4. Existing tests pass as-is (because the `IYamlStreamReader` contract is the same)

### 2.7 Scalar Tag Information

Tag information equivalent to actionlint (Go)'s `yaml.Node.Tag` (`!!str`, `!!bool`, `!!int`, `!!float`, `!!null`) is returned by the adapter layer's `IYamlStreamReader.GetScalarTag()` as a `ScalarTag` enum.

- VYaml adapter: Converts from VYaml internal tag information
- YamlDotNet adapter: Converts from `NodeEvent.Tag`
- For libraries without tag information: Fallback estimation based on value content (`"true"` / `"false"` / numeric patterns)

The parser core references only the `ScalarTag` enum and has no knowledge of library-specific tag representations.

### 2.8 Relationship with Current VYamlStreamReader

The current `VYamlStreamReader` (`ref struct`) is the predecessor of `IYamlStreamReader`. Going forward:

1. Define the `IYamlStreamReader` interface
2. Rename `VYamlStreamReader` to `VYamlStreamAdapter` and implement `IYamlStreamReader`
3. Change all parse functions in `WorkflowParser` from `ref VYamlStreamReader` → `IYamlStreamReader`
4. Contain all references to VYaml-specific types (`ParseEventType`, `Marker`) within the adapter

**Note**: `ref struct` cannot implement interfaces, so the adapter must be a `class` or passed via generic type parameter. If there are performance concerns, adopt the `WorkflowParser<TReader> where TReader : IYamlStreamReader` pattern to enable JIT devirtualization.

---

## 3. AST C# Type Definitions

> For field semantics and constraints, see `Seiton_Parser_spec.md` §2.
> Only the C# type structure is defined here.

### 3.1 AST Design Principles

- Use `sealed class` with `{ get; init; }` properties
- TextRange is held as `TextRange Range` on every node
- Nullable types represent YAML omission
- Collections use `IReadOnlyList<T>` or `IReadOnlyDictionary<TKey, TValue>` for public API; internally built with arrays or dictionaries

### 3.2 Workflow

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
    public IReadOnlyDictionary<string, Job> Jobs { get; init; }
        = new Dictionary<string, Job>();
    public TextRange Range { get; init; }
}
```

### 3.3 Events

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
    public IReadOnlyDictionary<string, DispatchInput>? Inputs { get; init; }
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
    public IReadOnlyDictionary<string, WorkflowCallEventSecret>? Secrets { get; init; }
    public IReadOnlyDictionary<string, WorkflowCallEventOutput>? Outputs { get; init; }
}

public sealed class WorkflowCallEventInput
{
    public StringNode Name { get; init; }
    public string Id { get; init; }   // lower-case
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

### 3.4 Job

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
    public IReadOnlyDictionary<string, StringNode>? Outputs { get; init; }
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

### 3.5 Step / StepExec

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
    public IReadOnlyDictionary<string, StringNode>? Inputs { get; init; }
    public StringNode? Entrypoint { get; init; }   // docker only
    public StringNode? Args { get; init; }          // docker only
}
```

### 3.6 Common Nodes

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

### 3.7 Structural Nodes

```csharp
public sealed class Permissions
{
    public StringNode? All { get; init; }               // "read-all" / "write-all"
    public IReadOnlyDictionary<string, PermissionScope>? Scopes { get; init; }
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
    public IReadOnlyDictionary<string, EnvVar>? Vars { get; init; }
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
    public IReadOnlyDictionary<string, MatrixRow>? Rows { get; init; }
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
    public IReadOnlyList<IReadOnlyDictionary<string, RawYamlValue>>? Entries { get; init; }
}

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
    public IReadOnlyDictionary<string, RawYamlValue> Properties { get; init; }
}

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
    public IReadOnlyDictionary<string, Service>? ServiceMap { get; init; }
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

public sealed class WorkflowCall
{
    public StringNode Uses { get; init; }
    public Dictionary<string, WorkflowCallInput>? Inputs { get; init; }
    public Dictionary<string, WorkflowCallSecret>? Secrets { get; init; }
    public bool InheritSecrets { get; init; }
}
```

---

## 4. Entry Point and Output Model

### 4.1 Entry Point

```csharp
public static ParseResult Parse(byte[] utf8Yaml, string filePath)
```

- Return: `ParseResult { Workflow?, Diagnostic[], HasFatalError }`
- Returns `Diagnostic[]` even if YAML parsing itself fails; `Workflow` is null
- Errors during AST construction are accumulated, not immediately fatal

### 4.2 Diagnostic Type

```csharp
public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null);
```

### 4.3 TextRange

```csharp
public readonly record struct TextRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
```

---

## 5. Visitor / Pass C# Interfaces

### 5.1 Pass Interface

```csharp
public interface IPass
{
    void VisitWorkflowPre(Workflow workflow);
    void VisitWorkflowPost(Workflow workflow);
    void VisitJobPre(Job job);
    void VisitJobPost(Job job);
    void VisitStep(Step step);
}
```

### 5.2 Visitor

```csharp
public sealed class WorkflowVisitor
{
    private readonly List<IPass> _passes = new();

    public void AddPass(IPass pass) => _passes.Add(pass);

    public void Visit(Workflow workflow)
    {
        foreach (var pass in _passes)
            pass.VisitWorkflowPre(workflow);

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

### 5.3 Rule Interface

```csharp
public interface IRule : IPass
{
    string Id { get; }
    string Name { get; }
    Diagnostic[] GetDiagnostics();
    void SetConfig(LintConfig config);
}
```

Each Rule inspects the AST within `IPass` methods and accumulates diagnostics in an internal `List<Diagnostic>`.

---

## 6. Expression Parser C# Implementation

### 6.1 Expression Visitor

```csharp
public delegate void ExprNodeVisitor(ExpressionNode node, int parentId, bool entering);

public static void VisitExprNode(
    int nodeId,
    ExpressionNode[] nodes,
    int[] arguments,
    ExprNodeVisitor visitor);
```

### 6.2 Expression Type System

As a future implementation, a type system equivalent to actionlint's `ExprType` hierarchy will be introduced:
- `AnyType` / `NullType` / `BoolType` / `NumberType` / `StringType`
- `ObjectType` (properties map) / `ArrayType` (element type)
- `EmptyObjectType` / `EmptyArrayType`

Type inference is performed bottom-up in `ExprSemanticsChecker` while traversing expressions.

---

## 7. Generated Data Files

| Data | Source | File Name |
|---|---|---|
| Webhook event + activity types | GitHub Docs | `WebhookTypes.g.cs` |
| Context availability table | GitHub Docs | `Availability.g.cs` |
| Special function names | GitHub Docs | Within `Availability.g.cs` |
| Popular actions metadata | Fetched from action.yml | `PopularActions.g.cs` |

### 7.1 Update Policy

- Fetch external data via update command (`Seiton.Update` or script)
- Commit generated results as `.g.cs`
- CI periodic runs detect diffs and create auto PRs
- Parser and rules do not make network requests at runtime

### 7.2 Relationship with Current `OnEventSpecs`

`OnEventSpecs` is a hand-implemented event name + activity types table. It can be replaced by `WebhookTypes.g.cs` in the future, but the hand-implementation is sufficient initially.

---

## Appendix A: actionlint parse.go → C# Function Mapping

| actionlint Function | C# Counterpart | Status |
|---|---|---|
| `Parse()` | `WorkflowParser.Parse()` | Partially implemented |
| `parser.parse()` | Workflow mapping traversal within Parse | Partially implemented |
| `parser.parseEvents()` | `ParseOn()` | Partially implemented (no typed nodes) |
| `parser.parseScheduleEvent()` | — | **Not implemented** |
| `parser.parseWorkflowDispatchEvent()` | — | **Not implemented** |
| `parser.parseWorkflowCallEvent()` | — | **Not implemented** |
| `parser.parseRepositoryDispatchEvent()` | — | **Not implemented** |
| `parser.parseWebhookEvent()` | `ParseOnEventOptions()` | Partially implemented |
| `parser.parsePermissions()` | — (skip) | **Not implemented** |
| `parser.parseEnv()` | `ParseStringMapping()` | Partially implemented |
| `parser.parseDefaults()` | — (skip) | **Not implemented** |
| `parser.parseConcurrency()` | — (skip) | **Not implemented** |
| `parser.parseJob()` | `ParseJobNode()` | Partially implemented (flags only) |
| `parser.parseStep()` | `ParseStep()` | Partially implemented (flags only) |
| `parser.parseRunsOn()` | — (shape check) | **Not implemented** |
| `parser.parseEnvironment()` | — | **Not implemented** |
| `parser.parseOutputs()` | — (skip) | **Not implemented** |
| `parser.parseStrategy()` | `ParseStrategy()` | Shape only |
| `parser.parseMatrix()` | `ParseMatrix()` | Shape only |
| `parser.parseContainer()` | `ParseContainerLike()` | Shape only |
| `parser.parseServices()` | `ParseServices()` | Shape only |
| `parser.parseCredentials()` | `ParseCredentials()` | Shape only |
| `parser.parseStepExecAction()` | — | **Not implemented** |
| `parser.parseStepExecRun()` | — | **Not implemented** |
| `parser.parseMapping()` | — (inline) | No corresponding generic function |
| `parser.parseString()` | `ReadScalarOrSkip()` | Partial match |
| `parser.parseBool()` | — | **Not implemented** |
| `parser.parseInt()` | — | **Not implemented** |
| `parser.parseFloat()` | — | **Not implemented** |
| `parser.mayParseExpression()` | — | **Not implemented** |
| `parser.resolveAliases()` | — | **Not implemented** |
| `Visitor.Visit()` | — | **Not implemented** |
| `Pass` interface | — | **Not implemented** |

## Appendix B: Expression Parser Mapping

| actionlint | C# `ExpressionParser` | Status |
|---|---|---|
| `ExprLexer` | Inline lexing within `Parser` | ✓ Implemented |
| `ExprParser.parseLogicalOr()` | `ParseOr()` | ✓ |
| `ExprParser.parseLogicalAnd()` | `ParseAnd()` | ✓ |
| `ExprParser.parseCompare()` | `ParseEquality()` + `ParseRelational()` | ✓ |
| `ExprParser.parsePrimaryExpr()` | `ParsePrimary()` | ✓ |
| `ExprParser.parseIdent()` | `ParseKeywordOrIdentifier()` | ✓ |
| `ExprParser.parsePostfixOp()` | Loop within `ParsePrimary()` | ✓ |
| `VariableNode` | `Identifier` | ✓ |
| `ObjectDerefNode` | `MemberAccess` | ✓ |
| `ArrayDerefNode` | `WildcardAccess` | ✓ |
| `IndexAccessNode` | `IndexAccess` | ✓ |
| `FuncCallNode` | `FunctionCall` | ✓ |
| `NotOpNode` | `Unary (Not)` | ✓ |
| `CompareOpNode` | `Binary (Equal/NotEqual/Less/...)` | ✓ |
| `LogicalOpNode` | `Binary (And/Or)` | ✓ |
| arithmetic ops | `Binary (Add/Sub/Mul/Div/Mod)` | C# extension (not in GHA spec) |
| `ExprSemanticsChecker` | `ExpressionSemanticAnalyzer` | Partially implemented |
| `BuiltinFuncSignatures` | `TryGetFunctionArity()` | Arity only (no types) |
