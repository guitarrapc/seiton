# CLI に `install --skills` 相当を実装するための設計メモ

このドキュメントは、https://github.com/microsoft/playwright-cli と https://github.com/microsoft/playwright-core にみるCLIでユーザーにskillを提供して、LLMが動かしやすいようにするための実装方針をまとめたものです。

## 結論

CLI で skills を配布したい場合は、**実行時に skill を動的生成するより、あらかじめ `SKILL.md` と関連 reference markdown をパッケージに同梱し、`install --skills` で所定ディレクトリへコピーする方式**がシンプルで実用的です。

Playwright の実装もこの方式です。

---

## 推奨アーキテクチャ

### 1. skill を静的ファイルとして持つ

まず CLI のソースツリーに、skill 本体を markdown で持ちます。

例:

```text
src/tools/cli-client/skill/
  SKILL.md
  references/
    request-mocking.md
    session-management.md
    tracing.md
```

`SKILL.md` には少なくとも以下を含めます。

- frontmatter
  - `name`
  - `description`
  - 必要なら `allowed-tools`
- quick start
- コマンド一覧
- 出力の読み方
- よくあるワークフロー
- 詳細 reference へのリンク

Playwright の `SKILL.md` もこの形です。

---

### 2. build / publish 時に skill ファイルを成果物へ含める

`install --skills` は **npm 配布後のパッケージ内ファイルをコピーできる必要**があります。
そのため、ビルド時に markdown を `lib/` や配布成果物へコピーします。

イメージ:

```text
src/tools/cli-client/skill/** -> lib/tools/cli-client/skill/**
```

つまり install 時は:

- GitHub からダウンロードする
- ネットワーク越しに生成する

のではなく、**ローカルにインストール済みのパッケージ内ファイルをコピーする**だけにします。

---

### 3. `install` サブコマンドに `--skills` オプションを持たせる

CLI の install 系サブコマンドに `--skills` オプションを追加します。

例:

```bash
mycli install --skills
mycli install --skills=agents
```

推奨動作:

- `--skills` → `.claude/skills/<skill-name>/` に展開
- `--skills=agents` → `.agents/skills/<skill-name>/` に展開

これにより、Claude 系・汎用 agent 系の両方に対応しやすくなります。

---

## 実装の基本方針

### 4. コピー元ディレクトリを固定する

skill の source は、実行ファイルの相対位置ではなく、**パッケージ内の確実なパス解決関数**で求めるのが安全です。

やること:

1. skill source directory を解決
2. cwd 基準で出力先を決める
3. ディレクトリごと再帰コピーする

疑似コード:

```ts
async function installSkills(target: 'claude' | 'agents' = 'claude') {
  const cwd = process.cwd();
  const skillSourceDir = resolvePackagePath('tools/cli-client/skill');
  const skillDestDir = path.join(cwd, `.${target}`, 'skills', 'mycli');

  await fs.promises.mkdir(path.dirname(skillDestDir), { recursive: true });
  await fs.promises.cp(skillSourceDir, skillDestDir, { recursive: true });

  console.log(`✅ Skills installed to \`${path.relative(cwd, skillDestDir)}\`.`);
}
```

重要なのは **`SKILL.md` 単体ではなく skill ディレクトリごとコピーする**ことです。
reference 文書も一緒に配るためです。

---

### 5. 出力先は workspace 基準にする

skills のインストール先は、**CLI を実行したカレントディレクトリ**基準にするのが自然です。

例:

```text
<workspace>/.claude/skills/mycli/SKILL.md
<workspace>/.claude/skills/mycli/references/...
```

この方式だと:

- プロジェクト単位で skill を閉じ込められる
- グローバル環境を汚しにくい
- リポジトリごとに skill バージョンを揃えやすい

---

### 6. 動的生成は避け、必要なら生成元を別に持つ

基本は静的 markdown コピーで十分です。
もしコマンド一覧や説明を更新したい場合は、次のどちらかにします。

#### A. `SKILL.md` を source of truth にする
- 手で更新する
- README や docs をそこから同期する

#### B. 別の source から `SKILL.md` を生成し、publish 前に確定させる
- コマンド定義 JSON / schema / help 情報
- テンプレート
- reference markdown

ただしこの場合も、**ユーザーの `install --skills` 実行時には生成しない**方がよいです。
install 時は「生成済み成果物をコピーするだけ」にしておくと安定します。

---

## `SKILL.md` に書くべき内容

### 7. frontmatter を入れる

最小例:

```yaml
---
name: mycli
description: Automate X and help agents perform Y.
allowed-tools: Bash(mycli:*) Bash(npx:*) Bash(npm:*)
---
```

最低限:

- `name`
- `description`

必要に応じて:

- `allowed-tools`
- `user_invocable`
- その他 agent 側仕様に沿う属性

---

### 8. エージェントが次の行動を決めやすい説明を書く

`SKILL.md` は人向け README というより、**エージェント向けの運用マニュアル**です。

必須に近い内容:

1. 何をする CLI か
2. 最短の quick start
3. 主コマンド一覧
4. 出力の読み方
5. 推奨ワークフロー
6. 失敗時の対処
7. reference への導線

良い例:

- 「まず `snapshot` を取る」
- 「ref を使って `click e15` する」
- 「`--raw` は結果値だけ返す」
- 「JSON が必要なら `--json` を使う」

---

### 9. 長い説明は `references/` に分割する

1ファイルに全部詰め込まず、複雑なタスクは個別 markdown に分けます。

例:

```text
skill/
  SKILL.md
  references/
    testing.md
    auth.md
    tracing.md
    request-mocking.md
