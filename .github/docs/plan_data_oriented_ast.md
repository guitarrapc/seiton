# Plan: Data-Oriented AST (Arena 徹底化)

> Status: **in progress** (2026-07-11 開始)
> Related: `Seiton_Parser_csharp_spec.md` §0.5 / §2, `architecture_spec_performance.md`, `.claude/skills/performance-requirements/SKILL.md`

## 1. WHAT

AST を「クラスオブジェクトのグラフ + 後付けプール」から「AstArena 内の flat struct テーブル + 型付きインデックスハンドル + readonly struct ファサード」へ全面移行する。

- 全複合ノード (`Job`, `Step`, `Exec*`, `Permissions`, `Env`, `Runner`, `Strategy`, `Matrix`, `Container`, `Services`, `Event` 階層, `RawYamlValue` 階層, ほか) を `AstArena` 内の struct 配列の行にする。スカラー (`StringNodeId` 等) が既に採用している方式を全ノードへ拡張する。
- 子リスト (`Job.Steps`, `Job.Needs`, `ExecParallel.Steps`, filter values, ...) は共有 ID 配列への `(first, count)` レンジにする。`IReadOnlyList<T>` フィールドと `ArenaList<T>` の boxing を廃止する。
- `SliceMap` の `Entry[]` 個別レンタル + `RegisterSliceMapBuffer` 登録を廃止し、arena 共有エントリテーブルへのレンジにする。
- ルール/テスト向け API は `WorkflowRef` / `JobRef` / `StepRef` / `StringRef` 等の readonly struct ファサード (arena + handle を内包) に統一する。ルール作者が `Arena.GetStringValue(id)` / `Utf8Slice` / `ReadOnlySpan<byte>` を直接ジャグリングする現行 API を廃止する。
- `AstNodePool<T>`、手書きオブジェクトプール、全 `Reset()` メソッド、手動バッファ登録 (`RegisterSliceMapBuffer` 等) を削除する。arena リセットは配列カウンタのゼロクリアのみになる。
- DEBUG ビルドで世代カウンタ検証を入れ、dispose 後ハンドル解決を「別データが黙って返る」から「即例外」に変える。

## 2. WHY

### 2.1 現行構造の問題 (経緯の清算)

現行実装は「クラス AST に arena を後付けした」経路依存の産物であり、設計空間の中で複雑さが最大の地点にある:

1. スカラーだけ真の arena (struct 配列 + ハンドル)、複合ノード 28 種は `Reset()` 付き mutable クラスのオブジェクトプール、という二重構造。`Job.Reset()` は 22 フィールドの手動クリアで、フィールド追加のたび保守が要る。
2. `ArenaList<T>` (struct) を `IReadOnlyList<T>?` フィールドに代入した時点で boxing する。ゼロアロケーション目的の型がアロケーションを産んでいた。
3. プール系抽象が 4 系統並立 (`ArenaList` / `PooledBuffer` / `SliceMap` / `DiagnosticList`) し、`RegisterSliceMapBuffer` 等の手動登録は漏れるとリークする。
4. use-after-reset がサイレントに誤データを返す (dispose 後のハンドルを再利用中 arena に解決すると別ファイルのデータが返る)。
5. arena の制約がルール作者 API に露出し (`Arena.GetStringValue`、zero-copy 規約、`"literal"u8` 規約)、ルール実装の難度が高い。

### 2.2 移行で得るもの / 得ないもの

- 得るもの: 構造の一貫性 (全ノードが同一のライフタイム機構)、`Reset()`/プール/登録コードの全削除、boxing 根絶、ルール API の大幅な単純化、DEBUG での use-after-dispose 検出。
- 得ないもの: **大きな速度向上は目的ではない**。ベンチマーク実測 (2026-07-11 時点) で Medium/Large は Gen0 コレクション 0 回であり、GC は既に wall-clock に現れていない。本移行の目的は複雑さと安全性の回収である。速度の次のフロンティアは別課題 (パース時間の超線形成長: 13.7→19.5→65 μs/step) として扱う。

### 2.3 タイミングの判断

CLI は診断のみを参照し AST 型に依存しない (実測: `src/Seiton/` に AST 参照ゼロ)。ライブラリとして未公開のため、Seiton.Core の公開 API を破壊できる今が唯一の低コスト期。

