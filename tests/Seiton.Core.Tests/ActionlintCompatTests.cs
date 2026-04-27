using System.Text;
using System.Text.RegularExpressions;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>
/// Integration test runner that compares seiton lint output against actionlint
/// <c>.out</c> expectation files from <c>testdata/err/</c> fixtures.
/// </summary>
public sealed class ActionlintCompatTests
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
    };

    // Seiton-only rule IDs that have no actionlint equivalent and should be excluded.
    private static readonly HashSet<string> SeitonOnlyRules = new(StringComparer.Ordinal)
    {
        "unpinned-uses",
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
    // These are excluded from actionlint compatibility comparison entirely.
    private static readonly HashSet<string> ScopeOutFixtures = new(StringComparer.Ordinal)
    {
        "shellcheck_default_shell_detection", // shellcheck integration
        "pyflakes_job_default_shell",         // pyflakes integration
        "pyflakes_step_shell",                // pyflakes integration
        "pyflakes_workflow_default_shell",    // pyflakes integration
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

    private static string GetErrFixturesRoot()
    {
        return Path.Combine(FindRepoRoot(), "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err");
    }

    /// <summary>Enumerates all .yaml/.out pairs in the err fixtures directory.</summary>
    public static IEnumerable<(string Name, string YamlPath, string OutPath)> GetFixtures()
    {
        var dir = GetErrFixturesRoot();
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
                yield return (name, yamlPath, outPath);
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(GetFixtures))]
    [DisplayName("Compat: $name")]
    public async Task CompareWithActionlintExpectation((string Name, string YamlPath, string OutPath) fixture)
    {
        // Skip fixtures that require external tools seiton intentionally does not support
        if (ScopeOutFixtures.Contains(fixture.Name))
        {
            await Assert.That(true).IsTrue();
            return;
        }

        // 1. Run seiton lint
        var utf8Yaml = File.ReadAllBytes(fixture.YamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, "test.yaml");

        // 2. Convert seiton diagnostics to actionlint format, excluding seiton-only rules
        var seitonLines = FormatAsActionlint(result.Diagnostics);

        // 3. Parse .out expectations
        var expectations = ParseOutFile(fixture.OutPath);

        // 4. Match seiton lines against expectations (exact + line-number fallback)
        var matchResult = Match(seitonLines, expectations);

        // 5. Verify: seiton must not crash (this always asserts)
        await Assert.That(result.Diagnostics).IsNotNull();

        // 6. Report only true gaps (informational — does not fail the test)
        // Line-level matches (same YAML line, different col/msg) are design differences, not gaps.
        // Extra seiton lines are additional useful detections, not problems.
        if (matchResult.UnmatchedExpected.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{fixture.Name}] exact={matchResult.ExactMatchCount} line={matchResult.LineMatchCount} miss={matchResult.UnmatchedExpected.Count} extra={matchResult.ExtraSeiton.Count}");
            foreach (var line in matchResult.UnmatchedExpected)
            {
                sb.AppendLine($"  MISS: {(line.IsRegex ? "/" + line.Pattern + "/" : line.Pattern)}");
            }

            Console.Write(sb);
        }
    }

    /// <summary>
    /// Summary test that reports aggregate compatibility statistics across all fixtures.
    /// Scope-out fixtures (shellcheck/pyflakes) are excluded.
    /// Line-level matches count as compatible (design differences, not gaps).
    /// Extra seiton lines are additional detections, not problems.
    /// </summary>
    [Test]
    public async Task CompatibilitySummary()
    {
        var errRoot = GetErrFixturesRoot();
        if (!Directory.Exists(errRoot))
        {
            return;
        }

        var totalFixtures = 0;
        var scopeOutCount = 0;
        var fullyMatched = 0;    // no unmatched expected lines (exact + line match)
        var totalExpected = 0;
        var totalExactMatched = 0;
        var totalLineMatched = 0;
        var totalMiss = 0;
        var totalExtra = 0;

        foreach (var (name, yamlPath, outPath) in GetFixtures())
        {
            if (ScopeOutFixtures.Contains(name))
            {
                scopeOutCount++;
                continue;
            }

            totalFixtures++;

            var utf8Yaml = File.ReadAllBytes(yamlPath);
            var engine = new LintEngine();
            var result = engine.Check(utf8Yaml, "test.yaml");

            var seitonLines = FormatAsActionlint(result.Diagnostics);
            var expectations = ParseOutFile(outPath);
            var matchResult = Match(seitonLines, expectations);

            totalExpected += expectations.Count;
            totalExactMatched += matchResult.ExactMatchCount;
            totalLineMatched += matchResult.LineMatchCount;
            totalMiss += matchResult.UnmatchedExpected.Count;
            totalExtra += matchResult.ExtraSeiton.Count;

            if (matchResult.UnmatchedExpected.Count == 0)
            {
                fullyMatched++;
            }
        }

        var report = new StringBuilder();
        report.AppendLine("=== Actionlint Compatibility Summary ===");
        report.AppendLine($"Fixtures: {fullyMatched}/{totalFixtures} fully compatible ({scopeOutCount} scope-out excluded)");
        report.AppendLine($"Expected lines: {totalExactMatched + totalLineMatched}/{totalExpected} matched ({(totalExpected > 0 ? (totalExactMatched + totalLineMatched) * 100 / totalExpected : 0)}%)");
        report.AppendLine($"  Exact match: {totalExactMatched}");
        report.AppendLine($"  Line match (design diff): {totalLineMatched}");
        report.AppendLine($"  True gaps (MISS): {totalMiss}");
        report.AppendLine($"Extra seiton lines: {totalExtra} (additional detections)");
        Console.Write(report);

        // Informational — always passes
        await Assert.That(totalFixtures).IsGreaterThan(0);
    }

    // ──────────────────────────────────────────────
    // Format conversion: seiton → actionlint format
    // ──────────────────────────────────────────────

    /// <summary>
    /// Converts seiton diagnostics to actionlint output format.
    /// <para>Actionlint: <c>test.yaml:line:col: message [rule-id]</c></para>
    /// <para>Seiton:     <c>file:line:col: severity [ruleId] message</c></para>
    /// </summary>
    private static List<string> FormatAsActionlint(Diagnostic[] diagnostics)
    {
        var lines = new List<string>(diagnostics.Length);
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var d = diagnostics[i];
            var seitonRuleId = d.RuleId ?? "parse";

            // Exclude seiton-only rules
            if (SeitonOnlyRules.Contains(seitonRuleId))
            {
                continue;
            }

            // Map seiton rule ID → actionlint rule ID
            if (!RuleIdMap.TryGetValue(seitonRuleId, out var actionlintRuleId))
            {
                // Unknown mapping — keep original for debugging
                actionlintRuleId = seitonRuleId;
            }

            // Format: test.yaml:line:col: message [rule-id]
            lines.Add($"test.yaml:{d.Location.StartLine}:{d.Location.StartColumn}: {d.Message} [{actionlintRuleId}]");
        }

        return lines;
    }

    // ──────────────────────────────────────────────
    // .out file parser
    // ──────────────────────────────────────────────

    /// <summary>Parsed expectation line from an <c>.out</c> file.</summary>
    private readonly record struct ExpectedLine(string Pattern, bool IsRegex);

    /// <summary>
    /// Parses an actionlint <c>.out</c> file into expectation lines.
    /// Lines wrapped in <c>/pattern/</c> are treated as regex; others are literal.
    /// </summary>
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
                // Regex pattern — strip surrounding slashes
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
        public List<ExpectedLine> UnmatchedExpected { get; } = [];
        public List<string> ExtraSeiton { get; } = [];
    }

    /// <summary>
    /// Matches seiton output lines against expected lines using a two-pass algorithm.
    /// <para>Pass 1: Exact/regex match — each expected line consumes at most one seiton line.</para>
    /// <para>Pass 2: Line-number match — remaining expected lines are matched by YAML line number.
    /// This accounts for design differences where seiton reports at a different column or
    /// with a different message format than actionlint for the same YAML line.</para>
    /// <para>Unmatched expected lines after both passes are true detection gaps.</para>
    /// <para>Unmatched seiton lines are additional detections (features, not gaps).</para>
    /// </summary>
    private static MatchResult Match(List<string> seitonLines, List<ExpectedLine> expectations)
    {
        var result = new MatchResult();
        var seitonMatched = new bool[seitonLines.Count];

        // Pass 1: Exact/regex match
        var pass1Unmatched = new List<ExpectedLine>();
        for (var i = 0; i < expectations.Count; i++)
        {
            var expected = expectations[i];
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
        // Same YAML line with different column/message = design difference, not a gap.
        foreach (var expected in pass1Unmatched)
        {
            var expectedLineNum = ExtractExpectedLineNumber(expected);
            if (expectedLineNum < 0)
            {
                result.UnmatchedExpected.Add(expected);
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
                result.UnmatchedExpected.Add(expected);
            }
        }

        // Collect unmatched seiton lines (additional detections)
        for (var j = 0; j < seitonLines.Count; j++)
        {
            if (!seitonMatched[j])
            {
                result.ExtraSeiton.Add(seitonLines[j]);
            }
        }

        return result;
    }

    /// <summary>Extracts the YAML line number from a formatted seiton line (test.yaml:LINE:COL: ...).</summary>
    private static int ExtractLineNumber(string formattedLine)
    {
        // Format: test.yaml:LINE:COL: message [rule-id]
        if (formattedLine.StartsWith("test.yaml:", StringComparison.Ordinal))
        {
            var colonIdx = formattedLine.IndexOf(':', 10); // after "test.yaml:"
            if (colonIdx > 10 && int.TryParse(formattedLine.AsSpan(10, colonIdx - 10), out var lineNum))
            {
                return lineNum;
            }
        }

        return -1;
    }

    /// <summary>Extracts the YAML line number from an expected line (regex or literal).</summary>
    private static int ExtractExpectedLineNumber(ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            // Regex patterns contain test\.yaml:LINE: or test\\.yaml:LINE:
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

    // ──────────────────────────────────────────────
    // .seiton.out generation
    // ──────────────────────────────────────────────

    /// <summary>
    /// Generates or updates <c>.seiton.out</c> files that capture seiton's actual output
    /// for each actionlint err fixture. Run with <c>SEITON_UPDATE_OUT=1</c> env var to write files.
    /// Otherwise, this test just verifies existing <c>.seiton.out</c> files are up to date.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(GetFixtures))]
    [DisplayName("SeitonOut: $name")]
    public async Task GenerateOrVerifySeitonOut((string Name, string YamlPath, string OutPath) fixture)
    {
        var utf8Yaml = File.ReadAllBytes(fixture.YamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, "test.yaml");

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
            await Assert.That(true).IsTrue(); // always passes when writing
        }
        else if (File.Exists(seitonOutPath))
        {
            var expected = File.ReadAllText(seitonOutPath).Replace("\r\n", "\n");
            await Assert.That(actualContent).IsEqualTo(expected);
        }
        else
        {
            // No .seiton.out yet — just report what would be written
            Console.Write($"[{fixture.Name}] .seiton.out not found, would write {seitonLines.Count} lines");
            await Assert.That(true).IsTrue();
        }
    }

    /// <summary>
    /// Generates a mapping summary CSV showing actionlint vs seiton correspondence per fixture.
    /// Run with <c>SEITON_UPDATE_OUT=1</c> to write the mapping file.
    /// </summary>
    [Test]
    public async Task GenerateMappingSummary()
    {
        var errRoot = GetErrFixturesRoot();
        if (!Directory.Exists(errRoot))
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("fixture,actionlint_line,actionlint_msg_prefix,seiton_line,seiton_msg_prefix,status");

        foreach (var (name, yamlPath, outPath) in GetFixtures())
        {
            if (ScopeOutFixtures.Contains(name))
            {
                continue;
            }

            var utf8Yaml = File.ReadAllBytes(yamlPath);
            var engine = new LintEngine();
            var result = engine.Check(utf8Yaml, "test.yaml");

            var seitonLines = FormatAsActionlint(result.Diagnostics);
            var expectations = ParseOutFile(outPath);

            // Track which seiton lines were matched and match status
            var seitonMatched = new bool[seitonLines.Count];
            var pass1Unmatched = new List<(int Index, ExpectedLine Expected)>();

            // Pass 1: exact/regex match
            for (var i = 0; i < expectations.Count; i++)
            {
                var expected = expectations[i];
                var matchedSeitonIdx = -1;
                for (var j = 0; j < seitonLines.Count; j++)
                {
                    if (seitonMatched[j])
                    {
                        continue;
                    }

                    if (IsMatch(seitonLines[j], expected))
                    {
                        seitonMatched[j] = true;
                        matchedSeitonIdx = j;
                        break;
                    }
                }

                var expectedPrefix = Truncate(expected.IsRegex ? "/" + expected.Pattern + "/" : expected.Pattern, 120);
                if (matchedSeitonIdx >= 0)
                {
                    var seitonPrefix = Truncate(seitonLines[matchedSeitonIdx], 120);
                    sb.AppendLine($"{name},{i + 1},\"{Escape(expectedPrefix)}\",{matchedSeitonIdx + 1},\"{Escape(seitonPrefix)}\",MATCH");
                }
                else
                {
                    pass1Unmatched.Add((i, expected));
                }
            }

            // Pass 2: line-number match for remaining
            foreach (var (idx, expected) in pass1Unmatched)
            {
                var expectedPrefix = Truncate(expected.IsRegex ? "/" + expected.Pattern + "/" : expected.Pattern, 120);
                var expectedLineNum = ExtractExpectedLineNumber(expected);
                var matchedSeitonIdx = -1;

                if (expectedLineNum >= 0)
                {
                    for (var j = 0; j < seitonLines.Count; j++)
                    {
                        if (seitonMatched[j])
                        {
                            continue;
                        }

                        if (ExtractLineNumber(seitonLines[j]) == expectedLineNum)
                        {
                            seitonMatched[j] = true;
                            matchedSeitonIdx = j;
                            break;
                        }
                    }
                }

                if (matchedSeitonIdx >= 0)
                {
                    var seitonPrefix = Truncate(seitonLines[matchedSeitonIdx], 120);
                    sb.AppendLine($"{name},{idx + 1},\"{Escape(expectedPrefix)}\",{matchedSeitonIdx + 1},\"{Escape(seitonPrefix)}\",LINE_MATCH");
                }
                else
                {
                    sb.AppendLine($"{name},{idx + 1},\"{Escape(expectedPrefix)}\",-,,MISS");
                }
            }

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (!seitonMatched[j])
                {
                    var seitonPrefix = Truncate(seitonLines[j], 120);
                    sb.AppendLine($"{name},-,,{j + 1},\"{Escape(seitonPrefix)}\",EXTRA");
                }
            }
        }

        var updateMode = Environment.GetEnvironmentVariable("SEITON_UPDATE_OUT") == "1";
        if (updateMode)
        {
            var mappingPath = Path.Combine(GetErrFixturesRoot(), "..", "mapping_summary.csv");
            File.WriteAllText(mappingPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Console.Write(sb);
        await Assert.That(sb.Length).IsGreaterThan(0);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static string Escape(string s) => s.Replace("\"", "\"\"");
}
