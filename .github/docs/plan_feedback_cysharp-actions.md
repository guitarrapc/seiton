# Cysharp/Actions フィードバック対応計画

本書は [feedback_seiton_cysharp-actions.md](./feedback_seiton_cysharp-actions.md)（seiton 0.9.17 による `Cysharp/Actions` レビュー）を受け、各指摘の妥当性評価と対応方針を整理したものである。

## 背景

| 項目 | 値 |
|---|---|
| 対象リポジトリ | `.references/actions`（`Cysharp/Actions`） |
| seiton バージョン | 0.9.17 |
| 初回診断 | 79 diagnostics（36 errors, 43 warnings）/ 32 files |
| 設定調整後 | 30 errors / 10 files |

フィードバック全体として、ルール検出の精度（特に `expr-undefined-var`）は高く評価されている。一方、ネストされた参照リポジトリでの config 探索、機械可読出力、および `--fix` のログ信頼性に改善余地がある。

---

## 指摘一覧と妥当性評価

### 凡例

| 判定 | 意味 |
|---|---|
| **妥当** | 指摘は正しく、seiton 側の改善またはドキュメント整備が望ましい |
| **部分的に妥当** | 事象は正しいが、仕様上の意図や回避策もあり、対応は限定すべき |
| **妥当（対象側）** | 検出は正しいが、主な対応は対象リポジトリ側。seiton は現状維持でよい |
| **妥当（設定で解決済み）** | 指摘は理解できるが、既存 config 機能で十分。ドキュメント強化が主 |

---

## 1. 実行経過・設定調整

### 1.1 ネストされたリポジトリで親の `.github/seiton.yaml` を拾う

| 項目 | 内容 |
|---|---|
| 指摘 | `.references/actions` に移動しただけでは親リポジトリの config が使われる |
| 判定 | **妥当** |
| 根拠 | `CliConfigBridge.ResolveConfigPath` は CWD から親ディレクトリへ再帰的に `.github/seiton.yaml` を探索する実装である（[Seiton_config_spec.md](./Seiton_config_spec.md) の discovery 順序とも一致）。意図した設計だが、モノレポ内の参照リポジトリでは想定外に感じやすい |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P1 | `--verbose` 時に「どのディレクトリから config を発見したか」を stderr に出力する（例: `config: /path/to/parent/.github/seiton.yaml (discovered from /path/to/.references/actions, walked up 2 level(s))`） | 現状は config パスのみで、親採用の理由が分かりにくい |
| P1 | `docs/configuration.md` と skill の configuration リファレンスに「参照リポジトリ／サブディレクトリ実行時は `-c` 明示または対象側に config を置く」節を追加 | フィードバック通り `seiton init` + `-c` で解決できるが、初見では気づきにくい |
| P2 | config 探索の停止条件を検討（例: git リポジトリ root、または `--config-discovery=local` で CWD のみ） | 破壊的変更は受け入れるので実装 & ドキュメントとSkill更新 |

**対応しない（現時点）**

- デフォルトで親探索を止める — 単一リポジトリ root からサブディレクトリを lint する既存ユースケースを壊す

---

### 1.2 初回結果の `unpinned-uses` ノイズ（`Cysharp/Actions@main` 自己参照）

| 項目 | 内容 |
|---|---|
| 指摘 | 32 件の大半が自リポジトリ reusable workflow / action の `@main` 参照で、レビューノイズになる |
| 判定 | **妥当（設定で解決済み）** |
| 根拠 | 技術的には fixable だが、同一リポジトリ内の floating ref は運用上意図的なケースが多い。`rules.unpinned-uses.ignore-actions` の `owner: "Cysharp/*"` で 79 → 31 に整理できており、機能は期待通り |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P2 | `seiton init` テンプレートと `docs/configuration.md` に「同一 org の自己参照を ignore する」レシピを追加 | 診断メッセージ内 help でも触れているが、初回セットアップ時に見つけやすくする |
| P3 | 将来: `discovery` セクションに `ignore-self-references: true` のような opt-in を検討 | 自動推論は誤抑制リスクがあるため、明示 config が安全 |

**seiton 側のルール変更は不要**

