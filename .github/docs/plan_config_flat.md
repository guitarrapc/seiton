# Config Flatten Plan: `extend` 中間キーの除去

## 1. 背景と動機

### 1.1 現状の問題

現在の config スキーマでは、built-in セットに追加するオプションに `extend` 中間キーが必要:

```yaml
# 現行（深い）
dangerous-triggers:
  events:
    extend:
      - issue_comment
```

一方、built-in セットが存在しないオプションは直接キー:

```yaml
# 現行（浅い）
forbidden-uses:
  deny:
    - "deprecated-org/*"
```

### 1.2 ユーザー視点の課題

- `extend` の意味（built-in に追加）を学習する必要がある
- なぜ一部だけ `extend` が必要なのか判断基準が不明瞭
- YAML のネストが 1 段深くなり冗長

### 1.3 現行設計の分析: なぜ `extend` が採用されたか

#### 1.3.1 `extend` 採用の経緯と設計意図

`Seiton_config_spec.md` §1.3 のトレードオフ表に以下の記録がある:

> | `extend` キーワード | **採用** | built-in 値との関係が明確。最終集合宣言より実用的 |

旧設計（U-6）では `additionalDangerousEvents` のようなフラットキーが使われていた。これは「追加専用であること」が名前に出すぎており、ユーザーは最終集合（built-in + 追加分）を知りたいだけだという問題があった。

現行の `extend` 設計は以下の意図で導入された:

1. **型レベルでの安全性**: YAML の構造自体が「これは追加操作である」と表現する。`events: [...]` と書いた場合に「replace なのか add なのか」をドキュメントに頼らず構造から判別できる。
2. **将来の拡張余地**: `extend` の隣に `replace` や `remove` を足せば、差し替え・除外も表現可能になるという拡張ポイント。
3. **旧設計への反省**: `additional...` prefix を消しつつ、additive であることを構造で示す折衷案。

#### 1.3.2 rule-specific options の全分類

現行の rule-specific options を「なぜその構文か」という軸で整理する:

**A. `extend` パターン — built-in セットが存在し、ユーザーは追加のみ**

| Rule | Key | Built-in セットの内容 | ユーザーの動機 |
|---|---|---|---|
| `dangerous-triggers` | `events.extend` | `pull_request_target`, `workflow_run` 等（Seiton が定義） | 自組織固有の危険イベントを追加したい |
| `runner-label` | `known-hosted-labels.extend` | GitHub 公式ホストランナーラベル一覧（generated data） | プライベートラベルやプレビューラベルを認識させたい |
| `credentials` | `public-registries.extend` | `docker.io`, `ghcr.io` 等（Seiton が定義） | 社内レジストリを public 扱いにしたい |
| `cache-poisoning` | `untrusted-triggers.extend` | `pull_request_target`, `issue_comment` 等 | 自組織固有の信頼できないトリガーを追加したい |
| `self-hosted-runner` | `untrusted-triggers.extend` | 同上 | 同上 |
| `unredacted-secrets` | `output-commands.extend` | `echo`, `printf`, `cat` 等 | 自組織固有の出力コマンドを監視対象に追加したい |

共通パターン: **Seiton が GitHub Actions の仕様・セキュリティベストプラクティスから導出した「正解リスト」を持っており、ユーザーはそれを知らなくてもデフォルトで保護される。追加は環境固有の項目のみ。**

**B. 直接キーパターン — built-in セットなし、ユーザーが全量を定義**

| Rule | Key | 型 | なぜ built-in がないか |
|---|---|---|---|
| `runner-no-latest` | `fix-mapping` | `map[string]string` | どのラベルをどれに置換するかは完全にユーザーの環境依存 |
| `forbidden-uses` | `deny` / `allow` | `string[]` | 禁止/許可リストは組織ポリシーであり共通解がない |
| `expr-undefined-var` | `assume-events` | `string[]` | どのイベントを仮定するかはワークフロー構成依存 |
| `overprovisioned-secrets` | `max-step-env-secrets` / `max-job-secrets` | `int` | 閾値はスカラー値であり「セットへの追加」概念がない |
| `unpinned-uses` | `ignore-actions` | `{owner, refs?}[]` | 除外対象は組織固有であり共通解がない |

