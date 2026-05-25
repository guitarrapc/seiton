using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_BotConditionsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "warning-actor-dependabot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-actor-id-known-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == '49699333'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-actor-id-known-bot-number",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == 49699333
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "ok-actor-id-unknown",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == '123456789'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "warning-actor-github-actions-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'github-actions[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-triggering-actor-renovate",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.triggering_actor != 'renovate[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-pr-sender-login",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.sender.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "ok-event-name-push",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event_name == 'push'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-actor-not-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'my-user'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "warning-pr-sender-id-known-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.sender.id == '41898282'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-step-actor-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.actor == 'dependabot[bot]'
                          run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            // --- Index-style context tests (zizmor parity) ---
            new RuleCase(
            "warning-index-actor-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['actor'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-actor-case-insensitive",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['ACTOR'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-actor-id-known-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['ACTOR_ID'] == 49699333
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-mixed-index-pr-sender-login",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event['pull_request'].sender['login'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
            new RuleCase(
            "warning-index-pr-sender-id-known-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['event']['pull_request']['sender']['id'] == '41898282'
                    steps:
                        - run: echo test
            """,
            ["spoofable context", "pull_request.user.login"]),
        };

        await AssertRuleCases(new BotConditionsRule(), "bot-conditions", cases);
    }
}
