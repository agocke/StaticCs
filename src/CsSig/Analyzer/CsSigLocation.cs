using Microsoft.CodeAnalysis;

namespace CsSig;

/// <summary>
/// Helpers for reporting diagnostics against <c>.cssig</c> additional files.
/// </summary>
internal static class CsSigLocation
{
    /// <summary>
    /// Converts a location that lives in a synthetic <c>.cssig</c> syntax tree (which is not part
    /// of the analyzed compilation) into an external-file location that can be safely reported.
    /// </summary>
    public static Location ToExternal(Location location, string fallbackPath)
    {
        if (!location.IsInSource)
        {
            return Location.None;
        }

        var path = location.SourceTree?.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            path = fallbackPath;
        }

        return Location.Create(path!, location.SourceSpan, location.GetLineSpan().Span);
    }
}
