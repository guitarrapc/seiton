# Generated Data Updater Implementation Plan

> Goal: implement a deterministic updater pipeline for generated parser/rule metadata (`WebhookTypes.g.cs`, `Availability.g.cs`, `PopularActions.g.cs`) so Spec §9 update policy is fully implemented, not just documented.

## 1. Problem Statement

Current state:
- Generated data files exist in `src/Seiton.Core/Generated/`.
- Specs (`Seiton_Parser_spec.md` and `Seiton_Parser_csharp_spec.md`) describe an update command (`Seiton.Update` or script).
- At plan authoring start, no updater project existed under `src/`.

Gap:
- There is no first-class, repeatable, testable update executable.
- Regeneration provenance and drift detection are manual.

## 2. Scope

In scope:
- Introduce `src/Seiton.Update` as a .NET console tool.
- Implement CLI command routing with `ConsoleAppFramework`.
- Implement fetch/parse/generate pipeline for:
  - webhook events + activity types
  - expression availability table
  - popular actions metadata
- Generate deterministic `.g.cs` output files in `src/Seiton.Core/Generated/`.
- Add verification mode and CI integration.
- Add tests for parsing, normalization, and generated output stability.

Out of scope:
- Runtime network access from parser/linter.
- Replacing current parser/linter behavior semantics (updater only manages data refresh).
- Auto-merge bot logic.

## 3. Target Deliverables

Code:
- `src/Seiton.Update/Seiton.Update.csproj`
- `src/Seiton.Update/Program.cs`
- `src/Seiton.Update/Commands/*.cs`
- `src/Seiton.Update/Sources/*.cs`
- `src/Seiton.Update/Parsers/*.cs`
- `src/Seiton.Update/Generators/*.cs`
- `src/Seiton.Update/Model/*.cs`

Generated outputs (existing files updated by tool):
- `src/Seiton.Core/Generated/WebhookTypes.g.cs`
- `src/Seiton.Core/Generated/Availability.g.cs`
- `src/Seiton.Core/Generated/PopularActions.g.cs`

Tests:
- `tests/Seiton.Update.Tests/Seiton.Update.Tests.csproj`
- Unit tests for source parsing and output rendering
- Golden tests for deterministic generated content

CI:
- `.github/workflows/generated-data-update.yaml`
- Optional weekly schedule + manual dispatch
- Verification gate in PR CI

Docs:
- `Docs/Seiton_Parser_spec.md` section 9 (status wording)
- `Docs/Seiton_Parser_csharp_spec.md` section 9 (status wording)
- `Docs/parser_implementation_csharp_plan.md` link/reference to updater completion

## 4. CLI Contract (Implemented)

Implementation note:
- Command and argument binding is implemented with `ConsoleAppFramework` so command methods can focus on updater logic instead of manual argument parsing.

Primary command:
- `dotnet run --project src/Seiton.Update -- sync`

Subcommands:
- `sync --dataset webhooks`
- `sync --dataset availability`
- `sync --dataset popular-actions`
- `verify --dataset webhooks`
- `verify --dataset availability`
- `verify --dataset popular-actions`
- `verify` (fails when generated outputs are stale)
- convenience aliases:
  - `sync-webhooks`, `verify-webhooks`
  - `sync-availability`, `verify-availability`
  - `sync-popular-actions`, `verify-popular-actions`
