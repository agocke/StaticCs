using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StaticCs.Async.Tests;

public sealed class AsyncTests
{
    private ITestOutputHelper _output;

    public AsyncTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ExceptionInBackgroundCancelsForeground()
    {
        CancellationToken token = default;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TaskScope.With(async scope =>
            {
                token = scope.CancellationToken;
                var task = scope.Run(() => throw new InvalidOperationException("Test"));
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            })
        );
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ExceptionInForegroundCancelsBackground()
    {
        CancellationToken token = default;
        Task? backgroundTask = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TaskScope.With(scope =>
            {
                token = scope.CancellationToken;
                backgroundTask = scope.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    _output.WriteLine("Background task completed.");
                });
                throw new InvalidOperationException("Test");
            })
        );
        Assert.True(token.IsCancellationRequested);
        Assert.NotNull(backgroundTask);
        Assert.Equal(TaskStatus.Canceled, backgroundTask.Status);
    }

    [Fact]
    public async Task ScopeWaitsForBackground()
    {
        CancellationToken token = default;
        Task? backgroundTask = null;
        var backgroundCompleted = new TaskCompletionSource();
        var foregroundCompleted = new TaskCompletionSource();
        var scope = TaskScope.With(scope =>
        {
            token = scope.CancellationToken;
            backgroundTask = scope.Run(async () =>
            {
                await backgroundCompleted.Task;
                _output.WriteLine("Background task completed.");
            });
            foregroundCompleted.SetResult();
            return Task.CompletedTask;
        });
        await foregroundCompleted.Task;
        Assert.False(scope.IsCompleted);
        Assert.NotNull(backgroundTask);
        Assert.Equal(TaskStatus.WaitingForActivation, backgroundTask.Status);
        backgroundCompleted.SetResult();
        await backgroundTask;
        await scope;
    }

    [Fact]
    public Task ExceptionRethrownIfNoBackgroundCanceled()
    {
        return Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            Task? backgroundTask = null;
            var scope = TaskScope.With(scope =>
            {
                var backgroundCompleted = new TaskCompletionSource();
                try
                {
                    backgroundTask = scope.Run(async () =>
                    {
                        await backgroundCompleted.Task;
                        _output.WriteLine("Background task completed.");
                    });
                    throw new InvalidOperationException("Test");
                }
                finally
                {
                    backgroundCompleted.SetResult();
                }
            });
            await scope;
        });
    }
}
