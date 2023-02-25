using System;
using System.Collections.Generic;

namespace StaticCs.Collections;

/// <summary>
/// Extension methods are split into two classes in order to avoid restrictions on
/// overloading.
/// </summary>
public static class EnumerableEx1
{
    public static T? FirstOrNull<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        where T : struct
    {
        foreach (var item in source)
        {
            if (predicate(item))
            {
                return item;
            }
        }
        return null;
    }
}

public static class EnumerableEx2
{
    public static T? FirstOrNull<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        where T : class
    {
        foreach (var item in source)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        return null;
    }
}