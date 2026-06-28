using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace CsSig;

/// <summary>
/// A small immutable array wrapper that provides structural (element-wise) equality, so it can be
/// used as a member of <c>record</c> types and still participate correctly in value equality.
/// </summary>
internal readonly struct EqArray<T>(ImmutableArray<T> _array) : IEquatable<EqArray<T>>
    where T : IEquatable<T>
{
    public ImmutableArray<T> Array => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    public int Length => Array.Length;

    public T this[int index] => Array[index];

    public ImmutableArray<T>.Enumerator GetEnumerator() => Array.GetEnumerator();

    public bool Equals(EqArray<T> other)
    {
        var left = Array;
        var right = other.Array;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EqArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Array)
        {
            hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
        }

        return hash;
    }

    public static EqArray<T> From(IEnumerable<T> items) => new(items.ToImmutableArray());
}
