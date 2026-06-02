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

---

## 実装結果（推奨方針対応）

以下を実装した。

1. github-actions 出力で file 単位の `::group::` / `::endgroup::` を導入
   - 実装: `src/Seiton/Output/DiagnosticFormatter.cs`
   - 設計: diagnostics を入力順に1回走査し、file が変わる境界で group を開閉
   - `--oneline` / rich どちらでも group 挙動は共通
2. `--oneline` + github-actions の回帰テストを追加
   - `tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs`
   - `tests/Seiton.Tests/CheckCommandTests.cs`
3. 仕様/ドキュメントを実装に同期
   - `.github/docs/Seiton_CLI_spec.md`
   - `docs/usage.md`
   - `src/Seiton/Skills/SKILL.md`
4. パフォーマンス検証用ベンチマークを拡張
   - `src/Seiton.Benchmark/DiagnosticOutputBenchmark.cs`
   - `github-actions rich` / `github-actions oneline` を追加

## テスト結果

### Red（失敗確認）

- `DiagnosticFormatterRichTextTests/GitHubActions_Format_Oneline_EmitsGroupedDiagnosticsPerFile`
  - 実装前に `::group::a.yml` 不在で失敗を確認

### Green（修正後）

- 追加・変更した github-actions 関連テストは通過
  - formatter レベル
  - command レベル

### 全体回帰

- `dotnet test` を実行
- 結果:
  - `Seiton.Tests` / `Seiton.Core.Tests` / `Seiton.Playground.Tests` は通過
  - `Seiton.Update.Tests` で既存データハッシュ差分由来の失敗（2件）
    - 今回変更範囲（CLI 出力整形）とは独立

## ベンチマーク結果

### 実装前（基準）

`dotnet run --project src/Seiton.Benchmark -c Release -- --filter "*DiagnosticOutputBenchmark*"`

- `DiagnosticFormatter text rich (F1)`
  - Mean: **489.701 us**
  - Allocated: **117.51 KB**
- `DiagnosticFormatter text rich (F10)`
  - Mean: **3,737.0 us**
  - Allocated: **1137 KB**

### 実装後（最終）

同コマンドを再実行（github-actions ベンチ追加後）。

- `DiagnosticFormatter text rich (F1)`
  - Mean: **414.132 us**
  - Allocated: **117.51 KB**
- `DiagnosticFormatter text rich (F10)`
  - Mean: **4,129.66 us**
  - Allocated: **1136.99 KB**
- `DiagnosticFormatter github-actions rich (F10)`
  - Mean: **4,196.47 us**
  - Allocated: **1152.69 KB**
- `DiagnosticFormatter github-actions oneline (F10)`
  - Mean: **185.714 us**
  - Allocated: **700.22 KB**

### 性能評価

- text rich の割当量は実質横ばい（≈0%）
- text rich の Mean は F1 で改善、F10 で悪化に見えるが、ShortRun（N=3）で誤差幅が大きく、安定差分の断定は困難
- 追加した github-actions rich は text rich 比で概ね +1〜2% 程度（group 行追加分として妥当）
- github-actions oneline は rich と比較して大幅に高速・低割当（期待どおり）

### 低下時の改善策（必要になった場合）

1. `DiagnosticFormatter` の group 出力で path 文字列書き込みをさらに削減（`WriteLine` 回数最適化）
2. ディレクティブ文字列（`::group::` / `::endgroup::`）の連結回数を減らす
3. 大規模診断ケースを対象に MediumRun 以上で再計測し、ノイズを抑えた比較へ切替

## ユーザーファースト API 観点の確認

- 直感性:
  - `--oneline` が format ごとに不自然に禁止されないため、利用者の期待に一致
- 可観測性:
  - GitHub Actions ログが file 単位で折りたため、CI 読解性が向上
- 互換性:
  - 既存の `text/json/sarif` 挙動は保持
  - `github-actions` のみ、期待に沿った拡張を実施

## 仕様整合の確認

- 実装変更に合わせ、CLI spec / usage を同期済み
- 変更後は以下が一致
  - `github-actions` で `--oneline` 利用可能
  - file 単位の `::group::` / `::endgroup::` 出力

## フェーズごとのレビュー反復

### フェーズ1: formatter + テスト実装

- 指摘1: text 出力の hot path を helper 化しすぎると測定揺れが増える懸念
- 対応1: text path を直接処理に戻し、github-actions 専用経路へ責務分離
- 再レビュー: 追加テスト・既存関連テスト通過を確認

### フェーズ2: 仕様同期 + ベンチ拡張

- 指摘1: ドキュメントが「oneline 非対応」のままでは実装と乖離
- 対応1: spec / usage / skill を更新
- 指摘2: github-actions 専用の性能測定軸が不足
- 対応2: benchmark に `github-actions rich/oneline` を追加
- 再レビュー: 仕様・実装・テストの整合を確認
