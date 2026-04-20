# Seiton.Core リファクタリング計画

## 現状分析

| 指標 | 値 |
|---|---|
| 総ファイル数 | 101 (.cs) |
| 総行数 | ~23,200 行 |
| 最大ファイル | WorkflowParser.cs (5,335行) |
| 500行超のファイル | 10 ファイル |

### ファイルサイズ上位（500行超）

| ファイル | 行数 | 問題 |
|---|---|---|
| `Parsing/WorkflowParser.cs` | 5,335 | God class: 98メソッド、全パース処理を1ファイルに集約 |
| `Linting/LintConfigLibrary.cs` | 1,689 | パーサ + バリデーション + 正規化 + テンプレート生成の混在 |
| `Linting/LintEngine.cs` | 864 | 正規化ロジックの重複（LintConfigLibraryと） |
| `Parsing/ExpressionSemanticAnalyzer.cs` | 693 | ValidateNode が140行の再帰ディスパッチャ |
| `Linting/Rules/RunInputsContextDirectUseRule.cs` | 649 | 3ルール間で75-85%重複 |
| `Parsing/VYamlStreamAdapter.cs` | 621 | リプレイ状態管理の複雑さ |
| `Linting/PinRemediation/OciImageDigestResolver.cs` | 607 | 多責務 + GlobMatch重複 |
| `Linting/PinRemediation/GitHubActionShaResolver.cs` | 576 | 多責務 |
| `Linting/Rules/RunEnvContextDirectUseRule.cs` | 552 | 3ルール間で75-85%重複 |
| `Linting/Rules/RunSecretsContextDirectUseRule.cs` | 542 | 3ルール間で75-85%重複 |

---

## 問題の重要度別分類

### Critical: ユーティリティ関数の大量重複

最も深刻な問題は、同一のヘルパーメソッドがファイル単位で private static としてコピーされていること。ファイルをまたいだ共有がなく、同じロジックが最大14箇所に存在する。

| メソッド | 重複数 | 主な所在 |
|---|---|---|
| `EqualsAsciiIgnoreCase` | 14 | Rules全般, Parsing, Generated |
| `TrimAsciiWhiteSpace` | 14 | Rules全般, Parsing |
| `TryFindExpression` | 9 | SecretsRule系, RunContext系, TemplateInjection |
| `NormalizeAsciiLower` | 8 | Rules全般, RuleSpecificConfigNormalizer |
| `IsWhiteSpace` / `IsAsciiWhiteSpace` | 7 + 5 | Rules全般, Parsing |
| `BuildNormalizedSet` | 6 | CachePoison, Credentials, RunnerLabel 等 |
| `IsContextRootIdentifier` | 5 | RunContext系, ExprSemanticAnalyzer |
| `BuildLineStarts` | 5 | RunContext系, ExpressionExtractor, Unredacted |
| `OffsetToLineColumn` | 5 | 同上 |
| `ToLowerAscii` | 4 | Generated, ExprType, ExprSemanticAnalyzer |
| `IsSimpleIdentifier` | 4 | RunContext系, Unredacted |
| `TryParseActionReference` | 3 | OnlineAudit, PinRemediation, RefVersionMismatch |
| `GlobMatch` | 2 | LintEngine, OciImageDigestResolver |
| SHA-40検証 | 5 | ActionRefResolver, OnlineAudit, PinFix, RefVersion, Unpinned |

**推定削減行数**: 共有化により ~800–1,200行 削減可能

### High: WorkflowParser の巨大化

5,335行・98メソッドの static クラスに、ワークフロー解析の全階層が集中している。

**functional area の内訳:**

| 領域 | 概算行数 | メソッド数 |
|---|---|---|
| On-event パース | ~1,800 | ~25 |
| Jobs/Steps パース | ~1,200 | ~12 |
| Root セクション (permissions, env, defaults, concurrency) | ~500 | ~5 |
| Scalar/Expression ヘルパー | ~600 | ~18 |
| Strategy/Matrix/RawYaml | ~450 | ~7 |
| Container/Services | ~500 | ~5 |
| Location/Diagnostics/Utility | ~300 | ~15 |

