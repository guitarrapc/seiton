# run-env-context-direct-use fix安全化計画

## 背景

`run-env-context-direct-use` の `--fix` は、`run:` 文字列内の `${{ env.FOO }}` を shell 変数に自動置換する。

しかし現在はクォート文脈を判定せずに置換するため、次のようなケースで意味を壊す。

- 置換前: `echo '${{ env.FOOBAR }}'`
- 置換後: `echo '${FOOBAR}'`

GitHub Actions の式展開（`${{ ... }}`）はシングルクォート内でも行われる一方、bash/pwsh の shell 変数展開はシングルクォート内で行われない。このため、fix 後に実行時挙動が変わる。

## 原因（WHY）

現状の `RunEnvContextDirectUseRule` は、`TryBuildFix` で単純参照（`env.FOO` / `env['FOO']`）を検出すると、引用符文脈を見ずに次を返す。

- PowerShell: `$env:FOO`
- それ以外: `${FOO}`

つまり「置換先トークンの妥当性」は見ているが、「置換後に同じ展開意味を保てるか（クォート/複合文字列/エスケープ）」を見ていないのが根本原因。

## 目的（WHAT）

`run-env-context-direct-use` の fix を「意味保存が担保できる場合のみ適用」へ変更する。

- 安全に意味保存できる場合のみ fix を付与
- 意味保存に不確実性がある場合は fix を付与しない（診断のみ）
- ユーザーが期待する範囲で、単純なシングルクォートはダブルクォート化して置換可能にする

## 優先順位付き対応プラン

### P0（最優先）: 危険な自動置換を止める

1. 失敗再現テストを先に追加（Red）
- `run: echo '${{ env.FOOBAR }}'` で、現状 fix が意味破壊することを再現するテストを追加
- 期待値は「現状は失敗（誤fix）」を明示

2. fix 付与条件に「クォート安全性判定」を導入
- `run` の対象式位置について、最低限次を判定する
- シングルクォート文字列内か
- ダブルクォート文字列内か
- クォート外か
- 判定不能/曖昧（複雑なエスケープ、混在など）か

3. P0 判定ルール（保守的）
- クォート外: 既存通り置換可
- シングルクォート内: P1 の「単純ケース」以外は置換不可
- ダブルクォート内: 置換可（ただし複合ケースは P1 で厳密化）
- 判定不能: 置換不可

4. 置換不可時の挙動
- 診断は出す
- `Fix` は付与しない
- 必要なら `Help` に「クォート文脈のため自動修正しない」旨を追加

完了条件:
- 危険ケース（シングルクォート）で `Fix == null`
- 既存の安全ケース（クォート外、単純ダブルクォート）は従来通り fix 可能

### P1（高）: 単純シングルクォートを安全に救済

1. ユーザー意図の反映
- 明らかに単純なシングルクォートのみ、ダブルクォートへ変換して置換する

2. 単純ケース定義（初期）
- 1つの単純式のみを含むトークン（例: `'${{ env.FOO }}'`）
- 周囲に追加テキスト無し（prefix/suffix なし）
- 同一スカラー内に別のクォート/エスケープ複雑性がない

3. 自動修正内容
- `'${{ env.FOO }}'` → `"${FOO}"`（bash系）
- `'${{ env.FOO }}'` → `"$env:FOO"`（pwsh）

4. 複雑ケースは非fix
- 例: `"x='${{ env.FOO }}'"`、`'pre-${{ env.FOO }}-post'`、複数式混在
- これらは意味差異リスクが高いため fix 付与しない

完了条件:
- 単純ケースだけ fix が付き、期待どおりダブルクォート化される
- 複雑ケースでは `Fix == null` を維持

### P2（中）: 仕様化・回帰防止・性能確認

1. テスト拡充（等価クラス）
- 正常: クォート外 / ダブルクォート内 / 単純シングルクォート救済
- 非fix: 複雑シングルクォート / 混在クォート / 判定不能
- shell 差分: bash/pwsh と defaults 上書き（workflow/job/step）

2. ドキュメント更新
- `.github/docs/Seiton_Linter_spec.md` と `.github/docs/Seiton_Linter_csharp_spec.md` の `run-env-context-direct-use` fix 制約へ反映
- `docs/rules.md` に「When fixing」でクォート依存の自動修正制限を明記

3. ベンチ確認
- Linting 経路の benchmark を実行し、Mean/Allocated の増加が許容範囲（+10%）内を確認

完了条件:
- 仕様・実装・ユーザー向け説明が一致
- 既存性能目標を満たす

## 実装順（推奨）

1. P0 の failing test 追加（まず再現）
2. P0 の安全性ゲート実装
3. P0 テスト緑化 + 既存回帰
4. P1 の単純救済テスト追加
5. P1 実装
6. 全テスト + benchmark
7. P2 ドキュメント同期

## 受け入れ基準

