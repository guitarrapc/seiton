# Seiton Linter Go Implementation Specification

> Go implementation specification for the linter contract defined in `Seiton_Linter_spec.md`. This document captures Go runtime structures and behavior for rule execution, exclusion/suppression, and diagnostic output. See `Seiton_Linter_csharp_spec.md` for the C# target. Both language specs share the same outline; only language-specific content differs. Parser behavior is specified in `Seiton_Parser_spec.md` and `Seiton_Parser_go_spec.md`.

> **Cross-document synchronization rule**: `Seiton_Linter_spec.md` is the source of truth. When this Go spec is updated, also review and update `Seiton_Linter_spec.md`, `Seiton_Linter_csharp_spec.md`, and `linter_implementation_go_plan.md` in the same PR/commit scope.

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

| Rule ID | Required Behavior Summary |
|---|---|
| `job-structure` | Validate core job shape constraints: `uses` is mutually exclusive with `steps`/`runs-on`, and each job requires either reusable-call form (`uses`) or executable form (`runs-on` + `steps`). |
| `reusable-workflow` | Validate reusable workflow call semantics: `with`/`secrets` require `uses`, reusable-call jobs must reject incompatible execution keys, and local reusable calls should validate caller `with`/`secrets` against called workflow `on.workflow_call` contracts when statically resolvable. |
| `permissions` | Validate `permissions` value domain: scalar must be `read-all` or `write-all`; scope values must be `read`, `write`, or `none`. |
| `popular-action-inputs` | Validate known action input names against maintained popular-action metadata and emit diagnostics for unknown inputs. |
| `outdated-action-runner` | Error when a popular action's `runs.using` runtime is deprecated. The rule is catalog-driven: it looks up the action in the popular-actions generated catalog, reads the `runs.using` value, and checks against a maintained list of deprecated runtimes (`node12`, `node16`). |
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
| `expr-undefined-var` | Error when expressions reference context roots unavailable in the current scope (for example job scope vs step scope context mismatch). Validates `step.run`, `step.if`, `step.env`, and `step.with` expressions. For `matrix` context, builds strict per-job types from matrix row definitions (including nested object property inference) and flags undefined axis keys. For `steps` context, builds strict per-job types from step IDs and validates forward references. For `needs` context, validates that referenced job IDs are declared in the job's `needs` list. For popular actions with known outputs, builds strict step output types and flags unknown output names. |
| `run-env-context-direct-use` | Error when `run:` script text directly references `${{ env.* }}`; shell variable expansion must be used instead. |
| `run-secrets-context-direct-use` | Error when `run:` script text directly references `${{ secrets.* }}`; secret values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `run-inputs-context-direct-use` | Error when `run:` script text directly references `${{ inputs.* }}` or `${{ github.event.inputs.* }}`; values should be mapped via `env` and referenced as shell variables (`${ENV_NAME}` / `$ENV_NAME` / `$env:ENV_NAME`). |
| `secrets-whole-context-access` | Error when any expression references the entire `secrets` context as an object (e.g. `${{ toJson(secrets) }}`, `${{ format('{0}', secrets) }}`), rather than accessing a specific secret key (`secrets.MY_KEY`). Exposing the whole secrets object in one expression leaks all secrets simultaneously. |
| `checkout-persist-credentials` | Warn when `actions/checkout` does not explicitly set `with.persist-credentials: false`; persisting checkout-managed credentials increases secret exposure risk when repository data is reused or uploaded. Legacy checkout versions store credentials in `.git/config`; v6+ stores them under `$RUNNER_TEMP`. |
| `artipacked` | Detect credential leakage risk when `actions/checkout` (without `persist-credentials: false`) is followed later in the same job by `actions/upload-artifact` uploading a dangerous path (`.`, `..`, `${{ github.workspace }}`) in a way that may include hidden files. Root-like uploads can sweep sensitive checkout state into artifacts; for early pinned `actions/upload-artifact@v4.0` through `@v4.3`, hidden files are included by default, while for `actions/upload-artifact@v4.4+` and floating `@v4`, hidden files are excluded by default. Arbitrary or unrecognized version refs (e.g. `@v4-legacy`, `@v4.x`, `@v4.4-legacy`) are treated conservatively as unknown and assumed to potentially include hidden files. As a result, legacy `.git/config` credentials are typically included by default for pinned `v4.0`–`v4.3`, but for `v4.4+`/floating `@v4` they are typically included only when hidden files are uploaded explicitly or hidden-file behavior is not statically known. Error for non-v6+ checkout; warning for v6+ (credentials stored in `$RUNNER_TEMP`). |
| `workflow-secrets` | Error when workflow-level `env` assigns values from `secrets.*` or `github.token` in workflows with multiple jobs. |
| `job-secrets` | Error when job-level `env` assigns values from `secrets.*` or `github.token` in jobs with multiple steps. |
| `action-shell-is-required` | Error when a composite action `run` step omits explicit `shell` declaration (including empty shell values). |


### 3.5 Phase 14 Catalog Additions

The shared rule catalog additionally defines the following Phase 14 rule IDs:

- `known-vulnerable-actions`
- `impostor-commit`
- `ref-confusion`
- `stale-action-refs`
- `deny-read-all`
- `deny-inherit-secrets`
- `job-timeout-minutes-required`
- `github-app-token-inputs`

Implementation status:

- Shared-spec catalog status is fixed for these IDs.
- Go runtime mapping for these IDs is planned and should follow the same sequencing as documented in `.github/docslinter_implementation_csharp_plan.md` Phase 14 until Go-specific implementation plan items are added.

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

- These IDs are specification-defined high-priority parity candidates and are not part of the current Go default rule catalog mapping.
- `archived-uses` / `insecure-commands` / `overprovisioned-secrets` / `forbidden-uses` / `ref-version-mismatch` / `use-trusted-publishing` are tracked as zizmor parity candidates.
- Go runtime implementation planning for these IDs should be tracked explicitly when Go-side rule implementation work starts.

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

- `.github/docsSeiton_Linter_spec.md`
- `.github/docsSeiton_spec.md` when parser/linter boundary wording changes
