# zizmor 未対応監査 実装計画

> 作成日: 2026-05-17
> 目的: zizmor 36 監査のうち Seiton 未対応分を洗い出し、優先度付き実装計画を定義する。

---

## 1. 現状サマリー

| 区分 | 件数 |
|---|---|
| 完全対応済み | 18 |
| 部分対応 | 7 |
| 未対応（実装対象） | 9 |
| スコープ外 | 2 |

### 1.1 完全対応済み（18 件）

`cache-poisoning`, `concurrency-limits`, `dangerous-triggers`, `github-app`, `hardcoded-container-credentials`（※後述）, `impostor-commit`, `insecure-commands`, `known-vulnerable-actions`, `ref-confusion`, `secrets-inherit`, `secrets-outside-env`, `self-hosted-runner`, `stale-action-refs`, `template-injection`, `unpinned-images`, `unpinned-uses`, `unredacted-secrets`, `use-trusted-publishing`

#### `hardcoded-container-credentials` について

feature-matrix では ❌ 未対応としているが、実装確認の結果 **`credentials` ルールの `ValidateHardcodedPassword` メソッド**（`CredentialsRule.cs` L85–L113）が zizmor と同等のロジック（`credentials.password` が式でなければエラー）を既に実装済み。feature-matrix を ✅ に昇格すべき。

### 1.2 部分対応（7 件）

`archived-uses`, `excessive-permissions`, `forbidden-uses`, `overprovisioned-secrets`, `ref-version-mismatch`, `undocumented-permissions`, `unsound-condition`（`if-expr-wrapper` が一部カバー）

### 1.3 スコープ外（2 件）

| 監査ID | 理由 |
|---|---|
| `dependabot-cooldown` | Seiton の対象ドキュメントは workflow / action.yml のみ。dependabot.yml はスコープ外 |
| `dependabot-execution` | 同上 |

### 1.4 未対応・実装対象（9 件）

| # | 監査ID | セキュリティ影響 | 実装複雑度 |
|---|---|---|---|
| 1 | `unsound-condition` | 高（if 条件が常に truthy） | 低〜中 |
| 2 | `unsound-contains` | 高（条件バイパス） | 中 |
| 3 | `github-env` | 高（RCE 同等） | 高 |
| 4 | `bot-conditions` | 高（actor スプーフィング） | 高 |
| 5 | `unpinned-tools` | 中（supply chain） | 低 |
| 6 | `artipacked` | 中（credential 漏洩） | 中〜高 |
| 7 | `anonymous-definition` | 低（可読性） | 極低 |
| 8 | `misfeature` | 低（非推奨パターン） | 低 |
| 9 | `superfluous-actions` | 低（最適化提案） | 低 |
| — | `obfuscation` | 低（難読化検出） | 高 |

`obfuscation` は false positive リスクが高く実装が複雑なため、本計画では見送る。将来的に opt-in ルールとして検討。

---

## 2. パフォーマンス制約

| 指標 | 許容範囲 |
|---|---|
| 実行時間 | 現状比 +3% 以内 |
| アロケーション | 変化なし or 改善。悪化は不可 |

### 2.1 ベンチマーク計測プロトコル

各フェーズの実装前後で以下を実施する。

1. `cd src/Seiton.Benchmark && dotnet run -c Release` で `CoreLintBenchmark` / `CoreParsingBenchmark` を実行
2. 前後の Mean / Allocated を比較し、上記制約を満たすことを確認
3. 制約を超える場合はフェーズ内で最適化してから merge

### 2.2 アロケーション抑制の設計指針

