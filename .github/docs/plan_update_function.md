# actionlint Correctness Parity 実装計画

## 背景と目的

`documents/index.md` の比較表では actionlint に対して Seiton が上位互換に見える書き方になっているが、実際には actionlint が持つ **correctness チェックの深さ** と **template injection の精度** で Seiton が劣る領域がある。

本文書では、actionlint との機能差のうち shellcheck/pyflakes 連携を除く全項目をカバーするための実装計画を示す。

### 対象ギャップ一覧

| # | ギャップ | actionlint | Seiton 現状 | 影響度 |
|---|---|---|---|---|
| G1 | cron 式構文検証 + 最小間隔チェック | ✓ (5分制限, timezone 検証) | ✗ | 中 |
| G2 | ローカル action.yml の input/output 解決 | ✓ (required/deprecated/unknown) | ✗ (存在チェックのみ) | 大 |
| G3 | 非推奨 action runner 検出 (node12/16) | ✓ | ✗ | 中 |
| G4 | workflow_dispatch input 制約 | ✓ (choice 重複, 型チェック, 25個上限) | ✗ | 中 |
| G5 | 重複 step ID 検出 | ✓ (case-insensitive) | ✗ | 小 |
| G6 | template injection の精度 | untrusted path ツリー + safe function 認識 | github.event.* 全体をブランケットフラグ | 大 |
| G7 | ID 命名の先頭文字制約 | 先頭は英字 or `_` のみ | 全位置 `[a-zA-Z0-9_-]` のみ | 小 |

**対象外**: shellcheck/pyflakes 連携（外部プロセス依存のため意図的に対象外）。

---

## 1. 調査結果

### G1: cron 式構文検証

**actionlint の実装**:
- `robfig/cron/v3` ライブラリで5フィールド cron をパース。パース失敗時にエラー。
- epoch から 2 連続発火時刻を算出し、差が 300 秒未満ならエラー（`"the shortest interval is once every 5 minutes"`）。
- timezone を `time.LoadLocation()` で IANA 検証。空文字/`"UTC"`/`"Local"` を不正として拒否。

**Seiton の現状**:
- パーサー (`WorkflowParser.On.cs`) が `ScheduleEntry { Cron, Timezone }` AST ノードを正しく生成済み。
- cron 文字列の構文検証なし、interval チェックなし、timezone 検証なし。
- これらはリンタールールで実装すべき（パーサーは構造を取得済み）。

**実装方針**:
- 新ルール `schedule-event` を追加。
- 5フィールド cron のパースは自前で実装する（各フィールド: 分 0-59, 時 0-23, 日 1-31, 月 1-12, 曜日 0-6、`*`, `*/N`, `N-M`, `N,M` の組み合わせ）。
  - 既にカバレッジされているcronパースに必要な構文はシンプルで、外部ライブラリ不要。
- 最小間隔チェック: GitHub Actions の制限は5分だが、正確な interval 算出は複雑。actionlint と同様に2回分の発火時刻からdiffを計算するか、あるいは各フィールドの最小刻みから下限を推定する簡易方式にする。
- timezone 検証: `TimeZoneInfo.FindSystemTimeZoneById()` もしくは IANA 名リストとの照合。NativeAOT + `InvariantGlobalization` 環境では `TimeZoneInfo` の動作が制限されるため、IANA 名の静的リストでの照合が安全。

### G2: ローカル action.yml の input/output 解決

**actionlint の実装**:
- `LocalActionsCache` が `./path` を解決し、`action.yml`/`action.yaml` を読み込み → `ActionMetadata` に parse → キャッシュ。
- `checkAction()` で input 検証: unknown input エラー、required input 欠落エラー、deprecated input 警告。
- `checkLocalActionRuns()` で `runs.using` 検証: `docker`/`composite`/`node20`/`node24` のみ有効、それ以外エラー。
- `checkLocalActionMetadata()` で `name`/`description` 必須、`branding` 検証。

**Seiton の現状**:
- `UnpinnedUsesRule` でローカルアクションのディレクトリ存在 + `action.yml` 存在チェックのみ。
- `PopularActionInputsRule` はリモート popular actions のみ対応、ローカルは未対応。
- パーサーの `ParseMode.ActionMetadata` は存在するが、action-metadata キーを `SkipCurrentNode()` でスキップしている。typed AST なし。
- **既存パターン**: `ReusableWorkflowRule` にローカルファイルの cross-file 解決パターンがある（ファイル読み込み → パース → キャッシュ → 契約検証）。

