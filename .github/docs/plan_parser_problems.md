# Parser 問題対応メモ / 実装計画

> 作成日: 2026-05-23
> 対象: 途中まで parser diagnostics が出ている壊れた YAML で、最終的に `yaml parse failure` 1 件だけになってしまう問題

---

## 1. 問題概要

Seiton は原則として、**1つのエラーで判定を止めず、同一ファイル内で取得できる診断をできるだけ保持する**。

しかし、壊れた YAML の一部では次のような挙動になっていた。

- parser が途中まで `unexpected key` などの診断を追加する
- その後で VYaml / adapter 側の fatal parse exception が発生する
- それまでに蓄積していた parser diagnostics が実質的に失われる
- ユーザーには `yaml parse failure` だけが見える

この挙動は Seiton の multi-error recovery 方針とずれているため、修正が必要。

---

## 2. 再現例

以下のような workflow を与えると、`branch` が不正キーであることに加え、後段で YAML parse failure が発生する。

```yaml
on:
  push:
    branch: main
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - run: echo "Title: ${{ github.event.pull_request.title }}"
      - uses: actions/checkout@v6
```

期待する結果:

- `on.push` の `branch` に対する parser diagnostic を保持する
- その後の fatal `yaml parse failure` も報告する
- fatal parse だからといって `"on" section is missing` や `"jobs" section is missing` のような未観測の補完診断を捏造しない

---

## 3. 原因整理

今回の直接原因は 1 箇所ではなく、以下の複合要因だった。

### 3.1 Structural hints prepass が先に落ちる

`ParseClassified(...)` は本体 parse の前に `TryReadRootStructuralHints(...)` を使って document kind のヒントを読む。

この prepass 自体が壊れた YAML で例外を投げると、本体 parse の recovery ロジックに到達する前に処理が崩れる。

### 3.2 Fatal parse exception 時に earlier diagnostics を保持する経路が弱い

本体 parse 中に例外が出た場合、fatal error として扱う必要はあるが、**それ以前に追加済みの diagnostics を落とさない**必要がある。

### 3.3 Fatal 後に adapter 依存の後処理を続けると状態が不安定になる

unused anchor / recursive alias の後処理は adapter の正常終了を前提にしている。fatal parse 後にこれを続けると、追加で状態を壊したり、意図しない診断に繋がる可能性がある。

---

## 4. 対応方針

以下をこの問題の修正方針とする。

1. Structural hints prepass の失敗は classify の失敗にしない
2. 本体 parse の fatal exception は `yaml parse failure` として診断化する
3. その際、既に蓄積済みの parser diagnostics は保持する
4. fatal parse 後は adapter 依存の後処理を行わない
5. incremental parse でも同じ方針を適用する
6. parser spec / csharp spec / regression tests を合わせて更新する

---

## 5. 実施した修正

### 5.1 `ParseClassified(...)` の prepass を fail-open に変更

`TryReadRootStructuralHints(...)` が例外を投げても parser 全体を止めず、`hasHints = false` として本体 parse に進めるようにした。

狙い:

- classify 用の補助処理が recovery を妨げないようにする
- 壊れた YAML でも main parse 側で本来の診断を回収できるようにする

### 5.2 `ParseCore(...)` で fatal parse exception を診断化

`ParseCoreInner(...)` 呼び出しを `try/catch` で包み、例外時は以下を行うようにした。

- `yaml parse failure: ...` を error diagnostic として追加
- 可能なら例外メッセージから line / column を抽出
- `HasFatalError = true` の `ParseCoreResult` を返す
- 既存 diagnostics はそのまま保持する

### 5.3 `ParseIncremental(...)` にも同じ fatal recovery を適用

通常 parse と incremental parse で回復方針がズレないよう、incremental 側にも同等の fatal handling を入れた。

### 5.4 Fatal 時は anchor 後処理をスキップ

`GetUnusedAnchors(...)` と `GetRecursiveAliases(...)` は fatal parse 後には実行しないようにした。

狙い:

- 不安定な adapter 状態に追加で触れない
- 途中の fatal parse と無関係な副作用を増やさない

---

## 6. 追加した回帰テスト

### 6.1 earlier diagnostics を保持するテスト

`Parse_BrokenYaml_AfterEarlierParserDiagnostic_PreservesEarlierDiagnostics`

確認内容:

- `HasFatalError == true`
- `unexpected key "branch"` が残る
- `yaml parse failure` も出る

### 6.2 fatal parse で missing section を捏造しないテスト

`Parse_BrokenYaml_FatalParseDoesNotInventMissingSections`

確認内容:

- fatal parse になる
- `yaml parse failure` は出る
- `"on" section is missing in workflow` は出ない
- `"jobs" section is missing in workflow` は出ない

---

## 7. 仕様反映

以下の spec を今回の挙動に合わせて更新した。

- `Seiton_Parser_spec.md`
- `Seiton_Parser_csharp_spec.md`

更新内容:

- YAML parse failure 時は単に `Diagnostic[]` に落とし込むのではなく、**同一ファイル内でそれ以前に出ていた parser diagnostics を保持したまま** fatal diagnostic を追加する
- AST は partial または null になり得る

---

## 8. 検証結果

### 8.1 Focused regression tests

- `Parse_BrokenYaml_*` スライス: 4 passed / 0 failed

### 8.2 Core test suite

- `tests/Seiton.Core.Tests`: 1609 passed / 0 failed

### 8.3 CLI 実挙動確認

壊れた YAML を CLI に流して、少なくとも次の 2 件が同時に見えることを確認した。

1. `on.push has unexpected key "branch" ...`
2. `yaml parse failure: Mapping values are not allowed in this context ...`

### 8.4 Benchmark

- benchmark 自体は完走
- 少なくとも allocation の増加は見えていない
- ただし、手元比較に使える parser baseline report は runtime / SDK 条件が揃っておらず、厳密な退行判定には不向き

---

## 9. 残課題

### 9.1 Benchmark baseline の揃え直し

同一 SDK / runtime / benchmark settings で parser benchmark の baseline を取り直し、今回の変更の純粋な差分を見やすくする。

### 9.2 他の prepass / post-process でも同種の崩れがないか点検

今回の問題は「本体 parse 以外の補助処理が recovery を壊す」類型だったため、将来的には以下も点検対象にする。

- classify 前後の補助走査
- fatal 後にだけ不安定になり得る adapter 依存処理
- incremental parse と direct parse の挙動差

---

## 10. 完了条件

本件は以下を満たしたら完了とする。

- [x] 壊れた YAML でも earlier parser diagnostics が保持される
- [x] fatal `yaml parse failure` も併記される
- [x] missing section などの不正な補完診断を追加しない
- [x] direct parse / incremental parse の方針を揃える
- [x] regression tests を追加する
- [x] parser spec / csharp spec を更新する
- [x] focused tests を通す
- [x] core tests を通す
- [ ] benchmark baseline を同条件で再整理する

---

## 11. 今回の結論

今回の修正で、Seiton の parser は「fatal parse failure が起きても、それ以前に確定していた parser diagnostics を捨てない」という原則に沿うようになった。

本件の本質は YAML 例外そのものではなく、**prepass / main parse / post-process の境界で recovery 方針が分断されていたこと**にある。以後は parser の補助処理も含めて、fatal recovery を一貫した contract として扱う必要がある。
