# Seiton Linter Go Implementation Specification

> Go implementation specification for the linter contract defined in `.github/docs/Seiton_Linter_spec.md`. This document captures Go runtime structures and behavior for rule execution, exclusion/suppression, and diagnostic output. See `.github/docs/Seiton_Linter_csharp_spec.md` for the C# target. Both language specs share the same outline; only language-specific content differs. Parser behavior is specified in `.github/docs/Seiton_Parser_spec.md` and `.github/docs/Seiton_Parser_go_spec.md`.

> **Cross-document synchronization rule**: `.github/docs/Seiton_Linter_spec.md` is the source of truth. When this Go spec is updated, also review and update `.github/docs/Seiton_Linter_spec.md` and `.github/docs/Seiton_Linter_csharp_spec.md` in the same PR/commit scope.

---

## 0. Go Preamble

### 0.1 Contract

This document defines the Go implementation contract for linter behavior under `Seiton_Linter_spec.md`.

In scope:

- Go linter entrypoint and orchestration
- `Pass` traversal and rule integration
- rule configuration and exclusion/suppression behavior mapping
- suppression observability output contract in Go result shapes

Out of scope:

- YAML parse algorithm details
- AST schema definitions

### 0.2 Overview

The Seiton Linter Go implementation provides:

1. Input document kind classification and parse-first lint execution flow
2. Pass-based traversal and rule execution
3. Deterministic diagnostics post-processing
4. Config/inline suppression integration per contract
5. Concurrent repository/file lint execution model (implementation-dependent)
6. GitHub Actions context-dependent expression semantic validation (via `expr-undefined-var` rule and expression semantic analysis)

> **Boundary note**: Under the refined expression validation boundary (`Seiton_spec.md` §3), the linter owns GitHub Actions context-dependent expression validation: context availability, function availability by workflow position, dynamic property existence, and workflow-site-aware type suitability. The integration contract exposes an optional expression-artifact hook; when attached, the linter consumes those artifacts without re-parsing, and otherwise falls back to its existing expression parse cache.

### 0.3 Structure

Representative implementation surface:

- `Linter`
- `LinterOptions`
- `Visitor`
- `Pass`
- rule implementations
- error/diagnostic result model

### 0.4 Runtime Model

Linter runtime assumes parser output as structural input and applies rule execution/post-processing over that output.

- Parse result consumed first
- Rule traversal performed next
- Diagnostics collected, post-processed, and filtered by linter policies

### 0.5 Design

1. Keep parser/linter responsibility boundary strict.
2. Keep lint output deterministic for identical input/config.
3. Keep rule/exclusion policy behavior aligned with language-agnostic linter contract.
4. Keep language-specific runtime details explicit and testable.
5. Keep the implemented rule catalog aligned with the rule inclusion policy in `Seiton_Linter_spec.md` §1.1; Go runtime-specific rules must not reintroduce style-only or tool-preference-only checks.

---

## 1. Go Runtime Surface

Primary types/functions (reference naming may vary by package):

- `Linter`
- `LinterOptions`
- `Visitor`
- `Pass`
- rule implementations
- error/diagnostic result model

Implementation details should be tracked against language-specific implementation work items.

Current cross-runtime parity note:

- The shared rule catalog includes opt-in local and online rules. Go implementations must preserve the same rule IDs, default-off behavior, and default severity contract from `Seiton_Linter_spec.md` even if implementation work lands later.

---

## 2. Entry Point Mapping

Shared contract (`Seiton_Linter_spec.md` §2):

```
Check(utf8Yaml, filePath) -> LintResult
```

Go mapping (reference shape):

- methods on `Linter` (`LintFile`, `Lint`, etc.)

Normative behavior follows `Seiton_Linter_spec.md` for:

- parser kind classification/routing
- parse-first flow
- fatal parse short-circuit
- rule execution
- deterministic post-processing

Reference runtime shape:

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

Lint flow (reference):

```go
func (l *Linter) check(path string, content []byte, project *Project,
	proc *concurrentProcess, localActions *LocalActionsCache,
	localReusableWorkflows *LocalReusableWorkflowCache) ([]*Error, error)
```

1. `Parse(content)` -> `(*Workflow, []*Error)`
2. If parse produced a workflow, construct rule set
3. Build `Visitor` and add rules as passes
4. `visitor.Visit(workflow)`
5. Collect diagnostics from each rule
6. Merge parse errors and rule errors
7. `filterErrors` -> sort + deduplicate -> return

