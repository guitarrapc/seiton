# LintConfig Brushup Plan

## 1. 現状分析

### 1.1 関連ファイル

| ファイル | 責務 |
|---|---|
| `LintConfig.cs` | Config モデル定義 (LintConfig, RuleConfig, RuleSpecificConfig 群, FixConfig, NetworkConfig 等) |
| `LintConfigYamlParser.cs` | VYaml の `YamlSerializer.Deserialize<Dictionary<string,object?>>` で DOM 化 → 手動ツリーウォーク |
| `LintConfigRuleBodyMaterializer.cs` | パーサーが集めた個別フィールドから `RuleSpecificConfig` サブクラスを生成 |
| `RuleSpecificConfigNormalizer.cs` | rule-id ごとに RuleSpecificConfig の値を正規化 (trim, dedup, lowercase 等) |
| `RuleCatalog.cs` | `BuildAllowedRuleConfigKeys()` でルールごとの許可キー定義 |
| `RuleNormalizer.cs` | rule-id 解決、non-disableable/min-severity ポリシー、`RuleSpecificConfigNormalizer.Normalize` 呼び出し |
| `ExclusionNormalizer.cs` | exclusion の rule-id 解決 |
| `LintConfigLibrary.cs` | `Validate()` エントリポイント、`NormalizeRules/Exclusions/Fix/Network` |
| `LintEngine.cs` | `Check()` 内で config を受け取り、各ルールに `SetConfig()` で渡す |
| 各ルール (`DangerousTriggersRule`, `ForbiddenUsesRule`, ...) | `SetConfig()` で `RuleSpecificConfig` をパターンマッチして自フィールドに展開 |

### 1.2 データフロー (現状)

```
YAML bytes
  ↓ YamlSerializer.Deserialize<Dictionary<string, object?>>()
  ↓ (untyped DOM: Dictionary/List/boxed primitives)
  ↓
LintConfigYamlParser.Convert()
  ├─ rules セクション → AddRule() で switch(key) { "events", "known-hosted-labels", ... }
  │    ↓ 全ルール共通の ~10 個のローカル変数に収集
  │    ↓ LintConfigRuleBodyMaterializer.BuildSpecific(ruleId, ...)
  │    ↓ switch(ruleId) で RuleSpecificConfig サブクラスを生成
  │    ↓ RuleConfig { Enabled, Severity, Specific } として dictionary に追加
  ├─ exclusions セクション → AddExclusion()
  ├─ fix セクション → ParseFix/Pinning/Images
  └─ network セクション → ParseNetwork/GitHub
  ↓
LintConfigParseResult { Rules, Exclusions, Fix, Network, Diagnostics }
  ↓
LintConfigLibrary.Validate()
  ├─ NormalizeRules() → RuleNormalizer.NormalizeRuleEntries()
  │    → RuleSpecificConfigNormalizer.Normalize() (per-rule switch)
  ├─ NormalizeExclusions() → ExclusionNormalizer
  ├─ NormalizeFix()
  └─ NormalizeNetwork()
  ↓
LintConfig (最終形)
  ↓
LintEngine.Check()
  ↓ rule.SetConfig(effectiveConfig)
  ↓ 各ルールが GetRuleConfig(Id)?.Specific is XxxSpecificConfig でキャスト
```

### 1.3 課題の整理

#### 課題 A: Config モデルが YAML 構造と乖離しており、双方向の対応が想像しにくい

**YAML (ユーザーが書くもの):**
```yaml
rules:
  dangerous-triggers:
    severity: warning
    events:
      extend:
        - issue_comment
```

**C# Config (コードで触るもの):**
```csharp
RuleConfig {
    Severity = Warning,
    Specific = DangerousTriggersSpecificConfig(Events: ["issue_comment"])
}
```

問題点:
- YAML 上の `events.extend` が C# では `DangerousTriggersSpecificConfig.Events` になる。`extend` というコンセプトが Config モデルから消えている。
- `RuleSpecificConfig` が抽象基底の discriminated union で、`Specific` プロパティ経由でアクセスする。YAML キー名 (`events`, `known-hosted-labels`) とクラス名 (`DangerousTriggersSpecificConfig`) の対応が暗黙的。
- ルール固有キーと共通キー (`enabled`, `severity`) が別の抽象レイヤーに分離されているが、YAML 上はフラットに並ぶ。
- 新しいルール固有キーを追加するとき、YAML→Config の対応を理解するために `LintConfigYamlParser.AddRule()` → `LintConfigRuleBodyMaterializer.BuildSpecific()` → `RuleSpecificConfigNormalizer.Normalize()` → ルールの `SetConfig()` を全て追わないと全体像が掴めない。

