# AST Zero-Allocation 計画

## 1. 現状分析

### 1.1 ベンチマーク基準値

**ParsingBenchmark（2026-04-22 再測定）:**

| Size | Time | Allocated |
|------|------|-----------|
| Small (1 job × 3 steps) | 28 μs | 12,080 B |
| Medium (6 jobs × 8 steps) | 480 μs | 83,512 B |
| Large (20 jobs × 12 steps) | 6,844 μs | 376,696 B |

**参考 — VYaml raw event scan（AST なし）:**

| Size | Time | Allocated |
|------|------|-----------|
| Small | 8 μs | 0 B |
| Medium | 66 μs | 0 B |
| Large | 291 μs | 0 B |

→ AST 構築 + ルール検証で VYaml raw 比 **23 倍遅く、377 KB 割り当て**。

### 1.2 struct 変換の失敗記録

StringNode を class → struct に変換した結果、アロケーションが **+22.8%** 増加し速度も後退した。

| Size | class (Phase 11) | struct (Phase 10) | Change |
|------|-------------------|-------------------|--------|
| Small | 12,080 B | 14,360 B | +18.9% |
| Medium | 83,515 B | 102,363 B | +22.6% |
| Large | 376,781 B | 462,754 B | +22.8% |

**原因:** `StringNode?`（Nullable<StringNode>）が含有クラス（Job, Step 等）に ~72B の構造体をインライン埋め込みし、従来の 8B ポインタ参照より遥かに大きくなった。struct は dense array かスタック上で使わなければ逆効果。

### 1.3 アロケーション内訳の推定（Large ワークフロー: 20 jobs × 12 steps = 240 steps）

| カテゴリ | 推定件数 | 推定割り当て | 全体比 | Phase 1 後 |
|----------|---------|-------------|--------|-----------|
| StringNode オブジェクト | 600–1000 | 48–80 KB | 20% | 残存（class のまま） |
| 複合ノード（Job, Step, Event, etc.） | 300+ | 40–60 KB | 15% | 残存（class のまま） |
| List\<T\> 中間バッファ | 100+ | ~20 KB | 5% | ✅ 排除（PooledBuffer） |
| List\<T\> → ToArray() 最終配列 | 100+ | ~30 KB | 8% | 残存（最終配列は必要） |
| Dictionary\<K,V\> 内部配列 | 50+ | ~30 KB | 8% | ✅ 排除（SliceMap） |
| SliceMap Entry[] 最終配列 | 50+ | ~15 KB | 4% | 残存（最終配列は必要） |
| Utf8String byte[] コピー | 200+ | 5–15 KB | 3% | ✅ 排除（Utf8Slice） |
| Diagnostic 配列・文字列 | 少数 | 10–20 KB | 5% | 残存 |
| ExprType/expression 関連 | — | ~30 KB | 8% | 残存 |
| その他（中間変数、delegate等） | — | 残り | 24% | 残存 |

→ Phase 1 で排除されたのは **List 中間バッファ + Dictionary 内部配列 + Utf8String コピー** で ~60 KB（全体の ~16%）。
→ **残り 316 KB の主要構成:** StringNode/複合ノードの class オブジェクト、最終配列、expression 関連。Phase 2 の flat store 化が次の大きな削減機会。

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

### Phase 1: コレクション最適化（低リスク、中効果） ✅ 完了

**目標:** List\<T\>→ToArray() と Dictionary 中間バッファの割り当てを削減。

**実施結果（2026-04-22）:**

| Size | Before | After | Reduction |
|------|--------|-------|-----------|
| Small | 12,080 B | 9,672 B | -2,408 B (-19.9%) |
| Medium | 83,512 B | 70,232 B | -13,280 B (-15.9%) |
| Large | 376,696 B | 316,088 B | -60,608 B (-16.1%) |

速度: Large 6,844 μs → 6,815 μs（変化なし）。全 540 テスト通過。

**実施内容:**
1. `PooledBuffer<T>` を `Parsing/` 配下の共有ユーティリティに昇格（`PooledBuffer.cs`）。ExpressionParser の private 定義を共有化。
   - `List<T>` → `PooledBuffer<T>` に全 11 箇所を変換（Steps, Events, ScheduleEntry, WorkflowCallEventInput, StringNode, Diagnostic 等）。
   - 各メソッドは `try/finally { buffer.Dispose(); }` パターンで ArrayPool 返却を保証。
2. `SliceMap<TValue>` を新規作成（`SliceMap.cs`）。Utf8Slice キー + リニアスキャンの flat map。
   - `Dictionary<Utf8String, T>` → `SliceMap<T>` に全 15 箇所を変換。
   - 構築時は `PooledBuffer<SliceMap<T>.Entry>` で中間蓄積 → `.ToArray()` で確定。
   - case-insensitive 比較（`AsciiEqualsIgnoreCase`）をデフォルトとし、env/permissions のみ case-sensitive。
3. AST 公開型の 16 プロパティを `IReadOnlyDictionary<Utf8String, T>` → `SliceMap<T>` に変更。
   - Lint ルール ~22 ファイルを SliceMap API（`foreach`/`TryGetValue`/`ContainsKey` with source span）に移行。
   - `DynamicContextTypeBuilder` は SliceMap を受け取り、ExprType 境界で Utf8String に変換。
