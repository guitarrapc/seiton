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

### 4.3 [推奨] EmccMaximumHeapSize の明示設定

既定の 2GB はブラウザ側で実際に確保できないケースがある。512MB を明示設定し、起動時にランタイムが必要なメモリを予約できるようにする:

```xml
<PropertyGroup>
    <EmccMaximumHeapSize>536870912</EmccMaximumHeapSize> <!-- 512MB -->
</PropertyGroup>
```

あるいはより保守的に:
```xml
<EmccInitialHeapSize>67108864</EmccInitialHeapSize> <!-- 64MB initial -->
<EmccMaximumHeapSize>268435456</EmccMaximumHeapSize> <!-- 256MB max -->
```

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

### 4.5 [中優先] PlaygroundLintRunner を stateless 化 ✅ 実装済み

actionlint のパターンに倣い、`LintEngine` を呼び出しごとに生成する。
static `Engine` フィールドと `lock (EngineGate)` を除去し、`RunToJson` / `ApplyAllFixes` の各呼び出し（ApplyAllFixes はループ内の各パス）で `new LintEngine()` を生成する設計に変更。
これにより各呼び出し後に engine の内部バッファ（rule リスト、diagnostic リスト、HashSet 等）が全て GC 対象になる。

### 4.6 [低優先] VYaml ThreadStatic バッファの制御

VYaml 内部の ThreadStatic バッファは shrink しない設計。長期的には VYaml にパッチを当てるか、seiton 側で parse 後にリフレクションで強制クリアする（非推奨）。現実的には 4.1–4.3 で十分な効果が得られるはず。

---

## 5. 実装優先度

| # | 施策 | 難易度 | メモリ削減効果 | リスク |
|---|------|--------|--------------|--------|
| 1 | AstArena の明示的 Dispose | 低 | **大** | 低（dispose 後に Arena 参照しないことを確認済み） |
| 2 | InvariantGlobalization を全構成で有効化 | 低 | **大** | なし（lint に文化依存処理不要） |
| 3 | EmccMaximumHeapSize / EmccInitialHeapSize 設定 | 低 | 中（断片化耐性向上） | 低 |
| 4 | PlaygroundLintRunner stateless 化 | 中 | 中 | 毎回の初期化コスト増 | ✅ |
| 5 | GC.Collect 検討 | 低 | 小 | full GC 自体の OOM リスク |
| 6 | VYaml パッチ | 高 | 小 | 上流変更管理 |

---

## 6. 推奨実装順序

1. **4.1 + 4.2 + 4.3 を同時に実装**（数分で完了、最大効果）— 4.1 は実装済み
2. 改善後に再テスト
3. ~~それでも OOM が発生する場合は 4.5 を検討~~ → 4.5 も実装済み
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
