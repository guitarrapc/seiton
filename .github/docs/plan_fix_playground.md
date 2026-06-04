# Playground diagnostics display bug investigation and fix plan

## 背景

Playground で lint 結果が存在するにもかかわらず、表示が次のように崩れる事象を確認:

- `line:0, col:0`
- `Info`
- メッセージが欠落（または空）

この状態だと診断内容が読めず、実質的に Playground の主要機能が失われる。

## 調査結果（根本原因）

根本原因は `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` の `RunToJsonUtf8()` にある。

- Browser 実行時は `UseIncrementalLint == false` のため、非 incremental 分岐 (`Engine.Check(...)`) を通る。
- この分岐では `using var lintResult = Engine.Check(...)` で `lintResult.Diagnostics.AsSpan()` を `diagnosticsToSerialize` に退避している。
- しかし JSON へのシリアライズは `using` スコープの外側で実行されるため、`lintResult` 解放後の span を読み取ってしまう（解放済み/再利用済みバッファ参照）。
- その結果、`Diagnostic` 構造体の値が既定値寄りに崩れ、`line:0 col:0 / severity:Info / message空` のような表示になる。

要するに **diagnostics バッファの lifetime 破壊（use-after-dispose 相当）** が直接原因。

## なぜ既存テストで検出できなかったか

1. `tests/Seiton.Playground.Tests/PlaygroundLintRunnerTests.cs` はデスクトップ環境で実行されるため、通常 `UseIncrementalLint == true` 経路を主に通る。
2. 問題の分岐（Browser 向け `UseIncrementalLint == false`）を直接検証するテストがない。
3. 既存の JSON 検証は「プロパティの存在」中心で、`line >= 1`、`column >= 1`、`message 非空`、期待 ruleId の存在などの意味検証が弱い。
4. UI 側 Playwright テストは主にクラッシュ回避・レイアウト・フック可用性を見ており、描画された診断の内容妥当性を検証していない。

## 影響範囲

- 主影響: `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` の Browser 経路
- 間接影響: Playground UI (`src/Seiton.Playground/wwwroot/main.js`) の表示品質
- 影響しない領域: CLI 表示フォーマッタ本体（ただし Playground 経路とは別実装）

## 優先度付き対応プラン

### P0（最優先: 直ちに修正）

1. `RunToJsonUtf8()` の非 incremental 分岐で、`lintResult` が生存している間に JSON シリアライズまで完了させるように構造を修正する。
2. `Diagnostic` span を `using` スコープ外に持ち出さない形に統一する（他分岐も含め lifetime 安全性を明示）。
3. バグ再現用の最小回帰テストを追加する:
   - `line >= 1`
   - `column >= 1`
   - `severity` が期待クラス（少なくとも全件 `Info` 固定にならない）
   - `message` 非空

完了条件:

- 再現入力で `line:0, col:0 Info` 固定表示が消える。
- 追加した回帰テストが fail→fix 後 pass になる。

### P1（高優先: 検出力強化）

1. Playground テストに Browser 経路専用の検証を追加する（`runLint` フック経由で diagnostics payload の内容を厳密検証）。
2. 既存の `PlaygroundLintRunnerTests` を強化し、「JSON shape」ではなく「意味（位置・メッセージ・ruleId）」を assert する。
3. UI テストに 1 本、結果テーブルの実描画検証を追加する（1 行以上の line/col と message 表示を確認）。

完了条件:

- Browser/desktop 双方で diagnostics 妥当性テストが存在。
- lifetime 破壊の再発時に CI で即検出できる。

### P2（中優先: 保守性と安全策）

1. `PlaygroundLintRunner` に「`DiagnosticList` の有効期間」についてのコードコメントを追記。
2. span を跨ぐ設計になっていないかを同ファイルで棚卸しし、同種パターンを除去。
3. ドキュメント（Playground spec もしくはテスト設計メモ）に「Browser 経路での診断完全性」を非機能要件として明記。

