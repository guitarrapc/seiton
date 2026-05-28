# Plan: Playground Results Pane — Severity Level Display

## Investigation Summary

### Current State

| Aspect | Status |
|--------|--------|
| Severity in diagnostic JSON | ✅ Always present (`"Error"`, `"Warning"`, `"Info"`) |
| Gutter markers | ✅ Color-coded (Error=red, Warning/Info=amber) |
| Result table rows | ❌ No severity indicator — all rows look identical |
| Info vs Warning distinction | ❌ Both rendered as amber in gutter; indistinguishable |

### Data Flow

```
LintEngine (DiagnosticSeverity enum)
  → PlaygroundLintRunner (SeverityString: "Error"/"Warning"/"Info")
    → JSON payload (diag.severity field)
      → main.js renderResults() — only reads severity for gutter markers
        → Result table row — severity NOT displayed
```

The severity data **already reaches the browser** in every diagnostic object. The only gap is UI rendering in the results table.

### Files to Modify

| File | Change |
|------|--------|
| `src/Seiton.Playground/wwwroot/main.js` | Add severity chip to result row rendering |
| `src/Seiton.Playground/wwwroot/style.css` | Add severity chip styles + `--info` CSS variable |

No backend (C#/WASM) changes required — the JSON payload already includes severity.

---

## Implementation Plan

### Phase 1: Severity Chip in Results Table (Priority: High)

Add a colored severity chip (similar to existing `pos-chip` / `rule-chip`) to each result row.

**main.js — `renderResults()`:**

```javascript
// After posCell, before descCell
const sevCell = document.createElement('td');
const sevTag = document.createElement('span');
sevTag.className = `severity-chip severity-chip--${diag.severity.toLowerCase()}`;
sevTag.textContent = diag.severity;
sevCell.appendChild(sevTag);
row.appendChild(sevCell);
```

**style.css — severity chip:**

```css
.severity-chip {
  display: inline-block;
  padding: 0.1rem 0.45rem;
  border-radius: 3px;
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.02em;
}
.severity-chip--error   { background: var(--danger);  color: #fff; }
.severity-chip--warning { background: var(--warning); color: #1a1a2e; }
.severity-chip--info    { background: var(--info);    color: #1a1a2e; }
```

**style.css — add `--info` CSS variable:**

```css
/* Dark theme (root) */
--info: #82aaff;

/* Light theme */
--info: #1565c0;
```

**Effort**: Small (JS chip creation + CSS styling). No WASM rebuild needed.

### Phase 2: Gutter Marker Info Distinction (Priority: Medium)

Currently Info and Warning share the same gutter color (`--warning`). Add a third branch:

```javascript
const cls = diag.severity === 'Error'  ? 'gutter-marker--error'
          : diag.severity === 'Info'   ? 'gutter-marker--info'
          :                              'gutter-marker--warning';
marker.className = `gutter-marker ${cls}`;
```

```css
.gutter-marker--info { color: var(--info); }
```

**Effort**: Minimal (one conditional + one CSS rule).

### Phase 3: Row Background Tint (Priority: Low, Optional)

Apply a subtle left-border or background tint to result rows by severity for scanability:

```css
.result-table tr[data-severity="error"]   { border-left: 3px solid var(--danger); }
.result-table tr[data-severity="warning"] { border-left: 3px solid var(--warning); }
.result-table tr[data-severity="info"]    { border-left: 3px solid var(--info); }
```

```javascript
row.dataset.severity = diag.severity.toLowerCase();
```

**Effort**: Minimal. Provides redundant cue alongside chip for accessibility.

---

## Priority Summary

| Phase | What | Priority | Effort | Impact |
|-------|------|----------|--------|--------|
| 1 | Severity chip in result rows | **High** | Small | Primary UX gap closed |
| 2 | Gutter marker Info distinction | **Medium** | Minimal | Info/Warning no longer conflated |
| 3 | Row left-border tint | **Low** | Minimal | Accessibility & scanability boost |

---

## Spec Update Required

After implementation, update `Seiton_Playground_spec.md` § 4.1 Feature Catalog:

- Results table description: add "severity chip (Error/Warning/Info color-coded)"
- Gutter markers description: add Info = blue (`--info`)

---

## Constraints

- No WASM/C# changes needed — severity is already in the JSON payload
- No performance concern — chip creation is O(n) where n = number of diagnostics (already iterating)
- Existing `--danger` / `--warning` CSS variables reused; only `--info` is new
- Both light and dark themes need `--info` defined