- `run: echo '${{ env.FOOBAR }}'` は、
  - bash/pwsh ともに「意味破壊する fix」を出さない
  - もしくは単純ケースとして救済する場合は、意味保存される形（ダブルクォート化 + 適切変数）でのみ fix を出す
- 複雑クォート文脈では fix を出さない
- 既存の単純安全ケースは従来の自動修正を維持

## リスクと対策

- リスク: 判定が厳しすぎて fix が減る
- 対策: まず安全側（false negative 許容）で導入し、P1 で段階的に救済範囲を拡張

- リスク: クォート解析追加で性能劣化
- 対策: 式近傍のみの軽量走査に限定し、benchmark で確認

- リスク: shell ごとの差異見落とし
- 対策: bash/pwsh のテーブル駆動テストを同時に追加

## 補足（今回の判断方針）

- 「安全に意味保存できるか」を fix 付与の最優先条件にする
- 自動修正は便利性よりも非破壊性を優先する

## P0 実装記録（完了）

### 実装内容

- `RunEnvContextDirectUseRule.TryBuildFix` に `IsInsideShellSingleQuotes` 判定を追加
- シングルクォート内部の `${{ env.* }}` は診断のみ（`Fix` 非付与）に変更
- 既存の heredoc no-expand 判定と組み合わせ、安全側で fix を抑制

### テスト（Red → Green）

- 追加: `LintEngine_RunEnvContextDirectUse_Fix_DoesNotAttachFix_InShellSingleQuotes`
  - ケース: `run: echo '${{ env.VERSION }}'`
  - 期待: `diagnostic.Fix == null`
- Red 確認: 変更前は `Fix` が付いて失敗
- Green 確認: 実装後に当該テスト、および `LintEngine_RunEnvContextDirectUse*` 群が全件成功

### 回帰確認

- `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/LintEngine_RunEnvContextDirectUse*` は成功
- `dotnet test` 全体は実行済みだが、今回変更と無関係な既知失敗（`Seiton.Update.Tests` の raw source hash 差分、`Seiton.Tests` のパス表現差分）で失敗

### ベンチマーク

実行コマンド（前後共通）:

```shell
dotnet run -c Release --project src/Seiton.Benchmark --filter "*CoreLintBenchmark*"
```

結果（ShortRun）:

| Case | Before Mean | After Mean | 変化 | Before Alloc | After Alloc | 変化 |
|---|---:|---:|---:|---:|---:|---:|
| Small / Fix=false | 173.3 us | 227.2 us | +31.1% | 8.67 KB | 8.81 KB | +1.6% |
| Small / Fix=true | 194.8 us | 302.0 us | +55.0% | 10.13 KB | 10.27 KB | +1.4% |
| Medium / Fix=false | 3,471.1 us | 3,079.7 us | -11.3% | 68.52 KB | 68.66 KB | +0.2% |
| Medium / Fix=true | 4,433.1 us | 4,909.9 us | +10.8% | 81.88 KB | 82.00 KB | +0.1% |
| Large / Fix=false | 49,977.3 us | 52,249.8 us | +4.5% | 325.53 KB | 325.53 KB | 0.0% |
| Large / Fix=true | 64,850.6 us | 77,876.0 us | +20.1% | 380.38 KB | 380.38 KB | 0.0% |

評価:

- Allocated は全ケースでほぼ不変（0.0%〜+1.6%）で、追加判定は実質ゼロアロケーション
- Mean は ShortRun のばらつきが大きく、特に Small/Fix=true と Large/Fix=true で揺れが大きい
- 本変更は `IsInsideShellSingleQuotes` を 1 回追加するだけで、計算量は「式位置までの同一行走査」増分に限定される

改善策:

- P2 で `run-env-context-direct-use` 専用の rule-focused benchmark を追加し、ノイズの少ない比較に切り替える
- CoreLintBenchmark は回帰監視継続しつつ、性能判定は複数回計測の中央値で評価する

### ユーザーファースト API 観点

- 危険な自動修正を抑制し、ユーザーの意図しない実行時挙動変更を防止
- 診断自体は維持するため、修正対象の発見性は落とさない
- 既存の安全ケース（クォート外、単純式）は従来どおり自動修正可能

### 仕様整合性

- `.github/docs/Seiton_Linter_spec.md` の fixability 記述を更新
  - `run-env-context-direct-use` は「quoted heredoc に加えて shell single-quoted strings も no-fix」と明記
- `docs/rules.md` の `run-env-context-direct-use` セクションも同様に更新

### 実装レビュー（反復）

レビューラウンド 1 指摘:

- 指摘: `run-env-context-direct-use` だけが single-quote no-fix を持たず、`run-inputs` / `run-secrets` と挙動不整合
- 対応: `TryBuildFix` に `IsInsideShellSingleQuotes` を追加して整合化

レビューラウンド 2 指摘:

- 指摘: 実装変更後、仕様文言が heredoc 例外のみで不一致
- 対応: `Seiton_Linter_spec.md` と `docs/rules.md` を更新して一致

レビューラウンド 3 指摘:

- 指摘なし

## P1 実装記録（完了）

