# Parser Implementation Plan

> `Seiton_Parser_spec.md`（パーサー仕様）と `Seiton_Parser_csharp_spec.md`（C# 実装仕様）に基づき、パーサーを段階的に完成させるための実装計画。各ステップを独立してテスト可能な単位で区切って記述する。

## 現状サマリー

| 領域 | 現行状況 |
|---|---|
| YAML 読み取り | `VYamlStreamReader` は `ref struct` で VYaml を直接ラップ。interface 抽象なし |
| パーサー本体 | `WorkflowParser` は shape 検証 + diagnostics。parse 関数は `void`、AST を返さない |
| 出力モデル | `ParseResult.Workflow` は `Workflow?` を返却。`WorkflowDocument` は削除済み |
| `on:` パース | イベント名検証 + options/types 検証済み。typed event node なし |
| Job/Step | キー検証・排他制約・reusable workflow 関連制約・`secrets: inherit` 形状検証あり。typed node なし |
| permissions/defaults/concurrency | `SkipCurrentNode()` のみ |
| 式パーサー | 再帰下降 + arena-style flat array で完成。算術演算は GHA 仕様外の独自拡張あり |
| 式セマンティクス | context availability + function arity + 一部リテラル型チェック |
| 式抽出 | `${{ }}` 抽出 → parse → validate パイプライン完成 |
| イベントスペック | `OnEventSpecs` で 33 イベント + activity types + options を UTF-8 span で管理 |
| テスト基盤 | `ParserTests` / `ExpressionTests` に加えて corpus smoke（actionlint/ghalint/zizmor と actionlint testdata）を実装済み |
| Visitor / Pass | 未実装 |
| Rule Engine | 未実装 |
| Generated Data | `OnEventSpecs` のみ手実装。availability / popular actions 未実装 |

---

## Phase 1: YAML アダプター層

**目標**: パーサー本体から VYaml 依存を排除し、差し替え可能なアダプター層を構築する

### Step 1.1: 自前型の定義

**ファイル**: `src/Seiton.Core/Parsing/YamlEventKind.cs`, `ScalarTag.cs`, `TextPosition.cs`

- `YamlEventKind` enum を定義
- `ScalarTag` enum を定義
- `TextPosition`（Offset/Line/Column）を実装し、adapter 境界で位置情報を正規化

**完了条件**: enum が定義され、ビルドが通る

### Step 1.2: IYamlStreamReader インターフェースの定義

**ファイル**: `src/Seiton.Core/Parsing/IYamlStreamReader.cs`

- C# 実装仕様 §0.3.2 の `IYamlStreamReader` を定義
- 必須メンバー（`GetScalarUtf8`, `GetScalarSlice`, `GetScalarString`, `GetScalarTag`, `IsScalarQuoted`, `SkipAfter`, `CurrentStart`, `CurrentEnd`）を網羅
- ただし `ref struct` は interface を実装できないため、設計判断が必要
  - **案A**: `interface IYamlStreamReader` + `class VYamlStreamAdapter` → 仮想呼び出しコスト発生
  - **案B**: `WorkflowParser<TReader> where TReader : IYamlStreamReader` で generic 化 → devirtualization 可能
  - **案C**: interface なしで `VYamlStreamReader` の API 署名だけ統一 → 差し替え時に手動修正
  - 初期は **案A** で実装、ベンチマークで問題があれば案B に移行

**完了条件**: interface が定義され、ビルドが通る

### Step 1.3: VYamlStreamAdapter の実装

**ファイル**: `src/Seiton.Core/Parsing/VYamlStreamAdapter.cs`

- 既存 `VYamlStreamReader` を `VYamlStreamAdapter : IYamlStreamReader` にリネーム・変換
- VYaml の `ParseEventType` → `YamlEventKind` 変換を実装
- VYaml の `Marker` → `TextPosition` / `TextRange` 変換を実装
- `GetScalarTag()` を実装、VYaml のタグ情報が不足する場合は値パターンで推定
- `IsScalarQuoted()` を実装

**完了条件**: 既存テストが `VYamlStreamAdapter` 経由で全パスする

### Step 1.4: WorkflowParser の VYaml 直接参照を排除

**ファイル**: `src/Seiton.Core/Parsing/WorkflowParser.cs`

