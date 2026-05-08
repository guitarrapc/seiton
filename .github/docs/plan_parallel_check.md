# Parallel Check Implementation Plan

> `seiton check` でワークフローファイルを並列処理するための実装計画。
> actionlint の `errgroup` + goroutine per file パターンに相当する C# 実装を目指す。

---

## 0. 現状と目標

### 0.1 現状

- `CheckCommand` は `for` ループで1ファイルずつ逐次処理
- `LintEngine` のインスタンスを1つ使い回し（内部で Clear/Reset して再利用）
- `LintEngine` は多数の可変インスタンスフィールドを持ち、同一インスタンスの並行 `Check()` 呼び出しはスレッドセーフでない
- `WorkflowParser` は `[ThreadStatic]` バッファ + per-call アリーナで実質スレッドセーフ
- 生成データ（`PopularActions`, `WebhookTypes` 等）は static readonly で読み取り専用
- `FixCommand` は ファイル書き戻し + 反復 re-check パスがあり並列化の恩恵が薄い

### 0.2 目標

| 項目 | 目標 |
|---|---|
| **check コマンド** | ファイル単位で並列処理 |
| **fix コマンド** | 逐次処理を維持（変更なし） |
| **並列制御** | `Parallel.ForEach` + `MaxDegreeOfParallelism` |
| **デフォルト並列数** | `Environment.ProcessorCount` |
| **1ファイル時** | 逐次 fast path（並列オーバーヘッド回避） |
| **出力順** | 入力ファイル順で安定（actionlint と同等） |
| **アロケーション** | 並列化で新規ヒープ割り当てを最小化 |
| **スレッドセーフ** | LintEngine をスレッドごとに分離 |

### 0.3 方式選定

| 方式 | 評価 |
|---|---|
| `Parallel.ForEach` + `MaxDegreeOfParallelism` | **採用** — ファイル単位の CPU バウンド処理に最適。スレッドプール管理を CLR に委任でき、`MaxDegreeOfParallelism` で並行数を完全制御可能。actionlint の `errgroup` + `runtime.NumCPU()` と等価 |
| `System.IO.Pipelines` キュー方式 | **不採用** — I/O ストリーム処理向き。ファイル単位の独立タスクには過剰。パイプラインの接続・完了管理がオーバーヘッドになる |
| `Task.WhenAll` + `SemaphoreSlim` | 候補だが `Parallel.ForEach` のほうが同期処理に自然。async は不要（check は同期 CPU バウンド） |

---

## 1. フェーズ構成

| Phase | 内容 | 依存 |
|---|---|---|
| **P0** | ベースラインベンチマーク作成（実装前の計測基盤） | — |
| **P1** | LintEngine スレッドセーフティ監査・検証テスト | — |
| **P2** | LintEngine per-thread 分離（ThreadLocal 方式） | P1 |
| **P3** | CheckCommand 並列化 + 出力順安定化 | P2 |
| **P4** | 結合テスト・ベンチマーク検証（P0 との比較） | P0, P3 |
| **P5** | CLI spec・ドキュメント更新 | P4 |

---

## 2. Phase 0: ベースラインベンチマーク作成

### 2.0 目的

並列化実装の前に、逐次処理での複数ファイル lint スループットを計測するベンチマークを用意する。P4 で並列化後の同一ベンチマークと比較し、改善効果を定量評価する。

### 2.1 ベンチマーククラス: `MultiFileLintBenchmark`

既存の `CoreLintBenchmark` は単一ファイル × 単一エンジンの micro-benchmark。並列化の効果測定には「複数ファイルを一括処理するスループット」を計測する別ベンチマークが必要。

