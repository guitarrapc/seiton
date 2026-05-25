using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_GlobPatternRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-branch-and-path-glob",
            """
            on:
                pull_request:
                    branches: [main, release/**]
                    paths: ['src/**', '!docs/**']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on:
                push:
                    branches:
                        - ${{ github.ref_name }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-triple-star-in-branches",
            """
            on:
                push:
                    branches: ['feature/***']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "consecutive '*'"]),
            new RuleCase(
            "ng-unclosed-class-in-paths-ignore",
            """
            on:
                pull_request:
                    paths-ignore:
                        - 'src/[abc'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }


    [Test]
    public async Task RuleRegression_GlobPatternRule_Syntax_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-reversed-bracket-range",
            """
            on:
                push:
                    branches: ['feature/[z-a]']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["start of range", "is larger than end of range"]),
            new RuleCase(
            "ng-dot-dot-path-segment",
            """
            on:
                push:
                    paths: ['src/../etc/passwd']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-caret-char-in-branch-pattern",
            """
            on:
                push:
                    branches: ['^foo-']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["character '^' is invalid"]),
            new RuleCase(
            "ng-star-plus-in-tag-pattern",
            """
            on:
                push:
                    tags: ['v*+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["unexpected character '+' after '*'"]),
            new RuleCase(
            "ng-dot-path-segment",
            """
            on:
                push:
                    paths: ['./foo/bar.txt']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["'.' and '..' are not allowed"]),
            new RuleCase(
            "ok-valid-bracket-range",
            """
            on:
                push:
                    branches: ['release/v[0-9].*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-backslash-regex-escape-in-tags",
            """
            on:
                push:
                    tags: ['v\d+']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names", "can be escaped"]),
            new RuleCase(
            "ng-trailing-backslash-in-branches",
            """
            on:
                push:
                    branches: ["feature\\"]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "trailing backslash"]),
            new RuleCase(
            "ok-valid-backslash-escape-star",
            """
            on:
                push:
                    branches: ['feature/\*']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-lone-bang-in-tags",
            """
            on:
                push:
                    tags: ['!']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
            new RuleCase(
            "ng-glob-errors-detected-after-null-entry-in-paths",
            """
            on:
                push:
                    paths:
                        -
                        - '!'
                        - '  foo'
                        - '.'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !", "leading and trailing spaces", "'.' and '..' are not allowed"]),
            new RuleCase(
            "ng-leading-space-in-paths",
            """
            on:
                push:
                    paths: ['  foo']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-trailing-space-in-paths",
            """
            on:
                push:
                    paths: ['foo  ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ng-space-only-in-paths",
            """
            on:
                push:
                    paths: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["leading and trailing spaces"]),
            new RuleCase(
            "ok-space-in-branches-is-ref-error",
            """
            on:
                push:
                    branches: [' ']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["invalid for branch and tag names"]),
            new RuleCase(
            "ng-ref-starts-with-slash",
            """
            on:
                push:
                    tags: ['/v1.0']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not start with /"]),
            new RuleCase(
            "ng-ref-ends-with-slash",
            """
            on:
                push:
                    branches: ['feature/']
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
            """,
            ["ref name must not end with /"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }


    [Test]
    public async Task RuleRegression_GlobPatternRule_SnapshotVersion_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v[0-'
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ok-valid-snapshot-version",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    snapshot:
                        image-name: my-image
                        version: 'v1.2.3'
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }


    [Test]
    public async Task RuleRegression_GlobPatternRule_ImageVersionVersions_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-unclosed-bracket-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - 'v[0-'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["invalid glob pattern", "missing ]"]),
            new RuleCase(
            "ng-lone-bang-in-image-version-versions",
            """
            on:
                image_version:
                    versions:
                        - '!'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["at least one character must follow !"]),
        };

        await AssertRuleCases(new GlobPatternRule(), "glob-pattern", cases);
    }




























































    // Duplicate job ID in needs array

    // regression: cycle diagnostics should report at the needs value position (actionable)
    // with a cycle path in the message for clarity


    // OS-specific shell validation


    // Runner label — matrix-expanded runs-on

    // Runner label conflict

    // Runner label — matrix conflict with static labels











    // Workflow call input default validation




    // Glob pattern syntax validation



    [Test]
    public async Task GlobPatternRule_BlockScalarTrailingNewline_ReportsAtIndicatorLine()
    {
        // MISS #6: block scalar `- |\n  foo.txt` should report at the `|` indicator line,
        // not at the content line.
        // Layout:
        //   line 5: "      - |"           <- `|` at col 9
        //   line 6: "        foo.txt"     <- content at col 9
        // actionlint expects line 5, col 9
        var yaml = "on:\n  push:\n    paths:\n      - 'ok'\n      - |\n        foo.txt\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";
        using var result = new LintEngine([new GlobPatternRule()]).Check(

            System.Text.Encoding.UTF8.GetBytes(yaml), "test.yaml");
        var diagnostics = result.Diagnostics.Where(d => d.RuleId == "glob-pattern" && d.Message.Contains("leading and trailing spaces")).ToArray();

        await Assert.That(diagnostics).Count().IsEqualTo(1);
        // Must report at block scalar indicator line, not content line
        await Assert.That(diagnostics[0].Location.StartLine).IsEqualTo(5);
        await Assert.That(diagnostics[0].Location.StartColumn).IsEqualTo(9);
    }
}
