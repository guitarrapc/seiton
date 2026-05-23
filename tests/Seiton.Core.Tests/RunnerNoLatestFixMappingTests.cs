using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Tests for the runner-no-latest rule fix-mapping configuration feature.
/// Covers: config parsing/validation, detection extension, fix generation.
/// </summary>
public sealed class RunnerNoLatestFixMappingTests
{
    #region Phase 1: Config Parsing and Validation

    [Test]
    public async Task Config_FixMapping_ValidMapping_ParsesCorrectly()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest: ubuntu-24.04
              windows-latest: windows-2025
              macos-latest: macos-15
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
        var ruleConfig = result.Config!.Rules!["runner-no-latest"];
        await Assert.That(ruleConfig.FixMapping).IsNotNull();
        await Assert.That(ruleConfig.FixMapping!.Count).IsEqualTo(3);
        await Assert.That(ruleConfig.FixMapping["ubuntu-latest"]).IsEqualTo("ubuntu-24.04");
        await Assert.That(ruleConfig.FixMapping["windows-latest"]).IsEqualTo("windows-2025");
        await Assert.That(ruleConfig.FixMapping["macos-latest"]).IsEqualTo("macos-15");
    }

    [Test]
    public async Task Config_FixMapping_PartialMapping_ParsesCorrectly()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest: ubuntu-24.04
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
        var ruleConfig = result.Config!.Rules!["runner-no-latest"];
        await Assert.That(ruleConfig.FixMapping).IsNotNull();
        await Assert.That(ruleConfig.FixMapping!.Count).IsEqualTo(1);
        await Assert.That(ruleConfig.FixMapping["ubuntu-latest"]).IsEqualTo("ubuntu-24.04");
    }

    [Test]
    public async Task Config_FixMapping_NotSpecified_ReturnsNull()
    {
        var yaml = """
        rules:
          runner-no-latest:
            enabled: true
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.IsValid).IsTrue();
        var ruleConfig = result.Config!.Rules!["runner-no-latest"];
        await Assert.That(ruleConfig.FixMapping).IsNull();
    }

    [Test]
    public async Task Config_FixMapping_EmptyKey_ProducesError()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              "": ubuntu-24.04
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal) &&
            d.Message.Contains("key", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Config_FixMapping_WhitespaceOnlyKey_ProducesError()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              "  ": ubuntu-24.04
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Config_FixMapping_EmptyValue_ProducesError()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest: ""
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal) &&
            d.Message.Contains("value", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Config_FixMapping_WhitespaceOnlyValue_ProducesError()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest: "   "
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Config_FixMapping_NullValue_ProducesError()
    {
        var yaml = """
        rules:
          runner-no-latest:
            fix-mapping:
              ubuntu-latest:
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Config_FixMapping_NotAllowedOnOtherRule_ProducesError()
    {
        var yaml = """
        rules:
          runner-label:
            fix-mapping:
              ubuntu-latest: ubuntu-24.04
        """;

        var result = LintConfigLibrary.Validate(yaml, "seiton.yaml");

        await Assert.That(result.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("fix-mapping", StringComparison.Ordinal))).IsTrue();
    }

    #endregion

    #region Phase 2: Detection Extension

    [Test]
    public async Task Detection_BuiltInLabels_DetectedWithoutConfig()
    {
        var cases = new[]
        {
            new RuleCase("ng-ubuntu-latest", """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """, ["moving latest label"]),
            new RuleCase("ng-windows-latest", """
            on: push
            jobs:
              build:
                runs-on: windows-latest
                steps:
                  - run: echo ng
            """, ["moving latest label"]),
            new RuleCase("ng-macos-latest", """
            on: push
            jobs:
              build:
                runs-on: macos-latest
                steps:
                  - run: echo ng
            """, ["moving latest label"]),
        };

        await AssertRuleCases(new RunnerNoLatestRule(), "runner-no-latest", cases);
    }

    [Test]
    public async Task Detection_CustomLabel_InFixMapping_IsDetected()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: my-org-runner-latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["my-org-runner-latest"] = "my-org-runner-v2"
        });

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("my-org-runner-latest");
        result.Dispose();
    }

    [Test]
    public async Task Detection_CustomLabel_NotInFixMapping_NotDetected()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: my-org-runner-latest
            steps:
              - run: echo ok
        """;

        // No fix-mapping or empty fix-mapping for this label
        var result = LintWithConfig(yaml, config: null);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(0);
        result.Dispose();
    }

    [Test]
    public async Task Detection_CaseInsensitive_BuiltInLabel()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: Ubuntu-Latest
            steps:
              - run: echo ng
        """;

        var result = LintWithConfig(yaml, config: null);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        result.Dispose();
    }

    [Test]
    public async Task Detection_CaseInsensitive_FixMappingKey()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: My-Org-Runner-Latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["my-org-runner-latest"] = "my-org-runner-v2"
        });

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        result.Dispose();
    }

    [Test]
    public async Task Detection_SelfHosted_SkippedEvenWithFixMapping()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: [self-hosted, ubuntu-latest]
            steps:
              - run: echo ok
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        });

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(0);
        result.Dispose();
    }

    [Test]
    public async Task Detection_ExpressionLabel_Skipped()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ${{ matrix.runner }}
            steps:
              - run: echo ok
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        });

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(0);
        result.Dispose();
    }

    [Test]
    public async Task Detection_VersionPinnedLabel_NotDetected()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-24.04
            steps:
              - run: echo ok
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        });

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(0);
        result.Dispose();
    }

    #endregion

    #region Phase 3: Fix Generation

    [Test]
    public async Task Fix_WithMapping_GeneratesFixForMatchedLabel()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        }, fixEnabled: true);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Fix).IsNotNull();
        await Assert.That(diagnostics[0].Fix!.Value.Description).Contains("ubuntu-24.04");

        // Verify the fix edits replace the correct text
        var edit = diagnostics[0].Fix!.Value.Edits[0];
        var utf8Yaml = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var originalText = Encoding.UTF8.GetString(utf8Yaml.AsSpan(edit.Offset, edit.Length));
        await Assert.That(originalText).IsEqualTo("ubuntu-latest");
        await Assert.That(edit.NewText).IsEqualTo("ubuntu-24.04");
        result.Dispose();
    }

    [Test]
    public async Task Fix_WithoutMapping_NoFixGenerated()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ng
        """;

        // No fix-mapping
        var config = new LintConfig
        {
            Fix = new FixConfig { Enabled = true },
        };

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Fix).IsNull();
        result.Dispose();
    }

    [Test]
    public async Task Fix_PartialMapping_OnlyMappedLabelsGetFix()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo a
          deploy:
            runs-on: windows-latest
            steps:
              - run: echo b
        """;

        // Only map ubuntu-latest, not windows-latest
        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        }, fixEnabled: true);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics
            .Where(d => d.RuleId == "runner-no-latest")
            .OrderBy(d => d.Location.StartLine)
            .ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        // ubuntu-latest has fix
        await Assert.That(diagnostics[0].Fix).IsNotNull();
        await Assert.That(diagnostics[0].Fix!.Value.Edits[0].NewText).IsEqualTo("ubuntu-24.04");
        // windows-latest has no fix
        await Assert.That(diagnostics[1].Fix).IsNull();
        result.Dispose();
    }

    [Test]
    public async Task Fix_CustomLabel_GeneratesFixWhenMapped()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: my-org-runner-latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["my-org-runner-latest"] = "my-org-runner-v2"
        }, fixEnabled: true);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Fix).IsNotNull();
        await Assert.That(diagnostics[0].Fix!.Value.Edits[0].NewText).IsEqualTo("my-org-runner-v2");
        result.Dispose();
    }

    [Test]
    public async Task Fix_FixDisabled_NoFixEvenWithMapping()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        }, fixEnabled: false);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Fix).IsNull();
        result.Dispose();
    }

    [Test]
    public async Task Fix_CaseInsensitive_MatchAndFix()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: Ubuntu-Latest
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04"
        }, fixEnabled: true);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Fix).IsNotNull();
        await Assert.That(diagnostics[0].Fix!.Value.Edits[0].NewText).IsEqualTo("ubuntu-24.04");
        result.Dispose();
    }

    [Test]
    public async Task Fix_MultipleLabelsInArray_EachGetsFix()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: [ubuntu-latest, windows-latest]
            steps:
              - run: echo ng
        """;

        var config = BuildConfigWithFixMapping(new Dictionary<string, string>
        {
            ["ubuntu-latest"] = "ubuntu-24.04",
            ["windows-latest"] = "windows-2025"
        }, fixEnabled: true);

        var result = LintWithConfig(yaml, config);
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "runner-no-latest").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.All(d => d.Fix is not null)).IsTrue();
        result.Dispose();
    }

    #endregion

    #region Helpers

    private static LintConfig BuildConfigWithFixMapping(
        Dictionary<string, string> fixMapping,
        bool fixEnabled = false)
    {
        // Use OrdinalIgnoreCase to match the behavior of ParseFixMapping in production
        var caseInsensitiveMapping = new Dictionary<string, string>(fixMapping, StringComparer.OrdinalIgnoreCase);
        var ruleConfig = new RuleConfig
        {
            FixMapping = caseInsensitiveMapping,
        };

        return new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["runner-no-latest"] = ruleConfig,
            },
            Fix = new FixConfig { Enabled = fixEnabled },
        };
    }

    private static LintResult LintWithConfig(string yaml, LintConfig? config)
    {
        var normalized = NormalizeYaml(yaml);
        var utf8 = Encoding.UTF8.GetBytes(normalized);
        var engine = new LintEngine([new RunnerNoLatestRule()]);
        return engine.Check(utf8, "test.yml", config);
    }

    private static async Task AssertRuleCases(IRule rule, string ruleId, RuleCase[] cases, LintConfig? config = null)
    {
        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = NormalizeYaml(c.Yaml);
            using var result = config is null
                ? new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml")
                : new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), $"rule-case-{c.Name}.yml", config);
            var diagnostics = result.Diagnostics.Where(x => x.RuleId == ruleId).ToArray();

            if (c.ExpectedSubstrings.Length == 0)
            {
                await Assert.That(diagnostics).IsEmpty();
                continue;
            }

            for (var j = 0; j < c.ExpectedSubstrings.Length; j++)
            {
                var expected = c.ExpectedSubstrings[j];
                var found = diagnostics.Any(x => x.Message.Contains(expected, StringComparison.Ordinal));
                if (!found)
                {
                    var observed = diagnostics.Length == 0
                        ? "<no rule diagnostics>"
                        : string.Join(" | ", diagnostics.Select(static x => x.Message));
                    throw new InvalidOperationException($"rule={ruleId} case={c.Name} expected={expected} observed={observed}");
                }
            }
        }
    }

    private static string NormalizeYaml(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
            start++;

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
            end--;

        if (start > end)
            return string.Empty;

        var minIndent = int.MaxValue;
        for (var i = start; i <= end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            var indent = lines[i].Length - lines[i].TrimStart().Length;
            if (indent < minIndent)
                minIndent = indent;
        }

        if (minIndent == int.MaxValue)
            minIndent = 0;

        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append('\n');
            }
            else
            {
                sb.Append(line.AsSpan(Math.Min(minIndent, line.Length)));
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private sealed record RuleCase(string Name, string Yaml, string[] ExpectedSubstrings);

    #endregion
}