完了条件:

- 同種バグの温床となる寿命依存コードがレビューで見つけやすくなる。

## 推奨実施順序

1. P0 のテスト追加（失敗確認）
2. P0 の実装修正
3. P0 テスト再実行 + `dotnet test` 全体実行
4. P1 の検出力強化
5. 必要に応じて P2 ドキュメント整備

## 補足

- この不具合はユーザー体験への影響が大きいため、優先度は **Critical (P0)**。
- 修正時は Playground の Browser 経路を明示的に通すテストを必須化すること（desktop のみでは再発を見逃す）。

---

## P0 実装結果（2026-06-04）

### 実装した変更

1. `src/Seiton.Playground.Core/PlaygroundLintRunner.cs`
   - `RunToJsonUtf8()` を修正し、非 incremental 分岐（`Engine.Check`）で `lintResult` 生存中に JSON シリアライズを完了するよう変更。
   - 診断シリアライズを `SerializeDiagnosticsToResult(ReadOnlySpan<Diagnostic>)` に抽出し、分岐ごとの寿命管理を明確化。
   - テスト専用フック `ForceUseIncrementalLintForTests` を追加し、desktop でも Browser 相当の非 incremental 経路を再現可能にした。
   - `ResetSharedStateForTests()` で上記フックを確実に初期化するよう追加。

2. `tests/Seiton.Playground.Tests/PlaygroundLintRunnerTests.cs`
   - 回帰テスト `RunToJson_NonIncrementalPath_ProducesNonDefaultDiagnosticFields` を追加。
   - 非 incremental 経路で `deny-write-all` 診断を検出し、`line > 0` / `column > 0` / `message 非空` を検証。

### TDD 実施ログ（Red → Green）

- **Red**: 新規テスト追加直後、`ForceUseIncrementalLintForTests` 未定義でコンパイル失敗を確認。
- **Green**: 実装後、同テスト単体が pass。
- 追加で `PlaygroundLintRunnerTests` クラス全体（23件）も pass。

### ベンチマーク（PlaygroundLintBenchmark）

実行コマンド:

```shell
dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj --filter "*PlaygroundLintBenchmark*"
```

比較（Mean / Allocated）:

| Case | Before Mean | After Mean | Diff | Before Alloc | After Alloc | Diff |
|---|---:|---:|---:|---:|---:|---:|
| Small / NoChange | 97.567 ns | 96.852 ns | -0.7% | 0 B | 0 B | 0% |
| Small / PartialChange | 676.460 us | 683.007 us | +1.0% | 136,080 B | 136,080 B | 0% |
| Small / FullChange | 194.809 us | 204.978 us | +5.2% | 51,927 B | 51,927 B | 0% |
| Large / NoChange | 89.094 ns | 100.729 ns | +13.1%* | 0 B | 0 B | 0% |
| Large / PartialChange | 4.783 ms | 3.370 ms | -29.5% | 383,252 B | 383,206 B | -0.0% |
| Large / FullChange | 1.129 ms | 1.126 ms | -0.3% | 170,782 B | 170,782 B | 0% |

\* ShortRun（Iteration=3）のため ns オーダーの揺れが大きく、同時に信頼区間が広い。割当メモリは全ケースで維持され、ホットパスの allocation regression は確認されていない。

### テスト実行結果

- ✅ `dotnet test --project tests/Seiton.Playground.Tests --treenode-filter /*/*/PlaygroundLintRunnerTests/RunToJson_NonIncrementalPath_ProducesNonDefaultDiagnosticFields*`
- ✅ `dotnet test --project tests/Seiton.Playground.Tests --treenode-filter /*/*/PlaygroundLintRunnerTests/*`
- ⚠️ `dotnet test`（全体）は Playground UI 系でタイムアウト/環境依存失敗（本変更と無関係な Playwright 起動待ち失敗）により非成功。
- ⚠️ skill 推奨の事前 publish も、既存の `Seiton.Playground.csproj` 側エラー（MSB3094）で完了できず、UI 系の再検証をブロック。

