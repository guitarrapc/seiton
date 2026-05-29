# Seiton CLI `install --skills` 実装計画

copilotcli_skill_investigation.mdの設計を受けて、 seiton CLI (C# / NativeAOT) に適用する具体的な実装計画。

## reference

.github/docs/copilotcli_skill_investigation.md の内容を前提としています。

---

## 1. コマンド体系

### 1.1 コマンド構文

```bash
seiton install --skills [--target claude|copilot] [--output PATH] [--force]
```

### 1.2 フラグ定義

| Flag | Short | Type | Default | Description |
|---|---|---|---|---|
| `--skills` | | `bool` | `false` | Install agent skill files to the workspace. |
| `--target` | `-t` | `claude\|copilot` | `claude` | Target agent platform. `claude` → `.claude/skills/seiton/`, `copilot` → `.github/copilot-instructions.md` or `.github/instructions/`. |
| `--output` | `-o` | `string` | (platform default) | Override the output directory path. |
| `--force` | `-f` | `bool` | `false` | Overwrite existing skill files. |

### 1.3 動作仕様

1. `--skills` が指定されていない場合、ヘルプを表示して exit 0。
2. `--target` に応じた出力先ディレクトリを決定:
   - `claude` → `<cwd>/.claude/skills/seiton/`
   - `copilot` → `<cwd>/.github/instructions/seiton/`
3. 出力先に既存ファイルがある場合:
   - `--force` なし → エラー終了 (exit 3)
   - `--force` あり → 上書き
4. skill ファイルを出力先にコピー（ディレクトリごと再帰）。
5. 成功メッセージを stdout に出力。

### 1.4 出力例

```
✅ Skills installed to `.claude/skills/seiton`.

Files:
  .claude/skills/seiton/SKILL.md
  .claude/skills/seiton/references/rules.md
  .claude/skills/seiton/references/fix-mode.md
```

### 1.5 Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `2` | Invalid options (e.g. unknown `--target` value) |
| `3` | Fatal error (destination exists without `--force`, I/O failure) |

---

## 2. Skill コンテンツ設計

### 2.1 配布する skill ファイル構成

```text
skill/
  SKILL.md
  references/
    rules.md
    fix-mode.md
    configuration.md
```

### 2.2 `SKILL.md` の内容方針

```yaml
---
name: seiton
description: Lint and fix GitHub Actions workflow files and action metadata files using seiton CLI.
---
```

本文に含める内容:

1. seiton とは何か（1-2 文）
2. Quick start: `seiton`, `seiton --fix`, `seiton --fix --dry-run`
3. 主要コマンド一覧（check, fix, init, rules, validate-config）
4. 出力の読み方（text format の構造、exit codes）
5. 推奨ワークフロー（lint → fix --dry-run → fix）
6. エラー時の対処（config エラー、unknown option）
7. references/ への導線

### 2.3 References の役割分担

| File | Content |
|---|---|
| `rules.md` | 全ルール一覧と各ルールの意味・修正方法 |
| `fix-mode.md` | fix モードの詳細（`--dry-run`, `--check`, network flags） |
| `configuration.md` | `seiton.yaml` の設定項目と記述例 |

---

## 3. NativeAOT 対応: コンテンツ同梱方式

### 3.1 方式選定

seiton は NativeAOT (`PublishAot=true`) でビルドされるため、ファイルシステム上の相対パスに依存する方式は使えない。以下の 2 方式を検討する。

| 方式 | Pros | Cons |
|---|---|---|
| A. C# string literal (source generator) | AOT 安全、依存なし、既存パターン踏襲 | ファイルが大きいとソース管理が煩雑 |
| B. EmbeddedResource | ファイルを直接管理できる、バイナリ同梱 | Assembly.GetManifestResourceStream() は AOT 対応済み |

**推奨: B. EmbeddedResource 方式**

理由:
- skill markdown は数 KB 程度で、複数ファイルを管理する必要がある
- `Assembly.GetManifestResourceStream()` は .NET NativeAOT で対応済み
- markdown ファイルをそのまま編集・レビューできる
- init コマンドの string literal 方式は単一テンプレートには適するが、複数ファイル管理には不向き

### 3.2 ファイル配置

```text
src/Seiton/
  Skills/
    SKILL.md
    references/
      rules.md
      fix-mode.md
      configuration.md
```

### 3.3 .csproj 設定

```xml
<ItemGroup>
  <EmbeddedResource Include="Skills\**\*" LogicalName="Skills/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

### 3.4 リソース読み出しヘルパー

```csharp
internal static class SkillResources
{
    private static readonly Assembly ThisAssembly = typeof(SkillResources).Assembly;

    /// <summary>Get all embedded skill file entries (logical name → content).</summary>
    public static IEnumerable<(string RelativePath, string Content)> GetAllSkillFiles()
    {
        var prefix = "Skills/";
        foreach (var name in ThisAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            using var stream = ThisAssembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var relativePath = name[prefix.Length..];
            yield return (relativePath, reader.ReadToEnd());
        }
    }
}
```

---

## 4. 実装ファイル構成

### 4.1 新規作成ファイル

| File | Responsibility |
|---|---|
| `src/Seiton/Commands/InstallCommand.cs` | install コマンドのロジック |
| `src/Seiton/Skills/SKILL.md` | 配布用 skill 本体 |
| `src/Seiton/Skills/references/rules.md` | ルールリファレンス |
| `src/Seiton/Skills/references/fix-mode.md` | fix モードリファレンス |
| `src/Seiton/Skills/references/configuration.md` | 設定リファレンス |
| `tests/Seiton.Tests/Commands/InstallCommandTests.cs` | コマンドテスト |

### 4.2 変更ファイル

| File | Change |
|---|---|
| `src/Seiton/Program.cs` | `Install(...)` メソッド追加 |
| `src/Seiton/Seiton.csproj` | `EmbeddedResource` 追加 |
| `.github/docs/Seiton_CLI_spec.md` | §1 に `install` コマンド仕様追加 |
| `.github/docs/Seiton_CLI_csharp_spec.md` | C# 実装仕様追加 |

---

## 5. Program.cs への追加

```csharp
/// <summary>Install skill files for coding agents into the workspace.</summary>
/// <param name="skills">Install agent skill files.</param>
/// <param name="target">-t, Target agent platform: claude | copilot.</param>
/// <param name="output">-o, Override the output directory path.</param>
/// <param name="force">-f, Overwrite existing skill files.</param>
public void Install(bool skills = false, string target = "claude", string? output = null, bool force = false)
{
    var code = InstallCommand.Run(skills, target, output, force);
    if (code != 0) Environment.ExitCode = code;
}
```

---

## 6. InstallCommand.cs 実装概要

```csharp
internal static class InstallCommand
{
    public static int Run(bool skills, string target, string? output, bool force)
    {
        if (!skills)
        {
            Console.WriteLine("Usage: seiton install --skills [--target claude|copilot] [--force]");
            return ExitCode.Success;
        }

        // Validate target
        var destDir = ResolveDestination(target, output);
        if (destDir is null)
        {
            Console.Error.WriteLine($"unknown target: {target}. Use 'claude' or 'copilot'.");
            return ExitCode.InvalidOptions;
        }

        // Check existing
        if (Directory.Exists(destDir) && !force)
        {
            Console.Error.WriteLine($"skill directory already exists: {destDir}");
            Console.Error.WriteLine("use --force to overwrite");
            return ExitCode.FatalError;
        }

        // Write files
        var files = SkillResources.GetAllSkillFiles().ToList();
        foreach (var (relativePath, content) in files)
        {
            var filePath = Path.Combine(destDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content);
        }

        // Success output
        var relativeDestDir = Path.GetRelativePath(Directory.GetCurrentDirectory(), destDir);
        Console.WriteLine($"Skills installed to `{relativeDestDir}`.");
        Console.WriteLine();
        Console.WriteLine("Files:");
        foreach (var (relativePath, _) in files)
        {
            Console.WriteLine($"  {Path.Combine(relativeDestDir, relativePath.Replace('/', Path.DirectorySeparatorChar))}");
        }

        return ExitCode.Success;
    }

    private static string? ResolveDestination(string target, string? output)
    {
        if (output is not null)
            return Path.GetFullPath(output);

        var cwd = Directory.GetCurrentDirectory();
        return target switch
        {
            "claude" => Path.Combine(cwd, ".claude", "skills", "seiton"),
            "copilot" => Path.Combine(cwd, ".github", "instructions", "seiton"),
            _ => null,
        };
    }
}
```

---

## 7. テスト計画

### 7.1 テストケース

| # | Test | Expectation |
|---|---|---|
| 1 | `install --skills` (default target) | `.claude/skills/seiton/SKILL.md` が作成される |
| 2 | `install --skills --target copilot` | `.github/instructions/seiton/SKILL.md` が作成される |
| 3 | `install --skills` (既存あり, `--force` なし) | exit 3, エラーメッセージ |
| 4 | `install --skills --force` (既存あり) | 上書き成功, exit 0 |
| 5 | `install --skills --target unknown` | exit 2, エラーメッセージ |
| 6 | `install` (`--skills` なし) | ヘルプ表示, exit 0 |
| 7 | references/ 配下もコピーされること | 全 embedded resource が展開される |

### 7.2 テスト実装方針

- tmpdir に cwd を切り替えてコマンドを実行
- `File.Exists()` / `File.ReadAllText()` で検証
- EmbeddedResource の内容が正しく展開されることを確認

---

## 8. Skill コンテンツのメンテナンス方針

### 8.1 更新タイミング

- 新ルール追加時 → `references/rules.md` を更新
- CLI フラグ変更時 → `SKILL.md` のコマンド一覧を更新
- config スキーマ変更時 → `references/configuration.md` を更新

### 8.2 同期チェック

CI で以下を検証する（オプション）:

- `SKILL.md` に記載されたコマンドが実際の help と一致するか
- `references/rules.md` のルール一覧が `seiton rules --format json` の出力と一致するか

---

## 9. 将来の拡張

### 9.1 `--target` の追加候補

| Target | 出力先 | 用途 |
|---|---|---|
| `claude` | `.claude/skills/seiton/` | Claude Code / Claude Desktop |
| `copilot` | `.github/instructions/seiton/` | GitHub Copilot custom instructions |
| `cursor` | `.cursor/rules/seiton/` | Cursor rules |

### 9.2 `seiton install` の他のサブ機能（将来）

```bash
seiton install --skills          # skill files
seiton install --config          # seiton.yaml (= init の alias)
seiton install --ci              # CI workflow template
```

`install` をインストール系操作の umbrella コマンドとして位置づけ、将来 `--ci` などを追加できる余地を残す。

---

## 10. CLI Spec への追記内容 (概要)

`Seiton_CLI_spec.md` §1 に以下を追加:

```markdown
### 1.7 `seiton install`

Install agent skill files and other workspace assets.

\`\`\`
seiton install --skills [--target claude|copilot] [--output PATH] [--force]
\`\`\`

- `--skills`: Install agent skill files to the workspace.
- `--target`: Target agent platform (`claude` or `copilot`). Defaults to `claude`.
- `--output`: Override the output directory.
- `--force`: Overwrite existing files.

Exit codes:
- `0`: Success or help displayed.
- `2`: Invalid options.
- `3`: Fatal error (destination exists, I/O failure).
\`\`\`
```

---

## 11. 実装順序

1. `src/Seiton/Skills/SKILL.md` と references を作成
2. `.csproj` に `EmbeddedResource` を追加
3. `SkillResources.cs` ヘルパーを作成
4. `InstallCommand.cs` を作成
5. `Program.cs` に `Install(...)` メソッドを追加
6. テストを作成・実行（Red → Green）
7. CLI spec ドキュメントを更新
8. `dotnet build` + `dotnet test` で全体検証