- `fetch-webhooks-sources` (download raw official webhook source files into `data/sources/...`)
- `parse-webhooks-sources` (parse local raw files into local parsed JSON artifacts)
- `merge-webhooks-sources` (merge parsed artifacts into `webhook_types.json`)
- `fetch-webhooks` (orchestrates `fetch-webhooks-sources` -> `parse-webhooks-sources` -> `merge-webhooks-sources`)
- `fetch-availability-sources` (download raw official availability source file into `data/sources/...`)
- `parse-availability-sources` (parse local availability raw file into local parsed JSON artifacts)
- `merge-availability-sources` (merge parsed artifacts into `availability.json`)
- `fetch-availability` (orchestrates `fetch-availability-sources` -> `parse-availability-sources` -> `merge-availability-sources`)
- `fetch-popular-actions-sources` (download raw official action metadata files into `data/sources/...`)
- `parse-popular-actions-sources` (parse local action metadata files into local parsed JSON artifacts)
- `merge-popular-actions-sources` (merge parsed artifacts into `popular_actions.json`)
- `fetch-popular-actions` (orchestrates `fetch-popular-actions-sources` -> `parse-popular-actions-sources` -> `merge-popular-actions-sources`)
- `validate-popular-actions-targets` (validates `data/sources/popular-actions/targets.json` contract before update stages)

Common options:
- `--offline` use vendored snapshots only
- `--input-dir <path>` override source fixture directory
- `--output-dir <path>` override generated output root
- `--strict` fail on unknown schema shape
- `--exclude-schema-only` (webhooks fetch) exclude events found only in SchemaStore (default behavior is include)

ConsoleAppFramework mapping (target shape):
- `sync` command method: `Sync(string dataset = "all", bool strictParity = false, ...)`
- `verify` command method: `Verify(string dataset = "all", bool strictParity = false, ...)`
- framework handles tokenization, type conversion, default values, and help output

Exit code policy:
- `0` success
- `1` usage/config error
- `2` network/source fetch error
- `3` parse/transform error
- `4` verify failed (outdated generated files)

## 5. Data Source Strategy

Principles:
- Build must not require network.
- Updater may fetch network sources only when explicitly run.
- All fetched raw source artifacts are vendored for reproducibility.

### 5.1 Source Precedence Policy (Primary-first)

Updater must resolve data using this precedence order:
1. Primary source: official GitHub documentation or official action metadata endpoints.
2. Secondary source: `.references/actionlint` generated outputs as a reference baseline.
3. Local vendored snapshots under `data/sources/**` for reproducible/offline runs.

Rules:
- Primary source defines Seiton's intended contract.
- actionlint is used as a parity probe, not as a contract owner.
- If primary source and actionlint disagree, updater records the diff and keeps primary-source-derived result by default.

Non-negotiable correctness rule:
- Official GitHub sources are the only normative source for generated data correctness.
- actionlint and `.references/actionlint/**` must never become the effective contract source.
- Any implementation path that consumes actionlint-only inputs is temporary and must be replaced or guarded by primary-source ingestion before final completion.

### 5.2 actionlint Differential Validation

For each dataset, updater provides a parity-diff report against `.references/actionlint`:
- webhooks: compare event names, activity types, and option allowance
- availability: compare root contexts and special function availability per key position
- popular actions: compare action IDs and known inputs

Validation modes:
- `sync`: emits diff report, does not fail unless `--strict-parity` is specified.
- `verify`: fails with exit code `4` when parity differences exceed configured tolerance.

Diff report output:
- `data/sources/reports/actionlint-diff-<dataset>.md`
- includes: missing in Seiton, extra in Seiton, and field-level mismatches

Proposed source storage:
- `data/sources/webhooks/github/raw/*` (downloaded official source files)
- `data/sources/webhooks/github/parsed/*` (local parse outputs from raw files)
- `data/sources/webhooks/github/webhook_types.json` (merged canonical snapshot)
- `data/sources/webhooks/actionlint/*` (reference-only parity inputs)
- `data/sources/availability/*`
- `data/sources/popular-actions/*`

Provenance metadata file:
- `data/sources/manifest.json`
  - dataset name
  - source URLs
  - fetched timestamp (UTC)
  - per-raw-file sha256 hashes (`rawFileHashes`)

## 6. Architecture

Pipeline stages per dataset:
1. FetchSource (network or local snapshot)
2. ParseRawDocument (HTML/Markdown/YAML/JSON as needed)
3. NormalizeModel (canonical typed model)
4. ValidateModel (required invariants)
5. RenderGeneratedCode (deterministic formatting)
6. WriteIfChanged (atomic file update)

