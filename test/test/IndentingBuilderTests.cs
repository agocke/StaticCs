using System;
using System.Text;
using Xunit;

namespace StaticCs.Tests;

public sealed class IndentingBuilderTests
{
    [Fact]
    public void Constructor_Default_CreatesEmptyBuilder()
    {
        var builder = new IndentingBuilder();
        Assert.Equal("", builder.ToString());
    }

    [Fact]
    public void Constructor_WithString_InitializesWithContent()
    {
        var builder = new IndentingBuilder("Hello, World!");
        Assert.Equal("Hello, World!", builder.ToString());
    }

    [Fact]
    public void Constructor_WithInterpolatedString_InitializesWithContent()
    {
        var name = "World";
        var builder = new IndentingBuilder($"Hello, {name}!");
        Assert.Equal("Hello, World!", builder.ToString());
    }

    [Fact]
    public void Append_SimpleString_AppendsContent()
    {
        var builder = new IndentingBuilder();
        builder.Append("Hello");
        Assert.Equal("Hello", builder.ToString());
    }

    [Fact]
    public void AppendLine_SimpleString_AppendsWithNewline()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Hello");
        var expected = "Hello" + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void AppendLine_MultipleLines_PreservesNewlines()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Line 1");
        builder.AppendLine("Line 2");
        var expected = "Line 1" + Environment.NewLine + "Line 2" + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Append_IndentingBuilder_AppendsContent()
    {
        var builder1 = new IndentingBuilder();
        builder1.Append("Hello");

        var builder2 = new IndentingBuilder();
        builder2.Append(builder1);

        Assert.Equal("Hello", builder2.ToString());
    }

    [Fact]
    public void AppendLine_WithIndentingBuilder_AppendsWithNewline()
    {
        var builder1 = new IndentingBuilder();
        builder1.Append("Inner content");

        var builder2 = new IndentingBuilder();
        builder2.AppendLine(builder1);
        builder2.Append("Next line");

        var expected = "Inner content" + Environment.NewLine + "Next line";
        Assert.Equal(expected, builder2.ToString());
    }

    [Fact]
    public void Indent_IncreasesIndentation()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Level 0");
        builder.Indent();
        builder.AppendLine("Level 1");
        builder.Indent();
        builder.AppendLine("Level 2");

        var expected =
            "Level 0"
            + Environment.NewLine
            + "    Level 1"
            + Environment.NewLine
            + "        Level 2"
            + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Dedent_DecreasesIndentation()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Level 0");
        builder.Indent();
        builder.AppendLine("Level 1");
        builder.Dedent();
        builder.AppendLine("Back to Level 0");

        var expected =
            "Level 0"
            + Environment.NewLine
            + "    Level 1"
            + Environment.NewLine
            + "Back to Level 0"
            + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Indent_Dedent_MultipleLevel_WorksCorrectly()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Level 0");
        builder.Indent();
        builder.AppendLine("Level 1");
        builder.Indent();
        builder.AppendLine("Level 2");
        builder.Indent();
        builder.AppendLine("Level 3");
        builder.Dedent();
        builder.Dedent();
        builder.AppendLine("Level 1");
        builder.Dedent();
        builder.AppendLine("Level 0");

