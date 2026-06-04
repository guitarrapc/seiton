# ルール名/コンフィグ名変更プラン（trigger 明確化）

## 背景

`cache-poisoning` と `self-hosted-runner` は、実際には「untrusted trigger と組み合わさったときの検出」を行うルールだが、現在の rule-id / config key だと「対象そのものを検出するルール」に見えやすい。

そのため、次の命名に変更して意図を明示する。

- `cache-poisoning` -> `cache-poisoning-trigger`
- `self-hosted-runner` -> `self-hosted-runner-trigger`

## 目標

1. ルールID・コンフィグキー・ドキュメント表記を trigger ルールだと分かる名前へ統一する。
2. 破壊的変更を許容し、旧ID/旧キーは受け付けない。
3. 仕様（`.github/docs`）と公開 docs（`docs/`）と実装/テストを同期する。

## 影響範囲（調査結果）

### 1) 実装コア（rule-id 解決/正規化）

- `src/Seiton.Core/Linting/RuleIdExtensions.cs`
  - `RuleId.CachePoisoning` / `RuleId.SelfHostedRunner` の `ToId()` が公開 rule-id 文字列の実体。
- `src/Seiton.Core/Linting/RuleCatalog.cs`
  - rule-id からの解決 (`TryResolveRuleId`)、候補提案 (`SuggestRuleId`)、優先度、severity、allowed config key (`UntrustedTriggers`) に影響。
- `src/Seiton.Core/Linting/RuleNormalizer.cs` / `ExclusionNormalizer.cs`
  - `rules:` / `exclusions.rules:` の未知 rule-id 診断に影響。
- `src/Seiton.Core/Linting/LintEngine.cs`
  - 設定読み込み・除外解決で rule-id 解決を使うため、実行時挙動に影響。

### 2) 設定モデル/テンプレート/サンプル

- `src/Seiton.Core/Linting/LintConfigLibrary.cs`
  - `GenerateTemplateYaml()` のサンプルキー (`cache-poisoning:` など)。
- `.github/seiton.yaml`
- `samples/docs-init-output/seiton.yaml`
- `samples/docs-init/.github/seiton.yaml`
- `src/Seiton.Benchmark/ConfigYamlBuilder.cs`

### 3) ルール実装（クラス名は据え置き可）

- `src/Seiton.Core/Linting/Rules/CachePoisoningRule.cs`
- `src/Seiton.Core/Linting/Rules/SelfHostedRunnerRule.cs`

※ クラス名/enum 名（`CachePoisoning`, `SelfHostedRunner`）は内部名として残せるが、`Id` が返す公開文字列のみ変更すれば外部見え方は改善できる。

### 4) テスト

- `tests/Seiton.Core.Tests/LintConfigLibraryTests.cs`
  - `rules["cache-poisoning"]` 参照や old syntax パラメータ。
- `tests/Seiton.Core.Tests/RuleInterfaceTests*.cs`
  - ルールID文字列期待値、priority 参照、diagnostics 判定。
- `tests/Seiton.Core.Tests/ActionlintCompatTests.cs`
- `tests/Seiton.Core.Tests/ActionlintExamplesCompatTests.cs`
- `tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/config/**`
  - actionlint互換fixtureのキー（`self-hosted-runner`）の扱いを要確認。

### 5) ドキュメント/仕様/skills 参照

- 仕様
  - `.github/docs/Seiton_Linter_spec.md`
  - `.github/docs/Seiton_config_spec.md`
  - `.github/docs/Seiton_Linter_csharp_spec.md`
  - `.github/docs/Seiton_Linter_go_spec.md`
  - `.github/docs/Seiton-feature-matrix.md`
  - `.github/docs/competitor-zizmor-structure-details.md`
- 公開 docs
  - `docs/rules.md`
  - `docs/configuration.md`
- skills/references
  - `src/Seiton/Skills/references/rules.md`
  - `src/Seiton/Skills/references/configuration.md`
  - `.claude/skills/seiton/references/rules.md`
  - `.claude/skills/seiton/references/configuration.md`

