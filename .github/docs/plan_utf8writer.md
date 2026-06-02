# Diagnostic 出力 UTF-8 化調査・対応計画

## 背景

`sarif` 出力は `Utf8JsonWriter + IBufferWriter<byte>` 化済みだが、現状の CLI 出力経路は `TextWriter` 中心であり、`text/json/github-actions` のアロケーション最小化余地が残っている。

本書は、現行実装を調査した上で「どこまで `IBufferWriter<byte>` 化すると実効的にアロケーション削減できるか」を整理し、段階的な実装方針を示す。

## 調査結果（現状）

### 1) 出力の共通入口

- 入口は `src/Seiton/Output/DiagnosticFormatter.cs` の `DiagnosticFormatter.Write(TextWriter, ...)`。
- `CheckCommand` / `FixCommand` は `Console.Out` を `TextWriter` として渡している。
- テストも `StringWriter` 注入前提で組まれている（`tests/Seiton.Tests/DiagnosticFormatterRichTextTests.cs`）。

### 2) フォーマット別の実装とアロケーション特性

- `sarif`:
  - 既に `Utf8JsonWriter` + `PooledByteBufferWriter`（`IBufferWriter<byte>`）実装。
  - ただし最終的に `WriteUtf8ToTextWriter()` で UTF-16 へデコードして `TextWriter` に書くため、出力末端は still text パス。
- `json`:
  - `JsonDiagnosticEntry[]` を作成し `JsonSerializer.Serialize(...)` で `string` 化して `TextWriter.Write`。
  - 配列確保 + JSON 文字列生成の二重アロケーションが入る。
- `text` / `github-actions`:
  - 補間文字列、`new string(...)`（ガター/キャレット/パディング）、`StringBuilder`（エスケープ）依存が多い。
  - `Diagnostic.Message` や `RuleId` は `string` を保持しており、UTF-8 直書きにしても根本の文字列生成が消えるわけではない。

### 3) `IBufferWriter<byte>` 化の実効性

- `json` は効果が高い（中間 `string` と DTO 配列を外せる）。
- `text` / `github-actions` は「完全 UTF-8 化」の工数に対し削減効果が限定的。
  - 主なアロケーション源は「文字列構築」であり、「最終書き込み先の型」ではないため。
- したがって、`text` / `github-actions` は first priority を「文字列生成削減」に置き、`IBufferWriter<byte>` は段階的導入が妥当。

## 結論（対応可否）

- 対応は **可能**。
- ただし、最大効果はフォーマット別に異なるため、以下を推奨:
  1. `json` を先に UTF-8 writer 化（高ROI）
  2. `text` / `github-actions` は先に string-side 最適化
  3. 最後に共通 UTF-8 出力経路を導入（必要に応じて）

## 対応方針（実装フェーズ）

## フェーズ 0: 計測基盤の明確化

- `src/Seiton.Benchmark/DiagnosticOutputBenchmark.cs` に `json` ベンチを追加。
- `text` / `github-actions` / `sarif` / `json` の `Allocated` と `Mean` を基準値化。
- 受け入れ基準: 以後のフェーズで allocation regressions を比較可能にする。

### フェーズ 0 実装（完了）

- `DiagnosticOutputBenchmark.WriteJson` を追加。既存 4 フォーマットと同一の `GlobalSetup`（LintEngine で実 diagnostic 生成）を共有。
- 計測条件: `Job.ShortRun`（ローカル）、`MemoryDiagnoser` 有効。

#### 基準値（フェーズ 0 計測時点、ShortRun）

| Format | Count | Mean | Allocated |
|---|---|---|---|
| text rich (baseline) | F1 | 218.6 us | 118.93 KB |
| github-actions rich | F1 | 212.2 us | 118.93 KB |
| github-actions oneline | F1 | 12.4 us | 86.24 KB |
| sarif | F1 | 60.1 us | 126.16 KB |
| **json** | F1 | **32.2 us** | **99.83 KB** |
| text rich (baseline) | F10 | 2.30 ms | 1140.67 KB |
| github-actions rich | F10 | 2.39 ms | 1156.37 KB |
| github-actions oneline | F10 | 118.6 us | 703.99 KB |
| sarif | F10 | 685.5 us | 1070.68 KB |
| **json** | F10 | **542.8 us** | **947.6 KB** |

#### フェーズ 0 レビュー

| 指摘 | 対応 |
|---|---|
| json 以外のフォーマット基準値も同一ベンチで取得すべき | 既存 4 ベンチと同一 Run で再計測し上表に記載 |
| F10 で json が Gen2 を発生（249 KB Gen2） | 現行 `JsonSerializer.Serialize` + 中間配列が原因。フェーズ 1 で改善対象 |

## フェーズ 1: `json` の UTF-8 writer 化

- `DiagnosticFormatter.WriteJson` を `Utf8JsonWriter` ベースへ置換。
- `JsonDiagnosticEntry[]` の中間配列構築を削除し、`diagnostics` から直接 JSON を書き出す。
- 互換性維持:
  - 公開 API は当面 `Write(TextWriter, ...)` を維持。
  - 改行・プロパティ名・null 出力条件（`help`）は現行と同一。
- 期待効果: `json` 出力の主要な heap allocation 削減。

## フェーズ 2: `text` / `github-actions` の string-side 最適化

- 先に `IBufferWriter<byte>` へ寄せず、以下を優先:
  - 補間文字列を `Write` 連結へ置換
  - 繰り返し `new string(...)` 箇所の再利用/軽量化
  - GitHub command escape の一時 `StringBuilder` 発生を最小化
- 期待効果: 人間可読フォーマットでの実メモリ削減を低リスクで獲得。

## フェーズ 3: UTF-8 出力経路の導入（任意/効果確認後）

- 内部に `Utf8OutputSink` 相当の抽象を導入し、`TextWriter` と `IBufferWriter<byte>` の二系統を選択可能にする。
- `CheckCommand` / `FixCommand` では machine-format（少なくとも `json/sarif`）時に UTF-8 直接出力を使えるようにする。
- テスト性維持のため、既存 `TextWriter` 経路は残す（既存テスト破壊回避）。

## フェーズ 4: 仕様・ドキュメント同期

- 実装完了後、必要に応じて以下を更新:
  - `.github/docs/Seiton_CLI_csharp_spec.md`（出力実装詳細）
  - `.github/docs/Seiton_CLI_spec.md`（挙動変更がある場合のみ）

## リスクと対策

- 互換リスク（出力文字列差分）:
  - 対策: 既存 `DiagnosticFormatterRichTextTests` を回帰テストとして維持し、文字列同値を検証。
- 工数過多リスク（text/github-actions 全面 UTF-8 化）:
  - 対策: ROI が高い `json` から段階実施し、ベンチで効果確認後に次段階判断。
- テスト注入性低下リスク:
  - 対策: `TextWriter` API を維持しつつ内部実装を差し替える。

## 実施判断

- 現時点で着手するなら、**フェーズ 1（json UTF-8 writer 化）までは即対応推奨**。
- `text/github-actions` の全面 `IBufferWriter<byte>` 化は、フェーズ 2 の計測結果を見て継続判断するのが最小リスク。
