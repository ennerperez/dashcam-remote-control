using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
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
                var startupFolder = GetStartupFolder(desktop.Args ?? []);
                if (startupFolder is not null)
                {
                    try
                    {
                        if (!TryGetPlaylist(startupFolder, out var mediaUris, out var firstEntry))
                        {
                            ShowStartupError(desktop, $"No supported video files were found in '{startupFolder}'.");
                            return;
                        }

                        var playerWindow = new MediaPlayerWindow(null, firstEntry.MediaUris, playlistUris: mediaUris);
                        desktop.MainWindow = playerWindow;
                        playerWindow.Show();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        ShowStartupError(desktop, exception.Message);
                    }
                }
                else
                {
                    var splashWindow = new SplashWindow();
                    splashWindow.StartupReady += (_, _) =>
                    {
                        var folderSelectionWindow = new FolderSelectionWindow();
                        folderSelectionWindow.FolderSelected += folderPath =>
                        {
                            if (!TryGetPlaylist(folderPath, out var mediaUris, out var firstEntry))
                            {
                                return;
                            }

                            var playerWindow = new MediaPlayerWindow(null, firstEntry.MediaUris, playlistUris: mediaUris);
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

        private static string? GetStartupFolder(string[] args)
        {
            if (args.Length >= 2 && string.Equals(args[0], "--folder", StringComparison.OrdinalIgnoreCase))
            {
                return args[1];
            }

            return args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
                ? args[0]
                : null;
        }

        private static bool TryGetPlaylist(string folderPath, out Uri[] mediaUris, out PlayerPlaylistEntry firstEntry)
        {
            mediaUris = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Where(IsVideoFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new Uri(path))
                .ToArray();
            var entries = PlayerPlaylistGrouping.Group(mediaUris);
            if (entries.Count == 0)
            {
                firstEntry = null!;
                return false;
            }

            firstEntry = entries[0];
            return true;
        }

        private static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, string message)
        {
            var window = new Window
            {
                Title = "Unable to open recordings",
                Width = 520,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBlock
                {
                    Text = message,
                    Margin = new Thickness(24),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            desktop.MainWindow = window;
            window.Show();
        }
    }
}