#### 課題 B: パーサーの見通しの悪さと新規ルール追加時の変更箇所の多さ

`LintConfigYamlParser.AddRule()` は全ルールの全キーを 1 つの巨大な switch で処理する。新規ルール固有キー追加時に修正が必要な箇所:

1. **`LintConfig.cs`** — 新しい `XxxSpecificConfig` record を追加
2. **`LintConfigYamlParser.cs`** — `AddRule()` の switch に case 追加 + ローカル変数追加
3. **`LintConfigRuleBodyMaterializer.cs`** — `BuildSpecific()` の switch に case 追加 + パラメータ追加
4. **`RuleSpecificConfigNormalizer.cs`** — `Normalize()` の switch に case 追加
5. **`RuleCatalog.cs`** — `BuildAllowedRuleConfigKeys()` に許可キー追加
6. **ルール実装** — `SetConfig()` でパターンマッチ追加

**6 箇所の変更が必要** で、それぞれが別のファイルに散在している。特に `LintConfigRuleBodyMaterializer.BuildSpecific()` はパラメータが 14 個もあり、新規追加のたびに全ルール無関係のパラメータも含めて引き渡す必要がある。

さらに `LintConfigYamlParser` について:
- VYaml の `YamlSerializer.Deserialize<Dictionary<string,object?>>()` で一度 untyped DOM に変換してから手動ウォークする二段構え。DOM 化のコストと、`AsMap()`/`AsList()` のようなユーティリティでの boxing 型チェックが必要。
- パーサーのどこを読めば「このセクションはこういう YAML 構造を期待している」が分かるかが一目で分からない。`AddRule()` 内のフラットな switch/case がその唯一の情報源。

sandbox の `NewLintConfigLibrary.cs` プロトタイプでは以下の案を試していたが、行ベースパーサー部分は YAML 仕様 (フロースタイル、アンカー等) を捨てるため破棄済み。一方、Config モデル側の改善 (フラットプロパティ + `ExtendableList`) は有効で、パーサー技術と独立に採用できる:
- `RuleSpecificConfig` discriminated union を廃止し、`RuleConfig` にルール固有プロパティを直接持たせる (`Events`, `KnownHostedLabels`, `Allow`, `Deny` 等)
- `ExtendableList` record を導入して YAML の `extend:` セマンティクスを型で表現

## 2. 設計方針

### 2.1 目標

1. **Config モデルが YAML 構造を鏡映する**: YAML のキー階層と Config のプロパティ階層が 1:1 対応し、一方から他方を推測できる
2. **パーサーの見通し向上**: セクション単位で独立したパース関数を持ち、各セクションがどういう YAML を期待するか一目で分かる
3. **新規ルール追加の変更箇所を最小化**: 理想は 2-3 箇所以内 (Config 定義 + ルール実装 + 許可キー登録)
4. **既存テストが同等のセマンティクスで通る**: 動作の後方互換性を維持
5. **パフォーマンスの劣化なし**: Config YAML のサイズは小さいので主要関心事ではないが、不必要な退行を避ける

### 2.2 VYaml パーサー戦略

VYaml は以下の選択肢を提供する:

| 方式 | 概要 | 適合性 |
|---|---|---|
| `YamlSerializer.Deserialize<Dictionary<string,object?>>()` | untyped DOM 経由 | **現行方式。** 問題の本質はここではない |
| `YamlSerializer.Deserialize<T>()` + `[YamlObject]` source gen | 型付きデシリアライズ | rules セクションの dynamic key (rule-id) に不適 |
| `YamlParser` pull parser (ref struct) | event ベースの手動パース | config パースにはオーバーエンジニアリング |
| 行ベース自前パーサー | sandbox で試作・破棄済み | **不採用。** フロースタイル・アンカー等の YAML 仕様を捨てるトレードオフが悪い |

**決定: 現行の VYaml DOM パースを維持する。**

理由:
- 問題の本質はパーサー技術ではなく、パース後の **モデル構造** と **マテリアライズ パイプライン**
- VYaml DOM は YAML 仕様に準拠しており、フロースタイル (`{key: value}`) やアンカー/エイリアスを正しく扱える。この仕様準拠を捨てるべきではない
- `AsMap()`/`AsList()` ユーティリティは冗長だが、config パースは hot path ではなく実害はない
- パーサー内の `Convert()` → `AddRule()` のセクション構造自体は今のままで十分読みやすい。問題は `AddRule()` の **出口** (ローカル変数の収集 → 14 引数のマテリアライザー呼び出し) にある

