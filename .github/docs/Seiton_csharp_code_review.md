# Seiton.Core アーキテクチャ評価レポート

> 評価日: 2026-04-24
> 対象: `src/Seiton.Core/` (Parsing + Linting + Generated)
> 目的: リリース前最終アーキテクチャ検証

---

## 0. 総合評価

**結論: リリース品質に達している。** 重大なアーキテクチャ違反はない。以下に評価詳細と軽微な改善候補を示す。

| 評価軸 | 判定 | 根拠 |
|---|---|---|
| C# 実装品質 | ✅ 良好 | 手書き再帰下降パーサ、UTF-8 span ベース、partial class 分割で可読性維持 |
| メンテナンス性 | ✅ 良好 | 4 層分離、50 ルールがすべて同一パターン、仕様ドキュメントと実装の一致度が高い |
| NativeAOT 対応 | ✅ 完全対応 | リフレクション使用ゼロ、`PublishAot=true` で CLI が AOT ビルド済み |
| メモリ最適化 | ✅ 良好 | AstArena + PooledBuffer + SliceMap + Utf8Slice で主要パスのアロケーション抑制済み |
| 仕様準拠 | ✅ 合致 | Parser spec / Linter spec の全 AST ノード・49 ルールが実装済み |
| データ志向設計 | ✅ 適切 | AST ノードは record struct / sealed class + init プロパティ、過度な OOP なし |
| YAML パーサ差し替え | ✅ 可能 | `IYamlStreamReader` アダプタ層で VYaml を完全隔離（1 箇所の例外あり） |
| コード品質 | ✅ 良好 | LINQ 不使用（parser/linter ホットパス）、正規表現不使用、テスト網羅あり |

---

## 1. C# 実装品質とメンテナンス性

### 1.1 良好な点

1. **partial class による WorkflowParser 分割**: `WorkflowParser.cs`（ルート解析）に加え、`.Jobs.cs`, `.Steps.cs`, `.Strategy.cs`, `.On.Core.cs`, `.On.Webhook.cs`, `.On.Schedule.cs` 等、機能単位で 14 ファイルに分割。1,000 行超の単一ファイルを回避し、各セクションの変更が局所化されている。

2. **ルールの uniform パターン**: 49 のローカルルール + 4 のオンラインルールがすべて `RuleBase` を継承し、`RuleId` enum + `VisitXxx` コールバック + `GetDiagnostics()` の統一契約に従う。新規ルール追加時の認知コストが低い。

3. **`RuleCatalog` の静的ファクトリ配列**: ルール生成を `Func<IRule>` の static lambda で登録。priority 番号付きで deterministic な実行順序を保証。ルール追加は 1 行追加のみ。

4. **`RuleId` enum + `RuleIdExtensions.ToId()` の switch 式**: 文字列変換を switch 式で静的に定義し、`FrozenDictionary` で逆引き。NativeAOT 安全かつ高速。

5. **型安全なスカラーノードハンドル**: `StringNodeId`, `BoolNodeId`, `IntNodeId`, `FloatNodeId` が `AstArena` へのインデックスを内包。`_raw == 0` で "値なし" を表現し、`HasValue` プロパティで nullable 代替。ボクシングなし。

6. **`SliceMap<T>`**: `Dictionary<string, T>` を排除し、`Utf8Slice[]` + 線形スキャンで GitHub Actions 典型規模（1–25 エントリ）に最適化。大小文字無視比較も ASCII ベースで手実装。

7. **`PooledBuffer<T>`**: `ArrayPool<T>.Shared` をラップした成長可能バッファ。パーサ内部の一時バッファで `List<T>` の代替として使用。

### 1.2 軽微な改善候補

