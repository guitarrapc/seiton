# Workflow Flow 可視化 — 調査メモと実装計画

本書は、GitHub Actions workflow の実行フローを Seiton で可視化するための調査結果、意思決定、実装フェーズを整理した計画文書である。

## 背景

workflow で `parallel` 構文が入ると、単純な job の並びだけでなく step レベルでも実行の広がりが生まれ、YAML を読むだけでは実際の流れを把握しづらい。

特に次の課題がある。

- `needs` による job DAG と、job 内の step 列を頭の中で合成しないと全体像が見えない。
- `parallel` step によって直列の読み下しが破綻し、レビュー時に「どこが同時に走るか」を追いづらい。
- Playground は lint 結果の表形式表示まではあるが、workflow 構造の俯瞰には向いていない。

本計画の目的は、Seiton の parse 済み workflow 構造を再利用しつつ、CLI と Playground の両方で同じ flow 情報を扱える形を定義することにある。

## 調査結果

### 現在の CLI 出力構造

- CLI は `check` / `fix` / `init` / `validate-config` / `rules` などのコマンド構成を持ち、lint 実行の中心は `check` と `fix` に集約されている。
- 出力フォーマットはコマンドごとの独自実装ではなく、共通の `OutputFormat` と formatter 入口で処理されている。
- `--format` は CLI option と config bridge で解決され、GitHub Actions 上では `github-actions` を既定値にする分岐もある。
- 既存の設計では「lint 結果の出力形」を `check` の責務として扱っているため、flow 可視化もまずは `check` の出力拡張として載せるのが自然である。

### 現在の Playground 構造

- Playground は .NET WASM + hand-written HTML/CSS/JavaScript 構成で、右カラムに lint 結果を表形式で表示している。
- `LintInterop` から JS に JSON を返し、`main.js` で結果を描画する構造になっている。
- 既存 UI に graph library は入っていないが、結果ペインに別タブを足す余地はある。
- 現在の result 表示は診断一覧に最適化されており、workflow 全体の流れを俯瞰するには別表現が必要である。

### Core 側の workflow 構造

- Seiton.Core は parse 後に workflow AST を持っており、job / step / `needs` / `if` / `strategy` / `workflow_call` / `parallel` step などの情報が保持されている。
- `WorkflowVisitor` と `IPass` により AST 走査の仕組みがすでにあるため、flow 専用の collector を追加する余地がある。
- `StringNodeId` などの handle は `ParseResult` の寿命に依存するため、flow 出力用 DTO は走査中に文字列へ解決して構築する必要がある。
- `parallel` step は AST 上で明示的に表現されているため、UI 側で YAML を再解釈せずとも boundary を取り出せる。

### 制約と注意点

- matrix は宣言情報は取れるが、動的 expression を含む場合に「実行時の全展開結果」を静的に確定できない。
- reusable workflow job は参照先 workflow の中身を現在の 1 ファイル parse だけでは追えない。
- `fix` は修正処理と通常診断出力が主目的であり、flow 表示まで責務を広げると CLI 契約が曖昧になる。
- Playground で YAML を別経路で解析すると、lint と可視化の解釈差分が発生しやすい。

## 決めたこと

### 1. CLI 入口は専用コマンドではなく `check` の新フォーマット拡張にする

flow 可視化は lint 対象 workflow の別表現であり、まずは `check` の出力契約として扱う。

#### WHY

- 既存の `json` / `sarif` と同じ拡張点に載せられる。
- ユーザーが新しい専用コマンドを覚える必要がない。
- Playground 向けの共通データ契約を CLI からも直接確認できる。

### 2. 最初に定義するのは human-readable 形式ではなく `flow-json`

Mermaid や text 図は魅力があるが、最初に固定すべきなのは再利用可能な machine-readable 契約である。

#### WHY

- Playground がそのまま消費できる。
- 将来 Mermaid/text/SVG exporter を足す場合も二重実装を避けられる。
- まず表現すべき構造を JSON で明文化した方が、UI と CLI の責務分離が明確になる。

### 3. `flow-json` は `check` 専用にし、`fix` では非対応とする

`fix` は修正後のファイル出力と診断制御が中心であり、flow 可視化のような読み取り専用契約は `check` に閉じ込める。

### 4. v1 のスコープは最小集合に絞る