### 2.3 Config モデル設計

#### 現行の discriminated union 方式 (廃止)

```
RuleConfig
  ├── Enabled: bool
  ├── Severity: DiagnosticSeverity?
  └── Specific: RuleSpecificConfig  ← abstract base
       ├── DangerousTriggersSpecificConfig(Events)
       ├── RunnerLabelSpecificConfig(KnownHostedLabels)
       ├── CredentialsSpecificConfig(PublicRegistries)
       ├── UntrustedTriggersSpecificConfig(UntrustedTriggers)
       ├── UnredactedSecretsSpecificConfig(OutputCommands)
       ├── ExprUndefinedVarSpecificConfig(AssumeEvents)
       ├── ForbiddenUsesSpecificConfig(Allow, Deny)
       └── OverprovisionedSecretsSpecificConfig(MaxStepEnvSecrets, MaxJobSecrets)
```

#### 新方式: フラット・プロパティ + ExtendableList

```
RuleConfig
  ├── Enabled: bool
  ├── Severity: DiagnosticSeverity?
  │
  │  ── extend 系 (ExtendableList?) ──
  ├── Events: ExtendableList?
  ├── KnownHostedLabels: ExtendableList?
  ├── PublicRegistries: ExtendableList?
  ├── UntrustedTriggers: ExtendableList?
  ├── OutputCommands: ExtendableList?
  │
  │  ── 直接リスト系 ──
  ├── AssumeEvents: IReadOnlyList<string>?
  ├── Allow: IReadOnlyList<string>?
  ├── Deny: IReadOnlyList<string>?
  │
  │  ── スカラー系 ──
  ├── MaxStepEnvSecrets: int?
  └── MaxJobSecrets: int?

ExtendableList
  └── Extend: IReadOnlyList<string>
```

YAML との対応:

```yaml
rules:
  dangerous-triggers:           # → rules["dangerous-triggers"]
    enabled: true               # → RuleConfig.Enabled = true
    severity: warning           # → RuleConfig.Severity = Warning
    events:                     # → RuleConfig.Events
      extend:                   # → ExtendableList.Extend
        - issue_comment         # → ["issue_comment"]

  forbidden-uses:               # → rules["forbidden-uses"]
    allow:                      # → RuleConfig.Allow
      - actions/*               # → ["actions/*"]
    deny:                       # → RuleConfig.Deny
      - some-org/*              # → ["some-org/*"]

  overprovisioned-secrets:      # → rules["overprovisioned-secrets"]
    max-step-env-secrets: 3     # → RuleConfig.MaxStepEnvSecrets = 3
    max-job-secrets: 5          # → RuleConfig.MaxJobSecrets = 5
```

**利点:**
- YAML のキーがそのまま Config のプロパティ名に対応する (kebab-case → PascalCase の機械的変換)
- `ExtendableList` が `extend:` セマンティクスを型として表現し、YAML 構造が Config から想像できる
- `RuleSpecificConfig` 抽象基底 + discriminated union が不要になり、ルールの `SetConfig()` でのパターンマッチが直接プロパティアクセスに変わる
- 新規ルール固有キーの追加は `RuleConfig` にプロパティを足すだけ

**ルール側の消費パターン変更:**

```csharp
// Before (discriminated union)
var events = config.GetRuleConfig(Id)?.Specific is DangerousTriggersSpecificConfig s
    ? BuildNormalizedSet(s.Events) : [];

// After (direct property)
var events = config.GetRuleConfig(Id)?.Events?.Extend is { } list
    ? BuildNormalizedSet(list) : [];
```

### 2.4 許可キーバリデーションの一元化

現行の `RuleCatalog.BuildAllowedRuleConfigKeys()` は rule-id → 許可キー Set の手動マッピング。新方式では:

`RuleCatalog.BuildAllowedRuleConfigKeys()` は引き続き使うが、キー名が `RuleConfig` のプロパティ名 (の kebab-case) と一致するため、追加忘れを防ぎやすくなる。

### 2.5 マテリアライズの統合

現行パイプライン:
```
AddRule() switch → ~10 ローカル変数に収集
  → LintConfigRuleBodyMaterializer.BuildSpecific(ruleId, 14 args)
    → switch(ruleId) で RuleSpecificConfig サブクラスを new
      → RuleConfig.Specific にセット
        → 後で RuleSpecificConfigNormalizer が switch(Specific type) で正規化
```

フラットモデルでは中間段が消える:
```
AddRule() switch → RuleConfig のプロパティに直接セット
  → RuleConfigNormalizer が non-null プロパティを正規化
```

