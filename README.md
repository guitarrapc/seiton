# seiton

## Recent Updates

### 2026-04: Action metadata input support

- Added document-kind classification for workflow/action-metadata/unknown.
- Added action metadata path hints and root-structure finalization (`jobs` vs `runs`).
- Added mismatch and ambiguity diagnostics for classification stage.
- Default no-arg discovery remains workflow-first under `.github/workflows/`.
- Action metadata is supported when explicitly passed as input files.

Detailed notes: `Docs/release_notes_actions_support.md`

## Generated Data Updater

Generated parser metadata is maintained by `src/Seiton.Update`.

### Daily developer flow

```shell
dotnet run --project src/Seiton.Update -- sync --dataset all
dotnet run --project src/Seiton.Update -- verify --dataset all
dotnet test
```

### Dataset-specific flow

```shell
dotnet run --project src/Seiton.Update -- sync --dataset webhooks
dotnet run --project src/Seiton.Update -- sync --dataset availability
dotnet run --project src/Seiton.Update -- sync --dataset popular-actions
```

### Three-stage source pipeline

Each dataset supports independent fetch/parse/merge commands:

- webhooks:
	- `fetch-webhooks-sources`
	- `parse-webhooks-sources`
	- `merge-webhooks-sources`
- availability:
	- `fetch-availability-sources`
	- `parse-availability-sources`
	- `merge-availability-sources`
- popular-actions:
	- `fetch-popular-actions-sources`
	- `parse-popular-actions-sources`
	- `merge-popular-actions-sources`

## Benchmark

You can check parser benchmark at [GitHub Actions/Benchmark](https://github.com/guitarrapc/seiton/actions/runs/000000).
