# Release Notes: Action Metadata Input Support

Date: 2026-04-19

## Summary

Seiton now classifies input YAML documents as workflow or action-metadata using path-hint candidate plus root-structure finalization.

## Highlights

- Added document-kind classification model in core (`workflow`, `action-metadata`, `unknown`).
- Added path hints for action metadata:
  - `action.yml` / `action.yaml`
  - `.github/actions/<name>/action.yml` / `.github/actions/<name>/action.yaml`
- Added structural finalization hints:
  - root `jobs` => workflow
  - root `runs` => action-metadata
  - both `jobs` and `runs` => unknown + ambiguity diagnostic
- Added mismatch diagnostics when path hint and structure disagree.
- Added parser classified entrypoint for downstream routing.
- Updated C# linter routing:
  - workflow rules run only for finalized workflow inputs
  - finalized action-metadata currently returns parser diagnostics without workflow rule traversal

## Compatibility / Migration Notes

- No behavior change for default no-arg CLI discovery: `seiton` continues to auto-discover only under `.github/workflows/`.
- To lint action metadata, pass files explicitly (for example `.github/actions/release/action.yml`).

## Validation Snapshot

- Added classification tests covering path hints, structural confirmation, mismatch, and ambiguity.
- Confirmed parser regression suite remains green.
- Build is successful after updates.
