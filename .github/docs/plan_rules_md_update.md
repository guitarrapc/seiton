# docs/rules.md 拡充プラン（Why / 修正時の注意）

## 背景・課題

`docs/rules.md` は各ルールについて **何を検出するか（Summary）** と **直し方の例（Remediation）** を載せているが、次が不足している。

| 不足している観点 | 例 |
|---|---|
| **Why（意図）** | なぜそのパターンが危険／非推奨なのか（攻撃面・運用リスク） |
| **修正時の注意（Cautions）** | Remediation をそのまま適用したときの副作用・追加作業 |
| **診断メッセージとの同期** | `checkout-persist-credentials` は診断／auto-fix に `git push` 要確認の文言があるが、`rules.md` の Remediation にはない |

その結果、lint メッセージとドキュメントで読むべき情報が分断され、特に **auto-fix やセキュリティ系** では「直したら CI が壊れた」が起きやすい。

## 目標

1. 全ルール（`seiton rules` の 61 件）に、読者が判断できる **Why** と **修正時の注意** を追加する。
2. 診断メッセージ・`Seiton_Linter_spec.md` §8.4・`rules.md` の三者で、修正ガイダンスの文言を揃える。
3. `.github/docs/docs_authoring_guidelines.md` §2.1 のセクションテンプレートを更新し、以降の新規ルールも同じ形で書く。

## 提案するセクション構成（ルール 1 件あたり）

`docs_authoring_guidelines.md` §2.1 を次の順に改訂する。

| # | 見出し | 内容 |
|---|---|---|
| 1 | **Summary** | 現状どおり（1 文で検出内容） |
| 2 | **Why** | 2〜4 文。脅威モデル・運用上の理由。仕様の §4.4 一行要約を膨らませる程度でよい |
| 3 | **Example trigger** | 現状どおり |
| 4 | **Remediation** | 現状どおり（推奨修正＋YAML） |
| 5 | **When fixing（修正時の注意）** | 箇条書き 1〜5 項目。副作用・auto-fix の限界・手動追従が必要な箇所 |
| 6 | **Notes**（任意） | 既存の `<details>` / 検出詳細 |
| 7 | **Configuration**（任意） | 既存どおり |

**命名:** ユーザー向けは **「When fixing」**（または **「Cautions when fixing」**）に統一。実装側の `FixHint` 定数と対応づける場合はコメントで明記。

**診断メッセージとの関係:**

- 診断本文は短く保ち、長い注意は `rules.md` の **When fixing** に寄せる。
- ただし **auto-fix 直後に必ず確認すべき 1 行**（例: `checkout-persist-credentials` の git 認証）は、診断／`DiagnosticFix` 説明と **When fixing の先頭** の両方に同じ文言を置く（現状の `FixHint` パターンを標準化）。

## 作業の進め方（フェーズ）

### Phase 0 — テンプレート・同期ルール（1 PR）

- [x] `docs_authoring_guidelines.md` §2.1 を上記テンプレートに更新
- [x] `docs/rules.md` 冒頭に「各ルールの読み方」（Summary / Why / Remediation / When fixing）を 1 段落追加
- [x] 診断文言を持つルール一覧を機械的に洗い出し（`FixHint` / `DiagnosticFix` / 定数 `Message`）→ Phase 1 のチェックリスト化

#### Phase 0 実施結果: 診断文言ソース抽出（機械抽出）

抽出コマンド:

```shell
rg "FixHint|DiagnosticFix\(|Message\s*=|WarningMessage|ErrorMessage|InfoMessage|DiagnosticMessage" src/Seiton.Core/Linting/Rules --files-with-matches
rg "DiagnosticFix\(" src/Seiton.Core/Linting/Rules --files-with-matches
```

抽出結果（重複除外済み、Phase 1 チェック対象）:

