// Copyright (C) 2026  Grant Harris
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UIXtend.Core.Interfaces;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.Services
{
    public class ShellService : IShellService
    {
        public event Action? OnOpenMenuRequested;
        public event Action? OnExitRequested;
        private HWND _messageWindowHandle;
        private const uint WM_TRAYICON = PInvoke.WM_USER + 1;
        private WNDPROC? _wndProcDelegate; // Prevent GC
        private string _className = "UIXtendTrayIconApp";

        public unsafe void Initialize()
        {
            CreateMessageWindow();
            ShowTrayIcon();
            if (IsFirstRun())
                ShowBalloonTip();
        }

        private static bool IsFirstRun()
        {
            const string keyPath = @"Software\UIXtend";
            const string valueName = "ShownTrayTip";
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) != null) return false;
            using var writeKey = Registry.CurrentUser.CreateSubKey(keyPath);
            writeKey.SetValue(valueName, 1);
            return true;
        }

        private unsafe void CreateMessageWindow()
        {
            _wndProcDelegate = WndProc;
            var hInst = PInvoke.GetModuleHandle((string?)null);
            
            fixed (char* classNamePtr = _className)
            {
                var wndClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProcDelegate,
                    hInstance = (HINSTANCE)hInst.DangerousGetHandle(),
                    lpszClassName = classNamePtr
                };

                PInvoke.RegisterClassEx(in wndClass);

                _messageWindowHandle = PInvoke.CreateWindowEx(
                    0,
                    _className, // CsWin32 overload takes string
                    "UIXtend Message Window",
                    0,
                    0, 0, 0, 0,
                    (HWND)(IntPtr)(-3), // HWND_MESSAGE
                    default,
                    hInst, // Argument 11 takes SafeHandle, which hInst is
                    null);
            }
        }

        private unsafe LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            if (msg == WM_TRAYICON)
            {
                var lparamMsg = (uint)(lParam.Value & 0xFFFF);
                if (lparamMsg == PInvoke.WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                else if (lparamMsg == PInvoke.WM_LBUTTONDBLCLK)
                {
                    OnOpenMenuRequested?.Invoke();
                }
            }
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private const uint IMAGE_ICON     = 1;
        private const uint LR_LOADFROMFILE = 0x0010;
        private const uint LR_DEFAULTSIZE  = 0x0040;

        private unsafe void ShowContextMenu()
        {
            PInvoke.GetCursorPos(out var pt);
            
            var hMenu = PInvoke.CreatePopupMenu();
            
            // ID 2 for Open
            AppendMenuW((IntPtr)hMenu.Value, 0x0000, 2, "Open UIXtend");
            // ID 1 for Exit, MF_STRING is 0
            AppendMenuW((IntPtr)hMenu.Value, 0x0000, 1, "Exit UIXtend");

            // Required to dismiss menu when clicking away
            PInvoke.SetForegroundWindow(_messageWindowHandle);
            
            var cmd = PInvoke.TrackPopupMenu(hMenu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY, pt.X, pt.Y, 0, _messageWindowHandle, null);

            if (cmd.Value == 1)
            {
                // Hide tray instantly for a snappy UX — shutdown is handled by the subscriber
                HideTrayIcon();
                OnExitRequested?.Invoke();
            }
            else if (cmd.Value == 2)
            {
                OnOpenMenuRequested?.Invoke();
            }

            PInvoke.DestroyMenu(hMenu);
        }

        private unsafe HICON LoadCustomIcon()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "UIXtend.ico");
            var ptr  = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            // Fall back to the default app icon if the file is missing
            return ptr != IntPtr.Zero
                ? new HICON(ptr)
                : PInvoke.LoadIcon(default, (PCWSTR)(char*)32512);
        }

        public unsafe void ShowTrayIcon()
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _messageWindowHandle,
                uID = 1,
                uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = LoadCustomIcon(), // UIXtend.ico
            };
            
            var tipStr = "UIXtend Overlay";
            fixed (char* pTipStr = tipStr)
            {
                char* dest = (char*)&nid.szTip;
                for (int i = 0; i < tipStr.Length && i < 127; i++)
                {
                    dest[i] = pTipStr[i];
                }
                dest[tipStr.Length] = '\0';
            }

            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in nid);
        }

        private unsafe void ShowBalloonTip()
        {
            const string title = "UIXtend is running in your tray!";
            const string body  = "Double-click the tray icon anytime to open the menu.";

            var nid = new NOTIFYICONDATAW
            {
                cbSize      = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd        = _messageWindowHandle,
                uID         = 1,
                uFlags      = NOTIFY_ICON_DATA_FLAGS.NIF_INFO,
                dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO
                            | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_RESPECT_QUIET_TIME,
            };
            nid.Anonymous.uTimeout = 5000;

            fixed (char* pTitle = title)
            fixed (char* pBody  = body)
            {
                char* dest = (char*)&nid.szInfoTitle;
                for (int i = 0; i < title.Length && i < 63; i++) dest[i] = pTitle[i];
                dest[title.Length] = '\0';

                dest = (char*)&nid.szInfo;
                for (int i = 0; i < body.Length && i < 255; i++) dest[i] = pBody[i];
                dest[body.Length] = '\0';
            }

            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in nid);
        }

        public unsafe void HideTrayIcon()
        {
            if (!_messageWindowHandle.IsNull)
            {
                var nid = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _messageWindowHandle,
                    uID = 1
                };
                PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in nid);
            }
        }

        public void Dispose()
        {
            HideTrayIcon();
            if (!_messageWindowHandle.IsNull)
            {
                PInvoke.DestroyWindow(_messageWindowHandle);
                _messageWindowHandle = default;
            }
            GC.SuppressFinalize(this);
        }
    }
}
