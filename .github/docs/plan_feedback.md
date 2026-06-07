# githubactions-lab 移行フィードバック — 評価と対応プラン

本書は [feedback_seiton.md](./feedback_seiton.md) に記録された、`.references/githubactions-lab` で actionlint / zizmor / ghalint を seiton（v0.9.25）に置き換えた際のフィードバックを評価し、seiton 本体への対応方針を整理したもの。

## 背景

| 項目 | 内容 |
|------|------|
| 対象リポジトリ | `guitarrapc/githubactions-lab`（ラボ用・意図的な bad practice を多数含む） |
| 置き換え対象 | actionlint + zizmor (medium) + ghalint |
| seiton バージョン | v0.9.25 |
| 評価観点 | 使い勝手、ログの把握しやすさ、設定移植のしやすさ |

移植後も **31 errors** が残った主因は、旧リンターがカバーしなかったルール（特に `run-env-context-direct-use`）と、意図的デモ workflow の存在。これは製品バグというより検出範囲の差異とラボ固有の CI 方針の問題である（後述 §6）。

---

## 評価サマリー

| # | 区分 | 項目 | 重要度 | 評価 | 対応 |
|---|------|------|--------|------|------|
| P1 | 良い点 | 診断フォーマット | — | 妥当。差別化要素 | 維持 |
| P2 | 良い点 | サマリー行（excluded / suppressed） | — | 妥当。設定デバッグに有効 | 維持 |
| P3 | 良い点 | ファイル別集計テーブル | — | 妥当。大規模リポで有用 | 維持 |
| P4 | 良い点 | `--verbose` の設定デバッグ情報 | — | 妥当 | 維持 |
| P5 | 良い点 | `init` / `validate-config` / `install --ci` | — | 妥当。導入導線として機能 | 維持 |
| P6 | 良い点 | `--oneline` | — | 妥当 | 維持 |
| P7 | 良い点 | 実行速度（~10–50 ms / 120 files） | — | 妥当 | 維持・ベンチマーク監視 |
| P8 | 良い点 | 除外設定の集約（1 ファイル化） | — | 妥当 | 維持 |
| B1 | 課題 | `jobs` スコープ付き exclusion の job-id 検証 | **重大** | **バグ（確認済み）** | 修正必須 |
| B2 | 課題 | `rules: ["*"]` 未サポート | 中 | **ドキュメントと実装の不整合** | 実装 or ドキュメント統一 |
| B3 | 課題 | `skip-agentic-workflows` の検出範囲 | 中 | **仕様どおりだが説明不足** | ドキュメント改善 |
| B4 | 課題 | ルール別 Count テーブルの表示条件 | 軽 | **UX の非対称** | 表示条件の整理 |
| B5 | 課題 | 旧リンターインライン抑制の移行 | 軽 | **移行支援不足**（機能欠如ではない） | ドキュメント / 将来ツール |
| I1 | 情報 | 検出範囲の差異（和集合だが完全一致ではない） | — | 想定内 | 移行ガイド整備 |
| B6 | 課題 | `validate-config` が unknown job-id を検出しない | 中 | **B1 の派生** | B1 修正後に拡張 |

---

## 詳細評価と対応プラン

### §1 良い点（P1–P8）— 維持・強化

いずれもフィードバックは正確で、現行実装の強みとして妥当。コード変更は不要。今後のリグレッション防止のみ意識する。

| 項目 | 評価 | 維持方針 |
|------|------|----------|
| 診断フォーマット（`error[rule-id]` + キャレット + `= help:`） | actionlint / rustc 系に慣れたユーザーにとって直感的。help 行が設定チューニングに繋がる点は差別化要素 | フォーマッタ変更時はスナップショットテストで回帰防止 |
| サマリー（`N errors … (Y excluded, Z suppressed)`） | 設定移植の検証にそのまま使えた。フィードバックの「設定あり実行 ◎」は妥当 | `CheckSummaryMetadata` の出力を E2E で固定 |
| ファイル別集計テーブル | ラボのようにファイル数が多いリポで優先度付けに有効 | デフォルト表示を維持（`showPerFile: true`） |
| `--verbose` | config パス、discovery、skip 理由、抑制件数が stderr に出る。設定デバッグに有効 | verbose ログ項目の追加・削除時は CLI spec を同期 |
| 導入コマンド群 | `init` → `validate-config` → lint の流れが機能した | CI テンプレート（`install --ci`）を release ごとに検証 |
| `--oneline` | CI / grep / annotation 連携向き | 維持 |
| 高速 | 旧構成（actionlint + zizmor Docker）との差は体感的にも大きい | `Seiton.Benchmark` で回帰監視 |
| 除外の集約 | zizmor インライン + `.zizmor.yaml` + `.ghalint.yaml` を `.github/seiton.yaml` に寄せられる | 移行ガイドで旧設定との対応表を公開（§6 参照） |

