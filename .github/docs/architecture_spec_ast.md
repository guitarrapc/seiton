# seiton AST アーキテクチャ (データ指向設計)

> 本書は AST の設計思想・規約・不変条件を定義する恒久ドキュメントである。
> C# の型シグネチャレベルの契約は `.github/docs/Seiton_Parser_csharp_spec.md` §2、
> ルール実装者向けの読み取り API 規約は `src/Seiton.Core/Linting/Rules/AGENTS.md`、
> 性能要求の詳細は `.github/docs/architecture_spec_performance.md` と
> `.claude/skills/performance-requirements/SKILL.md` を参照。

## 1. コンセプト (WHAT)

AST は「クラスオブジェクトのグラフ」ではなく、**`AstArena` が単独所有する flat struct 行テーブルの集合**である。

- 全複合ノード (Job / Step / Event / Permissions / Matrix / ...) は、ノード種別ごとの `NodeTable<T>` (ArrayPool-backed 追記専用配列) の**行**。
- ノード参照は **1-based の型付き ID** (`JobId`, `StepId`, `PermissionsId`, ...)。`default` = 不在。
- 子リストは共有ストアへの **(first, count) レンジ** (`NodeRange` / `StringIdRange` / `StepIdRange`)。
- マップは**キー (`Utf8Slice`) を行に内蔵した行テーブルへのレンジ**。lookup はレンジ内線形スキャン。
- 多態ノードは **tagged union**: `Kind` enum + kind 別 payload テーブルへの 1-based index。
- ルール・テストが触れる公開面は **readonly struct の Ref ファサード** (`WorkflowRef` / `JobRef` / `StepRef` / `StringRef` / 各種リスト・マップ Ref) のみ。arena の生アクセサはルール作者の API ではない。
- ルート (`Workflow` / `ActionMetadata`) のみ、ID とレンジを束ねる薄いクラスとして残る。

## 2. 選定理由 (WHY)

旧実装 (プール付き mutable クラス + 後付け arena) で顕在化した複雑さの回収が目的である。**速度向上は目的ではない**(移行前の時点で Medium/Large は Gen0=0 であり、GC は wall-clock に現れていなかった)。

回収した問題:

1. スカラーは arena・複合ノードは `Reset()` 付きオブジェクトプール、という二重ライフタイム機構。フィールド追加のたびに `Reset()` の手動保守が必要だった。
2. `ArenaList<T>` (struct) を `IReadOnlyList<T>` フィールドに代入した時点で boxing し、ゼロアロケーション目的の型がアロケーションを産んでいた。
3. プール系抽象の並立 (`ArenaList` / `SliceMap` / `AstNodePool` / 手動バッファ登録) と、登録漏れによるリーク。
4. use-after-reset がサイレントに**別ファイルのデータ**を返す事故。
5. arena の制約 (`Arena.GetStringValue`、zero-copy 規約) がルール作者にそのまま露出し、ルール実装の難度が高かった。

移行後、これらの機構はすべて削除済みであり、arena のリセットは「全テーブルのカウンタをゼロにする」だけになった。

トレードオフとして意図的に受け入れたもの:

- sealed クラス階層への `is` パターンが持っていた **switch 網羅性のコンパイルエラーは、Kind enum switch の警告ベースに弱まった**。tagged union 化 (アロケーション根絶・ライフタイム一元化) との交換である。

## 3. ストレージモデルの規約

### 3.1 ID とレンジ

| 型 | 表現 | `default` の意味 |
|---|---|---|
| 型付き ID (`JobId` 等) | 1-based int の `readonly record struct`。`HasValue` / internal `Index` | キー不在 (旧 `null`) |
| `NodeRange` | 行テーブルへの (first, count) | キー不在 |
| `StringIdRange` / `StepIdRange` | 共有 ID ストア (`StringNodeId[]` / `StepId[]`) への (first, count) | キー不在 |

**「キーは在るが空」と「キー不在」は区別する**: 前者は anchored な空レンジ (`HasValue == true`, `Count == 0`)、後者は `default`。パーサの回復パスがどちらを返すかは旧実装の観測可能挙動を保存して決めてある (例: `ParseSteps` は `steps:` の値が sequence である限り、要素が全部エラーでも常に anchored レンジを返す。値が sequence ですらない場合は呼ばれず `default` = 不在になる)。この区別を崩すとルールの診断有無が変わる。

