# plan_parser_not_detect

## 背景

以下の Workflow 断片は YAML マッピングとして不正です（同一レベルで `env` が重複）。

```yaml
on:
  workflow_dispatch:

jobs:
  action-pin-samples:
    permissions:
      contents: read
    timeout-minutes: 10
    runs-on: ubuntu-24.04
    steps:
      - run: foo "$FOOBAR"
        env:
          FOOBAR: "foobar"
        env:
          PIYOPIYO: "piyopiyo"
```

実際の出力は `0 issues in 1 file` となり、検出漏れが発生している。

## 調査結果

1. 再現確認
- `samples/parser/.github/workflows/test.yaml` で `seiton -vv .github/workflows/test.yaml` を実行すると、`0 diagnostics` と表示される。

2. 診断集約経路は欠落していない
- `LintEngine.Check` は parse diagnostics を捨てておらず、fatal parse 時も diagnostics を返す設計。
- したがって今回の `0 issues` は「表示前に落ちる」のではなく「パース側で診断が作られていない」ことが主因。

3. 直接原因
- `WorkflowParser.Steps.ParseStep` には、step-level 既知キー（`run`, `uses`, `name`, `if`, `with`, `shell`, `working-directory`, `timeout-minutes`, `continue-on-error`, `env`）の重複検出がない。
- 同一 step 内で `env` が2回出現しても、2回目で `envNode` が上書きされるだけで、diagnostic が発行されない。
- 一方で、workflow root/job/container/on.* など多くのセクションは duplicate key を検出済みであり、steps だけ抜けている。

4. parser error という語の整理
- 今回は VYaml 例外（fatal parse error）ではなく、Seiton の構文診断（syntax-check 相当）として扱うべきケース。
- 現仕様の「最後まで検出をあきらめない」にも整合する（fatal terminate ではなく、診断追加して継続する）。

## 優先度付き対応一覧

## P0: 必須（検出漏れを塞ぐ）

1. `ParseStep` に duplicate key 検出を追加
- 方式: 既存セクションと同様に bit mask（`ulong seen`）で step-level known keys の重複を判定。
- 診断: 例 `jobs.'action-pin-samples'.steps[1] key "env" is duplicated in step. previously defined at line:X,col:Y`。
- 動作: 重複を検出しても処理は継続し、重複側 value は `SkipCurrentNode()` でスキップ。fatal 化しない。
- 意図: 「走査をターミネートしない」要件を満たしつつ、検出漏れを解消。

2. 回帰テスト追加（Parser）
- `tests/Seiton.Core.Tests/ParserTests.cs` に、step-level duplicate `env` の再現テストを追加。
- 期待: duplicate diagnostic が 1 件以上出ること、`HasFatalError` は false のままであること。

3. 回帰テスト追加（CLI 表示）
- `tests/Seiton.Tests` 側に、同入力で summary が `0 issues` にならないことを担保するテストを追加。
- 期待: summary に error 件数が反映されること。

## 実装結果（2026-06-10）

### P0-1 `ParseStep` duplicate key 検出
- 実装済み。
- 変更ファイル: `src/Seiton.Core/Parsing/WorkflowParser.Steps.cs`
- 変更内容:
  - `ParseStep` に `ulong seen` + `stackalloc` の first-mark 配列を追加。
  - step-level known keys（`StepMappingKeyTable`）で duplicate を検出。
  - duplicate 時はエラー診断を追加し、`SkipCurrentNode()` で値ノードを読み飛ばして走査継続（fatal 化しない）。
- 診断例:
  - `jobs.'action-pin-samples'.steps[1] key "env" is duplicated in step. previously defined at line:12,col:9`

### P0-2 回帰テスト（Parser）
- 実装済み（P1/P2 + コードレビューで追加）。
- テスト:
  - `Parse_StepDuplicateEnv_IncludesHelp` / `Parse_StepDuplicateEnv_MessageFormatMatchesOtherSections`
  - `Parse_StepDuplicateKnownKeys_TableDriven`（`run`, `uses`, `with`, `shell`）
  - `Parse_StepSingleEnv_NoDuplicateDiagnostic`（negative）
  - `Parse_StepDuplicateEnv_FirstOccurrenceWins_SecondEnvSkipped`（first-win）
  - `Parse_StepDuplicateShell_IncludesGenericHelp`（env 以外の Help）
  - `Lint_StepDuplicateEnv_DoesNotSuppressSubsequentRuleDiagnostics`

