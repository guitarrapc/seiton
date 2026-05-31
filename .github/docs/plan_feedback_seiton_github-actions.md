# 修正プラン: githubactions-lab フィードバック (seiton 0.9.17)

## 出典

- フィードバック原文: [feedback_seiton_githubactions-lab.md](./feedback_seiton_githubactions-lab.md)
- 対象リポジトリ: `.references/githubactions-lab`（123 workflows）
- 評価バージョン: seiton 0.9.17
- 関連仕様: [Seiton_spec.md](./Seiton_spec.md), [Seiton_Linter_spec.md](./Seiton_Linter_spec.md), [Seiton_CLI_spec.md](./Seiton_CLI_spec.md), [Seiton_config_spec.md](./Seiton_config_spec.md)
- 横断参照: [feedback_seiton_cysharp-actions.md](./feedback_seiton_cysharp-actions.md)（`--fix` 結果表示の信頼性など一部重複）

## 総括

githubactions-lab での運用は、初回 83 issues → コンフィグ調整 + `--fix` 後 0 issues まで到達しており、**lint 精度・自動修正品質・出力可読性は概ね良好**と評価できる。

改善提案 10 件のうち **7 件は妥当**（実装または仕様更新の価値あり）、**2 件は部分妥当**（既存機能で代替可能だが UX 改善余地あり）、**1 件は現行仕様どおり**（意図的な制限だが拡張は検討可）。

| # | 項目 | 判定 | 優先度 |
|---|------|------|--------|
| 1 | `bot-conditions` の精度（dependabot 除外） | **部分妥当** — `!=` は既に info。ノイズ問題の本質は #10 | P1 |
| 2 | `run-inputs-context-direct-use` 複合式の自動修正 | **妥当** — 仕様上「未対応」だが help と矛盾する UX | P2 |
| 3 | 除外コンフィグの重複記述 | **妥当** — 動作は正しいが可読性・保守性の問題 | P2 |
| 4 | `--verbose` の情報過多 | **妥当** — 大規模リポジトリで実害あり | P2 |
| 5 | `seiton init` の exclusions 例不足 | **妥当** — 低コスト改善 | P3 |
| 6 | Agentic Workflow 自動検出 | **妥当** — memo でも手動除外を推奨済み | P2 |
| 7 | `--oneline` サマリー情報不足 | **妥当** — CI 監査用途で有用 | P2 |
| 8 | `--fix` 実行時の diff 表示 | **部分妥当** — `--fix --dry-run` で代替可能 | P3 |
| 9 | `exclude` vs `exclusions` の typo ヒント | **妥当** — 低コスト改善 | P3 |
| 10 | `bot-conditions` 複数トリガー誤検出 | **妥当** — 現行ロジックの gap | P1 |

---

## 良い点（対応不要・維持）

フィードバックで高評価された項目。回 regress しないようテスト・仕様で固定する。

| 項目 | 現状 |
|------|------|
| Rust 風 diagnostic 表示（`-->` スニペット） | [Seiton_CLI_spec.md §6](./Seiton_CLI_spec.md) |
| 末尾サマリーテーブル | CheckCommand per-file breakdown |
| `= help:` ヒント（一部ルール） | `run-inputs-context-direct-use` 等 |
| `--fix` 品質（env 変換・イメージ SHA ピン） | Linter spec §8 fix 表 |
| `--fix --dry-run` unified diff | CLI spec §1.2 |
| ファイル × ルール exclusions | [Seiton_config_spec.md §exclusions](./Seiton_config_spec.md) |
| 123 ファイル < 1s パフォーマンス | performance-requirements skill で維持 |
| `seiton rules` / `validate-config` | CLI spec §1.3–1.5 |

---

## 項目別判断と修正方針

### 1 & 10. `bot-conditions` の false positive（統合）

#### 現状

- `!=`（除外パターン）は **info**、`==`（権限付与）は **warning**（[Seiton_Linter_spec.md §rules](./Seiton_Linter_spec.md), [docs/rules.md §bot-conditions](../../docs/rules.md)）。
- **PR 系トリガーが 1 つでもあれば** `_hasPrEvent = true` となり、`push` + `pull_request` のような混合トリガーでも diagnostic を出す（`BotConditionsRule_MixedEvents_*` テストで意図的に維持）。
- PR 系トリガーが **一切ない** 場合（`push` only, `schedule` only）は **完全抑制**（Phase 3）。

#### フィードバックの妥当性

