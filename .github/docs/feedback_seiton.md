# seiton 移行フィードバック（githubactions-lab）

実施日: 2026-06-07
環境: Windows 11, seiton **v0.9.25** (.NET 10.0.8, win-x64)

## 目的

actionlint / zizmor / ghalint の組み合わせを seiton に置き換え、既存の除外設定を `.github/seiton.yaml` に移植したうえで、使い勝手とログの把握しやすさを評価する。

---

## 実行経過

### 1. 既存リンター設定の調査

| ツール | 設定ファイル | 主な除外 |
|--------|-------------|----------|
| **zizmor** | `.zizmor.yaml` | `impostor-commit` / `ref-version-mismatch` の行単位 ignore（`agentics-maintenance.yml`, `monthly-oss-repo-status.lock.yml`） |
| **zizmor** (インライン) | ワークフロー内コメント | `dangerous-triggers`（`auto-dump-context.yaml`, `dump-context.yaml`）、`overprovisioned-secrets`（`matrix-secret.yaml`）、`secrets-inherit`（`reusable-workflow-caller-nest.yaml`） |
| **ghalint** | `.ghalint.yaml` | `deny_inherit_secrets`（nest 系 2 ファイルの `call-workflow-passing-data` job）、`action_ref_should_be_full_length_commit_sha`（ローカル reusable workflow 参照） |
| **actionlint** | （リポジトリ内に専用 config なし） | `-color -oneline` で実行。ghalint ステップはコメントアウト済み |

旧 CI: `.github/workflows/actionlint.yaml`（aqua で actionlint、Docker で zizmor v1.22.0）

### 2. seiton 初期セットアップ

```bash
seiton version          # v0.9.25
seiton init             # .github/seiton.yaml 生成
seiton install --ci --target cursor --force  # CI テンプレート取得
seiton validate-config  # 設定検証
```

### 3. 設定移植（`.github/seiton.yaml`）

旧設定との対応:

| 旧設定 | seiton 設定 |
|--------|------------|
| zizmor `impostor-commit` 有効 | `rules.impostor-commit.enabled: true` |
| ghalint ローカル workflow SHA 免除 | `rules.unpinned-uses.ignore-actions` に `guitarrapc/githubactions-lab/.github/workflows/*` |
| gh-aw 生成物（`*.lock.yml`） | `discovery.skip-agentic-workflows: true` |
| ghalint `agentics-maintenance.yml`（コメントアウトされていた timeout 除外の意図） | `exclusions` でファイル全体スキップ（`rules` キーなし） |
| ghalint `deny_inherit_secrets` | `exclusions` で `reusable-workflow-caller-nest.yaml` の `deny-inherit-secrets` を抑制 |
| zizmor `dangerous-triggers` ignore | `auto-dump-context.yaml`, `dump-context.yaml` で `dangerous-triggers` 除外 |
| zizmor `overprovisioned-secrets` ignore | `matrix-secret.yaml` で `overprovisioned-secrets` 除外 |

### 4. CI 差し替え

- **削除**: `.github/workflows/actionlint.yaml`
- **追加/更新**: `.github/workflows/seiton.yml`
  - 旧 workflow と同じトリガー（`workflow_dispatch`, `pull_request`, `schedule: 0 0 * * *`）
  - Docker: `ghcr.io/guitarrapc/seiton:v0.9.25`（`:latest` ではなくタグ固定）
  - `--include-actions` で composite action も対象
  - `GH_TOKEN` を渡して online ルール（`impostor-commit`）向け
- **aqua.yaml**: `actionlint` / `ghalint` パッケージを削除（seiton は Docker 実行のため）

### 5. 検出結果の比較

| フェーズ | 対象ファイル | Errors | Warnings | 備考 |
|---------|-------------|--------|----------|------|
| デフォルト設定 | 120 | 46 | 35 | config なし |
| 移植後設定 | 119（1 excluded） | 31 | 15 | 3 suppressed（dangerous-triggers×2, deny-inherit-secrets×1） |
| `--include-actions` | 127 | 31 | 16 | composite action 8 件追加 |

`verbose` 出力の要点:

```
verbose: config: .github/seiton.yaml
verbose: discovery: 120 file(s) resolved
verbose: discovery: skipped monthly-oss-repo-status.lock.yml (agentic workflow)
verbose: suppressed: 3 diagnostic(s) (dangerous-triggers: 2, deny-inherit-secrets: 1)
verbose: total: 119 file(s) checked in ~10 ms
```

旧リンターとの差分（想定）:

- **actionlint**: ローカルでは GH API 待ちで長時間ハングし、タイムアウト前に結果取得できず。リポジトリは syntax デモが多く、actionlint は比較的静かだったと推定。
- **zizmor** (medium): インライン ignore + `.zizmor.yaml` の行 ignore で CI は通過していた。
- **ghalint**: CI ではコメントアウト。有効化すると `deny_inherit_secrets` 等を検出。
- **seiton**: 上記除外を反映後も **31 errors** が残る。主因は旧リンターがカバーしなかったルール（特に `run-env-context-direct-use`）と、意図的なデモ workflow（`secrets-access.yaml` 等）。

