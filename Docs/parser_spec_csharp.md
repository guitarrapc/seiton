# seiton C# YAML パーサー仕様案

## 1. 目的

本仕様は、**Go の actionlint 型パーサーアプローチを、C# + VYaml で再構成するための実装案** を定義する。

対象は GitHub Actions workflow YAML であり、単なる YAML デシリアライズではなく、以下を同時に満たすことを目的とする。

1. 低アロケーションでの YAML 解析
2. 構文スキーマ検証と AST 構築の同時実行
3. 複数エラー回復
4. 行・列付き診断生成
5. 後段の semantic / policy rule engine へ適した AST 提供

## 2. 基本方針

### 2.1 採用方針

パーサーは **hand-written recursive descent style parser** とする。

ただし、一般的な式パーサーのようなトークン列に対する再帰下降ではなく、**YAML ストリームアダプター越しにイベントまたは node を読む構造化パーサー** とする。

設計思想は actionlint に近い。

1. YAML の shape を直接検証する
2. 許可キーをコードで持つ
3. 必須キー、排他キー、条件付き必須をコードで表現する
4. 可能な範囲で解析継続し、複数診断を返す

### 2.2 採用しない方針

以下は採用しない。

1. POCO への全面デシリアライズ
2. JSON Schema を主判定に使う構成
3. `Dictionary<string, object>` ベースの動的モデル
4. 1 エラーで解析停止する fail-fast parser

## 3. なぜ Go/actionlint パターンを移植するのか

Go/actionlint から移植したい本質は、Go の構文ではなく次の責務分割である。

1. **YAML 低レベル API の直接走査**
2. **構文検証しながら AST を構築する**
3. **診断位置を YAML ノードの位置から引く**
4. **後段ルールを Visitor へ分離する**
5. **可変仕様は generated data へ逃がす**

この分割は C# でもそのまま有効であり、VYaml に置き換えることで YAML 解析フェーズのアロケーションをより下げられる可能性がある。

## 3.1 actionlint / ghalint / zizmor から何を採るか

本仕様は actionlint だけを参照しているわけではない。3 つの先行例から、それぞれ異なる層の知見を採る。

### actionlint から採るもの

1. hand-written parser で shape を直接検証する方針
2. 構文検証と AST 構築を同時に行う責務分割
3. エラー回復しながら複数 diagnostics を返す設計
4. 後段 semantic / policy rule を Visitor に分離する構造

### ghalint から採るもの

1. ポリシー対象に必要なモデルだけを明確に絞る姿勢
2. 設定ファイルとポリシー除外の運用単純さ
3. 実装を過剰に抽象化しない方針

ただし ghalint のような struct 直デコード中心のパーサー方針は、本プロジェクトでは採らない。理由は、未知キー検出、厳密な shape 検証、複数エラー回復、細粒度な位置情報に不利だからである。

### zizmor から採るもの

1. 補助的な schema validation を併用する現実的な割り切り
2. 生成済みデータや外部仕様差分の自動同期方針
3. 診断出力と後段 rule engine の責務分離

ただし zizmor のように Serde / model deserialize を中核に置く構成は、本プロジェクトの parser 中核には採らない。これは actionlint 型の hand-written parser のほうが shape 検証に向くためである。

## 4. VYaml の使い方

### 4.1 推奨 API レベル

最初の実装では、次の優先順位で API を使う。

1. **VYaml adapter 経由の event / tokenizer ベース**
2. 必要なら **node ラッパー層** を adapter 配下に自前で作る
3. VYaml の高水準デシリアライズ API は使わない

理由は以下。

1. 位置情報と shape 制御を自前で保持したい
2. 中間オブジェクト生成を最小化したい
3. `scalar or mapping`、`scalar or sequence` など GitHub Actions 固有の多態性を自前制御したい
4. scalar を `string` に変換せず `ReadOnlySpan<byte>` / slice 情報のまま扱いたい

### 4.2 自前で追加する抽象化

VYaml の低レベル API をそのまま各 parse 関数で扱うのではなく、**parser 本体から VYaml を隠蔽する adapter 層** を置く。

```text
Utf8YamlTokenizer / Parser
    -> VYamlSyntaxAdapter
    -> IYamlStreamReader
    -> YamlCursor
    -> YamlEventReader
  -> ParseContext
  -> WorkflowParser
```

#### `IYamlStreamReader`

責務:

1. parser 本体が依存する最小の YAML 読み取り契約を定義する
2. VYaml 差し替え時に parser 本体を保護する
3. テスト用 fake reader を差し込み可能にする

想定 API:

