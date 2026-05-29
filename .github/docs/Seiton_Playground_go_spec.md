# Seiton Playground Go Implementation Specification

> Go implementation specification for the playground contract defined in `Seiton_Playground_spec.md`. This document captures Go runtime structures, WASM interop design, project layout, and build/deploy configuration specific to the Go target. No implementation exists yet — this spec defines the target architecture.

> **Cross-document synchronization rule**: `Seiton_Playground_spec.md` is the source of truth for playground behavior. When this Go spec is updated, also review and update `Seiton_Playground_spec.md` in the same PR/commit scope.

---

## 0. Go Preamble

### 0.1 Contract

This document specifies how the Seiton Playground would be implemented in Go, targeting `GOOS=js GOARCH=wasm`. The reference implementation is [actionlint playground](https://github.com/rhysd/actionlint/tree/main/playground), which uses the same Go WASM approach.

### 0.2 Overview

The Seiton Playground Go implementation would provide:

1. **Single WASM binary** — compiled from `playground/main.go` with `GOOS=js GOARCH=wasm`
2. **`syscall/js` interop** — `js.FuncOf` exports registered on `window`
3. **Callback-based result delivery** — Go calls JS-provided callbacks rather than returning values
4. **`wasm_exec.js` runtime** — Go standard library WASM glue
5. **`wasm-opt` post-processing** — Binaryen optimization for binary size reduction

### 0.3 Structure

All playground code lives in a `playground/` directory at the repository root.

| File | Responsibility |
|---|---|
| `playground/main.go` | WASM entry point: register exports, run initial lint, block forever |
| `playground/index.html` | Static HTML shell (CodeMirror editor, results pane) |
| `playground/main.js` or `index.ts` | UI logic: debounce, render, theme, permalink, URL fetch |
| `playground/lib.d.ts` | TypeScript declarations for Go↔JS interop contract |
| `playground/Makefile` | Build commands (WASM compile, wasm-opt, TypeScript transpile) |
| `playground/deploy.bash` | Deployment script (build + optimize + copy to gh-pages) |

### 0.4 Design Decisions

1. **Single binary output** — Go compiles to one `main.wasm` file (no framework directory, no multi-assembly split).
2. **Callback pattern** — Go WASM cannot return complex objects across the boundary; instead, Go calls `window.onCheckCompleted(results)` after lint completes.
3. **Block forever** — `main()` ends with `select {}` to keep the Go WASM runtime alive. Without this, exported functions become unreachable.
4. **No Goroutines for lint** — Lint execution is synchronous and blocking. The single-threaded JS environment cannot benefit from goroutines.
5. **Panic recovery** — All exported functions wrap execution in `recover()` to prevent WASM runtime crash.

---

## 1. WASM Interop Layer

### 1.1 Export Pattern

Go uses `syscall/js` to register callable functions on the global object:

```go
package main

import "syscall/js"

func main() {
    window := js.Global()
    window.Set("runSeitonLint", js.FuncOf(runLint))
    window.Set("applySeitonFixes", js.FuncOf(applyAllFixes))
    window.Set("setSeitonConfig", js.FuncOf(setConfig))
    window.Set("getSeitonVersion", js.FuncOf(getVersion))

    // Keep runtime alive
    select {}
}
```

### 1.2 Exported Functions

| JS Name | Go Function | Parameters | Return Mechanism |
|---|---|---|---|
| `runSeitonLint` | `runLint` | `args[0]`: yamlSource (string), `args[1]`: filePath (string) | Calls `window.onLintCompleted(diagnosticsArray)` |
| `applySeitonFixes` | `applyAllFixes` | `args[0]`: yamlSource (string), `args[1]`: filePath (string) | Calls `window.onFixesCompleted(fixedYaml)` |
| `setSeitonConfig` | `setConfig` | `args[0]`: configYaml (string) | Calls `window.onConfigCompleted(diagnosticsArray)` |
| `getSeitonVersion` | `getVersion` | none | Returns `js.ValueOf(version)` directly |

### 1.3 Function Signatures

```go
func runLint(_ js.Value, args []js.Value) interface{} {
    defer func() {
        if r := recover(); r != nil {
            showError(fmt.Sprintf("internal error: %v", r))
        }
    }()
    source := args[0].String()
    filePath := args[1].String()
    // ... lint execution ...
    window.Call("onLintCompleted", toJSArray(diagnostics))
    return nil
}

func applyAllFixes(_ js.Value, args []js.Value) interface{} {
    defer func() {
        if r := recover(); r != nil {
            showError(fmt.Sprintf("internal error during fix: %v", r))
            window.Call("onFixesCompleted", args[0]) // return original
        }
    }()
    source := args[0].String()
    filePath := args[1].String()
    // ... fix execution ...
    window.Call("onFixesCompleted", js.ValueOf(fixedYaml))
    return nil
}
```

### 1.4 Result Marshalling

Diagnostic objects are constructed as JS objects via `syscall/js`:

```go
func toJSObject(d *Diagnostic) js.Value {
    obj := js.Global().Get("Object").New()
    obj.Set("message", d.Message)
    obj.Set("line", d.Line)
    obj.Set("column", d.Column)
    obj.Set("severity", d.Severity)
    if d.RuleID != "" {
        obj.Set("ruleId", d.RuleID)
    } else {
        obj.Set("ruleId", js.Null())
    }
    obj.Set("fixable", d.Fixable)
    if d.FixDescription != "" {
        obj.Set("fixDescription", d.FixDescription)
    } else {
        obj.Set("fixDescription", js.Null())
    }
    return obj
}
```

Alternative: serialize to JSON string in Go, decode in JS. Tradeoff: one allocation for JSON string vs N allocations for JS objects.

### 1.5 Error Communication

```go
func showError(msg string) {
    js.Global().Call("onSeitonError", js.ValueOf(msg))
}
```

JS registers `window.onSeitonError` before loading WASM to handle pre-runtime and runtime errors.

---

## 2. Lint Engine Integration

### 2.1 Engine Lifecycle

```go
var (
    engine *LintEngine
    once   sync.Once
)

func getEngine() *LintEngine {
    once.Do(func() {
        engine = NewLintEngine()
    })
    return engine
}
```

- Single engine instance, initialized lazily on first lint call
- Go `sync.Once` is safe even in single-threaded WASM (no-op contention)
- Engine reuse avoids repeated rule setup cost

### 2.2 Input Normalization

- Empty `source`: treated as empty YAML (returns zero diagnostics)
- Empty `filePath`: defaults to `.github/workflows/test.yml`

### 2.3 Incremental Parse

If incremental parsing is implemented for Go (per `Seiton_Parser_go_spec.md`), the playground would:

- Maintain a `*IncrementalContext` as a package-level variable
- Reset on file-path change
- Reuse section hashes for unchanged root keys

If not implemented, full parse on every invocation (acceptable for Go WASM given single-binary size advantage).

### 2.4 Config Content-Hash Caching

Config parsing allocates (YAML reader state, maps, slices). To avoid unnecessary allocation on every lint call, the Go implementation caches the parsed config alongside an XXH64 hash of the normalized config string.

**Package-level state:**

```go
var (
    cachedConfigHash uint64
    cachedConfig     *LintConfig
    cachedConfigDiag []Diagnostic
)
```

**setConfig algorithm:**

1. If input is empty/whitespace-only: reset to default config, clear hash, call `window.onConfigCompleted([])`
2. Normalize the input (strip trailing whitespace per line, remove blank lines)
3. Compute XXH64 of the normalized bytes
4. If hash matches `cachedConfigHash`: call callback with `cachedConfigDiag` immediately (skip parse)
5. Parse config via `ValidateConfig(configYaml, "seiton.yaml")`
6. On success: update `cachedConfig`, `cachedConfigHash`, `cachedConfigDiag = []`
7. On validation errors: keep previous `cachedConfig`, serialize diagnostics to `cachedConfigDiag`, do NOT update hash

**Normalization** (identical to C# spec §2.1.1):

- Split on `\n`
- TrimRight each line (whitespace)
- Remove empty lines
- Join with `\n`

**Integration with `runLint`/`applyAllFixes`:**

- Both use `cachedConfig` (or default) when invoking the lint engine
- Config change does NOT invalidate incremental parse context (config affects rule evaluation, not YAML structure)

---

## 3. Build and Toolchain

### 3.1 WASM Compilation

```makefile
GOOS=js GOARCH=wasm go build -o playground/main.wasm ./playground/
```

Build tags may exclude platform-specific code:

```go
//go:build js && wasm
```

### 3.2 wasm-opt Post-Processing

```bash
wasm-opt -O --enable-bulk-memory playground/main.wasm -o playground/main.wasm
```

- Binaryen `wasm-opt` reduces binary size by 10–30%
- `--enable-bulk-memory` required for Go WASM output
- Requires Binaryen installation (`apt install binaryen` or manual)

### 3.3 Runtime Glue

`wasm_exec.js` is copied from the Go SDK:

```bash
cp "$(go env GOROOT)/misc/wasm/wasm_exec.js" playground/lib/js/
```

This file provides:
- `Go` class with `importObject` for WebAssembly instantiation
- Memory management, syscall emulation
- `run(instance)` method to start Go main

### 3.4 JS Initialization Flow

```javascript
const go = new Go();
const result = await WebAssembly.instantiateStreaming(
    fetch('main.wasm'),
    go.importObject
);
go.run(result.instance);
// After this, window.runSeitonLint is available
```

Safari fallback (no `instantiateStreaming`):

```javascript
const resp = await fetch('main.wasm');
const bytes = await resp.arrayBuffer();
const result = await WebAssembly.instantiate(bytes, go.importObject);
go.run(result.instance);
```

### 3.5 Prerequisites

| Tool | Purpose |
|---|---|
| Go 1.22+ | WASM compilation |
| Binaryen (`wasm-opt`) | Binary size optimization |
| Node.js (optional) | TypeScript transpilation for UI code |

---

## 4. Project Configuration

### 4.1 Build Constraints

```go
//go:build js && wasm

package main
```

Platform-specific exclusions:
- `HttpClient` / network-dependent rules: excluded via build tag `!wasm`
- File system operations: excluded (browser has no FS)

### 4.2 Makefile

```makefile
.PHONY: wasm clean serve

WASM_OUT = playground/main.wasm

wasm:
	GOOS=js GOARCH=wasm go build -o $(WASM_OUT) ./playground/
	wasm-opt -O --enable-bulk-memory $(WASM_OUT) -o $(WASM_OUT)

clean:
	rm -f $(WASM_OUT)

serve:
	python3 -m http.server 8080 --directory playground/
```

### 4.3 Output Artifacts

```
playground/
  index.html
  main.js (or transpiled from index.ts)
  style.css
  main.wasm            ← single compiled+optimized binary
  lib/js/wasm_exec.js  ← Go runtime glue
  favicon.png, ogp.png
```

---

## 5. Size Optimization

| # | Technique | Effect |
|---|---|---|
| 1 | `wasm-opt -O --enable-bulk-memory` | 10–30% size reduction |
| 2 | `-ldflags="-s -w"` | Strip debug symbols and DWARF |
| 3 | Minimal imports (avoid `fmt` in hot paths) | Reduces binary bloat from reflection |
| 4 | `//go:build` exclusion of unused packages | Prevents dead code inclusion |
| 5 | TinyGo (alternative) | 50–70% smaller, but limited stdlib support |

### 5.1 Expected Size

| Configuration | Expected Size |
|---|---|
| Standard Go + wasm-opt + ldflags strip | **3–8 MB** |
| Reference: actionlint main.wasm (after wasm-opt) | ~3 MB |
| TinyGo (if viable) | **1–3 MB** |

### 5.2 TinyGo Consideration

TinyGo produces significantly smaller WASM binaries but has limitations:
- Incomplete `reflect` support (relevant if linter uses reflection)
- Incomplete `sync` package (acceptable for single-threaded WASM)
- Different GC behavior (conservative, may affect long-running sessions)

Decision: start with standard Go; evaluate TinyGo if binary size exceeds 5 MB.

---

## 6. Deployment

### 6.1 GitHub Pages via gh-pages Branch

Unlike the C# implementation (which uses `actions/deploy-pages` artifact approach), the Go playground traditionally deploys to a `gh-pages` branch:

```bash
# deploy.bash pattern (from actionlint reference)
git checkout gh-pages
cp -r playground/dist/* .
git add .
git commit -m "Deploy playground"
git push origin gh-pages
```

Alternatively, the same `actions/deploy-pages` artifact approach can be used.

### 6.2 Workflow

```yaml
name: Deploy Playground

on:
  push:
    tags: ["v*"]
  workflow_dispatch:

permissions: {}

jobs:
  build:
    permissions:
      contents: read
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@<sha>
      - uses: actions/setup-go@<sha>
        with:
          go-version-file: go.mod
      - name: Build WASM
        run: make -C playground wasm
      - name: Add .nojekyll
        run: touch playground/.nojekyll
      - uses: actions/upload-pages-artifact@<sha>
        with:
          path: playground/

  deploy:
    needs: build
    permissions:
      pages: write
      id-token: write
    runs-on: ubuntu-24.04
    environment:
      name: github-pages
    steps:
      - uses: actions/deploy-pages@<sha>
```

### 6.3 Static Hosting Notes

- No `_framework/` directory (Go produces single `main.wasm`)
- `.nojekyll` still needed if any paths start with underscore
- No import map concerns (single JS entry point)

---

## 7. Comparison with C# Implementation

| Aspect | C# (.NET WASM) | Go |
|---|---|---|
| WASM output | `_framework/` directory (runtime + assemblies) | Single `main.wasm` |
| Binary size (optimized) | 2–5 MB (Brotli) | 3–8 MB (wasm-opt) |
| JS glue | `dotnet.js` (auto-generated, complex) | `wasm_exec.js` (simple, vendored from SDK) |
| Interop pattern | `[JSExport]` returning values | `js.FuncOf` + callback invocation |
| Return mechanism | Direct return (`byte[]`, `string`) | Callback (`window.onLintCompleted(...)`) |
| Error handling | `try/catch` → safe return value | `recover()` → callback with original input |
| Runtime lifecycle | Managed by dotnet.js loader | `select {}` in main (manual keep-alive) |
| AOT | IL→WASM AOT compilation | Native WASM from Go compiler |
| Post-optimization | Brotli compression | `wasm-opt` (Binaryen) |
| Build prerequisite | `dotnet workload install wasm-tools` | Go SDK + Binaryen |
| Safari support | Handled by dotnet.js | Manual `instantiateStreaming` fallback |

---

## 8. Lessons Learned (Go-Specific)

### 8.1 `select {}` Is Required to Keep the Runtime Alive

Without `select {}` (or equivalent blocking construct) at the end of `main()`, the Go WASM runtime exits immediately after `main` returns. All registered `js.FuncOf` functions become unreachable.

### 8.2 `js.FuncOf` Must Not Panic

An unrecovered panic in a `js.FuncOf` callback crashes the Go WASM runtime. Unlike C# where `try/catch` prevents propagation, Go requires explicit `defer recover()` in every exported function.

### 8.3 `syscall/js` Object Creation Cost

Creating JS objects via `js.Global().Get("Object").New()` involves crossing the WASM/JS boundary per property. For large diagnostic arrays, JSON serialization in Go (`encoding/json`) + `JSON.parse` in JS may be more efficient than per-field object construction.

Benchmark both approaches:
- Small results (< 10 diagnostics): per-field object construction is fine
- Large results (> 50 diagnostics): JSON string serialization is preferred

### 8.4 `fmt.Sprintf` Bloats Binary Size

The `fmt` package pulls in significant reflection machinery. For WASM builds, prefer `strconv` + string concatenation for simple formatting, or `strings.Builder` for complex cases.

### 8.5 wasm-opt Requires `--enable-bulk-memory`

Go WASM output uses bulk memory operations. Running `wasm-opt` without `--enable-bulk-memory` produces a broken binary that fails at runtime with opcode errors.

### 8.6 Safari `instantiateStreaming` Unavailability

Older Safari versions do not support `WebAssembly.instantiateStreaming`. The JS initialization code must include an `arrayBuffer` fallback path (as demonstrated in actionlint's implementation).

---

## 9. References

- [actionlint playground source](https://github.com/rhysd/actionlint/tree/main/playground)
- [Go WebAssembly wiki](https://go.dev/wiki/WebAssembly)
- [syscall/js package](https://pkg.go.dev/syscall/js)
- [wasm_exec.js documentation](https://github.com/nicolo-ribaudo/pako)
- [Binaryen wasm-opt](https://github.com/WebAssembly/binaryen)
- [TinyGo WASM](https://tinygo.org/docs/guides/webassembly/)
