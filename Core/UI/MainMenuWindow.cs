using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

using Microsoft.UI.Xaml.Shapes;
using Microsoft.Extensions.DependencyInjection;

namespace UIXtend.Core.UI
{
    public class MainMenuWindow : Window
    {
        public MainMenuWindow()
        {
            // Setup Aesthetics per instructions
            SystemBackdrop = new MicaBackdrop()
            {
                Kind = MicaKind.BaseAlt
            };

            Title = "UIXtend Configuration";

            var btnRegion = new Button
            {
                Content = "Select Region",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 120,
                MinHeight = 40
            };

            btnRegion.Click += async (s, e) =>
            {
                this.AppWindow.Hide(); // Visually disappear during snipping
                
                var selectionService = ServiceHost.ServiceProvider?.GetRequiredService<Interfaces.IRegionSelectionService>();
                if (selectionService != null)
                {
                    var rect = await selectionService.StartSelectionAsync();
                    
                    this.AppWindow.Show(); // Reappear when finished!
                    
                    if (rect != null)
                    {
                        btnRegion.Content = $"Selected: {rect.Value.X}, {rect.Value.Y} [{rect.Value.Width}x{rect.Value.Height}]";
                    }
                    else
                    {
                        btnRegion.Content = "Select Region";
                    }
                }
            };

            var rootGrid = new Grid();
            rootGrid.Children.Add(btnRegion);

            Content = rootGrid;
        }
    }
}
