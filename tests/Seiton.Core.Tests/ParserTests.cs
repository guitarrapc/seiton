using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class ParserTests
{
    [Test]
    public async Task Parse_MinimalWorkflow_NoDiagnostics()
    {
        var yaml = """
        name: ci
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """
        .Replace("\r\n", "\n");

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "minimal.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Name is not null).IsTrue();
        await Assert.That(result.Workflow.Name!.Value.Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.RunName).IsNull();
        await Assert.That(result.Workflow.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WebhookEvent>();
        await Assert.That(result.Workflow.Jobs.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_TopLevelRunName_PopulatesWorkflowAst()
    {
        var yaml = """
        run-name: Build-${{ github.ref }}
        on: push
        jobs: {}
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "run-name.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.RunName is not null).IsTrue();
        await Assert.That(result.Workflow.RunName!.Value.Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WebhookEvent>();
        await Assert.That(result.Workflow.Jobs.Count).IsEqualTo(0);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_WorkflowStructuralNodes_PopulatesAst()
    {
        var yaml = """
        name: ci
        on: push
        permissions:
            contents: read
            actions: write
        env:
            FOO: bar-${{ github.ref }}
        defaults:
            run:
                shell: bash
                working-directory: src
        concurrency:
            group: ci-${{ github.ref }}
            cancel-in-progress: true
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-structural.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Permissions is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions!.Scopes is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions.Scopes!.Count).IsEqualTo(2);
        await Assert.That(result.Workflow.Env is not null).IsTrue();
        await Assert.That(result.Workflow.Env!.Vars is not null).IsTrue();
        await Assert.That(result.Workflow.Env.Vars!.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.Defaults is not null).IsTrue();
        await Assert.That(result.Workflow.Defaults!.Run.Shell is not null).IsTrue();
        await Assert.That(result.Workflow.Defaults.Run.WorkingDirectory is not null).IsTrue();
        await Assert.That(result.Workflow.Concurrency is not null).IsTrue();
        await Assert.That(result.Workflow.Concurrency!.Group.Value.Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.Concurrency.CancelInProgress is not null).IsTrue();
        await Assert.That(result.Workflow.Concurrency.CancelInProgress!.Value).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_WorkflowPermissionsAndConcurrencyScalar_PopulatesAst()
    {
        var yaml = """
        on: push
        permissions: read-all
        concurrency: ci-${{ github.ref }}
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-scalar-structural.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Permissions is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions!.All is not null).IsTrue();
        await Assert.That(result.Workflow.Concurrency is not null).IsTrue();
        await Assert.That(result.Workflow.Concurrency!.Group.Value.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_MissingRequiredKeys_ReportsErrors()
    {
        var yaml = """
        name: only-name
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "missing.yml");

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("required key 'on' is missing", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("required key 'jobs' is missing", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_UnknownKey_ReportsUnexpectedKey()
    {
        var yaml = """
        on: push
        jobs: {}
        foobar: 1
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "unknown.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected workflow key: foobar", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_DuplicateWorkflowKey_ReportsError()
    {
        var yaml = """
        on: push
        on: pull_request
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "duplicate-workflow-key.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("workflow contains duplicate key: on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_MergeKey_ReportsError()
    {
        var yaml = """
        <<:
          on: push
        on: push
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "merge-key.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("workflow does not support merge key '<<'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnMappingDuplicateKey_ReportsError()
    {
        var yaml = """
                on:
                    push: {}
                    PUSH: {}
                jobs: {}
                """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-duplicate.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on contains duplicate key: PUSH", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobsMappingDuplicateKey_ReportsError()
    {
        var yaml = """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo one
                    BUILD:
                        runs-on: ubuntu-latest
                        steps:
                            - run: echo two
                """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "jobs-duplicate.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("jobs contains duplicate key: BUILD", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnWorkflowDispatchInputsMergeKey_ReportsError()
    {
        var yaml = """
                on:
                    workflow_dispatch:
                        inputs:
                            <<:
                                a:
                                    type: string
                            name:
                                description: d
                                required: true
                                type: string
                jobs: {}
                """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-dispatch-inputs-merge.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.workflow_dispatch.inputs does not support merge key '<<'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnTypeInvalid_ReportsError()
    {
        var yaml = """
        on: true
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-type.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown event in on: true", StringComparison.Ordinal))).IsTrue();

        var yaml2 = """
        on: &a ref
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result2 = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml2), "on-type2.yml");
        await Assert.That(result2.Diagnostics.Any(x => x.Message.Contains("unknown event in on: ref", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnSequenceItemNonScalar_ReportsError()
    {
        var yaml = """
        on:
          - push
          - [nested]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-seq.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on sequence item must be scalar event name", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_WorkflowEnv_WithStepOnlyContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        env:
          BAD: ${{ steps.prep.outputs.ok }}
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in workflow expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_RunName_UnknownFunction_ReportsSemanticError()
    {
        var yaml = """
        run-name: Build ${{ unknownFn(github.ref) }}
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "run-name-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventOptionsMutualExclusive_ReportsError()
    {
        var yaml = """
        on:
            push:
                branches: [main]
                branches-ignore: [dev]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-exclusive.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot use both branches and branches-ignore", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnPushTagsAndTagsIgnore_ReportsError()
    {
        var yaml = """
        on:
            push:
                tags: [v*]
                tags-ignore: [v0.*]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-push-tags-exclusive.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot use both tags and tags-ignore", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnPullRequestPathsAndPathsIgnore_ReportsError()
    {
        var yaml = """
        on:
            pull_request:
                paths: [src/**]
                paths-ignore: [docs/**]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-pr-paths-exclusive.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot use both paths and paths-ignore", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnMergeGroupBranchesAndIgnore_ReportsError()
    {
        var yaml = """
        on:
            merge_group:
                branches: [main]
                branches-ignore: [release/*]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-merge-group-branches-exclusive.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot use both branches and branches-ignore", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnMergeGroupValidTypes_NoError()
    {
        var yaml = """
        on:
            merge_group:
                types: [checks_requested]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-merge-group-types.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unsupported activity type", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_OnUnknownEventScalar_ReportsError()
    {
        var yaml = """
        on: unknown_event
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-unknown-scalar.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown event in on: unknown_event", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnUnknownEventInSequence_ReportsError()
    {
        var yaml = """
        on:
          - push
          - unknown_event
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-unknown-sequence.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown event in on: unknown_event", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventOptionsTypeInvalid_ReportsError()
    {
        var yaml = """
        on:
            pull_request:
                types: { a: b }
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-types-invalid.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.pull_request.types must be scalar or sequence of scalar", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventUnknownOption_ReportsError()
    {
        var yaml = """
        on:
            push:
                unknown-filter: 1
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-unknown-option.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.push does not support option: unknown-filter", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventDisallowedOption_ReportsError()
    {
        var yaml = """
        on:
            workflow_dispatch:
                paths: [src/**]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-disallowed-option.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.workflow_dispatch does not support option: paths", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventTypesInvalidValue_ReportsError()
    {
        var yaml = """
        on:
            pull_request:
                types: [opened, unknown_type]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-types-invalid-value.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unsupported activity type", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnEventTypesValidValue_NoError()
    {
        var yaml = """
        on:
            pull_request:
                types: [opened, reopened]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-types-valid-value.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unsupported activity type", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_OnEventTypesOnUnsupportedEvent_ReportsError()
    {
        var yaml = """
        on:
            push:
                types: [created]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-types-push.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.push.types is not supported", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnRepositoryDispatchCustomTypes_NoError()
    {
        var yaml = """
        on:
            repository_dispatch:
                types: [my-custom-event, another_event]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-repository-dispatch-types.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unsupported activity type", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("does not support option", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_OnSchedule_PopulatesEventAst()
    {
        var yaml = """
        on:
            schedule:
                - cron: '0 0 * * *'
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-schedule.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<ScheduledEvent>();
        var evt = (ScheduledEvent)result.Workflow.On[0];
        await Assert.That(evt.Schedules.Count).IsEqualTo(1);
        await Assert.That(evt.Schedules[0].Cron is not null).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_OnWorkflowDispatch_PopulatesEventAst()
    {
        var yaml = """
        on:
            workflow_dispatch:
                inputs:
                    target:
                        description: Deploy target
                        required: true
                        type: choice
                        options: [dev, prod]
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-workflow-dispatch.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WorkflowDispatchEvent>();
        var evt = (WorkflowDispatchEvent)result.Workflow.On[0];
        await Assert.That(evt.Inputs is not null).IsTrue();
        await Assert.That(evt.Inputs!.Count).IsEqualTo(1);
        var key = Utf8String.FromLowerAscii("target"u8);
        await Assert.That(evt.Inputs.ContainsKey(key)).IsTrue();
        var input = evt.Inputs[key];
        await Assert.That(input.Type).IsEqualTo(DispatchInputType.Choice);
        await Assert.That(input.Required is not null).IsTrue();
        await Assert.That(input.Required!.Value).IsTrue();
        await Assert.That(input.Options is not null).IsTrue();
        await Assert.That(input.Options!.Count).IsEqualTo(2);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_OnWorkflowCall_PopulatesEventAst()
    {
        var yaml = """
        on:
            workflow_call:
                inputs:
                    image:
                        required: true
                        type: string
                secrets:
                    token:
                        required: true
                outputs:
                    digest:
                        value: digest-sha
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-workflow-call.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WorkflowCallEvent>();
        var evt = (WorkflowCallEvent)result.Workflow.On[0];
        await Assert.That(evt.Inputs is not null).IsTrue();
        await Assert.That(evt.Inputs!.Count).IsEqualTo(1);
        await Assert.That(evt.Inputs[0].Type).IsEqualTo(WorkflowCallInputType.String);
        await Assert.That(evt.Secrets is not null).IsTrue();
        await Assert.That(evt.Secrets!.Count).IsEqualTo(1);
        await Assert.That(evt.Outputs is not null).IsTrue();
        await Assert.That(evt.Outputs!.Count).IsEqualTo(1);
        await Assert.That(evt.Outputs.Values.First().Value is not null).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_OnRepositoryDispatch_PopulatesEventAst()
    {
        var yaml = """
        on:
            repository_dispatch:
                types: [sync, deploy]
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-repository-dispatch-ast.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<RepositoryDispatchEvent>();
        var evt = (RepositoryDispatchEvent)result.Workflow.On[0];
        await Assert.That(evt.Types is not null).IsTrue();
        await Assert.That(evt.Types!.Count).IsEqualTo(2);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_JobsTypeInvalid_ReportsError()
    {
        var yaml = """
        on: push
        jobs: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "jobs-type.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("jobs must be mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_CorpusSmoke_Actionlint_Ghalint_Zizmor_DoesNotThrow()
    {
        var root = FindRepoRoot();
        var files = EnumerateCorpusYamlFiles(root).ToArray();
        await Assert.That(files.Length).IsGreaterThan(0);

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                _ = WorkflowParser.Parse(bytes, file);
            }
            catch (Exception ex)
            {
                failures.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Parse_CorpusSmoke_ActionlintTestdata_DoesNotThrow()
    {
        var root = FindRepoRoot();
        var actionlintTestdata = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata");
        if (!Directory.Exists(actionlintTestdata))
        {
            // Checked-in fixture corpus should always exist.
            return;
        }

        var allFiles = Directory.EnumerateFiles(actionlintTestdata, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(actionlintTestdata, "*.yaml", SearchOption.AllDirectories))
            .ToArray();

        var files = allFiles.Where(static f =>
        {
            var n = f.Replace('\\', '/');
            return !n.Contains("/testdata/err/", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("/broken/", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("broken_yaml", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("dangling_alias", StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                _ = WorkflowParser.Parse(bytes, file);
            }
            catch (Exception ex)
            {
                failures.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Parse_CorpusSmoke_ActionlintBrokenFixtures_ContainParseFailures()
    {
        var root = FindRepoRoot();
        var actionlintTestdata = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata");
        if (!Directory.Exists(actionlintTestdata))
        {
            return;
        }

        var files = Directory.EnumerateFiles(actionlintTestdata, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(actionlintTestdata, "*.yaml", SearchOption.AllDirectories))
            .Where(static f =>
            {
                var n = f.Replace('\\', '/');
                return n.Contains("/testdata/err/", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("/broken/", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("broken_yaml", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("dangling_alias", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);

        var failedCount = 0;
        foreach (var file in files)
        {
            try
            {
                _ = WorkflowParser.Parse(File.ReadAllBytes(file), file);
            }
            catch
            {
                failedCount++;
            }
        }

        await Assert.That(failedCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_ActionlintErrFixtures_ExpectedDiagnosticsSubset()
    {
        var root = FindRepoRoot();
        var errRoot = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err");
        if (!Directory.Exists(errRoot))
        {
            return;
        }

        // Subset matching intentionally checks only stable, parser-owned message fragments.
        // This keeps the corpus test resilient to wording differences versus actionlint output.
        var expectations = new[]
        {
            new ErrFixtureExpectation("empty.yaml", ["workflow root must be mapping"]),
            new ErrFixtureExpectation("empty_on.yaml", ["unknown event in on"]),
            new ErrFixtureExpectation("case_sensitive_keys.yaml", ["unexpected workflow key", "unexpected job key"]),
            new ErrFixtureExpectation("duplicate_keys.yaml", ["contains duplicate key"]),
            new ErrFixtureExpectation("invalid_int_at_max_parallel.yaml", ["strategy.max-parallel must be integer"]),
            new ErrFixtureExpectation("invalid_steps.yaml", ["cannot have both run and uses", "requires run or uses"]),
        };

        var failures = new List<string>();
        foreach (var expectation in expectations)
        {
            AssertFixtureDiagnosticSubset(errRoot, expectation, failures);
        }

        await Assert.That(failures).IsEmpty();
    }

    private readonly record struct ErrFixtureExpectation(string FileName, string[] ExpectedSubstrings);

    private static void AssertFixtureDiagnosticSubset(
        string errRoot,
        ErrFixtureExpectation expectation,
        List<string> failures)
    {
        var path = Path.Combine(errRoot, expectation.FileName);
        if (!File.Exists(path))
        {
            failures.Add($"missing fixture: {expectation.FileName}");
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        for (var i = 0; i < expectation.ExpectedSubstrings.Length; i++)
        {
            var expected = expectation.ExpectedSubstrings[i];
            var found = result.Diagnostics.Any(d => d.Message.Contains(expected, StringComparison.Ordinal));
            if (found)
            {
                continue;
            }

            var observed = result.Diagnostics.Length == 0
                ? "<no diagnostics>"
                : string.Join(" | ", result.Diagnostics.Select(static d => d.Message));
            failures.Add($"{expectation.FileName}: expected diagnostic containing '{expected}' was not found. observed={observed}");
        }
    }

    [Test]
    public async Task Schema_Corpus_JsonFilesAreValid()
    {
        var root = FindRepoRoot();
        var schemaRoot = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema");
        var candidates = new[]
        {
            Path.Combine(schemaRoot, "ghalint", "json-schema", "ghalint.json"),
            Path.Combine(schemaRoot, "zizmor", "crates", "zizmor", "src", "data", "github-workflow.json"),
            Path.Combine(schemaRoot, "zizmor", "crates", "zizmor", "src", "data", "github-action.json"),
            Path.Combine(schemaRoot, "zizmor", "crates", "zizmor", "src", "data", "dependabot-2.0.json"),
            Path.Combine(schemaRoot, "local-workflow.json"),
        };

        var existing = candidates.Where(File.Exists).ToArray();
        await Assert.That(existing.Length).IsGreaterThan(0);

        var invalid = new List<string>();
        foreach (var path in existing)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                invalid.Add($"{path}: {ex.Message}");
            }
        }

        await Assert.That(invalid).IsEmpty();
    }

    [Test]
    public async Task Parse_JobMissingRunsOn_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                steps:
                    - run: echo hello
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-missing-runs-on.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires runs-on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobWithUsesAndSteps_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                steps:
                    - run: echo hello
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-uses-steps.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobAst_PopulatesBasicFields()
    {
        var yaml = """
        on: push
        jobs:
            build:
                name: Build
                runs-on: ubuntu-latest
                timeout-minutes: 30
                continue-on-error: false
                env:
                    FOO: bar
                outputs:
                    digest: sha256
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-ast-basic.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);
        var key = Utf8String.FromLowerAscii("build"u8);
        await Assert.That(result.Workflow.Jobs.ContainsKey(key)).IsTrue();
        var job = result.Workflow.Jobs[key];
        await Assert.That(job.Name is not null).IsTrue();
        await Assert.That(job.RunsOn is not null).IsTrue();
        await Assert.That(job.RunsOn!.Labels is not null).IsTrue();
        await Assert.That(job.RunsOn.Labels!.Count).IsEqualTo(1);
        await Assert.That(job.TimeoutMinutes is not null).IsTrue();
        await Assert.That(job.ContinueOnError is not null).IsTrue();
        await Assert.That(job.ContinueOnError!.Value).IsFalse();
        await Assert.That(job.Env is not null).IsTrue();
        await Assert.That(job.Outputs is not null).IsTrue();
        await Assert.That(job.Outputs!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ReusableWorkflowJob_PopulatesWorkflowCallAst()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                with:
                    target: prod
                secrets: inherit
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-ast-reuse.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("reuse"u8);
        await Assert.That(result.Workflow!.Jobs.ContainsKey(key)).IsTrue();
        var job = result.Workflow.Jobs[key];
        await Assert.That(job.WorkflowCall is not null).IsTrue();
        await Assert.That(job.WorkflowCall!.Uses.Value.Length).IsGreaterThan(0);
        await Assert.That(job.WorkflowCall.Inputs is not null).IsTrue();
        await Assert.That(job.WorkflowCall.Inputs!.Count).IsEqualTo(1);
        await Assert.That(job.WorkflowCall.InheritSecrets).IsTrue();
    }

    [Test]
    public async Task Parse_JobAst_StrategyContainerServices_PopulatesFields()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                strategy:
                    fail-fast: true
                    max-parallel: 2
                    matrix:
                        os: [ubuntu-latest, windows-latest]
                container:
                    image: node:20
                    options: --cpus 1
                    ports: [8080]
                    volumes: [/tmp:/tmp]
                    credentials:
                        username: user
                        password: pass
                services:
                    redis:
                        image: redis:7
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-ast-strategy-container-services.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs[key];
        await Assert.That(job.Strategy is not null).IsTrue();
        await Assert.That(job.Strategy!.FailFast is not null).IsTrue();
        await Assert.That(job.Strategy.MaxParallel is not null).IsTrue();
        await Assert.That(job.Strategy.Matrix is not null).IsTrue();
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Image.Value.Length).IsGreaterThan(0);
        await Assert.That(job.Container.Credentials is not null).IsTrue();
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.ServiceMap is not null).IsTrue();
        await Assert.That(job.Services.ServiceMap!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_StepWithoutRunOrUses_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: only-name
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-missing-run-uses.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires run or uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepWithRunAndUses_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hi
                      uses: actions/checkout@v4
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-uses.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("cannot have both run and uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepRun_PopulatesExecRunAst()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Run Step
                      run: echo ok
                      shell: bash
                      working-directory: src
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-ast.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs[jobKey].Steps![0];
        await Assert.That(step.Name is not null).IsTrue();
        await Assert.That(step.Exec).IsTypeOf<ExecRun>();
        var exec = (ExecRun)step.Exec;
        await Assert.That(exec.Run.Value.Length).IsGreaterThan(0);
        await Assert.That(exec.Shell is not null).IsTrue();
        await Assert.That(exec.WorkingDirectory is not null).IsTrue();
    }

    [Test]
    public async Task Parse_StepUses_PopulatesExecActionAst()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                        fetch-depth: '0'
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-uses-ast.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs[jobKey].Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();
        var exec = (ExecAction)step.Exec;
        await Assert.That(exec.Uses.Value.Length).IsGreaterThan(0);
        await Assert.That(exec.Inputs is not null).IsTrue();
        await Assert.That(exec.Inputs!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_StepDockerAction_PopulatesEntrypointAndArgsAst()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: docker://alpine:3.20
                      with:
                        entrypoint: /bin/sh
                        args: -c "echo ok"
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-docker-ast.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs[jobKey].Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();
        var exec = (ExecAction)step.Exec;
        await Assert.That(exec.Entrypoint is not null).IsTrue();
        await Assert.That(exec.Args is not null).IsTrue();
    }

    [Test]
    public async Task Parse_JobMustBeMapping_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-mapping.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("must be mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobStrategyMustBeMapping_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                strategy: []
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-strategy-shape.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("strategy must be mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobMatrixIncludeMustBeSequenceOrScalar_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                strategy:
                    matrix:
                        include: { os: ubuntu-latest }
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-matrix-include-shape.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("strategy.matrix.include must be sequence or scalar", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobContainerMissingImage_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    options: --cpus 1
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-container-image.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("container.image is required", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobServicesMustBeMapping_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                services: []
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-services-shape.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("services must be mapping", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ReusableWorkflowWithStepsOnlyKey_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                container: node:20
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-reuse-steps-only-key.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobWithWithoutUses_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                with:
                    node-version: '20'
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-without-uses-with.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key 'with' requires uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobSecretsScalarMustBeInherit_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                secrets: not-inherit
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-secrets-scalar.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("secrets scalar must be 'inherit'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobIf_WithStepOnlyContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                if: steps.prep.outputs.ok == 'true'
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-if-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepIf_UnknownFunction_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: unknownFn(github.ref)
                      run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-if-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobEnv_WithStepOnlyContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            env:
              BAD: ${{ steps.prep.outputs.ok }}
            steps:
              - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepRun_EmbeddedUnknownFunction_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ${{ unknownFn(github.ref) }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepWith_EmbeddedUnknownFunction_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/cache@v4
                with:
                  key: ${{ unknownFn(github.ref) }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-with-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepIf_FunctionTypeMismatch_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - if: contains(1, 'x')
                run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-if-type-mismatch.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("argument 1 should be string, but got number", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_OnScalar_PopulatesEventAst()
    {
        var yaml = """
        on: push
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-scalar.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        var evt = result.Workflow.On[0];
        await Assert.That(evt).IsTypeOf<WebhookEvent>();
        var webhook = (WebhookEvent)evt;
        await Assert.That(webhook.Hook.Value.Length).IsGreaterThan(0);
        await Assert.That(webhook.Types).IsNull();
        await Assert.That(webhook.Branches).IsNull();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_OnSequence_PopulatesEventAst()
    {
        var yaml = """
        on: [push, pull_request]
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-sequence.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(2);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WebhookEvent>();
        await Assert.That(result.Workflow.On[1]).IsTypeOf<WebhookEvent>();
        var first = (WebhookEvent)result.Workflow.On[0];
        var second = (WebhookEvent)result.Workflow.On[1];
        await Assert.That(first.Hook.Value.Length).IsGreaterThan(0);
        await Assert.That(second.Hook.Value.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_OnMappingWithFilters_PopulatesEventAst()
    {
        var yaml = """
        on:
            push:
                branches: [main]
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-mapping-filters.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WebhookEvent>();
        var webhook = (WebhookEvent)result.Workflow.On[0];
        await Assert.That(webhook.Branches is not null).IsTrue();
        await Assert.That(webhook.Branches!.Values.Count).IsEqualTo(1);
        await Assert.That(webhook.BranchesIgnore).IsNull();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parse_AstStructure_ComprehensiveWorkflow_PopulatesDeepNodes()
    {
        var yaml = """
        name: ci
        run-name: CI Run
        on:
            push:
                branches: [main]
                paths-ignore: [docs/**]
            schedule:
                - cron: '0 0 * * *'
                  timezone: 'UTC'
            workflow_dispatch:
                inputs:
                    target:
                        description: Deploy target
                        required: true
                        default: dev
                        type: choice
                        options: [dev, prod]
            workflow_call:
                inputs:
                    image:
                        required: true
                        type: string
                        default: alpine
                secrets:
                    token:
                        required: true
                outputs:
                    digest:
                        value: digest-sha
            repository_dispatch:
                types: [sync, deploy]
        permissions:
            contents: read
        env:
            GLOBAL: value
        defaults:
            run:
                shell: bash
                working-directory: src
        concurrency:
            group: ci-${{ github.ref }}
            cancel-in-progress: true
        jobs:
            build:
                name: Build Job
                needs: [prep]
                runs-on: ubuntu-latest
                environment:
                    name: production
                    url: https://example.com
                permissions:
                    contents: read
                concurrency:
                    group: build-${{ github.ref }}
                    cancel-in-progress: false
                strategy:
                    fail-fast: true
                    max-parallel: 2
                    matrix:
                        include:
                            - os: ubuntu-latest
                        exclude:
                            - os: windows-latest
                        os: [ubuntu-latest, windows-latest]
                container:
                    image: node:20
                    credentials:
                        username: user
                        password: pass
                    env:
                        INSIDE: "yes"
                    ports: ["8080"]
                    volumes: ["/tmp:/tmp"]
                    options: --cpus 1
                services:
                    redis:
                        image: redis:7
                outputs:
                    digest: sha256
                env:
                    JOB_ENV: job_value
                defaults:
                    run:
                        shell: pwsh
                        working-directory: app
                if: ${{ github.ref != '' }}
                timeout-minutes: 15
                continue-on-error: false
                steps:
                    - id: run1
                      name: Run Step
                      run: echo ok
                      shell: bash
                      working-directory: .
                      env:
                        STEP_ENV: step_value
                      continue-on-error: true
                      timeout-minutes: 5
                    - id: act1
                      if: ${{ success() }}
                      uses: actions/checkout@v4
                      with:
                        fetch-depth: '0'
                      env:
                        STEP2_ENV: value
            call:
                uses: owner/repo/.github/workflows/reuse.yml@main
                with:
                    target: prod
                secrets:
                    token: ghs_dummy_token
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "ast-comprehensive.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var workflow = result.Workflow!;

        await Assert.That(workflow.Name is not null).IsTrue();
        await Assert.That(workflow.RunName is not null).IsTrue();
        await Assert.That(workflow.Permissions is not null).IsTrue();
        await Assert.That(workflow.Env is not null).IsTrue();
        await Assert.That(workflow.Defaults is not null).IsTrue();
        await Assert.That(workflow.Concurrency is not null).IsTrue();

        await Assert.That(workflow.On.Count).IsEqualTo(5);
        await Assert.That(workflow.On.Any(static e => e is WebhookEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is ScheduledEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is WorkflowDispatchEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is WorkflowCallEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is RepositoryDispatchEvent)).IsTrue();

        var scheduled = (ScheduledEvent)workflow.On.First(static e => e is ScheduledEvent);
        await Assert.That(scheduled.Schedules.Count).IsEqualTo(1);
        await Assert.That(scheduled.Schedules[0].Cron is not null).IsTrue();
        await Assert.That(scheduled.Schedules[0].Timezone is not null).IsTrue();

        var dispatch = (WorkflowDispatchEvent)workflow.On.First(static e => e is WorkflowDispatchEvent);
        await Assert.That(dispatch.Inputs is not null).IsTrue();
        await Assert.That(dispatch.Inputs!.Count).IsEqualTo(1);
        var targetKey = Utf8String.FromLowerAscii("target"u8);
        await Assert.That(dispatch.Inputs.ContainsKey(targetKey)).IsTrue();
        await Assert.That(dispatch.Inputs[targetKey].Type).IsEqualTo(DispatchInputType.Choice);

        var callEvent = (WorkflowCallEvent)workflow.On.First(static e => e is WorkflowCallEvent);
        await Assert.That(callEvent.Inputs is not null).IsTrue();
        await Assert.That(callEvent.Inputs!.Count).IsEqualTo(1);
        await Assert.That(callEvent.Inputs[0].Type).IsEqualTo(WorkflowCallInputType.String);
        await Assert.That(callEvent.Secrets is not null).IsTrue();
        await Assert.That(callEvent.Secrets!.Count).IsEqualTo(1);
        await Assert.That(callEvent.Outputs is not null).IsTrue();
        await Assert.That(callEvent.Outputs!.Count).IsEqualTo(1);

        var buildKey = Utf8String.FromLowerAscii("build"u8);
        var callKey = Utf8String.FromLowerAscii("call"u8);
        await Assert.That(workflow.Jobs.ContainsKey(buildKey)).IsTrue();
        await Assert.That(workflow.Jobs.ContainsKey(callKey)).IsTrue();

        var buildJob = workflow.Jobs[buildKey];
        await Assert.That(buildJob.Needs is not null).IsTrue();
        await Assert.That(buildJob.RunsOn is not null).IsTrue();
        await Assert.That(buildJob.Environment is not null).IsTrue();
        await Assert.That(buildJob.Permissions is not null).IsTrue();
        await Assert.That(buildJob.Concurrency is not null).IsTrue();
        await Assert.That(buildJob.Outputs is not null).IsTrue();
        await Assert.That(buildJob.Env is not null).IsTrue();
        await Assert.That(buildJob.Defaults is not null).IsTrue();
        await Assert.That(buildJob.If is not null).IsTrue();
        await Assert.That(buildJob.TimeoutMinutes is not null).IsTrue();
        await Assert.That(buildJob.ContinueOnError is not null).IsTrue();
        await Assert.That(buildJob.Strategy is not null).IsTrue();
        await Assert.That(buildJob.Container is not null).IsTrue();
        await Assert.That(buildJob.Services is not null).IsTrue();
        await Assert.That(buildJob.Steps is not null).IsTrue();
        await Assert.That(buildJob.Steps!.Count).IsEqualTo(2);

        var runStep = buildJob.Steps[0];
        await Assert.That(runStep.Exec).IsTypeOf<ExecRun>();
        await Assert.That(runStep.Env is not null).IsTrue();
        await Assert.That(runStep.ContinueOnError is not null).IsTrue();
        await Assert.That(runStep.TimeoutMinutes is not null).IsTrue();

        var actionStep = buildJob.Steps[1];
        await Assert.That(actionStep.Exec).IsTypeOf<ExecAction>();
        var actionExec = (ExecAction)actionStep.Exec;
        await Assert.That(actionExec.Inputs is not null).IsTrue();
        await Assert.That(actionExec.Inputs!.Count).IsEqualTo(1);

        var callJob = workflow.Jobs[callKey];
        await Assert.That(callJob.WorkflowCall is not null).IsTrue();
        await Assert.That(callJob.WorkflowCall!.Inputs is not null).IsTrue();
        await Assert.That(callJob.WorkflowCall.Secrets is not null).IsTrue();
    }

    [Test]
    public async Task Parse_AstStructure_MatrixRawYamlKinds_PopulatesStringArrayObjectNodes()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                strategy:
                    matrix:
                        include:
                            - os: ubuntu-latest
                              meta:
                                distro: ubuntu
                                versions: [20, 22]
                        exclude:
                            - os: windows-latest
                        axis:
                            - plain
                            - { nested: [x, y] }
                            - [one, two]
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "ast-matrix-rawyaml.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();

        var buildKey = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs[buildKey];
        await Assert.That(job.Strategy is not null).IsTrue();
        await Assert.That(job.Strategy!.Matrix is not null).IsTrue();

        var matrix = job.Strategy.Matrix!;
        await Assert.That(matrix.Include is not null).IsTrue();
        await Assert.That(matrix.Exclude is not null).IsTrue();
        await Assert.That(matrix.Rows is not null).IsTrue();
        var axisRow = matrix.Rows!.Values.FirstOrDefault(static r => r.Values is not null && r.Values.Count == 3);
        await Assert.That(axisRow is not null).IsTrue();
        axisRow ??= new MatrixRow { Name = new StringNode { Value = default, Quoted = false, Range = default } };
        await Assert.That(axisRow.Values is not null).IsTrue();
        await Assert.That(axisRow.Values!.Count).IsEqualTo(3);
        await Assert.That(axisRow.Values[0]).IsTypeOf<RawYamlString>();
        await Assert.That(axisRow.Values[1]).IsTypeOf<RawYamlObject>();
        await Assert.That(axisRow.Values[2]).IsTypeOf<RawYamlArray>();

        await Assert.That(matrix.Include![0].Entries is not null).IsTrue();
        var includeEntries = matrix.Include[0].Entries!;
        await Assert.That(includeEntries.Count).IsEqualTo(1);
        var includeEntry = includeEntries[0];
        var metaKey = Utf8String.FromLowerAscii("meta"u8);
        await Assert.That(includeEntry.ContainsKey(metaKey)).IsTrue();
        await Assert.That(includeEntry[metaKey]).IsTypeOf<RawYamlObject>();

        var metaObject = (RawYamlObject)includeEntry[metaKey];
        var versionsKey = Utf8String.FromLowerAscii("versions"u8);
        await Assert.That(metaObject.Properties.ContainsKey(versionsKey)).IsTrue();
        await Assert.That(metaObject.Properties[versionsKey]).IsTypeOf<RawYamlArray>();
    }

    private static IEnumerable<string> EnumerateCorpusYamlFiles(string repoRoot)
    {
        var refsRoot = Path.Combine(repoRoot, ".references");
        var localCorpusRoot = Path.Combine(repoRoot, "tests", "Seiton.Core.Tests", "fixtures", "corpus");
        var candidates = new[]
        {
            Path.Combine(refsRoot, "actionlint-main", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint-main", ".github", "workflows"),
            Path.Combine(refsRoot, "zizmor-main", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint-main"),
            localCorpusRoot,
        };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories))
            {
                yield return file;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

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
}
