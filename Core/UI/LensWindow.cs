using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using UIXtend.Core;
using UIXtend.Core.Interfaces;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.UI
{
    internal class LensWindow : Window
    {
        private readonly IRegionCapture _capture;
        private readonly CanvasDevice _device;
        private readonly Windows.UI.Color _tintColor;
        private CanvasSwapChainPanel? _panel;
        private CanvasSwapChain? _swapChain;
        private readonly object _swapChainLock = new();
        private bool _closed;

        /// <summary>Fires with the capture ID when this window fully closes and cleans up.</summary>
        internal event Action<int>? LensClosed;

        public LensWindow(IRegionCapture capture, CanvasDevice device, Windows.UI.Color tintColor)
        {
            _capture = capture;
            _device = device;
            _tintColor = tintColor;

            Title = $"UIXtend — Region {capture.Id}";

            _panel = new CanvasSwapChainPanel();
            _panel.Loaded += OnPanelLoaded;
            _panel.SizeChanged += OnPanelSizeChanged;
            Content = _panel;

            // Borderless, always-on-top overlay — user manages it via the main menu
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
            }

            var hwndPtr = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var hwnd = (HWND)hwndPtr;

            AppLogger.Log($"LensWindow {capture.Id}: hwnd=0x{hwndPtr:X} region=({capture.Region.X},{capture.Region.Y} {capture.Region.Width}x{capture.Region.Height})");

            // ── Pixel-perfect borderless ──────────────────────────────────────────
            // SetWindowLong(WS_POPUP) changes the style but Windows won't recalculate
            // the frame metrics until SWP_FRAMECHANGED is sent. Without it the old
            // WS_DLGFRAME border (3 px per side) stays in the non-client area, making
            // the client rect 6 px smaller than the outer window rect — exactly the
            // black bar on the right and bottom. Apply the style change and force a
            // frame recalculation BEFORE MoveAndResize so the outer rect = client rect.
            var styleBefore = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var styleAfter = styleBefore & ~(int)WINDOW_STYLE.WS_OVERLAPPEDWINDOW | unchecked((int)WINDOW_STYLE.WS_POPUP);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, styleAfter);
            PInvoke.SetWindowPos(hwnd, default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            AppLogger.Log($"  WS style: 0x{styleBefore:X8} -> 0x{styleAfter:X8} (SWP_FRAMECHANGED applied)");

            // Now position the window — with WS_POPUP in effect the outer rect = client rect.
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                (int)capture.Region.X,
                (int)capture.Region.Y,
                (int)capture.Region.Width,
                (int)capture.Region.Height));

            unsafe
            {
                // Windows 11 rounds top-level window corners by default; the clipped area
                // shows as white. Force square corners for pixel-perfect edges.
                var pref = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
                var hrCorner = PInvoke.DwmSetWindowAttribute(hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                    &pref,
                    (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
                AppLogger.Log($"  DWMWA_WINDOW_CORNER_PREFERENCE=DONOTROUND hr=0x{hrCorner.Value:X8} ({(hrCorner.Value == 0 ? "OK" : "FAILED")})");

                // DWM adds a drop shadow that extends a few pixels beyond the window bounds
                // on the right and bottom, causing both a visual artifact and a position offset.
                // Disabling non-client rendering removes it entirely.
                int ncrpDisabled = 1; // DWMNCRP_DISABLED
                var hrNcr = PInvoke.DwmSetWindowAttribute(hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_NCRENDERING_POLICY,
                    &ncrpDisabled,
                    (uint)sizeof(int));
                AppLogger.Log($"  DWMWA_NCRENDERING_POLICY=DISABLED hr=0x{hrNcr.Value:X8} ({(hrNcr.Value == 0 ? "OK" : "FAILED")})");
            }

            // ── Task 4: Capture Exclusion ─────────────────────────────────────────
            // WDA_EXCLUDEFROMCAPTURE makes this window invisible to all WGC sessions,
            // preventing the lens from appearing inside its own captured feed.
            PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);

            // ── Diagnostic: compare outer (window) rect vs inner (client) rect ─────
            // If they differ, the non-client area is non-zero (border / DWM shadow).
            unsafe
            {
                RECT winRect = default, clientRect = default;
                PInvoke.GetWindowRect(hwnd, &winRect);
                PInvoke.GetClientRect(hwnd, &clientRect);
                AppLogger.Log($"  GetWindowRect:  ({winRect.left},{winRect.top}) {winRect.Width}x{winRect.Height}");
                AppLogger.Log($"  GetClientRect:  ({clientRect.left},{clientRect.top}) {clientRect.Width}x{clientRect.Height}");
                var exStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                AppLogger.Log($"  WS_EX style:    0x{exStyle:X8}");
            }

            AppWindow.Closing += OnAppWindowClosing;
        }

        // ── Swap chain setup ──────────────────────────────────────────────────────

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            lock (_swapChainLock)
            {
                if (_closed || _panel == null) return;
                var scale = (float)(_panel.XamlRoot?.RasterizationScale ?? 1.0);
                var physW = Math.Max(1f, (float)_panel.ActualWidth * scale);
                var physH = Math.Max(1f, (float)_panel.ActualHeight * scale);

                AppLogger.Log($"  OnPanelLoaded id={_capture.Id}: panel={_panel.ActualWidth}x{_panel.ActualHeight} dips, scale={scale}, swapChain={physW}x{physH} phys");

                // 96 DPI means 1 drawing unit = 1 physical pixel — simplest coordinate system.
                _swapChain = new CanvasSwapChain(_device, physW, physH, 96f);
                _panel.SwapChain = _swapChain;
            }

            // Subscribe after the swap chain is ready
            _capture.FrameArrived += OnFrameArrived;
        }

        private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            lock (_swapChainLock)
            {
                if (_closed || _swapChain == null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
                var scale = (float)(_panel?.XamlRoot?.RasterizationScale ?? 1.0);
                var physW = Math.Max(1f, (float)e.NewSize.Width * scale);
                var physH = Math.Max(1f, (float)e.NewSize.Height * scale);
                _swapChain.ResizeBuffers(physW, physH, 96f);
            }
        }

        // ── Frame rendering ───────────────────────────────────────────────────────

        private void OnFrameArrived(object? sender, CanvasBitmap fullMonitorBitmap)
        {
            CanvasSwapChain? swapChain;
            lock (_swapChainLock) swapChain = _swapChain;
            if (swapChain == null || _closed) return;

            try
            {
                // At 96 DPI, Size.Width/Height == physical pixels. DrawImage GPU-scales
                // the captured CropRect to fill the entire swap chain surface.
                var size = swapChain.Size;
                using var ds = swapChain.CreateDrawingSession(Colors.Black);
                ds.DrawImage(
                    fullMonitorBitmap,
                    new Rect(0, 0, size.Width, size.Height),
                    _capture.CropRect);
                // Tint overlay: 50/255 ≈ 20% opacity
                ds.FillRectangle(
                    new Rect(0, 0, size.Width, size.Height),
                    Windows.UI.Color.FromArgb(50, _tintColor.R, _tintColor.G, _tintColor.B));

                // Centered label
                var label = $"Capture {_capture.Id}";
                using var fmt = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI Variable",
                    FontSize = 18f,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                // Semi-transparent dark background pill for legibility
                using var layout = new CanvasTextLayout(ds, label, fmt, (float)size.Width, (float)size.Height);
                var tb = layout.LayoutBounds;
                const float padX = 12f, padY = 6f;
                var pill = new Rect(
                    size.Width / 2 - tb.Width / 2 - padX,
                    size.Height / 2 - tb.Height / 2 - padY,
                    tb.Width + padX * 2,
                    tb.Height + padY * 2);
                ds.FillRoundedRectangle(pill, 6f, 6f, Windows.UI.Color.FromArgb(140, 0, 0, 0));
                ds.DrawTextLayout(layout, 0f, 0f, Colors.White);

                swapChain.Present();
            }
            catch (Exception ex) when (IsDeviceLostHResult(ex.HResult))
            {
                // GPU device lost — skip frame; next frame will attempt again.
            }
        }

        private static bool IsDeviceLostHResult(int hr) =>
            unchecked((uint)hr) is 0x887A0005u  // DXGI_ERROR_DEVICE_REMOVED
                                or 0x887A0007u  // DXGI_ERROR_DEVICE_RESET
                                or 0x887A0026u; // DXGI_ERROR_DEVICE_HUNG

        // ── Cleanup ───────────────────────────────────────────────────────────────

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            CleanupResources();
        }

        private void CleanupResources()
        {
            if (_closed) return;
            _closed = true;

            _capture.FrameArrived -= OnFrameArrived;

            lock (_swapChainLock)
            {
                _swapChain?.Dispose();
                _swapChain = null;
            }

            _capture.Dispose();

            // Notify LensService so it can update ActiveLenses and fire LensesChanged
            LensClosed?.Invoke(_capture.Id);
        }
    }
}