**繰り返しパターン:**

1. **Mapping パーサーループ**: MappingStart確認 → HashSet生成 → キーループ → TryRegisterMappingKey → UTF-8キーマッチ → MappingEnd処理。25箇所以上で同一パターン。
2. **Sequence パーサーループ**: SequenceStart確認 → List生成 → アイテムループ → SequenceEnd処理。11箇所以上。
3. **Scalar パーサーの IYamlStreamReader / ref TReader 二重定義**: ParseString, ParseExpression, ParseFloat, ParseInt, ParseStringOrStringSequence がそれぞれ2つのオーバーロードとして存在。
4. **HashSet の都度割り当て**: マッピングパーサーごとに `new HashSet<Utf8String>()` が割り当てられている（38箇所）。

### High: RunContext系 3ルールのクローン

`RunEnvContextDirectUseRule`, `RunInputsContextDirectUseRule`, `RunSecretsContextDirectUseRule` は同一の解析パイプライン（runテキストスキャン → 式抽出 → AST解析 → 検出 → Fix生成）を3回実装している。

- **重複率**: 75-85%
- **合計行数**: 1,743行（552 + 649 + 542）
- **固有ロジック**: env は here-doc 安全性チェック、inputs は `github.event.inputs` チェーン、secrets は最小の特殊化

### Medium: LintConfigLibrary の多責務

1,689行に以下が混在:
- YAML テンプレート生成
- 設定ファイルパス探索
- 行ベースのカスタムパーサー (`LintConfigLineParser`)
- ルール正規化
- 除外パターン正規化
- Fix/Network 正規化

### Medium: 正規化ロジックの重複

`LintConfigLibrary` と `LintEngine` の両方に `NormalizeRules` / `NormalizeExclusions` が存在する。意図的（パース時 vs ランタイム）な可能性はあるが、動作の乖離リスクがある。

### Low: ExprType の OOP 階層

`ExprType` は abstract base + 7 derived classes という OOP パターンだが、浅い継承で制御されており、性能への影響も小さい。データ志向にするなら tagged union (discriminated union) 的な `readonly struct` + `ExprTypeKind` enum にできるが、優先度は低い。

### Low: AST ノードのデフォルト割り当て

`RawYamlObject.Properties` と `Workflow.Jobs` がデフォルトで空の `Dictionary` を割り当てている。使わなくても割り当てが発生する。

---

## リファクタリング方針

### 設計思想

- **Go のようなデータ志向**: struct, static メソッド, 明示的なデータの受け渡しを基本とする
- **C# らしさの維持**: `ReadOnlySpan<T>`, `ref struct`, record, パターンマッチング, file-scoped namespace など最新の C# 機能を活用
- **OOP は最小限**: 継承は、ルールの `IRule` 実装など明確なインターフェース境界にのみ使用。共有ロジックは static ヘルパーで提供する
- **パフォーマンス不退行**: ゼロアロケーション原則を維持。共有化にあたって新しいアロケーションを導入しない

---

## フェーズ別実行計画

### Phase 1: 共有ユーティリティの抽出（最小リスク・最大効果）

**目的**: 14箇所にコピーされている private static ヘルパーを共通モジュールに集約。

#### 1-A: `Parsing/SpanHelpers.cs` を新設

WorkflowParser および ExpressionParser から以下を抽出:

```
static class SpanHelpers
├── TrimAsciiWhiteSpace(ReadOnlySpan<byte>) → ReadOnlySpan<byte>
├── IsAsciiWhiteSpace(byte) → bool
├── IsWhiteSpace(byte) → bool
├── EqualsAsciiIgnoreCase(ReadOnlySpan<byte>, ReadOnlySpan<byte>) → bool
├── ToLowerAscii(byte) → byte
├── IndexOf(ReadOnlySpan<byte>, byte) → int
└── ComputeLineColumn(ReadOnlySpan<byte>, int) → (int line, int col)
```

