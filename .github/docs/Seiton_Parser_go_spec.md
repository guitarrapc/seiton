# Seiton Parser Go Implementation Specification

> Go implementation specification for the parser contract defined in `Seiton_Parser_spec.md`. This document captures Go runtime structures and behavior for parsing, AST construction, and expression analysis. See `Seiton_Parser_csharp_spec.md` for the C# target. Both language specs share the same outline; only language-specific content differs. Linter behavior is specified in `Seiton_Linter_spec.md` and `Seiton_Linter_csharp_spec.md`.

> **Cross-document synchronization rule**: `Seiton_Parser_spec.md` is the source of truth. When this Go spec is updated, also review and update `Seiton_Parser_spec.md` and `Seiton_Parser_csharp_spec.md` in the same PR/commit scope.

---

## 0. Go Preamble

### 0.1 Contract

#### 0.1.1 Current Contract vs Reference Parity Gap

This document uses the following terms consistently:

- **Current contract**: behavior Seiton currently implements and treats as supported parser behavior for Go.
- **Reference parity gap**: behavior present in reference implementation (`actionlint`) but not fully matched by Seiton yet.
- **Out of scope**: behavior intentionally excluded from this spec's completion criteria.

The source of truth for supported behavior is `Seiton_Parser_spec.md`. Reference comparisons are informational and must not silently expand Seiton's contract.

### 0.2 Overview

The Seiton Parser Go implementation provides:

1. **YAML parsing** via `go.yaml.in/yaml/v4` into a `yaml.Node` tree
2. **Alias resolution** pre-pass on the `yaml.Node` tree
3. **Hand-written recursive descent parser** converting `yaml.Node` into a typed AST
4. **Expression parser** (separate recursive descent parser for `${{ }}` expressions)
5. **Expression-language intrinsic validation** (function existence, arity, operator-local type checks)
6. **Expression semantic analyzer** with type inference and context validation (transitional: Go implementation still performs context-dependent checks in parser)
7. **Generated data** for webhooks, context availability, and popular actions
8. **Input document-kind classification** (workflow vs action metadata) using path-hint candidate + structure-confirm finalization

> **Boundary note**: Under the refined expression validation boundary (`Seiton_spec.md` §3), the parser owns expression-language intrinsic validation. GitHub Actions context-dependent validation (context availability, function availability by position, dynamic properties, site-aware types) is owned by the linter. The current Go implementation still performs both; context-dependent checks will migrate to the linter in future phases.

Linter-side runtime details are specified in `Seiton_Linter_go_spec.md`.

### 0.3 Structure

All code lives in a single Go package `seiton`. Key source files:

| File | Responsibility |
|---|---|
| `parse.go` | YAML → AST parser |
| `ast.go` | AST type definitions |
| `expr_parser.go` | Expression recursive descent parser |
| `expr_lexer.go` | Expression lexer |
| `expr_ast.go` | Expression AST nodes |
| `expr_sema.go` | Expression semantic checker with type inference |
| `expr_type.go` | Expression type system |
| `error.go` | Error/diagnostic types |
| `all_webhooks.go` | Generated webhook event data |
| `availability.go` | Generated context availability data |
| `popular_actions.go` | Generated popular action metadata |

### 0.4 YAML/Alias

#### 0.4.1 yaml.Node Model

```go
// Provided by the YAML library — not a Seiton type
type Node struct {
    Kind    Kind     // ScalarNode, MappingNode, SequenceNode, AliasNode, DocumentNode
    Tag     string   // "!!str", "!!bool", "!!int", "!!float", "!!null", "!!seq", "!!map"
    Value   string   // Scalar value as string
    Content []*Node  // Children: mapping has key/value pairs interleaved
    Anchor  string   // Anchor name (if defined)
    Line    int      // 1-based line number
    Column  int      // 1-based column number
}
```

#### 0.4.2 Alias Resolution (Spec §1.1 step 1b)

```go
func (p *parser) resolveAliases(root *yaml.Node)
```

Pre-walk of the entire `yaml.Node` tree:

1. Track all anchors (defined) and aliases (used) with their positions
2. For each `AliasNode`, replace it with the referenced anchor's `yaml.Node`
3. Detect and report recursive aliases as errors

Alias resolution happens **before** the parser runs, ensuring the parser never encounters `AliasNode`. This simplifies all downstream parsing logic.

### 0.5 Design

1. Keep parser behavior aligned with `Seiton_Parser_spec.md` current contract.
2. Keep parser and linter responsibilities separated; linter runtime details belong to `Seiton_Linter_go_spec.md`.
3. Keep YAML library specifics isolated from parser semantics and diagnostics contract.
4. Prefer deterministic diagnostics accumulation and recovery over fail-fast parsing.
5. Treat generated metadata as read-only runtime inputs, updated by explicit update pipeline.

---

## 1. Overall Parser Flow (Spec §1)

### 1.0.1 Input Document Kind Classification (Spec §1.1.2)

Go parser entrypoint classifies input kind before kind-specific parse traversal.

Normative path hints for action-metadata candidate:

- Basename `action.yml` or `action.yaml`
- `.github/actions/<name>/action.yml` or `.github/actions/<name>/action.yaml`

Normative structural hints for finalization:

- Root `jobs` => workflow
- Root `runs` => action-metadata
- Root has both `jobs` and `runs` => `unknown` + ambiguity diagnostic