---

### 1.3 `_test-*` workflow の除外

| 項目 | 内容 |
|---|---|
| 指摘 | 内部テストハーネス由来の `bot-conditions` / `deny-inherit-secrets` が支配的。`_test-*` 除外で見やすくなった |
| 判定 | **妥当（設定で解決済み）** |
| 根拠 | `exclusions` の glob は仕様通り動作。Agentic Workflow 向けには `# gh-aw-metadata:` ヘッダ検出と `discovery.skip-agentic-workflows` も別途存在するが、`_test-*` 命名とは無関係 |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P2 | init テンプレートの `exclusions` コメント例に `_test-*` や `generated.yml` パターンを追記 | フィードバックの「命名規則で機械判定しやすく」の実践例として |
| P3 | Agentic / 生成 workflow の除外ガイドを `.github/docs/Seiton_config_spec.md` の lessons learned に 1 段落追加 | `_test-*` は ad hoc だが有効なパターンであることを記録 |

**対応しない（現時点）**

- `_test-*` をデフォルト除外 — 一般リポジトリで `_test-build.yaml` 等を誤除外する可能性

---

### 1.4 `checkout-persist-credentials` on wrapper action

| 項目 | 内容 |
|---|---|
| 指摘 | `.github/actions/checkout/action.yaml` は `inputs.persist-credentials` を下流へ素通ししており、`false` 固定を求める warning は非実用的 |
| 判定 | **部分的に妥当** |
| 根拠 | ルールは `actions/checkout` 直使用で `persist-credentials: false` を推奨する設計（[Seiton_Linter_spec.md](./Seiton_Linter_spec.md) §4.4）。composite action 内で `${{ inputs.persist-credentials }}` を渡すケースは、式値のため auto-fix も無効（`CheckoutPersistCredentialsRule` の意図通り）。caller 側で false を渡す運用もありうるため、一律抑制は過剰 |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| — | 現状は rule 単位 exclusion で十分（フィードバックの最終 config と同じ） | 即時のコード変更は不要 |
| P3 | ルール改善案: `persist-credentials` が `${{ inputs.* }}` / `${{ github.event.inputs.* }}` 等の passthrough 式のみのとき severity を下げるか抑制 | composite wrapper の典型パターン。実装前に equivalence-class テスト必須 |
| P2 | `docs/rules.md` の `checkout-persist-credentials` Notes に wrapper action 向け exclusion 例を追記 | 同様ケースのユーザーが config で逃げられるように |

---

### 1.5 最終的に残した 30 件の診断

| 項目 | 内容 |
|---|---|
| 指摘 | `run-inputs-context-direct-use` / `run-env-context-direct-use` / `expr-undefined-var` がレビュー価値が高い。特に `create-release.yaml` の `inputs.nuget-path` 未定義は実バグ |
| 判定 | **妥当（対象側）** |
| 根拠 | 各ルールは仕様どおり動作。`expr-undefined-var` は `workflow_dispatch` inputs に存在しないキー参照を正しく検出 |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| — | seiton 側の変更なし | 検出品質は期待を上回っている |
| — | （参考）Cysharp/Actions 側で `nuget-path` input 追加または参照削除、`run:` 内 context を step `env` へ移行 | 対象リポジトリの修正事項 |

---

## 2. `--fix` の使い勝手

### 2.1 dry-run / 実 fix で diff が空なのに `fixed 1 file(s)` と表示される

| 項目 | 内容 |
|---|---|
| 指摘 | `_post-release.yaml` の自己参照 `unpinned-uses` を `--fix` したが diff なし。verbose では `fixed 1 file(s)` |
| 判定 | **妥当（バグ／UX 欠陥）** |
| 根拠 | `FixCommand` 末尾の `WriteTotalTiming(..., verb: "fixed")` は **処理したファイル数**（`resolvedFiles.Length`）を「fixed」と表示しており、**実際に内容が変わったファイル数**ではない（`CheckCommand.WriteTotalTiming` の実装）。fix summary（`WriteFixSummary`）は `appliedFixes > 0` のときのみ出るため、verbose の total 行だけが誤解を招く |

**再現条件（推定）**

