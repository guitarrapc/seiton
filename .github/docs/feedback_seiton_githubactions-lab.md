# seiton フィードバック

## 概要

- **バージョン**: seiton 0.9.15 (built with .NET 10.0.8, win-x64)
- **対象リポジトリ**: guitarrapc/githubactions-lab (GitHub Actions のラボ/デモリポジトリ)
- **ワークフロー数**: 123 ファイル

## 実行経過

### 1. 初回実行 (設定なし)

```
48 errors, 39 warnings in 123 files
```

主な検出カテゴリ:
- `run-env-context-direct-use`: 多数 (env コンテキストの直接参照)
- `job-timeout-minutes-required`: 多数 (Agentic Workflow 起因)
- `unpinned-image`: コンテナイメージの digest 未固定
- `if-expr-wrapper`: `${{ }}` ラッパー欠落
- `bot-conditions`: github.actor チェック
- `dangerous-triggers`: pull_request_target 利用

### 2. Agentic Workflow 除外後

```yaml
exclusions:
  - file: .github/workflows/monthly-oss-repo-status.lock.yml
  - file: .github/workflows/agentics-maintenance.yml
```

```
34 errors, 20 warnings in 123 files
```

Agentic Workflow (自動生成/ロックファイル) を除外し、大幅にノイズが減少。

### 3. `--fix --enable-pin-network --enable-image-network` 適用後

自動修正で以下が解決:
- `run-env-context-direct-use` の大部分 → `${VAR}` (bash) に変換
- `if-expr-wrapper` → `${{ }}` ラッパー追加
- `unpinned-image` → `@sha256:...` digest 付与 (ネットワーク解決)

```
6 errors, 15 warnings in 123 files
```

### 4. 意図的パターンの除外設定追加後

ラボリポジトリ固有の意図的パターン (デモ用シークレット表示、意図的なbad example等) を除外:

```
1 error, 7 warnings in 123 files
```

**最終残存**:
- 1 error: `parse` エラー (除外ファイルからリーク — seiton のバグ)
- 4 warnings: `bot-conditions` (github.actor パターン、受容済みリスク)
- 3 warnings: `dangerous-triggers` (pull_request_target、zizmor コメントで認知済み)

---

## 使い勝手の評価

### 良い点

| 項目 | 評価 |
|------|------|
| CLI UX | シンプルで直感的。引数なしで `.github/workflows/` を自動探索する |
| ルール数 | 61ルール (56有効) は充実。セキュリティ・ベストプラクティス・スタイルを包括的にカバー |
| エラーメッセージ | ファイル名、行番号、該当コード、修正ヒントが明瞭。一目で問題箇所と対処がわかる |
| `--fix --dry-run` | unified diff で修正内容を事前確認できる。安全 |
| `--enable-pin-network` / `--enable-image-network` | ネットワーク経由で SHA/digest を自動解決し正確にピン留めできる |
| config 構造 | `exclusions` で file + rules の組合せ除外が柔軟 |
| `validate-config` | config の構文エラーを即座に検出可能 |
| `--oneline` | CI パイプラインでの利用に適する |
| 実行速度 | 123ファイルを1-2秒で解析完了。十分高速 |

### 改善すべき点 (バグ/問題)

#### 1. [Bug] `parse` エラーがファイル除外を貫通する

```yaml
exclusions:
  - file: .github/workflows/monthly-oss-repo-status.lock.yml
```

上記設定にもかかわらず、同ファイルの `parse` エラーが出力される:

```
error[parse]: jobs.'conclusion'.concurrency has unexpected key "queue" ...
  --> monthly-oss-repo-status.lock.yml:932:7
```

**期待動作**: ファイル全体除外時は parse エラーも抑制されるべき。

#### 2. [Bug] `run-env-context-direct-use` の auto-fix が PowerShell シェルコンテキストを考慮しない

`defaults.run.shell: pwsh` または明示的 `shell: pwsh` のステップで:

```yaml
# Before (original)
run: echo "BRANCH=${{ env.BRANCH_NAME }}" | Tee-Object ...

# After (auto-fix applied - INCORRECT)
run: echo "BRANCH=${BRANCH_NAME}" | Tee-Object ...

# Expected correct fix
run: echo "BRANCH=$env:BRANCH_NAME" | Tee-Object ...
```

PowerShell では `${BRANCH_NAME}` は **PowerShell 変数** を参照し、**環境変数** (`$env:BRANCH_NAME`) ではない。auto-fix が常に bash スタイル `${VAR}` を出力するのはシェルコンテキスト非対応。

**影響**: pwsh ステップで auto-fix を適用すると **動作しないワークフロー** が生成される。

#### 3. [False Positive] `run-env-context-direct-use` がヒアドキュメント内を検出する

```yaml
run: |
  cat << 'EOF' > pr_comment.md
    Workflow [${{ env.GITHUB_ACTIONS_RUN_URL }}) found ...
  EOF
```

シングルクォート `'EOF'` ヒアドキュメント内ではシェル変数は展開されないため、`${{ env.* }}` が唯一の展開手段。検出は不適切。

