# Seiton Playground Specification

> This document is WASM-language-neutral — it specifies WHAT the Playground does, not HOW a specific language implementation achieves it. Defines the playground functional contract: architecture, WASM interop API, UI behavior, deployment, and operational constraints. For language-specific implementation details, see `Seiton_Playground_csharp_spec.md` or `Seiton_Playground_go_spec.md`.

> **Cross-document rule**: This spec is the source of truth for playground behavior. When revised, also review and update `Seiton_Playground_csharp_spec.md` and `Seiton_Playground_go_spec.md` for consistency.

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
│  Exports: RunLint / ApplyAllFixes / GetProductVersion │
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
5. After debounce (500ms), JS calls `RunLint(yamlSource, filePath)`
6. WASM side: parse + lint execution
7. WASM returns diagnostic results as UTF-8 JSON
8. JS renders results table + gutter markers in the editor

### 2.3 WASM Interop API

Exported functions callable from JavaScript:

| Function | Parameters | Return | Description |
|---|---|---|---|
| `RunLint` | `(yamlSource: string, filePath: string)` | UTF-8 JSON byte array | Diagnostic result array |
| `ApplyAllFixes` | `(yamlSource: string, filePath: string)` | `string` | Fixed YAML (original text on error) |
| `ApplyAllFixesWithNetworkAsync` | `(yamlSource: string, filePath: string)` | `Promise<string>` | JSON: `{"yaml":"...","resolved":N,"skipped":N,"failed":N}` |
| `SetConfig` | `(configYaml: string)` | UTF-8 JSON byte array | Config diagnostic array (empty = success) |
| `GetFlowJson` | `(yamlSource: string, filePath: string)` | UTF-8 JSON byte array | flow-json document for the Flow tab (see §2.7) |
| `GetProductVersion` | none | `string` | Build version string |

#### 2.3.1 SetConfig Behavior

- Parses `configYaml` as a seiton configuration (same format as `.github/seiton.yaml`)
- On success: stores the parsed `LintConfig` for subsequent `RunLint`/`ApplyAllFixes` calls; returns empty JSON array `[]`
- On parse/validation errors: returns diagnostic array (same schema as `RunLint`); previous valid config is retained
- Empty or whitespace-only input: resets to default config (no overrides)
- Uses content-hash caching to skip re-parse when config content has not meaningfully changed (see §3.4)

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

Non-functional requirement:

- Diagnostic completeness is mandatory on every `RunLint` path (workflow and action metadata). Any non-empty diagnostic in the returned JSON must preserve meaningful fields (`line >= 1`, `column >= 1`, non-empty `message`, and valid `severity`) in both the payload and rendered UI.

### 2.5 Input Normalization

- `yamlSource`: null/empty treated as empty string
- `filePath`: null/whitespace trimmed; defaults to `.github/workflows/test.yml` when absent

### 2.6 Error Handling Contract

- WASM exported functions must never propagate unhandled exceptions across the interop boundary. An unhandled exception causes the WASM runtime to abort irreversibly.
- `RunLint`: on internal error, returns a single-element diagnostic array with `ruleId: "internal-error"`, message prefixed with `[internal error]`, position `(1,1)`, severity `Error`, `fixable: false`.
- `ApplyAllFixes`: on error, logs to browser console and returns the original input text unchanged.
- `SetConfig`: on internal error, returns single-element diagnostic array (same as `RunLint` error format); previous valid config is retained.
- `GetFlowJson`: on internal error, returns an empty flow document with an `error` property (`{"version":1,"workflows":[],"error":"..."}`) so the flow-json shape never breaks the UI parser.
- `GetProductVersion`: on error, returns `"unknown"`.

### 2.7 Flow API

`GetFlowJson` returns the **flow-json contract** — the same machine-readable workflow-structure document as `seiton check --format flow-json` (see `Seiton_CLI_spec.md` §6.6). It is backed by `PlaygroundFlowRunner` (separate from `PlaygroundLintRunner` so the diagnostics API and the flow API remain independent contracts) and consumed by the Flow tab.

- **WHY shared contract**: parsing YAML separately in the UI would create interpretation drift between lint and visualization; both CLI and Playground build the flow from the same parsed AST via `WorkflowFlowCollector` in Seiton.Core.
- Non-workflow documents (e.g. `action.yml`) yield an empty `workflows` array; the Flow tab shows an empty-state notice.
- Identity-based caching mirrors `RunLint`: an identical `(yamlSource, filePath)` reference pair returns the cached byte array without re-parsing.

