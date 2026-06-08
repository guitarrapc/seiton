# zizmor リポジトリ詳細解析レポート

**バージョン:** 1.23.1
**解析日:** 2026年4月7日
**ライセンス:** MIT
**ホームページ:** https://docs.zizmor.sh

---

## 目次

1. [プロジェクト概要](#1-プロジェクト概要)
2. [クレート構造と依存グラフ](#2-クレート構造と依存グラフ)
3. [CLI インターフェース](#3-cli-インターフェース)
4. [YAML 解析の仕組み](#4-yaml-解析の仕組み)
5. [全 Audit ルール一覧](#5-全-audit-ルール一覧)
6. [Finding システム](#6-finding-システム)
7. [出力フォーマット](#7-出力フォーマット)
8. [設定システム](#8-設定システム)
9. [GitHub スキーマ変更への追随](#9-github-スキーマ変更への追随)
10. [テスト戦略](#10-テスト戦略)
11. [主要な技術的設計](#11-主要な技術的設計)

---

## 1. プロジェクト概要

`zizmor`は **GitHub Actions のセキュリティ静的解析ツール**。`.github/workflows/*.yml`、コンポジットアクションの`action.yml`、`dependabot.yml`をローカル・リモート問わず解析し、CI/CDパイプラインの脆弱性を検出する。

主な検出カテゴリ:
- テンプレートインジェクション（`${{ }}`による任意コード実行）
- クレデンシャルの漏洩・永続化
- `GITHUB_TOKEN`の過剰権限
- 改ざんされたアクション参照（インポスターコミット、アーカイブ済みリポジトリ）
- 危険なワークフロートリガー（`pull_request_target`など）
- コンテナ・ランナーの設定ミス
- Dependabot設定のセキュリティ問題

---

## 2. クレート構造と依存グラフ

```
zizmor (メインバイナリ)
├── github-actions-models (0.45.0)      ← workflow/action/dependabot の Serde モデル
├── github-actions-expressions (0.0.15) ← ${{ }} 式パーサー (pest PEG)
├── yamlpath (0.34.0)                   ← YAML ルートベース位置追跡
├── yamlpatch (0.13.0)                  ← YAML 自動修正パッチ操作
├── subfeature (0.0.5)                  ← ソーススパントラッキング
├── tree-sitter-iter (0.0.3)            ← tree-sitter ノードイテレータ
├── reqwest + http-cache-reqwest        ← GitHub API（HTTP キャッシュ付き）
├── clap                                ← CLI パース
├── tokio                               ← 非同期ランタイム
├── serde-sarif                         ← SARIF 出力
├── annotate-snippets                   ← 診断スニペット描画（Cargo スタイル）
├── fst                                 ← コンパイル時 FST
├── tree-sitter-bash / -powershell      ← シェルスクリプト AST 解析
└── tower-lsp-server [optional]         ← LSP サーバーモード
```

### ビルド時アセット (`build.rs`)

| 元ファイル | 生成物 | 用途 |
|---|---|---|
| `data/archived-repos.txt` | `archived-repos.fst` | アーカイブ済みリポジトリの O(n) 検索 |
| `data/context-capabilities.csv` | `context-capabilities.fst` | コンテキストの危険度マッピング |
| `data/codeql-injection-sinks.json` | バイナリに埋め込み | テンプレートインジェクションのシンクリスト |

---

## 3. CLI インタフェース

```
zizmor [OPTIONS] <INPUT>...
```

### 入力オプション (`InputArgs`)

| フラグ | 説明 |
|---|---|
| `INPUT`（位置引数） | ファイル / ディレクトリ / `user/repo[@ref]` / `-`（stdin） |
| `--collect <KIND>` | 収集種別: `default`, `workflows`, `actions`, `dependabot`（コンマ区切り） |
| `--strict-collection` | YAML エラーで警告ではなく失敗 |

### Audit オプション (`AuditArgs`)

| フラグ | 説明 |
|---|---|
| `--fix[=MODE]` | 自動修正: `safe`（デフォルト）/ `unsafe-only` / `all` |
| `--persona <PERSONA>` | `regular`（デフォルト）/ `pedantic` / `auditor` |
| `--min-severity` | `informational` / `low` / `medium` / `high` |
| `--min-confidence` | `low` / `medium` / `high` |

### 出力オプション (`OutputArgs`)

| フラグ | 説明 |
|---|---|
| `--format <KIND>` | `plain`（デフォルト）/ `json` / `sarif` / `github` |
| `--color <WHEN>` | `always` / `never` / `auto` |
| `--no-progress` | プログレスバー非表示 |
| `--render-links` | OSC 8 ハイパーリンク |
| `--no-exit-codes` | 終了コードを常に 0 に |

### ネットワークオプション (`NetworkArgs`)

| フラグ | 説明 |
|---|---|
| `--offline` / `-o` | 完全オフライン（API 呼び出しなし、リモートリポジトリ不可） |
| `--no-online-audits` | オンライン Audit のみスキップ（リモートリポジトリは可） |
| `--gh-token <TOKEN>` | GitHub トークン（`GH_TOKEN`, `GITHUB_TOKEN`, `ZIZMOR_GITHUB_TOKEN`も可） |
| `--gh-hostname <HOST>` | GHES ホスト名（デフォルト: `github.com`） |
| `--cache-dir <DIR>` | HTTP キャッシュディレクトリ |

### グローバルオプション

| フラグ | 説明 |
|---|---|
| `-c` / `--config <FILE>` | 設定ファイルパス（`ZIZMOR_CONFIG`環境変数も可） |
| `--no-config` | 設定ファイル読み込みを無効化 |
| `--completions <SHELL>` | シェル補完生成（bash / fish / zsh / powershell 等） |
| `--generate-schema` | `zizmor.yml`の JSON Schema を出力（`schema` feature 必要） |
| `--lsp` | LSP サーバーモード（`lsp` feature 必要） |

### 終了コード

| コード | 意味 |
|---|---|
| `0` | 成功（閾値以上の検出なし） |
| `1` | 閾値以上の検出あり |
| `2` | ツール内部エラー |

---

## 4. YAML 解析の仕組み

### パーサー

**`serde_yaml`** を使用。カスタムトークナイザーは存在せず、全データは`serde_yaml::from_str`経由でRustの型へデシリアライズされる。

### モデル定義（`github-actions-models`クレート）

外部スキーマファイルではなく、**Serde の derive マクロ**でRust型として定義している。

| パターン | 用途例 |
|---|---|
| `#[serde(rename_all = "kebab-case")]` | YAML の`runs-on:` → Rust の`runs_on` |
| `#[serde(untagged)]` | `Job`、`StepBody`（`run:`か`uses:`かを構造で区別） |
| `#[serde(flatten)]` | `Step`に`StepBody`をインライン展開 |
| `#[serde(deserialize_with = "...")]` | `scalar_or_vector`、`bool_is_string`等カスタムデシリアライザ |

**カスタムデシリアライザ（`common.rs`）:**
- `scalar_or_vector` — `needs:`など、単値か配列かを正規化
- `bool_is_string` — YAMLの`true`/`false`を文字列に変換（`run:`フィールド用）
- `null_to_default` — YAML nullを`T::default()`にマップ
- `If`の`impl Deserialize` — `if:`条件のbare式・bool・数値を吸収
- `RunsOn`の`#[serde(remote = "Self")]` — バリアント制約の事後検証

### `${{ }}`式の処理（2 層構造）

**1. モデル層（`common/expr.rs`）**
- `ExplicitExpr` — `${{ ... }}`形式を検証するnewtype
- `LoE<T>`（Literal or Expr）— `#[serde(untagged)]`で先に`ExplicitExpr`を試み、失敗すれば`T`にフォールバック。`runs-on:`, `env:`, `matrix:`など広く使用
- `BoE` — `LoE<bool>`の型エイリアス（`if:`, `cancel-in-progress:`等）
- `If` — bare式・bool・数値を受け付ける専用型

**2. 解析層（`github-actions-expressions`クレート）**
- **`pest` PEG パーサー**（`.pest`文法ファイルあり）で式をASTに変換
- `Context`（`github.event.pull_request.title`等）、`Literal`、`BinOp`、`Call`等のノード
- テンプレートインジェクション検出等のaudit層が使用

### ロードパイプライン

```
raw YAML
  → serde_yaml::from_str::<Workflow>()       Serde でデシリアライズ
  → jsonschema でバリデーション              バンドルされた JSON Schema（SchemaStore 由来）
  → yamlpath::Document::new()                YAML パス索引構築（行番号取得用）
  → models::workflow::Workflow { .. }        解析結果 + パス索引を保持
```

エラー分類:
- `Syntax` — YAML自体が不正
- `Schema` — JSON Schemaバリデーション失敗
- `Model` — Serdeモデル定義との不一致（`github-actions-models`のバグ扱い）

---

## 5. 全 Audit ルール一覧

### Audit 分類

- **Severity:** `Informational < Low < Medium < High`
- **Confidence:** `Low < Medium < High`
- **Persona:** `Regular`（デフォルト）< `Pedantic` < `Auditor`

### オンライン必須（`--gh-token`が必要）

| Audit ID | 重要度 | 説明 |
|---|---|---|
| `impostor-commit` | High | `uses:`のコミット SHA が参照リポジトリに存在しないゴーストコミットを検出 |
| `ref-confusion` | High | ブランチとタグ両方に存在する曖昧なシンボリック ref を検出 |
| `known-vulnerable-actions` | 可変 | GitHub Security Advisories API で CVE のあるアクションバージョンを検出 |
| `stale-action-refs` | Low | タグに紐付いていないコミット SHA ピンを検出 |
| `ref-version-mismatch` | Medium | ハッシュピンとバージョンコメントの不一致を検出 |

### オンライン拡張（オフラインでも動作、トークンで精度向上）

| Audit ID | 重要度 | 説明 |
|---|---|---|
| `artipacked` | High/Medium/Low | `actions/checkout`の`persist-credentials: true` + アーティファクトアップロードの組み合わせ |
| `unpinned-uses` | High | コミット SHA でピン止めされていないアクション参照 |

### 完全オフライン

| Audit ID | 重要度 | ペルソナ | 説明 |
|---|---|---|---|
| `template-injection` | High | Regular | `${{ }}`内の攻撃者制御コンテキストが`run:`やアクション入力に展開されるケースを検出 |
| `dangerous-triggers` | High | Regular | `pull_request_target` / `workflow_run`トリガーを検出 |
| `excessive-permissions` | 可変 | Regular/Pedantic | `permissions: write-all`や過剰な書き込み権限 |
| `insecure-commands` | High | Regular | `ACTIONS_ALLOW_UNSECURE_COMMANDS: true`を検出 |
| `github-env` | High/Medium | Regular | tree-sitter で`GITHUB_ENV` / `GITHUB_PATH`への書き込みを検出 |
| `cache-poisoning` | High | Regular | 危険なトリガー + キャッシュ対応アクションの組み合わせ |
| `secrets-inherit` | Medium | Regular | `secrets: inherit`による全シークレット伝搬 |
| `hardcoded-container-credentials` | High | Regular | コンテナ資格情報のリテラルパスワード |
| `self-hosted-runner` | Medium | **Auditor** | セルフホストランナーの使用 |
| `unpinned-images` | High | Regular | SHA256 未固定のコンテナイメージ |
| `overprovisioned-secrets` | Medium | Regular | `toJSON(secrets)`等での全シークレット注入 |
| `unredacted-secrets` | Medium | Regular | `fromJSON(secrets.foo)`等でのシークレット redaction 迂回 |
| `secrets-outside-env` | Low/Medium | Regular | デプロイ環境なしでの`secrets.*`参照 |
| `obfuscation` | Medium | Regular | `uses:`パスに`..`等が含まれる難読化 |
| `bot-conditions` | High | Regular | `github.actor == 'dependabot[bot]'`等のなりすまし可能な bot チェック |
| `unsound-condition` | High | Regular | YAML ブロックスカラーで`if:`条件が常に true になるパターン |
| `unsound-contains` | High/Medium | Regular | `contains(user-context, 'value')`の迂回可能なパターン |
| `archived-uses` | Medium | Regular | アーカイブ済みリポジトリのアクション参照 |
| `concurrency-limits` | Low | **Pedantic** | `cancel-in-progress: true`なし concurrency 設定 |
| `dependabot-execution` | High | Regular | `insecure-external-code-execution: allow` |
| `dependabot-cooldown` | Low | Regular | Dependabot の`cooldown.default-days`未設定 |
| `forbidden-uses` | High | Regular | ユーザー設定の deny リストに一致する`uses:` |
| `superfluous-actions` | Low | Regular | `gh` CLI で代替できるアクションの使用 |
| `undocumented-permissions` | Info | **Pedantic** | コメントのない`permissions:`エントリ |
| `use-trusted-publishing` | Medium | Regular | PyPI/RubyGems/npm で Trusted Publishing（OIDC）未使用 |

**合計: 30 以上の Audit ルール**

---

## 6. Finding システム

### Finding 構造体

```rust
Finding {
    ident:          &'static str,     // "template-injection"
    desc:           &'static str,     // 短い説明
    url:            &'static str,     // https://docs.zizmor.sh/audits/#<ident>
    determinations: Determinations { confidence, severity, persona },
    locations:      Vec<Location>,    // YAML ルート + バイトオフセット
    tip:            Option<String>,   // 修正アドバイス
    ignored:        bool,             // 抑制フラグ
    fixes:          Vec<Fix>,         // 自動修正パッチ
}
```

### Location システム

各Locationは以下を持つ:
- `SymbolicLocation`: YAMLルート + アノテーションテキスト + `LocationKind`（Primary / Related / Hidden）
- 具体的なバイトオフセット（`yamlpath::Document`から解決）

### 自動修正 (`Fix`)

`yamlpatch`操作で元のYAMLソースを直接変更（再シリアライズなし → コメント・フォーマット保持）。

| Disposition | 適用条件 |
|---|---|
| `Safe` | `--fix` / `--fix=safe` |
| `Unsafe` | `--fix=unsafe-only` / `--fix=all` |

パッチ操作: `Replace`, `Remove`, `Add`, `EmplaceComment`, `ReplaceComment`

### 主要データフロー

```
CLI 入力（ファイル / ディレクトリ / リポジトリ スラグ / stdin）
  ↓
InputRegistry::collect()
  ├── ローカル: .gitignore を尊重したディレクトリ走査
  ├── リモート: GitHub API でリポジトリ tarball を取得・展開
  └── JSON Schema で YAML バリデーション
  ↓
AuditRegistry::default_audits(AuditState)（30+ ルール登録）
  ↓
非同期 Audit ループ（FuturesOrdered で並行実行）
  audit_workflow() → audit_normal_job() → audit_step()
  ↓
FindingRegistry（persona / severity / confidence フィルタ、ignore 適用）
  ↓
出力（plain / json / sarif / github）
  ↓
--fix 指定時: yamlpatch でソースを直接パッチ
  ↓
ExitCode（最高重要度ベース）
```

### テンプレートインジェクション検出フロー

```
Step.run / Step.with.script
  ↓
extract_fenced_expressions() → Vec<ExtractedExpr>（${{ ... }} を抽出）
  ↓
Expr::parse() → SpannedExpr（pest PEG パーサー）
  ↓
SpannedExpr::dataflow_contexts() → Vec<(Context, Origin)>
  ↓
CONTEXT_CAPABILITIES_FST.get(context_str) → Capability
  ├── Arbitrary → High severity（攻撃者制御）
  ├── Structured → Medium severity
  └── Fixed → スキップ（制御不能）
```

---

## 7. 出力フォーマット

| フォーマット | フラグ | 実装 | 用途 |
|---|---|---|---|
| `plain` | デフォルト | `output/plain.rs`（`annotate-snippets`） | Cargo スタイル診断、OSC 8 ハイパーリンク対応 |
| `json` | `--format=json` | `output/json/v1.rs` | バージョン管理された`V1Finding` JSON 配列 |
| `sarif` | `--format=sarif` | `output/sarif.rs`（`serde-sarif`） | SARIF 2.1.0（セキュリティスキャン統合） |
| `github` | `--format=github` | `output/github.rs` | GitHub workflow コマンド形式（PR アノテーション） |

**重要度 → 出力レベル対応:**

| Severity | plain | SARIF level | GitHub コマンド |
|---|---|---|---|
| Informational | `INFO` | `note` | `notice` |
| Low | `HELP` | `note` | `warning` |
| Medium | `WARNING` | `warning` | `warning` |
| High | `ERROR` | `error` | `error` |

---

## 8. 設定システム

### 設定ファイル探索順（入力グループのルートから）

1. `.github/zizmor.yml`
2. `.github/zizmor.yaml`
3. `zizmor.yml`
4. `zizmor.yaml`

`--config <FILE>`または`ZIZMOR_CONFIG`環境変数で上書き可能。

### `zizmor.yml`スキーマ例

```yaml
rules:
  template-injection:
    disable: false
    ignore:
      - "ci.yml:42"       # ファイル名[:行[:列]] 形式
  forbidden-uses:
    config:
      deny:
        - "some-org/*"
  dependabot-cooldown:
    config:
      days: 14
  secrets-outside-env:
    config:
      allow:
        - MY_SECRET
```

### インライン抑制

```yaml
- run: echo ${{ github.event.issue.title }}  # zizmor: ignore[template-injection]
```

### Audit 固有設定

| Audit | 設定キー | オプション |
|---|---|---|
| `unpinned-uses` | `unpinned-uses` | `policy`: `allow`/`deny`リスト |
| `secrets-outside-env` | `secrets-outside-env` | `allow`: スキップするシークレット名リスト |
| `dependabot-cooldown` | `dependabot-cooldown` | `days`: 最小日数（デフォルト: 7） |
| `forbidden-uses` | `forbidden-uses` | `allow`または`deny`パターンリスト |

---

## 9. GitHub スキーマ変更への追随

### スキーマの保持形式

3つのJSON Schemaが **バイナリにコンパイル時埋め込み**（`include_str!`）される。元データはSchemaStoreから定期取得:

| ファイルパス | 取得元 |
|---|---|
| `crates/zizmor/src/data/github-workflow.json` | `https://www.schemastore.org/github-workflow.json` |
| `crates/zizmor/src/data/github-action.json` | `https://www.schemastore.org/github-action.json` |
| `crates/zizmor/src/data/dependabot-2.0.json` | `https://www.schemastore.org/dependabot-2.0.json` |

### 自動更新の仕組み（`codegen.yml`）

```
スケジュール: 毎週月曜 12:00 UTC（+ workflow_dispatch で手動実行可）
  ↓
make refresh-schemas
  curl https://www.schemastore.org/github-workflow.json → ファイルに上書き
  curl https://www.schemastore.org/github-action.json  → ファイルに上書き
  curl https://www.schemastore.org/dependabot-2.0.json → ファイルに上書き
  ↓
peter-evans/create-pull-request
  → git diff が存在する場合のみ
  → ドラフト PR を自動作成（タイトル: "[BOT] update JSON schemas from SchemaStore"）
  → アサイン: woodruffw
  → マージは人間がレビューして手動実施
```

同じ`codegen.yml`が以下も自動更新する:
- `context-capabilities.csv`（GitHub webhookイベントコンテキスト）
- CodeQLインジェクションシンクリスト

### 更新プロセスの限界

**自動化の範囲と手動対応が必要な範囲:**

| 対象 | 自動化 | 方法 |
|---|---|---|
| JSON Schema ファイル（バリデーション用） | **Yes** | 週次 bot PR |
| `context-capabilities.fst` | **Yes** | 週次 bot PR |
| CodeQL インジェクションシンク | **Yes** | 週次 bot PR |
| `github-actions-models` Rust struct 定義 | **No** | コントリビューターが手動更新 |
| FST データ（`archived-repos.fst`） | **部分的** | 手動スクリプト |

**重要:** Rustモデルの自動同期は存在しない。JSON Schemaに新フィールドが追加されても、Serdeモデルに定義がなければ**サイレントに無視**される。`github-actions-models`がどのスキーマバージョンを最後に同期したかを追跡するメカニズムも持たない。

---

## 10. テスト戦略

### テスト階層

```
crates/github-actions-models/tests/   ← モデルパーサーのフィクスチャテスト
crates/zizmor/tests/integration/      ← Audit ルールのスナップショットテスト
crates/yamlpath/tests/                ← YAML パス解決の統合テスト
crates/yamlpatch/tests/               ← YAML パッチ操作のスナップショットテスト
```

### 層 1: `github-actions-models`フィクスチャテスト

`crates/github-actions-models/tests/`に配置。テストライブラリは`insta`がdev-dependencyに含まれるが、このレイヤーでは純粋アサーション（`assert_eq!`、`assert!`、`matches!`）を使用。

**`test_load_all()`パターン:**
各テストファイルにサンプルディレクトリを全走査する`test_load_all()`を実装。全てのYAMLファイルで`serde_yaml::from_str`が成功することを検証するスモークテスト。

| サンプルディレクトリ | 内容 |
|---|---|
| `tests/sample-workflows/` | 実際のワークフロー YAML（`pip-audit-ci.yml`、`runs-on-expr.yml`等） |
| `tests/sample-actions/` | 実際のアクション YAML（`setup-python.yml`、`gh-action-pip-audit.yml`等） |
| `tests/sample-dependabot/v2/` | Dependabot 設定；`*.invalid.*`は失敗が期待される |

**名前付きテスト例:**
- `test_pip_audit_ci()` — 特定の実ワールドワークフローの構造を検証
- `test_setup_python()` — setup-Pythonアクションのモデル解析を検証
- `test_contents()` — GitHub API v3レスポンスとの互換性検証

### 層 2: `zizmor`統合テスト（スナップショット）

`crates/zizmor/tests/integration/`構造:

```
main.rs              ← モジュール宣言
acceptance.rs        ← CLI 実行 + JSON 出力の JSONPath アサーション
cli.rs               ← CLI 動作（stdin 読み込み、フラグ）
config.rs            ← 設定ファイル探索
common.rs            ← Zizmor ビルダー構造体 + input_under_test() ヘルパー
e2e.rs               ← エンドツーエンド（オンライン GitHub API テスト、feature フラグで制御）
audit/               ← 各 Audit ルールごとに 1 ファイル（約 30 ファイル）
snapshots/           ← insta スナップショット保存ディレクトリ
test-data/           ← Audit テスト用入力 YAML フィクスチャ
```

**スナップショットテストパターン（`insta`）:**

```rust
// audit/template_injection.rs の例
insta::assert_snapshot!(
    zizmor()
        .input(input_under_test(
            "template-injection/template-injection-static-matrix.yml"
        ))
        .args(["--persona=auditor"])
        .run()?,
    @r#"
help[template-injection]: code injection via template expansion
  --> @@INPUT@@:25:36
  ...
1 finding: 0 informational, 1 low, 0 medium, 0 high
"#
);
```

- `@@INPUT@@`プレースホルダーは実行時に実際のファイルパスに置換
- 特定GitHub Issueの回帰テストは`issue-NNN-repro.yml`と命名
- オンラインAPIテストは`#[cfg_attr(not(feature = "gh-token-tests"), ignore)]`でゲート

### 層 3: `yamlpath`統合テスト

`crates/yamlpath/tests/integration_test.rs`が`tests/testcases/`からYAMLテストケースファイルを読み込み。各ファイルにドキュメント + クエリ + 期待抽出結果を含む。クエリモード: `pretty`、`exact`、`key-only`。

### 層 4: `yamlpatch`スナップショットテスト

`crates/yamlpatch/tests/unit_tests.rs`が`insta::assert_snapshot!`でYAMLシリアライズ/パッチ往復変換を検証。

### CI パイプライン（`.github/workflows/ci.yml`）

PR及び`main`へのプッシュで実行されるジョブ:

| ジョブ | 内容 |
|---|---|
| **lint** | `cargo fmt --check` + `cargo clippy -- --deny warnings` |
| **test** | `cargo test --features crater-tests,tty-tests,schema` + `make snippets` + `git diff --exit-code`（`help.txt`の最新化確認） |
| **test-site** | `make site`（ドキュメントサイトビルド） |
| **test-schema** | (1) `register_audit!`数と`zizmor.schema.json`のプロパティ数の一致確認 (2) `make generate-schema` + `git diff --exit-code`（スキーマ最新化確認） |
| **all-tests-pass** | 上記全ジョブの通過を必須とするゲートジョブ |

その他のCIワークフロー:

| ワークフロー | 用途 |
|---|---|
| `zizmor.yml` | zizmor 自身で`.github/`を解析（自己適用） |
| `benchmark.yml` | CodSpeed ベンチマーク（`make bench`、Python pytest） |
| `codegen.yml` | 週次スキーマ/データ自動更新 bot PR |
| `wolfi-update-check.yml` | 6 時間ごとに Wolfi OS の zizmor 新リリールを確認してイシュー起票 |

### スキーマ整合性チェック（`test-schema`ジョブ詳細）

```bash
# 1. Audit 数の整合確認
register_audit_count=$(grep -c "register_audit!" crates/zizmor/src/registry.rs)
schema_property_count=$(jq '.properties | length' support/zizmor.schema.json)
[ "$register_audit_count" == "$schema_property_count" ] || exit 1

# 2. コミット済みスキーマが最新かの確認
make generate-schema
git diff --exit-code support/zizmor.schema.json
```

これはzizmor **自身の設定スキーマ**（`zizmor.yml`のJSON Schema）の整合性を検証するもので、GitHubのGHAスキーマとの整合性チェックではない点に注意。

---

## 11. 主要な技術的設計

| 設計 | 内容 |
|---|---|
| **Audit トレイト階層** | `audit_step` → `audit_normal_job` → `audit_workflow`のデフォルト委譲。各 Audit は必要な粒度のみ実装すれば良く、実装負担を最小化 |
| **`audit_meta!`マクロ** | `ident()`、`desc()`、`url()`を一括生成。URL は`https://docs.zizmor.sh/audits/#<id>`に自動導出 |
| **コンパイル時 FST** | アーカイブリポジトリ・コンテキスト危険度を`build.rs`で FST にコンパイル。O(n·k) 検索をゼロヒープで実現 |
| **tree-sitter シェル解析** | Bash/PowerShell の AST を tree-sitter で解析し、正規表現では不可能な`GITHUB_ENV`書き込みパターンを検出。Windows CMD は正規表現（制限あり） |
| **非同期 Audit 実行** | `FuturesOrdered`で Audit を並行実行（特にオンライン Audit の API 待機を並列化） |
| **バージョン管理 JSON 出力** | 内部`Finding`と別の`V1Finding`型を使用。内部リファクタでも下流を壊さない設計 |
| **`yamlpath` + `yamlpatch`** | YAML を再シリアライズせず元ソースへのルートベースパッチで修正を適用（コメント・フォーマット保持） |
| **Persona モデル** | `Regular < Pedantic < Auditor`の 3 段階ノイズ制御でユーザーの成熟度に合わせたフィルタリング |
| **LSP モード** | `tower-lsp-server`によるエディタ統合オプション（`--lsp`フラグ、`lsp` feature デフォルト有効） |
| **HTTP キャッシュ** | Moka（インメモリ）+ cacache（ディスク）の二層キャッシュで API 呼び出しを抑制 |
| **`subfeature`** | `Span`・`Subfeature`型が解析済み AST ノード位置を元の YAML ソースのバイトオフセットにマップ。`annotate-snippets`と SARIF の`physicalLocation.region`に使用 |
| **jemalloc** | Windows/OpenBSD 以外では`tikv-jemallocator`を採用し、デフォルトのシステムアロケータより優れたメモリ効率を実現 |
