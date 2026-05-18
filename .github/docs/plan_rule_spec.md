# ルール仕様 3 層化 計画

> 作成日: 2026-05-18
> 目的: 複雑なルールについて、仕様説明を `Core intent` / `Current supported scope` / `Deferred scope` の 3 層で整理し、レビュー時に「既存契約の不具合」と「契約拡張」の境界を明確にする。

---

## 1. 背景

`artipacked` では、検知意図そのものは一貫していても、実際にどこまでを Seiton の現在契約として扱うか、どこから先を未対応とするかが文書上で分離されていなかったため、レビューで仕様策定と実装修正が混ざりやすかった。

この計画では、同様のレビュー増殖が起きやすい複雑ルールについて、説明を次の 3 層に分けて記述する。

1. `Core intent`
2. `Current supported scope`
3. `Deferred scope`

`artipacked` は適用済みなので、本計画はそれ以外のルールに横展開するためのものとする。

---

## 2. 目的

この作業の目的は実装変更ではなく、仕様境界の固定である。

期待する効果:

- レビュー時に、指摘が「既存契約からの逸脱」か「新しい契約追加」かを判別しやすくする
- user-facing docs と source-of-truth spec の温度差を減らす
- 別セッションでルール単位に安全に進められるようにする
- 実装未対応の範囲を先に明示し、後追いの議論を減らす

---

## 3. 記述テンプレート

各対象ルールで、少なくとも以下を追加する。

### 3.1 `Core intent`

そのルールが何を守るのかを 1-2 文で書く。

書く内容:

- 守りたいユーザー上の失敗や危険
- 実装詳細ではなく、ルールの存在理由

書かない内容:

- 具体的な AST ノード列挙
- 例外条件の全列挙
- 現時点の limitation

### 3.2 `Current supported scope`

Seiton が現在、契約として保証している検知範囲を書く。

書く内容:

- どのドキュメント種別、どのノード、どの条件を扱うか
- 同値扱いしている構文や正規化
- 保守的扱い、既定値扱い、severity 分岐など、現在仕様に入っているもの

書かない内容:

- 将来やりたいが未実装の内容
- 参考実装との比較

### 3.3 `Deferred scope`

意図的に未対応として残している範囲を書く。

書く内容:

- 現在未相関のケース
- 現在扱わない sink / source / config form
- false positive / false negative を避けるために切っている範囲

書き方:

- 「未対応」ではなく、なぜ今は deferred にしているかが分かる表現にする
- 可能なら 1 つ具体例を入れる

---

## 4. 更新対象ファイル

各ルールで原則更新するファイルは次の 4 つ。

- `.github/docs/Seiton_Linter_spec.md`
- `.github/docs/Seiton_Linter_csharp_spec.md`
- `.github/docs/Seiton_Linter_go_spec.md`
- `docs/rules.md`

方針:

- `Seiton_Linter_spec.md` を source of truth とする
- C# / Go spec は source-of-truth の粒度に合わせる
- `docs/rules.md` は user-facing に短く整理する
- 実装変更を伴わない限り、テスト追加は不要

---

## 5. 対象ルールと追記内容

### Phase 1: 優先度高

#### 5.1 `template-injection`

理由:

- source、sink、緩和条件、auto-fix 境界が多い
- レビューで「これは検知対象か」「env 経由は safe か」がぶれやすい

追加する内容:

- `Core intent`
  - untrusted event-origin data が shell / script sink に直接入ることを防ぐ
- `Current supported scope`
  - 現在の source 群
  - 現在の sink 群 (`run`, `actions/github-script` など)
  - `env:` 経由を indirection として扱う現契約
  - 現在の auto-fix 対象と非対象
- `Deferred scope`
  - shell quoting の完全意味解析
  - sanitizer 認識
  - 複雑な組み立て式や heredoc の一部ケース

更新時の注意:

- 仕様を拡張しない
- 既存の「Partial auto-fix」境界を 3 層に再配置する

#### 5.2 `expr-undefined-var`

理由:

- dynamic context override、strict/loose の切り替え、local reusable workflow 解決など契約が広い
- 実装は強いが、どこまで静的解決を保証しているかが読み取りづらい

