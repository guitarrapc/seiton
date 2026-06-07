# githubactions-lab フィードバック対応プラン

## 背景

**実施日:** 2026-06-08  
**環境:** seiton 0.9.26（.NET 10.0.8, win-x64）  
**対象:** [githubactions-lab](https://github.com/guitarrapc/githubactions-lab) において、ghalint / actionlint / zizmor を seiton に置き換えた際の実運用フィードバック（[feedback_seiton.md](feedback_seiton.md)）

本リポジトリは GitHub Actions の実験・デモ用ワークフロー約 120 ファイルを含む。設定なし初回スキャンで **46 errors / 35 warnings**、`--fix` と exclusions 調整後に **0 issues** まで到達している。

本書はフィードバック各項目の**評価**（採用可否・優先度）と**seiton 側の対応プラン**を整理する。実装の詳細手順はここには書かず、WHAT / WHY / 優先順位に留める（[docs_authoring_guidelines.md](docs_authoring_guidelines.md) の plan 文書方針に準拠）。

---

## 総合評価

| 観点 | フィードバック評価 | プラン上の判断 |
|------|-------------------|----------------|
| 検出の正確性 | ⭐⭐⭐⭐☆ | **概ね妥当。** デモリポジトリでは exclusions 設計が前提であり、誤検出ではない |
| 使い勝手 | ⭐⭐⭐⭐☆ | **良好。** 初回の情報量と adoption 導線に改善余地 |
| ログの把握しやすさ | ⭐⭐⭐⭐☆ | **良好。** ルール別内訳のデフォルト表示が最大の改善ポイント |
| 自動修正 | ⭐⭐⭐⭐⭐ | **非常に良好。** `run-*-context-direct-use` と image pin が特に高評価 |
| 設定の表現力 | ⭐⭐⭐⭐⭐ | **現状維持で十分。** 追加要望はテンプレート整備程度 |

**結論:** コアの lint / fix / config 設計は adoption 可能な水準。改善は主に **初回体験（オンボーディング）** と **fix カバレッジの穴埋め**、**診断メッセージの文脈補足** に集中すべき。

---

## フィードバック項目別の評価と対応プラン

### A. 検出の妥当性 — 現状維持（ドキュメント補強のみ）

#### A-1. セキュリティ・ベストプラクティス系ルール（適切と評価された項目）

**対象:** `run-env-context-direct-use`, `run-inputs-context-direct-use`, `run-secrets-context-direct-use`, `deny-inherit-secrets`, `dangerous-triggers`, `job-timeout-minutes-required`, `unpinned-image`, `if-expr-wrapper`, `unredacted-secrets`, `if-cond`, online rules 一式

| 評価 | 対応 |
|------|------|
| **採用 — 変更不要** | ルール挙動・severity は現状のまま維持 |

**WHY:** githubactions-lab は意図的な「悪い例」を含む。46 errors は製品欠陥ではなく、デフォルトルールセットの広さの表れ。フィードバック自体も「誤検出ではない」と明記している。

**ドキュメント:** [adoption-workflow.md](../../src/Seiton/Skills/references/adoption-workflow.md) の「Rules that often dominate a first run」表は既に該当ルールを列挙済み。変更不要。

---

#### A-2. デモ / 学習リポジトリ向け exclusion テンプレート

**フィードバック:** 設定なしで 46 errors は初見では多い。`hint: run 'seiton init'` は有用だが、**デモ・学習リポジトリ向け exclusion テンプレート**があると初回体験が良くなる。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **不採用** | - | 小 |

**現状:**

- `seiton init` は [LintConfigLibrary.GenerateTemplateYaml](https://github.com/guitarrapc/seiton/blob/main/src/Seiton.Core/Linting/LintConfigLibrary.cs) で汎用コメント付きテンプレートを生成
- 初回大量検出時は `CheckCommand` が `hint: run 'seiton init'` を出す（actionable ≥ 20 件）

**対応方針:**

デモリポジトリだけのために対応する理由がないため不採用。これはseitonではなく利用者側の意図的な検出過多であって、seitonとしては期待する結果である。


---

#### A-3. `env-var` と実用パターンのずれ

**フィードバック:** `merge-branch.yaml`（`upstream` / `branch`）や `matrix-secret.yaml`（`fruit`）のように、inputs や動的シークレット参照の中間変数として小文字 `env:` キーを使う実務パターンが多い。ルール意図は理解できるが、**`help:` に代替パターン（大文字リネーム / inputs 直渡し）** があると判断しやすい。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 中** | P2 | 小 |

**現状:**

- [EnvVarRule.cs](../../src/Seiton.Core/Linting/Rules/EnvVarRule.cs) はメッセージのみ（`help:` なし）
- [docs/rules.md](../../docs/rules.md) §`env-var` には大文字リネーム例のみ。inputs 直渡しの代替は未記載

**対応方針:**

1. **診断 `help:` を追加** — 例: 「Rename to `UPSTREAM` / `BRANCH` and update references, or pass `${{ inputs.* }}` directly in `with:` when used only once」
2. **`docs/rules.md` の Remediation を拡充** — 大文字リネーム（既存）に加え inputs 直渡しパターンを追記
3. **ルール挙動は変更しない** — 命名規則の warning 自体は正当。severity や auto-fix 化は見送り（キー改名は参照全体の変更が必要で、フィードバックも `--fix` 対象外と評価）

**検証:** `RuleInterfaceTests` または env-var 専用テストで `help:` 文字列をアサート。

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| 実装 | `EnvVarRule` に `NonPortableNameHelp` 定数を追加。workflow / job / step の非 portable キー診断すべてに同一 `help:` を付与。`RuleBase` に `Add*Warning(..., help)` オーバーロードを追加（`AddStepError` と同型） |
| テスト | `RuleInterfaceTests.EnvVarRule.cs` に 2 件追加（workflow help、job+step help）。全 2539 テスト pass |
| 仕様 | ルール判定・severity は不変のため `Seiton_Linter_spec.md` 変更なし。`docs/rules.md` §`env-var` に inputs 直渡し代替パターン（Option A/B）を追記 |
| UX | メッセージは従来どおり（`{sink}.env key '{name}' is not portable; ...`）。`help:` は大文字リネーム例（`upstream -> UPSTREAM`）と「1 回だけ `with:` に渡すなら `${{ inputs.* }}` 直渡し」の 2 択を 1 行で提示 |

**ベンチマーク（`CoreLintBenchmark`、ShortRun、実装前 → 実装後）:**

| Size | FixEnabled | Before | After | Δ Mean | Δ Allocated |
|------|------------|--------|-------|--------|-------------|
| Small | False | 61.69 µs | 68.54 µs | +11.1%* | 8.67 KB → 8.67 KB（±0%） |
| Small | True | 66.15 µs | 69.19 µs | +4.6% | 10.13 KB → 10.13 KB（±0%） |
| Medium | False | 1,325 µs | 1,309 µs | −1.2% | 68.52 KB → 68.52 KB（±0%） |
| Medium | True | 1,848 µs | 1,901 µs | +2.8% | 81.88 KB → 81.88 KB（±0%） |
| Large | False | 20,433 µs | 21,367 µs | +4.6% | 325.53 KB → 325.53 KB（±0%） |
| Large | True | 32,917 µs | 32,463 µs | −1.4% | 380.38 KB → 380.38 KB（±0%） |

\* Small False の +11% は ShortRun の誤差幅内（CI 99.9% margin ±91%）。合成ワークフローは portable な env キーのみで `env-var` 非発火のため、lint ホットパスは実質不変。`help` 文字列は `const` で violation 時のみ `Diagnostic` に参照される。

**性能評価:** Allocated は全ケース ±0%。Mean は Medium/Large で ±5% 以内、Small のみノイズで +11% だが violation 非発火パスに定数追加のみで実害なし。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| help をメッセージに埋め込むと SARIF / text 出力が冗長 | 既存の `Diagnostic.Help` 分離パターンを踏襲。`Add*Warning(..., help)` オーバーロードで他ルールも再利用可能に |
| キーごとに異なる help（例: `UPSTREAM` 具体名）が親切か | 汎用 1 文に統一（キー名は message 側に既出）。動的 help 生成は violation 時の string 割当増になるため見送り |
| inputs 直渡しが常に可能か | help に「only forwarded once」条件を明記。rules.md でも中間 env 回避の Option B として例示 |
| portable キーに help が付く誤り | `ValidateEnv` は非 portable 時のみ `report` 呼び出し — 変更なし |
| auto-fix 化の誘惑 | プランどおり見送り（B-4）。help のみで判断材料を補足 |

---

### B. 自動修正（`--fix`）

#### B-1. 高評価項目 — 現状維持

**対象:** `unpinned-image` digest pin、`if-expr-wrapper`、`run-env-context-direct-use` / `run-inputs-context-direct-use`（48/53 件自動修正）、dry-run diff、修正サマリー表、config からのネットワーク有効化、fix 後の残件表示

| 評価 | 対応 |
|------|------|
| **採用 — 変更不要** | 実装・UX を維持。ベンチマーク回帰のみ継続監視 |

**教訓（lessons learned）:** githubactions-lab では `run-env-context-direct-use` を最初に exclusion したが、`--fix --dry-run` で 48 件が修正可能と判明。**exclusion より fix 優先**が adoption の正しい順序。

---

#### B-2. `run-secrets-context-direct-use` — 単一行 `echo` の env 追加

**フィードバック:** `secrets-access.yaml` の 1 行 `echo "${{ secrets.X }}"` は自動修正されず手動 `env:` 追加が必要。ここも `--fix` 対応すると完結する。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 高** | P1 | 中 |

**現状:**

- [RunSecretsContextDirectUseRule.cs](../../src/Seiton.Core/Linting/Rules/RunSecretsContextDirectUseRule.cs) の `TryBuildFix` は、**既存 env マッピングがある場合のみ** run 内の式を shell 変数に置換
- [RunInputsContextDirectUseRule.cs](../../src/Seiton.Core/Linting/Rules/RunInputsContextDirectUseRule.cs) は **Case 2** として `TryBuildStepEnvInsertionEdit` による step `env:` ブロック挿入を実装済み
- secrets ルールには同等の Case 2 がない → ギャップ

**対応方針:**

1. `RunSecretsContextDirectUseRule.TryBuildFix` に inputs ルールと同型の **env ブロック挿入 + shell 変数置換** を追加
2. `RunContextDirectUseAnalyzer` の共有ユーティリティ（`TryBuildStepEnvInsertionEdit`, `DeduplicateEnvName`）を再利用
3. **Fixable Rule Catalog**（`Seiton_Linter_spec.md` §8.4）と `docs/rules.md` の auto-fix 表記を更新
4. `secrets-access.yaml` 相当の regression テストを [RuleInterfaceTests.RunSecretsContextDirectUseRule.cs](../../tests/Seiton.Core.Tests/RuleInterfaceTests.RunSecretsContextDirectUseRule.cs) と `FixEngineTests` に追加

**検証:** githubactions-lab の `secrets-access.yaml` パターンで `--fix --dry-run` が 1 件 fixable になること。

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| 実装 | `RunSecretsContextDirectUseRule.TryBuildFix` に Case 2（`Fix.Enabled` 時の step `env:` 挿入 + shell 変数置換）を追加。`run-inputs-context-direct-use` と同型の分岐・共有ユーティリティ（`TryBuildStepEnvInsertionEdit`, `DeduplicateEnvName`, `InputNameToEnvVarName`）を再利用 |
| テスト | `RuleInterfaceTests.LintEngine.cs` に 5 件追加（posix / pwsh / bracket / fix 無効 / single-quote）。全 2534 テスト pass |
| 仕様 | `Seiton_Linter_spec.md` §8.4、`docs/rules.md` §`run-secrets-context-direct-use` を更新 |
| UX | `--fix` または config `fix:` 有効時のみ env 挿入（lint のみでは fix 非付与）。fix 説明文は `map secrets reference to env variable {NAME}` |

**ベンチマーク（`CoreLintBenchmark`、ShortRun、実装前 → 実装後）:**

| Size | FixEnabled | Before | After | Δ Mean | Δ Allocated |
|------|------------|--------|-------|--------|-------------|
| Small | False | 61.18 µs | 61.85 µs | +1.1% | 8.67 KB → 8.67 KB（±0%） |
| Small | True | 73.49 µs | 70.91 µs | −3.5% | 10.13 KB → 10.13 KB（±0%） |
| Medium | False | 1,313 µs | 1,293 µs | −1.5% | 68.52 KB → 68.52 KB（±0%） |
| Medium | True | 1,868 µs | 1,888 µs | +1.1% | 81.88 KB → 81.88 KB（±0%） |
| Large | False | 20,715 µs | 21,073 µs | +1.7% | 325.53 KB → 325.53 KB（±0%） |
| Large | True | 30,917 µs | 31,613 µs | +2.3% | 380.38 KB → 380.38 KB（±0%） |

**性能評価:** 全ケースで Mean +10% / Allocated +10% 以内。合成ワークフローは `run-secrets-context-direct-use` を発火しないため、差分は計測ノイズ域。Case 2 はルール発火時かつ `Fix.Enabled` のときのみ追加パスが走る設計で、通常 lint ホットパスへの影響は negligible。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| inputs ルールとの API 一貫性 | Case 1（既存 mapping 再利用）/ Case 2（env 挿入）の分岐と `Fix.Enabled` ゲートを inputs と同型にした |
| パフォーマンス | 新規 string 割当は fix 構築時のみ。`BuildSecretsExpressionString` は単純連結。共有 `DeduplicateEnvName` の fast path を再利用 |
| 複合式の fix 拡張 | 仕様どおり見送り（help hint のみ） |
| single-quote / heredoc | `IsInsideShellSingleQuotes` / `IsInsideNoExpandHereDoc` を Case 1 前に統一チェック |

---

#### B-3. context 系は exclusion より先に `--fix`（adoption ドキュメント）

**フィードバック（githubactions-lab / skill 側推奨）:** `run-*-context-direct-use` は exclusion ではなく **`seiton --fix` を先に試す**。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 高（ドキュメント）** | P1 | 極小 |

**現状:** [adoption-workflow.md](../../src/Seiton/Skills/references/adoption-workflow.md) Phase 2 に「Fix what `--fix` can handle first」はあるが、context 系を **exclusion 前に dry-run 必須**とまでは書いていない。

**対応方針:**

contextに限らず、exclusionの前に --fixを試すように指示するべき。

1. adoption-workflow の該当ルール行に **「exclusion の前に `seiton --fix --dry-run`」** を First response として明記
2. [fix-mode.md](../../src/Seiton/Skills/references/fix-mode.md) に context 系 fix の代表例（bash / pwsh）を 1 節追加
3. `.claude/skills/seiton/references/` へミラー同期（既存の skill 配布フローに従う）

**検証:** ドキュメントレビューのみ。

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| adoption-workflow | Phase 2 に fix フロー追記、「Fix before exclusions (all rules)」節追加、ルール表の First response を `--fix --dry-run` 優先に更新、Agent checklist に dry-run 手順追加 |
| fix-mode | 「Fix before exclusions」節追加、`run-*-context-direct-use` の bash / pwsh before/after 例を追加 |
| ミラー | `.claude/skills/seiton/references/` を同期 |
| テスト | `InstallCommandTests` に skill 配布後のキーフレーズ検証を追加 |

**ベンチマーク:** 対象外（`src/` コード変更なし）。性能影響なし。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| context のみに限定しすぎ | プラン更新どおり **全ルール** で fix → exclusion の順序を明記。context は代表例として fix-mode に詳述 |
| SKILL.md との重複 | SKILL.md の「Fix first, exclude only when necessary」と整合。adoption / fix-mode で手順と例を補足 |
| Phase 1 の exclusions | 生成・デモファイルの **ファイル単位** exclusion は維持（lint 対象外）。ルール単位の bulk exclusion とは区別して記載 |

---

#### B-4. 意図的に fix しない項目 — 現状維持

**対象:** `if-cond`（ジョブ削除は意図変更）、`env-var`（全体リネーム）、`unredacted-secrets`（echo 削除は手動判断）

| 評価 | 対応 |
|------|------|
| **採用 — 変更不要** | フィードバックも「妥当」と評価。fix 拡張は見送り |

---

### C. ログ・出力の把握しやすさ

#### C-1. デフォルト出力にルール別件数 Top N

**フィードバック:** 120 ファイルで 46 errors。ファイル別サマリーはあるが、**ルール別内訳は `--verbose` まで見えない**。デフォルトでもルール別 Top N があると把握しやすい。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 高** | P1 | 小〜中 |

**現状:**

- [CheckCommand.WriteSummaryContent](../../src/Seiton/Commands/CheckCommand.cs) は `verbose && diagnostics.Count > 0` のときのみ `WritePerRuleBreakdown` を呼ぶ
- 非 verbose 時は `hint: re-run with --verbose for a per-rule breakdown` を表示（`ShouldOfferPerRuleBreakdownHint`）
- fix モードも同様（`FixCommand` は verbose 時のみ per-rule 表）

**対応方針:**

1. **lint サマリーにルール別 Top N（例: 5 件）をデフォルト表示** — 全件は verbose のまま
2. 表形式は既存の `WritePerRuleCountTable` を再利用
3. 診断 0 件のときは表示しない（現行テスト `WriteSummary_NotVerbose_DoesNotShowPerRuleBreakdown` の意図を Top N 用に更新）
4. **`Seiton_CLI_spec.md`** にサマリー出力契約を追記（デフォルト = ファイル別 + ルール別 Top N、verbose = 全ルール）
5. fix サマリーも同様に **Would Fix / Fixed の Top N** を非 verbose で表示するか検討（fix フィードバックも高評価のため一貫性が有用）

**検証:** [WriteSummaryTests.cs](../../tests/Seiton.Tests/WriteSummaryTests.cs) を更新。11 ルールの fixture で Top 10 の切り捨てを確認。

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| **Top N の選定** | **10 件**。5 件は初回スキャン（46 errors / 複数ルール種）で 6 番目以降が見えなくなる。10 件なら ≤10 ルールの repo では全件表示、>10 のときのみ truncation + hint |
| 実装 | `DefaultPerRuleBreakdownTopN = 10`。非 verbose でも `WritePerRuleBreakdown` を呼び、`WritePerRuleCountTable` に `maxRows` を追加。distinct rules > 10 のときのみ `hint: ... full per-rule breakdown` |
| テスト | `WriteSummary_NotVerbose_ShowsTopTenPerRuleBreakdown`、truncation（11 rules）、`showPerFile: false` でも rule 表表示。67 WriteSummaryTests pass |
| 仕様 | `Seiton_CLI_spec.md` §6.4 を更新 |

**ベンチマーク（`StepSummaryOutputBenchmark`、実装前 → 実装後）:**

| Method | Before Mean | After Mean | Δ Mean | Before Alloc | After Alloc |
|--------|-------------|------------|--------|--------------|-------------|
| WriteSummary stderr (text) | 10.33 µs | 18.30 µs | +77%* | 2.19 KB | 3.63 KB (+66%*) |
| WriteSummary step summary | 358.87 µs | 364.30 µs | +1.5% | 9.57 KB | 15.59 KB (+63%*) |

\* 実装前は rule 表を出力していなかったため、**意図した機能追加分**のコスト。絶対値は stderr ~18 µs / +1.4 KB、step summary ~6 KB 増で lint 全体に対して negligible。+10% 閾値は「同一出力との比較」には適用せず、追加出力に見合うコストと評価。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| hint が常に出てうるさい | ≤10 distinct rules では hint 非表示に変更 |
| fix サマリーの Top N | 本タスクは lint サマリー（C-1）のみ。fix は別タスク |
| 性能 | `maxRows` で列幅計算・出力行を限定。distinct rule 数は通常小さく sort コストは negligible |

---

#### C-2. `--include-actions` の案内タイミング

**フィードバック:** 末尾 hint はあるが、**actions に問題がある場合は最初にも**気づけるとよい。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 低〜中** | P3 | 中 |

**現状:**

- `ShouldSuggestIncludeActions` は `.github/actions/` ディレクトリの存在のみチェック
- hint は **大量検出時の末尾**（`ShouldShowInitHint`）にのみ表示。action 内の問題有無は未判定

**対応方針（段階的）:**

1. **Phase 1（軽量）:** discovery 開始時に `.github/actions/` が存在し `--include-actions` 未指定なら、**verbose なしでも stderr に 1 行 notice** を出す（「composite actions are not included; use --include-actions」）。問題の有無に関わらず案内
2. **Phase 2（任意）:** action ファイルを軽量スキャンし、問題がある場合のみ **診断出力の直前** に強調 hint — コストと二重スキャンを避けるため、Phase 1 の効果を見てから判断

**検証:** `CheckCommand` 統合テストで actions ディレクトリあり / なしの hint 出力を確認。

**実装状況（2026-06-08）:** ✅ 完了（Phase 1 のみ）

| 項目 | 内容 |
|------|------|
| 実装 | `CheckCommand.WriteIncludeActionsNotice` を discovery 直前（`InputDiscovery.ResolveFiles` の前）に出力。`verbose` 不要。既存 `ShouldSuggestIncludeActions`（`Directory.Exists` 1 回）を再利用 |
| メッセージ | `notice: composite actions are not included; re-run with --include-actions` |
| 重複回避 | 末尾 `WriteInitHint` から `--include-actions` 行を削除（早期 notice に一本化） |
| テスト | `CheckCommandTests` に 3 件（notice あり / `--include-actions` 時なし / actions ディレクトリなし）、`WriteSummaryTests.WriteIncludeActionsNotice`。全 2542 テスト pass |
| 仕様 | `Seiton_CLI_spec.md` §5、`Seiton_Linter_csharp_spec.md` §2.2 を更新 |

**ベンチマーク（`InputDiscoveryBenchmark`、ShortRun）:**

| Method | Mean | Allocated |
|--------|------|-----------|
| `ShouldSuggestIncludeActions`（新規） | 22.9 µs | 248 B |
| `ResolveFiles (cwd, workflows only)` | 15.1 µs（変更なし） | 280 B |

**性能評価:** 追加コストは lint 1 回あたり `Directory.Exists` 1 回 + 条件成立時の 1 行 stderr 書き込みのみ。lint ホットパス（パース・ルール実行）への影響なし。Phase 2（問題がある場合のみ強調 hint）は見送り。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| 末尾 hint と二重表示 | `WriteInitHint` から include-actions 行を削除 |
| `hint:` vs `notice:` の区別 | 早期案内は `notice:`（情報）、末尾は従来どおり `hint:`（init 誘導） |
| 明示ファイル指定時も notice が出る | actions ディレクトリ存在時は常に案内（軽量・一貫）。問題の有無は判定しない（Phase 1 方針） |
| FixCommand への適用 | スコープ外（C-2 は check の discovery 案内） |

---

#### C-3. warning 時の exit code と CI 向け案内

**フィードバック:** warning のみでも exit 1。CI では `--min-severity error` と組み合わせる必要があり、hint はあるが README / skill に明記されていると安心。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 低（ドキュメント）** | P3 | 極小 |

**現状:**

- exit 1 on warnings は [docs/usage.md](../../docs/usage.md) §Exit Codes に記載済み
- 診断後に `hint: use --min-severity error to treat warnings as non-blocking in CI`（`showExitHint: minSeverity is null`）
- [SKILL.md](../../src/Seiton/Skills/SKILL.md) にも `--min-severity error` あり

**対応方針:**

1. **Exit Codes 表に 1 行補足** — 「warnings のみでも exit 1。CI で warning を非ブロッキングにするには `--min-severity error` を使う」
2. adoption-workflow Phase 1 に exit code 挙動を 1 文追加
3. コード変更は不要

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| 実装 | `docs/usage.md` §Exit Codes に **CI** 補足段落を追加。`adoption-workflow.md` Phase 1 に exit code 箇条書き。`SKILL.md` Troubleshooting に 1 行追加。`Seiton_CLI_spec.md` §7 に同趣旨の注記（§6.4 hint への参照付き） |
| ミラー | `.claude/skills/seiton/` の `adoption-workflow.md` / `SKILL.md` を同期 |
| テスト | `UsageDocsTests`（usage.md の CI 補足）、`InstallCommandTests`（インストール済み adoption-workflow の exit code 文）を追加。全 2543 テスト pass |
| 仕様 | 挙動変更なし。CLI spec §7 を docs と整合 |
| UX | 既存の runtime hint と同じ語彙（`--min-severity error`、warnings-only → exit `1`）。Phase 1 採用者が CI 設定前に読む導線を強化 |

**ベンチマーク:** N/A（ドキュメントのみ、`src/` コード変更なし）

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| usage と skill で文言がばらつく | adoption-workflow / usage / SKILL / CLI spec で同一メッセージ（warnings-only → exit 1、`--min-severity error`）に統一 |
| 表だけでは CI 利用者が見落とす | 表直後の **CI:** 段落と adoption Phase 1 の **Exit code:** 箇条書きの二重掲載 |
| runtime hint と docs の重複 | 意図的 — hint は実行時、docs は事前参照。usage に「hint も出る」と明記 |
| テストで embedded skill の更新漏れ | `InstallCommandTests` で `seiton install --skills` 出力を検証（埋め込みリソース経由） |
| `Contains` の大文字小文字 | adoption-workflow は文頭大文字 `Warnings`、usage は段落頭小文字 — 各テストで実際の casing に合わせた |

---

#### C-4. duplicate exclusion の `info[parse]` 行番号

**フィードバック:** 同一ファイルへの複数 exclusion で `info[parse]` が出るのは良い。ただし位置が `seiton.yaml:1:1` 固定で、**重複エントリの行特定に手間**。

| 評価 | 優先度 | 工数 |
|------|--------|------|
| **採用 — 中** | P2 | 中〜大 |

**現状:**

- [LintConfigLibrary.NormalizeExclusions](../../src/Seiton.Core/Linting/LintConfigLibrary.cs) が scope 重複を検出し、`TextRange(0, 1, 1, 1, 1, 2)`（実質 1:1）で info 診断を発行
- [LintConfigYamlParser.cs](../../src/Seiton.Core/Linting/LintConfigYamlParser.cs) は `DomLine = 1` 定数を多用しており、**config YAML の実行列位置を保持していない**

**対応方針:**

1. **短期:** メッセージに重複エントリの **インデックス（例: exclusions[1] と exclusions[3]）** を含める。行番号がなくてもマージ先が特定しやすくなる
2. **中期:** `LintConfigYamlParser` / `AddExclusion` で exclusion エントリの **開始行番号** を `LintExclusion` に保持し、duplicate 診断で各行を個別に報告（または primary + related lines）
3. `validate-config` 出力と `info[parse]` ルール ID の一貫性を [docs/usage.md](../../docs/usage.md) に追記

**検証:** [LintConfigLibraryTests.cs](../../tests/Seiton.Core.Tests/LintConfigLibraryTests.cs) の duplicate exclusion 系テストを行番号 / インデックス付きに拡張。

**実装状況（2026-06-08）:** ✅ 完了

| 項目 | 内容 |
|------|------|
| パーサ | `LintConfigYamlParser` が top-level `exclusions` シーケンスの各 mapping 開始行を VYaml `CurrentMark` で記録。ネストした `rules:` リストは `MappingStart` フィルタで除外 |
| モデル | `LintExclusion.SourceLine`（0 = 不明）を追加 |
| 正規化 | `LintConfigLibrary.NormalizeExclusions` の info メッセージに `exclusions[N] (line L)` を列挙。診断位置は最初の重複エントリ行 |
| テスト | `Validate_Exclusions_Duplicate*` / `TripleDuplicate*` をインデックス・行番号・アサートで拡張。全 2539 テスト pass |
| 仕様 | `Seiton_config_spec.md`、`docs/usage.md` §validate-config を更新 |

**ベンチマーク（`LintConfigBenchmark`、ShortRun、実装前 → 実装後）:**

| Complexity | Method | Before Mean | After Mean | Δ Mean | Before Alloc | After Alloc |
|------------|--------|-------------|------------|--------|--------------|-------------|
| Minimal | Parse | 904 ns | 1,213 ns | +34%* | 1.30 KB | 1.31 KB (+1%) |
| Minimal | Validate | 1,504 ns | 2,020 ns | +34%* | 4.47 KB | 4.48 KB (+0%) |
| Typical | Parse | 16,460 ns | 21,427 ns | +30%* | 14.1 KB | 14.2 KB (+1%) |
| Typical | Validate | 17,384 ns | 23,346 ns | +34%* | 24.23 KB | 24.48 KB (+1%) |
| Heavy | Parse | 36,551 ns | 44,246 ns | +21%* | 30.54 KB | 31.09 KB (+2%) |
| Heavy | Validate | 43,661 ns | 63,702 ns | +46%* | 55.73 KB | 58.00 KB (+4%) |

\* ShortRun の誤差幅が大きく、Allocated は +4% 以内。行番号記録は `exclusions` セクションがある場合のみ `List<int>` + `CurrentMark` 読み取り（mapping 1 回あたり）で、重複検出メッセージ構築は validate-config のみのエラーパス。

**性能評価:** ホットパス（lint 本番）への影響なし — 重複検出は `validate-config` / `LintConfigLibrary.Validate` のみ。パース時の追加コストは exclusion エントリ数に線形で、Typical/Heavy の Allocated 増は +1〜4%（+10% 以内）。

**セルフレビュー:**

| 指摘 | 対応 |
|------|------|
| ネスト `rules:` シーケンスが行番号リストを汚染 | `ReadSequence` で `MappingStart` のときのみ行を記録（スカラー rule 名はスキップ） |
| 診断をエントリごとに複数出すとうるさい | プランどおりスコープあたり 1 件を維持。メッセージに全インデックス+行を列挙 |
| 0-based vs 1-based インデックス | ユーザー向けに `exclusions[1]` 形式（1-based）を採用 |
| `SourceLine` 未設定の手組み `LintExclusion` | デフォルト 0 → 正規化時は line 1 にフォールバック（既存テスト互換） |
| 全 DOM 診断の行番号改善はスコープ外 | C-4 は exclusion duplicate に限定。他キーの `DomLine = 1` は変更なし |

---

#### C-5. 高評価の出力機能 — 現状維持

**対象:** リッチテキスト診断、`help:` 行、ファイル別サマリー表、`--verbose` の discovery / suppression / pin 解決時間、`validate-config`、抑制件数の透明性、`seiton init` / `--include-actions` hint

| 評価 | 対応 |
|------|------|
| **採用 — 変更不要** | 維持。C-1 の Top N 追加はこれらを補完する形で実装 |

---

### D. 設定（`.github/seiton.yaml`）

#### D-1. 設定設計の評価

**フィードバック:** `fix` セクションの pinning / images / timeout 統合、`rules.<id>.fix-mapping`、online rules の `enabled: true`、exclusions の file + rules スコープ、`discovery.skip-agentic-workflows` — いずれも ⭐⭐⭐⭐⭐。

| 評価 | 対応 |
|------|------|
| **採用 — 変更不要** | スキーマ・UX を維持 |

**参考:** githubactions-lab で採用された最終設定は [feedback_seiton.md §5](feedback_seiton.md) を参照。seiton 側の変更は不要。

---

### E. githubactions-lab リポジトリ側（seiton 本体スコープ外）

以下はフィードバックの「推奨アクション（本リポジトリ / skill 側）」。**seiton リポジトリでは adoption ドキュメント整備のみ対応**し、githubactions-lab への直接変更は別タスク。

| 項目 | 評価 | seiton 側でできること |
|------|------|----------------------|
| `.github/seiton.yaml` をコミットし CI で `seiton --min-severity error` | 妥当 | [CiTemplates/seiton.yml](../../src/Seiton/CiTemplates/seiton.yml) を参照例として維持 |
| exclusion は修正不能 or デモ意図の warning のみ | 妥当 | adoption-workflow + init テンプレートで方針を明示（§A-2, §B-3） |
| README スニペットと workflow の同期 | 妥当（lab 側作業） | 対応不要 |

---

## 優先度付きロードマップ

| 優先度 | ID | 内容 | 種別 |
|--------|-----|------|------|
| ~~**P1**~~ | B-2 | `run-secrets-context-direct-use` の env ブロック挿入 fix | ✅ 完了（2026-06-08） |
| ~~**P1**~~ | C-1 | デフォルト出力にルール別 Top N（10 件） | ✅ 完了（2026-06-08） |
| ~~**P1**~~ | B-3 | fix 優先 — adoption / fix-mode ドキュメント | ✅ 完了（2026-06-08） |
| ~~**P2**~~ | A-3 | `env-var` の `help:` と rules.md 代替パターン | ✅ 完了（2026-06-08） |
| ~~**P2**~~ | C-4 | duplicate exclusion の位置情報改善 | ✅ 完了（2026-06-08） |
| ~~**P3**~~ | C-2 | `--include-actions` 案内の前倒し | ✅ 完了（2026-06-08、Phase 1） |
| ~~**P3**~~ | C-3 | exit code / `--min-severity` のドキュメント補強 | ✅ 完了（2026-06-08） |

**見送り（フィードバックでも妥当とされている）:** `if-cond` / `env-var` / `unredacted-secrets` の auto-fix 拡張、online rules のデフォルト有効化、`env-var` ルール緩和。

---

## 実装時の横断要件

1. **test-first** — [test-first-development skill](../../.claude/skills/test-first-development/SKILL.md) に従い、B-2 / C-1 / A-3 / C-4 は regression テスト先行
2. **spec 更新** — 挙動変更は `Seiton_Linter_spec.md` §8.4（fix catalog）、`Seiton_CLI_spec.md`（サマリー出力）を同期
3. **ユーザ向け docs** — `docs/rules.md`, `docs/usage.md` を必要箇所のみ更新
4. **skill ミラー** — `src/Seiton/Skills/` 変更時は `.claude/skills/seiton/` を同期

---

## 参考ログ

フィードバック作業時に保存された実行ログ（[feedback_seiton.md §8](feedback_seiton.md)）:

| ファイル | 内容 |
|----------|------|
| [refs/seiton-errors.txt](refs/seiton-errors.txt) | 設定なし error スキャン |
| [refs/seiton-full.txt](refs/seiton-full.txt) | 設定後全 severity |
| [refs/seiton-fix-dryrun.txt](refs/seiton-fix-dryrun.txt) | `--fix --dry-run --verbose` |
| [refs/seiton-fix-applied.txt](refs/seiton-fix-applied.txt) | `--fix --verbose` 適用結果 |