Public API (reference):

```go
func NewLinter(out io.Writer, opts *LinterOptions) (*Linter, error)
func (l *Linter) LintRepository(dir string) ([]*Error, error)
func (l *Linter) LintDir(dir string, project *Project) ([]*Error, error)
func (l *Linter) LintFiles(filepaths []string, project *Project) ([]*Error, error)
func (l *Linter) LintFile(path string, project *Project) ([]*Error, error)
func (l *Linter) LintStdin(stdin io.Reader) ([]*Error, error)
func (l *Linter) Lint(path string, content []byte, project *Project) ([]*Error, error)
```

### 2.1. Multi-File Parallel Execution

Shared contract reference: `Seiton_Linter_spec.md` §2.1.

Go implementation:

- Repository/file lint paths use `errgroup` + semaphore for concurrent processing.
- Project-scoped caches (for example local actions/reusable workflows) are shared across worker goroutines.
- Per-file `check` goroutines each own an independent rule set; diagnostics are collected per-file and merged in input-file order after all goroutines complete.

---

## 3. Pass/Rule Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §4.1, §4.2, §4.3

Go mapping:

- `Pass` interface callbacks
- `Visitor` traversal order
- rules as pass implementations

### 3.1 Pass Interface

```go
type Pass interface {
		VisitStep(node *Step) error
		VisitJobPre(node *Job) error
		VisitJobPost(node *Job) error
		VisitWorkflowPre(node *Workflow) error
		VisitWorkflowPost(node *Workflow) error
}
```

### 3.2 Visitor

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

Traversal order:

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
- If any pass callback returns an error, traversal aborts (internal error path)

### 3.3 Rule Hook

Rules are pass implementations. Rule injection/filtering can be applied through options:

```go
type LinterOptions struct {
		// ...
		OnRulesCreated func([]Rule) []Rule
}
```

### 3.4 Default Rule Catalog (Contract Mapping)

Go runtime behavior must align with `Seiton_Linter_spec.md` §4.4 for the default rule catalog.

> **Detail policy:** Rule behavior is defined in `Seiton_Linter_spec.md` §4.4. User-facing detail (path lists, examples, remediation) lives in [`docs/rules.md`](../../docs/rules.md). This table only records Go-specific implementation notes.

| Rule ID | Go Implementation Notes |
|---|---|
| `job-structure` | — |
| `reusable-workflow` | — |
| `permissions` | — |
| `popular-action-inputs` | Catalog-driven via generated popular-actions data. |
| `outdated-action-runner` | Catalog-driven via generated popular-actions data. |
| `unpinned-uses` | — |
| `unpinned-image` | — |
| `dangerous-triggers` | — |
| `job-permissions-required` | — |
| `needs-graph` | — |
| `shell-name` | — |
| `runner-label` | Uses generated runner-labels data. |
| `runner-no-latest` | — |
| `id-naming` | — |
| `glob-pattern` | — |
| `deny-write-all` | — |
| `credentials` | — |
| `template-injection` | — |
| `expr-undefined-var` | Builds strict per-job types for matrix (including nested object inference), steps, and needs. Popular-action outputs are catalog-driven. |
| `run-env-context-direct-use` | — |
| `run-secrets-context-direct-use` | — |
| `run-inputs-context-direct-use` | — |
| `secrets-whole-context-access` | — |
| `checkout-persist-credentials` | — |
| `artipacked` | Step-order scan within job. Tracks unsafe legacy/v6+ checkout state and re-evaluates exclusion lines. |
| `workflow-secrets` | — |
| `job-secrets` | — |
| `action-shell-is-required` | — |

> **Note:** The normative rule catalog is defined in `.github/docs/Seiton_Linter_spec.md` §4.4. Rules not yet mapped in Go are tracked in Go-specific implementation work items.

---

## 4. Exclusion and Suppression Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5, §6.1, §11

Go implementation must provide:

- config-based exclusion matching
- inline next-line directive handling
- unknown rule-id as configuration error
- severity override application
- suppression observability in lint result output

### 4.1 Rule-Specific Configuration Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §5.8

Go implementation must support rule-specific configuration within `rules.<rule-id>` entries. Each rule accepts the shared `enabled` / `severity` keys plus rule-specific keys.

Additive merge (`effective = built-in U user-extended`) is used for all additive list keys:

- `rules.dangerous-triggers.events`
- `rules.runner-label.known-hosted-labels`
- `rules.credentials.public-registries`
- `rules.cache-poisoning-trigger.untrusted-triggers`
- `rules.self-hosted-runner-trigger.untrusted-triggers`
- `rules.unredacted-secrets.output-commands`

Direct list keys:

- `rules.forbidden-uses.allow` / `rules.forbidden-uses.deny`
- `rules.expr-undefined-var.assume-events`

Mapping requirements:

- Use deterministic deduplication after normalization.
- Normalization uses ASCII lower-case matching for event names, runner labels, and registry hosts.
- Invalid customization entries are configuration errors.
- Extension never removes built-in defaults.
- Unknown rule-specific keys for a given rule ID are configuration errors.

### 4.2 Auto-Fix Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §8

Go runtime mapping for fix-capable diagnostics:

- diagnostic model carries optional fix payload
- fix payload contains description and one-or-more text edits
- text edits use UTF-8 byte offset/length (`TextRange.Start`/`Length` compatible semantics)

Reference shape:

```go
type TextEdit struct {
	Offset  int
	Length  int
	NewText string
}

type DiagnosticFix struct {
	Description string
	Edits       []TextEdit
}
```

Implementation requirements:

- Rules attach fixes only when remediation is deterministic and safe.
- Edits inside one fix must be non-overlapping.
- Overlaps across diagnostics are conflict cases handled by fix-application layer.
- Fix application is separate from lint execution path.

### 4.3 Fix Engine Formatting Preservation Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §9

Go fix-engine implementation must enforce:

- Indentation preservation based on sibling/parent structure.
- Line-ending preservation (`LF`/`CRLF`) per file style.
- Quote-style preservation for scalar replacement when valid.
- YAML context safety (no implicit node-kind transition unless rule contract defines it).
- Whitespace stability outside explicit edit ranges.
- Fallback to no-fix when style-safe synthesis is ambiguous.

Go implementation note:

- Quote/range information comes from AST nodes.
- Indentation and line-ending style are inferred from original source bytes/text.

### 4.4 Fix Observability Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §10

Go result model must support caller-side fix workflows:

- enumerate fixable diagnostics
- count fixable diagnostics
- apply selected fixes without mutating original lint result object/value

Dry-run preview mapping:

- Fix engine provides unified diff generation from source + selected fixes.
- Preview APIs should support both string-return and writer-target output (CLI standard output use).
- Preview operation must not mutate source bytes.

Reference shape:

```go
func BuildUnifiedDiff(
    utf8YAML []byte,
    diagnosticsWithFix []*Diagnostic,
    filePath string,
    contextLines int,
) (string, error)

func WriteUnifiedDiff(
    w io.Writer,
    utf8YAML []byte,
    diagnosticsWithFix []*Diagnostic,
    filePath string,
    contextLines int,
) error
```

Output contract:

- Unified diff hunk format with `@@ -a,b +c,d @@`
- changed-line focused output with configurable context lines
- deterministic output for identical input bytes + selected fixes

### 4.5 Network-Assisted Pin Remediation Mapping

Shared contract reference:

- `Seiton_Linter_spec.md` §12

Go implementation mapping for network-assisted pin remediation.

#### 4.5.1 Resolver Interfaces

```go
// ActionShaResolver resolves a GitHub Actions / Reusable Workflow reference to a pinned commit SHA.
type ActionShaResolver interface {
    // Resolve resolves owner/repo@ref to (sha, tagComment, error).
    // Returns ("", "", ErrReferenceSkipped) when the ref is excluded by configuration.
    Resolve(ctx context.Context, owner, repo, ref string) (sha, tagComment string, err error)
}

// ImageDigestResolver resolves an OCI image reference to a pinned digest.
type ImageDigestResolver interface {
    // Resolve resolves imageRef to a sha256 digest string.
    // Returns ("", ErrReferenceSkipped) when the image is excluded by configuration.
    Resolve(ctx context.Context, imageRef string) (digest string, err error)
}

// ErrReferenceSkipped is returned when a resolver excludes a reference by configuration.
var ErrReferenceSkipped = errors.New("reference skipped by configuration")
```

Go implementation notes:

- Both interfaces accept `context.Context` for timeout/cancellation propagation.
- `ErrReferenceSkipped` sentinel distinguishes config-based skip from resolution failure.
- Implementations must be concurrency-safe and cache successful resolutions in-process.
- Error results (non-skip failures) must not be cached.
- Resolver implementations are injected by caller — not held by `Linter`.
- Action ref resolution order is tag-first (`refs/tags/{ref}`), then branch fallback (`refs/heads/{ref}`) when tag is not found.

#### 4.5.2 Remediation Entry Point

```go
type PinRemediationEngine struct {
    actionShaResolver   ActionShaResolver   // may be nil when EnableNetwork is false
    imageDigestResolver ImageDigestResolver // may be nil when EnableNetwork is false
    pinningConfig       FixPinningConfig
    imagesConfig        FixImagesConfig
    networkConfig       NetworkConfig
}

func NewPinRemediationEngine(
    actionShaResolver ActionShaResolver,
    imageDigestResolver ImageDigestResolver,
    pinningConfig FixPinningConfig,
    imagesConfig FixImagesConfig,
    networkConfig NetworkConfig,
) *PinRemediationEngine

// Remediate attaches network-resolved fix payloads to unpinned-uses / unpinned-image diagnostics.
// Returns a new slice where fixable diagnostics carry DiagnosticFix.
// Does not mutate the input slice.
func (e *PinRemediationEngine) Remediate(
    ctx context.Context,
    diagnostics []*Diagnostic,
    utf8Yaml []byte,
) (*RemediationResult, error)
```

#### 4.5.3 Configuration Mapping

Pin remediation configuration maps from the `fix` and `network` sections of the configuration file (§5.12, §5.13, §12.3):

```go
type FixConfig struct {
    Defaults FixDefaultsConfig
    Pinning  FixPinningConfig
    Images   FixImagesConfig
}

type FixDefaultsConfig struct {
    JobTimeoutMinutes *int // nil = no timeout auto-fix; <= 0 disables
}

type FixPinningConfig struct {
    EnableNetwork   bool
    MinAgeDays      int              // default: 14; 0 = no constraint
    ExcludeBranches []string         // default: ["main", "master"]
    IgnoreActions   []IgnoreActionEntry
}

type FixImagesConfig struct {
    EnableNetwork bool
    ExcludeImages []string // default: ["scratch"] — always enforced
    ExcludeTags   []string // default: ["latest"]
    IgnoreImages  []string // doublestar glob patterns
}

type IgnoreActionEntry struct {
    NamePattern string // regex
    RefPattern  string // regex
}

type NetworkConfig struct {
    OnError        NetworkErrorMode // default: Skip
    TimeoutSeconds int              // default: 30
    MaxConcurrency int              // default: min(4, max(1, logical CPUs))
    GitHub         GitHubNetworkConfig
}

type NetworkErrorMode int

const (
    NetworkErrorSkip NetworkErrorMode = iota
    NetworkErrorFail
)

type GitHubNetworkConfig struct {
    // Token env var order (SEITON_GITHUB_TOKEN → GITHUB_TOKEN) is hardcoded
    // and not configurable via config file.
    GHESApiURL   string // empty = github.com only
    GHESFallback bool
}
```

Safety invariants:

- `scratch` is always appended to `ExcludeImages` even if omitted from user config (enforced in construction).
- `EnableNetwork: false` prevents any network call; `Remediate` must return input diagnostics unchanged.
- Token resolution order is hardcoded as a code-internal constant: `["SEITON_GITHUB_TOKEN", "GITHUB_TOKEN"]`. This value is not exposed in config to prevent config-injection attacks.
- `FixPinningConfig.MinAgeDays = 0` disables age filtering only; tag-first and branch-fallback ref resolution still applies.

#### 4.5.4 Fix Format

Actions SHA fix (§12.5.1): replace `@ref` in uses scalar with `@<sha40> # <originalRef>` using ` # ` separator.

OCI digest fix (§12.5.2): append `@sha256:<hex>` to the image reference after the tag.

Both match the output format of pinact (actions) and dockerfile-pin (images).

#### 4.5.5 RemediationResult

```go
type RemediationResult struct {
    Diagnostics    []*Diagnostic
    ResolvedCount  int
    SkippedCount   int
    FailedCount    int
}
```

---

## 5. Cross-Document Consistency Rule

When this document is revised, also review and update:

- `.github/docs/Seiton_Linter_spec.md`
- `.github/docs/Seiton_spec.md` when parser/linter boundary wording changes
