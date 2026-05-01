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

### 1.1 評価マトリクス（計画初版時点・対策実装**前**の整理）

| ID | 項目 | 深刻度（相対） | 主な経路・根拠 |
|---|---|---|---|
| **C-1** | `network.github.ghes-api-url` への **Bearer トークン付きリクエスト** | **高** | URL はトリムのみ。`https` / ホストの強制検証なし。`GithubActionShaResolver` / `OnlineAudit/ActionRefResolver` がトークン付き GET。意図しないホストへの到達やリダイレクト経由の漏えいが設計次第で現実になり得る。 |
| **C-2** | `fix.pinning.ignore-actions` の **`uses` / `ref` がそのまま正規表現**としてコンパイル | **中〜高（可用性）** | `RegexOptions.Compiled`。**マッチタイムアウト未指定**。悪意のあるパターンで ReDoS。`exclude-branches` は `Regex.Escape` でリテラル化されているが、`ignore-actions` のみユーザー正規表現。**→ 2026-05-02 に Regex 全廃で完全解決**。 |
| **C-3** | **リソース枯渇**（メモリ・時間・並列 HTTP） | **中** | `File.ReadAllText` にサイズ上限なし。`LintConfigYamlParser.ParseYamlDom` で入力と同サイズの `byte[]` を再確保。ネスト深度・コレクション要素数の明示上限なし。`network.max-concurrency` は `>0` のみで上限なし。`timeout-seconds` は負のみ拒否で、極大の正の値を許容。 |
| **C-4** | `exclusions` による **セキュリティルールの広域抑止** | **低（ガバナンス）** | 技術的脆弱性というより、「悪意ある／不注意な PR」で検知を丸ごと無効化し得る。 |
| **C-5** | `exclusions.files` の **グロブ**の計算コスト | **低** | `ActionRefHelpers.GlobMatch` はメモ化あり。極長パターン＋パスで辞書サイズ増。 |
| **C-6** | YAML アンカー／エイリアス等の **表現増幅** | **要確認** | VYaml の展開実装依存。ワークフロー側と別経路だが、設定入力が「小さく見えて巨大 DOM」になる経路があり得る。 |

※ **§3 はすべて完了**。上表は歴史的コンテキスト。対策適用後の当該項の要約・**残存ベクター**は **§1.2** と **§5**。

### 1.2 対策実施後の当該項の状態（2026‑05‑01 時点の整理）

| ID | 状態 |
|---|---|
| **C-1** | **部分緩和**: HTTPS 必須、URL 埋め込み資格情報の拒否、`GitHubApiHttpClientFactory`＋同一オリジン リダイレクトのみ。**未解決の本質**: 設定を書き換えられる主体が、`SEITON_GITHUB_TOKEN` / `GITHUB_TOKEN` を付けて**任意の「正しい HTTPS」ホスト**へ送らせられる（ホスト allowlist は未実装）→ **§5.1** |
| **C-2** | **解決済み**: Regex を全廃し wildcard マッチング / 完全一致 / 部分文字列一致に置換。ReDoS リスクは完全に排除 → **§5.2** |
| **C-3** | **緩和**: `LintConfigResourceLimits`（サイズ／深さ／構造単位数／timeout≤300／concurrency≤CPU）、`ValidateFile` 事前サイズ。**残り**: グロブ探索の細部、設定 DOM 生成と VYaml イベント・アンカー相互作用の検証余地 → **§5.3** |
| **C-4** | **運用**: ドキュメントで利用者リポのレビューを推奨（上流に CODEOWNERS は配置しない）。 |
| **C-5** | 設定規模上限で **過剰パターン**は抑制。**理論上** `GlobMatch` の内部キャッシュやパスとの組み合わせでのコストは残る → **§5.3** |
| **C-6** | **Limiter** で複製 DOM の実効サイズ・深さには上限。**備考**: VYaml と limiter の境界のより厳密な論証が必要なら別タスクとして追跡可 → **§5.3** |