Final kind is confirmed from top-level structure; structure has priority over path hint on conflict.

### 1.1 Entry Point (Spec §1.1)

```go
func Parse(b []byte) (*Workflow, []*Error)
```

Implementation:

1. `yaml.Unmarshal(b, &n)` — parse into `yaml.Node` tree
2. `parser.resolveAliases(&n)` — resolve all YAML aliases
3. `parser.parse(n.Content[0])` — recursive descent into Seiton AST
4. Return `(workflow, parser.errors)`

On YAML unmarshal failure, `handleYAMLUnmarshalError` converts `yaml.TypeError` and `yaml.ParserError` into `[]*Error`.

Current cross-runtime note for Spec §3.1 fatal-parse hints:

- The shared parser spec documents optional explanatory `Help` hints for some fatal YAML parse cases.
- The reviewed C# implementation attaches the `run:`/`script:` plain-scalar colon-space hint.
- The Go implementation documented here does not currently describe or guarantee that hint payload, so this remains a cross-runtime parity gap until Go adds equivalent behavior.

### 1.2 Parse Pipeline

```
Parse([]byte)
  1. yaml.Unmarshal → yaml.Node tree
  2. resolveAliases → alias-free yaml.Node tree
  3. parser.parse() → Workflow AST + []*Error
```

### 1.3 Linter Integration

Go linter integration behavior is specified in `Seiton_Linter_go_spec.md`.

This parser document only assumes the integration boundary from `Seiton_Parser_spec.md` §8:

- Parser emits parse output (AST + parser diagnostics)
- Linter consumes parser output as structural input

---

## 2. AST Definitions (Spec §2)

> For field semantics and constraints, see `Seiton_Parser_spec.md` §2.
> Only the Go type structure is defined here.

### 2.1 Primitive Types (Spec §2.6)

```go
// Source position (1-based line and column)
type Pos struct {
    Line int
    Col  int
}

// Positioned string value
type String struct {
    Value  string
    Quoted bool    // whether the scalar was single/double quoted
    Pos    *Pos
}

// Boolean: literal or expression
type Bool struct {
    Value      bool
    Expression *String  // non-nil when value is ${{ }}
    Pos        *Pos
}

// Integer: literal or expression
type Int struct {
    Value      int
    Expression *String
    Pos        *Pos
}

// Float: literal or expression
type Float struct {
    Value      float64
    Expression *String
    Pos        *Pos
}
```

Helper functions on primitives:

```go
func (p *Pos) String() string
func (p *Pos) IsBefore(other *Pos) bool

func ContainsExpression(s string) bool          // checks if string contains ${{ }}
func (s *String) ContainsExpression() bool
func isExprAssigned(s string) bool              // checks if entire string is ${{ ... }}
func (s *String) IsExpressionAssigned() bool
```

### 2.2 Workflow (Spec §2.2)

```go
type Workflow struct {
    Name        *String
    RunName     *String
    On          []Event
    Permissions *Permissions
    Env         *Env
    Defaults    *Defaults
    Concurrency *Concurrency
    Jobs        map[string]*Job
}

func (w *Workflow) FindWorkflowCallEvent() (*WorkflowCallEvent, bool)
```

### 2.3 Events (Spec §2.3)

```go
type Event interface {
    EventName() string
}
```

#### 2.3.1 WebhookEvent (Spec §2.3.1)

```go
type WebhookEvent struct {
    Hook           *String
    Types          []*String
    Branches       *WebhookEventFilter
    BranchesIgnore *WebhookEventFilter
    Tags           *WebhookEventFilter
    TagsIgnore     *WebhookEventFilter
    Paths          *WebhookEventFilter
    PathsIgnore    *WebhookEventFilter
    Workflows      []*String
    Pos            *Pos
}

type WebhookEventFilter struct {
    Name   *String
    Values []*String
}

func (f *WebhookEventFilter) IsEmpty() bool
```

#### 2.3.2 ScheduledEvent (Spec §2.3.2)

```go
type ScheduledEvent struct {
    Schedules []*ScheduleEntry
    Pos       *Pos
}

type ScheduleEntry struct {
    Cron     *String
    Timezone *String
}
```

#### 2.3.3 WorkflowDispatchEvent (Spec §2.3.3)

```go
type WorkflowDispatchEventInputType uint8
// Constants: WorkflowDispatchEventInputTypeNone, ..String, ..Number, ..Boolean, ..Choice, ..Environment

type DispatchInput struct {
    Name        *String
    Description *String
    Required    *Bool
    Default     *String
    Type        WorkflowDispatchEventInputType
    Options     []*String
}

type WorkflowDispatchEvent struct {
    Inputs map[string]*DispatchInput
    Pos    *Pos
}
```

#### 2.3.4 WorkflowCallEvent (Spec §2.3.4)

```go
type WorkflowCallEventInputType uint8
// Constants: WorkflowCallEventInputTypeInvalid, ..Boolean, ..Number, ..String

type WorkflowCallEventInput struct {
    Name        *String
    Description *String
    Default     *String
    Required    *Bool
    Type        WorkflowCallEventInputType
    ID          string    // lower-cased name for case-insensitive comparison
}

type WorkflowCallEventSecret struct {
    Name        *String
    Description *String
    Required    *Bool
}

type WorkflowCallEventOutput struct {
    Name        *String
    Description *String
    Value       *String
}

type WorkflowCallEvent struct {
    Inputs  []*WorkflowCallEventInput
    Secrets map[string]*WorkflowCallEventSecret
    Outputs map[string]*WorkflowCallEventOutput
    Pos     *Pos
}

func (i *WorkflowCallEventInput) IsRequired() bool
```

