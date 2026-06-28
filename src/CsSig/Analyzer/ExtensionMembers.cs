using System.Reflection;
using Microsoft.CodeAnalysis;

namespace CsSig;

/// <summary>
/// Helpers for C# "extension" members (<c>extension(Receiver) { ... }</c>). Roslyn models an
/// extension block as a nested type whose <see cref="ITypeSymbol.TypeKind"/> is
/// <c>TypeKind.Extension</c> and whose name is an unspeakable, compiler-generated marker derived
/// from the block's contents. The members declared inside the block live on that marker type; the
/// enclosing static class carries only their (implicit) implementations.
/// </summary>
internal static class ExtensionMembers
{
    /// <summary>
    /// The structural name used for an extension marker type in a <see cref="TypeRef"/> /
    /// <see cref="MemberIdentity"/>. The real metadata name is an unspeakable content hash, so the
    /// block is instead identified by this fixed discriminator plus its receiver type.
    /// </summary>
    public const string Name = "<extension>";

    // TypeKind.Extension is newer than the Roslyn baseline this analyzer compiles against, but the
    // host compiler reports it at runtime. Reference it by its numeric value (mirroring how
    // ApiMember handles RefKind.RefReadOnlyParameter) so the analyzer keeps targeting the older
    // Roslyn version.
    private const TypeKind ExtensionKind = (TypeKind)14;

    // INamedTypeSymbol.ExtensionParameter (the receiver of an extension block) is likewise newer
    // than the baseline, so it is read reflectively.
    private static readonly PropertyInfo? s_extensionParameter =
        typeof(INamedTypeSymbol).GetProperty("ExtensionParameter");

    /// <summary>Whether <paramref name="type"/> is an extension marker type.</summary>
    public static bool IsExtension(INamedTypeSymbol type) => type.TypeKind == ExtensionKind;

    /// <summary>
    /// Whether <paramref name="type"/> directly contains any extension block (i.e. it is a static
    /// class hosting <c>extension(...) { ... }</c> members). Such a class also carries the implicit
    /// implementation methods of those members, which are not part of the signature surface.
    /// </summary>
    public static bool ContainsExtension(INamedTypeSymbol type)
    {
        foreach (var member in type.GetTypeMembers())
        {
            if (IsExtension(member))
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
    public static IParameterSymbol? Receiver(INamedTypeSymbol type) =>
        s_extensionParameter?.GetValue(type) as IParameterSymbol;
}
