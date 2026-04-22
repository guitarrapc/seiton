# Lint / Parse メモリ削減計画

## 1. 現状ベンチマーク

### 1.1 ParsingBenchmark（Phase 4a 完了後）

| Size | Allocated |
|------|-----------|
| Small (1 job × 3 steps) | 4,984 B |
| Medium (6 jobs × 8 steps) | 27,208 B |
| Large (20 jobs × 12 steps) | 113,464 B |

### 1.2 LintBenchmark（Phase 4c 完了後）

| Size | FixEnabled | Allocated |
|------|-----------|-----------|
| Small | False | 18.85 KB |
| Small | True | 19.27 KB |
| Medium | False | 521.65 KB |
| Medium | True | 528.07 KB |
| Large | False | 8,482.75 KB |
| Large | True | 8,512.91 KB |

**Lint の Large は Parse の ~75 倍のアロケーション。** Lint パイプラインの最適化が主要な改善機会。

---

## 2. アロケーション内訳（実測）

### 2.1 Lint パイプライン全体構成（Large, FixEnabled=false）

`LintAllocBreakdown.cs` + `LintPerRuleAlloc.cs` による実測。

```
全体: ~8,640 KB (GC.GetTotalAllocatedBytes)

├── ベースライン（全ルール無効時）          ~257 KB (3.0%)
│   ├── WorkflowParser.ParseClassified      ~253 KB
│   │   ├── AST 構造体 (ThreadStatic arena再利用時)  ~113 KB
│   │   └── 初回パース overhead               ~140 KB
│   ├── Config normalization                ~2 KB
│   └── Inline suppression parsing          ~2 KB
│
├── Expression cache（lint 用再パース）     ~320 KB (3.7%)
│   ├── 482 ExpressionParseResult (ExpressionNode[], int[])
│   ├── Dictionary<long, ExpressionParseResult> bucket 成長
│   └── 各 Parse() per cache miss
│
├── run-*-context-direct-use 3 ルール    ~7,800 KB (90.3%)  ★★★
│   ├── BuildLineStarts (per expression)  × 3 rules × 360 calls
│   │   └── new List<int> + ToArray() × ~1,080 回
│   ├── IsInsideNoExpandHereDoc (per expression)
│   │   └── new List<HereDocState> × ~360 回
│   └── Expression parsing (cache hit, low cost)
│
├── ExprUndefinedVarRule                   ~176 KB (2.0%)
│   ├── DynamicContextTypeBuilder per job
│   │   ├── Dictionary<Utf8String, ExprType> × 20 jobs × 3 contexts
│   │   ├── new Utf8String (byte[] clone) per step/matrix/needs key
│   │   └── new ObjectExprType per override
│   ├── Override arrays (_jobScopeOverrides, _stepScopeOverrides) × 20
│   └── "steps"u8.ToArray() / "matrix"u8.ToArray() / "needs"u8.ToArray() per job
│
├── Diagnostic 生成 (280 diags)            ~50 KB (0.6%)
│   ├── Diagnostic record struct (96B × 280)  ~26 KB
│   ├── Message 文字列 (~180B avg × 280)     ~50 KB
│   └── _diagnostics List 成長              ~4 KB
│
├── その他ルール固有                       ~37 KB (0.4%)
│   ├── NeedsGraphRule: Utf8String keys + Dictionary
│   ├── UnredactedSecretsRule: BuildLineStarts (1回)
│   ├── 各ルールの Decode() 文字列
│   └── Fix 生成 (FixEnabled=true 時: +134 KB)
│
└── ルールインスタンス + 初期化             微量
```

### 2.2 ルール別アロケーション（Large, 単独実行）

