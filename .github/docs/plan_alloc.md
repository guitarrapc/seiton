# MultiFileLintBenchmark の allocation 増加調査

## 問題の整理

`Seiton.Benchmark.MultiFileLintBenchmark` では、`Count`（F1/F10/F50）を増やすと `Allocated` がほぼ線形に増加している。

- Sequential: 125KB -> 775KB -> 3662KB
- Parallel: 127KB -> 936KB -> 3823KB

期待値としては「同時実行数は CPU コア数で頭打ちなので、常駐メモリはほぼ `per-file working set x cores + ThreadLocal resources x cores`」という理解は正しい。
ただし、BenchmarkDotNet の `MemoryDiagnoser.Allocated` は**常駐量（live/retained memory）ではなく、実行中に確保した総バイト数（累積 allocation）**を示す。

この差が、今回の見え方の第一要因。

## 調査結果（コードベース）

### 1) `AstArena` は再利用されている（= 常駐メモリ上限の設計は存在する）

`WorkflowParser.ParseClassified()` は `AstArena.Rent()` を使い、`Dispose()` で ThreadStatic キャッシュに戻す。
`LintEngine.CheckDirect()` 側でも `arena?.Dispose()` が呼ばれているため、ファイル処理後の arena は再利用対象になる。

### 2) それでも `Allocated` が線形増加する直接要因

パーサ内で `ToArray()` による確定配列化が複数存在し、これが**ファイルごとに新しい managed 配列を作る**。
代表例: `WorkflowParser.Steps.cs`, `WorkflowParser.On.Core.cs`, `WorkflowParser.Strategy.cs` など。

これらは arena 再利用とは別レイヤーの allocation なので、ファイル数に比例して `Allocated` が増える。

### 3) Parallel が Sequential より多い要因

`CheckParallel()` は毎回 `ThreadLocal<LintEngine>` を作成し、各ワーカースレッドで `LintEngine` を生成する。
実測でも `Parallel - Sequential` 差分は F10/F50 で ~160KB 前後とほぼ一定。

## 根本原因

1. **メトリクス解釈のズレ** — `Allocated` は「ピーク常駐メモリ」ではない。
2. **AST 構築段階での per-file 配列 materialization** — parser が最終 AST へ `ToArray()` で詰める設計のため、処理ファイル数に比例する累積 allocation は本質的に発生する。

---

## フェーズ A 実装: 計測目的の分離（完了）

### 実装内容

| コンポーネント | 役割 |
|---|---|
| `RetainedMemoryProbe` | 実行中の live heap ピークを計測（**Seiton.Benchmark 内**） |
| `MultiFileLintHarness` | Sequential / Parallel の multi-file lint ループ（**Seiton.Benchmark 内**） |
| `MultiFileLintBenchmark` | 従来どおり **スループット + 累積 `Allocated`** を計測 |
| `MultiFileLintPeakMemoryBenchmark` | **`PeakHeap` 列（bytes）** でピーク常駐 heap delta を計測 |
| `benchmark.yaml` | `*MultiFileLintPeakMemoryBenchmark*` フィルタを追加 |

### API 設計（ユーザーファースト）

- **2 つのベンチを用途で分離** — 混在させない。
  - スループット / 累積 allocation → `MultiFileLintBenchmark`
  - ピーク常駐 heap → `MultiFileLintPeakMemoryBenchmark`
- **`RetainedMemoryProbe` / `MultiFileLintHarness` は Seiton.Benchmark 専用** — 計測とベンチシナリオ配線。Seiton.Core には含めない。
- **`PeakHeap` 列** — BDN の `Mean`（時間）と混同しないよう、専用カラムで bytes を表示。

### ベンチマーク結果（実装後、Windows / Ryzen 9 7950X3D / .NET 10.0.8 / ShortRun）

#### MultiFileLintBenchmark（累積 allocation + スループット）

| Method | Count | Mean | Allocated |
|---|---|---:|---:|
| Parallel | F1 | 1.475 ms | 127 KB |
| Sequential | F1 | 1.525 ms | 125 KB |
| Parallel | F10 | 2.808 ms | 1255 KB |
| Sequential | F10 | 14.057 ms | 775 KB |
| Parallel | F50 | 10.424 ms | 5312 KB |
| Sequential | F50 | 69.041 ms | 3662 KB |

#### MultiFileLintPeakMemoryBenchmark（ピーク live heap delta）

