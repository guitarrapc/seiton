# Expression Type Checking 強化計画

## 背景と目的

`documents/index.md` の比較表では「Expression type checking」が Seiton は Partial、actionlint は Full と記載されている。本文書では Partial である根本原因と、Full に到達するためのロードマップを示す。

---

## 1. actionlint の型定義管理アプローチ

### 1.1 二層構造

actionlint は expression type checking に関するデータを **2 種類の方法で管理** している。

| データ | ファイル | 管理方法 | 内容 |
|---|---|---|---|
| コンテキスト型定義 | `expr_sema.go` (`BuiltinGlobalVariableTypes`) | **手書き** | `github.actor` が string、`github.ref_protected` が bool など、各コンテキストのプロパティ名・型のスキーマ |
| コンテキスト可用性テーブル | `availability.go` | **自動生成** | どの workflow キー（`jobs.<id>.if` 等）でどのコンテキストが使えるか、特別関数が使えるかのマッピング |

### 1.2 自動生成（availability.go）の仕組み

`scripts/generate-availability` スクリプトが以下を行う。

1. GitHub 公式ドキュメント（`github/docs` リポジトリの `contexts.md`）の raw Markdown を fetch
2. Markdown をパースし "Context availability" テーブルを抽出
3. workflow キー → [contexts, special functions] のマッピングを Go コードとして出力

このスクリプトは CI で週次実行され、ドキュメント変更に追随する。

### 1.3 型定義（BuiltinGlobalVariableTypes）が手書きである理由

`contexts.md` の各コンテキストセクションには「プロパティ名 | 型 | 説明」形式のテーブルが存在し、型列には `string` / `number` / `boolean` / `object` が構造的に記載されている。またドット区切り（`job.container.id`）でネスト階層も表現されているため、プロパティ名と基本型は原理的に自動取得できる。

ただし actionlint が手書きを選択している理由は以下の制限にある。

- **strict/loose 区別が docs にない**: docs は「このオブジェクトは定義外のプロパティを許すか」を明記しない。ランタイム動作を確認して判断が必要。
- **map パターンの型判別**: `env.<env_name>` のような `<placeholder>` 形式を「map object」として特別処理する必要がある。自動判定は誤りやすい。
- **undocumented プロパティ**: actionlint には docs に載っていない `github.artifact_cache_size_limit`（number）、`github.output`、`github.state`、`github.step_summary` などが手書きで追加されている。
- **動的コンテキスト**: `steps.<step_id>.*`、`matrix.<key>`、`needs.<job_id>.*`、`inputs.<name>` はパターン記述であり、型は AST 走査時のランタイム情報から解決する必要がある。

---

## 2. Seiton の現状と差分

### 2.1 Seiton の現状管理

| データ | ファイル | 管理方法 |
|---|---|---|
| コンテキスト可用性テーブル | `Availability.g.cs` | 自動生成（actionlint 相当） |
| コンテキスト型定義 | **存在しない** | — |

### 2.2 Partial の根本原因

`ExpressionSemanticAnalyzer.InferType` で `Identifier` ノードを常に `ExprType.Any` として返している。

```csharp
ExpressionNodeKind.Identifier => ExprType.Any,  // ← Partial の根本原因
```

`Any` が返されると以降の MemberAccess/IndexAccess/WildcardAccess も全て `Any` に伝播し、型検査が機能しない。

### 2.3 具体的な差分一覧

