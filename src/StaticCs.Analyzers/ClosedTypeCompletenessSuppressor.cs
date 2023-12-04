using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StaticCs;

/// <summary>
/// Checks if a switch expression's target is an enum and if it's marked
/// [Closed]. If so, suppresses the warning about unhandled non-named cases.
/// </summary>
[DiagnosticAnalyzer("C#")]
public class ClosedTypeCompletenessSuppressor : DiagnosticSuppressor
{
    private readonly static SuppressionDescriptor s_enumDescriptor = new SuppressionDescriptor(
        DiagId.SwitchOnClosedSuppress.ToIdString(),
        "CS8524",
        "Enum is marked [Closed]");
    private readonly static SuppressionDescriptor s_classDescriptor = new SuppressionDescriptor(
        DiagId.SwitchOnClosedSuppress.ToIdString(),
        "CS8509",
        "Enum is marked [Closed]");
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(s_enumDescriptor, s_classDescriptor);


    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diag in context.ReportedDiagnostics)
        {
            var location = diag.Location;
            var tree = location.SourceTree!;
            var node = (SwitchExpressionSyntax)tree.GetRoot().FindNode(location.SourceSpan);
            var model = context.GetSemanticModel(tree);
            var switchTypeInfo = model.GetTypeInfo(node.GoverningExpression);
            var type = switchTypeInfo.Type;
            if (type is null)
            {
                continue;
            }

            foreach (var attr in type.GetAttributes())
            {
                if (ClosedDeclarationChecker.IsClosedAttribute(attr.AttributeClass))
                {
                    if (type is { TypeKind: TypeKind.Enum })
                    {
                        context.ReportSuppression(Suppression.Create(s_enumDescriptor, diag));
                        break;
                    }
                    else
                    {
                        // Must be class/record. Need to find all nested sub-types and check if they've all been
                        // matched by the switch expression
                        var subTypes = new List<INamedTypeSymbol>();
                        foreach (var m in type.GetTypeMembers())
                        {
                            if (m.BaseType?.Equals(type, SymbolEqualityComparer.Default) ?? false)
                            {
                                subTypes.Add(m);
                            }
                        }
                        foreach (var arm in node.Arms)
                        {
                            if (arm.Pattern is ConstantPatternSyntax typePattern)
                            {
                                var symbolInfo = model.GetSymbolInfo(typePattern.Expression);
                                if (symbolInfo.Symbol is INamedTypeSymbol namedType)
                                {
                                    subTypes.Remove(namedType);
                                }
                            }
                            else if (arm.Pattern is RecursivePatternSyntax
                            {
                                Type: { } patternTypeSyntax,
                                PositionalPatternClause: PositionalPatternClauseSyntax { Subpatterns: { } subpatterns }
                            })
                            {
                                if (model.GetSymbolInfo(patternTypeSyntax).Symbol is INamedTypeSymbol patternType &&
                                    subTypes.Contains(patternType) &&
                                    IsIrrefutablePositional(subpatterns, patternType, model))
                                {
                                    subTypes.Remove(patternType);
                                }
                            }
                        }

                        if (subTypes.Count == 0)
                        {
                            context.ReportSuppression(Suppression.Create(s_classDescriptor, diag));
                            break;
                        }

                    }
                }
            }
        }
    }

    private static bool IsIrrefutablePositional(
        SeparatedSyntaxList<SubpatternSyntax> subpatterns,
        INamedTypeSymbol patternType,
        SemanticModel model)
    {
        var matchingDeconstructors = patternType
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m is { Name: "Deconstruct", Parameters: { Length: var paramCount } } &&
                        paramCount == subpatterns.Count);
        foreach (var deconstruct in matchingDeconstructors)
        {
            bool matched = true;
            for (int i = 0; i < deconstruct.Parameters.Length; i++)
            {
                if (!IsSubpatternIrrefutable(subpatterns[i].Pattern, deconstruct.Parameters[i].Type, model))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return true;
            }
        }
        return false;
    }

    // Check for an irrefutable match of a positional subpattern against a
    // Deconstruct parameter
    private static bool IsSubpatternIrrefutable(PatternSyntax pattern, ITypeSymbol paramType, SemanticModel model)
    {
        switch (pattern)
        {
            case DiscardPatternSyntax:
            case VarPatternSyntax:
                return true;
            // A type check is irrefutable if the type is the same as the Deconstruct parameter's static type
            case DeclarationPatternSyntax { Type: { } paramTypeSyntax }:
                var deconstructParamType = model.GetTypeInfo(paramTypeSyntax).Type;
                if (paramType.Equals(deconstructParamType, SymbolEqualityComparer.Default))
                {
                    return true;
                }
                break;
        }
        return false;
    }
}
