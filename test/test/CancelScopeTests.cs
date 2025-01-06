
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StaticCs.Async.Tests;

public sealed class CancelScopeTests
{
    [Fact]
    public void LeavingScopeCancels()
    {
        CancellationToken token = default;
        CancelScope.With(scope =>
        {
            token = scope.CancellationToken;
            Assert.False(token.IsCancellationRequested);
        });
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task LeavingScopeCancelsAsync()
    {
        CancellationToken token = default;
        await CancelScope.With(async scope =>
        {
            await Task.Delay(0);
            token = scope.CancellationToken;
            Assert.False(token.IsCancellationRequested);
        });
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAfter()
    {
        await CancelScope.CancelAfter(TimeSpan.FromMilliseconds(10), async scope =>
        {
            var token = scope.CancellationToken;
            Assert.False(token.IsCancellationRequested);
            await Task.Delay(50);
            Assert.True(token.IsCancellationRequested);
        });
    }
}