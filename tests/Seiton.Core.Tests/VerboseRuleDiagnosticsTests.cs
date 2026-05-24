using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

/// <summary>Phase 4: Verbose diagnostics expansion for rules that skip/ignore items based on config patterns.</summary>
public sealed class VerboseRuleDiagnosticsTests
{
    private static string NormalizeYaml(string yaml)
    {
        return yaml.Replace("\r\n", "\n");
    }

    // ==========================================================================
    // ForbiddenUsesRule — verbose info when allow pattern matches
    // ==========================================================================

    [Test]
    public async Task ForbiddenUsesRule_AllowPattern_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Deny = ["evil-org/*"],
                    Allow = ["evil-org/exception-repo"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: evil-org/exception-repo@v1
            """);

        using var result = new LintEngine([new ForbiddenUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "forbidden-uses" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("evil-org/exception-repo");
        await Assert.That(infoDiags[0].Message).Contains("allow");
    }

    [Test]
    public async Task ForbiddenUsesRule_AllowPattern_NoVerbose_NoInfo()
    {
        var config = new LintConfig
        {
            Verbose = false,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Deny = ["evil-org/*"],
                    Allow = ["evil-org/exception-repo"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: evil-org/exception-repo@v1
            """);

        using var result = new LintEngine([new ForbiddenUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "no-verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "forbidden-uses" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ForbiddenUsesRule_NotDenied_Verbose_NoInfo()
    {
        // Action not in deny list → no diagnostic at all (not even verbose info)
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["forbidden-uses"] = new RuleConfig
                {
                    Deny = ["evil-org/*"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: good-org/safe-action@v1
            """);

        using var result = new LintEngine([new ForbiddenUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "forbidden-uses" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    // ==========================================================================
    // CredentialsRule — public registries stay silent to avoid verbose noise
    // ==========================================================================

    [Test]
    public async Task CredentialsRule_PublicRegistry_Verbose_EmitsNoInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/myorg/myapp:latest
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new CredentialsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "credentials" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CredentialsRule_PublicRegistry_NoVerbose_NoInfo()
    {
        var config = new LintConfig
        {
            Verbose = false,
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/myorg/myapp:latest
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new CredentialsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "no-verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "credentials" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CredentialsRule_AdditionalPublicRegistry_Verbose_EmitsNoInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["credentials"] = new RuleConfig
                {
                    PublicRegistries = (string[])["myregistry.example.com"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: myregistry.example.com/myorg/myapp:latest
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new CredentialsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "credentials" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    // ==========================================================================
    // RunnerLabelRule — verbose info when additional-known-hosted label matches
    // ==========================================================================

    [Test]
    public async Task RunnerLabelRule_AdditionalKnownLabel_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: my-custom-runner
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("my-custom-runner");
        await Assert.That(infoDiags[0].Message).Contains("known-hosted-labels");
    }

    [Test]
    public async Task RunnerLabelRule_AdditionalKnownLabel_NoVerbose_NoInfo()
    {
        var config = new LintConfig
        {
            Verbose = false,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: my-custom-runner
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "no-verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RunnerLabelRule_BuiltinLabel_Verbose_NoInfo()
    {
        // Built-in known labels (ubuntu-latest etc.) should NOT emit verbose info
        // to avoid noise — only user-configured additional labels emit.
        var config = new LintConfig
        {
            Verbose = true,
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RunnerLabelRule_MatrixAdditionalKnownLabel_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner: [my-custom-runner]
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("my-custom-runner");
        await Assert.That(infoDiags[0].Message).Contains("known-hosted-labels");
    }

    [Test]
    public async Task RunnerLabelRule_MatrixAdditionalKnownLabelArray_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - [my-custom-runner]
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("my-custom-runner");
        await Assert.That(infoDiags[0].Message).Contains("known-hosted-labels");
    }

    [Test]
    public async Task RunnerLabelRule_StaticMultiLabel_DeduplicatesVerboseInfoPerJob()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner", "my-custom-runner-2"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: [my-custom-runner, my-custom-runner-2]
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        // Both labels are additional-known, but info is deduplicated to 1 per job
        await Assert.That(infoDiags.Length).IsEqualTo(1);
    }

    [Test]
    public async Task RunnerLabelRule_MatrixAdditionalKnownLabelArray_DeduplicatesVerboseInfoPerJob()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["runner-label"] = new RuleConfig
                {
                    KnownHostedLabels = (string[])["my-custom-runner", "my-custom-runner-2"],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            runner:
                                - [my-custom-runner, my-custom-runner-2]
                    runs-on: ${{ matrix.runner }}
                    steps:
                        - run: echo hello
            """);

        using var result = new LintEngine([new RunnerLabelRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics
            .Where(x => x.RuleId == "runner-label" && x.Severity == DiagnosticSeverity.Info)
            .ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
    }
}