具体的な変更:
- **`LintConfigRuleBodyMaterializer` を廃止** — `AddRule()` の switch/case 内で `RuleConfig` のプロパティを直接構築するため、ローカル変数の収集と 14 引数リレーが不要
- **`AddRule()` の許可キー検証を統合** — 現行は materializer 内で `RuleCatalog.TryGetAllowedConfigKeys()` を呼んでいたが、`AddRule()` 内の `seenRuleSpecificKeys` チェックに一本化。materializer への依存が消える
- **`RuleSpecificConfigNormalizer` → `RuleConfigNormalizer` にリファクタ** — discriminated union のパターンマッチ (`case DangerousTriggersSpecificConfig`) ではなく、プロパティ存在チェック (`config.Events is not null`) に変更。ruleId ごとの分岐が原則不要になる

## 3. 実行計画

### Phase 1: Config モデルの変更

**変更対象:**
- `LintConfig.cs`

**作業内容:**
1. `RuleSpecificConfig` 抽象基底クラスと全サブクラスを削除
2. `RuleConfig` に `ExtendableList?` プロパティ群と直接リスト/スカラープロパティを追加
3. `ExtendableList` record を新設 (`sealed record ExtendableList(IReadOnlyList<string> Extend)`)

**影響:** コンパイルエラーが多数出るが、後続フェーズで解消

### Phase 2: パーサーの簡素化と Materializer 廃止

**変更対象:**
- `LintConfigYamlParser.cs` (`AddRule()` 内部の書き換え)
- `LintConfigRuleBodyMaterializer.cs` (削除)

**作業内容:**
1. `AddRule()` の switch/case が `RuleConfig` プロパティを直接セットするように変更
2. `LintConfigRuleBodyMaterializer` を削除
3. 許可キー検証 (`seenRuleSpecificKeys` + `RuleCatalog.TryGetAllowedConfigKeys()`) を `AddRule()` 末尾に移動
4. `LintConfigParseResult` は構造そのまま

**Before (AddRule 出口):**
```csharp
// ~10 個のローカル変数を収集した後:
var config = new RuleConfig
{
    Enabled = enabled,
    Severity = severity,
    Specific = LintConfigRuleBodyMaterializer.BuildSpecific(
        ruleId, seenRuleSpecificKeys,
        events, knownHostedLabels, publicRegistries,
        untrustedTriggers, outputCommands, assumeEvents,
        allow, deny, maxStepEnvSecrets, maxJobSecrets,
        DomLine, diagnostics, filePath),
};
```

**After (AddRule 出口):**
```csharp
// switch/case で直接プロパティを埋めた後:
var config = new RuleConfig
{
    Enabled = enabled,
    Severity = severity,
    Events = events,
    KnownHostedLabels = knownHostedLabels,
    PublicRegistries = publicRegistries,
    UntrustedTriggers = untrustedTriggers,
    OutputCommands = outputCommands,
    AssumeEvents = assumeEvents,
    Allow = allow,
    Deny = deny,
    MaxStepEnvSecrets = maxStepEnvSecrets,
    MaxJobSecrets = maxJobSecrets,
};
// 許可キー検証をここで実行
ValidateAllowedKeys(ruleId, seenRuleSpecificKeys, ...);
```

ローカル変数は残るが、それらを 14 引数で別メソッドにリレーする必要がなくなるのが本質的な改善。

**VYaml DOM パース部分 (`Convert()`, `ParseFix`, `ParseNetwork` 等) は変更なし。**

### Phase 3: 正規化の統合

**変更対象:**
- `RuleSpecificConfigNormalizer.cs` → `RuleConfigNormalizer.cs` にリネーム
- `RuleNormalizer.cs` (微修正)
- `LintConfigLibrary.cs` (微修正)

**作業内容:**
1. `RuleSpecificConfigNormalizer` を `RuleConfigNormalizer` にリネーム
2. 内部の switch を `RuleSpecificConfig` サブクラスのパターンマッチから、`RuleConfig` のプロパティ存在チェック (`Events is not null`, `Allow is not null` 等) に変更
3. `RuleNormalizer.NormalizeRuleEntries()` が `RuleConfigNormalizer.Normalize()` を呼ぶ形は維持
4. `LintConfigLibrary` の `NormalizeRules` は `RuleNormalizer` に委譲する現行パターンを維持

### Phase 4: ルール側の消費パターン変更

**変更対象:**
- `DangerousTriggersRule.cs`, `RunnerLabelRule.cs`, `CredentialsRule.cs`, `CachePoisoningRule.cs`, `SelfHostedRunnerRule.cs`, `UnredactedSecretsRule.cs`, `ExprUndefinedVarRule.cs`, `ForbiddenUsesRule.cs`, `OverprovisionedSecretsRule.cs`

