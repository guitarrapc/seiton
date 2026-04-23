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
| ポリシーロジックの重複 | High | LintEngine ↔ LintConfigLibrary（§2.2: `RuleNormalizer` / `ExclusionNormalizer` で共通化済） |
| 診断メッセージへの構造的依存 | High | PinRemediation（§2.3: `Diagnostic.Metadata` + `PinDiagnosticMetadata` で解消済） |
| IPass の ActionMetadata 非対称性 | Medium | WorkflowVisitor, IPass（§2.4: 専用フック + 仕様同期済） |
| 設定パーサーの維持コスト | Medium | `LintConfigVYamlParser` + DOM 変換（§2.5: 行パーサー廃止済） |
| アクション参照パース処理の分散 | Medium | `ActionRefHelpers` + `ParsedActionRef` に集約済（§2.6） |
| Online ルールとローカルルールの契約差異 | Medium | OnlineAuditEngine vs IRule |
| ユーティリティの責務混在 | Low | WorkflowParser.ScalarParsing / ExpressionIntegration, SpanHelpers |

---

## 2. 問題点と改善提案

### 2.1 [High] WorkflowParser partial の肥大化と繰り返しパターン

**問題**

`WorkflowParser` は複数の partial に分割されているが、合計 ~6,000 行の static partial class であり、以下の問題があった。

- ~~`WorkflowParser.On.cs` (1,692 行)~~ → **責務別に 7 partial**（`On.Core` / `On.Schedule` / `On.WorkflowDispatch` / `On.WorkflowCall` / `On.RepositoryDispatch` / `On.ImageVersion` / `On.Webhook`）へ分割済み。
- `WorkflowParser.Jobs.cs` の `ParseJobNode` はキー共通化で整理済み（以下 実装状況）。
- ~~各 partial で同じ「キーチェック → パース → エラー → スキップ」パターンが手書きで反復されている。~~ → **ジョブ／runs-on／ステップに加え**、ルート `WorkflowParser`（`TryReadRootStructuralHints` / `ParseCore` / `defaults` / `concurrency`）、`Strategy`（`matrix` トップレベル。`include`/`exclude` は動的キー登録後の判定）、`Containers`（本体・`credentials`）、`ActionMetadata`（input / output / branding / runs）、`On.*` の小さなマッピング（`schedule`、`workflow_dispatch`、`workflow_call`、`repository_dispatch`、`image_version`、webhook オプション）でも `Utf8MappingDispatch` + `IUtf8OrderedKeyTable` + `switch` に寄せ済み（下表）。
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
| 追加ファイル | `Utf8MappingDispatch.cs`（共通）。キーテーブル用 partial: **`WorkflowParser.MappingKeys.WorkflowRoot.cs`**（ルート workflow / action メタ、`defaults` 外側・`run` 内側、`concurrency`、構造ヒント `jobs`/`runs`）、**`WorkflowParser.MappingKeys.Extended.cs`**（strategy、container、credentials、action metadata 各マッピング、on.schedule / workflow_dispatch / workflow_call / repository_dispatch / image_version、webhook オプション拡張テーブル、dispatch・workflow_call 入力の型スカラー用テーブル等）。 |
| 適用箇所 | **`WorkflowParser.Jobs.cs`**: `ParseJobNode`、`ParseRunsOnNode` mapping 形。**`WorkflowParser.Steps.cs`**: `ParseStep`。**`WorkflowParser.cs`**: ルート `ParseCore`、`TryReadRootStructuralHints`、`ParseDefaultsNode`、`ParseConcurrencyNode`。**`WorkflowParser.Strategy.cs`**: `ParseStrategy`、行列の `include`/`exclude` 判定。**`WorkflowParser.Containers.cs`**: `ParseContainerLike`、`ParseCredentials`。**`WorkflowParser.ActionMetadata.cs`**: input / output / branding / runs。**`WorkflowParser.On.*.cs`**: schedule エントリ、workflow_dispatch（トップ・入力フィールド・型スカラー）、workflow_call（イベント・入力・秘密・出力・型スカラー）、repository_dispatch、image_version、webhook（`ParseWebhookEventWithOptions` / `ParseOnEventOptions`。後者は `types` → `IsOptionAllowed` → 拡張テーブルの順序を維持）。 |
| 当初案（delegate）との差分 | `ReadOnlySpan<byte>` をキャプチャする delegate は使えないうえ、ホットパスでは `Invoke` の間接呼び出しが不利。.NET 10 では `ReadOnlySpan<ReadOnlySpan<byte>>` のような ref struct の入れ子も不可（CS9244）のため、**静的抽象インターフェイス + `Utf8Key(ordinal)` の switch** でテーブルを表現した。 |
| 残り（任意） | 動的キー主体のマッピング（`permissions`、`env`、行列の可変軸、`ParseRawYamlObject` 等）や null リテラル分岐は従来どおり。必要なら個別にテーブル化を検討。 |

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

