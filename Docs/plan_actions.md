# Seiton Actions Support Plan

## 1. Goal

Enable Seiton to classify and lint both workflow files and action metadata files.

Primary requirement for fast path hints:

- `action.yml` or `action.yaml` basename => action-metadata candidate
- `.github/actions/<name>/action.yml` or `.github/actions/<name>/action.yaml` => action-metadata candidate

Final document kind must be structure-confirmed (path hint is candidate only).

## 2. Scope

In scope:

- Document-kind classifier in core
- CLI file-kind routing behavior updates
- Parser/linter entrypoint routing updates
- Tests for path-hint + structure confirmation behavior

Out of scope in this plan:

- Full action metadata rule parity with external tools
- Broad recursive auto-discovery of action files by default

## 3. Design Policy

Classifier policy:

1. Build candidate kind from path hints (fast)
2. Confirm kind from YAML top-level structure (authoritative)
3. If path and structure disagree, structure wins and mismatch diagnostic is emitted

Structural discriminator policy:

- Root `jobs` => workflow
- Root `runs` => action-metadata
- Root has both `jobs` and `runs` => `unknown` + ambiguity diagnostic
- Root has neither `jobs` nor `runs` => unresolved; existing parser diagnostics determine failure details

Kinds:

- `workflow`
- `action-metadata`
- `unknown`

## 4. Work Breakdown

### Phase A: Core classifier contract

Tasks:

- Add `DocumentKind` model and classifier API in core
- Implement path-hint matcher for action metadata paths
- Add structure-confirmation logic for workflow and action metadata
- Implement root-key discriminator (`jobs` vs `runs`) with ambiguous-case handling

Done when:

- Classifier returns deterministic kind for known fixtures
- Path-only false positives are corrected by structure stage
- `jobs`/`runs` discriminator behavior is covered by positive and ambiguity fixtures

### Phase B: Parser/linter entrypoint routing

Tasks:

- Route check pipeline by finalized `DocumentKind`
- Keep fatal parse behavior deterministic for each kind
- Return mismatch diagnostics when hint and structure conflict

Done when:

- Existing workflow behavior is unchanged
- Action metadata inputs no longer fail as missing workflow keys (`on`, `jobs`)

### Phase C: CLI behavior update

Tasks:

- Keep default auto-discovery workflow-first under `.github/workflows/`
- Support explicit action metadata file paths in `FILES`
- Route explicit files through classifier before parsing

Done when:

- `seiton` (no args) behavior stays compatible
- `seiton .github/actions/foo/action.yml` is accepted and routed as action-metadata

### Phase D: Test coverage

Tasks:

- Unit tests for path-hint matching
- Unit tests for structure confirmation
- Integration tests for explicit action files through CLI
- Regression tests for workflow-only defaults

Done when:

- Workflow regression tests remain green
- New action-classification tests cover positive/negative/conflict cases

### Phase E: Documentation and release readiness

Tasks:

- Keep parser/linter/CLI specs synchronized
- Add release note entries for new file-kind support
- Add migration note clarifying workflow-first auto-discovery remains unchanged

Done when:

- Docs and behavior match
- CI passes with updated tests

## 5. Risks and Mitigations

Risk:

- Misclassification from path-only assumptions

Mitigation:

- Always finalize by structure
- Emit mismatch diagnostics for observability

Risk:

- Breaking existing workflow auto-discovery behavior

Mitigation:

- Keep no-argument discovery unchanged in this phase
- Add explicit regression tests for current defaults

## 6. Acceptance Criteria

- Action path hints required in this request are implemented
- Final kind is structure-confirmed, not path-only
- Structural hints are implemented in classifier (`jobs` => workflow, `runs` => action-metadata)
- CLI can lint explicit action metadata files
- Workflow default discovery remains compatible
- Specifications and tests are updated together
