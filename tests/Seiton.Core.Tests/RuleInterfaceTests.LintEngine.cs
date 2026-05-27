using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Fixing;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task LintEngine_ReturnsCombinedParseAndRuleDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
          build:
            steps:
              - run: echo hello
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "lint-engine.yml");

        await Assert.That(result.HasFatalError).IsFalse();
        await Assert.That(result.Workflow is not null).IsTrue();
        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_FatalParse_ReturnsParseDiagnosticsOnly()
    {
        var yaml = "[]";

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "fatal.yml");

        await Assert.That(result.HasFatalError).IsTrue();
        await Assert.That(result.Workflow).IsNull();
        await Assert.That(result.Diagnostics).HasSingleItem();
        await Assert.That(result.Diagnostics[0].Message).IsEqualTo("workflow root must be object");
        await Assert.That(result.Diagnostics[0].FilePath).IsEqualTo("fatal.yml");
    }

    [Test]
    public async Task LintEngine_RuleDiagnostics_IncludeRuleIdAndFilePath()
    {
        var yaml = """
        on: push
        jobs:
            build:
                steps:
                    - run: echo hello
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "rule-filepath.yml");
        var diagnostic = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId == "job-structure"
            && x.Message.Contains("\"runs-on\" section is missing", StringComparison.Ordinal));

        await Assert.That(diagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(diagnostic.RuleId).IsEqualTo("job-structure");
        await Assert.That(diagnostic.FilePath).IsEqualTo("rule-filepath.yml");
    }

    [Test]
    public async Task LintEngine_ReportsInvalidWorkflowPermissionsScalar()
    {
        var yaml = """
        on: push
        permissions: admin-all
        jobs: {}
        """.Replace("\r\n", "\n");

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "permissions-invalid-scalar.yml");

        await Assert.That(result.ParseDiagnostics).IsEmpty();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("permissions scalar must be 'read-all' or 'write-all'", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReportsInvalidJobPermissionScopeValue()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions:
                    contents: admin
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """.Replace("\r\n", "\n");

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "permissions-invalid-scope.yml");

        await Assert.That(result.ParseDiagnostics).IsEmpty();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"admin\" is invalid as permission of scope \"contents\"", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReportsReusableWorkflowForbiddenKeys()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reuse.yml@main
                container: node:20
        """.Replace("\r\n", "\n");

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "reuse-forbidden-key.yml");

        // Parser no longer emits forbidden-key diagnostics (linter handles them)
        await Assert.That(result.ParseDiagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("calls reusable workflow with uses", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_ReportsMismatches()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-contract-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        target:
                            required: true
                            type: string
                        dry_run:
                            required: false
                            type: boolean
                    secrets:
                        token:
                            required: true
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        extra: test
                        dry_run: maybe
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var ruleDiagnostics = result.Diagnostics.Where(x => x.RuleId == "reusable-workflow").Select(x => x.Message).ToArray();

            await Assert.That(ruleDiagnostics.Any(m => m.Contains("unknown reusable workflow input 'extra'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("missing required reusable workflow input 'target'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("expects boolean but got 'maybe'", StringComparison.Ordinal))).IsTrue();
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("missing required reusable workflow secret 'token'", StringComparison.Ordinal))).IsTrue();
            // Input type error should use jobs.'<id>'.with path (not .input)
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("jobs.'deploy'.with", StringComparison.Ordinal) && m.Contains("expects boolean", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_AllowsValidCallerContract()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-contract-ok-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        retries:
                            required: true
                            type: number
                    secrets:
                        token:
                            required: true
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        retries: 3
                    secrets:
                        token: ${{ secrets.GITHUB_TOKEN }}
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "reusable-workflow")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowRule_LocalWorkflowContractValidation_NumberTypeMismatch_UsesWithPath()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-reuse-number-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var calleePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var calleeYaml = """
            on:
                workflow_call:
                    inputs:
                        retries:
                            required: true
                            type: number
            jobs:
                noop:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo callee
            """;

            var callerYaml = """
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml
                    with:
                        retries: abc
            """;

            File.WriteAllText(calleePath, NormalizeYaml(calleeYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ReusableWorkflowRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var ruleDiagnostics = result.Diagnostics.Where(x => x.RuleId == "reusable-workflow").Select(x => x.Message).ToArray();

            // Number type error should use jobs.'<id>'.with path
            await Assert.That(ruleDiagnostics.Any(m => m.Contains("jobs.'deploy'.with", StringComparison.Ordinal) && m.Contains("expects number but got 'abc'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_LocalReusableWorkflowWithAtRef_UsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                deploy:
                    uses: ./.github/workflows/reusable.yml@v1
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-local-ref.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses").ToArray();
        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Local @ref warning should use jobs.'<id>'.uses path
        await Assert.That(diagnostics[0].Message).Contains("jobs.'deploy'.uses");
        await Assert.That(diagnostics[0].Message).Contains("must not contain '@ref'");
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_InvalidRemoteReusableWorkflowFormat_UsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                deploy:
                    uses: invalid-format-no-at-ref
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-invalid-format.yml");

        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses").ToArray();
        await Assert.That(diagnostics).Count().IsGreaterThanOrEqualTo(1);
        // Invalid format error should use jobs.'<id>'.uses path
        await Assert.That(diagnostics[0].Message).Contains("jobs.'deploy'.uses");
        await Assert.That(diagnostics[0].Message).Contains("invalid reference format");
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_UnknownAndRequiredInputs()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            name: My action
            inputs:
                required_input:
                    required: true
                optional_input:
                    required: false
                legacy:
                    required: false
                    deprecationMessage: use optional_input instead
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                          with:
                            extra_key: x
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var msgs = result.Diagnostics.Where(x => x.RuleId == "local-action-inputs").Select(x => x.Message).ToArray();
            await Assert.That(msgs.Any(m => m.Contains("unknown local action input 'extra_key'", StringComparison.Ordinal) && m.Contains("optional_input", StringComparison.Ordinal) && m.Contains("required_input", StringComparison.Ordinal))).IsTrue();
            await Assert.That(msgs.Any(m => m.Contains("required input 'required_input' is not set", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_DeprecatedInput_Warns()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-dep-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            inputs:
                legacy:
                    required: false
                    deprecationMessage: use something else
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                          with:
                            legacy: v
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Severity == DiagnosticSeverity.Warning && x.Message.Contains("deprecated", StringComparison.Ordinal) && x.Message.Contains("use something else", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_Node16Runner_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-node16-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "old-node");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            runs:
              using: node16
              main: dist/index.js
            """;

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/old-node
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("deprecated runner 'node16'", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_Node20AndComposite_Allowed()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-ok-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(Path.Combine(actionsDir, "n20"));
        Directory.CreateDirectory(Path.Combine(actionsDir, "comp"));

        var actionN20 = Path.Combine(actionsDir, "n20", "action.yml");
        var actionComp = Path.Combine(actionsDir, "comp", "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionN20, NormalizeYaml("""
            name: N20
            description: Node20 action
            runs:
              using: node20
              main: index.js
            """), Encoding.UTF8);

            // Create the index.js file so file-existence check passes
            File.WriteAllText(Path.Combine(actionsDir, "n20", "index.js"), "", Encoding.UTF8);

            File.WriteAllText(actionComp, NormalizeYaml("""
            name: Comp
            description: Composite action
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """), Encoding.UTF8);

            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/n20
                        - uses: ./.github/actions/comp
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingActionFile_NoCrash()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-missing-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/does-not-exist
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingDescription_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-desc-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: No Description
            runs:
              using: composite
              steps:
                - run: echo hi
                  shell: bash
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("description is required", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_EnvNotAllowedForJsAction_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-env-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: JS with env
            description: A JS action that incorrectly uses env
            runs:
              using: node20
              main: index.js
              env:
                SOME_VAR: value
            """), Encoding.UTF8);
            File.WriteAllText(Path.Combine(actionsDir, "index.js"), "", Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("\"env\" is not allowed", StringComparison.Ordinal) && x.Message.Contains("JavaScript action", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_MissingMainFile_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-nofile-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Missing Main
            description: A JS action with missing main file
            runs:
              using: node20
              main: nonexistent.js
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("does not exist", StringComparison.Ordinal) && x.Message.Contains("nonexistent.js", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_InvalidBranding_Error()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-brand-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Bad Brand
            description: An action with bad branding
            branding:
              icon: dog
              color: neon-pink
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("invalid branding icon", StringComparison.Ordinal))).IsTrue();
            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("invalid branding color", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_DockerEnvAllowed_NoError()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-docker-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            File.WriteAllText(actionPath, NormalizeYaml("""
            name: Docker Action
            description: A Docker action with env
            runs:
              using: docker
              image: Dockerfile
              env:
                SOME_VAR: value
            """), Encoding.UTF8);

            File.WriteAllText(callerPath, NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
            """), Encoding.UTF8);

            using var result = new LintEngine([new LocalActionInputsRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "local-action-inputs" && x.Message.Contains("env", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_LocalActionInputsRule_ActionMetadataFixture_AllChecks()
    {
        // Full integration test against the testdata/examples fixture
        var root = FindRepoRoot();
        var path = Path.Combine(root, "testdata", "examples", "action_metadata_syntax_validation.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        using var result = new LintEngine([new LocalActionInputsRule()])
            .Check(File.ReadAllBytes(path), path);

        var msgs = result.Diagnostics.Where(x => x.RuleId == "local-action-inputs").Select(x => x.Message).ToArray();
        // 6 checks matching actionlint behavior
        await Assert.That(msgs.Any(m => m.Contains("\"env\" is not allowed", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("description is required", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("does not exist", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid branding color", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid branding icon", StringComparison.Ordinal))).IsTrue();
        await Assert.That(msgs.Any(m => m.Contains("invalid runs.using", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_LocalActionOutputs_StrictPropertyValidation()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-outputs-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionsDir = Path.Combine(rootDir, ".github", "actions", "my-action-with-output");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionsDir);

        var actionPath = Path.Combine(actionsDir, "action.yaml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var actionYaml = """
            name: My action with output
            description: my action with outputs
            outputs:
              some_value:
                description: some value returned from this action
            runs:
              using: node20
              main: index.js
            """;

            var callerYaml = """
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - uses: ./.github/actions/my-action-with-output
                    id: my_action
                  - run: echo ${{ steps.my_action.outputs.some_value }}
                  - run: echo ${{ steps.my_action.outputs.some-value }}
            """;

            File.WriteAllText(actionPath, NormalizeYaml(actionYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            var msgs = result.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            // some_value should be valid (no error) — check that no diagnostic targets "some_value" as the undefined property
            await Assert.That(msgs.Any(m => m.Contains("property \"some_value\" is not defined", StringComparison.Ordinal))).IsFalse();
            // some-value should be flagged as undefined property
            await Assert.That(msgs.Any(m => m.Contains("\"some-value\" is not defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_InsertsWithBlockAfterUses()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        using var result = engine.Check(sourceBytes, "checkout-persist-fix-insert-with.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Message.Contains("git remote set-url origin", StringComparison.Ordinal)).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-insert-with.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var withIndex = fixedText.IndexOf("with:", StringComparison.Ordinal);
        var persistIndex = fixedText.IndexOf("persist-credentials: false", StringComparison.Ordinal);
        await Assert.That(withIndex >= 0).IsTrue();
        await Assert.That(persistIndex > withIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_InsertsMissingInputIntoExistingWithBlock()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          fetch-depth: 1
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        using var result = engine.Check(sourceBytes, "checkout-persist-fix-existing-with.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-existing-with.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var persistIndex = fixedText.IndexOf("persist-credentials: false", StringComparison.Ordinal);
        var fetchDepthIndex = fixedText.IndexOf("fetch-depth: 1", StringComparison.Ordinal);
        await Assert.That(persistIndex >= 0).IsTrue();
        await Assert.That(fetchDepthIndex > persistIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_Fix_ReplacesTrueWithFalse()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          persist-credentials: true
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        using var result = engine.Check(sourceBytes, "checkout-persist-fix-replace.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials");

        await Assert.That(diagnostic.Fix is not null).IsTrue();
        await Assert.That(diagnostic.Fix!.Value.Description.Contains("git push", StringComparison.Ordinal)).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "checkout-persist-fix-replace.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("persist-credentials: false", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("persist-credentials: true", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "checkout-persist-credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_CheckoutPersistCredentials_DoesNotAttachFix_ForExpressionOrFlowMapping()
    {
        var expressionYaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                          persist-credentials: ${{ inputs.persist_credentials }}
        """;

        var flowYaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with: { fetch-depth: 1 }
        """;

        var engine = new LintEngine([new CheckoutPersistCredentialsRule()]);
        using var expressionResult = engine.Check(Encoding.UTF8.GetBytes(expressionYaml), "checkout-persist-no-fix-expression.yml");
        using var flowResult = engine.Check(Encoding.UTF8.GetBytes(flowYaml), "checkout-persist-no-fix-flow.yml");

        await Assert.That(expressionResult.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials").Fix is null).IsTrue();
        await Assert.That(flowResult.Diagnostics.First(x => x.RuleId == "checkout-persist-credentials").Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_LocalActionResolution_ReportsMissingMetadata()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-action-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        var actionDir = Path.Combine(rootDir, ".github", "actions", "setup");
        Directory.CreateDirectory(workflowsDir);
        Directory.CreateDirectory(actionDir);

        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            var callerYaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup
            """;

            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new UnpinnedUsesRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);

            await Assert.That(result.Diagnostics.Any(x => x.RuleId == "unpinned-uses" && x.Message.Contains("missing action.yml or action.yaml", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_StepRefLocation_PointsToRefPart()
    {
        const string usesLine = "            - uses: owner/repo/action@main";
        var yaml = string.Join(
            "\n",
            "on: push",
            "jobs:",
            "    build:",
            "        runs-on: ubuntu-latest",
            "        steps:",
            usesLine,
            string.Empty);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(NormalizeYaml(yaml)), "unpinned-uses-step-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        var refStartColumn = usesLine.IndexOf("@main", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(refStartColumn);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(refStartColumn + "@main".Length);
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_ReusableWorkflowRefLocation_PointsToRefPart()
    {
        const string usesLine = "        uses: owner/repo/.github/workflows/reusable.yml@main";
        var yaml = string.Join(
            "\n",
            "on: push",
            "jobs:",
            "    release:",
            usesLine,
            string.Empty);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(NormalizeYaml(yaml)), "unpinned-uses-job-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        var refStartColumn = usesLine.IndexOf("@main", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(refStartColumn);
        await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(refStartColumn + "@main".Length);
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_ReusableWorkflow_MessageIncludesUsesPath()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-uses-path.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        // Message should include jobs.'<id>'.uses path segment
        await Assert.That(diagnostic.Message).Contains("jobs.'release'.uses");
    }

    [Test]
    public async Task LintEngine_UnpinnedUsesRule_FixableHint_AppearsAfterUrl()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unpinned-uses-fixable-order.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unpinned-uses");

        // URL should come before the fixable hint
        var urlIdx = diagnostic.Message.IndexOf("see https://", StringComparison.Ordinal);
        var fixableIdx = diagnostic.Message.IndexOf("(fixable with", StringComparison.Ordinal);
        await Assert.That(urlIdx).IsGreaterThan(-1);
        await Assert.That(fixableIdx).IsGreaterThan(urlIdx);
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_JobIdWithSpace_ConvertsToKebabCase()
    {
        var yaml = """
        on: push
        jobs:
            "bad id":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo ng
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "id-naming");

        await Assert.That(diagnostic.Fix).IsNotNull();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "id-naming-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText).Contains("bad-id:");
        await Assert.That(fixedText).DoesNotContain("\"bad id\"");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "id-naming")).IsFalse();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_JobIdWithSpace_UpdatesNeedsReferences()
    {
        var yaml = """
        on: push
        jobs:
            "build job":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo build
            deploy:
                needs: "build job"
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo deploy
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-needs-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "id-naming");

        await Assert.That(diagnostic.Fix).IsNotNull();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "id-naming-needs-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText).Contains("build-job:");
        await Assert.That(fixedText).DoesNotContain("\"build job\"");
        await Assert.That(fixedText).Contains("needs: build-job");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "id-naming")).IsFalse();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_JobIdWithSpace_UpdatesNeedsSequenceReferences()
    {
        var yaml = """
        on: push
        jobs:
            "build job":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo test
            deploy:
                needs: ["build job", test]
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo deploy
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-needs-seq-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "id-naming");

        await Assert.That(diagnostic.Fix).IsNotNull();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "id-naming-needs-seq-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText).Contains("build-job:");
        await Assert.That(fixedText).DoesNotContain("\"build job\"");
        await Assert.That(fixedText).Contains("needs: [build-job, test]");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "id-naming")).IsFalse();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_JobIdWithSpace_UpdatesNeedsSequenceReferences_CaseInsensitive()
    {
        var yaml = """
        on: push
        jobs:
            "build job":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo build
            test:
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo test
            deploy:
                needs: ["BUILD JOB", test]
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo deploy
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-needs-seq-fix-case-insensitive.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "id-naming");

        await Assert.That(diagnostic.Fix).IsNotNull();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "id-naming-needs-seq-fix-case-insensitive.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText).Contains("build-job:");
        await Assert.That(fixedText).DoesNotContain("\"BUILD JOB\"");
        await Assert.That(fixedText).Contains("needs: [build-job, test]");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "id-naming")).IsFalse();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_SuggestedJobIdCollidesWithExisting_NoFix()
    {
        var yaml = """
        on: push
        jobs:
            "build job":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo a
            build-job:
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo b
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-collision-kebab-existing.yml");

        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "id-naming"
            && x.Message.Contains("\"build job\"", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix).IsNull();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_SuggestedJobIdCollidesWithExisting_DifferentAsciiCase_NoFix()
    {
        var yaml = """
        on: push
        jobs:
            "BUILD JOB":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo a
            BUILD-JOB:
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo b
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-collision-case-insensitive.yml");

        var diagnostic = result.Diagnostics.First(x =>
            x.RuleId == "id-naming"
            && x.Message.Contains("\"BUILD JOB\"", StringComparison.Ordinal));

        await Assert.That(diagnostic.Fix).IsNull();
    }

    [Test]
    public async Task LintEngine_IdNaming_Fix_InvalidJobId_UnderscoresInSuggestedNameBecomeHyphens()
    {
        var yaml = """
        on: push
        jobs:
            "foo_bar baz":
                runs-on: ubuntu-24.04
                permissions: {}
                steps:
                    - run: echo ng
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(NormalizeYaml(yaml));
        var engine = new LintEngine([new IdNamingRule()]);
        using var result = engine.Check(sourceBytes, "id-naming-underscore-kebab.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "id-naming");

        await Assert.That(diagnostic.Fix).IsNotNull();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "id-naming-underscore-kebab.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText).Contains("foo-bar-baz:");
        await Assert.That(fixedText).DoesNotContain("foo_bar");
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "id-naming")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_Fix_ReplacesValueAndClearsDiagnostic()
    {
        var yaml = """
        on: push
        permissions: 'write-all'
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        using var result = engine.Check(sourceBytes, "deny-write-all-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-write-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-write-all-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Workflow-level write-all should be fixed to {} (drop permissions), not read-all
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_Fix_JobLevel_ReplacesWithEmptyMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions: write-all
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyWriteAllRule()]);
        using var result = engine.Check(sourceBytes, "deny-write-all-job-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-write-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-write-all-job-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Job-level write-all should be fixed to {} (drop permissions)
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("write-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyReadAll_Fix_ReplacesReadAllWithExplicitMappingBaseline()
    {
        var yaml = """
        on: push
        permissions: 'read-all'
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyReadAllRule()]);
        using var result = engine.Check(sourceBytes, "deny-read-all-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-read-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-read-all-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Workflow-level read-all should be fixed to {}
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyReadAll_Fix_JobLevel_ReplacesWithEmptyMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                permissions: read-all
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new DenyReadAllRule()]);
        using var result = engine.Check(sourceBytes, "deny-read-all-job-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "deny-read-all");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "deny-read-all-job-fix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        // Job-level read-all should be fixed to {} (drop permissions)
        await Assert.That(fixedText.Contains("{}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("read-all", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobTimeoutMinutesRequired_Fix_AttachesWhenDefaultTimeoutConfigured()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobTimeoutMinutesRequiredRule()]);
        var config = new LintConfig
        {
            Fix = new FixConfig { Enabled = true, Defaults = new FixDefaultsConfig { JobTimeoutMinutes = 15 } },
        };

        using var result = engine.Check(sourceBytes, "job-timeout-minutes-required-fix.yml", config);
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-timeout-minutes-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "job-timeout-minutes-required-fix.yml", [diagnostic], config);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("timeout-minutes: 15", StringComparison.Ordinal)).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "job-timeout-minutes-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobTimeoutMinutesRequired_Fix_DoesNotAttachWhenDefaultTimeoutMissing()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobTimeoutMinutesRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-timeout-minutes-required-no-fix.yml", new LintConfig());
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-timeout-minutes-required");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_UnredactedSecrets_DiagnosticLocation_PointsToRunExpression_NotFollowingEnvKey()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: called secret
                      run: |
                        echo "called secret. ${APPLES}"
                      env:
                        APPLES: ${{ secrets.APPLES }}
        """;

        using var result = new LintEngine([new UnredactedSecretsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "unredacted-secrets-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "unredacted-secrets");

        var highlightedText = yaml.Split('\n')[diagnostic.Location.StartLine - 1].Trim();
        await Assert.That(highlightedText.Contains("${APPLES}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(highlightedText.StartsWith("echo", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsPermissionsAfterRunsOn()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-runs-on.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "job-permissions-required-fix-runs-on.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml).Replace("\r\n", "\n", StringComparison.Ordinal);

        var runsOnIndex = fixedText.IndexOf("runs-on: ubuntu-latest", StringComparison.Ordinal);
        var permissionsIndex = fixedText.IndexOf("permissions: {}", StringComparison.Ordinal);
        var stepsIndex = fixedText.IndexOf("steps:", StringComparison.Ordinal);

        await Assert.That(runsOnIndex >= 0).IsTrue();
        await Assert.That(permissionsIndex > runsOnIndex).IsTrue();
        await Assert.That(stepsIndex > permissionsIndex).IsTrue();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotIntroduceTabIndentation_WhenTargetScopeUsesSpaces()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-tab-introduce.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var permissionsLine = fixedText.Split('\n').First(x => x.Contains("permissions: {}", StringComparison.Ordinal));

        await Assert.That(permissionsLine.Contains('\t')).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsPermissionsAfterUses()
    {
        var yaml = """
        on: push
        jobs:
            reuse:
                uses: owner/repo/.github/workflows/reusable.yml@main
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-uses.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        var usesIndex = fixedText.IndexOf("uses: owner/repo/.github/workflows/reusable.yml@main", StringComparison.Ordinal);
        var permissionsIndex = fixedText.IndexOf("permissions: {}", StringComparison.Ordinal);

        await Assert.That(usesIndex >= 0).IsTrue();
        await Assert.That(permissionsIndex > usesIndex).IsTrue();
        using var relint = engine.Check(fixedBytes, "job-permissions-required-fix-uses.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotChangeWhitespaceOutsideInsertion()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-whitespace.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var permissionsLine = fixedText.Split('\n').First(x => x.Contains("permissions: {}", StringComparison.Ordinal));
        var withoutInsertedPermissions = fixedText.Replace(permissionsLine + "\n", string.Empty, StringComparison.Ordinal);
        var original = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(withoutInsertedPermissions).IsEqualTo(original);
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_DoesNotIntroduceTrailingSpaces()
    {
        var yaml = """
        on: push
        jobs:
            build:
                uses: owner/repo/.github/workflows/reusable.yml@main
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-trailing.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedText = Encoding.UTF8.GetString(FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits));
        var normalized = fixedText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            await Assert.That(lines[i].EndsWith(" ", StringComparison.Ordinal)).IsFalse();
            await Assert.That(lines[i].EndsWith("\t", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_DoesNotAttachFix_WhenIndentationInferenceIsAmbiguous()
    {
        var yaml = """
        on: push
        jobs:
            build: {}
        """;

        using var result = new LintEngine([new JobPermissionsRequiredRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "job-permissions-required-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsContentsRead_WhenJobUsesCheckout()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-checkout.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Fix should insert contents: read, not empty {}
        await Assert.That(fixedText.Contains("permissions: {}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(fixedText.Contains("permissions:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("contents: read", StringComparison.Ordinal)).IsTrue();

        // Relint should pass
        using var relint = engine.Check(fixedBytes, "job-permissions-required-fix-checkout.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_MergesPermissions_WhenJobUsesMultipleKnownActions()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                    - uses: actions/stale@v9
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-multi.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Fix should contain merged permissions from both actions
        await Assert.That(fixedText.Contains("permissions: {}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(fixedText.Contains("contents: read", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("issues: write", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("pull-requests: write", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_JobPermissionsRequired_Fix_InsertsEmptyPermissions_WhenNoKnownActionsUsed()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(sourceBytes, "job-permissions-required-fix-no-known-actions.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // No known actions → empty permissions
        await Assert.That(fixedText.Contains("permissions: {}", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_ReplacesSimpleDotAccessWithPosixVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-posix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        using var revalidated = FixEngine.ApplyAndRelint(engine, sourceBytes, "run-env-fix-posix.yml", [diagnostic]);
        var fixedText = Encoding.UTF8.GetString(revalidated.UpdatedUtf8Yaml);

        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${{ env.VERSION }}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(revalidated.After.Diagnostics.Any(x => x.RuleId == "run-env-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_ReplacesSimpleBracketAccessWithPowerShellVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: windows-latest
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ env['VERSION'] }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-powershell.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${{ env['VERSION'] }}", StringComparison.Ordinal)).IsFalse();
        using var relint = engine.Check(fixedBytes, "run-env-fix-powershell.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-env-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_UsesJobDefaultsShellForPowerShell()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: windows-latest
                defaults:
                    run:
                        shell: pwsh
                steps:
                    - run: Write-Host "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-job-defaults-pwsh.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_UsesWorkflowDefaultsShellForPowerShell()
    {
        var yaml = """
        on: push
        defaults:
            run:
                shell: pwsh
        jobs:
            build:
                runs-on: windows-latest
                steps:
                    - run: Write-Host "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-workflow-defaults-pwsh.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_StepShellOverridesJobDefaults()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: windows-latest
                defaults:
                    run:
                        shell: pwsh
                steps:
                    - shell: bash
                      run: echo "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-step-overrides-defaults.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Fix_ExpressionStepShell_DoesNotUseDefaultsShell()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: windows-latest
                defaults:
                    run:
                        shell: pwsh
                strategy:
                    matrix:
                        shell: [bash]
                steps:
                    - shell: ${{ matrix.shell }}
                      run: echo "${{ env.VERSION }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunEnvContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-env-fix-expression-shell-overrides-defaults.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("${VERSION}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("$env:VERSION", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_DoesNotAttachFix_ForCompositeExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ format('{0}', env.VERSION) }}"
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-no-fix-composite.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Help_ShownForCompositeExpression()
    {
        // When TryParseSimpleContextReference fails (composite expression), Help should hint env-block approach
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ env.TAG_VALUE || 'fallback' }}"
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-help-composite.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Help).IsNotNull();
        await Assert.That(diagnostic.Help!).Contains("env:");
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Help_NotShownForSimpleExpression()
    {
        // Simple env.VAR reference should NOT have help (it has a fix instead)
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    VERSION: "1.0"
                steps:
                    - run: echo "${{ env.VERSION }}"
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-no-help-simple.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        await Assert.That(diagnostic.Help).IsNull();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Help_ShownForCompositeExpression()
    {
        var yaml = """
        on:
            workflow_dispatch:
                inputs:
                    tag:
                        type: string
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ inputs.tag || 'v1.0.0' }}"
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-help-composite.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Help).IsNotNull();
        await Assert.That(diagnostic.Help!).Contains("env:");
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_Help_ShownForCompositeExpression()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ secrets.TOKEN || secrets.FALLBACK_TOKEN }}"
        """;

        using var result = new LintEngine([new RunSecretsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-secrets-help-composite.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-secrets-context-direct-use");

        await Assert.That(diagnostic.Help).IsNotNull();
        await Assert.That(diagnostic.Help!).Contains("env:");
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_NoDiagnostic_InsideSingleQuotedHereDoc()
    {
        // Single-quoted heredoc (<<'EOF') does not expand shell variables,
        // so ${{ env.* }} is the only way to insert values - not a false positive
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > pr_comment.md
                          Workflow [${{ env.GITHUB_ACTIONS_RUN_URL }}) found CRLF files.
                        EOF
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-no-diag-heredoc.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-env-context-direct-use")).IsEmpty();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_StillDetects_InsideUnquotedHereDoc()
    {
        // Unquoted heredoc (<<EOF) DOES expand shell variables,
        // so ${{ env.* }} should still be flagged
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << EOF > pr_comment.md
                          Workflow [${{ env.GITHUB_ACTIONS_RUN_URL }}) found CRLF files.
                        EOF
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-detect-unquoted-heredoc.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-env-context-direct-use")).IsNotEmpty();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_NoDiagnostic_InsideSingleQuotedHereDoc()
    {
        var yaml = """
        on:
            workflow_dispatch:
                inputs:
                    name:
                        type: string
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > output.md
                          Input: ${{ inputs.name }}
                        EOF
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-diag-heredoc.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-inputs-context-direct-use")).IsEmpty();
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_NoDiagnostic_InsideSingleQuotedHereDoc()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > config.yml
                          token: ${{ secrets.GITHUB_TOKEN }}
                        EOF
        """;

        using var result = new LintEngine([new RunSecretsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-secrets-no-diag-heredoc.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-secrets-context-direct-use")).IsEmpty();
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_Detects_AfterIndentedHereDocTerminator()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > pr_comment.md
                          inside ${{ env.IGNORED }}
                        EOF
                        echo ${{ env.DETECT_ME }}
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-detect-after-heredoc-terminator.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-env-context-direct-use")).Count().IsEqualTo(1);
        await Assert.That(result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use").Message).Contains("${{ env.* }}");
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Detects_AfterIndentedHereDocTerminator()
    {
        var yaml = """
        on:
            workflow_dispatch:
                inputs:
                    name:
                        type: string
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > output.md
                          inside ${{ inputs.name }}
                        EOF
                        echo ${{ inputs.name }}
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-detect-after-heredoc-terminator.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-inputs-context-direct-use")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_Detects_AfterIndentedHereDocTerminator()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF' > config.yml
                          inside ${{ secrets.GITHUB_TOKEN }}
                        EOF
                        echo ${{ secrets.GITHUB_TOKEN }}
        """;

        using var result = new LintEngine([new RunSecretsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-secrets-detect-after-heredoc-terminator.yml");

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-secrets-context-direct-use")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_RunEnvContextDirectUse_DiagnosticLocation_PointsToExpression_NotFollowingEnvKey()
    {
        // Regression: diagnostic was pointing to the step-level env: key (after the block scalar)
        // instead of the ${{ env.* }} expression inside the run: script.
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Dump environment
                      shell: bash
                      run: |
                        mkdir -p "${{ env.OUTPUT_PATH }}"
                        env | tee "${{ env.OUTPUT_PATH }}/out.sh"
                      env:
                        OUTPUT_PATH: ${{ inputs.output-path }}/env
        """;

        using var result = new LintEngine([new RunEnvContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-env-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-env-context-direct-use");

        // The diagnostic must NOT point to the env: key line (which comes after the run: block).
        // It must point to the actual ${{ env.* }} expression inside the run: script.
        var locationLine = diagnostic.Location.StartLine;
        var envKeyLineNumber = yaml.Split('\n')
            .Select((line, i) => (line, lineNumber: i + 1))
            .First(x => x.line.TrimStart().StartsWith("env:") && x.lineNumber > 10)
            .lineNumber;

        await Assert.That(locationLine).IsNotEqualTo(envKeyLineNumber);
        await Assert.That(locationLine).IsLessThan(envKeyLineNumber);
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_Fix_ReplacesSimpleReferenceWithMappedVariable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.MY_TOKEN }}
                steps:
                    - run: echo "${{ secrets.MY_TOKEN }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunSecretsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-secrets-fix-posix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-secrets-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("run: echo \"${TOKEN}\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunSecretsContextDirectUse_DoesNotAttachFix_WithoutUniqueMapping()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN_A: ${{ secrets.MY_TOKEN }}
                    TOKEN_B: ${{ secrets.MY_TOKEN }}
                steps:
                    - run: echo "${{ secrets.MY_TOKEN }}"
        """;

        using var result = new LintEngine([new RunSecretsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-secrets-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-secrets-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_ReplacesSimpleReferenceWithMappedVariable()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: windows-latest
                env:
                    TARGET: ${{ github.event.inputs.target }}
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ github.event.inputs.target }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-powershell.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("run: Write-Host \"$env:TARGET\"", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-powershell.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_DoesNotAttachFix_WithoutUniqueMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TARGET_A: ${{ inputs.target }}
                    TARGET_B: ${{ github.event.inputs.target }}
                steps:
                    - run: echo "${{ inputs.target }}"
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-ambiguous.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_BlockRunLocation_PointsToExpressionLine()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: benchmark
                      run: |
                        echo "${{ inputs.target }}"
                        echo done
                    - name: next
                      run: exit 1
        """.Replace("\r\n", "\n").Replace("\n", "\r\n");

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-block-location.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Location.StartLine).IsEqualTo(8);
        await Assert.That(diagnostic.Location.StartColumn).IsEqualTo(23);
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_InsertsEnvAndReplacesExpression_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ inputs.target }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-no-mapping.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Should replace the expression with shell variable
        await Assert.That(fixedText.Contains("${TARGET}", StringComparison.Ordinal)).IsTrue();
        // The run line should not contain the direct expression anymore
        var runLine = fixedText.Split('\n').First(l => l.Contains("run:", StringComparison.Ordinal));
        await Assert.That(runLine.Contains("${{ inputs.target }}", StringComparison.Ordinal)).IsFalse();
        // Should insert env mapping
        await Assert.That(fixedText.Contains("env:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("TARGET: ${{ inputs.target }}", StringComparison.Ordinal)).IsTrue();

        // Relint should pass
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-no-mapping.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_InsertsEnvAndReplacesPowershell_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: windows-latest
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ inputs.target }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-no-mapping-pwsh.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("$env:TARGET", StringComparison.Ordinal)).IsTrue();
        var runLine = fixedText.Split('\n').First(l => l.Contains("run:", StringComparison.Ordinal));
        await Assert.That(runLine.Contains("${{ inputs.target }}", StringComparison.Ordinal)).IsFalse();
        await Assert.That(fixedText.Contains("TARGET: ${{ inputs.target }}", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-no-mapping-pwsh.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_InsertsEnvWithHyphenatedName_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Generate Matrix
                      run: ./tool --config-path "./Repo/${{ inputs.benchmark-config-path }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-hyphenated.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Hyphens in input name should become underscores in env var
        await Assert.That(fixedText.Contains("${BENCHMARK_CONFIG_PATH}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("BENCHMARK_CONFIG_PATH: ${{ inputs.benchmark-config-path }}", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-hyphenated.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_InsertsEnvWithBracketAccess_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Generate Matrix
                      run: ./tool --config-path "./Repo/${{ inputs['benchmark-config-path'] }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-bracket.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        // Bracket access with hyphens should produce correct env var name
        await Assert.That(fixedText.Contains("${BENCHMARK_CONFIG_PATH}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("BENCHMARK_CONFIG_PATH: ${{ inputs.benchmark-config-path }}", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-bracket.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_InsertsEnvWithGithubEventBracketAccess_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - name: Generate Matrix
                      run: ./tool --config-path "./Repo/${{ github.event.inputs['benchmark-config-path'] }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-github-event-bracket.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("${BENCHMARK_CONFIG_PATH}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("BENCHMARK_CONFIG_PATH: ${{ github.event.inputs.benchmark-config-path }}", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-github-event-bracket.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_AppendsToExistingEnv_WhenNoExistingMapping()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ inputs.target }}"
                      env:
                        OTHER: value
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new RunInputsContextDirectUseRule()]);
        using var result = engine.Check(sourceBytes, "run-inputs-fix-existing-env.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        await Assert.That(fixedText.Contains("${TARGET}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("TARGET: ${{ inputs.target }}", StringComparison.Ordinal)).IsTrue();
        // Existing env should still be present
        await Assert.That(fixedText.Contains("OTHER: value", StringComparison.Ordinal)).IsTrue();

        // Relint: fixed YAML should not trigger the rule
        using var relint = engine.Check(fixedBytes, "run-inputs-fix-existing-env.yml");
        await Assert.That(relint.Diagnostics.Any(x => x.RuleId == "run-inputs-context-direct-use")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_DoesNotAttach_InsideHereDoc()
    {
        // Single-quoted heredoc suppresses the entire diagnostic (not just the fix)
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat << 'EOF'
                        ${{ inputs.target }}
                        EOF
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-heredoc.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });

        await Assert.That(result.Diagnostics.Where(x => x.RuleId == "run-inputs-context-direct-use")).IsEmpty();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_DoesNotAttach_ForCompositeExpression()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ format('{0}', inputs.target) }}"
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-composite.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_DoesNotAttach_InsideSingleQuotes()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo '${{ inputs.target }}'
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-single-quotes.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunInputsContextDirectUse_Fix_DoesNotAttach_WithEmptyFlowStyleEnv()
    {
        var yaml = """
        on: workflow_dispatch
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ inputs.target }}"
                      env: {}
        """;

        using var result = new LintEngine([new RunInputsContextDirectUseRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "run-inputs-no-fix-empty-env.yml", new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "run-inputs-context-direct-use");

        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_DeduplicatesRuleDiagnostics_ByPriority()
    {
        var yaml = """
                on: push
                jobs: {}
                """;

        var engine = new LintEngine(
        [
            new DuplicateDiagnosticRule(RuleId.Permissions),
                new DuplicateDiagnosticRule(RuleId.JobStructure),
        ]);

        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "priority-dedup.yml");
        var duplicated = result.Diagnostics
            .Where(static x => x.Message == "shared duplicate diagnostic")
            .ToArray();

        await Assert.That(duplicated.Length).IsEqualTo(1);
        await Assert.That(duplicated[0].RuleId).IsEqualTo("job-structure");
    }

    [Test]
    public async Task LintEngine_DisabledRule_DoesNotEmitDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var disabledConfig = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new RuleConfig { Enabled = false },
            },
        };

        using var disabledResult = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-disable.yml", disabledConfig);
        await Assert.That(disabledResult.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();

        using var enabledResult = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-enabled.yml");
        await Assert.That(enabledResult.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsTrue();
    }

    [Test]
    public async Task LintEngine_CanonicalIdInRuleOptions_EmitsConfigDiagnosticAndDoesNotDisable()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var configWithCanonicalId = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["seiton-lint-rule-008"] = new RuleConfig { Enabled = false },
            },
        };

        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "rule-disable-canonical.yml", configWithCanonicalId);
        // Canonical ID is rejected as unknown — the rule is NOT disabled
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsTrue();
        // A config diagnostic is emitted for the unknown rule ID
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("unknown rule-id 'seiton-lint-rule-008'"))).IsTrue();
    }

    [Test]
    public async Task LintEngine_RuleSeverityOverride_RewritesDiagnosticSeverity()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        var overrideConfig = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-required"] = new RuleConfig { Severity = DiagnosticSeverity.Error },
            },
        };

        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "severity-override.yml", overrideConfig);
        var diagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "job-permissions-required");

        await Assert.That(diagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SuppressesTargetRuleOnlyOnNextLine()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-required
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-next-line.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SupportsMultipleRuleIds()
    {
        var yaml = """
        on:
            # seiton: disable-next-line dangerous-triggers, job-permissions-required
            pull_request_target:
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-multi.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "dangerous-triggers")).IsFalse();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsTrue();
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_SupportsSemanticRuleId()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-required
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-semantic.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableNextLine_SupportsSemanticRuleId()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-required
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-next-line.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableFile_SuppressesRuleForEntireFile()
    {
        var yaml = """
        # seiton: disable-file job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-file.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableJob_SuppressesRuleOnlyForTargetJob()
    {
        var yaml = """
        # seiton: disable-job build job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-job.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(8);
    }

    [Test]
    public async Task LintEngine_InlineDisableNextLine_UnknownRuleId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            # seiton: disable-next-line job-permissions-requred
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-unknown-rule.yml");
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_InlineSeitonDisableJob_UnknownJobId_ReportsConfigurationError()
    {
        var yaml = """
        # seiton: disable-job buid job-permissions-required
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo test
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "inline-seiton-unknown-job.yml");
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown job-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_FileGlob_SuppressesDiagnosticsAndEmitsSummary()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsEqualTo(2);
        await Assert.That(result.SuppressionSummary.SuppressedByRule.TryGetValue("job-permissions-required", out var count) && count == 2).IsTrue();
        await Assert.That(result.SuppressionSummary.Records.All(x => x.Source == SuppressionSource.ConfigFile)).IsTrue();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_RepoRootRelativePath_SuppressesDiagnostics()
    {
        // Bug: repo-root relative exclusion like ".github/workflows/ci.yml" should work
        // even when the file path passed to Check is an absolute path (e.g. on Windows).
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion(".github/workflows/ci.yml", ["job-permissions-required"]),
            ],
        };

        // Simulate absolute path as produced by CLI's Path.GetFullPath on Windows
        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "D:/repo/.github/workflows/ci.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_RepoRootRelativeGlob_SuppressesDiagnostics()
    {
        // Pattern with glob but still repo-root relative (no leading **)
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion(".github/workflows/*.yml", ["job-permissions-required"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "D:/repo/.github/workflows/ci.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "job-permissions-required")).IsFalse();
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_NullRules_SuppressesAllDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: null),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        // File-level exclusion (Rules: null, no Jobs) short-circuits before rule execution
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_NullRules_JobScope_SuppressesOnlyTargetJob()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: null, Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);

        // 'build' job diagnostics should be suppressed, 'test' job diagnostics should remain
        var remaining = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();
        await Assert.That(remaining.Length).IsEqualTo(1);
        await Assert.That(remaining[0].Location.StartLine).IsEqualTo(7); // 'test' job
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsGreaterThanOrEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records.All(x => x.Source == SuppressionSource.ConfigJob)).IsTrue();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_JobScope_SuppressesTargetJobOnly()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
            test:
                runs-on: ubuntu-latest
                steps:
                    - run: echo two
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"], Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "job-permissions-required").ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(7);
        await Assert.That(result.SuppressionSummary.TotalSuppressed).IsEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records.Length).IsEqualTo(1);
        await Assert.That(result.SuppressionSummary.Records[0].Source).IsEqualTo(SuppressionSource.ConfigJob);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_UnknownRuleId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-requred"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_UnknownJobId_ReportsConfigurationError()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo one
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["job-permissions-required"], Jobs: ["buid"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "workflows/main.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown job-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_CanBeDisabledByConfig()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Enabled = false },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "disableable-rule-options.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_SeverityOverride_AppliesConfiguredSeverity()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-write-all"] = new RuleConfig { Severity = DiagnosticSeverity.Warning },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "severity-override.yml", config);
        var configDiagnostics = result.Diagnostics.Where(x => x.RuleId is null).ToArray();
        var ruleDiagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "deny-write-all");

        await Assert.That(configDiagnostics).IsEmpty();
        await Assert.That(ruleDiagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task LintEngine_DenyReadAll_CanBeDisabledByConfig()
    {
        var yaml = """
        on: push
        permissions: read-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-read-all"] = new RuleConfig { Enabled = false },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "disableable-rule-options-deny-read-all.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-read-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyReadAll_SeverityOverride_AppliesConfiguredSeverity()
    {
        var yaml = """
        on: push
        permissions: read-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["deny-read-all"] = new RuleConfig { Severity = DiagnosticSeverity.Warning },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "severity-override-deny-read-all.yml", config);
        var configDiagnostics = result.Diagnostics.Where(x => x.RuleId is null).ToArray();
        var ruleDiagnostic = result.Diagnostics.FirstOrDefault(x => x.RuleId == "deny-read-all");

        await Assert.That(configDiagnostics).IsEmpty();
        await Assert.That(ruleDiagnostic.Message.Length).IsGreaterThan(0);
        await Assert.That(ruleDiagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_InlineSuppression_SuppressesDiagnostic()
    {
        var yaml = """
        on: push
        # seiton: disable-next-line deny-write-all
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "disableable-inline.yml");

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_DenyWriteAll_ConfigExclusion_SuppressesDiagnostic()
    {
        var yaml = """
        on: push
        permissions: write-all
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", ["deny-write-all"]),
            ],
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "disableable-exclusion.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "deny-write-all")).IsFalse();
    }

    [Test]
    public async Task LintEngine_RuleOptions_UnknownRuleId_ReportsConfigurationErrorWithSuggestion()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["job-permissions-requred"] = new RuleConfig { Enabled = false },
            },
        };

        using var result = new LintEngine().Check(Encoding.UTF8.GetBytes(yaml), "rule-options-unknown.yml", config);
        var configError = result.Diagnostics.FirstOrDefault(x =>
            x.RuleId is null
            && x.Message.Contains("unknown rule-id", StringComparison.Ordinal));

        await Assert.That(configError.Message.Length).IsGreaterThan(0);
        await Assert.That(configError.Message.Contains("Did you mean 'job-permissions-required'", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_IsPassedToRuleConfig()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = (string[])["issue_comment", "pull_request_review_comment"] },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = (string[])["ubuntu-24.04-arm", "windows-2025-vs2026"] },
                ["credentials"] = new RuleConfig { PublicRegistries = (string[])["registry.example.com", "mirror.example.net:5000"] },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = (string[])["issue_comment"] },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = (string[])["tee"] },
            },
        };

        using var _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        var dtRule = rule.LastConfig!.GetRuleConfig("dangerous-triggers");
        await Assert.That(dtRule?.Events).IsEquivalentTo(new[] { "issue_comment", "pull_request_review_comment" });
        var rlRule = rule.LastConfig.GetRuleConfig("runner-label");
        await Assert.That(rlRule?.KnownHostedLabels).IsEquivalentTo(new[] { "ubuntu-24.04-arm", "windows-2025-vs2026" });
        var crRule = rule.LastConfig.GetRuleConfig("credentials");
        await Assert.That(crRule?.PublicRegistries).IsEquivalentTo(new[] { "registry.example.com", "mirror.example.net:5000" });
        var cpRule = rule.LastConfig.GetRuleConfig("cache-poisoning");
        await Assert.That(cpRule?.UntrustedTriggers).IsEquivalentTo(new[] { "issue_comment" });
        var usRule = rule.LastConfig.GetRuleConfig("unredacted-secrets");
        await Assert.That(usRule?.OutputCommands).IsEquivalentTo(new[] { "tee" });
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_DefaultsToEmptyWhenConfigOmitsIt()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();

        using var _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-default.yml", new LintConfig());

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.Rules).IsNull();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_NormalizesToAsciiLowerAndDeduplicates()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var rule = new ConfigCaptureRule();
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = (string[])["Issue_Comment", "issue_comment"] },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = (string[])["Custom-Large", "custom-large"] },
                ["credentials"] = new RuleConfig { PublicRegistries = (string[])["Registry.Example.Com", "registry.example.com"] },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = (string[])["Issue_Comment", "issue_comment"] },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = (string[])["TEE", "tee"] },
            },
        };

        using var _ = new LintEngine([rule]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-normalized.yml", config);

        await Assert.That(rule.LastConfig is not null).IsTrue();
        await Assert.That(rule.LastConfig!.GetRuleConfig("dangerous-triggers")?.Events).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.GetRuleConfig("runner-label")?.KnownHostedLabels).IsEquivalentTo(new[] { "custom-large" });
        await Assert.That(rule.LastConfig.GetRuleConfig("credentials")?.PublicRegistries).IsEquivalentTo(new[] { "registry.example.com" });
        await Assert.That(rule.LastConfig.GetRuleConfig("cache-poisoning")?.UntrustedTriggers).IsEquivalentTo(new[] { "issue_comment" });
        await Assert.That(rule.LastConfig.GetRuleConfig("unredacted-secrets")?.OutputCommands).IsEquivalentTo(new[] { "tee" });
    }

    [Test]
    public async Task LintEngine_DangerousTriggers_AdditionalDangerousEvents_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: ubuntu-latest
                permissions: {}
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = (string[])["issue_comment"] },
            },
        };

        using var result = new LintEngine([new DangerousTriggersRule()]).Check(Encoding.UTF8.GetBytes(yaml), "dangerous-trigger-custom.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId == "dangerous-triggers" && x.Message.Contains("issue_comment", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_CachePoisoning_AdditionalUntrustedTriggers_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/cache@v4
                      with:
                          path: ~/.npm
                          key: npm-${{ runner.os }}
        """;

        var engine = new LintEngine([new CachePoisoningRule()]);
        using var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "cache-poisoning-custom-without.yml");
        using var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "cache-poisoning-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = (string[])["issue_comment"] },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "cache-poisoning")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "cache-poisoning" && x.Message.Contains("untrusted triggers", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_SelfHostedRunner_AdditionalUntrustedTriggers_EmitWarning()
    {
        var yaml = """
        on: issue_comment
        jobs:
            build:
                runs-on: self-hosted
                steps:
                    - run: echo ok
        """;

        var engine = new LintEngine([new SelfHostedRunnerRule()]);
        using var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "self-hosted-runner-custom-without.yml");
        using var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "self-hosted-runner-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["self-hosted-runner"] = new RuleConfig { UntrustedTriggers = (string[])["issue_comment"] },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "self-hosted-runner")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "self-hosted-runner" && x.Message.Contains("untrusted triggers", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_UnredactedSecrets_AdditionalOutputCommands_EmitWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    TOKEN: ${{ secrets.GITHUB_TOKEN }}
                steps:
                    - run: tee /dev/null <<< "${TOKEN}"
        """;

        var engine = new LintEngine([new UnredactedSecretsRule()]);
        using var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "unredacted-secrets-custom-without.yml");
        using var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "unredacted-secrets-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["unredacted-secrets"] = new RuleConfig { OutputCommands = (string[])["tee"] },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "unredacted-secrets")).IsFalse();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "unredacted-secrets" && x.Message.Contains("without masking", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_RunnerLabel_AdditionalKnownHostedLabels_SuppressWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: custom-large
                permissions: {}
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new RunnerLabelRule()]);
        using var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "runner-label-custom-without.yml");
        using var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "runner-label-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["runner-label"] = new RuleConfig { KnownHostedLabels = (string[])["custom-large"] },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "runner-label")).IsTrue();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "runner-label")).IsFalse();
    }

    [Test]
    public async Task LintEngine_Credentials_AdditionalPublicRegistries_SuppressWarning()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                container:
                    image: registry.example.com/team/app:1.0.0
                steps:
                    - run: echo hello
        """;

        var engine = new LintEngine([new CredentialsRule()]);
        using var withoutConfig = engine.Check(Encoding.UTF8.GetBytes(yaml), "credentials-custom-without.yml");
        using var withConfig = engine.Check(
            Encoding.UTF8.GetBytes(yaml),
            "credentials-custom-with.yml",
            new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>
                {
                    ["credentials"] = new RuleConfig { PublicRegistries = (string[])["registry.example.com"] },
                },
            });

        await Assert.That(withoutConfig.Diagnostics.Any(x => x.RuleId == "credentials")).IsTrue();
        await Assert.That(withConfig.Diagnostics.Any(x => x.RuleId == "credentials")).IsFalse();
    }

    [Test]
    public async Task LintEngine_AdditiveCustomization_InvalidValues_ReportConfigurationErrors()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo hello
        """;

        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["dangerous-triggers"] = new RuleConfig { Events = (string[])["   "] },
                ["runner-label"] = new RuleConfig { KnownHostedLabels = (string[])[""] },
                ["credentials"] = new RuleConfig { PublicRegistries = (string[])["https://registry.example.com/team/app"] },
                ["cache-poisoning"] = new RuleConfig { UntrustedTriggers = (string[])[""] },
                ["unredacted-secrets"] = new RuleConfig { OutputCommands = (string[])["   "] },
                ["forbidden-uses"] = new RuleConfig { Allow = ["   "], Deny = ["   "] },
            },
        };

        using var result = new LintEngine([new ConfigCaptureRule()]).Check(Encoding.UTF8.GetBytes(yaml), "additive-customization-invalid.yml", config);

        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("events entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("known-hosted-labels entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("credentials additional public registry host 'https://registry.example.com/team/app' is invalid", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("untrusted-triggers entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("output-commands entry must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("allow pattern must not be empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Diagnostics.Any(x => x.RuleId is null && x.Message.Contains("deny pattern must not be empty", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_DuplicateParserAndLintDiagnostics_AreDeduplicated()
    {
        // Job without runs-on triggers both parser and job-structure rule
        var yaml = """
        on: push
        jobs:
          test:
            steps:
              - run: echo ok
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var runsOnDiags = result.Diagnostics.Where(d => d.Message.Contains("\"runs-on\" section is missing")).ToArray();
        await Assert.That(runsOnDiags).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_DuplicateParserAndLintDiagnostics_BothUsesAndSteps_AreDeduplicated()
    {
        // Job with both uses and steps triggers both parser and lint rules
        var yaml = """
        on: push
        jobs:
          test:
            uses: org/repo/.github/workflows/build.yml@main
            runs-on: ubuntu-latest
            steps:
              - run: echo ok
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        var bothDiags = result.Diagnostics.Where(d => d.Message.Contains("cannot have both uses and steps")).ToArray();
        await Assert.That(bothDiags).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_HashFilesInWorkflowEnv_ReportsParserDiagnostic()
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
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_HashFilesInJobIf_ReportsParserDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                if: ${{ hashFiles('**/package-lock.json') != '' }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"hashFiles\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_HashFilesInStepRun_NoDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo ${{ hashFiles('**/package-lock.json') }}
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task LintEngine_HashFilesInStepWith_NoDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/cache@v4
                      with:
                        key: ${{ hashFiles('**/package-lock.json') }}
                        path: ./packages
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("hashFiles", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task LintEngine_JobName_WithSecrets_ReportsDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            build:
                name: ${{ secrets.TOKEN }}
                runs-on: ubuntu-latest
                steps:
                    - run: echo ok
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task LintEngine_JobEnv_WithSecrets_NoDiagnostic()
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
        """u8;
        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        await Assert.That(result.Diagnostics.Any(x => x.Message.Contains("\"secrets\" is not allowed here", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task LintEngine_ReusableWorkflowSteps_NoDuplicateForbiddenKeyDiagnostic()
    {
        var yaml = """
        on: push
        jobs:
            call1:
                uses: owner/repo/.github/workflows/reuse.yml@main
                steps:
                    - run: echo hello
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        // Count messages about "steps" being not allowed — should be exactly 1, not 2 (parser + linter)
        var stepsNotAllowed = result.Diagnostics
            .Where(d => d.Message.Contains("key 'steps' is not allowed", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(stepsNotAllowed).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_AliasExpandedSteps_DedupDiagnosticsAtSamePosition()
    {
        var yaml = """
        on: push
        jobs:
          test:
            runs-on: ubuntu-latest
            steps:
              - &step
                run: echo
                with:
                  foo: bar
              - *step
              - *step
              - *step
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "test.yaml");
        // All alias-expanded steps point to the anchor position (same line:col).
        // The "unexpected key" errors differ only in step index prefix and must dedup to 1.
        var unexpectedKeyDiags = result.Diagnostics
            .Where(d => d.Message.Contains("unexpected key \"with\"", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(unexpectedKeyDiags).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_AliasExpandedActionMetadataSteps_DedupDiagnosticsAtSamePosition()
    {
        var yaml = """
        name: test
        description: test action
        runs:
          using: composite
          steps:
            - &step
              run: echo
              with:
                foo: bar
            - *step
            - *step
            - *step
        """u8;

        using var result = new LintEngine().Check(yaml.ToArray(), "action.yaml");
        // All alias-expanded steps point to the anchor position (same line:col).
        // The "unexpected key" errors differ only in step index prefix (steps[N]) and must dedup to 1.
        var unexpectedKeyDiags = result.Diagnostics
            .Where(d => d.Message.Contains("unexpected key \"with\"", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(unexpectedKeyDiags).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_InsertsEnvAndReplacesExpression()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // The expression should be replaced with a shell variable reference
        await Assert.That(fixedText.Contains("${GITHUB_EVENT_PULL_REQUEST_TITLE}", StringComparison.Ordinal)).IsTrue();
        // An env mapping should be inserted
        await Assert.That(fixedText.Contains("GITHUB_EVENT_PULL_REQUEST_TITLE: ${{ github.event.pull_request.title }}", StringComparison.Ordinal)).IsTrue();
        // The run line should use the shell variable, not the raw expression
        await Assert.That(fixedText.Contains("run: echo \"${GITHUB_EVENT_PULL_REQUEST_TITLE}\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_PowerShellUsesEnvPrefix()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - shell: pwsh
                      run: Write-Host "${{ github.event.head_commit.message }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-pwsh.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("$env:GITHUB_EVENT_HEAD_COMMIT_MESSAGE", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("GITHUB_EVENT_HEAD_COMMIT_MESSAGE: ${{ github.event.head_commit.message }}", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_DeduplicatesEnvName()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - env:
                        GITHUB_EVENT_PULL_REQUEST_TITLE: existing
                      run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-dedup.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // Should use _2 suffix since GITHUB_EVENT_PULL_REQUEST_TITLE already exists
        await Assert.That(fixedText.Contains("GITHUB_EVENT_PULL_REQUEST_TITLE_2", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_ReusesExistingEnvMapping()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - env:
                        PR_TITLE: ${{ github.event.pull_request.title }}
                      run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-reuse.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // Should reuse existing mapping, only replace expression (no new env entry)
        await Assert.That(fixedText.Contains("${PR_TITLE}", StringComparison.Ordinal)).IsTrue();
        // Should NOT add a new env mapping
        await Assert.That(fixedText.Contains("GITHUB_EVENT_PULL_REQUEST_TITLE", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_NoFix_WildcardPath()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo '${{ toJSON(github.event.*.body) }}'
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-no-fix-wildcard.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Wildcard paths can't generate a deterministic env var name
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_GithubHeadRef()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.head_ref }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-head-ref.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        await Assert.That(fixedText.Contains("${GITHUB_HEAD_REF}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fixedText.Contains("GITHUB_HEAD_REF: ${{ github.head_ref }}", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_BlockScalar_InsertsEnvAfterRunContent()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        echo "title: ${{ github.event.pull_request.title }}"
                        echo "done"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-block.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // The env: block must appear AFTER the run: block content (as a sibling key in the step mapping)
        var envLine = fixedText.IndexOf("GITHUB_EVENT_PULL_REQUEST_TITLE:", StringComparison.Ordinal);
        var runLine = fixedText.IndexOf("run: |", StringComparison.Ordinal);
        await Assert.That(envLine).IsGreaterThanOrEqualTo(0);
        await Assert.That(runLine).IsGreaterThan(0);
        await Assert.That(envLine).IsGreaterThan(runLine);

        // The shell variable should be inside the script body
        await Assert.That(fixedText.Contains("${GITHUB_EVENT_PULL_REQUEST_TITLE}", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_SingleKeyListItem_ProducesValidYaml()
    {
        // When a step is written as `- run: ...` (single-key list item),
        // the env: block must be inserted as a sibling key inside the list item mapping,
        // not outside the list item.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-list-item.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // The fixed output must be valid YAML that re-parses without fatal errors
        using var reparse = engine.Check(fixedBytes, "template-injection-fix-list-item.yml");
        await Assert.That(reparse.HasFatalError).IsEqualTo(false);

        // env: should be at the same indent as run: (inside the list item mapping)
        await Assert.That(fixedText.Contains("GITHUB_EVENT_PULL_REQUEST_TITLE:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_NotAttachedWhenFixDisabled()
    {
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        // Fix.Enabled defaults to false
        using var result = engine.Check(sourceBytes, "template-injection-no-fix.yml");
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Diagnostic should exist but without a fix attached
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_MultiLineEnvValue_InsertsAfterFullValue()
    {
        // When an existing env var has a multi-line block scalar value,
        // the new env entry must be inserted AFTER the entire block, not inside it.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - env:
                        SCRIPT: |
                            echo "line1"
                            echo "line2"
                      run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-multiline-env.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // The new env entry must NOT be inside the SCRIPT block scalar
        var scriptIdx = fixedText.IndexOf("SCRIPT: |", StringComparison.Ordinal);
        var newEntryIdx = fixedText.IndexOf("GITHUB_EVENT_PULL_REQUEST_TITLE:", StringComparison.Ordinal);
        var echoLine2Idx = fixedText.IndexOf("echo \"line2\"", StringComparison.Ordinal);
        await Assert.That(scriptIdx).IsGreaterThan(-1);
        await Assert.That(newEntryIdx).IsGreaterThan(echoLine2Idx);

        // The fixed output must still be valid YAML
        using var reparse = engine.Check(fixedBytes, "template-injection-fix-multiline-env.yml");
        await Assert.That(reparse.HasFatalError).IsEqualTo(false);
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_DeduplicateEnvName_SkipsWhenExhausted()
    {
        // When baseName through baseName_99 are all taken, the fix should NOT be attached
        // rather than producing a duplicate env key name.
        var envLines = string.Join("\n", Enumerable.Range(2, 98)
            .Select(i => $"                GITHUB_EVENT_PULL_REQUEST_TITLE_{i}: placeholder"));
        var yaml = "on: pull_request\n" +
            "jobs:\n" +
            "    build:\n" +
            "        runs-on: ubuntu-latest\n" +
            "        steps:\n" +
            "            - env:\n" +
            "                GITHUB_EVENT_PULL_REQUEST_TITLE: existing\n" +
            envLines + "\n" +
            "              run: echo \"${{ github.event.pull_request.title }}\"\n";

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-dedup-exhausted.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix should not be attached since all candidate names are taken
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_BlockScalarContentStartsWithRun_FindsCorrectKey()
    {
        // When a block scalar's first content line starts with "run:", the fix must
        // still correctly find the actual YAML run: key, not the content text.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        run: echo "${{ github.event.pull_request.title }}"
                        echo "second line"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-fix-run-in-content.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        await Assert.That(diagnostic.Fix is not null).IsTrue();

        var fixedBytes = FixEngine.Apply(sourceBytes, diagnostic.Fix!.Value.Edits);
        var fixedText = Encoding.UTF8.GetString(fixedBytes);

        // Fixed YAML must be parseable
        using var reparse = engine.Check(fixedBytes, "template-injection-fix-run-in-content.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        await Assert.That(reparse.HasFatalError).IsEqualTo(false);

        // After applying the fix, the template-injection diagnostic must be resolved.
        // If the env was inserted inside the block scalar (wrong), the expression remains
        // unresolved and the diagnostic persists.
        var remaining = reparse.Diagnostics.Where(x => x.RuleId == "template-injection").ToList();
        await Assert.That(remaining).Count().IsEqualTo(0);
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_NoExpandHeredoc_SkipsFix()
    {
        // When the untrusted expression is inside a no-expand heredoc body (<<'EOF'),
        // shell variables won't expand, so the fix must NOT be attached.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        cat <<'EOF'
                        ${{ github.event.pull_request.title }}
                        EOF
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-heredoc.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached because shell variable expansion is disabled in heredoc
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_FlowStyleEnv_SkipsFix()
    {
        // When existing env is flow-style (e.g. env: { A: 1 }), inserting a new line
        // after it would create a sibling key at step level, not inside env.
        // The fix must NOT be attached for flow-style env maps.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - env: { SCRIPT: "hello" }
                      run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-flow-env.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached because flow-style env can't be extended by line insertion
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_ActionMetadata_NoStaleStateFromPreviousWorkflow()
    {
        // When a LintEngine instance first checks a workflow (setting _currentWorkflow/_currentJob),
        // then checks an action.yml, the stale state must not influence the action metadata check.
        // The workflow has env var GITHUB_EVENT_PULL_REQUEST_TITLE that, if stale state leaks,
        // would cause the action fix to deduplicate with _2 suffix.
        var workflowYaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                env:
                    GITHUB_EVENT_PULL_REQUEST_TITLE: "placeholder"
                steps:
                    - run: echo "${{ github.event.pull_request.title }}"
        """;

        var actionYaml = """
        name: My Action
        description: Test action
        runs:
            using: composite
            steps:
                - run: echo "${{ github.event.pull_request.title }}"
                  shell: bash
        """;

        var engine = new LintEngine([new TemplateInjectionRule()]);
        var fixConfig = new LintConfig { Fix = new FixConfig { Enabled = true } };

        // First: check the workflow (this populates _currentWorkflow and _currentJob)
        using var _ = engine.Check(Encoding.UTF8.GetBytes(workflowYaml), ".github/workflows/ci.yml", fixConfig);

        // Second: check the action metadata
        using var actionResult = engine.Check(Encoding.UTF8.GetBytes(actionYaml), "action.yml", fixConfig);
        await Assert.That(actionResult.HasFatalError).IsEqualTo(false);
        var actionDiag = actionResult.Diagnostics.First(x => x.RuleId == "template-injection");
        await Assert.That(actionDiag.Fix is not null).IsTrue();

        // The fix must use GITHUB_EVENT_PULL_REQUEST_TITLE (not _2 suffix from stale dedup)
        await Assert.That(actionDiag.Fix!.Value.Description)
            .Contains("GITHUB_EVENT_PULL_REQUEST_TITLE");
        await Assert.That(actionDiag.Fix!.Value.Description)
            .DoesNotContain("_2");
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_EmptyEnvMapping_SkipsFix()
    {
        // When step has env: {} (empty mapping), inserting a new env: block would create
        // a duplicate env: key in the same step mapping. Fix must NOT be attached.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - env: {}
                      run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-empty-env.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached because empty env mapping would lead to duplicate env: keys
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_CompoundExpression_SkipsFix()
    {
        // When the untrusted path is inside a larger expression (e.g., with || operator),
        // the fix must NOT be attached because replacing the entire ${{ ... }} would
        // silently drop the surrounding expression logic.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.event.pull_request.title || 'default' }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-compound.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached because the path is embedded in a larger expression
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_MultipleExpressionsInStep_OnlyFirstGetsFix()
    {
        // When a single run: step has multiple untrusted expressions, only the first
        // diagnostic should get a fix attached. Multiple fixes would produce duplicate
        // insertion edits at the same offset, causing FixEngine.Apply to throw.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        echo "${{ github.event.pull_request.title }}"
                        echo "${{ github.event.pull_request.body }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-multi.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostics = result.Diagnostics
            .Where(x => x.RuleId == "template-injection")
            .ToArray();

        await Assert.That(diagnostics.Length).IsEqualTo(2);

        // First diagnostic gets a fix
        await Assert.That(diagnostics[0].Fix is not null).IsTrue();
        // Second diagnostic must NOT get a fix (would conflict at same insertion offset)
        await Assert.That(diagnostics[1].Fix is null).IsTrue();

        // Applying the single fix must not throw
        var fixedYaml = Seiton.Core.Linting.Fixing.FixEngine.Apply(sourceBytes, result.FixableDiagnostics);
        await Assert.That(fixedYaml).IsNotNull();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_InsideSingleQuotes_SkipsFix()
    {
        // When the untrusted expression is inside shell single quotes,
        // shell variables won't expand, so fixing to ${VAR} would be nonsensical.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo '${{ github.event.pull_request.title }}'
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-single-quote.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached because shell variable expansion is disabled in single quotes
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_InsideDoubleQuotes_GetsFix()
    {
        // When the untrusted expression is inside shell double quotes,
        // shell variables DO expand, so fix should be attached.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "${{ github.event.pull_request.title }}"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-double-quote.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix SHOULD be attached because shell variable expansion works in double quotes
        await Assert.That(diagnostic.Fix is not null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_MultilineSingleQuotes_SkipsFix()
    {
        // Multi-line run with expression inside single quotes on a specific line
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: |
                        echo 'prefix ${{ github.event.pull_request.title }} suffix'
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-multiline-sq.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix must NOT be attached - inside single quotes
        await Assert.That(diagnostic.Fix is null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_SingleQuotesInsideDoubleQuotes_GetsFix()
    {
        // Single quotes inside double quotes are literal characters, NOT shell delimiters.
        // The expression is inside double quotes where ${VAR} expands, so fix should attach.
        var yaml = """
        on: pull_request
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - run: echo "'${{ github.event.pull_request.title }}'"
        """;

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-sq-in-dq.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix SHOULD be attached - single quotes inside double quotes don't suppress expansion
        await Assert.That(diagnostic.Fix is not null).IsTrue();
    }

    [Test]
    public async Task LintEngine_TemplateInjection_Fix_ExistingEnvNoTrailingNewline_InsertsCorrectly()
    {
        // When the file ends without a trailing newline and the last env entry is on the
        // final line (env after run), insertion must still produce valid YAML.
        var yaml = "on: pull_request\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo \"${{ github.event.pull_request.title }}\"\n        env:\n          EXISTING: value";

        var sourceBytes = Encoding.UTF8.GetBytes(yaml);
        var engine = new LintEngine([new TemplateInjectionRule()]);
        using var result = engine.Check(sourceBytes, "template-injection-no-trailing-newline.yml",
            new LintConfig { Fix = new FixConfig { Enabled = true } });
        var diagnostic = result.Diagnostics.First(x => x.RuleId == "template-injection");

        // Fix should be attached (existing env, not flow-style)
        await Assert.That(diagnostic.Fix is not null).IsTrue();

        // Apply fix should produce valid output without corrupting the last line
        var fixedYaml = Seiton.Core.Linting.Fixing.FixEngine.Apply(sourceBytes, result.FixableDiagnostics);
        var fixedStr = Encoding.UTF8.GetString(fixedYaml);
        // The new env entry must be on its own line, not appended to the last env line
        await Assert.That(fixedStr).Contains("\n          GITHUB_EVENT_PULL_REQUEST_TITLE: ${{ github.event.pull_request.title }}");
    }

    [Test]
    public async Task LintEngine_DefaultSortOrder_SortsByLocation()
    {
        // This workflow triggers:
        // - runner-no-latest (priority 20) at line 4 (runs-on: ubuntu-latest)
        // - job-permissions-required (priority 7) at line 3 (job 'build' without permissions)
        // Default sort (location) should order by line: line 3 before line 4.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule(), new RunnerNoLatestRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "sort-order-default.yml");

        var ruleDiags = result.Diagnostics
            .Where(x => x.RuleId == "job-permissions-required" || x.RuleId == "runner-no-latest")
            .ToArray();

        await Assert.That(ruleDiags.Length).IsEqualTo(2);
        // With location-based sort (default): job-permissions-required (line 3) before runner-no-latest (line 4)
        await Assert.That(ruleDiags[0].RuleId).IsEqualTo("job-permissions-required");
        await Assert.That(ruleDiags[1].RuleId).IsEqualTo("runner-no-latest");
    }

    [Test]
    public async Task LintEngine_RuleSortOrder_SortsByRulePriority()
    {
        // Same workflow as above but with sort-order: rule config.
        // Rule priority: job-permissions-required (7) < runner-no-latest (20)
        // So job-permissions-required should still come first (lower priority number = higher priority).
        // But if they were on same lines, rule-priority sort would group by rule.
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        """;

        var config = new LintConfig
        {
            Output = new OutputConfig { SortOrder = DiagnosticSortOrder.Rule },
        };
        var engine = new LintEngine([new JobPermissionsRequiredRule(), new RunnerNoLatestRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "sort-order-rule.yml", config);

        var ruleDiags = result.Diagnostics
            .Where(x => x.RuleId == "job-permissions-required" || x.RuleId == "runner-no-latest")
            .ToArray();

        await Assert.That(ruleDiags.Length).IsEqualTo(2);
        // With rule sort: job-permissions-required (priority 7) before runner-no-latest (priority 20)
        await Assert.That(ruleDiags[0].RuleId).IsEqualTo("job-permissions-required");
        await Assert.That(ruleDiags[1].RuleId).IsEqualTo("runner-no-latest");
    }

    [Test]
    public async Task LintEngine_DefaultSortOrder_GloballySortsParserAndRuleDiagnostics()
    {
        // Parser diagnostic ("does not support option: branch") appears at line 8 (on.push.branch).
        // Rule diagnostic (job-permissions-required) appears at line 2 (jobs.build).
        // Without global sort, parser diagnostics come first regardless of line number.
        // With correct global sort, line 2 (rule) must appear before line 8 (parser).
        var yaml = """
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@0ad4b8fadaa221de15dcec353f45205ec38ea70b
        on:
          push:
            branch: main
        """;

        var engine = new LintEngine([new JobPermissionsRequiredRule()]);
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "global-sort.yml");

        // Should have at least 2 diagnostics: parser (line 8) + rule (line 2)
        await Assert.That(result.Diagnostics.Length).IsGreaterThanOrEqualTo(2);

        // Verify ALL diagnostics are globally sorted by line number
        for (var i = 1; i < result.Diagnostics.Length; i++)
        {
            var prev = result.Diagnostics[i - 1];
            var curr = result.Diagnostics[i];
            var prevLine = prev.Location.StartLine;
            var currLine = curr.Location.StartLine;
            await Assert.That(currLine).IsGreaterThanOrEqualTo(prevLine)
                .Because($"Diagnostic at index {i} (line {currLine}: {curr.Message}) should not appear before diagnostic at index {i - 1} (line {prevLine}: {prev.Message})");
        }
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_FileLevelExclusion_SuppressesParseErrors()
    {
        // YAML with parse errors (invalid syntax)
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                        invalid_yaml: [unclosed
        """u8.ToArray();

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: null),
            ],
        };

        using var result = new LintEngine().Check(yaml, "workflows/broken.yml", config);

        // File-level exclusion (Rules: null, no Jobs) should suppress ALL diagnostics including parse errors
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_RuleSpecificExclusion_DoesNotSuppressParseErrors()
    {
        // YAML with parse errors (invalid syntax)
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                        invalid_yaml: [unclosed
        """u8.ToArray();

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: ["job-permissions-required"]),
            ],
        };

        using var result = new LintEngine().Check(yaml, "workflows/broken.yml", config);

        // Rule-specific exclusion should NOT suppress parse errors (parse errors have no RuleId)
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_FileLevelWithJobs_DoesNotSuppressParseErrors()
    {
        // YAML with parse errors - file-level exclusion with Jobs specified should not short-circuit
        // because job-scoped exclusion requires parse to determine job boundaries
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                        invalid_yaml: [unclosed
        """u8.ToArray();

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/*.yml", Rules: null, Jobs: ["build"]),
            ],
        };

        using var result = new LintEngine().Check(yaml, "workflows/broken.yml", config);

        // Job-scoped exclusion cannot suppress parse errors (job scope requires successful parse)
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task LintEngine_ConfigExclusion_FileLevelExclusion_NonMatchingFile_DoesNotSuppressParseErrors()
    {
        // YAML with parse errors - file-level exclusion for different file pattern
        var yaml = """
        on: push
        jobs:
            build:
                runs-on: ubuntu-latest
                steps:
                    - uses: actions/checkout@v4
                      with:
                        invalid_yaml: [unclosed
        """u8.ToArray();

        var config = new LintConfig
        {
            Exclusions =
            [
                new LintExclusion("**/other.yml", Rules: null),
            ],
        };

        using var result = new LintEngine().Check(yaml, "workflows/broken.yml", config);

        // Non-matching file-level exclusion should NOT suppress parse errors
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }
}