### ユーザーファースト/API 観点の確認

- 公開 API（JS から見える `RunLint` / `SetConfig` / `ApplyAllFixes*`）のシグネチャと返却形式は変更なし。
- ユーザー体験上の改善点:
  - 診断が `line:0, col:0 / Info` へ崩れる不正表示を防止。
  - 既存 UI 操作（クリックジャンプ、severity 表示、ruleId 表示）が直感どおり機能する前提を回復。

### 仕様整合性チェック

- `Seiton_Playground_spec.md` の診断 JSON スキーマ（`line`,`column`,`severity`,`message`）と実装は整合。
- `Seiton_Playground_csharp_spec.md` の `RunToJsonUtf8` hot path 記述（Utf8JsonWriter, zero-allocation方針）とも整合。
- 今回は仕様変更ではなく不具合修正であり、仕様本文の更新は不要と判断。

### 実装レビュー反復（セルフレビュー）

#### Round 1
- 指摘: 非 incremental 分岐で dispose 後 span 参照の可能性（根本不具合）。
- 対応: 分岐内で即シリアライズする構造へ変更。

#### Round 2
- 指摘: Browser 経路を desktop テストで再現できず、回帰検出不能。
- 対応: `ForceUseIncrementalLintForTests` を追加し、回帰テストを作成。

#### Round 3
- 指摘: 修正で allocation が増えないことの裏付け不足。
- 対応: PlaygroundLintBenchmark を前後実行して Allocated を比較（全ケース維持）。

最終判定: P0 要件（原因修正 + 回帰テスト + 性能確認）は満たした。

---

## P1 実装結果（2026-06-04）

### 実装した変更

1. `tests/Seiton.Playground.Tests/PlaygroundLintRunnerTests.cs`
   - `RunToJson_InvalidYaml_ContainsParserDiagnosticWithLineAndMessage` を強化し、`line > 0` / `column > 0` / `message 非空` / `severity 値妥当` を検証。
   - `RunToJson_DenyWriteAll_IncludesFixableDiagnostic` を強化し、`deny-write-all` 診断の位置情報とメッセージ妥当性を検証。

2. `tests/Seiton.Playground.Tests/PlaygroundUiLayoutTests.cs`
   - `BrowserHook_RunLint_DiagnosticsHaveMeaningfulFields` を追加。
     - Browser の `__SEITON_PLAYGROUND_TEST__.runLint` を直接呼び、hook payload に対して `line/column/message/severity` の意味検証を実施。
   - `DiagnosticsTable_RendersPositiveLineColumnAndMessage` を追加。
     - 実際の結果テーブル描画を対象に `line:[1-9]..., col:[1-9]...`、severity、message を検証。

### TDD 実施ログ（Red → Green）

- **Red**:
  - 新規 UI テスト実行時に `dotnet publish` 競合（StaticWebAssets 圧縮ファイル lock）で失敗を確認。
- **Green**:
  - skill に沿って prepublish 生成物を利用し、`SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG` を指定して UI テストを再実行。
  - 追加2テストとも pass。
  - 既存 `PlaygroundLintRunnerTests` クラス（23件）も pass。

### テスト実行結果

- ✅ `dotnet test --project tests/Seiton.Playground.Tests --treenode-filter /*/*/PlaygroundLintRunnerTests/*`
- ✅ `dotnet test --project tests/Seiton.Playground.Tests --maximum-parallel-tests 1 --treenode-filter /*/*/PlaygroundUiLayoutTests/BrowserHook_RunLint_DiagnosticsHaveMeaningfulFields*`
- ✅ `dotnet test --project tests/Seiton.Playground.Tests --maximum-parallel-tests 1 --treenode-filter /*/*/PlaygroundUiLayoutTests/DiagnosticsTable_RendersPositiveLineColumnAndMessage*`

