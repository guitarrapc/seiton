using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;
using Seiton.Core.Parsing;

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
            // --- Phase 1: AND conjunction with non-spoofable context suppresses diagnostic ---
            new RuleCase(
            "ok-actor-with-user-login-conjunction",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-actor-with-user-login-conjunction-and-extra",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]' && github.repository == github.event.pull_request.head.repo.full_name
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-actor-id-with-user-id-conjunction",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor_id == '49699333' && github.event.pull_request.user.id == '49699333'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "warning-actor-with-user-login-different-literal",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'renovate[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context"]),
            new RuleCase(
            "ok-step-actor-with-user-login-conjunction",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.actor == 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]'
                          run: echo test
            """,
            []),
        };

        await AssertRuleCases(new BotConditionsRule(), "bot-conditions", cases);
    }

    // --- Phase 2: != operator emits info severity instead of warning ---
    [Test]
    public async Task BotConditionsRule_NotEqual_EmitsInfoSeverity()
    {
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor != 'dependabot[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-ne-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
        await Assert.That(botDiags[0].Message).Contains("spoofable context");
    }

    [Test]
    public async Task BotConditionsRule_Equal_EmitsWarningSeverity()
    {
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task BotConditionsRule_TriggeringActorNotEqual_EmitsInfoSeverity()
    {
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.triggering_actor != 'renovate[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-ne-triggering-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    }
}