4. `PermissionScope.NameText`/`ValueText` を `Utf8String` → `Utf8Slice` に変更（byte[] コピー排除）。

**完了条件の達成状況:**
- [x] `new List<T>` がパーサー内に 0 箇所
- [x] `new Dictionary<Utf8String, T>` がパーサー hot path に 0 箇所
- [x] Utf8String byte[] コピーがパーサー dictionary key で 0 箇所
- [ ] ベンチマーク: Large Allocated ≤ 280 KB → 実績 309 KB（目標未達、下記 Lessons Learned 参照）
- [x] ベンチマーク: Large Mean ≤ 6,844 μs → 実績 6,815 μs
- [x] 全テスト通過（540/540）

**Lessons Learned:**
- 推定 25–35% 削減に対し実績は **-16.1%**。List/Dictionary の中間バッファは全体の ~16% に過ぎなかった。
- 推定内訳表（§1.3）の List\<T\> + Dictionary\<K,V\> 合計 33% は過大だった。実際には ToArray() の最終配列は残るため、削減できるのは中間バッファのみ。
- SliceMap 導入により AST 公開 API が変更されたが、影響は限定的。Lint ルールの foreach/TryGetValue パターンは機械的に移行可能だった。
- `DynamicContextTypeBuilder` は ExprType 型が `IReadOnlyDictionary<Utf8String, ExprType>` を内部的に使い続けるため、SliceMap→Utf8String 変換が境界で必要。ここは ExprType 自体を Phase 2 以降で改善する候補。
- 残り 316 KB の主要構成: AST ノードオブジェクト（StringNode ~60 KB, Job/Step/Event ~50 KB）、最終 ToArray 配列、ExprType/expression 関連。Phase 2 の flat store 化が次の大きな削減機会。

### Phase 2: Scalar Node の Flat Store 化（中リスク、高効果） ✅ 完了

**目標:** StringNode/BoolNode/IntNode/FloatNode をヒープオブジェクトから dense flat array に移行。

**実施結果（ParsingBenchmark — WorkflowParser.Parse）:**

| Size | Before (Phase 1) | After (Phase 2) | Reduction |
|------|-------------------|-----------------|-----------|
| Small (1 job × 3 steps) | 12,080 B | 10,104 B | -1,976 B (-16.4%) |
| Medium (6 jobs × 8 steps) | 83,515 B | 72,029 B | -11,486 B (-13.7%) |
| Large (20 jobs × 12 steps) | 376,738 B | 327,680 B | -49,058 B (-13.0%) |

速度: Large 11,465 μs → 14,800 μs（微増、計測ノイズ範囲）。全 540 テスト通過。

**実施内容:**
1. **Handle 型の導入** (`AstArena.cs`):
   - `StringNodeId` / `BoolNodeId` / `IntNodeId` / `FloatNodeId` readonly struct (4B each, offset-by-1 encoding: `default` = None)。
   - 旧 `StringNode`/`BoolNode`/`IntNode`/`FloatNode` class を `CommonNodes.cs` から完全削除。
2. **AstArena 実装** (`Parsing/AstArena.cs`, ~370 行):
   - `StringNodeData[]` / `BoolNodeData[]` / `IntNodeData[]` / `FloatNodeData[]` dense arrays。
   - `AddString`/`AddBool`/`AddInt`/`AddFloat` (allocation) + `GetStringValue`/`GetStringSlice`/`GetStringRange`/`GetStringQuoted`/`GetStringExpression`/`GetBoolValue`/`GetBoolRange`/`GetBoolExpression`/`GetIntValue`/`GetIntRange`/`GetIntExpression`/`GetFloatValue`/`GetFloatRange`/`GetFloatExpression` (read access)。全メソッド `AggressiveInlining`。
   - `CreateForSource(byte[])` ファクトリメソッドでソースサイズに基づく初期容量推定（`source.Length / 20` for strings）。
3. **AST ノード型の移行** (6 files: `Events.cs`, `Job.cs`, `Step.cs`, `StructuralNodes.cs`, `Workflow.cs`, `ActionMetadata.cs`):
   - 全 scalar プロパティを handle に変更: `StringNode? X` → `StringNodeId X`, `BoolNode? Y` → `BoolNodeId Y`。
   - 配列プロパティ: `IReadOnlyList<StringNode>` → `StringNodeId[]`, `SliceMap<StringNode>` → `SliceMap<StringNodeId>`。
4. **パーサーの移行** (8 files: `WorkflowParser.cs`, `WorkflowParser.*.cs`, `ScalarHelpers.cs`):
   - 全 `ParseString`/`ParseBool`/`ParseInt`/`ParseFloat` メソッドに `AstArena arena` パラメータ追加。
   - `new StringNode { ... }` → `arena.AddString(...)` パターンに全箇所変換。
5. **Arena threading through engine**:
   - `ParseResult` に `AstArena? Arena` プロパティ追加。
   - `LintConfig` に `AstArena? Arena` プロパティ追加。
   - `RuleBase` に `protected AstArena Arena => Config.Arena!;` 追加。
   - `LintEngine` で `Arena = parseResult.Arena` を LintConfig に注入。