#### 2.3.5 Other Events (Spec §2.3.5)

```go
type RepositoryDispatchEvent struct {
    Types []*String
    Pos   *Pos
}

type ImageVersionEvent struct {
    Names    []*String
    Versions []*String
    Pos      *Pos
}
```

### 2.4 Job (Spec §2.4)

```go
type Job struct {
    ID              *String
    Name            *String
    Needs           []*String
    RunsOn          *Runner
    Permissions     *Permissions
    Environment     *Environment
    Concurrency     *Concurrency
    Outputs         map[string]*Output
    Env             *Env
    Defaults        *Defaults
    If              *String
    Steps           []*Step
    TimeoutMinutes  *Float
    Strategy        *Strategy
    ContinueOnError *Bool
    Container       *Container
    Services        *Services
    WorkflowCall    *WorkflowCall
    Snapshot        *Snapshot
    Pos             *Pos
}
```

### 2.5 Step and Exec (Spec §2.5)

```go
type ExecKind uint8
// Constants: ExecKindRun, ExecKindAction

type Exec interface {
    Kind() ExecKind
}

type Step struct {
    ID              *String
    If              *String
    Name            *String
    Exec            Exec
    Env             *Env
    ContinueOnError *Bool
    TimeoutMinutes  *Float
    Pos             *Pos
}

type ExecRun struct {
    Run              *String
    Shell            *String
    WorkingDirectory *String
    RunPos           *Pos    // position of "run:" key itself
}

type Input struct {
    Name  *String
    Value *String
}

type ExecAction struct {
    Uses       *String
    Inputs     map[string]*Input
    Entrypoint *String    // docker only
    Args       *String    // docker only
}
```

### 2.6 Structural Nodes (Spec §2.7–§2.11)

```go
// Permissions (Spec §2.7): scalar ("read-all"/"write-all") or mapping
type PermissionScope struct {
    Name  *String
    Value *String
}

type Permissions struct {
    All    *String
    Scopes map[string]*PermissionScope
    Pos    *Pos
}

// Env (Spec §2.8): expression or mapping
type EnvVar struct {
    Name  *String
    Value *String
}

type Env struct {
    Vars       map[string]*EnvVar
    Expression *String
}

// Defaults (Spec §2.9)
type DefaultsRun struct {
    Shell            *String
    WorkingDirectory *String
    Pos              *Pos
}

type Defaults struct {
    Run *DefaultsRun
    Pos *Pos
}

// Concurrency (Spec §2.10)
type Concurrency struct {
    Group            *String
    CancelInProgress *Bool
    Queue            *String
    Pos              *Pos
}

Implementation note: `Queue` accepts literal values `single` and `max`. When the scalar contains expression markers, the parser preserves the string node and still performs normal expression validation; it skips only the literal `single`/`max` domain check that applies to non-expression scalar values.

// Environment (Spec §2.11)
type Environment struct {
    Name       *String
    URL        *String
    Deployment *Bool
    Pos        *Pos
}

// Output
type Output struct {
    Name  *String
    Value *String
}

// Runner (Spec §2.12)
type Runner struct {
    Labels     []*String
    LabelsExpr *String
    Group      *String
}

// WorkflowCall (Spec §2.15)
type WorkflowCallInput struct {
    Name  *String
    Value *String
}

type WorkflowCallSecret struct {
    Name  *String
    Value *String
}

type WorkflowCall struct {
    Uses           *String
    Inputs         map[string]*WorkflowCallInput
    Secrets        map[string]*WorkflowCallSecret
    InheritSecrets bool
}

// Snapshot
type Snapshot struct {
    ImageName *String
    Version   *String
    If        *String
}
```

### 2.7 Strategy / Matrix (Spec §2.13)

```go
type Strategy struct {
    Matrix      *Matrix
    FailFast    *Bool
    MaxParallel *Int
    Pos         *Pos
}

type Matrix struct {
    Rows       map[string]*MatrixRow
    Include    *MatrixCombinations
    Exclude    *MatrixCombinations
    Expression *String
    Pos        *Pos
}

type MatrixRow struct {
    Name       *String
    Values     []RawYAMLValue
    Expression *String
}

type MatrixAssign struct {
    Key   *String
    Value RawYAMLValue
}

type MatrixCombination struct {
    Assigns    map[string]*MatrixAssign
    Expression *String
}

type MatrixCombinations struct {
    Combinations []*MatrixCombination
    Expression   *String
}

func (cs *MatrixCombinations) ContainsExpression() bool
```

### 2.8 Container / Services / Credentials (Spec §2.14)

```go
type Credentials struct {
    Username   *String
    Password   *String
    Expression *String
    Pos        *Pos
}

type Container struct {
    Image       *String
    Credentials *Credentials
    Env         *Env
    Ports       []*String
    Volumes     []*String
    Options     *String
    Pos         *Pos
}

type Service struct {
    Name      *String
    Container *Container
}

type Services struct {
    Value      map[string]*Service
    Expression *String
    Pos        *Pos
}
```

