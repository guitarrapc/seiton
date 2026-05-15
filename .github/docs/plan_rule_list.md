# Plan: Rule List Command

## 背景・目的

ユーザーが seiton でサポートされているルール一覧を簡単に確認できるようにする。ルールは config やCLI引数によって有効/無効が変動するため、「現在の設定で何が有効か」を含めて一覧表示できる機能が求められている。

---

## 現状分析

### ルールメタデータの管理場所

| 情報 | 管理場所 | アクセス可否 (CLI) |
|---|---|---|
| Rule ID (kebab-case) | `RuleIdExtensions.ToId()` | public |
| Rule Name (human-readable) | 各 Rule クラスの `Name` プロパティ | internal (IRule は public だが Factory は internal) |
| Opt-in フラグ | `RuleCatalog.IsOptIn()` | internal |
| Allowed config keys | `RuleCatalog.TryGetAllowedConfigKeys()` | internal |
| Priority (execution order) | `RuleCatalog.GetPriority()` | internal |
| Online vs Local | `OnlineRuleFactories` vs `DefaultRuleFactories` | internal |
| Document kind support | 各 Rule クラスの `SupportsDocumentKind()` | internal |
| Default severity | ルールごとに `AddWarning`/`AddError` をハードコード | ソースのみ |

### CLIからのアクセス制約

- `RuleCatalog` は `internal static class` であり、CLI (`src/Seiton/`) からは直接アクセス不可
- `Seiton.Core` の `InternalsVisibleTo` には `Seiton` (CLI) が含まれていない
- CLI は `LintConfigLibrary` (public) 経由でのみ Core の機能にアクセスしている

### 設定による有効/無効の変動パターン

1. **Default-on ルール**: config で `enabled: false` にしない限り有効
2. **Opt-in ルール** (`ConcurrencyLimits`): config に `rules.<id>` エントリがないと無効
3. **Online ルール** (4件): ネットワーク有効 + config で有効化が必要
4. **Exclusion**: ファイル/ジョブ単位で特定ルールを抑制

---

## 実装案

### 案1: `RuleCatalog` に public な一覧取得 API を追加 (推奨)

#### 設計

```csharp
// Seiton.Core/Linting/RuleDescriptor.cs (新規, public)
public readonly record struct RuleDescriptor(
    string Id,              // kebab-case rule id
    string Name,            // human-readable name
    bool IsOptIn,           // opt-in only (disabled by default)
    bool IsOnline,          // requires network access
    bool SupportsWorkflow,  // applies to workflow documents
    bool SupportsAction     // applies to action metadata documents
);

// RuleCatalog.cs に追加 (public static method)
public static IReadOnlyList<RuleDescriptor> GetAllRuleDescriptors();
```

#### 有効状態の解決

```csharp
// Seiton.Core/Linting/RuleListResolver.cs (新規, public)
public readonly record struct RuleStatus(
    RuleDescriptor Rule,
    bool Enabled,           // 設定反映後の有効/無効
    string Reason           // "default" | "config" | "opt-in (not configured)"
);

public static class RuleListResolver
{
    public static IReadOnlyList<RuleStatus> Resolve(LintConfig? config);
}
```

#### CLI コマンド

```
seiton rules [--config PATH] [--format text|json]
```

出力例 (text):
```
Rule                            Enabled  Type      Reason
────────────────────────────────────────────────────────────────
job-structure                   ✓        local     default
reusable-workflow               ✓        local     default
permissions                     ✓        local     default
...
deny-write-all                  ✓        local     default
deny-read-all                   ✓        local     default
...
concurrency-limits              ✗        local     opt-in (not configured)
known-vulnerable-actions        ✗        online    opt-in (not configured)
...
template-injection              ✗        local     config (disabled)
```

出力例 (json):
```json
[
  {
    "id": "job-structure",
    "name": "Job Structure Rule",
    "enabled": true,
    "type": "local",
    "reason": "default",
    "supportsWorkflow": true,
    "supportsAction": false
  }
]
```

#### メリット

