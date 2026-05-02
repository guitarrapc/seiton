# Seiton Playground (gh-pages) 仕様

> seiton の lint 機能をブラウザ上で体験できる Web Playground を GitHub Pages にデプロイする仕様。
> actionlint playground を参照実装とし、「ブラウザ内完結・サーバー不要」の構成を採用する。

---

## 1. 背景と決定

### 1.1 動機

- actionlint は Go→WASM でブラウザ内 lint を実現する playground を gh-pages に公開している
- seiton も同等の体験（ブラウザ上で YAML を入力→即座に lint 結果を表示）を提供したい
- サーバーサイド不要（WASM 実行）にすることで、ユーザーの入力データが外部に送信されない安全な構成になる

### 1.2 アプローチ選定

| アプローチ | 概要 | バイナリサイズ | 成熟度 | 判定 |
|---|---|---|---|---|
| A. Blazor WebAssembly | Blazor フレームワーク経由で .NET ランタイムをブラウザに配信 | 5–15 MB (trimmed) | 安定 | **却下** — ファーストロードが遅く、バイナリサイズが大きい。実際に試した結果、改善困難と判断 |
| **B. `wasm-experimental` (wasmbrowser)** | `dotnet new wasmbrowser` テンプレート。Blazor を使わず `[JSImport]`/`[JSExport]` で直接 JS ↔ .NET interop | 1–5 MB (trimmed+AOT) | 実験的 (API は .NET 7+ で安定) | **採用** — 最小構成で actionlint と同等のアーキテクチャを実現可能 |
| C. NativeAOT→WASM (Emscripten) | dotnet/runtimelab の実験的コンパイラ | 最小 | 未成熟 | **却下** — プロダクション非推奨、ツールチェイン不安定 |

### 1.3 決定

**Option B (`wasm-experimental` / WebAssembly Browser App)** を採用する。