#### 4. [False Positive] `run-env-context-direct-use` が式フォールバックを検出する

```yaml
run: echo "value=${{ env.TAG_VALUE || (github.event_name == 'pull_request' && '0.1.0-test' || env.GITHUB_REF_NAME) }}"
```

あるいは

```yaml
run: echo "value=${{ inputs.tag || (github.event_name == 'pull_request' && '0.1.0-test' || github.ref_name) }}"
```

`||` 演算子によるフォールバックロジックはシェル変数では代替不可。純粋な env 参照ではなく、GitHub Actions 式としての利用。
このケースはenvに式ごと持って行ってから参照する形に修正するのが望ましい。あるいは修正できない。

#### 5. [UX] `bot-conditions` が zizmor 互換の無視コメントを認識しない

`dangerous-triggers` と同様に `# zizmor: ignore[bot-conditions]` コメントがあっても seiton は無視しない。seiton 独自の抑制手段 (config exclusion のみ) しかない。インラインでの抑制がサポートされると CI 連携時に便利。

#### 6. [UX] `--fix` 結果のサマリーが不明瞭

`--fix` 実行後、修正されたファイルのリストや修正数が表示されない。`--dry-run` では diff が出るが、実適用時は「何件修正したか」が不明。

##### 6. インデントを崩すケースがある

`${{ env.FOO }}` を env変数に修正するfixで、元のコードのインデントが崩れるケースがあった。100%じゃないが特定のファイルで発生しているように見えるので、再現条件を含めてredテストが必要。

before (2スペースインデント)

```
name: set env with script
on:
  workflow_dispatch:
  push:
    branches: ["main"]
  pull_request:
    branches: ["main"]

env:
  BRANCH_NAME: ${{ startsWith(github.event_name, 'pull_request') && github.head_ref || github.ref_name }}

jobs:
  bash:
    strategy:
      matrix:
        runs-on: [ubuntu-24.04, windows-2025]
    permissions:
      contents: read
    runs-on: ${{ matrix.runs-on }}
    timeout-minutes: 3
    defaults:
      run:
        shell: bash
    steps:
      - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
        with:
          persist-credentials: false
      - name: Add ENV and OUTPUT by Script
        id: script
        run: bash ./.github/scripts/setenv.sh --ref "${{ env.BRANCH_NAME }}"
      - name: Show Script  ENV and OUTPUT
        run: |
          echo ${{ env.BRANCH_SCRIPT }}
          echo ${{ steps.script.outputs.branch }}

  pwsh:
    strategy:
      matrix:
        runs-on: [ubuntu-24.04, windows-2025]
    permissions:
      contents: read
    runs-on: ${{ matrix.runs-on }}
    timeout-minutes: 3
    defaults:
      run:
        shell: pwsh
    steps:
      - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
        with:
          persist-credentials: false
      - name: Add ENV and OUTPUT by Script
        id: script
        run: ./.github/scripts/setenv.ps1 -Ref "${{ env.BRANCH_NAME }}"
      - name: Show Script ENV and OUTPUT
        run: |
          echo "${{ env.BRANCH_SCRIPT }}"
          echo "${{ steps.script.outputs.branch }}"
```

after (4スペースインデンスに変わっている)

```
name: set env with script
on:
    workflow_dispatch:
    push:
        branches: ["main"]
    pull_request:
        branches: ["main"]

env:
    BRANCH_NAME: ${{ startsWith(github.event_name, 'pull_request') && github.head_ref || github.ref_name }}

jobs:
    bash:
        strategy:
            matrix:
                runs-on: [ubuntu-24.04, windows-2025]
        permissions:
            contents: read
        runs-on: ${{ matrix.runs-on }}
        timeout-minutes: 3
        defaults:
            run:
                shell: bash
        steps:
            - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
              with:
                  persist-credentials: false
            - name: Add ENV and OUTPUT by Script
              id: script
              run: bash ./.github/scripts/setenv.sh --ref "${BRANCH_NAME}"
            - name: Show Script  ENV and OUTPUT
              run: |
                  echo ${BRANCH_SCRIPT}
                  echo ${{ steps.script.outputs.branch }}

    pwsh:
        strategy:
            matrix:
                runs-on: [ubuntu-24.04, windows-2025]
        permissions:
            contents: read
        runs-on: ${{ matrix.runs-on }}
        timeout-minutes: 3
        defaults:
            run:
                shell: pwsh
        steps:
            - uses: actions/checkout@8e8c483db84b4bee98b60c0593521ed34d9990e8 # v6.0.1
              with:
                  persist-credentials: false
            - name: Add ENV and OUTPUT by Script
              id: script
              run: ./.github/scripts/setenv.ps1 -Ref "$env:BRANCH_NAME"
            - name: Show Script ENV and OUTPUT
              run: |
                  echo "$env:BRANCH_SCRIPT"
                  echo "${{ steps.script.outputs.branch }}"

```

---

## 検出の適切性まとめ

