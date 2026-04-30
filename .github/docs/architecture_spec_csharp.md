# seiton アーキテクチャ検討（C# 採用案）

## 1. 結論

本プロジェクトを **C# + ConsoleAppFramework + VYaml** で開始する案は、十分に成立する。

特に次の条件を満たすなら、Go より C# を採用する合理性が高い。

1. 開発チームが .NET に習熟している。
2. CLI 体験を ConsoleAppFramework で統一したい。
3. YAML 位置情報（行・列）を VYaml で扱えることを重視する。
4. 将来 LSP や IDE 連携を .NET 資産で展開したい。

ただし、設計思想は Go 案と同じく **actionlint 型のハイブリッド** を維持する。

1. 構文検証は hand-written parser を主軸にする。
2. 変化しやすい GitHub 由来データは生成物として管理する。
3. セマンティクスとポリシーは Visitor 型の Rule Engine で評価する。

## 2. C# / Go / Rust の再比較（C# 採用前提）

### 2.1 比較表

| 観点 | C#（ConsoleAppFramework + VYaml） | Go（actionlint 型） | Rust（zizmor 型含む） |
|---|---|---|---|
| CLI 開発体験 | 非常に良い | 非常に良い | 良い |
| YAML 行/列取得 | 良い（VYaml で対応可能） | 良い（yaml.v3 Node） | 中程度（実装依存） |
| 手実装パーサの前例 | 中程度 | 非常に強い | 中程度 |
| 初期実装速度 | 非常に良い（チーム依存） | 非常に良い | 中程度 |
| セキュリティ静的解析の拡張 | 良い | 良い | 非常に高い |
| パフォーマンス上限 | 良い | 良い | 非常に高い |
| 学習コスト | 低い〜中程度 | 低い | 高い |
| エコシステム適合（この領域） | 良い | 非常に強い | 強い |

### 2.2 C# を採用する意思決定条件

C# 採用を推奨するのは以下の場合。

1. チームに .NET の深い運用知見がある。
2. CI/CD 以外の社内ツールも .NET で統一したい。
3. NuGet 配布、単一ファイル発行、AOT 含む .NET 配布戦略を活かしたい。
4. 開発速度を最優先し、Rust の学習コストを避けたい。

逆に、次を最重視するなら Go/Rust も再検討する。

1. actionlint 互換に近い実装を最短で作る（Go 有利）。
2. 高度なデータフロー解析や超高性能化を最優先する（Rust 有利）。

## 3. 設計原則（C# 版）

### 3.1 基本方針

本プロジェクトは、単一 JSON Schema で GitHub Actions の正しさを定義しきる方式を採らない。

採用方針は次のとおり。

1. **構文スキーマ**: YAML ノードを直接走査する hand-written parser で検証。
2. **意味スキーマ**: AST に対するルール群で文脈依存制約を検証。
3. **可変外部データ**: GitHub Docs / SchemaStore 由来データを生成・更新する。

### 3.2 4 層構成

1. Source Layer: ソース本文、行テーブル、Span 管理。
2. Parse/Model Layer: YAML Node -> Typed AST + syntax diagnostics。
3. Rule Layer: Visitor で semantic/policy diagnostics。
4. Diagnostic Layer: plain / json / sarif 出力。

## 4. C# 実装での技術選定

### 4.1 CLI

- 採用: **ConsoleAppFramework**
- 役割: サブコマンド、オプション、終了コード、ヘルプ生成

推奨サブコマンド:

1. `seiton lint <input...>`
2. `seiton schema sync`
3. `seiton data sync`
4. `seiton version`

### 4.2 YAML 解析

- 採用: **VYaml**
- 方針: POCO 直デシリアライズ中心ではなく、**Node/Token ベースで AST を自前構築**

重要点:

1. key/value 両方の位置を保持する。
2. 未知キー診断を厳密化する。
3. 複数エラー回復を前提にする。

### 4.3 式解析（`${{ }}`）

初期は自前実装を推奨。

1. fenced expression 抽出
2. 最小文法（property access, call, comparison, logical ops）
3. 後続で availability/type/taint を追加

必要なら将来パーサライブラリ（例: Sprache/Superpower 系）を検討するが、初期は依存を増やしすぎない。

### 4.4 JSON Schema の位置づけ

- SchemaStore の workflow/action/dependabot を vendoring する。
- ただし用途は「補助的検証」と「追随監視」。
- 主判定ロジックは hand-written parser + rules に置く。

### 4.5 generated data の定義

ここでいう generated data は、Roslyn Source Generator を前提にする意味ではない。意味としては、**GitHub Docs や SchemaStore などの外部仕様から取ってきた可変データを、lint 実行前に固定化しておく** ことである。

C# では初期方針として、Source Generator よりも **更新専用コマンドで `.g.cs` または vendored JSON を生成して commit する** 方式を推奨する。

具体的には以下のどちらかを採る。