**実装状況（§2.2、2026-04-23 時点）**

| 項目 | 内容 |
|---|---|
| 追加 | `src/Seiton.Core/Linting/RuleNormalizer.cs` — `BuildUnknownRuleIdMessage`（LintEngine と同一文言: `Did you mean`）、`NormalizeRuleEntries`（`RuleSpecificConfigNormalizer` まで含む）。`src/Seiton.Core/Linting/ExclusionNormalizer.cs` — `CollectResolvedExclusionRules`。 |
| 呼び出し | `LintEngine.NormalizeRules` / `LintConfigLibrary.NormalizeRules` は `RuleNormalizer.NormalizeRuleEntries` に委譲。`NormalizeExclusions` のルール ID ループは `ExclusionNormalizer.CollectResolvedExclusionRules` に委譲。インライン抑制の `AddRuleIds` は `RuleNormalizer.BuildUnknownRuleIdMessage` を参照。 |
| 意図的な差分の維持 | 空の `files` パターン、ジョブ ID 検証（LintEngine のみ `utf8Yaml` + `AstArena`）、`LintExclusion` の trim / jobs 正規化（LintConfigLibrary のみ）は各呼び出し側のまま。 |
| 付随変更 | 設定バリデーション経由の unknown rule メッセージが LintEngine 側と同じ大文字 `Did you mean` に揃う（従来は小文字 `did you mean`）。 |

**ベンチマーク・計測（同一マシン、BenchmarkDotNet `ShortRun`、Release、実装後）**

- **LintBenchmark**（`LintEngine.Check`、Allocated / op）: Small `FixEnabled=false` **14.41 KB**、Small `true` **14.83 KB**、Medium `false` **92.88 KB**、Medium `true` **99.3 KB**、Large `false` **435.71 KB**、Large `true` **465.79 KB**。本変更は設定正規化の重複排除のみで、上記は「回帰監視用の採取値」（baseline 比は別途 CI/ローカルで比較）。
- **ParsingBenchmark**（`WorkflowParser.Parse (AST + rules)` のみ、Allocated / op）: Small **4984 B**、Medium **27 220 B**、Large **113 464 B**。パーサー非変更のため理論上は差なし；採取値で回帰なしを確認。
- **LintPerRuleAlloc.cs**（Large 20×12、`GC.GetTotalAllocatedBytes`）: baseline（全ルール無効）**230 520 B**、ALL RULES **450 792 B**、最大単体は `expr-undefined-var` **320 680 B**（実行ログに準拠）。

**テスト:** `dotnet test -c Release` — **553 件 Green**（実装直後）。

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

**実装状況（§2.3、2026-04-23 時点）**

