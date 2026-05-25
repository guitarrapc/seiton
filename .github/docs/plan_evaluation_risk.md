## リスクと改善余地

> 外部評価によるリスク指摘と、コード検証に基づく妥当性評価・対応策をまとめる。

---

### 1. 複雑になりやすい箇所

#### 1.1 RuleInterfaceTests.cs が巨大（一枚岩）

**指摘**: 数万行の単一クラスでテスト追加・保守が煩雑。

**評価: 妥当** — 実測 12,300行超、region/partial 分割なし。ルール特定にファイル内検索が必須で開発体験を損なう。ただしビルド・テスト実行速度への影響はない。

**対応策**: ルール毎に partial class ファイル分割（`RuleInterfaceTests.JobStructure.cs` 等）。既存テストは移動のみで壊れない。

#### 1.2 FinalizeRuleDiagnostics() の O(n×m) 二重ループ

**指摘**: parser/linter 重複診断の置換ロジックが O(n×m) で診断数増加時に遅くなる。

**評価: 過大評価** — 実際のコードは `_seen.Add(identity)` が `false`（重複検出）のときのみ内側ループに入り、`break` で即脱出する。計算量は O(d×p)（d = 重複診断数 ≈ 0〜数件、p = パーサー診断数 ≈ 数十件）であり、現実のワークフローファイルで O(n×m) に到達することはない。

**対応策**: 不要。将来パーサー診断を `Dictionary<DiagnosticIdentity, int>` でインデックス化すれば O(1) 置換が可能だが、現状で問題は顕在化していない。ベンチマークで継続監視のみ。

#### 1.3 suppression 優先順位の仕様文書不在

**指摘**: 優先順位がコード実装のみで明示的仕様ドキュメントがない。

**評価: 妥当** — ただし `Seiton_Linter_spec.md` §5.2 に「Inline > Config > Default」の3段階優先順位が明記されている。指摘時点では未確認だった可能性がある。現状は仕様に対応済み。

**対応策**: 不要（既に仕様文書に記載済み）。

---

### 2. メモリ管理リスク

#### 2.1 AstArena の ThreadStatic 設計

**指摘**: ThreadPool recycling で高水準の arena が永続化するリスク。

**評価: 妥当だが既に対策済み** — `ShrinkIfOversized()` が Dispose 時に全配列をデフォルト容量まで縮小してからキャッシュする。ピーク時の巨大配列は保持されない。WASM は single-thread のため ThreadStatic は 1 インスタンスのみ。サーバーシナリオでも .NET ThreadPool が idle スレッドを回収するため実害は限定的。

**残存リスク**: 数百スレッドが同時にピーク使用した場合の理論的メモリ圧。

**対応策**: 現状維持で十分。万一問題が顕在化した場合は `Gen2GcCallback` で ThreadStatic をクリアする手法が使える。WeakReference 化はオーバーエンジニアリング。

#### 2.2 PooledBuffer.DetachArray() のライフサイクル

**指摘**: arena を dispose し忘れるとプール汚染につながる。

**評価: 妥当だが既に対策済み** — CLI は全箇所で `using var result = engine.Check(...)` パターンを使用。`LintResult` が `IDisposable` + `_ownsArena` フラグで所有権を管理し、Dispose で ArrayPool に返却。

**対応策**: CA2000 analyzer を有効化して静的解析で未 dispose を検出する。

---

### 3. キャッシュリスク

#### 3.1 ExpressionCache の全消去 eviction

**指摘**: MaxExpressionCacheEntries = 512 到達時に全消去するため、境界付近でスラッシングが発生しうる。

**評価: 妥当だが影響は極めて軽微** — 単一ファイル lint で式が 512 に達することはまずない。さらに ExpressionArtifacts（パーサー生成の式キャッシュ）統合により、`_expressionCache` へのフォールバック自体が稀になった。WASM Playground で巨大ファイルを連続 lint する場合のみ理論上起きうる。

**対応策**: 不要（LRU 化は過剰）。スラッシングを検出したい場合はカウンターを追加して telemetry で計測。

---

### 4. ルール相互作用リスク

#### 4.1 競合 fix offset

**指摘**: 異なるルールが同じ location に fix を持つ場合、競合 offset でエラーになる。

