# PartialChange が FullChange より遅い原因と改善策

## 1. ベンチマーク結果

```
 Method        | Size  | Mean            | Allocated | Alloc Ratio
-------------- |------ |----------------:|----------:|------------:
 NoChange      | Small |        93.12 ns |         - |          NA
 PartialChange | Small | 1,134,250.78 ns |  154000 B |          NA
 FullChange    | Small |   232,809.21 ns |   67427 B |          NA
               |       |                 |           |
 NoChange      | Large |        81.35 ns |         - |          NA
 PartialChange | Large | 4,715,520.83 ns |  627396 B |          NA
 FullChange    | Large | 1,304,325.26 ns |  409287 B |          NA
```

PartialChange は FullChange と比較して **Small で 4.87x 遅く / 2.28x メモリ増加、Large で 3.61x 遅く / 1.53x メモリ増加**している。

## 2. 根本原因

### 2.1 FullChange が高速な理由（直感に反する事実）

`nameSuffix` の変更パターンを詳細に追跡すると、FullChange が高速なのは「全部再解析しているから」ではない。

| イテレーション | 前回ソース | 今回ソース | name セクション長 | 実行パス |
|---:|---|---|---|---|
| Warmup → 0 | `name: bench` (5文字) | `name: bench-variant0` (14文字) | **+9バイト** | offset shift → `FullParseAndStore` |
| 0 → 1 | `bench-variant0` (14文字) | `bench-variant1` (14文字) | **同一** | すべての root section + すべての job が同一 offset・同一 hash → **全スキップ** |
| 1 → 2 | `bench-variant1` | `bench-variant2` | 同一 | 全スキップ |
| ... | ... | ... | ... | 全スキップ |

`ComputeSkipMask` は `Name`/`RunName` を検査対象外にしているため、`name:` の値が変わっても `anyChanged` は `false` のままで skip mask が有効になる。さらに全 job の offset・hash も一致するため `ComputeJobSkipEntries` が全 job をスキップ可能と判定する。

**結果: 10 回中 9 回は実質ゼロコスト（parse も lint も行わない）。**

### 2.2 PartialChange が遅い理由

`firstJobStepSuffix` の変更パターン：

| イテレーション | 前回ソース | 今回ソース | job0 ステップ名 | 実行パス |
|---:|---|---|---|---|
| Warmup → 0 | `- name: Run` | `- name: Run-edit0` | **+6バイト** | offset shift → `FullParseAndStore` |
| 0 → 1 | `Run-edit0` (6文字) | `Run-edit1` (6文字) | **同一長** | job0 変更、job1-5 スキップ → **incremental** |
| 1 → 2 | `Run-edit1` | `Run-edit2` | 同一長 | incremental |
| ... | ... | ... | ... | incremental |

**Small (1 job) の場合:**
- 唯一の job が毎回変更 → `ComputeJobSkipEntries` が null を返す → **毎回 `FullParseAndStore`**
- しかし `FullParseAndStore` の前に `ScanRootSections` + `ScanJobSections` + `ComputeSkipMask` + `ComputeJobSkipEntries` を実行しており、すべて無駄になる
- 10 回すべてフルパース + 無駄なスキャン・ハッシュ計算オーバーヘッド

**Large (6 jobs) の場合:**
- イテレーション 1-9 は incremental パスを通る
- しかし incremental パスは以下のコスト合計を持つ:

### 2.3 Incremental パスの隠れたコスト

incremental パス（iterations 1-9）が 1 回あたりに支払うコスト：

| ステージ | 処理内容 | コスト |
|---|---|---|
| 1. Section scan | `ScanRootSections` + `ScanJobSections`：全ソースの O(n) バイトスキャン | バイトスキャン + XXH64 hash 計算 × セクション数 |
| 2. Skip 判定 | `ComputeSkipMask` + `ComputeJobSkipEntries` | 比較 + hash 比較 |
| 3. Arena import | `BulkImportFrom`: 前回 arena のエントリを新 arena にコピー | `Array.Copy` × 4 (string/bool/int/float)。Large workflow ではエントリ数が多い |
| 4. VYaml tokenization | `ParseIncremental`: VYaml が**ソース全体**をトークナイズし、skip 対象は `SkipCurrentNode()` で飛ばす | ソース全体の字句解析コスト（skip してもトークンは読む） |
| 5. Job parse | 変更された job0 のフルパース | 1 job 分の parse コスト |
| 6. Section patch | `PatchSkippedSections`: 前回 AST ノードの参照コピー | 軽い |
| 7. Arena retention | 前回 arena の retain 判定 + `_retainedArenas` 管理 | 軽いが複雑性を増す |
| 8. Lint setup | `PrepareForRun` + rule activation + inline suppression parse + exclusion normalization | **毎回**実行（skip 有無にかかわらず） |
| 9. Visitor traversal | `VisitWorkflowPre`/`VisitEvent`/`VisitWorkflowPost` は**全ルールで毎回**実行 + job0 の step lint | skip されない job + workflow 全体の visit |
| 10. Diagnostic merge | `MergeDiagnosticsWithCache`: List + Sort + `CacheJobDiagnostics` (per-job Diagnostic[] 割当) | **毎回ソート** + per-job 配列割当 |
| 11. JSON serialize | `WriteDiagnosticsArray` → JSON buffer | 毎回 |
| 12. Result compare | `_lastJsonOutput` との比較・コピー | 毎回 |

