# seiton フィードバック — githubactions-lab

**実施日:** 2026-06-08
**環境:** Windows 10, seiton 0.9.26 (.NET 10.0.8, win-x64)
**対象リポジトリ:** [githubactions-lab](https://github.com/guitarrapc/githubactions-lab)（GitHub Actions の実験・デモ用ワークフロー約 120 ファイル）

---

## 1. 実行経過サマリー

| フェーズ | コマンド | 結果 |
|---------|---------|------|
| 初期スキャン（設定なし） | `seiton --min-severity error` | **46 errors** / 120 files |
| 初期スキャン（全 severity） | `seiton --min-severity warning`（設定なし） | **46 errors, 35 warnings** |
| 設定作成 | `.github/seiton.yaml` を作成・`seiton validate-config` | `config valid` |
| 設定後スキャン（初回） | `seiton --min-severity error` | **0 errors**（exclusions 過多の暫定設定） |
| `run-*-context-direct-use` の `--fix` 検証 | exclusions なしで `seiton --fix --dry-run` | **48/53 件**が自動修正可能と判明 |
| context 系自動修正適用 | `seiton --fix`（14 ファイル） | **48 件**修正 + `secrets-access.yaml` を手動 1 件 |
| README 同期 | `README.md` / `README-ja.md` の該当スニペット更新 | ワークフローと整合 |
| 最終 exclusions | デモ意図の warning のみ残す | **0 issues**（119 files, 2 excluded, 15 suppressed） |
| composite actions 含む | `seiton --include-actions` | **0 issues**（127 files） |

### 反復サイクル

```
seiton（設定なし）→ 46 errors を確認
    ↓
.github/seiton.yaml 作成（fix / online rules / agentic exclusions）
    ↓
【訂正】run-env-context-direct-use を先に exclusion していたが、
        --fix を試すと 48/53 件が自動修正可能だった
    ↓
seiton --fix --dry-run（context exclusions なし）
  → run-env-context-direct-use: 47件
  → run-inputs-context-direct-use: 1件
  → secrets-access.yaml のみ未対応（手動 env 追加が必要）
    ↓
seiton --fix → 14 ワークフローを修正
README.md / README-ja.md のスニペットも同期
    ↓
残り warning はデモ意図のみ exclusion
  - unredacted-secrets（matrix-secret, secrets-access, _reusable-workflow-called）
  - if-cond（job-needs-skip-handling-bad）
  - env-var（merge-branch）
  - runner-no-latest（matrix）
    ↓
seiton → 0 issues ✅
```

---

## 2. 検出の妥当性評価

### 2.1 適切な検出（そのまま活かせる）

| ルール | 例 | 評価 |
|--------|-----|------|
| `run-env-context-direct-use` | `_reusable-dump-context.yaml` 等 | **適切**。**`--fix` で `${{ env.* }}` → `${VAR}` / `$env:VAR` に変換可能**（47 件一括修正） |
| `run-inputs-context-direct-use` | `create-release.yaml` | **適切**。**`--fix` で step `env:` ブロック追加 + shell 変数化** |
| `run-secrets-context-direct-use` | `secrets-access.yaml` | **適切**。1 行 `echo` のみのケースは自動修正されず **手動で `env:` 追加**が必要 |
| `deny-inherit-secrets` | `auto-dump-context.yaml`, `reusable-workflow-caller-nest.yaml` | **適切**。最小権限の原則に沿う |
| `dangerous-triggers` | `dump-context.yaml`（`workflow_dispatch` + 危険トリガー） | **適切** |
| `job-timeout-minutes-required` | `agentics-maintenance.yml`, `*.lock.yml` | **適切**。`help:` で `fix.defaults.job-timeout-minutes` を案内しており、設定と連動した UX が良い |
| `unpinned-image` | `container-job.yaml` の `golang:1.25` | **適切**。digest 未固定を正しく検出 |
| `if-expr-wrapper` | `cache.yaml` の `if:` に `${{ }}` 不足 | **適切**。実際に修正が必要なバグに近いケース |
| `unredacted-secrets` | `matrix-secret.yaml` の `echo ... ${SECRET}` | **適切**（デモ意図） |
| `if-cond` | `job-needs-skip-handling-bad.yaml` の `if: ${{ false }}` | **適切**。ファイル名どおり「悪い例」として正しく検出 |
| `env-var` | `merge-branch.yaml` の小文字 `upstream`/`branch` | **妥当だが文脈依存**（具体例は **§2.3.1**） |
| online rules（有効化済） | `known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs` | 本リポジトリは既に SHA pin 済みのため **該当なし**。ルール有効化自体は問題なし |

### 2.2 デモリポジトリ特有の扱い

本リポジトリは **意図的に「悪い例」「比較例」を含む** ため、設定なしでは 46 errors と多く出る。これは seiton の誤検出ではなく、**exclusions / `discovery.skip-agentic-workflows` による調整が前提**のリポジトリ。

特に有効だった設定:

```yaml
discovery:
  skip-agentic-workflows: true   # monthly-oss-repo-status.lock.yml を自動スキップ

exclusions:
  - file: .github/workflows/agentics-maintenance.yml
  - file: .github/workflows/*.lock.yml
  - file: .github/workflows/injection-attack-via-context.yaml
  # デモ意図の warning のみ（unredacted-secrets, if-cond 等）
```

**重要:** `run-env-context-direct-use` 系は exclusion ではなく **`seiton --fix` を先に実行すべき**。本リポジトリでは 48 件が自動修正され、README スニペットも更新した。

### 2.3 改善を検討したい検出

| 項目 | 内容 |
|------|------|
| 初回の情報量 | 設定なしで 46 errors は初見では多い。末尾の `hint: run 'seiton init'` は有用だが、**デモ/学習リポジトリ向けの exclusion テンプレート**があると初回体験がさらに良くなる |
| `env-var` と実用パターン | 下記 **§2.3.1** 参照。実務でよくある書き方とルールの期待がずれる |
| duplicate exclusion | 同一ファイルに複数 exclusion エントリがあると `info[parse]` で通知される（良い）。マージを促すメッセージは分かりやすい |

#### 2.3.1 `env-var` と実用パターン — 具体例

**ルールが何を指摘しているか**

`env-var` は、workflow / job / step の `env:` キー名が **`[A-Z_][A-Z0-9_]*`（大文字・数字・アンダースコア）** 以外だと warning を出す。GitHub Actions が `GITHUB_ENV` へ書き込む環境変数は OS 間で扱いが揃うよう、慣習的に大文字スネークケースが推奨される、という観点のルール。

**seiton の出力例**（`merge-branch.yaml`）:

```
warning[env-var]: job.env key 'upstream' is not portable; use [A-Z_][A-Z0-9_]* naming
  --> .github/workflows/merge-branch.yaml:17:7
     |
  17 |       upstream: ${{ inputs.upstream }}
     |       ^^^^^^^
     |

warning[env-var]: job.env key 'branch' is not portable; use [A-Z_][A-Z0-9_]* naming
  --> .github/workflows/merge-branch.yaml:18:7
     |
  18 |       branch: ${{ inputs.branch }}
     |       ^^^^^
     |
```

**指摘されている YAML**（抜粋）:

```yaml
# .github/workflows/merge-branch.yaml
jobs:
  merge:
    env:
      upstream: ${{ inputs.upstream }}   # ← 'upstream' が小文字のため warning
      branch: ${{ inputs.branch }}       # ← 'branch' も同様
    steps:
      - uses: devmasx/merge-branch@...
        with:
          from_branch: ${{ env.upstream }}
          target_branch: ${{ env.branch }}
```

ここでの意図は、`workflow_dispatch` の inputs を job レベル `env:` に載せ、`with:` で `${{ env.upstream }}` として再利用する、という **よくある実用パターン**。動作上は問題ないが、キー名が小文字のため `env-var` に引っかかる。

**別例**（`matrix-secret.yaml` — workflow レベル `env:`）:

```
warning[env-var]: workflow.env key 'fruit' is not portable; use [A-Z_][A-Z0-9_]* naming
  --> .github/workflows/matrix-secret.yaml:10:3
     |
  10 |   fruit: APPLES
     |   ^^^^
     |
```

```yaml
# .github/workflows/matrix-secret.yaml
env:
  fruit: APPLES          # ← シークレット名への間接参照用。小文字キーで warning

jobs:
  dereference:
    steps:
      - run: echo "env:${fruit} secret:${SECRET}"
        env:
          SECRET: ${{ secrets[env.fruit] }}
```

`fruit` は後続ステップで `secrets[env.fruit]` のように **動的にシークレット名を解決する** デモ用のキー。こちらも命名規則の観点では warning 対象。

**なぜ「文脈依存」と評価したか**

| 観点 | 内容 |
|------|------|
| ルールの意図 | `GITHUB_ENV` 経由で export する変数と揃え、クロスプラットフォームな shell から参照しやすくする |
| 実務での実情 | inputs 名やドメイン用語（`upstream`, `branch`）をそのまま `env:` キーに使うことは多い。third-party action の `with:` に渡す中間変数としても自然 |
| 本リポジトリの判断 | いずれも **意図的なデモ** のため `env-var` のみ exclusion。`--fix` 対象ではない（キー改名は参照箇所全体の変更が必要） |

**seiton が期待する書き方の例**（`merge-branch.yaml` の場合）:

```yaml
jobs:
  merge:
    env:
      UPSTREAM: ${{ inputs.upstream }}
      BRANCH: ${{ inputs.branch }}
    steps:
      - uses: devmasx/merge-branch@...
        with:
          from_branch: ${{ env.UPSTREAM }}
          target_branch: ${{ env.BRANCH }}
```

あるいは `env:` を介さず inputs を直接渡す:

```yaml
      - uses: devmasx/merge-branch@...
        with:
          from_branch: ${{ inputs.upstream }}
          target_branch: ${{ inputs.branch }}
```

**フィードバック（seiton 側）**: 現状のメッセージだけでは「何が portable でないのか」が伝わりにくい。`help:` に上記のような **代替パターン（大文字リネーム / inputs 直渡し）** を示すと、warning を見ただけで次のアクションが判断できる。

---

## 3. 自動修正（`--fix`）の使い勝手評価

### 3.1 成功した自動修正例

#### コンテナイメージの digest pin（`unpinned-image`）

**Before** (`container-job.yaml`):

```yaml
container:
  image: golang:1.25
```

**After**（`fix.images.enable-network: true` により自動解決）:

```yaml
container:
  image: golang:1.25@sha256:dd7d32e19b28621cd982082397fc0510d396805b717d5e77466aa2dd692340de
```

- ネットワーク解決に約 1 秒（`--verbose` で `resolved 1 pin(s) ... in 1092.4 ms` と表示）
- 同一イメージ（`mcr.microsoft.com/dotnet/sdk:10.0`）は 2 ファイル目から **0.0 ms（キャッシュ）** と表示され、効率が良い
- **評価: 非常に良い**。CLI フラグ不要で config から有効化でき、diff も読みやすい

#### `if:` 式の `${{ }}` ラップ（`if-expr-wrapper`）

**Before** (`cache.yaml`):

```yaml
if: (github.event_name == 'push' || ...) && needs.detect_library_change.outputs.cache-hit != 'true'
```

**After**:

```yaml
if: ${{ (github.event_name == 'push' || ...) && needs.detect_library_change.outputs.cache-hit != 'true' }}
```

- 1 行の最小 diff。**評価: 良い**

### 3.2 設定連動で有効になるが本 run では未適用だった修正

| 設定 | 期待される修正 | 本リポジトリでの状況 |
|------|---------------|---------------------|
| `fix.defaults.job-timeout-minutes: 15` | `job-timeout-minutes-required` の自動追加 | 該当ファイルは exclusion 済みのため未実演。`help:` メッセージは明確 |
| `runner-no-latest.fix-mapping` | `ubuntu-latest` → `ubuntu-24.04` 等 | `matrix.yaml` は意図的に `ubuntu-latest` を matrix 値に含むため exclusion。マッピング設定の存在は有用 |
| `fix.pinning.enable-network: true` | `unpinned-uses` の SHA pin | 大半のワークフローが既に SHA pin 済み。 |

### 3.3 `run-*-context-direct-use` 自動修正の詳細（今回の主な発見）

**Before** (`default-shell.yaml` / bash):

```yaml
run: |
  echo "BRANCH=${{ env.BRANCH_NAME }}" | tee -a "$GITHUB_ENV"
  echo ${{ env.BRANCH }}
```

**After** (`seiton --fix`):

```yaml
run: |
  echo "BRANCH=${BRANCH_NAME}" | tee -a "$GITHUB_ENV"
  echo ${BRANCH}
```

- pwsh では `$env:BRANCH_NAME` / `$env:BRANCH` に適切に変換される
- 既存の workflow/job レベル `env:` を活用し、不足分のみ step `env:` を追加
- `_reusable-dump-context.yaml` は 16 箇所を一括修正（約 68 ms）

**`create-release.yaml`（inputs）** は expression を step `env:` に移動:

```yaml
run: echo "value=${TAG}" | tee -a "$GITHUB_OUTPUT"
env:
  TAG: ${{ inputs.tag || (...) }}
```

### 3.4 自動修正されない（手動 or exclusion が必要）

| ルール | 理由 |
|--------|------|
| `run-secrets-context-direct-use`（単一行・env なし） | `secrets-access.yaml` は手動で `env:` 追加が必要だった |
| `if-cond`（`if: ${{ false }}`） | ジョブ削除は意図を変える可能性があるため未対応（妥当） |
| `env-var` | リネームは参照箇所全体の変更が必要 |
| `unredacted-secrets` | echo 削除は手動判断。デモワークフローは exclusion |

### 3.4 `--fix` モードの UX 総評

| 観点 | 評価 |
|------|------|
| dry-run の diff 出力 | ⭐⭐⭐⭐⭐ unified diff で変更箇所が明確 |
| 修正サマリー表 | ⭐⭐⭐⭐⭐ `Would Fix / Remaining` のファイル別・ルール別集計が有用 |
| config からのネットワーク有効化 | ⭐⭐⭐⭐⭐ `--enable-pin-network` を毎回打たなくてよい |
| 残件の再表示 | ⭐⭐⭐⭐ fix 後に残った warning を一覧表示。fix 専用モードでも lint 結果が見える |

---

## 4. ログ・出力の把握しやすさ評価

### 4.1 良い点

1. **リッチテキスト診断形式** — `error[rule-id]: message` + ファイル位置 + ソース行 + キャレット + `help:` の構成は actionlint 系と同等以上に読みやすい
2. **末尾サマリー表** — ファイル別 Errors/Warnings のランキングで優先度付けしやすい
3. **`--verbose`** — discovery パス、除外ファイル数、suppressed ルール内訳、ネットワーク解決時間が出る
4. **`validate-config`** — 設定エラーを早期検出できる
5. **抑制の透明性** — `2 excluded, 44 suppressed` のように件数が表示される
6. **ヒント行** — `--min-severity error`、`seiton init`、`--include-actions` への誘導がある

### 4.2 改善余地

| 項目 | 詳細 |
|------|------|
| 初回（設定なし）の圧倒感 | 120 ファイルで 46 errors。サマリー表はあるが、**ルール別の件数内訳**は `--verbose` まで見ないと分からない。デフォルトでもルール別 Top N があると把握しやすい |
| `--include-actions` の案内 | デフォルト実行末尾に `hint` があるが、actions に問題がある場合は **最初にも** 気づけるとよい |
| exit code | warning のみでも exit 1。CI では `--min-severity error` と組み合わせる必要があり、hint はあるが README/skill に明記されていると安心 |
| `info[parse]` の位置表示 | duplicate exclusion 警告は `seiton.yaml:1:1` となり、重複エントリの行特定に少し手間 |

---

## 5. 設定（`.github/seiton.yaml`）の使い勝手

本リポジトリで最終的に採用した設定の要点:

```yaml
rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true

fix:
  defaults:
    job-timeout-minutes: 15
  pinning:
    enable-network: true
    min-age-days: 14
  images:
    enable-network: true

discovery:
  skip-agentic-workflows: true
```

**評価:**

- `fix` セクションに pinning / images / timeout をまとめられる設計は直感的
- `rules.<id>.fix-mapping` で runner ラベル修正方針を宣言できるのは良い
- online rules は `enabled: true` のみで有効化でき、トークンがあれば追加チェックが走る
- exclusions の `file` + `rules` スコープはデモリポジトリに最適。`discovery.skip-agentic-workflows` との組み合わせも自然

---

## 6. 変更されたワークフローファイル

### context 系 `--fix`（14 ファイル + 手動 1 件）

| ファイル | 修正件数 | 主なルール |
|---------|---------|-----------|
| `_reusable-dump-context.yaml` | 16 | `run-env-context-direct-use` |
| `default-shell.yaml` | 6 | `run-env-context-direct-use` |
| `gitops-k8s-manifest.yaml` | 5 | `run-env-context-direct-use` |
| `setenv-script.yaml` | 4 | `run-env-context-direct-use` |
| `setup-dotnet.yaml` | 4 | `run-env-context-direct-use` |
| `workflowdispatch-inputs.yaml` | 4 | `run-env-context-direct-use` |
| `fake-ternary.yaml` | 3 | `run-env-context-direct-use` |
| 他 7 ファイル | 各 1〜2 | env / inputs context |
| `secrets-access.yaml` | 1（手動） | `run-secrets-context-direct-use` |

### その他 `--fix`（イメージ pin 等）

| ファイル | ルール | 変更内容 |
|---------|--------|---------|
| `cache.yaml` | `if-expr-wrapper` | `if:` に `${{ }}` 追加 |
| `container-job.yaml` | `unpinned-image` | `golang:1.25` → digest pin |
| `container-service.yaml` | `unpinned-image` | `redis:8` → digest pin |
| `dotnet-build.yaml` | `unpinned-image` | dotnet SDK image → digest pin |
| `dotnet-build-only-tag.yaml` | `unpinned-image` | 同上 |

`README.md` / `README-ja.md` の該当 YAML スニペットも同期済み。

---

## 7. 総合評価

| 観点 | 評価 | コメント |
|------|------|---------|
| **検出の正確性** | ⭐⭐⭐⭐☆ | セキュリティ・ベストプラクティス系の検出は妥当。デモリポジトリでは exclusions 設計が必須 |
| **使い勝手（素直さ）** | ⭐⭐⭐⭐☆ | `seiton` → `--fix --dry-run` → `--fix` の流れが自然。config でネットワーク pin を恒久化できる |
| **ログの把握しやすさ** | ⭐⭐⭐⭐☆ | 診断フォーマット・サマリー表・verbose は優秀。初回の大量検出時のルール別内訳があるとさらに良い |
| **自動修正** | ⭐⭐⭐⭐⭐ | イメージ digest pin・if 式ラップは高品質。config 連動の timeout / runner mapping も設計が良い |
| **設定の表現力** | ⭐⭐⭐⭐⭐ | exclusions / discovery / fix / online rules の分離が明確 |

### 推奨アクション（seiton 側）

1. 初回スキャン時に **ルール別件数 Top N** をデフォルト出力に含める（`--verbose` なしでも）
2. `run-secrets-context-direct-use` の単一行 `echo "${{ secrets.X }}"` パターンも `--fix` 対応すると完結する
3. `env-var` の `help:` に inputs 直接参照の代替パターンを追記
4. duplicate exclusion の `info[parse]` で重複エントリの行番号を示す

### 推奨アクション（本リポジトリ / skill 側）

- **`run-*-context-direct-use` は exclusion より先に `seiton --fix` を試す**（skill / adoption ドキュメントに明記）
- `.github/seiton.yaml` をコミットし、CI（`seiton --min-severity error`）で継続チェック
- exclusion は **修正不能かデモ意図の warning のみ**に限定（`unredacted-secrets`, `if-cond` 等）

---

## 8. 参考: 実行ログファイル

作業中に保存したログ（リポジトリルート）:

| ファイル | 内容 |
|---------|------|
| `refs/seiton-errors.txt` | 設定なし error スキャン（途中） |
| `refs/seiton-full.txt` | 設定後の全 severity スキャン（13 warnings） |
| `refs/seiton-fix-dryrun.txt` | `--fix --dry-run --verbose` 出力 |
| `refs/seiton-fix-applied.txt` | `--fix --verbose` 適用結果 |
