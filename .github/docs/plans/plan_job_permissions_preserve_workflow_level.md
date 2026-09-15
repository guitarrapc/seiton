# Plan: Preserve workflow-level permissions in the `job-permissions-required` fix

## Goal

Stop `seiton --fix` from silently discarding scopes that the workflow author declared in a workflow-level `permissions` block. Today the fix replaces that declaration with an inference drawn from a four-entry table, produces a job that can no longer do what the author enabled, and reports `0 issues remain`.

The defect is one of correctness and availability, not security: the fix only ever narrows permissions, so it cannot over-grant. It can, however, break a working workflow in a way that is invisible until the workflow runs.

## Observed Behavior

Reproduced on **1.7.4** and **1.8.0** (Windows amd64). Both behave identically.

### Setup

```bash
mkdir -p repro/.github/workflows && cd repro
```

### Case A — OIDC job loses `id-token: write`

This is the case that motivated the report. `aws-actions/configure-aws-credentials` cannot request an OIDC token without `id-token: write`, so AWS authentication fails for the whole job.

```yaml
# .github/workflows/a-oidc.yml
name: a-oidc
on: [workflow_dispatch]

permissions:
  contents: read
  id-token: write

jobs:
  deploy:
    runs-on: ubuntu-24.04
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0
        with:
          persist-credentials: false
      - uses: aws-actions/configure-aws-credentials@61815dcd50bd041e203e49132bacad1fd04d2708 # v5.1.1
        with:
          role-to-assume: arn:aws:iam::123456789012:role/example
          aws-region: us-east-1
```

```bash
seiton --fix
```

Result — the job is given `contents: read` only:

```yaml
jobs:
  deploy:
    runs-on: ubuntu-24.04
    permissions:
      contents: read
    timeout-minutes: 10
```

`id-token` is now `none`. Re-running `seiton` reports `0 issues in 1 file`.

### Case B — write access removed from a release job

```yaml
# .github/workflows/b-write.yml
name: b-write
on: [workflow_dispatch]

permissions:
  contents: write

jobs:
  release:
    runs-on: ubuntu-24.04
    timeout-minutes: 10
    steps:
      - run: git push
```

Result — `permissions: {}` is inserted, so `git push` loses its token access.

### Case C — sole declared scope removed

```yaml
# .github/workflows/c-oidc-only.yml
name: c-oidc-only
on: [workflow_dispatch]

permissions:
  id-token: write

jobs:
  oidc:
    runs-on: ubuntu-24.04
    timeout-minutes: 10
    steps:
      - uses: aws-actions/configure-aws-credentials@61815dcd50bd041e203e49132bacad1fd04d2708 # v5.1.1
        with:
          role-to-assume: arn:aws:iam::123456789012:role/example
          aws-region: us-east-1
```

Result — `permissions: {}`.

### Case D — control, current behavior is correct here

```yaml
# .github/workflows/d-checkout.yml
name: d-checkout
on: [workflow_dispatch]

jobs:
  build:
    runs-on: ubuntu-24.04
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0
        with:
          persist-credentials: false
```

Result — `permissions: contents: read`. No workflow-level block exists, the action is in the inference table, and the outcome is what the rule intends.

### Summary

| Case | Workflow-level `permissions` | Inserted at job level | Outcome |
|---|---|---|---|
| A | `contents: read`, `id-token: write` | `contents: read` | `id-token` silently dropped |
| B | `contents: write` | `{}` | write access silently dropped |
| C | `id-token: write` | `{}` | only declared scope silently dropped |
| D | none | `contents: read` | correct |

## Why the Result Is Broken

Job-level `permissions` **replaces** the workflow-level block; it does not merge with it. The GitHub workflow syntax reference states that "if you specify the access for any of these permissions, all of those that are not specified are set to `none`".

So writing any job-level block, however minimal, discards every workflow-level scope the author declared. The fix writes such a block on every job that lacks one.

## Analysis

Three facts combine to produce the result.

1. `JobPermissionsRequiredRule` never reads the workflow-level `permissions` block. Its entry point is `VisitJobPre(JobRef job)`, and `CollectRequiredPermissions(JobRef job)` walks `job.Steps` only. The value that would make the fix correct is present in the same file and is not consulted.
2. The inference source, `src/Seiton.Core/Generated/PopularActions.g.cs`, declares `RequiredPermissions` for four actions:

   | Action | Scopes |
   |---|---|
   | `actions/checkout` | `contents: read` |
   | `actions/stale` | `issues: write`, `pull-requests: write` |
   | `pypa/gh-action-pypi-publish` | `id-token: write` |
   | `reviewdog/action-actionlint` | `contents: read` |

   Everything else — including every OIDC-consuming action other than the PyPI publisher, and every `run:` step — falls through to the strict `{}` default.
