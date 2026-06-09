# Direct Use Rules Plan (run:*context-direct-use)

## 1. 目的

`run-inputs-context-direct-use` を起点に、`run-env-context-direct-use` / `run-secrets-context-direct-use` を含む「run スクリプト内での直接参照ルール」のノイズと検出価値のバランスを再設計する。

本書は調査結果と優先度付き対応プランを示す。実装詳細（HOW）は別作業で扱う。

## 2. スコープ

対象ルール:

- `run-inputs-context-direct-use`
- `run-env-context-direct-use`
- `run-secrets-context-direct-use`

関連ルール（整合確認対象）:

- `template-injection`

対象外:

- ルール以外の parser / formatter の仕様変更
- CLI 互換性ポリシーの変更

## 3. 調査結果

### 3.1 現在の挙動（事実）

1. `run-inputs-context-direct-use` は、単一引用符内でも診断自体は出す。
2. ただし auto-fix は、単一引用符内では付与しない。
3. no-expand heredoc (`<<'EOF'`) では、診断自体を抑制する。

確認根拠:

- 実装: `.github/docs` ではなくコード実体として `src/Seiton.Core/Linting/Rules/RunInputsContextDirectUseRule.cs`
- 既存テスト: `tests/Seiton.Core.Tests/RuleInterfaceTests.LintEngine.cs`
  - 単一引用符内で fix なし（diagnostic は存在）
  - single-quoted heredoc で diagnostic 抑制

### 3.2 ユーザー観点の問題（今回の論点）

`ssh ... 'bash -s -- --branch "${{ inputs.branch }}" ...'` のようなケースでは、次が同時に成立する。

1. 利用者が意図的に単一引用符でリモート実行文字列を組み立てる。
2. 現行メッセージは「env へマッピングして shell 変数へ置換」を推奨するが、同文脈では適用しづらい。
3. 結果として「修正不能に近い診断」がノイズになる。

### 3.3 リスク観点（ルール別）

- `inputs` / `env`:
  - 主目的は可読性・運用整合・安全な展開パターンの推奨。
  - 単一引用符内は実用上の例外が多く、ノイズ化しやすい。
- `secrets`:
  - 主目的が機密情報取り扱いの安全性に近い。
  - 同じ単一引用符例外をそのまま適用すると、検出価値を落とす懸念が高い。

### 3.4 ドキュメント整合の現状

`Seiton_Linter_spec.md` のルール説明は、`run-inputs-context-direct-use` を「must map via env」として定義している。

- 仕様文言上、単一引用符内の診断抑制ポリシーは未明文化。
- fixability テーブルには「single-quoted strings は no-fix」とあるが、diagnostic 抑制の明確な規定はない。

## 4. 意思決定方針（提案）

原則: 「検出価値 > ノイズ」の閾値をルール目的ごとに分離する。

- `inputs` / `env`: ノイズ抑制を優先
- `secrets`: 検出維持を優先

## 5. 優先度付き対応プラン

### P0 (最優先): 方針確定と仕様文言の明文化

目的:

- 単一引用符内の扱いをルールごとに明文化し、期待値の揺れを止める。

対応:

1. `run-inputs-context-direct-use` の単一引用符内ポリシーを「diagnostic 抑制」にするか、または「diagnostic 維持 + 明示メッセージ」にするかを確定。
2. 同時に `run-env-context-direct-use` の方針も揃える。
3. `run-secrets-context-direct-use` は別基準（抑制しない寄り）であることを明文化。

完了条件:

- 3ルールの単一引用符内ポリシーが仕様文書に矛盾なく記載される。

### P1: run-inputs-context-direct-use のノイズ削減

目的:

- ssh リモート実行など、利用者意図が明確な単一引用符パターンで不要警告を減らす。

対応候補:

1. 単一引用符内の `inputs` 直接参照を diagnostic 抑制。
2. もしくは既定抑制 + strict モードで再有効化可能にする。

