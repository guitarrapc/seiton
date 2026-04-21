# AST Zero-Allocation 計画

## 1. 現状分析

### 1.1 ベンチマーク基準値（class ベース AST、Phase 11）

**ParsingBenchmark:**

| Size | Time | Allocated |
|------|------|-----------|
| Small (1 job × 3 steps) | 69 μs | 12,080 B |
| Medium (6 jobs × 8 steps) | 1,289 μs | 83,515 B |
| Large (20 jobs × 12 steps) | 14,610 μs | 376,781 B |

**参考 — VYaml raw event scan（AST なし）:**

| Size | Time | Allocated |
|------|------|-----------|
| Small | 19 μs | 0 B |
| Medium | 113 μs | 0 B |
| Large | 503 μs | 0 B |

→ AST 構築 + ルール検証で VYaml raw 比 **29 倍遅く、377 KB 割り当て**。

### 1.2 struct 変換の失敗記録

StringNode を class → struct に変換した結果、アロケーションが **+22.8%** 増加し速度も後退した。

| Size | class (Phase 11) | struct (Phase 10) | Change |
|------|-------------------|-------------------|--------|
| Small | 12,080 B | 14,360 B | +18.9% |
| Medium | 83,515 B | 102,363 B | +22.6% |
| Large | 376,781 B | 462,754 B | +22.8% |

**原因:** `StringNode?`（Nullable<StringNode>）が含有クラス（Job, Step 等）に ~72B の構造体をインライン埋め込みし、従来の 8B ポインタ参照より遥かに大きくなった。struct は dense array かスタック上で使わなければ逆効果。

### 1.3 アロケーション内訳の推定（Large ワークフロー: 20 jobs × 12 steps = 240 steps）

| カテゴリ | 推定件数 | 推定割り当て | 全体比 |
|----------|---------|-------------|--------|
| StringNode オブジェクト | 600–1000 | 48–80 KB | 20% |
| 複合ノード（Job, Step, Event, etc.） | 300+ | 40–60 KB | 15% |
| List\<T\> → ToArray() | 100+ | 50–80 KB | 18% |
| Dictionary\<K,V\> | 50+ | 40–60 KB | 15% |
| Utf8String byte[] コピー | 200+ | 5–15 KB | 3% |
| Diagnostic 配列・文字列 | 少数 | 10–20 KB | 5% |
| その他（中間変数、delegate等） | — | 残り | 24% |

→ **StringNode + コレクション + 複合ノード** が全体の 70% 近くを占める。

---

## 2. VYaml のアプローチ分析

VYaml のソースコード（`.references/VYaml/`）を調査した結果、以下の設計原則が確認された。

### 2.1 VYaml が AST を構築しない理由

VYaml は **streaming event parser** であり、AST（中間木構造）を一切構築しない。

```
YAML bytes → Utf8YamlTokenizer (tokenize) → YamlParser (events) → Formatter (直接 typed object)
```

- `YamlParser` は `ref struct` — パーサー自体がスタック上に存在。
- イベント型は `ParseEventType` enum（Scalar, MappingStart, MappingEnd, ...）。
- Scalar データは `Scalar` クラスにプールされ、次の `Read()` 呼び出しで自動返却。
- Deserialization は `IYamlFormatter<T>.Deserialize(ref YamlParser, ...)` で streaming からオブジェクトへ直接変換。

### 2.2 VYaml の主要な零アロケーション技法

| 技法 | 実装 | 効果 |
|------|------|------|
| **ref struct パーサー** | `YamlParser` はスタック上 | パーサーインスタンスのヒープ割り当て回避 |
| **ScalarPool** | `ConcurrentQueue<Scalar>` + fast-path slot | Scalar バッファをパース間でプール再利用 |
| **ThreadStatic バッファ** | `anchors`, `stateStack` を thread-local 再利用 | パースごとのコレクション生成を完全排除 |
| **ExpandBuffer\<T\>** | 自前の growable buffer（ArrayPool 不使用） | List\<T\> 相当を割り当てなしで提供 |
| **UTF-8 span ベース比較** | `ReadOnlySpan<byte>` 上の直接比較 | string 変換なし |

### 2.3 Seiton への適用可能性