| ルール | 単独Allocated | ベースライン超過 | 主因 |
|--------|-------------|---------------|------|
| run-secrets-context-direct-use | 2,875 KB | **2,618 KB** | BuildLineStarts × 360回 |
| run-env-context-direct-use | 2,875 KB | **2,618 KB** | 同上 |
| run-inputs-context-direct-use | 2,875 KB | **2,618 KB** | 同上 |
| expr-undefined-var | 433 KB | **176 KB** | DynamicContextTypeBuilder |
| secrets-outside-env | 366 KB | **109 KB** | Expression cache |
| if-cond | 366 KB | **109 KB** | Expression cache |
| fake-ternary | 366 KB | **109 KB** | Expression cache |
| checkout-persist-credentials | 366 KB | **109 KB** | Expression cache + 120 diagnostics |
| secrets-whole-context-access | 340 KB | **83 KB** | Expression cache |
| unpinned-uses | 321 KB | **64 KB** | 120 diagnostics + 文字列 |
| unredacted-secrets | 286 KB | **29 KB** | BuildLineStarts (1回) |
| template-injection | 285 KB | **28 KB** | Expression cache |
| 残り 35 ルール | 各 ~257 KB | **~0 KB** | ほぼベースラインのみ |

**結論:** アロケーションの **90% が BuildLineStarts の繰り返し呼び出し** に起因。3 つの run-context ルールで ~7,800 KB。

---

## 3. 改善計画

### Phase L1: BuildLineStarts キャッシュ化（低リスク、極めて高効果）

**目標:** `BuildLineStarts` を per-expression 呼び出しから per-lint-run キャッシュに変更。

**現状の問題:**
- `RunContextDirectUseAnalyzer.BuildExpressionLocation()` が per-expression で `BuildLineStarts(utf8Yaml)` を呼ぶ。
- Large で 360 expressions × 3 rules = ~1,080 回呼び出し。
- 各呼び出しで `new List<int>(64)` + `ToArray()` → `int[]` を割り当て。
- YAML が ~12,000 bytes で ~300 行の場合、各 `int[]` は ~1.2 KB → 合計 ~1.3 MB × 3 rules ≈ **~3.9 MB**。
- さらに `List<int>` の内部配列成長で追加 ~3 MB。

**改善案:**
1. `LintConfig` に `int[]? _lineStarts` キャッシュフィールドを追加。
2. `LintConfig.GetLineStarts()` メソッドを追加（lazy 初期化、per-lint-run で 1 回のみ計算）。
3. `RunContextDirectUseAnalyzer.BuildExpressionLocation()` に `int[] lineStarts` パラメータを追加。
4. 3 つの run-context ルールが `Config.GetLineStarts()` から取得して渡す。
5. `UnredactedSecretsRule` も同様にキャッシュ活用。

**推定削減:** ~7,500 KB → ~10 KB（1 回のみ計算 + キャッシュ）

**完了条件:**
- [x] `BuildLineStarts` が per-lint-run で最大 1 回のみ呼ばれる
- [x] 3 つの run-context ルール各 ~2,875 KB → ~286 KB（ベースライン付近）
- [x] 全テスト通過（543/543）

**実測結果（Phase L1 完了後）:**

LintBenchmark:
| Size | FixEnabled | Before | After | 削減率 |
|------|-----------|--------|-------|--------|
| Small | False | 18.85 KB | 15.98 KB | -15.2% |
| Small | True | 19.27 KB | 16.39 KB | -14.9% |
| Medium | False | 521.65 KB | 150.04 KB | -71.2% |
| Medium | True | 528.07 KB | 156.47 KB | -70.4% |
| Large | False | **8,482.75 KB** | **720.96 KB** | **-91.5%** |
| Large | True | 8,512.91 KB | 752.61 KB | -91.2% |

Per-rule (LintPerRuleAlloc.cs):
| ルール | Before | After | 削減率 |
|--------|--------|-------|--------|
| run-env-context-direct-use | 2,875 KB | 285.6 KB | -90.1% |
| run-secrets-context-direct-use | 2,875 KB | 285.6 KB | -90.1% |
| run-inputs-context-direct-use | 2,875 KB | 285.6 KB | -90.1% |
| unredacted-secrets | 280 KB | 280.0 KB | 変化なし（元々1回のみ） |
| ALL RULES TOTAL | 8,539 KB | 770.9 KB | -91.0% |

ParseBenchmark: Large 113,553 B = 110.9 KB（回帰なし）

