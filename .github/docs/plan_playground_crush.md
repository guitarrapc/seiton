# Playground WASM クラッシュ調査・対策プラン

## 1. 症状

エディタで新規行を追加し、ジョブ ID 等を入力中に **100% 再現** でクラッシュ。

```
Error: Garbage collector could not allocate 16384u bytes of memory for major heap section.
[MONO] /__w/1/s/src/runtime/src/mono/mono/sgen/sgen-gc.c:3961
```

その後 `Assert failed: .NET runtime already exited with 1` が連鎖し、ページリロードまで復帰不可。

---

## 2. 根本原因

### 2.1 SGEN GC の OOM（16KB すら確保不可）

クラッシュの直接原因は Mono SGEN GC が "major heap section" 用の 16KB を確保できなかったこと。これは利用可能メモリの絶対量が不足しているのではなく、**GC が新しいヒープセクションを確保しようとした際に WASM linear memory の grow に失敗した** ことを示す。

### 2.2 Debug ビルドのメモリ圧迫

`Seiton.Playground.csproj` では `InvariantGlobalization` / `InvariantTimezone` / `PublishTrimmed` / `RunAOTCompilation` は **Release 構成のみ** に適用される:

```xml
<PropertyGroup Condition="'$(Configuration)'=='Release'">
    <InvariantGlobalization>true</InvariantGlobalization>
    ...
</PropertyGroup>
```

Debug ビルド (`dotnet run`) では:
- **ICU データ** (libicudata.a, libicui18n.a, libicuuc.a) が全てリンクされる（数 MB）
- **トリミングなし** — BCL 全体がロードされる
- **AOT なし** — IL インタープリタが使われ、JIT メタデータ用にメモリ追加消費
- **初期メモリ 32MB** (`INITIAL_MEMORY=33554432` in `emcc-link.rsp`)

### 2.3 LintEngine の呼び出しごとのメモリ蓄積

`PlaygroundLintRunner` は **static な `LintEngine`** インスタンスを再利用する:

```csharp
private static readonly LintEngine Engine = new();
```

各 `Check()` 呼び出しで:
1. `Encoding.UTF8.GetBytes(yamlSource)` — 入力の UTF-8 コピーを毎回新規作成
2. `AstArena.Rent(utf8Yaml)` — パース木全体を保持するアリーナを取得
3. `LintResult` が `ParseResult` を保持し、`ParseResult` が `AstArena` を保持する
4. **`AstArena` は明示的に `Dispose()` されない** — `RunToJson()` は `LintResult` を使い捨てにするだけで、`result.ParseResult.Arena?.Dispose()` を呼んでいない
5. 結果として、前回の `AstArena` は GC 回収待ちになり、次回 `AstArena.Rent()` は ThreadStatic キャッシュが空のため **新規 `AstArena` を生成する**
6. 古い `AstArena` のバッキング配列（`_strings[]`, `_bools[]` 等）が GC に回収されるまで両方のアリーナがメモリを占有

### 2.4 VYaml の [ThreadStatic] バッファ蓄積

VYaml (`YamlParser`, `Utf8YamlTokenizer`) は `[ThreadStatic]` プールを使用:
- `InsertionQueue<Token>` — Grow のみ、shrink なし
- `ExpandBuffer<SimpleKeyState>` — 同上
- `ExpandBuffer<int>` (indents) — 同上
- `Dictionary<string, int>` (anchors) — Clear のみ、capacity は保持
- `ScalarPool.Shared` (ConcurrentQueue) — Scalar オブジェクトが蓄積

これらは 1 回目の大きな入力でピークまで拡張され、以降は縮小しない。WASM シングルスレッドのため ThreadStatic は実質 static。

### 2.5 GC タイミングの問題

同期 `[JSExport]` 呼び出しでは:
1. JS → C# 呼び出し（メインスレッドブロック）
2. lint 実行中に GC が走る必要がある場合、WASM linear memory を grow しようとする
3. grow が失敗 → SGEN が abort → ランタイム死亡

通常の .NET アプリではこの状況は GC がメモリ不足例外を投げるだけだが、**WASM の SGEN 実装では abort してランタイムを殺す**。

---

## 3. actionlint との比較

| 項目 | actionlint (Go WASM) | seiton (.NET WASM) |
|------|---------------------|-------------------|
| エンジンの寿命 | **呼び出しごとに `NewLinter` で新規作成** | static `LintEngine` を再利用 |
| パース木の寿命 | 関数スコープで即解放（Go GC） | `LintResult` が保持、明示的 Dispose なし |
| GC の種類 | Go のマーク＆スイープ（WASM 向け最適化済み） | Mono SGEN（サーバー向け設計、WASM で制約あり） |
| WASM バイナリ形態 | 単一 `main.wasm`（全コード含む） | `_framework/` ディレクトリ（ランタイム + DLL 群） |
| メモリモデル | Go ランタイムが linear memory を自己管理 | Emscripten + Mono が linear memory を共有管理 |
| バッファプール | なし（毎回 GC 任せ） | ThreadStatic プール（grow only, shrink なし） |
| Debug 時ペナルティ | 軽微（Go WASM は常に同じバイナリ） | 重大（ICU + 非 trim + IL 解釈 = 数倍のメモリ） |
| JS 側の制御 | debounce 300ms のみ、その他制御なし | debounce 300ms + fingerprint（今回追加） |

**actionlint の JS コード** (`index.ts`):
```typescript
editor.on('change', function (_, e) {
    if (debounceId !== null) {
        window.clearTimeout(debounceId);
    }
    function startActionlint(): void {
        debounceId = null;
        window.runActionlint!(editor.getValue());
    }
    if (e.origin === 'paste') {
        startActionlint();
    } else {
        debounceId = window.setTimeout(() => {
            startActionlint();
        }, debounceInterval);
    }
});
```

actionlint は **debounce 以外の制御を一切行っていない**。クラッシュしない理由は JS 側の制御ではなく、Go WASM のメモリ特性による。

**actionlint の Go コード** (`main.go`):
```go
func lint(source string) interface{} {
    opts := actionlint.LinterOptions{}
    linter, err := actionlint.NewLinter(io.Discard, &opts) // ← 毎回新規作成
    ...
    errs, err := linter.Lint("test.yaml", []byte(source), nil)
    ...
}
```

各呼び出しで linter を新規作成し、結果を返した後は全てのメモリが GC 対象になる。**状態の蓄積がない**。

---

## 4. 対策案（優先度順）

### 4.1 [最優先] AstArena の明示的 Dispose