| VYaml 技法 | Seiton 適用 | 制約 |
|------------|-------------|------|
| ref struct パーサー | ✅ VYamlStreamAdapter 既に ref struct | — |
| ScalarPool | ✅ 適用可能（Utf8String や内部バッファ） | ライフサイクル管理が必要 |
| ThreadStatic バッファ | ✅ List/Dict 中間蓄積に適用可能 | thread-safety 注意 |
| ExpandBuffer\<T\> | ✅ List\<T\> → ToArray() 置換候補 | 初期容量チューニングが重要 |
| AST レス設計 | ❌ Lint ルールが AST の横断的アクセスを要求 | 下記で代替案を検討 |

**AST を完全に排除できない理由:**
1. `NeedsGraphRule` は全 Job の参照グラフを構築する（ストリーミングでは後方参照不可）。
2. `ReusableWorkflowRule` は別ファイルの Workflow AST を参照する。
3. `RunContextDirectUseAnalyzer` は Workflow/Job/Step 各レベルの Env を合成する。
4. Lint Fix はオフセットベースのテキスト編集を生成する（位置情報が正確な AST が必要）。

→ **AST は必要だが、その表現を根本的に変える必要がある。**

---

## 3. 零アロケーション AST の設計方針

### 3.1 核心アイデア: Arena-Backed Flat Store

**現状:** 各 AST ノードが個別のヒープオブジェクト → 数百～数千のアロケーション。

**目標:** 全 AST データを少数の事前確保バッファに格納し、ノードはバッファへのインデックスで参照する。

```
現状:
  Workflow → new Job → new Step → new StringNode  (1000+ ヒープオブジェクト)
           → new Dictionary                        (50+ コレクション)
           → new byte[]                             (200+ Utf8String)

目標:
  AstArena (1 object, pre-sized buffers)
  ├── StringNodeData[]     ← 全 StringNode のデータ
  ├── BoolNodeData[]       ← 全 BoolNode のデータ
  ├── JobData[]            ← 全 Job のデータ
  ├── StepData[]           ← 全 Step のデータ
  ├── EventData[]          ← 全 Event のデータ
  ├── MapEntry[]           ← 全 Dictionary のフラット化エントリ
  └── ChildIndex[]         ← 全リスト/配列のフラット化インデックス
```

### 3.2 設計詳細

#### 3.2.1 Handle 型（型安全なインデックス）

```csharp
// 4 バイトの struct — nullable は int.MinValue などの sentinel で表現
readonly record struct StringNodeId(int Index)
{
    public static readonly StringNodeId None = new(-1);
    public bool HasValue => Index >= 0;
}

readonly record struct BoolNodeId(int Index)
{
    public static readonly BoolNodeId None = new(-1);
    public bool HasValue => Index >= 0;
}

readonly record struct JobId(int Index);
readonly record struct StepId(int Index);
readonly record struct EventId(int Index);
```

→ nullable class 参照（8B + 16B header）が 4B の struct に。Nullable 判定は sentinel（-1）。

#### 3.2.2 Flat Data Store（AstArena）

```csharp
sealed class AstArena : IDisposable
{
    // ---- Scalar stores ----
    struct StringNodeData
    {
        public Utf8Slice Value;      // 8B  — offset/length into source bytes
        public bool Quoted;          // 1B
        public StringNodeId Expression; // 4B — index to another StringNode, or -1
        public TextRange Range;      // 24B
    }
    // Total: 37B padded to 40B — vs current 80B (class + header)

    struct BoolNodeData
    {
        public bool Value;           // 1B
        public StringNodeId Expression; // 4B
        public TextRange Range;      // 24B
    }

    struct IntNodeData { public long Value; public StringNodeId Expression; public TextRange Range; }
    struct FloatNodeData { public double Value; public StringNodeId Expression; public TextRange Range; }

    // Dense arrays — pre-sized, grow if needed
    StringNodeData[] _stringNodes;
    int _stringNodeCount;

    BoolNodeData[] _boolNodes;
    int _boolNodeCount;

    // ... 同様に IntNodeData[], FloatNodeData[]

    // ---- Collection store ----
    // 全リスト・配列を 1 つの共有バッキング配列に集約
    // 各リストは (startIndex, count) で参照
    int[] _childIndices;  // StepId, EventId, StringNodeId などの値を格納
    int _childIndexCount;

    // ---- Map store ----
    // Dictionary<Utf8String, T> → フラットなソート済みエントリ列
    struct MapEntry
    {
        public Utf8Slice Key;  // source bytes への参照（コピーなし）
        public int ValueIndex; // 型に応じたストアへのインデックス
    }
    MapEntry[] _mapEntries;
    int _mapEntryCount;

    // ---- Composite stores ----
    // Job, Step, Event 等は struct として dense array に格納
    // 詳細は Phase 2/3 で定義

    // ---- Source reference ----
    byte[] _source; // 元の YAML UTF-8 バイト列（Utf8Slice 解決用）
}
```