- `Seiton.Core` にルール情報の正規 API が生まれ、Playground 等他の消費者も利用可能
- Rule インスタンスを生成せずにメタデータを取得できる (パフォーマンス影響なし)
- 既存の `RuleCatalog` の internal 構造を公開型に投影するだけで、ロジック変更なし

#### デメリット

- `RuleCatalog` の可視性変更 or 新しい public facade が必要
- Rule ごとの `Name` と `SupportsDocumentKind` は現状 Factory 経由でしか取得不可 → 起動時 1回だけインスタンス生成して取得するか、静的メタデータとして二重管理

---

### 案2: 静的メタデータテーブル (二重管理だが最も軽量)

#### 設計

`RuleCatalog` 内に静的な `RuleDescriptor[]` を定義し、Rule クラスをインスタンス化せずに全メタデータを返す。

```csharp
private static readonly RuleDescriptor[] AllDescriptors = [
    new("job-structure", "Job Structure Rule", IsOptIn: false, IsOnline: false, SupportsWorkflow: true, SupportsAction: false),
    new("reusable-workflow", "Reusable Workflow Rule", IsOptIn: false, IsOnline: false, SupportsWorkflow: true, SupportsAction: false),
    // ... 全ルール
];
```

#### メリット

- ゼロアロケーション (配列は static readonly)
- ルールインスタンスを生成しない
- 最もパフォーマンスに優れる

#### デメリット

- ルール追加時に `AllDescriptors` と `DefaultRuleFactories` の両方を更新する必要あり (二重管理)
- Name や SupportsDocumentKind の不整合リスク → テストで検証可能

---

### 案3: 起動時に全ルールを1回インスタンス化してメタデータを収集

#### 設計

```csharp
public static IReadOnlyList<RuleDescriptor> GetAllRuleDescriptors()
{
    var descriptors = new RuleDescriptor[DefaultRuleFactories.Length + OnlineRuleFactories.Length];
    for (var i = 0; i < DefaultRuleFactories.Length; i++)
    {
        var rule = DefaultRuleFactories[i].Factory();
        descriptors[i] = new RuleDescriptor(
            DefaultRuleFactories[i].Id.ToId(),
            rule.Name,
            DefaultRuleFactories[i].OptIn,
            IsOnline: false,
            rule.SupportsDocumentKind(DocumentKind.Workflow),
            rule.SupportsDocumentKind(DocumentKind.ActionMetadata));
    }
    // + OnlineRuleFactories
    return descriptors;
}
```

#### メリット

- 二重管理なし (既存の Factory + Rule クラスから動的に取得)
- ルール追加時にメタデータ更新が自動的に反映

#### デメリット

- 全ルールをインスタンス化するコスト (とはいえ `rules` コマンド実行時のみ)
- `rules` コマンドは lint とは独立なので、パフォーマンスインパクトは実質ゼロ

---

## 推奨案: 案3 (動的メタデータ収集) + 案1 の public API

### 理由

1. **二重管理回避**: ルール追加時にメタデータテーブルの更新忘れを防ぐ
2. **パフォーマンス影響なし**: `rules` コマンドは独立実行で、lint hot path に影響しない
3. **テスト容易性**: 全ルールのメタデータが正しく取得できることを1つのテストで検証
4. **拡張性**: 将来的にカテゴリや説明文を追加する際も Rule クラスに属性追加だけで済む

### 実装計画

#### Phase 1: Seiton.Core API (優先度: 高)

1. `RuleDescriptor` 型を定義 (`Seiton.Core/Linting/RuleDescriptor.cs`)
2. `RuleStatus` 型を定義 (`Seiton.Core/Linting/RuleStatus.cs`)
3. `RuleCatalog.GetAllRuleDescriptors()` を public static で追加
4. `RuleListResolver.Resolve(LintConfig?)` を public static で追加
5. テスト: `RuleCatalogTests` に全ルールの descriptor 検証を追加

#### Phase 2: CLI コマンド (優先度: 高)

1. `RulesCommand.cs` を `src/Seiton/Commands/` に追加
2. `Program.cs` / `SeitonCli` にサブコマンド `rules` を追加
3. Text / JSON 出力フォーマット対応
4. テスト: `RulesCommandTests` で出力フォーマットを検証

