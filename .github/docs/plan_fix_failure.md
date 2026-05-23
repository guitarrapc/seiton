# fix 実行時の overlapping edits 例外 調査結果と修正計画

> 作成日: 2026-05-23
> 対象: `seiton --fix test.yaml` / `seiton --fix --enable-pin-network test.yaml` で `overlapping or conflicting edits detected at offset 78` が発生する問題

---

## 1. 結論

今回の例外は network pinning 固有の問題ではなく、**通常の local autofix だけで再現する fix 競合**である。

`test.yaml` では、少なくとも次の 2 つの rule が **同じ byte offset 78** に 0-length insert を生成する。

- `job-permissions-required`
- `job-timeout-minutes-required`

`FixCommand` は first pass で fixable diagnostics をまとめて `FixEngine.Apply(...)` に渡すため、`FixEngine.ValidateEdits(...)` が同一 offset の edit を競合として例外にしている。

---

## 2. 再現確認

ユーザー報告では publish 済み `Seiton` で以下が失敗する。

```yaml
on:
  pull_request:
    branch: main
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "title is ${{ github.event.pull_request.title }}"
      - uses: actions/checkout@v6
      - uses: actions/setup-node@v4
        with:
          node_version: 18.x
```

```text
seiton --fix --enable-pin-network test.yaml
```

ref 側ソースでも、network pinning を使わない次の実行で同じ例外を確認した。

```text
dotnet run --project src/Seiton -- --fix test.yaml
```

したがって、`--enable-pin-network` は再現条件ではない。pin remediation は first pass に fix を追加するが、**今回のクラッシュ原因そのものは local fix 同士の衝突**である。

---

## 3. 直接原因

### 3.1 例外発生箇所

`src/Seiton.Core/Linting/Fixing/FixEngine.cs`

- `ValidateEdits(...)` は edit を offset 順に並べる
- 次の条件で例外を投げる
  - `edit.Offset < previousEnd`
  - `edit.Offset == previousOffset`

つまり、**同じ offset に複数 edit があるだけで即失敗**する。

### 3.2 競合している edit

`test.yaml` の byte offset 78 は `jobs.test.runs-on` 行の改行直後、つまり `steps:` 行の先頭である。

この位置に対して次の 2 rule が insert fix を作る。

1. `src/Seiton.Core/Linting/Rules/JobPermissionsRequiredRule.cs`
   - `runs-on:` 行を anchor にして `permissions:` block を挿入
2. `src/Seiton.Core/Linting/Rules/JobTimeoutMinutesRequiredRule.cs`
   - 同じく `runs-on:` 行を anchor にして `timeout-minutes:` を挿入

`test.yaml` は `.github/seiton.yaml` により `fix.defaults.job-timeout-minutes: 15` が有効なので、両方の rule が fix を持つ。

結果として first pass の edit 集合に、**同一 offset 78 の 0-length insert が 2 件**入る。

---

## 4. なぜこの YAML で起きたか

`test.yaml` には複数の fixable diagnostics が含まれているが、今回のクラッシュに効いているのは job-level 挿入 fix の組み合わせである。

特に次の条件が揃っている。

- `jobs.test` に `permissions:` がない
- `jobs.test` に `timeout-minutes:` がない
- job 直下に `runs-on:` がある
- `job-timeout-minutes-required` の fix default が config で有効

この 2 rule はどちらも「`runs-on:` の直後に入れる」のが現行設計なので、同じ anchor を共有する job では衝突しうる。

---

## 5. 既存実装の問題点

### 5.1 `FixCommand` の first pass が楽観的すぎる

`src/Seiton/Commands/FixCommand.cs` では first pass で次をそのまま実行している。

```csharp
var firstPassYaml = FixEngine.Apply(currentYaml, effectiveDiagnostics);
```

ここで `effectiveDiagnostics` には以下が同居する。

- local lint/parser fixes
- network pin remediation で fix を付与された diagnostics

しかし、**同一 pass で共存可能かの判定がない**。

### 5.2 pin remediation が first pass 一回だけ

pin remediation は initial YAML に対して 1 回だけ実行される。

このため、first pass を単純に「1件ずつ apply」に変えるだけでは不十分である。先に別の insert を適用すると、後続の pin fix や local fix の offset が stale になるからである。

### 5.3 rule 側での個別回避では根治しない

今回の衝突は `job-permissions-required` と `job-timeout-minutes-required` の組み合わせで顕在化したが、根は「複数 rule が同じ anchor に insert できる」ことにある。

