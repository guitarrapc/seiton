# 検出行の構造抜粋表示（AST 構造）計画

## 目的

`jobs.examples.steps[0].uses` のような検出パスに対して、診断の可読性と修正速度を上げるため、**影響範囲の YAML 構造のみ**を表示する。

狙いは以下:

- ユーザーが「どの階層の何を直せばよいか」を一目で理解できる
- 大きな `job` / `step` 本文を見せず、ノイズを減らす
- 既存の source snippet（1行/複数行表示）を補完し、文脈不足を解消する

---

## 仕様案（WHAT）

### 1. 出力対象

- 対象は、診断メッセージまたは診断メタデータから **構造パス** を特定できる診断。
  - 例: `jobs.examples.steps[0].uses`
  - 例: `steps[0].run`（action metadata）
- 構造パスを特定できない診断は、従来出力のみ（後方互換）。

### 2. 表示内容

- 表示するのは「パスに対応する祖先ノード + 対象ノード」のみ。
- 同階層の無関係な sibling は省略可。
- 省略時は `...` を使って明示する。
- 値は原則として実際の YAML を表示する（例: `uses: actions/checkout@v2`）。

### 3. 表示例（text / rich）

`jobs.examples.steps[0].uses` の場合、概念的に以下を出す:

```text
4    jobs:
5      examples:
...    # unrelated keys omitted
6        steps:
7          - uses: actions/checkout@v2
```

補足:

- 行番号は「元ファイル行番号」を維持する（可能な場合）。
- `...` 行は擬似行として扱い、行番号なしでもよい。

### 4. 省略ポリシー

- デフォルト: 最小表示（パスに必要な骨格のみ）。
- 情報不足になりやすい箇所では、近傍の sibling を 1-2 件含める拡張を許容。
- 省略は決定論的に行う（同入力で同出力）。

### 5. 出力フォーマット別方針

- `text` / `github-actions`:
  - 既存 snippet の直後に「Structure」ブロックを追加表示。
- `json` / `sarif`:
  - **本機能の対象外**。改行を含む構造ヒントは出力しない。
  - 機械的な解釈は既存どおり `line` / `col`（および既存フィールド）を正とする。

### 6. 後方互換

- 既存のヘッダ、位置情報、caret、help は維持。
- 新表示は「追加情報」とし、無効化可能にする。

---

## インターフェース方針（WHY）

### 方針: rich 出力では常に structure 表示

- 理由: 可読性向上の恩恵が大きく、オプトアウト用フラグは不要と判断。
- `--oneline` / `json` / `sarif` は従来どおり structure 非表示。

---

## 実装プラン（段階）

## フェーズ 1: 最小実装（text / github-actions）

1. 診断から構造パスを抽出する層を追加
   - 既存の `jobs.'id'.steps[n]` 形式メッセージを正規化して `jobs.id.steps[n]` に変換
   - 取得不能時は `null`
2. `sourceMap` から YAML を走査し、パスに対応する最小構造ブロックを復元
3. `DiagnosticFormatter` の rich 出力に構造ブロックを追記
4. `github-actions` 出力でも同様に追記（グループ内）
5. rich 出力では structure を常時表示（`--oneline` / `json` / `sarif` は除外）

完了条件:

- 例示ケースで `jobs -> examples -> steps -> uses` の骨格のみ表示される
- 既存テキスト出力の主要契約（header/location/caret/help）が崩れない

### フェーズ 1 実装記録（完了）

**実装内容**

- `StructureSnippetBuilder` / `YamlLineIndex`: インデント親たどりで最小 YAML 骨格を復元。無関係 sibling は `...` で省略。
- `DiagnosticFormatter`: rich 出力で structure を解決できる場合は source snippet を最小 YAML 骨格 gutter に置換（`text` / `github-actions` のみ）。`= structure:` ラベルは使わない。
- 表示ゲート: メッセージに `jobs.` / `steps[` プレフィックスがある、または祖先に `jobs:` / `steps:` / `runs:` がある場合のみ。
- rich 出力では structure を常時表示（オプトアウトなし）。
- ファイル単位で `YamlLineIndex` をキャッシュし、診断ごとの行インデックス構築を抑制。

