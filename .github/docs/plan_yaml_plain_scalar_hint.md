# YAML Plain Scalar Fatal Error 補助ヒント — 調査結果と対応計画

> 作成日: 2026-05-23
> 対象: `run:` / `script:` の plain scalar に `: ` が含まれたとき、YAML fatal parse だけで終わる問題に対して、説明的な補助診断を追加するべきかの検討

## 実装結果

> 実装日: 2026-05-23
> 方針B を P1 まで実装完了

### 実装内容

- `WorkflowParser.PlainScalarHint.cs` (新規): fatal YAML parse 後のヒューリスティック検出ロジック
- `ParseCore` / `ParseClassified` / `ParseIncremental` の catch ブロックにヒント生成を統合
- `AddFatalParseError` ヘルパー追加 (Help フィールド対応)
- Diagnostic の既存 `Help` フィールドを活用 (全出力形式で自動表示)
- レビュー反映: YAML node property (`&anchor`, `!tag`) をスキップし、inline comment を colon 判定対象から除外
- レビュー反映: `run: # reason: ...` のような empty/comment-only value ではヒントを出さないよう修正

### ベンチマーク結果 (CoreParsingBenchmark)

ヒントロジックはエラーパスのみで実行されるため、正常パースへの性能影響はゼロ。

| Size | Before (Mean) | After (Mean) | Change | Allocated |
|------|--------------|-------------|--------|-----------|
| Small | 45.9 us | 46.7 us | +1.7% (ノイズ範囲) | 3.87 KB = 同一 |
| Medium | 1,085 us | 1,021 us | -5.9% (ノイズ範囲) | 35.59 KB = 同一 |
| Large | 19,212 us | 18,352 us | -4.5% (ノイズ範囲) | 180.04 KB = 同一 |

性能変化なし。ヒント検出は catch ブロック内でのみ実行され、通常の成功パスに影響を与えない。

### テスト追加

16 テスト追加 (4 positive + 12 negative):
- `Parse_PlainScalarColonHint_RunWithColonSpace_ReturnsHint` — `run:` + `: ` → ヒントあり
- `Parse_PlainScalarColonHint_ScriptWithColonSpace_ReturnsHint` — `script:` + `: ` → ヒントあり
- `Parse_PlainScalarColonHint_RunWithBareColon_ReturnsHint` — `run: foo: bar` → ヒントあり
- `Parse_PlainScalarColonHint_SingleQuoted_NoHint` — quoted → ヒントなし (正常パース)
- `Parse_PlainScalarColonHint_BlockScalar_NoHint` — block scalar → ヒントなし (正常パース)
- `Parse_PlainScalarColonHint_PlainScalarWithoutColon_NoHint` — `: ` なし → ヒントなし
- `Parse_PlainScalarColonHint_UnrelatedFatalYaml_NoHint` — 無関係な fatal → ヒントなし
- `Parse_PlainScalarColonHint_InlineCommentColonSpaceWithUnrelatedFatal_NoHint` — inline comment 中の `: ` → ヒントなし
- `Parse_PlainScalarColonHint_EmptyRunValueWithCommentColonSpaceAndUnrelatedFatal_NoHint` — empty/comment-only value → ヒントなし
- `Parse_PlainScalarColonHint_AnchoredQuotedScalarWithUnrelatedFatal_NoHint` — `&anchor` + quoted → ヒントなし
- `Parse_PlainScalarColonHint_TaggedQuotedScalarWithUnrelatedFatal_NoHint` — `!!tag` + quoted → ヒントなし
- `TryGetPlainScalarColonHint_AnchoredQuotedScalar_ReturnsNull` — ヒューリスティック単体: `&anchor` + quoted → null
- `TryGetPlainScalarColonHint_TaggedQuotedScalar_ReturnsNull` — ヒューリスティック単体: `!!tag` + quoted → null
- `TryGetPlainScalarColonHint_InlineCommentColonSpace_ReturnsNull` — ヒューリスティック単体: inline comment 中の `: ` → null
- `TryGetPlainScalarColonHint_EmptyValueCommentColonSpace_ReturnsNull` — ヒューリスティック単体: empty/comment-only value → null
- `TryGetPlainScalarColonHint_RunThreeLinesAboveOffset_ReturnsHint` — ヒューリスティック単体: error line の 3 行上でもヒントあり

全 1946 テスト通過。

---

## 1. 問題設定

以下のような記述は、GitHub Actions の文脈では自然に見えるが、YAML としては壊れている。

```yaml
- run: echo "Title: ${{ github.event.pull_request.title }}"
```

`run:` の右辺は YAML plain scalar として解釈されるが、plain scalar 中の `: ` は mapping value indicator と衝突するため、YAML reader が fatal parse error を返す。

ユーザー視点では次の問題がある。

- `yaml parse failure` という低レベルなメッセージだけでは原因が分かりにくい
- 「GitHub Actions の expression が悪い」のか「YAML の quoting が悪い」のか判別しづらい
- `run:` / `script:` で非常に起きやすい典型ミスである

---

## 2. 調査結果

### 2.1 根本原因

原因は expression ではなく YAML syntax にある。

- `Title: ` の `: ` が plain scalar として不正
- YAML adapter / library が event stream を最後まで返せず fatal parse になる
- parser 本体は当該値を正常な scalar node として受け取れない

したがって、これは expression validation の問題ではない。

### 2.2 現在の責務境界で難しい点

Seiton の parser は YAML adapter が返した event stream を前提にしている。

このケースでは adapter が fatal で止まるため、parser 側には以下が確定情報として存在しない。

- その行が `run:` の値だったこと
- 値全体の scalar 範囲
- `:` が plain scalar 内にあったこと

つまり、**通常の parser recovery としてこの値を救済するのは難しい**。

