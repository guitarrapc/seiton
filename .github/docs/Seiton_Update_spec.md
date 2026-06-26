# Seiton Update Specification

> Defines the specification for Seiton.Update, the maintainer-facing data update pipeline tool.
> This document consolidates the generated-data pipeline contract previously scattered across `Seiton_Parser_spec.md` §9, `Seiton_Parser_csharp_spec.md` §9, `Seiton_Parser_go_spec.md` §9, and `architecture_spec_csharp.md` §4.5/§9.
> Parser and linter behavior are specified in their respective spec documents.

---

## 1. Purpose and Scope

### 1.1 What Seiton.Update Does

Seiton.Update is a maintainer-only CLI tool that fetches, parses, merges, and code-generates metadata consumed by `Seiton.Core` at compile time. It is **not** user-facing.

Responsibilities:

- Fetch raw source data from official GitHub sources (network access)
- Parse raw files into normalized intermediate JSON (local, deterministic)
- Merge parsed artifacts into canonical snapshot JSON (local, deterministic)
- Generate `.g.cs` files under `src/Seiton.Core/Generated/` from snapshot/source JSON
- Verify that generated `.g.cs` files are up to date (CI gate)
- Validate source data consistency where applicable (optional cross-checks)
- Maintain provenance metadata (`data/sources/manifest.json`)

### 1.2 What Seiton.Update Does NOT Do

- Expose user-facing lint, parse, or config commands
- Make network requests at parser/linter runtime
- Implement any lint or parse logic

### 1.3 Design Principles

1. **Explicit update**: Generated data is produced by explicit CLI invocation, not by build-time source generators. This keeps fetch/generate diffs reviewable in PRs.
2. **Official sources are normative**: GitHub Docs and official GitHub metadata define Seiton's data contract. Reference implementations (e.g., actionlint) are used for differential validation only.
3. **Deterministic pipeline**: Given identical raw inputs, stages 2 and 3 must produce identical outputs.
4. **Stage independence**: Each pipeline stage can be invoked independently for incremental updates.
5. **Git-tracked artifacts**: All stage outputs (raw files, parsed JSON, snapshots, reports) are committed to the repository for auditability and independent review.

---

## 2. Project Structure

```
src/Seiton.Update/
  Program.cs              — CLI entrypoint (ConsoleAppFramework)
  Commands/               — Per-dataset command static methods
  Generators/             — StringBuilder-based .g.cs code generators
  Model/                  — Data models for parsed/merged artifacts
  Parsers/                — Markdown and source file parsers
  Services/               — Sync/verify services and path resolvers
  Sources/                — HTTP fetchers per data source
  Validators/             — Cross-check validators
  TextNormalization.cs    — Shared text normalization utilities
  UpdateLogger.cs         — Logging utilities
```

---

## 3. Pipeline Architecture

### 3.1 Three-Stage Pipeline

Each generated dataset follows a deterministic 3-stage pipeline:

```
Stage 1: Fetch Raw Sources     (network)
  ↓
Stage 2: Parse Local Sources   (local, deterministic)
  ↓
Stage 3: Merge Parsed Artifacts (local, deterministic)
  ↓
Codegen: Sync .g.cs            (local, deterministic)
```

Hand-authored datasets and **collapsed** parse layouts skip some boxes; derived **reports** are off this diagram entirely. See §3.4.

#### 3.1.1 Stage 1 — Fetch Raw Sources

Download official source files verbatim and persist them locally.

- Input: Remote URLs for each official source
- Output: Raw files in `data/sources/{dataset}/{provider}/raw/`
- Network access: **yes** (only stage that accesses the network)
- All downloaded files are committed to the repository for auditability
- Provenance metadata (URLs, timestamps, file hashes) is recorded in `data/sources/manifest.json`

#### 3.1.2 Stage 2 — Parse Local Source Files

Parse raw bytes into structured JSON (the **Stage 2 product**). On disk this usually lives under `data/sources/{dataset}/{provider}/parsed/`, but **collapsed** pipelines write the same logical artifact directly to the canonical snapshot path (§3.4).

- Input: Raw files from Stage 1 (where the dataset uses fetch)
- Output: One or more JSON files containing the extracted model **and** recommended raw-linkage metadata (§3.3)
- Network access: **no**
- Parsing must be deterministic given the same raw inputs
- If a Stage 2 artifact includes an HTTPS **`sourceUrl`**, it MUST match the manifest-backed URL configuration used for Stage 1 for that dataset (see §7).

#### 3.1.3 Stage 3 — Merge Parsed Artifacts

Apply conflict resolution policy across all parsed artifacts to produce one canonical snapshot.

