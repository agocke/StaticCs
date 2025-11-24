using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public sealed class TaskScope
{
    private readonly CancellationTokenSource _cts = new();
    private List<Task>? _tasks = new();

    public CancellationToken CancellationToken => _cts.Token;

    private TaskScope() { }

    public static async Task With(
        Func<TaskScope, Task> action,
        Action<OperationCanceledException>? onCanceled = null
    )
    {
        var scope = new TaskScope();
        try
        {
            await action(scope);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == scope.CancellationToken)
        {
            onCanceled?.Invoke(e);
        }
        catch (Exception e)
        {
            var (canceled, backgroundException) = await scope.CancelAndGather();
            switch (canceled, backgroundException)
            {
                case (true, _):
                case (false, null):
                    throw;
                case (false, not null):
                    throw new AggregateException(e, backgroundException);
            }
        }
        if (await scope.CancelAndGather() is (true, { } ex))
        {
            throw ex;
        }
    }

    /// <summary>
    /// Cancels all outstanding tasks and waits for them to complete.
    /// </summary>
    /// <returns>
    /// (true, null) if any tasks where canceled, and their cancellation token is
    ///     owned by this scope.
    /// (true, not null) if any tasks where canceled, and any of them had a token not
    ///     owned by this scope. The exception is an AggregateException containing all
    ///     the unhandled OperationCanceledExceptions.
    /// (false, null) if all background tasks ran to completion.
    /// (false, not null) if any background tasks faulted.
    /// </returns>
    private async Task<(bool Canceled, Exception?)> CancelAndGather()
    {
        var finalTasks = CancelOutstanding();
        var t = Task.WhenAll(finalTasks);
        TaskCanceledException? canceled = null;
        try
        {
            await t;
        }
        catch (TaskCanceledException e)
        {
            // Only catch TaskCanceledException since it otherwise won't be listed on the task
            canceled = e;
        }
        switch (t.Status)
        {
            case TaskStatus.RanToCompletion:
                return (false, null);
            case TaskStatus.Canceled:
                List<OperationCanceledException>? unhandledCancels = null;
                foreach (
                    var ex in (IEnumerable<Exception>?)t.Exception?.InnerExceptions
                        ?? Array.Empty<Exception>()
                )
                {
                    // Filter out all cancellations that are owned by this scope
                    if (ex is OperationCanceledException e && e.CancellationToken != _cts.Token)
                    {
                        unhandledCancels ??= new();
                        unhandledCancels.Add(e);
                    }
                }
                if (unhandledCancels is not null)
                {
                    return (true, new AggregateException(unhandledCancels));
                }
                return (true, canceled);
            case TaskStatus.Faulted:
                return (false, t.Exception);
            default:
                throw new Exception("Unreachable");
        }
    }

    private List<Task> CancelOutstanding()
    {
        List<Task> finalTasks = _tasks!;
        _tasks = null;
        foreach (var task in finalTasks)
        {
            if (!task.IsCompleted)
            {
                Cancel();
                break;
            }
        }
        return finalTasks;
    }

    /// <summary>
    /// Used as an exception filter to cancel outstanding tasks and rethrow the current
    /// exception (by always returning false for filtering).
    /// </summary>
    static bool CancelAndRethrow(TaskScope scope)
    {
        scope.Cancel();
        return false;
    }

    private void Cancel() => _cts.Cancel();

    public Task Run(Func<Task> action)
    {
        if (_tasks is null)
        {
            throw new InvalidOperationException("TaskScope has ended.");
        }

        var task = Task
            .Factory.StartNew(
                async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch when (CancelAndRethrow(this))
                    {
                        throw new Exception("Unreachable");
                    }
                },
                TaskCreationOptions.AttachedToParent
            )
            .Unwrap();
        _tasks.Add(task);
        return task;
    }
}
