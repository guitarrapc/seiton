# seiton フィードバック修正プラン — githubactions-lab

本書は [guitarrapc/githubactions-lab](https://github.com/guitarrapc/githubactions-lab) に対する seiton **0.9.18** の実地評価結果（[feedback-githubactions-lab.md](./feedback-githubactions-lab.md)）を精査し、各指摘の妥当性と修正方針を整理したもの。

参照リポジトリ実体: `.references/githubactions-lab/`（gitignore 対象。`sync-references.sh` で取得）

---

## 評価サマリー

| # | カテゴリ | 指摘 | 妥当性 | 対応方針 |
|---|----------|------|:------:|----------|
| 1 | 検出 | 実運用 workflow への `run-env-context-direct-use` 等の検出 | ✅ 適切 | 修正不要（現状どおり） |
| 2 | 検出 | 意図的デモ workflow の除外 | ✅ 適切 | 修正不要（`exclusions` で対応） |
| 3 | 検出 | `injection-attack-via-context.yaml` の非検出 | ✅ 適切 | 修正不要 |
| 4 | 検出 | `git-push/action.yaml` の local path false positive | ✅ **妥当（バグ）** | **P0: リポジトリ root 解決を修正** |
| 5 | auto-fix | 修正品質・複合式・シェル種別・dry-run フロー | ✅ 適切 | 修正不要 |
| 6 | auto-fix | JSON `fixable` と `--fix --dry-run` の不一致 | ✅ **妥当** | **P1: fixable 意味の整理** |
| 7 | auto-fix | dry-run 末尾の出力順 | ✅ **妥当** | **P1: 出力順の改善** |
| 8 | auto-fix | `job-timeout-minutes-required` の config 誘導不足 | ✅ **妥当** | **P1: help 追加** |
| 9 | auto-fix | pin/image network hint | ✅ 有用 | 修正不要（現状維持） |
| 10 | CLI | リッチ出力・verbose・exit code 等 | ✅ 適切 | 修正不要 |
| 11 | CLI | `validate-config --verbose` 未対応 | ✅ **妥当** | **P2: verbose 対応** |
| 12 | CLI | verbose で excluded ファイル名が見えない | ✅ **妥当** | **P1: 一覧出力** |
| 13 | CLI | `--include-actions` がデフォルト off | △ 仕様判断 | **P2: ドキュメント強化**（デフォルト変更は見送り） |
| 14 | CLI | 初回実行時の `seiton init` 案内不足 | ✅ **妥当** | **P1: ヒント追加** |

---

## 修正不要（確認済み）

### 検出精度

フィードバックで「適切な検出」と評価されたルールは、seiton の設計意図と一致している。

| ルール | 判断根拠 |
|--------|----------|
| `run-env-context-direct-use` | シェルインジェクション対策として `${{ env.* }}` 直接参照を禁止する仕様どおり |
| `run-inputs-context-direct-use` | 同上（`inputs.*` / `github.event.inputs.*`） |
| `if-expr-wrapper` | 式の `${{ }}` ラッパー不足を検出 |
| `job-timeout-minutes-required` | 外部管理 workflow を除けば運用上妥当 |

意図的デモ workflow（`secrets-access.yaml`、`matrix-secret.yaml`、`container-*.yaml` 等）への検出は、`.github/seiton.yaml` の `exclusions` で抑制するのが正しい運用。seiton 本体の変更は不要。

`injection-attack-via-context.yaml` が検出されないのも正しい。env ブロック + シェル変数パターンはルールの推奨 remediation と一致。

### auto-fix 品質

lab での auto-fix 結果（34 件 / 9 ファイル、0 remaining）は期待どおり。特に以下は現状維持:

- `${{ env.X }}` → `${X}` 置換
- 複合式の `env:` ブロック移動（`create-release.yaml` 等）
- PowerShell ステップでの `$env:VAR` 形式
- `--fix --dry-run` → `--fix --show-diff` のフロー

---

## P0 — バグ修正

### 4. composite action lint 時の local path false positive

**フィードバック:** `git-push/action.yaml` で `uses: ./.github/actions/signed-commit` が「path does not exist」と報告される。実在する path。

**妥当性:** ✅ 妥当。コード調査で根本原因を特定。

**原因:**

`UnpinnedUsesRule.ValidateLocalActionResolution` は `./.github/...` 形式の path を repo root 基準で解決しようとするが、`TryGetRepositoryRoot` が **`.github/workflows/` 配下のファイルにしか対応していない**。

composite action（`.github/actions/git-push/action.yaml`）を `--include-actions` で lint すると:

1. base directory が `.github/actions/git-push/` になる（workflows マーカー不一致）
2. 解決先が `.github/actions/git-push/.github/actions/signed-commit` となり存在しない
3. false positive が発生

同一ロジックが以下にも重複実装されている:

- `ActionRefHelpers.TryGetRepositoryRoot`（共有ヘルパー）
- `UnpinnedUsesRule.TryGetRepositoryRoot`（ローカルコピー）
- `ReusableWorkflowRule.TryGetRepositoryRoot`
- `LocalActionInputsRule.TryGetRepositoryRoot`

**修正方針:**

1. `ActionRefHelpers.TryGetRepositoryRoot` を拡張し、`.github/workflows/` に加え **`.github/actions/`** マーカーからも repo root を導出する
2. 各 Rule 内の重複 `TryGetRepositoryRoot` を `ActionRefHelpers` に統一（DRY）
3. 回帰テストを追加:
   - `ActionRefHelpersTests`: action ファイル path からの base directory 解決
   - `RuleInterfaceTests.UnpinnedUsesRule`: composite action 内の sibling local action 参照が warning にならないこと

**検証:** `.references/githubactions-lab` で `seiton --include-actions` を実行し、`git-push/action.yaml` の false positive が消えることを確認。

---

## P1 — UX 改善

### 6. JSON `fixable` フラグの不一致

**フィードバック:** `create-release.yaml` の `run-inputs-context-direct-use` が JSON では `fixable: false` だが、`--fix --dry-run` では修正可能。

**妥当性:** ✅ 妥当。

**原因:**

- lint モード（通常の `seiton`）では `Fix.Enabled = false`
- `RunInputsContextDirectUseRule` 等は `Config.Fix.Enabled` が true のときのみ `DiagnosticFix` を付与（Case 2: env ブロック挿入）
- JSON の `fixable` は `d.Fix is not null` で判定（`DiagnosticFormatter.WriteJson`）

そのため「ルール自体は auto-fix 対応（§8.4 △ Partial）」でも、lint 実行時の JSON では `fixable: false` になる。

**修正方針（WHY: CI / ツール連携が `--fix` 可否を正しく判断できるようにする）:**

| 案 | 内容 | 採否 |
|----|------|------|
| A | JSON `fixable` を「`seiton --fix` で修正可能か」に変更。lint 時も fix 生成条件を評価（Fix オブジェクトは付けない） | **推奨** |
| B | `fixableInFixMode` フィールドを追加し既存 `fixable` は維持 | 互換性重視時の代替 |

推奨案 A の実装イメージ:

1. `Diagnostic` に `FixEligibility`（または rule 側の静的判定）を導入
2. `DiagnosticFormatter` / Playground JSON で `fixable = Fix is not null || IsFixEligibleInFixMode(d)`
3. 条件付き fix（`job-timeout-minutes-required` は `fix.defaults.job-timeout-minutes` 必須、`unpinned-uses` は `--enable-pin-network` 必須）も eligibility に反映
4. `Seiton_Linter_spec.md` §8 と `docs/rules.md` に JSON `fixable` の意味を明記

**影響:** JSON 消費者向けの意味変更。破壊的変更の可能性があるため CHANGELOG に記載。

### 7. dry-run 末尾の出力順

**フィードバック:** 「Would fix」テーブルと残存 warning が混在し読みにくい。

**妥当性:** ✅ 妥当。

**原因:** `FixCommand` は残存 diagnostic を **先に** stdout へ出力し（L391–397）、その後 stderr に fix summary（L409–416）を出力している。コメント「Write fix summary FIRST」と実装が逆。

**修正方針:**

1. dry-run / apply 時の出力順を統一:
   - (1) fix summary（Would fix / Fixed テーブル）
   - (2) 残存 diagnostic（fix 後も残る問題）
   - (3) network fix hint
2. `--fix --check` モードは現状の diagnostic-first を維持（fix 未適用のため）

### 8. 非 fixable 問題への config 誘導

**フィードバック:** `job-timeout-minutes-required` は `fix.defaults.job-timeout-minutes` を設定すれば fix 可能だが、検出メッセージから config へのリンクがない。

**妥当性:** ✅ 妥当。

**修正方針:**

1. `JobTimeoutMinutesRequiredRule` で fix 未設定時に `Help` を付与:
   - 例: `to enable auto-fix, add to .github/seiton.yaml: fix: { defaults: { job-timeout-minutes: 15 } }`
2. `LintConfigLibrary.GenerateTemplateYaml()` のコメントアウト行を参照し、init テンプレートとの一貫性を保つ
3. 同パターンの条件付き fix ルール（`runner-no-latest` の `fix-mapping` 等）も必要に応じて help を追加

### 12. verbose で excluded ファイル名が見えない

**フィードバック:** verbose ログは excluded **件数**のみ表示。どのファイルが excluded か不明。

**妥当性:** ✅ 妥当。

**修正方針:**

1. `CheckCommand` / `FixCommand` で fully-excluded ファイル path を verbose 時に収集
2. `VerboseLogger.Log("excluded", ...)` でファイル名一覧を出力（件数が多い場合は `--verbose` Summary 以上で表示、Full では全件）
3. suppressed との対称性: suppressed はルール別集計、excluded はファイル別一覧

### 14. 初回体験 — `seiton init` 案内

**フィードバック:** コンフィグなしで lab リポジトリを lint すると 80 件超の検出となりノイズが多い。初回実行時に `seiton init` の案内があるとよい。

**妥当性:** ✅ 妥当（lab のような大規模・混合リポジトリ向け UX）。

**修正方針:**

1. config 未検出（`(none, using defaults)`）かつ lint 結果が一定閾値以上（例: actionable diagnostic ≥ 20）のとき、summary 末尾に 1 行 hint:
   - `hint: many issues detected with default config; run 'seiton init' to create .github/seiton.yaml and customize exclusions`
2. 毎回出力しない（セッション内 1 回、または CI では `--format json` 時は抑制）
3. `docs/usage.md` の Getting Started に lab 的リポジトリ向けの exclusions 例を追記

---

## P2 — 低優先・ドキュメント

### 11. `validate-config --verbose` 未対応

**フィードバック:** `seiton validate-config --verbose` で `Argument '--verbose' is not recognized`。

**妥当性:** ✅ 妥当だが影響は小。

**修正方針:**

1. `ValidateConfig` コマンドに `--verbose` を追加（`CheckCommand` と同じ `VerboseLevel`）
2. verbose 時: 読み込んだ config path、パース時間、有効 rule 数、exclusion 件数を表示
3. 他サブコマンド（`rules`, `init`）との一貫性を確認

### 13. `--include-actions` がデフォルト off

**フィードバック:** composite action の lint には明示フラグが必要。

**妥当性:** △ 指摘は正しいが、**デフォルト変更は見送り**。

**理由:**

- 大半の CI 利用は workflow のみ lint
- CI テンプレート（`CiTemplates/seiton.yml`）は既に `--include-actions` を指定
- デフォルト on にすると discovery 対象が増え、初回 lint のノイズ・実行時間が増加

**修正方針:**

1. `docs/usage.md` / `docs/configuration.md` に「composite action も lint する場合は `--include-actions`」を目立つ位置に記載
2. `seiton` 初回実行 hint（P1-14）と組み合わせ、actions ディレクトリが存在する repo では `--include-actions` も提案

---

## 実装フェーズ

### フェーズ 1 — P0 バグ修正（必須）

| タスク | 対象 |
|--------|------|
| repo root 解決の拡張 | `ActionRefHelpers.TryGetRepositoryRoot` |
| 重複コード統合 | `UnpinnedUsesRule`, `ReusableWorkflowRule`, `LocalActionInputsRule` |
| 回帰テスト | `ActionRefHelpersTests`, `RuleInterfaceTests.UnpinnedUsesRule` |
| 実地確認 | `.references/githubactions-lab` + `--include-actions` |

### フェーズ 2 — P1 UX（推奨）

| タスク | 対象 |
|--------|------|
| JSON fixable 意味変更 | `DiagnosticFormatter`, 条件付き fix ルール |
| dry-run 出力順 | `FixCommand` |
| job-timeout help | `JobTimeoutMinutesRequiredRule` |
| excluded ファイル一覧 | `CheckCommand`, `FixCommand`, `VerboseLogger` |
| init hint | `CheckCommand.WriteSummary` 付近 |

### フェーズ 3 — P2 ドキュメント・CLI 拡張

| タスク | 対象 |
|--------|------|
| validate-config --verbose | `Program.cs`, `ValidateCommand` |
| usage ドキュメント | `docs/usage.md`, `docs/configuration.md` |

---

## スペック更新

実装完了後、以下を更新する（[spec-document-policy](../.claude/skills/spec-document-policy/SKILL.md) 準拠）:

| ドキュメント | 更新内容 |
|--------------|----------|
| `Seiton_Linter_spec.md` | JSON `fixable` の意味、local path 解決の repo root 判定（actions 配下を含む） |
| `Seiton_Linter_csharp_spec.md` | `ActionRefHelpers` 統一、fix eligibility 判定の実装メモ |
| `docs/rules.md` | 条件付き auto-fix の config 前提（job-timeout 等）を Notes に追記 |

---

## 非ゴール

- lab リポジトリ固有の `exclusions` 設定を seiton デフォルトに組み込むこと
- `--include-actions` のデフォルト変更
- 意図的アンチパターン workflow に対するルール緩和

---

## 参照

- フィードバック原文: [feedback-githubactions-lab.md](./feedback-githubactions-lab.md)
- 評価対象: seiton 0.9.18 / githubactions-lab（123 workflows, 8 actions）
- 関連コード:
  - `src/Seiton.Core/Linting/ActionRefHelpers.cs` — `TryGetRepositoryRoot`
  - `src/Seiton.Core/Linting/Rules/UnpinnedUsesRule.cs` — `ValidateLocalActionResolution`
  - `src/Seiton/Commands/FixCommand.cs` — dry-run 出力順
  - `src/Seiton/Output/DiagnosticFormatter.cs` — JSON `fixable`
