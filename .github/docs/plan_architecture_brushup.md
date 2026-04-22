# Seiton.Core アーキテクチャ改善計画

> `src/Seiton.Core/` のコード品質レビュー結果と改善提案。
> 対象: Parsing (45 files, ~10,500 lines), Linting (82 files, ~14,700 lines), Generated (6 files, ~725 lines)

---

## 1. 総合評価

全体として Seiton.Core はアーキテクチャ仕様 (`Seiton_spec.md`, `architecture_spec_csharp.md`) に沿った設計になっている。
Parser/Linter の責務分離、Adapter パターンによる YAML ライブラリ隔離、UTF-8 ファーストの性能設計は一貫性がある。

一方で、コードベースが成長するに伴い以下のカテゴリの課題が顕在化している。

| カテゴリ | 深刻度 | 対象エリア |
|---|---|---|
| 責務の集中・肥大化 | High | WorkflowParser partials, LintEngine |
| ポリシーロジックの重複 | High | LintEngine ↔ LintConfigLibrary |
| 診断メッセージへの構造的依存 | High | PinRemediation |
| IPass のドキュメントカインド非対称性 | Medium | WorkflowVisitor, IPass |
| 設定パーサーの維持コスト | Medium | LintConfigLineParser |
| アクション参照パース処理の分散 | Medium | Rules 内の ad-hoc 解析 vs ActionRefHelpers |
| Online ルールとローカルルールの契約差異 | Medium | OnlineAuditEngine vs IRule |
| ユーティリティの責務混在 | Low | WorkflowParser.ScalarParsing / ExpressionIntegration, SpanHelpers |

---

## 2. 問題点と改善提案

### 2.1 [High] WorkflowParser partial の肥大化と繰り返しパターン

**問題**

`WorkflowParser` は複数の partial に分割されているが、合計 ~6,000 行の static partial class であり、以下の問題があった。

- ~~`WorkflowParser.On.cs` (1,692 行)~~ → **責務別に 7 partial**（`On.Core` / `On.Schedule` / `On.WorkflowDispatch` / `On.WorkflowCall` / `On.RepositoryDispatch` / `On.ImageVersion` / `On.Webhook`）へ分割済み。
- `WorkflowParser.Jobs.cs` の `ParseJobNode` はキー共通化で整理済み（以下 実装状況）。
- 各 partial で同じ「キーチェック → パース → エラー → スキップ」パターンが手書きで反復されている。
- ~~`WorkflowParser.Primitives.cs`~~ → **`WorkflowParser.ScalarParsing.cs`** / **`WorkflowParser.ExpressionIntegration.cs`** に分割済み（旧ファイルは削除）。

**コンセプトとの乖離**

仕様は "hand-written recursive descent" を採用理由として挙げているが、パターンの機械的反復はメンテナンスリスクを高めており、 hand-written の利点（柔軟なリカバリ、文脈依存チェック）が活きない箇所まで冗長になっている。

**改善提案**

1. キーディスパッチを UTF-8 キーテーブル + 共通照合ヘルパーで一般化し、mapping 走査の「キー照合」定型を共通化する。パース本体の文脈依存ロジックは **呼び出し側の `switch`（静的ディスパッチ）** に残す（後述の実装状況）。
2. ~~`WorkflowParser.Primitives.cs` を 2 つに分割~~ **実施済み**: `WorkflowParser.ScalarParsing.cs`（純粋スカラー変換・`TryParse*`・位置ヘルパー・マッピング補助・`AddError` 等）と `WorkflowParser.ExpressionIntegration.cs`（`ParseExpression` / `ValidateExpressionText` / `ExpressionParser` 連携等）。
3. ~~`WorkflowParser.On.cs` のイベント種別ごとのパーサーを切り出す~~ **実施済み（責務分離優先）**: 単一ファイルを廃止し、イベント種別・役割ごとに partial を分割。行数上限は目的とせず、**編集単位をイベント／サブドメインに揃える**。

**実装状況（§2.1 提案 1、2026-04-22 時点）**

