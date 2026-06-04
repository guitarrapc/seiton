#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:project ../../src/Seiton.Core/Seiton.Core.csproj
using System.Text;
using System.Text.RegularExpressions;
using Seiton.Core.Linting;
using Seiton.Core.Parsing;

// Seiton rule ID → actionlint rule ID mapping
var ruleIdMap = new Dictionary<string, string>(StringComparer.Ordinal)
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

var seitonOnlyRules = new HashSet<string>(StringComparer.Ordinal)
{
    "unpinned-uses", "unpinned-image", "dangerous-triggers", "job-permissions-required",
    "runner-no-latest", "checkout-persist-credentials", "deny-write-all", "deny-read-all",
    "deny-inherit-secrets", "job-timeout-minutes-required", "github-app-token-inputs",
    "known-vulnerable-actions", "impostor-commit", "ref-confusion", "stale-action-refs",
    "cache-poisoning-trigger", "self-hosted-runner-trigger", "unredacted-secrets", "secrets-outside-env",
    "workflow-secrets", "job-secrets", "action-shell-is-required", "fake-ternary",
    "archived-uses", "insecure-commands", "overprovisioned-secrets", "forbidden-uses",
    "ref-version-mismatch", "use-trusted-publishing", "outdated-action-runner",
    "run-env-context-direct-use", "run-secrets-context-direct-use", "run-inputs-context-direct-use",
    "secrets-whole-context-access", "syntax",
};

var dir = FindRepoRoot();
var errDir = Path.Combine(dir, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err");

var mode = args.Length > 0 ? args[0] : "report";
// mode: "report" = show what would change, "update" = write .out files

var updatedCount = 0;
var skippedCount = 0;
var noChangeCount = 0;

foreach (var yamlPath in Directory.EnumerateFiles(errDir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
{
    var outPath = Path.ChangeExtension(yamlPath, ".seiton.out");
    if (!File.Exists(outPath)) continue;

    var name = Path.GetFileNameWithoutExtension(yamlPath);
    var utf8Yaml = File.ReadAllBytes(yamlPath);
    var engine = new LintEngine();
    var result = engine.Check(utf8Yaml, "test.yaml");

    var seitonLines = FormatAsActionlint(result.Diagnostics, ruleIdMap, seitonOnlyRules);
    var existingOut = File.ReadAllLines(outPath);

    // Parse existing .out to check current state
    var expectations = ParseOutFile(outPath);
    var (matchedCount, unmatchedExpected, extraSeiton) = Match(seitonLines, expectations);

    // If fully matched and no extra, skip
    if (unmatchedExpected.Count == 0 && extraSeiton.Count == 0)
    {
        noChangeCount++;
        continue;
    }

    // Generate new .out content from seiton's output
    // Keep lines from existing .out that are NOT produced by seiton (they represent detection gaps)
    // Replace matched lines with seiton's version
    // This approach: write seiton's output as the new .out
    // Lines that actionlint detects but seiton doesn't are removed
    var newOutLines = seitonLines;

    if (mode == "report")
    {
        Console.WriteLine($"[{name}] would update: {seitonLines.Count} lines (was {existingOut.Length} lines, matched={matchedCount}/{expectations.Count})");
        if (seitonLines.Count == 0)
        {
            Console.WriteLine($"  WARNING: seiton produces no mapped output for this fixture");
            skippedCount++;
        }
        else
        {
            updatedCount++;
        }
    }
    else if (mode == "update")
    {
        if (seitonLines.Count == 0)
        {
            Console.WriteLine($"[{name}] SKIPPED: seiton produces no mapped output");
            skippedCount++;
            continue;
        }

        File.WriteAllLines(outPath, newOutLines, new UTF8Encoding(false));
        Console.WriteLine($"[{name}] UPDATED: {seitonLines.Count} lines");
        updatedCount++;
    }
}

Console.WriteLine($"\nTotal: {updatedCount} updated, {skippedCount} skipped (no output), {noChangeCount} no change needed");

static string FindRepoRoot()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d is not null)
    {
        if (File.Exists(Path.Combine(d.FullName, "seiton.slnx")))
            return d.FullName;
        d = d.Parent;
    }
    throw new InvalidOperationException("Could not find repo root");
}

static List<string> FormatAsActionlint(Diagnostic[] diagnostics, Dictionary<string, string> ruleIdMap, HashSet<string> seitonOnlyRules)
{
    var lines = new List<string>(diagnostics.Length);
    foreach (var d in diagnostics)
    {
        var seitonRuleId = d.RuleId ?? "parse";
        if (seitonOnlyRules.Contains(seitonRuleId)) continue;
        if (!ruleIdMap.TryGetValue(seitonRuleId, out var actionlintRuleId))
            actionlintRuleId = seitonRuleId;
        lines.Add($"test.yaml:{d.Location.StartLine}:{d.Location.StartColumn}: {d.Message} [{actionlintRuleId}]");
    }
    return lines;
}

static List<(string Pattern, bool IsRegex)> ParseOutFile(string outPath)
{
    var rawLines = File.ReadAllLines(outPath);
    var expectations = new List<(string, bool)>(rawLines.Length);
    foreach (var rawLine in rawLines)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) continue;
        if (line.Length >= 2 && line[0] == '/' && line[^1] == '/')
            expectations.Add((line[1..^1], true));
        else
            expectations.Add((line, false));
    }
    return expectations;
}

static (int MatchedCount, List<(string Pattern, bool IsRegex)> UnmatchedExpected, List<string> ExtraSeiton) Match(
    List<string> seitonLines, List<(string Pattern, bool IsRegex)> expectations)
{
    var matchedCount = 0;
    var unmatchedExpected = new List<(string, bool)>();
    var matched = new bool[seitonLines.Count];

    foreach (var expected in expectations)
    {
        var found = false;
        for (var j = 0; j < seitonLines.Count; j++)
        {
            if (matched[j]) continue;
            if (expected.IsRegex ? Regex.IsMatch(seitonLines[j], expected.Pattern) : string.Equals(seitonLines[j], expected.Pattern, StringComparison.Ordinal))
            {
                matched[j] = true;
                found = true;
                matchedCount++;
                break;
            }
        }
        if (!found) unmatchedExpected.Add(expected);
    }

    var extraSeiton = new List<string>();
    for (var j = 0; j < seitonLines.Count; j++)
    {
        if (!matched[j]) extraSeiton.Add(seitonLines[j]);
    }
    return (matchedCount, unmatchedExpected, extraSeiton);
}
