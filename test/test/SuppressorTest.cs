
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace StaticCs.Tests;

internal class SuppressorTest<TAnalyzer, TVerifier> : CSharpAnalyzerTest<TAnalyzer, TVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TVerifier : IVerifier, new()
{
    public CSharpCompilationOptions CompilationOptions { get; private init; }
        = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
            .WithReportSuppressedDiagnostics(true);

    public SuppressorTest() { }

    private SuppressorTest(SuppressorTest<TAnalyzer, TVerifier> other)
    {
        CompilationOptions = other.CompilationOptions;
    }

    protected override CompilationOptions CreateCompilationOptions() => CompilationOptions;

    protected override CompilationWithAnalyzers CreateCompilationWithAnalyzers(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options, CancellationToken cancellationToken)
        => new CompilationWithAnalyzers(compilation, analyzers, new CompilationWithAnalyzersOptions(options, onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true));

    public SuppressorTest<TAnalyzer, TVerifier> WithCompilationOptions(CSharpCompilationOptions options)
    {
        return new(this) { CompilationOptions = options };
    }
}