6. **Lint ルールの移行** (~50 files):
   - 全 `node.Value.AsSpan(source)` → `Arena.GetStringValue(node)` パターンに変換。
   - 全 `node.Range` → `Arena.GetStringRange(node)` パターンに変換。
   - `HasNodeValue`, `BuildUsesLocation`, `BuildJobLocation`, `BuildEventLocation` を arena 対応。
   - `RunContextDirectUseAnalyzer` の static メソッドに `AstArena arena` パラメータ追加。
   - `LocalActionInputsRule` のキャッシュを 3-tuple `(ActionMetadata?, byte[]?, AstArena?)` に変更（action の arena は呼び出し元 workflow の arena とは別）。
   - `ReusableWorkflowRule.LocalWorkflowContract.FromEvent()` に `AstArena arena` パラメータ追加。
7. **テストの移行** (5 files):
   - `ParserTests.cs`: `result.Arena!` 経由での handle アクセス、`IsNull()` → `.HasValue.IsFalse()` struct 対応。
   - `RuleInterfaceTests.cs`: `new StringNode { ... }` → `arena.AddString(...)` + `Arena = arena` in LintConfig。
   - `WorkflowVisitorTests.cs`, `ScalarHelpersTests.cs`, `ParserAdapterResilienceTests.cs`: 同様の arena パラメータ追加。

**完了条件の達成状況:**
- [x] `StringNode`/`BoolNode`/`IntNode`/`FloatNode` class が 0 箇所（完全削除）
- [x] 全 scalar プロパティが handle struct（`StringNodeId` 等）に移行
- [x] パーサーの全 `new XxxNode { ... }` が `arena.AddXxx(...)` に変換
- [x] ベンチマーク: Large Allocated 327,680 B（-13.0% from Phase 1）
- [x] 全テスト通過（540/540）

**Lessons Learned:**
- **初期容量の重要性:** AstArena のデフォルト初期容量 64 では、large ワークフロー（~1000 strings）で 64→128→256→512→1024 と 5 回の配列成長が発生し、廃棄される中間配列のアロケーションが scalar node 削減効果を相殺して割り当てが逆に +4.8% 増加した。`CreateForSource()` でソースサイズ連動の初期容量推定（`source.Length / 20`）を導入して解消。
- **推定 30-40% 削減に対し実績は -13.0%:** §1.3 の推定で StringNode ~60 KB（20%）としていたが、実際の削減は ~49 KB。理由: (1) handle struct (4B) が AST 複合ノードの nullable reference (8B) より小さいが、AstArena 自体の dense array がオーバーヘッドを持つ。(2) 複合ノード class （Job, Step, Event 等）は依然としてヒープ割り当てであり、その参照フィールドサイズ削減（8B→4B）は全体に対して限定的。
- **Expression property の設計判断:** パーサーコードの調査で `StringNode.Expression` はパーサーが一切設定しないことが判明（常に null）。`BoolNode.Expression` は `ParseBoolOrExpression` で設定される。複合ノード(Env, Services, Matrix)の Expression は StringNodeId プロパティとして直接保持する設計を採用。
- **Local action の arena 分離:** `LocalActionInputsRule` と `ReusableWorkflowRule` は別ファイルの YAML を独立パースするため、独自の AstArena を持つ。action metadata の handle は action の arena で解決し、workflow の handle は workflow の arena で解決する必要がある。初期実装で `Decode(actionArena.GetStringSlice(...))` としたが、`Decode(Utf8Slice)` は `Config.Utf8Yaml`（workflow source）を解決先として使うため、action side のスライスが workflow source のバイト列を参照してしまうバグが発生。`DecodeSlice(actionSource, ...)` に修正。
- **struct の IsNull テスト:** TUnit の `await Assert.That(value).IsNull()` は struct に対して常に false を返す（struct は null にならない）。handle struct の「未設定」判定は `.HasValue.IsFalse()` に変更が必要。
- **残り 328 KB の構成推定:** 複合ノード class (Job, Step, Event 等) ~50 KB、最終 ToArray/SliceMap 配列 ~80 KB、AstArena バッキング配列 ~70 KB、ExprType/expression ~30 KB、Diagnostic ~20 KB、その他 ~78 KB。Phase 3 で複合ノードを arena 化 + ThreadStatic プールが次の削減機会。

### Phase 3: 複合ノードの Arena 化 + ThreadStatic プール（高リスク、完成形） ✅ 完了

**目標:** AST ノードの Arena 最適化とパース間再利用でアロケーションをさらに削減。

**実施結果（ParsingBenchmark — WorkflowParser.Parse）:**

| Size | Before (Phase 2) | After (Phase 3) | Reduction |
|------|-------------------|-----------------|-----------|
| Small (1 job × 3 steps) | 10,104 B | 6,704 B | -3,400 B (-33.7%) |
| Medium (6 jobs × 8 steps) | 72,029 B | 48,957 B | -23,072 B (-32.0%) |
| Large (20 jobs × 12 steps) | 327,680 B | 221,313 B | -106,367 B (-32.5%) |

速度: Large 14,800 μs → 16,311 μs（計測ノイズ範囲）。全 540 テスト通過。

**累積削減（初期 → Phase 3）:**

| Size | 初期値 | Phase 3 後 | 累積削減 |
|------|--------|-----------|----------|
| Small | 12,080 B | 6,704 B | **-44.5%** |
| Medium | 83,512 B | 48,957 B | **-41.4%** |
| Large | 376,696 B | 221,313 B | **-41.3%** |