**対して FullChange iterations 1-9 は:**
- ステージ 1-3 は同じコスト
- ステージ 4: `ParseIncremental` で**全 root section + 全 job を SkipCurrentNode()**  → parse ゼロ
- ステージ 5: parse する job がゼロ
- ステージ 8: lint setup は同じ（ここが非効率）
- ステージ 9: `VisitWorkflowPre`/`VisitEvent`/`VisitWorkflowPost` は同じだが**全 job が skip される** → per-job/per-step lint ゼロ
- ステージ 10: `MergeDiagnosticsWithCache` で全 job が cache hit → fresh diagnostics が少ないため merge コスト低

### 2.4 支配的コスト要因の推定

1. **VYaml tokenization（ステージ 4）**: incremental パスでも VYaml はソース全体をトークン化する。FullChange で全スキップの場合でも同じだが、PartialChange は job0 のトークンを実際に消費するため差が出る。
2. **Lint setup（ステージ 8）**: `CheckCore` は毎回 `NormalizeRules` + `ParseInlineSuppression` + `NormalizeExclusions` を実行する。これは skip 有無に関係なく発生する固定コスト。
3. **Job parse + lint（ステージ 5 + 9）**: 1 job 分の parse + lint は、Large で 8 steps × 多数のルールの visit を含む。
4. **Diagnostic merge + cache（ステージ 10）**: `MergeDiagnosticsWithCache` の `List<Diagnostic>.Sort()` と `CacheJobDiagnostics` の per-job `Diagnostic[]` 割当が毎回発生する。
5. **Small 特有**: 1 job しかないため incremental の恩恵がゼロ。毎回フルパースする上にスキャン・ハッシュ計算のオーバーヘッドが加算される。

## 3. 改善策

### 3.1 即効性のある改善（Low-Hanging Fruit）

#### P-1: Small (1 job) の early exit — スキャン前に job 変更を検出

現状は `ScanRootSections` → `ScanJobSections` → `ComputeSkipMask` → `ComputeJobSkipEntries` → null → `FullParseAndStore` のフルパイプラインを走らせた後に「スキップ不可」と判定している。

**改善**: `IsSourceIdentical` の直後に、ソース長が同一かつ前回 job 数が 1 の場合は、job セクション全体の hash だけ比較して変更を即検出し、`FullParseAndStore` に直行する。

```
効果: Small PartialChange のスキャン + hash 計算オーバーヘッドを排除。
      FullParseAndStore 自体のコストは変わらないが、無駄な前処理が消える。
推定効果: Small で 5-10% 改善。
```

#### P-2: MergeDiagnosticsWithCache の Sort 排除

現在 `MergeDiagnosticsWithCache` は毎回 `List.Sort()` を実行しているが、fresh diagnostics も cached diagnostics も既に offset 順でソート済みの場合が多い。2 つのソート済みリストのマージは O(n+m) で可能。

```
改善: ソート済み配列のマージ走査に変更。
      CacheJobDiagnostics が格納する Diagnostic[] も offset 順を保証。
推定効果: Large で 3-5% 改善（diagnostic 数に比例）。
```

#### P-3: CacheJobDiagnostics の配列再利用

`CacheJobDiagnostics` は毎回 `new Diagnostic[count]` を job 数分割り当てる。前回と同じ count なら配列を再利用できる。

```
改善: _cachedJobDiagnostics[j] の Length == counts[j] なら再利用、
      そうでなければ新規割当。
推定効果: Large で 2-3% 改善。
```

### 3.2 中程度の改善

#### P-4: Lint setup の差分スキップ

`CheckCore` は毎回 `NormalizeRules` + `ParseInlineSuppression` + `NormalizeExclusions` を実行するが、これらはソースが変わらない限り結果が同じ。incremental パスで source content hash が同じ部分に限り、前回の結果を再利用する。

ただし、`ParseInlineSuppression` はソースの `# seiton-disable` コメントをスキャンするため、ソースが変わった job のコメント部分だけ再スキャンする必要がある。

```
改善: NormalizeRules は config 依存のみ → 初回計算、以降キャッシュ。
      ParseInlineSuppression は変更 job のバイト範囲のみ再スキャン。
推定効果: 10-15% 改善（特に Large で suppression スキャンが重い場合）。
複雑性: 中。ParseInlineSuppression の差分スキャンには section registry の活用が必要。
```

#### P-5: VYaml tokenization のスキップ範囲拡大

現在、`ParseIncremental` は VYaml にソース全体を渡し、skip 対象セクションは `SkipCurrentNode()` で飛ばす。しかし VYaml の `SkipCurrentNode()` はトークンを読み進めるだけで、字句解析（UTF-8 デコード、indent 追跡）はスキップできない。