```

`SKILL.md` 側では最後に一覧で参照させます。

```markdown
## Specific tasks

* **Running tests** [references/tests.md](references/tests.md)
* **Authentication flows** [references/auth.md](references/auth.md)
* **Tracing** [references/tracing.md](references/tracing.md)
```

これにより:

- 本体は短く保てる
- 具体タスクだけ詳細化できる
- エージェントが必要な節だけ参照しやすい

---

## CLI 側の UX

### 10. 成功時はインストール先を明示する

出力例:

```text
✅ Skills installed to `.claude/skills/mycli`.
```

ユーザーが確認したいのは主に:

- どこに入ったか
- 成功したか
- 次に何をすればよいか

必要なら次の一行も有効です。

```text
Use your coding agent in this workspace to pick up the installed skill.
```

---

### 11. source が見つからない場合は即失敗する

例えば build 漏れで skill ファイルが配布物に入っていない場合、曖昧に成功扱いしてはいけません。

```ts
if (!fs.existsSync(skillSourceDir)) {
  console.error('❌ Skills source directory not found:', skillSourceDir);
  process.exit(1);
}
```

これで packaging ミスを早く発見できます。

---

### 12. テストを書く

最低限テストすべき内容:

1. `install --skills` で `.claude/skills/<name>/SKILL.md` ができる
2. `install --skills=agents` で `.agents/skills/<name>/SKILL.md` ができる
3. `references/` もコピーされる
4. 成功メッセージが出る
5. source 不在時に失敗する

---

## おすすめディレクトリ構成

```text
packages/mycli/src/tools/cli-client/
  program.ts
  commands.ts
  skill/
    SKILL.md
    references/
      testing.md
      auth.md
      tracing.md
```

build 後:

```text
packages/mycli/lib/tools/cli-client/
  program.js
  commands.js
  skill/
    SKILL.md
    references/
      testing.md
      auth.md
      tracing.md
```

install 後:

```text
<workspace>/.claude/skills/mycli/
  SKILL.md
  references/
    testing.md
    auth.md
    tracing.md
```

---

## サンプル実装イメージ

```ts
import fs from 'fs';
import path from 'path';

async function installWorkspace(options: { skills?: string }) {
  const cwd = process.cwd();

  await fs.promises.mkdir(path.join(cwd, '.mycli'), { recursive: true });

  if (options.skills) {
    const target = options.skills === 'agents' ? 'agents' : 'claude';
    const skillSourceDir = resolvePackagePath('tools/cli-client/skill');
    const skillDestDir = path.join(cwd, `.${target}`, 'skills', 'mycli');

    if (!fs.existsSync(skillSourceDir))
      throw new Error(`Skills source directory not found: ${skillSourceDir}`);

    await fs.promises.cp(skillSourceDir, skillDestDir, { recursive: true });
    console.log(`✅ Skills installed to \`${path.relative(cwd, skillDestDir)}\`.`);
  }
}
```

---

## アンチパターン

### やらない方がいいこと

- install のたびに外部サイトから markdown を取りに行く
- install 時に help 出力から毎回 `SKILL.md` を生成する
- `SKILL.md` だけコピーして `references/` を落とす
- 出力先を固定絶対パスにする
- 成功したふりをして実際には何も置かない

---

## 実践的なベストプラクティス

- **skill は静的 markdown として管理する**
- **配布物に必ず含める**
- **`install --skills` はコピーだけにする**
- **workspace ローカルへ展開する**
- **`.claude/skills/<name>` と `.agents/skills/<name>` をサポートする**
- **reference 文書を含むディレクトリごとコピーする**
- **テストで `SKILL.md` と references の存在を検証する**
- **README と `SKILL.md` の同期手順をメンテナンスフローに組み込む**

---

## 一文で言うと

**CLI の skills 配布は、「生成」より「同梱済み skill ディレクトリのコピー」として実装するのがよい。**
