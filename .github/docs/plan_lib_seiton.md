# Seiton.Core ライブラリ公開計画

本書は `Seiton.Core` を NuGet ライブラリとして公開し、他の .NET アプリケーションから Seiton の Parse / Lint / Fix 機能を利用可能にするために、考慮すべき論点と対策案を整理したものである。

対象は主に `src/Seiton.Core/` の公開であり、CLI 配布（`src/Seiton/`）やインストールチャネルの整備は本書の主題ではない。

## 現状

`Seiton.Core` はすでに parser / linter の実装本体として成立しており、外部利用の入口になりうる public API も一部存在する。

- [`WorkflowParser`](../../src/Seiton.Core/Parsing/WorkflowParser.cs) に `Parse`（`ParseResult` を返す）/ `ParseClassified` がある
- [`LintEngine`](../../src/Seiton.Core/Linting/LintEngine.cs) に `Check(byte[] utf8Yaml, string filePath, LintConfig? config)` がある（`LintResult` を返す）
- public surface は [`ParseResult`](../../src/Seiton.Core/Parsing/OwnedParseResult.cs) / [`LintResult`](../../src/Seiton.Core/Linting/OwnedLintResult.cs) の 1 概念 1 型に整理済み
- Arena ライフタイムは `ParseResult` / `LintResult` が `IDisposable` で管理し、値解決 API (`GetString`, `GetUtf8` など) を結果型側に集約した
- [`FixEngine`](../../src/Seiton.Core/Linting/Fixing/FixEngine.cs) も public で、fix 実行 API として使いうる

一方で、現在の `Seiton.Core` は「CLI の内部実装」に近い形で育っており、公開ライブラリとしては以下の未整備箇所がある。

- [`Directory.Build.props`](../../Directory.Build.props) で `IsPackable=false`
- [`Seiton.Core.csproj`](../../src/Seiton.Core/Seiton.Core.csproj) は `net10.0` 単一ターゲット
- low-level API が多く、安定化対象の公開面が大きい
- pooled buffer / `AstArena` のライフタイム制約が外部利用者に露出している
- thread-safe でない前提の型がある
- NuGet パッケージメタデータ、README、サンプル、互換性ポリシーが未整備

## ゴールと非ゴール

- **ゴール**: 他のアプリが Seiton の Parse / Lint / Fix を安全かつ明快に利用できるライブラリ配布を実現する
- **ゴール**: 公開 API の安定面を定義し、SemVer に基づいて運用できる状態にする
- **ゴール**: NuGet パッケージとして pack / publish / 検証まで自動化する
- **非ゴール**: CLI の主要 UX をライブラリ API にそのまま移植すること
- **非ゴール**: 初回公開時点で Seiton のすべての内部型を安定 API として保証すること
- **非ゴール**: 他言語向け SDK を同時に設計すること

## 推奨方針

推奨は次のいずれかである。

1. **`Seiton.Core` をそのまま公開せず、安定 facade を追加してから公開する**
2. **内部実装寄りの `Seiton.Core` と、外部公開向け facade パッケージを分離する**

2 を採用する。

理由:

- `Seiton.Core` には最適化都合で public になっている型が混ざっている可能性が高い
- parser / lint engine は allocation 最適化や AST 設計変更の影響を受けやすく、公開互換性を直接背負わせると進化速度が落ちる
- facade パッケージを別にすれば、外部公開 API を小さく保ったまま `Seiton.Core` を内部実装として改善し続けられる

候補:

- `Seiton.Core` を実装ライブラリとして維持し、`Seiton.SDK` または `Seiton.Core.Api` を公開面にする
- もし単一パッケージでいくなら、`Seiton.Core` 内に facade namespace を用意し、既存 low-level API は将来的に advanced 扱いへ寄せる

## 考慮事項と対策案

### 1. 公開 API 面積が大きすぎる

**論点**:

- `Parsing.Ast`、`Linting`、`Generated` 配下に public 型が多数存在する
- これらを全部サポート対象にすると、将来の内部最適化や AST 変更が破壊的変更になりやすい

**対策案**:

- 外部公開 API を facade に限定する
- 初回公開で保証する型を明示的に絞る
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` などで公開 API を固定する
- low-level API は internal 化できるものから順次閉じる

**最低限の facade API 案**:

```csharp
public static class SeitonParser
{
    public static SeitonParseResult Parse(string yaml, string filePath);
    public static SeitonParseResult Parse(byte[] utf8Yaml, string filePath);
}

public static class SeitonLinter
{
    public static SeitonLintResult Lint(string yaml, string filePath, SeitonLintOptions? options = null);
    public static SeitonLintResult Lint(byte[] utf8Yaml, string filePath, SeitonLintOptions? options = null);
}
```

### 2. ライフタイム制約が危険

**論点**:

- [`LintResult`](../../src/Seiton.Core/Linting/LintResult.cs) は pooled diagnostics を返し、Arena の寿命に依存する
- ~~外部利用者は `Arena.Dispose()` 前提を知らずに診断配列を保持しがち~~ → **対策済み**: `OwnedParseResult` / `OwnedLintResult` (`IDisposable`) が Arena を保持し、`using var result = ...` で自然に寿命管理できる

**本質的な問題の構造**:

`ParseResult` / `LintResult` は値型 (struct) だが、中身は pooled memory への borrowed reference。Rust で言えば lifetime パラメータなしの借用を返しているのと同じ。

| 型 | 所有権 | 利用者への露出 | 危険性 |
|---|---|---|---|
| `AstArena` | ThreadStatic pool | `OwnedParseResult` / `OwnedLintResult` が所有 | Result の Dispose で安全に返却 |
| `Diagnostic[]` | Arena に登録 | `DiagnosticList` 経由で参照 | Arena 廃棄で dangling |
| `Workflow` / `ActionMetadata` | Arena 内の pooled 配列 | `ParseResult` フィールド | 同上 |
| `StringNodeData[]` 等 | Arena 内 | NodeId handle 経由 | 同上 |

これは facade レイヤーで snapshot を返すだけでは不十分であり、`Seiton.Core` 自体の API デザインの問題。

**パフォーマンスコストの現実的評価**:

- **Diagnostics 配列コピー**: 典型的なワークフローで 0〜50 件。50 件で ~5KB の memcpy。parsing 全体 (数 ms) に対してナノ秒オーダー。無視できる。
- **AST snapshot**: 現在の `Workflow` / `Job` / `Step` は class で Arena 内に pooled。丸ごとコピーは高コスト。
- **重要な洞察**: pooling が効くのは **parsing 中** の中間バッファ再利用（数百回の allocate/free 回避）。**結果の 1 回のコピー** は処理全体に対して無視できるコスト。

**データ指向での解決パターン（3 段階）**:

#### パターン A: Scope-bound processing（Core 内部向け・ゼロアロケーション）

```csharp
// 結果を「借りて処理する」パターン。所有権移転なし。
public static TResult Parse<TResult>(
    byte[] utf8Yaml,
    string filePath,
    ParseResultProcessor<TResult> processor);

public delegate TResult ParseResultProcessor<TResult>(in ParseResultView view);

// ref struct で escape をコンパイラが禁止
public ref struct ParseResultView
{
    public readonly Workflow? Workflow;
    public readonly ActionMetadata? ActionMetadata;
    public readonly ReadOnlySpan<Diagnostic> Diagnostics;
    public readonly bool HasFatalError;
}
```

- 利用者は callback 内でしか結果にアクセスできない
- Arena のライフタイムはフレームワーク側が管理
- `ref struct` なので escape 不可（コンパイラが保証）

#### パターン B: ref struct session（バッチ処理向け・ゼロアロケーション）

```csharp
// 複数ファイルを処理する際の amortized zero-alloc パターン
public ref struct LintSession : IDisposable
{
    private AstArena _arena;

    public static LintSession Create() => ...;

    // 結果は session 内のみ有効。次の Check() で前の結果は無効。
    public LintResultView Check(byte[] utf8Yaml, string filePath, LintConfig? config);

    public void Dispose() => _arena.Dispose();
}

public ref struct LintResultView
{
    public ReadOnlySpan<Diagnostic> Diagnostics { get; }
    public bool HasFatalError { get; }
}
```

- `using var session = LintSession.Create();` で自然なスコープ管理
- `ref struct` で escape 防止（.NET 10 なら `ref struct : IDisposable` が使える）
- CLI やバッチ処理で allocation ゼロを維持

#### パターン C: Owned snapshot（facade / SDK 向け・安全第一）

```csharp
// 完全に所有権を移転した結果を返す。Dispose 不要。
public static ParseSnapshot Parse(byte[] utf8Yaml, string filePath);

// Plain data。Pool も Arena も関係ない。
public readonly record struct ParseSnapshot(
    Diagnostic[] Diagnostics,
    bool HasFatalError,
    WorkflowData? Workflow);

