# Seiton Specification

> Defines Seiton's top-level architecture contract and responsibility boundaries.
> This document is an overview. Detailed behavior is defined in component specifications.

---

## 1. Purpose and Scope

Seiton has two primary runtime components for GitHub Actions workflow analysis.

1. **Parser**: Parses UTF-8 YAML into typed AST and parser diagnostics.
2. **Linter**: Executes rules over parsed AST and returns aggregated lint diagnostics.

This document fixes the boundary between those components and links to each component's normative specification.

---

## 2. High-Level Pipeline

```
Check(utf8Yaml, filePath)
  -> Parse(utf8Yaml, filePath)
  -> if fatal parse failure: return parser diagnostics
  -> execute linter rules on AST
  -> collect diagnostics
  -> filter/sort/dedup
  -> output
```

---

## 3. Responsibility Boundary

| Area | Parser | Linter |
|---|---|---|
| YAML structural validation | Owns | - |
| Workflow AST construction | Owns | Consumes |
| Expression parsing and semantic typing data | Owns | Consumes |
| Rule traversal hooks and rule execution model | - | Owns |
| Rule configuration (enable/disable/severity) | - | Owns |
| Diagnostic aggregation from rules | - | Owns |
| Diagnostic sort/dedup/final filtering | Shared contract (entrypoint-defined) | Owns in lint entrypoint |

Boundary policy:

- Parser must remain reusable without rule execution.
- Linter must consume parser outputs instead of reparsing workflow structure.
- Rule suppression/exclusion belongs to linter contract, not parser contract.

---

## 4. Normative Specifications

- Parser (language-agnostic): `Docs/Seiton_Parser_spec.md`
- Linter (language-agnostic): `Docs/Seiton_Linter_spec.md`
- Parser C# companion: `Docs/Seiton_Parser_csharp_spec.md`
- Parser Go companion: `Docs/Seiton_Parser_go_spec.md`

Implementation plans:

- Parser plan (C#): `Docs/parser_implementation_csharp_plan.md`
- Linter plan (C#): `Docs/linter_implementation_csharp_plan.md`

---

## 5. Cross-Document Consistency Rule

- Parser contract changes must update parser companion docs and parser implementation plan in the same change scope.
- Linter contract changes must update linter implementation plan in the same change scope.
- If a change affects parser/linter boundary, update this overview document together with both component specs.