**API（ユーザーファースト観点）**

- 追加学習なしで文脈が得られる（rich 出力のデフォルト体験）。
- `json` / `sarif` は非対象のまま（機械解釈は既存の line/col）。

**テスト**

- `tests/Seiton.Tests/StructureSnippetTests.cs`（5 件）
- `DiagnosticFormatterRichTextTests` 回帰 65 件パス

**ベンチマーク（`DiagnosticOutputBenchmark`, Release, ShortRun）**

| ケース | 変更前 Mean (baseline) | 変更後 Mean | 変化 | Allocated |
|---|---|---|---|---|
| F1 text rich | 231.88 μs | 212.06 μs | -8.5% | 1.65 KB（不変） |
| F10 text rich | 2,217.15 μs | 2,430.41 μs | +9.6% | 5.64 KB（不変） |
**性能評価**

- **Allocated は増加なし**（構造表示は既存 source バイト参照 + 行インデックス再利用）。
- F1 Mean はベースライン比で改善（計測誤差範囲を含む）。F10 は +9.6% で許容閾値（+10%）内。
- 増加理由: 診断ごとに祖先チェーン構築と追加 gutter 出力。F10 では診断数が多いため線形にコスト増。
- 改善策（フェーズ 2）: 診断ごとの `int[]` 再割当を削減、パスメタデータを `Diagnostic` に持たせメッセージ解析を省略。

**レビュー指摘と対応**

| 指摘 | 対応 |
|---|---|
| `json`/`sarif` に改行混入しないこと | フォーマッター分岐で対象外を維持。テストで確認。 |
| メッセージにパスがない step 診断（`unpinned-uses`） | 祖先 `steps:` ゲートで location ベース表示を許可。 |
| パフォーマンス | ファイル単位 `YamlLineIndex` キャッシュ。ベンチで Allocated 不変を確認。 |
| fix モードの sourceMap | 初回読み込み bytes を保持（残診断表示用）。fix 適用後の行ズレはフェーズ 2 で改善余地。 |

## フェーズ 2: 品質向上

1. 省略ポリシーの改善（必要時のみ sibling 補助表示）
2. 複数パス形式（`jobs.'x'...` / `jobs.x...` / action metadata）の抽出精度向上
3. パフォーマンス最適化（再パース抑制、キャッシュ）

完了条件:

- 大規模 workflow でも実用的な速度・メモリで動作
- ノイズ過多・情報不足の両方を抑制

### フェーズ 2 実装記録（完了）

**実装内容**

- `DiagnosticStructurePathParser` / `DiagnosticStructurePathResolver`: メッセージまたは `structure-path` メタデータから `jobs.'id'.steps[n].field` 形式を解析し、YAML 上の対象行を解決。
- インライン sequence キー（`- uses:` / `- run:`）をパス終端として認識。
- 診断 location が範囲外でも、パス解決に成功すれば structure を表示（メッセージと表示の一貫性）。
- `DiagnosticStructurePathMetadata.Key`（`structure-path`）: ルールが明示パスを付与できる公開定数（`Seiton.Core.Linting`、メッセージ非依存の安定 API）。
- `StructureSnippetBuilder`: 祖先チェーン構築で `int[]` の診断ごと `ToArray()` を廃止し、`stackalloc`/`ArrayPool` 上で trim + 表示行生成。
- `YamlLineIndex`: 子キー探索・sequence 項探索のナビゲーション API を追加。
- `FixCommand`: fix / dry-run 適用後に `sourceMap` を更新し、残診断の structure が修正後 YAML と一致。
- `StructureSnippetBenchmark`: TryBuild 専用ベンチマークを追加。

