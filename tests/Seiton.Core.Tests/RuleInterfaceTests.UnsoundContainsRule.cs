using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UnsoundContainsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "error-contains-github-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('refs/heads/main refs/heads/develop', github.ref)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "ok-fromjson-array-contains",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains(fromJSON('["main", "develop"]'), github.ref)
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "info-non-controllable-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('push pull_request', github.event_name)
                    steps:
                        - run: echo test
            """,
            ["context reference", "substring bypass"]),
            new RuleCase(
            "error-or-contains-head-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: false || contains('main,develop', github.head_ref)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-not-contains-base-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: "!contains('main|develop', github.base_ref)"
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "ok-no-contains",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.ref == 'refs/heads/main'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "error-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: contains('expected_value', env.MY_VAR)
                          run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-inputs-context",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: string
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('main develop', inputs.target)
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-fenced-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ contains('refs/heads/main', github.ref) }}
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            // --- Index-style context tests (zizmor parity) ---
            new RuleCase(
            "error-index-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: contains('expected_value', env['MY_VAR'])
                          run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "error-index-github-ref",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('refs/heads/main', github['ref'])
                    steps:
                        - run: echo test
            """,
            ["user-controllable context", "substring bypass"]),
            new RuleCase(
            "info-index-non-controllable",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: contains('push pull_request', github['event_name'])
                    steps:
                        - run: echo test
            """,
            ["context reference", "substring bypass"]),
        };

        await AssertRuleCases(new UnsoundContainsRule(), "unsound-contains", cases);
    }
}
