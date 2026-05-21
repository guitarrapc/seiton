# Plan: configuration.md & Parser Spec Review

## 1. 総合所見

### 1.1 configuration.md (592行)

**役割**: ユーザーが README → Installation → Usage と進み、ルール検出を調整したいときに参照する第一級ドキュメント。

**現状評価**: 概ね良好。構成は論理的で、Annotated Example が config 全体像を一覧できる。以下に改善余地あり:

| 強み | 弱み |
|---|---|
| Annotated Example で全セクション俯瞰可能 | Config エラー時の挙動説明がない (unknown key → どうなる? 表示はどう出る?) |
| Exclusion の4パターン明示 | `SEITON_CONFIG` / `--config` の優先順序が本文散在 (Trust節 vs 冒頭) |
| Inline directive の placement 注意点が具体例付き | Loader resource limits が突然内部仕様を説明 (ユーザー目線で不要な詳細) |
| Defaults Reference テーブルで全デフォルト一覧 | `fix.pinning.ignore-actions` が Annotated Example と "Network-Assisted SHA Pinning" で重複説明 |
| 各ルール固有オプションのテーブル | テーブルの Key 列と実際の YAML パスが対応しない場合がある (例: `events.extend` vs 実YAML `events: extend:`) |

### 1.2 Seiton_Parser_spec.md (1454行)

**役割**: パーサー契約 (AST構造、パースアルゴリズム、式パーサー、セマンティック検証)。言語中立。

**現状評価**: 仕様として適切な粒度。以下の観点:

| 強み | 弱み |
|---|---|
| §2 AST定義がフィールド一覧で簡潔 | §7.2.1 Context Availability テーブルが巨大 (80行) — 生成データのスナップショット |
| §3 パースアルゴリズムが擬似コードで明確 | §10.3 Diagnostic Message Format が非常に長い (Principle 1–6a + 2つの normative テーブル ≈ 200行) |
| §11 Allowed Keys が一覧で参照可能 | §3.4.2a Unknown Key Suggestion が実装詳細に踏み込みすぎ (Levenshtein 閾値、修正付きFix) |
| §6 Expression EBNF が完結 | §14 Polymorphic Field Handling が §3 と重複するまとめ |
| §5 Error Recovery が表で簡潔 | |

### 1.3 Seiton_Parser_csharp_spec.md (1468行)

**役割**: C# 実装仕様。パフォーマンス設計、型ボキャブラリ、Adapter 層。

**現状評価**: 共有 spec と重複が多い。

| 強み | 弱み |
|---|---|
| §0.4 Adapter Layer が設計根拠含め包括的 | §0.1.2 parity table (60行) が全行 "Implemented" → 情報密度低い |
| §0.5 Zero-Allocation Policy が明確 | §2 AST Definitions が shared spec の C# コード版の再掲 (300行) — shared を参照すべき |
| Appendix A/B Mapping が実装追跡に有用 | Appendix A (120行) は全行 "Implemented" で、changelog 用メモ化している |

### 1.4 Seiton_Parser_go_spec.md (1292行)

**役割**: Go 実装仕様。AST型定義 (Go struct)、パースアルゴリズム。

**現状評価**: C# spec と同じ構成問題。Go struct 定義が shared spec 型のコードリテラル再掲。

---

## 2. 修正提案 (優先度順)

### P0: configuration.md — ユーザー体験上の欠落

| # | 問題 | 修正案 |
|---|---|---|
| P0-1 | **Config エラー時の挙動がない** — unknown key/unknown rule ID を書いたらどうなるかユーザーに伝わらない | §Config File Format 直下に "Error Reporting" 小節を追加: unknown top-level key → stderr にエラー + exit 1、unknown rule ID → 同様、無効な severity 値 → 同様。`--verbose` で解決パス表示 |
| P0-2 | **`--config` / `SEITON_CONFIG` / discovery の優先順序** が Trust 節と冒頭の例に分散して一目で分からない | §Config File Location 冒頭に 3行箇条書きで明示: `1. --config (最優先) 2. SEITON_CONFIG 3. discovery order`。Linter spec §5.10 と整合確認済 |
| P0-3 | **Rule-Specific Options テーブルの Key 列** — `events.extend` はユーザーが YAML でどう書くか直感的でない | テーブル列を「Config YAML Path」に改名し `rules.dangerous-triggers.events.extend` のように完全パスで記載。あるいは現行テーブルの直後に "The YAML shape follows nested keys" と1行注記 |

### P1: configuration.md — 冗長性/構成