- 参照ドキュメント: [JavaScript [JSImport]/[JSExport] interop with a WebAssembly Browser App project](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- ランタイム構成: [Configuring and hosting .NET WebAssembly applications](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md)

このアプローチは上記 MS ドキュメントの内容そのもの。Blazor フレームワークに依存せず、`Microsoft.NET.Sdk.WebAssembly` SDK と `[JSImport]`/`[JSExport]` 属性で JS ↔ C# の相互呼出を行う。`dotnet publish` の出力（`_framework/` ディレクトリ）をそのまま静的ファイルとしてホスティングする。

---

## 2. アーキテクチャ

### 2.1 actionlint との対応

```
actionlint                              seiton
─────────                               ──────
Go ライブラリ (actionlint pkg)           Seiton.Core (C# ライブラリ)
  ↓ GOOS=js GOARCH=wasm                   ↓ dotnet publish (browser-wasm)
main.wasm (単一 WASM バイナリ)           _framework/ (dotnet.native.wasm + trimmed DLLs)
  ↓                                        ↓
playground/main.go                       Seiton.Playground/LintInterop.cs
  (Go→JS: window.runActionlint)            ([JSExport] RunLint)
  ↓                                        ↓
wasm_exec.js (Go WASM glue)             dotnet.js (dotnet WASM glue, 自動生成)
  ↓                                        ↓
index.ts + CodeMirror                    main.js + CodeMirror
  ↓                                        ↓
gh-pages (手動 deploy.bash)              gh-pages (GitHub Actions)
```

### 2.2 処理フロー

```
[ブラウザ]
  1. index.html ロード
  2. main.js が dotnet.js を import → .NET WASM ランタイム起動
  3. main.js が [JSExport] された C# メソッドへの参照を取得
  4. ユーザーが CodeMirror エディタに YAML を入力
  5. debounce 後、JS → C# の runLint(yamlSource) を呼出
  6. C# 側: LintEngine.Check(utf8Yaml, filePath) 実行
  7. C# → JS: 診断結果の配列を返却
  8. JS 側: 結果をテーブルに表示 + エディタのガターにマーカー表示
```

### 2.3 Seiton.Core の WASM 互換性

Seiton.Core のコードを WASM 環境で実行する上での互換性評価:

| 項目 | 互換性 | 備考 |
|---|---|---|
| `stackalloc` | ✅ 問題なし | .NET WASM ランタイムでサポート |
| `System.Runtime.CompilerServices.Unsafe` | ✅ 問題なし | BCL の一部として含まれる |
| `MemoryMarshal` | ✅ 問題なし | BCL の一部として含まれる |
| `XxHash64.cs` (scalar impl) | ✅ 問題なし | SIMD 不使用、純粋な算術演算のみ |
| `ReadOnlySpan<byte>` / UTF-8 比較 | ✅ 問題なし | ランタイムサポートあり |
| VYaml 1.2.0 | ⚠️ 要検証 | 純粋 C# だが WASM での動作テストが必要 |
| `HttpClient` (OnlineAudit) | ⚠️ 制限あり | ブラウザの fetch API 経由になるため CORS 制約。Playground ではオフライン lint のみ使用 |
| SSE/AVX/Vector intrinsics | ✅ 該当なし | Seiton.Core では使用していない |

結論: **Seiton.Core は WASM 互換。OnlineAudit（ネットワーク依存ルール）を無効化すれば、そのまま動作する見込み。**

---

## 3. プロジェクト構成

### 3.1 ディレクトリ構成

```
src/Seiton.Playground/
  Seiton.Playground.csproj        ← WebAssembly Browser App プロジェクト
  LintInterop.cs                  ← [JSExport] lint API (C# → JS bridge)
  Program.cs                      ← WASM エントリポイント
  wwwroot/
    index.html                    ← メインページ (CodeMirror エディタ + 結果表示)
    main.js                       ← JS エントリポイント (dotnet.js import + UI ロジック)
    style.css                     ← スタイルシート
```

### 3.2 プロジェクトファイル

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- サイズ最適化 -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <InvariantTimezone>true</InvariantTimezone>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
    <RunAOTCompilation>true</RunAOTCompilation>

    <!-- Webcil 形式 (ファイアウォール/ウイルススキャナ互換) -->
    <WasmEnableWebcil>true</WasmEnableWebcil>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Seiton.Core\Seiton.Core.csproj" />
  </ItemGroup>

</Project>
```

**プロパティの説明**:

| プロパティ | 値 | 理由 |
|---|---|---|
| `Sdk` | `Microsoft.NET.Sdk.WebAssembly` | `wasmbrowser` テンプレートの SDK。Blazor フレームワーク不使用 |
| `AllowUnsafeBlocks` | `true` | `[JSImport]`/`[JSExport]` の Roslyn コードジェネレータが必要とする |
| `InvariantGlobalization` | `true` | ICU データ不要 (数 MB 節約)。Seiton CLI も同設定 |
| `InvariantTimezone` | `true` | タイムゾーンDB 不要 (サイズ削減) |
| `PublishTrimmed` + `TrimMode=full` | `true` / `full` | 未使用コード除去。最大限のサイズ削減 |
| `RunAOTCompilation` | `true` | IL→WASM AOT コンパイル。実行時パフォーマンス向上。publish 時のみ有効 |
| `WasmEnableWebcil` | `true` | .dll ではなく .wasm 形式で配信 (デフォルト true、明示) |

### 3.3 前提条件 (workload)

```shell
dotnet workload install wasm-tools
dotnet workload install wasm-experimental   # テンプレート使用時のみ
```

`wasm-tools` は AOT コンパイル・ネイティブリビルド・trimming に必要。`wasm-experimental` は `dotnet new wasmbrowser` テンプレートを利用する場合のみ必要（手動でプロジェクトを構成する場合は不要）。

---

## 4. 実装コード

### 4.1 C# 側: LintInterop.cs

`[JSExport]` で JS から呼び出せる lint 関数を公開する。

```csharp
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Seiton.Core.Linting;

namespace Seiton.Playground;

public static partial class LintInterop
{
    private static readonly LintEngine s_engine = new();

    /// <summary>
    /// JS から呼び出される lint エントリポイント。
    /// YAML 文字列を受け取り、診断結果を JSON 文字列で返す。
    /// </summary>
    [JSExport]
    public static string RunLint(string yamlSource, string filePath)
    {
        var utf8Yaml = Encoding.UTF8.GetBytes(yamlSource);
        var result = s_engine.Check(utf8Yaml, filePath);

        // 診断結果を JSON シリアライズして返す
        var diagnostics = new List<object>();
        foreach (var diag in result.Diagnostics)
        {
            diagnostics.Add(new
            {
                message = diag.Message,
                line = diag.Line,
                column = diag.Column,
                severity = diag.Severity.ToString(),
                ruleId = diag.RuleId,
            });
        }

        return JsonSerializer.Serialize(diagnostics);
    }
}
```

**設計判断**:
- `LintEngine` インスタンスを `static` フィールドで再利用 (内部ルールのセットアップコストを償却)
- 戻り値は JSON 文字列。JS 側で `JSON.parse()` する。`[JSExport]` は primitive 型と string の marshalling を直接サポートするが、複雑なオブジェクトのやり取りは JSON 文字列経由が最もシンプル
- `filePath` を引数にすることで、ユーザーが `.github/workflows/test.yml` と `action.yml` を選択可能にできる

### 4.2 C# 側: Program.cs

```csharp
using System.Runtime.InteropServices.JavaScript;

// WASM エントリポイント — ランタイム初期化のみ
// LintInterop の [JSExport] メソッドは dotnet.js 経由で JS に公開される
Console.WriteLine("Seiton WASM runtime initialized.");
```

`wasmbrowser` テンプレートでは `Program.Main` が `runMain()` 時に実行される。Lint 機能自体は `[JSExport]` 経由で JS から呼び出す。

### 4.3 JS 側: main.js

```javascript
import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig, runMain } = await dotnet
    .withApplicationArguments("playground")
    .create();

