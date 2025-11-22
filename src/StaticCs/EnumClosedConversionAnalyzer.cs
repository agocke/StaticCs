
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
        ctx.RegisterOperationAction(ctx =>
        {
            var conversion = (IConversionOperation)ctx.Operation;
            
            // Check if the conversion is from an integer type to an enum type
            if (conversion.Type is { TypeKind: TypeKind.Enum } targetType &&
                conversion.Operand.Type?.SpecialType is 
                    SpecialType.System_SByte or
                    SpecialType.System_Byte or
                    SpecialType.System_Int16 or
                    SpecialType.System_UInt16 or
                    SpecialType.System_Int32 or
                    SpecialType.System_UInt32 or
                    SpecialType.System_Int64 or
                    SpecialType.System_UInt64)
            {
                foreach (var attr in targetType.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == "StaticCs.ClosedAttribute")
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(s_descriptor, conversion.Syntax.GetLocation(), targetType));
                    }
                }
            }
        }, OperationKind.Conversion);
    }
}