using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Manages the System Tray icon and associated shell integration.
    /// </summary>
    public interface IShellService : IService
    {
        void ShowTrayIcon();
        void HideTrayIcon();
    }
}