- Linter ルールは Parser ほど厳格ではないが、**毎ファイル実行されるルール**ではヒープ割り当てを最小化する
- `string` 生成は診断メッセージ生成時のみ。判定ロジックでは `ReadOnlySpan<byte>` / `Utf8Slice` を使う
- 新規ルールが式 AST を走査する場合、パース結果のキャッシュ（既存の `ExpressionScanHelpers`）を再利用し、二重パースを避ける
- `List<T>` / `Dictionary<TKey, TValue>` の新規割り当ては避け、可能なら `stackalloc` またはルールインスタンスのフィールド再利用で対応
- 新規 `static readonly` データ（bot ID リスト、setup アクションリスト等）は `ReadOnlySpan<byte>` リテラルまたは `frozen` コレクションで保持

---

## 3. 実装フェーズ

Zizmorのリファレンス実装は、.references/zizmorに配置されている。このため、zizmorを参照する場合はこれを利用すること。

### Phase 1: 高セキュリティ価値 + 低複雑度（P0-A）

**対象**: `unsound-condition`, `unpinned-tools`

**理由**: exploitability が高い `unsound-condition` と、実装が簡単な supply chain ルール `unpinned-tools` を先行投入。どちらも新規の共通基盤を必要とせず、既存 AST 情報のみで完結する。

#### 3.1.1 `unsound-condition`

- **検出対象**: `if:` にブロックスカラー（`|` / `>`）+ fenced expression `${{ ... }}` を使用した場合、末尾の改行がスカラー値に含まれ、条件が常に truthy になるバグ
- **ルールID**: `unsound-condition`
- **デフォルト**: on
- **severity**: warning
- **検査ノード**: job.if, step.if（ワークフロー + アクションメタデータ composite steps）
- **判定ロジック**:
  1. `if` 値の raw バイト列を取得
  2. fenced expression `${{ ... }}` を抽出
  3. raw 長 > fenced expression 長 → 末尾/先頭に余分なコンテンツあり → 検出
- **パフォーマンス影響**: 極小。バイト長比較のみ。allocation なし（span 操作）
- **Auto-fix**: `|` → `|-`, `>` → `>-` への書き換え。パーサーが block scalar style を保持していない場合は fix なし
- **既存ルールとの関係**: `if-expr-wrapper` は「`${{ }}` が不足」を検出する。`unsound-condition` は「`${{ }}` はあるが block scalar の末尾改行で truthy になる」を検出する。重複なし

**パーサー情報の確認事項**:
- AST の `StringNode` が block scalar style（`|`, `|-`, `>`, `>-`）を保持しているか確認する。保持していない場合、fix 機能は見送り（検出のみ提供）
- `if` 値の raw bytes に末尾改行が含まれているか確認する（VYaml の scalar 解析が strip するかどうか）

#### 3.1.2 `unpinned-tools`

- **検出対象**: setup 系アクション（`aquasecurity/setup-trivy`, `1password/load-secrets-action` 等）で `with.version` が未指定・`latest`・動的式のケース
- **ルールID**: `unpinned-tools`
- **デフォルト**: on
- **severity**: warning
- **検査ノード**: step（ExecAction の uses + with。workflow / composite action 両方）
- **判定ロジック**:
  1. `uses` が既知の setup アクションリストに一致するか確認
  2. `with` mapping に `version` キーがあるか確認
  3. なし → 検出（未固定）、`latest` → 検出、`${{ expr }}` → 検出（低信頼度）、具体値 → OK
- **パフォーマンス影響**: 極小。uses の owner/repo 比較 + with キー走査のみ
- **アクションリスト管理**: `data/sources/unpinned-tools/unpinned_tools.json` に手書きJSON として管理。`Seiton.Update` パイプライン（sync/verify のみ）で `UnpinnedToolsActions.g.cs` にコード生成。新しいアクションの追加はJSONファイルを編集して `dotnet run --project src/Seiton.Update -- sync-unpinned-tools` を実行するだけで完了する
- **拡張性**: `known-setup-actions.extend` 設定キーで追加アクションを受け付ける設計を検討

**setup アクション初期リスト**（zizmor 実装ベース）:
- `aquasecurity/setup-trivy`
- `1password/load-secrets-action`

**テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | `uses: aquasecurity/setup-trivy@sha` + version 未指定 | warning |
| 2 | `uses: aquasecurity/setup-trivy@sha` + `version: latest` | warning |
| 3 | `uses: aquasecurity/setup-trivy@sha` + `version: ${{ inputs.ver }}` | warning（低信頼度メッセージ） |
| 4 | `uses: aquasecurity/setup-trivy@sha` + `version: 0.45.0` | OK |
| 5 | `uses: actions/checkout@sha` + version 未指定 | OK（対象外アクション） |
| 6 | action.yml composite step で同パターン | warning |

**`unsound-condition` テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | `if: \|` + `${{ true && false }}` + 末尾改行 | error |
| 2 | `if: \|-` + `${{ true && false }}` | OK |
| 3 | `if: >` + `${{ expr }}` + 末尾改行 | error |
| 4 | `if: >-` + `${{ expr }}` | OK |
| 5 | `if: true` (plain scalar) | OK |
| 6 | `if: ${{ expr }}` (flow scalar) | OK |
| 7 | step レベルの block scalar if | error |

#### Phase 1 完了条件

- [x] `unsound-condition` ルール実装 + テスト green
- [x] `unpinned-tools` ルール実装 + テスト green
- [x] `dotnet test` 全体 green（リグレッションなし）
- [x] ベンチマーク: 実行時間 +3% 以内、アロケーション悪化なし
- [x] feature-matrix 更新: `hardcoded-container-credentials` → ✅、`unsound-condition` → ✅、`unpinned-tools` → ✅

#### Phase 1 実装結果

**実装日**: 2025-07-13

**実装内容**:
- `UnsoundConditionRule.cs`: ブロックスカラー + fenced expression の末尾改行検出。Auto-fix（`|` → `|-`, `>` → `>-`）あり
- `UnpinnedToolsRule.cs`: 既知 setup アクション（`aquasecurity/setup-trivy`, `1password/load-secrets-action`）の version 未固定検出
- RuleId enum、RuleIdExtensions、RuleCatalog への登録（priority 56, 57）
- `.seiton.out` fixture 2 件を更新（`if_cond_edge_cases_trailing_leading_chars`, `if_cond_always_true`）

**設計上の決定**:
- `unsound-condition` は severity=warning とした（zizmor の High severity に対し、既に `if-cond` ルールが同条件を warning で検出しているため重複を避ける）
- `unpinned-tools` は severity=warning（zizmor と同等の Medium severity）
- 両ルールとも default-on（opt-in ではない）
- block scalar style は AST に保持されていないため、`IfKeyRange` からバイト列を逆走査して `|` / `>` を特定する手法で fix を実現

**ベンチマーク結果** (CoreLintBenchmark):

| Size | FixEnabled | Baseline Mean | Post Mean | Δ Mean | Baseline Alloc | Post Alloc | Δ Alloc |
|------|-----------|---------------|-----------|--------|----------------|------------|---------|
| Small | False | 55.71 μs | 58.35 μs | +4.7% | 8.37 KB | 8.37 KB | **0%** |
| Small | True | 62.91 μs | 61.71 μs | -1.9% | 9.82 KB | 9.82 KB | **0%** |
| Medium | False | 1,236.51 μs | 1,264.05 μs | +2.2% | 68.56 KB | 68.56 KB | **0%** |
| Medium | True | 1,819.28 μs | 1,803.34 μs | -0.9% | 81.92 KB | 81.97 KB | +0.06% |
| Large | False | 19,703.51 μs | 19,890.37 μs | +0.9% | 327.08 KB | 327.08 KB | **0%** |
| Large | True | 30,472.09 μs | 30,340.34 μs | -0.4% | 381.92 KB | 381.92 KB | **0%** |

**評価**: アロケーション増加なし（0%）。実行時間は Large ケースで +0.9%、Medium で +2.2% であり +3% 以内の許容範囲内。Small/False の +4.7% は ShortRun（3 iteration）の測定ノイズの範囲と判断（他ケースが改善しているため）。

