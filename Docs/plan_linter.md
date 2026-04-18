# Linter Config リファクタリング計画

> `Seiton_config_review.md` で指摘された UI/UX 問題を解消するための、仕様・実装の修正計画。

---

## 0. 背景と目的

`Seiton_config_review.md` は、現行 config が **ユーザーの思考単位と config の切り方がズレている** ことを指摘している。主要な問題点を要約すると:

1. `additiveCustomization` — 内部実装の概念がそのまま表に出ている
2. `exprContext` — ユーザーにとって「何がしたいか」が分からない名前
3. `pin_resolution` / `online_audit` — サブシステム名が前面に出ており、共通のネットワーク設定が重複
4. `default_job_timeout_minutes_for_fix` — 冗長で所属が不明瞭
5. ルールに効く補助設定がルールから離れた場所にある
6. 命名規則が不統一（snake_case / camelCase / 長い複合語が混在）
7. `exclusions` のフィールド名が微妙に不自然（`filePattern` / `ruleIds`）

本計画は**案 1 + 案 2 の折衷**（review doc §3–4）を採用し、段階的に仕様と実装を移行する。

---

## 1. ターゲット Config 形状

### 1.1 Before（現行）

```yaml
rules:
  job-permissions-required:
    enabled: false
  deny-write-all:
    severity: error

additiveCustomization:
  additionalDangerousEvents:
    - issue_comment
  additionalKnownHostedLabels:
    - ubuntu-24.04-large
  additionalPublicRegistries:
    - registry.example.com
  additionalUntrustedTriggers:
    - issue_comment
  additionalOutputCommands:
    - tee
  forbiddenUsesDenyPatterns:
    - some-untrusted-org/*

exclusions:
  - filePattern: ".github/workflows/legacy-*.yml"
    ruleIds:
      - runner-no-latest
      - job-permissions-required
  - filePattern: ".github/workflows/release.yml"
    jobId: publish
    ruleIds:
      - credentials

exprContext:
  eventTypes:
    - workflow_dispatch

default_job_timeout_minutes_for_fix: 15

pin_resolution:
  allow_network: true
  github_actions:
    token_env_vars: [SEITON_GITHUB_TOKEN, GITHUB_TOKEN]
    min_age_days: 14
    exclude_branches: [main, master]
    ignore_actions:
      - name: "slsa-framework/.*"
        ref: ".*"
  images:
    exclude_images: [scratch]
    exclude_tags: [latest]
  fail_open: true
  request_timeout_sec: 30
  max_concurrency: 4

online_audit:
  allow_network: true
  github_actions:
    token_env_vars: [SEITON_GITHUB_TOKEN, GITHUB_TOKEN]
  fail_open: true
  request_timeout_sec: 30
  max_concurrency: 4
```

### 1.2 After（ターゲット）

```yaml
rules:
  job-permissions-required:
    enabled: false

  deny-write-all:
    severity: error

  dangerous-triggers:
    severity: error
    events:
      extend:
        - issue_comment

  action-shell-is-required:
    severity: warning

  runner-label:
    known-hosted-labels:
      extend:
        - ubuntu-24.04-large

  credentials:
    public-registries:
      extend:
        - registry.example.com

  cache-poisoning:
    untrusted-triggers:
      extend:
        - issue_comment

  unredacted-secrets:
    output-commands:
      extend:
        - tee

  forbidden-uses:
    deny:
      - some-untrusted-org/*

  # expr-undefined-var は式中の未定義コンテキストを検出するルール。
  # assume-events で「このワークフローはこのイベントで発火する」と仮定を与えることで、
  # イベント固有コンテキスト (github.event.inputs 等) の誤検出を抑える。
  expr-undefined-var:
    assume-events:
      - workflow_dispatch
      - repository_dispatch

  # online ルール: デフォルト無効。enabled: true にすると
  # ネットワーク経由の脆弱性/ref 監査が有効になる。
  known-vulnerable-actions:
    enabled: true
  impostor-commit:
    enabled: true
  ref-confusion:
    enabled: true
  stale-action-refs:
    enabled: true

exclusions:
  - files: ".github/workflows/legacy-*.yml"
    rules:
      - runner-no-latest
      - job-permissions-required

  - files: ".github/workflows/release.yml"
    jobs:
      - publish
    rules:
      - credentials

fix:
  defaults:
    job-timeout-minutes: 15

  pinning:
    enable-network: true
    min-age-days: 14
    exclude-branches:
      - main
      - master
    ignore-actions:
      - uses: "slsa-framework/.*"
        ref: ".*"

  images:
    enable-network: true
    exclude-images:
      - scratch
    exclude-tags:
      - latest

network:
  on-error: skip          # skip = エラー時はその診断をスキップして続行, fail = 即座にエラー終了
  timeout-seconds: 30
  max-concurrency: 4
  github:
    ghes-api-url: ""
    ghes-fallback: false
```