- [ ] `artipacked`
- [ ] `bot-conditions`
- [ ] `checkout-persist-credentials`
- [ ] `deny-read-all`
- [ ] `deny-write-all`
- [ ] `forbidden-uses`
- [ ] `id-naming`
- [ ] `if-expr-wrapper`
- [ ] `job-permissions-required`
- [ ] `job-timeout-minutes-required`
- [ ] `matrix`
- [ ] `popular-action-inputs`
- [ ] `run-env-context-direct-use`
- [ ] `run-inputs-context-direct-use`
- [ ] `run-secrets-context-direct-use`
- [ ] `runner-no-latest`
- [ ] `secrets-whole-context-access`
- [ ] `template-injection`
- [ ] `unpinned-uses`
- [ ] `unsound-condition`
- [ ] `unsound-contains`

### Phase 1 — 高リスク・auto-fix あり（優先）

副作用が大きい、または `seiton --fix` で機械適用されるルールから着手。

| 優先度 | ルール ID | 理由 |
|---|---|---|
| P0 | `checkout-persist-credentials` | 診断と docs の乖離が顕在（git push 等） |
| P0 | `deny-write-all`, `deny-read-all`, `job-permissions-required` | auto-fix が権限を変え、後続 step が失敗しうる |
| P0 | `template-injection`, `run-*-context-direct-use` | auto-fix が script / env を書き換え、heredoc・複合式は未対応 |
| P0 | `id-naming` | `needs:` は更新するが `needs.<id>.outputs` 式は未更新 |
| P1 | `runner-no-latest`, `popular-action-inputs`, `unsound-condition`, `if-expr-wrapper`, `job-timeout-minutes-required` | partial auto-fix |
| P1 | `unpinned-uses`, `unpinned-image` | ネットワーク fix・更新コスト・ignore 設定 |

#### Phase 1 実施結果

- [x] `checkout-persist-credentials`
- [x] `deny-write-all`
- [x] `deny-read-all`
- [x] `job-permissions-required`
- [x] `template-injection`
- [x] `run-env-context-direct-use`
- [x] `run-secrets-context-direct-use`
- [x] `run-inputs-context-direct-use`
- [x] `id-naming`
- [x] `runner-no-latest`
- [x] `popular-action-inputs`
- [x] `unsound-condition`
- [x] `if-expr-wrapper`
- [x] `job-timeout-minutes-required`
- [x] `unpinned-uses`
- [x] `unpinned-image`

実装内容は `docs/rules.md` に対する **Why** / **When fixing** の追加。既存の Remediation 例と Configuration は維持し、auto-fix 可能条件と副作用（特に認証・権限・ネットワーク依存）を明示した。

### Phase 2 — セキュリティ／シークレット（手動修正が多い）

Remediation はあるが注意が書かれていないルール群。

#### Phase 2 実施結果

- [x] `dangerous-triggers`
- [x] `secrets-whole-context-access`
- [x] `expr-undefined-var`
- [x] `cache-poisoning`
- [x] `self-hosted-runner`
- [x] `insecure-commands`
- [x] `workflow-secrets`
- [x] `job-secrets`
- [x] `unredacted-secrets`
- [x] `secrets-outside-env`
- [x] `overprovisioned-secrets`
- [x] `deny-inherit-secrets`

実装内容は `docs/rules.md` の対象ルールに **Why** / **When fixing** を追加し、手動修正時の副作用（権限境界・秘密情報スコープ・イベント差分）を明示することに集中した。

### Phase 3 — 正しさ・供給網・online（Why は短く、注意は必要時のみ）

構文・グラフ系は Why が自明なものは 1〜2 文に抑える。online ルールは API／トークン前提を When fixing に記載。

#### Phase 3 実施結果

- [x] Correctness 未反映分に `Why` を追加（`job-structure` 〜 `outdated-action-runner`）
- [x] Supply Chain 未反映分に `Why` を追加（`credentials`, `unpinned-tools`, `archived-uses`, `ref-version-mismatch`, `forbidden-uses`, `github-app-token-inputs`, `use-trusted-publishing`）
- [x] Online 4 ルールに `Why` を追加（`known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs`）
- [x] 注意が必要なルールに限定して `When fixing` を追加（`concurrency-limits`, `artipacked`）