### 1.3 相対的にリスクが低いと判断した点

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
| P1-1 | **`ignore-actions` のパターンマッチング** | **完了**: Regex を全廃し **wildcard マッチング**（`*` / `?`）に置換。ReDoS リスクなし。`exclude-branches` は `string.Equals` 完全一致、CLI `--ignore` は `string.Contains` 部分文字列一致に変更。 |
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

## 3. 実装・検証のチェックリスト（**2026‑05 時点すべて完了**）

計画どおり完了した項目（再掲）:

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

## 5. 対策実施後の Config 経路の攻撃ベクター（深掘り・残存）

前提: **ワークフロー本体 YAML** と **ワークフロー用パーサ**はスコープ外。ここでは **`LintConfig` に由来する入力**のみ。

### 5.1 ネットワーク＋資格情報（構成可能な送信先）

| 項 | 内容 |
|---|---|
| **悪意ある `ghes-api-url` と Bearer** | **依然として主要リスク**。P0 で `https` 必須・UserInfo 禁止・クロスオリジンへのリダイレクト経由でのトークンの再送信を抑止したが、**信頼できないホストへの「直接」HTTPS リクエスト**は許容モデルとなる（正当なユーザが自社 GHES に向ける機能と区別できないため）。設定を細工できる攻撃者は、**自分のサーバ**に GH API とは限らないが **Authorization: Bearer が付いたリクエスト**を送らせ得る。**追加緩和**としては環境別 **ホスト allowlist**、またはプライベートネットワーク向け明示オプション等が検討対象。**発火条件**: ネットワーク有効（`fix` / online／pinning）かつ環境変数にトークン。 |
| **SSRF との境界** | 意図的に **`https`** のみ許可 は、クラウドプロバイダの **`http`** メタデータ等への素朴な SSRF は出しにくいが、社内 **`https`** サービスへ **トークン付き GET** が飛ぶ「信頼境界の広がり」は残る。**CI のトークン権限最小化**と **設定のレビュー**が実効性の中心。 |
| **OCI / コンテナ画像解決** | `OciImageDigestResolver` は **ワークフロー内の画像参照からレジストリ URL を構成**する。`fix.images.*` は主にスキップ用のリスト／グロブで、**設定だけで任意 URL を指す経路ではない**。ただし **`new HttpClient()`**（クロスオリジン自動リダイレクト許容）は **レジストリ**向けであり、ワークフローが悪意あるレジストリを参照する場合との**合成リスク**はワークフロー側とセットで評価。 |
| **タイムアウト** | `PinRemediationEngine` は `CancellationTokenSource.CancelAfter(network.timeout)` で **1 解決試行あたり**上限。`OnlineAuditEngine` も `TimeoutSeconds` を使用。 **`0` は無期限**になり得る点は設定側の運用依存。 |

### 5.2 パターンマッチング（可用性）

| 項 | 内容 |
|---|---|
| **`fix.pinning.ignore-actions`** | **Regex を全廃し wildcard マッチング**（`*` / `?`）に置換。ReDoS リスクは完全に排除。アルゴリズムは決定的で指数爆発しない。 |
| **`OnlineAuditEngine`** | 同じ wildcard マッチング。`ShouldIgnore` は単純なループで、タイムアウト例外なし。 |
| **CLI `--ignore`** | **部分文字列一致**（`string.Contains`, 大文字小文字無視）に置換。Regex 不使用。 |
| **`exclude-branches`** | **完全一致**（`string.Equals`, ordinal）に置換。Regex 不使用。 |

### 5.3 リソース・パース・グロブ