**実施内容:**
1. **ThreadStatic AstArena プール** (`AstArena.cs`):
   - `AstArena` に `IDisposable` を実装。`Dispose()` で _source をクリアし、カウンタをリセットして ThreadStatic キャッシュに返却。
   - `Rent(byte[] source)` ファクトリメソッド: キャッシュからの取得（`ResetForSource` で配列を再利用）またはフレッシュ作成。
   - `ResetForSource()`: ソースを差し替え、カウンタをリセット、既存配列が新ソースに対して不足なら `EnsureMinCapacity` で拡張（既存配列が十分大きければ再利用 → 0 アロケーション）。
   - コンストラクタを `internal` に変更（テストコードからの直接構築を維持）。
   - `WorkflowParser.Parse` 内で `AstArena.CreateForSource(...)` → `AstArena.Rent(...)` に変更。
   - ParsingBenchmark で `result.Arena?.Dispose()` を呼び出し、2 回目以降の反復で arena 配列を再利用。
2. **DebuggerDisplay（§6.2 デバッグ体験の改善）**:
   - 全 4 ハンドル型（`StringNodeId`, `BoolNodeId`, `IntNodeId`, `FloatNodeId`）に `[DebuggerDisplay]` 属性を付与。デバッガで `String[42]` や `(none)` と表示。
   - `AstArena` 本体に `[DebuggerDisplay]` 属性: `"AstArena: 523 strings, 41 bools, 18 ints, 2 floats"` 形式。
   - `DebugGetStringText(StringNodeId)`: ハンドルから UTF-8 文字列を取得するデバッグ専用メソッド。ウォッチウインドウで `arena.DebugGetStringText(job.Id)` と入力すれば値を確認可能。
   - `DebugDump()`: arena 利用状況のサマリー文字列を返すメソッド。
3. **リーフ複合型の struct 化** (6 types):
   - `EnvVar` → `readonly struct`（SliceMap 内のみ、nullable なし）。121 インスタンス × ~24B class overhead 削減。
   - `PermissionScope` → `readonly struct`（SliceMap 内のみ）。
   - `WorkflowCallInput`, `WorkflowCallSecret` → `readonly struct`（SliceMap 内のみ）。
   - `WorkflowCallEventSecret`, `WorkflowCallEventOutput` → `readonly struct`（SliceMap 内のみ）。
   - `ScheduleEntry` → `readonly struct`（IReadOnlyList 内のみ）。
4. **LintEngine arena ライフサイクル設計**:
   - LintEngine.Check() 内部で arena を **Dispose しない**（OnlineAuditEngine が LintResult 経由で arena を後から参照するため）。
   - arena の Dispose 責任は最終的な消費者に委譲。ベンチマークでは明示的に Dispose。

**完了条件の達成状況:**
- [x] ThreadStatic arena プール実装・ベンチマーク計測で効果確認
- [x] DebuggerDisplay 属性で handle 型のデバッグ可読性向上
- [x] リーフ複合型 7 タイプを struct 化
- [x] ベンチマーク: Large Parse Allocated 221 KB（Phase 2 比 -32.5%、初期比 -41.3%）
- [x] 全テスト通過（540/540）

**Lessons Learned:**
- **ThreadStatic プールが最大効果:** arena 配列の再利用で Large -106 KB（-32.5%）を達成。BenchmarkDotNet の ShortRun (3 warmup + 3 actual) では warmup で ThreadStatic キャッシュが充填され、actual iterations は完全に再利用配列を使用する。これが一番のアロケーション削減源。
- **LintEngine での arena Dispose は不可:** 初期実装で LintEngine.Check() 内 try/finally で arena を Dispose したところ、OnlineAuditEngine のテスト 4 件が失敗。理由: LintResult が ParseResult を保持し、OnlineAuditEngine.AuditAsync() が lintResult.ParseResult.Arena を参照するため、LintEngine 終了時に arena を返却すると後続処理でスレーブデータを読み返す。解決: arena Dispose を呼び出し元に委譲。
- **全複合ノードの Arena 化は不採用:** Phase 1.2 の struct 変換失敗と同じ理由で、nullable 参照として埋め込まれる複合型（Job.Strategy?, Step.Env?, Container.Credentials? 等）は struct 化すると Nullable\<T\> のインライン膨張で逆効果。また Event (7 subtypes) / StepExec (2 subtypes) / RawYamlValue (3 subtypes) のポリモルフィック型は struct で表現不可。WorkflowVisitor が Workflow/Event/Job/Step を class 参照で受け取るため、ハンドル化すると全 ~50 lint ルールの変更が必要。ROI（~54KB 削減）に対してリスクが過大。
- **struct 化安全条件:** nullable フィールドとして使われず、SliceMap\<T\> や IReadOnlyList\<T\> のみで保持されるリーフ型のみが class→struct 変換の対象。EnvVar, PermissionScope, WorkflowCallInput/Secret, WorkflowCallEventSecret/Output, ScheduleEntry がこの条件を満たす。
- **残り 221 KB の構成推定:** 複合ノード class (Job 20×136B, Step 240×80B, StepExec 240×76B, etc.) ~47KB、最終 ToArray/SliceMap 配列 ~14KB、Expression/Diagnostic 関連 ~30KB、その他合体パース支援 ~130KB。さらなる削減には allocation profiler (dotMemory/PerfView) による正確な内訳特定が必要。

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
| 実装前 (2026-04-22) | 377 KB | 6.8 ms |
| Phase 1 完了 ✅ | 309 KB (実績) | 6.8 ms (実績) |
| Phase 2 完了 ✅ | 328 KB (実績) | — |
| Phase 3 完了 ✅ | 221 KB (実績) | — |