実装方針として、Phase 3 は「Why は短く」「注意は必要時のみ」を維持し、既存の Remediation/Notes/Configuration 構成は崩さずに補強した。

### Phase 4 — 仕様・スキル同期

- [x] `Seiton_Linter_spec.md` §4.4 は一行のまま、詳細 Why は `rules.md` に集約（authoring policy どおり）
- [x] §8.4 Fixable Rule Catalog の「Review downstream…」系文言を `rules.md` When fixing と突合（例: `deny-write-all` の spec 記述「read-all に置換」は実装が `{}` — **要修正の不整合**）
- [x] `.claude/skills/seiton/references/rules.md` / `src/Seiton/Skills/references/rules.md` は `docs/rules.md` へのリンクまたは同期方針を決める

#### Phase 4 実施結果

- `Seiton_Linter_spec.md` §8.4 の `deny-write-all` fix 記述を実装準拠に修正（`write-all -> {}` ベースライン）。
- `Seiton_Linter_spec.md` §8.5 の例示文言も同様に更新し、fix safety の説明を実装と整合。
- skills 参照ファイル（`.claude/.../rules.md`, `src/Seiton/.../rules.md`）に「`docs/rules.md` を source of truth とする」同期方針を追記。

## ルール別プラン一覧

凡例:

- **Why 追加:** 新規 **Why** 節に書く要点（ドラフト）
- **When fixing:** 新規 **When fixing** 節に書く要点（ドラフト）
- **既存 Notes:** 既に `<details>` や長文がある → 移設・要約の方針
- **同期:** 診断／§8.4／実装を揃える作業

---

### Correctness