---

### §2 `jobs` スコープ付き exclusion の job-id 検証（B1）— 重大バグ

#### 評価

**妥当。再現条件はコードと一致する。**

`LintEngine.NormalizeExclusions` は、exclusion ごとの `jobs` 配列を **現在 lint 中の workflow** の `knownJobIdSlices` に対して検証している。exclusion の `file` パターンが現在のファイルにマッチするかどうかを見る前に検証するため、別ファイル向けの job-id が全ファイル分の `unknown job-id` エラーとして膨張する。

```1402:1416:src/Seiton.Core/Linting/LintEngine.cs
            if (exclusion.Jobs is not null && !knownJobIdSlices.IsEmpty)
            {
                for (var j = 0; j < exclusion.Jobs.Count; j++)
                {
                    var jobId = exclusion.Jobs[j];
                    if (!string.IsNullOrEmpty(jobId) && !ContainsJobIdOrdinalIgnoreCase(knownJobIdSlices, utf8Yaml, jobId))
                    {
                        _configDiagnostics.Add(new Diagnostic(
                            DiagnosticSeverity.Error,
                            $"unknown job-id '{jobId}' in exclusion configuration",
                            ...
```

フィードバックの回避策（ファイルスコープのみの除外）は正しいが、ghalint 互換の job スコープ除外という本来の用途を潰す。

既存テスト `LintEngine_ConfigExclusion_UnknownJobId_ReportsConfigurationError` は `file: "**/*.yml"` の glob 前提でこの挙動を期待しており、**特定ファイル向け exclusion のケースがカバーされていない**。

#### 対応プラン

**フェーズ A（必須・次リリース）**

1. job-id 検証を **exclusion の `file` パターンが現在の workflow パスにマッチするときだけ** 実行する。
2. マッチしない exclusion エントリの job-id はスキップ（他ファイル向けの設定として保持）。
3. テスト追加:
   - ファイル A 向け job 除外を設定し、ファイル B を lint しても `unknown job-id` が出ないこと。
   - ファイル A を lint したとき、存在しない job-id なら 1 件の設定エラーになること（回帰）。
4. 設定エラーの `FilePath` を **seiton.yaml** 側に付与し、workflow ファイルの `error[parse]` と混同しないようにする（可能なら行番号も config 側を指す）。

**フェーズ B（B6 と連動・推奨）**

`validate-config` 時に、リポジトリ内の workflow ファイルを走査し、exclusion の `file` + `jobs` の組み合わせで unknown job-id を **lint 前に** 報告する。

- `LintConfigLibrary.ValidateFile` の後段、または `validate-config` 専用の cross-file 検証ステップとして実装。
- workflow ファイルが存在しない glob パターンは warning のみ（CI ではファイル未 checkout の可能性あり）。
- `--verbose` でどの exclusion / どの workflow を検証したかを出す。

**完了条件**

- フィードバックの再現 YAML で 119 ファイル lint 時に `unknown job-id` が 0 件（対象ファイル以外）。
- `reusable-workflow-caller-nest.yaml` で job スコープ除外が機能すること。
- `validate-config` が unknown job-id を検出できること（フェーズ B）。

#### 実装記録（B1 — 2026-06-07）

**実装内容**

| 対象 | 変更 |
|------|------|
| `LintEngine.NormalizeExclusions` | `jobs` の unknown job-id 検証を **exclusion `file` が現在の workflow にマッチするときのみ** 実行 |
| `LintConfig.ConfigFilePath` | 設定ファイルパスを保持。exclusion 設定診断の `FilePath` に使用 |
| `LintConfigLibrary.Validate` / `CliConfigBridge` / `CheckCommand` / `FixCommand` | `ConfigFilePath` の伝播 |
| テスト | 他ファイル lint 時に誤検出しないこと、マッチ時に config パスで報告すること、有効 job で抑制されること |
| 仕様 | `Seiton_Linter_spec.md` §5.4、`Seiton_Linter_csharp_spec.md`、`docs/configuration.md` |

