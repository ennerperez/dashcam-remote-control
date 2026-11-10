using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Dashcam.Remote.Player
{
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
                    folderSelectionWindow.FolderSelected += folderPath =>
                    {
                        var mediaUris = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                            .Where(path => IsVideoFile(path))
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .Select(path => new Uri(path))
                            .ToArray();
                        if (mediaUris.Length == 0)
                        {
                            return;
                        }

                        var playlistEntries = PlayerPlaylistGrouping.Group(mediaUris);
                        if (playlistEntries.Count == 0)
                        {
                            return;
                        }

                        var playerWindow = new MediaPlayerWindow(null, playlistEntries[0].MediaUris, playlistUris: mediaUris);
                        desktop.MainWindow = playerWindow;
                        playerWindow.Show();
                        folderSelectionWindow.Close();
                    };
                    desktop.MainWindow = folderSelectionWindow;
                    folderSelectionWindow.Show();
                    splashWindow.Close();
                };
                desktop.MainWindow = splashWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static bool IsVideoFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".avi", StringComparison.OrdinalIgnoreCase);
        }
    }
}
