# Impostor Commit ルール改善計画

## 1. 問題の概要

### 現在の挙動

`impostor-commit` ルールは、SHA ピンされた `uses:` 参照が本来のリポジトリに属するコミットかどうかを検証するオンラインルールである。

現在の実装 (`ActionRefResolver.CommitExistsWithFallbackAsync`) は以下の API を使用:

```
GET /repos/{owner}/{repo}/commits/{sha}
```

この API が 200 を返せば `CommitExists = true` とし、`ImpostorCommitRule.EvaluateTarget` は「コミットが存在する」としてスキップする。

### 脆弱性

GitHub はリポジトリとそのフォークを「ネットワーク」として共有オブジェクトストレージで管理する。このため:

1. 攻撃者が `actions/checkout` をフォーク
2. フォーク上で悪意あるコミット `abc123...` を作成
3. 被害者のワークフローで `uses: actions/checkout@abc123...` と記述
4. `GET /repos/actions/checkout/commits/abc123...` は **200 OK** を返す（フォークのコミットも親のオブジェクトストレージから見える）
5. `CommitExists = true` → ルールはエラーを出さない

これは zizmor が文書化している攻撃手法と同一: フォーク由来のコミットが親リポジトリの `owner/repo` スラッグ経由でアクセス可能になり、impostor commit として悪用される。

### 参考: zizmor の impostor-commit 説明

> GitHub represents a repository and its forks as a "network" of commits. This results in ambiguity about where a commit comes from: a commit that exists only in a fork can be referenced via its parent's `owner/repo` slug, and vice versa.

歴史的な例: `github/dmca@565ece4` — `github/dmca` 上に存在するように見えるが、実際にはフォーク上のコミット（コミット作者も偽装されている）。

## 2. 修正方針

### 検出戦略: `branches-where-head` API を使った到達可能性検証

コミットが存在する (`CommitExists = true`) 場合でも、そのコミットがリポジトリの正当なブランチから到達可能かを追加検証する。

**使用する API:**

```
GET /repos/{owner}/{repo}/commits/{commit_sha}/branches-where-head
```

- レスポンス: そのコミットが HEAD であるブランチのリスト
- 空配列 `[]` → そのコミットはリポジトリのどのブランチの HEAD でもない → impostor の可能性が高い

**判定ロジック:**

```
CommitExists == true かつ IsTaggedCommit == false かつ IsReachable == false
  → impostor commit と判定
```

### なぜ `branches-where-head` か

| API | 利点 | 欠点 |
|-----|------|------|
| `GET /commits/{sha}` (現行) | 1 リクエスト | フォークコミットも見える。到達可能性を検証できない |
| `GET /commits/{sha}/branches-where-head` | 1 追加リクエスト。ブランチ HEAD のみ返す。フォーク由来は空 | HEAD 以外のコミット（古いコミット）も空を返す |
| `GET /compare/{base}...{sha}` | 到達可能性を厳密に判定可能 | レスポンスが重い。デフォルトブランチ名の取得に追加リクエストが必要 |

**選定: `branches-where-head` + 既存の `IsTaggedCommit` を組み合わせる。**

- ブランチ HEAD でもタグ付きでもないコミットは impostor の可能性が非常に高い
- 正当なコミット（タグ付きリリース）はすでに `IsTaggedCommit` で検出される
- API レスポンスが軽量（ブランチ名のリストのみ）
- 追加 API コールは SHA ピンされたコミットに対してのみ発生（コミットが存在しない場合はスキップ）

### 判定フロー（修正後）

```
ResolveCommitAsync(owner, repo, sha):
  1. CommitExists = CommitExistsWithFallbackAsync(...)
  2. if !CommitExists → return { CommitExists: false, ... }  // 現行通り
  3. IsTaggedCommit = IsTaggedCommitWithFallbackAsync(...)
  4. if IsTaggedCommit → return { CommitExists: true, IsTaggedCommit: true, IsReachable: true }
  5. IsReachable = IsBranchHeadWithFallbackAsync(...)  // 新規
  6. return { CommitExists: true, IsTaggedCommit: false, IsReachable }
```

`ImpostorCommitRule.EvaluateTarget` の修正後ロジック:

```
if resolution is null || !target.IsCommitSha → skip
if !resolution.CommitExists → error (現行通り: コミット自体が存在しない)
if resolution.CommitExists && !resolution.IsReachable → error (新規: フォーク由来の可能性)
```

