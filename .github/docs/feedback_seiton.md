# seiton フィードバック（githubactions-lab）

実施日: 2026-06-07
環境: Windows 11, seiton **v0.9.26** (.NET 10.0.8, win-x64)

## 実行経過

1. 初回実行（デフォルト設定）
   - コマンド: `seiton --oneline`
   - 結果: **46 errors / 35 warnings in 120 files**
   - 所感:
     - ルール検出自体は有効だが、教材/生成物/意図的な悪例ファイルが混在しておりノイズが多い。
     - `hint: run 'seiton init'` と `--include-actions` の誘導は分かりやすい。

2. 設定初期化
   - コマンド: `seiton init`
   - 生成: `.github/seiton.yaml`

3. 設定調整（1回目）
   - 追加した内容:
     - `fix.defaults.job-timeout-minutes: 15`
     - `fix.pinning.enable-network: true`
     - `fix.images.enable-network: true`
     - `rules.runner-no-latest.fix-mapping`（ubuntu/windows/macos の latest マッピング）
     - `discovery.skip-agentic-workflows: true`
     - agentic/lock/demo ワークフローの除外
   - 失敗ケース:
     - `rules: ["*"]` を exclusion に指定すると `unknown rule-id '*'` で config parse エラー。
   - 対応:
     - 当該ファイルは file-only exclusion に変更（`rules` 指定なし）。

4. 再実行（調整後）
   - コマンド: `seiton validate-config && seiton --include-actions --oneline`
   - 結果: **32 errors / 17 warnings in 127 files (2 excluded)**
   - 所感:
     - ノイズが減り、主要な問題クラスが見えやすくなった。

5. 自動修正の評価（dry-run）
   - 成功例:
     - `seiton --fix --dry-run .github/workflows/cache.yaml`
     - `if` 式の `${{ }}` ラップを正しく提案。差分が短く読みやすい。
   - 失敗例:
     - `seiton --fix --dry-run --enable-pin-network .github/workflows/prevent-file-change.yaml`
     - `overlapping or conflicting edits detected` で終了（fix生成競合）。
   - 競合の具体メッセージ（実測）
     - ```
       error: fix failed for D:\github\guitarrapc\githubactions-lab\.github\workflows\prevent-file-change.yaml: overlapping or conflicting edits detected at offset 271 (previous edit at offset 271 with length 24, current edit at offset 271 with length 24; total 2 edits in batch)
       hint: this may indicate conflicting lint rules or a bug in fix generation. Please report this issue.
       ```
   - 対応:
     - 同ファイルの `unpinned-uses` を exclusions で抑制し、全体dry-run評価を継続可能にした。

### fix競合の再現情報（具体例）

- 対象 YAML: `.github/workflows/prevent-file-change.yaml`
- 競合が起きた箇所（抜粋）
  - 1つ目の `uses`: `actions/github-script@v9`
  - 2つ目の `uses`: `actions/github-script@v9`
  - 同一ファイル内で同じ未pin action 参照が複数ある構成

```yaml
jobs:
  dependabot:
    steps:
      - uses: actions/github-script@v9
        id: check
        with:
          script: |
            // ...省略...

  external:
    steps:
      - uses: actions/github-script@v9
        id: check
        with:
          script: |
            // ...省略...
```

- 再現コマンド
  - `seiton --fix --dry-run --enable-pin-network .github/workflows/prevent-file-change.yaml`

- 期待する挙動
  - 2箇所の `actions/github-script@v9` をそれぞれ full SHA pin に置換する diff が表示されること。

- 実際の挙動
  - 同一 offset（271）への編集が2件として扱われ、編集バッチ競合で失敗する。

- 競合が起きる条件の仮説（再現観点）
  - 同一ファイル内で同一 `uses` 文字列が複数回登場する。
  - `unpinned-uses` の自動修正が複数箇所へ同時適用される。
  - edit 位置計算が同一開始位置を返し、重複 edit と判定される。

6. 再調整後の全体 dry-run
   - コマンド: `seiton --fix --dry-run --include-actions --oneline`
   - 結果:
     - **Would fix 53 of 64 issues in 23 files (11 remaining)**
     - 最終残件: **3 errors / 8 warnings in 7 files**
   - 残った主な項目:
     - `deny-inherit-secrets`
     - `run-secrets-context-direct-use`
     - `unredacted-secrets`
     - 意図的 bad 例の `if-cond` / `env-var`

## 検出の妥当性ハンドリング

- 妥当（適切）と判断
  - `run-env-context-direct-use`, `run-inputs-context-direct-use`, `run-secrets-context-direct-use`
  - `deny-inherit-secrets`
  - `unpinned-image`, `unpinned-uses`
  - `if-expr-wrapper`
- 意図的な教材/生成物として除外したほうが良いもの
  - agentic workflow、`.lock.yml`、攻撃デモ系 workflow
  - 学習用 bad ケース（意図的にアンチパターンを残している例）

## 使い勝手評価

- 良い点
  - `init` → `validate-config` → `fix --dry-run` の流れが素直。
  - diagnostics の `rule-id` と `help` が具体的で、修正方針を取りやすい。
  - dry-run diff がそのままレビュー可能で実運用向き。
  - `Would fix / Remaining` の集計が非常に見やすい。
