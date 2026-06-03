# setup-seiton `@v1` が pinning されない件の調査・実装計画

## 目的

`uses: guitarrapc/setup-seiton@v1` が seiton で pinning されない一方、pinact では `v1.0.1`（現在は `v1.0.2` に変わり得る）へ解決される差分を整理し、優先度付きの対応計画を定める。

## 事実確認

- `setup-seiton` の現状:
  - `v1` は **branch**（`origin/v1 -> 0f877ad...`）。
  - `v1.0.1`, `v1.0.2` は **tag**。
  - 直近タグのコミット時刻は当日（`v1.0.0`, `v1.0.1`, `v1.0.2` すべて同日）。
- pinact 側:
  - `processUnpinnedVersion()` で非 semver ref（branch など）を通常は `ErrCantPinned` にする。
  - ただし `--branch-to-tag` に一致した場合、`convertBranchToLatestTag()` で「最新 stable tag」を選び SHA 化する。
- seiton 側:
  - `UnpinnedUsesRule` は `@v1` を未 pin として検出（fix 対象になる）。
  - 実解決は `GitHubActionShaResolver.ResolveAsync()` が担当。
  - `fix.pinning.min-age-days` の既定値は `14`。
  - ref が `v1` のような version family と解釈できる場合、`SelectBestEligibleTagAsync()` で同系列タグ候補（`v1.*`）を列挙し、`min-age-days` 未満を除外する。
  - 今回はタグがすべて当日作成のため、既定 `14` 日で候補が空になり `(null, null)` を返し、fix が skip される。

## 差分の本質（再整理）

1. **branch 解決戦略の差**
   - pinact: `--branch-to-tag` に明示一致させると branch をタグへ変換して pin 可能。
   - seiton: ref 文字列が version family と解釈可能ならタグ候補探索を行うが、branch 専用の opt-in 機構はない。
2. **既定クールダウンの差（補助要因）**
   - pinact: `--min-age` 既定 0（実質無効）。
   - seiton: `fix.pinning.min-age-days` 既定 14（有効）。
   - `setup-seiton` のように新規タグ直後は seiton が意図的に skip しやすい。
3. **今回の直接原因（確定）**
   - `fix.pinning.min-age-days=0` でも `@v1` が失敗することを確認。
   - 原因は `GitHubActionShaResolver` が SHA 解決で `git/ref/tags/{ref}` のみ参照し、`refs/heads/{ref}` への fallback を持たないため。
   - そのため、`v1` が branch で存在しても解決できない。

## 優先度付き対応プラン（更新）

### P0（実装対象・最優先）

- `GitHubActionShaResolver` に branch fallback を実装する。
  - 仕様: tag 解決失敗（特に 404）時、`git/ref/heads/{ref}` を試す。
  - 目的: `@v1` のような branch ref を `min-age-days=0` でも確実に pin 可能にする。
  - 互換性: 既存 tag ref の挙動は維持。

### P1（テスト強化）

- `GitHubActionShaResolverTests` に branch fallback の失敗再現テストを追加し、Red→Green で実装する。
- 既存の min-age 系テストと共存する形で、分岐網羅（tag 成功 / tag 404→branch 成功 / 両方失敗）を担保する。

### P2（UX/API改善）

- 必要に応じて branch→latest-tag 変換の opt-in（pinact `--branch-to-tag` 相当）を検討する。
- ただし今回フェーズでは「branch ref を SHA へ解決できること」を優先し、設定面の追加は行わない。

### P3（仕様/ドキュメント同期）

- `.github/docs` と `docs/configuration.md` に、ref 解決順（tag 優先、branch fallback）を明記する。
- 実装と仕様に差分があれば、実装または仕様を一致させる。

## 実装フェーズ（TDD + 性能確認 + レビュー反復）

1. **Phase 1 (Red)**: branch fallback 未実装を示すテストを追加して失敗確認。
2. **Phase 2 (Green)**: `GitHubActionShaResolver` に最小変更で branch fallback を実装。
3. **Phase 3 (Verify)**: 追加テスト + 関連テスト + `dotnet test` 全体実行。
4. **Phase 4 (Benchmark)**: lint 系ベンチマークを実施し、変更前後を比較。
5. **Phase 5 (Review Loop)**: 各フェーズで自己レビュー（正確性・性能・API直感性・仕様整合）を実施し、指摘がなくなるまで反復。
6. **Phase 6 (Commit)**: フェーズ完了ごとにコミットする。

## ユーザーファースト API 観点

- 既存設定で直感的に動くことを優先し、`min-age-days=0` で branch ref が自然に pin される体験を実現する。
- 新規設定を増やさず、既存ユーザーの認知負荷を上げない。
- エラーメッセージ/失敗時挙動は従来互換を維持する。

## 補足（今回入力例への適用）

- `uses: guitarrapc/setup-seiton@v1` + `seiton-version: v0.9.19` のケースは、`seiton-version` の値とは独立に `uses` ref 解決側で skip される。
- `@0f877ad... # v1.0.1` は既に SHA pin されており、今回の未 pin 問題の対象外。

## 実装結果（Phase 1-6）

### Phase 1 (Red): 失敗テスト追加

- `GitHubActionShaResolverTests` に以下を追加:
  - `ResolveAsync_FallsBackToBranchReference_WhenTagReferenceIsMissing_AndMinAgeDaysIsZero`
- 失敗確認:
  - `git/ref/tags/v1` が 404 の場合に `InvalidOperationException` となり、branch fallback が無いことを確認。

