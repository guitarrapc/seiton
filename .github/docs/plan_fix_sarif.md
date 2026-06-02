# SARIF Validator フィードバック分析と修正計画

対象: `.github/docs/feedback_sarif_validator.md` に記録された SARIF Validator 指摘。

目的: 指摘ごとに「修正すべきか」を判断し、実装時の優先順位と検証方針を定義する。

## 1. 結論サマリ

| ID | 件数 | 判定 | 優先度 | 方針 |
|---|---:|---|---|---|
| SARIF1002 | 80 | 修正する | P0 | `artifactLocation.uri` を RFC 3986 準拠 URI にする |
| SARIF2005 | 1 | 修正する | P0 | `tool.driver` にバージョン情報を追加する |
| SARIF2004 | 1 | 今回は見送り（意図的維持） | P2 | `tool.driver.rules` は当面維持。将来メタデータ拡張時に再評価 |

理由:
- SARIF1002 は仕様違反であり、Code Scanning 連携先で解釈差異が出るリスクが高い。
- SARIF2005 はツール同定性に直結し、ログ比較・トリアージ品質に影響する。
- SARIF2004 は最適化提案（advisory）であり、現時点で機能不全は起こさない。

## 2. 現状分析

実装位置:
- SARIF 出力本体: `src/Seiton/Output/DiagnosticFormatter.cs`

確認できた現状:
- `artifactLocation.uri` に Windows 絶対パス文字列（例: `C:\...\file.yml`）をそのまま書いているため、URI として不正。
- `tool.driver` に `name` と `informationUri` はあるが、`version` / `semanticVersion` などがない。
- `tool.driver.rules` は `id` のみを持つ配列。

## 3. 指摘別の判断

### 3.1 SARIF1002 (P0, 修正)

問題:
- `artifactLocation.uri` がファイルパス文字列であり URI 形式ではない。

修正方針:
- SARIF 出力時にパスを URI に正規化する。
- 基本方針は以下の順で選択:
  1. 絶対パス: `file:///` 形式 URI に変換
  2. 相対パス: 相対 URI（スラッシュ区切り）として出力
  3. 不明値（`<unknown>`）: URI として不正になるため、安全な代替値に置換（後述の実装検討で最終決定）

実装上の論点:
- Windows パス区切り `\\` を `/` に統一する。
- 空白・`#` などの予約文字を適切にエスケープする。
- `..` を含む file URI を避ける（必要なら正規化）。

受け入れ条件:
- Validator の SARIF1002 が 0 件になる。
- 既存の `text` / `json` / `github-actions` 出力には影響しない。

### 3.2 SARIF2005 (P0, 修正)

問題:
- `runs[0].tool.driver` に version 系プロパティがない。

修正方針:
- `tool.driver.version` または `tool.driver.semanticVersion` を付与する。
- 既存の `seiton version` コマンドで使っているバージョン解決ロジック（`AssemblyInformationalVersion` 優先、`+metadata` トリム）と整合する値を採用する。

実装上の論点:
- Pre-release を含む値を使う場合は `semanticVersion` が適切。
- 互換性重視なら `version` も併記できる（必要最小限で開始するならどちらか一方で可）。

受け入れ条件:
- Validator の SARIF2005 が 0 件になる。
- ログ比較時にツールバージョンが識別できる。

### 3.3 SARIF2004 (P2, 今回見送り)

問題:
- `tool.driver.rules` が `id` のみで、冗長配列の可能性として警告されている。

判断:
- これは仕様違反ではなく最適化提案。
- `ruleId`/`ruleIndex` の参照先として `rules` を保持する設計は妥当。
- 将来、`shortDescription` / `helpUri` などを追加すれば警告は自然に解消できる。

今回の方針:
- 直近リリースでは変更しない（ノイズ削減より互換性と安定性を優先）。
- 別タスクで「ルールメタデータ拡張」を検討する。

## 4. 実施プラン（未実装）

### Phase 1 (P0): Validator エラー解消

対象:
- SARIF1002, SARIF2005

計画:
1. 失敗再現テストを追加（現状で赤になることを確認）。
2. URI 正規化ロジックを SARIF 出力パスに実装。
3. `tool.driver` へ version 系フィールドを追加。
4. 既存テストを含めて回帰確認。
5. SARIF Validator 再実行で 1002/2005 が解消されたことを確認。

### Phase 2 (P2): 最適化見直し（任意）

対象:
- SARIF2004