- 改善してほしい点
  - `rules: ["*"]` exclusion が使えないバージョンでは、エラーメッセージに代替記法（file-only exclusion）を直接案内してほしい。
  - fix 競合（overlapping/conflicting edits）発生時、競合ルール名や該当 edit の詳細が出ると原因追跡が容易。
  - 一部の large diff では、ruleごとのサマリ（どのルールで何件修正予定か）があると把握しやすい。

## ログ可観測性評価

- 総評: **高い**
  - `file:line:col`, `rule-id`, `help`, 集計テーブル、最終サマリが一貫している。
  - 失敗時も hint があり、行動につなげやすい。
- 課題
  - fix競合時の内部理由が不足（再現は容易だが切り分け情報が弱い）。

## 今回の最終コンフィグ方針（要点）

- pinning/image の network 解決を有効化
- default `job-timeout-minutes` を設定
- `*-latest` runner の fix mapping を設定
- generated/demo 系は exclusions でノイズ制御
- unstable なケース（今回の fix競合ファイル）は rule単位で一時抑制

```yaml
# Seiton linter configuration. see https://github.com/guitarrapc/seiton/blob/main/docs/configuration.md for details.
# Preferred location: .github/seiton.yaml

rules:
  # Add dangerous trigger events (appended to built-in set).
  # dangerous-triggers:
  #   severity: warning
  #   events:
  #     - issue_comment

  # Add known GitHub-hosted runner labels (appended to built-in set).
  # runner-label:
  #   known-hosted-labels:
  #     - ubuntu-24.04-large

  # Map moving labels to pinned replacements for detection/fix.
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: "ubuntu-24.04"
      windows-latest: "windows-2025"
      macos-latest: "macos-15"

  # Add public registries treated as credential-optional.
  # credentials:
  #   public-registries:
  #     - ghcr.io

  # Add untrusted triggers for cache poisoning checks.
  # cache-poisoning-trigger:
  #   untrusted-triggers:
  #     - issue_comment

  # Add untrusted triggers for self-hosted runner checks.
  # self-hosted-runner-trigger:
  #   untrusted-triggers:
  #     - issue_comment

  # Add output commands watched as secret sinks.
  # unredacted-secrets:
  #   output-commands:
  #     - tee

  # Define allow/deny patterns for uses references.
  # forbidden-uses:
  #   allow:
  #     - actions/*
  #   deny:
  #     - some-untrusted-org/*

  # Ignore selected actions from SHA pin checks.
  # unpinned-uses:
  #   ignore-actions:
  #     - owner: "my-org/*"
  #     - owner: "my-org/internal-action"
  #     - owner: "my-org/setup-*"
  #       refs: [main, master]

  # Tune secret count thresholds.
  # overprovisioned-secrets:
  #   max-step-env-secrets: 5
  #   max-job-secrets: 5

  # Assume additional events for expression validation.
  # expr-undefined-var:
  #   assume-events:
  #     - workflow_dispatch

  # Online rules (default: disabled). Enable individually:
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true

exclusions:
  # Glob + jobs scope: exclude rules for specific jobs only
  # - file: .github/workflows/legacy-*.yml
  #   rules:
  #     - runner-no-latest
  #   jobs:
  #     - legacy
  # One file, multiple rules (entire file, no jobs scope):
  # - file: .github/workflows/demo.yml
  #   rules:
  #     - run-env-context-direct-use
  #     - unpinned-image
  # File-only exclusion (skips lint for the entire file):
  # - file: .github/workflows/generated.yml
  # Agentic Workflow files (# gh-aw-metadata: header or *.lock.yml):
  - file: .github/workflows/agentics-maintenance.yml
  - file: .github/workflows/*.lock.yml
  - file: .github/workflows/injection-attack-via-context.yaml
  - file: .github/workflows/prevent-file-change.yaml
    rules:
      - unpinned-uses
  - file: .github/workflows/auto-dump-context.yaml
    rules:
      - dangerous-triggers
  - file: .github/workflows/dump-context.yaml
    rules:
      - dangerous-triggers

discovery:
  skip-agentic-workflows: true

fix:
  defaults:
    job-timeout-minutes: 15
  pinning:
    enable-network: true
    min-age-days: 14
    # exclude-branches:
    #   - main
    #   - master
    # ignore-actions:
    #   - uses: "slsa-framework/*"
    #     ref: "*"
  images:
    enable-network: true
    # exclude-images:
    #   - scratch
    # exclude-tags:
    #   - latest
    # ignore-images:
    #   - mcr.microsoft.com/**

network:
  # on-error: skip
  # timeout-seconds: 30
  # max-concurrency: (omit; default is min(4, logical CPUs))
  # github:
  #   ghes-api-url: ""
  #   ghes-fallback: false

output:
  # sort-order: location    # location (default) | rule
```

## まとめ

このリポジトリのように「良い例/悪い例/生成物」が混在する環境でも、
`seiton` は **設定調整を前提にすれば実用的で、ログも十分読みやすい**。
特に `--fix --dry-run` は改善インパクトを可視化しやすく、導入判断に有効。
一方で、fix競合時の診断情報と exclusion 記法エラー時の誘導は、今後の改善余地がある。