### P0-3 回帰テスト（CLI 表示）
- 実装済み。
- 変更ファイル: `tests/Seiton.Tests/CheckCommandTests.cs`
- 追加テスト:
  - `Check_TextMode_DuplicateStepEnv_IsReportedAndSummaryIsNotZeroIssues`
- 検証内容:
  - exit code が `LintIssuesFound`
  - 標準出力に `error[syntax-check]:` と duplicate `env` 診断が含まれる
  - 標準エラー summary に `0 issues in 1 file` が出ない
  - 標準エラー summary に `| syntax-check |` が含まれる

## 動作確認結果

### サンプル再現
- 実装後に `dotnet run --project src/Seiton -- .github/workflows/test.yaml`（`samples/parser` 直下）で確認し、以下を確認:
  - duplicate `env` が parse error として表示
  - summary が `1 error in 1 file`
  - 走査は継続され、通常の診断出力・summary 出力を維持

### テスト
- `dotnet test` 実施。
- 結果:
  - `tests/Seiton.Update.Tests` passed
  - `tests/Seiton.Tests` passed
  - `tests/Seiton.Core.Tests` passed
  - `tests/Seiton.Playground.Tests` passed（1 test skipped: `TypingIncrementalDeployJob_RepeatedEdits_DoNotCrashRuntime`）

## ベンチマーク結果（CoreParsingBenchmark）

- 実行コマンド:
  - `cd src/Seiton.Benchmark`
  - `dotnet run -c Release --filter "*CoreParsingBenchmark*"`
- 実測（`src/Seiton.Benchmark/BenchmarkDotNet.Artifacts/results/Seiton.Benchmark.CoreParsingBenchmark-report-default.md`）
  - `WorkflowParser.Parse (AST + rules)` Small: **78.393 us**, **3.84 KB**
  - `WorkflowParser.Parse (AST + rules)` Medium: **1,692.683 us**, **35.21 KB**
  - `WorkflowParser.Parse (AST + rules)` Large: **31,280.822 us**, **178.16 KB**

### ベースライン比較（参考）
- 参照: `BenchmarkDotNet.Artifacts/results/Seiton.Benchmark.CoreParsingBenchmark-report-default.md`
  - Small: 108.255 us -> 78.393 us（約 **-27.6%**）
  - Medium: 1,948.793 us -> 1,692.683 us（約 **-13.1%**）
  - Large: 36,128.127 us -> 31,280.822 us（約 **-13.4%**）
  - Allocated は Small/Medium/Large すべて **同等**（3.84 KB / 35.21 KB / 178.16 KB）

### 所見
- 今回追加した duplicate 判定は `bit mask + stackalloc` で実装しており、追加検出のためのヒープ割り当ては増やしていない。
- 測定上は平均時間が改善し、少なくとも本変更に起因する性能劣化は確認されなかった。

## フェーズレビュー（セルフレビュー）

1. Correctness
- duplicate key を検出しても fatal 化せずに継続する要件を満たした。

2. Performance
- hot path は定数時間判定（bit 操作）を維持し、割り当て増なし。

3. User-first API / UX
- ユーザーは `0 issues` の誤判定から、具体的な修正可能診断（duplicate key）を受け取れるようになった。

4. Spec 整合
- 「最後まで検出を諦めない」方針と整合（ターミネートせず診断を追加）。

## P1: 高（ユーザー理解の改善）

1. duplicate `env` 向け Help メッセージの追加
- 例: 「YAML mapping key は一意である必要があります。`env` は1つにまとめてください。」
- 目的: 何をどう直せばよいかを明示し、ヒント要件を満たす。

