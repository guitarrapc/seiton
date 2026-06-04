# Playground メモリ枯渇・WASM OOB 調査メモ

> 作成: 2026-06-04
> 対象: `tests/Seiton.Playground.Tests`、ブラウザ Playground（`src/Seiton.Playground`）

## 要約

Playground テスト実行で **128GB RAM が枯渇**し、OS が強制シャットダウンする事象は、単一の小さなリークというより **複数の高ピーク要因が TUnit の並列実行と重なった結果**と判断した。本 PR では再発を抑えるガードを入れた。
別件として、編集中の **`      - uses:` だけ**（アクション ref 未入力）で **Release+AOT WASM が `memory access out of bounds` またはランタイム死亡**する問題があり、UI 側で該当入力中は WASM lint を延期する。

---

## 調査結果

### 1. テストプロセス内の `dotnet publish`（WASM AOT）— 影響度: **最高**

| 項目 | 内容 |
|------|------|
| 現象 | `PlaygroundUiTestHost.CreateAsync(ReleaseAot)` が **テスト実行中**に `dotnet publish -p:RunAOTCompilation=true` を起動する |
| ピーク RAM | マシン・並列度により **数十 GB**（AOT コンパイル + リンク）。128GB マシンでも他負荷と重なるとスワップ→実質フリーズ |
| トリガ | `PlaygroundWasmMemoryOobUiTests` など `[ReleaseAot]` を要求する UI テストの初回 `GetOrCreateAsync` |
| 悪化要因 | Debug 用ホストと Release AOT ホストを **同時に保持**（2 つの Kestrel + 2 つの publish ツリー） |

**根拠コード**: `tests/Seiton.Playground.Tests/PlaygroundUiTestHost.cs` の `CreateAsync` → `Process.Start("dotnet", "publish ...")`。

### 2. TUnit 適応並列 × Playground テスト未直列化 — 影響度: **高**

| 項目 | 内容 |
|------|------|
| 現象 | UI テストだけ `[NotInParallel]` があり、**ユニットテスト 100 件超**はデフォルト並列のままだった |
| 重なり | 並列ユニットテスト（`PlaygroundLintRunner.RunToJsonUtf8` + 共有 `IncrementalParseContext`）と、同プロセス内の **publish / Chromium** が同時進行しうる |
| 共有状態 | `PlaygroundLintRunner` の static `LintEngine` / `IncrementalParseContext` — `EngineGate` で直列化されるが、**アリーナ保持・JSON キャッシュ**はプロセス寿命で積み上がる |

### 3. Chromium + Mono WASM ヒープ — 影響度: **中〜高**

| 項目 | 内容 |
|------|------|
| 設定 | `EmccInitialHeapSize=64MB`, `EmccMaximumHeapSize=1GB`（`Seiton.Playground.csproj`） |
| 現象 | 1 タブあたり WASM ヒープが **最大 1GB** まで成長しうる。Mono は OS へメモリを返さないことが多い |
| テスト | `WasmLint_AlternatingBufferSizes` が同一ページで多数回 `RunLint` → ブラウザプロセス RSS が階段状に増加 |
| 緩和 | ページ／コンテキストを閉じる、ラウンド数を抑える、本番では debounce + 不完全入力スキップ |

### 4. デスクトップでの incremental lint（テストのみ）— 影響度: **低〜中**

| 項目 | 内容 |
|------|------|
| 経路 | `OperatingSystem.IsBrowser() == false` のため `UseIncrementalLint == true` |
| ガード | `MaxRetainedArenas=4`、成長閾値 3× でフルパースにフォールバック（`IncrementalParseContext`） |
| リスク | テスト間で static コンテキストをクリアしないと、理論上は保持アリーナがプロセス終了まで残る |

### 5. 編集中の `      - uses:`（WASM AOT のみ）— 影響度: **高（UX・安定性）**

| 項目 | 内容 |
|------|------|
| 現象 | サンプル末尾に `      - uses:` だけ付けた状態で `RunLint` → **`memory access out of bounds`** またはランタイム終了 |
| デスクトップ | `PlaygroundWasmMemoryOobDesktopTests` では **例外なし**（CoreCLR フルパース） |
| ブラウザ | Release+AOT WASM のみ再現（#125 で incremental lint をブラウザでは無効化済みだが OOB は残りうる） |
| 対応方針 | 行末が `- uses:` のみの間は **WASM を呼ばない**（`main.js` の `shouldDeferWasmLintForIncompleteUses`） |

---

## 実施した対応（本ブランチ）

| # | 対応 | ファイル |
|---|------|----------|
| 1 | 全 Playground テストを `[NotInParallel(AssemblyLockKey)]` で直列化 | `PlaygroundTestParallelism.cs`、各 `*Tests.cs` |
| 2 | CI で publish を **テスト前**に実行し、テスト中は env で再利用 | `.github/workflows/build.yaml`、`PlaygroundUiTestHost` |
| 3 | Debug / Release AOT ホストを **同時保持しない** | `ShutdownOtherModeAsync` |
| 4 | 共有 static の `ResetSharedStateForTests`（アセンブリ Before/After） | `PlaygroundLintRunner`, `IncrementalParseContext`, `PlaygroundUiTestAssemblyHooks` |
| 5 | 不完全 `- uses:` 行で lint 延期 | `wwwroot/main.js` |
| 6 | UI ストレステストのラウンド削減・ページ `CloseAsync` | `PlaygroundWasmMemoryOobUiTests.cs` |