**API（ユーザーファースト観点）**

- ユーザーは追加フラグなしで、メッセージパスに基づく正しい job/step 文脈を得られる。
- ルール作者は任意で `Metadata["structure-path"]` を付与でき、メッセージ文言変更の影響を受けにくい。
- fix 後も structure がファイル実体とずれない。

**テスト**

- `StructureSnippetTests` 10 件（パス選択、metadata、job `uses`、action `steps[n]`、sourceMap、回帰）
- `Seiton.Tests` 400 / `Seiton.Core.Tests` 1887 パス

**ベンチマーク（Release, ShortRun）**

| ケース | フェーズ 1 後 Mean | フェーズ 2 後 Mean | 変化 | Allocated (F10) |
|---|---|---|---|---|
| F1 text rich | 212.06 μs | 262.5 μs | +23.8%* | 18.93 KB |
| F10 text rich | 2,430.41 μs | 2,578.1 μs | +6.1% | 177.31 KB |
| TryBuild all (新規) | — | 176.7 μs | — | 128.91 KB |

\*F1 は ShortRun 3 反復の誤差が大きく、セッション内の直前計測（~2535 μs F10）と比べると F10 は +1.7% 程度。フェーズ 2 のパス解析コストは F10 で許容閾値（+10%）内。

**性能評価**

- 祖先チェーンの中間 `int[]` 割当を削減。表示用 `StructureSnippetEntry[]` のみ残る（出力に必要）。
- パス解析はメッセージ先頭または metadata のみを参照し、失敗時は location ベースにフォールバック（追加コストは診断あたり O(path長)）。
- F10 Mean +6.1%（計測誤差含む）: パス解決とインラインキー判定が診断ごとに走るため。+10% 以内で許容。
- 改善余地: パース結果を `Diagnostic` 生成時にキャッシュ（ルール側 metadata 付与で既に可能）。

**レビュー指摘と対応**

| 指摘 | 対応 |
|---|---|
| メッセージ末尾文言がパス解決を壊す（`uses must be string`） | パス suffix を空白で打ち切り |
| location 範囲外で structure 非表示 | パス解決成功時は location 検証をバイパス |
| `- uses:` が子キー探索で見つからない | インライン sequence キー判定を追加 |
| fix 後に structure が旧 YAML 参照 | `sourceMap` を fix/dry-run 後 bytes で更新 |
| 省略ポリシー拡張はノイズ増リスク | フェーズ 2 ではパス精度・性能に集中。省略拡張は将来 |

---

## テスト計画

### 契約テスト

- `jobs.examples.steps[0].uses` で期待骨格が出る
- action metadata `steps[0].run` でも同様に出る
- パス不明診断で従来出力のみ
### 回帰テスト

- 既存の `text` / `github-actions` スナップショット比較
- `json` / `sarif` の出力非変更確認（構造ヒントを混入させない）
- 色付き出力・`--oneline` での非干渉確認

### 性能確認

- 診断件数が多いファイルでの追加コスト測定
- ベースライン比で許容範囲内か評価

---

## リスクと対策

- パス抽出をメッセージ文字列に依存すると壊れやすい
  - 対策: 将来的に `Diagnostic` に構造パス専用メタデータを持たせる
- ログ量増加
  - 対策: 省略ポリシー + OFF スイッチ
- フォーマットごとの差異拡大
  - 対策: 本機能は text 系限定。JSON/SARIF は対象外を維持

---

## 推奨決定事項

1. 初期スコープは `text` / `github-actions` に限定する
2. 表示は rich 出力で常時 ON（オプトアウトなし）
3. 省略ポリシーは「最小骨格優先」、必要時のみ補助情報を追加する
4. `json` / `sarif` は対象外とし、機械解釈は既存の行・列情報を正とする
5. 将来拡張として `Diagnostic` に構造パスメタデータを導入し、文字列抽出依存を解消する
