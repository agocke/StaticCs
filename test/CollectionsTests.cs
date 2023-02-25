
using System.Collections.Generic;
using StaticCs.Collections;
using Xunit;

namespace StaticCs.Tests;

public class CollectionsTests
{
    private enum RGB
    {
        Red, Green, Blue
    }

    [Fact]
    public void FirstOrNull()
    {
        var intList = new List<int> { 1, 2, 3 };
        var found = intList.FirstOrNull(i => i == 2);
        Assert.Equal(2, found);
        var notFound = intList.FirstOrNull(i => i == 5);
        Assert.Null(notFound);

        var refList = new List<string> { "a", "b", "c" };
        var foundRef = refList.FirstOrNull(s => s == "b");
        Assert.Equal("b", foundRef);
        var notFoundRef = refList.FirstOrNull(s => s == "d");
        Assert.Null(notFoundRef);

        var enumList = new List<RGB> { RGB.Red, RGB.Green, RGB.Blue };
        var foundEnum = enumList.FirstOrNull(e => e == RGB.Green);
        Assert.Equal(RGB.Green, foundEnum);
        var notFoundEnum = enumList.FirstOrNull(e => e == (RGB)5);
        Assert.Null(notFoundEnum);
    }

    [Fact]
    public void EqArrayTest()
    {
        var intArr1 = EqArray.Create(1, 2, 3);
        var intArr2 = EqArray.Create(1, 2, 3);
        Assert.Equal(intArr1, intArr2);
        Assert.Equal(intArr1.GetHashCode(), intArr2.GetHashCode());

        var stringArr1 = EqArray.Create("a", "b", "c");
        var stringArr2 = EqArray.Create("a", "b", "c");
        Assert.Equal(stringArr1, stringArr2);
        Assert.Equal(stringArr1.GetHashCode(), stringArr2.GetHashCode());

        var nested1 = EqArray.Create(stringArr1, stringArr1);
        var nested2 = EqArray.Create(stringArr2, stringArr2);
        Assert.Equal(nested1, nested2);
        Assert.Equal(nested1.GetHashCode(), nested2.GetHashCode());

        // Reversed
        var nested3 = EqArray.Create(stringArr2, stringArr1);
        var nested4 = EqArray.Create(stringArr1, stringArr2);
        Assert.NotEqual(nested3, nested4);
        Assert.NotEqual(nested3.GetHashCode(), nested4.GetHashCode());
    }
}
