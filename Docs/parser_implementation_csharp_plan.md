# Parser Implementation Plan

> `Seiton_Parser_spec.md`（パーサー仕様）と `Seiton_Parser_csharp_spec.md`（C# 実装仕様）に基づき、パーサーを段階的に完成させるための実装計画。各ステップを独立してテスト可能な単位で区切って記述する。

## 現状サマリー

| 領域 | 現行状況 |
|---|---|
| YAML 読み取り | `IYamlStreamReader` + `VYamlStreamAdapter` を使用。`WorkflowParser` は generic core（`ParseCore<TReader>`）で adapter 差し替え可能 |
| パーサー本体 | `WorkflowParser` は shape 検証 + AST 構築 + diagnostics を実行。`ParseResult.Workflow` に typed AST を返却 |
| 出力モデル | `ParseResult.Workflow` は `Workflow?` を返却。`WorkflowDocument` は削除済み |
| `on:` パース | scalar/sequence/mapping の 3 形態を `Event[]` として AST 化。`schedule` / `workflow_dispatch` / `workflow_call` / `repository_dispatch` も typed node で構築 |
| Job/Step | `Job` / `Step` を typed node で構築。`uses` と `steps`/`runs-on` 排他、`with`/`secrets` 依存、必須キーを parser で診断 |
| permissions/defaults/concurrency | top-level / job-level ともに typed node 構築済み（shape 診断維持） |
| 式パーサー | 再帰下降 + arena-style flat array。GHA 仕様に合わせて算術演算子サポートを削除済み |
| 式セマンティクス | generated availability + function arity + bottom-up 型推論（`ExprType`）を実装 |
| 式抽出 | `${{ }}` 抽出 → parse → validate パイプライン完成 |
| イベントスペック | generated `WebhookTypes.g.cs` を参照して UTF-8 span ベースで検証 |
| テスト基盤 | `ParserTests` / `ExpressionTests` に加えて corpus smoke（actionlint/ghalint/zizmor と actionlint testdata）を実装済み |
| Visitor / Pass | `IPass` + `WorkflowVisitor` 実装済み |
| Rule Engine | `IRule` + `SyntaxRule` を実装済み（ルール拡充は継続課題） |
| Generated Data | `WebhookTypes.g.cs` / `Availability.g.cs` / `PopularActions.g.cs` を実装済み |

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

**状態**: 完了（`src/Seiton.Core/Generated/PopularActions.g.cs` を追加し、主要アクションの input 名テーブルを実装。`SyntaxRule` から `uses` + `with` を参照して既知アクションの未知 input を warning 診断する導線を追加）

**ファイル**: `src/Seiton.Core/Generated/PopularActions.g.cs`

- actionlint の `popular_actions.go` に相当
- action.yml から input 名・型を取得し、static table 化
- ルールエンジンから参照（パーサーは直接使わない）

**完了条件**: 設計と初期データの投入

---

## Phase 7: 式パーサー改善

### Step 7.1: 算術演算子の除去検討

**状態**: 完了（仕様 §6.2 に合わせて算術演算子サポートを削除。`ExpressionParser` から `ParseAdditive` / `ParseMultiplicative` と unary `-` を除去し、式パースは logical/comparison/`!`/postfix のみを受理するように変更。`ExpressionTests` に算術式の非受理テストを追加）

- 現行 `ParseAdditive` / `ParseMultiplicative` は GitHub Actions 仕様外
- パーサー仕様 §6.2 に従い、使用中のテストを確認して除去 or 非推奨化

**完了条件**: 判断を記録。除去する場合はテスト更新

### Step 7.2: ExprType 型階層の導入

- `AnyType`, `NullType`, `BoolType`, `NumberType`, `StringType`, `ObjectType`, `ArrayType`（パーサー仕様 §7.3）
- `ExprSemanticsChecker` に bottom-up 型推論を追加

**完了条件**: リテラル・関数戻り値・context プロパティの型推論が動作する

