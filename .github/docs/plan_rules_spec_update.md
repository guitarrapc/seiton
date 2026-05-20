# rules/spec 見直し計画

## 目的

`docs/rules.md` と `Seiton_Linter*.md` の役割分担を整理し、次の状態に寄せる。

- `docs/rules.md` はユーザーが最初に読むルール説明書として、誤解なく、短く、必要十分に伝わる
- `Seiton_Linter_spec.md` は言語中立の契約に集中し、実装詳細や運用ガイドで膨らまない
- `Seiton_Linter_csharp_spec.md` / `Seiton_Linter_go_spec.md` は実装差分と実装固有の契約に集中し、ロードマップや backlog を抱え込まない

---

## 調査結果

### 1. shared spec の責務が広がりすぎている

対象: `.github/docs/Seiton_Linter_spec.md`

現状の shared spec は「language-neutral な契約」と宣言している一方で、実際には以下が混在している。

- C# 実装前提の記述
- ユーザー向け運用ガイド
- auto-fix の細かい境界条件
- 実装状況・候補ルール・比較メモ

特に問題なのは以下。

- 冒頭では language-neutral を宣言しているが、本文に `Current profile note (C# runtime)` や `The default C# local-AST linter profile must include...` が入っている
- §4.5 `Rule Guidance (Operational)` が rule-by-rule の巨大な運用表になっており、`docs/rules.md` と役割が競合している
- §4.4, §4.5, §8.4 の間で、ルールの説明・fixability・注意点が複数箇所に分散している

行数も `Seiton_Linter_spec.md` 1330 行で、shared spec としては読み始めるコストが高い。

判断:

- shared spec は「契約」と「実装・利用ガイド」の境界が崩れている
- source of truth が一箇所に見えず、将来の差分同期コストが高い

### 2. shared spec に表構造の不整合がある

対象: `.github/docs/Seiton_Linter_spec.md` §4.4

規範カタログの列定義は `Rule ID | Default | Network | Required Behavior Summary` だが、以下の行では `Network` 列に network 種別ではなく auto-fix 可否が入っている。

- `if-expr-wrapper`
- `unsound-condition`

この状態だと、表の列意味が途中で崩れる。shared spec を読む実装者にとって解釈コストが高く、`§8.4 Fixable Rule Catalog` とも責務が重複する。

### 3. shared spec に implementation/status/backlog 情報が残りすぎている

対象: `.github/docs/Seiton_Linter_spec.md`

以下は shared contract より implementation plan に近い。

