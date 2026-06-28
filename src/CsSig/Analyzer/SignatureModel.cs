using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace CsSig;

/// <summary>
/// A canonical, structural reference to a type. Two type references from different compilations
/// are equal when they denote the same type, regardless of how they were written.
/// </summary>
internal abstract partial record TypeRef
{
    // Closed hierarchy: only the nested cases below may derive from TypeRef.
    private TypeRef() { }

    public sealed record Named(
        string Namespace,
        TypeRef? ContainingType,
        string Name,
        EqArray<TypeRef> TypeArguments
    ) : TypeRef;

    public sealed record Array(TypeRef ElementType, int Rank) : TypeRef;

    public sealed record Pointer(TypeRef ElementType) : TypeRef;

    public sealed record TypeParameter(int Ordinal, bool IsMethodTypeParameter) : TypeRef;

    public sealed record FunctionPointer(
        SignatureCallingConvention CallingConvention,
        EqArray<TypeRef> CallingConventionTypes,
        ParamKey Return,
        EqArray<ParamKey> Parameters
    ) : TypeRef;

    public sealed record Dynamic : TypeRef
    {
        public static readonly Dynamic Instance = new();
    }
}

partial record TypeRef
{
    public static TypeRef From(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return new Array(From(array.ElementType), array.Rank);

            case IPointerTypeSymbol pointer:
                return new Pointer(From(pointer.PointedAtType));

            case ITypeParameterSymbol typeParameter:
                return new TypeParameter(
                    typeParameter.Ordinal,
                    typeParameter.TypeParameterKind == TypeParameterKind.Method
                );

            case IDynamicTypeSymbol:
                return Dynamic.Instance;

            case IFunctionPointerTypeSymbol functionPointer:
            {
                var signature = functionPointer.Signature;
                var callingConventionTypes = EqArray<TypeRef>.From(
                    signature.UnmanagedCallingConventionTypes.Select(From)
                );
                var @return = new ParamKey(From(signature.ReturnType), signature.RefKind);
                var parameters = EqArray<ParamKey>.From(
                    signature.Parameters.Select(p => new ParamKey(From(p.Type), p.RefKind))
                );
                return new FunctionPointer(
                    signature.CallingConvention,
                    callingConventionTypes,
                    @return,
                    parameters
                );
            }

            case INamedTypeSymbol named:
                var container = named.ContainingType is { } containingType
                    ? From(containingType)
                    : null;
                var @namespace = container is null
                    ? NamespaceName(named.ContainingNamespace)
                    : string.Empty;

                if (ExtensionMembers.IsExtension(named))
                {
                    // An extension block's metadata name is an unspeakable content hash that depends
                    // on its members, so two compilations agree only when every member agrees.
                    // Identify it structurally by its receiver type instead, so members are paired
                    // by the receiver they extend, independent of their sibling members.
                    var receiver = ExtensionMembers.Receiver(named);
                    var receiverArgs = receiver is null
                        ? default
                        : EqArray<TypeRef>.From(new[] { From(receiver.Type) });
                    return new Named(@namespace, container, ExtensionMembers.Name, receiverArgs);
                }

                return new Named(
                    @namespace,
                    container,
                    named.Name,
                    EqArray<TypeRef>.From(named.TypeArguments.Select(From))
                );

            default:
                // The ITypeSymbol hierarchy above is exhaustive (array, pointer, type parameter,
                // dynamic, function pointer, named/error). Anything else cannot be modeled
                // structurally, so fail loudly rather than invent a comparison.
                throw new ArgumentException(
                    $"Cannot build a type reference from type kind '{type.TypeKind}'.",
                    nameof(type)
                );
        }
    }

    private static string NamespaceName(INamespaceSymbol? @namespace) =>
        @namespace is null || @namespace.IsGlobalNamespace
            ? string.Empty
            : @namespace.ToDisplayString();
}

internal enum ApiMemberKind
{
    Type,
    Method,
    Field,
    Event,
}

/// <summary>
/// The boolean aspects of a member that are common to both equivalences (changing any breaks both),
/// packed into a single value. A type uses <see cref="Static"/>/<see cref="Abstract"/>/
/// <see cref="Sealed"/>; a method or event uses <see cref="Static"/> plus the virtuality bits
/// (<see cref="Virtual"/>/<see cref="Abstract"/>/<see cref="Override"/>/<see cref="Sealed"/>); a
/// field uses <see cref="Static"/>/<see cref="ReadOnly"/>/<see cref="HasConstantValue"/>. The
/// <see cref="Abstract"/>/<see cref="Sealed"/> bits are shared between type-level and member-level
/// meanings, which never apply to the same member.
/// </summary>
[Flags]
internal enum MemberFlags
{
    None = 0,
    Static = 1 << 0,
    Abstract = 1 << 1,
    Sealed = 1 << 2,
    Virtual = 1 << 3,
    Override = 1 << 4,
    ReadOnly = 1 << 5,
    HasConstantValue = 1 << 6,
}