| Rule ID | Fix | Why 追加（要点） | When fixing（要点） | 既存 Notes / 同期 |
|---|---|---|---|---|
| `job-structure` | ✗ | 無効な job 定義は Actions が解釈できず、実行時エラーまたは意図しないスキップになる | reusable `uses` と executable `runs-on`+`steps` は排他。どちらの job にしたいか決めてから片方を削除 | — |
| `reusable-workflow` | ✗ | `with`/`secrets` の誤配置は呼び出し契約違反で実行失敗 | `uses` job から `container`/`steps` 等を外すと実行環境が変わる。 callee 側の必須入力を確認 | — |
| `permissions` | ✗ | 過剰な scalar 権限はトークン侵害時の blast radius を広げる | `read-all`/`write-all` から scope 列挙へ移行すると、**足りない scope で後続 action が失敗**する。`deny-*` / `job-permissions-required` と合わせて設計 | `permissions` と `deny-read-all` の Remediation が重複 → 相互リンク |
| `needs-graph` | ✗ | 依存グラフ誤りは並列化・デプロイ順序バグ | 循環解消時は **ビジネス上の正しい DAG** を再設計。unknown `needs` は job 名 typo と削除の両方ありうる | — |
| `shell-name` | ✗ | 未対応 shell は runner 上で実行不能 | `defaults.run.shell` と step `shell` の両方を揃える。Windows 専用 shell を Linux job に入れない | — |
| `id-naming` | △ | 不正 id は `needs` / `steps.<id>` 参照を壊す | auto-fix は **ASCII case-insensitive な `needs:` 文字列のみ**更新。`${{ needs.old_id.outputs.x }}` は手動。重複 slug 時は fix なし | §8.4 と同期 |
| `glob-pattern` | ✗ | 誤った filter は意図しないイベント発火／未発火 | GitHub の filter 構文と git ref 制約は別物。`branches`/`paths` の組み合わせを公式 cheat sheet で確認 | 診断に Docs URL あり → When fixing にリンク |
| `runner-label` | ✗ | 未知ラベルはキュー滞留、OS 混在は flaky | ラベル変更は **実在 runner / larger runner 契約**と一致させる。matrix 全組み合わせを確認 | — |
| `runner-no-latest` | △ | `*-latest` は OS/イメージが予告なく変わりビルドが壊れる | `fix-mapping` 未設定時は **検出のみ**。pin 後は利用ツールチェーン（Node 等）の互換を再確認 | Configuration 既存 → When fixing に auto-fix 条件を移す |
| `popular-action-inputs` | △ | typo input はサイレントにデフォルト動作（セキュリティ・機能両方） | auto-fix は **一意な近傍候補のみ**。意図した別名 input に置換されないか diff 確認 | — |
| `action-shell-is-required` | ✗ | composite で shell 省略は実行時エラー | `bash`/`pwsh` 等は step 内容と runner OS に合わせる | action メタデータのみ |
| `matrix` | ✗ | 不正 matrix は展開失敗または空ジョブ | `include`/`exclude` 変更は組み合わせ爆発・課金に影響 | — |
| `env-var` | ✗ | 非推奨命名は可搬性・secret マスクの問題 | リネーム時は **全 step の `$VAR` 参照**を追従 | — |
| `if-cond` | ✗ | 常真/常偽・構文誤りは gate 無効化 | 条件変更は **fork PR での実行可否**に直結しうる | — |
| `fake-ternary` | ✗ | `&& \|\|` 偽三項は falsy 値で誤分岐 | ネイティブ `if` や明示分岐に置換時、空文字・0 の扱いを確認 | — |
| `if-expr-wrapper` | △ | `${{ }}` 欠落は文字列比較として誤評価 | auto-fix は単一行 scalar のみ。ブロック `if` は手動 | — |
| `unsound-condition` | △ | YAML 改行 chomping で `if` が常真になりうる | `\|-`/`>-` 変更後、**意図した multiline 条件**か再テスト | fix 説明は実装と同期済み |
| `concurrency-limits` | ✗ | 並行実行でデプロイ競合・コスト増 | `cancel-in-progress: true` は **進行中の本番デプロイを殺しうる**。group キー設計が重要 | opt-in ルールである旨を Why に明記 |
| `unsound-contains` | ✗ | 空白区切り疑似リストの部分一致バイパス | `fromJSON` 化は **入力が JSON 配列である**前提。文字列 contains のままなら別ルール | severity error/info の使い分けを Why に |
| `bot-conditions` | ✗ | `github.actor` 等は fork 文脈で偽装されうる | `pull_request.user.*` は **イベントによって未定義**。PR 以外の trigger では別ガード | 抑制条件を Notes から Why/When fixing へ要約 |
| `artipacked` | ✗ | checkout 認証＋危険 path の artifact で credential 漏洩 | `persist-credentials: false` は **checkout-persist-credentials と同じ副作用**（git push 等）。upload path の絞り込みだけでは v6+ の `$RUNNER_TEMP` 経路に注意 | 既存 Notes 充実 → When fixing に git/artifact 両方 |
| `deprecated-commands` | ✗ | 廃止 workflow commands は runner で拒否・漏洩リスク | `$GITHUB_OUTPUT` 等へ移行時、**composite の output 契約**を維持 | Docs URL を When fixing に |
| `dispatch-inputs` | ✗ | 不正 inputs は UI/API から起動不能 | 25 個上限・choice options は **既存 dispatch 呼び出し**と互換 | — |
| `schedule-event` | ✗ | 無効 cron は静かに失敗 or 高頻度実行 | 最小間隔違反は意図した頻度か確認。timezone は IANA 名 | — |
| `workflow-call-input-default` | ✗ | 型不一致 default は callee 実行時エラー | required input に default を付けない。boolean/number リテラル厳守 | — |
| `local-action-inputs` | ✗ | ローカル action 契約違反は実行時失敗 | `action.yml` 変更は **呼び出し元 workflow 全箇所**に影響 | workflow + action 両方 |
| `outdated-action-runner` | ✗ | 非推奨 runtime（node12 等）は runner 削除で突然死 | action の `using:` を上げると **入出力・composite 構造**が変わる場合あり | version tag 例は意図的例外 |

