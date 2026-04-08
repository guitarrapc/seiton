# Seiton Parser Specification

> Go actionlint と同等の構文解析・AST 構築・Visitor 巡回・式検証を再現できることを目的とする。

## 0. 前提：現行 C# 実装の Gap 分析

### 0.1 actionlint (Go) に対して不足している機能

以下は `.references/actionlint-main` の実装と `src/Seiton.Core/Parsing` の差分。

| カテゴリ | actionlint で実装済み | 現行 C# の状態 |
|---|---|---|
| **AST 構築** | Typed AST（`Workflow`, `Job`, `Step`, …）をパーサーが返す | `WorkflowDocument` は `HasName` / `HasJobs` 等のフラグのみ。Job/Step/Event の typed node がない |
| **Event 詳細パース** | `schedule`, `workflow_dispatch`, `workflow_call`, `repository_dispatch`, `image_version` それぞれ専用パーサー | `on` のキー名検証とオプション検証はあるが、構造化 AST node を生成しない |
| **workflow_dispatch inputs** | `type` (string/number/boolean/choice/environment)、`options`、`required`、`default` を個別パース | 未実装 |
| **workflow_call inputs/secrets/outputs** | inputs に `type` 必須検証、secrets に `required` 検証、outputs に `value` 必須検証 | 未実装 |
| **schedule の cron/timezone** | mapping の `cron` / `timezone` キーを個別にパース | 未実装 |
| **Permissions 構造** | scalar (`read-all` / `write-all`) or mapping (scope → value) を typed node で返す | skip のみ |
| **Defaults / Concurrency** | `defaults.run.shell`, `defaults.run.working-directory` を typed node で返す | skip のみ |
| **Environment** | scalar (name) or mapping (`name`, `url`, `deployment`) を typed node で返す | 未実装 |
| **Runner (runs-on)** | scalar/sequence → labels, mapping → `labels` + `group`, expression 対応 | shape 検証のみ、typed node なし |
| **Step ExecRun / ExecAction** | `run` step → `ExecRun`、`uses` step → `ExecAction` を variant で持つ。Docker step は `entrypoint` / `args` を分けてパース | `hasRun` / `hasUses` フラグのみ |
| **Matrix & Strategy** | `matrix` の row/include/exclude を `RawYAMLValue` で再帰パース、`fail-fast` / `max-parallel` を typed | shape 検証のみ |
| **Container / Services** | `Container` node (image, credentials, env, ports, volumes, options)、Services は `map[string]*Service` | shape 検証のみ |
| **YAML Alias 解決** | パース前に全 alias を解決し、再帰 alias を検出・エラー化 | 未実装（VYaml が内部で解決する可能性あり） |
| **Duplicate key 検出** | mapping 走査時にキー重複を case-insensitive で検出 | 未実装 |
| **Visitor / Pass** | `Pass` interface → `WorkflowPre → JobPre → Step → JobPost → WorkflowPost` | 存在しない |
| **Rule Engine** | `Rule` interface × 15+ ルール | 存在しない |
| **式の型システム** | `ExprType` 階層 + `ExprSemanticsChecker` で型推論・可用性検証 | `ExpressionSemanticAnalyzer` に context root / function arity のみ。型推論なし |
| **式 AST ノード** | `VariableNode`, `ObjectDerefNode`, `ArrayDerefNode`, `IndexAccessNode`, `NotOpNode`, `CompareOpNode`, `LogicalOpNode`, `FuncCallNode` | 相当ノードあり。ただし `ObjectDerefNode`（`.` アクセス）と `ArrayDerefNode`（`.*` アクセス）の区別は `MemberAccess` / `WildcardAccess` で代替済み |
| **Generated data** | `all_webhooks.go`, `availability.go`, `popular_actions.go` | `OnEventSpecs` で webhook イベント + activity types を手実装。availability / popular actions は未実装 |

### 0.2 ghalint から補完すべき観点

| 観点 | 内容 |
|---|---|
| 多態的 YAML フィールド | `permissions` (scalar or mapping)、`container` (scalar or mapping)、`secrets` (`"inherit"` or mapping) のカスタム解析パターン — 現行 C# は `secrets` のみ対応済み |
| ポリシー対象モデルの必要最小性 | ghalint は必要フィールドだけ struct 定義。本仕様はフル AST を構築するが、将来のルール追加を見据え actionlint の Job / Step 全フィールドを一通り持つ |

### 0.3 zizmor から補完すべき観点

| 観点 | 内容 |
|---|---|
| `${{ }}` 式の fenced 抽出 | C# 実装済み（`ExpressionExtractor`） |
| JSON Schema による補助検証 | 将来的に vendored schema で実施。本パーサー仕様のスコープ外 |
| コンテキスト危険度テーブル（`context-capabilities`） | generated data として管理。パーサーではなくルール層の仕事 |

---

## 1. パーサー全体フロー

```
  ┌────────────────────────────────────────────────────────────────┐
  │                     Linter.Check()                            │
  │                                                              │
  │  1. Parse(utf8Yaml)                                          │
  │     ┌─────────────────────────────────────────────┐          │
  │     │  YAML Adapter Layer (腐敗防止層)             │          │
  │     │  IYamlStreamReader ← VYamlStreamAdapter     │          │
  │     │                    ← (将来: YamlDotNet 等)  │          │
  │     └─────────────────────────────────────────────┘          │
  │     1a. IYamlStreamReader 経由で YAML イベント読み取り       │
  │     1b. Alias 解決 (adapter 層 or YAML ライブラリ内部)       │
  │     1c. WorkflowParser.parse() → Workflow AST                │
  │     1d. syntax diagnostics 収集                              │
  │  2. Rule 群を構築                                            │
  │  3. Visitor.Visit(workflow)                                  │
  │     WorkflowPre → JobPre → Step → JobPost → WorkflowPost    │
  │  4. 各 Rule から diagnostics 収集                            │
  │  5. FilterErrors → Sort + Dedup → 出力                      │
  └────────────────────────────────────────────────────────────────┘
```

### 1.1 エントリポイント

```
public static ParseResult Parse(byte[] utf8Yaml, string filePath)
```

- 戻り値: `ParseResult { Workflow?, Diagnostic[], HasFatalError }`
- YAML パース自体が失敗しても `Diagnostic[]` を返却し、`Workflow` は null
- YAML パース成功後に parser が AST を構築。AST 構築中のエラーは即中断せず蓄積

### 1.2 参照実装の対応

