namespace CsSig;

/// <summary>
/// Diagnostic ids reported by the <c>.cssig</c> analyzer.
/// </summary>
public enum DiagId
{
    /// <summary>A signature declared in a .cssig file is not present in the project.</summary>
    MissingFromProject = 1,

    /// <summary>A public API member in the project is not declared in any .cssig file.</summary>
    MissingFromSignature = 2,

    /// <summary>A .cssig file could not be parsed.</summary>
    SignatureFileError = 3,

    /// <summary>A .cssig file uses a construct that is not allowed in a signature file.</summary>
    DisallowedSignatureSyntax = 4,

    /// <summary>A member exists on both sides but its signature is not equivalent.</summary>
    SignatureMismatch = 5,
}

public static class DiagUtils
{
    private const string DiagPrefix = "CSSIG";

    public static string ToIdString(this DiagId id) => $"{DiagPrefix}{(int)id:D3}";
}