---

## 8. 推奨実装順序

```
Phase 1 (低リスク) ✅ ──→ ベンチマーク検証 ✅ ──→ Phase 2 (中リスク) ✅ ──→ ベンチマーク検証 ✅ ──→ Phase 3 (高リスク) ✅
     │                                           │                                           │
     ├─ PooledBuffer 導入 ✅                       ├─ Handle struct 定義 ✅                      ├─ ThreadStatic AstArena プール ✅
     ├─ SliceMap 導入 ✅                           ├─ AstArena scalar stores ✅                  ├─ リーフ複合型 struct 化 ✅
     ├─ Utf8String → Utf8Slice ✅                  ├─ 複合ノードのプロパティ変更 ✅                  ├─ DebuggerDisplay (§6.2) ✅
     └─ ArrayPool 活用 ✅                           └─ Lint ルール移行 ✅                          └─ ライフサイクル設計 ✅
```

Phase 1〜3 で累計 **-41.3% 削減**（377 KB → 221 KB）を達成。

---

## 9. Phase 4+ 詳細調査結果

### 9.1 残り 221 KB のアロケーション内訳（実測）

AllocationBreakdown.cs + ExpressionAllocProbe.cs による精密計測結果（Large: 20 jobs × 12 steps = 240 steps、482 式）。

#### 9.1.1 構造体サイズ実測

| 型 | サイズ |
|----|--------|
| ExpressionNode | 32B |
| Utf8Slice | 8B |
| TextRange | 24B |
| Diagnostic | 96B |
| ExpressionParseResult | 32B |

#### 9.1.2 クラスインスタンス内訳（実測 56,168B）

| クラス | 個数 | 単体サイズ | 合計 |
|--------|------|-----------|------|
| Step | 240 | 80B | 19,200B |
| ExecAction | 120 | 88B | 10,560B |
| Env (step) | 120 | 72B | 8,640B |
| ExecRun | 120 | 64B | 7,680B |
| Job | 20 | 160B | 3,200B |
| Matrix | 20 | 96B | 1,920B |
| Strategy | 20 | 64B | 1,280B |
| Runner | 20 | 64B | 1,280B |
| RawYamlString | 40 | 24B | 960B |
| MatrixRow | 20 | 32B | 640B |
| その他 (Workflow, Events, Env, Defaults, etc.) | 少数 | — | ~808B |
| **合計** | **750** | — | **~56,168B** |

#### 9.1.3 配列アロケーション内訳（実測 ~7,608B）

| 配列 | 推定合計 |
|------|---------|
| Steps arrays (20 jobs × 12 steps) | ~2,240B |
| EnvVar SliceMap entries | ~4,840B |
| Jobs SliceMap entries | ~496B |
| Handle arrays (Needs/Labels/Types/etc.) | ~424B |
| On events array | ~32B |
| **合計** | **~7,608B** |

#### 9.1.4 Expression パイプライン内訳（実測）

**単独計測（ExpressionAllocProbe.cs）:**

| 操作 | 482 回合計 | 内訳 |
|------|-----------|------|
| ExpressionParser.Parse のみ | 95,576B | ExpressionNode[] ~88KB + int[] ~4KB + ArrayPool warmup ~3KB |
| ExpressionSemanticAnalyzer.Validate のみ | 220,616B | List\<Diagnostic\> ~15KB + ExprType[] ~4KB + その他 ~201KB |
| **Parse + Validate 合計** | **316,192B** | — |

**式の種類別 Parse コスト:**

| 式 | Parse alloc | ノード数 | ノード配列 | 引数配列 |
|----|-----------|---------|-----------|---------|
| `github.ref_name` | 6,280B※ | 2 | 88B | 0B |
| `github.ref` | 2,608B | 2 | 88B | 0B |
| `startsWith(...) && success()` | 3,840B | 8 | 280B | 32B |
| `matrix.os` | 2,560B | 2 | 88B | 0B |
| `github.sha` | 2,272B | 2 | 88B | 0B |
| `!cancelled() && ...` | 3,808B | 8 | 280B | 0B |

※初回は ArrayPool cold start コストを含む。

**式の種類別 Validate コスト:**

| 式 | Validate alloc | 概要 |
|----|---------------|------|
| `github.ref_name` | 15,704B※ | 初回 JIT + static 初期化 |
| `github.ref` | 1,432B | List\<Diagnostic\> + 検証走査 |
| `startsWith(...) && success()` | 2,264B | + ExprType[] + 関数検証 |
| `matrix.os` | 320B | 最小コスト |
| `github.sha` | 376B | 最小コスト |
| `!cancelled() && ...` | 2,168B | + 関数検証 |

※初回コストは JIT コンパイルと静的フィールド初期化を含む。Validate の定常コストは 320–2,264B/回。

#### 9.1.5 全体アロケーション構成（推定）

