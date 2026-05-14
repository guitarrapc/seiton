using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed class RuleStateIsolationTests
{
    [Test]
    public async Task RunInputsContextDirectUseRule_DoesNotCarryDiagnosticsAcrossChecks()
    {
        var violatingYaml = """
        on:
          workflow_dispatch:
            inputs:
              benchmark:
                required: false
                type: string
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - run: echo "${{ inputs.benchmark }}"
        """;

        var cleanYaml = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - run: echo "ok"
        """;

        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);

        using var first = engine.Check(Encoding.UTF8.GetBytes(violatingYaml), "first.yml");
        using var second = engine.Check(Encoding.UTF8.GetBytes(cleanYaml), "second.yml");

        await Assert.That(first.Diagnostics.Count(d => d.RuleId == "run-inputs-context-direct-use")).IsEqualTo(1);
        await Assert.That(second.Diagnostics.Count(d => d.RuleId == "run-inputs-context-direct-use")).IsEqualTo(0);
    }

    [Test]
    public async Task RunSecretsContextDirectUseRule_DoesNotCarryDiagnosticsAcrossChecks()
    {
        var violatingYaml = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - run: echo "${{ secrets.TOKEN }}"
        """;

        var cleanYaml = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - run: echo "ok"
        """;

        var engine = new LintEngine([new RunSecretsContextDirectUseRule()]);

        using var first = engine.Check(Encoding.UTF8.GetBytes(violatingYaml), "first.yml");
        using var second = engine.Check(Encoding.UTF8.GetBytes(cleanYaml), "second.yml");

        await Assert.That(first.Diagnostics.Count(d => d.RuleId == "run-secrets-context-direct-use")).IsEqualTo(1);
        await Assert.That(second.Diagnostics.Count(d => d.RuleId == "run-secrets-context-direct-use")).IsEqualTo(0);
    }
}