追加する内容:

- `Core intent`
  - 現在スコープで利用できない context 参照や、存在しない strict property 参照を防ぐ
- `Current supported scope`
  - `step.run`, `step.if`, `step.env`, `step.with`
  - `matrix`, `steps`, `needs`, local action outputs, local reusable workflow outputs
  - remote reusable workflow outputs は loose 扱い
- `Deferred scope`
  - remote reusable workflow の strict output resolution
  - 実行時データ依存の動的 shape の完全解決
  - 外部 fetch 前提の contract expansion

更新時の注意:

- 「型システム全体」ではなく、ルールとしての保証境界を前面に出す

#### 5.3 `credentials`

理由:

- 1 ルールで「registry credentials の欠落」と「平文 password」の 2 つの契約を持つ
- 何を守る rule なのかが user-facing に見えにくい

追加する内容:

- `Core intent`
  - private/custom registry 利用時の pull failure / unsafe credential handling を防ぐ
- `Current supported scope`
  - `job.container`, `job.services.*`
  - public registry built-in + extend
  - literal password error と expression password allowed
- `Deferred scope`
  - credential の強度、rotation、secret provenance
  - registry-side authorization correctness

更新時の注意:

- 1 rule 2 responsibilities を無理に分割せず、1 つの core intent に束ねる

#### 5.4 `checkout-persist-credentials`

理由:

- `artipacked` と隣接しており、役割差分を明示した方がよい
- legacy / v6+ の違いはあるが、どこまで結果を追う rule ではないかを固定したい

追加する内容:

- `Core intent`
  - checkout-managed credentials を不用意に残さない
- `Current supported scope`
  - `actions/checkout` の `persist-credentials: false` 強制
  - missing / true / expression の扱い
  - legacy `.git/config` と v6+ `$RUNNER_TEMP` の背景説明
- `Deferred scope`
  - 後続 step の actual leak correlation
  - push が必要な workflow での explicit auth design

更新時の注意:

- `artipacked` との責務差を user-facing docs でも明示する

---

### Phase 2: 優先度中

#### 5.5 `cache-poisoning`

理由:

- trust boundary の議論が多く、どの trigger を untrusted とみなすかで話が広がりやすい

追加する内容:

- `Core intent`
  - untrusted trigger で cache state が汚染されることを防ぐ
- `Current supported scope`
  - 現在の untrusted trigger set
  - cache action / cache-like pattern の対象
  - config extend の効き方
- `Deferred scope`
  - repository-specific trust policy
  - cache key semantic analysis
  - restore-only / partial isolation の精密判定

#### 5.6 `self-hosted-runner`

理由:

- trigger trust、runner isolation、実環境依存が絡みやすい

追加する内容:

- `Core intent`
  - untrusted workflow execution を self-hosted 環境に載せない
- `Current supported scope`
  - 現在の untrusted trigger 判定
  - self-hosted label 判定の現契約
- `Deferred scope`
  - 実際の runner hardening 状況の証明
  - ephemeral / isolated runner の安全性認識

#### 5.7 `forbidden-uses`

理由:

- deny/allow pattern の policy rule で、仕様拡張が入りやすい

追加する内容:

- `Core intent`
  - 組織ポリシーに反する action / workflow reference の利用を防ぐ
- `Current supported scope`
  - pattern syntax
  - allow が deny を上書きする現契約
  - verbose/info の扱い
- `Deferred scope`
  - remote metadata に基づく policy
  - owner trust / provenance-aware policy

#### 5.8 `overprovisioned-secrets`

理由:

- warning threshold rule であり、何を guarantee しないかを明確にしたい

追加する内容:

- `Core intent`
  - step/job に対する secret exposure surface を広げすぎない
- `Current supported scope`
  - しきい値ベースの警告
  - step env / job secrets の対象範囲
- `Deferred scope`
  - secret sensitivity weighting
  - actual use reachability analysis

---

### Phase 3: 優先度中〜低

#### 5.9 `reusable-workflow`

理由:

- local resolvable contract と remote unresolved contract の差が大きい