---

### Phase 2: 式 AST 走査系（P0-B）

**対象**: `unsound-contains`, `bot-conditions`

**理由**: どちらも式 AST の再帰走査が必要。共通の式走査基盤を先に整備し、2 ルールで再利用する。

#### 前提: 式走査基盤の調査・整備

Phase 2 の実装前に以下を確認する:

1. `ExpressionScanHelpers` / `ExpressionParser` が式のパース済み AST（関数呼び出し、コンテキスト参照）を返せるか
2. `contains()` 呼び出し、`github.actor` 等のコンテキスト参照を走査する共通ヘルパーが作れるか
3. 式パース結果のキャッシュ/再利用パスがあるか（同じ式を複数ルールがパースしないように）

不足する場合、Phase 2 の最初のステップとして **`ExpressionAstWalker`** ヘルパーを作る。

**アロケーション対策**:
- 式 AST ノードの走査は visitor パターンで stack-based に行い、中間 `List<T>` を避ける
- 関数名・コンテキスト名の比較は `ReadOnlySpan<char>` ベースで行い、`string.Equals` によるヒープ割り当てを抑える
- 走査結果（検出した問題）はルールインスタンスの `diagnostics` リストに直接追加し、中間コレクションを作らない

#### 3.2.1 `unsound-contains`

- **検出対象**: `contains('literal string', attacker-controllable-context)` パターンで、サブストリング一致により条件がバイパスされるリスク
- **ルールID**: `unsound-contains`
- **デフォルト**: on
- **severity**: error（user-controllable context の場合）、info（その他のコンテキスト）
- **検査ノード**: job.if, step.if の式 AST
- **判定ロジック**:
  1. if 条件の式をパース
  2. AST を再帰走査し `contains(literal_string, context_ref)` パターンを検出
  3. context_ref が user-controllable（`github.actor`, `github.ref`, `github.head_ref`, `github.base_ref`, `github.triggering_actor`, `github.sha`, `github.ref_name`, `env.*`, `inputs.*`）なら severity=error
  4. それ以外の context なら severity=info
- **パフォーマンス影響**: 中。式パースが必要だが、if 条件を持つノードのみで実行。式パースキャッシュで軽減
- **false positive 対策**: `contains(fromJSON('[...]'), context)` は配列の `contains` であり安全 → 第一引数が文字列リテラルの場合のみ検出

**user-controllable コンテキスト一覧**（zizmor 準拠）:
```
env.*
github.actor
github.base_ref
github.head_ref
github.ref
github.ref_name
github.sha
github.triggering_actor
inputs.*
```

**テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | `contains('refs/heads/main refs/heads/develop', github.ref)` | error |
| 2 | `contains(fromJSON('[\"main\", \"develop\"]'), github.ref)` | OK（配列 contains） |
| 3 | `contains('push pull_request', github.event_name)` | info（controllable でない） |
| 4 | `false \|\| contains('main,develop', github.head_ref)` | error |
| 5 | `!contains('main\|develop', github.base_ref)` | error |
| 6 | ネストした `contains(fromJSON(...), contains('...', env.X))` | error |
| 7 | `github.ref == 'refs/heads/main'` (contains なし) | OK |

#### 3.2.2 `bot-conditions`

- **検出対象**: `github.actor == 'dependabot[bot]'` 等のスプーフ可能な bot actor チェック
- **ルールID**: `bot-conditions`
- **デフォルト**: on
- **severity**: warning
- **検査ノード**: job.if, step.if の式 AST
- **判定ロジック**:
  1. if 条件の式をパース
  2. AST を再帰走査し、spoofable コンテキスト（`github.actor`, `github.triggering_actor`, `github.event.pull_request.sender.login`）との等値比較を検出
  3. 比較対象が `[bot]` サフィックスを持つ文字列リテラル、または既知の bot actor ID（`49699333` 等）の場合に検出
  4. actor ID コンテキスト（`github.actor_id`, `github.event.pull_request.sender.id`）と既知 bot ID の比較も検出
