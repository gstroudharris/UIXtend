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

    internal class ClearBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
    {
    }

    internal class RegionSelectionOverlay : Window
    {
        private HWND _hwnd;
        private Canvas _canvas;
        private Path _dimmingPath;
        private RectangleGeometry _holeGeometry;
        private Rectangle _selectionRect;
        private bool _isDragging = false;
        private Windows.Foundation.Point _startPoint;

        public event Action<Windows.Foundation.Rect>? OnRegionSelected;
        public event Action? OnSelectionCancelled;
        public RECT MonitorBounds { get; }

        public RegionSelectionOverlay(RECT monitorBounds)
        {
            MonitorBounds = monitorBounds;
            _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

            var rootGrid = new CrosshairGrid();
            _canvas = new Canvas();
            _canvas.Opacity = 0.0; // Start physically invisible for the fade in
            
            var baseRect = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, monitorBounds.Width, monitorBounds.Height) };
            _holeGeometry = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(baseRect);
            group.Children.Add(_holeGeometry);

            _dimmingPath = new Path
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(51, 0, 0, 0)), // ~20% black opacity
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

            InitializeTransparentWindow(monitorBounds);

            // Execute the 0.5s Fade In animation
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
                this.Close();
                onCompleted?.Invoke();
            };
            sb.Begin();
        }

        private unsafe void InitializeTransparentWindow(RECT bounds)
        {
            // Applying an empty custom SystemBackdrop forces WinUI 3's compositor to skip drawing its default opaque background
            this.SystemBackdrop = new ClearBackdrop();

            // Native WinUI 3 border removal
            var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)this.AppWindow.Presenter;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // Re-apply Win32 transparency flags to the underlying HWND to ensure full pass-through
            var style = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style & ~(int)WINDOW_STYLE.WS_OVERLAPPEDWINDOW | unchecked((int)WINDOW_STYLE.WS_POPUP));

            var exStyle = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | (int)WINDOW_EX_STYLE.WS_EX_LAYERED | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | unchecked((int)WINDOW_EX_STYLE.WS_EX_TOPMOST));

            var margins = default(Windows.Win32.UI.Controls.MARGINS);
            margins.cxLeftWidth = -1; margins.cxRightWidth = -1; margins.cyTopHeight = -1; margins.cyBottomHeight = -1;
            PInvoke.DwmExtendFrameIntoClientArea(_hwnd, in margins);

            // Move and size perfectly to the target monitor bounds
            PInvoke.SetWindowPos(_hwnd, (HWND)(IntPtr)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointerPoint = e.GetCurrentPoint(_canvas);
            if (pointerPoint.Properties.IsRightButtonPressed)
            {
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

            var currentPoint = e.GetCurrentPoint(_canvas).Position;
            
            var x = Math.Min(_startPoint.X, currentPoint.X);
            var y = Math.Min(_startPoint.Y, currentPoint.Y);
            var w = Math.Abs(currentPoint.X - _startPoint.X);
            var h = Math.Abs(currentPoint.Y - _startPoint.Y);

            var selectionRect = new Windows.Foundation.Rect(x, y, w, h);

            _holeGeometry.Rect = selectionRect;
            
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

            // Convert to global virtual screen coordinates
            var localRect = _holeGeometry.Rect;
            var globalRect = new Windows.Foundation.Rect(
                MonitorBounds.X + localRect.X,
                MonitorBounds.Y + localRect.Y,
                localRect.Width,
                localRect.Height
            );

            OnRegionSelected?.Invoke(globalRect);
        }
    }
}