- **#1**: `github.actor != 'dependabot[bot]'` が広く使われる点は事実。ただし severity は既に info。フィードバック例の `warning[bot-conditions]` は **0.9.17 の実際の挙動（info）と不一致** — おそらく古い実行ログか severity 表記の混同。
- **#10**: 混合トリガーで `github.event.pull_request.user.login` が使えないケースは **技術的に正しい**。現行「PR トリガーがあれば常に指摘」は actionable でない場面を生む。

#### 修正方針（P1）

**WHY**: 混合トリガーワークフローでは `github.actor` が実質唯一の cross-trigger bot 除外手段。指摘しても修正不能なためノイズになる。

**WHAT**（仕様変更案）:

1. **抑制条件を拡張**: ワークフローの `on:` に PR 系以外のトリガー（`push`, `workflow_dispatch`, `schedule`, `issues` 等）が **1 つでも** 含まれる場合、job/step レベルの `if:` に対する `bot-conditions` を **完全抑制**する。
   - PR 系トリガーのみ（`pull_request`, `pull_request_target` 等だけ）のワークフローでは現行どおり warning/info を維持。
2. **help メッセージ**: 抑制できない PR-only ケースでは現行メッセージを維持。混合トリガーで抑制した場合、verbose モードのみ `bot-conditions: suppressed (mixed triggers; github.actor is acceptable)` のような 1 行を出す（通常出力では silent）。
3. **dependabot 専用除外オプション**（#1 の代替案）: 上記抑制で大半が解消するため **初期フェーズでは不要**。PR-only でも `!= dependabot` を info 抑制したい要望が残る場合のみ `rules.bot-conditions.ignore-patterns` 等を検討。

**更新対象**:

- `src/Seiton.Core/Linting/Rules/BotConditionsRule.cs` — 混合トリガー判定
- `tests/Seiton.Core.Tests/RuleInterfaceTests.BotConditionsRule.cs` — `MixedEvents_*` を抑制期待に変更、PR-only ケースは維持
- `Seiton_Linter_spec.md`, `docs/rules.md` — 抑制条件の記述更新

**検証**: `.references/githubactions-lab` の `auto-doc.yaml`, `create-release.yaml`, `auto-dump-context.yaml` で `bot-conditions` が出ないこと。`prevent-file-change2.yaml`（`pull_request_target` only）は引き続き actionable な指摘が出ること。

---

### 2. `run-inputs-context-direct-use` 複合式の自動修正

#### 現状

- 単純参照 `${{ inputs.KEY }}` は env 挿入 + シェル変数化の fix あり。
- **複合式**（`inputs.tag || (...)` 等）は fix なし、help のみ — [Seiton_Linter_spec.md §8 fix 表](./Seiton_Linter_spec.md) で **意図的に Partial**。

#### フィードバックの妥当性

- **妥当（機能拡張）**。help が「env ブロックへ移行」を示す以上、機械的 fix が可能で UX 一貫性が上がる。
- ただし **バグ報告ではない**。Fix Safety Policy（§8.5）上、式全体を env に移すのはセマンティクス等価で安全。

#### 修正方針（P2）

**WHAT**:

1. 複合式（単一 `${{ ... }}` ブロックで inputs / github.event.inputs を参照）を検出した場合:
   - ステップ `env:` に式全体を移動（変数名は `TAG_VALUE` 等、既存 env 名と衝突しない `_`-normalized 名を生成）。
   - `run:` 内の `${{ ... }}` をシェル変数参照に置換。
2. 以下は引き続き fix なし: heredoc no-expand、単一引用符内、flow-style `env`、空 `env: {}`、同一ステップ内に複数の独立した inputs 参照。

**更新対象**:

- `RunInputsContextDirectUseRule` / fix 生成ロジック
- `RuleInterfaceTests.LintEngine.cs` — `create-release.yaml` 相当の regression
- `Seiton_Linter_spec.md` §8 fix 表 — `run-inputs-context-direct-use` を compound 対応に更新

**参考**: `run-secrets-context-direct-use` も同パターン（compound → help only）のため、成功後に横展開を検討。

---

### 3. 除外コンフィグの重複記述

#### 現状

- 同一 `file:` の exclusion エントリが複数あっても **それぞれ独立に評価**され、機能的には正しい（`LintConfigLibrary.NormalizeExclusions` はマージしない）。
- YAML 上は冗長になり、カテゴリ別に追記した際に分散しやすい。

#### フィードバックの妥当性

- **妥当（UX / 保守性）**。動作バグではない。

#### 修正方針（P2）

**WHAT**（2 段階）:

1. **Phase A（validate-config）**: 正規化後に同一 `file` + 同一 `jobs` スコープの exclusion が複数ある場合、**info/warning diagnostic** を出す（例: `exclusion for '.github/workflows/matrix-secret.yaml' appears 2 times; consider merging rules into one entry`）。auto-fix はしない。

