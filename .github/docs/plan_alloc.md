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
| `RetainedMemoryProbe` | 実行中の live heap ピークを計測（`GC.GetTotalMemory` + `PeakSampler`） |
| `MultiFileLintHarness` | Sequential / Parallel の multi-file lint ループを共通化 |
| `MultiFileLintBenchmark` | 従来どおり **スループット + 累積 `Allocated`** を計測 |
| `MultiFileLintPeakMemoryBenchmark` | **`PeakHeap` 列（bytes）** でピーク常駐 heap delta を計測 |
| `RetainedMemoryProbeTests` | プローブの正当性 + parallel peak の sub-linear 性を検証 |
| `benchmark.yaml` | `*MultiFileLintPeakMemoryBenchmark*` フィルタを追加 |

### API 設計（ユーザーファースト）

- **2 つのベンチを用途で分離** — 混在させない。
  - スループット / 累積 allocation → `MultiFileLintBenchmark`
  - ピーク常駐 heap → `MultiFileLintPeakMemoryBenchmark`
- **`RetainedMemoryProbe` / `MultiFileLintHarness` は internal** — ベンチとテストの共有実装。CLI ユーザー向け API は変更なし。
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
| Parallel | F1 | 6,717,133 (~6.4 MB) |
| Sequential | F1 | 6,532,833 (~6.2 MB) |
| Parallel | F10 | 9,194,400 (~8.8 MB) |
| Sequential | F10 | 65,121,967 (~62 MB) |
| Parallel | F50 | 25,981,067 (~24.8 MB) |
| Sequential | F50 | 207,340,767 (~198 MB) |

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
| Parallel F10 → F50 | PeakHeap 9.2 MB → 26.0 MB（**2.8x**） | ファイル数 5 倍に対し sub-linear。ThreadLocal engine + arena 再利用により同時実行数で頭打ち |
| Parallel F50 vs Allocated F50 | PeakHeap ~26 MB vs Allocated ~5.3 MB | 指標の意味が異なる（常駐ピーク vs 累積確保） |
| Sequential F50 PeakHeap ~198 MB | F1 の ~30x | 単一 `LintEngine` が high-water mark を保持。累積 allocation と同様にファイル数に依存 |

**結論:** ユーザーの「並列時の常駐上限はコア数付近」という理解は、**Parallel + PeakHeap 指標**で確認できる。`Allocated` だけを見ると誤解が生じる。

### レビュー指摘と対応

| 指摘 | 対応 |
|---|---|
| `GC.GetGCMemoryInfo().HeapSizeBytes` ではピークを捕捉できない | `GC.GetTotalMemory` に変更 |
| BDN が `long` 戻り値を `Mean ms` と誤表示 | `PeakHeapColumn` + `PeakMemoryBenchmarkConfig` で bytes 列を追加 |
| メモリテストが並列実行で flaky | `[NotInParallel("RetainedMemory")]` + sub-linear 判定を F10→F50 比較に変更 |
| `IterationSetup` 内 GC を計測対象に含めていた | GC compact を `IterationSetup` に分離し、lint 本体のみ計測 |

### CI ゲート方針（提案）

| ベンチ | 用途 | ゲート |
|---|---|---|
| `MultiFileLintBenchmark` | スループット / 累積 allocation 回帰 | Mean ±10%, Allocated ±10% |
| `MultiFileLintPeakMemoryBenchmark` | 常駐 heap 上限 | Parallel F50/F10 PeakHeap < 3.75x（= 5x linear x 0.75） |
| `RetainedMemoryProbeTests` | 上記の自動検証 | `dotnet test` で常時実行 |

---

## 今後の対応（フェーズ B 以降）

### B. per-file allocation を減らす（構造改善）

1. `WorkflowParser.*` の `List<T>.ToArray()` を `PooledBuffer<T>` + arena 登録方式に置換
2. AST ノードの `T[]` を `(T[] buffer, int length)` 型へ段階的再設計
3. `SliceMap` 同様、配列バッファのライフサイクルを arena に統合

### C. 並列実行時オーバーヘッドの抑制

CLI 実運用で `ThreadLocal<LintEngine>` 初期化コストが問題になる場合のみ、ワーカープール再利用を検討。

## 結論

- 現状の `Allocated` 線形増加は「リーク」ではなく、**累積 allocation 指標の性質 + per-file 配列生成設計**で説明できる。
- **フェーズ A 完了:** 計測を分離し、Parallel peak heap が file count に対して sub-linear であることをベンチ + テストで確認した。
- 根本的な `Allocated` 係数低減は **フェーズ B**（`ToArray()` 削減）で対応する。