すべて `[MethodImpl(MethodImplOptions.AggressiveInlining)]` を付与。
呼び出し元の private コピーを `SpanHelpers.XXX` 呼び出しに置換。

#### 1-B: `Linting/ExpressionScanHelpers.cs` を新設

ルール間で重複する式スキャンヘルパーを集約:

```
static class ExpressionScanHelpers
├── TryFindExpression(ReadOnlySpan<byte>, int, out int, out int, out ReadOnlySpan<byte>) → bool
├── BuildLineStarts(ReadOnlySpan<byte>) → int[]
├── OffsetToLineColumn(int[], int) → (int line, int col)
├── IsContextRootIdentifier(ExpressionNode[], int, ReadOnlySpan<byte>) → bool
├── ConsumeWordIgnoreCase(ReadOnlySpan<byte>, ref int, ReadOnlySpan<byte>) → bool
├── SkipWhiteSpace(ReadOnlySpan<byte>, ref int) → void
├── TryReadIdentifier(ReadOnlySpan<byte>, ref int, out ReadOnlySpan<byte>) → bool
├── IsSimpleIdentifier(ReadOnlySpan<byte>) → bool
├── IsIdentifierStart(byte) → bool
└── IsIdentifierPart(byte) → bool
```

#### 1-C: `Linting/ActionRefHelpers.cs` を新設

```
static class ActionRefHelpers
├── TryParseActionReference(string, out string owner, out string repo, out string reference) → bool
├── TryParseActionReference(ReadOnlySpan<char>, ...) → bool  // span版
├── IsFullSha(ReadOnlySpan<char>) → bool  // 40-hex check
├── IsDigestSha256(ReadOnlySpan<char>) → bool  // sha256: check
├── GlobMatch(string pattern, string path) → bool
└── NormalizeAsciiLower(string) → string
```

#### 1-D: `Linting/RuleConfigHelpers.cs` を新設

```
static class RuleConfigHelpers
├── BuildNormalizedSet(IReadOnlyList<string>?, StringComparer) → HashSet<string>
└── NormalizeAsciiLower(string) → string  // 1-C と統合可能
```

**推定効果**: ~1,000行削減、14×重複 → 1箇所に統一

**検証**: 全テストスイート通過。grep で private copy が残っていないことを確認。

---

### Phase 2: WorkflowParser の partial class 分割

**目的**: 5,335行の god class を機能領域別の partial ファイルに分割。動作変更なし。

**方針**: C# の `partial class` を使い、名前空間・クラス名・アクセス修飾子を一切変えずにファイルを分割する。

```
Parsing/
├── WorkflowParser.cs              // エントリポイント + ParseCore (~400行)
├── WorkflowParser.Primitives.cs   // Scalar/Expression/Location helpers (~600行)
├── WorkflowParser.On.cs           // ParseOnEvents 以下全 on: 関連 (~1,800行)
├── WorkflowParser.Jobs.cs         // ParseJobsMapping, ParseJobNode, RunsOn, Env, Outputs (~1,200行)
├── WorkflowParser.Steps.cs        // ParseSteps, ParseStep, StepWith (~350行)
├── WorkflowParser.Strategy.cs     // Strategy, Matrix, RawYaml (~450行)
└── WorkflowParser.Containers.cs   // Services, Container, Credentials, StringMapping (~500行)
```

**留意点**:
- static class の partial 分割であり、インスタンス状態はない
- 各 partial ファイル内のメソッドは互いに呼び出し可能（同一クラスのため）
- ファイル間の循環参照の心配は不要

**推定効果**: 最大ファイルが ~400行に縮小、ナビゲーション性大幅改善

**検証**: ビルド通過 + パーサーテスト全通過

---

### Phase 3: RunContext 系ルールの統合

