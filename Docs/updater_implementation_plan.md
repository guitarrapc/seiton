# Generated Data Updater Implementation Plan

> Goal: implement a deterministic updater pipeline for generated parser/rule metadata (`WebhookTypes.g.cs`, `Availability.g.cs`, `PopularActions.g.cs`) so Spec §9 update policy is fully implemented, not just documented.

## 1. Problem Statement

Current state:
- Generated data files exist in `src/Seiton.Core/Generated/`.
- Specs (`Seiton_Parser_spec.md` and `Seiton_Parser_csharp_spec.md`) describe an update command (`Seiton.Update` or script).
- No updater project currently exists under `src/`.

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
- `src/Seiton.Update/UpdaterCommands.cs`
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

## 4. CLI Contract (Proposed)

Implementation note:
- Command and argument binding is implemented with `ConsoleAppFramework` so command methods can focus on updater logic instead of manual argument parsing.

Primary command:
- `dotnet run --project src/Seiton.Update -- sync`

Subcommands:
- `sync --dataset webhooks`
- `sync --dataset availability`
- `sync --dataset popular-actions`
- `verify` (fails when generated outputs are stale)
- `dump-sources` (optional diagnostics)
- convenience aliases: `sync-webhooks`, `verify-webhooks`

Common options:
- `--offline` use vendored snapshots only
- `--input-dir <path>` override source fixture directory
- `--output-dir <path>` override generated output root
- `--strict` fail on unknown schema shape

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
- `data/sources/webhooks/*`
- `data/sources/availability/*`
- `data/sources/popular-actions/*`

Provenance metadata file:
- `data/sources/manifest.json`
  - source URL
  - fetched timestamp (UTC)
  - parser version
  - content hash (sha256)

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
- `Program.cs` registers `sync`, `verify`, and convenience aliases `sync-webhooks`, `verify-webhooks`.
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
- Kept reference parser for parity: `src/Seiton.Update/Parsers/ActionlintWebhookSourceParser.cs`
- Added sync/verify service: `src/Seiton.Update/Services/WebhookSyncService.cs`
- Added source resolver: `src/Seiton.Update/Services/WebhookSourcePathResolver.cs`
  - primary generation source: `data/sources/webhooks/github/webhook_types.json`
  - actionlint parity reference: `data/sources/webhooks/actionlint/all_webhooks.go` or `.references/actionlint/all_webhooks.go`
- Added primary vendored snapshot: `data/sources/webhooks/github/webhook_types.json`
- Added tests: `tests/Seiton.Update.Tests/WebhookUpdaterGoldenTests.cs`
- Added test project: `tests/Seiton.Update.Tests/Seiton.Update.Tests.csproj`
- `sync webhooks` regenerates `src/Seiton.Core/Generated/WebhookTypes.g.cs` from GitHub primary snapshot.
- `verify webhooks` checks staleness against GitHub primary snapshot first.
- actionlint parity diff is executed as a secondary, separate validation path when reference input exists.
- CI includes `Seiton.Update` verification via `verify-webhooks` in `.github/workflows/build.yaml`.
- Golden tests and CI verification run without requiring `.references` when vendored primary snapshot is present.

### Phase U3: Availability Updater

Tasks:
- Implement parser for context availability source.
- Normalize by expression site granularity used by parser.
- Generate `Availability.g.cs`.
- Add regression tests for key contexts and special functions.

Exit criteria:
- Generated availability file matches parser semantic analyzer expectations.
- Existing expression tests pass unchanged.

### Phase U4: Popular Actions Updater

Tasks:
- Implement source ingestion for popular actions metadata.
- Normalize action id/version + input/output schema shape.
- Generate `PopularActions.g.cs`.
- Add tests for representative actions and schema drift handling.

Exit criteria:
- `PopularActionInputsRule` tests remain green with regenerated data.

### Phase U5: Verify Mode + CI

Tasks:
- Implement `verify` command to detect stale generated files.
- Add CI workflow job that runs `sync` then `verify` (or `git diff --exit-code`).
- Add scheduled/manual updater workflow to open update PRs.

Exit criteria:
- PR CI fails when generated files are outdated.
- Manual update workflow can regenerate and produce reviewable diff.

### Phase U6: Documentation and Contract Finalization

Tasks:
- Update Spec section 9 in parser and C# docs from planned wording to implemented wording.
- Record operational runbook in README or dedicated updater doc.
- Link this plan from parser implementation plan and mark complete.

Exit criteria:
- Section 9 wording and repository behavior are consistent.
- No references to non-existent updater remain.

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
1. `dotnet run --project src/Seiton.Update -- sync`
2. `dotnet test`
3. review generated diffs
4. commit source snapshot + generated output + manifest updates

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

- [ ] `src/Seiton.Update` exists and is wired in solution build
- [ ] `sync` regenerates all three generated files
- [ ] `verify` detects stale generated files
- [ ] updater unit/integration tests exist and pass
- [ ] `dotnet build` and `dotnet test` pass after regeneration
- [ ] CI has generated-data verification gate
- [ ] parser spec and C# spec section 9 describe implemented behavior
- [ ] parser implementation plan references updater completion
