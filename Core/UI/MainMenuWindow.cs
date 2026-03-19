using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using UIXtend.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using UIXtend.Core.Interfaces;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace UIXtend.Core.UI
{
    public class MainMenuWindow : Window
    {
        private readonly StackPanel _buttonPanel;
        private readonly Button _selectRegionBtn;
        private readonly Button _closeConfigBtn;
        private readonly DispatcherQueue _dispatcher;
        private ILensService? _lensService;

        public MainMenuWindow()
        {
            SystemBackdrop = new MicaBackdrop() { Kind = MicaKind.BaseAlt };
            Title = "UIXtend Configuration";

            // Capture the UI thread dispatcher at construction time for later marshaling
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _selectRegionBtn = new Button
            {
                Content = "Select Region",
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 200,
                MinHeight = 40
            };
            _selectRegionBtn.Click += OnSelectRegionClicked;

            _closeConfigBtn = new Button
            {
                Content              = "Close configuration",
                HorizontalAlignment  = HorizontalAlignment.Center,
                MinWidth             = 200,
                MinHeight            = 40
            };
            _closeConfigBtn.Click += (_, _) => AppWindow.Hide();

            _buttonPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Margin = new Thickness(24, 20, 24, 20)
            };
            _buttonPanel.Children.Add(_selectRegionBtn);

            var root = new Grid();
            root.Children.Add(_buttonPanel);
            root.Loaded += (s, e) => { SizeToContent(); CenterOnPrimaryDisplay(); };
            Content = root;

            // Hook up LensService after DI container is fully built.
            // ServiceHost.ServiceProvider is available at this point because Build()
            // completes before BootstrapperHostedService.StartAsync() is called.
            _lensService = ServiceHost.ServiceProvider?.GetService<ILensService>();
            if (_lensService != null)
            {
                _lensService.LensesChanged += OnLensesChanged;
                RebuildLensButtons();
            }

            // Show overlays while the menu is visible; hide them when it's hidden/closed.
            // We use AppWindow.Changed rather than Closed because WindowService intercepts
            // the Closing event, cancels it, and calls AppWindow.Hide() — Closed never fires.
            AppWindow.Changed += (sender, args) =>
            {
                if (args.DidVisibilityChange)
                    _lensService?.SetOverlayVisible(AppWindow.IsVisible);
            };

            // Pre-size and pre-center before Activate() so the window never appears at the
            // default WinUI large size. SizeToContent()+CenterOnPrimaryDisplay() in root.Loaded
            // will correct the exact pixel measurements once XamlRoot is available.
            PreSizeAndCenter();
        }

        private async void OnSelectRegionClicked(object sender, RoutedEventArgs e)
        {
            AppLogger.Log("SelectRegion: starting");
            AppWindow.Hide();

            var selectionService = ServiceHost.ServiceProvider
                ?.GetService<IRegionSelectionService>();

            if (selectionService == null)
            {
                AppLogger.Log("SelectRegion: IRegionSelectionService not found");
                AppWindow.Show();
                return;
            }

            var rect = await selectionService.StartSelectionAsync();

            // Main menu comes back regardless of whether the user selected or cancelled
            AppWindow.Show();
            Activate();

            if (rect == null)
            {
                AppLogger.Log("SelectRegion: cancelled");
                return;
            }

            AppLogger.Log($"SelectRegion: completed rect=({rect.Value.X},{rect.Value.Y} {rect.Value.Width}x{rect.Value.Height})");

            if (_lensService != null)
            {
                try
                {
                    _lensService.OpenLens(rect.Value);
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("OnSelectRegionClicked", ex);
                    var dialog = new ContentDialog
                    {
                        Title = "Capture Error",
                        Content = ex.Message,
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };
                    _ = dialog.ShowAsync();
                }
            }
        }

        // ── Dynamic lens close buttons ─────────────────────────────────────────────

        private void OnLensesChanged(object? sender, System.EventArgs e)
        {
            // LensesChanged can fire from any thread — marshal to the UI thread
            _dispatcher.TryEnqueue(RebuildLensButtons);
        }

        private void RebuildLensButtons()
        {
            // Always keep _selectRegionBtn at index 0; replace everything after it
            while (_buttonPanel.Children.Count > 1)
                _buttonPanel.Children.RemoveAt(_buttonPanel.Children.Count - 1);

            if (_lensService == null) return;

            foreach (var lens in _lensService.ActiveLenses)
            {
                var capturedId = lens.Id; // local copy for lambda capture
                var paletteColor = LensColorPalette.ForIndex(capturedId - 1);
                var fgColor = LensColorPalette.GetForeground(paletteColor);
                var hoverColor = LensColorPalette.Darken(paletteColor, 0.82);
                var pressedColor = LensColorPalette.Darken(paletteColor, 0.65);

                SolidColorBrush Brush(Windows.UI.Color c) => new(c);

                var btn = new Button
                {
                    Content = $"Close Capture {capturedId}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MinWidth = 200,
                    MinHeight = 40
                };

                // Override button colour resources
                btn.Resources["ButtonBackground"]              = Brush(paletteColor);
                btn.Resources["ButtonBackgroundPointerOver"]   = Brush(hoverColor);
                btn.Resources["ButtonBackgroundPressed"]        = Brush(pressedColor);
                btn.Resources["ButtonBackgroundDisabled"]       = Brush(paletteColor);
                btn.Resources["ButtonForeground"]              = Brush(fgColor);
                btn.Resources["ButtonForegroundPointerOver"]   = Brush(fgColor);
                btn.Resources["ButtonForegroundPressed"]        = Brush(fgColor);
                btn.Resources["ButtonForegroundDisabled"]       = Brush(fgColor);
                btn.Resources["ButtonBorderBrush"]             = Brush(pressedColor);
                btn.Resources["ButtonBorderBrushPointerOver"]  = Brush(pressedColor);
                btn.Resources["ButtonBorderBrushPressed"]       = Brush(pressedColor);

                btn.Click += (s, e) => _lensService?.CloseLens(capturedId);
                _buttonPanel.Children.Add(btn);
            }

            _buttonPanel.Children.Add(_closeConfigBtn);
            SizeToContent();
        }

        private void PreSizeAndCenter()
        {
            // GetDpiForWindow works on the HWND even before the window is shown.
            var hwndPtr = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi    = GetDpiForWindow(hwndPtr);
            float scale = dpi / 96f;

            // Estimate the compact window size in logical DIPs:
            //   1 button:     MinWidth=200, template adds ~8px height → ~200×48 desired
            //   panel margin: 24+24 H, 20+20 V
            //   total logical: 248W × 88H + title bar
            int preW = (int)Math.Ceiling(248 * scale);
            int preH = (int)Math.Ceiling(88  * scale) + AppWindow.TitleBar.Height;

            var wa = DisplayArea.Primary.WorkArea;
            AppWindow.MoveAndResize(new RectInt32(
                wa.X + (wa.Width  - preW) / 2,
                wa.Y + (wa.Height - preH) / 2,
                preW, preH));
        }

        private void CenterOnPrimaryDisplay()
        {
            var workArea = DisplayArea.Primary.WorkArea; // physical pixels, excludes taskbar
            var size     = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width  - size.Width)  / 2,
                workArea.Y + (workArea.Height - size.Height) / 2));
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        private void SizeToContent()
        {
            if (Content?.XamlRoot == null) return;

            // Measure the panel unconstrained so WinUI reports the true content size,
            // accounting for the button control template's internal padding and font metrics
            // rather than relying on our MinWidth/MinHeight guesses.
            _buttonPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = _buttonPanel.DesiredSize; // logical px, excludes Margin
            var margin  = _buttonPanel.Margin;

            var logicalW = desired.Width  + margin.Left + margin.Right;
            var logicalH = desired.Height + margin.Top  + margin.Bottom;

            // AppWindow coordinates are physical pixels; scale logical values accordingly.
            var scale = Content.XamlRoot.RasterizationScale;
            var physW = (int)Math.Ceiling(logicalW * scale);
            var physH = (int)Math.Ceiling(logicalH * scale) + AppWindow.TitleBar.Height;

            AppWindow.Resize(new SizeInt32(physW, physH));
        }
    }
}
