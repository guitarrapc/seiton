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
| B-1 | `WorkflowParser` 各所 | `PooledBuffer<SliceMap<T>.Entry>.ToArray()` — jobs, permissions, env, outputs 等のバッキング配列 | 大（全 SliceMap の Entry[] コピー） | SliceMap が PooledBuffer を直接保持する設計に変更。SliceMap<T> を IDisposable にし、Entry[] を ArrayPool 管理にする。AstArena の Dispose 時に一括返却 |
| B-2 | `Workflow`, `Job`, `Step` | `new Workflow()`, `new Job()`, `new Step()` — class instance | 大（各 AST ノード） | struct 化は参照サイクルとサイズの問題で困難。代替: AstArena にオブジェクトプールを追加し、Reset で再利用 |
| B-3 | `WorkflowParser.ParseCore` L533 | `new Workflow { ... }` | 中（per parse） | B-2 と連動 |
| B-4 | `ActionMetadata` | `new ActionMetadata { ... }` | 小（action metadata のみ） | B-2 と連動 |
| B-5 | `ExpressionSemanticAnalyzer.ConvertJsonType` L924-927 | `new Dictionary` + `Encoding.UTF8.GetBytes` per prop | 小（fromJSON リテラルのみ） | 条件パスのため低優先 |

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

### 11.6 推奨実装順序

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

**Phase 4: 高リスク・大効果（D-1 〜 D-4, B-1, B-2）**
- ParseResult/LintResult の所有権モデル変更
- Diagnostic.Message の Utf8String 化
- SliceMap の ArrayPool 化
- AST ノードプール化
- 推定削減: 根本的に配列コピーと文字列変換を排除。最大の効果だが影響範囲も最大

**Phase 5: Playground 固有（C-1, C-4）**
- JS interop 層の byte[] 直接受け渡し
- Utf8JsonWriter ベースのシリアライズ
- 推定削減: Playground の RunToJson 呼び出しごとの大規模文字列生成を排除