`PlaygroundLintRunner.RunToJson()` で diagnostic 抽出後に `Arena` を返却する。

```csharp
public static string RunToJson(string yamlSource, string filePath)
{
    // ...
    LintResult result;
    lock (EngineGate)
    {
        result = Engine.Check(utf8Yaml, filePath, LintWithFixMetadata);
    }

    // diagnostic 情報を抽出
    var list = /* ... build dto list ... */;
    var json = JsonSerializer.Serialize(list, ...);

    // AstArena を即座に ThreadStatic キャッシュに返却
    result.ParseResult.Arena?.Dispose();

    return json;
}
```

**効果**: 次回 `AstArena.Rent()` がキャッシュからアリーナを再利用できるようになり、新規 `AstArena` の生成と古い `AstArena` の GC 待ちが解消される。**最大のメモリ削減効果**。

`ApplyAllFixes` のループ内でも同様に各パスの `result` の `Arena` を dispose する。

### 4.2 [最優先] Debug ビルドでも InvariantGlobalization を有効化

ICU データだけで数 MB を消費する。Debug でも有効にすることで WASM メモリ消費を大幅削減:

```xml
<!-- csproj で条件を外す -->
<PropertyGroup>
    <InvariantGlobalization>true</InvariantGlobalization>
    <InvariantTimezone>true</InvariantTimezone>
</PropertyGroup>
```

### 4.3 [推奨] EmccMaximumHeapSize の明示設定 ✅ 実装済み

512MB では Debug ビルドのメモリ圧迫でちょうど上限に到達して OOM が発生した。
初期ヒープ 64MB + 最大 1GB に変更:

```xml
<PropertyGroup>
    <EmccInitialHeapSize>67108864</EmccInitialHeapSize>  <!-- 64MB initial -->
    <EmccMaximumHeapSize>1073741824</EmccMaximumHeapSize> <!-- 1GB max -->
</PropertyGroup>
```

**教訓**: 512MB は Debug ビルド（非 trim + 非 AOT + ICU）ではギリギリ不足する。1GB なら十分なマージンがある。

### 4.4 [推奨] lint 呼び出し前に GC.Collect を検討

WASM 環境では GC が自動で走るタイミングが限られる。明示的 GC は通常避けるべきだが、WASM の制約下では有効:

```csharp
[JSExport]
public static string RunLint(string? yamlSource, string? filePath)
{
    try
    {
        // ... existing code ...
    }
    catch (Exception ex)
    {
        return SerializeInternalError(ex);
    }
}
```

ただし `GC.Collect()` は SGEN の full collection を強制し、それ自体が OOM の原因になりうるため、**Arena Dispose が先**。

### 4.5 [中優先] PlaygroundLintRunner を stateless 化 → ❌ 撤回、static Engine に戻した

**当初の変更**: `new LintEngine()` per call でルールオブジェクトを毎回新規生成する設計に変更。
**問題**: 呼び出しごとに 50+ のルールオブジェクト + 内部 List/HashSet/Dictionary を新規作成し、GC 圧力を大幅に増大させた。Debug WASM のベースラインが ~800MB の環境では逆効果。

**撤回後の設計**: static `LintEngine` + `lock(EngineGate)` に戻し、以下を維持:
- Arena.Dispose() は毎回呼び出し
- AstArena.Dispose() でバッキング配列の高水位キャップを追加（Grow で膨張した配列をデフォルトサイズに縮小）

**教訓**: `LintEngine` は再利用設計（Check() 冒頭で全リスト Clear、ルールは VisitWorkflowPre で diagnostics Clear）。per-call インスタンス生成は actionlint の Go パターンを模倣したが、Go は GC が世代別ではなく並行マーク＆スイープなので事情が異なる。.NET WASM の SGEN GC では短命大量オブジェクトが nursery → major heap プロモーションを引き起こし、memory.grow 失敗の原因になる。

### 4.7 [新規] AstArena バッキング配列の高水位キャップ ✅ 実装済み

`AstArena.Dispose()` で、Grow() によって膨張したバッキング配列がデフォルト容量を超えている場合、デフォルトサイズの新しい配列に置き換える。ThreadStatic キャッシュがピーク時の大きな配列を永続的に保持する問題を解消。

### 4.6 [低優先] VYaml ThreadStatic バッファの制御

VYaml 内部の ThreadStatic バッファは shrink しない設計。長期的には VYaml にパッチを当てるか、seiton 側で parse 後にリフレクションで強制クリアする（非推奨）。現実的には 4.1–4.3 で十分な効果が得られるはず。

---

## 5. 実装優先度

| # | 施策 | 難易度 | メモリ削減効果 | リスク |
|---|------|--------|--------------|--------|
| 1 | AstArena の明示的 Dispose | 低 | **大** | 低（dispose 後に Arena 参照しないことを確認済み） |
| 2 | InvariantGlobalization を全構成で有効化 | 低 | **大** | なし（lint に文化依存処理不要） | ✅ |
| 3 | EmccMaximumHeapSize / EmccInitialHeapSize 設定 | 低 | 中（断片化耐性向上） | 低 | ✅ (64MB init / 1GB max) |
| 4 | PlaygroundLintRunner stateless 化 | 中 | ~~中~~ **逆効果** | GC 圧増大 | ❌ 撤回 |
| 5 | GC.Collect 検討 | 低 | 小 | full GC 自体の OOM リスク |
| 6 | VYaml パッチ | 高 | 小 | 上流変更管理 |
| 7 | AstArena バッキング配列の高水位キャップ | 低 | 中 | 低 | ✅ |

---

## 6. 推奨実装順序

1. ~~**4.1 + 4.2 + 4.3 を同時に実装**~~ → 全て実装済み（4.1 Arena Dispose, 4.2 InvariantGlobalization, 4.3 EmccHeapSize 1GB）
2. ~~改善後に再テスト~~ → 512MB で OOM 再発を確認、1GB に引き上げ済み
3. ~~それでも OOM が発生する場合は 4.5 を検討~~ → 4.5 は撤回済み（see §4.5）
4. 4.4 は最終手段

---

## 7. 補足：なぜ 16KB 程度で OOM するのか

エラーメッセージの "16384u bytes" は GC の major heap section サイズ。SGEN GC は：
1. 小さなオブジェクトを "nursery" (minor heap) に割り当て
2. 生存オブジェクトを "major heap" にプロモーション
3. major heap は固定サイズの "sections" で構成される

