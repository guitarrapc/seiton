# GitHub Actions lint/fix ツール向け config 設計メモ

## 1. 現状 config への批判まとめ（ユーザー視点の UI/UX）

現状の config は、機能としては揃っているものの、**ユーザーが「何をしたいか」で自然に読める構造になっていない**のが最大の問題です。

自然に理解しやすいのは `rules` と `exclusions` までで、それ以外は実装都合の概念や内部モジュール名が前面に出ています。ルール群は rule-id ベースで整理されているのに、config 側は途中から rule-id とは別の軸で切られており、見た人が「この設定はどのルールに効くのか」「なぜここにあるのか」を直感的に追えません。

---

### 1-1. ユーザーの思考単位と config の単位がズレている

ユーザーは普通、次のように考えます。

- このルールを弱めたい
- このファイルだけ除外したい
- この判定対象を増やしたい
- このルールだけネットワーク監査を有効にしたい
- fix の挙動だけ調整したい

つまり、ユーザーの思考単位は基本的に **rule-id** です。

しかし現状 config では、`additiveCustomization`、`exprContext`、`pin_resolution`、`online_audit` のように、**ユーザーの問題ではなく内部実装の都合で切られた概念**が表に出ています。これは UI/UX 的に筋が悪いです。

---

### 1-2. 設定名が「何をしたいか」ではなく「どう実装しているか」になっている

`additiveCustomization` は典型です。  
ユーザーが考えるのは「危険イベントを追加したい」「公開レジストリ扱いを増やしたい」であって、「加算的カスタマイズをしたい」ではありません。

同様に、

- `exprContext`
- `pin_resolution`
- `online_audit`
- `token_env_vars`

なども、ユーザーにとっては目的ではなく実装の内部事情に見えます。

設定ファイルでは、**内部モジュール名ではなく、ユーザーがやりたい操作の名前**が前面に出るべきです。

---

### 1-3. rule-id と設定が直接結びついていない

ルール一覧は rule-id 単位で理解されます。  
しかし現状 config では、rule に効く補助設定が rule の近くにありません。

たとえば次の関係は、実装を知らないと読み解きづらくなっています。

- `dangerous-triggers` と `additionalDangerousEvents`
- `runner-label` と `additionalKnownHostedLabels`
- `credentials` と `additionalPublicRegistries`
- `unpinned-uses` / `unpinned-image` と `pin_resolution`
- `known-vulnerable-actions` / `impostor-commit` / `ref-confusion` / `stale-action-refs` と `online_audit`

ユーザー視点では、**その rule の設定はその rule の近くにあるべき**です。

---

### 1-4. 同じ種類の設定が複数箇所に分散している

ネットワーク利用可否、タイムアウト、並列度、fail-open のような設定が `pin_resolution` と `online_audit` に重複しています。

これは、

- どちらがどこまで効くのか分かりにくい
- 同じ意味の設定を複数箇所で書くことになる
- 将来のメンテナンスで設定 drift を起こしやすい

という問題を生みます。

同様に、抑制も `rules.enabled: false` と `exclusions` に分かれており、全体無効化と局所除外の関係が読み取りづらくなっています。

---

### 1-5. 重要度の違う設定が同じレベルに並んでいる

`rules` や `exclusions` は、ほぼすべてのユーザーが日常的に触る設定です。  
一方で、`token_env_vars`、`request_timeout_sec`、`max_concurrency` などは低レベルな実行エンジン設定です。

これらが同じレベルで表に出てくると、

- 一般ユーザーにはノイズになる
- 詳しいユーザーには危険な足場になる
- 「設定可能である必然性」が薄い項目まで露出してしまう

という問題が出ます。

特に `token_env_vars` は、公開設定として見せる必然性が弱く、誤設定や意図しない token 選択を招きやすい項目です。

---

### 1-6. 「追加専用」の思想が UI に出すぎている

`additionalDangerousEvents` のような追加専用設計は、内部モデルとしては理解できても、UI としては不自然です。

ユーザーは「最終的に何が危険イベントとして扱われるか」を設定したいのであって、「内蔵セットに追加する」という実装方式を意識したくありません。

少なくとも UI 上は、

- 最終集合を宣言する
- あるいは `extend` / `append` のような分かりやすい概念で見せる

べきです。

---

### 1-7. 命名規則と抽象度が揃っていない

現状案では、次のようなズレがあります。

- kebab-case の rule-id
- snake_case の top-level key
- `additional...` の冗長な名前
- `forbiddenUsesDenyPatterns` のような長く歪な複合語
- `eventTypes` のような曖昧な短名

