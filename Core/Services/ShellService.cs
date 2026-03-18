using System;
using System.Runtime.InteropServices;
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
        private HWND _messageWindowHandle;
        private const uint WM_TRAYICON = PInvoke.WM_USER + 1;
        private WNDPROC? _wndProcDelegate; // Prevent GC
        private string _className = "UIXtendTrayIconApp";

        public unsafe void Initialize()
        {
            CreateMessageWindow();
            ShowTrayIcon();
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
                // Hide tray instantly for a snappy "instant quit" UX instead of waiting for full teardown
                HideTrayIcon();
                
                // Spin up a graceful host stoppage and forcefully exit the WinUI environment loops
                System.Threading.Tasks.Task.Run(() => 
                {
                    ServiceHost.StopAsync().GetAwaiter().GetResult();
                    Environment.Exit(0);
                });
            }
            else if (cmd.Value == 2)
            {
                OnOpenMenuRequested?.Invoke();
            }

            PInvoke.DestroyMenu(hMenu);
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
                hIcon = PInvoke.LoadIcon(default, (PCWSTR)(char*)32512), // IDI_APPLICATION
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
