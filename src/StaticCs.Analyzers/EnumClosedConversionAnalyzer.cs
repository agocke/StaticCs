
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StaticCs;

[DiagnosticAnalyzer("C#")]
public sealed class EnumClosedConversionAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor s_descriptor = new DiagnosticDescriptor(
            id: DiagId.ClosedEnumConversion.ToIdString(),
            title: "Integers cannot be converted to [Closed] enums",
            messageFormat: "Integer conversions to [Closed] enum {0} are disallowed",
            category: "StaticCs",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_descriptor);

    public override void Initialize(AnalysisContext ctx)
    {
        ctx.EnableConcurrentExecution();
        ctx.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);
        ctx.RegisterSyntaxNodeAction(ctx =>
        {
            var castSyntax = (CastExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var targetTypeInfo = model.GetTypeInfo(castSyntax.Type);

            if (targetTypeInfo.Type is { TypeKind: TypeKind.Enum } type)
            {
                foreach (var attr in type.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == "StaticCs.ClosedAttribute")
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(s_descriptor, castSyntax.GetLocation(), type));
                    }
                }
            }
        }, SyntaxKind.CastExpression);
    }
}