---

## 優先度付き対応策（未実施・フォロー）

| 優先度 | 対策 | 期待効果 | 工数 |
|--------|------|----------|------|
| **P0** | ローカルでも CI と同様に事前 publish + env を使う（下記手順） | テスト中の AOT ピーク除去 | 低 |
| **P0** | `dotnet test` で Playground 以外と **モジュール並列を抑える**（`--max-parallel-test-modules 2` など） | ソリューション全体の RAM ピーク低減 | 低 |
| **P1** | WASM OOB の **AOT 根本原因**をバイナリ検索（desktop 再現不可 → WASM 専用ビルドで lldb / ログ） | 不完全 YAML でも安全に lint | 高 |
| **P1** | `RunLintParseOnly` 等、**パース診断のみ**の JSExport（中間編集向け） | UX 向上・トラップ回避 | 中 |
| **P2** | ブラウザテストを `[Category("BrowserWasm")]` 分離し、通常 CI ではスキップ可能に | 日常開発の RAM/時間削減 | 中 |
| **P2** | `EmccMaximumHeapSize` を本番 1GB / テスト用プロファイル 256MB などに分割 | タブあたり上限 | 低 |
| **P3** | `dotnet publish` 子プロセスに **タイムアウト・kill tree** | ハング時のゾンビ防止 | 低 |

---

## 再現手順

### A. メモリ枯渇（テスト・最悪ケース）

前提: `dotnet workload install wasm-tools`、Playwright Chromium 導入済み。

```powershell
cd D:\github\guitarrapc\seiton-gh
dotnet build -c Release

# 悪化条件: 事前 publish なし + 全テスト + 高並列（他プロジェクトと同時）
dotnet test -c Release --no-build

# 特に重い: Playground UI（Release AOT publish がテスト内で走る）
dotnet test tests/Seiton.Playground.Tests/Seiton.Playground.Tests.csproj -c Release --no-build
```

**観察**: タスクマネージャで `dotnet.exe`（publish 子プロセス）と `chromium` のコミットサイズが増え続ける。128GB でもスワップが張られ OS が応答しなくなることがある。

**安全な実行例（推奨）**:

```powershell
# 1) 事前 publish（CI と同じ）
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj -c Debug -o publish/playground-dbg/ -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:PlaygroundSoftFingerprint=true
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj -c Release -o publish/playground-aot/ -p:RunAOTCompilation=true -p:PublishTrimmed=true -p:PlaygroundSoftFingerprint=true

# 2) env を渡してテスト（テスト内 publish をスキップ）
$env:SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG  = "$PWD\publish\playground-dbg"
$env:SEITON_PLAYGROUND_PUBLISH_DIR_RELEASE = "$PWD\publish\playground-aot"
dotnet test tests/Seiton.Playground.Tests/Seiton.Playground.Tests.csproj -c Release --no-build --maximum-parallel-tests 1
```

### B. WASM `memory access out of bounds`（ブラウザ・編集中）

1. Release+AOT で Playground をホスト（または https://guitarrapc.github.io/seiton/ の本番相当ビルド）。
2. デフォルトサンプル workflow の末尾に、行を追加しながら `      - uses:` で止める（ref 未入力）。
3. 500ms debounce 後に `RunLint` が走ると、コンソール / トーストに `memory access out of bounds`、以降「ランタイムがクラッシュしました」。

**デスクトップのみの切り分け**:

```powershell
dotnet test tests/Seiton.Playground.Tests/Seiton.Playground.Tests.csproj -c Release `
  --maximum-parallel-tests 1
# PlaygroundWasmMemoryOobDesktopTests が CoreCLR で落ちないことを確認
```

**fixtures**: サンプル + `      - uses:` は旧 `temp-partial-uses.yml` と同等（リポジトリからは削除済み）。

### C. ユニットテストのみ（ブラウザなし）

```powershell
dotnet test tests/Seiton.Playground.Tests/Seiton.Playground.Tests.csproj -c Release `
  --maximum-parallel-tests 1
# PlaygroundHtmlContractTests / IncrementalParse* / PlaygroundLintRunner* のみ
# UI テストは Playwright + publish で時間と RAM が増える
```

---

## 関連ドキュメント・PR

- `Seiton_Playground_csharp_spec.md` §2.1 — ブラウザでは incremental lint 無効
- PR #125 — WASM OOB 対策（`UseIncrementalLint`、OOB テスト追加）
- PR #56 — WASM 向け incremental 診断キャッシュ（デスクトップ経路）

---

## 検証チェックリスト

- [ ] 事前 publish + env で `Seiton.Playground.Tests` 完了、タスクマネージャで publish 子プロセスがテスト中に出ない
- [ ] `shouldDeferWasmLintForIncompleteUses` 適用後、`- uses:` のみの状態でトースト OOB が出ない
- [ ] `WasmLint_*` UI テストが Release AOT で pass
- [ ] ソリューション全体 `dotnet test -c Release` が CI と同条件で pass
