# Seiton Playground (GitHub Pages) — 実装計画

本書は [Seiton_ghpages_spec.md](Seiton_ghpages_spec.md) に基づく **実施順・検証項目・リスク** を整理したもの。ディレクトリ構成、コード例、プロジェクトプロパティの一次情報は同仕様に従う。

## ゴールと非ゴール

- **ゴール**: `wasm-experimental` 系 WebAssembly Browser App で `Seiton.Core` の lint をブラウザ内実行し、静的ホスティング（GitHub Pages artifact）で公開する。
- **非ゴール**（この計画フェーズでは扱わない）: CLI の変更、Parser/Lint 本体のパフォーマンス改善（Playground 追加による Seiton.Core の変更は最小限に留める）。

## Seiton.Core との境界（Lint パフォーマンス指針）

- **LintEngine の単一再利用**: `LintInterop` で `LintEngine` を static シングルトンとして持つ設計は、ルールセット初期化コストの償却として妥当（仕様 4.1 と整合）。
- **Playground が触るコード**: WASM バインド層での `Encoding.UTF8.GetBytes`、`JsonSerializer`、診断用のリスト構築は **ブラウザ向けホットパスであり Seiton.Core の Parser/Lint のホットパスではない**。`Seiton.Core` 内を変更しない限り、[performance-requirements](../../.claude/skills/performance-requirements/SKILL.md) の「Parser/Lint のゼロアロケーション」規則への影響はない。
- **将来**: シリアライズを Source Generator 化する判断は仕様 7.1 の通り trimming 互換性の都合で検討（必須ではない）。

## 前提

- .NET 10、`Microsoft.NET.Sdk.WebAssembly`、`wasm-tools` workload（仕様 3.3）。
- リポジトリは `seiton.slnx` を使用 — 新規プロジェクト追加時はソリューションへ登録する。

## フェーズ 0 — 仕様との差分確認（着手前）

| 確認項目 | 内容 |
|----------|------|
| ターゲットフレームワーク | 仕様は `net10.0`。リポジトリの他プロジェクトと一致すること。 |
| VYaml | 仕様 2.3 — WASM での動作は **実ビルド・手動スモーク** で検証する（フェーズ 3）。 |
| オンラインルール | `LintEngine.Check()` は同期パスでネットワークを使わない。オンライン専用診断は `OnlineAuditEngine.AuditAsync` 側。Playground は `Check()` のみとすれば **追加の「ネットワーク無効化」は不要**（仕様の「オフライン lint」と整合）。UI 上「一部ルールは CLI の online モードでのみ」など必要なら後からドキュメント化。 |
| CodeMirror 5 vs 6 | 仕様 8.1 は「5 or 6」。実装時に一方を選び、取得方法（CDN / `wwwroot/lib` 手置き）を固定する。 |

## フェーズ 1 — プロジェクト骨格

1. `src/Seiton.Playground/` を新規作成。
   - `Seiton.Playground.csproj`: 仕様 3.2 の `Sdk`・`PropertyGroup`・`Seiton.Core` 参照を反映。
   - `Program.cs` / `LintInterop.cs` のプレースホルダ（中身はフェーズ 2 で本実装）。
   - `wwwroot/` の空レイアウト（`index.html`, `main.js`, `style.css` は最小でも可）。
2. `seiton.slnx` にプロジェクトを追加。
3. `dotnet build` が通ること（WASM ターゲットはフェーズ 3 で検証）。

**完了条件**: ローカルで `dotnet build src/Seiton.Playground` 成功。

## フェーズ 2 — C# ↔ JS ブリッジ（LintInterop）

1. 仕様 4.1 に沿い `[JSExport] RunLint(string yamlSource, string filePath)` を実装。
2. `LintEngine` を static で保持し、`Check(utf8Yaml, filePath)` を呼ぶ。
3. 診断を JSON 配列へシリアライズ（匿名型または DTO、`JsonSerializerOptions` は必要最小限）。
4. 仕様にある `dotnet.js` の import パス・`getAssemblyExports`・命名空間 (`exports.Seiton.Playground.LintInterop.RunLint`) は **実際の publish 出力に合わせて調整**（テンプレート差異の吸収）。

**テスト**: `src/` を変更したため、[test-first-development](../../.claude/skills/test-first-development/SKILL.md) に従い、可能なら `Seiton.Playground` 用に **インテグレーション相当のテスト**（例: 通常のテストプロジェクトから `LintInterop` と同等の処理を共有メソッド化して検証）を検討。単体テストを置きにくい場合は、`dotnet publish` + ブラウザ手動確認をフェーズ 3 のゲートとして明文化する。