```csharp
[MemoryDiagnoser]
[RankColumn]
public class MultiFileLintBenchmark
{
    public enum FileCount
    {
        F1,    // 1 ファイル（fast path ベースライン）
        F10,   // 10 ファイル
        F50,   // 50 ファイル
    }

    [Params(FileCount.F1, FileCount.F10, FileCount.F50)]
    public FileCount Count { get; set; }

    private byte[][] _yamlFiles = [];
    private string[] _filePaths = [];

    [GlobalSetup]
    public void Setup()
    {
        var n = Count switch
        {
            FileCount.F1 => 1,
            FileCount.F10 => 10,
            FileCount.F50 => 50,
            _ => 1,
        };

        _yamlFiles = new byte[n][];
        _filePaths = new string[n];

        for (var i = 0; i < n; i++)
        {
            // 各ファイルは Medium サイズ (6 jobs × 8 steps) で異なる内容
            var yaml = WorkflowYamlBuilder.Build(
                jobCount: 6, stepsPerJob: 8,
                nameSuffix: $"-file{i}");
            _yamlFiles[i] = Encoding.UTF8.GetBytes(yaml);
            _filePaths[i] = $".github/workflows/bench-{i}.yml";
        }
    }

    /// <summary>逐次パス: 1 エンジンで for ループ（現行実装相当）</summary>
    [Benchmark(Baseline = true, Description = "Sequential (for loop)")]
    public int CheckSequential()
    {
        var engine = new LintEngine();
        var total = 0;
        for (var i = 0; i < _yamlFiles.Length; i++)
        {
            var result = engine.Check(_yamlFiles[i], _filePaths[i]);
            total += result.Diagnostics.Length;
        }
        return total;
    }

    /// <summary>並列パス: ThreadLocal + Parallel.ForEach（並列化後に追加）</summary>
    [Benchmark(Description = "Parallel (ThreadLocal)")]
    public int CheckParallel()
    {
        using var engines = new ThreadLocal<LintEngine>(
            () => new LintEngine(), trackAllValues: false);
        var slots = new int[_yamlFiles.Length];

        Parallel.For(0, _yamlFiles.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var result = engines.Value!.Check(_yamlFiles[i], _filePaths[i]);
                slots[i] = result.Diagnostics.Length;
            });

        var total = 0;
        for (var i = 0; i < slots.Length; i++) total += slots[i];
        return total;
    }
}
```

### 2.2 計測項目

| メトリクス | 説明 |
|---|---|
| **Mean (ms)** | 全ファイル処理の平均所要時間 |
| **Allocated (KB)** | GC ヒープ割り当て量 |
| **Ratio** | Sequential を Baseline とした Parallel の相対速度 |

### 2.3 期待結果

- **P0 時点（実装前）**: Sequential と Parallel の両メソッドが存在するが、Parallel は「正しく動作する」ことの確認のみ（LintEngine がスレッドセーフでないため P2 完了後に初めて信頼できる計測になる）
- **P4 時点（実装後）**: F10/F50 で Parallel が Sequential より有意に高速であること。F1 では差がないか Parallel がわずかに遅い（オーバーヘッド）ことを確認

### 2.4 実行方法

```shell
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter *MultiFileLint*
```

---

## 3. Phase 1: LintEngine スレッドセーフティ監査

### 3.1 目的

LintEngine および依存コンポーネントのスレッドセーフでない箇所を特定し、並列化で発生しうるレースを検証テストで実証する。

### 3.2 監査結果（事前調査済み）

| コンポーネント | スレッドセーフ | 理由 |
|---|---|---|
| `LintEngine` インスタンス | ✗ | 可変リスト・辞書・visitor を Check ごとに Clear/Reset して再利用 |
| `LintEngine._effectiveConfig` | ✗ | 同一インスタンス内の共有 `LintConfig` を毎回上書き |
| `WorkflowVisitor` | ✗ | `_passes` リストを Reset/AddPass で毎回再構築 |
| `IRule` インスタンス | ✗ | ルールは内部に診断リストを蓄積。同一ルールオブジェクトの並行 Visit は不可 |
| `WorkflowParser` | ○ | static メソッド + `[ThreadStatic]` バッファ + per-call アリーナ |
| `RuleCatalog` | ○ | static readonly テーブル + ファクトリメソッド |
| 生成データ | ○ | static readonly 読み取り専用 |
| `ExpressionParser` | ○ | `[ThreadStatic]` キャッシュ |
| `ExpressionSemanticAnalyzer` | ○ | static lookup + ローカルバッファ |