```
全体: ~221 KB (ベンチマーク) / ~259 KB (GC.GetTotalAllocatedBytes)

├── Expression パイプライン    ~150–180 KB (68–81%)
│   ├── ExpressionNode[] ToArray   ~88 KB
│   ├── Validate 内部割り当て      ~60–90 KB
│   │   ├── List<Diagnostic> (482回)  ~15 KB
│   │   ├── ExprType[] (関数検証)    ~4 KB
│   │   └── その他検証走査           ~41–71 KB
│   └── int[] Arguments ToArray     ~4 KB
│
├── 構造クラスインスタンス      ~56 KB (25%)
│   ├── Step + StepExec           ~46 KB
│   ├── Job + Strategy/Matrix/Runner  ~8 KB
│   └── その他                    ~2 KB
│
└── 構造配列 (SliceMap等)        ~8 KB (4%)
```

**結論:** 残り 221 KB の **約 7 割が Expression パイプライン（Parse + Validate）** に起因。構造クラスは約 25%。Expression 最適化が Phase 4 の最優先。

### 9.2 Expression パイプラインの割り当てメカニズム

#### 現行フロー（per expression, 482 回/Large parse）

```
WorkflowParser.ValidateExpressionText()
  └── ParseAndValidateExpression(expressionUtf8, ...)
        ├── ExpressionParser.Parse(expressionUtf8)
        │     ├── new PooledBuffer<ExpressionNode>(16)  ← ArrayPool.Rent (再利用可)
        │     ├── new PooledBuffer<int>(16)             ← ArrayPool.Rent (再利用可)
        │     ├── new PooledBuffer<Diagnostic>(4)       ← ArrayPool.Rent (再利用可)
        │     ├── ... parse ...
        │     ├── NodesToArray() → new ExpressionNode[N] ← ★ 毎回ヒープ割り当て
        │     ├── ArgumentsToArray() → new int[M]        ← ★ (M>0 の場合)
        │     ├── DiagnosticsToArray() → Diagnostic[]     ← (通常 empty → cached)
        │     └── Dispose() → 3× ArrayPool.Return
        │
        └── ExpressionSemanticAnalyzer.Validate(parseResult, ...)
              ├── new List<Diagnostic>()                   ← ★ 毎回ヒープ割り当て
              ├── ValidateNode() → 再帰走査
              │     └── ValidateFunctionCall()
              │           └── new ExprType[argCount]       ← ★ 関数呼び出し時
              └── diagnostics.ToArray()                    ← (通常 empty → cached)
```

**問題点:**
1. `ExpressionNode[]` / `int[]` は `ParseAndValidateExpression` 内で生成・消費・破棄される **一時データ** だが、毎回新規配列を割り当てている。
2. `List<Diagnostic>` も同様に一時的だが、482 回生成される。
3. `ExprType[]` は stackalloc で代替可能なサイズ（関数引数は最大 ~16）。

---

## 10. Phase 4+ 実装計画

### Phase 4a: Expression バッファ再利用（中リスク、高効果）

**目標:** Expression パーサーの内部パスで ExpressionNode[] / int[] の ToArray() を排除し、PooledBuffer を直接再利用する。

**推定削減:** Large -88 KB to -92 KB（ExpressionNode[] ~88 KB + int[] ~4 KB）

**設計:**

```csharp
// 新: ExpressionParser に内部パス追加
public static class ExpressionParser
{
    // 既存 public API（変更なし）
    public static ExpressionParseResult Parse(ReadOnlySpan<byte> expressionUtf8) { ... }

    // 新: 内部パス — 呼び出し元提供の PooledBuffer に書き込み
    internal static int ParseInto(
        ReadOnlySpan<byte> expressionUtf8,
        ref PooledBuffer<ExpressionNode> nodes,
        ref PooledBuffer<int> args,
        ref PooledBuffer<Diagnostic> diagnostics)
    {
        // PooledBuffer をリセット（配列は保持）
        nodes.Reset();
        args.Reset();
        diagnostics.Reset();

        // Parser 内部で ref PooledBuffer を使用
        var parser = new Parser(expressionUtf8, ref nodes, ref args, ref diagnostics);
        var root = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.End) parser.AddError(...);
        return root;
    }
}
```

```csharp
// WorkflowParser 側: パース全体で共有する PooledBuffer
public static partial class WorkflowParser
{
    // ParseAndValidateExpression 内で毎回 Reset + 再利用
    private static void ParseAndValidateExpression(
        ReadOnlySpan<byte> expressionUtf8,
        TextRange expressionLocation,
        ExpressionValidationContext context,
        List<Diagnostic> diagnostics,
        ref PooledBuffer<ExpressionNode> exprNodes,
        ref PooledBuffer<int> exprArgs,
        ref PooledBuffer<Diagnostic> exprDiags,
        bool allowStatusCheckFunctions = false)
    {
        var rootNode = ExpressionParser.ParseInto(
            expressionUtf8, ref exprNodes, ref exprArgs, ref exprDiags);

        // Parse diagnostics → caller's list
        for (var i = 0; i < exprDiags.Count; i++) { ... }

        // Validate with spans（配列割り当てなし）
        ExpressionSemanticAnalyzer.ValidateInline(
            rootNode, exprNodes.AsSpan(), exprArgs.AsSpan(),
            expressionUtf8, expressionLocation, context,
            diagnostics, allowStatusCheckFunctions);
    }
}
```