| # | 対象 | 内容 | 優先度 |
|---|---|---|---|
| M-1 | `RuleBase.diagnostics` | `List<Diagnostic>` を毎回 `Clear()` で再利用しているが、内部配列は縮小されない。大規模ワークフローの後に小規模を解析すると過剰確保が残る。`LintEngine` がルールインスタンスを再利用する前提なら許容範囲だが、ルールをワンショット使用する場合は初期容量を小さく保つことを検討。 | P2 |
| M-2 | `LintEngine` のフィールド数 | `_diagnostics`, `_ruleDiagnostics`, `_activeRules`, `_activeOnlineRules`, `_seen`, `_suppressedByRule`, `_suppressionRecords` の 7 フィールドが `LintEngine` に直接保持されている。現時点では問題ないが、責務が増える場合は suppression 関連を内部構造体に切り出すことで可読性が改善する。 | P3 |
| M-3 | `StepExec` の継承ヒエラルキー | `StepExec` (abstract class) → `ExecRun` / `ExecAction` は最小限の OOP であり適切。ただし `StepExecKind` enum が `StepExec.Kind` プロパティに格納されているため、パターンマッチ (`step.Exec is ExecRun run`) と enum チェック (`step.Exec.Kind == StepExecKind.Run`) が混在可能。ルール内は一貫してパターンマッチを使用しており実害はないが、enum フィールドが冗長である。 | P3（変更不要） |

---

## 2. NativeAOT レディネス

### 2.1 検証結果: 完全対応

- **リフレクション使用**: `src/Seiton.Core/**/*.cs` に `System.Reflection` の using、`typeof()`、`Activator.Create`、`MethodInfo`、`BindingFlags` 等のリフレクション API は一切存在しない。
- **`GetType()` 使用**: `ExprType.IsAssignableTo()` 内の `GetType() == target.GetType()` のみ。これは `System.Object.GetType()` であり、NativeAOT で問題なく動作する。
- **CLI プロジェクト**: `Seiton.csproj` に `<PublishAot>true</PublishAot>` + `<InvariantGlobalization>true</InvariantGlobalization>` が設定済み。
- **ConsoleAppFramework**: Source Generator ベースのため AOT 互換。
- **VYaml**: VYaml の pull parser (`YamlParser.FromBytes`) はリフレクション不要。`YamlSerializer.Deserialize<T>` は使用していない。
- **`LintConfigYamlParser`**: VYaml を直接使用するが、`ParseYamlDom` は手書きの pull-parser → untyped DOM 変換であり、`YamlSerializer` を使用しない。AOT 安全。
- **`FrozenDictionary`**: .NET 8+ の `FrozenDictionary` / `FrozenSet` は AOT 対応済み。

### 2.2 改善候補: なし

現時点で NativeAOT に関する問題は発見されなかった。

---

## 3. メモリ最適化

### 3.1 パーサ側の評価

| パターン | 実装状況 | 判定 |
|---|---|---|
| UTF-8 span ベースのキーチェック | `Utf8MappingDispatch.TryMatchFirstOrdered` + `IUtf8OrderedKeyTable` で全キー比較が `ReadOnlySpan<byte>.SequenceEqual` | ✅ |
| `GetScalarString()` の不使用 | パーサホットパスでの使用は `WorkflowParser.On.Core.cs` L281 の 1 箇所のみ（未知イベント名のフォールバック）。成功パスでは不使用。 | ✅ |
| `AstArena` によるスカラーノードプーリング | `ThreadStatic` キャッシュ + `Rent/Dispose` パターン。バッキング配列は `Grow` で倍増し、`Dispose` でリセット後に再利用。 | ✅ |
| `Utf8Slice` ゼロコピー | AST ノードのすべての文字列値が offset+length ペアで保持。デコードは診断メッセージ生成時のみ。 | ✅ |
| `SliceMap` による Dictionary 排除 | `jobs`, `env`, `outputs`, `inputs`, `services`, `matrix.rows` すべてで使用。 | ✅ |
| LINQ 不使用 | パーサ配下の全 `.cs` ファイルに LINQ の using / メソッド呼び出しなし。 | ✅ |
| 正規表現不使用 | パーサ配下に `Regex` 使用なし。 | ✅ |

**ベンチマーク実測値** (Large ワークフロー、~20 jobs × ~12 steps):
- パーサ単体: 7.9ms / 113KB allocated
- Lint (parse + lint): 12.0ms / 464KB allocated
- VYaml raw scan 比: パーサは VYaml 生イベント走査の ~24x（AST 構築 + 式パース + セマンティック解析含む）

### 3.2 リンター側の評価