### 2.9 RawYAMLValue

```go
type RawYAMLValueKind int
// Constants: RawYAMLValueKindObject, RawYAMLValueKindArray, RawYAMLValueKindString

type RawYAMLValue interface {
    Kind() RawYAMLValueKind
    Equals(other RawYAMLValue) bool
    Pos() *Pos
    String() string
}

type RawYAMLObject struct {
    Props map[string]RawYAMLValue
    pos   *Pos
}

type RawYAMLArray struct {
    Elems []RawYAMLValue
    pos   *Pos
}

type RawYAMLString struct {
    Value string
    pos   *Pos
}
```

---

## 3. Parse Algorithms (Spec §3)

### 3.1 Parser State

```go
type parser struct {
    errors []*Error
}
```

The parser accumulates errors in a slice and never aborts on the first error (multi-error recovery, Spec §5.1).

Internal helper types:

```go
// Mapping entry yielded by ParseMapping iterator
type workflowMappingEntry struct {
    id  string     // key string (potentially lower-cased)
    key *String    // positioned key node
    val *yaml.Node // value node
}

// Deferred format string for error messages
type delayedSprintf struct {
    result string
    arg    string
}
```

### 3.2 Mapping Traversal (Spec §3.3)

The mapping traversal is the core parsing pattern. It uses Go iterators (`iter.Seq`):

```go
// ParseMapping (Spec §3.3): Generic mapping traversal with duplicate detection and optional case-insensitivity.
func (p *parser) parseMapping(where delayedSprintf, n *yaml.Node, allowEmpty, caseSensitive bool) iter.Seq[workflowMappingEntry]

// Convenience: section name as string, used for most parse functions.
func (p *parser) parseSectionMapping(section string, n *yaml.Node, allowEmpty, caseSensitive bool) iter.Seq[workflowMappingEntry]

// Convenience: fixed string for "where" parameter.
func (p *parser) parseMappingAt(where string, n *yaml.Node, allowEmpty, caseSensitive bool) iter.Seq[workflowMappingEntry]
```

**Mapping traversal behavior:**

1. Check for null scalar → if `allowEmpty`, yield nothing; otherwise error
2. Verify `MappingNode` kind
3. Iterate over `Content` in key/value pairs
4. Normalize key to lower-case if `caseSensitive = false`
5. Detect duplicate keys; report error with note on case-insensitive mode
6. Detect `<<` (YAML merge key); report error
7. Yield `workflowMappingEntry` to caller
8. If not `allowEmpty` and 0 entries, report error

### 3.3 Workflow Parse (Spec §3.2)

```go
func (p *parser) parse(n *yaml.Node) *Workflow
```

Top-level mapping traversal:
- `"name"` → `parseString`
- `"run-name"` → `parseString`
- `"on"` → `ParseEvents`
- `"permissions"` → `ParsePermissions`
- `"env"` → `ParseEnv`
- `"defaults"` → `ParseDefaults`
- `"concurrency"` → `ParseConcurrency`
- `"jobs"` → `ParseJobs`
- Other → `unexpectedKey`

Post-validation: `on` and `jobs` are required.

### 3.4 Events Parse (Spec §3.4)

```go
func (p *parser) parseEvents(n *yaml.Node) []Event
func (p *parser) parseEventWithNoConfig(n *yaml.Node) Event
```

Three forms: scalar → single event, sequence → multiple events, mapping → events with config.

Implementation note (2026-04-15 sync):
- The scalar / sequence shortcut is only valid for no-config events. `schedule` keeps its mapping-only contract, so scalar `on: schedule` is a parser error in the shared spec.

For mapping form, dispatches by event name:
- `"schedule"` → `ParseScheduleEvent`
- `"workflow_dispatch"` → `ParseWorkflowDispatchEvent`
- `"repository_dispatch"` → `ParseRepositoryDispatchEvent`
- `"workflow_call"` → `ParseWorkflowCallEvent`
- `"image_version"` → `ParseImageVersionEvent`
- other → `ParseWebhookEvent`

#### 3.4.1 Specific Event Parse Functions (Spec §3.4)

```go
// ParseScheduleEvent (Spec §2.3.2)
func (p *parser) parseScheduleEvent(pos *Pos, n *yaml.Node) *ScheduledEvent

// ParseWorkflowDispatchEvent (Spec §2.3.3)
func (p *parser) parseWorkflowDispatchEvent(pos *Pos, n *yaml.Node) *WorkflowDispatchEvent
func (p *parser) parseWorkflowDispatchEventInput(name *String, n *yaml.Node) *DispatchInput

// ParseRepositoryDispatchEvent (Spec §2.3.5)
func (p *parser) parseRepositoryDispatchEvent(pos *Pos, n *yaml.Node) *RepositoryDispatchEvent

// ParseWebhookEvent (Spec §3.4.2)
func (p *parser) parseWebhookEvent(name *String, n *yaml.Node) *WebhookEvent
func (p *parser) parseWebhookEventFilter(name *String, n *yaml.Node) *WebhookEventFilter

// ParseWorkflowCallEvent (Spec §2.3.4)
func (p *parser) parseWorkflowCallEvent(pos *Pos, n *yaml.Node) *WorkflowCallEvent
func (p *parser) parseWorkflowCallEventInput(id string, name *String, n *yaml.Node) *WorkflowCallEventInput
func (p *parser) parseWorkflowCallEventSecret(name *String, n *yaml.Node) *WorkflowCallEventSecret
func (p *parser) parseWorkflowCallEventOutput(name *String, n *yaml.Node) *WorkflowCallEventOutput

// ParseImageVersionEvent
func (p *parser) parseImageVersionEvent(pos *Pos, n *yaml.Node) *ImageVersionEvent
```