// WorkflowData は immutable flat data（struct/record の木）
public readonly record struct WorkflowData(JobData[] Jobs, ...);
```

- 利用者視点で完全に安全。GC が管理する通常のオブジェクト。
- 内部では Arena + pooling を使い、最後に 1 回だけ snapshot を取る。

#### パターン比較

| 観点 | A: Callback | B: ref struct session | C: Owned snapshot |
|---|---|---|---|
| 安全性 | コンパイラ保証 | using 忘れのみリスク | 完全安全 |
| Allocation | ゼロ | ゼロ（session 内） | Diagnostics[] + AST data 1 回 |
| 使いやすさ | △ callback nesting | ○ using スコープ | ◎ 最も自然 |
| データ指向適合 | ◎ 関数 + データ | ○ session は state 持ち | ◎ plain record |
| 適用場面 | single-shot 処理 | batch CLI / IDE | 外部ライブラリ利用者 |

**推奨する対策案（3 層構成）**:

```
┌─────────────────────────────────────────────────────┐
│ Seiton.SDK (facade)                                  │
│  - ParseSnapshot Parse(byte[], string)               │
│  - LintSnapshot Lint(byte[], string, options?)       │
│  - パターン C: 完全 owned、Dispose 不要、安全         │
└─────────────────────────────────────────────────────┘
         │ (internal dependency)
┌─────────────────────────────────────────────────────┐
│ Seiton.Core Public API (advanced users)              │
│  - パターン B: ref struct LintSession (batch)        │
│  - パターン A: Parse<T>(..., processor) (callback)   │
│  - 利用者がスコープを管理する上級 API                  │
└─────────────────────────────────────────────────────┘
         │
┌─────────────────────────────────────────────────────┐
│ Seiton.Core Internal (現行のまま)                     │
│  - AstArena, pooled buffers, ThreadStatic cache      │
│  - CLI / Playground が直接使う                        │
└─────────────────────────────────────────────────────┘
```

**Seiton.Core 自体の改善（facade とは独立）**:

1. ✅ **`ParseResult` から `Arena?` フィールドを除去する** — Arena は結果の一部ではなくリソース管理の関心事。owned result wrapper が管理する。
2. ✅ **`DiagnosticList` を borrowed / owned で型レベル区別する** — `OwnedDiagnostics` 型を導入し、`CopyDiagnostics()` の返り値で所有権を明示。
3. ✅ **`LintEngine.Check()` の返り値を caller-owned class にする** — `using var result = engine.Check(...)` で自然に Dispose。async/await・フィールド保持・クロージャ capture を自然に許容。
4. ✅ **AST の外部公開は read-only snapshot を別型で返し、内部は pooled class を維持** — `OwnedParseResult` / `OwnedLintResult` が直接 Arena を保持する。

#### 実装済み: ref struct ハンドル導入（項目 1, 3）

**実施日**: 2026-05-14

**変更内容**:

- `ParseResult` から `AstArena? Arena` パラメータを除去
- `ref struct ParseHandle : IDisposable` を新設（`src/Seiton.Core/Parsing/ParseHandle.cs`）
  - `Result` (ParseResult), `Arena` (internal), 便利プロパティ (`HasFatalError`, `Workflow`, `ActionMetadata`, `Diagnostics`)
  - `Dispose()` で Arena を ThreadStatic pool に返却
- `ref struct LintHandle : IDisposable` を新設（`src/Seiton.Core/Linting/LintHandle.cs`）
  - `Result` (LintResult), `ParseResult`, `Diagnostics`, `CopyDiagnostics()`, `Arena` (internal)
  - `Dispose()` で Arena を ThreadStatic pool に返却
- `WorkflowParser.Parse()` → `ParseHandle` を返す（public API）
- `LintEngine.Check()` → `LintHandle` を返す（public API）
- internal ヘルパー追加:
  - `WorkflowParser.ParseDirect(byte[], string, out AstArena?)` — async テスト・ベンチマーク用
  - `LintEngine.CheckDirect(byte[], string, out AstArena?)` — 同上
  - `LintEngine.CheckDirect(byte[], string, LintConfig?, out AstArena?)` — config 付き
- CLI (`CheckCommand`, `FixCommand`) は `using var handle = engine.Check(...)` パターンに移行
- `FixCommand` は async 境界前に `CopyDiagnostics()` で所有権移転
- `IncrementalParseContext` は `internal AstArena? Arena` プロパティで playground に公開
- `Seiton.Playground.Core.csproj` に `InternalsVisibleTo: Seiton.Playground.Tests` 追加

**ベンチマーク結果（回帰なし）**:

| メトリクス | 変更前 | 変更後 | 差分 |
|---|---|---|---|
| CoreLint Large (fix=false) | 21838 μs / 327.08 KB | 21784 μs / 327.08 KB | 0% alloc |
| CoreLint Large (fix=true) | 36310 μs / 381.93 KB | 32029 μs / 381.92 KB | 0% alloc |
| CoreParsing Large | 18858 μs / 180.04 KB | 18037 μs / 180.04 KB | 0% alloc |

**テスト結果**: 全 1615 テスト pass、0 failures

**設計判断と教訓**:

- `ref struct` は async メソッドの `await` 境界を跨げない。テストコード（TUnit は async）では `ParseDirect` / `CheckDirect` で `out AstArena?` を受け取り手動管理する internal API が必要だった。
- CLI の並列パス (`Parallel.For`) では `LintHandle` をラムダ内で `using` できるため自然に動作する。
- `FixCommand` のように結果を `await` 後に使う場合は、`CopyDiagnostics()` で owned 配列にコピーしてから handle を dispose する。これはパターン C (Owned snapshot) の部分適用に相当する。

#### 実装済み: OwnedDiagnostics 型導入（項目 2）

**実施日**: 2026-05-14

**変更内容**:

- `OwnedDiagnostics` readonly struct を新設（`src/Seiton.Core/Parsing/OwnedDiagnostics.cs`）
  - `Diagnostic[]` のラッパー。`IReadOnlyList<Diagnostic>` を実装
  - `AsSpan()`, `AsArray()`, struct `Enumerator`, implicit conversion to `Diagnostic[]`
  - 型名で「この診断コレクションは caller-owned で安全に保持可能」と表現
- `LintResult.CopyDiagnostics()` → 返り値を `Diagnostic[]` から `OwnedDiagnostics` に変更
- `LintHandle.CopyDiagnostics()` → 同上
- `ParseHandle.CopyDiagnostics()` → 新規追加（`OwnedDiagnostics` を返す）
- CLI `CheckCommand`:
  - `FileCheckResult.Diagnostics` を `OwnedDiagnostics` に変更
  - 逐次パスの `allDiagnostics.AddRange(result.Diagnostics)` → `AddRange(result.Diagnostics.AsSpan())` に変更（span-based, IEnumerable boxing 回避）
  - 並列パス集約も `AddRange(slots[i].Diagnostics.AsSpan())` に変更
- CLI `FixCommand`: `lintDiagnostics` 変数を `OwnedDiagnostics` に変更
- テストコード: `OwnedDiagnostics` → `Diagnostic[]` の implicit conversion により変更不要

**型レベルの安全性改善**:

| API | 返り値型 | 所有権 | 安全性 |
|---|---|---|---|
| `ParseHandle.Diagnostics` | `DiagnosticList` | borrowed (arena) | handle スコープ内のみ有効 |
| `LintHandle.Diagnostics` | `DiagnosticList` | borrowed (arena) | handle スコープ内のみ有効 |
| `ParseHandle.CopyDiagnostics()` | `OwnedDiagnostics` | caller-owned | 永続保持可能 |
| `LintHandle.CopyDiagnostics()` | `OwnedDiagnostics` | caller-owned | 永続保持可能 |

**追加最適化**: 逐次パスで `List<T>.AddRange(IEnumerable<T>)` → `List<T>.AddRange(ReadOnlySpan<T>)` に変更。IEnumerable 経由の yield-return state machine アロケーションを回避。

**ベンチマーク結果（回帰なし）**:

| メトリクス | 変更前 | 変更後 | 差分 |
|---|---|---|---|
| CoreLint Small (fix=false) | 8.37 KB | 8.37 KB | 0% alloc |
| CoreLint Medium (fix=false) | 68.56 KB | 68.56 KB | 0% alloc |
| CoreLint Large (fix=false) | 327.08 KB | 327.08 KB | 0% alloc |
| CoreLint Large (fix=true) | 381.93 KB | 381.92 KB | 0% alloc |

**テスト結果**: 全 1615 テスト pass、0 failures

#### 実装済み: AST Detach 機構（項目 4）

**実施日**: 2026-05-14

**変更内容**:

- `OwnedParseResult` sealed class を新設（`src/Seiton.Core/Parsing/OwnedParseResult.cs`）
  - `IDisposable` を実装。`Dispose()` で Arena を解放
  - `Workflow?`, `ActionMetadata?`, `OwnedDiagnostics Diagnostics`, `bool HasFatalError`, `AstArena Arena` プロパティ
  - `Arena` は Dispose 後にアクセスすると `ObjectDisposedException` をスロー
  - 通常のクラスなのでフィールド保持・async 境界越え・クロージャキャプチャが可能
- `OwnedLintResult` sealed class を新設（`src/Seiton.Core/Linting/OwnedLintResult.cs`）
  - `IDisposable` を実装。`Dispose()` で Arena を解放
  - `Workflow?`, `ActionMetadata?`, `OwnedDiagnostics Diagnostics`, `OwnedDiagnostics ParseDiagnostics`, `bool HasFatalError`, `SuppressionSummary`, `AstArena Arena`, `int DiagnosticCount`
- `ParseHandle.Detach()` メソッドを追加
  - `OwnedParseResult` を生成し、診断を `OwnedDiagnostics` にコピー
  - `_arena = null` にして handle の `Dispose()` が arena を二重解放しないようにする
- `LintHandle.Detach()` メソッドを追加
  - `OwnedLintResult` を生成し、lint/parse 両方の診断をコピー
  - 同様に `_arena = null` で所有権移転
- テスト: `DetachTests.cs` に 11 テストケースを追加
  - ParseHandle/LintHandle の Detach、所有権移転、async 境界越え、フィールド保持、Dispose 後の ObjectDisposedException

**所有権モデル**:

| 操作 | Arena 所有者 | 用途 |
|---|---|---|
| `using var handle = parser.Parse(...)` | ParseHandle | 通常のスコープ内利用 |
| `var owned = handle.Detach()` | OwnedParseResult | async 境界越え、フィールド保持 |
| `owned.Dispose()` | (解放済み) | Arena を pool に返却 |

**ベンチマーク結果（回帰なし）**:

| メトリクス | 変更前 | 変更後 | 差分 |
|---|---|---|---|
| CoreLint Small (fix=false) | 8.37 KB | 8.37 KB | 0% alloc |
| CoreLint Small (fix=true) | 9.82 KB | 9.82 KB | 0% alloc |
| CoreLint Medium (fix=false) | 68.56 KB | 68.56 KB | 0% alloc |
| CoreLint Medium (fix=true) | 81.92 KB | 81.92 KB | 0% alloc |
| CoreLint Large (fix=false) | 327.08 KB | 327.08 KB | 0% alloc |
| CoreLint Large (fix=true) | 381.93 KB | 381.93 KB | 0% alloc |

**テスト結果**: 全 1626 テスト pass、0 failures（+11 新規 DetachTests）

#### 実装済み: ref struct ハンドル廃止 → Parse/Check が直接 class を返す（項目 1,3,4 統合リファクタ）

**動機**:

項目 1,3 で導入した `ParseHandle` / `LintHandle` (ref struct) は「スコープ外で使えない」コンパイラ制約を活かす設計だったが、利用者視点で以下の問題がある:

- ref struct は async 不可・フィールド保持不可・クロージャ不可 → テストコード（TUnit は全 async）で使えず `ParseDirect`/`CheckDirect` internal ハックが必要
- 項目 4 の `Detach()` パターンが冗長: `{ using var h = Parse(...); owned = h.Detach(); } using (owned) { ... }`
- 「class + IDisposable + using」で十分な安全性。ref struct の制約は利用者に不要な負荷

**設計方針**: `Parse()` / `Check()` が直接 `OwnedParseResult` / `OwnedLintResult` (class, IDisposable) を返す。`ParseHandle` / `LintHandle` / `Detach()` を廃止し、利用者が `using var result = ...` をそのまま書ける形へ寄せる。`ParseDirect` / `CheckDirect` は internal 互換 API として現時点では維持する。

**改善後の利用パターン**:

```csharp
// 1行で完結。async もフィールド保持も OK
using var result = WorkflowParser.Parse(yaml, path);
result.Workflow    // AST
result.Diagnostics // DiagnosticList
result.Arena       // handle 解決用