| 項目 | 内容 |
|---|---|
| `Diagnostic` | `Metadata`（`IReadOnlyDictionary<string, string>?`、末尾オプション）を追加。既存の `new Diagnostic(...)` は既定で `null`。 |
| `PinDiagnosticMetadata` | `src/Seiton.Core/Linting/PinRemediation/PinDiagnosticMetadata.cs` — キー `uses-ref` / `image-ref`、`ForUsesRef` / `ForImageRef`、`TryGetUsesRef` / `TryGetImageRef`（公開 API、テスト・CLI から利用可）。 |
| ルール | `UnpinnedUsesRule`（再利用 workflow の `uses`、リモート `uses` の未ピン）、`UnpinnedImageRule`（`docker://` ステップ、`job.container` / services イメージ）が該当診断にメタデータを付与。`RuleBase` にメタデータ付き `AddStepWarning` / `AddJobWarning` オーバーロード。 |
| Pin 修復 | `PinFixFormatter` / `PinRemediationEngine` はメタデータのみ参照；`TryExtractQuotedValue` 削除。 |
| 割り当て | 初版は診断ごとに `Dictionary` を new しており、同一 `uses` が多いワークフローで `LintBenchmark` の Allocated が悪化。**`PinSingleEntryReadOnlyDictionary`**（1 エントリ専用）＋**同一参照文字列のメタデータを `ConcurrentDictionary` で共有**（`ForUsesRef` / `ForImageRef`）で抑制。`Diagnostic.Metadata` フィールド自体のコスト（`ParsingBenchmark` のわずかな増分）は別。 |
| テスト | `PinFixFormatterTests`（メタデータ欠落時は fix なし）、`PinRemediationEngineTests` 等を更新。**`dotnet test` Green**。 |

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

**実装状況（§2.4、2026-04-23 時点）**

| 項目 | 内容 |
|---|---|
| `IPass` | `VisitActionMetadataPre` / `VisitActionMetadataPost` を追加（既定実装は no-op。`RuleBase` が `VisitActionMetadataPre` で `diagnostics.Clear()`）。 |
| `WorkflowVisitor` | `VisitActionMetadata` は上記フックのみ使用。**`EmptyLintWorkflow` による `VisitWorkflowPre` / `VisitWorkflowPost` の呼び出しを廃止**。 |
| `SyntaxRule` | 子ルールへ `VisitActionMetadataPre` / `Post` を転送。 |
| `LintEngine` | action-metadata のみの入力では引き続き inline suppression / exclusion 用に **`EmptyWorkflowForSuppression`** を使用（ジョブスコープは workflow AST に依存）。visitor 側のダミー workflow は削除済み。 |
| 仕様 | `Seiton_Linter_spec.md` §4.1 / §4.2 / プロファイル注記を更新。 |
| テスト | `WorkflowVisitorTests.VisitActionMetadata_TraversesInExpectedOrder` を追加。 |

---

### 2.5 [Medium] LintConfigLineParser の維持コスト

**問題**

`LintConfigLineParser` (~1,289 行) は YAML の部分的なセマンティクスを手書きの行パーサーで再実装していた。
インデントベースのステートマシンと多数の switch 分岐で構成されており、以下の懸念がある。

- YAML のエッジケース（フロースタイル、マルチラインスカラー、コメント位置）に対応しきれない可能性。
- 設定仕様に新しいセクションやキーを追加するたびに大きな変更が必要。
- パーサー部分のテストカバレッジに依存した正確性。
- 全文 `Split('\n')` と UTF-16 行処理により、ワークフロー本体と異なり UTF-8 / VYaml 方針と割り当て特性が揃わない。

**コンセプトとの乖離**

Parser 仕様が VYaml アダプター経由で YAML 解析を行う方針と、設定ファイルだけ独自行パーサーを使う方針の二重基準になっていた。

**改善提案**

1. ~~短期: 変更なし~~
2. **VYaml `YamlSerializer.Deserialize<Dictionary<string, object?>>` でルートを読み、`LintConfigYamlDomConverter` で既存の `LintConfigParseResult`（rules / exclusions / fix / network）に変換する。** ルール本体の `RuleSpecificConfig` 組み立ては `LintConfigRuleBodyMaterializer` に抽出し、旧行パーサーと同じ検証メッセージを維持。
3. `LintConfigLibrary.Validate` は UTF-8 バイト列を一度だけ生成し、`LintConfigVYamlParser.Parse` に渡す。

**実装状況（§2.5、2026-04-23 時点）**

| 項目 | 内容 |
|---|---|
| 削除 | `LintConfigLineParser.cs` |
| 追加 | `LintConfigVYamlParser.cs`（VYaml デシリアライズ + 例外時診断）、`LintConfigYamlDomConverter.cs`（DOM→既存モデル）、`LintConfigParseResult.cs`、`LintConfigRuleBodyMaterializer.cs` |
| API | 外部公開 API は `LintConfigLibrary` のみ（従来どおり）。 |
| テスト | 既存 `LintConfigLibraryTests` および全ソリューション **`dotnet test` 555 件 Green**。 |

