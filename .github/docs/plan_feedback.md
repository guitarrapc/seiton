# githubactions-lab フィードバック — 評価と対応プラン

本書は [feedback_seiton.md](./feedback_seiton.md) に記録された、`.references/githubactions-lab` へ ghalint / actionlint / zizmor を seiton に置き換えて適用した際のフィードバックを評価し、対応方針を整理したもの。

## 前提

| 項目 | 内容 |
|------|------|
| 対象リポジトリ | `githubactions-lab`（良い例・悪い例・生成物が混在する教材リポ） |
| 置き換え対象 | ghalint / actionlint / zizmor |
| フィードバック時の seiton | **v0.9.25** |
| 本書作成時点の main | `rules: ["*"]` exclusion 正規化、lint 時の `--verbose` per-rule breakdown ヒント等が **v0.9.25 以降にマージ済み** |

フィードバックの数値・挙動は v0.9.25 基準。現行 main との差分は各項目の「現状」列に明記する。

---

## 評価サマリー

| 区分 | 件数 | 対応方針 |
|------|:----:|----------|
| 肯定的（維持・強化） | 8 | ドキュメント化・adoption 導線の補強 |
| 改善要望（UX） | 3 | 1 件は解消済み、2 件は未対応 |
| バグ（fix 競合） | 1 | 要修正（根本原因特定済み） |
| 運用知見（設定パターン） | 複数 | adoption ドキュメントへ反映 |

**総合判断**: 教材混在リポでも設定調整を前提に **実用可能**。ブロッカーは `unpinned-uses` の network pin 時 fix 競合 1 件。v0.9.25 以前の exclusion `*` 問題と per-rule ヒント不足は現行で解消済み。

---

## 1. 導入フロー・全体所感

### フィードバック

- `init` → `validate-config` → `--fix --dry-run` の流れが素直
- 初回は 46 errors / 35 warnings（120 files）とノイズが多いが、設定後は 32 errors / 17 warnings（127 files, 2 excluded）まで収束
- `--fix --dry-run` 全体で **Would fix 53 of 64 issues in 23 files (11 remaining)** → 最終 **3 errors / 8 warnings in 7 files**
- 教材混在環境でも **設定調整を前提にすれば実用的で、ログも十分読みやすい**

### 評価

| 観点 | 判定 | 理由 |
|------|:----:|------|
| 導入フロー | ✅ 妥当 | CLI 設計意図どおり。段階的 adoption（error のみ → warning → fix）と整合 |
| 初回ノイズ | ✅ 想定内 | 教材リポの性質上、exclusions / `skip-agentic-workflows` が必須。バグではない |
| dry-run 集計 | ✅ 強み | `Would fix / Remaining` と per-file 表は導入判断に有効（[Seiton_CLI_spec.md](./Seiton_CLI_spec.md) §6.4） |

### 対応プラン

| 優先度 | アクション | 成果物 |
|:------:|------------|--------|
| P2 | `githubactions-lab` 相当リポ向けの「初回設定テンプレ」を adoption 資料に追加 | `src/Seiton/Skills/references/adoption-workflow.md` または `docs/configuration.md` の Recipes 節 |
| P3 | フィードバックで確立した config 方針（下記 §6）を **参考例** としてリンク | `feedback_seiton.md` から `docs/configuration.md` へ相互参照 |

---

## 2. 検出の妥当性

### フィードバック

**妥当と判断されたルール**

- `run-env-context-direct-use`, `run-inputs-context-direct-use`, `run-secrets-context-direct-use`
- `deny-inherit-secrets`
- `unpinned-image`, `unpinned-uses`
- `if-expr-wrapper`

**意図的除外が適切なもの**

- agentic workflow、`.lock.yml`、攻撃デモ系 workflow
- 学習用 bad ケース（`if-cond`, `env-var` 等）

**fix 後も残った主な項目**（意図的 bad 例）

- `deny-inherit-secrets`, `run-secrets-context-direct-use`, `unredacted-secrets`, `if-cond`, `env-var`

### 評価

| 観点 | 判定 | 理由 |
|------|:----:|------|
| セキュリティ系検出 | ✅ 妥当 | ghalint / zizmor 代替として期待どおり。残件は教材の意図と一致 |
| 除外戦略 | ✅ 妥当 | file-only / rule-scoped exclusion の使い分けが適切 |
| actionlint 代替 | 🟡 部分 | 構文・式チェックは seiton がカバーするが、教材リポでは seiton 固有ルールが支配的 |

