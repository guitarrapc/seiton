using System.Text;
using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

/// <summary>
/// Regression tests for list ref indexer bounds. The backing stores are shared across
/// all lists in the arena, so an unbounded index would silently return another list's
/// element instead of failing. All list indexers must throw
/// <see cref="ArgumentOutOfRangeException"/> for out-of-range indexes, including on
/// <c>default</c> instances (Count == 0).
/// </summary>
public sealed class ListRefBoundsTests
{
    private static readonly string WorkflowYaml = """
        name: bounds-test
        on:
          push:
            branches: [main]
          schedule:
            - cron: '0 0 * * *'
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo build
          deploy:
            needs: [build]
            runs-on: ubuntu-latest
            steps:
              - run: echo deploy
        """;

    private static ParseResult Parse(string yaml)
        => WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), "bounds.yml");

    [Test]
    public async Task ListIndexers_PastCount_ThrowArgumentOutOfRange()
    {
        using var result = Parse(WorkflowYaml);
        var workflow = result.Workflow;

        workflow.Jobs.TryGetValue("deploy"u8, out var deploy);

        // StringRefList: needs has 1 element; [Count] must not read the adjacent shared-store entry
        var needs = deploy.Needs;
        await Assert.That(needs.Count).IsEqualTo(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = needs[needs.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = needs[-1]);

        // StepRefList
        var steps = deploy.Steps;
        await Assert.That(steps.Count).IsEqualTo(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = steps[steps.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = steps[-1]);

        // EventRefList
        var on = workflow.On;
        await Assert.That(on.Count).IsEqualTo(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = on[on.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = on[-1]);

        // ScheduleRefList
        var schedule = on[1].AsScheduled().Schedules;
        await Assert.That(schedule.Count).IsEqualTo(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = schedule[schedule.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = schedule[-1]);
    }

    [Test]
    public async Task ListIndexers_DefaultInstance_ThrowArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(StringRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(StepRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(EventRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(ScheduleRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(RawYamlRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(CombinationsRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(CombinationEntryRefList)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(WorkflowCallEventInputRefList)[0]);

        await Task.CompletedTask;
    }
}