**変更ファイル:**
1. `LintConfig.cs` — `_lineStarts` キャッシュフィールド + `GetLineStarts()` lazy メソッド追加
2. `ExpressionScanHelpers.cs` — `BuildLineStarts` を `PooledBuffer<int>` 使用に変更
3. `RunContextDirectUseAnalyzer.cs` — `BuildExpressionLocation` に `int[] lineStarts` パラメータ追加
4. `RunEnvContextDirectUseRule.cs` — `Config.GetLineStarts()` 呼び出しに変更
5. `RunInputsContextDirectUseRule.cs` — 同上
6. `RunSecretsContextDirectUseRule.cs` — 同上
7. `UnredactedSecretsRule.cs` — `Config.GetLineStarts()` 呼び出しに変更

### Phase L2: IsInsideNoExpandHereDoc の最適化（低リスク、中効果）

**目標:** `RunEnvContextDirectUseRule.IsInsideNoExpandHereDoc()` の per-expression アロケーションを排除。

**現状の問題:**
- `new List<HereDocState>(2)` を per-expression で作成。
- HereDocState は `byte[] Terminator` を持ち `line[start..i].ToArray()` で毎回コピー。
- Large で ~360 回（run step の expression ごと）呼び出し。

**改善案:**
1. Early return: run-context ルールは最初のマッチで `return` する設計なので、通常 0-1 回しか呼ばれない。ただし呼ばれた場合の最適化は worthwhile。
2. `HereDocState` リストをフィールドにキャッシュし `Clear()` で再利用。
3. `Terminator` の byte[] コピーを Utf8Slice（offset + length）に変更。

**推定削減:** ~5-10 KB（元々の呼び出し回数が限定的なら小効果）

**完了条件:**
- [x] `IsInsideNoExpandHereDoc` 内の `new List<>` 排除
- [x] `byte[].ToArray()` 排除
- [x] 全テスト通過（543/543）

**実測結果（Phase L2 完了後）:**

LintBenchmark:
| Size | FixEnabled | L1後 | L2後 | 差分 |
|------|-----------|------|------|------|
| Large | False | 720.96 KB | 720.29 KB | -0.67 KB |
| Large | True | 752.61 KB | 750.45 KB | -2.16 KB |

効果は小さい（~2 KB）。`IsInsideNoExpandHereDoc` は fix 構築時のみ呼ばれ、かつ run-context ルールは最初のマッチで return するため呼び出し回数が限定的。ただしゼロアロケーション化により GC 圧力を完全排除。

**変更内容:**
1. `new List<HereDocState>(2)` → `stackalloc HereDocState[4]` + カウンタ
2. `HereDocState(byte[] Terminator, bool StripTabs)` → `HereDocState(int TerminatorOffset, int TerminatorLength, bool StripTabs)` — source 配列への offset 参照
3. `line[start..i].ToArray()` → `lineStartInSource + start` offset 計算（byte[] コピー排除）

### Phase L3: Expression キャッシュの最適化（中リスク、中効果）

**目標:** Lint 時の Expression パース結果について、キャッシュのアロケーションコストを削減。

**現状の問題:**
- `LintConfig.ParseExpression()` は `ExpressionParser.Parse()` を呼ぶ。
- `Parse()` は `ExpressionNode[]` + `int[]` + `Diagnostic[]` を毎回 new する（`NodesToArray`, `ArgumentsToArray`）。
- 482 unique expressions × (~88B nodes + ~32B args avg) ≈ ~58 KB（ノード配列のみ）。
- `Dictionary<long, ExpressionParseResult>` の内部バケット成長コスト。
- 全ルールが同一 `LintConfig` を共有するため、Cache hit 率は高い。ただし「最初の 1 ルール」が全 miss を踏む。

**改善案 A: テキストベースキャッシュ（推奨）:**
- 現在のキャッシュキーは `(offset, length)`。同じ式テキスト（例: `github.sha`）が複数箇所に出現すると別エントリになる。
- テキストベース（`ReadOnlySpan<byte>` の内容ハッシュ + 長さ）キーに変更すると、大幅なキャッシュ hit 増加が期待できる。
- Large benchmark の 482 expressions のうち unique テキストは ~6 種類のみ。482 → 6 に削減。