- `ref VYamlStreamReader` → `IYamlStreamReader` に全 parse 関数の引数を変更
- `ParseEventType` → `YamlEventKind` に置換
- `Marker` → `TextPosition` / adapter 経由に置換
- `VYamlStreamReader.cs` を削除（`VYamlStreamAdapter.cs` に統合済み）

**完了条件**: WorkflowParser.cs 内に VYaml 名前空間の using が残らない。既存テストがパス

### Step 1.5: テスト用 FakeYamlStreamReader

**ファイル**: `tests/Seiton.Core.Tests/FakeYamlStreamReader.cs`

- `IYamlStreamReader` を実装するテスト用 fake
- イベント列を配列で受け取り、順次返す
- YAML ファイルなしでパーサー単体テストが可能になる

**完了条件**: fake reader でパーサーの最小テストが書ける

### Step 1.6: UTF-8 型語彙の基盤実装（`Utf8String` 追加）

**ファイル**: `src/Seiton.Core/Parsing/Utf8String.cs`（新規）, `Utf8Slice.cs`

- C# 実装仕様 §0.2.4 に従い `Utf8String` を実装
  - `ReadOnlySpan<byte>` からのコピーコンストラクタ
  - `IEquatable<Utf8String>` 実装
  - 生バイトベースの `GetHashCode()`
  - `FromLowerAscii(ReadOnlySpan<byte>)` を追加（キー正規化用）
- `Utf8Slice` → `Utf8String` 変換ヘルパー（`ToUtf8String`）を用意
- パーサー success path で `System.String` を使わないガード方針を明文化

**完了条件**: `Utf8String` の単体テスト（等値性/ハッシュ/lower-case 正規化）が通る

---

## Phase 2: AST 型定義

**目標**: 仕様 §2 の全 AST ノードを定義する。パーサーはまだ変更しない

### Step 2.1: 共通ノード型

**ファイル**: `src/Seiton.Core/Parsing/Ast/CommonNodes.cs`

- `StringNode`, `BoolNode`, `IntNode`, `FloatNode`（仕様 §2.6）
- 既存 `Utf8Slice`, `TextRange` を活用
- `StringNode.Value` は `Utf8Slice` を維持し、`System.String` を持たせない

**完了条件**: 型が定義され、ビルドが通る

### Step 2.2: Workflow ルートノード

**ファイル**: `src/Seiton.Core/Parsing/Ast/Workflow.cs`

- `Workflow` class（仕様 §2.2）
- 一旦フィールドをすべて nullable で定義

**完了条件**: 型が定義され、ビルドが通る

### Step 2.3: Event ノード群

**ファイル**: `src/Seiton.Core/Parsing/Ast/Events.cs`

- `Event`（abstract base）
- `WebhookEvent`, `ScheduledEvent`, `WorkflowDispatchEvent`, `WorkflowCallEvent`, `RepositoryDispatchEvent`（仕様 §2.3）
- `WebhookEventFilter`, `ScheduleEntry`, `DispatchInput`, `DispatchInputType`
- `WorkflowCallEventInput`, `WorkflowCallInputType`, `WorkflowCallEventSecret`, `WorkflowCallEventOutput`

**完了条件**: 型が定義され、ビルドが通る

### Step 2.4: Job ノード

**ファイル**: `src/Seiton.Core/Parsing/Ast/Job.cs`

- `Job` class（仕様 §2.4）

**完了条件**: 型が定義され、ビルドが通る

### Step 2.5: Step ノード群

**ファイル**: `src/Seiton.Core/Parsing/Ast/Step.cs`

- `Step`, `StepExec`, `StepExecKind`, `ExecRun`, `ExecAction`（仕様 §2.5）

**完了条件**: 型が定義され、ビルドが通る

### Step 2.6: 構造ノード群

**ファイル**: `src/Seiton.Core/Parsing/Ast/StructuralNodes.cs`

- `Permissions`, `PermissionScope`（仕様 §2.7）
- `Env`, `EnvVar`（仕様 §2.8）
- `Defaults`, `DefaultsRun`（仕様 §2.9）
- `Concurrency`（仕様 §2.10）
- `Environment`（仕様 §2.11）
- `Runner`（仕様 §2.12）
- `Strategy`, `Matrix`, `MatrixRow`, `MatrixCombinations`（仕様 §2.13）
- `RawYamlValue`, `RawYamlString`, `RawYamlArray`, `RawYamlObject`（仕様 §2.13）
- `Container`, `Services`, `Service`, `Credentials`（仕様 §2.14）
- `WorkflowCall`, `WorkflowCallInput`, `WorkflowCallSecret`（仕様 §2.15）
- Dictionary キー型は `string` ではなく `Utf8String` に統一

