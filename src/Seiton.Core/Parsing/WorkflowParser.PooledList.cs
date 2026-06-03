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
        arena.RegisterSliceMapBuffer(array);
        return new ArenaList<T>(array, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ArenaList<T> ArenaListOfOne<T>(T item, AstArena arena)
    {
        var array = ArrayPool<T>.Shared.Rent(1);
        array[0] = item;
        arena.RegisterSliceMapBuffer(array);
        return new ArenaList<T>(array, 1);
    }
}