using var result = engine.Check(yaml, path);
await DoSomethingAsync(result); // ref struct ではないので問題なし
```

**OwnedParseResult 再設計** (ParseResult ラッパー、診断コピー廃止):

```csharp
public sealed class OwnedParseResult : IDisposable
{
    internal OwnedParseResult(ParseResult result, AstArena? arena);
    public ParseResult Result { get; }                    // 内部 ParseResult (FixEngine 等)
    public Workflow? Workflow => Result.Workflow;
    public ActionMetadata? ActionMetadata => Result.ActionMetadata;
    public DiagnosticList Diagnostics => Result.Diagnostics;  // arena 非依存、コピー不要
    public bool HasFatalError => Result.HasFatalError;
    public OwnedDiagnostics CopyDiagnostics();            // Dispose 後も保持したい場合用
    public AstArena Arena => _arena ?? throw new ObjectDisposedException(...);
    public void Dispose();
}
```

**OwnedLintResult 再設計** (LintResult ラッパー):

```csharp
public sealed class OwnedLintResult : IDisposable
{
    internal OwnedLintResult(LintResult result, AstArena? arena);
    public LintResult Result { get; }                     // 内部 LintResult
    public Workflow? Workflow => Result.Workflow;
    public ActionMetadata? ActionMetadata => Result.ActionMetadata;
    public DiagnosticList Diagnostics => Result.Diagnostics;
    public DiagnosticList ParseDiagnostics => Result.ParseDiagnostics;
    public bool HasFatalError => Result.HasFatalError;
    public bool HasFixableDiagnostics => Result.HasFixableDiagnostics;
    public int FixableDiagnosticCount => Result.FixableDiagnosticCount;
    public Diagnostic[] FixableDiagnostics => Result.FixableDiagnostics;
    public SuppressionSummary SuppressionSummary => Result.SuppressionSummary;
    public int DiagnosticCount => Result.DiagnosticCount;
    public OwnedDiagnostics CopyDiagnostics();
    public AstArena Arena => _arena ?? throw new ObjectDisposedException(...);
    public void Dispose();
}
```

**パフォーマンス影響見積**: OwnedParseResult (class) のヒープ割当 ≈ 32 bytes/call。Small ワークフロー (8.37 KB) で +0.4%。1% 許容範囲内。

**実施内容**:

| ファイル/メソッド | 理由 |
|---|---|
| `ParseHandle.cs` | OwnedParseResult に統合 |
| `LintHandle.cs` | OwnedLintResult に統合 |
| `Detach()` | Parse()/Check() が直接 Owned を返す |
| `OwnedParseResult` | `ParseResult` ラッパーに再設計。`Result`, `Diagnostics`, `CopyDiagnostics()`, `Arena` を提供 |
| `OwnedLintResult` | `LintResult` ラッパーに再設計。`Result`, `FixableDiagnostics`, `CopyDiagnostics()`, `CopyParseDiagnostics()` を提供 |
| `WorkflowParser.Parse()` | `OwnedParseResult` を直接返す |
| `LintEngine.Check()` | `OwnedLintResult` を直接返す |
| `CoreParsingBenchmark` / `CoreLintBenchmark` | public API を直接計測する形に更新 |

**実装結果**:

- `WorkflowParser.Parse()` は `OwnedParseResult` を直接返すように変更
- `LintEngine.Check()` は `OwnedLintResult` を直接返すように変更
- `OwnedParseResult` / `OwnedLintResult` は `ParseResult` / `LintResult` の wrapper として再設計
- `ParseHandle.cs` / `LintHandle.cs` を削除
- direct API を期待する所有権テストへ更新し、async/await 越え・フィールド保持・Dispose 後例外を確認
- 既存の full test suite でも回帰なしを確認
- `ParseDirect` / `CheckDirect` は internal 呼び出しの影響範囲を抑えるため現時点では維持

**ベンチマーク結果（回帰なし）**:

| メトリクス | 変更前 | 変更後 | 差分 |
|---|---|---|---|
| CoreLint Small (fix=false) | 8.37 KB | 8.37 KB | 0% alloc |
| CoreLint Small (fix=true) | 9.82 KB | 9.82 KB | 0% alloc |
| CoreLint Medium (fix=false) | 68.56 KB | 68.56 KB | 0% alloc |
| CoreLint Medium (fix=true) | 81.92 KB | 81.92 KB | 0% alloc |
| CoreLint Large (fix=false) | 327.08 KB | 327.08 KB | 0% alloc |
| CoreLint Large (fix=true) | 381.93 KB | 381.92 KB | 0% alloc |
| CoreParsing Small | 3.87 KB | 3.87 KB | 0% alloc |
| CoreParsing Medium | 35.59 KB | 35.59 KB | 0% alloc |
| CoreParsing Large | 180.04 KB | 180.04 KB | 0% alloc |

**テスト結果**: 全 1625 テスト pass、0 failures

**評価**:

この計画は、現状の `ParseHandle` / `LintHandle` + `Detach()` よりは明確に良い。特に「`Parse()` した結果をそのまま `using` で使う」という形は C# 利用者の直感に沿っており、async/await やテストコードとの相性も改善する。一方で、これは **Seiton.Core の advanced API を整流する中間段階としては有効** だが、公開ライブラリの最終形としてはまだ改善余地がある。

**データ志向の観点**:

- 現状案は `ref struct` を廃止しても、AST 自体は依然として `AstArena` と NodeId handle に依存している
- つまり「結果オブジェクトは class になって保持しやすくなる」が、「データそのものが自己完結している」わけではない
- `OwnedParseResult` / `OwnedLintResult` が `Arena` を公開し続ける限り、利用者は AST 値の読み方として arena 解決を理解する必要がある
- このため、**データ志向としては改善だが十分ではない**。真に素直な API は facade 側での owned snapshot (`WorkflowData`, `JobData` など) である

**async/await 相性の観点**:

- ここは現状より大きく改善する。`ref struct` は async メソッド・TUnit テスト・フィールド保持・クロージャで極端に扱いづらい
- `ParseDirect` / `CheckDirect` という internal 回避 API が必要になっている時点で、public API が利用スタイルに負けている
- `Parse()` / `Check()` が通常の `IDisposable` class を返せば、`using var result = ...; await ...;` が自然に書ける
- C# では async/await が通常の制御フローであり、そこに素直に乗る API の方が長期的に保守しやすい

**今後の設計改善の観点**:

- この計画で `ParseHandle` / `LintHandle` / `ParseDirect` / `CheckDirect` / `Detach()` を整理できるのは良い
- ただし最終的には、`OwnedParseResult` / `OwnedLintResult` という命名自体も「所有権モデルを利用者に説明している名前」であり、利用者タスク起点の名前ではない
- C# らしい公開 API の終着点としては、`Parse()` が `ParseResult` を返し、`Check()` が `LintResult` を返す方が自然。現在の borrowed 側 struct は internal に退避させ、public には 1 概念 1 型を保つのが望ましい
- さらに facade では `AstArena` 非公開・AST snapshot 化を進め、Core advanced API と SDK public API を明確に分けるのがよい

**ユーザー視点 / 使い勝手の観点**:

- 現状の `Detach()` 儀式は「仕組みを知っている人しか正しく使えない API」であり、使い方駆動ではなく実装都合駆動になっている
- `using var result = WorkflowParser.Parse(yaml, path);` は API から使い方が読み取れるため、その点で大幅に前進
- ただし `result.Arena.GetStringValue(job.Id)` のようなアクセスが残る限り、まだ「素直で分かりやすい API」とは言い切れない
- 利用者が欲しいのは `job.IdText` や `step.RunText` のような直接読める値であり、arena 解決の仕組みではない
- よって **この計画は「使いにくさを大きく減らす」が、「API を見ればそのまま使える」最終形ではない**

**総合評価**:

| 観点 | 評価 | コメント |
|---|---|---|
| データ志向 | △ | `ref struct` 除去は改善だが、`AstArena` 依存が残る |
| async/await 相性 | ◎ | current API の最大の欠点を解消できる |
| C# らしさ | ○ | `using var result = Parse(...)` は自然。`Owned*` 命名はやや内部都合 |
| API から使い方が分かるか | ○ | `Detach()` は消えるが、`Arena` 公開がなお学習コスト |
| facade への接続性 | ○ | Core advanced API の整理として有効。最終的には snapshot facade へ進むべき |

**結論**:

- **この計画は進める価値が高い**。少なくとも現状の `Detach()` ベース API より、C# らしく、async に強く、利用者視点で素直である
- ただしこれは **公開ライブラリの最終形ではなく、Seiton.Core advanced API をまっすぐにするための整理ステップ** と位置付けるのが適切
- 公開 API の最終ゴールは、`AstArena` や NodeId 解決を利用者に見せない snapshot/facade API である

**この計画を採用する場合の設計原則**:

1. **公開 API は 1 概念 1 型を守る**
  `Parse()` は最終的に 1 つの `ParseResult`、`Check()` は 1 つの `LintResult` を返す形へ寄せる。`Handle` / `Owned` / `Direct` のような実装都合の分岐を public surface に増やさない。

2. **async/await を通常経路として素直に通す**
  public API は `await`・フィールド保持・クロージャ capture を自然に許容する。`ref struct` 制約を避けるための別 API や internal 回避経路を増やさない。

3. **所有権や arena 解決の仕組みを利用者に押し付けない**
  `Dispose` の必要性は許容しても、`Detach()` や `ParseDirect(..., out arena)` のようなライフタイム儀式は公開 API から排除する。さらに将来的には `AstArena` 参照自体も facade 側に隠蔽する。

4. **高頻度経路の性能を守りつつ、利用者体験を優先する**
  Small ケースで許容範囲内の微小アロケーション増であれば、API 単純化を優先する。ただし parser/lint hot path のゼロアロケーション原則は維持し、追加コストは result wrapper など境界部に限定する。

5. **Core advanced API と facade API の責務を混ぜない**
  この計画で整えるのは `Seiton.Core` の advanced API の使い勝手であり、最終的な外部公開 API は snapshot/facade が担う。Core 側で利便性を上げつつも、公開ライブラリの完成形を Core の都合で固定しない。

---

#### 次ステップ計画: 「1 概念 1 型」原則に基づく Core API 最終整理

**現状の問題点（利用者視点）**:

```csharp
using var result = WorkflowParser.Parse(yaml, path);
// ✅ ここまでは自然
var workflow = result.Workflow;