/// <summary>The source-only modifiers of a parameter, packed into a single value.</summary>
[Flags]
internal enum ParamModifiers
{
    None = 0,
    Params = 1 << 0,
    This = 1 << 1,
    Optional = 1 << 2,

    /// <summary>
    /// The parameter is <c>ref readonly</c> rather than <c>in</c>. The two share a binary calling
    /// convention (see <see cref="ParamKey"/>), so this distinction affects source equivalence only.
    /// </summary>
    RefReadOnly = 1 << 3,
}

/// <summary>
/// The nullable reference type annotations of a type, flattened in a deterministic pre-order walk of
/// its structure (one entry per type node, e.g. <c>string?</c> on the array and its element for
/// <c>string?[]?</c>). NRT annotations affect <em>source</em> equivalence only — they are erased to
/// attributes in metadata and never change a calling convention, so they are carried by the source
/// projection alone (mirroring how <see cref="ParamModifiers.RefReadOnly"/> is source-only). Each
/// byte is the numeric <see cref="NullableAnnotation"/> (0 = oblivious, 1 = not annotated, 2 =
/// annotated). When the project compiles without nullable reference types every node is oblivious on
/// both the project and the <c>.cssig</c> side, so the annotations compare equal and nothing is
/// enforced.
/// </summary>
internal readonly record struct Nullability(EqArray<byte> Annotations)
{
    public static Nullability Of(ITypeSymbol type)
    {
        var builder = ImmutableArray.CreateBuilder<byte>();
        Walk(type, builder);
        return new Nullability(EqArray<byte>.From(builder));
    }

    private static void Walk(ITypeSymbol type, ImmutableArray<byte>.Builder builder)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                builder.Add((byte)array.NullableAnnotation);
                Walk(array.ElementType, builder);
                break;

            case IPointerTypeSymbol pointer:
                builder.Add((byte)pointer.NullableAnnotation);
                Walk(pointer.PointedAtType, builder);
                break;

            case ITypeParameterSymbol typeParameter:
                builder.Add((byte)typeParameter.NullableAnnotation);
                break;

            case IDynamicTypeSymbol:
                builder.Add((byte)type.NullableAnnotation);
                break;

            case IFunctionPointerTypeSymbol functionPointer:
                builder.Add((byte)functionPointer.NullableAnnotation);
                var signature = functionPointer.Signature;
                Walk(signature.ReturnType, builder);
                foreach (var parameter in signature.Parameters)
                {
                    Walk(parameter.Type, builder);
                }

                break;

            case INamedTypeSymbol named:
                // A nested type's containing type can itself carry annotations (Outer<string?>.Inner),
                // so descend into it before the type's own annotation and its type arguments.
                if (named.ContainingType is { } containingType)
                {
                    Walk(containingType, builder);
                }

                builder.Add((byte)named.NullableAnnotation);
                foreach (var argument in named.TypeArguments)
                {
                    Walk(argument, builder);
                }

                break;

            default:
                builder.Add((byte)type.NullableAnnotation);
                break;
        }
    }
}

/// <summary>
/// The part of a parameter that contributes to a member's <em>identity</em>: callers can never tell
/// two members apart by anything else, so a difference here is an add/remove, not a modification.
/// The <see cref="RefKind"/> is the <em>binary</em> ref kind: <c>in</c> and <c>ref readonly</c>
/// collapse to <see cref="RefKind.In"/> because they share one calling convention; their source
/// difference is carried by <see cref="ParamModifiers.RefReadOnly"/> instead.
/// </summary>
internal readonly record struct ParamKey(TypeRef Type, RefKind RefKind);

/// <summary>
/// The part of a parameter that affects <em>source</em> equivalence only: its name (named
/// arguments), its source-only modifiers (<c>params</c>, extension <c>this</c>, optional,
/// <c>ref readonly</c> vs <c>in</c>), and the nullable annotations of its type. None of these change
/// the binary calling convention.
/// </summary>
internal readonly record struct SourceParam(
    string Name,
    ParamModifiers Modifiers,
    Nullability Nullability
);

/// <summary>
/// The identity of an API member: the tuple by which two members from different compilations are
/// paired. A difference in identity is reported as an added/removed member, never a modification.
/// </summary>
internal sealed record MemberIdentity(
    ApiMemberKind Kind,
    string Namespace,
    TypeRef? ContainingType,
    string Name,
    int Arity,
    EqArray<ParamKey> Parameters
);