---

### Security

| Rule ID | Fix | Why 追加（要点） | When fixing（要点） | 既存 Notes / 同期 |
|---|---|---|---|---|
| `template-injection` | △ | PR タイトル等の event データを shell に直埋めすると RCE 相当 | auto-fix は 1 step 1 pass・heredoc/単一引用符/script action は **手動**。env 化後も **ログに env が出ないか**確認 | §8.4 partial 条件を When fixing に |
| `dangerous-triggers` | ✗ | `pull_request_target` 等は base リポジトリ権限で untrusted コードが走りうる | `pull_request` へ変更すると **secrets が使えなくなる** trade-off。guard だけでは不十分なケースあり | 複数 Approach 既存 → When fixing に trade-off 表 |
| `run-env-context-direct-use` | △ | `${{ env.X }}` 直書きは展開タイミングが shell とずれる | heredoc `<<'EOF'` 内は auto-fix しない。POSIX vs PowerShell の変数構文 | — |
| `run-secrets-context-direct-use` | △ | secrets 直書きはログ・クラッシュダンプに載りやすい | 既存 `env` に同一 secret が **1 つだけ**あるときのみ自動置換。複合式は hint のみ | — |
| `run-inputs-context-direct-use` | △ | inputs 直書きも同様 | 新規 `env` 挿入は flow style `env` ではスキップ。挿入後の **マスク・ログ**確認 | §8.4 長文を When fixing に要約 |
| `secrets-whole-context-access` | ✗ | `secrets` オブジェクト全体参照は列挙漏れ・過剰露出 | 必要なキーだけに分解。動的キー名はルール外の設計判断 | 診断定数メッセージと同期 |
| `expr-undefined-var` | ✗ | 未定義 context は実行時に空/null でサイレント失敗 | reusable workflow 出力は静的に解決できない場合あり。**意図した optional** か確認 | — |
| `cache-poisoning` | ✗ | fork PR から共有 cache キーへ poison 可能 | 対策は path 限定・`pull_request` 分離・環境保護など **複数案** — 単一 Remediation 不可 | 既存 bullet Approaches → When fixing で優先順位 |
| `self-hosted-runner` | ✗ | 永続 runner + untrusted コードはラテラルムーブ | ラベルガードだけでは不十分なことがある。エフェメラル runner 検討 | — |
| `insecure-commands` | ✗ | `ACTIONS_ALLOW_UNSECURE_COMMANDS` は PATH 攻撃を再許可 | 環境ファイル API へ移行後、**カスタム action が set-output 依存**していないか | — |

---

### Permissions & Secrets