v1 で含めるのは次の要素に限定する。

- jobs
- `needs` による job 間 edge
- 各 job の steps
- job / step の `if`
- `uses` / `run` などの step 種別
- `parallel` step の boundary
- strategy / matrix の宣言情報のみ

v1 で含めないものは次の通り。

- matrix の完全展開
- reusable workflow の参照先内部グラフ
- drag によるノード再配置
- 高度な LOD 制御や状態永続化

### 5. Playground は result と切り替える flow タブを追加する

既存の editor/result レイアウトは維持し、右カラムで result と flow を切り替える。

#### WHY

- 既存 UI を大きく壊さずに導入できる。
- モバイル時の退避がしやすい。
- 表形式の診断とグラフ表示を同じ文脈で比較できる。

### 6. Playground の v1 interaction は zoom / pan / node click detail に限定する

初期段階では閲覧性を優先し、自由ドラッグやレイアウト編集は後続フェーズへ回す。

### 7. Playground 描画は D3.js を使う

SVG ベースの描画、zoom / pan、ノード選択の基盤を短期間で整えることを優先する。

### 8. 実装は test-first で進める

各フェーズで production code より先に red テストを追加し、そのテストを最小実装で green にする流れを守る。

#### WHY

- Core、CLI、Playground の三層にまたがるため、契約を先に固定しないと解釈差分が入りやすい。
- `flow-json` は UI 実装より先に出力契約の安定性が重要であり、テストがその土台になる。
- incremental に進めないと、どの層で仕様の取り違えが起きたかを切り分けにくい。

### 9. アロケーション悪化は許容しない

flow collector と Playground backend は parser/linter の近傍で動くため、実装前後の benchmark を計測し、allocation と平均実行時間の退行を監視する。

#### WHY

- Core 側は hot path に近く、軽い補助機能でも allocation 増加が全体性能に波及しうる。
- Playground も lint 実行のたびに flow を返す構造になるため、都度の余分な allocation を放置しにくい。
- 「見える価値」のための機能追加で性能特性を悪化させるのは避けるべきである。

## 想定する `flow-json` の責務

`flow-json` は「workflow の構造を人間・機械の両方が解釈できるように落とした中間表現」とする。

最低限、次の情報を持てることを目標とする。

- workflow 単位のメタデータ
- job 一覧と job ID
- `needs` edge
- job ごとの step 列
- step が `run` / `uses` / `parallel` / `workflow_call` などのどれか
- `if` 条件の raw 表現
- matrix / strategy の宣言有無と注釈
- reusable workflow job を opaque leaf として示すための種別

この JSON は CLI の stdout 出力と Playground backend API の共通契約として扱う。

## 実装フェーズ

### フェーズ共通ルール

各フェーズで code 変更が入る場合は、次の順序を守る。

1. 対象フェーズの期待振る舞いを表す red テストを先に追加し、失敗を確認する。
2. そのフェーズに必要な最小限の production code を実装する。
3. 追加したテストが green になることを確認する。
4. 同フェーズに関連する狭い回帰テストを流し、必要なら周辺テストを追加する。

また、Core または Playground backend に手を入れるフェーズでは、変更前後で benchmark を採取し、Mean と Allocated の比較を残す。

### フェーズ 0 — 契約整理

**WHY**: 実装に入る前に、CLI と Playground が共有する `flow-json` の責務を固定する必要がある。

#### 実施内容

- `flow-json` DTO の最小形を定義する。
- jobs / edges / steps / parallel boundary / opaque reusable workflow job の表現を決める。
- matrix は「宣言だけ含める」ことを文書上で明示する。
- `check` 専用フォーマットであることを明示する。
- 実装前 benchmark の採取対象とコマンドを決める。

**完了条件**: 実装前に、何が v1 の出力対象で何が非対象かが文書だけで判断できる。

---

### フェーズ 1 — Core の flow collector

**WHY**: CLI と Playground が同じ構造を使うためには、AST から 1 回で flow 情報を取り出せる collector が必要である。

#### 実施内容

