using System;

namespace UIXtend.Core.Interfaces
{
    /// <summary>
    /// Manages the application's underlying Win32 window handles and styles.
    /// </summary>
    public interface IWindowService : IService
    {
        IntPtr MainWindowHandle { get; }
        
        /// <summary>
        /// Creates the main overlay window with WS_EX_TOOLWINDOW and WS_EX_TRANSPARENT styles.
        /// </summary>
        void CreateWindow();
        void ShowMainMenu();
    }
}
