using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Dashcam.Remote.Player;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
