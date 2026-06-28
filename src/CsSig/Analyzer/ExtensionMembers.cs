using Microsoft.CodeAnalysis;

namespace CsSig;

/// <summary>
/// Helpers for C# "extension" members (<c>extension(Receiver) { ... }</c>). Roslyn models an
/// extension block as a nested type whose <see cref="ITypeSymbol.TypeKind"/> is
/// <see cref="TypeKind.Extension"/> and whose name is an unspeakable, compiler-generated marker
/// derived from the block's contents. The members declared inside the block live on that marker
/// type; the enclosing static class carries only their (implicit) implementations.
/// </summary>
internal static class ExtensionMembers
{
    /// <summary>
    /// The structural name used for an extension marker type in a <see cref="TypeRef"/> /
    /// <see cref="MemberIdentity"/>. The real metadata name is an unspeakable content hash, so the
    /// block is instead identified by this fixed discriminator plus its receiver type.
    /// </summary>
    public const string Name = "<extension>";

    /// <summary>Whether <paramref name="type"/> is an extension marker type.</summary>
    public static bool IsExtension(INamedTypeSymbol type) => type.IsExtension;

    /// <summary>
    /// Whether <paramref name="type"/> directly contains any extension block (i.e. it is a static
    /// class hosting <c>extension(...) { ... }</c> members). Such a class also carries the implicit
    /// implementation methods of those members, which are not part of the signature surface.
    /// </summary>
    public static bool ContainsExtension(INamedTypeSymbol type)
    {
        foreach (var member in type.GetTypeMembers())
        {
            if (member.IsExtension)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The receiver parameter of an extension block (e.g. the <c>int</c> in <c>extension(int)</c>),
    /// or <see langword="null"/> when it cannot be determined.
    /// </summary>
    public static IParameterSymbol? Receiver(INamedTypeSymbol type) => type.ExtensionParameter;
}