Determinism rules:
- Stable ordering by ASCII key.
- Explicit newline style (`\n`).
- No timestamp comments in generated files.
- No environment-dependent formatting.

## 7. Implementation Phases

### Phase U1: Bootstrap Tooling

Status:
- Completed (ConsoleAppFramework command routing is in place and wired in solution build)

Tasks:
- Create `src/Seiton.Update` console project.
- Add `ConsoleAppFramework` command routing and structured logging.
- Add shared utilities (I/O, hashing, deterministic ordering helpers).

Exit criteria:
- `sync --help` and `verify --help` are emitted by `ConsoleAppFramework` command metadata.
- Project builds in solution CI.

Implementation notes:
- `src/Seiton.Update/Seiton.Update.csproj` includes `ConsoleAppFramework`.
- `Program.cs` registers `sync`, `verify`, dataset-specific fetch/parse/merge commands, and convenience aliases for webhooks/availability/popular-actions.
- `seiton.slnx` includes `src/Seiton.Update/Seiton.Update.csproj`.

### Phase U2: Webhook Updater

Status:
- Completed (parser/model/generator/sync-verify path and golden tests are in place)

Tasks:
- Implement source parser for webhook event/type data.
- Build canonical model for event options/types.
- Generate `WebhookTypes.g.cs` with deterministic ordering.
- Add golden test coverage.

Exit criteria:
- Regenerated `WebhookTypes.g.cs` is stable across repeated runs.
- Existing parser tests continue to pass.

Implementation notes (current):
- Added model: `src/Seiton.Update/Model/WebhookEventModel.cs`
- Added generator: `src/Seiton.Update/Generators/WebhookTypesCSharpGenerator.cs`
- Added primary parser: `src/Seiton.Update/Parsers/GitHubWebhookSourceParser.cs`
- Added docs parser: `src/Seiton.Update/Parsers/GitHubDocsWebhookMarkdownParser.cs`
- Kept reference parser for parity: `src/Seiton.Update/Parsers/ActionlintWebhookSourceParser.cs`
- Added sync/verify service: `src/Seiton.Update/Services/WebhookSyncService.cs`
- Added fetch source service: `src/Seiton.Update/Sources/GitHubWebhookFetcher.cs`
- Added manifest service/model: `src/Seiton.Update/Services/WebhookManifestService.cs`, `src/Seiton.Update/Model/SourceManifest.cs`
- Added source resolver: `src/Seiton.Update/Services/WebhookSourcePathResolver.cs`
  - primary generation source (normalized snapshot): `data/sources/webhooks/github/webhook_types.json`
  - actionlint parity reference: `data/sources/webhooks/actionlint/all_webhooks.go` or `.references/actionlint/all_webhooks.go`
- Added primary vendored snapshot: `data/sources/webhooks/github/webhook_types.json`
- Added tests: `tests/Seiton.Update.Tests/WebhookUpdaterGoldenTests.cs`
- Added test project: `tests/Seiton.Update.Tests/Seiton.Update.Tests.csproj`
- `fetch-webhooks-sources` downloads official source files into:
  - `data/sources/webhooks/github/raw/github-workflow.schema.json`
  - `data/sources/webhooks/github/raw/events-that-trigger-workflows.docs.md`
- `parse-webhooks-sources` parses local raw files and writes:
  - `data/sources/webhooks/github/parsed/schema-webhook-events.json`
  - `data/sources/webhooks/github/parsed/docs-webhook-events.json`
- `merge-webhooks-sources` merges parsed local artifacts and writes canonical snapshot:
  - `data/sources/webhooks/github/webhook_types.json`
