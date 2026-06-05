# Playground メモリクラッシュ調査プラン

## 背景

Playground で YAML を繰り返し入力していると、WebAssembly ランタイムまたはブラウザタブがメモリ関連の理由で落ちることがある。本ドキュメントは再現テストの条件・結果・想定原因・対応策をまとめる。

## 再現テスト

| 項目 | 内容 |
|------|------|
| クラス | `tests/Seiton.Playground.Tests/PlaygroundWasmMemoryCrashUiTests.cs` |
| メソッド | `TypingIncrementalDeployJob_RepeatedEdits_DoNotCrashRuntime` |
| WASM バンドル | `PlaygroundWasmPublishMode.ReleaseAot`（GitHub Pages と同じ trim + AOT） |
| 実行 | `SEITON_PLAYGROUND_PUBLISH_DIR_RELEASE` を pre-publish 出力に向けて `--maximum-parallel-tests 1` |

```powershell
dotnet publish src/Seiton.Playground/Seiton.Playground.csproj -c Release -o publish/playground-aot `
  -p:RunAOTCompilation=true -p:PublishTrimmed=true -p:PlaygroundSoftFingerprint=true
$env:SEITON_PLAYGROUND_PUBLISH_DIR_RELEASE = "$(pwd)/publish/playground-aot"
dotnet test --project tests/Seiton.Playground.Tests `
  --treenode-filter "/*/*/PlaygroundWasmMemoryCrashUiTests/*" --maximum-parallel-tests 1
```

### シナリオ（ユーザー操作の模倣）

1. **起点**: `SAMPLES.default`（Playground 初期表示の default workflow テンプレート）
2. **完成形**: 上記 + 約 50 行の `deploy` job（matrix・複数 steps・式を含む）
3. **入力方法**: CodeMirror `replaceRange(..., '+input')` で suffix を可変長チャンクに分割して追記
4. **間隔**: debounce 500ms を跨ぐよう、決定論的乱数で **580〜950ms** の待機（「打ってから考える」ペース）
5. **繰り返し**: 3 ラウンド。各ラウンドで suffix を打ち切ったあと **後半を削除して再入力**（修正しながら考える動作）
6. **設定**: `fullFix` 相当 config（fix + pinning + runner-no-latest mapping）を test hook で適用
7. **判定**: `getRuntimeAlive()`、`runtime has crashed` トースト、コンソールの OOM / OOB / abort メッセージ

### ローカル実行結果（2026-06-06）

| 実行 | ラウンド | 結果 | 所要時間 |
|------|---------|------|---------|
| 1 | 3 | **Pass** | 約 5m 45s |

**現時点では editor + debounce 経路でランタイム死亡は再現できていない。** テストは回帰ガードとして「この条件で落ちないこと」をアサートする。

## 再現条件（ユーザー報告とテストの対応）

| 条件 | ユーザー報告 | テストでの再現 |
|------|-------------|---------------|
| default テンプレートから開始 | ✓ | ✓ `DefaultSampleYaml` |
| 途中状態の YAML を lint | ✓（debounce 後） | ✓ 各チャンク後に待機して lint 発火 |
| 大きめ job（~50 行）を追加 | ✓ | ✓ `DeployJobSuffix` |
| 入力間隔がばらつく | ✓ | ✓ `RandomTypingDelayMs` |
| 同セッションで繰り返し編集 | ✓ | ✓ 3 ラウンド + 削除・再入力 |
| Config 同時編集 | あり得る | 未モデル（固定 fullFix のみ） |
| 長時間セッション（10+ 分） | あり得る | 3 ラウンド ≒ 6 分（5 ラウンドは ~10 分超） |

## 調査で分かったこと

### 1. UI debounce が中間状態への lint 回数を減らす

`main.js` の `DEBOUNCE_MS = 500` により、連続入力は 1 回の lint にまとまる。テストも debounce より長い待機を入れているため、**「打つ → 止まる → lint」** のサイクルが再現される。

### 2. 未完成 `- uses:` 行は lint しない（OOB 回避）