2. 診断文言の統一
- 他セクションの duplicate key 文言と同じトーン/フォーマットに合わせる。
- 目的: 既存ルール検出と同じ読み心地に寄せる。

## 実装結果（2026-06-11）— P1

### P1-1 Help メッセージ
- 実装済み。
- 変更ファイル: `src/Seiton.Core/Parsing/WorkflowParser.Steps.cs`, `WorkflowParser.ScalarParsing.cs`
- 内容:
  - step-level duplicate 診断に `Help` を付与（`AddError(..., help)` オーバーロード追加）。
  - `env` 重複時: `YAML mapping keys must be unique. Merge variables into a single env: block.`
  - その他キー: `YAML mapping keys must be unique. Keep only one "{key}" key in this step.`

### P1-2 診断文言の統一
- 実装済み。
- 変更前: `jobs.'build'.steps[1] key "env" is duplicated. previously defined at line:X,col:Y`
- 変更後: `jobs.'build'.steps[1] key "env" is duplicated in step. previously defined at line:X,col:Y`
- job/section 形式（`is duplicated in "{container}" job/section`）と同じトーンに `"in step"` を追加。

### P1 テスト
- `tests/Seiton.Core.Tests/ParserTests.cs`
  - `Parse_StepDuplicateEnv_IncludesHelp`
  - `Parse_StepDuplicateEnv_MessageFormatMatchesOtherSections`
- `tests/Seiton.Tests/CheckCommandTests.cs`
  - `Check_TextMode_DuplicateStepEnv_IsReportedAndSummaryIsNotZeroIssues`（文言更新）

### P1 ベンチマーク（CoreParsingBenchmark）

| Size | Before (us) | After P1 (us) | Delta | Allocated |
|------|-------------|---------------|-------|-----------|
| Small | 45.328 | 45.777 | +1.0% | 3.84 KB（同等） |
| Medium | 1,050.791 | 1,065.58 | +1.4% | 35.21 KB（同等） |
| Large | 17,195.129 | 18,448.55 | +7.3% | 178.16 KB（同等） |

- 所見: Help 文字列はエラー経路（コールドパス）のみで生成。hot path の bit mask 判定は不変。+10% 以内であり性能劣化なし。

### P1 フェーズレビュー
1. **Correctness**: Help が CLI `help:` として表示される（`DiagnosticFormatter` 経由）。文言は job duplicate と整合。
2. **Performance**: エラー時のみ追加割り当て。ベンチマーク +10% 以内。
3. **User-first API**: ユーザーは「何を直すか」（1 つの env にまとめる）を Help で受け取れる。
4. **Spec 整合**: `Seiton_Parser_spec.md` に step duplicate + Help を追記。

## P2: 中（運用耐性の向上）

1. 同一 step での複数重複ケース網羅
- `env` 以外（`run`, `uses`, `with`, `shell` など）でも duplicate が検出されることをテスト。

2. non-fatal 継続検証
- duplicate key があっても、同ファイル内の後続 rule diagnostics が引き続き出ることを検証。

## 実装結果（2026-06-11）— P2

### P2-1 複数キー重複テスト
- 実装済み（テストのみ。P0 の bit mask が全 known keys を既にカバー）。
- 変更ファイル: `tests/Seiton.Core.Tests/ParserTests.cs`
- 追加テスト: `Parse_StepDuplicateKnownKeys_TableDriven`（`run`, `uses`, `with`, `shell`）

### P2-2 non-fatal 継続検証
- 実装済み（テストのみ）。
- 追加テスト: `Lint_StepDuplicateEnv_DoesNotSuppressSubsequentRuleDiagnostics`
- 検証内容:
  - `HasFatalError` は false
  - duplicate `env` 診断と `expr-undefined-var` ルール診断が同一 LintEngine.Check 結果に共存

### P2 ベンチマーク
- 本番コード変更なしのため再計測不要。P1 時点の結果を維持。

### P2 フェーズレビュー
1. **Correctness**: `run`/`uses`/`with`/`shell` 重複が non-fatal で検出。後続ステップの lint も継続。
2. **Performance**: 変更なし。
3. **User-first API**: 1 ファイル内の複数問題を一度に把握できる挙動をテストで固定。
4. **Spec 整合**: P0/P1 仕様と矛盾なし。