**更新対象**:

- `LintConfigLibrary.NormalizeExclusions` — 重複検出
- `tests/Seiton.Core.Tests/LintConfigLibraryTests.cs`
- `Seiton_config_spec.md`, `docs/configuration.md`

---

### 4. `--verbose` の情報過多

#### 現状

- `--verbose` で全ファイルの `checking ...` + per-file timing（123 ファイル → 246 行以上）を stderr に出力（[Seiton_CLI_spec.md §6.4](./Seiton_CLI_spec.md)）。

#### フィードバックの妥当性

- **妥当**。1000+ ファイルのモノレポでは stderr が実用限界を超える。

#### 修正方針（P2）

**WHAT**:

| レベル | 内容 |
|--------|------|
| デフォルト | 変更なし |
| `-v` | config path, discovery count, rules enabled/disabled, total timing, suppression 集計 |
| `-vv` | 現行の per-file checking + per-file results |
| `--verbose` | -v 相当とする |

**互換性**: 破壊的変更になるがいきなり変更でOK
**更新対象**: `VerboseLogger`, `CheckCommand`, `FixCommand`, `Seiton_CLI_spec.md`, `docs/usage.md`

---

### 5. `seiton init` の exclusions 例不足

#### 現状

- `LintConfigLibrary.GenerateTemplateYaml()` は glob + file-only 除外の 2 例のみ（[InitCommand.cs](../../src/Seiton/Commands/InitCommand.cs)）。

#### フィードバックの妥当性

- **妥当**。ドキュメント／テンプレ改善。実装コスト最小。

#### 修正方針（P3）

**WHAT**: `exclusions:` セクションにコメント例を追加:

- 1 ファイル × 複数ルール
- Agentic Workflow（`*.lock.yml` や `# gh-aw-metadata` ファイル）の file-only 除外
- `jobs:` スコープ付き除外（既存例を維持）

**更新対象**: `LintConfigLibrary.GenerateTemplateYaml()`, `docs/configuration.md`

---

### 6. Agentic Workflow 自動検出

#### 現状

- [memo.md](./memo.md) / seiton SKILL: Agentic Workflow は **手動で exclusions** する前提。
- githubactions-lab では `agentics-maintenance.yml`, `monthly-oss-repo-status.lock.yml` を手動除外（初回 4 errors + 17 warnings）。

#### フィードバックの妥当性

- **妥当**。`# gh-aw-metadata:` ヘッダーは安定した識別子。ユーザー編集不可ファイルの lint ノイズを削減できる。

#### 修正方針（P2）

**WHAT**:

1. **Discovery 時スキップ（opt-in）**: `discovery.skip-agentic-workflows: true`（config）または `--skip-agentic-workflows`（CLI）。ファイル先頭 ~10 行に `# gh-aw-metadata:` があれば lint 対象外。
2. **verbose 通知**: `verbose: discovery: skipped <file> (agentic workflow)`。
3. **init テンプレ**: 上記 config キーと exclusions 例をコメントで記載。

**非ゴール**: デフォルト自動除外（ユーザーが意図せずスキップするリスク）。opt-in を原則とする。

**更新対象**: `InputDiscovery`, `Seiton_config_spec.md`, `Seiton_CLI_spec.md`, SKILL / docs

---

### 7. `--oneline` サマリー情報不足

#### 現状

- issues 0 件時: `0 issues in 123 files` のみ（stderr）。
- suppressed / fully-excluded 件数は **verbose のみ**（`WriteSuppressionSummary`）。

#### フィードバックの妥当性

- **妥当**。CI で「本当に全部チェックしたか」を `--verbose` なしで確認したい。

#### 修正方針（P2）

**WHAT**:

- stderr サマリー行を拡張（`--oneline` 時および通常時共通）:
  ```
  0 issues in 123 files (2 excluded, 15 suppressed)
  ```
- `excluded` = file-level 全ルール除外で **parse/lint 自体をスキップ**したファイル数（`IsFileFullyExcluded`）。
- `suppressed` = 実行したが exclusion / inline directive で抑制した diagnostic 数（既存 `SuppressionSummary.TotalSuppressed`）。
- 0 のカテゴリは省略（`0 issues in 123 files` のまま）。

**更新対象**: `CheckCommand.WriteSummary`, `FixCommand`, `Seiton_CLI_spec.md §6.4`

---

### 8. `--fix` 実行時の diff 表示

#### 現状

