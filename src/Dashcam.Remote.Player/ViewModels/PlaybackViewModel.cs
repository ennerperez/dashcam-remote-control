using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Dashcam.Remote;

namespace Dashcam.Remote.Player
{
    public sealed class PlaybackViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IReadOnlyList<MediaPlayer> _players;
        private readonly IReadOnlyList<Uri> _playlistUris;
        private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private IReadOnlyList<GpsPoint> _gpsPoints = [];
        private DateTimeOffset? _gpsRecordingStart;
        private string _mediaTitle = "Video";
        private string _playbackStatus = "Opening video…";
        private string _currentTimeText = "00:00";
        private string _durationText = "00:00";
        private string _gpsTrackStatus = "GPS data is read from the recording.";
        private string _currentSpeedText = "—";
        private string _averageSpeedText = "—";
        private string _distanceText = "—";
        private string _latitudeText = "—";
        private string _longitudeText = "—";
        private string _altitudeText = "—";
        private double _meanPointSpeedKmh;
        private double _speedStandardDeviationKmh;
        private double _maximumPointSpeedKmh;
        private double _minimumPointSpeedKmh;
        private bool _isPlaying;
        private bool _canSeek;
        private bool _updatingPosition;
        private bool _isMuted;
        private bool _hasGpsTrack;
        private bool _isPlaylistOpen;
        private int _selectedCameraIndex;
        private double _zoom = 1;
        private double _position;
        private bool _disposed;

