using System;
using Microsoft.Extensions.DependencyInjection;
using UIXtend.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using UIXtend.Core.Interfaces;

namespace UIXtend.Core.UI
{
    public class MainMenuWindow : Window
    {
        private readonly StackPanel _buttonPanel;
        private readonly Button _selectRegionBtn;
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

            _buttonPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8
            };
            _buttonPanel.Children.Add(_selectRegionBtn);

            var root = new Grid();
            root.Children.Add(_buttonPanel);
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
                var btn = new Button
                {
                    Content = $"Edit Screen Capture {capturedId}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MinWidth = 200,
                    MinHeight = 40
                };
                btn.Click += (s, e) => _lensService?.CloseLens(capturedId);
                _buttonPanel.Children.Add(btn);
            }
        }
    }
}