### 対応プラン

| 優先度 | アクション | 成果物 |
|:------:|------------|--------|
| P3 | adoption 資料の「よく出るルール」表に `unredacted-secrets` を追記（残件例として） | `adoption-workflow.md` |
| — | ルール検出ロジックの変更は不要 | — |

---

## 3. ログ可観測性

### フィードバック

- **総評: 高い** — `file:line:col`, `rule-id`, `help`, 集計テーブル、最終サマリが一貫
- 失敗時も hint があり行動につなげやすい
- **課題**: fix 競合時の内部理由が不足

### 評価

| 観点 | 判定 | 理由 |
|------|:----:|------|
| lint 出力 | ✅ 強み | 競合ツール比較でも差別化要素（[Seiton-feature-matrix.md](./Seiton-feature-matrix.md)） |
| fix 競合時 | ❌ 不足 | offset / length は出るが rule-id・診断位置・対処がない（§5 参照） |
| per-rule breakdown | 🟡 改善済み（lint） | v0.9.25 以降、`hint: re-run with --verbose for a per-rule breakdown` を lint サマリに表示 |

### 対応プラン

| 優先度 | アクション | 詳細 |
|:------:|------------|------|
| P1 | fix 競合エラーの診断強化 | §5 参照 |
| P2 | fix モード向け per-rule「修正予定」サマリ | §4.3 参照 |
| — | lint per-rule ヒント | **対応不要**（main 実装済み） |

---

## 4. 使い勝手改善要望

### 4.1 `rules: ["*"]` exclusion

**フィードバック**: v0.9.25 では `unknown rule-id '*'` で config parse エラー。エラーメッセージに file-only exclusion の代替案内がほしい。

**評価**

| 観点 | 判定 |
|------|:----:|
| 機能 | ✅ **main で解消** — `ExclusionNormalizer.IsAllRulesWildcard` により `rules: ["*"]` は `rules` 省略と同義に正規化（[Seiton_config_spec.md](./Seiton_config_spec.md)） |
| エラーメッセージ | 🟡 旧バージョン利用者向けの案内は未整備 |

**対応プラン**

| 優先度 | アクション |
|:------:|------------|
| — | 機能修正は **不要**（実装済み） |
| P3 | `docs/configuration.md` の Exclusions 節に「file-only = `rules` 省略または `rules: ["*"]`」を目立たせる |
| P3 | CHANGELOG / リリースノートで v0.9.25 以降の挙動変更を明記（再評価時の混乱防止） |

### 4.2 fix 競合時の詳細情報

**フィードバック**: `overlapping or conflicting edits` 発生時、競合ルール名や該当 edit の詳細があると原因追跡が容易。

**評価**

| 観点 | 判定 |
|------|:----:|
| 現状 | offset / length / batch 内 edit 数のみ |
| 期待 | 競合した rule-id ペア、診断の `file:line:col`、pin vs local fix の区別 |

**対応プラン** — §5 と統合（P1）

### 4.3 fix 時の rule 別サマリ

**フィードバック**: large diff では、どのルールで何件修正予定かのサマリがあると把握しやすい。

**評価**

| 観点 | 判定 |
|------|:----:|
| lint `--verbose` | ✅ 実装済み — Remaining 件数の per-rule 表（[Seiton_CLI_spec.md](./Seiton_CLI_spec.md) §6.4） |
| fix `--verbose` | ❌ 未実装 — per-file 表（Would Fix / Remaining）のみ。 **修正予定件数の per-rule 内訳はない** |
| フィードバックの意図 | fix dry-run で「53 件のうち unpinned-uses が何件か」を一目で知りたい |

**対応プラン**

| 優先度 | アクション | 仕様案 |
|:------:|------------|--------|
| P2 | `WriteFixSummary` に optional per-rule 表を追加 | `--verbose` 時のみ。列: `Rule` / `Would Fix`（dry-run）または `Fixed`（apply） |
| P2 | ヒント行の追加 | fix サマリ表示時に `hint: re-run with --verbose for a per-rule fix breakdown`（lint と対称） |
| P2 | 仕様更新 | `Seiton_CLI_spec.md` §6.4 fix モード節 |