const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
await runMain();

// ── CodeMirror セットアップ ──
const editor = CodeMirror(document.getElementById('editor'), {
    mode: 'yaml',
    theme: 'material-darker',
    lineNumbers: true,
    lineWrapping: true,
    autofocus: true,
    styleActiveLine: true,
    gutters: ['CodeMirror-linenumbers', 'error-marker'],
    extraKeys: {
        Tab(cm) {
            cm.execCommand(cm.somethingSelected() ? 'indentMore' : 'insertSoftTab');
        },
    },
    value: getDefaultSource(),
});

// ── Lint 実行 (debounce) ──
const DEBOUNCE_MS = 300;
let debounceId = null;

editor.on('change', (_cm, changeObj) => {
    if (debounceId !== null) {
        clearTimeout(debounceId);
    }

    const run = () => {
        debounceId = null;
        runLint();
    };

    if (changeObj.origin === 'paste') {
        run(); // ペースト時は即時実行
    } else {
        debounceId = setTimeout(run, DEBOUNCE_MS);
    }
});

function runLint() {
    const source = editor.getValue();
    const filePath = getSelectedFilePath();

    try {
        const json = exports.Seiton.Playground.LintInterop.RunLint(source, filePath);
        const diagnostics = JSON.parse(json);
        renderResults(diagnostics);
    } catch (err) {
        showToast(err.message, 'error');
    }
}

function renderResults(diagnostics) {
    const body = document.getElementById('lint-result-body');
    const successMsg = document.getElementById('success-msg');

    body.textContent = '';
    editor.clearGutter('error-marker');

    if (diagnostics.length === 0) {
        successMsg.style.display = 'block';
        return;
    }
    successMsg.style.display = 'none';

    for (const diag of diagnostics) {
        const row = document.createElement('tr');
        row.addEventListener('click', () => {
            editor.setCursor({ line: diag.line - 1, ch: diag.column - 1 });
            editor.focus();
        });

        // 位置タグ
        const posCell = document.createElement('td');
        const posTag = document.createElement('span');
        posTag.className = 'tag is-dark is-medium';
        posTag.textContent = `line:${diag.line}, col:${diag.column}`;
        posCell.appendChild(posTag);
        row.appendChild(posCell);

        // メッセージ + ルール ID
        const descCell = document.createElement('td');
        descCell.textContent = diag.message;
        const kindTag = document.createElement('span');
        kindTag.className = 'tag is-dark';
        kindTag.textContent = diag.ruleId;
        kindTag.style.marginLeft = '4px';
        descCell.appendChild(kindTag);
        row.appendChild(descCell);

        body.appendChild(row);

        // ガターマーカー
        const marker = document.createElement('div');
        marker.style.color = '#ff5370';
        marker.textContent = '●';
        editor.setGutterMarker(diag.line - 1, 'error-marker', marker);
    }
}