**目的**: 3ファイル1,743行のクローンを、共有パイプライン + 3つの薄い定義に圧縮。

#### 構造

```
Rules/
├── RunContextDirectUseAnalyzer.cs    // 共有スキャン・Fix パイプライン (~350行)
├── RunEnvContextDirectUseRule.cs     // env 固有: 検出 + here-doc ガード (~120行)
├── RunInputsContextDirectUseRule.cs  // inputs 固有: github.event.inputs チェーン (~100行)
└── RunSecretsContextDirectUseRule.cs // secrets 固有: secrets.* 検出 (~60行)
```

#### RunContextDirectUseAnalyzer の責務

```
static class RunContextDirectUseAnalyzer
├── ScanRunExpressions(...)               // run テキストから ${{ }} を抽出・パース
├── BuildExpressionLocation(...)          // 式の位置を TextRange に変換
├── TryResolveShellVariableInEnv(...)     // env マッピングから shell 変数名を解決 (inputs/secrets 共通)
├── TryExtractExpressionBody(...)         // env 値から式本体を抽出
├── TryExtractEmbeddedExpressionBody(...) // ${{ }} ラッパーを除去
└── IsPowerShell(...)                     // シェル種判定
```

各ルールは `IRule` を実装し、以下のみ定義:
- `Id`, `Name`
- `ContainsTargetReference(ExpressionNode[], ReadOnlySpan<byte>)` — AST に対象コンテキストが含まれるか
- `TryParseSimpleReference(ReadOnlySpan<byte>, ...)` — 対象コンテキストの単純参照をパース
- `TryBuildFix(...)` — 修正提案を生成（env は直接置換、inputs/secrets は env マッピング経由）
- 診断メッセージ、Fix タイトル

**推定効果**: 1,743行 → ~630行（約64%削減）

**検証**: RunContext 系テスト全通過 + LintEngine 統合テスト通過

---

### Phase 4: LintConfigLibrary の責務分離

**目的**: 1,689行の多責務クラスを、役割ごとに分割。

```
Linting/
├── LintConfigLibrary.cs              // ファサード: Validate, FindRecommendedConfigPath (~80行)
├── LintConfigParser.cs               // LintConfigLineParser 抽出 (~600行)
├── LintConfigNormalizer.cs           // NormalizeRules/Exclusions/Fix/Network (~400行)
└── LintConfigTemplateGenerator.cs    // GenerateTemplateYaml (~200行)
```

**留意点**:
- `LintConfigLineParser` は既に nested class として存在するため、抽出の自然な境界がある
- `LintEngine.NormalizeRules` との重複は Phase 4 のスコープ内で検討し、共通部分を `LintConfigNormalizer` に統合するか、明示的にコメントで差分理由を記録する

**推定効果**: 最大クラス 1,689行 → ~600行

---

### Phase 5: 中規模の改善（オプショナル）

#### 5-A: UnredactedSecretsRule のヘルパー共有化

Phase 1 の共有ユーティリティ適用後、このルール固有の重複（BuildLineStarts, OffsetToLineColumn, IsSimpleIdentifier 等）が自動的に解消される。残るのは sink 解析ロジックのみとなり、~300行程度に圧縮される見込み。

#### 5-B: WorkflowParser の HashSet 割り当て最適化

マッピングパーサーごとに `new HashSet<Utf8String>()` を割り当てている問題。

**選択肢**:
1. 再利用可能な `HashSet` を ref パラメータで受け渡し、`Clear()` で再利用
2. 小規模マッピング（キー数 < 8）では `Span<Utf8String>` + 線形探索で代替
3. 当面は維持（ワークフローあたり1回の解析なのでインパクトは限定的）

推奨: 選択肢 3（ベンチマーク結果を見て必要なら 2 を適用）

#### 5-C: Scalar パーサーの IYamlStreamReader / ref TReader 二重定義の統合