- `--fix --dry-run` → stdout に unified diff（仕様どおり）。
- `--fix`（apply）→ diff なし。修正サマリー `Fixed X of Y issues in Z files` のみ。

#### フィードバックの妥当性

- **部分妥当**。dry-run で事前確認できるため **必須ではない**。
- [feedback_seiton_cysharp-actions.md](./feedback_seiton_cysharp-actions.md) も「fixed と出るが diff が空」問題を指摘 — 別 issue（pin 系でファイル touch 扱い）の可能性あり。

#### 修正方針（P3）

**WHAT**:

1. **ドキュメント強化**: `--fix` 前に `--fix --dry-run` を推奨する流れを usage / SKILL に明記（既に一部記載）。
2. **オプション追加（任意）**: `--show-diff` — apply 時も diff を stdout に出力（dry-run と同じフォーマット）。`-` と `--dry-run` の併用は `--dry-run` 優先。

**非ゴール**: apply 時の diff をデフォルト ON にしない（CI ログ肥大化）。

---

### 9. `exclude` vs `exclusions` typo ヒント

#### 現状

- 未知 top-level key → `unknown top-level key 'exclude'` のみ（[LintConfigYamlParser.cs](../../src/Seiton.Core/Linting/LintConfigYamlParser.cs)）。
- CLI 未知オプションには `Did you mean` あり（`CliOptionSuggester`）。rule-id 未知にも `RuleNormalizer.BuildUnknownRuleIdMessage`。

#### フィードバックの妥当性

- **妥当**。既存パターンの横展開で済む低コスト改善。

#### 修正方針（P3）

**WHAT**: 既知 top-level key 集合に対し Levenshtein / 部分一致で `Did you mean 'exclusions'?` を付与（`exclude`, `exclusion` 等）。

**更新対象**: `LintConfigYamlParser.Convert`, テスト, `docs/configuration.md` エラーテーブル

---

## 実装フェーズ

### フェーズ 1 — P1: bot-conditions 混合トリガー抑制

| タスク | 検証 |
|--------|------|
| 混合トリガー判定 + 抑制ロジック | unit tests 更新 |
| spec / docs 同期 | `dotnet test` 全通 |
| githubactions-lab 再実行 | `bot-conditions` 4 件が解消（または PR-only のみ残る） |

### フェーズ 2 — P2: CLI / config UX

| タスク | 依存 |
|--------|------|
| oneline サマリー拡張（excluded / suppressed） | なし |
| validate-config 重複 exclusion 警告 | なし |
| verbose レベル分け | 破壊的変更方針の決定 |
| Agentic Workflow opt-in スキップ | discovery 設計 |
| run-inputs 複合式 fix | test-first（red-green） |

### フェーズ 3 — P3: 低コスト polish

| タスク |
|--------|
| init テンプレ exclusions 例追加 |
| config typo `Did you mean` |
| `--show-diff`（任意） |
| usage / SKILL ドキュメント更新 |

---

## 仕様更新チェックリスト

実装完了後、spec-document-policy に従い以下を同期する。

- [ ] `Seiton_Linter_spec.md` — `bot-conditions` 抑制条件、fix 表（run-inputs compound）
- [ ] `Seiton_CLI_spec.md` — verbose レベル、oneline サマリー、skip-agentic、show-diff
- [ ] `Seiton_config_spec.md` — 重複 exclusion 警告、discovery.skip-agentic-workflows
- [ ] `docs/rules.md` — bot-conditions Notes
- [ ] `docs/configuration.md` — エラーメッセージ、exclusions 例
- [ ] `docs/usage.md` — verbose / fix ワークフロー

---

## 見送り・非ゴール

| 提案 | 理由 |
|------|------|
| `bot-conditions` デフォルト無効化 | セキュリティルールの意図を弱める |
| Agentic Workflow **デフォルト**自動除外 | 意図しないスキップリスク。opt-in のみ |
| 複合式 fix の無条件全ルール横展開 | run-secrets 等は別途安全評価後 |
| exclusions 自動マージ（silent） | ユーザー意図不明瞭。validate 警告で十分 |

---

## 参考: githubactions-lab 運用で確認されたパターン

ラボリポジトリ特有の **意図的アンチパターン** は exclusions で適切に管理できた。

- デモ用 `run-env-context-direct-use` / `dangerous-triggers` / `unpinned-image`
- Agentic Workflow 2 ファイルの手動 file exclusion
- `bot-conditions` グローバル無効化（本プラン P1 実装後は不要になる可能性）

これらは seiton の **ファイル × ルール exclusions 設計が機能している** 好例として、init テンプレと SKILL に反映する。