---

## 2. 変更マッピング

### 2.1 `additiveCustomization` → `rules.<rule-id>` 配下へ移動

| Before (additiveCustomization) | After (rules 配下) |
|---|---|
| `additionalDangerousEvents` | `rules.dangerous-triggers.events.extend` |
| `additionalKnownHostedLabels` | `rules.runner-label.known-hosted-labels.extend` |
| `additionalPublicRegistries` | `rules.credentials.public-registries.extend` |
| `additionalUntrustedTriggers` | `rules.cache-poisoning.untrusted-triggers.extend` (+ `rules.self-hosted-runner.untrusted-triggers.extend` で共有) |
| `additionalOutputCommands` | `rules.unredacted-secrets.output-commands.extend` |
| `forbiddenUsesAllowPatterns` | `rules.forbidden-uses.allow` |
| `forbiddenUsesDenyPatterns` | `rules.forbidden-uses.deny` |

**設計判断:** `extend` キーワードは built-in との関係を明示する。ユーザーは最終的に何が有効かを理解しやすい。

### 2.2 `exprContext` → `rules.expr-undefined-var` 配下へ移動

| Before | After |
|---|---|
| `exprContext.eventTypes` | `rules.expr-undefined-var.assume-events` |

**理由:** この設定は `expr-undefined-var` ルールに直接効く。ルールの近くに置くことで「この設定は何に効くのか」が自明になる。旧 `analysis` セクションは廃止。

### 2.3 `default_job_timeout_minutes_for_fix` → `fix.defaults`

| Before | After |
|---|---|
| `default_job_timeout_minutes_for_fix` | `fix.defaults.job-timeout-minutes` |

**理由:** fix 生成のデフォルト値であることが構造から明確になる。

### 2.4 `pin_resolution` → `fix.pinning` + `fix.images`

| Before | After |
|---|---|
| `pin_resolution.allow_network` | `fix.pinning.enable-network` / `fix.images.enable-network` |
| `pin_resolution.github_actions.min_age_days` | `fix.pinning.min-age-days` |
| `pin_resolution.github_actions.exclude_branches` | `fix.pinning.exclude-branches` |
| `pin_resolution.github_actions.ignore_actions` | `fix.pinning.ignore-actions` |
| `pin_resolution.images.exclude_images` | `fix.images.exclude-images` |
| `pin_resolution.images.exclude_tags` | `fix.images.exclude-tags` |
| `pin_resolution.images.ignore_images` | `fix.images.ignore-images` |

**理由:** fix サブセクションとして意味が通る。pinning (Actions SHA) と images (OCI digest) が独立にネットワーク有効化できる。

### 2.5 `online_audit` → `rules` セクションに吸収（`audit` セクション廃止）

| Before | After |
|---|---|
| `online_audit.allow_network: true` | `rules.known-vulnerable-actions.enabled: true` 等（個別ルール有効化） |

**理由:** online audit の 4 ルール（`known-vulnerable-actions`, `impostor-commit`, `ref-confusion`, `stale-action-refs`）はデフォルト `enabled: false` で、ユーザーが `rules` で有効化するだけで十分。「`audit` とは何か」「`enable-online-rules` とは何のルールか」という疑問が消える。ネットワーク必要性はこれらのルールが有効化された時点でシステムが自動判定する。

