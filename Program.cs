// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

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
            if (Array.Exists(args, a => a.Equals("--loggingEnabled", StringComparison.OrdinalIgnoreCase)))
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