### `IsReachable` の定義

- `IsTaggedCommit == true` → `IsReachable = true`（タグから到達可能）
- `branches-where-head` が 1 件以上 → `IsReachable = true`（ブランチ HEAD から到達可能）
- それ以外 → `IsReachable = false`

### 制約と既知の限界

1. **古い正当コミット**: ブランチ HEAD でもタグ付きでもないが正当なコミット（例: 過去のリリースコミットでタグが 100 件以降にある場合）は false positive になる。しかし、SHA ピンされたコミットは通常タグと関連付けられるため、実運用上は問題になりにくい。
2. **タグ検査の 100 件制限**: `IsTaggedCommitWithFallbackAsync` は `per_page=100` で最初のページのみ取得。タグが 100 件を超えるリポジトリでは見逃しの可能性がある。この問題は本計画のスコープ外とするが、将来的にページネーション対応を検討。
3. **`branches-where-head` は HEAD のみ**: ブランチの途中にあるコミット（HEAD ではない）は検出されない。これは stale-action-refs と組み合わせてカバーされる想定。

## 3. 実装計画

### Step 1: `ActionRefResolution` に `IsReachable` フィールド追加

**変更ファイル:** `src/Seiton.Core/Linting/OnlineAudit/ActionRefResolver.cs`

```csharp
public readonly record struct ActionRefResolution(
    bool CommitExists,
    bool HasBranchReference,
    bool HasTagReference,
    bool IsTaggedCommit,
    bool IsReachable);  // 新規追加
```

- `IsReachable` は `IsTaggedCommit || branches-where-head が非空` の場合 `true`
- 既存の `ActionRefResolution` の生成箇所すべてで `IsReachable` を設定

### Step 2: `IsBranchHeadWithFallbackAsync` メソッド追加

**変更ファイル:** `src/Seiton.Core/Linting/OnlineAudit/ActionRefResolver.cs`

```csharp
private async Task<bool> IsBranchHeadWithFallbackAsync(
    string owner, string repo, string sha, string token, CancellationToken cancellationToken)
{
    var path = $"repos/{owner}/{repo}/commits/{sha}/branches-where-head";
    var response = await SendGetWithFallbackAsync(path, token, cancellationToken);
    if (response is null) return false;
    using (response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.GetArrayLength() > 0;
    }
}
```

### Step 3: `ResolveCommitAsync` の修正

**変更ファイル:** `src/Seiton.Core/Linting/OnlineAudit/ActionRefResolver.cs`

`CommitExists` が `true` の場合、タグチェックに加えて `IsBranchHeadWithFallbackAsync` を呼び出す。タグ付きの場合はブランチ HEAD チェックをスキップ（API 呼び出し削減）。

```csharp
private async Task<ActionRefResolution> ResolveCommitAsync(...)
{
    var commitExists = await CommitExistsWithFallbackAsync(...);
    if (!commitExists)
        return new ActionRefResolution(false, false, false, false, false);

    var isTaggedCommit = await IsTaggedCommitWithFallbackAsync(...);
    if (isTaggedCommit)
        return new ActionRefResolution(true, false, false, true, true);  // タグ付き = 到達可能

    var isBranchHead = await IsBranchHeadWithFallbackAsync(...);
    return new ActionRefResolution(true, false, false, false, isBranchHead);
}
```

### Step 4: `ImpostorCommitRule.EvaluateTarget` の修正

**変更ファイル:** `src/Seiton.Core/Linting/Rules/ImpostorCommitRule.cs`

```csharp
public override void EvaluateTarget(ActionAuditTarget target, ActionAdvisory? advisory, ActionRefResolution? resolution)
{
    if (resolution is null || !target.IsCommitSha)
        return;

    if (!resolution.Value.CommitExists)
    {
        AddError(
            $"'{target.UsesText}' pins commit '{target.Reference}' that is not reachable in '{target.Owner}/{target.Repo}'",
            target.Location);
        return;
    }

    if (!resolution.Value.IsReachable)
    {
        AddError(
            $"'{target.UsesText}' pins commit '{target.Reference}' that exists in '{target.Owner}/{target.Repo}' object storage but is not reachable from any branch or tag (possible impostor commit from a fork)",
            target.Location);
    }
}
```

### Step 5: `StaleActionRefsRule` への影響確認

