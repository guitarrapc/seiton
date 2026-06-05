# Input Discovery 境界の見直しプラン

## 背景

ネストした CI チェックアウト（親リポジトリ + `path:` で子リポジトリ）で `seiton` を子ディレクトリの `working-directory` から実行すると、子の `.github/workflows` に加えて**親**の `.github/actions` が lint 対象に含まれることがある。

再現例（Cysharp/Actions がワークスペース root、LogicLooper が `LogicLooper/` に checkout、`--include-actions` 有効）:

```text
../.github/actions/benchmark-runnable/action.yaml   # 親リポジトリ
.github/workflows/build-release.yaml                # 子リポジトリ（CWD）
```

ユーザーは「CWD 以下だけを見るのが自然では」と指摘している。本ドキュメントはその見解の評価と、修正する場合の方針をまとめる。

---

## 現状の原因

### 1. 入力探索は親ディレクトリへさかのぼる（仕様どおり）

`FILES` 引数なしの自動探索では、CWD から親へ向かって次を**独立に**解決する（`Seiton_CLI_spec.md` §5 Input Discovery）。

| 対象 | 探索関数 | 挙動 |
|------|----------|------|
| workflows | `FindWorkflowsDirectory` | 最初に見つかった `.github/workflows/` を採用 |
| actions（`--include-actions` 時） | `FindActionsDirectory` | 最初に見つかった `.github/actions/` を採用 |

実装: `src/Seiton/Commands/InputDiscovery.cs`

### 2. workflows と actions が別階層から取られる

子リポジトリに `.github/workflows` はあるが `.github/actions` がない場合:

1. workflows → CWD（子）でヒットし、探索停止
2. actions → CWD に無いため親へさかのぼり、親の `.github/actions` を採用

結果として「子の workflow + 親の action」という**意図しない混在**が起きる。これはバグというより、独立探索の帰結。

### 3. `--config` は探索範囲を絞らない

`--config ../.github/seiton.yaml` は設定ファイルの読み込み先のみを指定する。lint 対象ファイルの探索起点は引き続き CWD（`Environment.CurrentDirectory`）であり、config の所在とは無関係。

### 4. 参照解決と入力探索は別経路

workflow 内の `uses: ./.github/actions/foo` や `uses: ../.github/actions/foo` を辿る処理（`LocalActionOutputResolver` 等）は、workflow ファイルパスと repository root を基準に**参照先の存在確認**を行う。パストラバーサル防止のガードあり。

一方、入力探索は YAML の内容を見ずにファイルシステムを走査するため、「参照されていない親ディレクトリの action」も `--include-actions` で拾う。

### 5. 設定ファイル探索も同様に親へさかのぼる

config の自動探索（`CliConfigBridge.DiscoverConfigPath`）も CWD から親へ walk する。`discoveryBoundary` パラメータは実装済みだが production では未使用（`discoveryBoundary: null`）。

---

## 見解の評価

### 妥当な点（修正を検討すべき理由）

1. **境界の一貫性がない**  
   workflows は子、actions は親、という組み合わせは利用者のメンタルモデルと合わない。「1 回の `seiton` 実行 = 1 つの lint 対象ツリー」が自然。

2. **CI のネスト checkout で予期しない副作用**  
   親リポジトリを先に checkout したうえで子を `path:` 配置するパターンでは、子だけ lint したい意図と実際の対象がずれる。`--config` を明示しても防げない。

3. **「パストラバーサル」との類似**  
   セキュリティツールとして、同意なく親ディレクトリの YAML を読み込むのは説明が難しい。lint エンジン内部の参照解決には root ガードがあるのに、探索段階には境界がないのは非対称。

4. **ユーザーの提案するモデルは筋が通る**  
   - **入力探索**: 原則 CWD 以下（または明示した 1 つの repository root 以下）のみ  
   - **参照追従**: workflow / action YAML が指すローカルパスだけ、存在確認付きで追加解析  
   探索と参照は責務が分離され、今回の事故パターンを構造的に防げる。

### 現行仕様にあった（ただし限定的な）理由

1. **モノレポのサブディレクトリからの実行**  
   `cd packages/foo && seiton` でリポジトリ root の `.github/workflows` を見つけたい、というユースケース。git / eslint 等の「root を探して上る」パターンに倣った。

2. **actionlint 等との慣習**  
   「カレントから repo root を推定して lint」は CLI ツールでありがち。ただし **workflows と actions を別階層から混在させる**ことまでは慣習として説明しにくい。

### 結論

**入力探索の現行仕様は、ネスト CI やマルチ checkout を考えると問題がある。** 特に workflows / actions の独立探索は設計ミスに近く、修正対象とするのが妥当。

一方、モノレポ向けの「サブディレクトリから root を見つける」需要はゼロではない。完全に walk を廃止するなら **明示的な opt-in**（フラグまたは config）で残すのが安全。

参照追従だけに頼る案は、**誰も参照していない standalone の composite action** を `--include-actions` で一括 lint したい場合に不足する。これは「探索 root を 1 つ決める」ことで従来用途をカバーできる（後述）。

---

## 目標とする挙動（WHAT）

### 原則

