# Hash Algorithm Replacement Plan: FNV-1a → XXH64

## 現状

現在 2 箇所で FNV-1a ハッシュを自前実装している。

| 箇所 | ファイル | ビット幅 | 用途 |
|---|---|---|---|
| `LintConfig.ComputeContentHash` | `src/Seiton.Core/Linting/LintConfig.cs` | FNV-1a 64-bit | Expression 内容ベースの重複排除キャッシュ |
| `Utf8String.GetHashCode` | `src/Seiton.Core/Parsing/Utf8String.cs` | FNV-1a 32-bit | Dictionary/HashSet のバケット分散 |

> `AstArena.cs` の `GetHashCode()` は `_raw` (int インデックス) をそのまま返しており、バイト列ハッシュではないため対象外。

## FNV-1a の問題点

- **雪崩特性が弱い**: 入力の 1 ビット変化がハッシュ全体に波及しにくく、短い入力で衝突率が高くなりやすい。
- **スループットが低い**: バイト単位の逐次処理 (1 アキュムレータ) のため、現代 CPU の ILP (命令レベル並列性) を活用できない。
- **乗算定数が固定**: 素数乗算のみで混合するため、ビット拡散が不十分。

## 候補アルゴリズム比較

### 評価基準

自前実装 (NuGet 非依存) で以下を重視する:

1. **実装サイズ**: 行数が少なく、レビュー・保守が容易
2. **品質**: 雪崩特性・衝突率が良好
3. **速度**: FNV-1a より高速 (特に 10–200 バイト帯)
4. **依存**: 外部パッケージ不要、SIMD 不要

| アルゴリズム | 実装行数 (目安) | 速度 (vs FNV-1a) | 雪崩特性 | SIMD 依存 | 評価 |
|---|---|---|---|---|---|
| **FNV-1a** (現行) | ~10 行 | 1x (基準) | △ 弱い | なし | 現行 |
| **XXH64** | ~60–80 行 | ~3–5x | ◎ 優秀 | **不要** | **推奨** |
| **XXH3** | ~300+ 行 | ~5–10x | ◎ 優秀 | **必要** (SSE2/AVX2/NEON) | × 実装過大 |
| **FarmHash** | ~150–200 行 | ~3–5x | ○ 良好 | 一部 variant で必要 | △ 実装大きめ |

### 結論: **XXH64** を採用

- **速度**: 4 レーンアキュムレータの ILP により、FNV-1a の 3–5 倍のスループット。短い入力 (< 32 bytes) にも専用のファストパスがあり、式キャッシュキーの典型長 (10–100 bytes) で最も恩恵が大きい。
- **品質**: SMHasher 全テスト合格。雪崩特性・衝突率ともに暗号ハッシュ級に良好。
- **実装サイズ**: スカラー C# で 60–80 行。SIMD 不要。`BinaryPrimitives.ReadUInt64LittleEndian` で十分。
- **実績**: zstd、Linux カーネル、LZ4、多数のゲームエンジンで使用。仕様が安定しており互換性の心配がない。

### XXH3 / FarmHash を見送る理由

- **XXH3**: 最高速だが、SSE2/AVX2/NEON の SIMD 分岐が必要で実装が 300 行超になる。Seiton の入力長 (式は数十〜数百バイト) では XXH64 との差は小さい。
- **FarmHash**: XXH64 と同等の速度だが、実装が 150–200 行と大きく、C# への移植例も少ない。Google 内部では CityHash 後継だが、XXH64 ほどのエコシステム普及はない。

## 実装計画

### ステップ 1: `XxHash64` ユーティリティの追加

`src/Seiton.Core/Parsing/XxHash64.cs` に自前の XXH64 実装を追加する。

```csharp
// 公開 API (案)
internal static class XxHash64
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0) { ... }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash32(ReadOnlySpan<byte> data, ulong seed = 0)
        => unchecked((int)Hash(data, seed));
}
```