- Input: Parsed JSON files from Stage 2, plus optional **supplemental JSON** (hand-written sections not derivable from raw sources)
- Output:
  - Canonical snapshot: `data/sources/{dataset}/{provider}/{snapshot}.json`
  - Official-source diff report: `data/sources/reports/official-{dataset}-source-diff.md` (when applicable)
- Network access: **no**

**Supplemental merge pattern**: Some datasets need key sets or entries that are not present in the raw source files parsed in Stage 2 (for example, action metadata keys are not in `workflow-syntax.md`). These are maintained as hand-written `supplemental-*.json` files alongside the canonical snapshot. Stage 3 reads the supplemental file, merges its entries into the parsed model (deduplicating by name, sorting alphabetically), and writes the combined result as the canonical snapshot. This keeps Stage 2 pure (derived only from raw) while allowing the canonical snapshot to include repository-managed additions. Current datasets using this pattern: `runner-labels` (`supplemental-labels.json`), `expected-keys` (`supplemental-keys.json`), `step-schema` (`supplemental-step-schema.json`), `popular-actions` (`supplemental-required-permissions.json`).

**Curated policy merge pattern (`runner-labels`)**: `deprecated-labels.json` is a hand-written file merged in Stage 3 alongside supplemental labels. It lists hosted runner labels under GitHub deprecation policy but still documented as available. The canonical snapshot exposes `deprecatedLabels` separately from `stableLabels` / `previewLabels` so generated `RunnerLabels.IsDeprecatedHostedLabel` can drive deprecation lint without removing labels from the known set.

#### 3.1.4 Codegen — Sync .g.cs

Generate `.g.cs` files from canonical snapshot or hand-written source JSON.

- Input: Canonical snapshot JSON (or hand-written JSON for datasets without merge)
- Output: `.g.cs` files in `src/Seiton.Core/Generated/`
- Generated files start with `// <auto-generated>` and state the regeneration command

### 3.2 Artifact layers: manifest, raw, Stage 2 output, canonical snapshot

This repository uses four conceptual layers. They are consistent across datasets; only the **on-disk layout** varies by pipeline profile (§3.4).

| Layer | Role | Typical location |
|---|---|---|
| **Manifest** | Canonical record of Stage 1 **fetch URLs**, **timestamp**, and **`sha256` of each committed raw file** (by file name). | `data/sources/manifest.json` |
| **Raw** | Verbatim (after repo newline normalization where applicable) downloaded bytes from those URLs. | `data/sources/{dataset}/{provider}/raw/...` |
| **Stage 2 JSON** | Deterministic parse of raw **plus metadata** tying the parse to originating raw file names and byte identity (§3.3). May live under `parsed/` or, when collapsed, in the canonical snapshot file only. | `.../parsed/*.json` and/or `{snapshot}.json` |
| **Canonical snapshot** | Input to merge (if any) and to **codegen** / verify. Often the merged Stage 3 output; for composite or hand-authored datasets, may combine maintainer-owned JSON with fetched inputs. | e.g. `context-types.json`, `function-specs.json` |

**Reports** under `data/sources/reports/` are human-readable artifacts from comparison or merge policies. They are **not** codegen inputs and do **not** have manifest entries.

### 3.3 Stage 2 metadata (raw linkage)

Stage 2 exists to answer: *what structured data did we extract, and **which raw files and revisions** produced it?*

**Normative**

- Stage 2 JSON MUST contain the **parsed model** derived only from raw bytes (for fetched datasets) and deterministic rules, or the maintainer-authored payload for hand-written snapshots.
- When a Stage 2 artifact includes an HTTPS **`sourceUrl`**, it MUST match the manifest-backed URL configuration used for Stage 1 for that dataset (same rule as §3.1.2).

**Recommended (convergence)**

- Add a documented **`schemaVersion`** (integer) and stable logical **`source`** string (for example `iana-tzdb-tzdata-zi`) when consumers can tolerate the schema change.
- Identify contributing raw files with the same **base names** as keys in `manifest.json` → `rawFileHashes` for that dataset.
- Echo each file's **`sha256:`** digest **matching** the manifest entry for the committed raw bytes (recomputing from working-tree raw is equivalent when the file matches the manifest).
- Prefer a **`rawSources`** array of objects `{ "fileName": "<base-name>", "sha256": "sha256:..." }` when multiple raw files feed one artifact; a single raw may use `rawFileName` / `rawSha256` or equivalent explicit fields.
- Include **domain-specific** upstream labels when the source provides them (for example IANA **tzdb `version`** parsed from `tzdata.zi`).

**Non-normative note:** Committed JSON may predate this metadata. Older snapshots may omit `schemaVersion` / `rawSources`; current **`shells`** and **`expected-keys`** Stage 2 artifacts carry `schemaVersion`, `source`, and `rawSources` alongside their payload fields.

