using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Integration test runner that compares seiton lint output against actionlint
/// <c>.out</c> expectation files from <c>testdata/examples/</c> fixtures.
/// These are the documentation examples that actionlint uses to showcase its detection capabilities.
/// </summary>
public sealed class ActionlintExamplesCompatTests
{
    // Seiton rule ID → actionlint rule ID mapping.
    // Parser diagnostics (RuleId = null) are mapped via the "parse" key.
    private static readonly Dictionary<string, string> RuleIdMap = new(StringComparer.Ordinal)
    {
        ["parse"] = "syntax-check",
        ["job-structure"] = "syntax-check",
        ["shell-name"] = "syntax-check",
        ["env-var"] = "syntax-check",
        ["permissions"] = "permissions",
        ["template-injection"] = "expression",
        ["expr-undefined-var"] = "expression",
        ["runner-label"] = "runner-label",
        ["schedule-event"] = "schedule-event",
        ["needs-graph"] = "job-needs",
        ["id-naming"] = "id",
        ["glob-pattern"] = "glob",
        ["credentials"] = "credentials",
        ["deprecated-commands"] = "deprecated-commands",
        ["if-cond"] = "if-cond",
        ["matrix"] = "matrix",
        ["reusable-workflow"] = "workflow-call",
        ["dispatch-inputs"] = "events",
        ["workflow-call-input-default"] = "workflow-call-input-default",
        ["popular-action-inputs"] = "action",
        ["local-action-inputs"] = "action",
        ["outdated-action-runner"] = "action",
        ["unpinned-uses"] = "action",
    };

    // Seiton-only rule IDs that have no actionlint equivalent and should be excluded.
    private static readonly HashSet<string> SeitonOnlyRules = new(StringComparer.Ordinal)
    {
        "unpinned-image",
        "dangerous-triggers",
        "job-permissions-required",
        "runner-no-latest",
        "checkout-persist-credentials",
        "deny-write-all",
        "deny-read-all",
        "deny-inherit-secrets",
        "job-timeout-minutes-required",
        "github-app-token-inputs",
        "known-vulnerable-actions",
        "impostor-commit",
        "ref-confusion",
        "stale-action-refs",
        "cache-poisoning-trigger",
        "self-hosted-runner-trigger",
        "unredacted-secrets",
        "secrets-outside-env",
        "workflow-secrets",
        "job-secrets",
        "action-shell-is-required",
        "fake-ternary",
        "archived-uses",
        "insecure-commands",
        "overprovisioned-secrets",
        "forbidden-uses",
        "ref-version-mismatch",
        "use-trusted-publishing",
        "run-env-context-direct-use",
        "run-secrets-context-direct-use",
        "run-inputs-context-direct-use",
        "secrets-whole-context-access",
        "concurrency-limits",
        "syntax",
    };

    // Fixtures that rely on external tools seiton intentionally does not support.
    private static readonly HashSet<string> ScopeOutFixtures = new(StringComparer.Ordinal)
    {
        "shellcheck_integration",
        "pyflakes_integration",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "seiton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string GetExamplesFixturesRoot()
    {
        return Path.Combine(FindRepoRoot(), "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "examples");
    }

    /// <summary>Fixture data for a single actionlint examples test case.</summary>
    public sealed record ExamplesFixture(string Name, string YamlPath, string OutPath);

    /// <summary>Enumerates all .yaml/.out pairs in the examples fixtures directory.</summary>
    public static IEnumerable<Func<ExamplesFixture>> GetFixtures()
    {
        var dir = GetExamplesFixturesRoot();
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var yamlPath in Directory.EnumerateFiles(dir, "*.yaml").OrderBy(static p => p, StringComparer.Ordinal))
        {
            var outPath = Path.ChangeExtension(yamlPath, ".out");
            if (File.Exists(outPath))
            {
                var name = Path.GetFileNameWithoutExtension(yamlPath);
                yield return () => new ExamplesFixture(name, yamlPath, outPath);
            }
        }
    }

    /// <summary>
    /// Builds the file path to pass to LintEngine.Check so that local action resolution works.
    /// The examples directory contains a .github/actions/ folder, so we simulate the workflow
    /// being at .github/workflows/test.yaml relative to the examples root.
    /// </summary>
    private static string GetLintFilePath()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        return Path.Combine(examplesRoot, ".github", "workflows", "test.yaml");
    }