// ❌ ここから不自然: "Arena" という内部メモリプール概念が露出
var bytes = result.Arena.GetStringValue(job.Id);   // Arena って何？
var text = Encoding.UTF8.GetString(bytes);          // なぜ自分でデコード？
```

利用者は「パース結果から値を読みたい」だけであり、メモリプーリング機構の存在を知る必要はない。

また `OwnedParseResult` / `OwnedLintResult` という型名は「所有権が移転した結果」という実装モデルを説明しているのであり、利用者のタスク（「パースする」「チェックする」）を反映していない。

**ゴール（利用者から見た理想的 API）**:

```csharp
using var result = WorkflowParser.Parse(yaml, path);
var workflow = result.Workflow;
var jobId = result.GetString(workflow.Jobs.Entries[0].Value.Id);  // 直感的
ReadOnlySpan<byte> raw = result.GetUtf8(step.Run);               // perf 志向

using var lint = engine.Check(yaml, path);
foreach (var diag in lint.Diagnostics) { ... }
```

- 型名が API 契約と一致: `Parse()` → `ParseResult`, `Check()` → `LintResult`
- AST 値の読み出しは result 自身のメソッド。Arena を知る必要がない
- perf 志向パス (`GetUtf8`) と convenience パス (`GetString`) を両立

**今回の分析で確定した評価**:

- **データ志向**: `Owned*` を class にしただけでは不十分で、`Arena` を public に残す限り「結果が自己完結している」体験にはならない。Core advanced API でも、borrowed 実装を result 自身の操作 API で包むべき。
- **async/await 相性**: 現行の class + `IDisposable` 方針は妥当。`using var result = ...; await ...;` が自然に書けることは public API の必須条件であり、この方向は維持すべき。
- **今後の設計改善**: 本質は rename そのものより `Arena` 隠蔽である。`ParseResult` / `LintResult` への統一は discoverability 改善のために必要だが、使い勝手を決めるのは `GetString` / `GetUtf8` / `GetRange` 群の導入である。
- **ユーザー視点**: `using var result = WorkflowParser.Parse(...)` はすでに素直だが、その次の一手が `result.Arena.GetStringValue(...)` では API から使い方が読み取れない。結果オブジェクト自身が「読むための API」を持つ必要がある。

**今回の計画で固定すること / 次ステップに回すこと**:

- **今回固定する**: public API は `ParseResult` / `LintResult` の 1 概念 1 型へ寄せる、`Arena` は public から隠す、結果型に値解決メソッド群を持たせる、という方向性。
- **次ステップで詰める**: `GetString / GetUtf8 / GetRange / GetExpression / GetSlice` の最終シグネチャ統一、命名、overload 範囲、`Source` の公開粒度。
- **判断理由**: 今の段階で重要なのは API の責務境界を固めることであり、各 accessor の最終命名や細かい overload 設計は、その前提が固まってから詰める方がぶれない。

---

##### Step 1: 型名の統一（1 概念 1 型）

| 現在 | 変更後 | visibility |
|---|---|---|
| `OwnedParseResult` (sealed class) | **`ParseResult`** | public |
| `ParseResult` (readonly record struct) | **`ParseResultData`** | **internal** |
| `OwnedLintResult` (sealed class) | **`LintResult`** | public |
| `LintResult` (readonly record struct) | **`LintResultData`** | **internal** |

**理由**:

- 利用者から見える型は「1 概念 = 1 型」: `Parse()` が返すものは `ParseResult`。それ以上の分岐はない
- 内部実装が data carrier struct を持つのは自由だが、public surface に出さない
- `.Result` プロパティ（内部 struct へのアクセサ）は public から除去。代わりに internal accessor を提供

**影響範囲**:

- Public: `WorkflowParser.Parse()` → `ParseResult` を返す（利用者コードの型名だけ変わる）
- Public: `LintEngine.Check()` → `LintResult` を返す
- Internal: lint rules・CLI・playground は `ParseResultData` / `LintResultData` を参照（internal visibility で問題なし）
- Tests: 型名変更に追従

---

##### Step 2: Arena 隠蔽 + 解決メソッド追加

`AstArena` を **public surface から除去** し、結果型に値解決メソッドを追加する。

```csharp
public sealed class ParseResult : IDisposable
{
    // AST アクセス (現状と同じ)
    public Workflow? Workflow { get; }
    public ActionMetadata? ActionMetadata { get; }
    public DiagnosticList Diagnostics { get; }
    public bool HasFatalError { get; }

