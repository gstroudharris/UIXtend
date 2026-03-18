using System;
using System.Threading;
using Microsoft.UI.Xaml;
using UIXtend.Core;

namespace UIXtend
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppLogger.Initialize();

            try
            {
                global::WinRT.ComWrappersSupport.InitializeComWrappers();

                Application.Start((p) => {
                    var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
            }
            finally
            {
                AppLogger.Dispose();
            }
        }
    }
}