        var expected =
            "Level 0"
            + Environment.NewLine
            + "    Level 1"
            + Environment.NewLine
            + "        Level 2"
            + Environment.NewLine
            + "            Level 3"
            + Environment.NewLine
            + "    Level 1"
            + Environment.NewLine
            + "Level 0"
            + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Append_StringWithNewlines_PreservesIndentation()
    {
        var builder = new IndentingBuilder();
        builder.Indent();
        builder.Append("Line 1\nLine 2\nLine 3");

        var expected =
            "    Line 1" + Environment.NewLine + "    Line 2" + Environment.NewLine + "    Line 3";
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Normalize_RemovesTrailingWhitespace()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Hello   ");
        builder.AppendLine("World\t\t");

        var expected = "Hello" + Environment.NewLine + "World" + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Normalize_ConvertsAllNewlinesToEnvironmentNewline()
    {
        var builder = new IndentingBuilder("Line1\nLine2\r\nLine3");
        var result = builder.ToString();

        Assert.Contains("Line1" + Environment.NewLine, result);
        Assert.Contains("Line2" + Environment.NewLine, result);
        Assert.Contains("Line3", result);
    }

    [Fact]
    public void CompareTo_EqualBuilders_ReturnsZero()
    {
        var builder1 = new IndentingBuilder("Test");
        var builder2 = new IndentingBuilder("Test");

        Assert.Equal(0, builder1.CompareTo(builder2));
    }

    [Fact]
    public void CompareTo_DifferentLengths_ReturnsNonZero()
    {
        var builder1 = new IndentingBuilder("Short");
        var builder2 = new IndentingBuilder("Longer String");

        Assert.True(builder1.CompareTo(builder2) < 0);
        Assert.True(builder2.CompareTo(builder1) > 0);
    }

    [Fact]
    public void CompareTo_DifferentContent_ReturnsNonZero()
    {
        var builder1 = new IndentingBuilder("Apple");
        var builder2 = new IndentingBuilder("Banana");

        Assert.True(builder1.CompareTo(builder2) < 0);
        Assert.True(builder2.CompareTo(builder1) > 0);
    }

    [Fact]
    public void CompareTo_WithNull_ReturnsPositive()
    {
        var builder = new IndentingBuilder("Test");
        Assert.True(builder.CompareTo(null) > 0);
    }

    [Fact]
    public void Equals_SameContent_ReturnsTrue()
    {
        var builder1 = new IndentingBuilder("Test");
        var builder2 = new IndentingBuilder("Test");

        Assert.True(builder1.Equals(builder2));
    }

    [Fact]
    public void Equals_DifferentContent_ReturnsFalse()
    {
        var builder1 = new IndentingBuilder("Test1");
        var builder2 = new IndentingBuilder("Test2");

        Assert.False(builder1.Equals(builder2));
    }

    [Fact]
    public void Equals_WithNull_ReturnsFalse()
    {
        var builder = new IndentingBuilder("Test");
        Assert.False(builder.Equals(null));
    }

    [Fact]
    public void AppendLine_WithIndentingBuilder_WorksCorrectly()
    {
        var builder1 = new IndentingBuilder();
        builder1.Append("Inner content");

        var builder2 = new IndentingBuilder();
        builder2.AppendLine(builder1);

        var expected = "Inner content" + Environment.NewLine;
        Assert.Equal(expected, builder2.ToString());
    }

    [Fact]
    public void AppendLine_WithIndentingBuilder_PreservesIndentation()
    {
        var builder1 = new IndentingBuilder();
        builder1.Append("First");
        builder1.AppendLine("Second");

        var builder2 = new IndentingBuilder();
        builder2.Indent();
        builder2.AppendLine(builder1);
        builder2.Append("After");

        var expected = "    FirstSecond" + Environment.NewLine + Environment.NewLine + "    After";
        Assert.Equal(expected, builder2.ToString());
    }

    [Fact]
    public void InterpolatedString_SimpleValues_WorksCorrectly()
    {
        var builder = new IndentingBuilder();
        var name = "World";
        var number = 42;
        builder.Append($"Hello {name}, the answer is {number}");

        Assert.Equal("Hello World, the answer is 42", builder.ToString());
    }

    [Fact]
    public void InterpolatedString_StartingWithValue_AppliesIndentation()
    {
        var builder = new IndentingBuilder();
        builder.Indent();
        var value = "test";
        builder.Append($"{value} is the value");

        Assert.Equal("    test is the value", builder.ToString());
    }

    [Fact]
    public void InterpolatedString_WithIndentation_WorksCorrectly()
    {
        var builder = new IndentingBuilder();
        builder.Indent();
        var value = "test";
        builder.AppendLine($"Value: {value}");

        var expected = "    Value: test" + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void InterpolatedString_WithMultilineContent_PreservesIndentation()
    {
        var builder = new IndentingBuilder();
        builder.Indent();
        var multiline = "Line1\nLine2";
        builder.Append($"Start\n{multiline}\nEnd");

        // After a formatted value, indentation resets to the original level
        var expected =
            "    Start"
            + Environment.NewLine
            + "Line1"
            + Environment.NewLine
            + "    Line2"
            + Environment.NewLine
            + "    End";
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void EmptyBuilder_ToString_ReturnsEmptyString()
    {
        var builder = new IndentingBuilder();
        Assert.Equal("", builder.ToString());
    }

    [Fact]
    public void ComplexScenario_MixedIndentationAndContent_WorksCorrectly()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("public class Example");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("public void Method()");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("Console.WriteLine(\"Hello\");");
        builder.AppendLine("Console.WriteLine(\"World\");");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");

        var expected =
            "public class Example"
            + Environment.NewLine
            + "{"
            + Environment.NewLine
            + "    public void Method()"
            + Environment.NewLine
            + "    {"
            + Environment.NewLine
            + "        Console.WriteLine(\"Hello\");"
            + Environment.NewLine
            + "        Console.WriteLine(\"World\");"
            + Environment.NewLine
            + "    }"
            + Environment.NewLine
            + "}"
            + Environment.NewLine;
        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void Append_BlankLines_PreservesBlankLines()
    {
        var builder = new IndentingBuilder();
        builder.Indent();
        builder.Append("Line 1\n\nLine 3");

        var result = builder.ToString();
        Assert.Contains("Line 1" + Environment.NewLine, result);
        Assert.Contains("Line 3", result);
    }

    [Fact]
    public void ToString_CalledMultipleTimes_ReturnsConsistentResults()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("Test  ");

        var result1 = builder.ToString();
        var result2 = builder.ToString();

        Assert.Equal(result1, result2);
        Assert.Equal("Test" + Environment.NewLine, result1);
    }

    [Fact]
    public void InterpolatedString_WithNullValue_HandlesGracefully()
    {
        var builder = new IndentingBuilder();
        string? nullValue = null;
        builder.Append($"Value: {nullValue}");

        Assert.Equal("Value: ", builder.ToString());
    }

    [Fact]
    public void MultipleIndents_ThenDedents_MaintainsCorrectLevel()
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("L0");

        for (int i = 1; i <= 5; i++)
        {
            builder.Indent();
            builder.AppendLine($"L{i}");
        }

        for (int i = 4; i >= 0; i--)
        {
            builder.Dedent();
            builder.AppendLine($"Back to L{i}");
        }

        var result = builder.ToString();
        Assert.StartsWith("L0" + Environment.NewLine, result);
        Assert.Contains("                    L5" + Environment.NewLine, result); // 20 spaces (5 * 4)
        Assert.EndsWith("Back to L0" + Environment.NewLine, result);
    }
}