## 受け入れ基準

1. 提示サンプルで `seiton` 実行時に duplicate `env` がエラーとして表示される。
2. summary が `0 issues` ではなく、error 件数を表示する。
3. duplicate 検出後も処理継続し、同ファイルの他問題も報告できる。
4. 追加テストが通過し、既存テストを壊さない。

## TODO（次セッション）— CLI サマリー: parser 診断のルール別集計

### 背景

`dotnet publish` 後に `samples/parser` で実行すると、診断本文は `error[parse]:` と表示されるが、末尾のルール別テーブル（`| Rule | Count |`）には parser 診断が載らない。

```
1 error in 1 file

| File      | Errors | Warnings |
|-----------|-------:|---------:|
| test.yaml |      1 |        0 |

（ルール別テーブルなし）
```

件数サマリーとファイル別テーブルは正しいが、「何のエラーか」のカテゴリがサマリーに残らず、本文を読まないと把握できない。

### 現状（parser と syntax-check の整理 — 2026-06-11 実装後）

Seiton 内部では **parser 診断と lint rule 診断は分離**されている。ユーザー向け表示 ID は **`syntax-check` に統一済み**。

| レイヤ | 内部 `RuleId` | CLI 表示 | actionlint 互換タグ |
|--------|---------------|----------|---------------------|
| Parser 構文診断（duplicate key 等） | `null` | `syntax-check`（`DiagnosticDisplayRuleIds.Resolve`） | `syntax-check` |
| VYaml fatal（YAML 壊れ） | `null` | `syntax-check` | `syntax-check` |
| Lint rule 診断 | 各 rule id（例: `expr-undefined-var`） | その rule id | 互換マップ経由で `syntax-check` になるものあり |
| `RuleId.Syntax`（`SyntaxRule`） | `"syntax"` | `"syntax"` | Seiton-only（actionlint 非対応） |

要点:

- 内部 `RuleId: null` のまま。抑制不可・`seiton rules` 非掲載。
- CLI 本文・JSON・SARIF・ルール別サマリーはすべて `syntax-check` で一貫。
- `DiagnosticDisplayRuleIds.cs` が表示レイヤーの単一フォールバック。

### 案 B: parser 診断をルール別サマリーに集計（採用 → syntax-check に統一）

**表示レイヤー**で `DiagnosticDisplayRuleIds.Resolve(ruleId)` を使い、診断本文の `error[syntax-check]:` と揃える。

期待出力:

```
1 error in 1 file

| File      | Errors | Warnings |
|-----------|-------:|---------:|
| test.yaml |      1 |        0 |

| Rule          | Count |
|---------------|------:|
| syntax-check  |     1 |
```

**「デフォルトでもルール表を出す」について**: ルール表自体は既にデフォルト（非 verbose）で出力される。parser 診断のみのケースでも `syntax-check` 行が出る — 別フラグや verbose 不要。

### 実装スコープ（完了）

1. `CheckCommand.WritePerRuleBreakdown` — `DiagnosticDisplayRuleIds.Resolve(ruleId)` で集計。
2. `CheckCommand.ShouldOfferFullPerRuleBreakdownHint` — 同上。
3. `DiagnosticFormatter` — text / JSON / SARIF すべて `Resolve` 経由。
4. テスト更新（`WriteSummaryTests`, `CheckCommandTests`, `DiagnosticFormatterRichTextTests`）
5. 仕様: `Seiton_CLI_spec.md`, `docs/usage.md`, `README.md`

### 受け入れ基準（達成）

1. parser 診断のみの workflow で、stderr サマリーに `| syntax-check | N |` が出る。
2. 診断本文の `error[syntax-check]:` とサマリーの Rule 列が一致する。
3. lint rule 診断との混在時、両方がルール表に載る。
4. 既存 `WriteSummaryTests` / `CheckCommandTests` が通過。

## 実装結果（2026-06-11）— CLI サマリー: parse 集計

