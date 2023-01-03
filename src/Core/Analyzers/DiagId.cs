
using System.Collections.Immutable;
using System.Dynamic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using static StaticCs.DiagId;

namespace StaticCs;

internal enum DiagId
{
    ClosedEnumConversion = 1,
    SwitchOnClosedSuppress = 2,

    // Ownership
    LinearResourceMissingDispose = 50,
}

internal static class DiagUtils
{
    private const string DiagPrefix = "STATICCS";

    public static string ToIdString(this DiagId id) => $"{DiagPrefix}{(int)id:D3}";

    public static void ReportDiagnostic(this SyntaxNodeAnalysisContext ctx, DiagId id, Location? location, params object?[]? messageArgs)
        => ctx.ReportDiagnostic(id.Create(location, messageArgs));

    public static void ReportDiagnostic(this SyntaxNodeAnalysisContext ctx, DiagId id, SyntaxNode node, params object?[]? messageArgs)
        => ctx.ReportDiagnostic(id.Create(node.GetLocation(), messageArgs));

    public static Diagnostic Create(this DiagId id, Location? location, params object?[]? messageArgs)
        => Diagnostic.Create(id.GetDescriptor(), location, messageArgs);

    public static DiagnosticDescriptor GetDescriptor(this DiagId id) => s_descriptors[id];

    private static readonly ImmutableDictionary<DiagId, DiagnosticDescriptor> s_descriptors = MakeDescriptors();

    private static ImmutableDictionary<DiagId, DiagnosticDescriptor> MakeDescriptors()
    {
        var builder = ImmutableDictionary.CreateBuilder<DiagId, DiagnosticDescriptor>();

        builder.Add(SwitchOnClosedSuppress, new DiagnosticDescriptor(
            id: SwitchOnClosedSuppress.ToIdString(),
            title: "Integers cannot be converted to [Closed] enums",
            messageFormat: "Integer conversions to [Closed] enum {0} are disallowed",
            category: "StaticCs",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true));

        builder.Add(LinearResourceMissingDispose, new DiagnosticDescriptor(
            id: LinearResourceMissingDispose.ToIdString(),
            title: "Linear resource is not disposed",
            messageFormat: "Type {0} is marked as a linear resource, but it is not disposed and ownership is not transferred.",
            category: "StaticCs",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true));

        return builder.ToImmutable();
    }
}

internal static class AnalyzerConfig
{
    public const string KeyPrefix = "static_cs";
}