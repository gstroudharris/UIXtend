using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UIXtend.Core.Interfaces;
using UIXtend.Core.Services;

namespace UIXtend.Core
{
    public static class ServiceHost
    {
        public static IHost? Host { get; private set; }
        public static IServiceProvider? ServiceProvider => Host?.Services;

        /// <summary>
        /// Initializes and starts the generic host for the headless headless background process.
        /// </summary>
        public static async Task StartAsync(string[]? args = null)
        {
            Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args ?? Array.Empty<string>())
                .ConfigureServices((context, services) =>
                {
                    // Register Core Services (Implementation classes)
                    services.AddSingleton<WindowService>();
                    services.AddSingleton<RenderService>();
                    services.AddSingleton<ShellService>();

                    // Forward interface resolution to the registered implementations
                    services.AddSingleton<IWindowService>(sp => sp.GetRequiredService<WindowService>());
                    services.AddSingleton<IRenderService>(sp => sp.GetRequiredService<RenderService>());
                    services.AddSingleton<IShellService>(sp => sp.GetRequiredService<ShellService>());

                    // Register them as IService for the Bootstrapper to find them
                    services.AddSingleton<IService>(sp => sp.GetRequiredService<WindowService>());
                    services.AddSingleton<IService>(sp => sp.GetRequiredService<RenderService>());
                    services.AddSingleton<IService>(sp => sp.GetRequiredService<ShellService>());

                    // Add the bootstrapper to run Initialize() on IService instances
                    services.AddHostedService<BootstrapperHostedService>();
                })
                .Build();

            // Run the host asynchronously (blocks until stopped or canceled)
            await Host.StartAsync();
        }

        public static async Task StopAsync()
        {
            if (Host != null)
            {
                await Host.StopAsync();
                Host.Dispose();
                Host = null;
            }
        }
    }

    /// <summary>
    /// Hosted service that properly ties our IService initialization into the Microsoft.Extensions.Hosting lifecycle.
    /// </summary>
    internal class BootstrapperHostedService : IHostedService
    {
        private readonly IEnumerable<IService> _services;

        public BootstrapperHostedService(IEnumerable<IService> services)
        {
            _services = services;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services)
            {
                service.Initialize();
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services)
            {
                service.Dispose();
            }
            return Task.CompletedTask;
        }
    }
}