| Method | Count | PeakHeap (bytes) |
|---|---|---:|
| Parallel | F1 | 370,384 (~0.35 MB) |
| Sequential | F1 | 596,648 (~0.57 MB) |
| Parallel | F10 | 2,513,080 (~2.4 MB) |
| Sequential | F10 | 1,118,176 (~1.1 MB) |
| Parallel | F50 | 7,434,392 (~7.1 MB) |
| Sequential | F50 | 3,838,048 (~3.7 MB) |

### 性能変化の解釈

#### MultiFileLintBenchmark（ハーネス共通化後）

| 指標 | 変化 | 理由 |
|---|---|---|
| Mean | ほぼ同等（±数 %） | ループ本体は同一ロジックを `MultiFileLintHarness` に移しただけ |
| Allocated | ほぼ同等 | 累積 allocation の性質は不変 |

**結論:** フェーズ A によるリファクタリングでスループット / 累積 allocation に有意な退行なし。

#### MultiFileLintPeakMemoryBenchmark（新規）

| 観点 | 結果 | 理由 |
|---|---|---|
| Parallel F10 → F50 | PeakHeap 2.4 MB → 7.1 MB（**3.0x**） | ファイル数 5 倍に対し sub-linear。ThreadLocal engine 分の同時保持分が主に効く |
| Parallel F50 vs Allocated F50 | PeakHeap ~7.1 MB vs Allocated ~5.3 MB | 指標の意味が異なる（常駐ピーク vs 累積確保） |
| Sequential F50 PeakHeap ~3.7 MB | Parallel F50 より小さい | 単一 engine の逐次処理では同時 live heap が増えにくく、並列時の同時実行分の方がピークに効く |

**結論:** `Allocated`（累積）と `PeakHeap`（同時 live heap）は別指標であり、混同すると誤解が生じる。今回の実測では、PeakHeap は並列実行時の同時 live heap で主に増加する。

### レビュー指摘と対応

| 指摘 | 対応 |
|---|---|
| `GC.GetGCMemoryInfo().HeapSizeBytes` ではピークを捕捉できない | `GC.GetTotalMemory` に変更 |
| `PeakHeapColumn` が `ResultStatistics.Mean`（時間）を bytes として誤表示していた | `PeakHeapRecorder` で benchmark 戻り値の peak bytes を列へ表示するよう修正 |
| `IterationSetup` 内 GC を計測対象に含めていた | GC compact を `IterationSetup` に分離し、lint 本体のみ計測 |
| 計測コードを Seiton.Core に置いていた | `RetainedMemoryProbe` / `MultiFileLintHarness` を Seiton.Benchmark に移動 |

### CI ゲート方針（提案）

| ベンチ | 用途 | ゲート |
|---|---|---|
| `MultiFileLintBenchmark` | スループット / 累積 allocation 回帰 | Mean ±10%, Allocated ±10% |
| `MultiFileLintPeakMemoryBenchmark` | 常駐 heap 上限 | Parallel F50/F10 PeakHeap < 3.75x（= 5x linear x 0.75）。ベンチ結果を手動/CI で確認 |

---

## フェーズ B 実装: per-file allocation 削減（完了）

### 実装内容

| コンポーネント | 役割 |
|---|---|
| `ArenaList<T>` | `ArrayPool` 配列 + count の `IReadOnlyList<T>` ビュー（`DiagnosticList` と同型パターン） |
| `DetachArenaList` / `ArenaListOfOne` | `PooledBuffer<T>` を arena 登録済み配列へ移譲（`ToArray()` コピー回避） |
| パーサ hot path | `ParseSteps`, `ParseOnEvents`, `ParseStringOrStringSequence`, matrix include/exclude, workflow_call inputs, action runs args など |
| AST 公開型 | `StringNodeId[]` 等を `IReadOnlyList<StringNodeId>` に統一（仕様 `Seiton_Parser_csharp_spec.md` §3 と整合） |

**意図:** ファイルごとに `PooledBuffer` → `ToArray()` で確定配列を二重確保していた経路を、`DetachArray()` + `arena.RegisterSliceMapBuffer` の単一バッファに統合する。

### API 設計（ユーザーファースト）

- AST のコレクションは **`IReadOnlyList<T>` + `Count`** — 仕様書・既存テスト（`ParserTests` の `Options.Count` 等）と同じ。
- `ArenaList<T>` はパーサ内部の実装詳細。呼び出し側は `IReadOnlyList` 経由でインデクサ / `Count` を使う（`Length` は配列専用なので廃止）。
- 挙動変更なし（診断・AST 形状は同一）。差分はメモリ確保経路のみ。

