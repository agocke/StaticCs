
namespace StaticCs;

internal enum DiagId
{
    ClosedEnumConversion = 1,
    SwitchOnClosedSuppress = 2
}

internal static class DiagUtils
{
    private const string DiagPrefix = "STATICCS";

    public static string ToIdString(this DiagId id) => $"{DiagPrefix}{(int)id:D3}";
}