**改善案 B: ParseResult の arena 化（高リスク）:**
- `ExpressionNode[]` / `int[]` を共有バッファに格納し、ParseResult はバッファへの range 参照。
- キャッシュ + 共有バッファで、per-expression 配列割り当てをゼロに。

**推定削減:** 案 A: ~250 KB → ~5 KB。案 B: さらに ~5 KB → ~1 KB。

**完了条件:**
- [ ] 同一テキストの式が 1 回のみパースされる
- [ ] Expression cache の total allocation ≤ 20 KB

### Phase L4: DynamicContextTypeBuilder 最適化（中リスク、中効果）

**目標:** per-job の ExprType/Utf8String/Dictionary 割り当てを削減。

**現状の問題（per-job, × 20 jobs）:**
- `BuildStepsOverride`: `"steps"u8.ToArray()` (6B) + `Dictionary<Utf8String, ExprType>` + per-step-id `new Utf8String(idBytes)` (byte[] clone)
- `BuildMatrixOverride`: `"matrix"u8.ToArray()` (7B) + Dictionary + per-row `new Utf8String(...)` (byte[] clone)
- `BuildNeedsOverride`: `"needs"u8.ToArray()` (6B) + Dictionary + per-need `new Utf8String(...)` + per-need `Dictionary<Utf8String, ExprType>` (result/outputs)
- `BuildInputsOverride`: `"inputs"u8.ToArray()` (7B) — workflow level (1回)
- Override arrays: `new (byte[], ExprType)[3]` + `new (byte[], ExprType)[4]` per job

**改善案:**
1. **Static byte[] キー:** `"steps"u8.ToArray()` 等を `static readonly byte[]` にキャッシュ（20 jobs → 1 allocation）。
2. **Override array キャッシュ:** `_jobScopeOverrides` / `_stepScopeOverrides` を固定長配列としてフィールドに保持し、要素を上書き再利用。
3. **Utf8String キャッシュ:** step ID の `new Utf8String(idBytes)` は毎回 `byte[]` をクローン。代わりに `Utf8Slice` を直接使う ExprType 型の改善が必要（後述 Phase L6）。

**推定削減:** 案 1+2 で ~10-15 KB。案 3 は Phase L6 に依存。

**完了条件:**
- [ ] per-job byte[] コピーが排除
- [ ] Override array が per-job 割り当てされない

### Phase L5: Diagnostic メッセージ文字列の最適化（低リスク、低効果）

**目標:** 頻出する同一メッセージ文字列のインターン化。

**現状の問題:**
- 280 diagnostics のうち 120 は同一の unpinned-uses メッセージ、120 は同一の checkout メッセージ。
- 各メッセージは `string` として個別にヒープ割り当て。

**改善案:**
- `UnpinnedUsesRule` / `CheckoutPersistCredentialsRule` のメッセージテンプレートが固定なら、action 名を含まない部分を `const string` 化。
- action 名を含む場合でも、同一 action 名は string intern 化可能。

**推定削減:** ~20-30 KB（Large で unpinned-uses 120 × ~100B + checkout 120 × ~100B の重複排除）

**完了条件:**
- [ ] 同一メッセージ文字列が共有される

### Phase L6: ExprType の Utf8Slice 化（高リスク、中効果 — 検討段階）

**目標:** `ObjectExprType` の `Dictionary<Utf8String, ExprType>` が per-key で `byte[]` をクローンしている問題を根本解決。

**現状の問題:**
- `Utf8String` コンストラクタは `utf8.ToArray()` で毎回 byte[] をヒープ割り当て。
- DynamicContextTypeBuilder が per-job で step/matrix/needs のキーを Utf8String 化 → 多数の byte[] clone。
- BuiltinContextTypes の static 初期化でも Utf8String を使用。

**改善案:**
- `ObjectExprType` のプロパティ検索を `ReadOnlySpan<byte>` ベースに変更（既に `TryGetProperty(ReadOnlySpan<byte>)` で実装済み）。
- Dictionary のキーを `Utf8String` → `int` (hash) にして、衝突時に source バイト比較する設計。
- ただし ExprType は Lint ルール境界を超えて共有されるため、source bytes への参照ライフサイクル管理が困難。

**リスク:** ExprType は公開 API の一部。変更影響が広範。