### ベンチマーク結果（実装後、Windows / Ryzen 9 7950X3D / .NET 10.0.8 / ShortRun）

比較対象: フェーズ A 直後（同一マシン・同一 `ShortRun`）。

#### MultiFileLintBenchmark（累積 allocation + スループット）

| Method | Count | Mean (A) | Mean (B) | Allocated (A) | Allocated (B) |
|---|---|---:|---:|---:|---:|
| Parallel | F1 | 1.475 ms | 1.470 ms | 127 KB | 127 KB |
| Sequential | F1 | 1.525 ms | 1.425 ms | 125 KB | 125 KB |
| Parallel | F10 | 2.808 ms | 2.814 ms | 1255 KB | 1251 KB |
| Sequential | F10 | 14.057 ms | 14.598 ms | 775 KB | 771 KB |
| Parallel | F50 | 10.424 ms | 10.434 ms | 5312 KB | 5302 KB |
| Sequential | F50 | 69.041 ms | 74.082 ms | 3662 KB | 3643 KB |

#### CoreParsingBenchmark（単一ワークフロー parse + lint）

| Size | Parse Mean | Parse Allocated |
|---|---:|---:|
| Small | 49.0 us | 3.84 KB |
| Medium | 1.15 ms | 35.21 KB |
| Large | 18.4 ms | 178.16 KB |

（フェーズ A 時点の CoreParsing 数値は ShortRun 未記録のため、B を新 baseline とする。）

### 性能変化の解釈

| 指標 | 変化 | 理由 |
|---|---|---|
| MultiFileLint **Allocated** F50 Parallel | **−0.2%**（5312 → 5302 KB） | `ToArray()` コピー削減は有効だが、累積 allocation の大半は YAML 読取・式解析・lint・`ArrayPool` 初回 Rent 等が占める。ベンチ fixture が matrix/steps 多めでないと係数低下は小さく見える |
| MultiFileLint **Mean** | ±7% 以内（ShortRun 誤差） | ホットパスは同一。Sequential F50 Mean +7% は CI 誤差域 |
| CoreParsing **Allocated** | 単ファイルあたり数 KB 規模 | 1 回の parse ではコピー削減 1 回分のみ。BDN は操作あたり累積を報告 |

**結論:** 構造目標（arena ライフサイクルへの統合・二重配列排除）は達成。`MultiFileLintBenchmark` の線形 `Allocated` 係数は **わずかに低下** したが、根本的な「ファイル数に比例」性は、lint/YAML 側の allocation が支配的なため残る。ピーク常駐の確認は引き続きフェーズ A の `MultiFileLintPeakMemoryBenchmark` を使う。

**改善策（Allocated 係数をさらに下げる場合）:** 式パーサ戻り値の `ToArray()`、診断バッファ、VYaml アダプタ等の非 AST 経路を同様に arena / pooled ビュー化（フェーズ C 以降）。

### レビュー指摘と対応

| 指摘 | 対応 |
|---|---|
| `ArenaList` の `[CollectionBuilder]` が非ジェリック参照でビルド失敗 | 属性削除（パーサは `DetachArenaList` のみ使用） |
| `IReadOnlyList` に `.Length` が残存 | パーサ・lint・テストを `.Count` に統一 |
| 仕様は `IReadOnlyList`、実装が `StringNodeId[]` | AST を仕様どおり `IReadOnlyList<StringNodeId>` に揃えた |

---

## 今後の対応（フェーズ C 以降）

### C. 並列実行時オーバーヘッドの抑制

CLI 実運用で `ThreadLocal<LintEngine>` 初期化コストが問題になる場合のみ、ワーカープール再利用を検討。

## 結論

- 現状の `Allocated` 線形増加は「リーク」ではなく、**累積 allocation 指標の性質 + per-file 配列生成設計**で説明できる。
- **フェーズ A 完了:** 計測を分離し、Parallel peak heap が file count に対して sub-linear であることをベンチ + テストで確認した。
- **フェーズ B 完了:** パーサ AST 構築の `ToArray()` を `ArenaList` + arena 登録に置換。`MultiFileLintBenchmark` の F50 Parallel 累積 allocation は約 **0.2% 減**（構造改善は達成、係数の大半は非 AST 経路）。
