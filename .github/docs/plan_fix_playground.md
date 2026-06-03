# Playground diagnostics display bug investigation and fix plan

## 背景

Playground で lint 結果が存在するにもかかわらず、表示が次のように崩れる事象を確認:

- `line:0, col:0`
- `Info`
- メッセージが欠落（または空）

この状態だと診断内容が読めず、実質的に Playground の主要機能が失われる。

## 調査結果（根本原因）

根本原因は `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` の `RunToJsonUtf8()` にある。

- Browser 実行時は `UseIncrementalLint == false` のため、非 incremental 分岐 (`Engine.Check(...)`) を通る。
- この分岐では `using var lintResult = Engine.Check(...)` で `lintResult.Diagnostics.AsSpan()` を `diagnosticsToSerialize` に退避している。
- しかし JSON へのシリアライズは `using` スコープの外側で実行されるため、`lintResult` 解放後の span を読み取ってしまう（解放済み/再利用済みバッファ参照）。
- その結果、`Diagnostic` 構造体の値が既定値寄りに崩れ、`line:0 col:0 / severity:Info / message空` のような表示になる。

要するに **diagnostics バッファの lifetime 破壊（use-after-dispose 相当）** が直接原因。

## なぜ既存テストで検出できなかったか

1. `tests/Seiton.Playground.Tests/PlaygroundLintRunnerTests.cs` はデスクトップ環境で実行されるため、通常 `UseIncrementalLint == true` 経路を主に通る。
2. 問題の分岐（Browser 向け `UseIncrementalLint == false`）を直接検証するテストがない。
3. 既存の JSON 検証は「プロパティの存在」中心で、`line >= 1`、`column >= 1`、`message 非空`、期待 ruleId の存在などの意味検証が弱い。
4. UI 側 Playwright テストは主にクラッシュ回避・レイアウト・フック可用性を見ており、描画された診断の内容妥当性を検証していない。

## 影響範囲

- 主影響: `src/Seiton.Playground.Core/PlaygroundLintRunner.cs` の Browser 経路
- 間接影響: Playground UI (`src/Seiton.Playground/wwwroot/main.js`) の表示品質
- 影響しない領域: CLI 表示フォーマッタ本体（ただし Playground 経路とは別実装）

## 優先度付き対応プラン

### P0（最優先: 直ちに修正）

1. `RunToJsonUtf8()` の非 incremental 分岐で、`lintResult` が生存している間に JSON シリアライズまで完了させるように構造を修正する。
2. `Diagnostic` span を `using` スコープ外に持ち出さない形に統一する（他分岐も含め lifetime 安全性を明示）。
3. バグ再現用の最小回帰テストを追加する:
   - `line >= 1`
   - `column >= 1`
   - `severity` が期待クラス（少なくとも全件 `Info` 固定にならない）
   - `message` 非空

完了条件:

- 再現入力で `line:0, col:0 Info` 固定表示が消える。
- 追加した回帰テストが fail→fix 後 pass になる。

### P1（高優先: 検出力強化）

1. Playground テストに Browser 経路専用の検証を追加する（`runLint` フック経由で diagnostics payload の内容を厳密検証）。
2. 既存の `PlaygroundLintRunnerTests` を強化し、「JSON shape」ではなく「意味（位置・メッセージ・ruleId）」を assert する。
3. UI テストに 1 本、結果テーブルの実描画検証を追加する（1 行以上の line/col と message 表示を確認）。

完了条件:

- Browser/desktop 双方で diagnostics 妥当性テストが存在。
- lifetime 破壊の再発時に CI で即検出できる。

### P2（中優先: 保守性と安全策）

1. `PlaygroundLintRunner` に「`DiagnosticList` の有効期間」についてのコードコメントを追記。
2. span を跨ぐ設計になっていないかを同ファイルで棚卸しし、同種パターンを除去。
3. ドキュメント（Playground spec もしくはテスト設計メモ）に「Browser 経路での診断完全性」を非機能要件として明記。

完了条件:

- 同種バグの温床となる寿命依存コードがレビューで見つけやすくなる。

## 推奨実施順序

1. P0 のテスト追加（失敗確認）
2. P0 の実装修正
3. P0 テスト再実行 + `dotnet test` 全体実行
4. P1 の検出力強化
5. 必要に応じて P2 ドキュメント整備

## 補足

- この不具合はユーザー体験への影響が大きいため、優先度は **Critical (P0)**。
- 修正時は Playground の Browser 経路を明示的に通すテストを必須化すること（desktop のみでは再発を見逃す）。