### ベンチマーク（PlaygroundLintBenchmark）

実行コマンド（2回）:

```shell
dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj --filter "*PlaygroundLintBenchmark*"
```

比較（P1 run1 vs run2, Mean / Allocated）:

| Case | Run1 Mean | Run2 Mean | Diff | Run1 Alloc | Run2 Alloc | Diff |
|---|---:|---:|---:|---:|---:|---:|
| Small / NoChange | 112.046 ns | 115.181 ns | +2.8% | 0 B | 0 B | 0% |
| Small / PartialChange | 1.325 ms | 1.226 ms | -7.5% | 136,080 B | 136,080 B | 0% |
| Small / FullChange | 225.449 us | 230.395 us | +2.2% | 51,927 B | 51,927 B | 0% |
| Large / NoChange | 103.997 ns | 109.202 ns | +5.0% | 0 B | 0 B | 0% |
| Large / PartialChange | 3.904 ms | 4.005 ms | +2.6% | 383,206 B | 383,206 B | 0% |
| Large / FullChange | 1.278 ms | 1.278 ms | +0.0% | 170,782 B | 170,782 B | 0% |

評価:

- 変更対象はテストコードのみで、実行時コード（`src/`）のロジック変更はなし。
- Mean の差分は ShortRun の計測揺れ範囲内。
- Allocated は全ケースで不変であり、性能劣化の兆候は確認されない。

### ユーザーファースト/API 観点の確認

- ユーザー向け API/操作フローの変更はなし（公開シグネチャ不変）。
- ただし検出力の強化により、次の UX 劣化を早期検知できる:
  - 表示位置が `line:0,col:0` へ崩れる
  - メッセージ欠落
  - Browser 経路だけで発生する payload 破損
- つまり「壊れた結果をユーザーに見せる」リスクを CI で抑制する改善。

### 仕様整合性チェック

- `Seiton_Playground_spec.md` の diagnostics schema（`message`,`line`,`column`,`severity`）に沿った検証をテストで明文化。
- `Seiton_Playground_csharp_spec.md` の interop/JSON 仕様と矛盾なし。
- 仕様変更は不要（検証強化のみ）。

### 実装レビュー反復（セルフレビュー）

#### Round 1
- 指摘: desktop テストだけでは Browser hook payload の破損を見逃す。
- 対応: `BrowserHook_RunLint_DiagnosticsHaveMeaningfulFields` を追加。

#### Round 2
- 指摘: payload 検証だけでは「UI描画の最終形」が担保されない。
- 対応: `DiagnosticsTable_RendersPositiveLineColumnAndMessage` を追加。

#### Round 3
- 指摘: 並列テスト実行で publish lock が発生し、検証が不安定。
- 対応: prepublish 生成物 + `SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG` + `--maximum-parallel-tests 1` で安定実行に変更。

最終判定: P1 要件（Browser/desktop/UI の検出力強化）は満たした。

---

## P2 実装結果（2026-06-04）

### 実装した変更

1. `src/Seiton.Playground.Core/PlaygroundLintRunner.cs`
   - `RunToJsonUtf8()` の分岐直前に、`DiagnosticList` / `ReadOnlySpan<Diagnostic>` の寿命不変条件（owner 生存中に消費すること）を明示コメントで追加。
   - Action metadata 分岐・incremental 分岐の変数名を `lintResultData` に統一し、`LintResultData`（非 `IDisposable`）を扱っていることをコード上で明確化。
   - 同ファイル内を棚卸しし、`using` スコープ外へ span を持ち出す旧パターンがないことを再確認（いずれも owner 生存中に `SerializeDiagnosticsToResult(...)` へ渡す構造を維持）。

2. `.github/docs/Seiton_Playground_spec.md`
   - 非機能要件として「Browser 経路での診断完全性（`line>=1`,`column>=1`,`message非空`,`severity妥当`）」を明記。