### 2.6 ネットワーク共通設定 → `network`

| Before (pin_resolution + online_audit に重複) | After |
|---|---|
| `*.fail_open` | `network.on-error` (`skip` / `fail`) |
| `*.request_timeout_sec` | `network.timeout-seconds` |
| `*.max_concurrency` | `network.max-concurrency` |
| `*.github_actions.ghes_api_url` | `network.github.ghes-api-url` |
| `*.github_actions.ghes_fallback` | `network.github.ghes-fallback` |

**理由:** ネットワーク設定の重複を解消。一箇所で管理する。`token_env_vars` は config 経由での変更メリットがほぼなく、悪意ある config で意図しない環境変数からトークンを読ませる攻撃経路になるため、config から除外しコード内にハードコードする（探索順序: `SEITON_GITHUB_TOKEN` → `GITHUB_TOKEN`）。

### 2.7 `exclusions` フィールド名の改善

| Before | After |
|---|---|
| `filePattern` | `files` |
| `ruleIds` | `rules` |
| `jobId` | `jobs` (リスト化) |

**理由:** 短く自然な名前。`jobs` はリスト化して複数ジョブ指定に対応。

### 2.8 命名規則の統一

- config YAML keys: **kebab-case** で統一
- rule-id: 既存の kebab-case を維持（変更なし）
- `additional*` プレフィックスの廃止 → `extend` パターンへ

---

## 3. 仕様書修正スコープ

### 3.1 `Seiton_Linter_spec.md` — 変更箇所

| セクション | 変更内容 |
|---|---|
| §5 Lint Configuration Contract | デフォルト値テーブルを新 config 形状に書き換え |
| §5.8 Rule-Specific Additive Customization | `additiveCustomization` を廃止し、§5.8.N を rules 配下の rule-specific config として再定義 |
| §5.9 Example Configuration | Before/After 両方の例を新形状に書き換え |
| §5.11 Configuration Profile Reference | Profile 3a/3b/4 の例を新キー名で書き換え |
| §12.3 Configuration (pin_resolution) | `fix.pinning` + `fix.images` + `network` への分割を反映 |
| 新規 §§5.12 (仮) | `fix` セクション仕様を追加 |
| 新規 §§5.13 (仮) | `network` セクション仕様を追加 |

### 3.2 `Seiton_Linter_csharp_spec.md` — 変更箇所

| セクション | 変更内容 |
|---|---|
| §4.1 Additive Rule Customization Mapping | `RuleSpecificAdditiveCustomization` 廃止。各ルールの config 型を `rules` 配下に移動 |
| §4.5 Network-Assisted Pin Remediation | `PinResolutionConfig` を `FixConfig` + `NetworkConfig` に分割 |
| 型定義全体 | `LintConfig` のプロパティ構造を新 config に合わせて更新。`AnalysisConfig` / `AuditConfig` 廃止 |

### 3.3 `Seiton_Linter_go_spec.md` — 変更箇所

| セクション | 変更内容 |
|---|---|
| §4.1 Additive Rule Customization Mapping | C# と同等の変更 |
| §4.5 Network-Assisted Pin Remediation | C# と同等の変更 |

---

## 4. C# 実装修正スコープ

### 4.1 型定義の変更

#### `LintConfig` (変更後)

```csharp
public sealed class LintConfig
{
    public byte[]? Utf8Yaml { get; init; }
    public string? FilePath { get; init; }

    // rules セクション: rule-id -> RuleConfig
    public IReadOnlyDictionary<string, RuleConfig>? Rules { get; init; }

    // exclusions セクション
    public IReadOnlyList<LintExclusion>? Exclusions { get; init; }

    // fix セクション
    public FixConfig Fix { get; init; } = new();

    // network セクション
    public NetworkConfig Network { get; init; } = new();
}
```

#### `RuleConfig` (新規: RuleOption + rule-specific config を統合)