```csharp
internal interface IYamlStreamReader
{
    YamlEventKind CurrentKind { get; }
    bool Read();
    ReadOnlySpan<byte> GetScalarUtf8();
    Utf8Slice GetScalarSlice();
    TextPosition GetStart();
    TextPosition GetEnd();
    bool TryEnterMapping();
    bool TryEnterSequence();
    void SkipSubtree();
}
```

#### `VYamlSyntaxAdapter`

責務:

1. VYaml の tokenizer / parser API を `IYamlStreamReader` に変換する
2. VYaml 固有の event kind や position 取得方法を吸収する
3. parser 本体が VYaml 型を参照しなくて済むようにする

#### `YamlCursor`

責務:

1. 現在イベントの kind 参照
2. scalar 値取得
3. 行・列取得
4. mapping / sequence への enter / exit
5. skip subtree

#### `YamlEventReader`

責務:

1. 先読み
2. `TryReadScalarKey`
3. `ReadRequiredScalar`
4. `SkipCurrentValue`
5. mapping / sequence の終端確認

この層分割により、以下の利点がある。

1. parser 本体が VYaml から独立する
2. 将来 `YamlDotNet` や別実装へ差し替えるときの変更箇所を adapter に閉じ込められる
3. テストで最小イベント列を直接流し込める

### 4.3 adapter を入れる理由

VYaml を生で各 parse 関数から触る設計は、短期的には書きやすく見えても長期保守に弱い。

問題は次のとおり。

1. VYaml の event API 変更が parser 全体へ波及する
2. テストが VYaml の詳細仕様に引きずられる
3. parser 自体の責務と YAML ライブラリ吸収責務が混ざる

したがって、本仕様では **VYaml 生 APIを parser 本体へ露出しない** ことを原則とする。

## 5. パーサー全体構造

### 5.1 エントリポイント

```csharp
public static ParseResult ParseWorkflow(ReadOnlySpan<byte> utf8Yaml, string filePath)
```

戻り値 `ParseResult` は次を持つ。

1. `Workflow? Workflow`
2. `ImmutableArray<Diagnostic> Diagnostics`
3. `bool HasFatalError`

`Workflow` が null でも diagnostics は返す。

### 5.2 ParseContext

`ParseContext` という heap object は使わず、**stack-only の parser state** を使う。

```csharp
internal ref struct ParserState
{
    public ReadOnlySpan<byte> Utf8Source { get; }
    public SourceText SourceText { get; }
    public YamlCursor Cursor { get; }
    public DiagnosticBuffer Diagnostics { get; }
    public NodeArena Arena { get; }
    public ParserLimits Limits { get; }
}
```

責務:

1. 診断追加
2. 現在位置の取得
3. source snippet 解決
4. skip / recover 補助
5. arena へのノード追加

`class` にしない理由は、生成時アロケーションを避けるためである。parser の実行状態は stack 上に置けるため、`ref struct` が適している。

## 6. AST 設計

### 6.1 基本方針

AST は **typed + range-attached + arena-backed** とする。

1. 全主要ノードが `SourceRange` を持つ
2. key range と value range を分けられる場合は分ける
3. scalar 値は原則 `string` ではなく `Utf8Slice` で保持する
4. ノード間参照は object 参照ではなく index / range で表現する
5. ホットパスで不要な情報は持たない

重要なのは、**`ReadOnlySpan<byte>` / `ReadOnlySpan<char>` は読むために使い、保持には使わない** ことである。

`Span<T>` は stack-only なので、戻り値 AST や heap 上のオブジェクトに長期保持できない。したがって AST には `Span<T>` を埋め込まず、元ソースへの offset/length だけを保持する。

### 6.2 例

```csharp
public readonly record struct SourceRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public readonly record struct Utf8Slice(int Offset, int Length)
{
    public ReadOnlySpan<byte> GetSpan(ReadOnlySpan<byte> source)
        => source.Slice(Offset, Length);
}

public readonly record struct WorkflowNode(
    SourceRange Range,
    Utf8Slice Name,
    Utf8Slice RunName,
    int OnNodeId,
    int JobsStart,
    int JobsCount);

public readonly record struct StringAtom(
    Utf8Slice Slice,
    SourceRange Range);
```

### 6.3 AST の持ち方の原則

1. heap object の木構造を作らない
2. ノードは原則 `readonly struct` で持つ
3. 可変長の子要素は arena 内 contiguous buffer + start/count で持つ
4. scalar は原則 `Utf8Slice` で持ち、必要時のみ文字列化する
5. semantic rule に必要ない raw 情報は保持しない

### 6.4 AST を struct にする理由

`Workflow` や `StringNode` を `class` にすると、ノード数に比例してヒープアロケーションが発生する。zero-alloc 指向では不利である。

そのため、AST は次の構成を推奨する。