| 項目 | 内容 |
|---|---|
| 追加ファイル | `src/Seiton.Core/Parsing/Utf8MappingDispatch.cs` — `IUtf8OrderedKeyTable`（`KeyCount` / `Utf8Key(int)`）と `Utf8MappingDispatch.TryMatchFirstOrdered<TTable>`。キー行は空の `readonly struct` が静的メソッドで供給し、ジェネリック特殊化を期待。照合はヒープ割り当てなし。 |
| 適用箇所 | `WorkflowParser.Jobs.cs` の `ParseJobNode`: `JobNodeMappingKey` 列挙子（ordinal = 重複検出用ビット番号）と `JobNodeKeyTable : IUtf8OrderedKeyTable` をロックステップで定義し、`TryMatchFirstOrdered<JobNodeKeyTable>` 成功後に `switch` で各キーのパース処理を実行。 |
| 当初案（delegate）との差分 | `ReadOnlySpan<byte>` をキャプチャする delegate は使えないうえ、ホットパスでは `Invoke` の間接呼び出しが不利。.NET 10 では `ReadOnlySpan<ReadOnlySpan<byte>>` のような ref struct の入れ子も不可（CS9244）のため、**静的抽象インターフェイス + `Utf8Key(ordinal)` の switch** でテーブルを表現した。 |
| 未実施 | `ParseJobNode` 以外（`runs-on` マッピング等）へのキー共通化は未着手。 |

**実装状況（§2.1 提案 2、Primitives 分割）**

| ファイル | 主な責務 |
|---|---|
| `WorkflowParser.ScalarParsing.cs` | `ParseBool` / `ParseString` / `ParseInt` / `ParseFloat` / `ParseStringOrStringSequence`、`ParseBoolNode`（`on.*` / action-metadata 用）、`TryParseBool|Int64|Double`、`BuildScalarLocation` / `BuildCompositeLocation` / `ShiftLocation` / `BuildLocationFromSourceSlice`、`TrySetBit`、`IsMergeKey`、`TryRegisterDynamicKey`、`DecodeUtf8`、`FormatContainerSectionName`、`AddError`。 |
| `WorkflowParser.ExpressionIntegration.cs` | `MayParseExpression`、`ParseExpression`、`ParseStringAndValidateExpression`、`ParseFloatOrExpression`、`ParseConditionalExpression`、`ValidateExpressionText`、`ParseAndValidateExpression`（`ExpressionParser.ParseAndValidateInline` 委譲）、`ContainsExpression`。 |
| 削除 | `WorkflowParser.Primitives.cs`（内容は上記 2 ファイルへ移動。`partial class` のため API 互換）。 |

**実装状況（§2.1 提案 3、`on` パーサー分割）**

| ファイル | 責務（概略） |
|---|---|
| `WorkflowParser.On.Core.cs` | `ParseOnEvents`（scalar / sequence / mapping）、`BuildSimpleEvent`、`ParseOnEventWithOptions`、`IsSpecialOnEvent`、`IsNullLikeOnEventOptionsScalar`、`ValidateKnownOnEvent`、`ReadOnEventInfo`。 |
| `WorkflowParser.On.Schedule.cs` | `on.schedule` — `ParseScheduleEvent`、`ParseScheduleEntry`。 |
| `WorkflowParser.On.WorkflowDispatch.cs` | `on.workflow_dispatch` — 本体・`inputs`・`DispatchInput` フィールド。 |
| `WorkflowParser.On.WorkflowCall.cs` | `on.workflow_call` — inputs / secrets / outputs と各子マッピング。 |
| `WorkflowParser.On.RepositoryDispatch.cs` | `on.repository_dispatch`。 |
| `WorkflowParser.On.ImageVersion.cs` | `on.image_version`。 |
| `WorkflowParser.On.Webhook.cs` | 汎用 webhook — `ParseWebhookEventWithOptions`、`ParseOnTypesNodes`、`ParseStringSequence`、`ParseOnEventOptions`、`ParseOnTypes`。 |
| 削除 | 単体 `WorkflowParser.On.cs`（論理は上記へ移譲。`partial` のため呼び出し互換）。 |

**リスク**: パーサーのリファクタは回帰バグの温床になるため、既存テストが全パス完了するまで変更を分割適用すること。

---

### 2.2 [High] LintEngine と LintConfigLibrary のポリシーロジック重複

**問題**

ルール正規化（ID 解決、non-disableable チェック、minimum-severity チェック、RuleSpecificConfig 正規化）が以下の 2 箇所に実質同一のコードとして存在する。

