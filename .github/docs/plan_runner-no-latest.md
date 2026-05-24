# Plan: runner-no-latest fix-mapping Config

## Summary

`runner-no-latest` ルールに `fix-mapping` config を追加し、`-latest` ラベルを任意のバージョン固定ラベルに auto-fix できるようにする。

## 現状

- `RunnerNoLatestRule` は `ubuntu-latest`, `windows-latest`, `macos-latest` の 3 ラベルをハードコード検出し、警告を出す。
- fix 機能なし。rule-specific config なし。

## 設計

### Config Shape

```yaml
rules:
  runner-no-latest:
    fix-mapping:
      ubuntu-latest: ubuntu-24.04
      windows-latest: windows-2025
      macos-latest: macos-15
```

### セマンティクス

| ケース | 検出 (warn) | Fix |
|---|---|---|
| built-in ラベル、mapping あり | Yes | Yes (mapping の値で置換) |
| built-in ラベル、mapping なし | Yes | No |
| mapping にのみ存在するラベル (self-hosted 等) | Yes | Yes (mapping の値で置換) |
| mapping にないラベル、built-in でもない | No | No |

- **部分 mapping**: `fix-mapping` に `ubuntu-latest` だけ書いた場合、`ubuntu-latest` のみ fix 付き。`windows-latest` / `macos-latest` は従来どおり warn のみ。
- **検出拡張**: `fix-mapping` に書いたキーは built-in でなくても検出対象に追加される。
- **空文字/null 禁止**: キー・値ともに空文字列 (`""`, `''`)、空白のみ、`null` (YAML の `key:` 値なし) は config バリデーションエラーとする。

### Config バリデーション

| 条件 | 結果 |
|---|---|
| キーが空文字/空白のみ | config エラー (load 時に diagnostic) |
| 値が空文字/空白のみ | config エラー (load 時に diagnostic) |
| 値が `null` | config エラー (load 時に diagnostic) |
| 重複キー | YAML の仕様上、後勝ち。特別処理しない |

### 大文字小文字

- キーの matching は ASCII case-insensitive (既存の runner-label ルールと同様)。

## 課題点

1. **LintConfig の拡張**: 現在 `RuleConfig` に `Dictionary<string, string>` 型のフィールドがない。`fix-mapping` 用に新しいプロパティまたは汎用 dictionary 型を追加する必要がある。
2. **Config デシリアライズ**: `fix-mapping` の YAML mapping を `Dictionary<string, string>` にデシリアライズする。null 値・空文字ともに拒否するバリデーションが必要。
3. **DiagnosticFix の生成**: 現在 `RunnerNoLatestRule` は `DiagnosticFix` を返していない。mapping から fix を生成するロジックを追加する。
4. **検出対象の拡張**: `IsLatestHostedRunnerLabel()` を config-aware にする。built-in 3 ラベル + mapping キーの和集合を検出対象とする。
5. **テスト**: mapping あり/なし/部分指定/null 値/空文字拒否/case-insensitive のすべてをカバーする。
6. **ドキュメント**: `docs/configuration.md` と `docs/rules.md` の更新。

## 実装案

### RuleConfig への追加

```csharp
// LintConfig.cs - RuleConfig に追加
public IReadOnlyDictionary<string, string>? FixMapping { get; init; }
```

### Config デシリアライズ (ConfigLoader)

- `fix-mapping` キーを検出したら `Dictionary<string, string>` としてパース。
- 各エントリのキー: `string.IsNullOrWhiteSpace()` チェック → エラー。
- 各エントリの値: `null` または `string.IsNullOrWhiteSpace()` チェック → エラー。

### RunnerNoLatestRule の変更

```csharp
// 検出対象の判定
private bool IsTargetLabel(string label)
{
    // built-in OR fix-mapping に存在
    return IsBuiltInLatestLabel(label) || _fixMapping.ContainsKey(label);
}

// Fix の生成
private DiagnosticFix? GetFix(string label)
{
    if (_fixMapping.TryGetValue(label, out var pinned))
        return new DiagnosticFix(...); // label → pinned に置換
    return null;
}
```

### Fix 出力

- `DiagnosticFix` は既存の fix インフラ (`--fix` フラグ) で適用される。
- replacement text = mapping の値。位置 = AST 上の runs-on 値ノードの span。

## 実装フェーズ

### Phase 1: Config 基盤 (Priority: High)

