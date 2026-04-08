using System.Text;
using Seiton.Core.Parsing;

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
        var yaml = "on: push\nenv:\n  BAD: ${{ steps.prep.outputs.ok }}\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "workflow-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in workflow expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_RunName_UnknownFunction_ReportsSemanticError()
    {
        var yaml = "run-name: Build ${{ unknownFn(github.ref) }}\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ok\n";

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
        var actionlintTestdata = Path.Combine(root, ".references", "actionlint-main", "testdata");
        if (!Directory.Exists(actionlintTestdata))
        {
            // Optional corpus in local checkout.
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
        var actionlintTestdata = Path.Combine(root, ".references", "actionlint-main", "testdata");
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
        var errRoot = Path.Combine(root, ".references", "actionlint-main", "testdata", "err");
        if (!Directory.Exists(errRoot))
        {
            return;
        }

        var expectations = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["empty.yaml"] = ["workflow root must be mapping"],
            ["empty_on.yaml"] = ["unknown event in on"],
            ["case_sensitive_keys.yaml"] = ["unexpected workflow key", "unexpected job key"],
        };

        var failures = new List<string>();
        foreach (var (fileName, expectedMessages) in expectations)
        {
            var path = Path.Combine(errRoot, fileName);
            if (!File.Exists(path))
            {
                failures.Add($"missing fixture: {fileName}");
                continue;
            }

            var result = WorkflowParser.Parse(File.ReadAllBytes(path), path);
            for (var i = 0; i < expectedMessages.Length; i++)
            {
                var expected = expectedMessages[i];
                var found = result.Diagnostics.Any(d => d.Message.Contains(expected, StringComparison.Ordinal));
                if (!found)
                {
                    failures.Add($"{fileName}: expected diagnostic containing '{expected}' was not found");
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Schema_Corpus_JsonFilesAreValid()
    {
        var root = FindRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, ".references", "ghalint-main", "json-schema", "ghalint.json"),
            Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "github-workflow.json"),
            Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "github-action.json"),
            Path.Combine(root, ".references", "zizmor-main", "crates", "zizmor", "src", "data", "dependabot-2.0.json"),
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
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    env:\n      BAD: ${{ steps.prep.outputs.ok }}\n    steps:\n      - run: echo ok\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "job-env-step-context.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("context 'steps' is not available in job expressions", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepRun_EmbeddedUnknownFunction_ReportsSemanticError()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ${{ unknownFn(github.ref) }}\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-run-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepWith_EmbeddedUnknownFunction_ReportsSemanticError()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/cache@v4\n        with:\n          key: ${{ unknownFn(github.ref) }}\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-with-unknown-function.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown expression function: unknownFn", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Parse_StepIf_FunctionTypeMismatch_ReportsSemanticError()
    {
        var yaml = "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - if: contains(1, 'x')\n        run: echo ok\n";

        var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml), "step-if-type-mismatch.yml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("argument 1 should be string, but got number", StringComparison.Ordinal))).IsTrue();
    }

    private static IEnumerable<string> EnumerateCorpusYamlFiles(string repoRoot)
    {
        var refsRoot = Path.Combine(repoRoot, ".references");
        var candidates = new[]
        {
            Path.Combine(refsRoot, "actionlint-main", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint-main", ".github", "workflows"),
            Path.Combine(refsRoot, "zizmor-main", ".github", "workflows"),
            Path.Combine(refsRoot, "ghalint-main"),
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
