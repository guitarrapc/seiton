# Seiton Parser Go Implementation Specification (actionlint Reference)

> Documents the Go implementation architecture of [actionlint](https://github.com/rhysd/actionlint) in sufficient detail to serve as a reference for reimplementation.
> This is a companion to `Seiton_Parser_spec.md` (language-agnostic) and `Seiton_Parser_csharp_spec.md` (C# target).

---

## 0. Overview

actionlint is a static analysis tool for GitHub Actions workflow YAML files, written in Go. Its architecture consists of:

1. **YAML parsing** via `go.yaml.in/yaml/v4` into a `yaml.Node` tree
2. **Alias resolution** pre-pass on the `yaml.Node` tree
3. **Hand-written recursive descent parser** converting `yaml.Node` into a typed AST
4. **Visitor/Pass pattern** for AST traversal
5. **Rule engine** executing lint rules as Pass implementations
6. **Expression parser** (separate recursive descent parser for `${{ }}` expressions)
7. **Expression semantic analyzer** with type inference and context validation
8. **Generated data** for webhooks, context availability, and popular actions

### 0.1 Package Structure

All code lives in a single Go package `actionlint`. Key source files:

| File | Responsibility |
|---|---|
| `parse.go` | YAML → AST parser (~1700 lines) |
| `ast.go` | AST type definitions (~1050 lines) |
| `pass.go` | Visitor/Pass infrastructure (~200 lines) |
| `linter.go` | Linter entry point and orchestration (~650 lines) |
| `expr_parser.go` | Expression recursive descent parser |
| `expr_lexer.go` | Expression lexer |
| `expr_ast.go` | Expression AST nodes |
| `expr_sema.go` | Expression semantic checker with type inference |
| `expr_type.go` | Expression type system |
| `rule_*.go` | Individual lint rules |
| `error.go` | Error/diagnostic types |
| `all_webhooks.go` | Generated webhook event data |
| `availability.go` | Generated context availability data |
| `popular_actions.go` | Generated popular action metadata |

---

## 1. YAML Integration

### 1.1 Library

actionlint uses `go.yaml.in/yaml/v4` (successor to `gopkg.in/yaml.v3`). It unmarshals the entire YAML document into a `yaml.Node` tree, then performs custom conversion.

### 1.2 yaml.Node Model

```go
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

### 1.3 Parse Entry Point

```go
func Parse(b []byte) (*Workflow, []*Error) {
    var n yaml.Node
    if err := yaml.Unmarshal(b, &n); err != nil {
        return nil, handleYAMLUnmarshalError(err)
    }
    // n is DocumentNode; n.Content[0] is the root mapping
    p := &parser{}
    p.resolveAliases(&n)
    w := p.parse(n.Content[0])
    return w, p.errors
}
```

`handleYAMLUnmarshalError` converts `yaml.TypeError` and `yaml.ParserError` into actionlint's `[]*Error` format.

### 1.4 Tag-Based Type Dispatch

The parser uses `yaml.Node.Tag` to distinguish scalar types:

| Tag | Meaning | Used by |
|---|---|---|
| `!!str` | String scalar | `parseString`, `parseExpression` |
| `!!bool` | Boolean scalar | `parseBool` |
| `!!int` | Integer scalar | `parseInt` |
| `!!float` | Float scalar | `parseFloat` |
| `!!null` | Null scalar | empty value detection |
| `!!seq` | Sequence | `checkSequence` |
| `!!map` | Mapping | `parseMapping` |

---

## 2. Alias Resolution

### 2.1 Algorithm

```go
func (p *parser) resolveAliases(root *yaml.Node)
```

Pre-walk of the entire `yaml.Node` tree:

1. Track all anchors (defined) and aliases (used) with their positions
2. For each `AliasNode`, replace it with the referenced anchor's `yaml.Node`
3. Detect and report recursive aliases as errors
4. Report unused anchors as warnings (not implemented in current version)

### 2.2 Design Decision

Alias resolution happens **before** the custom parser runs, ensuring the parser never encounters `AliasNode`. This simplifies all downstream parsing logic.

---

## 3. AST Definitions

### 3.1 Primitive Types

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

### 3.2 Helper Functions on Primitives

```go
func (p *Pos) String() string
func (p *Pos) IsBefore(other *Pos) bool

func ContainsExpression(s string) bool          // checks if string contains ${{ }}
func (s *String) ContainsExpression() bool
func isExprAssigned(s string) bool              // checks if entire string is ${{ ... }}
func (s *String) IsExpressionAssigned() bool
```

### 3.3 Workflow (Root)

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

### 3.4 Event (Interface)

```go
type Event interface {
    EventName() string
}
```

#### 3.4.1 WebhookEvent

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

#### 3.4.2 ScheduledEvent

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

#### 3.4.3 WorkflowDispatchEvent

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

#### 3.4.4 WorkflowCallEvent

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

#### 3.4.5 Other Events

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

### 3.5 Job

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

### 3.6 Step and Exec

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

### 3.7 Structural Nodes

```go
// Permissions: scalar ("read-all"/"write-all") or mapping
type PermissionScope struct {
    Name  *String
    Value *String
}

type Permissions struct {
    All    *String
    Scopes map[string]*PermissionScope
    Pos    *Pos
}

// Env: expression or mapping
type EnvVar struct {
    Name  *String
    Value *String
}

type Env struct {
    Vars       map[string]*EnvVar
    Expression *String
}

// Defaults
type DefaultsRun struct {
    Shell            *String
    WorkingDirectory *String
    Pos              *Pos
}

type Defaults struct {
    Run *DefaultsRun
    Pos *Pos
}

// Concurrency
type Concurrency struct {
    Group            *String
    CancelInProgress *Bool
    Pos              *Pos
}

// Environment
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

// Runner (runs-on)
type Runner struct {
    Labels    []*String
    LabelsExpr *String
    Group     *String
}

// WorkflowCall (job-level reusable workflow)
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

### 3.8 Strategy / Matrix

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

### 3.9 RawYAMLValue (Recursive Matrix Values)

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

### 3.10 Container / Services / Credentials

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

---

## 4. Parser Implementation

### 4.1 Parser State

```go
type parser struct {
    errors []*Error
}
```

The parser accumulates errors in a slice and never aborts on the first error (multi-error recovery).

### 4.2 Internal Helper Types

```go
// Mapping entry yielded by parseMapping iterator
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

### 4.3 Error Reporting Functions

```go
func (p *parser) error(n *yaml.Node, m string)
func (p *parser) errorAt(pos *Pos, m string)
func (p *parser) errorf(n *yaml.Node, format string, args ...interface{})
func (p *parser) errorfAt(pos *Pos, format string, args ...interface{})
func (p *parser) unexpectedKey(s *String, sec string, expected []string)
func (p *parser) missingExpression(n *yaml.Node, expecting string)
```

### 4.4 Validation Helpers

```go
func (p *parser) checkNotEmpty(sec string, len int, n *yaml.Node) bool
func (p *parser) checkSequence(sec string, n *yaml.Node, allowEmpty bool) bool
func (p *parser) checkString(n *yaml.Node, allowEmpty bool) bool
```

### 4.5 Scalar Parse Functions

```go
// Parse a string scalar. Returns nil on error.
func (p *parser) parseString(n *yaml.Node, allowEmpty bool) *String

// Parse a boolean. If tag is !!str, treats as expression.
func (p *parser) parseBool(n *yaml.Node) *Bool

// Parse an integer. If tag is !!str, treats as expression.
func (p *parser) parseInt(n *yaml.Node) *Int

// Parse a float. If tag is !!str or !!int, handles appropriately.
func (p *parser) parseFloat(n *yaml.Node) *Float

// Verify value is a ${{ }} expression. Error if not.
func (p *parser) parseExpression(n *yaml.Node, expecting string) *String

// If value is a ${{ }} expression, return it. Otherwise return nil (no error).
func (p *parser) mayParseExpression(n *yaml.Node) *String
```

### 4.6 Collection Parse Functions

```go
// Parse a string sequence.
func (p *parser) parseStringSequence(sec string, n *yaml.Node, allowEmpty bool, allowElemEmpty bool) []*String

// Parse a scalar or sequence of strings (polymorphic).
func (p *parser) parseStringOrStringSequence(sec string, n *yaml.Node, allowEmpty bool, allowElemEmpty bool) []*String
```

### 4.7 Mapping Traversal

The mapping traversal is the core parsing pattern. It uses Go iterators (`iter.Seq`):

```go
// Generic mapping traversal with duplicate detection and optional case-insensitivity.
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

### 4.8 root parse (Workflow)

```go
func (p *parser) parse(n *yaml.Node) *Workflow
```

Top-level mapping traversal:
- `"name"` → `parseString`
- `"run-name"` → `parseString`
- `"on"` → `parseEvents`
- `"permissions"` → `parsePermissions`
- `"env"` → `parseEnv`
- `"defaults"` → `parseDefaults`
- `"concurrency"` → `parseConcurrency`
- `"jobs"` → `parseJobs`
- Other → `unexpectedKey`

Post-validation: `on` and `jobs` are required.

### 4.9 Events Parse

```go
func (p *parser) parseEvents(n *yaml.Node) []Event
func (p *parser) parseEventWithNoConfig(n *yaml.Node) Event
```

Three forms: scalar → single event, sequence → multiple events, mapping → events with config.

For mapping form, dispatches by event name:
- `"schedule"` → `parseScheduleEvent`
- `"workflow_dispatch"` → `parseWorkflowDispatchEvent`
- `"repository_dispatch"` → `parseRepositoryDispatchEvent`
- `"workflow_call"` → `parseWorkflowCallEvent`
- `"image_version"` → `parseImageVersionEvent`
- other → `parseWebhookEvent`

#### 4.9.1 Specific Event Parse Functions

```go
func (p *parser) parseScheduleEvent(pos *Pos, n *yaml.Node) *ScheduledEvent
func (p *parser) parseWorkflowDispatchEvent(pos *Pos, n *yaml.Node) *WorkflowDispatchEvent
func (p *parser) parseWorkflowDispatchEventInput(name *String, n *yaml.Node) *DispatchInput
func (p *parser) parseRepositoryDispatchEvent(pos *Pos, n *yaml.Node) *RepositoryDispatchEvent
func (p *parser) parseWebhookEvent(name *String, n *yaml.Node) *WebhookEvent
func (p *parser) parseWebhookEventFilter(name *String, n *yaml.Node) *WebhookEventFilter
func (p *parser) parseWorkflowCallEvent(pos *Pos, n *yaml.Node) *WorkflowCallEvent
func (p *parser) parseWorkflowCallEventInput(id string, name *String, n *yaml.Node) *WorkflowCallEventInput
func (p *parser) parseWorkflowCallEventSecret(name *String, n *yaml.Node) *WorkflowCallEventSecret
func (p *parser) parseWorkflowCallEventOutput(name *String, n *yaml.Node) *WorkflowCallEventOutput
func (p *parser) parseImageVersionEvent(pos *Pos, n *yaml.Node) *ImageVersionEvent
```

### 4.10 Structural Section Parse Functions

```go
func (p *parser) parsePermissions(pos *Pos, n *yaml.Node) *Permissions
func (p *parser) parseEnv(n *yaml.Node) *Env
func (p *parser) parseDefaults(pos *Pos, n *yaml.Node) *Defaults
func (p *parser) parseConcurrency(pos *Pos, n *yaml.Node) *Concurrency
func (p *parser) parseEnvironment(pos *Pos, n *yaml.Node) *Environment
func (p *parser) parseOutputs(n *yaml.Node) map[string]*Output
```

### 4.11 Strategy / Matrix Parse Functions

```go
func (p *parser) parseStrategy(pos *Pos, n *yaml.Node) *Strategy
func (p *parser) parseMatrix(pos *Pos, n *yaml.Node) *Matrix
func (p *parser) parseMaxParallel(n *yaml.Node) *Int
func (p *parser) parseMatrixCombinations(sec string, n *yaml.Node) *MatrixCombinations
func (p *parser) parseRawYAMLValue(n *yaml.Node) RawYAMLValue
```

### 4.12 Container / Services Parse Functions

```go
func (p *parser) parseContainer(sec string, pos *Pos, n *yaml.Node) *Container
func (p *parser) parseCredentials(pos *Pos, n *yaml.Node) *Credentials
func (p *parser) parseServices(n *yaml.Node) *Services
```

### 4.13 Job Parse

```go
func (p *parser) parseJob(id *String, n *yaml.Node) *Job
func (p *parser) parseJobs(n *yaml.Node) map[string]*Job
func (p *parser) parseRunsOn(n *yaml.Node) *Runner
func (p *parser) parseSnapshot(pos *Pos, n *yaml.Node) *Snapshot
func (p *parser) parseTimeoutMinutes(n *yaml.Node) *Float
```

Job parsing includes reusable workflow detection and constraint validation:
- If `uses` is present → reusable workflow call; certain keys are forbidden
- If `uses` is absent → normal job; `steps` and `runs-on` are required

### 4.14 Step Parse

```go
func (p *parser) parseStep(n *yaml.Node) *Step
func (p *parser) parseSteps(n *yaml.Node) []*Step
func (p *parser) parseStepExecAction(entries []workflowMappingEntry, isDocker bool) *ExecAction
func (p *parser) parseStepExecRun(entries []workflowMappingEntry) *ExecRun
```

Step parsing uses a **2-pass design**:
1. **Pass 1**: Collect all mapping entries, determine step kind (`run` vs `uses`/`docker://`)
2. **Pass 2**: Build the appropriate `Exec` variant based on determined kind

---

## 5. Visitor / Pass Pattern

### 5.1 Pass Interface

```go
type Pass interface {
    VisitStep(node *Step) error
    VisitJobPre(node *Job) error
    VisitJobPost(node *Job) error
    VisitWorkflowPre(node *Workflow) error
    VisitWorkflowPost(node *Workflow) error
}
```

### 5.2 Visitor

```go
type Visitor struct {
    passes []Pass
    dbg    io.Writer
}

func NewVisitor() *Visitor
func (v *Visitor) AddPass(p Pass)
func (v *Visitor) EnableDebug(w io.Writer)
func (v *Visitor) Visit(n *Workflow) error
```

### 5.3 Traversal Order

```
VisitWorkflowPre(workflow)      // all passes
  for each job:
    VisitJobPre(job)            // all passes
    for each step:
      VisitStep(step)           // all passes
    VisitJobPost(job)           // all passes
VisitWorkflowPost(workflow)     // all passes
```

- Depth-first workflow traversal
- At each stage, all registered passes are invoked in order
- If any pass callback returns an error, traversal aborts (used for internal errors, not lint diagnostics)
- Optional debug timing per phase

---

## 6. Linter Orchestration

### 6.1 Linter Structure

```go
type Linter struct {
    projects       *Projects
    out            io.Writer
    logOut         io.Writer
    logLevel       LogLevel
    oneline        bool
    shellcheck     string
    pyflakes       string
    ignorePats     IgnorePatterns
    stdin          string
    defaultConfig  *Config
    errFmt         *ErrorFormatter
    cwd            string
    onRulesCreated func([]Rule) []Rule
}
```

### 6.2 Lint Flow

```go
func (l *Linter) check(path string, content []byte, project *Project,
    proc *concurrentProcess, localActions *LocalActionsCache,
    localReusableWorkflows *LocalReusableWorkflowCache) ([]*Error, error)
```

1. `Parse(content)` → `(*Workflow, []*Error)`
2. If parse produced a workflow, construct Rule set
3. Build `Visitor` and add all Rules as Passes
4. `visitor.Visit(workflow)`
5. Collect diagnostics from each Rule
6. Merge parse errors and rule errors
7. `filterErrors` → sort + deduplicate → return

### 6.3 Concurrent Multi-File Linting

```go
func (l *Linter) LintRepository(dir string) ([]*Error, error)
func (l *Linter) LintDir(dir string, project *Project) ([]*Error, error)
func (l *Linter) LintFiles(filepaths []string, project *Project) ([]*Error, error)
func (l *Linter) LintFile(path string, project *Project) ([]*Error, error)
func (l *Linter) LintStdin(stdin io.Reader) ([]*Error, error)
func (l *Linter) Lint(path string, content []byte, project *Project) ([]*Error, error)
```

Uses `errgroup` + semaphore for concurrent file processing with per-project caches.

### 6.4 Rule Hook

```go
type LinterOptions struct {
    // ...
    OnRulesCreated func([]Rule) []Rule  // allows external rule injection/filtering
}
```

---

## 7. Expression Parser

### 7.1 Lexer

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

### 7.2 Parser

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

### 7.3 Expression AST

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

### 7.4 Expression Visitor

```go
type VisitExprNodeFunc func(node, parent ExprNode, entering bool)

func VisitExprNode(n ExprNode, f VisitExprNodeFunc)
func visitExprNode(n, p ExprNode, f VisitExprNodeFunc)
```

Traverses the expression tree with `entering = true` before visiting children, `entering = false` after.

---

## 8. Expression Semantic Checker

### 8.1 Function Signatures

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

### 8.2 Checker State

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

### 8.3 Context and Function Updates

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

### 8.4 Type Checking Functions

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

### 8.5 Key Semantic Patterns

1. **Overloaded function resolution**: Functions like `contains` have multiple signatures (string,string) or (array,any). All are checked; first matching signature is used.
2. **Case-insensitive function lookup**: Function names are compared case-insensitively.
3. **Context availability**: `SetContextAvailability` is called before each expression check to configure which root identifiers are valid at that location.
4. **Type narrowing**: Logical expressions (`&&`, `||`) use type narrowing to improve inference accuracy on branches.
5. **Untrusted input tracking**: A separate checker (`UntrustedInputChecker`) is optionally invoked at enter/leave of each expression node to track taint flow.

---

## 9. Generated Data

### 9.1 all_webhooks.go

Static table mapping webhook event names to their allowed activity types and filter options. Used by the event validation rules.

### 9.2 availability.go

Static table defining which expression contexts (`github`, `env`, `steps`, etc.) and special functions (`success()`, `failure()`, etc.) are available at each workflow position. Keyed by position type (workflow-level, job-level, step-level, and finer-grained positions like `if:`, `env:`, `with:`).

### 9.3 popular_actions.go

Static table of well-known GitHub Actions with their expected input names, output names, and types. Used by rules that validate `with:` inputs and step output references.

---

## 10. Error Model

### 10.1 Error Structure

```go
type Error struct {
    Message  string
    Filepath string
    Line     int
    Column   int
    Kind     string  // rule identifier
}
```

### 10.2 Expression Error

```go
type ExprError struct {
    Message string
    Offset  int
    Line    int
    Column  int
}
```

### 10.3 Error Handling Philosophy

- **Parser errors**: Accumulated in `parser.errors`, never cause early abort
- **Rule errors**: Each rule accumulates its own diagnostics; collected after visitor traversal
- **Expression errors**: Lexer stores first error; parser stores first error; semantic checker accumulates all errors
- **Final output**: All errors are merged, filtered by ignore patterns, sorted by position, and deduplicated

---

## 11. Key Design Decisions and Patterns

### 11.1 yaml.Node Tree vs Event Stream

actionlint uses the `yaml.Node` tree model (full DOM), not a streaming parser. This allows:
- Random access to any node's children
- Pre-pass alias resolution over the full tree
- Position information on every node

Trade-off: Higher memory usage than event-stream parsing.

### 11.2 Error Recovery Strategy

The parser never aborts on a single error. Each parse function:
1. Validates the current node type
2. On mismatch, reports an error and returns a partial/nil result
3. The caller continues processing remaining siblings

This allows collecting the maximum number of diagnostics in a single pass.

### 11.3 Polymorphic YAML Fields

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

### 11.4 Two-Pass Step Parsing

Steps are parsed in two passes because the step kind (run vs uses) determines which keys are valid, but the `run`/`uses` key may appear anywhere in the mapping:

1. **Pass 1**: Collect all entries, find `run` or `uses` key
2. **Pass 2**: Dispatch to `parseStepExecRun` or `parseStepExecAction`

### 11.5 Case Sensitivity

- Mapping keys within a section: case-sensitive (e.g., `name` ≠ `Name`)
- Identifiers used as dictionary keys: case-insensitive (e.g., job IDs, env var names, matrix row names)
- The `parseMapping` function supports both modes via the `caseSensitive` parameter

### 11.6 Iterator-Based Mapping

actionlint uses Go 1.23 iterators (`iter.Seq`) for `parseMapping`, yielding entries to the caller via a `for range` loop. This avoids materializing an intermediate slice and provides a clean API for the caller to switch on keys.