/// <summary>
/// The aspects of a type declaration observable to <em>every</em> consumer (source or binary):
/// changing any breaks both equivalences. Shared by <see cref="SourceMember.Type"/> and
/// <see cref="BinaryMember.Type"/>.
/// </summary>
internal sealed record CommonTypeAspects(MemberFlags Flags);

/// <summary>The method aspects common to both equivalences (return type, static-ness, virtuality).</summary>
internal sealed record CommonMethodAspects(TypeRef ReturnType, MemberFlags Flags);

/// <summary>The field aspects common to both equivalences (type, static-ness, readonly, const-ness).</summary>
internal sealed record CommonFieldAspects(TypeRef Type, MemberFlags Flags);

/// <summary>The event aspects common to both equivalences (type, static-ness, virtuality).</summary>
internal sealed record CommonEventAspects(TypeRef Type, MemberFlags Flags);

/// <summary>
/// The projection of a member that defines <em>source</em> equivalence. Two members are
/// source-equivalent exactly when their <see cref="SourceMember"/> values are equal, so each case
/// carries the aspects common to both equivalences plus only the source-only aspects of its kind.
/// </summary>
internal abstract record SourceMember
{
    private SourceMember() { }

    public sealed record Type(CommonTypeAspects Common) : SourceMember;

    public sealed record Method(
        CommonMethodAspects Common,
        Nullability ReturnNullability,
        EqArray<SourceParam> Parameters
    ) : SourceMember;

    public sealed record Field(CommonFieldAspects Common, Nullability Nullability) : SourceMember;

    public sealed record Event(CommonEventAspects Common, Nullability Nullability) : SourceMember;
}

/// <summary>
/// The projection of a member that defines <em>binary</em> equivalence. Two members are
/// binary-equivalent exactly when their <see cref="BinaryMember"/> values are equal. It carries the
/// same common aspects as <see cref="SourceMember"/> plus only the binary-only aspects of its kind
/// (the constant value baked into already-compiled consumers).
/// </summary>
internal abstract record BinaryMember
{
    private BinaryMember() { }

    public sealed record Type(CommonTypeAspects Common) : BinaryMember;

    public sealed record Method(CommonMethodAspects Common) : BinaryMember;

    public sealed record Field(CommonFieldAspects Common, string? ConstantValue) : BinaryMember;

    public sealed record Event(CommonEventAspects Common) : BinaryMember;
}

