# service image pinning 調査・対応計画

## 概要

`seiton --fix --enable-image-network` 実行時に `jobs.<job_id>.services.<service_id>.image` がピン留めされない件を調査した。
結論として、**service 固有のバグではなく**、タグ省略（暗黙 `latest`）と `fix.images.exclude-tags` デフォルトの組み合わせにより、**container / docker:// を含むすべての image 位置で同様にピン留めがスキップされる**。

## 再現条件

報告されたワークフロー断片:

```yaml
jobs:
  build-dotnet:
    services:
      redis:
        image: redis
```

### 再現手順

```bash
seiton --fix --dry-run --enable-image-network path/to/workflow.yml
```

### 観測結果

| 入力 | `unpinned-image` 診断 | ネットワークピン留め |
|------|----------------------|---------------------|
| `services.redis.image: redis` | 発生する | **スキップ（fix なし）** |
| `services.redis.image: redis:7` | 発生する | **成功**（`redis:7@sha256:...` に置換） |
| `container.image: redis` | 発生する | **スキップ（fix なし）** |
| `steps[].uses: docker://...:latest` | 発生する | **スキップ（fix なし）** |

`redis:7` の service image はネットワーク解決・テキスト編集とも正常。service パースや `PinFixFormatter` のオフセット解決に service 固有の欠陥は見つからなかった。

## 根本原因

### 1. タグ省略は暗黙 `latest` として解釈される

`OciImageDigestResolver.TryParseImageReference` は、コロンでタグが明示されない参照（例: `redis`）を **`latest` タグ付き**として扱う。

- `redis` → `registry-1.docker.io/library/redis:latest`
- `ghcr.io/org/app` → `ghcr.io/org/app:latest`

該当コード: `src/Seiton.Core/Linting/PinRemediation/OciImageDigestResolver.cs`（`reference = "latest"` 分岐）

### 2. デフォルト設定で `latest` タグのピン留めは意図的にスキップされる

`fix.images.exclude-tags` のデフォルトは `["latest"]`（frizbee 互換）。
`ShouldSkip` が `true` を返すと `ResolveAsync` は `null` を返し、fix は生成されない。

仕様根拠:

- `.github/docs/Seiton_Linter_spec.md` §12.3.6 — `latest` はデフォルト除外。「`latest` をピン留めしても意味が薄い（すぐ drift する）」
- テスト: `OciImageDigestResolverTests.ResolveAsync_ReturnsNull_ForScratch_AndLatestDefaults`

### 3. lint と fix の挙動ギャップ（UX 問題）

| 層 | `image: redis`（暗黙 latest）の扱い |
|----|--------------------------------------|
| `unpinned-image` ルール | **警告する**（digest 未指定を検出） |
| `OciImageDigestResolver` | **ピン留めしない**（`exclude-tags: latest`） |
| 診断へのフィードバック | **なし**（action pinning の `SkipReason` 相当が image には未実装） |

action pinning（`unpinned-uses`）は `ActionShaResolution.Skipped(reason)` により `help:` にスキップ理由を付与するが、image pinning は `IImageDigestResolver` が `string?` のみ返すため、ユーザーには「ネットワークを有効にしたのに直らない」ように見える。

### 4. service ルール自体は正常

`UnpinnedImageRule.VisitJobPre` は `job.services.*.image` を検査している。
回帰テスト `RuleInterfaceTests.UnpinnedImageRule` にも service container ケースがある。

ピン留め E2E テスト（`PinRemediationTests`）は `docker://` のみで、**service image の統合テストが不足**している（暗黙 latest スキップのテストも不足）。

## 影響範囲

- `job.services.<id>.image`（報告対象）
- `job.container.image`
- `steps[].uses: docker://...`（`:latest` またはタグ省略）

いずれも同一の `OciImageDigestResolver` + `PinRemediationEngine` 経路。

## 対応方針

### 方針の前提

`latest` の自動ピン留めをデフォルトで有効化するのは、仕様上・セマンティクス上ともに推奨しない（frizbee / dockerfile-pin 系ツールと同様）。
**問題の本質は「スキップがサイレントであること」と「lint が warn するのに fix が動かないことの説明不足」**。

### Phase 1: 可観測性の改善（優先・低リスク）

