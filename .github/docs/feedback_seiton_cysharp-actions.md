# Feedback: seiton on `Cysharp/Actions`

## 対象

- リポジトリ: `.references/actions` (`Cysharp/Actions`)
- 実行時点の commit: `1cd753a`
- seiton: `0.9.17`

## 実行経過

### 1. 初期確認

- `seiton --help` と `seiton version` は素直で、`--fix` / `--enable-pin-network` / `--enable-image-network` まで一通り把握しやすかった。
- ただし `.references/actions` へ移動しただけでは親リポジトリの `.github/seiton.yaml` を拾った。ネストされたリポジトリでは、移動だけでは不十分で、対象側に `.github/seiton.yaml` を置くか `-c` を明示する必要があった。
- 対象側で `seiton init --output .github/seiton.yaml` を実行し、以後は `-c .github/seiton.yaml` を明示して確認した。

### 2. ベースライン実行

- 実行コマンド: `seiton --include-actions -c .github/seiton.yaml --color never --verbose`
- 初回結果: **79 diagnostics (36 errors, 43 warnings) in 32 files**
- 目立った内訳:

| Rule | Count | 所感 |
|---|---:|---|
| `unpinned-uses` | 32 | `Cysharp/Actions/...@main` への自己参照が大半で、ノイズ寄り |
| `run-inputs-context-direct-use` | 21 | reusable workflow / composite action の `run:` 内での直接参照を多く検出 |
| `bot-conditions` | 10 | `_test-*` に集中 |
| `run-env-context-direct-use` | 9 | Bash 内 `${{ env.* }}` を直接使う箇所を検出 |
| `deny-inherit-secrets` | 4 | `_test-create-release.yaml` に集中 |
| `expr-undefined-var` | 2 | `create-release.yaml` の `inputs.nuget-path` 参照を検出 |

ベースラインでは `_test-*` と自己参照 `unpinned-uses` が支配的で、レビューしたい本命のエラーが埋もれやすかった。

### 3. 設定調整 1

`.references/actions/.github/seiton.yaml` に以下を追加した。

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "Cysharp/*"

exclusions:
  - file: .github/workflows/_test-*.yaml
```

再実行結果: **31 diagnostics (30 errors, 1 warning)**

- `unpinned-uses` が消え、残件が `run-inputs-context-direct-use` / `run-env-context-direct-use` / `expr-undefined-var` に寄った。
- `_test-*` を外したことで、テストハーネス由来の `bot-conditions` / `deny-inherit-secrets` も消えた。

### 4. 設定調整 2

残った 1 warning は `.github/actions/checkout/action.yaml` の `checkout-persist-credentials` だったが、これは wrapper action が `inputs.persist-credentials` を下流へ素通ししているだけで、`false` 固定を求めるのは実用的ではない。そこで次を追加した。

```yaml
exclusions:
  - file: .github/actions/checkout/action.yaml
    rules:
      - checkout-persist-credentials