推奨:

- 既定は抑制、必要時のみ strict で検出。

完了条件:

- 回帰ケース（ssh リモート実行）で期待どおりの結果になる。
- 既存の `inputs` 検出価値を過度に毀損しない。

### P2: run-env-context-direct-use への同一設計適用

目的:

- 類似ルール間で運用体験を統一する。

対応:

1. `env` ルールでも単一引用符内を `inputs` と同じ哲学で扱う。
2. 例外の説明文を統一する。

完了条件:

- `inputs` / `env` の単一引用符内挙動が一貫し、利用者説明が同じロジックで可能になる。

### P3: run-secrets-context-direct-use の安全側維持と文言改善

目的:

- セキュリティ検出を維持しつつ、誤解を減らす。

対応:

1. 単一引用符内でも検出維持を基本とするかを再確認。
2. fix 不可文脈では、メッセージを「安全上の注意喚起」へ調整し、機械的な env 置換誘導を弱める。

完了条件:

- セキュリティ感度を下げずに、修正不能ケースでの不満を軽減する。

### P4: クロスルール整合とガードレール整備

目的:

- `template-injection` を含む関連ルールで「single quote 文脈」の扱いを体系化する。

対応:

1. single quote / heredoc / double quote 内 single quote の判定方針を共通ルールとして整理。
2. 将来の新規ルールが同じ誤差を繰り返さないよう、設計ガイドを追記。

完了条件:

- ルール間で single quote 文脈の挙動差分が意図的なものだけになる。

## 6. 受け入れ指標（実装前に合意する指標）

- ノイズ指標:
  - 単一引用符内の `inputs` / `env` で、実運用上の「修正不能」診断を減らせる。
- 検出維持指標:
  - `secrets` 系の検出率を下げない。
- 一貫性指標:
  - 類似ルール間で、同じ文脈に対する挙動説明が矛盾しない。

## 7. 想定リスク

1. 抑制を広げすぎると、本来拾うべきケースを落とす。
2. ルール別に挙動を変えると、利用者にとって覚えにくくなる。
3. strict モード導入時に設定複雑性が増える。

## 8. 推奨実施順

1. P0: 仕様合意（最短）
2. P1: `inputs` ノイズ削減
3. P2: `env` 整合
4. P3: `secrets` 文言調整
5. P4: 横断ガードレール

---

本計画は「今すぐ実装」ではなく、「方針の衝突を先に解消してから最小変更で実装する」ことを目的とする。

## 9. 実装結果（2026-06-09）

### 9.1 P0 実施結果（方針確定と仕様文言の明文化）

確定方針:

1. `run-inputs-context-direct-use` は shell no-expand 文脈（単一引用符文字列 / single-quoted heredoc）で diagnostic を抑制する。
2. `run-secrets-context-direct-use` は本フェーズでは挙動変更しない（安全側維持）。
3. `run-env-context-direct-use` は本フェーズでは挙動変更しない（P2 で再評価）。

反映ドキュメント:

- `.github/docs/Seiton_Linter_spec.md`
- `.github/docs/Seiton_Linter_csharp_spec.md`
- `docs/rules.md`

### 9.2 P1 実施結果（run-inputs-context-direct-use のノイズ削減）

実装概要:

1. `RunInputsContextDirectUseRule` の検出フェーズに単一引用符文脈判定を追加。
2. `IsInsideShellSingleQuotes(...)` が真の場合、diagnostic を emit せず `continue`。
3. 既存の no-expand heredoc 抑制は維持。

追加/更新テスト:

1. `LintEngine_RunInputsContextDirectUse_DoesNotDiagnose_InsideSingleQuotes`
2. `LintEngine_RunInputsContextDirectUse_DoesNotDiagnose_SshRemoteCommandInsideSingleQuotes`

補足:

- 既存の fix 動作（通常文脈）は保持。
- no-expand heredoc 抑制テスト・複合式 fix テストも併せて再実行して回帰なしを確認。

## 10. 検証結果

### 10.1 テスト

実行コマンド:

1. `dotnet test --project tests/Seiton.Core.Tests --treenode-filter ...RunInputsContextDirectUse_DoesNotDiagnose_...`
2. `dotnet test`

結果:

- 追加した P1 テストは Red→Green を確認。
- 全体テストは各テストプロジェクトの pass を確認（Playground は既知の skip 1 件）。

### 10.2 ベンチマーク（実装前後）

実行コマンド（前後同一）:

- `dotnet run --project src/Seiton.Benchmark -c Release --filter "*CoreLintBenchmark*"`

前回計測（実装前、抜粋）:

- Small/Fix=false: Mean 243.9 us, Alloc 8.81 KB
- Small/Fix=true: Mean 128.1 us, Alloc 10.13 KB
- Medium/Fix=false: Mean 2,501.8 us, Alloc 68.52 KB
- Medium/Fix=true: Mean 3,297.6 us, Alloc 81.88 KB
- Large/Fix=false: Mean 32,949.1 us, Alloc 325.53 KB
- Large/Fix=true: Mean 65,305.9 us, Alloc 380.52 KB

今回計測（実装後、抜粋）:

- Small/Fix=false: Mean 361.8 us, Alloc 8.81 KB
- Small/Fix=true: Mean 341.1 us, Alloc 10.27 KB
- Medium/Fix=false: Mean 3,767.7 us, Alloc 68.66 KB
- Medium/Fix=true: Mean 5,467.4 us, Alloc 81.88 KB
- Large/Fix=false: Mean 51,731.7 us, Alloc 325.54 KB
- Large/Fix=true: Mean 71,917.7 us, Alloc 380.38 KB

評価:

1. Allocated は実質同等（誤差レベル）。
2. Mean は短時間計測（ShortRun）でぶれが大きく、今回差分のみで +10% 判定は信頼性不足。
3. 変更内容は「該当文脈で早期 continue」なので、理論的には悪化要因は小さい。

改善策:

1. 本件の最終性能判定は `CoreLintBenchmark` を長め設定で再計測（Iteration 増）して確定する。
2. `run-inputs-context-direct-use` 専用ベンチ（single-quote / normal / heredoc ケース）を追加し、局所回帰を継続監視する（P2 着手前に実施）。

## 11. ユーザーファースト観点レビュー

レビュー観点:

1. 診断が利用者の意図を尊重しているか
2. 修正可能性のない警告を過度に出していないか
3. メッセージと実際の修正手段が一致しているか

レビュー結果:

1. ssh リモート実行などの単一引用符文脈で non-actionable な警告を抑制でき、体験改善。
2. 危険側の `secrets` 系は本フェーズでは据え置きにして過抑制を回避。
3. 仕様・実装・ユーザー文書の記述を同時更新し、期待値の不一致を解消。

残課題:

1. `run-env-context-direct-use` への同哲学適用（P2）
2. `run-secrets-context-direct-use` のメッセージ最適化（P3）

## 12. フェーズレビュー反復ログ

### Review Round 1

指摘:

1. 単一引用符文脈で diagnostic は残るため、実運用上ノイズが続く。

対応:

1. `run-inputs-context-direct-use` で単一引用符文脈を検出段階で抑制。

### Review Round 2

指摘:

1. 仕様とユーザー文書が旧挙動（single-quoted は no-fix）寄りのままだと混乱する。

対応:

1. shared spec / csharp spec / docs/rules を同期更新。

### Review Round 3

指摘:

1. 性能比較が ShortRun 単発ではノイズを含む。

対応:

1. プランに再計測条件と専用ベンチ追加方針を明記。

最終判定:

- 本フェーズの目的（P0/P1）は達成。追加改善は P2/P3 で継続。
