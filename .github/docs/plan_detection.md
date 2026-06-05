# Input Discovery 境界の見直しプラン

## 背景

ネストした CI チェックアウト（親リポジトリ + `path:` で子リポジトリ）で `seiton` を子ディレクトリの `working-directory` から実行すると、子の `.github/workflows` に加えて**親**の `.github/actions` が lint 対象に含まれることがある。

再現例（Cysharp/Actions がワークスペース root、LogicLooper が `LogicLooper/` に checkout、`--include-actions` 有効）:

```text
../.github/actions/benchmark-runnable/action.yaml   # 親リポジトリ
.github/workflows/build-release.yaml                # 子リポジトリ（CWD）
```

## 採用方針

**案 B（探索 root = CWD 厳密）を採用・実装済み（2026-06-06）。**

親ディレクトリへの walk は廃止。自動探索は `<cwd>/.github/workflows/` と `<cwd>/.github/actions/`（`--include-actions` 時）のみ。親を lint する場合は明示 `FILES` または CWD をリポジトリ root に変更する。

案 A（単一 root + walk）は、`.github` が無い CWD からファイルシステム root までさかのぼる挙動がパストラバーサルと見なされうるため不採用。

---

## 現状の原因（修正前）

### 1. 入力探索は親ディレクトリへさかのぼっていた

`FILES` 引数なしの自動探索では、CWD から親へ向かって workflows / actions を**独立に**解決していた。

### 2. workflows と actions が別階層から取られていた

子に `workflows` のみ・親に `actions` のみ、という組み合わせで混在 lint が発生。

### 3. `--config` は探索範囲を絞らない

lint 対象の探索起点は CWD のまま。config パスとは無関係。

### 4. 参照解決と入力探索は別経路

`LocalActionOutputResolver` 等の参照追従は repository root ガード付きで維持（変更なし）。

### 5. 設定ファイル探索

config の自動探索は**引き続き親へ walk**（本変更の対象外）。入力探索のみ CWD 厳密化。

---

## 実装内容

### コード変更

| ファイル | 変更 |
|----------|------|
| `src/Seiton/Commands/InputDiscovery.cs` | `FindWorkflowsDirectory` / `FindActionsDirectory` の親 walk を削除。`GetWorkflowsDirectory` / `GetActionsDirectory` で CWD 直下のみ判定。`startDir` を `Path.GetFullPath` で正規化。verbose ログを `searching under cwd {path}` に変更。`CollectYamlFiles` を `*.yml` / `*.yaml` の2パス列挙に変更（拡張子フィルタのオーバーヘッド削減）。 |
| `src/Seiton/Commands/CheckCommand.cs` | `ShouldSuggestIncludeActions` の判定を `<cwd>/.github/actions/` の存在のみに変更（ancestor walk 廃止）。 |

### テスト

| ファイル | 内容 |
|----------|------|
| `tests/Seiton.Tests/InputDiscoveryTests.cs` | ネスト CI 再現、親 walk 非依存、CWD 両方収集、親のみ actions の hint 非表示 |
| `tests/Seiton.Tests/VerbosePhase1Tests.cs` | verbose discovery ログ文言の更新 |

全テスト 2474 passed / 1 skipped（2026-06-06 ローカル）。

### 仕様・ドキュメント

- `Seiton_CLI_spec.md` §5 Input Discovery を案 B に更新（lessons learned 追記）
- `Seiton_CLI_csharp_spec.md` / `Seiton_CLI_go_spec.md` 同期
- `Seiton_Linter_csharp_spec.md`（`--include-actions` hint 条件）
- `docs/usage.md` / `docs/configuration.md`
- `src/Seiton/Skills/SKILL.md` / `references/configuration.md`
- `.claude/skills/seiton/SKILL.md`

### ベンチマーク

新規: `src/Seiton.Benchmark/InputDiscoveryBenchmark.cs`

環境: Windows 11, Ryzen 9 7950X3D, .NET 10.0.8, BenchmarkDotNet ShortRun