```

最終結果: **30 errors in 10 files**

## 最終的に残した検出

| File | Count | 評価 |
|---|---:|---|
| `.github/workflows/benchmark-execute.yaml` | 12 | 妥当。`run:` 内で `${{ inputs.* }}` / `${{ env.* }}` を多用している |
| `.github/workflows/create-release.yaml` | 8 | 妥当。`run:` 内直接参照に加え、`inputs.nuget-path` 未定義検出は価値が高い |
| `.github/workflows/update-packagejson.yaml` | 3 | 妥当。`run:` 内での `inputs.*` 直接参照 |
| `.github/workflows/benchmark-loader.yaml` | 1 | 妥当。`run:` 内で `inputs.*` / event context を直接参照 |
| `.github/workflows/increment-version.yaml` | 1 | 妥当。`run:` 内で `inputs.*` を直接参照 |
| `.github/workflows/clean-packagejson-branch.yaml` | 1 | 妥当。`run:` 内で `inputs.branch` を直接参照 |
| `.github/actions/benchmark-progress-comment/action.yaml` | 1 | 妥当。heredoc / `printf` 内で `inputs.*` を直接参照 |
| `.github/actions/benchmark-runnable/action.yaml` | 1 | 妥当。Bash 比較式で `inputs.username` を直接参照 |
| `.github/actions/check-metas/action.yaml` | 1 | 妥当。Bash 比較式で `inputs.exit-on-error` を直接参照 |
| `.github/actions/unity-builder/action.yaml` | 1 | 妥当。Bash で `${{ env.UNITY_SERIAL }}` を直接参照 |

特に `create-release.yaml` の `expr-undefined-var` は有益だった。`workflow_dispatch` 側 inputs には `nuget-path` が定義されていないのに、`run:` では `inputs.nuget-path` を参照しており、実バグとして扱ってよい。

## 除外した検出の評価

### `Cysharp/*` 自己参照の `unpinned-uses`

- 技術的には fixable だが、このリポジトリでは自リポジトリ reusable workflow / action を `@main` 参照している箇所が多く、レビュー時のノイズが大きい。
- このケースは修正より config の `ignore-actions` で落とすほうが素直だった。

### `_test-*` workflows

- 明示的に `agentic` という名前やコメントを持つ workflow は見つからなかった。
- ただし `_test-*` は内部テストハーネスで、日常の運用レビュー対象としては優先度が低く、ここを除外すると結果がかなり見やすくなった。
- もし今後「生成物で手修正しない workflow」を本当に除外対象にするなら、命名規則かコメントで機械判定しやすくしておくと config が書きやすい。

### `checkout-persist-credentials` on wrapper action

- `.github/actions/checkout/action.yaml` は wrapper action で、caller の `inputs.persist-credentials` を渡している。
- この形に対して「必ず false にしろ」はフィットしないため、非実用的な warning と判断して rule 単位で除外した。

## `--fix` の使い勝手

`_post-release.yaml` を対象に、自己参照 `unpinned-uses` をサンプルとして `--fix` を評価した。

- 実行コマンド:
  - `seiton --fix --dry-run --enable-pin-network --enable-image-network -c <temp-config> .github/workflows/_post-release.yaml`
  - `seiton --fix --enable-pin-network --enable-image-network -c <temp-config> --verbose .github/workflows/_post-release.yaml`
- 観測:
  - `--dry-run` で差分が出なかった。
  - 実 fix でもファイル差分は出なかった。
  - それにもかかわらず verbose では `fixed 1 file(s)` と出た。

この挙動はかなり分かりづらい。少なくとも次のどれかが欲しい。

1. 実際に変更した diff を必ず出す
2. 変更ゼロなら `fixed 0 file(s)` にする
3. 「候補解決はできたが書き換えは不要だった / できなかった」理由を明示する

現状の表示だと、修正できたのか・できなかったのか・何が起きたのかをログだけで判断しづらい。

## CLI / ログの評価

### 良かった点

- `--help` は短く、主要オプションが把握しやすい。
- 診断メッセージに config 例 (`ignore-actions`) が含まれていて、その場で対処方針を決めやすい。
- `--verbose` は `config path`、discovery、file 数、per-file diagnostics、suppressed 件数まで出るので、調査ログとして有用だった。

### 気になった点

1. **config auto-discovery が親ディレクトリへ抜ける**
   - ネストされた参照リポジトリで親の `.github/seiton.yaml` を拾う。
   - 「対象 repo の root を越えたら止める」か、少なくとも verbose に「親の config を採用した」理由がもう少し欲しい。

2. **`--format json` が純粋な JSON ではない**
   - 実際の出力は「1 行目が JSON 配列、2 行目が `36 errors, 43 warnings in 32 files`」だった。
   - そのまま `ConvertFrom-Json` できず、機械処理に向かない。JSON モードでは summary も JSON に含めるか、stderr に逃がしたほうがよい。

3. **`--fix` の結果表示が信用しづらい**
   - `fixed 1 file(s)` と出ても diff が空だった。
   - ここは UX 上かなり重要で、修正結果の説明を増やしたい。

4. **config 調整導線は少し弱い**
   - `config` サブコマンドはなく、実質 `init` / `validate-config` / 診断メッセージの help を繋いで理解する形だった。
   - 慣れれば十分だが、初見だと「何をどこまで書けるか」を探る必要がある。

### 最終的なコンフィグ

```yaml
rules:
  unpinned-uses:
    ignore-actions:
      - owner: "Cysharp/*"
exclusions:
  - file: .github/workflows/_test-*.yaml
  - file: .github/actions/checkout/action.yaml
    rules:
      - checkout-persist-credentials
```

## まとめ

- `Cysharp/Actions` に対する最初の結果はノイズが多かったが、`ignore-actions` と `exclusions` を少し入れるだけで、**79 diagnostics → 30 diagnostics** まで整理できた。
- 最終的に残った 30 件は、ほぼ `run:` 内での context 直接参照と `create-release.yaml` の未定義 input で、レビュー価値が高い。
- 一方で、**親 config を拾う挙動**、**JSON 出力が純粋 JSON でない点**、**`--fix` が変更なしでも `fixed` と見える点** は、使い勝手とログ信頼性の改善余地が大きい。