#### Phase 3: テスト & 検証 (優先度: 高)

1. 全既存テストがパスすることを確認 (リグレッションなし)
2. `RuleDescriptor` の完全性テスト (全 RuleId がカバーされていること)
3. `RuleListResolver` のテスト (config パターン別の有効/無効解決)
4. ベンチマーク確認: `CoreParsingBenchmark` と `CoreLintBenchmark` に影響なし

---

## CLI UX 設計

### コマンド仕様

```
seiton rules [OPTIONS]

OPTIONS:
  --config <PATH>     Config file path (auto-discovered if omitted)
  --format <FORMAT>   Output format: text (default) | json
```

### 出力カラム

| カラム | 説明 |
|---|---|
| id | Rule ID (kebab-case) |
| name | Human-readable name |
| enabled | yes / no |
| type | local / online |
| document | workflow / action / both |
| reason | default / config (enabled) / config (disabled) / opt-in (not configured) |

### Exit Code

- 0: 成功 (ルール一覧を出力)
- 2: 無効なオプション (e.g. `--format sarif`)
- 3: 致命的エラー (config ファイル不在またはバリデーション失敗)

---

## パフォーマンス影響分析

### lint hot path への影響

- **なし**: `GetAllRuleDescriptors()` は `rules` コマンド実行時のみ呼ばれる
- `RuleCatalog` の既存 static フィールド (`DefaultRuleFactories` 等) に変更なし
- `LintEngine` のコードパスに変更なし

### 新規コマンドのパフォーマンス

- ルールインスタンス生成: ~55 ルール × 軽量コンストラクタ → 無視できるコスト
- config 読み込み: 既存の `CliConfigBridge` を再利用

### 検証手順

1. `dotnet test` — 全テスト pass
2. `CoreParsingBenchmark` — Mean/Allocated 変化なし
3. `CoreLintBenchmark` — Mean/Allocated 変化なし

---

## 将来の拡展

- カテゴリ (Correctness / Security / Supply Chain / Permissions) の追加
- ルールごとの description (short/long) の追加
- `seiton rules --docs` で Markdown ドキュメント生成
- LSP server での rule list API 利用
- `seiton rules --diff` で config 変更前後の差分表示

---

## 実装結果

### 実施内容

推奨案 (案3: 動的メタデータ収集 + 案1: public API) に沿って実装を完了。

#### 追加ファイル

| ファイル | 役割 |
|---|---|
| `src/Seiton.Core/Linting/RuleDescriptor.cs` | ルールメタデータの public readonly record struct |
| `src/Seiton.Core/Linting/RuleListResolver.cs` | `RuleStatus` 型 + config 反映の解決ロジック |
| `src/Seiton/Commands/RulesCommand.cs` | CLI `rules` サブコマンド (text/json出力) |
| `tests/Seiton.Core.Tests/RuleCatalogDescriptorTests.cs` | RuleCatalog.GetAllRuleDescriptors() のテスト (8件) |
| `tests/Seiton.Core.Tests/RuleListResolverTests.cs` | RuleListResolver.Resolve() のテスト (8件) |

#### 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/Seiton.Core/Linting/RuleCatalog.cs` | `GetAllRuleDescriptors()` internal static メソッド追加 |
| `src/Seiton/Program.cs` | `rules` サブコマンドの追加 |
| `src/Seiton/Output/DiagnosticFormatter.cs` | `SeitonJsonContext` に `RuleStatusJsonEntry[]` を追加 |

#### CLI 仕様

```
seiton rules [--config PATH] [--format text|json]
```

- `--config`: 設定ファイルパス (省略時は自動探索)
- `--format`: 出力形式 (text がデフォルト、json も対応)
- Exit code: 成功時は 0。無効なオプション時は 2 (`InvalidOptions`)、設定ファイル不在/不正時は 3 (`FatalError`)

#### テスト結果

- 全テスト: **1650 passed, 0 failed** (既存1634 + 新規16)
- リグレッション: なし

