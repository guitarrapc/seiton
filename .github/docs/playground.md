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

### P0: 仕様の明確化（即日対応）

**目的**: 誤解による問い合わせをまず止める。  
**対応**:
- Share の tooltip / aria-label を「YAML only」に明示（既に近いが、config 非対応をさらに明確化）。
- `docs/usage.md` や playground 説明文に「config は URL 共有対象外」を追記。

**メリット**: 実装変更なし、最短で認知齟齬を解消。  
**デメリット**: 根本的な共有再現性は改善しない。

### P1: URL 共有フォーマットの拡張（推奨）

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

### P2: URL 長超過のフォールバック（中優先）

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
- payload に `v` を必須化し、decoder をバージョン分岐。
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
