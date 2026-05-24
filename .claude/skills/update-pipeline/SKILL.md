---
name: update-pipeline
description: How to work with and add new dataset pipelines in `src/Seiton.Update/`. Covers the multi-stage pipeline model, CLI command naming conventions, and implementation checklist for new datasets.
---

# Update Pipeline

## Pipeline Model

Each generated dataset follows a multi-stage pipeline:

```
data/sources/{dataset}/github/
  raw/          ← Stage 1: fetched raw files from official sources (network)
  parsed/       ← Stage 2: parsed intermediate JSON (local, deterministic)
  supplemental-*.json ← Hand-written entries merged in Stage 3 (optional)
  {name}.json   ← Stage 3: merged canonical snapshot or hand-written source
```

## CLI Command Naming Convention

Per-dataset commands:

| Command | Purpose |
|---|---|
| `fetch-{dataset}` | Orchestrator: fetch + parse + manifest update |
| `fetch-{dataset}-sources` | Stage 1: download raw source files |
| `parse-{dataset}-sources` | Stage 2: parse raw files into intermediate JSON |
| `merge-{dataset}-sources` | Stage 3: merge parsed artifacts into snapshot |
| `sync-{dataset}` | Generate `.g.cs` from snapshot/source JSON |
| `verify-{dataset}` | Check `.g.cs` is up to date (CI) |
| `validate-{dataset}` | Cross-check source data against docs (optional) |

Aggregate commands:

| Command | Purpose |
|---|---|
| `fetch --dataset all` | Run every dataset's `fetch-{dataset}` in dependency order (network) |
| `sync --dataset all` | Regenerate every `.g.cs` |
| `verify --dataset all` | Verify every `.g.cs` |
| `update` | `fetch --dataset all` → `sync --dataset all` → `verify --dataset all` (full refresh) |

Not all datasets implement all stages. Some use hand-written JSON as primary source and only implement sync/verify. Some datasets have **supplemental JSON** (`supplemental-*.json`) for entries not derivable from raw sources. Stage 3 merges these into the canonical snapshot. See `.github/docs/Seiton_Update_spec.md` §3.1.3 for details.

## Adding a New Dataset Pipeline

Follow the existing pattern:

1. Create a `SourcePathResolver` in `Services/` (with legacy path fallback)
2. Create a `Fetcher` in `Sources/` (with `HttpClient`, `ComputeSha256`, manifest entry)
3. Create a `MarkdownParser` or `SourceParser` in `Parsers/`
4. Create a `Generator` in `Generators/` (StringBuilder-based codegen)
5. Create a `SyncService` in `Services/` (Sync + IsUpToDate)
6. Create `Commands` in `Commands/` (static methods)
7. Wire up commands in `Program.cs` (both convenience aliases and `RunSync`/`RunVerify` dispatchers)

## Regeneration Example

```shell
dotnet run --project src/Seiton.Update -- sync-availability
```