### 実装内容
- 変更ファイル: `src/Seiton/Commands/CheckCommand.cs`
  - `WritePerRuleBreakdown`: `RuleId ?? "parse"` で集計（`DiagnosticFormatter` と同一フォールバック）。
  - `ShouldOfferFullPerRuleBreakdownHint`: 同上（parser のみでも distinct rule として `parse` を数える）。
- テスト:
  - `WriteSummary_NotVerbose_ParserOnlyDiagnostics_ShowsParseInRuleBreakdown`（新規）
  - `WriteSummary_NotVerbose_ParserAndLintDiagnostics_ShowBothInRuleBreakdown`（新規）
  - `WriteSummary_Verbose_ParserDiagnosticsWithNullRuleId_GroupedSeparately`（`| parse |` 期待に更新）
  - `Check_TextMode_DuplicateStepEnv_IsReportedAndSummaryIsNotZeroIssues`（stderr に `| parse |` を追加）
- 仕様: `.github/docs/Seiton_CLI_spec.md` §6.4 に pseudo rule ID `parse` を追記。

### ベンチマーク（StepSummaryOutputBenchmark）

| Method | Before | After | Delta | Allocated |
|--------|--------|-------|-------|-----------|
| WriteSummary stderr (text) | 17.90 us | 17.30 us | -3.4% | 3.63 KB（同等） |
| WriteSummary step summary (github-actions) | 385.97 us | 388.20 us | +0.6% | 15.59 KB（同等） |

- 所見: 表示レイヤーの `?? "parse"` のみ。hot path への影響なし。Allocated 不変。

### フェーズレビュー
1. **Correctness**: parser-only / parser+lint 混在の両方でルール表に `parse` が出る。本文 `error[parse]:` と一致。
2. **Performance**: サマリー生成は診断出力後のコールドパス。ベンチマーク ±10% 以内。
3. **User-first API**: サマリーだけ読んでも「parse エラーが N 件」と把握できる。
4. **Spec 整合**: `Seiton_CLI_spec.md` を更新済み。内部 `RuleId: null` は維持（lint 層との分離不変）。

## 実装結果（2026-06-11）— 表示 ID 統一: `parse` → `syntax-check`

### 方針

- 内部 `Diagnostic.RuleId` は **null のまま**（LintEngine 付与・抑制可能ルール化はしない）。
- ユーザー向け出力（text / JSON / SARIF / サマリー / actionlint 互換）のみ `DiagnosticDisplayRuleIds.ParserSyntaxCheck`（`syntax-check`）に統一。
- actionlint の `[syntax-check]` タグと一致。compat テストの `parse` → `syntax-check` 中間マップを削除。

### 実装内容

- 新規: `src/Seiton.Core/Parsing/DiagnosticDisplayRuleIds.cs`
  - `ParserSyntaxCheck = "syntax-check"`
  - `Resolve(string? ruleId)` — null のみフォールバック（定数時間、追加割り当てなし）
- 更新:
  - `DiagnosticFormatter.cs` — text / JSON / SARIF
  - `CheckCommand.cs` — per-rule サマリー
  - `ActionlintCompatTests.cs` / `ActionlintExamplesCompatTests.cs`
  - `README.md`, `docs/usage.md`, `Seiton_CLI_spec.md`

### テスト

- `DiagnosticFormatterRichTextTests.Oneline_NullRuleId_UsesSyntaxCheckLabel`
- `WriteSummaryTests` / `CheckCommandTests` — `| syntax-check |`
- `StructureSnippetTests` — 擬似 `RuleId: "parse"` を `null` に修正（実際の parser 診断に合わせる）
- 全テスト **2647 passed**

### ベンチマーク（StepSummaryOutputBenchmark）

| Method | parse 集計時 | syntax-check 統一後 | Delta | Allocated |
|--------|-------------|----------------------|-------|-----------|
| WriteSummary stderr | 17.30 us | 18.30 us | +5.8% | 3.63 KB（同等） |
| WriteSummary GHA | 388.20 us | 390.97 us | +0.7% | 15.59 KB（同等） |