## 3. ターゲットモデル (契約レベル)

### 3.1 ストレージ

- `AstArena` が全ノードデータの単独所有者。ノード種別ごとに struct 行テーブル (`JobData[]`, `StepData[]`, ...)。
- 多態ノードは tagged union: `StepExec` は `StepExecHandle(StepExecKind Kind, int Index)` + 種別ごとの payload テーブル。`Event` / `RawYamlValue` も同方式 (`EventKind` / `RawYamlKind` は新設)。既存の `StepExecKind` enum をそのまま討別子に使う。
- 子リストは共有 ID 配列 (`StepId[]`, `StringNodeId[]`, ...) への `(first, count)`。パース中はスクラッチ (`PooledBuffer`) に構築し、確定時に共有配列へ一括コピーする (parallel の入れ子で行が非連続になるため、行テーブル直接レンジは使わない)。
- `SliceMap` エントリは `(Utf8Slice Key, int Value)` の統一形へ。値は常にハンドル/インデックスなので型ごとのテーブルは不要、共有 `SliceMapEntry[]` へのレンジ + 型付きビュー struct で読む。

### 3.2 ハンドルとファサード

- ハンドル: 既存 `StringNodeId` と同型の 1-based `readonly record struct` (`JobId`, `StepId`, `EventId`, ...)。`default` = None。
- ファサード: `readonly struct JobRef` = (arena, JobId)。プロパティは他の Ref を返す (`job.Steps` → `StepListRef`、`job.Name` → `StringRef`)。`StringRef` は `.Utf8` (span)、`.Slice`、`.Range`、`.Decode()` (診断時のみ) を持つ。
- 存在しない値は `HasValue == false` の default Ref で表現する (現行の null 三値と等価な表現力を `Nullable` なしで持つ)。
- Ref の等値はハンドル等値 (同一パース内で安定)。`Dictionary<StepRef, T>` は identity キーの現行 `Dictionary<Step, T>` と同じ意味論になる。
- 網羅性: `StepExec` サブクラスへの `is` パターンは `Kind` enum の switch + `AsRun()` / `AsAction()` 等の型付きアクセサに置き換える。sealed 階層の網羅性チェック (コンパイルエラー) は enum switch の警告ベースに弱まる — これは意図的なトレードオフ (2.2 の得るものと引き換え)。

### 3.3 変わらないもの

- `Utf8Slice` / `Utf8String` / `TextRange` / `Diagnostic` / `DiagnosticList` / 診断パイプライン / VYaml アダプタ境界 / recovery-first パーサ挙動 / CLI の入出力契約 (**CLI 挙動は不変**)。

## 4. 影響範囲 (実測 2026-07-11)

| 領域 | 実測 | 影響 |
|---|---|---|
| CLI `src/Seiton/` | AST 参照ゼロ (診断のみ) | なし |
| `src/Seiton.Update/` | AST 参照ゼロ | なし |
| ルール | 62 クラス / 128 Visit オーバーライド / StepExec・RawYamlValue パターンマッチ 41 箇所 31 ファイル | 全面 (ファサード移行) |
| null 三値ルール | `_currentJob` 等を null 判定に使う 11 ルール | `HasValue` へ書換 |
| 同一性キー | `ExprUndefinedVarRule` の `Dictionary<Step,int>` / `List<Step>` | ハンドル等値へ (意味論同等) |
| テスト | AST 直接参照 43 ファイル 555 箇所 (最大 `ParserTests.cs` 247) | Ref API へ書換。手組み AST (`new Job{...}`) はビルダーヘルパーへ |
| `PublicApiContractTests` | AST 表面を固定 | 新表面で更新 |
| **`IncrementalParseContext`** (Playground.Core, ~1300 行) | 旧 arena の Job/セクションの**オブジェクト参照**を新 Workflow へ継ぎ足し、arena 生存をオブジェクト所有で管理 | **最大リスク**。row-copy + ハンドル再配置方式へ再設計 (Stage 3) |
| AST シリアライズ | 存在しない (共有ペイロードはテキストのみ) | なし |

## 5. 段階計画