function showToast(message, variant = 'info') {
    /* `#toast-stack`: ラッパー + `.toast__body` + `button.toast__dismiss`。実装は `wwwroot/main.js` */
}

function getSelectedFilePath() {
    const select = document.getElementById('filetype-select');
    return select ? select.value : '.github/workflows/test.yml';
}

function getDefaultSource() {
    // URL hash からの復元（ツールバー共有ボタンで `history.replaceState` したハッシュ）
    if (window.location.hash) {
        try {
            const b64 = window.location.hash.slice(1);
            const compressed = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
            const decompressed = pako.inflate(compressed);
            return new TextDecoder().decode(decompressed);
        } catch { /* ignore */ }
    }

    return `# Paste your workflow YAML to check with seiton

on:
  push:
    branch: main
    tags:
      - 'v\\d+'
jobs:
  test:
    strategy:
      matrix:
        os: [macos-latest, linux-latest]
    runs-on: \${{ matrix.os }}
    steps:
      - run: echo "hello"
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
`;
}

// 初回 lint 実行
document.getElementById('loading').style.display = 'none';
runLint();
```

### 4.4 HTML: index.html

```html
<!DOCTYPE html>
<html lang="en">
  <head>
    <title>seiton playground</title>
    <meta charset="utf-8"/>
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <link rel="stylesheet" href="lib/css/codemirror.css">
    <link rel="stylesheet" href="lib/css/material-darker.css">
    <link rel="stylesheet" href="style.css">
    <script src="lib/js/codemirror.js"></script>
    <script src="lib/js/active-line.js"></script>
    <script src="lib/js/yaml.js"></script>
    <script src="lib/js/pako.min.js"></script>
    <!-- _framework/dotnet.js のプリロード -->
    <link rel="preload" href="./_framework/dotnet.boot.js" as="fetch" crossorigin="anonymous">
  </head>
  <body>
    <nav id="header-bar">
      <header>
        <h1>
          <a href="https://github.com/guitarrapc/seiton">seiton</a> playground
        </h1>
        <h2>Security-focused linter &amp; fixer for GitHub Actions</h2>
      </header>
      <div id="controls">
        <div class="controls-row controls-row--primary">
          <button id="permalink-btn" type="button" class="toolbar-icon-btn"
                  title="Share — copy link to clipboard; YAML is stored in URL hash"
                  aria-label="Share — copy link to clipboard; YAML is stored in URL hash">
            <svg class="toolbar-icon-btn__svg" viewBox="0 0 24 24" aria-hidden="true"><!-- 共有アイコン path は実装の `wwwroot/index.html` と同じ --></svg>
          </button>
          <span class="fetch-group" role="group" aria-label="Fetch YAML by URL">
            <input type="url" id="url-input" aria-label="YAML URL" placeholder="https://…"/>
            <button id="fetch-btn" type="button" class="toolbar-icon-btn" disabled
                    title="Enter a YAML URL first"
                    aria-label="Fetch and lint YAML — enter a URL first">
              <svg class="toolbar-icon-btn__svg" viewBox="0 0 24 24" aria-hidden="true"><!-- 虫眼鏡 path は同上 --></svg>
            </button>
          </span>
        </div>
        <select id="filetype-select">
          <option value=".github/workflows/test.yml" selected>workflow</option>
          <option value="action.yml">action.yml</option>
        </select>
      </div>
    </nav>
    <div id="toast-stack" class="toast-stack" aria-live="polite"></div>
    <main>
      <section id="linter">
        <div id="editor" class="split-pane"></div>
        <div class="split-pane">
          <div id="loading" class="notification">Loading WebAssembly binary...</div>
          <table id="lint-result" class="table">
            <tbody id="lint-result-body"></tbody>
          </table>
          <div id="success-msg" class="notification" style="display:none">No errors found.</div>
        </div>
      </section>
    </main>
    <footer>
      <p>
        <a href="https://github.com/guitarrapc/seiton">seiton</a> — Security-focused linter & fixer for GitHub Actions.
      </p>
    </footer>
    <script type="module" src="main.js"></script>
  </body>
