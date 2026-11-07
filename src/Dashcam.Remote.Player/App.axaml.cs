using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Dashcam.Remote.Player;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splashWindow = new SplashWindow();
            splashWindow.StartupReady += (_, _) =>
            {
                var folderSelectionWindow = new FolderSelectionWindow();
                desktop.MainWindow = folderSelectionWindow;
                folderSelectionWindow.Show();
                splashWindow.Close();
            };
            desktop.MainWindow = splashWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