**実装結果**:
- `src/Seiton.Core/Parsing/ExprType.cs` を新規作成: 抽象基底 `ExprType` + 具体型 7 種（`AnyExprType`, `NullExprType`, `BoolExprType`, `NumberExprType`, `StringExprType`, `ObjectExprType`, `ArrayExprType`）。シングルトンアクセサ (`ExprType.Any`/`Bool`/etc.)。`IsAssignableTo()` により Any は全型と互換
- `ExpressionSemanticAnalyzer` に `public static ExprType InferType(int nodeId, ExpressionNode[] nodes, int[] arguments, ReadOnlySpan<byte> expressionUtf8)` を追加: Literal → 対応型、Unary `!` / Binary 比較・論理 → `Bool`、FunctionCall → 関数ごとの戻り型（Bool: contains/startsWith/endsWith/success/failure/always/cancelled、String: format/join/toJson/hashFiles、Any: fromJson）
- `ValidateStringArg` が shallow literal チェックから `InferType()` による bottom-up 型推論に変更。`expressionUtf8` を `ValidateFunctionArgumentTypes` → `ValidateStringArg` へ伝播
- `ExpressionStaticType` enum と `GetStaticType()` / `StaticTypeName()` ヘルパーを削除
- テスト 12 件追加（literals × 4、演算子 × 3、関数戻り値 × 3、コンテキストアクセス × 1、型不一致バリデーション × 1）。全 112 テスト通過

### Step 7.3: 式 Visitor ✅

- `VisitExprNode()` パターンの実装（パーサー仕様 §6.5, C# 実装仕様 §6.1）
- semantic checker をこのパターンで書き直す

**完了条件**: 式 AST の巡回が Visitor パターンで動作する

**実装結果**:
- `src/Seiton.Core/Parsing/ExpressionVisitor.cs` を新規作成: depth-first 巡回の 2 つのオーバーロードを提供
  - `VisitExprNode(... ExprNodeVisitor visitor)` — デリゲート版。スパンをキャプチャしない呼び元向け
  - `VisitExprNode<TVisitor>(... ref TVisitor visitor) where TVisitor : IExprNodeVisitor, allows ref struct` — ゼロアロケーション版。`ref struct` 実装者が `ReadOnlySpan<byte>` をフィールドに直接保持できる（C# 13 / .NET 9+ の `allows ref struct` 反制約と ref struct インターフェース実装を活用）
  - `IExprNodeVisitor` インターフェース（`void Visit(int nodeId, ExpressionNode node, int parentId, bool entering)`）を追加。`ref struct` が実装可能なため `ToArray()` 不要
  - 仕様シグネチャの `ExprNodeVisitor(node, parentId, entering)` に `nodeId` を追加。function callee 判別（`nodes[parentId].Left == nodeId`）に必要なため
  - ノード種別ごとに子を正しく巡回: Unary→Left, Binary→Left+Right, MemberAccess/WildcardAccess→Left, IndexAccess→Left+Right, FunctionCall→Left(callee)+Arguments, Leaf→なし
- `ExpressionSemanticAnalyzer.Validate()` を書き直し: 手書きの `ValidateNode()` 再帰を削除し、`private ref struct SemanticValidationVisitor : IExprNodeVisitor` + `VisitExprNode<TVisitor>` を使用
  - `SemanticValidationVisitor` が `ReadOnlySpan<byte> ExpressionUtf8` をフィールドに直接保持。`ToArray()` は一切なし
  - function callee 判別を `parentId` と `Nodes[parentId].Left == nodeId` で行う
- テスト 5 件追加（単一リテラルの enter/leave × 1、二項式の全ノード巡回 × 1、関数呼び出しで callee と引数が訪問されること × 1、enter-before-leave の巡回順序 × 1、root ノードの parentId == -1 × 1）
  - `VisitExprNode_FunctionCall_VisitsCalleeAndArguments` は `private ref struct IdentifierNamesVisitor : IExprNodeVisitor` + 同期ヘルパー `CollectIdentifierNames()` を使用。`async Task` は ref struct をまたげないため同期/非同期を分離して実現
- 全 117 テスト通過

---

## Phase 8: テスト強化・ベンチマーク

### Step 8.1: actionlint testdata ベースの統合テスト ✅

**状態**: 完了（actionlint err fixture に対する diagnostics サブセット照合を実装し、既存 corpus smoke と併用で回帰検知を強化）

