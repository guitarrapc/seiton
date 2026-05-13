using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

/// <summary>
/// Integration tests for local reusable workflow output resolution via ExprUndefinedVarRule.
/// Validates that the resolver's guards (prefix, extension, size cap, path traversal)
/// correctly determine whether needs.&lt;job&gt;.outputs.* is treated as strict or loose.
/// </summary>
public sealed class LocalReusableWorkflowOutputResolutionTests
{
    [Test]
    public async Task ResolveOutputNames_DotDotSlashPrefix_NotResolved()
    {
        // ../path references are not valid for GitHub Actions local reusable workflows.
        // Only ./ prefix is valid. The resolver must reject ../ so outputs are treated as loose.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-dotdot-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var parentDir = Path.Combine(rootDir, ".github");
        var reusablePath = Path.Combine(parentDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow declares only "version" output
            var reusableYaml = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Caller uses ../ prefix and references a NON-EXISTENT output "typo_output".
            // If ../ is resolved → strict → "typo_output" flagged as undefined (test fails).
            // If ../ is rejected → loose → no error (test passes).
            var callerYaml = """
            on: push
            jobs:
              new-version:
                uses: ../reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, reusableYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // ../ must be rejected → loose → no "is not defined" error
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    [Arguments(".yamlsss")]
    [Arguments(".ymla")]
    [Arguments(".ymlx")]
    [Arguments(".yamll")]
    [Arguments(".YAMLS")]
    public async Task ResolveOutputNames_FalseYamlExtension_NotResolved(string fakeExtension)
    {
        // Extensions like .yamlsss, .ymla must NOT be treated as YAML
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-fakeext-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable" + fakeExtension);
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var reusableContent = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Reference non-existent output via fake extension file
            var callerYaml = "on: push\njobs:\n  new-version:\n    uses: ./.github/workflows/reusable" + fakeExtension + "\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [new-version]\n    steps:\n      - env:\n          TAG: ${{ needs.new-version.outputs.typo_output }}\n        run: echo \"$TAG\"\n";

            File.WriteAllText(reusablePath, reusableContent, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // Fake extension must be rejected → loose → no "is not defined" error
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResolveOutputNames_NonYamlExtension_NotResolved()
    {
        // Files without .yml/.yaml extension should not be read.
        // If the guard is missing, the resolver reads and parses .txt → strict → error.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-ext-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.txt");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Valid YAML content but wrong extension — declares only "version"
            var reusableContent = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Caller references non-existent output "typo_output" via .txt file.
            // If .txt is resolved → strict → "typo_output" flagged (test fails).
            // If .txt is rejected → loose → no error (test passes).
            var callerYaml = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.txt
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, reusableContent, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // .txt must be rejected → loose → no "is not defined" error
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResolveOutputNames_ValidDotSlashYmlPath_ResolvesOutputsStrictly()
    {
        // Confirm ./ prefix with .yml extension resolves strictly (flags unknown outputs)
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-valid-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var reusableYaml = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Valid: ./ prefix + .yml extension + references NON-EXISTENT output "typo_output"
            // → resolver succeeds → strict → "typo_output is not defined"
            var callerYaml = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, reusableYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // Should resolve strictly — "typo_output" IS flagged
            await Assert.That(msgs.Any(m => m.Contains("\"typo_output\" is not defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResolveOutputNames_OversizedFile_NotResolved()
    {
        // Files larger than the size cap should not be parsed
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-size-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Create a file that exceeds a reasonable size cap (> 2 MB)
            var builder = new StringBuilder();
            builder.AppendLine("on:");
            builder.AppendLine("  workflow_call:");
            builder.AppendLine("    outputs:");
            builder.AppendLine("      version:");
            builder.AppendLine("        description: The computed version");
            builder.AppendLine("        value: ${{ jobs.compute.outputs.ver }}");
            builder.AppendLine("jobs:");
            builder.AppendLine("  compute:");
            builder.AppendLine("    runs-on: ubuntu-latest");
            builder.AppendLine("    steps:");
            // Pad the file to exceed 2 MB
            while (builder.Length < 2 * 1024 * 1024 + 1)
            {
                builder.AppendLine("      - run: echo \"padding to make file large\"");
            }

            // Caller references non-existent output "typo_output" via oversized file.
            // If size cap is missing → file parsed → strict → error (test fails).
            // If size cap works → not resolved → loose → no error (test passes).
            var callerYaml = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, builder.ToString(), Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // Oversized → not resolved → loose → no "is not defined" error
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResolveOutputNames_PathTraversal_NotResolved()
    {
        // A crafted uses path with ../ segments that escapes the repository root must be rejected.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-traversal-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Place a valid workflow file OUTSIDE the repository root (in a sibling directory)
        var outsideDir = rootDir + "-outside";
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "evil.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var outsideYaml = """
            on:
              workflow_call:
                outputs:
                  secret:
                    description: Should not be resolvable
                    value: ${{ jobs.x.outputs.v }}
            jobs:
              x:
                runs-on: ubuntu-latest
                outputs:
                  v: ${{ steps.s.outputs.v }}
                steps:
                  - id: s
                    run: echo "v=leaked" >> "$GITHUB_OUTPUT"
            """;

            // Path traversal: escapes repo root via enough ../ segments
            // ./.github/workflows/../../../<rootDir>-outside/evil.yml
            // After Path.GetFullPath this lands outside the repository root
            var callerYaml = "on: push\njobs:\n  escaped:\n    uses: ./.github/workflows/../../../" + Path.GetFileName(outsideDir) + "/evil.yml\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [escaped]\n    steps:\n      - env:\n          TAG: ${{ needs.escaped.outputs.typo }}\n        run: echo \"$TAG\"\n";

            File.WriteAllText(outsidePath, outsideYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            using var _ = result.ParseResult.Arena;

            await Assert.That(result.ParseResult.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            // Path traversal must be rejected → loose → no "is not defined" error
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
            if (Directory.Exists(outsideDir))
            {
                Directory.Delete(outsideDir, recursive: true);
            }
        }
    }
}