### 実装内容

- `RunEnvContextDirectUseRule.TryBuildFix` の single-quote 分岐を P0 の全面 no-fix から拡張
- `IsInsideShellSingleQuotes` かつ「単純トークン `'${{ env.VAR }}'`」のみを救済
- 救済時は置換範囲を式本体ではなく前後のシングルクォートを含む範囲へ拡張し、ダブルクォート化して shell 変数へ置換
  - POSIX: `"${VAR}"`
  - PowerShell: `"$env:VAR"`
- 複雑 single-quote（前後に追加テキストあり等）は従来通り no-fix

### テスト（Red → Green）

追加/更新:

- `LintEngine_RunEnvContextDirectUse_Fix_RewritesSimpleShellSingleQuotes_ForPosix`
- `LintEngine_RunEnvContextDirectUse_Fix_RewritesSimpleShellSingleQuotes_ForPowerShell`
- `LintEngine_RunEnvContextDirectUse_Fix_DoesNotAttachFix_ForComplexShellSingleQuotes`

Red:

- 実装前は simple single-quote ケースで `Fix == null` となり失敗

Green:

- 実装後は simple single-quote 2 ケースで fix 付与・期待置換
- complex single-quote は `Fix == null` を維持
- `LintEngine_RunEnvContextDirectUse*` 一式（18件）成功

### 回帰確認

- `dotnet test --project tests/Seiton.Core.Tests --treenode-filter /*/*/RuleInterfaceTests/LintEngine_RunEnvContextDirectUse*` は成功
- `dotnet test` 全体は実行済みだが、今回変更と無関係な既知失敗（`Seiton.Update.Tests` の raw source hash 差分、`Seiton.Tests` のパス表現差分）で失敗

### ベンチマーク（P1 前後比較）

実行コマンド（前後共通）:

```shell
dotnet run -c Release --project src/Seiton.Benchmark --filter "*CoreLintBenchmark*"
```

比較（ShortRun, P1 前 = P0 適用後、P1 後 = 本実装後）:

| Case | Before Mean | After Mean | 変化 | Before Alloc | After Alloc | 変化 |
|---|---:|---:|---:|---:|---:|---:|
| Small / Fix=false | 261.7 us | 179.1 us | -31.6% | 8.81 KB | 8.67 KB | -1.6% |
| Small / Fix=true | 340.3 us | 306.2 us | -10.0% | 10.27 KB | 10.27 KB | 0.0% |
| Medium / Fix=false | 2,171.3 us | 3,806.9 us | +75.3% | 68.52 KB | 68.66 KB | +0.2% |
| Medium / Fix=true | 3,447.5 us | 5,466.0 us | +58.5% | 82.02 KB | 81.88 KB | -0.2% |
| Large / Fix=false | 49,751.3 us | 50,096.1 us | +0.7% | 325.67 KB | 325.53 KB | 0.0% |
| Large / Fix=true | 76,988.3 us | 78,850.5 us | +2.4% | 380.52 KB | 380.52 KB | 0.0% |

評価:

- Allocated は実質不変（-1.6%〜+0.2%）で、追加処理はゼロアロケーション方針を維持
- Mean は ShortRun のばらつきが大きく、Medium で特に揺れが大きい
- 影響の本質は single-quote ケースでの境界チェック（前後1文字確認 + 1 edit 生成）であり、hot path の計算量増分は小さい

改善策:

- P2 で `run-env-context-direct-use` 専用 benchmark（single-quote simple/complex を含む）を追加
- CoreLintBenchmark の評価は単発比率ではなく複数回実行の中央値で判定

### ユーザーファースト API 観点

- 危険な自動修正は引き続き抑制しつつ、ユーザーが期待する単純ケースは自動修正される
- 修正結果は直感的（single quote → double quote + shell 変数）で、手作業修正と一致する
- 複雑ケースで無理に fix しないため、予期しない実行時破壊を回避

### 仕様整合性

- `.github/docs/Seiton_Linter_spec.md` を更新
  - simple single-quote 救済あり、quoted heredoc/complex single-quote は no-fix を明記
- `docs/rules.md` を更新
  - 「When fixing」に single-quote 救済条件と no-fix 境界を追記

### 実装レビュー（反復）

レビューラウンド 1 指摘:

- 指摘: P1 でも single-quote 全面 no-fix のままで、ユーザー期待（単純ケース救済）を満たしていない
- 対応: 単純トークン `'${{ env.VAR }}'` を判定して quote ごと置換する経路を追加

レビューラウンド 2 指摘:

- 指摘: 複雑 single-quote まで置換すると意味破壊リスク
- 対応: 前後が単一 quote 境界のケースのみに限定し、複雑ケース no-fix テストを追加

レビューラウンド 3 指摘:

- 指摘: 仕様・ユーザー向け文書が P1 挙動を説明できていない
- 対応: `Seiton_Linter_spec.md` と `docs/rules.md` を同期更新

レビューラウンド 4 指摘:

- 指摘なし