追加する内容:

- `Core intent`
  - reusable workflow call contract mismatch を防ぐ
- `Current supported scope`
  - local call resolution
  - `with` / `secrets` validation
  - incompatible execution key rejection
- `Deferred scope`
  - remote workflow deep validation
  - network fetch 前提の contract checking

#### 5.10 `runner-label`

理由:

- known label set、matrix expansion、conflict detection が混在している

追加する内容:

- `Core intent`
  - invalid or conflicting hosted runner selection を防ぐ
- `Current supported scope`
  - built-in + extend
  - OS family conflict
  - matrix-expanded mixed list conflict
- `Deferred scope`
  - repository-local label governance
  - fully dynamic runner expression resolution

#### 5.11 `local-action-inputs`

理由:

- 1 ルールの中に metadata validation が多数入っている

追加する内容:

- `Core intent`
  - statically resolvable local action call / metadata mismatch を防ぐ
- `Current supported scope`
  - unknown input, missing required, deprecated input
  - `runs.using` validation
  - JS entry-point existence
  - branding forward checks
- `Deferred scope`
  - remote action metadata resolution
  - dynamic path / generated metadata resolution

---

## 6. ルールごとの文書反映方針

各ルールで反映場所は次の通り。

### 6.1 `docs/rules.md`

もっとも読みやすい形で 3 層を書く。

推奨位置:

- ルールの導入文の直後に
  - `Core intent`
  - `Current supported scope`
  - `Deferred scope`

方針:

- user-facing なので短めに書く
- 実装内部名は必要最小限にする

### 6.2 `Seiton_Linter_spec.md`

表の rule summary 列、または non-normative explanation に圧縮して入れる。

方針:

- source-of-truth では、3 層のうち少なくとも `Core intent` と `Deferred scope` が読める状態にする
- 文量が増えすぎる場合は、summary には短く入れ、該当 rule 節が将来必要ならそこへ逃がす

### 6.3 `Seiton_Linter_csharp_spec.md`

現行実装が何を保証するかを `Current supported scope` として具体化する。

方針:

- 実装依存の話はここに寄せる
- allocation / single-pass / cached parsing のような C# 実装事情は、必要なものだけ残す

### 6.4 `Seiton_Linter_go_spec.md`

Go 側は C# と同じ契約境界を保ちつつ、未実装差があれば明示する。

方針:

- source-of-truth とズレないことを優先する
- Go 実装が未追随なら、誤って parity を装わない

---

## 7. 作業順

別セッションでの推奨順序:

1. `template-injection`
2. `expr-undefined-var`
3. `credentials`
4. `checkout-persist-credentials`
5. `cache-poisoning`
6. `self-hosted-runner`
7. `forbidden-uses`
8. `overprovisioned-secrets`
9. `reusable-workflow`
10. `runner-label`
11. `local-action-inputs`

理由:

- 先に security-critical かつレビューが膨らみやすいルールを固める
- 次に policy rule を整理する
- 最後に correctness-heavy で説明量の多いルールを整える

---

## 8. セッションごとの完了条件

1 ルールごとに次を満たせば完了とする。

- `docs/rules.md` に 3 層が追加されている
- `Seiton_Linter_spec.md` に契約境界が反映されている
- `Seiton_Linter_csharp_spec.md` に current implementation boundary が反映されている
- `Seiton_Linter_go_spec.md` が source-of-truth と矛盾していない
- 実装変更を伴わない限り、コードやテストは触らない

---

## 9. 非ゴール

この計画では以下は行わない。

- ルール実装の挙動変更
- 新しい false positive / false negative の修正
- 参考ツールとの feature parity 宣言
- 全ルール一括の大規模リライト

必要なら、個別ルールの 3 層化作業中に見つかった仕様不足を別 PR / 別計画に切り出す。

---

## 10. 最初の着手単位

最初の別セッションでは、まず `template-injection` だけを対象にするとよい。

理由:

- 3 層化の効果が最も大きい
- security intent と auto-fix boundary の両方を含み、テンプレート化しやすい
- ここで書式が固まれば、他ルールへの横展開がやりやすい