### Phase 2 (Green): branch fallback 実装

- `GitHubActionShaResolver` を変更:
  - 既存の `git/ref/tags/{ref}` 解決を維持。
  - tag 解決が 404 の場合のみ `git/ref/heads/{ref}` を試行。
  - 成功時は既存と同形式で `(sha, ref comment)` を返却。
- 実装は最小差分に限定し、既存の annotated tag 展開ロジックは再利用。

### Phase 3 (Verify): テスト実行

- 追加テスト: pass
- `GitHubActionShaResolverTests` 全体: pass（12/12）
- `dotnet test` 全体:
  - `Seiton.Core.Tests`, `Seiton.Tests`, `Seiton.Update.Tests` は pass
  - `Seiton.Playground.Tests` で Playwright UI timeout/asset 系の既存失敗あり（今回変更箇所外）

### Phase 4 (Benchmark): 変更前後比較

- 対象: `CoreLintBenchmark`（`HEAD~1` vs 実装後）
- 結果（代表）:
  - Large / Fix=true: `33.916 ms -> 33.958 ms`（+0.12%）
  - Large / Fix=false: `22.425 ms -> 22.903 ms`（+2.13%）
  - Medium / Fix=false: `1.513 ms -> 1.571 ms`（+3.83%）
  - Allocated: 全ケースで変化なし（Alloc Ratio 1.00）
- 評価:
  - 主要ケースで +10% 以内、割当増加なし。
  - 変更はネットワーク解決経路の分岐追加であり、lint hot-path への影響は実質なし。

### Phase 5 (Review Loop): 指摘と対応

- 指摘1: `min-age=0` 時の branch 未解決（根本原因）
  - 対応: tag 404 時の branch fallback を追加（解消）
- 指摘2: API 体験（ユーザーファースト）
  - 対応: 新設定を増やさず既存設定のまま `@v1` が直感的に解決される挙動へ改善
- 指摘3: 性能劣化リスク
  - 対応: 404 時のみ追加 API 呼び出しとし、通常 tag 成功パスのコスト増を抑制
- 指摘4: min-age skip 時に利用者が理由を把握できない
  - 対応: `IActionShaResolver` に skip 理由を返すモデルを追加し、`PinRemediationEngine` で `Diagnostic.Help` に反映
  - 詳細記録: `.github/docs/plan_pinning.md`

### Phase 6 (Commit)

- フェーズ完了ごとにコミット運用とし、本変更では:
  - テスト追加（Red）
  - 実装＋ドキュメント更新（Green/Verify/Benchmark）
  をそれぞれコミット対象とする。

# min-age-days により pin されない理由表示の調査・対応

## 背景

`seiton --fix --enable-pin-network` 実行時、`guitarrapc/setup-seiton@v1.0.0` が当日リリース直後で `fix.pinning.min-age-days: 14` により skip されたが、利用者には「なぜ pin されなかったか」が分からなかった。

## 調査結果

- `GitHubActionShaResolver` は min-age 条件で候補なしの場合、`(null, null)` を返すだけで理由文字列を返していない。
- `PinRemediationEngine` は `sha/tag` が `null` のとき「skip」としてカウントするが、診断メッセージや `help` へ理由を反映していない。
- そのため CLI 出力では「fix されなかった事実」は分かるが、「なぜ skip されたか（例: min-age gate）」が分からない。

## 対応方針（ユーザーファースト）

1. `IActionShaResolver` の戻り値を `ActionShaResolution` に拡張し、skip 理由 (`SkipReason`) を運べるようにする。
2. `GitHubActionShaResolver` で min-age により skip された場合、明示的な理由文を `SkipReason` に設定する。
3. `PinRemediationEngine` で uses 解決が skip のとき、`SkipReason` を `Diagnostic.Help` に追記する。
4. 既存挙動（fix 成功時の修正内容、失敗カウント、network off 時の挙動）は維持する。

## TDD 実施

- Red:
  - `RemediateAsync_AttachesSkipReasonToHelp_WhenUsesResolutionIsSkipped`
  - `RemediateAsync_AppendsSkipReasonToExistingHelp_WhenHelpAlreadyExists`
- Green:
  - `ActionShaResolution` を追加
  - `IActionShaResolver` / `GitHubActionShaResolver` / `PinRemediationEngine` を更新
- 等価クラス観点（分類ロジック）:
  - `GitHubActionShaResolverTests` で既存の分岐網羅を維持（tag成功 / tag404→branch成功 / tag404→branch失敗 / tag非404失敗）

## 仕様同期

- 更新:
  - `.github/docs/Seiton_Linter_spec.md`
  - `.github/docs/Seiton_Linter_csharp_spec.md`
- 反映内容:
  - skip 理由を `help` に表示する仕様
  - C# 実装の `IActionShaResolver` 戻り値モデルの更新

## 検証結果

- テスト:
  - `PinRemediationEngineTests`: pass
  - `GitHubActionShaResolverTests`: pass
  - `PinRemediationContractsTests`: pass
  - `PlaygroundLintRunnerAsyncFixTests` の対象テスト: pass
  - `dotnet test` 全体は既存の `Seiton.Playground.Tests` 7件（Playwright timeout/asset）で失敗（今回変更箇所外）
- ベンチマーク:
  - `CoreLintBenchmark` 実施
  - `Allocated` は全ケースで `Alloc Ratio = 1.00`
  - `Ratio` は `1.00 ~ 1.01` 程度で +10% しきい値内
