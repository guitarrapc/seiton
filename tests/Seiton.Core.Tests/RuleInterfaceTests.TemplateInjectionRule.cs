using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_TemplateInjectionRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref }}"
            """,
            []),
            new RuleCase(
            "ok-run-without-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hello
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-event-pull-request-title",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-env-maps-github-event-comment-body",
            """
            on: issue_comment
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            COMMENT_BODY: ${{ github.event.comment.body }}
                          run: echo "$COMMENT_BODY"
            """,
            []),
            new RuleCase(
            "ng-run-uses-bracket-event-access",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github['event'].pull_request.title }}"
            """,
            ["\"github.event.pull_request.title\" is potentially untrusted"]),
            new RuleCase(
            "ok-run-uses-github-event-number-not-leaf",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.number }}"
            """,
            []),
            new RuleCase(
            "ng-run-uses-github-head-ref",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.head_ref }}"
            """,
            ["\"github.head_ref\" is potentially untrusted"]),
            new RuleCase(
            "ok-safe-function-contains-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ contains(github.event.issue.title, 'bug') }}"
            """,
            []),
            new RuleCase(
            "ok-safe-function-startswith-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ startsWith(github.event.pull_request.head.ref, 'feature/') }}"
            """,
            []),
            new RuleCase(
            "ng-unsafe-function-format-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', github.event.issue.title) }}"
            """,
            ["\"github.event.issue.title\" is potentially untrusted"]),
            new RuleCase(
            "ng-github-script-with-untrusted-input",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
            new RuleCase(
            "ok-github-script-with-safe-expression",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.ref }}')
            """,
            []),
            new RuleCase(
            "ok-action-input-not-github-script",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/stale@v9
                          with:
                            stale-pr-message: ${{ github.event.pull_request.title }} was closed
            """,
            []),
            new RuleCase(
            "ng-run-with-object-filter-untrusted",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ toJSON(github.event.*.body) }}'
            """,
            ["is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }


    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PerReferenceReporting_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-single-untrusted-reference-names-path",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}"
            """,
            ["\"github.event.head_commit.message\" is potentially untrusted"]),
            new RuleCase(
            "ng-nested-untrusted-reports-all-three",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """,
            [
                "\"github.event.pages.*.page_name\" is potentially untrusted",
                "\"github.event.commits.*.author.name\" is potentially untrusted",
                "\"github.event.issue.title\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-two-expressions-in-one-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.head_commit.message }}" and "${{ github.head_ref }}"
            """,
            [
                "\"github.event.head_commit.message\" is potentially untrusted",
                "\"github.head_ref\" is potentially untrusted",
            ]),
            new RuleCase(
            "ng-github-script-names-path",
            """
            on: issues
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/github-script@v7
                          with:
                            script: console.log('${{ github.event.head_commit.author.name }}')
            """,
            ["\"github.event.head_commit.author.name\" is potentially untrusted"]),
        };

        await AssertRuleCases(new TemplateInjectionRule(), "template-injection", cases);
    }


    [Test]
    public async Task RuleRegression_TemplateInjectionRule_PositionPrecision()
    {
        // actionlint expects 6:41 for: echo "Checking commit '${{ github.event.head_commit.message }}'"
        // Col 41 = start of "github" inside the expression body
        var yaml = NormalizeYaml("""
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "Checking commit '${{ github.event.head_commit.message }}'"
            """);
        using var result = new LintEngine([new TemplateInjectionRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "position-test.yml");
        var diagnostics = result.Diagnostics.Where(x => x.RuleId == "template-injection").ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        await Assert.That(diagnostics[0].Message).Contains("github.event.head_commit.message");

        // The untrusted reference starts at the "g" of "github" inside the expression
        var line6 = yaml.Split('\n')[5]; // 0-based index for line 6
        var expectedCol = line6.IndexOf("github.event.head_commit.message", StringComparison.Ordinal) + 1; // 1-based
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(6);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(expectedCol);
    }


    [Test]
    public async Task RuleRegression_TemplateInjectionRule_NestedUntrustedPositions()
    {
        // actionlint expects 7:23, 7:42, 7:63 for nested untrusted references
        var yaml = NormalizeYaml("""
            name: Test
            on: pull_request
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ github.event.pages[github.event.commits[github.event.issue.title].author.name].page_name }}
            """);
        using var result = new LintEngine([new TemplateInjectionRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "nested-test.yml");
        var diagnostics = result.Diagnostics
            .Where(x => x.RuleId == "template-injection")
            .OrderBy(x => x.Location.StartColumn)
            .ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(3);

        // All on line 7
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[1].Location.StartLine).IsEqualTo(7);
        await Assert.That(diagnostics[2].Location.StartLine).IsEqualTo(7);

        // Check messages name correct paths
        await Assert.That(diagnostics[0].Message).Contains("github.event.pages.*.page_name");
        await Assert.That(diagnostics[1].Message).Contains("github.event.commits.*.author.name");
        await Assert.That(diagnostics[2].Message).Contains("github.event.issue.title");

        // Verify column positions
        var line7 = yaml.Split('\n')[6]; // 0-based for line 7
        var col1 = line7.IndexOf("github.event.pages[", StringComparison.Ordinal) + 1;
        var col2 = line7.IndexOf("github.event.commits[", StringComparison.Ordinal) + 1;
        var col3 = line7.IndexOf("github.event.issue.title", StringComparison.Ordinal) + 1;
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(col1);
        await Assert.That(diagnostics[1].Location.StartColumn).IsEqualTo(col2);
        await Assert.That(diagnostics[2].Location.StartColumn).IsEqualTo(col3);
    }
}
