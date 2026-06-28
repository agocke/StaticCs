using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CsSig;

/// <summary>
/// Determines a compilation's public API surface and reduces each member to a structural
/// <see cref="ApiMember"/> signature, so that symbols originating from two different compilations
/// (the project and the synthetic <c>.cssig</c> compilation) can be compared for equivalence.
///
/// The surface-detection rules and the display format are ported from the Roslyn Public API
/// analyzer (dotnet/roslyn:
/// src/RoslynAnalyzers/PublicApiAnalyzers/Core/Analyzers/DeclarePublicApiAnalyzer*.cs).
/// </summary>
internal static class ApiSurface
{
    private const string InstanceConstructorName = ".ctor";

    /// <summary>
    /// A readable signature format, used only for diagnostic messages. Equivalence is decided by
    /// the structural <see cref="ApiMember"/>, not by this string.
    /// </summary>
    private static readonly SymbolDisplayFormat s_displayFormat =
        new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions:
                SymbolDisplayMemberOptions.IncludeParameters |
                SymbolDisplayMemberOptions.IncludeContainingType |
                SymbolDisplayMemberOptions.IncludeExplicitInterface |
                SymbolDisplayMemberOptions.IncludeModifiers |
                SymbolDisplayMemberOptions.IncludeConstantValue,
            parameterOptions:
                SymbolDisplayParameterOptions.IncludeExtensionThis |
                SymbolDisplayParameterOptions.IncludeParamsRefOut |
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeDefaultValue,
            miscellaneousOptions:
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Builds a map from the structural signature of every externally visible member declared in
    /// <paramref name="assembly"/> to its source location and a human-readable display string.
    /// </summary>
    public static Dictionary<MemberIdentity, ApiEntry> Collect(IAssemblySymbol assembly)
    {
        var map = new Dictionary<MemberIdentity, ApiEntry>();

        foreach (var type in AllNamedTypes(assembly.GlobalNamespace))
        {
            if (!IsTrackedApi(type))
            {
                continue;
            }

            Add(map, type);

            foreach (var member in GetApiMembers(type))
            {
                Add(map, member);
            }
        }

        return map;
    }

    private static void Add(Dictionary<MemberIdentity, ApiEntry> map, ISymbol symbol)
    {
        var member = ApiMember.From(symbol);
        if (map.ContainsKey(member.Identity))
        {
            return;
        }

        map.Add(member.Identity, new ApiEntry(member, GetLocation(symbol), GetDisplay(symbol)));
    }

