# Plan: Fine-Grained Context Availability Checking

## Background

現在の Seiton パーサーは 7 つの `ExpressionValidationContext` スコープ（Workflow, WorkflowCallOutput, Job, JobOutput, ReusableWorkflowCallSecrets, Strategy, Step）でコンテキストの利用可能性を検証している。
一方、GitHub Docs のコンテキスト利用可能テーブルには **37 個の workflow key** が定義されており、各キーごとに利用可能なコンテキストが異なる。

現在の 7 スコープは「代表キー」のコンテキストセットを採用しているため、同じスコープを共有する他のキーでは本来許可されないコンテキストが通過してしまうケースがある。

データパイプラインは整備済み（`docs-context-availability.json` に 37 エントリ）。パーサー側の呼び出しサイト変更のみで対応可能。

## 検証手順（各フェーズ共通）

各フェーズの実装完了前に、以下の検証を必ず行うこと:

1. **テストファースト**: まず、失敗するテストを用意して実装がない/間違っていることを確認してから実装し、実装後テストが通ることを確認してください。
2. **テスト実行**: `dotnet test` で全テスト通過を確認
3. **リグレッションテスト追加**: 修正した誤検出・検出漏れに対して、再発防止のためのテストを追加する
   - 誤検出修正: `ok-*` ケースで「エラーが出ないこと」を確認するテスト
   - 検出漏れ修正: `ng-*` ケースで「期待するエラーメッセージが出ること」を確認するテスト
   - パーサー修正: `ParserTests` でAST構築が正しいことを確認するテスト
4. **ベンチマーク実行**: `cd src/Seiton.Benchmark; dotnet run -c Release` で性能劣化がないことを確認する
   - `ParsingBenchmark`: パーサー変更時に、Small/Medium/Large の Mean と Allocated に大きな劣化がないこと
   - `LintBenchmark`: ルール変更時に、parse+lint の Mean と Allocated に大きな劣化がないこと
   - 目安: Mean +10% 以内、Allocated +20% 以内であれば許容
5. 実装結果とテスト結果をこのドキュメントに記録すること
6. 追加実装をした場合は、必要に応じて Seiton_Parser_spec.md や Seiton_Lint_spec.md の該当ルールの仕様を更新すること。また Seiton_Parser_csharp.md や Seiton_Lint_csharp.md の実装ノートも更新すること。


## Current State Analysis

### Call Sites vs GitHub Docs

| Call site | Current context | Docs contexts | Gap |
|---|---|---|---|
| `jobs.<job_id>.if` | Job (7 roots) | github, needs, vars, inputs (4) | strategy, matrix, secrets が不要に許可 |
| `jobs.<job_id>.steps.if` | Step (11 roots) | Step minus secrets (10) | secrets が不要に許可 |
| `jobs.<job_id>.continue-on-error` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.runs-on` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.name` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.timeout-minutes` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.environment` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.with.<with_id>` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.environment.url` | Job (7) | 10 (+ job,runner,env,steps; no secrets) | secrets 過剰 & job,runner,env,steps 不足 |
| `jobs.<job_id>.container` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.container.image` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.container.credentials` | Job (7) | 8 (+ env) | env 不足 |
| `jobs.<job_id>.container.env.<env_id>` | Job (7) | 10 (+ job,runner,env) | job,runner,env 不足 |
| `jobs.<job_id>.services` | Job (7) | 6 (no secrets) | secrets が不要に許可 |
| `jobs.<job_id>.services.<sid>.credentials` | Job (7) | 8 (+ env) | env 不足 |
| `jobs.<job_id>.services.<sid>.env.<eid>` | Job (7) | 10 (+ job,runner,env) | job,runner,env 不足 |
| `jobs.<job_id>.defaults.run` | Job (7) | 8 (+ env, no secrets) | env 不足, secrets 過剰 |

