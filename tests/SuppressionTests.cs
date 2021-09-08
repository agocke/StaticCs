using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CsUtil.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace CsUtil
{
    public class AnalyzerTests
    {
        [Fact]
        public Task TestEnumWithoutClosed()
        {
            var src = @"
using System;
class C
{
    public void M(Rgb color)
    {
        Console.WriteLine(color switch
        {
            Rgb.Red => 0,
            Rgb.Green => 1,
            Rgb.Blue => 2
        });
    }
}
enum Rgb
{
    Red,
    Green,
    Blue
}
class ClosedAttribute : Attribute {}";
            return VerifyDiagnostics(src,
// /0/Test0.cs(7,33): warning CS8524: The switch expression does not handle some values of its input type (it is not exhaustive) involving an unnamed enum value. For example, the pattern '(Rgb)3' is not covered.
DiagnosticResult.CompilerWarning("CS8524").WithSpan(7, 33, 7, 39).WithArguments("(Rgb)3"));
        }

        [Fact]
        public Task TestCompleteEnumWithClosed()
        {
            var src = @"
using System;
class C
{
    public void M(Rgb color)
    {
        Console.WriteLine(color switch
        {
            Rgb.Red => 0,
            Rgb.Green => 1,
            Rgb.Blue => 2
        });
    }
}
[Closed]
enum Rgb
{
    Red,
    Green,
    Blue
}
class ClosedAttribute : Attribute {}";
            return VerifyDiagnostics(src);
        }

        [Fact]
        public Task TestInCompleteEnumWithClosed()
        {
            var src = @"
using System;
class C
{
    public void M(Rgb color)
    {
        Console.WriteLine(color switch
        {
            Rgb.Red => 0,
            Rgb.Green => 1,
        });
    }
}
[Closed]
enum Rgb
{
    Red,
    Green,
    Blue
}
class ClosedAttribute : Attribute {}";
            return VerifyDiagnostics(src,
            // /0/Test0.cs(7,33): warning CS8509: The switch expression does not handle all possible values of its input type (it is not exhaustive). For example, the pattern 'Rgb.Blue' is not covered.
            DiagnosticResult.CompilerWarning("CS8509").WithSpan(7, 33, 7, 39).WithArguments("Rgb.Blue"));
        }

        private Task VerifyDiagnostics(string src, params DiagnosticResult[] diagnostics)
        {
            var test = new CompilerSuppressionVerifier<ExhaustiveSuppressor>()
            {
                CompilerDiagnostics = Microsoft.CodeAnalysis.Testing.CompilerDiagnostics.Warnings,
                TestCode = src
            };
            test.ExpectedDiagnostics.AddRange(diagnostics);
            return test.RunAsync();
        }

        private class CompilerSuppressionVerifier<TSuppressor> : CSharpAnalyzerTest<TSuppressor, XUnitVerifier>
            where TSuppressor : DiagnosticSuppressor, new()
        {
            private static readonly ImmutableArray<SuppressionDescriptor> s_suppressions = new TSuppressor().SupportedSuppressions;

            protected override bool IsCompilerDiagnosticIncluded(Diagnostic diagnostic, CompilerDiagnostics compilerDiagnostics)
            {
                // Skip compiler diagnostics that are suppressed by the suppressor
                return !(diagnostic.IsSuppressed && s_suppressions.Any(s => s.SuppressedDiagnosticId == diagnostic.Id));
            }
        }
    }
}
