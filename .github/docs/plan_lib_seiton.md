# Seiton.Core ライブラリ公開計画

本書は `Seiton.Core` を NuGet ライブラリとして公開し、他の .NET アプリケーションから Seiton の Parse / Lint / Fix 機能を利用可能にするために、考慮すべき論点と対策案を整理したものである。

対象は主に `src/Seiton.Core/` の公開であり、CLI 配布（`src/Seiton/`）やインストールチャネルの整備は本書の主題ではない。

## 現状

`Seiton.Core` はすでに parser / linter の実装本体として成立しており、外部利用の入口になりうる public API も一部存在する。

- [`WorkflowParser`](../../src/Seiton.Core/Parsing/WorkflowParser.cs) に `Parse`（`ParseHandle` を返す）/ `ParseClassified` がある
- [`LintEngine`](../../src/Seiton.Core/Linting/LintEngine.cs) に `Check(byte[] utf8Yaml, string filePath, LintConfig? config)` がある（`LintHandle` を返す）
- [`ParseResult`](../../src/Seiton.Core/Parsing/Diagnostics.cs) と [`LintResult`](../../src/Seiton.Core/Linting/LintResult.cs) は public
- [`ParseHandle`](../../src/Seiton.Core/Parsing/ParseHandle.cs) と [`LintHandle`](../../src/Seiton.Core/Linting/LintHandle.cs) は `ref struct : IDisposable` で Arena ライフタイムを管理
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
- ~~外部利用者は `Arena.Dispose()` 前提を知らずに診断配列を保持しがち~~ → **対策済み**: `ParseHandle` / `LintHandle` (`ref struct : IDisposable`) が Arena を隠蔽し、スコープ外保持をコンパイラが禁止する

**本質的な問題の構造**:

`ParseResult` / `LintResult` は値型 (struct) だが、中身は pooled memory への borrowed reference。Rust で言えば lifetime パラメータなしの借用を返しているのと同じ。

| 型 | 所有権 | 利用者への露出 | 危険性 |
|---|---|---|---|
| `AstArena` | ThreadStatic pool | `ParseHandle` / `LintHandle` 内部 (internal) | Handle の Dispose で安全に返却 |
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

1. ✅ **`ParseResult` から `Arena?` フィールドを除去する** — Arena は結果の一部ではなくリソース管理の関心事。`ref struct ParseHandle` でまとめる。
2. ✅ **`DiagnosticList` を borrowed / owned で型レベル区別する** — `OwnedDiagnostics` 型を導入し、`CopyDiagnostics()` の返り値で所有権を明示。
3. ✅ **`LintEngine.Check()` の返り値を `ref struct` にする（.NET 10）** — `using var result = engine.Check(...)` で自然に Dispose。`ref struct` なのでフィールドに保持不可（コンパイラ強制）。
4. ✅ **AST の外部公開は read-only snapshot を別型で返し、内部は pooled class を維持** — `OwnedParseResult` / `OwnedLintResult` + `Detach()` で所有権移転。

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

#### 計画: ref struct ハンドル廃止 → Parse/Check が直接 class を返す（項目 1,3,4 統合リファクタ）

**動機**:

項目 1,3 で導入した `ParseHandle` / `LintHandle` (ref struct) は「スコープ外で使えない」コンパイラ制約を活かす設計だったが、利用者視点で以下の問題がある:

- ref struct は async 不可・フィールド保持不可・クロージャ不可 → テストコード（TUnit は全 async）で使えず `ParseDirect`/`CheckDirect` internal ハックが必要
- 項目 4 の `Detach()` パターンが冗長: `{ using var h = Parse(...); owned = h.Detach(); } using (owned) { ... }`
- 「class + IDisposable + using」で十分な安全性。ref struct の制約は利用者に不要な負荷

**設計方針**: `Parse()` / `Check()` が直接 `OwnedParseResult` / `OwnedLintResult` (class, IDisposable) を返す。ParseHandle / LintHandle / Detach() / ParseDirect / CheckDirect をすべて廃止。

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

**削除対象**:

| ファイル/メソッド | 理由 |
|---|---|
| `ParseHandle.cs` | OwnedParseResult に統合 |
| `LintHandle.cs` | OwnedLintResult に統合 |
| `Detach()` | Parse()/Check() が直接 Owned を返す |
| `WorkflowParser.ParseDirect()` | Parse() が async で使えるので不要 |
| `LintEngine.CheckDirect()` | Check() が async で使えるので不要 |

**変更ファイル一覧**:

| カテゴリ | ファイル数 | 内容 |
|---|---|---|
| Core 型変更 | 6 | OwnedParseResult/OwnedLintResult 再設計, WorkflowParser/LintEngine 返り値変更, ParseHandle/LintHandle 削除 |
| Production callers | 8 | FixEngine, CheckCommand, FixCommand, PlaygroundLintRunner, 4 lint rules — プロパティ名同一のため機械的置換 |
| Benchmarks | 4 | CheckDirect→Check, using 追加 |
| Tests | ~8ファイル, ~230箇所 | ParseDirect→Parse, CheckDirect→Check, try/finally arena.Dispose() 除去, DetachTests 全面簡素化 |
| Doc/Comments | 3 | XML doc 参照更新 |
| Sandbox | ~15 | using 追加のみ（メソッド名同一） |

**実装順序**:

1. OwnedParseResult / OwnedLintResult を再設計（ParseResult/LintResult ラッパー化）
2. WorkflowParser.Parse() → OwnedParseResult, LintEngine.Check() → OwnedLintResult に変更
3. ParseDirect/CheckDirect を削除（コンパイルエラーで全呼び出し元を検出）
4. ParseHandle.cs / LintHandle.cs を削除
5. Production callers 修正 (FixEngine, CLI, Playground, lint rules)
6. Benchmark 修正
7. Tests 修正（機械的置換）
8. Sandbox 修正
9. ベンチマーク実行で回帰確認 (OwnedParseResult class alloc ≈ +32B 許容)
10. DetachTests.cs を OwnedResultTests.cs にリネーム・簡素化

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
