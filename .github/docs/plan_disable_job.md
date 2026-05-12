# Plan: Fix `disable-job` / Config Exclusion Job-Scope Bug

## 問題

`disable-job` インライン指示と config `exclusions[].jobs` のジョブスコープ抑制が、ジョブID行に報告される診断（例: `job-permissions-required`）のみに効き、ジョブ本体内の行に報告される診断（例: `matrix`, `runner-no-latest`, `runner-label`, `if-cond` 等）には効かない。

### 根本原因

`BuildJobScopes` ([LintEngine.cs L899](../../src/Seiton.Core/Linting/LintEngine.cs)) が `pair.Value.Range` を使用しているが、`Job.Range` はパーサーで `arena.GetStringRange(jobIdNode)` に設定されており、ジョブIDキー名だけの範囲（例: `build` → `L4:C5 - L4:C9`）を表す。ジョブマッピング全体の範囲ではない。

```
# seiton: disable-job build matrix    ← matrix ルールを build ジョブで抑制したい
on: push
jobs:
    build:                             ← Job.Range = L4:C5 - L4:C9 (ここだけ)
        strategy:
            matrix:
                os: []                 ← matrix 診断はここ (L7) → JobScope 外
        runs-on: ubuntu-latest         ← runner-no-latest 診断はここ (L8) → JobScope 外
```

`TryFindJobIdForLine` が `line >= scope.StartLine && line <= scope.EndLine` で判定するため、L4〜L4 の範囲しかマッチせず、L7 や L8 の診断はジョブに帰属できない。

### 影響範囲

- `# seiton: disable-job <job-id> <rule>` — ジョブ本体内の診断に効かない
- `exclusions[].jobs` — 同じ `TryFindJobIdForLine` を使うため同様に効かない
- `TryGetInlineSuppressionRecord` の `JobRuleSuppressions` パス — 同上

### 正しく動作するケース

- `job-permissions-required` — 診断がジョブID行に報告されるため一致する
- 他にジョブID行に報告される診断があれば同様に動作する

---

## 修正方針

### 方針: パーサー側で `Job.Range` をジョブマッピング全体の範囲に拡張

`Job.Range` を「ジョブキー名の範囲」から「ジョブマッピング全体の範囲」に変更する。

#### 変更箇所

**1. パーサー (`WorkflowParser.Jobs.cs`)**

`ParseJobNode` 内で MappingStart の位置は既に `mappingStart` 変数に取得済み（L164）。MappingEnd の `reader.Read()` 直前（L521-523）で `reader.CurrentStart` を取得し、ジョブID行（`jobIdMark`）から MappingEnd 行までの範囲を `Job.Range` に設定する。

```csharp
// 現在 (L594):
job.Range = arena.GetStringRange(jobIdNode);

// 修正後:
// mappingEndMark を MappingEnd 消費直前に取得
// ジョブID行を StartLine、MappingEnd行を EndLine として設定
job.Range = new TextRange(
    Start: arena.GetStringRange(jobIdNode).Start,
    Length: 0,
    StartLine: arena.GetStringRange(jobIdNode).StartLine,
    StartColumn: arena.GetStringRange(jobIdNode).StartColumn,
    EndLine: mappingEndMark.Line,
    EndColumn: mappingEndMark.Column);
```

早期リターンのケース (L157) は現行のまま `arena.GetStringRange(jobIdNode)` でよい（パースエラー時はジョブ本体がないため）。

**2. リンターの `BuildJobScopes` は変更不要**

`pair.Value.Range` が正しいジョブ全体範囲を返すようになれば、既存ロジックがそのまま動作する。

**3. `RuleBase.BuildJobLocation` への影響確認**

`BuildJobLocation` は `arena.GetStringRange(job.Id)` を使って診断位置を構築しているため、`Job.Range` 変更の影響を受けない。ただし `job.Range` を直接使っている箇所がないか確認が必要。

#### 影響分析

