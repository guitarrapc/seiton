# Documentation Authoring Guidelines

Rules for writing and maintaining specification documents (`.github/docs/`) and the user-facing rule reference (`docs/rules.md`).

---

## 1. Specification Documents (`.github/docs/Seiton_Linter_*.md`)

### 1.1 Shared Spec (`Seiton_Linter_spec.md`)

- Contains **WHAT** (behavior contract) and **WHY** (design rationale) only.
- No HOW, no implementation status, no backlog, no roadmap.
- Must remain **language-neutral**: do not name C#, Go, or any specific runtime in normative statements.
- Rule entries in the normative catalog (§4.4) are single-sentence behavior summaries, not user-facing guides.

### 1.2 Language Specs (`Seiton_Linter_csharp_spec.md`, `Seiton_Linter_go_spec.md`)

- Record **current** runtime-specific contracts and implementation notes.
- Do not include future plans, backlog, or roadmap items — those belong in plan documents.
- Reference the shared spec for normative rule behavior; only add language-specific deviations or notes.

### 1.3 Cross-Document Paths

- Every cross-document path must resolve to an existing file.
- Use relative paths from the repository root (e.g., `.github/docs/Seiton_Linter_spec.md`).
- When renaming or removing a referenced document, search all `.github/docs/*.md` files and update references in the same commit.

### 1.4 Fixability Truth Source

- The authoritative sources for which rules support auto-fix are `docs/rules.md` (user-facing) and the `§8.4 Fixable Rule Catalog` in the shared spec (implementer-facing).
- Do not duplicate fixability information in language spec rule tables unless noting a language-specific limitation.

---

## 2. User-Facing Rule Reference (`docs/rules.md`)

### 2.1 Section Template

Every rule section follows this order:

1. **Summary** — one sentence: what the rule detects.
2. **Example trigger** — minimal YAML that fires the rule.
3. **Remediation** — how to fix, with code example(s).
4. **Notes** (optional) — edge cases, auto-fix limitations, advanced details.
5. **Configuration** (optional) — rule-specific config keys.

### 2.2 Example Purity

- A trigger example must fire **only** the rule being documented.
- Do not use patterns that inadvertently fire other rules (e.g., `ubuntu-latest` in a rule about permissions).
- When cross-trigger is intentional (e.g., the rule itself checks runner labels), add a comment noting the intent.

### 2.3 Example Design Conventions

| Element | Convention | Exception |
|---|---|---|
| Runner label | `ubuntu-24.04` (version-pinned) | `runner-no-latest` rule's own examples |
| `uses:` reference | Full SHA (`owner/repo/action@<40-hex>`) or local path | Rules that examine action behavior by version (e.g., `artipacked`, `checkout-persist-credentials`, `outdated-action-runner`, `cache-poisoning`) may use version tags when the version distinction is essential to the explanation |
| Reusable workflow ref | Full SHA (`owner/repo/.github/workflows/x.yml@<40-hex>`) | `unpinned-uses` / `reusable-workflow` own examples |
| Shell examples | bash by default; show PowerShell only when shell distinction matters | — |
| Placeholder commands | Convey rule intent (e.g., `npm test`, `./deploy.sh`, `curl`) when the step content illustrates *why* the rule matters | `echo ok` / `echo ng` acceptable in purely structural rules where the command content has no bearing on the rule |

### 2.4 Remediation Style

- Show the **recommended** approach first.
- For policy/security rules with multiple valid fixes, list approaches as a bullet summary before code examples. Label each code block (e.g., `# Approach A: ...`).
- Never present a single example as the "only correct answer" unless the fix is truly singular (e.g., `persist-credentials: false`).

### 2.5 Long Sections

- Keep trigger + remediation within 1-scroll readability for first-time users.
- Move dense edge-case lists into a `<details>` block or a **Notes** subsection at the end of the rule section.

---

## 3. General

- Do not add implementation language names to language-neutral documents.
- Lesson-learned notes are welcome in specs (they capture surprising constraints); status/backlog items are not.
- When adding a new rule, update all three locations: shared spec §4.4, language spec rule table, and `docs/rules.md` section.
