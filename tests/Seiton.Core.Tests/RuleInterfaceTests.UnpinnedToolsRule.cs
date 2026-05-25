using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UnpinnedToolsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-unrelated-action",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
            new RuleCase(
            "ok-pinned-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: v0.50.0
            """,
            []),
            new RuleCase(
            "ng-no-version-input",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
            """,
            ["does not specify 'version'", "unpinned latest"]),
            new RuleCase(
            "ng-version-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: latest
            """,
            ["version: latest", "unpinned"]),
            new RuleCase(
            "ng-version-dynamic-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: aquasecurity/setup-trivy@v0.2.0
                          with:
                            version: ${{ inputs.trivy-version }}
            """,
            ["dynamically", "unpinned"]),
            new RuleCase(
            "ng-case-insensitive-owner-repo",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: AquaSecurity/Setup-Trivy@v0.2.0
            """,
            ["does not specify 'version'", "unpinned latest"]),
        };

        await AssertRuleCases(new UnpinnedToolsRule(), "unpinned-tools", cases);
    }


    [Test]
    public async Task RuleRegression_UnpinnedToolsRule_ActionMetadataCompositeStep_Warns()
    {
        var yaml = NormalizeYaml("""
            name: demo
            description: demo composite action
            runs:
              using: composite
              steps:
                - uses: aquasecurity/setup-trivy@v0.2.0
        """);

        using var result = new LintEngine([new UnpinnedToolsRule()]).Check(
            Encoding.UTF8.GetBytes(yaml),
            ".github/actions/demo/action.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-tools").ToArray();
        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].Message.Contains("does not specify 'version'", StringComparison.Ordinal)).IsTrue();
    }
}