- Core flow collector の期待 DTO を表す red テストを先に追加する。
- `WorkflowVisitor` / `IPass` を再利用した flow collector を追加する。
- `ParseResult` が生きている間に string handle を DTO に解決する。
- `parallel` step を境界つきで表現する。
- reusable workflow job を opaque leaf として表現する。
- 追加した Core テストが green になることを確認する。
- 実装前後で Core/Lint 系 benchmark を比較し、allocation 退行がないことを確認する。

**完了条件**: parse 済み workflow から `flow-json` DTO を安定して組み立てられる。

---

### フェーズ 2 — CLI `check --format flow-json`

**WHY**: Playground より先に CLI から契約を確認できると、UI 実装とデバッグの土台になる。

#### 実施内容

- CLI の `check --format flow-json` を表す red テストを先に追加する。
- `OutputFormat` に `flow-json` を追加する。
- `check` 実行時に flow collector を呼び、JSON を stdout に出す。
- `fix` では unsupported として扱う。
- 既存の `github-actions` 既定値や config bridge との優先順位を崩さないことを確認する。
- 追加した CLI テストが green になることを確認する。

**完了条件**: `seiton check --format flow-json` で workflow 構造を JSON として取得できる。

---

### フェーズ 3 — Playground backend 連携

**WHY**: UI 側で YAML を再解釈せず、lint と同じ parse 結果から flow を取得する必要がある。

#### 実施内容

- Playground backend の flow JSON 返却を表す red テストを先に追加する。
- Playground backend に flow JSON を返す API を追加する。
- lint 診断 API と flow API を分離する。
- backend 側で collector を使い、JS 側は描画に専念させる。
- 追加した backend テストが green になることを確認する。
- 実装前後で Playground lint/interop 系 benchmark を比較し、allocation 退行がないことを確認する。

**完了条件**: Playground から flow JSON を取得し、frontend がそのまま使える。

---

### フェーズ 4 — Playground の flow タブ UI

**WHY**: 目標は「parallel を含む workflow の全体像を読みやすくすること」であり、最終的な価値は UI で発揮される。

#### 実施内容

- result / flow タブ UI の振る舞いを表す red テストを先に追加する。
- result / flow のタブ UI を追加する。
- D3.js により SVG ベースの graph を描画する。
- zoom / pan / node click detail を実装する。
- `parallel` boundary と job DAG が視覚的に区別できるようにする。
- 追加した UI テストが green になることを確認する。

**完了条件**: Playground 上で workflow の flow を result と切り替えて閲覧できる。

---

### フェーズ 5 — テストと回帰確認

**WHY**: Core、CLI、Playground の三層にまたがるため、各層で契約テストが必要である。

#### 実施内容

- フェーズ 1-4 で追加した red/green テスト群を統合的に見直す。
- Core で flow collector の期待値テストを拡張し、境界ケースを補う。
- CLI で `check --format flow-json` の出力テストを拡張し、unsupported ケースも確認する。
- Playground で flow API の JSON テストと、必要最小限の UI タブ切替テストを拡張する。
- 全体の `dotnet test` を実施する。
- 実装前に採取した baseline と比較して benchmark を再実行し、Mean と Allocated の退行がないことを確認する。

**完了条件**: flow 可視化追加による既存機能の退行がなく、主要ケースで期待どおりの構造が得られる。

## 将来フェーズで扱うもの

次の項目は v1 では扱わず、別フェーズで検討する。

- Mermaid や text など人間向け export の追加
- matrix 完全展開
- reusable workflow の参照先読込み
- ノードのドラッグ再配置
- 表示密度に応じた高度な LOD
- share/permalink に flow タブ状態を含めるかどうか

## 関連ドキュメント

- `.github/docs/Seiton_CLI_spec.md`
- `.github/docs/Seiton_CLI_csharp_spec.md`
- `.github/docs/Seiton_Playground_spec.md`
- `.github/docs/Seiton_Playground_csharp_spec.md`
- `.github/docs/Seiton_Parser_csharp_spec.md`

## 現時点の結論

flow 可視化は、専用コマンドや UI 先行の個別実装ではなく、まず `check --format flow-json` という共通契約を作り、それを Playground が消費する形で進めるのが最も整合的である。

この方針なら、Core の parse 済み AST を唯一の truth source としながら、CLI と Playground の両方で同じ flow 表現を扱える。v1 は最小スコープで価値を出し、その後に Mermaid、matrix 展開、reusable workflow 深掘りなどを段階的に追加する。