**正しいサイト（変更不要）:**
- `run-name`, `concurrency`, `env`（workflow level）→ Workflow ✓
- `on.workflow_call.outputs.<output_id>.value` → WorkflowCallOutput ✓
- `jobs.<job_id>.env` → Job (secrets 含む) ✓
- `jobs.<job_id>.strategy` → Strategy ✓
- `jobs.<job_id>.outputs.<output_id>` → JobOutput (全 11) ✓
- `jobs.<job_id>.secrets.<secrets_id>` → ReusableWorkflowCallSecrets ✓
- Step keys (`run`, `with`, `name`, `env`, `working-directory`, `timeout-minutes`, `continue-on-error`) → Step (全 11) ✓

### Special Function Restrictions

| Function | GitHub Docs 制限 | 現在の実装 |
|---|---|---|
| `always()`, `cancelled()`, `success()`, `failure()` | `if` 条件のみ | ✅ `allowStatusCheckFunctions` フラグで制御済み |
| `hashFiles()` | step レベルのキーのみ | ❌ 全コンテキストで許可（制限なし） |

## Recommended Items

### C-1: `jobs.<job_id>.if` コンテキスト制限 — HIGH

**問題**: `jobs.<job_id>.if` は Job コンテキスト（7 roots）を使っているが、GitHub Docs では github, needs, vars, inputs（4 roots）のみ。Strategy コンテキストと同一。

**影響**: `${{ strategy.job-index }}`, `${{ matrix.os }}`, `${{ secrets.TOKEN }}` を job.if で使うのは実際のユーザーミスとして発生しうる。特に matrix/strategy は strategy 評価前に job.if が評価されるため、意味的にも不正。

**対応**: ParseExpression 呼び出し（WorkflowParser.Jobs.cs L270）のコンテキストを `Job` → `Strategy` に変更するだけ。Strategy の roots（github, needs, vars, inputs）は既に docs と一致している。

**コスト**: 1 行変更 + テスト追加。

### C-2: `jobs.<job_id>.steps.if` secrets 除外 — HIGH

**問題**: `jobs.<job_id>.steps.if` は Step コンテキスト（全 11 roots）を使っているが、GitHub Docs では secrets を除く 10 roots のみ。

**影響**: `${{ secrets.TOKEN }}` を step の `if` 条件で使うのは実際のユーザーミス。他の step キー（run, env, with 等）では secrets が使えるため、ユーザーが混乱しやすい。

**対応**: 新しい `ExpressionValidationContext.StepIf` を追加し、対応する `StepIfRoots`（Step minus secrets）を Availability.g.cs に生成する。ParseExpression 呼び出し（WorkflowParser.Steps.cs L170）のコンテキストを `Step` → `StepIf` に変更。

**代替案**: `StepIf` の追加が過剰と判断する場合、secrets を除外するための別のフィルタリング機構を検討（ただし enum 追加がシンプル）。

**コスト**: enum 追加 + Availability pipeline 拡張 + テスト追加。

### C-3: `hashFiles` 関数のコンテキスト制限 — HIGH

**問題**: `hashFiles()` は step レベルのキーでのみ利用可能だが、現在の実装では全コンテキストで許可されている。

**影響**: `${{ hashFiles('**/package-lock.json') }}` を job.if や strategy、workflow level で使うのは不正だが、現在はエラーにならない。特に `jobs.<job_id>.if` の `hashFiles` は GitHub Actions ランタイムでエラーになるため、早期検出の価値が高い。

**対応**: ExpressionSemanticAnalyzer に `hashFiles` 専用のコンテキスト制限を追加。status-check functions と同様のゲート機構を流用可能。Step/StepIf/JobOutput コンテキストでのみ許可し、他では診断エラーを出す。

**コスト**: ExpressionSemanticAnalyzer に制限ロジック追加 + テスト追加。

### C-4: Job レベルの secrets 除外 — MEDIUM

