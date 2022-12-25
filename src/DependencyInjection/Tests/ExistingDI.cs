
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StaticCs.DependencyInjection.Tests;

static class ExistingDI
{
    public static void Run()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices(services =>
            services.AddHostedService<Worker>()
                .AddScoped<IMessageWriter, ConsoleMessageWriter>());

        using var host = builder.Build();
        host.Run();
    }

}