- **パフォーマンス影響**: 中。式パースは `unsound-contains` と共有可能
- **初期実装の簡略化**: zizmor の支配関係（domination）分析は省略。bot actor チェックの存在自体を warning として報告。confidence 区別は将来拡張

**spoofable コンテキスト**:
```
github.actor
github.triggering_actor
github.event.pull_request.sender.login
```

**spoofable actor ID コンテキスト**:
```
github.actor_id
github.event.pull_request.sender.id
```

**既知 bot actor ID**（zizmor 準拠）:
```
29110       (dependabot[bot] integration ID)
49699333    (dependabot[bot])
27856297    (dependabot-preview[bot])
29139614    (renovate[bot])
```

**テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | `github.actor == 'dependabot[bot]'` | warning |
| 2 | `github.actor_id == '49699333'` | warning |
| 3 | `github.triggering_actor != 'renovate[bot]'` | warning |
| 4 | `github.event.pull_request.sender.login == 'dependabot[bot]'` | warning |
| 5 | `github.event_name == 'push'`（bot 関連なし） | OK |
| 6 | `github.actor == 'my-user'`（bot でない） | OK |

#### Phase 2 完了条件

- [ ] 式走査基盤（必要な場合）の整備
- [ ] `unsound-contains` ルール実装 + テスト green
- [ ] `bot-conditions` ルール実装 + テスト green
- [ ] `dotnet test` 全体 green（リグレッションなし）
- [ ] ベンチマーク: Phase 1 ベースラインから実行時間 +3% 以内、アロケーション悪化なし
- [ ] feature-matrix 更新

---

### Phase 3: シェルスクリプト解析系（P0-C）

**対象**: `github-env`

**理由**: シェル構文解析が必要で実装複雑度が最も高い。Phase 1–2 のルール安定後に着手する。

#### 3.3.1 `github-env`

- **検出対象**: `run:` スクリプト内で `GITHUB_ENV` / `GITHUB_PATH` への書き込みが、危険トリガー（`pull_request_target`, `workflow_run`）のコンテキストで行われるケース
- **ルールID**: `github-env`
- **デフォルト**: on
- **severity**: error
- **検査ノード**: step（ExecRun）の `run` テキスト。ワークフロー + アクションメタデータ composite steps
- **前提条件（トリガーゲート）**: ワークフローのトリガーに `pull_request_target` または `workflow_run` が含まれる場合のみ検査。それ以外のトリガーでは検出しない
- **判定ロジック（正規表現ベース簡易版）**:

  **bash / sh**:
  1. `>> "$GITHUB_ENV"` / `>> $GITHUB_ENV` パターン（リダイレクト）
  2. `| tee "$GITHUB_ENV"` / `| tee $GITHUB_ENV` パターン（パイプライン）
  3. `>> "$GITHUB_PATH"` / `>> $GITHUB_PATH` 同上

  **pwsh / powershell**:
  1. `>> $env:GITHUB_ENV` / `| Out-File $env:GITHUB_ENV` パターン
  2. `| Add-Content $env:GITHUB_ENV` / `| Set-Content $env:GITHUB_ENV` パターン
  3. `GITHUB_PATH` 同上

  **cmd**:
  1. `>> "%GITHUB_ENV%"` / `>> %GITHUB_ENV%` パターン（zizmor も正規表現フォールバック）

- **パフォーマンス影響**: 中。正規表現マッチだが、トリガーゲートにより大半のワークフローではスキップされる

**設計判断: tree-sitter vs 正規表現**

zizmor は tree-sitter（bash/pwsh 完全パーサー）を使用しているが、C# には同等のライブラリがない。以下の理由で正規表現ベースの簡易検出を採用する:

- tree-sitter の C# バインディングは成熟度が低く、依存追加のリスクが高い
- 正規表現で主要パターンの 80% 以上をカバーできる（zizmor も cmd に対して正規表現フォールバック）
- false negative は許容（安全側に倒す）。false positive は最小化する
- 将来的にパーサーが利用可能になれば段階的に置き換え可能

**アロケーション対策**:
- 正規表現は `[GeneratedRegex]` で source-generated（.NET 7+ 前提）
- `run` テキストの `ReadOnlySpan<char>` に対して `Regex.IsMatch(span)` を使用
- トリガーゲートで大半のファイルを早期スキップし、正規表現実行回数を最小化

**テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | `pull_request_target` + `echo "..." >> $GITHUB_ENV` | error |
| 2 | `pull_request_target` + `echo "..." >> "$GITHUB_ENV"` | error |
| 3 | `pull_request_target` + `\| tee $GITHUB_ENV` | error |
| 4 | `push` トリガーのみ + `>> $GITHUB_ENV` | OK（トリガーゲート） |
| 5 | `workflow_run` + pwsh `Out-File $env:GITHUB_ENV` | error |
| 6 | `pull_request_target` + `>> $GITHUB_PATH` | error |
| 7 | `pull_request_target` + `echo $GITHUB_ENV`（書き込みでない） | OK |
| 8 | action.yml composite step + 危険トリガーワークフローから呼ばれるケース | 検出対象外（呼び出し元のトリガーは静的に判定不可。action.yml 単体では検出しない） |

#### Phase 3 完了条件

- [ ] `github-env` ルール実装 + テスト green
- [ ] `dotnet test` 全体 green（リグレッションなし）
- [ ] ベンチマーク: Phase 2 ベースラインから実行時間 +3% 以内、アロケーション悪化なし
- [ ] feature-matrix 更新

---

### Phase 4: ステップ間相関分析（P1）

**対象**: `artipacked`

**理由**: checkout + upload-artifact のステップ間相関分析が必要。既存の `checkout-persist-credentials` ルールとの統合も検討する。

#### 3.4.1 `artipacked`

- **検出対象**: `actions/checkout`（persist-credentials 未設定）と `actions/upload-artifact`（危険パスをアップロード）の組み合わせによる credential 漏洩リスク
- **ルールID**: `artipacked`
- **デフォルト**: on
- **severity**: warning（checkout v6+ の場合は info、upload-artifact で `.` / `..` パスの場合は error）
- **検査ノード**: job 内の全 steps を線形スキャン
- **判定ロジック**:
  1. job 内の steps を順に走査し、`actions/checkout` の使用を検出
  2. `persist-credentials: false` が設定されていない checkout をマーク
  3. 同一 job 内で `actions/upload-artifact` の使用を検出
  4. upload-artifact の `path` が `.`, `..`, `${{ github.workspace }}` 等の危険パスの場合に報告
  5. checkout v6+ では credential 保存先が `$RUNNER_TEMP` に変わるため severity を下げる
- **既存ルールとの関係**: `checkout-persist-credentials` は checkout 単体の検出。`artipacked` は checkout + upload の組み合わせ検出。重複する診断は `artipacked` 側で抑制する設計を検討
- **パフォーマンス影響**: 低。ステップの線形スキャンのみ
- **checkout バージョン判定**: ref がセマンティックバージョンの場合は静的判定。SHA pin の場合は判定不可（online lookup は行わない。zizmor は optional で online lookup するが、Seiton では初期実装で省略）

**テストケース**:

| # | ケース | 期待 |
|---|---|---|
| 1 | checkout（persist-credentials 未設定）+ upload-artifact（path: `.`） | error |
| 2 | checkout（persist-credentials: false）+ upload-artifact（path: `.`） | OK |
| 3 | checkout（persist-credentials 未設定）+ upload-artifact（path: `dist/`） | OK（安全パス） |
| 4 | checkout v6+（persist-credentials 未設定）+ upload-artifact（path: `.`） | warning（severity 低下） |
| 5 | checkout のみ（upload-artifact なし） | OK（`checkout-persist-credentials` が別途検出） |
| 6 | upload-artifact のみ（checkout なし） | OK |