**実装方針**:
- **Phase A**: Action Metadata パーサーの実装
  - `ActionMetadata` AST 型を新規追加（`Name`, `Description`, `Inputs` (map), `Outputs` (map), `Runs` sub-type）
  - `ActionMetadataInput`: `Description?`, `Required` (bool), `Default?`, `DeprecationMessage?`
  - `ActionMetadataRuns`: `Using` (string), `Main?`, `Pre?`, `Post?`, `PreIf?`, `PostIf?`, `Image?`, `Steps` exists flag
  - `WorkflowParser` の `ParseMode.ActionMetadata` パスで実際にパースするよう変更
- **Phase B**: ローカルアクション解決ルール
  - 既存の `PopularActionInputsRule` を拡張するか、新ルール `local-action-inputs` を追加
  - `ReusableWorkflowRule` と同パターンの `LocalActionCache` を実装（ファイル読み込み → `ActionMetadata` パース → キャッシュ）
  - 検証: unknown input, missing required input, deprecated input 警告
  - `runs.using` の検証（有効値チェック）

### G3: 非推奨 action runner 検出

**actionlint の実装**:
- `OutdatedPopularActionSpecs` — `owner/repo@ref` の静的 map（150+ エントリ）。マッチしたらエラー。
- ローカルアクションの `runs.using` 検証で `node20`/`node24` 以外の `node*` をエラーにする。

**Seiton の現状**: 該当する検出なし。

**実装方針**:
- **approach A (outdated remote actions)**: `PopularActions` の生成データに `outdated` フラグを追加する方法。または `PopularActionInputsRule` で version 比較する方法。
  - しかし actionlint の `OutdatedPopularActionSpecs` は150+エントリの固定リストであり、メンテナンスの持続性に疑問がある。
  - **別アプローチ**: ローカルアクション解決（G2）で `runs.using` を検証する際に、`node12`/`node16` 等の非推奨 runner を検出する方が汎用的。リモートアクションについては `unpinned-uses` ルールが SHA ピンニングを要求しており、古いバージョンはそもそもピンニング更新の対象になるため、優先度は低い。
- **推奨**: G2 のローカルアクション解決の一部として `runs.using` の非推奨 runner チェックを実装する。リモートアクションの outdated 検出は将来的にデータ駆動で対応可能とする。

### G4: workflow_dispatch input 制約

**actionlint の検証項目**:
1. `choice` 型で `options` 未設定 → エラー
2. `options` 内の重複値 → エラー
3. `choice` 型の `default` が `options` に含まれない → エラー
4. `choice` 型以外で `options` が設定されている → エラー
5. `number` 型の `default` が float にパースできない → エラー
6. `boolean` 型の `default` が `"true"`/`"false"` でない → エラー
7. 25 個を超える inputs → エラー

**Seiton の現状**:
- パーサーが `DispatchInput` AST を正しく生成済み（`Type`, `Options`, `Default`, `Required` 含む）。
- 上記 7 項目のいずれも検証していない。

**実装方針**:
- 既存の `GlobPatternRule`（イベント検証ルール）を拡張するか、新ルール `dispatch-inputs` を追加。
- AST に必要な情報は全て揃っているため、リンタールールの追加のみ。パーサー変更不要。
- 7 項目を `VisitEvent` フック内で `WorkflowDispatchEvent` に対して実装。

### G5: 重複 step ID 検出

**actionlint の実装**:
- `VisitJobPre` で `seen` map を初期化、`VisitStep` で lowercase にして重複検出。case-insensitive。

**Seiton の現状**:
- `IdNamingRule` が文字セット検証のみ。重複検出なし。

**実装方針**:
- `IdNamingRule` に重複検出を追加。
- `VisitJobPre` で `Dictionary<string, TextRange>` を初期化。
- `VisitStep` で `step.Id` を lowercase にして辞書チェック。既存ならエラー。
- 非常に小さな変更。

### G6: template injection の精度向上

**actionlint の実装**:
- `BuiltinUntrustedInputs` — ツリー構造の untrusted path 定義。leaf ノードのみがエラーをトリガー。
- `UntrustedInputChecker` — expression AST をウォークし、`cur` リストで現在のツリー位置を追跡。
  - `VariableNode` → root 検索、`ObjectDerefNode` → 子ノード進行、`IndexAccessNode` (文字列リテラル) → プロパティアクセスと同等、`IndexAccessNode` (数値/式) → array element (`*`)、`ArrayDerefNode` → object filter fan-out
  - chain の終端でいずれかの `cur` が leaf なら → エラー。