1. **単一の探索 root**  
   1 回の実行で採用する `.github/workflows` と `.github/actions` は、**同じ探索 root** から解決する。別階層の混在を禁止する。

2. **デフォルトの探索 root = CWD が属するリポジトリ root（推奨案 A）**  
   CWD から親へ walk し、**最初に見つかった `.github/` ディレクトリの親**を探索 root とする。  
   - `LogicLooper/` から実行 → root は `LogicLooper/`（子の `.github` を検出）  
   - 親の `.github/actions` は探索 root 外のため対象外  

   これだけで今回の CI 事故は解消する。モノレポの `cd subdir && seiton` も、subdir 内に `.github` が無ければ従来どおり上位 repo root に到達する。

3. **代替案 B: 探索 root = CWD 厳密（より保守的）**  
   walk を廃止し、CWD 以下の `.github/workflows` / `.github/actions` のみ。  
   - ネスト CI では最も予測可能  
   - `cd repo/sub && seiton` では `repo/.github` を見つけられなくなる → `seiton .` や `seiton ../../.github/workflows` 等の明示操作が必要  

   **推奨は案 A**（単一 root + walk、ただし混在禁止）。案 B は breaking が大きいため、必要なら `discovery.scope: cwd` で opt-in。

4. **参照追従は現状維持（lint エンジン側）**  
   workflow が `uses: ../foo` 等で指すファイルは、既存の repository root ガード付き resolver で解析する。入力探索の walk とは別問題。

5. **明示 `FILES` は現状どおり**  
   パスを渡した場合はそのまま lint（ディレクトリは再帰展開）。

### 設定ファイル探索

入力探索と整合させる:

- デフォルト: 入力探索と同じ root 境界まで walk（混在しない）
- `--config` / `SEITON_CONFIG`: 従来どおり明示パス優先
- 将来: `discovery.boundary` または既存の `discoveryBoundary` を production で使い、walk の上限を設定可能にする

---

## 対策（実装時のチェックリスト）

### 仕様

- [ ] `Seiton_CLI_spec.md` §5 Input Discovery を更新  
  - 独立探索の記述を削除  
  - 単一探索 root の定義を追加  
  - ネスト checkout の例を lessons learned として追記  
- [ ] `Seiton_CLI_csharp_spec.md` / `Seiton_CLI_go_spec.md` を同期  
- [ ] `docs/configuration.md` の nested repositories 節を更新（挙動変更後の推奨ワークフロー）

### コード

- [ ] `InputDiscovery`: `FindWorkflowsDirectory` / `FindActionsDirectory` を統合し、共通の `FindRepositoryRoot(startDir)` から `.github/workflows` と `.github/actions` を列挙  
- [ ] `CheckCommand.ShouldSuggestIncludeActions`: ancestor walk の前提を単一 root に合わせる  
- [ ] 回帰テスト: ネスト CI 相当のディレクトリツリー（親 actions + 子 workflows、`cwd=子`）で親 actions が**含まれない**こと  
- [ ] 回帰テスト: `cd monorepo/subdir`（subdir に `.github` なし）で repo root の workflows が**引き続き見つかる**こと（案 A）  
- [ ] `--verbose` の discovery ログに採用した探索 root を出力

### ドキュメント・利用者向け

- [ ] SKILL / `docs/usage.md`: 「workflows と actions は同じ root から探索される」旨を記載  
- [ ] breaking change がある場合は CHANGELOG に明記（案 B を採用した場合）

---

## 影響と移行

| シナリオ | 現状 | 案 A（単一 root） |
|----------|------|-------------------|
| LogicLooper CI（子 cwd、親 actions あり） | 子 workflows + 親 actions | 子のみ |
| 通常の単一 repo root から実行 | root の workflows（+ actions） | 変更なし |
| `cd subdir`（subdir に `.github` なし） | root の workflows | 変更なし |
| 子に workflows のみ、親に actions のみで**両方** lintしたい | 偶然動く | **意図的に** `FILES` で両方指定するか、2 回実行 |

親と子をまとめて lint したい CI は、もともと偶然動いていただけなので、明示パスまたは matrix 設計の見直しが必要になる可能性がある。

---

## 未決定事項

1. **デフォルトを案 A と案 B のどちらにするか** — 本プランは案 A を推奨。議論があれば `discovery.scope` で切り替え可能にする。  
2. **config 探索の walk** — 入力探索と同じ root 境界に揃えるか、config だけ従来どおり広く walk するか。揃える方が一貫性が高い。  
3. **メジャーバージョン** — 案 A は「誤って親を拾っていた」ケースの修正であり、semver 的には minor でも説明可能。案 B は major 検討。

---

## 関連ファイル

| 種別 | パス |
|------|------|
| 実装 | `src/Seiton/Commands/InputDiscovery.cs` |
| 仕様 | `.github/docs/Seiton_CLI_spec.md` §5 |
| 参照解決（変更対象外） | `src/Seiton.Core/Linting/LocalActionOutputResolver.cs`, `ActionRefHelpers.TryGetRepositoryRoot` |
| config 探索 | `src/Seiton/Config/CliConfigBridge.cs` |
| ユーザードキュメント | `docs/configuration.md`（Nested repositories） |