- `tests/Seiton.Core.Tests/ParserTests.cs` の `Parse_ActionlintErrFixtures_ExpectedDiagnosticsSubset` を table-driven 化
- fixture ごとに期待する diagnostics の部分文字列を宣言し、共通ヘルパー `AssertFixtureDiagnosticSubset` で検証
- 初期対象 fixture を 6 件に拡張:
  - `empty.yaml` → `workflow root must be mapping`
  - `empty_on.yaml` → `unknown event in on`
  - `case_sensitive_keys.yaml` → `unexpected workflow key` / `unexpected job key`
  - `duplicate_keys.yaml` → `contains duplicate key`
  - `invalid_int_at_max_parallel.yaml` → `strategy.max-parallel must be integer`
  - `invalid_steps.yaml` → `cannot have both run and uses` / `requires run or uses`
- 期待不一致時は observed diagnostics を併記して失敗させるため、fixture 差分の調査が容易

**完了条件**: 主要テストケースで期待 diagnostics サブセットが一致

### Step 8.2: AST 構造テスト ✅

**状態**: 完了（AST 深部ノードの構築検証を統合テストとして追加し、特に matrix の RawYaml 階層を型レベルで検証）

- `tests/Seiton.Core.Tests/ParserTests.cs` に AST 構造テストを追加
  - `Parse_AstStructure_ComprehensiveWorkflow_PopulatesDeepNodes`
  - `Parse_AstStructure_MatrixRawYamlKinds_PopulatesStringArrayObjectNodes`
- 検証対象（抜粋）:
  - Workflow 直下: `Name`, `RunName`, `Permissions`, `Env`, `Defaults`, `Concurrency`
  - Event 系: `WebhookEvent`, `ScheduledEvent`（`ScheduleEntry.Cron/Timezone`）, `WorkflowDispatchEvent`（`DispatchInput`）, `WorkflowCallEvent`（`WorkflowCallEventInput/Secret/Output`）, `RepositoryDispatchEvent`
  - Job/Step 系: `Job` の主要フィールド、`ExecRun` / `ExecAction`、reusable workflow の `WorkflowCall`
  - Structural 系: `Strategy.Matrix`、`Container`、`Services`
  - Raw YAML 系: `RawYamlString` / `RawYamlArray` / `RawYamlObject` の実体型とネスト
- matrix 検証では `include/exclude/rows` の各経路で `RawYamlValue` サブタイプが正しく構築されることを確認

**完了条件**: 全 AST ノード型に最低 1 つの構築テスト

### Step 8.3: ベンチマーク更新 ✅

**状態**: 完了（`src/Seiton.Benchmark/ParsingBenchmark.cs` を実装し、AST 構築込みベンチマークと adapter 相当オーバーヘッド比較を追加。`MemoryDiagnoser` で allocation を記録）

- `ParsingBenchmark` を実装（`Small` / `Medium` / `Large` の 3 シナリオ）
  - `WorkflowParser.Parse (AST + rules)`（baseline）
  - `ExpressionExtractor.ExtractParseAndValidate`
  - `VYaml raw event scan`
  - `VYaml scan + adapter-like mapping`
- adapter 層オーバーヘッド測定は「生 VYaml スキャン」と「イベント種別マッピング込みスキャン」の差分で把握
- allocation 回帰計測は `Program.cs` の `MemoryDiagnoser.Default` + benchmark class の `[MemoryDiagnoser]` で有効化
- 実行コマンド:
  - `dotnet run -c Release --project src/Seiton.Benchmark -- --filter *ParsingBenchmark*`

**ベースライン（ShortRun, .NET 10, local）**:
- Small:
  - `WorkflowParser.Parse (AST + rules)`: `37.243 us`, `30072 B`
  - `ExpressionExtractor.ExtractParseAndValidate`: `6.827 us`, `14032 B`
- Medium:
  - `WorkflowParser.Parse (AST + rules)`: `299.190 us`, `244664 B`
  - `ExpressionExtractor.ExtractParseAndValidate`: `70.187 us`, `152160 B`
- Large:
  - `WorkflowParser.Parse (AST + rules)`: `1466.948 us`, `1135016 B`
  - `ExpressionExtractor.ExtractParseAndValidate`: `349.151 us`, `737320 B`

**完了条件**: ベンチマーク結果のベースライン記録

---

## 次にやるべきこと（2026-04-14 時点）

