# seiton フィードバック (2026-06-03)

## 目的
- 本リポジトリで seiton を実行し、検出内容を「妥当/不適切」でハンドリングする。
- config 調整と再実行を反復し、運用しやすい状態まで寄せる。
- 自動修正の使い勝手、ログの把握しやすさを評価する。

## 実行コマンドと経過
1. 初回診断 (config なし)
   - コマンド: `seiton --include-actions --verbose`
   - 結果: 35 errors, 33 warnings
   - 主な内訳: `run-inputs-context-direct-use` / `run-env-context-direct-use` / `unpinned-uses` / `expr-undefined-var`

2. config 初期化
   - コマンド: `seiton init`
   - 生成: `.github/seiton.yaml`

3. config 1回目調整
   - `fix.defaults.job-timeout-minutes: 20`
   - `fix.pinning.enable-network: true`
   - `fix.images.enable-network: true`
   - `rules.runner-no-latest.fix-mapping` を設定
   - `_test-*.yaml` に対して一部ルールを除外

4. 再診断
   - コマンド: `seiton --include-actions --verbose`
   - 結果: 29 errors, 33 warnings, 6 suppressed
   - ノイズ抑制は確認できたが、`unpinned-uses` は依然多数。

5. 自動修正の dry-run
   - コマンド: `seiton --include-actions --fix --dry-run --verbose`
   - 結果: 68件中22件を修正可能、46件残存
   - 良かった点: `run-inputs-context-direct-use` / `run-env-context-direct-use` の一部を env 追加で具体的に修正提案
   - 残課題: `unpinned-uses` は「fixable」と表示されるが、このリポジトリでは差分に現れなかった

6. pinning関連の追加検証
   - コマンド: `seiton --include-actions --fix --dry-run --enable-pin-network --enable-image-network --verbose`
   - 結果: 上記と同等 (22件修正可能、`unpinned-uses` 変化なし)
   - 観察: config でも明示フラグでも結果が同じ。少なくとも本ケースでは pinning 自動修正は実質効かなかった。

7. config 2回目調整 (ノイズ整理)
   - `rules.unpinned-uses.ignore-actions` に `owner: "Cysharp/*"` を追加

8. 最終再診断
   - コマンド: `seiton --include-actions --verbose`
   - 結果: 29 errors, 1 warning, 32 infos, 6 suppressed
   - `unpinned-uses` は warning から info (ignored) へ移行し、実修正対象が見やすくなった

## 妥当な検出
- `run-inputs-context-direct-use`
  - shell 内で `${{ inputs.* }}` を直接使う箇所を検出。安全性・可読性の観点で妥当。
- `run-env-context-direct-use`
  - shell 内で `${{ env.* }}` を直接使う箇所を検出。Shell変数へ寄せる方針は妥当。
- `expr-undefined-var`
  - `create-release.yaml` の `inputs.nuget-path` 未定義検出は明確に有効。
- `checkout-persist-credentials`
  - `persist-credentials` に関する注意喚起は実害を防ぐ方向で妥当。

## 不適切/運用上ノイズになりやすい検出
- `unpinned-uses` (同一オーナー内の再利用 workflow/action 参照)
  - このリポジトリでは `Cysharp/*@main` を内部参照として多用しており、警告が大量発生して信号雑音比を悪化させる。
  - `ignore-actions` で抑制すると、残る重要エラーを追いやすくなる。
- `_test-*.yaml` の一部ルール
  - テスト目的で意図的に使っているパターンがあり、限定除外した方が運用しやすい。

## 自動修正の使い勝手評価
- 良い点
  - diff が具体的で、何をどう変えるかが分かりやすい。
  - `env` 追加 + 変数参照置換の提案は実践的で、そのまま取り込みやすい。
  - `--verbose` で設定ソース (config/CLI) と discovery 状況が確認できる。

- 改善希望
  - `unpinned-uses` が `fixable` と表示されるのに差分が出ないケースがある。
    - ユーザー視点では「なぜfixされないか」を判断しづらい。
    - "skip理由" (例: owner policy, branch policy, self-reference policy, resolution failure) を明示すると良い。
  - 集計テーブルで `action.yaml` が同名表示のみになり、どの action ディレクトリか判別しにくい。
    - 相対パス表示だと追跡しやすい。
  - info 出力 (`ignored ...`) は有用だが件数が多いと長くなる。
    - `--oneline` 相当の短縮や集約表示が欲しい。

## 最終判断
- seiton は「危険/不整合パターンの検出」と「修正可能な項目の提示」に有効。
- ただしこのリポジトリでは、`unpinned-uses` をそのまま運用するとノイズが高い。
- 実運用では以下を推奨:
  - 内部参照は `ignore-actions` で整理
  - テスト用途ファイルは `exclusions` で限定除外
  - `run-*direct-use` と `expr-undefined-var` を優先修正対象として継続監視

## 今回の変更ファイル
- `.github/seiton.yaml`
- `feedback_seiton.md`

## 追記: 最後まで修正を適用した結果

追加で以下を実施し、修正を最後まで適用した。

1. 自動修正の本適用
  - コマンド: `seiton --include-actions --fix --enable-pin-network --enable-image-network --verbose`
  - 結果: `Fixed 22 of 68 issues in 16 files (46 remaining)`

2. 残件の手修正
  - 対象ルール: `run-inputs-context-direct-use`, `expr-undefined-var`, `checkout-persist-credentials`
  - 主な対応:
    - run 内の `${{ inputs.* }}` を `env` 経由の shell 変数へ置換
    - `create-release.yaml` の `workflow_dispatch.inputs.nuget-path` 未定義を追加
    - `inputs.* && '--flag' || ''` 形式を bash の条件分岐へ置換
    - composite checkout action の `persist-credentials` を `false` 固定化

3. 最終再診断
  - コマンド: `seiton --include-actions --verbose`
  - 結果: `32 infos in 32 files (6 suppressed)`
  - エラー/警告: `0 errors, 0 warnings`
  - 残りは `unpinned-uses` の ignore 設定に基づく info のみ

## 最終評価 (更新)
- 要求どおり、修正適用まで完了できた。
- seiton の auto-fix は大量の定型修正に有効で、残件は手修正で収束可能だった。
- 現在のルール設定では、CIでの実運用観点で actionable な診断だけを追える状態になっている。