**完了条件**: `dotnet run --project src/Seiton.Playground` で開発サーバーが起動し、`RunLint` が JS から呼べること（コンソール・簡易 UI で可）。

## フェーズ 3 — フロント（wwwroot）と開発体験

1. 仕様 4.3〜4.4 の UI: CodeMirror、debounce、`renderResults`、`getDefaultSource`、`loading` の非表示タイミング。
2. `.github/workflows/test.yml` / `action.yml` の切替（`filetype-select`）と `filePath` 連携。
3. オプション（Post-MVP 扱いでよいが仕様にコードあり）: Permalink（Pako + hash）、`preload` リンクは実際の `_framework` ファイル名に合わせる。

**完了条件**:

- デフォルト YAML で lint 結果がテーブル・ガターに出る。
- パースエラー時も致命で落ちず、ユーザー向けエラー表示に繋げる（try/catch 仕様 4.3）。

## フェーズ 4 — リリース publish（trim / AOT / サイズ）

1. `dotnet publish src/Seiton.Playground -c Release`（出力パスは仕様 5.2）。
2. `.nojekyll` を AppBundle ルートに配置する手順を README または CI と一致させる。
3. Trimming で落ちる場合:
   - `JsonSerializer`: 匿名型問題ならソースジェネレータまたは明示 DTO に切替（仕様 7.1）。
   - `Rd.xml` / 属性だけで足りない箇所は最小限の `DynamicDependency` を検討。
4. ブラウザで `AppBundle/index.html` を開き smoke（または `dotnet run` の WasmDevServer との差異を確認）。

**完了条件**: Publish 済み静的ファイル一式でゲストブラウザから主要フローが動作。転送サイズは仕様 5.3 の目安を記録（任意で Brotli 後の数値を残す）。

## フェーズ 5 — GitHub Actions & Pages

1. 仕様 6.2 の `playground.yml`（または同等名前）を `.github/workflows/` に追加。
2. `actions/setup-dotnet` のバージョン、`dotnet-version: '10.0.x'`、ブランチ名 `main` をリポジトリ実態に合わせる。
3. `paths` フィルター（`Seiton.Playground` / `Seiton.Core`）を維持。
4. リポジトリ Settings → Pages で **GitHub Actions** ソースを有効化（仕様 6.3）。

**完了条件**: `main` への push（または手動 dispatch）で artifact がデプロイされ、公開 URL が仕様どおり参照できる。

## フェーズ 6 — Post-MVP（優先度は任意）

仕様 8.2 の順に、必要に応じてバックログ化:

- Permalink（完全版）
- GitHub / Gist URL 取り込み（CORS 制約のドキュメント付き）
- ダークモード、`prefers-color-scheme`
- severity フィルター

## 検証チェックリスト（リリース前）

- [ ] `dotnet build` / `dotnet publish`（Release）成功
- [ ] `.nojekyll` がデプロイ物に含まれる
- [ ] `Seiton.Core` の変更がない、またはフル `dotnet test` 合格
- [ ] Seiton.Core を変更した場合は該当ベンチマーク差分確認（test-first skill の表に従う）
- [ ] 仕様 [Seiton_ghpages_spec.md](Seiton_ghpages_spec.md) §10 Lessons Learned に、実装で得た知見を追記

## リスクとフォロー

| リスク | 対応 |
|--------|------|
| VYaml が WASM で予期せぬ例外 | フェーズ 3 で最小 YAML から段階的に試し、仕様 §10 に記録 |
| Trim 過剰で実行時失敗 | フェーズ 4 の DTO / Rd.xml / 属性の段階的緩和 |
| `_framework` パス・プリロード名のバージョン差 | MS ドキュメントと実際の `AppBundle` を突き合わせ、HTML を更新 |
| ビルド時間（AOT）が CI を圧迫 | キャッシュ戦略や `workflow_dispatch` のみ AOT 等は将来検討 |

## 参照

- [Seiton_ghpages_spec.md](Seiton_ghpages_spec.md)
- [performance-requirements](../../.claude/skills/performance-requirements/SKILL.md)（Seiton.Core 変更時）
- [test-first-development](../../.claude/skills/test-first-development/SKILL.md)（`src/` 変更時）