**変更ファイル:** `src/Seiton.Core/Linting/Rules/StaleActionRefsRule.cs`

現在のロジック:
```csharp
if (resolution is null || !target.IsCommitSha || !resolution.Value.CommitExists || resolution.Value.IsTaggedCommit)
    return;
```

`IsReachable` が `false` の場合（impostor commit の可能性があるケース）、`StaleActionRefsRule` は impostor-commit ルールに判定を委ねるべき。`IsReachable == false` のコミットに対して stale 警告を出すのは冗長。

修正案:
```csharp
if (resolution is null || !target.IsCommitSha || !resolution.Value.CommitExists
    || resolution.Value.IsTaggedCommit || !resolution.Value.IsReachable)
    return;
```

### Step 6: `ResolveSymbolicRefAsync` の `IsReachable` 設定

シンボリック ref（ブランチ/タグ名）の場合は SHA ピンではないため impostor commit の検証対象外。`IsReachable` はデフォルト `false` で問題ない（ルール側で `IsCommitSha` チェックが先にある）。

## 4. テスト計画

### 4.1 既存テストの修正

以下のテストで `ActionRefResolution` のコンストラクタに `IsReachable` パラメータを追加する必要がある:

**`OnlineAuditEngineTests.cs`:**

| テストメソッド | 修正内容 |
|---|---|
| `AuditAsync_PassThrough_WhenProvidersReturnNoData` | `new ActionRefResolution()` → デフォルト値で OK |
| `AuditAsync_AddsExpectedDiagnostics_ForWorkflowCallAndStepUses` | `CommitExists: true` のケースに `IsReachable: true` を追加 |
| `AuditAsync_AddsImpostorCommit_WhenShaMissing` | `CommitExists: false` → `IsReachable: false` は既にデフォルト。変更不要 |
| `AuditAsync_ProcessesAllActions_WhenNoIgnoreConfig` | `IsReachable` 追加 |

### 4.2 新規テスト（Red → Green）

**テストクラス:** `OnlineAuditEngineTests`

| テストメソッド | シナリオ | 期待結果 |
|---|---|---|
| `AuditAsync_AddsImpostorCommit_WhenCommitExistsButNotReachable` | `CommitExists: true, IsTaggedCommit: false, IsReachable: false` | `impostor-commit` 診断が 1 件出力される |
| `AuditAsync_NoImpostorCommit_WhenCommitIsTagged` | `CommitExists: true, IsTaggedCommit: true, IsReachable: true` | `impostor-commit` 診断なし |
| `AuditAsync_NoImpostorCommit_WhenCommitIsBranchHead` | `CommitExists: true, IsTaggedCommit: false, IsReachable: true` | `impostor-commit` 診断なし |
| `AuditAsync_StaleRefs_SkipsUnreachableCommit` | `CommitExists: true, IsTaggedCommit: false, IsReachable: false` | `stale-action-refs` 診断なし（impostor-commit に委ねる） |

**テストクラス:** `ImpostorCommitRuleTests`（新規、ルール単体テスト）

| テストメソッド | シナリオ | 期待結果 |
|---|---|---|
| `EvaluateTarget_CommitNotExists_AddsError` | `CommitExists: false` | エラー 1 件 |
| `EvaluateTarget_CommitExistsAndReachable_NoError` | `CommitExists: true, IsReachable: true` | エラーなし |
| `EvaluateTarget_CommitExistsButNotReachable_AddsError` | `CommitExists: true, IsReachable: false` | エラー 1 件（メッセージに "impostor commit from a fork" を含む） |
| `EvaluateTarget_NonCommitSha_NoError` | `IsCommitSha: false` | エラーなし |

### 4.3 テストの実行順序

```shell
# Step 1: 新規テスト作成 → 失敗を確認 (Red)
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/OnlineAuditEngineTests/AuditAsync_AddsImpostorCommit_WhenCommitExistsButNotReachable*

# Step 2: 実装 → テスト成功を確認 (Green)
dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/OnlineAuditEngineTests/*

# Step 3: 全テスト実行 → リグレッションなし確認
dotnet test
```

## 5. パフォーマンス評価

### 影響範囲

- この変更は **online ルール**（ネットワーク API 呼び出し）にのみ影響
- パーサーやローカル lint ルールのホットパスには一切変更なし
- `ActionRefResolution` に `bool` フィールド 1 つ追加 → 構造体サイズ変化は無視可能（1 byte、パディング含めても既存アラインメント内）

