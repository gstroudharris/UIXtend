using System;
using Microsoft.UI.Xaml;
using UIXtend.Core;
using UIXtend.Core.Interfaces;

namespace UIXtend
{
    public class App : Application
    {
        public App()
        {
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // Initialize Core Backend Services alongside WinUI 3
            await ServiceHost.StartAsync(Environment.GetCommandLineArgs());

            var shell = ServiceHost.ServiceProvider?.GetService(typeof(IShellService)) as IShellService;
            if (shell != null)
                shell.OnExitRequested += OnExitRequested;
        }

        private async void OnExitRequested()
        {
            AppLogger.Log("Shutdown initiated");

            // Close all lens windows on the UI thread before stopping services
            var lensService = ServiceHost.ServiceProvider?.GetService(typeof(ILensService)) as ILensService;
            lensService?.Dispose();

            // Close the main menu window cleanly (bypasses the hide-on-close handler)
            var windowService = ServiceHost.ServiceProvider?.GetService(typeof(IWindowService)) as IWindowService;
            windowService?.Shutdown();

            // Stop remaining services (WGC sessions, GPU device, capture, etc.)
            await ServiceHost.StopAsync();

            AppLogger.Dispose();
            Environment.Exit(0);
        }
    }
}