**推定削減:** ~20-40 KB（全 Utf8String byte[] clone の排除）

**判断:** Phase L4 で部分改善後、残存コストを再計測してから判断。

---

## 4. Parse パイプライン改善（追加分）

### Phase P1: ParseClassified のベースラインコスト（低優先度）

**現状:** Parse のベースラインは ~253 KB（GC.GetTotalAllocatedBytes）。BenchmarkDotNet での計測は 113 KB。差分 ~140 KB は:
- GC 計測オーバーヘッドとベンチマーク計測方法の差（BenchmarkDotNet は複数 iteration の安定値、GC は 1-shot）
- ThreadStatic arena の初回割り当て（2回目以降は再利用で 0）

BenchmarkDotNet の 113 KB が安定計測値であり、既に Phase 4a 目標を達成。追加最適化の ROI は低い。

**残存 113 KB の内訳（plan_allocation_ast.md §9 より）:**
- 構造クラスインスタンス: ~56 KB（Step 240×80B, ExecAction/Run, Job 等）
- 構造配列 (SliceMap, ToArray): ~8 KB
- 残存 Expression 関連: ~49 KB（ExprType[], ArrayPool overhead, 公開 API パスの残存）

### Phase P2: 式バリデーションの content-based dedup（Parse 時）

**現状:** `WorkflowParser.ParseAndValidateExpression()` は同一テキスト（例: `github.sha`）を複数回バリデーション。ParseAndValidateInline が PooledBuffer → span で配列を排除したが、validation 走査自体の CPU コストは残る。

**改善案:** Parse 時に `HashSet<(int offset, int length)>` でバリデーション済み式を追跡し、同一テキストの 2 回目以降をスキップ。
ただし、現在の Parse 時の式バリデーションは既に inline（配列割り当てなし）のため、CPU コスト削減のみでアロケーション影響は微小。

**判断:** ROI が低い。スキップ。

---

## 5. 優先順位と期待される効果

```
Phase L1 (低リスク) ──→ Phase L3 (中リスク) ──→ Phase L4 (中リスク) ──→ ベンチマーク検証
     │                       │                       │
     ├─ BuildLineStarts       ├─ テキストベースキャッシュ  ├─ static byte[] キー
     │  キャッシュ化            ├─ 482→6 重複排除        ├─ override array 再利用
     └─ ~7,500 KB 削減        └─ ~250 KB 削減          └─ ~15 KB 削減
```

| Phase | 推定削減 (Large Lint) | リスク | 推定 Lint Allocated |
|-------|---------------------|--------|---------------------|
| 現状 | — | — | 8,483 KB |
| **Phase L1 (BuildLineStarts)** ✅ | **-7,762 KB (実測)** | 低 | **721 KB** |
| **Phase L2 (HereDoc最適化)** ✅ | **-2 KB (実測)** | 低 | **720 KB** |
| Phase L3 (Expression cache) | **-250 KB** | 中 | **~470 KB** |
| Phase L4 (DynamicContextType) | -15 KB | 中 | ~455 KB |
| Phase L5 (Diagnostic strings) | -25 KB | 低 | ~430 KB |
| **累積目標** | **-8,053 KB (-94.9%)** | — | **~430 KB** |

**Phase L1+L2 完了: 91.5% の削減達成（8,483 KB → 720 KB）**。

---

## 6. 検証計画

各 Phase 完了時に以下を測定:

1. **BenchmarkDotNet LintBenchmark** — Allocated (Small/Medium/Large, FixEnabled=false/true)
2. **BenchmarkDotNet ParsingBenchmark** — 回帰なしを確認
3. **LintPerRuleAlloc.cs** — run-context ルール個別計測
4. **全テストパス** — `dotnet test` Green 確認

**成功基準（Phase L1）:**

| 指標 | 現状 | 目標 |
|------|------|------|
| Large Lint (FixEnabled=false) | 8,483 KB | ≤ 1,200 KB |
| run-env-context-direct-use (単独) | 2,875 KB | ≤ 300 KB |
| Large Parse | 113 KB | 113 KB（回帰なし） |
| 全テスト | 543/543 | 543+/543+ |