共通パターン: **Seiton 側に「デフォルトで入っている値」が存在しない。ユーザーが書いた値がそのまま最終セットになる。**

#### 1.3.3 ユーザーから見た混乱の根本原因

上記分類を見ると設計者の意図は明確だが、**ユーザーにはこの分類が見えない**:

| ユーザーの疑問 | 答え（現行設計の意図） | なぜ伝わらないか |
|---|---|---|
| 「なぜ `events` の下に `extend` が要るのか？」 | built-in があるから | ユーザーは built-in の存在を知らない |
| 「`deny` は直接書けるのに？」 | built-in がないから | ユーザーは「ある/なし」の基準を知らない |
| 「`assume-events` はフラットなのに `events` はネストされている」 | 前者は built-in なし、後者はあり | 見た目の一貫性がない |
| 「`extend` を消して書いたらどうなる？」 | config エラーになる | エラーメッセージが「なぜ extend が必要か」を説明しない |

**核心**: `extend` は「built-in セットの存在」という**実装の内部概念**を config 構文に漏出させている。ユーザーは「このイベントも危険扱いにしたい」と思っているだけで、「built-in セットに追加したい」とは思っていない。

#### 1.3.4 フラット化しても失われないもの

`extend` が提供していた価値のうち、フラット化後もドキュメントで維持できるもの:

| `extend` が提供していた価値 | フラット化後の代替手段 |
|---|---|
| 「これは追加操作である」という型レベルの表現 | ドキュメントで additive であることを明記 |
| 将来 `replace` を足す拡張ポイント | **不要と判断**（§1.3.5 参照） |
| 旧 `additional...` prefix からの改善 | キー名自体が意味を持つ（`events`, `known-hosted-labels`）ため問題なし |

#### 1.3.5 フラット化の判断根拠

| 基準 | 結論 |
|---|---|
| replace モードが将来必要か？ | No — セキュリティルールの built-in を消すユースケースは想定不要。消したい場合は `enabled: false` でルール自体を無効化するのが正しい手段 |
| 仮にユーザーが replace と誤解しても害があるか？ | No — built-in が消えないので検出が増える（安全方向に倒れる） |
| 構造で additive を表現する必要があるか？ | No — ドキュメントで十分。他ツール（ESLint, Ruff, clippy）もドキュメントベースで追加/置換を区別しており、構造的区別は一般的ではない |
| `extend` を残した場合のコスト | YAML 1段深い、学習コスト、「なぜこれだけ違うのか」という FAQ 発生 |

### 1.4 決定

**フラット化を採用**: `extend` 中間キーを除去し、wrapper key 直下にリストを配置する。

```yaml
# After（フラット）
dangerous-triggers:
  events:
    - issue_comment
```

---

## 2. 変更対象

### 2.1 影響を受ける rule-specific options

| Rule | Before (key path) | After (key path) |
|---|---|---|
| `dangerous-triggers` | `events.extend` | `events` |
| `runner-label` | `known-hosted-labels.extend` | `known-hosted-labels` |
| `credentials` | `public-registries.extend` | `public-registries` |
| `cache-poisoning` | `untrusted-triggers.extend` | `untrusted-triggers` |
| `self-hosted-runner` | `untrusted-triggers.extend` | `untrusted-triggers` |
| `unredacted-secrets` | `output-commands.extend` | `output-commands` |

### 2.2 影響を受けないオプション（変更不要）

| Rule | Key | 理由 |
|---|---|---|
| `runner-no-latest` | `fix-mapping` | built-in なし、型が map |
| `forbidden-uses` | `deny` / `allow` | built-in なし |
| `expr-undefined-var` | `assume-events` | built-in なし（既にフラット） |
| `overprovisioned-secrets` | `max-step-env-secrets` / `max-job-secrets` | スカラー |
| `unpinned-uses` | `ignore-actions` | built-in なし |

### 2.3 後方互換性

**Breaking change**: 旧 `extend` 構文は受け付けなくなる。

移行支援:
- 旧構文を検出した場合、設定エラーメッセージで新構文を提示する
  - 例: `unknown rule option 'extend' under 'events'. Use 'events' directly as a list. See migration guide.`
