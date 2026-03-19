using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using UIXtend.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
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
        // ── Brand colours ──────────────────────────────────────────────────────────
        private static readonly Windows.UI.Color s_accent   = Windows.UI.Color.FromArgb(255,   0, 103, 192); // #0067C0
        private static readonly Windows.UI.Color s_accentBg = Windows.UI.Color.FromArgb( 12,   0, 103, 192); // faint tint
        private static readonly Windows.UI.Color s_accentHv = Windows.UI.Color.FromArgb( 25,   0, 103, 192);
        private static readonly Windows.UI.Color s_accentPr = Windows.UI.Color.FromArgb( 45,   0, 103, 192);

        // ── Children ───────────────────────────────────────────────────────────────
        private readonly StackPanel       _buttonPanel;
        private readonly FrameworkElement _header;
        private readonly Button           _selectRegionBtn;
        private readonly Button           _closeConfigBtn;
        private readonly DispatcherQueue  _dispatcher;
        private ILensService?             _lensService;

        // ── Backdrop ───────────────────────────────────────────────────────────────
        private DesktopAcrylicController?  _acrylicController;
        private SystemBackdropConfiguration? _backdropConfig;

        public MainMenuWindow()
        {
            Title = "UIXtend";

            _dispatcher = DispatcherQueue.GetForCurrentThread();

            // Light frosted-glass acrylic — white tint at near-zero opacity so the
            // luminosity layer dominates, giving a bright translucent feel that reads
            // clearly as a transient overlay rather than a persistent shell window.
            _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
            _acrylicController = new DesktopAcrylicController
            {
                TintColor         = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                TintOpacity       = 0.08f,
                LuminosityOpacity = 0.62f,
            };
            _acrylicController.AddSystemBackdropTarget(
                (Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop)(object)this);
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
            // Keep IsInputActive pinned to true so the acrylic appearance never changes
            // when the window loses focus — the menu should look identical whether selected or not.
            this.Closed += (_, _) =>
            {
                _acrylicController?.Dispose();
                _acrylicController = null;
            };

            // Window icon — shown in the taskbar and the alt-tab thumbnail.
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "UIXtend.ico"));

            // Extend the acrylic surface into the title bar so it reads as one continuous surface.
            // Caption buttons are kept but their backgrounds are made transparent so they
            // sit over the Mica material rather than over a separate opaque strip.
            var tb = AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar    = true;
            // Backgrounds — transparent so the acrylic surface shows through
            tb.ButtonBackgroundColor         = Windows.UI.Color.FromArgb(  0, 0, 0, 0);
            tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(  0, 0, 0, 0);
            tb.ButtonHoverBackgroundColor    = Windows.UI.Color.FromArgb( 20, 0, 0, 0);
            tb.ButtonPressedBackgroundColor  = Windows.UI.Color.FromArgb( 40, 0, 0, 0);
            // Foregrounds — pin every state to the same white so the X never changes
            // colour when focus is lost (Windows would otherwise dim it to dark gray)
            tb.ButtonForegroundColor         = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            tb.ButtonHoverForegroundColor    = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            tb.ButtonPressedForegroundColor  = Windows.UI.Color.FromArgb(255, 255, 255, 255);

            // ── Brand header ─────────────────────────────────────────────────────
            _header = BuildHeader();

            // ── Select Region (primary action) ───────────────────────────────────
            _selectRegionBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth            = 200,
                MinHeight           = 40,
                CornerRadius        = new CornerRadius(4),
                BorderThickness     = new Thickness(2),
                Content             = MakeButtonContent("\uE721", "SELECT REGION",
                                          new SolidColorBrush(s_accent)),
            };
            ApplyButtonResources(_selectRegionBtn,
                bg: s_accentBg, bgHov: s_accentHv, bgPrs: s_accentPr,
                border: s_accent);
            _selectRegionBtn.Click += OnSelectRegionClicked;

            // ── Close configuration (primary action — same style as Select Region) ──
            _closeConfigBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth            = 200,
                MinHeight           = 40,
                CornerRadius        = new CornerRadius(4),
                BorderThickness     = new Thickness(2),
                Content             = MakeButtonContent("\uE711", "CLOSE CONFIGURATION",
                                          new SolidColorBrush(s_accent)),
            };
            ApplyButtonResources(_closeConfigBtn,
                bg: s_accentBg, bgHov: s_accentHv, bgPrs: s_accentPr,
                border: s_accent);
            _closeConfigBtn.Click += (_, _) => AppWindow.Hide();

            // ── Layout ───────────────────────────────────────────────────────────
            _buttonPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Spacing             = 8,
                Margin              = new Thickness(24, 20, 24, 20),
            };
            _buttonPanel.Children.Add(_header);
            _buttonPanel.Children.Add(_selectRegionBtn);

            var root = new Grid();
            root.Children.Add(_buttonPanel);
            root.Loaded += (s, e) =>
            {
                var scale      = Content.XamlRoot.RasterizationScale;
                var titleBarH  = AppWindow.TitleBar.Height / scale;
                _buttonPanel.Margin = new Thickness(24, titleBarH + 8, 24, 20);
                SizeToContent();
                CenterOnPrimaryDisplay();
            };
            Content = root;

            _lensService = ServiceHost.ServiceProvider?.GetService<ILensService>();
            if (_lensService != null)
            {
                _lensService.LensesChanged += OnLensesChanged;
                RebuildLensButtons();
            }

            AppWindow.Changed += (sender, args) =>
            {
                if (args.DidVisibilityChange)
                    _lensService?.SetOverlayVisible(AppWindow.IsVisible);
            };

            PreSizeAndCenter();
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Select Region
        // ═════════════════════════════════════════════════════════════════════════

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

            // ── 1. Open the lens (if selected) BEFORE showing the menu ──────────
            // RebuildLensButtons + SizeToContent run here, so when the window is
            // revealed it is already at its correct final size — no resize flicker.
            Exception? openLensEx = null;
            if (rect != null)
            {
                AppLogger.Log($"SelectRegion: completed rect=({rect.Value.X},{rect.Value.Y} {rect.Value.Width}x{rect.Value.Height})");
                if (_lensService != null)
                {
                    try   { _lensService.OpenLens(rect.Value); }
                    catch (Exception ex)
                    {
                        openLensEx = ex;
                        AppLogger.LogException("OnSelectRegionClicked", ex);
                    }
                }
            }
            else
            {
                AppLogger.Log("SelectRegion: cancelled");
            }

            // ── 2. Cloak → Show → compositor tick → uncloak ─────────────────────
            // Prevents the one-frame black-box flash: the window is invisible while
            // the compositor renders its first frame, then revealed fully composited.
            SetCloaked(true);
            AppWindow.Show();

            var revealTcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                SetCloaked(false);
                Activate();
                revealTcs.SetResult();
            });
            await revealTcs.Task;

            // ── 3. Show any lens-open error now that the window is visible ───────
            if (openLensEx != null)
            {
                var dialog = new ContentDialog
                {
                    Title           = "Capture Error",
                    Content         = openLensEx.Message,
                    CloseButtonText = "OK",
                    XamlRoot        = Content.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Dynamic lens close buttons
        // ═════════════════════════════════════════════════════════════════════════

        private void OnLensesChanged(object? sender, System.EventArgs e)
            => _dispatcher.TryEnqueue(RebuildLensButtons);

        private void RebuildLensButtons()
        {
            // Keep _header (index 0) and _selectRegionBtn (index 1); replace everything after
            while (_buttonPanel.Children.Count > 2)
                _buttonPanel.Children.RemoveAt(_buttonPanel.Children.Count - 1);

            if (_lensService != null)
            {
                foreach (var lens in _lensService.ActiveLenses)
                {
                    var capturedId   = lens.Id;
                    var palette      = LensColorPalette.ForIndex(capturedId - 1);
                    var fg           = LensColorPalette.GetForeground(palette);
                    var hov          = LensColorPalette.Darken(palette, 0.82);
                    var prs          = LensColorPalette.Darken(palette, 0.65);
                    var fgBrush      = new SolidColorBrush(fg);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        MinWidth            = 200,
                        MinHeight           = 40,
                        CornerRadius        = new CornerRadius(4),
                        BorderThickness     = new Thickness(1),
                        Content             = MakeButtonContent("\uE711",
                                                 $"Close Capture {capturedId}", fgBrush),
                    };

                    SolidColorBrush B(Windows.UI.Color c) => new(c);
                    btn.Resources["ButtonBackground"]             = B(palette);
                    btn.Resources["ButtonBackgroundPointerOver"]  = B(hov);
                    btn.Resources["ButtonBackgroundPressed"]      = B(prs);
                    btn.Resources["ButtonBackgroundDisabled"]     = B(palette);
                    btn.Resources["ButtonForeground"]             = fgBrush;
                    btn.Resources["ButtonForegroundPointerOver"]  = fgBrush;
                    btn.Resources["ButtonForegroundPressed"]      = fgBrush;
                    btn.Resources["ButtonForegroundDisabled"]     = fgBrush;
                    btn.Resources["ButtonBorderBrush"]            = B(prs);
                    btn.Resources["ButtonBorderBrushPointerOver"] = B(prs);
                    btn.Resources["ButtonBorderBrushPressed"]     = B(prs);

                    btn.Click += (s, e) => _lensService?.CloseLens(capturedId);
                    _buttonPanel.Children.Add(btn);
                }
            }

            _buttonPanel.Children.Add(_closeConfigBtn);
            SizeToContent();
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Builder helpers
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Brand header: "UIX" in bold accent blue + "tend" in regular weight.
        /// </summary>
        private static FrameworkElement BuildHeader()
        {
            var panel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 4),
            };
            panel.Children.Add(new TextBlock
            {
                Text       = "UI",
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(s_accent),
            });
            panel.Children.Add(new TextBlock
            {
                Text       = "Xtend",
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontSize   = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            });
            return panel;
        }

        /// <summary>
        /// Creates button content: a Segoe Fluent icon glyph beside a label.
        /// When <paramref name="foreground"/> is supplied both icon and text use it;
        /// otherwise they inherit the button's foreground (useful for coloured buttons).
        /// </summary>
        private static UIElement MakeButtonContent(
            string glyph, string label, Brush? foreground = null)
        {
            var icon = new FontIcon
            {
                Glyph    = glyph,
                FontSize = 15,
            };
            var text = new TextBlock
            {
                Text              = label,
                FontFamily        = new FontFamily("Segoe UI Variable"),
                FontSize          = 12,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (foreground != null)
            {
                icon.Foreground = foreground;
                text.Foreground = foreground;
            }
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Children    = { icon, text },
            };
        }

        /// <summary>
        /// Applies background and border colour resources to a button so hover/press
        /// states look correct without touching the control template.
        /// </summary>
        private static void ApplyButtonResources(
            Button btn,
            Windows.UI.Color bg,  Windows.UI.Color bgHov, Windows.UI.Color bgPrs,
            Windows.UI.Color border)
        {
            SolidColorBrush B(Windows.UI.Color c) => new(c);
            btn.Resources["ButtonBackground"]             = B(bg);
            btn.Resources["ButtonBackgroundPointerOver"]  = B(bgHov);
            btn.Resources["ButtonBackgroundPressed"]      = B(bgPrs);
            btn.Resources["ButtonBackgroundDisabled"]     = B(bg);
            btn.Resources["ButtonBorderBrush"]            = B(border);
            btn.Resources["ButtonBorderBrushPointerOver"] = B(border);
            btn.Resources["ButtonBorderBrushPressed"]     = B(border);
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Sizing & positioning
        // ═════════════════════════════════════════════════════════════════════════

        private void PreSizeAndCenter()
        {
            var hwndPtr = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi    = GetDpiForWindow(hwndPtr);
            float scale = dpi / 96f;

            // Rough estimate: header (~30px) + SelectRegion (~48px) + CloseConfig (~48px)
            // + spacing + panel margin. SizeToContent() corrects on Loaded.
            int preW = (int)Math.Ceiling(248 * scale);
            int preH = (int)Math.Ceiling(168 * scale) + AppWindow.TitleBar.Height;

            var wa = DisplayArea.Primary.WorkArea;
            AppWindow.MoveAndResize(new RectInt32(
                wa.X + (wa.Width  - preW) / 2,
                wa.Y + (wa.Height - preH) / 2,
                preW, preH));
        }

        private void CenterOnPrimaryDisplay()
        {
            var workArea = DisplayArea.Primary.WorkArea;
            var size     = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width  - size.Width)  / 2,
                workArea.Y + (workArea.Height - size.Height) / 2));
        }

        [DllImport("user32.dll")]  private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int DwmSetWindowAttributeInt(IntPtr hwnd, uint attr, ref int value, uint size);
        private const uint DWMWA_CLOAK = 13;

        /// <summary>
        /// Cloaks (hides without un-rendering) or uncloaks the window.
        /// Used to suppress the one-frame black flash that occurs when
        /// AppWindow.Show() re-presents before the compositor has a frame ready.
        /// </summary>
        private void SetCloaked(bool cloak)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int v = cloak ? 1 : 0;
            DwmSetWindowAttributeInt(hwnd, DWMWA_CLOAK, ref v, sizeof(int));
        }

        private void SizeToContent()
        {
            if (Content?.XamlRoot == null) return;

            _buttonPanel.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            var desired = _buttonPanel.DesiredSize;
            var margin  = _buttonPanel.Margin;

            var logicalW = desired.Width  + margin.Left + margin.Right;
            var logicalH = desired.Height + margin.Top  + margin.Bottom;

            var scale = Content.XamlRoot.RasterizationScale;
            var physW = (int)Math.Ceiling(logicalW * scale);
            var physH = (int)Math.Ceiling(logicalH * scale); // no +TitleBar.Height — content extends into title bar

            AppWindow.Resize(new SizeInt32(physW, physH));
        }
    }
}