**目的:** ユーザーが「なぜ直らないか」を CLI 出力から判断できるようにする。

1. **`IImageDigestResolver` にスキップ理由を返す API を追加**
   - `ActionShaResolution` と同様に `ImageDigestResolution`（`Digest`, `SkipReason`）を導入
   - `OciImageDigestResolver.ShouldSkip` 時に理由を返す  
     例: `pinning skipped: tag 'latest' matches fix.images.exclude-tags`
2. **`PinRemediationEngine.RemediateUnpinnedImageAsync` で `help:` にスキップ理由を付与**
   - `RemediateUnpinnedUsesAsync` と同パターン
3. **テスト追加**
   - service image + 暗黙 latest → `SkippedCount` 増加、`Help` に exclude-tags 理由
   - service image + 明示タグ（`redis:7`）→ fix 適用・re-lint で `unpinned-image` 解消

### Phase 2: ドキュメント整備

1. `docs/rules.md` の `unpinned-image` に追記:
   - タグ省略 = 暗黙 `latest`
   - デフォルトでは `latest` は自動ピン留め対象外
   - 回避: 明示タグ（`redis:7`）を使う、または `fix.images.exclude-tags: []` を設定
2. `references/fix-mode.md` / `docs/configuration.md` に同内容を反映
3. ネットワーク fix ヒント（`WriteNetworkFixHint`）で `latest` スキップの可能性に言及

### Phase 3: lint / fix 整合性の検討（要判断）

以下は仕様変更に当たるため、Phase 1 デプロイ後にフィードバックを見て決定する。

| 案 | 内容 | メリット | デメリット |
|----|------|----------|------------|
| A（現状維持 + Phase 1） | warn は継続、fix のみスキップ、理由を表示 | 破壊的変更なし | warn が残る |
| B | `exclude-tags` に含まれるタグは `unpinned-image` でも warn しない | warn/fix の矛盾解消 | 暗黙 latest を見逃す可能性 |
| C | 暗黙 latest のみ warn 継続、明示 `:latest` は info 化 | よくあるパターンに限定 | ルール複雑化 |

**推奨:** 当面は **案 A（Phase 1 のみ）**。`image: redis` は実質 `latest` 運用であり、warn 自体は妥当。

### ワークアラウンド（実装前）

`fix.images.exclude-tags` から `latest` を外す:

```yaml
fix:
  images:
    enable-network: true
    exclude-tags: []   # latest もピン留め対象にする（drift リスクあり）
```

または、明示タグを使う（推奨）:

```yaml
services:
  redis:
    image: redis:7.4   # ピン留め後: redis:7.4@sha256:...
```

## 実装タスク一覧

| # | タスク | 優先度 |
|---|--------|--------|
| 1 | `ImageDigestResolution` 型と `IImageDigestResolver` 拡張 | 高 |
| 2 | `OciImageDigestResolver` スキップ理由の具体化 | 高 |
| 3 | `PinRemediationEngine` で image skip reason を `Help` に反映 | 高 |
| 4 | service image 統合テスト（`redis` スキップ / `redis:7` 成功） | 高 |
| 5 | ドキュメント更新（rules / configuration / fix-mode） | 中 |
| 6 | Phase 3 方針決定（lint 整合性） | 低 |

## 関連ファイル

| ファイル | 役割 |
|----------|------|
| `src/Seiton.Core/Linting/Rules/UnpinnedImageRule.cs` | 診断生成（service 含む） |
| `src/Seiton.Core/Linting/PinRemediation/OciImageDigestResolver.cs` | OCI 解決・exclude-tags |
| `src/Seiton.Core/Linting/PinRemediation/PinRemediationEngine.cs` | fix 付与 |
| `src/Seiton.Core/Linting/PinRemediation/PinFixFormatter.cs` | テキスト編集 |
| `src/Seiton.Core/Linting/LintConfig.cs` | `FixImagesConfig.ExcludeTags` デフォルト |
| `tests/Seiton.Core.Tests/OciImageDigestResolverTests.cs` | latest スキップの単体テスト |
| `.github/docs/Seiton_Linter_spec.md` §12.3.6 | 仕様（latest デフォルト除外） |

## 調査日

2026-06-10