現状、ParseString 等の scalar パーサーが `IYamlStreamReader` 版と `ref TReader where TReader : IYamlStreamReader` 版の2つを持っている。

**選択肢**:
1. `IYamlStreamReader` 版を削除し、generic 版に統一（呼び出し元の変更が必要）
2. `IYamlStreamReader` 版を generic 版のラッパーとして実装（thin wrapper のみ残す）

推奨: 選択肢 2（既存テストへの影響を最小化）

#### 5-D: AST ノードのデフォルト割り当て除去

`RawYamlObject.Properties` と `Workflow.Jobs` の空 Dictionary デフォルト値を除去し、nullable またはパーサー側で必ず設定するパターンに変更。

---

## 効果の見積もり

| フェーズ | 削減行数 | リスク | 工数 |
|---|---|---|---|
| Phase 1: 共有ユーティリティ | ~1,000行 | 低 | 小 |
| Phase 2: WorkflowParser 分割 | 0 (構造改善) | 低 | 小 |
| Phase 3: RunContext 統合 | ~1,100行 | 中 | 中 |
| Phase 4: LintConfigLibrary 分割 | 0 (構造改善) | 低 | 中 |
| Phase 5: 中規模改善 | ~200行 | 低-中 | 小 |
| **合計** | **~2,300行削減** | | |

構造改善を含めると、最大ファイルが 5,335行 → ~400行、重複コードが 14×コピー → 1箇所に統合される。

## 実行順序と依存関係

```
Phase 1 ──→ Phase 2 (独立)
         ──→ Phase 3 (Phase 1 のヘルパーに依存)
         ──→ Phase 4 (独立)
         ──→ Phase 5-A (Phase 1 に依存)

Phase 2 は Phase 1 と並行可能
Phase 4 は Phase 1-3 と並行可能
Phase 5 は Phase 1 完了後いつでも可能
```

推奨順: **Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5**

Phase 1 と Phase 2 は低リスクで即時効果があるため、最初に着手する。Phase 3 はテスト網羅性を確認してから実行する。

## 各フェーズ完了時の検証

1. `dotnet build` 成功
2. `dotnet test` 全通過
3. 重複コードの grep 確認（Phase 1）
4. パーサーベンチマーク比較（Phase 2, 5-B）
5. 新規 `GetScalarString()` が success path に追加されていないことの確認（Phase 2）

---

## 実装ステータス

### Phase 1: 共有ユーティリティの抽出 — ✅ 完了

**実施日**: 2026-04-20

**成果物 (4ファイル新設):**

| ファイル | 内容 |
|---|---|
| `Parsing/SpanHelpers.cs` | `EqualsAsciiIgnoreCase`, `TrimAsciiWhiteSpace` (2 overloads), `IsAsciiWhiteSpace`, `IsWhiteSpace`, `ToLowerAscii`, `NormalizeAsciiLower` (2 overloads), `IndexOf`, `ComputeLineColumn` |
| `Parsing/ExpressionScanHelpers.cs` | `TryFindExpression`, `BuildLineStarts`, `OffsetToLineColumn`, `IsContextRootIdentifier`, `ConsumeWordIgnoreCase`, `SkipWhiteSpace`, `TryReadIdentifier`, `IsSimpleIdentifier`, `IsIdentifierStart`, `IsIdentifierPart` |
| `Linting/ActionRefHelpers.cs` | `TryParseActionReference` (2 overloads), `IsFullCommitSha` (2 overloads), `NormalizePath`, `GlobMatch`, `GlobMatchCore` |
| `Linting/RuleConfigHelpers.cs` | `BuildNormalizedSet` |

**変更したファイル**: 33ファイル（private static コピーを削除し、`using static` に置換）

**実績値:**

| 指標 | Before | After |
|---|---|---|
| 総ファイル数 | 101 | 105 (+4 shared helpers) |
| 総行数 | 23,172 | 21,921 |
| **削減行数** | — | **-1,251行** |
| 重複メソッド定義数 | 113 | 0 |
| テスト | 477 passed | 477 passed |