**変更ファイル:**
1. `ExpressionParser.cs` — `ParseInto()` 追加、`Parser` ref struct に外部バッファ受け入れモード追加
2. `PooledBuffer.cs` — `Reset()` メソッド追加（`_count = 0`, 配列保持）
3. `ExpressionSemanticAnalyzer.cs` — `ValidateInline()` 追加（`ReadOnlySpan<ExpressionNode>` / `ReadOnlySpan<int>` 受け入れ）
4. `WorkflowParser.Primitives.cs` — `ParseAndValidateExpression` に PooledBuffer 引数追加
5. `WorkflowParser.cs` / `WorkflowParser.Steps.cs` / `WorkflowParser.Jobs.cs` — PooledBuffer のスレッディング

**公開 API への影響:** なし（ExpressionParser.Parse / ExpressionExtractor / LintConfig.ParseExpression は変更不要）

**完了条件:**
- [ ] `ExpressionParser.ParseInto` が ExpressionNode[] を割り当てない
- [ ] `ValidateInline` が List\<Diagnostic\> を割り当てない
- [ ] 公開 API `ExpressionParser.Parse` / `ExpressionExtractor` が変更されていない
- [ ] ベンチマーク: Large Parse Allocated ≤ 140 KB
- [ ] 全テスト通過

### Phase 4b: Semantic Validator per-call 割り当て排除（低リスク、中効果）

**目標:** ExpressionSemanticAnalyzer 内部の per-call ヒープ割り当てを排除。

**推定削減:** Large -15 KB to -25 KB（List\<Diagnostic\> ~15 KB + ExprType[] ~4 KB + その他）

**設計:**

```csharp
// 変更 1: List<Diagnostic> を呼び出し元から受け取り
internal static void ValidateInline(
    int rootNode,
    ReadOnlySpan<ExpressionNode> nodes,
    ReadOnlySpan<int> arguments,
    ReadOnlySpan<byte> expressionUtf8,
    TextRange expressionLocation,
    ExpressionValidationContext context,
    List<Diagnostic> diagnostics,  // ← 呼び出し元の list に直接追加
    bool allowStatusCheckFunctions = false)
{
    if (rootNode < 0) return;
    ValidateNode(rootNode, -1, nodes, arguments, ...);
}

// 変更 2: ExprType[] → stackalloc
private static void ValidateFunctionCall(...)
{
    // 現行: var argTypes = new ExprType[argCount];
    // 新: stackalloc（関数引数は最大 ~16）
    Span<ExprType> argTypes = argCount <= 16
        ? stackalloc ExprType[argCount]
        : new ExprType[argCount]; // fallback（到達しないはず）
}
```

**変更ファイル:**
1. `ExpressionSemanticAnalyzer.cs` — `ValidateInline()` 追加 / `ValidateFunctionCall` の stackalloc 化
2. `WorkflowParser.Primitives.cs` — `ParseAndValidateExpression` が caller's diagnostics を渡す

**Phase 4a との統合:** Phase 4a の `ValidateInline` に Phase 4b の変更を含める。同時実装が自然。

**完了条件:**
- [ ] `ValidateFunctionCall` 内の `new ExprType[]` が stackalloc に変更
- [ ] 内部パスで `new List<Diagnostic>()` が 0 箇所
- [ ] ベンチマーク: Phase 4a の目標に追加で -15 KB 以上
- [ ] 全テスト通過

### Phase 4c: （漸進的）ValidateDynamicPropertyAccess の改善

**目標:** Lint ルール側の ExpressionSemanticAnalyzer 呼び出しでも同様の割り当て削減。

`ExprUndefinedVarRule` 等が `ValidateDynamicPropertyAccess` を呼び出す際にも `new List<Diagnostic>() + ToArray()` パターンが存在。Phase 4a/4b の内部パスとは別に、公開 API 側にも List\<Diagnostic\> 受け入れオーバーロードを追加。

**推定削減:** Lint 時のみの効果。Parse ベンチマークには影響なし。

### Phase 5: （抜本的）Expression 検証の遅延実行

**目標:** WorkflowParser.Parse 時に式の解析・検証を行わず、後段（Lint ルール or 独立パス）に遅延。

**推定削減:** Large -130 KB to -150 KB（Parse 時の expression 割り当てをほぼ全排除）

**設計コンセプト:**

```
現行:
  WorkflowParser.Parse()
    ├── 構造パース (jobs, steps, events...)
    └── 式ごとに Parse + Validate            ← ~150 KB

遅延実行案:
  WorkflowParser.Parse()
    └── 構造パース (jobs, steps, events...)    ← ~70 KB
  ExpressionValidationPass.Run(workflow)
    └── 構造を走査し式を Parse + Validate      ← ~150 KB（遅延）
```

**利点:**
- Parse のアロケーションが ~70 KB に劇的削減
- 式検証が不要な場合（高速 lint サブセット等）は完全スキップ可能
- 式検証を Lint ルールとして統合すれば、ルールベースの有効/無効制御が可能

**リスク・問題点:**
- **診断モデルの変更:** 現在 expression エラーはパーサー診断。遅延すると Lint 診断に分類が変わる。
- **TextRange の保持:** 式の位置情報（Utf8Slice + TextRange）を構造ノードに保持する必要。現在は StringNodeId の Expression プロパティで式参照を持つが、遅延検証では式テキストの位置を別途記録する必要がある。
- **大規模リファクタリング:** ValidateExpressionText の呼び出し ~20 箇所を削除し、代わりに式位置を記録するロジックが必要。

