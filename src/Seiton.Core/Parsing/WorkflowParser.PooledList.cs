using System.Buffers;
using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

public static partial class WorkflowParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ArenaList<T> DetachArenaList<T>(ref PooledBuffer<T> buffer, AstArena arena)
    {
        if (buffer.Count == 0)
        {
            buffer.Dispose();
            return default;
        }

        var (array, count) = buffer.DetachArray();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && count < array.Length)
        {
            Array.Clear(array, count, array.Length - count);
        }

        arena.RegisterSliceMapBuffer(array);
        return new ArenaList<T>(array, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ArenaList<T> ArenaListOfOne<T>(T item, AstArena arena)
    {
        var array = ArrayPool<T>.Shared.Rent(1);
        array[0] = item;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && array.Length > 1)
        {
            Array.Clear(array, 1, array.Length - 1);
        }

        arena.RegisterSliceMapBuffer(array);
        return new ArenaList<T>(array, 1);
    }
}