```text
NodeArena
  - WorkflowNode[]
  - JobNode[]
  - StepNode[]
  - EventNode[]
  - StringAtom[]
```

必要なら `ArrayPool<T>` を使って backing storage を再利用する。

## 7. parse 関数の責務分割

### 7.1 トップレベル

```text
ParseWorkflow
  -> ParseWorkflowMapping
      -> ParseName
      -> ParseRunName
      -> ParseOn
      -> ParsePermissions
      -> ParseEnv
      -> ParseDefaults
      -> ParseConcurrency
      -> ParseJobs
```

### 7.2 job 以下

```text
ParseJobs
  -> ParseJob(id)
      -> ParseRunsOn
      -> ParseSteps
      -> ParseUses
      -> ParseNeeds
      -> ParseStrategy
      -> ParseContainer
      -> ParseServices
      -> ParseIf
      -> ParsePermissions
```

### 7.3 step 以下

```text
ParseStep
  -> ParseRun
  -> ParseUses
  -> ParseWith
  -> ParseEnv
  -> ParseIf
  -> ParseName
  -> ParseId
  -> ParseTimeoutMinutes
```

## 8. shape 検証の実装方針

### 8.1 許可キー管理

各 mapping ごとに、許可キーセットを static な生成済みテーブルとして持つ。

```csharp
private static readonly FrozenSet<string> WorkflowKeys =
[
    "name",
    "run-name",
    "on",
    "permissions",
    "env",
    "defaults",
    "concurrency",
    "jobs",
];
```

未知キーは即診断するが、対応する value は skip して解析継続する。

### 8.2 必須キー検証

mapping を走査しながら bit flag で出現キーを記録し、終了時に不足キーを診断する。

```text
seenOn
seenJobs
seenRunsOn
seenSteps
seenUses
```

### 8.3 相互排他と条件付き必須

job / step 終了時にまとめて判定する。

例:

1. job は `uses` と `steps` を同時に持てない
2. reusable workflow job では `runs-on` を持てない
3. run step は `uses` を同時に持てない

この手の制約は parse 完了後にそのノード単位で診断する。

## 9. エラー回復戦略

### 9.1 基本方針

1. 可能な限り解析継続する
2. subtree 単位で skip できるようにする
3. mapping / sequence の境界を壊さない

### 9.2 recover パターン

#### パターン A: 未知キー

1. key を診断
2. value subtree を skip
3. 次の key へ進む

#### パターン B: 型不一致

1. value の開始位置を診断
2. 期待型が scalar なら subtree を skip
3. 親 mapping / sequence に復帰

#### パターン C: 致命的な構造崩壊

1. 現在ノードの subtree を全部 skip
2. 親レベルへ復帰
3. それ以上継続不可能なら fatal とする

### 9.3 DiagnosticBag

診断の格納先は append-only bag とし、パース中に例外を投げない。

```csharp
internal sealed class DiagnosticBag
{
    public void Add(Diagnostic diagnostic);
}
```

## 10. Span と位置情報

### 10.1 必須要件

1. 行・列を必ず取る
2. key range と value range を区別できる箇所は区別する
3. end position が取れない場合でも start position は正確に出す

### 10.2 推奨方針

初期版は `StartLine`, `StartColumn` を最優先し、`EndLine`, `EndColumn` は推定でもよい。

`SourceText` を使って end を推定する。

```text
start = token start
end   = scalar length or subtree end event から推定
```

### 10.3 どこを primary にするか

1. 未知キー: key
2. 型不一致: value
3. 排他違反: 主因を primary、相手を related
4. 必須キー欠落: 親 mapping の span

## 11. 低アロケーション方針

### 11.1 原則

1. UTF-8 入力は `ReadOnlySpan<byte>` で受ける
2. scalar 比較は可能なら span 比較で行う
3. AST には `Span<T>` を保持せず `Utf8Slice` を保持する
4. 診断以外で一時文字列を作らない
5. metadata lookup は generated static table を使う
6. adapter 層以外で VYaml 固有型を保持しない

### 11.2 string 化のタイミング

以下だけ string 化を許可する。

1. 診断メッセージに埋め込む表示用文字列
2. 診断メッセージに必要な表示用文字列
3. ハッシュキー化が必要で、かつ `Utf8Slice` のまま扱うコストが高い箇所
4. 後段 semantic rule が繰り返し参照し、interning の効果が見込める値

原則として、**AST 保持のために string 化しない**。

### 11.3 避けるべきこと

1. YAML 全体の DOM 構築
2. `Dictionary<string, object>` 化
3. `string.Split` や regex による後処理
4. parse 中の LINQ

## 12. 生成済みデータとの統合

parser 自体は generated data に強く依存しないが、以下の lookup で使う。

