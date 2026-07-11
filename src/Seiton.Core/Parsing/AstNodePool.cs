using System.Runtime.CompilerServices;

namespace Seiton.Core.Parsing;

/// <summary>
/// Growable object pool for arena-owned AST section nodes (Permissions, Env, Runner, ...).
/// Mirrors the hand-rolled Job/Step pools in <see cref="AstArena"/>: instances are created
/// once, reset on every allocation, and retained across parses up to a capped capacity.
/// Not thread-safe — owned by a single <see cref="AstArena"/>.
/// </summary>
internal struct AstNodePool<T> where T : class, new()
{
    private readonly Action<T> _reset;
    private T?[] _items;
    private int _count;

    public AstNodePool(int initialCapacity, Action<T> reset)
    {
        _reset = reset;
        _items = new T?[initialCapacity];
        _count = 0;
    }

    /// <summary>Returns a pooled or new instance with all fields reset to default.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Alloc()
    {
        if (_count == _items.Length)
        {
            // Math.Max guards the zero-length case (capacity 0 would otherwise stay 0 forever).
            var grown = new T?[Math.Max(_items.Length * 2, 4)];
            Array.Copy(_items, grown, _items.Length);
            _items = grown;
        }

        var obj = _items[_count];
        if (obj is null)
        {
            obj = new T();
            _items[_count] = obj;
        }

        _reset(obj);
        _count++;
        return obj;
    }

    /// <summary>
    /// Resets all allocated nodes (releasing references to the prior AST graph) and caps the
    /// retained capacity so the ThreadStatic arena cache does not keep high-water-mark pools.
    /// Retains at least what the finished parse used: shrinking straight to
    /// <paramref name="maxRetainedCapacity"/> would discard instances the very next parse of
    /// the same document re-allocates (per-parse alloc/free ping-pong). Retention follows the
    /// most recent parse with a one-parse lag, so a small parse still releases a prior peak.
    /// Call from <see cref="AstArena.Dispose"/> before recaching the arena.
    /// </summary>
    public void Release(int maxRetainedCapacity)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_items[i] is { } item)
            {
                _reset(item);
            }
        }

        var retain = Math.Max(maxRetainedCapacity, _count);
        _count = 0;
        if (_items.Length > retain)
        {
            var shrunk = new T?[retain];
            Array.Copy(_items, shrunk, retain);
            _items = shrunk;
        }
    }
}