### 3.3 検証テスト

並行 `Check()` でレース状態が発生することを実証するストレステストを作成する。

```
テスト名: ConcurrentCheckOnSameEngine_ShouldDetectRace
内容:
  1. 1つの LintEngine インスタンスを作成
  2. 複数ファイルの UTF-8 YAML を用意
  3. Parallel.ForEach で同一エンジンに同時 Check() 呼び出し
  4. 結果の不整合またはクラッシュを検出 → テストとしては「現状の不安全性を文書化」する目的
```

---

## 4. Phase 2: LintEngine per-thread 分離

### 4.1 方式

`ThreadLocal<LintEngine>` を使用して、スレッドごとに独立した LintEngine インスタンスを保持する。

```csharp
// CheckCommand 内（並列パス）
using var engines = new ThreadLocal<LintEngine>(() => new LintEngine(), trackAllValues: false);

Parallel.ForEach(resolvedFiles, parallelOptions, (filePath, _, index) =>
{
    var engine = engines.Value!;
    // ... engine.Check(utf8Yaml, filePath, lintConfig) ...
});
```

### 4.2 設計判断

| 項目 | 判断 | 理由 |
|---|---|---|
| `ThreadLocal<LintEngine>` | 採用 | Parallel.ForEach のワーカスレッドに1:1で紐づく。ルールオブジェクト・内部バッファが完全分離される |
| LintEngine のスレッドセーフ化 | 不採用 | ロック導入はパフォーマンス劣化。現行の clear/reset パターンを壊す変更は広範囲に影響 |
| `ObjectPool<LintEngine>` | 不採用 | `Parallel.ForEach` のスレッド再利用パターンでは `ThreadLocal` のほうが自然 |

### 4.3 アロケーション考慮

- LintEngine コンストラクタで `RuleCatalog.CreateDefaultRules()` が呼ばれる（ルールインスタンス生成）
- スレッド数分の LintEngine が生成されるが、`Parallel.ForEach` はスレッドを再利用するため実際の生成数は `MaxDegreeOfParallelism` 以下
- 1ファイル fast path では `ThreadLocal` を使わず直接 `new LintEngine()` → 追加アロケーションなし

### 4.4 P2 実装結果（監査済み）

| 懸念事項 | 結果 |
|---|---|
| `LintConfig` スレッドセーフティ | ✅ **安全**。各 `LintEngine` は自身の `_effectiveConfig = new LintConfig()` を保持。呼び出し元の `lintConfig` は `PrepareForRun` で読み取り専用としてコピーされる。`_expressionCache`（`Dictionary`）と `_lineStarts` は `_effectiveConfig` 上にあり、スレッド間で共有されない |
| `Diagnostic` 所有権 | ✅ **解決**。`BuildLintResult` は `PooledBuffer.DetachArray()` で内部リストから分離した配列を生成し、`AstArena` に登録する。アリーナは `Check()` ループ中に dispose されないため配列は有効だが、安全のため並列スロットパターンでは `CopyDiagnostics()` を使用する |
| コンフィグ診断フィールド | ✅ **削減済み**。`_ruleNormDiagnostics` + `_suppressionDiagnostics` + `_exclusionDiagnostics` → 単一 `_configDiagnostics` に統合（P1 で実施） |
| テスト網羅性 | ✅ **9テスト通過**。スロットパターン出力順安定性、`CopyDiagnostics` 保持、共有 `LintConfig` 安全性を検証済み |

---

## 5. Phase 3: CheckCommand 並列化 + 出力順安定化

### 5.1 全体構成

```
1ファイルの場合:
  既存の逐次パスをそのまま使用（fast path）

2ファイル以上の場合:
  a. ファイルリストと同サイズの結果スロット配列を確保
  b. Parallel.ForEach でインデックス付き並列処理
  c. 各スレッドが自分のスロットに結果を書き込み（ロック不要）
  d. 全完了後、スロット順に結果を集約 → 出力順 = 入力順
```