二大変更を直交させる (strangler 方式): 先にルール API を移行し (ストレージ不変で検証)、次にストレージを差し替える (ルールコード不変で検証)。

| Stage | 内容 | 検証 |
|---|---|---|
| 1 ✅ (2026-07-11 完了) | Ref ファサードを**現行クラスの上に**導入 (`JobRef` が Job オブジェクトを内包)。`IPass`/`RuleBase`/`WorkflowVisitor` 署名と全ルール・テストを Ref へ移行。三値 null 11 ルールと同一性キー 1 ルールを修正 | 全テスト green。ベンチマーク中立 (±10% 以内) |
| 2 (進行中) | ストレージ差替: 複合ノード → struct 行テーブル + typed ID、子リスト → レンジ、SliceMap → 共有テーブル。パーサ構築サイト書換。Ref 内部を (arena, id) に差替 — **ルールコードは不変** | 全テスト green。Allocated 減少を確認 |
| 3 | `IncrementalParseContext` を row-copy + ハンドル再配置へ再設計 (`BulkImportFrom` を全テーブル + レンジ再マップに拡張) | Incremental 系 + Playground desktop テスト green |
| 4 | `AstNodePool` / オブジェクトプール / 全 `Reset()` / 手動登録の削除。プール系抽象の統合。DEBUG 世代カウンタ | 全テスト green。`Reset(` が src から消えること |
| 5 | spec 更新 (`Seiton_Parser_csharp_spec.md` §0.5/§2、`Seiton_Linter_csharp_spec.md`、skills)、全ベンチマーク比較の記録 | spec と実装の一致 |

各 Stage 完了時に `dotnet test` 全 green + `CoreParsingBenchmark` / `CoreLintBenchmark` を baseline (committed reports) と比較し、Mean/Allocated +10% 以内を守る。

ベースライン (2026-07-11, main): Seiton.Core.Tests 1994 green / Seiton.Tests 432 green / CoreLint Large 21.8ms・234KB・Gen0=0。

## 6. Stage 1 実装記録 (2026-07-11 完了)

- Ref 層: `src/Seiton.Core/Parsing/Ast/Refs/` に 7 ファイル (`ScalarRefs` / `ListRefs` / `MapRefs` / `WorkflowRefs` / `EventRefs` / `SectionRefs` / `ActionMetadataRefs`)。~35 ノード Ref + 8 リスト + 14 マップ。マップは公開名前付きラッパー + 内部共有 `RefMap<TNode,TRef>` (`INodeRef` static-abstract factory)。
- 公開 API: `ParseResult.Workflow` / `LintResult.Workflow` は `WorkflowRef` を返す (旧クラスは internal `WorkflowNode` / `ActionMetadataNode` として Stage 3 まで存置 — `IncrementalParseContext` が使用)。
- 判別子: `StepExecKind.None` を追加 (default ref 用、既存値は 1 ずつ後ろへ)。`EventKind` / `RawYamlKind` を新設。
- ルール 62 本 + アナライザ 2 本を移行。ルールから `Arena.Get*` 直接呼び出しはほぼ消滅 (残: `ExpressionScanHelpers.ContainsExpressionMarker(id, Arena)` 等の StringNodeId 経由 API と、他ファイル parse を読むクロスファイル箇所 — Stage 2 で整理)。
- `ExprUndefinedVarRule` → `DynamicContextTypeBuilder` 境界は `.Node` 内部アクセサでブリッジ (Stage 2 で本移行)。
- 検証: Seiton.Core.Tests 2005 / Seiton.Tests 432 / Seiton.Update.Tests 193 / Playground desktop 系 52 全 green。CoreParsing / CoreLint ベンチは全ケースで baseline 以下 (Mean −5〜17%、gate +10% 以内)。

## 6.1 Stage 2 実装記録 (進行中)

完了した増分 (各増分で全テスト green を維持):

