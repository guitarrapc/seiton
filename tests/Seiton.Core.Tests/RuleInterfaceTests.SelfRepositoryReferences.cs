using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task LocalActionInputsRule_SelfRepositoryReference_ValidatesContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-action-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "required-input");
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(actionDirectory);
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), """
            name: Required input
            description: Test action
            inputs:
              target:
                required: true
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - uses: $/.github/actions/required-input
            """, Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "local-action-inputs"
                && x.Message.Contains("required input 'target' is not set", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task LocalActionInputsRule_SelfRepositoryReferenceWithNonAsciiPath_ValidatesContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-action-unicode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "日本語");
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(actionDirectory);
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), """
            name: Required input
            description: Test action
            inputs:
              target:
                required: true
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - uses: $/.github/actions/日本語
            """, Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "local-action-inputs"
                && x.Message.Contains("required input 'target' is not set", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ReusableWorkflowRule_SelfRepositoryReference_ValidatesContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "reusable.yml"), """
            on:
              workflow_call:
                inputs:
                  target:
                    type: string
                    required: true
            jobs:
              run:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo ok
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              call:
                uses: $/.github/workflows/reusable.yml
            """, Encoding.UTF8);

            using var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "reusable-workflow"
                && x.Message.Contains("missing required reusable workflow input 'target'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ReusableWorkflowRule_SelfRepositoryReferenceWithNonAsciiPath_ValidatesContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-workflow-unicode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "再利用.yml"), """
            on:
              workflow_call:
                inputs:
                  target:
                    type: string
                    required: true
            jobs:
              run:
                runs-on: ubuntu-24.04
                steps:
                  - run: echo ok
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              call:
                uses: $/.github/workflows/再利用.yml
            """, Encoding.UTF8);

            using var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "reusable-workflow"
                && x.Message.Contains("missing required reusable workflow input 'target'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task UnpinnedUsesRule_SelfRepositoryReferenceWithNonAsciiPath_NoDiagnostics()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-unpinned-unicode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "日本語");
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(actionDirectory);
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), """
            name: Unicode action
            description: Test action
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - uses: $/.github/actions/日本語
            """, Encoding.UTF8);

            using var result = new LintEngine([new UnpinnedUsesRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics).IsEmpty();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ExprUndefinedVarRule_SelfRepositoryActionOutputs_UsesStrictContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-action-outputs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actionDirectory = Path.Combine(repositoryRoot, ".github", "actions", "outputs");
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(actionDirectory);
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(actionDirectory, "action.yml"), """
            name: Outputs
            description: Test action
            outputs:
              value:
                description: A value
            runs:
              using: node20
              main: index.js
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              build:
                runs-on: ubuntu-24.04
                steps:
                  - id: local
                    uses: $/.github/actions/outputs
                  - run: echo ${{ steps.local.outputs.typo }}
            """, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "expr-undefined-var"
                && x.Message.Contains("\"typo\" is not defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    [Test]
    public async Task ExprUndefinedVarRule_SelfRepositoryWorkflowOutputs_UsesStrictContract()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "seiton-self-workflow-outputs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workflowDirectory = Path.Combine(repositoryRoot, ".github", "workflows");
            Directory.CreateDirectory(workflowDirectory);
            File.WriteAllText(Path.Combine(workflowDirectory, "reusable.yml"), """
            on:
              workflow_call:
                outputs:
                  value:
                    value: ${{ jobs.build.outputs.value }}
            jobs:
              build:
                runs-on: ubuntu-24.04
                outputs:
                  value: done
                steps:
                  - run: echo ok
            """, Encoding.UTF8);

            var callerPath = Path.Combine(workflowDirectory, "caller.yml");
            File.WriteAllText(callerPath, """
            on: push
            jobs:
              call:
                uses: $/.github/workflows/reusable.yml
              consume:
                needs: call
                runs-on: ubuntu-24.04
                steps:
                  - run: echo ${{ needs.call.outputs.typo }}
            """, Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x =>
                x.RuleId == "expr-undefined-var"
                && x.Message.Contains("\"typo\" is not defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{path}': {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{path}': {ex}");
        }
    }
}