### 3.4 Pipeline profiles

Profiles explain datasets that do not use a literal `parsed/` directory or full three-stage flow. They are **not** ad-hoc exceptions: the layers in §3.2 still apply logically.

| Profile | Stage 2 on disk | Canonical / codegen input | Examples (current) |
|---|---|---|---|
| **Standard** | `raw/` → `parsed/` → merge → `{snapshot}.json` | Merged snapshot | `availability`, `context-types`, `expected-keys`, `permissions`, `runner-labels`, `shells`, `webhooks`, `popular-actions`, `iana-timezones` |
| **Collapsed Stage 2** | `raw/` → **`{snapshot}.json` directly** (no `parsed/` subtree) | Same file | *(none — prefer Standard + `parsed/`)*; reserved for narrow exceptions |
| **Composite primary** | Maintained canonical JSON **plus** fetched raw and optional `parsed/` supplements | Hand-written base merged or validated against parses | `function-specs` (`function-specs.json` primary; `parsed/docs-function-names.json` from Docs) |
| **Hand-authored snapshot** | No automated fetch | Maintainers edit JSON; optional `schemaVersion` / `source` for consistency | `bot-actors`, `unpinned-tools` |
| **Satellite manifest dataset** | Own manifest `dataset` key; files may live under another tree | Snapshot path defined by that tool | `event-payload-types` (manifest + raw/parsed under `webhooks/github/...`; codegen reads `event_payload_types.json`) |
| **Reports** | — | — | `data/sources/reports/*.md` (diff / parity narrative only) |

### 3.5 Stage Independence

Each stage may be invoked independently:

- Stage 1 may be re-run to refresh raw source files
- Stage 2 re-parses existing raw files without network access
- Stage 3 re-merges using existing parsed artifacts without network access
- An orchestrator command runs stages 1–3 in sequence

### 3.6 Storage Path Convention

```
data/sources/{dataset}/{provider}/raw/                  ← Stage 1: raw downloaded source files
data/sources/{dataset}/{provider}/parsed/               ← Stage 2: per-source parsed JSON (if not collapsed)
data/sources/{dataset}/{provider}/supplemental-*.json   ← Hand-written entries merged in Stage 3 (optional)
data/sources/{dataset}/{provider}/{name}.json           ← Stage 3 (or collapsed Stage 2): canonical snapshot
data/sources/reports/                                   ← diff and parity reports (not manifest-backed)
data/sources/manifest.json                              ← fetch provenance for datasets with Stage 1
```

Not every path appears for every dataset. Some omit `parsed/` (collapsed), some omit `raw/` (hand-authored). See §4.0.

---

## 4. Datasets

### 4.0 Pipeline layout reference

Cross-walk of maintainer-facing datasets (including satellite **`event-payload-types`**) and **§3.4** profiles. **Reports** are not a named `dataset` in `manifest.json`; they document merge/parity outcomes.

| Dataset / artifact | Manifest `dataset` key | Profile (§3.4) | Raw (Stage 1) | Stage 2 JSON | Canonical / codegen snapshot |
|---|---|---|---|---|---|
| availability | `availability` | Standard | `.../raw/contexts.docs.md` | `.../parsed/docs-context-availability.json` | `availability.json` |
| context-types | `context-types` | Standard | `.../raw/contexts.docs.md` | `.../parsed/docs-contexts.json` | `context-types.json` (+ hand-maintained override JSON merged in Stage 3) |
| expected-keys | `expected-keys` | Standard | `.../raw/workflow-syntax.md` | `.../parsed/expected-keys.json` | `expected-keys.json` (parsed + supplemental merge) |
| function-specs | `function-specs` | Composite primary | `.../raw/expressions.docs.md` | `.../parsed/docs-function-names.json` | `function-specs.json` (hand-maintained base) |
| iana-timezones | `iana-timezones` | Standard | `.../raw/tzdata.zi` | `.../parsed/iana-timezone-ids.json` | `iana_timezones.json` |
| permissions | `permissions` | Standard | `.../raw/github-token-available-permissions.md` | `.../parsed/permissions-scopes.json` | `permissions.json` |
| popular-actions | `popular-actions` | Standard | `.../raw/*.action.yml` (from `targets.json`) | `.../parsed/popular-actions-metadata.json` | `popular_actions.json` |
| runner-labels | `runner-labels` | Standard | two `*.docs.md` under `raw/` | `.../parsed/docs-runner-labels.json` | `runner_labels.json` (+ optional `supplemental-labels.json`, `deprecated-labels.json`) |
| shells | `shells` | Standard (passthrough merge) | `.../raw/supported-shells.md` | `.../parsed/shells.json` | `shells.json` (copy of parsed) |
| step-schema | `step-schema` | Standard | `.../raw/github-workflow.schema.json` + `workflow-syntax.md` | `.../parsed/step-schema.json` | `step-schema.json` (parsed + supplemental merge) |
| bot-actors | — | Hand-authored snapshot | — | — | `bot-actors.json` |
| webhooks | `webhooks` | Standard | schema JSON + Docs `*.md` | multiple under `parsed/` | `webhook_types.json` |
| event-payload-types | `event-payload-types` | Satellite | `webhooks/github/raw/webhook-events-and-payloads.html` | `webhooks/github/parsed/parsed-event-payload-types.json` | `webhooks/github/event_payload_types.json` |
| unpinned-tools | — | Hand-authored (sync/verify only) | — | — | `unpinned_tools.json` |
| reports | — | Reports | — | — | `data/sources/reports/*.md` |