1. **基盤** (2026-07-12): `NodeTable<T>` (ArrayPool-backed 行テーブル、Reset/CopyFrom/縮退)。typed ID は `Ast/NodeIds.cs`、行 struct は `Ast/SectionData.cs`。
2. **葉ノード 6 種** (2026-07-12): `Concurrency`/`Environment`/`Credentials`/`Snapshot`/`Defaults`/`DefaultsRun` をクラス+プールから行テーブル+ID へ。クラスと `AstNodePool` エントリは削除。
3. **文字列リスト → `StringIdRange`** (2026-07-12): `IReadOnlyList<StringNodeId>` フィールド 12 個 (needs/labels/types/values/options/ports/volumes/targets/args/workflows/names/versions) を共有 `StringNodeId[]` ストアへの (first,count) レンジへ。この経路の `ArenaList` boxing と `RegisterSliceMapBuffer` 登録を廃止。

**incremental parse との整合の要**: 継ぎ足し (セクション/ジョブの再利用) は「同一バイトオフセット + 同一内容ハッシュ」の場合にのみ発生するため、新テーブルを `BulkImportFrom` で**全行コピー**すれば、再利用ノード内の ID は新 arena でもそのまま解決できる。テーブル追加時は (a) `ResetForSource`/`Dispose` のリセット、(b) `BulkImportFrom` のコピー、(c) discard パスの `ReleaseAll` の 3 点を配線すること。

意味論の写像 (テスト移行時の規約): 旧 `null` (キー不在) → `default` ID/レンジ (`HasValue == false`)。旧「キーは在るが空」→ `HasValue == true` かつ `Count == 0`。`ParseStringOrStringSequence` は回復パスでも常に present レンジを返す (旧実装で default `ArenaList` が boxing により非 null になっていた挙動の保存)。

残作業: Permissions/Env/Runner、Strategy/Matrix/RawYaml (tagged union)、Container/Services/WorkflowCall、SliceMap → 共有 `(Utf8Slice, int)` エントリテーブル、Events tagged union、Exec*/Step/Job/Workflow 行化、ActionMetadata 族。

## 7. Lessons Learned (随時追記)

- (2026-07-11) `ArenaList<T> : IReadOnlyList<T>` を interface 型フィールドに代入すると boxing する。「struct で包めばゼロアロケ」はフィールドの静的型が interface なら成立しない。
- (2026-07-11) ゼロアロケーション追求は Gen0=0 到達時点で wall-clock への寄与が消える。以後の削減は複雑さの純増になりやすく、投資判断はベンチの Gen0 列を見てから行う。
- (2026-07-11 Stage 1) default ref の安全連鎖 (`x.Permissions.Scopes` が絶対に throw しない) は、ルール側の `?.`/null ガードの大半をそのまま削除できるため、移行コストを大きく下げた。一方 `is { }` パターンは struct では常に true になるため、`is { HasValue: true }` への書換が必要 (機械的置換の罠)。
- (2026-07-11 Stage 1) 判別子 enum に `None = 0` を持たせると default ref の `Kind` が誤って有効値 (旧 `Run = 0`) を返す事故を防げる。tagged union 化する際は必ず None を先頭に置く。
- (2026-07-11 Stage 1) 静的 abstract interface メンバ (`INodeRef<TNode,TSelf>.Create`) は internal メンバとして公開 struct に実装でき、マップ/リストの生成ボイラープレートを 1/5 に圧縮できた。AOT でも問題なし (値型 generic は特殊化される)。
- (2026-07-12 Stage 2) **arena 再利用バグの教訓**: 共有リストストアの `Reset()` 配線漏れで、count がパースを跨いで蓄積 → 保持上限超過で配列だけ解放 (count 残留) → 次パースで `IndexOutOfRange` → fatal 化。単発パース中心のテストでは検出できず、**ベンチマークの「速すぎる数値 + 全サイズ一律の Allocated」が実質の検出器**だった (壊れた op は fatal 即返しで速い)。対策: (a) `NodeTable` は配列解放時に必ず count も 0 化する不変条件を実装に内蔵、(b) `AstArenaReuseTests` で同一スレッド 40 回再利用の黒箱回帰を常設、(c) ベンチ結果が不自然に良い時はまず正しさを疑う (`jobs+diags` の積算検証)。
- (2026-07-12 Stage 2) CRLF リポジトリでは perl/sed の複数行パターン (`\n` アンカー) がサイレントに不発になる。ライフサイクル配線 (Reset/Release/CopyFrom の 3 点セット) は必ず grep で着地確認するか Edit ツールで行う。
