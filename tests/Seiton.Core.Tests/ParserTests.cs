using System.Text;
using Seiton.Core.Linting;
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

        var bytes = Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n").Replace("\n", "\r\n"));
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
    public async Task Parse_CrLfStepsWithBlockRun_PreservesScalarSlices()
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
        .Replace("\r\n", "\n")
        .Replace("\n", "\r\n");

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
    public async Task Parse_BlockRunDoesNotCaptureFollowingEnvOrNextStepIf()
    {
        var yaml = """
        name: ci
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: benchmark
                      run: |
                        dotnet run --filter "${FILTER}"
                        echo "result=success" >> "$GITHUB_OUTPUT"
                      env:
                        FILTER: benchmark
                    - name: report
                      run: |
                        echo first
                        echo second
                    - name: update
                      if: ${{ github.ref_name != '' }}
                      run: |
                        echo done
        """;

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "block-run-boundary.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();

        var job = result.Workflow!.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(job.Steps is not null).IsTrue();
        await Assert.That(job.Steps!.Count).IsEqualTo(3);

        var firstRun = Encoding.UTF8.GetString(((ExecRun)job.Steps[0].Exec).Run.Value.AsSpan(bytes));
        var secondRun = Encoding.UTF8.GetString(((ExecRun)job.Steps[1].Exec).Run.Value.AsSpan(bytes));
        var thirdRun = Encoding.UTF8.GetString(((ExecRun)job.Steps[2].Exec).Run.Value.AsSpan(bytes));

        await Assert.That(firstRun.Contains("env:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(firstRun.Contains("FILTER: benchmark", StringComparison.Ordinal)).IsFalse();
        await Assert.That(secondRun.Contains("if:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(secondRun.Contains("github.ref_name", StringComparison.Ordinal)).IsFalse();
        await Assert.That(firstRun.Contains("dotnet run --filter \"${FILTER}\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(firstRun.Contains("echo \"result=success\" >> \"$GITHUB_OUTPUT\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(secondRun.Contains("echo first", StringComparison.Ordinal)).IsTrue();
        await Assert.That(secondRun.Contains("echo second", StringComparison.Ordinal)).IsTrue();
        await Assert.That(thirdRun.Contains("echo done", StringComparison.Ordinal)).IsTrue();
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
    public async Task Parse_OnSpecialEventsScalarForm_PopulatesEmptyEvents_TableDriven()
    {
        // spec §3.4.1: workflow_dispatch / workflow_call / repository_dispatch in scalar form → empty typed event
        var cases = new (string EventName, Type ExpectedType)[]
        {
            ("workflow_dispatch", typeof(WorkflowDispatchEvent)),
            ("workflow_call", typeof(WorkflowCallEvent)),
            ("repository_dispatch", typeof(RepositoryDispatchEvent)),
            ("image_version", typeof(ImageVersionEvent)),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = $$"""
                on: {{c.EventName}}
                jobs: {}
                """.Replace("\r\n", "\n");

            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), $"on-scalar-{c.EventName}.yml");

            await Assert.That(result.Workflow is not null).IsTrue();
            await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result.Workflow.On[0].GetType()).IsEqualTo(c.ExpectedType);
            await Assert.That(result.Diagnostics).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_OnSpecialEventsEmptyMappingValue_PopulatesEmptyEvents_TableDriven()
    {
        // spec §3.4.1: empty mapping value (YAML null scalar) is treated as scalar-form event
        var cases = new (string EventName, Type ExpectedType)[]
        {
            ("workflow_dispatch", typeof(WorkflowDispatchEvent)),
            ("workflow_call", typeof(WorkflowCallEvent)),
            ("repository_dispatch", typeof(RepositoryDispatchEvent)),
            ("image_version", typeof(ImageVersionEvent)),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var yaml = $$"""
                on:
                    {{c.EventName}}:
                jobs: {}
                """.Replace("\r\n", "\n");

            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), $"on-empty-mapping-{c.EventName}.yml");

            await Assert.That(result.Workflow is not null).IsTrue();
            await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result.Workflow.On[0].GetType()).IsEqualTo(c.ExpectedType);
            await Assert.That(result.Diagnostics).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_OnScheduleScalarForm_ReportsError_TableDriven()
    {
        // spec §3.4.1: schedule in scalar / sequence-item form is an error (mapping required)
        var cases = new (string Name, string Yaml)[]
        {
            (
                "scalar form",
                """
                on: schedule
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
            (
                "sequence form",
                """
                on: [push, schedule]
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"on-schedule-scalar-{i}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.schedule must be mapping", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ServicesExpression_PopulatesAst()
    {
        // spec §3.17: services: ${{ ... }} is accepted as Services { Expression }
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                services: ${{ fromJson(inputs.services) }}
                steps: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "services-expression.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.Expression is not null).IsTrue();
        await Assert.That(job.Services.ServiceMap).IsNull();
    }

    [Test]
    public async Task Parse_CredentialsExpression_PopulatesAst()
    {
        // spec §3.18: credentials: ${{ ... }} is accepted as Credentials { Expression }
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: node:20
                    credentials: ${{ fromJson(secrets.creds) }}
                steps: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "credentials-expression.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Credentials is not null).IsTrue();
        await Assert.That(job.Container.Credentials!.Expression is not null).IsTrue();
        await Assert.That(job.Container.Credentials.Username).IsNull();
        await Assert.That(job.Container.Credentials.Password).IsNull();
    }

    [Test]
    public async Task Parse_ContainerEnvExpression_PopulatesAst()
    {
        // spec §2.8/§14: container env accepts expression form (${{ }})
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: node:20
                    env: ${{ fromJson(secrets.env_vars) }}
                steps: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "container-env-expression.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Env is not null).IsTrue();
        await Assert.That(job.Container.Env!.Expression is not null).IsTrue();
        await Assert.That(job.Container.Env.Vars).IsNull();
    }

    [Test]
    public async Task Parse_ServiceEnvExpression_PopulatesAst()
    {
        // spec §2.8/§14: service container env accepts expression form (${{ }})
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                services:
                    redis:
                        image: redis:7
                        env: ${{ github.sha }}
                steps: []
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "service-env-expression.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.ServiceMap is not null).IsTrue();
        await Assert.That(job.Services.ServiceMap!.Count).IsEqualTo(1);
        var redis = job.Services.ServiceMap.Values.First();
        await Assert.That(redis.Container.Env is not null).IsTrue();
        await Assert.That(redis.Container.Env!.Expression is not null).IsTrue();
        await Assert.That(redis.Container.Env.Vars).IsNull();
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
    public async Task Parse_OnWorkflowDispatch_ChoiceOptionsAllowEmptyString()
    {
        // spec §3.4.3: choice-type inputs legitimately use '' as a "no selection" option
        var yaml = """
        on:
            workflow_dispatch:
                inputs:
                    operation:
                        description: 'Optional operation'
                        required: false
                        type: choice
                        default: ''
                        options:
                            - ''
                            - 'disable'
                            - 'enable'
        jobs: {}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "dispatch-choice-empty.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var evt = (WorkflowDispatchEvent)result.Workflow!.On[0];
        var key = Utf8String.FromLowerAscii("operation"u8);
        var input = evt.Inputs![key];
        await Assert.That(input.Type).IsEqualTo(DispatchInputType.Choice);
        await Assert.That(input.Options!.Count).IsEqualTo(3);
        // no parse errors: '' is a valid choice option
        await Assert.That(result.Diagnostics).IsEmpty();
        // Empty-string option node must report the line of '' itself, not the next item.
        // This validates VYamlStreamAdapter's backward-scan fix for empty-scalar mark positions.
        var emptyOptionNode = input.Options![0];
        var disableOptionNode = input.Options![1];
        await Assert.That(emptyOptionNode.Range.StartLine).IsNotEqualTo(disableOptionNode.Range.StartLine);
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
    public async Task Parse_OnWorkflowCall_OutputValue_AllowsJobsContext()
    {
        var yaml = """
        on:
            workflow_call:
                outputs:
                    firstword:
                        value: ${{ jobs.reusable_workflow_job.outputs.output1 }}
        jobs:
            reusable_workflow_job:
                runs-on: ubuntu-latest
                outputs:
                    output1: ${{ steps.emit.outputs.value }}
                steps:
                    - id: emit
                      run: echo "value=ok" >> "$GITHUB_OUTPUT"
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "on-workflow-call-output-jobs-context.yml");
        var hasJobsUnavailable = result.Diagnostics.Any(static x =>
            x.Message.Contains("context 'jobs' is not available", StringComparison.Ordinal));

        await Assert.That(hasJobsUnavailable).IsFalse();
    }

    [Test]
    public async Task Parse_RequiredKeys_WorkflowCallAndSchedule_ReportsError_TableDriven()
    {
        var cases = new (string Name, string Yaml, string ExpectedMessagePart)[]
        {
            (
                "workflow_call input missing type",
                """
                on:
                    workflow_call:
                        inputs:
                            image:
                                required: true
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.workflow_call.inputs.image.type is required"
            ),
            (
                "workflow_call output missing value",
                """
                on:
                    workflow_call:
                        outputs:
                            digest:
                                description: output
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.workflow_call.outputs.digest.value is required"
            ),
            (
                "schedule item empty mapping",
                """
                on:
                    schedule:
                        - {}
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.schedule item requires cron"
            ),
            (
                "schedule item timezone only",
                """
                on:
                    schedule:
                        - timezone: UTC
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.schedule item requires cron"
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"required-keys-{i}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains(c.ExpectedMessagePart, StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Parse_DefaultsMissingRun_ReportsError_TableDriven()
    {
        var cases = new (string Name, string Yaml)[]
        {
            (
                "top-level defaults empty mapping",
                """
                on: push
                defaults: {}
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
            (
                "top-level defaults with unexpected key only",
                """
                on: push
                defaults:
                    foo: bar
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
            (
                "job-level defaults empty mapping",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        defaults: {}
                        steps: []
                """.Replace("\r\n", "\n")
            ),
            (
                "job-level defaults with unexpected key only",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        defaults:
                            foo: bar
                        steps: []
                """.Replace("\r\n", "\n")
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"defaults-missing-run-{i}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("defaults should have run", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ConcurrencyMissingGroup_ReportsError_TableDriven()
    {
        var cases = new (string Name, string Yaml)[]
        {
            (
                "top-level concurrency cancel-in-progress only",
                """
                on: push
                concurrency:
                    cancel-in-progress: true
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
            (
                "top-level concurrency empty mapping",
                """
                on: push
                concurrency: {}
                jobs: {}
                """.Replace("\r\n", "\n")
            ),
            (
                "job-level concurrency cancel-in-progress only",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        concurrency:
                            cancel-in-progress: false
                        steps: []
                """.Replace("\r\n", "\n")
            ),
            (
                "job-level concurrency empty mapping",
                """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        concurrency: {}
                        steps: []
                """.Replace("\r\n", "\n")
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"concurrency-missing-group-{i}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("concurrency.group is required", StringComparison.Ordinal))).IsTrue();
        }
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
    public async Task Parse_OnImageVersion_PopulatesEventAst_TableDriven()
    {
        var cases = new (string Name, string Yaml, int ExpectedNames, int ExpectedVersions)[]
        {
            (
                "names-only",
                """
                on:
                    image_version:
                        names: [my-image, other-image]
                jobs: {}
                """.Replace("\r\n", "\n"),
                2,
                0
            ),
            (
                "versions-only",
                """
                on:
                    image_version:
                        versions: [1.*, 2.*]
                jobs: {}
                """.Replace("\r\n", "\n"),
                0,
                2
            ),
            (
                "names-and-versions",
                """
                on:
                    image_version:
                        names: [my-image]
                        versions: [3.*]
                jobs: {}
                """.Replace("\r\n", "\n"),
                1,
                1
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"on-image-version-{c.Name}.yml");

            await Assert.That(result.Workflow is not null).IsTrue();
            await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
            await Assert.That(result.Workflow.On[0]).IsTypeOf<ImageVersionEvent>();
            var evt = (ImageVersionEvent)result.Workflow.On[0];
            await Assert.That(evt.Names?.Count ?? 0).IsEqualTo(c.ExpectedNames);
            await Assert.That(evt.Versions?.Count ?? 0).IsEqualTo(c.ExpectedVersions);
            await Assert.That(result.Diagnostics).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_OnImageVersion_InvalidForms_ReportDiagnostics_TableDriven()
    {
        var cases = new (string Name, string Yaml, string ExpectedDiagnostic)[]
        {
            (
                "non-mapping-event-config",
                """
                on:
                    image_version: true
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.image_version must be mapping"
            ),
            (
                "unknown-option",
                """
                on:
                    image_version:
                        entrypoint: [x]
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.image_version does not support option: entrypoint"
            ),
            (
                "names-must-be-sequence",
                """
                on:
                    image_version:
                        names: one
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.image_version.names must be sequence of scalar"
            ),
            (
                "versions-must-be-sequence",
                """
                on:
                    image_version:
                        versions:
                            foo: bar
                jobs: {}
                """.Replace("\r\n", "\n"),
                "on.image_version.versions must be sequence of scalar"
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var bytes = Encoding.UTF8.GetBytes(c.Yaml);
            var result = WorkflowParser.Parse(bytes, $"on-image-version-invalid-{c.Name}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains(c.ExpectedDiagnostic, StringComparison.Ordinal))).IsTrue();

            var lintResult = new LintEngine().Check(bytes, $"on-image-version-invalid-{c.Name}.yml");
            await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains(c.ExpectedDiagnostic, StringComparison.Ordinal))).IsTrue();
        }
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
    public async Task Parse_Fixture_ContextAvailability_KeyGranularity()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "corpus", "context-availability-key-granularity.yml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        var messages = result.Diagnostics.Select(static d => d.Message).ToArray();

        await Assert.That(messages.Any(static m => m.Contains("context 'steps' is not available in workflow expressions", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(static m => m.Contains("context 'env' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(static m => m.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(static m => m.Contains("context 'steps' is not available in step expressions", StringComparison.Ordinal))).IsFalse();
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
            // Keep dangling_alias out of the non-error smoke set: alias failures are
            // intentionally validated in dedicated error-fixture tests.
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
                // Includes dangling_alias on purpose: this bucket tracks malformed YAML
                // and parser-fatal inputs that must not be treated as successful parses.
                return n.Contains("/testdata/err/", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("/broken/", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("broken_yaml", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("dangling_alias", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);

        var problematicCount = 0;
        foreach (var file in files)
        {
            try
            {
                var result = WorkflowParser.Parse(File.ReadAllBytes(file), file);
                if (result.HasFatalError || result.Diagnostics.Length > 0)
                {
                    problematicCount++;
                }
            }
            catch
            {
                problematicCount++;
            }
        }

        await Assert.That(problematicCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_ActionlintOkFixtures_DoNotHaveFatalErrors()
    {
        var root = FindRepoRoot();
        var actionlintTestdata = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata");
        if (!Directory.Exists(actionlintTestdata))
        {
            return;
        }

        var candidateRoots = new[]
        {
            Path.Combine(actionlintTestdata, "ok"),
            Path.Combine(actionlintTestdata, "bench"),
            Path.Combine(actionlintTestdata, "reusable_workflow_metadata"),
        };

        var files = candidateRoots
            .Where(Directory.Exists)
            .SelectMany(static dir => Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)))
            .Where(static f =>
            {
                var n = f.Replace('\\', '/');
                return !n.Contains("/broken", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("broken_", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("/err/", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        await Assert.That(files.Length).IsGreaterThan(0);

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var result = WorkflowParser.Parse(File.ReadAllBytes(file), file);
                if (result.HasFatalError)
                {
                    failures.Add($"{file}: unexpected fatal parse error");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(failures).IsEmpty();
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
            new ErrFixtureExpectation("missing_on.yaml", ["required key 'on' is missing"]),
            new ErrFixtureExpectation("missing_jobs.yaml", ["required key 'jobs' is missing"]),
            new ErrFixtureExpectation("merge_key_unsupported.yaml", ["does not support merge key '<<'"]),
            new ErrFixtureExpectation("undefined_anchor.yaml", ["yaml parse failure"]),
            new ErrFixtureExpectation("recursive_anchors.yaml", ["must be mapping"]),
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
    public async Task Parse_ActionlintErrFixture_UndefinedAnchor_ReportsFatalYamlParseDiagnostic()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err", "undefined_anchor.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        await Assert.That(result.HasFatalError).IsTrue();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("yaml parse failure", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Parse_ActionlintErrFixture_RecursiveAnchors_ReportsDeterministicDiagnostics()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err", "recursive_anchors.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        await Assert.That(result.Diagnostics.Length).IsGreaterThan(0);
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("must be mapping", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Parse_ActionlintErrFixture_MergeKeyUnsupported_ReportsMergeKeyDiagnostics()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "err", "merge_key_unsupported.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("does not support merge key '<<'", StringComparison.Ordinal))).IsTrue();
    }

    // ── YAML anchor / alias tests ─────────────────────────────────────────────

    [Test]
    public async Task Parse_AnchorOnScalar_AliasedScalarResolved()
    {
        // &anchor on a scalar and *alias referencing it — basic scalar alias case.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: &runner ubuntu-latest
            steps:
              - uses: actions/checkout@v4
                with:
                  ref: *runner
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-scalar.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var step = result.Workflow!.Jobs.Values.First().Steps![0];
        var execAction = (ExecAction)step.Exec;
        // ref input value should be resolved to "ubuntu-latest"
        await Assert.That(execAction.Inputs).IsNotNull();
        var refValue = execAction.Inputs!.Values.First();
        await Assert.That(refValue.Value.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_AnchorOnSequence_AliasedSequenceResolved()
    {
        // &anchor on a sequence scalar alias — used in paths/paths-ignore filter.
        var yaml = """
        on:
          push:
            paths: &common_paths
              - src/**
              - tests/**
          pull_request:
            paths: *common_paths
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-sequence.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var events = result.Workflow!.On.OfType<WebhookEvent>().ToArray();
        await Assert.That(events.Length).IsEqualTo(2);
        var pushEvent = events[0];
        var prEvent = events[1];
        await Assert.That(pushEvent.Paths).IsNotNull();
        await Assert.That(prEvent.Paths).IsNotNull();
        await Assert.That(pushEvent.Paths!.Values.Count).IsEqualTo(prEvent.Paths!.Values.Count);
    }

    [Test]
    public async Task Parse_AnchorOnMapping_AliasedMappingResolved()
    {
        // &anchor on a mapping — step env mapping aliased and reused.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ${{ env.FOO }}
                env: &default_env
                  FOO: bar
              - run: echo ${{ env.FOO }}
                env: *default_env
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-mapping.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var steps = result.Workflow!.Jobs.Values.First().Steps!;
        await Assert.That(steps[0].Env).IsNotNull();
        await Assert.That(steps[1].Env).IsNotNull();
        // Both steps should have env vars from the anchor
        await Assert.That(steps[1].Env!.Vars!.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_AnchorOnStep_AliasedStepResolved()
    {
        // &anchor on a complete step mapping — aliased step is replayed correctly.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - &checkout
                uses: actions/checkout@v4
              - *checkout
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-step.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var steps = result.Workflow!.Jobs.Values.First().Steps!;
        await Assert.That(steps.Count).IsEqualTo(2);
        await Assert.That(((ExecAction)steps[0].Exec).Uses.Value.Length).IsGreaterThan(0);
        await Assert.That(((ExecAction)steps[1].Exec).Uses.Value.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_AnchorOnJob_AliasedJobResolved()
    {
        // &anchor on an entire job mapping — aliased job is replayed correctly.
        var yaml = """
        on: push
        jobs:
          base: &base_job
            runs-on: ubuntu-latest
            steps:
              - run: echo hello
          copy: *base_job
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-job.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(2);
        foreach (var job in result.Workflow.Jobs.Values)
        {
            await Assert.That(job.RunsOn).IsNotNull();
            await Assert.That(job.Steps).IsNotNull();
        }
    }

    [Test]
    public async Task Parse_AnchorOnEnv_AliasedEnvResolved()
    {
        // &anchor on a top-level env mapping — aliased in job env.
        var yaml = """
        on: push
        env: &global_env
          FOO: bar
          BAZ: qux
        jobs:
          build:
            runs-on: ubuntu-latest
            env: *global_env
            steps:
              - run: echo ${{ env.FOO }}
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-env.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var job = result.Workflow!.Jobs.Values.First();
        await Assert.That(job.Env).IsNotNull();
        await Assert.That(job.Env!.Vars!.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_AnchorOnIf_AliasedIfExpressionResolved()
    {
        // &anchor on an `if:` expression scalar — aliased across multiple steps.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo one
                if: &cond ${{ github.ref == 'refs/heads/main' }}
              - run: echo two
                if: *cond
        """;

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "anchor-if.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        var steps = result.Workflow!.Jobs.Values.First().Steps!;
        await Assert.That(steps[0].If).IsNotNull();
        await Assert.That(steps[1].If).IsNotNull();
    }

    [Test]
    public async Task Parse_AnchorActionlintOkFixture_NoDiagnostics()
    {
        // Full test using the actionlint anchors.yaml fixture — comprehensive anchor/alias coverage.
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata", "ok", "anchors.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        await Assert.That(result.HasFatalError).IsFalse();
        // anchors.yaml has expression diagnostics but no fatal parse errors
        await Assert.That(result.Diagnostics.Any(d => d.Message.StartsWith("yaml parse failure", StringComparison.OrdinalIgnoreCase))).IsFalse();
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

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-missing-runs-on.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("requires runs-on", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobMissingSteps_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-missing-steps.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("requires steps", StringComparison.Ordinal))).IsTrue();

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-missing-steps.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("requires steps", StringComparison.Ordinal))).IsTrue();
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

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-uses-steps.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("cannot have both uses and steps", StringComparison.Ordinal))).IsTrue();
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
    public async Task Parse_ReusableWorkflowCallSecrets_AllowsSecretsContext()
    {
        var yaml = """
        on: push
        jobs:
            call-workflow-passing-data:
                permissions:
                    contents: read
                uses: ./.github/workflows/_reusable-workflow-called.yaml
                with:
                    username: ${{ inputs.username }}
                    is-valid: ${{ inputs.is-valid }}
                secrets:
                    APPLES: ${{ secrets.APPLES }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "reusable-workflow-call-secrets-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'secrets' is not available", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_ReusableWorkflowCallSecrets_InvalidContext_ReportsExpressionLine()
    {
        var yaml = """
        on: push
        jobs:
            call-workflow-passing-data:
                permissions:
                    contents: read
                uses: ./.github/workflows/_reusable-workflow-called.yaml
                secrets:
                    APPLES: ${{ env.APPLES }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "reusable-workflow-call-secrets-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.Message.Contains("context 'env' is not available", StringComparison.Ordinal));
        var expectedLine = yaml.Split('\n')
            .Select((line, i) => (line, lineNumber: i + 1))
            .First(x => x.line.Contains("${{ env.APPLES }}", StringComparison.Ordinal))
            .lineNumber;

        await Assert.That(diagnostic.Location.StartLine).IsEqualTo(expectedLine);
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
    public async Task Parse_ContainerAndServiceImageRange_PointsToImageLine()
    {
        // Regression: image StringNode.Range must point to the image: value line,
        // not a subsequent line (e.g. ports: or steps:).
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-24.04
                container:
                    image: golang:1.25
                services:
                    redis:
                        image: redis:8
                        ports:
                            - 6379:6379
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var lines = yaml.Split('\n');

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "container-image-range.yml");

        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs[key];

        // container image line: "    image: golang:1.25" - find the actual line number
        var expectedContainerImageLine = -1;
        var expectedServiceImageLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("image: golang", StringComparison.Ordinal) && expectedContainerImageLine < 0)
            {
                expectedContainerImageLine = i + 1; // 1-based
            }
            else if (trimmed.StartsWith("image: redis", StringComparison.Ordinal))
            {
                expectedServiceImageLine = i + 1; // 1-based
            }
        }

        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Image.Range.StartLine).IsEqualTo(expectedContainerImageLine);

        await Assert.That(job.Services is not null).IsTrue();
        var redis = job.Services!.ServiceMap!.Values.First();
        await Assert.That(redis.Container.Image.Range.StartLine).IsEqualTo(expectedServiceImageLine);
    }

    [Test]
    public async Task Parse_JobRunsOnMapping_PopulatesRunnerGroupAndLabels()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on:
                    group: default
                    labels: [ubuntu-latest, x64]
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-runs-on-mapping.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var runner = result.Workflow!.Jobs[jobKey].RunsOn;
        await Assert.That(runner is not null).IsTrue();
        await Assert.That(runner!.Group is not null).IsTrue();
        await Assert.That(runner.Labels is not null).IsTrue();
        await Assert.That(runner.Labels!.Count).IsEqualTo(2);
        await Assert.That(runner.LabelsExpr).IsNull();
    }

    [Test]
    public async Task Parse_JobRunsOnExpression_PopulatesRunnerLabelsExpr()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ${{ github.ref }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-runs-on-expression.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var runner = result.Workflow!.Jobs[jobKey].RunsOn;
        await Assert.That(runner is not null).IsTrue();
        await Assert.That(runner!.LabelsExpr is not null).IsTrue();
        await Assert.That(runner.Labels).IsNull();
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
    public async Task Parse_StepUses_WithFlowStyleInputs_PreservesUsesScalar()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with: { fetch-depht: 1 }
        """
        .Replace("\r\n", "\n");

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "step-uses-flow-with.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs[jobKey].Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();

        var exec = (ExecAction)step.Exec;
        var uses = Encoding.UTF8.GetString(exec.Uses.Value.AsSpan(bytes));
        await Assert.That(uses).IsEqualTo("actions/checkout@v4");
        await Assert.That(exec.Inputs is not null).IsTrue();
        await Assert.That(exec.Inputs!.ContainsKey(Utf8String.FromLowerAscii("fetch-depht"u8))).IsTrue();
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
    public async Task Parse_JobTimeoutMinutes_NonPositive_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            zero:
                runs-on: ubuntu-latest
                timeout-minutes: 0
                steps:
                    - run: echo ok
            neg:
                runs-on: ubuntu-latest
                timeout-minutes: -1
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-timeout-non-positive.yml");
        var count = result.Diagnostics.Count(x => x.Message.Contains("timeout-minutes must be greater than 0", StringComparison.Ordinal));
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_StepTimeoutMinutes_NonPositive_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - timeout-minutes: 0
                      run: echo zero
                    - timeout-minutes: -1
                      run: echo neg
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-timeout-non-positive.yml");
        var count = result.Diagnostics.Count(x => x.Message.Contains("timeout-minutes must be greater than 0", StringComparison.Ordinal));
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_StrategyMaxParallel_NonPositive_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            zero:
                runs-on: ubuntu-latest
                strategy:
                    max-parallel: 0
                steps:
                    - run: echo zero
            neg:
                runs-on: ubuntu-latest
                strategy:
                    max-parallel: -1
                steps:
                    - run: echo neg
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "strategy-max-parallel-non-positive.yml");
        var count = result.Diagnostics.Count(x => x.Message.Contains("strategy.max-parallel must be greater than 0", StringComparison.Ordinal));
        await Assert.That(count).IsEqualTo(2);
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
    public async Task Parse_ReusableWorkflowForbiddenKeys_ReportsError_TableDriven()
    {
        static string BuildYaml(string body)
        {
            return (
                    "on: push\n"
                    + "jobs:\n"
                    + "  reuse:\n"
                    + "    uses: owner/repo/.github/workflows/reuse.yml@main\n"
                    + body
                    + "\n")
                    .Replace("\r\n", "\n");
        }

        var cases = new (string Name, string Body, string Key)[]
        {
            ("runs-on", "    runs-on: ubuntu-latest", "runs-on"),
            ("environment", "    environment: prod", "environment"),
            ("outputs", "    outputs:\n      digest: sha256", "outputs"),
            ("env", "    env:\n      FOO: bar", "env"),
            ("defaults", "    defaults:\n      run:\n        shell: bash", "defaults"),
            ("steps", "    steps:\n      - run: echo ok", "steps"),
            ("timeout-minutes", "    timeout-minutes: 5", "timeout-minutes"),
            ("continue-on-error", "    continue-on-error: true", "continue-on-error"),
            ("container", "    container: node:20", "container"),
        };

        foreach (var c in cases)
        {
            var yaml = BuildYaml(c.Body);
            var fileName = $"job-reuse-forbidden-{c.Name}.yml";

            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), fileName);
            if (!result.Diagnostics.Any(x => x.Message.Contains($"key '{c.Key}' is not allowed", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"parser case '{c.Name}' diagnostics: {string.Join(" | ", result.Diagnostics.Select(x => x.Message))}");
            }

            var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), fileName);
            if (!lintResult.Diagnostics.Any(x => x.Message.Contains($"key '{c.Key}' is not allowed", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"lint case '{c.Name}' diagnostics: {string.Join(" | ", lintResult.Diagnostics.Select(x => x.Message))}");
            }
        }
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

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-without-uses-with.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("key 'with' requires uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobSecretsWithoutUses_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                secrets: inherit
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-without-uses-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key 'secrets' requires uses", StringComparison.Ordinal))).IsTrue();

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-without-uses-secrets.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("key 'secrets' requires uses", StringComparison.Ordinal))).IsTrue();
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
        public async Task Parse_JobOutputs_WithStepsContext_DoesNotReportSemanticError()
        {
                var yaml = """
                on: push
                jobs:
                    build:
                        runs-on: ubuntu-latest
                        outputs:
                            output1: ${{ steps.step1.outputs.firstword }}
                            output2: ${{ steps.step2.outputs.secondword }}
                        steps:
                            - name: output step1
                                id: step1
                                run: echo "firstword=hello" >> "$GITHUB_OUTPUT"
                            - name: output step2
                                id: step2
                                run: echo "secondword=world" >> "$GITHUB_OUTPUT"
                """
                .Replace("\r\n", "\n");

                var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-outputs-steps-context.yml");
                await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsFalse();
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
    public async Task Parse_StepRun_FormatPlaceholderOutOfRange_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ${{ format('value-{1}', github.ref) }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-format-placeholder.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("format placeholder '{1}' requires argument 2, but got 1 format argument(s)", StringComparison.Ordinal))).IsTrue();
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
            image_version:
                names: [base-image]
                versions: [1.*]
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

        await Assert.That(workflow.On.Count).IsEqualTo(6);
        await Assert.That(workflow.On.Any(static e => e is WebhookEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is ScheduledEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is WorkflowDispatchEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is WorkflowCallEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is RepositoryDispatchEvent)).IsTrue();
        await Assert.That(workflow.On.Any(static e => e is ImageVersionEvent)).IsTrue();

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

        var imageVersionEvent = (ImageVersionEvent)workflow.On.First(static e => e is ImageVersionEvent);
        await Assert.That(imageVersionEvent.Names is not null).IsTrue();
        await Assert.That(imageVersionEvent.Names!.Count).IsEqualTo(1);
        await Assert.That(imageVersionEvent.Versions is not null).IsTrue();
        await Assert.That(imageVersionEvent.Versions!.Count).IsEqualTo(1);

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
    public async Task Parse_AstRanges_MajorNodesAreNonDefault()
    {
        var yaml = """
        name: ci
        on:
            push:
                branches: [main]
        permissions:
            contents: read
        env:
            GLOBAL: value
        defaults:
            run:
                shell: bash
        concurrency:
            group: ci-${{ github.ref }}
        jobs:
            build:
                runs-on: ubuntu-latest
                strategy:
                    matrix:
                        os: [ubuntu-latest]
                container:
                    image: node:20
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

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "ast-ranges.yml");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();

        static bool HasRange(TextRange range) => range.Length > 0;

        var workflow = result.Workflow!;
        await Assert.That(HasRange(workflow.Range)).IsTrue();
        await Assert.That(HasRange(workflow.On[0].Range)).IsTrue();
        await Assert.That(workflow.Permissions is not null).IsTrue();
        await Assert.That(HasRange(workflow.Permissions!.Range)).IsTrue();
        await Assert.That(workflow.Env is not null).IsTrue();
        await Assert.That(HasRange(workflow.Env!.Range)).IsTrue();
        await Assert.That(workflow.Defaults is not null).IsTrue();
        await Assert.That(HasRange(workflow.Defaults!.Range)).IsTrue();
        await Assert.That(HasRange(workflow.Defaults.Run.Range)).IsTrue();
        await Assert.That(workflow.Concurrency is not null).IsTrue();
        await Assert.That(HasRange(workflow.Concurrency!.Range)).IsTrue();

        var buildJob = workflow.Jobs[Utf8String.FromLowerAscii("build"u8)];
        await Assert.That(HasRange(buildJob.Range)).IsTrue();
        await Assert.That(buildJob.Strategy is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Strategy!.Range)).IsTrue();
        await Assert.That(buildJob.Strategy.Matrix is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Strategy.Matrix!.Range)).IsTrue();
        await Assert.That(buildJob.Container is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Container!.Range)).IsTrue();
        await Assert.That(buildJob.Container.Credentials is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Container.Credentials!.Range)).IsTrue();
        await Assert.That(buildJob.Services is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Services!.Range)).IsTrue();
        await Assert.That(buildJob.Steps is not null).IsTrue();
        await Assert.That(HasRange(buildJob.Steps![0].Range)).IsTrue();
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
        var actionlintFixtureRoot = Path.Combine(repoRoot, "tests", "Seiton.Core.Tests", "fixtures", "schema", "actionlint", "testdata");
        var candidates = new[]
        {
            Path.Combine(refsRoot, "actionlint", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint", ".github", "workflows"),
            Path.Combine(refsRoot, "zizmor", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint"),
            Path.Combine(actionlintFixtureRoot, "ok"),
            Path.Combine(actionlintFixtureRoot, "bench"),
            Path.Combine(actionlintFixtureRoot, "reusable_workflow_metadata"),
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
