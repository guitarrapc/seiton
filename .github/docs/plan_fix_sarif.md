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