WASM 環境では：
- linear memory の grow は `memory.grow` 命令で行われる
- ブラウザによっては連続した大きな空きページの確保に失敗することがある
- **Debug ビルドで ICU + 非 trim + IL interpreter の全てがメモリを消費** した状態で GC section の追加確保が失敗する
- 特に Chromium 系は WASM memory の grow に保守的な場合がある

実際のメモリ使用量は小さい YAML 入力であっても、ランタイム自体が 100MB 以上消費している可能性が高く、GC が新しい section を確保しようとした時点で linear memory の上限に達している。

---

## 8. Debug ビルドの根本的なメモリ問題

### 8.1 測定結果

Debug ビルドの `_framework/` ディレクトリ:
- **182 個の .wasm アセンブリ** (合計 35.3 MB on disk)
- `dotnet.native.wasm`: 13.9 MB (Mono ランタイム)
- `System.Private.CoreLib`: 4.6 MB
- `System.Private.Xml`: 3.0 MB (lint に不要)
- `System.Data.Common`: 1.0 MB (lint に不要)
- `Microsoft.VisualBasic.Core`: 0.4 MB (lint に不要)
- その他 175 の BCL アセンブリ

### 8.2 なぜ Debug ビルドで ~800MB 消費するか

1. **未トリム**: `PublishTrimmed` は Release only。全 BCL アセンブリがロードされる
2. **非 AOT**: IL インタプリタがメソッド本体 + メタデータテーブルをメモリ上に展開
3. **HotReload**: `Microsoft.DotNet.HotReload.WebAssembly.Browser` がロードされる
4. **35 MB on disk → ~500-800 MB in memory**: メタデータ展開 + 型解決テーブル + GC ヒープ管理

### 8.3 推奨: Release ビルドで Playground を実行

Debug ビルドの 182 アセンブリ × IL インタプリタのベースラインは ~800 MB。
アプリケーションコードの最適化だけでは根本解決できない。

```shell
# Release ビルドで実行（トリム + AOT 適用）
dotnet run --project src/Seiton.Playground -c Release
```

Release ビルドでは:
- `PublishTrimmed=true` + `TrimMode=full` → 不要なアセンブリ除去
- `RunAOTCompilation=true` → IL インタプリタのメタデータ展開不要
- 推定ベースライン: 50-100 MB（Debug の 1/10 以下）

---

## 9. per-call ヒープアロケーション詳細分析

### 9.1 分析対象パス

```
PlaygroundLintRunner.RunToJson()
  → Encoding.UTF8.GetBytes()
  → LintEngine.Check()
    → WorkflowParser.ParseClassified()
      → VYamlStreamAdapter (hint pass)
      → VYamlStreamAdapter (full parse)
      → AstArena.Rent()
      → PooledBuffer<Diagnostic>.ToArray()
    → NormalizeRules()
    → ParseInlineSuppression()
    → NormalizeExclusions()
    → new LintConfig { ... }
    → WorkflowVisitor.Visit()
    → _diagnostics.ToArray()
    → new SuppressionSummary(...)
  → new List<PlaygroundDiagnosticDto>(...)
  → JsonSerializer.Serialize()
  → Arena.Dispose()
```

### 9.2 Tier 1: 毎回必ず発生、影響大

| # | 箇所 | アロケーション | 推定サイズ |
|---|------|-------------|----------|
| 1 | `LintEngine.Check` L118 | **`new LintConfig { Fix = ..., Network = ..., Output = ... }`** | LintConfig 本体 + `new FixConfig()` + `new FixDefaultsConfig()` + `new FixPinningConfig()` (内部に `string[]` 2本: ExcludeBranches, IgnoreActions) + `new FixImagesConfig()` (内部に `string[]` 2本: ExcludeImages, ExcludeTags) + `new NetworkConfig()` + `new GitHubNetworkConfig()` + `new OutputConfig()` — 合計 **9–10 オブジェクト per call** |
| 2 | `LintEngine.Check` L274 | **`_diagnostics.ToArray()`** | `Diagnostic[]` — 診断数分のコピー |
| 3 | `LintEngine.Check` L276 | **`new Dictionary<string,int>(_suppressedByRule)` + `_suppressionRecords.ToArray()`** | Dictionary スナップショット + 配列コピー |
| 4 | `PlaygroundLintRunner` L54 | **`Encoding.UTF8.GetBytes(yamlSource)`** | 入力全体の `byte[]` コピー |
| 5 | `PlaygroundLintRunner` L84 | **`JsonSerializer.Serialize(list, ...)`** | JSON `string` 全体 |
| 6 | `PlaygroundLintRunner` L68 | **`new List<PlaygroundDiagnosticDto>(...)`** + 各 DTO | List + N 個の DTO オブジェクト |

### 9.3 Tier 2: 毎回必ず発生、影響中

| # | 箇所 | アロケーション |
|---|------|-------------|
| 7 | `WorkflowParser.ParseClassified` | `PooledBuffer<Diagnostic>.ToArray()` — パーサー診断の配列コピー |
| 8 | `WorkflowParser.ParseCore` | `new List<Diagnostic>(16)` + `.ToArray()` |
| 9 | `LintEngine.NormalizeRules` | `new Dictionary<string, RuleConfig>()` + `new List<Diagnostic>()` + `.ToArray()` |
| 10 | `LintEngine.ParseInlineSuppression` | 3 つの `new Dictionary<>()` + `new List<Diagnostic>()` + `.ToArray()` |
| 11 | `LintConfig.GetLineStarts()` | `int[]` (行数分) — LintConfig が毎回新規なのでキャッシュが効かない |
| 12 | `LintConfig._expressionCache` | `new Dictionary<long, ExpressionCacheEntry>()` — 同上 |

### 9.4 核心的な問題: LintConfig が per-call 新規作成

`LintEngine.Check()` L118 で毎回 `new LintConfig { ... }` を生成するため:

1. LintConfig 本体 + 6-8 個のネストした config record が**毎回新規生成**される
2. `GetLineStarts()` の `int[]` キャッシュが毎回破棄される（前回 Check の LintConfig が GC 対象）
3. `_expressionCache` の Dictionary が毎回破棄される
4. 式パース結果のキャッシュが一切 cross-call で再利用されない

LintConfig の init-only property のデフォルト値が eager にサブオブジェクトを生成する設計:
```csharp
public FixConfig Fix { get; init; } = new();          // → new FixDefaultsConfig() + new FixPinningConfig(string[2]) + new FixImagesConfig(string[2])
public NetworkConfig Network { get; init; } = new();    // → new GitHubNetworkConfig()
public OutputConfig Output { get; init; } = new();
```

