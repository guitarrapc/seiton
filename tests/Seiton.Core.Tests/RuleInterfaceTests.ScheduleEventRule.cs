using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ScheduleEventRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-valid-cron",
            """
            on:
                schedule:
                    - cron: "*/5 * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-cron-syntax",
            """
            on:
                schedule:
                    - cron: "* * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "invalid", "exactly 5 fields"]),
            new RuleCase(
            "ng-cron-too-frequent",
            """
            on:
                schedule:
                    - cron: "* * * * *"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["runs too frequently", "once per", "once every 5 minutes"]),
            new RuleCase(
            "ng-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Mars/Phobos"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-iana-like-invalid-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Asia/Somewhere"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
            new RuleCase(
            "ng-typo-timezone-did-you-mean",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "Asia/Toky"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid", "did you mean", "Asia/Tokyo"]),
            new RuleCase(
            "ng-empty-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "must not be empty"]),
            new RuleCase(
            "ng-empty-cron",
            """
            on:
                schedule:
                    - cron: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["cron", "must not be empty"]),
            new RuleCase(
            "ng-extremely-long-timezone",
            """
            on:
                schedule:
                    - cron: "0 0 * * *"
                      timezone: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["timezone", "invalid"]),
        };

        await AssertRuleCases(new ScheduleEventRule(), "schedule-event", cases);
    }
}
