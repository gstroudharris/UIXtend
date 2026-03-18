using System;
using UIXtend.Core.Interfaces;

namespace UIXtend.Core.Services
{
    public class WindowService : IWindowService
    {
        public IntPtr MainWindowHandle { get; private set; }

        public void Initialize()
        {
            // Initialization for Window Management
            CreateWindow();
        }

        public void CreateWindow()
        {
            // Create hidden or tool window, set styles WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT
        }

        public void Dispose()
        {
            // Cleanup HWND
            GC.SuppressFinalize(this);
        }
    }
}