以下は、仕様整合を維持しつつ次の実装を安全に進めるための**実行順付き作業計画**。

### Priority 1: Parser Core の generic 化（必須）

1. **`WorkflowParser` に internal generic core を導入**
  - 状態: 完了
  - 目標: `ParseCore<TReader>(ref TReader reader, ReadOnlySpan<byte> source)`（`where TReader : IYamlStreamReader, allows ref struct`）を追加。
  - 方針: public API `Parse(byte[] utf8Yaml, string filePath)` は維持し、`VYamlStreamAdapter` は entrypoint の factory のみに閉じ込める。
  - 実施結果: `ParseCore<TReader>` を実装し、entrypoint 以外の parser 本体は generic reader で統一。
  - 完了条件: 達成。

2. **段階移行（大規模一括変換を避ける）**
  - 状態: 完了
  - フェーズA: scalar helper 群（`ParseString/Bool/Int/Float/Expression`）を generic 化。
  - フェーズB: top-level と `on` / `jobs` の骨格 parser を generic 化。
  - フェーズC: job/step/event の下位 parser を generic 化。
  - 実施結果: `WorkflowParser.cs` の `ref VYamlStreamAdapter` 依存は public parse entrypoint の adapter 生成箇所のみ。
  - 完了条件: 達成。

3. **置換耐性の検証**
  - 状態: 完了
  - `FakeYamlStreamReader` を使った最小統合テスト（root mapping, scalar parse, key traversal）を追加。
  - 将来の `YamlDotNet` adapter を想定したテスト観点を明文化。
  - 実施結果: `ParserAdapterResilienceTests` を追加し、`ParseWithReader<TReader>` 経由で最小 workflow parse と duplicate key 診断を検証。
  - 完了条件: 達成。

### Priority 2: Diagnostic モデルの仕様追従

4. **`filePath` を Diagnostic 出力へ正式反映**
  - 状態: 完了
  - 目標: `Parse(byte[], string)` の `filePath` を diagnostics に一貫して伝搬。
  - 補足: rule 由来 diagnostics も含めた最終出力で path が欠落しないことを確認。
  - 実施結果: parser diagnostics は `WorkflowParser.Parse` で `FilePath` を付与、rule diagnostics は `LintConfig.FilePath` + `RuleBase` 共通ヘルパーで付与。
  - 完了条件: 達成（`RuleInterfaceTests` で parser/lint 双方の `FilePath` を検証）。

5. **Spec §10 との整合確認（RuleId/Help/RelatedLocations）**
  - 状態: 完了
  - `Diagnostic` モデルと `Seiton_Parser_spec.md` / `Seiton_Parser_csharp_spec.md` の記述差分を解消。
  - 必要なら spec 側の status table/本文も更新。
  - 実施結果: `Seiton_Parser_spec.md` の Diagnostic table に `FilePath` を追記し、`Seiton_Parser_csharp_spec.md` の `Diagnostic` シグネチャへ `FilePath` を追加。
  - 完了条件: 達成。

### Priority 3: テスト拡充（generic 化の回帰防止）

6. **actionlint err fixture の期待診断セット拡張**
  - 状態: 完了
  - 既存 subset を増やし、型違反・必須キー不足・排他制約・重複キーを優先拡張。
  - 実施結果: `Parse_ActionlintErrFixtures_ExpectedDiagnosticsSubset` に `missing_on.yaml` / `missing_jobs.yaml` / `merge_key_unsupported.yaml` を追加し、必須キー不足と merge key 診断を固定化。

7. **alias 系テスト追加**
  - 状態: 完了
  - dangling alias / recursive alias / alias merge の挙動を fixture 固定で可視化。
  - adapter 側吸収と parser 側責務の境界もテスト名で明示。
  - 実施結果: `undefined_anchor.yaml` / `recursive_anchors.yaml` / `merge_key_unsupported.yaml` を対象に回帰テストを追加し、異常系を決定的診断契約（fatal parse diagnostic または構造診断）として固定化。

8. **AST ゴールデンテスト拡張**
  - 状態: 完了
  - `runs-on` mapping、`workflow_call` job、`container/services` の深い構造を追加固定化。
  - 実施結果: 既存の `Parse_AstStructure_ComprehensiveWorkflow_PopulatesDeepNodes` と `Parse_JobRunsOnMapping_PopulatesRunnerGroupAndLabels` などで対象構造を継続検証。