| Rule ID | Fix | Why 追加（要点） | When fixing（要点） | 既存 Notes / 同期 |
|---|---|---|---|---|
| `deny-write-all` | ✓ | `write-all` は全リソースへの書き込みを許可 | auto-fix は `permissions: {}` に置換（**read-all ではない**）。その後 **各 job/step が必要な scope を足す** | **spec §8.4「read-all に置換」と実装不一致 → spec 修正** |
| `deny-read-all` | ✓ | `read-all` は読み取り範囲が広すぎる | auto-fix は explicit mapping ベースライン。空 `{}` だけでは **checkout 等が失敗**しうる | Remediation 例と auto-fix の差を明記 |
| `job-permissions-required` | ✓ | 暗黙継承は過剰権限の温床 | 推論 fix は **popular action カタログ依存**。未知 action のみの job は `{}` になり **後で手動追加** | 既存段落を Why / When fixing に分割 |
| `credentials` | ✗ | プライベート registry 画像は認証必須。平文 password は漏洩 | `public-registries` に誤追加すると **pull 失敗**。secrets 名はリポジトリに存在するものを指定 | Configuration 既存 |
| `checkout-persist-credentials` | △ | 永続化された git 認証は後続 step・artifact から窃取されうる | **`persist-credentials: false` 後は `git push`/`git fetch` 等で明示認証が必要**（`git remote set-url` / `gh auth setup-git`）。auto-fix は式値は不可 | **診断 `FixHint` を When fixing 先頭にコピー** |
| `workflow-secrets` | ✗ | workflow 全体 env の secret は全 job に露出 | job/step へ移すと **他 job から参照不可** — 本当に単一 job か確認 | — |
| `job-secrets` | ✗ | job 全体 env の secret は全 step に露出 | step `env` へ分割後、**同 job 内の他 step** が secret を要しないか | — |
| `unredacted-secrets` | ✗ | secret を echo するとログ・fork に漏れる | `::add-mask::` は **既に出力後では遅い**。デバッグは secret 値ではなく存在フラグのみ | — |
| `secrets-outside-env` | ✗ | `if`/`uses` 直参照はログ・式評価で露出しやすい | `env` 化しても **式の評価順**は変わらない。条件の意味を再確認 | — |
| `overprovisioned-secrets` | ✗ | 1 step に多数 secret は漏洩面積増 | 分割後は **step 間で secret を渡さない**（必要なら job 再設計） | 閾値 5 を Why に |
| `deny-inherit-secrets` | ✗ | `secrets: inherit` は callee へ全 secret を渡す | 明示列挙は **callee の inputs 契約**と一致させる。足りないと reusable 実行失敗 | — |

---

### Supply Chain

| Rule ID | Fix | Why 追加（要点） | When fixing（要点） | 既存 Notes / 同期 |
|---|---|---|---|---|
| `unpinned-uses` | network | タグ/branch は第三者が書き換え可能（supply chain） | SHA pin は **意図したリリースと一致**するか `ref-version-mismatch` も確認。`--fix` はネットワーク・レート制限。ignore は組織ポリシーと整合 | Configuration 既存 |
| `unpinned-image` | network | digest 未固定はイメージ差し替え可能 | digest 更新は **破壊的ベースイメージ変更**あり。pull quota / mirror 設定 | — |
| `unpinned-tools` | ✗ | setup action の `latest` はスキャナ等の結果が日々変わる | JSON データセット更新が必要な action 追加時は Update パイプライン | データ更新手順を When fixing に |
| `archived-uses` | ✗ | archived repo は CVE 修正なし | 代替 action へ移行時 **入出力・バージョン**の互換テスト | — |
| `ref-version-mismatch` | ✗ | コメントと SHA の不一致は監査を欺く | path 付き ref（`owner/repo/v1@sha`）は **意図的な別バージョン**の可能性 — 人間確認 | — |
| `forbidden-uses` | ✗ | 組織ポリシー違反の action 利用 | `allow` 例外は **セキュリティレビュー**前提。deny 追加は既存 workflow 一斉失敗 | Configuration 既存 |
| `github-app-token-inputs` | ✗ | 過剰 App token は横断的書き込み | `repositories` 省略 + `owner` は **インストール全体**に及びうる。permission-* は最小 | — |
| `job-timeout-minutes-required` | △ | 無制限 job はコスト・ハングのリスク | auto-fix は `fix.defaults.job-timeout-minutes` 設定時のみ。値は **ワークロードに合わせ調整** | — |
| `use-trusted-publishing` | ✗ | 長期 PAT より OIDC の方が漏洩時影響が小さい | パッケージレジストリ側の trusted publishing 設定が **先に必要** | 複数 registry 例を When fixing に整理 |

---

### Online (opt-in)

