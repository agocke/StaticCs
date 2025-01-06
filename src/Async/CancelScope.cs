
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StaticCs.Async;

public sealed class CancelScope
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    public static async Task With(Func<CancelScope, Task> action, Action<OperationCanceledException>? onCanceled = null)
    {
        var scope = new CancelScope();
        try
        {
            await action(scope);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == scope._cts.Token)
        {
            onCanceled?.Invoke(e);
        }
        finally
        {
            scope._cts.Cancel();
        }
    }

    public static void With(Action<CancelScope> action, Action<OperationCanceledException>? onCanceled = null)
    {
        With(scope =>
        {
            action(scope);
            return Task.CompletedTask;
        }, onCanceled).GetAwaiter().GetResult();
    }

    public static void CancelAfter(TimeSpan delay, Action<CancelScope> action, Action<OperationCanceledException>? onCanceled = null)
    {
        CancelAfter(delay, scope =>
        {
            action(scope);
            return Task.CompletedTask;
        }, onCanceled).GetAwaiter().GetResult();
    }

    public static async Task CancelAfter(TimeSpan delay, Func<CancelScope, Task> action, Action<OperationCanceledException>? onCanceled = null)
    {
        var scope = new CancelScope();
        var token = scope._cts.Token;
        try
        {
            scope._cts.CancelAfter(delay);
            await action(scope);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == token)
        {
            // Ignore
            onCanceled?.Invoke(e);
        }
        finally
        {
            scope._cts.Cancel();
        }
    }
}