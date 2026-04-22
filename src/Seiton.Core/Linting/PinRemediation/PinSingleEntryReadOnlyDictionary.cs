using System.Collections;
using System.Collections.Generic;

namespace Seiton.Core.Linting.PinRemediation;

/// <summary>
/// Minimal <see cref="IReadOnlyDictionary{TKey,TValue}"/> with one entry (ordinal key equality).
/// Avoids <see cref="Dictionary{TKey,TValue}"/> bucket/entry allocations for pin-rule metadata.
/// </summary>
internal sealed class PinSingleEntryReadOnlyDictionary : IReadOnlyDictionary<string, string>
{
    private readonly string _key;
    private readonly string _value;

    public PinSingleEntryReadOnlyDictionary(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public int Count => 1;

    public IEnumerable<string> Keys
    {
        get
        {
            yield return _key;
        }
    }

    public IEnumerable<string> Values
    {
        get
        {
            yield return _value;
        }
    }

    public string this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) =>
        string.Equals(_key, key, StringComparison.Ordinal);

    public bool TryGetValue(string key, out string value)
    {
        if (string.Equals(_key, key, StringComparison.Ordinal))
        {
            value = _value;
            return true;
        }

        value = null!;
        return false;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        yield return new KeyValuePair<string, string>(_key, _value);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
