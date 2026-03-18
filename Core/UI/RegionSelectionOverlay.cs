using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UIXtend.Core.UI
{
    internal class CrosshairGrid : Grid
    {
        public CrosshairGrid()
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross);
        }
    }

    /// <summary>
    /// Assigns an empty backdrop so WinUI 3's compositor skips painting its default
    /// opaque background, letting DWM show through for true transparency.
    /// </summary>
    internal class ClearBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
    }

    internal class RegionSelectionOverlay : Window
    {
        private readonly RECT _virtualDesktopBounds;
        private readonly Canvas _canvas;
        private readonly Path _dimmingPath;
        private readonly RectangleGeometry _holeGeometry;
        private readonly Rectangle _selectionRect;
        private bool _isDragging = false;
        private Windows.Foundation.Point _startPoint;

        public event Action<Windows.Foundation.Rect>? OnRegionSelected;
        public event Action? OnSelectionCancelled;

        public RegionSelectionOverlay(RECT virtualDesktopBounds)
        {
            _virtualDesktopBounds = virtualDesktopBounds;
            var hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

            var rootGrid = new CrosshairGrid();
            _canvas = new Canvas { Opacity = 0.0 }; // Start invisible for fade-in

            // GeometryGroup with EvenOdd fill punches a transparent "hole" through the dim layer
            var baseRect = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, virtualDesktopBounds.Width, virtualDesktopBounds.Height)
            };
            _holeGeometry = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(baseRect);
            group.Children.Add(_holeGeometry);

            _dimmingPath = new Path
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(51, 0, 0, 0)), // ~20% black
                Data = group
            };

            _selectionRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                Visibility = Visibility.Collapsed
            };

            _canvas.Children.Add(_dimmingPath);
            _canvas.Children.Add(_selectionRect);
            rootGrid.Children.Add(_canvas);

            rootGrid.PointerPressed += OnPointerPressed;
            rootGrid.PointerMoved += OnPointerMoved;
            rootGrid.PointerReleased += OnPointerReleased;

            this.Content = rootGrid;

            // Must be set on the Window instance before Win32 style changes
            this.SystemBackdrop = new ClearBackdrop();
            InitializeTransparentWindow(hwnd, virtualDesktopBounds);
            BeginFadeIn();
        }

        public void FadeOutAndClose(Action onCompleted)
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = 0.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.5))
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, _canvas);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Completed += (s, e) =>
            {
                onCompleted?.Invoke(); // resolve the TCS before Close() fires the Closed event
                this.Close();
            };
            sb.Begin();
        }

        private static unsafe void InitializeTransparentWindow(HWND hwnd, RECT bounds)
        {
            var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)
                Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd)).Presenter;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            var style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE,
                style & ~(int)WINDOW_STYLE.WS_OVERLAPPEDWINDOW | unchecked((int)WINDOW_STYLE.WS_POPUP));

            var exStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                exStyle
                | (int)WINDOW_EX_STYLE.WS_EX_LAYERED
                | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | unchecked((int)WINDOW_EX_STYLE.WS_EX_TOPMOST));

            var margins = default(Windows.Win32.UI.Controls.MARGINS);
            margins.cxLeftWidth = -1; margins.cxRightWidth = -1;
            margins.cyTopHeight = -1; margins.cyBottomHeight = -1;
            PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);

            // Position and size to the full virtual desktop (origin may be negative on multi-monitor)
            PInvoke.SetWindowPos(hwnd, (HWND)(IntPtr)(-1),
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }

        private void BeginFadeIn()
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.5))
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, _canvas);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Right-click anywhere cancels immediately, even mid-drag
            var pointerPoint = e.GetCurrentPoint(_canvas);
            if (pointerPoint.Properties.IsRightButtonPressed)
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    ((UIElement)sender).ReleasePointerCapture(e.Pointer);
                }
                OnSelectionCancelled?.Invoke();
                return;
            }

            _isDragging = true;
            _startPoint = pointerPoint.Position;
            _holeGeometry.Rect = new Windows.Foundation.Rect(_startPoint, _startPoint);

            Canvas.SetLeft(_selectionRect, _startPoint.X);
            Canvas.SetTop(_selectionRect, _startPoint.Y);
            _selectionRect.Width = 0;
            _selectionRect.Height = 0;
            _selectionRect.Visibility = Visibility.Visible;

            ((UIElement)sender).CapturePointer(e.Pointer);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;

            // Right button pressed during an active drag — cancel immediately
            if (e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed)
            {
                _isDragging = false;
                ((UIElement)sender).ReleasePointerCapture(e.Pointer);
                OnSelectionCancelled?.Invoke();
                return;
            }

            var current = e.GetCurrentPoint(_canvas).Position;
            var x = Math.Min(_startPoint.X, current.X);
            var y = Math.Min(_startPoint.Y, current.Y);
            var w = Math.Abs(current.X - _startPoint.X);
            var h = Math.Abs(current.Y - _startPoint.Y);

            _holeGeometry.Rect = new Windows.Foundation.Rect(x, y, w, h);
            Canvas.SetLeft(_selectionRect, x);
            Canvas.SetTop(_selectionRect, y);
            _selectionRect.Width = w;
            _selectionRect.Height = h;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);

            // Convert local overlay coords to global virtual screen coordinates
            var local = _holeGeometry.Rect;
            var global = new Windows.Foundation.Rect(
                _virtualDesktopBounds.X + local.X,
                _virtualDesktopBounds.Y + local.Y,
                local.Width,
                local.Height);

            OnRegionSelected?.Invoke(global);
        }
    }
}
