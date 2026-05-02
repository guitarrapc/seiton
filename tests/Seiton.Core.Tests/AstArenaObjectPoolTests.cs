using Seiton.Core.Parsing;
using Seiton.Core.Parsing.Ast;

namespace Seiton.Core.Tests;

public sealed class AstArenaObjectPoolTests
{
    [Test]
    public async Task AllocJob_ReturnsJobWithDefaultFields()
    {
        var source = "name: test"u8.ToArray();
        using var arena = AstArena.Rent(source);

        var job = arena.AllocJob();

        await Assert.That(job).IsNotNull();
        await Assert.That(job.Id.HasValue).IsFalse();
        await Assert.That(job.Name.HasValue).IsFalse();
        await Assert.That(job.Needs).IsNull();
        await Assert.That(job.Steps).IsNull();
        await Assert.That(job.RunsOn).IsNull();
    }

    [Test]
    public async Task AllocStep_ReturnsStepWithDefaultFields()
    {
        var source = "name: test"u8.ToArray();
        using var arena = AstArena.Rent(source);

        var step = arena.AllocStep();

        await Assert.That(step).IsNotNull();
        await Assert.That(step.Id.HasValue).IsFalse();
        await Assert.That(step.Name.HasValue).IsFalse();
        await Assert.That(step.Env).IsNull();
    }

    [Test]
    public async Task AllocExecRun_ReturnsExecRunWithDefaultFields()
    {
        var source = "name: test"u8.ToArray();
        using var arena = AstArena.Rent(source);

        var exec = arena.AllocExecRun();

        await Assert.That(exec).IsNotNull();
        await Assert.That(exec.Kind).IsEqualTo(StepExecKind.Run);
        await Assert.That(exec.Run.HasValue).IsFalse();
        await Assert.That(exec.Shell.HasValue).IsFalse();
    }

    [Test]
    public async Task AllocExecAction_ReturnsExecActionWithDefaultFields()
    {
        var source = "name: test"u8.ToArray();
        using var arena = AstArena.Rent(source);

        var exec = arena.AllocExecAction();

        await Assert.That(exec).IsNotNull();
        await Assert.That(exec.Kind).IsEqualTo(StepExecKind.Action);
        await Assert.That(exec.Uses.HasValue).IsFalse();
        await Assert.That(exec.Inputs).IsNull();
    }

    [Test]
    public async Task AllocJob_AfterDisposeAndRent_ReusesObjectWithResetFields()
    {
        var source = "name: test"u8.ToArray();
        var arena = AstArena.Rent(source);
        var job1 = arena.AllocJob();
        job1.Name = arena.AddString(new Utf8Slice(0, 4), false, default);
        arena.Dispose();

        // Re-rent (should reuse cached arena with pooled objects)
        var source2 = "x: y"u8.ToArray();
        arena = AstArena.Rent(source2);
        var job2 = arena.AllocJob();

        // Should be same instance reused, but with reset fields
        await Assert.That(job2).IsSameReferenceAs(job1);
        await Assert.That(job2.Name.HasValue).IsFalse();
        await Assert.That(job2.Steps).IsNull();
        await Assert.That(job2.Needs).IsNull();
        arena.Dispose();
    }

    [Test]
    public async Task AllocStep_GrowsBeyondInitialCapacity()
    {
        var source = "name: test"u8.ToArray();
        using var arena = AstArena.Rent(source);

        // Allocate more steps than the default pool capacity
        var steps = new Step[50];
        for (var i = 0; i < 50; i++)
        {
            steps[i] = arena.AllocStep();
        }

        // All should be distinct instances (O(n) check via reference-equality set)
        var set = new HashSet<Step>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 50; i++)
        {
            set.Add(steps[i]);
        }
        await Assert.That(set.Count).IsEqualTo(50);
    }

    [Test]
    public async Task AllocStep_AfterDisposeAndRent_ReusesAllPooledObjects()
    {
        var source = "name: test"u8.ToArray();
        var arena = AstArena.Rent(source);

        // Allocate 5 steps, set some fields
        var originalSteps = new Step[5];
        for (var i = 0; i < 5; i++)
        {
            originalSteps[i] = arena.AllocStep();
            originalSteps[i].Name = arena.AddString(new Utf8Slice(0, 4), false, default);
        }
        arena.Dispose();

        // Re-rent and allocate same number
        var source2 = "x: y"u8.ToArray();
        arena = AstArena.Rent(source2);
        for (var i = 0; i < 5; i++)
        {
            var step = arena.AllocStep();
            await Assert.That(step).IsSameReferenceAs(originalSteps[i]);
            await Assert.That(step.Name.HasValue).IsFalse(); // Reset
            await Assert.That(step.Env).IsNull(); // Reset
        }
        arena.Dispose();
    }
}