LintEngine.Check 内で `config?.Fix ?? new FixConfig()` とフォールバックしているため、Playground の `LintWithFixMetadata` から渡された Fix は使われるが、Network と Output は毎回 new される。

---

## 10. 対策候補（優先度別）

### P0: LintEngine にフィールド LintConfig を保持し再利用

**概要**: `LintEngine` にインスタンスフィールドとして `_effectiveConfig` を持ち、Check() で property を書き換えて再利用。`_expressionCache` と `_lineStarts` が cross-call でキャッシュされるようになる。

**削減効果**: 毎回 9-10 オブジェクト + expression cache Dictionary + lineStarts `int[]` の新規生成を排除。

**難易度**: 中 — LintConfig を `init` property から mutable `set` に変更するか、別途 mutable な "effective config context" を導入。API 変更を伴うが、LintConfig は internal 利用のみなので影響範囲は限定的。

**注意**: `_expressionCache` は `Utf8Yaml` (ソースバイト列) に依存するため、ソースが変わったらキャッシュをクリアする必要がある。ソース同一性チェック（参照比較 or ハッシュ）が必要。

### P1: `_diagnostics.ToArray()` の排除

**概要**: LintResult が `Diagnostic[]` の所有権を取る設計を改め、`ReadOnlySpan<Diagnostic>` ベースにするか、pooled array を返す。

**削減効果**: 診断数 × `Diagnostic` サイズ分の配列コピーを毎回削除。

**難易度**: 中 — LintResult の API 変更、下流の消費コード修正。`.ToArray()` を `.AsSpan()` に置き換え、LintResult が内部リストへの参照を保持する形に。

### P2: NormalizeRules / ParseInlineSuppression の Dictionary をフィールド化

**概要**: `LintEngine` のインスタンスフィールドとして Dictionary/List を保持し、Check() で `.Clear()` + 再利用。

**削減効果**: 毎回 5-6 個のコレクションオブジェクト新規生成を排除。

**難易度**: 中 — NormalizeRules/ParseInlineSuppression の戻り値を struct + フィールド参照に変更。

### P3: Playground 固有 — LintConfig のサブオブジェクト生成回避

**概要**: `PlaygroundLintRunner` が渡す `LintWithFixMetadata` に Network/Output も含めた完全な設定を保持し、LintEngine.Check 内の `config?.Fix ?? new FixConfig()` フォールバックで new が走らないようにする。

**削減効果**: 毎回 3-4 個のサブ config オブジェクト新規生成を排除。

**難易度**: **低** — `LintWithFixMetadata` の定義を拡張するだけ。LintEngine 側の変更不要。

```csharp
private static readonly LintConfig LintWithFixMetadata = new()
{
    Fix = new FixConfig { Enabled = true },
    Network = new NetworkConfig(),    // ← 追加: Check 内での new を回避
    Output = new OutputConfig(),      // ← 追加: Check 内での new を回避
};
```

### P4: SuppressionSummary の生成スキップ（Playground 用）

**概要**: Playground では suppression 機能を使わないため、SuppressionSummary の Dictionary/配列スナップショットを生成しない。LintConfig に「summary 不要」フラグを追加。

**削減効果**: 毎回の `new Dictionary<string,int>(...)` + `_suppressionRecords.ToArray()` を排除。

**難易度**: 低 — フラグ 1 つと条件分岐。

### P5: AstArena バッキング配列を ArrayPool 化 ✅ 実装済み

全 `new T[]` を `ArrayPool<T>.Shared.Rent/Return` に置換済み。Grow/Shrink 時の旧配列が GC ゴミにならず ArrayPool に返却される。

---

## 11. 追加アロケーション削減 — 深層分析結果

P0–P5 実装後の残存アロケーションを Seiton.Core 全体にわたって網羅的に調査した結果。
**後方互換性は無視し、根本的な構造変更を含む。**

### 11.1 優先度 S（最優先）: パーサー毎回実行ホットパス

毎回の `Check()` 呼び出しで **無条件に** 発生し、ファイルサイズに比例して影響が拡大するもの。

| # | 箇所 | アロケーション | 推定影響 | 対策 |
|---|------|-------------|---------|------|
| S-1 | `WorkflowParser.ParseClassified` L68 | `new (string, TextPosition)[8]` unusedBuf | 小（固定 8 要素） | `stackalloc` + ref struct 化、または static フィールド化して再利用 |
| S-2 | `WorkflowParser.ParseClassified` L70 | `new (string, TextPosition, TextPosition)[8]` recursiveBuf | 小（固定 8 要素） | 同上 |
| S-3 | `WorkflowParser.ParseClassified` L115 | `diagnostics.ToArray()` — パーサー診断の最終配列 | 中（診断数依存） | `PooledBuffer<Diagnostic>` の所有権を `ParseResult` に移転し、ToArray を廃止。ParseResult が PooledBuffer を保持し、AsSpan で参照を返す設計に変更 |
| S-4 | `WorkflowParser.ParseCore` L249 | `new List<Diagnostic>(16)` | 中（List本体 + 内部配列） | `PooledBuffer<Diagnostic>` に置換。ParseCore を PooledBuffer ベースに変更 |
| S-5 | `WorkflowParser.ParseCore` L546 | `diagnostics.ToArray()` — workflow/action の最終配列 | 中 | S-3 と連動: ParseResult が PooledBuffer 所有権を取る |
| S-6 | `LintEngine.BuildKnownJobIdSlices` L843 | `new Utf8Slice[count]` — ジョブ ID スライス配列 | 中（ジョブ数依存） | LintEngine フィールドに `Utf8Slice[]` を保持し、容量が足りればそのまま再利用。不足時のみ拡張 |
| S-7 | `ExpressionSemanticAnalyzer.Validate` L71-73 | `new List<Diagnostic>()` + `.ToArray()` | 大（式ごとに呼出） | PooledBuffer 化 or 呼び出し元が渡す共有バッファ |
| S-8 | `ExpressionSemanticAnalyzer.ValidateDynamicPropertyAccess` L1400-1409 | `new List<Diagnostic>()` + `.ToArray()` | 大（動的プロパティアクセスごと） | 同上 |
| S-9 | `ExpressionSemanticAnalyzer.ValidateFunctionCall` L468 | `new ExprType[argCount]` | 中（関数呼出ごと） | stackalloc + Span 化（argCount は通常 1-3） |

### 11.2 優先度 A（高）: ルール実行ホットパス

ルール Visit メソッド内で **ステップごと・ジョブごと** に発生するもの。

