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

sandbox の `NewLintConfigLibrary.cs` プロトタイプでは、VYaml DOM → 行ベースパーサー (`LintConfigLineParser`) への移行を試みている。このプロトタイプでは:
- `RuleSpecificConfig` discriminated union を廃止し、`RuleConfig` にルール固有プロパティを直接持たせている (`Events`, `KnownHostedLabels`, `Allow`, `Deny` 等)
- `ExtendableList` record を導入して YAML の `extend:` セマンティクスを型で表現
- 行ベースパーサーでインデントレベルごとにセクションを明確に分離

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
| `YamlSerializer.Deserialize<Dictionary<string,object?>>()` | untyped DOM 経由 | **現行方式。** boxing + 型チェック冗長 |
| `YamlSerializer.Deserialize<T>()` + `[YamlObject]` source gen | 型付きデシリアライズ | Config YAML はキーが hyphenated (kebab-case) で、`[YamlObject]` は rename mapping が限定的。fix/network は相性良いが rules セクションは dynamic key (rule-id) があり不適 |
| `YamlParser` pull parser (ref struct) | event ベースの手動パース | 最も柔軟。lint config 程度なら string 変換許容。ただし config YAML は小さく、パフォーマンスが主要関心事ではない |
| 行ベース自前パーサー | sandbox プロトタイプ方式 | VYaml 不要で最もシンプル。config YAML はインデントが固定で YAML の高度な機能 (アンカー、フロースタイル等) を使わないため十分 |

**推奨: 行ベースパーサー (`LintConfigLineParser`) を採用する。**

理由:
- Config YAML はユーザーが手書きする小さなファイルで、アンカー/エイリアス/フロースタイルは使わない前提
- 行ベースパーサーはセクション構造がインデントレベルで明確に分離され、「どの YAML を読もうとしているか」がコードから直読できる
- VYaml DOM 経由の `AsMap()`/`AsList()` boxing 型チェックが不要になる
- sandbox プロトタイプで実証済み
- VYaml pull parser (ref struct) は高性能だが、config パースにはオーバーエンジニアリング

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

### 2.5 正規化の統合

現行は `LintConfigRuleBodyMaterializer` + `RuleSpecificConfigNormalizer` の 2 段階で rule-specific config を構築・正規化するが、新方式では:

- `LintConfigRuleBodyMaterializer` を**廃止** — パーサーが直接 `RuleConfig` のプロパティに値をセットするため不要
- `RuleSpecificConfigNormalizer` を**リネーム/リファクタ** — `RuleConfig` のフラットプロパティを正規化する `RuleConfigNormalizer` に変更。switch が rule-specific ではなくプロパティベースになり、ルール固有の知識が減る

## 3. 実行計画

### Phase 1: Config モデルの変更

**変更対象:**
- `LintConfig.cs`

**作業内容:**
1. `RuleSpecificConfig` 抽象基底クラスと全サブクラスを削除
2. `RuleConfig` に `ExtendableList?` プロパティ群と直接リスト/スカラープロパティを追加
3. `ExtendableList` record を新設 (`sealed record ExtendableList(IReadOnlyList<string> Extend)`)

**影響:** コンパイルエラーが多数出るが、後続フェーズで解消

### Phase 2: パーサーの置換

**変更対象:**
- `LintConfigYamlParser.cs` (書き換え)
- `LintConfigRuleBodyMaterializer.cs` (削除)

**作業内容:**
1. `LintConfigYamlParser` を行ベースの `LintConfigLineParser` に置換 (sandbox プロトタイプベース)
2. `LintConfigRuleBodyMaterializer` を削除 (パーサーが直接 `RuleConfig` プロパティに値をセット)
3. `LintConfigParseResult` は構造はそのまま (パーサーの出力型)

**設計ポイント:**
- セクションごとに `ParseRulesSection()`, `ParseRuleBody()`, `ParseFixSection()`, `ParseNetworkSection()`, `ParseExclusionsSection()` と明確に分離
- 各 Parse メソッドがどのインデントレベルのどのキーを期待するかがコードから直読できる
- `AddRule()` 内のフラットな switch は残るが、収集先が `RuleConfig` のプロパティに直接マッピングされるので、ローカル変数の受け渡しが不要

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
3. sandbox プロトタイプの `NewLintConfigLibrary.cs` 等は参照用として残す (or 削除)

## 4. 変更後の全体像

### 4.1 ファイル構成

| ファイル | 責務 |
|---|---|
| `LintConfig.cs` | Config モデル (LintConfig, RuleConfig, ExtendableList, FixConfig, NetworkConfig 等) |
| `LintConfigYamlParser.cs` | 行ベースパーサー (LintConfigLineParser) |
| `LintConfigParseResult.cs` | パーサー出力型 (変更なし) |
| `LintConfigValidationResult.cs` | バリデーション結果型 (変更なし) |
| `RuleConfigNormalizer.cs` | RuleConfig のプロパティ正規化 (旧 RuleSpecificConfigNormalizer) |
| `RuleNormalizer.cs` | rule-id 解決 + ポリシー適用 + RuleConfigNormalizer 呼び出し (微修正) |
| `ExclusionNormalizer.cs` | exclusion rule-id 解決 (変更なし) |
| `RuleCatalog.cs` | ルール定義 + 許可キー (変更なし) |
| `LintConfigLibrary.cs` | Validate エントリポイント (微修正) |
| ~~`LintConfigRuleBodyMaterializer.cs`~~ | **削除** |

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
YAML text (string)
  ↓
LintConfigLineParser(yamlText, filePath)
  ├─ ParseRulesSection()
  │    └─ ParseRuleBody(ruleId) → RuleConfig { Enabled, Severity, Events?, Allow?, ... }
  ├─ ParseExclusionsSection()
  │    └─ ParseExclusionItem() → LintExclusion
  ├─ ParseFixSection()
  │    ├─ ParseFixDefaultsSection()
  │    ├─ ParseFixPinningSection()
  │    └─ ParseFixImagesSection()
  └─ ParseNetworkSection()
       └─ ParseNetworkGitHubSection()
  ↓
LintConfigParseResult { Rules, Exclusions, Fix, Network, Diagnostics }
  ↓
LintConfigLibrary.Validate()
  ├─ NormalizeRules() → RuleNormalizer.NormalizeRuleEntries()
  │    → RuleConfigNormalizer.Normalize() (プロパティベースの正規化)
  ├─ NormalizeExclusions()
  ├─ NormalizeFix()
  └─ NormalizeNetwork()
  ↓
LintConfig (最終形)
  ↓
LintEngine.Check()
  ↓ rule.SetConfig(effectiveConfig)
  ↓ 各ルールが GetRuleConfig(Id)?.Events?.Extend 等で直接アクセス
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

### 行ベースパーサーの制限
- アンカー (`&`)/エイリアス (`*`) やフロースタイル (`{key: value}`) は非対応
- Config YAML でこれらの機能を使うユースケースは想定しない。もし将来必要になれば VYaml pull parser に移行可能

### RuleConfig のプロパティ膨張
- ルール固有キーが増えると `RuleConfig` のプロパティ数が増える
- 現状 9 ルール分のオプションで 10 プロパティ程度。50 ルール以上に対応する規模にはならない見込み
- 仮に膨らんだ場合は、`RuleConfig` 内に `RuleExtendOptions` のようなネストした record を導入すれば対処可能

### 後方互換性
- YAML フォーマット自体は変わらない (ユーザーの config ファイルは無変更)
- 公開 API (`LintConfig`, `RuleConfig`) の形状が変わるため、外部から直接 Config を構築しているコード (テスト、ベンチマーク) は更新が必要