</html>
```

---

## 5. ビルドとパブリッシュ

### 5.1 開発時 (ローカル)

```shell
# wasm-tools workload が必要
dotnet workload install wasm-tools

# ビルド
dotnet build src/Seiton.Playground

# ローカルサーバー起動 (dotnet run は WasmDevServer を起動する)
dotnet run --project src/Seiton.Playground
# → http://localhost:5292/index.html
```

`dotnet run` は開発用 WASM サーバーを起動する。AOT コンパイルは行われないため高速。

### 5.2 パブリッシュ (リリースビルド)

```shell
dotnet publish src/Seiton.Playground -c Release
```

出力先: `src/Seiton.Playground/bin/Release/net10.0/browser-wasm/AppBundle/`

このディレクトリ構造:

```
AppBundle/
  index.html
  main.js
  style.css
  lib/                      ← wwwroot からコピーされた静的ファイル
  _framework/
    dotnet.js               ← .NET WASM ランタイムローダー
    dotnet.native.js        ← Emscripten POSIX エミュレーション層
    dotnet.runtime.js       ← ブラウザ統合
    dotnet.boot.js          ← アセットリスト + integrity hash
    dotnet.native.wasm      ← コンパイル済み .NET ランタイム (Mono)
    System.Private.CoreLib.wasm   ← BCL コア
    Seiton.Core.wasm        ← trimmed Seiton.Core
    Seiton.Playground.wasm  ← playground アプリ
    ...                     ← その他 trimmed BCL アセンブリ
```

### 5.3 サイズ見込み

| 構成 | 予想サイズ (Brotli 圧縮後) |
|---|---|
| trimmed + AOT + InvariantGlobalization + InvariantTimezone | **2–5 MB** |
| 参考: actionlint main.wasm (wasm-opt 後) | 約 3 MB |

`InvariantGlobalization` と `InvariantTimezone` で ICU データとタイムゾーン DB を除外できるため、大幅にサイズ削減できる。Seiton.Core は純粋な YAML パース + lint のみ（UI フレームワーク不使用）なので、trimming が効果的に働く見込み。

---

## 6. GitHub Pages デプロイ

### 6.1 デプロイ方式

GitHub Actions の `actions/deploy-pages` を使用（新しい Pages artifact 方式）。`gh-pages` ブランチは使わない。

actionlint は手動の `deploy.bash` でローカルから `gh-pages` ブランチに push する方式だが、seiton は GitHub Actions で自動デプロイする。

### 6.2 ワークフロー

```yaml
# .github/workflows/playground.yml
name: Deploy Playground

on:
  push:
    branches: [main]
    paths:
      - 'src/Seiton.Playground/**'
      - 'src/Seiton.Core/**'
  workflow_dispatch:  # 手動トリガーも可能

permissions:
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install wasm-tools workload
        run: dotnet workload install wasm-tools

      - name: Publish Playground
        run: dotnet publish src/Seiton.Playground -c Release

      - name: Add .nojekyll
        run: touch src/Seiton.Playground/bin/Release/net10.0/browser-wasm/AppBundle/.nojekyll

      - uses: actions/upload-pages-artifact@v3
        with:
          path: src/Seiton.Playground/bin/Release/net10.0/browser-wasm/AppBundle

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deploy.outputs.page_url }}
    steps:
      - id: deploy
        uses: actions/deploy-pages@v4
