using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
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

        // Roslyn's SymbolDisplay suppresses every modifier (including `static`) on interface
        // members, treating them all as implicit. `static` is observable and representable, so put
        // it back; the remaining virtuality modifiers are intentionally not tracked for interface
        // members (a body-less signature cannot express default implementations — see FlagsFrom).
        if (
            member is { IsStatic: true, ContainingType.TypeKind: TypeKind.Interface }
            && !text.StartsWith("static ", StringComparison.Ordinal)
        )
        {
            text = "static " + text;
        }

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

        var name = type.Name + TypeParameterList(type);

        // A positional record's parameter list drives its synthesized members (the positional
        // properties, Deconstruct, copy constructor). Emit it so the .cssig compilation synthesizes
        // the same members; the parameters and those members are therefore not written individually.
        if (PrimaryConstructor(type) is { } primary)
        {
            name += "(" + FormatParameters(primary.Parameters) + ")";
        }
        // An accessible implicit parameterless constructor is real public API the project could
        // remove or make private, so it is declared explicitly via primary-constructor syntax (a
        // bare `()` after the type name) rather than left to synthesis. The analyzer ignores
        // synthesized parameterless constructors on the declaration side, so a class with only an
        // inaccessible constructor declares no `()` and no private member appears in the signature.
        else if (ImplicitParameterlessConstructor(type) is not null)
        {
            name += "()";
        }

        parts.Add(name);
        var header = string.Join(" ", parts);

        // A record's base record changes the signatures of its synthesized members (Equals, Clone,
        // PrintMembers become `override`/`sealed` and key off the base type), so it must be carried
        // through. Base types are otherwise not part of the tracked surface and are omitted.
        if (
            type.IsRecord
            && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType
        )
        {
            header += " : " + baseType.ToDisplayString(s_typeFormat);
        }

        header += ConstraintClauses(type.TypeParameters);

        return header;
    }

    /// <summary>Renders the <c>where</c> constraint clauses for a generic type's parameters. These
    /// must be carried because they change member semantics (most importantly, <c>where T : struct</c>
    /// makes <c>T?</c> a <see cref="System.Nullable{T}"/> rather than an annotated reference type).</summary>
    private static string ConstraintClauses(IEnumerable<ITypeParameterSymbol> typeParameters)
    {
        var clauses = new List<string>();
        foreach (var typeParameter in typeParameters)
        {
            var constraints = new List<string>();

            if (typeParameter.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }
            else if (typeParameter.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            else if (typeParameter.HasReferenceTypeConstraint)
            {
                constraints.Add(
                    typeParameter.ReferenceTypeConstraintNullableAnnotation
                    == NullableAnnotation.Annotated
                        ? "class?"
                        : "class"
                );
            }
            else if (typeParameter.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }

            foreach (var constraintType in typeParameter.ConstraintTypes)
            {
                constraints.Add(constraintType.ToDisplayString(s_typeFormat));
            }

            if (typeParameter.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                clauses.Add($"where {typeParameter.Name} : {string.Join(", ", constraints)}");
            }
        }

        return clauses.Count == 0 ? "" : " " + string.Join(" ", clauses);
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
        var primaryConstructor = PrimaryConstructor(type);

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol || member.IsImplicitlyDeclared)
            {
                continue;
            }

            // The primary constructor and the properties synthesized from its parameters are
            // emitted as part of the record header (see TypeHeader), not as individual members.
            if (SymbolEqualityComparer.Default.Equals(member, primaryConstructor))
            {
                continue;
            }

            if (member is IPropertySymbol property && IsPositionalProperty(property))
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

    /// <summary>The primary constructor of a record (or primary-constructor type), identified by its
    /// declaring syntax being the type declaration's parameter list; <c>null</c> when there is
    /// none.</summary>
    private static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type)
    {
        foreach (var constructor in type.InstanceConstructors)
        {
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax { ParameterList: not null })
                {
                    return constructor;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a property was synthesized from a positional record parameter (rather than
    /// explicitly declared), in which case it is carried by the record's parameter list.</summary>
    private static bool IsPositionalProperty(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is ParameterSyntax);


    private static bool IsVisible(ISymbol member) =>
        member switch
        {
            // Properties are tracked through their accessors; emit the property if either surfaces.
            IPropertySymbol property => (
                property.GetMethod is { } getter && ApiSurface.IsTrackedApi(getter)
            ) || (property.SetMethod is { } setter && ApiSurface.IsTrackedApi(setter)),
            _ => ApiSurface.IsTrackedApi(member),
        };

    /// <summary>The implicitly synthesized parameterless constructor of a class that is part of the
    /// public surface (accessible), or <c>null</c> when the type has none or it is inaccessible.
    /// Structs are excluded: their parameterless constructor is always public on both sides, so it
    /// need not be written. This is the one implicit member whose presence and accessibility can vary
    /// independently of the declaration, so it is emitted explicitly rather than left to synthesis.</summary>
    private static IMethodSymbol? ImplicitParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
        {
            return null;
        }

        var constructor = type.InstanceConstructors.FirstOrDefault(static c =>
            c.IsImplicitlyDeclared && c.Parameters.IsEmpty
        );

        return constructor is not null && IsVisible(constructor) ? constructor : null;
    }


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
