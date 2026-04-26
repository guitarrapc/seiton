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
        "outdated-action-runner",
        "run-env-context-direct-use",
        "run-secrets-context-direct-use",
        "run-inputs-context-direct-use",
        "secrets-whole-context-access",
        "syntax",
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
        // 1. Run seiton lint
        var utf8Yaml = File.ReadAllBytes(fixture.YamlPath);
        var engine = new LintEngine();
        var result = engine.Check(utf8Yaml, "test.yaml");

        // 2. Convert seiton diagnostics to actionlint format, excluding seiton-only rules
        var seitonLines = FormatAsActionlint(result.Diagnostics);

        // 3. Parse .out expectations
        var expectations = ParseOutFile(fixture.OutPath);

        // 4. Match seiton lines against expectations
        var matchResult = Match(seitonLines, expectations);

        // 5. Verify: seiton must not crash (this always asserts)
        await Assert.That(result.Diagnostics).IsNotNull();

        // 6. Report match statistics (informational — does not fail the test)
        // When all expectations are matched, the fixture is fully compatible.
        // Unmatched expectations indicate rules or messages seiton doesn't yet produce.
        if (matchResult.UnmatchedExpected.Count > 0 || matchResult.ExtraSeiton.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{fixture.Name}] matched={matchResult.MatchedCount}/{expectations.Count} unmatched={matchResult.UnmatchedExpected.Count} extra={matchResult.ExtraSeiton.Count}");
            foreach (var line in matchResult.UnmatchedExpected)
            {
                sb.AppendLine($"  MISS: {(line.IsRegex ? "/" + line.Pattern + "/" : line.Pattern)}");
            }

            foreach (var line in matchResult.ExtraSeiton)
            {
                sb.AppendLine($"  EXTRA: {line}");
            }

            // Write to test output for visibility
            Console.Write(sb);
        }
    }

    /// <summary>
    /// Summary test that reports aggregate compatibility statistics across all fixtures.
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
        var fullyMatched = 0;
        var totalExpected = 0;
        var totalMatched = 0;
        var totalExtra = 0;

        foreach (var (name, yamlPath, outPath) in GetFixtures())
        {
            totalFixtures++;

            var utf8Yaml = File.ReadAllBytes(yamlPath);
            var engine = new LintEngine();
            var result = engine.Check(utf8Yaml, "test.yaml");

            var seitonLines = FormatAsActionlint(result.Diagnostics);
            var expectations = ParseOutFile(outPath);
            var matchResult = Match(seitonLines, expectations);

            totalExpected += expectations.Count;
            totalMatched += matchResult.MatchedCount;
            totalExtra += matchResult.ExtraSeiton.Count;

            if (matchResult.UnmatchedExpected.Count == 0)
            {
                fullyMatched++;
            }
        }

        var report = new StringBuilder();
        report.AppendLine("=== Actionlint Compatibility Summary ===");
        report.AppendLine($"Fixtures: {fullyMatched}/{totalFixtures} fully matched");
        report.AppendLine($"Expected lines: {totalMatched}/{totalExpected} matched ({(totalExpected > 0 ? totalMatched * 100 / totalExpected : 0)}%)");
        report.AppendLine($"Extra seiton lines: {totalExtra}");
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
        public int MatchedCount { get; set; }
        public List<ExpectedLine> UnmatchedExpected { get; } = [];
        public List<string> ExtraSeiton { get; } = [];
    }

    /// <summary>
    /// Matches seiton output lines against expected lines.
    /// Each expected line consumes at most one seiton line.
    /// Literal expectations use exact string comparison; regex expectations use <see cref="Regex.IsMatch(string)"/>.
    /// </summary>
    private static MatchResult Match(List<string> seitonLines, List<ExpectedLine> expectations)
    {
        var result = new MatchResult();

        // Track which seiton lines have been matched
        var matched = new bool[seitonLines.Count];

        for (var i = 0; i < expectations.Count; i++)
        {
            var expected = expectations[i];
            var found = false;

            for (var j = 0; j < seitonLines.Count; j++)
            {
                if (matched[j])
                {
                    continue;
                }

                if (IsMatch(seitonLines[j], expected))
                {
                    matched[j] = true;
                    found = true;
                    result.MatchedCount++;
                    break;
                }
            }

            if (!found)
            {
                result.UnmatchedExpected.Add(expected);
            }
        }

        // Collect unmatched seiton lines
        for (var j = 0; j < seitonLines.Count; j++)
        {
            if (!matched[j])
            {
                result.ExtraSeiton.Add(seitonLines[j]);
            }
        }

        return result;
    }

    private static bool IsMatch(string actual, ExpectedLine expected)
    {
        if (expected.IsRegex)
        {
            return Regex.IsMatch(actual, expected.Pattern);
        }

        return string.Equals(actual, expected.Pattern, StringComparison.Ordinal);
    }
}
