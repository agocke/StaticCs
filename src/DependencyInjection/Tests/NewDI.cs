
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;

namespace StaticCs.DependencyInjection.Tests;

public static class NewDI
{
    public static void Run()
    {
        // var builder = Host.CreateDefaultBuilder();
        // builder.ConfigureServices(services =>
        //     services.AddHostedService<Worker>()
        //         .AddScoped<IMessageWriter, ConsoleMessageWriter>());

        // using var host = builder.Build();
        // host.Run();
        var host = new BackgroundServiceHost(new Worker(new ConsoleMessageWriter()));
        host.Run();
    }

    class BackgroundServiceHost
    {
        public static BackgroundServiceHost CreateFromProvider<P>()
            where P : IServiceProvider<BackgroundService>,
                      IServiceProvider<IMessageWriter>,
                      IServiceProvider<Worker>
        {
            var service = ServiceProvider.GetService<P, Worker>();
            return new BackgroundServiceHost(service);
        }

        private readonly BackgroundService _service;
        public BackgroundServiceHost(BackgroundService service)
        {
            _service = service;
        }

        public void Run()
        {
            _service.StartAsync(default);
        }
    }
}