**ベンチマーク（`Seiton.Benchmark.LintConfigParseBenchmark`、BenchmarkDotNet 0.15.6、ShortRun、MemoryDiagnoser）**

| Size (入力) | メソッド | Mean (概算) | Allocated (概算) |
|---|---|---:|---:|
| `Template`（`GenerateTemplateYaml`） | `LintConfigVYamlParser.Parse` | ~4.9 μs | ~2.1 KB |
| `Template` | `LintConfigLibrary.Validate`（parse + normalize） | ~6.1 μs | ~10.0 KB |
| `Full`（統合テスト相当のフル YAML） | `LintConfigVYamlParser.Parse` | ~22 μs | ~26.8 KB |
| `Full` | `LintConfigLibrary.Validate` | ~25 μs | ~38.8 KB |
| `FullRepeated`（`Full` を 10 回連結） | `LintConfigVYamlParser.Parse` | ~29 μs | ~21.0 KB |
| `FullRepeated` | `LintConfigLibrary.Validate` | ~35 μs | ~67.6 KB |

- **Parse** 行は VYaml + DOM のみ（`Validate` に比べ割り当て小）。**Validate** 行は `Encoding.UTF8.GetString`（ベンチ内）・ルール／除外の正規化・`LintConfig` 構築を含む。
- 旧行パーサーは削除済みのため同一バイナリでの A/B 比較はしない。行パーサーはテキスト全面展開と行配列割り当てが支配的で、上記 **Parse** 行より重くなりやすい構造だった。

---

### 2.6 [Medium] アクション参照パース処理の分散

**問題（整理前）**

`ActionRefHelpers` に共通ユーティリティがあった一方、`ForbiddenUsesRule` / `UnpinnedUsesRule` / `RefVersionMismatchRule` が remote `uses` の検証・分割・バージョン抽出をそれぞれ重複実装していた。

**実装内容（完了）**

1. **`ParsedActionRef`**（`readonly ref struct`）— UTF-8 `uses` と最終 `@` 位置を保持し、`ActionPath` / `Ref` をスパンで公開。
2. **`TryParseRemoteUses`** — remote 形状の検証と分割を単一路に統一（`UnpinnedUsesRule` の旧 `HasRemoteActionUsesFormat` 相当を置換）。
3. **`TryGetOwnerRepoPolicyKey(actionPath, scratch, out ReadOnlySpan<byte> key)`** / **`WildcardMatchUsesPolicy(ReadOnlySpan<byte> text, ReadOnlySpan<byte> pattern)`** — `forbidden-uses` 用のキーはスクラッチへ UTF-8 書き込み（ヒープなし）、ワイルドカードは UTF-8 バイト列で比較。
4. **`TryExtractRefVersionMajor` / `TryExtractPathVersionMajor`** — `ref-version-mismatch` 用の major 抽出を `ActionRefHelpers` に移動。
5. 各ルールは上記 API のみを呼び出し、ad-hoc パースを削除。`UnpinnedUsesRule` のローカル `uses`（`./` 等）とファイルシステム解決は従来どおりルール側（本項の対象外）。

**パフォーマンス・割り当て（マイクロベンチマーク）**

同一バイナリでの「分散前／一元化後」の A/B は持たない（旧実装の重複削除が主目的）。一元化後のホットパスコストを **`ActionRefParseBenchmark`**（`src/Seiton.Benchmark/ActionRefParseBenchmark.cs`）で計測した。

- **環境**: Windows 11 (10.0.26200), BenchmarkDotNet 0.15.6, .NET 10.0.6, AMD Ryzen 9 7950X3D, `Job=ShortRun`（IterationCount=3, WarmupCount=3）
- **コマンド**: `dotnet run -c Release --project src/Seiton.Benchmark/Seiton.Benchmark.csproj -- --filter *ActionRefParseBenchmark*`