**完了条件**: 型が定義され、ビルドが通る

### Step 2.7: AST の UTF-8 型ポリシー確認

**対象**: `src/Seiton.Core/Parsing/Ast/**/*.cs`

- AST ノードの public フィールド/プロパティに `System.String` を導入しない
- ID/名前系の辞書キーは `Utf8String`、スカラー値は `Utf8Slice` を使用
- 例外: diagnostics/rule metadata に限り `System.String` を許可

**完了条件**: AST 定義で許可外の `System.String` 利用がない

### Step 2.8: ParseResult の更新

**ファイル**: `src/Seiton.Core/Parsing/Diagnostics.cs`

- `ParseResult.Workflow` の型を `WorkflowDocument` → `Workflow?` に変更
- `WorkflowDocument` は非推奨化（互換性のため一時的に残す）

**完了条件**: ビルドが通る。ParseResult を使う箇所は一時的に null を返す

---

## Phase 3: パーサー書き換え（AST 構築）

**目標**: 既存の shape 検証ロジックを維持しつつ、typed AST を構築するようにパーサーを書き換える

### Step 3.1: Scalar ヘルパー実装

**ファイル**: `src/Seiton.Core/Parsing/WorkflowParser.cs`（先に着手）

- `parseString()` → `StringNode`（仕様 §4.1）
- `parseBool()` → `BoolNode`（仕様 §4.2）
- `parseInt()` → `IntNode`（仕様 §4.3）
- `parseFloat()` → `FloatNode`（仕様 §4.4）
- `parseExpression()` → `StringNode`（仕様 §4.5）
- `mayParseExpression()` → `StringNode?`（仕様 §4.6）
- `parseStringOrStringSequence()` → `StringNode[]`（仕様 §4.7）
- tag 判定には `IYamlStreamReader.GetScalarTag()` → `ScalarTag` を使用
- success path で `GetScalarString()` を使わない（診断/フォールバック専用）

**テスト**: 各ヘルパーの単体テスト（FakeYamlStreamReader 使用）

**完了条件**: ヘルパーが値を返し、テストがパスする

### Step 3.2: Workflow トップレベルパースの AST 化

**状態**: 完了（`ParseResult.Workflow` は `Workflow` を返し、`name` / `run-name` を `StringNode` として保持。`on` / `jobs` はスタブとして空コレクションで初期化）

**目標**: `Parse()` が `Workflow` AST を返すようにする

- まず `name`, `run-name` を `StringNode` で返す
- `on`, `jobs` はまだスタブ（空配列）
- `permissions`, `env`, `defaults`, `concurrency` もスタブ
- 必須キー検証（`on`, `jobs`）はそのまま維持

**テスト**: `Parse(minimalWorkflow)` が `Workflow { Name = "test", Jobs = {} }` を返すことを検証

**完了条件**: `ParseResult.Workflow` が非 null の `Workflow` を返す。既存テストがパス

### Step 3.3: Permissions / Env / Defaults / Concurrency パース

**状態**: 完了（`workflow.Permissions` / `workflow.Env` / `workflow.Defaults` / `workflow.Concurrency` の AST 構築を実装。既存 shape/diagnostics を維持しつつ top-level 構造ノードを populate）

- `ParsePermissions()` → `Permissions` node（パーサー仕様 §3.5）
- `ParseEnv()` → `Env` node（パーサー仕様 §3.6）
- `ParseDefaults()` → `Defaults` node（パーサー仕様 §3.7）
- `ParseConcurrency()` → `Concurrency` node（パーサー仕様 §3.8）

**テスト**: 各 parse 関数の yaml → AST node 変換テスト

**完了条件**: workflow.Permissions / Env / Defaults / Concurrency が populated。テストパス

### Step 3.4: Events パースの AST 化

**状態**: 完了（`on` の scalar / sequence / mapping 3形態を `Event[]` として AST 化し、WebhookEvent フィルタと既存 diagnostics を維持）

