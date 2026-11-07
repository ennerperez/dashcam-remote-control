using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Dashcam.Remote;

namespace Dashcam.Remote.Player
{
    public partial class MediaPlayerWindow : Window
    {
        private Window? _owner;
        private LibVLC? _libVlc;
        private readonly List<MediaPlayer> _mediaPlayers = [];
        private readonly Action<IReadOnlyList<Uri>>? _deleteCachedMedia;
        private PlaybackViewModel? _viewModel;
        private readonly List<Media> _currentMedia = [];
        private Uri[] _mediaUris = [];
        private bool _isVideoFullscreen;
        private bool _viewReady;
        private bool _isReplacingMedia;
        private int _activeCameraIndex;
        private MediaPlayer? _exportPlayer;
        private CancellationTokenSource? _playbackStartCancellation;
        private bool _isClosing;
        private long? _pendingCameraPositionMs;
        private int _pendingCameraIndex = -1;

        public MediaPlayerWindow()
        {
            InitializeComponent();
            Closing += OnClosing;
            Closed += OnClosed;
            Opened += OnOpened;
            VideoSurface.Loaded += OnVideoSurfaceLoaded;
        }

        public int MediaCount => _mediaPlayers.Count;

        public MediaPlayerWindow(Window owner, IReadOnlyList<Uri> mediaUris, Action<IReadOnlyList<Uri>>? deleteCachedMedia = null) : this()
        {
            _owner = owner;
            _deleteCachedMedia = deleteCachedMedia;
            _mediaUris = mediaUris.Take(2).ToArray();
            if (_mediaUris.Length == 0)
            {
                throw new ArgumentException("At least one media URI is required.", nameof(mediaUris));
            }

            Core.Initialize();
            _libVlc = new LibVLC();
            foreach (var uri in _mediaUris)
            {
                var player = new MediaPlayer(_libVlc);
                var media = new Media(_libVlc, uri);
                player.Media = media;
                _mediaPlayers.Add(player);
                _currentMedia.Add(media);
                player.Opening += (_, _) => ReportStatus("Opening video…");
                player.Playing += (_, _) => OnPlayerPlaying(player);
                player.Paused += (_, _) => ReportStatus("Paused");
                player.Stopped += (_, _) => ReportStatus("Stopped");
                player.EncounteredError += (_, _) => ReportStatus("Unable to play this file.");
            }

            _viewModel = new PlaybackViewModel(_mediaPlayers, _mediaUris);
            _viewModel.ChooseGpsExportStreamRequested = ChooseGpsExportStreamAsync;
            _viewModel.ExportCurrentVideoRequested = ExportCurrentVideoToMp4Async;
            _viewModel.ExportRoutePngRequested = ExportRouteMapAsPngAsync;
            _viewModel.ExportSpeedChartPngRequested = ExportSpeedChartAsPngAsync;
            _viewModel.ExportSpeedCsvRequested = ExportSpeedDataAsCsvAsync;
            _viewModel.FullscreenRequested += ToggleFullscreen;
            _viewModel.CameraSelectionChanged += index => _ = TryLoadSiblingGpsTrackAsync(index);
            _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
            ((PlayerAsyncCommand)_viewModel.ExportGpsCommand).Failed += exception => ReportStatus($"GPX export failed: {exception.Message}");
            ((PlayerAsyncCommand)_viewModel.ExportMp4Command).Failed += exception => ReportStatus($"MP4 export failed: {exception.Message}");
            ((PlayerAsyncCommand)_viewModel.ExportRoutePngCommand).Failed += exception => ReportStatus($"Route PNG export failed: {exception.Message}");
            ((PlayerAsyncCommand)_viewModel.ExportSpeedChartPngCommand).Failed += exception => ReportStatus($"Speed chart PNG export failed: {exception.Message}");
            ((PlayerAsyncCommand)_viewModel.ExportSpeedCsvCommand).Failed += exception => ReportStatus($"Speed data CSV export failed: {exception.Message}");
            ((PlayerAsyncCommand)_viewModel.ExportMp4Command).RaiseCanExecuteChanged();
            ((PlayerAsyncCommand)_viewModel.ExportRoutePngCommand).RaiseCanExecuteChanged();
            ((PlayerAsyncCommand)_viewModel.ExportSpeedChartPngCommand).RaiseCanExecuteChanged();
            ((PlayerAsyncCommand)_viewModel.ExportSpeedCsvCommand).RaiseCanExecuteChanged();
            DataContext = _viewModel;
            Owner = owner;
            UpdateOwnerSizeConstraints(initial: true);
            _owner.SizeChanged += OwnerOnSizeChanged;

        }

