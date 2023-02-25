
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace StaticCs.Collections;

public static class EqArray
{
    public static EqArray<T> ToEq<T>(this ImmutableArray<T> array) => new(array);
    public static EqArray<T> Create<T>(params T[] array) => ImmutableArray.Create(array).ToEq();
}

public readonly record struct EqArray<T>(ImmutableArray<T> Array) : IReadOnlyCollection<T>
{
    public static readonly EqArray<T> Empty = ImmutableArray<T>.Empty.ToEq();

    public int Length => Array.Length;

    int IReadOnlyCollection<T>.Count => Array.Length;

    public bool Equals(EqArray<T> other) => Array.SequenceEqual(other.Array);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Array).GetEnumerator();

    public override int GetHashCode()
    {
        return Array.Aggregate(0, (acc, item) => HashCode.Combine(acc, item));
    }

    public override string ToString()
    {
        return "[ " + string.Join(", ", Array) + " ]";
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)Array).GetEnumerator();
    }
}