**ユーザーファースト API レビュー**

- 別 workflow 向けの job 除外が全ファイルで `error[parse]` になる挙動を修正。設定ミスは **seiton.yaml** に紐づく。
- `**/*.yml` のような広い glob は従来どおり各ファイルで job-id を検証（既存テスト維持）。

**性能**

| 項目 | 結果 |
|------|------|
| `CoreLintBenchmark`（Small/Medium/Large × Fix on/off） | Ratio **1.00**（Mean / Allocated ベースライン同等） |
| 改善点 | マッチしない job-scoped exclusion では `BuildKnownJobIdSlices` を遅延構築（該当 exclusion が無い workflow ではスキップ） |
| 低下 | なし |

**セルフレビューと対応**

| 指摘 | 対応 |
|------|------|
| exclusion 設定診断が workflow パスに付いていた | `ConfigFilePath` を導入し exclusion 正規化診断に使用 |
| `FixCommand` の LintConfig クローンで `ConfigFilePath` が落ちる | クローンに追加 |
| `NormalizeExclusionPattern` の二重呼び出し | ループ内で 1 回に集約 |

**ステータス**: B1 フェーズ A 完了。フェーズ B（`validate-config` 横断検証）は未着手。

---

### §3 `rules: ["*"]` 未サポート（B2）— ドキュメントと実装の不整合

#### 評価

**妥当。** `ExclusionNormalizer.CollectResolvedExclusionRules` は `RuleCatalog.TryResolveRuleId` のみで、`*` を「全ルール」として扱わない。`unknown rule-id '*'` → exit code 3 は実装どおり。

一方、正規のユーザー向けドキュメント [docs/configuration.md](../../docs/configuration.md) は `rules` 省略で全ルール抑制と記載しており正しい。不整合は主に **Agent Skill**（`.claude/skills/seiton/SKILL.md`, `src/Seiton/Skills/SKILL.md`）が `rules: ["*"]` を推奨している点。

`LintConfigLibrary` / パーサーは `rules` 省略 → `null`（全ルール）を既にサポートしている（`Validate_Exclusions_FileOnly_NoRules_ExcludesAllRules` テストあり）。フィードバックの回避策（`rules` 省略）は有効。

#### 対応プラン

**推奨: 実装で `*` を受け入れる（後方互換 + Skill との整合）**

1. `ExclusionNormalizer`（および `LintConfigLibrary` 正規化）で `rules` 内の `"*"` を「全ルール抑制」（`resolvedRules = null` 相当）として解釈。
2. `rules: ["*"]` と `rules` 省略が同義であることを `Seiton_config_spec.md` に明記。
3. Skill の例は `rules` 省略を第一選択、`["*"]` を明示的エイリアスとして併記。
4. テスト: `rules: ["*"]` でパース成功・ファイル全体除外が効くこと。

**代替（実装を増やしたくない場合）**

Skill / `docs/configuration.md` から `rules: ["*"]` の記述を削除し、`file` のみの exclusion に統一。ただし Skill 利用者には破壊的なドキュメント変更になる。

**完了条件**

- `rules: ["*"]` で `validate-config` が成功する。
- Skill と `docs/configuration.md` の記述が一致する。

#### 実装記録（B2 — 2026-06-07）

**実装内容**

| 対象 | 変更 |
|------|------|
| `ExclusionNormalizer` | `AllRulesWildcard` (`"*"`) と `IsAllRulesWildcard` を追加 |
| `LintConfigLibrary` / `LintEngine` | `rules: ["*"]` を `rules` 省略と同義（`null` = 全ルール）に正規化 |
| `ExclusionMatcher` | `rules: ["*"]` をファイル全体除外として扱う |
| テスト | `Validate_Exclusions_AllRulesWildcard_*`, `LintEngine_ConfigExclusion_AllRulesWildcard_*`, `ExclusionMatcherTests` |
| 仕様・ドキュメント | `Seiton_config_spec.md`, `Seiton_Linter_spec.md`, `docs/configuration.md`, Skill |

**ユーザーファースト API レビュー**