- Safe function 認識: `contains`/`startsWith`/`endsWith` 内の untrusted ref は抑制。`safeCalls` カウンター。
- **untrusted path の完全なリスト**:
  - `github.event.issue.title`, `github.event.issue.body`
  - `github.event.pull_request.title`, `github.event.pull_request.body`, `github.event.pull_request.head.ref`, `github.event.pull_request.head.label`, `github.event.pull_request.head.repo.default_branch`
  - `github.event.comment.body`
  - `github.event.review.body`, `github.event.review_comment.body`
  - `github.event.pages.*.page_name`
  - `github.event.commits.*.message`, `github.event.commits.*.author.email`, `github.event.commits.*.author.name`
  - `github.event.head_commit.message`, `github.event.head_commit.author.email`, `github.event.head_commit.author.name`
  - `github.event.discussion.title`, `github.event.discussion.body`
  - `github.head_ref`

**Seiton の現状**:
- `TemplateInjectionRule` が `github.event.*` 全体をブランケットフラグ。leaf 判定なし。
- safe function 認識なし。
- `github.head_ref` をチェックしていない。
- `run:` シンクのみ対応。

**実装方針**:
- `UntrustedInputTree` 静的データ構造を定義。`Dictionary<string, UntrustedInputNode>` のネスト。leaf ノードは `Children == null`。
- `TemplateInjectionRule` の `IsGithubEventReference()` を置き換え:
  - expression AST のウォーク時に `cur` リスト（マッチ中のツリー位置）を追跡するステートマシンに変更。
  - `Identifier` → root 検索、`MemberAccess` → 子ノード進行、`IndexAccess` (文字列リテラル) → プロパティアクセス相当、`IndexAccess` (数値) → `*` 要素、`WildcardAccess` → fan-out。
  - chain の終端で leaf 判定。
- safe function 認識を追加: `safeCalls` カウンターで `contains`/`startsWith`/`endsWith` 内を抑制。
- `github.head_ref` をルートツリーに追加。

### G7: ID 命名の先頭文字制約

**actionlint**: `^[a-zA-Z_]` — 先頭は英字 or `_`。数字や `-` で始まる ID はエラー。
**Seiton**: 全位置で `[a-zA-Z0-9_-]` のみチェック。先頭文字の制約なし。

**実装方針**:
- `IdNamingRule.IsValidId()` に先頭文字チェック（`IsAsciiLetter || '_'`）を追加。

---

## 2. ロードマップ

### Phase 1: 小規模ルール追加（G4, G5, G7）

**目的**: パーサー変更不要で、リンタールール追加のみで解消できるギャップを一括で埋める。

**What**:

#### G4: `dispatch-inputs` ルール（新規）
- `VisitEvent` で `WorkflowDispatchEvent` を処理。
- 7 項目の検証ロジックを実装:
  1. choice 型で options 未設定
  2. options 内の重複値
  3. choice 型の default が options に含まれない
  4. choice 型以外で options が設定されている
  5. number 型の default が数値でない
  6. boolean 型の default が "true"/"false" でない
  7. 25 個を超える inputs

#### G5: `IdNamingRule` に重複 step ID 検出を追加
- `VisitJobPre` で `Dictionary<string, TextRange>` を初期化。
- `VisitStep` で step ID を lowercase にして重複チェック。

#### G7: `IdNamingRule` に先頭文字制約を追加
- `IsValidId()` の先頭バイトチェックを `IsAsciiLetter(b) || b == '_'` に変更。

**完了条件**:
- [x] choice 型で options 未設定がエラーになること
- [x] options 内の重複値がエラーになること
- [x] choice 型の default が options に含まれないときエラーになること
- [x] choice 型以外で options 設定がエラーになること
- [x] number 型の default が数値でないときエラーになること
- [x] boolean 型の default が "true"/"false" でないときエラーになること
- [x] inputs が 25 個を超えるとエラーになること
- [x] 同一 job 内の重複 step ID が case-insensitive でエラーになること
- [x] 数字や `-` で始まる ID がエラーになること
- [x] 全テスト通過

---

### Phase 2: cron 式検証（G1）

**目的**: schedule イベントの cron 構文/interval/timezone を検証する。

**What**:

#### G1: `schedule-event` ルール（新規）
- `VisitEvent` で `ScheduledEvent` を処理。
- cron 式の 5 フィールドパーサーを自前実装:
  - 各フィールド: `*`, `N`, `N-M`, `N/step`, `N-M/step`, `N,M,...` の文法
  - 範囲: 分 (0-59), 時 (0-23), 日 (1-31), 月 (1-12), 曜日 (0-6 / 0-7 で 0,7 は日曜)
  - 月の名前 (`jan`-`dec`) と曜日の名前 (`sun`-`sat`) の対応