#### 3.2.3 Lint ルールへの公開 API（Typed View）

Lint ルールは `AstArena` を直接操作しない。型安全な view 経由でアクセスする。

```csharp
// 旧 API:
//   job.Name?.Value.AsSpan(source)
//   job.Steps?[i].Exec is ExecAction action

// 新 API:
//   arena.GetStringSpan(job.Name, source)   // StringNodeId → ReadOnlySpan<byte>
//   arena.GetStep(job.StepsStart + i)        // StepId → StepData ref

// ヘルパーメソッドで使い勝手を維持
public readonly ReadOnlySpan<byte> GetStringValue(StringNodeId id)
{
    if (!id.HasValue) return ReadOnlySpan<byte>.Empty;
    ref var node = ref _stringNodes[id.Index];
    return node.Value.AsSpan(_source);
}

public readonly TextRange GetStringRange(StringNodeId id) => _stringNodes[id.Index].Range;
public readonly bool GetStringQuoted(StringNodeId id) => _stringNodes[id.Index].Quoted;
```

#### 3.2.4 コレクションの表現

**List\<T\> → Span Range:**

```csharp
// 旧: IReadOnlyList<Step> Steps
// 新: SpanRange Steps  (start index + count into shared backing)

readonly record struct SpanRange(int Start, int Count)
{
    public static readonly SpanRange Empty = new(0, 0);
    public bool IsEmpty => Count == 0;
}

// AstArena 側:
public ReadOnlySpan<StepId> GetSteps(SpanRange range)
    => _stepIds.AsSpan(range.Start, range.Count);
```

**Dictionary\<Utf8String, T\> → Sorted Flat Map:**

```csharp
// 旧: IReadOnlyDictionary<Utf8String, Job> Jobs
// 新: MapRange Jobs  (start index + count into shared MapEntry[])

readonly record struct MapRange(int Start, int Count);

// 検索は binary search（O(log n)）— 小マップならリニアスキャンの方が速い
public bool TryGetJob(MapRange range, ReadOnlySpan<byte> key, out JobId jobId)
{
    var entries = _mapEntries.AsSpan(range.Start, range.Count);
    for (int i = 0; i < entries.Length; i++)
    {
        if (entries[i].Key.AsSpan(_source).SequenceEqual(key))
        {
            jobId = new JobId(entries[i].ValueIndex);
            return true;
        }
    }
    jobId = default;
    return false;
}
```

### 3.3 期待されるアロケーション削減

| カテゴリ | 現状 (Large) | Arena 後 | 削減 |
|----------|-------------|----------|------|
| StringNode オブジェクト | ~60 KB (600+ objects) | 0 (flat array) | -100% |
| BoolNode/IntNode/FloatNode | ~15 KB | 0 (flat array) | -100% |
| 複合ノード (Job, Step, etc.) | ~50 KB | 0 (flat array) | -100% |
| List\<T\> + ToArray() | ~65 KB | 0 (shared backing) | -100% |
| Dictionary\<K,V\> | ~50 KB | 0 (flat map) | -100% |
| Utf8String byte[] copy | ~10 KB | 0 (Utf8Slice に統一) | -100% |
| AstArena バッファ群 | 0 | ~100–150 KB (数個の配列) | — |
| **合計** | **~377 KB (1000+ objects)** | **~100–150 KB (5–10 arrays)** | **~60–70%削減** |

GC 圧力は 1000+ 個別オブジェクト → 5–10 配列に劇的に減少。Gen0 コレクション頻度が大幅低下。

### 3.4 ThreadStatic プール化による追加最適化

VYaml の ThreadStatic パターンを適用し、AstArena のバッキング配列をパース間で再利用する。

```csharp
sealed class AstArena : IDisposable
{
    [ThreadStatic] static AstArena? _cachedInstance;

    public static AstArena Rent(byte[] source)
    {
        var arena = _cachedInstance ?? new AstArena();
        _cachedInstance = null;
        arena.Reset(source);
        return arena;
    }

    public void Dispose()
    {
        // 配列は保持したままカウンタのみリセット
        _cachedInstance ??= this;
    }
}
```

→ 2 回目以降のパースは **アロケーション 0** に近づく。

---

## 4. 段階的実装計画

### Phase 1: コレクション最適化（低リスク、中効果）

**目標:** List\<T\>→ToArray() と Dictionary 割り当てを削減。AST の公開 API は変更しない。