| パターン | 実装状況 | 判定 |
|---|---|---|
| `LintConfig.ParseExpression()` でキャッシュ | XXH64 コンテンツハッシュベースのキャッシュ。同一式テキストの重複パースを排除。 | ✅ |
| `LintConfig.GetLineStarts()` で遅延キャッシュ | 初回アクセスで構築、2 回目以降はキャッシュ返却。 | ✅ |
| `Utf8Slice.ToUtf8StringZeroCopy()` | `Utf8String` 生成時のゼロコピーパスが提供されている。 | ✅ |
| LINQ 不使用（ホットパス） | `OciImageDigestResolver` (ネットワーク I/O) のみ `FirstOrDefault` を使用。パーサ/リンタートラバーサルでは不使用。 | ✅ |

### 3.3 軽微な改善候補

| # | 対象 | 内容 | 優先度 |
|---|---|---|---|
| P-1 | `LintEngine._diagnostics` / `_ruleDiagnostics` | `List<Diagnostic>` の初期容量 16 / 64 は妥当だが、`Check` 呼び出し間でインスタンスを再利用する場合、大規模ファイル後に内部配列が大きくなる。ワンショット使用なら問題なし。 | P3 |
| P-2 | `ExpressionParseResult.Nodes` / `Arguments` | 式パース結果は `ExpressionNode[]` + `int[]` の new 配列。`LintConfig.ParseExpression()` のキャッシュで同一式テキストは再パースされないため、実質的な影響は小さい。将来、式パース結果もアリーナ化すればさらにアロケーション削減可能だが、現状のベンチマーク値から見て ROI は低い。 | P3 |

---

## 4. 仕様に対する実装の整合性

### 4.1 パーサ仕様 (`Seiton_Parser_spec.md`)

| 仕様項目 | 実装状況 |
|---|---|
| §1.1 Entry Point: `Parse(utf8Yaml, filePath) -> ParseResult` | ✅ `WorkflowParser.Parse` / `ParseClassified` |
| §1.1.2 Document Kind Classification | ✅ `DocumentKindClassifier` + `TryReadRootStructuralHints` |
| §2.2 Workflow AST | ✅ `Workflow` class with all specified fields |
| §2.3 Event types (6 種) | ✅ `WebhookEvent`, `ScheduledEvent`, `WorkflowDispatchEvent`, `WorkflowCallEvent`, `RepositoryDispatchEvent`, `ImageVersionEvent` |
| §2.4 Job | ✅ 全フィールド実装 |
| §2.5 Step / ExecRun / ExecAction | ✅ |
| §2.6 Common Node Types | ✅ `StringNodeId`, `BoolNodeId`, `IntNodeId`, `FloatNodeId` (AstArena ハンドル) |
| §2.7–2.15 構造ノード | ✅ `Permissions`, `Env`, `Defaults`, `Concurrency`, `Environment`, `Runner`, `Strategy`, `Matrix`, `Container`, `Services`, `WorkflowCall` |
| §3.1 Hand-written recursive descent | ✅ |
| §3.1.1 YAML Anchor/Alias | ✅ `VYamlStreamAdapter` でアダプタ層が担当 |
| §3.3 Mapping Traversal Pattern | ✅ `Utf8MappingDispatch` + `IUtf8OrderedKeyTable` |

### 4.2 リンター仕様 (`Seiton_Linter_spec.md`)

| 仕様項目 | 実装状況 |
|---|---|
| §2 Entry Point: `Check(utf8Yaml, filePath) -> LintResult` | ✅ `LintEngine.Check` |
| §4.1 Pass Hooks (8 コールバック) | ✅ `IPass` インターフェースに全 8 フック |
| §4.2 Traversal Order | ✅ `WorkflowVisitor.Visit` / `VisitActionMetadata` |
| §4.3 Rule Contract | ✅ `IRule` : `IPass` + `Id` + `Name` + `SetConfig` + `GetDiagnostics` |
| §4.4 Normative Rule Catalog (49 + 4) | ✅ `RuleCatalog` に全 53 ルール登録 |
| §5 Exclusion/Suppression | ✅ `LintEngine` 内で inline suppression + file exclusion 処理 |

### 4.3 欠落: なし

仕様書に記載された全 AST ノードと全ルールが実装されている。

---

## 5. アーキテクチャ仕様への準拠

### 5.1 4 層分離