| Rule ID | Fix | Why 追加（要点） | When fixing（要点） | 既存 Notes / 同期 |
|---|---|---|---|---|
| `known-vulnerable-actions` | ✗ | 既知 CVE バージョンの継続利用 | アップグレードは **メジャー bump** の可能性。pin 更新後に e2e | `GITHUB_TOKEN` / GHES API を Why に |
| `impostor-commit` | ✗ | 到達不能 SHA は悪意ある fork 偽装の典型 | 置換 SHA は **公式リリース／タグ**から取得。fork PR では特に注意 | — |
| `ref-confusion` | ✗ | tag/branch 同名は解決先が曖昧 | SHA 固定が最も明確。ポリシーで `refs/tags/` 強制など | — |
| `stale-action-refs` | ✗ | 古い SHA pin は修正 CVE を取り込んでいない | 更新は **意図したメジャー系列**を維持するか確認（動作変更） | — |

---

## 代表ルールの詳細ドラフト（実装時のたたき台）

### `checkout-persist-credentials`（P0・乖離解消の模範）

**Why（案）**

`actions/checkout` はデフォルトで job 内に git 認証情報を残す。v5 以前は `.git/config`、v6 以降は `$RUNNER_TEMP` 配下に保存され、後続の `run` や危険な artifact path から参照されると、トークン窃取につながる。

**When fixing（案）**

- `persist-credentials: false` にすると、**同一 job 内の後続 step で `git push` / 認証付き `git fetch` が失敗**しうる。必要ならその step だけ `actions/checkout` を再実行するか、`git remote set-url` でトークン付き URL を設定するか、`gh auth setup-git` 等で明示的に認証する。
- `seiton --fix` は `persist-credentials` が式（`${{ }}`）の場合は適用しない。
- `artipacked` と併用時は、認証無効化に加え artifact の `path` も見直す（`artipacked` 節を参照）。

**Remediation:** 現状 YAML は維持し、上記を Remediation の直後に置く。

**同期:** `CheckoutPersistCredentialsRule.FixHint` ↔ 診断 ↔ When fixing（同一文言）。

---

### `deny-write-all`（P0・spec/実装/docs 三点整合）

**Why（案）**

`write-all` は GitHub のほぼ全リソースへの書き込みを許可し、漏洩した `GITHUB_TOKEN` の影響が最大になる。

**When fixing（案）**

- `seiton --fix` は `write-all` を `permissions: {}` に置換する（**すべての write を即座に剥奪**）。その後、失敗した action に合わせて `contents`/`packages` 等を **明示追加**する。
- Remediation 例のように事前に最小 scope を設計してから手動で直す方が安全であり推奨。
- `permissions` ルールの warning（scalar 推奨）と併せて読む。

**同期:** `Seiton_Linter_spec.md` §8.4 の「read-all に置換」記述を **実装（`{}`）に合わせて修正**。

---

### `job-permissions-required`（P0）

**Why（案）**

job に `permissions` が無いと、workflow 既定や `GITHUB_TOKEN` のデフォルト権限をそのまま継承し、不要な write が付いたまま実行される。

**When fixing（案）**

- auto-fix が挿入する scope は **カタログ上の popular action から推論**した最小値。カスタム action や `run` だけの job では `{}` になり、GitHub API を呼ぶ step は **実行時 403** になりうる。
- 推論後に `deny-write-all` / workflow 既定 permissions との **合成**を確認する。
- reusable workflow call job（`uses:`）にも挿入されうる — callee が期待する token 権限と矛盾しないか確認。

---

## 品質チェックリスト（各ルール PR ごと）

- [ ] **Why** が「検出内容の言い換え」になっていない（脅威・運用・コストのいずれかが書いてある）
- [ ] **When fixing** に、Remediation を機械適用したときの **最低 1 つの失敗モード**がある（該当なしの場合は「特になし（構文修正のみ）」と明記）
- [ ] auto-fix ありルールは **§8.4 の Partial/Fixable 条件**と一致
- [ ] 診断に fix hint があるルールは **文言一致**（または意図的差分を Notes に記載）
- [ ] Example trigger が他ルールを意図せず発火していない（authoring §2.2）
- [ ] `seiton rules` 表の Fix 列（✓/△/✗）と矛盾しない