```

**ポイント**:
- `.nojekyll` ファイルを追加して、GitHub Pages の Jekyll 処理を無効化（`_framework/` ディレクトリが `_` プレフィックスのため必須）
- `paths` フィルターで Playground/Core に変更があった時のみトリガー
- `concurrency` で同時デプロイを防止

### 6.3 リポジトリ設定

GitHub リポジトリの Settings → Pages で:
- **Source**: GitHub Actions を選択

---

## 7. サイズ最適化戦略

優先度順:

| # | 施策 | 効果 | 備考 |
|---|---|---|---|
| 1 | `InvariantGlobalization=true` | ICU データ除外 (数 MB) | Seiton CLI と同設定。lint に文化依存処理不要 |
| 2 | `InvariantTimezone=true` | タイムゾーン DB 除外 | lint にタイムゾーン不要 |
| 3 | `PublishTrimmed=true` + `TrimMode=full` | 未使用コード除去 | `full` モードで最大限の trimming |
| 4 | `RunAOTCompilation=true` | IL→WASM、実行時 JIT 不要 | publish 時のみ有効。ビルド時間増加だが実行速度向上 |
| 5 | Brotli 圧縮 | 転送サイズ 60-70% 削減 | `dotnet publish` が自動で `.br` ファイル生成。GitHub Pages が `Accept-Encoding: br` に対応 |
| 6 | `_framework` のプリロード | 初期表示高速化 | `<link rel="preload">` で並列ダウンロード |

### 7.1 Trimming 互換性

Seiton.Core は以下の特性により trimming 親和性が高い:
- リフレクション不使用（パーサーは手書き、ルールは静的登録）
- JSON シリアライゼーション不使用（VYaml で YAML パース）
- DI コンテナ不使用

Playground 側の `JsonSerializer.Serialize()` は匿名型なので、必要に応じて Source Generator 対応にする。

---

## 8. UI 機能

actionlint playground を参照し、以下の機能を実装する:

### 8.1 必須機能 (MVP)

| 機能 | actionlint での実装 | seiton での実装 |
|---|---|---|
| YAML エディタ | CodeMirror 5 (yaml mode) | CodeMirror 5 or 6 (yaml mode) |
| リアルタイム lint | debounce 300ms → `runActionlint()` | debounce 300ms → `LintInterop.RunLint()` |
| 結果テーブル | 行番号 + メッセージ + kind タグ | 行番号 + メッセージ + ruleId タグ + severity |
| ガターマーカー | エラー行に赤丸 | エラー行に赤丸 |
| 行クリックジャンプ | テーブル行クリック → エディタカーソル移動 | 同左 |
| ローディング表示 | "Loading WebAssembly binary..." | 同左 |
| ファイル種別選択 | なし (workflow のみ) | workflow / action.yml 切替セレクト |

### 8.2 追加機能 (Post-MVP)

| 機能 | 説明 |
|---|---|
| 共有 URL（permalink） | `#permalink-btn`。**ラベルは共有（アップロード風）SVG アイコンのみ** — `title` / `aria-label` で説明。クリック後に `history.replaceState` で hash を更新しつつ、**完全な現在ページ URL（`location.href`）をクリップボードへコピー**（同期の一時 `textarea` + `execCommand('copy')` を優先し、無効時は `navigator.clipboard.writeText`。いずれも拒否時はユーザーにアドレスバーからコピーできる旨をツールチップで示す）。DOM id は後方互換のため `permalink-btn`。 |
| GitHub/Gist URL からの読み込み | `#url-input` と `#fetch-btn`。**ボタンは虫眼鏡 SVG のみ**。**空、または明らかに未完成／不正な http(s)**（`new URL` 失敗、非 http(s)、ホストが単一ラベルのみ等）の間は **`disabled`** — `localhost`・IP（v4/v6）・二段以上のホストラベルのときだけ有効化（`main.js` の `looksLikePlausibleHttpFetchUrl`）。空時は `title` / `aria-label` が「先に URL」、不正時は「不完全」旨。**フェッチ中**（`main.js` の `fetchInFlight`）は **両方とも `disabled`** とし、`input`/paste が再度有効にしない。重なる **Enter**/クリックでのフェッチは **no-op**。キーボード **Enter** も空・不正なら info トーストのみ（フェッチは走らせない）。raw をブラウザ `fetch` で取得（CORS 依存）してエディタに設定してから lint する。HTTP 失敗・HTML 返却・無効 URL などは **結果ペインを伏せない** で、画面上部 **`#toast-stack` のトースト**（**`button.toast__dismiss` または Escape で閉じる**、自動消失）で知らせる。成功時もトーストで「読み込み完了」を短く通知してよい。 |
| トースト（診断パネルとは独立） | WASM / 共有 / fetch / Apply fixes などで **lint 結果テーブルの表示を崩さない**。`RunLint` が例外を投げたときも直前の診断を残し、メッセージはトーストのみ。**本文に URL リンクを含め得るため、トースト全体をクリックで閉じるのではなく**、専用の閉じる `button` と **Escape**（**`document` 上のキャプチャ段階**：エディタ・URL のフォーカス中でも、`#toast-stack` の **最上位** を閉じる）で閉じる。外枠に `role="alert"`（error）/ `status`（成功・その他）。スタイルは `style.css` の `.toast-stack` / `.toast--*` / `.toast__dismiss`。 |
| severity フィルター | error/warning/info の表示切替 |