- `fetch-webhooks` orchestrates the above 3 commands and then updates `data/sources/manifest.json` provenance.
- `fetch-webhooks` / `merge-webhooks-sources` default to include schema-only events for compatibility with preview/source lag; `--exclude-schema-only` can enforce docs-only event set.
- `sync webhooks` regenerates `src/Seiton.Core/Generated/WebhookTypes.g.cs` from normalized primary snapshot.
- `verify webhooks` checks staleness against normalized primary snapshot.
- `parity-webhooks` is an explicit actionlint differential command (separated from verify).
- actionlint parity diff is executed as a secondary, separate validation path when reference input exists.
- CI includes `Seiton.Update` verification via `verify --dataset all` in `.github/workflows/build.yaml`.
- Golden tests and CI verification run without requiring `.references` when vendored primary snapshot is present.

### Phase U3: Availability Updater

Status:
- Completed (docs source fetch/parse/merge pipeline, sync/verify path, and tests are in place)

Tasks:
- Implement parser for context availability source.
- Normalize by expression site granularity used by parser.
- Generate `Availability.g.cs`.
- Add regression tests for key contexts and special functions.

Exit criteria:
- Generated availability file matches parser semantic analyzer expectations.
- Existing expression tests pass unchanged.

Implementation notes (current):
- Added model: `src/Seiton.Update/Model/AvailabilityModel.cs`
- Added docs parser: `src/Seiton.Update/Parsers/GitHubDocsAvailabilityMarkdownParser.cs`
- Added source snapshot parser: `src/Seiton.Update/Parsers/GitHubAvailabilitySourceParser.cs`
- Added generator: `src/Seiton.Update/Generators/AvailabilityCSharpGenerator.cs`
- Added source resolver: `src/Seiton.Update/Services/AvailabilitySourcePathResolver.cs`
- Added sync/verify service: `src/Seiton.Update/Services/AvailabilitySyncService.cs`
- Added fetch source service: `src/Seiton.Update/Sources/GitHubAvailabilityFetcher.cs`
- Added command wiring: `src/Seiton.Update/Commands/AvailabilityCommands.cs`
- Added CLI commands:
  - `fetch-availability-sources`
  - `parse-availability-sources`
  - `merge-availability-sources`
  - `fetch-availability`
  - `sync-availability`
  - `verify-availability`
- Added dataset routing:
  - `sync --dataset availability`
  - `verify --dataset availability`
  - `sync --dataset all` now runs webhooks + availability
  - `verify --dataset all` now runs webhooks + availability
- Added primary availability source artifacts:
  - `data/sources/availability/github/raw/contexts.docs.md`
  - `data/sources/availability/github/parsed/docs-context-availability.json`
  - `data/sources/availability/github/availability.json`
- Added tests:
  - `tests/Seiton.Update.Tests/GitHubDocsAvailabilityMarkdownParserTests.cs`
  - `tests/Seiton.Update.Tests/AvailabilityPipelineStageTests.cs`
  - `tests/Seiton.Update.Tests/AvailabilityUpdaterGoldenTests.cs`

### Phase U4: Popular Actions Updater

Status:
- Completed (action metadata fetch/parse/merge pipeline, sync/verify path, and tests are in place)

Tasks:
- Implement source ingestion for popular actions metadata.
- Normalize action id/version + input/output schema shape.
- Generate `PopularActions.g.cs`.
- Add tests for representative actions and schema drift handling.

Exit criteria:
- `PopularActionInputsRule` tests remain green with regenerated data.

Implementation notes (current):
- Added model: `src/Seiton.Update/Model/PopularActionModel.cs`
- Added action metadata parser: `src/Seiton.Update/Parsers/GitHubActionMetadataYamlParser.cs`
- Added source snapshot parser: `src/Seiton.Update/Parsers/GitHubPopularActionsSourceParser.cs`
- Added generator: `src/Seiton.Update/Generators/PopularActionsCSharpGenerator.cs`
- Added source resolver: `src/Seiton.Update/Services/PopularActionsSourcePathResolver.cs`
- Added sync/verify service: `src/Seiton.Update/Services/PopularActionsSyncService.cs`
- Added fetch source service: `src/Seiton.Update/Sources/GitHubPopularActionsFetcher.cs`
- Added command wiring: `src/Seiton.Update/Commands/PopularActionsCommands.cs`
- Added CLI commands:
  - `fetch-popular-actions-sources`
  - `parse-popular-actions-sources`
  - `merge-popular-actions-sources`
  - `fetch-popular-actions`
  - `sync-popular-actions`
  - `verify-popular-actions`
