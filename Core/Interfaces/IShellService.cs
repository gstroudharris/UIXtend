using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Manages the System Tray icon and associated shell integration.
    /// </summary>
    public interface IShellService : IService
    {
        event Action OnOpenMenuRequested;
        event Action OnExitRequested;
        void ShowTrayIcon();
        void HideTrayIcon();
    }
}