## 移行方針（確定: 破壊的変更）

- canonical は `cache-poisoning-trigger` / `self-hosted-runner-trigger` のみ。
- 旧ID (`cache-poisoning`, `self-hosted-runner`) は alias 非対応。
- `rules:` / `exclusions.rules:` / API出力 / docs の全表記を新IDへ一括置換。
- 旧ID入力時は通常の unknown rule-id error を返す。

## 実施フェーズ

### Phase 1: 破壊的 rename 実装

- `RuleIdExtensions.ToId()` を新 canonical ID に変更。
- `TryParse` は新IDのみ受理（旧IDは unknown 扱い）。
- `RuleCatalog` の priority/severity/allowed-key は既存 enum ベースで維持。
- `LintConfigLibrary` テンプレート、benchmark config builder、テスト期待値を新IDへ変更。
- APIの使い勝手確認（「trigger」意図が rule-id で即座に伝わるか）をレビュー観点に含める。

完了条件:

- 新IDのみ設定読み込み可能。
- 旧IDは unknown rule-id で失敗する。
- 出力診断の rule-id は新IDで安定。

### Phase 2: 設定テンプレート/サンプル更新

- `GenerateTemplateYaml`、`.github/seiton.yaml`、`samples/**`、benchmark 用 builder のキーを新IDへ更新。
- `docs-init` 相当の出力検証テスト更新。

完了条件:

- 新規ユーザーが触れるサンプルはすべて新ID表記。

### Phase 3: テスト一式更新

- 既存テスト期待値を新IDへ更新。
- 追加テスト:
  - 新ID accepted
  - 旧ID rejected（unknown rule-id）
  - `exclusions.rules` 側も同様に新IDのみ受理

完了条件:

- `dotnet test` グリーン。
- 旧ID reject テストと canonical テスト（新ID出力）が両立。

### Phase 4: 仕様/公開 docs/skills 同期

- `Seiton_config_spec.md` の rule-specific key 対象を新IDへ更新。
- `Seiton_Linter_spec.md` の rule catalog と該当節を新IDへ更新。
- `docs/rules.md` 見出し・目次・config 参照アンカーを新IDへ更新。
- `docs/configuration.md` の表、節見出し、アンカーを新IDへ更新。
- skills reference の rule/config 一覧を同期。

完了条件:

- 仕様・実装・docs で rule-id 表記差分がない。

## リスクと対策

- 既存ユーザー設定破壊
  - 対策: changelog / release note / docs で breaking change と移行先IDを明記。
- suppression/exclusion の効かなくなるリスク
  - 対策: `exclusions.rules` の新ID受理と旧ID拒否を必須テスト化。
- docs のアンカー切れ
  - 対策: `configuration.md#...` 参照元（`docs/rules.md`）を同PRで更新。
- actionlint 互換fixtureとの乖離
  - 対策: fixture は外部互換要件を確認し、必要なら「fixtureは旧キー維持・内部正規化で吸収」と明記。

## PR分割案

1. Core rename (breaking) + tests（実装中心）
2. docs/spec/skills 同期（文書中心）

この順に分けると、実行互換性のレビューとドキュメントレビューを分離できる。

## 受け入れ基準（Definition of Done）

- 新ID（`*-trigger`）で設定/出力/ドキュメントが統一されている。
- 旧IDは受理されず、unknown rule-id で明確に失敗する。
- ルール名変更に伴う参照更新漏れがない（`rg "cache-poisoning|self-hosted-runner"` で旧ID残存が許可対象以外にない）。
- 仕様ドキュメント（`.github/docs`）と公開 docs（`docs/`）が一致している。

## Phase 1 実装結果（breaking rename）

### 実装内容

- `RuleIdExtensions.ToId()` を以下へ変更。
  - `cache-poisoning` -> `cache-poisoning-trigger`
  - `self-hosted-runner` -> `self-hosted-runner-trigger`
