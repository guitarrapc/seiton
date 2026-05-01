# Seiton 設定ファイル (`seiton.yaml`) の取り扱い — 脅威調査と対策計画

> Seiton が読み込むリント設定（`.github/seiton.yaml`、`seiton.yaml`、`SEITON_CONFIG` / `--config` で指定されるファイル）について、攻撃ベクターと防御策を整理する計画ドキュメント。
>
> **スコープ**: `LintConfigLibrary` / `LintConfigYamlParser` による読込・検証、`network.github.ghes-api-url`、`fix.pinning.ignore-actions`、`exclusions` のグロブ、`NetworkConfig` を消費する HTTP 経路。
>
> **非スコープ**: ワークフロー本体 YAML のパーサ／ルール論理（それぞれ別 spec）。

---

## 0. 前提（脅威モデル）

| 信頼境界 | 想定 |
|---|---|
| **通常運用** | 設定ファイルはリポジトリのコードと同じ信頼枠で、コードレビュー対象となる。 |
| **高リスク** | `SEITON_CONFIG` や `--config` が、CI・スクリプト経由で**攻撃者制御パス**に向けられる場合（設定ファイルの単体での「インポート攻撃」に近い）。 |
| **前提にしないこと** | ローカルで任意のユーザーが自分用の YAML を読むだけの単独利用での「自分自身への攻撃」は主眼にしない（可用性のみ影響し得る）。 |

**コード実行・任意シェル起動への直結**は見当たらない。リスクは主に、(1) ネットワーク＋資格情報、(2) 正規表現の ReDoS、(3) リソース枯渇、(4) ルール抑止によるガバナンス迂回、に集約される。

---

## 1. 調査結果サマリ

### 1.1 評価マトリクス

| ID | 項目 | 深刻度（相対） | 主な経路・根拠 |
|---|---|---|---|
| **C-1** | `network.github.ghes-api-url` への **Bearer トークン付きリクエスト** | **高** | URL はトリムのみ。`https` / ホストの強制検証なし。`GithubActionShaResolver` / `OnlineAudit/ActionRefResolver` がトークン付き GET。意図しないホストへの到達やリダイレクト経由の漏えいが設計次第で現実になり得る。 |
| **C-2** | `fix.pinning.ignore-actions` の **`uses` / `ref` がそのまま正規表現**としてコンパイル | **中〜高（可用性）** | `RegexOptions.Compiled`。**マッチタイムアウト未指定**。悪意のあるパターンで ReDoS。`exclude-branches` は `Regex.Escape` でリテラル化されているが、`ignore-actions` のみユーザー正規表現。 |
| **C-3** | **リソース枯渇**（メモリ・時間・並列 HTTP） | **中** | `File.ReadAllText` にサイズ上限なし。`LintConfigYamlParser.ParseYamlDom` で入力と同サイズの `byte[]` を再確保。ネスト深度・コレクション要素数の明示上限なし。`network.max-concurrency` は `>0` のみで上限なし。`timeout-seconds` は負のみ拒否で、極大の正の値を許容。 |
| **C-4** | `exclusions` による **セキュリティルールの広域抑止** | **低（ガバナンス）** | 技術的脆弱性というより、「悪意ある／不注意な PR」で検知を丸ごと無効化し得る。 |
| **C-5** | `exclusions.files` の **グロブ**の計算コスト | **低** | `ActionRefHelpers.GlobMatch` はメモ化あり。極長パターン＋パスで辞書サイズ増。 |
| **C-6** | YAML アンカー／エイリアス等の **表現増幅** | **要確認** | VYaml の展開実装依存。ワークフロー側と別経路だが、設定入力が「小さく見えて巨大 DOM」になる経路があり得る。 |

### 1.2 相対的にリスクが低いと判断した点

- 設定のみから **任意のローカルパスへ直接 `File.Open` する**ような汎用 API は持たない（`files` はリント対象パス文字列へのグロブ一致）。
- 設定は **宣言的スカラー→型付きレコード**で、コードインジェクション用の実行フックはない。
- 設定発見は **決め打ちの相対パス**とカレント→親ディレクトリの走査に限られる。

---

## 2. 優先度付き対策案

優先度の目安:

- **P0**: 悪意ある設定がトークン漏えいや明確な越境につながりやすい → 早める価値が高い。
- **P1**: DoS／ReDoス等、実運用で再現しうる問題。
- **P2**: ガバナンス・ドキュメント・上限の細部。

### P0 — ネットワーク・資格情報

| # | 対策 | 内容 |
|---|---|---|
| P0-1 | **`ghes-api-url` の検証を厳格化** | **HTTPS のみ許可**。`Uri.TryCreate` 後に `Scheme == https` を必須とする（`http` / それ以外は設定エラー）。必要に応じ **ホストの allowlist**（例: 環境変数または設定の別キーで許可ドメイン列挙）を検討。 |
| P0-2 | **リダイレクトとトークン** | `HttpClient` のデフォルトリダイレクトで、**別オリジンへトークンが送られない**方針を明文化し、実装で保証する（カスタム `HttpMessageHandler` でクロスオリジンリダイレクト拒否等）。 |
| P0-3 | **ドキュメント** | `docs/configuration.md` / `Seiton_Linter_spec.md` 等で「`ghes-api-url` は信頼できる GHES のみ。PR に含まれる設定はレビュー対象」と明記。 |

**実装の置き場（参考）**: `LintConfigLibrary.NormalizeNetwork`、`GitHubActionShaResolver` / `ActionRefResolver` の `NormalizeApiBaseUri`、共有 HTTP クライアント設定。