| ルール | 適切性 | 備考 |
|--------|--------|------|
| `run-env-context-direct-use` | ⚠️ 条件付き | bash では適切。pwsh / heredoc / 式フォールバック で偽陽性 |
| `unpinned-image` | ✅ 適切 | digest ピン留め推奨は正当 |
| `if-expr-wrapper` | ✅ 適切 | `${{ }}` ラッパーの一貫性 |
| `job-timeout-minutes-required` | ✅ 適切 | セキュリティ/コスト観点で妥当 |
| `bot-conditions` | ✅ 適切 | spoofable context の指摘は正当 |
| `dangerous-triggers` | ✅ 適切 | pull_request_target の注意喚起 |
| `unredacted-secrets` | ✅ 適切 | シークレット露出リスクの指摘 |
| `deny-inherit-secrets` | ✅ 適切 | 明示的シークレットマッピング推奨 |
| `env-var` | ✅ 適切 | ポータビリティの指摘 |
| `if-cond` | ✅ 適切 | 定数条件の検出 |
| `run-secrets-context-direct-use` | ✅ 適切 | secrets の env マッピング推奨 |

---

## 自動修正の評価

| 項目 | 評価 |
|------|------|
| bash での `run-env-context-direct-use` 修正 | ✅ 正確。`${{ env.X }}` → `${X}` |
| pwsh での `run-env-context-direct-use` 修正 | ❌ **不正確**。`${X}` ではなく `$env:X` であるべき |
| `if-expr-wrapper` 修正 | ✅ 正確 |
| `unpinned-image` 修正 (ネットワーク有効) | ✅ 正確。正しい digest を付与 |
| `create-release.yaml` の式フォールバック | ⚠️ 修正スキップ (正しい判断だが検出は残る) |
| heredoc 内 `${{ env.* }}` | ⚠️ 修正スキップ (正しい判断だが検出は残る) |

---

## ログからの状況把握しやすさ

| 項目 | 評価 |
|------|------|
| エラー/警告の区別 | ✅ `error[rule]` / `warning[rule]` で明確 |
| ファイル・行番号 | ✅ `-->` で正確に示される |
| 該当コードの表示 | ✅ コードスニペットとキャレットで位置特定が容易 |
| 集計表示 | ✅ `N errors, M warnings in X files` で全体把握可能 |
| diff 表示 (`--dry-run`) | ✅ unified diff で変更前後が明確 |
| 修正適用時の情報 | ❌ 修正されたファイル一覧・件数が表示されない |
| 除外ファイルの通知 | ❌ 除外されたファイル数や除外理由が表示されない (`--verbose` でも不明) |

---

## 推奨改善事項 (優先度順)

1. **[Critical]** PowerShell シェルコンテキストでの auto-fix 修正 (`$env:VAR` を使う)
2. **[High]** ファイル除外が `parse` エラーに適用されない問題の修正
3. **[High]** `run-env-context-direct-use` でヒアドキュメント (`<< 'EOF'`) 内の検出を除外
4. **[Medium]** `run-env-context-direct-use` で `||` 等の式フォールバックを含む場合はスキップ
5. **[Medium]** `--fix` 適用時に修正サマリー (修正ファイル数/修正箇所数) を表示
6. **[Low]** インラインコメントによるルール抑制のサポート
7. **[Low]** `--verbose` で除外されたファイル・ルール情報を表示

---

## 設定ファイル最終状態

`.github/seiton.yaml` の `exclusions` セクションは次の通り。

```yaml
exclusions:
  # Agentic Workflows - generated/locked files, not manually maintained
  - file: .github/workflows/monthly-oss-repo-status.lock.yml
  - file: .github/workflows/agentics-maintenance.yml

  # Intentional "bad example" workflows for testing/demonstration
  - file: .github/workflows/job-needs-skip-handling-bad.yaml
    rules:
      - if-cond

  # Demonstration workflows showing secret access patterns
  - file: .github/workflows/secrets-access.yaml
    rules:
      - run-secrets-context-direct-use

  # Demonstration workflow: secrets: inherit is intentional
  - file: .github/workflows/reusable-workflow-caller-nest.yaml
    rules:
      - deny-inherit-secrets

  # Intentional lowercase env naming for demonstration
  - file: .github/workflows/matrix-secret.yaml
    rules:
      - env-var
      - unredacted-secrets
  - file: .github/workflows/merge-branch.yaml
    rules:
      - env-var

  # False positive: ${{ env.* }} used inside 'EOF' heredoc (shell vars don't expand)
  - file: .github/workflows/crlf-checker.yaml
    rules:
      - run-env-context-direct-use
  - file: .github/workflows/dotnet-lint.yaml
    rules:
      - run-env-context-direct-use

  # False positive: expression fallback logic, not simple env reference
  - file: .github/workflows/create-release.yaml
    rules:
      - run-env-context-direct-use

  # Intentional: reusable-workflow-called demonstrates secret echo
  - file: .github/workflows/_reusable-workflow-called.yaml
    rules:
      - unredacted-secrets
```
