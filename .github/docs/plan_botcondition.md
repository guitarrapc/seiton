# bot-conditions Rule Improvement Plan

## Problem Statement

`bot-conditions` ルールが false-positive を生成する2つのパターンが報告されている。

### FP-1: trigger-author context との AND 結合

```yaml
if: github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]' && github.repository == github.event.pull_request.head.repo.full_name
```

`github.actor` (spoofable) と `github.event.pull_request.user.login` (non-spoofable) を AND で結合しているため、spoofability のリスクは実質的に緩和されている。現在のルールはこの文脈を無視して `github.actor` の比較だけを見て警告する。

### FP-2: 否定パターン (`!=`) での bot 除外

```yaml
if: ${{ github.actor != 'dependabot[bot]' }}
```

- GitHub Docs で推奨されている標準パターン
- `push` イベントでは `github.event.pull_request.user.login` は利用不可 → `github.actor` が唯一の選択肢
- `!=` は「bot を除外する」意図であり、特権付与ではない → セキュリティリスクが質的に異なる

---

## 調査結果

### 現在の実装 (`BotConditionsRule.cs`)

```
CheckCondition → pre-filter (bot suffix/ID) → parse expression → ScanForBotConditions
```

`ScanForBotConditions` は式AST内の全 Binary ノード (`==`/`!=`) を走査し、一方が spoofable context path、他方が bot literal であれば即座に warning を発行する。

**欠落している分析**:
1. 同一式内に non-spoofable context の比較が AND で結合されているか確認しない
2. 演算子が `==` か `!=` かによる severity 区別がない

### Spoofable contexts (現在検知対象)

| Context path | 種別 |
|---|---|
| `github.actor` | actor name |
| `github.triggering_actor` | actor name |
| `github.actor_id` | actor ID |
| `github.event.pull_request.sender.login` | sender name |
| `github.event.pull_request.sender.id` | sender ID |

### Non-spoofable contexts (緩和として認識すべき)

| Context path | 種別 |
|---|---|
| `github.event.pull_request.user.login` | PR author (trigger-author) |
| `github.event.pull_request.user.id` | PR author ID |

### リスク分析: `==` vs `!=`

| 演算子 | 意図 | 攻撃シナリオ | リスク |
|---|---|---|---|
| `==` | bot にのみ特権を与える | actor を偽装 → 特権取得 | **高** |
| `!=` | bot を除外する | actor を偽装 → 除外回避 | **低** (得られるのは通常処理のみ) |

---

## 実装フェーズ

### Phase 1: AND 結合による抑制 (FP-1 解消)

**優先度: High** — 明確な false-positive であり、ユーザーの正当なセキュリティ強化パターンを誤検知している。

**変更内容**:

1. `ScanForBotConditions` で spoofable bot comparison を検出した後、**同一式 AST 内**に同じ bot リテラル値に対する non-spoofable context の `==` 比較が存在するか走査する
2. 存在すれば、当該 spoofable comparison の diagnostic を **発行しない**

**判定ロジック**:
```
spoofable comparison が検出された場合:
  if (式内に OR 演算子が存在する):
    → 抑制しない (OR の反対側の non-spoofable チェックは緩和にならない)
  同一 expression 内の全 Binary(==) ノードを走査
  if (いずれかが non-spoofable context path で、かつ同じ literal 値を持つ):
    → 抑制 (diagnostic 発行しない)
  else:
    → 従来通り warning
```

**Non-spoofable context path の定義**:
- `github.event.pull_request.user.login`
- `github.event.pull_request.user.id`

**テストケース**:
- `github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]'` → no diagnostic
- `github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'renovate[bot]'` → warning (リテラル不一致)
- `github.actor == 'dependabot[bot]'` (単体) → warning (変更なし)
- `github.actor_id == '49699333' && github.event.pull_request.user.id == '49699333'` → no diagnostic

### Phase 2: `!=` パターンの severity 変更 (FP-2 緩和)

**優先度: Medium** — false-positive 頻度が高いが、workaround (disable-next-line) は存在する。

**変更内容**:

1. `ScanForBotConditions` で演算子が `!=` の場合、severity を **info** に下げる
2. info はデフォルト出力では非表示 (verbose のみ)

**影響**:
- `github.actor != 'dependabot[bot]'` → info (通常非表示)
- `github.actor == 'dependabot[bot]'` → warning (変更なし)
- `github.triggering_actor != 'renovate[bot]'` → info

**Spec への反映**:
- `Seiton_Linter_spec.md` §5.7.2 の `bot-conditions` を `mixed` に変更: `warning (equality checks), info (inequality/exclusion checks)`

**テストケース**:
- `github.actor != 'dependabot[bot]'` → info
- `github.actor == 'dependabot[bot]'` → warning
- `github.triggering_actor != 'renovate[bot]'` → info
- 既存テストの期待値更新

### Phase 3 (将来): event type 考慮

**優先度: Low** — 実装複雑度が高く、Phase 1+2 で主要 FP は解消される。

`on: push` のみのワークフローでは `github.event.pull_request` が null であるため `github.actor` が唯一の手段。イベント情報と条件位置を紐づけて、代替手段がないケースを完全に抑制する。

Phase 1+2 の効果を計測した後に要否を判断する。

---

## Spec 更新箇所

| ファイル | 変更内容 |
|---|---|
| `.github/docs/Seiton_Linter_spec.md` §4.4 | `bot-conditions` の Required Behavior Summary に AND 結合抑制と `!=` severity 区別を追記 |
| `.github/docs/Seiton_Linter_spec.md` §5.7.2 | severity を `mixed` に変更、notes 追記 |
| `docs/rules.md` | ドキュメントに抑制条件と severity 区別を反映 |

## コード変更箇所

| ファイル | 変更内容 |
|---|---|
| `src/Seiton.Core/Linting/Rules/BotConditionsRule.cs` | Phase 1: AND 結合検知ロジック追加、Phase 2: `!=` 時 info emission |
| `tests/Seiton.Core.Tests/RuleInterfaceTests.BotConditionsRule.cs` | 新規テストケース追加 + 既存テスト期待値更新 |

---

## 実装順序

```
1. テスト追加 (red) — Phase 1 + Phase 2 の新規テストケース
2. BotConditionsRule.cs 修正 (green) — Phase 1 ロジック → Phase 2 severity
3. 既存テスト期待値更新
4. Spec 更新
5. docs/rules.md 更新
6. ベンチマーク実行 (回帰確認)
```
