using System;
using UIXtend.Core.Interfaces;
using UIXtend.Core.UI;

namespace UIXtend.Core.Services
{
    public class WindowService : IWindowService
    {
        private readonly IShellService _shellService;
        private MainMenuWindow? _mainMenuWindow;

        public IntPtr MainWindowHandle { get; private set; }

        public WindowService(IShellService shellService)
        {
            _shellService = shellService;
        }

        public void Initialize()
        {
            // Initialization for Window Management
            CreateWindow();

            _shellService.OnOpenMenuRequested += ShowMainMenu;

            // Automatically open the Main Menu immediately after startup
            ShowMainMenu();
        }

        public void ShowMainMenu()
        {
            if (_mainMenuWindow == null)
            {
                _mainMenuWindow = new MainMenuWindow();
                // Instead of destroying the window on close, cancel it and hide it (Minimize to Tray)
                _mainMenuWindow.AppWindow.Closing += (s, e) => 
                {
                    e.Cancel = true;
                    _mainMenuWindow.AppWindow.Hide();
                };
            }

            _mainMenuWindow.AppWindow.Show();
            _mainMenuWindow.Activate();
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
