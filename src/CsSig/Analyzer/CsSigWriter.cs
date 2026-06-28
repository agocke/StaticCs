using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using StaticCs;

namespace CsSig;

/// <summary>
/// Generates the text of a <c>.cssig</c> file from a compilation's public API surface: the inverse
/// of <see cref="ApiSurface"/>. Every externally visible member is emitted as a body-less C#
/// declaration, grouped by namespace and nesting, so that feeding the result back through
/// <see cref="CsSigAnalyzer"/> produces no diagnostics.
/// </summary>
/// <remarks>
/// Only explicitly declared members are emitted; implicit members (default constructors, record
/// members, …) are intentionally omitted because the same declaration causes the compiler to
/// synthesize identical members on both the project and the generated signature.
/// </remarks>
public static class CsSigWriter
{
    // Full signature of a non-type member, body-less. Types are fully qualified so the generated
    // file needs no using directives. SymbolDisplay omits non-signature modifiers such as `unsafe`
    // and `async`; the few it still emits (`volatile`, `required`) are scrubbed afterwards.
    private static readonly SymbolDisplayFormat s_memberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeExtensionThis
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
    );

    // A bare type reference, fully qualified, for return/parameter/underlying types.
    private static readonly SymbolDisplayFormat s_typeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
    );

    /// <summary>Generates the <c>.cssig</c> text for <paramref name="compilation"/>'s public API.</summary>
    public static string Write(Compilation compilation) => Write(compilation.Assembly);

    /// <summary>Generates the <c>.cssig</c> text for <paramref name="assembly"/>'s public API.</summary>
    public static string Write(IAssemblySymbol assembly) => Write(assembly, topLevelKeys: null);

    /// <summary>
    /// Generates the <c>.cssig</c> text for the subset of <paramref name="assembly"/>'s public API
    /// whose <em>top-level</em> types are named in <paramref name="topLevelKeys"/> (see
    /// <see cref="TopLevelKey"/>). When <paramref name="topLevelKeys"/> is <see langword="null"/>
    /// the entire surface is written. The code fix uses this to (re)generate a single signature
    /// file that owns a particular set of top-level types.
    /// </summary>
    public static string Write(IAssemblySymbol assembly, ISet<string>? topLevelKeys)
    {
        var byNamespace = new SortedDictionary<string, List<INamedTypeSymbol>>(
            StringComparer.Ordinal
        );
        foreach (var type in TopLevelTypes(assembly.GlobalNamespace))
        {
            if (!ApiSurface.IsTrackedApi(type))
            {
                continue;
            }

            if (topLevelKeys is not null && !topLevelKeys.Contains(TopLevelKey(type)))
            {
                continue;
            }

            var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n
                ? n.ToDisplayString()
                : string.Empty;
            if (!byNamespace.TryGetValue(ns, out var list))
            {
                byNamespace[ns] = list = new List<INamedTypeSymbol>();
            }

            list.Add(type);
        }

        var builder = new IndentingBuilder();
        var first = true;
        foreach (var entry in byNamespace)
        {
            if (!first)
            {
                builder.AppendLine("");
            }

            first = false;

            if (entry.Key.Length == 0)
            {
                foreach (var type in Sorted(entry.Value))
                {
                    WriteType(builder, type);
                }
            }
            else
            {
                builder.AppendLine($"namespace {entry.Key}");
                builder.AppendLine("{");
                builder.Indent();
                foreach (var type in Sorted(entry.Value))
                {
                    WriteType(builder, type);
                }

                builder.Dedent();
                builder.AppendLine("}");
            }
        }

        return builder.ToString();
    }

    private static void WriteType(IndentingBuilder builder, INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Delegate)
        {
            builder.AppendLine(DelegateDeclaration(type) + ";");
            return;
        }

        builder.AppendLine(TypeHeader(type));
        builder.AppendLine("{");
        builder.Indent();

        if (type.TypeKind == TypeKind.Enum)
        {
            foreach (
                var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue)
            )
            {
                builder.AppendLine(FormatMember(field));
            }
        }
        else
        {
            foreach (var member in Sorted(VisibleMembers(type)))
            {
                builder.AppendLine(FormatMember(member));
            }

            var nestedTypes = type.GetMembers()
                .OfType<INamedTypeSymbol>()
                .Where(ApiSurface.IsTrackedApi)
                .ToList();

            // Extension blocks are nested types with an unspeakable name; emit them as
            // `extension(Receiver) { ... }` ordered by their header so the output is deterministic.
            foreach (
                var extension in nestedTypes
                    .Where(ExtensionMembers.IsExtension)
                    .OrderBy(ExtensionHeader, StringComparer.Ordinal)
            )
            {
                WriteExtension(builder, extension);
            }

            foreach (
                var nested in Sorted(
                    nestedTypes.Where(static t => !ExtensionMembers.IsExtension(t))
                )
            )
            {
                WriteType(builder, nested);
            }
        }

        builder.Dedent();
        builder.AppendLine("}");
    }

    private static void WriteExtension(IndentingBuilder builder, INamedTypeSymbol extension)
    {
        builder.AppendLine(ExtensionHeader(extension));
        builder.AppendLine("{");
        builder.Indent();

        foreach (var member in Sorted(VisibleMembers(extension)))
        {
            builder.AppendLine(FormatMember(member));
        }

        builder.Dedent();
        builder.AppendLine("}");
    }

    /// <summary>The header of an extension block, e.g. <c>extension(int)</c> or
    /// <c>extension&lt;T&gt;(T[] source)</c>. The receiver is the block's marker type's receiver
    /// parameter; its name is emitted only when the source declared one.</summary>
    private static string ExtensionHeader(INamedTypeSymbol extension)
    {
        var receiver = ExtensionMembers.Receiver(extension);
        var receiverText = receiver is null ? string.Empty : FormatReceiver(receiver);
        return $"extension{TypeParameterList(extension)}({receiverText})";
    }

    private static string FormatReceiver(IParameterSymbol receiver)
    {
        var prefix = receiver.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => string.Empty,
        };

        var type = prefix + receiver.Type.ToDisplayString(s_typeFormat);
        return receiver.Name.Length == 0 ? type : type + " " + receiver.Name;
    }

    /// <summary>Generates the body-less declaration text of a single member, exactly as it should
    /// appear inside a type in a <c>.cssig</c> file (no leading indentation, no trailing newline).
    /// Enum members are rendered as <c>Name = value,</c>.</summary>
    private static string FormatMember(ISymbol member)
    {
        if (member is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } enumField)
        {
            return $"{enumField.Name} = {FormatConstant(enumField.ConstantValue)},";
        }

        var text = member
            .ToDisplayString(s_memberFormat)
            .Replace("volatile ", string.Empty)
            .Replace("required ", string.Empty);

        // ShowReadWriteDescriptor already renders the `{ get; set; }` body for properties/indexers,
        // so only non-property members need a terminating semicolon.
        return member is IPropertySymbol ? text : text + ";";
    }

    private static string TypeHeader(INamedTypeSymbol type)
    {
        var parts = new List<string> { Accessibility(type.DeclaredAccessibility) };

        if (type.IsStatic)
        {
            parts.Add("static");
        }
        else if (type.TypeKind == TypeKind.Class)
        {
            if (type.IsAbstract)
            {
                parts.Add("abstract");
            }

            if (type.IsSealed)
            {
                parts.Add("sealed");
            }
        }

        parts.Add(
            type.TypeKind switch
            {
                TypeKind.Class => type.IsRecord ? "record" : "class",
                TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                _ => "class",
            }
        );

        parts.Add(type.Name + TypeParameterList(type));
        return string.Join(" ", parts);
    }

    private static string DelegateDeclaration(INamedTypeSymbol type)
    {
        var invoke = type.DelegateInvokeMethod!;
        var @return =
            (
                invoke.ReturnsByRef ? "ref "
                : invoke.ReturnsByRefReadonly ? "ref readonly "
                : string.Empty
            ) + invoke.ReturnType.ToDisplayString(s_typeFormat);
        return $"{Accessibility(type.DeclaredAccessibility)} delegate {@return} "
            + $"{type.Name}{TypeParameterList(type)}({FormatParameters(invoke.Parameters)})";
    }

    private static string TypeParameterList(INamedTypeSymbol type) =>
        type.TypeParameters.IsEmpty
            ? string.Empty
            : "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";

    private static string FormatParameters(IEnumerable<IParameterSymbol> parameters) =>
        string.Join(
            ", ",
            parameters.Select(p =>
            {
                var prefix = p.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    _ => string.Empty,
                };
                return prefix + p.Type.ToDisplayString(s_typeFormat) + " " + p.Name;
            })
        );

    /// <summary>The members of <paramref name="type"/> that should appear in the signature file:
    /// explicitly declared, externally visible non-type members, excluding accessors (emitted via
    /// their property/event) so the set matches what <see cref="ApiSurface"/> compares.</summary>
    private static IEnumerable<ISymbol> VisibleMembers(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol || member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (
                member is IMethodSymbol
                {
                    MethodKind: MethodKind.PropertyGet
                        or MethodKind.PropertySet
                        or MethodKind.EventAdd
                        or MethodKind.EventRemove,
                }
            )
            {
                continue;
            }

            if (IsVisible(member))
            {
                yield return member;
            }
        }
    }

    private static bool IsVisible(ISymbol member) =>
        member switch
        {
            // Properties are tracked through their accessors; emit the property if either surfaces.
            IPropertySymbol property => (
                property.GetMethod is { } getter && ApiSurface.IsTrackedApi(getter)
            ) || (property.SetMethod is { } setter && ApiSurface.IsTrackedApi(setter)),
            _ => ApiSurface.IsTrackedApi(member),
        };

    private static IEnumerable<INamedTypeSymbol> TopLevelTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var member in stack.Pop().GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol ns:
                        stack.Push(ns);
                        break;
                    case INamedTypeSymbol type:
                        yield return type;
                        break;
                }
            }
        }
    }

    private static IEnumerable<T> Sorted<T>(IEnumerable<T> symbols)
        where T : ISymbol =>
        symbols.OrderBy(s => s.ToDisplayString(s_memberFormat), StringComparer.Ordinal);

    /// <summary>
    /// A stable identity for a <em>top-level</em> type — its namespace-qualified name plus generic
    /// arity (e.g. <c>N.Outer`1</c>). Used to decide which signature file owns a type. The same
    /// string can be reconstructed from a <c>.cssig</c> type declaration's syntax, so the code fix
    /// can match a declared type back to its project symbol.
    /// </summary>
    public static string TopLevelKey(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n
            ? n.ToDisplayString() + "."
            : string.Empty;
        var name = type.Arity > 0 ? type.Name + "`" + type.Arity : type.Name;
        return ns + name;
    }

    private static string Accessibility(Accessibility accessibility) =>
        accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            Microsoft.CodeAnalysis.Accessibility.Private => "private",
            _ => "internal",
        };

    private static string FormatConstant(object? value) =>
        value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