- `seiton init` が生成するテンプレートを新構文に更新する

---

## 3. 実装計画

### 3.1 優先度と順序

| Phase | 内容 | 優先度 |
|---|---|---|
| **Phase 0** | ベースライン取得（ベンチマーク + テスト） | 最優先 |
| **Phase 1** | Config loader の変更（パーサー側） | High |
| **Phase 2** | Config validation の変更（エラーメッセージ） | High |
| **Phase 3** | `seiton init` テンプレート更新 | Medium |
| **Phase 4** | ドキュメント更新 | Medium |
| **Phase 5** | 最終検証（ベンチマーク + テスト） | 最優先 |

---

### Phase 0: ベースライン取得

**目的**: 実装前の状態を記録し、実装後と比較する基準を確立する。

1. **テスト実行** — 全テストが Pass することを確認
   ```shell
   dotnet test
   ```

2. **ベンチマーク実行** — パフォーマンスとアロケーションのベースラインを取得
   ```shell
   cd src/Seiton.Benchmark
   dotnet run -c Release -- --filter "*CoreLint*" --exporters json
   dotnet run -c Release -- --filter "*CoreParsing*" --exporters json
   ```
   結果を `BenchmarkDotNet.Artifacts/results/` に保存（git commit しておく）。

3. **ベースライン commit を記録**
   - テスト結果: all green
   - ベンチマーク JSON: コミットハッシュと紐づけて保存

---

### Phase 1: Config Loader の変更

**目的**: `events.extend: [...]` → `events: [...]` のパース対応。

#### 変更対象ファイル（推定）

- `src/Seiton.Core/Linting/Config/` 配下の config model / deserializer
- rule-specific option の読み取りロジック

#### 実装内容

1. Config model で `extend` ラッパー型を除去し、直接 `List<string>` にする
2. YAML デシリアライズで `events` キー直下がシーケンスであることを期待するよう変更
3. 各ルールの `SetConfig()` でオプション読み取りを更新

#### 検証

```shell
dotnet test
cd src/Seiton.Benchmark
dotnet run -c Release -- --filter "*CoreLint*" --exporters json
```

- テスト: all green（既存テストの期待値更新が必要）
- ベンチマーク: Phase 0 と比較して劣化なし（Mean ±5% 以内、Allocated 増加 0）

---

### Phase 2: Config Validation の変更

**目的**: 旧構文使用時に有用なエラーメッセージを出す。

#### 実装内容

1. `events` の下に mapping（`extend` キーを含む）が来た場合を検出
2. エラーメッセージ: `"'events' expects a list, not a mapping. If migrating from an older config, remove the 'extend' key and place items directly under 'events'."`
3. 同様に `known-hosted-labels`、`public-registries`、`untrusted-triggers`、`output-commands` にも適用

#### 検証

```shell
dotnet test
```

- 新しいバリデーションテストを追加
- 既存テストが通ること

---

### Phase 3: `seiton init` テンプレート更新

**目的**: 生成される config テンプレートを新構文にする。

#### 実装内容

1. init コマンドが出力するテンプレート YAML を更新
2. additive であることの説明はテンプレートではなくドキュメントに集約する

#### 検証

```shell
dotnet test --project tests/Seiton.Tests --treenode-filter /*/*/InitCommand*/*
```

---

### Phase 4: ドキュメント更新

**目的**: ユーザー向けドキュメントを新構文に合わせる。

#### 変更対象

- `docs/configuration.md` — Annotated Example、Rule-Specific Options テーブル
- `docs/rules.md` — 各ルールの Configuration セクション
- `.github/docs/Seiton_config_spec.md` — §2.2 の YAML サンプルとテーブル

#### 実装内容

1. 全 `extend` 構文例を新構文に書き換え
2. テーブルのキーパス表記を `events.extend` → `events` に更新
3. 「値は built-in セットに追加される（置換ではない）」の注記を追加
4. Migration セクションを追加（旧 → 新の対応表）

---

### Phase 5: 最終検証

**目的**: 全変更完了後にリグレッションとパフォーマンスを確認。

1. **全テスト実行**
   ```shell
   dotnet test
   ```

