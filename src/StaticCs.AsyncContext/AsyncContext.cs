
namespace StaticCs;

public sealed class AsyncContext : IAsyncDisposable
{
    private static AsyncLocal<AsyncContext> MakeTopLevelContext()
    {
        var ctx = new AsyncContext(null);
        var local = new AsyncLocal<AsyncContext>();
        local.Value = ctx;
        return local;
    }

    private static readonly AsyncLocal<AsyncContext> _currentContext = MakeTopLevelContext();
    public static AsyncContext CurrentContext => _currentContext.Value!;

    private readonly AsyncContext? _parentContext;
    private AsyncContext(AsyncContext? parentContext)
    {
        _parentContext = parentContext;
    }

    public AsyncContext NewContext()
    {
        return new AsyncContext(this);
    }

    public static AsyncContext OpenContext()
    {
        var current = CurrentContext;
        var newCtx = current.NewContext();
        _currentContext.Value = newCtx;
        return newCtx;
    }

    public ValueTask DisposeAsync()
    {
        if (_parentContext is not null)
        {
            _currentContext.Value = _parentContext;
        }
        return ValueTask.CompletedTask;
    }
}