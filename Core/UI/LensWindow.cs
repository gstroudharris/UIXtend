using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using UIXtend.Core.Services;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using UIXtend.Core;
using UIXtend.Core.Interfaces;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
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
        private CursorBorder? _contentSurface;  // input-forwarding hit surface (below top bar)
        private bool _showOverlay = true;
        private bool _closed;
        private readonly Stopwatch _openStopwatch = Stopwatch.StartNew();
        private bool _firstFrameLogged;

        // ── Input forwarding state ────────────────────────────────────────────────
        private bool _inputForwardingEnabled;

        // ── Live capture state ────────────────────────────────────────────────────
        private bool _captureLive = true;
        private CanvasRenderTarget? _frozenFrame;   // GPU-side snapshot; owned by LensWindow
        private bool _needFrozenFrame;              // set on UI thread, consumed on WGC thread

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
            // Window.Closed fires after the HWND is actually destroyed — reliable fallback
            // for popup-style windows where AppWindow.Closing may not fire via WM_CLOSE.
            // CleanupResources is idempotent so firing twice is harmless.
            this.Closed += (_, _) => CleanupResources();
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
                FontWeight        = FontWeights.SemiBold,
                FontSize          = 14,
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
                FontSize                   = 12,
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

            // ── Interactive-mode toggle button ────────────────────────────────────
            // BitmapIcon with ShowAsMonochrome=true treats the PNG as a mask:
            // dark pixels → foreground colour, transparent pixels → transparent.
            // The Fluent icon (dark cursor on transparent bg) tints correctly with
            // whatever ToggleButtonForeground* resource is active for the current state.
            // AppContext.BaseDirectory resolves to the exe folder; the asset is copied
            // there by the <Content> item in the csproj (unpackaged app — no ms-appx://).
            var toggleBtnIconUri = new Uri(
                System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "assets",
                    "ic_fluent_cursor_click_24_filled.png"));
            var toggleBtn = new ToggleButton
            {
                Content = new BitmapIcon
                {
                    UriSource        = toggleBtnIconUri,
                    ShowAsMonochrome = true,
                    Width            = 16,
                    Height           = 16
                },
                Width                      = CloseButtonLogicalW,
                Padding                    = new Thickness(0),
                HorizontalAlignment        = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                VerticalAlignment          = VerticalAlignment.Stretch
            };
            ToolTipService.SetToolTip(toggleBtn, "Toggle input forwarding");
            // Unchecked state — matches close button transparency
            toggleBtn.Resources["ToggleButtonBackground"]                        = Trans();
            toggleBtn.Resources["ToggleButtonBackgroundPointerOver"]             = Hover();
            toggleBtn.Resources["ToggleButtonBackgroundPressed"]                 = Press();
            toggleBtn.Resources["ToggleButtonBackgroundDisabled"]                = Trans();
            toggleBtn.Resources["ToggleButtonForeground"]                        = FgBrush();
            toggleBtn.Resources["ToggleButtonForegroundPointerOver"]             = FgBrush();
            toggleBtn.Resources["ToggleButtonForegroundPressed"]                 = FgBrush();
            toggleBtn.Resources["ToggleButtonBorderBrush"]                       = Trans();
            toggleBtn.Resources["ToggleButtonBorderBrushPointerOver"]            = Trans();
            toggleBtn.Resources["ToggleButtonBorderBrushPressed"]                = Trans();
            // Checked state — slightly brighter so it reads as "on"
            SolidColorBrush CheckedBg()     => new(Windows.UI.Color.FromArgb(120, 255, 255, 255));
            SolidColorBrush CheckedBgHov()  => new(Windows.UI.Color.FromArgb(160, 255, 255, 255));
            SolidColorBrush CheckedFg()     => new(Windows.UI.Color.FromArgb(255,   0,   0,   0));
            toggleBtn.Resources["ToggleButtonBackgroundChecked"]                 = CheckedBg();
            toggleBtn.Resources["ToggleButtonBackgroundCheckedPointerOver"]      = CheckedBgHov();
            toggleBtn.Resources["ToggleButtonBackgroundCheckedPressed"]          = CheckedBg();
            toggleBtn.Resources["ToggleButtonForegroundChecked"]                 = CheckedFg();
            toggleBtn.Resources["ToggleButtonForegroundCheckedPointerOver"]      = CheckedFg();
            toggleBtn.Resources["ToggleButtonForegroundCheckedPressed"]          = CheckedFg();
            toggleBtn.Resources["ToggleButtonBorderBrushChecked"]                = Trans();
            toggleBtn.Resources["ToggleButtonBorderBrushCheckedPointerOver"]     = Trans();
            toggleBtn.Resources["ToggleButtonBorderBrushCheckedPressed"]         = Trans();
            toggleBtn.Checked   += (s, e) =>
            {
                _inputForwardingEnabled = true;
                if (_contentSurface != null)
                    _contentSurface.IsHitTestVisible = true;
                AppLogger.Log($"  LensWindow {_capture.Id}: input forwarding ON");
            };
            toggleBtn.Unchecked += (s, e) =>
            {
                _inputForwardingEnabled = false;
                if (_contentSurface != null)
                    _contentSurface.IsHitTestVisible = false;
                AppLogger.Log($"  LensWindow {_capture.Id}: input forwarding OFF");
            };

            // ── Live-capture toggle button ────────────────────────────────────────
            var liveBtnIconUri = new Uri(
                System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "assets",
                    "ic_fluent_play_circle_24_filled.png"));
            var liveToggleBtn = new ToggleButton
            {
                Content = new BitmapIcon
                {
                    UriSource        = liveBtnIconUri,
                    ShowAsMonochrome = true,
                    Width            = 16,
                    Height           = 16
                },
                IsChecked                  = true,   // live by default
                Width                      = CloseButtonLogicalW,
                Padding                    = new Thickness(0),
                HorizontalAlignment        = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                VerticalAlignment          = VerticalAlignment.Stretch
            };
            ToolTipService.SetToolTip(liveToggleBtn, "Toggle live capture");
            liveToggleBtn.Resources["ToggleButtonBackground"]                        = Trans();
            liveToggleBtn.Resources["ToggleButtonBackgroundPointerOver"]             = Hover();
            liveToggleBtn.Resources["ToggleButtonBackgroundPressed"]                 = Press();
            liveToggleBtn.Resources["ToggleButtonBackgroundDisabled"]                = Trans();
            liveToggleBtn.Resources["ToggleButtonForeground"]                        = FgBrush();
            liveToggleBtn.Resources["ToggleButtonForegroundPointerOver"]             = FgBrush();
            liveToggleBtn.Resources["ToggleButtonForegroundPressed"]                 = FgBrush();
            liveToggleBtn.Resources["ToggleButtonBorderBrush"]                       = Trans();
            liveToggleBtn.Resources["ToggleButtonBorderBrushPointerOver"]            = Trans();
            liveToggleBtn.Resources["ToggleButtonBorderBrushPressed"]                = Trans();
            liveToggleBtn.Resources["ToggleButtonBackgroundChecked"]                 = CheckedBg();
            liveToggleBtn.Resources["ToggleButtonBackgroundCheckedPointerOver"]      = CheckedBgHov();
            liveToggleBtn.Resources["ToggleButtonBackgroundCheckedPressed"]          = CheckedBg();
            liveToggleBtn.Resources["ToggleButtonForegroundChecked"]                 = CheckedFg();
            liveToggleBtn.Resources["ToggleButtonForegroundCheckedPointerOver"]      = CheckedFg();
            liveToggleBtn.Resources["ToggleButtonForegroundCheckedPressed"]          = CheckedFg();
            liveToggleBtn.Resources["ToggleButtonBorderBrushChecked"]                = Trans();
            liveToggleBtn.Resources["ToggleButtonBorderBrushCheckedPointerOver"]     = Trans();
            liveToggleBtn.Resources["ToggleButtonBorderBrushCheckedPressed"]         = Trans();
            liveToggleBtn.Checked   += (s, e) =>
            {
                _captureLive = true;
                AppLogger.Log($"  LensWindow {_capture.Id}: live capture ON");
            };
            liveToggleBtn.Unchecked += (s, e) =>
            {
                _needFrozenFrame = true;  // snapshot the very next arriving frame
                _captureLive = false;
                AppLogger.Log($"  LensWindow {_capture.Id}: live capture OFF (frozen)");
            };

            var topBarContent = new Grid();
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBarContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label,         0);
            Grid.SetColumn(liveToggleBtn, 1);
            Grid.SetColumn(toggleBtn,     2);
            Grid.SetColumn(closeBtn,      3);
            topBarContent.Children.Add(label);
            topBarContent.Children.Add(liveToggleBtn);
            topBarContent.Children.Add(toggleBtn);
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

            // ── Input-forwarding content surface ──────────────────────────────────
            // Transparent hit surface covering the capture area below the top bar.
            // Sits below the resize handles in z-order so edge handles still win.
            // IsHitTestVisible is toggled by the mode button; off by default so the
            // window behaves as a normal overlay until the user enables forwarding.
            // A non-null Background is required — WinUI only hit-tests borders that
            // have an explicitly set background (even if fully transparent).
            _contentSurface = new CursorBorder
            {
                Margin              = new Thickness(0, TopBarLogicalH, 0, 0),
                Background          = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                IsHitTestVisible    = false,
                Cursor              = InputSystemCursorShape.Cross
            };
            _contentSurface.PointerPressed      += OnContentPointerPressed;
            _contentSurface.PointerReleased     += OnContentPointerReleased;
            _contentSurface.PointerMoved        += OnContentPointerMoved;
            _contentSurface.PointerWheelChanged += OnContentPointerWheelChanged;

            var chrome = new Grid { Visibility = Visibility.Visible };
            chrome.Children.Add(tintOverlay);
            chrome.Children.Add(edgeBorder);
            chrome.Children.Add(_contentSurface); // below top bar + resize handles in z-order
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
            // Both toggle button and close button occupy the rightmost two button widths
            if (pt.X >= logicalW - CloseButtonLogicalW * 2) return;

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

        private async void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            if (_closed || _panel == null) return;

            var scale = (float)(_panel.XamlRoot?.RasterizationScale ?? 1.0);
            var physW = Math.Max(1f, (float)_panel.ActualWidth  * scale);
            var physH = Math.Max(1f, (float)_panel.ActualHeight * scale);

            AppLogger.Log($"  OnPanelLoaded id={_capture.Id}: panel={_panel.ActualWidth}x{_panel.ActualHeight} dips, scale={scale}, swapChain={physW}x{physH} phys — {_openStopwatch.ElapsedMilliseconds} ms since ctor");

            // CanvasSwapChain is created on a background thread so the UI message pump
            // stays live while we hold DeviceCreationLock's write side.  If we blocked the
            // UI thread here, Present() on WGC threads needs a DWM/compositor ACK via the
            // UI STA — but the STA isn't pumping — causing a deadlock → freeze → crash.
            //
            // The write lock excludes concurrent CreateFromDirect3D11Surface calls on WGC
            // threads (the specific operation that AVs when racing with new CanvasSwapChain).
            // _panel.SwapChain assignment runs back on the UI thread (XAML requirement).
            CanvasSwapChain? swapChain = null;
            AppLogger.Log($"  LensWindow {_capture.Id}: queuing Task.Run on ui-thread={Environment.CurrentManagedThreadId}");
            await System.Threading.Tasks.Task.Run(() =>
            {
                AppLogger.Log($"  LensWindow {_capture.Id}: Task.Run started on thread={Environment.CurrentManagedThreadId}");

                // Acquire DeviceCreationLock exclusively so no WGC thread is inside
                // CreateFromDirect3D11Surface while we allocate the swap chain.
                // Both operations share the same ID2D1DeviceContext and are not thread-safe
                // when called concurrently on the same CanvasDevice.
                //
                // Use Monitor.TryEnter with a timeout: if a WGC thread is stuck inside
                // CreateFromDirect3D11Surface (diagnosed in session logs), we bail cleanly
                // rather than hanging. The window stays black, but the app keeps running.
                const int LockTimeoutMs = 5_000;
                var lockSw = Stopwatch.StartNew();
                bool lockAcquired = System.Threading.Monitor.TryEnter(
                    MonitorCapture.DeviceCreationLock, LockTimeoutMs);
                lockSw.Stop();

                if (!lockAcquired)
                {
                    AppLogger.Log($"  LensWindow {_capture.Id}: *** DeviceCreationLock TIMED OUT after {lockSw.ElapsedMilliseconds}ms — WGC thread may be stuck ***");
                    return; // swapChain stays null — window stays black, no hang
                }

                if (lockSw.ElapsedMilliseconds > 2)
                    AppLogger.Log($"  LensWindow {_capture.Id}: device lock waited {lockSw.ElapsedMilliseconds}ms");

                try
                {
                    AppLogger.Log($"  LensWindow {_capture.Id}: calling new CanvasSwapChain {physW}x{physH}");
                    swapChain = new CanvasSwapChain(_device, physW, physH, 96f);
                    AppLogger.Log($"  LensWindow {_capture.Id}: CanvasSwapChain created");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"  LensWindow {_capture.Id}: OnPanelLoaded swap chain EXCEPTION hr=0x{ex.HResult:X8}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    System.Threading.Monitor.Exit(MonitorCapture.DeviceCreationLock);
                }
            });

            // Resumed on UI thread (WinUI 3 DispatcherQueue SynchronizationContext).
            AppLogger.Log($"  LensWindow {_capture.Id}: Task.Run done — swapChain={swapChain != null}, closed={_closed}, thread={Environment.CurrentManagedThreadId}");
            if (swapChain == null || _closed) { swapChain?.Dispose(); return; }

            AppLogger.Log($"  LensWindow {_capture.Id}: assigning panel.SwapChain");
            _panel.SwapChain = swapChain;
            AppLogger.Log($"  LensWindow {_capture.Id}: panel.SwapChain assigned");

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
            bool live        = _captureLive;
            bool needCapture = _needFrozenFrame;

            // ── Frozen + stable: only re-render if a resize is waiting ────────────
            // The WGC thread keeps delivering frames even when frozen; we use those
            // calls purely as a trigger to apply pending ResizeBuffers + redraw the
            // snapshot at the new size.  No GPU work is done if nothing changed.
            if (!live && !needCapture)
            {
                CanvasSwapChain? sc;
                float pw = 0, ph = 0;
                lock (_swapChainLock)
                {
                    if (_swapChain == null || _closed || _pendingSwapW == 0) return;
                    sc = _swapChain;
                    pw = _pendingSwapW; ph = _pendingSwapH;
                    _pendingSwapW = 0;  _pendingSwapH = 0;
                }
                if (_frozenFrame == null) return;

                try
                {
                    lock (MonitorCapture.DeviceCreationLock)
                    {
                        if (Math.Abs(sc.Size.Width - pw) > 0.5f || Math.Abs(sc.Size.Height - ph) > 0.5f)
                            sc.ResizeBuffers(pw, ph, 96f);
                        var sz = sc.Size;
                        using var ds = sc.CreateDrawingSession(Colors.Black);
                        ds.DrawImage(_frozenFrame,
                            new Rect(0, 0, sz.Width, sz.Height),
                            new Rect(0, 0, _frozenFrame.Size.Width, _frozenFrame.Size.Height));
                    }
                    sc.Present(0);
                }
                catch (Exception ex) when (IsDeviceLostHResult(ex.HResult)) { }
                catch (Exception ex)
                {
                    AppLogger.Log($"  LensWindow {_capture.Id}: OnFrameArrived (frozen resize) EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
                return;
            }

            // ── Live or first-freeze snapshot ─────────────────────────────────────
            var drawStart = Stopwatch.GetTimestamp();

            try
            {
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

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AppLogger.Log($"  First frame id={_capture.Id}: {_openStopwatch.ElapsedMilliseconds} ms since ctor, thread={Environment.CurrentManagedThreadId}");
                }

                // All D2D device operations — ResizeBuffers, CreateDrawingSession (BeginDraw),
                // DrawImage, EndDraw — must be serialized under DeviceCreationLock.
                // Present() is excluded: compositor-facing, must not hold the lock.
                lock (MonitorCapture.DeviceCreationLock)
                {
                    if (pendingW > 0 &&
                        (Math.Abs(swapChain.Size.Width  - pendingW) > 0.5f ||
                         Math.Abs(swapChain.Size.Height - pendingH) > 0.5f))
                    {
                        AppLogger.Log($"  LensWindow {_capture.Id}: ResizeBuffers {pendingW}x{pendingH}");
                        swapChain.ResizeBuffers(pendingW, pendingH, 96f);
                    }

                    // Snapshot this frame when freeze was just requested.
                    // GPU blit into a CanvasRenderTarget we own — no CPU readback.
                    if (needCapture)
                    {
                        _frozenFrame?.Dispose();
                        var cr = _capture.CropRect;
                        _frozenFrame = new CanvasRenderTarget(_device, (float)cr.Width, (float)cr.Height, 96f);
                        using var fds = _frozenFrame.CreateDrawingSession();
                        fds.DrawImage(fullMonitorBitmap,
                            new Rect(0, 0, cr.Width, cr.Height),
                            cr);
                        _needFrozenFrame = false;
                        // re-read: if the user toggled back to live before this frame, stay live
                        live = _captureLive;
                    }

                    var size = swapChain.Size;
                    using var ds = swapChain.CreateDrawingSession(Colors.Black);
                    if (live)
                    {
                        ds.DrawImage(fullMonitorBitmap,
                            new Rect(0, 0, size.Width, size.Height),
                            _capture.CropRect);
                    }
                    else
                    {
                        // Draw the just-captured snapshot scaled to the current swap chain
                        ds.DrawImage(_frozenFrame!,
                            new Rect(0, 0, size.Width, size.Height),
                            new Rect(0, 0, _frozenFrame!.Size.Width, _frozenFrame.Size.Height));
                    }
                }

                swapChain.Present(0);
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

            // Stop input forwarding immediately so no queued pointer events are
            // processed after the window has begun tearing down.
            _inputForwardingEnabled = false;
            if (_contentSurface != null)
                _contentSurface.IsHitTestVisible = false;

            _capture.FrameArrived -= OnFrameArrived;

            lock (_swapChainLock)
            {
                _swapChain?.Dispose();
                _swapChain = null;
            }

            _frozenFrame?.Dispose();
            _frozenFrame = null;

            _capture.Dispose();
            LensClosed?.Invoke(_capture.Id);
            AppLogger.Log($"  Lens {_capture.Id} closed");
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Input forwarding — coordinate remapping + SendInput injection
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a pointer position inside this LensWindow to the corresponding
        /// global screen coordinate in the original captured region.
        /// </summary>
        /// <param name="logicalPos">
        /// Position in logical (DIP) coordinates relative to the LensWindow root,
        /// as returned by <c>PointerRoutedEventArgs.GetCurrentPoint(null).Position</c>
        /// minus the window's own screen origin — or more simply, the position relative
        /// to any full-window element passed to <c>GetCurrentPoint</c>.
        /// </param>
        /// <returns>Global physical screen coordinates of the equivalent point in the source.</returns>
        private PointInt32 RemapToSource(Windows.Foundation.Point logicalPos)
        {
            // 1. Logical → physical pixels within the LensWindow.
            //    _dpiScale = GetDpiForWindow / 96 = XamlRoot.RasterizationScale.
            float physX = (float)logicalPos.X * _dpiScale;
            float physY = (float)logicalPos.Y * _dpiScale;

            // 2. Normalise to [0..1] across the window's current physical size.
            //    AppWindow.Size is always in physical pixels, so this handles the case
            //    where the user has resized the LensWindow (zoom in / zoom out).
            float normX = physX / AppWindow.Size.Width;
            float normY = physY / AppWindow.Size.Height;

            // 3. Map into the captured crop rect, then add the capture's global origin.
            //    _capture.Region.X/Y  = global screen origin of the captured region (physical px).
            //    _capture.CropRect.Width/Height = physical size of that region on its monitor.
            //
            //    The monitor offset cancels:
            //      globalX = (Region.X - CropRect.X) + CropRect.X + normX * CropRect.Width
            //              =  Region.X               + normX * CropRect.Width
            int globalX = (int)(_capture.Region.X + normX * _capture.CropRect.Width);
            int globalY = (int)(_capture.Region.Y + normY * _capture.CropRect.Height);

            return new PointInt32(globalX, globalY);
        }

        /// <summary>
        /// Synthesises a mouse event at the given global screen position using SendInput.
        /// Moves the real cursor to <paramref name="globalX"/>, <paramref name="globalY"/>
        /// and then fires <paramref name="buttonFlags"/> (e.g. MOUSEEVENTF_LEFTDOWN).
        /// Pass <see cref="MOUSE_EVENT_FLAGS"/> with no button bits to perform a move only.
        /// </summary>
        /// <remarks>
        /// MOUSEEVENTF_ABSOLUTE coordinates are normalised to [0..65535] across the full
        /// virtual desktop (MOUSEEVENTF_VIRTUALDESK), so negative and large coordinates on
        /// secondary monitors are handled correctly.
        ///
        /// SendInput cannot inject into windows at a higher privilege level (UIPI); clicks
        /// targeting elevated processes (e.g. Task Manager) will be silently dropped by the OS.
        /// </remarks>
        private static unsafe void InjectMouseEvent(
            int globalX, int globalY,
            MOUSE_EVENT_FLAGS buttonFlags,
            int mouseData = 0)
        {
            // Map global physical coords to the [0..65535] space that MOUSEEVENTF_ABSOLUTE
            // uses.  MOUSEEVENTF_VIRTUALDESK extends the space from the primary monitor to
            // the full virtual desktop — essential for multi-monitor and negative coordinates.
            int vLeft   = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            int vTop    = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            int vWidth  = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
            int vHeight = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);

            int absX = (int)((globalX - vLeft) * 65535.0 / (vWidth  - 1));
            int absY = (int)((globalY - vTop)  * 65535.0 / (vHeight - 1));

            // Always send MOVE and button as two separate events in one atomic SendInput
            // call.  Combining them into a single INPUT struct causes the OS to route the
            // button to the window that had focus *before* the move, not the one now under
            // the cursor — resulting in the cursor visibly jumping but the click being lost.
            // Sending MOVE first and button second guarantees the window under the new
            // cursor position receives the button message.
            INPUT* events = stackalloc INPUT[2];

            events[0] = new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = new INPUT._Anonymous_e__Union
                {
                    mi = new MOUSEINPUT
                    {
                        dx          = absX,
                        dy          = absY,
                        mouseData   = 0,
                        dwFlags     = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE
                                    | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE
                                    | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                        time        = 0,
                        dwExtraInfo = UIntPtr.Zero,
                    }
                }
            };

            events[1] = new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = new INPUT._Anonymous_e__Union
                {
                    mi = new MOUSEINPUT
                    {
                        dx          = absX,
                        dy          = absY,
                        mouseData   = (uint)mouseData,
                        // No MOUSEEVENTF_MOVE — dx/dy are ignored without it, cursor
                        // stays where event[0] placed it. ABSOLUTE+VIRTUALDESK are still
                        // required for the button message to be routed correctly on some
                        // driver stacks.
                        dwFlags     = MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE
                                    | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK
                                    | buttonFlags,
                        time        = 0,
                        dwExtraInfo = UIntPtr.Zero,
                    }
                }
            };

            // nInputs=1 for a move-only call (buttonFlags==0), 2 when a button is involved.
            uint count = buttonFlags == 0 ? 1u : 2u;
            PInvoke.SendInput(count, events, sizeof(INPUT));
        }

        private void OnContentPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_inputForwardingEnabled || _closed || _isDragging || _isResizing) return;

            var point         = e.GetCurrentPoint(null);
            var kind          = point.Properties.PointerUpdateKind;
            var (msg, wParam) = GetDownMessage(kind);
            if (msg == 0) return; // unrecognised button — ignore

            var src = RemapToSource(point.Position);
            AppLogger.Log($"  LensWindow {_capture.Id}: forward {kind} → ({src.X},{src.Y})");
            PostMouseButton(src.X, src.Y, msg, wParam);

            // Capture pointer so PointerReleased fires even if the cursor leaves
            // the window while a button is held, guaranteeing a matching button-up.
            ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnContentPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_inputForwardingEnabled || _closed || _isDragging || _isResizing) return;

            var point         = e.GetCurrentPoint(null);
            var kind          = point.Properties.PointerUpdateKind;
            var (msg, wParam) = GetUpMessage(kind);
            if (msg == 0) return;

            var src = RemapToSource(point.Position);
            AppLogger.Log($"  LensWindow {_capture.Id}: forward {kind} → ({src.X},{src.Y})");
            PostMouseButton(src.X, src.Y, msg, wParam);

            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void OnContentPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_inputForwardingEnabled || _closed || _isDragging || _isResizing) return;

            var point = e.GetCurrentPoint(null);
            var props = point.Properties;
            var src   = RemapToSource(point.Position);

            // Build virtual-key flags from the current button and modifier state.
            // When no buttons are held this is 0 → pure hover (tooltips, highlights).
            // When a button is held this carries MK_LBUTTON etc. → drag forwarding.
            ushort fwKeys = 0;
            if (props.IsLeftButtonPressed)   fwKeys |= 0x0001; // MK_LBUTTON
            if (props.IsRightButtonPressed)  fwKeys |= 0x0002; // MK_RBUTTON
            if (props.IsMiddleButtonPressed) fwKeys |= 0x0010; // MK_MBUTTON
            if (props.IsXButton1Pressed)     fwKeys |= 0x0020; // MK_XBUTTON1
            if (props.IsXButton2Pressed)     fwKeys |= 0x0040; // MK_XBUTTON2

            var mods = e.KeyModifiers;
            if (mods.HasFlag(Windows.System.VirtualKeyModifiers.Control)) fwKeys |= 0x0008; // MK_CONTROL
            if (mods.HasFlag(Windows.System.VirtualKeyModifiers.Shift))   fwKeys |= 0x0004; // MK_SHIFT

            // Only forward WM_MOUSEMOVE during drag (at least one button held).
            // Pure hover (fwKeys == 0) causes rapid WM_MOUSELEAVE/WM_MOUSEMOVE flashing:
            // the OS immediately sends WM_MOUSELEAVE to the target because the real cursor
            // is not physically over it, which clears the hover state on every frame.
            if (fwKeys == 0) return;

            // WM_MOUSEMOVE = 0x0200. lParam = client coords, same layout as button msgs.
            PostMouseButton(src.X, src.Y, 0x0200u, (nuint)fwKeys);
        }

        // Maps PointerUpdateKind → (WM_* message, wParam) for PostMessage.
        // Using PostMessage rather than SendInput keeps the real cursor inside the
        // LensWindow — the message is delivered directly to the target HWND without
        // moving the physical cursor.
        private static (uint msg, nuint wParam) GetDownMessage(PointerUpdateKind kind) =>
            kind switch
            {
                PointerUpdateKind.LeftButtonPressed   => (0x0201u, 0x0001u),           // WM_LBUTTONDOWN, MK_LBUTTON
                PointerUpdateKind.RightButtonPressed  => (0x0204u, 0x0002u),           // WM_RBUTTONDOWN, MK_RBUTTON
                PointerUpdateKind.MiddleButtonPressed => (0x0207u, 0x0010u),           // WM_MBUTTONDOWN, MK_MBUTTON
                PointerUpdateKind.XButton1Pressed     => (0x020Bu, (nuint)(1u << 16)), // WM_XBUTTONDOWN, XBUTTON1 in hi-word
                PointerUpdateKind.XButton2Pressed     => (0x020Bu, (nuint)(2u << 16)), // WM_XBUTTONDOWN, XBUTTON2 in hi-word
                _                                     => (0u, 0u),
            };

        private static (uint msg, nuint wParam) GetUpMessage(PointerUpdateKind kind) =>
            kind switch
            {
                PointerUpdateKind.LeftButtonReleased   => (0x0202u, 0u),               // WM_LBUTTONUP
                PointerUpdateKind.RightButtonReleased  => (0x0205u, 0u),               // WM_RBUTTONUP
                PointerUpdateKind.MiddleButtonReleased => (0x0208u, 0u),               // WM_MBUTTONUP
                PointerUpdateKind.XButton1Released     => (0x020Cu, (nuint)(1u << 16)),// WM_XBUTTONUP, XBUTTON1
                PointerUpdateKind.XButton2Released     => (0x020Cu, (nuint)(2u << 16)),// WM_XBUTTONUP, XBUTTON2
                _                                      => (0u, 0u),
            };

        /// <summary>
        /// Delivers a mouse button message directly to the window at the given global
        /// screen position via PostMessage, without moving the real cursor.
        /// WindowFromPoint returns the deepest child window at the point, so button
        /// messages reach the correct control (e.g. a button inside a dialog) rather
        /// than just the top-level window.
        /// </summary>
        /// <remarks>
        /// Like SendInput, PostMessage is subject to UIPI: messages targeting a window
        /// at a higher integrity level (e.g. Task Manager) are silently dropped.
        /// </remarks>
        private static void PostMouseButton(int globalX, int globalY, uint msg, nuint wParam)
        {
            // CsWin32 0.3.x maps Win32 POINT to System.Drawing.Point.
            var pt     = new System.Drawing.Point(globalX, globalY);
            var target = PInvoke.WindowFromPoint(pt);
            if (target == default) return;

            // Convert global screen coords → client coords of the target window.
            PInvoke.ScreenToClient(target, ref pt);

            // MAKELPARAM: low word = X, high word = Y (matches Win32 macro exactly).
            nint lParamVal = (nint)(uint)((ushort)pt.X | ((uint)(ushort)pt.Y << 16));

            PInvoke.PostMessage(target, msg, new WPARAM(wParam), new LPARAM(lParamVal));
        }

        private void OnContentPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!_inputForwardingEnabled || _closed || _isDragging || _isResizing) return;

            var point = e.GetCurrentPoint(null);
            var props = point.Properties;
            var src   = RemapToSource(point.Position);

            // Pass modifier key state so Ctrl+scroll (zoom) works correctly in apps
            // that inspect the low word of wParam (fwKeys).
            var mods = e.KeyModifiers;
            ushort fwKeys = 0;
            if (mods.HasFlag(Windows.System.VirtualKeyModifiers.Control)) fwKeys |= 0x0008; // MK_CONTROL
            if (mods.HasFlag(Windows.System.VirtualKeyModifiers.Shift))   fwKeys |= 0x0004; // MK_SHIFT

            // WM_MOUSEWHEEL  = 0x020A  (vertical)
            // WM_MOUSEHWHEEL = 0x020E  (horizontal, e.g. tilting the scroll wheel)
            uint msg   = props.IsHorizontalMouseWheel ? 0x020Eu : 0x020Au;
            int  delta = props.MouseWheelDelta;

            // wParam: high word = signed zDelta, low word = fwKeys.
            // Casting delta to ushort preserves the two's-complement bit pattern so
            // the receiver reads the correct signed value via GET_WHEEL_DELTA_WPARAM.
            nuint wParam = (nuint)(uint)((uint)fwKeys | ((uint)(ushort)delta << 16));

            AppLogger.Log($"  LensWindow {_capture.Id}: forward wheel delta={delta} → ({src.X},{src.Y})");
            PostMouseWheel(src.X, src.Y, msg, wParam);
            e.Handled = true;
        }

        private static void PostMouseWheel(int globalX, int globalY, uint msg, nuint wParam)
        {
            var pt     = new System.Drawing.Point(globalX, globalY);
            var target = PInvoke.WindowFromPoint(pt);
            if (target == default) return;

            // WM_MOUSEWHEEL / WM_MOUSEHWHEEL lParam carries *screen* coordinates
            // (unlike button messages that use client coords).  No ScreenToClient needed.
            nint lParamVal = (nint)(uint)((ushort)globalX | ((uint)(ushort)globalY << 16));

            PInvoke.PostMessage(target, msg, new WPARAM(wParam), new LPARAM(lParamVal));
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
