using System.Windows.Input;
using Dashcam.Remote;

namespace Dashcam.Remote.Player
{
    public sealed class PlaybackDesignViewModel
    {
        private static PlayerRelayCommand NoOpCommand() => new(_ => { });

        public string MediaTitle => "2026-11-07_08-40_F.ts";
        public string SelectedCameraName => "Front view";
        public string SideBySideButtonLabel => "Single view";
        public bool HasRearCamera => true;
        public bool IsFrontSelected => true;
        public bool IsRearSelected => false;
        public bool IsSideBySide => true;
        public bool IsPlaylistOpen => true;
        public PlaylistEntryPreview SelectedPlaylistEntry => PlaylistEntries[0];
        public IReadOnlyList<PlaylistEntryPreview> PlaylistEntries =>
        [
            new("2026-11-07_08-40.ts", "Nov 7, 2026 8:40 AM"),
            new("2026-11-07_08-37.ts", "Nov 7, 2026 8:37 AM"),
            new("2026-11-07_08-34.ts", "Nov 7, 2026 8:34 AM")
        ];
        public string PlaybackSpeedText => "1.0×";
        public double PlaybackSpeed { get; set; } = 1;
        public int Volume { get; set; } = 70;
        public bool IsLooping => false;
        public bool IsObjectDetectionEnabled => true;
        public bool IsExportMode => false;
        public bool IsPlaybackControlsMode => true;
        public string CurrentTimeText => "00:42";
        public string DurationText => "03:00";
        public double Position { get; set; } = 0.23;
        public bool CanSeek => true;
        public string PlaybackButtonLabel => "Pause";
        public string PlaybackButtonIconPath => "avares://Dashcam.Remote.Shared/Assets/Icons/pause.svg";
        public string MuteButtonLabel => "Mute";
        public string MuteButtonIconPath => "avares://Dashcam.Remote.Shared/Assets/Icons/volume_up.svg";
        public string ClipSelectionText => "Start: 00:30 · End: 02:00";
        public string ClipStartText => "00:00:30";
        public string ClipEndText => "00:02:00";
        public string GpsTrackStatus => "180 GPS points · embedded in TS";
        public string CurrentSpeedText => "55.0 km/h";
        public string AverageSpeedText => "42.3 km/h";
        public string MaximumSpeedText => "80.0 km/h";
        public string MinimumSpeedText => "0.0 km/h";
        public string DistanceText => "2.10 km";
        public string LatitudeText => "-0.065810";
        public string LongitudeText => "-78.457530";
        public string AltitudeText => "2526.0 m";
        public double MeanPointSpeedKmh => 45.4;
        public double SpeedStandardDeviationKmh => 22.3;
        public string SpeedDistributionSummary => "μ 45.4 km/h · σ 22.3 km/h";
        public IReadOnlyList<GpsPoint> GpsPoints { get; } =
        [
            new(new DateTimeOffset(2026, 11, 7, 8, 40, 0, TimeSpan.Zero), -0.06620, -78.46310, 2520, 11),
            new(new DateTimeOffset(2026, 11, 7, 8, 40, 1, TimeSpan.Zero), -0.06600, -78.46220, 2522, 13),
            new(new DateTimeOffset(2026, 11, 7, 8, 40, 2, TimeSpan.Zero), -0.06581, -78.46143, 2526, 15),
            new(new DateTimeOffset(2026, 11, 7, 8, 40, 3, TimeSpan.Zero), -0.06555, -78.46050, 2528, 12)
        ];
        public GpsPoint? GpsCurrentPoint => GpsPoints[^1];

        public ICommand TogglePlaylistCommand { get; } = NoOpCommand();
        public ICommand ToggleSideBySideCommand { get; } = NoOpCommand();
        public ICommand ToggleFullscreenCommand { get; } = NoOpCommand();
        public ICommand SelectCameraCommand { get; } = NoOpCommand();
        public ICommand PreviousVideoCommand { get; } = NoOpCommand();
        public ICommand NextVideoCommand { get; } = NoOpCommand();
        public ICommand SeekBackwardCommand { get; } = NoOpCommand();
        public ICommand SeekForwardCommand { get; } = NoOpCommand();
        public ICommand TogglePlaybackCommand { get; } = NoOpCommand();
        public ICommand StopPlaybackCommand { get; } = NoOpCommand();
        public ICommand ToggleMuteCommand { get; } = NoOpCommand();
        public ICommand ToggleLoopCommand { get; } = NoOpCommand();
        public ICommand ToggleObjectDetectionCommand { get; } = NoOpCommand();
        public ICommand CaptureFrameCommand { get; } = NoOpCommand();
        public ICommand EnterClipExportModeCommand { get; } = NoOpCommand();
        public ICommand CancelClipExportModeCommand { get; } = NoOpCommand();
        public ICommand ExportMp4Command { get; } = NoOpCommand();
        public ICommand SetClipStartCommand { get; } = NoOpCommand();
        public ICommand SetClipEndCommand { get; } = NoOpCommand();
        public ICommand ExportClipCommand { get; } = NoOpCommand();
        public ICommand ExportGpsCommand { get; } = NoOpCommand();
        public ICommand ExportRoutePngCommand { get; } = NoOpCommand();
        public ICommand ExportSpeedCsvCommand { get; } = NoOpCommand();
        public ICommand ExportSpeedChartPngCommand { get; } = NoOpCommand();
    }

    public sealed record PlaylistEntryPreview(string DisplayName, string CreatedAtText)
    {
        public string SummaryText => $"{CreatedAtText} · 03:00 · 48.0 MB · Front + Rear";
    }

    public sealed class FolderSelectionDesignViewModel
    {
        private static PlayerRelayCommand NoOpCommand() => new(_ => { });

        public string SelectedFolderPath => @"C:\Dashcam\Recordings";
        public ICommand ChooseFolderCommand { get; } = NoOpCommand();
        public ICommand OpenFolderCommand { get; } = NoOpCommand();
        public ICommand CancelCommand { get; } = NoOpCommand();
    }
}