- `LintEngine.NormalizeRules()` (ランタイム lint 実行パス)
- `LintConfigLibrary.NormalizeRules()` (設定ファイルバリデーションパス)

同様に exclusion 正規化も 2 箇所に分かれている（`LintEngine.NormalizeExclusions()` と `LintConfigLibrary.NormalizeExclusions()`）。

差異としては LintEngine 側は `byte[] utf8Yaml` + `AstArena` を使った job-id 存在チェックを追加している点のみ。

**コンセプトとの乖離**

`Seiton_spec.md` §3 にある「Linter owns rule configuration」の責務境界が、2 つの実装に分散したことで、一方を変更した際にもう一方を同期し忘れるリスクがある。

**改善提案**

1. 共通の `RuleNormalizer` internal static クラスを導入し、rule-id 解決 + non-disableable + minimum-severity + specific-config 正規化を一元化する。
2. LintEngine 側は `RuleNormalizer` + job-id 検証のみ追加、LintConfigLibrary 側は `RuleNormalizer` のみ呼び出す構成にする。
3. 同様に `ExclusionNormalizer` を導入し、共通部分 (rule-id 解決、non-disableable チェック) を一元化する。

---

### 2.3 [High] PinRemediation の診断メッセージ依存

**問題**

`PinFixFormatter` と `PinRemediationEngine` の両方が `TryExtractQuotedValue(diagnostic.Message, ...)` で診断メッセージのシングルクォート内テキストをパースし、修正対象のアクション参照・イメージ参照を取得している。

- `TryExtractQuotedValue` が `PinFixFormatter.cs` と `PinRemediationEngine.cs` の両方に別々に定義されている（コード重複）。
- ルール側の診断メッセージ文言を変更すると、fix 生成が無言で壊れる。
- 構造化データではなく人間可読テキストに依存する脆弱な結合。

**コンセプトとの乖離**

`architecture_spec_csharp.md` §7 の Diagnostic モデルは構造化位置情報を前提としている。メッセージテキストへの構造的依存は設計意図に反する。

**改善提案**

1. `Diagnostic` に `Metadata` プロパティ (`IReadOnlyDictionary<string, string>?` 等) を追加し、ルール側が `uses-ref` や `image-ref` を構造化データとして付与する。
2. `PinFixFormatter` / `PinRemediationEngine` は `Metadata` からアクション参照を取得し、メッセージパースを廃止する。
3. `TryExtractQuotedValue` の重複定義を排除する。

---

### 2.4 [Medium] IPass の ActionMetadata 非対称性

**問題**

`IPass` は `VisitWorkflowPre/Post`, `VisitEvent`, `VisitJobPre/Post`, `VisitStep` のみ定義しており、ActionMetadata 専用のフックがない。`WorkflowVisitor.VisitActionMetadata()` は `EmptyLintWorkflow` を synthetic に渡して `VisitWorkflowPre/Post` を呼んでいる。

- ルール側で `ActionMetadata` 固有の情報（`runs.using`, `branding`, `inputs/outputs` の action-metadata 構造）にアクセスするフックがない。
- `EmptyLintWorkflow` を渡すことで、ルールの `VisitWorkflowPre` 内でワークフロー固有のフィールド参照が NullReference になるリスクを持つ。
- `LintEngine` にも `EmptyWorkflowForSuppression` として同様のダミーインスタンスがあり、二重に回避策が存在する。

**コンセプトとの乖離**

`Seiton_Linter_spec.md` §4.1 の pass hooks は workflow 中心で定義されており、action-metadata は「今後のルールセット拡充」として後回しになっている。しかし、既に `action-shell-is-required` ルール等が実装されている状態で、フック不足は設計負債になっている。

**改善提案**

1. `IPass` に `VisitActionMetadataPre(ActionMetadata)` / `VisitActionMetadataPost(ActionMetadata)` を追加する。
2. `WorkflowVisitor.VisitActionMetadata()` を更新し、新フックを使う。ダミー Workflow の注入を廃止する。
3. 既存の `VisitWorkflowPre` で `diagnostics.Clear()` している `RuleBase` のパターンは `VisitActionMetadataPre` にも適用する。
4. `Seiton_Linter_spec.md` も同期更新する。

---

### 2.5 [Medium] LintConfigLineParser の維持コスト

**問題**

