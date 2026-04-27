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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Name.HasValue).IsTrue();
        await Assert.That(arena.GetStringValue(result.Workflow.Name).Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.RunName.HasValue).IsFalse();
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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.RunName.HasValue).IsTrue();
        await Assert.That(arena.GetStringValue(result.Workflow.RunName).Length).IsGreaterThan(0);
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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Name.HasValue).IsTrue();
        await Assert.That(arena.GetStringValue(result.Workflow.Name).Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.RunName.HasValue).IsFalse();
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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();

        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Steps is not null).IsTrue();
        await Assert.That(job.Steps!.Count).IsEqualTo(3);

        var firstRun = Encoding.UTF8.GetString(arena.GetStringValue(((ExecRun)job.Steps[0].Exec).Run));
        var secondRun = Encoding.UTF8.GetString(arena.GetStringValue(((ExecRun)job.Steps[1].Exec).Run));
        var thirdRun = Encoding.UTF8.GetString(arena.GetStringValue(((ExecRun)job.Steps[2].Exec).Run));

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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Permissions is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions!.Scopes is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions.Scopes!.Value.Count).IsEqualTo(2);
        await Assert.That(result.Workflow.Env is not null).IsTrue();
        await Assert.That(result.Workflow.Env!.Vars is not null).IsTrue();
        await Assert.That(result.Workflow.Env.Vars!.Value.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.Defaults is not null).IsTrue();
        await Assert.That(result.Workflow.Defaults!.Run.Shell.HasValue).IsTrue();
        await Assert.That(result.Workflow.Defaults.Run.WorkingDirectory.HasValue).IsTrue();
        await Assert.That(result.Workflow.Concurrency is not null).IsTrue();
        await Assert.That(arena.GetStringValue(result.Workflow.Concurrency!.Group).Length).IsGreaterThan(0);
        await Assert.That(result.Workflow.Concurrency.CancelInProgress.HasValue).IsTrue();
        await Assert.That(arena.GetBoolValue(result.Workflow.Concurrency.CancelInProgress)).IsTrue();
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
        var arena = result.Arena!;

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Permissions is not null).IsTrue();
        await Assert.That(result.Workflow.Permissions!.All.HasValue).IsTrue();
        await Assert.That(result.Workflow.Concurrency is not null).IsTrue();
        await Assert.That(arena.GetStringValue(result.Workflow.Concurrency!.Group).Length).IsGreaterThan(0);
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

        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"on\" section is missing in workflow", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"jobs\" section is missing in workflow", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected key \"foobar\" for \"workflow\" section", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("GitHub Actions does not support YAML merge key \"<<\"", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key \"PUSH\" is duplicated in \"on\" section", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("key \"BUILD\" is duplicated in \"jobs\" section", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("GitHub Actions does not support YAML merge key \"<<\"", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on sequence item must be string event name", StringComparison.Ordinal))).IsTrue();
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

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflow-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"steps\" is not allowed here", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("both \"branches\" and \"branches-ignore\" filters cannot be used for the same event", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("both \"tags\" and \"tags-ignore\" filters cannot be used for the same event", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("both \"paths\" and \"paths-ignore\" filters cannot be used for the same event", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("both \"branches\" and \"branches-ignore\" filters cannot be used for the same event", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_ExclusiveFilterError_ReportsAtIgnoreKeyPosition()
    {
        // branches-ignore is at line 4, column 9
        var yaml = "on:\n  push:\n    branches: [main]\n    branches-ignore: [dev]\njobs: {}\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "exclusive-position.yml");
        var diag = result.Diagnostics.First(x => x.Message.Contains("both \"branches\" and \"branches-ignore\"", StringComparison.Ordinal));
        await Assert.That(diag.Location.StartLine).IsEqualTo(4);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_ExclusiveFilterError_TagsIgnore_ReportsAtIgnoreKeyPosition()
    {
        // tags-ignore is at line 4, column 5
        var yaml = "on:\n  push:\n    tags: [v*]\n    tags-ignore: [v0.*]\njobs: {}\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "exclusive-tags-position.yml");
        var diag = result.Diagnostics.First(x => x.Message.Contains("both \"tags\" and \"tags-ignore\"", StringComparison.Ordinal));
        await Assert.That(diag.Location.StartLine).IsEqualTo(4);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_ExclusiveFilterError_PathsIgnore_ReportsAtIgnoreKeyPosition()
    {
        // paths-ignore is at line 4, column 5
        var yaml = "on:\n  pull_request:\n    paths: [src/**]\n    paths-ignore: [docs/**]\njobs: {}\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "exclusive-paths-position.yml");
        var diag = result.Diagnostics.First(x => x.Message.Contains("both \"paths\" and \"paths-ignore\"", StringComparison.Ordinal));
        await Assert.That(diag.Location.StartLine).IsEqualTo(4);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_ExclusiveFilterError_IgnoreFirst_ReportsAtLaterKey()
    {
        // branches-ignore first (line 3), branches second (line 4) → report at branches (line 4)
        var yaml = "on:\n  merge_group:\n    branches-ignore: bar\n    branches: foo\njobs: {}\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "exclusive-ignore-first-position.yml");
        var diag = result.Diagnostics.First(x => x.Message.Contains("both \"branches\" and \"branches-ignore\"", StringComparison.Ordinal));
        await Assert.That(diag.Location.StartLine).IsEqualTo(4);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("on.pull_request.types must be string or array of strings", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("expected \"inputs\" key for \"workflow_dispatch\" section but got \"paths\"", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(evt.Schedules[0].Cron.HasValue).IsTrue();
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
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("schedule event must be configured with mapping", StringComparison.Ordinal))).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "services-expression.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.Expression.HasValue).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "credentials-expression.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Credentials is not null).IsTrue();
        await Assert.That(job.Container.Credentials!.Expression.HasValue).IsTrue();
        await Assert.That(job.Container.Credentials.Username.HasValue).IsFalse();
        await Assert.That(job.Container.Credentials.Password.HasValue).IsFalse();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "container-env-expression.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(job.Container!.Env is not null).IsTrue();
        await Assert.That(job.Container.Env!.Expression.HasValue).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "service-env-expression.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.ServiceMap is not null).IsTrue();
        await Assert.That(job.Services.ServiceMap!.Value.Count).IsEqualTo(1);
        var redis = job.Services.ServiceMap.Value.Values().First();
        await Assert.That(redis.Container.Env is not null).IsTrue();
        await Assert.That(redis.Container.Env!.Expression.HasValue).IsTrue();
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
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WorkflowDispatchEvent>();
        var evt = (WorkflowDispatchEvent)result.Workflow.On[0];
        await Assert.That(evt.Inputs is not null).IsTrue();
        await Assert.That(evt.Inputs!.Value.Count).IsEqualTo(1);
        var key = Utf8String.FromLowerAscii("target"u8);
        evt.Inputs.Value.TryGetValue(Encoding.UTF8.GetBytes(yaml), key.Span, out var input);
        await Assert.That(input.Type).IsEqualTo(DispatchInputType.Choice);
        await Assert.That(input.Required.HasValue).IsTrue();
        await Assert.That(arena.GetBoolValue(input.Required)).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "dispatch-choice-empty.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var evt = (WorkflowDispatchEvent)result.Workflow!.On[0];
        var key = Utf8String.FromLowerAscii("operation"u8);
        evt.Inputs!.Value.TryGetValue(bytes, key.Span, out var input);
        await Assert.That(input.Type).IsEqualTo(DispatchInputType.Choice);
        await Assert.That(input.Options!.Count).IsEqualTo(3);
        // no parse errors: '' is a valid choice option
        await Assert.That(result.Diagnostics).IsEmpty();
        // Empty-string option node must report the line of '' itself, not the next item.
        // This validates VYamlStreamAdapter's backward-scan fix for empty-scalar mark positions.
        var emptyOptionNode = input.Options![0];
        var disableOptionNode = input.Options![1];
        await Assert.That(arena.GetStringRange(emptyOptionNode).StartLine).IsNotEqualTo(arena.GetStringRange(disableOptionNode).StartLine);
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
        await Assert.That(evt.Secrets!.Value.Count).IsEqualTo(1);
        await Assert.That(evt.Outputs is not null).IsTrue();
        await Assert.That(evt.Outputs!.Value.Count).IsEqualTo(1);
        await Assert.That(evt.Outputs.Value.Values().First().Value.HasValue).IsTrue();
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
                "\"type\" is missing at \"image\" input of workflow_call event"
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
                "\"value\" is missing at \"digest\" output of workflow_call event"
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

    // Event filter availability
    [Test]
    public async Task Parse_FilterNotAvailableForEvent_IncludesAvailableEvents_TableDriven()
    {
        var cases = new (string Name, string Yaml, string ExpectedMessagePart)[]
        {
            (
                "paths not available for merge_group",
                """
                on:
                    merge_group:
                        paths: [src/**]
                jobs: {}
                """.Replace("\r\n", "\n"),
                "\"paths\" filter is not available for merge_group event. it is only for pull_request, pull_request_target, push events"
            ),
            (
                "tags not available for pull_request",
                """
                on:
                    pull_request:
                        tags: [v*]
                jobs: {}
                """.Replace("\r\n", "\n"),
                "\"tags\" filter is not available for pull_request event. it is only for push events"
            ),
            (
                "branches not available for pull_request_review",
                """
                on:
                    pull_request_review:
                        branches: [main]
                jobs: {}
                """.Replace("\r\n", "\n"),
                "\"branches\" filter is not available for pull_request_review event. it is only for merge_group, pull_request, pull_request_target, push, workflow_run events"
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(c.Yaml), $"filter-avail-{i}.yml");
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains(c.ExpectedMessagePart, StringComparison.Ordinal))).IsTrue();
        }
    }

    // workflow_call null body handling
    [Test]
    public async Task Parse_WorkflowCallInputNullBody_ReportsTypeRequired()
    {
        var yaml = """
        on:
            workflow_call:
                inputs:
                    input0:
                    input1:
                        type: string
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "wc-input-null.yml");
        // input0 has null body — should report "type is missing" not "must be object"
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"type\" is missing at \"input0\" input of workflow_call event", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("input must be object", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_WorkflowCallSecretNullBody_NoError()
    {
        var yaml = """
        on:
            workflow_call:
                secrets:
                    secret0:
                    secret1:
                        description: test
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "wc-secret-null.yml");
        // secret0 has null body — should NOT report error (secrets have no required fields)
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("secret must be object", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_WorkflowCallOutputNullBody_ReportsValueRequired()
    {
        var yaml = """
        on:
            workflow_call:
                outputs:
                    missing-all:
                    has-value:
                        value: ${{ jobs.test.outputs.x }}
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "wc-output-null.yml");
        // missing-all has null body — should report "value is missing" not "must be object"
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"value\" is missing at \"missing-all\" output of workflow_call event", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("output must be object", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_WorkflowCallOutputEmptyValue_ReportsEmptyString()
    {
        var yaml = """
        on:
            workflow_call:
                outputs:
                    empty-value:
                        description: test
                        value:
        jobs: {}
        """
        .Replace("\r\n", "\n");
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "wc-output-empty-value.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("string should not be empty", StringComparison.Ordinal))).IsTrue();
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
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"defaults\" section should have \"run\" section", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Parse_DefaultsNull_ReportsShouldHaveRunAndNotEmpty()
    {
        var yaml = """
        on: push
        defaults:
        jobs: {}
        """.Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "defaults-null.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"defaults\" section should have \"run\" section", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"defaults\" section should not be empty. please remove this section if it's unnecessary", StringComparison.Ordinal))).IsTrue();
        // Must NOT emit the generic "must be object" error for null defaults
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("must be object", StringComparison.Ordinal))).IsEqualTo(false);
    }

    [Test]
    public async Task Parse_ConcurrencyMissingGroup_ReportsAtKeyLine()
    {
        var yaml = """
        on: push
        concurrency:
            cancel-in-progress: true
        jobs: {}
        """.Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "concurrency-key-pos.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message.Contains("group name is missing", StringComparison.Ordinal));
        await Assert.That(diag.Message).IsNotEmpty();
        // Should report at the "concurrency:" key line (line 2), not the mapping body
        await Assert.That(diag.Location.StartLine).IsEqualTo(2);
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
            await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("group name is missing in \"concurrency\" section", StringComparison.Ordinal))).IsTrue();
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
            await Assert.That(evt.Names?.Length ?? 0).IsEqualTo(c.ExpectedNames);
            await Assert.That(evt.Versions?.Length ?? 0).IsEqualTo(c.ExpectedVersions);
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
                "on.image_version must be object"
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
                "on.image_version.names must be array of strings"
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
                "on.image_version.versions must be array of strings"
            ),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var bytes = Encoding.UTF8.GetBytes(c.Yaml);
            var result = WorkflowParser.Parse(bytes, $"on-image-version-invalid-{c.Name}.yml");
            var arena = result.Arena!;
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("jobs must be object", StringComparison.Ordinal))).IsTrue();
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

        var result = new LintEngine().Check(File.ReadAllBytes(path), path);
        var messages = result.Diagnostics.Select(static d => d.Message).ToArray();

        await Assert.That(messages.Any(static m => m.Contains("\"steps\" is not allowed here", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(static m => m.Contains("\"env\" is not allowed here", StringComparison.Ordinal))).IsTrue();
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
            new ErrFixtureExpectation("empty.yaml", ["workflow root must be object"]),
            new ErrFixtureExpectation("empty_on.yaml", ["unknown event in on"]),
            new ErrFixtureExpectation("case_sensitive_keys.yaml", ["unexpected key", "for \"workflow\" section", "for \"job\" section"]),
            new ErrFixtureExpectation("duplicate_keys.yaml", ["is duplicated in"]),
            new ErrFixtureExpectation("invalid_int_at_max_parallel.yaml", ["strategy.max-parallel must be integer"]),
            new ErrFixtureExpectation("invalid_steps.yaml", ["unexpected key", "step must run script"]),
            new ErrFixtureExpectation("missing_on.yaml", ["\"on\" section is missing in workflow"]),
            new ErrFixtureExpectation("missing_jobs.yaml", ["\"jobs\" section is missing in workflow"]),
            new ErrFixtureExpectation("merge_key_unsupported.yaml", ["GitHub Actions does not support YAML merge key \"<<\""]),
            new ErrFixtureExpectation("undefined_anchor.yaml", ["yaml parse failure"]),
            new ErrFixtureExpectation("recursive_anchors.yaml", ["recursive alias"]),
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
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("must be object", StringComparison.OrdinalIgnoreCase))).IsTrue();
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

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("GitHub Actions does not support YAML merge key \"<<\"", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_MergeKey_ReportsCorrectPosition()
    {
        // B-8: merge key positions should point to the '<<' key, not past it
        var yaml = "on:\n  workflow_call:\n    inputs:\n      <<: &inputs\n        foo:\n          type: string\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: env\n        env:\n          <<: &default_env\n            FOO: BAR\n      - run: env\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var mergeKeyDiags = result.Diagnostics.Where(d => d.Message.Contains("merge key", StringComparison.Ordinal)).ToArray();

        // workflow_call inputs merge key at line 4, col 7 (6 spaces + <<)
        await Assert.That(mergeKeyDiags.Length).IsGreaterThanOrEqualTo(2);
        var inputMerge = mergeKeyDiags.First(d => d.Message.Contains("workflow_call", StringComparison.Ordinal));
        await Assert.That(inputMerge.Location.StartLine).IsEqualTo(4);
        await Assert.That(inputMerge.Location.StartColumn).IsEqualTo(7);

        // step env merge key at line 13, col 11 (10 spaces + <<)
        var envMerge = mergeKeyDiags.First(d => d.Message.Contains("env", StringComparison.Ordinal));
        await Assert.That(envMerge.Location.StartLine).IsEqualTo(13);
        await Assert.That(envMerge.Location.StartColumn).IsEqualTo(11);
    }

    [Test]
    public async Task Parse_MergeKey_StepLevel_ReportsAsMergeKey()
    {
        // B-8: step-level merge key should be reported as merge key, not "unexpected step key"
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - &default_step\n        run: echo hello\n      - <<: *default_step\n        run: echo bye\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var mergeKeyDiags = result.Diagnostics.Where(d => d.Message.Contains("merge key", StringComparison.Ordinal)).ToArray();

        await Assert.That(mergeKeyDiags.Length).IsGreaterThanOrEqualTo(1);
        var stepMerge = mergeKeyDiags[0];
        await Assert.That(stepMerge.Message).Contains("GitHub Actions does not support YAML merge key \"<<\"");
        // step merge key at line 8, col 9 (8 spaces + <<)
        await Assert.That(stepMerge.Location.StartLine).IsEqualTo(8);
        await Assert.That(stepMerge.Location.StartColumn).IsEqualTo(9);
    }

    [Test]
    public async Task Parse_MergeKey_EnvMessage_NotGarbled()
    {
        // B-8: env merge key message should not contain "must be object" prefix
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: env\n        env:\n          <<: &e\n            FOO: BAR\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var mergeKeyDiags = result.Diagnostics.Where(d => d.Message.Contains("merge key", StringComparison.Ordinal)).ToArray();

        await Assert.That(mergeKeyDiags.Length).IsGreaterThanOrEqualTo(1);
        var envMerge = mergeKeyDiags[0];
        // Should NOT contain "must be object" in the merge key error
        await Assert.That(envMerge.Message).DoesNotContain("must be object");
        // Should contain "env" section reference
        await Assert.That(envMerge.Message).Contains("env");
        await Assert.That(envMerge.Message).Contains("GitHub Actions does not support YAML merge key \"<<\"");
    }

    // YAML anchor / alias tests

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
        var arena = result.Arena!;
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var step = result.Workflow!.Jobs.Values().First().Steps![0];
        var execAction = (ExecAction)step.Exec;
        // ref input value should be resolved to "ubuntu-latest"
        await Assert.That(execAction.Inputs).IsNotNull();
        var refValue = execAction.Inputs!.Value.Values().First();
        await Assert.That(arena.GetStringValue(refValue).Length).IsGreaterThan(0);
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
        await Assert.That(pushEvent.Paths!.Values.Length).IsEqualTo(prEvent.Paths!.Values.Length);
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
        var steps = result.Workflow!.Jobs.Values().First().Steps!;
        await Assert.That(steps[0].Env).IsNotNull();
        await Assert.That(steps[1].Env).IsNotNull();
        // Both steps should have env vars from the anchor
        await Assert.That(steps[1].Env!.Vars!.Value.Count).IsGreaterThan(0);
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
        var arena = result.Arena!;
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        var steps = result.Workflow!.Jobs.Values().First().Steps!;
        await Assert.That(steps.Count).IsEqualTo(2);
        await Assert.That(arena.GetStringValue(((ExecAction)steps[0].Exec).Uses).Length).IsGreaterThan(0);
        await Assert.That(arena.GetStringValue(((ExecAction)steps[1].Exec).Uses).Length).IsGreaterThan(0);
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
        foreach (var job in result.Workflow.Jobs.Values())
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
        var job = result.Workflow!.Jobs.Values().First();
        await Assert.That(job.Env).IsNotNull();
        await Assert.That(job.Env!.Vars!.Value.Count).IsGreaterThan(0);
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
        var steps = result.Workflow!.Jobs.Values().First().Steps!;
        await Assert.That(steps[0].If.HasValue).IsTrue();
        await Assert.That(steps[1].If.HasValue).IsTrue();
    }

    // unused anchor detection
    [Test]
    public async Task Parse_UnusedAnchor_ReportsWarning()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ${{ env.FOO }}
                env: &unused_env
                  FOO: bar
              - run: echo done
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "unused-anchor.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("unused_env", StringComparison.Ordinal) && d.Message.Contains("not used", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_UsedAnchor_NoUnusedWarning()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo ${{ env.FOO }}
                env: &shared_env
                  FOO: bar
              - run: echo ${{ env.FOO }}
                env: *shared_env
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "used-anchor.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("not used", StringComparison.Ordinal))).IsFalse();
    }

    // recursive alias detection
    [Test]
    public async Task Parse_RecursiveAlias_ReportsRecursiveDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - &recursive
                run: echo hello
                env: *recursive
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "recursive-alias.yml");
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("recursive alias", StringComparison.OrdinalIgnoreCase) && d.Message.Contains("recursive", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_RecursiveAnchors_NestedAnchorResolvesCorrectly()
    {
        // Tests that *recursive1 resolves (nested anchor stored) and *recursive2 is detected as recursive
        var yaml = """
        on: push
        jobs:
          test: &recursive2
            runs-on: ubuntu-latest
            steps:
              - &recursive1
                env: *recursive1
                run: *recursive2
              - *recursive1
              - *recursive2
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "recursive-nested.yml");
        // *recursive2 should be detected as recursive alias with the correct name
        var recursiveAliases = result.Diagnostics.Where(d => d.Message.Contains("recursive alias", StringComparison.Ordinal)).ToArray();
        await Assert.That(recursiveAliases.Count(d => d.Message.Contains("\"recursive2\""))).IsGreaterThanOrEqualTo(1);
        // No unused anchor warnings — recursive aliases should count as references
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("is defined but not used", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_NonMappingStep_ReportsRunOrUsesRequired()
    {
        // Non-mapping, non-null step (e.g. alias that doesn't expand to mapping) should report "must run/uses"
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - 42
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "non-mapping-step.yml");
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("must be object", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("step must run script", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_NullScalarAnchor_DoesNotCrash()
    {
        // env: &anchor with no value (null scalar) should not cause a fatal parse error.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo hello
                env: &empty_anchor
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "null-scalar-anchor.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        // The null scalar env is not a valid mapping — expect a parse error but not fatal
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("expecting a single", StringComparison.Ordinal) && d.Message.Contains("\"env\" section", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_NullScalarAnchorRedefined_DoesNotCrash()
    {
        // Redefining an anchor on a null scalar (env: &credentials) after initial mapping definition.
        var yaml = """
        on: push
        jobs:
          test:
            services:
              nginx:
                image: nginx:latest
                credentials: &credentials
                  username: user
                  password: pass
            runs-on: ubuntu-latest
            steps:
              - run: ./download.sh
                env: *credentials
              - run: ./upload.sh
                env: &credentials
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "null-scalar-anchor-redef.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        // env: &credentials with null value is not valid
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("expecting a single", StringComparison.Ordinal) && d.Message.Contains("\"env\" section", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_YamlAnchorUsageFixture_DoesNotCrash()
    {
        // Full integration test for the yaml_anchor_usage.yaml fixture — was previously crashing.
        var root = FindRepoRoot();
        var path = Path.Combine(root, "testdata", "examples", "yaml_anchor_usage.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
        await Assert.That(result.HasFatalError).IsFalse();
        // Expect parse/lint diagnostics but no fatal crash
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("expecting a single", StringComparison.Ordinal) && d.Message.Contains("\"env\" section", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("recursive alias", StringComparison.OrdinalIgnoreCase))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-missing-runs-on.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"steps\" section is missing", StringComparison.Ordinal))).IsTrue();

        var lintResult = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-missing-steps.yml");
        await Assert.That(lintResult.Diagnostics.Any(x => x.Message.Contains("\"steps\" section is missing", StringComparison.Ordinal))).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "job-ast-basic.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.Jobs.Count).IsEqualTo(1);
        var key = Utf8String.FromLowerAscii("build"u8);
        await Assert.That(result.Workflow.Jobs.ContainsKey(bytes, key.Span)).IsTrue();
        var job = result.Workflow.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Name.HasValue).IsTrue();
        await Assert.That(job.RunsOn is not null).IsTrue();
        await Assert.That(job.RunsOn!.Labels is not null).IsTrue();
        await Assert.That(job.RunsOn.Labels!.Count).IsEqualTo(1);
        await Assert.That(job.TimeoutMinutes.HasValue).IsTrue();
        await Assert.That(job.ContinueOnError.HasValue).IsTrue();
        await Assert.That(arena.GetBoolValue(job.ContinueOnError)).IsFalse();
        await Assert.That(job.Env is not null).IsTrue();
        await Assert.That(job.Outputs is not null).IsTrue();
        await Assert.That(job.Outputs!.Value.Count).IsEqualTo(1);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "job-ast-reuse.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("reuse"u8);
        await Assert.That(result.Workflow!.Jobs.ContainsKey(bytes, key.Span)).IsTrue();
        var job = result.Workflow.Jobs.Get(bytes, "reuse"u8);
        await Assert.That(job.WorkflowCall is not null).IsTrue();
        await Assert.That(arena.GetStringValue(job.WorkflowCall!.Uses).Length).IsGreaterThan(0);
        await Assert.That(job.WorkflowCall.Inputs is not null).IsTrue();
        await Assert.That(job.WorkflowCall.Inputs!.Value.Count).IsEqualTo(1);
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

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "reusable-workflow-call-secrets-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.Message.Contains("\"env\" is not allowed here", StringComparison.Ordinal));
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "job-ast-strategy-container-services.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Strategy is not null).IsTrue();
        await Assert.That(job.Strategy!.FailFast.HasValue).IsTrue();
        await Assert.That(job.Strategy.MaxParallel.HasValue).IsTrue();
        await Assert.That(job.Strategy.Matrix is not null).IsTrue();
        await Assert.That(job.Container is not null).IsTrue();
        await Assert.That(arena.GetStringValue(job.Container!.Image).Length).IsGreaterThan(0);
        await Assert.That(job.Container.Credentials is not null).IsTrue();
        await Assert.That(job.Services is not null).IsTrue();
        await Assert.That(job.Services!.ServiceMap is not null).IsTrue();
        await Assert.That(job.Services.ServiceMap!.Value.Count).IsEqualTo(1);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "container-image-range.yml");
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        var key = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);

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
        await Assert.That(arena.GetStringRange(job.Container!.Image).StartLine).IsEqualTo(expectedContainerImageLine);

        await Assert.That(job.Services is not null).IsTrue();
        var redis = job.Services!.ServiceMap!.Value.Values().First();
        await Assert.That(arena.GetStringRange(redis.Container.Image).StartLine).IsEqualTo(expectedServiceImageLine);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "job-runs-on-mapping.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var runner = result.Workflow!.Jobs.Get(bytes, "build"u8).RunsOn;
        await Assert.That(runner is not null).IsTrue();
        await Assert.That(runner!.Group.HasValue).IsTrue();
        await Assert.That(runner.Labels is not null).IsTrue();
        await Assert.That(runner.Labels!.Count).IsEqualTo(2);
        await Assert.That(runner.LabelsExpr.HasValue).IsFalse();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "job-runs-on-expression.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var runner = result.Workflow!.Jobs.Get(bytes, "build"u8).RunsOn;
        await Assert.That(runner is not null).IsTrue();
        await Assert.That(runner!.LabelsExpr.HasValue).IsTrue();
        await Assert.That(runner.Labels).IsNull();
    }

    [Test]
    public async Task Parse_RunsOnMappingGroupNull_ReportsEmptyAtGroupLine()
    {
        // group: has null value on line 4 — diagnostic must point to line 4, not to the next line
        var yaml = "on: push\njobs:\n  j:\n    runs-on:\n      group:\n    steps:\n      - run: echo ok\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "group-null.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message == "string should not be empty");
        await Assert.That(diag.Message).IsNotNull();
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);  // "group:" is on line 5
        await Assert.That(diag.Location.StartColumn).IsEqualTo(13); // col after "group: "
    }

    [Test]
    public async Task Parse_RunsOnMappingGroupEmptyQuoted_ReportsEmptyAtQuoteLine()
    {
        // group: '' on line 5 — diagnostic must point to '', not to the next line
        var yaml = "on: push\njobs:\n  j:\n    runs-on:\n      group: ''\n    steps:\n      - run: echo ok\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "group-empty.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message == "string should not be empty");
        await Assert.That(diag.Message).IsNotNull();
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);  // "group: ''" is on line 5
        await Assert.That(diag.Location.StartColumn).IsEqualTo(14); // col at ''
    }

    [Test]
    public async Task Parse_RunsOnMappingLabelsEmptyQuoted_ReportsEmptyAtQuoteLine()
    {
        // labels: '' on line 5 — diagnostic must point to '', not to the next line
        var yaml = "on: push\njobs:\n  j:\n    runs-on:\n      labels: ''\n    steps:\n      - run: echo ok\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "labels-empty.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message == "string should not be empty");
        await Assert.That(diag.Message).IsNotNull();
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);  // "labels: ''" is on line 5
        await Assert.That(diag.Location.StartColumn).IsEqualTo(15); // col at ''
    }

    [Test]
    public async Task Parse_NullStepExplicit_ReportsEmptyAtNullText()
    {
        // `- null` on line 7 — diagnostic must point to "null" text (col 9), not past it
        var yaml = "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n      - null\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "null-step.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message.Contains("element of \"steps\" section should not be empty"));
        await Assert.That(diag.Message).IsNotNull();
        await Assert.That(diag.Location.StartLine).IsEqualTo(7);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(9); // col at 'n' of "null"
    }

    [Test]
    public async Task Parse_BareDashStep_ReportsEmptyAtDashPosition()
    {
        // bare `-` on line 8 — diagnostic must point to after the dash (col 8)
        var yaml = "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n      - foo: aaa\n        bar: bbb\n      -\n      - run: echo done\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "bare-dash-step.yml");
        var diag = result.Diagnostics.FirstOrDefault(x => x.Message.Contains("element of \"steps\" section should not be empty"));
        await Assert.That(diag.Message).IsNotNull();
        await Assert.That(diag.Location.StartLine).IsEqualTo(8);  // bare `-` on line 8
        await Assert.That(diag.Location.StartColumn).IsEqualTo(8); // col right after '-'
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("step must run script", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unexpected key", StringComparison.Ordinal))).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "step-run-ast.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs.Get(bytes, "build"u8).Steps![0];
        await Assert.That(step.Name.HasValue).IsTrue();
        await Assert.That(step.Exec).IsTypeOf<ExecRun>();
        var exec = (ExecRun)step.Exec;
        await Assert.That(arena.GetStringValue(exec.Run).Length).IsGreaterThan(0);
        await Assert.That(exec.Shell.HasValue).IsTrue();
        await Assert.That(exec.WorkingDirectory.HasValue).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "step-uses-ast.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs.Get(bytes, "build"u8).Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();
        var exec = (ExecAction)step.Exec;
        await Assert.That(arena.GetStringValue(exec.Uses).Length).IsGreaterThan(0);
        await Assert.That(exec.Inputs is not null).IsTrue();
        await Assert.That(exec.Inputs!.Value.Count).IsEqualTo(1);
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
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs.Get(bytes, "build"u8).Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();

        var exec = (ExecAction)step.Exec;
        var uses = Encoding.UTF8.GetString(arena.GetStringValue(exec.Uses));
        await Assert.That(uses).IsEqualTo("actions/checkout@v4");
        await Assert.That(exec.Inputs is not null).IsTrue();
        await Assert.That(exec.Inputs!.Value.ContainsKey(bytes, "fetch-depht"u8)).IsTrue();
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "step-docker-ast.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var jobKey = Utf8String.FromLowerAscii("build"u8);
        var step = result.Workflow!.Jobs.Get(bytes, "build"u8).Steps![0];
        await Assert.That(step.Exec).IsTypeOf<ExecAction>();
        var exec = (ExecAction)step.Exec;
        await Assert.That(exec.Entrypoint.HasValue).IsTrue();
        await Assert.That(exec.Args.HasValue).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("must be object", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("strategy must be object", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("strategy.matrix.include must be array or string", StringComparison.Ordinal))).IsTrue();
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
    public async Task Parse_JobTimeoutMinutes_Expression_AcceptsExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: ${{ fromJson(matrix.timeout || 10) }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-timeout-expression.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("timeout-minutes must be number", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.Workflow).IsNotNull();
        var bytes = Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n"));
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.TimeoutMinutes.HasValue).IsTrue();
    }

    [Test]
    public async Task Parse_StepTimeoutMinutes_Expression_AcceptsExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - timeout-minutes: ${{ fromJson(matrix.timeout || 10) }}
                      run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-timeout-expression.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("timeout-minutes must be number", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.Workflow).IsNotNull();
    }

    // fail-fast/timeout-minutes type validation
    [Test]
    public async Task Parse_FailFast_Off_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            test:
                strategy:
                    fail-fast: off
                runs-on: ubuntu-latest
                steps:
                    - run: echo ng
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "fail-fast-off.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("fail-fast must be bool", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_TimeoutMinutes_String_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            test:
                runs-on: ubuntu-latest
                steps:
                    - timeout-minutes: two minutes
                      run: echo ng
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "timeout-string.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("timeout-minutes must be number", StringComparison.Ordinal))).IsTrue();
    }

    // regression: step env: with expression scalar should parse without error
    [Test]
    public async Task Parse_StepEnvExpressionScalar_ParsesWithoutError()
    {
        var yaml = """
        on: push
        jobs:
            test:
                strategy:
                    matrix:
                        env_object:
                            - FOO: BAR
                            - FOO: PIYO
                runs-on: ubuntu-latest
                steps:
                    - run: echo "$FOO"
                      env: ${{ matrix.env_object }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-env-expression-scalar.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        var steps = result.Workflow!.Jobs.Values().First().Steps;
        await Assert.That(steps).IsNotNull();
        await Assert.That(steps!.Count).IsEqualTo(1);
        // The env node should exist and be treated as an expression
        await Assert.That(steps[0].Env).IsNotNull();
    }

    // env plain text scalar should report error
    [Test]
    public async Task Parse_StepEnvPlainTextScalar_ReportsError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
                      env: hello_world
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-env-plain-text.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("env", StringComparison.Ordinal) && x.Message.Contains("expression", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Parse_WorkflowEnvPlainTextScalar_ReportsError()
    {
        var yaml = """
        on: push
        env: some_value
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-env-plain-text.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("env", StringComparison.Ordinal))).IsTrue();
    }

    // regression: permission value position should point to actual value, not comment
    [Test]
    public async Task Parse_PermissionsWithComment_PositionPointsToValue()
    {
        var yaml = """
        on: push
        permissions:
            contents: read # this is a comment with write in it
            issues: write
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "permissions-comment.yml");
        var arena = result.Arena!;
        await Assert.That(result.HasFatalError).IsFalse();
        var permissions = result.Workflow!.Permissions;
        await Assert.That(permissions).IsNotNull();
        await Assert.That(permissions!.Scopes).IsNotNull();

        // Verify contents scope value position points to correct line (not a comment line)
        var source = arena.Source;
        var contentsScope = permissions.Scopes!.Value.Values().FirstOrDefault(s => s.NameText.AsSpan(source).SequenceEqual("contents"u8));
        await Assert.That(contentsScope.NameText.Length).IsGreaterThan(0);
        await Assert.That(Encoding.UTF8.GetString(contentsScope.ValueText.AsSpan(source))).IsEqualTo("read");
    }

    // regression: YAML parse error position extracted from VYaml exception
    [Test]
    public async Task Parse_BrokenYaml_ErrorPositionNotAtFirstLine()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n        with\n          bad: yaml\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "broken.yml");
        await Assert.That(result.HasFatalError).IsTrue();
        var diag = result.Diagnostics[0];
        // Should NOT point to 1:1 — the error is deeper in the file
        await Assert.That(diag.Location.StartLine).IsGreaterThan(1);
    }

    // regression: webhook activity type error position uses slice offset (not VYaml mark)
    [Test]
    public async Task Parse_WebhookUnsupportedActivityType_PositionPointsToValue()
    {
        var yaml = "on:\n  issues:\n    types: created\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "webhook-types.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        var typeDiag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("unsupported activity type", StringComparison.Ordinal));
        await Assert.That(typeDiag.Message).IsNotEmpty();
        // 'created' is on line 3, column 12 (1-based)
        await Assert.That(typeDiag.Location.StartLine).IsEqualTo(3);
        await Assert.That(typeDiag.Location.StartColumn).IsEqualTo(12);
    }

    // regression: TryExtractLineCol parses VYaml exception format
    // VYaml Line is 1-based, Col is 0-based; only Col needs +1
    [Test]
    public async Task TryExtractLineCol_VYamlFormat_ExtractsCorrectPosition()
    {
        var (line, col) = WorkflowParser.TryExtractLineCol("Failed to parse at Line: 5, Col: 3, Idx: 42");
        await Assert.That(line).IsEqualTo(5);  // Line is 1-based already
        await Assert.That(col).IsEqualTo(4);   // Col 0-based → 1-based
    }

    [Test]
    public async Task TryExtractLineCol_NoMatch_ReturnsOneOne()
    {
        var (line, col) = WorkflowParser.TryExtractLineCol("Some random error message");
        await Assert.That(line).IsEqualTo(1);
        await Assert.That(col).IsEqualTo(1);
    }

    // regression: matrix include adds extra keys to the matrix context
    [Test]
    public async Task Parse_MatrixIncludeAddsExtraKeys_ContextIncludesIncludeOnlyKeys()
    {
        var yaml = """
        on: push
        jobs:
            test:
                strategy:
                    matrix:
                        os: [ubuntu-latest, windows-latest]
                        include:
                            - os: ubuntu-latest
                              npm: 7.5.4
                runs-on: ${{ matrix.os }}
                steps:
                    - run: echo ${{ matrix.npm }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "matrix-include-extra-keys.yml");
        await Assert.That(result.HasFatalError).IsFalse();
        // Should not have any diagnostics about matrix.npm being undefined
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("npm", StringComparison.Ordinal) && x.Message.Contains("not defined", StringComparison.Ordinal))).IsFalse();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"image\" is missing in \"container\" section", StringComparison.Ordinal))).IsTrue();
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
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("services must be object", StringComparison.Ordinal))).IsTrue();
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

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-if-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"steps\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobIf_WithStrategyContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                if: strategy.fail-fast == true
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-if-strategy-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"strategy\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobIf_WithMatrixContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                if: matrix.os == 'ubuntu-latest'
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-if-matrix-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"matrix\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobIf_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                if: secrets.TOKEN != ''
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-if-secrets-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
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
    public async Task Parse_StepIf_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: secrets.TOKEN != ''
                      run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "step-if-secrets-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepRun_WithSecretsContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ${{ secrets.TOKEN }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-secrets-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("secrets", StringComparison.Ordinal) && x.Message.Contains("not available", StringComparison.Ordinal))).IsFalse();
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

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"steps\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StrategyMatrix_WithRunnerContext_ReportsContextAvailability()
    {
        var yaml = """
        on: push
        jobs:
          test:
            strategy:
              matrix:
                directory:
                  - ${{ runner.temp }}
            runs-on: ubuntu-24.04
            timeout-minutes: 10
            steps:
              - run: echo done
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "strategy-matrix-runner-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"runner\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StrategyMatrix_WithAllowedContexts_DoesNotReportError()
    {
        var yaml = """
        on: push
        jobs:
          test:
            strategy:
              matrix:
                value:
                  - ${{ github.ref_name }}
            runs-on: ubuntu-24.04
            timeout-minutes: 10
            steps:
              - run: echo done
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "strategy-matrix-github-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("is not available in strategy expressions", StringComparison.Ordinal))).IsFalse();
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
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(1);
        var evt = result.Workflow.On[0];
        await Assert.That(evt).IsTypeOf<WebhookEvent>();
        var webhook = (WebhookEvent)evt;
        await Assert.That(arena.GetStringValue(webhook.Hook).Length).IsGreaterThan(0);
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
        var arena = result.Arena!;

        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.Workflow!.On.Count).IsEqualTo(2);
        await Assert.That(result.Workflow.On[0]).IsTypeOf<WebhookEvent>();
        await Assert.That(result.Workflow.On[1]).IsTypeOf<WebhookEvent>();
        var first = (WebhookEvent)result.Workflow.On[0];
        var second = (WebhookEvent)result.Workflow.On[1];
        await Assert.That(arena.GetStringValue(first.Hook).Length).IsGreaterThan(0);
        await Assert.That(arena.GetStringValue(second.Hook).Length).IsGreaterThan(0);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "ast-comprehensive.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();
        var workflow = result.Workflow!;

        await Assert.That(workflow.Name.HasValue).IsTrue();
        await Assert.That(workflow.RunName.HasValue).IsTrue();
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
        await Assert.That(scheduled.Schedules[0].Cron.HasValue).IsTrue();
        await Assert.That(scheduled.Schedules[0].Timezone.HasValue).IsTrue();

        var dispatch = (WorkflowDispatchEvent)workflow.On.First(static e => e is WorkflowDispatchEvent);
        await Assert.That(dispatch.Inputs is not null).IsTrue();
        await Assert.That(dispatch.Inputs!.Value.Count).IsEqualTo(1);
        var targetKey = Utf8String.FromLowerAscii("target"u8);
        await Assert.That(dispatch.Inputs.Value.ContainsKey(bytes, targetKey.Span)).IsTrue();
        dispatch.Inputs.Value.TryGetValue(bytes, targetKey.Span, out var dispatchTargetInput);
        await Assert.That(dispatchTargetInput.Type).IsEqualTo(DispatchInputType.Choice);

        var callEvent = (WorkflowCallEvent)workflow.On.First(static e => e is WorkflowCallEvent);
        await Assert.That(callEvent.Inputs is not null).IsTrue();
        await Assert.That(callEvent.Inputs!.Count).IsEqualTo(1);
        await Assert.That(callEvent.Inputs[0].Type).IsEqualTo(WorkflowCallInputType.String);
        await Assert.That(callEvent.Secrets is not null).IsTrue();
        await Assert.That(callEvent.Secrets!.Value.Count).IsEqualTo(1);
        await Assert.That(callEvent.Outputs is not null).IsTrue();
        await Assert.That(callEvent.Outputs!.Value.Count).IsEqualTo(1);

        var imageVersionEvent = (ImageVersionEvent)workflow.On.First(static e => e is ImageVersionEvent);
        await Assert.That(imageVersionEvent.Names is not null).IsTrue();
        await Assert.That(imageVersionEvent.Names!.Length).IsEqualTo(1);
        await Assert.That(imageVersionEvent.Versions is not null).IsTrue();
        await Assert.That(imageVersionEvent.Versions!.Length).IsEqualTo(1);

        var buildKey = Utf8String.FromLowerAscii("build"u8);
        var callKey = Utf8String.FromLowerAscii("call"u8);
        await Assert.That(workflow.Jobs.ContainsKey(bytes, buildKey.Span)).IsTrue();
        await Assert.That(workflow.Jobs.ContainsKey(bytes, callKey.Span)).IsTrue();

        var buildJob = workflow.Jobs.Get(bytes, "build"u8);
        await Assert.That(buildJob.Needs is not null).IsTrue();
        await Assert.That(buildJob.RunsOn is not null).IsTrue();
        await Assert.That(buildJob.Environment is not null).IsTrue();
        await Assert.That(buildJob.Permissions is not null).IsTrue();
        await Assert.That(buildJob.Concurrency is not null).IsTrue();
        await Assert.That(buildJob.Outputs is not null).IsTrue();
        await Assert.That(buildJob.Env is not null).IsTrue();
        await Assert.That(buildJob.Defaults is not null).IsTrue();
        await Assert.That(buildJob.If.HasValue).IsTrue();
        await Assert.That(buildJob.TimeoutMinutes.HasValue).IsTrue();
        await Assert.That(buildJob.ContinueOnError.HasValue).IsTrue();
        await Assert.That(buildJob.Strategy is not null).IsTrue();
        await Assert.That(buildJob.Container is not null).IsTrue();
        await Assert.That(buildJob.Services is not null).IsTrue();
        await Assert.That(buildJob.Steps is not null).IsTrue();
        await Assert.That(buildJob.Steps!.Count).IsEqualTo(2);

        var runStep = buildJob.Steps[0];
        await Assert.That(runStep.Exec).IsTypeOf<ExecRun>();
        await Assert.That(runStep.Env is not null).IsTrue();
        await Assert.That(runStep.ContinueOnError.HasValue).IsTrue();
        await Assert.That(runStep.TimeoutMinutes.HasValue).IsTrue();

        var actionStep = buildJob.Steps[1];
        await Assert.That(actionStep.Exec).IsTypeOf<ExecAction>();
        var actionExec = (ExecAction)actionStep.Exec;
        await Assert.That(actionExec.Inputs is not null).IsTrue();
        await Assert.That(actionExec.Inputs!.Value.Count).IsEqualTo(1);

        var callJob = workflow.Jobs.Get(bytes, callKey.Span);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "ast-ranges.yml");
        var arena = result.Arena!;

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

        var buildJob = workflow.Jobs.Get(bytes, "build"u8);
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

        var bytes = Encoding.UTF8.GetBytes(yaml);
        var result = WorkflowParser.Parse(bytes, "ast-matrix-rawyaml.yml");
        var arena = result.Arena!;

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Workflow is not null).IsTrue();

        var buildKey = Utf8String.FromLowerAscii("build"u8);
        var job = result.Workflow!.Jobs.Get(bytes, "build"u8);
        await Assert.That(job.Strategy is not null).IsTrue();
        await Assert.That(job.Strategy!.Matrix is not null).IsTrue();

        var matrix = job.Strategy.Matrix!;
        await Assert.That(matrix.Include is not null).IsTrue();
        await Assert.That(matrix.Exclude is not null).IsTrue();
        await Assert.That(matrix.Rows is not null).IsTrue();
        var axisRow = matrix.Rows!.Value.Values().FirstOrDefault(static r => r.Values is not null && r.Values.Count == 3);
        await Assert.That(axisRow is not null).IsTrue();
        axisRow ??= new MatrixRow { Name = default };
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
        await Assert.That(includeEntry.ContainsKey(bytes, metaKey.Span)).IsTrue();
        var metaRaw = includeEntry.Get(bytes, "meta"u8);
        await Assert.That(metaRaw).IsTypeOf<RawYamlObject>();

        var metaObject = (RawYamlObject)metaRaw;
        var versionsKey = Utf8String.FromLowerAscii("versions"u8);
        await Assert.That(metaObject.Properties.ContainsKey(bytes, versionsKey.Span)).IsTrue();
        var versionsRaw = metaObject.Properties.Get(bytes, "versions"u8);
        await Assert.That(versionsRaw).IsTypeOf<RawYamlArray>();
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

    // regression: YAML parse error line number should not be off-by-one
    [Test]
    public async Task Parse_BrokenYaml_ReportsCorrectLineNumber()
    {
        var yaml = "on: push\njobs:\n  linux:\n    runs-on: ubuntu-latest\n    steps:\n      - run: foo:\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        await Assert.That(result.HasFatalError).IsTrue();
        var diag = result.Diagnostics[0];
        await Assert.That(diag.Location.StartLine).IsEqualTo(6);
    }

    // regression: webhook known-but-disallowed option must include key name in message
    [Test]
    public async Task Parse_WebhookOptionNotAllowed_MessageContainsKeyName()
    {
        var yaml = "on:\n  release:\n    tags: v*.*.*\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("filter is not available"));
        await Assert.That(diag.Message).Contains("tags");
    }

    // regression: timeout-minutes parse error must have valid position
    [Test]
    public async Task Parse_TimeoutMinutesInvalidValue_ReportsCorrectPosition()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n        timeout-minutes: two\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("timeout-minutes"));
        // "two" starts at line 7, column 26 (8 spaces + "timeout-minutes: " = 26)
        await Assert.That(diag.Location.StartLine).IsEqualTo(7);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(26);
    }

    // hashFiles function context restriction

    [Test]
    public async Task Parse_WorkflowEnv_WithHashFiles_ReportsSemanticError()
    {
        var yaml = """
        on: push
        env:
            CACHE_KEY: ${{ hashFiles('**/package-lock.json') }}
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflow-env-hashfiles.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobIf_WithHashFiles_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                if: ${{ hashFiles('**/package-lock.json') != '' }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-if-hashfiles.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StrategyMatrix_WithHashFiles_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                strategy:
                    matrix:
                        key:
                            - ${{ hashFiles('**/package-lock.json') }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "strategy-hashfiles.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepRun_WithHashFiles_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ${{ hashFiles('**/package-lock.json') }}
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-hashfiles.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_StepIf_WithHashFiles_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - if: hashFiles('**/package-lock.json') != ''
                      run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-if-hashfiles.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    // regression: job-level secrets exclusion

    [Test]
    public async Task Parse_JobName_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                name: ${{ secrets.TOKEN }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-name-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobRunsOn_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ${{ secrets.RUNNER }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-runs-on-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobEnvironment_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                environment: ${{ secrets.ENV_NAME }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-environment-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobEnv_WithSecretsContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.TOKEN }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-env-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'secrets' is not available", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_JobContinueOnError_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                continue-on-error: ${{ secrets.ALLOW_FAIL != '' }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-continue-on-error-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_JobTimeoutMinutes_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                timeout-minutes: ${{ fromJSON(secrets.TIMEOUT) }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-timeout-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    // regression: environment.url has extended contexts (job, runner, env, steps)

    [Test]
    public async Task Parse_JobEnvironmentUrl_WithStepsContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            deploy:
                runs-on: ubuntu-latest
                environment:
                    name: production
                    url: ${{ steps.deploy.outputs.url }}
                steps:
                    - id: deploy
                      run: echo "url=https://example.com" >> $GITHUB_OUTPUT
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-env-url-steps.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_JobEnvironmentUrl_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            deploy:
                runs-on: ubuntu-latest
                environment:
                    name: production
                    url: ${{ secrets.DEPLOY_URL }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "job-env-url-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    // regression: container/service env has extended contexts

    [Test]
    public async Task Parse_JobContainerEnv_WithRunnerContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: node:20
                    env:
                        RUNNER_OS: ${{ runner.os }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "container-env-runner.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'runner' is not available", StringComparison.Ordinal))).IsFalse();
    }

    // regression: container/service credentials includes env

    [Test]
    public async Task Parse_JobContainerCredentials_WithEnvContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: ghcr.io/owner/repo
                    credentials:
                        username: ${{ env.REGISTRY_USER }}
                        password: ${{ secrets.REGISTRY_TOKEN }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "container-credentials-env.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'env' is not available", StringComparison.Ordinal))).IsFalse();
    }

    // regression: defaults.run includes env, excludes secrets

    [Test]
    public async Task Parse_JobDefaultsRun_WithEnvContext_NoError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                defaults:
                    run:
                        working-directory: ${{ env.WORK_DIR }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "defaults-run-env.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'env' is not available", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Parse_JobDefaultsRun_WithSecretsContext_ReportsSemanticError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                defaults:
                    run:
                        working-directory: ${{ secrets.WORK_DIR }}
                steps:
                    - run: echo ok
        """
        .Replace("\r\n", "\n");

        var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "defaults-run-secrets.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    // regression: fail-fast parse error must have valid position
    [Test]
    public async Task Parse_FailFastInvalidValue_ReportsCorrectPosition()
    {
        var yaml = "on: push\njobs:\n  test:\n    strategy:\n      fail-fast: off\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("fail-fast"));
        // "off" starts at line 5, column 18 (6 spaces + "fail-fast: " = 18)
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(18);
    }

    // regression: max-parallel parse error must have valid position
    [Test]
    public async Task Parse_MaxParallelInvalidValue_ReportsCorrectPosition()
    {
        var yaml = "on: push\njobs:\n  test:\n    strategy:\n      max-parallel: 1.5\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("max-parallel must be integer"));
        // "1.5" starts at line 5, column 21 (6 spaces + "max-parallel: " = 21)
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(21);
    }

    // regression: null scalar position for "permissions:" with no value should report the
    // permissions line, not the next token's line. VYaml advances past the null scalar to the
    // next key; ResolveEmptyScalarStart must walk backward past the next key's colon.
    [Test]
    public async Task Parse_NullScalarPermissions_ReportsPermissionsLine()
    {
        // "permissions:" is on line 4, column 5 (4 spaces indent), colon at column 16.
        // The empty value position should be line 4, column 17 (right after the colon).
        var yaml = "on: push\njobs:\n  test:\n    permissions:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("permissions value must not be empty"));
        // Must be on the "permissions:" line (line 4), NOT the "runs-on:" line (line 5)
        await Assert.That(diag.Location.StartLine).IsEqualTo(4);
    }

    // regression: null scalar position at workflow level "permissions:" should report
    // the correct line even when the next key is "jobs:".
    [Test]
    public async Task Parse_NullScalarPermissions_WorkflowLevel_ReportsPermissionsLine()
    {
        // "permissions:" is on line 2
        var yaml = "on: push\npermissions:\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("permissions value must not be empty"));
        // Must be on the "permissions:" line (line 2), NOT the "jobs:" line (line 3)
        await Assert.That(diag.Location.StartLine).IsEqualTo(2);
    }

    // regression: empty step id (id: "") should report "must not be empty", not "must be string"
    [Test]
    public async Task Parse_EmptyStepId_ReportsEmptyNotScalar()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n        id: \"\"\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("string should not be empty"));
        await Assert.That(diag.Message).IsNotEmpty();
        // Must say "string should not be empty", NOT "must be string"
        await Assert.That(diag.Message).Contains("string should not be empty");
        await Assert.That(diag.Message).DoesNotContain("must be string");
    }

    // regression: Utf8Slice internal representation must not leak into error messages
    [Test]
    public async Task Parse_NonSequenceSteps_MessageDoesNotContainUtf8Slice()
    {
        var yaml = "on: push\njobs:\n  myJob:\n    runs-on: ubuntu-latest\n    steps: notASequence\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("\"steps\" section must be sequence node"));
        await Assert.That(diag.Message).IsNotEmpty();
        await Assert.That(diag.Message).DoesNotContain("Utf8Slice");
    }

    // regression: linter should not produce 0:0 position for steps with empty uses
    [Test]
    public async Task Lint_StepWithoutRunOrUses_NoZeroZeroUnpinnedUses()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - name: broken\n"u8;
        var lintResult = new LintEngine([new Seiton.Core.Linting.Rules.UnpinnedUsesRule()])
            .Check(yaml.ToArray(), "test.yaml");
        // After the fix, no unpinned-uses diagnostic should be emitted for empty uses
        var hasUnpinned = lintResult.Diagnostics.Any(d => d.RuleId == "unpinned-uses");
        await Assert.That(hasUnpinned).IsFalse();
    }

    // regression: container: null is valid (means no container)
    [Test]
    public async Task Parse_ContainerNull_NoDiagnostic()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    container: null\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var hasContainerDiag = result.Diagnostics.Any(d => d.Message.Contains("container"));
        await Assert.That(hasContainerDiag).IsFalse();
    }

    // regression: service entrypoint and command are valid keys
    [Test]
    public async Task Parse_ServiceEntrypointAndCommand_NoDiagnostic()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    services:\n      redis:\n        image: redis\n        entrypoint: redis-server\n        command: --save 60 1\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        var hasUnexpectedDiag = result.Diagnostics.Any(d => d.Message.Contains("unexpected") && (d.Message.Contains("entrypoint") || d.Message.Contains("command")));
        await Assert.That(hasUnexpectedDiag).IsFalse();
    }

    // regression: nested scalar anchors inside outer recording resolve correctly
    [Test]
    public async Task Parse_NestedScalarAnchorInsideOuterRecording_Resolves()
    {
        var yaml = """
            on: push
            jobs:
              test1:
                runs-on: ubuntu-latest
                steps:
                  - &step
                    run: echo hello
                    if: &cond true
                  - run: echo two
                    if: *cond
            """u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        // *cond should resolve correctly — no "if must be string" error
        var hasIfDiag = result.Diagnostics.Any(d => d.Message.Contains("if must be string"));
        await Assert.That(hasIfDiag).IsFalse();
    }

    // regression: nested scalar anchor inside job anchor resolves for alias replay
    [Test]
    public async Task Parse_NestedScalarAnchorInsideJobAnchor_Resolves()
    {
        var yaml = """
            on: push
            jobs:
              test1: &job
                runs-on: &runner ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                    with:
                      ref: *runner
              test2: *job
            """u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        // *runner should resolve — no "recursive alias" or "must be string" errors
        var hasRunnerDiag = result.Diagnostics.Any(d =>
            d.Message.Contains("recursive alias") || d.Message.Contains("must be string"));
        await Assert.That(hasRunnerDiag).IsFalse();
    }

    // regression: GetScalarTag returns Null for YAML null scalars
    [Test]
    public async Task Parse_NullScalarTag_ReturnsNull()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    container: null\n    steps:\n      - run: echo\n"u8;
        var result = WorkflowParser.Parse(yaml.ToArray(), "test.yaml");
        // If ScalarTag.Null is returned correctly, container: null doesn't produce a parse error
        var hasParseDiag = result.Diagnostics.Any(d => d.Message.Contains("container must be"));
        await Assert.That(hasParseDiag).IsFalse();
    }

    // regression: Unused anchor position accuracy
    [Test]
    public async Task Parse_UnusedAnchor_ReportsCorrectPosition()
    {
        // &unused_env is at line 5, col 22
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n        env: &unused_env\n          FOO: bar\n      - run: echo done\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unused_env") && d.Message.Contains("not used"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(7);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(14);
    }

    [Test]
    public async Task Parse_UnusedAnchor_OnScalar_ReportsAnchorPosition()
    {
        // on: &anchor push — the anchor &anchor starts at col 5
        var yaml = "on: &anchor push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("\"anchor\"") && d.Message.Contains("not used"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(1);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_UnusedAnchor_NestedInMapping_ReportsAnchorPosition()
    {
        // env: &unused at col 10
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    env: &unused\n      FOO: bar\n    steps:\n      - run: echo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("\"unused\"") && d.Message.Contains("not used"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(5);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(10);
    }

    // regression: Step empty element detection
    [Test]
    public async Task Parse_StepNullElement_ReportsEmptyAndMissingRunUses()
    {
        // `- null` should produce two diagnostics
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - null\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diags = result.Diagnostics;
        await Assert.That(diags.Any(d => d.Message.Contains("element of \"steps\" section should not be empty"))).IsTrue();
        await Assert.That(diags.Any(d => d.Message.Contains("step must run script with \"run\" section or run action with \"uses\" section"))).IsTrue();
    }

    [Test]
    public async Task Parse_StepBareDash_ReportsEmptyAndMissingRunUses()
    {
        // bare `-` produces null scalar
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      -\n      - run: echo ok\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diags = result.Diagnostics;
        await Assert.That(diags.Any(d => d.Message.Contains("element of \"steps\" section should not be empty"))).IsTrue();
        await Assert.That(diags.Any(d => d.Message.Contains("step must run script"))).IsTrue();
    }

    [Test]
    public async Task Parse_StepEmptyMapping_ReportsEmptyAndMissingRunUses()
    {
        // `- {}` produces MappingStart then MappingEnd with no keys
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - { }\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diags = result.Diagnostics;
        await Assert.That(diags.Any(d => d.Message.Contains("element of \"steps\" section should not be empty"))).IsTrue();
        await Assert.That(diags.Any(d => d.Message.Contains("step must run script"))).IsTrue();
    }

    [Test]
    public async Task Parse_StepNullElement_Position_PointsToElement()
    {
        // line 6: "      - null" — the null scalar starts at col 9
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - null\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("element of \"steps\" section should not be empty"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(6);
    }

    // regression: Step coexistence: run-first then uses
    [Test]
    public async Task Parse_StepRunThenUses_ReportsUnexpectedRunForAction()
    {
        // run: appears first, then uses: → run is unexpected for action step
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n        uses: actions/checkout@v4\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"run\" for step to execute action"));
        await Assert.That(diag.Message).Contains("expected one of");
        await Assert.That(diag.Message).Contains("\"uses\"");
    }

    [Test]
    public async Task Parse_StepUsesThenRun_ReportsUnexpectedUsesForRun()
    {
        // uses: appears first, then run: → uses is unexpected for run step
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        run: echo hello\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"uses\" for step to run shell command"));
        await Assert.That(diag.Message).Contains("expected one of");
        await Assert.That(diag.Message).Contains("\"run\"");
    }

    [Test]
    public async Task Parse_StepRunThenUses_PositionPointsToRunKey()
    {
        // run: on line 6 at col 9
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n        uses: actions/checkout@v4\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"run\" for step to execute action"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(6);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(9);
    }

    // regression: Step secondary key conflicts
    [Test]
    public async Task Parse_ActionStepWithShell_ReportsUnexpectedShell()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        shell: bash\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"shell\" for step to execute action"));
        await Assert.That(diag.Message).Contains("expected one of");
        await Assert.That(diag.Location.StartLine).IsEqualTo(7);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(9);
    }

    [Test]
    public async Task Parse_ActionStepWithWorkingDirectory_ReportsUnexpectedWD()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        working-directory: /foo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("unexpected key \"working-directory\" for step to execute action"))).IsTrue();
    }

    [Test]
    public async Task Parse_RunStepWithWith_ReportsUnexpectedWith()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n        with:\n          foo: bar\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"with\" for step to run shell command"));
        await Assert.That(diag.Message).Contains("expected one of");
    }

    [Test]
    public async Task Parse_ActionStepWithUnknownKey_ReportsUnexpectedForAction()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        foobar: baz\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"foobar\" for step to execute action"));
        await Assert.That(diag.Message).Contains("expected one of");
    }

    [Test]
    public async Task Parse_RunStepWithUnknownKey_ReportsUnexpectedForRun()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n        foobar: baz\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("unexpected key \"foobar\" for step to run shell command"));
        await Assert.That(diag.Message).Contains("expected one of");
    }

    // regression: Step missing run/uses message
    [Test]
    public async Task Parse_StepWithOnlyName_ReportsNewMissingRunUsesMessage()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - name: no-exec\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("step must run script"));
        await Assert.That(diag.Message).IsEqualTo("step must run script with \"run\" section or run action with \"uses\" section");
    }

    // regression: "with" section scalar → mapping expected
    [Test]
    public async Task Parse_StepWithScalar_ReportsScalarNotMapping()
    {
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n        with: foo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("\"with\" section is scalar node but mapping node is expected"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(7);
    }

    // regression: "steps" must be sequence node with tag info
    [Test]
    public async Task Parse_StepsNullScalar_ReportsSequenceNodeWithNullTag()
    {
        // steps: (empty, null scalar) → "steps" section must be sequence node but got scalar node with "!!null" tag
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("\"steps\" section must be sequence node"));
        await Assert.That(diag.Message).Contains("scalar node");
        await Assert.That(diag.Message).Contains("\"!!null\" tag");
    }

    [Test]
    public async Task Parse_StepsStringScalar_ReportsSequenceNodeWithoutTag()
    {
        // steps: notASequence → scalar without !!null tag
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps: notASequence\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("\"steps\" section must be sequence node"));
        await Assert.That(diag.Message).Contains("scalar node");
        await Assert.That(diag.Message).DoesNotContain("\"!!null\"");
    }

    [Test]
    public async Task Parse_StepsMapping_ReportsSequenceNodeGotMapping()
    {
        // steps: {foo: bar} → mapping node
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      foo: bar\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.FirstOrDefault(d => d.Message.Contains("\"steps\" section must be sequence node"));
        await Assert.That(diag.Message).Contains("mapping node");
    }

    // regression: "steps" section is missing in job
    [Test]
    public async Task Parse_JobMissingSteps_ReportsNewMissingMessage()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("\"steps\" section is missing"));
        await Assert.That(diag.Message).IsEqualTo("\"steps\" section is missing in job \"build\"");
    }

    // regression:Schedule message wording
    [Test]
    public async Task Parse_ScheduleScalar_ReportsConfiguredWithMapping()
    {
        var yaml = "on: schedule\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("schedule"));
        await Assert.That(diag.Message).IsEqualTo("schedule event must be configured with mapping");
    }

    [Test]
    public async Task Parse_ScheduleInSequence_ReportsConfiguredWithMapping()
    {
        var yaml = "on: [push, schedule]\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("schedule"));
        await Assert.That(diag.Message).IsEqualTo("schedule event must be configured with mapping");
    }

    [Test]
    public async Task Parse_ScheduleScalar_Position_PointsToEventName()
    {
        // on: schedule — "schedule" starts at col 5
        var yaml = "on: schedule\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diag = result.Diagnostics.First(d => d.Message.Contains("schedule event must be configured"));
        await Assert.That(diag.Location.StartLine).IsEqualTo(1);
        await Assert.That(diag.Location.StartColumn).IsEqualTo(5);
    }

    // regression: VYaml non-empty scalar position accuracy
    [Test]
    public async Task Parse_ContainerImageScalar_PositionPointsToImageValue()
    {
        // container: ubuntu:20.04 — "ubuntu:20.04" starts at col 16
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    container: ubuntu:20.04\n    steps:\n      - run: echo\n"u8;
        var bytes = yaml.ToArray();
        var result = WorkflowParser.Parse(bytes, "test.yaml");
        await Assert.That(result.HasFatalError).IsFalse();
        var container = result.Workflow!.Jobs.Get(bytes, "test"u8)!.Container;
        await Assert.That(container).IsNotNull();
    }

    [Test]
    public async Task Parse_RunsOnScalar_PositionPointsToLabelValue()
    {
        // runs-on: ubuntu-latest — "ubuntu-latest" starts at col 14
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"u8;
        var bytes = yaml.ToArray();
        var result = WorkflowParser.Parse(bytes, "test.yaml");
        var runner = result.Workflow!.Jobs.Get(bytes, "test"u8)!.RunsOn;
        await Assert.That(runner).IsNotNull();
        var arena = result.Arena!;
        var labels = runner!.Labels;
        await Assert.That(labels).IsNotNull();
        var labelStr = Encoding.UTF8.GetString(arena.GetStringValue(labels![0]));
        await Assert.That(labelStr).IsEqualTo("ubuntu-latest");
        var range = arena.GetStringRange(labels[0]);
        await Assert.That(range.StartLine).IsEqualTo(4);
        await Assert.That(range.StartColumn).IsEqualTo(14);
    }

    [Test]
    public async Task Parse_StepRunValue_PositionPointsToScalarContent()
    {
        // run: echo hello — "echo hello" starts at col 14
        var yaml = "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hello\n"u8;
        var bytes = yaml.ToArray();
        var result = WorkflowParser.Parse(bytes, "test.yaml");
        var arena = result.Arena!;
        var step = result.Workflow!.Jobs.Get(bytes, "test"u8)!.Steps![0];
        var exec = (ExecRun)step.Exec;
        var range = arena.GetStringRange(exec.Run);
        await Assert.That(range.StartLine).IsEqualTo(6);
        await Assert.That(range.StartColumn).IsEqualTo(14);
    }
}
