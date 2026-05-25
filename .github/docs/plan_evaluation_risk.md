## リスクと改善余地

### 複雑になりやすい箇所
RuleInterfaceTests.cs が非常に大きい（数万行の一枚岩）。テストの追加と保守が煩雑で、どのルールのどのケースかを追うのが難しくなりつつあります。

LintEngine.FinalizeRuleDiagnostics() — parser/linter 重複診断の置換ロジック（LintEngine.cs:386-428）は O(n×m) の二重ループで、診断数が増えると緩やかに遅くなります。現状の規模では問題ないですが。

suppression の優先順位 はコードとして実装されているものの、明示的な仕様ドキュメントに対応していないと、将来の変更時に意図しない挙動変化が起きるリスクがあります。

### メモリ管理リスク

AstArena の ThreadStatic 設計は スレッドプール recycling との組み合わせで高水準の arenas が永続化するリスクがあります（.NET ではスレッドプールスレッドがリサイクルされるため）。ShrinkIfOversized() でデフォルト容量以下に縮小する設計で対処していますが、WASM やロングランサーバーシナリオでは要注意です（AstArena.cs:338-345のコメントでも明示）。

PooledBuffer.DetachArray() → arena.Register*Buffer() のライフサイクル管理は正しく実装されているが、呼び出し側が arena を dispose し忘れるとプール汚染につながります（LintResult.cs が IDisposable ゆえ using 必須）。

### キャッシュリスク

LintConfig._expressionCache はコンテントハッシュ（XxHash64）ベースですが、MaxExpressionCacheEntries = 512 に達すると全消去する単純な eviction 戦略（LintConfig.cs:80）で、エントリが 512 件付近で常に border する場合は連続クリアが発生しパフォーマンスが劣化します。LRU 的な部分 eviction にすれば改善余地があります。

### ルール相互作用リスク

各ルールは独立した List<Diagnostic> を持ち、_seen HashSet で診断重複を除去しています。ただし 異なるルールが同じ location に異なるメッセージを発行する場合（重複排除は message+location 両方が一致した場合のみ）、意図的な二重診断は許容されますが、両方が fix を持ち競合 offset を生成するケースはエラーになります。

### CLI/Core 境界のリスク
FixCommand.cs が LintEngine を直接 new して使い回す設計は疎結合ですが、LintEngine が not thread-safe（LintEngine.cs:148-156のコメントで明示）なため、FixCommand は意図的に sequential に処理しています。将来並列 fix が必要になった場合は ThreadLocal<LintEngine> への移行が必要です。
