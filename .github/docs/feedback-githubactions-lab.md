# seiton フィードバック — githubactions-lab

## 実行環境

| 項目 | 値 |
|------|-----|
| seiton バージョン | 0.9.18 (.NET 10.0.8, win-x64) |
| 対象リポジトリ | [guitarrapc/githubactions-lab](https://github.com/guitarrapc/githubactions-lab) |
| ワークフロー数 | 123 ファイル（`.github/workflows/`） |
| 評価方針 | 意図的なアンチパターンを示す workflow を除き、実リポジトリとして扱う |

---

## 実行手順と経過

### 1. 初回 lint（コンフィグなし）

```bash
seiton --verbose
```

**結果:** exit code 1 — **45 errors, 34 warnings, 1 info**（123 ファイル）

| ルール | 件数 |
|--------|-----:|
| run-env-context-direct-use | 28 |
| if-expr-wrapper | 16 |
| job-timeout-minutes-required | 12 |
| unpinned-image | 4 |
| dangerous-triggers | 3 |
| env-var | 3 |
| run-inputs-context-direct-use | 3 |
| runner-no-latest | 3 |
| unredacted-secrets | 3 |
| if-cond | 2 |
| bot-conditions | 1 |
| deny-inherit-secrets | 1 |
| run-secrets-context-direct-use | 1 |

verbose ログから状況把握は容易だった:

- `config: (none, using defaults)` — コンフィグ未検出
- `discovery: 123 file(s) resolved` — 探索範囲が明示
- ファイル別・ルール別のサマリーテーブルが末尾に出力
- `total: 123 file(s) checked in 45.5 ms` — 性能も確認可能

### 2. コンフィグ作成

```bash
seiton init
seiton validate-config --config .github/seiton.yaml
```

`seiton init` で `.github/seiton.yaml` のスターターを生成し、リポジトリ固有の exclusions を追記した。

**除外方針:**

| カテゴリ | 対象 | 理由 |
|----------|------|------|
| 外部管理 | `agentics-maintenance.yml`, `monthly-oss-repo-status.lock.yml` | 生成・外部管理 workflow |
| 意図的アンチパターン | `secrets-access.yaml`, `matrix-secret.yaml`, `job-needs-skip-handling-bad.yaml` 等 | ラボ用デモ（セキュリティ・needs 等） |
| デモ（digest 未 pin） | `container-job.yaml`, `container-service.yaml`, `dotnet-build*.yaml` | 意図的に digest 未固定 |
| 既知リスク（コメント付き） | `auto-dump-context.yaml`, `dump-context.yaml`, `prevent-file-change2.yaml` | `zizmor: ignore` コメントでリスク承知 |
| composite action | `git-push/action.yaml` | ローカル path 参照の false positive 回避 |

追加設定:

```yaml
rules:
  bot-conditions:
    enabled: false   # prevent-file-change2 の info ノイズ抑制

discovery:
  skip-agentic-workflows: true   # *.lock.yml を自動スキップ
```

### 3. コンフィグ適用後 lint

```bash
seiton --verbose
```

**結果:** **16 errors, 1 warning**（122 ファイル、1 excluded、30 suppressed）

意図的デモ workflow のノイズが消え、実運用に近い 9 ファイルへ絞れた。

### 4. auto-fix 適用

```bash
seiton --fix --dry-run   # プレビュー
seiton --fix --show-diff # 適用
```

**結果:** **34 件を 9 ファイルで修正、0 remaining**

修正対象:

| ファイル | 修正内容 |
|----------|----------|
| `_reusable-dump-context.yaml` | `${{ env.OUTPUT_PATH }}` → `${OUTPUT_PATH}`（8 ステップ） |
| `cache.yaml` | `if:` に `${{ }}` ラッパー追加 |
| `create-release.yaml` | `inputs.*` を `env: TAG` に移動し `${TAG}` 参照 |
| `create-release-simple.yaml`, `create-tag.yaml` | env 直接参照をシェル変数へ |
| `gitops-k8s-manifest.yaml` | job env をシェル変数参照へ |
| `setup-dotnet.yaml` | `DOTNET_ROOT` の参照方法修正 |
| `tag-push-only-context.yaml`, `workflowdispatch-inputs.yaml` | env 直接参照をシェル変数へ |

### 5. 最終確認

```bash
seiton --verbose
seiton --include-actions --verbose
```

**結果:** **0 issues**（122 workflow + 8 actions、1 excluded、30–31 suppressed）

---

## 検出の適切性評価

### 適切な検出（実リポジトリとして修正すべき）

| ルール | 評価 | 例 |
|--------|------|-----|
| `run-env-context-direct-use` | ✅ 適切 | `setup-dotnet.yaml`, `gitops-k8s-manifest.yaml` — インジェクションリスクのあるパターン |
| `run-inputs-context-direct-use` | ✅ 適切 | `create-release.yaml` — help メッセージ通り env ブロックへ移動が正解 |
| `if-expr-wrapper` | ✅ 適切 | `cache.yaml` — 式の `${{ }}` ラッパー不足 |
| `job-timeout-minutes-required` | ✅ 適切（外部管理除く） | `agentics-maintenance.yml` 等 — タイムアウト未設定は運用上の問題 |

### 意図的デモとして除外が妥当

| ファイル | ルール | 理由 |
|----------|--------|------|
| `secrets-access.yaml` | `run-secrets-context-direct-use` | セキュリティ章の意図的デモ |
| `matrix-secret.yaml` | `unredacted-secrets`, `env-var` | secret 取り扱いの実験 |
| `job-needs-skip-handling-bad.yaml` | `if-cond` | ファイル名どおり bad パターン |
| `reusable-workflow-caller-nest.yaml` | `deny-inherit-secrets` | `zizmor: ignore[secrets-inherit]` 付きデモ |
| `container-*.yaml`, `dotnet-build*.yaml` | `unpinned-image` | digest pin の比較デモ |

### 検出されなかったが正しいケース

| ファイル | 評価 |
|----------|------|
| `injection-attack-via-context.yaml` | ✅ env ブロック + シェル変数の正しいパターンを採用しており、誤検出なし |

### 要検討（false positive / 改善余地）

| ファイル | ルール | 内容 |
|----------|--------|------|
| `git-push/action.yaml` | `unpinned-uses` | `./.github/actions/signed-commit` は実在するが「path does not exist」と報告。composite action 単体 lint 時の path 解決問題の可能性 |

---

## auto-fix の使い勝手評価

### 良い点

1. **修正品質が高い** — `${{ env.X }}` → `${X}` への置換は、既存の `env:` ブロックを活かした自然な修正
2. **複合式も対応** — `create-release.yaml` の `inputs.tag || (...)` を `env: TAG` ブロックへ移動（help メッセージどおり）
3. **シェル種別を考慮** — PowerShell ステップでは `$env:BRANCH_NAME` 形式に修正（`default-shell.yaml` dry-run で確認）
4. **dry-run → apply の流れが素直** — `--fix --dry-run` で unified diff、`--fix --show-diff` で適用結果確認
5. **サマリーが明確** — `Fixed 34 of 34 issues in 9 files (0 remaining)` とファイル別内訳

### 改善余地

1. **JSON の `fixable` フラグ不一致** — `create-release.yaml` の `run-inputs-context-direct-use` が JSON では `fixable: false` だが、`--fix --dry-run` では修正可能だった
2. **dry-run 末尾の出力順** — 「Would fix」テーブルと残存 warning が混在し、一度に読みにくい
3. **非 fixable 問題への誘導** — `job-timeout-minutes-required` 等は fix 不可だが、`fix.defaults.job-timeout-minutes` を設定すれば fix 可能（init テンプレートにコメントあり）。検出メッセージから config へのリンクがあるとより親切

### pin / image ネットワーク fix

```bash
seiton --fix --dry-run --enable-pin-network --enable-image-network
```

コンフィグで `unpinned-image` を除外した状態では diff なし。dry-run 末尾の `hint: re-run with --enable-image-network to auto-fix image pinning` は有用。

---

## ログ・CLI の使い勝手評価

### 良い点

| 機能 | 評価 |
|------|------|
| リッチテキスト出力 | ファイル名・行番号・ソース行・キャレット表示で問題箇所が一目瞭然 |
| `= help:` 行 | `create-release.yaml` で env ブロック移動を具体的に提案 |
| `--verbose` | config パス、discovery 結果、suppressed 件数、ルール有効/無効、処理時間を表示 |
| サマリーテーブル | ファイル別 errors/warnings、ルール別件数 |
| `--oneline` | CI ログ向けにコンパクト |
| `--format json` | プログラム連携向け（`help` フィールドあり） |
| exit code | 0=問題なし, 1=lint 問題, 3=設定エラー と意味が明確 |
| `validate-config` | 設定ミスを事前検出 |
| `seiton rules` | ルール一覧と fix 可否が確認できる |

### 改善余地

| 項目 | 内容 |
|------|------|
| `validate-config --verbose` | `--verbose` が未対応（`Argument '--verbose' is not recognized`） |
| excluded vs suppressed | verbose ログで「excluded ファイル名」が一覧されない（件数のみ） |
| `--include-actions` | デフォルトは workflows のみ。composite action の lint には明示フラグが必要 |
| 初回体験 | コンフィグなしだと lab リポジトリでは 80 件超の検出となり、ノイズが多い。`seiton init` の案内が初回実行時にあるとよい |

---

## 最終状態

```
seiton --verbose
→ 0 issues in 122 files (1 excluded, 30 suppressed)

seiton --include-actions --verbose
→ 0 issues in 130 files (1 excluded, 31 suppressed)
```

**実運用 workflow:** 9 ファイルを auto-fix で修正し、lint clean を達成。

**意図的デモ workflow:** `.github/seiton.yaml` の exclusions で抑制（workflow 本体はデモ目的のパターンを維持）。

**外部管理 workflow:** ファイル単位 exclusion + `skip-agentic-workflows` でスキップ。

---

## 総合評価

| 観点 | 評価 | コメント |
|------|:----:|----------|
| 検出精度 | ★★★★☆ | 実問題の検出は的確。composite action の path 解決に 1 件 false positive |
| auto-fix 品質 | ★★★★★ | 修正内容が自然で、help メッセージと fix 結果が一致 |
| ログの読みやすさ | ★★★★☆ | verbose + テーブルで状況把握しやすい。dry-run 末尾の出力順に改善余地 |
| コンフィグの使いやすさ | ★★★★☆ | file / rules スコープの exclusion が効果的。init テンプレートが充実 |
| 初回体験（lab リポジトリ） | ★★★☆☆ | コンフィグなしでは意図的デモとの区別が必要。exclusion 設定後は実用レベル |

**結論:** seiton は GitHub Actions workflow のセキュリティ・ベストプラクティス違反を実用的に検出でき、auto-fix の品質も高い。研究用 lab リポジトリでは `.github/seiton.yaml` による exclusion 設定が必須だが、設定後は CI への組み込みに十分耐える使い勝手といえる。

---

## 付録: 作成・変更ファイル

| ファイル | 変更内容 |
|----------|----------|
| `.github/seiton.yaml` | 新規作成（init + exclusions 追記） |
| 9 workflow ファイル | `seiton --fix` による auto-fix |