### Priority 4: ドキュメント同期運用

9. **spec/plan 同期の運用ルールを明文化**
  - 状態: 完了
  - `Seiton_Parser_spec.md` を source of truth とし、以下 3 文書を同時更新対象として固定。
  - `Seiton_Parser_csharp_spec.md`
  - `Seiton_Parser_go_spec.md`
  - `parser_implementation_csharp_plan.md`
  - 実施結果: 3 文書を同時更新対象とする運用ルールを明文化し、実装時のドキュメント更新順を固定化。
  - 運用手順:
    1. 仕様レベル変更は `Seiton_Parser_spec.md` を先に更新する。
    2. 同一コミット（または同一 PR）で `Seiton_Parser_csharp_spec.md` / `Seiton_Parser_go_spec.md` / `parser_implementation_csharp_plan.md` を追従更新する。
    3. 追従不要と判断した文書がある場合は、PR 説明または plan の実施結果に理由を明記する。
    4. 実装変更後は「仕様（WHAT/WHY）と実装結果（lesson learned）が矛盾しない」ことを確認する。
  - 完了条件: 達成。

### 実行順（推奨）

1. Priority 1-1, 1-2（generic core の導入と段階移行）
2. Priority 1-3（置換耐性テスト追加）
3. Priority 2-4, 2-5（Diagnostic 仕様整合）
4. Priority 3-6, 3-7, 3-8（回帰防止テスト拡充）
5. Priority 4-9（文書同期）

### 追加作業リスト（仕様整合監査対応）

以下は、2026-04-14 の監査結果を反映した差分タスク。既存 plan の「完了済み項目」とは別に、次スプリントで消化する。

#### A. Parser と Lint の責務を仕様に合わせて確定

- [x] 方針決定: Job 制約（`uses` と `steps`/`runs-on` 排他、`with`/`secrets` の `uses` 依存、normal job の `steps`/`runs-on` 必須）を
  - パーサー責務に戻す
  - もしくは Lint 責務として `Seiton_Parser_spec.md` 側を改訂する
- [x] 決定した責務に合わせて `Seiton_Parser_spec.md` / `Seiton_Parser_csharp_spec.md` / `Seiton_Parser_go_spec.md` / 本 plan を同一 PR で同期更新
- [x] 期待挙動を固定するテストを追加
  - Parser 側で検証するなら `WorkflowParser.Parse` の diagnostics を直接検証
  - Lint 側で検証するなら Parser 非検出 + Lint 検出の組を明示

実装結果:
- Parser を責務の一次判定系として採用。`WorkflowParser.ParseJobNode` で Job 制約を診断するように変更。
- `ParserTests` の該当ケースを Parser 診断前提へ更新。
- `RuleInterfaceTests` の ParseDiagnostics 前提を更新し、RuleId 検証は parser 診断と衝突しない条件に修正。

#### B. 数値制約（`> 0`）の実装とテスト

- [x] `job.timeout-minutes` に `> 0` 制約を追加（0, 負値で error）
- [x] `step.timeout-minutes` に `> 0` 制約を追加（0, 負値で error）
- [x] `strategy.max-parallel` に `> 0` 制約を追加（0, 負値で error）
- [x] 回帰テストを追加
  - `timeout-minutes: 0` / `-1`
  - `max-parallel: 0` / `-1`
  - 既存の型不正テスト（non-int/non-float）との重複を避ける

実装結果:
- `WorkflowParser` に non-positive 数値検証を追加。
  - Job: `timeout-minutes <= 0` を error
  - Step: `timeout-minutes <= 0` を error
  - Strategy: `max-parallel <= 0` を error
- `ParserTests` に 3 つの回帰テストを追加（job/step timeout, strategy max-parallel の 0/-1 ケース）。
- `dotnet test --project tests/Seiton.Core.Tests/Seiton.Core.Tests.csproj` で全件パス（136 passed）。

#### C. 必須キー検証の負ケーステスト拡充