- 旧ID alias は追加せず、`TryParse/TryResolveRuleId` は新IDのみ受理。
- `rules.<id>.untrusted-triggers` の対象キーを新IDに合わせて更新。
  - `src/Seiton.Core/Linting/LintConfigLibrary.cs` テンプレート
  - `src/Seiton.Benchmark/ConfigYamlBuilder.cs`
  - `.github/seiton.yaml`, `samples/docs-init*`
- テスト更新（TDD）
  - Red: 新ID受理/旧ID拒否テストを先に追加し、失敗確認。
  - Green: 実装と既存期待値更新で通過。
  - 追加した代表テスト:
    - `Validate_TriggerRuleIds_AcceptsTriggerSuffixAndRejectsLegacyIds`
    - `Validate_LegacyTriggerRuleIds_ReturnsUnknownRuleIdErrors`
    - `RuleCatalog_TriggerRuleIds_UseTriggerSuffixOnly`
- 仕様/ドキュメント同期
  - `.github/docs/Seiton_Linter_spec.md`
  - `.github/docs/Seiton_config_spec.md`
  - `.github/docs/Seiton_Linter_csharp_spec.md`
  - `.github/docs/Seiton_Linter_go_spec.md`
  - `docs/configuration.md`, `docs/rules.md`
  - skills references（`src/Seiton/Skills/...`, `.claude/skills/...`）

### ユーザーファーストAPI観点レビュー

- **指摘 1:** 旧IDは trigger 用ルールである意図が名前から読み取れない。
  - **対応:** 新IDを `*-trigger` に統一し、rule-id だけで役割が推測できるようにした。
- **指摘 2:** config key と docs 見出し/アンカーが旧IDのままだと利用者が混乱する。
  - **対応:** config key / docs anchor / spec path をすべて新IDへ同期。
- **指摘 3:** 破壊的変更時の挙動が曖昧だと利用者が原因特定しづらい。
  - **対応:** 旧IDを alias 受理せず `unknown rule-id` を返す設計に統一し、失敗理由を明確化。

### パフォーマンス検証

対象ベンチマーク（Rule ID 解決と config 処理のホットパス）:

- `RuleCatalogBenchmark`
- `LintConfigBenchmark`

実行コマンド:

```shell
dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj -- --filter "*LintConfigBenchmark*" "*RuleCatalogBenchmark*" --job short
```

比較結果（`BenchmarkDotNet.Artifacts/results` 既存レポート比）:

- `RuleCatalogBenchmark`
  - `RuleIdExtensions.TryParse(string)`: **6.7599 ns -> 6.7599 ns**（差分 0%）
  - `TryResolveRuleId(string)`: **7.9389 ns -> 7.9389 ns**（差分 0%）
- `LintConfigBenchmark`
  - `LintConfigLibrary.Validate (Typical)`: **17,496.3 ns / 23.45 KB -> 同値**
  - `LintConfigLibrary.Validate (Heavy)`: **43,152.6 ns / 49.79 KB -> 同値**

評価:

- Mean/Allocated ともに変化なし（+10% ルール内）。
- 理由: 文字列定数の置換のみで、解決アルゴリズム・データ構造（`FrozenDictionary`）は不変。

### 回帰確認

- 追加した red テストを green 化済み。
- `dotnet test` 全体実行: **2459 passed / 0 failed**

### セルフレビュー反復

- Round 1 指摘: 新旧ID受理テストの YAML が入れ替わっており、意図と逆の検証になっていた。
  - 対応: テストデータを修正し、対象2テスト + 全体テストを再実行して解消。
- Round 2 指摘: rule-id 変更に対する docs/spec 同期漏れが一部存在。
  - 対応: linter/config spec + 公開 docs + skills references を同期。
- Round 3 指摘: lints チェック。
  - 対応: `ReadLints` で対象変更ファイルのエラーなしを確認。
