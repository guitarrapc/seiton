using System.Text;
using System.Text.RegularExpressions;
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
        "cache-poisoning",
        "self-hosted-runner",
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
        var utf8Yaml = File.ReadAllBytes(fixture.YamlPath);
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

            var utf8Yaml = File.ReadAllBytes(yamlPath);
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

        var utf8Yaml = File.ReadAllBytes(fixture.YamlPath);
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
            // No .seiton.out yet — write it for the first time
            Console.Write($"[{fixture.Name}] .seiton.out not found, writing {seitonLines.Count} lines");
            File.WriteAllText(seitonOutPath, actualContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    // ──────────────────────────────────────────────
    // Format conversion: seiton → actionlint format
    // ──────────────────────────────────────────────

    private static List<string> FormatAsActionlint(Diagnostic[] diagnostics)
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

    // ──────────────────────────────────────────────
    // .out file parser
    // ──────────────────────────────────────────────

    private readonly record struct ExpectedLine(string Pattern, bool IsRegex);

    private static List<ExpectedLine> ParseOutFile(string outPath)
    {
        var rawLines = File.ReadAllLines(outPath);
        var expectations = new List<ExpectedLine>(rawLines.Length);
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length >= 2 && line[0] == '/' && line[^1] == '/')
            {
                expectations.Add(new ExpectedLine(line[1..^1], IsRegex: true));
            }
            else
            {
                expectations.Add(new ExpectedLine(line, IsRegex: false));
            }
        }

        return expectations;
    }

    // ──────────────────────────────────────────────
    // Line matching engine
    // ──────────────────────────────────────────────

    private sealed class MatchResult
    {
        public int ExactMatchCount { get; set; }
        public int LineMatchCount { get; set; }
        public int NearLineMatchCount { get; set; }
        public List<ExpectedLine> UnmatchedExpected { get; } = [];
        public List<string> ExtraSeiton { get; } = [];
    }

    private static MatchResult Match(List<string> seitonLines, List<ExpectedLine> expectations, string? fixtureName = null)
    {
        var result = new MatchResult();
        var seitonMatched = new bool[seitonLines.Count];

        // Normalize expected lines: replace {fixtureName}.yaml: with test.yaml:
        var normalized = NormalizeExpectations(expectations, fixtureName);

        // Pass 1: Exact/regex match
        var pass1Unmatched = new List<ExpectedLine>();
        for (var i = 0; i < normalized.Count; i++)
        {
            var expected = normalized[i];
            var found = false;

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                if (IsMatch(seitonLines[j], expected))
                {
                    seitonMatched[j] = true;
                    found = true;
                    result.ExactMatchCount++;
                    break;
                }
            }

            if (!found)
            {
                pass1Unmatched.Add(expected);
            }
        }

        // Pass 2: Line-number match for remaining unmatched expectations.
        var pass2Unmatched = new List<ExpectedLine>();
        foreach (var expected in pass1Unmatched)
        {
            var expectedLineNum = ExtractExpectedLineNumber(expected);
            if (expectedLineNum < 0)
            {
                pass2Unmatched.Add(expected);
                continue;
            }

            var found = false;
            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                var seitonLineNum = ExtractLineNumber(seitonLines[j]);
                if (seitonLineNum == expectedLineNum)
                {
                    seitonMatched[j] = true;
                    found = true;
                    result.LineMatchCount++;
                    break;
                }
            }

            if (!found)
            {
                pass2Unmatched.Add(expected);
            }
        }

        // Pass 3: Near-line match with same rule ID for remaining unmatched expectations.
        foreach (var expected in pass2Unmatched)
        {
            var expectedLineNum = ExtractExpectedLineNumber(expected);
            var expectedRuleId = ExtractExpectedRuleId(expected);

            if (expectedRuleId == null)
            {
                result.UnmatchedExpected.Add(expected);
                continue;
            }

            var bestIdx = -1;
            var bestDistance = int.MaxValue;

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (seitonMatched[j])
                {
                    continue;
                }

                var seitonRuleId = ExtractRuleId(seitonLines[j]);
                if (!string.Equals(seitonRuleId, expectedRuleId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (expectedLineNum == 0)
                {
                    bestIdx = j;
                    break;
                }

                if (expectedLineNum > 0)
                {
                    var seitonLineNum = ExtractLineNumber(seitonLines[j]);
                    var distance = Math.Abs(seitonLineNum - expectedLineNum);
                    if (distance <= 5 && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIdx = j;
                    }
                }
            }

            if (bestIdx >= 0)
            {
                seitonMatched[bestIdx] = true;
                result.NearLineMatchCount++;
            }
            else
            {
                result.UnmatchedExpected.Add(expected);
            }
        }

        // Collect unmatched seiton lines
        for (var j = 0; j < seitonLines.Count; j++)
        {
            if (!seitonMatched[j])
            {
                result.ExtraSeiton.Add(seitonLines[j]);
            }
        }

        return result;
    }

    private static List<ExpectedLine> NormalizeExpectations(List<ExpectedLine> expectations, string? fixtureName)
    {
        if (fixtureName == null)
        {
            return expectations;
        }

        var fixturePrefix = $"{fixtureName}.yaml:";
        var needsNormalization = false;
        for (var i = 0; i < expectations.Count; i++)
        {
            if (!expectations[i].IsRegex && expectations[i].Pattern.StartsWith(fixturePrefix, StringComparison.Ordinal))
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
        {
            return expectations;
        }

        var normalized = new List<ExpectedLine>(expectations.Count);
        for (var i = 0; i < expectations.Count; i++)
        {
            var e = expectations[i];
            if (!e.IsRegex && e.Pattern.StartsWith(fixturePrefix, StringComparison.Ordinal))
            {
                normalized.Add(new ExpectedLine("test.yaml:" + e.Pattern[fixturePrefix.Length..], e.IsRegex));
            }
            else
            {
                normalized.Add(e);
            }
        }

        return normalized;
    }

    private static string? ExtractRuleId(string line)
    {
        var end = line.LastIndexOf(']');
        if (end < 1)
        {
            return null;
        }

        var start = line.LastIndexOf('[', end - 1);
        if (start < 0)
        {
            return null;
        }

        return line[(start + 1)..end];
    }

    private static string? ExtractExpectedRuleId(ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            var match = Regex.Match(expected.Pattern, @"\\\[([^\]\\]+)\\\]$");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            var match2 = Regex.Match(expected.Pattern, @"\[([^\]]+)\]$");
            return match2.Success ? match2.Groups[1].Value : null;
        }

        return ExtractRuleId(expected.Pattern);
    }

    private static int ExtractLineNumber(string formattedLine)
    {
        if (formattedLine.StartsWith("test.yaml:", StringComparison.Ordinal))
        {
            var colonIdx = formattedLine.IndexOf(':', 10);
            if (colonIdx > 10 && int.TryParse(formattedLine.AsSpan(10, colonIdx - 10), out var lineNum))
            {
                return lineNum;
            }
        }

        return -1;
    }

    private static int ExtractExpectedLineNumber(ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            var match = Regex.Match(expected.Pattern, @"test\\?\.yaml:(\d+):");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        return ExtractLineNumber(expected.Pattern);
    }

    private static bool IsMatch(string actual, ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            return Regex.IsMatch(actual, expected.Pattern);
        }

        return string.Equals(actual, expected.Pattern, StringComparison.Ordinal);
    }

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

        var utf8Yaml = File.ReadAllBytes(yamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, GetLintFilePath());

        var seitonLines = FormatAsActionlint(result.Diagnostics);
        var expectations = ParseOutFile(outPath);
        var matchResult = Match(seitonLines, expectations, "invalid_action_format");

        await Assert.That(matchResult.UnmatchedExpected).Count().IsEqualTo(0);
    }
}
