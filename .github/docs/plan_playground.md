# Playground Permanent URL: config が復元されない原因と対処

## 結論

Playground の Permanent URL は、現在 **editor の YAML本文のみ** を URL hash (`#...`) に保存する実装です。
そのため URL にアクセスしても config editor の内容は復元されません。これは障害というより、現行仕様と実装の一致による挙動です。

## 根本原因

1. **保存対象が YAML 本文だけ**
   - `permalinkBtn` の click ハンドラでは `editor.getValue()` だけを `deflate + base64` して hash に入れている。
2. **復元ロジックも YAML 本文だけ**
   - 初期化時 `getDefaultSource()` は `window.location.hash` を展開して editor 初期値として使うだけで、config の復元処理がない。
3. **UI 文言/仕様も YAML 保存を明示**
   - Share ボタンの説明は `YAML is stored in URL hash`。
   - Playground spec にも permalink は YAML 保存として定義されている。

## 影響

- Permanent URL を共有しても、受信側で config editor は空のまま（またはローカル編集状態なし）になる。
- `SetConfig` ベースの lint/fix 結果が共有時に再現しない。
- 特に `enable-network` や rule override を使うケースで、同じ URL でも診断差分が発生する。

## 対処方法（優先度付き）

### P0: 仕様の明確化（即日対応） — **実装済み**

**目的**: 誤解による問い合わせをまず止める。
**実装** (詳細: `playground_p0_plan.md`):
- Share の tooltip / aria-label: `workflow YAML in URL hash (config not included)`
- About セクション・config トグル tooltip で同趣旨を明示
- `docs/usage.md` に Playground / Share 節を追加
- `Seiton_Playground_spec.md` §4.1 を更新

**メリット**: ランタイム挙動を変えず、文言のみで認知齟齬を解消。
**デメリット**: 根本的な共有再現性は改善しない（P1 で対応）。

### P1: URL 共有フォーマットの拡張（推奨） — **実装済み**

**目的**: URL だけで YAML + config の再現を可能にする。
**推奨方式**:
- hash payload を versioned JSON にする。
  - 例: `{ "v": 2, "yaml": "...", "config": "...", "filePath": "..." }`
- これを `deflate + base64url` で hash に保存。
- 復元時は:
  - `v2` payload なら YAML + config + filePath を復元
  - 旧形式（YAML文字列のみ）も後方互換で読み込む

**メリット**:
- 共有再現性が大幅に向上（config 差異を排除）。
- 旧 URL を壊さず移行可能。

**注意点**:
- URL 長の増加（特に YAML + config が大きい場合）。
- URL 長超過時のフォールバック（P2）とセットで設計推奨。

### P2: URL 長超過のフォールバック（中優先） — **実装済み**

**目的**: 大きな入力でも共有失敗を減らす。
**案**:
- hash 長が閾値超過時に toast で警告し、以下を案内:
  - config を省いた軽量 URL を生成
  - または「Copy YAML+Config to clipboard」経由で共有
  - 必要なら Gist 作成導線（将来的には opt-in）

**メリット**: 実運用での共有失敗を抑制。
**デメリット**: 完全な one-click 共有の単純性は下がる。

### P3: 共有の耐久性改善（低優先）

**目的**: 将来拡張時の破壊的変更を防ぐ。
**案**:
- decode 失敗時は安全に default にフォールバックしつつ、原因を toast 表示。
- 互換テスト（旧URL / 新URL / 壊れたURL）を UI test に追加。

## 実装時の受け入れ条件（提案）

1. Share 後 URL で再アクセスすると YAML と config が一致して復元されること。
2. 旧形式 URL（従来 hash）でも YAML 復元が継続すること。
3. 破損 hash でもページが壊れず default 表示に戻ること。
4. URL 長超過時の UX が明示されること（警告 toast など）。

## 推奨アクションプラン