`LintConfigLineParser` (1,289 行) は YAML の部分的なセマンティクスを手書きの行パーサーで再実装している。
インデントベースのステートマシンと多数の switch 分岐で構成されており、以下の懸念がある。

- YAML のエッジケース（フロースタイル、マルチラインスカラー、コメント位置）に対応しきれない可能性。
- 設定仕様に新しいセクションやキーを追加するたびに大きな変更が必要。
- パーサー部分のテストカバレッジに依存した正確性。

**コンセプトとの乖離**

Parser 仕様が VYaml アダプター経由で YAML 解析を行う方針と、設定ファイルだけ独自行パーサーを使う方針の二重基準になっている。

**改善提案**

1. 短期: 変更なし（現在の方式が動作しており、テストで保護されている）。
2. 中期: VYaml の streaming API または VYaml のデシリアライズ機能を使った設定パーサーに置き換えを検討する。lint 設定のスキーマは小さいため、パフォーマンス要件は低く VYaml デシリアライズでも十分。

---

### 2.6 [Medium] アクション参照パース処理の分散

**問題**

`ActionRefHelpers` に共通のアクション参照パース・マッチングユーティリティが存在するが、以下のルールが ad-hoc に独自の参照パースロジックを持っている。

- `ForbiddenUsesRule`: 独自の owner/repo パース、ワイルドカードマッチ。
- `UnpinnedUsesRule`: 部分的に ActionRefHelpers を使うが、ローカルアクション検証で独自のファイルシステムアクセスも持つ。
- `RefVersionMismatchRule`: 独自の tag バージョンパース。

**コンセプトとの乖離**

同一ドメインの処理が複数箇所に分散しており、パース結果の正規化が一貫していない。

**改善提案**

1. `ActionRefHelpers` に `ParsedActionRef` 構造体（owner, repo, ref, refKind, path 等）を定義し、パース結果を一元的に返す。
2. 各ルールは `ParsedActionRef` を消費するのみとし、ad-hoc パースを排除する。
3. ワイルドカードマッチも `ActionRefHelpers` に集約する。

---

### 2.7 [Medium] Online ルールとローカルルールの契約差異

**問題**

Online ルール（`known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs`）は `IRule` を実装しているが、`OnlineAuditEngine` 経由で実行され、`WorkflowVisitor` のパス traversal を経ない。

- `RuleCatalog` に ID とメタデータは登録されているが、ファクトリからは生成されない (`AdditionalRuleMetadata`)。
- `OnlineAuditEngine` が内部で直接ルールインスタンスを保持し、独自のトラバーサルで診断を収集する。
- ローカルルールとオンラインルールで実行パスが分岐しているため、suppression や severity override の適用漏れリスクがある。

**コンセプトとの乖離**

`Seiton_Linter_spec.md` §4.3 のルール契約は統一的な `IRule` ベースを前提としているが、実装では二系統のパイプラインが存在している。

**改善提案**

1. 短期: 現状の二系統モデルを明文化し、`Seiton_Linter_csharp_spec.md` に「Online ルールは post-lint phase であり IRule パス traversal を使わない」と記載する。
2. 中期: `OnlineAuditEngine` の出力診断にも suppression / severity override を適用するフィルタを LintEngine 側に追加する。
3. 長期: Online ルールも `IRule` ファクトリ登録し、`WorkflowVisitor` で traversal + 後段の非同期 resolve を組み合わせる設計を検討する。

---

### 2.8 [Low] ユーティリティの責務混在

**問題**

- ~~`WorkflowParser.Primitives.cs`~~ は `ScalarParsing` / `ExpressionIntegration` に分割済み（§2.1 提案 2）。
- `SpanHelpers.cs` は ASCII 処理、行/列計算、文字列正規化を 1 ファイルに含む。
- `RuleBase.cs` (250 行) には位置構築ヘルパー、デコードヘルパー、SHA チェック、診断追加ヘルパーなど多数の便利メソッドが混在している。

**改善提案**

- `RuleBase` の SHA チェック (`IsSha256DigestPinned`, `IsFullCommitSha`) は複数ルールと `ActionRefHelpers` で共通利用されるべきロジックであり、`ActionRefHelpers` に移動を検討する（既に一部は存在する）。
- それ以外は現状維持で問題ない。分割による効果がコスト（import 変更、テスト調整）を上回る段階で実施する。