### P1 — ReDoS・リソース上限

| # | 対策 | 内容 |
|---|---|---|
| P1-1 | **`ignore-actions` の正規表現** | 各 `Regex` に **`MatchTimeout`**（`TimeSpan`）を指定する。または **グロブ／サブセット言語に置き換え**、`(` `)` `|` `*` の深さ制限などで危険パターンを排除。 |
| P1-2 | **設定ファイルサイズ上限** | 例: **512 KiB 〜 1 MiB**（プロダクトで合意した値）。超過時は設定エラーで読まない。`File.ReadAllText` 前に長さチェック、またはストリーム読み。 |
| P1-3 | **`network.max-concurrency` の上限** | ホストの **論理プロセッサ数**（`Environment.ProcessorCount`、最低 **`1`**）まで。超過はクランプおよびエラー。 |
| P1-4 | **`network.timeout-seconds` の上限** | 例: **最大 300** 秒など。超過はクランプまたはエラー。 |
| P1-5 | **YAML DOM の防御** | パース後またはパース中に **最大深度・最大ノード数**（またはバイト数との組み合わせ）を超えたらエラー。VYaml のアンカー展開挙動を確認し、増幅攻撃があれば同じ枠で抑止。 |

### P2 — ガバナンス・観測性・軽微な改善

`seiton.yaml` が **入力の入り口**になり得るのは **Seiton を導入している利用者のリポジトリ／CI**。P2-1（CODEOWNERS 等）は **そちら側の運用推奨**であり、**seiton 上流リポジトリにファイルを追加する話ではない**（誤って上流に `CODEOWNERS` を置かない）。

**本リポジトリで行ったもの**と **利用者のみが行うもの**の整理:

| # | 対策 | 本リポジトリ | 利用者側（推奨の例） |
|---|---|---|---|
| P2-1 | **レビューポリシー** | ドキュメントで案内のみ | `exclusions` やオンラインルール無効化など。**独自**リポジトリの `.github/CODEOWNERS` とブランチ保護で `seiton.yaml` を保護 |
| P2-2 | **`SEITON_CONFIG`** | `docs/configuration.md` / `Seiton_Linter_spec.md` で信頼境界を記載 | 共有 CI・フォーク PR でパスと値を運用規定 |
| P2-3 | **DOM パース時のバッファ** | `LintConfigYamlParser.ParseYamlDom` で冗長 **`byte[]`** コピー削減 | （該当なし） |

---

## 3. 実装・検証のチェックリスト（着手時）

実装に入る際の最小確認:

- [x] P0: `ghes-api-url` が `http://` 等のとき **検証失敗**すること（HTTPS / ユーザー情報検証済み）。
- [x] P0: リダイレクト挙動: GitHub Bearer 経路では **同一オリジンの 3xx のみフォロー**する（クロスオリジンではフォローせず Bearer を再送信しない）。
- [x] P1: 悪意ある `ignore-actions` パターンで **プロセスが事実上固まらない**（タイムアウトまたは拒否）。
- [x] P1: 巨大設定ファイル・巨大 `exclusions` リストで **明確にエラー**または **上限内に収まる**。
- [x] P2: SEITON_CONFIG／ガバナンスを **利用者向け**にドキュメント化。**上流に CODEOWNERS は置かない**。`--verbose` の `config:`、`ParseYamlDom` の冗長コピー削減。
- [x] 回帰: 既存 `LintConfigLibraryTests` および fix / online 系テストが通ること。

---

## 4. 関連ファイル（調査時点）

| 領域 | ファイル例 |
|---|---|
| 読込・正規化 | `src/Seiton.Core/Linting/LintConfigLibrary.cs` |
| YAML → DOM | `src/Seiton.Core/Linting/LintConfigYamlParser.cs` |
| CLI 解決 | `src/Seiton/Config/CliConfigBridge.cs` |
| GHES + 正規表現（pin） | `src/Seiton.Core/Linting/PinRemediation/GitHubActionShaResolver.cs` |
| GHES（online audit） | `src/Seiton.Core/Linting/OnlineAudit/ActionRefResolver.cs` |
| グロブ | `src/Seiton.Core/Linting/ActionRefHelpers.cs`（`GlobMatch`） |
| 除外適用 | `src/Seiton.Core/Linting/LintEngine.cs`（`GlobMatch` と `NormalizedExclusion`） |

---

## 5. 変更履歴

| 日付 | 内容 |
|---|---|
| 2026-05-01 | 初版: 設定まわりの脅威調査結果と P0/P1/P2 対策案を文書化。 |
| 2026-05-01 | P0: `ghes-api-url` HTTPS 検証、GitHub 向け HttpClient の同一オリジン リダイレクト制限。 |
| 2026-05-01 | P1: 設定 UTF-8 1MiB 上限、YAML DOM 深さ/ユニット上限、`ignore-actions` Regex `MatchTimeout`、ネットワーク timeout/concurrency 上限。 |
| 2026-05-01 | `network.max-concurrency` 上限を固定 64 から **論理プロセッサ数** に変更。 |
| 2026-05-01 | P2（上流）: `SEITON_CONFIG`/CI の信頼境界ドキュメント、CLI `--verbose` の `config:` ログ、`LintConfigYamlParser` の冗長 **`byte[]`** コピー削減。 |
| 2026-05-01 | P2 の範囲修正: **`CODEOWNERS` は Seiton を導入した利用者リポジトリの運用**。上流 `.github/CODEOWNERS` を削除し計画書・ユーザー向けドキュメントでスコープを明確化。 |