**作業内容:**
各ルールの `SetConfig()` を更新:

```csharp
// Before
if (config.GetRuleConfig(Id)?.Specific is DangerousTriggersSpecificConfig specific)
    additionalDangerousEvents = BuildNormalizedSet(specific.Events);

// After
if (config.GetRuleConfig(Id)?.Events?.Extend is { Count: > 0 } events)
    additionalDangerousEvents = BuildNormalizedSet(events);
```

機械的な書き換えで、ルールのロジック自体は変わらない。

### Phase 5: RuleCatalog の許可キー更新

**変更対象:**
- `RuleCatalog.cs`

**作業内容:**
`BuildAllowedRuleConfigKeys()` のキー名を現行のまま維持 (YAML キー名は変わらないため)。内部実装は変更不要。

### Phase 6: テスト更新

**変更対象:**
- `tests/Seiton.Core.Tests/LintConfigLibraryTests.cs`
- `tests/Seiton.Core.Tests/OnlineAuditConfigTests.cs`
- `tests/Seiton.Core.Tests/PinResolutionConfigTests.cs`
- 必要に応じて他の lint テスト

**作業内容:**
1. テスト内の `RuleSpecificConfig` パターンマッチを `RuleConfig` のプロパティアクセスに変更
2. セマンティクスは同一なので、テストの期待値は変わらない
3. sandbox のプロトタイプテスト (`NewLintConfigLibraryTests.cs` 等) を参考に、本体テストを調整

### Phase 7: クリーンアップ

**変更対象:**
- 不要ファイルの削除

**作業内容:**
1. `LintConfigRuleBodyMaterializer.cs` の削除 (Phase 2 で使わなくなった)
2. `RuleSpecificConfig` 関連の全サブクラス削除確認 (Phase 1 で実施済み)
3. `RuleConfigHelpers.cs` — `BuildNormalizedSet()` がルール側で引き続き使われる場合は残す

## 4. 変更後の全体像

### 4.1 ファイル構成

| ファイル | 責務 | 変更 |
|---|---|---|
| `LintConfig.cs` | Config モデル (LintConfig, RuleConfig, ExtendableList, FixConfig, NetworkConfig 等) | **大** (モデル再設計) |
| `LintConfigYamlParser.cs` | VYaml DOM パーサー (現行維持、`AddRule()` 出口を簡素化) | **中** (`AddRule` 修正) |
| `LintConfigParseResult.cs` | パーサー出力型 | 変更なし |
| `LintConfigValidationResult.cs` | バリデーション結果型 | 変更なし |
| `RuleConfigNormalizer.cs` | RuleConfig のプロパティ正規化 (旧 `RuleSpecificConfigNormalizer`) | **中** (リネーム+リファクタ) |
| `RuleNormalizer.cs` | rule-id 解決 + ポリシー適用 + RuleConfigNormalizer 呼び出し | **小** |
| `ExclusionNormalizer.cs` | exclusion rule-id 解決 | 変更なし |
| `RuleCatalog.cs` | ルール定義 + 許可キー | 変更なし |
| `LintConfigLibrary.cs` | Validate エントリポイント | **小** |
| ~~`LintConfigRuleBodyMaterializer.cs`~~ | | **削除** |

### 4.2 新規ルール固有キー追加時の変更箇所

| # | ファイル | 作業 |
|---|---|---|
| 1 | `LintConfig.cs` | `RuleConfig` にプロパティ追加 |
| 2 | `LintConfigYamlParser.cs` | `ParseRuleBody()` の switch に case 追加 |
| 3 | `RuleConfigNormalizer.cs` | 正規化ロジック追加 (extend 系なら既存パターンにフォールイン) |
| 4 | `RuleCatalog.cs` | `BuildAllowedRuleConfigKeys()` に許可キー追加 |
| 5 | ルール実装 | `SetConfig()` でプロパティ読み取り |

**現行 6 箇所 → 5 箇所** (LintConfigRuleBodyMaterializer 削除分)。ただし:
- 各箇所の変更量が小さい (プロパティ追加 or case 追加)
- パラメータリレー (14 引数の受け渡し) が不要
- extend 系であれば RuleConfigNormalizer の変更は既存の `NormalizeExtendList()` を呼ぶだけ

### 4.3 データフロー (変更後)

