using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CsUtil.Analyzer
{
    [DiagnosticAnalyzer("CSharp")]
    public sealed class ExhaustiveSuppressor : DiagnosticSuppressor
    {
        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(new SuppressionDescriptor(
            "SuppressNonExhaustive",
            "CS8524",
            "Type is marked complete and therefore switch is exhaustive"
        ));

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diag in context.ReportedDiagnostics)
            {
                var loc = diag.Location;
                if (loc.IsInSource)
                {
                    var model = context.GetSemanticModel(loc.SourceTree);
                    var node = loc.SourceTree.GetRoot().FindNode(loc.SourceSpan, getInnermostNodeForTie: true);
                    if (node is SwitchExpressionSyntax switchExpr)
                    {
                        var expr = switchExpr.GoverningExpression;
                        var typeInfo = model.GetTypeInfo(expr);
                        if (typeInfo.Type is not null && IsClosedEnum(typeInfo.Type))
                        {
                            context.ReportSuppression(Suppression.Create(SupportedSuppressions[0], diag));
                        }
                    }
                }
            }
        }

        private static bool IsClosedEnum(ITypeSymbol type)
        {
            return type.TypeKind == TypeKind.Enum && HasClosedAttribute(type.GetAttributes());
        }

        private static bool HasClosedAttribute(ImmutableArray<AttributeData> attributes)
        {
            foreach (var attr in attributes)
            {
                if (attr.AttributeClass?.Name == "ClosedAttribute")
                {
                    return true;
                }
            }
            return false;
        }
    }
}