### 3.5 Structural Section Parse (Spec §3.5–§3.8)

```go
// ParsePermissions (Spec §3.5)
func (p *parser) parsePermissions(pos *Pos, n *yaml.Node) *Permissions

// ParseEnv (Spec §3.6)
func (p *parser) parseEnv(n *yaml.Node) *Env

// ParseDefaults (Spec §3.7)
func (p *parser) parseDefaults(pos *Pos, n *yaml.Node) *Defaults

// ParseConcurrency (Spec §3.8)
func (p *parser) parseConcurrency(pos *Pos, n *yaml.Node) *Concurrency

// ParseEnvironment (Spec §3.14)
func (p *parser) parseEnvironment(pos *Pos, n *yaml.Node) *Environment

// ParseOutputs (Spec §3.10)
func (p *parser) parseOutputs(n *yaml.Node) map[string]*Output
```

Implementation note (2026-04-15 sync):
- `defaults` requires `run` and `concurrency` requires `group`; both are parser-level structural diagnostics in the shared spec rather than later semantic checks.
- `concurrency.queue` accepts literal `single` / `max` and reports a parser diagnostic for any other plain literal value. Expression-bearing strings are preserved and still go through the normal expression semantic validation path during parsing; only the plain-literal `single` / `max` check is bypassed.

### 3.6 Job Parse (Spec §3.9–§3.10)

```go
// ParseJobs (Spec §3.9)
func (p *parser) parseJobs(n *yaml.Node) map[string]*Job

// ParseJob (Spec §3.10)
func (p *parser) parseJob(id *String, n *yaml.Node) *Job

// ParseRunsOn (Spec §3.13)
func (p *parser) parseRunsOn(n *yaml.Node) *Runner

// parseSnapshot
func (p *parser) parseSnapshot(pos *Pos, n *yaml.Node) *Snapshot

// parseTimeoutMinutes: validates > 0
func (p *parser) parseTimeoutMinutes(n *yaml.Node) *Float
```

Job parsing includes reusable workflow detection and constraint validation (Spec §3.10.1):
- If `uses` is present → reusable workflow call; certain keys are forbidden
- If `uses` is absent → normal job; `steps` and `runs-on` are required

### 3.7 Step Parse (Spec §3.11–§3.12)

```go
// ParseSteps (Spec §3.11)
func (p *parser) parseSteps(n *yaml.Node) []*Step

// ParseStep (Spec §3.12)
func (p *parser) parseStep(n *yaml.Node) *Step

// parseStepExecAction (Spec §3.12.1)
func (p *parser) parseStepExecAction(entries []workflowMappingEntry, isDocker bool) *ExecAction

// parseStepExecRun (Spec §3.12.2)
func (p *parser) parseStepExecRun(entries []workflowMappingEntry) *ExecRun
```

Step parsing uses a **2-pass design** (Spec §3.12):
1. **Pass 1**: Collect all entries, find `run` or `uses` key
2. **Pass 2**: Dispatch to `parseStepExecRun` or `parseStepExecAction`

### 3.8 Strategy / Matrix Parse (Spec §3.15)

```go
// ParseStrategy (Spec §3.15)
func (p *parser) parseStrategy(pos *Pos, n *yaml.Node) *Strategy

// ParseMatrix (Spec §3.15)
func (p *parser) parseMatrix(pos *Pos, n *yaml.Node) *Matrix

// parseMaxParallel: validates > 0
func (p *parser) parseMaxParallel(n *yaml.Node) *Int

// parseMatrixCombinations (Spec §3.15)
func (p *parser) parseMatrixCombinations(sec string, n *yaml.Node) *MatrixCombinations

// parseRawYAMLValue (Spec §3.15)
func (p *parser) parseRawYAMLValue(n *yaml.Node) RawYAMLValue
```

### 3.9 Container / Services Parse (Spec §3.16–§3.18)

```go
// ParseContainer (Spec §3.16)
func (p *parser) parseContainer(sec string, pos *Pos, n *yaml.Node) *Container

// ParseCredentials (Spec §3.18)
func (p *parser) parseCredentials(pos *Pos, n *yaml.Node) *Credentials

// ParseServices (Spec §3.17)
func (p *parser) parseServices(n *yaml.Node) *Services
```

Implementation note (2026-04-14 sync):
- Job structural constraints (`uses` vs `steps`/`runs-on`, and `with`/`secrets` requires `uses`) are parser-level diagnostics.
- Rule/visitor diagnostics are additive; parser diagnostics remain the base contract.

Implementation note (2026-04-15 sync):
- `services` accepts either a mapping of named services or a single expression scalar.
- `credentials` accepts either an expression scalar or a mapping with required `username` + `password`.
- Container-level and service-level `env` reuse the shared expression-or-mapping `Env` shape.

---

## 4. Scalar Parsing Helpers (Spec §4)