### 5.2 結果スロット方式

```csharp
// 入力順にスロットを確保（actionlint の workspace[] に相当）
var slots = new FileCheckResult[resolvedFiles.Length];

Parallel.ForEach(
    Enumerable.Range(0, resolvedFiles.Length),
    parallelOptions,
    index =>
    {
        var engine = engines.Value!;
        var filePath = resolvedFiles[index];
        var utf8Yaml = File.ReadAllBytes(filePath);
        var result = engine.Check(utf8Yaml, filePath, lintConfig);

        // CopyDiagnostics で caller-owned コピーを取得（アリーナ寿命に依存しない）
        slots[index] = new FileCheckResult(result.CopyDiagnostics(), filePath, utf8Yaml);
    });

// 入力順で集約
for (var i = 0; i < slots.Length; i++)
{
    allDiagnostics.AddRange(slots[i].Diagnostics);
    sourceMap?.TryAdd(slots[i].FilePath, slots[i].Utf8Yaml);
}
```

### 5.3 FileCheckResult 型

```csharp
// 軽量な結果格納構造体
internal readonly struct FileCheckResult
{
    public readonly Diagnostic[] Diagnostics; // CopyDiagnostics() の戻り値（caller-owned）
    public readonly string FilePath;
    public readonly byte[]? Utf8Yaml; // sourceMap 用（null 可）

    public FileCheckResult(Diagnostic[] diagnostics, string filePath, byte[]? utf8Yaml)
    {
        Diagnostics = diagnostics;
        FilePath = filePath;
        Utf8Yaml = utf8Yaml;
    }
}
```

### 5.4 Verbose 出力

- `verbose` モード時の `Console.Error.WriteLine($"checking {filePath}...")` は並列実行中にインターリーブするが、stderr は診断目的であり順序保証は不要
- 診断結果（stdout）はスロット順集約で順序安定

### 5.5 stdin 処理

- stdin (`"-"`) はファイル解決時点で resolvedFiles に含まれる
- stdin がある場合は**逐次 fast path にフォールバック**する（stdin 読み取りは1回限り、並列化不可）

### 5.6 fix コマンド

- **変更なし**。逐次処理を維持する
- 理由：
  - ファイル書き戻し + 反復 re-check パスがあり、各ファイル内で LintEngine を再利用する
  - ネットワーク修正（pin remediation）は既に内部で SemaphoreSlim 並列化済み
  - 並列化のメリット（CPU 待ち削減）よりファイル I/O 競合管理のコストが上回る

---

## 6. Phase 4: 結合テスト・ベンチマーク検証

### 6.1 結合テスト

| テスト | 内容 | 状態 |
|---|---|---|
| 出力順安定性テスト | 同一ファイルセットで5回並列実行し、診断出力が毎回同一であることを検証 | ✅ `ParallelSlotPattern_RepeatedRuns_ProduceIdenticalOutput` |
| 並列結果一致テスト | 逐次パス（1ファイルずつ）と並列パスで同一の診断結果が得られることを検証 | ✅ `FullPipeline_ParallelCheckWithIgnoreFilter` + P1 `DiagnosticContentMatchesSequential` |
| 大量ファイルテスト | 100ファイルで並列実行し、クラッシュ・デッドロックが発生しないことを検証 | ✅ `StressTest_NoCrashOrDeadlock` (100 files, Repeat(3)) |
| LintResult 所有権テスト | CopyDiagnostics の結果が後続 Check 呼び出し後も有効であることを検証 | ✅ `CopyDiagnostics_SurvivesSubsequentCheckCalls` |

テスト合計: 11/11 通過（P1: 6, P2: 3, P4: 2）

### 6.2 ベンチマーク比較（P4 実測結果）

Machine: AMD Ryzen 9 7950X3D, 16C/32T, .NET 10.0.6, ShortRun

