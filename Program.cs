using System;
using System.Threading.Tasks;
using UIXtend.Core;
using Windows.Win32;

namespace UIXtend
{
    public class Program
    {
        [STAThread]
        public static async Task Main(string[] args)
        {
            // Start the headless host
            await ServiceHost.StartAsync(args);

            // Run standard Win32 message pump for tray icon and overlay windows
            while (PInvoke.GetMessage(out var msg, default, 0, 0))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            // Msg loop exited (PostQuitMessage called), stop the host
            await ServiceHost.StopAsync();
        }
    }
}