| # | 箇所 | アロケーション | 推定影響 | 対策 |
|---|------|-------------|---------|------|
| A-1 | `DynamicContextTypeBuilder.BuildStepsOverride` L44,57 | `new Dictionary<byte[], ExprType>()` per VisitStep | 大（ステップ数 × ジョブ数） | Dictionary をルールフィールドで保持し Clear + 再投入。ExprType 側を不変にし、steps override を差分更新にする |
| A-2 | `ExprUndefinedVarRule.VisitEvent` L104,112 | `new[] { incrementalInputsOverride }` — 1要素配列 | 小（input_default 数依存） | static readonly 1要素配列をフィールド化し、要素を書き換えて再利用 |
| A-3 | `ExprUndefinedVarRule.VisitWorkflowPost` L194 | `new (byte[], ExprType)[]` — 2要素配列 | 小（workflow_call のみ） | 同上（2要素フィールド） |
| A-4 | `PopularActionInputsRule.VisitStep` L31 | `Decode(usesSlice)` — string 変換 | 中（popular action ステップごと） | UTF-8 span ベースのルックアップに変更。action spec の名前を byte[] で保持し、span 比較 |
| A-5 | `UnpinnedUsesRule.VisitStep` L134 | `Decode(usesSlice)` — PinDiagnosticMetadata 用 | 中（unpinned ステップごと） | メタデータを Utf8Slice ベースに変更し、Decode を遅延化（表示時のみ） |
| A-6 | `RuleBase.Decode(Utf8Slice/Utf8String)` L195-207 | `Encoding.UTF8.GetString` per diagnostic | 中（全ルールの診断メッセージ生成） | Diagnostic.Message を `Utf8String` ベースに変更し、表示時のみ string 化。根本的だが影響範囲が非常に大きい |
| A-7 | `RunContextDirectUseAnalyzer.IsPowerShell` L59 | `Encoding.UTF8.GetString` per shell check | 小 | UTF-8 span 比較に置換（`"pwsh"u8`, `"powershell"u8` 等） |
| A-8 | `LintEngine.NormalizeExclusions` L1025-1079 | `new List<>`, `new HashSet<>`, `.ToArray()` | 小（exclusion 設定時のみ） | フィールド化して Clear + 再利用 |

### 11.3 優先度 B（中）: パーサー AST 構築パス

AST ノード生成とそのバッキング配列。ファイルごとに 1 回だが、サイズが大きいと影響する。

| # | 箇所 | アロケーション | 推定影響 | 対策 |
|---|------|-------------|---------|------|
| B-1 | `WorkflowParser` 各所 | `PooledBuffer<SliceMap<T>.Entry>.ToArray()` — jobs, permissions, env, outputs 等のバッキング配列 | 大（全 SliceMap の Entry[] コピー） | SliceMap が PooledBuffer を直接保持する設計に変更。SliceMap<T> を IDisposable にし、Entry[] を ArrayPool 管理にする。AstArena の Dispose 時に一括返却 | ✅ D-4 で実装済み (DetachArray + RegisterSliceMapBuffer) |
| B-2 | `Workflow`, `Job`, `Step` | `new Workflow()`, `new Job()`, `new Step()` — class instance | 大（各 AST ノード） | struct 化は参照サイクルとサイズの問題で困難。代替: AstArena にオブジェクトプールを追加し、Reset で再利用 | ✅ Workflow/ActionMetadata を AstArena でプール化 (Job/Step/ExecRun/ExecAction は既存) |
| B-3 | `WorkflowParser.ParseCore` L533 | `new Workflow { ... }` | 中（per parse） | B-2 と連動 | ✅ arena.AllocWorkflow() に置換 |
| B-4 | `ActionMetadata` | `new ActionMetadata { ... }` | 小（action metadata のみ） | B-2 と連動 | ✅ arena.AllocActionMetadata() に置換 |
| B-5 | `ExpressionSemanticAnalyzer.ConvertJsonType` L924-927 | `new Dictionary` + `Encoding.UTF8.GetBytes` per prop | 小（fromJSON リテラルのみ） | 条件パスのため低優先 | ✅ ReadOnlyMemory<byte> 直接ラップで二重コピー排除 |

### 11.4 優先度 C（低）: Playground 固有パス

`PlaygroundLintRunner.RunToJson` 内の JSON シリアライズ周辺。

| # | 箇所 | アロケーション | 推定影響 | 対策 |
|---|------|-------------|---------|------|
| C-1 | `PlaygroundLintRunner` L57 | `Encoding.UTF8.GetBytes(yamlSource)` | 大（入力全体コピー） | JS 側で TextEncoder を使い、SharedArrayBuffer 経由で byte[] を渡す。C# 側で string → byte[] 変換を廃止 |
| C-2 | `PlaygroundLintRunner` L64 | `new List<PlaygroundDiagnosticDto>()` | 小 | フィールド化して Clear + 再利用 |
| C-3 | `PlaygroundLintRunner` L74 | `d.Severity.ToString()` per diagnostic | 小 | static readonly string[] ルックアップテーブルに変更 |
| C-4 | `PlaygroundLintRunner` L87 | `JsonSerializer.Serialize(list, ...)` | 中 | `Utf8JsonWriter` + `IBufferWriter<byte>` ベースに変更し、中間 string 生成を排除。JS に byte[] を渡して TextDecoder で変換 |
| C-5 | `FixEngine.Apply` L175-232 | `new List<>`, `.ToArray()`, `Encoding.UTF8.GetBytes`, `new byte[][]`, `new byte[]` | 中（fix 適用時） | fix 適用は頻度が低いため低優先 |

### 11.5 優先度 D（根本的変更）: ParseResult/Diagnostic の所有権モデル

現在の設計: `ParseResult` と `LintResult` は `Diagnostic[]` を所有する `readonly record struct`。配列は毎回新規コピー。

| # | 変更 | 概要 | 影響範囲 |
|---|------|------|---------|
| D-1 | **ParseResult を PooledBuffer 所有に変更** | `Diagnostic[]` → `PooledBuffer<Diagnostic>` を内部保持、外部には `ReadOnlySpan<Diagnostic>` を返す。ParseResult を IDisposable にして PooledBuffer を返却 | ParseResult の全消費者、テスト |
| D-2 | **LintResult を PooledBuffer 所有に変更** | 同上。`_resultDiagnostics` の two-buffer swap パターンを廃止し、PooledBuffer の所有権移転に一本化 | LintResult の全消費者、CLI Output, Playground |
| D-3 | **Diagnostic.Message を Utf8String 化** | `string Message` → `Utf8String Message`。表示時のみ UTF-8 → string 変換。全ルールの診断メッセージ生成が zero-copy に | 全ルール、CLI 出力、Playground DTO、テスト |
| D-4 | **SliceMap を ArrayPool-backed に** | `Entry[]` を ArrayPool から取得し、AstArena 経由で一括管理。PooledBuffer.ToArray() を廃止 | WorkflowParser 全体、全 SliceMap 消費者 |