- `ParseOn()` → `Event[]` を返す
- scalar / sequence / mapping の 3 形態対応（仕様 §3.4）
- `ParseWebhookEvent()` → `WebhookEvent`（仕様 §3.4.2）
- 排他フィルタ検証はそのまま維持（仕様 §3.4.3）

**テスト**: `on: push`, `on: [push, pull_request]`, `on: { push: { branches: [main] } }` のテスト

**完了条件**: `workflow.On` が typed `Event[]` を持つ。既存 on テストがパス

### Step 3.5: ScheduledEvent / WorkflowDispatchEvent / WorkflowCallEvent パース

**状態**: 完了（`schedule` / `workflow_dispatch` / `workflow_call` / `repository_dispatch` の専用パーサーを実装し、typed event AST を生成）

- `ParseScheduleEvent()` → `ScheduledEvent`（仕様 §2.3.2）
  - `cron`, `timezone` キーをパース
- `ParseWorkflowDispatchEvent()` → `WorkflowDispatchEvent`（仕様 §2.3.3）
  - `inputs` mapping → `DispatchInput[]`
  - input の `type`, `options`, `required`, `default`, `description` をパース
- `ParseWorkflowCallEvent()` → `WorkflowCallEvent`（仕様 §2.3.4）
  - `inputs` mapping → `WorkflowCallEventInput[]`（`type` 必須検証）
  - `secrets` mapping → `WorkflowCallEventSecret[]`
  - `outputs` mapping → `WorkflowCallEventOutput[]`（`value` 必須検証）
- `ParseRepositoryDispatchEvent()` → `RepositoryDispatchEvent`（仕様 §2.3.5）

**テスト**: 各イベント型の yaml → AST 変換テスト

**完了条件**: 全イベント型が構造化 AST を返す

### Step 3.6: Job パースの AST 化

**状態**: 完了（`workflow.Jobs` を `Dictionary<Utf8String, Job>` として構築し、`Id` / `name` / `needs` / `runs-on` / `permissions` / `environment` / `concurrency` / `outputs` / `env` / `defaults` / `if` / `timeout-minutes` / `continue-on-error` / `strategy` / `container` / `services` / `workflow_call` を AST に populate。既存 reusable workflow 制約 diagnostics を維持）

- `ParseJob()` → `Job` node（仕様 §3.10）
- `ParseRunsOn()` → `Runner` node（仕様 §3.13）
- `ParseEnvironment()` → `Environment` node（仕様 §3.14）
- `ParseStrategy()` → `Strategy` + `Matrix` node（仕様 §3.15）
- `ParseContainer()` → `Container` node（仕様 §3.16）
- `ParseServices()` → `Services` node（仕様 §3.17）
- `ParseCredentials()` → `Credentials` node（仕様 §3.18）
- reusable workflow 制約はそのまま維持（仕様 §3.10.1）
- Job/outputs/env/with/secrets などの map キーを `Utf8String` で保持

**テスト**: 各 Job 下位構造の yaml → AST 変換テスト

**完了条件**: `workflow.Jobs["job-id"]` が typed `Job` を返す。既存 job テストがパス

### Step 3.7: Step パースの AST 化

**状態**: 完了（`ParseStep` が `Step` ノードを構築し、`ExecRun` / `ExecAction` を判別して `job.Steps` へ格納。`run` vs `uses` 排他 diagnostics を維持）

- `ParseStep()` → `Step` node（仕様 §3.12）
- `parseStepExecRun()` → `ExecRun` node（仕様 §3.12.2）
- `parseStepExecAction()` → `ExecAction` node（仕様 §3.12.1）
- `run` vs `uses` 排他チェックはそのまま維持

**テスト**: run step, uses step, docker step の yaml → AST 変換テスト

**完了条件**: `job.Steps[i].Exec` が `ExecRun` or `ExecAction` を返す。既存 step テストがパス

### Step 3.8: WorkflowDocument の廃止

**状態**: 完了（`WorkflowDocument` は削除済みで、`ParseResult.Workflow` は `Workflow?` を返却。現行コードに `WorkflowDocument` 参照なし）

- `WorkflowDocument` を削除
- `ParseResult.Workflow` が `Workflow?` を返すことをテストで確認
- ベンチマークの更新

**完了条件**: `WorkflowDocument` への参照がコード上にない。テスト・ベンチマークパス

---