1. **今すぐ**: P0 を先行してユーザー誤解を抑止。
2. **次の開発サイクル**: P1（versioned payload + 後方互換）を実装。
3. **同 PR または次PR**: P2/P3（長さ制限 UX + テスト強化）を追加。


# Playground P0 実装プラン（仕様の明確化）

## スコープ

Permanent URL / Share が **workflow YAML のみ** を URL hash に含み、**config editor の内容は含まない** ことを UI とユーザードキュメントで明示する。挙動（エンコード対象）の変更は行わない。

## 実装内容

| 領域 | 変更 |
|---|---|
| `main.js` | `permalinkShareTitle` を YAML-only + config 非含有の文言に更新 |
| `index.html` | Share ボタンの `title` / `aria-label` を同期 |
| `index.html` | About セクションに config 非共有を追記 |
| `index.html` | Config トグル `title` に Share 非含有を追記（トグル説明は維持） |
| `Seiton_Playground_spec.md` | §4.1 Permalink 行を仕様どおり明文化 |
| `docs/usage.md` | Playground / Share 節を新規追加 |
| `PlaygroundHtmlContractTests` | 上記文言の回帰テスト 4 件 |

## ユーザーファースト観点（レビュー）

- **Share ボタン**: 「何が共有されるか / 何がされないか」を 1 文で提示（`workflow YAML` + `config not included`）。従来の “stored in URL hash” だけより誤解が少ない。
- **Config パネル**: Share 操作の直前に触る UI のため、トグル tooltip で非含有を案内。トグル機能の説明は残した。
- **About**: 長文だが、リンク受信者の期待値調整に必要な情報を 2 文に集約。

## ベンチマーク

P0 は静的文言のみのため、lint / WASM ホットパスに変更なし。既存の `PlaygroundLintBenchmark` でリグレッション確認。

| 項目 | 実装前 | 実装後 | 判定 |
|---|---|---|---|
| `PlaygroundLintBenchmark` (Release, ShortRun) | コードパス不変のため事前ベースライン省略 | 下表（2026-06-03 計測） | **変化なし（対象外）** |

計測サマリ（`dotnet run -c Release -- --filter "*PlaygroundLint*"`）:

| Method | Size | Mean | Allocated |
|---|---|---|---|
| NoChange | Small | 100.9 ns | 0 B |
| PartialChange | Small | 742.5 µs | 136080 B |
| FullChange | Small | 249.2 µs | 51927 B |
| NoChange | Large | 118.4 ns | 0 B |
| PartialChange | Large | 3.56 ms | 383206 B |
| FullChange | Large | 1.25 ms | 170782 B |

**理由**: `PlaygroundLintRunner` / `LintInterop` / permalink の deflate 処理は未変更。ベンチマーク対象外。

**改善策（低下時）**: 該当なし。もし将来 P1 で hash payload 拡張する場合は、encode/decode の micro-benchmark を `Seiton.Benchmark` に追加する。

## テスト

```shell
dotnet test --project tests/Seiton.Playground.Tests --treenode-filter "/*/*/PlaygroundHtmlContractTests/*"
```

追加テスト:

- `IndexTemplate_PermalinkButton_StatesYamlOnlyConfigNotIncludedInShareUrl`
- `IndexTemplate_AboutPlayground_StatesConfigNotIncludedInShareUrl`
- `IndexTemplate_ConfigPanel_StatesConfigNotIncludedInShareUrl`
- `MainJs_PermalinkShareTitle_StatesYamlOnlyConfigNotIncluded`

## 仕様整合

- `Seiton_Playground_spec.md` §4.1 を更新済み（WHAT の明確化のみ）。
- `playground.md` の P0 は本 PR で完了扱い。

## レビュー指摘と対応

| 指摘 | 対応 |
|---|---|
| Config トグルから「Toggle」説明が消える | `Toggle lint configuration editor.` を残し、Share 非含有を追記 |
| `usage.md` に Playground 記載がなかった | 「Playground (browser)」節を追加 |
| 文言の二重管理（HTML と main.js） | 契約テストで両方を検証（既存パターンに合わせる） |