- [x] `on.workflow_call.inputs.<id>.type is required` の負ケースを追加
- [x] `on.workflow_call.outputs.<id>.value is required` の負ケースを追加
- [x] `on.schedule item requires cron` の負ケースを追加（空 mapping / timezone のみ）
- [x] 既存の happy path テストとの対になる table-driven テストへ統合

実装結果:
- `ParserTests` に `Parse_RequiredKeys_WorkflowCallAndSchedule_ReportsError_TableDriven` を追加。
- 以下 4 ケースを table-driven で検証:
  - workflow_call input で `type` 欠落
  - workflow_call output で `value` 欠落
  - schedule item が空 mapping
  - schedule item が timezone のみ

#### D. Alias 方針の明文化とテスト固定

- [x] Alias 解決を parser が担うか、YAML adapter 任せにするかを明文化
- [x] 方針に応じて diagnostics 契約を固定
  - undefined anchor
  - recursive anchor
  - merge key を含む alias ケース
- [x] corpus smoke の除外条件（`dangling_alias` など）を見直し、理由をコメントで明記

実装結果:
- Alias 解決は parser 本体ではなく YAML adapter / YAML ライブラリ層の責務として固定。
- `WorkflowParser.Parse` で adapter 由来例外を捕捉し、`yaml parse failure: ...` の fatal diagnostic として返す契約を追加。
- `ParserTests` の alias 異常テストを「診断または例外」から決定的契約へ更新。
  - `undefined_anchor`: fatal diagnostic（`yaml parse failure`）
  - `recursive_anchors`: 解析継続時の構造診断（例: `must be mapping`）
- `Parse_ActionlintErrFixtures_ExpectedDiagnosticsSubset` に alias 異常 fixture（`undefined_anchor.yaml` / `recursive_anchors.yaml`）を追加。
- corpus smoke の `dangling_alias` 除外・包含の意図をコメントで明示。

#### E. C# spec のドリフト修正

- [x] `runs-on` を「partial」から実装実態へ更新（mapping + expression の対応状況を明記）
- [x] 未実装/部分実装テーブルを現行コードに合わせて棚卸し
- [x] 本 plan の「現状サマリー」を現行実装に合わせて更新（古い記述の削除）

実装結果:
- `Seiton_Parser_csharp_spec.md` の `Runner (runs-on)` を実装済みへ更新し、A.5 の `ParseRunsOn` ステータスを scalar/sequence/mapping + expression 対応として明記。
- 同 spec の drift 箇所を更新（adapter 移行完了の実態、`parseTimeoutMinutes` の実装形態、Context Availability の generated table 適用）し、旧来の移行前記述を解消。
- 本 plan の「現状サマリー」を 2026-04-14 時点の実装状態に更新し、初期フェーズ前提の古い説明を削除。

#### F. 完了判定（この監査対応の Exit Criteria）

- [x] `dotnet test --project tests/Seiton.Core.Tests/Seiton.Core.Tests.csproj` が全パス
- [x] 仕様と実装の責務境界について、4 文書（Parser spec / C# spec / Go spec / plan）に矛盾がない
- [x] 上記 A-E の追加テストが CI で安定して再現可能

実装結果:
- テスト再実行を 2 回実施し、いずれも `137 passed / 0 failed` を確認。
  - 1 回目: duration 2.071s
  - 2 回目: duration 1.916s
- 4 文書の責務境界を突合し、以下の整合を確認。
  - Job 構造制約（`uses` vs `steps`/`runs-on`、`with`/`secrets` の `uses` 依存）は parser 側の一次診断契約。
  - Alias は adapter/library 側解決を前提とし、失敗時は parse failure 診断へ正規化（C#）。
  - YAML parse failure の扱いは parser spec 側でも adapter/library 起因失敗を含む旨を明記。
- A-E で追加した table-driven / fixture 固定テスト（必須キー、数値制約、alias 異常、job 制約）は再実行で同一結果を返し、回帰検知の再現性を確認。

### 追加作業リスト（C# spec 未充足項目対応）

以下は、`Seiton_Parser_csharp_spec.md` の「Partially implemented」領域を解消するための次スプリント向けタスク。

#### G. Rule Engine の拡張（spec 準拠）