残存 error の多いファイル（意図的デモを含む）:

| ファイル | Errors | 主なルール |
|----------|--------|-----------|
| `_reusable-dump-context.yaml` | 8 | `run-env-context-direct-use` |
| `default-shell.yaml` | 4 | `run-env-context-direct-use` |
| `setenv-script.yaml` | 4 | `run-env-context-direct-use` |
| `auto-dump-context.yaml` | 1 | `deny-inherit-secrets`（旧設定では未除外） |

### 最終的なseiton.yaml

```yaml
# Seiton linter configuration for githubactions-lab.
# Migrated from actionlint / ghalint / zizmor exclusion settings.
# https://github.com/guitarrapc/seiton/blob/main/docs/configuration.md

rules:
  # zizmor: impostor-commit was enabled with line-level ignores in .zizmor.yaml
  impostor-commit:
    enabled: true

  # ghalint: action_ref_should_be_full_length_commit_sha for local reusable workflows
  unpinned-uses:
    ignore-actions:
      - owner: "guitarrapc/githubactions-lab/.github/workflows/*"

discovery:
  # Skip gh-aw generated workflows (agentics-maintenance.yml, *.lock.yml)
  skip-agentic-workflows: true

exclusions:
  # gh-aw generated workflow (DO NOT EDIT) — ghalint had commented job_timeout exclusion
  - file: ".github/workflows/agentics-maintenance.yml"

  # ghalint: deny_inherit_secrets — intentional secrets: inherit demo
  # Note: job-scoped exclusion currently emits parse errors on unrelated files; use file scope.
  - file: ".github/workflows/reusable-workflow-caller-nest.yaml"
    rules:
      - deny-inherit-secrets

  # zizmor: ignore[dangerous-triggers] on pull_request_target demos
  - file: ".github/workflows/auto-dump-context.yaml"
    rules:
      - dangerous-triggers
  - file: ".github/workflows/dump-context.yaml"
    rules:
      - dangerous-triggers

  # zizmor: ignore[overprovisioned-secrets] — matrix secret dereference demo
  - file: ".github/workflows/matrix-secret.yaml"
    rules:
      - overprovisioned-secrets

fix:
  defaults:
    job-timeout-minutes: 15

network:
  on-error: skip
```

---

## 使い勝手の評価

### 良い点（素直・把握しやすい）

1. **診断フォーマットが明快**
   `error[rule-id]: message` + ソース行 + キャレット + `= help:` の構成は、rustc/eslint に近く一度見れば修正方針が分かる。

2. **サマリーが実用的**
   末尾の `N errors, M warnings in X files (Y excluded, Z suppressed)` で、除外・抑制が効いているか一目で分かる。

3. **ファイル別集計テーブル**
   問題の多い workflow を優先して直せる。ラボリポジトリのようにファイル数が多い場合に特に有用。

4. **`--verbose` が設定デバッグに有効**
   読み込んだ config パス、discovery 件数、skip 理由、有効/無効ルール一覧、抑制件数が stderr に出る。設定移植のトラブルシュートに使えた。

5. **`seiton init` / `validate-config` / `install --ci`**
   ゼロから CI 導入する導線が揃っている。`install --ci` のテンプレートは Docker 実行・`--include-actions`・job summary まで含み、そのままカスタマイズしやすい。

6. **`--oneline` は CI ログ向き**
   `file:line:col: severity [rule-id] message` 形式で grep / GitHub annotation 連携しやすい。

7. **高速**
   120 workflow を ~10–50 ms でスキャン。旧構成（actionlint + zizmor Docker）はオーダーが違う。

8. **除外の集約**
   zizmor のインライン `# zizmor: ignore[...]`、ghalint の YAML、zizmor の行 ignore を **1 つの `.github/seiton.yaml`** に寄せられる。ワークフロー本体からコメントを減らせる余地がある。

### 課題・つまずき（改善提案）

#### 重大: `jobs` スコープ付き exclusion の挙動

ghalint 互換で以下を設定したところ:

```yaml
exclusions:
  - file: ".github/workflows/_reusable-workflow-nest.yaml"
    jobs: [call-workflow-passing-data]
    rules: [deny-inherit-secrets]
```

**全 119 ファイル**に対して `error[parse]: unknown job-id 'call-workflow-passing-data'` が発生し、診断が 271 errors に膨張した。

- **期待**: 指定 `file` にだけ job 存在チェック
- **実際**: 全 workflow で job-id を検証しているように見える
- **回避**: ファイルスコープのみの除外に変更（今回 `reusable-workflow-caller-nest.yaml` のみ）