---

## 5. バグ: pin fix 競合（`unpinned-uses` 重複）

### フィードバック

- 対象: `.github/workflows/prevent-file-change.yaml`
- 条件: 同一ファイル内に同一 `uses: actions/github-script@v9` が複数（異なる job）
- コマンド: `seiton --fix --dry-run --enable-pin-network <file>`
- 期待: 2 箇所それぞれ full SHA pin への diff
- 実際: 同一 offset（271）への edit が 2 件として扱われ、`overlapping or conflicting edits detected` で失敗
- 回避: 当該ファイルの `unpinned-uses` を exclusion で抑制

### 評価

| 観点 | 判定 | 根拠 |
|------|:----:|------|
| 再現性 | ✅ 確認 | フィードバックに再現コマンド・YAML 抜粋あり |
| 深刻度 | **P1（バグ）** | network pin は `--fix` の主要ユースケース。workaround は rule 単位抑制のみ |
| 根本原因 | **特定済み** | 下記 |

#### 根本原因分析

1. **`PinFixFormatter.TryBuildReplacementFix`**（`src/Seiton.Core/Linting/PinRemediation/PinFixFormatter.cs`）
   - 診断の `Location` 範囲内で `oldValue`（例: `actions/github-script@v9`）を検索
   - 範囲内に見つからない場合、**ファイル全体の最初の出現**（`IndexOf`）にフォールバック
2. **`UnpinnedUsesRule.BuildRefLocation`** は `@ref` 部分のみを `Location` に設定（フル `uses` 文字列より短い）
3. 同一 `uses` 文字列が複数あると、2 件目以降の診断でもフォールバックが **常に先頭出現** を指す → 同一 offset の edit が生成される
4. **`ApplyPinRemediationAsync`** は `FixEngine.Apply` を **一括適用**（`SelectNonConflictingBatch` 未使用）。local fix 用の iterative pass とは経路が異なる

local fix（`job-permissions-required` + `job-timeout-minutes-required` の同一 offset insert）は `SelectNonConflictingBatch` + iterative re-lint で回避済み（`FixCommandTests.Fix_OverlappingInserts_*`）。pin fix 経路のみ未対応。

### 対応プラン

#### フェーズ A — 正しい edit 位置（P1）

| ステップ | 内容 | 検証 |
|----------|------|------|
| A1 | `PinFixFormatter`: フォールバックを「診断 `Location.Start` 以降の最初の一致」に変更。グローバル先頭 `IndexOf` を廃止または最終手段に格下げ | `PinFixFormatterTests` に同一 uses 2 箇所のケースを追加 |
| A2 | 代替案: `Location` をフル uses 文字列幅に拡張（`BuildRefLocation` 変更）— A1 と併用可否を実装時に判断 | `UnpinnedUsesRule` 回帰テスト |

#### フェーズ B — 防御的 fix 適用（P1）

| ステップ | 内容 | 検証 |
|----------|------|------|
| B1 | `ApplyPinRemediationAsync` で `PinFixableDiagnostics` に `SelectNonConflictingBatch` を適用し、競合分は次パスへ defer（local fix と同様の iterative モデル） | `FixCommandTests` に `prevent-file-change` 相当 fixture |
| B2 | 複数 pin を 1 パスで適用できない場合も **部分適用で継続**（全件失敗にしない） | dry-run / apply 両方 |

#### フェーズ C — 診断メッセージ（P1）

| ステップ | 内容 |
|----------|------|
| C1 | `FixEngine` / `FixCommand` の競合例外に rule-id、診断 line:col、edit の NewText プレビュー（先頭 N 文字）を付与 |
| C2 | hint: `this often occurs when the same unpinned action appears multiple times; re-run with --verbose or apply fixes per job` |

#### 仕様・ドキュメント

- `Seiton_Linter_spec.md` §8（fix 適用）に「同一文字列の複数出現は診断位置ベースで個別 edit すること」を明記
- `docs/rules.md` の `unpinned-uses` **When fixing** に既知制限と回避策を追記（修正完了後に削除可能）

---

## 6. 設定パターン（運用知見）

フィードバックで有効だった設定。教材混在リポの **参考レシピ** としてドキュメント化する。