- `rules` 省略を推奨しつつ、Skill / 移行ユーザーが使う `["*"]` を正式サポート。
- `["*"]` はファイル全体除外（parse error 含む short-circuit）と同等。

**性能**

| 項目 | 結果 |
|------|------|
| `LintConfigBenchmark`（Minimal/Typical/Heavy） | Ratio **1.00** |
| 理由 | ルール ID リストの線形スキャン 1 回のみ。ホットパスへの影響なし |

**ステータス**: B2 完了。

---

### §4 `skip-agentic-workflows` の検出範囲（B3）— 仕様どおり、説明不足

#### 評価

**妥当だが、期待と仕様のギャップがある。**

実装は先頭 ~10 行に `# gh-aw-metadata:` を含むファイルのみスキップ（`AgenticWorkflowSkipTests`, `Seiton_config_spec.md` §2.3.1）。`monthly-oss-repo-status.lock.yml` はスキップ、`agentics-maintenance.yml`（`DO NOT EDIT` のみ）はスキップされない — **現仕様どおり**。

`seiton init` テンプレートのコメントが「agentics-maintenance を列挙」と `skip-agentic-workflows` の関係を曖昧にしている可能性がある。

#### 対応プラン

**フェーズ A（ドキュメント・テンプレート — 必須）**

1. `docs/configuration.md` と `Seiton_config_spec.md` に以下を明記:
   - `skip-agentic-workflows` が検出するのは **`# gh-aw-metadata:` ヘッダーのみ**。
   - metadata のない gh-aw 生成物（例: `agentics-maintenance.yml`）は **`exclusions` でファイル単位除外**が必要。
2. `seiton init` 生成テンプレート（`LintConfigLibrary`）のコメントを修正し、上記の使い分けを 1–2 行で説明。
3. Skill の Agentic Workflow 節を同内容に同期。

**フェーズ B（任意・将来）**

metadata なし gh-aw 生成物のヒューリスティック拡張（例: 先頭コメントに `DO NOT EDIT` + 既知の gh-aw パスパターン）。誤検知リスクがあるため、opt-in フラグまたは別キー（`skip-generated-workflows-patterns`）で検討。

**完了条件**

- 移行ユーザーが「なぜ lock.yml だけ skip されたか」をドキュメントだけで理解できる。
- init テンプレートと Skill に矛盾がない。

#### B4-フェーズ A 実装記録（2026-06-07）

**実装内容**

| 対象 | 変更 |
|------|------|
| `docs/configuration.md` | `## Discovery` 節を新設。`skip-agentic-workflows` と `exclusions` の使い分け表・例を追加 |
| `.github/docs/Seiton_config_spec.md` | §2.3.1 に gh-aw 使い分け・`agentics-maintenance.yml` 例を追記 |
| `src/Seiton.Core/Linting/LintConfigLibrary.cs` | `seiton init` テンプレのコメント修正（`*.lock.yml` を exclusion 例から削除、metadata 検出条件を明記） |
| `.github/seiton.yaml` | テンプレと同内容のコメント整合 |
| `docs/usage.md` | `--skip-agentic-workflows` の説明を first 10 lines 基準に更新 |
| Skill（`.claude/skills/seiton/SKILL.md`, `src/Seiton/Skills/SKILL.md`） | Agentic Workflow 節を二段構え（discovery vs exclusions）に書き換え。`rules: ["*"]` 例を file-only exclusion に変更 |
| Skill references（`src/Seiton/Skills/references/configuration.md` 等） | `discovery` スキーマと gh-aw パターンを追加 |
| テスト | `GenerateTemplateYaml_AgenticWorkflowDocs_ClarifySkipVsExclusions` 追加、`AgenticWorkflowSkipTests.ResolveFiles_SkipAgenticWorkflows_DoNotEditWithoutMetadata_IsNotSkipped` 追加 |

**ユーザーファースト API レビュー**

- 誤解の元だった「`# gh-aw-metadata:` または `*.lock.yml`」表現を廃止。ロックファイルは **マーカー** でスキップされることを明示。
- `agentics-maintenance.yml` は **exclusions（file のみ）** が必要と三箇所（ユーザー doc / spec / init テンプレ）で統一。
- Skill 例は未サポートの `rules: ["*"]` ではなく、動作する `file` のみ exclusion を採用。