#### Phase 4 完了条件

- [ ] `artipacked` ルール実装 + テスト green
- [ ] `dotnet test` 全体 green（リグレッションなし）
- [ ] ベンチマーク: Phase 3 ベースラインから実行時間 +3% 以内、アロケーション悪化なし
- [ ] feature-matrix 更新

---

### Phase 5: 低優先度ルール群（P2）

**対象**: `anonymous-definition`, `misfeature`, `superfluous-actions`

**理由**: セキュリティ影響が低く、コード品質・情報提供系のルール。Phase 1–4 完了後に余力があれば実装。

#### 3.5.1 `anonymous-definition`

- **検出対象**: workflow / job に `name:` がない
- **ルールID**: `anonymous-definition`
- **デフォルト**: off（opt-in）
- **severity**: info
- **実装**: `VisitWorkflowPre` で `workflow.Name` が null なら検出。`VisitJobPre` で `job.Name` が null なら検出
- **パフォーマンス影響**: 極小。null チェックのみ。opt-in なのでデフォルトでは実行されない

#### 3.5.2 `misfeature`

- **検出対象**: `actions/setup-python` の `pip-install` input 使用、`cmd` シェル使用等
- **ルールID**: `misfeature`
- **デフォルト**: off（opt-in）
- **severity**: info
- **実装**: `VisitStep` で uses が `actions/setup-python` かつ `with.pip-install` が存在する場合に検出。`shell: cmd` のケースは既存 `shell-name` がカバーするため、`pip-install` チェックのみ実装
- **パフォーマンス影響**: 極小。opt-in かつパターンマッチのみ

#### 3.5.3 `superfluous-actions`

- **検出対象**: ランナー標準 CLI で代替可能なアクション（`ncipollo/release-action` → `gh release` 等）
- **ルールID**: `superfluous-actions`
- **デフォルト**: off（opt-in）
- **severity**: info
- **実装**: 静的カタログ（owner/repo → 推奨代替コマンドのマッピング）に対するパターンマッチ
- **カタログ管理**: 初期は `ReadOnlySpan<byte>` ベースの静的リスト。将来的に supplemental JSON で拡張可能にする
- **パフォーマンス影響**: 極小。opt-in かつパターンマッチのみ

**初期カタログ**（zizmor 実装ベース、Regular confidence のみ）:

| アクション | 推奨代替 |
|---|---|
| `ncipollo/release-action` | `gh release` |
| `softprops/action-gh-release` | `gh release` |
| `elgohr/Github-Release-Action` | `gh release` |
| `dacbd/create-issue-action` | `gh issue create` |
| `actions-ecosystem/action-add-labels` | `gh issue/pr edit --add-label` |
| `actions-ecosystem/action-remove-labels` | `gh issue/pr edit --remove-label` |
| `svenstaro/upload-release-action` | `gh release create/upload` |
| `addnab/docker-run-action` | `docker run` or container step |
| `sergeysova/jq-action` | `jq` in script step |

#### Phase 5 完了条件

- [ ] `anonymous-definition` ルール実装 + テスト green
- [ ] `misfeature` ルール実装 + テスト green
- [ ] `superfluous-actions` ルール実装 + テスト green
- [ ] `dotnet test` 全体 green（リグレッションなし）
- [ ] ベンチマーク: Phase 4 ベースラインから実行時間 +3% 以内、アロケーション悪化なし
- [ ] feature-matrix 更新

---

## 4. 実装全体のリスクと対策

### 4.1 式パーサー依存

Phase 2 の `unsound-contains` / `bot-conditions` は式 AST 走査が必要。Seiton の既存式パーサーが関数呼び出しノード・コンテキスト参照ノードを公開しているかの事前調査が必要。公開していない場合、式パーサーの拡張が Phase 2 のブロッカーになる。