1. `Seiton.Update` で外部データを取得し `.g.cs` や JSON を更新する。
2. MSBuild とは独立した更新コマンドを CI/手元で明示実行する。

Source Generator は必須ではなく、初期段階ではむしろ不要である。理由は、外部 HTTP 取得やスクレイピングをビルドに混ぜず、生成差分を PR でレビューしやすくするためである。

生成対象は次のように分ける。

#### A. `.g.cs` に生成するもの

1. webhook event/activity types 一覧。
2. expression context availability table。
3. special function names。
4. popular actions の input/output metadata。

これらはランタイムで JSON を読むより、定数テーブルや `FrozenDictionary` 相当の初期化コードへ固定化したほうがよい。

#### B. JSON のまま vendoring するもの

1. SchemaStore の workflow/action/dependabot schema。

これは parser 本体の主ロジックではなく補助検証用途なので、C# コードへ無理に変換しない。

#### C. 生成しないもの

1. parser の構文制約。
2. AST モデル。
3. semantic/policy rules。

ここは仕様差分の意味解釈が必要であり、人が保守するコードとして残す。

## 5. Go パターンを C# に移植した設計

### 5.1 対応関係

| Go パターン | C# での実装 |
|---|---|
| `yaml.Node` を直接走査 | VYaml Node/Event を走査 |
| `parse.go` で AST 組み立て + 構文検証 | `WorkflowParser` で AST 生成 + SyntaxRule 併合 |
| Visitor で Rule 実行 | `WorkflowVisitor` + `IRule` 群 |
| generated data (`availability.go` 等) | `Generated/*.g.cs` or JSON 埋め込み |
| 位置情報付きエラー | `Span` + `Diagnostic` モデル |

### 5.2 期待する効果

1. actionlint の実証済み思想を活用できる。
2. C# の開発生産性を維持できる。
3. 将来の規模拡大時も責務分離が保てる。

## 6. 推奨プロジェクト構成（.NET）

```text
src/
  Seiton/
    Program.cs
    Commands/
      LintCommand.cs
      SchemaCommand.cs
      DataCommand.cs

  Seiton.Core/
    Source/
      SourceText.cs
      SourceIndex.cs
      Span.cs

    Diagnostics/
      Diagnostic.cs
      Severity.cs
      Renderers/
        PlainRenderer.cs
        JsonRenderer.cs
        SarifRenderer.cs

    Yaml/
      YamlNodeExtensions.cs
      YamlReaders.cs

    Gha/Ast/
      Workflow.cs
      Job.cs
      Step.cs
      EventSpec.cs
      ExprNode.cs

    Gha/Parsing/
      WorkflowParser.cs
      ParseContext.cs
      ParseErrors.cs

    Expr/
      Lexer.cs
      Parser.cs
      Ast.cs
      Semantics.cs

    Rules/
      IRule.cs
      RuleContext.cs
      RuleRegistry.cs
      WorkflowVisitor.cs
      Syntax/
      Semantics/
      Policy/

    Config/
      SeitonConfig.cs
      ConfigLoader.cs

    Schema/
      Vendored/
        github-workflow.json
        github-action.json
        dependabot-2.0.json
      SchemaValidator.cs

    Generated/
      Availability.g.cs
      WebhookTypes.g.cs
      PopularActions.g.cs

  Seiton.Update/
    SchemaSync.cs
    AvailabilitySync.cs
    WebhookSync.cs
    PopularActionsSync.cs

tests/
  Seiton.Core.Tests/
    Parsing/
    Expr/
    Rules/
  Seiton.IntegrationTests/
    Fixtures/
```

### 6.1 Assembly 分割の意図

1. `Seiton.Cli`: 入出力とコマンド定義のみ。
2. `Seiton.Core`: ドメインロジック本体。
3. `Seiton.Update`: 外部データ更新スクリプト責務。

この分割により、lint 実行系と更新系の依存を分離できる。

## 7. 位置情報設計（行・列）

### 7.1 Diagnostic モデル

```csharp
public readonly record struct Span(
    int Line,
    int Column,
    int EndLine,
    int EndColumn);

public sealed record Diagnostic(
    string RuleId,
    Severity Severity,
    string Message,
    string FilePath,
    Span Primary,
    IReadOnlyList<RelatedLocation> Related,
    string? Help,
    Fix? Fix);
```

### 7.2 指す位置のルール

1. 未知キー: key span
2. 型不一致: value span
3. 排他違反: 主因を primary、もう一方を related
4. 伝播系ルール: sink 側を primary、source 側を related

### 7.3 End 位置

初期版は「開始位置の正確さ」を優先する。

1. End が不正確になるより、開始位置だけ正しく出す。
2. 後続で SourceIndex から End 推定を強化する。

## 8. ルール定義方針

### 8.1 ルールはコード実装

DSL 主体にせず、C# コードで実装する。

1. ルールは `IRule` を実装。
2. 設定ファイルは enable/disable、severity 上書き、ignore のみ。
3. ルールパラメータ（deny list 等）だけ設定で受ける。