計画:
1. `rules` 維持時に付与できるメタデータ（説明・ドキュメント URL）を棚卸し。
2. コストに見合うなら `rules` を拡張、見合わなければ現状維持を明文化。

## 5. テスト・検証計画

最低限追加する検証観点:
- Windows 絶対パスが有効 URI になること。
- 相対パス入力時にも URI 形式が壊れないこと。
- 不明パス時に不正 URI を出さないこと。
- `tool.driver` に version 系プロパティが出ること。

実行確認:
- `dotnet test`（該当テストプロジェクト中心）
- 実 SARIF を生成し、SARIF Validator で再検証。

期待結果:
- SARIF1002: 80 -> 0
- SARIF2005: 1 -> 0
- SARIF2004: 1 -> 1（意図的に維持）

## 6. 影響範囲とリスク

影響範囲:
- `src/Seiton/Output/DiagnosticFormatter.cs`（SARIF 出力のみ）
- SARIF 関連テスト

主なリスク:
- URI 正規化で既存コンシューマとの見え方が変わる可能性。
- バージョン文字列形式の選択を誤ると validator 警告が残る可能性。

リスク緩和:
- 既存のバージョン取得ロジックと整合する。
- 変換結果を固定化するテストを先に置く。

## 7. この計画の完了定義

- P0 項目（SARIF1002/SARIF2005）の修正方針が合意されている。
- 実装時にそのまま着手できるテスト観点と受け入れ条件が明記されている。
- P2 項目（SARIF2004）の扱いが「今回は見送り」として明文化されている。

## 8. Phase 1 実装結果（2026-06-02）

実装ステータス:
- 完了（SARIF1002/SARIF2005 対応）

実装内容:
1. SARIF の `artifactLocation.uri` を URI 正規化する実装を追加。
  - 絶対パス: `file:///...`
  - 相対パス: URI-safe な相対参照
  - 不明パス: `file:///unknown`
2. SARIF の `runs[].tool.driver.version` を追加。
  - 取得元は assembly informational version
  - `+metadata` はトリム
3. SARIF ベンチマーク項目を追加。
  - `DiagnosticOutputBenchmark.WriteSarif`

追加テスト（Red -> Green）:
- `Sarif_Format_WindowsAbsolutePath_EmitsFileUri`
- `Sarif_Format_UnknownPath_UsesSafeFileUri`
- `Sarif_Format_Driver_IncludesVersionMetadata`

検証結果:
- 追加テスト: 失敗を確認後、実装後に成功。
- フルテスト: `dotnet test` で全件成功（2363 passed, 0 failed）。

## 9. ベンチマーク結果（変更前後比較）

測定条件:
- BenchmarkDotNet ShortRun
- 対象: `DiagnosticOutputBenchmark.WriteSarif`
- Count パラメータ: `F1`, `F10`

変更前（baseline: HEAD~1 + 同一ベンチハーネス）:
- F1: Mean 90.276 us, Allocated 143.17 KB
- F10: Mean 1,141.39 us, Allocated 1,533.92 KB

変更後（current）:
- F1: Mean 90.094 us, Allocated 143.26 KB
- F10: Mean 1,164.80 us, Allocated 1,528.03 KB

差分評価:
- F1 Mean: -0.20%（改善）
- F10 Mean: +2.05%（許容範囲、閾値 +10% 以内）
- F1 Allocated: +0.06%（ほぼ同等）
- F10 Allocated: -0.38%（改善）

考察:
- 初回実装では相対パスの URI エンコード処理が重く、F10 で +10% 超の劣化を確認。
- その後、一般的な安全相対パスをそのまま返す高速経路を追加し、劣化を +2.05% まで低減。

## 10. API/仕様整合レビュー

ユーザーファースト API 観点:
- CLI の利用方法は変更なし（`--format sarif` のまま）。
- 出力品質のみ改善し、利用者は追加設定なしで validator 準拠の SARIF を得られる。
- `driver.version` 追加により、ログ比較時の可観測性が向上。

仕様整合:
- 実装変更に合わせて `.github/docs/Seiton_CLI_spec.md` の SARIF 仕様を更新済み。

## 11. フェーズ内レビュー反復記録

Review Round 1:
- 指摘: SARIF URI 正規化の初期実装でパフォーマンス劣化（F10 Mean が +10% 超）。
- 対応: 安全相対パスの高速経路、絶対 URI 判定の軽量化を追加。

Review Round 2:
- 再評価: ベンチ差分が閾値内（+2.05%）に収束、テスト全件成功。
- 追加指摘: なし。