- Added dataset routing:
  - `sync --dataset popular-actions`
  - `verify --dataset popular-actions`
  - `sync --dataset all` now runs webhooks + availability + popular-actions
  - `verify --dataset all` now runs webhooks + availability + popular-actions
- Added primary popular-actions source artifacts:
  - `data/sources/popular-actions/github/raw/*.action.yml`
  - `data/sources/popular-actions/github/parsed/popular-actions-metadata.json`
  - `data/sources/popular-actions/github/popular_actions.json`
- Added tests:
  - `tests/Seiton.Update.Tests/GitHubActionMetadataYamlParserTests.cs`
  - `tests/Seiton.Update.Tests/PopularActionsPipelineStageTests.cs`
  - `tests/Seiton.Update.Tests/PopularActionsUpdaterGoldenTests.cs`

### Phase U5: Verify Mode + CI

Status:
- Completed (verify gate is wired in PR CI and scheduled/manual update workflow is in place)

Tasks:
- Implement `verify` command to detect stale generated files.
- Add CI workflow job that runs `sync` then `verify` (or `git diff --exit-code`).
- Add scheduled/manual updater workflow to open update PRs.

Exit criteria:
- PR CI fails when generated files are outdated.
- Manual update workflow can regenerate and produce reviewable diff.

Implementation notes (current):
- `build` workflow verify gate now checks all implemented datasets:
  - `.github/workflows/build.yaml`
  - `dotnet run --project src/Seiton.Update -- validate-popular-actions-targets`
  - `dotnet run --project src/Seiton.Update -- verify --dataset all`
- Added scheduled/manual generated-data update workflow:
  - `.github/workflows/generated-data-update.yaml`
  - triggers:
    - weekly schedule (`0 3 * * 1`)
    - `workflow_dispatch`
  - runs:
    - `dotnet run --project src/Seiton.Update -- validate-popular-actions-targets`
    - `dotnet run --project src/Seiton.Update -- sync --dataset all`
    - `dotnet run --project src/Seiton.Update -- verify --dataset all`
  - creates/updates PR with generated diffs via `gh pr` CLI flow
  - uses GitHub App token (`actions/create-github-app-token`) with `contents:write` and `pull-requests:write`

### Phase U6: Documentation and Contract Finalization

Status:
- Completed (spec wording, runbook, and cross-plan references are aligned with implementation)

Tasks:
- Update Spec section 9 in parser and C# docs from planned wording to implemented wording.
- Record operational runbook in README or dedicated updater doc.
- Link this plan from parser implementation plan and mark complete.

Exit criteria:
- Section 9 wording and repository behavior are consistent.
- No references to non-existent updater remain.

Implementation notes (current):
- Updated parser spec section 9 wording to reflect implemented updater commands and manifest provenance fields.
  - `Docs/Seiton_Parser_spec.md`
- Updated C# implementation spec section 9 with multi-dataset CLI contract and data paths.
  - `Docs/Seiton_Parser_csharp_spec.md`
- Kept Go spec section 9 consistent with parser spec changes.
  - `Docs/Seiton_Parser_go_spec.md`
- Added updater runbook to repository README.
  - `README.md`
- Added explicit link from parser implementation plan to updater implementation plan.
  - `Docs/parser_implementation_csharp_plan.md`

### Phase U7: Popular Actions Target Configuration Externalization

Status:
- Completed (config externalization, contract validation command, schema file, and CI wiring are in place)

Tasks:
- Add repository-managed target config file: `data/sources/popular-actions/targets.json`.
- Replace hard-coded target list in updater source with config loading.
- Add contract validation for required fields and duplicate keys:
  - duplicate `uses` -> fail
  - duplicate `rawFileName` -> fail
  - missing required fields (`uses`, immutable source locator URL, `rawFileName`) -> fail