```csharp
public sealed record RuleConfig
{
    // 共通 (旧 RuleOption)
    public bool Enabled { get; init; } = true;
    public DiagnosticSeverity? Severity { get; init; }

    // rule-specific 拡張 (旧 additiveCustomization 相当)
    public ExtendableList? Events { get; init; }                   // dangerous-triggers
    public ExtendableList? KnownHostedLabels { get; init; }        // runner-label
    public ExtendableList? PublicRegistries { get; init; }          // credentials
    public ExtendableList? UntrustedTriggers { get; init; }        // cache-poisoning, self-hosted-runner
    public ExtendableList? OutputCommands { get; init; }            // unredacted-secrets
    public IReadOnlyList<string>? AssumeEvents { get; init; }      // expr-undefined-var
    public IReadOnlyList<string>? Allow { get; init; }             // forbidden-uses
    public IReadOnlyList<string>? Deny { get; init; }              // forbidden-uses
}

public sealed record ExtendableList(IReadOnlyList<string> Extend);
```

#### `LintExclusion` (フィールド名変更)

```csharp
public sealed record LintExclusion(
    string Files,                           // 旧 FilePattern
    IReadOnlyList<string> Rules,            // 旧 RuleIds
    IReadOnlyList<string>? Jobs = null);    // 旧 JobId → リスト化
```

#### `AnalysisConfig` (廃止 → `RuleConfig.AssumeEvents` に吸収)

`rules.expr-undefined-var.assume-events` として `RuleConfig` のプロパティに吸収された。専用型は不要。

#### `FixConfig` (新規: fix セクション集約)

```csharp
public sealed record FixConfig
{
    public FixDefaultsConfig Defaults { get; init; } = new();
    public FixPinningConfig Pinning { get; init; } = new();
    public FixImagesConfig Images { get; init; } = new();
}

public sealed record FixDefaultsConfig
{
    public int? JobTimeoutMinutes { get; init; }  // 旧 default_job_timeout_minutes_for_fix
}

public sealed record FixPinningConfig
{
    public bool EnableNetwork { get; init; } = false;
    public int MinAgeDays { get; init; } = 14;
    public IReadOnlyList<string> ExcludeBranches { get; init; } = ["main", "master"];
    public IReadOnlyList<IgnoreActionEntry> IgnoreActions { get; init; } = [];
}

public sealed record FixImagesConfig
{
    public bool EnableNetwork { get; init; } = false;
    public IReadOnlyList<string> ExcludeImages { get; init; }   // "scratch" 強制包含
    public IReadOnlyList<string> ExcludeTags { get; init; } = ["latest"];
    public IReadOnlyList<string> IgnoreImages { get; init; } = [];
}
```

#### `AuditConfig` (廃止 → `rules` セクションに吸収)

online audit の 4 ルールはデフォルト `enabled: false` 。ユーザーが `rules.<rule-id>.enabled: true` で有効化すると、システムがネットワーク必要性を自動判定。専用 config 型は不要。

#### `NetworkConfig` (共通ネットワーク設定)

```csharp
public sealed record NetworkConfig
{
    public NetworkErrorMode OnError { get; init; } = NetworkErrorMode.Skip;
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxConcurrency { get; init; } = 4;
    public GitHubNetworkConfig GitHub { get; init; } = new();
}

public enum NetworkErrorMode { Skip, Fail }

public sealed record GitHubNetworkConfig
{
    // 探索順序 SEITON_GITHUB_TOKEN → GITHUB_TOKEN はコード内定数として保持。
    public string? GhesApiUrl { get; init; } = null;
    public bool GhesFallback { get; init; } = false;
}
```