### 11.6 優先度 D-5（根本的変更）: インクリメンタルパース — 差分からの AST 構築

#### 11.6.1 前提: Playground の利用パターン

ユーザーの操作パターンを分類すると:

| パターン | 頻度 | 変化量 |
|---------|------|--------|
| キー入力（1-3 文字追加/削除） | **99%** | 1-50 bytes |
| 行の追加/削除（Enter, Backspace で行消去） | 80% (上記に含む) | 1-200 bytes |
| コピペ（大きなブロックの挿入/置換） | 1% 未満 | 100-5000 bytes |
| 全体書き換え（URL から取得/テンプレート選択） | 極稀 | 全バイト |

99% のケースでは **前回パースした AST の大半が再利用可能** である。例えば 6 jobs × 8 steps のワークフローで 1 step の `run:` 値を編集した場合、残り 47 steps + 5 jobs + on/env/permissions/defaults/concurrency は全く変わっていない。

#### 11.6.2 現在のアーキテクチャの制約

```
VYamlStreamAdapter → forward-only streaming parser
  ├─ FromBytes(Memory<byte>) — 常にバイト 0 から開始
  ├─ Read() → 次のトークンに進む（巻き戻し不可）
  └─ SkipCurrentNode() → サブツリーを高速スキップ
```

**VYaml は任意オフセットへのシーク不可**。`YamlParser.FromBytes()` は常にバイト先頭からトークン化を開始する。YAML のインデント依存構文により、ドキュメント途中からのパースは原理的に不安全（前のインデントレベルの文脈がないとスカラーとブロックの判定ができない）。

ただし、以下の既存機能はインクリメンタル化と親和性がある:
1. **AstArena のオブジェクトプール** — Job/Step/ExecRun/ExecAction は AllocXxx() で取得、Dispose() で Reset & 返却
2. **Utf8Slice** — ソースバイト配列への zero-copy 参照。ソースが変わればスライスのオフセットは無効化されるが、セクション単位で不変であれば再利用可能
3. **SliceMap の線形スキャン** — Entry[] のキーがソースバイト列へのスライスなので、ソースが同一ならそのまま使える
4. **TextRange** — 各ノードが自身のバイト範囲を保持

#### 11.6.3 インクリメンタルパースのアプローチ比較

| アプローチ | 概要 | VYaml 互換 | アロケーション削減 | 実装難易度 |
|-----------|------|-----------|-----------------|-----------|
| **A. セクション単位のバイト比較 + 選択的再パース** | ルートセクション (on/jobs/env/...) の前回バイト範囲を記録し、変更がないセクションの AST を丸ごと再利用 | ○ (全体トークン化は維持) | 大 (未変更セクションの AST 構築を全スキップ) | 中 |
| **B. ジョブ単位の差分検出 + 部分再構築** | A の拡張。jobs マッピング内で各 job のバイト範囲を記録し、変更された job のみ再パース | ○ | 非常に大 | 中〜高 |
| **C. Tree-sitter 式の完全インクリメンタル** | 編集操作 (offset, deleteLen, insertLen) からツリーの無効化範囲を算出し、最小サブツリーのみ再パース | × (VYaml 置換必須) | 最大 | 非常に高 |
| **D. AST キャッシュ + コンテンツハッシュ** | 各セクションの XXH64 ハッシュを保持し、再パース後に前回 AST と比較して未変更ノードを差し替え | ○ | 中 (パースは毎回だが AST ノード割当を回避) | 低〜中 |

#### 11.6.4 推奨: アプローチ A+B「セクション単位選択的再パース」

**なぜ A+B か:**
- VYaml の forward-only 制約内で実現可能
- YAML のルートマッピングは GitHub Actions では **固定キーセット** (name, run-name, on, jobs, env, permissions, defaults, concurrency) であり、セクション境界が明確
- jobs 内も **job ID をキーとするフラットマッピング** であり、各 job の開始/終了バイト位置は一意に特定可能
- 99% のケースで変更は 1 job 内に閉じるため、残りの jobs + 全ルートセクションの AST を丸ごと再利用できる

**アルゴリズム概要:**

```
Phase 0: 初回パース (従来通り)
  1. VYaml で全体をトークン化 + AST 構築
  2. 副産物として「セクションレジストリ」を構築:
     SectionRegistry = {
       "on":          { startOffset: 45,  endOffset: 120,  hash: 0xABC... },
       "jobs":        { startOffset: 121, endOffset: 980,  hash: 0xDEF... },
       "jobs/build":  { startOffset: 135, endOffset: 520,  hash: 0x123... },
       "jobs/deploy": { startOffset: 521, endOffset: 970,  hash: 0x456... },
       "env":         { startOffset: 30,  endOffset: 44,   hash: 0x789... },
       ...
     }
  3. ParseResult + SectionRegistry + 前回ソース byte[] を保持

Phase 1: 差分検出 (次回 Check 呼び出し時)
  1. 新旧ソースの長さを比較 → 差分 delta を算出
  2. 編集位置 (editOffset) を特定:
     - 先頭から一致する最長プレフィックス長
     - 末尾から一致する最長サフィックス長
     → editRegion = [prefixLen, newLen - suffixLen)
  3. SectionRegistry を走査し、editRegion と重なるセクションを「無効」とマーク
  4. 重ならないセクションは「有効」(再利用可能)

Phase 2: 選択的再パース
  1. VYaml で全体をトークン化開始 (forward-only 制約のため)
  2. ルートマッピングのキーを読むたびに:
     - そのセクションが「有効」→ reader.SkipCurrentNode() + 前回 AST ノードを流用
     - そのセクションが「無効」→ 通常通りパースして新 AST ノードを構築
  3. jobs マッピング内でも同様:
     - 各 job キーのバイト範囲が有効 → SkipCurrentNode() + 前回 Job ノードを流用
     - 無効 → ParseJob() を実行

Phase 3: オフセット補正
  - 「有効」セクションの AST ノード内の TextRange/Utf8Slice は前回ソースの
    オフセットを参照している
  - 新ソースでのオフセットは delta (editRegion の挿入/削除量) だけシフトしている
  - 対策:
    a) 再利用ノードの全 TextRange に delta を加算する (O(node count) だが allocation-free)
    b) または、ソースへの参照を「セクション相対オフセット」にする (大きな設計変更)
    c) または、lint 側が「このノードは旧ソース参照」と認識し、
       diagnostic 生成時にのみ新ソースのオフセットに変換する
```