### 3.2 リストの 2 形態と連続性ルール

リストの表現は、**入れ子のパースがどのテーブルに行を差し込むか**で決まる:

- 入れ子のパースが**自分のテーブルに行を差し込む**場合 (例: ステップの中の `parallel:` が Step 行を追加する、RawYaml の再帰)、行テーブル直接レンジは非連続になり使えない。**スクラッチ `PooledBuffer<T>` に ID を集めてから共有 ID ストアへ一括 append** し、そのレンジを持つ (`StepIdRange` / `StringIdRange` 方式)。
- 入れ子のパースが**他のテーブル (スカラー等) にしか触れない**場合 (例: env vars、with: 入力、jobs マップエントリ)、行は連続するので**行テーブルへ直接 append + `NodeRange`** で良い。

**新しい入れ子構造を導入するときは、この前提を再確認すること。** 「値のパースがスカラーにしか触れない」前提で直接 append しているマップに、行テーブルを触る入れ子を後から足すと、レンジがサイレントに壊れる。

### 3.3 キー内蔵マップと case sensitivity

マップ行はキー `Utf8Slice` を行に内蔵し、lookup はレンジ内線形スキャン (旧 SliceMap と同じ計算量。GitHub Actions のマップは小さいことが前提)。case sensitivity は **lookup (Ref マップ) とパーサの重複キー検出で別々に固定**されており、変更は挙動変更である:

- **Ref マップの lookup**: `permissions:` の scopes と `env:` の変数名のみ case-SENSITIVE (バイト完全一致)。それ以外すべて (jobs / outputs / with: / secrets / services / action metadata inputs·outputs / dispatch inputs / ...) は case-INSENSITIVE。
- **パーサの重複キー検出** (`TryRegisterDynamicKey`): 到達可能な全呼び出しサイトが `caseSensitive: false` (case-INSENSITIVE)。permissions / env も含む — actionlint 互換の「note that this key is case insensitive」診断に対応する。

case-insensitive 比較は `SpanHelpers.EqualsAsciiIgnoreCase` に集約されている。

### 3.4 tagged union

- 判別子 enum は**必ず `None = 0` を先頭に置く** (default ref の `Kind` が有効値を返す事故の防止)。
- payload は kind 別テーブルへの **1-based index** (0 = payload なし)。
- パース手順は「**payload 行を先に append → 最後に本体行を 1 回 append**」(本体テーブルの連続性を守る)。
- 現在の tagged union: `StepData.ExecKind + ExecPayload`、`EventData.Kind + Payload`、`RawYamlData.Kind`。

### 3.5 行 struct の不変性とローカル蓄積

行 struct は `init` プロパティのみで、**追記後の mutate はできない**。パーサは値をローカル変数に蓄積し、ノードの解析完了時に 1 回で行化する。複数の YAML キーにまたがって 1 ノードが構成される場合 (例: 旧 workflow-call の uses/with/secrets) も同様にローカル蓄積で解決する。

## 4. Ref ファサードの規約 (公開 API)

- `ParseResult.Workflow` / `LintResult.Workflow` は `WorkflowRef` を返す。ルール・テストは Ref だけで完結させる。
- **default ref は安全に連鎖する**: `job.Strategy.Matrix.Rows` は途中がどれだけ不在でも throw せず、末端が `HasValue == false` / 空になる。ルール側の null ガードは原則不要。
- 不在チェックは `HasValue`。struct なので `is null` / `is { }` パターンは使えない (`is { }` は常に true になる — 機械的置換の罠)。テストで boxed struct に `IsNull()` / `IsNotNull()` を使うとコンパイルは通るが実行時に誤判定する — 必ず `HasValue` を assert する。
- 多態は `Kind` の switch + `AsRun()` / `AsAction()` 等の型付きアクセサ。Kind 不一致の `As*()` は default ref を返す。
- Ref の等価は (arena, id) の値等価。同一パース内で安定しており、`Dictionary<StepRef, T>` は旧オブジェクト同一性キーと同じ意味論になる。
- 文字列は `StringRef.Value` (UTF-8 span) / `.Slice` / `.Range` で読む。`.Decode()` (string 化) は**診断メッセージ構築時のみ**。

