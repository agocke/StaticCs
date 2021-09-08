using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StaticCs.Analyzers
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
                        var switchType = model.GetTypeInfo(expr).Type;
                        if (switchType is not null &&
                            IsClosedEnum(switchType) &&
                            ChecksAllCases(switchType, switchExpr.Arms, model))
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

        private static bool ChecksAllCases(ITypeSymbol switchType, SeparatedSyntaxList<SwitchExpressionArmSyntax> arms, SemanticModel model)
        {
            var enumFieldMembers = new HashSet<ISymbol>(switchType.GetMembers().Where(m => m.Kind == SymbolKind.Field),
                SymbolEqualityComparer.Default);
            foreach (var arm in arms)
            {
                if (arm.WhenClause is not null)
                {
                    return false;
                }
                if (arm.Pattern is not ConstantPatternSyntax constantPattern)
                {
                    return false;
                }
                var patternSymbol = model.GetSymbolInfo(constantPattern.Expression).Symbol;
                if (patternSymbol is not null)
                {
                    enumFieldMembers.Remove(patternSymbol);
                }
            }
            return enumFieldMembers.Count == 0;
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