| 層 | 仕様 | 実装 | 判定 |
|---|---|---|---|
| YAML Stream Layer | YAML イベント読み取り + 位置メタデータ | `IYamlStreamReader` + `VYamlStreamAdapter` | ✅ |
| Workflow Syntax Parsing Layer | マッピング/シーケンス走査 + 形状制約 + 診断 | `WorkflowParser` (partial class, 14 ファイル) | ✅ |
| Expression Parsing & Semantics Layer | `${{ }}` 抽出 + パース + セマンティック検査 | `ExpressionExtractor` + `ExpressionParser` + `ExpressionSemanticAnalyzer` | ✅ |
| Diagnostics Layer | severity/message/location の一貫表現 | `Diagnostic` record struct + `TextRange` + `TextPosition` | ✅ |

### 5.2 データ志向設計の評価

**適切にデータ志向が実現されている。**

1. **AST ノード**: `Workflow`, `Job`, `Step` 等は `sealed class` + `init` プロパティのプレーンなデータコンテナ。振る舞いを持たない（メソッドなし）。仕様 §2.1「All major nodes carry source position」に沿い、全ノードが `TextRange Range` を保持。

2. **スカラーノード**: `StringNodeId` 等の `readonly record struct` ハンドルが `AstArena` のフラット配列にインデックス。これは ECS (Entity Component System) 的なデータ志向パターンであり、キャッシュフレンドリーかつアロケーション効率が高い。

3. **式 AST**: `ExpressionNode` は `readonly record struct` でフラット配列に格納。`ExpressionNodeKind` enum で判別。class ヒエラルキーではなく構造体 + enum のデータ志向設計。

4. **ルール**: `RuleBase` は唯一の継承ヒエラルキーだが、これはビジタパターンのコールバック実装に必要な最小限の OOP。ルール自体はデータ（`RuleId` enum + 設定）とロジック（`VisitXxx`）の組み合わせであり、過度な OOP ではない。

5. **`SliceMap<T>`**: データ配列 + 線形スキャンのフラットな構造。`Dictionary` の代替としてデータ指向を徹底。

### 5.3 過度な OOP に該当する箇所: なし

- `ExprType` の class ヒエラルキー (`AnyExprType`, `BoolExprType`, `ObjectExprType`, `ArrayExprType`) は式の型システム表現として必要最小限。sealed class + private protected コンストラクタで外部拡張を防止。
- `StepExec` → `ExecRun` / `ExecAction` は 2 バリアントの判別のみ。将来 discriminated union が C# に導入されれば struct union に移行可能だが、現状の class ベースは実用上問題なし。
- `RawYamlValue` → `RawYamlString` / `RawYamlArray` / `RawYamlObject` は matrix の非構造値を表現するもので、仕様上の要請。

---

## 6. YAML パーサの差し替え可能性

### 6.1 アダプタ層の実装

`IYamlStreamReader` インターフェースがパーサコアと YAML ライブラリの唯一の境界。

- **VYaml 固有型の漏出**: `VYaml.Parser` namespace の使用は `VYamlStreamAdapter.cs` 1 ファイルのみに完全封じ込め。パーサコア (`WorkflowParser.*`) は `IYamlStreamReader` のみに依存。
- **テスト用フェイク**: `FakeYamlStreamReader` がテストで使用されており、アダプタ置換の実証がある。
- **カスタム enum**: `YamlEventKind`, `ScalarTag` は VYaml 非依存の独自定義。

### 6.2 VYaml 漏出箇所: 1 箇所

| ファイル | 内容 | リスク |
|---|---|---|
| `LintConfigYamlParser.cs` | `using VYaml.Parser;` で `YamlParser.FromBytes` を直接使用 | **低い** |

`LintConfigYamlParser` は lint 設定 YAML のパースに VYaml を直接使用している。これはワークフロー/アクション YAML のパースとは別のコードパスであり、ホットパスではない。ただし、YAML ライブラリの完全差し替え時にはこのファイルも対応が必要。

### 6.3 差し替え手順

1. `IYamlStreamReader` を実装する新アダプタを作成（例: `YamlDotNetStreamAdapter`）
2. `WorkflowParser.ParseClassified()` 内の `new VYamlStreamAdapter(...)` を新アダプタに置換
3. `LintConfigYamlParser.ParseYamlDom()` を新ライブラリ向けに書き換え
4. パーサコア（`WorkflowParser.*` の 14 partial ファイル）は変更不要
5. 既存テストはそのまま通過

