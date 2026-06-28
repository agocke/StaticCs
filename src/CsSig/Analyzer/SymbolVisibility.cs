using Microsoft.CodeAnalysis;

namespace CsSig;

internal enum SymbolVisibility
{
    Public = 0,
    Internal = 1,
    Private = 2,
}

internal static class SymbolVisibilityExtensions
{
    /// <summary>
    /// Computes the effective visibility of a symbol, taking its containing types into account.
    /// Ported from Roslyn's <c>ISymbolExtensions.GetResultantVisibility</c>
    /// (dotnet/roslyn: src/Workspaces/SharedUtilitiesAndExtensions/Compiler/Extensions/Symbols/ISymbolExtensions.cs).
    /// </summary>
    public static SymbolVisibility GetResultantVisibility(this ISymbol symbol)
    {
        // Start by assuming it's visible.
        var visibility = SymbolVisibility.Public;

        switch (symbol.Kind)
        {
            case SymbolKind.Alias:
                // Aliases are only visible in the file they were declared in.
                return SymbolVisibility.Private;

            case SymbolKind.Parameter:
                // Parameters are only as visible as their containing symbol.
                return GetResultantVisibility(symbol.ContainingSymbol);

            case SymbolKind.TypeParameter:
                // Type parameters are private.
                return SymbolVisibility.Private;
        }

        ISymbol? current = symbol;
        while (current != null && current.Kind != SymbolKind.Namespace)
        {
            switch (current.DeclaredAccessibility)
            {
                // If we see anything private, then the symbol is private.
                case Accessibility.NotApplicable:
                case Accessibility.Private:
                    return SymbolVisibility.Private;

                // If we see anything internal, then knock it down from public to internal.
                case Accessibility.Internal:
                case Accessibility.ProtectedAndInternal:
                    visibility = SymbolVisibility.Internal;
                    break;

                // For anything else (Public, Protected, ProtectedOrInternal), the
                // symbol stays at the level we've gotten so far.
            }

            current = current.ContainingSymbol;
        }

        return visibility;
    }
}