## 5. ライフタイムと安全性

- arena は thread-static キャッシュ経由で再利用される (`Rent` → `ResetForSource` → parse → `Dispose`)。リセットは全テーブルのカウンタクリアのみ。
- **`NodeTable` の不変条件: backing 配列を解放するとき、count も必ず 0 にする。** 配列だけ解放して count が残ると、次のパースで縮小後の配列を旧 count で索引して `IndexOutOfRange` → fatal 化する (実際に起きた事故。`AstArenaReuseTests` が同一スレッド 40 回再利用の黒箱回帰として常設されている)。
- **DEBUG 世代カウンタ**: arena は `ResetForSource` / `Dispose` で世代をインクリメントする (カウンタ自体は Release でも動く int 加算)。DEBUG ビルドでは全 Ref が生成時世代を捕捉し、dispose 後のハンドル解決は即 `InvalidOperationException` (Release では捕捉フィールドもチェックもコンパイルアウトされコストゼロ)。`HasValue` と等価比較は stale でも throw しない (安全に呼べる)。
- 値を arena の寿命より長く保持したい場合は、**dispose 前に値をコピーして持ち出す** (`Decode()` した string、`LocalWorkflowContract` のような値スナップショット)。

## 6. incremental parse との整合 (不変条件)

Playground の `IncrementalParseContext` (D-5b/5c/5d) は次の不変条件の上に成立している:

1. セクション/ジョブの再利用は「**同一バイトオフセット + 同一内容ハッシュ**」の場合にのみ発生する。したがって再利用ノード内の `Utf8Slice` は新ソースでもそのまま有効。
2. 新 arena は `BulkImportFrom` で前 arena の**全ノードテーブルを全行コピー**する (スカラー 4 テーブルのみ base count でキャップ)。したがって前パースの ID / レンジは新 arena でもそのまま解決できる。
3. ジョブ再利用は **`JobId` ベース** (`JobSkipEntry` が JobId を運ぶ)。jobs マップが `JobEntryData {Key, JobId}` へのエントリ間接になっているのは、再利用 JobId (インポートされた低い行 index) と新規パース JobId が 1 つのマップに混在するためである。
4. 旧 arena は毎パース後に即 Dispose する。オブジェクト所有による arena 退避 (retention) は存在しない。
5. テーブルの行はパースを跨いで蓄積するため、scalar 成長しきい値 (3×) がフルパースを強制して境界を保つ。

**この不変条件から導かれる配線ルール**: arena に新しいテーブルを追加したら、必ず次の全箇所に配線する —
(a) `ResetForSource` の Reset、(b) `Dispose` の Reset + `ReleaseOversized` (保持・破棄どちらのパスでも通る)、(c) `Dispose` 破棄パス (キャッシュ占有時) の `ReleaseAll`、(d) **`BulkImportFrom` の `CopyFrom`**。既存テーブル (`_stringIdItems` 等) を grep して全配線箇所を列挙し、着地を grep で確認する。(d) を忘れると単発パースのテストは全部通り、incremental parse だけがサイレントに壊れる。

## 7. ノード追加チェックリスト

新しい AST ノード種別を追加するときの規約 (詳細な型契約は Parser spec §2):

1. 行 struct を定義する (`Ast/*Data.cs`)。フィールドはスカラー ID / 他ノード ID / レンジ / `TextRange` のみ。オブジェクト参照・string は持たない。
2. 型付き ID を `Ast/NodeIds.cs` に追加する (1-based、既存 ID の複製)。
3. arena に `NodeTable<T>` + アクセサ (`AddXxx` / `GetXxx`、マップなら `GetXxxAt(NodeRange, i)` + `XxxCount`) を追加し、§6 の 4 点ライフサイクル配線を行う。
4. Ref (と必要ならリスト/マップ Ref) を追加する。既存の同型 Ref を複製し、公開面の形 (HasValue / TryGetValue / enumerator) を揃える。
5. パーサ構築サイトはローカル蓄積 → 解析完了時に 1 回 `Add` (§3.5)。リスト表現は §3.2 の連続性ルールで決める。マップなら case sensitivity を §3.3 に従い明示する。
6. テスト: 意味論の写像 (§3.1 の不在 vs 空) を保存し、`HasValue` で assert する。