- [x] `SyntaxRule` 依存の最小構成から、spec 記載のルール責務へ段階拡張する
- [x] 既存 rule（`JobStructureRule` / `ReusableWorkflowRule` / `PermissionsRule` / `PopularActionInputsRule`）の責務境界を文書化し、重複診断の優先順位を固定する
- [x] Rule ごとの回帰テストを table-driven で追加（正常系 1 + 異常系 2 以上）

完了条件:
- `Seiton_Parser_csharp_spec.md` の Rule Engine を partially から更新できるだけの実装・テスト証跡が揃う

実装結果:
- `RuleCatalog` を追加し、既定ルール構成を共通化（`SyntaxRule` / `LintEngine` の両方で同一構成を使用）。
- Rule 責務境界をコードコメントで固定し、優先順位（`job-structure` → `reusable-workflow` → `permissions` → `popular-action-inputs`）を明示。
- `LintEngine` で rule diagnostics を優先順位順に整列し、同一診断の重複を deterministic に統合する処理を追加。
- `RuleInterfaceTests` に rule別 table-driven 回帰テストを追加（各 rule で正常系 1 件 + 異常系 2 件）。
- 追加の lesson learned: `with: { ... }` を含むケースで `PopularActionInputsRule` が未発火する事象は rule 本体ではなく scalar slice 解決の問題だった。`VYamlStreamAdapter.GetScalarSlice()` を「ソース先頭からの逐次 forward 検索」で安定化し、`ParserTests` に `Parse_StepUses_WithFlowStyleInputs_PreservesUsesScalar` を追加して再発防止した。

#### H. Expression Semantic Checker の actionlint 同等性向上

- [x] 関数シグネチャ検証を arity 中心から型付きシグネチャ検証へ拡張（overload を含む）
- [x] `ExpressionSemanticAnalyzer.InferType()` の推論対象を拡大し、`Any` 依存の診断不能ケースを縮小する
- [x] 型不一致 diagnostics のメッセージ契約を固定し、Parser/ExpressionTests に統合テストを追加する

完了条件:
- C# spec §7.1/§7.3 の「Target」記述を実装済みへ寄せられる状態になる

実装結果:
- 欠落していた `ExpressionSemanticAnalyzer` / `ExpressionValidationContext` を復元し、関数仕様を overload 可能な typed signature モデル（戻り値型・引数型・可変長引数）へ置換。
- built-in 関数 (`contains`, `startsWith`, `endsWith`, `format`, `join`, `toJson`, `fromJson`, `hashFiles`, `success/failure/cancelled/always`) に対し、arity と型を同時検証するよう変更。
- diagnostics 契約を固定: `unknown expression function: ...` / `function '...' expects ...` / `argument N should be ...`。
- `format()` に対してプレースホルダ整合チェック（`{n}` と実引数数）を追加し、`format placeholder '{n}' requires argument ...` 診断を導入。
- `InferType()` を拡張し、`MemberAccess` / `IndexAccess` / `WildcardAccess` / `FunctionCall` を bottom-up 推論。特に `fromJson('<literal-json>')` は literal JSON を解析して object/array 要素型を推論。
- `ExpressionTests` に overload 許容ケース（array `contains`）と `fromJson` literal 推論（member/index）、`format()` プレースホルダ整合の正常/異常ケースを追加し、既存 Parser 側の semantic 診断統合テストと合わせて回帰を固定。

#### I. Context Availability の位置依存検証を完成

- [x] 生成テーブル（`Availability.g.cs`）をキー位置粒度（`if`/`env`/`with` など）で検証するテストセットを追加
- [x] workflow/job/step の同一 root identifier でも位置により許可が変わるケースを fixture で固定化
- [x] C# spec §7.2 の「Target」記述を現実装と一致する表現へ更新する

完了条件:
- Context availability に関する partially/target 表記が解消され、仕様・実装・テストの 3 点が一致する

実装結果:
- `Availability.g.cs` の root-context 判定を parser の expression site（workflow/job/step）に結びつけた現実装に合わせて、`Seiton_Parser_csharp_spec.md` §7.2 を更新。
- key 粒度 fixture `context-availability-key-granularity.yml` と `ParserTests` の fixture 駆動検証を spec 側の説明へ反映。
- これにより I の残作業は解消し、計画上の H/I 境界は「special function availability の finer-grained parity は将来課題、root context availability は現実装として完了」に整理した。

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