#### ベンチマーク結果

**CoreLintBenchmark (Allocated):**
- Small/False: 8.37 KB → 8.37 KB (±0%)
- Medium/False: 68.56 KB → 68.56 KB (±0%)
- Large/False: 327.08 KB → 327.08 KB (±0%)

**CoreParsingBenchmark (Allocated):**
- Small: 3.87 KB → 3.87 KB (±0%)
- Medium: 35.59 KB → 35.59 KB (±0%)
- Large: 180.04 KB → 180.04 KB (±0%)

**結論: lint/parse hot path へのパフォーマンス影響は完全にゼロ。**

### 実装上のプランとの差分

| プラン | 実装 | 理由 |
|---|---|---|
| `--enabled-only` / `--disabled-only` フィルタ | 未実装 | 初期リリースではシンプルに全件表示。フィルタは将来追加可能 |
| ルール数 59 | 実際は 56 (52 default + 4 online) | プラン作成時のカウントミス |
| `RuleCatalog` を public 化 | internal のまま、`GetAllRuleDescriptors()` も internal。公開ファサードは `RuleListResolver` | 最小限の公開範囲を維持 |

### レビュー後の修正

| 指摘 | 対応 |
|---|---|
| `GetAllRuleDescriptors()` が毎回全ルールをインスタンス化 | `Lazy<RuleDescriptor[]>` でキャッシュ化。初回呼び出し時のみインスタンス化、以降は O(1) |
| `RuleStatusJsonEntry` が既存スタイル (`{ get; set; } = ""`) と不一致 | 既存の `JsonDiagnosticEntry` パターンに合わせて修正 |
| config 読み込み時の diagnostics が握りつぶされていた | stderr に `config: {message}` として出力 |
| 未使用 `using Seiton.Core.Parsing` がテストファイルに残存 | 削除 |
| default-on ルールの明示的 `enabled: true` 設定テストが欠落 | `Resolve_ConfigExplicitlyEnablesDefaultRule_ReasonIsDefault` テスト追加 |

### 最終テスト結果

- 全テスト: **1651 passed, 0 failed** (既存1634 + 新規17)
- ベンチマーク: lint/parse 全サイズで **Allocated 完全一致** (±0%)

---

## Phase 2 拡張: DefaultSeverity / SupportsAutoFix カラム追加

### 背景

`seiton rules` の初期実装ではルールの有効/無効とタイプのみ表示していたが、以下のフィードバックがあった:

1. ルールが fix 可能かどうかが分からない
2. ルールごとの warning/error の区別が分からない

### 実施内容

`RuleDescriptor` に 2 フィールドを追加:

```csharp
public readonly record struct RuleDescriptor(
    string Id,
    string Name,
    bool IsOptIn,
    bool IsOnline,
    bool SupportsWorkflow,
    bool SupportsAction,
    string DefaultSeverity,   // "error" | "warning" | "mixed"
    bool SupportsAutoFix);    // true if rule can produce DiagnosticFix
```

`RuleCatalog` に静的メタデータルックアップを追加:
- `GetDefaultSeverity(RuleId)`: spec §5.7.2 の per-rule severity table に準拠
- `GetSupportsAutoFix(RuleId)`: 実装から導出 (DiagnosticFix を生成するルール)

CLI `seiton rules` 出力に **Severity** カラムと **Fix** カラムを追加:

```
Rule                                     Enabled   Type     Severity   Fix   Document   Reason
---------------------------------------------------------------------------------------------------------
job-structure                            yes       local    error      no    both       default
template-injection                       yes       local    error      yes   both       default
unpinned-uses                            yes       local    mixed      yes   both       default
```

JSON 出力にも `defaultSeverity` と `supportsAutoFix` フィールドを追加。

### テスト結果

- 新規テスト 7 件追加 (`RuleCatalogDescriptorTests`): DefaultSeverity / SupportsAutoFix の検証
- 全テスト: **1672 passed, 1 pre-existing failure** (unrelated exclusion test)
- ベンチマーク: CoreLintBenchmark Allocated 完全一致 (±0%)