    // ── 値解決メソッド (NEW) ──────────────────────────────

    // String
    public string GetString(StringNodeId id);                  // UTF-8 デコード済み (convenience)
    public ReadOnlySpan<byte> GetUtf8(StringNodeId id);        // zero-copy UTF-8 bytes (perf)
    public Utf8Slice GetSlice(StringNodeId id);                // zero-copy offset+length (advanced)
    public bool IsQuoted(StringNodeId id);                     // YAML quoted?
    public TextRange GetRange(StringNodeId id);                // source location
    public StringNodeId GetExpression(StringNodeId id);        // embedded ${{ }}

    // Bool
    public bool GetBool(BoolNodeId id);
    public TextRange GetRange(BoolNodeId id);
    public StringNodeId GetExpression(BoolNodeId id);

    // Int
    public long GetInt(IntNodeId id);
    public TextRange GetRange(IntNodeId id);
    public StringNodeId GetExpression(IntNodeId id);

    // Float
    public double GetFloat(FloatNodeId id);
    public TextRange GetRange(FloatNodeId id);
    public StringNodeId GetExpression(FloatNodeId id);

    // Source bytes (read-only)
    public ReadOnlySpan<byte> Source { get; }

    // ── Diagnostics copy (既存) ──────────────────────────
    public OwnedDiagnostics CopyDiagnostics();

    // ── Internal ─────────────────────────────────────────
    internal AstArena Arena { get; }            // lint rules / playground 向け internal accessor
    internal ParseResultData Data { get; }      // FixEngine 等 internal 向け

    public void Dispose();
}
```

**設計判断**:

| 判断 | 理由 |
|---|---|
| `GetString(id)` は string を返す | convenience 重視。Core API でも「ちょっと確認したい」ケースが最多。alloc は parse 全体に対して無視できる |
| `GetUtf8(id)` は `ReadOnlySpan<byte>` を返す | perf 志向の利用者向け。lint rules は内部的にこれを使う |
| `GetRange` は overload (NodeId type で区別) | 利用者は「このノードの位置」を型を意識せず取得できる |
| `Arena` は internal に退避 | lint rules は `RuleBase.Arena` 経由で引き続きゼロオーバーヘッドでアクセス |
| `Source` は `ReadOnlySpan<byte>` | YAML 原文参照が必要な高度ユースケース向け |

**パフォーマンス影響**:

- 解決メソッドは Arena への 1 メソッド委譲のみ。JIT inline 可能。ゼロオーバーヘッド。
- `GetString()` は string 生成するが、これは利用者が明示的に呼ぶ convenience API。hot path では `GetUtf8()` を使う。
- lint rules は従来通り `Arena` (internal) に直接アクセスするため性能劣化なし。

**補足**:

- この段階では「解決メソッド群を public result に持たせる」ことまでを設計確定とし、個々の最終シグネチャ調整は別ステップで詰める。
- つまり、`GetString / GetUtf8 / GetRange` 群を導入する方針自体は今回の分析で妥当と判断し、詳細な API shaping は次回の設計作業として扱う。

---

##### Step 3: LintResult の同構造

```csharp
public sealed class LintResult : IDisposable
{
    // AST & diagnostics
    public Workflow? Workflow { get; }
    public ActionMetadata? ActionMetadata { get; }
    public DiagnosticList Diagnostics { get; }
    public DiagnosticList ParseDiagnostics { get; }
    public bool HasFatalError { get; }
    public SuppressionSummary SuppressionSummary { get; }
    public int DiagnosticCount { get; }
    public bool HasFixableDiagnostics { get; }
    public int FixableDiagnosticCount { get; }
    public Diagnostic[] FixableDiagnostics { get; }

    // 値解決メソッド (ParseResult と同一シグネチャ)
    public string GetString(StringNodeId id);
    public ReadOnlySpan<byte> GetUtf8(StringNodeId id);
    // ... (省略: ParseResult と同一)

    // Diagnostics copy
    public OwnedDiagnostics CopyDiagnostics();
    public OwnedDiagnostics CopyParseDiagnostics();

    // Internal
    internal AstArena Arena { get; }
    internal LintResultData Data { get; }