- 最小間隔チェック:
  - エポックから 2 連続発火時刻を算出し、差が 300 秒未満ならエラー。
  - 実装: `DateTime` ベースでの次回発火時刻計算（`GetNextFireTime`）。
- timezone 検証:
  - NativeAOT + `InvariantGlobalization` 環境では `TimeZoneInfo` が制限されるため、IANA timezone 名の静的リスト（~500 エントリ）との照合。
  - 空文字列は有効（デフォルト）。

**完了条件**:
- [x] 不正な cron 式（フィールド不足、範囲外値、不正な構文）がエラーになること
- [x] 5分未満の間隔がエラーになること
- [x] 不正な timezone がエラーになること
- [x] 全テスト通過

---

### Phase 3: template injection 精度向上（G6）

**目的**: template injection の検出精度を actionlint 水準に引き上げる。

**What**:

#### ステップ 1: UntrustedInputTree データ構造
- `UntrustedInputNode` レコード: `Name`, `Children` (Dictionary or null)
- `UntrustedInputTree` static class に `Roots` を定義。actionlint の `BuiltinUntrustedInputs` と同等のツリーを構築。
- 将来的に `data/sources/` 配下の JSON からの自動生成も可能（ただし現時点は手書きで十分 — untrusted path は actionlint でも手書き）。

#### ステップ 2: TemplateInjectionRule の精度改善
- `IsGithubEventReference()` を `CheckUntrustedInput()` に置き換え。
- expression AST ウォークのステートマシン:
  - `List<UntrustedInputNode>` で現在のマッチ中ツリー位置を追跡。
  - `Identifier` → root 検索、`MemberAccess` → 子ノード進行、`IndexAccess` (文字列リテラル) → プロパティアクセス、`IndexAccess` (数値) → `*` 要素探索、`WildcardAccess` → fan-out。
  - chain 終端（leaf 到達）でエラー発行。
- `github.head_ref` をツリーに追加（現在完全に未検出）。

#### ステップ 3: safe function 認識
- `safeCalls` int カウンターを追加。
- `FunctionCall` ノードの callee が `contains`/`startsWith`/`endsWith` (case-insensitive) なら、子ノードの検査期間中 `safeCalls` をインクリメント。
- `safeCalls > 0` の間は untrusted 検出を抑制。

**完了条件**:
- [x] `github.event.issue.title` は検出されるが `github.event.number` は検出されないこと
- [x] `github.event.pull_request.head.ref` が検出されること
- [x] `github.event.commits[0].message` が検出されること
- [x] `github.event.commits.*.author.name` が検出されること
- [x] `github.head_ref` が検出されること
- [x] `contains(github.event.issue.title, 'keyword')` は検出されないこと（safe function）
- [x] `startsWith(github.event.pull_request.head.ref, 'feature/')` は検出されないこと
- [x] `format('{0}', github.event.issue.title)` は検出されること（unsafe function）
- [x] 全テスト通過
- [ ] ベンチマークを実行してアロケーションされないことを確認

---

### Phase 4: ローカル action.yml 解決（G2 + G3）

**目的**: ローカルアクションの input/output/runs 検証を実装する。最も工数が大きいフェーズ。

**What**:

#### ステップ 1: ActionMetadata AST 型
- `src/Seiton.Core/Parsing/Ast/` に以下を新規追加:
  ```
  ActionMetadata
    Name: StringNode?
    Description: StringNode?
    Inputs: Dictionary<string, ActionMetadataInput>?
    Outputs: Dictionary<string, ActionMetadataOutput>?
    Runs: ActionMetadataRuns?
    Branding: ActionMetadataBranding?
    Range: TextRange

  ActionMetadataInput
    Name: string
    Description: StringNode?
    Required: BoolNode?
    Default: StringNode?
    DeprecationMessage: StringNode?
    Range: TextRange

  ActionMetadataOutput
    Name: string
    Description: StringNode?
    Value: StringNode?
    Range: TextRange

  ActionMetadataRuns
    Using: StringNode?
    Main: StringNode?
    Pre: StringNode?
    Post: StringNode?
    PreIf: StringNode?
    PostIf: StringNode?
    Image: StringNode?
    Entrypoint: StringNode?
    Args: StringNode[]?
    Env: Env?
    Steps: bool  // composite action steps の存在フラグ
    Range: TextRange

  ActionMetadataBranding
    Icon: StringNode?
    Color: StringNode?
    Range: TextRange
  ```

#### ステップ 2: ActionMetadata パーサー
- `WorkflowParser` の `ParseMode.ActionMetadata` パスを改修。
- 現在の `SkipCurrentNode()` を実際のパースに置き換え。
- `ParseResult` に `ActionMetadata?` を追加（`Workflow?` と排他）。