## Phase 4: 汎用 Mapping パーサーと重複キー検出

**注記**: C# 実装仕様 §3.2 に合わせ、`ReadOnlySpan<byte>` キー比較は delegate callback ではなく各 parse 関数内の inline 走査を基本とする。

### Step 4.1: ParseMapping ヘルパー

**状態**: 完了（`TryRegisterMappingKey` を導入し、case-sensitive / ASCII case-insensitive 切替、duplicate key 検出、`<<` merge key エラーを実装。`workflow` / `jobs` / 汎用 string mapping 走査に適用）

- パーサー仕様 §3.3 の mapping 走査「共通パターン」を整備
- case-insensitive / case-sensitive の切り替え可能
- 重複キー検出（duplicate key → error、先勝ち）
- `<<` merge key → error
- 実装形態は delegate ではなく、inline 走査で再利用できる軽量ユーティリティ（重複キー管理・正規化）を提供

**テスト**: 重複キー、merge key の Error が diagnostics に出ること

**完了条件**: mapping 走査の共通ルーチンが動作し、テストパス

### Step 4.2: 既存パース関数の ParseMapping に移行

**状態**: 完了（`TryRegisterMappingKey` を `on` 系 mapping、job/strategy/container/services など主要 mapping 走査へ適用。duplicate key / `<<` merge key 診断を広範囲で有効化）

- `ParseJobsMapping`, `ParseOnMapping`, 各 parse 関数を inline mapping 走査パターンへ統一
- 重複キー検出が全 mapping で有効になる

**完了条件**: 既存テストがパス。新たに duplicate key テスト追加

---

## Phase 5: Visitor / Pass パターン

**目標**: AST 巡回の基盤を構築し、ルールエンジンの土台を作る

### Step 5.1: IPass インターフェースと WorkflowVisitor

**状態**: 完了（`IPass` と `WorkflowVisitor` を実装し、`WorkflowPre → JobPre → Step → JobPost → WorkflowPost` の巡回順を unit test で検証）

**ファイル**: `src/Seiton.Core/Linting/IPass.cs`, `WorkflowVisitor.cs`

- `IPass` interface（パーサー仕様 §8.1, C# 実装仕様 §5.1）
- `WorkflowVisitor`（パーサー仕様 §8.2, C# 実装仕様 §5.2）
- 巡回順: WorkflowPre → JobPre → Step → JobPost → WorkflowPost

**テスト**: dummy pass で巡回順序を検証

**完了条件**: Visitor が全ノードを正しい順序で訪問する

### Step 5.2: IRule インターフェース

**状態**: 完了（`IRule : IPass` を追加し、`Id` / `Name` / `GetDiagnostics()` / `SetConfig(LintConfig)` を定義。`WorkflowVisitor` と組み合わせた rule 実装テストを追加）

**ファイル**: `src/Seiton.Core/Linting/IRule.cs`

- `IRule : IPass`（パーサー仕様 §8.3, C# 実装仕様 §5.3）
- `Id`, `Name`, `GetDiagnostics()`, `SetConfig()`

**完了条件**: interface が定義され、ビルドが通る

### Step 5.3: 既存 syntax diagnostics の SyntaxRule に移行

**状態**: 完了（`SyntaxRule` を実装して `WorkflowVisitor` から実行し、job 制約診断の一部を parser 直書きから rule 側へ移行。`uses` と `steps` / `runs-on` の排他、および `runs-on`・`steps` 必須条件の検証を `VisitJobPre` で実施）

- パーサー内の未知キー検出・排他検証等は引き続きパーサーで行う
- Visitor 側に移行する候補: permissions の値検証、reusable workflow 制約など、よりセマンティックなもの
- 最初の 1-2 個のルールを試作して Visitor パイプラインの動作を確認

**完了条件**: 少なくとも 1 つの Rule が Visitor 経由で diagnostics を返す

---

## Phase 6: Generated Data

### Step 6.1: Context Availability テーブル

**状態**: 完了（`src/Seiton.Core/Generated/Availability.g.cs` を追加し、`ExpressionSemanticAnalyzer` の context availability 判定を generated テーブル参照へ置換）

**ファイル**: `src/Seiton.Core/Generated/Availability.g.cs`

- パーサー仕様 §7.2 の完全な availability table を生成
- 現行 `ExpressionSemanticAnalyzer` の手実装を置換
- キー位置（`if:` / `env:` / `with:` 等）ごとの粒度で管理

