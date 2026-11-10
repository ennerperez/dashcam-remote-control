using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using Dashcam.Remote.Player;

namespace Dashcam.Remote
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private LibVLC? _libVlc;
        private MediaPlayer? _mediaPlayer;
        private Media? _currentMedia;
        private VideoView? _videoView;
        private MediaPlayerWindow? _filePlayerWindow;
        private bool _activityCollapsed;
        private bool _isLiveMedia;

        public MainWindow()
        {
            _viewModel = new MainWindowViewModel();
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.FileExplorer.ChooseDownloadDirectoryRequested = ChooseDownloadDirectoryAsync;
            _viewModel.FileExplorer.ChooseLocalFolderRequested = ChooseLocalFolderAsync;
            _viewModel.FileExplorer.ConfirmRequested = ConfirmAsync;
            _viewModel.FileExplorer.PlayVideosRequested = OpenFilePlayer;
            _viewModel.FileExplorer.PreviewImagesRequested = OpenImagePreview;
            _viewModel.FileExplorer.CommandFailed = _viewModel.HandleCommandFailure;
            _viewModel.FileExplorer.Log = Log;
            _viewModel.FileExplorer.OperationCompleted = FlashTaskbarIcon;
            var filesList = FilesList ?? throw new InvalidOperationException("File list control is unavailable.");
            _viewModel.FileExplorer.SelectionRequested = entry =>
            {
                var selectedItems = filesList.SelectedItems;
                if (selectedItems is null)
                {
                    return;
                }

                selectedItems.Clear();
                selectedItems.Add(entry);
                filesList.ScrollIntoView(entry);
            };
            filesList.SelectionChanged += (_, _) => _viewModel.FileExplorer.SetSelectedEntries(filesList.SelectedItems?.OfType<ExplorerFileItem>() ?? []);
            foreach (var command in _viewModel.FileExplorer.AsyncCommands)
            {
                ((AsyncCommand)command).Failed += _viewModel.HandleCommandFailure;
                if (!ReferenceEquals(command, _viewModel.FileExplorer.DownloadOrCancelSelectedCommand))
                {
                    ((AsyncCommand)command).Finished += FlashTaskbarIcon;
                }
            }

            _viewModel.CopyRequested = value => CopyToClipboardAsync("Device data", value);
            _viewModel.ConfirmRequested = ConfirmAsync;
            _viewModel.SaveFileStreamRequested = ChooseSaveFileStreamAsync;
            _viewModel.PlayRequested = OpenFilePlayer;
            _viewModel.LiveStarted = PlayLiveStream;
            _viewModel.LiveStopped = StopLiveView;
            _viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsActivityCollapsed))
                {
                    UpdateActivityLayout();
                }

                if (eventArgs.PropertyName is nameof(MainWindowViewModel.IsConnected) or nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.ConnectionStatus))
                {
                    UpdateConnectionPresentation();
                }
            };
            ((AsyncCommand)_viewModel.ConnectCommand!).Failed += _viewModel.HandleCommandFailure;
            ((AsyncCommand)_viewModel.ConnectCommand!).Finished += FlashTaskbarIcon;
            ((AsyncCommand)_viewModel.RefreshCommand).Failed += _viewModel.HandleCommandFailure;
            ((AsyncCommand)_viewModel.RefreshCommand).Finished += FlashTaskbarIcon;
            ((AsyncParameterCommand)_viewModel.DeviceActionCommand).Failed += _viewModel.HandleCommandFailure;
            ((AsyncParameterCommand)_viewModel.DeviceActionCommand).Finished += FlashTaskbarIcon;
            ((AsyncParameterCommand)_viewModel.CopyValueCommand).Failed += _viewModel.HandleCommandFailure;
            ((AsyncParameterCommand)_viewModel.CopyValueCommand).Finished += FlashTaskbarIcon;
            foreach (var command in new[] { _viewModel.LoadFilesCommand, _viewModel.LoadThumbnailCommand, _viewModel.DownloadFileCommand, _viewModel.DeleteFileCommand, _viewModel.PlayFileCommand, _viewModel.LoadSettingsCommand, _viewModel.SetTimezoneCommand, _viewModel.SetWifiCommand, _viewModel.FormatSdCommand, _viewModel.FactoryResetCommand, _viewModel.RawRequestCommand, _viewModel.ToggleRecordingCommand, _viewModel.StartRecordingCommand, _viewModel.StopRecordingCommand, _viewModel.StartLiveCommand, _viewModel.StopLiveCommand })
            {
                ((AsyncCommand)command).Failed += _viewModel.HandleCommandFailure;
                ((AsyncCommand)command).Finished += FlashTaskbarIcon;
            }

            foreach (var action in _viewModel.CameraActions)
            {
                ((AsyncCommand)action.Command).Finished += FlashTaskbarIcon;
            }

            _viewModel.CameraActions.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.NewItems is null)
                {
                    return;
                }

                foreach (CameraSwitchItem action in eventArgs.NewItems)
                {
                    ((AsyncCommand)action.Command).Finished += FlashTaskbarIcon;
                }
            };

            foreach (var setting in _viewModel.Settings)
            {
                setting.Finished += FlashTaskbarIcon;
            }

            _viewModel.Settings.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.NewItems is null)
                {
                    return;
                }

                foreach (DeviceSettingItem setting in eventArgs.NewItems)
                {
                    setting.Finished += FlashTaskbarIcon;
                }
            };

            MainTabs.SelectionChanged += (_, _) => UpdateNavigationHighlight(MainTabs.SelectedIndex);
            LogBox.TextChanged += (_, _) => LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
            UpdateConnectionPresentation();
            Closed += async (_, _) =>
            {
                _filePlayerWindow?.Close();
                await _viewModel.StopLivePreviewAsync();
                _currentMedia?.Dispose();
                _mediaPlayer?.Dispose();
                _libVlc?.Dispose();
                _viewModel.Dispose();
            };
            Log("Ready. Connect to 192.168.169.1 or enter the device IP.");
        }

        private void OnContextMenuOpening(object? sender, CancelEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                menu.DataContext = menu.PlacementTarget?.DataContext;
            }
        }

        private void OnSelectAllFilesClicked(object? sender, RoutedEventArgs e)
        {
            var selectedItems = FilesList.SelectedItems;
            if (selectedItems is null)
            {
                return;
            }

            selectedItems.Clear();
            if (SelectAllFilesCheckBox.IsChecked is not true)
            {
                return;
            }

            foreach (var file in _viewModel.FileExplorer.VisibleFiles)
            {
                selectedItems.Add(file);
            }
        }

        private void UpdateNavigationHighlight(int index)
        {
            var buttons = new[] { DashcamNavButton, LiveNavButton, SettingsNavButton, FilesNavButton, SystemNavButton };
            for (var i = 0; i < buttons.Length; i++)
            {
                buttons[i].Classes.Remove("active");
                if (i == index)
                {
                    buttons[i].Classes.Add("active");
                }
            }
        }

        private void UpdateActivityLayout()
        {
            _activityCollapsed = _viewModel.IsActivityCollapsed;
            ActivityPanel.Height = _activityCollapsed ? 48 : 150;
            ActivityPanel.Padding = _activityCollapsed ? new Thickness(8) : new Thickness(12);
            ActivityContentGrid.RowSpacing = _activityCollapsed ? 0 : 6;
            ToolTip.SetTip(ActivityToggleButton, _activityCollapsed ? "Expand activity" : "Collapse activity");
        }

        private void UpdateConnectionPresentation()
        {
            ConnectionLabel.Foreground = Avalonia.Media.Brush.Parse(_viewModel.IsConnected ? "#7FF0AE" : "#FFD66E");
            foreach (var mode in new[] { "connect", "cancel", "close" })
            {
                ConnectButton.Classes.Remove(mode);
            }

            ConnectButton.Classes.Add(_viewModel.IsConnecting ? "cancel" : _viewModel.IsConnected ? "close" : "connect");
        }

        private void FlashTaskbarIcon()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!OperatingSystem.IsWindows() || IsActive)
                {
                    return;
                }

                var windowHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (windowHandle == IntPtr.Zero)
                {
                    return;
                }

                var flashInfo = new FlashWindowInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                    WindowHandle = windowHandle,
                    Flags = FlashWindowTray,
                    Count = 3
                };
                FlashWindowEx(ref flashInfo);
            });
        }

        private const uint FlashWindowTray = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWindowInfo
        {
            public uint Size;
            public IntPtr WindowHandle;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashWindowInfo flashInfo);

        private async Task<Stream?> ChooseSaveFileStreamAsync(DeviceFile file)
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker is unavailable.");
            var pick = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = Path.GetFileName(file.Name),
                Title = "Save dashcam recording"
            });
            if (pick is null)
            {
                return null;
            }

            return await pick.OpenWriteAsync();
        }

        private async Task<string?> ChooseDownloadDirectoryAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Folder picker is unavailable.");
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose download folder", AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }

        private async Task<string?> ChooseLocalFolderAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Folder picker is unavailable.");
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose local folder to browse", AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }

        private void OpenFilePlayer(IReadOnlyList<Uri> uris)
        {
            if (uris.Count == 0)
            {
                return;
            }

            if (_filePlayerWindow is { IsVisible: true } playerWindow && playerWindow.MediaCount == uris.Count)
            {
                playerWindow.Play(uris);
                playerWindow.Activate();
                return;
            }
            _filePlayerWindow?.Close();

            var window = new MediaPlayerWindow(this, uris, PlaybackMediaCache.DeleteForMediaUris);
            _filePlayerWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_filePlayerWindow, window))
                {
                    _filePlayerWindow = null;
                }
            };
            window.Show(this);
        }

        private void OpenImagePreview(IReadOnlyList<PreviewImageItem> images)
        {
            if (images.Count == 0)
            {
                return;
            }

            var window = new ImagePreviewWindow(this, images);
            window.Show(this);
        }

        private void OnFileSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs eventArgs)
        {
            if (eventArgs.Key != Avalonia.Input.Key.Enter)
            {
                return;
            }

            _viewModel.FileExplorer.SearchCommand.Execute(null);
            eventArgs.Handled = true;
        }

        private bool PlayLiveStream(Uri uri)
        {
            _isLiveMedia = true;
            EnsurePlayer();
            var started = PlayMedia(uri);
            if (started)
            {
                VideoPlaceholder.IsVisible = false;
            }

            return started;
        }

        private void StopLiveView()
        {
            _isLiveMedia = false;
            _mediaPlayer?.Stop();
            _currentMedia?.Dispose();
            _currentMedia = null;
            if (_videoView is not null)
            {
                VideoHost.Children.Remove(_videoView);
            }

            _videoView = null;
            if (VideoPlaceholder is not null)
            {
                VideoPlaceholder.IsVisible = true;
            }
        }

        private void EnsurePlayer()
        {
            if (_mediaPlayer is null)
            {
                Core.Initialize();
                _libVlc = new LibVLC("--network-caching=600", "--clock-jitter=0");
                _libVlc.Log += (_, eventArgs) =>
                {
                    if (_isLiveMedia && (eventArgs.Level is LogLevel.Warning or LogLevel.Error))
                    {
                        ReportLivePlayback($"VLC {eventArgs.Level}: {eventArgs.Message}", eventArgs.Level == LogLevel.Error);
                    }
                };
                _mediaPlayer = new MediaPlayer(_libVlc);
                _mediaPlayer.Opening += (_, _) => ReportLivePlayback("RTSP opening…");
                _mediaPlayer.Playing += (_, _) => ReportLivePlayback("Live stream playing.");
                _mediaPlayer.EncounteredError += (_, _) => ReportLivePlayback("VLC encountered an RTSP playback error.", failed: true);
            }

            if (_videoView is not null)
            {
                return;
            }

            _videoView = new VideoView { MediaPlayer = _mediaPlayer };
            VideoHost.Children.Insert(0, _videoView);
        }

        private bool PlayMedia(Uri uri)
        {
            _mediaPlayer!.Stop();
            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVlc!, uri);
            if (uri.Scheme == "rtsp")
            {
                _currentMedia.AddOption(":rtsp-tcp");
            }

            _mediaPlayer.Media = _currentMedia;
            return _mediaPlayer.Play();
        }

        private void ReportLivePlayback(string message, bool failed = false)
        {
            if (!_isLiveMedia)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => _viewModel.ReportLivePlayback(message, failed));
        }

        private async Task CopyToClipboardAsync(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "—")
            {
                throw new InvalidOperationException("Connect to the camera before copying its data.");
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                throw new InvalidOperationException("Clipboard is unavailable on this window.");
            }

            await clipboard.SetTextAsync(value);
            Log($"{label} copied to clipboard.");
        }

        private async Task<bool> ConfirmAsync(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 18,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Spacing = 8,
                            Children =
                            {
                                new Button { Content = "Cancel", IsCancel = true },
                                new Button { Content = "Continue", IsDefault = true, Tag = true }
                            }
                        }
                    }
                }
            };

            var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[1]).Children;
            ((Button)buttons[0]).Click += (_, _) => dialog.Close(false);
            ((Button)buttons[1]).Click += (_, _) => dialog.Close(true);
            return await dialog.ShowDialog<bool>(this);
        }

        private void Log(string message)
        {
            _viewModel.Log(message);
        }

    }
}