| actionlint (Go) | seiton (C#) |
|---|---|
| `yaml.Unmarshal` → `yaml.Node` tree | `IYamlStreamReader` 経由で YAML イベントストリームを読み取り |
| `parser.resolveAliases()` | adapter 層（`IYamlStreamReader` 実装）または YAML ライブラリ内部で実施 |
| `parser.parse()` → `*Workflow` | `WorkflowParser.Parse()` → `Workflow` AST node |
| `linter.check()` で Rule 群構築 + Visitor | `LintEngine.Check()` で同等構成 |

---

## 2. AST 定義

### 2.1 設計原則

1. 全主要ノードは `TextRange` を持つ
2. scalar 値は原則 `Utf8Slice` で保持し、必要時にのみ文字列化
3. nullable フィールドは `T?` で「YAML 上で省略された」を表現
4. 式が使える場所は `Expression` フィールドを持つ（`string? Expression` or `Utf8Slice`）
5. ノードは `readonly record struct` を基本とし、可変長要素は配列で持つ

### 2.2 Workflow（ルート）

```csharp
public sealed class Workflow
{
    public StringNode? Name { get; init; }
    public StringNode? RunName { get; init; }
    public Event[] On { get; init; } = [];
    public Permissions? Permissions { get; init; }
    public Env? Env { get; init; }
    public Defaults? Defaults { get; init; }
    public Concurrency? Concurrency { get; init; }
    public Dictionary<string, Job> Jobs { get; init; } = new();
    public TextRange Range { get; init; }
}
```

### 2.3 Event（`on:` セクション）

```csharp
public abstract class Event
{
    public abstract string EventName { get; }
    public TextRange Range { get; init; }
}

public sealed class WebhookEvent : Event { ... }
public sealed class ScheduledEvent : Event { ... }
public sealed class WorkflowDispatchEvent : Event { ... }
public sealed class RepositoryDispatchEvent : Event { ... }
public sealed class WorkflowCallEvent : Event { ... }
```

#### 2.3.1 WebhookEvent

```csharp
public sealed class WebhookEvent : Event
{
    public override string EventName => Hook.Value;
    public StringNode Hook { get; init; }
    public StringNode[]? Types { get; init; }
    public WebhookEventFilter? Branches { get; init; }
    public WebhookEventFilter? BranchesIgnore { get; init; }
    public WebhookEventFilter? Tags { get; init; }
    public WebhookEventFilter? TagsIgnore { get; init; }
    public WebhookEventFilter? Paths { get; init; }
    public WebhookEventFilter? PathsIgnore { get; init; }
    public StringNode[]? Workflows { get; init; }  // workflow_run のみ
}

public sealed class WebhookEventFilter
{
    public StringNode Name { get; init; }
    public StringNode[] Values { get; init; } = [];
}
```

#### 2.3.2 ScheduledEvent

```csharp
public sealed class ScheduledEvent : Event
{
    public override string EventName => "schedule";
    public ScheduleEntry[] Schedules { get; init; } = [];
}

public sealed class ScheduleEntry
{
    public StringNode? Cron { get; init; }
    public StringNode? Timezone { get; init; }
}
```

#### 2.3.3 WorkflowDispatchEvent

```csharp
public sealed class WorkflowDispatchEvent : Event
{
    public override string EventName => "workflow_dispatch";
    public Dictionary<string, DispatchInput>? Inputs { get; init; }
}

public sealed class DispatchInput
{
    public StringNode Name { get; init; }
    public StringNode? Description { get; init; }
    public BoolNode? Required { get; init; }
    public StringNode? Default { get; init; }
    public DispatchInputType Type { get; init; }
    public StringNode[]? Options { get; init; }
}

public enum DispatchInputType
{
    None, String, Number, Boolean, Choice, Environment
}
```

#### 2.3.4 WorkflowCallEvent

```csharp
public sealed class WorkflowCallEvent : Event
{
    public override string EventName => "workflow_call";
    public WorkflowCallEventInput[]? Inputs { get; init; }
    public Dictionary<string, WorkflowCallEventSecret>? Secrets { get; init; }
    public Dictionary<string, WorkflowCallEventOutput>? Outputs { get; init; }
}

public sealed class WorkflowCallEventInput
{
    public StringNode Name { get; init; }
    public string Id { get; init; }              // lower-case
    public StringNode? Description { get; init; }
    public BoolNode? Required { get; init; }
    public StringNode? Default { get; init; }
    public WorkflowCallInputType Type { get; init; }  // boolean/number/string — 必須
}

public enum WorkflowCallInputType { Invalid, Boolean, Number, String }

public sealed class WorkflowCallEventSecret { ... }  // Name, Description, Required
public sealed class WorkflowCallEventOutput { ... }  // Name, Description, Value(必須)
```

#### 2.3.5 RepositoryDispatchEvent

```csharp
public sealed class RepositoryDispatchEvent : Event
{
    public override string EventName => "repository_dispatch";
    public StringNode[]? Types { get; init; }
}
```

### 2.4 Job

```csharp
public sealed class Job
{
    public StringNode Id { get; init; }
    public StringNode? Name { get; init; }
    public StringNode[]? Needs { get; init; }
    public Runner? RunsOn { get; init; }
    public Permissions? Permissions { get; init; }
    public Environment? Environment { get; init; }
    public Concurrency? Concurrency { get; init; }
    public Dictionary<string, Output>? Outputs { get; init; }
    public Env? Env { get; init; }
    public Defaults? Defaults { get; init; }
    public StringNode? If { get; init; }
    public Step[]? Steps { get; init; }
    public FloatNode? TimeoutMinutes { get; init; }
    public Strategy? Strategy { get; init; }
    public BoolNode? ContinueOnError { get; init; }
    public Container? Container { get; init; }
    public Services? Services { get; init; }
    public WorkflowCall? WorkflowCall { get; init; }  // uses: による reusable workflow 呼び出し
    public TextRange Range { get; init; }
}
```

### 2.5 Step

```csharp
public sealed class Step
{
    public StringNode? Id { get; init; }
    public StringNode? If { get; init; }
    public StringNode? Name { get; init; }
    public StepExec Exec { get; init; }          // ExecRun or ExecAction
    public Env? Env { get; init; }
    public BoolNode? ContinueOnError { get; init; }
    public FloatNode? TimeoutMinutes { get; init; }
    public TextRange Range { get; init; }
}

public abstract class StepExec
{
    public abstract StepExecKind Kind { get; }
}
public enum StepExecKind { Run, Action }

public sealed class ExecRun : StepExec
{
    public override StepExecKind Kind => StepExecKind.Run;
    public StringNode Run { get; init; }
    public StringNode? Shell { get; init; }
    public StringNode? WorkingDirectory { get; init; }
    public TextRange RunRange { get; init; }
}

public sealed class ExecAction : StepExec
{
    public override StepExecKind Kind => StepExecKind.Action;
    public StringNode Uses { get; init; }
    public Dictionary<string, Input>? Inputs { get; init; }   // with:
    public StringNode? Entrypoint { get; init; }   // docker only
    public StringNode? Args { get; init; }          // docker only
}
```

### 2.6 共通ノード型

```csharp
// 位置つき文字列値
public sealed class StringNode
{
    public string Value { get; init; }
    public bool Quoted { get; init; }
    public TextRange Range { get; init; }
    public bool ContainsExpression() => Value.Contains("${{");
    public bool IsExpressionAssigned() { /* ${{ ... }} が単一で丸ごと代入か */ }
}

// Boolean（リテラル or 式）
public sealed class BoolNode
{
    public bool Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

// Int（リテラル or 式）
public sealed class IntNode
{
    public int Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

// Float（リテラル or 式）
public sealed class FloatNode
{
    public double Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.7 Permissions

```csharp
public sealed class Permissions
{
    public StringNode? All { get; init; }                             // "read-all", "write-all", "{}"
    public Dictionary<string, PermissionScope>? Scopes { get; init; }
    public TextRange Range { get; init; }
}

public sealed class PermissionScope
{
    public StringNode Name { get; init; }
    public StringNode Value { get; init; }
}
```

### 2.8 Env

```csharp
public sealed class Env
{
    public Dictionary<string, EnvVar>? Vars { get; init; }
    public StringNode? Expression { get; init; }   // env 全体が ${{ }} の場合
}
```

### 2.9 Defaults

```csharp
public sealed class Defaults
{
    public DefaultsRun? Run { get; init; }
    public TextRange Range { get; init; }
}

public sealed class DefaultsRun
{
    public StringNode? Shell { get; init; }
    public StringNode? WorkingDirectory { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.10 Concurrency

```csharp
public sealed class Concurrency
{
    public StringNode? Group { get; init; }          // 必須
    public BoolNode? CancelInProgress { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.11 Environment

```csharp
public sealed class Environment
{
    public StringNode? Name { get; init; }   // 必須
    public StringNode? Url { get; init; }
    public BoolNode? Deployment { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.12 Runner (runs-on)

```csharp
public sealed class Runner
{
    public StringNode[]? Labels { get; init; }
    public StringNode? LabelsExpr { get; init; }  // ${{ }} で labels 指定
    public StringNode? Group { get; init; }
}
```

### 2.13 Strategy / Matrix

```csharp
public sealed class Strategy
{
    public Matrix? Matrix { get; init; }
    public BoolNode? FailFast { get; init; }
    public IntNode? MaxParallel { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Matrix
{
    public Dictionary<string, MatrixRow> Rows { get; init; } = new();
    public MatrixCombinations? Include { get; init; }
    public MatrixCombinations? Exclude { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

public sealed class MatrixRow
{
    public StringNode? Name { get; init; }
    public RawYamlValue[]? Values { get; init; }
    public StringNode? Expression { get; init; }
}

public abstract class RawYamlValue { ... }
public sealed class RawYamlString : RawYamlValue { ... }
public sealed class RawYamlArray : RawYamlValue { ... }
public sealed class RawYamlObject : RawYamlValue { ... }
```

### 2.14 Container / Services

```csharp
public sealed class Container
{
    public StringNode? Image { get; init; }       // 必須
    public Credentials? Credentials { get; init; }
    public Env? Env { get; init; }
    public StringNode[]? Ports { get; init; }
    public StringNode[]? Volumes { get; init; }
    public StringNode? Options { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Services
{
    public Dictionary<string, Service>? Value { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}

public sealed class Service
{
    public StringNode Name { get; init; }
    public Container Container { get; init; }
}

public sealed class Credentials
{
    public StringNode? Username { get; init; }
    public StringNode? Password { get; init; }
    public StringNode? Expression { get; init; }
    public TextRange Range { get; init; }
}
```

### 2.15 WorkflowCall (job-level reusable workflow)

```csharp
public sealed class WorkflowCall
{
    public StringNode Uses { get; init; }
    public Dictionary<string, WorkflowCallInput>? Inputs { get; init; }
    public Dictionary<string, WorkflowCallSecret>? Secrets { get; init; }
    public bool InheritSecrets { get; init; }
}
```

---

## 3. パーサー実装仕様

### 3.1 基本設計

- **hand-written recursive descent** over YAML イベントストリーム
- パーサー本体は **`IYamlStreamReader` インターフェース** のみに依存し、具体的な YAML ライブラリ（VYaml, YamlDotNet 等）を直接参照しない
- VYaml 固有の型・API は **アダプター層**（`VYamlStreamAdapter`）に閉じ込める
- 全 parse 関数は `IYamlStreamReader` を引き回し、状態を共有
- エラーは `List<Diagnostic>` に蓄積し、**解析を中断しない**（multi-error recovery）

### 3.1A YAML アダプター層（腐敗防止層）

パーサー本体と YAML ライブラリの間に **Anti-Corruption Layer** を置く。
この層により、YAML シリアライザ／デシリアライザを差し替えても、パーサー本体に変更が波及しない。

#### 3.1A.1 アーキテクチャ

```
┌───────────────────────────────────────────────────────────┐
│  WorkflowParser / parse 関数群                            │
│  (YAML ライブラリ非依存)                                  │
└────────────────────┬──────────────────────────────────────┘
                     │ depends on
                     ▼
┌───────────────────────────────────────────────────────────┐
│  IYamlStreamReader (interface)                            │
│  パーサーが依存する最小の YAML 読み取り契約               │
└────────────────────┬──────────────────────────────────────┘
                     │ implemented by
          ┌──────────┼──────────┐
          ▼          ▼          ▼
   VYamlStream   (将来)      FakeYaml
   Adapter       YamlDotNet  StreamReader
                 Adapter     (テスト用)
```

#### 3.1A.2 IYamlStreamReader インターフェース

パーサー本体が依存する **唯一の YAML 読み取り契約**。

```csharp
/// <summary>
/// YAML イベントストリームの読み取り抽象。
/// パーサー本体はこのインターフェースのみに依存し、
/// 具体的な YAML ライブラリを参照しない。
/// </summary>
public interface IYamlStreamReader
{
    // --- 状態参照 ---
    YamlEventKind CurrentKind { get; }
    bool End { get; }

    // --- 読み進め ---
    bool Read();
    void SkipCurrentNode();
    void SkipAfter(YamlEventKind kind);

    // --- Scalar 値取得 ---
    ReadOnlySpan<byte> GetScalarUtf8();
    Utf8Slice GetScalarSlice();
    string? GetScalarString();       // diagnostics / fallback 用
    ScalarTag GetScalarTag();        // !!str, !!bool, !!int, !!float, !!null
    bool IsScalarQuoted();           // single/double quoted

    // --- 位置情報 ---
    TextPosition CurrentStart { get; }   // 行・列・バイトオフセット
    TextPosition CurrentEnd { get; }
}
```

#### 3.1A.3 自前列挙型

パーサー本体が参照する YAML イベント種別・タグ種別は、YAML ライブラリ非依存の自前 enum とする。

```csharp
/// YAML イベント種別（YAML ライブラリ非依存）
public enum YamlEventKind
{
    None,
    StreamStart,
    StreamEnd,
    DocumentStart,
    DocumentEnd,
    MappingStart,
    MappingEnd,
    SequenceStart,
    SequenceEnd,
    Scalar,
    Alias,
}

/// Scalar タグ種別（YAML ライブラリ非依存）
public enum ScalarTag
{
    Unknown,
    Str,        // !!str
    Bool,       // !!bool
    Int,        // !!int
    Float,      // !!float
    Null,       // !!null
}

/// ソース上の位置
public readonly record struct TextPosition(
    int Offset,
    int Line,
    int Column);
```

#### 3.1A.4 VYamlStreamAdapter（VYaml 実装）

現行のデフォルト実装。VYaml の `YamlParser` を内部に持ち、`IYamlStreamReader` に変換する。

```csharp
internal sealed ref struct VYamlStreamAdapter : IYamlStreamReader
{
    private YamlParser _parser;  // VYaml 固有型

    // --- IYamlStreamReader 実装 ---
    public YamlEventKind CurrentKind => MapEventKind(_parser.CurrentEventType);
    public bool End => _parser.End;
    public bool Read() => _parser.Read();
    public void SkipCurrentNode() => _parser.SkipCurrentNode();
    public ReadOnlySpan<byte> GetScalarUtf8() => _parser.GetScalarAsUtf8();
    // ... 他メンバーも VYaml API を自前 enum/struct に変換

    // VYaml の ParseEventType → YamlEventKind 変換
    private static YamlEventKind MapEventKind(ParseEventType vt) => vt switch
    {
        ParseEventType.MappingStart  => YamlEventKind.MappingStart,
        ParseEventType.MappingEnd    => YamlEventKind.MappingEnd,
        ParseEventType.SequenceStart => YamlEventKind.SequenceStart,
        ParseEventType.SequenceEnd   => YamlEventKind.SequenceEnd,
        ParseEventType.Scalar        => YamlEventKind.Scalar,
        // ...
    };
}
```

**重要**: VYaml 固有の型（`ParseEventType`, `Marker`, `YamlParser` 等）はこのファイル内にのみ出現する。パーサー本体やテストからは一切参照しない。

#### 3.1A.5 アダプター層を入れる理由

| 問題 | adapter で解決 |
|---|---|
| VYaml の event API 変更がパーサー全体へ波及する | 変更は `VYamlStreamAdapter` 内に閉じる |
| テストが VYaml の詳細仕様に引きずられる | `FakeYamlStreamReader` で最小イベント列を直接流し込める |
| パーサー本体の責務と YAML ライブラリ吸収責務が混ざる | 責務が明確に分離される |
| YamlDotNetなど他シリアライザへ差し替えたい | 新しい adapter を実装するだけでパーサーは不変 |
| Scalar タグ (`!!str`, `!!int` 等) の取得方法がライブラリごとに異なる | `ScalarTag` enum に正規化して吸収 |

#### 3.1A.6 差し替え時の手順

1. `IYamlStreamReader` を実装する新しい adapter クラスを作る（例: `YamlDotNetStreamAdapter`）
2. エントリポイント（`WorkflowParser.Parse()`）で adapter のファクトリを差し替える
3. パーサー本体の parse 関数群は **一切変更不要**
4. 既存テストもそのまま通る（`IYamlStreamReader` 契約が同一のため）

#### 3.1A.7 現行 VYamlStreamReader との関係

現行の `VYamlStreamReader`（`ref struct`）は `IYamlStreamReader` の前身。今後：

1. `IYamlStreamReader` インターフェースを定義
2. `VYamlStreamReader` を `VYamlStreamAdapter` にリネームし、`IYamlStreamReader` を実装
3. `WorkflowParser` の全 parse 関数を `ref VYamlStreamReader` → `IYamlStreamReader` に変更
4. VYaml 固有型（`ParseEventType`, `Marker`）への参照を adapter 内に閉じる

**注意**: `ref struct` は interface を実装できないため、adapter は `class` または generic type parameter で渡す設計とする。パフォーマンス上の懸念がある場合は、`IYamlStreamReader` を generic constraint として渡す `WorkflowParser<TReader> where TReader : IYamlStreamReader` パターンを採用し、JIT による仮想呼び出し排除を狙う。

### 3.2 Workflow トップレベルパース

```
ParseWorkflow(utf8Yaml) → ParseResult
  1. reader.SkipHeader()
  2. expect MappingStart → workflow root
  3. mapping 走査:
     key ごとに switch:
       "name"         → ParseString → workflow.Name
       "run-name"     → ParseString → workflow.RunName
       "on"           → ParseEvents → workflow.On
       "permissions"  → ParsePermissions → workflow.Permissions
       "env"          → ParseEnv → workflow.Env
       "defaults"     → ParseDefaults → workflow.Defaults
       "concurrency"  → ParseConcurrency → workflow.Concurrency
       "jobs"         → ParseJobs → workflow.Jobs
       その他          → UnexpectedKey error + SkipValue
  4. 必須キー検証: "on" と "jobs" がなければ error
  5. return ParseResult(workflow, diagnostics, hasFatalError)
```

### 3.3 Mapping 走査パターン

actionlint の `parseMapping()` に相当する汎用ルーチンを用意する。

```
ParseMapping(sectionName, allowEmpty, caseSensitive):
  1. null scalar → allowEmpty ? ok : error "should not be empty"
  2. expect MappingStart
  3. seen keys の Dictionary で重複検出 (case-insensitive なら ToLower)
  4. key ごとに:
     a. ParseString で key を読む
     b. "<<" (YAML merge key) → error
     c. duplicate check → error if dup
     d. yield (id, keyNode, valueEvent) to caller
  5. allowEmpty でなく 0 件 → error
```

### 3.4 Events パース (`on:` セクション)

`on:` は 3 形態を取る:

1. **scalar**: `on: push` → 単一イベント
2. **sequence**: `on: [push, pull_request]` → 複数イベント
3. **mapping**: `on: { push: { branches: [main] } }` → 設定付き

```
ParseEvents(node):
  switch kind:
    Scalar  → parseEventWithNoConfig(scalar) → [Event]
    Sequence → for each item: parseEventWithNoConfig(scalar) → [Event]
    Mapping  → for each entry:
      switch eventName:
        "schedule"            → ParseScheduleEvent
        "workflow_dispatch"   → ParseWorkflowDispatchEvent
        "repository_dispatch" → ParseRepositoryDispatchEvent
        "workflow_call"       → ParseWorkflowCallEvent
        "image_version"       → ParseImageVersionEvent
        other                 → ParseWebhookEvent
```

#### 3.4.1 parseEventWithNoConfig

scalar がイベント名の場合：
- `"schedule"` → error（mapping 必須）
- `"repository_dispatch"` / `"workflow_dispatch"` / `"workflow_call"` → 空の typed event
- その他 → `WebhookEvent { Hook = name }`

#### 3.4.2 WebhookEvent パース

```
ParseWebhookEvent(name, configNode):
  mapping 走査:
    "types"            → parseStringOrStringSequence
    "branches"         → parseWebhookEventFilter
    "branches-ignore"  → parseWebhookEventFilter
    "tags"             → parseWebhookEventFilter
    "tags-ignore"      → parseWebhookEventFilter
    "paths"            → parseWebhookEventFilter
    "paths-ignore"     → parseWebhookEventFilter
    "workflows"        → parseStringOrStringSequence   (workflow_run のみ)
    other              → unexpectedKey
```

#### 3.4.3 排他フィルタ検証

以下を mapping 走査後に検証する:
- `branches` と `branches-ignore` は同時不可
- `tags` と `tags-ignore` は同時不可
- `paths` と `paths-ignore` は同時不可

#### 3.4.4 activity types 検証

`OnEventSpecs` で定義されたイベントごとの許可 activity type を検証する。現行 C# 実装の `EventSpec.IsTypeAllowed()` をそのまま使う。

### 3.5 Permissions パース

```
ParsePermissions(node):
  if Scalar → All = parseString
  if Mapping → for each entry:
    Scopes[id] = { Name = key, Value = parseString(val) }
```

### 3.6 Env パース

```
ParseEnv(node):
  if Scalar → Expression = parseExpression (全体が ${{ }} か検証)
  if Mapping → for each entry: Vars[id] = { Name = key, Value = parseString(val, allowEmpty) }
```

**注意**: `env:` の値が `${{ }}` でない plain string の場合はエラー（"expecting ${{ }} expression or mapping"）。

### 3.7 Defaults パース

```
ParseDefaults(node):
  mapping 走査:
    "run" → ParseDefaultsRun(val):
      "shell" → parseString
      "working-directory" → parseString
      other → unexpectedKey
    other → unexpectedKey
  run が nil → error "defaults should have run"
```

### 3.8 Concurrency パース

```
ParseConcurrency(node):
  if Scalar → group = parseString
  if Mapping:
    "group" → parseString
    "cancel-in-progress" → parseBool
    other → unexpectedKey
  group が nil → error
```

### 3.9 Jobs パース

```
ParseJobs(node):
  mapping 走査 (case-insensitive):
    for each entry: jobs[id] = ParseJob(keyNode, valNode)
```

### 3.10 Job パース

```
ParseJob(id, node):
  mapping 走査:
    "name"             → parseString
    "needs"            → scalar or sequence of strings
    "runs-on"          → ParseRunsOn
    "permissions"      → ParsePermissions
    "environment"      → ParseEnvironment
    "concurrency"      → ParseConcurrency
    "outputs"          → ParseOutputs
    "env"              → ParseEnv
    "defaults"         → ParseDefaults
    "if"               → parseString
    "steps"            → ParseSteps
    "timeout-minutes"  → parseTimeoutMinutes (Float, > 0)
    "strategy"         → ParseStrategy
    "continue-on-error" → parseBool
    "container"        → ParseContainer
    "services"         → ParseServices
    "uses"             → parseString → WorkflowCall.Uses
    "with"             → mapping → WorkflowCall.Inputs
    "secrets"          → "inherit" or mapping → WorkflowCall.Secrets
    other              → unexpectedKey

  後検証:
    if uses あり:
      stepsOnlyKey があれば error
        (runs-on, environment, outputs, env, defaults, steps,
         timeout-minutes, continue-on-error, container は reusable workflow では不可)
    else:
      steps なし → error "steps is missing"
      runs-on なし → error "runs-on is missing"
      callOnlyKey (with, secrets) あれば error
```

#### 3.10.1 reusable workflow 呼び出し許可キー

`uses` による呼び出し時に許可されるキー:
- `name`, `uses`, `with`, `secrets`, `needs`, `if`, `permissions`, `concurrency`, `strategy`

それ以外のキーは `stepsOnlyKey` エラー。

### 3.11 Steps パース

```
ParseSteps(node):
  expect SequenceStart, not empty
  for each item: ParseStep(item)
```

### 3.12 Step パース

```
ParseStep(node):
  mapping 全 entry を収集（2 パス設計）:
    Pass 1: kind 判定
      "uses" → uses の値が "docker://" prefix → isDocker, else → isAction
      "run"  → isRun
      共通: "id", "if", "name", "env", "continue-on-error", "timeout-minutes"
    Pass 2: kind に応じて ExecAction or ExecRun を構築
      isAction/isDocker → parseStepExecAction(entries, isDocker)
      isRun             → parseStepExecRun(entries)
      unknown           → error "step must have run or uses"
```

#### 3.12.1 ExecAction パース

```
parseStepExecAction(entries, isDocker):
  "uses" → parseString
  "with" → mapping:
    if isDocker:
      "entrypoint" → Entrypoint
      "args"       → Args
      other        → Inputs[id]
    else:
      all          → Inputs[id]
  共通キー以外 → unexpectedKey
```

#### 3.12.2 ExecRun パース

```
parseStepExecRun(entries):
  "run"               → parseString
  "shell"             → parseString
  "working-directory" → parseString
  共通キー以外 → unexpectedKey
```

### 3.13 RunsOn パース

```
ParseRunsOn(node):
  if expression(${{ }}) → Runner { LabelsExpr }
  if Scalar or Sequence → labels = parseStringOrStringSequence
  if Mapping:
    "labels" → expression or stringOrSeq
    "group"  → parseString
    other    → unexpectedKey
```

### 3.14 Environment パース

```
ParseEnvironment(node):
  if Scalar → Name = parseString
  if Mapping:
    "name"       → parseString (必須)
    "url"        → parseString
    "deployment" → parseBool
    other → unexpectedKey
  name が nil → error
```

### 3.15 Strategy / Matrix パース

```
ParseStrategy(node):
  "matrix"       → ParseMatrix
  "fail-fast"    → parseBool
  "max-parallel" → parseInt (> 0)
  other → unexpectedKey

ParseMatrix(node):
  if Scalar → expression
  if Mapping:
    "include" → parseMatrixCombinations
    "exclude" → parseMatrixCombinations
    other     → custom row:
      if Scalar → expression
      if Sequence → [parseRawYAMLValue(item)]
```

### 3.16 Container パース

```
ParseContainer(section, node):
  if Scalar → Image = parseString
  if Mapping:
    "image"       → parseString (必須)
    "credentials" → ParseCredentials
    "env"         → ParseEnv
    "ports"       → stringSequence
    "volumes"     → stringSequence
    "options"     → parseString
    other → unexpectedKey
  image nil → error
```

### 3.17 Services パース

```
ParseServices(node):
  if expression → Services { Expression }
  if Mapping:
    for each entry:
      services[id] = Service { name, ParseContainer("services", val) }
```

### 3.18 Credentials パース

```
ParseCredentials(node):
  if expression → Credentials { Expression }
  if Mapping:
    "username" → parseString (必須)
    "password" → parseString (必須)
    other → unexpectedKey
  both nil → error
```

---

## 4. Scalar 解析ヘルパー

### 4.1 parseString

```
parseString(node, allowEmpty):
  expect Scalar
  if !allowEmpty && value == "" → error
  return StringNode { Value, Quoted, Range }
```

### 4.2 parseBool

```
parseBool(node):
  if tag == !!str → parseExpression → BoolNode { Expression }
  if tag == !!bool → BoolNode { value == "true" }
  else → error
```

### 4.3 parseInt

```
parseInt(node):
  if tag == !!str → parseExpression → IntNode { Expression }
  if tag == !!int → parse int literal → IntNode { Value }
  else → error
```

### 4.4 parseFloat

```
parseFloat(node):
  if tag == !!str → parseExpression → FloatNode { Expression }
  if tag == !!int or !!float → parse float literal → FloatNode { Value }
  else → error
```

### 4.5 parseExpression

```
parseExpression(node, expecting):
  value が ${{ ... }} 形式か検証
  形式でなければ → error "expecting ${{ }} or {expecting}"
  return StringNode
```

### 4.6 mayParseExpression

```
mayParseExpression(node):
  tag が !!str で、値が ${{ ... }} なら StringNode を返す
  それ以外は null
```

### 4.7 parseStringOrStringSequence

```
parseStringOrStringSequence(section, node, allowEmpty, allowElemEmpty):
  if Scalar:
    if null tag && allowEmpty → []
    else → [parseString]
  if Sequence:
    for each item: parseString
```

### 4.8 Scalar タグ情報について

actionlint (Go) の `yaml.Node.Tag`（`!!str`, `!!bool`, `!!int`, `!!float`, `!!null`）に相当する情報は、アダプター層の `IYamlStreamReader.GetScalarTag()` が `ScalarTag` enum として返す。

- VYaml adapter: VYaml 内部のタグ情報から変換
- YamlDotNet adapter: `NodeEvent.Tag` から変換
- タグが取得できないライブラリの場合: 値の内容（`"true"` / `"false"` / 数値パターン）でフォールバック推定する

パーサー本体は `ScalarTag` enum のみを参照し、YAML ライブラリ固有のタグ表現を知らない。

---

## 5. エラー回復戦略

### 5.1 基本方針

1. 1 エラーで解析を**停止しない**
2. mapping / sequence の境界を超えたエラーは subtree skip で回復
3. 可能な限り多くの diagnostic を返す

### 5.2 パターン別 recovery

| 状況 | recovery |
|---|---|
| 未知キー | error + value を SkipCurrentNode |
| 値の型不一致 | error + SkipCurrentNode |
| 必須キー不足 | mapping 走査後にまとめて error |
| 排他制約違反 | mapping 走査後にまとめて error |
| YAML パース失敗 | VYaml exception → `Diagnostic[]` に変換、`Workflow = null` |
| 重複キー | error + 後のキーを無視（先勝ち） |

---

## 6. 式パーサー仕様

### 6.1 概要

GitHub Actions `${{ }}` 式の再帰下降パーサー。現行 C# `ExpressionParser` はほぼ完成している。

### 6.2 文法（EBNF）

```
Expression    := LogicalOr
LogicalOr     := LogicalAnd ( "||" LogicalAnd )*
LogicalAnd    := Equality ( "&&" Equality )*
Equality      := Comparison ( ( "==" | "!=" ) Comparison )*
Comparison    := Primary ( ( "<" | "<=" | ">" | ">=" ) Primary )*
Primary       := UnaryExpr
UnaryExpr     := "!" UnaryExpr | Postfix
Postfix       := Atom ( "." Ident | "." "*" | "[" Index "]" | "(" ArgList ")" )*
Atom          := Ident | StringLit | IntLit | FloatLit | "true" | "false" | "null" | "(" Expression ")"
ArgList       := Expression ( "," Expression )*
Index         := Expression
```

**注意**: actionlint は算術演算（`+`, `-`, `*`, `/`, `%`）を**パースしない**。GitHub Actions の式仕様にはこれら演算子は存在しない。現行 C# 実装の `ParseAdditive` / `ParseMultiplicative` は GitHub Actions 仕様を超えているため、将来的に削除を検討する。

### 6.3 トークン種別

| トークン | 記号 |
|---|---|
| `Ident` | 英数字 + `_` + `-` |
| `String` | `'...'` (single-quoted, `''` でエスケープ) |
| `Int` | 整数リテラル（10 進 / `0x` 16 進） |
| `Float` | 浮動小数点リテラル |
| `(` `)` `[` `]` `.` `!` | 記号 |
| `<` `<=` `>` `>=` `==` `!=` | 比較演算子 |
| `&&` `||` | 論理演算子 |
| `*` | ワイルドカード（`foo.*` の `*`） |
| `,` | 関数引数区切り |

### 6.4 式 AST ノード

| ノード種別 | 説明 |
|---|---|
| `VariableNode` | `github`, `env`, `secrets` 等のコンテキスト変数 |
| `ObjectDerefNode` | `foo.bar` — プロパティアクセス |
| `ArrayDerefNode` | `foo.*` — ワイルドカードアクセス |
| `IndexAccessNode` | `foo['bar']` or `foo[0]` — インデックスアクセス |
| `NotOpNode` | `!expr` |
| `CompareOpNode` | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| `LogicalOpNode` | `&&`, `||` |
| `FuncCallNode` | `contains(...)`, `startsWith(...)` 等 |
| `NullNode` | `null` リテラル |
| `BoolNode` | `true` / `false` リテラル |
| `IntNode` | 整数リテラル |
| `FloatNode` | 浮動小数点リテラル |
| `StringNode` | 文字列リテラル |

### 6.5 式 Visitor

式 AST は `VisitExprNode(node, parent, entering)` パターンで巡回する。`entering = true` で子要素訪問前、`entering = false` で子要素訪問後にコールバック。

```csharp
public delegate void ExprNodeVisitor(ExpressionNode node, int parentId, bool entering);

public static void VisitExprNode(
    int nodeId,
    ExpressionNode[] nodes,
    int[] arguments,
    ExprNodeVisitor visitor);
```

---

## 7. 式 意味検証 (Semantic Analysis)

### 7.1 組み込み関数シグネチャ

| 関数名 | パラメータ | 戻り値 | 可変長 |
|---|---|---|---|
| `contains` | (string, string) or (array, any) | bool | No |
| `startsWith` | (string, string) | bool | No |
| `endsWith` | (string, string) | bool | No |
| `format` | (string, any...) | string | Yes |
| `join` | (array\|string, string?) | string | No |
| `toJSON` | (any) | string | No |
| `fromJSON` | (string) | any | No |
| `hashFiles` | (string...) | string | Yes |
| `success` | () | bool | No |
| `always` | () | bool | No |
| `failure` | () | bool | No |
| `cancelled` | () | bool | No |

### 7.2 コンテキスト可用性検証

式の root identifier（`github`, `env`, `steps`, `job`, `runner`, `secrets`, `strategy`, `matrix`, `needs`, `inputs`, `vars`）は使用場所（workflow, job, step）によって使えるかが異なる。

| コンテキスト | workflow level | job level | step level |
|---|---|---|---|
| `github` | ✓ | ✓ | ✓ |
| `env` | ✓ | ✓ | ✓ |
| `vars` | ✓ | ✓ | ✓ |
| `job` | - | ✓ | ✓ |
| `steps` | - | - | ✓ |
| `runner` | - | ✓ | ✓ |
| `secrets` | - | ✓ | ✓ |
| `strategy` | - | ✓ | ✓ |
| `matrix` | - | ✓ | ✓ |
| `needs` | - | ✓ | ✓ |
| `inputs` | ✓ | ✓ | ✓ |
| `hashFiles` | - | ✓ | ✓ |
| `success`/`failure`/`always`/`cancelled` | - | ✓ | ✓ |

**注意**: これは simplified table。厳密にはキーの位置（`if:` / `env:` / `with:` 等）ごとに異なる。完全な availability table は generated data（`Availability.g.cs`）として管理する。

### 7.3 型検証

将来実装として、actionlint の `ExprType` 階層に相当する型システムを導入する：
- `AnyType` / `NullType` / `BoolType` / `NumberType` / `StringType`
- `ObjectType` (properties map) / `ArrayType` (element type)
- `EmptyObjectType` / `EmptyArrayType`

型推論は `ExprSemanticsChecker` で式を走査しながら bottom-up で実行する。

---

## 8. Visitor / Pass 仕様

### 8.1 Pass インターフェース

```csharp
public interface IPass
{
    void VisitWorkflowPre(Workflow workflow);
    void VisitWorkflowPost(Workflow workflow);
    void VisitJobPre(Job job);
    void VisitJobPost(Job job);
    void VisitStep(Step step);
}
```

### 8.2 Visitor

```csharp
public sealed class WorkflowVisitor
{
    private readonly List<IPass> _passes = new();

    public void AddPass(IPass pass) => _passes.Add(pass);

    public void Visit(Workflow workflow)
    {
        foreach (var pass in _passes)
            pass.VisitWorkflowPre(workflow);

        foreach (var (_, job) in workflow.Jobs)
        {
            foreach (var pass in _passes)
                pass.VisitJobPre(job);

            if (job.Steps is not null)
            {
                foreach (var step in job.Steps)
                {
                    foreach (var pass in _passes)
                        pass.VisitStep(step);
                }
            }

            foreach (var pass in _passes)
                pass.VisitJobPost(job);
        }

        foreach (var pass in _passes)
            pass.VisitWorkflowPost(workflow);
    }
}
```

### 8.3 巡回順序

```
VisitWorkflowPre(workflow)
  for each job in workflow.Jobs:
    VisitJobPre(job)
    for each step in job.Steps:
      VisitStep(step)
    VisitJobPost(job)
VisitWorkflowPost(workflow)
```

この深さ優先順は actionlint と完全に同一。

### 8.4 Rule インターフェース

```csharp
public interface IRule : IPass
{
    string Id { get; }
    string Name { get; }
    Diagnostic[] GetDiagnostics();
    void SetConfig(LintConfig config);
}
```

各 Rule は `IPass` のメソッド内で AST を検査し、内部に `List<Diagnostic>` を蓄積する。

---

## 9. Generated Data 仕様

### 9.1 対象

| データ | 生成元 | ファイル名 |
|---|---|---|
| Webhook event + activity types | GitHub Docs | `WebhookTypes.g.cs` |
| Context availability table | GitHub Docs | `Availability.g.cs` |
| Special function names | GitHub Docs | `Availability.g.cs` 内 |
| Popular actions metadata | action.yml 取得 | `PopularActions.g.cs` |

### 9.2 更新方針

- 更新コマンド (`Seiton.Update` or script) で外部データを取得
- 生成結果を `.g.cs` として commit
- CI の定期実行で差分を検知し自動 PR
- パーサー・ルール実行時はネットワーク参照しない

### 9.3 現行 `OnEventSpecs` との関係

`OnEventSpecs` は手実装のイベント名 + activity types テーブル。将来は `WebhookTypes.g.cs` に置換可能だが、初期時点では手実装で十分。

---

## 10. Diagnostic 仕様

### 10.1 Diagnostic 構造

```csharp
public readonly record struct Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextRange Location,
    string? RuleId = null,
    TextRange[]? RelatedLocations = null,
    string? Help = null);
```

### 10.2 TextRange

```csharp
public readonly record struct TextRange(
    int Start,
    int Length,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
```

### 10.3 位置の方針

| 状況 | primary location |
|---|---|
| 未知キー | キーの位置 |
| 型不一致 | 値の位置 |
| 必須キー不足 | セクション開始位置 |
| 排他制約違反 | 主因のキー位置 |
| 重複キー | 2 番目のキー位置 |
| 式エラー | 式内のオフセットを元ソースに変換した位置 |

---

## 11. 許可キー一覧

以下は各 mapping セクションで許可されるキーの完全一覧。未知キーはすべて diagnostic error とする。

### 11.1 Workflow 直下

```
name, run-name, on, permissions, env, defaults, concurrency, jobs
```

### 11.2 Job

```
name, needs, runs-on, permissions, environment, concurrency, outputs,
env, defaults, if, steps, timeout-minutes, strategy, continue-on-error,
container, services, uses, with, secrets, snapshot
```

### 11.3 Step

```
id, if, name, uses, run, with, env, shell, working-directory,
continue-on-error, timeout-minutes
```

### 11.4 Strategy

```
matrix, fail-fast, max-parallel
```

### 11.5 Defaults

```
run
```

### 11.6 defaults.run

```
shell, working-directory
```

### 11.7 Concurrency

```
group, cancel-in-progress
```

### 11.8 Container

```
image, credentials, env, ports, volumes, options
```

### 11.9 Credentials

```
username, password
```

### 11.10 Environment

```
name, url, deployment
```

### 11.11 runs-on (mapping 形式)

```
labels, group
```

### 11.12 workflow_dispatch

```
inputs
```

### 11.13 workflow_dispatch input

```
description, required, default, type, options
```

### 11.14 workflow_call

```
inputs, secrets, outputs
```

### 11.15 workflow_call input

```
description, required, default, type
```

### 11.16 workflow_call secret

```
description, required
```

### 11.17 workflow_call output

```
description, value
```

### 11.18 repository_dispatch

```
types
```

### 11.19 schedule entry

```
cron, timezone
```

### 11.20 Webhook event options

各イベントで許可されるオプションは `OnEventSpecs.EventSpec.IsOptionAllowed()` で定義済み。共通の候補:
```
types, branches, branches-ignore, tags, tags-ignore,
paths, paths-ignore, workflows, inputs, secrets, outputs
```

---

## 12. 相互制約・条件付き必須の一覧

| セクション | 制約 |
|---|---|
| Workflow | `on` 必須、`jobs` 必須 |
| Job (normal) | `steps` 必須、`runs-on` 必須 |
| Job (reusable) | `uses` 必須、`steps` / `runs-on` / `environment` / `outputs` / `env` / `defaults` / `timeout-minutes` / `continue-on-error` / `container` 不可 |
| Job | `uses` と `steps` は排他 |
| Job | `with` / `secrets` は `uses` 時のみ |
| Step | `run` と `uses` は排他、どちらか必須 |
| Webhook event | `branches` と `branches-ignore` は排他 |
| Webhook event | `tags` と `tags-ignore` は排他 |
| Webhook event | `paths` と `paths-ignore` は排他 |
| Concurrency | `group` は必須 |
| Container | `image` は必須 |
| Credentials | `username` と `password` は両方必須 |
| Environment | `name` は必須 |
| workflow_call input | `type` は必須 |
| workflow_call output | `value` は必須 |
| Defaults | `run` は必須 |
| max-parallel | > 0 |
| timeout-minutes | > 0 |
| schedule | `cron` が必須 |

---

## 13. Case Sensitivity ルール

| セクション | キー比較 | 備考 |
|---|---|---|
| workflow 直下キー | case-sensitive | |
| job id | case-insensitive | duplicate 検出 |
| job 内キー | case-sensitive | |
| step 内キー | case-sensitive | |
| matrix row 名 | case-insensitive | |
| env 変数名 | case-insensitive | |
| permission scope | case-insensitive | |
| workflow_dispatch input 名 | case-insensitive | |
| with input 名 | case-insensitive | |
| event 名 | case-sensitive | |

---

## 14. YAML 多態フィールド処理

| フィールド | 取りうる形 | パース方針 |
|---|---|---|
| `on:` | scalar / sequence / mapping | `ParseEvents` で 3 分岐 |
| `runs-on:` | scalar / sequence / mapping / expression | `ParseRunsOn` で 4 分岐 |
| `permissions:` | scalar / mapping | `ParsePermissions` で 2 分岐 |
| `env:` | expression / mapping | `ParseEnv` で 2 分岐 |
| `container:` | scalar (image name) / mapping | `ParseContainer` で 2 分岐 |
| `services:` | expression / mapping | `ParseServices` で 2 分岐 |
| `credentials:` | expression / mapping | `ParseCredentials` で 2 分岐 |
| `concurrency:` | scalar (group name) / mapping | `ParseConcurrency` で 2 分岐 |
| `environment:` | scalar (name) / mapping | `ParseEnvironment` で 2 分岐 |
| `needs:` | scalar / sequence | `parseStringOrStringSequence` |
| `secrets:` (job level) | `"inherit"` / mapping | `ParseJobSecrets` で 2 分岐 |
| `matrix:` | expression / mapping | `ParseMatrix` で 2 分岐 |
| `matrix.include` / `matrix.exclude` | expression / sequence | 要素はさらに expression / mapping |
| `matrix.<row>` | expression / sequence | |
| Bool / Int / Float | expression / literal | `parseBool` / `parseInt` / `parseFloat` |

---

## 15. 実装優先度

### Phase 1: AST 構築（パーサー本体）

1. AST 型定義（Section 2）
2. パーサー書き換え: `WorkflowParser.Parse()` が `Workflow` AST を返すようにする
3. 既存の shape 検証ロジック（未知キー、排他、必須）は維持
4. テスト更新

### Phase 2: Visitor / Pass

1. `IPass` / `WorkflowVisitor` 実装
2. 既存 diagnostics を syntax rule として移行

### Phase 3: Generated Data

1. `Availability.g.cs` — context availability table
2. `WebhookTypes.g.cs` — webhook event + activity types
3. 更新スクリプト

### Phase 4: 式型システム

1. `ExprType` 階層
2. `ExprSemanticsChecker` の型推論
3. context availability 連携

---

## 付録 A: actionlint parse.go → C# 関数対応表

| actionlint 関数 | C# 対応関数 | 状態 |
|---|---|---|
| `Parse()` | `WorkflowParser.Parse()` | 部分実装 |
| `parser.parse()` | Parse 内 workflow mapping 走査 | 部分実装 |
| `parser.parseEvents()` | `ParseOn()` | 部分実装（typed node 未生成） |
| `parser.parseScheduleEvent()` | — | **未実装** |
| `parser.parseWorkflowDispatchEvent()` | — | **未実装** |
| `parser.parseWorkflowCallEvent()` | — | **未実装** |
| `parser.parseRepositoryDispatchEvent()` | — | **未実装** |
| `parser.parseWebhookEvent()` | `ParseOnEventOptions()` | 部分実装 |
| `parser.parsePermissions()` | — (skip) | **未実装** |
| `parser.parseEnv()` | `ParseStringMapping()` | 部分実装 |
| `parser.parseDefaults()` | — (skip) | **未実装** |
| `parser.parseConcurrency()` | — (skip) | **未実装** |
| `parser.parseJob()` | `ParseJobNode()` | 部分実装（フラグのみ） |
| `parser.parseStep()` | `ParseStep()` | 部分実装（フラグのみ） |
| `parser.parseRunsOn()` | — (shape check) | **未実装** |
| `parser.parseEnvironment()` | — | **未実装** |
| `parser.parseOutputs()` | — (skip) | **未実装** |
| `parser.parseStrategy()` | `ParseStrategy()` | shape のみ |
| `parser.parseMatrix()` | `ParseMatrix()` | shape のみ |
| `parser.parseContainer()` | `ParseContainerLike()` | shape のみ |
| `parser.parseServices()` | `ParseServices()` | shape のみ |
| `parser.parseCredentials()` | `ParseCredentials()` | shape のみ |
| `parser.parseStepExecAction()` | — | **未実装** |
| `parser.parseStepExecRun()` | — | **未実装** |
| `parser.parseMapping()` | — (inline) | 対応する汎用関数なし |
| `parser.parseString()` | `ReadScalarOrSkip()` | 部分対応 |
| `parser.parseBool()` | — | **未実装** |
| `parser.parseInt()` | — | **未実装** |
| `parser.parseFloat()` | — | **未実装** |
| `parser.mayParseExpression()` | — | **未実装** |
| `parser.resolveAliases()` | — | **未実装** |
| `Visitor.Visit()` | — | **未実装** |
| `Pass` interface | — | **未実装** |

## 付録 B: 式パーサー対応表

| actionlint | C# `ExpressionParser` | 状態 |
|---|---|---|
| `ExprLexer` | `Parser` 内の inline lexing | ✓ 対応済み |
| `ExprParser.parseLogicalOr()` | `ParseOr()` | ✓ |
| `ExprParser.parseLogicalAnd()` | `ParseAnd()` | ✓ |
| `ExprParser.parseCompare()` | `ParseEquality()` + `ParseRelational()` | ✓ |
| `ExprParser.parsePrimaryExpr()` | `ParsePrimary()` | ✓ |
| `ExprParser.parseIdent()` | `ParseKeywordOrIdentifier()` | ✓ |
| `ExprParser.parsePostfixOp()` | `ParsePrimary()` 内ループ | ✓ |
| `VariableNode` | `Identifier` | ✓ |
| `ObjectDerefNode` | `MemberAccess` | ✓ |
| `ArrayDerefNode` | `WildcardAccess` | ✓ |
| `IndexAccessNode` | `IndexAccess` | ✓ |
| `FuncCallNode` | `FunctionCall` | ✓ |
| `NotOpNode` | `Unary (Not)` | ✓ |
| `CompareOpNode` | `Binary (Equal/NotEqual/Less/...)` | ✓ |
| `LogicalOpNode` | `Binary (And/Or)` | ✓ |
| arithmetic ops | `Binary (Add/Sub/Mul/Div/Mod)` | C# 独自拡張 (GHA 仕様外) |
| `ExprSemanticsChecker` | `ExpressionSemanticAnalyzer` | 部分実装 |
| `BuiltinFuncSignatures` | `TryGetFunctionArity()` | arity のみ (型なし) |
| `VisitExprNode()` | — | **未実装** |