**内容:**
1. ThreadStatic な `ExpandBuffer<T>` を導入し、パーサー内の `List<T>` を置換。
   - `List<Step>` → `ExpandBuffer<Step>` (ThreadStatic)
   - `List<Event>` → `ExpandBuffer<Event>` (ThreadStatic)
   - `List<StringNode>` → `ExpandBuffer<StringNode>` (ThreadStatic)
   - `List<Diagnostic>` → `ExpandBuffer<Diagnostic>` (ThreadStatic)
2. `ToArray()` を `ExpandBuffer.ToArray()` に変更（内部で ArrayPool.Rent → コピー → Return）。
3. 小さな Dictionary（permissions, env 等）を sorted array ベースの `FlatMap<TKey,TValue>` に置換。
   - GitHub Actions の map は通常 2–20 エントリ → リニアスキャンが Dictionary より高速。
4. `Utf8String` コンストラクタを `Utf8Slice` ベースに切り替え（byte[] コピー排除）。
   - Dictionary キーとしての `Utf8String` → `Utf8Slice` ベースに変更し、source bytes 参照を保持。

**推定削減:** 30–40%（Large: 377 KB → ~230–260 KB）
**リスク:** 低。内部実装変更のみ。公開 AST 型は変更なし。

### Phase 2: Scalar Node の Flat Store 化（中リスク、高効果）

**目標:** StringNode/BoolNode/IntNode/FloatNode をヒープオブジェクトから dense flat array に移行。

**内容:**
1. `StringNodeId` / `BoolNodeId` / `IntNodeId` / `FloatNodeId` handle struct を導入。
2. `AstArena` に scalar data の dense array を実装。
3. AST の複合ノード（Job, Step, Event, Workflow 等）の scalar 型プロパティを handle に変更。
   - `StringNode? Name` → `StringNodeId Name`
   - `BoolNode? ContinueOnError` → `BoolNodeId ContinueOnError`
4. Lint ルールの scalar アクセスを `arena.GetStringValue(id)` パターンに移行。
5. `WorkflowVisitor` に `AstArena` 参照を追加し、各ルールで利用可能にする。

**消費者への影響:**
- Lint ルールのプロパティアクセスが `node.Value.AsSpan(source)` → `arena.GetStringValue(node.Name)` に変わる。
- パターンマッチ `if (step.Exec is ExecAction action)` → `if (arena.GetStepExecKind(step) == StepExecKind.Action)` に変わる可能性。
- 移行は機械的だが量が多い（~30 ルールファイル）。

**推定追加削減:** 25–35%（Large: ~230 KB → ~150–170 KB）
**リスク:** 中。AST 公開 API が変わるため、全 Lint ルールの修正が必要。

### Phase 3: 複合ノードの Arena 化 + ThreadStatic プール（高リスク、完成形）

**目標:** 全 AST ノードを Arena に格納。パース間で Arena を再利用し、アロケーション 0 に近づける。

**内容:**
1. Job, Step, Event 等の複合ノードも struct 化し、AstArena の dense array に格納。
2. 全コレクション（Steps, Events, Jobs, Map entries）を共有バッキング配列に統合。
3. ThreadStatic な `AstArena.Rent()/Dispose()` パターンで配列をパース間再利用。
4. `ParseResult` を `AstArena` の参照に変更し、ライフサイクルを管理。
5. Lint 完了後に `AstArena.Dispose()` で配列を返却。

**ライフサイクル管理:**
```
WorkflowParser.Parse()
  → AstArena.Rent(source)
  → parse into arena
  → return ParseResult { Arena = arena }

LintEngine.Lint()
  → use arena for rule execution
  → arena.Dispose()  // return buffers to thread-local cache
```

**推定最終値:** Large: ~100–150 KB（初回）、~0 KB（2 回目以降、ThreadStatic 再利用時）
**リスク:** 高。AST の消費モデルが根本的に変わる。段階的に進める必要がある。

---

## 5. 代替案・不採用案の記録

### 5.1 class → struct 単純変換（不採用 ✗）

既に試行済み。Nullable struct のインライン膨張で逆効果。上記「1.2 struct 変換の失敗記録」参照。

### 5.2 AST 排除 + 完全ストリーミング（不採用 ✗）

VYaml スタイルの AST レス設計は理論上最速だが、Seiton の Lint ルール要件と両立しない。

- `NeedsGraphRule` は全 Job の後方参照が必要。
- `ReusableWorkflowRule` は別ファイルの AST を参照。
- `RunContextDirectUseAnalyzer` は Workflow/Job/Step 横断の Env 合成が必要。
- 複数ルールが同一 AST に対して独立に走る（single-pass ストリーミングでは 1 ルールしか走れない）。

