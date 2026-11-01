using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace DazeControl.Core.Model;

/// <summary>
/// Immutable list with value equality, so model records compare by content (round-trip tests, and D-021's
/// "did this flow change" check). Build it with a collection expression: <c>ValueList&lt;Edge&gt; after = [a, b];</c>
/// </summary>
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public readonly struct ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly ImmutableArray<T> _items;

    public ValueList(IEnumerable<T> items) => _items = [.. items];

    private ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();

    public bool Equals(ValueList<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
            hash.Add(item);
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueList<T> left, ValueList<T> right) => left.Equals(right);

    public static bool operator !=(ValueList<T> left, ValueList<T> right) => !left.Equals(right);

    public override string ToString() => "[" + string.Join(", ", Items) + "]";
}

public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}