The parser uses `yaml.Node.Tag` to distinguish scalar types:

| Tag | Meaning | Used by |
|---|---|---|
| `!!str` | String scalar | `parseString`, `parseExpression` |
| `!!bool` | Boolean scalar | `parseBool` |
| `!!int` | Integer scalar | `parseInt` |
| `!!float` | Float scalar | `parseFloat` |
| `!!null` | Null scalar | empty value detection |
| `!!seq` | Sequence | `checkSequence` |
| `!!map` | Mapping | `ParseMapping` |

### 4.1 parseString (Spec §4.1)

```go
// Parse a string scalar. Returns nil on error.
func (p *parser) parseString(n *yaml.Node, allowEmpty bool) *String
```

### 4.2 parseBool (Spec §4.2)

```go
// Parse a boolean. If tag is !!str, treats as expression.
func (p *parser) parseBool(n *yaml.Node) *Bool
```

### 4.3 parseInt (Spec §4.3)

```go
// Parse an integer. If tag is !!str, treats as expression.
func (p *parser) parseInt(n *yaml.Node) *Int
```

### 4.4 parseFloat (Spec §4.4)

```go
// Parse a float. If tag is !!str or !!int, handles appropriately.
func (p *parser) parseFloat(n *yaml.Node) *Float
```

### 4.5 parseExpression (Spec §4.5)

```go
// Verify value is a ${{ }} expression. Error if not.
func (p *parser) parseExpression(n *yaml.Node, expecting string) *String
```

### 4.6 mayParseExpression (Spec §4.6)

```go
// If value is a ${{ }} expression, return it. Otherwise nil (no error).
func (p *parser) mayParseExpression(n *yaml.Node) *String
```

### 4.7 Collection Helpers (Spec §4.7)

```go
// Parse a string sequence.
func (p *parser) parseStringSequence(sec string, n *yaml.Node, allowEmpty bool, allowElemEmpty bool) []*String

// Parse a scalar or sequence of strings (polymorphic).
func (p *parser) parseStringOrStringSequence(sec string, n *yaml.Node, allowEmpty bool, allowElemEmpty bool) []*String
```

Validation helpers:

```go
func (p *parser) checkNotEmpty(sec string, len int, n *yaml.Node) bool
func (p *parser) checkSequence(sec string, n *yaml.Node, allowEmpty bool) bool
func (p *parser) checkString(n *yaml.Node, allowEmpty bool) bool
```

---

## 5. Error Recovery (Spec §5)

### 5.1 Error Reporting

```go
func (p *parser) error(n *yaml.Node, m string)
func (p *parser) errorAt(pos *Pos, m string)
func (p *parser) errorf(n *yaml.Node, format string, args ...interface{})
func (p *parser) errorfAt(pos *Pos, format string, args ...interface{})
func (p *parser) unexpectedKey(s *String, sec string, expected []string)
func (p *parser) missingExpression(n *yaml.Node, expecting string)
```

### 5.2 Recovery Patterns

The parser never aborts on a single error. Each parse function:
1. Validates the current node type
2. On mismatch, reports an error and returns a partial/nil result
3. The caller continues processing remaining siblings

This allows collecting the maximum number of diagnostics in a single pass.

| Situation | Recovery |
|---|---|
| Unknown key | error + skip value node |
| Type mismatch | error + skip value node |
| Missing required key | aggregate error after mapping traversal |
| Exclusive constraint violation | aggregate error after mapping traversal |
| YAML unmarshal failure | Convert to `[]*Error`, `Workflow = nil` |
| Duplicate key | error + ignore the later key (first wins) |

---

## 6. Expression Parser (Spec §6)

### 6.1 Lexer (Spec §6.3)

```go
type TokenKind int
// Constants: TokenKindIdent, TokenKindString, TokenKindInt, TokenKindFloat,
//   TokenKindLeftParen, TokenKindRightParen, TokenKindLeftBracket, TokenKindRightBracket,
//   TokenKindDot, TokenKindNot, TokenKindLess, TokenKindLessEq, TokenKindGreater, TokenKindGreaterEq,
//   TokenKindEq, TokenKindNotEq, TokenKindAnd, TokenKindOr, TokenKindStar, TokenKindComma, TokenKindEnd

type Token struct {
    Kind   TokenKind
    Value  string
    Offset int
    Line   int
    Column int
}

type ExprLexer struct {
    src    string
    scan   scanner.Scanner
    lexErr *ExprError
    start  scanner.Position
}

func NewExprLexer(src string) *ExprLexer
func (lex *ExprLexer) Next() *Token
func (lex *ExprLexer) Offset() int
func (lex *ExprLexer) Err() *ExprError
func LexExpression(src string) ([]*Token, int, *ExprError)
```

**Lexer details:**
- Single-quoted strings with `''` escape
- Integer literals: decimal and `0x` hexadecimal
- Float literals: standard floating-point
- Identifiers: alphanumeric + `_` + `-`
- Two-character operators: `<=`, `>=`, `==`, `!=`, `&&`, `||`

### 6.2 Parser (Spec §6.2)

```go
type ExprParser struct {
    cur   *Token
    lexer *ExprLexer
    err   *ExprError
}

func NewExprParser() *ExprParser
func (p *ExprParser) Parse(l *ExprLexer) (ExprNode, *ExprError)
func (p *ExprParser) Err() *ExprError
```