→ ストリーミングへの全面移行はルールエンジンの根本再設計が必要で Non-Viable。

### 5.3 ObjectPool\<T\> による class プール（部分採用候補 △）

全 AST クラスを `ObjectPool<T>` でプールし、パース後に返却する方式。

**利点:** 消費者 API 変更なし、実装が比較的容易。
**欠点:**
- 初回パースは全オブジェクト割り当て（プールが空）。
- プールのオーバーヘッド（借用/返却のブックキーピング）。
- mutable なリセットが必要（init-only プロパティとの相性が悪い）。
- GC 圧力は減るが、メモリ使用量は増える可能性。

→ Phase 3 の ThreadStatic Arena が上位互換。ただし Phase 1 の暫定措置として Node クラスの一部プール化は有効。

### 5.4 Roslyn Green/Red Tree パターン（不採用 ✗）

Roslyn の不変 Green Tree + 位置付き Red Tree パターンは、IDE の incremental update に最適化されたもの。
Seiton は single-pass parse → lint → dispose のバッチ処理であり、incremental update は不要。
Green/Red Tree のオーバーヘッド（2 倍のノード数、追加のインダイレクション）は不必要。

---

## 6. リスク・考慮事項

### 6.1 Lint ルール移行コスト

Phase 2 以降は ~30 ルールファイルの修正が必要。各ルールのプロパティアクセスパターンが変わる。
機械的な置換が大半だが、テストカバレッジが重要。

**緩和策:** Arena 上に旧 API 互換のラッパー（`StringNodeView` struct）を提供し、段階的に移行。

### 6.2 デバッグ体験の悪化

インデックスベースの AST はデバッガでの可読性が低下する。

**緩和策:** `DebuggerDisplay` 属性で human-readable な表示を提供。`AstArena.Dump()` メソッドでデバッグ時に木構造を出力。

### 6.3 Arena のサイジング

小さなワークフローに対して大きすぎる初期アロケーションは無駄。

**緩和策:** YAML のバイト数から初期容量をヒューリスティックに推定。例: `stringNodeCapacity = yamlBytes.Length / 40`。

### 6.4 Expression テスト

Expression パーサーは AST とは独立だが、Expression 結果の格納（`StringNode.Expression` → `StringNodeId`）に影響。

**緩和策:** Phase 2 で Expression 格納を handle 化する際、既存 Expression テストの全パスを確認。

---

## 7. 検証計画

各 Phase 完了時に以下を測定し、前 Phase と比較:

1. **BenchmarkDotNet ParsingBenchmark** — Allocated bytes, Mean time（Small/Medium/Large）。
2. **BenchmarkDotNet LintBenchmark** — Allocated bytes（FixEnabled=false/true、Small/Medium/Large）。
3. **GC コレクション回数** — Gen0/Gen1 回数の変化。
4. **全テストパス** — `dotnet test` の Green 確認。
5. **プロファイラ確認** — dotMemory/PerfView で主要アロケーションサイトが排除されたことを確認。

**成功基準:**

| Phase | Allocated (Large Parse) | Speed (Large Parse) |
|-------|------------------------|---------------------|
| 現状 | 377 KB | 14.6 ms |
| Phase 1 完了 | ≤ 260 KB | ≤ 14.6 ms (同等以上) |
| Phase 2 完了 | ≤ 170 KB | ≤ 13 ms |
| Phase 3 完了 | ≤ 150 KB (初回) / ~0 (再利用) | ≤ 12 ms |

---

## 8. 推奨実装順序

```
Phase 1 (低リスク) ──→ ベンチマーク検証 ──→ Phase 2 (中リスク) ──→ ベンチマーク検証 ──→ Phase 3 (高リスク)
     │                                           │                                           │
     ├─ ExpandBuffer 導入                         ├─ Handle struct 定義                        ├─ 複合ノード struct 化
     ├─ FlatMap 導入                              ├─ AstArena scalar stores                   ├─ 全ノード Arena 格納
     ├─ Utf8String → Utf8Slice                    ├─ 複合ノードのプロパティ変更                  ├─ ThreadStatic 再利用
     └─ ArrayPool 活用                             └─ Lint ルール移行                           └─ ライフサイクル管理
```

Phase 1 だけでも 30–40% 削減が見込め、リスクは低い。Phase 2 以降は Phase 1 の結果を見て判断する。