| Count | Method | Mean | Ratio | Allocated | Alloc Ratio |
|---|---|---:|---:|---:|---:|
| **F1** | Sequential | 1.444 ms | 1.00 | 192 KB | 1.00 |
| **F1** | Parallel | 1.398 ms | 0.97 | 194 KB | 1.01 |
| **F10** | Sequential | 14.772 ms | 1.00 | 1,469 KB | 1.00 |
| **F10** | Parallel | 2.830 ms | **0.19** | 1,926 KB | 1.31 |
| **F50** | Sequential | 74.468 ms | 1.00 | 7,142 KB | 1.00 |
| **F50** | Parallel | 10.796 ms | **0.15** | 8,702 KB | 1.22 |

**分析:**

- **F1**: 差なし（Ratio 0.97）。fast path が正しく機能
- **F10**: **5.2x 高速化**（14.8ms → 2.8ms）。アロケーション +31%（ThreadLocal LintEngine 生成分）
- **F50**: **6.9x 高速化**（74.5ms → 10.8ms）。アロケーション +22%
- アロケーション増加は ThreadLocal 分の LintEngine コンストラクタコストのみ。ファイル数が増えるほど相対比率が下がる（F50: +22% < F10: +31%）

---

## 7. Phase 5: CLI spec・ドキュメント更新

### 7.1 CLI spec 更新

`Seiton_CLI_spec.md` に以下を追記：

- check コマンドの並列実行モデル（デフォルト `ProcessorCount`、1ファイル時は逐次）
- fix コマンドは逐次処理を維持する旨
- 出力順序の安定性保証（入力ファイル順）

### 7.2 Linter spec 更新

`Seiton_Linter_spec.md` / `Seiton_Linter_csharp_spec.md` に以下を追記：

- `LintEngine` のスレッドセーフティ契約（同一インスタンスの並行 Check は不可、per-thread 分離が必要）

---

## 8. 実装上の注意事項

### 8.1 Diagnostic の所有権（解決済み）

`LintResult.Diagnostics` は `DiagnosticList`（`Diagnostic[]` + count のラッパー構造体）を返す。この配列は `PooledBuffer.DetachArray()` で分離され、`AstArena` に登録される。

- **アリーナ寿命**: 各 `Check()` は `AstArena.Rent()` で新しいアリーナを取得する。アリーナは `ParseResult` 経由で `LintResult` に保持され、Check ループ中は dispose されない。したがって次の `Check()` が前回結果の配列を無効化することはない。
- **並列パスでの対応**: 安全のため `CopyDiagnostics()` を使用してアリーナ寿命に依存しない caller-owned コピーを取得する。`CopyDiagnostics()` は `Diagnostics.AsSpan().ToArray()` で新しい配列を割り当てる。
- **逐次パスとの整合**: 現行 CheckCommand は `allDiagnostics.AddRange(result.Diagnostics)` で即座に値をコピーしているため問題なし。

### 8.2 LintConfig の共有（解決済み）

- `LintConfig` インスタンスはスレッド間で共有される（読み取り専用として）
- 各 `LintEngine` は自身の `_effectiveConfig = new LintConfig()` フィールドを保持。`PrepareForRun` で呼び出し元 `lintConfig` の設定値（`Fix`, `Network`, `Output`, `Verbose`）をコピーするが、呼び出し元オブジェクト自体は変更しない
- `_expressionCache`（`Dictionary<long, ExpressionCacheEntry>`）と `_lineStarts`（`int[]`）は `_effectiveConfig` 上に存在し、スレッド間で共有されない
- **結論**: 呼び出し元の `lintConfig` は並列パスで安全に共有可能。テスト `SharedLintConfig_SafeAcrossThreadLocalEngines` で検証済み

### 8.3 ThreadLocal の Dispose

`ThreadLocal<LintEngine>` は `Dispose()` が必要。`using` 宣言で確保する。
LintEngine 自体は `IDisposable` でないため、ThreadLocal の Dispose だけで十分。