**仕様整合**

- 実装ロジック（`AgenticWorkflowDetector`）は変更なし。`Seiton_config_spec.md` の記述を拡張し、既存仕様と一致。

**性能**

| 項目 | 結果 |
|------|------|
| 変更の性質 | テンプレ文字列・ドキュメントのみ。lint / discovery の実行パスに変更なし |
| `CoreLintBenchmark`（Small/Medium/Large × Fix on/off） | Ratio **1.00**（Mean / Allocated ともベースラインと同等） |
| 理由 | ホットパスに触れていないため性能差なし。新規ベンチマーク追加は不要 |

**セルフレビューと対応**

| 指摘 | 対応 |
|------|------|
| init テンプレの小文字コメント行が `GenerateTemplateYaml_Uncommented_IsValidConfig` で不正 YAML になる | コメント行を `Gh-aw file without...`（先頭大文字）に変更し prose 扱いでアンコメント対象外に |
| `verboseLogger: null` でコンパイルエラー | `VerboseLogger.Create(VerboseLevel.Off, TextWriter.Null)` を使用 |

**ステータス**: フェーズ A 完了。

---

### §5 ルール別 Count テーブルの表示条件（B4）— UX の非対称

#### 評価

**妥当。** 現行実装では:

- ファイル別テーブル: `showPerFile && diagnostics.Count > 0`（デフォルト `showPerFile: true`）
- ルール別テーブル: `verbose && diagnostics.Count > 0` のみ（`CheckCommand.WriteSummaryContent`）

そのため設定あり実行でファイル別だけ出てルール別が出ないのは **仕様どおり**だが、フィードバックの「初回は両方出た」という体験との差は **`--verbose` の有無**か、実行モードの違いで説明できる。ユーザーにとっては「ファイル別が出るならルール別も欲しい」という期待は自然。

#### 対応プラン

**推奨: ルール別テーブルをファイル別と同条件で表示**

1. `WritePerRuleBreakdown` の条件を `diagnostics.Count > 0` に変更（`showPerFile` と揃える、または `showPerFile` が true のとき常に両方）。
2. `--oneline` 時はテーブル非表示のまま維持（1 行 1 診断と競合するため）。
3. `--verbose` 専用だったルール別テーブルは、verbose 時の追加情報（Active rules 一覧など）と役割分担を CLI spec に記載。

**代替**

表示は変えず、`docs/usage.md` に「ルール別集計は `--verbose` 必須」と明記。体験改善効果は小さい。

**完了条件**

- 通常の `seiton` 実行で、診断があればファイル別・ルール別の両テーブルが出る。
- CLI 出力スナップショットテストを更新。

---

### §6 旧リンターインライン抑制の移行（B5）— 移行支援不足

#### 評価

**半分妥当。** seiton はネイティブのインライン抑制を既に持つ（`# seiton: disable-next-line`, `# seiton: disable-job` 等、`Seiton_Linter_spec.md`）。zizmor の `# zizmor: ignore[rule]` や ghalint `excludes` を **そのまま読む機能はない**。

フィードバックの「`.github/seiton.yaml` に集約（推奨）」は seiton の設計思想（`rules: enabled: false` より `exclusions` を優先）と一致。大規模リポ向けの **移行ツール**不足が本質。

#### 対応プラン

**フェーズ A（ドキュメント）**

1. `docs/configuration.md` または新規 `docs/migration.md` に対応表を追加:

   | 旧方式 | seiton |
   |--------|--------|
   | `# zizmor: ignore[rule]` | `exclusions` に集約（推奨）または `# seiton: disable-next-line rule` |
   | `.zizmor.yaml` 行単位 ignore | `exclusions` の `file` + `rules` |
   | ghalint `.ghalint.yaml` excludes | `exclusions` の `file` / `jobs` / `rules` |
   | actionlint（config なし） | デフォルトルール + 必要なら `exclusions` |

2. Skill の Migration 節に同表を要約で載せる。

**フェーズ B（将来・任意）**

- `seiton migrate --from zizmor|ghalint` で既存 YAML から `exclusions` ドラフトを生成。
- zizmor インラインコメントの静的変換は優先度低（集約設定が推奨のため）。

**完了条件**

- 移行ユーザーがインラインコメントを残すか config に寄せるかを判断できる。
- コード変更はフェーズ A では不要。