### Phase 2: WorkflowParser の partial class 分割 — ✅ 完了

**実施日**: 2026-04-20

**成果物 (7 partial ファイルに分割):**

| ファイル | 行数 | 内容 |
|---|---|---|
| `WorkflowParser.cs` | 857 | エントリポイント, nested types, ParseCore, Root sections (permissions, env, defaults, concurrency), ParseJobsMapping |
| `WorkflowParser.On.cs` | 1,691 | ParseOnEvents以下全 on: イベントパーサー (25メソッド) |
| `WorkflowParser.Jobs.cs` | 911 | ParseJobNode, IsKnownJobKey, RunsOn, Environment, Outputs, WorkflowCall inputs/secrets |
| `WorkflowParser.Primitives.cs` | 638 | Scalar parsers, Expression validation, Location builders, TryRegisterMappingKey, AddError |
| `WorkflowParser.Containers.cs` | 455 | ParseServices, ParseContainerLike, ParseCredentials, ParseStringMapping |
| `WorkflowParser.Strategy.cs` | 429 | ParseStrategy, ParseMatrix, ParseRawYaml* |
| `WorkflowParser.Steps.cs` | 342 | ParseSteps, ParseStep, ParseStepWithInputsNode |

**実績値:**

| 指標 | Before | After |
|---|---|---|
| WorkflowParser.cs 行数 | 5,279 | 857 (最大ファイル: On.cs 1,691) |
| 総ファイル数 | 105 | 111 (+6 partial files) |
| 動作変更 | — | なし（partial class 分割のみ） |
| テスト | 477 passed | 477 passed |

### Phase 3: RunContext 系ルールの統合 — ✅ 完了

**実施日**: 2026-04-20

**成果物 (1ファイル新設、3ファイル書き換え):**

| ファイル | 行数 | 内容 |
|---|---|---|
| `RunContextDirectUseAnalyzer.cs` (NEW) | 333 | 共有パイプライン: BuildExpressionLocation, IsPowerShell (2 overloads), TryExtractExpressionBody, TryExtractEmbeddedExpressionBody, TryConsumeMemberOrBracketName, TryParseSimpleContextReference, ContainsContextRootReference, TryResolveShellVariableNameInEnv, TryResolveShellVariableName, SimpleReferenceParser delegate |
| `RunEnvContextDirectUseRule.cs` | 251 | env 固有: CheckRunNode, TryBuildFix + IsInsideNoExpandHereDoc, TryParseNoExpandHereDocStart, HereDocState |
| `RunInputsContextDirectUseRule.cs` | 338 | inputs 固有: CheckRunNode, TryBuildFix + ContainsInputsReference (github.event.inputs chain), TryParseSimpleInputsReference, TryConsumeGithubEventInputsRoot |
| `RunSecretsContextDirectUseRule.cs` | 140 | secrets 固有: CheckRunNode, TryBuildFix のみ (全共有メソッドはAnalyzerに委譲) |

**実績値:**

| 指標 | Before | After |
|---|---|---|
| 3ファイル合計行数 | 1,391 | 729 (3ルール) + 333 (Analyzer) = 1,062 |
| **削減行数** | — | **-329行** |
| 重複メソッド (BuildExpressionLocation等) | 3コピー | 1定義 |
| 重複メソッド (IsPowerShell) | 3コピー | 1定義 (2 overloads) |
| 重複メソッド (TryExtractExpressionBody等) | 2コピー | 1定義 |
| 重複メソッド (ContainsRootReference) | 2コピー | 1定義 (汎用rootToken) |
| 重複メソッド (TryResolveShellVariableNameInEnv) | 2コピー | 1定義 (delegate parameterized) |
| テスト | 477 passed | 477 passed |

### Phase 4: LintConfigLibrary の責務分離 — 🔲 未着手

### Phase 5: 中規模の改善 — 🔲 未着手
