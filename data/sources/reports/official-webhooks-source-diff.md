# Official Source Diff Report: webhooks

- source-a: parsed schema input (raw schema file in-repo)
- source-b: parsed docs input (raw GitHub Docs markdown in-repo)
- exclude-schema-only: False
- generated-at-utc: 2026-06-26T05:28:59.4852477Z

Policy: normalized snapshot follows GitHub Docs for activity types when Docs table is parseable.

## Activity Type Mismatches
- check_suite
  - schema: [completed, requested, rerequested]
  - docs: [completed]
- issues
  - schema: [assigned, closed, deleted, demilestoned, edited, labeled, locked, milestoned, opened, pinned, reopened, transferred, unassigned, unlabeled, unlocked, unpinned]
  - docs: [assigned, closed, deleted, demilestoned, edited, labeled, locked, milestoned, opened, pinned, reopened, transferred, typed, unassigned, unlabeled, unlocked, unpinned, untyped]

## Docs Only Events
- none

## Schema Only Events
- project
- project_card
- project_column
