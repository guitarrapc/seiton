# setup-seiton `@v1` が pinning されない件の調査と対応計画

## 目的

`uses: guitarrapc/setup-seiton@v1` が seiton で pinning されない一方、pinact では `v1.0.1`（現在は `v1.0.2` に変わり得る）へ解決される差分を整理し、優先度付きの対応計画を定める。

## 事実確認

- `setup-seiton` の現状:
  - `v1` は **branch**（`origin/v1 -> 0f877ad...`）。
  - `v1.0.1`, `v1.0.2` は **tag**。
  - 直近タグのコミット時刻は当日（`v1.0.0`, `v1.0.1`, `v1.0.2` すべて同日）。
- pinact 側:
  - `processUnpinnedVersion()` で非 semver ref（branch など）を通常は `ErrCantPinned` にする。
  - ただし `--branch-to-tag` に一致した場合、`convertBranchToLatestTag()` で「最新 stable tag」を選び SHA 化する。
- seiton 側:
  - `UnpinnedUsesRule` は `@v1` を未 pin として検出（fix 対象になる）。
  - 実解決は `GitHubActionShaResolver.ResolveAsync()` が担当。
  - `fix.pinning.min-age-days` の既定値は `14`。
  - ref が `v1` のような version family と解釈できる場合、`SelectBestEligibleTagAsync()` で同系列タグ候補（`v1.*`）を列挙し、`min-age-days` 未満を除外する。
  - 今回はタグがすべて当日作成のため、既定 `14` 日で候補が空になり `(null, null)` を返し、fix が skip される。

## 差分の本質

1. **branch 解決戦略の差**
   - pinact: `--branch-to-tag` に明示一致させると branch をタグへ変換して pin 可能。
   - seiton: ref 文字列が version family と解釈可能ならタグ候補探索を行うが、branch 専用の opt-in 機構はない。
2. **既定クールダウンの差**
   - pinact: `--min-age` 既定 0（実質無効）。
   - seiton: `fix.pinning.min-age-days` 既定 14（有効）。
   - `setup-seiton` のように新規タグ直後は seiton が意図的に skip しやすい。
3. **今回の直接原因**
   - `@v1` 自体が branch であることよりも、`min-age-days: 14` で `v1.*` 候補が全除外された点が支配的。

## 優先度付き対応プラン

### P0（即時回避・運用）

- 利用側設定で `fix.pinning.min-age-days` を一時的に `0`（または 1 未満に相当する運用）へ変更する。
  - 目的: リリース直後タグでも pinning を成立させる。
  - 影響: 新鮮すぎるタグを拾うリスクは上がる。
- あるいは、対象行を当面 SHA 指定に変更して運用する（手動 pin）。

### P1（短期改善・UX）

- `unpinned-uses` の fix skip 理由を明示する診断改善:
  - 例: 「`v1` は解決候補があるが `min-age-days=14` により全候補が除外された」。
  - 期待効果: 「なぜ pin されないか」の可観測性を上げ、設定調整の判断を容易にする。

### P2（中期改善・pinact との整合）

- seiton に branch→tag 変換の明示的 opt-in（pinact の `--branch-to-tag` 相当）を導入する。
  - 例: `fix.pinning.branch-to-tag` に regex リストを追加。
  - 既定は無効（現状互換）にし、利用者が `^v[0-9]+$` 等を指定可能にする。
  - 変換時は stable 優先・候補なし時 prerelease 許容の方針を選択可能にする。

### P3（仕様明文化）

- `.github/docs` と `docs/configuration.md` に以下を明記:
  - `min-age-days` 既定 14 の意図と副作用（新タグ直後に pin skip）。
  - `@v1` のような ref が branch / tag で曖昧な場合の解決方針。
  - branch を pin 対象にしたい場合の推奨設定（P2 実装後はその設定を正式案内）。

## 推奨実行順

1. P0 を適用して現場 unblock。
2. P1 でデバッグ性を先に改善（実装コスト小・効果大）。
3. P2 を設計/実装して pinact 相当の運用移行を可能にする。
4. P3 で仕様とドキュメントを同期。

## 補足（今回入力例への適用）

- `uses: guitarrapc/setup-seiton@v1` + `seiton-version: v0.9.19` のケースは、`seiton-version` の値とは独立に `uses` ref 解決側で skip される。
- `@0f877ad... # v1.0.1` は既に SHA pin されており、今回の未 pin 問題の対象外。