| # | 問題 | 修正案 |
|---|---|---|
| P1-1 | **Loader resource limits** がユーザー向け情報としては過剰 | "Implementation Notes" 折りたたみ or ファイル末尾 Appendix に移動。ユーザーが気にするのは「1MB 以上のコンフィグは受け付けない」程度 |
| P1-2 | **fix.pinning の重複説明** — Annotated Example (L170) と "Network-Assisted SHA Pinning" (L445) で設定例が同じ内容 | Annotated Example にコメントで full-config を示し、後続節は「Annotated Example の `fix.pinning` を参照」+ CLI flag のみに縮小 |
| P1-3 | **Tuning for Sample/Demo Repositories** が中途半端** — "Disable noisy rules" と "Combine strategies" の例がほぼ同じ | 2つを1つにマージ。「Demo repo 向け推奨 config」を1ブロックで示し、なぜそう設定するかの1行注釈 |

### P2: Seiton_Parser_spec.md — 仕様肥大化

| # | 問題 | 修正案 |
|---|---|---|
| P2-1 | **§7.2.1 Context Availability テーブル** — 生成データのスナップショットであり spec に静的コピーがあると陳腐化リスク | テーブルを「normative summary (3行要約 + 重要観察点 §7.2.3)」に圧縮し、完全テーブルは `data/sources/availability/` 参照と明記。または折りたたみ `<details>` (GitHub renders) |
| P2-2 | **§10.3 Diagnostic Message Format (≈200行)** — Principle 1–6a + normative テーブル2つが長い | Principle 1–4 は 必須コア (残す)。Principle 5–6a (dotted-path prefix) は「§10.3.2 Location Prefix Convention」に分離し、normative テーブルは Appendix 化 |
| P2-3 | **§3.4.2a Unknown Key Suggestion** — Levenshtein 閾値 (≤4→1, ≤8→2, >8→3)、Fix 添付、scope 一覧が HOW レベル | 契約として残す: 「unknown key に対し距離ベース suggestion + fix が提供される」。閾値とスコープ一覧は実装ドキュメント or テストで保証 (spec からは "distance-based suggestion" のみ) |
| P2-4 | **§14 Polymorphic Field Handling** — §3 の各 Parse 節と情報重複 | §14 を「Quick Reference (Summary Table)」として明示し、§3 が normative であることを注記。あるいは §3 各所から §14 に forward-ref を置いて §14 を canonical にする |

### P3: Parser Language Specs — 共有 spec との重複

| # | 問題 | 修正案 |
|---|---|---|
| P3-1 | **C# spec §2 AST Definitions (300行)** — shared spec §2 の C# struct 版再掲 | 「フィールドセマンティクスは shared spec §2 を参照。ここでは C# 型シグネチャのみ定義」と注記し、各 sealed class 定義は残すが field description を削除 → ≈ -100行 |
| P3-2 | **C# spec §0.1.2 parity table** — 全行 "Implemented" | テーブルを折りたたみ or Appendix に移動。Preamble に「全カテゴリ実装済み。詳細は Appendix C 参照」1行で済ます |
| P3-3 | **C# spec Appendix A** — 全行 "Implemented" で情報密度ゼロ | Status 列を削除し、Spec→C# マッピングテーブルのみ残す (追跡用) |
| P3-4 | **Go spec §2 AST Definitions** — Go struct リテラルが shared spec §2 と同じ情報 | C# spec と同じ方針: shared spec 参照 + Go 型シグネチャのみ |

---

## 3. 非修正 (現状維持の判断)

| 項目 | 理由 |
|---|---|
| Shared spec §6 Expression EBNF | コンパクトで仕様として必要十分 |
| Shared spec §11 Allowed Keys | 一覧表として参照価値が高い。パースアルゴリズム §3 と相補的 |
| Shared spec §12 Mutual Constraints | テーブル1つで表が compact |
| configuration.md Inline Suppression | placement 注意点は具体例がないと伝わらない。現状の長さは妥当 |
| configuration.md Trust/CI 節 | セキュリティ情報は短縮すべきでない |

---

## 4. 実施順序

```
Phase 1 (P0): configuration.md ユーザー体験修正
  - P0-1 Error Reporting 追加
  - P0-2 優先順序明示
  - P0-3 テーブル Key 列修正 or 注記

Phase 2 (P1): configuration.md 冗長性削減
  - P1-1 Loader limits を Appendix 化
  - P1-2 fix.pinning 重複統合
  - P1-3 Demo repo 節マージ

Phase 3 (P2): Parser shared spec 圧縮
  - P2-1 Availability テーブル圧縮
  - P2-2 Diagnostic Message 分割
  - P2-3 Suggestion 節を契約レベルに抽象化
  - P2-4 §14 の位置づけ明確化

Phase 4 (P3): Language spec 重複削減
  - P3-1〜P3-4 (C# / Go 両方)
```

---

## 5. 判断基準メモ

- **configuration.md**: ユーザーが「このルールをどう設定すればいいか」「設定ミスしたらどう分かるか」「何もしなくていいデフォルトは何か」を 30秒以内に見つけられるか?
- **Parser spec**: 仕様として「新しい実装者が読んで、何を実装すれば contract 満たせるか」が分かるか? 閾値やスコープ一覧は実装テストで保証すべき内容では?
- **Language spec**: shared spec と重複なく「その言語固有の設計判断」だけが書かれているか?