| メソッド（BDN 表示名） | Mean | Allocated (managed / op) | 備考 |
|---|---:|---:|---|
| `TryParseRemoteUses`（`actions/checkout@v4`） | ~14.5 ns | **0** | スパンのみ、ヒープ割り当てなし |
| `TryParseRemoteUses`（サブパス + `.yml`） | ~15.6 ns | **0** | 同上 |
| 解析 + `TryGetOwnerRepoPolicyKey`（`stackalloc` スクラッチ、forbidden-uses 相当） | ~31.6 ns | **0** | キーは `ReadOnlySpan<byte>`（スクラッチのスライス） |
| `TryParseActionReference(string)`（短い `uses`、stackalloc 経路） | ~54.8 ns | **~112 B** | UTF-8 化 + `owner`/`repo`/`ref` の `string` |
| 解析 + ref/path major（ref-version-mismatch 相当） | ~72.0 ns | **~24 B** | `int.TryParse` 用の一時 `string`（桁抽出） |

**解釈**

- **Parse 相当**（`TryParseRemoteUses`）はルール共通の最軽量層で、**割り当てゼロ**を維持できる。
- **forbidden-uses キー**はスクラッチ上の UTF-8 で完結し、**通常パスではキーのヒープ割り当ては不要**（極端に長い `owner/repo` のみ `ArrayPool`）。診断メッセージは警告時のみ `Encoding.UTF8.GetString`。ポリシー文字列はマッチ時に `stackalloc` へ UTF-8 化（バッファ不足時のみ `GetBytes`）。
- **`TryParseActionReference(string)`** は API 都合で **`string` 割り当て**が残る。
- **ref-version-mismatch** 経路は major 抽出の `Encoding.UTF8.GetString` が小さな割り当てを生む；旧実装も同種の抽出を含んでいたため、**意図した挙動の共有**が主成果。

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

**実装状況（§2.7、2026-04-23 時点）**

| 項目 | 内容 |
|---|---|
| `IOnlineRule` | `IRule` を拡張し、`CollectedTargets`（traversal 中に収集した `ActionAuditTarget` リスト）と `EvaluateTarget`（解決後の評価メソッド）を追加。 |
| `OnlineRuleBase` | `RuleBase` を拡張した抽象基底クラス。`VisitJobPre` で reusable workflow `uses:`、`VisitStep` で step `uses:` からリモートアクション参照を収集。ローカル (`./`) と Docker (`docker://`) 参照はスキップ。 |
| 4 ルール | `KnownVulnerableActionsRule` / `ImpostorCommitRule` / `RefConfusionRule` / `StaleActionRefsRule` は `OnlineRuleBase` を拡張。各ルールの `EvaluateTarget` が従来の `Evaluate` ロジックを継承し、`RuleBase.AddError` / `AddWarning` 経由で診断を追加。 |
| `RuleCatalog` | `AdditionalRuleMetadata` を廃止し、`OnlineRuleFactories`（ファクトリ付き）に移行。`CreateOnlineRules()` を追加。`IsOptIn()` で opt-in ルール判定（online ルールはデフォルト無効）。`AllRuleMetadata` は local + online 両方を含む。 |
| `RuleBase` | `AddError(message, location)` / `AddWarning(message, location)` を追加（AST ノードなしで診断追加可能）。 |
| `LintEngine` | `_onlineRules` / `_activeOnlineRules` フィールド追加。デフォルトコンストラクタで `RuleCatalog.CreateOnlineRules()` を生成。`Check()` で online ルールも `WorkflowVisitor` に追加し、traversal 中にターゲットを収集。`IsRuleEnabled` は `RuleCatalog.IsOptIn()` で opt-in ルールのデフォルト無効を判定。`ActiveOnlineRules` プロパティで外部公開。 |
| `OnlineAuditEngine` | `AuditAsync(LintResult, IReadOnlyList<IOnlineRule>, CancellationToken)` に変更。内部ルールインスタンスと `CollectTargets` を廃止。online ルールの `CollectedTargets` からユニークターゲットを集約し、非同期解決後に各ルールの `EvaluateTarget` を呼び出し、`GetDiagnostics()` 経由で診断を収集。 |
| テスト | `OnlineAuditEngineTests` 全テストを新 API に移行。`EnableAllOnlineRules()` ヘルパーで opt-in 有効化。opt-in 無効時のパススルーテスト、`ActiveOnlineRules` 件数テスト、ターゲット収集テストを追加。**558 件 Green**。 |