rule ごとに anchor をずらして回避しても、別の組み合わせで再発する。

---

## 6. 推奨修正方針

### 6.1 `FixEngine` の strict validation は維持する

`FixEngine` は low-level edit applicator なので、競合 edit を拒否する現在の挙動は妥当である。

ここを緩めて同一 offset insert を許可すると、

- 挿入順序の定義が必要になる
- replace と insert の意味衝突を隠す
- 本当に危険な overlap まで見逃しやすくなる

ため、根本修正としては不適切。

### 6.2 `FixCommand` を conflict-aware な反復適用に変える

根本修正は `FixCommand` 側で行う。

推奨は次の流れ。

1. 現在の YAML を lint する
2. fixable diagnostics から **非競合な batch** を選ぶ
3. その batch だけを `FixEngine.Apply(...)` で適用する
4. 更新後 YAML に対して再度 lint し直す
5. fix が残っていれば繰り返す

この方式なら、同一 offset 競合は batch 選択時に分離できる。

### 6.3 network pinning は再計算可能な位置に寄せる

pin remediation を含める場合は、stale offset を避ける必要がある。

安全な選択肢は 2 つある。

#### 案 A: 毎 pass で relint + pin remediation をやり直す

- 最も単純で正しい
- ただし network を含むためコストが高い

#### 案 B: local fixes を安定化させてから pin remediation を行う

- まず local/parser/lint fixes を conflict-aware loop で解消
- その後、安定した YAML に対して pin remediation を 1 回実行
- pin fixes を適用

今回の問題に対しては **案 B を第一候補**とする。

理由:

- 今回のクラッシュ原因は local fix 同士
- pin remediation を後段に回せば stale offset の扱いが単純になる
- network 呼び出し回数を増やさずに済む

---

## 7. 実装ステップ

`test.yaml` はテストを設けることでリグレッションを防ぎつつ、red/greenテストで進める。これでテスト用にファイルを用意する必要もなく、実際のユーザーケースに近い形で修正の効果を確認できる。

### Step 1: 失敗再現テストを先に追加

追加先候補:

- `tests/Seiton.Tests/FixCommandTests.cs`

最低限必要な回帰テスト:

1. `--fix` 実行で例外を出さず成功する
2. `permissions:` と `timeout-minutes: 15` の両方が挿入される
3. `steps:` より前に挿入される
4. 既存の他 fix を壊さない

入力は今回の `test.yaml` に近い最小 YAML を使う。

### Step 2: local fix の first pass を batch apply から反復 apply に変更

`FixCommand` で現在の一括 first pass をやめ、

- non-conflicting batch selection
  もしくは
- 1 diagnostic ずつ適用

に変更する。

ここで重要なのは、**適用後に relint して新しい offset を再取得すること**。

### Step 3: pin remediation のフェーズ分離

推奨順序:

1. local fixes を安定化
2. 現在 YAML に対して pin remediation を実行
3. pin fixes を適用
4. 最終 relint

これにより local insert による offset ずれで pin edit が壊れる問題を避けられる。

### Step 4: conflict diagnostics の可観測性を上げる

追加改善として、競合を検知した際に次を出せるようにすると調査が容易になる。

- rule id
- edit offset/length
- 競合相手の rule id

例外メッセージを改善するか、verbose ログに補足を出す。

---

## 8. テスト計画

### 8.1 必須回帰テスト

#### CLI レベル

- `FixCommandTests` に今回の再現ケースを追加
- `--fix` が成功し、ファイルが更新されることを確認

#### Core レベル

- `FixEngine` の「同一 offset edit は拒否する」既存契約は維持
- 新しい batch selector / pass scheduler があるなら、その単体テストを追加

### 8.2 追加で見るべきケース

1. `permissions` と `timeout-minutes` が同時に必要な job
2. `permissions` + `runner-no-latest` のように別位置 fix を含む job
3. local fixes 適用後に unpinned action fix を続けて適用するケース
4. 複数 job があり、それぞれ同種の挿入 fix を持つケース

---

## 9. 非推奨案

以下は採らない方がよい。

### 9.1 `FixEngine` で同一 offset insert を自動マージする

一見楽だが、順序規則が必要になり、真の競合と benign insert の区別が曖昧になる。

### 9.2 各 rule の insert 位置を個別にずらす

今回の 2 rule だけは直っても、別 rule の組み合わせで再発する。

### 9.3 競合時に片方の fix を黙って捨てる

ユーザーにとって結果が不安定になり、fix の決定性が落ちる。