### 4.2 変更対象ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `src/Seiton.Core/Linting/LintConfig.cs` | 型定義の全面書き換え |
| `src/Seiton.Core/Linting/LintConfigLibrary.cs` | YAML パーサーを新キー構造に対応 |
| `src/Seiton.Core/Linting/LintEngine.cs` | 新 config 型からルール設定を読み出す変更 |
| `src/Seiton.Core/Linting/PinRemediation/PinResolutionConfig.cs` | `FixPinningConfig` / `FixImagesConfig` + `NetworkConfig` へ分割 |
| `src/Seiton.Core/Linting/OnlineAudit/OnlineAuditConfig.cs` | `OnlineAuditConfig` 廃止。online ルールの有効化は `RuleConfig.Enabled` で制御、ネットワーク設定は `NetworkConfig` へ |
| `src/Seiton.Core/Linting/Rules/*.cs` | 各ルールの `SetConfig()` を新 config 読み出しに対応 |
| `tests/Seiton.Core.Tests/LintConfigLibraryTests.cs` | 全 YAML インラインテストを新形状に書き換え |
| `tests/Seiton.Core.Tests/PinResolutionConfigTests.cs` | 新型に合わせてテスト書き換え |
| `tests/Seiton.Core.Tests/OnlineAuditConfigTests.cs` | 新型に合わせてテスト書き換え |

---

## 5. 後方互換性戦略

### 5.1 マイグレーション期間

config は外部ユーザーに公開済みであるため、旧形式を即座に廃止せず一時的に両方受け入れる。

- **Phase A (導入)**: 新形式を primary としてサポート。旧形式は deprecated warning 付きで受け入れる。
- **Phase B (移行完了)**: 旧形式のサポートを削除。

### 5.2 旧→新の変換ロジック

`LintConfigLibrary.Validate()` で旧キーを検出した場合:

1. 旧キーの値を新構造に変換
2. deprecation warning を `LintConfigValidationResult.Warnings` に追加
3. 新旧両方が存在する場合は **config エラー** (二重定義は禁止)

### 5.3 旧キーの検出テーブル

| 旧キー | 新キー | 変換ルール |
|---|---|---|
| `additiveCustomization.additionalDangerousEvents` | `rules.dangerous-triggers.events.extend` | リストをそのまま移動 |
| `additiveCustomization.additionalKnownHostedLabels` | `rules.runner-label.known-hosted-labels.extend` | 同上 |
| `additiveCustomization.additionalPublicRegistries` | `rules.credentials.public-registries.extend` | 同上 |
| `additiveCustomization.additionalUntrustedTriggers` | `rules.cache-poisoning.untrusted-triggers.extend` + `rules.self-hosted-runner.untrusted-triggers.extend` | 両方に展開 |
| `additiveCustomization.additionalOutputCommands` | `rules.unredacted-secrets.output-commands.extend` | リストをそのまま移動 |
| `additiveCustomization.forbiddenUsesAllowPatterns` | `rules.forbidden-uses.allow` | 同上 |
| `additiveCustomization.forbiddenUsesDenyPatterns` | `rules.forbidden-uses.deny` | 同上 |
| `exprContext.eventTypes` | `rules.expr-undefined-var.assume-events` | リストをそのまま移動 |
| `default_job_timeout_minutes_for_fix` | `fix.defaults.job-timeout-minutes` | 値をそのまま移動 |
| `pin_resolution` | `fix.pinning` + `fix.images` + `network` | §2.4, §2.6 に従い分解 |
| `online_audit` | `rules.<online-rule-id>.enabled: true` + `network` | §2.5, §2.6 に従い分解 |
| `exclusions[].filePattern` | `exclusions[].files` | キー名変更 |
| `exclusions[].ruleIds` | `exclusions[].rules` | キー名変更 |
| `exclusions[].jobId` | `exclusions[].jobs` | スカラー → リスト化 |

---

## 6. 実行フェーズ

### Phase 1: 仕様書更新

1. `Seiton_Linter_spec.md` §5 を新 config 形状に全面改定
2. `Seiton_Linter_spec.md` §12 を `fix.pinning` / `fix.images` / `network` に改定
3. `Seiton_Linter_csharp_spec.md` の型定義・config mapping を改定
4. `Seiton_Linter_go_spec.md` の型定義・config mapping を改定

### Phase 2: C# 型定義の書き換え

1. 新 config 型を定義（`RuleConfig`, `FixConfig`, `NetworkConfig`）
2. 旧型（`RuleOption`, `ExpressionContext`, `RuleSpecificAdditiveCustomization`, `OnlineAuditConfig`）を deprecated マーク
3. `LintConfig` プロパティを新型に切り替え