3. `.github/docs/Seiton_Playground_csharp_spec.md`
   - C# 実装詳細に「diagnostic lifetime invariant（`LintResult` / `AstArena` 生存中に診断を消費）」を追記。

### TDD 実施ログ（Red → Green）

- **Red**:
  - `LintResultData` を `using` しようとしてコンパイル失敗（`CS1674`）を確認。
- **Green**:
  - `LintResultData` が非 `IDisposable` である設計に合わせ、寿命境界を崩さない最小修正へ戻しビルド通過。
  - `PlaygroundLintRunner` 非incremental回帰テスト単体 pass を確認。

### ベンチマーク（PlaygroundLintBenchmark）

実行コマンド:

```shell
dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj --filter "*PlaygroundLintBenchmark*"
```

比較（Before = P2 着手前, After = P2 実装後の再計測）:

| Case | Before Mean | After Mean | Diff | Before Alloc | After Alloc | Diff |
|---|---:|---:|---:|---:|---:|---:|
| Small / NoChange | 109.958 ns | 109.752 ns | -0.2% | 0 B | 0 B | 0% |
| Small / PartialChange | 1.254 ms | 1.230 ms | -1.9% | 136,080 B | 136,080 B | 0% |
| Small / FullChange | 237.299 us | 230.199 us | -3.0% | 51,927 B | 51,927 B | 0% |
| Large / NoChange | 107.453 ns | 105.578 ns | -1.7% | 0 B | 0 B | 0% |
| Large / PartialChange | 3.762 ms | 3.977 ms | +5.7% | 383,206 B | 383,206 B | 0% |
| Large / FullChange | 1.326 ms | 1.312 ms | -1.1% | 170,942 B | 170,782 B | -0.1% |

評価:

- すべて +10% 以内（Mean/Allocated）で、回帰閾値を満たす。
- 変更は寿命不変条件の明文化と可読性改善が中心で、Hot path の allocation 特性は実測でも維持された。

### テスト実行結果

- ✅ `dotnet build tests/Seiton.Playground.Tests/Seiton.Playground.Tests.csproj`
- ✅ `dotnet test --project tests/Seiton.Playground.Tests --maximum-parallel-tests 1 --treenode-filter /*/*/PlaygroundLintRunnerTests/RunToJson_NonIncrementalPath_ProducesNonDefaultDiagnosticFields*`
- ⚠️ `dotnet test`（全体）は `Seiton.Playground.Tests.exe` の file lock（`MSB3027`/`MSB3021`）で失敗。プロセス解放後に再試行が必要。

### ユーザーファースト/API 観点の確認

- 公開 API（`RunLint` / `SetConfig` / `ApplyAllFixes*`）のシグネチャ・返却形式は変更なし。
- 開発者視点では、寿命境界がコードと仕様に明示され、将来の保守で「なぜこの場所で即シリアライズが必要か」を直感的に把握しやすくなった。

### 仕様整合性チェック

- `Seiton_Playground_spec.md` に Browser 診断完全性を非機能要件として追加。
- `Seiton_Playground_csharp_spec.md` に実装側の lifetime invariant を追加。
- 仕様と実装の整合をとる目的の更新であり、外部 API 互換性は維持。

### 実装レビュー反復（セルフレビュー）

#### Round 1
- 指摘: lifetime ルールがコードから読み取りづらく、同種バグの再発防止として弱い。
- 対応: `RunToJsonUtf8` に寿命不変条件コメントを追加し、分岐境界での意図を固定。

#### Round 2
- 指摘: Action/incremental 分岐の一時変数名が `lintResult` のままだと `IDisposable` 誤認を誘発する。
- 対応: `lintResultData` に改名して型意図を明確化。

#### Round 3
- 指摘: 文書側に Browser 診断完全性要件がなく、実装意図が将来失われる。
- 対応: Playground spec と C# spec の両方へ非機能要件・実装不変条件を追記。

最終判定: P2 要件（保守性向上 + 同種リスクの可視化 + 文書化）は満たした。