        public void Play(IReadOnlyList<Uri> mediaUris)
        {
            if (_viewModel is null || _mediaPlayers.Count == 0)
            {
                return;
            }

            var previousUris = _mediaUris;
            var nextUris = mediaUris.Take(2).ToArray();
            if (nextUris.Length == 0)
            {
                return;
            }

            _isReplacingMedia = true;
            _pendingCameraPositionMs = null;
            _pendingCameraIndex = -1;
            try
            {
                _mediaUris = nextUris;
                _viewModel.UpdateMediaTitle(_mediaUris);
                for (var index = 0; index < _mediaPlayers.Count; index++)
                {
                    var player = _mediaPlayers[index];
                    player.Stop();
                    player.Media = null;
                    _currentMedia[index].Dispose();
                    var uri = _mediaUris[Math.Min(index, _mediaUris.Length - 1)];
                    _currentMedia[index] = new Media(_libVlc!, uri);
                    player.Media = _currentMedia[index];
                }
            }
            finally { _isReplacingMedia = false; }

            _activeCameraIndex = _viewModel.SelectedCameraIndex;
            if (_viewReady)
            {
                QueueSelectedCameraStart(_activeCameraIndex);
            }

            if (!HasSameFiles(previousUris, _mediaUris))
            {
                _deleteCachedMedia?.Invoke(previousUris);
            }

            if (_viewReady)
            {
                _ = TryLoadSiblingGpsTrackAsync();
            }
        }

        private void OnOpened(object? sender, EventArgs eventArgs)
        {
            _viewReady = true;
            AttachSelectedPlayerToVideoSurface();
            QueueSelectedCameraStart(_viewModel?.SelectedCameraIndex ?? 0);
            _ = TryLoadSiblingGpsTrackAsync();
        }

        private void OnVideoSurfaceLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            AttachSelectedPlayerToVideoSurface();
            if (_viewReady)
            {
                QueueSelectedCameraStart(_viewModel?.SelectedCameraIndex ?? 0);
            }
        }

        private void AttachSelectedPlayerToVideoSurface()
        {
            if (_viewModel is null)
            {
                return;
            }

            var player = _viewModel.ActiveMediaPlayer;
            if (ReferenceEquals(VideoSurface.MediaPlayer, player) && player.Hwnd != IntPtr.Zero)
            {
                return;
            }

            VideoSurface.MediaPlayer = null;
            VideoSurface.MediaPlayer = player;
        }

        private void QueueSelectedCameraStart(int cameraIndex)
        {
            _playbackStartCancellation?.Cancel();
            _playbackStartCancellation?.Dispose();
            _playbackStartCancellation = new CancellationTokenSource();
            _ = StartSelectedCameraAfterHostReadyAsync(cameraIndex, _playbackStartCancellation.Token);
        }

