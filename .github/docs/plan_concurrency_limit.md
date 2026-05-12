# concurrency-limits ルール実装計画

## 1. 概要

zizmor の `concurrency-limits` に相当する専用ルールを Seiton に追加する。
ワークフローに `concurrency` 設定がないか、`cancel-in-progress` が明示されていない場合に警告する。

- **Rule ID**: `concurrency-limits`
- **カテゴリ**: Supply Chain（リソース浪費・レース条件の防止）
- **デフォルト**: `enabled: true`（ローカル AST ルール）
- **Severity**: `warning`（デフォルト）
- **Auto-fix**: なし
- **ドキュメント種別**: Workflow のみ（action-metadata は対象外）

---

## 2. zizmor `concurrency-limits` の調査結果

### 2.1 zizmor の挙動

| シナリオ | zizmor 判定 |
|---|---|
| ワークフロー `concurrency` + `cancel-in-progress: true` | Pass |
| ワークフロー `concurrency` + `cancel-in-progress: false` | Pass（意図的設定として尊重） |
| ワークフロー `concurrency` + `cancel-in-progress: ${{ expr }}` | Pass |
| ワークフロー `concurrency: <bare string>`（cancel-in-progress なし） | **Finding** |
| ワークフロー `concurrency` なし → ジョブごとに分析 | ジョブ単位で Finding |
| reusable-only ワークフロー（`on: workflow_call` のみ） | **Skip**（呼び出し側が管理すべき） |
| Reusable workflow call ジョブ（`uses: ./.github/...`） | Skip（ジョブレベル分析でスキップ） |

### 2.2 zizmor の Severity/Confidence

- Severity: `Low`
- Confidence: `High`
- Persona: `Pedantic`（`--pedantic` 必須）

### 2.3 Seiton での設計方針

zizmor は Pedantic（オプトイン）だが、Seiton では **デフォルト有効** とする。

理由:
- `concurrency` の未設定は、課金ランナーでのリソース浪費や CI レースコンディションに直結する
- `job-timeout-minutes-required` と同様、ベストプラクティスの強制として有用
- 不要なら `enabled: false` で無効化可能

### 2.4 zizmor との差異

| 項目 | zizmor | Seiton（計画） |
|---|---|---|
| デフォルト | Pedantic（opt-in） | 有効（opt-out） |
| `cancel-in-progress: false` | Pass（意図的） | Pass（同じ方針） |
| ジョブレベルの Finding 集約 | 2 つの Finding に集約 | ジョブ単位で個別 diagnostic |
| diagnostic 位置 | ワークフロー `on:` | ワークフロー `concurrency` 位置 / ジョブ ID 位置 |

---

## 3. 実装設計

### 3.1 AST 利用

既存の AST で十分。追加のパーサー変更は不要。

```csharp
// Workflow レベル
Workflow.Concurrency  // Concurrency? — null = 未設定

// Concurrency ノード
Concurrency.Group              // StringNodeId
Concurrency.CancelInProgress   // BoolNodeId — HasValue = false で未設定

// Job レベル
Job.Concurrency       // Concurrency? — null = 未設定
Job.WorkflowCall      // WorkflowCall? — null でない = reusable call ジョブ（スキップ）

// Event 判定
workflow.On[i] is WorkflowCallEvent  // reusable-only 判定
```

### 3.2 ルールロジック

```
VisitWorkflowPre(workflow):
  // reusable-only ワークフロー判定
  if all events are WorkflowCallEvent:
    _isReusableOnly = true
    return

  if workflow.Concurrency is not null:
    // ワークフローレベルに concurrency がある
    if !workflow.Concurrency.CancelInProgress.HasValue:
      // cancel-in-progress が未設定
      emit warning "workflow concurrency is missing 'cancel-in-progress' setting"
    // cancel-in-progress がある場合（true/false/expression）→ Pass
    _hasWorkflowConcurrency = true
  else:
    _hasWorkflowConcurrency = false

VisitJobPre(job):
  if _isReusableOnly: return
  if _hasWorkflowConcurrency: return
  if job.WorkflowCall is not null: return  // reusable workflow call ジョブはスキップ

  if job.Concurrency is null:
    emit warning "job '{jobId}' does not declare concurrency settings; consider adding workflow-level concurrency"
  else if !job.Concurrency.CancelInProgress.HasValue:
    emit warning "job '{jobId}' concurrency is missing 'cancel-in-progress' setting"
  // cancel-in-progress がある場合（true/false/expression）→ Pass
```

### 3.3 diagnostic メッセージ

| シナリオ | メッセージ |
|---|---|
| ワークフロー concurrency に cancel-in-progress なし | `workflow concurrency is missing 'cancel-in-progress' setting` |
| ジョブに concurrency なし | `job '{jobId}' does not declare concurrency settings; consider adding workflow-level concurrency` |
| ジョブ concurrency に cancel-in-progress なし | `job '{jobId}' concurrency is missing 'cancel-in-progress' setting` |

### 3.4 diagnostic 位置