これは単なる見た目の問題ではなく、**設定ファイル全体の構造理解を妨げる**問題です。

良い config では、命名規則と抽象度が揃っていて、「この階層にはこういう種類の設定が入る」と予測できる必要があります。

---

### 1-8. `rules` と `exclusions` は比較的良い

現状案でも、以下は比較的自然です。

- `rules`: ルール個別の enable / severity 調整
- `exclusions`: ファイルや job 単位の局所的な除外

この二つは、ユーザーの思考とかなり一致しています。  
したがって今後の config 設計でも、この二つは中心に据えるのがよいです。

---

## 2. 良い config が満たすべき設計原則

ここでは、今後の config を設計するときの原則を整理します。

---

### 原則1. ユーザーの思考単位で切る

設定は、内部実装やエンジン構造ではなく、**ユーザーが考える単位**で切るべきです。

このツールにおいてユーザーの思考単位は主に次です。

- rule-id
- exclusions
- analysis assumptions
- fix defaults
- network/runtime behavior

したがって、`rules` と `exclusions` を中心にしつつ、その他の設定も「何のための設定か」がすぐ分かる階層に置くべきです。

---

### 原則2. ルールに効く設定は、できるだけそのルールの近くに置く

rule に直接関係する補助設定は、その rule の近くに置くのが自然です。

たとえば、

- `dangerous-triggers` のイベント拡張
- `runner-label` の known-hosted-labels
- `credentials` の public-registries
- `forbidden-uses` の deny パターン

は、その rule に近い場所にあるほうが理解しやすいです。

---

### 原則3. 日常設定と高度設定を分離する

大半のユーザーが頻繁に触る設定と、低頻度な実行エンジン設定は分けるべきです。

日常設定:
- `rules`
- `exclusions`
- `analysis`
- `fix.defaults`

高度設定:
- network の fail-open
- timeout
- concurrency
- 監査の有効化
- 特殊な pinning 挙動

これにより、普段の config が見やすくなり、高度設定も必要なときだけ触ればよくなります。

---

### 原則4. 「何をしたいか」で名前を付ける

名前は内部都合ではなく、ユーザーがやりたい操作を表すべきです。

悪い例:
- `additiveCustomization`
- `exprContext`
- `pin_resolution`

良い方向:
- `analysis.assume-events`
- `fix.defaults.job-timeout-minutes`
- `network.fail-open`
- `dangerous-triggers.events.extend`

---

### 原則5. 同種の設定はまとめる

同じ性質の設定が複数箇所に分かれていると、理解も保守も難しくなります。

たとえば、

- ネットワーク利用に関する設定
- 実行時挙動に関する設定
- fix 既定値に関する設定

は、まとまっているべきです。

---

### 原則6. 実装の内部概念はできるだけ外に出さない

ユーザーは AST パスや監査フェーズや pin 解決 subsystem を意識したくありません。

したがって、

- 内部の engine 名
- 内部用の token 解決戦略
- subsystem 固有の命名

は、できるだけ外に出さないほうがよいです。

どうしても出す場合でも、ユーザーが理解できる概念に翻訳すべきです。

---

### 原則7. 追加専用ではなく、最終的な意味が分かる形にする

`additional...` は内部設計としては便利でも、UI としては分かりにくいです。

ユーザーに見せるなら、

- `extend`
- `append`
- `allow`
- `deny`
- `defaults`

のように、最終的に何が起きるかが読める語を使うべきです。

---

### 原則8. 命名規則を統一する

少なくとも config 表面に出るキーは、表記ルールを統一したほうがよいです。

たとえば、

- top-level と nested key は kebab-case
- rule-id も kebab-case
- 単位を持つ値は明示的な名前にする

のように揃えると、かなり見やすくなります。

---

### 原則9. 低レベル設定は本当に必要なものだけ露出する

外部公開 config に出す設定は、「多くのユーザーが調整する合理性があるか」で選ぶべきです。

露出に慎重であるべきもの:
- `token_env_vars`
- 内部キャッシュ戦略
- 内部解決順序
- subsystem 固有の細かい動作

公開する価値が高いもの:
- severity / enabled
- exclusions
- analysis assumptions
- fix defaults
- fail-open / timeout / concurrency のような明確な運用設定

---

## 3. 推奨方針: 案1 + 案2 の折衷

採用方針としては、**rule 中心**を軸にしつつ、**日常設定と高度設定を軽く分離する**のがよいです。

狙いは次です。