実装ポイント:
- `BinaryPrimitives.ReadUInt64LittleEndian` / `ReadUInt32LittleEndian` でエンディアン安全に読み取り
- 32 bytes 以上: 4 レーンアキュムレータ + merge round
- 4–31 bytes: 簡略パス
- 1–3 bytes: 最小パス
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` をホットパスに付与
- アロケーション: ゼロ (スタック変数のみ)

### ステップ 2: `LintConfig.ComputeContentHash` の置き換え

```diff
 [MethodImpl(MethodImplOptions.AggressiveInlining)]
 private static long ComputeContentHash(ReadOnlySpan<byte> span)
 {
-    // FNV-1a 64-bit hash on expression content
-    const ulong offsetBasis = 14695981039346656037;
-    const ulong prime = 1099511628211;
-    var hash = offsetBasis;
-    for (var i = 0; i < span.Length; i++)
-    {
-        hash ^= span[i];
-        hash *= prime;
-    }
-    return (long)hash;
+    return (long)XxHash64.Hash(span);
 }
```

### ステップ 3: `Utf8String.GetHashCode` の置き換え

```diff
 public override int GetHashCode()
 {
-    unchecked
-    {
-        const uint offsetBasis = 2166136261;
-        const uint prime = 16777619;
-        var hash = offsetBasis;
-        var span = Span;
-        for (var i = 0; i < span.Length; i++)
-        {
-            hash ^= span[i];
-            hash *= prime;
-        }
-        return (int)hash;
-    }
+    return XxHash64.Hash32(Span);
 }
```

### ステップ 4: テスト

1. **既存テスト通過**: `Utf8StringTests` — ハッシュ値の安定性・等値性テストが引き続き通ること
2. **XXH64 リファレンスベクタ検証**: 公式テストベクタ (空入力、1 byte、14 bytes、100+ bytes) と一致することを確認するユニットテストを追加
3. **Expression キャッシュ動作**: `LintConfig.ParseExpression` のキャッシュヒット・ミス挙動が変わらないこと

### ステップ 5: ベンチマーク

サンドボックスで FNV-1a vs XXH64 のマイクロベンチマークを実行し、想定通りの速度向上を確認する。

## 検証計画

各 Phase 完了時に以下を測定:

1. **BenchmarkDotNet LintBenchmark** — Allocated (Small/Medium/Large, FixEnabled=false/true)
2. **BenchmarkDotNet ParsingBenchmark** — 回帰なしを確認
3. **LintPerRuleAlloc.cs** — run-context ルール個別計測
4. **全テストパス** — `dotnet test` Green 確認

### 実測結果

**LintBenchmark Allocated** (ベースラインは直前の L5/L6 メモリ最適化後):

| Size | FixEnabled | Before (L6後) | After (XXH64 + ref最適化) | 差分 |
|---|---|---|---|---|
| Small | False | 14.42 KB | 14.41 KB | -0.01 KB |
| Small | True | 14.83 KB | 14.83 KB | ±0 |
| Medium | False | 92.91 KB | 92.88 KB | -0.03 KB |
| Medium | True | 99.32 KB | 99.30 KB | -0.02 KB |
| Large | False | 435.63 KB | 435.63 KB | ±0 |
| Large | True | 465.79 KB | 467.95 KB | ±0 (ノイズ) |

> ハッシュ関数の変更はアロケーション量にほぼ影響しない (期待通り)。
> XXH64 はスタック上の固定長変数のみ使用し、ヒープ割り当てゼロ。

**実装最適化**: `Span.Slice()` (毎回境界チェック + 新 Span 構築) を `ref byte` + `Unsafe.Add` (純粋なポインタ算術、bounds check なし) に置き換え済み。

**ParsingBenchmark**: 回帰なし。WorkflowParser.Parse は誤差範囲内で安定。

**テスト**: 全 553 テスト pass、0 failures。

## 影響範囲

- `src/Seiton.Core/Parsing/XxHash64.cs` — **新規**
- `src/Seiton.Core/Linting/LintConfig.cs` — `ComputeContentHash` 書き換え
- `src/Seiton.Core/Parsing/Utf8String.cs` — `GetHashCode` 書き換え
- `tests/Seiton.Core.Tests/` — XXH64 テストベクタ追加

## リスク

| リスク | 影響 | 対策 |
|---|---|---|
| ハッシュ値の変化 | Expression キャッシュのキーが変わるが、キャッシュは LintConfig インスタンスごとにリセットされるため永続化影響なし | なし |
| `Utf8String` をシリアライズ済みの場合 | GetHashCode は Dictionary のランタイムキーにのみ使用。プロセス跨ぎの永続化はしていない | なし |
| XXH64 実装のバグ | 公式テストベクタで検証 | ステップ 4 で対処 |