1. 対象ファイル 1 件に `--fix` を実行
2. fixable diagnostic が無い、または pin / local fix 適用後も YAML バイト列が同一
3. `-v` 以上で `verbose: total: 1 file(s) fixed in ... ms` が出力される

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P0 | `WriteTotalTiming` の fix モード verb を `processed` に変更するか、**実際に変更があったファイル数**を渡す | フィードバック 3 提案の (2) に相当。最小修正で信頼性が大きく改善 |
| P1 | `fixedFiles` 登録条件を「fix 試行数 > 0」から「出力 YAML が入力と byte 不等」に統一 | 提案 (3)「書き換え不要だった理由」の一部。dry-run / applied 両方 |
| P1 | 変更ゼロで fixable が残る場合、stderr に理由を 1 行出力（例: `no changes written: pin resolution failed` / `all fixable issues filtered by config`） | 提案 (3)。network pin 失敗時に特に有用 |
| P2 | `--dry-run` で変更がある場合は必ず unified diff を stdout に出す（現状もそうだが、変更判定バグ修正とセットで回帰テスト） | 提案 (1) |

**テスト**

- `tests/Seiton.Tests/` に FixCommand の verbose total 行と fix summary 整合の CLI テストを追加（test-first-development skill に従う）
- フィクスチャ: fixable だが適用後 YAML 同一、fixable なし 1 file、pin 成功で diff あり

---

## 3. CLI / ログ

### 3.1 良かった点（help、診断メッセージ、verbose）

| 項目 | 内容 |
|---|---|
| 指摘 | `--help` が簡潔、診断に config 例が含まれる、verbose が調査に有用 |
| 判定 | **妥当（肯定的）** |

**対応方針**: 維持。回 regress 防止のため既存 CLI golden テストを継続。

---

### 3.2 `--format json` が純粋な JSON ではない

| 項目 | 内容 |
|---|---|
| 指摘 | 1 行目 JSON 配列、2 行目 `36 errors, 43 warnings in 32 files` で `ConvertFrom-Json` できない |
| 判定 | **部分的に妥当** |
| 根拠 | [Seiton_CLI_spec.md](./Seiton_CLI_spec.md) §6.2–§6.4 では **stdout = 診断 JSON、stderr = summary 行** が仕様。PowerShell 等で stdout/stderr を同一ストリームにリダイレクトすると混在する。stdout 単体は有効な JSON 配列 |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P1 | `docs/` と `--help` に「JSON 利用時は stdout のみをパイプする（summary は stderr）」を明記。PowerShell 例: `$d = seiton --format json 2>$null \| ConvertFrom-Json` | 仕様を変えずにフィードバックの pain を解消 |
| P2 | 機械処理向けに summary を JSON に含める拡張を検討。例: トップレベル `{ "diagnostics": [...], "summary": { "errors": 36, ... } }`（破壊的変更のため opt-in `--format json-v2` または major で） | フィードバック要望の根本対応 |
| P3 | `--quiet` / `--no-summary` で stderr summary を抑制 | CI パイプ向け。SARIF 利用者にも有益 |

**対応しない（現時点）**

- 0.9.x パッチで JSON 出力形式を破壊的変更 — 既存ツール連携リスク

---

### 3.3 config 調整導線が弱い

| 項目 | 内容 |
|---|---|
| 指摘 | `config` サブコマンドがなく、`init` / `validate-config` / 診断 help を繋いで理解する必要がある |
| 判定 | **部分的に妥当** |
| 根拠 | `seiton init` と `seiton validate-config` は存在する。スキーマ全体を対話的に探索する UI は無い |

**対応方針**

| 優先度 | 対応 | 理由 |
|:---:|---|---|
| P2 | `seiton init --help` と README に「設定探索フロー」（init → validate-config → 本番 lint）を 3 ステップで記載 | 低コスト |
| P3 | `seiton config schema` または `seiton config explain <key>` で [Seiton_config_spec.md](./Seiton_config_spec.md) の要約を表示 | サブコマンド追加は scope 大。需要を見て判断 |
| P3 | Playground または skill に Cysharp/Actions 相当の config サンプルを載せる | 本フィードバックの final config がそのままテンプレートになる |