1. webhook event 名の妥当性
2. activity types の妥当性
3. expression context availability
4. special function names

初期段階では parser が shape だけ見る責務に寄せ、event 妥当性や availability は semantic rule 側へ寄せてもよい。

## 13. 出力モデル

```csharp
public sealed class ParseResult
{
    public NodeArena Arena { get; init; }
    public int WorkflowNodeId { get; init; }
    public ImmutableArray<Diagnostic> Diagnostics { get; init; }
    public bool HasFatalError { get; init; }
}
```

原則:

1. fatal でも diagnostics は返す
2. diagnostics があっても arena と root node id は可能な限り返す
3. semantic rule 側は壊れたノードを防御的に扱う

## 14. 実装順序

### Phase 1

1. `SourceText`
2. `Span`
3. `YamlCursor`
4. `DiagnosticBag`
5. `ParseWorkflow` と top-level key 検証

### Phase 2

1. `ParseJobs`
2. `ParseJob`
3. `ParseStep`
4. 必須 / 排他制約

### Phase 3

1. 式文字列抽出
2. event shape 詳細化
3. services / container / strategy / matrix

### Phase 4

1. parser ベンチマーク
2. allocation 計測
3. error recovery 強化
4. semantic rule との統合

## 15. 最終提案

C# + VYaml で Go/actionlint 型パーサーを実装する案は、十分に現実的である。特に次の組み合わせが重要である。

1. **VYaml の event/tokenizer ベース解析**
2. **hand-written shape parser**
3. **typed AST + Span**
4. **append-only diagnostics + subtree skip recovery**
5. **generated metadata の後段利用**

この構成なら、Go/actionlint の設計資産を活かしつつ、YAML 解析フェーズのアロケーションをより小さく抑える方向で C# 実装を組み立てられる。

---

## 実装反映メモ

### `on` の詳細パースは、actionlint の RuleEvents に寄せて次を実装済み。

1. event ごとの `types` を 3 モードで管理
2. filter の排他組み合わせ検証
3. unknown event 名の診断

#### 1 `types` の 3 モード

`types` は event ごとに次の 3 モードで扱う。

1. NotSupported: `types` 指定を禁止
2. Any: 任意文字列を許可（例: `repository_dispatch`）
3. Restricted: 許可済み activity type のみ許可

これにより、`push` のような `types` 非対応 event での誤設定と、`repository_dispatch` のカスタム type 許容を両立できる。

#### 2 filter 排他の検証

次の排他制約を parser で検証する。

1. `branches` と `branches-ignore`
2. `tags` と `tags-ignore`
3. `paths` と `paths-ignore`

`merge_group` を含む event ごとの filter 対応可否は event spec テーブルで管理する。

#### 3 unknown event 診断

`on` が scalar / sequence / mapping のどの表現でも、未定義 event 名は診断として報告する。

実装上の教訓:

1. `types` は汎用 option 検証より先に専用分岐へ流す必要がある
2. そうしないと「非対応 event での `types`」が「未知 option」に吸収され、誤った診断文言になる

### `jobs` shape 検証の追加実装

以下を parser 側で shape 検証済み。

1. `strategy` は mapping 必須
2. `strategy.matrix` は scalar または mapping
3. `matrix.include` / `matrix.exclude` は scalar または sequence
4. `container` は scalar または mapping（mapping の場合 `image` 必須）
5. `services` は mapping 必須、各 service の container shape を検証

### reusable workflow (`jobs.<id>.uses`) 制約の追加実装

`uses` job について、actionlint の制約に寄せて次を検証済み。

1. `runs-on`, `steps`, `container` など steps 側キーは `uses` と併用不可
2. `with`, `secrets` は `uses` がある job でのみ許可
3. `secrets` は mapping または scalar `inherit` のみ許可

### expression 抽出と最小 AST

初期版として次を実装済み。

1. scalar 文字列から `${{ ... }}` を抽出
2. 論理演算・比較演算・四則演算・単項演算の最小再帰下降パーサー
3. 識別子、member access、関数呼び出し、文字列/数値/bool/null のノード
4. 抽出 + パースを一体で走らせる API
5. wildcard access（`.*`, `[*]`）と index access（`['key']`, `[0]`）

補足:

1. これは最小 AST であり、actionlint の expression semantics 相当の型検証や context availability は未実装
2. 後段で availability table と関数シグネチャ検証を接続する前提

### actionlint testdata の期待診断ベース比較

smoke test からの昇格として、`testdata/err` の一部 fixture で期待診断文字列を照合するテストを追加済み。

1. `empty.yaml`
2. `empty_on.yaml`
3. `case_sensitive_keys.yaml`

この段階では「完全一致」ではなく「期待サブセット一致」を採用し、parser の進化に追随しやすくしている。