---

## 3. Lint Execution Behavior

### 3.1 Debounce and Re-entry Control

| Behavior | Specification |
|---|---|
| Debounce interval | 500ms after last `change` event |
| Paste bypass | Lint executes immediately on paste (no debounce) |
| Re-entry guard | `lintInProgress` flag prevents concurrent lint invocations |
| Pending retry | If content changes during lint execution, a debounced re-lint is scheduled after completion |
| Staleness check | Lint is skipped when `(source, filePath, configVersion)` triple is identical to the last successful lint |
| Incomplete `uses` guard | While a line ends with bare `- uses:` (no action ref yet), JS defers `RunLint` to avoid known WASM AOT trap states during intermediate typing |
| Staleness non-update | Internal-error results do not update the staleness cache (allows retry on next keystroke) |
| Staleness invalidation | File-type change, fix application, URL fetch, and config change clear the staleness cache |

### 3.2 Runtime Death Detection

When the WASM runtime crashes (exits with non-zero code):

1. Set `runtimeAlive = false`
2. Stop all subsequent lint/fix calls
3. Display persistent error toast (60s duration) + insert inline error row into results table prompting page reload

Detection pattern: catch errors matching `"runtime already exited"` from WASM interop calls.

### 3.3 Apply All Fixes

- Calls `ApplyAllFixesWithNetworkAsync(source, filePath)` via WASM export
- If returned YAML differs from input: update editor, invalidate staleness, re-lint
- If unchanged: show informational toast (no fix was applicable or an error occurred)
- Network-dependent fixes (pinning via GitHub API, image digest resolution via OCI registries) require `enable-network` in the config; when enabled, resolves SHAs and digests concurrently before applying fixes
- Uses the currently active config (last successful `SetConfig` result)

### 3.4 Config Content-Hash Caching

`LintConfigYamlParser.Parse()` allocates internally (VYaml reader, dictionaries, lists). Re-parsing on every lint call would add unnecessary GC pressure on the constrained WASM heap. The WASM side caches the parsed config and only re-parses when meaningful content changes.

#### 3.4.1 Normalization Before Hashing

Before computing the content hash, the config YAML string is normalized:

1. Split into lines
2. Strip trailing whitespace from each line
3. Remove lines that are empty after stripping (blank lines)
4. Join remaining lines with `\n`

This ensures cosmetic edits (adding/removing blank lines, trailing spaces) do not trigger re-parse.

#### 3.4.2 Hash and Cache Strategy

| Step | Action |
|---|---|
| 1 | Normalize the incoming `configYaml` string |
| 2 | Compute XxHash64 of the normalized UTF-8 bytes |
| 3 | Compare hash to the previously cached hash |
| 4a | Hash matches → return cached diagnostics (skip parse entirely) |
| 4b | Hash differs → parse via `LintConfigLibrary.Validate()`, store new hash + parsed config + diagnostics |

#### 3.4.3 Cache Invalidation

- The config cache has exactly one slot (single-document playground)
- Empty/whitespace-only input resets to default config and clears the cache slot
- The cached config is retained across lint calls until explicitly changed via `SetConfig`

#### 3.4.4 Interaction with Lint Staleness

- JS side tracks a `configVersion` counter (incremented on each successful `SetConfig`)
- When config changes: JS invalidates `lastLintedSource` / `lastLintedFilePath` staleness cache and triggers re-lint
- The staleness triple becomes `(source, filePath, configVersion)` — any component change triggers re-lint

---

## 4. UI Specification

### 4.1 Feature Catalog