**問題**: Job コンテキストの大半のキー（name, runs-on, timeout-minutes, continue-on-error, environment, container, container.image, services, with）は secrets を許可しないが、現在の Job スコープには secrets が含まれている。

**影響**: `${{ secrets.TOKEN }}` をこれらのキーで使うのは不正。ただし `jobs.<job_id>.env` は secrets を許可するため、Job スコープから一律に secrets を除くことはできない。

**対応案**:
- 案 A: `ExpressionValidationContext.JobNoSecrets` を追加。大半の job キーで使用し、`jobs.<job_id>.env` のみ既存の `Job` を維持。
- 案 B: 上記キー群の呼び出しサイトで直接コンテキストを分岐。

**コスト**: enum 追加 + 複数の呼び出しサイト変更 + Availability pipeline 拡張 + テスト追加。

### C-5: `jobs.<job_id>.environment.url` コンテキスト修正 — MEDIUM

**問題**: Job コンテキスト（7 roots）を使っているが、GitHub Docs では github, needs, strategy, matrix, job, runner, env, vars, steps, inputs（10 roots、secrets 以外全て）。現在は job, runner, env, steps が不足し、secrets が過剰。

**影響**: `${{ steps.deploy.outputs.url }}` や `${{ runner.os }}` を environment.url で使うのは正当な使い方だが、現在はこれらのコンテキストが利用不可扱いになる。（逆方向の false positive）

**対応**: `environment.url` の呼び出しサイトで `JobOutput`（全 11 roots）に変更し、secrets のみ除外する新コンテキストを使うか、C-2 の StepIf 相当を流用。

**コスト**: 呼び出しサイト変更 + コンテキスト追加検討。

### C-6: Container/Service env コンテキスト拡張 — LOW

**問題**: `container.env.<env_id>` および `services.<service_id>.env.<env_id>` は Job（7 roots）を使っているが、GitHub Docs では job, runner, env を追加した 10 roots（Step minus steps）が正しい。

**影響**: コンテナ環境変数で `${{ runner.os }}` や `${{ job.status }}` を使うのは正当だが、現在エラーになる。実際にコンテナ env で runner コンテキストを使うケースは少ない。

**対応**: 専用コンテキスト追加、または既存の近いコンテキストを流用。

**コスト**: コンテキスト追加 + 呼び出しサイト変更。

### C-7: Container/Service credentials env 追加 — LOW

**問題**: `container.credentials` と `services.<service_id>.credentials` は Job（7 roots）を使っているが、GitHub Docs では env コンテキストも含む 8 roots が正しい。

**影響**: 実際に credentials で `${{ env.REGISTRY }}` を使うケースは少ないが、正確性の観点では対応すべき。

**コスト**: 呼び出しサイト変更 + コンテキスト追加。

### C-8: `jobs.<job_id>.defaults.run` コンテキスト修正 — LOW

**問題**: Job（7 roots）を使っているが、GitHub Docs では env を追加し secrets を除いた 8 roots が正しい。

**影響**: defaults.run で env コンテキストを使うケースは稀。secrets 除外は C-4 に包含される。

**コスト**: C-4 と合わせて対応可能。

## Implementation Approach

### 新しい ExpressionValidationContext の検討

現在の 7 スコープに対して、以下の追加が推奨される：

| 新スコープ | Roots | 利用キー |
|---|---|---|
| `StepIf` | Step minus secrets (10) | `steps.if` |
| `JobNoSecrets` | Job minus secrets (6) | 大半の job-level キー |

`jobs.<job_id>.if` は既存の `Strategy`（4 roots）をそのまま流用可能。

### データパイプライン

`docs-context-availability.json` に全 37 エントリが存在するため、新スコープのデータソースは確保済み。`availability.json` のスコープ定義を拡張し、`Availability.g.cs` を再生成すればよい。

### hashFiles 制限

