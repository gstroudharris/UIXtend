using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using UIXtend.Core.Interfaces;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.UI
{
    internal class LensWindow : Window
    {
        private readonly IRegionCapture _capture;
        private readonly CanvasDevice _device;
        private CanvasSwapChainPanel? _panel;
        private CanvasSwapChain? _swapChain;
        private readonly object _swapChainLock = new();
        private bool _closed;

        /// <summary>Fires with the capture ID when this window fully closes and cleans up.</summary>
        internal event Action<int>? LensClosed;

        public LensWindow(IRegionCapture capture, CanvasDevice device)
        {
            _capture = capture;
            _device = device;

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

            // Place the window at exactly the captured region — 1:1 physical pixels,
            // no scaling, no border offset. The swap chain fills the entire client area.
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                (int)capture.Region.X,
                (int)capture.Region.Y,
                (int)capture.Region.Width,
                (int)capture.Region.Height));

            // ── Task 4: Capture Exclusion ─────────────────────────────────────────
            // WDA_EXCLUDEFROMCAPTURE makes this window invisible to all WGC sessions,
            // preventing the lens from appearing inside its own captured feed.
            var hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
            PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);

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