| Feature | Description |
|---|---|
| YAML editor | CodeMirror 5 with yaml mode, auto-grow (`viewportMargin: Infinity`), line numbers, active line highlight |
| Config editor | CodeMirror 5 with yaml mode, collapsible panel below YAML editor; edits debounced 500ms before `SetConfig` call |
| Real-time lint | Debounce 500ms, immediate on paste, staleness check |
| Results table | Position chip + severity chip (Error/Warning/Info color-coded) + message + ruleId chip + fixable chip per diagnostic; left-border tint by severity (`data-severity` attribute). Long messages that overflow 3 lines when rendered show a link-style **Show more** / **Show less** control (toggle does not trigger row jump; omitted when line-clamp would not hide text) |
| Gutter markers | Error = red (`--danger`), Warning = yellow (`--warning`), Info = blue (`--info`), CSS class-based |
| Row click jump | Clicking a diagnostic row moves editor cursor to that position |
| Loading indicator | "Loading WebAssembly binary..." shown until WASM runtime is ready |
| File type selector | `workflow` (`.github/workflows/test.yml`) / `action.yml` |
| Sample selector | Built-in YAML snippets: default, simple, minimal, fixPermissions, matrix, actionComposite. `actionComposite` auto-switches file type to `action.yml`; others switch to workflow. |
| Permalink (share) | v2: JSON `{v:2,y,c?,p?}` → pako zlib deflate → base64url hash; restores YAML + config + file path on load. v1 legacy: raw YAML deflate + standard base64 (config empty). P2: if URL/hash too long, retry YAML-only; else clipboard bundle. See §4.9. |
| URL fetch | Fetch remote YAML by URL with validation and GitHub blob→raw conversion |
| Toast notifications | Dismiss button + Escape key (capture phase), auto-dismiss with configurable duration |
| Apply all fixes | Offline autofix with priority ordering (network fixes skipped) |
| Version badge | Shown after WASM startup, links to GitHub Release page |
| Color theme | System / Light / Dark cycle with localStorage persistence |
| Runtime crash detection | Stops calls, shows reload prompt |
| Flow tab | Result / Flow tab switch in the results column. Flow renders the flow-json document as an SVG graph (D3) with two structural levels: the `needs` job DAG, and an **intra-job step flow** inside each job box (main lane top-to-bottom; `background: true` steps fork into dashed side lanes; `wait`/`wait-all` join them; `cancel` cuts them with a dashed red edge; `parallel` boundaries hold simultaneous children). Statically expanded matrices render as a stacked-card job box with a legs line (`N legs: …`, capped chips). Declared runtime settings render as a job info line (`⏱ 15m · perms: contents:read · env: production`, hidden at lod0, full text on hover) and as step label suffixes (`⏱5m` timeout, `↷` continue-on-error) whose markers survive label truncation. **LOD by zoom** (geometry never changes, only visibility): lod0 (< 0.55×) job boxes + `N steps` summary, lod1 (< 1.05×) step shapes + edges without labels, lod2 full labels. Interactions: d3.zoom pan/zoom (0.2–3×, fit-to-view on render), click job header / step node / parallel child → **selection highlight** (accent border, exactly one node at a time) + detail panel (matrix legs list, background note, node diagnostics) + **editor line highlight** (`flow-hl-line` background over the node's `line..endLine`, extended to the deepest descendant end line for parallel boundaries, boundary-spill lines trimmed via other nodes' start lines; editor scrolls the range into view; cleared on reselection, re-render, and switching back to the Result tab). **Diagnostic markers**: lint diagnostics map to flow nodes by source line (`line`/`endLine` from flow-json; innermost step wins, boundary-line overlaps resolved by greatest start line) — jobs get an aggregated `✖N ⚠N ℹN` header badge (visible at every LOD), steps get a severity dot with hover tooltip. Refreshed on tab activation and after each lint while the Flow tab is active; skipped when source + path + diagnostics unchanged. |

### 4.2 Toast System

- Container: `#toast-stack` (fixed position, top of viewport)
- Independent from diagnostic results — lint results table is never cleared by toast operations
- Dismiss: dedicated `button.toast__dismiss` or **Escape** key (document capture phase, dismisses topmost toast)
- ARIA: `role="alert"` for error, `role="status"` for success/info
- Auto-dismiss durations: error = 8s, success = 3.8s, info = 4.2s
- URLs in toast body text and diagnostic messages are auto-linkified; link clicks do not propagate to toast dismiss

### 4.3 URL Fetch

- **Validation** (`looksLikePlausibleHttpFetchUrl`):
  - Protocol: http(s) only
  - Valid hosts: `localhost`, IPv4, IPv6, hostnames with ≥ 2 labels
  - Empty input → button disabled, title "Enter a YAML URL first"
  - Invalid input → button disabled, title "Incomplete URL"
  - During fetch → both input and button disabled
- **GitHub blob→raw normalization**: `github.com/{owner}/{repo}/blob/{ref}/{path}` → `raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}`
- **Fetch options**: `mode: 'cors'`, `redirect: 'follow'`, `cache: 'no-store'`
- **Error handling**: HTTP failure or HTML content-type → toast notification (results pane preserved)
- **Enter key**: on empty/invalid input, shows info toast only (no fetch); restores focus to input after toast
- **Overlapping requests**: blocked via `fetchInFlight` flag

### 4.4 Color Theme