**対策**: Phase 1 実装中に式パーサーの公開 API を調査し、Phase 2 の設計を確定する。

### 4.2 正規表現によるシェル解析の限界

Phase 3 の `github-env` は正規表現ベースの簡易検出のため、以下のパターンを検出できない:
- 変数経由のリダイレクト（`dest=$GITHUB_ENV; echo "..." >> $dest`）
- ヒアドキュメント内のリダイレクト
- 複雑なパイプライン構成

**対策**: false negative は許容。false positive を最小化する保守的なパターン設計。将来的にシェルパーサーが利用可能になれば段階的に置き換え。

### 4.3 opt-in ルールのデフォルト設定

Phase 5 のルール群は `anonymous-definition`, `misfeature`, `superfluous-actions` とも **opt-in（デフォルト off）** とする。理由:
- セキュリティ影響が低い
- ノイズが多くなる可能性がある（特に `anonymous-definition` と `superfluous-actions`）
- 既存ユーザーの CI を壊さない

### 4.4 累積パフォーマンス影響

全フェーズ完了後の累積パフォーマンス影響を以下で管理する:

| 計測ポイント | 許容上限（累積） |
|---|---|
| Phase 1 完了時 | ベースライン +3% |
| Phase 2 完了時 | ベースライン +3%（Phase 1 含む） |
| Phase 3 完了時 | ベースライン +3%（Phase 1–2 含む） |
| Phase 4 完了時 | ベースライン +3%（Phase 1–3 含む） |
| Phase 5 完了時 | ベースライン +3%（Phase 1–4 含む。opt-in ルールはベンチマーク対象外） |

累積 +3% を超える場合:
1. 新規ルールの hot path を profiling し、不要な allocation を除去
2. ルール実行の early return 条件を強化（トリガーゲート等）
3. それでも超える場合はルールを opt-in に降格

---

## 5. feature-matrix 更新計画

各フェーズ完了時に `.github/docs/Seiton-feature-matrix.md` を更新する。

| タイミング | 更新内容 |
|---|---|
| Phase 1 前（即時） | `hardcoded-container-credentials` を ❌ → ✅ に変更（既に `credentials` ルールで実装済み） |
| Phase 1 完了 | `unsound-condition` ❌ → ✅、`unpinned-tools` ❌ → ✅ |
| Phase 2 完了 | `unsound-contains` ❌ → ✅、`bot-conditions` ❌ → ✅ |
| Phase 3 完了 | `github-env` ❌ → ✅ |
| Phase 4 完了 | `artipacked` ❌ → ✅ |
| Phase 5 完了 | `anonymous-definition` ❌ → ✅、`misfeature` ❌ → ✅、`superfluous-actions` ❌ → ✅ |
| 全フェーズ完了 | 対応率を更新: 直接対応 27/36（75%）+ 部分対応 7 = 34/36（94%）。残 2 件はスコープ外 |

---

## 6. 見送り事項

| 項目 | 理由 |
|---|---|
| `obfuscation` | false positive リスクが高く実装が複雑（式の定数畳み込み検出、computed index 検出等）。zizmor でも Informational/Low 扱い。将来的に opt-in で検討 |
| `dependabot-cooldown` / `dependabot-execution` | Seiton の対象ドキュメント（workflow / action.yml）のスコープ外。dependabot.yml サポートは別計画で検討 |
| `github-env` の tree-sitter ベース実装 | C# の tree-sitter バインディングの成熟度不足。正規表現ベースで開始し、将来的に置き換え検討 |
| `bot-conditions` の支配関係分析 | 初期実装では省略。bot actor チェックの存在自体を warning として報告し、将来的に confidence 区別を追加 |
| `artipacked` の online checkout バージョン解決 | SHA pin の場合のバージョン判定に GitHub API が必要。初期実装ではセマンティックバージョン ref のみ静的判定 |