```
YAML bytes
  ↓ YamlSerializer.Deserialize<Dictionary<string, object?>>()  ← 現行維持
  ↓ (untyped DOM)
  ↓
LintConfigYamlParser.Convert()                                  ← 現行維持
  ├─ rules セクション → AddRule() で switch(key)
  │    ↓ RuleConfig のプロパティに直接セット       ← ★変更点: ローカル変数リレー廃止
  │    ↓ 許可キー検証を AddRule() 内で実行         ← ★変更点: Materializer 廃止
  │    ↓ RuleConfig { Enabled, Severity, Events?, Allow?, ... }
  ├─ exclusions → AddExclusion()                                ← 変更なし
  ├─ fix → ParseFix/Pinning/Images                              ← 変更なし
  └─ network → ParseNetwork/GitHub                              ← 変更なし
  ↓
LintConfigParseResult { Rules, Exclusions, Fix, Network, Diagnostics }
  ↓
LintConfigLibrary.Validate()
  ├─ NormalizeRules() → RuleNormalizer.NormalizeRuleEntries()
  │    → RuleConfigNormalizer.Normalize()         ← ★変更点: プロパティベース正規化
  ├─ NormalizeExclusions()                                      ← 変更なし
  ├─ NormalizeFix()                                             ← 変更なし
  └─ NormalizeNetwork()                                         ← 変更なし
  ↓
LintConfig (最終形)
  ↓
LintEngine.Check()
  ↓ rule.SetConfig(effectiveConfig)
  ↓ 各ルールが GetRuleConfig(Id)?.Events?.Extend 等で直接アクセス ← ★変更点
```

### 4.4 Config モデル↔YAML 対応表

```
YAML path                              → C# property path
─────────────────────────────────────── → ──────────────────────────────────────
rules.<id>.enabled                      → RuleConfig.Enabled
rules.<id>.severity                     → RuleConfig.Severity
rules.<id>.events.extend[]              → RuleConfig.Events.Extend
rules.<id>.known-hosted-labels.extend[] → RuleConfig.KnownHostedLabels.Extend
rules.<id>.public-registries.extend[]   → RuleConfig.PublicRegistries.Extend
rules.<id>.untrusted-triggers.extend[]  → RuleConfig.UntrustedTriggers.Extend
rules.<id>.output-commands.extend[]     → RuleConfig.OutputCommands.Extend
rules.<id>.assume-events[]              → RuleConfig.AssumeEvents
rules.<id>.allow[]                      → RuleConfig.Allow
rules.<id>.deny[]                       → RuleConfig.Deny
rules.<id>.max-step-env-secrets         → RuleConfig.MaxStepEnvSecrets
rules.<id>.max-job-secrets              → RuleConfig.MaxJobSecrets
exclusions[].files                      → LintExclusion.Files
exclusions[].rules[]                    → LintExclusion.Rules
exclusions[].jobs[]                     → LintExclusion.Jobs
fix.defaults.job-timeout-minutes        → FixConfig.Defaults.JobTimeoutMinutes
fix.pinning.enable-network              → FixConfig.Pinning.EnableNetwork
fix.pinning.min-age-days                → FixConfig.Pinning.MinAgeDays
fix.pinning.exclude-branches[]          → FixConfig.Pinning.ExcludeBranches
fix.pinning.ignore-actions[].uses       → IgnoreActionEntry.NamePattern
fix.pinning.ignore-actions[].ref        → IgnoreActionEntry.RefPattern
fix.images.enable-network               → FixConfig.Images.EnableNetwork
fix.images.exclude-images[]             → FixConfig.Images.ExcludeImages
fix.images.exclude-tags[]               → FixConfig.Images.ExcludeTags
fix.images.ignore-images[]              → FixConfig.Images.IgnoreImages
network.on-error                        → NetworkConfig.OnError
network.timeout-seconds                 → NetworkConfig.TimeoutSeconds
network.max-concurrency                 → NetworkConfig.MaxConcurrency
network.github.ghes-api-url             → GitHubNetworkConfig.GhesApiUrl
network.github.ghes-fallback            → GitHubNetworkConfig.GhesFallback
```

## 5. リスクと判断

### RuleConfig のプロパティ膨張
- ルール固有キーが増えると `RuleConfig` のプロパティ数が増える
- 現状 9 ルール分のオプションで 10 プロパティ程度。50 ルール以上に対応する規模にはならない見込み
- 仮に膨らんだ場合は、`RuleConfig` 内に `RuleExtendOptions` のようなネストした record を導入すれば対処可能

### 後方互換性
- YAML フォーマット自体は変わらない (ユーザーの config ファイルは無変更)
- 公開 API (`LintConfig`, `RuleConfig`) の形状が変わるため、外部から直接 Config を構築しているコード (テスト、ベンチマーク) は更新が必要