    public void Dispose();
}
```

---

##### Step 4: 利用パターン比較

| 観点 | 現在 | 変更後 |
|---|---|---|
| 型名 | `OwnedParseResult` | `ParseResult` |
| 値取得 | `result.Arena.GetStringValue(id)` → `Encoding.UTF8.GetString(...)` | `result.GetString(id)` |
| 値取得 (perf) | `result.Arena.GetStringValue(id)` (span) | `result.GetUtf8(id)` (span) |
| 型の種類 (public) | `ParseResult` struct + `OwnedParseResult` class | `ParseResult` class のみ |
| Arena 露出 | public property | internal (隠蔽) |
| lint rules の書き方 | `Arena.GetStringValue(id)` via `RuleBase` | 変更なし (internal) |

---

##### 実装順序と影響範囲

##### 実装結果

- public API は `WorkflowParser.Parse()` → `ParseResult`、`LintEngine.Check()` → `LintResult` に統一した
- 旧 public borrowed struct は internal `ParseResultData` / `LintResultData` に退避した
- `ParseResult` / `LintResult` から public `Arena` と `.Result` を除去し、`GetString` / `GetUtf8` / `GetSlice` / `GetRange` / `GetExpression` / `GetBool` / `GetInt` / `GetFloat` / `IsQuoted` / `Source` を追加した
- `FixEngine.RevalidationResult` は `LintResult` の寿命を保持する `IDisposable` class に変更した
- CLI / Playground / tests は public wrapper 利用と internal data carrier 利用を境界で分離する形に追従した

**検証結果**:

- `dotnet build --nologo -v q` : pass
- `dotnet test` : 1625 passed, 0 failed
- focused API contract: `tests/Seiton.Core.Tests/DetachTests.cs` の 10 tests pass

**ベンチマーク結果**:

**ベンチマーク結果（今回の変更での性能変化）**:

| メトリクス | 変更前 | 変更後 | 差分 |
|---|---|---|---|
| CoreLint Large (fix=false) | 21,784 us / 327.08 KB | 20,194 us / 327.08 KB | `-7.3%` / `0% alloc` |
| CoreLint Large (fix=true) | 32,029 us / 381.92 KB | 30,065 us / 381.92 KB | `-6.1%` / `0% alloc` |
| CoreParsing Large | 18,037 us / 180.04 KB | 20,443 us / 180.04 KB | `+13.3%` / `0% alloc` |

解釈:

- lint は Large の representative case で悪化なし。少なくとも今回の API 整理で lint 側の性能回帰は観測していない
- parsing は allocation 維持を確認した一方、Large の ShortRun mean は悪化側に出た
- ただし parsing Large は `ShortRun` + `N=3` で `Error` が大きく、今回の値だけで厳密に regression と断定するには弱い。time gate を厳密に通すなら、ここだけ iteration を増やした再測定が必要

実行コマンド:

```powershell
dotnet run --project src/Seiton.Benchmark -c Release -- --filter "Seiton.Benchmark.CoreParsingBenchmark.*" --warmupCount 1 --iterationCount 3
dotnet run --project src/Seiton.Benchmark -c Release -- --filter "Seiton.Benchmark.CoreLintBenchmark.*" --warmupCount 1 --iterationCount 3
```

実行条件:

- BenchmarkDotNet `0.15.6`
- `ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=1)`
- Windows 11 / .NET `10.0.6` / Ryzen 9 7950X3D
- ばらつき確認のため parsing / lint ともに 2 回測定

**CoreParsingBenchmark: 1回目**

| Size | Mean | Error | StdDev | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|
| Small | 48.219 us | 13.081 us | 0.717 us | 3.87 KB | 1.00 |
| Medium | 1,094.427 us | 1,632.371 us | 89.476 us | 35.59 KB | 1.00 |
| Large | 20,041.254 us | 6,417.139 us | 351.745 us | 180.04 KB | 1.00 |

**CoreParsingBenchmark: 2回目（再測定）**

| Size | Mean | Error | StdDev | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|
| Small | 48.845 us | 34.538 us | 1.893 us | 3.87 KB | 1.00 |
| Medium | 1,087.762 us | 1,403.770 us | 76.945 us | 35.59 KB | 1.00 |
| Large | 20,442.798 us | 14,491.911 us | 794.351 us | 180.04 KB | 1.00 |

補足:

- parsing は 2 回とも allocation が全サイズで固定 (`3.87 KB` / `35.59 KB` / `180.04 KB`)
- Large の mean は `20.04 ms` と `20.44 ms` で、過去に plan 内で記録していた `18.04-19.88 ms` より遅い
- ただし今回の設定は `ShortRun` かつ `N=3` なので、Large の `Error` が大きく、時間値だけで厳密判定するには弱い
- 今回の変更は parse hot path そのものではなく public wrapper / API surface の整理なので、allocation 維持は確認できた一方、time gate を厳密に通すなら iteration を増やした再測定が必要

**CoreLintBenchmark: 1回目**

| Size | FixEnabled | Mean | Error | StdDev | Allocated | Alloc Ratio |
|---|---|---:|---:|---:|---:|---:|
| Small | False | 66.04 us | 61.38 us | 3.365 us | 8.37 KB | 1.00 |
| Small | True | 70.65 us | 25.21 us | 1.382 us | 9.82 KB | 1.00 |
| Medium | False | 1,443.59 us | 1,221.89 us | 66.976 us | 68.56 KB | 1.00 |
| Medium | True | 2,053.58 us | 1,295.51 us | 71.011 us | 81.92 KB | 1.00 |
| Large | False | 23,223.38 us | 8,186.49 us | 448.729 us | 327.08 KB | 1.00 |
| Large | True | 33,715.12 us | 8,171.57 us | 447.911 us | 381.92 KB | 1.00 |

**CoreLintBenchmark: 2回目（再測定）**

| Size | FixEnabled | Mean | Error | StdDev | Allocated | Alloc Ratio |
|---|---|---:|---:|---:|---:|---:|
| Small | False | 67.71 us | 44.11 us | 2.418 us | 8.37 KB | 1.00 |
| Small | True | 68.45 us | 122.12 us | 6.694 us | 9.82 KB | 1.00 |
| Medium | False | 1,569.21 us | 1,620.04 us | 88.800 us | 68.56 KB | 1.00 |
| Medium | True | 1,766.53 us | 148.25 us | 8.126 us | 81.92 KB | 1.00 |
| Large | False | 20,193.97 us | 4,581.51 us | 251.128 us | 327.08 KB | 1.00 |
| Large | True | 30,065.10 us | 2,365.67 us | 129.670 us | 381.92 KB | 1.00 |

historical short-run 記録との比較（summary table の根拠）:

| Metric | Historical | This change (re-run) | Delta |
|---|---:|---:|---:|
| Lint Large fix=false | 21,784 us / 327.08 KB | 20,194 us / 327.08 KB | -7.3% / 0% alloc |
| Lint Large fix=true | 32,029 us / 381.92 KB | 30,065 us / 381.92 KB | -6.1% / 0% alloc |
| Parsing Large | 18,037 us / 180.04 KB | 20,443 us / 180.04 KB | +13.3% / 0% alloc |

総括:

- allocation は `CoreParsingBenchmark` / `CoreLintBenchmark` の全ケースで従来値を維持した
- lint は再測定でも historical short-run 記録より悪化せず、Large はむしろ改善側
- parsing は allocation 維持を確認したが、Large の mean は過去記録より遅めに出ている。wrapper 追加による managed allocation 増は観測されていないため、現時点では「API 整理で allocation regression はなし、time は要再測定余地あり」と評価するのが妥当

**Phase A: internal struct 名変更**

1. `ParseResult` record struct → `ParseResultData` (internal)
2. `LintResult` record struct → `LintResultData` (internal)
3. internal 参照をすべて追従（parser, lint engine, CLI, playground, tests）
4. この時点で public surface から旧名が消える

**Phase B: public class 名変更**

1. `OwnedParseResult` → `ParseResult`
2. `OwnedLintResult` → `LintResult`
3. public API ドキュメント・仕様書を更新

**Phase C: Arena 隠蔽 + 解決メソッド追加**

1. `Arena` property を `internal` に変更
2. `GetString`, `GetUtf8`, `GetSlice`, `GetBool`, `GetInt`, `GetFloat`, `GetRange`, `GetExpression`, `IsQuoted` を結果型に追加
3. テストを新パターン (`result.GetString(id)`) に更新
4. `Source` property 追加
5. `.Result` property を除去（`internal Data` に置換）

**Phase D: ParseDirect / CheckDirect 除去の検討**

- `ParseResult` class が async 対応なので、`ParseDirect` / `CheckDirect` の存在意義が消える
- ベンチマーク内部用途 (`IncrementalParseBenchmark` 等) は internal `ParseResultData` + arena 直接操作で十分
- 削除して public/internal 面積を縮小

---

##### リスクと対策

| リスク | 対策 |
|---|---|
| `GetRange` overload の ambiguity | 各 NodeId 型は distinct struct のため overload resolution で一意 |
| facade 設計との衝突 | facade は snapshot DTO を返す別パッケージ。Core API 変更は facade に影響しない |
| internal struct 名変更の影響範囲が大きい | IDE rename refactoring で安全に実施可能 |
| 解決メソッド追加で ParseResult class が肥大化 | 15 メソッド程度。IntelliSense で整理されるサイズ。interface extract は不要 |

---

##### 判定基準

- ベンチマーク: `Alloc Ratio = 1.00` を維持（解決メソッド自体はアロケーション増なし）
- テスト: 全テスト pass
- 利用者コード: `result.Arena.GetStringValue(...)` → `result.GetUtf8(...)` で短縮。Arena の概念が消える

##### 次回に詰める具体論点

1. `GetString` を string convenience API として常設するか、`DecodeString` など別名にするか。
2. `GetUtf8` / `GetSlice` / `Source` の 3 つをすべて public に出すか、advanced 度合いで整理するか。
3. `GetRange` / `GetExpression` を NodeId overload 群で揃えるか、共通 interface/utility に寄せるか。
4. `LintResult` に Parse 系と同じ accessor 群を完全対称で持たせるか、共通基底/共通 helper を使うか。
5. 利用者が IntelliSense だけで値の読み方を理解できる命名になっているかを、サンプルコード基準で再評価すること。

#### 利用者視点の API 分析と改善計画

##### 現状の public surface（実装結果）

```csharp
public sealed class ParseResult : IDisposable
{
    // AST
    public Workflow? Workflow { get; }
    public ActionMetadata? ActionMetadata { get; }
    public DiagnosticList Diagnostics { get; }
    public bool HasFatalError { get; }
    public ReadOnlySpan<byte> Source { get; }

    // Value resolution (15 methods)
    public string GetString(StringNodeId id);
    public ReadOnlySpan<byte> GetUtf8(StringNodeId id);
    public Utf8Slice GetSlice(StringNodeId id);
    public bool IsQuoted(StringNodeId id);
    public TextRange GetRange(StringNodeId id);
    public TextRange GetRange(BoolNodeId id);
    public TextRange GetRange(IntNodeId id);
    public TextRange GetRange(FloatNodeId id);
    public StringNodeId GetExpression(StringNodeId id);
    public StringNodeId GetExpression(BoolNodeId id);
    public StringNodeId GetExpression(IntNodeId id);
    public StringNodeId GetExpression(FloatNodeId id);
    public bool GetBool(BoolNodeId id);
    public long GetInt(IntNodeId id);
    public double GetFloat(FloatNodeId id);

    public OwnedDiagnostics CopyDiagnostics();
    public void Dispose();
}
```

`LintResult` も同構造 + lint 固有 properties。

##### 利用者コード（テストから再構成したパターン）

```csharp
// Parse → 値を読む
using var result = WorkflowParser.Parse(yaml, path);
var job = result.Workflow!.Jobs.Entries[0].Value;
var jobId = result.GetString(job.Id);       // ← "build"