**Recursive descent precedence layers (lowest to highest):**

| Precedence | Function | Operators |
|---|---|---|
| 1 (lowest) | `parseLogicalOr` | `\|\|` |
| 2 | `parseLogicalAnd` | `&&` |
| 3 | `parseCompareBinOp` | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| 4 | `parsePrefixOp` | `!` (unary) |
| 5 | `parsePostfixOp` | `.prop`, `.*`, `[idx]`, `(args)` |
| 6 (highest) | `parsePrimaryExpr` | literals, identifiers, `(expr)` |

**Note**: No arithmetic operators. The GitHub Actions expression spec does not include `+`, `-`, `*`, `/`, `%`.

**Internal parse functions:**

```go
func (p *ExprParser) parseLogicalOr() ExprNode
func (p *ExprParser) parseLogicalAnd() ExprNode
func (p *ExprParser) parseCompareBinOp() ExprNode
func (p *ExprParser) parsePrefixOp() ExprNode
func (p *ExprParser) parsePostfixOp() ExprNode
func (p *ExprParser) parsePrimaryExpr() ExprNode
func (p *ExprParser) parseIdent() ExprNode
func (p *ExprParser) parseNestedExpr() ExprNode
func (p *ExprParser) parseInt() ExprNode
func (p *ExprParser) parseFloat() ExprNode
func (p *ExprParser) parseString() ExprNode
```

### 6.3 Expression AST (Spec §6.4)

```go
type ExprNode interface {
    Token() *Token
}

type VariableNode struct {
    Name string
    tok  *Token
}

type NullNode struct { tok *Token }
type BoolNode struct { Value bool; tok *Token }
type IntNode struct { Value int; tok *Token }
type FloatNode struct { Value float64; tok *Token }
type StringNode struct { Value string; tok *Token }

type ObjectDerefNode struct {
    Receiver ExprNode
    Property string
}

type ArrayDerefNode struct {
    Receiver ExprNode
}

type IndexAccessNode struct {
    Operand ExprNode
    Index   ExprNode
}

type NotOpNode struct {
    Operand ExprNode
    tok     *Token
}

type CompareOpNodeKind int
// Constants: CompareOpNodeKindLess, ..LessEq, ..Greater, ..GreaterEq, ..Eq, ..NotEq

type CompareOpNode struct {
    Kind  CompareOpNodeKind
    Left  ExprNode
    Right ExprNode
}

type LogicalOpNodeKind int
// Constants: LogicalOpNodeKindAnd, LogicalOpNodeKindOr

type LogicalOpNode struct {
    Kind  LogicalOpNodeKind
    Left  ExprNode
    Right ExprNode
}

type FuncCallNode struct {
    Callee string
    Args   []ExprNode
    tok    *Token
}
```

### 6.4 Expression Visitor (Spec §6.5)

```go
type VisitExprNodeFunc func(node, parent ExprNode, entering bool)

func VisitExprNode(n ExprNode, f VisitExprNodeFunc)
func visitExprNode(n, p ExprNode, f VisitExprNodeFunc)
```

Traverses the expression tree with `entering = true` before visiting children, `entering = false` after.

---

## 7. Expression Semantic Analysis (Spec §7)

### 7.1 Function Signatures (Spec §7.1)

```go
type FuncSignature struct {
    Name                 string
    Ret                  ExprType
    Params               []ExprType
    VariableLengthParams bool
    IsConstFunc          bool
}
```

Built-in functions support overloading via `map[string][]*FuncSignature`.

### 7.2 Context Availability (Spec §7.2)

The checker is configured with context type information before checking each expression:

```go
func (sema *ExprSemanticsChecker) UpdateMatrix(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateSteps(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateNeeds(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateSecrets(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateInputs(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateDispatchInputs(ty *ObjectType)
func (sema *ExprSemanticsChecker) UpdateJobs(ty *ObjectType)
func (sema *ExprSemanticsChecker) SetContextAvailability(avail []string)
func (sema *ExprSemanticsChecker) SetSpecialFunctionAvailability(avail []string)
```

### 7.3 Type System (Spec §7.3)

```go
type ExprSemanticsChecker struct {
    funcs                map[string][]*FuncSignature
    vars                 map[string]ExprType
    errs                 []*ExprError
    varsCopied           bool
    githubVarCopied      bool
    untrusted            *UntrustedInputChecker
    availableContexts    []string
    availableSpecialFuncs []string
    configVars           []string
}

func NewExprSemanticsChecker(checkUntrustedInput bool, configVars []string) *ExprSemanticsChecker
func (sema *ExprSemanticsChecker) Check(expr ExprNode) (ExprType, []*ExprError)
```

**Type checking functions:**