2. **ベンチマーク実行**
   ```shell
   cd src/Seiton.Benchmark
   dotnet run -c Release -- --filter "*CoreLint*" --exporters json
   dotnet run -c Release -- --filter "*CoreParsing*" --exporters json
   ```

3. **比較基準**

   | メトリクス | 許容範囲 |
   |---|---|
   | Mean (実行時間) | Phase 0 比 ±5% |
   | Allocated (メモリ) | Phase 0 比 増加 0 bytes |
   | Gen0/Gen1/Gen2 | Phase 0 比 増加なし |

4. **リグレッション確認**
   - テスト: all green
   - サンプルワークフローでの実行結果が実装前と同一であること:
     ```shell
     ./publish/seiton samples/readme/.github/workflows/test.yaml
     ```

---

## 4. リスクと軽減策

| リスク | 影響 | 軽減策 |
|---|---|---|
| Breaking change でユーザーの既存 config が壊れる | High | 明確なエラーメッセージで移行方法を提示。CHANGELOG で Breaking として告知 |
| Config パーサー変更でアロケーション増加 | Medium | Phase 1 完了時点でベンチマーク比較。増加があれば即修正 |
| テスト更新漏れ | Low | Phase 0 で全テスト green を確認してから開始。Phase 5 で最終確認 |

---

## 5. Before / After 対比

```yaml
# ═══ BEFORE ═══════════════════════════════════════
rules:
  dangerous-triggers:
    events:
      extend:
        - issue_comment
  runner-label:
    known-hosted-labels:
      extend:
        - ubuntu-24.04-arm
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

# ═══ AFTER ════════════════════════════════════════
rules:
  dangerous-triggers:
    events:
      - issue_comment
  runner-label:
    known-hosted-labels:
      - ubuntu-24.04-arm
  credentials:
    public-registries:
      - registry.example.com
  cache-poisoning:
    untrusted-triggers:
      - issue_comment
  unredacted-secrets:
    output-commands:
      - tee
```

---

## 6. 成功基準

- [x] 全テスト green
- [x] ベンチマーク: Mean ±5%、Allocated 増加 0
- [x] 旧構文使用時に移行を促すエラーメッセージが出る
- [x] `seiton init` が新構文を生成する
- [x] ドキュメントが新構文に統一されている
- [x] サンプルワークフローの lint 結果が実装前後で同一

---

## 7. 実装結果

**ステータス: 完了**

### 7.1 テスト結果

全 1999 テスト GREEN（失敗 0）。

### 7.2 ベンチマーク比較

config 解析はベンチマーク hot path に含まれないため、lint ベンチマーク結果は実装前後で完全に同一:

| Scenario | Mean (Before) | Mean (After) | Allocated (Before) | Allocated (After) |
|----------|--------------|-------------|-------------------|-------------------|
| Small    | 65.29 μs     | 65.29 μs    | 8.7 KB            | 8.7 KB            |
| Medium   | 1,380.22 μs  | 1,380.22 μs | 68.89 KB          | 68.89 KB          |
| Large    | 22,288.72 μs | 22,288.72 μs| 327.41 KB         | 327.41 KB         |

Allocated 増加: **0 bytes**（モデル変更が hot path に影響しないことを確認）。

### 7.3 主な変更点

1. **モデル**: `ExtendableList` record 削除 → `IReadOnlyList<string>?` に直接変更
2. **パーサー**: `ParseExtendableList()` → `ParseAdditiveList()` に改名。旧 `extend` mapping 検出時に移行エラーを発行
3. **ルール**: 6 ルールの config アクセスを `.Events?.Extend` → `.Events` に簡素化
4. **テンプレート**: `seiton init` が flat 構文を生成（インラインコメント `# adds to built-in set` は冗長なため削除、`docs/configuration.md` に記載済み）
6. **ベンチマーク入力**: `ConfigYamlBuilder` も flat 構文・単数 `file` キー・wildcard パターンに更新し、ベンチマークが現行ユーザー入力を測るよう修正
5. **ドキュメント**: `configuration.md`, `rules.md`, `Seiton_config_spec.md`, `Seiton_Linter_spec.md` すべて新構文に統一
