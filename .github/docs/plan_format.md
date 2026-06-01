# `github-actions` 出力形式 — 仕様・実装計画

本書は GitHub Actions 向けの CLI 出力形式 `github-actions` の **WHAT / WHY** と、実装・検証の進め方を定義する。実装の詳細 HOW は `.github/docs/Seiton_CLI_spec.md` §6.5 および `Seiton_CLI_csharp_spec.md` に反映する。

## 背景と動機

### 課題

現状のデフォルト `text` 出力は、複数ワークフローを一度に lint するとログが縦に長くなり、どのファイルの指摘かを追いにくい。CI では次のニーズがある。

1. **ジョブログ** — ファイル単位で折りたたみ可能にし、失敗時に該当ファイルだけ開けるようにする。
2. **ジョブサマリー** — PR / ワークフロー実行画面の「Summary」タブに、件数とファイル別内訳を Markdown で見せたい。

GitHub Actions はそれぞれ次の公式メカニズムを提供する。

| 機構 | 用途 |
|---|---|
| [`::group::` / `::endgroup::`](https://docs.github.com/en/actions/using-workflows/workflow-commands-for-github-actions#grouping-log-lines) | ログ行の折りたたみグループ |
| [`GITHUB_STEP_SUMMARY`](https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does#adding-a-job-summary) | ステップサマリー用 Markdown ファイルへの追記 |

### 方針

- 新しい出力形式 **`github-actions`** を `--format` に追加する。
- **GitHub Actions ランナー上ではデフォルト**とする（`GITHUB_ACTIONS=true`）。他の CI（GitLab 等）では従来どおり `text` を維持し、既存パイプラインを壊さない。
- ジョブログには `::group::` で **診断があるファイルごと** に rich text 診断を出力する。
- サマリー（件数・ファイル別表・`-v` 時のルール別表）は **`GITHUB_STEP_SUMMARY` が設定されていればそこへ Markdown 追記**し、未設定時は従来どおり **stderr** に出す。

### 非ゴール

- SARIF / Code Scanning の代替（SARIF は引き続き別形式）。
- `rules` サブコマンドでの `github-actions` 対応。
- ワークフロー YAML 内に `echo "::group::"` を自動挿入すること（CLI がログに書くだけ）。

---

## 仕様（WHAT）

### 形式名と解決順

| 項目 | 値 |
|---|---|
| CLI 値 | `github-actions` |
| 環境変数 | `SEITON_FORMAT=github-actions` |
| フラグ | `--format github-actions` |

**デフォルト解決**（`check` / ルートの lint・fix）:

1. `--format` が明示的に `text` 以外 → その形式を使用（`SEITON_FORMAT` はフラグ未指定時のみ）。
2. フラグが `text`（デフォルト）かつ `GITHUB_ACTIONS` が非空 → **`github-actions`**。
3. それ以外 → **`text`**。

> **WHY `GITHUB_ACTIONS` か**: `CI` だけでは GitLab / CircleCI 等でも true になり、`GITHUB_STEP_SUMMARY` のない環境でサマリー先が変わる副作用を避けるため。

`json` / `sarif` を明示した場合は上記の自動切り替えを行わない。

### 対象コマンド

| コマンド | `github-actions` |
|---|---|
| ルート lint / `check` | ✅ |
| `--fix`（残診断・fix サマリー含む） | ✅ |
| `rules` | ❌（exit `2`、SARIF と同様） |
| `validate-config` | 変更なし（常に text 風メッセージを stderr） |
| `init` / `version` / `install` | 対象外 |

### ストリーム分担

| 出力 | 行き先 |
|---|---|
| 診断（ファイル別グループ内） | **stdout** |
| `::group::` / `::endgroup::` 行 | **stdout**（診断と同じストリーム） |
| サマリー Markdown | **`GITHUB_STEP_SUMMARY` ファイルへ追記**（パスが env にあり、ファイルが開ける場合） |
| サマリー（フォールバック） | **stderr**（step summary 未使用時） |
| `--verbose` / config エラー / init ヒント / fix 用 diff（非 text 時） | **stderr**（現行と同様） |

`GITHUB_STEP_SUMMARY` への書き込みは **追記（append）** とする。既に他ステップが Summary を書いている場合、先頭に空行 1 行を挟んでから Seiton のブロックを追加する。

### ジョブログ（stdout）— ワークフローコマンド

診断を **ファイルパスごと** にグループ化する。診断が 1 件もないファイルにはグループを出さない（空グループでログを汚さない）。

```
::group::<file-path>
<rich text diagnostic blocks for this file only>
::endgroup::
```

- `<file-path>` は診断の `file` フィールド（stdin の場合は `--stdin-filename`）。
- グループの **出現順** は、診断リスト内でそのファイルが **初めて現れる順**（lint エンジンの出力順を維持）。
- グループ内の 1 診断あたりの本文は **`text` 形式の rich 出力（§6.1.1）と同一**（スニペット・help 行を含む）。
- **色は出さない**（`github-actions` では常に `--color=never` 相当。CI 自動検出と重複するが明示的に無効化）。
- **`--oneline` は非対応**。指定時は exit `2`（「`oneline` は `text` 形式のみ」）。

グループとグループの間に余分な空行は挟まない（`::endgroup::` の直後に次の `::group::` が続いてよい）。

**診断ゼロ件**のとき: グループ行は出さず、サマリーのみ（step summary または stderr）。

#### 例（ジョブログ抜粋）

```
::group::.github/workflows/ci.yml
error[template-injection]: ...
  --> .github/workflows/ci.yml:7:32
     |
   7 |       - run: ...
     |              ^^^
     |
::endgroup::
::group::.github/workflows/release.yml
warning[unpinned-uses]: ...
  --> .github/workflows/release.yml:12:11
     |
::endgroup::
```

### ステップサマリー（`GITHUB_STEP_SUMMARY`）

環境変数 `GITHUB_STEP_SUMMARY` が非空で、指定パスに追記できる場合:

1. 先頭に区切り用の空行（ファイルが既に存在しサイズ > 0 のときのみ）。
2. 見出し: `## Seiton`
3. 本文: 現行 §6.4 と同等の内容を **GitHub Flavored Markdown** として出力する。
   - 1 行サマリー（`N errors, M warnings in K file(s)` 等、fix モードの remain 文言含む）
   - ファイル別表（`| File | Errors | Warnings |` …）
   - `-v` 時: ルール別表
   - fix 適用時: fix サマリー表（§6.4 の fix ブロック）
4. `hint:` 行はサマリーには含めない（ログの stderr にのみ残す）。

表の列・ソート・数値の意味は **§6.4 と同一**（実装は `WriteSummary` の Markdown 化を共有する）。

**未設定・書き込み不可**（ローカル、Docker のみ、他 CI）: サマリーは **stderr のみ**（現行動作）。exit code や診断内容は変えない。

### 他形式との関係

| 形式 | 用途 |
|---|---|
| `text` | ローカル開発・汎用 CI |
| `json` / `sarif` | 機械可読・Code Scanning |
| `github-actions` | GHA ジョブログ + Summary の人間可読表示 |

`--format github-actions` でも **exit code は現行と同一**（§7）。

### 互換性

- 既存の `CI=true` による色無効・verbose 抑制（§3.1）は維持。
- `GITHUB_ACTIONS` 未設定の環境で `--format` 未指定 → 従来どおり `text`（**破壊的変更なし**）。
- GHA 上で明示的に `--format text` または `SEITON_FORMAT=text` とすれば従来のフラットログに戻せる。

---

## 実装フェーズ

test-first-development（`.claude/skills/test-first-development/SKILL.md`）に従う。各フェーズ完了時に **同一コマンドでテストを再実行**し、リグレッションがないことを確認する。

### フェーズ 0 — ベースライン ✅ 完了

**実施日**: 2026-06-02  
**コミット**: （フェーズ 0 コミット後に SHA を追記）

#### 実施内容

1. `DiagnosticOutputBenchmark` を追加（`github-actions` 実装前後の formatter 比較用。`Seiton.Benchmark` → `Seiton` 参照を追加）。
2. テスト・ベンチマークを実行し、数値を記録。

```shell
dotnet test
dotnet test --project tests/Seiton.Tests
cd src/Seiton.Benchmark && dotnet run -c Release -- -f "*CoreLint*" "*CoreParsing*" "*DiagnosticOutput*"
```

レポート（ローカル、gitignore 対象）: `src/Seiton.Benchmark/BenchmarkDotNet.Artifacts/results/*-report-default.md`  
環境: Windows 11, AMD Ryzen 9 7950X3D, .NET 10.0.8, BenchmarkDotNet ShortRun（`CI` 未設定時）。

#### テスト結果

| プロジェクト | 結果 | 件数 |
|---|---|---|
| `Seiton.Core.Tests` | Passed | （ソリューション合算に含む） |
| `Seiton.Tests` | Passed | 243 / 243 |
| `Seiton.Update.Tests` | Passed | （ソリューション合算に含む） |
| `Seiton.Playground.Tests` | **Failed 7** | 2316 succeeded, 7 failed（本フェーズの変更とは無関係。Playground UI テスト。フェーズ 1 以降も CLI 変更のリグレッション判定は `Seiton.Tests` + `Seiton.Core.Tests` を主とする） |
| **合計** | 2323 tests, 7 failed |  |

#### ベンチマーク基準値（Mean / Allocated）

**CoreLintBenchmark** — parse + lint（変更監視用）

| Size | FixEnabled | Mean | Allocated |
|---|---|---:|---:|
| Small | false | 63.25 μs | 8.7 KB |
| Small | true | 72.58 μs | 10.16 KB |
| Medium | false | 1,388.97 μs | 68.9 KB |
| Medium | true | 2,008.84 μs | 82.26 KB |
| Large | false | 21,075.57 μs | 327.41 KB |
| Large | true | 31,627.99 μs | 382.26 KB |

**CoreParsingBenchmark** — parser（変更監視用）

| Size | Method | Mean | Allocated |
|---|---|---:|---:|
| Small | WorkflowParser.Parse | 46.676 μs | 3.88 KB |
| Medium | WorkflowParser.Parse | 1,123.578 μs | 35.59 KB |
| Large | WorkflowParser.Parse | 18,368.841 μs | 180.05 KB |

**DiagnosticOutputBenchmark** — `DiagnosticFormatter` text rich（**本機能の主比較対象**）

| Count | ファイル数 | Mean | Allocated | 備考 |
|---|---|---:|---:|---|
| F1 | 1 | 225.9 μs | 117.5 KB | Medium ワークフロー 1 件 lint 後の全診断をフォーマット |
| F10 | 10 | 2,237.1 μs | 1136.94 KB | 同上 × 10 |

フェーズ 2–3 完了時は上記 **DiagnosticOutputBenchmark** を必ず再実行し、Mean / Allocated が **+10% 以内**であること。CoreLint / CoreParsing は formatter-only 変更なら変化なしが期待。

#### フェーズ 0 レビュー

- **API**: 本フェーズはベンチ追加のみ。ユーザー向け CLI 変更なし。
- **仕様**: `plan_format.md` / `Seiton_CLI_spec.md` との差分なし。
- **性能**: ベンチ追加による Core パスへの影響なし（別プロセス・別ベンチクラス）。

### フェーズ 1 — 形式解決（Red → Green）

**テスト（先に失敗させる）**:

- `ResolveOutputFormat`: `GITHUB_ACTIONS=true` + フラグ default → `GitHubActions`
- `GITHUB_ACTIONS` 未設定 → `Text`
- フラグ `json` → `Json`（自動切り替えしない）
- `SEITON_FORMAT=github-actions` → `GitHubActions`

**実装**:

- `OutputFormat` に `GitHubActions` を追加
- `CliConfigBridge.ResolveOutputFormat` を拡張
- `Program` / Cocona の `--format` ヘルプ文字列更新

### フェーズ 2 — グループ付き診断出力（Red → Green）

**テスト**:

- `DiagnosticFormatter` / 専用 writer: 2 ファイル・複数診断で `::group::` / `::endgroup::` と rich 本文
- 診断なしファイルはグループなし
- stdin ファイル名がグループタイトルになる
- `--oneline` + `github-actions` → exit `2`

**実装**:

- `DiagnosticFormatter` または `GitHubActionsDiagnosticFormatter` でファイル単位グループ化
- `CheckCommand` / `FixCommand` の stdout 分岐

### フェーズ 3 — Step Summary（Red → Green）

**テスト**（`GITHUB_STEP_SUMMARY` を temp ファイルに向ける）:

- 追記内容に `## Seiton` と件数行・表が含まれる
- 既存内容があるファイルでは先頭に空行が入る
- env 未設定時は stderr のみ（既存 `WriteSummaryTests` を拡張）

**実装**:

- `WriteSummary` の Markdown 生成を共通化し、stderr / step summary の両方から利用
- step summary 書き込みは UTF-8、改行は `\n`（GHA 推奨）

### フェーズ 4 — 仕様書・ユーザードキュメント

- `.github/docs/Seiton_CLI_spec.md` §6.5、§3.1、フラグ表、§8 例
- `Seiton_CLI_csharp_spec.md` / `Seiton_CLI_go_spec.md` の列挙・解決順
- `docs/usage.md`、`docs/index.md`、`README.md`
- `.claude/skills/seiton/SKILL.md`（存在すれば）

### フェーズ 5 — CI テンプレート（任意・後続）

`seiton install --ci` の埋め込みワークフロー例に、SARIF 例に加え **デフォルトで `github-actions` が効く** シンプル例をコメントまたは別 job で記載（実装タイミングは別 PR 可）。

---

## 検証チェックリスト

各フェーズ後:

```shell
dotnet test --project tests/Seiton.Tests --treenode-filter /*/*/WriteSummary*
dotnet test --project tests/Seiton.Tests --treenode-filter /*/*/DiagnosticFormatter*
dotnet test
```

実装完了後:

| 観点 | 基準 |
|---|---|
| テスト | 全 `dotnet test` 成功 |
| ベンチ | Core parse/lint の Mean / Allocated がベースライン比 **+10% 以内**（変更が formatter のみなら変化なしが期待） |
| API | `--format github-actions` が明示的で、GHA では省略可能 |
| 仕様一致 | `Seiton_CLI_spec.md` §6.5 と実装の差分なし |

---

## レビュー観点（ユーザーファースト）

- GHA 上で **フラグなし** `seiton` だけでログが折りたたまれ、Summary に表が出るか。
- ローカルでは従来の `text` のままか。
- `docker run ... seiton` は `GITHUB_ACTIONS` が無い限り `text` のままか（SARIF 例との共存）。
- Summary 追記が他ツールの Summary を **上書きしない**（append のみ）か。

---

## 関連ドキュメント

| ドキュメント | 更新内容 |
|---|---|
| `Seiton_CLI_spec.md` | §6.5 追加、§3.1 デフォルト形式、フラグ・env・例 |
| `Seiton_CLI_csharp_spec.md` | `OutputFormat`, formatter, `ResolveOutputFormat` |
| `Seiton_CLI_go_spec.md` | 同上（Go 向け） |
| `docs/usage.md` | 形式説明・CI 例 |
| `docs/index.md` / `README.md` | 機能一覧 |

---

## 実装後記録（テンプレート）

各フェーズ完了時に以下を追記する。

### フェーズ 0

- **性能**: 上記ベースラインを確立。`DiagnosticOutputBenchmark` を新設（従来 formatter 専用ベンチなし）。CoreLint / CoreParsing は lint パス監視用。変化なし（新規計測のみ）。
- **テスト**: 新規テストなし。`Seiton.Tests` 243 件パス。Playground 7 失敗は既知・別系統。
- **仕様差分**: なし。

### フェーズ 1 以降（テンプレート）

- **性能**: ベースライン比較結果（Mean / Allocated）。変化があれば理由と対策。
- **テスト**: 追加したテストクラス・メソッド一覧。
- **仕様差分**: プランと異なった判断があれば §6.5 と lessons learned に反映。