        private async Task StartSelectedCameraAfterHostReadyAsync(int cameraIndex, CancellationToken cancellationToken)
        {
            if (!_viewReady || _viewModel is null)
            {
                return;
            }

            cameraIndex = Math.Clamp(cameraIndex, 0, _mediaPlayers.Count - 1);
            var player = _mediaPlayers[cameraIndex];
            try
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var started = await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (VideoSurface.MediaPlayer != player || player.Hwnd == IntPtr.Zero)
                        {
                            return false;
                        }

                        if (!player.IsPlaying && !player.Play())
                        {
                            _viewModel.SetPlaybackStatus("Unable to start playback.");
                        }

                        return true;
                    }, DispatcherPriority.Render);
                    if (started)
                    {
                        return;
                    }

                    await Task.Delay(50, cancellationToken);
                }
                _viewModel.SetPlaybackStatus("Video surface did not initialize.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                ReportStatus($"Playback error: {exception.Message}");
            }
        }

        private static bool HasSameFiles(Uri[] first, Uri[] second) =>
            first.Length == second.Length && first.Zip(second).All(pair => pair.First == pair.Second);

        private async Task TryLoadSiblingGpsTrackAsync(int? cameraIndex = null)
        {
            if (_mediaUris.Length == 0)
            {
                return;
            }

            var index = Math.Clamp(cameraIndex ?? _viewModel?.SelectedCameraIndex ?? 0, 0, _mediaUris.Length - 1);
            var uri = _mediaUris[index];
            if (!uri.IsFile)
            {
                _viewModel?.SetGpsUnavailable("GPS data is unavailable for this recording.");
                return;
            }

            _viewModel?.SetGpsLoading();
            try
            {
                await _viewModel!.LoadGpsTrackAsync(uri.LocalPath, index);
                if (index != _viewModel.SelectedCameraIndex)
                {
                    return;
                }

                return;
            }
            catch (InvalidDataException)
            {
                if (index != _viewModel!.SelectedCameraIndex)
                {
                    return;
                }

                var sidecarPath = Path.ChangeExtension(uri.LocalPath, ".gpx");
                if (File.Exists(sidecarPath))
                {
                    try
                    {
                        await _viewModel!.LoadGpsTrackAsync(sidecarPath, index);
                        return;
                    }
                    catch (Exception exception) { ReportStatus($"GPX load failed: {exception.Message}"); }
                }
            }
            catch (Exception exception)
            {
                if (index == _viewModel!.SelectedCameraIndex)
                {
                    ReportStatus($"GPS load failed: {exception.Message}");
                }
            }
            if (index != _viewModel!.SelectedCameraIndex)
            {
                return;
            }

            _viewModel?.SetGpsUnavailable("No usable GPS track was found in the recording.");
        }

        private async Task<Stream?> ChooseGpsExportStreamAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker is unavailable.");
            var fileName = $"{GetSelectedMediaBaseName()}.gpx";
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export GPS track",
                SuggestedFileName = string.IsNullOrWhiteSpace(fileName) ? "dashcam-track.gpx" : fileName,
                FileTypeChoices = [new FilePickerFileType("GPX track") { Patterns = ["*.gpx"] }]
            });
            return file is null ? null : await file.OpenWriteAsync();
        }

        private async Task ExportCurrentVideoToMp4Async(int cameraIndex)
        {
            if (_libVlc is null || _mediaUris.Length == 0)
            {
                return;
            }

            cameraIndex = Math.Clamp(cameraIndex, 0, _mediaUris.Length - 1);
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker is unavailable.");
            var sourceName = GetMediaBaseName(cameraIndex);
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export recording to MP4",
                SuggestedFileName = (string.IsNullOrWhiteSpace(sourceName) ? "dashcam-video" : sourceName) + ".mp4",
                FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }]
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new MediaPlayer(_libVlc);
            _exportPlayer = player;
            player.EndReached += (_, _) => finished.TrySetResult(null);
            player.EncounteredError += (_, _) => finished.TrySetResult(new InvalidOperationException("LibVLC could not export this recording."));
            using var media = new Media(_libVlc, _mediaUris[cameraIndex]);
            var output = path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
            media.AddOption($":sout=#std{{access=file,mux=mp4,dst='{output}'}}");
            media.AddOption(":sout-keep");
            try
            {
                player.Media = media;
                if (!player.Play())
                {
                    throw new InvalidOperationException("LibVLC could not start MP4 export.");
                }

                ReportStatus("Exporting MP4…");
                var error = await finished.Task.WaitAsync(TimeSpan.FromHours(3));
                if (error is not null)
                {
                    throw error;
                }

                ReportStatus("MP4 export complete.");
            }
            finally
            {
                player.Stop();
                player.Dispose();
                if (ReferenceEquals(_exportPlayer, player))
                {
                    _exportPlayer = null;
                }
            }
        }

        private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(PlaybackViewModel.SelectedCameraIndex))
            {
                SwitchCamera();
            }
        }

        private void SwitchCamera()
        {
            if (!_viewReady || _isReplacingMedia || _viewModel is null)
            {
                return;
            }

            var nextIndex = Math.Clamp(_viewModel.SelectedCameraIndex, 0, _mediaPlayers.Count - 1);
            var currentPlayer = _mediaPlayers[_activeCameraIndex];
            _pendingCameraPositionMs = _pendingCameraIndex == _activeCameraIndex && _pendingCameraPositionMs is long pendingPosition
                ? pendingPosition
                : Math.Max(0, currentPlayer.Time);
            _pendingCameraIndex = nextIndex;

            foreach (var player in _mediaPlayers)
            {
                if (player != _mediaPlayers[nextIndex])
                {
                    player.Stop();
                }
            }

            _activeCameraIndex = nextIndex;
            VideoSurface.MediaPlayer = _mediaPlayers[nextIndex];
            QueueSelectedCameraStart(nextIndex);
        }

        private void OnPlayerPlaying(MediaPlayer player)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                if (_viewModel is not null && player == _mediaPlayers[_viewModel.SelectedCameraIndex] &&
                    _pendingCameraIndex == _viewModel.SelectedCameraIndex && _pendingCameraPositionMs is long targetTime)
                {
                    if (player.Length > 0)
                    {
                        targetTime = Math.Min(targetTime, player.Length);
                    }

                    player.Time = targetTime;
                    _pendingCameraPositionMs = null;
                    _pendingCameraIndex = -1;
                }

                _viewModel?.SetPlaybackStatus("Playing");
            });
        }

        private async Task ExportRouteMapAsPngAsync()
        {
            if (_viewModel?.HasGpsTrack != true)
            {
                throw new InvalidOperationException("No GPS route is loaded for this recording.");
            }

            await using var destination = await ChoosePngExportStreamAsync("Export GPS route map", $"{GetSelectedMediaBaseName()}_route.png");
            if (destination is null)
            {
                return;
            }

            var width = Math.Max(400, (int)Math.Ceiling((double)GpsMap.Bounds.Width));
            var height = Math.Max(260, (int)Math.Ceiling((double)GpsMap.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(GpsMap);
            bitmap.Save(destination, new PngBitmapEncoderOptions());
            ReportStatus("Route map PNG exported.");
        }

        private async Task ExportSpeedChartAsPngAsync()
        {
            await using var destination = await ChoosePngExportStreamAsync("Export speed distribution", $"{GetSelectedMediaBaseName()}_speed-distribution.png");
            if (destination is null)
            {
                return;
            }

            var pixelWidth = Math.Max(900, (int)Math.Ceiling(SpeedDistributionChart.Bounds.Width));
            var pixelHeight = Math.Max(240, (int)Math.Ceiling(SpeedDistributionChart.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
            bitmap.Render(SpeedDistributionChart);
            bitmap.Save(destination, new PngBitmapEncoderOptions());
            ReportStatus("Speed distribution PNG exported.");
        }

        private async Task ExportSpeedDataAsCsvAsync()
        {
            if (_viewModel?.HasGpsTrack != true)
            {
                throw new InvalidOperationException("No GPS speed data is loaded for this recording.");
            }

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker is unavailable.");
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export speed data as CSV",
                SuggestedFileName = $"{GetSelectedMediaBaseName()}_speed-data.csv",
                FileTypeChoices = [new FilePickerFileType("CSV data") { Patterns = ["*.csv"] }]
            });
            if (file is null)
            {
                return;
            }

            await using var destination = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(destination, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
            await writer.WriteLineAsync("timestamp,latitude,longitude,altitude_m,speed_kmh");
            foreach (var point in _viewModel.GpsPoints)
            {
                var line = string.Join(',',
                    point.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    point.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    point.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    point.ElevationMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    (point.SpeedMetersPerSecond * 3.6).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                await writer.WriteLineAsync(line);
            }

            await writer.FlushAsync();
            ReportStatus("Speed data CSV exported.");
        }

        private async Task<Stream?> ChoosePngExportStreamAsync(string title, string suggestedFileName)
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker is unavailable.");
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
            });
            return file is null ? null : await file.OpenWriteAsync();
        }

        private string GetSelectedMediaBaseName() => GetMediaBaseName(_viewModel?.SelectedCameraIndex ?? 0);

        private string GetMediaBaseName(int cameraIndex)
        {
            if (_mediaUris.Length == 0)
            {
                return "dashcam-recording";
            }

            var uri = _mediaUris[Math.Clamp(cameraIndex, 0, _mediaUris.Length - 1)];
            var filePath = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            return string.IsNullOrWhiteSpace(baseName) ? "dashcam-recording" : baseName;
        }

        private void ToggleFullscreen()
        {
            _isVideoFullscreen = !_isVideoFullscreen;
            if (_isVideoFullscreen)
            {
                Grid.SetColumnSpan(VideoPanel, 2);
                Grid.SetRowSpan(VideoAndMapGrid, 3);
                MapPanel.IsVisible = false;
                TelemetryPanel.IsVisible = false;
                SpeedDistributionPanel.IsVisible = false;
                PlaybackControlsPanel.IsVisible = false;
                return;
            }

            Grid.SetColumnSpan(VideoPanel, 1);
            Grid.SetRowSpan(VideoAndMapGrid, 1);
            MapPanel.IsVisible = true;
            TelemetryPanel.IsVisible = true;
            SpeedDistributionPanel.IsVisible = true;
            PlaybackControlsPanel.IsVisible = true;
        }

        private void OwnerOnSizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateOwnerSizeConstraints(initial: false);

        private void UpdateOwnerSizeConstraints(bool initial)
        {
            if (_owner is null)
            {
                return;
            }

            var ownerWidth = _owner.Bounds.Width > 0 ? _owner.Bounds.Width : _owner.Width;
            var ownerHeight = _owner.Bounds.Height > 0 ? _owner.Bounds.Height : _owner.Height;
            if (ownerWidth <= 0 || ownerHeight <= 0)
            {
                return;
            }

            MinWidth = ownerWidth * 0.8;
            MaxWidth = ownerWidth;
            MinHeight = ownerHeight * 0.8;
            MaxHeight = ownerHeight;
            Width = initial ? ownerWidth * 0.9 : Math.Clamp(Bounds.Width, MinWidth, MaxWidth);
            Height = initial ? ownerHeight * 0.9 : Math.Clamp(Bounds.Height, MinHeight, MaxHeight);
        }

        private void ReportStatus(string status)
        {
            if (_isClosing)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_isClosing)
                {
                    _viewModel?.SetPlaybackStatus(status);
                }
            });
        }

        private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
        {
            _isClosing = true;
            _playbackStartCancellation?.Cancel();
            _exportPlayer?.Stop();
            foreach (var player in _mediaPlayers)
            {
                player.Stop();
            }

            VideoSurface.MediaPlayer = null;
        }

        private void OnClosed(object? sender, EventArgs eventArgs)
        {
            _playbackStartCancellation?.Cancel();
            _playbackStartCancellation?.Dispose();
            _playbackStartCancellation = null;
            if (_owner is not null)
            {
                _owner.SizeChanged -= OwnerOnSizeChanged;
            }

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
                _viewModel.Dispose();
            }
            foreach (var player in _mediaPlayers)
            {
                player.Media = null;
                player.Dispose();
            }

            foreach (var media in _currentMedia)
            {
                media.Dispose();
            }

            _libVlc?.Dispose();
            try { _deleteCachedMedia?.Invoke(_mediaUris); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
