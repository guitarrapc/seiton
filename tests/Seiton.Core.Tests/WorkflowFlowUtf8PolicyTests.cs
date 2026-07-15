using System.Text.RegularExpressions;

namespace Seiton.Core.Tests;

public sealed class WorkflowFlowUtf8PolicyTests
{
    [Test]
    public async Task Collector_ShouldNotUseTemporaryDecodedStringArrays()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Seiton.Core", "Flow", "WorkflowFlowCollector.cs");
        var text = File.ReadAllText(path);

        // These arrays are the final storage exposed by the Flow DTO. Any new string array
        // in the collector must be reviewed and explicitly classified as owned output.
        HashSet<string> ownedOutputArrays =
        [
            "new string[events.Count]",
            "new string[scopes.Count]",
            "new string[rows.Count]",
            "new string[list.Count]",
        ];

        var violations = Regex.Matches(text, @"new string\[[^\]]+\]")
            .Select(static match => match.Value)
            .Where(allocation => !ownedOutputArrays.Contains(allocation))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task LiveAstWriters_ShouldNotMaterializeOwnedDto()
    {
        var root = FindRepoRoot();
        var flowDir = Path.Combine(root, "src", "Seiton.Core", "Flow");
        var files = new[]
        {
            Path.Combine(flowDir, "WorkflowFlowJson.Ast.cs"),
            Path.Combine(flowDir, "WorkflowFlowMermaid.Ast.cs"),
        };

        var violations = files
            .Where(file => File.ReadAllText(file).Contains(
                "WorkflowFlowCollector",
                StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        await Assert.That(violations).IsEmpty();
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "seiton.slnx")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