1. `RuleConfig` に `FixMapping` プロパティを追加。
2. `ConfigLoader` で `fix-mapping` キーのデシリアライズ + バリデーション (空文字拒否)。
3. バリデーションエラー時の diagnostic メッセージ。
4. テスト: valid config / invalid config (空キー、空値、null値) / 未指定。

### Phase 2: 検出拡張 (Priority: High)

1. `RunnerNoLatestRule` に config (`FixMapping`) を注入。
2. 検出対象を built-in + mapping キーの和集合に拡張。
3. case-insensitive matching の適用。
4. テスト: built-in のみ検出 / mapping 追加ラベルの検出 / case 違い。

### Phase 3: Fix 生成 (Priority: High)

1. mapping に値があるラベルに対して `DiagnosticFix` を返す。
2. 部分 mapping (一部のみ指定) で、指定ありのみ fix 付き、なしは warn のみ。
3. 部分 mapping (一部のみ指定) で、指定ありのみ fix 付き、なしは warn のみ。
4. テスト: fix あり / fix なし (mapping 未指定) / 部分 mapping。

### Phase 4: ドキュメント (Priority: Medium)

1. `docs/configuration.md` に `fix-mapping` の説明を追加。
2. `docs/rules.md` の `runner-no-latest` セクションを更新。
3. 使用例を記載。

### Phase 5: 将来拡張 (Priority: Low, 実装不要)

- `fix-mapping` のデフォルト値を Seiton.Update のデータソースから生成する案。ただし GitHub の latest 指し先変更時にリリースが必要になるため、**ユーザー指定のみ** を当面の方針とする。
- `fix-mapping.extend` パターン (built-in default + ユーザー追加) は現時点では不要。

---

## 実装結果

### 完了日

実装完了。Phase 1–4 すべて完了。

### 変更ファイル

| ファイル | 変更内容 |
|---|---|
| `src/Seiton.Core/Linting/LintConfig.cs` | `RuleConfig` に `FixMapping` プロパティ追加 |
| `src/Seiton.Core/Linting/RuleKeyFlags.cs` | `FixMapping = 1 << 11` フラグ追加 |
| `src/Seiton.Core/Linting/RuleCatalog.cs` | `RunnerNoLatest` に `FixMapping` フラグ許可 |
| `src/Seiton.Core/Linting/LintConfigYamlParser.cs` | `fix-mapping` パース + バリデーション |
| `src/Seiton.Core/Linting/Rules/RunnerNoLatestRule.cs` | 検出拡張 + fix 生成 |
| `tests/Seiton.Core.Tests/RunnerNoLatestFixMappingTests.cs` | 23 テスト (config/detection/fix) |
| `docs/rules.md` | ルール説明・fix-mapping 設定例追加 |
| `docs/configuration.md` | Rule-Specific Options テーブル・例追加 |

### テスト結果

- 新規テスト: 26/26 pass
- 既存テスト: 1666/1666 pass (regression なし)

### レビューで追加修正した点

- `RunnerNoLatestRule` の custom `fix-mapping` 検出で per-label `Encoding.UTF8.GetString(...)` を行っていたため、`SetConfig()` 時に UTF-8 byte 配列を前計算し、実行時は ASCII case-insensitive byte 比較のみで判定するように修正。
- `fix-mapping` の key/value を parser が黙って `Trim()` していたため、設定値をそのまま使う直感とズレていた。空/空白-only の拒否は維持しつつ、非空の値は verbatim で保持するよう修正。
- `.github/docs/Seiton_config_spec.md`, `.github/docs/Seiton_Linter_spec.md`, `README.md` の記述を実装に同期。
- `AsciiEqualsIgnoreCase` が `left` 側のみ ASCII lower していたバグを修正。Config キーが大文字 (e.g. `My-Org-Runner-Latest`) で workflow ラベルが小文字の場合にマッチしない問題を解消。両側を lower するよう修正し、テスト 2 件追加。

### ベンチマーク結果

| Size | FixEnabled | Baseline Mean | Post Mean | Allocated (変化なし) |
|---|---|---|---|---|
| Small | False | 55.87 us | 69.24 us | 8.7 KB |
| Small | True | 60.98 us | 70.32 us | 10.15 KB |
| Medium | False | 1,182.57 us | 1,528.65 us | 68.89 KB |
| Medium | True | 1,712.06 us | 2,056.63 us | 82.25 KB |
| Large | False | 19,072.36 us | 21,974.13 us | 327.41 KB |
| Large | True | 30,005.30 us | 34,236.25 us | 382.25 KB |

**アロケーション変化: ゼロ** (全シナリオで同一)。時間差は ShortRun (3 iteration) のノイズ範囲内。