`context-types` and `function-specs` combine fetched material with **repository-managed** JSON; the table shows the main codegen inputs.

### 4.1 Dataset Inventory

| Dataset | Source | Generated File | Description |
|---|---|---|---|
| webhooks | GitHub Docs | `WebhookTypes.g.cs` | Webhook event names, activity types, and filter options |
| availability | GitHub Docs | `Availability.g.cs` | Expression context and special function availability per workflow position |
| popular-actions | Fetched `action.yml` files | `PopularActions.g.cs` | Well-known GitHub Actions with expected input names, output names, and `runs.using` runtime |
| runner-labels | GitHub Docs | `RunnerLabels.g.cs` | Known GitHub-hosted runner labels |
| context-types | Hand-written JSON + GitHub Docs | `ContextTypes.g.cs` | Built-in context type schemas for all 11 context roots |
| function-specs | Hand-written JSON + GitHub Docs | `FunctionSpecs.g.cs` | Built-in function specs with parameter types and overloads |
| permissions | GitHub Docs | `PermissionScopes.g.cs` | GitHub token permission scope metadata |
| iana-timezones | IANA `tzdata.zi` | `IanaTimeZones.g.cs` | IANA timezone identifiers (zones + links) for schedule-event timezone validation |
| event-payload-types | GitHub Docs (HTML) | `EventPayloadTypes.g.cs` | Webhook event payload type shapes for expression typing |
| shells | GitHub Docs reusable (`supported-shells.md`); table included from workflow-syntax `defaults.run.shell` | `Shells.g.cs` | Shell availability per OS platform for `shell-name` rule validation |
| step-schema | json.schemastore `github-workflow.json` (`definitions.step`) + supplemental overlay | `StepSchema.g.cs` | Step form allowed keys and value kinds for parser diagnostics (and future parse branches) |
| bot-actors | Hand-written GitHub API provenance JSON | `BotActors.g.cs` | Known bot actor logins and user IDs for provenance tracking and future audit consumers |
| expected-keys | GitHub Docs | `ExpectedKeys.g.cs` | Expected YAML key lists per parser section for diagnostic messages |

### 4.2 Source of Truth Policy

- Official GitHub sources (GitHub Docs, official metadata endpoints) are normative for generated data.
- `.references/actionlint/**` and actionlint data are non-normative reference inputs used for differential validation only.
- If official GitHub sources and actionlint differ, Seiton-generated outputs follow official GitHub sources, and the actionlint difference is reported as parity information.
- Reference parity must never silently override the contract defined by official GitHub sources.

### 4.3 Dataset-Specific Notes

#### 4.3.1 Webhook Activity Type Conflict Resolution

When official GitHub sources disagree for webhook activity types:

- GitHub Docs values are preferred when the Docs event table is parseable for the event.
- Docs cells that mix static activity types and Liquid version-condition blocks are treated as parseable for the static subset; static backtick values are retained and Liquid-only conditional values are ignored.
- SchemaStore metadata is used as fallback for events where Docs values are unavailable/unparseable.
- Official-source mismatches are recorded in a dedicated official-source diff report.
- actionlint parity is a separate differential check and never overrides official-source resolution.

#### 4.3.2 Context Types

`context-types` follows the **standard** profile (§3.4) with an additional repository-managed override file merged in Stage 3: GitHub Docs `contexts.md` is parsed into intermediate JSON and merged into `context-types.json` together with the override; the merged file is the source of truth for codegen.

**Orchestrator:** `fetch-context-types` (and the `context-types` step inside `fetch --dataset all` / `update`) runs Stage 1–2 **and** `merge-context-types-sources` so `context-types.json` stays aligned with the latest Docs parse before the manifest is saved.

#### 4.3.3 Function Specs

