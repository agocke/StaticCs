
namespace StaticCs;

public sealed class AsyncContext
{
    private static readonly AsyncLocal<AsyncContext> _currentContext = new();
    public static AsyncContext CurrentContext => _currentContext.Value;

    private readonly AsyncContext _parentContext;
    private AsyncContext(AsyncContext parentContext)
    {
        _parentContext = parentContext;
    }
}