### 6.4 改善候補

| # | 内容 | 優先度 |
|---|---|---|
| Y-1 | `LintConfigYamlParser` も `IYamlStreamReader` 経由にするか、別の config-YAML アダプタを導入することで VYaml 依存を `VYamlStreamAdapter.cs` 1 ファイルに完全集約可能。ただし、config YAML のパースはホットパスではなく、差し替え頻度も低いため、ROI は低い。 | P3 |

---

## 7. その他のコード品質評価

### 7.1 エラー回復戦略

パーサは recovery-first 設計を一貫して実装。

- 未知キーで `SkipCurrentNode()` + 診断追加後に次の兄弟へ継続
- 必須キー欠落はスコープ終了後にバリデーション
- YAML アダプタ例外は `ParseClassified` の try-catch で fatal diagnostic に変換
- 結果: 1 回のパースで複数エラーを返却可能（仕様 §3.1 準拠）

### 7.2 テストカバレッジ

- パーサテスト: `tests/Seiton.Core.Tests/` に包括的なテスト群
- `FakeYamlStreamReader` によるアダプタ非依存テスト
- 式パーサ/セマンティクスの独立テスト (`ExpressionTests`)
- lint ルールの個別テスト + 統合テスト

### 7.3 生成コードの管理

- `Generated/*.g.cs` は全て `// <auto-generated>` ヘッダ + 再生成コマンドを記載
- `Seiton.Update` プロジェクトの `sync-*` / `verify-*` コマンドで CI 検証可能
- 6 ファイル: `Availability.g.cs`, `ContextTypes.g.cs`, `FunctionSpecs.g.cs`, `PopularActions.g.cs`, `RunnerLabels.g.cs`, `WebhookTypes.g.cs`

### 7.4 セキュリティ考慮

- 入力バリデーション: パーサは `byte[]` UTF-8 入力を直接処理し、文字列デコードを最小化
- テンプレートインジェクション検出: `TemplateInjectionRule` が untrusted event data の式展開を検出
- シークレット漏洩検出: `UnredactedSecretsRule`, `SecretsOutsideEnvRule`, `SecretsWholeContextAccessRule` 等
- ネットワークアクセス: online ルールはオプトインのみ（`rules.<id>.enabled: true`）

### 7.5 ベンチマーク基盤

- `BenchmarkDotNet` による `ParsingBenchmark` / `LintBenchmark` / `ActionRefParseBenchmark` が整備済み
- Small / Medium / Large の 3 サイズでアロケーション (Allocated) と実行時間を計測
- 回帰検知の基盤として機能している

---

## 8. 改善候補サマリー

全項目は軽微であり、リリースブロッカーではない。

| # | カテゴリ | 内容 | 優先度 |
|---|---|---|---|
| M-1 | メンテナンス性 | `RuleBase.diagnostics` の容量管理 | P2 |
| M-2 | メンテナンス性 | `LintEngine` の suppression フィールド構造化 | P3 |
| M-3 | 設計 | `StepExecKind` enum の冗長性 | P3（変更不要） |
| P-1 | メモリ | `LintEngine` の List 内部配列の残存 | P3 |
| P-2 | メモリ | 式パース結果のアリーナ化 | P3 |
| Y-1 | 差し替え性 | `LintConfigYamlParser` の VYaml 直接依存 | P3 |

---

## 9. 結論

Seiton.Core は以下の設計目標をすべて達成している。

1. **パフォーマンス**: UTF-8 span ベース + AstArena + SliceMap + PooledBuffer によるホットパスのアロケーション最小化
2. **メンテナンス性**: partial class 分割、uniform ルールパターン、仕様ドキュメントとの対応
3. **NativeAOT**: リフレクションゼロ、`PublishAot=true` 実証済み
4. **データ志向**: AST は振る舞いのないデータコンテナ、式 AST はフラット struct 配列
5. **YAML 差し替え**: `IYamlStreamReader` アダプタ層で VYaml を封じ込め（`LintConfigYamlParser` の 1 箇所のみ漏出、低リスク）
6. **仕様準拠**: Parser spec 全ノード + Linter spec 全 53 ルールが実装済み

リリースに向けた最終調整に進んで問題ない。
