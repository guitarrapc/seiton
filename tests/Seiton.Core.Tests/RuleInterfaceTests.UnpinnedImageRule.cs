using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_UnpinnedImageRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-docker-uses-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            """,
            []),
            new RuleCase(
            "ng-docker-uses-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: docker://rhysd/actionlint:latest
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-job-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ng-job-container-implicit-latest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/example/app
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-service-container-pinned-digest",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-container-tag",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: postgres:16
                    steps:
                        - run: echo ng
            """,
            ["not pinned by digest"]),
            new RuleCase(
            "ok-non-docker-uses-is-ignored",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
            """,
            []),
        };

        await AssertRuleCases(new UnpinnedImageRule(), "unpinned-image", cases);
    }
}