---

## 4. 実装フェーズ（推奨順）

### フェーズ A — ログ信頼性（P0–P1）

**WHY**: フィードバックで最も severity が高い。`--fix` の結果をログだけで判断できない状態はツール信頼性に直結する。

1. FixCommand verbose total 行の wording / カウント修正
2. 変更ゼロ時の理由メッセージ（fixable 残存・network 失敗・config フィルタ）
3. CLI 回帰テスト追加
4. `Seiton_CLI_spec.md` §6.5 fix verbose 節を実装に合わせ更新

**完了条件**: `_post-release.yaml` 相当で「処理 1 ファイル・変更 0」のとき verbose が `0 file(s) modified`（または同等）を示し、fix summary と矛盾しない。

#### フェーズ A 実装結果（2026-06-01）

**実装内容**

| 項目 | 変更 |
|---|---|
| verbose total 行 | `WriteFixTotalTiming` を追加。`N file(s) processed, M modified in X ms` 形式に変更（`M` は YAML バイト列が変わったファイル数） |
| fix summary 登録 | `fixedFiles` は内容変更があったファイルのみ登録（`SequenceEqual` ベース） |
| ファイル書き込み | 内容が同一のとき `File.WriteAllBytes` をスキップ（mtime 更新も防止） |
| 変更ゼロ hint | `WriteNoFilesModifiedHint` を追加。fix 試行あり・変更 0 のとき stderr に理由行を出力 |
| 仕様 | `Seiton_CLI_spec.md` §6 verbose / fix summary 節を更新 |

**レビュー指摘と対応**

| 指摘 | 対応 |
|---|---|
| `WriteTotalTiming(..., "fixed")` が処理ファイル数を「fixed」と誤表示 | `WriteFixTotalTiming` で processed / modified を分離 |
| fix summary と diff の不一致 | 内容変更ベースの登録に統一 |
| 変更なしでも disk write される | byte 比較後に write をスキップ |
| fixable 残存時の理由が不明 | hint 行で fix 試行数と fixable 残数を明示 |
| テスト不足 | `VerboseTimingTests` / `FixCommandTests` に 9 件追加 |

**ベンチマーク（FixApplyBenchmark — fix 適用ループ、CLI 変更の間接指標）**

| Scenario | Before Mean | After Mean | Δ | Before Alloc | After Alloc | Δ |
|---|---:|---:|---:|---:|---:|---:|
| NoConflict | 23.61 us | 20.84 us | −12% | 10.57 KB | 10.57 KB | 0% |
| SingleJobConflict | 40.48 us | 36.15 us | −11% | 16.95 KB | 16.95 KB | 0% |
| MultiJobConflict | 115.32 us | 101.03 us | −12% | 39.72 KB | 39.72 KB | 0% |

- **解釈**: 今回の変更は `FixCommand` の CLI 層（ログ・I/O ガード）のみで、`FixEngine` / lint ループは未変更。ベンチマーク差分は計測誤差範囲と判断。
- **実際の性能効果**: 内容変更なしの `--fix` 実行時に `File.WriteAllBytes` を省略するため、該当ケースでは disk I/O と mtime 更新を回避（Cysharp フィードバックの「diff 空なのに fixed 表示」シナリオに該当）。

**テスト**: `Seiton.Tests` 213 passed、`Seiton.Core.Tests` 1815 passed。

---

### フェーズ B — ドキュメント・発見性（P1–P2）

**WHY**: フィードバックの大半は config で整理できており、機能不足より「気づきにくさ」が問題。

1. ネスト repo / `-c` 明示のガイド
2. JSON stdout/stderr 分離と PowerShell 例
3. `ignore-actions` / `_test-*` exclusion / wrapper checkout の config レシピ
4. config discovery の verbose 改善（フェーズ A と並行可）

**完了条件**: 新規ユーザーが feedback 記載の調整を、フィードバックなしで docs だけから再現できる。

#### フェーズ B 実装結果（2026-06-01）

**実装内容**

