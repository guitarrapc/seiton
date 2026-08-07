using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DeprecatedPermissionsRule_TableDriven()
    {
        var cases = new[]
        {
            // no permissions at all — nothing to report
            new RuleCase(
            "ok-no-permissions",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // active scopes are never reported by this rule
            new RuleCase(
            "ok-active-scopes",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                        id-token: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // scalar form has no scope names to inspect
            new RuleCase(
            "ok-scalar-read-all",
            """
            on: push
            permissions: read-all
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // unknown scopes belong to the permissions rule, not this one
            new RuleCase(
            "ok-unknown-scope-not-reported",
            """
            on: push
            jobs:
                build:
                    permissions:
                        check: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // deprecated scope at job level — the note half of the message comes from generated data
            new RuleCase(
            "ng-job-deprecated-models",
            """
            on: push
            jobs:
                build:
                    permissions:
                        contents: read
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated. GitHub Models is retired and the scope has no effect"]),
            // deprecated scope at workflow level
            new RuleCase(
            "ng-workflow-deprecated-models",
            """
            on: push
            permissions:
                models: read
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
            // deprecation is independent of the value being valid
            new RuleCase(
            "ng-deprecated-models-invalid-value",
            """
            on: push
            jobs:
                build:
                    permissions:
                        models: write
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
            // empty mapping has no scope names to inspect
            new RuleCase(
            "ok-empty-permissions-mapping",
            """
            on: push
            jobs:
                build:
                    permissions: {}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // scope names are matched case-sensitively; 'Models' is an unknown scope for the permissions rule
            new RuleCase(
            "ok-case-variant-not-reported",
            """
            on: push
            jobs:
                build:
                    permissions:
                        Models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // quoted keys must still match (the parser unquotes the key before the rule sees it)
            new RuleCase(
            "ng-quoted-key-deprecated-models",
            """
            on: push
            jobs:
                build:
                    permissions:
                        "models": read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["permission scope \"models\" was deprecated"]),
            // permissions rules apply to reusable workflow call jobs as well
            new RuleCase(
            "ng-reusable-call-job-deprecated-models",
            """
            on: push
            jobs:
                call:
                    permissions:
                        models: read
                    uses: ./.github/workflows/reusable.yml
            """,
            ["permission scope \"models\" was deprecated"]),
        };

        await AssertRuleCases(new DeprecatedPermissionsRule(), "deprecated-permissions", cases);
    }

    /// <summary>
    /// Regression: workflow-level and job-level permissions are separate visits, so a deprecated
    /// scope declared in both must produce two diagnostics (the table-driven helper only asserts
    /// substring presence, not counts).
    /// </summary>
    [Test]
    public async Task RuleRegression_DeprecatedPermissionsRule_WorkflowAndJobScopes_ReportsBoth()
    {
        const string Yaml = """
            on: push
            permissions:
                models: read
            jobs:
                build:
                    permissions:
                        models: read
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """;

        using var result = new LintEngine([new DeprecatedPermissionsRule()])
            .Check(Encoding.UTF8.GetBytes(NormalizeYaml(Yaml)), "workflow-and-job.yml");
        var diagnostics = result.Diagnostics.Where(static x => x.RuleId == "deprecated-permissions").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(2);
    }
}
