using System.Collections.Immutable;
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
    private readonly static SuppressionDescriptor s_descriptor = new SuppressionDescriptor(
        DiagId.ClosedEnumConversion.ToIdString(),
        "CS8524",
        "Enum is marked [Closed]");
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(s_descriptor);


    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diag in context.ReportedDiagnostics)
        {
            var location = diag.Location;
            var tree = location.SourceTree!;
            var node = (SwitchExpressionSyntax)tree.GetRoot().FindNode(location.SourceSpan);
            var model = context.GetSemanticModel(tree);
            var switchTypeInfo = model.GetTypeInfo(node.GoverningExpression);
            if (switchTypeInfo.Type is { TypeKind: TypeKind.Enum} type)
            {
                foreach (var attr in type.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == "StaticCs.ClosedAttribute")
                    {
                        context.ReportSuppression(Suppression.Create(s_descriptor, diag));
                        break;
                    }
                }
            }
        }
    }
}
