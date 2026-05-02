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

        const int allocatedStepCount = 65;

        // Allocate more steps than the default pool capacity
        var steps = new Step[allocatedStepCount];
        for (var i = 0; i < allocatedStepCount; i++)
        {
            steps[i] = arena.AllocStep();
        }

        // All should be distinct instances (O(n) check via reference-equality set)
        var set = new HashSet<Step>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < allocatedStepCount; i++)
        {
            set.Add(steps[i]);
        }
        await Assert.That(set.Count).IsEqualTo(allocatedStepCount);
    }

    [Test]
    public async Task RegisterSliceMapBuffer_BuffersReturnedOnDispose()
    {
        var source = "ab"u8.ToArray();
        var arena = AstArena.Rent(source);

        // Simulate what the parser does: PooledBuffer.DetachArray() → RegisterSliceMapBuffer
        var buf = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10));
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(1, 1), 20));
        var (entries, count) = buf.DetachArray();
        arena.RegisterSliceMapBuffer(entries);

        // The SliceMap should work correctly with the pooled array
        var map = new SliceMap<int>(entries, count, caseSensitive: true);
        await Assert.That(map.TryGetValue(source, "a"u8, out var va)).IsTrue();
        await Assert.That(va).IsEqualTo(10);
        await Assert.That(map.Count).IsEqualTo(2);

        // Dispose should not throw (buffers returned to pool)
        arena.Dispose();
    }

    [Test]
    public async Task RegisterSliceMapBuffer_MultipleBuffers_AllReturnedOnDispose()
    {
        var source = "abc"u8.ToArray();
        var arena = AstArena.Rent(source);

        // Register multiple buffers of different types
        var buf1 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf1.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 1));
        var (entries1, count1) = buf1.DetachArray();
        arena.RegisterSliceMapBuffer(entries1);

        var buf2 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(1, 1), 2));
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(2, 1), 3));
        var (entries2, count2) = buf2.DetachArray();
        arena.RegisterSliceMapBuffer(entries2);

        // Both should work
        var map1 = new SliceMap<int>(entries1, count1, caseSensitive: true);
        var map2 = new SliceMap<int>(entries2, count2, caseSensitive: true);
        await Assert.That(map1.Count).IsEqualTo(1);
        await Assert.That(map2.Count).IsEqualTo(2);

        // Dispose should not throw
        arena.Dispose();
    }

    [Test]
    public async Task RegisterSliceMapBuffer_ResetForSource_ClearsRegisteredBuffers()
    {
        var source = "ab"u8.ToArray();
        var arena = AstArena.Rent(source);

        var buf = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 10));
        var (entries, _) = buf.DetachArray();
        arena.RegisterSliceMapBuffer(entries);
        arena.Dispose();

        // Re-rent: should reuse arena (from ThreadStatic cache), registered buffers should be cleared
        var source2 = "xy"u8.ToArray();
        arena = AstArena.Rent(source2);

        // Register a new buffer with the fresh arena — should not fail
        var buf2 = new PooledBuffer<SliceMap<int>.Entry>(8);
        buf2.Add(new SliceMap<int>.Entry(new Utf8Slice(0, 1), 99));
        var (entries2, count2) = buf2.DetachArray();
        arena.RegisterSliceMapBuffer(entries2);

        var map = new SliceMap<int>(entries2, count2, caseSensitive: true);
        await Assert.That(map.TryGetValue(source2, "x"u8, out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(99);

        arena.Dispose();
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