`shouldDeferWasmLintForIncompleteUses()` により、行末が bare `- uses:` の間は `runLint()` をスキップする。これは既知の WASM AOT trap 対策（`PlaygroundWasmMemoryOobUiTests` と同系）。

### 3. test hook で全 prefix に即 lint すると別経路の OOB が出る

debounce なしで `__SEITON_PLAYGROUND_TEST__.runLint` を suffix 追記の **全中間 prefix** に対して呼ぶと、editor 経路より早い段階（例: suffix 追記 3 チャンク目）で `memory access out of bounds` になる。**本番 UI とは経路が異なる**ため、メモリクラッシュ再現の主テストには editor 経路を使う。

### 4. ブラウザ WASM では incremental lint が無効

`PlaygroundLintRunner.UseIncrementalLint` は browser では `false`。毎回 full parse + lint のため、lint 1 回あたりの WASM ヒープ使用量は desktop incremental より大きい。

### 5. 観測可能な「クラッシュ」の種類

| 現象 | 検出方法 | テスト |
|------|---------|--------|
| Mono WASM trap（OOB 等） | コンソール / toast / `runtimeAlive = false` | ✓ |
| `.NET runtime already exited` | 同上 | ✓ |
| ブラウザタブ OOM（Silent kill） | Playwright では検出困難 | ✗ |
| WASM heap 上限（`EmccMaximumHeapSize` 1GB） | 長時間・高頻度 lint で理論上可能 | 3 ラウンドでは未再現 |

## 想定原因（優先度順）

1. **WASM ヒープの断片化・滞留** — full lint の繰り返しで Mono GC が heap を OS に返せず、長セッションで上限に達する
2. **`lintPendingRetry` による lint バースト** — lint 実行中にさらに編集すると、完了後 debounce 付きで再 lint が走る。入力と lint が重なるとピークメモリが上がる
3. **identity cache が大きな文字列 / JSON を保持** — `_lastYamlSource` / `_lastJsonOutput` がセッション中ずっと参照を保持
4. **ブラウザ側** — CodeMirror 履歴、診断 DOM、Source map 等（`renderResults` は `replaceChildren` でクリアしているが、長時間で DevTools オープン時などは別要因あり得る）

## 対応策（提案）

| 優先 | 対策 | 効果 | 備考 |
|------|------|------|------|
| P0 | **回帰テストを CI に載せる** | 再発検知 | `PlaygroundWasmMemoryCrashUiTests` |
| P1 | **lint 中の retry 上限 / coalesce** | ピークメモリ低減 | `lintPendingRetry` が連鎖しないよう、実行中の edit は最新 1 件だけ保持 |
| P1 | **staleness を hash ベースに** | 大文字列の長期保持回避 | `ReferenceEquals` ではなく content hash + length で同一判定 |
| P2 | **N 回 lint ごとに WASM GC ヒント** | 断片化緩和 | `System.GC.Collect` を browser で呼べるか要検証 |
| P2 | **ヒープ使用量の test hook 公開** | 計測可能に | `dotnet.wasmMemory` 等があればテスト assert に利用 |
| P3 | **セッション長警告** | UX | 一定時間 / lint 回数超で「リロード推奨」 |
| P3 | **Config 同時編集の E2E** | カバレッジ拡大 | config debounce + workflow debounce の二重 lint |

## 今後

- [ ] 3 ラウンドテストを CI（`build.yaml`）で定期実行し、フレーク有無を確認
- [ ] ユーザー環境で落ちた場合: DevTools Console の全文、再現までの時間、config 有無、ブラウザ種別を収集
- [ ] 再現したら failing prefix の YAML を fixture 化し desktop / WASM の bisect

## 関連

- `PlaygroundWasmMemoryOobUiTests` — bare `- uses:` 行の OOB
- `Seiton_Playground_spec.md` §3.1 — debounce / defer / staleness
- `Seiton_Playground_csharp_spec.md` — `EmccMaximumHeapSize`、engine 再利用
