using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

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

            var rootGrid = new Grid();
            rootGrid.Children.Add(btnRegion);

            Content = rootGrid;
        }
    }
}