- ユーザーが最初に見るのは `rules` と `exclusions`
- rule に強く結びつく補助設定は rule の近く
- fixer の既定値は `fix`
- 解析上の仮定は `analysis`
- ネットワークや運用系は `network` / `audit` に分離
- 内部 subsystem 名は外に出さない

この方針なら、現状 config の問題だった

- `additiveCustomization`
- `exprContext`
- `pin_resolution`
- `online_audit`

のような内部整理っぽさをかなり減らせます。

---

## 4. UI/UX 的に筋のよい config 案（折衷案）

以下は、案1 + 案2 の折衷としての提案です。

```yaml
rules:
  job-permissions-required:
    enabled: false

  deny-write-all:
    severity: error

  dangerous-triggers:
    severity: error
    events:
      extend:
        - issue_comment

  action-shell-is-required:
    severity: warning

  runner-label:
    known-hosted-labels:
      extend:
        - ubuntu-24.04-large

  credentials:
    public-registries:
      extend:
        - registry.example.com

  forbidden-uses:
    deny:
      - some-untrusted-org/*

exclusions:
  - files: ".github/workflows/legacy-*.yml"
    rules:
      - runner-no-latest
      - job-permissions-required

  - files: ".github/workflows/release.yml"
    jobs:
      - publish
    rules:
      - credentials

analysis:
  assume-events:
    - workflow_dispatch
    - repository_dispatch

fix:
  defaults:
    job-timeout-minutes: 15

  pinning:
    enable-network: true
    min-age-days: 14
    exclude-branches:
      - main
      - master
    ignore-actions:
      - uses: "slsa-framework/.*"
        ref: ".*"

  images:
    enable-network: true
    exclude-images:
      - scratch
    exclude-tags:
      - latest

audit:
  enable-online-rules: true

network:
  fail-open: true
  timeout-seconds: 30
  max-concurrency: 4
```

---

## 5. この折衷案の良い点

### 5-1. `rules` と `exclusions` が主役のまま維持される

ユーザーが最も頻繁に触る設定が先頭にあり、理解しやすい構成です。

---

### 5-2. rule に強く紐づく補助設定が rule の近くにある

- `dangerous-triggers` のイベント拡張
- `runner-label` の known-hosted-labels
- `credentials` の public registries
- `forbidden-uses` の deny パターン

が rule の配下に入るため、どこに効く設定か直感的です。

---

### 5-3. `analysis` と `fix` が独立して意味を持つ

`exprContext` のような内部用語をやめ、`analysis.assume-events` とすることで、「これは静的解析時の仮定だ」と分かります。

また `default_job_timeout_minutes_for_fix` を `fix.defaults.job-timeout-minutes` に分解することで、fixer 用の既定値だと理解しやすくなります。

---

### 5-4. ネットワーク系設定を共通化できる

`pin_resolution` と `online_audit` に分散していた

- allow network
- fail-open
- timeout
- concurrency

のうち、共通運用設定は `network` に集約できます。

これにより、設定の重複と分散を減らせます。

---

### 5-5. 高度設定がノイズになりにくい

`rules` と `exclusions` を見れば多くのユーザーは十分で、  
さらに必要な場合だけ `analysis` / `fix` / `audit` / `network` を見ればよい構造になっています。

---

## 6. 今後さらに詰めるべき論点

この折衷案でも、まだ詰めるべき点はあります。

### 6-1. `fix.pinning` と `fix.images` の粒度

これはまだ subsystem 的です。  
将来的には `unpinned-uses` / `unpinned-image` 配下にさらに寄せる余地があります。

---

### 6-2. `audit.enable-online-rules` の表現

これもまだやや抽象的です。  
`online_audit` よりは良いですが、最終的には online rule 群の扱いをもう少し自然な語に寄せてもよいです。

---

### 6-3. `extend` と最終集合宣言のどちらを採るか

UI/UX 的には `extend` はまだ許容範囲ですが、  
より強く分かりやすさを求めるなら「最終集合の明示」に寄せる選択肢もあります。

ただし built-in 値との関係が見えにくくなるため、製品としては `extend` のほうが実用的な可能性があります。

---

## 7. 結論

現状 config の問題は、機能不足ではなく、**ユーザーが理解する単位と config の切り方がズレていること**にあります。

したがって改善方針は明確です。

- `rules` と `exclusions` を中心に据える
- rule に効く設定は rule の近くに寄せる
- `analysis` と `fix` を意味の通る名前で分離する
- 共通のネットワーク／運用設定は一か所にまとめる
- 内部 subsystem 名や低レベル実装設定は表に出しすぎない

この方針に基づく **案1 + 案2 の折衷**は、現状よりかなり自然で、製品としても説明しやすい config になります。