**完了条件**: semantic analyzer が generated table を参照。テストパス

### Step 6.2: Webhook Types テーブル

**状態**: 完了（`src/Seiton.Core/Generated/WebhookTypes.g.cs` を追加し、`WorkflowParser` のイベント仕様参照を hand-written `OnEventSpecs` から generated テーブルへ移行）

**ファイル**: `src/Seiton.Core/Generated/WebhookTypes.g.cs`

- 現行 `OnEventSpecs` の手実装を generated data で置換可能にする
- 初期は手実装で十分。生成スクリプトは後回しで可

**完了条件**: スクリプト or 手動で最新データが反映される仕組みの設計

### Step 6.3: Popular Actions Metadata

**ファイル**: `src/Seiton.Core/Generated/PopularActions.g.cs`

- actionlint の `popular_actions.go` に相当
- action.yml から input 名・型を取得し、static table 化
- ルールエンジンから参照（パーサーは直接使わない）

**完了条件**: 設計と初期データの投入

---

## Phase 7: 式パーサー改善

### Step 7.1: 算術演算子の除去検討

- 現行 `ParseAdditive` / `ParseMultiplicative` は GitHub Actions 仕様外
- パーサー仕様 §6.2 に従い、使用中のテストを確認して除去 or 非推奨化

**完了条件**: 判断を記録。除去する場合はテスト更新

### Step 7.2: ExprType 型階層の導入

- `AnyType`, `NullType`, `BoolType`, `NumberType`, `StringType`, `ObjectType`, `ArrayType`（パーサー仕様 §7.3）
- `ExprSemanticsChecker` に bottom-up 型推論を追加

**完了条件**: リテラル・関数戻り値・context プロパティの型推論が動作する

### Step 7.3: 式 Visitor

- `VisitExprNode()` パターンの実装（パーサー仕様 §6.5, C# 実装仕様 §6.1）
- semantic checker をこのパターンで書き直す

**完了条件**: 式 AST の巡回が Visitor パターンで動作する

---

## Phase 8: テスト強化・ベンチマーク

### Step 8.1: actionlint testdata ベースの統合テスト

- 現状: `.references/actionlint-main/testdata/` を含む corpus smoke（例外なく parse できること、broken fixture で失敗が出ること）は実装済み
- 次ステップ: fixture ごとの期待 diagnostics（サブセット）照合に拡張する
- 既存の単体テスト群（unknown key、on オプション排他、式関数/arity/type など）を diagnostics 照合方式に段階的に統一する

**完了条件**: 主要テストケースで期待 diagnostics サブセットが一致

### Step 8.2: AST 構造テスト

- 各 AST ノードが yaml から正しく構築されることの property-based テスト
- `FakeYamlStreamReader` を使ったパーサー単体テスト

**完了条件**: 全 AST ノード型に最低 1 つの構築テスト

### Step 8.3: ベンチマーク更新

- `ParsingBenchmark` を AST 構築込みのベンチマークに更新
- adapter 層のオーバーヘッド測定
- allocation 回帰テスト（`[MemoryDiagnoser]` で tracking）

**完了条件**: ベンチマーク結果のベースライン記録

---

## 依存関係グラフ

```
Phase 1 (adapter)
  └─> Phase 2 (AST types)
       └─> Phase 3 (parser rewrite)
            ├─> Phase 4 (mapping helpers)
            └─> Phase 5 (Visitor/Pass)
                 └─> Phase 6 (generated data)
Phase 7 (expression improvements)  ← Phase 3 以降いつでも着手可
Phase 8 (testing/benchmarks)       ← 各 Phase 完了時に随時実施
```

---

## チェックリスト（全 Phase 共通）

各 Step 完了時に以下を確認する:

- [ ] `dotnet build` が通る
- [ ] `dotnet test` が全パスする
- [ ] Parsing フォルダ内に新規 `GetScalarString()` が success path に追加されていない（allocation guardrails）
- [ ] 新規 key 判定が UTF-8 span ベースである
- [ ] AST / parser の success path に `System.String` を導入していない（診断系を除く）
- [ ] dictionary キーは `Utf8String`、スカラー値は `Utf8Slice` の方針を満たす
- [ ] diagnostics が有用なメッセージと正確な位置を持つ
