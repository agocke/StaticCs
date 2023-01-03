
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using static StaticCs.DiagId;

namespace StaticCs;

[DiagnosticAnalyzer("C#")]
public sealed class EnumClosedConversionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(SwitchOnClosedSuppress.GetDescriptor());

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
                        ctx.ReportDiagnostic(SwitchOnClosedSuppress, castSyntax, type);
                    }
                }
            }
        }, SyntaxKind.CastExpression);
    }
}