| 項 | 内容 |
|---|---|
| **設定サイズ／DOM** | UTF‑8 **1 MiB**、**深度 64**、**構造 50 000** で **設定のみの増幅には実効的**。 |
| **`ParseYamlDom` とバッファ** | 通常経路では **`LintConfig.Utf8Yaml` と同一バッファ**で VYaml が走る（別経路のみ `ArrayPool`）。ワークフロー用パーサと同様、**ソースバイト列の読み取りのみ**という前提が崩れれば別問題。 |
| **YAML アンカー・エイリアス** | 設定 DOM は VYaml で構築。アンカーで **ソース上スモールでもイベント列が伸びる**可能性は、`YamlDomParseLimiter` と **総バイト上限**で大部分を抑止。**形式的な証明まで**は本章では行わず、異常入力に対する回帰が欲しければ専用フィクスチャで追加可能。 |
| **`GlobMatch`** | `PatternIndex` と `PathIndex` の **`Dictionary`** キャッシュ。**1 試行ごと**に増える。**極長パターン×多ファイル**での CPU／メモリは、設定サイズ／エントリ上限で間接的に抑えられるが、ギリギリのケースでのプロファイルは検討余地あり。 |

### 5.4 ガバナンス・信頼境界（技術よりプロセス）

| 項 | 内容 |
|---|---|
| **`exclusions`** | セキュリティルールの **広域ミュート**。技術的 Exploit ではなく **検知迂回**。 |
| **ルール `enabled`** | オンライン監査など **オプトイン**をオフ→悪影響は主にコンプライアンス。 |
| **`SEITON_CONFIG` / `--config`** | **任意ファイルパス**を指す。**信頼できない入力でパスを組み立てない**こと。**フォーク PR** ではチェックアウトされる設定だけが混入し得る。 |
| **`--verbose` の `config:`** | 絶対パスを stderr に出力。**ログ集約環境でのパスの取り扱い**は利用者側のポリシー。 |

---

## 6. 変更履歴

| 日付 | 内容 |
|---|---|
| 2026-05-01 | 初版: 設定まわりの脅威調査結果と P0/P1/P2 対策案を文書化。 |
| 2026-05-01 | P0: `ghes-api-url` HTTPS 検証、GitHub 向け HttpClient の同一オリジン リダイレクト制限。 |
| 2026-05-01 | P1: 設定 UTF-8 1MiB 上限、YAML DOM 深さ/ユニット上限、`ignore-actions` Regex `MatchTimeout`、ネットワーク timeout/concurrency 上限。 |
| 2026-05-02 | P1: **Regex 全廃**。`ignore-actions` を wildcard (`*`/`?`) に、`exclude-branches` を完全一致に、CLI `--ignore` を部分文字列一致に置換。`IgnoreActionRegexPatterns` 削除、`LintConfigResourceLimits` から Regex タイムアウト定数削除。 |
| 2026-05-01 | `network.max-concurrency` 上限を固定 64 から **論理プロセッサ数** に変更。 |
| 2026-05-01 | P2（上流）: `SEITON_CONFIG`/CI の信頼境界ドキュメント、CLI `--verbose` の `config:` ログ、`LintConfigYamlParser` の冗長 **`byte[]`** コピー削減。 |
| 2026-05-01 | P2 の範囲修正: **`CODEOWNERS` は Seiton を導入した利用者リポジトリの運用**。上流 `.github/CODEOWNERS` を削除し計画書・ユーザー向けドキュメントでスコープを明確化。 |
| 2026-05-01 | §1.1 を対策**前**として明記。**§1.2** と **§5** に対策後の要約・残存攻撃ベクターを追記。 |
| 2026-05-01 | **§5.2 実装**: `OnlineAuditEngine` の ignore 用 Regex を `MatchTimeout` 化＋タイムアウト時は監査継続。**`fix.pinning.ignore-actions`** を第 4 引数で適用可能に。CLI `--ignore` に同タイムアウト。共通化 `IgnoreActionRegexPatterns`。 |
| 2026-05-02 | **§5.2 実装**: Regex を全廃。`ignore-actions` は wildcard マッチング、`exclude-branches` は完全一致、CLI `--ignore` は部分文字列一致に置換。`IgnoreActionRegexPatterns` 削除。ReDoS リスク完全排除。 |
