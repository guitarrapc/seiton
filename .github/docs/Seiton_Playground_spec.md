# Seiton Playground Specification

> This document is WASM-language-neutral — it specifies WHAT the Playground does, not HOW a specific language implementation achieves it. Defines the playground functional contract: architecture, WASM interop API, UI behavior, deployment, and operational constraints. For C#-specific implementation details, see `Seiton_Playground_csharp_spec.md`.

> **Cross-document rule**: This spec is the source of truth for playground behavior. When revised, also review and update `Seiton_Playground_csharp_spec.md` for consistency.

---

## 1. Scope and Motivation

### 1.1 Purpose

Provide a browser-based playground that runs Seiton's lint engine entirely in-browser via WebAssembly. User YAML input is never transmitted to a server.

### 1.2 Reference

The [actionlint playground](https://github.com/rhysd/actionlint/tree/main/playground) serves as reference architecture (Go→WASM, CodeMirror editor, static deployment).

### 1.3 Constraints

- No server-side processing — all lint execution happens in WASM on the client
- Static hosting only (GitHub Pages)
- Network-dependent lint rules (e.g., digest pinning, online audit) are disabled in the playground

---

## 2. Architecture

### 2.1 Layer Diagram

```
┌────────────────────────────────────────────────┐
│  Lint Engine (compiled to WASM)                │
├────────────────────────────────────────────────┤
│  WASM Interop Layer                            │
│  Exports: RunLint / ApplyAllFixes / GetVersion │
├────────────────────────────────────────────────┤
│  WASM Runtime Glue (language-specific)         │
├────────────────────────────────────────────────┤
│  main.js + CodeMirror UI                       │
├────────────────────────────────────────────────┤
│  GitHub Pages (static hosting)                 │
└────────────────────────────────────────────────┘
```

### 2.2 Processing Flow

1. Browser loads `index.html`
2. `main.js` initializes the WASM runtime
3. JS obtains references to exported WASM functions
4. User edits YAML in CodeMirror editor
5. After debounce (300ms), JS calls `RunLint(yamlSource, filePath)`
6. WASM side: parse + lint execution
7. WASM returns diagnostic results as UTF-8 JSON
8. JS renders results table + gutter markers in the editor

### 2.3 WASM Interop API

Exported functions callable from JavaScript:

| Function | Parameters | Return | Description |
|---|---|---|---|
| `RunLint` | `(yamlSource: string, filePath: string)` | UTF-8 JSON byte array | Diagnostic result array |
| `ApplyAllFixes` | `(yamlSource: string, filePath: string)` | `string` | Fixed YAML (original text on error) |
| `GetProductVersion` | none | `string` | Build version string |

### 2.4 Diagnostic JSON Schema

```json
[
  {
    "message": "Diagnostic message text",
    "line": 5,
    "column": 3,
    "severity": "Error | Warning | Info",
    "ruleId": "rule-name | null",
    "fixable": true,
    "fixDescription": "Fix description | null"
  }
]
```

### 2.5 Error Handling Contract

- WASM exported functions must never propagate unhandled exceptions across the interop boundary. An unhandled exception causes the WASM runtime to abort irreversibly.
- `RunLint`: on internal error, returns a single-element diagnostic array with `ruleId: "internal-error"`.
- `ApplyAllFixes`: on error, returns the original input text unchanged.
- `GetProductVersion`: on error, returns `"unknown"`.

---

## 3. Lint Execution Behavior

### 3.1 Debounce and Re-entry Control

| Behavior | Specification |
|---|---|
| Debounce interval | 300ms after last `change` event |
| Paste bypass | Lint executes immediately on paste (no debounce) |
| Re-entry guard | `lintInProgress` flag prevents concurrent lint invocations |
| Pending retry | If content changes during lint execution, a debounced re-lint is scheduled after completion |
| Staleness check | Lint is skipped when `(source, filePath)` pair is identical to the last successful lint |
| Staleness invalidation | File-type change, fix application, and URL fetch clear the staleness cache |

### 3.2 Runtime Death Detection

When the WASM runtime crashes (exits with non-zero code):

1. Set `runtimeAlive = false`
2. Stop all subsequent lint/fix calls
3. Display persistent error toast + inline message in the results pane prompting page reload

Detection pattern: catch errors matching `"runtime already exited"` from WASM interop calls.

### 3.3 Apply All Fixes

- Calls `ApplyAllFixes(source, filePath)` via WASM export
- If returned YAML differs from input: update editor, invalidate staleness, re-lint
- If unchanged: show informational toast (no fix was applicable or an error occurred)
- Network-dependent fixes (pinning, digest resolution) are unavailable in WASM and are skipped

---

## 4. UI Specification

### 4.1 Feature Catalog

| Feature | Description |
|---|---|
| YAML editor | CodeMirror 5 with yaml mode, auto-grow (`viewportMargin: Infinity`), line numbers, active line highlight |
| Real-time lint | Debounce 300ms, immediate on paste, staleness check |
| Results table | Position chip + message + ruleId chip + fixable chip per diagnostic |
| Gutter markers | Error = red (`--danger`), Warning = yellow (`--warning`), CSS class-based |
| Row click jump | Clicking a diagnostic row moves editor cursor to that position |
| Loading indicator | "Loading WebAssembly binary..." shown until WASM runtime is ready |
| File type selector | `workflow` (`.github/workflows/test.yml`) / `action.yml` |
| Sample selector | Built-in YAML snippets: default, simple, minimal, fixPermissions, matrix, actionComposite |
| Permalink (share) | pako deflate → Base64 → URL hash → clipboard copy |
| URL fetch | Fetch remote YAML by URL with validation and GitHub blob→raw conversion |
| Toast notifications | Dismiss button + Escape key (capture phase), auto-dismiss with configurable duration |
| Apply all fixes | Offline autofix with priority ordering (network fixes skipped) |
| Version badge | Shown after WASM startup, links to GitHub Release page |
| Color theme | System / Light / Dark cycle with localStorage persistence |
| Runtime crash detection | Stops calls, shows reload prompt |

### 4.2 Toast System

- Container: `#toast-stack` (fixed position, top of viewport)
- Independent from diagnostic results — lint results table is never cleared by toast operations
- Dismiss: dedicated `button.toast__dismiss` or **Escape** key (document capture phase, dismisses topmost toast)
- ARIA: `role="alert"` for error, `role="status"` for success/info
- Auto-dismiss durations: error = 8s, success = 3.8s, info = 4.2s
- URLs in toast body text are auto-linkified; link clicks do not propagate to toast dismiss

### 4.3 URL Fetch

- **Validation** (`looksLikePlausibleHttpFetchUrl`):
  - Protocol: http(s) only
  - Valid hosts: `localhost`, IPv4, IPv6, hostnames with ≥ 2 labels
  - Empty input → button disabled, title "Enter a YAML URL first"
  - Invalid input → button disabled, title "Incomplete URL"
  - During fetch → both input and button disabled
- **GitHub blob→raw normalization**: `github.com/{owner}/{repo}/blob/{ref}/{path}` → `raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}`
- **Error handling**: HTTP failure or HTML content-type → toast notification (results pane preserved)
- **Enter key**: on empty/invalid input, shows info toast only (no fetch)
- **Overlapping requests**: blocked via `fetchInFlight` flag

### 4.4 Color Theme

- **Default**: dark (`:root` tokens define dark palette)
- **System tracking**: `prefers-color-scheme: light` overrides via `:root:not([data-theme])` selector
- **Manual override**: footer button cycles **System → Light → Dark**
- **Persistence**: `localStorage` key `seiton-playground-color-mode` (`light`/`dark` stored; `system` removes key)
- **FOUC prevention**: inline `<script>` in `<head>` (before CSS) reads storage and sets `data-theme` + `meta[name=color-scheme]`
- **CodeMirror themes**: dark = `material-darker`, light = `default`. System mode tracks OS `change` event.
- **Gutter markers**: use `var(--danger)` / `var(--warning)` CSS custom properties

### 4.5 OGP and Twitter Card Metadata

| Meta Tag | Content |
|---|---|
| `<title>` | `seiton playground` |
| `meta[name=description]` | `A security-focused linter & fixer for GitHub Actions in your browser` |
| `meta[name=twitter:card]` | `summary_large_image` |
| `meta[name=twitter:image]` | `https://guitarrapc.github.io/seiton/ogp.png?v=2` |
| `meta[name=twitter:title]` | `seiton playground \| Try in your browser` |
| `meta[name=twitter:description]` | `A security-focused linter & fixer for GitHub Actions` |
| `meta[property=og:type]` | `website` |
| `meta[property=og:url]` | `https://guitarrapc.github.io/seiton/` |
| `meta[property=og:title]` | `seiton playground \| Try in your browser` |
| `meta[property=og:image]` | `https://guitarrapc.github.io/seiton/ogp.png?v=2` |
| `meta[property=og:description]` | `A security-focused linter & fixer for GitHub Actions` |
| `meta[property=og:site_name]` | `seiton playground` |
| `meta[name=apple-mobile-web-app-title]` | `seiton playground` |

- OGP image: `ogp.png` with cache-bust query (`?v=2`)
- Favicon: light/dark variants via `media="(prefers-color-scheme: ...)"` (`favicon.png` / `favicon-dark.png`)

### 4.6 Page Structure

```
<html lang="en">
  <head>
    meta (charset, viewport, color-scheme, description, OGP, twitter:card, apple-mobile-web-app-title)
    link rel="icon" (light/dark variants with prefers-color-scheme media query)
    link rel="preconnect" (cdnjs.cloudflare.com, cdn.jsdelivr.net)
    inline <script> (FOUC prevention)
    CodeMirror CSS (CDN, non-blocking)
    style.css (fingerprinted filename)
    CodeMirror JS (CDN, defer)
  </head>
  <body>
    <nav #header-bar>
      <header> (site title + logo, #playground-version badge, tagline)
      <div #controls> (permalink, URL fetch, file-type select, sample select)
    </nav>
    <div #toast-stack aria-live="polite">
    <main>
      <section #linter>
        editor pane (#editor-wrap > #editor + #apply-fixes-btn)
        results pane (.results-column > #loading + #lint-result + #success-msg)
      </section>
    </main>
    <section .playground-about>
    <section .resources>
    <footer> (GitHub link, copyright, #theme-cycle-btn)
    <script type="module" src="main.js">
  </body>
</html>
```

### 4.7 External Dependencies (CDN)

| Library | Version | Purpose |
|---|---|---|
| CodeMirror 5 | 5.65.16 | YAML editor (codemirror.min.js + yaml mode + active-line addon) |
| CodeMirror material-darker | 5.65.16 | Dark theme |
| pako | 2.1.0 | Permalink deflate/inflate (ESM import) |

---

## 5. Deployment

### 5.1 Method

GitHub Actions `actions/deploy-pages` (Pages artifact approach). No `gh-pages` branch.

### 5.2 Workflow Triggers

| Trigger | Behavior |
|---|---|
| `push.tags: ["v*"]` | Automatic deploy on release tag |
| `workflow_dispatch` | Manual re-deploy of an existing tag (validates tag existence via API) |

### 5.3 Security Posture

- Top-level `permissions: {}` (least privilege)
- Per-job permissions: `contents: read`, `pages: write`, `id-token: write`
- Checkout with `persist-credentials: false`
- All action references pinned to full commit SHA
- `concurrency.group: pages` prevents simultaneous deploys

### 5.4 Static Hosting Requirements

- `.nojekyll` file required (protects `_framework/` directory from Jekyll underscore prefix filtering)
- Fingerprinted + non-fingerprinted assets both emitted (no server-side import map rewriting on GitHub Pages)

### 5.5 Repository Configuration

GitHub Settings → Pages → Source: **GitHub Actions**

---

## 6. Lessons Learned

### 6.1 Unhandled Exceptions at WASM Interop Boundary Are Fatal

WASM runtimes abort on unhandled exceptions crossing the interop boundary. Once aborted, the runtime cannot be restarted without a full page reload.

**Mitigation pattern**:
- WASM side: wrap all exported functions in try/catch; return error information as normal return values
- JS side: detect "runtime already exited" pattern → set `runtimeAlive = false` → suppress all subsequent calls

### 6.2 Synchronous WASM Calls and JS Event Queue

Synchronous WASM calls block the main thread. User input queues at the browser level during lint execution. After completion, queued `change` events cascade.

**Mitigation pattern**:
1. `lintInProgress` flag (re-entry prevention)
2. `lintPendingRetry` flag (post-completion retry with debounce)
3. `lastLintedSource`/`lastLintedFilePath` (idempotency check)

### 6.3 Static Hosting and WASM Framework Assets

- `_framework/` directory requires `.nojekyll` due to underscore prefix
- Static hosts without import map support (GitHub Pages) require both fingerprinted and non-fingerprinted file output

---

## 7. References

- [actionlint playground source](https://github.com/rhysd/actionlint/tree/main/playground)
- [CodeMirror 5](https://codemirror.net/5/)
- [pako (zlib for browser)](https://github.com/nicolo-ribaudo/pako)
