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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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
            // OR does NOT suppress: non-spoofable on the other side of OR does not mitigate
            new RuleCase(
            "warning-actor-or-user-login-not-conjoined",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]' || github.event.pull_request.user.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            ["spoofable context"]),
            // != with AND conjunction: fully mitigated, suppress entirely
            new RuleCase(
            "ok-actor-ne-with-user-login-conjunction",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor != 'dependabot[bot]' && github.event.pull_request.user.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            []),
            // Non-bot actor comparisons should never flag
            new RuleCase(
            "ok-actor-equals-non-bot-user",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'octocat'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-triggering-actor-equals-non-bot",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.triggering_actor == 'my-service-account'
                    steps:
                        - run: echo test
            """,
            []),
            // Non-spoofable context alone should never flag
            new RuleCase(
            "ok-user-login-equals-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.user.login == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            []),
            new RuleCase(
            "ok-user-id-equals-known-bot-id",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.event.pull_request.user.id == '49699333'
                    steps:
                        - run: echo test
            """,
            []),
            // Index-style non-spoofable context should not flag
            new RuleCase(
            "ok-index-user-login-equals-bot",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github['event']['pull_request']['user']['login'] == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            []),
            // Phase 3: push-only workflow suppresses != entirely (no alternative, no risk)
            new RuleCase(
            "ok-push-only-actor-ne-suppressed",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor != 'dependabot[bot]'
                    steps:
                        - run: echo test
            """,
            []),
            // Phase 3: push-only workflow suppresses triggering_actor != entirely
            new RuleCase(
            "ok-push-only-triggering-actor-ne-suppressed",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.triggering_actor != 'renovate[bot]'
                    steps:
                        - run: echo test
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
            on: pull_request
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
            on: pull_request
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
            on: pull_request
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

    // --- Phase 3: event-type awareness (non-PR events suppress/downgrade) ---

    [Test]
    public async Task BotConditionsRule_PushOnly_Equal_Suppressed()
    {
        // on: push only → github.event.pull_request.user.login is NOT available
        // == suppressed entirely (user has no alternative)
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
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-push-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BotConditionsRule_PushOnly_NotEqual_Suppressed()
    {
        // on: push only → != has no risk and no alternative → suppress entirely
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
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-push-ne-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BotConditionsRule_MixedEvents_Equal_StaysWarning()
    {
        // on: [push, pull_request] → PR context IS available → stays warning
        var yaml = """
            on: [push, pull_request]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-mixed-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task BotConditionsRule_MixedEvents_NotEqual_StaysInfo()
    {
        // on: [push, pull_request] → PR context IS available → != stays info
        var yaml = """
            on: [push, pull_request]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor != 'dependabot[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-mixed-ne-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    }

    [Test]
    public async Task BotConditionsRule_ScheduleOnly_Equal_Suppressed()
    {
        // on: schedule → no PR context → == suppressed entirely
        var yaml = """
            on:
                schedule:
                    - cron: '0 0 * * *'
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'github-actions[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-schedule-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BotConditionsRule_PullRequestOnly_Equal_StaysWarning()
    {
        // on: pull_request → PR context IS available → == stays warning
        var yaml = """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: github.actor == 'dependabot[bot]'
                    steps:
                        - run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-pr-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(1);
        await Assert.That(botDiags[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task BotConditionsRule_StepLevel_PushOnly_Equal_Suppressed()
    {
        // Step-level with push-only: == suppressed entirely
        var yaml = """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: github.actor == 'dependabot[bot]'
                          run: echo test
            """.Replace("\r\n", "\n");

        using var result = new LintEngine([new BotConditionsRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), "bot-step-push-eq-test.yml");
        var botDiags = result.Diagnostics
            .Where(x => x.RuleId == "bot-conditions")
            .ToArray();
        await Assert.That(botDiags.Length).IsEqualTo(0);
    }
}