- 所見: `Resolve()` は null 分岐 + 定数参照のみ。±10% 以内。Allocated 不変。

### フェーズレビュー

1. **Correctness**: CLI・JSON・SARIF・サマリー・compat がすべて `syntax-check`。内部 null / 抑制モデルは不変。
2. **Performance**: 表示レイヤーのみ。ベンチマーク ±10% 以内。
3. **User-first API**: actionlint ユーザーに馴染む ID。`seiton rules` には載せず抑制不可を維持。
4. **Spec 整合**: CLI spec / usage / README 更新済み。

## 実装時の注意

- fatal parse error へ昇格しない。
- duplicate key の扱いは steps 以外の既存実装に合わせる（first-win + duplicate 側 skip が自然）。
- パフォーマンス回帰を避けるため、既存と同等の軽量判定（bit mask）を使う。

## コードレビュー結果（2026-06-11 — 最終）

### ラウンド 1 指摘と対応

| 指摘 | 対応 |
|------|------|
| P0-2 が plan 上「未完了」のまま | `Parse_StepDuplicate*` 群で完了と明記 |
| 等価クラス: 単一 `env`（negative）のテスト不足 | `Parse_StepSingleEnv_NoDuplicateDiagnostic` 追加 |
| 等価クラス: first-win（2 番目 skip）の AST 検証不足 | `Parse_StepDuplicateEnv_FirstOccurrenceWins_SecondEnvSkipped` 追加 |
| 等価クラス: env 以外の Help | `Parse_StepDuplicateShell_IncludesGenericHelp` 追加 |
| CLI E2E: stdout に `error[syntax-check]:` 未検証 | `CheckCommandTests` に assertion 追加 |
| JSON 出力の null RuleId → syntax-check 未検証 | `Json_Format_NullRuleId_UsesSyntaxCheckLabel` 追加 |
| Rich 出力: lint rule id が誤って syntax-check にならない negative | `Rich_ExplicitRuleId_PreservesRuleId` 追加 |
| plan TODO 節が `parse` 時代の記述のまま | 現状表・期待出力を `syntax-check` に更新 |
| sandbox `CheckVYamlExceptionFormat.cs` が `?? "parse"` | `DiagnosticDisplayRuleIds.Resolve` に更新 |

### ラウンド 2（再レビュー）

- 分類ロジック（duplicate bit mask）: positive（各 known key）+ negative（単一 env）+ first-win をカバー。追加指摘なし。
- `DiagnosticDisplayRuleIds.Resolve`: null → syntax-check、非 null 保持。formatter / summary / compat 一致。追加指摘なし。
- 内部 `RuleId: null` 維持、抑制不可。ユーザー向け ID は actionlint 互換。API 触り心地 OK。
- spec 整合: `Seiton_Parser_spec.md` §step duplicate + `Seiton_CLI_spec.md` §syntax-check サマリー。plan 更新済み。

### ベンチマーク（コードレビュー後 — 2026-06-11）

| Benchmark | Method | Mean | Allocated | vs 実装時 baseline | 判定 |
|-----------|--------|------|-----------|-------------------|------|
| CoreParsingBenchmark | Parse (Small) | 45.28 us | 3.84 KB | duplicate 検出実装時と同等 | OK |
| CoreParsingBenchmark | Parse (Medium) | 1.05 ms | 35.21 KB | 同等 | OK |
| CoreParsingBenchmark | Parse (Large) | 17.16 ms | 178.16 KB | 同等 | OK |
| StepSummaryOutputBenchmark | WriteSummary stderr | 17.70 us | 3.63 KB | syntax-check 統一時 18.30 us → **-3.3%** | OK |
| StepSummaryOutputBenchmark | WriteSummary GHA | 421.20 us | 15.59 KB | syntax-check 統一時 390.97 us → +7.7% | OK（±10% 以内） |

- 全テスト **2653 passed**（+6 レビュー追加分）、1 skipped（Playground）
- 所見: duplicate bit mask は hot path への measurable 回帰なし。表示レイヤー `Resolve()` も ±10% 以内。