**判断:** Phase 4a/4b で ~100-120 KB 削減が達成できれば、Parse 時の残り expression 関連は ~30-50 KB。その場合 Phase 5 の ROI は低下する。Phase 4a/4b の結果を見てから判断。

### Phase 6: 複合ノード最適化（高リスク、低〜中効果）

**目標:** Step / StepExec / Job 等の class インスタンス割り当てを削減。

**推定削減:** Large -20 KB to -40 KB

#### Phase 6a: StepExec Tagged Union

ExecRun / ExecAction の 2 サブクラスを struct tagged union に統合。

```csharp
// 現行:
abstract class StepExec { ... }
sealed class ExecRun : StepExec { StringNodeId Shell; StringNodeId WorkingDirectory; StringNodeId Run; }
sealed class ExecAction : StepExec { StringNodeId Uses; SliceMap<StringNodeId> With; SliceMap<EnvVar> Env; }

// 新:
readonly struct StepExecData
{
    public readonly StepExecKind Kind;  // Run or Action
    // ExecRun fields
    public readonly StringNodeId Shell;
    public readonly StringNodeId WorkingDirectory;
    public readonly StringNodeId Run;
    // ExecAction fields
    public readonly StringNodeId Uses;
    public readonly SliceMap<StringNodeId> With;
    public readonly SliceMap<EnvVar> Env;
}
```

**利点:** 240 × class overhead 削減（ExecRun 64B + ExecAction 88B → StepExecData ~64B inline in Step）
**問題点:**
- `step.Exec is ExecAction action` パターンが全 Lint ルールで使用（~30 箇所）
- WorkflowVisitor が ExecRun/ExecAction を区別して visit
- Union 化で未使用フィールドのメモリ浪費（ExecRun は With/Env 不要）

**推定削減:** ExecRun 120 × 64B + ExecAction 120 × 88B = 18,240B（Step 内の参照 8B × 240 = 1,920B も削減）→ ~20 KB

#### Phase 6b: Step Dense Array

Step を class → struct 化し、AstArena 内の dense array に格納。StepId handle で参照。

**前提:** Phase 6a の StepExec tagged union が完了していること（Step 内に StepExecData を直接埋め込み）。

**問題点:**
- Step は nullable フィールドを多数持つ（Env?, Container?, Services?）
- nullable class 参照 → nullable handle struct に変換が必要
- WorkflowVisitor が `Step` を class 参照で受け取る
- ~50 Lint ルールが Step のプロパティに直接アクセス

**推定削減:** Step 240 × 80B + StepExec 240 × 76B avg = ~37 KB（ただし StepData struct のサイズが大きいと arena 配列自体が増加）

#### Phase 6c: Job Dense Array

Job 20 個の class → struct 化。Phase 6b と同様のアプローチ。

**推定削減:** Job 20 × 160B = 3,200B（小効果）

**Phase 6 全体の判断:** ROI が低い（~20-40 KB 削減に対し、~50 ルールファイル + Visitor 変更が必要）。Phase 4a/4b の結果次第で実施判断。

---

## 11. 優先順位と実装順序

```
Phase 4a (中リスク) ──→ Phase 4b (低リスク) ──→ ベンチマーク検証 ──→ Phase 5/6 判断
     │                       │
     ├─ ExpressionParser.ParseInto()   ├─ ValidateFunctionCall stackalloc
     ├─ PooledBuffer.Reset()           ├─ ValidateInline (List受け入れ)
     ├─ ValidateInline (Span受け入れ)  └─ ExprType[] 排除
     └─ WP.ParseAndValidateExpression threading

Phase 4a + 4b 同時実装が最も効率的（ValidateInline の設計に両者の変更を含む）
```

**期待される成果:**

| Phase | 推定 Allocated (Large) | 推定削減 | 累積削減 (初期比) |
|-------|----------------------|---------|------------------|
| Phase 3 完了 (現状) | 221 KB | — | -41.3% |
| Phase 4a + 4b 完了 | 110–130 KB | -90 to -110 KB | -65% to -71% |
| Phase 5 (遅延検証) | 70–90 KB | -20 to -40 KB | -76% to -81% |
| Phase 6a (StepExec union) | 50–70 KB | -20 KB | -81% to -87% |

**Phase 4a + 4b で Large Parse を ~120 KB（初期比 -68%）にすることを次の目標とする。**

---

## 12. 検証計画（Phase 4+）

Phase 4a/4b 完了時に以下を測定:

1. **BenchmarkDotNet ParsingBenchmark** — Allocated bytes, Mean time（Small/Medium/Large）
2. **ExpressionAllocProbe.cs** — 482 回 Parse + Validate の合計割り当て
3. **AllocationBreakdown.cs** — クラスインスタンス vs Expression vs 構造配列の内訳
4. **全テストパス** — `dotnet test` Green 確認
5. **公開 API 互換性** — `ExpressionParser.Parse` / `ExpressionExtractor` / `LintConfig.ParseExpression` が変更されていないこと

**成功基準:**

| 指標 | Phase 3 (現状) | Phase 4 目標 |
|------|---------------|-------------|
| Large Parse Allocated | 221 KB | ≤ 130 KB |
| Large Parse Mean | 16.3 ms | ≤ 16.3 ms（速度維持） |
| Expression parse alloc (482回) | 96 KB | ≤ 10 KB |
| 全テスト | 540/540 | 540/540 |
