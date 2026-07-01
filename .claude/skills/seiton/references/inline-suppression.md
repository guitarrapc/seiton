# Inline Suppression Directives

Use this reference when a workflow still has **inline comment suppressions** from a previous linter, or when the user asks how to silence a single diagnostic without editing `.github/seiton.yaml`.

**Default recommendation:** prefer `.github/seiton.yaml` `exclusions` for file-wide, job-wide, or repeated patterns. Inline directives are for **one-off** cases in a single workflow file.

## Decision flow

When helping a user suppress a diagnostic:

1. **Can `seiton --fix` resolve it?** → Fix, do not suppress.
2. **Same rule on many files or jobs?** → `exclusions` in config (see `references/configuration.md`).
3. **One line in one file?** → `# seiton: disable-next-line <rule-id>`
4. **One step in one file?** → `# seiton: disable-step <rule-ids>` above the step item
5. **One job in one file?** → `# seiton: disable-job <job-id> <rule-ids>`
6. **Entire workflow file (rare)?** → `# seiton: disable-file <rule-ids>` at the top

If the workflow contains **unrecognized inline comments** from another tool, translate the intent into config `exclusions` or seiton directives below — seiton does not read foreign comment syntax.

## Config exclusions (preferred for scope)

```yaml
# File-wide — all rules for matching paths
exclusions:
  - file: ".github/workflows/demo-*.yml"

# File + specific rules
exclusions:
  - file: ".github/workflows/legacy-deploy.yml"
    rules:
      - unpinned-uses

# File + job + rules
exclusions:
  - file: ".github/workflows/ci.yml"
    jobs:
      - deploy
    rules:
      - job-permissions-required
```

Omitting `rules` (or `rules: ["*"]`) suppresses **all** rules for the matched scope.

## Inline syntax

All directives use YAML comments: `# seiton: <directive> ...`

### `disable-next-line` — next YAML line only

```yaml
steps:
  # seiton: disable-next-line unpinned-uses
  - uses: actions/checkout@v6
```

The comment must sit **directly above the key line the rule reports on**, not above a parent node.

**`if-cond` — place above `if:`:**

```yaml
steps:
  # ✗ Wrong — comment targets the step line; if-cond reports on if:
  # seiton: disable-next-line if-cond
  - run: echo ok
    if: ${{ true }}

  # ✓ Correct
  - run: echo ok
    # seiton: disable-next-line if-cond
    if: ${{ true }}
```

**`matrix` — place above the axis key:**

```yaml
strategy:
  matrix:
    # seiton: disable-next-line matrix
    os: []
```

Block-scalar `if:` (`|` or `>`): place the comment above the `if:` key; diagnostics are attributed to that line.

### `disable-step` — next step item only

```yaml
steps:
  # seiton: disable-step unredacted-secrets
  - name: Setup Cosign keys
    run: |
      echo "${SYNCED_COSIGN_PRIVATE_KEY}" > cosign.key
```

Use `disable-step` when the diagnostic belongs to a step as a whole or may be reported inside a multi-line `run:` block. It applies to the next step item in the same `steps:` sequence. Blank lines, ordinary comments, and other seiton directives between the directive and the step are allowed.

### `disable-job` — all matching diagnostics in one job

```yaml
# seiton: disable-job build unpinned-uses,job-permissions-required
jobs:
  build:
    ...
```

`job-id` is the YAML key under `jobs:` (e.g. `build`). Rule IDs follow, comma-separated.

### `disable-file` — top of workflow file

```yaml
# seiton: disable-file dangerous-triggers
on: push
jobs:
  ...
```

## Multiple rule IDs

Use commas or whitespace between rule IDs:

```yaml
# seiton: disable-next-line dangerous-triggers,job-permissions-required
# seiton: disable-next-line dangerous-triggers job-permissions-required
```

## Precedence

Inline directives override config-file `exclusions` for the same diagnostic.

## Agent checklist

When converting a workflow that has foreign inline suppressions:

1. Run `seiton` and note remaining diagnostics.
2. For each suppression intent, choose config vs inline using the decision flow above.
3. Look up rule IDs in `references/rules.md` or `seiton rules`.
4. Verify placement for `if-cond` / `matrix` (common mistake: comment above the step instead of the reported key).
5. Re-run `seiton` until the intended diagnostics are gone.

For the full config schema see `references/configuration.md`. The public docs site mirrors the same inline-suppression content under **Inline Suppression Directives**.