```yaml
# 要点のみ — 全文は feedback_seiton.md 参照
discovery:
  skip-agentic-workflows: true

exclusions:
  - file: .github/workflows/*.lock.yml
  - file: .github/workflows/agentics-maintenance.yml
  - file: .github/workflows/injection-attack-via-context.yaml
  # file-only exclusion（rules 省略）で全ルール抑制
  # prevent-file-change.yaml の unpinned-uses 抑制はフェーズ1修正後に削除可能

fix:
  defaults:
    job-timeout-minutes: 15
  pinning:
    enable-network: true
  images:
    enable-network: true

rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: ubuntu-24.04
      windows-latest: windows-2025
      macos-latest: macos-15
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true
```

### 評価

| パターン | 判定 | 対応 |
|----------|:----:|------|
| `skip-agentic-workflows` + `*.lock.yml` 除外 | ✅ 推奨 | adoption 資料に「教材 / agentic」節として記載 |
| online rules 4 種 enable | ✅ 妥当 | zizmor 相当の supply-chain チェック代替として整合 |
| `fix.pinning/images.enable-network` | ✅ 必須級 | `--enable-pin-network` CLI フラグと config の関係を adoption で明示 |
| `prevent-file-change` の rule 抑制 | 🟡 一時的 | §5 修正後に githubactions-lab で再評価し、exclusion 削除を試す |

---

## 7. 実装フェーズ（優先度順）

### フェーズ 1 — P1 バグ修正（pin fix 競合）— **実装済み（2026-06-07）**

**WHY**: `--fix --enable-pin-network` が同一 uses 複数箇所で失敗するのは、pinact / zizmor 置き換えの信頼性を損なう。

**完了条件**

- [x] `prevent-file-change.yaml` 相当 fixture で dry-run / apply が成功（`PinFixFormatterTests` / `PinRemediationTests`）
- [x] `PinFixFormatterTests` に duplicate uses ケース
- [x] 競合時メッセージに rule-id（`FixApplyConflictException` + `FixEngine` enrichment）

#### 実装内容

| コンポーネント | 変更 |
|----------------|------|
| `PinFixFormatter.TryFindReplacementOffset` | 診断 anchor（`@ref` 開始位置）を含む occurrence を選択。グローバル先頭 `IndexOf` フォールバックを廃止 |
| `FixCommand.ApplyPinRemediationAsync` | `SelectNonConflictingBatch` + 反復（re-lint / re-remediate）で pin fix を部分適用可能に |
| `FixApplyConflictException` | 競合 offset・edit 長・`rule-id` リストを構造化。CLI hint を競合専用に分岐 |
| `PinFixOffsetBenchmark` | 重複 uses 向け offset 解決のベンチマークを新規追加 |

#### セルフレビュー（実施済み）

| 指摘 | 対応 |
|------|------|
| 根本原因は `PinFixFormatter` の先頭一致フォールバック | anchor ベース解決に置換 |
| pin 適用が conflict-aware でない | `SelectNonConflictingBatch` 経由に変更 |
| 競合時の rule-id が不明 | `FixApplyConflictException` + diagnostic 適用時の enrichment |
| 未使用変数（`FixEngine.Apply`） | 削除 |
| UX: 汎用 hint が競合時に不親切 | `conflicting rule-id(s)` を参照する専用 hint |

#### ベンチマーク（ShortRun, Release, 本実装後）

| ベンチマーク | 条件 | Mean | Allocated/op | 備考 |
|--------------|------|------|--------------|------|
| `PinFixOffsetBenchmark` | 2 重複 uses | **~100 ns** | ~704 B | 新規。lint+pin fix 生成のホットパスはネットワーク待ちが支配的 |
| `PinFixOffsetBenchmark` | 8 重複 uses | **~392 ns** | ~2.8 KB | uses 数にほぼ線形（ファイル内スキャン窓は `oldBytes.Length` 上限） |
| `CoreLintBenchmark` | Small/Medium/Large | 変更前後で実質同等 | 変更なし | lint パス自体は未変更 |

**性能評価**