VYaml の tokenizer に「指定バイト範囲をスキップ」機能があれば、skip 対象セクションの字句解析コストをゼロにできるが、VYaml の内部変更が必要で実現性は低い。

代替案: skip 対象セクション数が多い場合（例: 全 root section skip + 5/6 jobs skip）、incremental parse せずに変更 job だけを個別パースし、前回の Workflow に差し替える「ピンポイント再パース」パターン。

```
推定効果: Large で 20-30% 改善。
複雑性: 高。個別 job パースは VYaml の document boundary 管理と整合性を取る必要あり。
リスク: VYaml の state 管理を迂回するため、anchor/alias 解決が壊れる可能性。
```

#### P-6: BulkImportFrom の条件付きスキップ

`BulkImportFrom` は incremental parse のたびに前回 arena のエントリを新 arena にコピーする。しかし全 root section が skip されるケース（FullChange iterations 1-9）では、reuse する job の StringNodeId は前回 arena のインデックスを保持しており、新 arena でも同じインデックスでアクセスできる必要がある。

ただし、FullChange のように全 job が reuse される場合、新 arena に新規エントリを追加する必要がないため、BulkImport は「前回 arena をそのまま新 arena として使う」ことで Array.Copy を回避できる。

```
改善: 全 job skip かつ全 root section skip の場合、前回 arena を直接再利用し、
      source 参照のみ更新する。新規パースが必要な job がある場合のみ BulkImport を実行。
推定効果: FullChange は既に高速なので効果は限定的。
          PartialChange (Large) で BulkImport コストを削減できる可能性あり。
複雑性: 中。arena の source 更新と reused job の StringNodeId 整合性の保証が必要。
```

### 3.3 構造的改善（大きな効果が見込めるが複雑）

#### P-7: Lint の job-level 差分実行

現在の incremental path は **parse は差分だが lint はほぼフル実行** している。`VisitWorkflowPre` は全ルールで毎回呼ばれ、`VisitEvent` も全イベントに対して毎回呼ばれる。skip されるのは `VisitJobPre`/`VisitStep`/`VisitJobPost` のみ。

しかし多くのルールは `VisitWorkflowPre` で workflow 全体の state を構築する（例: job 間の依存関係グラフ、permissions の推論）。この state 構築は変更 job だけに影響する場合でも全 job のデータを走査する。

```
改善方針:
  (a) 変更がない job の VisitWorkflowPre / VisitWorkflowPost 計算もキャッシュ
  (b) rule ごとに「workflow-level state が前回と同一なら skip 可能」フラグを持たせる
推定効果: Large で 30-40% 改善。
複雑性: 高。各ルールの VisitWorkflowPre の冪等性を保証する必要がある。
```

#### P-8: 「変更 job だけパースして前回 Workflow に差し込む」パターン

incremental parse の根本的な設計変更。VYaml でソース全体をトークナイズする代わりに：

1. Edit region から変更 job のバイト範囲を特定（Section registry で既に持っている）
2. 変更 job のバイト範囲だけを切り出して VYaml でパース（独立した YAML document として）
3. 前回の Workflow の該当 job を差し替え

```
課題:
  - job 単体では有効な YAML document にならない場合がある（anchor/alias が job 外を参照）
  - 独立パースすると indent level が異なる
  - VYaml の adapter 初期化コストが per-job で発生
推定効果: parse コストを O(変更 job サイズ) に削減。Large で 50-60% 改善。
複雑性: 非常に高。実用性は VYaml の制約次第。
```

## 4. 推奨実施順

| 優先度 | ID | 改善策 | 推定効果 | 実装コスト |
|---:|---|---|---|---|
| 1 | P-1 | Small の early exit | Small 5-10% | 低 |
| 2 | P-2 | Merge sort 排除 | Large 3-5% | 低 |
| 3 | P-3 | Cache 配列再利用 | Large 2-3% | 低 |
| 4 | P-4 | Lint setup 差分スキップ | 10-15% | 中 |
| 5 | P-6 | BulkImport 条件付きスキップ | 5-10% | 中 |
| 6 | P-7 | Lint job-level 差分 | 30-40% | 高 |
| 7 | P-5 | VYaml skip 範囲拡大 | 20-30% | 高 |
| 8 | P-8 | 変更 job だけパース | 50-60% | 非常に高 |

## 5. 結論

PartialChange が FullChange より遅い根本原因は **「FullChange は実質全スキップ（名前だけ変更、offset 不変、job 同一）」であり「PartialChange は毎回 1 job のフルパース + フル lint + diagnostic merge を実行する」** という非対称性にある。

FullChange ベンチマークは incremental optimization の最良ケース（全スキップ）を測定しており、PartialChange は実際のユーザー操作に近いケースを測定している。**PartialChange の改善こそが実用的なパフォーマンス改善** となる。

短期的には P-1 ~ P-3 の低コスト改善で 10-15% の改善が見込め、中期的には P-4 の lint setup 差分スキップで追加 10-15% の改善が見込める。構造的改善（P-7, P-8）は大きな効果が期待できるが、設計の複雑性とリスクを考慮して慎重に進める必要がある。