- C# runtime の current profile note
- default values (current C# runtime)
- 外部ツール比較の表現
- 候補ルールの現状 catalog 反映状況
- parity hardening 系の status 記述

仕様文書ポリシー上、lesson learned は残してよいが、現在の shared spec は WHAT/WHY よりも「今の C# 実装がどうなっているか」の比率が高い。

### 4. C#/Go spec は比較的健全だが、plan/backlog を抱え込んでいる

対象:

- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`

良い点:

- rule table 自体は `docs/rules.md` を user-facing source として参照しており、shared spec より役割分離ができている
- 実装固有メモという観点は明確

問題点:

- `Phase 14 Catalog Additions`
- `Planned High-Priority Candidate Rules`
- `Known Partial Parity Areas`

のような backlog/roadmap/status が spec 内に居座っている。これは仕様より implementation plan 向き。

判断:

- C#/Go spec は shared spec ほど崩れていない
- ただし「今の実装差分」と「今後やること」が混在しており、spec の密度が落ちている

### 5. cross-document 参照の壊れ・表記ゆれがある

対象:

- `.github/docs/Seiton_Linter_spec.md`
- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`

確認できた問題:

- `.github/docslinter_implementation_csharp_plan.md` のようなパス表記
- `.github/docsSeiton_Linter_spec.md` のようなスラッシュ欠落
- `.github/docsSeiton_spec.md` などの壊れた参照

加えて、参照先として書かれている linter implementation plan 文書自体が見当たらない。壊れたパス修正だけでなく、参照先の再定義も必要。

### 6. rules.md の立ち位置は適切だが、例の衛生状態が不足している

対象: `docs/rules.md`

`rules.md` は冒頭で「canonical user-facing reference」と明示しており、この立ち位置は正しい。

一方で、例の作り方に次の問題がある。

- `*-latest` 系 runner ラベル出現: 122 箇所
- unpinned `uses:` 系出現: 33 箇所
- `owner/repo/.github/workflows/reuse.yml@main` 出現: 5 箇所

このため、対象ルールとは別に以下のルールへ同時に引っかかる例が多い。

- `runner-no-latest`
- `unpinned-uses`

具体例として、以下の節で cross-trigger が起きている。

- `reusable-workflow`
- `outdated-action-runner`
- `artipacked`
- `deny-inherit-secrets`
- 多数の一般例で `runs-on: ubuntu-latest`

判断:

- rules.md の最大の品質問題は「例が対象ルールだけを説明していない」こと
- ユーザーが rule の境界を誤解する原因になる

### 7. rules.md は節によって abstraction level が揺れている

対象: `docs/rules.md`

節によって、説明の粒度がかなり異なる。

比較的読みやすい節:

- `template-injection`
- `dispatch-inputs`
- `workflow-call-input-default`

読み手負荷が高い節:

- `run-inputs-context-direct-use`
- `artipacked`
- 一部の fix 境界説明が長い節

問題の本質は「ユーザーが最初に知りたいこと」と「実装上の例外条件」が同じ階層で書かれていること。ユーザー向け doc では、まず以下が先に来るべき。

- 何を検知するのか
- どんな例で発火するのか
- どう直すのが基本か

fix の細かな skip 条件や静的解析限界は、その後の note として短く置くのがよい。

### 8. rules.md の remediation が一部で機械的すぎる

対象: `docs/rules.md`

一部のルールでは remediation が「例としては動く」が、ユーザーの本来意図を保つ説明になっていない。

例:

- `dangerous-triggers` で `pull_request_target` から `push` に変える例は、イベント意味自体を変えている
- policy 系ルールで、単一の置換案だけを示すと「それが唯一の正解」に見えやすい

判断:

- remediation は「代表解」でよいが、intent-preserving な複数の方向性を短く添える方が誤解が少ない

---

## 優先度順の修正計画

## P0

### P0-1. shared spec を contract-only に戻す

対象:

- `.github/docs/Seiton_Linter_spec.md`

やること:

- §4.4 を言語中立の規範カタログとして書き直す
- `default C# local-AST` のような実装依存表現を除去する
- §4.5 の rule-by-rule 巨大表は shared spec から外す
- shared spec に残す rule 個別説明は、契約上必要な例外だけに絞る
- `needs-graph` の diagnostic 位置のような cross-runtime で共有すべき設計判断だけ残す
- fixability の truth source は §8.4 に寄せ、§4.4 では列として持たないか、持つなら列定義を明示して全行で一貫させる

完了条件:

- shared spec を読めば「各 rule が最低限満たす契約」が追える
- user-facing な remediation や長い edge case 群は shared spec を読まなくても rules.md 側で追える
- shared spec の rule 記述は `what` と cross-runtime `why` に集中する

### P0-2. shared spec の表構造と参照を修正する

対象:

- `.github/docs/Seiton_Linter_spec.md`
- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`

やること:

- §4.4 の `Network` 列混入を修正する
- 壊れた cross-document path を全修正する
- 参照先の implementation plan 文書名を決め直す
- 存在しない implementation plan 文書を新規作成するか、参照を実在文書へ張り替える

完了条件:

- 列定義と各 row の意味が一致する
- cross-document sync rule から辿れないパスがなくなる

### P0-3. rules.md の例を「対象 rule のみ説明する例」に統一する

対象:

- `docs/rules.md`

やること:

- baseline として `ubuntu-latest` を原則やめ、`ubuntu-24.04` など version-pinned runner に置換する
- 対象 rule でない `uses:` は原則 full SHA か local path に置換する
- reusable workflow の例で `@main` を使わない
- `runner-no-latest` や `unpinned-uses` 自身を説明する節だけは例外とし、その場合は意図的な cross-trigger であることを明示する
- `artipacked`、`outdated-action-runner`、`reusable-workflow`、`deny-inherit-secrets` など cross-trigger が明確な節から先に直す

完了条件:

- 例を読んだユーザーが「この例は別 rule でも怒られるのでは」と迷いにくい
- 各節の trigger 例は、原則として対象 rule だけを説明する

### P0-4. rules.md のルール節テンプレートを固定する

対象:

- `docs/rules.md`

推奨テンプレート:

1. 何を検知するか
2. Example trigger
3. Recommended remediation
4. Notes

やること:

- 各節の最初の 1 段落は trigger と intent を短くまとめる
- 長い fix 境界や解析限界は `Notes` に送る
- 例と remediation を先に読めばユーザーが行動できる順序に揃える

完了条件:

- どの節も同じ読み順で理解できる
- 長い実装注記が本文の主役にならない

## P1

### P1-1. C#/Go spec から roadmap/backlog を切り出す

対象:

- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`

やること:

- `Phase 14 Catalog Additions`
- `Planned High-Priority Candidate Rules`
- `Known Partial Parity Areas`

を implementation plan 系ドキュメントへ移す

残すもの:

- 現在実装されている rule の runtime-specific note
- shared spec では表現できない実装契約

完了条件:

- C#/Go spec を読むと「今どう実装されているか」が分かる
- 「今後どうするか」は plan 文書に分離される

### P1-2. rules.md の remediation を intent-preserving に直す

対象:

- `docs/rules.md`

やること:

- policy 系・security 系ルールでは、単一の置換例だけでなく短い remediation 方針を添える
- 例: `dangerous-triggers` は `push` への置換だけでなく、`pull_request` への切り替え、strict guard 追加、privileged job 分離などを短く書く
- 「唯一の正解」に見える書き方を避ける

完了条件:

- remediation が「例」か「契約」かを読み手が混同しない

### P1-3. rules.md の長文 rule を二層化する

対象:

- `docs/rules.md`

優先節:

- `artipacked`
- `template-injection`
- `run-inputs-context-direct-use`
- `checkout-persist-credentials`

やること:

- 冒頭を短い user-facing summary に圧縮する
- 長い edge case 群は note か advanced details として分離する
- 必要なら appendix 相当の別文書へ切り出す

完了条件:

- 初見ユーザーが 1 スクロール以内で trigger/remediation を把握できる
- 細かな条件を失わずに、読書コストだけ下げる

## P2

### P2-1. 文書 authoring rule を明文化する

対象:

- 追加の contributor note か、この plan 実施後の follow-up 文書

最低限入れるべきルール:

- shared spec は WHAT/WHY のみ。HOW/実装状況/backlog は入れない
- language-neutral spec に実装言語名を持ち込まない
- rules.md の trigger/remediation 例は、原則として対象 rule 以外を発火させない
- cross-trigger をあえて使う場合は、その意図を注記する
- fixability の truth source は一箇所に寄せる
- cross-document path は実在ファイル名に合わせる

### P2-2. docs/rules.md の例デザインを統一する

候補ルール:

- runner 例は `ubuntu-24.04` を基本にする
- `uses:` 例は full SHA placeholder を基本にする
- shell 例は bash と PowerShell の違いが重要な節だけ明示する
- `echo ng/ok` のような意味の薄い文言は、ルール意図が分かる短いコマンドに置き換える

目的:

- docs 全体の見た目と理解速度を揃える
- 例から余計な推測をさせない

---

## 先に着手すべき具体編集順

1. `.github/docs/Seiton_Linter_spec.md` の §4.4 / §4.5 を整理し、shared spec の責務を締め直す
2. 3 つの spec から壊れた path を修正し、implementation plan の参照先を再定義する
3. `docs/rules.md` の baseline 例を一括置換する
4. `docs/rules.md` の cross-trigger が強い節から順に書き換える
5. C#/Go spec から roadmap/backlog 節を plan 文書へ退避する

---

## 付記

今回の調査で一番大きかったのは、`rules.md` の内容不足ではなく「例の純度」と「shared spec の責務逸脱」だった。まずここを直すと、spec は短くなり、rules.md は分かりやすくなり、以後の rule 追加でも diff の置き場所が明確になる。
