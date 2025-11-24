// See https://aka.ms/new-console-template for more information

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace StaticCs.Tests;

public class ClosedTests
{
    [Fact]
    public async Task WarningOnOpenEnum()
    {
        var src = """
enum Rgb { Red, Green, Blue }

class C
{
    int M(Rgb rgb) => rgb switch
    {
        Rgb.Red => 0,
        Rgb.Green => 1,
        Rgb.Blue => 2,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(5,27): warning CS8524: The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value. For example, the pattern '(Rgb)3' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8524")
                .WithSpan(5, 27, 5, 33)
                .WithArguments("(Rgb)3")
        );
    }

    [Fact]
    public async Task SuppressedOnClosedEnum()
    {
        var src = """
[StaticCs.Closed]
enum Rgb { Red, Green, Blue }

class C
{
    int M(Rgb rgb) => rgb switch
    {
        Rgb.Red => 0,
        Rgb.Green => 1,
        Rgb.Blue => 2,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(6,27): warning CS8524: The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value. For example, the pattern '(Rgb)3' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8524")
                .WithSpan(6, 27, 6, 33)
                .WithArguments("(Rgb)3")
                .WithIsSuppressed(true)
        );
    }

    [Fact]
    public async Task ExplicitConversionToClosedError()
    {
        var src = """
using System;

[StaticCs.Closed]
enum Rgb { Red, Green, Blue }
class C
{
    void M()
    {
        Rgb rgb = (Rgb)10;
        Console.WriteLine(rgb);
    }
}
""";
        await VerifyDiagnostics<EnumClosedConversionAnalyzer>(
            src,
            // /0/Test0.cs(9,19): error STATICCS002: Integer conversions to [Closed] enum Rgb are disallowed
            ClosedEnumConversion.WithSpan(9, 19, 9, 26).WithArguments("Rgb")
        );
    }

    [Fact]
    public async Task ImplicitConversionToClosedLoophole()
    {
        var src = """
using System;

[StaticCs.Closed]
enum Rgb { Red, Green, Blue }
class C
{
    void M()
    {
        Rgb rgb = 0;
        Console.WriteLine(rgb);
    }
}
""";
        await VerifyDiagnostics<EnumClosedConversionAnalyzer>(src);
    }

    [Fact]
    public async Task ClosedOnNonAbstractClass()
    {
        var src = """
[StaticCs.Closed]
class Base
{ }
""";
        await VerifyDiagnostics<ClosedDeclarationChecker>(
            src,
            // /0/Test0.cs(1,2): error STATICCS003: [Closed] is only valid on abstract classes/records with private constructors
            ClassOrRecordMustBeClosed.WithSpan(1, 2, 1, 17)
        );
    }

    [Fact]
    public async Task ClosedOnAbstractClassWithPublicConstructor()
    {
        var src = """
[StaticCs.Closed]
abstract class Base { }
""";
        await VerifyDiagnostics<ClosedDeclarationChecker>(
            src,
            // /0/Test0.cs(1,2): error STATICCS003: [Closed] is only valid on abstract classes/records with private constructors
            ClassOrRecordMustBeClosed.WithSpan(1, 2, 1, 17)
        );
    }

    [Fact]
    public async Task ClosedOnAbstractClassWithPrivateConstructor()
    {
        var src = """
[StaticCs.Closed]
abstract class Base
{
    private Base() { }
}
""";
        await VerifyDiagnostics<ClosedDeclarationChecker>(src);
    }

    [Fact]
    public async Task ClosedOnAbstractRecordWithPrivateConstructor()
    {
        var src = """
[StaticCs.Closed]
abstract record Base
{
    private Base() { }
}
""";
        await VerifyDiagnostics<ClosedDeclarationChecker>(src);
    }

    [Fact]
    public async Task WarningOnOpenRecord()
    {
        var src = """
abstract record Base
{
    public record A : Base;
    public record B : Base;
}
class C
{
    int M(Base b) => b switch
    {
        Base.A => 0,
        Base.B => 1,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(8,24): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
            DiagnosticResult.CompilerWarning("CS8509").WithSpan(8, 24, 8, 30).WithArguments("_")
        );
    }

    [Fact]
    public async Task SuppressedWarningOnClosedRecord()
    {
        var src = """
[StaticCs.Closed]
abstract record Base
{
    private Base() { }
    public record A : Base;
    public record B : Base;
}
class C
{
    int M(Base b) => b switch
    {
        Base.A => 0,
        Base.B => 1,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(10,24): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8509")
                .WithSpan(10, 24, 10, 30)
                .WithArguments("_")
                .WithIsSuppressed(true)
        );
    }

    [Fact]
    public async Task ClosedRecordMissingCase()
    {
        var src = """
[StaticCs.Closed]
abstract record Base
{
    private Base() { }
    public record A : Base;
    public record B : Base;
}
class C
{
    int M(Base b) => b switch
    {
        Base.A => 0,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(10,24): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
            DiagnosticResult.CompilerWarning("CS8509").WithSpan(10, 24, 10, 30).WithArguments("_")
        );
    }

    [Fact]
    public async Task ClosedRecordDeconstruct()
    {
        var src = """
[StaticCs.Closed]
abstract record Option<T>
{
    private Option() { }
    public record Some(T Value) : Option<T>;
    public record None : Option<T>;
}
class C
{
    int M(Option<int> b) => b switch
    {
        Option<int>.Some(int i) => i,
        Option<int>.None => 1,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(10,31): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8509")
                .WithSpan(10, 31, 10, 37)
                .WithArguments("_")
                .WithIsSuppressed(true)
        );
    }

    [Fact]
    public async Task ClosedRecordRefutable()
    {
        var src = """
[StaticCs.Closed]
abstract record Option<T>
{
    private Option() { }
    public record Some(T Value) : Option<T>;
    public record None : Option<T>;
}
class C
{
    int M(Option<int> b) => b switch
    {
        Option<int>.Some(5) => 5,
        Option<int>.None => 1,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(10,31): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern 'Option<int>.Some(0) { }' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8509")
                .WithSpan(10, 31, 10, 37)
                .WithArguments("Option<int>.Some(0) { }")
        );
    }

    [Fact]
    public async Task ClosedRecordTypeTestWithVariable()
    {
        var src = """
[StaticCs.Closed]
abstract record Base
{
    private Base() { }
    public record A : Base;
    public record B : Base;
}
class C
{
    int M(Base b) => b switch
    {
        Base.A a => a.GetHashCode(),
        Base.B => 1,
    };
}
""";
        await VerifyDiagnostics<ClosedTypeCompletenessSuppressor>(
            src,
            // /0/Test0.cs(10,24): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern '_' is not covered.
            DiagnosticResult
                .CompilerWarning("CS8509")
                .WithSpan(10, 24, 10, 30)
                .WithArguments("_")
                .WithIsSuppressed(true)
        );
    }

    private static readonly DiagnosticResult ClosedEnumConversion = CSharpAnalyzerVerifier<
        EnumClosedConversionAnalyzer,
        XUnitVerifier
    >.Diagnostic(DiagId.ClosedEnumConversion.ToIdString());
    private static readonly DiagnosticResult ClassOrRecordMustBeClosed = CSharpAnalyzerVerifier<
        ClosedDeclarationChecker,
        XUnitVerifier
    >.Diagnostic(DiagId.ClassOrRecordMustBeClosed.ToIdString());

    private Task VerifyDiagnostics<TAnalyzer>(string src, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new SuppressorTest<TAnalyzer>
        {
            TestCode = src,
            CompilerDiagnostics = CompilerDiagnostics.Warnings,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net60,
            TestBehaviors = TestBehaviors.SkipSuppressionCheck,
        };
        test.TestState.Sources.Add(
            File.ReadAllText(
                Path.Combine(CurrentPath(), "../../../src/StaticCs.ContentFiles/ClosedAttribute.cs")
            )
        );
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    private static string CurrentPath([CallerFilePath] string? path = null) =>
        path ?? throw new InvalidOperationException();
}
//using System;
//using StaticCs;
//
//public class Test
//{
//    public int M(RGB rgb) => rgb switch
//    {
//        RGB.Red => 0,
//        RGB.Green => 1,
//        RGB.Blue => 2
//    };
//
//    public void M2()
//    {
//        RGB rgb = (RGB)10;
//        Console.WriteLine(rgb);
//
//        // Conversion hole
//        RGB rgb2 = 0;
//        Console.WriteLine(rgb2);
//    }
//
//    public void M3(RGB rgb)
//    {
//        switch (rgb)
//        {
//            case RGB.Red:
//            case RGB.Blue:
//            case RGB.Green:
//                break;
//        }
//        switch (rgb)
//        {
//            case RGB.Red:
//            case RGB.Blue:
//                break;
//        }
//    }
//}
//
//[Closed]
//public enum RGB
//{
//    Red = 1,
//    Green,
//    Blue
//}
//