### 8.2 ルールインターフェース例

```csharp
public interface IRule
{
    string Id { get; }
    RuleMetadata Metadata { get; }
}

public interface IWorkflowRule : IRule
{
    void VisitWorkflow(RuleContext context, Workflow workflow);
}

public interface IJobRule : IRule
{
    void VisitJob(RuleContext context, Workflow workflow, Job job);
}

public interface IStepRule : IRule
{
    void VisitStep(RuleContext context, Workflow workflow, Job job, Step step);
}
```

## 9. スキーマとデータ更新戦略

### 9.1 自動更新対象

1. SchemaStore JSON（workflow/action/dependabot）
2. webhook event/activity type
3. context availability
4. popular actions metadata

### 9.2 CI フロー

```text
schedule + workflow_dispatch
  -> dotnet run --project src/Seiton.Update -- schema sync
  -> dotnet run --project src/Seiton.Update -- data sync
  -> dotnet test
  -> 変更があれば自動 PR
```

この更新フローは Source Generator 実行ではなく、**外部データを取得して生成物を更新する明示的なメンテナンス処理** として扱う。

### 9.3 手動更新対象

1. parser の構文制約
2. AST モデル
3. semantic/policy ルール

ここは仕様差分の意味解釈が必要なため、人手で更新する。

## 10. 出力フォーマット

初期から次を提供する。

1. plain（人間向け）
2. json（機械連携）
3. sarif（GitHub Code Scanning）
4. actions (GitHubActions の group対応ログ)

終了コード方針:

1. `0`: 閾値以上の finding なし
2. `1`: 閾値以上の finding あり
3. `2`: ツール内部エラー

## 11. パフォーマンス・アロケーション方針

### 11.1 結論

本プロジェクトでは、**パフォーマンスとメモリアロケーションを最重要要件** として扱うべきである。

ただし、C# でもエンドツーエンドで「完全 0 アロケーション」を保証するのは現実的ではない。特に YAML パース、文字列生成、診断出力、例外生成を完全にゼロにすることは難しい。

したがって目標は次のように置く。

1. **ホットパスでのアロケーションを極小化する**。
2. parse 後の rule evaluation と lookup をほぼゼロに近づける。
3. GC 発生回数と総割り当て量をベンチマークで継続監視する。

### 11.2 C# で可能なこと

C# は .NET 8 以降の API を前提にすれば、かなり低アロケーションな実装は可能である。

1. `ReadOnlySpan<char>` / `ReadOnlySpan<byte>` を活用する。
2. `ArrayPool<T>` や `ObjectPool<T>` を使ってバッファを再利用する。
3. `ValueStringBuilder` 相当で出力組み立てを最適化する。
4. 生成済み定数表や `FrozenDictionary` を lookup に使う。
5. Generic + struct 中心の設計で boxing を避ける。

ただし、VYaml の内部動作と AST 構築を含めた完全 0 アロケーションは難しい。これは Go でも同様であり、**C# で目指すべきは Go と同じく「管理可能な低アロケーション」** である。

### 11.3 C# での実務的な目標

1. YAML 解析は低アロケーション化するが、ゼロは前提にしない。
2. ルール評価、表データ lookup、式解析のホットパスを極小アロケーションに抑える。
3. 出力レンダリング時だけ必要最小限の文字列生成を行う。
4. Go と同じ比較条件でベンチマークし、採用判断を実測で行う。

### 11.4 設計上の禁止事項

1. LINQ による列挙と中間配列生成をホットパスで多用すること。
2. `string.Split`、`Regex`、boxing を多用すること。
3. ルール評価のたびに JSON や YAML を再パースすること。
4. `object` や `Dictionary<string, object>` を中心に動的処理すること。

## 12. 実装フェーズ

### Phase 1（最小成立）

1. ConsoleAppFramework で `lint` コマンド
2. VYaml で Node 走査
3. Workflow parser 最小版
4. basic diagnostics
5. syntax rules 最小セット

### Phase 2（実用 lint）

1. expression parser 最小版
2. security / best-practice rules
3. config 読み込み
4. json / sarif 出力

### Phase 3（追随性強化）

1. schema/data 自動同期
2. generated データ運用
3. 回帰テスト拡充

### Phase 4（高度化）

1. autofix
2. shell script 解析
3. taint 解析
4. LSP 対応

## 13. 最終提案

C# を採用する場合の最適解は、次の組み合わせ。

1. **CLI: ConsoleAppFramework**
2. **YAML: VYaml（Node ベース解析）**
3. **Parser: hand-written（actionlint パターン移植）**
4. **Rules: Visitor + typed AST**
5. **Schema/Data: 自動同期 + 生成物管理**
6. **Diagnostics: Span 中心の統一モデル**

これにより、Go 案で重視した「構文を自分たちで制御する」強みを維持しつつ、C# の開発生産性と .NET エコシステムを活かした実装が可能になる。