#### 中: `rules: ["*"]` が未サポート

ドキュメント / skill では `rules: ["*"]` で全ルール抑制とあるが、v0.9.25 では `unknown rule-id '*'` で **exit code 3**（設定パース失敗）。

- **回避**: `rules` キーを省略したファイル単位 exclusion（`agentics-maintenance.yml` のみ指定）で全体スキップできた
- **提案**: `rules: ["*"]` を実装するか、ドキュメントを `rules` 省略形式に統一

#### 中: `skip-agentic-workflows` の検出範囲

`monthly-oss-repo-status.lock.yml`（先頭に `# gh-aw-metadata:`）は skip されたが、
`agentics-maintenance.yml`（`DO NOT EDIT` だが metadata 行なし）は skip されなかった。

- gh-aw 生成物のうち metadata がないファイルは別途 `exclusions` が必要
- `seiton init` コメントの「agentics-maintenance を列挙」と `skip-agentic-workflows` の関係をもう少し明確にするとよい

#### 軽: ルール別集計テーブルの表示

診断件数が多いと、ファイル別テーブルは出るが **ルール別 Count テーブルが出ない** ことがある（デフォルト設定の初回実行では両方出ていた）。`--verbose` または `--oneline` 時の表示条件を揃えると、全体像の把握がさらに楽。

#### 軽: 旧リンターとのインライン抑制の対応

| 旧方式 | seiton |
|--------|--------|
| `# zizmor: ignore[rule]` | `.github/seiton.yaml` の `exclusions` に集約（推奨） |
| ghalint `excludes` | `exclusions` + `rules` オプションで代替可能 |

インライン抑制を seiton ネイティブでサポートするか、移行ツールがあると大規模リポジトリでは助かる。

#### 情報: 検出範囲の違い

seiton は actionlint + zizmor + ghalint の **和集合に近い** が、完全一致ではない。

- 新規に検出: `run-env-context-direct-use`（ラボのデモ多数）、`auto-dump-context.yaml` の `deny-inherit-secrets`
- zizmor medium 相当: `if-expr-wrapper` 等の warning はデフォルトで有効
- online ルール: `impostor-commit` は opt-in（今回有効化）。`ref-version-mismatch` はローカルでも有効（zizmor の行 ignore はファイル除外で代替）

---

## ログから状況を把握しやすいか（総合）

| 観点 | 評価 | コメント |
|------|------|----------|
| 初回実行（config なし） | ◎ | hint で `seiton init` と `--include-actions` を案内。件数サマリーとテーブルで全体像が掴める |
| 設定あり実行 | ◎ | `suppressed` / `excluded` 件数が明示され、除外が効いているか検証しやすい |
| 設定ミス時 | △ | `jobs` スコープ誤設定時に大量の parse error が出て原因特定に時間がかかった。`validate-config` では検出されなかった |
| CI 想定（`github-actions` format） | ○（未実機確認） | テンプレートは job summary 対応。ローカル `text` 形式の品質は高い |
| 修正導線 | ◎ | `= help:` に config 例が載る。`--fix --dry-run` で自動修正プレビュー可能 |

**総合**: 日常利用のログ品質は高く、**設定まわり（exclusion のスコープ検証、`rules: ["*"]`）にだけ改善余地**がある。ラボリポジトリのように「意図的に悪い例」を多く含む場合は、旧リンターより多くの error が出ることを前提に CI 方針（error only / 段階的 fix / デモファイル除外）を決める必要がある。

---

## 次のアクション案

1. **CI を赤のままにするか決める**
   - デモ workflow を直す / 除外する / `--min-severity error` + 段階的ルール有効化
2. **`auto-dump-context.yaml` の `deny-inherit-secrets`**
   - デモとして残すなら exclusion 追加、直すなら secrets を明示マッピング
3. **旧設定ファイルの整理**
   - `.zizmor.yaml`, `.ghalint.yaml` は seiton に移植済みのため削除候補
4. **インライン `# zizmor: ignore[...]` コメント**
   - 設定に移した箇所からは削除可能（可読性向上）
5. **seiton 側へのフィードバック**
   - job-scoped exclusion の file 限定検証
   - `rules: ["*"]` サポートまたはドキュメント修正
   - `validate-config` で unknown job-id を事前検出

---

## 変更ファイル一覧

| ファイル | 変更内容 |
|----------|----------|
| `.github/seiton.yaml` | 新規（旧 3 リンター除外の移植） |
| `.github/workflows/seiton.yml` | 新規（旧 actionlint.yaml 置換） |
| `.github/workflows/actionlint.yaml` | 削除 |
| `aqua.yaml` | actionlint / ghalint 削除 |
| `feedback_seiton.md` | 本ドキュメント |

未変更（参照用に残置）: `.zizmor.yaml`, `.ghalint.yaml`