`function-specs` follows the **composite primary** profile (§3.4): codegen reads a hand-maintained `function-specs.json` while Stage 1–2 ingest Docs `expressions.md` into `parsed/docs-function-names.json` for gap detection and validation. No separate merge stage exists; `fetch-function-specs` updates manifest and intermediate artifacts used by `validate-function-specs`.

#### 4.3.4 Popular Actions Target Configuration

The set of popular actions to ingest is a repository-managed configuration, not a hard-coded list.

- Target-set file: `data/sources/popular-actions/targets.json`
- Each entry must provide: canonical `uses` name, `actionRef` (`owner/repo@tag-or-branch` matching the manifest raw URL ref segment), and raw artifact file name
- Immutable download URLs for each target live in `data/sources/manifest.json` (dataset `popular-actions`): one URL per target, ordered to match targets sorted by `uses` (ascending)
- Duplicate `uses` entries or duplicate raw artifact file names are invalid and must fail updater execution
- Entries with missing required identity fields are invalid and must fail updater execution
- Target-set modifications and resulting generated diffs must be reviewed together in one change set

#### 4.3.5 Popular Actions Pipeline Data Fields

The popular-actions pipeline extracts the following metadata from each fetched `action.yml` / `action.yaml`:

| Field | Source | Description |
|---|---|---|
| `uses` | `targets.json` | Canonical `owner/repo` identifier (without version ref) |
| `inputs` | `action.yml` `inputs:` section | Input names and `required` flags |
| `outputs` | `action.yml` `outputs:` section | Output names |
| `runsUsing` | `action.yml` `runs.using` value | Runtime identifier (e.g. `node20`, `composite`, `docker`) |
| `requiredPermissions` | `supplemental-required-permissions.json` | Required GitHub token permission scopes (e.g. `contents: read`). Hand-written; merged during Stage 3 |

The Stage 2 parser (`GitHubActionMetadataYamlParser`) extracts `inputs`, `outputs`, and `runsUsing` independently from raw `action.yml` files using line-based indent-aware parsing.

The Stage 3 merge normalizes and deduplicates all fields into the canonical snapshot (`popular_actions.json`).

The codegen stage (`PopularActionsCSharpGenerator`) produces:

- `IsInputAllowed(name)` — case-insensitive input name lookup
- `GetOutputNames()` — returns `byte[][]` of known output names
- `GetRunsUsing()` — returns `ReadOnlySpan<byte>` of the `runs.using` value
- `GetRequiredPermissions()` — returns `(string Scope, string Access)[]` of required permission scopes

These generated methods are consumed by linter rules (`popular-action-inputs`, `outdated-action-runner`, `expr-undefined-var`) at compile time, with no runtime network access.

#### 4.3.6 Shells

`shells` follows the **standard** raw → **`parsed/shells.json`** → **`shells.json`** layout (§3.4), matching **`expected-keys`** (passthrough merge).