```go
func (sema *ExprSemanticsChecker) check(expr ExprNode) ExprType
func (sema *ExprSemanticsChecker) checkVariable(n *VariableNode) ExprType
func (sema *ExprSemanticsChecker) checkObjectDeref(n *ObjectDerefNode) ExprType
func (sema *ExprSemanticsChecker) checkArrayDeref(n *ArrayDerefNode) ExprType
func (sema *ExprSemanticsChecker) checkIndexAccess(n *IndexAccessNode) ExprType
func (sema *ExprSemanticsChecker) checkFuncCall(n *FuncCallNode) ExprType
func (sema *ExprSemanticsChecker) checkBuiltinFuncCall(n *FuncCallNode, sig *FuncSignature) ExprType
func (sema *ExprSemanticsChecker) checkNotOp(n *NotOpNode) ExprType
func (sema *ExprSemanticsChecker) checkCompareOp(n *CompareOpNode) ExprType
func (sema *ExprSemanticsChecker) checkLogicalOp(n *LogicalOpNode) ExprType
func (sema *ExprSemanticsChecker) checkWithNarrowing(n ExprNode, isTruthy bool) ExprType
func (sema *ExprSemanticsChecker) checkAvailableContext(n *VariableNode)
func (sema *ExprSemanticsChecker) checkSpecialFunctionAvailability(n *FuncCallNode)
func (sema *ExprSemanticsChecker) checkConfigVariables(n *ObjectDerefNode)
func (sema *ExprSemanticsChecker) IsConstant(expr ExprNode) bool
```

**Key semantic patterns:**

1. **Overloaded function resolution**: Functions like `contains` have multiple signatures (string,string) or (array,any). All are checked; first matching signature is used.
2. **Case-insensitive function lookup**: Function names are compared case-insensitively.
3. **Context availability** (Spec §7.2): `SetContextAvailability` is called before each expression check to configure which root identifiers are valid at that location.
4. **Type narrowing** (Spec §7.3): Logical expressions (`&&`, `||`) use type narrowing to improve inference accuracy on branches.
5. **Untrusted input tracking**: A separate checker (`UntrustedInputChecker`) is optionally invoked at enter/leave of each expression node to track taint flow.

---

## 8. Linter Integration Reference

Linter-side implementation details are intentionally out of scope in this parser document.

- Go linter runtime contract and implementation mapping: `Seiton_Linter_go_spec.md`
- Language-agnostic linter contract: `Seiton_Linter_spec.md`

This section remains as a boundary marker so the §0–§11 outline stays consistent across language companion documents.

---

## 9. Generated Data (Spec §9)

The generated-data pipeline specification has been moved to `Seiton_Update_spec.md`.

This section remains as a boundary marker so the §0–§11 outline stays consistent across language companion documents.

### 9.1 Go Generated Files

| Data | File | Description |
|---|---|---|
| Webhook event + activity types | `all_webhooks.go` | Static table mapping event names to allowed activity types and filter options |
| Context availability | `availability.go` | Which expression contexts and special functions are available at each workflow position |
| Popular actions metadata | `popular_actions.go` | Well-known GitHub Actions with expected input names, output names, and `runs.using` runtime |

For pipeline architecture, CLI commands, data paths, update policy, and conflict resolution, see `Seiton_Update_spec.md`.

---

## 10. Diagnostic Model (Spec §10)

### 10.1 Diagnostic Structure (Spec §10.1)

```go
type Error struct {
    Message  string
    Filepath string
    Line     int
    Column   int
    Kind     string  // rule identifier
}
```

Expression error:

```go
type ExprError struct {
    Message string
    Offset  int
    Line    int
    Column  int
}
```

### 10.2 Error Handling Philosophy (Spec §10.2)

- **Parser errors**: Accumulated in `parser.errors`, never cause early abort
- **Rule errors**: Each rule accumulates its own diagnostics; collected after visitor traversal
- **Expression errors**: Lexer stores first error; parser stores first error; semantic checker accumulates all errors
- **Final output**: All errors are merged, filtered by ignore patterns, sorted by position, and deduplicated

---

## 11. Design Decisions

### 11.1 yaml.Node Tree vs Event Stream

The Go implementation uses the `yaml.Node` tree model (full DOM), not a streaming parser. This allows:
- Random access to any node's children
- Pre-pass alias resolution over the full tree
- Position information on every node

Trade-off: Higher memory usage than event-stream parsing. (The C# implementation uses event-stream via the YAML adapter layer for zero-allocation goals.)

### 11.2 Iterator-Based Mapping

The Go implementation uses Go 1.23 iterators (`iter.Seq`) for `parseMapping`, yielding entries to the caller via a `for range` loop. This avoids materializing an intermediate slice and provides a clean API for the caller to switch on keys.

### 11.3 Two-Pass Step Parsing (Spec §3.12)

Steps are parsed in two passes because the step kind (run vs uses) determines which keys are valid, but the `run`/`uses` key may appear anywhere in the mapping:

1. **Pass 1**: Collect all entries, find `run` or `uses` key
2. **Pass 2**: Dispatch to `parseStepExecRun` or `parseStepExecAction`

### 11.4 Polymorphic YAML Fields (Spec §14)

Many fields accept multiple YAML forms. The pattern is:

```go
func (p *parser) parseFoo(n *yaml.Node) *Foo {
    if s := p.mayParseExpression(n); s != nil {
        return &Foo{Expression: s}
    }
    switch n.Kind {
    case yaml.ScalarNode:
        // simple form
    case yaml.MappingNode:
        // detailed form
    default:
        p.errorf(n, "...")
    }
}
```

### 11.5 Case Sensitivity (Spec §13)

- Mapping keys within a section: case-sensitive (e.g., `name` ≠ `Name`)
- Identifiers used as dictionary keys: case-insensitive (e.g., job IDs, env var names, matrix row names)
- The `parseMapping` function supports both modes via the `caseSensitive` parameter