### 8.3 カラーテーマ（ライト / ダーク）

- **システム連動**: OS / ブラウザの `prefers-color-scheme`。手動で上書きしない場合は、`:root` のダークトークンをベースに、`@media (prefers-color-scheme: light)` の **` :root:not([data-theme])` だけ**がライト用トークンを上書きする（`no-preference` 含めて「ライト未指定」時はダーク側）。
- **手動オーバーライド**: フッターのボタンで **System → Light → Dark** を巡回。選択は `localStorage` キー `seiton-playground-color-mode`（値 `light` / `dark` のみ永続化、`system` はキー削除）に保存。`html` に **`data-theme="light"`** を付与すると常にライト（` :root[data-theme="light"]` がシステム用ライトと同じトークン塊を適用）。**` data-theme="dark"`** は通常不要（`:root` 既定がダークのため）だが、一貫のためダーク選択時も付与し、`meta name="color-scheme"` を `light` / `dark` / `light dark` に合わせて更新する。
- **初回描画**: `index.html` の **インライン `<script>`**（CSS より前）でストレージを読み、`data-theme` と `meta` を同期して FOUC を抑える。
- **実装詳細**: ページ本体の色は `style.css` の **`var(--*)` トークン**のみ（生の色は `:root` / メディア / ` [data-theme="light"]` の定義部に限定）。
- **CodeMirror**:
  - **ダーク**: `material-darker`（CDN のテーマ CSS。シンタックス色用。将来 `wwwroot` にベンダーしてもよい）。
  - **ライト**: **`default`**（`codemirror.min.css` に同梱の `.cm-s-default`）。追加のライト用テーマ CSS（例: eclipse）は **不要**。
  - 実際のテーマ名は **ページの実効ライト/ダーク**（手動設定優先、なければ `prefers-color-scheme`）に合わせ、`main.js` が `editor.setOption('theme', …)` する。モードが **System** のときだけ OS の `change` でエディタも追従。
- **ガターマーカー**: `.gutter-marker--error` / `--warning` と `var(--danger)` / `var(--warning)`。

---

## 9. actionlint との構成差分サマリー

| 項目 | actionlint | seiton |
|---|---|---|
| 言語 | Go | C# (.NET 10) |
| WASM ビルド | `GOOS=js GOARCH=wasm go build` | `dotnet publish` (Microsoft.NET.Sdk.WebAssembly) |
| JS glue | `wasm_exec.js` (Go 標準) | `dotnet.js` (dotnet ランタイム、自動生成) |
| WASM 成果物 | 単一 `main.wasm` | `_framework/` ディレクトリ (dotnet.native.wasm + アセンブリ群) |
| JS ↔ WASM interop | `js.FuncOf` + `window.Set` | `[JSExport]` / `[JSImport]` 属性 |
| WASM 最適化 | `wasm-opt` (Binaryen) | AOT + IL trimming + Brotli |
| フロントエンド | TypeScript + npm (tsc ビルド) | 素の JavaScript（ビルドツール不要） |
| エディタ | CodeMirror 5 | CodeMirror 5 or 6 |
| デプロイ | 手動 `deploy.bash` → `gh-pages` ブランチ | GitHub Actions → Pages artifact |
| `.nojekyll` | あり | あり（`_framework/` 保護のため必須） |