### API 呼び出し数の変化

| ケース | 現行 | 修正後 | 差分 |
|--------|------|--------|------|
| SHA ピン、コミット不存在 | 1 (commits) | 1 (commits) | ±0 |
| SHA ピン、タグ付きコミット | 2 (commits + tags) | 2 (commits + tags) | ±0 |
| SHA ピン、タグなしコミット | 2 (commits + tags) | 3 (commits + tags + branches-where-head) | +1 |
| シンボリック ref | 2 (heads + tags) | 2 (heads + tags) | ±0 |

追加コストが発生するのは「SHA ピンかつタグなし」のケースのみ。タグなし SHA ピンは本来 `stale-action-refs` で検出されるべきケースであり、正当な SHA ピンは通常タグ付きなので、追加コストの発生頻度は低い。

### ベンチマーク確認手順

```shell
# 修正前にベースラインを取得
cd src/Seiton.Benchmark
dotnet run -c Release

# 修正後に再実行して比較
dotnet run -c Release
```

確認項目:
- `CoreLintBenchmark`: Mean と Allocated が +10% 以内
- `CoreParsingBenchmark`: 変更なしの確認（パーサーには無関係）

**予想**: パーサー/ローカル lint のベンチマークに影響なし。online ルールはベンチマーク対象外（ネットワーク依存のため）。`ActionRefResolution` の `bool` 追加は struct コピーコストに影響しない。

### ベンチマーク結果（実装後）

```
BenchmarkDotNet v0.15.6, Windows 11 (10.0.26200.8246)
AMD Ryzen 9 7950X3D 4.20GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.6 (10.0.6, 10.0.626.17701), X64 RyuJIT x86-64-v4

| Method                            | Size   | FixEnabled | Mean         | Allocated |
|---------------------------------- |------- |----------- |-------------:|----------:|
| 'LintEngine.Check (parse + lint)' | Small  | False      |     72.02 μs |  24.06 KB |
| 'LintEngine.Check (parse + lint)' | Small  | True       |     85.81 μs |  25.52 KB |
| 'LintEngine.Check (parse + lint)' | Medium | False      |  1,300.22 μs | 137.28 KB |
| 'LintEngine.Check (parse + lint)' | Medium | True       |  2,544.26 μs | 150.64 KB |
| 'LintEngine.Check (parse + lint)' | Large  | False      | 23,439.49 μs | 710.18 KB |
| 'LintEngine.Check (parse + lint)' | Large  | True       | 35,226.56 μs | 764.91 KB |
```

**結論**: Ratio=1.00、Alloc Ratio=1.00 — 全サイズでベースラインと同等。回帰なし確認済み。

## 6. スペック更新

### `Seiton_Linter_spec.md`

§4.4 `impostor-commit` の説明を更新:

> Error when a SHA-pinned `uses:` reference points to a commit that exists in the repository's object storage but is not reachable from any branch HEAD or tag in the referenced repository's own ref namespace. This detects both completely missing commits and fork-origin impostor commits.

### `Seiton_Linter_csharp_spec.md`

`ActionRefResolution` の定義に `IsReachable` フィールドを追加:

> `ActionRefResolution` includes `IsReachable` (bool): true when the commit is reachable from at least one branch HEAD or tag in the repository's own ref namespace. Determined by `branches-where-head` API when `IsTaggedCommit` is false.

## 7. 実装チェックリスト

- [x] `ActionRefResolution` に `IsReachable` フィールド追加
- [x] `IsBranchHeadWithFallbackAsync` メソッド追加
- [x] `ResolveCommitAsync` でタグなしコミットに対して `IsBranchHeadWithFallbackAsync` 呼び出し
- [x] `ImpostorCommitRule.EvaluateTarget` でフォーク由来コミットの検出ロジック追加
- [x] `StaleActionRefsRule.EvaluateTarget` で `IsReachable == false` のスキップ追加
- [x] 既存テストのコンパイルエラー修正（`ActionRefResolution` コンストラクタ変更）
- [x] 新規テスト追加（impostor commit フォークケース）
- [x] 全テスト通過確認
- [x] ベンチマーク比較（CoreLintBenchmark: Mean/Allocated ±10% 以内）
- [x] `Seiton_Linter_spec.md` 更新
- [x] `Seiton_Linter_csharp_spec.md` 更新