**評価: 理論的に妥当** — FixCommand は fix を適用する際にテキスト範囲の重複検出が必要。現在のルールセットで実際に競合が発生するケースは未確認。

**対応策**: 同一範囲に複数 fix が重なった場合に最初の fix のみ適用しスキップする guard の存在確認とテスト追加。

---

### 5. CLI/Core 境界のリスク

#### 5.1 FixCommand の sequential 処理

**指摘**: LintEngine が not-thread-safe のため sequential。将来並列化が必要なら ThreadLocal への移行が必要。

**評価: 妥当（現状正しい設計）** — fix は I/O bound（ファイル読み書き）であり、並列化の恩恵は限定的。GitHub Actions リポジトリでワークフローが数百ある状況は稀。`Seiton_Linter_spec.md` §2.1 で「Fix は sequential-only」と明示されている。

**対応策**: 不要。将来並列化が必要になった場合は `ObjectPool<LintEngine>` で対応可能だが、現時点で変更は不要。

---

## 対応実装計画

### 検証ポリシー

全ての対応実装は以下を満たすこと:

1. **実装前**: ベンチマーク baseline を取得（`dotnet run -c Release` in `src/Seiton.Benchmark`）
2. **実装後**: テスト全通過を確認（`dotnet test`）
3. **実装後**: ベンチマーク再実行し mean time / allocations に有意な劣化がないことを確認
4. **リグレッション**: 既存テストの変更は原則禁止（テスト移動は除く）

### 優先度と実装順序

| 優先度 | 対応 | 理由 | 工数目安 |
|---|---|---|---|
| P1 | RuleInterfaceTests.cs partial 分割 | 開発体験直結。ルール追加頻度が高い今やるのが最善 | 小 |
| P2 | CA2000 analyzer 有効化 | 静的解析で未 dispose 検出。設定変更のみ | 極小 |
| P3 | Fix 競合テスト追加 | 同一範囲の重複 fix が安全にスキップされることの確認 | 小 |
| — | FinalizeRuleDiagnostics インデックス化 | 現状問題なし。ベンチマーク監視で対応判断 | — |
| — | ExpressionCache LRU 化 | ExpressionArtifacts 統合で問題緩和済み | — |
| — | AstArena GC コールバック | 問題顕在化時のみ対応 | — |
| — | FixCommand 並列化 | 仕様で sequential-only と明示。需要発生時のみ | — |

### P1: RuleInterfaceTests.cs partial 分割

- `RuleInterfaceTests` を `partial class` に変更
- ルール毎のテストメソッドを個別ファイルに移動（例: `RuleInterfaceTests.UnpinnedUsesRule.cs`）
- ファイル命名規則: `RuleInterfaceTests.{RuleNamePascalCase}.cs`
- テスト内容の変更なし（移動のみ）

#### 実装結果

62 partial class ファイルに分割完了。全 2071 テスト合格、リグレッションなし。

| ファイル | 内容 | メソッド数 |
|---|---|---|
| `RuleInterfaceTests.cs` | インフラ/ヘルパー (RuleCatalog, LintConfig, Parser 等) | 15 |
| `RuleInterfaceTests.LintEngine.cs` | LintEngine_* (エンジン動作テスト) | 149 |
| `RuleInterfaceTests.Suppression.cs` | DisableNextLine/DisableJob/ConfigExclusion | 31 |
| `RuleInterfaceTests.{RuleName}.cs` × 59 | ルール毎の回帰テスト + 動作テスト | 229 |

主要ファイル例:
- `RuleInterfaceTests.ArtipackedRule.cs` (73 tests)
- `RuleInterfaceTests.ExprUndefinedVarRule.cs` (34 tests)
- `RuleInterfaceTests.UnpinnedUsesRule.cs` (18 tests)

### P2: CA2000 analyzer 有効化

- `.editorconfig` または `Directory.Build.props` で CA2000 を warning に設定
- 既知の安全な箇所に `#pragma warning disable` を付与（必要な場合のみ）

### P3: Fix 競合テスト追加

- 同一テキスト範囲に 2 つの fix を持つ診断を生成するテストケース作成
- FixCommand が先頭の fix のみ適用し、重複範囲をスキップする動作を検証
- 後方→前方の適用順序が保証されていることを検証
