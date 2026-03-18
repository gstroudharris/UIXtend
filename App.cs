using System;
using Microsoft.UI.Xaml;
using UIXtend.Core;

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
        }
    }
}