The workflow-syntax page renders the supported-shell table from the GitHub Docs **reusable** [`data/reusables/actions/supported-shells.md`](https://github.com/github/docs/blob/main/data/reusables/actions/supported-shells.md) (referenced from [`defaults.run.shell`](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax.md#defaultsrunshell) via Liquid). Stage 1 fetches that file as `supported-shells.md` under `raw/`.

The Stage 2 parser (`GitHubDocsSupportedShellsMarkdownParser`) reads the markdown table (`Supported platform`, `` `shell` parameter ``, *Command run internally*), skips the `unspecified` pseudo-shell row, normalizes platforms (`All` / `Linux / macOS` / `Windows`), merges duplicate shell names (for example `pwsh` on “All” and on Windows), and emits `name`, `platforms`, and `command` for each built-in shell.

Orchestrator: `fetch-shells`; stages: `fetch-shells-sources`, `parse-shells-sources`, `merge-shells-sources`; codegen: `sync-shells`, `verify-shells`.

The codegen stage (`ShellsCSharpGenerator`) produces:

- `IsValidShell(shellUtf8)` — checks if a shell name is any known built-in shell
- `IsAvailableOnLinux(shellUtf8)` — checks availability on Linux runners
- `IsAvailableOnMacOS(shellUtf8)` — checks availability on macOS runners
- `IsAvailableOnWindows(shellUtf8)` — checks availability on Windows runners
- `AllValidShellNames` — comma-separated string for diagnostic messages

All methods use `ReadOnlySpan<byte>` comparisons for zero-allocation hot-path usage. These are consumed by the `shell-name` linter rule.

#### 4.3.7 Step Schema

`step-schema` is **independent of the webhooks dataset** but fetches the same schemastore URL into its own `raw/github-workflow.schema.json`. Stage 2 (`GitHubWorkflowStepSchemaParser`) extracts `definitions.step` only — forms from `oneOf`, property value kinds, and `dependencies` — into `parsed/step-schema.json` with **no supplemental content**. The Stage 2 `rawSources` metadata lists **only** `github-workflow.schema.json` (the file actually parsed); `workflow-syntax.md` is fetched alongside for optional cross-check but is not part of the parsed artifact. Stage 3 merges `supplemental-step-schema.json` (modifiers such as `background`, additional parallel step forms when schemastore lags, per-form `disallowedKeys`) into canonical `step-schema.json`, and records **both** raw files in canonical `rawSources`.

Orchestrator: `fetch-step-schema`; stages: `fetch-step-schema-sources`, `parse-step-schema-sources`, `merge-step-schema-sources`; codegen: `sync-step-schema`, `verify-step-schema`.

`StepSchema.g.cs` emits per-form allowed-key constants (`RunStepKeys`, `UsesStepKeys`, `WaitStepKeys`, …), `FormId`, `GetUnexpectedKeyDescription`, and `IsModifierAllowed(formId, keyUtf8)` from snapshot modifiers. Step-form keys were **removed** from `ExpectedKeys.g.cs` (no more `action-step` / `run-step` derivation in `expected-keys`).

#### 4.3.8 Expected Keys

`expected-keys` follows the **standard** raw → **`parsed/expected-keys.json`** → **`expected-keys.json`** layout (§3.4): Stage 2 writes the parsed hierarchy; Stage 3 (`merge-expected-keys-sources`) deserializes the parsed JSON, merges hand-written supplemental sections from `supplemental-keys.json` (§3.1.3), sorts all sections alphabetically, and writes the combined result as `expected-keys.json`.

`expected-keys` fetches `workflow-syntax.md` from GitHub Docs and builds a complete parent→child key hierarchy by parsing all `## \`...\`` headings. The algorithm:

1. Extract all `## \`path\`` headings from the raw markdown.
2. Split each heading into dot-separated segments, preserving angle-bracket and square-bracket contents.
3. Expand pipe-separated alternatives in angle brackets (e.g. `<branches|branches-ignore>`) into the cartesian product of all combinations.
4. For each expanded path, register every concrete (non-parameter) segment as a child of its parent path. Single-parameter wildcards like `<job_id>` are skipped; `[*]` array subscripts are stripped from child key names.
5. Emit named sections for all parents that have concrete children, using a known-path→name mapping with algorithmic fallback for unknown paths.
6. Supplement sections whose sub-keys are documented in body text rather than as headings (`credentials`: `password`/`username`; `runs-on`: `group`/`labels`).

Stage 3 merges supplemental sections (for example `action-metadata`: keys for action.yml top-level that are not in workflow-syntax.md) into the parsed output. The canonical file has ~35 sections covering:

- Top-level workflow keys, `on` event names, per-event filter keys (`on.push`, `on.pull_request`, etc.)
- `on.workflow_call`/`on.workflow_dispatch` sub-keys and input/secret sub-keys
- Job-level keys, job defaults, strategy, strategy matrix
- Step keys (full union for the `step` section), step-with
- Container, service, credentials, runs-on

The codegen stage (`ExpectedKeysCSharpGenerator`) produces `const string` fields with quoted, sorted key names for each section. These are consumed by the parser for diagnostic messages when encountering unexpected keys.

Additionally, the `job` section emits `JobMappingKey` enum, `JobMappingKeyTable` (`IUtf8OrderedKeyTable`), and `IsKnownJobKey(ReadOnlySpan<byte>)` for parser UTF-8 dispatch — same stage-A pattern as `StepSchema.MappingKeyTable`.

---

## 5. CLI Commands

### 5.1 Command Naming Convention

Per-dataset commands follow this naming pattern:

| Pattern | Purpose |
|---|---|
| `fetch-{dataset}` | Orchestrator: fetch + parse + manifest update |
| `fetch-{dataset}-sources` | Stage 1: download raw source files |
| `parse-{dataset}-sources` | Stage 2: parse raw files into intermediate JSON |
| `merge-{dataset}-sources` | Stage 3: merge parsed artifacts into snapshot |
| `sync-{dataset}` | Generate `.g.cs` from snapshot/source JSON |
| `verify-{dataset}` | Check `.g.cs` is up to date (CI gate) |
| `validate-{dataset}` | Cross-check source data against docs (optional) |
| `parity-{dataset}` | Differential check against actionlint reference (optional) |

### 5.2 Per-Dataset Command Matrix

| Dataset | fetch | fetch-sources | parse-sources | merge-sources | sync | verify | validate | parity |
|---|---|---|---|---|---|---|---|---|
| webhooks | `fetch-webhooks [--exclude-schema-only]` | ✓ | ✓ | `merge-webhooks-sources [--exclude-schema-only]` | ✓ | ✓ | — | `parity-webhooks` |
| availability | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| popular-actions | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | `validate-popular-actions-targets` | — |
| runner-labels | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| context-types | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | `validate-context-types` | — |
| function-specs | ✓ | ✓ | ✓ | — | ✓ | ✓ | `validate-function-specs` | — |
| permissions | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| iana-timezones | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| shells | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| step-schema | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| bot-actors | — | — | — | — | ✓ | ✓ | — | — |
| expected-keys | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| event-payload-types | ✓ | ✓ | ✓ | — | ✓ | ✓ | — | — |

`sync-function-specs` automatically runs `validate-function-specs` when parsed data is available.

### 5.3 Aggregate Commands

| Command | Description |
|---|---|
| `fetch --dataset {name\|all}` | Run the `fetch-{dataset}` orchestrator (Stage 1–3 + manifest for that dataset) for one dataset or **all** in the same order as `sync`/`verify` |
| `fetch --dataset all --exclude-schema-only` | Same as full fetch, but the **webhooks** step uses schema-excluded mode (same flag as `fetch-webhooks`) |
| `sync --dataset {name\|all}` | Run sync for specified dataset or all datasets |
| `verify --dataset {name\|all}` | Run verify for specified dataset or all datasets |
| `update` | Run **`fetch --dataset all`**, then **`sync --dataset all`**, then **`verify --dataset all`**; optional `--exclude-schema-only` applies to the webhooks fetch step |

`fetch --dataset all` processes fetched datasets in a fixed internal order: webhooks → availability → popular-actions → runner-labels → context-types → function-specs → permissions → iana-timezones → shells → step-schema → expected-keys → event-payload-types. `sync --dataset all` / `verify --dataset all` run the same sequence and additionally include the hand-authored `bot-actors` dataset before `event-payload-types`.

### 5.4 Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | General failure / unsupported dataset |
| `4` | Parity check detected differences |

---

## 6. Data Paths

```
data/sources/webhooks/github/raw/*
data/sources/webhooks/github/parsed/*
data/sources/webhooks/github/webhook_types.json
data/sources/webhooks/github/event_payload_types.json

data/sources/availability/github/raw/*
data/sources/availability/github/parsed/*
data/sources/availability/github/availability.json

data/sources/popular-actions/github/raw/*.action.yml
data/sources/popular-actions/github/parsed/*
data/sources/popular-actions/github/popular_actions.json
data/sources/popular-actions/supplemental-required-permissions.json
data/sources/popular-actions/targets.json

data/sources/runner-labels/github/raw/*
data/sources/runner-labels/github/parsed/*
data/sources/runner-labels/github/supplemental-labels.json
data/sources/runner-labels/github/deprecated-labels.json
data/sources/runner-labels/github/runner_labels.json

data/sources/context-types/github/raw/*
data/sources/context-types/github/parsed/*
data/sources/context-types/github/context-types.json

data/sources/function-specs/github/raw/
data/sources/function-specs/github/parsed/
data/sources/function-specs/github/function-specs.json

data/sources/permissions/github/raw/*
data/sources/permissions/github/parsed/*
data/sources/permissions/github/permissions.json

data/sources/iana-timezones/iana/raw/tzdata.zi
data/sources/iana-timezones/iana/parsed/*
data/sources/iana-timezones/iana/iana_timezones.json

data/sources/shells/github/raw/supported-shells.md
data/sources/shells/github/parsed/shells.json
data/sources/shells/github/shells.json

data/sources/step-schema/github/raw/github-workflow.schema.json
data/sources/step-schema/github/raw/workflow-syntax.md
data/sources/step-schema/github/parsed/step-schema.json
data/sources/step-schema/github/supplemental-step-schema.json
data/sources/step-schema/github/step-schema.json

data/sources/expected-keys/github/raw/workflow-syntax.md
data/sources/expected-keys/github/parsed/expected-keys.json
data/sources/expected-keys/github/supplemental-keys.json
data/sources/expected-keys/github/expected-keys.json

data/sources/reports/*
data/sources/manifest.json
```

---

## 7. Provenance Manifest

`data/sources/manifest.json` tracks provenance metadata for all fetched datasets.

Each entry records:

| Field | Description |
|---|---|
| `dataset` | Dataset name |
| `sourceUrls` | URLs fetched in Stage 1 |
| `fetchedAtUtc` | Timestamp of the last fetch |
| `rawFileHashes` | Map of raw file names to `sha256:{hash}` values |

The manifest is updated atomically during Stage 1 (fetch) operations.

### 7.1 Relationship to Stage 2 JSON and canonical snapshots

- **`sourceUrls` + `rawFileHashes` + `fetchedAtUtc`** are the canonical record of Stage 1: which HTTPS URLs were used and the **`sha256:`** of each **committed** file under `raw/` (file name ↔ digest), after repository newline normalization where applicable.
- **Raw files** are the pre-parse documents from those URLs; operational “which URL applies” is always reconciled with the manifest for fetched datasets (§3.2).
- **`ManifestDatasetUrlSemantics`** (enforced when URLs are resolved for fetch) validates **host and full path** for fixed datasets: for example `raw.githubusercontent.com` URLs must be exactly under **`/github/docs/main/…`** with documented file paths (forks on `raw.githubusercontent.com` that reuse a file name elsewhere fail), and selected `docs.github.com` / `json.schemastore.org` / `data.iana.org` URLs must match their canonical paths exactly.
- **Stage 2 JSON** (whether under `parsed/` or, when collapsed, the canonical snapshot file) holds the extracted model and SHOULD repeat **raw file name + `sha256`** linkage as described in §3.3 so reviewers can validate “this parse came from these bytes” alongside the manifest. That linkage is **not** a second competing source of truth: it must remain **consistent** with `manifest.json` for the same committed raw files.
- **HTTPS `sourceUrl` fields** inside Stage 2 artifacts (when present) MUST match the manifest-backed URL configuration for Stage 1 (§3.3).
- Datasets **without** Stage 1 have **no** manifest entry; their canonical JSON is maintained per the appropriate profile in §3.4.

---

## 8. Implementation Components

### 8.1 Component Types

When adding a new dataset pipeline, follow this pattern:

| Component | Location | Responsibility |
|---|---|---|
| `SourcePathResolver` | `Services/` | Resolve data paths for the dataset (with legacy path fallback) |
| `Fetcher` | `Sources/` | HTTP fetch with `HttpClient`, SHA-256 hash computation, manifest entry |
| `MarkdownParser` / `SourceParser` | `Parsers/` | Parse raw files into intermediate JSON models |
| `Generator` | `Generators/` | StringBuilder-based `.g.cs` code generation |
| `SyncService` | `Services/` | Sync (regenerate) and IsUpToDate (verify) operations |
| `Commands` | `Commands/` | Static command methods wiring fetcher/parser/sync |
| `Model` | `Model/` | Data models for parsed/merged artifacts |
| `Validator` | `Validators/` | Optional cross-check validators |

### 8.2 Generator Output Convention

All generated `.g.cs` files:

- Start with `// <auto-generated>` comment
- State the regeneration command in the header
- Live under `src/Seiton.Core/Generated/`
- Must not be edited manually

### 8.3 Docs Markup Assumptions

Several parsers match GitHub Docs structure with fixed anchors (e.g., webhook `## \`event\`` headings, markdown tables with specific column headers, YAML fenced code blocks). If upstream docs change headings, table shapes, or column order, Stage 2 may emit empty or partial parsed JSON until the parser is updated.

CI `verify` on generated `.g.cs` and `Seiton.Update.Tests` contract tests on committed `raw/*.md` files are intended to surface such breaks early.

---

## 9. CI Integration

### 9.1 Verify Gate

`verify --dataset all` is the CI contract gate. It checks that all `.g.cs` files match their current source data. Non-zero exit indicates stale generated code.

### 9.2 Auto-Update Flow

```
schedule + workflow_dispatch
  → update   # or: fetch --dataset all → sync --dataset all → verify --dataset all
  → dotnet test (validate)
  → if changes detected → auto PR
```

### 9.3 Manual Update

Individual datasets can be refreshed independently:

```shell
# Full upstream refresh, regenerate all .g.cs, and verify (one command)
dotnet run --project src/Seiton.Update -- update

# Or as separate steps
dotnet run --project src/Seiton.Update -- fetch --dataset all
dotnet run --project src/Seiton.Update -- sync --dataset all
dotnet run --project src/Seiton.Update -- verify --dataset all

# Refresh webhooks end-to-end
dotnet run --project src/Seiton.Update -- fetch-webhooks
dotnet run --project src/Seiton.Update -- sync-webhooks

# Regenerate from existing snapshot (no network)
dotnet run --project src/Seiton.Update -- sync-webhooks

# Verify all generated files are up to date
dotnet run --project src/Seiton.Update -- verify --dataset all
```

---

## 10. Cross-Document References

- `Seiton_spec.md` §4 — Component architecture (Seiton.Update as maintainer-only tool)
- `Seiton_Parser_spec.md` §9 — Boundary marker referencing this document
- `Seiton_Parser_csharp_spec.md` §9 — C# generated file list and C#-specific notes; references this document for pipeline details
- `Seiton_Parser_go_spec.md` §9 — Go generated file list; references this document for pipeline details
- `architecture_spec_csharp.md` §4.5, §9 — Architecture rationale and CI flow
