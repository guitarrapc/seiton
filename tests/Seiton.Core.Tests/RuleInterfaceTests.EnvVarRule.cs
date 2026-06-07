using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task LintEngine_EnvVar_Help_ShowsRemediationHints()
    {
        var yaml = """
            on: push
            env:
                upstream: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """;

        using var result = new LintEngine([new EnvVarRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "env-var-help-workflow.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "env-var");

        await Assert.That(diagnostic.Help).IsNotNull();
        await Assert.That(diagnostic.Help!).Contains("UPPER");
        await Assert.That(diagnostic.Help!).Contains("inputs");
        await Assert.That(diagnostic.Help!).Contains("with:");
    }

    [Test]
    public async Task LintEngine_EnvVar_Help_ShownForJobAndStepEnvKeys()
    {
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        branch: main
                    steps:
                        - env:
                              fruit: apple
                          run: echo ng
            """;

        using var result = new LintEngine([new EnvVarRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "env-var-help-job-step.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "env-var").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(2);
        foreach (var diagnostic in diagnostics)
        {
            await Assert.That(diagnostic.Help).IsNotNull();
            await Assert.That(diagnostic.Help!).Contains("UPPER");
        }
    }

    [Test]
    public async Task RuleRegression_EnvVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-portable-env-keys",
            """
            on: push
            env:
                GLOBAL_TOKEN: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        JOB_TOKEN_1: x
                    steps:
                        - env:
                              STEP_TOKEN: x
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-env-key-lowercase",
            """
            on: push
            env:
                github_token: x
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["workflow.env key 'github_token' is not portable"]),
            new RuleCase(
            "ng-step-env-key-dash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                              TOKEN-NAME: x
                          run: echo ng
            """,
            ["step.env key 'TOKEN-NAME' is not portable"]),
        };

        await AssertRuleCases(new EnvVarRule(), "env-var", cases);
    }
}