---

### §7 検出範囲の差異（I1）— 想定内、移行ガイドで吸収

#### 評価

**妥当。バグではない。** seiton は actionlint + zizmor + ghalint の和集合に近いが完全一致ではない。フィードバックの具体例はいずれも説明可能:

| 現象 | 評価 |
|------|------|
| `run-env-context-direct-use` が大量に新規検出 | seiton 固有ルール。旧リンター未カバー |
| `auto-dump-context.yaml` の `deny-inherit-secrets` | zizmor では `dangerous-triggers` のみ ignore。ghalint 相当は今回の exclusion 未設定 |
| `if-expr-wrapper` 等の warning | seiton デフォルト有効。zizmor medium 相当 |
| `impostor-commit` opt-in | 設計どおり。今回有効化済み |
| `ref-version-mismatch` | ローカルでも有効。zizmor 行 ignore は `exclusions` で代替 |

#### 対応プラン

1. [Seiton-feature-matrix.md](./Seiton-feature-matrix.md) に「移行時に増えやすいルール」列を追加（`run-env-context-direct-use`, `deny-inherit-secrets` 等）。
2. `docs/migration.md`（§6 と共通化可）に段階的移行パターンを記載:
   - **段階 1**: `--min-severity error` + 既存 exclusion 移植
   - **段階 2**: warning ルールを順次有効化
   - **段階 3**: online ルール（`impostor-commit` 等）を opt-in
3. ラボリポジトリ固有の CI 方針（デモファイルの除外 / 修正）は **githubactions-lab 側**で決定。seiton 本体のスコープ外。

**完了条件**

- 移行ユーザーが「error が増えた＝バグ」と誤解しにくい。
- feature matrix と migration ドキュメントが整合。

---

### §8 `validate-config` と設定ミス検出（B6）

#### 評価

**妥当。** `ValidateCommand` は `LintConfigLibrary.ValidateFile` のみ実行し、workflow ファイルを読まない。unknown job-id は lint 時に各 workflow へ設定エラーとして付与される設計のため、フィードバックの「`validate-config` では検出されなかった」は事実。

ただし B1 のバグがあると、lint 時のエラーも大量かつ誤解を招くため、**B1 修正が先決**。

#### 対応プラン

B1 フェーズ B と一体で実装（§2 参照）。加えて:

1. 設定診断（unknown rule-id, unknown job-id, 重複 exclusion）を `validate-config` の exit code 3 で一括検出できることを `docs/usage.md` に強調。
2. 設定エラーと workflow 診断を出力上で区別（config path / `error[config]` 等）— 実装コストと相談。

---

## 実装優先度

```
フェーズ 1（次マイナー / パッチ — バグ・混乱の解消）
├── B1: job-scoped exclusion の file 限定 job-id 検証
├── B2: rules: ["*"] サポート（または Skill 修正のみ）
└── B4: ルール別テーブルをデフォルト表示に

フェーズ 2（ドキュメント — 同リリースまたは直後）
├── B3: skip-agentic-workflows の説明・init テンプレ修正
├── B5 / I1: migration ガイド・feature matrix 更新
└── B6: validate-config の cross-file job-id 検証

フェーズ 3（将来）
├── B5: seiton migrate コマンド
└── B3 フェーズ B: gh-aw ヒューリスティック拡張（要設計）
```

---

## 検証手順（リリース前）

1. `.references/githubactions-lab` 相当の fixture を tests に追加（または sandbox）し、フィードバックの `seiton.yaml` で回帰テスト。
2. 手動: `seiton validate-config --verbose` → `seiton --verbose --include-actions` の順で、フィードバック §4 の期待出力（119 files, 3 suppressed 等）を確認。
3. job-scoped exclusion の再現ケースで diagnostic 件数が膨張しないこと。
4. `rules: ["*"]` と `rules` 省略の両方で `agentics-maintenance.yml` 相当が全除外されること。

---

## 参照

- [feedback_seiton.md](./feedback_seiton.md) — 元フィードバック全文
- [Seiton_config_spec.md](./Seiton_config_spec.md) — exclusions / discovery の仕様
- [Seiton-feature-matrix.md](./Seiton-feature-matrix.md) — 競合ツールとの機能比較
- [docs/configuration.md](../../docs/configuration.md) — ユーザー向け設定リファレンス