#### ステップ 3: ローカルアクションキャッシュ
- `LocalActionCache` を新規実装（`ReusableWorkflowRule` の `LocalWorkflowContract` パターンを参考）。
- `./path` → ディレクトリ解決 → `action.yml`/`action.yaml` 読み込み → パース → `ActionMetadata` キャッシュ。
- ファイル不在時は null 返却（`UnpinnedUsesRule` が存在チェック済み）。

#### ステップ 4: `local-action-inputs` ルール（新規）
- `VisitStep` で `ExecAction` の `uses` が `./` 始まりなら `LocalActionCache` を参照。
- 検証:
  - `with:` の unknown input → エラー（available inputs リスト付き）
  - `required: true` の input が `with:` にない → エラー
  - deprecated input の使用 → 警告（deprecation message 付き）
- `runs.using` 検証:
  - `docker`/`composite`/`node20`/`node24` のみ有効。それ以外エラー。
  - `node12`/`node16` 等の非推奨 runner → エラー（G3 の解消）。

**完了条件**:
- [x] ローカルアクションの unknown input がエラーになること
- [x] ローカルアクションの required input 欠落がエラーになること
- [x] ローカルアクションの deprecated input が警告になること
- [x] `runs.using` が `node16` の場合にエラーになること
- [x] `runs.using` が `node20`/`node24` の場合は正常であること
- [x] composite action の `runs.using: composite` が正常であること
- [x] ローカルアクションが見つからない場合は null で早期リターン（クラッシュしない）こと
- [x] キャッシュにより同じアクションを2回パースしないこと（`LocalActionInputsRule` の `_cache`）
- [x] 全テスト通過

---

## 3. 優先度と依存関係

```
Phase 1 (小規模ルール: G4, G5, G7) ← パーサー変更なし
Phase 2 (cron 検証: G1)               ← パーサー変更なし
Phase 3 (template injection: G6)       ← ルール改修のみ
Phase 4 (ローカル action: G2, G3)      ← パーサー拡張 + ルール追加
```

**推奨実施順**: Phase 1 → Phase 2 → Phase 3 → Phase 4

- **Phase 1** は最小工数で 3 ギャップを解消。リスクが低く、即座に着手可能。
- **Phase 2** も自己完結型のルール追加。cron パーサーの実装がやや複雑だが独立している。
- **Phase 3** は既存ルールの改修だが、ステートマシン形式への書き換えが中程度の工数。
- **Phase 4** はパーサー拡張を含む最大工数フェーズ。他フェーズの完了後に着手が安全。

Phase 1–3 は互いに独立しており並行可能だが、実施順序は工数の小さい順を推奨。

---

## 4. 完了後の比較表更新

全 Phase 完了後、`documents/index.md` の比較表を以下のように更新する:

| Aspect | Seiton | actionlint |
|---|---|---|
| Syntax / structural validation | ✓ | ✓ |
| Expression type checking | ✓ | ✓ |
| shellcheck / pyflakes integration | ✗ | ✓ |
| Security rules (injection, secrets, permissions) | ✓ (broad) | Partial |
| Supply-chain rules (pinning, archived, vulnerable) | ✓ | ✗ |
| Auto-fix | ✓ | ✗ |
| Online audit rules | ✓ (opt-in) | ✗ |
| Action metadata file support | ✓ | ✗ (lints as secondary only) |
| Local action input/output resolution | ✓ | ✓ |
| Config model | Rule-ID-centric | Global + path-based |

**変更点**:
- `Syntax / structural validation`: `✓ (deeper)` を `✓` に統一（Seiton が cron/dispatch 制約をカバーした後は同等）
- `Supply-chain rules`: actionlint の `Partial` を `✗` に修正（actionlint には supply-chain ルールがない — 前回分析の誤り）
- `Action metadata file support`: actionlint は `✗ (lints as secondary only)` に修正（action.yml をトップレベル lint 対象にしない）
- `Local action input/output resolution`: 新行追加
- Summary セクションのトーンを「補完関係」から「Seiton をメインとしつつ shellcheck 連携が必要なら actionlint 併用」に調整

---

## 5. Seiton_Linter_spec.md §4.6 の更新

Phase 完了時に `Seiton_Linter_spec.md` §4.6 Known Partial Parity (actionlint) を更新:
- `events` のパリティギャップに cron/dispatch-inputs 解消を反映
- `action` のパリティギャップにローカルアクション解決の解消を反映
- 新ルール ID (`schedule-event`, `dispatch-inputs`, `local-action-inputs`) を §4.4 ルールカタログに追加