| 項目 | 変更 |
|---|---|
| config verbose 改善 | `ConfigPathResolution` を追加。`ResolveConfigPath` が discovery 起点・親 walk 段数・ソース（`--config` / `SEITON_CONFIG` / discovery）を返す。`FormatVerboseMessage()` で stderr 行を生成 |
| ネスト repo ガイド | `docs/configuration.md` §Nested repositories、`docs/usage.md` init 節、README Quick Start |
| JSON stdout/stderr | `docs/usage.md` §JSON に Bash / PowerShell 例、`Seiton_CLI_spec.md` §6.2 に stderr 分離を明記 |
| config レシピ | `docs/configuration.md` §Common configuration recipes（`ignore-actions` / `_test-*` / wrapper checkout） |
| config 導線 | README / `seiton init`・`validate-config` help 文、Skills `configuration.md` / `SKILL.md` に 3 ステップフロー |
| テスト | `CliConfigBridgeTests` 7 件（discovery メタデータ・verbose 文言） |
| 仕様 | `Seiton_CLI_spec.md` §4.2 verbose config 行、`Seiton_CLI_csharp_spec.md` §5 API 更新 |

**レビュー指摘と対応**

| 指摘 | 対応 |
|---|---|
| CWD 変更テストが並列実行で他テストと干渉 | `DiscoverConfigPath(start, discoveryBoundary)` internal テスト seam に変更（本番は boundary なし） |
| `Path` プロパティが `System.IO.Path` とシャドウ | `FormatVerboseMessage` 内で `System.IO.Path.GetFullPath` を明示 |
| usage.md JSON 節の重複コマンド例 | 冗長な `seiton --format json` ブロックを削除 |
| PowerShell 例が複雑 | `2>$null \| ConvertFrom-Json` の最小例に整理 |

**ベンチマーク（FixApplyBenchmark — lint/fix コア、CLI 変更の間接指標）**

| Scenario | Phase A After | Phase B After | Δ Mean | Alloc |
|---|---:|---:|---:|---:|
| NoConflict | 20.84 us | 23.94 us | +15%* | 10.57 KB (0%) |
| SingleJobConflict | 36.15 us | 40.40 us | +12%* | 16.95 KB (0%) |
| MultiJobConflict | 101.03 us | 112.59 us | +11%* | 39.72 KB (0%) |

\* Phase B は `CliConfigBridge` / verbose 文字列化のみで `FixEngine` 未変更。FixApplyBenchmark 差分は計測誤差・環境ノイズと判断（Allocated 不変）。verbose 無効時の config 解決は従来と同じ FS walk で、追加コストは `levelsWalked` カウンタのみ。

**テスト**: `Seiton.Tests` 220 passed、`Seiton.Core.Tests` 1815 passed。

---

### フェーズ C — ルール・フォーマット拡張（P3）

**WHY**: 品質向上だが、Cysharp/Actions フィードバックの blocking ではない。

1. `checkout-persist-credentials` の passthrough 式検出（optional suppression）
2. JSON envelope 形式（opt-in）
3. config discovery 停止オプション
4. `ignore-self-references` 等の discovery 拡張検討

---

## 5. フィードバック最終 config（参考）

本レビューで有効だった設定。ドキュメントレシピの seed として再利用可能。

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

---

## 6. まとめ

| カテゴリ | 評価 | seiton 側の主対応 |
|---|---|---|
| ルール検出精度 | 高い（30 件は妥当、undefined input は特に有用） | 変更なし |
| config 機能 | 十分（79 → 30 に整理可能） | ドキュメント・verbose 改善 |
| ネスト repo config | UX 問題 | verbose + docs（探索停止は P3） |
| `--format json` | 仕様通りだが PS 等では混在 | docs + 将来 JSON envelope |
| `--fix` ログ | **バグ** | P0 で total 行修正、理由メッセージ追加 |
| config 導線 | やや弱い | docs 強化、将来 subcommand 検討 |

Cysharp/Actions フィードバックは、seiton の lint コアの妥当性を裏付ける一方で、**CLI の fix 結果表示**と**参照リポジトリ運用時の config 発見**に集中した改善タスクを示している。フェーズ A と B を優先すれば、同種の外部レビューでの摩擦は大きく減る見込みである。
