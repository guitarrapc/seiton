using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{
    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_SelfRepositoryReferences_NoDiagnostics()
    {
        var yaml = """
        on: push
        jobs:
          call:
            uses: $/.github/workflows/reusable.yml
          build:
            runs-on: ubuntu-24.04
            steps:
              - uses: $/.github/actions/setup
        """;

        await AssertRuleCases(
            new UnpinnedUsesRule(),
            "unpinned-uses",
            [new RuleCase("ok-self-repository-references", yaml, [])]);
    }

    [Test]
    public async Task UnpinnedUsesRule_SelfRepositoryActionOnGhes_ReportsUnsupportedSyntax()
    {
        var config = new LintConfig
        {
            Network = new NetworkConfig
            {
                GitHub = new GitHubNetworkConfig { GhesApiUrl = "https://ghes.example.com/api/v3" },
            },
        };
        var yaml = """
        on: push
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: $/.github/actions/setup
        """;

        await AssertRuleCases(
            new UnpinnedUsesRule(),
            "unpinned-uses",
            [new RuleCase("ng-self-repository-action-ghes", yaml, ["not available on GitHub Enterprise Server"])],
            config);
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-action-pinned-sha",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-action-tag-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-action-missing-ref-format",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout
            """,
            ["invalid reference format", "owner/repo[/path]@ref"]),
            new RuleCase(
            "ok-local-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup
            """,
            []),
            new RuleCase(
            "ng-local-action-with-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/setup@v1
            """,
            ["local action uses must not contain '@ref'"]),
            new RuleCase(
            "ng-self-repository-action-with-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: $/.github/actions/setup@v1
            """,
            ["local action uses must not contain '@ref'"]),
            new RuleCase(
            "ok-docker-action-reference",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            []),
            new RuleCase(
            "ok-reusable-workflow-pinned-sha",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@0123456789abcdef0123456789abcdef01234567
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-branch-ref",
            """
            on: push
            jobs:
                release:
                    uses: owner/repo/.github/workflows/reusable.yml@main
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-self-repository-workflow-with-ref",
            """
            on: push
            jobs:
                release:
                    uses: $/.github/workflows/reusable.yml@main
            """,
            ["local reusable workflow reference must not contain '@ref'"]),
            // regression: step without run/uses produces empty uses — should not trigger unpinned-uses rule
            new RuleCase(
            "ok-empty-uses-from-parser-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: broken step with no run or uses
            """,
            []),
            // ../ prefix is not valid for reusable workflow calls (only ./ is allowed).
            // UnpinnedUsesRule silently returns; ReusableWorkflowRule owns this diagnostic.
            new RuleCase(
            "ok-reusable-workflow-dotdotslash-defers-to-reusable-rule",
            """
            on: push
            jobs:
                release:
                    uses: ../other-repo/.github/workflows/reusable.yml
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_TableDriven()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet"), new IgnoreActionRule("my-org/*")],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-ignored-exact-match",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """,
            []),
            new RuleCase(
            "ok-ignored-wildcard-match",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/some-action@v1
            """,
            []),
            new RuleCase(
            "ng-not-ignored-still-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ok-ignored-reusable-workflow",
            """
            on: push
            jobs:
                release:
                    uses: guitarrapc/setup-dotnet/.github/workflows/reusable.yml@main
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-not-ignored",
            """
            on: push
            jobs:
                release:
                    uses: other-org/repo/.github/workflows/reusable.yml@main
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_Verbose_EmitsInfo()
    {
        var config = new LintConfig
        {
            Verbose = true,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "verbose-test.yml", config);
        var infoDiags = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Info).ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(1);
        await Assert.That(infoDiags[0].Message).Contains("ignored");
        await Assert.That(infoDiags[0].Message).Contains("guitarrapc/setup-dotnet@main");
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_NoVerbose_NoInfo()
    {
        var config = new LintConfig
        {
            Verbose = false,
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("guitarrapc/setup-dotnet")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: guitarrapc/setup-dotnet@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "no-verbose-test.yml", config);
        var infoDiags = result.Diagnostics.Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Info).ToArray();
        await Assert.That(infoDiags.Length).IsEqualTo(0);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_ShowsConfigHint_OncePerOwner()
    {
        // Two steps from the same owner should produce only ONE diagnostic with Help set
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
                        - uses: my-org/action-b@v2
                        - uses: other-org/tool@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-hint-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        // All three should warn (none are pinned to SHA)
        await Assert.That(warnings.Length).IsEqualTo(3);

        // First occurrence of my-org should have Help with config snippet
        var myOrgFirst = warnings.First(w => w.Message.Contains("my-org/action-a", StringComparison.Ordinal));
        await Assert.That(myOrgFirst.Help).IsNotNull();
        await Assert.That(myOrgFirst.Help!).Contains("my-org/*");
        await Assert.That(myOrgFirst.Help!).Contains("owner:");
        await Assert.That(myOrgFirst.Help!).DoesNotContain("ignore-actions: [\"my-org/*\"]");
        await Assert.That(myOrgFirst.Help!).Contains("ignore-actions");

        // Second occurrence of same owner should NOT have Help (deduplicated)
        var myOrgSecond = warnings.First(w => w.Message.Contains("my-org/action-b", StringComparison.Ordinal));
        await Assert.That(myOrgSecond.Help).IsNull();

        // Different owner should have its own Help
        var otherOrg = warnings.First(w => w.Message.Contains("other-org/tool", StringComparison.Ordinal));
        await Assert.That(otherOrg.Help).IsNotNull();
        await Assert.That(otherOrg.Help!).Contains("other-org/*");
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_ShowsConfigHint_ReusableWorkflow()
    {
        // Reusable workflow (job-level uses) should also get the Help hint
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@main
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-job-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(1);
        await Assert.That(warnings[0].Help).IsNotNull();
        await Assert.That(warnings[0].Help!).Contains("my-org/*");
        await Assert.That(warnings[0].Help!).Contains("owner:");
        await Assert.That(warnings[0].Help!).DoesNotContain("ignore-actions: [\"my-org/*\"]");
        await Assert.That(warnings[0].Help!).Contains("ignore-actions");
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_WhenIgnored()
    {
        // When the action is already ignored, no warning → no hint needed
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*")],
                },
            },
        };

        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-no-hint-test.yml", config);
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        // Should be ignored, no warning at all
        await Assert.That(warnings.Length).IsEqualTo(0);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_CaseInsensitiveOwnerDedup()
    {
        // Same owner with different case should be deduplicated
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: MyOrg/action-a@v1
                        - uses: myorg/action-b@v2
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-case-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(2);

        // First gets hint
        var first = warnings.First(w => w.Message.Contains("MyOrg/action-a", StringComparison.Ordinal));
        await Assert.That(first.Help).IsNotNull();
        await Assert.That(first.Help!).Contains("MyOrg/*");

        // Second (different case) does NOT get hint — same owner, deduplicated
        var second = warnings.First(w => w.Message.Contains("myorg/action-b", StringComparison.Ordinal));
        await Assert.That(second.Help).IsNull();
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_PreservesUtf8Owner()
    {
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: äction-org/tool@v1
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-utf8-owner-test.yml");
        var warning = result.Diagnostics
            .Single(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning);

        await Assert.That(warning.Help).IsNotNull();
        await Assert.That(warning.Help!).Contains("äction-org/*");
        await Assert.That(warning.Help!).DoesNotContain("?ction-org/*");
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_IgnoreActions_ProgrammaticConfig_PreservesNonAsciiOwnerCase()
    {
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("Äction-org/*")],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-programmatic-nonascii-owner-ignore",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: Äction-org/tool@v1
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_ShaPinned()
    {
        // SHA-pinned actions produce no warning → no hint
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@a5ac7e51b41094c92402da3b24376905380afc29
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-sha-test.yml");
        var warnings = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses" && x.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(warnings.Length).IsEqualTo(0);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_Help_NoHint_LocalAndDocker()
    {
        // Local and Docker uses should not produce help hints (no owner concept)
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: ./.github/actions/my-action
                        - uses: docker://alpine:3.18
            """);

        using var result = new LintEngine([new UnpinnedUsesRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "help-local-docker-test.yml");
        var allDiags = result.Diagnostics
            .Where(x => x.RuleId == "unpinned-uses")
            .ToArray();

        // No unpinned-uses warnings for local/docker (local has no @ref, docker has no SHA check here)
        foreach (var d in allDiags)
        {
            await Assert.That(d.Help).IsNull();
        }
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_MatchingRef_Ignored()
    {
        // Object form: ignore MyOrg/* only when ref is main or master
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main", "master"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-ref-conditional-main-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@main
            """,
            []),
            new RuleCase(
            "ok-ref-conditional-master-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-b@master
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_NonMatchingRef_Warns()
    {
        // Object form: non-matching ref should still produce warning
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main", "master"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ng-ref-conditional-v1-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-a@v1
            """,
            ["not pinned to a full-length commit SHA"]),
            new RuleCase(
            "ng-ref-conditional-develop-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action-b@develop
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_OwnerOnlyAndRefSpecificEntries()
    {
        // Owner-only entry ignores all refs; ref-specific entry ignores only listed refs.
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions =
                    [
                        new IgnoreActionRule("trusted-org/*"),           // all refs
                        new IgnoreActionRule("semi-trusted/*", ["main"]), // only main
                    ],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-owner-only-entry-ignores-all-refs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: trusted-org/action@v1
            """,
            []),
            new RuleCase(
            "ok-ref-specific-entry-matching-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: semi-trusted/action@main
            """,
            []),
            new RuleCase(
            "ng-object-form-non-matching-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: semi-trusted/action@v2
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_ReusableWorkflow()
    {
        // Object form with reusable workflow (job-level uses)
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-reusable-workflow-matching-ref-ignored",
            """
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@main
            """,
            []),
            new RuleCase(
            "ng-reusable-workflow-non-matching-ref",
            """
            on: push
            jobs:
                release:
                    uses: my-org/repo/.github/workflows/reusable.yml@v1
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_RefConditionalIgnore_CaseSensitiveRef()
    {
        // Refs are matched case-sensitively
        var config = new LintConfig
        {
            Rules = new Dictionary<string, RuleConfig>
            {
                ["unpinned-uses"] = new RuleConfig
                {
                    IgnoreActions = [new IgnoreActionRule("my-org/*", ["main"])],
                },
            },
        };

        var cases = new[]
        {
            new RuleCase(
            "ok-exact-case-match-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action@main
            """,
            []),
            new RuleCase(
            "ng-different-case-warns",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: my-org/action@Main
            """,
            ["not pinned to a full-length commit SHA"]),
        };

        await AssertRuleCases(new UnpinnedUsesRule(), "unpinned-uses", cases, config);
    }


    [Test]
    public async Task UnpinnedUses_MessageStartsWithActionName_NotActionUses()
    {
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """;

        var engine = new LintEngine();
        using var result = engine.Check(Encoding.UTF8.GetBytes(yaml), "test.yaml");
        try
        {
            var diag = result.Diagnostics.FirstOrDefault(d =>
                d.RuleId == "unpinned-uses" &&
                d.Message?.Contains("is not pinned to a full-length commit SHA") == true);
            var message = diag.Message;
            await Assert.That(message).IsNotNull();
            await Assert.That(message!.StartsWith("'actions/checkout@v4'", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {

        }
    }

    [Test]
    public async Task RuleRegression_UnpinnedUsesRule_CompositeActionSiblingLocalReference_NoPathDoesNotExistWarning()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-unpinned-composite-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var gitPushDir = Path.Combine(rootDir, ".github", "actions", "git-push");
        var signedCommitDir = Path.Combine(rootDir, ".github", "actions", "signed-commit");
        Directory.CreateDirectory(gitPushDir);
        Directory.CreateDirectory(signedCommitDir);

        var gitPushActionPath = Path.Combine(gitPushDir, "action.yaml");
        var signedCommitActionPath = Path.Combine(signedCommitDir, "action.yaml");

        try
        {
            File.WriteAllText(signedCommitActionPath, NormalizeYaml("""
            name: Signed Commit
            description: Signs commits
            runs:
              using: composite
              steps:
                - run: echo ok
                  shell: bash
            """), Encoding.UTF8);

            File.WriteAllText(gitPushActionPath, NormalizeYaml("""
            name: Git Push
            description: Push changes
            runs:
              using: composite
              steps:
                - uses: ./.github/actions/signed-commit
            """), Encoding.UTF8);

            using var result = new LintEngine([new UnpinnedUsesRule()])
                .Check(File.ReadAllBytes(gitPushActionPath), gitPushActionPath);

            var pathWarnings = result.Diagnostics
                .Where(x => x.RuleId == "unpinned-uses"
                    && x.Message.Contains("does not exist", StringComparison.Ordinal))
                .ToArray();

            await Assert.That(pathWarnings.Length).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }
}