    private static Location GetLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location is null && symbol.ContainingType is { } containingType)
        {
            location = containingType.Locations.FirstOrDefault(static l => l.IsInSource);
        }

        return location ?? Location.None;
    }

    /// <summary>Yields the tracked members of <paramref name="type"/>, including implicit
    /// constructors and implicit record members, mirroring the Public API analyzer.</summary>
    private static IEnumerable<ISymbol> GetApiMembers(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            // Nested types are visited independently by AllNamedTypes.
            if (member is INamedTypeSymbol)
            {
                continue;
            }

            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (IsTrackedApi(member))
            {
                yield return member;
            }
        }

        // Implicitly declared (parameterless) constructor.
        IMethodSymbol? implicitConstructor = null;
        if (type is { TypeKind: TypeKind.Class, InstanceConstructors.Length: 1 } or { TypeKind: TypeKind.Struct })
        {
            implicitConstructor = type.InstanceConstructors.FirstOrDefault(static c => c.IsImplicitlyDeclared);
            if (implicitConstructor is not null && IsTrackedApi(implicitConstructor))
            {
                yield return implicitConstructor;
            }
        }

        // Implicitly declared members of a record (Equals, GetHashCode, Deconstruct, copy ctor,
        // positional property accessors, ...).
        //
        // A static class hosting extension blocks also carries implicit *implementation* methods
        // for each extension member (e.g. `get_Empty`, `TryFirst`). Those are implementation
        // details: the members themselves are tracked through the extension marker types, so skip
        // the implicit-method pass for such classes to avoid double-counting.
        bool hostsExtensions = ExtensionMembers.ContainsExtension(type);
        foreach (var member in type.GetMembers())
        {
            if (hostsExtensions)
            {
                break;
            }

            if (SymbolEqualityComparer.Default.Equals(member, implicitConstructor))
            {
                continue;
            }

            if (member is IMethodSymbol { IsImplicitlyDeclared: true } method && IsTrackedApi(method))
            {
                // Skip accessors of explicit (non-implicit) properties: those properties are
                // tracked through their own accessor callbacks already. Keep accessors that
                // belong to implicit properties (e.g. record `EqualityContract`).
                if (method.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet) ||
                    method is { AssociatedSymbol.IsImplicitlyDeclared: true })
                {
                    yield return method;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllNamedTypes(INamespaceSymbol root)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol ns:
                        stack.Push(ns);
                        break;
                    case INamedTypeSymbol type:
                        yield return type;
                        stack.Push(type);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Whether a symbol is part of the externally visible (public) API surface.
    /// Ported from the Public API analyzer's <c>IsTrackedAPI</c>/<c>IsTrackedApiCore</c>.
    /// </summary>
    public static bool IsTrackedApi(ISymbol symbol)
    {
        if (symbol is IMethodSymbol methodSymbol)
        {
            // Event accessors are encoded via the event symbol itself.
            if (methodSymbol.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove)
            {
                return false;
            }

            // Enum constructors are not user-visible API.
            if (methodSymbol is { MethodKind: MethodKind.Constructor, ContainingType.TypeKind: TypeKind.Enum })
            {
                return false;
            }

            // For delegates, only the 'Invoke' method carries the signature.
            if (methodSymbol is { ContainingType.TypeKind: TypeKind.Delegate, MethodKind: not MethodKind.DelegateInvoke })
            {
                return false;
            }
        }

        // Properties are not tracked directly; their get/set accessors (IMethodSymbols) are.
        if (symbol is IPropertySymbol)
        {
            return false;
        }

        if (symbol.GetResultantVisibility() != SymbolVisibility.Public)
        {
            return false;
        }

        for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Protected:
                case Accessibility.ProtectedOrInternal:
                    // Protected members are only externally visible if the containing type can
                    // actually be extended outside the assembly.
                    if (current.ContainingType is not { } container || !CanTypeBeExtended(container))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool CanTypeBeExtended(ITypeSymbol type)
    {
        // A type can be extended publicly if it isn't sealed and has a constructor that is not
        // internal, private, or protected-and-internal.
        return !type.IsSealed &&
            type.GetMembers(InstanceConstructorName).Any(static m => m.DeclaredAccessibility switch
            {
                Accessibility.Internal or Accessibility.ProtectedAndInternal => false,
                Accessibility.Private => false,
                _ => true,
            });
    }

    /// <summary>
    /// Produces a human-readable signature string for diagnostic messages only. Ported from the
    /// Public API analyzer's <c>getApiString</c> (without the nullability/oblivious variants).
    /// </summary>
    private static string GetDisplay(ISymbol symbol)
    {
        var signature = symbol.ToDisplayString(s_displayFormat);

        ITypeSymbol? memberType = symbol switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property => property.Type,
            IEventSymbol @event => @event.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };

        if (memberType is not null)
        {
            signature = signature + " -> " + memberType.ToDisplayString(s_displayFormat);
        }

        return signature;
    }
}

/// <summary>A located, human-readable record of a single API member.</summary>
internal readonly struct ApiEntry
{
    public ApiEntry(ApiMember member, Location location, string display)
    {
        Member = member;
        Location = location;
        Display = display;
    }

    /// <summary>The structural model, used to compare source/binary equivalence.</summary>
    public ApiMember Member { get; }

    public Location Location { get; }

    public string Display { get; }
}