#### 11.6.5 パフォーマンス/アロケーション観点の実現可能性分析

**削減効果の定量見積もり (Large: 6 jobs × 8 steps, 1 step を編集):**

| 項目 | 現在 (毎回フルパース) | インクリメンタル後 | 削減率 |
|------|---------------------|------------------|--------|
| Job オブジェクト構築 | 6 × AllocJob() + フィールド設定 | 1 × AllocJob() | 83% |
| Step オブジェクト構築 | 48 × AllocStep() + ExecRun/ExecAction | 8 × AllocStep() | 83% |
| SliceMap Entry[] コピー | ~20 個の SliceMap × ToArray() | ~3 個 (変更 job 内のみ) | 85% |
| Diagnostic[] (パーサー) | 全体パース分 | 変更セクション分のみ | 50-90% |
| ExpressionParser 呼出 | ~98 式 | ~12 式 (1 job 分) | 88% |
| AstArena scalar 登録 | 全ノード分 | 変更セクション分のみ | 80-90% |
| **合計推定** | **~113 KB/call** | **~20-30 KB/call** | **~75%** |

**アロケーション面の要注意点:**

1. **SectionRegistry 自体のコスト**: セクション数は固定的 (ルート 7-8 + jobs N 個)。`struct[]` フィールドで保持すれば追加アロケーションは初回のみ。

2. **前回 AST ノードの保持**: 再利用のために前回の `Workflow`, `Job[]`, `Step[]` を保持する必要がある。AstArena の Dispose タイミングを変更し、「前回 Arena を次回パースまで保持」する設計に。
   - 問題: 現在 `AstArena.Dispose()` で ThreadStatic キャッシュに返却している。前回 Arena を保持するなら 2 つの Arena が同時に存在する。
   - 対策: Playground 専用の `IncrementalParseContext` が前回 Arena の参照を持ち、次回パース完了後に旧 Arena を Dispose する。

3. **オフセット補正のコスト**: Phase 3 の補正は allocation-free だが、再利用ノード全ての TextRange を書き換える必要がある。Job は ~20 フィールドに TextRange を持つ → 6 jobs で ~120 フィールド書き換え。これは数百ナノ秒で完了し、パース時間 (~1ms) に対して無視可能。

4. **Utf8Slice の無効化問題**: Utf8Slice は `(int Offset, int Length)` でソース byte[] を参照する。ソース byte[] が変わると全スライスが無効になる。
   - 対策: 再利用セクションでは **旧ソース byte[] も保持** し、lint 時に `arena.Source` としてではなく、セクションごとに適切なソースを参照する。
   - **これは複雑すぎる** → 代替: オフセット補正後のスライスが新ソースの同じバイト列を指すことを保証する（未変更セクションのバイト内容は同一のため、新ソース上の補正済みオフセットで正しいバイト列を指す）。

5. **VYaml トークン化は全体を走る**: forward-only 制約のため、VYaml 自体は常に全バイトをトークン化する。削減されるのは **AST 構築コスト** (ノードオブジェクト + SliceMap Entry[] + scalar 登録) のみ。VYaml のトークン化コストは残る。
   - ベンチマーク参考: ParseWorkflowFull (Large) のうち VYaml トークン化は ~30-40% を占める推定。AST 構築が 60-70%。
   - つまりインクリメンタル化しても **パース時間は最大 60-70% 削減** で、30-40% は VYaml オーバーヘッドとして残る。

#### 11.6.6 技術的リスクと課題

| リスク | 深刻度 | 対策 |
|--------|--------|------|
| **YAML アンカー/エイリアス** がセクション境界を跨ぐ場合、再利用ノードが古いアンカー定義を参照する | 中 | GitHub Actions ではアンカーは稀。アンカー検出時はフルパースにフォールバック |
| **インデント変更** がルートレベルで発生するとセクション境界が全てシフトする | 高 | 先頭プレフィックス一致で editRegion がルートマッピング開始前なら全セクション無効化 (フルパース) |
| **jobs キーの追加/削除/リネーム** で SectionRegistry が無効化される | 中 | jobs マッピング自体が無効ならその中の全 job を再パース。他のルートセクションは再利用可能 |
| **VYaml の SkipCurrentNode() が正確にセクション終端までスキップすることの保証** | 低 | 既存実装でテスト済み。MappingStart → 対応する MappingEnd まで正確にスキップする |
| **LintEngine が新旧混在 AST を正しく処理できるか** | 中 | 再利用ノードのオフセットが補正済みであれば、lint 側は通常の AST と区別なく処理可能。ただし `arena.Source` は新ソースを指すため、再利用ノードの Utf8Slice が新ソース上で同じバイト列を指すことの検証が必要 |
| **エラーリカバリの整合性**: 前回パースでエラーがあったセクションを「有効」として再利用すると、修正後もエラーが残り続ける | 高 | パーサー診断が 0 でないセクションは常に「無効」として再パース対象にする |

#### 11.6.7 アプローチ D「AST キャッシュ + コンテンツハッシュ」も有力

アプローチ A+B の代替として、よりシンプルなアプローチ:

```
1. 毎回フルパースする (VYaml トークン化 + AST 構築は従来通り)
2. パース完了後、新旧 AST のルートセクション/Job を XXH64 で比較
3. 一致するノードは旧 AST から参照をコピー (新 AST ノードは破棄)
4. 不一致ノードのみ新 AST を採用
```

**利点:**
- パーサーコードの変更が最小 (パース後の後処理のみ)
- VYaml の制約を気にする必要なし
- セクション境界の追跡不要

**欠点:**
- パース自体のアロケーション (PooledBuffer, SliceMap Entry[], scalar 登録) は毎回発生する
- 削減効果は「AST ノードの保持コスト」のみ → D-5 本来の目的 (パース時のアロケーション削減) には貢献しない
- **結論: このアプローチはアロケーション削減には不向き** (パース後にノードを捨てても、パース中のアロケーションは既に発生している)

#### 11.6.8 結論と推奨

**インクリメンタルパースは技術的に実現可能だが、段階的に導入すべき。**

**推奨ロードマップ:**