// Lint → diagnostics を見る
using var lint = engine.Check(yaml, path);
foreach (var diag in lint.Diagnostics) { ... }
```

##### 評価: 良い点

1. **`using var result = ...` で完結する**: async 越え OK、フィールド保持 OK。C# の日常的なコード。
2. **Arena が公開 API から消えた**: 利用者は `result.GetString(id)` で直接値が取れる。メモリプールの概念を知らなくてよい。
3. **NodeId overload で型安全**: `GetRange(StringNodeId)` と `GetRange(BoolNodeId)` は overload resolution が一意で、IDE 補完も素直。
4. **Dispose 後の安全性**: `ObjectDisposedException` で明示的に壊れる。silent corruption がない。

##### 評価: 残る違和感

| 問題 | 具体例 | なぜ問題か |
|---|---|---|
| **`GetSlice` の用途が公開 API として不明瞭** | `result.GetSlice(id)` → `Utf8Slice` | `Utf8Slice` は offset+length のゼロコピーハンドル。lint rules が source 位置を特定するための internal 概念であり、通常の外部利用者は `GetString` か `GetUtf8` で済む。IntelliSense に並ぶと「3 つの文字列取得方法」が不必要に選択肢を増やす |
| **`GetExpression` の意味が直感的でない** | `result.GetExpression(job.Id)` → `StringNodeId` | 名前だけでは「何を返すのか」が分からない。実態は「この scalar が `${{ }}` embedded expression を持っていればその expression 部分の StringNodeId を返す」という内部パーサー概念。外部利用者にとって「式があるか確認して式テキストを取得する」は 2 段 API (`HasExpression` + `GetExpressionText`) の方が自然 |
| **`IsQuoted` は何のために必要？** | `result.IsQuoted(id)` → `bool` | YAML の引用符情報。fix engine が replacement text を生成する時に使う内部概念。外部利用者は「値」が欲しいだけで、引用符の有無はパーサー都合 |
| **`Source` が `ReadOnlySpan<byte>` で逃がせない** | `var src = result.Source;` | Span は ref struct なのでフィールドに保持できない。UTF-8 の生バイト列を返す妥当な設計だが、利用シーンが不明確。lint fix の byte offset 検証に使う internal 概念に近い |
| **`LintResult` が `ParseResult` の全メソッドを重複定義** | `LintResult.GetString(...)` | 1 概念 1 型を達成した代償で、同じ accessor が 2 つの結果型に完全対称にある。30 メソッドの表面積。将来のメンテコスト |
| **`LintConfig.Arena` が public のまま** | `config.Arena` property は public setter | `ParseResult.Arena` は internal にしたが、`LintConfig` が公開し続けている。外部利用者が `LintConfig` を自前構築して `.Arena` を触る想定はないはず |

##### 改善計画: Core API accessor の最終整理

**方針**: Core advanced API は性能最重視の利用者向け。ただし「公開する必要がないものは閉じる」原則に従い、internal で十分なものは internal に退避する。

**Step 1: public から除外すべきメソッド**

| メソッド | 理由 | 対応 |
|---|---|---|
| `GetSlice(StringNodeId)` | `Utf8Slice` は fix engine / lint rules 用。外部利用者は `GetUtf8` で十分 | `internal` に変更 |
| `IsQuoted(StringNodeId)` | fix engine 専用。外部からは不要 | `internal` に変更 |
| `GetExpression(StringNodeId/Bool/Int/Float)` | lint rules が expression embedding を検出する内部 API。外部利用者向けの自然な API ではない | `internal` に変更 |
| `Source` (ReadOnlySpan) | fix engine / advanced テスト向け。通常利用者は不要 | `internal` に変更 |

**Step 2: public に残すべきメソッド（最終 public surface）**

```csharp
public sealed class ParseResult : IDisposable
{
    // AST
    public Workflow? Workflow { get; }
    public ActionMetadata? ActionMetadata { get; }
    public DiagnosticList Diagnostics { get; }
    public bool HasFatalError { get; }

    // Value resolution — 利用者が必要とする 3 層
    public string GetString(StringNodeId id);              // convenience: デコード済み文字列
    public ReadOnlySpan<byte> GetUtf8(StringNodeId id);   // perf: zero-copy UTF-8 bytes
    public bool GetBool(BoolNodeId id);
    public long GetInt(IntNodeId id);
    public double GetFloat(FloatNodeId id);

    // Source location — diagnostic 表示に必要
    public TextRange GetRange(StringNodeId id);
    public TextRange GetRange(BoolNodeId id);
    public TextRange GetRange(IntNodeId id);
    public TextRange GetRange(FloatNodeId id);

    // Ownership
    public OwnedDiagnostics CopyDiagnostics();
    public void Dispose();
}
```

結果: **11 public methods** (値取得 5 + 位置取得 4 + copy 1 + dispose 1)。
現状 15 → 11 へ縮小。4 methods が internal に退避。

**Step 3: `LintConfig.Arena` の可視性**

- `LintConfig.Arena` を `internal set` に変更（getter は internal で十分）
- lint rules は `RuleBase.Arena` (protected, `Config.Arena!` を返す) 経由で引き続きアクセスできる
- 外部から `new LintConfig { Arena = ... }` と書くユースケースはない

**Step 4: 重複定義の対処**

- `ParseResult` と `LintResult` の accessor は完全対称を維持する。利用者は `Parse()` の結果でも `Check()` の結果でも同じ API で値を読めるべき
- ただし実装は共通化する: private static helper or shared extension で委譲し、メンテ負荷を減らす
- interface (`IValueResolver`) を抽出する案は、利用パターンがポリモーフィック（`ParseResult` と `LintResult` を統一的に扱う）な場合のみ意味がある。現時点では不要

**Step 5: `AstArena` の public visibility を消す最終判断**

現状:
- `AstArena` class 自体は `public sealed class`
- `ParseResult.Arena` は `internal`
- `LintConfig.Arena` は `public { get; set; }`
- `RuleBase.Arena` は `protected`
- `ReusableWorkflowRule` / `LocalActionInputsRule` が internal で Arena を触る

Core advanced API としても、**外部利用者が `AstArena` を直接触る正当なユースケースはない**:
- 値の読み出し → `result.GetString()` / `result.GetUtf8()` で完結
- lint rules を自前で書く → `RuleBase.Arena` (protected) で完結
- source bytes → 将来的に `result.GetSourceBytes()` (返り値 `ReadOnlyMemory<byte>`) で代替可能

判断: **`AstArena` を `internal` class に変更する**。

影響:
- `LintConfig.Arena` → internal property に変更
- `InternalsVisibleTo` で `Seiton.Benchmark`, `Seiton.Playground.Core`, `Seiton.Core.Tests` からは引き続きアクセス可能
- `NodeId` struct 群 (`StringNodeId`, `BoolNodeId`, `IntNodeId`, `FloatNodeId`) は public のまま（AST ノードのフィールドとして外部利用者が参照する）

##### 利用パターン比較: before → after

```csharp
// ── Before (今回の実装) ──────────────────────────────
using var result = WorkflowParser.Parse(yaml, path);
var job = result.Workflow!.Jobs.Entries[0].Value;
var id = result.GetString(job.Id);            // ✅ OK
var raw = result.GetUtf8(job.Id);             // ✅ OK
var slice = result.GetSlice(job.Id);          // ❓ 外部利用者に不要
var quoted = result.IsQuoted(job.Id);         // ❓ 外部利用者に不要
var expr = result.GetExpression(job.Id);      // ❓ 内部概念
var src = result.Source;                      // ❓ Span で逃がせない

