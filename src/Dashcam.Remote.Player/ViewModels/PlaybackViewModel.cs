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
        private readonly IReadOnlyList<PlayerPlaylistEntry> _playlistEntries;
        private Uri[] _currentMediaUris = [];
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
        private bool _isSideBySide;
        private long? _clipStartTimeMs;
        private long? _clipEndTimeMs;
        private int _selectedPlaylistIndex;
        private int _selectedCameraIndex;
        private double _playbackSpeed = 1;
        private int _volume = 100;
        private bool _isLooping;
        private bool _isExportMode;
        private double _position;
        private int _mapZoomOffset;
        private bool _isSatelliteMap;
        private bool _disposed;

        public PlaybackViewModel(IReadOnlyList<MediaPlayer> players, IReadOnlyList<Uri> mediaUris, IReadOnlyList<Uri>? playlistUris = null)
        {
            _players = players;
            _playlistEntries = PlayerPlaylistGrouping.Group(playlistUris ?? mediaUris);
            TogglePlaybackCommand = new PlayerRelayCommand(_ => TogglePlayback(), _ => HasMedia);
            StopPlaybackCommand = new PlayerRelayCommand(_ => StopPlayback());
            SeekBackwardCommand = new PlayerRelayCommand(_ => SeekBy(TimeSpan.FromSeconds(-10)), _ => CanSeek);
            SeekForwardCommand = new PlayerRelayCommand(_ => SeekBy(TimeSpan.FromSeconds(10)), _ => CanSeek);
            ToggleMuteCommand = new PlayerRelayCommand(_ => ToggleMute());
            ToggleLoopCommand = new PlayerRelayCommand(_ => IsLooping = !IsLooping);
            CaptureFrameCommand = new PlayerAsyncCommand(CaptureFrameAsync, () => HasMedia && CaptureFrameRequested is not null);
            EnterClipExportModeCommand = new PlayerRelayCommand(_ => EnterClipExportMode(), _ => CanSeek);
            CancelClipExportModeCommand = new PlayerRelayCommand(_ => IsExportMode = false);
            SelectCameraCommand = new PlayerRelayCommand(value => SelectCamera(value), CanSelectCamera);
            ToggleFullscreenCommand = new PlayerRelayCommand(_ => FullscreenRequested?.Invoke());
            ToggleSideBySideCommand = new PlayerRelayCommand(_ => IsSideBySide = !IsSideBySide, _ => HasRearCamera);
            TogglePlaylistCommand = new PlayerRelayCommand(_ => IsPlaylistOpen = !IsPlaylistOpen);
            ZoomMapInCommand = new PlayerRelayCommand(_ => MapZoomOffset = Math.Min(6, MapZoomOffset + 1));
            ZoomMapOutCommand = new PlayerRelayCommand(_ => MapZoomOffset = Math.Max(-6, MapZoomOffset - 1));
            ToggleMapLayerCommand = new PlayerRelayCommand(_ => IsSatelliteMap = !IsSatelliteMap);
            PreviousVideoCommand = new PlayerRelayCommand(_ => SelectPlaylistEntry(_selectedPlaylistIndex - 1), _ => _selectedPlaylistIndex > 0);
            NextVideoCommand = new PlayerRelayCommand(_ => SelectPlaylistEntry(_selectedPlaylistIndex + 1), _ => _selectedPlaylistIndex >= 0 && _selectedPlaylistIndex < _playlistEntries.Count - 1);
            SetClipStartCommand = new PlayerRelayCommand(_ => SetClipStart(), _ => CanSeek);
            SetClipEndCommand = new PlayerRelayCommand(_ => SetClipEnd(), _ => CanSeek);
            ExportClipCommand = new PlayerAsyncCommand(ExportClipAsync, CanExportClip);
            ExportGpsCommand = new PlayerAsyncCommand(ExportGpsAsync, () => HasGpsTrack);
            ExportMp4Command = new PlayerAsyncCommand(ExportMp4Async, () => HasMedia && ExportCurrentVideoRequested is not null);
            ExportRoutePngCommand = new PlayerAsyncCommand(ExportRoutePngAsync, () => HasGpsTrack);
            ExportSpeedChartPngCommand = new PlayerAsyncCommand(ExportSpeedChartPngAsync, () => HasGpsTrack);
            ExportSpeedCsvCommand = new PlayerAsyncCommand(ExportSpeedCsvAsync, () => HasGpsTrack);
            _progressTimer.Tick += (_, _) => RefreshPlaybackState();
            _progressTimer.Start();
            UpdateMediaTitle(mediaUris);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? FullscreenRequested;
        public event Action? SideBySidePlaybackRequested;
        public event Action<int>? CameraSelectionChanged;
        public event Action<PlayerPlaylistEntry>? PlaylistEntryRequested;
        public Func<int, long, long, Task<bool>>? ExportClipRequested { get; set; }
        public Func<int, Task>? CaptureFrameRequested { get; set; }
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
        public ICommand ToggleLoopCommand { get; }
        public ICommand CaptureFrameCommand { get; }
        public ICommand EnterClipExportModeCommand { get; }
        public ICommand CancelClipExportModeCommand { get; }
        public ICommand SelectCameraCommand { get; }
        public ICommand ToggleFullscreenCommand { get; }
        public ICommand ToggleSideBySideCommand { get; }
        public ICommand TogglePlaylistCommand { get; }
        public ICommand ZoomMapInCommand { get; }
        public ICommand ZoomMapOutCommand { get; }
        public ICommand ToggleMapLayerCommand { get; }
        public ICommand PreviousVideoCommand { get; }
        public ICommand NextVideoCommand { get; }
        public ICommand SetClipStartCommand { get; }
        public ICommand SetClipEndCommand { get; }
        public ICommand ExportClipCommand { get; }
        public ICommand ExportGpsCommand { get; }
        public ICommand ExportMp4Command { get; }
        public ICommand ExportRoutePngCommand { get; }
        public ICommand ExportSpeedChartPngCommand { get; }
        public ICommand ExportSpeedCsvCommand { get; }

        public IReadOnlyList<PlayerPlaylistEntry> PlaylistEntries => _playlistEntries;
        public string PlaylistCountText => $"{_playlistEntries.Count} items";
        public int MapZoomOffset
        {
            get => _mapZoomOffset;
            private set => Set(ref _mapZoomOffset, value);
        }
        public bool IsSatelliteMap
        {
            get => _isSatelliteMap;
            private set
            {
                if (Set(ref _isSatelliteMap, value))
                {
                    OnPropertyChanged(nameof(MapLayerButtonLabel));
                }
            }
        }
        public string MapLayerButtonLabel => IsSatelliteMap ? "Map" : "Satellite";
        public bool HasMedia => _currentMediaUris.Length > 0;
        public int SelectedPlaylistIndex
        {
            get => _selectedPlaylistIndex;
            private set
            {
                if (!Set(ref _selectedPlaylistIndex, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(SelectedPlaylistEntry));
                ((PlayerRelayCommand)PreviousVideoCommand).RaiseCanExecuteChanged();
                ((PlayerRelayCommand)NextVideoCommand).RaiseCanExecuteChanged();
            }
        }
        public PlayerPlaylistEntry? SelectedPlaylistEntry =>
            _selectedPlaylistIndex >= 0 && _selectedPlaylistIndex < _playlistEntries.Count
                ? _playlistEntries[_selectedPlaylistIndex]
                : null;

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
        public bool HasRearCamera => HasMedia && _currentMediaUris.Length > 1;
        public bool IsSideBySide
        {
            get => _isSideBySide;
            private set
            {
                if (value && !HasRearCamera)
                {
                    return;
                }

                if (Set(ref _isSideBySide, value))
                {
                    OnPropertyChanged(nameof(SideBySideButtonLabel));
                }
            }
        }
        public string SideBySideButtonLabel => IsSideBySide ? "Single view" : "Side by side";
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
            }
        }
        public MediaPlayer ActiveMediaPlayer => _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                var speed = Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, 1, 5);
                if (!Set(ref _playbackSpeed, speed))
                {
                    return;
                }

                OnPropertyChanged(nameof(PlaybackSpeedText));
                foreach (var player in _players)
                {
                    player.SetRate((float)speed);
                }
            }
        }
        public string PlaybackSpeedText => $"{PlaybackSpeed:0.0}×";
        public int Volume
        {
            get => _volume;
            set
            {
                var volume = Math.Clamp(value, 0, 100);
                if (!Set(ref _volume, volume))
                {
                    return;
                }

                foreach (var player in _players)
                {
                    player.Volume = volume;
                }
            }
        }
        public bool IsLooping { get => _isLooping; private set => Set(ref _isLooping, value); }
        private bool IsRearOnlyMedia => _currentMediaUris.Length == 1 && PlayerPlaylistGrouping.IsRear(_currentMediaUris[0]);
        public bool IsFrontSelected => HasMedia && (HasRearCamera ? SelectedCameraIndex == 0 : !IsRearOnlyMedia);
        public bool IsRearSelected => HasMedia && (HasRearCamera ? SelectedCameraIndex == 1 : IsRearOnlyMedia);
        public string SelectedCameraName
        {
            get
            {
                if (!HasMedia)
                {
                    return string.Empty;
                }

                if (_currentMediaUris.Length > 1)
                {
                    return SelectedCameraIndex == 0 ? "Front view" : "Rear view";
                }

                return IsRearOnlyMedia ? "Rear view" : "Front view";
            }
        }
        public string SelectedCameraLabel => _selectedCameraIndex == 0 ? "Front" : "Rear";
        public string MuteButtonLabel => IsMuted ? "Unmute" : "Mute";
        public string MuteButtonIconPath => IsMuted
            ? "avares://Dashcam.Remote.Shared/Assets/Icons/volume_mute.svg"
            : "avares://Dashcam.Remote.Shared/Assets/Icons/volume_up.svg";
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
                ((PlayerRelayCommand)EnterClipExportModeCommand).RaiseCanExecuteChanged();
                ((PlayerRelayCommand)SetClipStartCommand).RaiseCanExecuteChanged();
                ((PlayerRelayCommand)SetClipEndCommand).RaiseCanExecuteChanged();
                ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
            }
        }
        public string ClipStartText
        {
            get => FormatClipTime(_clipStartTimeMs);
            set => UpdateClipStart(value);
        }
        public string ClipEndText
        {
            get => FormatClipTime(_clipEndTimeMs);
            set => UpdateClipEnd(value);
        }
        public bool IsExportMode
        {
            get => _isExportMode;
            private set
            {
                if (Set(ref _isExportMode, value))
                {
                    OnPropertyChanged(nameof(IsPlaybackControlsMode));
                }
            }
        }
        public bool IsPlaybackControlsMode => !IsExportMode;
        public string ClipSelectionText => $"Start: {ClipStartText}  ·  End: {ClipEndText}";
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

                var activePlayer = ActiveMediaPlayer;
                if (IsSideBySide)
                {
                    var targetTime = (long)(position * activePlayer.Length);
                    foreach (var player in _players)
                    {
                        player.Time = Math.Clamp(targetTime, 0, Math.Max(0, player.Length));
                    }
                }
                else
                {
                    activePlayer.Position = (float)position;
                }
            }
        }
        public string PlaybackButtonLabel => IsPlaying ? "Pause" : "Play";
        public string PlaybackButtonIconPath => IsPlaying
            ? "avares://Dashcam.Remote.Shared/Assets/Icons/pause.svg"
            : "avares://Dashcam.Remote.Shared/Assets/Icons/play_arrow.svg";
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
            _currentMediaUris = mediaUris.ToArray();
            OnPropertyChanged(nameof(HasMedia));
            ((PlayerRelayCommand)TogglePlaybackCommand).RaiseCanExecuteChanged();
            ((PlayerAsyncCommand)ExportMp4Command).RaiseCanExecuteChanged();
            ((PlayerAsyncCommand)CaptureFrameCommand).RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedCameraName));
            OnPropertyChanged(nameof(IsFrontSelected));
            OnPropertyChanged(nameof(IsRearSelected));
            _clipStartTimeMs = null;
            _clipEndTimeMs = null;
            IsExportMode = false;
            OnPropertyChanged(nameof(ClipStartText));
            OnPropertyChanged(nameof(ClipEndText));
            OnPropertyChanged(nameof(ClipSelectionText));
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
            SelectedPlaylistIndex = FindPlaylistIndex(mediaUris);
            var names = mediaUris.Select(uri => Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var name = string.Join(" + ", names);
            _gpsRecordingStart = mediaUris.FirstOrDefault(uri => uri.IsFile) is { } firstUri &&
                                 DateTimeOffset.TryParseExact(
                                     Path.GetFileNameWithoutExtension(firstUri.LocalPath).AsSpan(0, Math.Min(19, Path.GetFileNameWithoutExtension(firstUri.LocalPath).Length)),
                                     "yyyy-MM-dd_HH_mm_ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var recordingStart)
                ? recordingStart
                : null;
            SelectedCameraIndex = 0;
            OnPropertyChanged(nameof(HasRearCamera));
            ((PlayerRelayCommand)ToggleSideBySideCommand).RaiseCanExecuteChanged();
            if (!HasRearCamera)
            {
                IsSideBySide = false;
            }
            ((PlayerRelayCommand)SelectCameraCommand).RaiseCanExecuteChanged();
            PlaybackSpeed = 1;
            _gpsPoints = [];
            HasGpsTrack = false;
            GpsTrackStatus = HasMedia ? "Checking recording for GPS data…" : "No video selected.";
            CurrentSpeedText = AverageSpeedText = DistanceText = LatitudeText = LongitudeText = AltitudeText = "—";
            _meanPointSpeedKmh = _speedStandardDeviationKmh = _maximumPointSpeedKmh = _minimumPointSpeedKmh = 0;
            OnPropertyChanged(nameof(GpsPoints));
            OnPropertyChanged(nameof(GpsRouteJson));
            OnPropertyChanged(nameof(SpeedDistributionSummary));
            MediaTitle = string.IsNullOrWhiteSpace(name) ? "Dashcam Player" : name;
            PlaybackStatus = HasMedia ? "Opening video…" : "Stopped";
            CurrentTimeText = "00:00";
            DurationText = "00:00";
            IsPlaying = false;
            CanSeek = false;
            _updatingPosition = true;
            Position = 0;
            _updatingPosition = false;
        }

        private int FindPlaylistIndex(IReadOnlyList<Uri> mediaUris)
        {
            if (mediaUris.Count == 0)
            {
                return -1;
            }

            for (var index = 0; index < _playlistEntries.Count; index++)
            {
                if (_playlistEntries[index].MediaUris.Any(mediaUri => mediaUris.Contains(mediaUri)))
                {
                    return index;
                }
            }

            return -1;
        }

        public void PlayPlaylistEntry(PlayerPlaylistEntry entry) => PlaylistEntryRequested?.Invoke(entry);

        private void SelectPlaylistEntry(int index)
        {
            if (index < 0 || index >= _playlistEntries.Count)
            {
                return;
            }

            PlaylistEntryRequested?.Invoke(_playlistEntries[index]);
        }

        private bool CanSelectCamera(object? value)
        {
            if (!HasMedia || value is null || !int.TryParse(value.ToString(), out var index))
            {
                return false;
            }

            if (HasRearCamera)
            {
                return index >= 0 && index < _players.Count;
            }

            return index == (IsRearOnlyMedia ? 1 : 0);
        }

        private void SetClipStart()
        {
            _clipStartTimeMs = Math.Max(0, ActiveMediaPlayer.Time);
            if (_clipEndTimeMs <= _clipStartTimeMs)
            {
                _clipEndTimeMs = null;
                OnPropertyChanged(nameof(ClipEndText));
            }

            OnPropertyChanged(nameof(ClipStartText));
            OnPropertyChanged(nameof(ClipSelectionText));
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
        }

        private void SetClipEnd()
        {
            _clipEndTimeMs = Math.Max(0, ActiveMediaPlayer.Time);
            OnPropertyChanged(nameof(ClipEndText));
            OnPropertyChanged(nameof(ClipSelectionText));
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
        }

        private bool CanExportClip() =>
            IsExportMode &&
            ExportClipRequested is not null &&
            _clipStartTimeMs is long startTimeMs &&
            _clipEndTimeMs is long endTimeMs &&
            endTimeMs - startTimeMs >= 1000;

        private async Task ExportClipAsync()
        {
            if (!CanExportClip() || _clipStartTimeMs is not long startTimeMs || _clipEndTimeMs is not long endTimeMs)
            {
                return;
            }

            if (await ExportClipRequested!(SelectedCameraIndex, startTimeMs, endTimeMs))
            {
                IsExportMode = false;
            }
        }

        private void EnterClipExportMode()
        {
            var duration = ActiveMediaPlayer.Length;
            if (!CanSeek || duration < 1000)
            {
                return;
            }

            _clipStartTimeMs = 0;
            _clipEndTimeMs = duration;
            OnPropertyChanged(nameof(ClipStartText));
            OnPropertyChanged(nameof(ClipEndText));
            OnPropertyChanged(nameof(ClipSelectionText));
            IsExportMode = true;
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
        }

        public void UpdateClipStart(string value)
        {
            if (!TryParseClipTime(value, out var time) || _clipEndTimeMs is not long end || time > end - 1000)
            {
                OnPropertyChanged(nameof(ClipStartText));
                return;
            }

            _clipStartTimeMs = time;
            OnPropertyChanged(nameof(ClipSelectionText));
            SeekToClipTime(time);
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
        }

        public void UpdateClipEnd(string value)
        {
            if (!TryParseClipTime(value, out var time) || _clipStartTimeMs is not long start || time < start + 1000 || time > ActiveMediaPlayer.Length)
            {
                OnPropertyChanged(nameof(ClipEndText));
                return;
            }

            _clipEndTimeMs = time;
            OnPropertyChanged(nameof(ClipSelectionText));
            SeekToClipTime(time);
            ((PlayerAsyncCommand)ExportClipCommand).RaiseCanExecuteChanged();
        }

        private void SeekToClipTime(long time)
        {
            ActiveMediaPlayer.Time = time;
            RefreshPlaybackState();
        }

        private static bool TryParseClipTime(string value, out long milliseconds)
        {
            if (TimeSpan.TryParseExact(value, [@"hh\:mm\:ss", @"mm\:ss"], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero)
            {
                milliseconds = (long)time.TotalMilliseconds;
                return true;
            }

            milliseconds = 0;
            return false;
        }

        private static string FormatClipTime(long? timeMs) =>
            timeMs is long value ? TimeSpan.FromMilliseconds(value).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "Not set";

        public void SetPlaybackStatus(string status)
        {
            if (_disposed)
            {
                return;
            }

            PlaybackStatus = status;
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
            if (!HasMedia || expectedCameraIndex is not null && expectedCameraIndex != SelectedCameraIndex)
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

        private async Task CaptureFrameAsync()
        {
            if (HasMedia && CaptureFrameRequested is not null)
            {
                await CaptureFrameRequested(SelectedCameraIndex);
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
            if (IsSideBySide)
            {
                if (_players.Any(item => item.IsPlaying))
                {
                    foreach (var mediaPlayer in _players)
                    {
                        mediaPlayer.SetPause(true);
                    }
                }
                else
                {
                    SideBySidePlaybackRequested?.Invoke();
                }
            }
            else if (player.IsPlaying)
            {
                player.SetPause(true);
            }
            else if (!player.Play())
            {
                PlaybackStatus = "Unable to play this file.";
            }

            RefreshPlaybackState();
        }

        public void ReplayPlayback()
        {
            if (!HasMedia)
            {
                return;
            }

            IEnumerable<MediaPlayer> players = IsSideBySide ? _players : new[] { ActiveMediaPlayer };
            foreach (var player in players)
            {
                player.Time = 0;
            }

            if (IsSideBySide)
            {
                SideBySidePlaybackRequested?.Invoke();
            }
            else
            {
                ActiveMediaPlayer.Play();
            }
        }

        private void StopPlayback()
        {
            foreach (var player in _players)
            {
                player.Stop();
            }

            PlaybackStatus = "Stopped";
            UpdateMediaTitle([]);
            PlaybackStatus = "Stopped";
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
            var time = Math.Clamp(player.Time + (long)offset.TotalMilliseconds, 0, duration);
            if (IsSideBySide)
            {
                foreach (var mediaPlayer in _players)
                {
                    mediaPlayer.Time = Math.Clamp(time, 0, Math.Max(0, mediaPlayer.Length));
                }
            }
            else
            {
                player.Time = time;
            }
            RefreshPlaybackState();
        }

        private void RefreshPlaybackState()
        {
            if (_disposed || _players.Count == 0)
            {
                return;
            }

            if (!HasMedia)
            {
                IsPlaying = false;
                CanSeek = false;
                CurrentTimeText = DurationText = "00:00";
                _updatingPosition = true;
                Position = 0;
                _updatingPosition = false;
                return;
            }

            var player = _players[Math.Clamp(SelectedCameraIndex, 0, _players.Count - 1)];
            if (IsSideBySide && _players.Count > 1)
            {
                SynchronizeSideBySidePlayback(player);
            }

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

        private void SynchronizeSideBySidePlayback(MediaPlayer referencePlayer)
        {
            var referenceTime = Math.Max(0, referencePlayer.Time);
            var referenceScale = referencePlayer.Scale;
            foreach (var player in _players)
            {
                if (ReferenceEquals(player, referencePlayer))
                {
                    continue;
                }

                var targetTime = Math.Min(referenceTime, Math.Max(0, player.Length));
                if (Math.Abs(player.Time - targetTime) > 120)
                {
                    player.Time = targetTime;
                }

                if (Math.Abs(player.Scale - referenceScale) > 0.001f)
                {
                    player.Scale = referenceScale;
                }
            }
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