| シナリオ | 位置 |
|---|---|
| ワークフロー concurrency に cancel-in-progress なし | `workflow.Concurrency.Range`（`concurrency:` キー行） |
| ジョブに concurrency なし | `BuildJobLocation(job)`（ジョブ ID 行） |
| ジョブ concurrency に cancel-in-progress なし | `job.Concurrency.Range`（ジョブの `concurrency:` キー行） |

---

## 4. 変更対象ファイル一覧

### 4.1 プロダクションコード

| ファイル | 変更内容 |
|---|---|
| `src/Seiton.Core/Linting/RuleId.cs` | `ConcurrencyLimits` 追加 |
| `src/Seiton.Core/Linting/RuleIdExtensions.cs` | `RuleId.ConcurrencyLimits => "concurrency-limits"` 追加 |
| `src/Seiton.Core/Linting/Rules/ConcurrencyLimitsRule.cs` | **新規作成** — ルール実装 |
| `src/Seiton.Core/Linting/RuleCatalog.cs` | `DefaultRuleFactories` にエントリ追加（priority 55） |

### 4.2 テストコード

| ファイル | 変更内容 |
|---|---|
| `tests/Seiton.Core.Tests/RuleInterfaceTests.cs` | `RuleRegression_ConcurrencyLimitsRule_TableDriven` テスト追加 |

### 4.3 ドキュメント・仕様

| ファイル | 変更内容 |
|---|---|
| `.github/docs/Seiton_Linter_spec.md` | §4.4 に `concurrency-limits` ルール追加 |
| `.github/docs/Seiton-feature-matrix.md` | `concurrency-limits` を `✅` に更新 |
| `docs/checks.md` | ルールドキュメント追加 |
| `README.md` | チェック一覧テーブルに追加 |

---

## 5. テスト計画

### 5.1 Red-Green テストケース

`RuleRegression_ConcurrencyLimitsRule_TableDriven` で以下のケースをカバーする。

#### OK ケース（diagnostic 0 件）

| ケース名 | 説明 |
|---|---|
| `ok-workflow-concurrency-with-cancel-true` | ワークフローレベル `concurrency` + `cancel-in-progress: true` |
| `ok-workflow-concurrency-with-cancel-false` | ワークフローレベル `concurrency` + `cancel-in-progress: false`（意図的設定） |
| `ok-workflow-concurrency-with-cancel-expression` | ワークフローレベル `concurrency` + `cancel-in-progress: ${{ ... }}` |
| `ok-job-concurrency-with-cancel-true` | ジョブレベル `concurrency` + `cancel-in-progress: true` |
| `ok-job-concurrency-with-cancel-false` | ジョブレベル `concurrency` + `cancel-in-progress: false` |
| `ok-reusable-only-workflow` | `on: workflow_call` のみのワークフロー（スキップ） |
| `ok-reusable-workflow-call-job` | ジョブが `uses:` で reusable workflow を呼ぶ場合（スキップ） |

#### NG ケース（diagnostic 1 件以上）

| ケース名 | 説明 | 期待メッセージ |
|---|---|---|
| `ng-workflow-concurrency-bare` | ワークフロー `concurrency: group-name`（cancel-in-progress なし） | `missing 'cancel-in-progress'` |
| `ng-no-concurrency-anywhere` | concurrency 設定がどこにもない | `does not declare concurrency` |
| `ng-job-concurrency-bare` | ジョブ `concurrency: group-name`（cancel-in-progress なし） | `missing 'cancel-in-progress'` |
| `ng-mixed-jobs` | 一部ジョブに concurrency あり/なし | 両方のメッセージ |

### 5.2 エッジケース

| ケース名 | 説明 |
|---|---|
| `ok-workflow-call-mixed-triggers` | `on: [push, workflow_call]` — reusable-only ではないので通常チェック |
| `ok-workflow-concurrency-covers-all-jobs` | ワークフローレベル concurrency がある場合、ジョブの concurrency 未設定は OK |

### 5.3 既存テスト確認

`dotnet test` で全テストが通ることを確認する。新ルール追加は既存テストに影響しないはず（`RuleCatalog` に追加されるが、既存テストは個別ルールインスタンスを渡しているため）。

ただし `RuleCatalog_AllRuleIds_HaveCanonicalMapping` 等のカタログ整合性テストが存在する場合、`RuleId.ConcurrencyLimits` の追加で更新が必要になる可能性がある。

---

## 6. パフォーマンス検証計画

### 6.1 ルール実装のパフォーマンス特性

このルールは以下の理由でパフォーマンスへの影響が極めて小さい。

- **アロケーションなし（成功パス）**: `HasValue` チェック（`int > 0`）と null チェックのみ。`GetScalarString()` は NG メッセージ生成時（`Decode()`）のみ使用
- **計算量**: `VisitWorkflowPre` で O(E) イベント走査 + O(1) concurrency チェック、`VisitJobPre` で O(1) — 全体で O(E + J)
- **状態**: `bool` フィールド 2 つ（`_isReusableOnly`, `_hasWorkflowConcurrency`）のみ

