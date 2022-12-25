
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace StaticCs.DependencyInjection.Tests;

interface IMessageWriter
{
    void Write(string message);
}

partial class ConsoleMessageWriter : IMessageWriter
{
    public void Write(string message) => Console.WriteLine(message);
}

sealed partial class Worker : BackgroundService
{
    private readonly IMessageWriter _writer;
    public Worker(IMessageWriter writer) => _writer = writer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _writer.Write($"Worker running at {DateTimeOffset.Now}");
            await Task.Delay(1_000, stoppingToken);
        }
    }
}
