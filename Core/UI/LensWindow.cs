using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        private readonly DispatcherQueue _dispatcher;
        private CanvasSwapChainPanel? _panel;
        private CanvasSwapChain? _swapChain;
        private readonly object _swapChainLock = new();
        private float _pendingSwapW, _pendingSwapH;   // set on UI thread, consumed on render thread
        private Grid? _chromeGrid;
        private CursorBorder? _topBarElement;   // kept for cursor updates during drag
        private bool _showOverlay = true;
        private bool _closed;
        private readonly Stopwatch _openStopwatch = Stopwatch.StartNew();
        private bool _firstFrameLogged;

        private nint _hwndPtr;
        private float _dpiScale = 1f;

        // ── Drag state (physical screen pixels) ──────────────────────────────────
        private bool _isDragging;
        private int _dragStartX, _dragStartY;
        private int _dragOrigX,  _dragOrigY;

        // ── Resize state (physical screen pixels) ─────────────────────────────────
        private bool _isResizing;
        private ResizeEdge _resizeEdge;
        private int _resizeStartX, _resizeStartY;
        private RectInt32 _resizeOrigRect;

        private enum ResizeEdge { Bottom, Left, Right, BottomLeft, BottomRight }

        // ── Chrome geometry (logical pixels) ─────────────────────────────────────
        private const int TopBarLogicalH      = 28;
        private const int CloseButtonLogicalW  = 40;
        private const int ResizeHandleLogical  = 8;
        private const int CornerHandleLogical  = 16;
        private const int MinWindowLogicalW    = 120;
        private const int MinWindowLogicalH    = 60;

        // Per-frame draw timing — logged every 5 s
        private int _frameDrawCount;
        private long _totalDrawTicks;
        private long _maxDrawTicks;
        private long _lastDrawStatsTicks = Stopwatch.GetTimestamp();

        internal event Action<int>? LensClosed;

        internal bool ShowOverlay
        {
            get => _showOverlay;
            set
            {
                _showOverlay = value;
                _dispatcher.TryEnqueue(() =>
                {
                    if (_chromeGrid != null)
                        _chromeGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Constructor
        // ═════════════════════════════════════════════════════════════════════════

        public LensWindow(IRegionCapture capture, CanvasDevice device, Windows.UI.Color tintColor)
        {
            _capture    = capture;
            _device     = device;
            _tintColor  = tintColor;
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            Title = $"UIXtend — Region {capture.Id}";

            _panel = new CanvasSwapChainPanel();
            _panel.Loaded      += OnPanelLoaded;
            _panel.SizeChanged += OnPanelSizeChanged;

            _chromeGrid = BuildChromeOverlay(tintColor, capture.Id);

            var root = new Grid();
            root.Children.Add(_panel);
            root.Children.Add(_chromeGrid);
            Content = root;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable   = false;
            }

            _hwndPtr = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var hwnd = (HWND)_hwndPtr;

            AppLogger.Log($"LensWindow {capture.Id}: hwnd=0x{_hwndPtr:X} region=({capture.Region.X},{capture.Region.Y} {capture.Region.Width}x{capture.Region.Height})");

            _dpiScale = GetDpiForWindow(_hwndPtr) / 96f;

            var styleBefore = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var styleAfter  = styleBefore & ~(int)WINDOW_STYLE.WS_OVERLAPPEDWINDOW
                                          | unchecked((int)WINDOW_STYLE.WS_POPUP);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, styleAfter);
            PInvoke.SetWindowPos(hwnd, default, 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
            AppLogger.Log($"  WS style: 0x{styleBefore:X8} -> 0x{styleAfter:X8} (SWP_FRAMECHANGED applied)");

            AppWindow.MoveAndResize(new RectInt32(
                (int)capture.Region.X, (int)capture.Region.Y,
                (int)capture.Region.Width, (int)capture.Region.Height));

            unsafe
            {
                var pref = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
                var hrCorner = PInvoke.DwmSetWindowAttribute(hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                    &pref, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
                AppLogger.Log($"  DWMWA_WINDOW_CORNER_PREFERENCE=DONOTROUND hr=0x{hrCorner.Value:X8} ({(hrCorner.Value == 0 ? "OK" : "FAILED")})");

                int ncrpDisabled = 1;
                var hrNcr = PInvoke.DwmSetWindowAttribute(hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_NCRENDERING_POLICY,
                    &ncrpDisabled, (uint)sizeof(int));
                AppLogger.Log($"  DWMWA_NCRENDERING_POLICY=DISABLED hr=0x{hrNcr.Value:X8} ({(hrNcr.Value == 0 ? "OK" : "FAILED")})");
            }

            PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);

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

        // ═════════════════════════════════════════════════════════════════════════
        //  Chrome overlay
        // ═════════════════════════════════════════════════════════════════════════

        private Grid BuildChromeOverlay(Windows.UI.Color tint, int captureId)
        {
            var fg       = LensColorPalette.GetForeground(tint);
            var topBarBg = Windows.UI.Color.FromArgb(200, tint.R, tint.G, tint.B);
            var edgeBg   = Windows.UI.Color.FromArgb(160, tint.R, tint.G, tint.B);

            var tintOverlay = new Border
            {
                Background       = new SolidColorBrush(Windows.UI.Color.FromArgb(50, tint.R, tint.G, tint.B)),
                IsHitTestVisible = false
            };

            var edgeBorder = new Border
            {
                BorderBrush      = new SolidColorBrush(edgeBg),
                BorderThickness  = new Thickness(2),
                IsHitTestVisible = false
            };

            // ── Top bar ───────────────────────────────────────────────────────────
            var label = new TextBlock
            {
                Text              = $"Capture {captureId}",
                Foreground        = new SolidColorBrush(fg),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily        = new FontFamily("Segoe UI Variable"),
                FontSize          = 13,
                Margin            = new Thickness(10, 0, 0, 0)
            };

            var closeBtn = new Button
            {
                Content                    = "✕",
                Width                      = CloseButtonLogicalW,
                Padding                    = new Thickness(0),
                HorizontalAlignment        = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                FontSize                   = 11,
                VerticalAlignment          = VerticalAlignment.Stretch
            };
            SolidColorBrush Trans()   => new(Windows.UI.Color.FromArgb(0,   0, 0, 0));
            SolidColorBrush Hover()   => new(Windows.UI.Color.FromArgb(50,  0, 0, 0));
            SolidColorBrush Press()   => new(Windows.UI.Color.FromArgb(100, 0, 0, 0));
            SolidColorBrush FgBrush() => new(fg);
            closeBtn.Resources["ButtonBackground"]             = Trans();
            closeBtn.Resources["ButtonBackgroundPointerOver"]  = Hover();
            closeBtn.Resources["ButtonBackgroundPressed"]      = Press();
            closeBtn.Resources["ButtonBackgroundDisabled"]     = Trans();
            closeBtn.Resources["ButtonForeground"]             = FgBrush();
            closeBtn.Resources["ButtonForegroundPointerOver"]  = FgBrush();
            closeBtn.Resources["ButtonForegroundPressed"]      = FgBrush();
            closeBtn.Resources["ButtonBorderBrush"]            = Trans();
            closeBtn.Resources["ButtonBorderBrushPointerOver"] = Trans();
            closeBtn.Resources["ButtonBorderBrushPressed"]     = Trans();
            closeBtn.Click += (s, e) => this.Close();

            var topBarContent = new Grid();
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label,    0);
            Grid.SetColumn(closeBtn, 1);
            topBarContent.Children.Add(label);
            topBarContent.Children.Add(closeBtn);

            // CursorBorder lets us set ProtectedCursor (WinUI 3's official cursor API).
            // Default cursor is Arrow; switches to SizeAll while the user drags.
            _topBarElement = new CursorBorder
            {
                Height            = TopBarLogicalH,
                VerticalAlignment = VerticalAlignment.Top,
                Background        = new SolidColorBrush(topBarBg),
                Cursor            = InputSystemCursorShape.Arrow
            };
            _topBarElement.Children.Add(topBarContent);
            _topBarElement.PointerPressed     += OnTopBarPointerPressed;
            _topBarElement.PointerMoved       += OnTopBarPointerMoved;
            _topBarElement.PointerReleased    += OnTopBarPointerReleased;
            _topBarElement.PointerCaptureLost += (s, e) =>
            {
                _isDragging = false;
                _topBarElement.Cursor = InputSystemCursorShape.Arrow;
                AppLogger.Log($"  LensWindow {_capture.Id}: drag capture lost");
            };

            // ── Resize handles (transparent, cursor set via ProtectedCursor) ──────
            var trans = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            var bottomH = new CursorBorder
            {
                Height              = ResizeHandleLogical,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(CornerHandleLogical, 0, CornerHandleLogical, 0),
                Background          = trans,
                IsHitTestVisible    = true,
                Cursor              = InputSystemCursorShape.SizeNorthSouth
            };
            var leftH = new CursorBorder
            {
                Width               = ResizeHandleLogical,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, TopBarLogicalH, 0, CornerHandleLogical),
                Background          = trans,
                IsHitTestVisible    = true,
                Cursor              = InputSystemCursorShape.SizeWestEast
            };
            var rightH = new CursorBorder
            {
                Width               = ResizeHandleLogical,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, TopBarLogicalH, 0, CornerHandleLogical),
                Background          = trans,
                IsHitTestVisible    = true,
                Cursor              = InputSystemCursorShape.SizeWestEast
            };
            var blH = new CursorBorder
            {
                Width               = CornerHandleLogical,
                Height              = CornerHandleLogical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Background          = trans,
                IsHitTestVisible    = true,
                Cursor              = InputSystemCursorShape.SizeNortheastSouthwest
            };
            var brH = new CursorBorder
            {
                Width               = CornerHandleLogical,
                Height              = CornerHandleLogical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Background          = trans,
                IsHitTestVisible    = true,
                Cursor              = InputSystemCursorShape.SizeNorthwestSoutheast
            };

            WireResizeHandle(bottomH, ResizeEdge.Bottom);
            WireResizeHandle(leftH,   ResizeEdge.Left);
            WireResizeHandle(rightH,  ResizeEdge.Right);
            WireResizeHandle(blH,     ResizeEdge.BottomLeft);
            WireResizeHandle(brH,     ResizeEdge.BottomRight);

            var chrome = new Grid { Visibility = Visibility.Visible };
            chrome.Children.Add(tintOverlay);
            chrome.Children.Add(edgeBorder);
            chrome.Children.Add(_topBarElement);
            chrome.Children.Add(bottomH);
            chrome.Children.Add(leftH);
            chrome.Children.Add(rightH);
            chrome.Children.Add(blH);
            chrome.Children.Add(brH);
            return chrome;
        }

        private void WireResizeHandle(CursorBorder handle, ResizeEdge edge)
        {
            // ProtectedCursor is already set on each handle at construction time.
            // It persists through CapturePointer so the cursor stays correct during resize.
            handle.PointerPressed     += (s, e) => BeginResize(edge, (UIElement)s, e);
            handle.PointerMoved       += (s, e) => DoResize(e);
            handle.PointerReleased    += (s, e) => EndResize((UIElement)s, e);
            handle.PointerCaptureLost += (s, e) => { _isResizing = false; };
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Drag-to-move
        // ═════════════════════════════════════════════════════════════════════════

        private void OnTopBarPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt       = e.GetCurrentPoint(null).Position;
            var logicalW = AppWindow.Size.Width / _dpiScale;
            if (pt.X >= logicalW - CloseButtonLogicalW) return;

            GetCursorPos(out var cursor);
            _isDragging  = true;
            _dragStartX  = cursor.X;
            _dragStartY  = cursor.Y;
            _dragOrigX   = AppWindow.Position.X;
            _dragOrigY   = AppWindow.Position.Y;

            // Switch top-bar cursor to SizeAll for the duration of the drag.
            // ProtectedCursor persists through CapturePointer — the cursor stays
            // as SizeAll even as the pointer leaves the top-bar bounds.
            _topBarElement!.Cursor = InputSystemCursorShape.SizeAll;
            ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
            AppLogger.Log($"  LensWindow {_capture.Id}: BeginDrag at screen ({cursor.X},{cursor.Y}) origPos=({_dragOrigX},{_dragOrigY})");
        }

        private void OnTopBarPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            GetCursorPos(out var cursor);
            AppWindow.Move(new PointInt32(_dragOrigX + (cursor.X - _dragStartX),
                                          _dragOrigY + (cursor.Y - _dragStartY)));
            e.Handled = true;
        }

        private void OnTopBarPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _topBarElement!.Cursor = InputSystemCursorShape.Arrow;   // restore hover cursor
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            AppLogger.Log($"  LensWindow {_capture.Id}: EndDrag -> ({AppWindow.Position.X},{AppWindow.Position.Y})");
            e.Handled = true;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Resize
        // ═════════════════════════════════════════════════════════════════════════

        private void BeginResize(ResizeEdge edge, UIElement element, PointerRoutedEventArgs e)
        {
            GetCursorPos(out var pt);
            _isResizing     = true;
            _resizeEdge     = edge;
            _resizeStartX   = pt.X;
            _resizeStartY   = pt.Y;
            _resizeOrigRect = new RectInt32(
                AppWindow.Position.X, AppWindow.Position.Y,
                AppWindow.Size.Width,  AppWindow.Size.Height);
            element.CapturePointer(e.Pointer);
            e.Handled = true;
            AppLogger.Log($"  LensWindow {_capture.Id}: BeginResize {edge} at screen ({pt.X},{pt.Y}) origRect=({_resizeOrigRect.X},{_resizeOrigRect.Y} {_resizeOrigRect.Width}x{_resizeOrigRect.Height})");
        }

        private void DoResize(PointerRoutedEventArgs e)
        {
            if (!_isResizing) return;

            try
            {
                GetCursorPos(out var pt);
                int dx   = pt.X - _resizeStartX;
                int dy   = pt.Y - _resizeStartY;
                int minW = (int)(_dpiScale * MinWindowLogicalW);
                int minH = (int)(_dpiScale * MinWindowLogicalH);

                int x = _resizeOrigRect.X, y = _resizeOrigRect.Y;
                int w = _resizeOrigRect.Width, h = _resizeOrigRect.Height;

                switch (_resizeEdge)
                {
                    case ResizeEdge.Bottom:
                        h = Math.Max(minH, h + dy);
                        break;
                    case ResizeEdge.Left:
                    {
                        int nw = Math.Max(minW, w - dx);
                        x += w - nw; w = nw;
                        break;
                    }
                    case ResizeEdge.Right:
                        w = Math.Max(minW, w + dx);
                        break;
                    case ResizeEdge.BottomLeft:
                    {
                        h = Math.Max(minH, h + dy);
                        int nw = Math.Max(minW, w - dx);
                        x += w - nw; w = nw;
                        break;
                    }
                    case ResizeEdge.BottomRight:
                        h = Math.Max(minH, h + dy);
                        w = Math.Max(minW, w + dx);
                        break;
                }

                var newRect = new RectInt32(x, y, w, h);
                AppLogger.Log($"  LensWindow {_capture.Id}: DoResize {_resizeEdge} cursor=({pt.X},{pt.Y}) d=({dx},{dy}) -> rect=({newRect.X},{newRect.Y} {newRect.Width}x{newRect.Height})");
                AppWindow.MoveAndResize(newRect);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"  LensWindow {_capture.Id}: DoResize EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                _isResizing = false;
            }

            e.Handled = true;
        }

        private void EndResize(UIElement element, PointerRoutedEventArgs e)
        {
            if (!_isResizing) return;
            _isResizing = false;
            element.ReleasePointerCapture(e.Pointer);
            AppLogger.Log($"  LensWindow {_capture.Id}: EndResize {_resizeEdge} -> ({AppWindow.Position.X},{AppWindow.Position.Y} {AppWindow.Size.Width}x{AppWindow.Size.Height})");
            e.Handled = true;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Swap chain setup
        // ═════════════════════════════════════════════════════════════════════════

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            if (_closed || _panel == null) return;

            var scale = (float)(_panel.XamlRoot?.RasterizationScale ?? 1.0);
            var physW = Math.Max(1f, (float)_panel.ActualWidth  * scale);
            var physH = Math.Max(1f, (float)_panel.ActualHeight * scale);

            AppLogger.Log($"  OnPanelLoaded id={_capture.Id}: panel={_panel.ActualWidth}x{_panel.ActualHeight} dips, scale={scale}, swapChain={physW}x{physH} phys — {_openStopwatch.ElapsedMilliseconds} ms since ctor");

            // Create and assign the swap chain OUTSIDE any lock.
            // CanvasSwapChain creation and _panel.SwapChain assignment both touch the DXGI/DWM
            // compositor, which requires the UI thread message queue to be live.  Holding
            // _swapChainLock here would deadlock if the WGC thread is simultaneously inside the
            // lock calling Present(0) — Present flushes through DWM, which pumps the UI queue.
            CanvasSwapChain swapChain;
            try
            {
                swapChain = new CanvasSwapChain(_device, physW, physH, 96f);
                _panel.SwapChain = swapChain;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"  LensWindow {_capture.Id}: OnPanelLoaded swap chain EXCEPTION hr=0x{ex.HResult:X8}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Only the reference assignment needs the lock — this is a pointer write, sub-microsecond.
            lock (_swapChainLock)
            {
                if (_closed) { swapChain.Dispose(); return; }
                _dpiScale  = scale;
                _swapChain = swapChain;
            }

            AppLogger.Log($"  LensWindow {_capture.Id}: subscribing to FrameArrived");
            _capture.FrameArrived += OnFrameArrived;
        }

        private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_closed || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
            var scale = (float)(_panel?.XamlRoot?.RasterizationScale ?? 1.0);
            // Record desired size; ResizeBuffers is applied on the render thread in OnFrameArrived
            // to avoid cross-thread DXGI interaction (which causes native AVs on .NET 5+).
            lock (_swapChainLock)
            {
                _pendingSwapW = Math.Max(1f, (float)e.NewSize.Width  * scale);
                _pendingSwapH = Math.Max(1f, (float)e.NewSize.Height * scale);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Frame rendering
        // ═════════════════════════════════════════════════════════════════════════

        private void OnFrameArrived(object? sender, CanvasBitmap fullMonitorBitmap)
        {
            var drawStart = Stopwatch.GetTimestamp();

            try
            {
                // Grab the swap chain reference and any pending resize under the lock.
                // GPU operations (ResizeBuffers, CreateDrawingSession, Present) happen outside
                // the lock — Present touches the DWM compositor, which can deadlock if the UI
                // thread is simultaneously blocked in lock(_swapChainLock) during OnPanelLoaded.
                CanvasSwapChain? swapChain;
                float pendingW = 0, pendingH = 0;
                lock (_swapChainLock)
                {
                    if (_swapChain == null || _closed) return;
                    swapChain = _swapChain;
                    if (_pendingSwapW > 0)
                    {
                        pendingW = _pendingSwapW; pendingH = _pendingSwapH;
                        _pendingSwapW = 0; _pendingSwapH = 0;
                    }
                }

                if (pendingW > 0 &&
                    (Math.Abs(swapChain.Size.Width  - pendingW) > 0.5f ||
                     Math.Abs(swapChain.Size.Height - pendingH) > 0.5f))
                {
                    AppLogger.Log($"  LensWindow {_capture.Id}: ResizeBuffers {pendingW}x{pendingH}");
                    swapChain.ResizeBuffers(pendingW, pendingH, 96f);
                }

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AppLogger.Log($"  First frame id={_capture.Id}: {_openStopwatch.ElapsedMilliseconds} ms since ctor, thread={Environment.CurrentManagedThreadId}");
                }

                var size = swapChain.Size;
                using var ds = swapChain.CreateDrawingSession(Colors.Black);
                ds.DrawImage(
                    fullMonitorBitmap,
                    new Rect(0, 0, size.Width, size.Height),
                    _capture.CropRect);
                ds.Dispose();        // flush draw commands before Present
                swapChain.Present(0); // outside lock — compositor-facing, must not hold lock
            }
            catch (Exception ex) when (IsDeviceLostHResult(ex.HResult)) { }
            catch (Exception ex)
            {
                AppLogger.Log($"  LensWindow {_capture.Id}: OnFrameArrived EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }

            var drawTicks = Stopwatch.GetTimestamp() - drawStart;
            _frameDrawCount++;
            _totalDrawTicks += drawTicks;
            if (drawTicks > _maxDrawTicks) _maxDrawTicks = drawTicks;

            var now = Stopwatch.GetTimestamp();
            if (now - _lastDrawStatsTicks >= Stopwatch.Frequency * 5)
            {
                var avgMs = _totalDrawTicks * 1000.0 / Stopwatch.Frequency / _frameDrawCount;
                var maxMs = _maxDrawTicks   * 1000.0 / Stopwatch.Frequency;
                AppLogger.Log($"  [perf] LensWindow {_capture.Id}: {_frameDrawCount / 5.0:F1} fps, draw avg={avgMs:F2}ms max={maxMs:F2}ms thread={Environment.CurrentManagedThreadId}");
                _frameDrawCount = 0;
                _totalDrawTicks = 0;
                _maxDrawTicks   = 0;
                _lastDrawStatsTicks = now;
            }
        }

        private static bool IsDeviceLostHResult(int hr) =>
            unchecked((uint)hr) is 0x887A0005u or 0x887A0007u or 0x887A0026u;

        // ═════════════════════════════════════════════════════════════════════════
        //  Cleanup
        // ═════════════════════════════════════════════════════════════════════════

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
            => CleanupResources();

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
            LensClosed?.Invoke(_capture.Id);
            AppLogger.Log($"  Lens {_capture.Id} closed");
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  CursorBorder — exposes ProtectedCursor (WinUI 3's official cursor API)
        // ═════════════════════════════════════════════════════════════════════════

        // UIElement.ProtectedCursor is protected, so we subclass Border to expose
        // it. Setting ProtectedCursor is the only reliable cursor mechanism in
        // WinUI 3 — Win32 SetCursor calls are overridden by the WM_POINTER pipeline.
        // The cursor set here persists through CapturePointer, so drag/resize cursors
        // automatically stay correct even as the pointer moves outside the element.
        private class CursorBorder : Grid
        {
            internal InputSystemCursorShape Cursor
            {
                set => ProtectedCursor = InputSystemCursor.Create(value);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  P/Invoke
        // ═════════════════════════════════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORPOINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CURSORPOINT pt);
    }
}