### 6.2 ベンチマーク実行手順

実装前後で以下を比較する。

```shell
# 1. main ブランチでベースライン取得
git stash
cd src/Seiton.Benchmark
dotnet run -c Release
# results/ のレポートを保存

# 2. 実装ブランチでベンチマーク実行
git stash pop
cd src/Seiton.Benchmark
dotnet run -c Release
# 比較
```

### 6.3 許容基準

| メトリクス | 許容閾値 |
|---|---|
| `CoreLintBenchmark` Mean | +3% 以内 |
| `CoreLintBenchmark` Allocated | +1% 以内 |

ルールは `bool` フィールド 2 つのみの追加なので、アロケーション増加は実質ゼロの見込み。Mean への影響も `null` チェック数回分であり無視できるレベル。

---

## 7. 実装手順（Test-First）

### Step 1: テスト作成（Red）

1. `RuleId.ConcurrencyLimits` を `RuleId.cs` に追加
2. `RuleIdExtensions.cs` に `"concurrency-limits"` マッピング追加
3. `ConcurrencyLimitsRule.cs` をスタブ（空の `VisitWorkflowPre`/`VisitJobPre`）で作成
4. `RuleCatalog.cs` にエントリ追加（priority 55）
5. `RuleInterfaceTests.cs` にテストケース追加
6. テスト実行 → NG ケースが失敗することを確認

### Step 2: 実装（Green）

1. `ConcurrencyLimitsRule.cs` にロジック実装
2. テスト実行 → 全ケース pass を確認

### Step 3: 全テスト実行

```shell
dotnet test
```

### Step 4: ベンチマーク実行

```shell
cd src/Seiton.Benchmark
dotnet run -c Release
```

### Step 5: ドキュメント更新

1. `Seiton_Linter_spec.md` §4.4 テーブルに追加
2. `Seiton-feature-matrix.md` の `concurrency-limits` を `✅` に更新
3. `docs/checks.md` にルール説明追加
4. `README.md` チェック一覧テーブルに追加

---

## 8. `ConcurrencyLimitsRule.cs` 実装スケルトン

```csharp
using Seiton.Core.Parsing.Ast;
using Seiton.Core.Parsing;

namespace Seiton.Core.Linting.Rules;

/// <summary>Warns when workflows or jobs lack concurrency limits with cancel-in-progress.</summary>
public sealed class ConcurrencyLimitsRule() : RuleBase(RuleId.ConcurrencyLimits)
{
    private bool _isReusableOnly;
    private bool _hasWorkflowConcurrency;

    public override string Name => "Concurrency Limits Rule";

    public override bool SupportsDocumentKind(DocumentKind documentKind) => documentKind == DocumentKind.Workflow;

    public override void VisitWorkflowPre(Workflow workflow)
    {
        base.VisitWorkflowPre(workflow);
        _isReusableOnly = false;
        _hasWorkflowConcurrency = false;

        // reusable-only ワークフロー判定
        if (IsReusableOnlyWorkflow(workflow))
        {
            _isReusableOnly = true;
            return;
        }

        if (workflow.Concurrency is { } concurrency)
        {
            _hasWorkflowConcurrency = true;

            if (!concurrency.CancelInProgress.HasValue)
            {
                AddWarning("workflow concurrency is missing 'cancel-in-progress' setting", concurrency.Range);
            }
        }
    }

    public override void VisitJobPre(Job job)
    {
        if (_isReusableOnly || _hasWorkflowConcurrency)
        {
            return;
        }

        // reusable workflow call ジョブはスキップ
        if (job.WorkflowCall is not null)
        {
            return;
        }

        if (job.Concurrency is null)
        {
            var jobId = Decode(Arena.GetStringSlice(job.Id));
            AddJobWarning(job, $"job '{jobId}' does not declare concurrency settings; consider adding workflow-level concurrency");
        }
        else if (!job.Concurrency.CancelInProgress.HasValue)
        {
            var jobId = Decode(Arena.GetStringSlice(job.Id));
            AddWarning($"job '{jobId}' concurrency is missing 'cancel-in-progress' setting", job.Concurrency.Range);
        }
    }

    private static bool IsReusableOnlyWorkflow(Workflow workflow)
    {
        if (workflow.On.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < workflow.On.Count; i++)
        {
            if (workflow.On[i] is not WorkflowCallEvent)
            {
                return false;
            }
        }

        return true;
    }
}
```

### パフォーマンス確認チェックリスト

- [x] `GetScalarString()` / `Decode()` は NG パス（diagnostic 生成時）のみ使用
- [x] 成功パスは `null` チェック + `BoolNodeId.HasValue`（`int > 0`）のみ
- [x] 新規アロケーション: 成功パスではゼロ
- [x] `List<T>`, `Dictionary<TKey, TValue>`, LINQ, regex 不使用
- [x] UTF-8 span 比較は不要（AST ノードの型チェックで十分）