### 2.3 それでも可能なこと

fatal parse 後に、元の UTF-8 YAML と line/column を使って限定的なヒューリスティックを走らせることは可能。

具体的には次のような補助判定は現実的である。

1. fatal location の行を取得する
2. その行または直近行が `run:` / `script:` を含むか確認する
3. 右辺が plain scalar っぽく、`"..."` や `|` / `>` では始まっていないことを確認する
4. 右辺に `: ` が含まれていることを確認する
5. 一致した場合のみ補助ヒントを追加する

これは recovery ではなく、**fatal parse に対する explanatory hint** として扱うのが自然。

---

## 3. 非ゴール

このタスクでは以下は目指さない。

- 壊れた YAML を parser が自動修復して継続解析すること
- 一般 YAML 全体に対する包括的な syntax repair
- YAML library 側の parser behavior 改変
- `run:` 以外の全 scalar について網羅的に colon misuse を推定すること

---

## 4. 方針候補

### 方針 A — 何もしない

`yaml parse failure` のみを返す。

利点:

- 実装コストがゼロ
- false positive を増やさない
- parser の責務を広げない

欠点:

- ユーザー体験が悪い
- Playground / README / issue 報告で繰り返し混乱を招く
- 典型ミスなのに補助情報がない

### 方針 B — `run:` / `script:` 限定の補助ヒントを追加

fatal parse はそのまま維持しつつ、条件が一致したときだけ説明メッセージを追加する。

候補メッセージ:

```text
run/script の plain scalar に `: ` が含まれているため YAML として解釈できない可能性があります。値全体を引用するか block scalar (`|`) を使ってください。
```

利点:

- ユーザー価値が高い
- 影響範囲を狭く保てる
- recovery を伴わないので既存 parser 設計と衝突しにくい

欠点:

- ヒューリスティックなので誤判定リスクがある
- fatal location と実際の原因行がずれるケースがある

### 方針 C — 一般 scalar への広域ヒント化

`run:` / `script:` に限らず plain scalar 全体に対して colon misuse を推定する。

利点:

- ルールとしては一般的

欠点:

- false positive リスクが大きい
- YAML syntax の局所推定としては過剰
- 説明責任が難しい

---

## 5. 推奨方針

**推奨は方針 B**。

理由:

- これはユーザーの典型的な GitHub Actions authoring mistake である
- しかし parser recovery に踏み込む必要はない
- `run:` / `script:` 限定ならヒントの precision を保ちやすい

Seiton の契約としては、fatal YAML error を fatal YAML error のまま返すべきであり、補助ヒントはその上に積むのが妥当。

---

## 6. 優先度別の対応策

### P0 — 調査 / 再現固定

- 最低 5 つ程度の再現パターンを整理する
- `run:` / `script:` / quoted scalar / block scalar / plain scalar の差を明文化する
- fatal location が原因行からずれるケースを確認する

完了条件:

- false positive / false negative の代表例が洗い出されている

### P1 — 補助ヒントの最小実装

- fatal parse 時のみ発火
- `run:` / `script:` の plain scalar colon misuse に限定
- 追加診断は explanation only とし、parse continuation はしない
- 可能なら修正例を diagnostics 文面に含める

候補修正例:

```yaml
- run: 'echo "Title: ${{ github.event.pull_request.title }}"'
```

または

```yaml
- run: |
    echo "Title: ${{ github.event.pull_request.title }}"
```

完了条件:

- 典型例で explanatory hint が出る
- fatal parse の既存挙動は変えない

### P2 — Playground / docs 連携

- Playground のサンプルから同種の誤記を除去する
- docs/README の例も同様に見直す
- 必要なら FAQ 的な短い説明を docs に追加する

完了条件:

- Seiton 自身のサンプルが誤誘導しない

### P3 — 一般化の再評価

- `name:` / `env:` / `with:` など他の frequent mistake へヒントを広げるか検討する
- ただし `run:` / `script:` で十分な効果が出るまでは広げない

完了条件:

- 誤判定率とユーザー価値のバランスが取れると判断できた場合のみ拡張する

---

## 7. 想定テスト観点

この task を将来実装する場合、少なくとも以下の等価クラスを押さえる必要がある。

| ケース | 例 | 期待 |
|---|---|---|
| plain scalar + `: ` + `run:` | `run: echo "Title: x"` | fatal + 補助ヒント |
| plain scalar + `: ` + `script:` | `script: console.log("A: b")` | fatal + 補助ヒント |
| single-quoted scalar | `run: 'echo "Title: x"'` | 補助ヒントなし |
| block scalar | `run: |` | 補助ヒントなし |
| plain scalar without `: ` | `run: echo hello` | 補助ヒントなし |
| unrelated fatal YAML line | 別要因の malformed YAML | 補助ヒントなし |

特に重要なのは「fatal parse だがこのヒントは出すべきでない」negative case を十分に取ること。

---

## 8. 仕様影響

この task は parser/linter の本体契約を大きく変えない。

更新が必要になるとすれば次のレベル。

- `Seiton_Parser_spec.md`: fatal YAML parse 時に、限定的な explanatory hint を追加し得ることを補足
- `Seiton_Parser_csharp_spec.md`: C# 実装での heuristic 条件を記述
- 必要なら `Seiton_spec.md`: parser entrypoint の fatal diagnostics augmentation を短く触れる

一方で、AST 生成 contract や linter rule catalog の変更は不要。

---

## 9. 推奨結論

この問題は「壊れた YAML を recover できるか」ではなく、「recover できない fatal parse に対して、十分に限定された補助ヒントを足すべきか」で考えるべきである。

Seiton では、**fatal parse は維持しつつ、`run:` / `script:` に特化した explanatory hint を P1 として導入する**のが最も費用対効果がよい。