### VYaml DOM パースの維持
- `AsMap()`/`AsList()` の boxing 型チェックは冗長だが、config パースは hot path ではないため実害なし
- フロースタイル (`{key: value}`) やアンカー/エイリアスなど YAML 仕様への準拠を維持できるメリットが大きい
- 将来の YAML 機能追加時にパーサー側の対応が不要

## 6. RuleId enum 化

### 6.1 動機

Phase 1-7 で Config モデルをフラット化したが、ルール ID は依然として `string` リテラルがコードベース全体に散在している:

- 各ルール (48+4) が `override string Id => "kebab-case-name"` で宣言
- `RuleCatalog` に 56+ 箇所の string リテラル (登録テーブル、ポリシーマップ、許可キーマップ)
- `LintEngine` の `IsRuleEnabled`, `TryGetSeverityOverride` 等が `string?` パラメータ
- タイポがコンパイル時に検出できない

**enum にすることで得られるもの:**
- コンパイル時のルール ID 安全性 (タイポ不可、switch 網羅性チェック CS8524)
- 各ルールから `override string Id` プロパティが不要になる (コンストラクタで渡す)
- `RuleCatalog` の Dictionary ルックアップが enum (int) ベースに改善
- Online ルールの `const string RuleId` パターンも不要に

### 6.2 設計

#### 境界の定義

| レイヤー | 型 | 理由 |
|---|---|---|
| `IRule.Id` | `RuleId` (enum) | 内部ルールシステムの型安全性 |
| `RuleCatalog` 内部データ | `RuleId` | enum 配列/Set/Dictionary で高速ルックアップ |
| `LintConfig.Rules` dict key | **`string` (変更なし)** | YAML パーサー出力と共通。変更の波及が大きく利点が少ない |
| `Diagnostic.RuleId` | **`string?` (変更なし)** | パーサー診断と共用の boundary 型。SARIF 出力にそのまま流れる |
| `LintExclusion.Rules` | **`IReadOnlyList<string>` (変更なし)** | YAML ユーザー入力のまま正規化 |
| SARIF/CLI 出力 | `string` | 外部仕様 |

**変換ポイント:**
- `RuleBase` 診断発行時: `Id.ToId()` → `Diagnostic.RuleId` (string)
- `RuleNormalizer`: `TryResolveRuleId(string) → RuleId` で解決、`resolvedId.ToId()` で dict キーに戻す
- `LintConfig.GetRuleConfig(RuleId)`: オーバーロード追加、内部で `ruleId.ToId()` → 既存 string dict ルックアップ
- `LintEngine.IsRuleEnabled(rule.Id, ...)`: `rule.Id` は `RuleId` だが `Id.ToId()` で string dict を参照

`LintConfig.Rules` の dict key を `string` のまま維持する設計により、`LintConfigYamlParser`, `LintEngine` の dict ルックアップ、テストの dict 構築コード (`Rules = new Dictionary<string, RuleConfig> { ["rule-id"] = ... }`) は **変更不要**。

#### RuleId enum

```csharp
public enum RuleId { JobStructure, ReusableWorkflow, ..., LocalActionInputs }  // 52 values
```

enum の int 値はメンバー追加順 (auto-increment)。Priority とは独立。

#### RuleIdExtensions

```csharp
internal static class RuleIdExtensions
{
    // NativeAOT 安全。string リテラル返却 (interned, zero-alloc)
    public static string ToId(this RuleId id) => id switch
    {
        RuleId.DangerousTriggers => "dangerous-triggers",
        ...
    };

    // YAML パース境界での string→enum 変換。FrozenDictionary で O(1)
    public static bool TryParse(string value, out RuleId ruleId) => ...;
}
```

#### RuleBase 変更

```csharp
// Before
public abstract class RuleBase : IRule
{
    public abstract string Id { get; }
    ...
    RuleId: Id,  // Diagnostic 発行時
}

// After
public abstract class RuleBase : IRule
{
    public RuleId Id { get; }
    protected RuleBase(RuleId id) => Id = id;
    ...
    RuleId: Id.ToId(),  // Diagnostic 発行時
}
```

#### 各ルールの変更

```csharp
// Before
public sealed class DangerousTriggersRule : RuleBase
{
    public override string Id => "dangerous-triggers";
    public override string Name => "Dangerous Triggers Rule";
}

// After
public sealed class DangerousTriggersRule() : RuleBase(RuleId.DangerousTriggers)
{
    public override string Name => "Dangerous Triggers Rule";
}
```