3. `Seiton_Linter_spec.md` §4.4 specifies the inference ("Auto-fix infers minimum scopes from known popular actions … strict `{}` default when unknown") but says nothing about what happens when a workflow-level block already exists.

The inference itself therefore behaves as specified. The gap is the unspecified interaction: an inference that covers four actions is allowed to overwrite an explicit author declaration, and the run then reports success.

A precedent for the missing input already exists in the codebase. `UseTrustedPublishingRule` tracks `workflowHasIdTokenWrite` and `currentJobHasIdTokenWrite`, so the linter can already observe workflow-level `id-token` state during a pass.

## User-Facing Specification

Proposed normative behavior for the `job-permissions-required` fix:

- When a workflow declares workflow-level `permissions`, the fix must not produce a job whose effective permissions are narrower than the workflow-level declaration.
- The inserted job-level block is the union of the inferred minimum scopes and the workflow-level declaration. For each scope present in both, the wider access wins.
- When no workflow-level `permissions` block exists, behavior is unchanged: inferred scopes, or `{}` when nothing is known.
- The diagnostic remains a warning in both cases; only the fix output changes.

The union keeps the rule's value — a job that previously inherited an unstated default now states its scopes — without the fix asserting that four table entries know better than the author.

### Alternative considered

Refusing to auto-fix when a workflow-level block exists, and reporting the diagnostic for a human to resolve, is safer still but leaves `--fix` unable to close a common finding on exactly the workflows that declare permissions carefully. The union achieves the same safety with a usable fix, so it is preferred.

### Related follow-up, out of scope here

The union above fixes every case in this report, because each one declares the scope it needs at workflow level. A job that needs a scope and declares it *nowhere* — no workflow-level block and an action outside the inference table — is still given `{}` and still breaks. Extending `PopularActions` with `RequiredPermissions` for OIDC-consuming actions (`aws-actions/configure-aws-credentials`, `azure/login`, `google-github-actions/auth`) is what closes that, and it is independent of the defect above. Plan it separately.

## Implementation Plan

1. Update `.github/docs/Seiton_Linter_spec.md` §4.4 with the union contract, then the language spec, before production code.
2. Add red tests for the equivalence classes in Cases A–D.
3. Give `JobPermissionsRequiredRule` access to the workflow-level `permissions` block, following the pattern already used by `UseTrustedPublishingRule`.
4. Seed the `merged` dictionary in `CollectRequiredPermissions` with the workflow-level scopes before walking `job.Steps`. The method already widens per scope through its `AccessLevel(access) > AccessLevel(existing)` comparison, so the union semantics fall out of the existing loop and no new merge logic is required.
5. Decide how `read-all` and `write-all` expand when seeding, and keep that consistent with `deny-read-all` / `deny-write-all`.
6. Update `docs/rules.md` for `job-permissions-required`, adding a **When fixing** bullet stating that workflow-level scopes are carried down.
7. Run targeted tests, then the full suite.

## Test Plan

Equivalence classes, each asserting the fixed output and that a re-lint reports no diagnostic:

- Workflow-level scope absent from the inference result is carried down (Case A).
- Workflow-level scope wider than the inferred one wins (Case B: `contents: write` over `contents: read`).
- Workflow-level block with no inferable steps is carried down verbatim (Case C).
- No workflow-level block leaves current behavior unchanged (Case D).
- Empty workflow-level block (`permissions: {}`) still yields the inferred scopes.
- `permissions: read-all` / `write-all` at workflow level, which `deny-read-all` / `deny-write-all` also report, do not produce a contradictory fix.
- Reusable-workflow call jobs keep the documented caller-side capping behavior.
- A job that already declares `permissions` is untouched.

## Performance Plan

The workflow-level block is parsed once per workflow and read per job, so the added work is O(jobs) lookups against a structure the pass already holds. Run `CoreLintBenchmark` before and after and compare mean time and allocated bytes against the committed baseline; no regression is expected.

## Reporter Notes

Found while adding deployment workflows to another repository. The broken output was caught by review before the workflow ran; had it merged, the failure would have surfaced as an AWS credential error in a scheduled job with no obvious link to a linter fix.