| Gap | 内容 | actionlint の対応箇所 |
|---|---|---|
| **#1** | コンテキスト型定義がない（最大の差）。`github`, `env`, `job`, `runner`, `secrets`, `strategy`, `steps`, `matrix`, `needs`, `inputs`, `vars` の型スキーマが未定義で全て `Any` | `BuiltinGlobalVariableTypes` |
| **#2** | 未定義のコンテキスト名（root identifier）を検出しない。`goggle.actor` のような typo に対してエラーが出ない | `checkVariable` が `sema.vars` に存在しない名前をエラーにする |
| **#3** | 動的コンテキスト（`steps`, `matrix`, `needs`, `inputs`）を実行時のスキーマで解決しない。`steps.nonexistent-step.outputs.foo` を検出できない | `UpdateSteps` / `UpdateMatrix` / `UpdateNeeds` / `UpdateInputs` / `UpdateDispatchInputs` |
| **#4** | 演算子の型検証がない。`<`/`>` の両辺が bool/null、`!` の被演算子の型、`.*` の receiver 型、index access の添字型チェックがない | `checkCompareOp` / `checkNotOp` / `checkArrayDeref` / `checkIndexAccess` |
| **#5** | `success()`/`failure()`/`cancelled()`/`always()` の使用場所制限がない。これらは `if:` 条件のみで使用可能だが、どこでも使えてしまう | `SetSpecialFunctionAvailability` / `checkSpecialFunctionAvailability` |
| **#6** | `case()` 関数が `FunctionSpec` に未登録 | `BuiltinFuncSignatures["case"]` |
| **#7** | `vars.*` 命名規約チェックがない。`GITHUB_` プレフィックス禁止や使用可能文字の制約を検出しない | `checkConfigVariables` |

---

## 3. Seiton.Update による型定義管理の検討

### 3.1 docs から自動生成できること・できないこと

実際に `contexts.md` と `expressions.md` を取得して確認した結果：

**contexts.md:**

各コンテキストセクションのプロパティは「`github.actor` | string | ...」形式のテーブルで構造的に記載されている。

| 項目 | 自動生成可否 | 根拠 |
|---|---|---|
| コンテキスト可用性（現行） | ✓ 実施中 | "Context availability" テーブルが構造的 |
| 静的コンテキストのプロパティ名（github, job, runner 等） | **✓ 自動取得可能** | プロパティテーブルの1列目がドット区切り階層付きで整然としている |
| プロパティの基本型（string/number/boolean） | **✓ 自動取得可能** | テーブル2列目に `string` / `number` / `boolean` / `object` が明示されている |
| map パターンの判別（`env.<env_name>` 等） | △ 要特別処理 | `<placeholder>` 記法を map-object として解釈するルールが必要 |
| オブジェクトの strict/loose 区別 | ✗ | docs に明示なし。ランタイム動作から判断が必要 |
| undocumented プロパティ（`github.step_summary` 等） | ✗ | docs に記載がないため取得不可 |
| 動的コンテキスト（steps/matrix/needs/inputs）の型 | ✗ | パターン記述のみ。AST 走査時のランタイム情報が必要 |

**expressions.md:**

関数のパラメータ型は散文記述（"Casts values to a string"、"Returns `true`"）に埋め込まれており、構造化されていない。

| 項目 | 自動生成可否 | 根拠 |
|---|---|---|
| 関数名一覧 | ✓ セクション見出しから抽出可能 | `contains` / `startsWith` / `case` 等 |
| 関数の戻り値型 | △ 推定のみ | "Returns `true`" → bool、"Returns a string" → string を正規表現で抽出可能だが不確実 |
| パラメータ型とオーバーロード | ✗ | 散文のみ。手書きが必要 |

### 3.2 採用方針

**コンテキスト型定義は `Availability.g.cs` と同じパターンで半自動生成する。**

パイプライン：`contexts.md` fetch → parse → 手書きオーバーライドを merge → `ContextTypes.g.cs` を codegen

```
data/sources/context-types/
  raw/contexts.md               ← fetch した raw ファイル
  parsed/contexts.json          ← parse 結果（プロパティ名・基本型の平坦なリスト）
  override/context-overrides.json  ← 手書きオーバーライド（strict/loose, map pattern, undocumented）
  merged/context-types.json     ← merge 後の最終モデル

src/Seiton.Core/Generated/
  ContextTypes.g.cs             ← 生成されるコード（Phase 5 の成果物）
```

オーバーライドで管理するもの：
- strict/loose 区別（docs に明示なし）
- `<placeholder>` 形式のプロパティを map-object として指定
- docs 未記載の undocumented プロパティ（`github.step_summary` 等）

**関数シグネチャは手書き管理**（actionlint と同様）。パラメータ型・オーバーロードが docs から取れないため。

**Phase 5 は Phase 1 完了後に着手**し、完了後は Phase 1 の手書き `BuiltinContextTypes` を `ContextTypes.g.cs` 由来の定義に置き換える。

