# Seiton Specification

> Defines Seiton's top-level architecture contract and responsibility boundaries.
> This document is an overview. Detailed behavior is defined in component specifications.

---

## 1. Purpose and Scope

Seiton has two primary runtime components for GitHub Actions document analysis.

1. **Parser**: Parses UTF-8 YAML into typed AST and parser diagnostics.
2. **Linter**: Executes rules over parsed AST and returns aggregated lint diagnostics.

Supported document kinds:

- Workflow files (for example `.github/workflows/*.yml`, `.github/workflows/*.yaml`)
- Action metadata files (`action.yml`, `action.yaml`, including `.github/actions/<name>/action.yml|action.yaml`)

This document fixes the boundary between those components and links to each component's normative specification.

---

## 2. High-Level Pipeline

```
Check(utf8Yaml, filePath)
  -> classify input document kind (workflow or action-metadata)
  -> Parse(utf8Yaml, filePath)
  -> if fatal parse failure: return parser diagnostics
  -> execute linter rules on AST
  -> collect diagnostics
  -> filter/sort/dedup
  -> output
```

### 2.1 File-Kind Classification Policy

Input routing uses a two-stage policy:

1. Fast path hint from `filePath` (candidate kind only)
2. Structural validation from YAML top-level keys (final kind)

Normative path hints:

- Basename `action.yml` or `action.yaml` -> action-metadata candidate
- Path matching `.github/actions/<name>/action.yml` or `.github/actions/<name>/action.yaml` -> action-metadata candidate

Normative structural hints:

- Root `jobs` indicates workflow
- Root `runs` indicates action metadata
- If both are present, treat as ambiguous input and emit diagnostic

Path hints are not final truth. If structure conflicts with hint, structure wins and diagnostics must explain the mismatch.

---

## 3. Responsibility Boundary

| Area | Parser | Linter |
|---|---|---|
| YAML structural validation | Owns | - |
| Workflow AST construction | Owns | Consumes |
| Expression parsing and semantic typing data | Owns | Consumes |
| Rule traversal hooks and rule execution model | - | Owns |
| Rule configuration (enable/disable/severity/exclusion) | - | Owns |
| Diagnostic aggregation from rules | - | Owns |
| Diagnostic sort/dedup/final filtering | Shared contract (entrypoint-defined) | Owns in lint entrypoint |

Boundary policy:

- Parser must remain reusable without rule execution.
- Linter must consume parser outputs instead of reparsing workflow structure.
- Rule suppression/exclusion belongs to linter contract, not parser contract.

---

## 4. Component Architecture

Seiton is distributed as three separate components with distinct responsibilities.

| Component | Type | Audience | Description |
|---|---|---|---|
| **Seiton.Core** | Library | Library consumers | Core parser and linter library. Exposes `WorkflowParser`, `LintEngine`, `LintConfigLibrary`, and related types. |
| **Seiton.Update** | CLI (management) | Maintainers only | Data source updater. Fetches, parses, and syncs generated metadata (webhook types, runner labels, popular actions, availability). Not user-facing. |
| **seiton** | CLI (user-facing) | End users | User-facing CLI. Wraps `Seiton.Core` and exposes user commands such as linting workflow files, validating config, and generating config templates. |

Responsibilities that belong exclusively to each component:

- **Seiton.Core**: All logic for parsing, linting, config normalization, and config validation. Must not reference CLI or I/O concerns.
- **Seiton.Update**: Dataset fetch/sync/verify pipelines. Must not expose user-facing lint or config commands.
- **seiton CLI**: Entry point for users. Delegates to `Seiton.Core` APIs.

---

## 5. Normative Specifications

- Parser (language-agnostic): `.github/docsSeiton_Parser_spec.md`
- Parser C# companion: `.github/docsSeiton_Parser_csharp_spec.md`
- Parser Go companion: `.github/docsSeiton_Parser_go_spec.md`

- Linter (language-agnostic): `.github/docsSeiton_Linter_spec.md`
- Linter C# companion: `.github/docsSeiton_Linter_csharp_spec.md`
- Linter Go companion: `.github/docsSeiton_Linter_go_spec.md`

Implementation plans:

- Parser plan (C#): `.github/docsparser_implementation_csharp_plan.md`
- Linter plan (C#): `.github/docslinter_implementation_csharp_plan.md`

---

## 5. Cross-Document Consistency Rule

- Parser contract changes must update parser companion docs and parser implementation plan in the same change scope.
- Linter contract changes must update linter implementation plan in the same change scope.
- If a change affects parser/linter boundary, update this overview document together with both component specs.
- Language-specific companion specs must keep chapter-0 template numbering aligned as `0.1 Contract`, `0.2 Overview`, `0.3 Structure`, `0.4 Model`, `0.5 Design` (parser may name `0.4` as `YAML/Alias`).