# Playground P1/P2 実装プラン

## スコープ

- **P1**: v2 共有ペイロード（YAML + config + filePath）を URL hash に格納し、読み込み時に復元。v1（YAML のみ・標準 base64）後方互換。
- **P2**: URL / hash 長超過時に YAML-only リンクへフォールバック、それでも長い場合はクリップボードへバンドルコピー。

## 実装内容

| 領域 | 変更 |
|---|---|
| `PlaygroundSharePayload.cs` | zlib + JSON v2 コーデック（Core、テスト・ベンチマーク用） |
| `share-payload.js` | ブラウザ用同一アルゴリズム |
| `main.js` | Share / 初期化復元 / P2 フォールバック |
| `index.html` | Share・About・config ツールチップ更新 |
| `PlaygroundSharePayloadTests` | ユニットテスト 8 件 |
| `PlaygroundShareRestoreUiTests` | Playwright: v2 復元・v1 互換 |
| `PlaygroundSharePayloadBenchmark` | Encode/Decode 性能 |
| 仕様・`docs/usage.md` | §4.9 / Playground 節 |

## ユーザーファースト API（レビュー）

| 操作 | 挙動 |
|---|---|
| Share（通常） | YAML + config + path を URL に含めてコピー |
| Share（長い） | トーストで YAML-only に切り替えたことを明示 |
| Share（極端に長い） | URL は更新せず、編集内容をクリップボードへ |
| リンクを開く | エディタ・config・path を自動復元（v1 は YAML のみ） |
| 壊れた hash | トースト + デフォルトサンプル（クラッシュしない） |

## ベンチマーク

`PlaygroundSharePayloadBenchmark`（Release, 2026-06-04）:

| Method | Mean | Allocated |
|---|---|---|
| Encode_Small | 8.2 µs | 2.38 KB |
| Encode_Large | 195.7 µs | 102.58 KB |
| Decode_Small | 12.6 µs | 4.68 KB |
| Decode_Large | 219.2 µs | 206.9 KB |

**性能判定**: 新規コードパスは Share クリック・初回 hash 復元時のみ。lint/WASM（`PlaygroundLintBenchmark`）は不変。
**理由**: 小ペイロードは ~10 µs 台でユーザー操作に対して無視できる。大ペイロードは large.yaml 級で ~200 µs — 依然インタラクティブ。

**低下時の改善策**: `Decode_Large` の Gen0/Allocated が高め（JSON + inflate バッファ）。改善するなら `ArrayPool` で deflate バッファ再利用、または deflate level 9→6。URL 超過時は encode を段階的に試す（現状どおり）ことで無駄な full encode を避けられる。

## テスト

```shell
dotnet test --project tests/Seiton.Playground.Tests --treenode-filter "/*/*/PlaygroundSharePayloadTests/*"
dotnet test --project tests/Seiton.Playground.Tests --treenode-filter "/*/*/PlaygroundShareRestoreUiTests/*"
dotnet test
```

## レビュー指摘と対応

| 指摘 | 対応 |
|---|---|
| C# と JS のドリフト | 同一 JSON 形状・短キー・zlib；Playwright が C# 生成 hash を JS で復元 |
| P0 文言と矛盾 | Share / About を v2 + フォールバック説明に更新 |
| config 復元タイミング | WASM `SetConfig` 準備後に `applyShareConfigAfterRuntimeReady` |
| 破損 hash | `TryDecode` false 時トースト（P3 の一部を先行実装） |

## コミット方針

1. `feat(playground): add v2 share payload codec (Core + share-payload.js)`
2. `feat(playground): restore YAML/config from Share URL; P2 length fallback`
3. `docs(playground): P1/P2 spec, usage, and plan`