### Phase 3: Config パーサー更新

1. `LintConfigLibrary` の YAML パーサーを新キー構造に対応
2. 旧キー検出 + deprecation warning + 変換ロジック実装
3. 新旧二重定義のエラー検出実装

### Phase 4: ルール適応

1. 各ルールの `SetConfig()` を新 config 読み出しに変更
2. `LintEngine` の config → ルール接続を更新
3. `PinRemediationEngine` / `OnlineAuditEngine` の config 読み出しを新型に変更

### Phase 5: テスト更新

1. `LintConfigLibraryTests` の全インライン YAML を新形状に書き換え
2. 旧形式の後方互換テスト追加（deprecation warning が出ることを検証）
3. 旧新二重定義のエラーテスト追加
4. `PinResolutionConfigTests` / `OnlineAuditConfigTests` を新型に書き換え

### Phase 6: テンプレート・ドキュメント更新

1. `LintConfigLibrary.GenerateTemplateYaml()` の出力を新形状に更新
2. README / ユーザー向けドキュメントの config 例を更新

---

## 7. 検証基準

各フェーズ完了時に以下を確認する:

- [ ] `dotnet build` が通る
- [ ] `dotnet test` が全テスト pass
- [ ] 新形式の config で lint 実行が正常動作する
- [ ] 旧形式の config で deprecation warning 付きで lint 実行が正常動作する（Phase 3 以降）
- [ ] 新旧二重定義で config エラーが出る（Phase 3 以降）
- [ ] config テンプレート生成が新形式を出力する（Phase 6）

---

## 8. リスクと注意

### 8.1 `rules` セクションの型多様性

`rules.<rule-id>` の値が `enabled` / `severity` の共通フィールドに加え、ルール固有フィールド（`events.extend`, `deny` 等）を持つ。YAML パーサーでルール ID ごとに許可されるフィールドを検証する必要がある。

**対策:** ルール ID → 許可フィールドのマッピングテーブルを `RuleCatalog` に持たせ、バリデーション時に参照する。未知のフィールドは config エラーにする。

### 8.2 `network` セクションの粒度

`fix.pinning` と online ルールが別々にネットワークを使うが、`network` 設定は共通。将来的に粒度を分けたくなる可能性がある。

**対策:** 現時点では `network` 共通で十分。将来必要になった場合は `network.pinning` / `network.online-rules` のようにサブキーを追加するオプションを残す。

### 8.3 `exclusions[].jobs` のリスト化

旧 `jobId` はスカラーだったが、新 `jobs` はリスト。既存のスカラー値は単要素リストとして変換する。

### 8.4 `additionalUntrustedTriggers` の展開先

旧 `additionalUntrustedTriggers` は `cache-poisoning` と `self-hosted-runner` の両方に影響する。新形式では各ルールに独立して `untrusted-triggers.extend` を持たせるが、ユーザーが両方に同じ値を設定したい場合は重複記述が必要になる。

**対策:** 許容する。ユーザーが「どのルールに効くか」を明確に制御できるメリットが、重複記述のデメリットを上回る。将来の YAML anchor (`&`/`*`) で緩和可能。

---

## 9. 今後の検討事項

`Seiton_config_review.md` §6 で指摘された未決事項:

1. **`fix.pinning` と `fix.images` の粒度**: 将来的に `rules.unpinned-uses` / `rules.unpinned-image` 配下にさらに寄せる余地がある。ただし fix 設定はルール検出ではなく修正適用時の設定であるため、`fix` 配下が自然。
2. **online ルールの将来拡張**: online ルールが増えた場合も、各ルールが「network 必要」という属性を `RuleCatalog` で持つため、`rules` セクションの統一的なインターフェースで自然に対応できる。
3. **`extend` vs 最終集合宣言**: `extend` は built-in との関係が明示的で安全。最終集合宣言は built-in の暗黙除外リスクがあるため、現時点では `extend` を採用。