`SetConfig()` 内の `config.GetRuleConfig(Id)` は `GetRuleConfig(RuleId)` オーバーロードにより変更不要。

#### RuleCatalog 変更

```csharp
// Tuple 型を enum に
private static readonly (RuleId Id, int Priority, Func<IRule> Factory)[] DefaultRuleFactories = [...];

// ポリシーマップを enum キーに
private static readonly HashSet<RuleId> NonDisableableRuleIds = [RuleId.DenyWriteAll, RuleId.DenyReadAll];

// TryResolveRuleId の返却型を enum に
public static bool TryResolveRuleId(string? input, out RuleId resolvedRuleId) { ... }

// string 引数版も維持 (Diagnostic.RuleId 等の boundary 用)
public static int GetPriority(string? ruleId) { ... }
public static int GetPriority(RuleId ruleId) { ... }
```

### 6.3 実行計画

#### Phase 8: RuleId enum + RuleIdExtensions (新規ファイル)

**作業:** `RuleId.cs`, `RuleIdExtensions.cs` を `src/Seiton.Core/Linting/` に追加。52 メンバーの enum + `ToId()` switch + `TryParse()` FrozenDictionary。既存コードへの影響なし。

#### Phase 9: IRule.Id + RuleBase + 全ルールクラス

**作業:**
1. `IRule.Id` を `string` → `RuleId` に変更
2. `RuleBase` を abstract プロパティからコンストラクタ注入に変更。Diagnostic 発行で `Id.ToId()` を使用
3. `OnlineRuleBase` にコンストラクタ追加
4. 48 default ルール + 4 online ルール: `override string Id` 削除、primary constructor で `RuleId.Xxx` を渡す
5. Online ルールの `const string RuleId` フィールド削除

#### Phase 10: RuleCatalog

**作業:**
1. tuple 型を `(RuleId, int, Func<IRule/IOnlineRule>)` に変更
2. `AllRuleMetadata` を `(RuleId, int)[]` に変更
3. `NonDisableableRuleIds` → `HashSet<RuleId>`、`MinimumSeverities` → `Dictionary<RuleId, DiagnosticSeverity>`
4. `AllowedRuleConfigKeys` → `Dictionary<RuleId, IReadOnlySet<string>>`
5. `TryResolveRuleId` 返却型を `RuleId` に変更
6. `CanonicalRuleIdToRuleId` → `Dictionary<string, RuleId>`
7. public メソッドの `string?` パラメータ版は `RuleId` 版とオーバーロードで維持 (boundary 用)

#### Phase 11: LintConfig + RuleNormalizer + LintEngine 等

**作業:**
1. `LintConfig.GetRuleConfig(RuleId)` オーバーロード追加 (`ruleId.ToId()` で既存 string dict 参照)
2. `RuleNormalizer`: `TryResolveRuleId` の out が `RuleId` に変わるため `resolvedRuleId.ToId()` で dict キー生成
3. `RuleConfigNormalizer`: `ruleId` パラメータ型変更 (string → RuleId)
4. `LintEngine`: `IsRuleEnabled(rule.Id, ...)` — `rule.Id` は `RuleId` になるが内部で `Id.ToId()` で string dict 参照
5. `PinRemediationEngine`: `const string` → `RuleId` enum 値に変更

#### Phase 12: テスト + ビルド検証

**作業:**
- テスト内の `RuleConfig` 構築 (`new Dictionary<string, RuleConfig> { ["rule-id"] = ... }`) → **変更不要** (dict key は string のまま)
- テスト内の `Diagnostic.RuleId == "..."` アサーション → **変更不要** (Diagnostic.RuleId は string のまま)
- テスト内で `rule.Id` を直接参照している箇所のみ `RuleId` enum に更新
- `dotnet build` + `dotnet test` で全テスト通過を確認

### 6.4 影響範囲

| カテゴリ | 変更量 | 備考 |
|---|---|---|
| 新規ファイル | 2 | `RuleId.cs`, `RuleIdExtensions.cs` |
| ルールクラス | 52 | 機械的変更 (override Id 削除 + constructor) |
| `IRule.cs`, `RuleBase.cs`, `OnlineRuleBase.cs` | 3 | interface + base class |
| `RuleCatalog.cs` | 1 | enum 化 (中規模) |
| `LintConfig.cs` | 1 | overload 追加のみ |
| `RuleNormalizer.cs`, `RuleConfigNormalizer.cs` | 2 | パラメータ型変更 |
| `LintEngine.cs` | 1 | 小規模 |
| `PinRemediationEngine.cs` | 1 | const 変更 |
| テスト | 最小限 | dict key/Diagnostic.RuleId が string のまま |
