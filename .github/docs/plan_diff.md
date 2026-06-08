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

### A. 既定 ON + 明示 OFF

- 理由: 可読性向上の恩恵が大きく、既存利用者にも有益。
- 互換性懸念（ログ量増）には `--no-structure-snippet` で対応。

### B. 既定 OFF + 明示 ON

- 理由: 出力差分を最小化できる。
- ただし UX 改善が届きにくい。

推奨は **A（既定 ON）**。CI ログサイズ懸念があるため、設定で抑制可能にする。

---

## 実装プラン（段階）

## フェーズ 1: 最小実装（text / github-actions）

1. 診断から構造パスを抽出する層を追加
   - 既存の `jobs.'id'.steps[n]` 形式メッセージを正規化して `jobs.id.steps[n]` に変換
   - 取得不能時は `null`
2. `sourceMap` から YAML を走査し、パスに対応する最小構造ブロックを復元
3. `DiagnosticFormatter` の rich 出力に構造ブロックを追記
4. `github-actions` 出力でも同様に追記（グループ内）
5. 機能フラグ（CLI/Config）で ON/OFF を切替可能にする

完了条件:

- 例示ケースで `jobs -> examples -> steps -> uses` の骨格のみ表示される
- 既存テキスト出力の主要契約（header/location/caret/help）が崩れない

### フェーズ 1 実装記録（完了）

**実装内容**

- `StructureSnippetBuilder` / `YamlLineIndex`: インデント親たどりで最小 YAML 骨格を復元。無関係 sibling は `...` で省略。
- `DiagnosticFormatter`: rich 出力の source snippet 直後に `= structure:` ブロックを追加（`text` / `github-actions` のみ）。
- 表示ゲート: メッセージに `jobs.` / `steps[` プレフィックスがある、または祖先に `jobs:` / `steps:` / `runs:` がある場合のみ。
- `DiagnosticFormatOptions.StructureSnippets`（既定 `true`）。
- CLI `--no-structure-snippet`、config `output.structure-snippets: false` で無効化。
- ファイル単位で `YamlLineIndex` をキャッシュし、診断ごとの行インデックス構築を抑制。

**API（ユーザーファースト観点）**

- 既定 ON: 追加学習なしで文脈が得られる。
- 明示 OFF のみ: `--no-structure-snippet`（否定形フラグでログ量抑制が直感的）。
- `json` / `sarif` は非対象のまま（機械解釈は既存の line/col）。

**テスト**

- `tests/Seiton.Tests/StructureSnippetTests.cs`（6 件）
- `RuleInterfaceTests` に `output.structure-snippets` 設定パース 3 件
- `DiagnosticFormatterRichTextTests` 回帰 65 件パス

**ベンチマーク（`DiagnosticOutputBenchmark`, Release, ShortRun）**

| ケース | 変更前 Mean (baseline) | 変更後 Mean | 変化 | Allocated |
|---|---|---|---|---|
| F1 text rich | 231.88 μs | 212.06 μs | -8.5% | 1.65 KB（不変） |
| F10 text rich | 2,217.15 μs | 2,430.41 μs | +9.6% | 5.64 KB（不変） |
| F1 no structure（新規比較） | — | 203.57 μs | 対 structure 有効比 -4% | 1.65 KB |
| F10 no structure | — | 2,249.18 μs | 対 structure 有効比 -7% | 5.64 KB |

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

---

## テスト計画

### 契約テスト

- `jobs.examples.steps[0].uses` で期待骨格が出る
- action metadata `steps[0].run` でも同様に出る
- パス不明診断で従来出力のみ
- `--no-structure-snippet`（または同等設定）で非表示

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
2. 表示は既定 ON、CLI/Config で OFF 可能にする
3. 省略ポリシーは「最小骨格優先」、必要時のみ補助情報を追加する
4. `json` / `sarif` は対象外とし、機械解釈は既存の行・列情報を正とする
5. 将来拡張として `Diagnostic` に構造パスメタデータを導入し、文字列抽出依存を解消する
