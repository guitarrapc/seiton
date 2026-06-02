# github-actions format の --oneline / ::group:: 挙動調査

## 目的

- github-actions format で `--oneline` が未サポートという扱いが妥当か確認する
- github-actions format がファイル単位で `::group:: ... ::endgroup::` を出しているか確認する
- 修正要否を判断する

## 調査対象

- 実装
  - src/Seiton/Output/DiagnosticFormatter.cs
  - src/Seiton/Commands/CheckCommand.cs
  - src/Seiton/Commands/FixCommand.cs
- 仕様/ドキュメント
  - .github/docs/Seiton_CLI_spec.md (6.5)
  - docs/usage.md (Output Formats > GitHub Actions)
- 実行確認
  - `dotnet run --project src/Seiton -- --format github-actions --oneline <fixture>`
  - `GITHUB_ACTIONS=true` + `GITHUB_STEP_SUMMARY=<file>` での実行

## 事実確認

### 1. --oneline は実装上サポートされている

実装では `OutputFormat.GitHubActions` のときも `DiagnosticFormatter.WriteText(... oneline, color: false, ...)` を呼んでおり、oneline の真偽値はそのまま有効。

- 根拠: src/Seiton/Output/DiagnosticFormatter.cs

### 2. CLI 実行でも --oneline は拒否されない

以下を実行し、exit code は `1` (診断あり) で、`2` (Invalid Options) にはならないことを確認。

- 実行:
  - `dotnet run --project src/Seiton -- --format github-actions --oneline tests/Seiton.Core.Tests/fixtures/schema/actionlint/testdata/err/deprecated_workflow_commands.yaml`
- 観測:
  - 1 行 1 diagnostic の oneline 出力
  - 最後に summary
  - exit code `1`

よって「github-actions では --oneline 非対応で exit code 2」は、現在の実装挙動と一致しない。

### 3. github-actions で ::group:: は現在出力されない

`::group::` / `::endgroup::` を src 配下で検索しても実装コードには存在しない。

- 検索結果:
  - src 配下で実装コード上の `::group::` ヒットなし（Skill 文書内の planned 記述を除く）

`GITHUB_ACTIONS=true` + `GITHUB_STEP_SUMMARY` を設定した実行でも、stdout の diagnostic は rich text だが `::group::` は出力されなかった。

### 4. job summary は期待どおり動作

`GITHUB_STEP_SUMMARY` が writable な場合、summary はファイルに追記される。

- 根拠: src/Seiton/Commands/CheckCommand.cs (GitHubStepSummaryWriter.TryAppend)
- 実行確認: summary ファイルに `## Seiton` と集計テーブルが出力

## 仕様/ドキュメントとの整合性

### 不整合A: --oneline 非対応という記述

- .github/docs/Seiton_CLI_spec.md 6.5 に「`--oneline` は github-actions で非対応 (exit code 2)」とある
- docs/usage.md にも同趣旨の記述がある
- しかし実装/実行は `--oneline` を受け付ける

=> 仕様/ドキュメントと実装が不整合。

### 不整合B: ::group:: 期待

- 現在実装は per-file `::group::` を出さない
- これは現行実装の仕様側でも「現在は extra per-file wrapper なし」と読むことができる

=> 「group を期待する」運用方針に対しては、機能未実装。

## 修正要否判定

結論: 修正は必要。

- 最低限必要な修正:
  - `--oneline` 非対応という記述の是正（実装に合わせるか、実装を仕様に合わせて拒否するかを統一）
- 要件として group を期待するなら追加で必要:
  - github-actions 出力に per-file `::group::` / `::endgroup::` を実装

## 推奨方針

ユーザー意図（group で折りたたみ、oneline でも挙動は変えない）に合わせるなら以下を推奨。

1. 仕様を次に統一
   - github-actions では `--oneline` を許可
   - `::group::` は表示上のラップであり、diagnostic の意味/件数に影響しない
2. 実装を拡張
   - github-actions 出力時、file 単位で diagnostics をグルーピングして
     - `::group::<file>`
     - diagnostics
     - `::endgroup::`
   - file 未特定 (`<unknown>`) は単一グループまたは非グループ出力を明文化
3. テスト追加
   - `--format github-actions --oneline` が Invalid Options にならない回帰テスト
   - `::group::` / `::endgroup::` の出力順序と件数テスト
   - 複数ファイル時の deterministic 順序テスト
4. ドキュメント同期
   - .github/docs/Seiton_CLI_spec.md 6.5 更新
   - docs/usage.md の github-actions セクション更新

## 実装着手時の最小タスク案

- T1: 仕様更新（CLI spec / usage）
- T2: formatter もしくは出力層に file-group ラッパ追加
- T3: unit test / integration test 追加
- T4: 既存 CI テストの通過確認

## 補足

今回の調査範囲では、`--oneline` を github-actions で禁止する実装分岐は確認できなかった。違和感の主因は「oneline 非対応」ではなく「group 未実装」にある。