---

## 10. Lessons Learned

### 10.1 `[JSExport]` メソッドからの例外伝播で WASM ランタイムが死ぬ

**問題**: `[JSExport]` メソッド内でハンドルされない例外が interop 境界を超えて伝播すると、Mono WASM ランタイムが exit code 1 で abort する。一度 abort すると、以降のすべての `[JSExport]` 呼び出しが `"Assert failed: .NET runtime already exited with 1"` で失敗し、ページのリロードなしには復旧不可能になる。

**発生シナリオ**: ユーザーがエディタで新規行を追加しながらタイプ中に debounce が発火 → 不完全な YAML に対してパーサー/リンターが例外を送出 → ランタイム死亡 → 以降の lint 呼び出しすべてが連鎖的に失敗。

**対策**:
1. **C# 側**: すべての `[JSExport]` メソッド内に `try/catch(Exception)` を配置。例外をキャッチしてエラー JSON を返すことで、例外が interop 境界を超えないようにする。
2. **JS 側**: `runtimeAlive` フラグを導入。"runtime already exited" パターンを検出したら以降の呼び出しを停止し、「ページをリロードしてください」メッセージを表示。

**教訓**: .NET WASM (browser-wasm) の `[JSExport]` メソッドは、例外が絶対に外に漏れない設計にしなければならない。通常の .NET アプリケーションと異なり、ハンドルされない例外＝プロセス終了＝復帰不可能。

### 10.2 同期 WASM 呼び出しに対する JS 側トリガー制御

**問題**: `[JSExport]` は同期呼出し（メインスレッドブロック）のため、lint 実行中にユーザー入力がブラウザレベルでキューされる。lint 完了後にキューされた `change` イベントが連発 → debounce リセット → 300ms 後にまた lint → の連鎖で、高速タイプ時に不要な lint が繰り返される可能性がある。

**対策** (`main.js` に以下の制御を導入):
1. **`lintInProgress` フラグ (再入防止)**: lint 実行中に新たな `runLint()` 呼び出しが来た場合、即座に `lintPendingRetry = true` を設定して return。実行完了後に debounce 付きで再試行する。
2. **`lintPendingRetry` フラグ (完了後リトライ)**: lint 実行中に `change` イベントが発生した場合にセットされる。lint 完了後、このフラグが立っていればさらに `DEBOUNCE_MS` 待ってから再 lint する（即時ではなく debounce を挟むことで、連打を吸収）。
3. **`lastLintedFingerprint` (冪等性)**: `filePath + '\x00' + source` の文字列を記録し、前回と同一なら lint をスキップ。ファイル種別変更・fix 適用・fetch 読み込み時には明示的にクリアする。

**設計判断**:
- 同期呼出しであるため真の並行実行は起きないが、将来的に Web Worker 化や async 化する際のためにも再入防止は入れておく。
- debounce (300ms) + staleness check の組み合わせで、高速タイプ時の無駄な lint を大幅に削減できる。

---

## 11. 参考リンク

- [JavaScript [JSImport]/[JSExport] interop with a WebAssembly Browser App project](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0)
- [Configuring and hosting .NET WebAssembly applications](https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md)
- [JavaScript [JSImport]/[JSExport] interop in .NET WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0)
- [actionlint playground ソース](https://github.com/rhysd/actionlint/tree/main/playground)
- [dotnet.d.ts (.NET WASM runtime configuration)](https://github.com/dotnet/runtime/blob/main/src/mono/browser/runtime/dotnet.d.ts)
