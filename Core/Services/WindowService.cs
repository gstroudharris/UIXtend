// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UIXtend.Core.Interfaces;
using UIXtend.Core.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.Services
{
    public class WindowService : IWindowService
    {
        private readonly IShellService _shellService;
        private MainMenuWindow? _mainMenuWindow;
        private bool _isShuttingDown;

        public IntPtr MainWindowHandle { get; private set; }

        public WindowService(IShellService shellService)
        {
            _shellService = shellService;
        }

        public void Initialize()
        {
            CreateWindow();
            _shellService.OnOpenMenuRequested += ShowMainMenu;
            ShowMainMenu();
        }

        public void ShowMainMenu()
        {
            if (_mainMenuWindow == null)
            {
                _mainMenuWindow = new MainMenuWindow();

                // Minimize to tray on close rather than destroying the window,
                // unless we are in a controlled shutdown.
                _mainMenuWindow.AppWindow.Closing += (s, e) =>
                {
                    if (_isShuttingDown) return;
                    e.Cancel = true;
                    _mainMenuWindow.AppWindow.Hide();
                };

                // The window auto-sizes to its button content — prevent manual resizing
                // so the user can't shrink it below the content size.
                if (_mainMenuWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    presenter.IsResizable    = false;
                    presenter.IsMinimizable  = false;
                    presenter.IsMaximizable  = false;
                    presenter.IsAlwaysOnTop  = true;
                }

                // ── Task 4: Capture Exclusion ─────────────────────────────────────
                // Exclude the main menu from all WGC capture sessions so it never
                // appears inside a user's captured region or lens feed.
                var hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(_mainMenuWindow);
                PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
            }

            _mainMenuWindow.AppWindow.Show();
            _mainMenuWindow.Activate();
        }

        public void CreateWindow()
        {
            // Reserved: hidden/tool overlay window, WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT
        }

        public void Shutdown()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;
            _mainMenuWindow?.Close();
            _mainMenuWindow = null;
        }

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }
    }
}
