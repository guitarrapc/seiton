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

以下は、`Seiton_Parser_csharp_spec.md` の同期後ステータスを前提にした優先度付きの継続タスク。

### Priority 1: 仕様整合の残ギャップを埋める

1. **YAML アダプター境界の完全整備**
  - `WorkflowParser` の `ref VYamlStreamAdapter` 直結を、仕様の `IYamlStreamReader` 境界（または `WorkflowParser<TReader>`）に合わせる。
  - 目的: YAML 実装差し替え容易性と仕様 §0.3 の整合。

2. **String-free success path の厳密化**
  - `VYamlStreamAdapter.GetScalarTag()` 内の UTF-16 変換を除去し、タグ推定を UTF-8 バイト処理のみで完結させる。
  - 目的: 仕様 §0.2.1 / §11.1 の厳密遵守。

3. **`runs-on` mapping / expression の完全対応**
  - 現在の scalar/sequence 中心実装に対し、`labels` + `group` mapping と expression パスを仕様どおり補完。
  - 目的: 仕様 §2.12 / §3.13 の残差分解消。

### Priority 2: ルール層の実用化

4. **`LintEngine` 導入と parser からの rule 実行分離**
  - 状態: 完了
  - parser 直接実行だった `SyntaxRule` を `LintEngine.Check(byte[], string)` 側へ移し、`WorkflowParser.Parse(...)` は parse diagnostics のみを返すように整理。
  - `LintResult` を追加し、`ParseResult` と最終 diagnostics を分離して保持。
  - 目的: 仕様 §1.3 アーキテクチャへ整合。

5. **Rule セットの拡張（SyntaxRule 依存の縮小）**
  - 状態: 完了
  - `JobStructureRule`、`ReusableWorkflowRule`、`PermissionsRule`、`PopularActionInputsRule` を追加し、`LintEngine` の既定 rule セットを分割構成へ更新。
  - `SyntaxRule` は互換ラッパーとして残しつつ、既定実行経路からは外した。
  - parser テストのうち rule 由来の診断期待は `LintEngine` ベースへ移行。
  - reusable workflow 制約、permissions 値検証、popular action input 検証を parser 直書きではなく `IRule` 実装側で評価する形に整理。
  - 目的: 仕様 §8.3 の「ルールエンジン化」を進める。

### Priority 3: テストの網羅性を仕様レベルへ引き上げる

6. **actionlint err fixture の期待診断セット拡張**
  - 既存 subset を増やし、主要カテゴリ（型違反、必須キー不足、排他制約、重複キー）を網羅。

7. **alias 系の明示テスト追加**
  - dangling alias / recursive alias / alias merge の挙動を fixture 固定で検証し、仕様との差分を可視化。

8. **AST ゴールデンテストの拡張**
  - `runs-on` mapping、container/services、workflow_call job などの深い構造を fixture ベースで固定化。

### Priority 4: ドキュメント整合

9. **`Seiton_Parser_csharp_spec.md` と `parser_implementation_csharp_plan.md` の定期同期運用**
  - 実装完了時に status table と plan の「完了/未完」記述を同時更新する運用ルールを明文化する。

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