        public PlaybackViewModel(IReadOnlyList<MediaPlayer> players, IReadOnlyList<Uri> mediaUris, IReadOnlyList<Uri>? playlistUris = null)
        {
            _players = players;
            _playlistUris = playlistUris ?? mediaUris;
            UpdateMediaTitle(mediaUris);
            TogglePlaybackCommand = new PlayerRelayCommand(_ => TogglePlayback());
            StopPlaybackCommand = new PlayerRelayCommand(_ => StopPlayback());
            SeekBackwardCommand = new PlayerRelayCommand(_ => SeekBy(TimeSpan.FromSeconds(-10)), _ => CanSeek);
            SeekForwardCommand = new PlayerRelayCommand(_ => SeekBy(TimeSpan.FromSeconds(10)), _ => CanSeek);
            ToggleMuteCommand = new PlayerRelayCommand(_ => ToggleMute());
            SelectCameraCommand = new PlayerRelayCommand(value => SelectCamera(value), _ => HasRearCamera);
            ToggleFullscreenCommand = new PlayerRelayCommand(_ => FullscreenRequested?.Invoke());
            TogglePlaylistCommand = new PlayerRelayCommand(_ => IsPlaylistOpen = !IsPlaylistOpen);
            ExportGpsCommand = new PlayerAsyncCommand(ExportGpsAsync, () => HasGpsTrack);
            ExportMp4Command = new PlayerAsyncCommand(ExportMp4Async, () => ExportCurrentVideoRequested is not null);
            ExportRoutePngCommand = new PlayerAsyncCommand(ExportRoutePngAsync, () => HasGpsTrack);
            ExportSpeedChartPngCommand = new PlayerAsyncCommand(ExportSpeedChartPngAsync, () => HasGpsTrack);
            ExportSpeedCsvCommand = new PlayerAsyncCommand(ExportSpeedCsvAsync, () => HasGpsTrack);
            _progressTimer.Tick += (_, _) => RefreshPlaybackState();
            _progressTimer.Start();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? FullscreenRequested;
        public event Action<int>? CameraSelectionChanged;
        public Func<Task<Stream?>>? ChooseGpsExportStreamRequested { get; set; }
        public Func<int, Task>? ExportCurrentVideoRequested { get; set; }
        public Func<Task>? ExportRoutePngRequested { get; set; }
        public Func<Task>? ExportSpeedChartPngRequested { get; set; }
        public Func<Task>? ExportSpeedCsvRequested { get; set; }
        public ICommand TogglePlaybackCommand { get; }
        public ICommand StopPlaybackCommand { get; }
        public ICommand SeekBackwardCommand { get; }
        public ICommand SeekForwardCommand { get; }
        public ICommand ToggleMuteCommand { get; }
        public ICommand SelectCameraCommand { get; }
        public ICommand ToggleFullscreenCommand { get; }
        public ICommand TogglePlaylistCommand { get; }
        public ICommand ExportGpsCommand { get; }
        public ICommand ExportMp4Command { get; }
        public ICommand ExportRoutePngCommand { get; }
        public ICommand ExportSpeedChartPngCommand { get; }
        public ICommand ExportSpeedCsvCommand { get; }

        public IReadOnlyList<Uri> PlaylistUris => _playlistUris;

        public bool IsPlaylistOpen
        {
            get => _isPlaylistOpen;
            private set => Set(ref _isPlaylistOpen, value);
        }

        public string MediaTitle { get => _mediaTitle; private set => Set(ref _mediaTitle, value); }
        public string PlaybackStatus
        {
            get => _playbackStatus;
            private set
            {
                if (!Set(ref _playbackStatus, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(PlaybackButtonLabel));
            }
        }
        public string CurrentTimeText { get => _currentTimeText; private set => Set(ref _currentTimeText, value); }
        public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }
        public string GpsTrackStatus { get => _gpsTrackStatus; private set => Set(ref _gpsTrackStatus, value); }
        public string CurrentSpeedText { get => _currentSpeedText; private set => Set(ref _currentSpeedText, value); }
        public string AverageSpeedText { get => _averageSpeedText; private set => Set(ref _averageSpeedText, value); }
        public string DistanceText { get => _distanceText; private set => Set(ref _distanceText, value); }
        public string LatitudeText { get => _latitudeText; private set => Set(ref _latitudeText, value); }
        public string LongitudeText { get => _longitudeText; private set => Set(ref _longitudeText, value); }
        public string AltitudeText { get => _altitudeText; private set => Set(ref _altitudeText, value); }
        public bool IsMuted
        {
            get => _isMuted;
            private set
            {
                if (!Set(ref _isMuted, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(MuteButtonLabel));
                OnPropertyChanged(nameof(MuteButtonIconPath));
            }
        }
        public bool HasGpsTrack
        {
            get => _hasGpsTrack;
            private set
            {
                if (!Set(ref _hasGpsTrack, value))
                {
                    return;
                }

                ((PlayerAsyncCommand)ExportGpsCommand).RaiseCanExecuteChanged();
                ((PlayerAsyncCommand)ExportRoutePngCommand).RaiseCanExecuteChanged();
                ((PlayerAsyncCommand)ExportSpeedChartPngCommand).RaiseCanExecuteChanged();
                ((PlayerAsyncCommand)ExportSpeedCsvCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(GpsRouteJson));
                OnPropertyChanged(nameof(GpsCurrentPointJson));
                OnPropertyChanged(nameof(GpsPoints));
                OnPropertyChanged(nameof(MaximumSpeedText));
                OnPropertyChanged(nameof(MinimumSpeedText));
            }
        }
        public bool HasRearCamera => _players.Count > 1;
        public int SelectedCameraIndex
        {
            get => _selectedCameraIndex;
            private set
            {
                if (!Set(ref _selectedCameraIndex, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(SelectedCameraLabel));
                OnPropertyChanged(nameof(SelectedCameraName));
                OnPropertyChanged(nameof(ActiveMediaPlayer));
                OnPropertyChanged(nameof(IsFrontSelected));
                OnPropertyChanged(nameof(IsRearSelected));
                ApplyVideoCrop();
            }
        }
        public MediaPlayer ActiveMediaPlayer => _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
        public double Zoom
        {
            get => _zoom;
            set
            {
                var zoom = Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, 1, 2);
                if (!Set(ref _zoom, zoom))
                {
                    return;
                }

                OnPropertyChanged(nameof(ZoomText));
                ApplyVideoCrop();
            }
        }
        public string ZoomText => $"{Zoom:0.0}×";
        public bool IsFrontSelected => SelectedCameraIndex == 0;
        public bool IsRearSelected => SelectedCameraIndex == 1;
        public string SelectedCameraName => _selectedCameraIndex == 0 ? "Front view" : "Rear view";
        public string SelectedCameraLabel => _selectedCameraIndex == 0 ? "Front" : "Rear";
        public string MuteButtonLabel => IsMuted ? "Unmute" : "Mute";
        public string MuteButtonIconPath => IsMuted ? "/Assets/Icons/volume_off.svg" : "/Assets/Icons/volume_up.svg";
        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (!Set(ref _isPlaying, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(PlaybackButtonLabel));
                OnPropertyChanged(nameof(PlaybackButtonIconPath));
            }
        }
        public bool CanSeek
        {
            get => _canSeek;
            private set
            {
                if (!Set(ref _canSeek, value))
                {
                    return;
                }

                ((PlayerRelayCommand)SeekBackwardCommand).RaiseCanExecuteChanged();
                ((PlayerRelayCommand)SeekForwardCommand).RaiseCanExecuteChanged();
            }
        }
        public double Position
        {
            get => _position;
            set
            {
                var position = Math.Clamp(value, 0d, 1d);
                if (!Set(ref _position, position) || _updatingPosition || !CanSeek)
                {
                    return;
                }

                _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)].Position = (float)position;
            }
        }
        public string PlaybackButtonLabel => IsPlaying ? "Pause" : "Play";
        public string PlaybackButtonIconPath => IsPlaying ? "/Assets/Icons/pause.svg" : "/Assets/Icons/play_arrow.svg";
        public string GpsRouteJson => JsonSerializer.Serialize(_gpsPoints.Select(point => new { lat = point.Latitude, lon = point.Longitude, speed = Math.Max(0, point.SpeedMetersPerSecond * 3.6) }));
        public IReadOnlyList<GpsPoint> GpsPoints => _gpsPoints;
        public GpsPoint? GpsCurrentPoint => CurrentPoint;
        public double MeanPointSpeedKmh => _meanPointSpeedKmh;
        public double SpeedStandardDeviationKmh => _speedStandardDeviationKmh;
        public double MaximumPointSpeedKmh => _maximumPointSpeedKmh;
        public string MaximumSpeedText => HasGpsTrack ? $"{_maximumPointSpeedKmh:0.0} km/h" : "—";
        public string MinimumSpeedText => HasGpsTrack ? $"{_minimumPointSpeedKmh:0.0} km/h" : "—";
        public string SpeedDistributionSummary => $"μ {_meanPointSpeedKmh:0.0} km/h · σ {_speedStandardDeviationKmh:0.0} km/h";
        public string GpsCurrentPointJson
        {
            get
            {
                var point = CurrentPoint;
                return point is null ? "null" : JsonSerializer.Serialize(new { lat = point.Latitude, lon = point.Longitude });
            }
        }

        public void UpdateMediaTitle(IReadOnlyList<Uri> mediaUris)
        {
            var names = mediaUris.Select(uri => Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var name = string.Join(" + ", names);
            _gpsRecordingStart = mediaUris.FirstOrDefault(uri => uri.IsFile) is { } firstUri &&
                                 DateTimeOffset.TryParseExact(
                                     Path.GetFileNameWithoutExtension(firstUri.LocalPath).AsSpan(0, Math.Min(19, Path.GetFileNameWithoutExtension(firstUri.LocalPath).Length)),
                                     "yyyy-MM-dd_HH_mm_ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var recordingStart)
                ? recordingStart
                : null;
            SelectedCameraIndex = 0;
            Zoom = 1;
            _gpsPoints = [];
            HasGpsTrack = false;
            GpsTrackStatus = "Checking recording for GPS data…";
            CurrentSpeedText = AverageSpeedText = DistanceText = LatitudeText = LongitudeText = AltitudeText = "—";
            _meanPointSpeedKmh = _speedStandardDeviationKmh = _maximumPointSpeedKmh = _minimumPointSpeedKmh = 0;
            OnPropertyChanged(nameof(GpsPoints));
            OnPropertyChanged(nameof(GpsRouteJson));
            OnPropertyChanged(nameof(SpeedDistributionSummary));
            MediaTitle = string.IsNullOrWhiteSpace(name) ? "Dashcam Player" : name;
            PlaybackStatus = "Opening video…";
            CurrentTimeText = "00:00";
            DurationText = "00:00";
            IsPlaying = false;
            CanSeek = false;
            _updatingPosition = true;
            Position = 0;
            _updatingPosition = false;
        }

        public void SetPlaybackStatus(string status)
        {
            if (_disposed)
            {
                return;
            }

            PlaybackStatus = status;
            if (status == "Playing")
            {
                ApplyVideoCrop();
            }

            RefreshPlaybackState();
        }

        public void SelectCamera(object? value)
        {
            if (value is null || !int.TryParse(value.ToString(), out var index))
            {
                return;
            }

            if (index < 0 || index >= _players.Count)
            {
                return;
            }

            SelectedCameraIndex = index;
            CameraSelectionChanged?.Invoke(index);
            RefreshPlaybackState();
        }

        public void Dispose()
        {
            _disposed = true;
            _progressTimer.Stop();
        }

        public async Task LoadGpsTrackAsync(string path, int? expectedCameraIndex = null)
        {
            var isTs = Path.GetExtension(path).Equals(".ts", StringComparison.OrdinalIgnoreCase);
            var points = await Task.Run(() => isTs ? GpsTrack.ReadFromTs(path) : GpsTrack.Read(path));
            if (expectedCameraIndex is not null && expectedCameraIndex != SelectedCameraIndex)
            {
                return;
            }

            if (points.Count == 0)
            {
                throw new InvalidDataException(isTs
                    ? "No valid GPS records were found in the TS trailer."
                    : "The selected GPX file has no valid track points.");
            }

            _gpsPoints = points;
            HasGpsTrack = true;
            GpsTrackStatus = $"{points.Count:N0} GPS points · {(isTs ? "embedded in TS" : Path.GetFileName(path))}";
            RefreshTelemetry();
            OnPropertyChanged(nameof(GpsRouteJson));
            OnPropertyChanged(nameof(GpsCurrentPointJson));
            OnPropertyChanged(nameof(GpsPoints));
        }

        public void SetGpsUnavailable(string message)
        {
            if (HasGpsTrack)
            {
                return;
            }

            GpsTrackStatus = message;
            OnPropertyChanged(nameof(GpsPoints));
            OnPropertyChanged(nameof(GpsCurrentPoint));
        }

        public void SetGpsLoading()
        {
            _gpsPoints = [];
            HasGpsTrack = false;
            GpsTrackStatus = "Loading GPS data from recording…";
            CurrentSpeedText = AverageSpeedText = DistanceText = LatitudeText = LongitudeText = AltitudeText = "—";
            OnPropertyChanged(nameof(GpsRouteJson));
            OnPropertyChanged(nameof(GpsCurrentPointJson));
            OnPropertyChanged(nameof(GpsPoints));
            OnPropertyChanged(nameof(GpsCurrentPoint));
        }

        private async Task ExportGpsAsync()
        {
            if (!HasGpsTrack || ChooseGpsExportStreamRequested is null)
            {
                return;
            }

            await using var destination = await ChooseGpsExportStreamRequested();
            if (destination is null)
            {
                return;
            }

            await GpsTrack.WriteAsync(destination, _gpsPoints);
            GpsTrackStatus = "GPX track exported.";
        }

        private async Task ExportMp4Async()
        {
            if (ExportCurrentVideoRequested is not null)
            {
                await ExportCurrentVideoRequested(SelectedCameraIndex);
            }
        }

        private async Task ExportRoutePngAsync()
        {
            if (ExportRoutePngRequested is not null)
            {
                await ExportRoutePngRequested();
            }
        }

        private async Task ExportSpeedChartPngAsync()
        {
            if (ExportSpeedChartPngRequested is not null)
            {
                await ExportSpeedChartPngRequested();
            }
        }

        private async Task ExportSpeedCsvAsync()
        {
            if (ExportSpeedCsvRequested is not null)
            {
                await ExportSpeedCsvRequested();
            }
        }

        private void TogglePlayback()
        {
            var player = _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
            if (player.IsPlaying)
            {
                player.Pause();
            }
            else if (!player.Play())
            {
                PlaybackStatus = "Unable to play this file.";
            }

            RefreshPlaybackState();
        }

        private void StopPlayback()
        {
            foreach (var player in _players)
            {
                player.Stop();
            }

            PlaybackStatus = "Stopped";
            RefreshPlaybackState();
        }

        private void ToggleMute()
        {
            IsMuted = !IsMuted;
            foreach (var player in _players)
            {
                player.Mute = IsMuted;
            }
        }

        private void SeekBy(TimeSpan offset)
        {
            if (!CanSeek)
            {
                return;
            }

            var player = _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
            var duration = Math.Max(0, player.Length);
            player.Time = Math.Clamp(player.Time + (long)offset.TotalMilliseconds, 0, duration);
            RefreshPlaybackState();
        }

        private void RefreshPlaybackState()
        {
            if (_disposed || _players.Count == 0)
            {
                return;
            }

            var player = _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
            IsPlaying = player.IsPlaying;
            var duration = player.Length;
            CanSeek = player.IsSeekable && duration > 0;
            DurationText = FormatTime(duration);
            CurrentTimeText = FormatTime(player.Time);
            _updatingPosition = true;
            Position = CanSeek ? Math.Clamp(player.Time / (double)duration, 0d, 1d) : 0d;
            _updatingPosition = false;
            RefreshTelemetry();
        }

        public void ApplyVideoCrop()
        {
            if (_players.Count == 0)
            {
                return;
            }

            var player = ActiveMediaPlayer;
            if (Zoom <= 1)
            {
                player.CropGeometry = string.Empty;
                return;
            }

            uint sourceWidth = 0;
            uint sourceHeight = 0;
            player.Size(0, ref sourceWidth, ref sourceHeight);
            if (sourceWidth == 0 || sourceHeight == 0)
            {
                return;
            }

            var cropWidth = Math.Max(1u, (uint)Math.Round(sourceWidth / Zoom));
            var cropHeight = Math.Max(1u, (uint)Math.Round(sourceHeight / Zoom));
            var cropX = (sourceWidth - cropWidth) / 2;
            var cropY = (sourceHeight - cropHeight) / 2;
            player.CropGeometry = $"{cropWidth}x{cropHeight}+{cropX}+{cropY}";
        }

        private GpsPoint? CurrentPoint
        {
            get
            {
                if (_gpsPoints.Count == 0)
                {
                    return null;
                }

                var elapsed = Math.Max(0, _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)].Time) / 1000d;
                var recordingStart = _gpsRecordingStart ?? _gpsPoints[0].Time;
                var targetTime = recordingStart.AddSeconds(elapsed);
                var low = 0;
                var high = _gpsPoints.Count - 1;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    if (_gpsPoints[middle].Time <= targetTime)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }
                if (high < 0)
                {
                    return null;
                }

                return _gpsPoints[Math.Clamp(high, 0, _gpsPoints.Count - 1)];
            }
        }

        private void RefreshTelemetry()
        {
            RefreshSpeedStatistics();
            var point = CurrentPoint;
            if (point is null)
            {
                CurrentSpeedText = AverageSpeedText = DistanceText = LatitudeText = LongitudeText = AltitudeText = "—";
                OnPropertyChanged(nameof(GpsCurrentPointJson));
                OnPropertyChanged(nameof(GpsCurrentPoint));
                return;
            }

            var distanceKm = 0d;
            for (var i = 1; i < _gpsPoints.Count; i++)
            {
                distanceKm += HaversineKm(_gpsPoints[i - 1], _gpsPoints[i]);
            }

            var elapsedHours = Math.Max(0.001, (_gpsPoints[^1].Time - _gpsPoints[0].Time).TotalHours);
            CurrentSpeedText = $"{point.SpeedMetersPerSecond * 3.6:0.0} km/h";
            AverageSpeedText = $"{distanceKm / elapsedHours:0.0} km/h";
            DistanceText = $"{distanceKm:0.00} km";
            LatitudeText = point.Latitude.ToString("0.000000", CultureInfo.CurrentCulture);
            LongitudeText = point.Longitude.ToString("0.000000", CultureInfo.CurrentCulture);
            AltitudeText = $"{point.ElevationMeters:0.0} m";
            OnPropertyChanged(nameof(GpsPoints));
            OnPropertyChanged(nameof(GpsCurrentPointJson));
            OnPropertyChanged(nameof(GpsCurrentPoint));
        }

        private void RefreshSpeedStatistics()
        {
            var speeds = _gpsPoints.Select(item => Math.Max(0, item.SpeedMetersPerSecond * 3.6)).ToArray();
            if (speeds.Length == 0)
            {
                _meanPointSpeedKmh = _speedStandardDeviationKmh = _maximumPointSpeedKmh = _minimumPointSpeedKmh = 0;
            }
            else
            {
                _meanPointSpeedKmh = speeds.Average();
                _speedStandardDeviationKmh = Math.Sqrt(speeds.Average(speed => Math.Pow(speed - _meanPointSpeedKmh, 2)));
                _maximumPointSpeedKmh = speeds.Max();
                _minimumPointSpeedKmh = speeds.Min();
            }

            OnPropertyChanged(nameof(MeanPointSpeedKmh));
            OnPropertyChanged(nameof(SpeedStandardDeviationKmh));
            OnPropertyChanged(nameof(MaximumPointSpeedKmh));
            OnPropertyChanged(nameof(MaximumSpeedText));
            OnPropertyChanged(nameof(MinimumSpeedText));
            OnPropertyChanged(nameof(SpeedDistributionSummary));
        }

        private static double HaversineKm(GpsPoint first, GpsPoint second)
        {
            const double earthRadiusKm = 6371.0088;
            var latitude1 = DegreesToRadians(first.Latitude);
            var latitude2 = DegreesToRadians(second.Latitude);
            var deltaLatitude = latitude2 - latitude1;
            var deltaLongitude = DegreesToRadians(second.Longitude - first.Longitude);
            var value = Math.Pow(Math.Sin(deltaLatitude / 2), 2) + Math.Cos(latitude1) * Math.Cos(latitude2) * Math.Pow(Math.Sin(deltaLongitude / 2), 2);
            return earthRadiusKm * 2 * Math.Asin(Math.Sqrt(value));
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

        private static string FormatTime(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes:00}:{time.Seconds:00}";
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
