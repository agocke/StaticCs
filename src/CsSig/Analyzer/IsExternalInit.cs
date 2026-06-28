namespace System.Runtime.CompilerServices
{
    // Required so that `record` types and `init` accessors can be used when targeting
    // netstandard2.0 (which does not ship the IsExternalInit type).
    internal static class IsExternalInit { }
}
