using System.Collections.Immutable;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Claims;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StaticCs;

[DiagnosticAnalyzer("C#")]
public sealed class ClosedDeclarationChecker : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_descriptor = new(
        id: DiagId.ClassOrRecordMustBeClosed.ToIdString(),
        title: "[Closed] is only valid on classes/records with closed hierarchies",
        messageFormat: "[Closed] is only valid on abstract classes/records with private constructors",
        category: "StaticCs",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_descriptor);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(
            ctx =>
            {
                var node = (AttributeListSyntax)ctx.Node;
                if (node.Parent is not TypeDeclarationSyntax typeDecl)
                    return;
                SemanticModel? model = null;
                foreach (var attr in node.Attributes)
                {
                    if (!attr.Name.ToFullString().Contains("Closed"))
                        continue;
                    model ??= ctx.SemanticModel;
                    if (
                        IsClosedAttribute(model.GetTypeInfo(attr).Type)
                        && !IsTypeClosed(model.GetDeclaredSymbol(typeDecl))
                    )
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(s_descriptor, attr.GetLocation()));
                    }
                }
            },
            SyntaxKind.AttributeList
        );

        // A type is considered "closed" if it is an enum or it is an abstract class or record with no non-private constructors.
        static bool IsTypeClosed(INamedTypeSymbol? type)
        {
            if (type is null)
            {
                return false;
            }
            if (type is not { TypeKind: TypeKind.Class })
            {
                return false;
            }

            if (!type.IsAbstract)
            {
                return false;
            }

            foreach (var ctor in type.InstanceConstructors)
            {
                // Skip copy constructor
                if (
                    ctor.Parameters.Length == 1
                    && ctor.Parameters[0].Type.Equals(type, SymbolEqualityComparer.Default)
                )
                {
                    continue;
                }
                if (ctor.DeclaredAccessibility != Accessibility.Private)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static bool IsClosedAttribute(ITypeSymbol? type)
    {
        return type
            is {
                Name: "ClosedAttribute",
                ContainingNamespace:
                { Name: "StaticCs", ContainingNamespace: { IsGlobalNamespace: true } }
            };
    }
}