## 8. パフォーマンス特性と注意点

- 定常状態のアロケーションは ArrayPool の rent/return に支配され、パース・lint とも **Medium/Large で Gen0 = 0**。移行完了時の実測 (ShortRun、冷機): Parse Large 15.70ms / 2,600B、Lint Large/False 16.26ms / 34.01KB (移行前 baseline: 21.8ms / 234KB)。
- マップ lookup は線形スキャンである。**大きなマップに対する繰り返し lookup をホットパスに置かない** (GitHub Actions の実ファイルではマップは小さく、これは問題にならない前提)。
- Ref のプロパティは arena の行を読むだけの薄いラッパで、JIT にインライン化される。Ref を経由すること自体のコストを理由に生 arena アクセスへ降りない。
- ベンチマーク判定の罠: `Program.cs` は常に `Job.ShortRun` であり、20-30ms/op のケースでは dynamic PGO の instrumented tier 区間に計測が落ちるかどうかで **コード変更なしに Mean が ±40% 振れる**。ゲート判定で ±10% を超えたら、(a) stash A/B、(b) `WorkflowParser.Parse` + pre-parsed `Check` の Stopwatch phase-split、(c) 400+ ops warmup 後の steady-state、の 3 点で実体かアーティファクトかを確定する。**Allocated 列とコントロールベンチ (`ExpressionExtractor`) は熱・位相の影響を受けにくい一次判定材料**である。
- 逆に「不自然に良い数値 + 全サイズ一律の Allocated」は正しさの破綻 (fatal 即返し) を疑う。ベンチマークは性能計測器であると同時に corruption detector である。

## 9. Lessons Learned (設計に埋め込まれた教訓)

移行過程で「実際にやってみて初めて分かった」もののうち、恒久的に設計判断へ影響するもの:

1. **struct + interface フィールドは boxing する**。`ArenaList<T> : IReadOnlyList<T>` を interface 型フィールドに代入した時点でヒープに逃げる。「struct で包めばゼロアロケ」はフィールドの静的型が interface なら成立しない。現設計がレンジ + 具象 struct Ref で統一されているのはこのため。
2. **ゼロアロケーション追求は Gen0=0 到達時点で wall-clock への寄与が消える**。以後の削減は複雑さの純増になりやすい。投資判断はベンチの Gen0 列を見てから行う。
3. **default ref の安全連鎖はルール側の null ガードを大量に消す**。移行コストの過半はこの設計で回収された。一方で `is { }` / `IsNull()` の struct 罠 (§4) が機械的置換の落とし穴になる。
4. **判別子 enum の `None = 0`** は default ref の誤動作を型で塞ぐ。tagged union を増やすときは必ず踏襲する。
5. **共有ストアの Reset 配線漏れは単発パースのテストでは検出できない**。count がパースを跨いで蓄積 → 保持上限で配列だけ解放 → 次パースで崩壊、という遅発性の壊れ方をする (§5 の NodeTable 不変条件と `AstArenaReuseTests` はその再発防止策)。
6. **「同一オフセット + 同一ハッシュのみ再利用」という不変条件が、incremental parse の複雑さを全行コピーの単純さに変換した** (§6)。この不変条件を緩める変更 (オフセットシフトを許す等) は、ID 再配置という別次元の複雑さを解禁するため、設計判断として扱うこと。

## 10. 関連ドキュメント

- `.github/docs/Seiton_Parser_csharp_spec.md` §2 — 型シグネチャレベルのストレージ/Ref 契約 (本書の規約の具体形)
- `.github/docs/Seiton_Linter_csharp_spec.md` — IPass/IRule/visitor の Ref 署名
- `.github/docs/Seiton_Playground_csharp_spec.md` — incremental parse (D-5b/5c/5d) の契約
- `.github/docs/architecture_spec_performance.md` — 性能アーキテクチャ全体と言語選定の記録
- `.claude/skills/architecture/SKILL.md` / `.claude/skills/performance-requirements/SKILL.md` — 実装時ガイド
- `src/Seiton.Core/Linting/Rules/AGENTS.md` — ルール実装者向けの Ref API 規約
