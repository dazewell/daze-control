using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace CtrlAltDaze.Core.Model;

/// <summary>
/// Small immutable string-keyed map that keeps the author's key order (for writing YAML back) and compares by
/// content regardless of order. Lookups are linear: step params hold a handful of keys.
/// </summary>
public readonly struct ValueDictionary<T> : IReadOnlyDictionary<string, T>, IEquatable<ValueDictionary<T>>
{
    private readonly ValueList<KeyValuePair<string, T>> _entries;

    /// <summary>Later duplicates replace earlier ones in place.</summary>
    public ValueDictionary(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var list = new List<KeyValuePair<string, T>>();
        foreach (var entry in entries)
        {
            var index = list.FindIndex(e => e.Key == entry.Key);
            if (index >= 0)
                list[index] = entry;
            else
                list.Add(entry);
        }
        _entries = new(list);
    }

    public int Count => _entries.Count;

    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    public IEnumerable<T> Values => _entries.Select(e => e.Value);

    public T this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value)
    {
        foreach (var entry in _entries)
        {
            if (entry.Key == key)
            {
                value = entry.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, T>>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ValueDictionary<T> other)
    {
        if (Count != other.Count)
            return false;
        foreach (var (key, value) in _entries)
        {
            if (!other.TryGetValue(key, out var otherValue) || !EqualityComparer<T>.Default.Equals(value, otherValue))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ValueDictionary<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var (key, value) in _entries)
            hash ^= HashCode.Combine(key, value); // order-insensitive, like Equals
        return hash;
    }

    public static bool operator ==(ValueDictionary<T> left, ValueDictionary<T> right) => left.Equals(right);

    public static bool operator !=(ValueDictionary<T> left, ValueDictionary<T> right) => !left.Equals(right);

    public override string ToString() => "{" + string.Join(", ", _entries.Select(e => $"{e.Key}: {e.Value}")) + "}";
}