    [Test]
    [MethodDataSource(nameof(GetFixtures))]
    [DisplayName("Examples: $name")]
    public async Task CompareWithActionlintExpectation(ExamplesFixture fixture)
    {
        // Skip fixtures that require external tools seiton intentionally does not support
        if (ScopeOutFixtures.Contains(fixture.Name))
        {
            return;
        }

        // 1. Run seiton lint
        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(fixture.YamlPath);
        var engine = new LintEngine();
        var lintFilePath = GetLintFilePath();
        var result = engine.Check(utf8Yaml, lintFilePath);

        // 2. Convert seiton diagnostics to actionlint format, excluding seiton-only rules
        var seitonLines = FormatAsActionlint(result.Diagnostics);

        // 3. Parse .out expectations
        var expectations = ParseOutFile(fixture.OutPath);

        // 4. Match seiton lines against expectations (exact + line-number + near-line fallback)
        var matchResult = Match(seitonLines, expectations, fixture.Name);

        // 5. Verify: seiton must not crash (this always asserts)
        await Assert.That(result.Diagnostics).IsNotNull();

        // 6. Report only true gaps (informational — does not fail the test)
        if (matchResult.UnmatchedExpected.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{fixture.Name}] exact={matchResult.ExactMatchCount} line={matchResult.LineMatchCount} near={matchResult.NearLineMatchCount} miss={matchResult.UnmatchedExpected.Count} extra={matchResult.ExtraSeiton.Count}");
            foreach (var line in matchResult.UnmatchedExpected)
            {
                sb.AppendLine($"  MISS: {(line.IsRegex ? "/" + line.Pattern + "/" : line.Pattern)}");
            }

            Console.Write(sb);
        }
    }

    /// <summary>
    /// Summary test that reports aggregate compatibility statistics across all examples fixtures.
    /// </summary>
    [Test]
    public async Task ExamplesCompatibilitySummary()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        if (!Directory.Exists(examplesRoot))
        {
            return;
        }

        var totalFixtures = 0;
        var scopeOutCount = 0;
        var fullyMatched = 0;
        var totalExpected = 0;
        var totalExactMatched = 0;
        var totalLineMatched = 0;
        var totalNearLineMatched = 0;
        var totalMiss = 0;
        var totalExtra = 0;
        var lintFilePath = GetLintFilePath();

        foreach (var (name, yamlPath, outPath) in GetFixtures().Select(static f => f()))
        {
            if (ScopeOutFixtures.Contains(name))
            {
                scopeOutCount++;
                continue;
            }

            totalFixtures++;

            var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(yamlPath);
            var engine = new LintEngine();
            var result = engine.Check(utf8Yaml, lintFilePath);

            var seitonLines = FormatAsActionlint(result.Diagnostics);
            var expectations = ParseOutFile(outPath);
            var matchResult = Match(seitonLines, expectations, name);

            totalExpected += expectations.Count;
            totalExactMatched += matchResult.ExactMatchCount;
            totalLineMatched += matchResult.LineMatchCount;
            totalNearLineMatched += matchResult.NearLineMatchCount;
            totalMiss += matchResult.UnmatchedExpected.Count;
            totalExtra += matchResult.ExtraSeiton.Count;

            if (matchResult.UnmatchedExpected.Count == 0)
            {
                fullyMatched++;
            }
        }

        var report = new StringBuilder();
        var totalMatched = totalExactMatched + totalLineMatched + totalNearLineMatched;
        report.AppendLine("=== Actionlint Examples Compatibility Summary ===");
        report.AppendLine($"Fixtures: {fullyMatched}/{totalFixtures} fully compatible ({scopeOutCount} scope-out excluded)");
        report.AppendLine($"Expected lines: {totalMatched}/{totalExpected} matched ({(totalExpected > 0 ? totalMatched * 100 / totalExpected : 0)}%)");
        report.AppendLine($"  Exact match: {totalExactMatched}");
        report.AppendLine($"  Line match (design diff): {totalLineMatched}");
        report.AppendLine($"  Near-line match (position diff): {totalNearLineMatched}");
        report.AppendLine($"  True gaps (MISS): {totalMiss}");
        report.AppendLine($"Extra seiton lines: {totalExtra} (additional detections)");
        Console.Write(report);

        // Informational — always passes
        await Assert.That(totalFixtures).IsGreaterThan(0);
    }

    /// <summary>
    /// Generates or updates <c>.seiton.out</c> files that capture seiton's actual output
    /// for each actionlint examples fixture. Run with <c>SEITON_UPDATE_OUT=1</c> env var to write files.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(GetFixtures))]
    [DisplayName("ExamplesSeitonOut: $name")]
    public async Task GenerateOrVerifySeitonOut(ExamplesFixture fixture)
    {
        if (ScopeOutFixtures.Contains(fixture.Name))
        {
            return;
        }

        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(fixture.YamlPath);
        var engine = new LintEngine();
        var lintFilePath = GetLintFilePath();
        var result = engine.Check(utf8Yaml, lintFilePath);

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var actualContent = string.Join("\n", seitonLines);
        if (actualContent.Length > 0)
        {
            actualContent += "\n";
        }

        var seitonOutPath = Path.ChangeExtension(fixture.YamlPath, ".seiton.out");
        var updateMode = Environment.GetEnvironmentVariable("SEITON_UPDATE_OUT") == "1";

        if (updateMode)
        {
            File.WriteAllText(seitonOutPath, actualContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        else if (File.Exists(seitonOutPath))
        {
            var expected = File.ReadAllText(seitonOutPath).Replace("\r\n", "\n");
            await Assert.That(actualContent).IsEqualTo(expected);
        }
        else
        {
            throw new InvalidOperationException(
                $"[{fixture.Name}] Missing expected output file '{seitonOutPath}'. " +
                "Run with SEITON_UPDATE_OUT=1 to generate or update .seiton.out fixtures.");
        }
    }

    // Format conversion: seiton → actionlint format

    private static List<string> FormatAsActionlint(DiagnosticList diagnostics)
    {
        var lines = new List<string>(diagnostics.Length);
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var d = diagnostics[i];
            var seitonRuleId = d.RuleId ?? "parse";

            if (SeitonOnlyRules.Contains(seitonRuleId))
            {
                continue;
            }

            if (!RuleIdMap.TryGetValue(seitonRuleId, out var actionlintRuleId))
            {
                actionlintRuleId = seitonRuleId;
            }

            lines.Add($"test.yaml:{d.Location.StartLine}:{d.Location.StartColumn}: {d.Message} [{actionlintRuleId}]");
        }

        return lines;
    }

    // .out file parser and matching engine — delegates to shared helper

    private static List<ExpectedLine> ParseOutFile(string outPath) => ActionlintCompatMatcher.ParseOutFile(outPath);

    private static MatchResult Match(List<string> seitonLines, List<ExpectedLine> expectations, string? fixtureName = null)
        => ActionlintCompatMatcher.Match(seitonLines, expectations, fixtureName);

    /// <summary>
    /// Regression test: unpinned-uses format checks (ref missing, owner missing, etc.)
    /// should map to actionlint's [action] rule and produce zero MISS for this fixture.
    /// </summary>
    [Test]
    public async Task InvalidActionFormat_UnpinnedUsesFormatChecks_ZeroMiss()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        var yamlPath = Path.Combine(examplesRoot, "invalid_action_format.yaml");
        var outPath = Path.Combine(examplesRoot, "invalid_action_format.out");

        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(yamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, GetLintFilePath());

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var expectations = ParseOutFile(outPath);
        var matchResult = Match(seitonLines, expectations, "invalid_action_format");

        await Assert.That(matchResult.UnmatchedExpected).Count().IsEqualTo(0);
    }

    /// <summary>
    /// Regression test: local-action-inputs rule resolves action metadata and produces
    /// required/unknown input diagnostics for the local_action_inputs fixture.
    /// </summary>
    [Test]
    public async Task LocalActionInputs_ResolvesMetadata_ZeroMiss()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        var yamlPath = Path.Combine(examplesRoot, "local_action_inputs.yaml");
        var outPath = Path.Combine(examplesRoot, "local_action_inputs.out");

        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(yamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, GetLintFilePath());

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var expectations = ParseOutFile(outPath);
        var matchResult = Match(seitonLines, expectations, "local_action_inputs");

        await Assert.That(matchResult.UnmatchedExpected).Count().IsEqualTo(0);
    }

    /// <summary>
    /// Regression test: action metadata validation (env not allowed, description required, etc.)
    /// produces diagnostics matching actionlint's expectations for the action_metadata_syntax_validation fixture.
    /// </summary>
    [Test]
    public async Task ActionMetadataSyntaxValidation_ResolvesMetadata_ZeroMiss()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        var yamlPath = Path.Combine(examplesRoot, "action_metadata_syntax_validation.yaml");
        var outPath = Path.Combine(examplesRoot, "action_metadata_syntax_validation.out");

        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(yamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, GetLintFilePath());

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var expectations = ParseOutFile(outPath);
        var matchResult = Match(seitonLines, expectations, "action_metadata_syntax_validation");

        await Assert.That(matchResult.UnmatchedExpected).Count().IsEqualTo(0);
    }

    /// <summary>
    /// Regression test: reusable workflow local file existence check produces a diagnostic
    /// that matches actionlint's expectation for workflow_call_jobs fixture.
    /// </summary>
    [Test]
    public async Task WorkflowCallJobs_LocalFileExistence_ZeroMiss()
    {
        var examplesRoot = GetExamplesFixturesRoot();
        var yamlPath = Path.Combine(examplesRoot, "workflow_call_jobs.yaml");
        var outPath = Path.Combine(examplesRoot, "workflow_call_jobs.out");

        var utf8Yaml = ActionlintCompatMatcher.ReadYamlUtf8Normalized(yamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, GetLintFilePath());

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var expectations = ParseOutFile(outPath);
        var matchResult = Match(seitonLines, expectations, "workflow_call_jobs");

        await Assert.That(matchResult.UnmatchedExpected).Count().IsEqualTo(0);
    }
}
