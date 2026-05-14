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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

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

    [Test]
    public async Task ResolveOutputNames_CaseDifferentPath_NotResolvedOnCaseSensitiveFs()
    {
        // On case-sensitive filesystems (Linux), a sibling directory whose name differs only by case
        // must NOT be treated as "under the base directory". The old StartsWith(OrdinalIgnoreCase) guard
        // would incorrectly allow this; Path.GetRelativePath correctly rejects it.
        // On case-insensitive FS (Windows, default macOS), the directories cannot differ by case alone,
        // so this test validates the general containment logic instead.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-case-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Create a sibling directory with UPPER-case name
        var siblingName = Path.GetFileName(rootDir).ToUpperInvariant();
        var siblingDir = Path.Combine(Path.GetDirectoryName(rootDir)!, siblingName);
        var siblingWorkflowsDir = Path.Combine(siblingDir, ".github", "workflows");
        Directory.CreateDirectory(siblingWorkflowsDir);

        // Detect case-sensitivity at runtime by probing the actual filesystem.
        // macOS default APFS is case-insensitive, so we cannot rely on OS checks alone.
        var isCaseSensitiveFs = IsCaseSensitiveFileSystem(workflowsDir);

        var callerPath = Path.Combine(workflowsDir, "caller.yml");
        var evilPath = Path.Combine(siblingWorkflowsDir, "evil.yml");

        try
        {
            // Place a valid reusable workflow in the sibling (case-different) directory
            var evilYaml = """
            on:
              workflow_call:
                outputs:
                  secret:
                    description: Should not be resolvable on case-sensitive FS
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

            File.WriteAllText(evilPath, evilYaml, Encoding.UTF8);

            // Construct a uses path that traverses via ../ into the case-different sibling
            // ./.github/workflows/../../../<SIBLING>/.github/workflows/evil.yml
            var usesPath = "./.github/workflows/../../../" + siblingName + "/.github/workflows/evil.yml";

            var callerYaml = "on: push\njobs:\n  escaped:\n    uses: " + usesPath + "\n  deploy:\n    runs-on: ubuntu-latest\n    needs: [escaped]\n    steps:\n      - env:\n          TAG: ${{ needs.escaped.outputs.typo }}\n        run: echo \"$TAG\"\n";
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();

            if (!isCaseSensitiveFs)
            {
                // On case-insensitive FS (Windows, default macOS), the sibling IS the same directory.
                // Path.GetRelativePath correctly treats them as same → resolution succeeds.
                // Actual case-bypass attack is impossible on case-insensitive FS.
                // Resolution succeeds, so "typo" is checked against the resolved outputs ("v")
                // and reported as not defined.
                await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsTrue();
            }
            else
            {
                // On case-sensitive FS (Linux, case-sensitive macOS), the sibling is a DIFFERENT directory.
                // Path.GetRelativePath returns "../<SIBLING>/..." which starts with ".."
                // → guard rejects → loose typing → no "is not defined" error.
                await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
            }
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
            // On case-sensitive FS, sibling is a different directory that needs separate cleanup
            if (!string.Equals(rootDir, siblingDir, StringComparison.Ordinal) && Directory.Exists(siblingDir))
            {
                Directory.Delete(siblingDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task ResolveOutputNames_DotDotPrefixedDirectoryName_NotFalsePositiveRejection()
    {
        // A directory named "..special" (starts with ".." but is NOT path traversal) must NOT be
        // rejected by the traversal guard. When Path.GetRelativePath returns "..special/..." it
        // starts with ".." but is a valid child, not a parent traversal.
        // This scenario triggers when the uses path does NOT start with ./.github/ — the
        // baseDirectory falls back to the workflow directory, and a sibling named "..special" is valid.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-dotdotdir-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Create a subdirectory named "..special" directly under the workflows dir (the base).
        // When uses = "./..special/reusable.yml" (no .github prefix), base = workflowsDir,
        // and relative path from base = "..special/reusable.yml" which starts with "..".
        var specialDir = Path.Combine(workflowsDir, "..special");
        Directory.CreateDirectory(specialDir);

        var reusablePath = Path.Combine(specialDir, "reusable.yml");
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

            // Reference via a path that does NOT start with "./.github/" so the base is
            // the workflow directory. The resolver should see "..special/reusable.yml" as a
            // valid child, not as parent traversal.
            var callerYaml = """
            on: push
            jobs:
              get-ver:
                uses: ./..special/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [get-ver]
                steps:
                  - env:
                      TAG: ${{ needs.get-ver.outputs.nonexistent }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, reusableYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

            // If resolution succeeds, "nonexistent" should be flagged as undefined
            // because the reusable workflow only declares "version".
            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsTrue();
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
    public async Task ResolveOutputNames_ReusableCallJobWithLocalOutputs_UsesCalledWorkflowOutputs()
    {
        // When a reusable-workflow call job invalidly also declares outputs:,
        // BuildJobOutputsType should prioritize the called workflow's outputs (via WorkflowCall)
        // over the local outputs block.
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-callwithoutputs-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow declares output "version" only
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

            // Caller has a reusable-workflow call job that also (invalidly) declares outputs:
            // with "local_out". If we resolve from the local outputs, "version" would be
            // flagged as undefined. If we correctly resolve from the called workflow,
            // "version" is valid and "local_out" would be unknown.
            var callerYaml = """
            on: push
            jobs:
              get-ver:
                uses: ./.github/workflows/reusable.yml
                outputs:
                  local_out: some_value
              deploy:
                runs-on: ubuntu-latest
                needs: [get-ver]
                steps:
                  - env:
                      VER: ${{ needs.get-ver.outputs.version }}
                      BAD: ${{ needs.get-ver.outputs.nonexistent }}
                    run: echo "$VER $BAD"
            """;

            File.WriteAllText(reusablePath, reusableYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();

            // "version" comes from the called workflow — must NOT be flagged
            await Assert.That(msgs.Any(m => m.Contains("\"version\" is not defined", StringComparison.Ordinal))).IsFalse();
            // "nonexistent" is not in the called workflow — must be flagged
            await Assert.That(msgs.Any(m => m.Contains("\"nonexistent\" is not defined", StringComparison.Ordinal))).IsTrue();
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
    public async Task ResolveOutputNames_EquivalentPathsWithDotSegments_ResolveCorrectly()
    {
        // Two semantically equivalent uses paths that differ in raw form should resolve correctly.
        // e.g., "./.github/workflows/reusable.yml" and "./.github/workflows/./reusable.yml"
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-resolver-cachepath-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
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

            // Two jobs referencing the same reusable workflow via different path forms.
            // Both should resolve and flag "nonexistent" as undefined.
            var callerYaml = """
            on: push
            jobs:
              job1:
                uses: ./.github/workflows/reusable.yml
              job2:
                uses: ./.github/workflows/./reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [job1, job2]
                steps:
                  - env:
                      V1: ${{ needs.job1.outputs.nonexistent }}
                      V2: ${{ needs.job2.outputs.nonexistent }}
                    run: echo "$V1 $V2"
            """;

            File.WriteAllText(reusablePath, reusableYaml, Encoding.UTF8);
            File.WriteAllText(callerPath, callerYaml, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.HasFatalError).IsFalse();

            var msgs = result.Diagnostics
                .Where(x => x.RuleId == "expr-undefined-var")
                .Select(x => x.Message)
                .ToArray();

            // Both jobs should resolve strictly — "nonexistent" flagged for both
            var undefinedCount = msgs.Count(m => m.Contains("\"nonexistent\" is not defined", StringComparison.Ordinal));
            await Assert.That(undefinedCount).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Probes whether the filesystem at the given directory is case-sensitive.
    /// Creates a temporary file and checks if an alternate-case path resolves to it.
    /// </summary>
    private static bool IsCaseSensitiveFileSystem(string directory)
    {
        var probeLower = Path.Combine(directory, ".caseprobe");
        var probeUpper = Path.Combine(directory, ".CASEPROBE");
        try
        {
            File.WriteAllText(probeLower, "probe");
            // If the upper-case path also exists, the FS is case-insensitive
            return !File.Exists(probeUpper);
        }
        finally
        {
            try { File.Delete(probeLower); } catch { /* best effort */ }
            // On case-sensitive FS, probeUpper is a different (nonexistent) file — no cleanup needed.
            // On case-insensitive FS, probeLower == probeUpper — already deleted above.
        }
    }
}