| 利用箇所 | 用途 | 影響 |
|---|---|---|
| `BuildJobScopes` (LintEngine) | ジョブスコープ行範囲の決定 | **修正の目的** — 正しく動作するようになる |
| `BuildJobLocation` (RuleBase) | 診断のロケーション | `job.Id` を使用、影響なし |
| `RuleBase` の `AddJobWarning(job, msg)` | job.Id 経由 | 影響なし |
| ルール内での `job.Range` 直接参照 | 要確認 | Range 全体が大きくなるが、ルールは通常個別フィールドの Range を使用 |

#### テスト計画

1. **Red (失敗テスト先行)**: 以下のテストを書き、現行コードで失敗することを確認
   - `DisableJob_Matrix_SuppressesDiagnostic` — `disable-job build matrix` がマトリクス診断を抑制
   - `DisableJob_RunnerNoLatest_SuppressesDiagnostic` — `disable-job build runner-no-latest` がラベル診断を抑制
   - `ConfigExclusion_Jobs_Matrix_SuppressesDiagnostic` — config `exclusions[].jobs` で matrix 診断を抑制
2. **Green (実装)**: パーサー修正を行い、テスト合格を確認
3. **リグレッション**: 既存テスト全件合格を確認
4. **ベンチマーク**: `CoreParsingBenchmark` と `CoreLintBenchmark` で Mean/Allocated +10% 以内を確認

#### リスク

- `Job.Range` のセマンティクス変更は、`Range` を直接使用しているコードに影響する可能性がある。ただし現時点のルール実装は個別フィールドの Range を使用しており、`Job.Range` を直接参照する箇所は限定的。
- `MappingEnd` の位置は YAML ライブラリが提供するもので、コメントや空行を含む場合がある。`TryFindJobIdForLine` は `>=` / `<=` 判定のため、ジョブ間の空行やコメントが隣接ジョブのスコープに含まれる可能性があるが、実用上は問題にならない（`disable-job` は指定ジョブのルールのみを対象とするため）。

---

## 代替案（不採用）

### 代替案 A: リンター側で `Workflow.Jobs` の順序からスコープを推定

`BuildJobScopes` でジョブを YAML 出現順にソートし、各ジョブの EndLine を次のジョブの StartLine - 1 に設定する（最後のジョブはファイル末尾まで）。

**不採用理由**: ジョブ間にコメントや空行がある場合の帰属が曖昧になる。パーサーが正しい範囲を提供すべき。

### 代替案 B: `Job` に `MappingRange` フィールドを追加

`Range` を変更せず、別フィールド `MappingRange` を追加してジョブ全体の範囲を保持する。

**不採用理由**: フィールド追加はメモリ増加（`TextRange` は 24 bytes）と API 複雑化を伴う。`Range` のセマンティクスを「ノード全体の範囲」に統一するほうが自然。

---

メモ

- permissionsルールは、permissionsのミニマムとして {} を差し込みます。しかし、実際のところactions/checkoutを使っているなら`contents: read`が必須になります。こう考えるとpermissions: {} を差し込むのはfalse-positiveを生む可能性があると思います。これをうまく改善できないでしょうか? (ワークフローレベルのpermissionsが {} なのは問題ないと思います。)

- imposter commitって、フォーク先のコミットを指している場合もひっかけられる? つまり、本来差しているリポジトリのコミットだけが信頼すべきだが、フォーク先のコミットも拾えるがために、悪意のあるユーザーがフォーク先でコミットを作成して、それを指すことができる可能性があるのではないかと心配しています。
- zizmorのconcurrency-limitsに相当するルールってありますか?

By default, GitHub Actions allows multiple instances of the same workflow to run concurrently, even when the new runs fully supersede the old. This can be a resource waste vector for attackers, particularly on billed runners. Separately, it can be a source of subtle race conditions when attempting to locate artifacts by workflow and job identifiers, rather than run IDs.

Remediation🔗
Include a concurrency setting in your workflow that sets the cancel-in-progress option either to true or to an expression that will be true in most cases. Specifying false would allow separate instances of the workflows to run concurrently, whereas true will imply that running jobs are cancelled as soon as the workflow is re-triggered.

Example

cancel-true.yml

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}
  cancel-in-progress: true
```