- **Default**: dark (`:root` tokens define dark palette)
- **System tracking**: `prefers-color-scheme: light` overrides via `:root:not([data-theme])` selector
- **Manual override**: footer button cycles **System → Light → Dark**
- **Persistence**: `localStorage` key `seiton-playground-color-mode` (`light`/`dark` stored; `system` removes key)
- **FOUC prevention**: inline `<script>` in `<head>` (before CSS) reads storage and sets `data-theme` + `meta[name=color-scheme]`
- **CodeMirror themes**: dark = `material-darker`, light = `default`. System mode tracks OS `change` event.
- **Gutter markers**: use `var(--danger)` / `var(--warning)` / `var(--info)` CSS custom properties

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
        editor pane (#editor-wrap > #editor + #apply-fixes-btn + #config-panel)
        results pane (.results-column > #loading + #results-tab-bar (Result/Flow tabs)
                      + #result-panel (#lint-result + #success-msg)
                      + #flow-panel (#flow-empty + #flow-graph + #flow-detail))
      </section>
    </main>
    <section .playground-about>
    <section .resources>
    <footer> (GitHub link, copyright, #theme-cycle-btn)
    <script type="module" src="main.js">
  </body>
</html>
```

### 4.7 Responsive Layout

| Breakpoint | Behavior |
|---|---|
| ≤ 639.98px | Controls row horizontal scroll, grid collapse to single column |
| ≤ 880px | Reduced padding, narrower layout |
| Desktop | Two-column grid: editor (left, YAML editor + collapsible config panel stacked vertically) + results (right, sticky with independent scroll, max 100dvh) |

### 4.8 External Dependencies (CDN)

| Library | Version | Purpose | Integrity |
|---|---|---|---|
| CodeMirror 5 | 5.65.16 | YAML editor (codemirror.min.js + yaml mode + active-line addon) | SRI hash pinned |
| CodeMirror material-darker | 5.65.16 | Dark theme | SRI hash pinned |
| pako | 2.1.0 | Permalink deflate/inflate (ESM import) | — |
| D3.js | 7.9.0 | Flow tab graph rendering (zoom/pan, selection, edge paths); UMD global `d3` | SRI hash pinned (sha512) |

CSS resources use non-blocking loading pattern (`media="print"` + `onload` swap + `<noscript>` fallback).

### 4.9 Share URL Payload (v2)

| Item | Value |
|---|---|
| Codec module | `share-payload.js` (browser); `PlaygroundSharePayload` in `Seiton.Playground.Core` (tests/benchmarks; must stay in sync) |
| v2 JSON keys | `v` (2), `y` (workflow YAML), optional `c` (config), optional `p` (file path) |
| Compression | pako / zlib deflate |
| v2 encoding | base64url (no `+` `/` padding) |
| v1 legacy decode | standard base64 + raw YAML bytes (no JSON wrapper) |
| Limits | hash ≤ 16384 chars; full URL ≤ 8192 chars |
| P2 fallback order | full v2 → YAML-only v2 → clipboard text bundle (no URL update) |
| Restore on load | YAML → editor; config → config panel + `SetConfig` after WASM ready; path → file selector when option exists |
| Decode failure | Toast + default sample YAML |

---

## 5. Deployment

### 5.1 Method

GitHub Actions `actions/deploy-pages` (Pages artifact approach). No `gh-pages` branch.

### 5.2 Workflow Triggers

| Trigger | Behavior |
|---|---|
| `push.tags: ["v*"]` | Automatic deploy on release tag |
| `workflow_dispatch` | Manual re-deploy of an existing tag (validates tag existence via API) |

### 5.3 Workflow Dispatch Behavior

- Tag input is normalized (accepts with or without `v` prefix)
- Tag existence is validated via GitHub API (`gh api`) before proceeding
- Each job has explicit `timeout-minutes`

### 5.4 Security Posture

- Top-level `permissions: {}` (least privilege)
- Per-job permissions: `contents: read`, `pages: write`, `id-token: write`
- Checkout with `persist-credentials: false`, `fetch-depth: 0`
- Action references pinned to full commit SHA (exception: `setup-dotnet@main` for pre-release SDK)
- `concurrency.group: pages` with `cancel-in-progress: false`

### 5.5 Static Hosting Requirements

- `.nojekyll` file required (protects `_framework/` directory from Jekyll underscore prefix filtering)
- Fingerprinted + non-fingerprinted assets both emitted (no server-side import map rewriting on GitHub Pages)
- Import map placeholder in HTML is rewritten by the SDK build

### 5.6 Repository Configuration

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