---

### 2.9 [Low] VYamlStreamAdapter の複雑性

**問題**

`VYamlStreamAdapter.cs` (621 行) はアダプターとしては大きく、VYaml 固有のワークアラウンド（空スカラーのマーク補正、アンカー/エイリアスリプレイエンジン）を含む。

**評価**

呼び出し側から見た境界は十分にクリーンであり、VYaml 由来の複雑性を他に漏洩させていない。アダプター内部の複雑性は VYaml の実装制約から来ており、リファクタの優先度は低い。テストカバレッジを維持していれば現状のままで良い。

---

## 3. 改善優先順位

| 順位 | 項目 | 期待効果 |
|---|---|---|
| 1 | §2.2 ポリシーロジック一元化 | ルール正規化の一貫性保証、変更コスト削減 |
| 2 | §2.3 Diagnostic.Metadata 導入 | Fix 生成の堅牢性向上、メッセージ変更耐性 |
| 3 | §2.6 ActionRef パース一元化 | ルール間の参照解析一貫性、重複排除 |
| 4 | §2.4 IPass ActionMetadata フック追加 | action-metadata ルール拡充の基盤整備 |
| 5 | §2.1 WorkflowParser パターン共通化 | パーサーの保守性向上 |
| 6 | §2.7 Online ルール契約の明文化 | 設計意図の明確化 |
| 7 | §2.5 LintConfigLineParser 検討 | 中期の保守性向上 |
| 8 | §2.8 ユーティリティ整理 | 凝集度の微改善 |

---

## 4. 定量サマリ

```
Seiton.Core 合計: ~133 files, ~26,000 lines（§1 表記の概算）
  Parsing: 45 files, ~10,500+ lines（`Utf8MappingDispatch`、`Primitives` 分割、`On` を 7 partial に分割）
  Linting: 82 files, ~14,700 lines
  Generated: 6 files, ~725 lines (自動生成、レビュー対象外)

巨大ファイル (>800 lines):
  WorkflowParser.On.Webhook.cs   ~445 lines（旧 On.cs から webhook 汎用部）
  LintConfigLineParser.cs      1,289 lines
  ExpressionSemanticAnalyzer.cs 1,171 lines
  WorkflowParser.Jobs.cs       ~966 lines（ParseJobNode のキー共通化後）
  LintEngine.cs                  912 lines
  WorkflowParser.cs              902 lines
```

---

## 5. 検証計画

各 Phase 完了時に以下を測定:

1. **BenchmarkDotNet LintBenchmark** — Allocated (Small/Medium/Large, FixEnabled=false/true)
2. **BenchmarkDotNet ParsingBenchmark** — 回帰なしを確認
3. **LintPerRuleAlloc.cs** — run-context ルール個別計測
4. **全テストパス** — `dotnet test` Green 確認
5. 本 plan の該当箇所を更新して、実装内容と乖離がないかレビュー、実装内容とベンチマーク結果の記載

### 5.1 §2.1 提案 1（ParseJobNode キー共通化）の検証記録

**単体テスト**

- コマンド: `dotnet test --project tests/Seiton.Core.Tests/Seiton.Core.Tests.csproj`
- 結果: **474 件成功**（2026-04-22 実行）

**ParsingBenchmark（WorkflowParser 全体パス; §2.1 変更の直接計測ではないが回帰の目安）**

- コマンド: `dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj -- -f *ParseWorkflowFull* -j short -m`
- 環境（ログより）: Windows 11, BenchmarkDotNet 0.15.6, .NET 10.0.6, Ryzen 9 7950X3D, `Job=ShortRun`（IterationCount=3, WarmupCount=3）
- メソッド: `ParsingBenchmark.ParseWorkflowFull`（説明: `WorkflowParser.Parse (AST + rules)`）

| Size | Mean | Allocated (managed) |
|---|---:|---:|
| Small | 28.79 μs | 4.87 KB |
| Medium | 489.36 μs | 26.57 KB |
| Large | 8.10 ms | 110.8 KB |

**解釈**

- ShortRun は反復回数が少なく誤差が大きいため、厳密な回帰比較には `medium` / `long` job や変更前コミットとの差分が望ましい。
- 上記は「キー共通化後のスナップショット」として記録したものであり、**変更前ベースラインとの Ratio は未取得**。
