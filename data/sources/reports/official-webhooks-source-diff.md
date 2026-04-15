# Official Source Diff Report: webhooks

- source-a: https://json.schemastore.org/github-workflow.json
- source-b: https://raw.githubusercontent.com/github/docs/main/content/actions/reference/workflows-and-actions/events-that-trigger-workflows.md
- include-schema-only: False
- generated-at-utc: 2026-04-15T08:29:51.1543863Z

Policy: normalized snapshot follows GitHub Docs for activity types when Docs table is parseable.

## Activity Type Mismatches
- check_suite
  - schema: [completed, requested, rerequested]
  - docs: [completed]
- issues
  - schema: [assigned, closed, deleted, demilestoned, edited, labeled, locked, milestoned, opened, pinned, reopened, transferred, unassigned, unlabeled, unlocked, unpinned]
  - docs: [assigned, closed, deleted, demilestoned, edited, labeled, locked, milestoned, opened, pinned, reopened, transferred, typed, unassigned, unlabeled, unlocked, unpinned, untyped]
- project
  - schema: [closed, created, deleted, edited, reopened, updated]
  - docs: [closed, created, deleted, edited, reopened]

## Docs Only Events
- none

## Schema Only Events
- pull_request
- pull_request_target