---

## 4. ロードマップ

### Phase 1: BuiltinContextTypes の定義（コア）

**目的**: Identifier を名前に応じた型で返す基盤を作る。

**What**: `ExpressionSemanticAnalyzer` に `BuiltinContextTypes` 辞書を追加し、全コンテキストの手書き型定義を実装する。

```
github     → strict object (30+ プロパティ)
env        → map object<string>
job        → strict object (container, services, status)
runner     → strict object (name, os, arch, temp, tool_cache, debug, environment)
secrets    → map object<string> + 自動追加キー (github_token 等)
strategy   → object (fail-fast, job-index, job-total, max-parallel)
steps      → empty strict object (Phase 2 で動的更新)
matrix     → empty strict object (Phase 2 で動的更新)
needs      → empty strict object (Phase 2 で動的更新)
inputs     → empty strict object (Phase 2 で動的更新)
vars       → map object<string>
```

- `InferType` の `Identifier` ケースを `BuiltinContextTypes` 参照に変更（root context 名ならその型、それ以外は `Any` のまま）
- 未定義 root context 名をエラーとして診断に追加（Gap #2 の解消）

**完了条件**:
- [ ] `github.typo_field` のような未知プロパティがエラーになること
- [ ] `goggle.actor` など未定義 root ident がエラーになること
- [ ] 既存の全テストが通過すること
- [ ] `github.actor` / `github.ref_protected` / `job.status` / `runner.os` など主要プロパティの型が正しく推定されること
- [ ] 全テスト通過

---

### Phase 2: 動的コンテキスト解決（Linter 連携）

**目的**: `steps`, `matrix`, `needs`, `inputs` を実際の AST 情報で解決する。

**What**: Linting フェーズで以下の型を AST から構築し、`ExpressionSemanticAnalyzer.Validate` に渡す。

| コンテキスト | 解決元 |
|---|---|
| `steps.<id>` | 各 Job の step の `id:` フィールド。`outputs.<key>` と `outcome`, `conclusion` を持つ |
| `matrix.<key>` | Job の `strategy.matrix:` セクション |
| `needs.<job_id>` | Job の `needs:` リストと、参照 job の `outputs:` セクション |
| `inputs.<name>` | `on.workflow_call.inputs:` または `on.workflow_dispatch.inputs:` の型情報付き解決 |

- `ExpressionSemanticAnalyzer.Validate` のシグネチャを拡張し、contextual 型マップ（`Dictionary<string, ExprType>`）を受け取れるようにする
- あるいは Validate 前に `BuiltinContextTypes` を context-specific な型でオーバーライドする機構を設ける

**完了条件**:
- [ ] `steps.nonexistent-step.outputs.foo` がエラーになること
- [ ] `matrix.unknown_key` がエラーになること（matrix セクション定義がある場合）
- [ ] `needs.nonexistent-job.outputs.foo` がエラーになること
- [ ] `inputs.unknown_param` がエラーになること（workflow_call/dispatch inputs 定義がある場合）
- [ ] 全テスト通過

---

### Phase 3: 演算子型検証の強化

**目的**: 型の不一致な演算子の使用を検出する（Gap #4 の解消）。

**What**:

- `<`, `>`, `<=`, `>=` の両辺が null/bool/array/object のとき診断エラー
- `!` の被演算子が bool-compatible でないとき診断エラー
- `.*` の receiver が array でも object でもないとき診断エラー
- index access `[]` の添字が配列なら number、オブジェクトなら string であることの検証

**完了条件**:
- [ ] `null < 1` や `true > false` などが診断エラーになること
- [ ] `"string".* ` が診断エラーになること
- [ ] `array[1]` が正常、`array["key"]` が診断エラーになること
- [ ] 全テスト通過

---

### Phase 4: 特別関数制限・`case()` 追加・`vars` 命名規約

**目的**: actionlint との残差を解消する（Gap #5, #6, #7 の解消）。

**What**:

- `success()` / `failure()` / `cancelled()` / `always()` を `ExpressionValidationContext.Step` および job/workflow の `if:` コンテキストに限定（他の場所でのエラー診断）
- `case(bool, any, any, ...)` を `FunctionSpec` に追加
- `vars.GITHUB_*` プレフィックスや `vars.foo-bar`（無効文字）の命名規約チェックを追加

**完了条件**:
- [ ] `env:` の値などで `success()` を使うとエラーになること
- [ ] `case(true, 1, 0)` が合法と認識されること
- [ ] `vars.GITHUB_FOO` が命名規約違反エラーになること
- [ ] 全テスト通過

---

### Phase 5: Seiton.Update による ContextTypes.g.cs の自動生成

**目的**: Phase 1 で手書きした `BuiltinContextTypes` を `Availability.g.cs` と同じパターンで自動生成に移行し、GitHub docs 更新への追随を自動化する。

**What**: `Availability.g.cs` の生成パイプライン（`GitHubAvailabilityFetcher` → `GitHubDocsAvailabilityMarkdownParser` → `AvailabilityCSharpGenerator`）と同じ構成を `ContextTypes` 向けに実装する。

```
Seiton.Update に追加する構成物：
  Sources/GitHubContextTypesFetcher.cs
    - contexts.md を fetch し data/sources/context-types/raw/ に保存
  Parsers/GitHubDocsContextTypesMarkdownParser.cs
    - 各コンテキストセクションのプロパティテーブルを parse し
      data/sources/context-types/parsed/contexts.json を出力
    - <placeholder> 形式を map パターンとして認識するルールを含む
  Sources/ContextTypesOverride.cs（手書き設定）
    - strict/loose 区別のオーバーライド
    - undocumented プロパティの追加
  Generators/ContextTypesCSharpGenerator.cs
    - parsed.json + override を merge し ContextTypes.g.cs を生成

Seiton.Core/Generated/ に追加されるファイル：
  ContextTypes.g.cs
    - Phase 1 の手書き BuiltinContextTypes をこのファイルに移行
    - 再生成コマンド: dotnet run --project src/Seiton.Update -- sync-context-types
```

生成する C# の内容例：

```csharp
// <auto-generated>
// Regenerate: dotnet run --project src/Seiton.Update -- sync-context-types
// </auto-generated>
public static class ContextTypes
{
    public static readonly IReadOnlyDictionary<Utf8String, ExprType> BuiltinContextTypes = ...;
}
```

**Seiton.Update コマンド追加**:
- `sync-context-types` — fetch + parse + merge + codegen を一括実行
- `verify-context-types` — 現在の `ContextTypes.g.cs` が再生成結果と一致するか検証（CI 用）

**完了条件**:
- [ ] `sync-context-types` を実行すると `ContextTypes.g.cs` が再生成されること
- [ ] Phase 1 のインライン手書き定義を `ContextTypes.g.cs` 由来に差し替えても全テストが通過すること
- [ ] docs にプロパティが追加された場合に `verify-context-types` が差分を報告すること
- [ ] CI で週次実行できる設計になっていること
- [ ] 全テスト通過

---

## 5. 優先度と依存関係

```
Phase 1 (BuiltinContextTypes 手書き実装)
  ├─ Phase 2 (動的コンテキスト解決)
  ├─ Phase 3 (演算子型検証)        ← Phase 2 と並行可
  ├─ Phase 4 (特別関数/case/vars)  ← Phase 2 に依存しない
  └─ Phase 5 (ContextTypes.g.cs 自動生成)
       └─ Phase 1 手書き定義を g.cs 由来に置き換え
```

**推奨実施順**: Phase 1 → Phase 4 → Phase 3 → Phase 2 → Phase 5

- **Phase 1** が唯一のブロッカー。型システムの基盤がないと残フェーズは開始できない。
- **Phase 4** は Phase 1 の後すぐ着手できる小さい変更（`case()` 追加・関数制限・命名規約）。
- **Phase 3** も Phase 1 の完了後に並行着手可能。
- **Phase 2** はシグネチャ設計が最も複雑なため、Phase 1 で型システムの動作を確認してから着手する。
- **Phase 5** は Phase 1 完了後に着手し、完了後に Phase 1 の手書き定義を生成コードに置き換える。他フェーズをブロックしない。