## 見積もり

| フェーズ | 内容 | 目安 |
|---|---|---|
| Phase 0 | テンプレート・冒頭説明 | 0.5 日 |
| Phase 1 | P0/P1 約 15 ルール | 2〜3 日 |
| Phase 2 | Security + Permissions 残り | 2 日 |
| Phase 3 | Correctness 残り + Supply + Online | 2〜3 日 |
| Phase 4 | spec §8.4 整合・スキル参照 | 0.5〜1 日 |

**合計:** おおよそ 7〜9 日（レビュー込み）。ルールごとに小 PR に分割するとレビューしやすい（カテゴリ単位 × 5〜7 PR 想定）。

## 完了定義

- `docs/rules.md` の全 61 ルールに **Why** と **When fixing**（該当なしは一行で可）がある
- `checkout-persist-credentials` について、診断メッセージと `rules.md` を読んだだけで git 認証の追従が分かる
- `docs_authoring_guidelines.md` が新テンプレートを規定している
- `Seiton_Linter_spec.md` §8.4 と実装の既知不整合（`deny-write-all` 等）が解消されている

## 参考

- ユーザ向けルールリファレンス: `docs/rules.md`
- 執筆ガイド: `.github/docs/docs_authoring_guidelines.md`
- 仕様（一行要約・fixable カタログ）: `.github/docs/Seiton_Linter_spec.md` §4.4, §8.4
- 診断 fix hint 実装例: `src/Seiton.Core/Linting/Rules/CheckoutPersistCredentialsRule.cs`

---

## 実装メモ（2026-06-04）: `expr-undefined-var.assume-events` 配線

### 実装内容

- `ExprUndefinedVarRule` で `rules.expr-undefined-var.assume-events` を取得し、入力コンテキスト推論に渡すようにした。
- `DynamicContextTypeBuilder.BuildInputsOverride` を拡張し、`assume-events` に `workflow_dispatch` または `workflow_call` が含まれる場合、`inputs` を strict-empty ではなく loose object として扱うようにした。
- これにより、イベント混在時（例: `on: [push, workflow_dispatch]`）に `inputs.*` を参照する式で発生していた false positive を抑制できる。

### テスト（Red → Green）

- 追加: `RuleRegression_ExprUndefinedVarRule_AssumeEvents_InputsContext`
  - `assume-events` なし: `inputs.target` は未定義診断が出る（期待どおり）
  - `assume-events: [issue_comment]`: 未定義診断が出る（期待どおり）
  - `assume-events: [workflow_dispatch]`: 未定義診断が抑制される
  - `assume-events: [workflow_call]`: 未定義診断が抑制される
- 回帰確認: `dotnet test`（2456 tests, failed 0）

### ベンチマーク

- 実行: `dotnet run -c Release --filter "*CoreLintBenchmark*"`（`src/Seiton.Benchmark`）
- 比較対象: 既存の `BenchmarkDotNet.Artifacts/results/Seiton.Benchmark.CoreLintBenchmark-report-default.md`
- 結果: 主要指標（Mean / Allocated）に差分なし（レポート差分なし）。

### 性能評価

- 変更は `VisitWorkflowPre` で 1 回だけ実行される config 分岐追加で、job/step ホットパスには新規処理を入れていない。
- 追加ロジックは `assume-events` の短い配列走査のみで、lint 全体性能への寄与はノイズレベル。
- したがって性能低下は観測されず、追加改善は不要。

### API / UX 観点

- 設定キーは既存の `rules.expr-undefined-var.assume-events` をそのまま有効化しており、API 追加なし。
- ユーザーの直感（「この workflow は実質 dispatch/call 入力を扱う」）をそのまま設定に反映できる挙動となった。

### 仕様整合

- `.github/docs/Seiton_Linter_spec.md` 5.8.7（event-type context を与えて false positive を抑制）と整合。