| Phase | 内容 | 削減効果 | 前提条件 |
|-------|------|---------|---------|
| D-5a | **SectionRegistry の記録** — 初回パース時にルートセクション + 各 job のバイト範囲と XXH64 を記録する仕組みを追加。パース動作自体は変更しない。 | なし (計測基盤) | なし |
| D-5b | **ルートセクション選択的スキップ** — on/env/permissions/defaults/concurrency が前回と同一バイト列なら `SkipCurrentNode()` + 前回 AST ノードをそのまま再利用。jobs は毎回パース。 | ~10-15% | D-5a |
| D-5c | **Job 単位選択的スキップ** — 各 job のバイト範囲が前回と同一なら SkipCurrentNode() + 前回 Job/Step[] を再利用。オフセット補正を実装。 | ~60-75% | D-5b + オフセット補正 |
| D-5d | **Lint 結果キャッシュ** — 未変更 job に対する lint 診断を前回結果から再利用 (job 単位で diagnostic[] をキャッシュ) | ~80-90% (lint 含む) | D-5c + LintEngine 変更 |

**現実的な初手:**

D-5a + D-5b は **低リスク** で実装可能。Playground 専用パスとして `IncrementalParseContext` を導入し、CLI パスには一切影響を与えない。

D-5c は **中リスク** だが最大の効果を持つ。オフセット補正の正確性を保証するテストが必要。

D-5d は LintEngine の根本的な変更を伴うため Phase 4 (D-1 〜 D-4) の後に検討。

#### 11.6.9 Phase D-5 に必要な設計変更サマリ

```csharp
// 新規: Playground 専用のインクリメンタルパースコンテキスト
internal sealed class IncrementalParseContext
{
    // 前回パース結果
    private byte[]? _previousSource;
    private ParseResult _previousResult;
    private AstArena? _previousArena;  // 前回 Arena を保持 (Dispose せず次回まで維持)
    private SectionRegistry _registry;

    // 差分検出
    public EditRegion DetectEditRegion(byte[] newSource);

    // セクション有効性判定
    public bool IsSectionValid(SectionId id, byte[] newSource);
}

// 新規: セクションバイト範囲 + ハッシュの記録
internal struct SectionRegistry
{
    // ルートセクション (固定数: 最大 8)
    public SectionEntry On, Jobs, Env, Permissions, Defaults, Concurrency, Name, RunName;

    // Job 単位 (可変長 — フィールド配列で保持)
    public SectionEntry[] JobEntries;  // capacity は前回 job 数で固定、拡張時のみ再割当
    public int JobCount;
}

internal readonly struct SectionEntry
{
    public readonly int StartOffset;
    public readonly int EndOffset;
    public readonly long ContentHash;  // XXH64
    public readonly bool HasDiagnostics;  // true なら常に再パース対象
}

internal readonly struct EditRegion
{
    public readonly int Start;   // 変更開始バイトオフセット
    public readonly int End;     // 変更終了バイトオフセット (旧ソース上)
    public readonly int Delta;   // 新ソースと旧ソースの長さの差
}
```

**アロケーション影響:**
- `IncrementalParseContext`: Playground 専用の static フィールド。1 インスタンスのみ。
- `SectionRegistry`: struct フィールド。JobEntries は `SectionEntry[]` で job 数が変わらない限り再利用。
- `EditRegion`: readonly struct、スタック割当。
- 前回 Arena の保持: 2 つの AstArena が同時存在するが、旧 Arena のスカラー配列は次回パース完了後に即 Dispose。

#### 11.6.10 D-5 と D-1〜D-4 の関係

D-5 (インクリメンタルパース) は D-1〜D-4 と **独立に** 実装可能:
- D-1 (ParseResult PooledBuffer 化) は「パース結果の保持方法」の変更であり、インクリメンタル化とは直交
- D-4 (SliceMap ArrayPool 化) はインクリメンタルで再利用する場合にも有効 (未変更 job の Entry[] をそのまま保持)
- D-5 は **Playground 専用パス** として分離できるため、CLI/テストへの影響なし

**実装順序の推奨:**
1. D-1, D-4 (所有権モデル + SliceMap) → 全パスで恒常的にアロケーション削減
2. D-5a, D-5b (SectionRegistry + ルートセクションスキップ) → Playground で追加削減
3. D-5c (Job 単位スキップ) → Playground で大幅削減
4. D-2, D-3 (LintResult + Diagnostic.Message) → 影響範囲が最大のため最後

### 11.7 推奨実装順序 (改訂版)

**Phase 1: 低リスク・高効果（S-1 〜 S-6, A-7, A-8）**
- WorkflowParser の tuple buf を stackalloc/static 化
- ParseCore の `List<Diagnostic>` → PooledBuffer
- BuildKnownJobIdSlices のフィールド化
- RunContextDirectUseAnalyzer.IsPowerShell の UTF-8 span 化
- NormalizeExclusions のフィールド化
- 推定削減: 毎回の Check() で 3-8 個のコレクション/配列新規生成を排除

**Phase 2: 中リスク・高効果（S-7, S-8, S-9, A-1）**
- ExpressionSemanticAnalyzer の List → PooledBuffer/共有バッファ
- ValidateFunctionCall の ExprType[] stackalloc 化
- DynamicContextTypeBuilder の Dictionary 再利用
- 推定削減: 式が多い大規模ワークフローで数十〜数百個のコレクション/配列生成を排除

**Phase 3: 中リスク・中効果（A-2 〜 A-6, C-2, C-3）**
- ExprUndefinedVarRule の 1/2 要素配列フィールド化
- Decode() の遅延化（ルール個別）
- Playground DTO の小最適化
- 推定削減: ルールごとの per-step 文字列変換を削減

**Phase 4: 高リスク・大効果（D-1, D-4）**
- ParseResult の所有権モデル変更 (PooledBuffer 保持)
- SliceMap の ArrayPool 化
- 推定削減: 根本的に配列コピーを排除

**Phase 5: Playground 専用 — インクリメンタルパース（D-5a 〜 D-5c）**
- SectionRegistry の記録基盤
- ルートセクション選択的スキップ
- Job 単位選択的スキップ + オフセット補正
- 推定削減: Playground per-call で ~75% のアロケーション削減 (113 KB → ~30 KB)

**Phase 6: 高リスク・大効果（D-2, D-3）**
- LintResult の所有権モデル変更
- Diagnostic.Message の Utf8String 化
- 推定削減: 根本的に文字列変換を排除。影響範囲が最大のため最後

**Phase 7: Playground 専用 — Lint 結果キャッシュ（D-5d）**
- 未変更 job に対する lint 診断の前回結果再利用
- 推定削減: Playground per-call で ~90% の総合削減 (lint 時間含む)