/// <summary>
/// Structural representation of one externally-visible API member, decomposed into an
/// <see cref="MemberIdentity"/> (the pairing key) and two projections whose record equality
/// <em>is</em> source/binary equivalence. The values may originate from different compilations
/// (the project and the synthetic <c>.cssig</c> compilation).
/// </summary>
internal sealed record ApiMember(MemberIdentity Identity, SourceMember Source, BinaryMember Binary)
{
    public static ApiMember From(ISymbol symbol)
    {
        var containingType = symbol.ContainingType is { } type ? TypeRef.From(type) : null;
        var @namespace =
            containingType is null && symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString()
                : string.Empty;

        // Every flag bit except a field's ReadOnly/HasConstantValue is reported generically by the
        // symbol: types expose IsAbstract/IsSealed, members expose virtuality, and the rest are
        // false for kinds they do not apply to.
        var flags = FlagsFrom(symbol);

        switch (symbol)
        {
            case INamedTypeSymbol named:
            {
                // Extension blocks share an unspeakable empty name; key them by their receiver so
                // two blocks that extend different receivers are distinct and members never collide.
                var isExtension = ExtensionMembers.IsExtension(named);
                var name = isExtension ? ExtensionMembers.Name : named.Name;
                var typeParameters = isExtension ? ExtensionReceiverKey(named) : default;
                var identity = new MemberIdentity(
                    ApiMemberKind.Type,
                    @namespace,
                    containingType,
                    name,
                    named.Arity,
                    typeParameters
                );
                var common = new CommonTypeAspects(flags);
                return new ApiMember(
                    identity,
                    new SourceMember.Type(common),
                    new BinaryMember.Type(common)
                );
            }

            case IMethodSymbol method:
            {
                var keys = EqArray<ParamKey>.From(
                    method.Parameters.Select(p => new ParamKey(
                        TypeRef.From(p.Type),
                        BinaryRefKind(p.RefKind)
                    ))
                );
                var parameters = EqArray<SourceParam>.From(
                    method.Parameters.Select(p => new SourceParam(
                        p.Name,
                        ParamModifiersFrom(p),
                        Nullability.Of(p.Type)
                    ))
                );
                var identity = new MemberIdentity(
                    ApiMemberKind.Method,
                    @namespace,
                    containingType,
                    method.Name,
                    method.Arity,
                    keys
                );
                var common = new CommonMethodAspects(
                    TypeRef.From(method.ReturnType),
                    flags | (method.IsReadOnly ? MemberFlags.ReadOnly : MemberFlags.None)
                );
                return new ApiMember(
                    identity,
                    new SourceMember.Method(common, Nullability.Of(method.ReturnType), parameters),
                    new BinaryMember.Method(common)
                );
            }

            case IFieldSymbol field:
            {
                var identity = new MemberIdentity(
                    ApiMemberKind.Field,
                    @namespace,
                    containingType,
                    field.Name,
                    Arity: 0,
                    default
                );
                flags |=
                    (field.IsReadOnly ? MemberFlags.ReadOnly : MemberFlags.None)
                    | (field.HasConstantValue ? MemberFlags.HasConstantValue : MemberFlags.None);
                var common = new CommonFieldAspects(TypeRef.From(field.Type), flags);
                var constant = field.HasConstantValue ? FormatConstant(field.ConstantValue) : null;
                return new ApiMember(
                    identity,
                    new SourceMember.Field(common, Nullability.Of(field.Type)),
                    new BinaryMember.Field(common, constant)
                );
            }

            case IEventSymbol @event:
            {
                var identity = new MemberIdentity(
                    ApiMemberKind.Event,
                    @namespace,
                    containingType,
                    @event.Name,
                    Arity: 0,
                    default
                );
                var common = new CommonEventAspects(TypeRef.From(@event.Type), flags);
                return new ApiMember(
                    identity,
                    new SourceMember.Event(common, Nullability.Of(@event.Type)),
                    new BinaryMember.Event(common)
                );
            }

            default:
                // ApiSurface only ever yields types, methods, fields, and events (see
                // IsTrackedApi/GetApiMembers). Any other kind cannot be credibly modeled or
                // compared, so fail loudly rather than synthesize a meaningless member.
                throw new ArgumentException(
                    $"Cannot build an API member from symbol kind '{symbol.Kind}'.",
                    nameof(symbol)
                );
        }
    }

    private static MemberFlags FlagsFrom(ISymbol symbol) =>
        (symbol.IsStatic ? MemberFlags.Static : MemberFlags.None)
        | (symbol.IsVirtual ? MemberFlags.Virtual : MemberFlags.None)
        | (symbol.IsAbstract ? MemberFlags.Abstract : MemberFlags.None)
        | (symbol.IsOverride ? MemberFlags.Override : MemberFlags.None)
        | (symbol.IsSealed ? MemberFlags.Sealed : MemberFlags.None);

    private static ParamModifiers ParamModifiersFrom(IParameterSymbol parameter) =>
        (parameter.IsParams ? ParamModifiers.Params : ParamModifiers.None)
        | (parameter.IsThis ? ParamModifiers.This : ParamModifiers.None)
        | (parameter.HasExplicitDefaultValue ? ParamModifiers.Optional : ParamModifiers.None)
        | (
            parameter.RefKind == RefKind.RefReadOnlyParameter
                ? ParamModifiers.RefReadOnly
                : ParamModifiers.None
        );

    // `in` and `ref readonly` parameters share the same binary calling convention (both an
    // `in`-flagged byref with a `modreq(InAttribute)`); `ref readonly` only adds a source-level
    // `RequiresLocationAttribute`. Identity therefore pairs them, and the source difference is
    // carried by ParamModifiers.RefReadOnly so it surfaces as a source-only modification.
    private static RefKind BinaryRefKind(RefKind refKind) =>
        refKind == RefKind.RefReadOnlyParameter ? RefKind.In : refKind;

    /// <summary>
    /// The identity contribution of an extension block: its receiver parameter, encoded as a single
    /// <see cref="ParamKey"/>, so that <see cref="MemberIdentity"/> distinguishes blocks by the
    /// receiver they extend (the receiver may reference the block's own type parameters).
    /// </summary>
    private static EqArray<ParamKey> ExtensionReceiverKey(INamedTypeSymbol extension)
    {
        var receiver = ExtensionMembers.Receiver(extension);
        return receiver is null
            ? default
            : EqArray<ParamKey>.From(
                new[] { new ParamKey(TypeRef.From(receiver.Type), BinaryRefKind(receiver.RefKind)) }
            );
    }

    private static string FormatConstant(object? value) =>
        value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