---

## 10. まとめ

今回の例外は、`test.yaml` に対して

- `job-permissions-required`
- `job-timeout-minutes-required`

が同じ anchor に insert fix を出し、`FixCommand` がそれらを first pass で無条件に一括適用したことが原因である。

根本修正は rule 個別調整ではなく、**`FixCommand` の fix orchestration を conflict-aware な反復適用へ変えること**である。

そのうえで pin remediation は local fixes と段階分離し、更新後 YAML に対して再計算する構成に寄せるのが安全である。

---

## 11. 実装結果

> 実装日: 2026-05-23
> 実装方針: 案B（local fixes 安定化後に pin remediation）

### 11.1 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/Seiton/Commands/FixCommand.cs` | first pass を conflict-aware iterative loop に置換。`SelectNonConflictingBatch` で同一 offset 競合を検出し、非競合 subset のみ apply。追加レビューで multi-edit diagnostic に対する occupied buffer 長の不足を修正し、diagnostic 数ベースの `List<int>` を exact-size 配列へ置換。Pin remediation を Phase 2 に分離。dry-run は iterative apply + `BuildUnifiedDiffFromBytes` で diff 生成。 |
| `src/Seiton.Core/Linting/Fixing/FixEngine.cs` | `BuildUnifiedDiffFromBytes` public メソッド追加。`ValidateEdits` の例外メッセージを改善（previous offset/length、batch size を含む）。 |
| `tests/Seiton.Tests/FixCommandTests.cs` | 4 件の回帰テスト追加（single job conflict、dry-run conflict、multi-job conflict、multi-edit diagnostic + another fix）。 |
| `src/Seiton.Benchmark/FixApplyBenchmark.cs` | Fix apply パフォーマンスベンチマーク追加（3 シナリオ）。 |

### 11.2 設計ポイント

1. **SelectNonConflictingBatch**: greedy offset-ordered selection。全 diagnostic を最小 offset 順にソートし、先着 diagnostic を選択。競合する diagnostic は次 pass へ繰り越し。occupied range は total edit count から exact-size 配列を確保して管理。
2. **Iterative relint loop**: 最大 8 pass。各 pass で relint → batch selection → apply。同一 YAML が返れば収束として停止。
3. **Pin remediation phase**: local fix 安定化後に実行。stabilized YAML に対して relint + `RemediateAsync` → apply。
4. **dry-run**: iterative apply + pin phase で最終 YAML を計算し、original との diff を生成。`FixEngine.BuildUnifiedDiffFromBytes` で直接 byte[] 間 diff。

### 11.3 ベンチマーク結果

#### FixApplyBenchmark (新規)

| Scenario | Mean | Allocated |
|---|---|---|
| NoConflict (no conflicts) | 23.37 μs | 10.55 KB |
| SingleJobConflict (2 rules at same offset) | 38.35 μs | 16.95 KB |
| MultiJobConflict (5 jobs × 2 conflicts) | 113.66 μs | 39.81 KB |

#### CoreLintBenchmark (既存 — 回帰確認)

| Size | FixEnabled | Baseline Mean | Current Mean | Δ |
|---|---|---|---|---|
| Small | true | 71.17 μs | 70.16 μs | -1.4% |
| Medium | true | 1,976 μs | 1,994 μs | +0.9% |
| Large | true | 35,950 μs | 33,782 μs | -6.0% |

Allocation は全サイズで完全一致（回帰なし）。Mean の差異は ShortRun ノイズ範囲内。

### 11.4 性能考察

- **conflict なしの場合**: 1 pass で完了。追加コストは `SelectNonConflictingBatch` の O(n log n) sort + O(n²) conflict check だが、n は通常 1-10 程度なので無視できる。
- **conflict ありの場合**: 追加 relint pass が発生。SingleJobConflict では 2 pass（1 diagnostic ずつ）。relint コストが支配的だが、fix 操作自体が低頻度（1 ファイルにつき 1 回）なので問題なし。
- **follow-up selector fix**: occupied range と selected index を exact-size 配列に変更したことで、multi-edit diagnostic が混在するケースでも `IndexOutOfRangeException` が発生しない。Mean / Allocated は悪化せず、NoConflict と conflict case の両方で微改善した。
- **CoreLintBenchmark 回帰なし**: `FixEngine` / `LintEngine.Check` のホットパスには変更なし。変更はすべて `FixCommand` orchestration 層。

### 11.5 テスト結果

全 1961 テスト パス。回帰なし。