| ベンチマーク | 修正前 Mean | 修正後 Mean | 変化 | 修正前 Alloc | 修正後 Alloc | 変化 |
|-------------|------------|------------|------|-------------|-------------|------|
| `ResolveFiles (cwd, workflows only)` | 126.1 µs | 17.5 µs | **−86%** | 2.25 KB | 280 B | **−88%** |
| `ResolveFiles (nested cwd, include-actions)` | 236.9 µs | 162.4 µs | **−31%** | 15.13 KB | 12.6 KB | **−17%** |

**性能向上の理由**

1. **親ディレクトリ walk の削除** — `Directory.Exists` と `Directory.GetParent` の繰り返しが不要になった。特に CWD に `.github/workflows` が無いケースで、以前は祖先チェーン全体を走査していた。
2. **列挙の絞り込み** — `*.*` + 拡張子判定から `*.yml` / `*.yaml` の直接列挙へ変更し、不要なファイル走査を削減。
3. **ネスト CI シナリオ** — 親 `.github/actions` の列挙がなくなり、32 件の workflow 列挙のみに（alloc も微減）。

性能低下は見られず。改善策の記載は不要。

---

## ユーザー向け API の振り返り

| 観点 | 評価 |
|------|------|
| 直感性 | `seiton` = 「今いるディレクトリの `.github/` を lint」は GitHub Actions の `working-directory` と一致し、CI 利用者にとって予測可能。 |
| ネスト CI | `working-directory: LogicLooper` で子のみ lint — 今回の事故パターンを構造的に防止。 |
| モノレポ subdir | `cd packages/foo && seiton` では親の workflows は見つからない。**明示パス**（`seiton ../../.github/workflows`）または repo root で実行が必要。breaking だがセキュリティと一貫性のトレードオフとして文書化済み。 |
| 参照追従 | workflow 内の `uses: ./.github/actions/...` は lint エンジンが従来どおり解決。探索範囲を狭めても式チェックは維持。 |
| verbose | `searching under cwd` で探索境界が明示される。 |

### セルフレビューと対応

| 指摘 | 対応 |
|------|------|
| `startDirectory` 未正規化で相対パス混在の可能性 | `ResolveFiles` 入口で `Path.GetFullPath` を適用 |
| `CollectYamlFiles` 重複定義 | 実装時のミスを削除し単一定義に修正 |
| config 探索と入力探索の非対称 | 意図的に分離。config は walk 継続、入力は CWD のみ。`docs/configuration.md` に明記 |
| 仕様 §1.1 と §5 の矛盾 | §5 を正とし §1.1 も CWD 厳密の文言に統一 |

---

## 移行ガイド

| シナリオ | 修正前 | 修正後（案 B） |
|----------|--------|----------------|
| LogicLooper CI（子 cwd、親 actions あり） | 子 workflows + 親 actions | 子のみ |
| 通常の repo root から実行 | root の workflows（+ actions） | 変更なし |
| `cd subdir`（subdir に `.github` なし） | 親の workflows を発見 | **空**（明示パスまたは root で実行） |
| 親子両方 lint | 偶然動く | `FILES` で両方指定 |

```yaml
# CI 例: 子リポジトリのみ（変更不要）
- run: seiton --include-actions --config .github/seiton.yaml
  working-directory: ${{ matrix.repository }}
```

```sh
# モノレポ subdir から親 workflows を lint したい場合
seiton .github/workflows          # repo root で実行
# または
seiton ../../.github/workflows    # subdir から明示パス
```

---

## 未決定・将来

1. **config 探索の walk** — 入力探索は CWD 厳密化済み。config も揃えるかは別 issue。
2. **`discovery.scope` フラグ** — 案 A 相当の walk を opt-in で復活させる需要があれば検討。現時点では不要。

---

## 関連ファイル

| 種別 | パス |
|------|------|
| 実装 | `src/Seiton/Commands/InputDiscovery.cs`, `CheckCommand.cs` |
| テスト | `tests/Seiton.Tests/InputDiscoveryTests.cs` |
| ベンチマーク | `src/Seiton.Benchmark/InputDiscoveryBenchmark.cs` |
| 仕様 | `.github/docs/Seiton_CLI_spec.md` §5 |