**設計ポイント**

- **Phase 分離**: (1) `LintEngine.Check()` 同期 traversal でターゲット収集（診断なし）→ (2) `OnlineAuditEngine.AuditAsync()` 非同期解決 + 評価 + 診断収集。
- **ターゲット重複排除**: 4 ルールが独立に同一ターゲットを収集するが、`OnlineAuditEngine` が `UsesText` ベースで重複排除してネットワーク解決は一度のみ。
- **Opt-in 制御**: `RuleCatalog.IsOptIn()` + `LintEngine.IsRuleEnabled()` で、config 未指定時は online ルール無効。`rules.<rule-id>.enabled: true` で有効化。
- **Suppression**: online ルール診断は現時点では `OnlineAuditEngine` 側で直接収集され、`LintEngine` の suppression パイプラインを通らない。suppression 統合は今後の課題。

---

### 2.8 [Low] ユーティリティの責務混在

**問題**

- ~~`WorkflowParser.Primitives.cs`~~ は `ScalarParsing` / `ExpressionIntegration` に分割済み（§2.1 提案 2）。
- `SpanHelpers.cs` は ASCII 処理、行/列計算、文字列正規化を 1 ファイルに含む。
- `RuleBase.cs` (250 行) には位置構築ヘルパー、デコードヘルパー、SHA チェック、診断追加ヘルパーなど多数の便利メソッドが混在している。

**改善提案**

- `RuleBase` の SHA チェック (`IsSha256DigestPinned`, `IsFullCommitSha`) は複数ルールと `ActionRefHelpers` で共通利用されるべきロジックであり、`ActionRefHelpers` に移動を検討する（既に一部は存在する）。
- それ以外は現状維持で問題ない。分割による効果がコスト（import 変更、テスト調整）を上回る段階で実施する。

**実装ステータス: 完了**

- `IsSha256DigestPinned` を `RuleBase`（`protected static`）から `ActionRefHelpers`（`internal static`）に移動。
- `IsFullCommitSha` は既に `ActionRefHelpers` にのみ定義されており移動不要だった。
- `UnpinnedImageRule` の呼び出し元 2 箇所を `ActionRefHelpers.IsSha256DigestPinned` に変更。
- `SpanHelpers.cs` は計画通り現状維持。
- 全 558 テスト通過を確認。

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
| 3 | §2.6 ActionRef パース一元化（完了） | ルール間の参照解析一貫性、重複排除 |
| 4 | §2.4 IPass ActionMetadata フック追加 | action-metadata ルール拡充の基盤整備 |
| 5 | §2.1 WorkflowParser パターン共通化 | パーサーの保守性向上 |
| 6 | §2.7 Online ルール契約の明文化 | 設計意図の明確化 |
| 7 | §2.5 Lint 設定の VYaml 化（完了） | 保守性・YAML 互換・UTF-8 方針との整合 |
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
  ExpressionSemanticAnalyzer.cs 1,171 lines
  WorkflowParser.Jobs.cs       ~966 lines（ParseJobNode のキー共通化後）
  LintEngine.cs                  912 lines
  WorkflowParser.cs              902 lines
```

---

## 5. 検証計画

各 Phase 完了時に以下を測定:

1. **BenchmarkDotNet LintBenchmark** — Allocated (Small/Medium/Large, FixEnabled=false/true)
2. **BenchmarkDotNet LintConfigParseBenchmark** — 設定 YAML の Parse / Validate（§2.5）
3. **BenchmarkDotNet ActionRefParseBenchmark** — `ActionRefHelpers` の Parse / ポリシーキー / major 抽出（§2.6）
4. **BenchmarkDotNet ParsingBenchmark** — 回帰なしを確認
5. **LintPerRuleAlloc.cs** — run-context ルール個別計測
6. **全テストパス** — `dotnet test` Green 確認
7. 本 plan の該当箇所を更新して、実装内容と乖離がないかレビュー、実装内容とベンチマーク結果の記載

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