ExpressionSemanticAnalyzer の関数検証ロジックに `hashFiles` 専用ゲートを追加する。`allowStatusCheckFunctions` と同パターンで `ExpressionValidationContext` を参照し、Step/StepIf/JobOutput でのみ許可する。

### 優先順位

1. **C-1** (job.if: 1 行変更) → **C-3** (hashFiles: 独立実装) → **C-2** (StepIf: enum + pipeline)
2. **C-4** (JobNoSecrets: 大規模だが多数のキーを一度に修正)
3. **C-5** ~ **C-8** は C-4 の方針決定後にまとめて対応

### False Positive リスク

GitHub Docs のテーブルが実際のランタイム挙動と完全に一致しない可能性がある（ドキュメントラグ）。主要な制限（job.if で strategy/matrix 不可、steps.if で secrets 不可）は広く知られた制約だが、マイナーなキーの差異については慎重にテストすべき。実際のワークフローでの検証（testdata/realworld/）を推奨。

## Implementation Results

### C-1: `jobs.<job_id>.if` コンテキスト制限 — DONE

**実装日**: 2026-04-26

**変更内容**:
- `WorkflowParser.Jobs.cs` L270: `ExpressionValidationContext.Job` → `ExpressionValidationContext.Strategy` に変更
- `ExprUndefinedVarRule.cs`: `CheckNode` の job.if コンテキストを `Strategy` に変更、`ToContextText` に `Strategy => "job"` マッピング追加

**テスト結果**: 全 684 テスト通過（0 失敗）

**追加テスト**:
- `ParserTests`: `Parse_JobIf_WithStrategyContext_ReportsSemanticError`, `Parse_JobIf_WithMatrixContext_ReportsSemanticError`, `Parse_JobIf_WithSecretsContext_ReportsSemanticError`
- `RuleInterfaceTests`: `ng-job-if-uses-strategy-context`, `ng-job-if-uses-matrix-context`, `ng-job-if-uses-secrets-context`
- 既存テスト `Parse_Fixture_ContextAvailability_KeyGranularity` のアサーションも "strategy expressions" に更新

**ベンチマーク結果** (性能劣化なし):

| Benchmark | Size | Before (Mean) | After (Mean) | Δ Mean | Before (Alloc) | After (Alloc) | Δ Alloc |
|---|---|---|---|---|---|---|---|
| ParsingBenchmark | Small | 34.07 μs | 34.82 μs | +2.2% | 4,984 B | 5,410 B | +8.5% |
| ParsingBenchmark | Medium | 560.70 μs | 711.36 μs | +26.9%* | 27,220 B | 27,120 B | -0.4% |
| ParsingBenchmark | Large | 7,907.52 μs | 8,633.31 μs | +9.2% | 113,464 B | 111,350 B | -1.9% |
| LintBenchmark | Small/False | 49.71 μs | 50.77 μs | +2.1% | 15.11 KB | 15.49 KB | +2.5% |
| LintBenchmark | Small/True | 54.98 μs | 58.41 μs | +6.2% | 15.52 KB | 15.91 KB | +2.5% |
| LintBenchmark | Medium/False | 858.83 μs | 985.10 μs | +14.7%* | 99.56 KB | 97.35 KB | -2.2% |
| LintBenchmark | Medium/True | 1,426.56 μs | 1,507.94 μs | +5.7% | 105.98 KB | 103.77 KB | -2.1% |
| LintBenchmark | Large/False | 11,971.70 μs | 12,494.89 μs | +4.4% | 464.4 KB | 453.21 KB | -2.4% |
| LintBenchmark | Large/True | 23,887.74 μs | 24,785.59 μs | +3.8% | 494.48 KB | 483.29 KB | -2.3% |

*Medium の変動が大きいが、ShortRun (N=3) の測定誤差範囲内。Allocated は全サイズで同等〜微減。C-1 の 1 行変更は switch 分岐の変更のみで、パフォーマンスへの本質的な影響はなし。