// ── After (改善後) ────────────────────────────────────
using var result = WorkflowParser.Parse(yaml, path);
var job = result.Workflow!.Jobs.Entries[0].Value;
var id = result.GetString(job.Id);            // ✅ string (convenience)
var raw = result.GetUtf8(job.Id);             // ✅ zero-copy bytes (perf)
var range = result.GetRange(job.Id);          // ✅ source location
// GetSlice, IsQuoted, GetExpression, Source → IDE に出ない
```

IntelliSense で `result.` と打った時に見えるメソッドが 15 → 11 に減り、全てが「利用者がやりたいこと」に直結する。

##### 判定基準

| 基準 | 閾値 |
|---|---|
| Allocation | `Alloc Ratio = 1.00` を維持 |
| Performance | parse/lint hot path に変更なし (accessor は JIT inline 委譲のみ) |
| テスト | 全テスト pass |
| API discoverability | `result.` の補完で表示されるメソッドが全て利用者タスクに直結すること |
| 内部互換 | lint rules の書き方 (`Arena.GetStringValue(id)` via protected) は変更なし |

### 3. スレッドセーフでない型がある

**論点**:

- [`LintEngine`](../../src/Seiton.Core/Linting/LintEngine.cs) は内部状態を再利用するため、同時実行安全ではない
- ライブラリ利用者は singleton 登録や並列処理で誤用しやすい

**対策案**:

- facade は stateless static API または per-call インスタンス生成に寄せる
- `LintEngine` をそのまま公開し続ける場合は thread-safety を明示する
- 並列利用ガイドとサンプルを README に追加する

### 4. 入力 API が低レベルすぎる

**論点**:

- 現在の主要 API は `byte[] utf8Yaml` を要求する
- 多くの利用者は `string`、`Stream`、`FileInfo`、`TextReader` ベースの API を期待する

**対策案**:

- facade に `string` / `byte[]` オーバーロードを用意する
- `Stream` 対応は必要性を見て追加する
- `filePath` は必須のまま維持し、診断位置と document kind 推定に使う

### 5. ターゲットフレームワークが `net10.0` 固定

**論点**:

- [`Seiton.Core.csproj`](../../src/Seiton.Core/Seiton.Core.csproj) は `net10.0` のみ
- ライブラリ利用者としては `net8.0` / `net9.0` を求める可能性が高い

**対策案**:

- `net8.0;net10.0` もしくは `net8.0;net9.0;net10.0` の multi-target を検討する
- AOT analyzer や API availability の差分がある場合は conditional compile を導入する
- もし `net10.0` のみを維持するなら、その理由を README と package description に明記する

### 6. パッケージング設定が未整備

**論点**:

- pack 不可設定のまま
- `PackageId`、説明文、ライセンス、README 埋め込みなどが未設定

**対策案**:

- `src/Seiton.Core/Seiton.Core.csproj` で `IsPackable=true` を明示する
- 必要な NuGet metadata を追加する
- パッケージ README とサンプルコードを含める
- `PackageTags` に `github-actions`, `yaml`, `linter`, `security`, `aot` などを設定する

### 7. 依存関係と transitive dependency の扱い

**論点**:

- `VYaml` が外部依存として露出する
- 将来的に依存入れ替えが起きると、公開 API への影響が出る可能性がある

**対策案**:

- facade では `VYaml` 型を public surface に出さない
- 依存ライブラリ由来の例外や型をそのまま露出しない
- transitive dependency の更新ポリシーを release note に残す

### 8. オンラインルールとネットワーク依存の扱い

**論点**:

- online audit、GitHub API、SHA/digest pin 解決はネットワークと認証を伴う
- ライブラリ利用者は「オフライン lint」と「オンライン拡張 lint」を分けて扱いたい可能性が高い

**対策案**:

- 初回公開では offline Parse / Lint を主 API にする
- online rule は opt-in の別 API または別 namespace に分離する
- `HttpClient` / resolver interface を DI 可能にしてテストしやすくする
- タイムアウト、リトライ、認証、失敗時ポリシーをオプションで明示する

### 9. Fix API は破壊的編集を伴う

**論点**:

- fix はファイル書き換え、revalidation、network-assisted pinning まで関わる
- ライブラリ API としては Parse / Lint より設計難度が高い

**対策案**:

- 初回公開では Parse / Lint を優先し、Fix は後続フェーズに分ける
- Fix を公開する場合は「差分を返す API」と「適用する API」を分離する
- ファイル I/O を伴う API よりも、text edit ベース API を優先する

### 10. DocumentKind と filePath 推定の扱い

**論点**:

- `WorkflowParser.ParseClassified` は `filePath` をヒントに kind 推定する
- 呼び出し側が仮想入力を渡すとき、ファイル名がないと期待結果がぶれうる

**対策案**:

- facade API でも `filePath` は基本必須にする
- stdin 的なケース向けに `<memory>` や `action.yml` 相当の path hint を許す
- kind を明示指定できるオーバーロードを後続で検討する

### 11. 公開後の破壊的変更管理

**論点**:

- 一度公開した public 型は、内部都合で簡単に変えられなくなる
- まだ仕様が固まり切っていない領域まで公開すると SemVer 運用が破綻しやすい

**対策案**:

- 安定 API と experimental API を分ける
- experimental には namespace / パッケージ / ドキュメント上の明示を入れる
- breaking change は release note と upgrade guide を必須化する
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` などを使って CI で API 変更検知する

### 12. サンプルとドキュメント不足

**論点**:

- ライブラリは CLI よりも README のサンプル品質が重要
- 利用者は「最短で 10 行くらいのコード」が欲しい

**対策案**:

- パッケージ README に最小サンプルを置く
- Parse only / Lint only / config あり / diagnostics snapshot の 4 パターンを示す
- `sandbox/DotnetFiles/` にライブラリ利用例を追加して検証を兼ねる

### 13. テストと検証観点の追加が必要

**論点**:

- 今のテストは主に CLI / core 実装前提であり、NuGet 消費者視点の API 契約テストが薄い

**対策案**:

- facade API の契約テストを追加する
- pack 後に別サンプルプロジェクトから package 参照して動かす integration test を追加する
- 診断配列保持、並列利用、config 読み込み、オンライン rule 無効時の挙動を検証する

### 14. パッケージ名・責務の衝突

**論点**:

- `Seiton` は CLI、`Seiton.Core` は実装、将来 `dotnet tool` も NuGet に載る
- 同じ NuGet 上で役割が近いパッケージが増えると利用者が混乱する

**対策案**:

- 命名ポリシーを先に決める
- 例:
  - `Seiton` = CLI / dotnet tool 用
  - `Seiton.Core` = 内部実装寄り、非推奨公開または上級者向け
  - `Seiton.SDK` = 外部利用向け安定ライブラリ
- package description に責務を明記する

## 推奨フェーズ

### フェーズ 0 — パッケージ戦略の決定

**WHY**: `Seiton.Core` をそのまま公開するか、facade を別パッケージにするかで以後の設計が大きく変わる。

#### 実施内容

- `Seiton.Core` 直公開か `Seiton.SDK` 分離かを決定する
- 安定 API と非安定 API の境界を決める
- package naming policy を確定する

**完了条件**: 公開対象パッケージ名、責務、保証する public API の範囲が文書化される。

---

### フェーズ 1 — facade API の設計

**WHY**: 現在の API は実装都合が強く、外部利用者が安全に使うには facade が必要。

#### 実施内容

- caller-owned result DTO を設計する
- Parse / Lint 用の最小 API を定義する
- config / online rule / fix の扱いを切り分ける
- lifetime / thread-safety を facade で隠蔽する

**完了条件**: 外部公開予定 API の C# シグネチャが固まり、サンプルコードを書ける。

---

### フェーズ 2 — csproj と packaging の整備

**WHY**: pack / publish できる形にしないと評価も配布もできない。

#### 実施内容

- `IsPackable=true`
- `PackageId`, `Description`, `License`, `ProjectUrl`, `RepositoryUrl`, `PackageReadmeFile` を設定
- target framework を再検討し必要なら multi-target 化する

**完了条件**: `dotnet pack` でローカル package が生成できる。

---

### フェーズ 3 — API 契約テストと消費者テスト

**WHY**: ライブラリ公開では CLI テストだけでは足りない。

#### 実施内容

- facade API の unit test を追加
- package を参照する sample / test project を追加
- parallel use、diagnostic snapshot、config あり / なしを検証
- 公開 API 差分チェックを CI に入れる

**完了条件**: pack した package を別プロジェクトから参照して Parse / Lint が通る。

---

### フェーズ 4 — README / サンプル / リリース自動化

**WHY**: パッケージ公開後の利用体験は README でほぼ決まる。

#### 実施内容

- NuGet README を整備
- 最小サンプルと注意点を記載
- release workflow に `dotnet pack` / `dotnet nuget push` を追加
- API 互換性・変更点を release note に出す

**完了条件**: NuGet から install し、README を見て最小コードで利用できる。

---

### フェーズ 5 — online rule / fix API の拡張

**WHY**: これらは価値が高いが設計難度も高いため、コア公開の後段に分ける。

#### 実施内容

- online lint API を opt-in で設計
- resolver / `HttpClient` 注入を整理
- fix API を text edit 중심に公開
- file I/O と純粋計算 API を分離

**完了条件**: Parse / Lint の安定 API を壊さずに online / fix を追加できる。

## 初回公開で推奨するスコープ

初回公開では次に絞ることを推奨する。

- Parse
- Lint（offline rules のみ）
- `LintConfig` もしくは facade 用 options
- caller-owned diagnostics/result DTO

初回では見送る候補:

- online audit
- file system を伴う fix 適用 API
- 低レベル AST mutation API
- internal performance-oriented helper 型の公開

## リスクと対策

| リスク | 影響 | 対策 |
|--------|------|------|
| public API を広げすぎる | 将来の最適化が破壊的変更になる | facade に絞る、API 差分チェックを CI 化 |
| pooled result を利用者が保持する | use-after-dispose 相当のバグ | immutable snapshot DTO を返す |
| `net10.0` 固定で利用者が限られる | 採用障壁が高い | multi-target を検討、固定理由を明示 |
| online/fix まで初回に盛り込む | 設計が複雑化し公開が遅れる | 初回は Parse / Lint に限定 |
| CLI と library package の責務が曖昧 | 利用者が混乱する | package naming と責務文書を先に決める |
| SemVer 運用が曖昧 | 利用者が更新しづらい | 安定 API 面を固定し、breaking change policy を明記 |

## まとめ

`Seiton.Core` は技術的にはすでに外部公開可能な機能を持っているが、そのまま公開すると内部実装の都合が public 契約になりすぎる。公開するなら、**安定 facade を定義し、lifetime / thread-safety / API surface を整理した上で NuGet 化する**のが最も安全である。

最初の一歩としては、`Seiton.Core` 直公開か `Seiton.SDK` 分離かを決め、その前提で facade API の最小スコープを固定するのがよい。
