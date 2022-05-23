// See https://aka.ms/new-console-template for more information

using System;
using StaticCs;

public class Test
{
    public int M(RGB rgb) => rgb switch
    {
        RGB.Red => 0,
        RGB.Green => 1,
        RGB.Blue => 2
    };

    public void M2()
    {
        RGB rgb = (RGB)10;
        Console.WriteLine(rgb);

        // Conversion hole
        RGB rgb2 = 0;
        Console.WriteLine(rgb2);
    }

    public void M3(RGB rgb)
    {
        switch (rgb)
        {
            case RGB.Red:
            case RGB.Blue:
            case RGB.Green:
                break;
        }
        switch (rgb)
        {
            case RGB.Red:
            case RGB.Blue:
                break;
        }
    }
}

[Closed]
public enum RGB
{
    Red = 1,
    Green,
    Blue
}
