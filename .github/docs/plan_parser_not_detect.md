# plan_parser_not_detect

## 背景

以下の Workflow 断片は YAML マッピングとして不正です（同一レベルで `env` が重複）。

```yaml
on:
  workflow_dispatch:

jobs:
  action-pin-samples:
    permissions:
      contents: read
    timeout-minutes: 10
    runs-on: ubuntu-24.04
    steps:
      - run: foo "$FOOBAR"
        env:
          FOOBAR: "foobar"
        env:
          PIYOPIYO: "piyopiyo"
```

実際の出力は `0 issues in 1 file` となり、検出漏れが発生している。

## 調査結果

1. 再現確認
- `samples/parser/.github/workflows/test.yaml` で `seiton -vv .github/workflows/test.yaml` を実行すると、`0 diagnostics` と表示される。

2. 診断集約経路は欠落していない
- `LintEngine.Check` は parse diagnostics を捨てておらず、fatal parse 時も diagnostics を返す設計。
- したがって今回の `0 issues` は「表示前に落ちる」のではなく「パース側で診断が作られていない」ことが主因。

3. 直接原因
- `WorkflowParser.Steps.ParseStep` には、step-level 既知キー（`run`, `uses`, `name`, `if`, `with`, `shell`, `working-directory`, `timeout-minutes`, `continue-on-error`, `env`）の重複検出がない。
- 同一 step 内で `env` が2回出現しても、2回目で `envNode` が上書きされるだけで、diagnostic が発行されない。
- 一方で、workflow root/job/container/on.* など多くのセクションは duplicate key を検出済みであり、steps だけ抜けている。

4. parser error という語の整理
- 今回は VYaml 例外（fatal parse error）ではなく、Seiton の構文診断（syntax-check 相当）として扱うべきケース。
- 現仕様の「最後まで検出をあきらめない」にも整合する（fatal terminate ではなく、診断追加して継続する）。

## 優先度付き対応一覧

## P0: 必須（検出漏れを塞ぐ）

1. `ParseStep` に duplicate key 検出を追加
- 方式: 既存セクションと同様に bit mask（`ulong seen`）で step-level known keys の重複を判定。
- 診断: 例 `jobs.'action-pin-samples'.steps[1] key "env" is duplicated. previously defined at line:X,col:Y`。
- 動作: 重複を検出しても処理は継続し、重複側 value は `SkipCurrentNode()` でスキップ。fatal 化しない。
- 意図: 「走査をターミネートしない」要件を満たしつつ、検出漏れを解消。

2. 回帰テスト追加（Parser）
- `tests/Seiton.Core.Tests/ParserTests.cs` に、step-level duplicate `env` の再現テストを追加。
- 期待: duplicate diagnostic が 1 件以上出ること、`HasFatalError` は false のままであること。

3. 回帰テスト追加（CLI 表示）
- `tests/Seiton.Tests` 側に、同入力で summary が `0 issues` にならないことを担保するテストを追加。
- 期待: summary に error 件数が反映されること。

## P1: 高（ユーザー理解の改善）

1. duplicate `env` 向け Help メッセージの追加
- 例: 「YAML mapping key は一意である必要があります。`env` は1つにまとめてください。」
- 目的: 何をどう直せばよいかを明示し、ヒント要件を満たす。

2. 診断文言の統一
- 他セクションの duplicate key 文言と同じトーン/フォーマットに合わせる。
- 目的: 既存ルール検出と同じ読み心地に寄せる。

## P2: 中（運用耐性の向上）

1. 同一 step での複数重複ケース網羅
- `env` 以外（`run`, `uses`, `with`, `shell` など）でも duplicate が検出されることをテスト。

2. non-fatal 継続検証
- duplicate key があっても、同ファイル内の後続 rule diagnostics が引き続き出ることを検証。

## 受け入れ基準

1. 提示サンプルで `seiton` 実行時に duplicate `env` がエラーとして表示される。
2. summary が `0 issues` ではなく、error 件数を表示する。
3. duplicate 検出後も処理継続し、同ファイルの他問題も報告できる。
4. 追加テストが通過し、既存テストを壊さない。

## 実装時の注意

- fatal parse error へ昇格しない。
- duplicate key の扱いは steps 以外の既存実装に合わせる（first-win + duplicate 側 skip が自然）。
- パフォーマンス回帰を避けるため、既存と同等の軽量判定（bit mask）を使う。