- **lint パス**: 変更なしのため CoreLint ベンチマークに有意差なし（±10% 以内）。
- **pin offset 解決**: 旧実装は誤って先頭一致するだけで安価だったが、正しい anchor 探索は **O(uses文字列長 × 同一文字列出現数)** の小さな窓スキャン。実測 8 重複でも **sub-µs** で、GitHub API 呼び出し（ms〜秒）に比べ無視できる。
- **pin 適用の反復**: 通常ケース（offset 正しい）は 1 パスで全 pin 適用。真の競合時のみ re-remediate が走る（エッジケース向けコスト）。

**変更ファイル**

- `src/Seiton.Core/Linting/PinRemediation/PinFixFormatter.cs`
- `src/Seiton.Core/Linting/Fixing/FixEngine.cs`
- `src/Seiton.Core/Linting/Fixing/FixApplyConflictException.cs`（新規）
- `src/Seiton/Commands/FixCommand.cs`
- `src/Seiton.Benchmark/PinFixOffsetBenchmark.cs`（新規）
- `tests/Seiton.Core.Tests/PinFixFormatterTests.cs`
- `tests/Seiton.Core.Tests/PinRemediationTests.cs`
- `tests/Seiton.Core.Tests/FixEngineTests.cs`
- `tests/Seiton.Tests/FixCommandTests.cs`
- `docs/rules.md`, `.github/docs/Seiton_Linter_spec.md`, `.github/docs/Seiton_Linter_csharp_spec.md`

### フェーズ 2 — P2 UX（fix per-rule サマリ）

**WHY**: 大規模 dry-run で修正インパクトの内訳が per-file のみでは不足。

**完了条件**

- [ ] `--fix --dry-run -v` で per-rule `Would Fix` 表
- [ ] `Seiton_CLI_spec.md` 更新
- [ ] `FixCommandTests` で表出力アサート

### フェーズ 3 — P2/P3 ドキュメント

**WHY**: フィードバック知見を次の採用者へ再利用可能にする。

**完了条件**

- [ ] `adoption-workflow.md` に教材混在リポ向け exclusions レシピ
- [ ] `docs/configuration.md` に `rules: ["*"]` / file-only exclusion の明記
- [ ] `unpinned-uses` fix 制限の記載（フェーズ 1 完了後）

### フェーズ 4 — 再検証（githubactions-lab）

**WHY**: フィードバックは v0.9.25 基準。修正後の実測で回帰確認。

**手順**

1. 現行 seiton（main または次リリース）を `githubactions-lab` に適用
2. `feedback_seiton.md` と同じコマンド列を再実行
3. 特に `prevent-file-change.yaml` で `--fix --dry-run --enable-pin-network` が成功すること
4. `prevent-file-change.yaml` の `unpinned-uses` exclusion を外して再試行
5. 結果を `feedback_seiton.md` に追記するか、本書の「再検証結果」節を更新

**成功基準（再検証）**

| 指標 | v0.9.25 実績 | 目標 |
|------|-------------|------|
| fix 競合 | 1 ファイルで失敗 | 0 失敗 |
| Would fix（全体 dry-run） | 53 / 64 | 同等以上（exclusion 削除後は件数増の可能性あり） |
| 最終残件 | 3 errors / 8 warnings / 7 files | 意図的 bad 例のみ残ること |

---

## 8. 非ゴール

- githubactions-lab の workflow 内容そのものの修正（seiton 側のタスクではない）
- actionlint との 1:1 診断一致の追求（スコープ外）
- 教材 bad 例の検出をデフォルトで無効化する機能（exclusions で十分）

---

## 9. 関連ドキュメント

| ドキュメント | 関係 |
|--------------|------|
| [feedback_seiton.md](./feedback_seiton.md) | 一次フィードバック・再現手順・最終 config |
| [Seiton-feature-matrix.md](./Seiton-feature-matrix.md) | 競合ツール比較 |
| [Seiton_CLI_spec.md](./Seiton_CLI_spec.md) | 出力・サマリ仕様 |
| [Seiton_config_spec.md](./Seiton_config_spec.md) | exclusion 正規化 |
| [competitor-ghalint-structure-details.md](./competitor-ghalint-structure-details.md) | ghalint 置き換え観点 |
| [competitor-actionlint-structure-details.md](./competitor-actionlint-structure-details.md) | actionlint 置き換え観点 |
| [competitor-zizmor-structure-details.md](./competitor-zizmor-structure-details.md) | zizmor 置き換え観点 |