- Keep stage contract intact (`fetch-*`, `parse-*`, `merge-*`) while making target set data-driven.
- Add tests for valid-config path and invalid-config failure behavior.
- Add a repository JSON Schema for review-time/IDE validation:
  - `data/sources/popular-actions/targets.schema.json`
- Add dedicated CI validation step for target config:
  - `dotnet run --project src/Seiton.Update -- validate-popular-actions-targets`

Exit criteria:
- `fetch-popular-actions-sources` / `parse-popular-actions-sources` use `targets.json` as the source of truth.
- Editing `targets.json` changes parsed/merged outputs deterministically without code edits.
- CI verify remains green for existing target set and fails on invalid target config.
- `validate-popular-actions-targets` fails fast on malformed/invalid `targets.json` in CI and local runs.

Implementation notes (current):
- Added initial migration in `src/Seiton.Update/Sources/GitHubPopularActionsFetcher.cs`:
  - load targets from `data/sources/popular-actions/targets.json`
  - validate duplicates/missing required fields
  - preserve deterministic ordering by `uses`
- Added target-config validation command:
  - `validate-popular-actions-targets`
  - command handler: `src/Seiton.Update/Commands/PopularActionsCommands.cs`
  - registration: `src/Seiton.Update/Program.cs`
- Added JSON Schema contract for review/IDE validation:
  - `data/sources/popular-actions/targets.schema.json`
  - `data/sources/popular-actions/targets.json` references schema via `$schema`
- Added tests in `tests/Seiton.Update.Tests/PopularActionsPipelineStageTests.cs` for:
  - direct `ValidateTargetsConfig` success/failure cases
  - config-driven target selection
  - duplicate `uses` rejection
  - duplicate `rawFileName` rejection
  - missing required field rejection
- Added CI wiring for dedicated target-config validation command:
  - `.github/workflows/build.yaml`
  - `.github/workflows/generated-data-update.yaml`

## 8. Test Plan

Unit tests:
- parser for each upstream source format
- model validation and normalization
- code renderer deterministic ordering

Integration tests:
- full `sync` on fixture snapshots
- `verify` stale detection behavior

Safety tests:
- malformed source input should fail with actionable diagnostics
- unknown keys policy under `--strict`

Compatibility tests:
- run `dotnet test` for `Seiton.Core.Tests` after regeneration

## 9. Operational Workflow

Developer flow:
1. `dotnet run --project src/Seiton.Update -- validate-popular-actions-targets`
2. `dotnet run --project src/Seiton.Update -- sync`
3. `dotnet test`
4. review generated diffs
5. commit source snapshot + generated output + manifest updates

CI verification flow:
1. run updater in verify mode
2. fail if generated outputs differ

Scheduled update flow:
1. weekly workflow runs `sync`
2. if diff exists, open PR with generated changes and source manifest updates

## 10. Risks and Mitigations

Risk: upstream source layout changes break parser.
- Mitigation: resilient parser + fixture tests + strict mode diagnostics.

Risk: non-deterministic generation causes noisy diffs.
- Mitigation: stable sort + canonical formatter + golden tests.

Risk: network instability during scheduled updates.
- Mitigation: retry policy and offline snapshot fallback; do not block normal CI builds.

Risk: spec drift between docs and tool behavior.
- Mitigation: update checklist requiring spec section 9 review whenever updater changes.

## 11. Completion Checklist

- [x] `src/Seiton.Update` exists and is wired in solution build
- [x] `sync` regenerates all three generated files
- [x] `verify` detects stale generated files
- [x] updater unit/integration tests exist and pass
- [x] `dotnet build` and `dotnet test` pass after regeneration
- [x] CI has generated-data verification gate
- [x] parser spec and C# spec section 9 describe implemented behavior
- [x] parser implementation plan references updater completion
- [x] popular-actions targets are config-driven and validated (`targets.json` + schema + CI validation command)
- [x] CLI contract section reflects implemented command set (no stale planned-only command entries)
